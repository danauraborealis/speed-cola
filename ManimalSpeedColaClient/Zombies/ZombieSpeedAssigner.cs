using EFT;
using Manimal.SpeedCola.Components;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // wave-based zombie speed multiplier picker. attaches a
    // ZombieSpeedComponent to each zombie bot on spawn with a multiplier
    // rolled from a wave-progressing distribution.
    //
    // formula (per user spec):
    //   - waveBase: 0.5 at wave 1, +0.125 per wave, cap at 1.6 (cap hits
    //     around wave 9-10). so wave 1 = 0.5x, wave 5 = 1.0x, wave 9+ = 1.6x.
    //   - per-bot variance: random multiplier rolled from a [lo, hi] band.
    //     at wave 1 the band is [0.5, 1.0] (high chance of slower-than-base
    //     bumblers). by wave 10+ the band narrows + shifts up to [0.9, 1.1]
    //     (mostly at base, occasional slightly-faster outlier). chance of a
    //     slow zombie tapers off smoothly as waves progress.
    //   - final speed = waveBase * variance.
    //
    // Tagilla (the boss-wave infected boss) keeps his natural speed - the
    // assigner skips infectedTagilla so he doesn't end up bumbling at 0.5x
    // on his own wave.
    public static class ZombieSpeedAssigner
    {
        // tuning knobs - edit literals to retune the distribution shape.
        public const float WaveBaseStart      = 0.7f;
        public const float WaveBaseRamp       = 0.093f;  // 0.7@w1 → 2.0 cap @w15
        public const float WaveBaseCap        = 2.5f;
        public const int   FullRampWaves      = 13;      // wave at which variance band has fully shifted (aligned with cap-hit @w15)
        public static readonly float StartLow  = 0.7f;
        public static readonly float StartHigh = 1.0f;
        public static readonly float EndLow    = 0.9f;
        public static readonly float EndHigh   = 1.4f;

        public static float WaveBase(int wave)
        {
            return Mathf.Min(WaveBaseStart + WaveBaseRamp * (wave - 1), WaveBaseCap);
        }

        // returns the speed multiplier a freshly-spawned zombie on `wave`
        // should run with. pure function - caller must keep the value and
        // hand it to the ZombieSpeedComponent.
        public static float RollSpeedForWave(int wave)
        {
            float waveBase = WaveBase(wave);
            float t = Mathf.Clamp01((wave - 1f) / FullRampWaves);
            float lo = Mathf.Lerp(StartLow,  EndLow,  t);
            float hi = Mathf.Lerp(StartHigh, EndHigh, t);
            float variance = UnityEngine.Random.Range(lo, hi);
            return waveBase * variance;
        }

        // attach a ZombieSpeedComponent to the bot's Player GameObject with
        // a rolled-for-wave multiplier. no-op for the boss role (Tagilla
        // keeps his natural speed) and for bots that already have one
        // (defensive against double-fire of OnBotAdd in EFT).
        public static void Assign(BotOwner bot, int wave)
        {
            try
            {
                if (bot == null || bot.GetPlayer == null) return;
                WildSpawnType role = bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (role == WildSpawnType.infectedTagilla) return;

                GameObject go = bot.GetPlayer.gameObject;
                if (go.GetComponent<ZombieSpeedComponent>() != null) return;

                float speed = RollSpeedForWave(wave);
                ZombieSpeedComponent c = go.AddComponent<ZombieSpeedComponent>();
                c.SpeedMultiplier = speed;

                Plugin.LogSource?.LogInfo($"[ZombieSpeed] '{bot.Profile?.Nickname ?? "?"}' wave={wave} base={WaveBase(wave):F2} → speed={speed:F2}");
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombieSpeed] Assign threw: {ex.Message}");
            }
        }
    }
}
