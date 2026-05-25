using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.Animations.NewRecoil;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // Deadshot Daiquiri perk:
    //   - 50% reduced horizontal + vertical recoil
    //   - +20% ergonomics
    //   - zero weapon sway while ADS (forced hold-breath state)
    //
    // perk is exclusively obtained from supply drops (no machine). buff
    // application is the standard SPT stim flow: drinking the custom item
    // adds "deadshotdaiquiri" to ActiveBuffsNames via the server-side
    // CustomBuffs entry, and these patches check that buff each time the
    // relevant EFT method runs.
    //
    // CAUTION on patch targets - identified via decompiled SPT 4.0.13 at
    // D:\SPT400_assembly. behavior assumed:
    //   - NewRecoilShotEffect.AddRecoilForce(float) - per-shot recoil entry
    //     point. multiplier applied here affects both vertical and horizontal
    //     because both rotation+position kicks downstream scale off the
    //     incomingForce. (verified by agent inspection - the H/V split
    //     happens inside method_1 from finalRecoilRadian.)
    //   - Weapon.ErgonomicsTotal - getter that returns base * (1 + delta).
    //     postfix that multiplies __result lifts the effective ergo. NOTE:
    //     some EFT systems snapshot ergo on weapon change rather than reading
    //     it live, so the buff may need a weapon-swap to actually engage for
    //     stat-derived effects. visible UI tooltip will reflect the buff.
    //   - PlayerPhysicalClass.HoldingBreath getter - reading TRUE here makes
    //     BreathEffector.Process route through the no-sway branch. forcing
    //     true while the player ADSes gives them held-breath-forever steadiness.
    //     does NOT drain oxygen because oxygen consumption keys off
    //     Oxygen.Consumptions, not the getter.
    internal static class DeadshotDaiquiriBuffState
    {
        public const string BuffName = "deadshotdaiquiri";
        public const float RecoilMultiplier = 0.5f; // 50% recoil reduction (both H + V)
        public const float ErgoMultiplier = 1.2f;   // +20% ergonomics

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
                // health controller not ready (menu / loading); not active.
            }
            return false;
        }

        // helper used by the ergo patch: is this Weapon currently held by the
        // local main player? gates the ergo buff so AI weapons and inventory
        // tooltips on un-held items aren't affected. relies on the firearm
        // hands controller exposing the held Item - mirrors how
        // BarWallbuyActionPatch.GetHeldWeaponSlot probes hands controller.
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

        // helper used by sway patch: is main player ADS right now? checks
        // ProceduralWeaponAnimation.IsAiming via the firearm controller.
        public static bool IsMainPlayerAiming()
        {
            try
            {
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main == null) return false;
                Player.FirearmController fc = main.HandsController as Player.FirearmController;
                if (fc == null) return false;
                return fc.IsAiming;
            }
            catch { return false; }
        }
    }

    // --------------------------------------------------------------------
    // RECOIL
    // --------------------------------------------------------------------
    // NewRecoilShotEffect.AddRecoilForce(float incomingForce):
    //   - sole per-shot recoil entry point. internal method_1 reads
    //     rotation+position recoil from this same value.
    //   - gate by __instance.FirearmController.Player.IsYourPlayer so AI
    //     bots' shots aren't softened (they also fire through this code
    //     path on their local FirearmControllers).
    public sealed class DeadshotDaiquiriRecoilPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(NewRecoilShotEffect),
                nameof(NewRecoilShotEffect.AddRecoilForce));
        }

        [PatchPrefix]
        private static void Prefix(NewRecoilShotEffect __instance, ref float incomingForce)
        {
            try
            {
                // gate to main player so AI bot recoil isn't softened.
                // Player.FirearmController doesn't expose its owning Player
                // back-reference publicly, so we compare via identity instead:
                // if MainPlayer.HandsController IS this same FirearmController,
                // this shot is the main player's.
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main == null) return;
                if (__instance?.FirearmController == null) return;
                if (!ReferenceEquals(main.HandsController, __instance.FirearmController)) return;
                if (!DeadshotDaiquiriBuffState.IsBuffActive()) return;
                incomingForce *= DeadshotDaiquiriBuffState.RecoilMultiplier;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Deadshot] recoil prefix threw: {ex.Message}");
            }
        }
    }

    // --------------------------------------------------------------------
    // ERGONOMICS
    // --------------------------------------------------------------------
    // Weapon.ErgonomicsTotal getter:
    //   - returns Template.Ergonomics * (1 + ErgonomicsDelta).
    //   - postfix multiplies __result by ErgoMultiplier when the buff is
    //     active AND the weapon is the main player's currently-held weapon.
    //   - CAVEAT: ergonomics may be snapshotted by ProceduralWeaponAnimation
    //     on weapon equip rather than read live. if the buff doesn't feel
    //     like it's affecting handling for an already-held weapon, swap
    //     weapons after drinking and swap back. follow-up if this is a
    //     persistent issue: force a stats recalc by calling whatever
    //     "RefreshStats"-style method PWA exposes when the buff activates.
    public sealed class DeadshotDaiquiriErgoPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(Weapon), nameof(Weapon.ErgonomicsTotal));
        }

        [PatchPostfix]
        private static void Postfix(Weapon __instance, ref float __result)
        {
            try
            {
                if (!DeadshotDaiquiriBuffState.IsBuffActive()) return;
                if (!DeadshotDaiquiriBuffState.IsHeldByMainPlayer(__instance)) return;
                __result *= DeadshotDaiquiriBuffState.ErgoMultiplier;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Deadshot] ergo postfix threw: {ex.Message}");
            }
        }
    }

    // --------------------------------------------------------------------
    // SWAY (held-breath while ADS)
    // --------------------------------------------------------------------
    // PlayerPhysicalClass.HoldingBreath getter:
    //   - postfix forces TRUE only when the getter is being read FROM
    //     INSIDE BreathEffector.Process. that's the visual-sway consumer;
    //     we want it dampened to the held-breath baseline (BreathIntensity
    //     0.15, ShakeIntensity 0.15).
    //   - we CANNOT force-true unconditionally - RunStateClass.Enter also
    //     reads this getter and, if true, calls MovementContext.EnableSprint(true)
    //     when the player enters a moving state. that caused a "aim then
    //     start moving = randomly sprinting" bug. MovementContext.cs:2673
    //     reads it too, with similar unwanted side effects.
    //   - stack-walk gating limits the override to the breath-effector
    //     call site. cost: one StackTrace alloc per getter call WHILE the
    //     buff is active and the player is ADS, which is ~60Hz. negligible
    //     compared to the per-frame Update cost we already pay.
    //   - oxygen drain still routes through Oxygen.Consumptions, not the
    //     getter, so we don't get unintended stamina debit.
    public sealed class DeadshotDaiquiriSwayPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(PlayerPhysicalClass), nameof(PlayerPhysicalClass.HoldingBreath));
        }

        [PatchPostfix]
        private static void Postfix(PlayerPhysicalClass __instance, ref bool __result)
        {
            try
            {
                if (__result) return; // already true, nothing to do
                if (__instance?.Player_0 == null || !__instance.Player_0.IsYourPlayer) return;
                if (!DeadshotDaiquiriBuffState.IsBuffActive()) return;
                if (!DeadshotDaiquiriBuffState.IsMainPlayerAiming()) return;
                if (!IsCalledFromBreathEffector()) return;
                __result = true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Deadshot] sway postfix threw: {ex.Message}");
            }
        }

        // walks the immediate caller frames (skipping our postfix + Harmony
        // shims) looking for a BreathEffector method. Harmony injects 2-4
        // intermediate frames on its own, so we sweep up to 10 frames to be
        // safe. early-returns the moment a match is found.
        private static bool IsCalledFromBreathEffector()
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(skipFrames: 1, fNeedFileInfo: false);
                int n = Math.Min(trace.FrameCount, 10);
                for (int i = 0; i < n; i++)
                {
                    var m = trace.GetFrame(i)?.GetMethod();
                    var t = m?.DeclaringType;
                    if (t == null) continue;
                    if (t == typeof(BreathEffector)) return true;
                    // Harmony patched-method shim has DeclaringType.Name that
                    // STARTS with the original type name - cover that too in
                    // case Harmony rewrites the frame metadata.
                    string name = t.Name;
                    if (name != null && name.StartsWith("BreathEffector", StringComparison.Ordinal)) return true;
                }
            }
            catch { /* on stack-walk failure, fall through to "not breath effector" */ }
            return false;
        }
    }
}
