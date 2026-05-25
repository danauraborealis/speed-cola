using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // sibling of TheOneSongBundleLoader / BeautySongBundleLoader. bundle holds
    // a root prefab "million" with a child "millionaire" carrying a 2D
    // AudioSource flagged Play On Awake - instantiating the prefab starts
    // playback. fires on each intermission window (every wave clear, not
    // just the first). unlike TheOne/Beauty (wave-triggered, once-per-raid),
    // this is the recurring intermission theme.
    //
    // intermission is fixed at 150s (2:30); the song is sized so it ends
    // ~15s before the intermission timer expires.
    public static class MillionSongBundleLoader
    {
        public const string BundleKey = "manimal/million.bundle";

        private static readonly string[] PrefabAssetNameCandidates =
        {
            "million",
            "Million",
            "millionaire",
            "million_song",
        };

        private static GameObject _prefab;
        private static Task<GameObject> _loadTask;
        // tracks the most-recent spawned instance so we can destroy it
        // before spawning a fresh one (intermission re-trigger). prevents
        // overlapping audio if a previous Million(Clone) somehow survives
        // past its play length into the next intermission.
        private static GameObject _currentInstance;

        public static Task<GameObject> EnsureLoaded()
        {
            if (_prefab != null) return Task.FromResult(_prefab);
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        public static void ResetForNewRaid()
        {
            if (_currentInstance != null)
            {
                UnityEngine.Object.Destroy(_currentInstance);
                _currentInstance = null;
            }
        }

        // returns the spawned instance so the caller can watch the
        // AudioSource if needed. for consistency with TheOne / Beauty.
        // intentionally NOT once-per-raid - the intermission song needs
        // to fire at every wave clear.
        public static async Task<GameObject> SpawnUnder(Transform parent)
        {
            GameObject prefab = await EnsureLoaded();
            if (prefab == null)
            {
                Plugin.LogSource?.LogWarning("[MillionSong] prefab failed to load; song will not play.");
                return null;
            }

            // tear down any prior instance still hanging around. either
            // its playback ended already (harmless GameObject) or it's
            // still playing into our new intermission (would cause double
            // audio). either way, replace it.
            if (_currentInstance != null)
            {
                UnityEngine.Object.Destroy(_currentInstance);
                _currentInstance = null;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            if (parent != null) instance.transform.SetParent(parent, worldPositionStays: false);
            _currentInstance = instance;
            Plugin.LogSource?.LogInfo($"[MillionSong] spawned '{instance.name}' under '{parent?.name ?? "<root>"}'.");
            return instance;
        }

        private static async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource.LogError("IEasyAssets not initialized; cannot load Million song bundle.");
                    _loadTask = null;
                    return null;
                }

                IEasyAssets ea = Singleton<IEasyAssets>.Instance;
                Plugin.LogSource.LogInfo($"Retaining Million song bundle: {BundleKey}");

                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);

                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource.LogError($"Million song bundle '{BundleKey}' failed to load.");
                    _loadTask = null;
                    return null;
                }

                foreach (string candidate in PrefabAssetNameCandidates)
                {
                    _prefab = ea.GetAsset<GameObject>(BundleKey, candidate);
                    if (_prefab != null)
                    {
                        Plugin.LogSource.LogInfo($"Loaded Million song prefab (asset='{candidate}'): {_prefab.name}");
                        return _prefab;
                    }
                }

                Plugin.LogSource.LogWarning("IEasyAssets.GetAsset failed for all Million candidates. Falling back to AssetBundle enumeration.");
                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    string abName = ab.name ?? "";
                    if (!abName.Contains("million", StringComparison.OrdinalIgnoreCase)) continue;

                    Plugin.LogSource.LogInfo($"=== Bundle '{abName}' asset inventory ===");
                    foreach (string n in ab.GetAllAssetNames())
                        Plugin.LogSource.LogInfo($"  asset path: {n}");

                    GameObject[] gameObjects = ab.LoadAllAssets<GameObject>();
                    foreach (GameObject go in gameObjects)
                        Plugin.LogSource.LogInfo($"     GameObject: name='{go.name}'");

                    if (gameObjects.Length > 0)
                    {
                        _prefab = gameObjects[0];
                        Plugin.LogSource.LogInfo($"Million song fallback picked: {_prefab.name}");
                        return _prefab;
                    }
                }

                Plugin.LogSource.LogError($"No loaded AssetBundle matched 'million'. Bundle key: '{BundleKey}'");
                return null;
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Million song bundle load failed: {e}");
                _loadTask = null;
                return null;
            }
        }
    }
}
