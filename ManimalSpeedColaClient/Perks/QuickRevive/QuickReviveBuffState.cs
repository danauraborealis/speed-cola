using System;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;

namespace Manimal.SpeedCola.Patches
{
    // active-buff probe for the Quick Revive perk. mirror of
    // JuggernogBuffState / DeathPerceptionBuffState - the actual
    // stimulator buff name in CustomBuffs (server-side JSON) drives
    // this. Plugin.QuickReviveForceActive overrides the check so
    // we can test the revive flow without wiring the full drink/
    // machine pipeline.
    internal static class QuickReviveBuffState
    {
        public const string BuffName = "quickrevive";

        public static bool IsBuffActive()
        {
            if (Plugin.QuickReviveForceActive) return true;

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
