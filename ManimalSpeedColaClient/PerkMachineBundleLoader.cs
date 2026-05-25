using System;
using System.Threading.Tasks;
using Comfort.Common;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // single bundle loader covering every perk-machine prefab. each entry is
    // a static instance describing its bundle key + asset-name candidates +
    // optional configured-name override. the load/retain/resolve plumbing
    // (IEasyAssets retain, prefab lookup, AssetBundle enumeration fallback)
    // is shared - the previous per-machine BundleLoader files were ~95%
    // duplicated copies of this same flow.
    //
    // every instance pre-retains the same drink-prefab bundles (moonshine
    // loot/container + beer loot/container) because the perks are clones
    // off moonshine and use beer as their UsePrefab, so EFT's lazy-load
    // path can't find the consume animation when the player drinks one
    // unless those bundles are already resident.
    //
    // NOTE: GClass1661 / GClass1857 are obfuscated SPT names; they may shift
    // between SPT releases.
    public class PerkMachineBundleLoader
    {
        // historically each drink reused moonshine's Prefab + beer's UsePrefab,
        // so we had to pre-retain those vanilla bundles. each drink now ships
        // with its own dedicated *_loot.bundle + *_container.bundle, BUT we
        // still need to pre-retain them here because EFT's
        // PoolManagerClass.CreateItemUsablePrefabAsync calls
        // IEasyAssets.GetAsset directly on the UsePrefab bundle when a meds
        // animation starts - it does NOT call Retain() itself. The
        // dependencyKeys cascade in ServerModFiles/bundles.json only kicks in
        // when something Retains the parent key; without a pre-retain here
        // the consume animation throws "<bundle> is not loaded. You should
        // load it first." and leaves the hands controller in a broken state
        // (every subsequent MouseLook/LateUpdate then NREs forever).
        //
        // every machine pre-retains all three drinks because a drink item
        // can persist in the player's stash across raids and be consumed in
        // a raid that only spawned one of the perk machines.
        private static readonly string[] DrinkDependencyBundles =
        {
            "manimal/speed_cola_loot.bundle",
            "manimal/speed_cola_container.bundle",
            "manimal/juggernog_loot.bundle",
            "manimal/juggernog_container.bundle",
            "manimal/staminup_loot.bundle",
            "manimal/staminup_container.bundle",
            "manimal/death_perception_loot.bundle",
            "manimal/death_perception_container.bundle",
            "manimal/revive_loot.bundle",
            "manimal/revive_container.bundle",
        };

        public static readonly PerkMachineBundleLoader SpeedCola = new PerkMachineBundleLoader(
            label: "SpeedCola",
            bundleKey: "manimal/speedcola_machine.bundle",
            prefabCandidates: new[] { "SpeedColar", "SpeedCola", "speedcola_machine", "speedcola" },
            assetBundleFallbackMatch: "speedcola",
            configuredNameGetter: () => Plugin.PrefabAssetName?.Value);

        public static readonly PerkMachineBundleLoader Juggernog = new PerkMachineBundleLoader(
            label: "Juggernog",
            bundleKey: "manimal/juggernog_machine.bundle",
            prefabCandidates: new[] { "JuggernogMachine", "Juggernog", "juggernog_machine", "juggernog" },
            assetBundleFallbackMatch: "juggernog",
            configuredNameGetter: () => Plugin.JuggernogPrefabAssetName?.Value);

        public static readonly PerkMachineBundleLoader Staminup = new PerkMachineBundleLoader(
            label: "Staminup",
            bundleKey: "manimal/staminup_machine.bundle",
            prefabCandidates: new[] { "StaminupMachine", "Staminup", "staminup_machine", "staminup" },
            assetBundleFallbackMatch: "staminup",
            configuredNameGetter: () => Plugin.StaminupPrefabAssetName?.Value);

        // NOTE: the DP machine bundle key is the abbreviated "deathperception"
        // (no underscores, no _machine suffix) - that's how it ships in
        // ServerModFiles/bundles.json. dont chase the "death_perception_machine"
        // naming convention here.
        public static readonly PerkMachineBundleLoader DeathPerception = new PerkMachineBundleLoader(
            label: "DeathPerception",
            bundleKey: "manimal/deathperception.bundle",
            prefabCandidates: new[] { "DeathPerceptionMachine", "DeathPerception", "deathperception_machine", "deathperception", "death_perception_machine", "death_perception" },
            assetBundleFallbackMatch: "deathperception",
            configuredNameGetter: () => Plugin.DeathPerceptionPrefabAssetName?.Value);

        public static readonly PerkMachineBundleLoader QuickRevive = new PerkMachineBundleLoader(
            label: "QuickRevive",
            bundleKey: "manimal/quickrevive_machine.bundle",
            prefabCandidates: new[] { "QuickReviveMachine", "QuickRevive", "quickrevive_machine", "quickrevive", "qr" },
            assetBundleFallbackMatch: "quickrevive",
            configuredNameGetter: () => Plugin.QuickRevivePrefabAssetName?.Value);

        public string Label { get; }
        public string BundleKey { get; }
        public GameObject Prefab => _prefab;

        private readonly string[] _prefabCandidates;
        private readonly string _assetBundleFallbackMatch;
        private readonly Func<string> _configuredNameGetter;

        private GameObject _prefab;
        private Task<GameObject> _loadTask;

        private PerkMachineBundleLoader(
            string label,
            string bundleKey,
            string[] prefabCandidates,
            string assetBundleFallbackMatch,
            Func<string> configuredNameGetter)
        {
            Label = label;
            BundleKey = bundleKey;
            _prefabCandidates = prefabCandidates;
            _assetBundleFallbackMatch = assetBundleFallbackMatch;
            _configuredNameGetter = configuredNameGetter;
        }

        public Task<GameObject> EnsureLoaded()
        {
            if (_prefab != null) return Task.FromResult(_prefab);
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        private async Task<GameObject> LoadAsync()
        {
            try
            {
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource.LogError($"IEasyAssets not initialized; cannot load {Label} bundle.");
                    _loadTask = null;
                    return null;
                }
                IEasyAssets ea = Singleton<IEasyAssets>.Instance;

                string[] allKeys = new string[1 + DrinkDependencyBundles.Length];
                allKeys[0] = BundleKey;
                Array.Copy(DrinkDependencyBundles, 0, allKeys, 1, DrinkDependencyBundles.Length);
                Plugin.LogSource.LogInfo($"Retaining {Label} bundles: {string.Join(", ", allKeys)}");

                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(allKeys);
                await GClass1857.LoadBundles(handle);

                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource.LogError($"{Label} bundle '{BundleKey}' failed to load.");
                    _loadTask = null;
                    return null;
                }

                // configured-name first (lets the user override via F12 if
                // the bundle's root GameObject is named something the
                // candidate list doesn't cover).
                string configured = _configuredNameGetter?.Invoke();
                if (!string.IsNullOrEmpty(configured))
                {
                    _prefab = ea.GetAsset<GameObject>(BundleKey, configured);
                    if (_prefab != null)
                    {
                        Plugin.LogSource.LogInfo($"Loaded {Label} prefab (configured asset='{configured}'): {_prefab.name}");
                        return _prefab;
                    }
                }
                foreach (string candidate in _prefabCandidates)
                {
                    _prefab = ea.GetAsset<GameObject>(BundleKey, candidate);
                    if (_prefab != null)
                    {
                        Plugin.LogSource.LogInfo($"Loaded {Label} prefab (asset='{candidate}'): {_prefab.name}");
                        return _prefab;
                    }
                }

                // raw AssetBundle enumeration fallback - dumps every asset
                // name so the user can see what's actually in there and
                // either fix the prefab name or set the config override.
                Plugin.LogSource.LogWarning($"IEasyAssets.GetAsset failed for all {Label} candidates ({string.Join(", ", _prefabCandidates)}). Falling back to AssetBundle enumeration.");
                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    string abName = ab.name ?? "";
                    if (!abName.Contains(_assetBundleFallbackMatch, StringComparison.OrdinalIgnoreCase)) continue;

                    Plugin.LogSource.LogInfo($"=== {Label} bundle '{abName}' asset inventory ===");
                    foreach (string n in ab.GetAllAssetNames())
                        Plugin.LogSource.LogInfo($"  asset path: {n}");
                    GameObject[] gameObjects = ab.LoadAllAssets<GameObject>();
                    Plugin.LogSource.LogInfo($"  -> {gameObjects.Length} GameObject(s) loaded.");
                    if (gameObjects.Length > 0)
                    {
                        _prefab = gameObjects[0];
                        Plugin.LogSource.LogInfo($"{Label} fallback picked: {_prefab.name}");
                        return _prefab;
                    }
                }

                Plugin.LogSource.LogError($"No loaded AssetBundle matched '{_assetBundleFallbackMatch}' and no candidate name resolved via IEasyAssets. Bundle key: '{BundleKey}'.");
                return null;
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"{Label} bundle load failed: {e}");
                _loadTask = null;
                return null;
            }
        }
    }
}
