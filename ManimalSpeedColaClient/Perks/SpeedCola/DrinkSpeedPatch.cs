using System.Linq;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // postfix on Player.MedsController.ObservedMedsControllerClass.Start - the
    // operation that fires when the player begins consuming a med/food/drink.
    // if the item being used is our SpeedCola, override the FirearmsAnimator
    // use-time multiplier to the configured drink-speed value so every phase
    // of the drink animation (raise, sip, lower) plays faster.
    //
    // vanilla MedsController.smethod_8 sets the multiplier to (1 + SurgerySpeed)
    // at controller spawn; our postfix runs after Start so it has the last say.
    //
    // method resolution goes through GetDeclaredMethods + name + parameter
    // count so the obfuscated GStruct382<EBodyPart> first-parameter type
    // doesnt need to be referenced (its index shifts across SPT releases).
    internal sealed class DrinkSpeedPatch : ModulePatch
    {
        private static readonly FieldInfo MedsControllerField =
            AccessTools.Field(typeof(Player.MedsController.ObservedMedsControllerClass), "MedsController_0");

        private static readonly FieldInfo FirearmsAnimatorField =
            AccessTools.Field(typeof(Player.MedsController), "firearmsAnimator_0");

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.GetDeclaredMethods(typeof(Player.MedsController.ObservedMedsControllerClass))
                .FirstOrDefault(m =>
                    m.Name == "Start" &&
                    m.GetParameters().Length == 3 &&
                    !m.IsStatic);
        }

        [PatchPostfix]
        private static void Postfix(Player.MedsController.ObservedMedsControllerClass __instance)
        {
            try
            {
                Player.MedsController medsController = MedsControllerField?.GetValue(__instance) as Player.MedsController;
                if (medsController?.Item == null) return;
                if (medsController.Item.TemplateId != Plugin.SpeedColaItemTpl) return;

                FirearmsAnimator animator = FirearmsAnimatorField?.GetValue(medsController) as FirearmsAnimator;
                if (animator == null)
                {
                    Plugin.LogSource?.LogWarning("[SpeedCola] DrinkSpeedPatch: firearmsAnimator_0 was null - cannot scale drink animation.");
                    return;
                }

                float mult = Plugin.DrinkSpeedMultiplier != null ? Plugin.DrinkSpeedMultiplier.Value : 1.4f;
                animator.SetUseTimeMultiplier(mult);
                Plugin.LogSource?.LogInfo($"[SpeedCola] drink animation scaled to {mult}x");
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogError($"[SpeedCola] DrinkSpeedPatch threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
