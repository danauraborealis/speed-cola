using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // sibling of RoundStartBundleLoader for roundend.bundle - a "wave cleared"
    // audio stinger. same shape: 2D AudioSource Play-On-Awake on a child of
    // the root prefab, fires automatically on Instantiate, auto-destroyed
    // after a few seconds so it doesnt pile up under the wave host.
    public static class RoundEndBundleLoader
    {
        public const string BundleKey = "manimal/roundend.bundle";
        private const float AutoDestroyAfterSec = 10f;

        private static readonly string[] PrefabAssetNameCandidates =
        {
            "roundend",
            "RoundEnd",
            "round_end",
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
            // kick it manually. destroy time is sized to the clip so the
            // audio finishes playing before the object is torn down.
            AudioSource src = instance.GetComponentInChildren<AudioSource>(includeInactive: true);
            float destroyAfter = AutoDestroyAfterSec;
            if (src != null)
            {
                src.Play();
                if (src.clip != null) destroyAfter = src.clip.length + 1f;
            }
            else
            {
                Plugin.LogSource?.LogWarning("[RoundEnd] no AudioSource found on instantiated prefab.");
            }
            UnityEngine.Object.Destroy(instance, destroyAfter);
        }

        private static async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource.LogError("IEasyAssets not initialized; cannot load roundend bundle.");
                    _loadTask = null;
                    return null;
                }
                IEasyAssets ea = Singleton<IEasyAssets>.Instance;
                Plugin.LogSource.LogInfo($"Retaining roundend bundle: {BundleKey}");
                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);
                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource.LogError($"Roundend bundle '{BundleKey}' failed to load.");
                    _loadTask = null;
                    return null;
                }
                foreach (string candidate in PrefabAssetNameCandidates)
                {
                    _prefab = ea.GetAsset<GameObject>(BundleKey, candidate);
                    if (_prefab != null)
                    {
                        Plugin.LogSource.LogInfo($"Loaded roundend prefab (asset='{candidate}'): {_prefab.name}");
                        return _prefab;
                    }
                }
                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (ab.name == null || !ab.name.Contains("roundend", StringComparison.OrdinalIgnoreCase)) continue;
                    GameObject[] gos = ab.LoadAllAssets<GameObject>();
                    if (gos.Length > 0)
                    {
                        _prefab = gos[0];
                        Plugin.LogSource.LogInfo($"Roundend fallback picked: {_prefab.name}");
                        return _prefab;
                    }
                }
                Plugin.LogSource.LogError("No matching AssetBundle for roundend found.");
                return null;
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"Roundend bundle load failed: {e}");
                _loadTask = null;
                return null;
            }
        }
    }
}
