using Comfort.Common;
using EFT;
using Manimal.SpeedCola.Patches;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // OnGUI overlay drawing the current wave + remaining zombie count in the
    // top-right corner of the screen. polls a sibling ZombiesWaveController
    // each frame for state - no events, no listeners. only active while
    // zombies mode is running.
    //
    // also draws a small TarCoin balance panel directly beneath the wave
    // panel - same OnGUI, same anchor, just stacked.
    public class ZombiesWaveHUD : MonoBehaviour
    {
        private ZombiesWaveController _controller;
        private GUIStyle _bigStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _coinStyle;

        // displayed coin count - eases toward the real balance each frame
        // so changes "tick up/down" instead of snapping. tracked as float so
        // small per-frame deltas don't round to zero before we converge.
        private float _displayedCoins;
        private int _liveCoins;

        // coins/sec the displayed counter walks at. picked so a typical 50-
        // 100 point earn animates in ~half a second; large jumps (eg buying
        // a 3000 cola) feel weighty but don't drag. linear ramp - simple
        // and predictable.
        private const float TickRateMin = 60f;
        private const float TickRatePerDelta = 4f;

        private void Awake()
        {
            _controller = GetComponent<ZombiesWaveController>();
        }

        private void Update()
        {
            // poll the wallet outside OnGUI - OnGUI fires twice per render
            // pass (Layout + Repaint), so animating there double-steps.
            Player main = Singleton<GameWorld>.Instance?.MainPlayer;
            _liveCoins = main != null ? TarCoinWallet.Balance(main) : 0;

            float diff = _liveCoins - _displayedCoins;
            if (Mathf.Abs(diff) < 0.5f)
            {
                _displayedCoins = _liveCoins;
                return;
            }
            float rate = Mathf.Max(TickRateMin, Mathf.Abs(diff) * TickRatePerDelta);
            _displayedCoins = Mathf.MoveTowards(_displayedCoins, _liveCoins, rate * Time.unscaledDeltaTime);
        }

        private void OnGUI()
        {
            if (_controller == null) return;
            EnsureStyles();

            const int width = 260;
            const int height = 70;
            const int padding = 20;
            const int coinHeight = 30;
            const int coinGap = 6;
            Rect rect = new Rect(Screen.width - width - padding, padding, width, height);

            // semi-transparent background panel so the text is readable on any map.
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prev;

            string waveText = _controller.CurrentWave <= 0
                ? "Preparing..."
                : $"WAVE {_controller.CurrentWave}";
            string remainingText = _controller.CurrentWave <= 0
                ? string.Empty
                : $"Zombies remaining: {_controller.RemainingCount} / {_controller.TargetCount}";
            string phaseText = _controller.CurrentPhase.ToString();

            GUI.Label(new Rect(rect.x + 12, rect.y + 6,  rect.width - 24, 24), waveText, _bigStyle);
            GUI.Label(new Rect(rect.x + 12, rect.y + 32, rect.width - 24, 18), remainingText, _smallStyle);
            GUI.Label(new Rect(rect.x + 12, rect.y + 50, rect.width - 24, 16), phaseText, _smallStyle);

            // coin panel sits directly under the wave panel, same width,
            // shorter. balance is polled fresh each frame - cheap since it
            // iterates the secured container (handful of items at most).
            Rect coinRect = new Rect(rect.x, rect.y + height + coinGap, width, coinHeight);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(coinRect, Texture2D.whiteTexture);
            GUI.color = prev;

            // displayed value comes from Update so the number visibly walks
            // toward the new balance instead of snapping.
            int shownCoins = Mathf.RoundToInt(_displayedCoins);
            string coinText = $"TarCoins: {shownCoins:N0}";
            GUI.Label(new Rect(coinRect.x + 12, coinRect.y + 6, coinRect.width - 24, 20), coinText, _coinStyle);
        }

        private void EnsureStyles()
        {
            if (_bigStyle == null)
            {
                _bigStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(1f, 0.2f, 0.2f) },
                };
            }
            if (_smallStyle == null)
            {
                _smallStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    normal = { textColor = Color.white },
                };
            }
            if (_coinStyle == null)
            {
                _coinStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(1f, 0.85f, 0.2f) },
                };
            }
        }
    }
}
