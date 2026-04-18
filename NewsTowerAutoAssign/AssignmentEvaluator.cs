using System;
using System.Collections.Generic;
using System.Linq;
using _Game._Common;
using Assigner;
using Employees;
using GameState;
using GlobalNews;
using Persons;
using Reportables;
using Risks;
using Skills;
using Tower_Stats;

namespace NewsTowerAutoAssign
{
    // Core auto-assignment decision flow - entry points, goal snapshot, and
    // per-story phase orchestration. Discard / path / assign details live in
    // AssignmentEvaluator.Pipeline.cs (partial class).
    //
    // Public entry points:
    //   TryAutoAssignAll    - full board scan; called from the idle-state patch
    //                         and after save-load.
    //   TryAssignNewsItem   - single story; called from AddReportable patch.
    //
    // Both gate on AutoAssignEnabled so the user's toggle is honoured; both also
    // use the shared `_isAssigning` reentrancy flag because AssignTo can itself
    // trigger additional patches.
    internal static partial class AssignmentEvaluator
    {
        // Reentrancy guard. Main-thread-only: Harmony patches fire on the
        // Unity main thread, so a non-volatile bool is sufficient. Do not
        // touch from a background thread.
        private static bool _isAssigning;

        internal static bool ProgressDoneEventFieldAvailable =>
            GameReflection.ProgressDoneEventFieldAvailable;

        internal static void TryAutoAssignAll()
        {
            if (_isAssigning)
                return;
            if (!AutoAssignPlugin.AutoAssignEnabled.Value)
                return;
            if (LiveReportableManager.Instance == null)
                return;
            // Defence in depth: every current call site opens the gate before
            // invoking us (Patch_AfterLoad opens then calls, Patch_IdleWorkplaceDoState
            // opens then calls). The gate check here protects us against a
            // future hook site that might forget to do so - the per-story
            // RemoveReportable / AssignTo mutations downstream are not safe
            // to fire mid-save-load under any circumstance.
            if (!SafetyGate.IsOpen)
                return;

            _isAssigning = true;
            try
            {
                var (quantityGoalTags, scoopGoalTags, binaryGoalTags, inProgressTags) =
                    LoadGoalContext();
                foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems().ToList())
                    ProcessScannedNewsItem(
                        newsItem,
                        quantityGoalTags,
                        scoopGoalTags,
                        binaryGoalTags,
                        inProgressTags
                    );
            }
            catch (Exception ex)
            {
                AssignmentLog.Error("TryAutoAssignAll: " + ex);
            }
            finally
            {
                _isAssigning = false;
            }
        }

        // One iteration of the board scan: side-effect resolutions (bribes,
        // suitcases) -> core assignment -> incremental in-progress merge.
        private static void ProcessScannedNewsItem(
            NewsItem newsItem,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            if (newsItem?.Data == null)
                return;
            // Pay any bribe nodes that unlocked since the last scan
            // (e.g. step 2 of a multi-step story that arrived before the weekend).
            BribeAutomation.TryPayBribes(newsItem);
            // Same rationale for suitcase reward nodes: they unlock when the
            // chain reaches them but don't self-resolve until the player
            // makes the story visible. Proactively resolve so the chain
            // doesn't stall behind an unread "new item!" popup.
            SuitcaseAutomation.TryResolveSuitcases(newsItem);
            AssignNewsItemCore(
                newsItem,
                quantityGoalTags,
                scoopGoalTags,
                binaryGoalTags,
                inProgressTags
            );
            MergeInProgressTags(newsItem, inProgressTags);
        }

        // Incrementally merge this item's tags into the running in-progress
        // snapshot rather than rescanning the whole board. Without this, two
        // fresh stories carrying the same binary goal (e.g. Red Herring)
        // both read the tag as uncovered and double-dip - spending two
        // reporter-weeks on one check mark. Rescanning worked but was O(N²)
        // in the board size; incremental is O(k) per item.
        //
        // Only merge when the item survived the scan (wasn't discarded) AND
        // at least one slot is now in progress - a fully idle item that we
        // decided not to assign shouldn't prematurely "cover" its tags for
        // later items in the same scan.
        private static void MergeInProgressTags(
            NewsItem newsItem,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            if (!AutoAssignPlugin.ChaseGoalsEnabled.Value)
                return;
            if (newsItem == null)
                return;
            if (!IsAnySlotInProgress(newsItem))
                return;
            foreach (var tag in newsItem.Data.DistinctStatTypes.OfType<PlayerStatDataTag>())
                inProgressTags.Add(tag);
        }

        // Returns true if any story file on the news item is either completed
        // or currently assigned. Used to decide whether to merge the item's
        // tags into the running inProgressTags snapshot after a scan - see
        // TryAutoAssignAll for why that matters.
        internal static bool IsAnySlotInProgress(NewsItem newsItem)
        {
            foreach (var storyFile in newsItem.GetComponentsInChildren<NewsItemStoryFile>(true))
            {
                if (storyFile != null && (storyFile.IsCompleted || storyFile.Assignee != null))
                    return true;
            }
            return false;
        }

        internal static void TryAssignNewsItem(NewsItem newsItem)
        {
            if (_isAssigning)
                return;
            // Honour the user's Enabled toggle - without this, installing the mod
            // is effectively irreversible because AddReportable fires on every
            // story and would keep auto-assigning regardless of the flag.
            if (!AutoAssignPlugin.AutoAssignEnabled.Value)
                return;
            // Mid-save-load, story-file state (IsCompleted, Assignee,
            // progressDoneEvent) has not been restored by SetComponentData.
            // `alreadyInvested` is computed from those fields and would report
            // "fresh" for a story the save had actually progressed - any
            // subsequent LiveReportableManager.RemoveReportable here would
            // silently destroy saved progress. Also, AssignTo mutates employee
            // state against a roster that hasn't finished restoring. Defer to
            // SafetyGate - see that class for the open/close event map.
            if (!SafetyGate.IsOpen)
                return;

            _isAssigning = true;
            try
            {
                var (quantityGoalTags, scoopGoalTags, binaryGoalTags, inProgressTags) =
                    LoadGoalContext();
                AssignNewsItemCore(
                    newsItem,
                    quantityGoalTags,
                    scoopGoalTags,
                    binaryGoalTags,
                    inProgressTags
                );
            }
            catch (Exception ex)
            {
                AssignmentLog.Error("TryAssignNewsItem: " + ex);
            }
            finally
            {
                _isAssigning = false;
            }
        }

        // Loads the current goal tag snapshot, or empty sets if ChaseGoals is off.
        // Kept here so both entry points build identical snapshots.
        private static (
            HashSet<PlayerStatDataTag> quantity,
            HashSet<PlayerStatDataTag> scoop,
            HashSet<PlayerStatDataTag> binary,
            HashSet<PlayerStatDataTag> inProgress
        ) LoadGoalContext()
        {
            var empty = new HashSet<PlayerStatDataTag>();
            if (!AutoAssignPlugin.ChaseGoalsEnabled.Value)
            {
                GoalChaseSnapshotLog.MaybeLog(false, empty, empty, empty);
                return (empty, empty, empty, empty);
            }

            var (quantity, scoop, binary) = ReporterLookup.GetCurrentGoalTagSets();
            GoalChaseSnapshotLog.MaybeLog(true, quantity, scoop, binary);
            var inProgress = ReporterLookup.GetInProgressTags();
            return (quantity, scoop, binary, inProgress);
        }

        // Per-news-item evaluation state. Collected once at the top of the
        // scan so each phase method doesn't re-walk the transform tree or
        // re-evaluate "is this story invested / goal-matching / risky".
        //
        // Not a struct: the HashSet references are already reference-typed,
        // and a class with one allocation per news item is cheaper than
        // passing an 8-field struct by value to every helper.
        private sealed class EvalContext
        {
            internal NewsItem NewsItem;
            internal HashSet<PlayerStatDataTag> Quantity;
            internal HashSet<PlayerStatDataTag> Scoop;
            internal HashSet<PlayerStatDataTag> Binary;
            internal HashSet<PlayerStatDataTag> InProgress;
            internal List<NewsItemStoryFile> AllStoryFiles;
            internal List<INewsItemRisk> Risks;
            internal bool AlreadyInvested;
            internal bool GoalsLoaded;
            internal bool StoryMatchesGoal;
            internal bool HasRisk;
        }

        // Orchestrator. Each phase helper either runs a discard (and returns
        // true so we bail) or a side-effect log. Keeping this method focused
        // on control flow makes the execution order obvious at a glance -
        // the actual per-phase logic lives in the helpers below.
        private static void AssignNewsItemCore(
            NewsItem newsItem,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            if (IsPassivelyBelowReporterThreshold(newsItem))
                return;

            var ctx = BuildContext(
                newsItem,
                quantityGoalTags,
                scoopGoalTags,
                binaryGoalTags,
                inProgressTags
            );

            // Slots are needed before discard phases when ObviousPath mode would
            // leave this story in WAIT(path): discarding a story the player is
            // actively choosing a branch for (e.g. weekend timer) is jarring.
            var slots = CollectOrderedAssignableSlots(ctx);
            // Compute once: used both to gate discards and to emit the WAIT(path) log.
            bool isAmbiguous = HasAmbiguousTopPathPriority(ctx, slots);
            // True when this item would hit WAIT(path) in ObviousPath mode: an actual
            // either/or XOR branch with no tie-break. Such stories are treated as
            // player-owned; automatic discards are skipped so they are not yanked off
            // the board while the player is deciding.
            bool deferDiscardsForManualPath =
                AutoAssignPlugin.AutoAssignOnlyObviousPath.Value && isAmbiguous;

            if (!deferDiscardsForManualPath && TryDiscardForRisk(ctx))
                return;
            LogRiskKeptIfApplicable(ctx);

            if (!deferDiscardsForManualPath && TryDiscardForDeadEnd(ctx))
                return;

            if (!deferDiscardsForManualPath && TryDiscardForWeekend(ctx))
                return;
            LogWeekendKeptIfApplicable(ctx);

            if (slots.Count == 0)
                return;

            if (!deferDiscardsForManualPath && TryDiscardForAvailability(ctx, slots))
                return;

            if (AutoAssignPlugin.AutoAssignOnlyObviousPath.Value && isAmbiguous)
            {
                bool withChase = AutoAssignPlugin.ChaseGoalsEnabled.Value;
                AssignmentLog.DecisionOnce(
                    ctx.NewsItem,
                    withChase ? "path_goal_tie" : "path_tie_no_chase",
                    AssignmentLog.StoryName(ctx.NewsItem)
                        + " "
                        + AssignmentLog.StoryTagList(ctx.NewsItem)
                        + " → WAIT (path): multiple assignable paths tie"
                        + (withChase ? " on goal priority" : " with ChaseGoals off")
                        + " - assign manually."
                );
                return;
            }

            // Mark as mod-owned before entering the slot loop so the "!" pin
            // shows green immediately — even when all reporters are busy and
            // we won't get past WAIT (no reporter) this scan.
            AutoAssignOwnershipRegistry.MarkModAutoAssigned(ctx.NewsItem);

            foreach (var storyFile in slots)
                TryAssignSingleSlot(
                    ctx.NewsItem,
                    storyFile,
                    slots,
                    ctx.Quantity,
                    ctx.Scoop,
                    ctx.Binary,
                    ctx.InProgress
                );
        }

        // Tutorial / early-game safety guard. Below the configured reporter
        // threshold we cannot make reliable skill/completability judgements,
        // so we skip ALL auto-assign AND discard logic - the player handles
        // everything manually until the roster grows.
        private static bool IsPassivelyBelowReporterThreshold(NewsItem newsItem)
        {
            int reporterCount = ReporterLookup.CountPlayableReporters();
            if (reporterCount >= AutoAssignPlugin.MinReportersToActivate.Value)
                return false;
            AssignmentLog.Verbose(
                "ASSIGN",
                "Skipped "
                    + AssignmentLog.StoryName(newsItem)
                    + " because only "
                    + reporterCount
                    + " reporter(s), need "
                    + AutoAssignPlugin.MinReportersToActivate.Value
                    + " for automation."
            );
            return true;
        }

        // Enumerates the story-file and risk trees once, computes the derived
        // booleans every phase reads. Kept in one place so a future field in
        // EvalContext has exactly one authoring site.
        private static EvalContext BuildContext(
            NewsItem newsItem,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            var allStoryFiles = newsItem.GetComponentsInChildren<NewsItemStoryFile>(true).ToList();
            var risks = newsItem.GetComponentsInChildren<INewsItemRisk>(true).ToList();
            bool goalsLoaded = quantityGoalTags.Count > 0 || binaryGoalTags.Count > 0;
            return new EvalContext
            {
                NewsItem = newsItem,
                Quantity = quantityGoalTags,
                Scoop = scoopGoalTags,
                Binary = binaryGoalTags,
                InProgress = inProgressTags,
                AllStoryFiles = allStoryFiles,
                Risks = risks,
                AlreadyInvested = allStoryFiles.Any(sf => sf.IsCompleted || sf.Assignee != null),
                GoalsLoaded = goalsLoaded,
                // Goals are loaded when either tag set is non-empty; treat
                // empty as "unknown". For binary goals, StoryMatchesUncoveredGoal
                // only counts them if not already covered by an in-progress story -
                // double-covering is wasteful. Quantity goals always count.
                StoryMatchesGoal =
                    goalsLoaded
                    && AssignmentRules.StoryMatchesUncoveredGoal(
                        newsItem.Data.DistinctStatTypes.OfType<PlayerStatDataTag>(),
                        quantityGoalTags,
                        binaryGoalTags,
                        inProgressTags
                    ),
                HasRisk = risks.Count > 0,
            };
        }

        // Risky story with nothing invested and no goal match → discard.
        // Returns true when the item was discarded so the caller can bail.
        private static bool TryDiscardForRisk(EvalContext ctx)
        {
            if (
                !DiscardPredicates.ShouldDiscardForRisk(
                    AutoAssignPlugin.AvoidRisksEnabled.Value,
                    ctx.AlreadyInvested,
                    ctx.GoalsLoaded,
                    ctx.HasRisk,
                    ctx.StoryMatchesGoal
                )
            )
                return false;
            var riskTypes = string.Join(
                ", ",
                ctx.Risks.Select(risk => risk.GetType().Name).Distinct()
            );
            DiscardStory(
                ctx.NewsItem,
                "risk",
                "risky ("
                    + riskTypes
                    + ") and no story tag matches an uncovered goal."
                    + GoalSnapshotSuffix(ctx.Quantity, ctx.Binary, ctx.InProgress)
            );
            return true;
        }

        // When a risky story is kept only because it matches an active goal,
        // log the reason once so it's visible WHY we kept something risky.
        // Invested stories are always kept and don't need this message.
        private static void LogRiskKeptIfApplicable(EvalContext ctx)
        {
            if (
                !AutoAssignPlugin.AvoidRisksEnabled.Value
                || !ctx.HasRisk
                || !ctx.StoryMatchesGoal
                || ctx.AlreadyInvested
            )
                return;
            AssignmentLog.DecisionOnce(
                ctx.NewsItem,
                "risk_kept",
                AssignmentLog.StoryName(ctx.NewsItem)
                    + " "
                    + AssignmentLog.StoryTagList(ctx.NewsItem)
                    + " → KEPT (risk): risky but "
                    + DescribeGoalMatch(ctx.NewsItem, ctx.Quantity, ctx.Binary, ctx.InProgress)
            );
        }

        // Completability check. Every non-resolved node on the story must
        // have at least one viable path (building built AND a reporter with
        // the required skill). If any node has zero viable paths, the whole
        // story is dead-ended and gets discarded.
        private static bool TryDiscardForDeadEnd(EvalContext ctx)
        {
            if (!HasDeadEndNode(ctx.AllStoryFiles))
                return false;
            DiscardStory(
                ctx.NewsItem,
                "dead-end",
                "at least one node has no viable path "
                    + "(missing building or no reporter with required skill)."
            );
            return true;
        }

        // Weekend deadline check. The weekly print window opens on Sunday; a
        // fresh story arriving Saturday or Sunday has no realistic chance of
        // finishing in time. Invested stories are untouched (we already paid
        // time on them) and goal-matching stories are protected (composed-
        // quest tags like Red Herring spawn rarely).
        private static bool TryDiscardForWeekend(EvalContext ctx)
        {
            bool isWeekend = TowerTimes.IsWeekend(TowerTime.CurrentTime);
            if (
                !DiscardPredicates.ShouldDiscardForWeekend(
                    AutoAssignPlugin.DiscardFreshStoriesOnWeekend.Value,
                    ctx.AlreadyInvested,
                    isWeekend,
                    ctx.StoryMatchesGoal
                )
            )
                return false;
            DiscardStory(
                ctx.NewsItem,
                "weekend",
                "arrived "
                    + TowerTime.CurrentTime.Day
                    + ", fresh, and no story tag matches an uncovered goal."
                    + GoalSnapshotSuffix(ctx.Quantity, ctx.Binary, ctx.InProgress)
            );
            return true;
        }

        // Mirrors LogRiskKeptIfApplicable - when a fresh weekend story is
        // spared because it matches an active goal, log why once.
        private static void LogWeekendKeptIfApplicable(EvalContext ctx)
        {
            bool isWeekend = TowerTimes.IsWeekend(TowerTime.CurrentTime);
            if (
                !AutoAssignPlugin.DiscardFreshStoriesOnWeekend.Value
                || !isWeekend
                || !ctx.StoryMatchesGoal
                || ctx.AlreadyInvested
            )
                return;
            AssignmentLog.DecisionOnce(
                ctx.NewsItem,
                "weekend_kept",
                AssignmentLog.StoryName(ctx.NewsItem)
                    + " "
                    + AssignmentLog.StoryTagList(ctx.NewsItem)
                    + " → KEPT (weekend): arrived "
                    + TowerTime.CurrentTime.Day
                    + " but "
                    + DescribeGoalMatch(ctx.NewsItem, ctx.Quantity, ctx.Binary, ctx.InProgress)
            );
        }

        // Gathers open slots, filters out paths that are blocked right now
        // (already running, completed, sibling chosen), and orders by goal
        // priority when ChaseGoals is on.
        private static List<NewsItemStoryFile> CollectOrderedAssignableSlots(EvalContext ctx)
        {
            var storyFiles = new List<NewsItemStoryFile>();
            ctx.NewsItem.GetUnlockedAndAssignableStoryFiles(storyFiles);
            if (storyFiles.Count == 0)
                return storyFiles;

            AssignmentLog.Verbose(
                "SLOTS",
                AssignmentLog.StoryName(ctx.NewsItem) + ": " + storyFiles.Count + " open slot(s)"
            );

            storyFiles = storyFiles.Where(sf => PathIsAssignableNow(sf)).ToList();
            if (storyFiles.Count == 0)
                return storyFiles;

            if (AutoAssignPlugin.ChaseGoalsEnabled.Value)
            {
                storyFiles = storyFiles
                    .OrderByDescending(sf =>
                        GetPathGoalPriority(sf, ctx.Quantity, ctx.Scoop, ctx.Binary, ctx.InProgress)
                    )
                    .ToList();
                LogPathOrder(storyFiles, ctx.Quantity, ctx.Scoop, ctx.Binary, ctx.InProgress);
            }
            return storyFiles;
        }

        // Uses PathPriority from AssignmentRules plus the game's graph: parallel
        // prerequisites (e.g. NewsItemAndBranch / AND merge) are not ambiguous —
        // multiple assignable slots can all be automated. Only alternatives under
        // the same NewsItemXorBranch count as "pick one path" ambiguity.
        private static bool HasAmbiguousTopPathPriority(
            EvalContext ctx,
            List<NewsItemStoryFile> slots
        )
        {
            if (slots == null || slots.Count <= 1)
                return false;
            if (!AutoAssignPlugin.ChaseGoalsEnabled.Value)
                return SlotsContainXorExclusivePair(slots);

            PathPriority best = PathPriority.None;
            foreach (var sf in slots)
            {
                var p = GetPathGoalPriority(
                    sf,
                    ctx.Quantity,
                    ctx.Scoop,
                    ctx.Binary,
                    ctx.InProgress
                );
                if (p > best)
                    best = p;
            }

            var tiedAtBest = slots
                .Where(sf =>
                    GetPathGoalPriority(sf, ctx.Quantity, ctx.Scoop, ctx.Binary, ctx.InProgress)
                    == best
                )
                .ToList();
            return tiedAtBest.Count > 1 && SlotsContainXorExclusivePair(tiedAtBest);
        }

        // Pre-loop availability check. Discard only if NO reporter will be
        // free soon for ANY visible slot AND nothing is invested AND the
        // story doesn't match an active goal. A goal tag outvalues the time
        // cost of waiting 12+ hours, so goal-matching stories are kept.
        private static bool TryDiscardForAvailability(
            EvalContext ctx,
            List<NewsItemStoryFile> slots
        )
        {
            float thresholdHours = AutoAssignPlugin.DiscardIfNoReporterForHours.Value;
            if (
                !DiscardPredicates.ShouldDiscardForAvailability(
                    ctx.AlreadyInvested,
                    ctx.StoryMatchesGoal,
                    thresholdHours,
                    anyReporterSoon: slots.Any(sf =>
                        ReporterLookup.AnyReporterAvailableSoon(sf.AssignSkill, thresholdHours)
                    )
                )
            )
                return false;
            DiscardStory(
                ctx.NewsItem,
                "availability",
                "no reporter free within "
                    + thresholdHours
                    + "h for any slot, fresh, and no story tag matches an uncovered goal."
                    + GoalSnapshotSuffix(ctx.Quantity, ctx.Binary, ctx.InProgress)
            );
            return true;
        }
    }
}
