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
    // sibling of UmpWallbuyActionPatch but for the "Rot" weapon (preset
    // imported from WTT-PresetConverterPlusServer/Presets/GlobalPresets/
    // rotzombies.json). dispenses the assembled weapon + 5 spare 20-round
    // mags filled with 5.56x45 HP. half-price ammo-refill branch when the
    // player already owns one.
    internal sealed class RotWallbuyActionPatch : ModulePatch
    {
        public const int TarCoinPrice = 1200;

        // tpls lifted from rotzombies.json. the 696fe*/696e4* range is the
        // custom "Rot" weapon mod tpl space added by WTT.
        public const string RotRootTpl     = "696fe2ebcf7469bf3805173f"; // root weapon
        public const string RotPistolGrip  = "696fe221cf7469bf38051738";
        public const string RotMagTpl      = "5448c1d04bdc2dff2f8b4569"; // 20rd mag
        public const string RotReceiver    = "696fe281cf7469bf3805173d";
        public const string RotStock1      = "696fe281cf7469bf3805173a"; // stock root
        public const string RotStock2      = "5c793fb92e221644f31bfb64"; // nested stock
        public const string RotStock000    = "5d135ecbd7ad1a21c176542e"; // mod_stock_000
        public const string RotBarrelTpl   = "696e452a1a038d893a04a429";
        public const string RotMuzzleTpl   = "612e0e55a112697a4b3a66e7";
        public const string RotSightRear   = "664a5ff61c4610dc01e84917";
        public const string RotSightFront  = "664a600b58753b43b7fea3af";
        public const string RotHandguard   = "696fe221cf7469bf38051736";
        public const string RotForegrip    = "57cffcd624597763133760c5";
        public const string AmmoTpl        = "59e6927d86f77411da468256"; // 5.56x45 55 HP

        private const int MagCount = 6;

        public static readonly string[] RequiredBundleTpls =
        {
            RotRootTpl, RotPistolGrip, RotMagTpl, RotReceiver, RotStock1, RotStock2, RotStock000,
            RotBarrelTpl, RotMuzzleTpl, RotSightRear, RotSightFront, RotHandguard, RotForegrip,
            AmmoTpl,
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

        // tree mirrors rotzombies.json. nested mods (muzzle on barrel, foregrip
        // on handguard, the two nested stock pieces, chamber + cartridges)
        // handled by the topological-sort BuildPresetTree.
        private static readonly PresetEntry[] RotTree =
        {
            new PresetEntry("root",       RotRootTpl,    null,         null),
            new PresetEntry("grip",       RotPistolGrip, "root",       "mod_pistol_grip"),
            new PresetEntry("magazine",   RotMagTpl,     "root",       "mod_magazine"),
            new PresetEntry("receiver",   RotReceiver,   "root",       "mod_reciever"),
            new PresetEntry("stock1",     RotStock1,     "root",       "mod_stock"),
            new PresetEntry("chamber",    AmmoTpl,       "root",       "patron_in_weapon"),
            new PresetEntry("barrel",     RotBarrelTpl,  "receiver",   "mod_barrel"),
            new PresetEntry("sightrear",  RotSightRear,  "receiver",   "mod_sight_rear"),
            new PresetEntry("sightfront", RotSightFront, "receiver",   "mod_sight_front"),
            new PresetEntry("handguard",  RotHandguard,  "receiver",   "mod_handguard"),
            new PresetEntry("muzzle",     RotMuzzleTpl,  "barrel",     "mod_muzzle"),
            new PresetEntry("foregrip",   RotForegrip,   "handguard",  "mod_foregrip"),
            new PresetEntry("stock2",     RotStock2,     "stock1",     "mod_stock"),
            new PresetEntry("stock000",   RotStock000,   "stock2",     "mod_stock_000"),
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
            RotWallbuy wallbuy = interactive as RotWallbuy;
            if (wallbuy == null) return true;

            try { __result = BuildActions(owner, wallbuy); }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[RotWallbuy] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, RotWallbuy wallbuy)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            InventoryController inv = owner.Player.InventoryController;
            bool isRefill = inv != null && FindFirstByTpl(inv.Inventory.Equipment, RotRootTpl) != null;
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

        private static void OnBuy(RotWallbuy wallbuy, Player player, int price)
        {
            try
            {
                if (!TarCoinWallet.TrySpend(player, price))
                {
                    Plugin.LogSource?.LogInfo("[RotWallbuy] not enough TarCoins; buy aborted.");
                    return;
                }
                wallbuy.PlayBuySound();
                wallbuy.PlayBuyAnimation();
                _ = DispenseAsync();
            }
            catch (Exception ex) { Plugin.LogSource?.LogError($"[RotWallbuy] buy failed: {ex.Message}"); }
        }

        private static async Task DispenseAsync()
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer == null) return;
            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            InventoryController inv = gw.MainPlayer.InventoryController;
            if (factory == null || inv == null)
            {
                Plugin.LogSource?.LogWarning("[RotWallbuy] ItemFactory or InventoryController missing.");
                return;
            }

            Item existing = FindFirstByTpl(inv.Inventory.Equipment, RotRootTpl);
            if (existing != null)
            {
                await RestockExisting(existing, inv, factory);
                return;
            }

            Item weapon = BuildPresetTree(inv, factory, RotTree);
            if (weapon == null) return;
            weapon.SpawnedInSession = true;

            // fill the in-weapon mag with HP. chamber is already present from
            // the preset tree (patron_in_weapon entry).
            LoadMagazineInSlot(weapon, "mod_magazine", AmmoTpl, inv, factory);

            EquipmentSlot targetSlot = ResolveTargetSlot(gw.MainPlayer, inv);
            Plugin.LogSource?.LogInfo($"[RotWallbuy] target slot: {targetSlot}");

            Slot equipSlot = inv.Inventory.Equipment.GetSlot(targetSlot);
            if (equipSlot.ContainedItem != null)
            {
                Plugin.LogSource?.LogInfo($"[RotWallbuy] {targetSlot} occupied by {equipSlot.ContainedItem.TemplateId}; replacing.");
                DiscardItem(equipSlot.ContainedItem, inv);
                WallbuyAmmoTracker.DiscardForSlot(targetSlot, inv);
            }

            // dispense the 5 spare loaded mags BEFORE the weapon swap.
            List<Item> givenMags = await AddLoadedMagStacks(inv, factory, RotMagTpl, AmmoTpl, MagCount);
            WallbuyAmmoTracker.Register(targetSlot, givenMags);

            // pre-load the weapon's held-prefab bundle BEFORE SwitchToWeapon
            // (see WallbuyBundleLoader.EnsureItemBundleLoaded for why).
            await WallbuyBundleLoader.EnsureItemBundleLoaded(weapon);

            equipSlot.ChangeContainedItemDirectly(weapon);
            InventoryEventHelpers.RaiseSlotChange(equipSlot, weapon);
            Plugin.LogSource?.LogInfo($"[RotWallbuy] Rot equipped in {targetSlot}.");

            SwitchToWeapon(gw.MainPlayer, weapon);
        }

        // Max Ammo power-up entry: if the player owns the Rot AR, run the same
        // restock branch the wallbuy uses on re-buy (without spending TC).
        // no-op if the player doesn't have one.
        public static System.Threading.Tasks.Task RefillForMaxAmmoPickup(Player player)
        {
            try
            {
                InventoryController inv = player?.InventoryController;
                if (inv == null) return System.Threading.Tasks.Task.CompletedTask;
                Item weapon = FindFirstByTpl(inv.Inventory.Equipment, RotRootTpl);
                if (weapon == null)
                {
                    Plugin.LogSource?.LogInfo("[MaxAmmo] Rot refill skipped: player doesn't own a Rot AR.");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                if (factory == null) return System.Threading.Tasks.Task.CompletedTask;
                Plugin.LogSource?.LogInfo("[MaxAmmo] Rot refill running...");
                return RestockExisting(weapon, inv, factory);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] Rot RefillForMaxAmmoPickup threw: {ex.Message}");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        private static async Task RestockExisting(Item weapon, InventoryController inv, ItemFactoryClass factory)
        {
            if (weapon is Weapon w)
            {
                RefillChambers(w, AmmoTpl, inv, factory);
                MaxAmmoRestockHelper.RepairWeapon(w);
            }

            CompoundItem compound = weapon as CompoundItem;
            Slot magSlot = compound?.Slots?.FirstOrDefault(s => s != null && s.ID == "mod_magazine");
            Item inWeaponMag = magSlot?.ContainedItem;
            if (inWeaponMag != null) RefillMagazineToCapacity(inWeaponMag, AmmoTpl, inv, factory);

            int spareCount = 0;
            foreach (Item it in inv.Inventory.Equipment.GetAllItemsFromCollection())
            {
                if (it == null) continue;
                if (it.TemplateId != RotMagTpl) continue;
                if (it == inWeaponMag) continue;
                RefillMagazineToCapacity(it, AmmoTpl, inv, factory);
                spareCount++;
            }

            int needed = MagCount - spareCount;
            if (needed > 0)
            {
                List<Item> givenMags = await AddLoadedMagStacks(inv, factory, RotMagTpl, AmmoTpl, needed);
                EquipmentSlot? hostSlot = FindSlotContaining(inv.Inventory.Equipment, weapon);
                if (hostSlot.HasValue) WallbuyAmmoTracker.Register(hostSlot.Value, givenMags);
            }

            Plugin.LogSource?.LogInfo($"[RotWallbuy] restock: chambers refilled, spare mags={spareCount}, dispensed {Math.Max(0, needed)} fresh.");
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

        // topological-sort tree builder. mirrors UmpWallbuyActionPatch.BuildPresetTree.
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
                            Plugin.LogSource?.LogError($"[RotWallbuy] CreateItem({entry.Tpl}) returned null for root.");
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
                        Plugin.LogSource?.LogWarning($"[RotWallbuy] slot '{entry.SlotId}' missing on {parent.TemplateId}; skipping mod {entry.Tpl}.");
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }

                    Item mod = factory.CreateItem(((IIdGenerator)inv).NextId, entry.Tpl, null);
                    if (mod == null)
                    {
                        Plugin.LogSource?.LogWarning($"[RotWallbuy] CreateItem({entry.Tpl}) returned null; skipping.");
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
                Plugin.LogSource?.LogWarning($"[RotWallbuy] {pending.Count} preset entries could not be attached (parents never resolved).");

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
        // in ZombiesLoadoutPatch so Rot mags overflow into the PackNStrap
        // belt when the rig is full. mirrors BAR/STG/UMP/MP-43.
        private static Task<List<Item>> AddLoadedMagStacks(InventoryController inv, ItemFactoryClass factory, string magTpl, string ammoTpl, int count)
        {
            List<Item> added = ZombiesLoadoutPatch.PlaceLoadedMagsAcrossEquipment(
                inv.Inventory.Equipment, magTpl, ammoTpl, count, inv, factory);
            foreach (Item m in added) m.SpawnedInSession = true;
            Plugin.LogSource?.LogInfo($"[RotWallbuy] dispensed {added.Count}/{count} loaded mag(s) to inventory.");
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

            Plugin.LogSource?.LogInfo("[RotWallbuy] both primaries full and player not holding a primary; defaulting to FirstPrimaryWeapon.");
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
                    Plugin.LogSource?.LogWarning($"[RotWallbuy] Discard failed for {item?.TemplateId}: {result.Error}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[RotWallbuy] Discard threw for {item?.TemplateId}: {ex.Message}");
            }
        }

        private static void SwitchToWeapon(Player player, Item weapon)
        {
            try
            {
                Weapon w = weapon as Weapon;
                if (w == null)
                {
                    Plugin.LogSource?.LogWarning("[RotWallbuy] cannot switch to weapon: cast to Weapon failed.");
                    return;
                }
                player.Proceed(w, new Callback<IFirearmHandsController>(result =>
                {
                    if (result.Failed)
                        Plugin.LogSource?.LogWarning($"[RotWallbuy] auto-switch failed: {result.Error}");
                }), scheduled: true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[RotWallbuy] SwitchToWeapon threw: {ex.Message}");
            }
        }
    }
}
