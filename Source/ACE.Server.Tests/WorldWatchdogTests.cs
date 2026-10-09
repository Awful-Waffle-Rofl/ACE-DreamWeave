using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WaffleACE: world-thread watchdog - the pure part of the decision (WorldWatchdog.Decide).
    ///
    /// Decide is where all of the watchdog's policy lives, and it is the only piece of this feature that
    /// can take a production process down, so it is deliberately a pure function of primitives: no
    /// statics, no clock, no side effects. That is what makes these tests possible with no running
    /// server, and it is why the poll loop is kept to gathering inputs and publishing the answer.
    ///
    /// Not covered here, because all of it needs a running world: WorldWatchdog.Poll and its background
    /// thread, the heartbeat file write (a real filesystem and an atomic replace), the liveness stamp in
    /// WorldManager.UpdateWorld, the ace.world.* gauges, and both shutdown paths in ServerManager - the
    /// fast path ends in Environment.Exit, which cannot be exercised in-process at all.
    ///
    /// The two properties these pin hardest, because they are the entire safety argument for shipping a
    /// self-exiting process:
    ///   * elapsed staleness ALONE never exits - a stall exit requires a non-zero WorldWatchdogExitOnStallSeconds,
    ///     and the shipped default is 0;
    ///   * a world that has not started yet is never exited for being slow, however long the preload runs.
    /// </summary>
    [TestClass]
    public class WorldWatchdogTests
    {
        private const long StallMs = 60000;
        private const long DeadGraceMs = WorldWatchdog.DeadGraceMs;

        /// <summary>
        /// Named-argument wrapper so each test below reads as the scenario it is, rather than as ten
        /// positional booleans and longs.
        /// </summary>
        private static WorldWatchdog.WorldState Decide(
            out bool shouldExit,
            bool everStarted = true,
            bool faulted = false,
            bool worldActive = true,
            bool shutdownInitiated = false,
            long staleMs = 0,
            long stallMs = StallMs,
            long exitOnStallMs = 0,
            long deadForMs = 0,
            bool exitOnDeath = true,
            bool enabled = true)
        {
            return WorldWatchdog.Decide(everStarted, faulted, worldActive, shutdownInitiated, staleMs,
                stallMs, exitOnStallMs, deadForMs, exitOnDeath, enabled, out shouldExit);
        }

        // ---------------------------------------------------------------------------------------------
        // Starting: LandblockManager.PreloadConfigLandblocks legitimately runs for minutes with no tick.
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void NeverStarted_NotFaulted_IsStartingAndNeverExits()
        {
            // Well past the stall threshold, past the dead grace, and past an hour - a preload that slow
            // is a problem for a human to look at, never a reason for the process to kill itself.
            long[] staleValues = { 0, 1000, StallMs, StallMs * 10, 3600000 };

            foreach (var staleMs in staleValues)
            {
                var state = Decide(out var shouldExit, everStarted: false, worldActive: false, staleMs: staleMs,
                    deadForMs: staleMs);

                Assert.AreEqual(WorldWatchdog.WorldState.Starting, state, $"staleMs {staleMs}");
                Assert.IsFalse(shouldExit, $"staleMs {staleMs}");
            }
        }

        [TestMethod]
        public void NeverStarted_WithStallExitConfigured_StillNeverExits()
        {
            // The stall exit is the knob most likely to be turned on by an operator chasing a hang. It
            // must still not be able to reach a server that is only slow to start.
            var state = Decide(out var shouldExit, everStarted: false, worldActive: false,
                staleMs: 3600000, exitOnStallMs: 30000, deadForMs: 3600000);

            Assert.AreEqual(WorldWatchdog.WorldState.Starting, state);
            Assert.IsFalse(shouldExit);
        }

        [TestMethod]
        public void NeverStarted_ButFaulted_IsDeadAndExitsAfterGrace()
        {
            // A crash inside PreloadConfigLandblocks. The world thread's catch block has already run, so
            // this is a death signal and not a slowness signal: the world will never tick.
            var state = Decide(out var shouldExit, everStarted: false, faulted: true, worldActive: false,
                deadForMs: DeadGraceMs);

            Assert.AreEqual(WorldWatchdog.WorldState.Dead, state);
            Assert.IsTrue(shouldExit);
        }

        [TestMethod]
        public void NeverStarted_ButFaulted_DoesNotExitInsideGrace()
        {
            var state = Decide(out var shouldExit, everStarted: false, faulted: true, worldActive: false,
                deadForMs: DeadGraceMs - 1);

            Assert.AreEqual(WorldWatchdog.WorldState.Dead, state);
            Assert.IsFalse(shouldExit);
        }

        // ---------------------------------------------------------------------------------------------
        // Ticking
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Started_FreshStamp_IsTicking()
        {
            var state = Decide(out var shouldExit, staleMs: 0);

            Assert.AreEqual(WorldWatchdog.WorldState.Ticking, state);
            Assert.IsFalse(shouldExit);
        }

        [TestMethod]
        public void Started_JustUnderStallThreshold_IsStillTicking()
        {
            var state = Decide(out var shouldExit, staleMs: StallMs - 1);

            Assert.AreEqual(WorldWatchdog.WorldState.Ticking, state);
            Assert.IsFalse(shouldExit);
        }

        // ---------------------------------------------------------------------------------------------
        // Stalled
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Started_StaleBeyondThreshold_IsStalledAndDoesNotExitByDefault()
        {
            // exitOnStallMs 0 is the shipped default. This is the case that must never exit: there is no
            // measured baseline for the worst legitimate world-thread stall on this fork.
            long[] staleValues = { StallMs, StallMs + 1, StallMs * 100, 86400000 };

            foreach (var staleMs in staleValues)
            {
                var state = Decide(out var shouldExit, staleMs: staleMs, exitOnStallMs: 0);

                Assert.AreEqual(WorldWatchdog.WorldState.Stalled, state, $"staleMs {staleMs}");
                Assert.IsFalse(shouldExit, $"staleMs {staleMs}");
            }
        }

        [TestMethod]
        public void Stalled_WithStallExitConfiguredAndExceeded_Exits()
        {
            var state = Decide(out var shouldExit, staleMs: 120000, exitOnStallMs: 120000);

            Assert.AreEqual(WorldWatchdog.WorldState.Stalled, state);
            Assert.IsTrue(shouldExit);
        }

        [TestMethod]
        public void Stalled_WithStallExitConfiguredButNotReached_DoesNotExit()
        {
            var state = Decide(out var shouldExit, staleMs: 119999, exitOnStallMs: 120000);

            Assert.AreEqual(WorldWatchdog.WorldState.Stalled, state);
            Assert.IsFalse(shouldExit);
        }

        [TestMethod]
        public void Stalled_ExitOnDeathDoesNotAuthoriseAStallExit()
        {
            // exitOnDeath governs the Dead state only. A stalled world is still alive and might recover,
            // so it must not be taken down on the death knob.
            var state = Decide(out var shouldExit, staleMs: 86400000, exitOnStallMs: 0,
                exitOnDeath: true, deadForMs: 86400000);

            Assert.AreEqual(WorldWatchdog.WorldState.Stalled, state);
            Assert.IsFalse(shouldExit);
        }

        // ---------------------------------------------------------------------------------------------
        // Dead
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Started_ThenWorldInactiveOutsideShutdown_IsDead()
        {
            var state = Decide(out var shouldExit, worldActive: false, shutdownInitiated: false,
                deadForMs: DeadGraceMs);

            Assert.AreEqual(WorldWatchdog.WorldState.Dead, state);
            Assert.IsTrue(shouldExit);
        }

        [TestMethod]
        public void Dead_InsideGrace_DoesNotExit()
        {
            // The grace exists so the world thread's log.Fatal flushes and at least one metrics scrape
            // observes the dead state before the process goes. Losing that is losing the post-mortem.
            long[] deadForValues = { 0, 1000, DeadGraceMs - 1 };

            foreach (var deadForMs in deadForValues)
            {
                var state = Decide(out var shouldExit, worldActive: false, deadForMs: deadForMs);

                Assert.AreEqual(WorldWatchdog.WorldState.Dead, state, $"deadForMs {deadForMs}");
                Assert.IsFalse(shouldExit, $"deadForMs {deadForMs}");
            }
        }

        [TestMethod]
        public void Dead_WithExitOnDeathOff_ReportsDeadButDoesNotExit()
        {
            var state = Decide(out var shouldExit, worldActive: false, exitOnDeath: false,
                deadForMs: DeadGraceMs * 100);

            Assert.AreEqual(WorldWatchdog.WorldState.Dead, state);
            Assert.IsFalse(shouldExit);
        }

        [TestMethod]
        public void Faulted_IsDeadEvenWhileWorldActiveIsStillSet()
        {
            // The fault flag is set in the world thread's catch block, before the finally clears
            // WorldActive. A poll landing in that window must still call it dead.
            var state = Decide(out var shouldExit, faulted: true, worldActive: true, deadForMs: DeadGraceMs);

            Assert.AreEqual(WorldWatchdog.WorldState.Dead, state);
            Assert.IsTrue(shouldExit);
        }

        [TestMethod]
        public void Faulted_DuringShutdown_IsStillDead()
        {
            // shutdownInitiated excuses an inactive world, never a faulted one: a crash during shutdown
            // is still a crash.
            var state = Decide(out var shouldExit, faulted: true, worldActive: false,
                shutdownInitiated: true, deadForMs: DeadGraceMs);

            Assert.AreEqual(WorldWatchdog.WorldState.Dead, state);
            Assert.IsTrue(shouldExit);
        }

        // ---------------------------------------------------------------------------------------------
        // Normal shutdown is not a crash
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void WorldInactive_DuringShutdown_IsNotDead()
        {
            // ServerManager.ShutdownServer clears WorldActive on its way out, so without this term every
            // clean /shutdown would be classified as a crash and the watchdog would race the real
            // shutdown to exit the process.
            var state = Decide(out var shouldExit, worldActive: false, shutdownInitiated: true,
                staleMs: 3600000, deadForMs: 3600000);

            Assert.AreNotEqual(WorldWatchdog.WorldState.Dead, state);
            Assert.IsFalse(shouldExit);
        }

        [TestMethod]
        public void WorldInactive_DuringShutdown_IsTickingNotStalled()
        {
            // Not Stalled either: Stalled requires worldActive, so a shutting-down world reports Ticking.
            // Recorded here so a future change to the state ordering has to face the choice deliberately.
            var state = Decide(out _, worldActive: false, shutdownInitiated: true, staleMs: 3600000);

            Assert.AreEqual(WorldWatchdog.WorldState.Ticking, state);
        }

        // ---------------------------------------------------------------------------------------------
        // Master switch
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Disabled_NeverExitsInAnyState()
        {
            // Dead
            var dead = Decide(out var deadExit, worldActive: false, deadForMs: DeadGraceMs * 10, enabled: false);
            Assert.AreEqual(WorldWatchdog.WorldState.Dead, dead);
            Assert.IsFalse(deadExit);

            // Dead by fault
            var faulted = Decide(out var faultedExit, faulted: true, deadForMs: DeadGraceMs * 10, enabled: false);
            Assert.AreEqual(WorldWatchdog.WorldState.Dead, faulted);
            Assert.IsFalse(faultedExit);

            // Stalled, with the stall exit configured and exceeded
            var stalled = Decide(out var stalledExit, staleMs: 600000, exitOnStallMs: 60000, enabled: false);
            Assert.AreEqual(WorldWatchdog.WorldState.Stalled, stalled);
            Assert.IsFalse(stalledExit);

            // Starting
            var starting = Decide(out var startingExit, everStarted: false, worldActive: false, enabled: false);
            Assert.AreEqual(WorldWatchdog.WorldState.Starting, starting);
            Assert.IsFalse(startingExit);

            // Ticking
            var ticking = Decide(out var tickingExit, enabled: false);
            Assert.AreEqual(WorldWatchdog.WorldState.Ticking, ticking);
            Assert.IsFalse(tickingExit);
        }

        // ---------------------------------------------------------------------------------------------
        // Classification parameter set. WorldWatchdog.Classify calls Decide with exits switched off so
        // that classifying can never authorise an exit - it is used from the metrics scrape thread and
        // from Program.InitiateContainerShutdown, and neither of those may take the process down as a
        // side effect of asking a question. Only the poll loop owns exit policy. These pin that the
        // parameter set really has that property, in every state, whatever the staleness.
        // ---------------------------------------------------------------------------------------------

        private static WorldWatchdog.WorldState ClassifyLike(out bool shouldExit, bool everStarted,
            bool faulted, bool worldActive, bool shutdownInitiated, long staleMs)
        {
            // Exactly the arguments Classify passes, other than the sampled ones.
            return WorldWatchdog.Decide(everStarted, faulted, worldActive, shutdownInitiated, staleMs,
                StallMs, exitOnStallMs: 0, deadForMs: 0, exitOnDeath: false, enabled: false, out shouldExit);
        }

        [TestMethod]
        public void ClassificationParameterSet_NeverAuthorisesAnExit()
        {
            long[] staleValues = { 0, StallMs - 1, StallMs, StallMs * 1000 };

            foreach (var staleMs in staleValues)
            {
                foreach (var everStarted in new[] { true, false })
                {
                    foreach (var faulted in new[] { true, false })
                    {
                        foreach (var worldActive in new[] { true, false })
                        {
                            foreach (var shutdownInitiated in new[] { true, false })
                            {
                                ClassifyLike(out var shouldExit, everStarted, faulted, worldActive,
                                    shutdownInitiated, staleMs);

                                Assert.IsFalse(shouldExit,
                                    $"everStarted {everStarted}, faulted {faulted}, worldActive {worldActive}, " +
                                    $"shutdownInitiated {shutdownInitiated}, staleMs {staleMs}");
                            }
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void ClassificationParameterSet_StillClassifiesCorrectly()
        {
            // Turning the exit knobs off must not change the STATE, only the exit decision - otherwise
            // the shutdown triage and the watchdog would be reading two different worlds.
            Assert.AreEqual(WorldWatchdog.WorldState.Starting,
                ClassifyLike(out _, everStarted: false, faulted: false, worldActive: false, shutdownInitiated: false, staleMs: 0));

            Assert.AreEqual(WorldWatchdog.WorldState.Ticking,
                ClassifyLike(out _, everStarted: true, faulted: false, worldActive: true, shutdownInitiated: false, staleMs: 0));

            Assert.AreEqual(WorldWatchdog.WorldState.Stalled,
                ClassifyLike(out _, everStarted: true, faulted: false, worldActive: true, shutdownInitiated: false, staleMs: StallMs));

            Assert.AreEqual(WorldWatchdog.WorldState.Dead,
                ClassifyLike(out _, everStarted: true, faulted: false, worldActive: false, shutdownInitiated: false, staleMs: 0));

            Assert.AreEqual(WorldWatchdog.WorldState.Dead,
                ClassifyLike(out _, everStarted: false, faulted: true, worldActive: false, shutdownInitiated: false, staleMs: 0));

            // A normal shutdown is still not a crash on this parameter set either.
            Assert.AreNotEqual(WorldWatchdog.WorldState.Dead,
                ClassifyLike(out _, everStarted: true, faulted: false, worldActive: false, shutdownInitiated: true, staleMs: StallMs * 10));
        }

        [TestMethod]
        public void ShutdownTriageSet_IsExactlyTheNonServiceableStates()
        {
            // Program.InitiateContainerShutdown takes the fast path on {Dead, Stalled}. That set has to
            // be exactly the states in which the world cannot service a warned countdown: Ticking can,
            // and Starting must not be fast-pathed either, because a stop arriving during preload has a
            // live process that will reach a normal shutdown on its own.
            var triaged = new[] { WorldWatchdog.WorldState.Dead, WorldWatchdog.WorldState.Stalled };

            Assert.IsTrue(System.Array.IndexOf(triaged,
                ClassifyLike(out _, everStarted: true, faulted: true, worldActive: true, shutdownInitiated: false, staleMs: 0)) >= 0,
                "a faulted world must be fast-pathed");

            Assert.IsTrue(System.Array.IndexOf(triaged,
                ClassifyLike(out _, everStarted: true, faulted: false, worldActive: true, shutdownInitiated: false, staleMs: StallMs)) >= 0,
                "a stalled world must be fast-pathed");

            Assert.IsTrue(System.Array.IndexOf(triaged,
                ClassifyLike(out _, everStarted: true, faulted: false, worldActive: true, shutdownInitiated: false, staleMs: 0)) < 0,
                "a ticking world must take the normal warned shutdown");

            Assert.IsTrue(System.Array.IndexOf(triaged,
                ClassifyLike(out _, everStarted: false, faulted: false, worldActive: false, shutdownInitiated: false, staleMs: StallMs * 100)) < 0,
                "a world still preloading must take the normal shutdown, however long it has been");
        }

        // ---------------------------------------------------------------------------------------------
        // Enum contract - the integers are published as ace.world.status and must not be renumbered.
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void WorldStateValues_ArePinned()
        {
            Assert.AreEqual(0, (int)WorldWatchdog.WorldState.Starting);
            Assert.AreEqual(1, (int)WorldWatchdog.WorldState.Ticking);
            Assert.AreEqual(2, (int)WorldWatchdog.WorldState.Stalled);
            Assert.AreEqual(3, (int)WorldWatchdog.WorldState.Dead);
        }
    }
}
