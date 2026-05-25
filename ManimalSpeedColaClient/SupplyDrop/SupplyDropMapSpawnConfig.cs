using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Manimal.SpeedCola
{
    // candidate spawn positions per map for the supply-drop intermission.
    // PickRandomPosition rolls one of the configured spots; empty /
    // unparseable entries are skipped at roll time.
    //
    // hardcoded after positions were dialed in (used to be 8 F12 slots per
    // map under "Supply Drop - <Map>"). retune by editing the literals
    // below + rebuilding the mod.
    public static class SupplyDropMapSpawnConfig
    {
        public class Entry
        {
            public HardcodedSetting<bool> Enabled;
            public HardcodedSetting<string>[] Positions;

            public List<Vector3> GetUsablePositions()
            {
                List<Vector3> list = new List<Vector3>();
                if (Enabled == null || !Enabled.Value) return list;
                if (Positions == null) return list;
                foreach (HardcodedSetting<string> entry in Positions)
                {
                    if (entry == null) continue;
                    string raw = entry.Value;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    if (MapSpawnConfig.TryParseVec3(raw, out Vector3 v))
                        list.Add(v);
                }
                return list;
            }
        }

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static void Bind(ConfigFile cfg)
        {
            Add("factory4_day",
                enabled: true,
                positions: new[]
                {
                    "56.120, 0.298, 52.827",
                    "42.700, 0.297, -52.652",
                    "-5.845, -0.354, -47.246",
                    "16.108, -2.623, -32.750",
                    "63.114, -2.599, 6.438",
                    "46.718, 0.249, 29.195",
                    "22.775, 8.381, 4.682",
                    "-15.997, 1.116, 65.633",
                });
        }

        private static void Add(string mapId, bool enabled, string[] positions)
        {
            HardcodedSetting<string>[] wrapped = new HardcodedSetting<string>[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                wrapped[i] = new HardcodedSetting<string>(positions[i]);

            _entries[mapId] = new Entry
            {
                Enabled = new HardcodedSetting<bool>(enabled),
                Positions = wrapped,
            };
        }

        public static Vector3 PickRandomPosition(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return Vector3.zero;
            if (!_entries.TryGetValue(mapId, out Entry e)) return Vector3.zero;
            List<Vector3> usable = e.GetUsablePositions();
            if (usable.Count == 0) return Vector3.zero;
            return usable[UnityEngine.Random.Range(0, usable.Count)];
        }
    }
}
