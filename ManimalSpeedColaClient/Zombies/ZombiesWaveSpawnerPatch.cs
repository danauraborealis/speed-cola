using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.SpeedCola.Patches
{
    // postfix GameWorld.OnGameStarted: when zombies mode is on, create the
    // ZombiesWaveController + ZombiesWaveHUD GameObject and parent it to the
    // GameWorld. the controller runs the spawn loop; the HUD reads state from
    // it. both are auto-destroyed when the GameWorld unloads at raid end.
    public class ZombiesWaveSpawnerPatch : ModulePatch
    {
        private const string HostObjectName = "ZombiesWaveHost";

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            try
            {
                if (!Plugin.ZombiesMode) return;
                if (__instance == null) return;

                GameObject host = new GameObject(HostObjectName);
                if (__instance.transform != null)
                    host.transform.SetParent(__instance.transform, false);

                host.AddComponent<ZombiesWaveController>();
                host.AddComponent<ZombiesWaveHUD>();
                Plugin.LogSource?.LogInfo("[ZombiesWaves] controller + HUD attached.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesWaves] spawner patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
