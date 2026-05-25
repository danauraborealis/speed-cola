using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // simple OnGUI overlay that displays a centered title + subtitle for a
    // limited duration with fade-in / hold / fade-out, then self-destroys.
    //
    // used for the supply-drop announcement at the start of each zombies
    // intermission ("SUPPLY DROP INCOMING - find it before the next wave").
    //
    // intentionally uses legacy IMGUI (OnGUI + GUI.Label) instead of a
    // worldspace Canvas: no setup overhead, no shader dependencies, draws
    // on top of every other UI layer automatically. cost is negligible
    // since OnGUI only runs while this component is alive (a few seconds).
    public class IntermissionMessageOverlay : MonoBehaviour
    {
        public string Title;
        public string Subtitle;
        public float FadeIn  = 0.4f;
        public float Hold    = 5f;
        public float FadeOut = 1.5f;

        // dark orange - only used when RainbowTitle is off. when rainbow
        // mode is on, this is ignored except for its alpha channel, which
        // still drives the fade.
        public Color TitleColor = new Color(0.95f, 0.4f, 0.05f, 1f);
        public Color SubColor   = new Color(1f, 1f, 1f, 0.85f);

        // rainbow title mode: cycles the title text's hue every frame for an
        // intentionally garish "supply drop incoming" alert. on by default
        // because the user specifically asked for "really annoying rainbow".
        public bool  RainbowTitle      = true;
        public float RainbowCycleSpeed = 1.5f; // full hue cycles per second

        private float _elapsed;
        private GUIStyle _titleStyle;
        private GUIStyle _subStyle;

        private void Update()
        {
            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed >= FadeIn + Hold + FadeOut) Destroy(gameObject);
        }

        private void OnGUI()
        {
            float alpha;
            if (_elapsed < FadeIn)
                alpha = _elapsed / FadeIn;
            else if (_elapsed < FadeIn + Hold)
                alpha = 1f;
            else
                alpha = Mathf.Clamp01(1f - (_elapsed - FadeIn - Hold) / FadeOut);

            // lazy-init styles inside OnGUI so we're definitely inside a GUI
            // context (GUI.skin is null outside it).
            if (_titleStyle == null)
            {
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 42,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                };
                _subStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22,
                    fontStyle = FontStyle.Normal,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                };
            }

            float w = Screen.width;
            float h = Screen.height;
            // sit slightly above center so the lower-third of the screen is
            // still readable - matches the convention from CoD-style alerts.
            float titleY = h * 0.38f;
            float subY   = titleY + 55f;
            Rect titleRect = new Rect(0f, titleY, w, 60f);
            Rect subRect   = new Rect(0f, subY,   w, 36f);

            // rainbow title: cycle hue with unscaled time so it doesn't freeze
            // during loading pauses. saturation + value pinned to 1 for max
            // garishness. alpha still tracks the fade.
            Color titleRgb;
            if (RainbowTitle)
            {
                float hue = (Time.unscaledTime * RainbowCycleSpeed) % 1f;
                if (hue < 0f) hue += 1f;
                titleRgb = Color.HSVToRGB(hue, 1f, 1f);
            }
            else
            {
                titleRgb = TitleColor;
            }
            Color title = new Color(titleRgb.r, titleRgb.g, titleRgb.b, TitleColor.a * alpha);
            Color sub   = new Color(SubColor.r,   SubColor.g,   SubColor.b,   SubColor.a   * alpha);
            Color shadow = new Color(0f, 0f, 0f, alpha * 0.85f);

            // drop shadow for legibility against bright skyboxes / muzzle
            // flashes / supply-crate-beacon glow.
            GUI.color = shadow;
            GUI.Label(new Rect(titleRect.x + 2f, titleRect.y + 2f, titleRect.width, titleRect.height), Title ?? "", _titleStyle);
            GUI.Label(new Rect(subRect.x   + 2f, subRect.y   + 2f, subRect.width,   subRect.height),   Subtitle ?? "", _subStyle);

            GUI.color = title;
            GUI.Label(titleRect, Title ?? "", _titleStyle);
            GUI.color = sub;
            GUI.Label(subRect, Subtitle ?? "", _subStyle);

            GUI.color = Color.white;
        }
    }
}
