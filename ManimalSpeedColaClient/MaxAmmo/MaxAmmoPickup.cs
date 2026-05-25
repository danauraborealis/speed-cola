using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // attached to a spawned max_ammo_pickup prefab instance. handles:
    //   - vertical bob + Y-axis spin animation (CoD Zombies power-up feel)
    //   - sphere trigger collider that fires OnTriggerEnter on the main player
    //   - on pickup: invoke MaxAmmoRestockHelper.Apply, log, destroy self
    //
    // configured with a TTL so an unclaimed power-up doesn't linger forever
    // (CoD Zombies max ammo despawns after ~30s of inactivity).
    public class MaxAmmoPickup : MonoBehaviour
    {
        // -- animation params --
        public float BobAmplitude  = 0.10f;   // meters of vertical sway
        public float BobFrequency  = 0.6f;    // hz
        public float SpinSpeedDeg  = 60f;     // deg/sec rotation around Y

        // -- trigger params --
        public float PickupRadius  = 0.4f;   // meters; sphere collider
        public float TimeToLive    = 30f;     // seconds before despawn

        // neon green matching the CoD Zombies max ammo aura. drives the
        // sparkle particle start + over-lifetime colors. (a dynamic point
        // light used to share this color too but was removed because EFT's
        // forward-rendering light budget is touchy and an extra realtime
        // light per-pickup occasionally clobbered shadowmaps on weapon
        // viewmodels.)
        public Color  GlowColor       = new Color(0.25f, 1.0f, 0.35f, 1f);

        // -- sparkle params (code-built ParticleSystem) --
        public float  SparkleEmissionRate = 25f;   // particles per second
        public float  SparkleLifetime      = 0.7f; // seconds before each particle dies
        public float  SparkleStartSize     = 0.07f;
        public float  SparkleStartSpeed    = 0.6f; // outward burst velocity (m/s)
        public float  SparkleShapeRadius   = 0.25f;

        // -- internal --
        private Vector3 _basePos;
        private float _spawnTime;
        private bool _consumed;
        private SphereCollider _trigger;
        private ParticleSystem _sparkle;
        private Material _sparkleMaterial;

        private void Start()
        {
            _basePos = transform.position;
            _spawnTime = Time.time;

            // ensure a trigger collider exists. add one if the bundled prefab
            // doesn't ship with one. set as trigger so OnTriggerEnter fires
            // (instead of physical collision pushing the player around).
            _trigger = GetComponentInChildren<SphereCollider>();
            if (_trigger == null)
            {
                _trigger = gameObject.AddComponent<SphereCollider>();
                _trigger.radius = PickupRadius;
                _trigger.isTrigger = true;
            }
            else
            {
                _trigger.isTrigger = true;
                if (_trigger.radius < PickupRadius) _trigger.radius = PickupRadius;
            }

            // disable any other non-trigger colliders that ship with the prefab
            // so the pickup doesn't physically block the player. we still need
            // the trigger collider to detect entry.
            Collider[] cols = GetComponentsInChildren<Collider>(includeInactive: true);
            foreach (Collider c in cols)
            {
                if (c == _trigger) continue;
                c.isTrigger = true; // make all colliders triggers - same effect, simpler
            }

            BuildSparkles();
        }

        // shared lookup used by Start (to silence-on-spawn) and Consume (to
        // play-on-pickup). prefers a child named exactly "AudioPlayer"; falls
        // back to any AudioSource on the hierarchy so a future bundle rename
        // doesn't break the SFX entirely.
        private AudioSource FindConsumeAudio()
        {
            foreach (AudioSource candidate in GetComponentsInChildren<AudioSource>(includeInactive: true))
            {
                if (candidate != null && candidate.gameObject != null && candidate.gameObject.name == "AudioPlayer")
                    return candidate;
            }
            return GetComponentInChildren<AudioSource>(includeInactive: true);
        }

        // code-built ParticleSystem with Hidden/Internal-Colored as the
        // billboard material (same shader the through-wall visualizer uses,
        // proven to ship with this EFT/Unity build). configured for a slow
        // outward sparkle: small bursts emit per second from a sphere, each
        // particle fades + shrinks over its lifetime.
        //
        // we can't load Unity's "Standard Assets" particle prefabs at runtime
        // (those require editor-time asset imports), so everything here is
        // built procedurally via the ParticleSystem module API.
        private void BuildSparkles()
        {
            try
            {
                GameObject sparkleHost = new GameObject("MaxAmmoSparkles");
                sparkleHost.transform.SetParent(transform, worldPositionStays: false);

                _sparkle = sparkleHost.AddComponent<ParticleSystem>();

                // configure BEFORE the system starts playing - the
                // AddComponent<ParticleSystem> auto-plays with default
                // settings, so we stop, configure, then re-play.
                _sparkle.Stop(withChildren: true, stopBehavior: ParticleSystemStopBehavior.StopEmittingAndClear);

                var main = _sparkle.main;
                main.duration = 5f;
                main.loop = true;
                main.startLifetime = SparkleLifetime;
                main.startSize = SparkleStartSize;
                main.startSpeed = SparkleStartSpeed;
                main.startColor = new Color(GlowColor.r, GlowColor.g, GlowColor.b, 0.9f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.gravityModifier = -0.1f; // tiny upward drift, like sparks rising

                var emission = _sparkle.emission;
                emission.rateOverTime = SparkleEmissionRate;

                var shape = _sparkle.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = SparkleShapeRadius;

                // fade alpha over lifetime so particles soft-disappear.
                var colorOverLifetime = _sparkle.colorOverLifetime;
                colorOverLifetime.enabled = true;
                Gradient grad = new Gradient();
                grad.SetKeys(
                    new[]
                    {
                        new GradientColorKey(GlowColor, 0f),
                        new GradientColorKey(GlowColor, 1f),
                    },
                    new[]
                    {
                        new GradientAlphaKey(0.9f, 0f),
                        new GradientAlphaKey(0f,   1f),
                    });
                colorOverLifetime.color = grad;

                // shrink over lifetime so the dot tapers off instead of
                // popping out.
                var sizeOverLifetime = _sparkle.sizeOverLifetime;
                sizeOverLifetime.enabled = true;
                AnimationCurve sizeCurve = new AnimationCurve(
                    new Keyframe(0f, 1f),
                    new Keyframe(1f, 0f));
                sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

                // renderer setup. Hidden/Internal-Colored is the same shader
                // BoxColliderVisualizer uses - guaranteed
                // present in this EFT build. set additive blend so the
                // sparkles read as glowing dots, not flat squares.
                var renderer = _sparkle.GetComponent<ParticleSystemRenderer>();
                if (renderer != null)
                {
                    Shader shader = Shader.Find("Hidden/Internal-Colored");
                    if (shader != null)
                    {
                        _sparkleMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        _sparkleMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        _sparkleMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One); // additive
                        _sparkleMaterial.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
                        _sparkleMaterial.SetInt("_ZWrite",   0);
                        renderer.material = _sparkleMaterial;
                    }
                    else
                    {
                        Plugin.LogSource?.LogWarning("[MaxAmmo] sparkles: Hidden/Internal-Colored shader missing; particles will render with Unity's default material.");
                    }
                    renderer.renderMode = ParticleSystemRenderMode.Billboard;
                }

                _sparkle.Play(withChildren: true);
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] BuildSparkles threw: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            if (_sparkleMaterial != null) DestroyImmediate(_sparkleMaterial);
        }

        private void Update()
        {
            // TTL: despawn unclaimed pickups.
            if (Time.time - _spawnTime > TimeToLive)
            {
                Plugin.LogSource?.LogInfo("[MaxAmmo] pickup expired (TTL); destroying.");
                Destroy(gameObject);
                return;
            }

            // bob + spin.
            float t = Time.time - _spawnTime;
            float sineWave = Mathf.Sin(t * BobFrequency * 2f * Mathf.PI);
            Vector3 pos = _basePos;
            pos.y += sineWave * BobAmplitude;
            transform.position = pos;
            transform.Rotate(0f, SpinSpeedDeg * Time.deltaTime, 0f, Space.World);

            // proximity check fallback - in case Unity's trigger callbacks don't
            // fire for the player's rigidbody (EFT's Player has a unique collider
            // setup that occasionally misses OnTriggerEnter).
            //
            // we measure HORIZONTAL distance separately from vertical because
            // Player.Position is at the player's feet (floor level) and the
            // pickup floats at the dead zombie's head bone (~1.4-1.7m up).
            // a naive 3D distance check would never satisfy radius < spawn-
            // altitude even when the player is standing directly under it.
            if (_consumed) return;
            Player main = Singleton<GameWorld>.Instance?.MainPlayer;
            if (main == null) return;
            float r = _trigger != null ? _trigger.radius : PickupRadius;
            Vector3 d = main.Position - transform.position;
            float yGap = Mathf.Abs(d.y);
            d.y = 0f;
            // horizontal radius doubled vs the 3D sphere - "walk into" semantics
            // care about XZ overlap more than precise contact distance. vertical
            // band of 2.5m covers the full standing-player-under-floating-pickup
            // range without false-positives from floor-below pickups.
            float horizR = r * 2f;
            if (d.sqrMagnitude <= horizR * horizR && yGap <= 2.5f)
                Consume(main);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_consumed) return;
            // only the main player can pick this up. bots shouldn't trigger it.
            Player main = Singleton<GameWorld>.Instance?.MainPlayer;
            if (main == null) return;
            Player other_player = other.GetComponentInParent<Player>();
            if (other_player != main) return;
            Consume(main);
        }

        private void Consume(Player main)
        {
            _consumed = true;
            Plugin.LogSource?.LogInfo("[MaxAmmo] picked up - applying restock + repair.");
            try { MaxAmmoRestockHelper.Apply(main); }
            catch (System.Exception ex) { Plugin.LogSource?.LogError($"[MaxAmmo] Apply threw: {ex.Message}"); }

            // play the consume SFX from the bundled "AudioPlayer" child. it
            // gets detached from our transform first so that Destroy(gameObject)
            // below doesn't kill it mid-playback; the detached object cleans
            // itself up after the clip finishes.
            PlayAndDetachConsumeAudio();

            Destroy(gameObject);
        }

        // find the bundled AudioSource ("AudioPlayer" child by name; falls
        // back to any AudioSource in the hierarchy), reparent it to the
        // scene root so our pending Destroy doesn't take it with us, play it,
        // and schedule it for cleanup after the clip duration finishes.
        private void PlayAndDetachConsumeAudio()
        {
            try
            {
                AudioSource src = FindConsumeAudio();
                if (src == null)
                {
                    Plugin.LogSource?.LogWarning("[MaxAmmo] no AudioSource on pickup; skipping consume SFX.");
                    return;
                }

                // detach so our Destroy(gameObject) doesn't take this with us.
                src.transform.SetParent(null, worldPositionStays: true);

                // Play() restarts from 0 even if playOnAwake already triggered
                // playback during the floating-pickup phase.
                src.Play();

                float lifetime = (src.clip != null ? src.clip.length : 3f) + 0.5f;
                Destroy(src.gameObject, lifetime);
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] PlayAndDetachConsumeAudio threw: {ex.Message}");
            }
        }
    }
}
