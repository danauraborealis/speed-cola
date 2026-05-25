using System.Collections;
using EFT.Interactive;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // marker + interaction component on the spawned MP-43 wallbuy prefab.
    // inheriting EFT.Interactive.InteractableObject means tarkov's
    // Player.InteractionRaycast resolves us via GetComponentInParent so the
    // existing action-menu dispatcher kicks in; Mp43WallbuyActionPatch is what
    // injects the "Buy" entry for our type.
    public sealed class Mp43Wallbuy : InteractableObject
    {
        public string WallbuyId { get; private set; }
        public WallbuyMapSpawnConfig.Entry SpawnConfig { get; private set; }
        public BoxCollider InteractionTrigger { get; private set; }

        private AudioSource _buySound;
        private Vector3 _autoBoxSize;
        private Vector3 _autoBoxCenter;
        private BoxColliderVisualizer _visualizer;

        // legacy UnityEngine.Animation component lives on the gun-mesh child
        // (barrel_mr43e-1c_725mm_LOD0). holds the idle/buy/bought clips.
        private Animation _animation;
        private bool _bought;

        private const string IdleClip = "mp43_idle";
        private const string BuyClip = "mp43_buy";
        private const string BoughtClip = "mp43_bought";

        public void Configure(string id, WallbuyMapSpawnConfig.Entry config, BoxCollider trigger)
        {
            WallbuyId = id;
            SpawnConfig = config;
            InteractionTrigger = trigger;
            if (InteractionTrigger != null)
            {
                _autoBoxSize = InteractionTrigger.size;
                _autoBoxCenter = InteractionTrigger.center;
            }

            // find the audio source named "buysound" on a child gameobject.
            _buySound = FindChildAudioSource("buysound");
            if (_buySound != null && _buySound.isPlaying) _buySound.Stop();

            SetupAnimations();

            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged += OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged += OnSpawnTransformChanged;
            }
            if (Plugin.WallbuyShowInteractionBounds != null) Plugin.WallbuyShowInteractionBounds.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.WallbuyInteractionBoxSize != null) Plugin.WallbuyInteractionBoxSize.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.WallbuyInteractionBoxCenter != null) Plugin.WallbuyInteractionBoxCenter.SettingChanged += OnInteractionConfigChanged;

            ApplyInteractionConfig();
        }

        // called by Mp43WallbuyActionPatch on Buy. plays the wallbuys
        // "buysound" audio source if present and not already playing.
        public void PlayBuySound()
        {
            if (_buySound != null && !_buySound.isPlaying) _buySound.Play();
        }

        // exposed so other systems (e.g. supply-drop unlock) can borrow the
        // wallbuy "ka-ching" clip without needing their own bundle. null
        // until the prefab's buysound child has been resolved in Configure.
        public AudioClip BuySoundClip => _buySound != null ? _buySound.clip : null;

        // first Buy triggers the buy animation, which on completion latches
        // into the bought animation. subsequent Buys are no-op for the
        // animation (the wallbuy stays "bought"); the action patch still
        // dispenses the weapon, the visual just doesnt change.
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
                Plugin.LogSource?.LogWarning($"[Wallbuy] '{BuyClip}' clip not found; skipping buy animation.");
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
                Plugin.LogSource?.LogWarning("[Wallbuy] no Animation component found on prefab; animations disabled.");
                return;
            }
            _animation.playAutomatically = false;

            // EFT-exported AnimationClips arent flagged as Legacy, but the
            // legacy UnityEngine.Animation component refuses to play non-legacy
            // clips and GetClip returns null for them. flip the flag at runtime
            // before we touch any of them. iterate AnimationState entries
            // (Animation implements IEnumerable<AnimationState>).
            foreach (AnimationState state in _animation)
            {
                if (state?.clip != null && !state.clip.legacy)
                    state.clip.legacy = true;
            }

            // idle must loop so the wallbuy keeps spinning/floating/whatever
            // until purchase. bought clamps at its final frame so the "wall is
            // empty" pose holds without re-playing.
            AnimationClip idleClip = _animation.GetClip(IdleClip);
            if (idleClip != null) idleClip.wrapMode = WrapMode.Loop;

            AnimationClip boughtClip = _animation.GetClip(BoughtClip);
            if (boughtClip != null) boughtClip.wrapMode = WrapMode.ClampForever;

            AnimationClip buyClip = _animation.GetClip(BuyClip);
            if (buyClip != null) buyClip.wrapMode = WrapMode.Once;

            if (idleClip != null)
                _animation.Play(IdleClip);
            else
                Plugin.LogSource?.LogWarning($"[Wallbuy] '{IdleClip}' clip not found; cannot start idle.");
        }

        private void OnDestroy()
        {
            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged -= OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged -= OnSpawnTransformChanged;
            }
            if (Plugin.WallbuyShowInteractionBounds != null) Plugin.WallbuyShowInteractionBounds.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.WallbuyInteractionBoxSize != null) Plugin.WallbuyInteractionBoxSize.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.WallbuyInteractionBoxCenter != null) Plugin.WallbuyInteractionBoxCenter.SettingChanged -= OnInteractionConfigChanged;
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
            string sizeStr = Plugin.WallbuyInteractionBoxSize != null ? Plugin.WallbuyInteractionBoxSize.Value : "";
            string centerStr = Plugin.WallbuyInteractionBoxCenter != null ? Plugin.WallbuyInteractionBoxCenter.Value : "";

            InteractionTrigger.size = MapSpawnConfig.TryParseVec3(sizeStr, out Vector3 size) ? size : _autoBoxSize;
            InteractionTrigger.center = MapSpawnConfig.TryParseVec3(centerStr, out Vector3 center) ? center : _autoBoxCenter;

            bool show = Plugin.WallbuyShowInteractionBounds != null && Plugin.WallbuyShowInteractionBounds.Value;
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
            if (t == null)
            {
                Plugin.LogSource?.LogWarning($"[Wallbuy] child '{childName}' not found on prefab.");
                return null;
            }
            AudioSource src = t.GetComponent<AudioSource>();
            if (src == null) Plugin.LogSource?.LogWarning($"[Wallbuy] '{childName}' has no AudioSource.");
            return src;
        }
    }
}
