using System.Threading.Tasks;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace ManimalSpeedColaMod;

// custom static route the client posts to right before a zombies-mode raid
// starts. body is empty - SPT injects the session id automatically.
//
// also snapshots the profile to disk on flag-receive: in-raid network
// transactions (TarCoin awards/spends, wallbuy dispenses, normal pickups)
// mutate the server's in-memory profile immediately, NOT just at raid end.
// our raid-end skip prevents the EndLocalRaid merge but doesn't undo those
// in-memory mutations. forcing a save here captures the pre-raid state on
// disk; the raid-end patch then reloads from disk to wipe the in-memory
// mutations.
[Injectable]
public class ZombieFlagRouter(
    JsonUtil jsonUtil,
    HttpResponseUtil httpResponseUtil,
    SaveServer saveServer,
    ISptLogger<ZombieFlagRouter> logger)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/manimal/zombies/flag",
                async (url, info, sessionId, output) =>
                {
                    ZombieRaidFlag.Set(sessionId);
                    // stash the SaveServer for the raid-end patch to read.
                    ZombieRaidRevertContext.SaveServer = saveServer;
                    // capture pre-raid state to disk so the raid-end patch
                    // has something clean to reload from.
                    try
                    {
                        await saveServer.SaveProfileAsync(sessionId);
                        logger.Info($"[SpeedCola] zombies raid flagged + pre-raid profile snapshot saved for session {sessionId}.");
                    }
                    catch (System.Exception ex)
                    {
                        logger.Error($"[SpeedCola] pre-raid snapshot save threw: {ex.GetType().Name}: {ex.Message}");
                    }
                    return httpResponseUtil.NullResponse();
                }
            ),
        ]
    )
{ }
