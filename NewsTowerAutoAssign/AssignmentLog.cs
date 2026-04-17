using System;
using System.Collections.Generic;
using System.Linq;
using Reportables;
using Tower_Stats;

namespace NewsTowerAutoAssign
{
    // Centralised logging + per-story decision suppression used by the assignment
    // evaluator and the bribe automation. Keeps a single writer for every log line
    // the mod produces so prefixes stay consistent.
    internal static class AssignmentLog
    {
        // Deduplicate state-type decisions (kept risky, no reporter, etc.) per story.
        // Cleared whenever a real change happens for that story so messages re-log
        // when conditions actually change.
        private static readonly HashSet<string> _suppressedDecisions = new HashSet<string>();

        // When OnlyLogTests is on we gate every non-error channel so the log
        // contains nothing but the in-game test banners + PASS/FAIL/SKIP lines
        // (those go through AutoAssignPlugin.Log directly in TestContext /
        // InGameTestRunner and bypass this class on purpose). Errors are never
        // suppressed - if something genuinely breaks the user still sees it.
        private static bool Quiet => AutoAssignPlugin.OnlyLogTests?.Value ?? false;

        internal static void Info(string area, string message)
        {
            if (Quiet)
                return;
            AutoAssignPlugin.Log.LogInfo("[" + area + "] " + message);
        }

        internal static void Decision(string message)
        {
            if (Quiet)
                return;
            AutoAssignPlugin.Log.LogInfo("[DECISION] " + message);
        }

        internal static void Discard(string message)
        {
            if (Quiet)
                return;
            AutoAssignPlugin.Log.LogWarning("[DISCARD] " + message);
        }

        internal static void Warn(string area, string message)
        {
            if (Quiet)
                return;
            AutoAssignPlugin.Log.LogWarning("[" + area + "] " + message);
        }

        internal static void Error(string message) => AutoAssignPlugin.Log.LogError(message);

        internal static void Verbose(string area, string message)
        {
            if (Quiet)
                return;
            if (AutoAssignPlugin.VerboseLogs.Value)
                AutoAssignPlugin.Log.LogInfo("[VERBOSE:" + area + "] " + message);
        }

        // Emits a DECISION line the first time a given (story, key) pair is seen.
        // Call ClearSuppression when a real state change happens so the message
        // can re-log next time the condition recurs.
        internal static void DecisionOnce(NewsItem newsItem, string decisionKey, string message)
        {
            if (Quiet)
                return;
            if (!_suppressedDecisions.Add(StoryId(newsItem) + ":" + decisionKey))
                return;
            AutoAssignPlugin.Log.LogInfo("[DECISION] " + message);
        }

        internal static void ClearSuppression(NewsItem newsItem)
        {
            var prefix = StoryId(newsItem) + ":";
            _suppressedDecisions.RemoveWhere(k => k.StartsWith(prefix));
        }

        // Identity-based hash - stable for the object's lifetime, no Unity call needed.
        private static string StoryId(NewsItem newsItem) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(newsItem).ToString();

        // Strips the " (cloneN)" suffix Unity adds to prefab instances.
        internal static string StoryName(NewsItem newsItem)
        {
            var name = newsItem?.Data?.name ?? "Unknown story";
            var paren = name.LastIndexOf(" (");
            return paren > 0 ? name.Substring(0, paren) : name;
        }

        // Comma-separated, sorted, distinct list of every PlayerStatDataTag on
        // the story (all paths, all nodes). Used as the shared prefix for
        // every story-level decision log so the reader can see, at a glance,
        // what tags went into the decision without chasing verbose mode.
        internal static string StoryTagList(NewsItem newsItem)
        {
            if (newsItem?.Data == null)
                return "[]";
            var tags = newsItem
                .Data.DistinctStatTypes.OfType<PlayerStatDataTag>()
                .Where(t => t != null)
                .Select(t => t.name)
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            return tags.Length == 0 ? "[]" : "[" + string.Join(", ", tags) + "]";
        }

        // Compact snapshot of the current goal landscape, with coverage
        // annotated per-binary. Renders as:
        //   binary={RedHerring(covered), PartyInspiration(uncovered)} quantity={Tragic, Inspiring}
        // Appended to discard / kept decision lines so it's always obvious
        // WHICH goals the mod was weighing the story against.
        internal static string GoalSnapshot(
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            var quantity = quantityGoalTags == null || quantityGoalTags.Count == 0
                ? "none"
                : string.Join(
                    ", ",
                    quantityGoalTags
                        .Where(t => t != null)
                        .Select(t => t.name)
                        .OrderBy(s => s, StringComparer.Ordinal)
                );
            var binary = binaryGoalTags == null || binaryGoalTags.Count == 0
                ? "none"
                : string.Join(
                    ", ",
                    binaryGoalTags
                        .Where(t => t != null)
                        .OrderBy(t => t.name, StringComparer.Ordinal)
                        .Select(t =>
                            t.name
                            + (
                                inProgressTags != null && inProgressTags.Contains(t)
                                    ? "(covered)"
                                    : "(uncovered)"
                            )
                        )
                );
            return "binary={" + binary + "} quantity={" + quantity + "}";
        }
    }
}
