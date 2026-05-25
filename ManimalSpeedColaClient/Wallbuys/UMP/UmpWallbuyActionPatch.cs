using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.SpeedCola.Components;
using SPT.Reflection.Patching;

namespace Manimal.SpeedCola.Patches
{
    // sibling of Mp43WallbuyActionPatch but for the UMP-45 wallbuy. on Buy:
    //   1. build the UMP-45 weapon tree (root + barrel + scope + 3 mounts +
    //      chamber round + cartridges-filled mag + muzzle on barrel + foregrip
    //      on mount_000). tree shape mirrors the zombiesUMP.json preset.
    //   2. resolve a primary slot to drop it in (empty wins; else held weapon's
    //      slot; else FirstPrimary fallback).
    //   3. clear that slot + any tracked-ammo we previously gave for it.
    //   4. dispense 4 spare mags pre-loaded with .45 ACP Lasermatch FMJ to the
    //      player's containers FIRST (before the weapon swap leaves the
    //      inventory in a half-updated state).
    //   5. equip the weapon directly and auto-switch hands to it.
    internal sealed class UmpWallbuyActionPatch : ModulePatch
    {
        public const int TarCoinPrice = 1000;

        public const string UmpRootTpl    = "5fc3e272f8b6a877a729eac5"; // UMP-45
        public const string UmpBarrelTpl  = "5fc3e4a27283c4046c5814ab";
        public const string UmpScopeTpl   = "609a63b6e2ff132951242d09";
        public const string UmpMagTpl     = "5fc3e466187fea44d52eda90"; // UMP-45 25rd mag
        public const string UmpMount000   = "5fc53954f8b6a877a729eaeb";
        public const string UmpMount001   = "5fc5396e900b1d5091531e72";
        public const string UmpMuzzleTpl  = "6130c4d51cb55961fa0fd49f"; // on mod_muzzle of barrel
        public const string UmpForegripTpl= "5c1bc5af2e221602b412949b"; // on mod_foregrip of mount_000
        public const string LasermatchTpl = "5efb0d4f4bc50b58e81710f3"; // .45 ACP Lasermatch FMJ
        private const int MagCount = 4;

        // every tpl whose viewmodel bundle has to be loaded before the player
        // can switch hands to this weapon. consumed by the spawn patch's
        // PreloadBundlesAsync at raid start.
        public static readonly string[] RequiredBundleTpls =
        {
            UmpRootTpl,
            UmpBarrelTpl,
            UmpScopeTpl,
            UmpMagTpl,
            UmpMount000,
            UmpMount001,
            UmpMuzzleTpl,
            UmpForegripTpl,
            LasermatchTpl,
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

        // tree mirrors zombiesUMP.json. nested mods (muzzle on barrel,
        // foregrip on mount_000, chamber + cartridges) handled by the
        // BuildPresetTree topological sort.
        private static readonly PresetEntry[] UmpTree =
        {
            new PresetEntry("root",      UmpRootTpl,        null,        null),
            new PresetEntry("magazine",  UmpMagTpl,         "root",      "mod_magazine"),
            new PresetEntry("scope",     UmpScopeTpl,       "root",      "mod_scope"),
            new PresetEntry("barrel",    UmpBarrelTpl,      "root",      "mod_barrel"),
            new PresetEntry("mount000",  UmpMount000,       "root",      "mod_mount_000"),
            new PresetEntry("mount001",  UmpMount001,       "root",      "mod_mount_001"),
            new PresetEntry("mount002",  UmpMount001,       "root",      "mod_mount_002"),
            new PresetEntry("chamber",   LasermatchTpl,     "root",      "patron_in_weapon"),
            new PresetEntry("muzzle",    UmpMuzzleTpl,      "barrel",    "mod_muzzle"),
            new PresetEntry("foregrip",  UmpForegripTpl,    "mount000",  "mod_foregrip"),
        };

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
        private static bool Prefix(GamePlayerOwner owner, object interactive, ref ActionsReturnClass __result)
        {
            UmpWallbuy wallbuy = interactive as UmpWallbuy;
            if (wallbuy == null) return true;

            try { __result = BuildActions(owner, wallbuy); }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[UmpWallbuy] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, UmpWallbuy wallbuy)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            // restock mode shows up when the player already owns a UMP - the
            // dispense flow then refills mags/chambers instead of giving a
            // dupe weapon, and we charge half price for that. ownership check
            // mirrors the one in DispenseAsync so the label matches what the
            // buy actually does.
            InventoryController inv = owner.Player.InventoryController;
            bool isRefill = inv != null && FindFirstByTpl(inv.Inventory.Equipment, UmpRootTpl) != null;
            int price = isRefill ? TarCoinPrice / 2 : TarCoinPrice;
            string prefix = isRefill ? "BUY AMMO:" : "BUY";

            int balance = TarCoinWallet.Balance(owner.Player);
            bool canAfford = balance >= price;
            string label = canAfford
                ? $"{prefix} ({price} TC)"
                : $"{prefix} ({price} TC) - need {price - balance} more";

            result.Actions.Add(new ActionsTypesClass
            {
                Name = label,
                Disabled = !canAfford,
                Action = canAfford ? (Action)(() => OnBuy(wallbuy, owner.Player, price)) : null,
            });
            return result;
        }

        private static void OnBuy(UmpWallbuy wallbuy, Player player, int price)
        {
            try
            {
                if (!TarCoinWallet.TrySpend(player, price))
                {
                    Plugin.LogSource?.LogInfo("[UmpWallbuy] not enough TarCoins; buy aborted.");
                    return;
                }
                wallbuy.PlayBuySound();
                wallbuy.PlayBuyAnimation();
                _ = DispenseAsync();
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"[UmpWallbuy] buy failed: {ex.Message}"); }
        }

        private static async Task DispenseAsync()
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer == null) return;
            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            InventoryController inv = gw.MainPlayer.InventoryController;
            if (factory == null || inv == null)
            {
                Plugin.LogSource?.LogWarning("[UmpWallbuy] ItemFactory or InventoryController missing.");
                return;
            }

            // restock mode: if the player already owns a UMP, refill its
            // chambers + in-weapon mag, top off any spare mags they have to
            // capacity, and dispense enough fresh mags to bring the spare
            // count back up to MagCount. avoids stacking up dupe weapons
            // every time the player re-buys.
            Item existing = FindFirstByTpl(inv.Inventory.Equipment, UmpRootTpl);
            if (existing != null)
            {
                await RestockExisting(existing, inv, factory);
                return;
            }

            Item weapon = BuildPresetTree(inv, factory, UmpTree);
            if (weapon == null) return;
            weapon.SpawnedInSession = true;

            // chamber comes from the preset; the magazine slot has the empty
            // mag attached. fill its Cartridges StackSlot with 25x Lasermatch
            // so the gun has 25 + 1 ready on Buy.
            LoadMagazineInSlot(weapon, "mod_magazine", LasermatchTpl, inv, factory);

            EquipmentSlot targetSlot = ResolveTargetSlot(gw.MainPlayer, inv);
            Plugin.LogSource?.LogInfo($"[UmpWallbuy] target slot: {targetSlot}");

            Slot equipSlot = inv.Inventory.Equipment.GetSlot(targetSlot);
            if (equipSlot.ContainedItem != null)
            {
                Plugin.LogSource?.LogInfo($"[UmpWallbuy] {targetSlot} occupied by {equipSlot.ContainedItem.TemplateId}; replacing.");
                DiscardItem(equipSlot.ContainedItem, inv);
                WallbuyAmmoTracker.DiscardForSlot(targetSlot, inv);
            }

            // dispense 4 spare loaded mags BEFORE the weapon swap (same
            // ordering reason as Mp43WallbuyActionPatch - the network
            // transaction fails on "Can not execute" if inventory is mid-update).
            List<Item> givenMags = await AddLoadedMagStacks(inv, factory, UmpMagTpl, LasermatchTpl, MagCount);
            WallbuyAmmoTracker.Register(targetSlot, givenMags);

            // pre-load the weapon's held-prefab bundle BEFORE SwitchToWeapon
            // (see WallbuyBundleLoader.EnsureItemBundleLoaded for why).
            await WallbuyBundleLoader.EnsureItemBundleLoaded(weapon);

            equipSlot.ChangeContainedItemDirectly(weapon);
            // fire Slot.OnAddOrRemoveItem so the equipment-bar HUD refreshes
            // immediately rather than waiting for the player to open inventory.
            InventoryEventHelpers.RaiseSlotChange(equipSlot, weapon);
            Plugin.LogSource?.LogInfo($"[UmpWallbuy] UMP-45 equipped in {targetSlot}.");

            SwitchToWeapon(gw.MainPlayer, weapon);
        }

        // Max Ammo power-up entry: if the player owns the UMP-45, run the same
        // restock branch the wallbuy uses on re-buy (without spending TC).
        // no-op if the player doesn't have one.
        public static System.Threading.Tasks.Task RefillForMaxAmmoPickup(Player player)
        {
            try
            {
                InventoryController inv = player?.InventoryController;
                if (inv == null) return System.Threading.Tasks.Task.CompletedTask;
                Item weapon = FindFirstByTpl(inv.Inventory.Equipment, UmpRootTpl);
                if (weapon == null)
                {
                    Plugin.LogSource?.LogInfo("[MaxAmmo] UMP refill skipped: player doesn't own a UMP-45.");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                if (factory == null) return System.Threading.Tasks.Task.CompletedTask;
                Plugin.LogSource?.LogInfo("[MaxAmmo] UMP refill running...");
                return RestockExisting(weapon, inv, factory);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] UMP RefillForMaxAmmoPickup threw: {ex.Message}");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        // restock path: weapon already owned, just refill ammo + top up mag
        // count to MagCount spare. tracks newly-given mags into the same
        // slot dictionary so a later replacement (different gun bought to
        // same slot) cleans them up.
        private static async Task RestockExisting(Item weapon, InventoryController inv, ItemFactoryClass factory)
        {
            // weapon chambers
            if (weapon is Weapon w)
            {
                RefillChambers(w, LasermatchTpl, inv, factory);
                MaxAmmoRestockHelper.RepairWeapon(w);
            }

            // in-weapon mag (if any). also counted toward total mags.
            CompoundItem compound = weapon as CompoundItem;
            Slot magSlot = compound?.Slots?.FirstOrDefault(s => s != null && s.ID == "mod_magazine");
            Item inWeaponMag = magSlot?.ContainedItem;
            if (inWeaponMag != null) RefillMagazineToCapacity(inWeaponMag, LasermatchTpl, inv, factory);

            // walk all UMP mag items in the inventory. refill each and count
            // spares (everything except the in-weapon one).
            int spareCount = 0;
            foreach (Item it in inv.Inventory.Equipment.GetAllItemsFromCollection())
            {
                if (it == null) continue;
                if (it.TemplateId != UmpMagTpl) continue;
                if (it == inWeaponMag) continue;
                RefillMagazineToCapacity(it, LasermatchTpl, inv, factory);
                spareCount++;
            }

            int needed = MagCount - spareCount;
            if (needed > 0)
            {
                List<Item> givenMags = await AddLoadedMagStacks(inv, factory, UmpMagTpl, LasermatchTpl, needed);
                EquipmentSlot? hostSlot = FindSlotContaining(inv.Inventory.Equipment, weapon);
                if (hostSlot.HasValue) WallbuyAmmoTracker.Register(hostSlot.Value, givenMags);
            }

            Plugin.LogSource?.LogInfo($"[UmpWallbuy] restock: chambers refilled, spare mags={spareCount}, dispensed {Math.Max(0, needed)} fresh.");
        }

        private static Item FindFirstByTpl(InventoryEquipment equipment, string tpl)
        {
            if (equipment == null) return null;
            foreach (Item it in equipment.GetAllItemsFromCollection())
            {
                if (it?.TemplateId == tpl) return it;
            }
            return null;
        }

        private static EquipmentSlot? FindSlotContaining(InventoryEquipment equipment, Item item)
        {
            foreach (EquipmentSlot slot in new[]
            {
                EquipmentSlot.FirstPrimaryWeapon,
                EquipmentSlot.SecondPrimaryWeapon,
                EquipmentSlot.Holster,
            })
            {
                if (equipment.GetSlot(slot)?.ContainedItem == item) return slot;
            }
            return null;
        }

        private static void RefillChambers(Weapon weapon, string ammoTpl, InventoryController inv, ItemFactoryClass factory)
        {
            if (weapon?.Chambers == null) return;
            foreach (Slot chamber in weapon.Chambers)
            {
                if (chamber == null) continue;
                if (chamber.ContainedItem != null) continue; // chambers hold 1; already loaded
                Item round = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
                if (round != null) chamber.ChangeContainedItemDirectly(round);
            }
        }

        // refill a magazine's Cartridges StackSlot to capacity. mags hold at
        // most one stack of ammo at any time, so we either bump the existing
        // stack's StackObjectsCount to MaxCount or add a fresh full stack
        // when the mag is empty. doesn't replace ammo if the mag has a
        // different ammo type loaded - we just top off whatever's in there.
        private static void RefillMagazineToCapacity(Item magItem, string ammoTpl, InventoryController inv, ItemFactoryClass factory)
        {
            MagazineItemClass mag = magItem as MagazineItemClass;
            if (mag?.Cartridges == null) return;
            int capacity = mag.Cartridges.MaxCount;
            if (capacity <= 0) return;

            Item existingStack = null;
            foreach (Item stack in mag.Cartridges.Items)
            {
                existingStack = stack;
                break;
            }

            if (existingStack != null)
            {
                existingStack.StackObjectsCount = capacity;
                return;
            }

            Item ammo = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
            if (ammo == null) return;
            ammo.StackObjectsCount = capacity;
            mag.Cartridges.Add(ammo, simulate: false);
        }

        // topological-sort tree builder. mirrors ZombiesLoadoutPatch.BuildPresetTree
        // (with the chambers fall-through). chambers (patron_in_weapon) live
        // on Weapon.Chambers, separate from CompoundItem.Slots.
        private static Item BuildPresetTree(InventoryController inv, ItemFactoryClass factory, PresetEntry[] preset)
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
                        Item root = factory.CreateItem(((IIdGenerator)inv).NextId, entry.Tpl, null);
                        if (root == null)
                        {
                            Plugin.LogSource?.LogError($"[UmpWallbuy] CreateItem({entry.Tpl}) returned null for root.");
                            return null;
                        }
                        byPresetId[entry.PresetId] = root;
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }

                    if (!byPresetId.TryGetValue(entry.ParentPresetId, out Item parent)) continue;

                    Slot slot = null;
                    CompoundItem compound = parent as CompoundItem;
                    if (compound?.Slots != null)
                        slot = compound.Slots.FirstOrDefault(s => s != null && s.ID == entry.SlotId);
                    if (slot == null && parent is Weapon weaponParent && weaponParent.Chambers != null)
                        slot = weaponParent.Chambers.FirstOrDefault(s => s != null && s.ID == entry.SlotId);

                    if (slot == null)
                    {
                        Plugin.LogSource?.LogWarning($"[UmpWallbuy] slot '{entry.SlotId}' missing on {parent.TemplateId}; skipping mod {entry.Tpl}.");
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }

                    Item mod = factory.CreateItem(((IIdGenerator)inv).NextId, entry.Tpl, null);
                    if (mod == null)
                    {
                        Plugin.LogSource?.LogWarning($"[UmpWallbuy] CreateItem({entry.Tpl}) returned null; skipping.");
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
                Plugin.LogSource?.LogWarning($"[UmpWallbuy] {pending.Count} preset entries could not be attached (parents never resolved).");

            return byPresetId.TryGetValue("root", out Item rootItem) ? rootItem : null;
        }

        private static void LoadMagazineInSlot(Item parent, string slotId, string ammoTpl, InventoryController inv, ItemFactoryClass factory)
        {
            CompoundItem compound = parent as CompoundItem;
            Slot slot = compound?.Slots?.FirstOrDefault(s => s != null && s.ID == slotId);
            if (slot?.ContainedItem == null) return;
            LoadAmmoIntoMagazine(slot.ContainedItem, ammoTpl, inv, factory);
        }

        private static void LoadAmmoIntoMagazine(Item magItem, string ammoTpl, InventoryController inv, ItemFactoryClass factory)
        {
            MagazineItemClass mag = magItem as MagazineItemClass;
            if (mag?.Cartridges == null) return;
            int capacity = mag.Cartridges.MaxCount;
            if (capacity <= 0) return;

            Item ammo = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
            if (ammo == null) return;
            ammo.StackObjectsCount = capacity;
            mag.Cartridges.Add(ammo, simulate: false);
        }

        // delegates to the shared rig -> pockets -> belt -> backpack cascade
        // in ZombiesLoadoutPatch so UMP mags overflow into the PackNStrap
        // belt when the rig is full. mirrors BAR/STG/Rot/MP-43.
        private static Task<List<Item>> AddLoadedMagStacks(InventoryController inv, ItemFactoryClass factory, string magTpl, string ammoTpl, int count)
        {
            List<Item> added = ZombiesLoadoutPatch.PlaceLoadedMagsAcrossEquipment(
                inv.Inventory.Equipment, magTpl, ammoTpl, count, inv, factory);
            foreach (Item m in added) m.SpawnedInSession = true;
            Plugin.LogSource?.LogInfo($"[UmpWallbuy] dispensed {added.Count}/{count} loaded mag(s) to inventory.");
            return Task.FromResult(added);
        }

        // ---- shared with Mp43WallbuyActionPatch by intent; duplicated here
        //      because the original is private and refactoring isn't worth the
        //      churn for a placeholder feature. ----

        private static EquipmentSlot ResolveTargetSlot(Player player, InventoryController inv)
        {
            InventoryEquipment equipment = inv.Inventory.Equipment;
            bool firstEmpty  = equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem == null;
            bool secondEmpty = equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem == null;

            if (firstEmpty)  return EquipmentSlot.FirstPrimaryWeapon;
            if (secondEmpty) return EquipmentSlot.SecondPrimaryWeapon;

            EquipmentSlot? heldSlot = GetHeldWeaponSlot(player, equipment);
            if (heldSlot == EquipmentSlot.FirstPrimaryWeapon ||
                heldSlot == EquipmentSlot.SecondPrimaryWeapon)
                return heldSlot.Value;

            Plugin.LogSource?.LogInfo("[UmpWallbuy] both primaries full and player not holding a primary; defaulting to FirstPrimaryWeapon.");
            return EquipmentSlot.FirstPrimaryWeapon;
        }

        private static EquipmentSlot? GetHeldWeaponSlot(Player player, InventoryEquipment equipment)
        {
            try
            {
                Item handsItem = (player?.HandsController as Player.FirearmController)?.Item;
                if (handsItem == null) return null;

                foreach (EquipmentSlot slotName in new[]
                {
                    EquipmentSlot.FirstPrimaryWeapon,
                    EquipmentSlot.SecondPrimaryWeapon,
                    EquipmentSlot.Holster,
                    EquipmentSlot.Scabbard,
                })
                {
                    Slot slot = equipment.GetSlot(slotName);
                    if (slot?.ContainedItem?.Id == handsItem.Id) return slotName;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static void DiscardItem(Item item, InventoryController inv)
        {
            try
            {
                var result = InteractionsHandlerClass.DiscardWithoutRestrictions(item, inv);
                if (result.Failed)
                    Plugin.LogSource?.LogWarning($"[UmpWallbuy] Discard failed for {item?.TemplateId}: {result.Error}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[UmpWallbuy] Discard threw for {item?.TemplateId}: {ex.Message}");
            }
        }

        private static void SwitchToWeapon(Player player, Item weapon)
        {
            try
            {
                Weapon w = weapon as Weapon;
                if (w == null)
                {
                    Plugin.LogSource?.LogWarning("[UmpWallbuy] cannot switch to weapon: cast to Weapon failed.");
                    return;
                }
                player.Proceed(w, new Callback<IFirearmHandsController>(result =>
                {
                    if (result.Failed)
                        Plugin.LogSource?.LogWarning($"[UmpWallbuy] auto-switch failed: {result.Error}");
                }), scheduled: true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[UmpWallbuy] SwitchToWeapon threw: {ex.Message}");
            }
        }
    }
}
