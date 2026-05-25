using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // when the "speedcola" stimulator buff is active on the player, multiply
    // the FirearmsAnimator speed knobs by 1.4 (40% faster). covers reload +
    // mag operations (SpeedReload), weapon draw (SpeedDraw), and jam clearing
    // (SpeedFix). the multiplier is hardcoded - the previous BepInEx config
    // entry was removed to keep the perk's effect canonical across saves.
    //
    // we read the buff status from ActiveHealthController.ActiveBuffsNames()
    // which is the IHealthController interface accessor BSG uses elsewhere
    // (kill condition checks etc) - stable across versions.
    //
    // these knobs are weapon-only - meds use SetUseTimeMultiplier on their
    // own FirearmsAnimator instance which we dont touch here, so the drink
    // animation (DrinkSpeedPatch) stays at exactly 1.4x as well.
    internal static class WeaponSpeedBuffState
    {
        public const string BuffName = "speedcola";
        public const float Multiplier = 1.5f;

        public static bool IsBuffActive()
        {
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
                // health controller not ready (menu/loading); not active.
            }
            return false;
        }
    }

    // FirearmsAnimator.SetSpeedParameters(reload, draw) - weapon reload + draw
    public sealed class SpeedColaReloadDrawPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetSpeedParameters));

        [PatchPostfix]
        private static void Postfix(FirearmsAnimator __instance, float reload, float draw)
        {
            if (!WeaponSpeedBuffState.IsBuffActive()) return;
            float mult = WeaponSpeedBuffState.Multiplier;
            // re-apply the scaled values - postfix runs after the original so this
            // becomes the final value on the animator.
            try
            {
                WeaponAnimationSpeedControllerClass.SetSpeedReload(__instance.Animator, reload * mult);
                WeaponAnimationSpeedControllerClass.SetSpeedDraw(__instance.Animator, draw * mult);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[SpeedCola] reload/draw scale failed: {ex.Message}");
            }
        }
    }

    // FirearmsAnimator.SetMalfRepairSpeed(fix) - jam clearing
    public sealed class SpeedColaMalfRepairPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetMalfRepairSpeed));

        [PatchPostfix]
        private static void Postfix(FirearmsAnimator __instance, float fix)
        {
            if (!WeaponSpeedBuffState.IsBuffActive()) return;
            float mult = WeaponSpeedBuffState.Multiplier;
            try
            {
                WeaponAnimationSpeedControllerClass.SetSpeedFix(__instance.Animator, fix * mult);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[SpeedCola] malf-repair scale failed: {ex.Message}");
            }
        }
    }
}
