using EFT.Interactive;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // grenade-dispenser wallbuy. flat 200 TC per buy, 1 VOG-25 per buy, no
    // animation pass on the prefab (per user spec). still wires up:
    //   - interaction trigger (BoxCollider on the prefab root, auto-fit by
    //     the spawn patch from child MeshRenderer bounds)
    //   - F12 live-edit for the trigger size/center + visualize toggle
    //   - F12 live-edit for the spawn position/rotation (re-applies on change)
    //   - optional "buysound" child AudioSource (played on purchase if the
    //     bundle ships one; silently skipped otherwise)
    public sealed class NadeWallbuy : InteractableObject
    {
        public string WallbuyId { get; private set; }
        public NadeWallbuyMapSpawnConfig.Entry SpawnConfig { get; private set; }
        public BoxCollider InteractionTrigger { get; private set; }

        private AudioSource _buySound;
        private Vector3 _autoBoxSize;
        private Vector3 _autoBoxCenter;
        private BoxColliderVisualizer _visualizer;

        public void Configure(string id, NadeWallbuyMapSpawnConfig.Entry config, BoxCollider trigger)
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
                if (SpawnConfig.Scale != null)
                    SpawnConfig.Scale.SettingChanged += OnSpawnScaleChanged;
            }
            if (Plugin.NadeWallbuyShowInteractionBounds != null) Plugin.NadeWallbuyShowInteractionBounds.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.NadeWallbuyInteractionBoxSize != null) Plugin.NadeWallbuyInteractionBoxSize.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.NadeWallbuyInteractionBoxCenter != null) Plugin.NadeWallbuyInteractionBoxCenter.SettingChanged += OnInteractionConfigChanged;

            ApplyInteractionConfig();
        }

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
                if (SpawnConfig.Scale != null)
                    SpawnConfig.Scale.SettingChanged -= OnSpawnScaleChanged;
            }
            if (Plugin.NadeWallbuyShowInteractionBounds != null) Plugin.NadeWallbuyShowInteractionBounds.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.NadeWallbuyInteractionBoxSize != null) Plugin.NadeWallbuyInteractionBoxSize.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.NadeWallbuyInteractionBoxCenter != null) Plugin.NadeWallbuyInteractionBoxCenter.SettingChanged -= OnInteractionConfigChanged;
        }

        private void OnSpawnTransformChanged(object sender, System.EventArgs e)
        {
            if (SpawnConfig == null) return;
            if (!SpawnConfig.TryParsePosRot(out Vector3 pos, out Quaternion rot)) return;
            transform.SetPositionAndRotation(pos, rot);
        }

        private void OnSpawnScaleChanged(object sender, System.EventArgs e)
        {
            if (SpawnConfig == null) return;
            // localScale only - global scale gets compounded by parent transforms.
            transform.localScale = SpawnConfig.GetScaleOrOne();
        }

        private void OnInteractionConfigChanged(object sender, System.EventArgs e) => ApplyInteractionConfig();

        private void ApplyInteractionConfig()
        {
            if (InteractionTrigger == null) return;
            string sizeStr = Plugin.NadeWallbuyInteractionBoxSize != null ? Plugin.NadeWallbuyInteractionBoxSize.Value : "";
            string centerStr = Plugin.NadeWallbuyInteractionBoxCenter != null ? Plugin.NadeWallbuyInteractionBoxCenter.Value : "";

            InteractionTrigger.size = MapSpawnConfig.TryParseVec3(sizeStr, out Vector3 size) ? size : _autoBoxSize;
            InteractionTrigger.center = MapSpawnConfig.TryParseVec3(centerStr, out Vector3 center) ? center : _autoBoxCenter;

            bool show = Plugin.NadeWallbuyShowInteractionBounds != null && Plugin.NadeWallbuyShowInteractionBounds.Value;
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
            if (t == null) return null;
            AudioSource src = t.GetComponent<AudioSource>();
            return src;
        }
    }
}
