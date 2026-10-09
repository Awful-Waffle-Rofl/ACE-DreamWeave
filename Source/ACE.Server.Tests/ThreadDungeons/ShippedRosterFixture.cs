using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The SHIPPED species roster (Content/events/axes/species/*.json), parsed through the same parser production
    /// uses, plus a per-wcid level and health read straight off each member row's own "level" and "health"
    /// fields, so a plan can be built over the real roster without a world database.
    ///
    /// The file level is a faithful stand-in for the live weenie level - see
    /// DungeonRosterBandTests.Every_raw_fragment_rung_finds_a_family_in_the_shipped_species_tables for the
    /// 2026-09-06 join that found zero disagreements. Health is the file's own "health" field, which is what the
    /// roster tooling wrote from the same weenie; it only has to be a stable, plausible denominator here.
    ///
    /// Also hosts <see cref="Fingerprint"/>, a canonical text form of everything a plan DECIDES, used to pin
    /// "this switch off reproduces the pre-change plan byte for byte" against hashes captured from the
    /// unmodified builder (origin/master 2a56f4c31) on 2026-10-08.
    /// </summary>
    internal static class ShippedRosterFixture
    {
        private static IReadOnlyDictionary<string, SpeciesTableDef> tables;
        private static Dictionary<uint, int> levels;
        private static Dictionary<uint, uint> healths;

        /// <summary>The shipped tables, or null when Content/events/axes/species cannot be found from the test assembly.</summary>
        public static IReadOnlyDictionary<string, SpeciesTableDef> Tables
        {
            get
            {
                EnsureLoaded();
                return tables;
            }
        }

        public static int LevelOf(uint wcid)
        {
            EnsureLoaded();
            return levels != null && levels.TryGetValue(wcid, out var level) ? level : 0;
        }

        public static uint HealthOf(uint wcid)
        {
            EnsureLoaded();
            return healths != null && healths.TryGetValue(wcid, out var health) ? health : 0;
        }

        /// <summary>A synthetic any-family dungeon with <paramref name="points"/> curated points and a boss anchor.</summary>
        public static DungeonEntryDef Dungeon(int points) => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, points).Select(i => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = true }).ToList(),
            BossAnchor = new DungeonSpawnPointDef { Cell = 0x01500199u, X = 99, Y = 0, Z = 0, Clearance = 2, Curated = true },
        };

        /// <summary>
        /// Every field of a plan that a draw or a level decision can move, in one canonical string. The stamp
        /// suffix is written ONLY for a non-zero stamp, so a plan with no stamped entry renders exactly as the
        /// pre-reach-up builder's plan did and the captured hashes stay comparable.
        /// </summary>
        public static string Fingerprint(DungeonSpawnPlan plan)
        {
            var sb = new StringBuilder();
            sb.Append("fam=").Append(plan.FamilyId ?? "none");
            sb.Append("|boss=").Append(plan.BossWcid).Append('@').Append(plan.BossLevel);
            sb.Append("|nat=").Append(plan.NaturalBandLow).Append("|eff=").Append(plan.EffectiveBandLow);
            sb.Append("|hpNorm=").Append(plan.HealthNormalizeRatio.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("|hpFloor=").Append(plan.TrashHealthFloor);
            sb.Append("|poolMax=").Append(plan.PoolMaxBase);
            sb.Append("|bossBase=").Append(plan.BossHealthBase.ToString("R", CultureInfo.InvariantCulture));

            foreach (var e in plan.Entries)
            {
                sb.Append("|e").Append(e.Wcid).Append(':').Append((int)e.Role).Append(':').Append(e.Point?.Cell.ToString("X8") ?? "-")
                  .Append(":u").Append(e.UpliftLevel);

                var stamp = StampOf(e);
                if (stamp != 0)
                    sb.Append(":s").Append(stamp);
            }

            foreach (var note in plan.Notes)
                sb.Append("|n:").Append(note);

            return sb.ToString();
        }

        public static string Hash(DungeonSpawnPlan plan)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Fingerprint(plan)));
            return string.Concat(bytes.Take(12).Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// The entry's reach-up stamp, read by reflection so this fixture compiled unchanged against the
        /// pre-reach-up builder when the master hashes were captured. 0 when the property does not exist.
        /// </summary>
        private static int StampOf(DungeonSpawnPlanEntry e)
        {
            var p = typeof(DungeonSpawnPlanEntry).GetProperty("StampLevel");
            return p == null ? 0 : (int)p.GetValue(e);
        }

        private static void EnsureLoaded()
        {
            if (levels != null || tables != null)
                return;

            var dir = FindSpeciesDir();
            if (dir == null)
                return;

            var files = Directory.GetFiles(dir, "*.json")
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (fileName: Path.GetFileName(p), json: File.ReadAllText(p)))
                .ToList();

            var store = WorldEventAxisStore.Parse(null, null, null, null, null, null, null, files);

            var lv = new Dictionary<uint, int>();
            var hp = new Dictionary<uint, uint>();

            foreach (var (_, json) in files)
            {
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("members", out var members))
                    continue;

                foreach (var m in members.EnumerateArray())
                {
                    var wcid = m.GetProperty("wcid").GetUInt32();
                    lv[wcid] = m.TryGetProperty("level", out var l) ? l.GetInt32() : 0;
                    hp[wcid] = m.TryGetProperty("health", out var h) && h.ValueKind == JsonValueKind.Number ? (uint)Math.Max(0, h.GetInt64()) : 0u;
                }
            }

            healths = hp;
            levels = lv;
            tables = store.SpeciesTables;
        }

        private static string FindSpeciesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "events", "axes", "species");

                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.json").Any())
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }
    }
}
