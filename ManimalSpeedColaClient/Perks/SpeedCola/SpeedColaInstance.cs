using EFT;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // attached to each spawned speed cola machine. drives the RandomJingle
    // audio source on a randomized interval whenever the player is within
    // proximity. BuyJingle is left silent until interaction logic is added.
    public class SpeedColaInstance : MonoBehaviour
    {
        public Player Player;
        public MapSpawnConfig.Entry SpawnConfig;
        public BoxCollider InteractionTrigger;

        // set to true once the player has successfully bought from this
        // machine in the current raid. SpeedColaActionPatch reads this and
        // greys out the action so the perk can only be acquired once per raid.
        // resets implicitly when the raid ends (the machine GameObject is
        // destroyed alongside the GameWorld), AND on a Quick Revive downed
        // event (QuickReviveDownedState.Enter calls ResetSold on every
        // single-use perk machine so the player can re-buy after the wipe).
        public bool Used;

        public void ResetSold() { Used = false; }

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

            // belt-and-suspenders: the prefab has playOnAwake off on the
            // sources, but stop anything that did slip through.
            if (_randomJingle != null && _randomJingle.isPlaying) _randomJingle.Stop();
            if (_buyJingle != null && _buyJingle.isPlaying) _buyJingle.Stop();
            if (_dispense != null && _dispense.isPlaying) _dispense.Stop();

            // first-play offset is random within the max interval so multiple
            // machines on one map dont jingle in sync.
            float maxInterval = Plugin.JingleMaxInterval != null ? Plugin.JingleMaxInterval.Value : 45f;
            _nextPlayTime = Time.time + Random.Range(0f, maxInterval);

            SetupLight();
        }

        private void SetupLight()
        {
            Transform t = transform.Find("Light");
            if (t == null)
            {
                Plugin.LogSource.LogWarning("SpeedCola: 'Light' child not found on prefab, skipping light setup.");
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
            if (Plugin.LightColor != null) _light.color = Plugin.LightColor.Value;
            if (Plugin.LightIntensity != null) _light.intensity = Plugin.LightIntensity.Value;
            if (Plugin.LightRange != null) _light.range = Plugin.LightRange.Value;
        }

        private void OnLightConfigChanged(object sender, System.EventArgs e) => ApplyLightConfig();

        // called by SpawnOnGameStartedPatch after AddComponent. Awake has
        // already run; this wires up the live-edit subscriptions so editing
        // Position / Rotation in F12 ConfigurationManager moves the spawned
        // machine in real time.
        public void Initialize(Player player, MapSpawnConfig.Entry config, BoxCollider interactionTrigger)
        {
            Player = player;
            SpawnConfig = config;
            InteractionTrigger = interactionTrigger;

            // remember the auto-fit dimensions so we can fall back to them
            // when the user clears the override strings.
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
            if (Plugin.LightColor != null) Plugin.LightColor.SettingChanged += OnLightConfigChanged;
            if (Plugin.LightIntensity != null) Plugin.LightIntensity.SettingChanged += OnLightConfigChanged;
            if (Plugin.LightRange != null) Plugin.LightRange.SettingChanged += OnLightConfigChanged;

            if (Plugin.SpeedColaShowInteractionBounds != null) Plugin.SpeedColaShowInteractionBounds.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.SpeedColaInteractionBoxSize != null) Plugin.SpeedColaInteractionBoxSize.SettingChanged += OnInteractionConfigChanged;
            if (Plugin.SpeedColaInteractionBoxCenter != null) Plugin.SpeedColaInteractionBoxCenter.SettingChanged += OnInteractionConfigChanged;

            ApplyInteractionConfig();
        }

        private void OnDestroy()
        {
            if (SpawnConfig != null)
            {
                SpawnConfig.Position.SettingChanged -= OnSpawnTransformChanged;
                SpawnConfig.Rotation.SettingChanged -= OnSpawnTransformChanged;
            }
            if (Plugin.LightColor != null) Plugin.LightColor.SettingChanged -= OnLightConfigChanged;
            if (Plugin.LightIntensity != null) Plugin.LightIntensity.SettingChanged -= OnLightConfigChanged;
            if (Plugin.LightRange != null) Plugin.LightRange.SettingChanged -= OnLightConfigChanged;

            if (Plugin.SpeedColaShowInteractionBounds != null) Plugin.SpeedColaShowInteractionBounds.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.SpeedColaInteractionBoxSize != null) Plugin.SpeedColaInteractionBoxSize.SettingChanged -= OnInteractionConfigChanged;
            if (Plugin.SpeedColaInteractionBoxCenter != null) Plugin.SpeedColaInteractionBoxCenter.SettingChanged -= OnInteractionConfigChanged;
        }

        private void OnInteractionConfigChanged(object sender, System.EventArgs e) => ApplyInteractionConfig();

        private void ApplyInteractionConfig()
        {
            if (InteractionTrigger == null) return;

            string sizeStr = Plugin.SpeedColaInteractionBoxSize != null ? Plugin.SpeedColaInteractionBoxSize.Value : "";
            string centerStr = Plugin.SpeedColaInteractionBoxCenter != null ? Plugin.SpeedColaInteractionBoxCenter.Value : "";

            InteractionTrigger.size = MapSpawnConfig.TryParseVec3(sizeStr, out Vector3 size) ? size : _autoBoxSize;
            InteractionTrigger.center = MapSpawnConfig.TryParseVec3(centerStr, out Vector3 center) ? center : _autoBoxCenter;

            bool show = Plugin.SpeedColaShowInteractionBounds != null && Plugin.SpeedColaShowInteractionBounds.Value;
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

            // detect end-of-playback edge: schedule the NEXT play interval
            // starting from now, so the full jingle always plays out and a
            // random silent gap follows. (without this, a 30s jingle + 15s
            // min interval would play back-to-back with no gap.)
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

        // called by SpeedColaActionPatch when the player picks "Buy". Dispense
        // fires on every buy. BuyJingle is gated: skipped if RandomJingle is
        // currently playing. both sources also skip if already in flight from
        // a previous click.
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
                Plugin.LogSource.LogWarning($"SpeedCola: child '{childName}' not found on prefab.");
                return null;
            }
            AudioSource src = t.GetComponent<AudioSource>();
            if (src == null) Plugin.LogSource.LogWarning($"SpeedCola: '{childName}' has no AudioSource.");
            return src;
        }
    }
}
