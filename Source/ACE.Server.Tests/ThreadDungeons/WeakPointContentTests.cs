using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The shipped Content/dungeons/dynamic/weak-points.json against the shipped species roster and the offline roster
    /// fixture (Fixtures/weak-point-roster.tsv, written by <c>ACE.Content.Tools weakpoint-census --write-fixture</c>).
    /// No PropertyManager and no database.
    /// </summary>
    [TestClass]
    public class WeakPointContentTests
    {
        private const string Regenerate = "regenerate it with: ACE.Content.Tools weakpoint-census --write-fixture";

        private sealed class FixtureRow
        {
            public int Hittable;
            public int[] Dominant = new int[3];
        }

        private static string FindUp(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            Assert.Fail($"could not find {relative} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        private static Dictionary<uint, FixtureRow> LoadFixture()
        {
            var rows = new Dictionary<uint, FixtureRow>();

            foreach (var line in File.ReadAllText(FindUp("Source/ACE.Server.Tests/ThreadDungeons/Fixtures/weak-point-roster.tsv")).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var f = line.Split('\t');
                var row = new FixtureRow { Hittable = int.Parse(f[1], CultureInfo.InvariantCulture) };

                for (var i = 0; i < 3; i++)
                    row.Dominant[i] = int.Parse(f[2 + i], CultureInfo.InvariantCulture);

                rows[uint.Parse(f[0], CultureInfo.InvariantCulture)] = row;
            }

            return rows;
        }

        private static ThreadDungeonStore LoadStore()
            => ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}",
                new Dictionary<string, string>(), weakPointsJson: File.ReadAllText(FindUp("Content/dungeons/dynamic/weak-points.json")));

        /// <summary>Family id to its distinct role 0/1/2 wcids.</summary>
        private static Dictionary<string, List<uint>> Families()
        {
            var tables = ShippedRosterFixture.Tables;
            Assert.IsNotNull(tables, "the shipped species tables could not be found");

            var result = new Dictionary<string, List<uint>>(StringComparer.Ordinal);

            foreach (var table in tables.Values)
            {
                var id = table.Id;

                if (!result.TryGetValue(id, out var list))
                    result[id] = list = new List<uint>();

                foreach (var m in table.Members.Where(m => m.Role >= 0 && m.Role <= 2).Where(m => !list.Contains(m.Wcid)))
                    list.Add(m.Wcid);
            }

            return result;
        }

        private static AttackHeight? HeightOf(string token)
        {
            Assert.IsTrue(DungeonWeakPoint.TryParseHeight(token, out var height), $"unparseable height '{token}'");
            return height;
        }

        /// <summary>Check (b): every non-random height or bossHeight is supported by at least half of the family's members.</summary>
        private static List<string> SupportProblems(IReadOnlyDictionary<string, WeakPointFamilyDef> rows, Dictionary<string, List<uint>> families, Dictionary<uint, FixtureRow> fixture)
        {
            var problems = new List<string>();

            foreach (var row in rows.Values.Where(r => families.ContainsKey(r.Family)))
            {
                var members = families[row.Family];

                foreach (var (field, token) in new[] { ("height", row.Height), ("bossHeight", row.BossHeight) })
                {
                    var height = token == null ? null : HeightOf(token);

                    if (height == null)
                        continue;

                    var supporting = members.Count(w => fixture.TryGetValue(w, out var r) && r.Dominant[(int)height.Value - 1] > 0);

                    if (supporting * 2 < members.Count)
                        problems.Add($"family '{row.Family}' {field} {token}: {supporting} of {members.Count} members support it");
                }
            }

            return problems;
        }

        [TestMethod]
        public void Every_species_family_has_an_entry_and_no_entry_names_an_unknown_family()
        {
            var families = Families();
            var store = LoadStore();

            var missing = families.Keys.Where(id => !store.WeakPoints.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var unknown = store.WeakPoints.Keys.Where(id => !families.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();

            Assert.AreEqual(0, missing.Count, "families with no entry: " + string.Join(", ", missing));
            Assert.AreEqual(0, unknown.Count, "entries naming no species-table family: " + string.Join(", ", unknown));
        }

        [TestMethod]
        public void Every_non_random_height_is_supported_by_at_least_half_of_the_family()
        {
            var problems = SupportProblems(LoadStore().WeakPoints, Families(), LoadFixture());

            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        [TestMethod]
        public void The_support_check_fails_when_a_family_is_given_a_height_it_lacks()
        {
            // Positive control: carenzi has no part dominant at Medium (members 19, supporting 0 in the census), so
            // switching it to medium must trip check (b). The shipped table is copied, never edited.
            var families = Families();
            var fixture = LoadFixture();
            var rows = LoadStore().WeakPoints.ToDictionary(kv => kv.Key, kv => kv.Value);

            Assert.IsTrue(rows.ContainsKey("carenzi"), "guard: carenzi is in the shipped table");
            Assert.AreEqual(0, SupportProblems(rows, families, fixture).Count, "guard: the shipped table passes before the mutation");

            rows["carenzi"] = new WeakPointFamilyDef { Family = "carenzi", Height = "medium" };

            var problems = SupportProblems(rows, families, fixture);

            Assert.IsTrue(problems.Any(p => p.Contains("carenzi")), "check (b) did not notice carenzi at medium: " + string.Join("\n", problems));
        }

        [TestMethod]
        public void Every_roster_wcid_has_a_fixture_row()
        {
            var fixture = LoadFixture();
            var missing = Families().Values.SelectMany(l => l).Distinct().Where(w => !fixture.ContainsKey(w)).OrderBy(w => w).ToList();

            Assert.AreEqual(0, missing.Count, $"roster wcids with no weak-point-roster.tsv row ({Regenerate}): " + string.Join(", ", missing.Take(40)));
        }

        [TestMethod]
        public void The_shipped_file_loads_with_no_diagnostics()
        {
            var own = LoadStore().Diagnostics.Where(m => m.StartsWith("weak-points.json:")).ToList();

            Assert.AreEqual(0, own.Count, string.Join("\n", own));
        }
    }
}
