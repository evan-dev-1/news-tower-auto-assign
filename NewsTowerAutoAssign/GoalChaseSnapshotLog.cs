using System.Collections.Generic;
using System.Linq;
using Tower_Stats;

namespace NewsTowerAutoAssign
{
    // Emits a single [GOALS] Info line when the in-game period changes (new week /
    // month per TowerTime) or when quest-derived tag sets change, so players always
    // see what the mod is chasing without enabling VerboseLogs.
    internal static class GoalChaseSnapshotLog
    {
        private static int _lastPeriodLogged = int.MinValue;
        private static string _lastFingerprint = "";

        internal static void MaybeLog(
            bool chaseGoalsEnabled,
            HashSet<PlayerStatDataTag> quantity,
            HashSet<PlayerStatDataTag> scoop,
            HashSet<PlayerStatDataTag> binary
        )
        {
            if (TowerTime.Instance == null)
                return;

            int period = TowerTime.ZeroBasedPeriodNumber;
            string fp = BuildFingerprint(chaseGoalsEnabled, quantity, scoop, binary);
            bool periodChanged = period != _lastPeriodLogged;
            bool goalsChanged = fp != _lastFingerprint;
            if (!periodChanged && !goalsChanged)
                return;

            _lastPeriodLogged = period;
            _lastFingerprint = fp;

            string date = TowerTime.CurrentTime.ToString();
            if (!chaseGoalsEnabled)
            {
                AssignmentLog.Info(
                    "GOALS",
                    "Period "
                        + period
                        + " ("
                        + date
                        + "): ChaseGoals is off — no goal-based prioritization."
                );
                return;
            }

            string q = FormatTagNames(quantity);
            string s = FormatTagNames(scoop);
            string b = FormatTagNames(binary);
            AssignmentLog.Info(
                "GOALS",
                "Period "
                    + period
                    + " ("
                    + date
                    + "): chasing quantity (scaling reward) ["
                    + (string.IsNullOrEmpty(q) ? "none" : q)
                    + "]; scoop-required ["
                    + (string.IsNullOrEmpty(s) ? "none" : s)
                    + "]; binary (threshold) ["
                    + (string.IsNullOrEmpty(b) ? "none" : b)
                    + "]."
            );
        }

        private static string BuildFingerprint(
            bool chaseGoalsEnabled,
            HashSet<PlayerStatDataTag> quantity,
            HashSet<PlayerStatDataTag> scoop,
            HashSet<PlayerStatDataTag> binary
        )
        {
            return chaseGoalsEnabled
                + "|"
                + FormatTagNames(quantity)
                + "|"
                + FormatTagNames(scoop)
                + "|"
                + FormatTagNames(binary);
        }

        private static string FormatTagNames(HashSet<PlayerStatDataTag> tags)
        {
            if (tags == null || tags.Count == 0)
                return "";
            return string.Join(
                ", ",
                tags.Where(t => t != null).Select(t => t.name).Distinct().OrderBy(n => n)
            );
        }
    }
}
