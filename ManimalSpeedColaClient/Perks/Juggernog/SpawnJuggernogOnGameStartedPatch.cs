using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // sibling of SpawnOnGameStartedPatch but for Juggernog. fires once at raid
    // start, resolves the current map id, looks up the per-map juggernog config,
    // and (if enabled) instantiates the perk-machine prefab at the configured
    // pose. layered on "Interactive" with a trigger collider so tarkov's
    // interaction raycast picks it up.
    //
    // PLACEHOLDER: this currently reuses the SpeedCola bundle (BundleLoader.
    // EnsureLoaded). once a dedicated juggernog_machine.bundle ships, swap to
    // a JuggernogBundleLoader (mirror of BundleLoader.cs with its own key).
    public class SpawnJuggernogOnGameStartedPatch : ModulePatch
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
                    Plugin.LogSource.LogInfo("Juggernog: Zombies Mode is off; skipping spawn.");
                    return;
                }

                string mapId = gw?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(mapId))
                {
                    Plugin.LogSource.LogWarning("Juggernog: map id unknown, skipping spawn.");
                    return;
                }

                JuggernogMapSpawnConfig.Entry entry = JuggernogMapSpawnConfig.GetForMap(mapId);
                if (entry == null)
                {
                    Plugin.LogSource.LogInfo($"Juggernog: no config section for map '{mapId}', skipping.");
                    return;
                }
                if (!entry.TryGetTransform(out Vector3 pos, out Quaternion rot))
                {
                    Plugin.LogSource.LogInfo($"Juggernog: map '{mapId}' disabled or coords unparseable, skipping.");
                    return;
                }

                GameObject prefab = await PerkMachineBundleLoader.Juggernog.EnsureLoaded();
                if (prefab == null)
                {
                    Plugin.LogSource.LogWarning("Juggernog: prefab not loaded from juggernog_machine.bundle; skipping spawn.");
                    return;
                }

                GameObject instance = Object.Instantiate(prefab, pos, rot);
                if (gw != null && gw.transform != null)
                    instance.transform.SetParent(gw.transform, worldPositionStays: true);

                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) instance.layer = interactiveLayer;
                BoxCollider interactionTrigger = EnsureInteractionTrigger(instance.transform);

                JuggernogInstance ctrl = instance.AddComponent<JuggernogInstance>();
                ctrl.Initialize(gw.MainPlayer, entry, interactionTrigger);

                JuggernogMachine machine = instance.AddComponent<JuggernogMachine>();
                machine.Configure($"juggernog:{mapId}:{instance.GetInstanceID()}");

                // attach the +max HP monitor as a child of the GameWorld - it
                // polls for the buff and applies +150 HP once when juggernog
                // becomes active. lives for the whole raid, dies with the world.
                if (gw != null && gw.transform != null && gw.GetComponentInChildren<JuggernogHpBoostMonitor>() == null)
                {
                    GameObject hpHost = new GameObject("JuggernogHpBoostHost");
                    hpHost.transform.SetParent(gw.transform, false);
                    hpHost.AddComponent<JuggernogHpBoostMonitor>();
                }

                // attach the cure-over-time monitor as a sibling host. ticks
                // every 3s while the buff is active and removes any active
                // LightBleeding / HeavyBleeding / Fracture effects, mirroring
                // the splint + Perfotoran cure mechanism (see JuggernogCureMonitor
                // for the FindActiveEffect<GInterface339/340/342> + ForceResidue
                // recipe).
                if (gw != null && gw.transform != null && gw.GetComponentInChildren<JuggernogCureMonitor>() == null)
                {
                    GameObject cureHost = new GameObject("JuggernogCureHost");
                    cureHost.transform.SetParent(gw.transform, false);
                    cureHost.AddComponent<JuggernogCureMonitor>();
                }

                Plugin.LogSource.LogInfo($"Juggernog: spawned on '{mapId}' at pos={pos} eul={rot.eulerAngles}");
            }
            catch (System.Exception e)
            {
                Plugin.LogSource.LogError($"Juggernog spawn failed: {e}");
            }
        }

        // identical pattern to SpawnOnGameStartedPatch.EnsureInteractionTrigger -
        // auto-fit BoxCollider trigger sized to the union of visible MeshRenderer
        // bounds.
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
                Plugin.LogSource?.LogWarning("[Juggernog] no MeshRenderers under prefab root; trigger left at default 1x1x1 cube.");
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
