using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // Double Tap Root Beer perk:
    //   - +20% fire rate (FireRate + SingleFireRate getters scaled when the
    //     main player is the one holding the weapon)
    //   - 1.5x damage on shots fired BY the main player at anything
    //     (Player.ApplyShot prefix mutates damageInfo.Damage before the
    //     method body captures it)
    //
    // perk is exclusively obtained from supply drops (no machine). buff
    // application is the standard SPT stim flow: drinking the custom item
    // adds "doubletap" to ActiveBuffsNames via the server-side CustomBuffs
    // entry, and these patches check that buff each time the relevant
    // EFT method runs.
    //
    // patch targets:
    //   - Weapon.FireRate (getter) - returns Template.bFirerate (RPM). live
    //     value used by the firing pipeline for the per-shot cooldown on
    //     auto fire. postfix multiplies __result by FireRateMultiplier,
    //     gated by IsHeldByMainPlayer.
    //   - Weapon.SingleFireRate (getter) - returns max(Template.SingleFireRate, 240).
    //     used for semi-auto cap. same gate + multiplier as FireRate so the
    //     buff covers both fire modes.
    //   - Player.ApplyShot (prefix with ref DamageInfoStruct) - canonical
    //     damage application path for the receiving player. damageInfo.Player
    //     is the SHOOTER. when shooter == main player, scale Damage by
    //     DamageMultiplier before the method body captures the local damage
    //     variable + ProceedDamageThroughArmor runs.
    //
    // gotchas:
    //   - FireRate is queried for AI bot weapons too. IsHeldByMainPlayer
    //     gate (mirrors Deadshot ergo patch) keeps the boost player-only.
    //   - Like ergo, fire rate may be snapshotted on weapon equip. Drinking
    //     the perk inherently forces a hands-controller swap (drink anim
    //     puts weapon away, then re-equips), which should re-read the
    //     buffed value.
    //   - ApplyShot fires on the RECEIVING player. __instance is the bot
    //     being hit; damageInfo.Player is the main player shooter. Gate is
    //     on damageInfo.Player.IsYourPlayer, NOT __instance.
    //   - Harmony prefix declares the param as `ref DamageInfoStruct` even
    //     though the original method takes by value. HarmonyX propagates
    //     the mutated value into the method body cleanly for struct params.
    internal static class DoubleTapBuffState
    {
        public const string BuffName = "doubletap";
        public const float FireRateMultiplier = 1.2f; // +20%
        public const float DamageMultiplier = 1.5f;   // 1.5x

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
            catch { /* health controller not ready */ }
            return false;
        }

        // is this Weapon currently held by the local main player? same gate
        // shape as DeadshotDaiquiriBuffState.IsHeldByMainPlayer. AI weapons
        // and inventory-tooltip queries don't match and are unaffected.
        public static bool IsHeldByMainPlayer(Weapon weapon)
        {
            if (weapon == null) return false;
            try
            {
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main == null) return false;
                Item handsItem = (main.HandsController as Player.FirearmController)?.Item;
                if (handsItem == null) return false;
                return handsItem.Id == weapon.Id;
            }
            catch { return false; }
        }
    }

    // --------------------------------------------------------------------
    // FIRE RATE
    // --------------------------------------------------------------------
    public sealed class DoubleTapFireRatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(Weapon), nameof(Weapon.FireRate));
        }

        [PatchPostfix]
        private static void Postfix(Weapon __instance, ref int __result)
        {
            try
            {
                if (!DoubleTapBuffState.IsBuffActive()) return;
                if (!DoubleTapBuffState.IsHeldByMainPlayer(__instance)) return;
                __result = UnityEngine.Mathf.RoundToInt(__result * DoubleTapBuffState.FireRateMultiplier);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DoubleTap] FireRate postfix threw: {ex.Message}");
            }
        }
    }

    public sealed class DoubleTapSingleFireRatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(Weapon), nameof(Weapon.SingleFireRate));
        }

        [PatchPostfix]
        private static void Postfix(Weapon __instance, ref int __result)
        {
            try
            {
                if (!DoubleTapBuffState.IsBuffActive()) return;
                if (!DoubleTapBuffState.IsHeldByMainPlayer(__instance)) return;
                __result = UnityEngine.Mathf.RoundToInt(__result * DoubleTapBuffState.FireRateMultiplier);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DoubleTap] SingleFireRate postfix threw: {ex.Message}");
            }
        }
    }

    // --------------------------------------------------------------------
    // DAMAGE
    // --------------------------------------------------------------------
    // Player.ApplyShot prefix:
    //   - __instance is the player getting hit (typically a bot in zombies mode)
    //   - damageInfo.Player is the shooter (the IPlayerOwner who fired)
    //   - gate: damageInfo.Player.IsYourPlayer == true (shooter is local main player)
    //   - mutate damageInfo.Damage *= DamageMultiplier BEFORE the method body
    //     captures `float damage = damageInfo.Damage;` at line ~7233 of Player.cs.
    //     this carries the boost through ProceedDamageThroughArmor, ApplyDamageInfo,
    //     and ReceiveDamage downstream.
    //   - declaring damageInfo as `ref DamageInfoStruct` in the prefix lets us
    //     mutate the struct value the method body sees, even though the original
    //     signature takes it by value. supported by HarmonyX.
    public sealed class DoubleTapDamagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.ApplyShot));
        }

        [PatchPrefix]
        private static void Prefix(Player __instance, ref DamageInfoStruct damageInfo)
        {
            try
            {
                IPlayerOwner shooterOwner = damageInfo.Player;
                if (shooterOwner == null) return;
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main == null) return;

                // IPlayerOwner.iPlayer -> IPlayer.ProfileId is the canonical
                // shooter identity. compare against MainPlayer.ProfileId to
                // gate the buff to bullets fired by the local main player.
                string shooterProfileId = shooterOwner.iPlayer?.ProfileId;
                if (string.IsNullOrEmpty(shooterProfileId)) return;
                if (shooterProfileId != main.ProfileId) return;

                if (!DoubleTapBuffState.IsBuffActive()) return;

                damageInfo.Damage *= DoubleTapBuffState.DamageMultiplier;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DoubleTap] ApplyShot prefix threw: {ex.Message}");
            }
        }
    }
}
