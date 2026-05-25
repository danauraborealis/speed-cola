using System;
using System.Reflection;
using EFT.InventoryLogic;
using HarmonyLib;

namespace Manimal.SpeedCola
{
    // helpers that manually raise inventory-system events the UI subscribes
    // to. needed because we mutate slots / contained items directly (via
    // ChangeContainedItemDirectly) for speed - those direct mutations skip
    // the normal Slot.method_6 path that fires OnAddOrRemoveItem.
    //
    // without these, the QuickSlotPanel / equipment-bar / health-screen UIs
    // dont know about our changes until they next rebuild themselves (which
    // happens when the player opens inventory and the UI re-syncs).
    public static class InventoryEventHelpers
    {
        private static FieldInfo _slotAddRemoveField;

        // public event Action<Item> OnAddOrRemoveItem - the backing field is
        // private with the same name in compiler-generated event accessors.
        // grab once, cache.
        public static void RaiseSlotChange(Slot slot, Item item)
        {
            if (slot == null) return;
            try
            {
                if (_slotAddRemoveField == null)
                {
                    _slotAddRemoveField = AccessTools.Field(typeof(Slot), "OnAddOrRemoveItem");
                    if (_slotAddRemoveField == null)
                    {
                        Plugin.LogSource?.LogWarning("[InventoryEvents] OnAddOrRemoveItem backing field not found via AccessTools; slot UI wont auto-refresh.");
                        return;
                    }
                }
                Action<Item> handler = _slotAddRemoveField.GetValue(slot) as Action<Item>;
                handler?.Invoke(item);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[InventoryEvents] RaiseSlotChange threw: {ex.Message}");
            }
        }
    }
}
