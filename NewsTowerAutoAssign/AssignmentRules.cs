using System;
using System.Collections.Generic;
using System.Linq;

namespace NewsTowerAutoAssign
{
    // Pure rules used by the assignment evaluator. Generic on the tag type so the
    // NUnit test project can exercise the logic without referencing game DLLs -
    // tests use `string` as TTag, runtime callers use PlayerStatDataTag.
    internal static class AssignmentRules
    {
        // True iff any of the story's stat tags matches an active goal that
        // still deserves chasing right now:
        //   * quantity goal (reward scales per-copy, so always "uncovered")
        //   * binary goal not yet covered by any in-progress story
        internal static bool StoryMatchesUncoveredGoal<TTag>(
            IEnumerable<TTag> storyTags,
            HashSet<TTag> quantityGoalTags,
            HashSet<TTag> binaryGoalTags,
            HashSet<TTag> inProgressTags
        )
        {
            return storyTags.Any(t =>
                quantityGoalTags.Contains(t)
                || (binaryGoalTags.Contains(t) && !inProgressTags.Contains(t))
            );
        }

        // Priority score for a story-file path given the current goal landscape.
        //   3 = binary goal uncovered + scoop-required + path.IsScoop (highest)
        //   2 = binary goal uncovered (regular threshold we haven't claimed yet)
        //   1 = quantity goal (scaling reward; still worth chasing even if
        //       another in-progress story covers it)
        //   0 = binary goal already covered (stacking wastes work)
        //  -1 = no match
        //
        // Scoop priority requires the tag to still be uncovered - once we've
        // already assigned a story that matches the scoop tag, the district
        // goal is on the rails and further scoop-matching paths just duplicate
        // effort.
        //
        // isScoop is a delegate so this class doesn't need a reference to
        // NewsItemStoryFile; callers bind it to storyFile.IsScoop(tag) at runtime.
        internal static int GetPathGoalPriority<TTag>(
            IEnumerable<TTag> yieldTags,
            HashSet<TTag> quantityGoalTags,
            HashSet<TTag> scoopGoalTags,
            HashSet<TTag> binaryGoalTags,
            HashSet<TTag> inProgressTags,
            Func<TTag, bool> isScoop
        ) =>
            GetPathGoalPriorityDetail(
                yieldTags,
                quantityGoalTags,
                scoopGoalTags,
                binaryGoalTags,
                inProgressTags,
                isScoop,
                _ => ""
            ).priority;

        // Same scoring as GetPathGoalPriority; labels are the distinct yield tags that
        // achieved the winning score (for log lines). formatTag maps each tag to a name.
        internal static (int priority, string[] labels) GetPathGoalPriorityDetail<TTag>(
            IEnumerable<TTag> yieldTags,
            HashSet<TTag> quantityGoalTags,
            HashSet<TTag> scoopGoalTags,
            HashSet<TTag> binaryGoalTags,
            HashSet<TTag> inProgressTags,
            Func<TTag, bool> isScoop,
            Func<TTag, string> formatTag
        )
        {
            int best = -1;
            var atBest = new List<TTag>();
            foreach (var tag in yieldTags)
            {
                if (tag == null)
                    continue;
                int score;
                bool binaryUncovered =
                    binaryGoalTags.Contains(tag) && !inProgressTags.Contains(tag);
                if (binaryUncovered && scoopGoalTags.Contains(tag) && isScoop(tag))
                    score = 3;
                else if (binaryUncovered)
                    score = 2;
                else if (quantityGoalTags.Contains(tag))
                    score = 1;
                else if (binaryGoalTags.Contains(tag))
                    score = 0;
                else
                    continue;

                if (score > best)
                {
                    best = score;
                    atBest.Clear();
                    atBest.Add(tag);
                }
                else if (score == best)
                    atBest.Add(tag);

                if (best == 3)
                    break;
            }

            if (best < 0)
                return (-1, Array.Empty<string>());

            var labels = atBest
                .Select(t => formatTag(t))
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            return (best, labels);
        }
    }
}
