using System.Linq;
using GameState;
using GlobalNews;
using Reportables;
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

                // For availability: anyReporterSoon=true because the story is already running —
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

        private static string ShortName(NewsItem newsItem)
        {
            var name = newsItem.Data.name ?? "?";
            var paren = name.LastIndexOf(" (");
            return paren > 0 ? name.Substring(0, paren) : name;
        }
    }
}
