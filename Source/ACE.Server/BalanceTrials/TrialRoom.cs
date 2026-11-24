using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.BalanceTrials
{
    /// <summary>A trial room: a landblock, where the player starts, the boss point, the spawn points trash and elites are re-pointed onto, and an optional box of landblock rows the copy does not load.</summary>
    public sealed record TrialRoom(string Id, ushort Landblock, DungeonSpawnPointDef Start, DungeonSpawnPointDef BossPoint, IReadOnlyList<DungeonSpawnPointDef> Points, EphemeralRowExclusion ExcludeRows);

    /// <summary>The room schema and loader. Closed key set; every point must sit on the room's landblock.</summary>
    public static class TrialRoomLoader
    {
        private static readonly string[] PointKeys = { "cell", "x", "y", "z" };
        private static readonly string[] PointOptionalKeys = { "qw", "qx", "qy", "qz" };

        public static TrialRoom Parse(string json)
        {
            using var doc = TrialJson.ParseDocument(json);
            var root = TrialJson.Obj(doc.RootElement, "room", new[] { "id", "landblock", "start", "points" }, "bossPoint", "excludeRows");

            var id = TrialJson.Str(root["id"], "id");
            if (!TrialJson.IsId(id))
                throw new TrialArchetypeException($"id '{id}' must be lower-case letters, digits and hyphens");

            var landblock = Landblock(root["landblock"], "landblock");

            var start = Point(root["start"], "start", landblock);

            DungeonSpawnPointDef boss = null;
            if (root.TryGetValue("bossPoint", out var bp) && bp.ValueKind != JsonValueKind.Null)
                boss = Point(bp, "bossPoint", landblock);

            var points = new List<DungeonSpawnPointDef>();
            var i = 0;
            foreach (var p in TrialJson.Arr(root["points"], "points"))
                points.Add(Point(p, $"points[{i++}]", landblock));

            EphemeralRowExclusion exclusion = null;
            if (root.TryGetValue("excludeRows", out var ex) && ex.ValueKind != JsonValueKind.Null)
                exclusion = Exclusion(ex, landblock);

            return new TrialRoom(id, landblock, start, boss, points, exclusion);
        }

        public static IReadOnlyList<(TrialRoom Room, string Sha256)> LoadDirectory(string dir)
        {
            if (!Directory.Exists(dir))
                throw new TrialArchetypeException($"room directory '{dir}' does not exist");

            var result = new List<(TrialRoom Room, string Sha256)>();
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file);
                TrialRoom r;
                string sha;
                try
                {
                    r = Parse(text);
                    sha = TrialArchetypeLoader.Sha256(text);
                }
                catch (TrialArchetypeException ex)
                {
                    throw new TrialArchetypeException($"{Path.GetFileName(file)}: {ex.Message}");
                }

                if (!string.Equals(Path.GetFileNameWithoutExtension(file), r.Id, StringComparison.Ordinal))
                    throw new TrialArchetypeException($"{Path.GetFileName(file)}: file name must equal id '{r.Id}'");

                result.Add((r, sha));
            }
            return result;
        }

        /// <summary>
        /// Null when the room can host the scenario; otherwise the refusal. Every non-boss wave count must be at most
        /// the number of spawn points, and a Boss wave needs a boss point.
        /// </summary>
        public static string CheckFits(TrialRoom room, TrialScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(room);
            ArgumentNullException.ThrowIfNull(scenario);

            foreach (var wave in scenario.Waves)
            {
                if (wave.Role == DungeonRole.Boss)
                {
                    if (room.BossPoint == null)
                        return $"room '{room.Id}' has no boss point but scenario '{scenario.Id}' has a Boss wave";
                }
                else if (wave.Count > room.Points.Count)
                {
                    return $"room '{room.Id}' has {room.Points.Count} spawn points but scenario '{scenario.Id}' wants {wave.Count} {wave.Role}";
                }
            }
            return null;
        }

        private static ushort Landblock(JsonElement e, string what)
        {
            var text = TrialJson.Str(e, what);
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && ushort.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lb))
                return lb;
            throw new TrialArchetypeException($"'{what}' must be a hex landblock such as \"0x0150\"");
        }

        private static DungeonSpawnPointDef Point(JsonElement e, string what, ushort landblock)
        {
            var o = TrialJson.Obj(e, what, PointKeys, PointOptionalKeys);

            var cellEl = o["cell"];
            uint cell;
            if (cellEl.ValueKind != JsonValueKind.Number || !cellEl.TryGetUInt32(out cell))
                throw new TrialArchetypeException($"{what} cell must be an unsigned integer");
            if ((cell >> 16) != landblock)
                throw new TrialArchetypeException($"{what} cell 0x{cell:X8} is not on landblock 0x{landblock:X4}");

            var p = new DungeonSpawnPointDef
            {
                Cell = cell,
                X = (float)TrialJson.Finite(o["x"], $"{what} x"),
                Y = (float)TrialJson.Finite(o["y"], $"{what} y"),
                Z = (float)TrialJson.Finite(o["z"], $"{what} z"),
            };
            if (o.TryGetValue("qw", out var qw)) p.QW = (float)TrialJson.Finite(qw, $"{what} qw");
            if (o.TryGetValue("qx", out var qx)) p.QX = (float)TrialJson.Finite(qx, $"{what} qx");
            if (o.TryGetValue("qy", out var qy)) p.QY = (float)TrialJson.Finite(qy, $"{what} qy");
            if (o.TryGetValue("qz", out var qz)) p.QZ = (float)TrialJson.Finite(qz, $"{what} qz");
            return p;
        }

        private static EphemeralRowExclusion Exclusion(JsonElement e, ushort roomLandblock)
        {
            var o = TrialJson.Obj(e, "excludeRows", new[] { "landblock", "minX", "maxX", "minY", "maxY", "minZ", "maxZ" });
            var lb = Landblock(o["landblock"], "excludeRows landblock");
            if (lb != roomLandblock)
                throw new TrialArchetypeException($"excludeRows landblock 0x{lb:X4} must equal the room landblock 0x{roomLandblock:X4}");
            float F(string k) => (float)TrialJson.Finite(o[k], $"excludeRows {k}");
            var ex = new EphemeralRowExclusion(lb, F("minX"), F("maxX"), F("minY"), F("maxY"), F("minZ"), F("maxZ"));
            if (ex.MinX > ex.MaxX || ex.MinY > ex.MaxY || ex.MinZ > ex.MaxZ)
                throw new TrialArchetypeException("excludeRows has a min above its max");
            return ex;
        }
    }
}
