using System.Linq;
using System.Reflection;
using GlobalNews;
using Reportables;
using Risks.UI;
using UI;

namespace NewsTowerAutoAssign.InGameTests
{
    // In-game tests for save-load safety invariants.
    //
    // These run after the test-runner's readiness gate (LiveReportableManager
    // exists, at least one ComposedQuest exists), so by construction a full
    // save load has completed before any assertion here fires.
    //
    // What we test
    // ------------
    //
    //  * SafetyGate is open. If it's closed at test-run time, either (a) the
    //    Patch_AfterLoad Postfix failed to run (Harmony install issue /
    //    exception swallowed) OR (b) the idle-workplace fallback also failed.
    //    Either way, automation would be silently dead for this session -
    //    surface it loudly rather than letting the user wonder why nothing
    //    auto-assigns.
    //
    //  * Reflection targets are resolvable. Probes the same members
    //    AutoAssignPlugin.VerifyReflection checks, so a game update that
    //    renames/removes members fails the test suite instead of just
    //    printing one error line that scrolls off the log.
    //
    //  * No suitcase node is stuck in the "unlocked but unresolved" state.
    //    If any suitcase has NodeState=Unlocked and DidAct=false at this
    //    point, the automation is broken: either the gate is wrong, the
    //    reflection failed, or the resolver logic regressed.
    internal static class SaveLoadSafetyTests
    {
        internal static void Run()
        {
            var ctx = new TestContext("SaveLoadSafety");
            GateIsOpenPostLoad(ctx);
            ReflectionTargetsResolvable(ctx);
            PopupReflectionTargetsResolvable(ctx);
            NoStuckSuitcases(ctx);
            ctx.PrintSummary();
        }

        // The runner only invokes us once game state is ready (see
        // InGameTestRunner.IsGameStateReady), so by the time we get here,
        // either Patch_AfterLoad or Patch_IdleWorkplaceDoState should have
        // opened the gate. A failure here means the save-safe automation
        // design is silently dead for this player's session.
        private static void GateIsOpenPostLoad(TestContext ctx)
        {
            ctx.Assert(
                SafetyGate.IsOpen,
                "SafetyGate is open once the game is ready",
                "gate still closed - Patch_AfterLoad Postfix likely failed; check earlier log for Patch_AfterLoad.Postfix error"
            );
        }

        // Match AutoAssignPlugin.VerifyReflection at plugin load. Running
        // this again at test time catches a very specific failure mode:
        // the game's assembly is swapped under a live process (e.g. Steam
        // auto-updated while the game was running and the new DLL got
        // partially loaded). Unlikely but possible, and surfacing it as a
        // test failure is cheap.
        private static void ReflectionTargetsResolvable(TestContext ctx)
        {
            ctx.Assert(
                AssignmentEvaluator.ProgressDoneEventFieldAvailable,
                "NewsItemStoryFile.progressDoneEvent resolvable",
                "reflection field not found - game update likely"
            );

            var (typeName, missing) = SuitcaseAutomation.ProbeReflectionTargets();
            ctx.Assert(
                missing.Length == 0,
                "SuitcaseAutomation reflection targets resolvable on " + typeName,
                "missing=[" + string.Join(", ", missing) + "]"
            );
        }

        // Verifies that the private fields the popup-skip patches set via
        // Traverse still exist after game updates. If either field is renamed,
        // Traverse.Field() silently does nothing and the popup freezes the game
        // for the full animation — the patch appears to run but has no effect.
        private static void PopupReflectionTargetsResolvable(TestContext ctx)
        {
            var didSkipField = typeof(SuitcasePopup).GetField(
                "didSkip",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
            ctx.Assert(
                didSkipField != null,
                "SuitcasePopup.didSkip field resolvable",
                "field not found — Patch_SuitcasePopupAutoSkip will silently fail to skip"
            );

            var shouldSkipField = typeof(RiskPopup).GetField(
                "shouldSkip",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
            ctx.Assert(
                shouldSkipField != null,
                "RiskPopup.shouldSkip field resolvable",
                "field not found — Patch_RiskPopupAutoSkip will silently fail to skip"
            );
        }

        // For every NewsItem on the board, every suitcase component's node
        // must not be in the state (Unlocked, DidAct=false). That combination
        // means the chain is blocked waiting for someone to make the story
        // visible - which is exactly what SuitcaseAutomation is supposed to
        // pre-resolve. If any are still stuck, the automation is failing
        // silently (gate stuck closed, reflection failing, resolver returning
        // early for some new reason).
        //
        // Uses reflection to read DidAct because the property is protected
        // on the generic base class NewsItemSuitcase<TData>.
        private static void NoStuckSuitcases(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("stuck suitcase", "LiveReportableManager not available");
                return;
            }

            int checkedCount = 0;
            int stuckCount = 0;
            foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems())
            {
                if (newsItem?.Data == null)
                    continue;

                foreach (
                    var suitcase in newsItem.GetComponentsInChildren<NewsItemSuitcaseBuildable>(
                        true
                    )
                )
                {
                    if (suitcase == null || suitcase.IsCompleted)
                        continue;
                    var node = suitcase.Node;
                    if (node == null || node.NodeState != NewsItemNodeState.Unlocked)
                        continue;

                    checkedCount++;

                    var didActProp = suitcase
                        .GetType()
                        .GetProperty(
                            "DidAct",
                            BindingFlags.Instance
                                | BindingFlags.Public
                                | BindingFlags.NonPublic
                                | BindingFlags.FlattenHierarchy
                        );
                    if (didActProp == null)
                    {
                        ctx.Skip(
                            "stuck suitcase: " + AssignmentLog.StoryName(newsItem),
                            "DidAct property not found via reflection"
                        );
                        continue;
                    }

                    bool didAct = (bool)didActProp.GetValue(suitcase, null);
                    bool stuck = !didAct;
                    if (stuck)
                        stuckCount++;
                    ctx.Assert(
                        !stuck,
                        "suitcase resolved: "
                            + AssignmentLog.StoryName(newsItem)
                            + " / "
                            + suitcase.GetType().Name,
                        "node state Unlocked + DidAct=false - auto-resolver didn't fire"
                    );
                }
            }

            if (checkedCount == 0)
                ctx.NotApplicable(
                    "stuck suitcase",
                    "no unlocked suitcases on the board to check right now"
                );
            else
                AutoAssignPlugin.Log.LogInfo(
                    "[INFO] SaveLoadSafety / checked "
                        + checkedCount
                        + " suitcase(s), "
                        + stuckCount
                        + " stuck"
                );
        }
    }
}
