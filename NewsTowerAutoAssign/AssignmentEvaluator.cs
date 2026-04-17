using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using _Game._Common;
using Assigner;
using Employees;
using GameState;
using GlobalNews;
using Persons;
using Reportables;
using Risks;
using Tower_Stats;

namespace NewsTowerAutoAssign
{
    // Core auto-assignment decision flow.
    //
    // Public entry points:
    //   TryAutoAssignAll    - full board scan; called from the idle-state patch
    //                         and after save-load.
    //   TryAssignNewsItem   - single story; called from AddReportable patch.
    //
    // Both gate on AutoAssignEnabled so the user's toggle is honoured; both also
    // use the shared `_isAssigning` reentrancy flag because AssignTo can itself
    // trigger additional patches.
    internal static class AssignmentEvaluator
    {
        private static bool _isAssigning;

        // Used only for pre-flight diagnostics - we never write to this field.
        // Non-null value means this slot is already running a job (restored from
        // save or mid-flight); a second assign would ghost. The game has no
        // public accessor for this - NewsbookPageDisplayer and the UI query it
        // via internal code paths only - so reflection is the only option.
        private static readonly FieldInfo _progressDoneEventField =
            typeof(NewsItemStoryFile).GetField(
                "progressDoneEvent",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

        internal static bool ProgressDoneEventFieldAvailable => _progressDoneEventField != null;

        internal static void TryAutoAssignAll()
        {
            if (_isAssigning)
                return;
            if (!AutoAssignPlugin.AutoAssignEnabled.Value)
                return;
            if (LiveReportableManager.Instance == null)
                return;

            _isAssigning = true;
            try
            {
                var (quantityGoalTags, scoopGoalTags, binaryGoalTags, inProgressTags) =
                    LoadGoalContext();
                foreach (var newsItem in LiveReportableManager.Instance.GetNewsItems().ToList())
                {
                    if (newsItem?.Data == null)
                        continue;
                    // Pay any bribe nodes that unlocked since the last scan
                    // (e.g. step 2 of a multi-step story that arrived before the weekend).
                    BribeAutomation.TryPayBribes(newsItem);
                    AssignNewsItemCore(
                        newsItem,
                        quantityGoalTags,
                        scoopGoalTags,
                        binaryGoalTags,
                        inProgressTags
                    );
                    // Refresh the in-progress tag snapshot after every item so later
                    // stories in the same scan see binaries we just started covering.
                    // Without this, two fresh stories carrying the same binary goal
                    // (e.g. Red Herring) both read it as uncovered and double-dip on
                    // the same goal - spending two reporter-weeks on one check mark.
                    if (AutoAssignPlugin.ChaseGoalsEnabled.Value)
                        inProgressTags = ReporterLookup.GetInProgressTags();
                }
            }
            catch (Exception e)
            {
                AssignmentLog.Error("TryAutoAssignAll: " + e);
            }
            finally
            {
                _isAssigning = false;
            }
        }

        internal static void TryAssignNewsItem(NewsItem newsItem)
        {
            if (_isAssigning)
                return;
            // Honour the user's Enabled toggle - without this, installing the mod
            // is effectively irreversible because AddReportable fires on every
            // story and would keep auto-assigning regardless of the flag.
            if (!AutoAssignPlugin.AutoAssignEnabled.Value)
                return;

            _isAssigning = true;
            try
            {
                var (quantityGoalTags, scoopGoalTags, binaryGoalTags, inProgressTags) =
                    LoadGoalContext();
                AssignNewsItemCore(
                    newsItem,
                    quantityGoalTags,
                    scoopGoalTags,
                    binaryGoalTags,
                    inProgressTags
                );
            }
            catch (Exception e)
            {
                AssignmentLog.Error("TryAssignNewsItem: " + e);
            }
            finally
            {
                _isAssigning = false;
            }
        }

        // Loads the current goal tag snapshot, or empty sets if ChaseGoals is off.
        // Kept here so both entry points build identical snapshots.
        private static (
            HashSet<PlayerStatDataTag> quantity,
            HashSet<PlayerStatDataTag> scoop,
            HashSet<PlayerStatDataTag> binary,
            HashSet<PlayerStatDataTag> inProgress
        ) LoadGoalContext()
        {
            var empty = new HashSet<PlayerStatDataTag>();
            if (!AutoAssignPlugin.ChaseGoalsEnabled.Value)
            {
                GoalChaseSnapshotLog.MaybeLog(false, empty, empty, empty);
                return (empty, empty, empty, empty);
            }

            var (quantity, scoop, binary) = ReporterLookup.GetCurrentGoalTagSets();
            GoalChaseSnapshotLog.MaybeLog(true, quantity, scoop, binary);
            var inProgress = ReporterLookup.GetInProgressTags();
            return (quantity, scoop, binary, inProgress);
        }

        // Inner logic - never touches _isAssigning, always called under the flag.
        private static void AssignNewsItemCore(
            NewsItem newsItem,
            HashSet<PlayerStatDataTag> quantityGoalTags,
            HashSet<PlayerStatDataTag> scoopGoalTags,
            HashSet<PlayerStatDataTag> binaryGoalTags,
            HashSet<PlayerStatDataTag> inProgressTags
        )
        {
            // --- Early exit: tutorial / early-game safety guard ---
            // Below the configured reporter threshold we cannot make reliable
            // skill/completability judgements. Skip ALL auto-assign and discard
            // logic - let the player handle everything manually until the roster grows.
            int globetrotterCount = ReporterLookup.CountPlayableGlobetrotters();
            if (globetrotterCount < AutoAssignPlugin.MinReportersToActivate.Value)
            {
                AssignmentLog.Verbose(
                    "ASSIGN",
                    "Skipped "
                        + AssignmentLog.StoryName(newsItem)
                        + " because only "
                        + globetrotterCount
                        + " reporter(s), need "
                        + AutoAssignPlugin.MinReportersToActivate.Value
                        + " for automation."
                );
                return;
            }

            // A story is "invested" if any step is completed or currently running.
            // Computed early so the risk phase can skip in-progress stories.
            bool alreadyInvested = newsItem
                .GetComponentsInChildren<NewsItemStoryFile>(true)
                .Any(sf => sf.IsCompleted || sf.Assignee != null);

            // Goals are loaded when either tag set is non-empty; treat empty as "unknown".
            // For binary goals, only count them as "active" if not already covered by an
            // in-progress story - covering the same binary goal twice is wasteful.
            // Quantity goals always count regardless of coverage (more is always useful).
            bool goalsLoaded = quantityGoalTags.Count > 0 || binaryGoalTags.Count > 0;
            bool storyMatchesGoal =
                goalsLoaded
                && AssignmentRules.StoryMatchesUncoveredGoal(
                    newsItem.Data.DistinctStatTypes.OfType<PlayerStatDataTag>(),
                    quantityGoalTags,
                    binaryGoalTags,
                    inProgressTags
                );

            bool hasRisk = newsItem.GetComponentsInChildren<INewsItemRisk>(true).Any();

            // --- Phase 1a: Risk check ---
            if (
                DiscardPredicates.ShouldDiscardForRisk(
                    AutoAssignPlugin.AvoidRisksEnabled.Value,
                    alreadyInvested,
                    goalsLoaded,
                    hasRisk,
                    storyMatchesGoal
                )
            )
            {
                var riskTypes = string.Join(
                    ", ",
                    newsItem
                        .GetComponentsInChildren<INewsItemRisk>(true)
                        .Select(r => r.GetType().Name)
                        .Distinct()
                );
                AssignmentLog.Discard(
                    AssignmentLog.StoryName(newsItem)
                        + " "
                        + AssignmentLog.StoryTagList(newsItem)
                        + " → DISCARDED (risk): risky ("
                        + riskTypes
                        + ") and no story tag matches an uncovered goal. Goals: "
                        + AssignmentLog.GoalSnapshot(
                            quantityGoalTags,
                            binaryGoalTags,
                            inProgressTags
                        )
                        + "."
                );
                LiveReportableManager.Instance.RemoveReportable(newsItem);
                return;
            }

            // If we're keeping a risky story because it matches an active goal,
            // log it once so the reasoning is visible. Only fires when the story
            // was actually eligible for risk-discard (fresh, goals loaded) -
            // invested stories are always kept and don't need this reasoning.
            if (
                AutoAssignPlugin.AvoidRisksEnabled.Value
                && hasRisk
                && storyMatchesGoal
                && !alreadyInvested
            )
            {
                AssignmentLog.DecisionOnce(
                    newsItem,
                    "risk_kept",
                    AssignmentLog.StoryName(newsItem)
                        + " "
                        + AssignmentLog.StoryTagList(newsItem)
                        + " → KEPT (risk): risky but "
                        + DescribeGoalMatch(
                            newsItem,
                            quantityGoalTags,
                            binaryGoalTags,
                            inProgressTags
                        )
                );
            }

            // --- Phase 1b: Completability check ---
            // Scan ALL story files on the news item (including locked future steps) and
            // verify that every node has at least one viable path. Viable = building built
            // AND at least one reporter has the required skill.
            // Nodes whose story files are all Completed/Destroyed/Locked (sibling chosen)
            // are skipped - they're already resolved.
            if (HasDeadEndNode(newsItem))
            {
                AssignmentLog.Discard(
                    AssignmentLog.StoryName(newsItem)
                        + " "
                        + AssignmentLog.StoryTagList(newsItem)
                        + " → DISCARDED (dead-end): at least one node has no viable path "
                        + "(missing building or no reporter with required skill)."
                );
                LiveReportableManager.Instance.RemoveReportable(newsItem);
                return;
            }

            // --- Phase 1c: Weekend deadline check ---
            // The weekly print window opens on Sunday. A fresh story arriving on
            // Saturday or Sunday has no realistic chance of completing before the
            // deadline - discard and let it reappear next week. Invested stories
            // are never touched here; we already paid time on them. Goal-matching
            // stories are kept regardless: composed-quest tags (Red Herring and
            // other faction injections) spawn rarely, and losing one to a weekend
            // timer is far worse than letting the player finish it manually.
            bool isWeekend = TowerTimes.IsWeekend(TowerTime.CurrentTime);
            if (
                DiscardPredicates.ShouldDiscardForWeekend(
                    AutoAssignPlugin.DiscardFreshStoriesOnWeekend.Value,
                    alreadyInvested,
                    isWeekend,
                    storyMatchesGoal
                )
            )
            {
                AssignmentLog.Discard(
                    AssignmentLog.StoryName(newsItem)
                        + " "
                        + AssignmentLog.StoryTagList(newsItem)
                        + " → DISCARDED (weekend): arrived "
                        + TowerTime.CurrentTime.Day
                        + ", fresh, and no story tag matches an uncovered goal. Goals: "
                        + AssignmentLog.GoalSnapshot(
                            quantityGoalTags,
                            binaryGoalTags,
                            inProgressTags
                        )
                        + "."
                );
                LiveReportableManager.Instance.RemoveReportable(newsItem);
                return;
            }

            // Mirror the risk-kept message: when a fresh weekend story is spared
            // because it matches an active goal, say so once. Makes it obvious in
            // the log which faction / quest tags are protecting the story from
            // the weekend timer.
            if (
                AutoAssignPlugin.DiscardFreshStoriesOnWeekend.Value
                && isWeekend
                && storyMatchesGoal
                && !alreadyInvested
            )
            {
                AssignmentLog.DecisionOnce(
                    newsItem,
                    "weekend_kept",
                    AssignmentLog.StoryName(newsItem)
                        + " "
                        + AssignmentLog.StoryTagList(newsItem)
                        + " → KEPT (weekend): arrived "
                        + TowerTime.CurrentTime.Day
                        + " but "
                        + DescribeGoalMatch(
                            newsItem,
                            quantityGoalTags,
                            binaryGoalTags,
                            inProgressTags
                        )
                );
            }

            // --- Slot collection ---
            var storyFiles = new List<NewsItemStoryFile>();
            newsItem.GetUnlockedAndAssignableStoryFiles(storyFiles);

            if (storyFiles.Count == 0)
                return;

            AssignmentLog.Verbose(
                "SLOTS",
                AssignmentLog.StoryName(newsItem) + ": " + storyFiles.Count + " open slot(s)"
            );

            // --- Phase 2: Filter out paths that cannot be assigned right now ---
            storyFiles = storyFiles.Where(sf => PathIsAssignableNow(sf)).ToList();
            if (storyFiles.Count == 0)
                return;

            // --- Goal-driven path preference ---
            if (AutoAssignPlugin.ChaseGoalsEnabled.Value)
            {
                storyFiles = storyFiles
                    .OrderByDescending(sf =>
                        GetPathGoalPriority(
                            sf,
                            quantityGoalTags,
                            scoopGoalTags,
                            binaryGoalTags,
                            inProgressTags
                        )
                    )
                    .ToList();
                LogPathOrder(
                    storyFiles,
                    quantityGoalTags,
                    scoopGoalTags,
                    binaryGoalTags,
                    inProgressTags
                );
            }

            // --- Phase 3: Pre-loop reporter availability check ---
            // Discard only if no reporter will be free soon for ANY visible slot AND
            // no work has been invested yet AND the story doesn't match an active goal.
            // Goal-matching stories are always kept regardless of reporter availability —
            // a goal tag is more valuable than the time cost of waiting 12+ hours.
            float thresholdHours = AutoAssignPlugin.DiscardIfNoReporterForHours.Value;
            if (
                DiscardPredicates.ShouldDiscardForAvailability(
                    alreadyInvested,
                    storyMatchesGoal,
                    thresholdHours,
                    anyReporterSoon: storyFiles.Any(sf =>
                        ReporterLookup.AnyReporterAvailableSoon(sf.AssignSkill, thresholdHours)
                    )
                )
            )
            {
                AssignmentLog.Discard(
                    AssignmentLog.StoryName(newsItem)
                        + " "
                        + AssignmentLog.StoryTagList(newsItem)
                        + " → DISCARDED (availability): no reporter free within "
                        + thresholdHours
                        + "h for any slot, fresh, and no story tag matches an uncovered goal. Goals: "
                        + AssignmentLog.GoalSnapshot(
                            quantityGoalTags,
                            binaryGoalTags,
                            inProgressTags
                        )
                        + "."
                );
                LiveReportableManager.Instance.RemoveReportable(newsItem);
                return;
            }

            // --- Assignment loop ---
            foreach (var storyFile in storyFiles)
                TryAssignSingleSlot(
                    newsItem,
                    storyFile,
                    storyFiles,
                    quantityGoalTags,
                    scoopGoalTags,
                    binaryGoalTags,
                    inProgressTags
                );
        }

        // Returns true if at least one non-resolved node on the story has zero
        // viable paths (missing building OR no reporter with the required skill).
        private static bool HasDeadEndNode(NewsItem newsItem)
        {
            var allSf = newsItem.GetComponentsInChildren<NewsItemStoryFile>(true);
            var nodeViability = new Dictionary<NewsItemNode, bool>();
            foreach (var sf in allSf)
            {
                var state = sf.Node?.NodeState ?? NewsItemNodeState.Unlocked;
                if (
                    state == NewsItemNodeState.Completed
                    || state == NewsItemNodeState.Destroyed
                    || state == NewsItemNodeState.Locked
                )
                    continue;
                if (sf.Node == null)
                    continue;

                var skill = sf.AssignSkill;
                bool pathViable =
                    skill == null
                    || (
                        AssetUnlocker.IsUnlockedSafe(skill)
                        && ReporterLookup.AnyReporterEverHasSkill(skill)
                    );

                bool existing;
                if (!nodeViability.TryGetValue(sf.Node, out existing))
                    nodeViability[sf.Node] = pathViable;
                else if (pathViable)
                    nodeViability[sf.Node] = true;
            }
            return nodeViability.Values.Any(v => !v);
        }

        // True when a story file path is currently assignable - not completed, not
        // already running, building is built, and the roster actually has the skill.
        private static bool PathIsAssignableNow(NewsItemStoryFile sf)
        {
            if (sf.IsCompleted)
            {
                AssignmentLog.Verbose(
                    "PATH",
                    "  -> skipping path (already completed: "
                        + (sf.AssignSkill?.skillName ?? "any")
                        + ")"
                );
                return false;
            }
            if (_progressDoneEventField != null && _progressDoneEventField.GetValue(sf) != null)
            {
                AssignmentLog.Verbose(
                    "PATH",
                    "  -> skipping path (progressDoneEvent active: "
                        + (sf.AssignSkill?.skillName ?? "any")
                        + ")"
                );
                return false;
            }
            var skill = sf.AssignSkill;
            if (skill != null)
            {
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
            }
            return true;
        }

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

            if (!ReporterLookup.AnyReporterAvailableSoon(skill, thresholdHours))
            {
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
                return;
            }

            var employee = Employee
                .Employees.Where(e =>
                    e.IsAvailableForGlobeAssignment
                    && e.AssignableToReportable.Assignment == null
                    && (skill == null || e.SkillHandler.HasSkillAndIsAssigned(skill))
                    && e.JobHandler?.JobData?.hideFromDrawer == false
                )
                .OrderByDescending(e => ReporterLookup.GetSkillLevel(e, skill))
                .FirstOrDefault();

            if (employee == null)
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
                return;
            }

            // Mark the story file as visible before AssignTo. NewsItemStoryFile
            // implements INewsItemVisibleHandler.OnVisibilityChanged - the same
            // method NewsbookRoot calls when the newsbook page is viewed - which
            // flips the private `isVisible` flag that ICanAssignHandler.CanAssign
            // gates on. Without this the reporter appears assigned but
            // progressDoneEvent is never created (black-bar bug) because the
            // CanAssign check inside OnAssigned silently fails.
            storyFile.OnVisibilityChanged(true);

            // Pre-flight: verify CanAssign will pass so we log exactly what's wrong
            // rather than silently ghosting the assignment.
            bool canAssign = storyFile.CanAssignHandlers.All(h => h.CanAssign(employee));
            if (!canAssign)
            {
                // NodeState=Locked is expected - a sibling branch was chosen on a
                // mutually-exclusive node. Log at VERBOSE so it doesn't look like a bug.
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
                }
                else
                {
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
                }
                return;
            }

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
            employee.AssignableToReportable.AssignTo(storyFile);
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
                .Where(t => t != null)
                .ToList();

            var quantityMatches = storyTags
                .Where(t => quantityGoalTags.Contains(t))
                .Select(t => t.name)
                .Distinct()
                .ToArray();
            var binaryUncoveredMatches = storyTags
                .Where(t => binaryGoalTags.Contains(t) && !inProgressTags.Contains(t))
                .Select(t => t.name)
                .Distinct()
                .ToArray();
            var binaryCoveredMatches = storyTags
                .Where(t => binaryGoalTags.Contains(t) && inProgressTags.Contains(t))
                .Select(t => t.name)
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

        private static (int priority, string[] labels) PathGoalDetail(
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

        private static int GetPathGoalPriority(
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

        private static string PathReasonFromPriority(int priority, string[] matchingYieldTagNames)
        {
            string tagSuffix =
                matchingYieldTagNames != null && matchingYieldTagNames.Length > 0
                    ? " [matched goal tags on this path: "
                        + string.Join(", ", matchingYieldTagNames)
                        + "]"
                    : "";
            if (priority == 3)
                return "it advances an uncovered scoop-required binary goal" + tagSuffix;
            if (priority == 2)
                return "it advances an uncovered binary goal" + tagSuffix;
            if (priority == 1)
                return "it advances a scaling-reward (quantity) goal - more tagged stories = more reward"
                    + tagSuffix;
            if (priority == 0)
                return "it matches a binary goal that an in-progress story already covers"
                    + tagSuffix;
            return "no goal-matching path had higher priority";
        }

        private static string OtherPathTagSuffix(string[] labels)
        {
            if (labels == null || labels.Length == 0)
                return "";
            return ", other path: " + string.Join(", ", labels);
        }

        // Emits the normal-mode ASSIGNED line. Format is the same uniform shape
        // as every other decision log:
        //   "<name> [story tags] → ASSIGNED: path=Skill yield=[...] reporter=Name — reason: ..."
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
                .Where(t => t != null)
                .Select(t => t.name)
                .Distinct()
                .OrderBy(s => s, System.StringComparer.Ordinal)
                .ToArray();
            string yieldDesc =
                yieldTagNames.Length == 0 ? "[]" : "[" + string.Join(", ", yieldTagNames) + "]";

            string reasonSuffix = "";
            if (AutoAssignPlugin.ChaseGoalsEnabled.Value && storyFiles.Count > 1)
            {
                var bestAlternative = storyFiles
                    .Where(sf => sf != chosen)
                    .Select(sf =>
                        PathGoalDetail(
                            sf,
                            quantityGoalTags,
                            scoopGoalTags,
                            binaryGoalTags,
                            inProgressTags
                        )
                    )
                    .OrderByDescending(x => x.priority)
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
                    + " — "
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
                        storyFiles.Select(sf =>
                        {
                            var pri = GetPathGoalPriority(
                                sf,
                                quantityGoalTags,
                                scoopGoalTags,
                                binaryGoalTags,
                                inProgressTags
                            );
                            var yieldTags = string.Join(
                                "|",
                                sf.BaseYieldDistinctStatTypes.OfType<PlayerStatDataTag>()
                                    .Select(t => t.name)
                            );
                            return (sf.AssignSkill?.skillName ?? "any")
                                + "["
                                + yieldTags
                                + "]"
                                + "(pri="
                                + pri
                                + ")";
                        })
                    )
            );
        }
    }
}
