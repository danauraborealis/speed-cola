using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // loads the Death Perception scan-line shader bundle (authored in
    // WTT-SDK-2022, see BundleSources/README.md for build steps). exposes
    // the bundled Material instance; DeathPerceptionEffectController
    // uses it if loaded, otherwise falls back to a Hidden/Internal-Colored
    // material as a placeholder.
    //
    // bundle key matches the manifest entry in ServerModFiles/bundles.json:
    //   manimal/death_perception_scan.bundle
    //
    // asset name candidates cover the common naming conventions for the
    // material's bundled name (matches the AssetBundle label suggested in
    // the build doc).
    public static class DeathPerceptionShaderBundleLoader
    {
        public const string BundleKey = "manimal/death_perception_scan.bundle";

        private static readonly string[] MaterialCandidates =
        {
            "DeathPerceptionScanMat",
            "DeathPerceptionScan",
            "DeathPerceptionScanMaterial",
            "DeathPerception",
            "scan",
        };

        private static Material _material;
        private static Task<Material> _loadTask;

        public static Task<Material> EnsureLoaded()
        {
            if (_material != null) return Task.FromResult(_material);
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        public static Material Cached => _material;

        public static void ResetForNewRaid()
        {
            _material = null;
            _loadTask = null;
        }

        private static async Task<Material> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource?.LogInfo("[DeathPerception] IEasyAssets not ready; scan shader not loaded yet.");
                    _loadTask = null;
                    return null;
                }
                IEasyAssets ea = Singleton<IEasyAssets>.Instance;

                Plugin.LogSource?.LogInfo($"[DeathPerception] retaining scan-shader bundle: {BundleKey}");
                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);
                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource?.LogInfo($"[DeathPerception] scan-shader bundle '{BundleKey}' not present; controller will use placeholder material.");
                    _loadTask = null;
                    return null;
                }

                foreach (string candidate in MaterialCandidates)
                {
                    _material = ea.GetAsset<Material>(BundleKey, candidate);
                    if (_material != null)
                    {
                        Plugin.LogSource?.LogInfo($"[DeathPerception] loaded custom scan-line material (asset='{candidate}'): {_material.name}");
                        return _material;
                    }
                }

                // raw bundle scan fallback - dumps every asset name so we can
                // see what's actually in there if none of the candidates hit.
                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    string name = ab?.name ?? "";
                    if (!name.Contains("death_perception_scan", StringComparison.OrdinalIgnoreCase)) continue;
                    Plugin.LogSource?.LogInfo($"[DeathPerception] === bundle '{name}' asset inventory ===");
                    foreach (string n in ab.GetAllAssetNames()) Plugin.LogSource?.LogInfo($"  asset path: {n}");
                    Material[] mats = ab.LoadAllAssets<Material>();
                    if (mats.Length > 0)
                    {
                        _material = mats[0];
                        Plugin.LogSource?.LogInfo($"[DeathPerception] fallback picked material: {_material.name}");
                        return _material;
                    }
                }

                Plugin.LogSource?.LogInfo("[DeathPerception] no material found in scan-shader bundle; using placeholder.");
                return null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] scan-shader bundle load threw: {ex.Message}");
                _loadTask = null;
                return null;
            }
        }
    }
}
