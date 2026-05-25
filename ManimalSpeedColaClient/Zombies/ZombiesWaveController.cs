using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // drives the zombies-mode raid: spawns waves of infected bots via
    // BotSpawner.SpawnBotByTypeForce, tracks alive count via OnBotCreated /
    // OnBotRemoved, advances to the next wave once the current one is fully
    // dead.
    //
    // wave sizes follow a 4 -> 9 -> 14 -> 19 -> 24 progression then cap at
    // 24 forever. spawning is staggered (SpawnDelaySec between bots) so the
    // map doesnt explode with all 24 at once.
    //
    // public properties (CurrentWave / TargetCount / RemainingCount / Phase)
    // are read by the HUD overlay each OnGUI tick. they're plain getters - no
    // events, the HUD just polls.
    public class ZombiesWaveController : MonoBehaviour
    {
        // wave-size progression: index = waveNumber - 1, clamped to last entry.
        // waves 1-5 use the early ramp (4 -> 24). past wave 5 the count
        // climbs further but spawning shape changes - we only put 24 on
        // the map at the start of the wave (RollingInitialCap), and each
        // zombie killed schedules 1-2 replacement spawns until the wave's
        // full target count has been dispatched. waves 16+ stay at 44.
        private static readonly int[] WaveCounts =
        {
            4, 9, 14, 19, 24,           // waves 1-5: classic ramp
            27, 28, 28, 29, 33,         // waves 6-10 (10 is the boss wave)
            34, 36, 39, 41, 44,         // waves 11-15
        };

        // past this wave, the dispatch loop only seeds RollingInitialCap
        // bots up front; the rest of the wave's TargetCount comes from
        // replacement spawns dispatched per zombie kill (1-2 per kill).
        public const int RollingSpawnStartWave = 5;
        public const int RollingInitialCap     = 24;
        public const int ReplacementsPerKillMin = 1;
        public const int ReplacementsPerKillMax = 2; // inclusive upper bound for Random.Range int form

        public const int InitialBurstSize = 5;          // zombies that arrive immediately at wave start
        public const float TrickleIntervalSec = 2.0f;   // seconds between trickled zombies after the burst
        public const float BetweenWavesSec = 8.0f;      // after wave clears before next starts
        public const float InitialDelaySec = 3.0f;      // grace period after raid start before wave 1
        public const int SongWaveTrigger = 5;           // wave number that triggers TheOne song
        public const int BossWaveTrigger = 10;          // wave number that spawns infected Tagilla as the first bot

        public enum Phase
        {
            Starting,
            Spawning,
            Active,
            BetweenWaves,
            Stopped,
        }

        public int CurrentWave { get; private set; }
        public int TargetCount { get; private set; }
        // HUD-facing counter: starts at TargetCount when the wave begins
        // and strictly decreases by 1 each time a zombie dies. doesn't
        // wobble during the spawn phase like the old (alive + unconfirmed)
        // formula did when batches arrived in clumps. between waves we
        // pin to 0 so the leftover-display doesn't sit at "6 remaining"
        // when 6 spawns were lost to pipeline saturation.
        public int RemainingCount
        {
            get
            {
                if (CurrentPhase == Phase.BetweenWaves || CurrentPhase == Phase.Stopped) return 0;
                return Mathf.Max(0, TargetCount - _killsThisWave);
            }
        }
        public Phase CurrentPhase { get; private set; } = Phase.Starting;

        private BotSpawner _botSpawner;
        private BotsClass _botsList;
        private readonly HashSet<string> _aliveZombieIds = new HashSet<string>();
        private int _unconfirmedSpawns;
        private int _killsThisWave;
        // total dispatches made in the current wave (initial seeds + any
        // replacement spawns past wave 5). wave is complete when this
        // equals TargetCount AND _unconfirmedSpawns == 0 AND
        // _aliveZombieIds is empty.
        private int _dispatchedTotal;
        // pending replacement-spawn budget accumulated in OnBotRemoved
        // for waves > RollingSpawnStartWave. drained by the Active-phase
        // polling loop, capped at (TargetCount - _dispatchedTotal).
        private int _replacementsBudget;
        private bool _disposed;

        // zombie unstucker. on every check tick (15s) a tracked zombie is
        // teleported if EITHER of these triggers fires AND it's out of LoS:
        //
        //   1. distance > UnstuckMaxDistFromPlayer (zombie wandered too
        //      far - the most common stuck mode is a bot that lost the
        //      player's track and ended up across the map)
        //   2. drifted < UnstuckMovementThreshold meters over the last
        //      UnstuckMovementWindowSec seconds (zombie may be "moving"
        //      according to its AI but isn't making meaningful progress -
        //      jittering against geometry, pacing in a tiny loop, etc.)
        //
        // visibility gate (raycast from player eye to bot torso) suppresses
        // the teleport when the bot is currently in the player's view, so
        // they don't visibly vanish.
        //
        // teleport destination is picked from zone SpawnPoints between
        // Teleport{Min,Max}Dist from the player AND out of LoS.
        private sealed class ZombieTrackInfo
        {
            public BotOwner Bot;
            // baseline position sample for the displacement check. updated
            // every UnstuckMovementWindowSec; current position compared
            // against this. while SampleTime is 0 the zombie is in the
            // initial window and not eligible for the displacement check.
            public Vector3 SamplePosition;
            public float SampleTime;
        }
        private readonly Dictionary<string, ZombieTrackInfo> _zombieTracker = new Dictionary<string, ZombieTrackInfo>();
        private const float UnstuckCheckInterval        = 15f;  // s between scans
        private const float UnstuckMaxDistFromPlayer    = 45f;  // m - zombie farther than this is a teleport candidate
        private const float UnstuckMovementThreshold    = 0.5f; // m - if displacement over the window is below this, stuck
        private const float UnstuckMovementWindowSec    = 30f;  // s - rolling window for the displacement check
        private const float TeleportMinDistFromPlayer = 8f;
        private const float TeleportMaxDistFromPlayer = 25f;
        private float _nextStuckCheckTime;

        // song-end trigger: when a boss-wave song finishes playing, latch
        // _intermissionPending so the NEXT wave-clear transition swaps in
        // a supply-drop spawn + an extended intermission timer instead of
        // the normal short BetweenWavesSec gap. tracked songs are the
        // wave-5 TheOne and the wave-10 Beauty - we hold a list because
        // both can be playing concurrently in theory (long TheOne overlapping
        // into wave 10+).
        private readonly List<AudioSource> _tracksToWatch = new List<AudioSource>();
        private readonly HashSet<AudioSource> _tracksThatEnded = new HashSet<AudioSource>();
        private bool _intermissionPending;
        // intermission window between waves when a song-end triggered it.
        // user-spec'd at 2:30 (the Million intermission song is ~2:15 long,
        // leaving a ~15s tail after the music ends before the next wave).
        public const float IntermissionSec = 150f;
        // breathing room between "wave cleared" and the intermission proper
        // (supply-drop announce + Million song). lets the round-end stinger
        // ring out and gives the player a beat to register the wave just
        // ended before the next phase kicks in. counted INSIDE IntermissionSec
        // so the total wave-clear-to-next-wave-start time is unchanged.
        public const float IntermissionLeadInSec = 8f;
        private const float TrackCheckInterval = 0.5f;
        private float _nextTrackCheckTime;

        // safety: if the wave makes no progress for this long (no new spawns,
        // no kills, no deaths) we assume something jammed and force-advance
        // rather than stranding the player on a dead wave forever. with the
        // per-bot Create fix in place, dispatched-but-unmaterialized should
        // be rare; 30s is plenty for any genuinely late OnBotAdd events to
        // arrive without making the player sit through a 2-minute wait if
        // one slips through.
        private const float MaxStallSec = 30f;

        private void Start()
        {
            CurrentPhase = Phase.Starting;

            // kick off the consumable-bundle warmup IMMEDIATELY at raid
            // start, not from inside RunRaid which sits behind a 3s
            // InitialDelaySec gate. without this, F8-dropped perk bottles
            // drunk in the first 3 seconds of a raid throw
            //   "manimal/speed_cola_container.bundle is not loaded"
            // because the warmup hadn't even begun yet.
            _ = SupplyDropLootTable.WarmupUsePrefabBundlesAsync();

            StartCoroutine(RunRaid());
        }

        private void OnDestroy()
        {
            _disposed = true;
            UnhookSpawner();
        }

        private IEnumerator RunRaid()
        {
            // wait a beat for the bot subsystems to fully wire up before we
            // call SpawnBotByTypeForce - it touches BotsController, BotCreator
            // and SpawnSystem, all of which are populated during OnGameStarted
            // but a frame of slack is cheap insurance.
            yield return new WaitForSeconds(InitialDelaySec);

            if (!HookSpawner())
            {
                Plugin.LogSource?.LogError("[ZombiesWaves] could not locate BotSpawner; controller giving up.");
                CurrentPhase = Phase.Stopped;
                yield break;
            }

            // reset the once-per-raid latch so the song spawns fresh each
            // raid that reaches wave 5.
            TheOneSongBundleLoader.ResetForNewRaid();
            BeautySongBundleLoader.ResetForNewRaid();
            MillionSongBundleLoader.ResetForNewRaid();
            SupplyDropSpawner.ClearForNewRaid();
            // consumable-bundle warmup moved to Start() (above this
            // coroutine) so it fires before InitialDelaySec elapses -
            // F8-dropped perk bottles drunk in the first few seconds
            // would otherwise hit "bundle not loaded" because the
            // warmup hadn't started yet.

            // spawn the Death Perception effect controller as a child of
            // this host. it polls the buff state internally and only does
            // anything when DP is active (or when the F7 force-active dev
            // toggle is on). lives for the whole raid.
            try
            {
                if (GetComponentInChildren<DeathPerceptionEffectController>() == null)
                {
                    GameObject dpHost = new GameObject("DeathPerceptionEffectHost");
                    dpHost.transform.SetParent(transform, false);
                    dpHost.AddComponent<DeathPerceptionEffectController>();
                    Plugin.LogSource?.LogInfo("[DeathPerception] effect controller attached.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] effect controller spawn threw: {ex.Message}");
            }

            // perf: hide zombie corpses 15s after they die. without this,
            // 24+ ragdolls per wave accumulate on the map and tax the
            // renderer + physics. self-contained component that polls
            // BotsController.Bots at 2 Hz, stamps death times, and
            // SetActive(false)'s expired corpses.
            try
            {
                if (GetComponentInChildren<ZombieCorpseCleaner>() == null)
                {
                    GameObject cleanerHost = new GameObject("ZombieCorpseCleanerHost");
                    cleanerHost.transform.SetParent(transform, false);
                    cleanerHost.AddComponent<ZombieCorpseCleaner>();
                    Plugin.LogSource?.LogInfo("[CorpseCleaner] attached.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[CorpseCleaner] spawn threw: {ex.Message}");
            }

            CurrentWave = 0;
            while (!_disposed)
            {
                CurrentWave++;
                TargetCount = WaveCounts[Mathf.Clamp(CurrentWave - 1, 0, WaveCounts.Length - 1)];

                // rolling spawn: past wave 5 only seed RollingInitialCap
                // up front and replenish from kills. waves 1-5 dispatch
                // their full target as before.
                bool rollingWave = CurrentWave > RollingSpawnStartWave;
                int initialDispatchTarget = rollingWave
                    ? Math.Min(TargetCount, RollingInitialCap)
                    : TargetCount;

                _unconfirmedSpawns = initialDispatchTarget;
                _killsThisWave = 0;
                _dispatchedTotal = 0;
                _replacementsBudget = 0;
                _aliveZombieIds.Clear();

                if (rollingWave)
                    Plugin.LogSource?.LogInfo($"[ZombiesWaves] starting wave {CurrentWave}: target={TargetCount}, initial seed={initialDispatchTarget} (replenished on kills).");
                else
                    Plugin.LogSource?.LogInfo($"[ZombiesWaves] starting wave {CurrentWave} with {TargetCount} zombie(s).");

                // round-start audio stinger - fires every wave.
                _ = RoundStartBundleLoader.SpawnUnder(transform);

                // wave-5 song trigger: instantiate TheOne prefab as a child
                // of the wave host. its 2D AudioSource has Play On Awake on,
                // so playback starts on instantiate. we capture the resulting
                // GameObject and start watching its AudioSource so we know
                // when the song ends - that triggers the supply-drop /
                // intermission for whichever wave is active at the time.
                if (CurrentWave == SongWaveTrigger)
                    _ = WatchSongTask(TheOneSongBundleLoader.SpawnUnder(transform));

                // wave-10 boss song: Beauty plays when Tagilla shows up.
                if (CurrentWave == BossWaveTrigger)
                    _ = WatchSongTask(BeautySongBundleLoader.SpawnUnder(transform));

                CurrentPhase = Phase.Spawning;

                // burst + trickle spawn shape: spawn InitialBurstSize zombies
                // back-to-back (one per frame) so the player gets an immediate
                // wave-incoming feel, then trickle the rest in one every
                // TrickleIntervalSec to keep the pressure on.
                //
                // why per-bot Create: BotsPresets.FillCreationDataWithProfiles
                // only adds ONE profile to the data class per Create call -
                // the count arg to BotCreationDataClass.Create is ignored on
                // the BotsPresets path (only the per-bot loop in the else
                // branch honors it). a single Create(count=24) gives at most
                // ~1-5 actual profiles (warm cache + one backend top-up),
                // so the next 20 Separate(1) calls hand out PROFILE-LESS
                // slices that increment InSpawnProcess but never materialize
                // into bots. one Create per zombie forces a fresh
                // FillCreationDataWithProfiles each time, which tops the
                // pool back up if it ran dry.
                //
                // other gates we already handle: ZonesLeaveController blocks
                // (NoZoneBlocks=true at HookSpawner) and spawn-point
                // selection (pre-pick from botZone.SpawnPoints in
                // TryPlaceWithRetries).
                int dispatched = 0;
                const int MaxCreateRetries = 6;        // 0.5s * 6 = ~3s total wait per stuck slot
                const float CreateRetryDelaySec = 0.5f;

                // boss-wave: spawn an infected Tagilla FIRST as part of
                // the initial seed (counts toward initialDispatchTarget,
                // NOT a bonus on top of it). HUD remaining ticks down per
                // kill regardless of role. HP boost is skipped for Tagilla
                // in OnBotAdd so he keeps his default stats.
                int regularCount = initialDispatchTarget;
                if (CurrentWave == BossWaveTrigger)
                {
                    BotCreationDataClass bossSlice = null;
                    for (int attempt = 0; attempt < MaxCreateRetries && !_disposed; attempt++)
                    {
                        Task<BotCreationDataClass> bossTask = CreateBatchDataAsync(1, WildSpawnType.infectedTagilla);
                        while (bossTask != null && !bossTask.IsCompleted && !_disposed)
                            yield return null;
                        bossSlice = bossTask?.Result;
                        if (IsSliceUsable(bossSlice)) break;
                        bossSlice = null;
                        yield return new WaitForSeconds(CreateRetryDelaySec);
                    }
                    if (bossSlice != null && TryPlaceWithRetries(bossSlice))
                    {
                        dispatched++;
                        _dispatchedTotal++;
                        regularCount = initialDispatchTarget - 1;
                        Plugin.LogSource?.LogInfo($"[ZombiesWaves] wave {CurrentWave} boss (infected Tagilla) dispatched.");
                        yield return new WaitForSeconds(TrickleIntervalSec);
                    }
                    else
                    {
                        Plugin.LogSource?.LogWarning($"[ZombiesWaves] wave {CurrentWave} boss dispatch failed; falling through to {initialDispatchTarget} regular zombies.");
                    }
                }

                for (int i = 0; i < regularCount && !_disposed; i++)
                {
                    // create-with-retry: BotsPresets.FillCreationDataWithProfiles
                    // hands out one profile per call from a cache that's
                    // refilled async via ISession.LoadBots. when we burn
                    // through the cache faster than the backend top-up
                    // completes, Create returns a slice with Count==0
                    // (or a slice whose only entry is null). retrying with
                    // a short delay lets the backend roundtrip finish and
                    // the next attempt sees the refilled cache.
                    BotCreationDataClass slice = null;
                    for (int attempt = 0; attempt < MaxCreateRetries && !_disposed; attempt++)
                    {
                        Task<BotCreationDataClass> createTask = CreateBatchDataAsync(1);
                        while (createTask != null && !createTask.IsCompleted && !_disposed)
                            yield return null;
                        slice = createTask?.Result;
                        if (IsSliceUsable(slice)) break;
                        slice = null;
                        yield return new WaitForSeconds(CreateRetryDelaySec);
                    }

                    if (slice == null)
                    {
                        Plugin.LogSource?.LogWarning($"[ZombiesWaves] zombie {i + 1}/{regularCount} create returned no profile after {MaxCreateRetries} retries (wave {CurrentWave}).");
                    }
                    else if (TryPlaceWithRetries(slice))
                    {
                        dispatched++;
                        _dispatchedTotal++;
                    }
                    else
                    {
                        Plugin.LogSource?.LogWarning($"[ZombiesWaves] zombie {i + 1}/{regularCount} rejected (wave {CurrentWave}).");
                    }

                    // burst the first N back-to-back (one frame between),
                    // then pace the rest at TrickleIntervalSec each.
                    if (i + 1 < InitialBurstSize) yield return null;
                    else if (i + 1 < regularCount) yield return new WaitForSeconds(TrickleIntervalSec);
                }
                Plugin.LogSource?.LogInfo($"[ZombiesWaves] wave {CurrentWave} initial seed: dispatched {dispatched}/{initialDispatchTarget} (wave target {TargetCount}).");

                CurrentPhase = Phase.Active;

                // poll until: (a) every dispatch for this wave has been made
                // (_dispatchedTotal == TargetCount), AND (b) every dispatch
                // has been confirmed via OnBotAdd (unconfirmed == 0), AND
                // (c) every confirmed zombie has died (aliveZombieIds empty).
                //
                // for waves > RollingSpawnStartWave, _dispatchedTotal lags
                // TargetCount until kills accumulate a replacement budget;
                // each tick we drain that budget by dispatching more zombies.
                //
                // stall guard: ONLY counts time where spawns haven't fully
                // delivered yet (_unconfirmedSpawns > 0 and not dropping).
                // once all spawns have arrived, we wait indefinitely for the
                // player to kill them - alive zombies sitting around is normal
                // gameplay, NOT a stall. previous version force-advanced after
                // 60s of no kills, which is why waves auto-progressed without
                // the player doing anything.
                float stallTimer = 0f;
                int lastUnconfirmed = _unconfirmedSpawns;
                while (!_disposed && (_dispatchedTotal < TargetCount || _unconfirmedSpawns > 0 || _aliveZombieIds.Count > 0))
                {
                    yield return new WaitForSeconds(1.0f);

                    // drain replacement budget: one dispatch per tick keeps
                    // the trickle paced (1Hz) instead of dumping a whole
                    // wave's worth of replacements the instant the player
                    // mows down the initial 24. budget is already capped at
                    // (TargetCount - _dispatchedTotal) in OnBotRemoved so we
                    // can drain naively here.
                    if (_replacementsBudget > 0 && _dispatchedTotal < TargetCount)
                    {
                        BotCreationDataClass replSlice = null;
                        Task<BotCreationDataClass> replTask = CreateBatchDataAsync(1);
                        while (replTask != null && !replTask.IsCompleted && !_disposed)
                            yield return null;
                        replSlice = replTask?.Result;

                        if (IsSliceUsable(replSlice) && TryPlaceWithRetries(replSlice))
                        {
                            _dispatchedTotal++;
                            _unconfirmedSpawns++;
                            _replacementsBudget--;
                        }
                        // if the slice was unusable or placement was rejected,
                        // leave the budget where it is - next tick will retry.
                        // we don't decrement the budget on failure because the
                        // replacement still hasn't happened.
                    }

                    if (_unconfirmedSpawns <= 0)
                    {
                        // all in-flight spawns confirmed; just waiting for
                        // kills (and possibly more replacement dispatches).
                        // reset the stall timer - this is legitimate
                        // "waiting for the player" time.
                        stallTimer = 0f;
                        continue;
                    }

                    if (_unconfirmedSpawns < lastUnconfirmed)
                    {
                        lastUnconfirmed = _unconfirmedSpawns;
                        stallTimer = 0f;
                    }
                    else
                    {
                        stallTimer += 1f;
                        if (stallTimer >= MaxStallSec)
                        {
                            Plugin.LogSource?.LogWarning($"[ZombiesWaves] wave {CurrentWave} spawn pipeline stalled (unconfirmed={_unconfirmedSpawns}); zeroing unconfirmed and waiting for kills only.");
                            _unconfirmedSpawns = 0;
                            stallTimer = 0f;
                        }
                    }
                }

                CurrentPhase = Phase.BetweenWaves;

                // intermission gate: if a tracked song finished during this
                // wave, swap in the long supply-drop intermission timer
                // instead of the normal short gap. when armed, we yield a
                // short breathing-room gap BEFORE spawning the supply drop /
                // announce / starting the song, so the wave-clear stinger has
                // a beat to ring out and the transition doesn't feel abrupt.
                bool runIntermission = _intermissionPending;
                if (runIntermission)
                {
                    _intermissionPending = false;
                    Plugin.LogSource?.LogInfo($"[ZombiesWaves] wave {CurrentWave} cleared; intermission armed -> {IntermissionSec}s hunt window (after {IntermissionLeadInSec}s lead-in).");
                }
                else
                {
                    Plugin.LogSource?.LogInfo($"[ZombiesWaves] wave {CurrentWave} cleared; next wave in {BetweenWavesSec}s.");
                }

                // round-end audio stinger - fires when the wave is done,
                // before the inter-wave delay so it overlaps the pause.
                _ = RoundEndBundleLoader.SpawnUnder(transform);

                if (runIntermission)
                {
                    // breathing-room gap before the intermission proper kicks
                    // in. counts toward the total IntermissionSec budget so
                    // the time-to-next-wave is unchanged.
                    yield return new WaitForSeconds(IntermissionLeadInSec);

                    // start the intermission song. Play-On-Awake AudioSource
                    // on the child means instantiating starts playback.
                    // sized so it ends ~15s before the IntermissionSec timer.
                    _ = MillionSongBundleLoader.SpawnUnder(transform);

                    // pull a random position from the configured list and spawn
                    // the supply drop. fire-and-forget; the spawner is async
                    // (PoolManager.LoadBundlesAndCreatePools).
                    string mapId = Singleton<GameWorld>.Instance?.MainPlayer?.Location;
                    Vector3 spawnPos = SupplyDropMapSpawnConfig.PickRandomPosition(mapId);
                    if (spawnPos != Vector3.zero)
                        _ = SupplyDropSpawner.SpawnAt(spawnPos, CurrentWave);
                    else
                        Plugin.LogSource?.LogWarning("[ZombiesWaves] intermission armed but no supply-drop spawn positions configured for this map.");

                    // screen-center announcement so the player knows the drop
                    // landed (the beacon is visible from anywhere via through-
                    // wall rendering, but a text alert reinforces the cue and
                    // tells them what they're looking at).
                    GameObject overlayHost = new GameObject("IntermissionMessageOverlay");
                    overlayHost.transform.SetParent(transform, false);
                    IntermissionMessageOverlay overlay = overlayHost.AddComponent<IntermissionMessageOverlay>();
                    overlay.Title    = "SUPPLY DROP INCOMING";
                    overlay.Subtitle = "GO FIND IT";

                    // wait the remainder of the intermission budget after the lead-in.
                    yield return new WaitForSeconds(IntermissionSec - IntermissionLeadInSec);

                    // intermission window closed - despawn the supply drop so
                    // it doesn't linger into the next wave's gameplay. matches
                    // the "find it during the intermission OR lose it" feel
                    // the announcement text promises ("GO FIND IT").
                    SupplyDropSpawner.DespawnCurrent();
                }
                else
                {
                    yield return new WaitForSeconds(BetweenWavesSec);
                }

                // sweep any stale entries out of the BSG spawn-pipeline
                // counters before the next wave dispatches. these can leak
                // across many waves when activations fail mid-flight (the
                // increment runs, the decrement never does). a reset here
                // is safe because the BetweenWavesSec wait gave any
                // legitimate in-flight spawns time to settle.
                ResetSpawnPipelineCounters();
            }
        }

        private bool HookSpawner()
        {
            try
            {
                IBotGame botGame = Singleton<IBotGame>.Instance;
                var botsController = botGame?.BotsController;
                _botSpawner = botsController?.BotSpawner;
                _botsList = botsController?.Bots;
                if (_botSpawner == null)
                {
                    Plugin.LogSource?.LogError("[ZombiesWaves] BotSpawner is null at hook time.");
                    return false;
                }

                // disable BSG's per-(zone, wildSpawnType) block list for this
                // raid. TryToSpawnInZoneInner consults
                // ZonesLeaveController.IsZoneBlockFor(zone, type) before
                // every spawn and rejects silently if the type is blocked
                // for that zone. on factory the single BotZone ends up
                // blocking infectedAssault under various conditions (we
                // can't see why - obfuscated), causing entire wave batches
                // to vanish. NoZoneBlocks=true makes IsZoneBlockFor a no-op
                // globally, which is fine because zombies mode is the only
                // thing spawning bots and we manage caps ourselves via
                // wave sizing. scope is per-raid (the controller is
                // destroyed at raid teardown along with the BotsController).
                try
                {
                    if (botsController?.ZonesLeaveController != null)
                    {
                        botsController.ZonesLeaveController.NoZoneBlocks = true;
                        Plugin.LogSource?.LogInfo("[ZombiesWaves] ZonesLeaveController.NoZoneBlocks = true (raid-scoped).");
                    }
                }
                catch (Exception zEx)
                {
                    Plugin.LogSource?.LogWarning($"[ZombiesWaves] could not set NoZoneBlocks: {zEx.Message}");
                }

                // we hook BOTH events on different objects:
                //   - BotsClass.OnBotAdd (on BotsController.Bots) - fires for
                //     every bot added to the master list. used to be needed
                //     because BotSpawner.OnBotCreated turned out to be
                //     unreliable in our setup (firing once for OnBotRemoved
                //     but never for OnBotCreated - possibly being nulled by
                //     another subscriber between our subscribe and the actual
                //     spawn). BotHalloweenWithZombies itself uses OnBotAdd,
                //     so this is the canonical "bot appeared" hook.
                //   - BotSpawner.OnBotRemoved - removal still works fine
                //     through the original event.
                if (_botsList != null) _botsList.OnBotAdd += OnBotAdd;
                _botSpawner.OnBotRemoved += OnBotRemoved;

                Plugin.LogSource?.LogInfo($"[ZombiesWaves] subscribed to BotSpawner ({_botSpawner.GetType().Name}); BotsClass list available = {_botsList != null}; existing alive bots = {_botSpawner.AllBotsCount}.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesWaves] HookSpawner threw: {ex.Message}");
                return false;
            }
        }

        private void UnhookSpawner()
        {
            try
            {
                if (_botsList != null) _botsList.OnBotAdd -= OnBotAdd;
                if (_botSpawner != null) _botSpawner.OnBotRemoved -= OnBotRemoved;
            }
            catch { /* shutdown; ignore */ }
        }

        private void OnBotAdd(BotOwner bot)
        {
            try
            {
                WildSpawnType role = bot?.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (!role.IsInfected()) return;
                string id = bot.Profile.Id;
                if (string.IsNullOrEmpty(id)) return;
                if (!_aliveZombieIds.Add(id)) return; // already tracked - dedupe

                // remember the BotOwner so the unstuck sweep can check it
                // later. seed the displacement sample with the bot's spawn
                // position + time, so the displacement check has a baseline
                // ready when UnstuckMovementWindowSec elapses.
                _zombieTracker[id] = new ZombieTrackInfo
                {
                    Bot = bot,
                    SamplePosition = bot.Position,
                    SampleTime = Time.unscaledTime,
                };

                // confirmation that a forced spawn actually materialized.
                // we only decrement here (not at the spawn call site) so
                // the polling loop waits for the async pipeline to deliver
                // before considering a spawn "resolved".
                if (_unconfirmedSpawns > 0) _unconfirmedSpawns--;

                // current-wave HP bonus. CurrentWave is set when the wave
                // begins spawning, so every zombie minted during this wave's
                // spawn loop reads the same bonus. wave 1 -> 0, wave 2 -> 100,
                // wave 3 -> 200, etc (linear, see ZombieHealthBooster).
                //
                // skip the boss role - Tagilla already has his own (very high)
                // default HP from his bot config; stacking +900 HP on top would
                // make him an unkillable bullet sponge.
                if (role != WildSpawnType.infectedTagilla)
                {
                    float bonus = ZombieHealthBooster.BonusForWave(CurrentWave);
                    if (bonus > 0f) ZombieHealthBooster.Apply(bot, bonus);
                }

                // tell BotHalloweenWithZombies the player is a pursuit
                // target so this zombie (and every zombie still alive)
                // chases them immediately instead of wandering. method_8
                // dedupes by target, so calling it every OnBotAdd is safe -
                // first call registers the pursuit, later calls are no-ops.
                EnsurePlayerPursuit(bot);

                // assign a wave-scaled random speed multiplier. early
                // waves bumble (~0.5x), later waves ramp toward 1.6x base
                // with mostly-at-base variance. Tagilla (boss role) is
                // skipped inside Assign - he keeps his natural speed.
                ZombieSpeedAssigner.Assign(bot, CurrentWave);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesWaves] OnBotAdd threw: {ex.Message}");
            }
        }

        private void EnsurePlayerPursuit(BotOwner zombie)
        {
            try
            {
                var bhwz = Singleton<IBotGame>.Instance?.BotsController?.EventsController?.BotHalloweenWithZombies;
                if (bhwz == null) return;
                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                if (main == null) return;
                bhwz.method_8(zombie, main);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesWaves] EnsurePlayerPursuit threw: {ex.Message}");
            }
        }

        // unwrap an async song-spawn task and register the resulting
        // AudioSource for end-of-track monitoring. fires-and-forgets; the
        // Update loop polls _tracksToWatch each tick.
        private async Task WatchSongTask(Task<GameObject> spawnTask)
        {
            try
            {
                GameObject go = await spawnTask;
                if (go == null) return;
                AudioSource src = go.GetComponentInChildren<AudioSource>(includeInactive: true);
                if (src == null)
                {
                    Plugin.LogSource?.LogWarning("[ZombiesWaves] song spawn has no AudioSource; intermission trigger disabled for this track.");
                    return;
                }
                _tracksToWatch.Add(src);
                Plugin.LogSource?.LogInfo($"[ZombiesWaves] watching song AudioSource (clip='{src.clip?.name}', length={src.clip?.length:F1}s).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesWaves] WatchSongTask threw: {ex.Message}");
            }
        }

        // distance-based unstuck check. throttled to UnstuckCheckInterval
        // (15s) so the per-zombie raycasts only fire once per cycle. for
        // each tracked alive zombie:
        //   - if distance to main player <= UnstuckMaxDistFromPlayer, skip
        //     (zombie is "near enough" - leave it alone)
        //   - else if zombie is currently visible to the player, skip
        //     (don't want them visibly vanishing on screen)
        //   - else queue for teleport to a fresh out-of-LoS spawn point
        //     between TeleportMin/MaxDistFromPlayer
        //
        // also polls every tracked song's AudioSource at TrackCheckInterval -
        // when a track stops playing (and was previously playing), latch
        // _intermissionPending so the next wave-clear transition will swap
        // in the supply-drop intermission.
        private void Update()
        {
            if (_disposed) return;

            // song-end detection (cheap, runs every TrackCheckInterval).
            if (Time.unscaledTime >= _nextTrackCheckTime)
            {
                _nextTrackCheckTime = Time.unscaledTime + TrackCheckInterval;
                for (int i = _tracksToWatch.Count - 1; i >= 0; i--)
                {
                    AudioSource src = _tracksToWatch[i];
                    if (src == null)
                    {
                        _tracksToWatch.RemoveAt(i);
                        continue;
                    }
                    if (!src.isPlaying && !_tracksThatEnded.Contains(src))
                    {
                        _tracksThatEnded.Add(src);
                        _intermissionPending = true;
                        Plugin.LogSource?.LogInfo($"[ZombiesWaves] song ended (clip='{src.clip?.name}'); intermission armed for next wave clear.");
                    }
                }
            }

            if (_zombieTracker.Count == 0) return;
            if (Time.unscaledTime < _nextStuckCheckTime) return;
            _nextStuckCheckTime = Time.unscaledTime + UnstuckCheckInterval;

            Player main = Singleton<GameWorld>.Instance?.MainPlayer;
            if (main == null) return;

            Vector3 playerPos = main.Position;
            Vector3 playerEye = playerPos + Vector3.up * 1.5f;
            float maxDistSqr = UnstuckMaxDistFromPlayer * UnstuckMaxDistFromPlayer;

            float now = Time.unscaledTime;
            float displacementThresholdSqr = UnstuckMovementThreshold * UnstuckMovementThreshold;

            List<string> toUnstick = null;
            foreach (var kvp in _zombieTracker)
            {
                ZombieTrackInfo info = kvp.Value;
                if (info.Bot == null || info.Bot.IsDead) continue;

                Vector3 botPos = info.Bot.Position;

                // -- trigger 1: too far from player --
                bool tooFar = (botPos - playerPos).sqrMagnitude > maxDistSqr;

                // -- trigger 2: hasn't displaced enough over the rolling
                // window. compares current position to the sample taken
                // UnstuckMovementWindowSec ago. only evaluates once a full
                // window has elapsed; the sample is then refreshed for
                // the next window regardless of stuck-or-not, so the
                // check rolls forward continuously.
                bool driftStuck = false;
                if (info.SampleTime > 0f && (now - info.SampleTime) >= UnstuckMovementWindowSec)
                {
                    float displacementSqr = (botPos - info.SamplePosition).sqrMagnitude;
                    driftStuck = displacementSqr < displacementThresholdSqr;
                    // roll the sample window forward.
                    info.SamplePosition = botPos;
                    info.SampleTime = now;
                }

                if (!tooFar && !driftStuck) continue;

                // visibility gate: raycast from player eye to bot torso.
                // if NOTHING blocks the line, the bot is in view - skip,
                // since teleporting them would make them visibly disappear.
                // if the raycast hits geometry first, the bot is occluded
                // and safe to teleport.
                Vector3 botTorso = botPos + Vector3.up * 1.0f;
                Vector3 toward = botTorso - playerEye;
                float castDist = toward.magnitude;
                if (castDist <= 0.01f) continue;
                bool occluded = Physics.Raycast(playerEye, toward.normalized, castDist, LayerMaskClass.HighPolyWithTerrainMaskAI);
                if (!occluded) continue; // bot is visible to the player - skip

                if (toUnstick == null) toUnstick = new List<string>();
                toUnstick.Add(kvp.Key);
                // store the reason in a side-band log line just below
                // (info gets re-fetched in the teleport loop).
                Plugin.LogSource?.LogInfo($"[ZombiesWaves] unstuck candidate '{info.Bot.Profile?.Nickname}' (tooFar={tooFar}, driftStuck={driftStuck}).");
            }

            if (toUnstick == null) return;
            foreach (string id in toUnstick)
            {
                if (!_zombieTracker.TryGetValue(id, out ZombieTrackInfo info)) continue;
                if (info.Bot == null) continue;
                Vector3 target = FindTeleportTarget(main);
                if (target == Vector3.zero) continue;
                try
                {
                    info.Bot.GetPlayer.Teleport(target, false);
                    // reset the displacement baseline to the teleport
                    // destination so the next window measures from here.
                    // without this, the teleport jump itself would be
                    // counted as "movement" and a zombie that goes stuck
                    // immediately after landing would dodge the next
                    // driftStuck check for ~30s.
                    info.SamplePosition = target;
                    info.SampleTime = Time.unscaledTime;
                    Plugin.LogSource?.LogInfo($"[ZombiesWaves] unstuck zombie '{info.Bot.Profile?.Nickname}' -> {target}.");
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"[ZombiesWaves] teleport stuck zombie threw: {ex.Message}");
                }
            }
        }

        // pick a zone SpawnPoint within TeleportMin..MaxDist of the player
        // that the player can't see (raycast from approx eye-height to
        // candidate-torso-height hits geometry => occluded => good target).
        // walks points in random order so repeated teleports don't pile
        // every zombie on the same spawn.
        private Vector3 FindTeleportTarget(Player main)
        {
            try
            {
                if (_botSpawner == null) return Vector3.zero;
                BotZone zone = _botSpawner.GetRandomBotZone(false);
                ISpawnPoint[] points = zone?.SpawnPoints;
                if (points == null || points.Length == 0) return Vector3.zero;

                Vector3 playerPos = main.Position;
                Vector3 eyePos = playerPos + Vector3.up * 1.5f; // approx head height; cheap

                int n = points.Length;
                int start = UnityEngine.Random.Range(0, n);
                for (int i = 0; i < n; i++)
                {
                    ISpawnPoint sp = points[(start + i) % n];
                    if (sp == null) continue;
                    Vector3 spawnPos = sp.Position;
                    float dist = Vector3.Distance(spawnPos, playerPos);
                    if (dist < TeleportMinDistFromPlayer || dist > TeleportMaxDistFromPlayer) continue;

                    Vector3 spawnTorso = spawnPos + Vector3.up * 1.0f;
                    Vector3 toward = spawnTorso - eyePos;
                    float castDist = toward.magnitude;
                    if (castDist <= 0.01f) continue;
                    // raycast occluded == out of LoS == acceptable teleport target.
                    if (Physics.Raycast(eyePos, toward.normalized, castDist, LayerMaskClass.HighPolyWithTerrainMaskAI))
                        return spawnPos;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesWaves] FindTeleportTarget threw: {ex.Message}");
            }
            return Vector3.zero;
        }

        private void OnBotRemoved(BotOwner bot)
        {
            try
            {
                string id = bot?.Profile?.Id;
                if (string.IsNullOrEmpty(id)) return;
                // only count it as a "wave kill" if the bot was actually part
                // of our tracked set - prevents random non-wave bot deaths
                // (if any leak through) from decrementing the HUD counter.
                if (_aliveZombieIds.Remove(id))
                {
                    _killsThisWave++;

                    // rolling spawn: past wave 5, each kill schedules 1-2
                    // replacement zombies to dispatch from the Active-phase
                    // polling loop. budget is capped at the remaining wave
                    // target so we never overshoot TargetCount.
                    if (CurrentWave > RollingSpawnStartWave && _dispatchedTotal < TargetCount)
                    {
                        int add = UnityEngine.Random.Range(ReplacementsPerKillMin, ReplacementsPerKillMax + 1);
                        int remaining = TargetCount - _dispatchedTotal;
                        _replacementsBudget = Mathf.Min(_replacementsBudget + add, remaining);
                    }
                }
                _zombieTracker.Remove(id);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesWaves] OnBotRemoved threw: {ex.Message}");
            }
        }

        // a slice is "usable" if it has at least one non-null Profile.
        // BotsPresets's cache-miss path adds a null placeholder to the list
        // before the backend load fires; if the load also returns empty
        // (backend rate-limited / our raid burning through profiles faster
        // than ISession.LoadBots completes), we end up with either Count==0
        // or Count==1 with a null entry. either way TryToSpawnInZoneInner
        // will silently no-op on the missing profile.
        private static bool IsSliceUsable(BotCreationDataClass slice)
        {
            if (slice == null) return false;
            if (slice.Count == 0) return false;
            try
            {
                var profiles = slice.Profiles;
                if (profiles == null) return false;
                for (int i = 0; i < profiles.Count; i++)
                {
                    if (profiles[i] != null) return true;
                }
                return false;
            }
            catch { return false; }
        }

        // create N zombie profiles in one shot. dispatch happens in the
        // coroutine afterwards via per-bot Separate(1) calls. role defaults
        // to infectedAssault but boss spawns (infected Tagilla on wave 10)
        // pass infectedTagilla so EFT picks the right profile pool.
        private async Task<BotCreationDataClass> CreateBatchDataAsync(int count, WildSpawnType role = WildSpawnType.infectedAssault)
        {
            try
            {
                if (_botSpawner == null || count <= 0) return null;

                BotSpawnParams spawnParams = new BotSpawnParams
                {
                    ShallBeGroup = new ShallBeGroupParams(false, false, 1),
                };
                var profileData = new BotProfileDataClass(
                    EPlayerSide.Savage,
                    role,
                    BotDifficulty.normal,
                    5f,
                    spawnParams,
                    false);

                return await BotCreationDataClass.Create(
                    profileData, _botSpawner.BotCreator, count, _botSpawner);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesWaves] CreateBatchDataAsync threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // dispatch one slice into a zone. tries the random-zone path first;
        // if the spawner silently rejects (InSpawnProcess didn't change)
        // we walk through any other zones it'll hand us. true on the first
        // acceptance.
        private bool TryPlaceWithRetries(BotCreationDataClass slice)
        {
            try
            {
                if (_botSpawner == null || slice == null) return false;

                // collect a working set of zones. on most maps this is a
                // single zone, but ask for a few so we have something to
                // try if the primary rejects.
                List<BotZone> zones = new List<BotZone>();
                HashSet<BotZone> seen = new HashSet<BotZone>();
                for (int j = 0; j < 8; j++)
                {
                    BotZone z = _botSpawner.GetRandomBotZone(false);
                    if (z != null && seen.Add(z)) zones.Add(z);
                }
                if (zones.Count == 0) return false;

                int before = _botSpawner.InSpawnProcess;
                foreach (BotZone zone in zones)
                {
                    // pre-compute a spawn point from the zone's
                    // SpawnPointMarkers and pass it via pointsToSpawn.
                    // without this, BSG's SpawnSystem.SelectAISpawnPoints
                    // refuses to allocate points once the zone is saturated
                    // (~map-default cap of bots near the spawn area) - even
                    // the forced fallback returns 0 points and the call
                    // silently no-ops. forcing our own point bypasses that
                    // gate entirely.
                    List<ISpawnPoint> picked = null;
                    try
                    {
                        ISpawnPoint[] zonePoints = zone.SpawnPoints;
                        if (zonePoints != null && zonePoints.Length > 0)
                        {
                            ISpawnPoint chosen = zonePoints[UnityEngine.Random.Range(0, zonePoints.Length)];
                            if (chosen != null) picked = new List<ISpawnPoint> { chosen };
                        }
                    }
                    catch { /* fall back to null = let BSG pick */ }

                    _botSpawner.TryToSpawnInZoneInner(zone, slice, 1, false, true, picked, true);
                    int after = _botSpawner.InSpawnProcess;
                    if (after > before) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesWaves] TryPlaceWithRetries threw: {ex.Message}");
                return false;
            }
        }

        // best-effort sweep of the BSG spawn-pipeline counters between
        // waves. clamps each one back down to the actual current state so
        // leaks (counter incremented but never decremented because an
        // activation silently failed) dont accumulate across a long raid.
        // wrapped in try/catch per access because the fields are internal
        // and could go missing or be renamed in a future SPT bump.
        private void ResetSpawnPipelineCounters()
        {
            try
            {
                if (_botSpawner == null) return;

                int alive = _botSpawner.AllBotsCount;

                // InSpawnProcess tracks bots dispatched but not yet
                // activated. set to 0 since BetweenWavesSec has elapsed
                // and no spawns are pending from our side.
                try { _botSpawner.InSpawnProcess = 0; } catch { }

                // BotCreator.BotsLoading tracks profiles being generated
                // by the BotCreator. same idea - clear after the gap.
                try
                {
                    if (_botSpawner.BotCreator is BotCreatorClass bc)
                        bc.BotsLoading = 0;
                }
                catch { }

                // BotCreationDataClass.ProfilesLoadingProcess is a STATIC
                // counter for in-flight Create() calls. reset to 0.
                try { BotCreationDataClass.ProfilesLoadingProcess = 0; } catch { }

                // SpawnDelaysService holds deferred spawns that werent
                // force-flagged. with forcedSpawn=true we shouldnt be
                // adding to it, but clear out any leftover entries from
                // earlier patches or other systems.
                try
                {
                    var delays = _botSpawner.SpawnDelaysService?.DelaysModels;
                    if (delays != null && delays.Count > 0)
                        delays.Clear();
                }
                catch { }

                Plugin.LogSource?.LogInfo($"[ZombiesWaves] spawn-pipeline counters reset between waves (alive={alive}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesWaves] ResetSpawnPipelineCounters threw: {ex.Message}");
            }
        }
    }
}
