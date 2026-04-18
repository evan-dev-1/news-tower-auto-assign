using System.Linq;
using GlobalNews;
using Reportables;

namespace NewsTowerAutoAssign.InGameTests
{
    // Tests for AutoAssignOwnershipRegistry and GlobeAttentionSync.
    //
    // What we test
    // ------------
    //  * Null safety: IsModAutoAssigned(null) returns false without throwing.
    //  * Round-trip: MarkModAutoAssigned makes IsModAutoAssigned return true.
    //  * Reset: ResetForNewSave clears all tracked stories.
    //  * Load rehydration: every in-progress story is in the registry after load,
    //    because Patch_AfterLoad calls MarkModAutoAssigned for all of them.
    //  * Attention sync: every mod-assigned story has UnseenState=FullSeen,
    //    because PromoteFullySeen is called on every MarkModAutoAssigned path.
    internal static class GlobePinOwnershipTests
    {
        internal static void Run()
        {
            var ctx = new TestContext("GlobePinOwnership");
            NullGuards(ctx);
            RegistryRoundTrip(ctx);
            ResetClearsRegistry(ctx);
            InProgressStoriesAreInRegistry(ctx);
            ModAssignedStoriesAreFullySeen(ctx);
            ctx.PrintSummary();
        }

        private static void NullGuards(TestContext ctx)
        {
            bool threw = false;
            bool result = true;
            try
            {
                result = AutoAssignOwnershipRegistry.IsModAutoAssigned(null);
            }
            catch
            {
                threw = true;
            }
            ctx.Assert(!threw, "IsModAutoAssigned(null) does not throw");
            ctx.Assert(!result, "IsModAutoAssigned(null) returns false");
        }

        // MarkModAutoAssigned followed by IsModAutoAssigned must return true.
        // If the story is already registered the test still passes (idempotent contract).
        private static void RegistryRoundTrip(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("registry round-trip", "no LiveReportableManager");
                return;
            }

            var target = LiveReportableManager
                .Instance.GetNewsItems()
                .FirstOrDefault(ni => ni?.Data != null);

            if (target == null)
            {
                ctx.NotApplicable("registry round-trip", "no news items on the board right now");
                return;
            }

            AutoAssignOwnershipRegistry.MarkModAutoAssigned(target);
            ctx.Assert(
                AutoAssignOwnershipRegistry.IsModAutoAssigned(target),
                "MarkModAutoAssigned makes IsModAutoAssigned return true"
            );
        }

        // After ResetForNewSave, no live story should appear as registered.
        // Restores the registry state in a finally block so the rest of the
        // session (globe tints, attention-sync) is not permanently disrupted.
        private static void ResetClearsRegistry(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("reset clears registry", "no LiveReportableManager");
                return;
            }

            var items = LiveReportableManager.Instance.GetNewsItems().ToList();
            AutoAssignOwnershipRegistry.ResetForNewSave();
            try
            {
                int wrong = 0;
                foreach (var ni in items)
                    if (ni?.Data != null && AutoAssignOwnershipRegistry.IsModAutoAssigned(ni))
                        wrong++;
                ctx.Assert(
                    wrong == 0,
                    "after ResetForNewSave no story is marked auto-assigned",
                    wrong + " of " + items.Count + " stories still show as auto-assigned"
                );
            }
            finally
            {
                // Re-register in-progress stories so globe tints remain consistent.
                foreach (var ni in items)
                {
                    if (ni?.Data == null)
                        continue;
                    if (AssignmentEvaluator.IsAnySlotInProgress(ni))
                    {
                        AutoAssignOwnershipRegistry.MarkModAutoAssigned(ni);
                        GlobeAttentionSync.PromoteFullySeen(ni);
                    }
                }
            }
        }

        // Patch_AfterLoad calls MarkModAutoAssigned for every in-progress story on load;
        // by the time the test runner fires, every such story must be registered.
        private static void InProgressStoriesAreInRegistry(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("in-progress stories in registry", "no LiveReportableManager");
                return;
            }

            int checkedCount = 0;
            int missedCount = 0;
            foreach (var ni in LiveReportableManager.Instance.GetNewsItems())
            {
                if (ni?.Data == null || !AssignmentEvaluator.IsAnySlotInProgress(ni))
                    continue;
                checkedCount++;
                if (!AutoAssignOwnershipRegistry.IsModAutoAssigned(ni))
                    missedCount++;
            }

            if (checkedCount == 0)
            {
                ctx.NotApplicable(
                    "in-progress stories in registry",
                    "no in-progress stories on the board right now"
                );
                return;
            }

            ctx.Assert(
                missedCount == 0,
                "every in-progress story is in the ownership registry",
                missedCount + " of " + checkedCount + " in-progress stories not registered"
            );
        }

        // GlobeAttentionSync.PromoteFullySeen is called on every MarkModAutoAssigned
        // path (both during live assignment and during load rehydration), so every
        // registered story must have UnseenState=FullSeen.
        private static void ModAssignedStoriesAreFullySeen(TestContext ctx)
        {
            if (LiveReportableManager.Instance == null)
            {
                ctx.Skip("assigned stories are FullSeen", "no LiveReportableManager");
                return;
            }

            int checkedCount = 0;
            int wrongCount = 0;
            foreach (var ni in LiveReportableManager.Instance.GetNewsItems())
            {
                if (ni?.Data == null || !AutoAssignOwnershipRegistry.IsModAutoAssigned(ni))
                    continue;
                checkedCount++;
                if (ni.UnseenState != UnseenState.FullSeen)
                    wrongCount++;
            }

            if (checkedCount == 0)
            {
                ctx.NotApplicable(
                    "assigned stories are FullSeen",
                    "no mod-auto-assigned stories on the board right now"
                );
                return;
            }

            ctx.Assert(
                wrongCount == 0,
                "every mod-assigned story has UnseenState=FullSeen",
                wrongCount + " of " + checkedCount + " mod-assigned stories not FullSeen"
            );
        }
    }
}
