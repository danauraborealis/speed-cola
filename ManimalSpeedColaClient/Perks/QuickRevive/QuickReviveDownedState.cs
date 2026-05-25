using System;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using Manimal.SpeedCola.Components;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // CoD-Zombies-style downed state for the Quick Revive perk. when the
    // player takes a fatal hit with a charge armed:
    //   - body parts are full-restored so they don't actually die
    //   - perks are wiped (handled by QuickReviveKillInterceptPatch)
    //   - we Enter() this state: force prone, slow crawl, weapons disabled,
    //     player Awareness zeroed, damage coefficient set to 0
    //   - every infected zombie's pursuit is cleared so they wander idle
    //     ("you're dead to them")
    //   - after DownedDurationSec, Tick() auto-revives: restores everything
    //     captured at Enter, re-calls method_8 to re-engage every zombie so
    //     they resume the chase the moment the player gets back up
    //
    // mirror of RevivalMod's ApplyRevivableState / ProcessCriticalState /
    // RestorePlayerMovement flow, simplified to a solo single-player path
    // with no defib item, no Fika networking, no teammate revives.
    public static class QuickReviveDownedState
    {
        public static bool IsDowned { get; private set; }
        // unscaled seconds remaining until the self-revive fires. 0 when
        // not downed. read by the QuickReviveDownedOverlay for the on-screen
        // countdown.
        public static float RemainingSec => IsDowned ? Mathf.Max(0f, _restoreAt - Time.unscaledTime) : 0f;

        private static Player _player;
        // captured at Enter so Exit restores the EXACT pre-down value rather
        // than baking in our overrides as the new "normal".
        private static float _originalAwareness = -1f;
        private static float _originalWalkSpeedLimit = -1f;
        private static float _restoreAt;
        private static QuickReviveDownedOverlay _overlay;

        // movement speed multiplier while downed. matches RevivalMod's
        // 0.1f - slow enough that the player can wiggle but can't run.
        private const float DownedMovementSpeed = 0.1f;
        // contusion intensity passed to ActiveHealthController.DoContusion -
        // 1f is the max blur/wobble. EFT's contusion effect handles its own
        // timed falloff so we just trigger it once at Enter.
        private const float DownedContusionStrength = 1f;

        public static void Enter(Player player, float durationSec)
        {
            if (player == null) return;
            if (IsDowned)
            {
                // re-Enter during the same down window (e.g. a follow-up
                // fatal hit in the same frame): extend the timer to the new
                // duration but don't re-capture originals or re-apply (we're
                // already in the locked state).
                _restoreAt = Time.unscaledTime + durationSec;
                return;
            }

            _player = player;
            _restoreAt = Time.unscaledTime + durationSec;

            try { _originalAwareness = player.Awareness; } catch { _originalAwareness = -1f; }
            try { _originalWalkSpeedLimit = player.Physical != null ? player.Physical.WalkSpeedLimit : -1f; }
            catch { _originalWalkSpeedLimit = -1f; }

            try
            {
                // SetDamageCoeff(0) makes all incoming damage * 0 while
                // downed, so we don't have to prefix ApplyDamage. on Exit
                // we restore to 1 (vanilla baseline). also un-destroys
                // head/chest if they were destroyed by the fatal hit that
                // put us here - safety in case the kill prefix didn't get
                // to FullRestoreBodyPart everything.
                player.ActiveHealthController?.SetDamageCoeff(0f);

                // EFT-native contusion screen wobble for the duration of
                // the down. canonical "you got hit hard" effect - blur,
                // muffled audio, slight camera wobble. matches the
                // CRITICAL_STATE feel in RevivalMod's ApplyCriticalEffects.
                player.ActiveHealthController?.DoContusion(durationSec, DownedContusionStrength);
            }
            catch { }

            ApplyDownedRestrictions();
            ClearAllZombiePursuit();
            ResetPerkMachines();
            SpawnOverlay(player);

            IsDowned = true;
            Plugin.LogSource?.LogInfo($"[QuickRevive] DOWNED (self-revive in {durationSec:F1}s, zombies disengaged).");
        }

        // attach the OnGUI overlay as a child of the GameWorld root so it
        // lives across MainPlayer respawn weirdness and is cleanly destroyed
        // at raid teardown. nukes any leftover overlay from a re-Enter to
        // avoid double-rendering.
        private static void SpawnOverlay(Player player)
        {
            try
            {
                if (_overlay != null) { UnityEngine.Object.Destroy(_overlay.gameObject); _overlay = null; }
                GameWorld gw = Singleton<GameWorld>.Instance;
                Transform parent = gw != null ? gw.transform : null;
                GameObject host = new GameObject("QuickReviveDownedOverlay");
                if (parent != null) host.transform.SetParent(parent, false);
                _overlay = host.AddComponent<QuickReviveDownedOverlay>();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] SpawnOverlay threw: {ex.Message}");
            }
        }

        private static void DestroyOverlay()
        {
            try
            {
                if (_overlay != null)
                {
                    UnityEngine.Object.Destroy(_overlay.gameObject);
                    _overlay = null;
                }
            }
            catch { }
        }

        public static void Exit()
        {
            if (!IsDowned) return;
            try
            {
                if (_player != null)
                {
                    // restore damage coefficient first - any frames between
                    // here and full restore should take normal damage so
                    // the player can't game extended invuln.
                    try { _player.ActiveHealthController?.SetDamageCoeff(1f); } catch { }
                    RestorePlayerState();
                    ReEngageZombies();
                }
                DestroyOverlay();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] Exit threw: {ex.Message}");
            }
            IsDowned = false;
            _player = null;
            _originalAwareness = -1f;
            _originalWalkSpeedLimit = -1f;
            Plugin.LogSource?.LogInfo("[QuickRevive] REVIVED - back on your feet.");
        }

        // called once per frame from Plugin.Update. cheap when not downed
        // (single bool check + early return).
        public static void Tick()
        {
            if (!IsDowned) return;
            if (_player == null) { Exit(); return; }

            // re-apply restrictions every frame - the game's movement /
            // pose / weapon systems can clobber our overrides between
            // Enter and the next Tick (e.g. the player presses prone-key
            // again, an Animator state advances the pose level, etc.).
            ApplyDownedRestrictions();

            // re-clear zombie pursuit every frame to handle:
            //   - bots whose GoalEnemy got reset by some other AI tick
            //   - NEW zombies that spawn mid-down (waves can still trickle
            //     in while we're down) - they need to be told "ignore me"
            //     too, otherwise they'd come straight at the prone player
            ClearAllZombiePursuit();

            if (Time.unscaledTime >= _restoreAt)
                Exit();
        }

        public static void ResetForNewRaid()
        {
            DestroyOverlay();
            IsDowned = false;
            _player = null;
            _originalAwareness = -1f;
            _originalWalkSpeedLimit = -1f;
            _restoreAt = 0f;
        }

        // mirror of RevivalMod ApplyRevivableState + ProcessCriticalState.
        // force prone, zero out movement + awareness + aim, disable sprint.
        private static void ApplyDownedRestrictions()
        {
            if (_player == null) return;
            try
            {
                _player.Awareness = 0f;
                if (_player.Physical != null) _player.Physical.WalkSpeedLimit = DownedMovementSpeed;
                var mc = _player.MovementContext;
                if (mc != null)
                {
                    mc.SetPoseLevel(0f, true);
                    try { mc.IsInPronePose = true; } catch { }
                    mc.EnableSprint(false);
                }
                if (_player.HandsController != null) _player.HandsController.IsAiming = false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] ApplyDownedRestrictions threw: {ex.Message}");
            }
        }

        // mirror of RevivalMod RemoveRevivableState + RestorePlayerMovement.
        private static void RestorePlayerState()
        {
            if (_player == null) return;
            try
            {
                if (_originalAwareness >= 0f)
                    _player.Awareness = _originalAwareness;
                if (_originalWalkSpeedLimit > 0f && _player.Physical != null)
                    _player.Physical.WalkSpeedLimit = _originalWalkSpeedLimit;

                var mc = _player.MovementContext;
                if (mc != null)
                {
                    try { mc.IsInPronePose = false; } catch { }
                    mc.SetPoseLevel(1f);
                    mc.EnableSprint(true);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] RestorePlayerState threw: {ex.Message}");
            }
        }

        // make every infected zombie forget the player. clears two layers:
        //   1. BotHalloweenWithZombies.ActivePursuits - the system that
        //      drives the wave-controller's forced chase. method_9 removes
        //      a pursuit; we call it for every pursuit targeting our
        //      MainPlayer.
        //   2. BotOwner.Memory.GoalEnemy - the per-bot target reference
        //      that drives engagement/shooting. nulling it returns the bot
        //      to idle/wander (canonical EFT pattern, see BotCalcGoal.cs).
        private static void ClearAllZombiePursuit()
        {
            try
            {
                IBotGame botGame = Singleton<IBotGame>.Instance;
                var botsController = botGame?.BotsController;
                var bots = botsController?.Bots;
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;

                // layer 1: pursuit list
                var bhwz = botsController?.EventsController?.BotHalloweenWithZombies;
                if (bhwz != null && main != null && bhwz.ActivePursuits != null)
                {
                    for (int i = bhwz.ActivePursuits.Count - 1; i >= 0; i--)
                    {
                        var pursuit = bhwz.ActivePursuits[i];
                        try
                        {
                            // can't peek Target without reflection (GClass674
                            // is obfuscated), so use the public method_9
                            // removal hook on each pursuit. nukes ALL active
                            // pursuits - fine for solo, only the player is
                            // ever a pursuit target in our zombie raids.
                            bhwz.method_9(pursuit);
                        }
                        catch { }
                    }
                }

                // layer 2: per-bot GoalEnemy null
                if (bots?.BotOwners != null)
                {
                    foreach (BotOwner bot in bots.BotOwners)
                    {
                        if (bot == null || bot.IsDead) continue;
                        WildSpawnType role = bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                        if (!role.IsInfected()) continue;
                        try
                        {
                            if (bot.Memory != null) bot.Memory.GoalEnemy = null;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] ClearAllZombiePursuit threw: {ex.Message}");
            }
        }

        // un-sells every single-use perk machine on the map so the player
        // can re-buy each perk after the QR wipe. Quick Revive's own
        // machine is intentionally NOT reset - it has its own multi-use
        // cap (UseCount up to MaxUses, then physical despawn) and shouldn't
        // be re-armed by going down.
        //
        // uses Object.FindObjectsOfType so we catch any instance on the
        // map regardless of which spawn patch put it there. cheap - runs
        // once per Enter (i.e. per fatal hit, not every frame).
        private static void ResetPerkMachines()
        {
            try
            {
                foreach (var inst in UnityEngine.Object.FindObjectsOfType<SpeedColaInstance>())
                    if (inst != null) inst.ResetSold();
                foreach (var inst in UnityEngine.Object.FindObjectsOfType<JuggernogInstance>())
                    if (inst != null) inst.ResetSold();
                foreach (var inst in UnityEngine.Object.FindObjectsOfType<StaminupInstance>())
                    if (inst != null) inst.ResetSold();
                foreach (var inst in UnityEngine.Object.FindObjectsOfType<DeathPerceptionInstance>())
                    if (inst != null) inst.ResetSold();
                Plugin.LogSource?.LogInfo("[QuickRevive] re-enabled perk machines (SC/Jug/Stam/DP) so player can re-buy after revive.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] ResetPerkMachines threw: {ex.Message}");
            }
        }

        // counterpart to ClearAllZombiePursuit. called from Exit so every
        // zombie on the map re-engages the player the moment they stand
        // back up - matches the CoD-Zombies feel where the swarm collapses
        // back on you as soon as the revive finishes.
        private static void ReEngageZombies()
        {
            try
            {
                IBotGame botGame = Singleton<IBotGame>.Instance;
                var botsController = botGame?.BotsController;
                var bots = botsController?.Bots;
                var bhwz = botsController?.EventsController?.BotHalloweenWithZombies;
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (bhwz == null || bots?.BotOwners == null || main == null) return;

                foreach (BotOwner bot in bots.BotOwners)
                {
                    if (bot == null || bot.IsDead) continue;
                    WildSpawnType role = bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                    if (!role.IsInfected()) continue;
                    try { bhwz.method_8(bot, main); } catch { }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[QuickRevive] ReEngageZombies threw: {ex.Message}");
            }
        }
    }
}
