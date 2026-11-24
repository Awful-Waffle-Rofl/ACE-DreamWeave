using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// protect.healthWisps: the pure show-count and geometry rules, the run's once-a-second reconcile through TickRuntime (the
    /// consumer), the load validation, and the shipped content (weenie 1007654 and the defend_envoys block). Reads files only and
    /// never PropertyManager.
    /// </summary>
    [TestClass]
    public class WorldEventHoldHealthWispTests
    {
        private static int Shown(double percent, int previouslyShown = 4, int n = 4) =>
            WorldEventHoldRules.HealthWispsShown(percent, 100, n, previouslyShown);

        // ---- the show count ------------------------------------------------------------------------------------------

        [TestMethod]
        public void HealthWispsShown_Thresholds_AreStrictlyAbove75_50_25_0()
        {
            Assert.AreEqual(4, Shown(100));
            Assert.AreEqual(4, Shown(75.01));
            Assert.AreEqual(3, Shown(75), "exactly 75% is the 50-75 band");
            Assert.AreEqual(3, Shown(50.01));
            Assert.AreEqual(2, Shown(50));
            Assert.AreEqual(2, Shown(25.01));
            Assert.AreEqual(1, Shown(25));
            Assert.AreEqual(1, Shown(0.01), "any health above 0 shows one");
            Assert.AreEqual(0, Shown(0), "dead shows none");
            Assert.AreEqual(0, WorldEventHoldRules.HealthWispsShown(-5, 100, 4, 4));
            Assert.AreEqual(0, WorldEventHoldRules.HealthWispsShown(50, 0, 4, 4), "no max health is no reading");
        }

        [TestMethod]
        public void HealthWispsShown_RestoreNeedsOnePercentOverTheThreshold()
        {
            // Out at 75%: three shown. Back above 75 is not enough; 76 is not above 75 + 1; 76.01 is.
            Assert.AreEqual(3, Shown(75.5, previouslyShown: 3));
            Assert.AreEqual(3, Shown(76.0, previouslyShown: 3));
            Assert.AreEqual(4, Shown(76.01, previouslyShown: 3));

            // A wisp that is already out stays out until restored, one that is shown goes out right at the threshold.
            Assert.AreEqual(4, Shown(75.5, previouslyShown: 4), "still shown: no hysteresis on the way down");
            Assert.AreEqual(3, Shown(75.0, previouslyShown: 4));

            // Several at once after a big heal.
            Assert.AreEqual(4, Shown(100, previouslyShown: 1));
            Assert.AreEqual(1, Shown(26.0, previouslyShown: 1));
            Assert.AreEqual(2, Shown(26.01, previouslyShown: 1));
        }

        [TestMethod]
        public void HealthWispsShown_OtherCounts_ScaleTheBands()
        {
            Assert.AreEqual(2, WorldEventHoldRules.HealthWispsShown(51, 100, 2, 2));
            Assert.AreEqual(1, WorldEventHoldRules.HealthWispsShown(50, 100, 2, 2));
            Assert.AreEqual(8, WorldEventHoldRules.HealthWispsShown(88, 100, 8, 8));
            Assert.AreEqual(7, WorldEventHoldRules.HealthWispsShown(87.5, 100, 8, 8));
        }

        // ---- the geometry --------------------------------------------------------------------------------------------

        [TestMethod]
        public void HealthWispOffsets_RowIsCentredOverTheKnightAndPerpendicularToItsFacing()
        {
            var def = new HealthWispsDef { Wcid = 1007654, Count = 4, Spacing = 0.5, Dz = 2.4, Scale = 0.35 };

            // A knight at the anchor facing north (yaw 0): the row runs east-west, centred on it.
            var north = WorldEventHoldRules.HealthWispOffsets(new WorldEventHoldRules.RingOffset(0f, 0f, 0f), def);

            Assert.AreEqual(4, north.Count);
            CollectionAssert.AreEqual(new[] { -0.75f, -0.25f, 0.25f, 0.75f }, north.Select(d => (float)Math.Round(d.Dx, 3)).ToArray());
            Assert.IsTrue(north.All(d => Math.Abs(d.Dy) < 1e-5f), "all on the knight's own east-west line");
            Assert.IsTrue(north.All(d => d.Dz == 2.4f && d.Scale == 0.35f && d.Wcid == 1007654 && d.Snap && d.Speed > 0));

            // A knight 16 m due north of the anchor facing north: same row, shifted with it.
            var shifted = WorldEventHoldRules.HealthWispOffsets(new WorldEventHoldRules.RingOffset(0f, 16f, 0f), def);
            Assert.AreEqual(16f, shifted[0].Dy, 1e-4f);
            Assert.AreEqual(-0.75f, shifted[0].Dx, 1e-4f);

            // Facing east (outward for a knight due east: yaw -90): the row runs north-south through the knight.
            var east = WorldEventHoldRules.HealthWispOffsets(new WorldEventHoldRules.RingOffset(16f, 0f, -90f), def);

            Assert.IsTrue(east.All(d => Math.Abs(d.Dx - 16f) < 1e-4f), "perpendicular to an east facing is the north-south line");
            Assert.AreEqual(1.5f, east.Max(d => d.Dy) - east.Min(d => d.Dy), 1e-4f, "three gaps of 0.5 m");
            Assert.AreEqual(0f, east.Average(d => d.Dy), 1e-4f, "centred on the knight");
        }

        [TestMethod]
        public void HealthWispOffsets_NullOrEmptyDef_IsNoWisps()
        {
            Assert.AreEqual(0, WorldEventHoldRules.HealthWispOffsets(default, null).Count);
            Assert.AreEqual(0, WorldEventHoldRules.HealthWispOffsets(default, new HealthWispsDef { Wcid = 0 }).Count);
        }

        // ---- load validation -----------------------------------------------------------------------------------------

        [TestMethod]
        public void ValidateHealthWisps_DefaultsAndBadBlocks()
        {
            var diagnostics = new List<string>();

            Assert.IsNull(WorldEventAxisStore.ValidateHealthWisps("g", null, diagnostics));

            var defaults = new HealthWispsDef { Wcid = 5 };
            Assert.AreEqual(4, defaults.Count);
            Assert.AreEqual(0.5, defaults.Spacing);
            Assert.AreEqual(2.4, defaults.Dz);
            Assert.AreEqual(0.35, defaults.Scale);
            Assert.AreSame(defaults, WorldEventAxisStore.ValidateHealthWisps("g", defaults, diagnostics));
            Assert.AreEqual(0, diagnostics.Count);

            foreach (var bad in new[]
            {
                new HealthWispsDef { Wcid = 0 },
                new HealthWispsDef { Wcid = 5, Count = 0 },
                new HealthWispsDef { Wcid = 5, Count = 9 },
                new HealthWispsDef { Wcid = 5, Spacing = 0 },
                new HealthWispsDef { Wcid = 5, Scale = -1 },
                new HealthWispsDef { Wcid = 5, Dz = double.NaN },
                new HealthWispsDef { Wcid = 5, Dz = 20.5 },
                new HealthWispsDef { Wcid = 5, Dz = -20.5 },
                new HealthWispsDef { Wcid = 5, Spacing = 5.1 },
                new HealthWispsDef { Wcid = 5, Scale = 5.1 },
            })
            {
                Assert.IsNull(WorldEventAxisStore.ValidateHealthWisps("g", bad, diagnostics));
            }

            Assert.AreEqual(10, diagnostics.Count);

            Assert.IsNotNull(WorldEventAxisStore.ValidateHealthWisps("g", new HealthWispsDef { Wcid = 5, Dz = -20, Spacing = 5, Scale = 5 }, diagnostics), "the bounds themselves are legal");
        }

        // ---- the run's tick (the consumer) ---------------------------------------------------------------------------

        private sealed class FakeWispHost : IHoldHealthWispHost
        {
            public readonly List<Creature> Created = new List<Creature>();
            public int Destroys;

            public void Create(DecorDef spot, Action<WorldObject> placed)
            {
                var wisp = HoldFixtures.MakeCreature("wisp " + Created.Count);
                Created.Add(wisp);
                placed(wisp);
            }

            public void Destroy(WorldObject wisp)
            {
                Destroys++;
                wisp.Destroy();
            }
        }

        private static (WorldEventHoldRun Run, Creature Knight, FakeWispHost Host) Rig(bool attachHost = true)
        {
            var run = new WorldEventHoldRun(7, g => WorldEventHoldSettingsResolver.Resolve(g, p => null));
            var knight = HoldFixtures.MakeCreature("Knight");
            var host = new FakeWispHost();

            var spots = WorldEventHoldRules.HealthWispOffsets(default, new HealthWispsDef { Wcid = 1007654 });
            run.RegisterHealthWisps(knight, spots);

            if (attachHost)
                run.AttachHealthWispHost(host);

            run.PublishKnights(new[] { knight });
            return (run, knight, host);
        }

        private static void SetPercent(Creature knight, double percent) =>
            knight.Health.Current = (uint)Math.Round(knight.Health.MaxValue * percent / 100.0);

        [TestMethod]
        public void TickRuntime_WispsFollowKnightHealth_OneDestroyOnDrop_RestoreOnHeal_AllGoneOnDeath()
        {
            var (run, knight, host) = Rig();

            run.TickRuntime(1, 0);
            Assert.AreEqual(4, host.Created.Count, "full health places all four");
            Assert.AreEqual(4, run.HealthWispsPresent(knight.Guid.Full));

            SetPercent(knight, 80);
            run.TickRuntime(2, 0);
            Assert.AreEqual(0, host.Destroys, "80% still shows four");

            SetPercent(knight, 70);
            run.TickRuntime(3, 0);
            Assert.AreEqual(1, host.Destroys, "80 -> 70 puts out exactly one");
            Assert.AreEqual(3, run.HealthWispsPresent(knight.Guid.Full));
            Assert.IsTrue(host.Created[3].IsDestroyed, "the highest-index wisp goes first");
            Assert.IsFalse(host.Created[0].IsDestroyed);

            run.TickRuntime(4, 0);
            Assert.AreEqual(1, host.Destroys, "nothing more while health holds");
            Assert.AreEqual(4, host.Created.Count);

            SetPercent(knight, 75.5);
            run.TickRuntime(5, 0);
            Assert.AreEqual(4, host.Created.Count, "75.5% is inside the 1% restore margin");

            SetPercent(knight, 77);
            run.TickRuntime(6, 0);
            Assert.AreEqual(5, host.Created.Count, "healing to 77% brings it back");
            Assert.AreEqual(4, run.HealthWispsPresent(knight.Guid.Full));

            knight.Health.Current = 0;
            run.TickRuntime(7, 0);
            Assert.AreEqual(0, run.HealthWispsPresent(knight.Guid.Full), "a dead knight has no wisps");
            Assert.AreEqual(5, host.Destroys, "one earlier plus the four that stood");
            Assert.IsTrue(host.Created.All(w => w.IsDestroyed));
        }

        [TestMethod]
        public void TickRuntime_AtMostOneBatchPerKnightPerTick()
        {
            var (run, knight, host) = Rig();

            run.TickRuntime(1, 0);
            SetPercent(knight, 10);
            run.TickRuntime(2, 0);

            Assert.AreEqual(3, host.Destroys, "a big drop destroys its wisps together, in one tick");
            Assert.AreEqual(1, run.HealthWispsPresent(knight.Guid.Full));
            Assert.AreEqual(4, host.Created.Count, "and creates nothing in the same tick");
        }

        [TestMethod]
        public void TickRuntime_KnightNotPublishedYet_ShowsNothingAndIsLeftAlone()
        {
            var (run, knight, host) = Rig();

            run.PublishKnights(Array.Empty<Creature>());
            run.TickRuntime(1, 0);

            Assert.AreEqual(0, host.Created.Count);
            Assert.AreEqual(0, host.Destroys);
        }

        [TestMethod]
        public void TickRuntime_NoHostAttached_IsOff()
        {
            var (run, knight, host) = Rig(attachHost: false);

            run.TickRuntime(1, 0);
            SetPercent(knight, 10);
            run.TickRuntime(2, 0);

            Assert.AreEqual(0, host.Created.Count);
            Assert.AreEqual(0, run.HealthWispsPresent(knight.Guid.Full));
        }

        [TestMethod]
        public void TickRuntime_FailedPlacement_IsNotRetried()
        {
            var run = new WorldEventHoldRun(8, g => WorldEventHoldSettingsResolver.Resolve(g, p => null));
            var knight = HoldFixtures.MakeCreature("Knight");
            var attempts = 0;

            run.RegisterHealthWisps(knight, WorldEventHoldRules.HealthWispOffsets(default, new HealthWispsDef { Wcid = 1, Count = 2 }));
            run.AttachHealthWispHost(new FailingHost(() => attempts++));
            run.PublishKnights(new[] { knight });

            run.TickRuntime(1, 0);
            run.TickRuntime(2, 0);
            run.TickRuntime(3, 0);

            Assert.AreEqual(2, attempts, "one attempt per wisp, never again");
        }

        private sealed class FailingHost : IHoldHealthWispHost
        {
            private readonly Action onCreate;

            public FailingHost(Action onCreate) => this.onCreate = onCreate;

            public void Create(DecorDef spot, Action<WorldObject> placed)
            {
                onCreate();
                placed(null);
            }

            public void Destroy(WorldObject wisp) { }
        }

        private sealed class ThrowingHost : IHoldHealthWispHost
        {
            public int Creates;

            public void Create(DecorDef spot, Action<WorldObject> placed)
            {
                Creates++;
                throw new InvalidOperationException("boom");
            }

            public void Destroy(WorldObject wisp) { }
        }

        /// <summary>Holds every placed callback until the test runs it, like a landblock queue that has not got to it yet.</summary>
        private sealed class DeferredHost : IHoldHealthWispHost
        {
            public readonly List<Action<WorldObject>> Callbacks = new List<Action<WorldObject>>();
            public int Creates;
            public int Destroys;

            public void Create(DecorDef spot, Action<WorldObject> placed)
            {
                Creates++;
                Callbacks.Add(placed);
            }

            public void Destroy(WorldObject wisp)
            {
                Destroys++;
                wisp.Destroy();
            }
        }

        private static (WorldEventHoldRun Run, Creature Knight) RigWith(IHoldHealthWispHost host)
        {
            var run = new WorldEventHoldRun(9, g => WorldEventHoldSettingsResolver.Resolve(g, p => null));
            var knight = HoldFixtures.MakeCreature("Knight");

            run.RegisterHealthWisps(knight, WorldEventHoldRules.HealthWispOffsets(default, new HealthWispsDef { Wcid = 1007654 }));
            run.AttachHealthWispHost(host);
            run.PublishKnights(new[] { knight });
            return (run, knight);
        }

        [TestMethod]
        public void TickRuntime_CreateThrows_SlotFailsOnce_NeverRetried_NothingPending()
        {
            var host = new ThrowingHost();
            var (run, knight) = RigWith(host);

            run.TickRuntime(1, 0);
            run.TickRuntime(2, 0);

            Assert.AreEqual(0, run.HealthWispsPresent(knight.Guid.Full), "a throwing create leaves no pending slot behind");
            Assert.AreEqual(4, host.Creates, "one attempt per slot, never again");
        }

        [TestMethod]
        public void TickRuntime_PlacementStillPending_IsNotCreatedAgain()
        {
            var host = new DeferredHost();
            var (run, knight) = RigWith(host);

            run.TickRuntime(1, 0);
            run.TickRuntime(2, 0);
            Assert.AreEqual(4, host.Creates, "a pending slot counts as present");
            Assert.AreEqual(4, run.HealthWispsPresent(knight.Guid.Full));

            foreach (var cb in host.Callbacks)
                cb(HoldFixtures.MakeCreature("wisp"));

            run.TickRuntime(3, 0);
            Assert.AreEqual(4, host.Creates, "and landing changes nothing");
            Assert.AreEqual(4, run.HealthWispsPresent(knight.Guid.Full));
        }

        [TestMethod]
        public void TickRuntime_KnightDiesWhilePlacementPending_LateWispIsDestroyedNextTick()
        {
            var host = new DeferredHost();
            var (run, knight) = RigWith(host);
            var landed = new List<Creature>();

            run.TickRuntime(1, 0);
            knight.Health.Current = 0;
            run.TickRuntime(2, 0);
            Assert.AreEqual(0, host.Destroys, "nothing exists yet to destroy");

            foreach (var cb in host.Callbacks)
            {
                var w = HoldFixtures.MakeCreature("late wisp");
                landed.Add(w);
                cb(w);
            }

            run.TickRuntime(3, 0);
            Assert.AreEqual(4, host.Destroys);
            Assert.IsTrue(landed.All(w => w.IsDestroyed));
            Assert.AreEqual(0, run.HealthWispsPresent(knight.Guid.Full));
        }

        [TestMethod]
        public void TickRuntime_PlacedCallbackNullOrDestroyed_FailsTheSlot()
        {
            var host = new DeferredHost();
            var (run, knight) = RigWith(host);

            run.TickRuntime(1, 0);

            var dead = HoldFixtures.MakeCreature("dead wisp");
            dead.Destroy();

            host.Callbacks[0](null);
            host.Callbacks[1](dead);
            host.Callbacks[2](HoldFixtures.MakeCreature("ok"));
            host.Callbacks[3](HoldFixtures.MakeCreature("ok"));

            Assert.AreEqual(2, run.HealthWispsPresent(knight.Guid.Full));

            run.TickRuntime(2, 0);
            run.TickRuntime(3, 0);
            Assert.AreEqual(4, host.Creates, "the two failed slots are not retried");
        }

        [TestMethod]
        public void ThreadDefense_And_GoalsWithoutTheBlock_ProduceNoWisps()
        {
            // Off by default: a ProtectDef has no block, so the spawner never attaches a host and a run with no host places nothing.
            Assert.IsNull(new ProtectDef().HealthWisps);

            var (run, knight, host) = Rig(attachHost: false);
            run.TickRuntime(1, 0);
            Assert.AreEqual(0, run.HealthWispsPresent(knight.Guid.Full));
            Assert.AreEqual(0, host.Created.Count);

            // Thread Defense never goes through SpawnProtected or a goals.json protect block.
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Server", "WorldEvents", "Hold", "ThreadDefenseHold.cs"));
            Assert.IsFalse(source.Contains("HealthWisps", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("SpawnProtected", StringComparison.Ordinal));

            var store = WorldEventAxisStore.Load(Path.Combine(RepoRoot(), "Content", "events", "axes"));
            var others = store.Goals.Values.Where(g => g.Id != "defend_envoys" && g.Protect != null).ToList();
            Assert.IsTrue(others.All(g => g.Protect.HealthWisps == null), "only defend_envoys configures wisps");
        }

        [TestMethod]
        public void SpawnProtected_AttachesTheHostAndRegistersEachKnightsSpots_OnlyWhenTheBlockIsSet()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Server", "WorldEvents", "WorldEventSpawner.Hold.cs"));

            const string guard = "if (protect.HealthWisps != null && run != null)";
            const string attach = "run.AttachHealthWispHost(new HealthWispHost(this, evt, new Position(centre)));";
            const string register = "run.RegisterHealthWisps(creature, wispSpots[i]);";

            var guardAt = source.IndexOf(guard, StringComparison.Ordinal);
            var attachAt = source.IndexOf(attach, StringComparison.Ordinal);
            var lambdaAt = source.IndexOf("PrepareKnight(evt, creature, protect, tuning, participants);", StringComparison.Ordinal);
            var registerAt = source.IndexOf(register, StringComparison.Ordinal);
            var lambdaEnd = lambdaAt < 0 ? -1 : source.IndexOf("return null;", lambdaAt, StringComparison.Ordinal);

            Assert.IsTrue(guardAt >= 0, "the null-block guard");
            Assert.IsTrue(attachAt > guardAt, "the host is attached under the guard");
            Assert.IsTrue(lambdaAt > attachAt, "attach precedes the knights' spawn");
            Assert.IsTrue(registerAt > lambdaAt && registerAt < lambdaEnd, "registration is inside the knight-prepare lambda body");
            StringAssert.Contains(source, "if (wispSpots != null && i < wispSpots.Count)");
        }

        [TestMethod]
        public void PickOwningLandblock_PicksTheBlockThatOwnsThePosition_NullWhenUnheldOrUnloaded()
        {
            var a = "A";
            var b = "B";
            var loaded = new[] { a, b };
            Func<string, ulong> id = x => x == "A" ? 0x0001UL : 0x0002UL;

            Assert.AreSame(a, WorldEventSpawner.PickOwningLandblock(loaded, id, 0x0001UL, held: true));
            Assert.AreSame(b, WorldEventSpawner.PickOwningLandblock(loaded, id, 0x0002UL, held: true), "a neighbour's position is placed by the neighbour");
            Assert.IsNull(WorldEventSpawner.PickOwningLandblock(loaded, id, 0x0003UL, held: true), "not loaded");
            Assert.IsNull(WorldEventSpawner.PickOwningLandblock(loaded, id, 0x0001UL, held: false), "not held by the run");
        }

        // ---- shipped content -----------------------------------------------------------------------------------------

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/property-registry.tsv by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }

        [TestMethod]
        public void DefendEnvoys_ConfiguresTheFourWispRow()
        {
            var store = WorldEventAxisStore.Load(Path.Combine(RepoRoot(), "Content", "events", "axes"));

            Assert.AreEqual(0, store.Diagnostics.Count(d => d.Contains("healthWisps", StringComparison.Ordinal)));
            Assert.IsTrue(store.Goals.TryGetValue("defend_envoys", out var goal));

            var wisps = goal.Protect.HealthWisps;
            Assert.IsNotNull(wisps, "defend_envoys.protect.healthWisps");
            Assert.AreEqual(1007654u, wisps.Wcid);
            Assert.AreEqual(4, wisps.Count);
            Assert.AreEqual(0.5, wisps.Spacing);
            Assert.AreEqual(2.4, wisps.Dz);
            Assert.AreEqual(0.35, wisps.Scale);
        }

        [TestMethod]
        public void EnvoyVigilWisp_IsInertUiHiddenEtherealMiscScenery()
        {
            var path = Path.Combine(RepoRoot(), "Content", "sql", "weenies", "1007654 Envoy Vigil Wisp.sql");
            Assert.IsTrue(File.Exists(path), "1007654 Envoy Vigil Wisp.sql is missing");

            var sql = File.ReadAllText(path);

            StringAssert.Contains(sql, "-- @unit: weenies/1007654");
            StringAssert.Contains(sql, "-- @owner: world-events");
            StringAssert.Contains(sql, "-- @owns: wcid:1007654");
            StringAssert.Contains(sql, "VALUES (1007654, 'dw-envoy-vigil-wisp', 1,");
            StringAssert.Contains(sql, "1, 'Envoy Vigil Wisp') /* Name */");
            StringAssert.Contains(sql, "128) /* ItemType - Misc */");
            StringAssert.Contains(sql, "19, False) /* Attackable */");
            StringAssert.Contains(sql, "24, True ) /* UiHidden */");
            StringAssert.Contains(sql, "13, True ) /* Ethereal */");
            StringAssert.Contains(sql, "133,         1) /* ShowableOnRadar - ShowNever */");
            StringAssert.Contains(sql, "0x02001973) /* Setup");
        }
    }
}
