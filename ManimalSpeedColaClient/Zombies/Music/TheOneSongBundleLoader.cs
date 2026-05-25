using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // bundle loader for theone_song.bundle - a single prefab (TheOne) with a
    // 2D AudioSource on a child GameObject (theonesong). the AudioSource is
    // flagged Play On Awake, so instantiating the prefab is enough to start
    // playback - no code-side AudioSource.Play() needed.
    //
    // SpawnUnder(parent) is the one-shot entry point used by ZombiesWaveController
    // when wave 5 starts. instantiated as a child of the wave host so it dies
    // with the raid (GameWorld teardown destroys the host tree).
    public static class TheOneSongBundleLoader
    {
        public const string BundleKey = "manimal/theone_song.bundle";

        private static readonly string[] PrefabAssetNameCandidates =
        {
            "TheOne",
            "theone",
            "theone_song",
            "theonesong",
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

        // resets the once-per-raid latch. wave controller calls this on
        // start so a new raid can spawn it again.
        public static void ResetForNewRaid() => _spawnedThisRaid = false;

        // load (if needed) and instantiate the song prefab as a child of
        // `parent`. fire-and-forget. only fires once per raid - guarded by
        // _spawnedThisRaid so accidental double-triggers don't stack songs.
        // returns the spawned GameObject so the caller can monitor its
        // AudioSource (e.g., to detect when the song ends and trigger an
        // intermission). returns null if the bundle failed to load or the
        // once-per-raid latch is already set.
        public static async Task<GameObject> SpawnUnder(Transform parent)
        {
            if (_spawnedThisRaid)
            {
                Plugin.LogSource?.LogInfo("[TheOneSong] already spawned this raid; skipping.");
                return null;
            }
            _spawnedThisRaid = true;

            GameObject prefab = await EnsureLoaded();
            if (prefab == null)
            {
                Plugin.LogSource?.LogWarning("[TheOneSong] prefab failed to load; song will not play.");
                _spawnedThisRaid = false; // allow retry later
                return null;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            if (parent != null) instance.transform.SetParent(parent, worldPositionStays: false);
            Plugin.LogSource?.LogInfo($"[TheOneSong] spawned '{instance.name}' under '{parent?.name ?? "<root>"}'.");
            return instance;
        }

        private static async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource.LogError("IEasyAssets not initialized; cannot load TheOne song bundle.");
                    _loadTask = null;
                    return null;
                }

                IEasyAssets ea = Singleton<IEasyAssets>.Instance;
                Plugin.LogSource.LogInfo($"Retaining TheOne song bundle: {BundleKey}");

                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);

                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource.LogError($"TheOne song bundle '{BundleKey}' failed to load.");
                    _loadTask = null;
                    return null;
                }

                foreach (string candidate in PrefabAssetNameCandidates)
                {
                    _prefab = ea.GetAsset<GameObject>(BundleKey, candidate);
                    if (_prefab != null)
                    {
                        Plugin.LogSource.LogInfo($"Loaded TheOne song prefab (asset='{candidate}'): {_prefab.name}");
                        return _prefab;
                    }
                }

                Plugin.LogSource.LogWarning("IEasyAssets.GetAsset failed for all TheOne candidates. Falling back to AssetBundle enumeration.");

                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    string abName = ab.name ?? "";
                    if (!abName.Contains("theone", StringComparison.OrdinalIgnoreCase)) continue;

                    Plugin.LogSource.LogInfo($"=== Bundle '{abName}' asset inventory ===");
                    foreach (string n in ab.GetAllAssetNames())
                        Plugin.LogSource.LogInfo($"  asset path: {n}");

                    GameObject[] gameObjects = ab.LoadAllAssets<GameObject>();
                    foreach (GameObject go in gameObjects)
                        Plugin.LogSource.LogInfo($"     GameObject: name='{go.name}'");

                    if (gameObjects.Length > 0)
                    {
                        _prefab = gameObjects[0];
                        Plugin.LogSource.LogInfo($"TheOne song fallback picked: {_prefab.name}");
                        return _prefab;
                    }
                }

                Plugin.LogSource.LogError($"No loaded AssetBundle matched 'theone'. Bundle key: '{BundleKey}'");
                return null;
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"TheOne song bundle load failed: {e}");
                _loadTask = null;
                return null;
            }
        }
    }
}
