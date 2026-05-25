using System.Collections;
using EFT.Interactive;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // sibling of UmpWallbuy / RotWallbuy / Mp43Wallbuy but for the BAR
    // (Browning Automatic Rifle - "choclitBar" in the preset registry).
    // bundle ships an Animation component with bar_idle / bar_buy /
    // bar_bought clips plus a fire_selector child mesh + buysound
    // AudioSource. clip names mirror the convention used by the other
    // animated wallbuys.
    public sealed class BarWallbuy : InteractableObject
    {
        public string WallbuyId { get; private set; }
        public BarWallbuyMapSpawnConfig.Entry SpawnConfig { get; private set; }
        public BoxCollider InteractionTrigger { get; private set; }

        private AudioSource _buySound;
        private Vector3 _autoBoxSize;
        private Vector3 _autoBoxCenter;
        private BoxColliderVisualizer _visualizer;

        private Animation _animation;
        private bool _bought;

        private const string IdleClip   = "bar_idle";
        private const string BuyClip    = "bar_buy";
        private const string BoughtClip = "bar_bought";

        public void Configure(string id, BarWallbuyMapSpawnConfig.Entry config, BoxCollider trigger)
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
            if (Plugin.BarWallbuyShowInteractionBounds != null) Plugin.BarWallbuyShowInteractionBounds.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.BarWallbuyInteractionBoxSize != null) Plugin.BarWallbuyInteractionBoxSize.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.BarWallbuyInteractionBoxCenter != null) Plugin.BarWallbuyInteractionBoxCenter.SettingChanged += OnInteractionConfigChanged;

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
                Plugin.LogSource?.LogWarning($"[BarWallbuy] '{BuyClip}' clip not found; skipping buy animation.");
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
                Plugin.LogSource?.LogWarning("[BarWallbuy] no Animation component found on prefab; animations disabled.");
                return;
            }
            _animation.playAutomatically = false;

            // EFT-exported clips arent flagged Legacy by default; the legacy
            // UnityEngine.Animation component refuses to play non-legacy
            // clips and GetClip returns null. flip the flag at runtime
            // before we touch any of them.
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
                Plugin.LogSource?.LogWarning($"[BarWallbuy] '{IdleClip}' clip not found; cannot start idle.");
        }

        private void OnDestroy()
        {
            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged -= OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged -= OnSpawnTransformChanged;
            }
            if (Plugin.BarWallbuyShowInteractionBounds != null) Plugin.BarWallbuyShowInteractionBounds.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.BarWallbuyInteractionBoxSize != null) Plugin.BarWallbuyInteractionBoxSize.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.BarWallbuyInteractionBoxCenter != null) Plugin.BarWallbuyInteractionBoxCenter.SettingChanged -= OnInteractionConfigChanged;
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
            string sizeStr = Plugin.BarWallbuyInteractionBoxSize != null ? Plugin.BarWallbuyInteractionBoxSize.Value : "";
            string centerStr = Plugin.BarWallbuyInteractionBoxCenter != null ? Plugin.BarWallbuyInteractionBoxCenter.Value : "";

            InteractionTrigger.size = MapSpawnConfig.TryParseVec3(sizeStr, out Vector3 size) ? size : _autoBoxSize;
            InteractionTrigger.center = MapSpawnConfig.TryParseVec3(centerStr, out Vector3 center) ? center : _autoBoxCenter;

            bool show = Plugin.BarWallbuyShowInteractionBounds != null && Plugin.BarWallbuyShowInteractionBounds.Value;
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
            if (src == null) Plugin.LogSource?.LogWarning($"[BarWallbuy] '{childName}' has no AudioSource.");
            return src;
        }
    }
}
