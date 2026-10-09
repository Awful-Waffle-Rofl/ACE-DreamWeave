using System;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Owner ruling R32 as amended 2026-09-08: "After each kill, the player should see a message 'Monsters
    /// killed x/y. Boss still remaining', where x is number killed and y is the number that must be cleared
    /// for completion, not total in dungeon". This replaced the three latched 25/50/75 "% remaining"
    /// milestones the first form of R32 shipped (DungeonRunKillProgressTests supersedes
    /// DungeonRunProgressMilestoneTests wholesale).
    ///
    /// ThreadDungeonRun.TryGetKillProgress is the whole of the rule - the manager only turns the three
    /// values it hands back into a chat line - so what is asserted here is: every kill reports, the
    /// denominator is the CLEAR TARGET rather than the population, x never overshoots y, the boss is tracked
    /// separately from the count, and a kill that finishes the run reports nothing at all.
    /// </summary>
    [TestClass]
    public class DungeonRunKillProgressTests
    {
        private static ThreadDungeonRun NewRun()
        {
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        /// <summary>
        /// The headline shape: a line after EVERY kill, counting up to the clear target rather than to the
        /// population. ClearFraction 0.5 over 8 bossless trash puts the target at 4, so the first three kills
        /// read 1/4, 2/4, 3/4 - and the run is still Active at 3, which is what makes those lines a progress
        /// report rather than an epitaph.
        ///
        /// The old milestone form went silent on most kills by design. Nothing here may be silent.
        /// </summary>
        [TestMethod]
        public void Every_kill_reports_a_count_against_the_clear_target()
        {
            var run = NewRun();
            run.ClearFraction = 0.5;
            run.MarkPopulated(planned: 8, spawned: 8, bossWcid: 0);

            Assert.AreEqual(4, run.ClearTarget, "guard: half of 8 trash, no boss to withhold weight");

            for (var expected = 1; expected <= 3; expected++)
            {
                run.RecordKill(isBoss: false);

                Assert.IsTrue(run.TryGetKillProgress(out var killed, out var required, out var bossRemaining), $"kill {expected} must report");
                Assert.AreEqual(expected, killed);
                Assert.AreEqual(4, required);
                Assert.IsFalse(bossRemaining, "no boss placed in this run");
                Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            }
        }

        /// <summary>
        /// The denominator is the CLEAR TARGET, not the population, and this is the case that separates the
        /// two: at the shipped tunables (ClearFraction 0.9, BossWeight 0.2) a 40-trash run with a boss needs
        /// ceil((0.9 - 0.2) / 0.8 * 40) = 35 trash kills, not 40 and not 41. Quoting the population would show
        /// the player a target they never have to reach.
        /// </summary>
        [TestMethod]
        public void The_denominator_is_the_clear_target_not_the_population()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.BossWeight = 0.2;
            run.MarkPopulated(planned: 41, spawned: 41, bossWcid: 10814);

            Assert.AreEqual(40, run.TrashSpawned, "guard: 41 placed, one of them the boss");

            run.RecordKill(isBoss: false);

            Assert.IsTrue(run.TryGetKillProgress(out var killed, out var required, out _));
            Assert.AreEqual(1, killed);
            Assert.AreEqual(35, required, "the trash needed once the boss is dead, not the trash placed");
        }

        /// <summary>
        /// The boss clause is a separate sentence because the boss is a separate requirement (R28): its
        /// BossWeight cannot be bought with trash kills. So the flag is true while it lives and false the
        /// moment it dies, independent of where the trash count stands.
        /// </summary>
        [TestMethod]
        public void The_boss_flag_tracks_the_boss_and_nothing_else()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.BossWeight = 0.2;
            run.MarkPopulated(planned: 11, spawned: 11, bossWcid: 10814);

            run.RecordKill(isBoss: false);
            Assert.IsTrue(run.TryGetKillProgress(out _, out _, out var aliveBoss));
            Assert.IsTrue(aliveBoss, "the boss is placed and alive");

            run.RecordKill(isBoss: true);
            Assert.IsTrue(run.TryGetKillProgress(out var killed, out var required, out var deadBoss));
            Assert.IsFalse(deadBoss, "the boss is dead, so the clause drops");
            Assert.AreEqual(1, killed, "a boss kill is not a trash kill and must not advance x");
            Assert.AreEqual(9, required, "ceil(0.875 * 10) - the target is unmoved by the boss dying");
        }

        /// <summary>
        /// x is CLAMPED to y. Trash kills legitimately exceed the target while the boss is alive - the run
        /// does not clear on trash alone - and "16/14" reads as a bug to the player. 16 trash at the shipped
        /// tunables needs 14, so killing all 16 must still report 14/14 with the boss outstanding.
        /// </summary>
        [TestMethod]
        public void Trash_kills_past_the_target_are_clamped_rather_than_overshooting()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.BossWeight = 0.2;
            run.MarkPopulated(planned: 17, spawned: 17, bossWcid: 10814);

            Assert.AreEqual(14, run.ClearTarget, "guard: ceil(0.875 * 16)");

            for (var i = 0; i < 16; i++)
                run.RecordKill(isBoss: false);

            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "guard: trash alone cannot clear a run under R28");
            Assert.IsTrue(run.TryGetKillProgress(out var killed, out var required, out var bossRemaining));
            Assert.AreEqual(14, killed, "16 trash dead, but the line must not read 16/14");
            Assert.AreEqual(14, required);
            Assert.IsTrue(bossRemaining);
        }

        /// <summary>
        /// The silence rule survives the rewrite: when the same kill takes the run to Cleared, the clear
        /// message is the ONLY message. A one-creature run makes it unambiguous - anything else would print a
        /// progress line immediately followed by "the dungeon is cleared".
        ///
        /// The mechanism is the Active check: RecordKill runs CheckClearedLocked inside its own critical
        /// section, so the run has already left Active before the manager asks for a line.
        /// </summary>
        [TestMethod]
        public void A_kill_that_clears_the_run_reports_nothing()
        {
            var run = NewRun();
            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);

            run.RecordKill(isBoss: false);

            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.IsFalse(run.TryGetKillProgress(out var killed, out var required, out var bossRemaining));
            Assert.AreEqual(0, killed, "nothing reported means nothing to print");
            Assert.AreEqual(0, required);
            Assert.IsFalse(bossRemaining);

            // And the clear announcement itself is untouched by any of this.
            Assert.IsTrue(run.TryClaimClearedAnnouncement());
        }

        /// <summary>
        /// The same rule at the other end of a run: a plan that placed nothing is Cleared the instant it is
        /// populated (ClearProgress is vacuously 1.0 with no trash to kill), and it must narrate nothing on
        /// the way there.
        /// </summary>
        [TestMethod]
        public void An_empty_plan_reports_nothing()
        {
            var run = NewRun();
            run.MarkPopulated(planned: 0, spawned: 0, bossWcid: 0);

            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "guard: an empty population clears immediately");
            Assert.IsFalse(run.TryGetKillProgress(out _, out _, out _));
        }

        /// <summary>
        /// Nothing is reported while the run is still Starting. This is not a theoretical state: the spawner
        /// places in batches across several landblock steps and RecordKill deliberately counts kills landed
        /// before MarkPopulated, so a fast player really can kill an early-batch creature while Spawned is
        /// still 0. Reporting there would quote a y of 0 that then moves under the player.
        /// </summary>
        [TestMethod]
        public void Nothing_is_reported_while_the_run_is_starting()
        {
            var run = NewRun();

            Assert.AreEqual(ThreadDungeonRunState.Starting, run.State);
            Assert.IsFalse(run.TryGetKillProgress(out _, out _, out _), "no line before the population is settled");

            run.RecordKill(isBoss: false); // an early-batch kill, banked while still Starting
            Assert.IsFalse(run.TryGetKillProgress(out _, out _, out _));

            // Once populated, the banked kill counts toward the real pool and the ordinary rule resumes.
            run.ClearFraction = 0.5;
            run.MarkPopulated(planned: 8, spawned: 8, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            Assert.IsTrue(run.TryGetKillProgress(out var killed, out var required, out _), "the banked kill counts against a settled pool");
            Assert.AreEqual(1, killed);
            Assert.AreEqual(4, required);
        }

        /// <summary>
        /// A run that ends without clearing - a TTL expiry, a /dd end, the owner walking out - stops
        /// narrating. Ended is not Active, so the same single check covers it; asserted separately because it
        /// is the one non-Active state no other test in this class visits, and a run being torn down must not
        /// push a progress line at the player as it goes.
        /// </summary>
        [TestMethod]
        public void An_ended_run_reports_nothing()
        {
            var run = NewRun();
            run.MarkPopulated(planned: 4, spawned: 4, bossWcid: 0);

            Assert.IsTrue(run.MarkEnded("ttl"));
            run.RecordKill(isBoss: false);

            Assert.IsFalse(run.TryGetKillProgress(out _, out _, out _));
        }

        /// <summary>
        /// The degenerate tunable, kept from the milestone suite because it asserts the real mechanism rather
        /// than an assumed containment: CheckClearedLocked tests ClearProgress >= ClearFraction - 1e-9 and
        /// ClearProgress is never negative, so ClearFraction 0 is satisfied by the very MarkPopulated that
        /// moves the run to Active. The run is Cleared before that call returns and is never observably
        /// Active, so the Active check refuses every line outright.
        /// </summary>
        [TestMethod]
        public void A_zero_clear_fraction_clears_on_populate_and_reports_nothing()
        {
            var run = NewRun();
            run.ClearFraction = 0.0;
            run.MarkPopulated(planned: 10, spawned: 10, bossWcid: 0);

            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State,
                "a zero clear target is met before MarkPopulated returns, so Active is never observable");
            Assert.IsFalse(run.TryGetKillProgress(out _, out _, out _));

            // And a kill arriving afterwards cannot reopen it either.
            run.RecordKill(isBoss: false);
            Assert.IsFalse(run.TryGetKillProgress(out _, out _, out _));
        }
    }
}
