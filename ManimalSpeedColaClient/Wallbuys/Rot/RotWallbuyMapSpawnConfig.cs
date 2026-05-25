using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // sibling of UmpWallbuyMapSpawnConfig but for the Rot wallbuy. hardcoded
    // after placement was dialed in.
    public static class RotWallbuyMapSpawnConfig
    {
        public class Entry
        {
            public HardcodedSetting<bool> Enabled;
            public HardcodedSetting<string> Position;
            public HardcodedSetting<string> Rotation;

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
            Add("factory4_day", enabled: true, pos: "28.54, 8.6, -24.82", rot: "0, 90, 0");
        }

        private static void Add(string mapId, bool enabled, string pos, string rot)
        {
            _entries[mapId] = new Entry
            {
                Enabled  = new HardcodedSetting<bool>(enabled),
                Position = new HardcodedSetting<string>(pos),
                Rotation = new HardcodedSetting<string>(rot),
            };
        }

        public static Entry GetForMap(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return null;
            return _entries.TryGetValue(mapId, out var e) ? e : null;
        }
    }
}
