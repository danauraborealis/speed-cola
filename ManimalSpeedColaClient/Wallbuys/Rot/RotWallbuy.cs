using System.Collections;
using EFT.Interactive;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // sibling of UmpWallbuy. interactable marker on the spawned Rot wallbuy
    // prefab so Player.InteractionRaycast picks it up; RotWallbuyActionPatch
    // attaches the Buy entry.
    //
    // wires up the legacy Animation component (clips rot_idle / rot_buy /
    // rot_bought) and an optional "buysound" child AudioSource. clip names
    // mirror the convention used by UmpWallbuy/Mp43Wallbuy - if the bundle
    // doesn't ship any of these clips, the setup logs a warning and the
    // animation is skipped without breaking the buy flow.
    public sealed class RotWallbuy : InteractableObject
    {
        public string WallbuyId { get; private set; }
        public RotWallbuyMapSpawnConfig.Entry SpawnConfig { get; private set; }
        public BoxCollider InteractionTrigger { get; private set; }

        private AudioSource _buySound;
        private Vector3 _autoBoxSize;
        private Vector3 _autoBoxCenter;
        private BoxColliderVisualizer _visualizer;

        private Animation _animation;
        private bool _bought;

        private const string IdleClip   = "rot_idle";
        private const string BuyClip    = "rot_buy";
        private const string BoughtClip = "rot_bought";

        public void Configure(string id, RotWallbuyMapSpawnConfig.Entry config, BoxCollider trigger)
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

            SetupAnimations();

            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged += OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged += OnSpawnTransformChanged;
            }
            if (Plugin.RotWallbuyShowInteractionBounds != null) Plugin.RotWallbuyShowInteractionBounds.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.RotWallbuyInteractionBoxSize != null) Plugin.RotWallbuyInteractionBoxSize.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.RotWallbuyInteractionBoxCenter != null) Plugin.RotWallbuyInteractionBoxCenter.SettingChanged += OnInteractionConfigChanged;

            ApplyInteractionConfig();
        }

        public void PlayBuySound()
        {
            if (_buySound != null && !_buySound.isPlaying) _buySound.Play();
        }

        public void PlayBuyAnimation()
        {
            if (_bought || _animation == null) return;
            _bought = true;
            StartCoroutine(BuyAnimSequence());
        }

        private IEnumerator BuyAnimSequence()
        {
            AnimationClip buyClip = _animation.GetClip(BuyClip);
            if (buyClip == null)
            {
                Plugin.LogSource?.LogWarning($"[RotWallbuy] '{BuyClip}' clip not found; skipping buy animation.");
            }
            else
            {
                _animation.Play(BuyClip);
                yield return new WaitForSeconds(buyClip.length);
            }
            if (_animation.GetClip(BoughtClip) != null)
                _animation.Play(BoughtClip);
        }

        private void SetupAnimations()
        {
            _animation = GetComponentInChildren<Animation>(includeInactive: true);
            if (_animation == null)
            {
                Plugin.LogSource?.LogWarning("[RotWallbuy] no Animation component found on prefab; animations disabled.");
                return;
            }
            _animation.playAutomatically = false;

            foreach (AnimationState state in _animation)
            {
                if (state?.clip != null && !state.clip.legacy)
                    state.clip.legacy = true;
            }

            AnimationClip idleClip = _animation.GetClip(IdleClip);
            if (idleClip != null) idleClip.wrapMode = WrapMode.Loop;
            AnimationClip boughtClip = _animation.GetClip(BoughtClip);
            if (boughtClip != null) boughtClip.wrapMode = WrapMode.ClampForever;
            AnimationClip buyClip = _animation.GetClip(BuyClip);
            if (buyClip != null) buyClip.wrapMode = WrapMode.Once;

            if (idleClip != null)
                _animation.Play(IdleClip);
            else
                Plugin.LogSource?.LogWarning($"[RotWallbuy] '{IdleClip}' clip not found; cannot start idle.");
        }

        private void OnDestroy()
        {
            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged -= OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged -= OnSpawnTransformChanged;
            }
            if (Plugin.RotWallbuyShowInteractionBounds != null) Plugin.RotWallbuyShowInteractionBounds.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.RotWallbuyInteractionBoxSize != null) Plugin.RotWallbuyInteractionBoxSize.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.RotWallbuyInteractionBoxCenter != null) Plugin.RotWallbuyInteractionBoxCenter.SettingChanged -= OnInteractionConfigChanged;
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
            string sizeStr = Plugin.RotWallbuyInteractionBoxSize != null ? Plugin.RotWallbuyInteractionBoxSize.Value : "";
            string centerStr = Plugin.RotWallbuyInteractionBoxCenter != null ? Plugin.RotWallbuyInteractionBoxCenter.Value : "";

            InteractionTrigger.size = MapSpawnConfig.TryParseVec3(sizeStr, out Vector3 size) ? size : _autoBoxSize;
            InteractionTrigger.center = MapSpawnConfig.TryParseVec3(centerStr, out Vector3 center) ? center : _autoBoxCenter;

            bool show = Plugin.RotWallbuyShowInteractionBounds != null && Plugin.RotWallbuyShowInteractionBounds.Value;
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
            if (src == null) Plugin.LogSource?.LogWarning($"[RotWallbuy] '{childName}' has no AudioSource.");
            return src;
        }
    }
}
