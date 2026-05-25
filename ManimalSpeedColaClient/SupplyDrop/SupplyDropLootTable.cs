using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // declarative loot pool for the supply-drop crate. each entry describes
    // an item plus (optionally) a preset tree for items that need assembly
    // (helmets with armor inserts, weapons with mods, etc.). Weight is the
    // BASE rarity - the actual draw weight at PickRandom time gets scaled
    // by the current wave so that rare items become more common as the
    // raid progresses.
    //
    // tier weight reference (base, before wave scaling):
    //   tier 0  ~5      max-coverage class 5 with face + aventail (LShZ)
    //   tier 1  ~6-10   class 4-5 heavy with face shield (Rys-T, Killa, Altyn)
    //   tier 2  ~15-22  class 4 standard with face shield (Maska, Diamond Age)
    //   tier 3  ~25-30  class 3 modular with face (DevTac) / open (Airframe)
    //   tier 4  ~35     class 3 fast helmet (ULACH)
    public static class SupplyDropLootTable
    {
        private const int LootEntriesPerCrate = 5;

        // TarCoin payout per crate. random integer in [Min, Max] (inclusive
        // on Min, exclusive on Max for Random.Range, so Max+1 to cover the
        // ceiling). guaranteed - every supply drop contains a coin stack.
        private const string TarCoinTpl     = "6a0b45f6682ea02b4ca45907";
        private const int    TarCoinStackMin = 500;
        private const int    TarCoinStackMax = 5000;

        // wave at which rare/common weights become fully equalized. at wave 1
        // the base weights apply unchanged (rare items are actually rare);
        // at wave WaveCapForScaling+, all entries draw at the same average
        // weight (rare items as likely as common). intermissions don't fire
        // until songs end, so realistically this curve operates over the
        // wave 5..WaveCap range.
        private const int WaveCapForScaling = 30;

        // per-category seeding caps. when we hit the cap for a category in
        // a crate, subsequent rolls of that category are skipped and the
        // slot is re-rolled (against the safety budget). lets a single
        // headgear slot per crate while letting ammo / medical / misc
        // stack freely. categories not listed are unlimited.
        public enum LootCategory
        {
            Headgear,
            BodyArmor,
            FaceCover,
            Misc,
            // supply-drop exclusive perks (Deadshot, Double Tap, future
            // additions). capped at 1 per crate so the player can't roll
            // a Deadshot + Double Tap combo from a single drop and get
            // both perks free in one trip.
            Perk,
        }

        private static readonly Dictionary<LootCategory, int> CategoryCaps =
            new Dictionary<LootCategory, int>
            {
                { LootCategory.Headgear,  1 },
                { LootCategory.BodyArmor, 1 },
                { LootCategory.FaceCover, 1 },
                { LootCategory.Perk,      1 },
            };

        public sealed class LootEntry
        {
            public string Label;
            public string Tpl;
            public int Weight;
            public LootCategory Category = LootCategory.Misc;
            public PresetEntry[] Preset;
            // for Perk category entries, the stimulator buff name this
            // bottle applies. used by SeedRandomLoot to skip the entry if
            // the player already has the buff active (no point dropping
            // a Deadshot when the player is already buffed). null/empty
            // for non-perk entries.
            public string BuffNameIfPerk;
        }

        public sealed class PresetEntry
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

        // ---- the loot pool ----
        // tiers chosen by armor coverage + armor rating. lower Weight = rarer.
        public static readonly List<LootEntry> Entries = new List<LootEntry>
        {
            // ---- tier 0: top-tier, max armor + max coverage ----
            new LootEntry
            {
                // LShZ-2DTM black + face shield + aventail. class 5, neck
                // protection. heaviest most-protective helmet in the pool.
                Label  = "BNTI LShZ-2DTM (black) + shield + aventail",
                Tpl    = "5d6d3716a4b9361bc8618872",
                Weight = 5,
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",     "5d6d3716a4b9361bc8618872", null,   null),
                    new PresetEntry("top",      "657fa009d4caf976440afe3a", "root", "Helmet_top"),
                    new PresetEntry("back",     "657fa04ac6679fefb3051e24", "root", "Helmet_back"),
                    new PresetEntry("ears",     "657fa07387e11c61f70bface", "root", "Helmet_ears"),
                    new PresetEntry("shield",   "5d6d3829a4b9361bc8618943", "root", "mod_equipment_000"),
                    new PresetEntry("aventail", "5d6d3be5a4b9361bc73bc763", "root", "mod_equipment_001"),
                },
            },

            // ---- tier 1: class 4-5 heavy with face shield ----
            new LootEntry
            {
                // Adept Neosteel + mandible. class 5 chassis (top + back),
                // mandible (jaw guard) in mod_equipment_000. no ear armor,
                // no face shield - slightly less coverage than Rys-T/LShZ
                // despite the same armor class.
                Label  = "Adept Neosteel + mandible",
                Tpl    = "65709d2d21b9f815e208ff95",
                Weight = 7, // class 5, jaw protection, no face/ears
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",     "65709d2d21b9f815e208ff95", null,   null),
                    new PresetEntry("top",      "657f9eb7e9433140ad0baf86", "root", "Helmet_top"),
                    new PresetEntry("back",     "657f9ef6c6679fefb3051e1f", "root", "Helmet_back"),
                    new PresetEntry("mandible", "6570a88c8f221f3b210353b7", "root", "mod_equipment_000"),
                },
            },
            new LootEntry
            {
                Label  = "Rys-T helmet + face shield",
                Tpl    = "5f60c74e3b85f6263c145586",
                Weight = 6, // class 5 modern, face shield
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",   "5f60c74e3b85f6263c145586", null,   null),
                    new PresetEntry("top",    "657bc285aab96fccee08bea3", "root", "Helmet_top"),
                    new PresetEntry("back",   "657bc2c5a1c61ee0c3036333", "root", "Helmet_back"),
                    new PresetEntry("ears",   "657bc2e7b30eca976305118d", "root", "Helmet_ears"),
                    new PresetEntry("shield", "5f60c85b58eff926626a60f7", "root", "mod_equipment"),
                },
            },
            new LootEntry
            {
                // Maska 1SCh Killa Edition - vanilla preset already has the
                // Killa-specific face shield in mod_equipment.
                Label  = "Maska-1SCh (Killa Edition)",
                Tpl    = "5c0e874186f7745dc7616606",
                Weight = 8, // class 4 boss helmet, full face protection
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",   "5c0e874186f7745dc7616606", null,   null),
                    new PresetEntry("top",    "6571133d22996eaf11088200", "root", "Helmet_top"),
                    new PresetEntry("back",   "6571138e818110db4600aa71", "root", "Helmet_back"),
                    new PresetEntry("ears",   "657112fa818110db4600aa6b", "root", "Helmet_ears"),
                    new PresetEntry("shield", "5c0e842486f77443a74d2976", "root", "mod_equipment"),
                },
            },
            new LootEntry
            {
                Label  = "Altyn helmet + face shield",
                Tpl    = "5aa7e276e5b5b000171d0647",
                Weight = 10, // class 4 heavy, face shield
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",   "5aa7e276e5b5b000171d0647", null,   null),
                    new PresetEntry("top",    "657bc06daab96fccee08be9b", "root", "Helmet_top"),
                    new PresetEntry("back",   "657bc0d8a1c61ee0c303632f", "root", "Helmet_back"),
                    new PresetEntry("ears",   "657bc107aab96fccee08be9f", "root", "Helmet_ears"),
                    new PresetEntry("shield", "5aa7e373e5b5b000137b76f0", "root", "mod_equipment"),
                },
            },

            // ---- tier 2: class 4 standard with face shield ----
            new LootEntry
            {
                Label  = "Maska-1SCh + face shield",
                Tpl    = "5c091a4e0db834001d5addc8",
                Weight = 15, // class 4 with face shield, no neck
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",   "5c091a4e0db834001d5addc8", null,   null),
                    new PresetEntry("top",    "6571133d22996eaf11088200", "root", "Helmet_top"),
                    new PresetEntry("back",   "6571138e818110db4600aa71", "root", "Helmet_back"),
                    new PresetEntry("ears",   "657112fa818110db4600aa6b", "root", "Helmet_ears"),
                    new PresetEntry("shield", "5c0919b50db834001b7ce3b9", "root", "mod_equipment"),
                },
            },
            new LootEntry
            {
                Label  = "Diamond Age Bastion + shield",
                Tpl    = "5ea17ca01412a1425304d1c0",
                Weight = 18, // class 4 modular with shield insert
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",   "5ea17ca01412a1425304d1c0", null,   null),
                    new PresetEntry("top",    "657f9a55c6679fefb3051e19", "root", "Helmet_top"),
                    new PresetEntry("back",   "657f9a94ada5fadd1f07a589", "root", "Helmet_back"),
                    new PresetEntry("shield", "5ea18c84ecf1982c7712d9a2", "root", "mod_nvg"),
                },
            },

            // ---- tier 3: class 3 modular ----
            new LootEntry
            {
                Label  = "DevTac Ronin (full)",
                Tpl    = "5b4329f05acfc47a86086aa1",
                Weight = 25, // class 3 but full integrated face coverage
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root", "5b4329f05acfc47a86086aa1", null,   null),
                    new PresetEntry("top",  "65711b07a330b8c9060f7b01", "root", "Helmet_top"),
                    new PresetEntry("back", "65711b489eb8c145180dbb9d", "root", "Helmet_back"),
                    new PresetEntry("eyes", "65711b9b65daf6aa960c9b1b", "root", "helmet_eyes"),
                    new PresetEntry("jaw",  "65711bc79eb8c145180dbba1", "root", "helmet_jaw"),
                    new PresetEntry("ears", "65711b706d197c216005b31c", "root", "Helmet_ears"),
                },
            },
            new LootEntry
            {
                // Ops-Core FAST MT (Black) standard preset + face shield in
                // mod_equipment_000 + RAC headset in mod_equipment_001.
                // class 3 chassis with proper face shield armor (rated
                // better than the Halloween mask) + comms accessory.
                Label  = "FAST MT (black) + face shield + RAC headset",
                Tpl    = "5a154d5cfcdbcb001a3b00da",
                Weight = 20, // class 3 with face shield, between Diamond Age and DevTac
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",    "5a154d5cfcdbcb001a3b00da", null,   null),
                    new PresetEntry("top",     "657f8ec5f4c82973640b234c", "root", "Helmet_top"),
                    new PresetEntry("back",    "657f8f10f4c82973640b2350", "root", "Helmet_back"),
                    new PresetEntry("shield",  "5a16b7e1fcdbcb00165aa6c9", "root", "mod_equipment_000"),
                    new PresetEntry("headset", "5a16b9fffcdbcb0176308b34", "root", "mod_equipment_001"),
                },
            },

            new LootEntry
            {
                // Ops-Core FAST MT (Sand) standard preset + Heavy Trooper Mask
                // (Halloween event piece) in mod_nvg. class 3 chassis with
                // full face coverage from the mask.
                Label  = "FAST MT + Heavy Trooper mask",
                Tpl    = "5ac8d6885acfc400180ae7b0",
                Weight = 28, // class 3, full face coverage via mask
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root", "5ac8d6885acfc400180ae7b0", null,   null),
                    new PresetEntry("top",  "657f8ec5f4c82973640b234c", "root", "Helmet_top"),
                    new PresetEntry("back", "657f8f10f4c82973640b2350", "root", "Helmet_back"),
                    new PresetEntry("mask", "5ea058e01dbce517f324b3e2", "root", "mod_nvg"),
                },
            },
            new LootEntry
            {
                // Crye Precision Airframe (tan) standard preset + chops insert
                // in mod_equipment_001 (verified by slot filter match).
                Label  = "Crye Airframe + chops",
                Tpl    = "5c17a7ed2e2216152142459c",
                Weight = 30, // class 3 modular, no face shield
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root",  "5c17a7ed2e2216152142459c", null,   null),
                    new PresetEntry("top",   "657f9897f4c82973640b235e", "root", "Helmet_top"),
                    new PresetEntry("back",  "657f98fbada5fadd1f07a585", "root", "Helmet_back"),
                    new PresetEntry("chops", "5c178a942e22164bef5ceca3", "root", "mod_equipment_001"),
                },
            },

            // ---- tier 4: class 3 fast helmet, no face ----
            new LootEntry
            {
                Label  = "ULACH (black)",
                Tpl    = "5b40e1525acfc4771e1c6611",
                Weight = 35, // class 3, no face shield
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root", "5b40e1525acfc4771e1c6611", null,   null),
                    new PresetEntry("top",  "657112234269e9a568089eac", "root", "Helmet_top"),
                    new PresetEntry("back", "657112a4818110db4600aa66", "root", "Helmet_back"),
                    new PresetEntry("ears", "657112ce22996eaf110881fb", "root", "Helmet_ears"),
                },
            },
            new LootEntry
            {
                Label  = "ULACH (tan)",
                Tpl    = "5b40e2bc5acfc40016388216",
                Weight = 35,
                Category = LootCategory.Headgear,
                Preset = new[]
                {
                    new PresetEntry("root", "5b40e2bc5acfc40016388216", null,   null),
                    new PresetEntry("top",  "657112234269e9a568089eac", "root", "Helmet_top"),
                    new PresetEntry("back", "657112a4818110db4600aa66", "root", "Helmet_back"),
                    new PresetEntry("ears", "657112ce22996eaf110881fb", "root", "Helmet_ears"),
                },
            },

            // ============================================================
            // BODY ARMOR
            // weights chosen by zone coverage (slot count) + armor class.
            // higher coverage + higher class = lower weight = rarer.
            // ============================================================

            // tier S (rarest, max coverage + class 4-5)
            new LootEntry
            {
                // 6B43 FORT Redut T5 - max coverage in the pool (13 zones:
                // soft 4 + collar + 2 shoulders + 2 groin + 2 plates + 2 side).
                Label  = "6B43 FORT Redut",
                Tpl    = "5ca21c6986f77479963115a7",
                Weight = 5,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",        "5ca21c6986f77479963115a7", null,   null),
                    new PresetEntry("soft_f",      "6575d9a79e27f4a85e08112d", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",      "6575d9b8945bf78edd04c427", "root", "Soft_armor_back"),
                    new PresetEntry("soft_l",      "6575d9c40546f8b1de093dee", "root", "Soft_armor_left"),
                    new PresetEntry("soft_r",      "6575d9cf0546f8b1de093df2", "root", "soft_armor_right"),
                    new PresetEntry("collar",      "6575d9d8945bf78edd04c42b", "root", "Collar"),
                    new PresetEntry("shoulder_l",  "6575da07945bf78edd04c433", "root", "Shoulder_l"),
                    new PresetEntry("shoulder_r",  "6575da159e27f4a85e081131", "root", "Shoulder_r"),
                    new PresetEntry("groin",       "6575d9e7945bf78edd04c42f", "root", "Groin"),
                    new PresetEntry("groin_back",  "6575d9f816c2762fba00588d", "root", "Groin_back"),
                    new PresetEntry("plate_f",     "65573fa5655447403702a816", "root", "Front_plate"),
                    new PresetEntry("plate_b",     "65573fa5655447403702a816", "root", "Back_plate"),
                    new PresetEntry("plate_l",     "64afd81707e2cf40e903a316", "root", "Left_side_plate"),
                    new PresetEntry("plate_r",     "64afd81707e2cf40e903a316", "root", "Right_side_plate"),
                },
            },
            new LootEntry
            {
                // NFM THOR Integrated Carrier - 12 zones (soft 4 + collar +
                // 2 shoulders + groin + 2 plates + 2 side).
                Label  = "NFM THOR Integrated Carrier",
                Tpl    = "60a283193cb70855c43a381d",
                Weight = 6,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",       "60a283193cb70855c43a381d", null,   null),
                    new PresetEntry("soft_f",     "6575d561b15fef3dd4051670", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",     "6575d56b16c2762fba005818", "root", "Soft_armor_back"),
                    new PresetEntry("soft_l",     "6575d57a16c2762fba00581c", "root", "Soft_armor_left"),
                    new PresetEntry("soft_r",     "6575d589b15fef3dd4051674", "root", "soft_armor_right"),
                    new PresetEntry("collar",     "6575d598b15fef3dd4051678", "root", "Collar"),
                    new PresetEntry("shoulder_l", "6575d5b316c2762fba005824", "root", "Shoulder_l"),
                    new PresetEntry("shoulder_r", "6575d5bd16c2762fba005828", "root", "Shoulder_r"),
                    new PresetEntry("groin",      "6575d5a616c2762fba005820", "root", "Groin"),
                    new PresetEntry("plate_f",    "656fa61e94b480b8a500c0e8", "root", "Front_plate"),
                    new PresetEntry("plate_b",    "656fa61e94b480b8a500c0e8", "root", "Back_plate"),
                    new PresetEntry("plate_l",    "64afdb577bb3bfe8fe03fd1d", "root", "Left_side_plate"),
                    new PresetEntry("plate_r",    "64afdb577bb3bfe8fe03fd1d", "root", "Right_side_plate"),
                },
            },
            new LootEntry
            {
                // BNTI Zhuk-6a EMR - 9 zones (soft 4 + collar + 2 plates + 2 side).
                Label  = "BNTI Zhuk-6a EMR",
                Tpl    = "5c0e625a86f7742d77340f62",
                Weight = 8,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",    "5c0e625a86f7742d77340f62", null,   null),
                    new PresetEntry("soft_f",  "65764275d8537eb26a0355e9", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",  "657642b0e6d5dd75f40688a5", "root", "Soft_armor_back"),
                    new PresetEntry("soft_l",  "6576434820cc24d17102b148", "root", "Soft_armor_left"),
                    new PresetEntry("soft_r",  "657643732bc38ef78e076477", "root", "soft_armor_right"),
                    new PresetEntry("collar",  "657643a220cc24d17102b14c", "root", "Collar"),
                    new PresetEntry("plate_f", "656f63c027aed95beb08f62c", "root", "Front_plate"),
                    new PresetEntry("plate_b", "656fafe3498d1b7e3e071da4", "root", "Back_plate"),
                    new PresetEntry("plate_l", "64afd81707e2cf40e903a316", "root", "Left_side_plate"),
                    new PresetEntry("plate_r", "64afd81707e2cf40e903a316", "root", "Right_side_plate"),
                },
            },

            // tier A (heavy with good coverage, class 4)
            new LootEntry
            {
                // 6B13 M Killa - 8 zones (soft 4 + collar + groin + 2 plates).
                Label  = "6B13 M (Killa)",
                Tpl    = "5c0e541586f7747fa54205c9",
                Weight = 12,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",    "5c0e541586f7747fa54205c9", null,   null),
                    new PresetEntry("soft_f",  "6575ea3060703324250610da", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",  "6575ea4cf6a13a7b7100adc4", "root", "Soft_armor_back"),
                    new PresetEntry("soft_l",  "6575ea5cf6a13a7b7100adc8", "root", "Soft_armor_left"),
                    new PresetEntry("soft_r",  "6575ea6760703324250610de", "root", "soft_armor_right"),
                    new PresetEntry("collar",  "6575ea719c7cad336508e418", "root", "Collar"),
                    new PresetEntry("groin",   "6575ea7c60703324250610e2", "root", "Groin"),
                    new PresetEntry("plate_f", "656f611f94b480b8a500c0db", "root", "Front_plate"),
                    new PresetEntry("plate_b", "656efaf54772930db4031ff5", "root", "Back_plate"),
                },
            },

            // tier B (medium coverage, class 3-4)
            new LootEntry
            {
                // NFM THOR Concealable Reinforced - 6 zones, class 4 concealable.
                Label  = "NFM THOR Concealable Reinforced",
                Tpl    = "609e8540d5c319764c2bc2e9",
                Weight = 18,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",    "609e8540d5c319764c2bc2e9", null,   null),
                    new PresetEntry("soft_f",  "6572e5221b5bc1185508c24f", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",  "6572e52f73c0eabb700109a0", "root", "Soft_armor_back"),
                    new PresetEntry("soft_l",  "6572e53c73c0eabb700109a4", "root", "Soft_armor_left"),
                    new PresetEntry("soft_r",  "6572e54873c0eabb700109a8", "root", "soft_armor_right"),
                    new PresetEntry("plate_f", "656f9fa0498d1b7e3e071d98", "root", "Front_plate"),
                    new PresetEntry("plate_b", "656f9fa0498d1b7e3e071d98", "root", "Back_plate"),
                },
            },
            new LootEntry
            {
                // 6B13 Assault Flora (digital) - 8 zones, class 3.
                // note: this preset uses lowercase soft_armor_*/front_plate/back_plate.
                Label  = "6B13 Assault (Flora digital)",
                Tpl    = "5c0e53c886f7747fa54205c7",
                Weight = 20,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",    "5c0e53c886f7747fa54205c7", null,   null),
                    new PresetEntry("soft_f",  "654a8b0b0337d53f9102c2ae", "root", "soft_armor_front"),
                    new PresetEntry("soft_b",  "654a8976f414fcea4004d78b", "root", "soft_armor_back"),
                    new PresetEntry("soft_l",  "654a8b3df414fcea4004d78f", "root", "soft_armor_left"),
                    new PresetEntry("soft_r",  "654a8b80f414fcea4004d797", "root", "soft_armor_right"),
                    new PresetEntry("collar",  "654a8ae00337d53f9102c2aa", "root", "Collar"),
                    new PresetEntry("groin",   "654a8bc5f414fcea4004d79b", "root", "Groin"),
                    new PresetEntry("plate_f", "656f603f94b480b8a500c0d6", "root", "front_plate"),
                    new PresetEntry("plate_b", "656efd66034e8e01c407f35c", "root", "back_plate"),
                },
            },
            new LootEntry
            {
                // Kirasa-N - 7 zones, class 3.
                Label  = "Kirasa-N",
                Tpl    = "5b44d22286f774172b0c9de8",
                Weight = 22,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",    "5b44d22286f774172b0c9de8", null,   null),
                    new PresetEntry("soft_f",  "65704de13e7bba58ea0285c8", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",  "65705c3c14f2ed6d7d0b7738", "root", "Soft_armor_back"),
                    new PresetEntry("soft_l",  "65705c777260e1139e091408", "root", "Soft_armor_left"),
                    new PresetEntry("soft_r",  "65705cb314f2ed6d7d0b773c", "root", "soft_armor_right"),
                    new PresetEntry("collar",  "65705cea4916448ae1050897", "root", "Collar"),
                    new PresetEntry("plate_f", "656f9d5900d62bcd2e02407c", "root", "Front_plate"),
                    new PresetEntry("plate_b", "656f9d5900d62bcd2e02407c", "root", "Back_plate"),
                },
            },
            new LootEntry
            {
                // Interceptor OTV - 6 zones, class 3.
                Label  = "Interceptor OTV",
                Tpl    = "64abd93857958b4249003418",
                Weight = 26,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",    "64abd93857958b4249003418", null,   null),
                    new PresetEntry("soft_f",  "6570f30b0921c914bf07964c", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",  "6570f35cd67d0309980a7acb", "root", "Soft_armor_back"),
                    new PresetEntry("soft_l",  "6570f3890b4ae5847f060dad", "root", "Soft_armor_left"),
                    new PresetEntry("soft_r",  "6570f3bb0b4ae5847f060db2", "root", "soft_armor_right"),
                    new PresetEntry("plate_f", "656fb0bd7c2d57afe200c0dc", "root", "Front_plate"),
                    new PresetEntry("plate_b", "656fb0bd7c2d57afe200c0dc", "root", "Back_plate"),
                },
            },

            // tier C (plate carriers - minimal soft coverage but high-class plates)
            new LootEntry
            {
                // LBT 6094A Slick Plate Carrier (Black) - 4 zones (soft front/back + 2 plates).
                Label  = "LBT 6094A Slick (black)",
                Tpl    = "5e4abb5086f77406975c9342",
                Weight = 30,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",    "5e4abb5086f77406975c9342", null,   null),
                    new PresetEntry("soft_f",  "6575e71760703324250610c3", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",  "6575e72660703324250610c7", "root", "Soft_armor_back"),
                    new PresetEntry("plate_f", "656fa76500d62bcd2e024080", "root", "Front_plate"),
                    new PresetEntry("plate_b", "656fa76500d62bcd2e024080", "root", "Back_plate"),
                },
            },
            new LootEntry
            {
                // HighCom Trooper - 4 zones (soft front/back + 2 plates).
                Label  = "HighCom Trooper",
                Tpl    = "5c0e655586f774045612eeb2",
                Weight = 30,
                Category = LootCategory.BodyArmor,
                Preset = new[]
                {
                    new PresetEntry("root",    "5c0e655586f774045612eeb2", null,   null),
                    new PresetEntry("soft_f",  "6570e025615f54368b04fcb0", "root", "Soft_armor_front"),
                    new PresetEntry("soft_b",  "6570e0610b57c03ec90b96ef", "root", "Soft_armor_back"),
                    new PresetEntry("plate_f", "656fad8c498d1b7e3e071da0", "root", "Front_plate"),
                    new PresetEntry("plate_b", "656fad8c498d1b7e3e071da0", "root", "Back_plate"),
                },
            },

            // ============================================================
            // FACE COVERS (own category cap; doesn't compete with helmets
            // or body armor for crate slots, rare drop tier)
            // ============================================================
            new LootEntry
            {
                // Death Shadow armored mask - UHMWPE light-armor face cover.
                // items.json has Slots: [] so no preset tree needed; the
                // armor is integral to the item. doesn't conflict with most
                // helmets (only specific face shields), so the player can
                // pair it with a headgear roll from the same crate.
                // RarityPvE "Rare" in vanilla.
                Label  = "Death Shadow armored mask",
                Tpl    = "6570aead4d84f81fd002a033",
                Weight = 10,
                Category = LootCategory.FaceCover,
            },

            // ============================================================
            // PERKS (supply-drop exclusive; no machine sells these)
            // ============================================================
            new LootEntry
            {
                // Deadshot Daiquiri - 50% recoil reduction (H+V), +20% ergo,
                // zero ADS sway. only spawns in supply drops. tpl is a clone
                // of Speed Cola's stim base on the server side. Perk category
                // is capped at 1/crate so deadshot + doubletap can't both
                // drop from the same supply box.
                Label  = "Deadshot Daiquiri",
                Tpl    = "9d3b6e1f4a5c7e2b8f3a9d04",
                Weight = 15,
                Category = LootCategory.Perk,
                BuffNameIfPerk = Patches.DeadshotDaiquiriBuffState.BuffName, // "deadshotdaiquiri"
            },
            new LootEntry
            {
                // Double Tap Root Beer - +20% fire rate, 1.5x damage. supply
                // drop only, no machine. clones Speed Cola stim base. shares
                // the Perk category cap with Deadshot - one or the other
                // per crate, not both.
                Label  = "Double Tap Root Beer",
                Tpl    = "ab4c7e2f5d6b8a3e9f1c5d05",
                Weight = 15,
                Category = LootCategory.Perk,
                BuffNameIfPerk = Patches.DoubleTapBuffState.BuffName, // "doubletap"
            },
        };

        // items seeded into every crate as a hard guarantee, outside the
        // random roll loop. like the TarCoin stack these are bonus items -
        // they don't consume a LootEntriesPerCrate slot, don't roll against
        // weights, and don't honor category caps. add a (tpl, label) here
        // to lock something into every supply drop.
        private static readonly (string Tpl, string Label)[] GuaranteedItems =
        {
            ("575146b724597720a27126d5", "Pack of milk"),
            ("590c5f0d86f77413997acfab", "MRE lunch box"),
        };

        // UsePrefab container bundles for every consumable that can land in
        // a supply drop. EFT's PoolManagerClass.CreateItemUsablePrefabAsync
        // calls IEasyAssets.GetAsset on these directly when the player
        // initiates the food/drink animation - it does NOT call Retain()
        // itself, so the bundle has to already be loaded or the animation
        // throws "<bundle> is not loaded. You should load it first" and
        // leaves the hands controller in a broken state (same NRE cascade
        // we hit with Juggernog before pre-retaining the perk bundles).
        //
        // these paths come from each item's _props.UsePrefab.path in
        // SPT_Data/database/templates/items.json. when you add a new
        // consumable to GuaranteedItems / Entries, also add its UsePrefab
        // path here.
        private static readonly string[] RequiredUsePrefabBundles =
        {
            // Pack of milk (tpl 575146b724597720a27126d5)
            "assets/content/weapons/usable_items/item_tetrapak/item_tetrapak_container.bundle",
            // MRE lunch box (tpl 590c5f0d86f77413997acfab)
            "assets/content/weapons/usable_items/item_mre/item_mre_container.bundle",
            // Deadshot Daiquiri (tpl 9d3b6e1f4a5c7e2b8f3a9d04) - reuses the
            // Speed Cola container bundle as a placeholder UsePrefab. when a
            // dedicated Deadshot bottle bundle ships, update the server-side
            // CustomItems UsePrefab path AND this entry to point at it.
            // NOTE: the same bundle is also pre-loaded by SpeedColaBundleLoader
            // when a Speed Cola machine spawns; this entry covers raids where
            // no SC machine is present so the supply-drop bottle still uses
            // its drink animation cleanly.
            "manimal/speed_cola_container.bundle",
        };

        // pre-retain every UsePrefab bundle a supply-drop consumable might
        // need. safe to call repeatedly - IEasyAssets refcounts retentions,
        // so subsequent calls bump the refcount without re-loading. should
        // be awaited from the supply-drop spawn path BEFORE the crate is
        // handed to the player.
        public static async Task WarmupUsePrefabBundlesAsync()
        {
            try
            {
                if (RequiredUsePrefabBundles.Length == 0) return;
                if (!Singleton<IEasyAssets>.Instantiated)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] IEasyAssets not initialized; cannot warm up consumable bundles.");
                    return;
                }
                IEasyAssets ea = Singleton<IEasyAssets>.Instance;
                Plugin.LogSource?.LogInfo($"[SupplyDrop] retaining {RequiredUsePrefabBundles.Length} consumable UsePrefab bundle(s) for crate loot.");
                DependencyGraphClass<IEasyBundle>.GClass1661 handle = ea.Retain(RequiredUsePrefabBundles);
                await GClass1857.LoadBundles(handle);
                Plugin.LogSource?.LogInfo("[SupplyDrop] consumable bundles loaded.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[SupplyDrop] WarmupUsePrefabBundlesAsync threw: {ex.Message}");
            }
        }

        // re-rolls the crate's loot in place: strips everything except the
        // existing TarCoin stack (so the player can't gamble for more coins),
        // then re-runs guaranteed item placement + the random roll loop.
        // called from SupplyDropUnlockPatch's RE-ROLL action.
        //
        // TarCoin preservation: we explicitly skip the SeedTarCoins call
        // here and we don't remove items matching TarCoinTpl in the clear
        // pass. the stack count from the original spawn carries through
        // every re-roll unchanged.
        public static void RerollLoot(Item crateItem, InventoryController inv, int waveCount)
        {
            if (crateItem == null) return;
            CompoundItem compound = crateItem as CompoundItem;
            if (compound?.Grids == null || compound.Grids.Length == 0)
            {
                Plugin.LogSource?.LogWarning("[SupplyDrop] re-roll: crate has no Grids; skipping.");
                return;
            }
            StashGridClass grid = compound.Grids[0];
            if (grid == null) return;

            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            if (factory == null) return;

            // collect non-TarCoin items first - can't remove while iterating
            // the live Items collection.
            List<Item> toRemove = new List<Item>();
            foreach (Item it in grid.Items)
            {
                if (it == null) continue;
                if (it.TemplateId == TarCoinTpl) continue; // preserve the TC stack
                toRemove.Add(it);
            }
            int cleared = 0;
            foreach (Item it in toRemove)
            {
                try
                {
                    var result = grid.RemoveWithoutRestrictions(it);
                    if (!result.Failed) cleared++;
                    else Plugin.LogSource?.LogWarning($"[SupplyDrop] re-roll: failed to remove '{it.TemplateId}': {result.Error}");
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"[SupplyDrop] re-roll: Remove threw for '{it.TemplateId}': {ex.Message}");
                }
            }
            Plugin.LogSource?.LogInfo($"[SupplyDrop] re-roll: cleared {cleared} item(s) (TarCoin stack preserved).");

            // re-seed guaranteed items (milk + MRE), boss weapon, and
            // random rolls. wave count drives the rarity scaling - we use
            // the wave count captured at spawn so the loot tier doesn't
            // shift between unlocks (the re-roll is "same crate, different
            // roll", not "now you're on a later wave").
            //
            // boss weapon IS re-rolled (per user spec) - the existing weapon
            // and its spare mags are cleared above, then a fresh roll picks
            // a new tpl + mag + ammo combo.
            SeedGuaranteedItems(grid, factory, inv);
            BossWeaponLootTable.SeedBossWeapons(grid, factory, inv, waveCount);
            SeedRandomLoot(grid, factory, inv, waveCount);
        }

        // shared random-loot loop, called by both PopulateLoot (first time)
        // and RerollLoot. category caps + safety budget enforce 1-headgear /
        // 1-bodyarmor per crate.
        private static void SeedRandomLoot(StashGridClass grid, ItemFactoryClass factory, InventoryController inv, int waveCount)
        {
            Dictionary<LootCategory, int> seededByCategory = new Dictionary<LootCategory, int>();
            int seeded = 0;
            int safety = LootEntriesPerCrate * 6;

            while (seeded < LootEntriesPerCrate && safety-- > 0)
            {
                LootEntry entry = PickRandom(waveCount);
                if (entry == null) break;

                seededByCategory.TryGetValue(entry.Category, out int curr);
                int cap = CategoryCaps.TryGetValue(entry.Category, out int c) ? c : int.MaxValue;
                if (curr >= cap) continue;

                // skip perk entries whose buff the player already has - no
                // point dropping a Deadshot bottle when the player is already
                // buffed. consumes the roll iteration (counts against safety
                // budget) but doesn't seat anything, so other entries get a
                // shot at the slot.
                if (entry.Category == LootCategory.Perk
                    && !string.IsNullOrEmpty(entry.BuffNameIfPerk)
                    && IsBuffActiveOnMainPlayer(entry.BuffNameIfPerk))
                {
                    continue;
                }

                Item item = BuildEntry(entry, factory, inv);
                if (item == null)
                {
                    Plugin.LogSource?.LogWarning($"[SupplyDrop] failed to build loot entry '{entry.Label}'.");
                    continue;
                }
                var seat = grid.AddAnywhere(item, EErrorHandlingType.Ignore);
                if (seat.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[SupplyDrop] crate full while seating '{entry.Label}': {seat.Error}");
                    break;
                }
                seededByCategory[entry.Category] = curr + 1;
                seeded++;
                Plugin.LogSource?.LogInfo($"[SupplyDrop] seeded '{entry.Label}' into crate (wave={waveCount}, category={entry.Category}).");
            }

            if (seeded < LootEntriesPerCrate)
                Plugin.LogSource?.LogInfo($"[SupplyDrop] crate filled with {seeded}/{LootEntriesPerCrate} items (category caps exhausted available roll space).");
        }

        // returns true if the main player currently has the named stim buff.
        // mirrors the *BuffState.IsBuffActive() pattern used by individual
        // perk patches, but takes a runtime buff name so we don't have to
        // dispatch into per-perk classes from here.
        private static bool IsBuffActiveOnMainPlayer(string buffName)
        {
            try
            {
                if (string.IsNullOrEmpty(buffName)) return false;
                GameWorld gw = Singleton<GameWorld>.Instance;
                var hc = gw?.MainPlayer?.ActiveHealthController;
                if (hc == null) return false;
                string[] names = hc.ActiveBuffsNames();
                if (names == null) return false;
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], buffName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* health controller not ready; treat as inactive */ }
            return false;
        }

        public static void PopulateLoot(Item crateItem, InventoryController inv, int waveCount)
        {
            if (crateItem == null || Entries.Count == 0) return;

            CompoundItem compound = crateItem as CompoundItem;
            if (compound?.Grids == null || compound.Grids.Length == 0)
            {
                Plugin.LogSource?.LogWarning("[SupplyDrop] crate has no Grids; skipping loot population.");
                return;
            }
            StashGridClass grid = compound.Grids[0];
            if (grid == null) return;

            ItemFactoryClass factory = Singleton<ItemFactoryClass>.Instance;
            if (factory == null) return;

            // guaranteed TarCoin stack (every crate). random stack size between
            // TarCoinStackMin and TarCoinStackMax. doesn't count toward the
            // LootEntriesPerCrate random rolls - this is a bonus 6th item.
            // NOTE: only seeded on initial population - re-roll preserves it.
            SeedTarCoins(grid, factory, inv, waveCount);

            // hard-guaranteed items (currently milk + MRE). same "bonus item"
            // treatment as TarCoins - don't roll, don't honor caps, don't
            // count toward the per-crate random roll budget.
            SeedGuaranteedItems(grid, factory, inv);

            // boss-style weapon: guaranteed per crate; rarity scaling controls
            // WHICH weapon (rare combos more likely at high waves), not whether
            // one spawns. seats the weapon + N spare loaded mags in the grid
            // and registers it with BossWeaponRegistry for Max Ammo refills.
            BossWeaponLootTable.SeedBossWeapons(grid, factory, inv, waveCount);

            // random rolls. shared with RerollLoot via SeedRandomLoot.
            SeedRandomLoot(grid, factory, inv, waveCount);
        }

        // mint a TarCoin stack with a random count in [TarCoinStackMin,
        // TarCoinStackMax] and seat it in the crate's grid. logs a warning
        // and proceeds if the crate has no room or the factory create fails.
        //
        // wave-scaled floor: at wave 1 the roll spans [Min, Max] uniformly
        // (preserving original behavior). as the wave count climbs the
        // lower bound lerps up toward 75% of Max, biasing the roll heavily
        // toward higher stacks at late game. uses the same WaveCapForScaling
        // (30) as the rarity table so the two systems stay in lockstep.
        // expected value goes from ~2750 at wave 1 to ~4375 at the cap.
        //
        // NOTE: re-rolling a crate doesn't re-seed TarCoins (RerollLoot
        // skips this step intentionally) so the wave count seen here is
        // only the spawn-time wave, not the wave the player re-rolled at.
        private static void SeedTarCoins(StashGridClass grid, ItemFactoryClass factory, InventoryController inv, int waveCount)
        {
            try
            {
                float t = Mathf.Clamp01((waveCount - 1f) / (float)WaveCapForScaling);
                int dynamicMin = Mathf.RoundToInt(Mathf.Lerp(TarCoinStackMin, TarCoinStackMax * 0.75f, t));
                int count = UnityEngine.Random.Range(dynamicMin, TarCoinStackMax + 1);
                Item coins = factory.CreateItem(((IIdGenerator)inv).NextId, TarCoinTpl, null);
                if (coins == null)
                {
                    Plugin.LogSource?.LogWarning("[SupplyDrop] failed to mint TarCoin stack (item registered server-side?).");
                    return;
                }
                coins.StackObjectsCount = count;
                var seat = grid.AddAnywhere(coins, EErrorHandlingType.Ignore);
                if (seat.Failed)
                {
                    Plugin.LogSource?.LogWarning($"[SupplyDrop] could not seat TarCoin stack: {seat.Error}");
                    return;
                }
                Plugin.LogSource?.LogInfo($"[SupplyDrop] seeded {count} TarCoins into crate (wave={waveCount}, floor={dynamicMin}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[SupplyDrop] SeedTarCoins threw: {ex.Message}");
            }
        }

        // mint and seat every entry in GuaranteedItems. each failure (mint or
        // grid placement) logs and continues to the next - one missing tpl
        // shouldn't block the others.
        private static void SeedGuaranteedItems(StashGridClass grid, ItemFactoryClass factory, InventoryController inv)
        {
            for (int i = 0; i < GuaranteedItems.Length; i++)
            {
                string tpl   = GuaranteedItems[i].Tpl;
                string label = GuaranteedItems[i].Label;
                try
                {
                    Item item = factory.CreateItem(((IIdGenerator)inv).NextId, tpl, null);
                    if (item == null)
                    {
                        Plugin.LogSource?.LogWarning($"[SupplyDrop] failed to mint guaranteed '{label}' ({tpl}); item registered server-side?");
                        continue;
                    }
                    var seat = grid.AddAnywhere(item, EErrorHandlingType.Ignore);
                    if (seat.Failed)
                    {
                        Plugin.LogSource?.LogWarning($"[SupplyDrop] could not seat guaranteed '{label}': {seat.Error}");
                        continue;
                    }
                    Plugin.LogSource?.LogInfo($"[SupplyDrop] seeded guaranteed '{label}' into crate.");
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"[SupplyDrop] SeedGuaranteedItems '{label}' threw: {ex.Message}");
                }
            }
        }

        // wave-scaled weighted random pick. wave count modulates weights:
        // at wave 1 the base weights apply as-is; at WaveCapForScaling+
        // every entry has equal effective weight (rare items boosted to
        // average rate). linear interpolation between those two endpoints
        // toward the pool's average weight.
        public static LootEntry PickRandom(int waveCount)
        {
            if (Entries.Count == 0) return null;

            float t = Mathf.Clamp01((waveCount - 1f) / (float)WaveCapForScaling);

            // pool average (used as the "target" effective weight at full
            // scaling - lifts rare items up while pulling common items down,
            // so the distribution flattens as t -> 1).
            float avg = 0f;
            int n = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                int w = Math.Max(0, Entries[i].Weight);
                avg += w;
                n++;
            }
            if (n == 0) return null;
            avg /= n;

            float totalEffective = 0f;
            float[] effective = new float[Entries.Count];
            for (int i = 0; i < Entries.Count; i++)
            {
                float baseW = Math.Max(0, Entries[i].Weight);
                effective[i] = Mathf.Lerp(baseW, avg, t);
                totalEffective += effective[i];
            }
            if (totalEffective <= 0f) return null;

            float roll = UnityEngine.Random.Range(0f, totalEffective);
            float cum = 0f;
            for (int i = 0; i < Entries.Count; i++)
            {
                cum += effective[i];
                if (roll < cum) return Entries[i];
            }
            return Entries[Entries.Count - 1];
        }

        private static Item BuildEntry(LootEntry entry, ItemFactoryClass factory, InventoryController inv)
        {
            if (entry.Preset == null || entry.Preset.Length == 0)
                return factory.CreateItem(((IIdGenerator)inv).NextId, entry.Tpl, null);

            var byPresetId = new Dictionary<string, Item>(entry.Preset.Length);
            var pending = new List<PresetEntry>(entry.Preset);
            int safety = pending.Count + 2;

            while (pending.Count > 0 && safety-- > 0)
            {
                bool progress = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    PresetEntry p = pending[i];
                    if (p.ParentPresetId == null)
                    {
                        Item root = factory.CreateItem(((IIdGenerator)inv).NextId, p.Tpl, null);
                        if (root == null) return null;
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
                        Plugin.LogSource?.LogWarning($"[SupplyDrop] preset slot '{p.SlotId}' missing on parent tpl {parent.TemplateId}; skipping {p.Tpl}.");
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }
                    Item mod = factory.CreateItem(((IIdGenerator)inv).NextId, p.Tpl, null);
                    if (mod == null)
                    {
                        pending.RemoveAt(i);
                        progress = true;
                        continue;
                    }
                    slot.ChangeContainedItemDirectly(mod);
                    byPresetId[p.PresetId] = mod;
                    pending.RemoveAt(i);
                    progress = true;
                }
                if (!progress) break;
            }

            return byPresetId.TryGetValue("root", out Item rootItem) ? rootItem : null;
        }
    }
}
