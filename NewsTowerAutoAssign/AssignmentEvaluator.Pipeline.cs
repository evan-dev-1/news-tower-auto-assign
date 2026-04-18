using System;
using System.Collections.Generic;
using System.Linq;
using _Game._Common;
using Assigner;
using Composable_Hierarchy;
using Employees;
using GameState;
using GlobalNews;
using Persons;
using Reportables;
using Risks;
using Skills;
using Tower_Stats;

namespace NewsTowerAutoAssign
{
    // Discard paths, path viability, slot assignment, and assignment logging.
    // Split from AssignmentEvaluator.cs to keep the entry/scan surface area
    // readable; this file is the bulk of the per-story pipeline.
    internal static partial class AssignmentEvaluator
    {
        // Emits the decision log line and removes the story from the live
        // manager. All four discard paths in AssignNewsItemCore funnel
        // through here so the log format stays identical regardless of
        // reason, and the removal can't accidentally be forgotten when a
        // new discard reason is added.
        private static void DiscardStory(NewsItem newsItem, string reasonLabel, string detail)
        {
            AssignmentLog.Discard(
                AssignmentLog.StoryName(newsItem)
                    + " "
                    + AssignmentLog.StoryTagList(newsItem)
                    + " → DISCARDED ("
                    + reasonLabel
                    + "): "
                    + detail
            );
            LiveReportableManager.Instance.RemoveReportable(newsItem);
        }

        // Formats the trailing "Goals: ..." clause shared by the risk /
        // weekend / availability discard lines. Kept as a helper so the three
        // logs can't drift apart in phrasing.
        private static string GoalSnapshotSuffix(
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        ) =>
            " Goals: "
            + AssignmentLog.GoalSnapshot(quantityGoalTags, binaryGoalTags, inProgressTags)
            + ".";

        // Returns true if at least one non-resolved node on the story has zero
        // viable paths (missing building OR no reporter with the required skill).
        // Takes the pre-cached story-file list from AssignNewsItemCore so we
        // don't walk the transform hierarchy twice per scan.
        private static bool HasDeadEndNode(List<NewsItemStoryFile> allStoryFiles)
        {
            var nodeViability = new Dictionary<NewsItemNode, bool>();
            foreach (var storyFile in allStoryFiles)
            {
                if (!NodeIsOpen(storyFile))
                    continue;
                bool pathViable = IsPathViable(storyFile.AssignSkill);
                if (!nodeViability.TryGetValue(storyFile.Node, out _))
                    nodeViability[storyFile.Node] = pathViable;
                else if (pathViable)
                    nodeViability[storyFile.Node] = true;
            }
            return nodeViability.Values.Any(viable => !viable);
        }

        // A node is "open" for dead-end analysis iff it is neither completed,
        // destroyed, locked, nor missing entirely. Everything else contributes
        // to whether at least one of its sibling paths is viable.
        private static bool NodeIsOpen(NewsItemStoryFile storyFile)
        {
            var state = storyFile.Node?.NodeState ?? NewsItemNodeState.Unlocked;
            if (
                state == NewsItemNodeState.Completed
                || state == NewsItemNodeState.Destroyed
                || state == NewsItemNodeState.Locked
            )
                return false;
            return storyFile.Node != null;
        }

        private static bool IsPathViable(SkillData skill) =>
            skill == null
            || (
                AssetUnlocker.IsUnlockedSafe(skill) && ReporterLookup.AnyReporterEverHasSkill(skill)
            );

        // True when a story file path is currently assignable - not completed, not
        // already running, building is built, and the roster actually has the skill.
        // Each rejection emits a verbose "skipping path" line so the log is
        // self-explanatory when stories sit with no assignments.
        private static bool PathIsAssignableNow(NewsItemStoryFile storyFile)
        {
            if (storyFile.IsCompleted)
            {
                LogPathSkip("already completed", storyFile.AssignSkill);
                return false;
            }
            if (GameReflection.IsSlotAlreadyRunning(storyFile))
            {
                LogPathSkip("progressDoneEvent active", storyFile.AssignSkill);
                return false;
            }
            return SkillIsAvailable(storyFile.AssignSkill);
        }

        // Skill-side gates shared between PathIsAssignableNow and any caller
        // that needs to know whether a given skill is reachable right now.
        private static bool SkillIsAvailable(SkillData skill)
        {
            if (skill == null)
                return true;
            if (!AssetUnlocker.IsUnlockedSafe(skill))
            {
                AssignmentLog.Verbose(
                    "PATH",
                    "  -> skipping path (building not built: " + skill.skillName + ")"
                );
                return false;
            }
            if (!ReporterLookup.AnyReporterEverHasSkill(skill))
            {
                AssignmentLog.Verbose(
                    "PATH",
                    "  -> skipping path (no reporter has skill: " + skill.skillName + ")"
                );
                return false;
            }
            return true;
        }

        private static void LogPathSkip(string reason, SkillData skill) =>
            AssignmentLog.Verbose(
                "PATH",
                "  -> skipping path (" + reason + ": " + (skill?.skillName ?? "any") + ")"
            );

        // NewsItemXorBranch (game "XOR Unlock") is a real either/or split: completing
        // one outbound branch freezes the others. NewsItemAndBranch is the opposite —
        // all inbound branches must complete. We only treat assignable slots as an
        // "ambiguous path" when two of them are alternatives under the same XOR
        // splitter; parallel prerequisites (converging AND) stay auto-assignable.
        private static NewsItemXorBranch GetXorSplitterForStoryNode(NewsItemNode node)
        {
            if (node == null)
                return null;
            foreach (var link in node.InLinks)
            {
                if (link?.FromNode == null)
                    continue;
                var xor = link.FromNode.GetComponentInChildren<NewsItemXorBranch>(true);
                if (xor != null)
                    return xor;
            }
            return null;
        }

        private static bool StoryNodeAnchoredOnLinkToNode(
            NewsItemNode storyNode,
            NewsItemNode linkToNode
        )
        {
            if (storyNode == null || linkToNode == null)
                return false;
            for (ComposableRuntimeComponent walk = storyNode; walk != null; walk = walk.Parent)
            {
                if (ReferenceEquals(walk, linkToNode))
                    return true;
            }
            return false;
        }

        private static int? GetXorOutLinkIndex(NewsItemXorBranch xor, NewsItemNode storyNode)
        {
            if (xor?.Node == null || storyNode == null)
                return null;
            int i = 0;
            foreach (var link in xor.Node.OutLinks)
            {
                var to = link?.ToNode;
                if (to != null && StoryNodeAnchoredOnLinkToNode(storyNode, to))
                    return i;
                i++;
            }
            return null;
        }

        private static bool AreXorAlternativeStoryFiles(NewsItemStoryFile a, NewsItemStoryFile b)
        {
            if (a == null || b == null || ReferenceEquals(a, b))
                return false;
            var xorA = GetXorSplitterForStoryNode(a.Node);
            if (xorA == null)
                return false;
            var xorB = GetXorSplitterForStoryNode(b.Node);
            if (!ReferenceEquals(xorA, xorB))
                return false;
            var ixA = GetXorOutLinkIndex(xorA, a.Node);
            var ixB = GetXorOutLinkIndex(xorB, b.Node);
            return ixA != null && ixB != null && ixA.Value != ixB.Value;
        }

        private static bool SlotsContainXorExclusivePair(IReadOnlyList<NewsItemStoryFile> slots)
        {
            for (int i = 0; i < slots.Count; i++)
            for (int j = i + 1; j < slots.Count; j++)
                if (AreXorAlternativeStoryFiles(slots[i], slots[j]))
                    return true;
            return false;
        }

        // Top-level per-slot flow: availability probe, pick, pre-flight, log,
        // commit. Each check that fails emits its own decision-once log and
        // returns; the commit is a single line.
        private static void TryAssignSingleSlot(
            NewsItem newsItem,
            NewsItemStoryFile storyFile,
            List<NewsItemStoryFile> storyFiles,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            var skill = storyFile.AssignSkill;
            float thresholdHours = AutoAssignPlugin.DiscardIfNoReporterForHours.Value;

            if (!HasAnyReporterSoon(newsItem, skill, thresholdHours))
                return;
            var employee = ReporterLookup.PickBestAvailable(skill);
            if (employee == null)
            {
                LogNoReporterAvailable(newsItem, skill);
                return;
            }

            // Visibility flip must happen before AssignTo (see
            // MarkStoryFileVisible for the full "black-bar bug" rationale).
            MarkStoryFileVisible(storyFile);

            if (!PassesPreFlight(storyFile, employee, skill, newsItem))
                return;

            LogAssignmentDecision(
                newsItem,
                storyFile,
                storyFiles,
                employee,
                quantityGoalTags,
                scoopGoalTags,
                binaryGoalTags,
                inProgressTags
            );
            AssignmentLog.ClearSuppression(newsItem);
            AutoAssignOwnershipRegistry.MarkModAutoAssigned(newsItem);
            employee.AssignableToReportable.AssignTo(storyFile);
            GlobeAttentionSync.PromoteFullySeen(newsItem);
        }

        // Soft availability gate: returns false (and logs once) when nobody
        // qualifies for this slot's skill within the configured lookahead.
        // "Story kept, will retry" - we don't discard here; that decision
        // was already made at the story level in TryDiscardForAvailability.
        private static bool HasAnyReporterSoon(
            NewsItem newsItem,
            SkillData skill,
            float thresholdHours
        )
        {
            if (ReporterLookup.AnyReporterAvailableSoon(skill, thresholdHours))
                return true;
            AssignmentLog.DecisionOnce(
                newsItem,
                "slot_skip_" + (skill?.skillName ?? "any"),
                AssignmentLog.StoryName(newsItem)
                    + " "
                    + AssignmentLog.StoryTagList(newsItem)
                    + " → WAIT (slot): no "
                    + (skill != null ? "'" + skill.skillName + "'" : "any-skill")
                    + " reporter free within "
                    + thresholdHours
                    + "h (story kept, will retry)."
            );
            return false;
        }

        // Emits the "all eligible reporters are busy right now" decision log
        // once. PickBestAvailable returning null is distinct from the "soon"
        // probe above - someone WILL be free in N hours but nobody is right
        // now (e.g. everyone mid-assignment).
        private static void LogNoReporterAvailable(NewsItem newsItem, SkillData skill)
        {
            AssignmentLog.DecisionOnce(
                newsItem,
                "no_reporter_" + (skill?.skillName ?? "any"),
                AssignmentLog.StoryName(newsItem)
                    + " "
                    + AssignmentLog.StoryTagList(newsItem)
                    + " → WAIT (no reporter): all "
                    + (skill != null ? "'" + skill.skillName + "'" : "eligible")
                    + " reporters busy right now (story kept, will retry)."
            );
        }

        // Mark the story file as visible before AssignTo. NewsItemStoryFile
        // implements INewsItemVisibleHandler.OnVisibilityChanged - the same
        // method NewsbookRoot calls when the newsbook page is viewed - which
        // flips the private `isVisible` flag that ICanAssignHandler.CanAssign
        // gates on. Without this the reporter appears assigned but
        // progressDoneEvent is never created (black-bar bug) because the
        // CanAssign check inside OnAssigned silently fails.
        private static void MarkStoryFileVisible(NewsItemStoryFile storyFile) =>
            storyFile.OnVisibilityChanged(true);

        // Runs every CanAssignHandler the story file publishes. Returns true
        // if they all accept the employee; otherwise logs the failure at the
        // appropriate level (verbose for expected sibling-lock, warn for
        // anything else) and returns false.
        private static bool PassesPreFlight(
            NewsItemStoryFile storyFile,
            Employee employee,
            SkillData skill,
            NewsItem newsItem
        )
        {
            if (storyFile.CanAssignHandlers.All(handler => handler.CanAssign(employee)))
                return true;
            // NodeState=Locked is expected - a sibling branch was chosen on a
            // mutually-exclusive node. Log at VERBOSE so it doesn't look like
            // a bug.
            if (storyFile.Node?.NodeState == NewsItemNodeState.Locked)
            {
                AssignmentLog.Verbose(
                    "ASSIGN",
                    "Branch locked (sibling chosen) ["
                        + (skill?.skillName ?? "any")
                        + "] for "
                        + AssignmentLog.StoryName(newsItem)
                        + "."
                );
                return false;
            }
            AssignmentLog.Warn(
                "ASSIGN",
                "  -> PRE-FLIGHT FAIL for "
                    + employee.name
                    + " ["
                    + (skill?.skillName ?? "any")
                    + "]"
                    + " | NodeState="
                    + storyFile.Node?.NodeState
                    + " IsCompleted="
                    + storyFile.IsCompleted
                    + " HasSkill="
                    + (skill == null || employee.SkillHandler.HasSkillAndIsAssigned(skill))
                    + " AvailableForGlobe="
                    + employee.IsAvailableForGlobeAssignment
            );
            return false;
        }

        // Builds a human-readable reason string for risk-kept / weekend-kept
        // decisions: lists which story tags pushed us into "match uncovered goal"
        // territory (by category), and which ones were ALREADY covered by in-progress
        // stories. The covered list is what the user cares about when they see the
        // "tag already covered but story started anyway" scenario - it lets them
        // confirm at a glance what the mod considered uncovered vs covered.
        private static string DescribeGoalMatch(
            NewsItem newsItem,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            var storyTags = newsItem
                .Data.DistinctStatTypes.OfType<PlayerStatDataTag>()
                .Where(tag => tag != null)
                .ToList();

            var quantityMatches = storyTags
                .Where(tag => quantityGoalTags.Contains(tag))
                .Select(tag => tag.name)
                .Distinct()
                .ToArray();
            var binaryUncoveredMatches = storyTags
                .Where(tag => binaryGoalTags.Contains(tag) && !inProgressTags.Contains(tag))
                .Select(tag => tag.name)
                .Distinct()
                .ToArray();
            var binaryCoveredMatches = storyTags
                .Where(tag => binaryGoalTags.Contains(tag) && inProgressTags.Contains(tag))
                .Select(tag => tag.name)
                .Distinct()
                .ToArray();

            var parts = new List<string>();
            if (binaryUncoveredMatches.Length > 0)
                parts.Add("uncovered binary: " + string.Join(", ", binaryUncoveredMatches));
            if (quantityMatches.Length > 0)
                parts.Add("scaling-reward quantity: " + string.Join(", ", quantityMatches));
            string matchSummary = parts.Count > 0 ? string.Join("; ", parts) : "no match";

            string suffix =
                binaryCoveredMatches.Length > 0
                    ? " (binary already covered by an in-progress story: "
                        + string.Join(", ", binaryCoveredMatches)
                        + ")"
                    : "";

            return "it matches active goals [" + matchSummary + "]" + suffix + ".";
        }

        private static (PathPriority priority, string[] labels) PathGoalDetail(
            NewsItemStoryFile storyFile,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            return AssignmentRules.GetPathGoalPriorityDetail(
                storyFile.BaseYieldDistinctStatTypes.OfType<PlayerStatDataTag>(),
                quantityGoalTags,
                scoopGoalTags,
                binaryGoalTags,
                inProgressTags,
                tag => storyFile.IsScoop(tag),
                tag => tag == null ? "?" : tag.name
            );
        }

        private static PathPriority GetPathGoalPriority(
            NewsItemStoryFile storyFile,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        ) =>
            PathGoalDetail(
                storyFile,
                quantityGoalTags,
                scoopGoalTags,
                binaryGoalTags,
                inProgressTags
            ).priority;

        private static string PathReasonFromPriority(
            PathPriority priority,
            string[] matchingYieldTagNames
        )
        {
            string tagSuffix =
                matchingYieldTagNames != null && matchingYieldTagNames.Length > 0
                    ? " [matched goal tags on this path: "
                        + string.Join(", ", matchingYieldTagNames)
                        + "]"
                    : "";
            switch (priority)
            {
                case PathPriority.UncoveredScoop:
                    return "it advances an uncovered scoop-required binary goal" + tagSuffix;
                case PathPriority.UncoveredBinary:
                    return "it advances an uncovered binary goal" + tagSuffix;
                case PathPriority.Quantity:
                    return "it advances a scaling-reward (quantity) goal - more tagged stories = more reward"
                        + tagSuffix;
                case PathPriority.CoveredBinary:
                    return "it matches a binary goal that an in-progress story already covers"
                        + tagSuffix;
                default:
                    return "no goal-matching path had higher priority";
            }
        }

        private static string OtherPathTagSuffix(string[] labels)
        {
            if (labels == null || labels.Length == 0)
                return "";
            return ", other path: " + string.Join(", ", labels);
        }

        // Emits the normal-mode ASSIGNED line. Format is the same uniform shape
        // as every other decision log:
        //   "<name> [story tags] → ASSIGNED: path=Skill yield=[...] reporter=Name - reason: ..."
        // Multi-path stories also include the runner-up's priority so it's
        // obvious why this path beat its siblings.
        private static void LogAssignmentDecision(
            NewsItem newsItem,
            NewsItemStoryFile chosen,
            List<NewsItemStoryFile> storyFiles,
            Employee employee,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            var skill = chosen.AssignSkill;
            var chosenDetail = PathGoalDetail(
                chosen,
                quantityGoalTags,
                scoopGoalTags,
                binaryGoalTags,
                inProgressTags
            );
            var yieldTagNames = chosen
                .BaseYieldDistinctStatTypes.OfType<PlayerStatDataTag>()
                .Where(tag => tag != null)
                .Select(tag => tag.name)
                .Distinct()
                .OrderBy(name => name, System.StringComparer.Ordinal)
                .ToArray();
            string yieldDesc =
                yieldTagNames.Length == 0 ? "[]" : "[" + string.Join(", ", yieldTagNames) + "]";

            string reasonSuffix = "";
            if (AutoAssignPlugin.ChaseGoalsEnabled.Value && storyFiles.Count > 1)
            {
                var bestAlternative = storyFiles
                    .Where(alternative => alternative != chosen)
                    .Select(alternative =>
                        PathGoalDetail(
                            alternative,
                            quantityGoalTags,
                            scoopGoalTags,
                            binaryGoalTags,
                            inProgressTags
                        )
                    )
                    .OrderByDescending(detail => detail.priority)
                    .First();
                reasonSuffix =
                    " (runner-up path priority = "
                    + bestAlternative.priority
                    + OtherPathTagSuffix(bestAlternative.labels)
                    + ")";
            }

            AssignmentLog.Decision(
                AssignmentLog.StoryName(newsItem)
                    + " "
                    + AssignmentLog.StoryTagList(newsItem)
                    + " → ASSIGNED: path="
                    + (skill?.skillName ?? "any")
                    + " yield="
                    + yieldDesc
                    + " - "
                    + PathReasonFromPriority(chosenDetail.priority, chosenDetail.labels)
                    + reasonSuffix
                    + "."
            );
        }

        private static void LogPathOrder(
            List<NewsItemStoryFile> storyFiles,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            AssignmentLog.Verbose(
                "PATH",
                "  path order: "
                    + string.Join(
                        ", ",
                        storyFiles.Select(storyFile =>
                        {
                            var priority = GetPathGoalPriority(
                                storyFile,
                                quantityGoalTags,
                                scoopGoalTags,
                                binaryGoalTags,
                                inProgressTags
                            );
                            var yieldTags = string.Join(
                                "|",
                                storyFile
                                    .BaseYieldDistinctStatTypes.OfType<PlayerStatDataTag>()
                                    .Select(tag => tag.name)
                            );
                            return (storyFile.AssignSkill?.skillName ?? "any")
                                + "["
                                + yieldTags
                                + "]"
                                + "(pri="
                                + priority
                                + ")";
                        })
                    )
            );
        }
    }
}
