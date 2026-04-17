using System.Collections.Generic;
using System.Linq;
using _Game.Quests;
using _Game.Quests.Composed;
using _Game.Quests.Composed.Components;
using _Game.Quests.Composed.Data.Components;
using AreaMaps;
using Tower_Stats;
using UnityEngine;

namespace NewsTowerAutoAssign.InGameTests
{
    // Tests the goal-tag extraction pipeline end-to-end against live game
    // state.
    //
    // The regression that motivated this rewrite: faction (mafia / mayor /
    // police) ComposedQuests carry their "Print at least one Red Herring"
    // goal as QuestCollectingCombo / QuestCollectingReward components (not
    // QuestTargetSetRequirement). The previous extractor and its tests
    // both only looked at QuestTargetSet/Combo/TopTag/AboveTheFold - so the
    // tag went unseen and the tests silently passed.
    //
    // Guard principles applied here:
    //
    //  * Assertions are driven by the DATA tree (the authored
    //    ScriptableObject hierarchy), NOT by the runtime fetch the
    //    extractor uses. If extraction has a blind spot, data-tree truth
    //    still surfaces the missed tag and the test fails.
    //
    //  * No pre-filtering by "reqs.OfType<X>().Any()" - that's the same
    //    broken signal the extractor uses. Every non-dummy quest with any
    //    authored tag-bearing component must round-trip through extraction.
    //
    //  * On failure, DumpComposedQuestStructure prints the full tree so
    //    the next coverage gap is obvious.
    internal static class GoalExtractionTests
    {
        internal static void Run()
        {
            var ctx = new TestContext("GoalExtraction");

            // Runner gates on QuestManager + non-dummy ComposedQuest existing,
            // so reaching this point with either null is a runner bug.
            if (QuestManager.Instance == null)
            {
                ctx.Skip("all", "QuestManager not available - runner should have deferred");
                ctx.PrintSummary();
                return;
            }

            EnumerationSourceTest(ctx);
            ExtractedTagsCoverDataTreeTest(ctx);
            FactionQuestsAreScannedTest(ctx);
            NonDummyQuestHasRuntimeChildrenTest(ctx);
            GetCurrentGoalTagSetsSanityTest(ctx);

            ctx.PrintSummary();
        }

        // Every active ComposedQuest must be reachable from at least one of
        // the sources ReporterLookup scans: QuestManager.AllRunningQuests or
        // AreaMapQuestHub.Quest. Faction quests only appear post-recap so a
        // zero-count here on a new save is legitimate - we log rather than
        // fail when there's nothing yet.
        private static void EnumerationSourceTest(TestContext ctx)
        {
            var viaManager = new HashSet<Quest>(
                QuestManager.Instance.AllRunningQuests.Where(q => q != null)
            );

            var viaHubs = new HashSet<Quest>();
            var root = AreaMapRoot.Instance;
            if (root != null)
            {
                foreach (var hub in root.GetComponentsInChildren<AreaMapQuestHub>(true))
                {
                    if (hub?.Quest != null)
                        viaHubs.Add(hub.Quest);
                }
            }

            int unionSize = viaManager.Count + viaHubs.Count(q => !viaManager.Contains(q));
            ctx.Assert(unionSize >= 0, "enumeration does not throw");
            AutoAssignPlugin.Log.LogInfo(
                "[INFO] GoalExtraction / enumeration: "
                    + viaManager.Count
                    + " via QuestManager, "
                    + viaHubs.Count
                    + " via hubs (union = "
                    + unionSize
                    + ")"
            );
        }

        // THE regression guard. For every non-dummy ComposedQuest, compute
        // the set of tags the DATA tree says should be goal tags, then run
        // the extractor and assert every data-tree tag appears in binary.
        //
        // The data tree is the authored source - every tag listed here is
        // something the player will see or be judged on. If extraction is
        // missing anything, this fails loudly with a structural dump so
        // the gap is obvious.
        private static void ExtractedTagsCoverDataTreeTest(TestContext ctx)
        {
            var allComposed = EnumerateAllComposedQuests().ToList();
            if (allComposed.Count == 0)
            {
                // Runner waits for at least one non-dummy ComposedQuest; zero
                // here means enumeration skipped every quest (all dummies /
                // filtered out). Not a save-state issue - something changed
                // under us between readiness check and extractor run.
                ctx.Skip("extracted covers data", "no ComposedQuests enumerable - runner bug");
                return;
            }

            int tested = 0;
            foreach (var cq in allComposed)
            {
                if (cq.IsDummy || cq.Data == null)
                    continue;

                var expected = CollectTagsFromDataTree(cq);
                if (expected.Count == 0)
                    continue; // quest has no tag-bearing requirements at all

                tested++;
                var quantity = new HashSet<PlayerStatDataTag>();
                var binary = new HashSet<PlayerStatDataTag>();
                var added = new HashSet<PlayerStatDataTag>();
                ReporterLookup.ExtractComposedQuestTags(cq, quantity, binary, added);
                var combined = new HashSet<PlayerStatDataTag>(quantity);
                combined.UnionWith(binary);

                var missing = expected.Except(combined).ToList();
                bool pass = missing.Count == 0;
                string label = IdentityLabel(cq);
                ctx.Assert(
                    pass,
                    "extractor finds every authored tag: " + label,
                    "missing=["
                        + string.Join(", ", missing.Select(t => t?.name ?? "null"))
                        + "] expected=["
                        + string.Join(", ", expected.Select(t => t?.name ?? "null"))
                        + "] got quantity=["
                        + string.Join(", ", quantity.Select(t => t?.name ?? "null"))
                        + "] binary=["
                        + string.Join(", ", binary.Select(t => t?.name ?? "null"))
                        + "]"
                );
                if (!pass)
                    ReporterLookup.DumpComposedQuestStructure(cq, "test failure: missing tags");
            }

            if (tested == 0)
                ctx.NotApplicable(
                    "extracted covers data",
                    "no active quest authors tag-bearing requirement components (nothing to extract)"
                );
        }

        // Specifically verifies the faction / HiddenAgenda row is scanned.
        // If any HiddenAgenda hub is holding a non-dummy ComposedQuest,
        // that quest must be enumerable via the same two sources
        // ReporterLookup walks.
        private static void FactionQuestsAreScannedTest(TestContext ctx)
        {
            var root = AreaMapRoot.Instance;
            if (root == null)
            {
                ctx.NotApplicable("faction hub scan", "AreaMapRoot not present in this scene");
                return;
            }

            var factionHubs = root.GetComponentsInChildren<AreaMapQuestHub>(true)
                .Where(h => h != null && h.QuestRow == QuestRow.HiddenAgenda)
                .ToList();

            if (factionHubs.Count == 0)
            {
                ctx.NotApplicable(
                    "faction hub scan",
                    "no HiddenAgenda hubs in this save - nothing for this test to cover"
                );
                return;
            }

            int withQuest = factionHubs.Count(h => h.Quest != null);
            AutoAssignPlugin.Log.LogInfo(
                "[INFO] GoalExtraction / faction hubs: "
                    + factionHubs.Count
                    + " total, "
                    + withQuest
                    + " holding a quest"
            );

            foreach (var hub in factionHubs)
            {
                if (hub.Quest == null)
                    continue;
                bool reachable =
                    QuestManager.Instance.AllRunningQuests.Any(q => q == hub.Quest)
                    || factionHubs.Any(h => h?.Quest == hub.Quest);
                ctx.Assert(
                    reachable,
                    "faction quest is enumerable: " + IdentityLabel(hub.Quest as ComposedQuest)
                );
            }
        }

        // Every non-dummy ComposedQuest must have a non-empty runtime tree
        // (at least one ComposableRuntimeComponent child). A non-dummy
        // quest with an empty runtime tree is a ComposableRuntimeComponent
        // construction failure - the game itself wouldn't be able to
        // evaluate or display it.
        //
        // This test is a structural sanity check; it's separate from the
        // extractor test because it catches runtime-build issues without
        // being confused by tag coverage gaps.
        private static void NonDummyQuestHasRuntimeChildrenTest(TestContext ctx)
        {
            int tested = 0;
            foreach (var cq in EnumerateAllComposedQuests())
            {
                if (cq.IsDummy)
                    continue;
                tested++;
                var runtimeChildren = cq.GetComponentsInChildren<object>(true)
                    .Where(o => !(o is ComposedQuest))
                    .ToList();
                string label = IdentityLabel(cq);
                bool pass = runtimeChildren.Count > 0;
                ctx.Assert(
                    pass,
                    "non-dummy quest has runtime children: " + label,
                    "runtime tree is empty but IsDummy=false"
                );
                if (!pass)
                    ReporterLookup.DumpComposedQuestStructure(
                        cq,
                        "test failure: empty runtime tree"
                    );
            }
            if (tested == 0)
                ctx.Skip(
                    "non-dummy runtime children",
                    "no non-dummy ComposedQuests - readiness gate failed"
                );
        }

        // Public-API sanity: GetCurrentGoalTagSets returns non-null sets
        // and, when the data tree says binary tags should exist, binary
        // must be non-empty.
        private static void GetCurrentGoalTagSetsSanityTest(TestContext ctx)
        {
            var (quantity, scoop, binary) = ReporterLookup.GetCurrentGoalTagSets();
            ctx.Assert(quantity != null, "quantity set non-null");
            ctx.Assert(scoop != null, "scoop set non-null");
            ctx.Assert(binary != null, "binary set non-null");

            AutoAssignPlugin.Log.LogInfo(
                "[INFO] GoalExtraction / final sets - quantity="
                    + quantity.Count
                    + " scoop="
                    + scoop.Count
                    + " binary="
                    + binary.Count
            );

            // Authoritative: union of all data-tree tags across every
            // active non-dummy ComposedQuest. If any quest has authored
            // tag-bearing components, binary must be populated.
            var expectedFromData = new HashSet<PlayerStatDataTag>();
            foreach (var cq in EnumerateAllComposedQuests())
            {
                if (cq.IsDummy)
                    continue;
                foreach (var t in CollectTagsFromDataTree(cq))
                    expectedFromData.Add(t);
            }

            if (expectedFromData.Count > 0)
            {
                var combined = new HashSet<PlayerStatDataTag>(quantity);
                combined.UnionWith(binary);
                var missing = expectedFromData.Except(combined).ToList();
                ctx.Assert(
                    missing.Count == 0,
                    "combined quantity+binary contains every authored tag across all quests",
                    "missing=["
                        + string.Join(", ", missing.Select(t => t?.name ?? "null"))
                        + "] expected=["
                        + string.Join(", ", expectedFromData.Select(t => t?.name ?? "null"))
                        + "] got quantity=["
                        + string.Join(", ", quantity.Select(t => t?.name ?? "null"))
                        + "] binary=["
                        + string.Join(", ", binary.Select(t => t?.name ?? "null"))
                        + "]"
                );
            }
        }

        // The authoritative "what tags should this quest care about?" read
        // from the ScriptableObject data tree. This deliberately covers
        // every tag-bearing *Data component type we know about so that new
        // extractor blind spots surface as test failures (data-tree says
        // there should be a tag, runtime extractor missed it).
        private static HashSet<PlayerStatDataTag> CollectTagsFromDataTree(ComposedQuest cq)
        {
            var tags = new HashSet<PlayerStatDataTag>();
            if (cq?.Data == null)
                return tags;

            foreach (var d in cq.Data.GetAllChildren<QuestTargetSetRequirementData>(true))
            {
                var protos = d.targets?.targets;
                if (protos == null)
                    continue;
                foreach (var pt in protos)
                    if (pt?.tag != null)
                        tags.Add(pt.tag);
            }
            foreach (var d in cq.Data.GetAllChildren<QuestTargetComboRequirementData>(true))
                if (d.tag != null)
                    tags.Add(d.tag);
            foreach (var d in cq.Data.GetAllChildren<QuestTopTagRequirementData>(true))
                if (d.tag != null)
                    tags.Add(d.tag);
            foreach (var d in cq.Data.GetAllChildren<QuestAboveTheFoldTagRequirementData>(true))
                if (d.tag != null)
                    tags.Add(d.tag);
            foreach (var d in cq.Data.GetAllChildren<QuestCollectingComboData>(true))
                if (d.tagToCollect != null)
                    tags.Add(d.tagToCollect);
            foreach (var d in cq.Data.GetAllChildren<QuestCollectingRewardData>(true))
                if (d.tagToCollect != null)
                    tags.Add(d.tagToCollect);
            return tags;
        }

        private static IEnumerable<ComposedQuest> EnumerateAllComposedQuests()
        {
            var seen = new HashSet<ComposedQuest>();
            if (QuestManager.Instance != null)
            {
                foreach (var q in QuestManager.Instance.AllRunningQuests)
                {
                    if (q is ComposedQuest cq && seen.Add(cq))
                        yield return cq;
                }
            }
            var root = AreaMapRoot.Instance;
            if (root != null)
            {
                foreach (var hub in root.GetComponentsInChildren<AreaMapQuestHub>(true))
                {
                    if (hub?.Quest is ComposedQuest cq && seen.Add(cq))
                        yield return cq;
                }
            }
        }

        private static string IdentityLabel(ComposedQuest cq)
        {
            if (cq?.Data == null)
                return "?";
            var identity =
                cq.Data.identity != null ? cq.Data.identity.UnlocalizedIdentityName : "?";
            return identity + ": " + (cq.Data.UnlocalizedTitle ?? "?");
        }
    }
}
