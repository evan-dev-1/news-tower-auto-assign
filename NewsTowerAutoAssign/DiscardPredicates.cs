namespace NewsTowerAutoAssign
{
    // Pure boolean predicates for the three story-discard decisions.
    // No game type dependencies - can be compiled and tested with NUnit
    // without any game DLL references.
    internal static class DiscardPredicates
    {
        // True when a risky story should be discarded.
        // All five conditions must hold: the feature is on, goals are loaded so we can
        // judge whether the risk is worth it, the story hasn't been started yet (never
        // discard invested work), it actually has a risk component, and none of its tags
        // match an active uncovered goal.
        internal static bool ShouldDiscardForRisk(
            bool featureEnabled,
            bool isInvested,
            bool goalsLoaded,
            bool hasRisk,
            bool matchesUncoveredGoal
        )
        {
            return featureEnabled && goalsLoaded && !isInvested && hasRisk && !matchesUncoveredGoal;
        }

        // True when a fresh weekend story should be discarded.
        // A story that arrives on Saturday/Sunday with nothing started can't finish
        // before the print deadline - let it reappear next week. Goal-matching
        // stories are exempt because faction / composed-quest tags (e.g. Red Herring)
        // are injected rarely and are far more valuable than the timing cost; even
        // partial progress on the current week is worth keeping.
        internal static bool ShouldDiscardForWeekend(
            bool featureEnabled,
            bool isInvested,
            bool isWeekend,
            bool matchesUncoveredGoal
        )
        {
            return featureEnabled && !isInvested && isWeekend && !matchesUncoveredGoal;
        }

        // True when a story with no reporter coverage should be discarded.
        // Only discards if: story hasn't been started, it offers no goal value
        // (goal-matching stories are always worth the wait), the feature threshold is
        // positive, and no reporter with the right skill will be free in time.
        internal static bool ShouldDiscardForAvailability(
            bool isInvested,
            bool matchesGoal,
            float thresholdHours,
            bool anyReporterSoon
        )
        {
            return !isInvested && !matchesGoal && thresholdHours > 0f && !anyReporterSoon;
        }
    }
}
