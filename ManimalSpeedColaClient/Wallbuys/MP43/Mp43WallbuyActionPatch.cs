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
    // prefix on GetActionsClass.GetAvailableActions: detect Mp43Wallbuy and
    // inject a "Buy" action. on click:
    //   1. build the MP-43 with barrel/stock/two chamber shells
    //   2. pick a primary slot: empty one if available, else the held weapon's
    //      slot (delete its contents); else default to FirstPrimaryWeapon
    //   3. delete any tracked ammo we had previously given for that slot
    //   4. equip new weapon, auto-switch hands to it
    //   5. drop 2 stacks of buckshot into the player's inventory and track
    //      those item references against the new slot for the next replacement
    internal sealed class Mp43WallbuyActionPatch : ModulePatch
    {
        public const int TarCoinPrice = 500;

        public const string Mp43ItemTpl   = "5580223e4bdc2d1c128b457f"; // MP-43-1C
        public const string Mp43BarrelTpl = "55d447bb4bdc2d892f8b456f"; // 725mm
        public const string Mp43StockTpl  = "611a31ce5b7ffe001b4649d1"; // buttpad
        public const string BuckshotTpl   = "5d6e67fba4b9361bc73bc779"; // 12/70 65 buckshot
        private const int    BuckshotStackMax = 20; // per items.json
        private const int    AmmoStacksOnBuy  = 2;

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
            Mp43Wallbuy wallbuy = interactive as Mp43Wallbuy;
            if (wallbuy == null) return true;

            try { __result = BuildActions(owner, wallbuy); }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Wallbuy] action build failed: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, Mp43Wallbuy wallbuy)
        {
            ActionsReturnClass result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            if (owner?.Player == null) return result;

            // restock mode = player already owns MP-43; dispense flow refills
            // chambers/ammo stacks instead of giving a dupe. half price.
            InventoryController inv = owner.Player.InventoryController;
            bool isRefill = inv != null && FindFirstByTpl(inv.Inventory.Equipment, Mp43ItemTpl) != null;
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

        private static void OnBuy(Mp43Wallbuy wallbuy, Player player, int price)
        {
            try
            {
                if (!TarCoinWallet.TrySpend(player, price))
                {
                    Plugin.LogSource?.LogInfo("[Wallbuy] not enough TarCoins; buy aborted.");
                    return;
                }
                wallbuy.PlayBuySound();
                wallbuy.PlayBuyAnimation();
                _ = DispenseAsync();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Wallbuy] buy failed: {ex.Message}");
            }
        }

        private static async Task DispenseAsync()
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer == null) return;
            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            InventoryController inv = gw.MainPlayer.InventoryController;
            if (factory == null || inv == null)
            {
                Plugin.LogSource?.LogWarning("[Wallbuy] ItemFactory or InventoryController missing.");
                return;
            }

            // restock mode: if the player already owns an MP-43, just refill
            // its chambers and top up buckshot stacks instead of spawning a
            // duplicate weapon. avoids stacking up weapons every re-buy.
            Item existing = FindFirstByTpl(inv.Inventory.Equipment, Mp43ItemTpl);
            if (existing != null)
            {
                await RestockExisting(existing, inv, factory);
                return;
            }

            // 1. build the weapon with pre-loaded chamber shells.
            Item weapon = BuildWallbuyWeapon(inv, factory);
            if (weapon == null) return;

            // 2. resolve where the weapon will land. empty primary wins; else
            //    held weapon's primary slot; else FirstPrimary fallback.
            EquipmentSlot targetSlot = ResolveTargetSlot(gw.MainPlayer, inv);
            Plugin.LogSource?.LogInfo($"[Wallbuy] target slot: {targetSlot}");

            // 3. if a weapon is already in that slot, clear it and its ammo.
            Slot equipSlot = inv.Inventory.Equipment.GetSlot(targetSlot);
            if (equipSlot.ContainedItem != null)
            {
                Plugin.LogSource?.LogInfo($"[Wallbuy] {targetSlot} occupied by {equipSlot.ContainedItem.TemplateId}; replacing.");
                DiscardItem(equipSlot.ContainedItem, inv);
                WallbuyAmmoTracker.DiscardForSlot(targetSlot, inv);
            }

            // 4. drop 2 stacks of buckshot into inventory FIRST. ammo placement
            //    runs a network transaction against the player's inventory -
            //    must happen before we ChangeContainedItemDirectly + Proceed,
            //    because those leave the inventory in a half-updated state that
            //    causes the transaction to fail with "Can not execute".
            List<Item> givenAmmo = await AddAmmoStacks(inv, factory, BuckshotTpl, AmmoStacksOnBuy);
            WallbuyAmmoTracker.Register(targetSlot, givenAmmo);

            // 5. pre-load the weapon's held-prefab bundle BEFORE SwitchToWeapon
            //    (see WallbuyBundleLoader.EnsureItemBundleLoaded for why).
            await WallbuyBundleLoader.EnsureItemBundleLoaded(weapon);

            // 6. equip new weapon directly, then fire the Slot.OnAddOrRemoveItem
            //    event manually so the equipment-bar / quickslot HUD refreshes
            //    immediately instead of waiting for the player to open inventory.
            equipSlot.ChangeContainedItemDirectly(weapon);
            InventoryEventHelpers.RaiseSlotChange(equipSlot, weapon);
            Plugin.LogSource?.LogInfo($"[Wallbuy] MP-43 equipped in {targetSlot}.");

            // 6. auto-switch hands to the new weapon.
            SwitchToWeapon(gw.MainPlayer, weapon);
        }

        // builds the MP-43-1C tree: root + barrel + stock + two chamber shells.
        // barrel/stock are regular Slots; the two chamber shells live on
        // Weapon.Chambers (a separate Slot[] from CompoundItem.Slots) - looking
        // them up via Slots misses, hence the LoadChamber helper.
        private static Item BuildWallbuyWeapon(InventoryController inv, ItemFactoryClass factory)
        {
            Item weapon = factory.CreateItem(((IIdGenerator)inv).NextId, Mp43ItemTpl, null);
            if (weapon == null)
            {
                Plugin.LogSource?.LogWarning($"[Wallbuy] CreateItem({Mp43ItemTpl}) returned null.");
                return null;
            }
            weapon.SpawnedInSession = true;

            AttachMod(weapon, "mod_barrel", Mp43BarrelTpl, factory, inv);
            AttachMod(weapon, "mod_stock",  Mp43StockTpl,  factory, inv);
            LoadChamber(weapon, "patron_in_weapon_000", BuckshotTpl, factory, inv);
            LoadChamber(weapon, "patron_in_weapon_001", BuckshotTpl, factory, inv);

            return weapon;
        }

        // resolves a chamber slot via Weapon.Chambers and snaps in a single
        // round. mirrors AttachMod but targets the weapon's Chambers array.
        private static void LoadChamber(Item weapon, string slotId, string ammoTpl, ItemFactoryClass factory, InventoryController inv)
        {
            try
            {
                Weapon w = weapon as Weapon;
                if (w?.Chambers == null)
                {
                    Plugin.LogSource?.LogWarning($"[Wallbuy] {weapon?.TemplateId} has no Chambers; cannot load {slotId}.");
                    return;
                }
                Slot slot = w.Chambers.FirstOrDefault(s => s != null && s.ID == slotId);
                if (slot == null)
                {
                    Plugin.LogSource?.LogWarning($"[Wallbuy] chamber '{slotId}' not found on {weapon.TemplateId}.");
                    return;
                }
                Item round = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
                if (round == null)
                {
                    Plugin.LogSource?.LogWarning($"[Wallbuy] CreateItem({ammoTpl}) returned null.");
                    return;
                }
                slot.ChangeContainedItemDirectly(round);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Wallbuy] LoadChamber({slotId}) threw: {ex.Message}");
            }
        }

        private static EquipmentSlot ResolveTargetSlot(Player player, InventoryController inv)
        {
            InventoryEquipment equipment = inv.Inventory.Equipment;
            bool firstEmpty  = equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon)?.ContainedItem == null;
            bool secondEmpty = equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon)?.ContainedItem == null;

            if (firstEmpty)  return EquipmentSlot.FirstPrimaryWeapon;
            if (secondEmpty) return EquipmentSlot.SecondPrimaryWeapon;

            // both primaries full: replace whichever the player is currently
            // holding out. if they're holding a pistol/melee/nothing, default
            // to FirstPrimary replacement (arbitrary but predictable).
            EquipmentSlot? heldSlot = GetHeldWeaponSlot(player, equipment);
            if (heldSlot == EquipmentSlot.FirstPrimaryWeapon ||
                heldSlot == EquipmentSlot.SecondPrimaryWeapon)
                return heldSlot.Value;

            Plugin.LogSource?.LogInfo("[Wallbuy] both primaries full and player not holding a primary; defaulting to FirstPrimaryWeapon replacement.");
            return EquipmentSlot.FirstPrimaryWeapon;
        }

        // returns the equipment slot the player's currently-held weapon sits in,
        // or null if they're holding nothing / something we don't recognize.
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
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Wallbuy] GetHeldWeaponSlot threw: {ex.Message}");
            }
            return null;
        }

        private static void DiscardItem(Item item, InventoryController inv)
        {
            try
            {
                var result = InteractionsHandlerClass.DiscardWithoutRestrictions(item, inv);
                if (result.Failed)
                    Plugin.LogSource?.LogWarning($"[Wallbuy] Discard failed for {item?.TemplateId}: {result.Error}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Wallbuy] Discard threw for {item?.TemplateId}: {ex.Message}");
            }
        }

        // initiates Player.Proceed(weapon, ...) so the new weapon is brought
        // into hands. same call path the vanilla menu uses when the player
        // hits a weapon's "Use" / hotkey. callback just logs failures.
        private static void SwitchToWeapon(Player player, Item weapon)
        {
            try
            {
                Weapon w = weapon as Weapon;
                if (w == null)
                {
                    Plugin.LogSource?.LogWarning("[Wallbuy] cannot switch to weapon: cast to Weapon failed.");
                    return;
                }
                player.Proceed(w, new Callback<IFirearmHandsController>(result =>
                {
                    if (result.Failed)
                        Plugin.LogSource?.LogWarning($"[Wallbuy] auto-switch failed: {result.Error}");
                }), scheduled: true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[Wallbuy] SwitchToWeapon threw: {ex.Message}");
            }
        }

        // delegates to the shared rig -> pockets -> belt -> backpack cascade
        // (TryPlaceItemAcrossEquipment) so buckshot stacks overflow into the
        // PackNStrap belt when the rig is full. mirrors BAR/STG/UMP/Rot.
        //
        // synchronous internally but kept Task-returning so the call sites
        // don't have to change shape.
        private static Task<List<Item>> AddAmmoStacks(InventoryController inv, ItemFactoryClass factory, string ammoTpl, int stackCount)
        {
            List<Item> added = new List<Item>();
            for (int i = 0; i < stackCount; i++)
            {
                Item ammo = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
                if (ammo == null) continue;
                ammo.StackObjectsCount = BuckshotStackMax;
                ammo.SpawnedInSession  = true;

                if (ZombiesLoadoutPatch.TryPlaceItemAcrossEquipment(inv.Inventory.Equipment, ammo))
                    added.Add(ammo);
                else
                    break; // every container full; stop trying to dispense further stacks
            }
            Plugin.LogSource?.LogInfo($"[Wallbuy] added {added.Count}/{stackCount} ammo stacks to inventory.");
            return Task.FromResult(added);
        }

        // Max Ammo power-up entry: if the player owns an MP-43, run the same
        // restock branch the wallbuy uses on re-buy (without spending TC).
        // no-op if the player doesn't have an MP-43.
        public static System.Threading.Tasks.Task RefillForMaxAmmoPickup(Player player)
        {
            try
            {
                InventoryController inv = player?.InventoryController;
                if (inv == null)
                {
                    Plugin.LogSource?.LogInfo("[MaxAmmo] Mp43 refill skipped: no InventoryController.");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
                Item weapon = FindFirstByTpl(inv.Inventory.Equipment, Mp43ItemTpl);
                if (weapon == null)
                {
                    Plugin.LogSource?.LogInfo("[MaxAmmo] Mp43 refill skipped: player doesn't own an MP-43.");
                    return System.Threading.Tasks.Task.CompletedTask;
                }
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
                if (factory == null) return System.Threading.Tasks.Task.CompletedTask;
                Plugin.LogSource?.LogInfo("[MaxAmmo] Mp43 refill running...");
                return RestockExisting(weapon, inv, factory);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] Mp43 RefillForMaxAmmoPickup threw: {ex.Message}");
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        // restock path: weapon already owned, just refill ammo. chambers
        // re-seeded, existing buckshot stacks bumped to max stack size, and
        // enough new stacks dispensed to bring total back up to AmmoStacksOnBuy.
        private static async Task RestockExisting(Item weapon, InventoryController inv, ItemFactoryClass factory)
        {
            if (weapon is Weapon w)
            {
                RefillChambers(w, BuckshotTpl, inv, factory);
                MaxAmmoRestockHelper.RepairWeapon(w);
            }

            int existingStacks = 0;
            foreach (Item it in inv.Inventory.Equipment.GetAllItemsFromCollection())
            {
                if (it == null || it.TemplateId != BuckshotTpl) continue;
                // skip chamber rounds (they live on Weapon.Chambers and have
                // StackObjectsCount=1; refilling them via stack count would be
                // a no-op anyway, but let's not count them as inventory stacks).
                if (IsChamberRound(it)) continue;
                it.StackObjectsCount = BuckshotStackMax;
                existingStacks++;
            }

            int needed = AmmoStacksOnBuy - existingStacks;
            if (needed > 0)
            {
                List<Item> given = await AddAmmoStacks(inv, factory, BuckshotTpl, needed);
                EquipmentSlot? host = FindSlotContaining(inv.Inventory.Equipment, weapon);
                if (host.HasValue) WallbuyAmmoTracker.Register(host.Value, given);
            }

            Plugin.LogSource?.LogInfo($"[Wallbuy] restock: chambers refilled, existing stacks={existingStacks}, dispensed {Math.Max(0, needed)} fresh.");
        }

        private static bool IsChamberRound(Item ammo)
        {
            // chamber rounds live in a Weapon.Chambers Slot whose ID is
            // "patron_in_weapon_NNN". their immediate ItemAddress.Container
            // is that slot. spare buckshot stacks in inventory have a
            // StashGridClass container (a container grid) instead, so this
            // distinguishes the two without walking up.
            try
            {
                ItemAddress addr = ammo?.Parent;
                if (addr?.Container is Slot slot && slot.ID != null && slot.ID.StartsWith("patron_in_weapon"))
                    return true;
            }
            catch { /* parent may be null on freshly-created items - ignore */ }
            return false;
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

        // creates a mod item and snaps it into the parent's named slot. uses
        // Slot.ChangeContainedItemDirectly which sets ContainedItem +
        // CurrentAddress in one step (no filter validation, no events).
        private static void AttachMod(Item parent, string slotId, string modTpl, ItemFactoryClass factory, InventoryController inv)
        {
            try
            {
                CompoundItem compound = parent as CompoundItem;
                if (compound?.Slots == null) return;
                Slot slot = compound.Slots.FirstOrDefault(s => s.ID == slotId);
                if (slot == null)
                {
                    Plugin.LogSource?.LogWarning($"[Wallbuy] slot '{slotId}' not found on {parent.TemplateId}");
                    return;
                }
                Item mod = factory.CreateItem(((IIdGenerator)inv).NextId, modTpl, null);
                if (mod == null)
                {
                    Plugin.LogSource?.LogWarning($"[Wallbuy] factory.CreateItem({modTpl}) returned null");
                    return;
                }
                slot.ChangeContainedItemDirectly(mod);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[Wallbuy] AttachMod({slotId},{modTpl}) threw: {ex.Message}");
            }
        }
    }
}
