using System.Reflection;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;

namespace ManimalSpeedColaMod;


public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = Manimal.SpeedCola.ModInfo.Guid;
    public override string Name { get; init; } = Manimal.SpeedCola.ModInfo.ServerName;
    public override string Author { get; init; } = Manimal.SpeedCola.ModInfo.Author;
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new(Manimal.SpeedCola.ModInfo.Version);
    public override SemanticVersioning.Range SptVersion { get; init; } = new(Manimal.SpeedCola.ModInfo.SptVersion);
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; } = "";
    public override bool? IsBundleMod { get; init; } = true;
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class ManimalSpeedColaServer(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    ISptLogger<ManimalSpeedColaServer> logger) : IOnLoad
{
    public async Task OnLoad()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        // no-op until db/CustomItems/ + db/CustomLocales/ are populated.
        await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly);
        await wttCommon.CustomLocaleService.CreateCustomLocales(assembly);
        await wttCommon.CustomBuffService.CreateCustomBuffs(assembly);
        await wttCommon.CustomWeaponPresetService.CreateCustomWeaponPresets(assembly);

        // Harmony patch on LocationLifecycleService.EndLocalRaid - skips the
        // raid-end save chain when ZombieRaidFlag is set for the session.
        // see ZombieRaidEndPatch for the prefix.
        try
        {
            Harmony harmony = new Harmony("Manimal.SpeedCola.Server");
            harmony.PatchAll(assembly);
            logger.Info("[SpeedCola] Harmony patches applied (raid-end skip).");
        }
        catch (System.Exception ex)
        {
            logger.Error($"[SpeedCola] Harmony PatchAll threw: {ex.GetType().Name}: {ex.Message}");
        }

        logger.Info($"[SpeedCola] loaded v{Manimal.SpeedCola.ModInfo.Version}");
    }
}
