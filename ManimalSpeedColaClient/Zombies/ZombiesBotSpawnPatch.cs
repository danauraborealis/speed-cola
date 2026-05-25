using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // hijacks the existing Halloween-2024 zombies system on raid start so the
    // map spawns infected bots only. flow:
    //
    //   - prefix LocalGame.vmethod_1 (runs right before bot controllers init).
    //   - read the location's Events.Halloween2024 config. Factory's location
    //     base.json already ships it (CrowdAttackSpawnParams, ZombieMultiplier,
    //     etc.) - only the InfectionPercentage field defaults to 0.
    //   - if ZombiesMode is armed:
    //       * InfectionPercentage = 100 -> BotHalloweenWithZombies activates,
    //         GetProfilesOnStart() returns all-infected starting waves, and
    //         NonWavesSpawnScenario.BotMax collapses to zero because
    //         BotMax * (1 - CalcRealInfectionLevel()) = BotMax * 0.
    //       * ACTIVE_HALLOWEEN_ZOMBIES_EVENT = true so the activate path on
    //         BotsEventsController actually fires (SPT's default for this is
    //         false; the field isn't in bots/core.json).
    //   - if ZombiesMode is off: force InfectionPercentage back to 0 so a
    //     mutation from a prior zombies raid doesnt leak into a normal raid.
    //
    // this runs once per raid in the prefix; the modification is observed by
    // BotHalloweenWithZombies through the same GClass1425 reference for the
    // whole raid lifetime.
    public class ZombiesBotSpawnPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LocalGame), "vmethod_1");
        }

        [PatchPrefix]
        private static void Prefix(LocalGame __instance)
        {
            try
            {
                LocationSettingsClass.Location.EventsDataClass events = __instance?.Location_0?.Events;
                LocationSettingsClass.Location.GClass1425 halloween = events?.Halloween2024;
                if (halloween == null)
                {
                    if (Plugin.ZombiesMode)
                        Plugin.LogSource?.LogWarning($"[ZombiesBotSpawn] no Halloween2024 config on '{__instance?.Location_0?.Id}'; zombies mode requested but the location lacks the crowd-attack config so the system cant activate.");
                    return;
                }

                if (Plugin.ZombiesMode)
                {
                    // InfectionPercentage=100 collapses NonWavesSpawnScenario.BotMax
                    // to zero (BotMax * (1 - 1)) so the random scav fill stops.
                    // ACTIVE_HALLOWEEN_ZOMBIES_EVENT=true lets the BotHalloweenWithZombies
                    // system construct + activate (the IsInfected behaviors we
                    // want for the zombie AI - swarm, slow look, etc - check
                    // BotHalloweenWithZombies != null).
                    // CrowdsLimit + MaxCrowdAttackSpawnLimit zeroed out so the
                    // Halloween crowd-attack spawner doesn't bring its own bots
                    // alongside our wave controller. our ZombiesWaveController
                    // is the only thing allowed to spawn AI in zombies raids.
                    halloween.InfectionPercentage = 100;
                    halloween.CrowdsLimit = 0;
                    halloween.MaxCrowdAttackSpawnLimit = 0;
                    LocalBotSettingsProviderClass.Core.ACTIVE_HALLOWEEN_ZOMBIES_EVENT = true;
                    Plugin.LogSource?.LogInfo($"[ZombiesBotSpawn] '{__instance.Location_0.Id}': InfectionPercentage=100, CrowdsLimit/MaxCrowdAttackSpawnLimit=0, event=on.");
                }
                else
                {
                    halloween.InfectionPercentage = 0;
                    Plugin.LogSource?.LogInfo($"[ZombiesBotSpawn] '{__instance.Location_0.Id}': normal raid, InfectionPercentage reset to 0.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesBotSpawn] prefix failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
