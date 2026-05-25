using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // bundle loader for roundstart.bundle - a "wave incoming" audio stinger.
    // prefab root is named "roundstart" with a child of the same name that
    // holds a 2D AudioSource flagged Play On Awake. SpawnUnder() instantiates
    // it as a child of the wave host; the audio fires automatically.
    //
    // unlike TheOneSongBundleLoader there's no per-raid latch - this is
    // designed to fire every wave start, so each call spawns a fresh instance.
    // we auto-destroy the GameObject after AutoDestroyAfterSec so we don't
    // accumulate stinger objects under the host over a long raid.
    public static class RoundStartBundleLoader
    {
        public const string BundleKey = "manimal/roundstart.bundle";
        private const float AutoDestroyAfterSec = 10f;

        private static readonly string[] PrefabAssetNameCandidates =
        {
            "roundstart",
            "RoundStart",
            "round_start",
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

        public static async Task SpawnUnder(Transform parent)
        {
            GameObject prefab = await EnsureLoaded();
            if (prefab == null) return;
            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            if (parent != null) instance.transform.SetParent(parent, worldPositionStays: false);

            // bundle's AudioSource doesn't have Play On Awake set, so we
            // kick it manually. find it on the root or any descendant
            // (the prefab structure has it on a child named "roundstart").
            // destroy time = clip length + a small buffer so the audio
            // finishes playing before the GameObject is torn down.
            AudioSource src = instance.GetComponentInChildren<AudioSource>(includeInactive: true);
            float destroyAfter = AutoDestroyAfterSec;
            if (src != null)
            {
                src.Play();
                if (src.clip != null) destroyAfter = src.clip.length + 1f;
            }
            else
            {
                Plugin.LogSource?.LogWarning("[RoundStart] no AudioSource found on instantiated prefab.");
            }
            UnityEngine.Object.Destroy(instance, destroyAfter);
        }

        private static async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource.LogError("IEasyAssets not initialized; cannot load roundstart bundle.");
                    _loadTask = null;
                    return null;
                }
                IEasyAssets ea = Singleton<IEasyAssets>.Instance;
                Plugin.LogSource.LogInfo($"Retaining roundstart bundle: {BundleKey}");
                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);
                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource.LogError($"Roundstart bundle '{BundleKey}' failed to load.");
                    _loadTask = null;
                    return null;
                }
                foreach (string candidate in PrefabAssetNameCandidates)
                {
                    _prefab = ea.GetAsset<GameObject>(BundleKey, candidate);
                    if (_prefab != null)
                    {
                        Plugin.LogSource.LogInfo($"Loaded roundstart prefab (asset='{candidate}'): {_prefab.name}");
                        return _prefab;
                    }
                }
                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (ab.name == null || !ab.name.Contains("roundstart", StringComparison.OrdinalIgnoreCase)) continue;
                    GameObject[] gos = ab.LoadAllAssets<GameObject>();
                    if (gos.Length > 0)
                    {
                        _prefab = gos[0];
                        Plugin.LogSource.LogInfo($"Roundstart fallback picked: {_prefab.name}");
                        return _prefab;
                    }
                }
                Plugin.LogSource.LogError("No matching AssetBundle for roundstart found.");
                return null;
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Roundstart bundle load failed: {e}");
                _loadTask = null;
                return null;
            }
        }
    }
}
