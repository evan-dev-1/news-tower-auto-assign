using System.Linq;
using _Game.Quests;
using _Game.Quests.Composed;
using BepInEx.Logging;
using GlobalNews;

namespace NewsTowerAutoAssign.InGameTests
{
    // Runs every in-game test suite exactly ONCE per session, and only after
    // the game has reached a state where every suite has real data to chew on.
    // That means:
    //   * LiveReportableManager.Instance exists (live-state suites can enumerate stories)
    //   * QuestManager.Instance exists AND contains at least one non-dummy
    //     ComposedQuest (goal-extraction suite has something to extract)
    //
    // Running earlier than this produced SKIP lines for "no ComposedQuests
    // active yet" / "no fresh story carries an uncovered binary", which made
    // the summary line flip to [WARN] even when nothing was wrong. Waiting
    // until the state is fully ready lets the summary stabilise at [OK].
    //
    // Invoked from the idle-rescan patch, which fires every in-game tick;
    // the _ran latch guarantees a single run regardless.
    //
    //   [OK]   every assertion passed                  → LogLevel.Message (bright / white)
    //   [WARN] one or more suites logged a Skip()      → LogLevel.Warning (yellow)
    //   [FAIL] one or more assertions failed           → LogLevel.Error   (red)
    internal static class InGameTestRunner
    {
        private static bool _ran = false;

        internal static void RunOnceWhenReady()
        {
            if (_ran)
                return;
            if (!IsGameStateReady())
                return;

            _ran = true;
            TestRunAggregator.Reset();
            AutoAssignPlugin.Log.LogInfo("========== AutoAssign In-Game Tests ==========");
            DiscardPredicateTests.Run();
            AssignmentRuleTests.Run();
            GoalExtractionTests.Run();
            KillSwitchTests.Run();
            LiveStateInvariantTests.Run();
            FactionTagDecisionTests.Run();
            SaveLoadSafetyTests.Run();
            GlobePinOwnershipTests.Run();
            PrintRunSummary();
            AutoAssignPlugin.Log.LogInfo("========== Tests complete ==========");
        }

        // Minimum state for every suite to have real data:
        //   * Live manager exists (so LiveStateInvariants / FactionTagDecision
        //     can walk the board).
        //   * QuestManager has at least one non-dummy ComposedQuest active
        //     (so GoalExtraction has a runtime tree to validate).
        // Faction hubs (HiddenAgenda) are NOT required - many saves never
        // expose them, and the relevant tests degrade to [N/A] rather than
        // [SKIP] so the summary stays clean.
        private static bool IsGameStateReady()
        {
            if (LiveReportableManager.Instance == null)
                return false;
            if (QuestManager.Instance == null)
                return false;
            bool anyComposed = QuestManager
                .Instance.AllRunningQuests.OfType<ComposedQuest>()
                .Any(cq => cq != null && !cq.IsDummy);
            return anyComposed;
        }

        private static void PrintRunSummary()
        {
            int passed = TestRunAggregator.TotalPassed;
            int failed = TestRunAggregator.TotalFailed;
            int skipped = TestRunAggregator.TotalSkipped;

            string badge;
            LogLevel level;
            if (failed > 0)
            {
                badge = "[FAIL]";
                level = LogLevel.Error;
            }
            else if (skipped > 0)
            {
                badge = "[WARN]";
                level = LogLevel.Warning;
            }
            else
            {
                badge = "[OK]";
                level = LogLevel.Message;
            }

            AutoAssignPlugin.Log.Log(
                level,
                badge
                    + " [RUN] In-Game Tests: "
                    + passed
                    + " passed, "
                    + failed
                    + " failed, "
                    + skipped
                    + " skipped"
            );
        }
    }
}
