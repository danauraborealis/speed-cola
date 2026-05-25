using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // HideoutPlayerOwner.Init fires before EFT activates the right level
    // subtree under each hideout area, so a direct scan at Init finds every
    // matress/bed transform as active=False. this poller spawns at Init,
    // re-scans every half-second until it finds an ACTIVE matress whose path
    // contains "09_rest_space" (the rest-space hideout area), then hands off
    // to HideoutMattress + the trigger setup.
    //
    // gives up after TimeoutSeconds. self-destructs on success or timeout.
    public sealed class HideoutMattressDiscoveryPoller : MonoBehaviour
    {
        private const float TimeoutSeconds = 30f;
        private const float PollInterval = 0.5f;
        private const string RestSpaceMarker = "09_rest_space";

        // delegate the actual wire-up to the patch class so the
        // EnsureInteractionTrigger / layer-swap / component-add logic lives in
        // one place.
        public Action<Transform> WireUpCallback;

        private void Start()
        {
            StartCoroutine(PollForMattress());
        }

        private IEnumerator PollForMattress()
        {
            float deadline = Time.time + TimeoutSeconds;
            int ticks = 0;
            while (Time.time < deadline)
            {
                Transform pick = FindActiveRestSpaceMattress();
                if (pick != null)
                {
                    Plugin.LogSource?.LogInfo($"HideoutMattress: poller found active matress after {ticks} tick(s) at '{GetPath(pick)}'.");
                    WireUpCallback?.Invoke(pick);
                    Destroy(gameObject);
                    yield break;
                }
                ticks++;
                yield return new WaitForSeconds(PollInterval);
            }
            Plugin.LogSource?.LogWarning($"HideoutMattress: poller timed out after {TimeoutSeconds}s waiting for an active matress in {RestSpaceMarker}.");
            Destroy(gameObject);
        }

        private static Transform FindActiveRestSpaceMattress()
        {
            // scan all transforms (active+inactive) and pick one that's:
            //   1. named matress/mattress/bed
            //   2. activeInHierarchy = true
            //   3. path includes "09_rest_space"
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(includeInactive: true);
            return all.FirstOrDefault(t =>
                t != null
                && t.gameObject.activeInHierarchy
                && IsMattressName(t.name)
                && GetPath(t).IndexOf(RestSpaceMarker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsMattressName(string name)
        {
            return string.Equals(name, "matress",  StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "mattress", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "bed",      StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "bed (1)",  StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return string.Empty;
            string path = t.name;
            Transform p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }
    }
}
