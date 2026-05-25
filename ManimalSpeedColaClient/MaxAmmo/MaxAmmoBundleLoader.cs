using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // bundle loader for manimal/max_ammo_pickup.bundle. mirrors the
    // RoundEndBundleLoader pattern but returns the prefab itself so the
    // caller (MaxAmmoSpawner) can Instantiate at a chosen position.
    //
    // unlike the music/SFX bundle loaders, this one is NOT a fire-and-
    // forget spawn-under helper - we need to control position, rotation,
    // and lifetime per-spawn (the pickup stays in the world until the
    // player walks into it OR a configurable timeout expires).
    public static class MaxAmmoBundleLoader
    {
        public const string BundleKey = "manimal/max_ammo_pickup.bundle";

        private static readonly string[] PrefabAssetNameCandidates =
        {
            "max_ammo",
            "MaxAmmo",
            "max_ammo_pickup",
            "maxammo",
            "MaxAmmoPickup",
        };

        private static GameObject _prefab;
        private static Task<GameObject> _loadTask;

        public static Task<GameObject> EnsureLoaded()
        {
            if (_prefab != null) return Task.FromResult(_prefab);
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        public static void ResetForNewRaid()
        {
            // bundle stays loaded but null the prefab ref so a fresh raid
            // re-resolves it (in case the user changed candidate name in F12).
            _prefab = null;
            _loadTask = null;
        }

        private static async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource?.LogError("[MaxAmmo] IEasyAssets not initialized; cannot load max_ammo_pickup bundle.");
                    _loadTask = null;
                    return null;
                }
                IEasyAssets ea = Singleton<IEasyAssets>.Instance;
                Plugin.LogSource?.LogInfo($"[MaxAmmo] retaining max_ammo bundle: {BundleKey}");
                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);
                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource?.LogError($"[MaxAmmo] bundle '{BundleKey}' failed to load.");
                    _loadTask = null;
                    return null;
                }
                foreach (string candidate in PrefabAssetNameCandidates)
                {
                    _prefab = ea.GetAsset<GameObject>(BundleKey, candidate);
                    if (_prefab != null)
                    {
                        Plugin.LogSource?.LogInfo($"[MaxAmmo] loaded prefab (asset='{candidate}'): {_prefab.name}");
                        return _prefab;
                    }
                }
                // raw bundle scan fallback - same as RoundEndBundleLoader. dumps
                // contents to the log so the user can see the actual asset name
                // if it doesn't match any candidate above.
                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (ab.name == null || !ab.name.Contains("max_ammo", StringComparison.OrdinalIgnoreCase)) continue;
                    Plugin.LogSource?.LogInfo($"[MaxAmmo] === bundle '{ab.name}' asset inventory ===");
                    foreach (string n in ab.GetAllAssetNames())
                        Plugin.LogSource?.LogInfo($"  asset path: {n}");
                    GameObject[] gos = ab.LoadAllAssets<GameObject>();
                    if (gos.Length > 0)
                    {
                        _prefab = gos[0];
                        Plugin.LogSource?.LogInfo($"[MaxAmmo] fallback picked: {_prefab.name}");
                        return _prefab;
                    }
                }
                Plugin.LogSource?.LogError("[MaxAmmo] no matching AssetBundle found.");
                return null;
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogError($"[MaxAmmo] bundle load failed: {e}");
                _loadTask = null;
                return null;
            }
        }
    }
}
