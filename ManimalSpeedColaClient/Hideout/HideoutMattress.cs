using EFT.Interactive;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // marker on the hideout mattress GameObject. inherits InteractableObject
    // so tarkov's interaction raycast resolves us, then
    // HideoutMattressActionPatch builds the "Sleep" action.
    //
    // attached by HideoutMattressDiscoveryPatch at HideoutPlayerOwner.Init.
    //
    // wires up the same visualize/resize live-edit controls as SpeedCola and
    // Wallbuy via Plugin.MattressShowInteractionBounds / MattressInteractionBoxSize
    // / MattressInteractionBoxCenter. toggling/editing those mid-hideout
    // updates the trigger live.
    public sealed class HideoutMattress : InteractableObject
    {
        public string MattressId { get; private set; }
        public BoxCollider InteractionTrigger { get; private set; }

        private Vector3 _autoBoxSize;
        private Vector3 _autoBoxCenter;
        private BoxColliderVisualizer _visualizer;

        public void Configure(string id, BoxCollider trigger)
        {
            MattressId = id;
            InteractionTrigger = trigger;
            if (InteractionTrigger != null)
            {
                _autoBoxSize = InteractionTrigger.size;
                _autoBoxCenter = InteractionTrigger.center;
            }

            if (Plugin.MattressShowInteractionBounds != null) Plugin.MattressShowInteractionBounds.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.MattressInteractionBoxSize != null) Plugin.MattressInteractionBoxSize.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.MattressInteractionBoxCenter != null) Plugin.MattressInteractionBoxCenter.SettingChanged += OnInteractionConfigChanged;

            ApplyInteractionConfig();
        }

        private void OnDestroy()
        {
            if (Plugin.MattressShowInteractionBounds != null) Plugin.MattressShowInteractionBounds.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.MattressInteractionBoxSize != null) Plugin.MattressInteractionBoxSize.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.MattressInteractionBoxCenter != null) Plugin.MattressInteractionBoxCenter.SettingChanged -= OnInteractionConfigChanged;
        }

        private void OnInteractionConfigChanged(object sender, System.EventArgs e) => ApplyInteractionConfig();

        private void ApplyInteractionConfig()
        {
            if (InteractionTrigger == null) return;
            string sizeStr = Plugin.MattressInteractionBoxSize != null ? Plugin.MattressInteractionBoxSize.Value : "";
            string centerStr = Plugin.MattressInteractionBoxCenter != null ? Plugin.MattressInteractionBoxCenter.Value : "";

            InteractionTrigger.size = MapSpawnConfig.TryParseVec3(sizeStr, out Vector3 size) ? size : _autoBoxSize;
            InteractionTrigger.center = MapSpawnConfig.TryParseVec3(centerStr, out Vector3 center) ? center : _autoBoxCenter;

            bool show = Plugin.MattressShowInteractionBounds != null && Plugin.MattressShowInteractionBounds.Value;
            if (show && _visualizer == null)
            {
                _visualizer = gameObject.AddComponent<BoxColliderVisualizer>();
                _visualizer.Target = InteractionTrigger;
            }
            else if (!show && _visualizer != null)
            {
                Destroy(_visualizer);
                _visualizer = null;
            }
        }
    }
}
