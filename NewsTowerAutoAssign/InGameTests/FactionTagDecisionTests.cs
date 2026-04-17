using System.Collections.Generic;
using System.Linq;
using GameState;
using GlobalNews;
using Reportables;
using Risks;
using Tower_Stats;

namespace NewsTowerAutoAssign.InGameTests
{
    // End-to-end decision tests for binary ("faction" / composed-quest) goal tags
    // like Red Herring. These are the tags the log prints under
    //   "binary: [Red Herring (Tower_Stats.PlayerStatDataTag)]"
    // and they need to survive every discard gate, not just the scoring step.
    //
    // Two layers:
    //
    //   1. Pure scenarios walk a synthetic story through each predicate to prove
    //      a binary-only tag is enough to beat risk, weekend and availability
    //      gates simultaneously. If any gate regresses to ignore matchesGoal,
    //      one of these asserts flips.
    //
    //   2. Live scenarios scan the current board and, for every fresh story
    //      whose actual PlayerStatDataTags include an uncovered binary goal,
    //      assert none of the three predicates would remove it right now. This
    //      is the "real Red Herring was on the board and we kept it" check.
    internal static class FactionTagDecisionTests
    {
        internal static void Run()
        {
            var ctx = new TestContext("FactionTagDecision");
            PureScenarios(ctx);
            LiveBoardScenarios(ctx);
            ctx.PrintSummary();
        }

        // Exercises AssignmentRules + DiscardPredicates with a story whose only
        // goal match is a binary tag - the shape of a real Red Herring injection.
        private static void PureScenarios(TestContext ctx)
        {
            var storyTags = new[] { "RedHerring" };
            var quantity = new HashSet<string>();
            var binary = new HashSet<string> { "RedHerring" };
            var inProgressUncovered = new HashSet<string>();
            var inProgressCovered = new HashSet<string> { "RedHerring" };

            bool matchesUncovered = AssignmentRules.StoryMatchesUncoveredGoal(
                storyTags,
                quantity,
                binary,
                inProgressUncovered
            );
            ctx.Assert(matchesUncovered, "binary-only tag registers as uncovered goal match");

            bool matchesCovered = AssignmentRules.StoryMatchesUncoveredGoal(
                storyTags,
                quantity,
                binary,
                inProgressCovered
            );
            ctx.Assert(
                !matchesCovered,
                "binary tag already in progress → no longer a match (prevents doubling up)"
            );

            // Binary-only path should score priority 2 (binary rung, not quantity=1 or scoop=3).
            int priority = AssignmentRules.GetPathGoalPriority(
                storyTags,
                quantity,
                new HashSet<string>(),
                binary,
                inProgressUncovered,
                _ => false
            );
            ctx.Assert(priority == 2, "binary-only path priority = 2", "got " + priority);

            // All three discard gates must respect the match when matchesUncovered is true.
            ctx.Assert(
                !DiscardPredicates.ShouldDiscardForRisk(
                    featureEnabled: true,
                    isInvested: false,
                    goalsLoaded: true,
                    hasRisk: true,
                    matchesUncoveredGoal: matchesUncovered
                ),
                "risky + fresh + binary-only goal match → kept"
            );
            ctx.Assert(
                !DiscardPredicates.ShouldDiscardForWeekend(
                    featureEnabled: true,
                    isInvested: false,
                    isWeekend: true,
                    matchesUncoveredGoal: matchesUncovered
                ),
                "weekend + fresh + binary-only goal match → kept"
            );
            ctx.Assert(
                !DiscardPredicates.ShouldDiscardForAvailability(
                    isInvested: false,
                    matchesGoal: matchesUncovered,
                    thresholdHours: 4f,
                    anyReporterSoon: false
                ),
                "no reporter soon + fresh + binary-only goal match → kept"
            );

            // Same trio, once the binary tag is already covered by another in-progress
            // story: match flips to false and the gates fire as normal. This is the
            // regression guard that proves we key off matchesGoal and not some other
            // signal that happens to be true in the live log.
            ctx.Assert(
                DiscardPredicates.ShouldDiscardForRisk(true, false, true, true, matchesCovered),
                "risky + fresh + binary already covered → discard"
            );
            ctx.Assert(
                DiscardPredicates.ShouldDiscardForWeekend(true, false, true, matchesCovered),
                "weekend + fresh + binary already covered → discard"
            );
            ctx.Assert(
                DiscardPredicates.ShouldDiscardForAvailability(false, matchesCovered, 4f, false),
                "no reporter soon + fresh + binary already covered → discard"
            );
        }

        // Walks the live board and checks that any fresh story currently matching
        // an uncovered binary goal is safe from all three predicates. Skips if the
        // game isn't loaded or if no such story is on the board right now.
        private static void LiveBoardScenarios(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("live board", "LiveReportableManager not available - runner bug");
                return;
            }

            var (_, _, binary) = ReporterLookup.GetCurrentGoalTagSets();
            if (binary.Count == 0)
            {
                ctx.NotApplicable(
                    "live board",
                    "no binary / composed-quest goal tags active in this save right now"
                );
                return;
            }

            var inProgress = ReporterLookup.GetInProgressTags();
            bool isWeekend = TowerTimes.IsWeekend(TowerTime.CurrentTime);

            int checkedCount = 0;
            foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems())
            {
                if (newsItem?.Data == null)
                    continue;

                bool isInvested = newsItem
                    .GetComponentsInChildren<NewsItemStoryFile>(true)
                    .Any(sf => sf.IsCompleted || sf.Assignee != null);
                if (isInvested)
                    continue; // handled by LiveStateInvariants

                var storyBinaryHits = newsItem
                    .Data.DistinctStatTypes.OfType<PlayerStatDataTag>()
                    .Where(t => binary.Contains(t) && !inProgress.Contains(t))
                    .Select(t => t.name)
                    .ToList();
                if (storyBinaryHits.Count == 0)
                    continue;

                checkedCount++;
                string label = ShortName(newsItem) + " [" + string.Join(",", storyBinaryHits) + "]";

                // The story matches a binary goal by construction, so matchesGoal=true.
                const bool matchesGoal = true;

                ctx.Assert(
                    !DiscardPredicates.ShouldDiscardForRisk(
                        AutoAssignPlugin.AvoidRisksEnabled.Value,
                        isInvested: false,
                        goalsLoaded: true,
                        hasRisk: newsItem.GetComponentsInChildren<INewsItemRisk>(true).Any(),
                        matchesUncoveredGoal: matchesGoal
                    ),
                    "binary-match fresh story not risk-discarded: " + label
                );

                ctx.Assert(
                    !DiscardPredicates.ShouldDiscardForWeekend(
                        AutoAssignPlugin.DiscardFreshStoriesOnWeekend.Value,
                        isInvested: false,
                        isWeekend: isWeekend,
                        matchesUncoveredGoal: matchesGoal
                    ),
                    "binary-match fresh story not weekend-discarded: " + label
                );

                ctx.Assert(
                    !DiscardPredicates.ShouldDiscardForAvailability(
                        isInvested: false,
                        matchesGoal: matchesGoal,
                        thresholdHours: AutoAssignPlugin.DiscardIfNoReporterForHours.Value,
                        anyReporterSoon: false
                    ),
                    "binary-match fresh story not availability-discarded: " + label
                );
            }

            if (checkedCount == 0)
                ctx.NotApplicable(
                    "live board",
                    "no fresh story on board currently carries an uncovered binary goal tag ("
                        + string.Join(",", binary.Select(t => t.name))
                        + ") - transient state, nothing to assert right now"
                );
        }

        private static string ShortName(NewsItem newsItem)
        {
            var name = newsItem.Data.name ?? "?";
            var paren = name.LastIndexOf(" (");
            return paren > 0 ? name.Substring(0, paren) : name;
        }
    }
}
