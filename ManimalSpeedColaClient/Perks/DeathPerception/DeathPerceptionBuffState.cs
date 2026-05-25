using System;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;

namespace Manimal.SpeedCola.Patches
{
    // active-buff probe for the Death Perception perk. mirrors
    // JuggernogBuffState - the actual stimulator buff name in
    // CustomBuffs (server-side JSON) drives this. for testing,
    // Plugin.DeathPerceptionForceActive overrides the check so
    // we can see the visual effect without wiring the full
    // drink/machine buy pipeline.
    internal static class DeathPerceptionBuffState
    {
        public const string BuffName = "deathperception";

        public static bool IsBuffActive()
        {
            // dev override: forces the effect on regardless of buff state.
            // toggled by the F7 test hotkey in Plugin.cs.
            if (Plugin.DeathPerceptionForceActive) return true;

            try
            {
                GameWorld gw = Singleton<GameWorld>.Instance;
                if (gw?.MainPlayer?.ActiveHealthController == null) return false;
                string[] names = gw.MainPlayer.ActiveHealthController.ActiveBuffsNames();
                if (names == null) return false;
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], BuffName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // controller not ready (menu / loading): not active.
            }
            return false;
        }
    }
}
