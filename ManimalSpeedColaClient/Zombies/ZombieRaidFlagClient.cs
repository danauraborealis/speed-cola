using System;
using SPT.Common.Http;

namespace Manimal.SpeedCola.Patches
{
    // tiny wrapper around SPT.Common.Http.RequestHandler that fires-and-
    // forgets a POST to /manimal/zombies/flag when the player enters a
    // zombies-mode raid. the server's ZombieFlagRouter picks it up and
    // adds the session to ZombieRaidFlag; the server's harmony patch on
    // LocationLifecycleService.EndLocalRaid then skips raid bookkeeping
    // for that session.
    //
    // RequestHandler injects the SPT session cookie automatically, so we
    // don't need to send the profile id - the server gets it from the
    // authenticated request context.
    internal static class ZombieRaidFlagClient
    {
        public static void SignalZombiesRaid()
        {
            try
            {
                // empty json body. server's RouteAction<EmptyRequestData>
                // tolerates anything that deserializes to EmptyRequestData.
                RequestHandler.PostJson("/manimal/zombies/flag", "{}");
                Plugin.LogSource?.LogInfo("[TarCoin] zombies-raid flag posted to server.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[TarCoin] zombies-raid flag post failed: {ex.Message}");
            }
        }
    }
}
