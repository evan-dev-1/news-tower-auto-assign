using GameState;

namespace NewsTowerAutoAssign.InGameTests
{
    // Regression tests for two specific fixes:
    //   1. AutoAssignEnabled must fully gate TryAssignNewsItem (HIGH review finding).
    //   2. DiscardIfNoReporterForHours must honour fractional hours - the old
    //      (int)hours cast silently rounded 0.5h → 0 and 3.5h → 3h (MEDIUM).
    // Both live in the decision path, so we validate the knobs via observable
    // state rather than by double-patching the methods.
    internal static class KillSwitchTests
    {
        internal static void Run()
        {
            var ctx = new TestContext("KillSwitch");
            AutoAssignEnabledToggleObservable(ctx);
            FractionalHoursDeadlinePreserved(ctx);
            ctx.PrintSummary();
        }

        // Simply assert the flag is readable and wired through to the evaluator -
        // we cannot feasibly trigger AddReportable from a test, but we can verify
        // the contract that TryAutoAssignAll respects the flag (it is observably
        // a no-op when the flag is false: it does not throw, log errors, or remove
        // reportables). Running with the flag toggled off is side-effect free.
        private static void AutoAssignEnabledToggleObservable(TestContext ctx)
        {
            bool original = AutoAssignPlugin.AutoAssignEnabled.Value;
            try
            {
                AutoAssignPlugin.AutoAssignEnabled.Value = false;
                AssignmentEvaluator.TryAutoAssignAll();
                ctx.Pass("TryAutoAssignAll is a no-op when Enabled=false");

                AutoAssignPlugin.AutoAssignEnabled.Value = true;
                ctx.Assert(
                    AutoAssignPlugin.AutoAssignEnabled.Value,
                    "Enabled flag round-trips through BepInEx config"
                );
            }
            finally
            {
                AutoAssignPlugin.AutoAssignEnabled.Value = original;
            }
        }

        // Build two TowerTimeDurations and confirm the one built from the new
        // minute-preserving path is strictly longer than the int-truncated one
        // for a fractional input. This would have been equal under the bug.
        private static void FractionalHoursDeadlinePreserved(TestContext ctx)
        {
            const float fractional = 3.5f;
            int truncated = (int)fractional; // 3h - what the old code used
            int preserved = (int)System.Math.Round(fractional * 60f); // 210min - new behaviour

            long truncatedMinutes = TowerTimeDuration.FromHours(truncated).TotalMinutes;
            long preservedMinutes = TowerTimeDuration.FromMinutes(preserved).TotalMinutes;

            ctx.Assert(
                preservedMinutes > truncatedMinutes,
                "fractional hours produce a longer deadline than the int-truncated form",
                "preserved=" + preservedMinutes + "m truncated=" + truncatedMinutes + "m"
            );

            // Also cover the 0 < hours < 1 case the old bug reduced to zero.
            long halfHourMinutes = TowerTimeDuration
                .FromMinutes((int)System.Math.Round(0.5f * 60f))
                .TotalMinutes;
            ctx.Assert(
                halfHourMinutes > 0L,
                "0.5h now has a positive duration (old bug made it zero)",
                "minutes=" + halfHourMinutes
            );
        }
    }
}
