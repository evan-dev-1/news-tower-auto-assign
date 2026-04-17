using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using _Game._Common;
using Assigner;
using Employees;
using GlobalNews;
using Persons;
using Reportables;
using Reportables.News;

namespace NewsTowerAutoAssign
{
    // Auto-assigns idle staff to ads on the Ads tab.
    //
    // Ads are NOT news items, but mechanically they share the same
    // NewsItemStoryFile / IAssignable<...> / AssignTo machinery. The Ads tab
    // (UI.AdsMenu) lists every Ad with ReportableFlags.IsOnGlobe; that's the
    // same set we iterate from LiveReportableManager.OnAdBoard.
    //
    // We do not implement risk / weekend / dead-end discard logic for ads:
    //   - Ads don't carry the same risk components reporter stories do.
    //   - Ads can expire on their own (ReportableExpireData) - the game handles
    //     removing them from the board. We simply re-scan every tick.
    //   - Boycotted ads (Ad.HasBoycott) are skipped because the ad's own
    //     setter unassigns from the newspaper slot when boycott flips on.
    //
    // The reporter-count gate (MinReportersToActivate) is intentionally NOT
    // applied here. Ads are worked by salespeople / copy editors / typesetters
    // / assemblers - the count of *reporters* is irrelevant. The user's
    // AutoAssignAds toggle is the only kill switch.
    internal static class AdAutomation
    {
        // Reentrancy guard. AssignTo can re-enter our scan via Harmony patches
        // on downstream events; without this we'd start a second pass before
        // the first finished and double-assign. Mirrors AssignmentEvaluator.
        private static bool _isAssigning;

        // Same reflection target the evaluator uses to detect a slot that
        // already has a job running but hasn't been marked completed yet
        // (the "ghost assignment" symptom). Probed at startup by the plugin.
        private static readonly FieldInfo _progressDoneEventField =
            typeof(NewsItemStoryFile).GetField(
                "progressDoneEvent",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

        internal static bool ProgressDoneEventFieldAvailable => _progressDoneEventField != null;

        internal static void TryAssignAds()
        {
            if (_isAssigning)
                return;
            if (!AutoAssignPlugin.AutoAssignAds.Value)
                return;
            if (LiveReportableManager.Instance == null)
                return;
            if (!SafetyGate.IsOpen)
                return;

            _isAssigning = true;
            try
            {
                foreach (var ad in LiveReportableManager.Instance.OnAdBoard.ToList())
                    TryAssignAdInternal(ad);
            }
            catch (Exception e)
            {
                AssignmentLog.Error("AdAutomation.TryAssignAds: " + e);
            }
            finally
            {
                _isAssigning = false;
            }
        }

        // Called from Patch_AddReportable when a new Ad is added so we react
        // immediately rather than waiting for the next periodic scan tick.
        internal static void TryAssignAd(Ad ad)
        {
            if (_isAssigning)
                return;
            if (!AutoAssignPlugin.AutoAssignAds.Value)
                return;
            if (ad == null || ad.Data == null)
                return;
            if (!SafetyGate.IsOpen)
                return;
            // OnCreatedLive sets IsOnGlobe via EvaluateFlags; if it hasn't
            // landed yet, defer to the next periodic scan rather than try to
            // assign an ad that isn't actually on the board.
            if ((ad.Flags & ReportableFlags.IsOnGlobe) == ReportableFlags.None)
                return;

            _isAssigning = true;
            try
            {
                TryAssignAdInternal(ad);
            }
            catch (Exception e)
            {
                AssignmentLog.Error("AdAutomation.TryAssignAd: " + e);
            }
            finally
            {
                _isAssigning = false;
            }
        }

        private static void TryAssignAdInternal(Ad ad)
        {
            if (ad == null || ad.Data == null)
                return;
            // A boycotted ad cannot earn money and the game forcibly unassigns
            // it from the newspaper slot. Don't waste an employee starting
            // work that will immediately be undone.
            if (ad.HasBoycott)
                return;

            // Ad story files are exposed the same way news items expose theirs.
            // Use the same "unlocked + assignable" filter the evaluator uses
            // for news, then run each open slot through the assignment loop.
            var storyFiles = new List<NewsItemStoryFile>();
            ad.GetUnlockedAndAssignableStoryFiles(storyFiles);
            if (storyFiles.Count == 0)
                return;

            foreach (var storyFile in storyFiles)
                TryAssignAdStoryFile(ad, storyFile);
        }

        private static void TryAssignAdStoryFile(Ad ad, NewsItemStoryFile storyFile)
        {
            if (storyFile == null)
                return;
            if (storyFile.IsCompleted)
                return;
            if (
                _progressDoneEventField != null
                && _progressDoneEventField.GetValue(storyFile) != null
            )
                return;

            var skill = storyFile.AssignSkill;
            if (skill != null)
            {
                // Mirror the evaluator's preflight: don't try to assign work
                // whose required building isn't built yet, and don't waste a
                // scan trying to find a skill nobody on the roster has. These
                // also keep the log clean of "no eligible employee" spam for
                // ads the player physically can't service yet.
                //
                // Important difference from the news path: ads are worked by
                // salespeople / editors / typesetters / assemblers, not
                // reporters. ReporterLookup.AnyReporterEverHasSkill filters
                // on JobData.name == "Reporter" and would silently reject
                // every ad skill. Use the job-agnostic variant instead.
                if (!AssetUnlocker.IsUnlockedSafe(skill))
                {
                    AssignmentLog.DecisionOnce(
                        ad,
                        "ad_building_missing_" + skill.skillName,
                        "Ad '"
                            + AdName(ad)
                            + "' → WAIT (ad): required building for '"
                            + skill.skillName
                            + "' not built yet (ad kept, will retry when unlocked)."
                    );
                    return;
                }
                if (!ReporterLookup.AnyEmployeeEverHasSkill(skill))
                {
                    AssignmentLog.DecisionOnce(
                        ad,
                        "ad_no_skill_" + skill.skillName,
                        "Ad '"
                            + AdName(ad)
                            + "' → WAIT (ad): no employee on the roster has '"
                            + skill.skillName
                            + "' trained (ad kept, will retry after hiring)."
                    );
                    return;
                }
            }

            // Same employee filter the evaluator uses for news. Every
            // dereference is null-guarded so a partially-destroyed or
            // mid-hire Employee can't NRE in the LINQ predicate and
            // abort the whole ad scan.
            var employee = Employee
                .Employees.Where(e =>
                    e != null
                    && e.IsAvailableForGlobeAssignment
                    && e.AssignableToReportable != null
                    && e.AssignableToReportable.Assignment == null
                    && e.SkillHandler != null
                    && (skill == null || e.SkillHandler.HasSkillAndIsAssigned(skill))
                    && e.JobHandler?.JobData?.hideFromDrawer == false
                )
                .OrderByDescending(e => ReporterLookup.GetSkillLevel(e, skill))
                .FirstOrDefault();

            if (employee == null)
            {
                AssignmentLog.DecisionOnce(
                    ad,
                    "ad_no_employee_" + (skill?.skillName ?? "any"),
                    "Ad '"
                        + AdName(ad)
                        + "' → WAIT (no employee): all "
                        + (skill != null ? "'" + skill.skillName + "'" : "eligible")
                        + " staff busy right now (ad kept, will retry)."
                );
                return;
            }

            // Same visibility flip the evaluator does for news. Without this
            // OnAssigned's CanAssign check fails silently and the assignment
            // ghosts (employee marked busy, no progressDoneEvent created).
            storyFile.OnVisibilityChanged(true);

            bool canAssign = storyFile.CanAssignHandlers.All(h => h.CanAssign(employee));
            if (!canAssign)
            {
                if (storyFile.Node?.NodeState == NewsItemNodeState.Locked)
                {
                    AssignmentLog.Verbose(
                        "AD",
                        "Ad branch locked (sibling chosen) ["
                            + (skill?.skillName ?? "any")
                            + "] for "
                            + AdName(ad)
                            + "."
                    );
                }
                else
                {
                    AssignmentLog.Warn(
                        "AD",
                        "  -> AD PRE-FLIGHT FAIL for "
                            + employee.name
                            + " ["
                            + (skill?.skillName ?? "any")
                            + "]"
                            + " | NodeState="
                            + storyFile.Node?.NodeState
                            + " IsCompleted="
                            + storyFile.IsCompleted
                            + " HasSkill="
                            + (skill == null || employee.SkillHandler.HasSkillAndIsAssigned(skill))
                            + " AvailableForGlobe="
                            + employee.IsAvailableForGlobeAssignment
                    );
                }
                return;
            }

            AssignmentLog.ClearSuppression(ad);
            AssignmentLog.Decision(
                "Ad '"
                    + AdName(ad)
                    + "' → ASSIGNED: path="
                    + (skill?.skillName ?? "any")
                    + " employee="
                    + employee.name
                    + "."
            );
            employee.AssignableToReportable.AssignTo(storyFile);
        }

        // Best-effort display name. Ad.Title can format mutable content that
        // isn't yet resolved during early load; fall back to the AdData asset
        // name so logs stay readable even when titles are blank.
        private static string AdName(Ad ad)
        {
            if (ad == null)
                return "?";
            try
            {
                var title = ad.Title;
                if (!string.IsNullOrEmpty(title))
                    return title;
            }
            catch
            {
                // Title can throw on partially-initialised ads (e.g. mid-load
                // before company / mutable refs are resolved). Falling back is
                // fine for log purposes.
            }
            return ad.Data != null ? ad.Data.name : "?";
        }
    }
}
