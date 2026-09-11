using System;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class ThreadDungeonRunTests
    {
        private static ThreadDungeonRun NewRun()
        {
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        /// <summary>
        /// ClearedUtc is what ThreadDungeonManager.Tick ages a finished run out of the registry from, so it
        /// must be stamped by the SAME transition that sets Cleared and must stay null before it. The
        /// unfinished half of this is the real assertion: a run that has not cleared has no timestamp to
        /// measure a grace period against, and Tick passes TimeSpan.Zero for it.
        /// </summary>
        [TestMethod]
        public void Cleared_stamps_the_moment_the_run_finished()
        {
            var run = NewRun();
            Assert.IsNull(run.ClearedUtc, "Starting");

            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            Assert.IsNull(run.ClearedUtc, "Active");

            var before = DateTime.UtcNow;
            run.RecordKill(isBoss: false);
            var after = DateTime.UtcNow;

            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.IsNotNull(run.ClearedUtc);
            Assert.IsTrue(run.ClearedUtc.Value >= before && run.ClearedUtc.Value <= after,
                $"ClearedUtc {run.ClearedUtc:O} outside [{before:O}, {after:O}]");
        }

        /// <summary>
        /// The entry latch behind ThreadDungeonManager's cleared-and-empty reap. It starts false - a run
        /// nobody has entered must never be reaped for standing empty - latches on the first observation, and
        /// is never cleared afterwards, because it records that entry HAPPENED, not who is inside now.
        /// </summary>
        [TestMethod]
        public void Player_observation_starts_false_and_latches_once_set()
        {
            var run = NewRun();
            Assert.IsFalse(run.PlayerEverObserved, "nobody has entered a brand new run");

            run.MarkPlayerObserved();
            Assert.IsTrue(run.PlayerEverObserved);

            // Idempotent, and still true after the run finishes: the reap reads it AFTER the clear, when the
            // player who set it has walked out again.
            run.MarkPlayerObserved();
            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            run.RecordKill(isBoss: false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.IsTrue(run.PlayerEverObserved);
        }

        [TestMethod]
        public void New_run_is_starting_with_counters_at_zero()
        {
            var run = NewRun();
            Assert.AreEqual(ThreadDungeonRunState.Starting, run.State);
            Assert.AreEqual(0x80001234u, run.RunId);
            Assert.AreEqual(run.RunId, run.Instance);
            Assert.AreEqual(1003601u, run.ExitPortalWcid);
            Assert.AreEqual(0, run.Killed);
            Assert.IsFalse(run.BossKilled);
        }

        /// <summary>
        /// Owner ruling R28 (replaces R27's boss-OR-fraction rule): the boss is worth BossWeight (default 0.2)
        /// of a run's weighted clear progress on its own, and nothing more. Killing ONLY the boss - every
        /// trash creature still alive - now caps progress at 0.2 and does NOT clear the run, which is the
        /// direct replacement for R27's "boss kill alone clears" behavior.
        /// </summary>
        [TestMethod]
        public void Boss_alone_does_not_clear_the_run()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.MarkPopulated(planned: 20, spawned: 21, bossWcid: 10981); // 20 trash + 1 boss

            run.RecordKill(isBoss: true);

            Assert.AreEqual(1, run.Killed);
            Assert.IsTrue(run.BossKilled);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "boss alone is only 20% progress under R28");
            Assert.AreEqual(0.2, run.ClearProgress, 1e-9);
        }

        /// <summary>
        /// The mirror image of <see cref="Boss_alone_does_not_clear_the_run"/>: killing every trash creature
        /// but leaving the boss alive caps progress at (1 - BossWeight) = 0.8 by default, which is below the
        /// 0.9 clear threshold, so the run also does NOT clear on trash alone.
        /// </summary>
        [TestMethod]
        public void All_trash_dead_without_the_boss_does_not_clear_the_run()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.MarkPopulated(planned: 20, spawned: 21, bossWcid: 10981); // 20 trash + 1 boss

            for (var i = 0; i < 20; i++)
                run.RecordKill(isBoss: false);

            Assert.AreEqual(20, run.Killed);
            Assert.IsFalse(run.BossKilled);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "trash alone is only 80% progress under R28");
            Assert.AreEqual(0.8, run.ClearProgress, 1e-9);
        }

        /// <summary>
        /// R28's worked example: 16 trash + 1 boss. 0.2 (boss) + 0.8*(14/16) = 0.2 + 0.7 = 0.9, exactly the
        /// default clear fraction, so boss + 14 of 16 trash clears. One fewer trash kill (13/16 -> 0.85 total)
        /// must NOT clear, proving the threshold is not simply "boss + most of the trash".
        /// </summary>
        [TestMethod]
        public void Boss_plus_87_5_percent_trash_clears_but_85_percent_does_not()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.MarkPopulated(planned: 16, spawned: 17, bossWcid: 10981); // 16 trash + 1 boss

            run.RecordKill(isBoss: true);
            for (var i = 0; i < 13; i++)
                run.RecordKill(isBoss: false);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "boss + 13/16 trash = 0.85, below 0.9");

            run.RecordKill(isBoss: false); // 14th trash kill
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "boss + 14/16 trash = 0.9, exactly the threshold");
        }

        /// <summary>
        /// Same scenario as <see cref="Boss_plus_87_5_percent_trash_clears_but_85_percent_does_not"/>, but the
        /// boss is killed LAST instead of first. ClearProgress is a pure function of the current kill counts,
        /// not of the order they were recorded in, so the run must clear at the identical 15th kill either way.
        ///
        /// Note what this one does NOT guard: both assertions would also hold under R27, whose boss-kill path
        /// cleared the run outright. The R27 regression guards are Boss_alone_does_not_clear and
        /// Boss_plus_87_5_percent_trash_clears_but_85_percent_does_not; this test guards order-independence
        /// only, against a future change that starts recording progress incrementally instead of recomputing
        /// it from the current counts.
        /// </summary>
        [TestMethod]
        public void Clearing_at_87_5_percent_trash_is_independent_of_kill_order()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.MarkPopulated(planned: 16, spawned: 17, bossWcid: 10981); // 16 trash + 1 boss

            for (var i = 0; i < 14; i++)
                run.RecordKill(isBoss: false);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "14/16 trash with the boss still alive is only 0.7");

            run.RecordKill(isBoss: true);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "same 15 total kills as boss-first, same 0.9 progress");
        }

        /// <summary>
        /// A run with no boss (none authored, or the boss failed to place) renormalizes: its trash alone is
        /// worth the FULL clear progress rather than being permanently capped at (1 - BossWeight). ClearTarget
        /// falls straight back to ceil(ClearFraction * Spawned) - 18 of 20, not the 20 the un-renormalized
        /// formula would demand - and 18 kills clear while 17 does not.
        /// </summary>
        [TestMethod]
        public void Bossless_run_renormalizes_trash_to_the_full_clear_fraction()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.MarkPopulated(planned: 20, spawned: 20, bossWcid: 0);

            Assert.AreEqual(18, run.ClearTarget);

            for (var i = 0; i < 17; i++)
                run.RecordKill(isBoss: false);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "17/20 == 0.85, below 0.9");

            run.RecordKill(isBoss: false); // 18th kill
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
        }

        /// <summary>
        /// ClearTarget on a bossed run is now the TRASH kills needed assuming the boss is already dead:
        /// requiredTrashFraction = (ClearFraction - BossWeight) / (1 - BossWeight) = (0.9 - 0.2) / 0.8 = 0.875,
        /// and ceil(0.875 * 16) = 14 - matching the worked example in
        /// <see cref="Boss_plus_87_5_percent_trash_clears_but_85_percent_does_not"/>.
        /// </summary>
        [TestMethod]
        public void ClearTarget_on_a_bossed_run_is_the_required_trash_kills_not_total_kills()
        {
            var run = NewRun();
            run.ClearFraction = 0.9;
            run.MarkPopulated(planned: 16, spawned: 17, bossWcid: 10981); // 16 trash + 1 boss

            Assert.AreEqual(14, run.ClearTarget);
        }

        /// <summary>
        /// 0.07 * 100 == 7.0000000000000009 in double arithmetic, which Math.Ceiling alone rounds up to 8.
        /// ClearTargetLocked subtracts a 1e-9 epsilon before ceiling-ing so the target lands on the exact 7.
        /// Bossless, so requiredTrashFraction is ClearFraction directly - this is the same product the old
        /// rule guarded, still reachable through the new formula's "no boss present" branch.
        /// </summary>
        [TestMethod]
        public void ClearTarget_absorbs_floating_point_error_at_the_ceiling()
        {
            var run = NewRun();
            run.ClearFraction = 0.07;
            run.MarkPopulated(planned: 100, spawned: 100, bossWcid: 0);

            Assert.AreEqual(7, run.ClearTarget);
        }

        /// <summary>
        /// Same float-error guard as <see cref="ClearTarget_absorbs_floating_point_error_at_the_ceiling"/>, but
        /// exercised through the NEW division branch: BossWeight 0 (boss present but worth nothing) makes
        /// requiredTrashFraction = (0.07 - 0) / (1 - 0) = 0.07, then 0.07 * 100 hits the same
        /// 7.0000000000000009 double-precision overshoot on the trash pool (100 of the 101 spawned, one slot
        /// taken by the boss) as the bossless case - guarding that the epsilon still applies once the target
        /// is computed via Math.Clamp((ClearFraction - BossWeight) / (1 - BossWeight), ...) rather than
        /// ClearFraction used directly.
        /// </summary>
        [TestMethod]
        public void ClearTarget_absorbs_floating_point_error_through_the_boss_weighted_division()
        {
            var run = NewRun();
            run.ClearFraction = 0.07;
            run.BossWeight = 0.0;
            run.MarkPopulated(planned: 100, spawned: 101, bossWcid: 10981); // 100 trash + 1 boss

            Assert.AreEqual(7, run.ClearTarget);
        }

        /// <summary>BossWeight and ClearFraction have different no-caller defaults, and both matter: ClearFraction's
        /// 1.0 preserves the old kill-everything behavior for callers that never touch it, while BossWeight's
        /// 0.2 is this field's own real production default (see PropertyManager's dynamic_dungeons_boss_clear_weight),
        /// not a "do nothing" value.</summary>
        [TestMethod]
        public void BossWeight_and_ClearFraction_have_their_documented_no_caller_defaults()
        {
            var run = NewRun();
            Assert.AreEqual(1.0, run.ClearFraction);
            Assert.AreEqual(0.2, run.BossWeight);
        }

        /// <summary>
        /// ClearProgress must never read above 1.0 even if Killed somehow overshoots Spawned (should not
        /// happen in the real spawner, but the formula's Math.Min clamp is what guarantees it regardless).
        /// ClearFraction is deliberately set above 1.0 here (a value PropertyManager's read-time clamp would
        /// never allow, but nothing in ThreadDungeonRun itself enforces it) purely so the run stays Active
        /// long enough to record the extra, unmatched kill.
        /// </summary>
        [TestMethod]
        public void Clear_progress_never_exceeds_one_even_if_killed_overshoots_spawned()
        {
            var run = NewRun();
            run.ClearFraction = 1.5;
            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);

            run.RecordKill(isBoss: false);
            run.RecordKill(isBoss: false);
            run.RecordKill(isBoss: false); // one more than Spawned

            Assert.AreEqual(3, run.Killed);
            Assert.AreEqual(1.0, run.ClearProgress, 1e-9);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "1.0 < 1.5, so it never reaches Cleared here");
        }

        [TestMethod]
        public void Expiry_is_start_plus_ttl()
        {
            var run = NewRun();
            Assert.IsTrue(run.ExpiresUtc > run.StartedUtc.AddMinutes(179) && run.ExpiresUtc < run.StartedUtc.AddMinutes(181));
            Assert.IsFalse(run.IsExpired(run.StartedUtc.AddMinutes(10)));
            Assert.IsTrue(run.IsExpired(run.StartedUtc.AddMinutes(181)));
        }

        [TestMethod]
        public void Ended_is_terminal()
        {
            var run = NewRun();
            run.MarkEnded("test");
            Assert.AreEqual(ThreadDungeonRunState.Ended, run.State);
            run.RecordKill(isBoss: true);
            Assert.AreEqual(ThreadDungeonRunState.Ended, run.State);
        }

        /// <summary>
        /// The spawner places in batches across several landblock action-queue steps, so everything an early
        /// batch placed is alive and killable while the later batches are still running. A kill landed before
        /// MarkPopulated must still count, or killed stays permanently short of spawned and the run can never
        /// clear.
        /// </summary>
        [TestMethod]
        public void Kills_landed_while_still_starting_are_counted()
        {
            var run = NewRun();

            run.RecordKill(isBoss: false);
            run.RecordKill(isBoss: false);
            Assert.AreEqual(2, run.Killed);
            Assert.AreEqual(ThreadDungeonRunState.Starting, run.State, "counting a kill does not populate the run");

            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
        }

        /// <summary>A boss killed during Starting is still recorded as the boss kill.</summary>
        [TestMethod]
        public void A_boss_killed_while_still_starting_still_clears_the_run()
        {
            var run = NewRun();

            run.RecordKill(isBoss: false);
            run.RecordKill(isBoss: true);
            Assert.IsTrue(run.BossKilled);

            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 10981);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
        }

        /// <summary>A run whose plan placed nothing is Cleared the moment it is populated.</summary>
        [TestMethod]
        public void An_empty_population_clears_immediately()
        {
            var run = NewRun();
            run.MarkPopulated(planned: 0, spawned: 0, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
        }

        /// <summary>
        /// Two paths announce the clear - the last creature's death and the populate step for an empty plan -
        /// so the latch is what stops the player being told twice.
        /// </summary>
        [TestMethod]
        public void The_cleared_announcement_is_claimed_exactly_once()
        {
            var run = NewRun();

            Assert.IsFalse(run.TryClaimClearedAnnouncement(), "nothing to announce while Starting");

            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            Assert.IsFalse(run.TryClaimClearedAnnouncement(), "nothing to announce while Active");

            run.RecordKill(isBoss: false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);

            Assert.IsTrue(run.TryClaimClearedAnnouncement());
            Assert.IsFalse(run.TryClaimClearedAnnouncement());
            Assert.IsFalse(run.TryClaimClearedAnnouncement());
        }

        /// <summary>A kill arriving after the clear does not reopen the announcement.</summary>
        [TestMethod]
        public void A_late_kill_does_not_re_announce_the_clear()
        {
            var run = NewRun();
            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            run.RecordKill(isBoss: false);
            Assert.IsTrue(run.TryClaimClearedAnnouncement());

            run.RecordKill(isBoss: false);
            Assert.AreEqual(1, run.Killed, "the ledger is closed once Cleared");
            Assert.IsFalse(run.TryClaimClearedAnnouncement());
        }
    }
}
