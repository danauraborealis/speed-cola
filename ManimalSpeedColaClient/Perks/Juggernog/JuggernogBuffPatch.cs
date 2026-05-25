using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // when the "juggernog" stimulator buff is active on the local main player,
    // scale incoming damage by 0.9 (10% reduction). this is one of three perk
    // effects - the other two are +150 total max HP (applied once at first buff
    // activation, see JuggernogHpBoostMonitor) and a 5 HP/sec regen (driven by
    // a HealthRate effect in the server-side buff json).
    //
    // prefix on ActiveHealthController.ApplyDamage(EBodyPart, ref float damage,
    // DamageInfoStruct) - that method is the canonical damage entry point;
    // it forwards to ChangeHealth(part, -damage, info). modifying `damage`
    // here also affects every downstream effect that reads off it (events,
    // metrics, falling damage, etc).
    //
    // we gate on `__instance == Singleton<GameWorld>.MainPlayer.ActiveHealthController`
    // so AI bots aren't affected even though they all run ApplyDamage paths.
    internal static class JuggernogBuffState
    {
        public const string BuffName = "juggernog";
        // multiplier applied to incoming damage. 0.9 = take 90% damage = 10%
        // reduction. hardcoded canonical value, no F12 knob.
        public const float DamageMultiplier = 0.9f;
        // applied once when the buff first becomes active on the player.
        // distributed evenly across body parts by ZombieHealthBooster.Apply.
        public const float MaxHpBonus = 150f;

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
    }

    public sealed class JuggernogApplyDamagePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));

        [PatchPrefix]
        private static void Prefix(ActiveHealthController __instance, ref float damage)
        {
            try
            {
                GameWorld gw = Singleton<GameWorld>.Instance;
                Player main = gw?.MainPlayer;
                if (main == null) return;
                // only scale damage taken by the local player - AI use the same
                // ApplyDamage code path so we'd otherwise nerf zombies too.
                if (main.ActiveHealthController != __instance) return;
                if (!JuggernogBuffState.IsBuffActive()) return;

                damage *= JuggernogBuffState.DamageMultiplier;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Juggernog] ApplyDamage prefix threw: {ex.Message}");
            }
        }
    }

    // suppresses pain on the local player while the juggernog buff is active.
    // prefixes the internal AddEffect<Pain> wrapper (method_27 on
    // ActiveHealthController) and short-circuits when our buff is up. effect:
    // damage still applies (ApplyDamage runs first, scaled by JuggernogApplyDamagePatch)
    // but the screen-shake / hand-tremor pain effect never gets created.
    //
    // we deliberately do NOT use the item's effects_damage.Pain entry for this -
    // that path registers a vanilla "painkiller" effect with its own canonical
    // pill icon in the effects panel, which would shadow our custom juggernog
    // sprite. doing it client-side means only the juggernog buff icon shows.
    public sealed class JuggernogPainSuppressPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController), "method_27");

        [PatchPrefix]
        private static bool Prefix(ActiveHealthController __instance)
        {
            try
            {
                GameWorld gw = Singleton<GameWorld>.Instance;
                Player main = gw?.MainPlayer;
                if (main == null) return true;
                if (main.ActiveHealthController != __instance) return true;
                if (!JuggernogBuffState.IsBuffActive()) return true;
                return false; // skip original - no pain effect added
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Juggernog] PainSuppress prefix threw: {ex.Message}");
                return true; // on error, let original run
            }
        }
    }

    // replaces Player.UpdateSpeedLimitByHealth while Juggernog is active.
    //
    // vanilla flow (see decompiled Player.cs:5255-5280):
    //   1. SetOnPainkillers from FindActiveEffect<GInterface358/350>
    //      (real painkillers/morphine only - Juggernog isn't either)
    //   2. Set leg-damaged flags from broken/destroyed checks
    //   3. RemoveStateSpeedLimit(HealthCondition)
    //   4. if (legDamaged) {
    //         if (!OnPainkillers) {
    //            MovementContext.EnableSprint(false)   <- forces sprint OFF
    //            AddStateSpeedLimit(0.2 or 0.3, HealthCondition)
    //         }
    //         if (sprinting) StartInflictSelfDamageCoroutine()
    //      }
    //
    // why we had to replace, not postfix: MovementContext.EnableSprint(bool)
    // doesn't just toggle the "can sprint" flag - it calls
    // `_player.Physical.Sprint(enable)`. so the previous postfix calling
    // EnableSprint(true) every tick was actively COMMANDING the player
    // to sprint each frame, which (a) made the avatar try to sprint
    // without input, and (b) cycled stamina up/down because the broken-leg
    // self-damage coroutine fired every time a sprint started.
    //
    // this prefix mirrors the safe parts of vanilla and skips the broken-leg
    // penalty branch entirely. for the edge case where the player broke their
    // legs BEFORE drinking Juggernog (and vanilla already disabled sprint),
    // we re-arm sprint exactly once per Juggernog activation via the
    // _sprintEnsured latch.
    public sealed class JuggernogPainkillerStatePatch : ModulePatch
    {
        // one-shot: when Juggernog activates, call EnableSprint(true) once
        // to undo any prior vanilla EnableSprint(false). reset when buff
        // ends so a re-application re-arms.
        private static bool _sprintEnsured;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.UpdateSpeedLimitByHealth));

        [PatchPrefix]
        private static bool Prefix(Player __instance)
        {
            try
            {
                GameWorld gw = Singleton<GameWorld>.Instance;
                if (gw == null || gw.MainPlayer != __instance) return true;
                if (!JuggernogBuffState.IsBuffActive())
                {
                    // arm the latch for next activation, then let vanilla run.
                    _sprintEnsured = false;
                    return true;
                }

                var mc = __instance.MovementContext;
                if (mc == null) return true;
                var hc = __instance.HealthController;
                if (hc == null) return true;

                // mirror vanilla lines 5257-5260, minus the OnPainkillers-from-
                // real-effect check (we force true) and the leg-penalty branch
                // (we skip it).
                mc.SetPhysicalCondition(EPhysicalCondition.OnPainkillers, true);
                mc.SetPhysicalCondition(
                    EPhysicalCondition.RightLegDamaged,
                    hc.IsBodyPartBroken(EBodyPart.RightLeg) || hc.IsBodyPartDestroyed(EBodyPart.RightLeg));
                mc.SetPhysicalCondition(
                    EPhysicalCondition.LeftLegDamaged,
                    hc.IsBodyPartBroken(EBodyPart.LeftLeg) || hc.IsBodyPartDestroyed(EBodyPart.LeftLeg));
                __instance.RemoveStateSpeedLimit(Player.ESpeedLimit.HealthCondition);

                // re-enable sprint exactly once per Juggernog activation. handles
                // the "broke legs, THEN drank Juggernog" path where vanilla already
                // called EnableSprint(false). idempotent if already enabled.
                if (!_sprintEnsured)
                {
                    mc.EnableSprint(true);
                    _sprintEnsured = true;
                }

                return false; // skip vanilla
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Juggernog] PainkillerState prefix threw: {ex.Message}");
                return true;
            }
        }
    }
}
