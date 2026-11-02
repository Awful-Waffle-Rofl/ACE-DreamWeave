using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.PuzzleGates;
using ACE.Server.SpeedCourses;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The course run's Seal tokens and the runner's lines against a fake world: K1 held behind P2, every token
    /// contributed by the run with nobody notified, the course's own progress lines (the lectern included), the intro and
    /// Seal lines, and the tokens the run grants when the course cannot offer them.
    /// </summary>
    [TestClass]
    public class SpeedCourseSealMessageTests
    {
        private const uint BookWcid = 1007500;
        private const uint Owner = 0x50000042;

        private static (SpeedCourseRun Run, SpeedCourseFakeWorld World) NewRun()
        {
            var world = new SpeedCourseFakeWorld { Plan = SpeedCourseFakeWorld.SamplePlan() };
            world.AddBlackBookSites();

            var run = new SpeedCourseRun(SpeedCourseFixtures.Course(), 3, 60, BookWcid, 120, Owner, 12345, world, now: 0.0);
            return (run, world);
        }

        private static void Populate(SpeedCourseRun run, SpeedCourseFakeWorld world)
        {
            run.Tick(0.5);
            world.Drain();
        }

        private static void Solve(SpeedCourseFakeWorld world, string siteId)
            => ((SpeedPuzzleGateHost)world.Placements.Last(p => p.Site.Id == siteId).Host).OnSolved(null, null);

        private static string Progress(string label, int gates, int rites)
            => $"{label} complete. Seal: ward gates {gates} of 2, rites {rites} of 2.";

        private static string Granted(string label) => $"{label} granted: the cells could not offer it this run.";

        // ------------------------------------------------------------------------------------------- K1 behind P2

        [TestMethod]
        public void K1_IsHeldUntilP2IsSolved_AndItsDeathContributesNorthRite_FromTheRun()
        {
            var (run, world) = NewRun();
            Populate(run, world);

            Assert.IsTrue(run.K1.IsNone, "K1 cannot be reached (or hit) before P2 opens");

            Solve(world, "bb_p2");
            Assert.IsFalse(run.K1.IsNone, "solving P2 lets K1 in");
            Assert.AreEqual(0, world.Contributions.Count, "P2 contributes nothing");

            run.Tick(1.0);
            Assert.AreEqual(0, world.Contributions.Count, "alive: nothing yet");

            world.Dead.Add(run.K1.Guid);
            run.Tick(1.5);
            run.Tick(2.0);

            Assert.AreEqual(1, world.Contributions.Count, "north_rite once");
            Assert.AreEqual(("bb_seal", "north_rite", 1.0), world.Contributions[0]);
            CollectionAssert.AreEqual(new[] { Progress("North rite", 0, 1) }, world.Tells.ToList());
        }

        [TestMethod]
        public void P2GivenUp_LetsK1In_WithNoGrant()
        {
            var (run, world) = NewRun();
            world.RefuseSites.Add("bb_p2");
            Populate(run, world);

            Assert.IsFalse(run.K1.IsNone, "with no P2 gate standing, K1 is not held");
            Assert.IsTrue(world.Errors.Any(e => e.Contains("puzzle P2") && e.Contains("giving up")));
            Assert.AreEqual(0, world.Contributions.Count, "P2 has no token to grant");
            Assert.AreEqual(0, world.Tells.Count);
        }

        // ------------------------------------------------------------------------------------------- the runner's lines

        [TestMethod]
        public void Intro_IsToldOnceAtTheClockRebase()
        {
            var (run, world) = NewRun();
            Populate(run, world);
            Assert.AreEqual(0, world.Tells.Count);

            world.RunnerView = new SpeedRunnerView { Online = true, BoundHere = true, ClockRebased = true };

            for (var i = 0; i < 5; i++)
                run.Tick(1.0 + i);

            CollectionAssert.AreEqual(new[] { "INTRO" }, world.Tells.ToList());
        }

        [TestMethod]
        public void EveryToken_IsToldWithTheSealCounts_RitesCappedAtTwo_ThenTheSealOpens()
        {
            var (run, world) = NewRun();
            Populate(run, world);

            Solve(world, "bb_p1");
            Solve(world, "bb_p2");

            // The lectern is a contributor object in content: it lands on the lock directly, never through the run.
            world.SealTokens.Add("east_rite");
            run.Tick(1.0);

            world.Dead.Add(run.K1.Guid);
            run.Tick(1.5);

            // The south memory's completion arrives through the hazard host, which contributes like any course source.
            world.Contribute("bb_seal", "south_rite", 1);
            run.Tick(2.0);

            Solve(world, "bb_p3");
            world.Seals["bb_seal"] = true;
            run.Tick(2.5);
            run.Tick(3.0);

            CollectionAssert.AreEqual(new[]
            {
                Progress("First ward gate", 1, 0),
                Progress("East rite", 1, 1),
                Progress("North rite", 1, 2),
                Progress("South rite", 1, 2),
                Progress("Last ward gate", 2, 2),
                "SEAL OPEN",
            }, world.Tells.ToList());

            Assert.IsFalse(world.Contributions.Any(c => c.Token == "east_rite"), "the run never contributes the lectern's token itself");
        }

        // ------------------------------------------------------------------------------------------- grants

        [TestMethod]
        public void PuzzleGivenUp_GrantsItsToken_WithTheGrantedLine()
        {
            var (run, world) = NewRun();
            world.RefuseSites.Add("bb_p1");
            Populate(run, world);

            Assert.AreEqual(1, world.Contributions.Count(c => c.Token == "p1"), "p1 granted once");
            Assert.IsTrue(run.WasGranted("p1"));
            CollectionAssert.AreEqual(new[] { Granted("First ward gate") }, world.Tells.ToList());
            Assert.IsTrue(world.Warnings.Any(w => w.Contains("granting token p1")));
        }

        [TestMethod]
        public void PuzzleRemovedPastTheReplaceCap_GrantsItsToken()
        {
            var (run, world) = NewRun();
            Populate(run, world);

            for (var i = 0; i <= SpeedCourseRun.MaxPuzzleReplaceTries; i++)
            {
                var host = (SpeedPuzzleGateHost)world.Placements.Last(p => p.Site.Id == "bb_p3").Host;
                host.OnRemoved(null, PuzzleRemovalReason.Reaped, solved: false);
                world.Drain();
            }

            Assert.AreEqual(1, world.Contributions.Count(c => c.Token == "p3"));
            CollectionAssert.AreEqual(new[] { Granted("Last ward gate") }, world.Tells.ToList());
        }

        [TestMethod]
        public void K1FailsToSpawn_GrantsNorthRite()
        {
            var (run, world) = NewRun();
            world.FailingWcids.Add(200);
            Populate(run, world);

            Assert.AreEqual(0, world.Contributions.Count, "K1 is only attempted when P2 opens");

            Solve(world, "bb_p2");

            Assert.AreEqual(1, world.Contributions.Count(c => c.Token == "north_rite"));
            CollectionAssert.AreEqual(new[] { Granted("North rite") }, world.Tells.ToList());
        }

        [TestMethod]
        public void K2FailsToSpawn_GrantsTheSouthRite_ItsMemoryWaitsOn()
        {
            var (run, world) = NewRun();
            world.FailingWcids.Add(201);
            Populate(run, world);

            Assert.AreEqual(1, world.Contributions.Count(c => c.Token == "south_rite"));
            Assert.AreEqual(0, world.Contributions.Count(c => c.Token == "north_rite"));
            CollectionAssert.AreEqual(new[] { Granted("South rite") }, world.Tells.ToList());
        }

        [TestMethod]
        public void NoPlan_GrantsTheNamedTokens()
        {
            var (run, world) = NewRun();
            world.Plan = null;
            Populate(run, world);

            CollectionAssert.AreEquivalent(new[] { "north_rite", "south_rite" }, world.Contributions.Select(c => c.Token).ToList());
            Assert.AreEqual(2, world.Tells.Count(t => t.EndsWith("granted: the cells could not offer it this run.")));
        }

        [TestMethod]
        public void PlanTooSmallForK2_GrantsSouthRite()
        {
            var (run, world) = NewRun();
            world.Plan = new ACE.Server.ThreadDungeons.DungeonSpawnPlan { BossWcid = 300 };
            world.Plan.Entries.Add(new ACE.Server.ThreadDungeons.DungeonSpawnPlanEntry(100, ACE.Server.ThreadDungeons.DungeonRole.Trash, SpeedCourseFakeWorld.Point(1, 1)));
            world.Plan.Entries.Add(new ACE.Server.ThreadDungeons.DungeonSpawnPlanEntry(300, ACE.Server.ThreadDungeons.DungeonRole.Boss, SpeedCourseFakeWorld.Point(9, 9)));
            Populate(run, world);

            Assert.AreEqual(1, world.Contributions.Count(c => c.Token == "south_rite"), "only one creature to promote: K2 has none");
            Assert.AreEqual(0, world.Contributions.Count(c => c.Token == "north_rite"), "K1 got the one entry");
        }

        [TestMethod]
        public void Grant_IsOncePerToken_AndNeverForAnEarnedToken()
        {
            var (run, world) = NewRun();
            Populate(run, world);

            Solve(world, "bb_p1");
            run.GrantToken("p1", "test: already earned");
            Assert.AreEqual(1, world.Contributions.Count(c => c.Token == "p1"));
            Assert.IsFalse(run.WasGranted("p1"));

            run.GrantToken("east_rite", "test");
            run.GrantToken("east_rite", "test again");
            Assert.AreEqual(1, world.Contributions.Count(c => c.Token == "east_rite"));
            Assert.AreEqual(1, world.Tells.Count(t => t == Granted("East rite")));
        }
    }
}
