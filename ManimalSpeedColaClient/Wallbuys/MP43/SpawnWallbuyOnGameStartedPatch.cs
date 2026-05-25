using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // sibling of SpawnOnGameStartedPatch but for the MP-43 wallbuy. fires once
    // at raid start: resolves the current map id, looks up the per-map wallbuy
    // config, and if enabled instantiates the wallbuy prefab. layered on the
    // "Interactive" layer with a trigger collider so tarkov's interaction
    // raycast picks it up.
    public class SpawnWallbuyOnGameStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            // central per-raid reset for the shared wallbuy tracker. piggy-
            // backed onto the MP-43 spawn patch because every wallbuy spawn
            // patch fires on OnGameStarted and we only need to call this
            // once per raid - this one happens to be alphabetically first
            // among the spawn-on-game-started patches we'd enable, but any
            // of them would work since the reset is idempotent.
            WallbuyAmmoTracker.ResetForNewRaid();
            _ = SpawnAsync(__instance);
        }

        private static async Task SpawnAsync(GameWorld gw)
        {
            try
            {
                if (!Plugin.ZombiesMode)
                {
                    Plugin.LogSource.LogInfo("Wallbuy: Zombies Mode is off; skipping spawn.");
                    return;
                }

                string mapId = gw?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(mapId))
                {
                    Plugin.LogSource.LogWarning("Wallbuy: map id unknown, skipping spawn.");
                    return;
                }

                WallbuyMapSpawnConfig.Entry entry = WallbuyMapSpawnConfig.GetForMap(mapId);
                if (entry == null)
                {
                    Plugin.LogSource.LogInfo($"Wallbuy: no config section for map '{mapId}', skipping.");
                    return;
                }
                if (!entry.TryGetTransform(out Vector3 pos, out Quaternion rot))
                {
                    Plugin.LogSource.LogInfo($"Wallbuy: map '{mapId}' disabled or coords unparseable, skipping.");
                    return;
                }

                GameObject prefab = await WallbuyBundleLoader.Mp43.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource.LogWarning("Wallbuy: prefab not loaded, skipping spawn.");
                    return;
                }

                GameObject instance = UnityEngine.Object.Instantiate(prefab, pos, rot);
                if (gw != null && gw.transform != null)
                    instance.transform.SetParent(gw.transform, worldPositionStays: true);

                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) instance.layer = interactiveLayer;
                BoxCollider interactionTrigger = EnsureInteractionTrigger(instance.transform);

                Mp43Wallbuy wallbuy = instance.AddComponent<Mp43Wallbuy>();
                wallbuy.Configure($"mp43wallbuy:{mapId}:{instance.GetInstanceID()}", entry, interactionTrigger);

                Plugin.LogSource.LogInfo($"Wallbuy: spawned on '{mapId}' at pos={pos} eul={rot.eulerAngles}");

                // pre-load every bundle the wallbuy weapon (root + each attached
                // mod) needs so Player.Proceed can swap hands without NREing.
                // each attached mod has its own viewmodel bundle - the root
                // weapon bundle alone isn't enough. only items in the player's
                // profile at raid start hit TarkovApplication's pre-bake pass;
                // wallbuy items are added mid-raid so we have to retain them
                // ourselves via PoolManagerClass.LoadBundlesAndCreatePools.
                _ = PreloadBundlesAsync(new[]
                {
                    Patches.Mp43WallbuyActionPatch.Mp43ItemTpl,
                    Patches.Mp43WallbuyActionPatch.Mp43BarrelTpl,
                    Patches.Mp43WallbuyActionPatch.Mp43StockTpl,
                    Patches.Mp43WallbuyActionPatch.BuckshotTpl,
                });
            }
            catch (System.Exception e)
            {
                Plugin.LogSource.LogError($"Wallbuy spawn failed: {e}");
            }
        }

        // same auto-fit BoxCollider trigger creation as SpeedCola - sized to
        // the union of visible MeshRenderer bounds, leaves any existing
        // physics colliders untouched.
        private static BoxCollider EnsureInteractionTrigger(Transform root)
        {
            foreach (BoxCollider existing in root.GetComponents<BoxCollider>())
            {
                if (existing != null && existing.isTrigger) return existing;
            }

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            BoxCollider box = root.gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;

            if (renderers.Length == 0)
            {
                Plugin.LogSource?.LogWarning("[Wallbuy] no MeshRenderers under prefab root; trigger left at default 1x1x1 cube.");
                return box;
            }

            Bounds worldBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                worldBounds.Encapsulate(renderers[i].bounds);

            Vector3 lossy = root.lossyScale;
            box.size = new Vector3(
                worldBounds.size.x / Mathf.Max(0.0001f, lossy.x),
                worldBounds.size.y / Mathf.Max(0.0001f, lossy.y),
                worldBounds.size.z / Mathf.Max(0.0001f, lossy.z));
            box.center = root.InverseTransformPoint(worldBounds.center);
            return box;
        }

        // asks PoolManagerClass to load + retain the resource bundles for each
        // tpl in the list (root weapon + every attached mod). mirrors the call
        // TarkovApplication uses during raid pre-bake (LoadBundlesAndCreatePools
        // on the Raid pool). resource keys come from ItemTemplate.AllResources
        // (Prefab + UsePrefab). preloading just the root weapon bundle isn't
        // enough because each attached mod (barrel, stock, etc) has its own
        // viewmodel bundle - missing any of them produces a "bundle not loaded"
        // NRE the moment Player.Proceed tries to instantiate the weapon's
        // ItemHandsController.
        private static async Task PreloadBundlesAsync(string[] tpls)
        {
            try
            {
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                PoolManagerClass pool = Singleton<PoolManagerClass>.Instance;
                if (factory == null || pool == null)
                {
                    Plugin.LogSource?.LogWarning("[Wallbuy] cannot preload bundles: factory or pool singleton missing.");
                    return;
                }

                List<ResourceKey> keys = new List<ResourceKey>();
                foreach (string tpl in tpls)
                {
                    if (string.IsNullOrEmpty(tpl)) continue;
                    if (!factory.ItemTemplates.TryGetValue(tpl, out ItemTemplate template) || template == null)
                    {
                        Plugin.LogSource?.LogWarning($"[Wallbuy] preload skipped: template '{tpl}' not found.");
                        continue;
                    }
                    foreach (ResourceKey k in template.AllResources)
                    {
                        if (k == null || string.IsNullOrEmpty(k.path)) continue;
                        keys.Add(k);
                    }
                }

                if (keys.Count == 0)
                {
                    Plugin.LogSource?.LogInfo("[Wallbuy] no resource keys to preload.");
                    return;
                }

                await pool.LoadBundlesAndCreatePools(
                    PoolManagerClass.PoolsCategory.Raid,
                    PoolManagerClass.AssemblyType.Local,
                    keys,
                    JobPriorityClass.Immediate,
                    null,
                    default(CancellationToken));

                Plugin.LogSource?.LogInfo($"[Wallbuy] preloaded {keys.Count} bundle(s) for {tpls.Length} tpl(s).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Wallbuy] PreloadBundlesAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
