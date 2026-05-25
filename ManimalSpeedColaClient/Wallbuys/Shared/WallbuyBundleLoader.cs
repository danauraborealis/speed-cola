using System;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // single bundle loader covering every wallbuy prefab. each entry is a
    // static instance describing its bundle key + asset-name candidates +
    // AssetBundle-enumeration fallback match string. the load/retain/resolve
    // plumbing is shared - the previous per-weapon loaders were ~95%
    // duplicated.
    //
    // wallbuy bundles have NO usable-item dependencies (unlike the perk
    // machines that need moonshine/beer for the drink animation); they just
    // hold the weapon-vending prefab.
    //
    // NOTE: GClass1661 / GClass1857 are obfuscated SPT names; they may shift
    // between SPT releases.
    public class WallbuyBundleLoader
    {
        public static readonly WallbuyBundleLoader Mp43 = new WallbuyBundleLoader(
            label: "MP-43 wallbuy",
            bundleKey: "manimal/mp43wallbuy.bundle",
            prefabCandidates: new[] { "mp43wallbuy", "mp3wallbuy", "Mp43Wallbuy", "Mp3Wallbuy" },
            assetBundleFallbackMatch: "mp43wallbuy");

        public static readonly WallbuyBundleLoader Ump = new WallbuyBundleLoader(
            label: "UMP wallbuy",
            bundleKey: "manimal/umpwallbuy.bundle",
            prefabCandidates: new[] { "umpwallbuy", "UmpWallbuy", "ump_wallbuy", "ump45wallbuy", "Ump45Wallbuy" },
            assetBundleFallbackMatch: "umpwallbuy");

        public static readonly WallbuyBundleLoader Rot = new WallbuyBundleLoader(
            label: "Rot wallbuy",
            bundleKey: "manimal/rotwallbuy.bundle",
            prefabCandidates: new[] { "rotwallbuy", "RotWallbuy", "rot_wallbuy", "Rot" },
            assetBundleFallbackMatch: "rotwallbuy");

        public static readonly WallbuyBundleLoader Stg = new WallbuyBundleLoader(
            label: "STG wallbuy",
            bundleKey: "manimal/stgwallbuy.bundle",
            prefabCandidates: new[] { "stgwallbuy", "StgWallbuy", "stg_wallbuy", "stg44wallbuy", "Stg44Wallbuy" },
            assetBundleFallbackMatch: "stgwallbuy");

        public static readonly WallbuyBundleLoader Bar = new WallbuyBundleLoader(
            label: "BAR wallbuy",
            bundleKey: "manimal/barwallbuy.bundle",
            prefabCandidates: new[] { "barwallbuy", "BarWallbuy", "bar_wallbuy", "BARWallbuy", "choclitBarWallbuy" },
            assetBundleFallbackMatch: "barwallbuy");

        public static readonly WallbuyBundleLoader Sks = new WallbuyBundleLoader(
            label: "SKS wallbuy",
            bundleKey: "manimal/skswallbuy.bundle",
            prefabCandidates: new[] { "skswallbuy", "SksWallbuy", "sks_wallbuy", "SKSWallbuy", "zombiesSKSWallbuy" },
            assetBundleFallbackMatch: "skswallbuy");

        // grenade dispenser: flat 200 TC, 1 VOG-25 per buy, no animations.
        // bundle has no Animation component, so NadeWallbuy doesn't run a
        // buy-anim coroutine. asset-name candidates mirror the other wallbuys'
        // naming convention.
        public static readonly WallbuyBundleLoader Nade = new WallbuyBundleLoader(
            label: "Nade wallbuy",
            bundleKey: "manimal/nadewallbuy.bundle",
            prefabCandidates: new[] { "nadewallbuy", "NadeWallbuy", "nade_wallbuy", "grenadewallbuy", "GrenadeWallbuy" },
            assetBundleFallbackMatch: "nadewallbuy");

        public string Label { get; }
        public string BundleKey { get; }
        public GameObject Prefab => _prefab;

        private readonly string[] _prefabCandidates;
        private readonly string _assetBundleFallbackMatch;

        private GameObject _prefab;
        private Task<GameObject> _loadTask;

        private WallbuyBundleLoader(
            string label,
            string bundleKey,
            string[] prefabCandidates,
            string assetBundleFallbackMatch)
        {
            Label = label;
            BundleKey = bundleKey;
            _prefabCandidates = prefabCandidates;
            _assetBundleFallbackMatch = assetBundleFallbackMatch;
        }

        public Task<GameObject> EnsureLoaded()
        {
            if (_prefab != null) return Task.FromResult(_prefab);
            if (_loadTask != null) return _loadTask;
            _loadTask = LoadAsync();
            return _loadTask;
        }

        // pre-loads + retains the bundle backing a dispensed item's held
        // prefab (e.g. weapon_stg44_container.bundle for the STG-44).
        //
        // why this exists: EFT's PoolManagerClass.CreateItemAsync calls
        // GClass1857.GetAsset synchronously on the item's Prefab bundle
        // when SwitchToWeapon proceeds through its held-weapon animation
        // event. it does NOT retain or load the bundle first - it assumes
        // the bundle is already resident. for vanilla items present in
        // the player's profile at raid start, EFT pre-bakes all bundles
        // via Profile.GetAllPrefabPaths(true). modded wallbuy weapons
        // (STG-44, BAR, etc.) aren't in the profile pre-raid so they get
        // skipped, and the first SwitchToWeapon throws "bundle not loaded".
        //
        // wallbuy DispenseAsync calls this on the freshly-built weapon
        // before equipping + SwitchToWeapon. the await suspends until the
        // bundle is fully resident, then SwitchToWeapon's animation event
        // resolves the prefab cleanly.
        public static async Task EnsureItemBundleLoaded(Item item)
        {
            try
            {
                if (item == null) return;
                ResourceKey rk = item.Prefab;
                string key = rk?.path;
                if (string.IsNullOrEmpty(key)) return;
                if (!Singleton<IEasyAssets>.Instantiated) return;

                IEasyAssets ea = Singleton<IEasyAssets>.Instance;
                if (ea.IsAssetLoaded(key)) return;

                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { key });
                await GClass1857.LoadBundles(handle);

                if (!ea.IsAssetLoaded(key))
                    Plugin.LogSource?.LogWarning($"[Wallbuy] EnsureItemBundleLoaded: bundle '{key}' still not loaded after retain - SwitchToWeapon may throw.");
                else
                    Plugin.LogSource?.LogInfo($"[Wallbuy] pre-loaded bundle '{key}' for {item.TemplateId}.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Wallbuy] EnsureItemBundleLoaded({item?.TemplateId}) threw: {ex.Message}");
            }
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
                Plugin.LogSource.LogInfo($"Retaining {Label} bundle: {BundleKey}");

                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(new[] { BundleKey });
                await GClass1857.LoadBundles(handle);

                if (!ea.IsAssetLoaded(BundleKey))
                {
                    Plugin.LogSource.LogError($"{Label} bundle '{BundleKey}' failed to load.");
                    _loadTask = null;
                    return null;
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
