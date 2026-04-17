using System;
using System.Collections.Generic;
using System.Linq;
using _Game.Quests;
using _Game.Quests.Composed;
using _Game.Quests.Composed.Components;
using _Game.Quests.Composed.Data.Components;
using AreaMaps;
using Employees;
using GameState;
using GlobalNews;
using Persons;
using Reportables;
using Skills;
using Tower_Stats;

namespace NewsTowerAutoAssign
{
    // Queries about reporters and active goals. Anything that
    // reads `Employee.Employees` or `QuestManager` lives here so the assignment
    // evaluator stays focused on decision flow.
    internal static class ReporterLookup
    {
        // The ScriptableObject asset name of the "Reporter" JobData. Production
        // and Utility staff also satisfy Employee.IsGlobetrotter (they have a
        // reportable component and a workplace) - IsGlobetrotter alone would
        // over-count a 2-reporter / 10-support roster as 12 "globetrotters",
        // which is exactly what the [ROSTER] diagnostic revealed. Filtering on
        // this asset name is the same signal the game's own scoring UI uses
        // (EmployeeDisplayer.reporterJobData) to tell reporter counts apart
        // from production / utility counts.
        private const string ReporterJobName = "Reporter";

        // Single source of truth for "this is a live, playable reporter we can
        // reason about". All three reporter-scanning helpers below MUST use
        // this - anything less restrictive pulls in non-reporter employees.
        private static bool IsPlayableReporter(Employee e)
        {
            if (e == null)
                return false;
            if (!e.IsGlobetrotter)
                return false;
            if (!e.IsInTower)
                return false;
            var job = e.JobHandler?.JobData;
            if (job == null)
                return false;
            if (job.hideFromDrawer)
                return false;
            if (job.name != ReporterJobName)
                return false;
            return true;
        }

        // Fewer than `MinReportersToActivate` playable reporters means we
        // cannot make reliable judgements. We intentionally require a
        // Reporter JobData match here - Production / Utility employees also
        // return true for Employee.IsGlobetrotter (they have reportable
        // components + workplaces) but they don't cover stories, so letting
        // them satisfy the gate would silently bypass the tutorial-phase
        // safety and auto-assign with only 2 real reporters.
        internal static int CountPlayableReporters()
        {
            var matched = Employee.Employees.Where(IsPlayableReporter).ToList();

            LogRosterIfChanged(matched);
            return matched.Count;
        }

        // Identity-based fingerprint of the last counted roster, used so the
        // [ROSTER] diagnostic only fires when the set changes (not on every
        // per-story check). Debug only - the caller never reads this.
        private static string _lastRosterFingerprint;

        // DEBUG-only: dumps every employee CountPlayableReporters treats as
        // a playable reporter, plus the full Employee.Employees count, so we
        // can see exactly which objects cause the gate to overcount. Fires
        // only when the matched set actually changes.
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogRosterIfChanged(List<Employee> matched)
        {
            var fingerprint = string.Join(
                ",",
                matched
                    .Select(e => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e))
                    .OrderBy(id => id)
            );
            if (fingerprint == _lastRosterFingerprint)
                return;
            _lastRosterFingerprint = fingerprint;

            int totalEmployees = Employee.Employees.Count;
            int totalAlive = Employee.Employees.Count(e => e != null);

            AssignmentLog.Info(
                "ROSTER",
                "counted="
                    + matched.Count
                    + " / Employees.Count="
                    + totalEmployees
                    + " (alive="
                    + totalAlive
                    + ")"
            );

            for (int i = 0; i < matched.Count; i++)
            {
                var e = matched[i];
                var personName = e.GetComponentInChildren<NameHandler>()?.EmployeeName;
                var jobName = e.JobHandler?.JobData?.name ?? "<no-job>";
                AssignmentLog.Info(
                    "ROSTER",
                    "  #"
                        + (i + 1)
                        + " obj='"
                        + (e.name ?? "<null>")
                        + "' person='"
                        + (string.IsNullOrEmpty(personName) ? "<no-name>" : personName)
                        + "' job='"
                        + jobName
                        + "' IsGlobetrotter="
                        + e.IsGlobetrotter
                        + " IsInTower="
                        + e.IsInTower
                        + " hideFromDrawer="
                        + (e.JobHandler?.JobData?.hideFromDrawer)
                );
            }

            // Excluded employees are summarised by job (Production x7, Utility
            // x3, etc.) rather than dumped line-by-line - a mature tower can
            // easily have a dozen support staff and we don't want the full
            // roster printed on every matched-set change. The per-employee
            // dump above is the important bit for confirming the filter.
            var excludedByJob = new Dictionary<string, int>();
            foreach (var e in Employee.Employees)
            {
                if (e == null)
                    continue;
                if (matched.Contains(e))
                    continue;
                var jobName = e.JobHandler?.JobData?.name ?? "<no-job>";
                excludedByJob.TryGetValue(jobName, out int n);
                excludedByJob[jobName] = n + 1;
            }
            if (excludedByJob.Count == 0)
            {
                AssignmentLog.Info("ROSTER", "  (no excluded employees)");
            }
            else
            {
                AssignmentLog.Info(
                    "ROSTER",
                    "  excluded by job: "
                        + string.Join(
                            ", ",
                            excludedByJob
                                .OrderByDescending(kv => kv.Value)
                                .Select(kv => kv.Key + " x" + kv.Value)
                        )
                );
            }
        }

        // Returns true if ANY playable reporter has the given skill trained,
        // regardless of whether they are currently busy/timed-out. skill==null
        // always returns true. Used to detect permanently unworkable paths
        // (building not built or roster simply lacks the skill) - not just
        // "everyone is temporarily busy".
        internal static bool AnyReporterEverHasSkill(SkillData skill)
        {
            if (skill == null)
                return true;
            foreach (var e in Employee.Employees)
            {
                if (!IsPlayableReporter(e))
                    continue;
                // SkillHandler is conceptually a MonoBehaviour child of the
                // employee and always present in a healthy game state, but
                // mid-destruction it can be null. Skip rather than NRE.
                if (e.SkillHandler == null)
                    continue;
                if (e.SkillHandler.HasSkillAndIsAssigned(skill))
                    return true;
            }
            return false;
        }

        // Returns true if any playable reporter with `skill` is free now or
        // will be free within `thresholdHours`. skill==null means any reporter
        // qualifies. thresholdHours <= 0 disables the feature and always
        // returns true. Converts via FromMinutes so fractional hours (e.g.
        // 3.5h) are honoured - FromHours only accepts int and would silently
        // round down.
        internal static bool AnyReporterAvailableSoon(SkillData skill, float thresholdHours)
        {
            if (thresholdHours <= 0f)
                return true;

            int minutes = (int)Math.Round(thresholdHours * 60f);
            var deadline = TowerTime.CurrentTime + TowerTimeDuration.FromMinutes(minutes);

            foreach (var e in Employee.Employees)
            {
                if (!IsPlayableReporter(e))
                    continue;
                // Same null-safety rationale as AnyReporterEverHasSkill:
                // these handlers are MonoBehaviour children and should always
                // exist, but guarding the deref means a mid-destruction employee
                // is silently skipped rather than NRE-ing the whole scan.
                if (e.SkillHandler == null || e.TimeoutHandler == null)
                    continue;
                if (skill != null && !e.SkillHandler.HasSkillAndIsAssigned(skill))
                    continue;
                if (!e.TimeoutHandler.IsTimedOut)
                    return true;
                if (e.TimeoutHandler.GetReleaseTime() <= deadline)
                    return true;
            }
            return false;
        }

        // Returns the highest trained level of `skill` for `employee`, or 0 if the
        // skill is null or untrained. Used to rank candidates so we prefer the
        // strongest reporter for a slot.
        internal static int GetSkillLevel(Employee employee, SkillData skill)
        {
            if (skill == null)
                return 0;
            // Null-safe against a destroyed or mid-hire employee - the caller
            // is a LINQ ordering in TryAssignSingleSlot where one NRE would
            // abort the OrderBy for the whole roster.
            if (employee?.SkillHandler == null)
                return 0;
            Skill s;
            return employee.SkillHandler.TryGetSkill(skill, out s) ? (int)s : 0;
        }

        // Quest sources scanned:
        //   1. QuestManager.Instance.AllRunningQuests - both District (row 0) and
        //      HiddenAgenda (row 1 - faction quests) once the weekly recap has
        //      promoted them from pending.
        //   2. AreaMapQuestHub.Quest on every registered hub - defensive: catches
        //      faction quests that are currently visible to the player but not yet
        //      (or no longer) in the QuestManager's running array (mid-week edge
        //      cases, save-load races).
        //
        // Extraction strategy (per ComposedQuest):
        //   a) Walk the RUNTIME ComposableRuntimeComponent tree via
        //      GetComponentsInChildren<T> - this is what the game itself uses in
        //      ComposedQuest.Eval/OnStart to find requirements. It works for both
        //      authored-static and mutable (CSV-driven) quests because mutable
        //      quests populate their runtime tree from SaveComponentData even
        //      when the ScriptableObject children list is empty.
        //   b) Fall back to the data tree (QuestTargetSetRequirementData etc.)
        //      only if the runtime tree yields nothing - belt-and-braces for
        //      quests that haven't fully initialised yet.
        // Returns three goal-tag sets. Correct per-tag classification (verified
        // against the game source - see HotTopicsQuest.IsTagCompleted,
        // QuestCollectingReward.CalculateInto, QuestCollectingCombo.CalculateInto):
        //
        //   quantity = tags whose reward SCALES per published copy - i.e. more
        //              stories carrying the tag means strictly more reward, so
        //              in-progress coverage never marks them "done". Only
        //              QuestCollectingReward and QuestCollectingCombo
        //              components produce these.
        //
        //   binary   = tags satisfied by a THRESHOLD - once the target count
        //              is reached, more copies do nothing. This is everything
        //              else:
        //                * HotTopicsQuest weekly district goals (absoluteTarget
        //                  bounded; typically target = 1)
        //                * QuestTargetSetRequirement ("print N items tagged X")
        //                * QuestTargetComboRequirement (combo thresholds)
        //                * QuestTopTagRequirement (top-of-page, one-shot)
        //                * QuestAboveTheFoldTagRequirement (above fold, one-shot)
        //              Binary tags lose the goal shield once an in-progress
        //              story already covers them; the mod stops chasing
        //              duplicates.
        //
        //   scoop    = subset of `binary` where the underlying HotTopicsQuest
        //              TargetSet is Scoop (competitive district mode). A
        //              story-file path earns the highest priority only if the
        //              tag is in this set AND still uncovered AND the path
        //              IsScoop.
        //
        // A tag contributed by BOTH a collecting-reward/combo AND a threshold
        // requirement belongs in `quantity` - the scaling reward keeps "more"
        // valuable regardless of threshold state, so we never stop chasing.
        internal static (
            HashSet<PlayerStatDataTag> quantity,
            HashSet<PlayerStatDataTag> scoopQuantity,
            HashSet<PlayerStatDataTag> binary
        ) GetCurrentGoalTagSets()
        {
            var quantity = new HashSet<PlayerStatDataTag>();
            var scoopQuantity = new HashSet<PlayerStatDataTag>();
            var binary = new HashSet<PlayerStatDataTag>();

            foreach (var quest in EnumerateAllActiveQuests())
            {
                if (quest == null)
                    continue;

                ExtractQuestTags(quest, quantity, scoopQuantity, binary);
            }

            // If a tag ended up in BOTH sets because the same composed quest
            // has a scaling-reward component AND a threshold requirement on
            // the same tag, quantity wins (stacking reward keeps it valuable).
            binary.ExceptWith(quantity);

            AssignmentLog.Verbose(
                "GOALS",
                "Goal tags - quantity: ["
                    + string.Join(", ", quantity)
                    + "]  scoopRequired: ["
                    + string.Join(", ", scoopQuantity)
                    + "]  binary: ["
                    + string.Join(", ", binary)
                    + "]"
            );
            return (quantity, scoopQuantity, binary);
        }

        // All live quests that could contribute goal tags, from every source we
        // know about. Union (by reference) across sources is handled by the
        // caller's HashSets - the tag sets dedupe naturally.
        private static IEnumerable<Quest> EnumerateAllActiveQuests()
        {
            // 1. QuestManager.runningQuests - primary source (both rows).
            if (QuestManager.Instance != null)
            {
                foreach (var quest in QuestManager.Instance.AllRunningQuests)
                {
                    if (quest != null)
                        yield return quest;
                }
            }

            // 2. AreaMapQuestHub.Quest on every hub - faction quests (mafia,
            //    mayor, police...) live here. Some mid-week states have the hub
            //    holding the live quest even when QuestManager's HiddenAgenda
            //    slot is transitioning.
            var areaMapRoot = AreaMapRoot.Instance;
            if (areaMapRoot != null)
            {
                foreach (var hub in areaMapRoot.GetComponentsInChildren<AreaMapQuestHub>(true))
                {
                    if (hub?.Quest != null)
                        yield return hub.Quest;
                }
            }
        }

        // Pull tags from a single quest into the three sets.
        private static void ExtractQuestTags(
            Quest quest,
            HashSet<PlayerStatDataTag> quantity,
            HashSet<PlayerStatDataTag> scoopQuantity,
            HashSet<PlayerStatDataTag> binary
        )
        {
            var added = new HashSet<PlayerStatDataTag>();

            // HotTopicsQuest weekly goals are threshold-bounded
            // (absoluteTarget, typically 1). Each tag is BINARY - satisfied
            // when StashSet + in-progress hits the target. Scoop flag here is
            // a flag on the SAME binary tag (competitive district), not a
            // separate category.
            if (quest is HotTopicsQuest hotTopics && hotTopics.TargetSet != null)
            {
                foreach (var tag in hotTopics.TargetSet.DistinctTagTypes)
                {
                    if (tag == null)
                        continue;
                    binary.Add(tag);
                    if (hotTopics.TargetSet.Scoop)
                        scoopQuantity.Add(tag);
                    added.Add(tag);
                }
            }

            // ComposedQuest (district story + faction/NPC hidden-agenda).
            // Scaling-reward components (QuestCollectingReward, QuestCollectingCombo)
            // contribute QUANTITY tags; every other requirement type is binary.
            if (quest is ComposedQuest composed)
            {
                ExtractComposedQuestTags(composed, quantity, binary, added);

                // If a non-dummy quest contributed zero tags, dump its
                // structure so we can see what component types the game
                // actually built for it - extractor coverage gap or a
                // runtime/data tree problem.
                if (added.Count == 0 && !composed.IsDummy)
                    DumpComposedQuestStructure(composed, "no tags extracted");
            }

            AssignmentLog.Verbose(
                "GOALS",
                "Scanned "
                    + quest.GetType().Name
                    + " ("
                    + QuestIdentityLabel(quest)
                    + ") → tags: ["
                    + string.Join(", ", added.Select(t => t?.name ?? "null"))
                    + "]"
            );
        }

        // Extract goal tags from a ComposedQuest, splitting by how the
        // underlying component rewards:
        //
        //   * QuestCollectingReward / QuestCollectingCombo tags → QUANTITY.
        //     These components' Contributor.CalculateInto multiplies the
        //     reward by the count of tagged articles / combo points - strictly
        //     more-is-better, so we never want to stop chasing the tag.
        //
        //   * QuestTargetSetRequirement / QuestTargetComboRequirement /
        //     QuestTopTagRequirement / QuestAboveTheFoldTagRequirement →
        //     BINARY. These are threshold-satisfied: once the count or
        //     placement is reached, extra tagged articles add no reward.
        //
        // `addedOut` collects every tag added to either bucket so the caller
        // can log per-quest provenance. Order matters: we populate quantity
        // first so the binary branches can skip tags already claimed as
        // quantity (de-dup is also enforced by the final ExceptWith in
        // GetCurrentGoalTagSets as a safety net).
        //
        // Data-tree fallback runs only when the runtime tree turns up empty,
        // which happens for quests that haven't fully initialised yet.
        internal static void ExtractComposedQuestTags(
            ComposedQuest composed,
            HashSet<PlayerStatDataTag> quantity,
            HashSet<PlayerStatDataTag> binary,
            HashSet<PlayerStatDataTag> addedOut
        )
        {
            int runtimeHits = 0;

            foreach (var req in composed.GetComponentsInChildren<QuestCollectingReward>(true))
            {
                runtimeHits++;
                AddTag(req.RequirementData?.tagToCollect, quantity, addedOut);
            }

            foreach (var req in composed.GetComponentsInChildren<QuestCollectingCombo>(true))
            {
                runtimeHits++;
                AddTag(req.RequirementData?.tagToCollect, quantity, addedOut);
            }

            foreach (var req in composed.GetComponentsInChildren<QuestTargetSetRequirement>(true))
            {
                runtimeHits++;
                var liveSet = req.TargetSet;
                if (liveSet != null && !liveSet.IsEmpty())
                {
                    foreach (var tag in liveSet.DistinctTagTypes)
                        AddBinaryTag(tag, quantity, binary, addedOut);
                    continue;
                }
                var protos = req.RequirementData?.targets?.targets;
                if (protos == null)
                    continue;
                foreach (var pt in protos)
                    AddBinaryTag(pt?.tag, quantity, binary, addedOut);
            }

            foreach (var req in composed.GetComponentsInChildren<QuestTargetComboRequirement>(true))
            {
                runtimeHits++;
                AddBinaryTag(req.Tag ?? req.RequirementData?.tag, quantity, binary, addedOut);
            }

            foreach (var req in composed.GetComponentsInChildren<QuestTopTagRequirement>(true))
            {
                runtimeHits++;
                AddBinaryTag(req.RequirementData?.tag, quantity, binary, addedOut);
            }

            foreach (
                var req in composed.GetComponentsInChildren<QuestAboveTheFoldTagRequirement>(true)
            )
            {
                runtimeHits++;
                AddBinaryTag(req.RequirementData?.tag, quantity, binary, addedOut);
            }

            if (runtimeHits == 0 && composed.Data != null)
            {
                foreach (var d in composed.Data.GetAllChildren<QuestCollectingRewardData>(true))
                    AddTag(d.tagToCollect, quantity, addedOut);
                foreach (var d in composed.Data.GetAllChildren<QuestCollectingComboData>(true))
                    AddTag(d.tagToCollect, quantity, addedOut);
                foreach (var d in composed.Data.GetAllChildren<QuestTargetSetRequirementData>(true))
                {
                    var protos = d.targets?.targets;
                    if (protos == null)
                        continue;
                    foreach (var pt in protos)
                        AddBinaryTag(pt?.tag, quantity, binary, addedOut);
                }
                foreach (
                    var d in composed.Data.GetAllChildren<QuestTargetComboRequirementData>(true)
                )
                    AddBinaryTag(d.tag, quantity, binary, addedOut);
                foreach (var d in composed.Data.GetAllChildren<QuestTopTagRequirementData>(true))
                    AddBinaryTag(d.tag, quantity, binary, addedOut);
                foreach (
                    var d in composed.Data.GetAllChildren<QuestAboveTheFoldTagRequirementData>(true)
                )
                    AddBinaryTag(d.tag, quantity, binary, addedOut);
            }
        }

        private static void AddTag(
            PlayerStatDataTag tag,
            HashSet<PlayerStatDataTag> bucket,
            HashSet<PlayerStatDataTag> addedOut
        )
        {
            if (tag != null && bucket.Add(tag))
                addedOut.Add(tag);
        }

        // Add to the binary bucket unless the tag is already claimed as
        // quantity elsewhere on this quest - a scaling-reward tag stays in
        // quantity even if the same quest also has a threshold requirement
        // on it (stacking beats threshold for "keep chasing").
        private static void AddBinaryTag(
            PlayerStatDataTag tag,
            HashSet<PlayerStatDataTag> quantity,
            HashSet<PlayerStatDataTag> binary,
            HashSet<PlayerStatDataTag> addedOut
        )
        {
            if (tag == null || quantity.Contains(tag))
                return;
            if (binary.Add(tag))
                addedOut.Add(tag);
        }

        // Dumps the full structure of a ComposedQuest to the log. Used by
        // tests and by the scanner when extraction turns up empty on a
        // non-dummy quest, so we can see exactly which components the game
        // built for that quest rather than guessing.
        internal static void DumpComposedQuestStructure(ComposedQuest composed, string reason)
        {
            if (composed == null)
                return;

            AssignmentLog.Verbose(
                "GOALS",
                "[DUMP] "
                    + reason
                    + " for "
                    + QuestIdentityLabel(composed)
                    + " (isDummy="
                    + composed.IsDummy
                    + ")"
            );

            // Runtime tree - what ComposedQuest.Eval / UI strip iterate.
            var runtimeTypes = composed
                .GetComponentsInChildren<object>(true)
                .Select(o => o.GetType().Name)
                .Where(n => n != "ComposedQuest")
                .ToList();
            AssignmentLog.Verbose(
                "GOALS",
                "[DUMP]   runtime tree ("
                    + runtimeTypes.Count
                    + "): ["
                    + string.Join(", ", runtimeTypes)
                    + "]"
            );

            // Data tree - authored ScriptableObject hierarchy. The UI and
            // CreateQuest read from here to build the runtime tree. When
            // the data tree has components but the runtime tree doesn't,
            // something went wrong in ComposableRuntimeComponent build.
            if (composed.Data != null)
            {
                var dataTypes = composed
                    .Data.GetAllChildren<UnityEngine.Object>(false)
                    .Select(o => o.GetType().Name)
                    .ToList();
                AssignmentLog.Verbose(
                    "GOALS",
                    "[DUMP]   data tree ("
                        + dataTypes.Count
                        + "): ["
                        + string.Join(", ", dataTypes)
                        + "]"
                );
            }
        }

        // Human-readable label so faction quests are identifiable in logs.
        // e.g. "Mafia: Cargo Cover-Up" vs. "District weekly".
        private static string QuestIdentityLabel(Quest quest)
        {
            if (quest is ComposedQuest cq && cq.Data != null)
            {
                var idName =
                    cq.Data.identity != null ? cq.Data.identity.UnlocalizedIdentityName : "?";
                return idName + ": " + (cq.Data.UnlocalizedTitle ?? "?");
            }
            if (quest is HotTopicsQuest)
                return "district";
            return "quest";
        }

        // Tags from news items that have ANY production progress - reporter assigned,
        // reporter returned with a completed story file, or the file is sitting in the
        // production queue awaiting the newspaper layout step. Used to avoid
        // double-chasing binary (green-panel) goals.
        internal static HashSet<PlayerStatDataTag> GetInProgressTags()
        {
            var tags = new HashSet<PlayerStatDataTag>();
            if (LiveReportableManager.Instance == null)
                return tags;

            foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems())
            {
                if (newsItem?.Data == null)
                    continue;

                bool hasProgress = false;
                foreach (var sf in newsItem.GetComponentsInChildren<NewsItemStoryFile>(true))
                {
                    if (sf.IsCompleted || sf.Assignee != null)
                    {
                        hasProgress = true;
                        break;
                    }
                }

                if (!hasProgress)
                    continue;

                foreach (var tag in newsItem.Data.DistinctStatTypes.OfType<PlayerStatDataTag>())
                    tags.Add(tag);
            }
            return tags;
        }
    }
}
