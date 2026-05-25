using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // sibling of StgWallbuyMapSpawnConfig but for the grenade-dispenser
    // wallbuy. hardcoded after placement was dialed in. Scale is hardcoded
    // alongside pos/rot because the bundle ships with an unusable default
    // size that needs the 2x bump to be visible.
    public static class NadeWallbuyMapSpawnConfig
    {
        public class Entry
        {
            public HardcodedSetting<bool> Enabled;
            public HardcodedSetting<string> Position;
            public HardcodedSetting<string> Rotation;
            public HardcodedSetting<string> Scale;

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

            // returns the parsed scale, or Vector3.one if missing/unparseable.
            // never returns a zero/negative axis - those would collapse the
            // mesh and break trigger collider math, so a parse to (0,0,0)
            // gets coerced back to Vector3.one.
            public Vector3 GetScaleOrOne()
            {
                if (Scale == null) return Vector3.one;
                if (!MapSpawnConfig.TryParseVec3(Scale.Value, out Vector3 s)) return Vector3.one;
                if (s.x <= 0f || s.y <= 0f || s.z <= 0f) return Vector3.one;
                return s;
            }
        }

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static void Bind(ConfigFile cfg)
        {
            Add("factory4_day", enabled: true, pos: "67.4, 1.6, 12.07", rot: "0, 0, 0", scale: "2, 2, 2");
        }

        private static void Add(string mapId, bool enabled, string pos, string rot, string scale)
        {
            _entries[mapId] = new Entry
            {
                Enabled  = new HardcodedSetting<bool>(enabled),
                Position = new HardcodedSetting<string>(pos),
                Rotation = new HardcodedSetting<string>(rot),
                Scale    = new HardcodedSetting<string>(scale),
            };
        }

        public static Entry GetForMap(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return null;
            return _entries.TryGetValue(mapId, out var e) ? e : null;
        }
    }
}
