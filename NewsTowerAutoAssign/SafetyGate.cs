namespace NewsTowerAutoAssign
{
    // Centralised "is it safe to mutate game state right now?" gate.
    //
    // Why this exists
    // ---------------
    // Every automation path in this mod eventually mutates game state:
    //   * SuitcaseAutomation calls UnlockItem (writes to BuildUnlockListManager).
    //   * BribeAutomation calls TowerStats.AddMoney and sets IsCompleted.
    //   * AssignmentEvaluator calls LiveReportableManager.RemoveReportable,
    //     AssignableToReportable.AssignTo, and NewsItemStoryFile.OnVisibilityChanged.
    //
    // All of those are called from Harmony patches that fire DURING save load
    // (AddReportable runs per-story while the save is still being deserialised).
    // Mutating game state at that moment races with the save-restore code and
    // in at least one case (BuildUnlockListManager.AddFromLoadGame) produces a
    // hard "An item with the same key has already been added" crash the player
    // sees as "Load Error".
    //
    // Rule
    // ----
    // Every state-mutating code path in the mod MUST early-out when
    // `IsOpen == false`. The gate is:
    //
    //   * CLOSED at plugin startup (initial static default).
    //   * CLOSED when LiveReportableManager.Awake fires (Patch_LRMAwake). Awake
    //     runs on the NEW manager instance as a save begins to load, before any
    //     component restoration - so every new save load starts with the gate
    //     closed, even if a previous save was loaded in this session.
    //   * OPENED when LiveReportableManager.OnAfterLoadStart completes
    //     (Patch_AfterLoad Postfix) - save restoration is finished.
    //   * OPENED when IdleWorkplaceState.DoState ticks (Patch_IdleWorkplaceDoState
    //     Prefix) - covers fresh-new-game starts where OnAfterLoadStart may not
    //     fire, and any scenario where the gate got stuck closed.
    //
    // Pure state; trivially unit testable (see NewsTowerAutoAssign.Tests).
    internal static class SafetyGate
    {
        // Default CLOSED. A patch must explicitly open the gate - never
        // assume "no load happened yet" means it's safe.
        private static bool _isOpen = false;

        internal static bool IsOpen => _isOpen;

        internal static void Open() => _isOpen = true;

        internal static void Close() => _isOpen = false;
    }
}
