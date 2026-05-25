using System;
using System.Collections.Generic;
using EFT.InventoryLogic;

namespace Manimal.SpeedCola.Patches
{
    // shared registry of mags/ammo stacks dispensed by ANY wallbuy, keyed
    // by the equipment slot the dispensing wallbuy's weapon was placed in.
    // before this, each wallbuy (BAR/STG/UMP/Rot/MP-43) had its OWN
    // _trackedMagsBySlot static; when buying weapon B replaced weapon A in
    // the same slot, only B's tracker was consulted, so A's mags would
    // linger in the player's belt/rig forever. swapping MP-43 -> Rot left
    // a pile of buckshot stacks behind because MP-43's per-class tracker
    // was invisible to Rot's discard pass.
    //
    // centralized contract:
    //   - on successful dispense, the wallbuy calls Register(slot, items)
    //     to record what it gave the player.
    //   - on replacement (any wallbuy taking over `slot`), the wallbuy
    //     calls DiscardForSlot(slot, inv) BEFORE equipping its weapon -
    //     this purges whatever previous occupant dispensed, regardless of
    //     which wallbuy class owned that occupant.
    //
    // Register APPENDS rather than replacing so a re-buy restock path
    // (which dispenses incremental mags to top up) doesn't lose the
    // already-tracked mags from the original buy.
    public static class WallbuyAmmoTracker
    {
        private static readonly Dictionary<EquipmentSlot, List<Item>> _bySlot = new Dictionary<EquipmentSlot, List<Item>>();

        public static void Register(EquipmentSlot slot, IEnumerable<Item> items)
        {
            if (items == null) return;
            if (!_bySlot.TryGetValue(slot, out var list))
            {
                list = new List<Item>();
                _bySlot[slot] = list;
            }
            foreach (Item item in items)
            {
                if (item != null) list.Add(item);
            }
        }

        // DiscardForSlot iterates each tracked item, asks the InventoryController
        // to discard it (no-op if it was already removed by the player), then
        // wipes the bin so subsequent buys start fresh.
        public static int DiscardForSlot(EquipmentSlot slot, InventoryController inv)
        {
            if (!_bySlot.TryGetValue(slot, out List<Item> items)) return 0;
            int cleaned = 0;
            foreach (Item item in items)
            {
                if (item == null) continue;
                // CurrentAddress == null means the item was already consumed,
                // dropped, or moved out by something else. skip silently.
                if (item.CurrentAddress == null) continue;
                try
                {
                    var result = InteractionsHandlerClass.DiscardWithoutRestrictions(item, inv);
                    if (result.Failed)
                        Plugin.LogSource?.LogWarning($"[WallbuyAmmoTracker] discard failed for {item.TemplateId} in {slot}: {result.Error}");
                    else
                        cleaned++;
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogError($"[WallbuyAmmoTracker] discard threw for {item.TemplateId} in {slot}: {ex.Message}");
                }
            }
            _bySlot.Remove(slot);
            Plugin.LogSource?.LogInfo($"[WallbuyAmmoTracker] cleared {cleaned} tracked item(s) for {slot}.");
            return cleaned;
        }

        // called at raid start (from any spawn patch) to drop the tracker
        // before the new raid's wallbuy interactions start registering -
        // otherwise items tracked in the previous raid (whose Item objects
        // no longer exist) would be queried with stale references.
        public static void ResetForNewRaid()
        {
            _bySlot.Clear();
        }
    }
}
