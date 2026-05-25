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
    // sibling of UmpWallbuyActionPatch / Mp43WallbuyActionPatch for the STG-44
    // wallbuy. tpls + slot layout come from
    // D:\SPTDev\SPT\user\mods\WTT-PresetConverterPlusServer\Presets\GlobalPresets\stgbby.json
    //
    // tree:
    //   root (STG-44)
    //     mod_magazine        -> 30rd mag, loaded with 30 cartridges
    //     mod_sight_rear      -> rear sight
    //     patron_in_weapon    -> 1 chambered round
    //
    // simpler than UMP/MP43 (no muzzle, no foregrip, no scope mounts) but
    // same flow: build preset, equip in primary slot, dispense spare mags,
    // auto-switch. restock branch refills ammo on re-buy at half price.
    internal sealed class StgWallbuyActionPatch : ModulePatch
    {
        public const int TarCoinPrice = 1400;

        public const string StgRootTpl     = "6a0b33c5263dc294754193ff"; // STG-44
        public const string StgMagTpl      = "6a0b8880a02e563a2a145010"; // 30rd mag
        public const string StgRearSightTpl= "6a0b8a84a02e563a2a145014"; // rear sight
        public const string StgAmmoTpl     = "6a0b879aa02e563a2a14500f"; // 7.92x33mm Kurz FMJ (was tracer 6a0b88cfa02e563a2a145012)
        private const int MagCount = 4;

        // every tpl whose viewmodel bundle has to be loaded before the player
        // can switch hands to this weapon. consumed by the spawn patch's
        // PreloadBundlesAsync at raid start.
        public static readonly string[] RequiredBundleTpls =
        {
            StgRootTpl,
            StgMagTpl,
            StgRearSightTpl,
            StgAmmoTpl,
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

        // tree mirrors stgbby.json. chamber lives on Weapon.Chambers (patron_in_weapon)
        // not CompoundItem.Slots; BuildPresetTree handles that fallback.
        private static readonly PresetEntry[] StgTree =
        {
            new PresetEntry("root",      StgRootTpl,        null,    null),
            new PresetEntry("magazine",  StgMagTpl,         "root",  "mod_magazine"),
            new PresetEntry("rearsight", StgRearSightTpl,   "root",  "mod_sight_rear"),
            new PresetEntry("chamber",   StgAmmoTpl,        "root",  "patron_in_weapon"),
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
            StgWallbuy wallbuy = interactive as StgWallbuy;
            if (wallbuy == null) return true;

            try { __result = BuildActions(owner, wallbuy); }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[StgWallbuy] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, StgWallbuy wallbuy)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            InventoryController inv = owner.Player.InventoryController;
            bool isRefill = inv != null && FindFirstByTpl(inv.Inventory.Equipment, StgRootTpl) != null;
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

        private static void OnBuy(StgWallbuy wallbuy, Player player, int price)
        {
            try
            {
                if (!TarCoinWallet.TrySpend(player, price))
                {
                    Plugin.LogSource?.LogInfo("[StgWallbuy] not enough TarCoins; buy aborted.");
                    return;
                }
                wallbuy.PlayBuySound();
                _ = DispenseAsync();
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"[StgWallbuy] buy failed: {ex.Message}"); }
        }

        private static async Task DispenseAsync()
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer == null) return;
            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            InventoryController inv = gw.MainPlayer.InventoryController;
            if (factory == null || inv == null)
            {
                Plugin.LogSource?.LogWarning("[StgWallbuy] ItemFactory or InventoryController missing.");
                return;
            }

            // restock mode: if the player already owns an STG, refill its
            // chambers + in-weapon mag, top off any spare mags they have to
            // capacity, and dispense enough fresh mags to bring spare count
            // back up to MagCount.
            Item existing = FindFirstByTpl(inv.Inventory.Equipment, StgRootTpl);
            if (existing != null)
            {
                await RestockExisting(existing, inv, factory);
                return;
            }

            Item weapon = BuildPresetTree(inv, factory, StgTree);
            if (weapon == null) return;
            weapon.SpawnedInSession = true;

            // fill the magazine's Cartridges StackSlot to capacity (30 rounds
            // of 7.92x33mm Kurz) so the gun has 30 + 1 ready on Buy.
            LoadMagazineInSlot(weapon, "mod_magazine", StgAmmoTpl, inv, factory);

            EquipmentSlot targetSlot = ResolveTargetSlot(gw.MainPlayer, inv);
            Plugin.LogSource?.LogInfo($"[StgWallbuy] target slot: {targetSlot}");

            Slot equipSlot = inv.Inventory.Equipment.GetSlot(targetSlot);
            if (equipSlot.ContainedItem != null)
            {
                Plugin.LogSource?.LogInfo($"[StgWallbuy] {targetSlot} occupied by {equipSlot.ContainedItem.TemplateId}; replacing.");
                DiscardItem(equipSlot.ContainedItem, inv);
                WallbuyAmmoTracker.DiscardForSlot(targetSlot, inv);
            }

            List<Item> givenMags = await AddLoadedMagStacks(inv, factory, StgMagTpl, StgAmmoTpl, MagCount);
            WallbuyAmmoTracker.Register(targetSlot, givenMags);

            // pre-load the weapon's held-prefab bundle BEFORE SwitchToWeapon
            // (see WallbuyBundleLoader.EnsureItemBundleLoaded for why).
            await WallbuyBundleLoader.EnsureItemBundleLoaded(weapon);

            equipSlot.ChangeContainedItemDirectly(weapon);
            InventoryEventHelpers.RaiseSlotChange(equipSlot, weapon);
            Plugin.LogSource?.LogInfo($"[StgWallbuy] STG-44 equipped in {targetSlot}.");

            SwitchToWeapon(gw.MainPlayer, weapon);
        }

        // Max Ammo power-up entry: if the player owns the STG-44, run the
        // restock branch without spending TC.
        public static System.Threading.Tasks.Task RefillForMaxAmmoPickup(Player player)
        {
            try
            {
                InventoryController inv = player?.InventoryController;
                if (inv == null) return System.Threading.Tasks.Task.CompletedTask;
                Item weapon = FindFirstByTpl(inv.Inventory.Equipment, StgRootTpl);
                if (weapon == null)
                {
                    Plugin.LogSource?.LogInfo("[MaxAmmo] STG refill skipped: player doesn't own an STG-44.");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                if (factory == null) return System.Threading.Tasks.Task.CompletedTask;
                Plugin.LogSource?.LogInfo("[MaxAmmo] STG refill running...");
                return RestockExisting(weapon, inv, factory);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] STG RefillForMaxAmmoPickup threw: {ex.Message}");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        private static async Task RestockExisting(Item weapon, InventoryController inv, ItemFactoryClass factory)
        {
            if (weapon is Weapon w)
            {
                RefillChambers(w, StgAmmoTpl, inv, factory);
                MaxAmmoRestockHelper.RepairWeapon(w);
            }

            CompoundItem compound = weapon as CompoundItem;
            Slot magSlot = compound?.Slots?.FirstOrDefault(s => s != null && s.ID == "mod_magazine");
            Item inWeaponMag = magSlot?.ContainedItem;
            if (inWeaponMag != null) RefillMagazineToCapacity(inWeaponMag, StgAmmoTpl, inv, factory);

            int spareCount = 0;
            foreach (Item it in inv.Inventory.Equipment.GetAllItemsFromCollection())
            {
                if (it == null) continue;
                if (it.TemplateId != StgMagTpl) continue;
                if (it == inWeaponMag) continue;
                RefillMagazineToCapacity(it, StgAmmoTpl, inv, factory);
                spareCount++;
            }

            int needed = MagCount - spareCount;
            if (needed > 0)
            {
                List<Item> givenMags = await AddLoadedMagStacks(inv, factory, StgMagTpl, StgAmmoTpl, needed);
                EquipmentSlot? hostSlot = FindSlotContaining(inv.Inventory.Equipment, weapon);
                if (hostSlot.HasValue) WallbuyAmmoTracker.Register(hostSlot.Value, givenMags);
            }

            Plugin.LogSource?.LogInfo($"[StgWallbuy] restock: chambers refilled, spare mags={spareCount}, dispensed {Math.Max(0, needed)} fresh.");
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
                if (chamber.ContainedItem != null) continue;
                Item round = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
                if (round != null) chamber.ChangeContainedItemDirectly(round);
            }
        }

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
                            Plugin.LogSource?.LogError($"[StgWallbuy] CreateItem({entry.Tpl}) returned null for root.");
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
                        Plugin.LogSource?.LogWarning($"[StgWallbuy] slot '{entry.SlotId}' missing on {parent.TemplateId}; skipping mod {entry.Tpl}.");
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }

                    Item mod = factory.CreateItem(((IIdGenerator)inv).NextId, entry.Tpl, null);
                    if (mod == null)
                    {
                        Plugin.LogSource?.LogWarning($"[StgWallbuy] CreateItem({entry.Tpl}) returned null; skipping.");
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
                Plugin.LogSource?.LogWarning($"[StgWallbuy] {pending.Count} preset entries could not be attached (parents never resolved).");

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
        // in ZombiesLoadoutPatch so STG mags overflow into the PackNStrap
        // belt when the rig is full. see the comment on
        // ZombiesLoadoutPatch.PlaceLoadedMagsAcrossEquipment for the full
        // reasoning - tl;dr the old TraderServicesEligibleSlots path didn't
        // include the belt and silently dropped mags when the rig packed out.
        //
        // synchronous under the hood but kept Task-returning so the call
        // sites (DispenseAsync / RestockExisting) don't have to change shape.
        private static Task<List<Item>> AddLoadedMagStacks(InventoryController inv, ItemFactoryClass factory, string magTpl, string ammoTpl, int count)
        {
            List<Item> added = ZombiesLoadoutPatch.PlaceLoadedMagsAcrossEquipment(
                inv.Inventory.Equipment, magTpl, ammoTpl, count, inv, factory);
            foreach (Item m in added) m.SpawnedInSession = true;
            Plugin.LogSource?.LogInfo($"[StgWallbuy] dispensed {added.Count}/{count} loaded mag(s) to inventory.");
            return Task.FromResult(added);
        }

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

            Plugin.LogSource?.LogInfo("[StgWallbuy] both primaries full and player not holding a primary; defaulting to FirstPrimaryWeapon.");
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
                    Plugin.LogSource?.LogWarning($"[StgWallbuy] Discard failed for {item?.TemplateId}: {result.Error}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[StgWallbuy] Discard threw for {item?.TemplateId}: {ex.Message}");
            }
        }

        private static void SwitchToWeapon(Player player, Item weapon)
        {
            try
            {
                Weapon w = weapon as Weapon;
                if (w == null)
                {
                    Plugin.LogSource?.LogWarning("[StgWallbuy] cannot switch to weapon: cast to Weapon failed.");
                    return;
                }
                player.Proceed(w, new Callback<IFirearmHandsController>(result =>
                {
                    if (result.Failed)
                        Plugin.LogSource?.LogWarning($"[StgWallbuy] auto-switch failed: {result.Error}");
                }), scheduled: true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[StgWallbuy] SwitchToWeapon threw: {ex.Message}");
            }
        }
    }
}
