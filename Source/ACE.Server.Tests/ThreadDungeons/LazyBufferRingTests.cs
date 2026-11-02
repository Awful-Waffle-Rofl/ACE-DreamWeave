using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// dynamic_dungeons_outdoor_lazy_buffer_ring: an outdoor Thread run's footprint landblocks stop eagerly loading the
    /// one-landblock buffer ring. The decision is pure (EphemeralInstanceGroup.LoadsAdjacents with the lazy flag, and
    /// UsesLazyBufferRing over the instance's realm), so it is tested as a full matrix here: every role, crossed with
    /// the recorded setting on and off, crossed with an outdoor run, a non-run ephemeral footprint (/enter-instance-group),
    /// a dungeon run, no realm at all, and the persistent world. The call sites (GetLandblock, TryStart) cannot run in this
    /// harness (a Landblock needs the client dat), so they are pinned on source text. No PropertyManager key is read by
    /// anything under test: the setting enters only through ThreadDungeonRun.LazyBufferRing, which the tests set directly.
    /// </summary>
    [TestClass]
    public class LazyBufferRingTests
    {
        private const ushort Primary = 0xA9B4;

        private static readonly EphemeralLoadRole[] AllRoles = (EphemeralLoadRole[])Enum.GetValues(typeof(EphemeralLoadRole));

        private static ThreadDungeonRun NewRun(bool lazy, bool outdoor)
        {
            var dungeon = new DungeonEntryDef { Id = "owt_test", Name = "OWT Test", Landblock = Primary, ExitPortalWcid = 1003601, Kind = outdoor ? DungeonEntryDef.KindOutdoor : DungeonEntryDef.KindDungeon };
            var spec = new DungeonGemSpec(DungeonGemSpec.Any, 185, 7, DungeonGemSpec.Any, 0, null, 0, 0).WithSeed(5);
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180)) { LazyBufferRing = lazy };
        }

        private static EphemeralFootprint ThreeByThree() => EphemeralFootprint.Square(Primary, 1);

        /// <summary>The instance kinds of the matrix, each with the realm its landblocks would read.</summary>
        private static IEnumerable<(string Name, EphemeralRealm Realm, bool ExpectLazy)> Kinds(bool setting)
        {
            yield return ($"outdoor run, setting {setting}", new EphemeralRealm(null) { Run = NewRun(setting, true), Footprint = ThreeByThree() }, setting);
            yield return ($"/enter-instance-group (footprint, no run), setting {setting}", new EphemeralRealm(null) { Footprint = ThreeByThree() }, false);
            yield return ($"dungeon run (run, no footprint), setting {setting}", new EphemeralRealm(null) { Run = NewRun(setting, false) }, false);
            yield return ($"no realm (persistent world, buffer of a non-run instance), setting {setting}", null, false);
        }

        [TestMethod]
        public void UsesLazyBufferRing_TruthTable()
        {
            for (var mask = 0; mask < 8; mask++)
            {
                bool footprint = (mask & 1) != 0, run = (mask & 2) != 0, lazy = (mask & 4) != 0;
                Assert.AreEqual(footprint && run && lazy, EphemeralInstanceGroup.UsesLazyBufferRing(footprint, run, lazy), $"footprint={footprint} run={run} lazy={lazy}");
            }
        }

        [TestMethod]
        public void UsesLazyBufferRing_OnlyAnOutdoorRunThatRecordedTheSetting()
        {
            foreach (var setting in new[] { true, false })
            {
                foreach (var (name, realm, expectLazy) in Kinds(setting))
                    Assert.AreEqual(expectLazy, EphemeralInstanceGroup.UsesLazyBufferRing(realm), name);
            }
        }

        [TestMethod]
        public void LoadsAdjacents_FullMatrix_RolesBySettingByInstanceKind()
        {
            foreach (var setting in new[] { true, false })
            {
                foreach (var (name, realm, _) in Kinds(setting))
                {
                    var lazy = EphemeralInstanceGroup.UsesLazyBufferRing(realm);

                    foreach (var role in AllRoles)
                    {
                        var expected = role == EphemeralLoadRole.NotEphemeral
                            || (!lazy && (role == EphemeralLoadRole.Primary || role == EphemeralLoadRole.Member));

                        Assert.AreEqual(expected, EphemeralInstanceGroup.LoadsAdjacents(role, lazy), $"{name}, role {role}");
                    }
                }
            }
        }

        [TestMethod]
        public void LoadsAdjacents_WithoutTheLazyRing_IsExactlyTheOldDecision()
        {
            foreach (var role in AllRoles)
                Assert.AreEqual(EphemeralInstanceGroup.LoadsAdjacents(role), EphemeralInstanceGroup.LoadsAdjacents(role, false), role.ToString());
        }

        [TestMethod]
        public void NotEphemeral_KeepsItsAdjacents_UnderBothFlagValues()
        {
            Assert.IsTrue(EphemeralInstanceGroup.LoadsAdjacents(EphemeralLoadRole.NotEphemeral, false));
            Assert.IsTrue(EphemeralInstanceGroup.LoadsAdjacents(EphemeralLoadRole.NotEphemeral, true), "the real world must never lose its adjacents, whatever the flag says");
        }

        [TestMethod]
        public void EnterInstanceGroup_KeepsItsRing_UnderBothSettings()
        {
            foreach (var setting in new[] { true, false })
            {
                // the seam's realm never carries a run, so a set setting cannot reach it
                var realm = new EphemeralRealm(null) { Footprint = ThreeByThree() };
                var lazy = EphemeralInstanceGroup.UsesLazyBufferRing(realm);

                Assert.IsFalse(lazy, $"setting {setting}");
                Assert.IsTrue(EphemeralInstanceGroup.LoadsAdjacents(EphemeralLoadRole.Primary, lazy));
                Assert.IsTrue(EphemeralInstanceGroup.LoadsAdjacents(EphemeralLoadRole.Member, lazy));
            }
        }

        [TestMethod]
        public void ALazilyLoadedNeighbour_IsStillABareBuffer_ThatNeverUnregisters()
        {
            // The ring is classified geometrically, whether it loads eagerly or on demand, so a lazily loaded neighbour
            // is a Buffer: no realm, no content, no further adjacents, and its unload never ends the run.
            var fp = ThreeByThree();

            foreach (var lb in new ushort[] { 0xABB6, 0xA9B6, 0xA7B4, 0xA9B2 })
            {
                var role = EphemeralInstanceGroup.Classify(lb, Position_Ephemeral, fp, registersInstance: false);

                Assert.AreEqual(EphemeralLoadRole.Buffer, role, $"0x{lb:X4}");
                Assert.IsFalse(EphemeralInstanceGroup.InheritsRealm(role));
                Assert.IsFalse(EphemeralInstanceGroup.LoadsWorldContent(role));
                Assert.IsFalse(EphemeralInstanceGroup.LoadsAdjacents(role, true));
                Assert.IsFalse(EphemeralInstanceGroup.LoadsAdjacents(role, false));
                Assert.IsFalse(EphemeralInstanceGroup.MayUnregister(lb, fp));
                Assert.IsTrue(OutdoorFootprintBuffer.Contains(lb, fp.Landblocks), "the perimeter treats it as on-site");
            }
        }

        private static readonly uint Position_Ephemeral = ACE.Entity.Position.InstanceIDFromVars(0, 0x1234, true);

        [TestMethod]
        public void RunCreationLoadCount_EagerIsFootprintPlusRing_LazyIsTheFootprint()
        {
            // 2x2 and 3x3 footprints away from the map edge, driven through the real decisions: run creation loads every
            // footprint landblock with loadAdjacents (RealmManager.GetNewThreadOutdoorLandblock), and each loaded landblock
            // expands to its 8 neighbours only when LoadsAdjacents(Classify(...), lazy) says so.
            var twoByTwo = EphemeralFootprint.Create(0xA9B4, new ushort[] { 0xAAB4, 0xA9B5, 0xAAB5 });

            Assert.AreEqual(16, CreationLoadCount(twoByTwo, lazy: false), "2x2 eager: 4 footprint + 12 ring");
            Assert.AreEqual(4, CreationLoadCount(twoByTwo, lazy: true), "2x2 lazy: the footprint alone");
            Assert.AreEqual(25, CreationLoadCount(ThreeByThree(), lazy: false), "3x3 eager: 9 footprint + 16 ring");
            Assert.AreEqual(9, CreationLoadCount(ThreeByThree(), lazy: true), "3x3 lazy: the footprint alone");
        }

        /// <summary>
        /// The set of landblocks a run's creation loads, simulated with the production decisions. The primary is the
        /// registering load; every other footprint landblock is loaded by TryLoadMembers; both pass loadAdjacents, and an
        /// adjacency load is loadAdjacents: false, so neighbours never expand further.
        /// </summary>
        private static int CreationLoadCount(EphemeralFootprint fp, bool lazy)
        {
            var loaded = new HashSet<ushort>();

            foreach (var lb in fp.Landblocks.OrderBy(l => l == fp.Primary ? 0 : 1))
            {
                loaded.Add(lb);

                var role = EphemeralInstanceGroup.Classify(lb, Position_Ephemeral, lb == fp.Primary ? null : fp, registersInstance: lb == fp.Primary);
                if (!EphemeralInstanceGroup.LoadsAdjacents(role, lazy))
                    continue;

                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        int x = (lb >> 8) + dx, y = (lb & 0xFF) + dy;
                        if ((dx != 0 || dy != 0) && x >= 0 && x <= 254 && y >= 0 && y <= 254)
                            loaded.Add((ushort)((x << 8) | y));
                    }
                }
            }

            return loaded.Count;
        }

        // ------------------------------------------------------------------ neighbour physics sync (thread safety)

        private sealed class FakeGroup { }

        [TestMethod]
        public void NeighbourPhysicsSync_NotLazy_AlwaysSkips()
        {
            var g = new FakeGroup();

            foreach (var dungeon in new[] { false, true })
                foreach (var ticking in new[] { false, true })
                    foreach (var neighbourGroup in new[] { null, g, new FakeGroup() })
                        Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.Skip,
                            EphemeralInstanceGroup.DecideNeighbourPhysicsSync(false, dungeon, ticking, neighbourGroup, g),
                            $"dungeon={dungeon} ticking={ticking}: without the lazy ring the traverse never touches a physics list");
        }

        [TestMethod]
        public void NeighbourPhysicsSync_DungeonNeighbour_Skips_EvenInTheSameGroup()
        {
            var g = new FakeGroup();

            foreach (var ticking in new[] { false, true })
            {
                Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.Skip, EphemeralInstanceGroup.DecideNeighbourPhysicsSync(true, true, ticking, g, g));
                Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.Skip, EphemeralInstanceGroup.DecideNeighbourPhysicsSync(true, true, ticking, new FakeGroup(), g));
                Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.Skip, EphemeralInstanceGroup.DecideNeighbourPhysicsSync<FakeGroup>(true, true, ticking, null, g));
            }
        }

        [TestMethod]
        public void NeighbourPhysicsSync_OtherGroupWhileTickingInParallel_SkipsCrossThread()
        {
            var mine = new FakeGroup();

            Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.SkipCrossThread, EphemeralInstanceGroup.DecideNeighbourPhysicsSync(true, false, true, new FakeGroup(), mine));
            Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.SkipCrossThread, EphemeralInstanceGroup.DecideNeighbourPhysicsSync(true, false, true, new FakeGroup(), null),
                "a thread ticking no group must not rewrite a list some group's thread reads");
        }

        [TestMethod]
        public void NeighbourPhysicsSync_SameGroup_NoGroupYet_OrNotTicking_Syncs()
        {
            var mine = new FakeGroup();

            Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.Sync, EphemeralInstanceGroup.DecideNeighbourPhysicsSync(true, false, true, mine, mine), "same group: this thread is the only reader");
            Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.Sync, EphemeralInstanceGroup.DecideNeighbourPhysicsSync<FakeGroup>(true, false, true, null, mine), "not in a group yet: nobody ticks it");
            Assert.AreEqual(EphemeralInstanceGroup.NeighbourPhysicsSync.Sync, EphemeralInstanceGroup.DecideNeighbourPhysicsSync(true, false, false, new FakeGroup(), mine), "single-threaded phase: no parallel reader");
        }

        [TestMethod]
        public void Tunable_Defaults_ToTheLazyRing()
        {
            // The shipped default row (a static table, not the PropertyManager cache), keyed by the constant TryStart reads.
            Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultBooleanProperties.TryGetValue("dynamic_dungeons_outdoor_lazy_buffer_ring", out var row),
                "the tunable must have a default row under its documented name");
            Assert.IsTrue(row.Item, "owner ruling: the ring is NOT eagerly loaded by default");
            Assert.IsTrue(ACE.Server.Managers.DefaultPropertyManager.DefaultBooleanProperties.ContainsKey(OutdoorSiteTunables.LazyBufferRingKey));
            Assert.IsFalse(new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u,
                new DungeonGemSpec(DungeonGemSpec.Any, 185, 7, DungeonGemSpec.Any, 0, null, 0, 0).WithSeed(5),
                new DungeonEntryDef { Id = "d", Name = "D", Landblock = 0x0150, ExitPortalWcid = 1003601 }, DateTime.UtcNow, TimeSpan.FromMinutes(180)).LazyBufferRing,
                "a run that nobody stamped (every dungeon run) keeps the eager behaviour");
        }

        // ------------------------------------------------------------------ call-site tripwires

        [TestMethod]
        public void TryStart_ReadsTheSettingOnlyForOutdoorRows_AndRecordsItOnTheRun()
        {
            var source = Source("ThreadDungeons", "ThreadDungeonManager.cs");

            AssertContains(source, "var lazyBufferRing = isOutdoor && PropertyManager.GetBool(OutdoorSiteTunables.LazyBufferRingKey, OutdoorSiteTunables.DefaultLazyBufferRing).Item;");
            AssertContains(source, "LazyBufferRing = lazyBufferRing,");

            var read = source.IndexOf("var lazyBufferRing = isOutdoor", StringComparison.Ordinal);
            var publish = source.IndexOf("if (!runs.TryAdd(instance, run))", StringComparison.Ordinal);
            Assert.IsTrue(read >= 0 && publish > read, "the setting is read before the run is published, once per run");
        }

        [TestMethod]
        public void GetLandblock_GatesTheRingOnTheInstancesRealm_AndSyncsNeighbourPhysicsOnlyUnderIt()
        {
            var source = Source("Managers", "LandblockManager.cs");
            var body = Method(source, "public static Landblock GetLandblock(LandblockId landblockId, uint instance, bool loadAdjacents, bool permaload = false, EphemeralRealm ephemeralRealm = null)");

            AssertContains(body, "lazyBufferRing = EphemeralInstanceGroup.UsesLazyBufferRing(ephemeralRealm ?? livePrimary?.InnerRealmInfo);");
            AssertContains(body, "else lazyBufferRing = EphemeralInstanceGroup.UsesLazyBufferRing(landblock.InnerRealmInfo);");
            AssertContains(body, "if (loadAdjacents && EphemeralInstanceGroup.LoadsAdjacents(landblock.GroupRole, lazyBufferRing))");
            AssertContains(body, "SetAdjacents(landblock, true, true, traversePSync: lazyBufferRing);");

            var setAdjacents = Method(source, "private static void SetAdjacents(Landblock landblock, bool traverse = true, bool pSync = false, bool traversePSync = false)");
            AssertContains(setAdjacents, "var sync = traversePSync ? EphemeralInstanceGroup.DecideNeighbourPhysicsSync(true, adjacent.IsDungeon, CurrentlyTickingLandblockGroupsMultiThreaded, adjacent.CurrentLandblockGroup, CurrentMultiThreadedTickingLandblockGroup.Value) : EphemeralInstanceGroup.NeighbourPhysicsSync.Skip;");
            AssertContains(setAdjacents, "SetAdjacents(adjacent, false, sync == EphemeralInstanceGroup.NeighbourPhysicsSync.Sync);");
            AssertContains(setAdjacents, "if (sync == EphemeralInstanceGroup.NeighbourPhysicsSync.SkipCrossThread) log.Error(");

            // the unload path keeps its own neighbour resync, unchanged
            AssertContains(Method(source, "private static void NotifyAdjacents(Landblock landblock)"), "SetAdjacents(adjacent, false, true);");
        }

        [TestMethod]
        public void TheOnlyRunStampIsTheOutdoorTryStart_AndTheSeamStillAsksForItsRing()
        {
            // RealmManager never reads the setting itself: the ring is skipped in GetLandblock, through the run.
            var realm = Source("Managers", "RealmManager.cs");
            Assert.IsFalse(realm.Contains("LazyBufferRingKey"), "the gate is the run's recorded flag, not a live read in RealmManager");

            var seam = Method(realm, "internal static Entity.Landblock GetNewEphemeralGroupLandblock(ACE.Entity.LandblockId primary, int radius, WorldObjects.Player owner, ushort? realmId = null, bool openToFellowship = true)");
            AssertContains(seam, "return LandblockManager.GetLandblock(primary, instance, loadAdjacents: true, permaload: false, ephemeralRealm: ephemeralRealm);");
            Assert.IsFalse(seam.Contains("Run ="), "/enter-instance-group must never carry a run, or the lazy ring could reach it");

            var server = Directory.GetFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bLazyBufferRing\s*=(?![=>])"))
                .Select(f => Path.GetFileName(f))
                .ToList();

            CollectionAssert.AreEquivalent(new[] { "ThreadDungeonManager.cs" }, server, "only TryStart stamps a run's LazyBufferRing");
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertContains(string normalizedHaystack, string needle, string message = null)
        {
            StringAssert.Contains(normalizedHaystack, Normalize(needle), message);
        }

        private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ");

        private static string ServerRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");
                if (Directory.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        /// <summary>The file, with whole-line // and /// comments dropped and whitespace collapsed.</summary>
        private static string Source(params string[] parts)
        {
            var path = Path.Combine(ServerRoot(), Path.Combine(parts));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            text = Regex.Replace(text, @"^[ \t]*//.*\n", string.Empty, RegexOptions.Multiline);
            return Normalize(text);
        }

        private static string Method(string source, string signature)
        {
            var start = source.IndexOf(Normalize(signature), StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find '{signature}' in the source under test");

            var open = source.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"Unbalanced braces while extracting '{signature}'");
            return null;
        }
    }
}
