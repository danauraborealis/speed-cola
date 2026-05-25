using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // three small patches that, when ZombiesMode is armed, blank out every
    // path the vanilla raid uses to spawn AI on its own. our custom wave
    // controller (ZombiesWaveController) is then the only thing spawning
    // bots, and it only spawns infected*.
    //
    //   1. LocalGame.smethod_7 - transforms `location.waves` into the
    //      runtime WildSpawnWave[] array stored on wavesSpawnScenario_0.
    //      we return Array.Empty<WildSpawnWave>() so timed waves never fire.
    //
    //   2. LocalGame.smethod_8 - same idea for `location.BossLocationSpawn`,
    //      which becomes the boss spawn scenario. cleared so no Tagilla /
    //      Killa / scav bosses spawn during a zombies raid.
    //
    //   3. LocationSettingsClass.Location.EventsDataClass.GetEventsProfilesOnStart
    //      returns null so BotHalloweenWithZombies.GetProfilesOnStart() doesn't
    //      get its infected starting waves merged into the bots-on-start list
    //      (LocalGame.vmethod_1:100). we want our wave 1 to be the player's
    //      first encounter, not a random Halloween dump.

    public class ZombiesSuppressWildWavesPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocalGame), "smethod_7");

        [PatchPrefix]
        private static bool Prefix(ref WildSpawnWave[] __result)
        {
            if (!Plugin.ZombiesMode) return true;
            __result = Array.Empty<WildSpawnWave>();
            Plugin.LogSource?.LogInfo("[ZombiesSuppress] WildSpawnWave[] cleared (zombies mode).");
            return false; // skip original
        }
    }

    public class ZombiesSuppressBossWavesPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocalGame), "smethod_8");

        [PatchPrefix]
        private static bool Prefix(ref BossLocationSpawn[] __result)
        {
            if (!Plugin.ZombiesMode) return true;
            __result = Array.Empty<BossLocationSpawn>();
            Plugin.LogSource?.LogInfo("[ZombiesSuppress] BossLocationSpawn[] cleared (zombies mode).");
            return false;
        }
    }

    public class ZombiesSuppressEventStartWavesPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(LocationSettingsClass.Location.EventsDataClass), nameof(LocationSettingsClass.Location.EventsDataClass.GetEventsProfilesOnStart));

        [PatchPrefix]
        private static bool Prefix(ref IEnumerable<WaveInfoClass> __result)
        {
            if (!Plugin.ZombiesMode) return true;
            __result = null;
            return false;
        }
    }
}
