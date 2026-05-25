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
    // sibling of SpawnBarWallbuyOnGameStartedPatch. spawns the grenade
    // wallbuy at raid start (gated on Plugin.ZombiesMode and a per-map
    // config entry), and preloads the VOG-25 bundle so the dispense never
    // hits a cold-pool stall.
    public class SpawnNadeWallbuyOnGameStartedPatch : ModulePatch
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
                    Plugin.LogSource.LogInfo("NadeWallbuy: Zombies Mode is off; skipping spawn.");
                    return;
                }

                string mapId = gw?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(mapId))
                {
                    Plugin.LogSource.LogWarning("NadeWallbuy: map id unknown, skipping spawn.");
                    return;
                }

                NadeWallbuyMapSpawnConfig.Entry entry = NadeWallbuyMapSpawnConfig.GetForMap(mapId);
                if (entry == null)
                {
                    Plugin.LogSource.LogInfo($"NadeWallbuy: no config section for map '{mapId}', skipping.");
                    return;
                }
                if (!entry.TryGetTransform(out Vector3 pos, out Quaternion rot))
                {
                    Plugin.LogSource.LogInfo($"NadeWallbuy: map '{mapId}' disabled or coords unparseable, skipping.");
                    return;
                }

                GameObject prefab = await WallbuyBundleLoader.Nade.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource.LogWarning("NadeWallbuy: prefab not loaded from nadewallbuy bundle; skipping spawn.");
                    return;
                }

                GameObject instance = UnityEngine.Object.Instantiate(prefab, pos, rot);
                if (gw != null && gw.transform != null)
                    instance.transform.SetParent(gw.transform, worldPositionStays: true);

                // apply F12 scale knob before the trigger collider auto-fit
                // runs - the fit math reads lossyScale so the trigger comes
                // out the right size for the scaled mesh.
                instance.transform.localScale = entry.GetScaleOrOne();

                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) instance.layer = interactiveLayer;
                BoxCollider interactionTrigger = EnsureInteractionTrigger(instance.transform);

                NadeWallbuy wallbuy = instance.AddComponent<NadeWallbuy>();
                wallbuy.Configure($"nadewallbuy:{mapId}:{instance.GetInstanceID()}", entry, interactionTrigger);

                Plugin.LogSource.LogInfo($"NadeWallbuy: spawned on '{mapId}' at pos={pos} eul={rot.eulerAngles}");

                _ = PreloadBundlesAsync(NadeWallbuyActionPatch.RequiredBundleTpls);
            }
            catch (Exception e)
            {
                Plugin.LogSource.LogError($"NadeWallbuy spawn failed: {e}");
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
                Plugin.LogSource?.LogWarning("[NadeWallbuy] no MeshRenderers under prefab root; trigger left at default 1x1x1 cube.");
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

                Plugin.LogSource?.LogInfo($"[NadeWallbuy] preloaded {keys.Count} bundle(s) for {tpls.Length} tpl(s).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[NadeWallbuy] PreloadBundlesAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
