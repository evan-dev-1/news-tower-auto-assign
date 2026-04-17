using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalNews;
using Reportables;

namespace NewsTowerAutoAssign
{
    // Resolves the "new item unlocked" suitcase reward without opening the UI popup.
    //
    // Why this exists: when a story's chain reaches a NewsItemSuitcaseBuildable node,
    // the node unlocks but the game does NOT run the unlock side-effect until the
    // player makes the story visible (opens the newsbook page). See
    // NewsItemSuitcase.OnVisibilityChanged - it is the only call site that sets DidAct,
    // fires the popup Play event, and invokes UnlockItem. Players who rely on the mod
    // to handle the board without opening stories never trigger visibility, so the
    // node sits at NodeState.Unlocked forever, IsCompleted stays false, and the chain
    // stalls - blocking the reporter from picking up new stories.
    //
    // Fix: scan each story on every evaluation cycle. For any suitcase whose node is
    // Unlocked and DidAct==false, replicate the game's own "act" sequence:
    //   1. call UnlockItem() via reflection (protected abstract) - identical unlock
    //      side-effect to the normal flow, including the DRNG draw from
    //      BuildUnlockListManager.TryUnlockFromList.
    //   2. set DidAct=true via reflection (protected set) - so if the player later
    //      opens the story, OnVisibilityChanged takes the short-circuit branch
    //      (DidAct already true -> just set IsCompleted=true, no double-fire).
    //   3. set IsCompleted=true (public set) - flips the NewsItemNodeCompleter flag
    //      that gates the chain, freeing the story to progress.
    //
    // The SuitcasePopup never opens. Player sees the unlocked building in the build
    // menu and a mod log line naming the item. Patch_SuitcasePopupAutoSkip remains
    // as a belt-and-braces fallback for any popup that still manages to open (e.g.
    // a popup that armed itself before the mod got a chance to scan).
    internal static class SuitcaseAutomation
    {
        // Cached per runtime type - NewsItemSuitcase<TData> is generic, so we can't
        // resolve the inherited DidAct / UnlockItem members at compile time without
        // knowing TData. Caching per GetType() is sufficient because there are only
        // a couple of concrete subclasses in the game.
        private static readonly Dictionary<Type, MethodInfo> _unlockItemMethodCache =
            new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _didActPropertyCache =
            new Dictionary<Type, PropertyInfo>();

        // Used by AutoAssignPlugin.VerifyReflection to surface "a game update
        // renamed the abstract NewsItemSuitcase.UnlockItem / DidAct members"
        // at plugin load time rather than at first-suitcase scan. Returns the
        // (first known subclass, missing member names) so the log line can
        // point the user at the specific game version mismatch.
        internal static (string typeName, string[] missing) ProbeReflectionTargets()
        {
            // Use typeof over a string-keyed Type.GetType lookup: this file
            // already references NewsItemSuitcaseBuildable statically, so a
            // rename in the game would fail the compile (loud) rather than
            // silently returning "<not-found>" at runtime (quiet).
            var suitcaseType = typeof(NewsItemSuitcaseBuildable);
            var missing = new System.Collections.Generic.List<string>();
            var unlockItem = suitcaseType.GetMethod(
                "UnlockItem",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            if (unlockItem == null)
                missing.Add("UnlockItem");
            var didAct = suitcaseType.GetProperty(
                "DidAct",
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.FlattenHierarchy
            );
            if (didAct == null)
                missing.Add("DidAct");
            return (suitcaseType.FullName, missing.ToArray());
        }

        internal static void TryResolveSuitcases(NewsItem newsItem)
        {
            if (!AutoAssignPlugin.AutoSkipSuitcasePopups.Value || newsItem == null)
                return;
            // Defer to the universal safety gate - before save restoration has
            // completed, calling UnlockItem -> TryUnlockFromList -> GetOrCreateList
            // seeds a fresh entry in BuildUnlockListManager.lists that
            // AddFromLoadGame later collides with (Dictionary.Add throws on
            // duplicate keys - the "Load Error: An item with the same key has
            // already been added" the player sees). See SafetyGate for the
            // full rationale and the open/close event map.
            if (!SafetyGate.IsOpen)
                return;

            try
            {
                foreach (
                    var suitcase in newsItem.GetComponentsInChildren<NewsItemSuitcaseBuildable>(
                        true
                    )
                )
                {
                    if (suitcase == null || suitcase.IsCompleted || suitcase.DidAct)
                        continue;

                    var node = suitcase.Node;
                    if (node == null || node.NodeState != NewsItemNodeState.Unlocked)
                        continue;

                    var type = suitcase.GetType();

                    if (!_unlockItemMethodCache.TryGetValue(type, out var unlockItem))
                    {
                        unlockItem = type.GetMethod(
                            "UnlockItem",
                            BindingFlags.Instance | BindingFlags.NonPublic
                        );
                        _unlockItemMethodCache[type] = unlockItem;
                    }
                    if (unlockItem == null)
                    {
                        // A game update renamed or removed the member we rely on.
                        // Surface once via Error (survives Release-build log
                        // suppression) and skip - leaving the suitcase for manual
                        // resolution is strictly safer than throwing mid-scan.
                        AssignmentLog.Error(
                            "SuitcaseAutomation: UnlockItem method not found on "
                                + type.FullName
                                + " - suitcase auto-resolve disabled for this type this session."
                        );
                        continue;
                    }

                    if (!_didActPropertyCache.TryGetValue(type, out var didActProp))
                    {
                        didActProp = type.GetProperty(
                            "DidAct",
                            BindingFlags.Instance
                                | BindingFlags.Public
                                | BindingFlags.NonPublic
                                | BindingFlags.FlattenHierarchy
                        );
                        _didActPropertyCache[type] = didActProp;
                    }
                    if (didActProp == null)
                    {
                        AssignmentLog.Error(
                            "SuitcaseAutomation: DidAct property not found on "
                                + type.FullName
                                + " - suitcase auto-resolve disabled for this type this session."
                        );
                        continue;
                    }

                    // Order mirrors the game's own flow in OnVisibilityChanged:
                    // DidAct -> UnlockItem -> IsCompleted. Setting DidAct first is
                    // important because some subscribers to the visibility-triggered
                    // state check it mid-unlock and would otherwise re-enter.
                    didActProp.SetValue(suitcase, true, null);
                    unlockItem.Invoke(suitcase, null);
                    suitcase.IsCompleted = true;

                    AssignmentLog.ClearSuppression(newsItem);
                    string itemName;
                    try
                    {
                        itemName =
                            suitcase.UnlockedItem != null
                                ? (
                                    suitcase.UnlockedItem.UnlocalizedBuildname_Safe
                                    ?? suitcase.UnlockedItem.name
                                )
                                : "<list empty>";
                    }
                    catch
                    {
                        itemName = "<unnamed>";
                    }
                    AssignmentLog.Decision(
                        AssignmentLog.StoryName(newsItem)
                            + " "
                            + AssignmentLog.StoryTagList(newsItem)
                            + " → ITEM UNLOCKED: "
                            + itemName
                            + " (suitcase node auto-resolved, chain unblocked)."
                    );
                }
            }
            catch (Exception e)
            {
                // Reflection / Unity component walks on an in-flight story can
                // surface transient nulls - log once and move on rather than
                // let the exception climb into the evaluator or Harmony patch.
                AssignmentLog.Error(
                    "SuitcaseAutomation.TryResolveSuitcases("
                        + AssignmentLog.StoryName(newsItem)
                        + "): "
                        + e
                );
            }
        }
    }
}
