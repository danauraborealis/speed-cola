using SPTarkov.Server.Core.Servers;

namespace ManimalSpeedColaMod;

// thin static handoff between the DI-injected ZombieFlagRouter and the
// static-only harmony patch on LocationLifecycleService.EndLocalRaid.
// harmony patch classes can't receive DI directly, so the router captures
// SaveServer on first flag-set and the patch reads it from here.
internal static class ZombieRaidRevertContext
{
    public static SaveServer SaveServer;
}
