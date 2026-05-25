using EFT;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // sibling of JuggernogInstance / SpeedColaInstance. owns the spawned
    // Staminup machine's lifecycle: proximity jingle, light color, interaction
    // trigger sizing, used-flag bookkeeping.
    //
    // shares the Plugin.Jingle* tunables with the other perks - the random
    // jingle timing/proximity behavior is the same across machines, only the
    // light color/interaction overrides are perk-specific.
    public class StaminupInstance : MonoBehaviour
    {
        public Player Player;
        public StaminupMapSpawnConfig.Entry SpawnConfig;
        public BoxCollider InteractionTrigger;

        private AudioSource _randomJingle;
        private AudioSource _buyJingle;
        private AudioSource _dispense;
        private Light _light;
        private BoxColliderVisualizer _visualizer;
        private Vector3 _autoBoxSize;
        private Vector3 _autoBoxCenter;
        private float _nextPlayTime;
        private bool _wasPlaying;

        private void Awake()
        {
            _randomJingle = FindChildAudioSource("RandomJingle");
            _buyJingle = FindChildAudioSource("BuyJingle");
            _dispense = FindChildAudioSource("Dispense");

            if (_randomJingle != null && _randomJingle.isPlaying) _randomJingle.Stop();
            if (_buyJingle != null && _buyJingle.isPlaying) _buyJingle.Stop();
            if (_dispense != null && _dispense.isPlaying) _dispense.Stop();

            float maxInterval = Plugin.JingleMaxInterval != null ? Plugin.JingleMaxInterval.Value : 45f;
            _nextPlayTime = Time.time + Random.Range(0f, maxInterval);

            SetupLight();
        }

        private void SetupLight()
        {
            Transform t = transform.Find("Light");
            if (t == null)
            {
                Plugin.LogSource.LogWarning("Staminup: 'Light' child not found on prefab, skipping light setup.");
                return;
            }
            _light = t.GetComponent<Light>();
            if (_light == null) _light = t.gameObject.AddComponent<Light>();
            _light.type = LightType.Point;
            ApplyLightConfig();
        }

        private void ApplyLightConfig()
        {
            if (_light == null) return;
            if (Plugin.StaminupLightColor != null) _light.color = Plugin.StaminupLightColor.Value;
            if (Plugin.StaminupLightIntensity != null) _light.intensity = Plugin.StaminupLightIntensity.Value;
            if (Plugin.StaminupLightRange != null) _light.range = Plugin.StaminupLightRange.Value;
        }

        private void OnLightConfigChanged(object sender, System.EventArgs e) => ApplyLightConfig();

        public void Initialize(Player player, StaminupMapSpawnConfig.Entry config, BoxCollider interactionTrigger)
        {
            Player = player;
            SpawnConfig = config;
            InteractionTrigger = interactionTrigger;

            if (InteractionTrigger != null)
            {
                _autoBoxSize = InteractionTrigger.size;
                _autoBoxCenter = InteractionTrigger.center;
            }

            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged += OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged += OnSpawnTransformChanged;
            }
            if (Plugin.StaminupLightColor != null) Plugin.StaminupLightColor.SettingChanged += OnLightConfigChanged;
            if (Plugin.StaminupLightIntensity != null) Plugin.StaminupLightIntensity.SettingChanged += OnLightConfigChanged;
            if (Plugin.StaminupLightRange != null) Plugin.StaminupLightRange.SettingChanged += OnLightConfigChanged;

            if (Plugin.StaminupShowInteractionBounds != null) Plugin.StaminupShowInteractionBounds.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.StaminupInteractionBoxSize != null) Plugin.StaminupInteractionBoxSize.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.StaminupInteractionBoxCenter != null) Plugin.StaminupInteractionBoxCenter.SettingChanged += OnInteractionConfigChanged;

            ApplyInteractionConfig();
        }

        private void OnDestroy()
        {
            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged -= OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged -= OnSpawnTransformChanged;
            }
            if (Plugin.StaminupLightColor != null) Plugin.StaminupLightColor.SettingChanged -= OnLightConfigChanged;
            if (Plugin.StaminupLightIntensity != null) Plugin.StaminupLightIntensity.SettingChanged -= OnLightConfigChanged;
            if (Plugin.StaminupLightRange != null) Plugin.StaminupLightRange.SettingChanged -= OnLightConfigChanged;

            if (Plugin.StaminupShowInteractionBounds != null) Plugin.StaminupShowInteractionBounds.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.StaminupInteractionBoxSize != null) Plugin.StaminupInteractionBoxSize.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.StaminupInteractionBoxCenter != null) Plugin.StaminupInteractionBoxCenter.SettingChanged -= OnInteractionConfigChanged;
        }

        private void OnInteractionConfigChanged(object sender, System.EventArgs e) => ApplyInteractionConfig();

        private void ApplyInteractionConfig()
        {
            if (InteractionTrigger == null) return;

            string sizeStr = Plugin.StaminupInteractionBoxSize != null ? Plugin.StaminupInteractionBoxSize.Value : "";
            string centerStr = Plugin.StaminupInteractionBoxCenter != null ? Plugin.StaminupInteractionBoxCenter.Value : "";

            InteractionTrigger.size = MapSpawnConfig.TryParseVec3(sizeStr, out Vector3 size) ? size : _autoBoxSize;
            InteractionTrigger.center = MapSpawnConfig.TryParseVec3(centerStr, out Vector3 center) ? center : _autoBoxCenter;

            bool show = Plugin.StaminupShowInteractionBounds != null && Plugin.StaminupShowInteractionBounds.Value;
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

        private void OnSpawnTransformChanged(object sender, System.EventArgs e)
        {
            if (SpawnConfig == null) return;
            if (!SpawnConfig.TryParsePosRot(out Vector3 pos, out Quaternion rot)) return;
            transform.SetPositionAndRotation(pos, rot);
        }

        private void Update()
        {
            if (_randomJingle == null || Player == null) return;

            bool isPlaying = _randomJingle.isPlaying;

            if (_wasPlaying && !isPlaying)
            {
                float min = Plugin.JingleMinInterval != null ? Plugin.JingleMinInterval.Value : 15f;
                float max = Plugin.JingleMaxInterval != null ? Plugin.JingleMaxInterval.Value : 45f;
                if (max < min) max = min;
                _nextPlayTime = Time.time + Random.Range(min, max);
            }
            _wasPlaying = isPlaying;

            if (isPlaying) return;
            if (Time.time < _nextPlayTime) return;

            float radius = Plugin.JingleProximityRadius != null ? Plugin.JingleProximityRadius.Value : 20f;
            float sqr = (Player.Position - transform.position).sqrMagnitude;
            if (sqr > radius * radius) return;

            _randomJingle.Play();
        }

        public bool Used { get; private set; }

        public void MarkUsed() { Used = true; }

        // called by QuickReviveDownedState.Enter when the player goes down -
        // the QR wipe clears the actual perk buff, this resets the machine
        // sold-out flag so the player can walk back over and re-buy.
        public void ResetSold() { Used = false; }

        public void PlayBuyJingle()
        {
            if (_dispense != null && !_dispense.isPlaying) _dispense.Play();
            if (_randomJingle != null && _randomJingle.isPlaying) return;
            if (_buyJingle != null && !_buyJingle.isPlaying) _buyJingle.Play();
        }

        private AudioSource FindChildAudioSource(string childName)
        {
            Transform t = transform.Find(childName);
            if (t == null)
            {
                Plugin.LogSource.LogWarning($"Staminup: child '{childName}' not found on prefab.");
                return null;
            }
            AudioSource src = t.GetComponent<AudioSource>();
            if (src == null) Plugin.LogSource.LogWarning($"Staminup: '{childName}' has no AudioSource.");
            return src;
        }
    }
}
