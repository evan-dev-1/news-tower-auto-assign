using System.Linq;
using _Game.Quests;
using _Game.Quests.Composed;
using BepInEx.Logging;
using GlobalNews;

namespace NewsTowerAutoAssign.InGameTests
{
    // Runs every in-game test suite exactly once per session, split across
    // two readiness phases:
    //
    //   Phase 1 — Pure suites (no board data needed).
    //     Fires as soon as LiveReportableManager exists (i.e. any save is loaded
    //     and the safety gate is open). Tests in this phase have zero live-state
    //     dependencies and will always produce [OK] on a healthy build regardless
    //     of board contents. Running them early means a regression is surfaced
    //     the moment you load any save, not only after faction quests appear.
    //
    //   Phase 2 — Live board suites (need real data).
    //     Fires once the board has at least one non-dummy ComposedQuest so goal-
    //     extraction and faction-tag tests have something to chew on. Accumulates
    //     into the same aggregator as Phase 1; the final summary covers both.
    //
    //   [OK]   every assertion passed              → LogLevel.Message (bright)
    //   [WARN] one or more suites logged a Skip()  → LogLevel.Warning (yellow)
    //   [FAIL] one or more assertions failed        → LogLevel.Error   (red)
    internal static class InGameTestRunner
    {
        private static bool _pureRan = false;
        private static bool _liveRan = false;

        internal static void RunOnceWhenReady()
        {
            // Phase 1: pure suites — fire as soon as any save is loaded.
            if (!_pureRan && LiveReportableManager.Instance != null)
            {
                _pureRan = true;
                TestRunAggregator.Reset();
                AutoAssignPlugin.Log.LogInfo("===== AutoAssign Pure Tests =====");
                DiscardPredicateTests.Run();
                KillSwitchTests.Run();
                AutoAssignPlugin.Log.LogInfo("===== Pure Tests complete =====");
            }

            // Phase 2: live board suites — fire once full game state is ready.
            if (_liveRan || !IsGameStateReady())
                return;

            _liveRan = true;
            AutoAssignPlugin.Log.LogInfo("========== AutoAssign In-Game Tests ==========");
            AssignmentRuleTests.Run();
            GoalExtractionTests.Run();
            LiveStateInvariantTests.Run();
            FactionTagDecisionTests.Run();
            SaveLoadSafetyTests.Run();
            GlobePinOwnershipTests.Run();
            PipelineInvariantTests.Run();
            PrintRunSummary();
            AutoAssignPlugin.Log.LogInfo("========== Tests complete ==========");
        }

        // Minimum state for every live suite to have real data:
        //   * Live manager exists (so board-walk suites can enumerate stories).
        //   * QuestManager has at least one non-dummy ComposedQuest active
        //     (so GoalExtraction has a runtime tree to validate).
        // Faction hubs (HiddenAgenda) are NOT required — many saves never expose
        // them, and the relevant tests degrade to [N/A] rather than [SKIP].
        private static bool IsGameStateReady()
        {
            if (LiveReportableManager.Instance == null)
                return false;
            if (QuestManager.Instance == null)
                return false;
            return QuestManager
                .Instance.AllRunningQuests.OfType<ComposedQuest>()
                .Any(cq => cq != null && !cq.IsDummy);
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
