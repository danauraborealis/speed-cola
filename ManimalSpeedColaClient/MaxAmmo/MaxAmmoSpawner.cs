using System;
using System.Threading.Tasks;
using Manimal.SpeedCola.Components;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // spawns a Max Ammo power-up at a given world position. fire-and-forget
    // from the kill hook; the loader is async so the first spawn waits on
    // bundle resolution, subsequent spawns are immediate.
    //
    // SpawnAt rolls Plugin.MaxAmmoSpawnChance internally - callers pass the
    // candidate position (e.g. zombie head on death) and trust the spawner
    // to decide whether to actually drop one.
    public static class MaxAmmoSpawner
    {
        // called from the zombie-death hook. rolls the configured spawn
        // chance and dispatches the async spawn if it hits.
        public static void TryRoll(Vector3 headPosition)
        {
            if (!Plugin.ZombiesMode) return;
            float chance = Plugin.MaxAmmoSpawnChance?.Value ?? 0.5f;
            if (chance <= 0f) return;
            if (UnityEngine.Random.value > chance) return;
            _ = SpawnAt(headPosition);
        }

        public static async Task SpawnAt(Vector3 position)
        {
            try
            {
                GameObject prefab = await MaxAmmoBundleLoader.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource?.LogWarning("[MaxAmmo] spawn aborted - prefab not loaded.");
                    return;
                }
                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                instance.transform.position = position;
                instance.transform.rotation = Quaternion.identity;

                // attach the pickup controller (handles float anim + trigger).
                instance.AddComponent<MaxAmmoPickup>();

                Plugin.LogSource?.LogInfo($"[MaxAmmo] spawned at {position}.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[MaxAmmo] SpawnAt threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
