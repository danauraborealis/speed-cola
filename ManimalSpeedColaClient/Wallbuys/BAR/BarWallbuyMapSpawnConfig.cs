using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // sibling of UmpWallbuyMapSpawnConfig but for the BAR wallbuy. live
    // in F12 (still being dialed in) - once placement is final, demote
    // to HardcodedSetting<T> like the other wallbuys.
    public static class BarWallbuyMapSpawnConfig
    {
        public class Entry
        {
            public ConfigEntry<bool> Enabled;
            public ConfigEntry<string> Position;
            public ConfigEntry<string> Rotation;

            public bool TryGetTransform(out Vector3 pos, out Quaternion rot)
            {
                pos = default;
                rot = Quaternion.identity;
                if (Enabled == null || !Enabled.Value) return false;
                return TryParsePosRot(out pos, out rot);
            }

            public bool TryParsePosRot(out Vector3 pos, out Quaternion rot)
            {
                pos = default;
                rot = Quaternion.identity;
                if (!MapSpawnConfig.TryParseVec3(Position.Value, out pos)) return false;
                if (!MapSpawnConfig.TryParseVec3(Rotation.Value, out Vector3 eul)) return false;
                rot = Quaternion.Euler(eul);
                return true;
            }
        }

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static void Bind(ConfigFile cfg)
        {
            Add(cfg, "BAR Wallbuy - Factory Day", "factory4_day");
        }

        private static void Add(ConfigFile cfg, string section, string mapId)
        {
            var entry = new Entry
            {
                Enabled = cfg.Bind(section, "Enabled", true,
                    $"Spawn the BAR wallbuy on this map. Map id: {mapId}"),
                Position = cfg.Bind(section, "Position", "0, 0, 0",
                    "World position to spawn at. Format: X, Y, Z (meters)."),
                Rotation = cfg.Bind(section, "Rotation", "0, 0, 0",
                    "Euler rotation in degrees. Format: X, Y, Z."),
            };
            _entries[mapId] = entry;
        }

        public static Entry GetForMap(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return null;
            return _entries.TryGetValue(mapId, out var e) ? e : null;
        }
    }
}
