using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // loads the Death Perception bot-replacement material from
    // manimal/death_perception_zombies_mat.bundle. material name in the
    // bundle is "Death_Perception_Default".
    //
    // unlike the older scan-line overlay (death_perception_scan.bundle),
    // this material is meant to SWAP the bot's body material directly -
    // its custom shader handles both the normal opaque pass AND the
    // through-wall X-ray effect natively, so we don't need a
    // CommandBuffer.DrawRenderer hack at AfterForwardAlpha anymore.
    //
    // DeathPerceptionEffectController loads this at Start; while the
    // perk is active, it swaps each in-range bot's SkinnedMeshRenderer
    // materials to a per-bot clone of this one and restores the
    // originals when the perk goes inactive.
    public static class DeathPerceptionBotMaterialLoader
    {
        public const string BundleKey = "manimal/death_perception_zombies_mat.bundle";

        private static readonly string[] MaterialCandidates =
        {
            "Death_Perception_Default",
            "DeathPerceptionDefault",
            "Death_Perception",
            "DeathPerception",
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
                    Plugin.LogSource?.LogInfo("[DeathPerception] IEasyAssets not ready; bot material not loaded yet.");
                    _loadTask = null;
                    return null;
                }
                IEasyAssets ea = Singleton<IEasyAssets>.Instance;

                Plugin.LogSource?.LogInfo($"[DeathPerception] retaining bot-material bundle: {BundleKey}");
                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);
                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource?.LogWarning($"[DeathPerception] bot-material bundle '{BundleKey}' not present; bot material swap disabled.");
                    _loadTask = null;
                    return null;
                }

                foreach (string candidate in MaterialCandidates)
                {
                    _material = ea.GetAsset<Material>(BundleKey, candidate);
                    if (_material != null)
                    {
                        Plugin.LogSource?.LogInfo($"[DeathPerception] loaded bot-replacement material (asset='{candidate}'): {_material.name}");
                        return _material;
                    }
                }

                // raw bundle scan fallback - dumps every asset name so we can
                // see what's actually in there if none of the candidates hit.
                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    string name = ab?.name ?? "";
                    if (!name.Contains("death_perception_zombies_mat", StringComparison.OrdinalIgnoreCase)) continue;
                    Plugin.LogSource?.LogInfo($"[DeathPerception] === bundle '{name}' asset inventory ===");
                    foreach (string n in ab.GetAllAssetNames()) Plugin.LogSource?.LogInfo($"  asset path: {n}");
                    Material[] mats = ab.LoadAllAssets<Material>();
                    if (mats.Length > 0)
                    {
                        _material = mats[0];
                        Plugin.LogSource?.LogInfo($"[DeathPerception] fallback picked bot material: {_material.name}");
                        return _material;
                    }
                }

                Plugin.LogSource?.LogWarning("[DeathPerception] no material found in bot-material bundle; swap disabled.");
                return null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] bot-material bundle load threw: {ex.Message}");
                _loadTask = null;
                return null;
            }
        }
    }
}
