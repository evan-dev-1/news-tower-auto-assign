using System.Collections.Generic;
using System.Linq;
using Employees;
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
            PriorityOrderingTests(ctx);
            StoryMatchTests(ctx);
            PriorityTests(ctx);
            ReporterSelectionTests(ctx);
            ctx.PrintSummary();
        }

        // Pure tests — no live game state required.
        //
        // Two things verified:
        //   1. Enum ordering: PathPriority is int-backed and the evaluator uses
        //      > / < to compare scores, so the declared numeric values must match
        //      the documented hierarchy. A typo in the enum values (e.g. Quantity=3
        //      and UncoveredBinary=1) would silently invert path selection.
        //
        //   2. Scoring: each reachable priority tier must be reachable with the
        //      right input combination and only that combination. Uses the generic
        //      string overload so no game DLL types are needed — these assertions
        //      survive game updates that rename PlayerStatDataTag instances.
        private static void PriorityOrderingTests(TestContext ctx)
        {
            ctx.Assert(
                PathPriority.UncoveredScoop > PathPriority.UncoveredBinary,
                "UncoveredScoop > UncoveredBinary"
            );
            ctx.Assert(
                PathPriority.UncoveredBinary > PathPriority.Quantity,
                "UncoveredBinary > Quantity"
            );
            ctx.Assert(
                PathPriority.Quantity > PathPriority.CoveredBinary,
                "Quantity > CoveredBinary"
            );
            ctx.Assert(
                PathPriority.CoveredBinary > PathPriority.None,
                "CoveredBinary > None"
            );

            // Synthetic goal sets — string-typed so the test is pure.
            var qty = new HashSet<string> { "Economy" };
            var scoop = new HashSet<string> { "Crime" };
            var bin = new HashSet<string> { "Society", "Crime" };
            var empty = new HashSet<string>();
            var covered = new HashSet<string> { "Society" };

            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    new[] { "Sports" }, qty, scoop, bin, empty, _ => false
                ) == PathPriority.None,
                "unrelated tag → None"
            );
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    new[] { "Economy" }, qty, scoop, bin, empty, _ => false
                ) == PathPriority.Quantity,
                "quantity tag → Quantity"
            );
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    new[] { "Society" }, qty, scoop, bin, covered, _ => false
                ) == PathPriority.CoveredBinary,
                "binary tag already in-progress → CoveredBinary"
            );
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    new[] { "Crime" }, qty, scoop, bin, empty, _ => false
                ) == PathPriority.UncoveredBinary,
                "uncovered binary tag on non-scoop path → UncoveredBinary"
            );
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    new[] { "Crime" }, qty, scoop, bin, empty, t => t == "Crime"
                ) == PathPriority.UncoveredScoop,
                "uncovered scoop tag on scoop-qualified path → UncoveredScoop"
            );
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    new[] { "Sports", "Economy" }, qty, scoop, bin, empty, _ => false
                ) == PathPriority.Quantity,
                "multi-tag path: best priority wins (Sports+Economy → Quantity)"
            );
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    new[] { "Society", "Economy" }, qty, scoop, bin, covered, _ => false
                ) == PathPriority.Quantity,
                "Quantity beats CoveredBinary when both tags present on same path"
            );
            // Once already covered by in-progress, a non-scoop binary is UncoveredBinary
            // only when it's NOT in the covered set — verify the boundary.
            var crimeCovered = new HashSet<string> { "Crime" };
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    new[] { "Crime" }, qty, scoop, bin, crimeCovered, t => t == "Crime"
                ) == PathPriority.CoveredBinary,
                "scoop tag already in-progress → CoveredBinary (not UncoveredScoop)"
            );
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

            // No yield tags → always PathPriority.None
            ctx.Assert(
                AssignmentRules.GetPathGoalPriority(
                    Enumerable.Empty<PlayerStatDataTag>(),
                    empty,
                    empty,
                    empty,
                    empty,
                    _ => false
                ) == PathPriority.None,
                "no tags → PathPriority.None"
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
                    var pri = AssignmentRules.GetPathGoalPriority(
                        sf.BaseYieldDistinctStatTypes.OfType<PlayerStatDataTag>(),
                        quantity,
                        scoop,
                        binary,
                        inProgress,
                        tag => sf.IsScoop(tag)
                    );
                    ctx.Assert(
                        pri >= PathPriority.None && pri <= PathPriority.UncoveredScoop,
                        "priority in valid PathPriority range for path "
                            + (sf.AssignSkill?.skillName ?? "any"),
                        "got " + pri
                    );
                    tested++;
                }
            }

            if (tested == 0)
                ctx.Skip("live priority range", "no story files found on board");
        }

        // Validates the six-clause filter in PickBestAvailable by asserting that:
        //   1. GetSkillLevel returns 0 for null inputs (pure, always runs).
        //   2. When PickBestAvailable returns a result, that result satisfies every
        //      filter clause — a regressed clause (e.g. Assignment check removed)
        //      would return a filtered-out employee and these assertions would fail.
        //   3. When PickBestAvailable returns null, there is genuinely no free
        //      employee — a false null return would fail this assertion.
        private static void ReporterSelectionTests(TestContext ctx)
        {
            ctx.Assert(
                ReporterLookup.GetSkillLevel(null, null) == 0,
                "GetSkillLevel(null, null) == 0"
            );

            var result = ReporterLookup.PickBestAvailable(null);

            if (result != null)
            {
                ctx.Assert(
                    result.IsAvailableForGlobeAssignment,
                    "PickBestAvailable result: IsAvailableForGlobeAssignment"
                );
                ctx.Assert(
                    result.AssignableToReportable != null,
                    "PickBestAvailable result: AssignableToReportable != null"
                );
                ctx.Assert(
                    result.AssignableToReportable?.Assignment == null,
                    "PickBestAvailable result: not currently assigned"
                );
                ctx.Assert(
                    result.SkillHandler != null,
                    "PickBestAvailable result: SkillHandler != null"
                );
                ctx.Assert(
                    result.JobHandler?.JobData?.hideFromDrawer == false,
                    "PickBestAvailable result: not hidden from drawer"
                );

                ctx.Assert(
                    ReporterLookup.GetSkillLevel(result, null) == 0,
                    "GetSkillLevel(employee, null) == 0"
                );
            }
            else
            {
                // Null is valid only when no employee passes all filter clauses.
                // If any free employee exists, PickBestAvailable should have returned them.
                bool anyFree = Employee.Employees.Any(e =>
                    e != null
                    && e.IsAvailableForGlobeAssignment
                    && e.AssignableToReportable != null
                    && e.AssignableToReportable.Assignment == null
                    && e.SkillHandler != null
                    && e.JobHandler?.JobData?.hideFromDrawer == false
                );
                ctx.Assert(
                    !anyFree,
                    "PickBestAvailable(null) returns null only when all employees are busy",
                    "at least one free employee exists but PickBestAvailable returned null"
                );
            }

            // Skill-level ordering: for any reporter returned from PickBestAvailable,
            // no other free reporter should have a strictly higher skill level for any
            // skill the returned reporter was picked for (i.e. null-skill pick).
            // We can't enumerate a reporter's skills directly, so instead we verify the
            // simpler invariant: every free reporter's GetSkillLevel(null) == 0, meaning
            // null-skill ordering is a no-op tie (picks first in roster order), not
            // accidentally reversed.
            int checkedCount = 0;
            foreach (var employee in Employee.Employees.Take(5))
            {
                if (employee == null)
                    continue;
                ctx.Assert(
                    ReporterLookup.GetSkillLevel(employee, null) == 0,
                    "GetSkillLevel(employee, nullSkill) == 0 for " + (employee.name ?? "?")
                );
                checkedCount++;
            }

            if (checkedCount == 0)
                ctx.NotApplicable(
                    "GetSkillLevel null-skill checks",
                    "no employees found on roster"
                );
        }
    }
}
