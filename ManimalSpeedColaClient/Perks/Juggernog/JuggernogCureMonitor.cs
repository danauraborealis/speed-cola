using System;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using Manimal.SpeedCola.Patches;
using UnityEngine;

namespace Manimal.SpeedCola.Components
{
    // periodic cure-over-time tick: while the Juggernog buff is active on
    // the local main player, every CureInterval seconds, scan every body
    // part for a LightBleeding / HeavyBleeding / Fracture effect and
    // remove any found.
    //
    // mirrors the splint (item_meds_alusplint, tpl 5af0454c86f7746bf20992e8)
    // and Perfotoran (tpl 637b6251104668754b72f8f9) cure mechanism. those
    // items declare effects_damage entries (Fracture, LightBleeding,
    // HeavyBleeding, Intoxication, RadExposure) in their item template;
    // EFT processes each entry by:
    //
    //     var effect = HealthController.FindActiveEffect<T>(bodyPart);
    //     if (effect != null) effect.ForceResidue();
    //
    // (see ActiveHealthController.method_16<T> at line 337-346 of the
    // decompile - that's the exact two-line cure recipe wrapped in a
    // protected generic helper. line 4070-4076 shows the splint flow
    // calling it with HeavyBleeding/LightBleeding/Fracture.)
    //
    // we replicate it externally using the public GInterface marker types
    // because the concrete LightBleeding/HeavyBleeding/Fracture classes
    // are PROTECTED nested types of ActiveHealthController:
    //   GInterface339 = LightBleeding
    //   GInterface340 = HeavyBleeding
    //   GInterface342 = Fracture
    //
    // the runtime object returned by FindActiveEffect IS-A
    // ActiveHealthController.GClass3008 (public abstract base for all
    // health effects), so we cast to that to access the public
    // virtual ForceResidue() method.
    //
    // attached as a sibling of JuggernogHpBoostMonitor in
    // SpawnJuggernogOnGameStartedPatch (host parented to GameWorld). lives
    // for the whole raid, dies with the world.
    public class JuggernogCureMonitor : MonoBehaviour
    {
        // seconds between cure passes. 6s strikes a balance - bleeds get a
        // tick or two through before clearing, but the scan cost is minimal
        // and the cure feels deliberate rather than instant. low enough not
        // to noticeably erode HP from a fresh wound.
        public const float CureInterval = 6f;

        // every body part that can hold a bleed or fracture. iterate the
        // whole enum range to keep this resilient if EFT adds new parts.
        // Common is included for completeness even though bleeds/fractures
        // don't land there in vanilla.
        private static readonly EBodyPart[] AllParts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
            EBodyPart.LeftArm, EBodyPart.RightArm,
            EBodyPart.LeftLeg, EBodyPart.RightLeg,
            EBodyPart.Common,
        };

        private float _nextTick;

        private void Update()
        {
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + CureInterval;

            try
            {
                if (!JuggernogBuffState.IsBuffActive()) return;

                Player main = Singleton<GameWorld>.Instance?.MainPlayer;
                ActiveHealthController hc = main?.ActiveHealthController;
                if (hc == null) return;

                int healed = 0;
                for (int i = 0; i < AllParts.Length; i++)
                {
                    EBodyPart part = AllParts[i];
                    healed += TryCureOne<GInterface339>(hc, part);  // LightBleeding
                    healed += TryCureOne<GInterface340>(hc, part);  // HeavyBleeding
                    healed += TryCureOne<GInterface342>(hc, part);  // Fracture
                }

                if (healed > 0)
                    Plugin.LogSource?.LogInfo($"[Juggernog] cure tick: removed {healed} negative effect(s).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Juggernog] cure tick threw: {ex.Message}");
            }
        }

        // T must match one of EFT's effect marker interfaces (GInterface339/
        // 340/342). FindActiveEffect returns the active instance or null;
        // the runtime object is a concrete subclass of GClass3008 so we cast
        // to that to reach ForceResidue.
        // T : IEffect matches FindActiveEffect's declared constraint (the
        // generic method on GClass3009 requires TEffect : IEffect). all three
        // marker interfaces (GInterface339/340/342) chain back to IEffect.
        private static int TryCureOne<T>(ActiveHealthController hc, EBodyPart part) where T : class, IEffect
        {
            T effect = hc.FindActiveEffect<T>(part);
            if (effect is ActiveHealthController.GClass3008 g)
            {
                g.ForceResidue();
                return 1;
            }
            return 0;
        }
    }
}
