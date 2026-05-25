using Comfort.Common;
using EFT;
using Manimal.SpeedCola.Patches;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // attached to a host GameObject at raid start (zombies mode only). polls
    // each frame for the "juggernog" buff becoming active and, on first
    // activation, applies a one-time +MaxHpBonus to the main player's body
    // parts via ZombieHealthBooster.
    //
    // we don't restore on buff expiry because the buff is configured for
    // 999999s duration (zombies raid effectively forever). if the player
    // dies the raid ends, the host GameObject is destroyed, and the monitor
    // along with it - nothing to clean up.
    public class JuggernogHpBoostMonitor : MonoBehaviour
    {
        private bool _applied;

        private void Update()
        {
            if (_applied) return;
            if (!JuggernogBuffState.IsBuffActive()) return;

            Player main = Singleton<GameWorld>.Instance?.MainPlayer;
            if (main == null) return;

            ZombieHealthBooster.Apply(main, JuggernogBuffState.MaxHpBonus, "Juggernog player buff");
            _applied = true;
            Plugin.LogSource?.LogInfo($"[Juggernog] applied +{JuggernogBuffState.MaxHpBonus} total max HP to player.");
        }

        // called by the Quick Revive perk-wipe path. subtracts the HP bonus we
        // previously added and clears the _applied latch so a subsequent
        // Juggernog re-buy will re-apply the bonus from scratch. no-op if
        // we never applied the bonus in the first place.
        public void Reset()
        {
            if (!_applied) return;
            Player main = Singleton<GameWorld>.Instance?.MainPlayer;
            if (main == null) return;
            ZombieHealthBooster.Reverse(main, JuggernogBuffState.MaxHpBonus, "Juggernog perk wipe");
            _applied = false;
            Plugin.LogSource?.LogInfo($"[Juggernog] reset HP boost (will re-apply on next Juggernog buff activation).");
        }
    }
}
