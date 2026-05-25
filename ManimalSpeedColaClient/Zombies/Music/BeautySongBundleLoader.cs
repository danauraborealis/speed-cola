using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // sibling of TheOneSongBundleLoader. bundle holds a root prefab "Beauty"
    // with a child "beautysong" carrying a 2D AudioSource flagged Play On
    // Awake - instantiating the prefab is enough to start playback. fires
    // once per raid on the boss-wave trigger (wave 10).
    public static class BeautySongBundleLoader
    {
        public const string BundleKey = "manimal/beautysong.bundle";

        private static readonly string[] PrefabAssetNameCandidates =
        {
            "Beauty",
            "beauty",
            "beautysong",
            "beauty_song",
        };

        private static GameObject _prefab;
        private static Task<GameObject> _loadTask;
        private static bool _spawnedThisRaid;

        public static Task<GameObject> EnsureLoaded()
        {
            if (_prefab != null) return Task.FromResult(_prefab);
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        public static void ResetForNewRaid() => _spawnedThisRaid = false;

        // returns the spawned GameObject so the caller can monitor its
        // AudioSource (e.g., to detect when the song ends and trigger an
        // intermission). returns null if the bundle failed to load or the
        // once-per-raid latch is already set.
        public static async Task<GameObject> SpawnUnder(Transform parent)
        {
            if (_spawnedThisRaid)
            {
                Plugin.LogSource?.LogInfo("[BeautySong] already spawned this raid; skipping.");
                return null;
            }
            _spawnedThisRaid = true;

            GameObject prefab = await EnsureLoaded();
            if (prefab == null)
            {
                Plugin.LogSource?.LogWarning("[BeautySong] prefab failed to load; song will not play.");
                _spawnedThisRaid = false;
                return null;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            if (parent != null) instance.transform.SetParent(parent, worldPositionStays: false);
            Plugin.LogSource?.LogInfo($"[BeautySong] spawned '{instance.name}' under '{parent?.name ?? "<root>"}'.");
            return instance;
        }

        private static async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource.LogError("IEasyAssets not initialized; cannot load Beauty song bundle.");
                    _loadTask = null;
                    return null;
                }

                IEasyAssets ea = Singleton<IEasyAssets>.Instance;
                Plugin.LogSource.LogInfo($"Retaining Beauty song bundle: {BundleKey}");

                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);

                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource.LogError($"Beauty song bundle '{BundleKey}' failed to load.");
                    _loadTask = null;
                    return null;
                }

                foreach (string candidate in PrefabAssetNameCandidates)
                {
                    _prefab = ea.GetAsset<GameObject>(BundleKey, candidate);
                    if (_prefab != null)
                    {
                        Plugin.LogSource.LogInfo($"Loaded Beauty song prefab (asset='{candidate}'): {_prefab.name}");
                        return _prefab;
                    }
                }

                Plugin.LogSource.LogWarning("IEasyAssets.GetAsset failed for all Beauty candidates. Falling back to AssetBundle enumeration.");

                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    string abName = ab.name ?? "";
                    if (!abName.Contains("beauty", StringComparison.OrdinalIgnoreCase)) continue;

                    Plugin.LogSource.LogInfo($"=== Bundle '{abName}' asset inventory ===");
                    foreach (string n in ab.GetAllAssetNames())
                        Plugin.LogSource.LogInfo($"  asset path: {n}");

                    GameObject[] gameObjects = ab.LoadAllAssets<GameObject>();
                    foreach (GameObject go in gameObjects)
                        Plugin.LogSource.LogInfo($"     GameObject: name='{go.name}'");

                    if (gameObjects.Length > 0)
                    {
                        _prefab = gameObjects[0];
                        Plugin.LogSource.LogInfo($"Beauty song fallback picked: {_prefab.name}");
                        return _prefab;
                    }
                }

                Plugin.LogSource.LogError($"No loaded AssetBundle matched 'beauty'. Bundle key: '{BundleKey}'");
                return null;
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Beauty song bundle load failed: {e}");
                _loadTask = null;
                return null;
            }
        }
    }
}
