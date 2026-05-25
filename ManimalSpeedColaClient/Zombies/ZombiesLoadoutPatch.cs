using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // two responsibilities, both gated by Plugin.ZombiesMode:
    //
    //   1. ApplyLoadout(...) — public entry point called from OnSleep BEFORE
    //      the raid starts (i.e. before MainMenuControllerClass.method_83).
    //      writes the zombies loadout directly into Profile.Inventory.Equipment
    //      so TarkovApplication.method_X pre-bakes all the item bundles (the
    //      pre-bake reads Profile.GetAllPrefabPaths(true)). without this we
    //      hit NREs the moment the player tries to switch to the 1911 because
    //      its container bundle was never loaded.
    //
    //   2. OnGameStarted postfix — once the raid is up, auto-switches hands
    //      to whatever sits in Holster (the 1911) so the player doesn't spawn
    //      holding their knife.
    public class ZombiesLoadoutPatch : ModulePatch
    {
        // zombies1911 preset (from db/Presets/GlobalPresets/zombies1911.json
        // in WTT-PresetConverterPlusServer). hand-built tree because the
        // server-registered preset uses fixed IDs that would collide if we
        // ever instantiate more than one copy.
        private const string Zombies1911RootTpl = "5e81c3cbac2bb513793cdc75"; // M1911A1
        private const string WartechBeltTpl = "69364a368a4b47eeaf9ba4a3";
        private const string PackNStrapHolderTpl = "6815465859b8c6ff13f94100";
        private const string PackNStrapHolderGrid = "packnstrap_belt_holder_grid";
        private const string BeltSlotId = "mod_belt";

        // .45 ACP RIP - used for chamber round, in-weapon mag fill, and belt
        // mag fill. one ammo type across the whole zombies loadout.
        private const string RipAmmoTpl = "5ea2a8e200685063ec28c05a";

        // M1911A1 7-round mag (same tpl as the in-weapon mag). 6 of these
        // get stuffed into the wartech belt grids. previously used the 999-cap
        // mag tpl 671d8b38b769f0d88c0950f8 which loaded with 6x999 RIP rounds
        // and pushed the player to ~150kg.
        private const string ExtraMagTpl = "5e81c4ca763d9f754677befa";
        private const int ExtraMagCount = 6;

        // Zhuk-3 Standard armor preset (vanilla globals.json preset id
        // 65765a38526e320fbe035795). hand-built tree so the IDs are fresh.
        private const string ZhukArmorRootTpl = "5c0e5edb86f77461f55ed1f7";

        // LShZ-2DTM helmet + RAC headset (slots into mod_equipment_001) and
        // the two built-in soft armor inserts. without the inserts the helmet
        // ships with empty armor slots and renders red in the UI.
        private const string LshzHelmetTpl = "5b432d215acfc4771e1c6624";
        private const string LshzTopArmorTpl = "657bb92fa1c61ee0c303631f";
        private const string LshzBackArmorTpl = "657bb99db30eca976305117f";
        private const string RacHeadsetTpl = "5a16b9fffcdbcb0176308b34";

        // arena balaclava (face cover slot).
        private const string BalaclavaTpl = "67a9cd6ecade15e0f00123b8";

        // Alpha tactical chest rig (WARTECH TV-110, vanilla tpl). roomy
        // 4x mag grid + a small kit grid - decent default for the 1911
        // reload loadout.
        private const string ChestRigTpl = "592c2d1a86f7746dbe2af32a";

        // each tuple: (presetId, tpl, parentPresetId | null, slotId | null).
        // mirrors the _items order from zombies1911.json; presetId is the
        // *source* id from the JSON, used here only for child->parent lookup
        // during the build (actual runtime IDs are minted by InventoryController).
        private static readonly PresetEntry[] Zombies1911Tree =
        {
            new PresetEntry("root",       Zombies1911RootTpl,         null,        null),
            new PresetEntry("barrel",     "5e81c519cb2b95385c177551", "root",      "mod_barrel"),
            new PresetEntry("grip",       "5e81c6bf763d9f754677beff", "root",      "mod_pistol_grip"),
            new PresetEntry("receiver",   "5e81edc13397a21db957f6a1", "root",      "mod_reciever"),
            new PresetEntry("magazine",   "5e81c4ca763d9f754677befa", "root",      "mod_magazine"), // empty here; filled below in EquipWeapon
            new PresetEntry("trigger",    "5ef32e4d1c1fd62aea6a150d", "root",      "mod_trigger"),
            new PresetEntry("hammer",     "5ef35bc243cb350a955a7ccd", "root",      "mod_hammer"),
            new PresetEntry("catch",      "5ef3553c43cb350a955a7ccb", "root",      "mod_catch"),
            new PresetEntry("mount001",   "5ef369b08cef260c0642acaf", "root",      "mod_mount_001"),
            new PresetEntry("chamber",    RipAmmoTpl,                  "root",      "patron_in_weapon"),
            new PresetEntry("rearSight",  "5e81ee4dcb2b95385c177582", "receiver",  "mod_sight_rear"),
            new PresetEntry("frontSight", "5e81ee213397a21db957f6a6", "receiver",  "mod_sight_front"),
            new PresetEntry("muzzle",     "5ef61964ec7f42238c31e0c1", "receiver",  "mod_muzzle"),
            // NOTE: "cartridges" in mag is a StackSlot, not a regular Slot -
            // ChangeContainedItemDirectly wont work. mag spawns empty; player
            // can load it manually. omitted to keep this loadout patch simple.
            new PresetEntry("tactical",   "5cc9c20cd7f00c001336c65d", "mount001",  "mod_tactical"),
        };

        // Zhuk-3 Standard preset items from globals.json. soft armor inserts +
        // front/back plates.
        private static readonly PresetEntry[] ZhukArmorTree =
        {
            new PresetEntry("root",       ZhukArmorRootTpl,           null,   null),
            new PresetEntry("softFront",  "6571dbd388ead79fcf091d71", "root", "Soft_armor_front"),
            new PresetEntry("softBack",   "6571dbda88ead79fcf091d75", "root", "Soft_armor_back"),
            new PresetEntry("softLeft",   "6571dbe07c02ae206002502e", "root", "Soft_armor_left"),
            new PresetEntry("softRight",  "6571dbeaee8ec43d520cf89e", "root", "soft_armor_right"),
            new PresetEntry("collar",     "6571dbef88ead79fcf091d79", "root", "Collar"),
            new PresetEntry("frontPlate", "656f57dc27aed95beb08f628", "root", "Front_plate"),
            new PresetEntry("backPlate",  "656fac30c6baea13cd07e10c", "root", "Back_plate"),
        };

        // LShZ-2DTM helmet vanilla preset (globals.json preset id
        // 657bc772aab96fccee08bebc): two soft armor inserts. headset attaches
        // at mod_equipment_001 (added below in EquipHelmet, separate from the
        // armor tree so it stays optional if RAC isnt available).
        private static readonly PresetEntry[] LshzHelmetTree =
        {
            new PresetEntry("root",      LshzHelmetTpl,      null,   null),
            new PresetEntry("helmetTop", LshzTopArmorTpl,    "root", "Helmet_top"),
            new PresetEntry("helmetBack",LshzBackArmorTpl,   "root", "Helmet_back"),
        };

        private readonly struct PresetEntry
        {
            public readonly string PresetId;
            public readonly string Tpl;
            public readonly string ParentPresetId;
            public readonly string SlotId;
            public PresetEntry(string id, string tpl, string parentId, string slotId)
            {
                PresetId = id; Tpl = tpl; ParentPresetId = parentId; SlotId = slotId;
            }
        }

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));

        // postfix's only job now: auto-switch hands to the Holster weapon so
        // the player doesnt spawn holding their knife. loadout itself is
        // applied pre-raid by ApplyLoadout(...) called from OnSleep.
        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            try
            {
                if (!Plugin.ZombiesMode) return;
                Player player = __instance?.MainPlayer;
                if (player == null) return;

                Slot holster = player.Profile?.Inventory?.Equipment?.GetSlot(EquipmentSlot.Holster);
                Weapon weapon = holster?.ContainedItem as Weapon;
                if (weapon == null)
                {
                    Plugin.LogSource?.LogWarning("[ZombiesLoadout] no Holster weapon found at raid start; skipping auto-switch.");
                    return;
                }

                player.Proceed(weapon, new Callback<IFirearmHandsController>(result =>
                {
                    if (result.Failed)
                        Plugin.LogSource?.LogWarning($"[ZombiesLoadout] auto-switch to holster weapon failed: {result.Error}");
                }), scheduled: true);
                Plugin.LogSource?.LogInfo("[ZombiesLoadout] requested auto-switch to holster weapon.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesLoadout] postfix failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // public entry point used by HideoutMattressActionPatch.OnSleep. writes
        // the loadout into `equipment` (which must be Profile.Inventory.Equipment
        // so TarkovApplication's pre-raid bundle pre-bake picks it up). `controller`
        // is used only as an IIdGenerator + factory ID source - it doesnt need to
        // be the inventory controller that owns `equipment`.
        public static void ApplyLoadout(InventoryController controller, InventoryEquipment equipment, IPlayerSearchController searchController)
        {
            try
            {
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                if (controller == null || equipment == null || factory == null)
                {
                    Plugin.LogSource?.LogWarning("[ZombiesLoadout] ApplyLoadout: controller/equipment/factory missing; aborting.");
                    return;
                }

                // chest rig first so any subsequent inventory placement has
                // a populated TacticalVest grid to land in.
                Item rig = EquipChestRig(controller, equipment, searchController, factory);
                EquipWeapon(controller, equipment, factory);
                EquipBelt(controller, equipment, searchController, factory);
                EquipArmor(controller, equipment, searchController, factory);
                EquipHelmet(controller, equipment, searchController, factory);
                EquipBalaclava(controller, equipment, searchController, factory);

                // place spare mags across equipped containers (rig first, then
                // belt, backpack, pockets) so the player has reloads no matter
                // which gear they end up wearing.
                PlaceLoadedMagsAcrossEquipment(equipment, ExtraMagTpl, RipAmmoTpl, ExtraMagCount, controller, factory);

                // starter Chattabka VOG-25 grenades. seated specifically in
                // the PackNStrap belt so the player has a quick-throw option
                // off the bat without burning rig / pocket space.
                SeedBeltItems(equipment, ChattabkaGrenadeTpl, ChattabkaGrenadeCount, controller, factory, "Chattabka VOG-25");

                // starter TarCoin stack so the player can buy a wallbuy or
                // unlock a supply drop without grinding the first wave.
                SeedStartingTarCoins(equipment, controller, factory);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesLoadout] ApplyLoadout failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // starter TarCoin allowance dropped into the secured container at
        // raid start. lets the player afford a wallbuy or one supply-drop
        // unlock without needing to clear wave 1 first. tpl matches the
        // shared TarCoinTpl in TarCoinScorePatch.
        public const string TarCoinTpl = "6a0b45f6682ea02b4ca45907";
        public const int StartingTarCoinCount = 500;

        // Chattabka VOG-25 (weapon_grenade_chattabka_vog25). impact-fuze
        // hand grenade. seated in the PackNStrap belt at raid start so
        // the player has a few thrown-explosive options without giving
        // up belt mag slots they need for reloads.
        public const string ChattabkaGrenadeTpl = "5e340dcdcb6d5863cc5e5efb";
        public const int ChattabkaGrenadeCount = 2;

        // creates `count` items of (tpl) and seats each one into the
        // PackNStrap belt's grids via AddAnywhere. logs a warning + skips
        // gracefully if the belt isn't found (PackNStrap not installed
        // or holder injection failed). intentionally belt-only - the
        // user wanted these specifically in the belt, not the cascade.
        private static void SeedBeltItems(InventoryEquipment equipment, string tpl, int count, InventoryController controller, ItemFactoryClass factory, string label)
        {
            try
            {
                Slot beltSlot = FindBeltHolderSlot(equipment);
                CompoundItem belt = beltSlot?.ContainedItem as CompoundItem;
                if (belt?.Grids == null || belt.Grids.Length == 0)
                {
                    Plugin.LogSource?.LogWarning($"[ZombiesLoadout] no belt available; skipping {count}x {label} seed.");
                    return;
                }

                int placed = 0;
                for (int i = 0; i < count; i++)
                {
                    Item item = factory.CreateItem(((IIdGenerator)controller).NextId, tpl, null);
                    if (item == null)
                    {
                        Plugin.LogSource?.LogWarning($"[ZombiesLoadout] factory.CreateItem({tpl}) returned null for {label}.");
                        continue;
                    }
                    bool seated = false;
                    foreach (var grid in belt.Grids)
                    {
                        if (grid == null) continue;
                        var add = grid.AddAnywhere(item, EErrorHandlingType.Ignore);
                        if (add.Succeeded) { seated = true; break; }
                    }
                    if (!seated)
                    {
                        Plugin.LogSource?.LogWarning($"[ZombiesLoadout] belt full after {placed}x {label}; dropping the rest.");
                        break;
                    }
                    MarkSearched(item);
                    placed++;
                }
                Plugin.LogSource?.LogInfo($"[ZombiesLoadout] seeded {placed}/{count} {label} into belt.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesLoadout] SeedBeltItems({label}) threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SeedStartingTarCoins(InventoryEquipment equipment, InventoryController controller, ItemFactoryClass factory)
        {
            try
            {
                CompoundItem secured = equipment.GetSlot(EquipmentSlot.SecuredContainer)?.ContainedItem as CompoundItem;
                if (secured?.Grids == null || secured.Grids.Length == 0)
                {
                    Plugin.LogSource?.LogWarning("[ZombiesLoadout] no secured container or grids; skipping starting TarCoins.");
                    return;
                }
                Item coins = factory.CreateItem(((IIdGenerator)controller).NextId, TarCoinTpl, null);
                if (coins == null)
                {
                    Plugin.LogSource?.LogWarning($"[ZombiesLoadout] CreateItem({TarCoinTpl}) returned null - is the TarCoin item registered server-side?");
                    return;
                }
                coins.StackObjectsCount = StartingTarCoinCount;

                // walk the grids and try AddAnywhere on each until one
                // accepts the stack. secured container usually has one grid
                // but defensively iterate.
                bool placed = false;
                foreach (var grid in secured.Grids)
                {
                    if (grid == null) continue;
                    var add = grid.AddAnywhere(coins, EErrorHandlingType.Ignore);
                    if (add.Succeeded) { placed = true; break; }
                }
                if (!placed)
                {
                    Plugin.LogSource?.LogWarning("[ZombiesLoadout] could not seat starting TarCoins in secured container.");
                    return;
                }
                Plugin.LogSource?.LogInfo($"[ZombiesLoadout] seeded {StartingTarCoinCount} starting TarCoins into secured container.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[ZombiesLoadout] SeedStartingTarCoins threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Max Ammo power-up entry point: count existing spare 1911 mags in the
        // player's inventory (excluding the one seated in mod_magazine on the
        // 1911 itself), and dispense additional loaded mags up to ExtraMagCount.
        // mirrors what ApplyLoadout did on spawn but only for the 1911 mags -
        // wallbuy weapons have their own restock systems.
        //
        // we don't fall through to wallbuy weapons because (a) those already
        // hand the player fresh mags / loose ammo on buy and (b) we don't know
        // their target counts without duplicating per-wallbuy state.
        public static void TopUp1911SpareMags(InventoryController controller, InventoryEquipment equipment, ItemFactoryClass factory)
        {
            try
            {
                if (controller == null || equipment == null || factory == null) return;

                int existing = 0;
                foreach (Item it in equipment.GetAllItemsFromCollection())
                {
                    if (it == null || it.TemplateId != ExtraMagTpl) continue;
                    // skip the mag currently seated in the 1911 - its parent
                    // container is a Slot with ID "mod_magazine". count only
                    // the spares stashed in rig/belt/pockets/backpack.
                    ItemAddress addr = it.Parent;
                    if (addr?.Container is Slot slot && slot.ID == "mod_magazine") continue;
                    existing++;
                }

                int deficit = ExtraMagCount - existing;
                if (deficit <= 0)
                {
                    Plugin.LogSource?.LogInfo($"[MaxAmmo] 1911 spare mags already at {existing}/{ExtraMagCount}; nothing to top up.");
                    return;
                }
                Plugin.LogSource?.LogInfo($"[MaxAmmo] topping up 1911 spare mags: existing={existing}, dispensing={deficit}.");
                PlaceLoadedMagsAcrossEquipment(equipment, ExtraMagTpl, RipAmmoTpl, deficit, controller, factory);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] TopUp1911SpareMags threw: {ex.Message}");
            }
        }

        private static void EquipWeapon(InventoryController controller, InventoryEquipment equipment, ItemFactoryClass factory)
        {
            Item weapon = BuildPresetTree(controller, factory, Zombies1911Tree);
            if (weapon == null)
            {
                Plugin.LogSource?.LogWarning("[ZombiesLoadout] could not build zombies1911 weapon.");
                return;
            }
            // fill the in-weapon mag with RIP. tree-build attached an empty
            // mag; we load it before equipping so the player has more than
            // one shot before reloading.
            LoadMagazineInSlot(weapon, "mod_magazine", RipAmmoTpl, controller, factory);
            EquipInSlot(equipment, EquipmentSlot.Holster, weapon, "zombies1911");
        }

        private static void EquipBelt(InventoryController controller, InventoryEquipment equipment, IPlayerSearchController searchController, ItemFactoryClass factory)
        {
            Slot beltSlot = FindBeltHolderSlot(equipment);
            if (beltSlot == null)
            {
                Plugin.LogSource?.LogWarning("[ZombiesLoadout] PackNStrap belt holder slot not found - is PackNStrap installed and the holder injected?");
                return;
            }
            if (beltSlot.ContainedItem != null)
            {
                Plugin.LogSource?.LogInfo("[ZombiesLoadout] belt slot occupied; not overwriting.");
                return;
            }
            Item belt = factory.CreateItem(((IIdGenerator)controller).NextId, WartechBeltTpl, null);
            if (belt == null)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] factory.CreateItem({WartechBeltTpl}) returned null.");
                return;
            }
            beltSlot.ChangeContainedItemDirectly(belt);
            MarkSearched(belt, searchController);
            Plugin.LogSource?.LogInfo("[ZombiesLoadout] wartech belt equipped in BeltHolder.mod_belt.");

            // belt no longer carries the spare 1911 mags - they go in the
            // chest rig now (stuffed at the end of ApplyLoadout). belt is
            // still equipped because PackNStrap mounts it visually.
        }

        private static void EquipArmor(InventoryController controller, InventoryEquipment equipment, IPlayerSearchController searchController, ItemFactoryClass factory)
        {
            Item armor = BuildPresetTree(controller, factory, ZhukArmorTree);
            if (armor == null)
            {
                Plugin.LogSource?.LogWarning("[ZombiesLoadout] could not build Zhuk-3 armor preset.");
                return;
            }
            EquipInSlot(equipment, EquipmentSlot.ArmorVest, armor, "Zhuk-3 armor");
            MarkSearched(armor, searchController);
        }

        private static void EquipHelmet(InventoryController controller, InventoryEquipment equipment, IPlayerSearchController searchController, ItemFactoryClass factory)
        {
            Item helmet = BuildPresetTree(controller, factory, LshzHelmetTree);
            if (helmet == null)
            {
                Plugin.LogSource?.LogWarning("[ZombiesLoadout] could not build LShZ-2DTM helmet preset.");
                return;
            }
            // headset added separately - the helmet tree contains only the
            // soft-armor inserts so the helmet renders correctly even if RAC
            // attachment fails for any reason.
            AttachMod(helmet, "mod_equipment_001", RacHeadsetTpl, controller, factory);
            EquipInSlot(equipment, EquipmentSlot.Headwear, helmet, "LShZ-2DTM helmet + RAC headset");
            MarkSearched(helmet, searchController);
        }

        private static void EquipBalaclava(InventoryController controller, InventoryEquipment equipment, IPlayerSearchController searchController, ItemFactoryClass factory)
        {
            Item balaclava = factory.CreateItem(((IIdGenerator)controller).NextId, BalaclavaTpl, null);
            if (balaclava == null)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] factory.CreateItem({BalaclavaTpl}) returned null.");
                return;
            }
            EquipInSlot(equipment, EquipmentSlot.FaceCover, balaclava, "arena balaclava");
            MarkSearched(balaclava, searchController);
        }

        private static Item EquipChestRig(InventoryController controller, InventoryEquipment equipment, IPlayerSearchController searchController, ItemFactoryClass factory)
        {
            Item rig = factory.CreateItem(((IIdGenerator)controller).NextId, ChestRigTpl, null);
            if (rig == null)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] factory.CreateItem({ChestRigTpl}) returned null.");
                return null;
            }
            EquipInSlot(equipment, EquipmentSlot.TacticalVest, rig, "wartech chest rig");
            MarkSearched(rig, searchController);
            return rig;
        }

        // belt-and-suspenders search-state clearing.
        //
        // critical detail: magazines/ammo are GearModItemClass (or AmmoItemClass)
        // - they are NOT SearchableItemItemClass. only containers (rigs,
        // backpacks, pockets, the secured container itself) are searchable.
        // an earlier version of this method only marked the item itself,
        // which silently no-op'd for mags - the rig they landed in still
        // showed the "Search" overlay until the player re-searched it.
        //
        // fix: walk UP the parent chain from the item. mark every
        // SearchableItemItemClass ancestor as searched. that covers the
        // case where a mag lands inside a rig - the rig (the actual
        // searchable container) gets cleared, mag is visible.
        //
        // for items that ARE searchable themselves (e.g. a fresh rig
        // dispensed at loadout time) the first loop iteration handles it.
        internal static void MarkSearched(Item item, IPlayerSearchController searchController)
        {
            if (item == null) return;

            Item cursor = item;
            int safety = 8; // depth bound in case of pathological parent loops
            while (cursor != null && safety-- > 0)
            {
                // SetItemAsKnown marks the ITEM as examined. without this,
                // newly-dispensed items show the "unknown" overlay and the
                // container they live in shows the "search me" prompt, even
                // when SetItemAsSearched fires on the container. raiseEvents
                // = true so the inventory HUD updates immediately.
                try { searchController?.SetItemAsKnown(cursor, true); }
                catch (Exception ex) { Plugin.LogSource?.LogWarning($"[ZombiesLoadout] SetItemAsKnown threw for {cursor.TemplateId}: {ex.Message}"); }

                if (cursor is SearchableItemItemClass sc)
                {
                    try { sc.SetItemInfo(null); }
                    catch (Exception ex) { Plugin.LogSource?.LogWarning($"[ZombiesLoadout] SetItemInfo threw for {cursor.TemplateId}: {ex.Message}"); }

                    try { searchController?.SetItemAsSearched(sc); }
                    catch (Exception ex) { Plugin.LogSource?.LogWarning($"[ZombiesLoadout] SetItemAsSearched threw for {cursor.TemplateId}: {ex.Message}"); }
                }
                // step up via ItemAddress.Container.ParentItem. nulls anywhere
                // in the chain terminate the walk cleanly. fully-qualify
                // EFT.InventoryLogic.IContainer because there's another
                // IContainer (Unity/system) shadowing the unqualified name.
                ItemAddress addr = cursor.Parent;
                EFT.InventoryLogic.IContainer container = addr?.Container;
                cursor = container?.ParentItem;
            }
        }

        // convenience overload: pulls the search controller off the local
        // main player. mid-raid callers (Max Ammo, wallbuy restocks) don't
        // have one in scope, so this saves them passing it through.
        internal static void MarkSearched(Item item)
        {
            IPlayerSearchController sc = null;
            try { sc = Singleton<GameWorld>.Instance?.MainPlayer?.SearchController; } catch { }
            MarkSearched(item, sc);
        }

        // shared "create + snap into named slot" used for helmet attachments.
        // skips silently if the slot or item factory fails - we log + move on
        // so one broken attachment doesnt prevent the rest of the loadout.
        private static void AttachMod(Item parent, string slotId, string modTpl, InventoryController controller, ItemFactoryClass factory)
        {
            CompoundItem compound = parent as CompoundItem;
            Slot slot = compound?.Slots?.FirstOrDefault(s => s != null && s.ID == slotId);
            if (slot == null)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] slot '{slotId}' missing on {parent?.TemplateId}; skipping attachment {modTpl}.");
                return;
            }
            Item mod = factory.CreateItem(((IIdGenerator)controller).NextId, modTpl, null);
            if (mod == null)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] factory.CreateItem({modTpl}) returned null.");
                return;
            }
            slot.ChangeContainedItemDirectly(mod);
        }

        // shared equip-to-equipment-slot helper. uses ChangeContainedItemDirectly
        // (skips filter checks) so any valid item for a slot will land regardless
        // of vanilla filter restrictions.
        private static void EquipInSlot(InventoryEquipment equipment, EquipmentSlot slotName, Item item, string description)
        {
            Slot slot = equipment.GetSlot(slotName);
            if (slot == null)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] {slotName} slot not found.");
                return;
            }
            if (slot.ContainedItem != null)
            {
                Plugin.LogSource?.LogInfo($"[ZombiesLoadout] {slotName} occupied; not overwriting {description}.");
                return;
            }
            slot.ChangeContainedItemDirectly(item);
            Plugin.LogSource?.LogInfo($"[ZombiesLoadout] {description} equipped in {slotName}.");
        }

        // creates `count` mags of (magTpl), each loaded to max capacity with
        // (ammoTpl), and tries to fit each into the player's equipped
        // containers in priority order: rig -> pockets -> belt -> backpack.
        // each container's grids are tried via AddAnywhere; first successful
        // fit wins. if every container is full the loop stops and logs how
        // many landed - we don't drop on ground or throw away the mag, just
        // stop trying.
        //
        // returns the list of mags successfully placed so callers can track
        // them (the wallbuy patches keep a per-slot list to discard when the
        // weapon is replaced). callers that don't care just ignore the
        // return value.
        //
        // CRITICAL: this is the SHARED dispense path for both the pre-raid
        // loadout (1911 spare mags) AND the mid-raid wallbuys (BAR / STG
        // spares + Max Ammo top-ups). the wallbuys USED to call
        // InteractionsHandlerClass.QuickFindAppropriatePlace against
        // InventoryEquipment.TraderServicesEligibleSlots, which is
        // [Backpack, TacticalVest, Pockets, SecuredContainer] - that array
        // does NOT include the PackNStrap belt (which lives inside Pockets
        // via a hidden grid, see FindBeltHolderSlot below), so wallbuy mags
        // would silently fail to find a home when the rig was full but the
        // belt had open mag slots. the cascade here explicitly walks the
        // belt as a peer of the rig/pockets/backpack so the belt picks up
        // the overflow.
        internal static List<Item> PlaceLoadedMagsAcrossEquipment(InventoryEquipment equipment, string magTpl, string ammoTpl, int count, InventoryController controller, ItemFactoryClass factory)
        {
            List<Item> placed = new List<Item>();
            var placedPerLabel = new Dictionary<string, int>();
            for (int i = 0; i < count; i++)
            {
                Item mag = factory.CreateItem(((IIdGenerator)controller).NextId, magTpl, null);
                if (mag == null)
                {
                    Plugin.LogSource?.LogWarning($"[ZombiesLoadout] factory.CreateItem({magTpl}) returned null.");
                    break;
                }
                LoadAmmoIntoMagazine(mag, ammoTpl, controller, factory);

                string landedIn = TryPlaceAcrossEquipmentInternal(equipment, mag);
                if (landedIn == null)
                {
                    Plugin.LogSource?.LogWarning($"[ZombiesLoadout] all containers full after {placed.Count} mag(s); stopping.");
                    break;
                }
                placed.Add(mag);
                placedPerLabel.TryGetValue(landedIn, out int n);
                placedPerLabel[landedIn] = n + 1;
            }

            string breakdown = string.Join(", ", placedPerLabel.Select(kv => $"{kv.Key}={kv.Value}"));
            Plugin.LogSource?.LogInfo($"[ZombiesLoadout] placed {placed.Count}/{count} mag(s) across equipment ({breakdown}).");
            return placed;
        }

        // generic per-item variant of PlaceLoadedMagsAcrossEquipment for
        // wallbuys that dispense things that aren't loaded mags (e.g.
        // MP-43's loose buckshot stacks). caller builds the item; we run
        // the same rig -> pockets -> belt -> backpack cascade + MarkSearched.
        // returns true if placed, false if every container was full.
        internal static bool TryPlaceItemAcrossEquipment(InventoryEquipment equipment, Item item)
        {
            if (item == null || equipment == null) return false;
            return TryPlaceAcrossEquipmentInternal(equipment, item) != null;
        }

        // shared cascade walker. returns the label of the container the
        // item landed in (for diagnostic logging), or null if no container
        // had room. MarkSearched fires on success so the dispensed item
        // isn't hidden behind the unsearched overlay when it lands in the
        // rig / belt / backpack.
        private static string TryPlaceAcrossEquipmentInternal(InventoryEquipment equipment, Item item)
        {
            List<(string label, CompoundItem container)> targets = CollectMagTargets(equipment);
            if (targets.Count == 0) return null;

            foreach (var (label, container) in targets)
            {
                if (TryAddToContainerGrids(container, item))
                {
                    MarkSearched(item);
                    return label;
                }
            }
            return null;
        }

        // tries each grid on the container; returns true on first successful fit.
        private static bool TryAddToContainerGrids(CompoundItem container, Item item)
        {
            if (container?.Grids == null) return false;
            foreach (var grid in container.Grids)
            {
                if (grid == null) continue;
                var add = grid.AddAnywhere(item, EErrorHandlingType.Ignore);
                if (add.Succeeded) return true;
            }
            return false;
        }

        // ordered list of (label, container) the cascade walks. priority:
        // rig -> pockets -> belt -> backpack. each is conditional on actually
        // being equipped. (in practice this rarely matters since the wallbuy
        // replacement system ensures the player only holds ammo for weapons
        // they currently own, so any landing slot is fine.)
        private static List<(string label, CompoundItem container)> CollectMagTargets(InventoryEquipment equipment)
        {
            var list = new List<(string, CompoundItem)>();
            void TryAdd(string label, CompoundItem c) { if (c != null) list.Add((label, c)); }

            TryAdd("rig",      equipment.GetSlot(EquipmentSlot.TacticalVest)?.ContainedItem as CompoundItem);
            TryAdd("pockets",  equipment.GetSlot(EquipmentSlot.Pockets)?.ContainedItem as CompoundItem);
            TryAdd("belt",     FindBeltHolderSlot(equipment)?.ContainedItem as CompoundItem);
            TryAdd("backpack", equipment.GetSlot(EquipmentSlot.Backpack)?.ContainedItem as CompoundItem);
            return list;
        }

        // resolves the named slot on `parent` and stuffs whatever magazine sits
        // in it with (ammoTpl) up to capacity. used for the in-weapon mag.
        private static void LoadMagazineInSlot(Item parent, string slotId, string ammoTpl, InventoryController controller, ItemFactoryClass factory)
        {
            CompoundItem compound = parent as CompoundItem;
            Slot slot = compound?.Slots?.FirstOrDefault(s => s != null && s.ID == slotId);
            if (slot?.ContainedItem == null) return;
            LoadAmmoIntoMagazine(slot.ContainedItem, ammoTpl, controller, factory);
        }

        // fills a MagazineItemClass's Cartridges StackSlot with `ammoTpl` up
        // to the magazine's max capacity. expects the mag to be empty.
        private static void LoadAmmoIntoMagazine(Item magItem, string ammoTpl, InventoryController controller, ItemFactoryClass factory)
        {
            MagazineItemClass mag = magItem as MagazineItemClass;
            if (mag?.Cartridges == null)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] {magItem?.TemplateId} is not a magazine; skipping ammo load.");
                return;
            }

            int capacity = mag.Cartridges.MaxCount;
            if (capacity <= 0) return;

            Item ammo = factory.CreateItem(((IIdGenerator)controller).NextId, ammoTpl, null);
            if (ammo == null)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] factory.CreateItem({ammoTpl}) returned null.");
                return;
            }
            ammo.StackObjectsCount = capacity;

            var result = mag.Cartridges.Add(ammo, simulate: false);
            if (result.Failed)
            {
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] Cartridges.Add failed for {magItem.TemplateId}: {result.Error}");
                return;
            }
            Plugin.LogSource?.LogInfo($"[ZombiesLoadout] loaded {capacity}x {ammoTpl} into {magItem.TemplateId}.");
        }

        // mirrors BeltHolderHelper.GetBeltSlot from Trenchfoot-BeltSlot: walk
        // pockets -> hidden grid by id -> holder by tpl -> mod_belt by slot id.
        // duplicated inline so we don't reference the Trenchfoot assembly.
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

        // builds an item tree from a preset list. handles nested mods (mods on
        // mods - the 1911's receiver carries sights + muzzle) via topological
        // sort: root first, then each child once its parent has been minted.
        // returns the root item with all mods attached, ready to drop into a
        // slot.
        private static Item BuildPresetTree(InventoryController controller, ItemFactoryClass factory, PresetEntry[] preset)
        {
            var byPresetId = new Dictionary<string, Item>(preset.Length);
            var pending = new List<PresetEntry>(preset);
            int safety = preset.Length + 2;

            while (pending.Count > 0 && safety-- > 0)
            {
                bool progress = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    PresetEntry entry = pending[i];

                    if (entry.ParentPresetId == null)
                    {
                        Item root = factory.CreateItem(((IIdGenerator)controller).NextId, entry.Tpl, null);
                        if (root == null)
                        {
                            Plugin.LogSource?.LogError($"[ZombiesLoadout] CreateItem({entry.Tpl}) returned null for root.");
                            return null;
                        }
                        byPresetId[entry.PresetId] = root;
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }

                    if (!byPresetId.TryGetValue(entry.ParentPresetId, out Item parent)) continue;

                    // chamber rounds (patron_in_weapon, patron_in_weapon_000, etc)
                    // live on Weapon.Chambers - a separate Slot[] from the
                    // regular CompoundItem.Slots used for mods. fall back to
                    // Chambers if the standard Slots lookup misses.
                    Slot slot = null;
                    CompoundItem compound = parent as CompoundItem;
                    if (compound?.Slots != null)
                        slot = compound.Slots.FirstOrDefault(s => s != null && s.ID == entry.SlotId);
                    if (slot == null && parent is Weapon weaponParent && weaponParent.Chambers != null)
                        slot = weaponParent.Chambers.FirstOrDefault(s => s != null && s.ID == entry.SlotId);

                    if (slot == null)
                    {
                        Plugin.LogSource?.LogWarning($"[ZombiesLoadout] slot '{entry.SlotId}' missing on {parent.TemplateId}; skipping mod {entry.Tpl}.");
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }

                    Item mod = factory.CreateItem(((IIdGenerator)controller).NextId, entry.Tpl, null);
                    if (mod == null)
                    {
                        Plugin.LogSource?.LogWarning($"[ZombiesLoadout] CreateItem({entry.Tpl}) returned null; skipping.");
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }

                    slot.ChangeContainedItemDirectly(mod);
                    byPresetId[entry.PresetId] = mod;
                    pending.RemoveAt(i);
                    progress = true;
                }
                if (!progress) break;
            }

            if (pending.Count > 0)
                Plugin.LogSource?.LogWarning($"[ZombiesLoadout] {pending.Count} preset entries could not be attached (parents never resolved).");

            return byPresetId.TryGetValue("root", out Item rootItem) ? rootItem : null;
        }
    }
}
