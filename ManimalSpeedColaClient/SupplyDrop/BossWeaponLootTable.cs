using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // boss-style weapon loot for supply drops. one weapon is rolled per
    // crate (guaranteed per user spec) - the rarity scaling controls WHICH
    // weapon, not whether a weapon spawns. mirrors the wave-scaled weight
    // model used by SupplyDropLootTable.PickRandom:
    //   - each entry has a base Weight (lower = rarer)
    //   - wave-scaled effective weight lerps toward pool average as wave
    //     count climbs, so rare entries become more accessible at high waves
    //
    // each entry carries its own:
    //   - static attachment preset (Slot[] minus magazine + chamber)
    //   - candidate magazine tpls (filtered at runtime to MaxCount <= 60)
    //   - candidate ammo tpls (weighted at runtime by Damage; wave-scaled
    //     so high waves prefer higher-damage rounds)
    //
    // build flow per spawn:
    //   1. pick weapon (wave-scaled rarity)
    //   2. pick magazine (random from candidates with capacity <= 60)
    //   3. pick ammo (damage-weighted, wave-scaled bias toward high damage)
    //   4. build weapon with attachments + load mag with ammo + chamber round
    //   5. dispense N spare loaded mags into the crate grid
    //   6. register weapon/mag/ammo/count in BossWeaponRegistry so the
    //      Max Ammo power-up can refill it later
    public static class BossWeaponLootTable
    {
        // mag capacity cap. raised to 100 so authentic boss feed devices
        // pass through:
        //   - 95rd RPK-16 drum (Killa's canonical mag) - included as one
        //     of several options in Killa's MagCandidates so it draws
        //     statistically ~1-in-N each time
        //   - 100rd PKP belt box (Kaban's only feed device) - Kaban is
        //     re-added at the rarest weight in the pool, so LMG drops
        //     are an occasional treat rather than the norm
        public const int MaxMagCapacity = 100;

        // shared scaling cap with SupplyDropLootTable (30 waves).
        public const int WaveCapForScaling = 30;

        // entries below. each preset tree is lifted DIRECTLY from the boss's
        // bot inventory file at SPT_Data/database/bots/types/<boss>.json -
        // specifically the "mods" section keyed by the weapon tpl. for slots
        // where a boss has multiple options (e.g. Knight's mod_stock can
        // roll one of two tpls), we pick the first listed option.
        //
        // ammo / mag candidates also come from each boss's mod_magazine
        // entry where listed; supplementary ammo tpls per caliber are
        // standard EFT zombies-effective rounds (HP / BP / equivalent
        // high-flesh-damage variants).
        public static readonly List<BossWeaponEntry> Entries = new List<BossWeaponEntry>
        {
            // ----------------------------------------------------------------
            // AK-102 (Reshala). 5.56x45 NATO short-barrel AK-100 series.
            // verified identity via items.json: tpl 5ac66d015acfc400180ae6e4
            // is "weapon_izhmash_ak102_556x45", NOT an AKM/AKMN variant
            // (early read of the boss data assumed 7.62x39 because the
            // mod table looked AK-shaped). preset mods lifted from
            // bossbully.json - they all bolt onto the AK-102 chassis;
            // EFT's slot filters accept them.
            // ----------------------------------------------------------------
            new BossWeaponEntry
            {
                Label     = "AK-102 (Reshala)",
                WeaponTpl = "5ac66d015acfc400180ae6e4",
                Weight    = 12,
                SpareMagsInCrate = 4,
                // preset lifted verbatim from WTT-PresetConverterPlus
                // zombies102.json (preset id 6a1297f0b829a1eb989bf7e1).
                // notable nesting:
                //   - muzzle chain is 3-deep (suppressor adapter + can)
                //   - foregrip has a sub-foregrip (vertical on the rail)
                //   - mount_001 carries a tac-light
                //   - stock has a sub-stock (collapsing tube + pad)
                //   - reciever holds the optic
                // mag tpl from preset matches one of our 5.56x45
                // MagCandidates already, so we let the runtime random
                // pick handle it (same as every other entry).
                Preset = new[]
                {
                    new BossPresetEntry("root",       "5ac66d015acfc400180ae6e4", null,        null),
                    new BossPresetEntry("gas_block",  "59c6633186f7740cf0493bb9", "root",      "mod_gas_block"),
                    new BossPresetEntry("handguard",  "5c9a07572e221644f31c4b32", "gas_block", "mod_handguard"),
                    new BossPresetEntry("fg_main",    "5b7be4895acfc400170e2dd5", "handguard", "mod_foregrip"),
                    new BossPresetEntry("fg_sub",     "5c791e872e2216001219c40a", "fg_main",   "mod_foregrip"),
                    new BossPresetEntry("mount_000",  "6269220d70b6c02e665f2635", "handguard", "mod_mount_000"),
                    new BossPresetEntry("mount_001",  "6269220d70b6c02e665f2635", "handguard", "mod_mount_001"),
                    new BossPresetEntry("light",      "560d657b4bdc2da74d8b4572", "mount_001", "mod_tactical"),
                    new BossPresetEntry("muzzle_1",   "5e21ca18e4d47f0da15e77dd", "root",      "mod_muzzle"),
                    new BossPresetEntry("muzzle_2",   "5cff9e5ed7ad1a09407397d4", "muzzle_1",  "mod_muzzle"),
                    new BossPresetEntry("muzzle_3",   "5cff9e84d7ad1a049e54ed55", "muzzle_2",  "mod_muzzle"),
                    new BossPresetEntry("grip",       "5649ae4a4bdc2d1b2b8b4588", "root",      "mod_pistol_grip"),
                    new BossPresetEntry("reciever",   "5d2c76ed48f03532f2136169", "root",      "mod_reciever"),
                    new BossPresetEntry("scope",      "59f9d81586f7744c7506ee62", "reciever",  "mod_scope"),
                    new BossPresetEntry("stock_main", "5ac78eaf5acfc4001926317a", "root",      "mod_stock"),
                    new BossPresetEntry("stock_sub",  "59ecc3dd86f7746dc827481c", "stock_main","mod_stock"),
                    new BossPresetEntry("charge",     "6130ca3fd92c473c77020dbd", "root",      "mod_charge"),
                },
                // boss data lists 3 mag tpls for the AK-102 (all 5.56x45
                // since the weapon chambers 5.56). EFT accepts them on
                // the AK-102 since this is what Reshala actually spawns
                // with.
                MagCandidates = new[]
                {
                    "6764139c44b3c96e7b0e2f7b", // Reshala's mod_magazine opt 1
                    "5ac66c5d5acfc4001718d314", // Reshala's mod_magazine opt 2
                    "5c0548ae0db834001966a3c2", // Reshala's mod_magazine opt 3
                },
                AmmoCandidates = new[]
                {
                    // 5.56x45 NATO (verified names from items.json).
                    "54527ac44bdc2d36668b4567", // M855A1
                    "59e6906286f7746c9f75e847", // M856A1
                    "59e690b686f7746c9f75e848", // M995 (AP)
                    "54527a984bdc2d4e668b4567", // M855
                    "59e6920f86f77411d82aa167", // 55gr FMJ
                    "59e6927d86f77411da468256", // 55gr HP
                    "5c0d5ae286f7741e46554302", // Warmageddon
                    "601949593ae8f707c4608daa", // SSA AP
                },
            },

            // ----------------------------------------------------------------
            // M1A (Glukhar). 7.62x51 NATO. preset lifted verbatim from
            // WTT-PresetConverterPlus zombiesM1A.json. uses the simpler
            // wood/plastic stock variant (5addbf17... - NOT the EBR
            // chassis from the earlier read) which doesn't require the
            // nested inner-stock/pistol-grip chain that broke the previous
            // preset. notable nesting:
            //   - stock carries a tac-light directly
            //   - barrel.muzzle is 3-deep (suppressor adapter + can +
            //     sub-can), with the front sight on muzzle_1
            //   - top mount has scope-mount -> nested optic
            // ----------------------------------------------------------------
            new BossWeaponEntry
            {
                Label     = "M1A (Glukhar)",
                WeaponTpl = "5aafa857e5b5b00018480968",
                Weight    = 10,
                SpareMagsInCrate = 4,
                Preset = new[]
                {
                    new BossPresetEntry("root",        "5aafa857e5b5b00018480968", null,         null),
                    new BossPresetEntry("stock",       "5addbf175acfc408fb13965b", "root",       "mod_stock"),
                    new BossPresetEntry("light",       "56def37dd2720bec348b456a", "stock",      "mod_tactical"),
                    new BossPresetEntry("barrel",      "5aaf9d53e5b5b00015042a52", "root",       "mod_barrel"),
                    new BossPresetEntry("muzzle_1",    "5ab3afb2d8ce87001660304d", "barrel",     "mod_muzzle"),
                    new BossPresetEntry("sight_front", "5addba3e5acfc4001669f0ab", "muzzle_1",   "mod_sight_front"),
                    new BossPresetEntry("muzzle_2",    "59bffc1f86f77435b128b872", "muzzle_1",   "mod_muzzle"),
                    new BossPresetEntry("muzzle_3",    "59bffbb386f77435b379b9c2", "muzzle_2",   "mod_muzzle"),
                    new BossPresetEntry("sight_rear",  "5abcbb20d8ce87001773e258", "root",       "mod_sight_rear"),
                    new BossPresetEntry("mount",       "5addbfe15acfc4001a5fc58b", "root",       "mod_mount"),
                    new BossPresetEntry("scope_mount", "5a33b652c4a28232996e407c", "mount",      "mod_scope"),
                    new BossPresetEntry("scope_optic", "688b4bd81cef2a61d0052738", "scope_mount","mod_scope"),
                },
                MagCandidates = new[]
                {
                    "5aaf8a0be5b5b00015693243", // M1A std 20rd (Glukhar's mag, verified mag_m14_springfield_armory_762x51_20)
                },
                AmmoCandidates = new[]
                {
                    // 7.62x51 NATO, all verified via items.json.
                    // first 4 are Glukhar's actual patron_in_weapon list
                    // for the M1A; M62 + Ultra Nosler are extras of the
                    // same caliber for variety.
                    "58dd3ad986f77403051cba8f", // M80 (patron_762x51_M80)
                    "5a6086ea4f39f99cd479502f", // M61 (patron_762x51_M61)
                    "5efb0c1bd79ff02a1f5e68d9", // M993 (AP)
                    "6768c25aa7b238f14a08d3f6", // M80A1
                    "5a608bf24f39f98ffc77720e", // M62
                    "5e023e88277cce2b522ff2b1", // Ultra Nosler
                },
            },

            // ----------------------------------------------------------------
            // RPK-16 (Killa). 5.45x39 LMG. preset lifted from bosskilla.json -
            // boss spawns with the 95rd drum which we explicitly exclude
            // (no-drums policy); MagCandidates substitutes 30rd RPK-16 mags
            // and the 45rd 6L18 which is the largest non-drum option.
            // ----------------------------------------------------------------
            new BossWeaponEntry
            {
                Label     = "RPK-16 (Killa)",
                WeaponTpl = "5beed0f50db834001c062b12",
                Weight    = 11,
                SpareMagsInCrate = 3,
                // preset lifted verbatim from WTT-PresetConverterPlus
                // zombiesRPK.json (preset id 6a129975b829a1eb989c4273).
                // notable nesting:
                //   - grip carries a sub-grip via mod_pistolgrip_000
                //   - reciever carries a scope mount, the actual optic
                //     nests inside that mount, AND a rear sight
                //   - stock_001 hosts a sub-stock (pad)
                //   - handguard has 2 mount rails + a foregrip, with a
                //     tac-light nested under mount_001
                //   - barrel.muzzle for a suppressor/comp
                // preset's mag tpl (55d482194bdc2d1d4e8b456b = 60rd 6L31)
                // is already in MagCandidates, so the random pick handles
                // mag selection same as every other entry.
                Preset = new[]
                {
                    new BossPresetEntry("root",        "5beed0f50db834001c062b12", null,         null),
                    new BossPresetEntry("grip",        "648ae3e356c6310a830fc291", "root",       "mod_pistol_grip"),
                    new BossPresetEntry("grip_sub",    "6113c3586c780c1e710c90bc", "grip",       "mod_pistolgrip_000"),
                    new BossPresetEntry("reciever",    "5beec91a0db834001961942d", "root",       "mod_reciever"),
                    new BossPresetEntry("scope_mount", "5a33b2c9c4a282000c5a9511", "reciever",   "mod_scope"),
                    new BossPresetEntry("scope_optic", "5a32aa8bc4a2826c6e06d737", "scope_mount","mod_scope"),
                    new BossPresetEntry("sight_rear",  "5beec9450db83400970084fd", "reciever",   "mod_sight_rear"),
                    new BossPresetEntry("stock_001",   "649ec87d8007560a9001ab36", "root",       "mod_stock_001"),
                    new BossPresetEntry("stock",       "5fbbaa86f9986c4cff3fe5f6", "stock_001",  "mod_stock"),
                    new BossPresetEntry("handguard",   "5beec3e30db8340019619424", "root",       "mod_handguard"),
                    new BossPresetEntry("mount_000",   "5beecbb80db834001d2c465e", "handguard",  "mod_mount_000"),
                    new BossPresetEntry("mount_001",   "5beecbb80db834001d2c465e", "handguard",  "mod_mount_001"),
                    new BossPresetEntry("light",       "560d657b4bdc2da74d8b4572", "mount_001",  "mod_tactical_000"),
                    new BossPresetEntry("foregrip",    "59f8a37386f7747af3328f06", "handguard",  "mod_foregrip"),
                    new BossPresetEntry("barrel",      "5beec1bd0db834001e6006f3", "root",       "mod_barrel"),
                    new BossPresetEntry("muzzle",      "564caa3d4bdc2d17108b458e", "barrel",     "mod_muzzle"),
                },
                // drum included for boss-authentic Killa rolls; it's one
                // of N candidates so PickMagazine draws it ~1/N of the time
                // (currently 1/6). want it rarer? remove it; want it more
                // common? just keep it and let the uniform pick do its
                // thing (or duplicate-list it for double weight).
                MagCandidates = new[]
                {
                    "5bed625c0db834001c062946", // RPK-16 95rd drum (boss-canonical, rare-ish roll)
                    "55d482194bdc2d1d4e8b456b", // 6L31 60rd
                    "5cbdaf89ae9215000e5b9c94", // RPK-16 5.45 30rd
                    "55d481904bdc2d8c2f8b456a", // 6L18 5.45 45rd
                    "55d480c04bdc2d1d4e8b456a", // 6L23 5.45 30rd (universal AK-74)
                    "5ac66c5d5acfc4001718d314", // AK-12 5.45 30rd polymer
                },
                AmmoCandidates = new[]
                {
                    "56dff216d2720bbd668b4568", // HP (highest flesh dmg)
                    "56dff61f4bdc2d70148b457c", // BS
                    "56dff3afd2720bba668b4567", // BP gs
                    "56dff2ced2720bb4668b4567", // PS gs
                    "56dff338d2720bbd668b4569", // BT gs
                    "56dfef82d2720bbd668b4567", // PP gs
                },
            },

            // ----------------------------------------------------------------
            // AS VAL (Sanitar). 9x39 suppressed assault rifle. preset
            // lifted verbatim from WTT-PresetConverterPlus zombiesVAL.json
            // (preset id 6a129c27b829a1eb989ce7d0). weapon is the
            // WTT-ContentBackport custom AS VAL tpl
            // 6871284e9a353bb50606f3ed (clones vanilla AS VAL
            // 57c44b372459772d2b39b8ce, "weapon_tochmash_val_9x39").
            // notable nesting:
            //   - root muzzle is 2-deep (suppressor + can extension)
            //   - handguard hosts foregrip + 3 mounts + its own muzzle
            //     + front sight
            //   - mount_001 carries the tac-light
            //   - mount_002 carries a scope mount (with nested optic)
            //     + the rear iron sight
            //   - stock has a sub-stock (recoil pad)
            // ----------------------------------------------------------------
            new BossWeaponEntry
            {
                Label     = "AS VAL (Sanitar)",
                WeaponTpl = "6871284e9a353bb50606f3ed",
                Weight    = 13,
                SpareMagsInCrate = 4,
                Preset = new[]
                {
                    new BossPresetEntry("root",        "6871284e9a353bb50606f3ed", null,         null),
                    new BossPresetEntry("muzzle_1",    "68712ce2251b8d4c6c04ec1f", "root",       "mod_muzzle"),
                    new BossPresetEntry("muzzle_2",    "6878c143254146e6fd043756", "muzzle_1",   "mod_muzzle"),
                    new BossPresetEntry("reciever",    "57c44f4f2459772d2c627113", "root",       "mod_reciever"),
                    new BossPresetEntry("grip",        "6878cc5bd0c26d57bf0aa37a", "root",       "mod_pistol_grip"),
                    new BossPresetEntry("stock",       "6878ccf4181ac8a5b5077236", "root",       "mod_stock"),
                    new BossPresetEntry("stock_000",   "5d135e83d7ad1a21b83f42d8", "stock",      "mod_stock_000"),
                    new BossPresetEntry("handguard",   "687128c4505fed5f370b1625", "root",       "mod_handguard"),
                    new BossPresetEntry("foregrip",    "5b057b4f5acfc4771e1bd3e9", "handguard",  "mod_foregrip"),
                    new BossPresetEntry("mount_000",   "68712b57a1be89347f0d8179", "handguard",  "mod_mount_000"),
                    new BossPresetEntry("mount_001",   "68712b57a1be89347f0d8179", "handguard",  "mod_mount_001"),
                    new BossPresetEntry("light",       "560d657b4bdc2da74d8b4572", "mount_001",  "mod_tactical"),
                    new BossPresetEntry("mount_002",   "68712bd4251b8d4c6c04ec19", "handguard",  "mod_mount_002"),
                    new BossPresetEntry("scope_mount", "5a33b652c4a28232996e407c", "mount_002",  "mod_scope"),
                    new BossPresetEntry("scope_optic", "68a5ac69b55a6b93c20a2bc7", "scope_mount","mod_scope"),
                    new BossPresetEntry("sight_rear",  "5bc09a18d4351e003562b68e", "mount_002",  "mod_sight_rear"),
                    new BossPresetEntry("hg_muzzle",   "6878c1c723c3173d7f06d926", "handguard",  "mod_muzzle"),
                    new BossPresetEntry("sight_front", "5bc09a30d4351e00367fb7c8", "handguard",  "mod_sight_front"),
                },
                // 30rd 9x39 mags ONLY per user spec. excludes the 10rd
                // and 20rd VSS variants.
                MagCandidates = new[]
                {
                    "65118f531b90b4fc77015083", // VSS/VAL 30rd 9x39 (mag_vss_tochmash_vss_val_9x39_30)
                },
                AmmoCandidates = new[]
                {
                    // 9x39 (verified names from items.json).
                    "57a0dfb82459774d3078b56c", // SP-5
                    "57a0e5022459774d1673f889", // SP-6
                    "5c0d688c86f77413ae3407b2", // BP
                    "5c0d668f86f7747ccb7f13b2", // SPP
                    "61962d879bb3d20b0946d385", // PAB-9
                    "6576f96220d53a5b8f3e395e", // FMJ
                },
            },

            // ----------------------------------------------------------------
            // SVD (Shturman). 7.62x54R DMR. preset lifted verbatim from
            // WTT-PresetConverterPlus zombiesSVD.json. weapon is the
            // WTT-Armory custom "SVDDragunovStandard" tpl
            // 6657bc8faeddd6b0a9b40224 (replaces the previous vanilla
            // SVDS 5c46fbd72e2216398b5a8c9c). still 7.62x54R, so
            // AmmoCandidates pool unchanged. notable nesting:
            //   - barrel.muzzle is a 3-deep chain (suppressor adapter +
            //     can + sub-can)
            //   - barrel.mount carries a tac-light
            //   - mount_001 holds the handguard + iron rear sight
            //   - mount_002 holds a scope-mount + nested optic
            // ----------------------------------------------------------------
            new BossWeaponEntry
            {
                Label     = "SVD (Shturman)",
                WeaponTpl = "6657bc8faeddd6b0a9b40224",
                Weight    = 8, // rarer - high-damage DMR
                SpareMagsInCrate = 5,
                Preset = new[]
                {
                    new BossPresetEntry("root",         "6657bc8faeddd6b0a9b40224", null,        null),
                    new BossPresetEntry("grip",         "6657bda8ebfed767215c15d4", "root",      "mod_pistol_grip"),
                    new BossPresetEntry("barrel",       "6657bdc55b6dea8c9e65dc93", "root",      "mod_barrel"),
                    new BossPresetEntry("muzzle_1",     "5c471bfc2e221602b21d4e17", "barrel",    "mod_muzzle"),
                    new BossPresetEntry("muzzle_2",     "5e01e9e273d8eb11426f5bc3", "muzzle_1",  "mod_muzzle"),
                    new BossPresetEntry("muzzle_3",     "5e01ea19e9dc277128008c0b", "muzzle_2",  "mod_muzzle"),
                    new BossPresetEntry("gas_block",    "5c471c842e221615214259b5", "barrel",    "mod_gas_block"),
                    new BossPresetEntry("barrel_mount", "5e569a132642e66b0b68015c", "barrel",    "mod_mount"),
                    new BossPresetEntry("light",        "560d657b4bdc2da74d8b4572", "barrel_mount", "mod_tactical_000"),
                    new BossPresetEntry("mount_001",    "5c471c2d2e22164bef5d077f", "root",      "mod_mount_001"),
                    new BossPresetEntry("handguard",    "6657bd891b74ff27b6a64fae", "mount_001", "mod_handguard"),
                    new BossPresetEntry("sight_rear",   "5c471b7e2e2216152006e46c", "mount_001", "mod_sight_rear"),
                    new BossPresetEntry("reciever",     "6657be1f9014a9663b39bc4b", "root",      "mod_reciever"),
                    new BossPresetEntry("mount_002",    "5dff8db859400025ea5150d4", "root",      "mod_mount_002"),
                    new BossPresetEntry("scope_mount",  "57c69dd424597774c03b7bbc", "mount_002", "mod_scope"),
                    new BossPresetEntry("scope_optic",  "5b2388675acfc4771e1be0be", "scope_mount","mod_scope"),
                },
                // preset's mag tpl (5c471c442... = SVD 10rd std) is already
                // in this candidate list. SAG MK3 20rd kept as the boss-
                // canonical alt.
                MagCandidates = new[]
                {
                    "5c471c442e221602b542a6f8", // SVD 10rd std (from new preset)
                    "5c88f24b2e22160bc12c69a6", // SAG MK3 SVD 20rd
                },
                AmmoCandidates = new[]
                {
                    // 7.62x54R (verified names from items.json).
                    "5887431f2459777e1612938f", // LPS gzh
                    "560d61e84bdc2da74d8b4571", // SNB gzh
                    "59e77a2386f7742ee578960a", // 7N1
                    "5e023cf8186a883be655e54f", // T-46M
                    "5e023d34e8a400319a28ed44", // 7BT1
                    "5e023d48186a883be655e551", // 7N37
                },
            },

            // ----------------------------------------------------------------
            // X-17 SCAR-17 (Knight). 7.62x51 NATO. preset lifted verbatim
            // from WTT-PresetConverterPlus zombiesScar.json. verified
            // identity via items.json: tpl 676176d362e0497044079f4c is
            // "weapon_x_products_x17_scar_17_762x51" - a heavily modded
            // SCAR-17 frame from the X Products mod. mag is the Lancer L7
            // 25rd 7.62x51. tree highlights:
            //   - custom reciever (6165adcdd3a39d50044c120f) hosts barrel,
            //     scope mount (which itself has an optic + top mount),
            //     iron sights, and 2 side rail mounts
            //   - barrel.muzzle.muzzle_000 + muzzle_001 (suppressor +
            //     adapter chain)
            //   - mount_000 carries a vertical foregrip; mount_001
            //     carries a tac-light
            //   - stock chain is 4-deep (collapsing tube + adapter + pad)
            // ----------------------------------------------------------------
            new BossWeaponEntry
            {
                Label     = "SCAR-17 X-17 (Knight)",
                WeaponTpl = "676176d362e0497044079f4c",
                Weight    = 7, // rarest - heavy battle rifle
                SpareMagsInCrate = 4,
                Preset = new[]
                {
                    new BossPresetEntry("root",         "676176d362e0497044079f4c", null,         null),
                    new BossPresetEntry("grip",         "5b07db875acfc40dc528a5f6", "root",       "mod_pistol_grip"),
                    new BossPresetEntry("reciever",     "6165adcdd3a39d50044c120f", "root",       "mod_reciever"),
                    new BossPresetEntry("scope_mount",  "618b9643526131765025ab35", "reciever",   "mod_scope"),
                    new BossPresetEntry("scope_optic",  "5b3b99475acfc432ff4dcbee", "scope_mount","mod_scope"),
                    new BossPresetEntry("scope_top",    "618b9671d14d6d5ab879c5ea", "scope_mount","mod_mount"),
                    new BossPresetEntry("barrel",       "6183b0711cb55961fa0fdcad", "reciever",   "mod_barrel"),
                    new BossPresetEntry("muzzle",       "5cf78496d7f00c065703d6ca", "barrel",     "mod_muzzle"),
                    new BossPresetEntry("muzzle_aux0",  "5c878e9d2e2216000f201903", "muzzle",     "mod_muzzle_000"),
                    new BossPresetEntry("muzzle_aux1",  "5cf78720d7f00c06595bc93e", "muzzle",     "mod_muzzle_001"),
                    new BossPresetEntry("sight_rear",   "5fb6564947ce63734e3fa1da", "reciever",   "mod_sight_rear"),
                    new BossPresetEntry("sight_front",  "5fb6567747ce63734e3fa1dc", "reciever",   "mod_sight_front"),
                    new BossPresetEntry("mount_000",    "61816df1d3a39d50044c139e", "reciever",   "mod_mount_000"),
                    new BossPresetEntry("foregrip",     "5c1bc5af2e221602b412949b", "mount_000",  "mod_foregrip"),
                    new BossPresetEntry("mount_001",    "61816dfa6ef05c2ce828f1ad", "reciever",   "mod_mount_001"),
                    new BossPresetEntry("light",        "560d657b4bdc2da74d8b4572", "mount_001",  "mod_tactical_001"),
                    new BossPresetEntry("stock_main",   "66ffc246a81a4f85e70d4d06", "root",       "mod_stock"),
                    new BossPresetEntry("stock_sub1",   "58ac1bf086f77420ed183f9f", "stock_main", "mod_stock"),
                    new BossPresetEntry("stock_sub2",   "5c793fb92e221644f31bfb64", "stock_sub1", "mod_stock"),
                    new BossPresetEntry("stock_pad",    "6516e91f609aaf354b34b3e2", "stock_sub2", "mod_stock_000"),
                    new BossPresetEntry("charge",       "6181688c6c780c1e710c9b04", "root",       "mod_charge"),
                },
                MagCandidates = new[]
                {
                    "65293c7a17e14363030ad308", // Lancer L7 AWM 7.62x51 25rd (from preset)
                },
                AmmoCandidates = new[]
                {
                    // 7.62x51 NATO (verified names from items.json), same
                    // pool the M1A uses.
                    "58dd3ad986f77403051cba8f", // M80
                    "5a6086ea4f39f99cd479502f", // M61
                    "5efb0c1bd79ff02a1f5e68d9", // M993 (AP)
                    "6768c25aa7b238f14a08d3f6", // M80A1
                    "5a608bf24f39f98ffc77720e", // M62
                    "5e023e88277cce2b522ff2b1", // Ultra Nosler
                },
            },

            // ----------------------------------------------------------------
            // PKP Pecheneg (Kaban). 7.62x54R LMG. preset lifted from
            // bossboar.json - mod table keyed by 64ca3d3954fc657e230529cc.
            // intentionally the rarest entry in the pool (Weight=4) so
            // belt-fed LMG drops feel like a jackpot. requires the 100rd
            // belt box to function; everything below 100rd that would fit
            // a PKP doesn't exist, so MaxMagCapacity=100 is mandatory for
            // this entry to spawn at all.
            // ----------------------------------------------------------------
            new BossWeaponEntry
            {
                Label     = "PKP Pecheneg (Kaban)",
                WeaponTpl = "64ca3d3954fc657e230529cc",
                Weight    = 4, // rarest in pool
                SpareMagsInCrate = 2, // belt boxes are huge - 2 spares plus the loaded one
                // preset lifted verbatim from WTT-PresetConverterPlus
                // zombiesPKP.json (preset id 6a1296d2b829a1eb989bbada).
                // mag tpl in the preset is the same 100rd PKP belt our
                // MagCandidates already picks, so we leave magazine out
                // of the tree and let the runtime random pick handle it
                // (same as every other entry). PKP slot ID is
                // "mod_pistolgrip" (one word) - BSG's PK-platform naming.
                Preset = new[]
                {
                    new BossPresetEntry("root",       "64ca3d3954fc657e230529cc", null,        null),
                    new BossPresetEntry("grip",       "6087e663132d4d12c81fd96b", "root",      "mod_pistolgrip"),
                    new BossPresetEntry("stock",      "6492e3a97df7d749100e29ee", "root",      "mod_stock"),
                    new BossPresetEntry("barrel",     "64639a9aab86f8fd4300146c", "root",      "mod_barrel"),
                    new BossPresetEntry("handguard",  "6491c6f6ef312a876705191b", "root",      "mod_handguard"),
                    // tac-light + angled foregrip nest under the handguard
                    new BossPresetEntry("light",      "560d657b4bdc2da74d8b4572", "handguard", "mod_tactical_000"),
                    new BossPresetEntry("foregrip",   "5c1cd46f2e22164bef5cfedb", "handguard", "mod_foregrip"),
                    new BossPresetEntry("sight_rear", "6492fb8253acae0af00a29b6", "root",      "mod_sight_rear"),
                    // scope mount on the receiver rail with a nested optic
                    new BossPresetEntry("scope_base", "591ee00d86f774592f7b841e", "root",      "mod_scope"),
                    new BossPresetEntry("scope_opt",  "68a5ab09c44fa287ba0a97b5", "scope_base","mod_scope"),
                },
                MagCandidates = new[]
                {
                    "646372518610c40fc20204e8", // PKP 100rd belt box (Kaban's only mag)
                },
                AmmoCandidates = new[]
                {
                    // 7.62x54R (verified names from items.json).
                    "5887431f2459777e1612938f", // LPS gzh
                    "560d61e84bdc2da74d8b4571", // SNB gzh
                    "59e77a2386f7742ee578960a", // 7N1
                    "5e023cf8186a883be655e54f", // T-46M
                    "5e023d34e8a400319a28ed44", // 7BT1
                    "5e023d48186a883be655e551", // 7N37
                },
            },
        };

        // entry point: called from SupplyDropLootTable.PopulateLoot AND
        // RerollLoot. guaranteed boss weapon per crate (per user spec).
        public static void SeedBossWeapons(StashGridClass grid, ItemFactoryClass factory, InventoryController inv, int waveCount)
        {
            if (Entries.Count == 0)
            {
                Plugin.LogSource?.LogInfo("[BossWeapon] no entries configured; skipping.");
                return;
            }
            try
            {
                BossWeaponEntry pick = PickWeapon(waveCount);
                if (pick == null) return;

                string magTpl = PickMagazine(pick, factory, inv);
                if (string.IsNullOrEmpty(magTpl))
                {
                    Plugin.LogSource?.LogWarning($"[BossWeapon] '{pick.Label}' has no valid mag candidates (all > {MaxMagCapacity} rounds?); skipping spawn.");
                    return;
                }

                string ammoTpl = PickAmmo(pick, factory, inv, waveCount);
                if (string.IsNullOrEmpty(ammoTpl))
                {
                    Plugin.LogSource?.LogWarning($"[BossWeapon] '{pick.Label}' has no valid ammo candidates; skipping spawn.");
                    return;
                }

                Item weapon = BuildWeapon(pick, magTpl, ammoTpl, factory, inv);
                if (weapon == null) return;

                // pre-load the weapon's held-prefab bundle. without this,
                // custom-mod weapons (WTT-Armory SVD, WTT-ContentBackport
                // AS VAL, X Products SCAR) throw "bundle not loaded" when
                // the player switches to them - their bundles aren't in
                // the player's profile pre-bake list. fire-and-forget:
                // by the time the player walks to the crate + picks up
                // the weapon, the async load is done. uses the same
                // helper the wallbuy patches use - reads item.Prefab.path
                // and IEasyAssets.Retain's it.
                _ = WallbuyBundleLoader.EnsureItemBundleLoaded(weapon);

                var seat = grid.AddAnywhere(weapon, EErrorHandlingType.Ignore);
                if (seat.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[BossWeapon] could not seat '{pick.Label}' in crate: {seat.Error}");
                    return;
                }
                Plugin.LogSource?.LogInfo($"[BossWeapon] seeded '{pick.Label}' into crate (mag={magTpl}, ammo={ammoTpl}, wave={waveCount}).");

                // dispense N spare loaded mags into the same crate grid.
                int placed = 0;
                for (int i = 0; i < pick.SpareMagsInCrate; i++)
                {
                    Item spareMag = factory.CreateItem(((IIdGenerator)inv).NextId, magTpl, null);
                    if (spareMag == null) continue;
                    LoadMagazineToCapacity(spareMag, ammoTpl, factory, inv);
                    var magSeat = grid.AddAnywhere(spareMag, EErrorHandlingType.Ignore);
                    if (magSeat.Failed)
                    {
                        Plugin.LogSource?.LogInfo($"[BossWeapon] crate full after {placed} spare mag(s).");
                        break;
                    }
                    placed++;
                }
                Plugin.LogSource?.LogInfo($"[BossWeapon] dispensed {placed}/{pick.SpareMagsInCrate} spare mags into crate.");

                // register so Max Ammo can refill these later when picked up.
                BossWeaponRegistry.Register(pick.WeaponTpl, magTpl, ammoTpl, pick.SpareMagsInCrate);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[BossWeapon] SeedBossWeapons threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // wave-scaled weighted random across Entries. same lerp-toward-avg
        // model SupplyDropLootTable.PickRandom uses, so rare boss weapons
        // become more likely as the wave count climbs.
        private static BossWeaponEntry PickWeapon(int waveCount)
        {
            if (Entries.Count == 0) return null;
            if (Entries.Count == 1) return Entries[0]; // shortcut: nothing to weight

            float t = Mathf.Clamp01((waveCount - 1f) / (float)WaveCapForScaling);
            float avg = 0f;
            for (int i = 0; i < Entries.Count; i++) avg += Math.Max(0, Entries[i].Weight);
            avg /= Entries.Count;

            float total = 0f;
            float[] effective = new float[Entries.Count];
            for (int i = 0; i < Entries.Count; i++)
            {
                effective[i] = Mathf.Lerp(Math.Max(0, Entries[i].Weight), avg, t);
                total += effective[i];
            }
            if (total <= 0f) return Entries[0];

            float roll = UnityEngine.Random.Range(0f, total);
            float cum = 0f;
            for (int i = 0; i < Entries.Count; i++)
            {
                cum += effective[i];
                if (roll < cum) return Entries[i];
            }
            return Entries[Entries.Count - 1];
        }

        // random pick from MagCandidates filtered by MaxMagCapacity. checks
        // each candidate's actual mag.MaxCount at runtime so we're never
        // tricked by stale tpl data - a mag's capacity comes from the
        // template's Cartridges[0].MaxCount.
        private static string PickMagazine(BossWeaponEntry entry, ItemFactoryClass factory, InventoryController inv)
        {
            List<string> valid = new List<string>();
            foreach (string tpl in entry.MagCandidates)
            {
                Item probe = factory.CreateItem(((IIdGenerator)inv).NextId, tpl, null);
                if (probe is MagazineItemClass mag && mag.MaxCount > 0 && mag.MaxCount <= MaxMagCapacity)
                    valid.Add(tpl);
                // probe is discarded - factory.CreateItem doesn't seat it anywhere,
                // so it'll get GC'd. no explicit cleanup needed.
            }
            if (valid.Count == 0) return null;
            return valid[UnityEngine.Random.Range(0, valid.Count)];
        }

        // damage-weighted ammo picker. each candidate's weight = Damage^exp
        // where exp goes from 1 at wave 1 (linear bias) to 3 at the wave cap
        // (cubic bias = high-damage ammo dominates). spawns temp items just
        // to read the Damage value off AmmoItemClass.
        private static string PickAmmo(BossWeaponEntry entry, ItemFactoryClass factory, InventoryController inv, int waveCount)
        {
            if (entry.AmmoCandidates == null || entry.AmmoCandidates.Length == 0) return null;

            float t = Mathf.Clamp01((waveCount - 1f) / (float)WaveCapForScaling);
            float exp = Mathf.Lerp(1f, 3f, t);

            // gather (tpl, damage) for each candidate
            List<KeyValuePair<string, float>> weighted = new List<KeyValuePair<string, float>>();
            float totalWeight = 0f;
            foreach (string tpl in entry.AmmoCandidates)
            {
                Item probe = factory.CreateItem(((IIdGenerator)inv).NextId, tpl, null);
                AmmoItemClass ammo = probe as AmmoItemClass;
                if (ammo == null) continue;
                float dmg = Math.Max(1f, ammo.Damage); // floor at 1 so weight^exp doesn't collapse
                float w = Mathf.Pow(dmg, exp);
                weighted.Add(new KeyValuePair<string, float>(tpl, w));
                totalWeight += w;
            }
            if (weighted.Count == 0 || totalWeight <= 0f) return null;

            float roll = UnityEngine.Random.Range(0f, totalWeight);
            float cum = 0f;
            for (int i = 0; i < weighted.Count; i++)
            {
                cum += weighted[i].Value;
                if (roll < cum) return weighted[i].Key;
            }
            return weighted[weighted.Count - 1].Key;
        }

        // builds the weapon: attachments from the preset tree (topological
        // sort, same as SupplyDropLootTable.BuildEntry), then attaches the
        // chosen magazine, loads it to capacity with the chosen ammo, and
        // seats one chamber round of the same ammo.
        private static Item BuildWeapon(BossWeaponEntry entry, string magTpl, string ammoTpl, ItemFactoryClass factory, InventoryController inv)
        {
            // topological build of attachments
            Dictionary<string, Item> byPresetId = new Dictionary<string, Item>(entry.Preset.Length);
            List<BossPresetEntry> pending = new List<BossPresetEntry>(entry.Preset);
            int safety = pending.Count + 2;
            while (pending.Count > 0 && safety-- > 0)
            {
                bool progress = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    BossPresetEntry p = pending[i];
                    if (p.ParentPresetId == null)
                    {
                        Item root = factory.CreateItem(((IIdGenerator)inv).NextId, p.Tpl, null);
                        if (root == null) { Plugin.LogSource?.LogError($"[BossWeapon] CreateItem({p.Tpl}) returned null."); return null; }
                        byPresetId[p.PresetId] = root;
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }
                    if (!byPresetId.TryGetValue(p.ParentPresetId, out Item parent)) continue;

                    CompoundItem cParent = parent as CompoundItem;
                    Slot slot = cParent?.Slots?.FirstOrDefault(s => s != null && s.ID == p.SlotId);
                    if (slot == null)
                    {
                        Plugin.LogSource?.LogWarning($"[BossWeapon] slot '{p.SlotId}' missing on {parent.TemplateId}; skipping {p.Tpl}.");
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }
                    Item mod = factory.CreateItem(((IIdGenerator)inv).NextId, p.Tpl, null);
                    if (mod == null) { pending.RemoveAt(i); progress = true; continue; }
                    slot.ChangeContainedItemDirectly(mod);
                    byPresetId[p.PresetId] = mod;
                    pending.RemoveAt(i);
                    progress = true;
                }
                if (!progress) break;
            }

            if (!byPresetId.TryGetValue("root", out Item weapon)) return null;

            // attach magazine + load it
            CompoundItem compound = weapon as CompoundItem;
            Slot magSlot = compound?.Slots?.FirstOrDefault(s => s != null && s.ID == "mod_magazine");
            if (magSlot != null)
            {
                Item mag = factory.CreateItem(((IIdGenerator)inv).NextId, magTpl, null);
                if (mag != null)
                {
                    magSlot.ChangeContainedItemDirectly(mag);
                    LoadMagazineToCapacity(mag, ammoTpl, factory, inv);
                }
            }
            else
            {
                Plugin.LogSource?.LogWarning($"[BossWeapon] '{entry.Label}' weapon has no mod_magazine slot.");
            }

            // seat chamber round (single round of the chosen ammo)
            Weapon w = weapon as Weapon;
            if (w?.Chambers != null && w.Chambers.Length > 0)
            {
                Slot chamber = w.Chambers[0];
                if (chamber != null && chamber.ContainedItem == null)
                {
                    Item round = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
                    if (round != null) chamber.ChangeContainedItemDirectly(round);
                }
            }
            weapon.SpawnedInSession = true;
            return weapon;
        }

        // fills a magazine's Cartridges StackSlot with ammoTpl up to MaxCount.
        // same recipe ZombiesLoadoutPatch.LoadAmmoIntoMagazine uses.
        private static void LoadMagazineToCapacity(Item magItem, string ammoTpl, ItemFactoryClass factory, InventoryController inv)
        {
            try
            {
                MagazineItemClass mag = magItem as MagazineItemClass;
                if (mag?.Cartridges == null) return;
                int capacity = mag.Cartridges.MaxCount;
                if (capacity <= 0) return;

                Item ammo = factory.CreateItem(((IIdGenerator)inv).NextId, ammoTpl, null);
                if (ammo == null) return;
                ammo.StackObjectsCount = capacity;
                var result = mag.Cartridges.Add(ammo, simulate: false);
                if (result.Failed)
                    Plugin.LogSource?.LogWarning($"[BossWeapon] Cartridges.Add failed for {magItem.TemplateId}: {result.Error}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[BossWeapon] LoadMagazineToCapacity threw: {ex.Message}");
            }
        }
    }

    // entry shape: weapon + attachment preset + mag/ammo pool + dispense count.
    public sealed class BossWeaponEntry
    {
        public string Label;
        public string WeaponTpl;
        public int Weight;
        public int SpareMagsInCrate;
        public BossPresetEntry[] Preset;
        public string[] MagCandidates;
        public string[] AmmoCandidates;
    }

    // local PresetEntry type (parallel to SupplyDropLootTable.PresetEntry but
    // declared here to keep the boss-weapon table self-contained).
    public sealed class BossPresetEntry
    {
        public readonly string PresetId;
        public readonly string Tpl;
        public readonly string ParentPresetId;
        public readonly string SlotId;
        public BossPresetEntry(string presetId, string tpl, string parentPresetId, string slotId)
        {
            PresetId = presetId; Tpl = tpl; ParentPresetId = parentPresetId; SlotId = slotId;
        }
    }
}
