using System.Collections.Generic;

namespace ManimalSpeedColaMod;

// process-wide set of session IDs whose CURRENT raid is a zombies-mode raid.
// added when the client POSTs /manimal/zombies/flag on raid start; removed
// inside the EndLocalRaid harmony patch after we decide to skip the save.
//
// no persistence - if the server restarts mid-raid the flag is lost and the
// raid would save normally. that's acceptable: a server restart already
// disrupts the in-flight raid more than a stale flag would.
internal static class ZombieRaidFlag
{
    private static readonly object _lock = new();
    private static readonly HashSet<string> _flaggedSessionIds = new();

    public static void Set(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_lock) _flaggedSessionIds.Add(sessionId);
    }

    public static bool ConsumeIfSet(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        lock (_lock) return _flaggedSessionIds.Remove(sessionId);
    }

    public static bool IsSet(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        lock (_lock) return _flaggedSessionIds.Contains(sessionId);
    }
}
