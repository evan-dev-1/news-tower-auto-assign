using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Reportables;
using Tower_Stats;

namespace NewsTowerAutoAssign
{
    // Centralised logging + per-story decision suppression used by the assignment
    // evaluator and the bribe automation. Keeps a single writer for every log line
    // the mod produces so prefixes stay consistent.
    //
    // Release-build logging policy
    // -----------------------------
    // Every helper except `Error` is marked [Conditional("DEBUG")]. In Release
    // builds the compiler removes the call AND the argument evaluation at every
    // callsite, so no string concatenation cost is paid and the player's log
    // stays clean. Only `Error` still fires - if something genuinely breaks we
    // want a stack trace in the player's BepInEx log so bug reports are useful.
    internal static class AssignmentLog
    {
        // Deduplicate state-type decisions (kept risky, no reporter, etc.) per story.
        // Cleared whenever a real change happens for that story so messages re-log
        // when conditions actually change.
        private static readonly HashSet<string> _suppressedDecisions = new HashSet<string>();

        [Conditional("DEBUG")]
        internal static void Info(string area, string message)
        {
            AutoAssignPlugin.Log.LogInfo("[" + area + "] " + message);
        }

        [Conditional("DEBUG")]
        internal static void Decision(string message)
        {
            AutoAssignPlugin.Log.LogInfo("[DECISION] " + message);
        }

        [Conditional("DEBUG")]
        internal static void Discard(string message)
        {
            AutoAssignPlugin.Log.LogWarning("[DISCARD] " + message);
        }

        [Conditional("DEBUG")]
        internal static void Warn(string area, string message)
        {
            AutoAssignPlugin.Log.LogWarning("[" + area + "] " + message);
        }

        // Errors are ALWAYS logged in every build - if something genuinely
        // breaks the player (and their bug report) need a trace.
        internal static void Error(string message) => AutoAssignPlugin.Log.LogError(message);

        [Conditional("DEBUG")]
        internal static void Verbose(string area, string message)
        {
#if DEBUG
            if (AutoAssignPlugin.VerboseLogs != null && AutoAssignPlugin.VerboseLogs.Value)
                AutoAssignPlugin.Log.LogInfo("[VERBOSE:" + area + "] " + message);
#endif
        }

        // Emits a DECISION line the first time a given (reportable, key) pair
        // is seen. Call ClearSuppression when a real state change happens so
        // the message can re-log next time the condition recurs.
        //
        // The parameter is typed as Reportable (the common base of NewsItem
        // and Ad) so both AssignmentEvaluator (news) and AdAutomation (ads)
        // can share the same per-board suppression set without colliding -
        // identity hashes are unique across both types.
        [Conditional("DEBUG")]
        internal static void DecisionOnce(Reportable reportable, string decisionKey, string message)
        {
            if (!_suppressedDecisions.Add(StoryId(reportable) + ":" + decisionKey))
                return;
            AutoAssignPlugin.Log.LogInfo("[DECISION] " + message);
        }

        internal static void ClearSuppression(Reportable reportable)
        {
            var prefix = StoryId(reportable) + ":";
            _suppressedDecisions.RemoveWhere(k => k.StartsWith(prefix));
        }

        // Called from Patch_LRMAwake when a new LiveReportableManager spins
        // up (i.e. a new save load begins). Keys are NewsItem identity hash
        // codes - recycled across different saves they would silently suppress
        // legitimate decisions for the new board. Also bounds long-session
        // memory growth if the player cycles through several saves without
        // restarting the game.
        internal static void ResetForNewSave()
        {
            _suppressedDecisions.Clear();
        }

        // Identity-based hash - stable for the object's lifetime, no Unity call needed.
        private static string StoryId(Reportable reportable) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(reportable).ToString();

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
            var quantity =
                quantityGoalTags == null || quantityGoalTags.Count == 0
                    ? "none"
                    : string.Join(
                        ", ",
                        quantityGoalTags
                            .Where(t => t != null)
                            .Select(t => t.name)
                            .OrderBy(s => s, StringComparer.Ordinal)
                    );
            var binary =
                binaryGoalTags == null || binaryGoalTags.Count == 0
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
