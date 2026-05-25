using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // on-screen feedback while Quick Revive's downed state is active:
    //   - flat full-screen red tint with a slow pulsing alpha (you're dying)
    //   - center text "DOWNED" + countdown subtitle "REVIVING IN X"
    //
    // self-driven from QuickReviveDownedState.RemainingSec - when downed
    // exits, the state's DestroyOverlay() removes this component. as a
    // safety net Update also self-destroys if the state flips out from
    // under us (e.g. raid teardown nulled the state without going through
    // Exit).
    //
    // legacy IMGUI like IntermissionMessageOverlay: no Canvas, no shader,
    // draws over every other UI layer.
    public class QuickReviveDownedOverlay : MonoBehaviour
    {
        // peak alpha of the red tint at the brightest point of each pulse.
        // bumped to 0.5 since this is now the only tint contribution -
        // previously a thinner full-screen wash was layered on top of edge
        // strips which together hit ~0.7 in the corners; this matches the
        // overall darkness without the framing artifact.
        public float TintPeakAlpha = 0.5f;
        public float PulseHz       = 0.7f; // pulses per second
        public Color TintColor     = new Color(0.85f, 0.05f, 0.05f, 1f);
        public Color TextColor     = new Color(1f, 0.18f, 0.18f, 1f);

        private Texture2D _whiteTex;
        private GUIStyle _titleStyle;
        private GUIStyle _subStyle;

        private void Awake()
        {
            // 1x1 white texture stretched fullscreen - cheapest way to get
            // a tintable solid-color quad without shipping an asset.
            _whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();
        }

        private void OnDestroy()
        {
            if (_whiteTex != null) Destroy(_whiteTex);
        }

        private void Update()
        {
            // safety net: if the QR state flipped out of downed without
            // calling our destroyer (e.g. via raid teardown nulling state),
            // self-clean.
            if (!QuickReviveDownedState.IsDowned) Destroy(gameObject);
        }

        private void OnGUI()
        {
            float w = Screen.width;
            float h = Screen.height;

            // pulse driver: sin wave on unscaled time so it doesn't freeze
            // during in-game pause menus. range 0..1, then * peak alpha.
            float pulse = (Mathf.Sin(Time.unscaledTime * PulseHz * Mathf.PI * 2f) * 0.5f) + 0.5f;
            float fillAlpha = pulse * TintPeakAlpha;

            // flat full-screen red tint (the "you're dying" wash).
            Color prev = GUI.color;
            GUI.color = new Color(TintColor.r, TintColor.g, TintColor.b, fillAlpha);
            GUI.DrawTexture(new Rect(0f, 0f, w, h), _whiteTex);

            // lazy-init text styles inside OnGUI (GUI.skin is null outside it).
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 56,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                };
                _subStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 26,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                };
            }

            // text: "DOWNED" + "REVIVING IN: X". Ceil so the timer ticks
            // from 5 -> 4 -> 3 -> 2 -> 1 -> 0 in whole numbers.
            float remaining = QuickReviveDownedState.RemainingSec;
            int remainingSec = Mathf.CeilToInt(remaining);
            string sub = remainingSec > 0
                ? $"REVIVING IN {remainingSec}..."
                : "REVIVING...";

            float titleY = h * 0.34f;
            float subY = titleY + 75f;
            Rect titleRect = new Rect(0f, titleY, w, 70f);
            Rect subRect   = new Rect(0f, subY,   w, 40f);

            // drop shadow first for legibility against the red wash.
            Color shadow = new Color(0f, 0f, 0f, 0.85f);
            GUI.color = shadow;
            GUI.Label(new Rect(titleRect.x + 3f, titleRect.y + 3f, titleRect.width, titleRect.height), "DOWNED", _titleStyle);
            GUI.Label(new Rect(subRect.x   + 2f, subRect.y   + 2f, subRect.width,   subRect.height),   sub,      _subStyle);

            GUI.color = TextColor;
            GUI.Label(titleRect, "DOWNED", _titleStyle);
            GUI.color = new Color(1f, 1f, 1f, 0.95f);
            GUI.Label(subRect, sub, _subStyle);

            GUI.color = prev;
        }
    }
}
