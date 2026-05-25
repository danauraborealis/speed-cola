using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Manimal.SpeedCola.Patches;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // Death Perception perk effect coordinator. while
    // DeathPerceptionBuffState.IsBuffActive() returns true:
    //
    //   1. every alive bot within RangeMeters has the Death_Perception_Default
    //      material (loaded from manimal/death_perception_zombies_mat.bundle)
    //      APPENDED as an extra slot at the end of each renderer's
    //      sharedMaterials, AND each original material's renderQueue is
    //      bumped to 3000 (Transparent) so the two passes render in lockstep.
    //      we do NOT replace originals - per the shader author's spec the
    //      DP material renders alongside the existing one.
    //   2. the active supply-drop crate gets the same treatment WITHOUT
    //      a range gate - visible map-wide so the player can hunt it
    //      from anywhere on Factory during the intermission.
    //   3. off-screen targets get small white arrows drawn at the screen
    //      edge pointing toward them, via OnGUI.
    //
    // per-target tinting: each target gets its OWN DP material clone so
    // we can drive _XRayEffectFade per distance AND apply a _Color tint
    // for special targets (crate = green, infected Tagilla = red). the
    // shared loaded material is never mutated - we always clone.
    //
    // when the buff goes inactive: each original material's renderQueue
    // is restored, the appended DP slot is dropped, and per-target clones
    // are destroyed.
    public class DeathPerceptionEffectController : MonoBehaviour
    {
        // -- tuning --
        public const float RangeMeters         = 40f;
        public const float TargetRefreshSec    = 0.25f;
        // seconds for the LOS occlusion fade to ramp from 0->1 (when a
        // bot ducks behind cover) or 1->0 (when stepping into clear
        // sight). 0.3s is the user-spec'd duration - slightly softer
        // than the original 0.2s.
        public const float OcclusionFadeSec    = 0.3f;
        // CrateRangeMultiplier was removed in favor of unlimited crate
        // visibility - see SyncCrateState for the always-on logic.

        // X-ray strength fades linearly with the bot's distance from the
        // player. closer = stronger (fade=1 at distance 0), range edge =
        // invisible (fade=0 at distance >= RangeMeters). driven into
        // _XRayEffectFade on each clone material every frame.
        private static readonly int XRayFadePropId = Shader.PropertyToID("_XRayEffectFade");

        // -- HUD arrows --
        public const float ArrowSize         = 24f;
        public const float ArrowEdgePadding  = 40f;
        public static readonly Color ArrowColor = new Color(1f, 1f, 1f, 0.9f);

        // per-target tint overrides applied to the DP clone's _Color so
        // the through-wall pass is recognizably this target's "kind":
        //   - supply-drop crate  -> green (find me)
        //   - infected Tagilla   -> red (kill me)
        //   - everything else    -> default DP material color (no clone tint)
        public static readonly Color CrateTint   = new Color(0.10f, 1.00f, 0.20f, 1f);
        public static readonly Color TagillaTint = new Color(1.00f, 0.10f, 0.10f, 1f);

        // queue we bump originals to while DP is active. per the shader
        // author: setting the existing material to render queue 3000
        // (Transparent bucket) lets the appended DP overlay layer with it
        // in the same render pass. doesn't actually make anything
        // transparent.
        private const int DpOverlayRenderQueue = 3000;

        private sealed class BotState
        {
            public Player Bot;
            public bool InRange;
            public float Fade;
            // smoothly-lerped LOS visibility multiplier. 1.0 when the bot
            // has been behind cover long enough for the fade-in to finish;
            // 0.0 when fully in sight. multiplied into the final shader
            // Fade so the overlay fades in/out instead of popping. while
            // OcclusionFade > 0 the DP overlay material stays attached;
            // hits exactly 0 and we strip + restore originals.
            public float OcclusionFade;
            // per-renderer: original sharedMaterials[] saved so we can
            // restore (drop the appended DP slot) on out-of-range / dead /
            // perk-off.
            public readonly Dictionary<Renderer, Material[]> SavedMaterials =
                new Dictionary<Renderer, Material[]>();
            // per original material: prior renderQueue saved so we restore
            // each one exactly when DP turns off. keyed by material instance
            // because multiple renderers can share a material reference.
            public readonly Dictionary<Material, int> SavedQueues =
                new Dictionary<Material, int>();
            // single DP material clone for this target. all of the target's
            // renderers append this same clone reference, so _XRayEffectFade
            // + tint apply uniformly across the whole bot/object with a
            // single SetFloat per frame.
            public Material DpOverlay;
            public bool Swapped => SavedMaterials.Count > 0;
        }

        private sealed class CrateState
        {
            public GameObject Crate;
            public bool InRange;
            public float Fade;
            public readonly Dictionary<Renderer, Material[]> SavedMaterials =
                new Dictionary<Renderer, Material[]>();
            public readonly Dictionary<Material, int> SavedQueues =
                new Dictionary<Material, int>();
            public Material DpOverlay;
            public bool Swapped => SavedMaterials.Count > 0;
        }

        // -- internal --
        private Camera _camera;
        private Material _xrayMaterial;
        private float _nextTargetRefresh;
        private readonly Dictionary<int, BotState> _botStates = new Dictionary<int, BotState>();
        private readonly CrateState _crateState = new CrateState();
        private GUIStyle _arrowStyle;
        private readonly List<int> _tmpRemoveKeys = new List<int>();

        private void Start()
        {
            try
            {
                _camera = ResolveWorldCamera();
                if (_camera == null)
                    Plugin.LogSource?.LogWarning("[DeathPerception] no world camera with Player layer in cullingMask; OnGUI arrows will not project correctly.");

                Material cached = DeathPerceptionBotMaterialLoader.Cached;
                if (cached != null)
                {
                    _xrayMaterial = cached;
                    Plugin.LogSource?.LogInfo($"[DeathPerception] using bot-replacement material '{_xrayMaterial.name}'.");
                }
                else
                {
                    _ = DeathPerceptionBotMaterialLoader.EnsureLoaded();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[DeathPerception] Start threw: {ex.Message}");
                enabled = false;
            }
        }

        private void OnDestroy()
        {
            foreach (var kv in _botStates)
                RestoreBotMaterials(kv.Value);
            _botStates.Clear();
            if (_crateState.Swapped) RestoreCrateMaterials();
        }

        private void Update()
        {
            try
            {
                if (_xrayMaterial == null)
                {
                    Material cached = DeathPerceptionBotMaterialLoader.Cached;
                    if (cached != null)
                    {
                        _xrayMaterial = cached;
                        Plugin.LogSource?.LogInfo($"[DeathPerception] late-loaded bot-replacement material '{_xrayMaterial.name}'.");
                    }
                }

                bool buffActive = DeathPerceptionBuffState.IsBuffActive();

                if (Time.unscaledTime >= _nextTargetRefresh)
                {
                    _nextTargetRefresh = Time.unscaledTime + TargetRefreshSec;
                    RefreshTargets(buffActive);
                }

                GameWorld gwForFade = Singleton<GameWorld>.Instance;
                Vector3 mainPosForFade = gwForFade?.MainPlayer?.Position ?? Vector3.zero;
                bool haveMainPos = gwForFade?.MainPlayer != null;

                Vector3 camPos = _camera != null ? _camera.transform.position : mainPosForFade;
                bool haveCamPos = _camera != null || haveMainPos;

                _tmpRemoveKeys.Clear();
                foreach (var kv in _botStates)
                {
                    BotState s = kv.Value;
                    if (s.Bot == null || !s.Bot.HealthController.IsAlive)
                        s.InRange = false;

                    // LOS gate with smooth fade. raw `occluded` is a step
                    // function (true/false from raycast); we lerp
                    // s.OcclusionFade toward 1 when occluded and 0 when
                    // visible at rate 1/OcclusionFadeSec, so the overlay
                    // smoothly ramps in/out over OcclusionFadeSec seconds.
                    // while OcclusionFade > 0 the overlay stays attached;
                    // once it lands exactly on 0 we strip and restore the
                    // originals.
                    //
                    // distance falloff is intentionally DISABLED at the
                    // shader level (see SwapBotMaterials - Start/End set
                    // outside gameplay range) so the overlay is full
                    // strength at any distance within RangeMeters. only
                    // the LOS lerp drives the visible intensity.
                    bool occluded = s.InRange && haveCamPos && IsBotOccluded(s.Bot, camPos);
                    float occluderTarget = occluded ? 1f : 0f;
                    float lerpStep = (OcclusionFadeSec > 0f ? 1f / OcclusionFadeSec : 1f) * Time.unscaledDeltaTime;
                    s.OcclusionFade = Mathf.MoveTowards(s.OcclusionFade, occluderTarget, lerpStep);

                    bool needsOverlay = s.OcclusionFade > 0f;
                    if (needsOverlay && _xrayMaterial != null)
                    {
                        // re-scan every tick so late-activating renderers
                        // (LOD swaps on the head as distance closes,
                        // equipment-driven renderer toggles, ragdoll
                        // secondaries) also get the overlay appended.
                        // SwapBotMaterials is idempotent via
                        // SavedMaterials.ContainsKey.
                        SwapBotMaterials(s);
                    }
                    else if (s.Swapped)
                    {
                        // fade fully drained; strip overlay + restore
                        // original sharedMaterials and renderQueues.
                        RestoreBotMaterials(s);
                    }

                    s.Fade = s.OcclusionFade;

                    if (s.Swapped) ApplyFadeToBotClones(s);

                    if (!s.InRange && !s.Swapped)
                        _tmpRemoveKeys.Add(kv.Key);
                }
                for (int i = 0; i < _tmpRemoveKeys.Count; i++)
                    _botStates.Remove(_tmpRemoveKeys[i]);

                SyncCrateState(buffActive, haveMainPos, mainPosForFade);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] Update threw: {ex.Message}");
            }
        }

        private void RefreshTargets(bool buffActive)
        {
            foreach (var kv in _botStates) kv.Value.InRange = false;
            if (!buffActive) return;

            GameWorld gw = Singleton<GameWorld>.Instance;
            Player main = gw?.MainPlayer;
            if (gw == null || main == null) return;

            Vector3 mainPos = main.Position;
            float r2 = RangeMeters * RangeMeters;

            foreach (Player p in gw.AllAlivePlayersList)
            {
                if (p == null || p == main) continue;
                if (!p.HealthController.IsAlive) continue;
                if ((p.Position - mainPos).sqrMagnitude > r2) continue;

                int key = p.GetInstanceID();
                if (!_botStates.TryGetValue(key, out BotState s))
                {
                    s = new BotState { Bot = p };
                    _botStates[key] = s;
                }
                s.Bot = p;
                s.InRange = true;
            }
        }

        // append a single DP-overlay clone to every renderer on the bot,
        // and bump each original material's renderQueue to 3000 so the
        // existing pass + the DP overlay render in lockstep (Transparent
        // queue bucket per the shader author's spec). originals + their
        // prior renderQueue are saved per-renderer / per-material so we
        // can restore exactly when DP turns off.
        private void SwapBotMaterials(BotState s)
        {
            if (s.Bot == null || _xrayMaterial == null) return;
            try
            {
                // one DP clone for the whole bot, shared across all its
                // renderers. cloned (not the loaded asset) so per-bot fade
                // + tint don't leak across targets. created lazily and
                // reused on re-scans - calling SwapBotMaterials repeatedly
                // (to pick up new LOD renderers) must NOT create extra clones.
                if (s.DpOverlay == null)
                {
                    // boss-level tint: infected Tagilla glows red so the player
                    // sees the big threat through walls even in a clump of
                    // regular zombies. other bots get an untinted DP overlay.
                    WildSpawnType role = s.Bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                    Color? tint = role == WildSpawnType.infectedTagilla ? (Color?)TagillaTint : null;
                    s.DpOverlay = BuildDpOverlayClone($"bot_{s.Bot.Profile?.Nickname ?? s.Bot.GetInstanceID().ToString()}", tint);
                }

                Renderer[] renderers = s.Bot.GetComponentsInChildren<Renderer>(includeInactive: false);
                int swapped = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer r = renderers[i];
                    if (!IsRenderable(r)) continue;
                    if (s.SavedMaterials.ContainsKey(r)) continue;

                    Material[] originals = r.sharedMaterials;
                    if (originals == null) continue;
                    s.SavedMaterials[r] = originals;

                    BumpOriginalsToOverlayQueue(originals, s.SavedQueues);
                    r.sharedMaterials = AppendOverlay(originals, s.DpOverlay);
                    swapped++;
                }
                if (swapped > 0)
                    Plugin.LogSource?.LogInfo($"[DeathPerception] appended DP overlay on {swapped} renderer(s) of bot '{s.Bot.Profile?.Nickname ?? "?"}' (queues bumped to {DpOverlayRenderQueue}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] SwapBotMaterials threw: {ex.Message}");
            }
        }

        private void RestoreBotMaterials(BotState s)
        {
            try
            {
                foreach (var kv in s.SavedMaterials)
                {
                    if (kv.Key == null) continue;
                    kv.Key.sharedMaterials = kv.Value;
                }
                RestoreOriginalQueues(s.SavedQueues);
                if (s.DpOverlay != null) Destroy(s.DpOverlay);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] RestoreBotMaterials threw: {ex.Message}");
            }
            finally
            {
                s.SavedMaterials.Clear();
                s.SavedQueues.Clear();
                s.DpOverlay = null;
            }
        }

        private static void ApplyFadeToBotClones(BotState s)
        {
            if (s.DpOverlay == null) return;
            s.DpOverlay.SetFloat(XRayFadePropId, s.Fade);
        }

        // creates a fresh DP-material clone with optional _Color tint. one
        // clone per target so per-distance fade + per-target color don't
        // bleed across other bots / the crate. hideFlags pin the clone out
        // of save scope; Destroy() on restore cleans up.
        private Material BuildDpOverlayClone(string label, Color? tint)
        {
            Material clone = new Material(_xrayMaterial)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = $"DP_Overlay_{label}"
            };

            // shader distance falloff intentionally degenerated to a
            // single point well beyond gameplay range so the in-range
            // visible region (0..RangeMeters) all gets full intensity.
            // user spec: no fading anywhere, just always-on overlay on
            // bots within RangeMeters. C# layer culls beyond RangeMeters
            // (InRange flag), so anything the shader actually renders is
            // inside the no-fade region.
            float farClamp = RangeMeters * 10f;
            if (clone.HasProperty("_XRayEffectFalloffStart")) clone.SetFloat("_XRayEffectFalloffStart", farClamp);
            if (clone.HasProperty("_XRayEffectFalloffEnd"))   clone.SetFloat("_XRayEffectFalloffEnd",   farClamp);

            if (tint.HasValue)
            {
                // _XRayColor is the actual tint uniform the new DP shader
                // exposes (see shader author's property dump). _Color is
                // ignored by this shader, so writes to it did nothing in
                // the previous iteration of this code.
                Color c = tint.Value;
                if (clone.HasProperty("_XRayColor")) clone.SetColor("_XRayColor", c);
            }
            return clone;
        }

        // returns a new sharedMaterials[] containing the originals followed
        // by `overlay` appended at the end. doesn't mutate originals.
        private static Material[] AppendOverlay(Material[] originals, Material overlay)
        {
            Material[] result = new Material[originals.Length + 1];
            for (int i = 0; i < originals.Length; i++) result[i] = originals[i];
            result[originals.Length] = overlay;
            return result;
        }

        // bumps each non-null original material's renderQueue to
        // DpOverlayRenderQueue. only records the prior value the FIRST time
        // we touch a given material (handles two renderers sharing a
        // material reference - we don't want to overwrite the saved prior
        // value with our own 3000 on the second pass).
        private static void BumpOriginalsToOverlayQueue(Material[] originals, Dictionary<Material, int> savedQueues)
        {
            for (int i = 0; i < originals.Length; i++)
            {
                Material m = originals[i];
                if (m == null) continue;
                if (!savedQueues.ContainsKey(m)) savedQueues[m] = m.renderQueue;
                m.renderQueue = DpOverlayRenderQueue;
            }
        }

        // restores each saved material to its prior renderQueue. safe if a
        // material was destroyed mid-raid (defensive null + try/catch).
        private static void RestoreOriginalQueues(Dictionary<Material, int> savedQueues)
        {
            foreach (var kv in savedQueues)
            {
                if (kv.Key == null) continue;
                try { kv.Key.renderQueue = kv.Value; }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"[DeathPerception] renderQueue restore threw for '{kv.Key.name}': {ex.Message}");
                }
            }
        }

        private void SyncCrateState(bool buffActive, bool haveMainPos, Vector3 mainPos)
        {
            GameObject crate = (buffActive ? SupplyDropSpawner.GetCurrentDropGameObject() : null);

            if (crate == null)
            {
                _crateState.Crate = null;
                _crateState.InRange = false;
                _crateState.Fade = 0f;
                if (_crateState.Swapped) RestoreCrateMaterials();
                return;
            }

            if (_crateState.Crate != null && _crateState.Crate != crate && _crateState.Swapped)
                RestoreCrateMaterials();
            _crateState.Crate = crate;

            // supply-drop crate is map-wide visible while the DP buff is
            // active: no range gate, no distance falloff. the user wants
            // the crate findable from anywhere on Factory so the
            // intermission hunt isn't about getting in range, it's about
            // pathing to it. always-on swap + Fade=1 (full xray intensity)
            // as long as the crate exists.
            _crateState.InRange = true;
            _crateState.Fade = 1f;

            if (!_crateState.Swapped && _xrayMaterial != null)
                SwapCrateMaterials();

            if (_crateState.Swapped) ApplyFadeToCrateClones();
        }

        private void SwapCrateMaterials()
        {
            if (_crateState.Crate == null || _xrayMaterial == null) return;
            try
            {
                _crateState.DpOverlay = BuildDpOverlayClone("crate", CrateTint);

                Renderer[] renderers = _crateState.Crate.GetComponentsInChildren<Renderer>(includeInactive: false);
                int swapped = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer r = renderers[i];
                    if (!IsRenderable(r)) continue;
                    if (_crateState.SavedMaterials.ContainsKey(r)) continue;

                    Material[] originals = r.sharedMaterials;
                    if (originals == null) continue;
                    _crateState.SavedMaterials[r] = originals;

                    BumpOriginalsToOverlayQueue(originals, _crateState.SavedQueues);
                    r.sharedMaterials = AppendOverlay(originals, _crateState.DpOverlay);
                    swapped++;
                }
                if (swapped > 0)
                    Plugin.LogSource?.LogInfo($"[DeathPerception] appended DP overlay on {swapped} renderer(s) of supply-drop crate (queues bumped to {DpOverlayRenderQueue}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] SwapCrateMaterials threw: {ex.Message}");
            }
        }

        private void RestoreCrateMaterials()
        {
            try
            {
                foreach (var kv in _crateState.SavedMaterials)
                {
                    if (kv.Key == null) continue;
                    kv.Key.sharedMaterials = kv.Value;
                }
                RestoreOriginalQueues(_crateState.SavedQueues);
                if (_crateState.DpOverlay != null) Destroy(_crateState.DpOverlay);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] RestoreCrateMaterials threw: {ex.Message}");
            }
            finally
            {
                _crateState.SavedMaterials.Clear();
                _crateState.SavedQueues.Clear();
                _crateState.DpOverlay = null;
            }
        }

        private void ApplyFadeToCrateClones()
        {
            if (_crateState.DpOverlay == null) return;
            _crateState.DpOverlay.SetFloat(XRayFadePropId, _crateState.Fade);
        }

        // line-of-sight check from the camera to three sample points on
        // the bot (head, chest, feet). returns true only if ALL three
        // samples are blocked by world geometry - so a bot peeking just
        // their head over a wall still counts as "visible" and we won't
        // draw the X-ray over the head-only sliver. uses the same
        // HighPolyWithTerrainMaskAI used by the wave controller's stuck-
        // zombie teleport LOS check so we hit walls/terrain/statics but
        // not bot characters (bots blocking other bots shouldn't count).
        private static bool IsBotOccluded(Player bot, Vector3 camPos)
        {
            if (bot == null) return false;
            Vector3 botBase = bot.Position;
            Vector3 head  = botBase + Vector3.up * 1.7f;
            Vector3 chest = botBase + Vector3.up * 1.2f;
            Vector3 feet  = botBase + Vector3.up * 0.3f;

            // any unblocked sample => bot is visible => not occluded.
            return IsLineBlocked(camPos, head)
                && IsLineBlocked(camPos, chest)
                && IsLineBlocked(camPos, feet);
        }

        private static bool IsLineBlocked(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.01f) return false;
            return Physics.Raycast(from, dir.normalized, dist, LayerMaskClass.HighPolyWithTerrainMaskAI);
        }

        private static bool IsRenderable(Renderer r)
        {
            if (r == null) return false;
            if (!r.enabled) return false;
            if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) return false;
            if (r is ParticleSystemRenderer) return false;
            if (r is TrailRenderer) return false;
            if (r is LineRenderer) return false;
            return true;
        }

        private static Camera ResolveWorldCamera()
        {
            try
            {
                int playerLayer = LayerMask.NameToLayer("Player");
                if (playerLayer < 0) return Camera.main;

                Camera best = null;
                int bestMask = 0;
                Camera[] cams = Camera.allCameras;
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera c = cams[i];
                    if (c == null) continue;
                    if ((c.cullingMask & (1 << playerLayer)) == 0) continue;

                    int popcount = PopCount(c.cullingMask);
                    if (popcount > bestMask)
                    {
                        bestMask = popcount;
                        best = c;
                    }
                }
                return best;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[DeathPerception] ResolveWorldCamera threw: {ex.Message}");
                return Camera.main;
            }
        }

        private static int PopCount(int v)
        {
            int n = 0;
            while (v != 0) { n += v & 1; v = (int)((uint)v >> 1); }
            return n;
        }

        private void OnGUI()
        {
            if (!DeathPerceptionBuffState.IsBuffActive()) return;
            if (_camera == null) return;

            if (_arrowStyle == null)
            {
                _arrowStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 26,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = ArrowColor },
                };
            }

            float w = Screen.width;
            float h = Screen.height;
            Vector2 center = new Vector2(w * 0.5f, h * 0.5f);

            foreach (var kv in _botStates)
            {
                BotState s = kv.Value;
                if (!s.InRange) continue;
                Player bot = s.Bot;
                if (bot == null) continue;

                Vector3 worldHead = TryGetHeadWorldPos(bot);
                Vector3 vp = _camera.WorldToViewportPoint(worldHead);

                bool onScreen = vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
                if (onScreen) continue;

                Vector2 screenPos;
                if (vp.z > 0f)
                    screenPos = new Vector2(vp.x * w, (1f - vp.y) * h);
                else
                    screenPos = new Vector2((1f - vp.x) * w, vp.y * h);

                Vector2 dir = (screenPos - center);
                if (dir.sqrMagnitude < 0.01f) continue;
                dir.Normalize();

                Vector2 edge = ClampToScreenEdge(center, dir, w, h, ArrowEdgePadding);

                float angleDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;

                Matrix4x4 prev = GUI.matrix;
                Color prevColor = GUI.color;
                GUIUtility.RotateAroundPivot(angleDeg, edge);
                GUI.color = ArrowColor;
                GUI.Label(
                    new Rect(edge.x - ArrowSize * 0.5f, edge.y - ArrowSize * 0.5f, ArrowSize, ArrowSize),
                    "▲",
                    _arrowStyle);
                GUI.matrix = prev;
                GUI.color = prevColor;
            }
        }

        private static Vector2 ClampToScreenEdge(Vector2 center, Vector2 dir, float w, float h, float pad)
        {
            float halfW = w * 0.5f - pad;
            float halfH = h * 0.5f - pad;
            float tx = (dir.x != 0f) ? halfW / Mathf.Abs(dir.x) : float.PositiveInfinity;
            float ty = (dir.y != 0f) ? halfH / Mathf.Abs(dir.y) : float.PositiveInfinity;
            float t = Mathf.Min(tx, ty);
            return center + dir * t;
        }

        private static Vector3 TryGetHeadWorldPos(Player bot)
        {
            try
            {
                Transform head = bot.PlayerBones?.Head?.Original;
                if (head != null) return head.position;
            }
            catch { }
            return bot.Position + Vector3.up * 1.6f;
        }
    }
}
