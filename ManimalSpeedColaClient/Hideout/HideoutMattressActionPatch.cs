using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using JsonType;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // prefix on GetActionsClass.GetAvailableActions: detect HideoutMattress
    // and build a "Sleep" action. the action is shown disabled when the
    // player has any disallowed gear equipped (everything except melee and
    // secure container must be removed first).
    internal sealed class HideoutMattressActionPatch : ModulePatch
    {
        // equipment slots that must be empty before the player can sleep.
        // melee (Scabbard), secure container, pockets and dogtag are
        // allowed to remain.
        private static readonly EquipmentSlot[] GearSlotsThatMustBeEmpty =
        {
            EquipmentSlot.FirstPrimaryWeapon,
            EquipmentSlot.SecondPrimaryWeapon,
            EquipmentSlot.Holster,
            EquipmentSlot.Backpack,
            EquipmentSlot.TacticalVest,
            EquipmentSlot.ArmorVest,
            EquipmentSlot.Eyewear,
            EquipmentSlot.FaceCover,
            EquipmentSlot.Headwear,
            EquipmentSlot.Earpiece,
            EquipmentSlot.ArmBand,
        };

        // PackNStrap belt holder lives in a hidden grid inside Pockets. the
        // belt itself isn't on a vanilla EquipmentSlot, so we walk pockets
        // -> holder grid -> mod_belt slot the same way ZombiesLoadoutPatch
        // does. constants mirrored here to keep this file self-contained.
        private const string PackNStrapHolderTpl = "6815465859b8c6ff13f94100";
        private const string PackNStrapHolderGrid = "packnstrap_belt_holder_grid";
        private const string BeltSlotId = "mod_belt";

        protected override MethodBase GetTargetMethod()
        {
            return typeof(GetActionsClass)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetAvailableActions" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(GamePlayerOwner));
        }

        [PatchPrefix]
        private static bool Prefix(
            GamePlayerOwner owner,
            object interactive,
            ref ActionsReturnClass __result)
        {
            HideoutMattress mattress = interactive as HideoutMattress;
            if (mattress == null) return true;

            try
            {
                __result = BuildActions(owner, mattress);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[HideoutMattress] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, HideoutMattress mattress)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            // use the PROFILE inventory, not the hideout-walk Player's
            // inventory. the walk-mode player is a separate avatar whose
            // equipment slots are always empty; the profile inventory is
            // where the player's actual loadout lives.
            InventoryEquipment equipment = owner.Player.Profile?.Inventory?.Equipment;
            bool gearOk = equipment != null && AllDisallowedSlotsEmpty(equipment);

            string name = gearOk ? "Sleep" : "Sleep (remove gear first)";

            result.Actions.Add(new ActionsTypesClass
            {
                Name = name,
                Disabled = !gearOk,
                Action = () => OnSleep(mattress, owner),
            });
            return result;
        }

        private static bool AllDisallowedSlotsEmpty(InventoryEquipment equipment)
        {
            foreach (EquipmentSlot slotName in GearSlotsThatMustBeEmpty)
            {
                Slot slot = equipment.GetSlot(slotName);
                if (slot?.ContainedItem != null) return false;
            }
            // belt is in the PackNStrap holder, not a vanilla equipment slot.
            return BeltHolderSlotEmpty(equipment);
        }

        // returns true if either the holder isn't present (PackNStrap not
        // installed / not yet injected for this profile) OR the mod_belt
        // slot exists but contains no belt. only returns false if a belt is
        // actually equipped.
        private static bool BeltHolderSlotEmpty(InventoryEquipment equipment)
        {
            Slot beltSlot = FindBeltHolderSlot(equipment);
            return beltSlot?.ContainedItem == null;
        }

        private static Slot FindBeltHolderSlot(InventoryEquipment equipment)
        {
            if (equipment == null) return null;
            Slot pocketsSlot = equipment.GetSlot(EquipmentSlot.Pockets);
            PocketsItemClass pockets = pocketsSlot?.ContainedItem as PocketsItemClass;
            if (pockets?.Grids == null) return null;

            foreach (var grid in pockets.Grids)
            {
                if (grid == null) continue;
                if (grid.ID != PackNStrapHolderGrid) continue;
                foreach (Item item in grid.Items)
                {
                    if (item == null) continue;
                    if (item.StringTemplateId != PackNStrapHolderTpl) continue;
                    CompoundItem holder = item as CompoundItem;
                    if (holder?.Slots == null) return null;
                    return holder.Slots.FirstOrDefault(s => s != null && s.ID == BeltSlotId);
                }
            }
            return null;
        }

        private const string FactoryLocationId = "factory4_day";

        private static void OnSleep(HideoutMattress mattress, GamePlayerOwner owner)
        {
            try
            {
                // profile inventory == actual player loadout. hideout-walk
                // Player.Inventory is a separate avatar with always-empty
                // slots so checking it here would always pass.
                InventoryEquipment equipment = owner.Player.Profile?.Inventory?.Equipment;
                if (equipment == null)
                {
                    Plugin.LogSource?.LogWarning("[HideoutMattress] no profile equipment context; blocking sleep.");
                    return;
                }

                // verbose dump of every gear slot so we can see what's actually
                // in there at click time. helps diagnose when sleep proceeds
                // despite gear being equipped.
                LogGearState(equipment);

                if (!AllDisallowedSlotsEmpty(equipment))
                {
                    Plugin.LogSource?.LogInfo("[HideoutMattress] sleep blocked - gear still equipped.");
                    return;
                }

                // arm the master gate so the Factory practice raid that
                // loads spawns the SpeedCola/Wallbuy/loadout. resets on the
                // next HideoutPlayerOwner.Init.
                Plugin.ZombiesMode = true;

                // tell the server this raid shouldn't count - profile is
                // restored to its pre-raid state on raid end (no xp, no
                // quest progress, no loot, no inventory diff).
                ZombieRaidFlagClient.SignalZombiesRaid();

                // apply the zombies loadout DIRECTLY to Profile.Inventory.Equipment
                // here, before triggering the raid start. TarkovApplication's
                // raid-load pre-bake reads Profile.GetAllPrefabPaths(true) to
                // decide which bundles to retain - if the items aren't in the
                // profile yet (i.e. we waited until OnGameStarted to add them)
                // the 1911 etc. bundles never get loaded and the player NREs
                // the moment they switch hands. SearchController comes off the
                // walk-mode Player but is constructed against the profile so
                // it's the right tracker.
                ZombiesLoadoutPatch.ApplyLoadout(
                    owner.Player.InventoryController,
                    equipment,
                    owner.Player.SearchController);

                Plugin.LogSource?.LogInfo("[HideoutMattress] Sleeping... exiting hideout, then loading Factory practice raid.");
                _ = StartFactoryPracticeRaidAsync();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[HideoutMattress] sleep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void LogGearState(InventoryEquipment equipment)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("[HideoutMattress] equipment at Sleep click: ");
                foreach (EquipmentSlot slotName in GearSlotsThatMustBeEmpty)
                {
                    Slot slot = equipment.GetSlot(slotName);
                    string contents = slot?.ContainedItem == null ? "(empty)" : slot.ContainedItem.TemplateId;
                    sb.Append($"{slotName}={contents} ");
                }
                Slot beltSlot = FindBeltHolderSlot(equipment);
                string beltContents = beltSlot == null
                    ? "(no holder)"
                    : (beltSlot.ContainedItem == null ? "(empty)" : beltSlot.ContainedItem.TemplateId);
                sb.Append($"Belt={beltContents} ");
                Plugin.LogSource?.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HideoutMattress] LogGearState threw: {ex.Message}");
            }
        }

        // attempts a direct programmatic raid start by:
        //   1. grabbing the TarkovApplication singleton
        //   2. pulling the private mainMenuControllerClass field via reflection
        //   3. invoking method_25 to exit hideout walk-mode back to main menu
        //      (without this step method_83 corrupts state because we're still
        //       in hideout walk-mode when the raid loader tries to take over)
        //   4. mutating RaidSettings_0 to Factory + offline + practice
        //   5. invoking the private method_83 (the OnReadyToStartRaid handler)
        // each step is BSG-obfuscated; if any breaks we log and bail without
        // touching state any further so the player can navigate manually.
        private static async Task StartFactoryPracticeRaidAsync()
        {
            try
            {
                TarkovApplication app = Singleton<ClientApplication<ISession>>.Instance as TarkovApplication;
                if (app == null)
                {
                    Plugin.LogSource?.LogError("[HideoutMattress] TarkovApplication not found via Singleton<ClientApplication<ISession>>.Instance");
                    return;
                }

                ISession session = app.Session;
                if (session == null)
                {
                    Plugin.LogSource?.LogError("[HideoutMattress] app.Session is null");
                    return;
                }

                // pull MainMenuControllerClass (private field on TarkovApplication)
                FieldInfo menuField = AccessTools.Field(typeof(TarkovApplication), "mainMenuControllerClass");
                MainMenuControllerClass menu = menuField?.GetValue(app) as MainMenuControllerClass;
                if (menu == null)
                {
                    Plugin.LogSource?.LogError("[HideoutMattress] mainMenuControllerClass not initialized on TarkovApplication");
                    return;
                }

                // step 1: exit hideout walk-mode back to main menu. method_25
                // is the private async Task that runs when the root screen is
                // EEftScreenType.Hideout and the player navigates out - it
                // calls method_60() validation then GClass2301_0.HideHideout().
                // we await the task so the main menu is actually ready before
                // we touch RaidSettings.
                MethodInfo exitMethod = AccessTools.Method(typeof(MainMenuControllerClass), "method_25");
                if (exitMethod == null)
                {
                    Plugin.LogSource?.LogError("[HideoutMattress] method_25 not found on MainMenuControllerClass; cannot exit hideout");
                    return;
                }

                Plugin.LogSource?.LogInfo("[HideoutMattress] invoking method_25 to exit hideout walk-mode");
                Task exitTask = exitMethod.Invoke(menu, null) as Task;
                if (exitTask != null) await exitTask;

                // small settle delay so UI / state transitions finish before we
                // mutate RaidSettings and fire the raid loader.
                await Task.Delay(500);

                // step 2: resolve Factory location from session.LocationSettings.
                LocationSettingsClass.Location factory = ResolveLocation(session, FactoryLocationId);
                if (factory == null)
                {
                    Plugin.LogSource?.LogError($"[HideoutMattress] could not find location '{FactoryLocationId}' in session.LocationSettings");
                    return;
                }

                // step 3: grab RaidSettings_0 (public field, mutate in-place).
                FieldInfo settingsField = AccessTools.Field(typeof(MainMenuControllerClass), "RaidSettings_0");
                RaidSettings settings = settingsField?.GetValue(menu) as RaidSettings;
                if (settings == null)
                {
                    Plugin.LogSource?.LogError("[HideoutMattress] RaidSettings_0 is null on MainMenuControllerClass");
                    return;
                }

                // only override location + side + time. RaidMode/IsPveOffline
                // are left at whatever the profile last used: SPT PvE rejects
                // the "Local + IsPveOffline" practice combo (it's a PvP-profile
                // path) and replies with "servers busy" on raid-start, so we
                // let the existing values ride and just point at Factory.
                settings.SelectedLocation = factory;
                settings.Side = ESideType.Pmc;
                settings.SelectedDateTime = EDateTime.CURR;

                Plugin.LogSource?.LogInfo($"[HideoutMattress] RaidSettings configured: location={factory.Id}, RaidMode={settings.RaidMode}, IsPveOffline={settings.IsPveOffline} (left as profile default)");

                // step 4: fire the raid-start handler. method_83 is the listener
                // bound to MatchMakerAcceptScreen.OnReadyToStartRaid - calling
                // it directly skips the AcceptScreen UI flow.
                MethodInfo startMethod = AccessTools.Method(typeof(MainMenuControllerClass), "method_83");
                if (startMethod == null)
                {
                    Plugin.LogSource?.LogError("[HideoutMattress] method_83 not found on MainMenuControllerClass");
                    return;
                }

                Plugin.LogSource?.LogInfo("[HideoutMattress] invoking method_83 to load the raid");
                startMethod.Invoke(menu, null);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[HideoutMattress] StartFactoryPracticeRaidAsync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static LocationSettingsClass.Location ResolveLocation(ISession session, string locationId)
        {
            try
            {
                LocationSettingsClass locationSettings = session.LocationSettings;
                if (locationSettings?.locations == null) return null;
                foreach (var kv in locationSettings.locations)
                {
                    if (kv.Value != null &&
                        string.Equals(kv.Value.Id, locationId, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HideoutMattress] ResolveLocation threw: {ex.Message}");
            }
            return null;
        }
    }
}
