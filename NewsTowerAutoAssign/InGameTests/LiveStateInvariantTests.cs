using System.Collections.Generic;
using System.Linq;
using _Game._Common;
using GameState;
using GlobalNews;
using Reportables;
using Reportables.News;
using Risks;
using Tower_Stats;

namespace NewsTowerAutoAssign.InGameTests
{
    // Invariant checks against live game state.
    // These assert that the current board cannot contain stories that violate the
    // mod's core guarantees: invested stories are never discarded, goal-matching
    // stories are never discarded for availability, and no bribe is stuck.
    // Requires a game to be loaded - skips gracefully otherwise.
    internal static class LiveStateInvariantTests
    {
        internal static void Run()
        {
            var ctx = new TestContext("LiveStateInvariants");

            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("all", "LiveReportableManager not available - load a game first");
                ctx.PrintSummary();
                return;
            }

            InvestedStoryGuards(ctx);
            BribeStateChecks(ctx);
            UnassignedAdsHaveNoFreeEmployee(ctx);
            InProgressTagsAreConsistent(ctx);
            DeadEndStoriesNotOnBoard(ctx);
            ctx.PrintSummary();
        }

        // For every invested story on the board, verify none of the three discard
        // predicates would currently flag it for removal.
        private static void InvestedStoryGuards(TestContext ctx)
        {
            var (quantity, _, binary) = ReporterLookup.GetCurrentGoalTagSets();
            var inProgress = ReporterLookup.GetInProgressTags();
            bool goalsLoaded = quantity.Count > 0 || binary.Count > 0;
            bool isWeekend = TowerTimes.IsWeekend(TowerTime.CurrentTime);

            foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems())
            {
                if (newsItem?.Data == null)
                    continue;

                bool isInvested = newsItem
                    .GetComponentsInChildren<NewsItemStoryFile>(true)
                    .Any(sf => sf.IsCompleted || sf.Assignee != null);

                if (!isInvested)
                    continue;

                string name = ShortName(newsItem);
                bool hasRisk = newsItem.GetComponentsInChildren<INewsItemRisk>(true).Any();
                bool matchesGoal =
                    goalsLoaded
                    && AssignmentRules.StoryMatchesUncoveredGoal(
                        newsItem.Data.DistinctStatTypes.OfType<PlayerStatDataTag>(),
                        quantity,
                        binary,
                        inProgress
                    );

                ctx.Assert(
                    !DiscardPredicates.ShouldDiscardForRisk(
                        AutoAssignPlugin.AvoidRisksEnabled.Value,
                        isInvested,
                        goalsLoaded,
                        hasRisk,
                        matchesGoal
                    ),
                    "invested not risk-discarded: " + name
                );

                ctx.Assert(
                    !DiscardPredicates.ShouldDiscardForWeekend(
                        AutoAssignPlugin.DiscardFreshStoriesOnWeekend.Value,
                        isInvested,
                        isWeekend,
                        matchesGoal
                    ),
                    "invested not weekend-discarded: " + name
                );

                // For availability: anyReporterSoon=true because the story is already running -
                // this always keeps, confirming the predicate honours the invested flag.
                ctx.Assert(
                    !DiscardPredicates.ShouldDiscardForAvailability(
                        isInvested,
                        matchesGoal,
                        AutoAssignPlugin.DiscardIfNoReporterForHours.Value,
                        anyReporterSoon: true
                    ),
                    "invested not availability-discarded: " + name
                );
            }
        }

        // Verify no bribe component is stuck: IsChosen=true but IsCompleted=false and
        // IsDestroyed=false means a click was attempted but the popup never completed,
        // permanently blocking the node.
        private static void BribeStateChecks(TestContext ctx)
        {
            foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems())
            {
                if (newsItem?.Data == null)
                    continue;

                string name = ShortName(newsItem);
                foreach (
                    var bribe in newsItem.GetComponentsInChildren<NewsItemBribeComponent>(true)
                )
                {
                    if (bribe == null)
                        continue;
                    bool stuck = bribe.IsChosen && !bribe.IsCompleted && !bribe.IsDestroyed;
                    ctx.Assert(
                        !stuck,
                        "bribe not stuck: " + name,
                        stuck ? "IsChosen=true but not Completed or Destroyed" : ""
                    );
                }
            }
        }

        // After TryAssignAds has run, every unboycotted ad slot must either have
        // an assignee or have no employee currently free for it. An unassigned slot
        // with a free employee means the ad automation path has a hole — it should
        // have assigned someone on this tick but didn't.
        //
        // Slots are skipped when:
        //   * AutoAssignAds is off — feature disabled, no automation ran.
        //   * The required building isn't built — SkillIsAvailableForAd already
        //     filtered it out (WAIT: building missing).
        //   * Nobody on the roster has the required skill — ditto (WAIT: no skill).
        //   * All capable employees are currently busy — legitimate WAIT state.
        private static void UnassignedAdsHaveNoFreeEmployee(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("unassigned ads have no free employee", "no LiveReportableManager");
                return;
            }

            if (!AutoAssignPlugin.AutoAssignAds.Value)
            {
                ctx.NotApplicable("unassigned ads have no free employee", "AutoAssignAds is off");
                return;
            }

            int checkedSlots = 0;
            int violatingSlots = 0;
            foreach (var ad in LiveReportableManager.Instance.OnAdBoard)
            {
                if (ad?.Data == null || ad.HasBoycott)
                    continue;

                var storyFiles = new List<NewsItemStoryFile>();
                ad.GetUnlockedAndAssignableStoryFiles(storyFiles);

                foreach (var sf in storyFiles)
                {
                    if (sf == null || sf.IsCompleted || GameReflection.IsSlotAlreadyRunning(sf))
                        continue;
                    if (sf.Assignee != null)
                        continue; // already assigned, all good

                    var skill = sf.AssignSkill;

                    // Skip slots the automation would itself skip.
                    if (skill != null && !AssetUnlocker.IsUnlockedSafe(skill))
                        continue; // building not built
                    if (!ReporterLookup.AnyEmployeeEverHasSkill(skill))
                        continue; // nobody trained for this

                    var available = ReporterLookup.PickBestAvailable(skill);
                    if (available == null)
                        continue; // everyone busy — legitimate WAIT

                    // Unassigned slot + free employee = automation failure.
                    checkedSlots++;
                    violatingSlots++;
                    ctx.Fail(
                        "unassigned ad slot with free employee: " + AdShortName(ad),
                        "skill='"
                            + (skill?.skillName ?? "any")
                            + "' employee='"
                            + available.name
                            + "' is free"
                    );
                }
            }

            if (violatingSlots == 0)
            {
                if (checkedSlots == 0)
                    ctx.NotApplicable(
                        "unassigned ads have no free employee",
                        "no unboycotted ads with open slots on the board"
                    );
                else
                    ctx.Pass(
                        "unassigned ads have no free employee: all "
                            + checkedSlots
                            + " checked slots accounted for"
                    );
            }
        }

        // Verifies GetInProgressTags() returns every tag carried by every story
        // that has any production progress (completed or assigned slot). If a
        // tag is missing, the binary-goal dedup logic can over-assign: a second
        // reporter is sent to chase a goal already covered by an in-progress story
        // because the coverage check didn't see the tag.
        private static void InProgressTagsAreConsistent(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("in-progress tag coverage", "no LiveReportableManager");
                return;
            }

            var reported = ReporterLookup.GetInProgressTags();
            int storiesChecked = 0;
            int violations = 0;

            foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems())
            {
                if (newsItem?.Data == null)
                    continue;

                bool hasProgress = newsItem
                    .GetComponentsInChildren<NewsItemStoryFile>(true)
                    .Any(sf => sf != null && (sf.IsCompleted || sf.Assignee != null));
                if (!hasProgress)
                    continue;

                storiesChecked++;
                foreach (var tag in newsItem.Data.DistinctStatTypes.OfType<PlayerStatDataTag>())
                {
                    if (tag != null && !reported.Contains(tag))
                    {
                        violations++;
                        ctx.Fail(
                            "in-progress tag in GetInProgressTags: "
                                + ShortName(newsItem)
                                + " / "
                                + tag.name,
                            "GetInProgressTags() missed this tag — binary goal dedup will over-assign"
                        );
                    }
                }
            }

            if (violations == 0)
            {
                if (storiesChecked == 0)
                    ctx.NotApplicable(
                        "in-progress tag coverage",
                        "no in-progress stories on board"
                    );
                else
                    ctx.Pass(
                        "in-progress tag coverage: all tags from "
                            + storiesChecked
                            + " in-progress stories present in GetInProgressTags()"
                    );
            }
        }

        // After TryAutoAssignAll, no non-invested story on the board should have
        // a dead-end node (a node where every path is unworkable: building not
        // built or no reporter has the skill). Dead-end stories should have been
        // discarded by the pipeline. A violation means the discard path for
        // permanently unworkable stories regressed and they are silently
        // accumulating on the board.
        private static void DeadEndStoriesNotOnBoard(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("dead-end stories not on board", "no LiveReportableManager");
                return;
            }

            if (!AutoAssignPlugin.AutoAssignEnabled.Value)
            {
                ctx.NotApplicable("dead-end stories not on board", "AutoAssign disabled");
                return;
            }

            int reporterCount = ReporterLookup.CountPlayableReporters();
            if (reporterCount < AutoAssignPlugin.MinReportersToActivate.Value)
            {
                ctx.NotApplicable(
                    "dead-end stories not on board",
                    "below reporter threshold ("
                        + reporterCount
                        + " < "
                        + AutoAssignPlugin.MinReportersToActivate.Value
                        + ") — mod is passive"
                );
                return;
            }

            int checkedCount = 0;
            int violations = 0;
            foreach (var ni in LiveReportableManager.Instance.GetNewsItems())
            {
                if (ni?.Data == null)
                    continue;
                if (AssignmentEvaluator.IsAnySlotInProgress(ni))
                    continue; // invested stories are never discarded regardless of dead-ends

                var allFiles = ni.GetComponentsInChildren<NewsItemStoryFile>(true)
                    .Where(sf => sf != null)
                    .ToList();

                checkedCount++;
                if (AssignmentEvaluator.HasDeadEndNode(allFiles))
                {
                    violations++;
                    ctx.Fail(
                        "dead-end story discarded: " + ShortName(ni),
                        "non-invested story has an unworkable node but survived on the board"
                    );
                }
            }

            if (violations == 0)
            {
                if (checkedCount == 0)
                    ctx.NotApplicable(
                        "dead-end stories not on board",
                        "no non-invested stories on board to check"
                    );
                else
                    ctx.Pass(
                        "dead-end stories not on board: all "
                            + checkedCount
                            + " non-invested stories checked"
                    );
            }
        }

        private static string AdShortName(Ad ad)
        {
            if (ad?.Data == null)
                return "?";
            try
            {
                var title = ad.Title;
                if (!string.IsNullOrEmpty(title))
                    return title;
            }
            catch { }
            return ad.Data.name;
        }

        private static string ShortName(NewsItem newsItem)
        {
            var name = newsItem.Data.name ?? "?";
            var paren = name.LastIndexOf(" (");
            return paren > 0 ? name.Substring(0, paren) : name;
        }
    }
}
