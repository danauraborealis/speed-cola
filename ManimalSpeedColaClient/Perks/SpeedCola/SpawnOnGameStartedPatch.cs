using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // fires once at raid start. resolves the current map id, looks up the
    // per-map spawn config, and if enabled instantiates the speed cola machine
    // prefab at the configured position/rotation. parented to the GameWorld so
    // it lives for the whole raid and is cleaned up on raid teardown.
    public class SpawnOnGameStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            // bundle load is async; run the whole spawn flow on a fire-and-forget
            // task so we dont block OnGameStarted.
            _ = SpawnAsync(__instance);
        }

        private static async Task SpawnAsync(GameWorld gw)
        {
            try
            {
                if (!Plugin.ZombiesMode)
                {
                    Plugin.LogSource.LogInfo("SpeedCola: Zombies Mode is off; skipping spawn.");
                    return;
                }

                string mapId = gw?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(mapId))
                {
                    Plugin.LogSource.LogWarning("SpeedCola: map id unknown, skipping spawn.");
                    return;
                }

                MapSpawnConfig.Entry entry = MapSpawnConfig.GetForMap(mapId);
                if (entry == null)
                {
                    Plugin.LogSource.LogInfo($"SpeedCola: no config section for map '{mapId}', skipping.");
                    return;
                }
                if (!entry.TryGetTransform(out Vector3 pos, out Quaternion rot))
                {
                    Plugin.LogSource.LogInfo($"SpeedCola: map '{mapId}' disabled or coords unparseable, skipping.");
                    return;
                }

                GameObject prefab = await PerkMachineBundleLoader.SpeedCola.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource.LogWarning("SpeedCola: prefab not loaded, skipping spawn.");
                    return;
                }

                GameObject instance = Object.Instantiate(prefab, pos, rot);
                if (gw != null && gw.transform != null)
                    instance.transform.SetParent(gw.transform, worldPositionStays: true);

                // interaction setup: tarkov's interaction raycast queries the
                // "Interactive" layer. swap the root to that layer and add a
                // trigger collider sized to the visible mesh so the raycast has
                // something to land on. mirrors the HackerMod ATM discovery
                // pattern.
                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) instance.layer = interactiveLayer;
                BoxCollider interactionTrigger = EnsureInteractionTrigger(instance.transform);

                SpeedColaInstance ctrl = instance.AddComponent<SpeedColaInstance>();
                ctrl.Initialize(gw.MainPlayer, entry, interactionTrigger);

                SpeedColaMachine machine = instance.AddComponent<SpeedColaMachine>();
                machine.Configure($"speedcola:{mapId}:{instance.GetInstanceID()}");

                Plugin.LogSource.LogInfo($"SpeedCola: spawned on '{mapId}' at pos={pos} eul={rot.eulerAngles}");
            }
            catch (System.Exception e)
            {
                Plugin.LogSource.LogError($"SpeedCola spawn failed: {e}");
            }
        }

        // adds a trigger BoxCollider sized to the union of visible mesh
        // bounds. tarkovs interactable raycast hits triggers — this is what
        // makes the machine clickable. the prefab's existing MeshCollider
        // stays untouched for physics / bullets. returns the box so the
        // caller can hand it to SpeedColaInstance for live size/center
        // overrides + wireframe visualization.
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
                // no renderers to measure - leave default unit cube around origin.
                Plugin.LogSource?.LogWarning("[SpeedCola] no MeshRenderers under prefab root; trigger left at default 1x1x1 cube.");
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
    }
}
