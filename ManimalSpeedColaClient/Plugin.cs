using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Manimal.SpeedCola.Patches;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // ModInfo is generated from Directory.Build.props - bump the version there.
    [BepInPlugin(ModInfo.Guid, ModInfo.ForgeName, ModInfo.Version)]
    [BepInDependency("com.wtt.commonlib")]
    [BepInDependency("com.wtt.packnstrap")]
    [BepInDependency("com.wtt.contentbackport")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;
        public static Plugin Instance;

        // tpl of the SpeedCola item registered by the server mod (see
        // ServerModFiles/db/CustomItems/SpeedCola.json). used by the buy
        // action to call ItemFactoryClass.CreateItem.
        public const string SpeedColaItemTpl = "7c1a4e9d2f5b3c8a6d0e9f12";

        // tpl of the Juggernog item registered by the server mod (see
        // ServerModFiles/db/CustomItems/Juggernog.json). drives the
        // "juggernog" stimulator buff that JuggernogBuffPatch reads.
        public const string JuggernogItemTpl = "8d2a5f0e3c4b6d9a7e1f2058";
        public const string StaminupItemTpl  = "6a0b8c76682ea02b4ca45908";

        // tpl of the Death Perception item registered by the server mod (see
        // ServerModFiles/db/CustomItems/DeathPerception.json). drives the
        // "deathperception" stimulator buff that DeathPerceptionBuffState
        // probes for to gate the through-wall outline effect.
        public const string DeathPerceptionItemTpl = "4e9c7a3b1f8d2056b9e0c4a7";

        // tpl of the Quick Revive item registered by the server mod (see
        // ServerModFiles/db/CustomItems/QuickRevive.json). drives the
        // "quickrevive" stimulator buff that QuickReviveBuffState probes
        // for to gate the auto-revive on the next fatal hit.
        public const string QuickReviveItemTpl = "5e9f0b1c4d2a3f8e6d1c4b2a";

        // tpl of the Deadshot Daiquiri item registered by the server mod
        // (ServerModFiles/db/CustomItems/DeadshotDaiquiri.json). this perk
        // has NO machine - it's exclusively dropped in supply boxes via
        // SupplyDropLootTable. drives the "deadshotdaiquiri" stimulator
        // buff read by DeadshotDaiquiriBuffState for recoil/ergo/sway.
        public const string DeadshotDaiquiriItemTpl = "9d3b6e1f4a5c7e2b8f3a9d04";

        // tpl of the Double Tap Root Beer item (CustomItems/DoubleTap.json).
        // same shape as Deadshot - supply-drop exclusive, no machine. drives
        // the "doubletap" stimulator buff for +20% fire rate and 1.5x damage.
        public const string DoubleTapItemTpl = "ab4c7e2f5d6b8a3e9f1c5d05";

        // tpl of the Bandolier Bandit item (CustomItems/BandolierBandit.json).
        // supply-drop exclusive, no machine. drives the "bandolierbandit"
        // stimulator buff for +10 mag capacity, 5% per-shot bullet refund,
        // and zero malfunctions on the held weapon.
        public const string BandolierBanditItemTpl = "c8a4f1e3d9b2a5f7e8c4d1a2";

        // master runtime gate. defaults to false; only ever flipped to true
        // by the hideout mattress Sleep action just before it kicks off the
        // Factory practice raid. resets to false on the next hideout entry
        // so it never carries over into raids the player starts normally.
        // intentionally NOT a config entry - the user wants it driven purely
        // by the Sleep flow.
        public static bool ZombiesMode = false;

        // these settings used to be live BepInEx ConfigEntry<T> bound under
        // F12 sections (Audio, Lighting, Interaction - <Perk>, Bundle, Drink,
        // Drink - <Perk>). once the positions/colors/sizes were dialed in
        // we baked the chosen values in as HardcodedSetting<T> so they no
        // longer clutter the F12 panel or the .cfg file. consumer code path
        // is unchanged (still reads .Value / subscribes to SettingChanged);
        // SettingChanged just never fires now, which is correct since the
        // values can't change at runtime.
        public static HardcodedSetting<string> PrefabAssetName;

        // RandomJingle audio behavior. shared by every perk machine instance
        // (SpeedCola/Juggernog/Staminup/DeathPerception read these directly).
        public static HardcodedSetting<float> JingleProximityRadius;
        public static HardcodedSetting<float> JingleMinInterval;
        public static HardcodedSetting<float> JingleMaxInterval;

        // SpeedCola machine light (the "Light" child of the prefab).
        public static HardcodedSetting<Color> LightColor;
        public static HardcodedSetting<float> LightIntensity;
        public static HardcodedSetting<float> LightRange;

        // interaction trigger box overrides per object. each interactable has
        // its own setting so the size/center overrides dont leak between
        // SpeedCola and Wallbuy (etc.). all hardcoded except for the hideout
        // mattress (which is still a live config below).
        public static HardcodedSetting<bool> SpeedColaShowInteractionBounds;
        public static HardcodedSetting<string> SpeedColaInteractionBoxSize;
        public static HardcodedSetting<string> SpeedColaInteractionBoxCenter;

        public static HardcodedSetting<bool> WallbuyShowInteractionBounds;
        public static HardcodedSetting<string> WallbuyInteractionBoxSize;
        public static HardcodedSetting<string> WallbuyInteractionBoxCenter;

        public static HardcodedSetting<bool> UmpWallbuyShowInteractionBounds;
        public static HardcodedSetting<string> UmpWallbuyInteractionBoxSize;
        public static HardcodedSetting<string> UmpWallbuyInteractionBoxCenter;

        public static HardcodedSetting<bool> RotWallbuyShowInteractionBounds;
        public static HardcodedSetting<string> RotWallbuyInteractionBoxSize;
        public static HardcodedSetting<string> RotWallbuyInteractionBoxCenter;

        // STG wallbuy interaction trigger - hardcoded after dial-in.
        public static HardcodedSetting<bool> StgWallbuyShowInteractionBounds;
        public static HardcodedSetting<string> StgWallbuyInteractionBoxSize;
        public static HardcodedSetting<string> StgWallbuyInteractionBoxCenter;

        // BAR wallbuy interaction trigger - same deal as STG, live until dialed.
        public static ConfigEntry<bool> BarWallbuyShowInteractionBounds;
        public static ConfigEntry<string> BarWallbuyInteractionBoxSize;
        public static ConfigEntry<string> BarWallbuyInteractionBoxCenter;

        // Nade (grenade dispenser) wallbuy interaction trigger - hardcoded.
        public static HardcodedSetting<bool> NadeWallbuyShowInteractionBounds;
        public static HardcodedSetting<string> NadeWallbuyInteractionBoxSize;
        public static HardcodedSetting<string> NadeWallbuyInteractionBoxCenter;

        // SKS wallbuy interaction trigger - hardcoded.
        public static HardcodedSetting<bool> SksWallbuyShowInteractionBounds;
        public static HardcodedSetting<string> SksWallbuyInteractionBoxSize;
        public static HardcodedSetting<string> SksWallbuyInteractionBoxCenter;

        // hideout mattress trigger - hardcoded.
        public static HardcodedSetting<bool> MattressShowInteractionBounds;
        public static HardcodedSetting<string> MattressInteractionBoxSize;
        public static HardcodedSetting<string> MattressInteractionBoxCenter;

        // juggernog machine. hardcoded after the placement was dialed in.
        public static HardcodedSetting<bool> JuggernogShowInteractionBounds;
        public static HardcodedSetting<string> JuggernogInteractionBoxSize;
        public static HardcodedSetting<string> JuggernogInteractionBoxCenter;
        public static HardcodedSetting<Color> JuggernogLightColor;
        public static HardcodedSetting<float> JuggernogLightIntensity;
        public static HardcodedSetting<float> JuggernogLightRange;
        public static HardcodedSetting<bool> JuggernogAutoUseAfterBuy;
        public static HardcodedSetting<string> JuggernogPrefabAssetName;

        public static HardcodedSetting<bool> StaminupShowInteractionBounds;
        public static HardcodedSetting<string> StaminupInteractionBoxSize;
        public static HardcodedSetting<string> StaminupInteractionBoxCenter;
        public static HardcodedSetting<Color> StaminupLightColor;
        public static HardcodedSetting<float> StaminupLightIntensity;
        public static HardcodedSetting<float> StaminupLightRange;
        public static HardcodedSetting<bool> StaminupAutoUseAfterBuy;
        public static HardcodedSetting<string> StaminupPrefabAssetName;

        // death perception machine. hardcoded after placement was dialed in.
        public static HardcodedSetting<bool> DeathPerceptionShowInteractionBounds;
        public static HardcodedSetting<string> DeathPerceptionInteractionBoxSize;
        public static HardcodedSetting<string> DeathPerceptionInteractionBoxCenter;
        public static HardcodedSetting<Color> DeathPerceptionLightColor;
        public static HardcodedSetting<float> DeathPerceptionLightIntensity;
        public static HardcodedSetting<float> DeathPerceptionLightRange;
        public static HardcodedSetting<bool> DeathPerceptionAutoUseAfterBuy;
        public static HardcodedSetting<string> DeathPerceptionPrefabAssetName;

        // quick revive machine. hardcoded after placement/light/auto-use
        // were dialed in.
        public static HardcodedSetting<bool> QuickReviveShowInteractionBounds;
        public static HardcodedSetting<string> QuickReviveInteractionBoxSize;
        public static HardcodedSetting<string> QuickReviveInteractionBoxCenter;
        public static HardcodedSetting<Color> QuickReviveLightColor;
        public static HardcodedSetting<float> QuickReviveLightIntensity;
        public static HardcodedSetting<float> QuickReviveLightRange;
        public static HardcodedSetting<bool> QuickReviveAutoUseAfterBuy;
        public static HardcodedSetting<string> QuickRevivePrefabAssetName;
        public static HardcodedSetting<int> QuickReviveMaxUses;
        public static HardcodedSetting<float> QuickReviveDownedDurationSec;

        // drink animation speed multiplier applied via FirearmsAnimator
        // SetUseTimeMultiplier when our SpeedCola item is consumed. 1.0 = base
        // speed, 1.4 = 40% faster, etc. hardcoded.
        public static HardcodedSetting<float> DrinkSpeedMultiplier;

        // Max Ammo power-up drop chance per zombie kill. range 0..1. default
        // 0.005 (0.5%) - balanced for normal play; bump via F12 for testing.
        // read by MaxAmmoSpawner.TryRoll.
        public static ConfigEntry<float> MaxAmmoSpawnChance;

        // Death Perception dev-only flag: force the perk's visual effect
        // (through-wall outlines + HUD arrows) on regardless of whether
        // the buff is actually active. driven by the F8 toggle hotkey
        // below until the drink/machine pipeline is wired up.
        public static bool DeathPerceptionForceActive = false;
        public static ConfigEntry<KeyboardShortcut> DeathPerceptionTestKey;

        // Quick Revive dev-only flag: forces QuickReviveBuffState.IsBuffActive
        // to return true so the auto-revive kill-intercept fires without
        // needing the player to actually have the buff. paired with
        // QuickReviveTestKey (F9 default) to toggle at runtime.
        public static bool QuickReviveForceActive = false;
        public static ConfigEntry<KeyboardShortcut> QuickReviveTestKey;

        // dev hotkey to drop a Bandolier Bandit bottle into the player's
        // equipment cascade so we can test the perk without grinding supply
        // drops. default F8 (the slot freed up when we removed the old
        // freeze-zombies-and-grant-TC toggle).
        public static ConfigEntry<KeyboardShortcut> BandolierBanditTestKey;

        // when true, the bought SpeedCola is auto-consumed the moment it lands
        // in the inventory - the player skips having to find and use it.
        // hardcoded.
        public static HardcodedSetting<bool> AutoUseAfterBuy;

        // applied to weapon SpeedReload / SpeedDraw / SpeedFix while the
        // WeaponSpeedMultiplier used to be a configurable BepInEx entry; it
        // was removed in favour of a hardcoded 1.4x in WeaponSpeedBuffState
        // so the perk has a canonical value across all installs.

        private void Awake()
        {
            LogSource = Logger;
            Instance = this;

            // ---- hardcoded settings (values baked in from the last edits
            // in BepInEx/config/com.manimal.speedcola.cfg). these no longer
            // appear in F12. to retune, edit the literals below + rebuild.

            // bundle prefab name (Unity asset name inside speedcola_machine.bundle)
            PrefabAssetName = new HardcodedSetting<string>("SpeedColar");

            // perk machine jingle behavior (shared by every machine)
            JingleProximityRadius = new HardcodedSetting<float>(15f);
            JingleMinInterval     = new HardcodedSetting<float>(18f);
            JingleMaxInterval     = new HardcodedSetting<float>(50f);

            // SpeedCola machine light. color 009E4EFF (green) + dimmer/wider
            // than the default 2-intensity / 5m range.
            LightColor     = new HardcodedSetting<Color>(new Color(0x00 / 255f, 0x9E / 255f, 0x4E / 255f, 1f));
            LightIntensity = new HardcodedSetting<float>(0.4694829f);
            LightRange     = new HardcodedSetting<float>(6.408455f);

            // interaction boxes (size/center in local space). empty string =
            // fall through to the auto-fit-to-mesh-bounds path in the Instance
            // ApplyInteractionConfig methods.
            SpeedColaShowInteractionBounds   = new HardcodedSetting<bool>(false);
            SpeedColaInteractionBoxSize      = new HardcodedSetting<string>("1.2,1,2.1");
            SpeedColaInteractionBoxCenter    = new HardcodedSetting<string>("0,0,1");

            WallbuyShowInteractionBounds     = new HardcodedSetting<bool>(false);
            WallbuyInteractionBoxSize        = new HardcodedSetting<string>("0.1,0.3,1.2");
            WallbuyInteractionBoxCenter      = new HardcodedSetting<string>("0, 0, 0.08");

            UmpWallbuyShowInteractionBounds  = new HardcodedSetting<bool>(false);
            UmpWallbuyInteractionBoxSize     = new HardcodedSetting<string>("");
            UmpWallbuyInteractionBoxCenter   = new HardcodedSetting<string>("-0.04, -0.05, 0.02");

            RotWallbuyShowInteractionBounds  = new HardcodedSetting<bool>(false);
            RotWallbuyInteractionBoxSize     = new HardcodedSetting<string>("");
            RotWallbuyInteractionBoxCenter   = new HardcodedSetting<string>("");

            // STG wallbuy interaction trigger - hardcoded after dial-in.
            StgWallbuyShowInteractionBounds  = new HardcodedSetting<bool>(false);
            StgWallbuyInteractionBoxSize     = new HardcodedSetting<string>("");
            StgWallbuyInteractionBoxCenter   = new HardcodedSetting<string>("");

            BarWallbuyShowInteractionBounds = Config.Bind(
                "Interaction - BAR Wallbuy",
                "ShowBoundsWireframe",
                false,
                "Draw the BAR wallbuy trigger collider as a green wireframe (visible through walls).");

            BarWallbuyInteractionBoxSize = Config.Bind(
                "Interaction - BAR Wallbuy",
                "BoxSize",
                "",
                "Override the BAR wallbuy trigger size in local-space meters. Format: X, Y, Z. Leave empty to auto-fit.");

            BarWallbuyInteractionBoxCenter = Config.Bind(
                "Interaction - BAR Wallbuy",
                "BoxCenter",
                "",
                "Override the BAR wallbuy trigger center in local space. Format: X, Y, Z. Leave empty to auto-fit.");

            // Nade wallbuy interaction trigger. hardcoded - auto-fit.
            NadeWallbuyShowInteractionBounds = new HardcodedSetting<bool>(false);
            NadeWallbuyInteractionBoxSize    = new HardcodedSetting<string>("");
            NadeWallbuyInteractionBoxCenter  = new HardcodedSetting<string>("");

            // SKS wallbuy interaction trigger. hardcoded - auto-fit.
            SksWallbuyShowInteractionBounds = new HardcodedSetting<bool>(false);
            SksWallbuyInteractionBoxSize    = new HardcodedSetting<string>("");
            SksWallbuyInteractionBoxCenter  = new HardcodedSetting<string>("");

            // hideout mattress interaction trigger. hardcoded.
            MattressShowInteractionBounds = new HardcodedSetting<bool>(false);
            MattressInteractionBoxSize    = new HardcodedSetting<string>("1,2,1");
            MattressInteractionBoxCenter  = new HardcodedSetting<string>("0,0,0.2");

            // SpeedCola drink animation tuning. hardcoded.
            DrinkSpeedMultiplier = new HardcodedSetting<float>(1.4f);
            AutoUseAfterBuy      = new HardcodedSetting<bool>(true);

            // Juggernog machine. interaction box matches SpeedCola; light is
            // FF3D43FF (red) at the same dim intensity/range as SpeedCola.
            JuggernogShowInteractionBounds = new HardcodedSetting<bool>(false);
            JuggernogInteractionBoxSize    = new HardcodedSetting<string>("1.2,1,2.1");
            JuggernogInteractionBoxCenter  = new HardcodedSetting<string>("0,0,1");
            JuggernogLightColor            = new HardcodedSetting<Color>(new Color(0xFF / 255f, 0x3D / 255f, 0x43 / 255f, 1f));
            JuggernogLightIntensity        = new HardcodedSetting<float>(0.4694829f);
            JuggernogLightRange            = new HardcodedSetting<float>(6.408455f);
            JuggernogAutoUseAfterBuy       = new HardcodedSetting<bool>(true);
            JuggernogPrefabAssetName       = new HardcodedSetting<string>("");

            // Staminup machine. interaction left to auto-fit; light is
            // FFC273FF (warm yellow) at default intensity/range.
            StaminupShowInteractionBounds  = new HardcodedSetting<bool>(false);
            StaminupInteractionBoxSize     = new HardcodedSetting<string>("");
            StaminupInteractionBoxCenter   = new HardcodedSetting<string>("");
            StaminupLightColor             = new HardcodedSetting<Color>(new Color(0xFF / 255f, 0xC2 / 255f, 0x73 / 255f, 1f));
            StaminupLightIntensity         = new HardcodedSetting<float>(2f);
            StaminupLightRange             = new HardcodedSetting<float>(5f);
            StaminupAutoUseAfterBuy        = new HardcodedSetting<bool>(true);
            StaminupPrefabAssetName        = new HardcodedSetting<string>("");

            // Death Perception machine. hardcoded after placement was dialed
            // in. light color FF800DFF (warm orange), default intensity/range.
            DeathPerceptionShowInteractionBounds = new HardcodedSetting<bool>(false);
            DeathPerceptionInteractionBoxSize    = new HardcodedSetting<string>("");
            DeathPerceptionInteractionBoxCenter  = new HardcodedSetting<string>("");
            DeathPerceptionLightColor            = new HardcodedSetting<Color>(new Color(0xFF / 255f, 0x80 / 255f, 0x0D / 255f, 1f));
            DeathPerceptionLightIntensity        = new HardcodedSetting<float>(2f);
            DeathPerceptionLightRange            = new HardcodedSetting<float>(5f);
            DeathPerceptionAutoUseAfterBuy       = new HardcodedSetting<bool>(true);
            DeathPerceptionPrefabAssetName       = new HardcodedSetting<string>("");

            // Quick Revive machine. hardcoded after placement + light + drink
            // were dialed in. light color 61C8DAFF (cyan/teal). DownedDurationSec
            // = 4s (player-tuned, was 5s default).
            QuickReviveShowInteractionBounds = new HardcodedSetting<bool>(false);
            QuickReviveInteractionBoxSize    = new HardcodedSetting<string>("");
            QuickReviveInteractionBoxCenter  = new HardcodedSetting<string>("");
            QuickReviveLightColor            = new HardcodedSetting<Color>(new Color(0x61 / 255f, 0xC8 / 255f, 0xDA / 255f, 1f));
            QuickReviveLightIntensity        = new HardcodedSetting<float>(2f);
            QuickReviveLightRange            = new HardcodedSetting<float>(5f);
            QuickReviveAutoUseAfterBuy       = new HardcodedSetting<bool>(true);
            QuickReviveMaxUses               = new HardcodedSetting<int>(3);
            QuickReviveDownedDurationSec     = new HardcodedSetting<float>(4f);
            QuickRevivePrefabAssetName       = new HardcodedSetting<string>("");

            // (WeaponSpeedMultiplier config entry removed - perk effect is now
            // a hardcoded 1.4x in WeaponSpeedBuffState.Multiplier.)

            MapSpawnConfig.Bind(Config);
            WallbuyMapSpawnConfig.Bind(Config);
            UmpWallbuyMapSpawnConfig.Bind(Config);
            RotWallbuyMapSpawnConfig.Bind(Config);
            SupplyDropMapSpawnConfig.Bind(Config);
            JuggernogMapSpawnConfig.Bind(Config);
            StaminupMapSpawnConfig.Bind(Config);
            DeathPerceptionMapSpawnConfig.Bind(Config);
            QuickReviveMapSpawnConfig.Bind(Config);
            StgWallbuyMapSpawnConfig.Bind(Config);
            BarWallbuyMapSpawnConfig.Bind(Config);
            NadeWallbuyMapSpawnConfig.Bind(Config);
            SksWallbuyMapSpawnConfig.Bind(Config);

            new SpawnOnGameStartedPatch().Enable();
            new SpeedColaActionPatch().Enable();
            new DrinkSpeedPatch().Enable();
            new SpeedColaReloadDrawPatch().Enable();
            new SpeedColaMalfRepairPatch().Enable();
            new SpeedColaBuffIconPatch().Enable();
            new SpeedColaEffectIconRegistrationPatch().Enable();

            // Deadshot Daiquiri (supply-drop exclusive perk) - recoil + ergo
            // + ADS-sway buffs. no machine, no action patch; the bottle item
            // comes out of the supply box and applies on drink.
            new DeadshotDaiquiriRecoilPatch().Enable();
            new DeadshotDaiquiriErgoPatch().Enable();
            new DeadshotDaiquiriSwayPatch().Enable();
            new DeadshotDaiquiriBuffIconPatch().Enable();
            new DeadshotDaiquiriEffectIconRegistrationPatch().Enable();

            // Double Tap Root Beer (supply-drop exclusive perk) - fire rate
            // + damage buffs. same supply-only shape as Deadshot.
            new DoubleTapFireRatePatch().Enable();
            new DoubleTapSingleFireRatePatch().Enable();
            new DoubleTapDamagePatch().Enable();
            new DoubleTapBuffIconPatch().Enable();
            new DoubleTapEffectIconRegistrationPatch().Enable();

            // Bandolier Bandit (supply-drop exclusive perk) - mag capacity
            // +10, 5% per-shot bullet refund, no malfunctions.
            new BandolierBanditMagMaxCountPatch().Enable();
            new BandolierBanditMalfunctionPatch().Enable();
            new BandolierBanditOnShotPatch().Enable();
            new BandolierBanditBuffIconPatch().Enable();
            new BandolierBanditEffectIconRegistrationPatch().Enable();
            new SpawnWallbuyOnGameStartedPatch().Enable();
            new Mp43WallbuyActionPatch().Enable();
            new SpawnUmpWallbuyOnGameStartedPatch().Enable();
            new UmpWallbuyActionPatch().Enable();
            new SpawnRotWallbuyOnGameStartedPatch().Enable();
            new RotWallbuyActionPatch().Enable();
            new SpawnStgWallbuyOnGameStartedPatch().Enable();
            new StgWallbuyActionPatch().Enable();
            new SpawnBarWallbuyOnGameStartedPatch().Enable();
            new BarWallbuyActionPatch().Enable();
            new SpawnNadeWallbuyOnGameStartedPatch().Enable();
            new NadeWallbuyActionPatch().Enable();
            new SpawnSksWallbuyOnGameStartedPatch().Enable();
            new SksWallbuyActionPatch().Enable();
            new HideoutMattressDiscoveryPatch().Enable();
            new HideoutMattressActionPatch().Enable();
            new ZombiesLoadoutPatch().Enable();
            new ZombiesBotSpawnPatch().Enable();
            new ZombieClampedSpeedPatch().Enable();
            new SpawnJuggernogOnGameStartedPatch().Enable();
            new JuggernogActionPatch().Enable();
            new JuggernogApplyDamagePatch().Enable();
            new JuggernogPainSuppressPatch().Enable();
            new JuggernogPainkillerStatePatch().Enable();
            new JuggernogBuffIconPatch().Enable();
            new JuggernogEffectIconRegistrationPatch().Enable();
            new SpawnStaminupOnGameStartedPatch().Enable();
            new StaminupActionPatch().Enable();
            new StaminupBuffIconPatch().Enable();
            new StaminupEffectIconRegistrationPatch().Enable();
            new SpawnDeathPerceptionOnGameStartedPatch().Enable();
            new DeathPerceptionActionPatch().Enable();
            new DeathPerceptionBuffIconPatch().Enable();
            new DeathPerceptionEffectIconRegistrationPatch().Enable();
            new SpawnQuickReviveOnGameStartedPatch().Enable();
            new QuickReviveActionPatch().Enable();
            new QuickReviveKillInterceptPatch().Enable();
            new QuickReviveBuffIconPatch().Enable();
            new QuickReviveEffectIconRegistrationPatch().Enable();
            new ZombiesNoEscapePatch().Enable();
            new ZombiesSuppressWildWavesPatch().Enable();
            new ZombiesSuppressBossWavesPatch().Enable();
            new ZombiesSuppressEventStartWavesPatch().Enable();
            new ZombiesWaveSpawnerPatch().Enable();
            new TarCoinScorePatch().Enable();
            new ZombieInfectionSuppressPatch().Enable();
            new SupplyDropUnlockPatch().Enable();
            new RainCondensatorNullGuardPatch().Enable();

            MaxAmmoSpawnChance = Config.Bind(
                "Power-ups - Max Ammo",
                "SpawnChance",
                0.005f,
                new ConfigDescription(
                    "Probability that a killed zombie drops a Max Ammo power-up. 0 = never, 1 = every kill. Default 0.005 (0.5%).",
                    new AcceptableValueRange<float>(0f, 1f)));

            DeathPerceptionTestKey = Config.Bind(
                "Debug",
                "DeathPerceptionTestKey",
                new KeyboardShortcut(KeyCode.F7),
                "Press to toggle the Death Perception visual effect on/off for testing (forces buff-active regardless of actual buff state). Default: F7.");

            BandolierBanditTestKey = Config.Bind(
                "Debug",
                "BandolierBanditTestKey",
                new KeyboardShortcut(KeyCode.F8),
                "Press to drop one Bandolier Bandit bottle into your equipment cascade (rig -> pockets -> belt -> backpack) for testing. Default: F8.");

            QuickReviveTestKey = Config.Bind(
                "Debug",
                "QuickReviveTestKey",
                new KeyboardShortcut(KeyCode.F9),
                "Press to toggle Quick Revive force-active on/off (arms a charge each toggle-on so you can test the auto-revive without buying the drink). Default: F9.");

            LogSource.LogInfo($"SpeedCola loaded v{ModInfo.Version}");
        }

        // dev hotkey polled here so we don't need a separate MonoBehaviour
        // host. Plugin already inherits MonoBehaviour via BaseUnityPlugin,
        // so its Update runs every frame regardless of raid/menu state.
        //
        // debounce: BepInEx plugin Update can fire more than once per input
        // frame in some setups (multiple plugin contexts polling the same
        // Input state), which causes IsDown() to return true twice and the
        // toggle to flip on-then-off in a single keypress. ignoring presses
        // within 200ms of the last accepted one collapses those duplicates.
        private float _lastDpToggleTime = -10f;
        private float _lastQrToggleTime = -10f;
        private float _lastBandolierTestTime = -10f;
        private void Update()
        {
            try
            {
                // pre-register every perk's HUD buff icon as soon as
                // EFTHardSettings.Instance is available. otherwise the HUD
                // strip renders a blank icon until the player opens the
                // inventory health screen for the first time (which is the
                // only thing that fires EffectsPanel.Show, the
                // *EffectIconRegistrationPatch hook). each EnsureRegistered
                // is idempotent + cheap once latched, so polling every frame
                // is fine - the early-return on `_registeredCustom` makes
                // the call essentially a single dictionary check.
                SpeedColaEffectIconRegistrationPatch.EnsureRegistered();
                JuggernogEffectIconRegistrationPatch.EnsureRegistered();
                StaminupEffectIconRegistrationPatch.EnsureRegistered();
                DeathPerceptionEffectIconRegistrationPatch.EnsureRegistered();
                QuickReviveEffectIconRegistrationPatch.EnsureRegistered();
                DeadshotDaiquiriEffectIconRegistrationPatch.EnsureRegistered();
                DoubleTapEffectIconRegistrationPatch.EnsureRegistered();
                BandolierBanditEffectIconRegistrationPatch.EnsureRegistered();

                // Bandolier Bandit: when the buff is active, detect mag-swap
                // events (reload, weapon swap) and top the freshly-loaded
                // in-weapon mag to the buffed +10 ceiling. cheap when the
                // buff isn't active (single ActiveBuffsNames check, then
                // early return).
                BandolierBanditTopUpTick.Tick();

                // tick the QR downed-state machine. cheap when not downed
                // (single bool early-return); while downed, re-applies pose/
                // speed/awareness locks + re-clears zombie pursuit each
                // frame and fires the self-revive when the timer expires.
                QuickReviveDownedState.Tick();

                // DP toggle (default F7).
                if (DeathPerceptionTestKey != null && DeathPerceptionTestKey.Value.IsDown()
                    && Time.unscaledTime - _lastDpToggleTime >= 0.2f)
                {
                    _lastDpToggleTime = Time.unscaledTime;
                    DeathPerceptionForceActive = !DeathPerceptionForceActive;
                    LogSource?.LogInfo($"[DeathPerception] force-active toggled to {DeathPerceptionForceActive}.");
                }

                // QR toggle (default F9). on toggle-on, also arm a charge
                // so the next fatal hit actually triggers the auto-revive
                // (the kill-intercept needs BOTH buff-active and a charge).
                if (QuickReviveTestKey != null && QuickReviveTestKey.Value.IsDown()
                    && Time.unscaledTime - _lastQrToggleTime >= 0.2f)
                {
                    _lastQrToggleTime = Time.unscaledTime;
                    QuickReviveForceActive = !QuickReviveForceActive;
                    if (QuickReviveForceActive) QuickReviveState.GrantCharge();
                    LogSource?.LogInfo($"[QuickRevive] force-active toggled to {QuickReviveForceActive} (charge {(QuickReviveState.HasCharge ? "armed" : "cleared")}).");
                }

                // Bandolier Bandit drop (default F8): mints one bottle and
                // seats it via the rig -> pockets -> belt -> backpack cascade.
                // for testing the perk without waiting on a supply drop.
                if (BandolierBanditTestKey != null && BandolierBanditTestKey.Value.IsDown()
                    && Time.unscaledTime - _lastBandolierTestTime >= 0.2f)
                {
                    _lastBandolierTestTime = Time.unscaledTime;
                    EFT.Player main = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer;
                    string result = TryDropBandolierBottle(main);
                    LogSource?.LogInfo($"[BandolierBandit-Test] drop result: {result}");
                }
            }
            catch (System.Exception ex)
            {
                LogSource?.LogError($"[Plugin] Update threw:\n{ex}");
            }
        }

        // shared helper for the F8 Bandolier Bandit test hotkey: create one
        // BandolierBanditItemTpl and seat it via the ZombiesLoadoutPatch
        // rig -> pockets -> belt -> backpack cascade. returns a short status
        // string for the log line - "placed", "no-room", "create-failed", etc.
        // also fire-and-forget triggers a UsePrefab bundle preload so the
        // drink animation works immediately even if no supply drop has
        // warmed up the bundle yet.
        private static string TryDropBandolierBottle(EFT.Player main)
        {
            if (main == null) return "(no main player)";
            try
            {
                var factory = Comfort.Common.Singleton<ItemFactoryClass>.Instance;
                var inv     = main.InventoryController;
                if (factory == null || inv == null) return "factory-or-inv-null";

                var bottle = factory.CreateItem(((IIdGenerator)inv).NextId, BandolierBanditItemTpl, null);
                if (bottle == null) return "create-failed (server-side tpl registered?)";

                bottle.SpawnedInSession = true;

                // belt-and-suspenders bundle preload. ZombiesWaveController.Start()
                // already kicks off the supply-drop UsePrefab warmup at raid
                // start, but if the player F8's before the zombies controller
                // attaches, this catches the race. refcounted: duplicate
                // retains are free.
                _ = WallbuyBundleLoader.EnsureItemBundleLoaded(bottle);

                bool placed = Manimal.SpeedCola.Patches.ZombiesLoadoutPatch.TryPlaceItemAcrossEquipment(
                    inv.Inventory.Equipment, bottle);
                return placed ? "placed" : "no-room";
            }
            catch (System.Exception ex) { return $"threw:{ex.Message}"; }
        }
    }
}
