using BepInEx.Logging;

namespace NewsTowerAutoAssign.InGameTests
{
    // Colour-coding (per suite and per run) is uniform across the test layer:
    //   [OK]   white / bright (LogLevel.Message) - all passed, no skips
    //   [WARN] yellow          (LogLevel.Warning) - skips but no failures
    //   [FAIL] red             (LogLevel.Error)   - any failures
    internal class TestContext
    {
        private readonly string _suite;
        private int _passed,
            _failed,
            _skipped;

        private static ManualLogSource Log => AutoAssignPlugin.Log;

        internal TestContext(string suite) => _suite = suite;

        internal int Passed => _passed;
        internal int Failed => _failed;
        internal int Skipped => _skipped;

        internal void Assert(bool condition, string name, string failReason = "")
        {
            if (condition)
                Pass(name);
            else
                Fail(name, failReason);
        }

        internal void Pass(string name)
        {
            _passed++;
            Log.LogInfo("[PASS] " + _suite + " / " + name);
        }

        // LogError so failed assertions render red in the BepInEx console and
        // stand out when scanning LogOutput.log by level. Previously this used
        // LogWarning, which made real failures indistinguishable from the
        // yellow "some skips" signal at the summary line.
        internal void Fail(string name, string reason = "")
        {
            _failed++;
            Log.LogError(
                "[FAIL] "
                    + _suite
                    + " / "
                    + name
                    + (string.IsNullOrEmpty(reason) ? "" : ": " + reason)
            );
        }

        internal void Skip(string name, string reason = "")
        {
            _skipped++;
            Log.LogInfo(
                "[SKIP] "
                    + _suite
                    + " / "
                    + name
                    + (string.IsNullOrEmpty(reason) ? "" : ": " + reason)
            );
        }

        // "Not Applicable": the test has a precondition that depends on the
        // current SAVE rather than the test run's timing (e.g. no HiddenAgenda
        // hubs exist in this save). There is nothing to defer - the scenario
        // just isn't present - so we log at INFO and do NOT count it against
        // the summary. This keeps the master [RUN] line at [OK] when every
        // runnable assertion passes.
        internal void NotApplicable(string name, string reason = "")
        {
            Log.LogInfo(
                "[N/A]  "
                    + _suite
                    + " / "
                    + name
                    + (string.IsNullOrEmpty(reason) ? "" : ": " + reason)
            );
        }

        internal bool HasFailures => _failed > 0;

        internal void PrintSummary()
        {
            // Feed the run-level aggregator before emitting the per-suite line
            // so InGameTestRunner sees every suite regardless of call order.
            TestRunAggregator.Accumulate(_passed, _failed, _skipped);

            string badge;
            LogLevel level;
            if (_failed > 0)
            {
                badge = "[FAIL]";
                level = LogLevel.Error;
            }
            else if (_skipped > 0)
            {
                badge = "[WARN]";
                level = LogLevel.Warning;
            }
            else
            {
                badge = "[OK]";
                level = LogLevel.Message;
            }

            Log.Log(
                level,
                badge
                    + " [TESTS] "
                    + _suite
                    + ": "
                    + _passed
                    + " passed, "
                    + _failed
                    + " failed, "
                    + _skipped
                    + " skipped"
            );
        }
    }
}
