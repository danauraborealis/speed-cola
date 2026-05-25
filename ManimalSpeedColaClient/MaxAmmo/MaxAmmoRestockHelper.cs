using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Manimal.SpeedCola.Patches;

namespace Manimal.SpeedCola
{
    // applies the "Max Ammo" power-up to a player. two effects:
    //
    //   1. RESTOCK: every loaded magazine (whether seated in a weapon or
    //      stashed in the rig/backpack) gets its Cartridges topped to
    //      MaxCount by bumping the last cartridge stack's StackObjectsCount
    //      by the deficit. every loose ammo stack (AmmoBox, loose rounds)
    //      gets StackObjectsCount = StackMaxSize. every empty weapon chamber
    //      gets a round if a loaded mag is in the weapon.
    //
    //   2. REPAIR: every weapon in the player's equipment slots
    //      (FirstPrimary/SecondPrimary/Holster) has its RepairableComponent
    //      Durability set to MaxDurability.
    //
    // direct field mutation is intentional - StackObjectsCount and Durability
    // are public fields the UI re-reads on the next interaction. no network
    // transaction needed for a local single-player change.
    public static class MaxAmmoRestockHelper
    {
        // equipment slots to scan for weapons. melee slot (Scabbard) skipped
        // because it doesn't hold a firearm; armbands / containers irrelevant.
        private static readonly EquipmentSlot[] WeaponSlots =
        {
            EquipmentSlot.FirstPrimaryWeapon,
            EquipmentSlot.SecondPrimaryWeapon,
            EquipmentSlot.Holster,
        };

        public static void Apply(Player player)
        {
            if (player == null) return;
            try
            {
                InventoryController inv = player.InventoryController;
                if (inv == null)
                {
                    Plugin.LogSource?.LogWarning("[MaxAmmo] Apply: no InventoryController.");
                    return;
                }

                int mags = 0, stacks = 0, chambers = 0, repaired = 0;
                ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;

                // 1. iterate every item the player has in equipment + sub-grids.
                //    GetAllItemsFromCollection recurses through every CompoundItem's
                //    Slots and Grids, so we see magazines inside weapons AND
                //    spare mags in the rig, plus every loose ammo stack.
                foreach (Item item in inv.Inventory.Equipment.GetAllItemsFromCollection())
                {
                    if (item == null) continue;

                    // magazine: top its Cartridges up to MaxCount.
                    if (item is MagazineItemClass mag)
                    {
                        if (RefillMagazine(mag)) mags++;
                        continue;
                    }

                    // AmmoBox (loadable box like 30rd 5.45 etc): top to MaxCount.
                    if (item is AmmoBox box)
                    {
                        if (RefillAmmoBox(box, factory, inv)) stacks++;
                        continue;
                    }

                    // loose ammo stack: bump StackObjectsCount to StackMaxSize.
                    if (item is AmmoItemClass ammo)
                    {
                        // skip cartridges that live inside a Magazine.Cartridges
                        // StackSlot - those are managed by the magazine refill
                        // above. mutating them here would double-count.
                        if (IsCartridgeInsideMag(ammo)) continue;
                        int max = ammo.StackMaxSize;
                        if (max > 0 && ammo.StackObjectsCount < max)
                        {
                            ammo.StackObjectsCount = max;
                            ammo.RaiseRefreshEvent(false, true);
                            stacks++;
                        }
                        continue;
                    }

                    // weapon: repair durability + refill chambers if possible.
                    if (item is Weapon w)
                    {
                        if (RepairWeapon(w)) repaired++;
                        chambers += RefillChambersIfPossible(w, factory, inv);
                        continue;
                    }
                }

                // 1911 spare-mag top-up: the 1911 isn't a wallbuy, so when the
                // player burns through the 6 spare mags spawned via
                // ZombiesLoadoutPatch.ApplyLoadout there's nothing left for the
                // mag-refill pass above to touch. dispense fresh loaded mags
                // back up to the spawn-time count.
                ZombiesLoadoutPatch.TopUp1911SpareMags(inv, inv.Inventory.Equipment, factory);

                // wallbuy-weapon top-ups: each patch's "restock on re-buy" branch
                // is what the user invokes from the wallbuy by paying TC. Max
                // Ammo runs the same path for every wallbuy weapon the player
                // owns - no-op for any not present. fire-and-forget; each is an
                // async network transaction and we don't want to serialize them.
                Plugin.LogSource?.LogInfo("[MaxAmmo] firing wallbuy refills (MP-43, Rot, UMP, STG, BAR)...");
                _ = Mp43WallbuyActionPatch.RefillForMaxAmmoPickup(player);
                _ = RotWallbuyActionPatch.RefillForMaxAmmoPickup(player);
                _ = UmpWallbuyActionPatch.RefillForMaxAmmoPickup(player);
                _ = StgWallbuyActionPatch.RefillForMaxAmmoPickup(player);
                _ = BarWallbuyActionPatch.RefillForMaxAmmoPickup(player);

                // boss-weapon top-ups: any weapon dispensed via supply drop
                // is registered in BossWeaponRegistry with its spawn-time
                // mag tpl + ammo tpl + target spare count. iterate and top
                // each one up. no-op for tpls the player no longer owns.
                TopUpBossWeapons(player, inv, factory);

                // schedule a delayed re-search sweep. the wallbuy refills
                // above are async (fire-and-forget) - they dispense fresh
                // mags via network transactions that complete on later
                // frames. when a fresh item lands in a previously-searched
                // rig, EFT silently marks the rig as needing re-search.
                // running ReSearchAllContainers immediately wouldn't catch
                // those late landings, so we run it once now and again
                // after a short delay to catch the async completions.
                ReSearchAllContainers(player);
                Plugin.Instance?.StartCoroutine(DelayedReSearch(player, 1.0f));

                Plugin.LogSource?.LogInfo(
                    $"[MaxAmmo] restock complete: mags={mags}, stacks={stacks}, chambers={chambers}, weapons_repaired={repaired}.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[MaxAmmo] Apply threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // top a magazine's Cartridges by bumping the last cartridge stack's
        // StackObjectsCount by (MaxCount - Count). assumes EFT's Count derived
        // property re-sums the items on read. returns true if anything changed.
        //
        // RaiseRefreshEvent on the mag itself triggers the inventory/HUD
        // listeners that drive the in-game ammo counter. without it the
        // visible count stays stale until the player reloads or opens the
        // inventory - which is exactly the "didn't seem to work" symptom.
        private static bool RefillMagazine(MagazineItemClass mag)
        {
            try
            {
                if (mag?.Cartridges == null) return false;
                int max = mag.MaxCount;
                int cur = mag.Count;
                if (max <= 0 || cur >= max) return false;
                int deficit = max - cur;

                Item last = mag.Cartridges.Last;
                if (last == null) return false; // empty mag: leave it (no ammo type to choose)
                last.StackObjectsCount += deficit;
                last.RaiseRefreshEvent(false, true);
                mag.RaiseRefreshEvent(false, true);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] RefillMagazine threw: {ex.Message}");
                return false;
            }
        }

        // AmmoBox is a partial-stack ammo container (e.g. cardboard 30rd boxes).
        // shares the same StackSlot.Cartridges structure as MagazineItemClass.
        private static bool RefillAmmoBox(AmmoBox box, ItemFactoryClass factory, InventoryController inv)
        {
            try
            {
                if (box?.Cartridges == null) return false;
                int max = box.MaxCount;
                int cur = box.Count;
                if (max <= 0 || cur >= max) return false;

                Item last = box.Cartridges.Last;
                if (last != null)
                {
                    last.StackObjectsCount += (max - cur);
                    last.RaiseRefreshEvent(false, true);
                    box.RaiseRefreshEvent(false, true);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] RefillAmmoBox threw: {ex.Message}");
                return false;
            }
        }

        // true if this ammo item's parent container is a magazine's Cartridges
        // StackSlot. needed because GetAllItemsFromCollection enumerates those
        // too and we don't want to bump them as if they were inventory stacks.
        private static bool IsCartridgeInsideMag(AmmoItemClass ammo)
        {
            try
            {
                ItemAddress addr = ammo?.Parent;
                if (addr?.Container is StackSlot ss && ss.ParentItem is MagazineItemClass) return true;
                if (addr?.Container is StackSlot ss2 && ss2.ParentItem is AmmoBox) return true;
                // chamber rounds (Weapon.Chambers[i] is a Slot, not a StackSlot)
                if (addr?.Container is Slot slot && slot.ID != null && slot.ID.StartsWith("patron_in_weapon"))
                    return true;
            }
            catch { /* freshly-created items may have null parent */ }
            return false;
        }

        // exposed internal so wallbuy "BUY AMMO" restock branches can call
        // it too - hitting a wallbuy for refill now also repairs durability.
        internal static bool RepairWeapon(Weapon w)
        {
            try
            {
                RepairableComponent r = w.GetItemComponent<RepairableComponent>();
                if (r == null)
                {
                    Plugin.LogSource?.LogInfo($"[MaxAmmo] repair: '{w.TemplateId}' has NO RepairableComponent; skipping.");
                    return false;
                }
                float before = r.Durability;
                if (r.Durability >= r.MaxDurability)
                {
                    Plugin.LogSource?.LogInfo($"[MaxAmmo] repair: '{w.TemplateId}' already at max ({before:F1}/{r.MaxDurability:F1}); skipping.");
                    return false;
                }
                r.Durability = r.MaxDurability;
                w.RaiseRefreshEvent(false, true);
                Plugin.LogSource?.LogInfo($"[MaxAmmo] repair: '{w.TemplateId}' {before:F1} -> {r.MaxDurability:F1}.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] RepairWeapon threw: {ex.Message}");
                return false;
            }
        }

        // walks the player's equipment and (a) marks every item as KNOWN
        // (clears the "?" overlay), (b) re-marks every searchable container
        // (rig, backpack, pockets, secured container) as SEARCHED (clears
        // the "search me" prompt). previously only did (b) - that's
        // necessary but not sufficient. EFT shows the search prompt on a
        // container if it has unknown items inside, so we have to mark
        // every individual item as known too.
        private static void ReSearchAllContainers(Player player)
        {
            try
            {
                InventoryController inv = player?.InventoryController;
                if (inv == null) return;
                IPlayerSearchController sc = player?.SearchController;
                int known = 0, searched = 0;
                foreach (Item it in inv.Inventory.Equipment.GetAllItemsFromCollection())
                {
                    if (it == null) continue;
                    try { sc?.SetItemAsKnown(it, true); known++; } catch { }
                    if (it is SearchableItemItemClass sit)
                    {
                        try { sit.SetItemInfo(null); } catch { }
                        try { sc?.SetItemAsSearched(sit); searched++; } catch { }
                    }
                }
                Plugin.LogSource?.LogInfo($"[MaxAmmo] re-searched {searched} container(s) + marked {known} item(s) as known.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] ReSearchAllContainers threw: {ex.Message}");
            }
        }

        // coroutine wrapper so we can run ReSearchAllContainers again after
        // the async wallbuy refill dispenses have a chance to land. EFT's
        // network transactions complete on subsequent frames - the immediate
        // sweep right after Apply() won't catch them.
        private static System.Collections.IEnumerator DelayedReSearch(Player player, float delaySec)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(delaySec);
            ReSearchAllContainers(player);
        }

        // for each empty chamber: if the weapon has a loaded magazine with a
        // cartridge of some type, mint a fresh round of that tpl and seat it.
        // returns count of chambers filled.
        private static int RefillChambersIfPossible(Weapon w, ItemFactoryClass factory, InventoryController inv)
        {
            if (w?.Chambers == null || factory == null || inv == null) return 0;
            try
            {
                // pull the ammo type out of the loaded mag's last cartridge if any.
                string ammoTpl = null;
                MagazineItemClass mag = w.GetCurrentMagazine();
                if (mag?.Cartridges?.Last != null) ammoTpl = mag.Cartridges.Last.TemplateId;
                if (string.IsNullOrEmpty(ammoTpl)) return 0; // no mag / no ammo: skip

                int filled = 0;
                for (int i = 0; i < w.Chambers.Length; i++)
                {
                    Slot chamber = w.Chambers[i];
                    if (chamber == null) continue;
                    if (chamber.ContainedItem != null) continue;
                    Item round = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
                    if (round == null) continue;
                    chamber.ChangeContainedItemDirectly(round);
                    filled++;
                }
                if (filled > 0) w.RaiseRefreshEvent(false, true);
                return filled;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] RefillChambersIfPossible threw: {ex.Message}");
                return 0;
            }
        }

        // iterates BossWeaponRegistry and, for each entry, if the player
        // currently owns the registered weapon tpl, dispenses fresh loaded
        // mags of the registered mag tpl + ammo tpl to bring the spare-mag
        // count back up to the spawn-time target.
        //
        // count logic mirrors ZombiesLoadoutPatch.TopUp1911SpareMags:
        // excludes the in-weapon mag (parent container is a Slot with
        // ID "mod_magazine") from the count.
        private static void TopUpBossWeapons(Player player, InventoryController inv, ItemFactoryClass factory)
        {
            try
            {
                if (player == null || inv == null || factory == null) return;
                InventoryEquipment equipment = inv.Inventory.Equipment;
                if (equipment == null) return;

                // diagnostic: dump registry state so the user can see whether
                // any boss weapons are registered when Max Ammo fires.
                int registrySize = 0;
                foreach (var _ in BossWeaponRegistry.All) registrySize++;
                Plugin.LogSource?.LogInfo($"[MaxAmmo] BossWeaponRegistry has {registrySize} entries.");
                if (registrySize == 0) return;

                foreach (KeyValuePair<string, BossWeaponRegistry.Entry> kv in BossWeaponRegistry.All)
                {
                    string weaponTpl = kv.Key;
                    BossWeaponRegistry.Entry e = kv.Value;
                    if (string.IsNullOrEmpty(e.MagTpl) || string.IsNullOrEmpty(e.AmmoTpl))
                    {
                        Plugin.LogSource?.LogWarning($"[MaxAmmo] boss registry entry '{weaponTpl}' has empty mag/ammo tpl; skipping.");
                        continue;
                    }

                    bool hasWeapon = false;
                    int existingSpareMags = 0;
                    foreach (Item it in equipment.GetAllItemsFromCollection())
                    {
                        if (it == null) continue;
                        if (it.TemplateId == weaponTpl) hasWeapon = true;
                        else if (it.TemplateId == e.MagTpl)
                        {
                            // skip in-weapon mag - we only count spares.
                            ItemAddress addr = it.Parent;
                            if (addr?.Container is Slot slot && slot.ID == "mod_magazine") continue;
                            existingSpareMags++;
                        }
                    }
                    if (!hasWeapon)
                    {
                        Plugin.LogSource?.LogInfo($"[MaxAmmo] boss weapon '{weaponTpl}' registered but not in player inventory; skipping top-up.");
                        continue;
                    }

                    int deficit = e.TargetMagCount - existingSpareMags;
                    if (deficit <= 0)
                    {
                        Plugin.LogSource?.LogInfo($"[MaxAmmo] boss weapon '{weaponTpl}' spare mags at {existingSpareMags}/{e.TargetMagCount}; no top-up.");
                        continue;
                    }

                    Plugin.LogSource?.LogInfo($"[MaxAmmo] boss weapon '{weaponTpl}' topping up spare mags: existing={existingSpareMags}, dispensing={deficit}.");
                    DispenseBossWeaponMags(inv, factory, e.MagTpl, e.AmmoTpl, deficit);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[MaxAmmo] TopUpBossWeapons threw: {ex.Message}");
            }
        }

        // delegates to the shared rig -> pockets -> belt -> backpack cascade
        // in ZombiesLoadoutPatch so boss-weapon mag top-ups overflow into
        // the PackNStrap belt when the rig is full. mirrors the wallbuy
        // refactor (BAR / STG / UMP / Rot / MP-43).
        private static void DispenseBossWeaponMags(InventoryController inv, ItemFactoryClass factory, string magTpl, string ammoTpl, int count)
        {
            List<Item> placed = Patches.ZombiesLoadoutPatch.PlaceLoadedMagsAcrossEquipment(
                inv.Inventory.Equipment, magTpl, ammoTpl, count, inv, factory);
            foreach (Item m in placed) m.SpawnedInSession = true;
            Plugin.LogSource?.LogInfo($"[MaxAmmo] boss weapon: dispensed {placed.Count}/{count} loaded mag(s).");
        }
    }
}
