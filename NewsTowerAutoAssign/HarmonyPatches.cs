using System.Linq;
using GlobalNews;
using HarmonyLib;
using Persons;
using Reportables;
using Risks;
using Risks.UI;
using Tower_Stats;
using UI;

namespace NewsTowerAutoAssign
{
    // Fires when any new reportable (news item) is added to the live manager.
    // Bribes pay first (respect AutoResolveBribeMinigame toggle) so the evaluator
    // sees any newly-unlocked nodes.
    [HarmonyPatch(typeof(LiveReportableManager), "AddReportable")]
    static class Patch_AddReportable
    {
        static void Postfix(Reportable reportable)
        {
            AssignmentLog.Verbose("PATCH", "AddReportable: " + reportable?.GetType().Name);
            var newsItem = reportable as NewsItem;
            if (newsItem?.Data != null)
            {
                // Dump the story's PlayerStatDataTags so faction / composed-quest
                // injections (e.g. Red Herring on "SUSPICIOUS SHIPMENT STOPPED")
                // are visible the moment they arrive - without this log the
                // player can't tell whether the injection actually carried the
                // expected tag until assignment already fired.
                if (AutoAssignPlugin.VerboseLogs.Value)
                {
                    var tagNames = newsItem
                        .Data.DistinctStatTypes.OfType<PlayerStatDataTag>()
                        .Select(t => t.name)
                        .ToList();
                    AssignmentLog.Verbose(
                        "TAGS",
                        AssignmentLog.StoryName(newsItem)
                            + " → ["
                            + (tagNames.Count > 0 ? string.Join(", ", tagNames) : "none")
                            + "]"
                    );
                }
                BribeAutomation.TryPayBribes(newsItem);
                AssignmentEvaluator.TryAssignNewsItem(newsItem);
            }
        }
    }

    // Fires when a reporter physically returns to the tower and the state machine
    // transitions them to their desk-idle state - AFTER the completed story has been
    // deposited (InteractingState.DoState drops item slot contents at the reporter's
    // current position at the start of the state, which at this point is inside the
    // building rather than in the street). Using OnUnassigned instead fired too early —
    // the reporter was still physically in transit, causing the item dump to land on
    // the street and triggering reassignment before delivery was complete.
    [HarmonyPatch(typeof(IdleWorkplaceState), "DoState")]
    static class Patch_IdleWorkplaceDoState
    {
        private static float _lastScanTime = 0f;

        static void Prefix()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastScanTime < 1f)
                return;
            _lastScanTime = now;
            AssignmentLog.Verbose("PATCH", "IdleWorkplaceState.DoState - rescanning");
            AssignmentEvaluator.TryAutoAssignAll();
            // Run the full test suite exactly once, and only when the game is
            // in a state where every test CAN pass (QuestManager populated, at
            // least one non-dummy ComposedQuest active, LiveReportableManager
            // instantiated). Running earlier just produced SKIP noise.
            InGameTests.InGameTestRunner.RunOnceWhenReady();
        }
    }

    // Fires when a save game is loaded - pay any outstanding bribes and scan all existing news items.
    // Tests used to run here too, but composed quests aren't wired up yet on
    // OnAfterLoadStart, so we defer the entire suite to the first idle rescan
    // that finds live quest data. See InGameTestRunner.RunOnceWhenReady.
    [HarmonyPatch(typeof(LiveReportableManager), "OnAfterLoadStart")]
    static class Patch_AfterLoad
    {
        static void Postfix()
        {
            AssignmentLog.Verbose("PATCH", "OnAfterLoadStart - scanning existing news");
            if (LiveReportableManager.Instance != null)
                foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems().ToList())
                    if (newsItem?.Data != null)
                        BribeAutomation.TryPayBribes(newsItem);
            AssignmentEvaluator.TryAutoAssignAll();
        }
    }

    // Auto-dismisses the new-story suitcase popup every frame so the player never has to click.
    // SuitcasePopup.Flush() locks the game (GameLockFlags.FullButAllowPause) and runs a 1s
    // open animation + 3s wait per reward card. Setting didSkip=true causes both loops to
    // exit immediately, completing the popup within one frame.
    [HarmonyPatch(typeof(SuitcasePopup), "Update")]
    static class Patch_SuitcasePopupAutoSkip
    {
        static void Postfix(SuitcasePopup __instance)
        {
            if (AutoAssignPlugin.AutoSkipSuitcasePopups.Value && __instance.IsBusy)
                Traverse.Create(__instance).Field("didSkip").SetValue(true);
        }
    }

    // Auto-dismisses risk spinner popups every frame so the player never has to click.
    // The risk outcome (None / Medium / Severe) is already decided and applied by the game
    // before the popup opens - the spinner is purely cosmetic. Setting shouldSkip=true
    // mirrors what RiskPopup.Update() does on any key/click, skipping the 4-5 second
    // animation and completing the coroutine within a single frame.
    [HarmonyPatch(typeof(RiskPopup), "Update")]
    static class Patch_RiskPopupAutoSkip
    {
        static void Postfix(RiskPopup __instance)
        {
            if (AutoAssignPlugin.AutoSkipRiskPopups.Value)
                Traverse.Create(__instance).Field("shouldSkip").SetValue(true);
        }
    }

    // Logs the resolved outcome of each risk popup exactly once. RiskPopup.Play is
    // the single public entry point every concrete popup goes through (called by
    // RiskPopupPlayer.Flush when the queue dequeues an arg bundle), and
    // RiskPopupArgs carries the already-decided severity + risk type + employee
    // context. RiskPopup itself does not know which NewsItem produced the risk,
    // so we log the employee rather than a story name - this matches the game's
    // own separation of concerns (severity is decided in NewsItemRisk.DecideSeverity
    // and applied before the popup opens; the popup is purely cosmetic).
    [HarmonyPatch(typeof(RiskPopup), "Play")]
    static class Patch_RiskPopupPlayLog
    {
        static void Prefix(RiskPopupArgs args)
        {
            var riskName = args.riskType != null ? args.riskType.name : "?";
            var employeeName = args.context != null ? args.context.name : "unassigned";
            AssignmentLog.Decision(
                "Risk resolution ("
                    + riskName
                    + ") for "
                    + employeeName
                    + ": severity="
                    + args.severity
                    + (args.isLucky ? " (lucky)" : "")
                    + "."
            );
        }
    }
}
