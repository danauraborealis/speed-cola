using HarmonyLib;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Services;

namespace ManimalSpeedColaMod;

// patch LocationLifecycleService.EndLocalRaid - the single chokepoint that
// runs at raid-end for both PMC and Scav. when our session-flag is set we:
//   1. short-circuit the original method - no inventory merge, no XP/quest
//      bookkeeping, no save-to-disk via the vanilla path.
//   2. reload the profile from disk - wipes any in-memory mutations made
//      during the raid by network transactions (TarCoin awards/spends,
//      wallbuy/perk dispenses, in-raid pickups). disk holds the pre-raid
//      snapshot taken by ZombieFlagRouter when the client flagged the raid.
//
// without the reload, the server's in-memory profile still carries
// in-raid mutations (and references to the temporary TraderControllerClass
// used by the dispense fake-stash flow), which produces "Unable to edit a
// traders item" errors and ghosted items in the post-raid hideout.
[HarmonyPatch(typeof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaid))]
internal static class ZombieRaidEndPatch
{
    [HarmonyPrefix]
    private static bool Prefix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        string sid = sessionId.ToString();
        if (!ZombieRaidFlag.ConsumeIfSet(sid))
        {
            // not a zombies raid - let the normal pipeline run.
            return true;
        }

        // reload the pre-raid profile from disk to wipe in-memory raid
        // mutations. blocking wait so the menu doesn't see a half-reverted
        // profile.
        try
        {
            if (ZombieRaidRevertContext.SaveServer != null)
            {
                ZombieRaidRevertContext.SaveServer.LoadProfileAsync(sessionId).GetAwaiter().GetResult();
            }
        }
        catch (System.Exception)
        {
            // best-effort; if reload fails the profile-disk state is still
            // pre-raid and the next session/load will pick it up. swallow
            // to keep the response 200.
        }
        return false;
    }
}
