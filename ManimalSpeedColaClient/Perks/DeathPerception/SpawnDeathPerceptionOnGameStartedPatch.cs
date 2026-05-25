using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // sibling of SpawnStaminupOnGameStartedPatch / SpawnJuggernogOnGameStartedPatch.
    // fires once at raid start when ZombiesMode is on, resolves the current map
    // id, looks up the per-map Death Perception config, and (if enabled)
    // instantiates the perk-machine prefab at the configured pose.
    public class SpawnDeathPerceptionOnGameStartedPatch : ModulePatch
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
                    Plugin.LogSource.LogInfo("DeathPerception: Zombies Mode is off; skipping spawn.");
                    return;
                }

                string mapId = gw?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(mapId))
                {
                    Plugin.LogSource.LogWarning("DeathPerception: map id unknown, skipping spawn.");
                    return;
                }

                DeathPerceptionMapSpawnConfig.Entry entry = DeathPerceptionMapSpawnConfig.GetForMap(mapId);
                if (entry == null)
                {
                    Plugin.LogSource.LogInfo($"DeathPerception: no config section for map '{mapId}', skipping.");
                    return;
                }
                if (!entry.TryGetTransform(out Vector3 pos, out Quaternion rot))
                {
                    Plugin.LogSource.LogInfo($"DeathPerception: map '{mapId}' disabled or coords unparseable, skipping.");
                    return;
                }

                GameObject prefab = await PerkMachineBundleLoader.DeathPerception.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource.LogWarning("DeathPerception: prefab not loaded from deathperception.bundle; skipping spawn.");
                    return;
                }

                GameObject instance = Object.Instantiate(prefab, pos, rot);
                if (gw != null && gw.transform != null)
                    instance.transform.SetParent(gw.transform, worldPositionStays: true);

                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) instance.layer = interactiveLayer;
                BoxCollider interactionTrigger = EnsureInteractionTrigger(instance.transform);

                DeathPerceptionInstance ctrl = instance.AddComponent<DeathPerceptionInstance>();
                ctrl.Initialize(gw.MainPlayer, entry, interactionTrigger);

                DeathPerceptionMachine machine = instance.AddComponent<DeathPerceptionMachine>();
                machine.Configure($"deathperception:{mapId}:{instance.GetInstanceID()}");

                Plugin.LogSource.LogInfo($"DeathPerception: spawned on '{mapId}' at pos={pos} eul={rot.eulerAngles}");
            }
            catch (System.Exception e)
            {
                Plugin.LogSource.LogError($"DeathPerception spawn failed: {e}");
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
                Plugin.LogSource?.LogWarning("[DeathPerception] no MeshRenderers under prefab root; trigger left at default 1x1x1 cube.");
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
