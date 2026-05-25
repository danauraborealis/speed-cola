using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // defensive null-guard around GClass985.SetEnabled, the extension method
    // EFT uses to flip RainCondensator components on/off in bulk on a
    // weapon's child renderers.
    //
    // vanilla flow (decompiled at line 52-58 of GClass985.cs):
    //   public static void SetEnabled(this IEnumerable<RainCondensator> rainCondensators, bool enabled)
    //   {
    //       foreach (RainCondensator rainCondensator in rainCondensators)
    //           rainCondensator.enabled = enabled;
    //   }
    //
    // bug: if any element of `rainCondensators` is null (which can happen
    // when a tracked RainCondensator's GameObject was destroyed but the
    // list reference stayed), the property assignment NREs and aborts the
    // whole loop.
    //
    // observed symptom: trying to eat an MRE / drink milk while holding a
    // weapon (any weapon - the boss-drop AK-103, the starter 1911, etc.)
    // fires the food animation's "switch away from weapon" path, which
    // calls WeaponManagerClass.OnReturnToPool → SetEnabled on its
    // List<RainCondensator> field. one entry is null, NRE cascades, the
    // FirearmController never finishes destroying itself, and the player
    // controller gets stuck mid-transition.
    //
    // workaround: replace SetEnabled with a prefix that does the same
    // iteration but skips null entries. the original method is bypassed
    // (return false) so the buggy foreach never runs.
    //
    // also handles a null collection (the vanilla caller already null-
    // checks at line 190 of WeaponManagerClass.OnReturnToPool but extra
    // safety doesn't hurt - other call sites may not).
    internal sealed class RainCondensatorNullGuardPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GClass985), nameof(GClass985.SetEnabled));

        [PatchPrefix]
        private static bool Prefix(IEnumerable<RainCondensator> rainCondensators, bool enabled)
        {
            if (rainCondensators == null) return false;
            try
            {
                foreach (RainCondensator rc in rainCondensators)
                {
                    if (rc != null) rc.enabled = enabled;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[RainCondensator] SetEnabled prefix threw: {ex.Message}");
            }
            return false; // skip vanilla foreach - we've handled it null-safely
        }
    }
}
