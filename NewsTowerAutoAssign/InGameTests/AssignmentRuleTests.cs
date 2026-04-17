using System.Collections.Generic;
using System.Linq;
using GlobalNews;
using Reportables;
using Tower_Stats;

namespace NewsTowerAutoAssign.InGameTests
{
    // Tests for AssignmentRules - the game-typed helpers.
    // These run against live game state and skip gracefully when the game isn't ready.
    internal static class AssignmentRuleTests
    {
        internal static void Run()
        {
            var ctx = new TestContext("AssignmentRules");
            StoryMatchTests(ctx);
            PriorityTests(ctx);
            ctx.PrintSummary();
        }

        private static void StoryMatchTests(TestContext ctx)
        {
            var empty = new HashSet<PlayerStatDataTag>();

            // Degenerate case: all empty → no match
            ctx.Assert(
                !AssignmentRules.StoryMatchesUncoveredGoal(empty, empty, empty, empty),
                "all-empty → false"
            );

            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip(
                    "live-tag tests",
                    "LiveReportableManager not available - load a game first"
                );
                return;
            }

            var story = LiveReportableManager
                .Instance.GetNewsItems()
                .FirstOrDefault(n => n?.Data != null);
            if (story == null)
            {
                ctx.Skip("live-tag tests", "no stories on board");
                return;
            }

            var tags = story.Data.DistinctStatTypes.OfType<PlayerStatDataTag>().ToList();
            if (tags.Count == 0)
            {
                ctx.Skip("live-tag tests", "first story has no PlayerStatDataTags");
                return;
            }

            var tag0 = tags[0];
            var setWithTag = new HashSet<PlayerStatDataTag> { tag0 };

            // Quantity goal: always matches regardless of inProgress
            ctx.Assert(
                AssignmentRules.StoryMatchesUncoveredGoal(tags, setWithTag, empty, empty),
                "quantity match → true"
            );
            ctx.Assert(
                AssignmentRules.StoryMatchesUncoveredGoal(tags, setWithTag, empty, setWithTag),
                "quantity match even when already in progress → true (quantity goals stack)"
            );

            // Binary goal uncovered → matches
            ctx.Assert(
                AssignmentRules.StoryMatchesUncoveredGoal(tags, empty, setWithTag, empty),
                "binary uncovered → true"
            );

            // Binary goal already covered → no match
            ctx.Assert(
                !AssignmentRules.StoryMatchesUncoveredGoal(tags, empty, setWithTag, setWithTag),
                "binary covered by in-progress story → false"
            );
        }

        private static void PriorityTests(TestContext ctx)
        {
            var empty = new HashSet<PlayerStatDataTag>();

            // No yield tags → always -1
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    Enumerable.Empty<PlayerStatDataTag>(),
                    empty,
                    empty,
                    empty,
                    empty,
                    _ => false
                ) == -1,
                "no tags → priority -1"
            );

            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("live priority tests", "load a game first");
                return;
            }

            var (quantity, scoop, binary) = ReporterLookup.GetCurrentGoalTagSets();
            var inProgress = ReporterLookup.GetInProgressTags();

            int tested = 0;
            foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems())
            {
                if (newsItem?.Data == null)
                    continue;
                foreach (var sf in newsItem.GetComponentsInChildren<NewsItemStoryFile>(true))
                {
                    int pri = AssignmentRules.GetPathGoalPriority(
                        sf.BaseYieldDistinctStatTypes.OfType<PlayerStatDataTag>(),
                        quantity,
                        scoop,
                        binary,
                        inProgress,
                        tag => sf.IsScoop(tag)
                    );
                    ctx.Assert(
                        pri >= -1 && pri <= 3,
                        "priority in [-1,3] for path " + (sf.AssignSkill?.skillName ?? "any"),
                        "got " + pri
                    );
                    tested++;
                }
            }

            if (tested == 0)
                ctx.Skip("live priority range", "no story files found on board");
        }
    }
}
