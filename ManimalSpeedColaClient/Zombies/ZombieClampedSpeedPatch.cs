using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // postfix on MovementContext.ClampedSpeed (the property getter used
    // by both bot AI and player movement). looks up ZombieSpeedComponent
    // on the owning Player's GameObject; if present, multiplies the
    // returned speed by the component's multiplier.
    //
    // same hook SuperSprintJump / knife-sprint use, but here keyed off
    // a per-bot component instead of a global config value - so each
    // zombie can have its own speed without affecting the player or
    // any non-zombie bots.
    public class ZombieClampedSpeedPatch : ModulePatch
    {
        // _player is private on MovementContext; cached FieldInfo so we
        // don't reflect on every getter call (ClampedSpeed fires a LOT).
        private static readonly FieldInfo PlayerField =
            AccessTools.Field(typeof(MovementContext), "_player");

        protected override MethodBase GetTargetMethod() =>
            AccessTools.PropertyGetter(typeof(MovementContext), nameof(MovementContext.ClampedSpeed));

        [PatchPostfix]
        private static void Postfix(MovementContext __instance, ref float __result)
        {
            try
            {
                if (__instance == null || PlayerField == null) return;
                Player p = PlayerField.GetValue(__instance) as Player;
                if (p == null) return;
                ZombieSpeedComponent c = p.gameObject.GetComponent<ZombieSpeedComponent>();
                if (c == null) return;
                __result *= c.SpeedMultiplier;
            }
            catch (Exception ex)
            {
                // ClampedSpeed runs every frame on every bot - swallow
                // exceptions to avoid log-spamming.
                Plugin.LogSource?.LogWarning($"[ZombieSpeed] ClampedSpeed postfix threw: {ex.Message}");
            }
        }
    }
}
