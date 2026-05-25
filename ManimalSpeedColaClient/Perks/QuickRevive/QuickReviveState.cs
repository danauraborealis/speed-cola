namespace Manimal.SpeedCola
{
    // tracks whether the player has an unused Quick Revive charge available.
    // each drink = one charge; the next fatal hit consumes it.
    //
    // lifecycle:
    //   - dispense path (QuickReviveActionPatch.DispenseItemAsync) sets
    //     HasCharge = true after the drink is successfully placed +
    //     (optionally) auto-used.
    //   - QuickReviveKillInterceptPatch sets HasCharge = false on the
    //     successful auto-revive.
    //   - SpawnQuickReviveOnGameStartedPatch (or wave start) calls
    //     ResetForNewRaid() so a charge doesnt leak between raids.
    //
    // independent of QuickReviveBuffState (the buff name probe) because
    // the stimulator buff has Duration 999999 - it never naturally
    // expires, so we cant rely on the buff itself to track consumption.
    // we use the static flag as the source of truth.
    public static class QuickReviveState
    {
        public static bool HasCharge { get; private set; }

        public static void GrantCharge()
        {
            HasCharge = true;
            Plugin.LogSource?.LogInfo("[QuickRevive] charge granted - next fatal hit will trigger auto-revive.");
        }

        public static bool TryConsumeCharge()
        {
            if (!HasCharge) return false;
            HasCharge = false;
            Plugin.LogSource?.LogInfo("[QuickRevive] charge consumed.");
            return true;
        }

        public static void ResetForNewRaid()
        {
            HasCharge = false;
        }
    }
}
