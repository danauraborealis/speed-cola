using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // Bandolier Bandit perk:
    //   1. +10 max-capacity to every magazine the main player loads into
    //      their currently held weapon. a 30-rd mag becomes 40, a 20-rd
    //      becomes 30, etc. patched via MagazineItemClass.MaxCount postfix
    //      so HUD + reload code see the extended ceiling. the freshly-
    //      loaded mag is then topped up to the new ceiling each time it
    //      changes (mag-swap detection in Plugin.Update).
    //   2. 5% per-shot chance to NOT consume a cartridge from the mag.
    //      hooked on Weapon.OnShot postfix - rolls a chance, if it hits
    //      the magazine's last cartridge stack gets its StackObjectsCount
    //      incremented back by 1. silent (per user spec).
    //   3. Weapons NEVER jam / misfire / fail-to-feed. Weapon.BaseMalfunctionChance
    //      getter postfix forces 0f when the buff is active + weapon is
    //      currently held by the main player. covers all malfunction types
    //      since BaseMalfunctionChance is what every roll keys off.
    //
    // perk is supply-drop exclusive (no machine). buff name is "bandolierbandit"
    // - matches the ServerModFiles CustomBuffs entry + StimulatorBuffs link
    // on the CustomItems entry. permanent for the raid (Duration: 999999
    // in the buff json).
    internal static class BandolierBanditBuffState
    {
        public const string BuffName = "bandolierbandit";
        public const int CapacityBonus = 10;     // +10 to every loaded mag's MaxCount
        public const float SaveChance = 0.05f;   // 5% per-shot bullet refund

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

        // returns the Weapon currently in the main player's hands, or null.
        public static Weapon GetHeldWeapon()
        {
            try
            {
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main == null) return null;
                Player.FirearmController fc = main.HandsController as Player.FirearmController;
                return fc?.Item as Weapon;
            }
            catch { return null; }
        }

        // is the given weapon what the main player is currently holding?
        // Id-compare to be safe across instance churn.
        public static bool IsHeldByMainPlayer(Weapon weapon)
        {
            if (weapon == null) return false;
            Weapon held = GetHeldWeapon();
            return held != null && held.Id == weapon.Id;
        }

        // is the given magazine inside the currently held weapon's mod_magazine
        // slot? walks the cartridge slot's parent address up one level.
        public static bool IsMagInHeldWeapon(MagazineItemClass mag)
        {
            try
            {
                if (mag == null) return false;
                ItemAddress addr = mag.Parent;
                if (addr?.Container is Slot slot && slot.ID == "mod_magazine")
                {
                    // slot's parent item is the weapon. check identity.
                    var parentWeapon = slot.ParentItem as Weapon;
                    return IsHeldByMainPlayer(parentWeapon);
                }
            }
            catch { /* freshly-created mag may have null Parent */ }
            return false;
        }

        // last-seen in-weapon mag id. when this changes (player reloaded,
        // swapped weapons, or just drank the perk), the new in-weapon mag
        // gets its cartridge stack topped to the buffed MaxCount.
        // Plugin.Update polls and drives this each frame.
        public static string LastTopppedMagId;
    }

    // --------------------------------------------------------------------
    // CAPACITY (+10)
    // --------------------------------------------------------------------
    // MagazineItemClass.MaxCount is a virtual property (line 16 of
    // MagazineItemClass.cs in the SPT decompile). postfixing it adds
    // CapacityBonus to the reported max ONLY when:
    //   - buff is active
    //   - the magazine is currently inside the main player's held weapon
    //
    // spare mags in pouches don't have their MaxCount inflated - the +10
    // only applies once a mag is loaded into the current weapon (per user
    // spec: "any mag becomes +10 the moment it's loaded").
    public sealed class BandolierBanditMagMaxCountPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(MagazineItemClass), nameof(MagazineItemClass.MaxCount));
        }

        [PatchPostfix]
        private static void Postfix(MagazineItemClass __instance, ref int __result)
        {
            try
            {
                if (!BandolierBanditBuffState.IsBuffActive()) return;
                if (!BandolierBanditBuffState.IsMagInHeldWeapon(__instance)) return;
                __result += BandolierBanditBuffState.CapacityBonus;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[BandolierBandit] MaxCount postfix threw: {ex.Message}");
            }
        }
    }

    // --------------------------------------------------------------------
    // MALFUNCTIONS (zero)
    // --------------------------------------------------------------------
    // Weapon.BaseMalfunctionChance is a virtual property (line 226). every
    // malfunction roll downstream multiplies/keys off this. forcing it to
    // 0 when the buff is active + weapon is main-player-held eliminates
    // all malfunction types (jam / misfire / feed / slide).
    public sealed class BandolierBanditMalfunctionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(Weapon), nameof(Weapon.BaseMalfunctionChance));
        }

        [PatchPostfix]
        private static void Postfix(Weapon __instance, ref float __result)
        {
            try
            {
                if (!BandolierBanditBuffState.IsBuffActive()) return;
                if (!BandolierBanditBuffState.IsHeldByMainPlayer(__instance)) return;
                __result = 0f;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[BandolierBandit] BaseMalfunctionChance postfix threw: {ex.Message}");
            }
        }
    }

    // --------------------------------------------------------------------
    // SAVE-A-BULLET (5% chance per shot)
    // --------------------------------------------------------------------
    // Weapon.OnShot is the per-shot post-fire hook (line 1612). by then
    // the cartridge stack has been decremented; we roll a chance and, if
    // it hits, bump the cartridge stack's last item's StackObjectsCount
    // by 1 (refund).
    //
    // edge case: if the shot was the LAST round (mag now empty), the
    // cartridge stack is gone and we skip the refund. user accepted this
    // as fine - barely noticeable.
    public sealed class BandolierBanditOnShotPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Weapon), nameof(Weapon.OnShot));
        }

        [PatchPostfix]
        private static void Postfix(Weapon __instance)
        {
            try
            {
                if (!BandolierBanditBuffState.IsBuffActive()) return;
                if (!BandolierBanditBuffState.IsHeldByMainPlayer(__instance)) return;
                if (UnityEngine.Random.value > BandolierBanditBuffState.SaveChance) return;

                MagazineItemClass mag = __instance.GetCurrentMagazine();
                if (mag?.Cartridges == null) return;
                Item last = mag.Cartridges.Last;
                if (last == null) return; // empty mag - nothing to bump
                last.StackObjectsCount += 1;
                last.RaiseRefreshEvent(false, true);
                mag.RaiseRefreshEvent(false, true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[BandolierBandit] OnShot postfix threw: {ex.Message}");
            }
        }
    }

    // --------------------------------------------------------------------
    // PER-TICK MAG TOP-UP
    // --------------------------------------------------------------------
    // when the buff is active and the player's held weapon contains a
    // magazine whose Id differs from the last one we topped up, fill
    // its cartridge stack to the buffed MaxCount. this catches:
    //   - just drank the perk (current mag becomes 40-cap, top up)
    //   - reloaded (new mag becomes in-weapon, top up)
    //   - swapped weapons (different mag, top up)
    //   - looted a fresh weapon (different mag, top up)
    //
    // does NOT run per-shot (that would defeat the firing limit). we only
    // top up when the IN-WEAPON MAG IDENTITY CHANGES. between reloads
    // the mag drains normally; the player rewards come from the 5% save
    // chance.
    //
    // called from Plugin.Update which is the existing per-frame hook used
    // for icon registration polling and QR downed state ticks. cost: 2-3
    // null-checks + one Id compare when not active; one stack-set when
    // a mag-swap happens.
    public static class BandolierBanditTopUpTick
    {
        public static void Tick()
        {
            try
            {
                if (!BandolierBanditBuffState.IsBuffActive()) return;
                Weapon held = BandolierBanditBuffState.GetHeldWeapon();
                if (held == null) return;
                MagazineItemClass mag = held.GetCurrentMagazine();
                if (mag?.Cartridges == null)
                {
                    BandolierBanditBuffState.LastTopppedMagId = null;
                    return;
                }
                if (mag.Id == BandolierBanditBuffState.LastTopppedMagId) return;

                // new mag detected - top it up to the buffed ceiling.
                int target = mag.MaxCount; // already includes the +10 via our postfix
                int cur = mag.Count;
                if (cur < target)
                {
                    Item last = mag.Cartridges.Last;
                    if (last != null)
                    {
                        last.StackObjectsCount += (target - cur);
                        last.RaiseRefreshEvent(false, true);
                        mag.RaiseRefreshEvent(false, true);
                    }
                    // if `last` is null (empty mag), we can't pick an ammo
                    // tpl to refill with - leave it. Max Ammo's empty-mag
                    // path (via BossWeaponRegistry lookup) can rescue it
                    // if the mag is a registered boss-weapon mag.
                }
                BandolierBanditBuffState.LastTopppedMagId = mag.Id;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[BandolierBandit] TopUpTick threw: {ex.Message}");
            }
        }
    }
}
