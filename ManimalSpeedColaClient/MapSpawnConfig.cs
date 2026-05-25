using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // per-map spawn data for the Speed Cola machine. used to be BepInEx
    // ConfigEntry<T>'s with each map as its own F12 section, but the values
    // were dialed in and no longer need editing - now seeded as
    // HardcodedSetting<T> with the final positions baked in. consumer code
    // path (TryGetTransform / TryParsePosRot / SettingChanged subscribe) is
    // unchanged; SettingChanged just never fires anymore.
    //
    // map keys mirror BSG/SPT location ids. lookup is case-insensitive because
    // we are not 100% sure which casing GameWorld/Player surfaces at runtime.
    public static class MapSpawnConfig
    {
        public class Entry
        {
            public HardcodedSetting<bool> Enabled;
            public HardcodedSetting<string> Position;   // "x, y, z"
            public HardcodedSetting<string> Rotation;   // euler "x, y, z"

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
                if (!TryParseVec3(Position.Value, out pos)) return false;
                if (!TryParseVec3(Rotation.Value, out Vector3 eul)) return false;
                rot = Quaternion.Euler(eul);
                return true;
            }
        }

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static void Bind(ConfigFile cfg)
        {
            // cfg param kept for call-site compatibility but ignored - the
            // map spawns are no longer surfaced in F12. retune positions by
            // editing the literals below + rebuilding the mod.
            Add("factory4_day", enabled: true, pos: "21.6, 0, -40",        rot: "-90, 0, 90");
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

        public static bool TryParseVec3(string s, out Vector3 v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string[] parts = s.Split(',');
            if (parts.Length != 3) return false;

            var inv = CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, inv, out float x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, inv, out float y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, inv, out float z)) return false;

            v = new Vector3(x, y, z);
            return true;
        }
    }
}
