using System;
using System.Collections.Generic;
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
    // sibling of SpawnBarWallbuyOnGameStartedPatch. spawns the SKS wallbuy
    // at raid start; SksWallbuyActionPatch injects the buy behavior.
    // preloads every viewmodel bundle the SKS build will need so
    // Player.Proceed can swap hands cleanly without a "bundle not loaded"
    // NRE on first buy.
    public class SpawnSksWallbuyOnGameStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            _ = SpawnAsync(__instance);
        }

        private static async Task SpawnAsync(GameWorld gw)
        {
            try
            {
                if (!Plugin.ZombiesMode)
                {
                    Plugin.LogSource.LogInfo("SksWallbuy: Zombies Mode is off; skipping spawn.");
                    return;
                }

                string mapId = gw?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(mapId))
                {
                    Plugin.LogSource.LogWarning("SksWallbuy: map id unknown, skipping spawn.");
                    return;
                }

                SksWallbuyMapSpawnConfig.Entry entry = SksWallbuyMapSpawnConfig.GetForMap(mapId);
                if (entry == null)
                {
                    Plugin.LogSource.LogInfo($"SksWallbuy: no config section for map '{mapId}', skipping.");
                    return;
                }
                if (!entry.TryGetTransform(out Vector3 pos, out Quaternion rot))
                {
                    Plugin.LogSource.LogInfo($"SksWallbuy: map '{mapId}' disabled or coords unparseable, skipping.");
                    return;
                }

                GameObject prefab = await WallbuyBundleLoader.Sks.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource.LogWarning("SksWallbuy: prefab not loaded from skswallbuy bundle; skipping spawn.");
                    return;
                }

                GameObject instance = UnityEngine.Object.Instantiate(prefab, pos, rot);
                if (gw != null && gw.transform != null)
                    instance.transform.SetParent(gw.transform, worldPositionStays: true);

                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) instance.layer = interactiveLayer;
                BoxCollider interactionTrigger = EnsureInteractionTrigger(instance.transform);

                SksWallbuy wallbuy = instance.AddComponent<SksWallbuy>();
                wallbuy.Configure($"skswallbuy:{mapId}:{instance.GetInstanceID()}", entry, interactionTrigger);

                Plugin.LogSource.LogInfo($"SksWallbuy: spawned on '{mapId}' at pos={pos} eul={rot.eulerAngles}");

                _ = PreloadBundlesAsync(SksWallbuyActionPatch.RequiredBundleTpls);
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"SksWallbuy spawn failed: {e}");
            }
        }

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
                Plugin.LogSource?.LogWarning("[SksWallbuy] no MeshRenderers under prefab root; trigger left at default 1x1x1 cube.");
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

        private static async Task PreloadBundlesAsync(string[] tpls)
        {
            try
            {
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                PoolManagerClass pool = Singleton<PoolManagerClass>.Instance;
                if (factory == null || pool == null) return;

                List<ResourceKey> keys = new List<ResourceKey>();
                foreach (string tpl in tpls)
                {
                    if (string.IsNullOrEmpty(tpl)) continue;
                    if (!factory.ItemTemplates.TryGetValue(tpl, out ItemTemplate template) || template == null) continue;
                    foreach (ResourceKey k in template.AllResources)
                    {
                        if (k == null || string.IsNullOrEmpty(k.path)) continue;
                        keys.Add(k);
                    }
                }
                if (keys.Count == 0) return;

                await pool.LoadBundlesAndCreatePools(
                    PoolManagerClass.PoolsCategory.Raid,
                    PoolManagerClass.AssemblyType.Local,
                    keys,
                    JobPriorityClass.Immediate,
                    null,
                    default(CancellationToken));

                Plugin.LogSource?.LogInfo($"[SksWallbuy] preloaded {keys.Count} bundle(s) for {tpls.Length} tpl(s).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[SksWallbuy] PreloadBundlesAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
