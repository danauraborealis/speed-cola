using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // postfix GameWorld.OnGameStarted. when zombies mode is armed, lock the
    // player into the raid for the duration: no exfils, no transits, no
    // ticking timer. only way out is death or alt-f4.
    //
    //   1. exfils -> Status = NotPresent on every ExfiltrationPoint /
    //      ScavExfiltrationPoint / SecretExfiltrationPoint. the setter runs
    //      Disable(prevStatus), kills the trigger collider, drops the entry
    //      from the timer panel, broadcasts OnStatusChanged.
    //   2. transits -> TransitControllerAbstractClass.DisableTransitPoints()
    //      walks every TransitPoint via LocationScene and SetActive(false)s
    //      its GameObject. Factory's transits live here so we need this.
    //   3. timer -> Singleton<AbstractGame>.Instance.GameTimer.ChangeSessionTime
    //      pushed to 99 hours. cant null SessionTime (its consumed via
    //      .Value in too many places), but 99h is effectively infinite.
    //
    // none of this leaks across raids - ExfiltrationControllerClass is rebuilt
    // each raid by BaseLocalGame.vmethod_6, transit objects come from the
    // freshly loaded scene, and GameTimer is constructed per-game.
    public class ZombiesNoEscapePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            try
            {
                if (!Plugin.ZombiesMode) return;

                DisableExfils(__instance);
                DisableTransits();
                ExtendRaidTimer();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesNoEscape] postfix failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void DisableExfils(GameWorld gw)
        {
            ExfiltrationControllerClass controller = ExfiltrationControllerClass.Instance;
            if (controller == null)
            {
                Plugin.LogSource?.LogWarning("[ZombiesNoEscape] ExfiltrationControllerClass.Instance is null; cannot disable exfils.");
                return;
            }

            int disabled = 0;
            disabled += DisableArray(controller.ExfiltrationPoints, "PMC");
            disabled += DisableArray(controller.ScavExfiltrationPoints, "Scav");
            disabled += DisableArray(controller.SecretExfiltrationPoints, "Secret");
            Plugin.LogSource?.LogInfo($"[ZombiesNoEscape] disabled {disabled} exfil(s) on '{gw?.MainPlayer?.Location}'.");
        }

        private static int DisableArray(ExfiltrationPoint[] points, string label)
        {
            if (points == null || points.Length == 0) return 0;
            int n = 0;
            foreach (ExfiltrationPoint point in points)
            {
                if (point == null) continue;
                try
                {
                    if (point.Status == EExfiltrationStatus.NotPresent) continue;
                    point.Status = EExfiltrationStatus.NotPresent;
                    n++;
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"[ZombiesNoEscape] failed to disable {label} exfil '{point?.Settings?.Name}': {ex.Message}");
                }
            }
            return n;
        }

        private static void DisableTransits()
        {
            try
            {
                // walks every TransitPoint MonoBehaviour in the scene and
                // SetActive(false)s its GameObject - same path BSG uses to
                // turn transits off for events that lock down a map.
                TransitControllerAbstractClass.DisableTransitPoints();
                Plugin.LogSource?.LogInfo("[ZombiesNoEscape] transit points disabled via DisableTransitPoints().");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesNoEscape] DisableTransitPoints threw: {ex.Message}");
            }
        }

        private static void ExtendRaidTimer()
        {
            try
            {
                AbstractGame game = Singleton<AbstractGame>.Instance;
                GameTimerClass timer = game?.GameTimer;
                if (timer == null)
                {
                    Plugin.LogSource?.LogWarning("[ZombiesNoEscape] GameTimer is null; cannot extend session time.");
                    return;
                }
                // ChangeSessionTime throws if Nullable_2 (raid end time) is
                // already set - that only happens once the raid has ended,
                // so at OnGameStarted it should always be null.
                timer.ChangeSessionTime(TimeSpan.FromHours(99));
                Plugin.LogSource?.LogInfo("[ZombiesNoEscape] session time pushed to 99h (raid won't time out).");

                // the visual countdown lives on the active MainTimerPanel,
                // which cached its deadline (private dateTime_0 on the base
                // TimerPanel) when ExtractionTimersPanel.SetTime ran at raid
                // start - i.e. BEFORE our ChangeSessionTime. without poking
                // it, the player still sees the original countdown ticking
                // down even though the actual game-end check now uses 99h.
                // reflection-set the cached deadline to UtcNow + 99h so the
                // panel renders a fresh long timer.
                RefreshTimerPanelDeadline(EFTDateTimeClass.UtcNow + TimeSpan.FromHours(99));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesNoEscape] ChangeSessionTime threw: {ex.Message}");
            }
        }

        private static void RefreshTimerPanelDeadline(DateTime newDeadline)
        {
            try
            {
                // FindObjectOfType picks up the live MainTimerPanel instance
                // (the main session-timer UI). only one in the scene during
                // a raid. cheaper than walking GameUI.Instance.TimerPanel's
                // private _mainTimerPanel field.
                var panel = UnityEngine.Object.FindObjectOfType<EFT.UI.BattleTimer.MainTimerPanel>();
                if (panel == null)
                {
                    Plugin.LogSource?.LogInfo("[ZombiesNoEscape] no MainTimerPanel in scene; visual timer wont be refreshed (raid still wont end).");
                    return;
                }

                // dateTime_0 is the private cached deadline on TimerPanel
                // (the base class). reflection through the base type so we
                // don't fight name-hiding in MainTimerPanel.
                FieldInfo dateTimeField = AccessTools.Field(typeof(EFT.UI.BattleTimer.TimerPanel), "dateTime_0");
                if (dateTimeField == null)
                {
                    Plugin.LogSource?.LogWarning("[ZombiesNoEscape] TimerPanel.dateTime_0 field not found; cannot refresh visual.");
                    return;
                }
                dateTimeField.SetValue(panel, newDeadline);
                Plugin.LogSource?.LogInfo($"[ZombiesNoEscape] MainTimerPanel deadline pushed to {newDeadline:HH:mm:ss}.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesNoEscape] RefreshTimerPanelDeadline threw: {ex.Message}");
            }
        }
    }
}
