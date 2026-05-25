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
    // sibling of SpawnUmpWallbuyOnGameStartedPatch. spawns the Rot wallbuy at
    // raid start (gated on ZombiesMode). RotWallbuyActionPatch injects the
    // actual buy behavior. preloads every viewmodel bundle the Rot build
    // needs so the hand-swap doesn't NRE on the first Buy.
    public class SpawnRotWallbuyOnGameStartedPatch : ModulePatch
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
                    Plugin.LogSource.LogInfo("RotWallbuy: Zombies Mode is off; skipping spawn.");
                    return;
                }

                string mapId = gw?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(mapId))
                {
                    Plugin.LogSource.LogWarning("RotWallbuy: map id unknown, skipping spawn.");
                    return;
                }

                RotWallbuyMapSpawnConfig.Entry entry = RotWallbuyMapSpawnConfig.GetForMap(mapId);
                if (entry == null)
                {
                    Plugin.LogSource.LogInfo($"RotWallbuy: no config section for map '{mapId}', skipping.");
                    return;
                }
                if (!entry.TryGetTransform(out Vector3 pos, out Quaternion rot))
                {
                    Plugin.LogSource.LogInfo($"RotWallbuy: map '{mapId}' disabled or coords unparseable, skipping.");
                    return;
                }

                GameObject prefab = await WallbuyBundleLoader.Rot.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource.LogWarning("RotWallbuy: prefab not loaded from rotwallbuy bundle; skipping spawn.");
                    return;
                }

                GameObject instance = UnityEngine.Object.Instantiate(prefab, pos, rot);
                if (gw != null && gw.transform != null)
                    instance.transform.SetParent(gw.transform, worldPositionStays: true);

                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) instance.layer = interactiveLayer;
                BoxCollider interactionTrigger = EnsureInteractionTrigger(instance.transform);

                RotWallbuy wallbuy = instance.AddComponent<RotWallbuy>();
                wallbuy.Configure($"rotwallbuy:{mapId}:{instance.GetInstanceID()}", entry, interactionTrigger);

                Plugin.LogSource.LogInfo($"RotWallbuy: spawned on '{mapId}' at pos={pos} eul={rot.eulerAngles}");

                _ = PreloadBundlesAsync(RotWallbuyActionPatch.RequiredBundleTpls);
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"RotWallbuy spawn failed: {e}");
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
                Plugin.LogSource?.LogWarning("[RotWallbuy] no MeshRenderers under prefab root; trigger left at default 1x1x1 cube.");
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

                Plugin.LogSource?.LogInfo($"[RotWallbuy] preloaded {keys.Count} bundle(s) for {tpls.Length} tpl(s).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[RotWallbuy] PreloadBundlesAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
