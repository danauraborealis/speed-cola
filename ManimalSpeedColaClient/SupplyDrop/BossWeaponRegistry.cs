using System.Collections.Generic;

namespace Manimal.SpeedCola
{
    // raid-scoped registry: for every boss-style weapon dispensed via supply
    // drop, remembers which magazine tpl + ammo tpl + spawn-time mag count
    // it came with. MaxAmmoRestockHelper iterates this on pickup to top up
    // the spare mag count back to the original.
    //
    // why not store the data on the SupplyDropTag itself: the tag dies with
    // the airdrop GameObject when the crate is recycled (e.g. next supply
    // drop replaces this one). the player may have already taken the weapon
    // by then, so we need the data to outlive the crate.
    //
    // dictionary keyed by weapon tpl: re-rolling the same boss weapon tpl
    // overwrites the previous entry (last roll's mag/ammo wins). re-rolling
    // a DIFFERENT tpl adds a new entry; the old weapon's entry stays in
    // case the player took it before re-rolling.
    public static class BossWeaponRegistry
    {
        public struct Entry
        {
            public string MagTpl;
            public string AmmoTpl;
            public int TargetMagCount;
        }

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>();

        public static void Register(string weaponTpl, string magTpl, string ammoTpl, int targetMagCount)
        {
            if (string.IsNullOrEmpty(weaponTpl)) return;
            _entries[weaponTpl] = new Entry
            {
                MagTpl = magTpl,
                AmmoTpl = ammoTpl,
                TargetMagCount = targetMagCount,
            };
            Plugin.LogSource?.LogInfo(
                $"[BossWeapon] registered '{weaponTpl}' -> mag={magTpl}, ammo={ammoTpl}, targetMags={targetMagCount}.");
        }

        // exposed for MaxAmmoRestockHelper's iterator. enumerable over (tpl, entry).
        public static IEnumerable<KeyValuePair<string, Entry>> All => _entries;

        public static void ClearForNewRaid()
        {
            _entries.Clear();
        }
    }
}
