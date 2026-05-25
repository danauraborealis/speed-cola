using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // sibling of SpawnDeathPerceptionOnGameStartedPatch. fires once at raid
    // start when ZombiesMode is on, resolves the current map id, looks up
    // the per-map Quick Revive config, and (if enabled) instantiates the
    // perk-machine prefab at the configured pose. also clears any leaked
    // charge from a previous raid via QuickReviveState.ResetForNewRaid().
    public class SpawnQuickReviveOnGameStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            QuickReviveState.ResetForNewRaid();
            QuickReviveDownedState.ResetForNewRaid();
            _ = SpawnAsync(__instance);
        }

        private static async Task SpawnAsync(GameWorld gw)
        {
            try
            {
                if (!Plugin.ZombiesMode)
                {
                    Plugin.LogSource.LogInfo("QuickRevive: Zombies Mode is off; skipping spawn.");
                    return;
                }

                string mapId = gw?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(mapId))
                {
                    Plugin.LogSource.LogWarning("QuickRevive: map id unknown, skipping spawn.");
                    return;
                }

                QuickReviveMapSpawnConfig.Entry entry = QuickReviveMapSpawnConfig.GetForMap(mapId);
                if (entry == null)
                {
                    Plugin.LogSource.LogInfo($"QuickRevive: no config section for map '{mapId}', skipping.");
                    return;
                }
                if (!entry.TryGetTransform(out Vector3 pos, out Quaternion rot))
                {
                    Plugin.LogSource.LogInfo($"QuickRevive: map '{mapId}' disabled or coords unparseable, skipping.");
                    return;
                }

                GameObject prefab = await PerkMachineBundleLoader.QuickRevive.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource.LogWarning("QuickRevive: prefab not loaded from quickrevive_machine.bundle; skipping spawn.");
                    return;
                }

                GameObject instance = Object.Instantiate(prefab, pos, rot);
                if (gw != null && gw.transform != null)
                    instance.transform.SetParent(gw.transform, worldPositionStays: true);

                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) instance.layer = interactiveLayer;
                BoxCollider interactionTrigger = EnsureInteractionTrigger(instance.transform);

                QuickReviveInstance ctrl = instance.AddComponent<QuickReviveInstance>();
                ctrl.Initialize(gw.MainPlayer, entry, interactionTrigger);

                QuickReviveMachine machine = instance.AddComponent<QuickReviveMachine>();
                machine.Configure($"quickrevive:{mapId}:{instance.GetInstanceID()}");

                Plugin.LogSource.LogInfo($"QuickRevive: spawned on '{mapId}' at pos={pos} eul={rot.eulerAngles}");
            }
            catch (System.Exception e)
            {
                Plugin.LogSource.LogError($"QuickRevive spawn failed: {e}");
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
                Plugin.LogSource?.LogWarning("[QuickRevive] no MeshRenderers under prefab root; trigger left at default 1x1x1 cube.");
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
