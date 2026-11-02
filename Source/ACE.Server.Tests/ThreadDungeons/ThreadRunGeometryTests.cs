using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads R23: ThreadRunGeometry is the one answer to "is this position on the run's landblock(s)".
    /// A dungeon run owns its one landblock; an outdoor run owns every footprint landblock and nothing else (the
    /// buffer ring is never on the run). The source-scan tripwire at the bottom keeps every other check in
    /// ThreadDungeons routed through the helper.
    /// </summary>
    [TestClass]
    public class ThreadRunGeometryTests
    {
        private const ushort Primary = 0x5456;
        private const ushort East = 0x5556;     // footprint member, east of the primary
        private const ushort North = 0x5457;    // buffer: adjacent to the footprint, not in it
        private const ushort West = 0x5356;     // buffer on the other side
        private const uint Instance = 0x80001234u;

        private static DungeonEntryDef DungeonRow(ushort landblock = 0x0150)
            => new DungeonEntryDef { Id = "filos_doom", Landblock = landblock, ExitPortalWcid = 1003601 };

        private static DungeonEntryDef OutdoorRow(params ushort[] footprint)
            => new DungeonEntryDef
            {
                Id = "ow_example",
                Kind = DungeonEntryDef.KindOutdoor,
                Landblock = Primary,
                ExitPortalWcid = 1003604,
                Outdoor = new OutdoorSiteDef { Primary = Primary, Footprint = footprint.ToList() },
            };

        private static ThreadDungeonRun Run(DungeonEntryDef dungeon)
        {
            var spec = new DungeonGemSpec(dungeon.Id, 100, 6, "any", 1, new (string, double)[0], 0, 0);
            return new ThreadDungeonRun(Instance, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        private static Position At(ushort landblock, float x = 96f, float y = 96f)
            => new Position(((uint)landblock << 16) | 0x0001u, x, y, 10f, 0f, 0f, 0f, 1f, Instance);

        [TestMethod]
        public void A_dungeon_run_owns_its_primary_only()
        {
            var run = Run(DungeonRow(0x0150));

            Assert.IsTrue(ThreadRunGeometry.IsOnRunLandblock(run, At(0x0150)));
            Assert.IsFalse(ThreadRunGeometry.IsOnRunLandblock(run, At(0x0151)), "a neighbouring landblock is not the dungeon");
            CollectionAssert.AreEqual(new ushort[] { 0x0150 }, ThreadRunGeometry.RunLandblocks(run.Dungeon).ToArray());
        }

        [TestMethod]
        public void An_outdoor_run_owns_every_footprint_member()
        {
            var run = Run(OutdoorRow(Primary, East));

            Assert.IsTrue(ThreadRunGeometry.IsOnRunLandblock(run, At(Primary)), "the primary");
            Assert.IsTrue(ThreadRunGeometry.IsOnRunLandblock(run, At(East)), "a footprint member other than the primary");
        }

        [TestMethod]
        public void A_buffer_landblock_is_not_on_the_run()
        {
            var run = Run(OutdoorRow(Primary, East));

            Assert.IsFalse(ThreadRunGeometry.IsOnRunLandblock(run, At(North)), "adjacent to the footprint, but bare buffer terrain");
            Assert.IsFalse(ThreadRunGeometry.IsOnRunLandblock(run, At(West)), "adjacent on the other side");
            Assert.IsFalse(ThreadRunGeometry.IsOnRunLandblock(run, At(0x6060)), "far away");
        }

        [TestMethod]
        public void Only_an_outdoor_row_reads_its_footprint()
        {
            // A dungeon-kind row that somehow carries an outdoor footprint still owns its one landblock: the kind decides.
            var row = DungeonRow(Primary);
            row.Outdoor = new OutdoorSiteDef { Primary = Primary, Footprint = new List<ushort> { Primary, East } };

            Assert.IsTrue(ThreadRunGeometry.IsRunLandblock(row, Primary));
            Assert.IsFalse(ThreadRunGeometry.IsRunLandblock(row, East));
            CollectionAssert.AreEqual(new[] { Primary }, ThreadRunGeometry.RunLandblocks(row).ToArray());
        }

        [TestMethod]
        public void Run_landblocks_list_the_primary_first_then_each_member_once()
        {
            // The footprint lists the member first and repeats nothing; the primary still leads.
            var row = OutdoorRow(East, Primary, 0x5557);

            CollectionAssert.AreEqual(new ushort[] { Primary, East, 0x5557 }, ThreadRunGeometry.RunLandblocks(row).ToArray());
        }

        [TestMethod]
        public void Null_inputs_are_never_on_the_run()
        {
            Assert.IsFalse(ThreadRunGeometry.IsOnRunLandblock(null, At(Primary)));
            Assert.IsFalse(ThreadRunGeometry.IsOnRunLandblock(Run(OutdoorRow(Primary)), null));
            Assert.IsFalse(ThreadRunGeometry.IsRunLandblock(null, Primary));
            Assert.AreEqual(0, ThreadRunGeometry.RunLandblocks(null).Count);
        }

        [TestMethod]
        public void The_loaded_kind_check_is_inverted_for_outdoor_rows()
        {
            Assert.IsTrue(ThreadRunGeometry.LoadedKindMatches(rowIsOutdoor: false, landblockIsDungeon: true), "a dungeon row on a dungeon landblock");
            Assert.IsFalse(ThreadRunGeometry.LoadedKindMatches(rowIsOutdoor: false, landblockIsDungeon: false), "a dungeon row must never instance an overworld landblock");
            Assert.IsTrue(ThreadRunGeometry.LoadedKindMatches(rowIsOutdoor: true, landblockIsDungeon: false), "an outdoor row on an outdoor landblock");
            Assert.IsFalse(ThreadRunGeometry.LoadedKindMatches(rowIsOutdoor: true, landblockIsDungeon: true), "an outdoor row on a dungeon landblock is a content error");
        }

        [TestMethod]
        public void Presence_is_footprint_aware_through_the_helper()
        {
            // ThreadRunPresence.IsMemberInside needs a Player, which the harness cannot build; its location clause is
            // the helper, pinned here so the presence path cannot fall back to a single-landblock comparison.
            var presence = PooledLootSourceText.ExpressionBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadRunPresence.cs"),
                "public static bool IsMemberInside(ThreadDungeonRun run, Player player)");
            StringAssert.Contains(presence, "player.Location.Instance == run.Instance");
            StringAssert.Contains(presence, "ThreadRunGeometry.IsOnRunLandblock(run, player.Location)");
        }

        // ---- source-scan tripwire ----

        /// <summary>
        /// Deny by default (code review F3): EVERY read of a dungeon row's Landblock in ThreadDungeons is an offender, so an
        /// alias (var lb = run.Dungeon.Landblock), an .Equals, or a comparison split over lines cannot slip past a pattern
        /// that only knew "==". The only reads allowed are this explicit list, removed before the scan:
        ///   1. building a LandblockId from it (new LandblockId((uint)dungeon.Landblock &lt;&lt; 16));
        ///   2. log text: an interpolation hole formatted as hex ({run.Dungeon.Landblock:X4});
        ///   3. the dungeon walk graph lookup (WalkGraphSource(run.Dungeon.Landblock)), which is dungeon-only by its own guard;
        ///   4. the "no landblock" validity test against the literal 0 (== 0 only; any other literal is flagged).
        /// The helper file itself (ThreadRunGeometry.cs) is the one place allowed to compare.
        /// </summary>
        private static readonly Regex DungeonLandblockRead = new Regex(@"[Dd]ungeon\??\.Landblock\b", RegexOptions.Compiled);

        private static readonly Regex[] AllowedReads =
        {
            new Regex(@"new\s+LandblockId\s*\(\s*\(uint\)\s*[\w\.\?]*[Dd]ungeon\??\.Landblock\s*<<\s*16\s*\)", RegexOptions.Compiled),
            new Regex(@"\{\s*[\w\.\?]*[Dd]ungeon\??\.Landblock\s*:X4\s*\}", RegexOptions.Compiled),
            new Regex(@"WalkGraphSource\s*\(\s*[\w\.\?]*[Dd]ungeon\??\.Landblock\s*\)", RegexOptions.Compiled),
            new Regex(@"[Dd]ungeon\??\.Landblock\s*==\s*0(?![\w\.])", RegexOptions.Compiled),
        };

        private static string StripComments(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(text, @"//[^\n]*", "");
        }

        /// <summary>
        /// The offending reads in one file's text: comments stripped, every run of whitespace (line breaks included) collapsed
        /// to one space so a statement continued over lines is matched as one, the allowed reads removed, and each remaining
        /// read reported with some context.
        /// </summary>
        private static List<string> Offenders(string source)
        {
            var joined = Regex.Replace(StripComments(source.Replace("\r\n", "\n")), @"\s+", " ");

            foreach (var allowed in AllowedReads)
                joined = allowed.Replace(joined, "<allowed>");

            return DungeonLandblockRead.Matches(joined)
                .Select(m => joined.Substring(Math.Max(0, m.Index - 60), Math.Min(joined.Length - Math.Max(0, m.Index - 60), 120)).Trim())
                .ToList();
        }

        private static string ThreadDungeonsDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "ThreadDungeons");
                if (Directory.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find Source/ACE.Server/ThreadDungeons by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        [TestMethod]
        public void The_tripwire_flags_every_shape_it_guards_against()
        {
            // Positive controls: each must be flagged, or the folder scan below proves nothing.
            var flagged = new[]
            {
                "&& player.Location.LandblockId.Landblock == run.Dungeon.Landblock;",                  // the WP3a shapes
                "if (position.LandblockId.Landblock != run.Dungeon.Landblock)",
                "if (run.Dungeon.Landblock == pos.LandblockId.Landblock)",
                "landed.LandblockId.Landblock != run?.Dungeon?.Landblock",
                "x.Landblock == dungeon.Landblock",
                "if (landed == null || landed.LandblockId.Landblock\n                != run.Dungeon.Landblock)",  // continued over a line break
                "var lb = run.Dungeon.Landblock; if (pos.LandblockId.Landblock == lb) return;",          // alias
                "if (pos.LandblockId.Landblock.Equals(run.Dungeon.Landblock)) return;",                  // .Equals, either side
                "if (run.Dungeon.Landblock.Equals(pos.LandblockId.Landblock)) return;",
                "if (run.Dungeon.Landblock == 0x0150) return;",                                          // only 0 is exempt
                "if (run.Dungeon.Landblock != 0) return;",                                               // only "== 0" is exempt
                "Say(player, $\"landblock {run.Dungeon.Landblock}\");",                                  // a hole without :X4 is not log text
            };

            foreach (var source in flagged)
                Assert.AreEqual(1, Offenders(source).Count, $"must be flagged: {source}");

            // Negative controls: the allow-list, and text that is only a comment.
            var allowed = new[]
            {
                "var lb = RealmManager.GetNewThreadDungeonLandblock(new LandblockId((uint)dungeon.Landblock << 16), owner, run, instance);",
                "log.Error($\"[DYNDUNGEON] dungeon {dungeon.Id} landblock 0x{dungeon.Landblock:X4} is not a dungeon\");",
                "graph = WalkGraphSource(run.Dungeon.Landblock);",
                "if (run?.Dungeon == null || run.Dungeon.Landblock == 0 || run.Dungeon.IsOutdoor) return;",
                "// if (pos.LandblockId.Landblock == run.Dungeon.Landblock)\nreturn;",
                "/* run.Dungeon.Landblock */ return;",
                "/// <summary>compares run.Dungeon.Landblock</summary>\nreturn;",
            };

            foreach (var source in allowed)
                Assert.AreEqual(0, Offenders(source).Count, $"must be allowed: {source}");
        }

        [TestMethod]
        public void No_dungeon_landblock_read_remains_outside_the_allow_list()
        {
            var dir = ThreadDungeonsDir();
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);

            // Not vacuous: the folder is large, the helper holds the one sanctioned comparison, and the allow-list is in use
            // (there ARE reads in the folder; every one of them is allowed).
            Assert.IsTrue(files.Length > 50, $"expected the whole ThreadDungeons folder, found {files.Length} file(s)");

            var helper = files.Single(f => Path.GetFileName(f) == "ThreadRunGeometry.cs");
            StringAssert.Contains(StripComments(File.ReadAllText(helper)), "landblock == dungeon.Landblock", "the helper keeps the comparison");

            var offenders = new List<string>();
            var rawReads = 0;

            foreach (var file in files)
            {
                if (file == helper)
                    continue;

                var text = File.ReadAllText(file);
                rawReads += DungeonLandblockRead.Matches(StripComments(text.Replace("\r\n", "\n"))).Count;
                offenders.AddRange(Offenders(text).Select(o => $"{Path.GetFileName(file)}: {o}"));
            }

            Assert.IsTrue(rawReads > 0, "the scan saw no reads at all, so the allow-list was never exercised");
            Assert.AreEqual(0, offenders.Count, "route these through ThreadRunGeometry.IsOnRunLandblock, or add them to the allow-list with a reason:\n" + string.Join("\n", offenders));
        }
    }
}
