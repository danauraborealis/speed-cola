using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // suppresses zombie infection entirely while ZombiesMode is on.
    //
    // EFT applies the infection via a single chokepoint:
    //   ActiveHealthController.DoZombieInfection(EBodyPart bodyPart)
    //     -> early-outs if a ZombieInfection effect is already on Common
    //     -> calls DoExternalBuff("BuffsZombieInfection", 0f), which is
    //        what spawns BOTH the ZombieInfection effect AND the bundled
    //        positive +50 HealthRate/+50 EnergyRate/+20 Vitality buffs
    //        defined in globals.json (config.Health.Effects.Stimulator
    //        .Buffs.BuffsZombieInfection).
    //
    // we prefix DoZombieInfection and return false in zombies-mode raids
    // on the local main player. AI bots (who also infect each other in
    // vanilla zombie events) are left alone - they share ApplyDamage/health
    // code paths, so we gate on __instance == MainPlayer.ActiveHealthController.
    //
    // why not just strip the positive components from globals.json:
    // the positive buffs are part of the same stimulator definition as the
    // ZombieInfection BuffType (Duration=0) tag, so they ride together.
    // skipping the whole apply call is one line; mutating server data
    // requires reflecting through SPTarkov.Server.Core's typed Globals
    // model and we burned enough time on that already.
    public sealed class ZombieInfectionSuppressPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DoZombieInfection));

        private static bool _loggedFirst;

        [PatchPrefix]
        private static bool Prefix(ActiveHealthController __instance)
        {
            try
            {
                if (!Plugin.ZombiesMode) return true;

                GameWorld gw = Singleton<GameWorld>.Instance;
                Player main = gw?.MainPlayer;
                if (main == null) return true;
                if (main.ActiveHealthController != __instance) return true;

                if (!_loggedFirst)
                {
                    Plugin.LogSource?.LogInfo("[ZombieInfection] suppressed (zombies mode).");
                    _loggedFirst = true;
                }
                return false; // skip vanilla - no infection effect, no positive buffs
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombieInfection] suppress prefix threw: {ex.Message}");
                return true;
            }
        }
    }
}
