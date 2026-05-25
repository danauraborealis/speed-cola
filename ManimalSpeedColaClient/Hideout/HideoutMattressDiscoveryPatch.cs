using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // fires after HideoutPlayerOwner.Init. resets ZombiesMode and spawns a
    // poller that waits for an active matress under "09_rest_space" to
    // appear, then wires it up as an interactable.
    //
    // does the polling rather than scanning immediately because the hideout
    // scene's level/upgrade activations happen ASYNC AFTER Init - scanning
    // at Init time finds everything inactive.
    //
    // does nothing in raid - HideoutPlayerOwner only exists in the hideout.
    internal sealed class HideoutMattressDiscoveryPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.Init));

        [PatchPostfix]
        private static void Postfix()
        {
            try
            {
                if (Plugin.ZombiesMode)
                {
                    Plugin.LogSource.LogInfo("HideoutMattress: resetting ZombiesMode flag on hideout entry.");
                    Plugin.ZombiesMode = false;
                }

                GameObject host = new GameObject("HideoutMattressDiscoveryPoller");
                HideoutMattressDiscoveryPoller poller = host.AddComponent<HideoutMattressDiscoveryPoller>();
                poller.WireUpCallback = WireUpMattress;
                Plugin.LogSource.LogInfo("HideoutMattress: spawned discovery poller.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"HideoutMattress discovery setup failed: {ex}");
            }
        }

        // invoked by the poller once it finds an active matress in 09_rest_space.
        private static void WireUpMattress(Transform matressTransform)
        {
            try
            {
                if (matressTransform == null) return;
                GameObject mattress = matressTransform.gameObject;
                if (mattress.GetComponent<HideoutMattress>() != null)
                {
                    Plugin.LogSource.LogInfo($"HideoutMattress: '{mattress.name}' already wired; skipping.");
                    return;
                }

                int interactiveLayer = LayerMask.NameToLayer("Interactive");
                if (interactiveLayer >= 0) mattress.layer = interactiveLayer;

                BoxCollider interactionTrigger = EnsureInteractionTrigger(mattress.transform);

                HideoutMattress component = mattress.AddComponent<HideoutMattress>();
                component.Configure($"matress:{mattress.GetInstanceID()}", interactionTrigger);

                Plugin.LogSource.LogInfo($"HideoutMattress: wired '{mattress.name}' as interactable.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"HideoutMattress wire-up failed: {ex}");
            }
        }

        // same auto-fit BoxCollider pattern as SpeedCola / Wallbuy. the
        // hideout matress already has its own mesh + physics collider; we add
        // a trigger on top so the interaction raycast lands.
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
                Plugin.LogSource?.LogWarning("[HideoutMattress] no MeshRenderers under matress; trigger left at default 1x1x1 cube.");
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
