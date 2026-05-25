using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // performance scrubber for dead zombie corpses. EFT keeps bot ragdolls
    // resident for minutes after death (so loot interactions work + the
    // local ai cleanup queue gets to it eventually), which means in a
    // zombies-mode raid with 24+ kills per wave, dozens of ragdolls
    // accumulate on the map driving renderer + physics + animator cost
    // every frame.
    //
    // we shorten that window aggressively: 15s after a tracked zombie
    // dies, set its GameObject inactive. inspired by jbs4bmx/RemoveTheDead
    // (same SetActive(false) mechanism - safer than Destroy() because
    // any EFT subsystem holding a stale BotOwner reference won't NRE,
    // the object just stops rendering / ticking).
    //
    // gated to infected zombies (Role.IsInfected()) so non-zombie bots
    // (player corpses, debug bots, whatever leaks in) are untouched.
    //
    // detection: we subscribe to BotSpawner.OnBotRemoved (the same hook
    // ZombiesWaveController uses for kill counting). EFT fires this when
    // it cleans the BotOwner off the master list - by then the bot is
    // dead and the GameObject is still resident (corpse). polling the
    // bot list each frame for IsDead was unreliable: in zombies mode,
    // the alive->removed transition can happen in a single frame, so a
    // 2 Hz sweep often missed the dead-but-still-listed window entirely.
    public class ZombieCorpseCleaner : MonoBehaviour
    {
        // seconds after death before we hide the corpse. 15s is short
        // enough to keep ragdolls from piling up but long enough that
        // a player can still loot the body if they want to.
        public const float CorpseLingerSec = 15f;

        // how often we check the pending-corpse list for expiries. cheap
        // (one dict scan + a timestamp compare per entry); 0.5s is fine.
        private const float SweepIntervalSec = 0.5f;

        private sealed class PendingCorpse
        {
            public float DeathTime;
            public GameObject Go;
        }

        // keyed by profile id (the same id ZombiesWaveController uses).
        // populated in OnBotRemoved when a zombie dies; consumed in the
        // sweep tick when its timer expires.
        private readonly Dictionary<string, PendingCorpse> _pending =
            new Dictionary<string, PendingCorpse>();

        private float _nextSweepTime;
        private readonly List<string> _tmpExpired = new List<string>();
        private bool _hookedSpawner;
        private BotSpawner _hookedRef;

        private void OnEnable()
        {
            // try to hook the spawner immediately - if the BotsController
            // isn't up yet, the Update loop will retry until it is.
            TryHookSpawner();
        }

        private void OnDisable()
        {
            UnhookSpawner();
        }

        private void Update()
        {
            if (!_hookedSpawner) TryHookSpawner();

            if (Time.unscaledTime < _nextSweepTime) return;
            _nextSweepTime = Time.unscaledTime + SweepIntervalSec;

            try { ProcessExpired(); }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[CorpseCleaner] sweep threw: {ex.Message}");
            }
        }

        private void TryHookSpawner()
        {
            try
            {
                IBotGame botGame = Singleton<IBotGame>.Instance;
                BotSpawner spawner = botGame?.BotsController?.BotSpawner;
                if (spawner == null) return;
                spawner.OnBotRemoved += OnBotRemovedHandler;
                _hookedSpawner = true;
                _hookedRef = spawner;
                Plugin.LogSource?.LogInfo("[CorpseCleaner] hooked BotSpawner.OnBotRemoved.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[CorpseCleaner] hook threw: {ex.Message}");
            }
        }

        private void UnhookSpawner()
        {
            try
            {
                if (_hookedRef != null)
                    _hookedRef.OnBotRemoved -= OnBotRemovedHandler;
            }
            catch { /* shutdown; ignore */ }
            _hookedSpawner = false;
            _hookedRef = null;
        }

        private void OnBotRemovedHandler(BotOwner bot)
        {
            try
            {
                if (bot == null) return;
                WildSpawnType role = bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (!role.IsInfected()) return;

                string id = bot.Profile?.Id;
                if (string.IsNullOrEmpty(id)) return;
                if (_pending.ContainsKey(id)) return; // dedupe

                Player p = bot.GetPlayer;
                GameObject go = p != null ? p.gameObject : null;
                if (go == null)
                {
                    Plugin.LogSource?.LogInfo($"[CorpseCleaner] no GameObject for '{id}'; skipping.");
                    return;
                }

                _pending[id] = new PendingCorpse
                {
                    DeathTime = Time.unscaledTime,
                    Go = go,
                };
                Plugin.LogSource?.LogInfo($"[CorpseCleaner] tracking corpse '{id}' (hide in {CorpseLingerSec:F0}s).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[CorpseCleaner] OnBotRemoved threw: {ex.Message}");
            }
        }

        // any tracked corpse whose timer has expired gets SetActive(false).
        // _tmpExpired buffers the keys so we don't mutate the dict we're
        // iterating over.
        private void ProcessExpired()
        {
            if (_pending.Count == 0) return;

            _tmpExpired.Clear();
            float now = Time.unscaledTime;
            foreach (var kv in _pending)
            {
                if (now - kv.Value.DeathTime < CorpseLingerSec) continue;
                _tmpExpired.Add(kv.Key);
            }
            for (int i = 0; i < _tmpExpired.Count; i++)
            {
                string id = _tmpExpired[i];
                if (_pending.TryGetValue(id, out PendingCorpse corpse) && corpse.Go != null)
                {
                    try
                    {
                        corpse.Go.SetActive(false);
                        Plugin.LogSource?.LogInfo($"[CorpseCleaner] hid corpse '{id}' after {CorpseLingerSec:F0}s.");
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning($"[CorpseCleaner] SetActive(false) threw for '{id}': {ex.Message}");
                    }
                }
                _pending.Remove(id);
            }
        }
    }
}
