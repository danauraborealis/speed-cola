using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // CoD-Zombies "Quick Revive" auto-revive: prefix on ActiveHealthController.
    // Kill that aborts the death and restores the main player to full HP
    // if they have a Quick Revive charge available.
    //
    // gates:
    //   - target must be the local main player's health controller
    //     (we don't want to revive bots or other players)
    //   - QuickReviveBuffState.IsBuffActive() must be true (player has
    //     the quickrevive stimulator buff, i.e. drank a Quick Revive
    //     this raid)
    //   - QuickReviveState.TryConsumeCharge() must succeed (one charge
    //     per drink; consumed atomically here to prevent double-revive
    //     on the same Kill call)
    //
    // restoration: every body part is FullRestoreBodyPart'd, which
    // un-destroys (clears the IsDestroyed flag) AND sets HP to max.
    // similar to a vanilla CMS/Surv12 surgical kit but without the
    // healthPenalty - QuickRevive is meant to be the strong CoD-style
    // full revive. balance is the per-drink TC cost (1500 default) plus
    // a full perk-wipe (see WipePerkBuffs below): every active perk
    // stimulator gets ForceRemove'd including Quick Revive itself, so
    // the player has to re-buy every perk they want back.
    //
    // pattern lifted from BringMeToLifeMod's DeathPatch.Prefix - same
    // hook point, same return-false-to-block-death technique.
    public class QuickReviveKillInterceptPatch : ModulePatch
    {
        // every perk buff our mod can put on the player. matches the BuffName
        // const on each perk's *BuffState / *BuffIconPatch. case-insensitive
        // because EFT's ActiveBuffsNames() returns the raw json string.
        private static readonly HashSet<string> _perkBuffNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "speedcola",
                JuggernogBuffState.BuffName,        // "juggernog"
                "staminup",
                DeathPerceptionBuffState.BuffName,  // "deathperception"
                QuickReviveBuffState.BuffName,      // "quickrevive"
                DeadshotDaiquiriBuffState.BuffName, // "deadshotdaiquiri"
                DoubleTapBuffState.BuffName,        // "doubletap"
            };

        // post-revive grace window. covers the gap between QuickReviveDownedState.Exit
        // (the prone get-up moment) and the player being fully back to
        // normal play - lingering same-frame Kill calls or AI burst damage
        // would otherwise re-kill them with the buff already wiped. set to
        // (downedDuration + a few seconds) on Enter so the window spans
        // BOTH the down period and the immediate get-up grace.
        private const float PostReviveGraceSec = 3.0f;
        private static float _postReviveInvulnUntil;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.Kill));

        [PatchPrefix]
        private static bool Prefix(ActiveHealthController __instance, EDamageType damageType)
        {
            try
            {
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main == null) return true;
                if (main.ActiveHealthController != __instance) return true;

                // already in the downed state: block ANY new Kill on the
                // main player. damage is also SetDamageCoeff(0) zeroed so
                // this is mostly belt-and-suspenders for paths that bypass
                // the damage coeff (instakill calls, scripted death events).
                if (QuickReviveDownedState.IsDowned)
                {
                    try { __instance.FullRestoreBodyPart(EBodyPart.Head); } catch { }
                    try { __instance.FullRestoreBodyPart(EBodyPart.Chest); } catch { }
                    Plugin.LogSource?.LogInfo($"[QuickRevive] downed state blocked Kill (from {damageType}).");
                    return false;
                }

                // post-revive grace: covers the few seconds after Exit when
                // lingering same-frame Kill calls might otherwise punch
                // through. multi-zombie melee can land chest-then-head
                // destruction in the same frame and we don't want the get-
                // up moment to be immediately undone.
                if (Time.unscaledTime < _postReviveInvulnUntil)
                {
                    try { __instance.FullRestoreBodyPart(EBodyPart.Head); } catch { }
                    try { __instance.FullRestoreBodyPart(EBodyPart.Chest); } catch { }
                    Plugin.LogSource?.LogInfo($"[QuickRevive] post-revive grace blocked Kill (from {damageType}).");
                    return false;
                }

                if (!QuickReviveBuffState.IsBuffActive())
                {
                    return true;
                }
                if (!QuickReviveState.TryConsumeCharge())
                {
                    // buff present but charge already burned this raid -
                    // let them die.
                    return true;
                }

                // restore every body part to full HP (clears destroyed
                // flag on head/chest too, so the player is fully back).
                foreach (EBodyPart part in (EBodyPart[])Enum.GetValues(typeof(EBodyPart)))
                {
                    if (part == EBodyPart.Common) continue;
                    try { __instance.FullRestoreBodyPart(part); }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning($"[QuickRevive] FullRestoreBodyPart({part}) threw: {ex.Message}");
                    }
                }

                // CoD-authentic perk wipe: lose every perk you bought,
                // including Quick Revive itself. buff-gated perk effects
                // (Speed Cola drink speed, Juggernog damage reduction +
                // pain suppress + painkiller state, Staminup stamina
                // regen, Death Perception xray, Quick Revive's own charge
                // arming) drop out the moment the underlying stimulator
                // is removed. Juggernog's +150 max HP is the one effect
                // not buff-gated - we reset its monitor explicitly so
                // the HP boost reverses AND re-arms for a future re-buy.
                WipePerkBuffs(__instance);
                ResetJuggernogHpBoost();

                // enter the CoD-style downed state: prone, slow crawl,
                // weapons disabled, awareness zeroed, damage zeroed, every
                // zombie's pursuit cleared. timer auto-revives after
                // QuickReviveDownedDurationSec (Plugin.Update calls Tick).
                float downedDuration = Plugin.QuickReviveDownedDurationSec != null
                    ? Plugin.QuickReviveDownedDurationSec.Value
                    : 4f;
                QuickReviveDownedState.Enter(main, downedDuration);

                // post-revive grace runs from now through downedDuration +
                // PostReviveGraceSec - covers the whole down window plus a
                // few seconds after Exit when lingering damage might leak.
                _postReviveInvulnUntil = Time.unscaledTime + downedDuration + PostReviveGraceSec;
                Plugin.LogSource?.LogInfo($"[QuickRevive] auto-revive triggered (would-die from {damageType}); entered DOWNED state for {downedDuration:F1}s + {PostReviveGraceSec:F1}s grace.");
                return false; // block the original Kill
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[QuickRevive] Kill prefix threw: {ex.Message}");
                return true; // safe-default: let the original Kill run
            }
        }

        // cached reflection handles. resolved once per process - perk wipe
        // is a once-per-revive event, but the cache also doubles as a
        // null-check so we dont retry resolution if a previous probe failed.
        private static MethodInfo _findActiveEffectsGeneric;
        private static MethodInfo _findActiveEffectsForIEffect;
        private static bool _resolvedFind;

        // walks every active Stimulator effect on the player's Common body
        // part and ForceRemove's the ones whose Name matches a perk we own.
        // Stimulator is a protected nested class on ActiveHealthController
        // and ForceRemove lives on its grandparent (the GClass3008 effect
        // base), so this is all reflection. we materialize the effect list
        // before iterating because ForceRemove mutates the underlying
        // FindActiveEffects collection mid-walk.
        private static void WipePerkBuffs(ActiveHealthController hc)
        {
            try
            {
                if (!_resolvedFind)
                {
                    // FindActiveEffects<TEffect>(EBodyPart) is declared on the
                    // GClass3009<T> generic base, not on ActiveHealthController
                    // itself. walk up the type hierarchy to find it.
                    Type t = typeof(ActiveHealthController);
                    while (t != null && _findActiveEffectsGeneric == null)
                    {
                        _findActiveEffectsGeneric = t
                            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(m =>
                                m.Name == "FindActiveEffects" &&
                                m.IsGenericMethodDefinition &&
                                m.GetParameters().Length == 1);
                        t = t.BaseType;
                    }
                    if (_findActiveEffectsGeneric != null)
                        _findActiveEffectsForIEffect = _findActiveEffectsGeneric.MakeGenericMethod(typeof(IEffect));
                    _resolvedFind = true;
                }

                if (_findActiveEffectsForIEffect == null)
                {
                    Plugin.LogSource?.LogWarning("[QuickRevive] could not resolve FindActiveEffects<IEffect> - perk wipe skipped.");
                    return;
                }

                object raw = _findActiveEffectsForIEffect.Invoke(hc, new object[] { EBodyPart.Common });
                if (raw == null) return;

                List<object> snapshot = new List<object>();
                foreach (object e in (IEnumerable)raw)
                    if (e != null) snapshot.Add(e);

                int removed = 0;
                foreach (object effect in snapshot)
                {
                    string name = ReadEffectName(effect);
                    if (name == null || !_perkBuffNames.Contains(name)) continue;
                    if (InvokeForceRemove(effect))
                    {
                        removed++;
                        Plugin.LogSource?.LogInfo($"[QuickRevive] wiped perk buff '{name}'.");
                    }
                }
                Plugin.LogSource?.LogInfo($"[QuickRevive] perk wipe done: removed {removed} buff(s).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] perk wipe threw: {ex.Message}");
            }
        }

        private static string ReadEffectName(object effect)
        {
            try
            {
                // Stimulator.Name is a public string get-only property on the
                // (protected) nested class - GetType resolves the concrete
                // runtime type, so per-instance GetProperty works.
                PropertyInfo nameProp = effect.GetType().GetProperty(
                    "Name", BindingFlags.Public | BindingFlags.Instance);
                return nameProp?.GetValue(effect) as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool InvokeForceRemove(object effect)
        {
            try
            {
                // ForceRemove is `public virtual void ForceRemove()` on the
                // GClass3008 effect base (see SPT decompile line ~2105).
                // Stimulator inherits it - resolve via the concrete type.
                MethodInfo mi = effect.GetType().GetMethod(
                    "ForceRemove",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                if (mi == null) return false;
                mi.Invoke(effect, null);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] ForceRemove threw: {ex.Message}");
                return false;
            }
        }

        // Juggernog's +150 max HP isn't carried by the stimulator buff -
        // it's applied once by JuggernogHpBoostMonitor with a latch. find
        // the monitor (lives on a GameWorld child host set up by
        // SpawnJuggernogOnGameStartedPatch) and reset it so the boost
        // reverses and re-arms.
        private static void ResetJuggernogHpBoost()
        {
            try
            {
                GameWorld gw = Singleton<GameWorld>.Instance;
                if (gw == null) return;
                JuggernogHpBoostMonitor monitor = gw.GetComponentInChildren<JuggernogHpBoostMonitor>();
                if (monitor == null) return;
                monitor.Reset();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] Juggernog HP reset threw: {ex.Message}");
            }
        }
    }
}
