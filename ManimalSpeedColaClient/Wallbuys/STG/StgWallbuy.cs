using EFT.Interactive;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // sibling of UmpWallbuy / RotWallbuy / Mp43Wallbuy but for the STG-44
    // wallbuy. STG bundle ships with just stgCHALK (wall decal) + stock
    // (weapon-ghost) + buysound, no Animation component - so this is the
    // bare-bones variant of the wallbuy pattern: marker for the action
    // patch + buysound playback + interaction trigger overrides.
    public sealed class StgWallbuy : InteractableObject
    {
        public string WallbuyId { get; private set; }
        public StgWallbuyMapSpawnConfig.Entry SpawnConfig { get; private set; }
        public BoxCollider InteractionTrigger { get; private set; }

        private AudioSource _buySound;
        private Vector3 _autoBoxSize;
        private Vector3 _autoBoxCenter;
        private BoxColliderVisualizer _visualizer;

        public void Configure(string id, StgWallbuyMapSpawnConfig.Entry config, BoxCollider trigger)
        {
            WallbuyId = id;
            SpawnConfig = config;
            InteractionTrigger = trigger;
            if (InteractionTrigger != null)
            {
                _autoBoxSize = InteractionTrigger.size;
                _autoBoxCenter = InteractionTrigger.center;
            }

            _buySound = FindChildAudioSource("buysound");
            if (_buySound != null && _buySound.isPlaying) _buySound.Stop();

            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged += OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged += OnSpawnTransformChanged;
            }
            if (Plugin.StgWallbuyShowInteractionBounds != null) Plugin.StgWallbuyShowInteractionBounds.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.StgWallbuyInteractionBoxSize != null) Plugin.StgWallbuyInteractionBoxSize.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.StgWallbuyInteractionBoxCenter != null) Plugin.StgWallbuyInteractionBoxCenter.SettingChanged += OnInteractionConfigChanged;

            ApplyInteractionConfig();
        }

        // called by StgWallbuyActionPatch on Buy. plays the wallbuy's
        // "buysound" audio source if present and not already playing.
        public void PlayBuySound()
        {
            if (_buySound != null && !_buySound.isPlaying) _buySound.Play();
        }

        private void OnDestroy()
        {
            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged -= OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged -= OnSpawnTransformChanged;
            }
            if (Plugin.StgWallbuyShowInteractionBounds != null) Plugin.StgWallbuyShowInteractionBounds.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.StgWallbuyInteractionBoxSize != null) Plugin.StgWallbuyInteractionBoxSize.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.StgWallbuyInteractionBoxCenter != null) Plugin.StgWallbuyInteractionBoxCenter.SettingChanged -= OnInteractionConfigChanged;
        }

        private void OnSpawnTransformChanged(object sender, System.EventArgs e)
        {
            if (SpawnConfig == null) return;
            if (!SpawnConfig.TryParsePosRot(out Vector3 pos, out Quaternion rot)) return;
            transform.SetPositionAndRotation(pos, rot);
        }

        private void OnInteractionConfigChanged(object sender, System.EventArgs e) => ApplyInteractionConfig();

        private void ApplyInteractionConfig()
        {
            if (InteractionTrigger == null) return;
            string sizeStr = Plugin.StgWallbuyInteractionBoxSize != null ? Plugin.StgWallbuyInteractionBoxSize.Value : "";
            string centerStr = Plugin.StgWallbuyInteractionBoxCenter != null ? Plugin.StgWallbuyInteractionBoxCenter.Value : "";

            InteractionTrigger.size = MapSpawnConfig.TryParseVec3(sizeStr, out Vector3 size) ? size : _autoBoxSize;
            InteractionTrigger.center = MapSpawnConfig.TryParseVec3(centerStr, out Vector3 center) ? center : _autoBoxCenter;

            bool show = Plugin.StgWallbuyShowInteractionBounds != null && Plugin.StgWallbuyShowInteractionBounds.Value;
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

        private AudioSource FindChildAudioSource(string childName)
        {
            Transform t = transform.Find(childName);
            if (t == null) return null; // optional
            AudioSource src = t.GetComponent<AudioSource>();
            if (src == null) Plugin.LogSource?.LogWarning($"[StgWallbuy] '{childName}' has no AudioSource.");
            return src;
        }
    }
}
