using System;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers ThreadDungeonManager.ShouldEnd, the pure half of the reaping decision in Tick(). Nothing here
    /// touches a landblock, a player or PropertyManager (whose reads throw under the test harness) - the
    /// world state Tick() samples is passed in as plain arguments.
    /// </summary>
    [TestClass]
    public class ThreadDungeonManagerRulesTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Far enough ahead that the TTL never fires unless a test asks for it.</summary>
        private static readonly DateTime NotExpired = Now.AddHours(3);

        [TestMethod]
        public void Starting_run_with_no_landblock_is_reaped()
        {
            // The regression this exists for: GetLandblock registers the instance synchronously, before
            // Init(), so a Starting run with no landblock is one whose copy never came up. Skipping the probe
            // while Starting pinned it in the registry for the whole TTL and locked its owner out for hours.
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Starting, landblockPresent: false,
                createCompleted: false, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromSeconds(5), sinceCleared: TimeSpan.Zero, now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsTrue(end);
            Assert.AreEqual("landblock unloaded", reason);
        }

        [TestMethod]
        public void Starting_run_whose_landblock_never_finished_loading_is_reaped_after_the_timeout()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Starting, landblockPresent: true,
                createCompleted: false, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromSeconds(61), sinceCleared: TimeSpan.Zero, now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsTrue(end);
            Assert.AreEqual("landblock failed to load", reason);
        }

        [TestMethod]
        public void Starting_run_still_inside_the_load_timeout_is_kept()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Starting, landblockPresent: true,
                createCompleted: false, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromSeconds(10), sinceCleared: TimeSpan.Zero, now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsFalse(end);
            Assert.IsNull(reason);
        }

        [TestMethod]
        public void Active_run_with_a_live_landblock_is_kept()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Active, landblockPresent: true,
                createCompleted: true, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromMinutes(40), sinceCleared: TimeSpan.Zero, now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsFalse(end);
            Assert.IsNull(reason);
        }

        [TestMethod]
        public void Active_run_whose_landblock_unloaded_is_reaped()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Active, landblockPresent: false,
                createCompleted: true, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromMinutes(40), sinceCleared: TimeSpan.Zero, now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsTrue(end);
            Assert.AreEqual("landblock unloaded", reason);
        }

        [TestMethod]
        public void Expiry_wins_over_every_other_reason()
        {
            var expired = Now.AddMinutes(-1);

            foreach (var state in new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Cleared })
            {
                foreach (var present in new[] { true, false })
                {
                    var end = ThreadDungeonManager.ShouldEnd(state, present, createCompleted: false, playerEverObserved: true, playerPresent: false,
                        sinceStart: TimeSpan.FromHours(4), sinceCleared: TimeSpan.Zero, now: Now, expiresUtc: expired, out var reason);

                    Assert.IsTrue(end, $"{state} present={present}");
                    Assert.AreEqual("expired", reason, $"{state} present={present}");
                }
            }
        }

        /// <summary>
        /// The owner report this branch exists for: finish a dungeon, walk out, and the next gem is refused
        /// with "You already have a dungeon open" because the finished run sat in the registry until its TTL.
        /// Once the copy is empty and the grace has passed, the run ends itself.
        /// </summary>
        [TestMethod]
        public void Cleared_and_empty_run_is_reaped_once_the_grace_has_passed()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true,
                createCompleted: true, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromMinutes(20),
                sinceCleared: TimeSpan.FromSeconds(30), now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsTrue(end);
            Assert.AreEqual("cleared and empty", reason);
        }

        /// <summary>
        /// The player probe is the real gate: someone still looting or walking to the exit portal must never
        /// be yanked, however long ago the run cleared.
        /// </summary>
        [TestMethod]
        public void Cleared_run_with_a_player_still_inside_is_kept()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true,
                createCompleted: true, playerEverObserved: true, playerPresent: true, sinceStart: TimeSpan.FromMinutes(20),
                sinceCleared: TimeSpan.FromMinutes(10), now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsFalse(end);
            Assert.IsNull(reason);
        }

        /// <summary>
        /// The grace covers the entry race: a run whose plan placed nothing is Cleared before its owner has
        /// finished materialising, and "no player inside" is then a lie rather than a fact.
        /// </summary>
        [TestMethod]
        public void Cleared_and_empty_run_inside_the_grace_is_kept()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true,
                createCompleted: true, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromSeconds(3),
                sinceCleared: TimeSpan.FromSeconds(29), now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsFalse(end);
            Assert.IsNull(reason);
        }

        /// <summary>
        /// The control for the two above: the new branch is gated on Cleared, so an ACTIVE run standing empty
        /// (its owner logged out, or stepped out to sell) is left exactly as it was before. Without this the
        /// three tests above would pass just as happily against a rule that reaped any empty copy.
        /// </summary>
        [TestMethod]
        public void Active_run_standing_empty_is_still_kept()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Active, landblockPresent: true,
                createCompleted: true, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromMinutes(20),
                sinceCleared: TimeSpan.Zero, now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsFalse(end);
            Assert.IsNull(reason);
        }

        /// <summary>
        /// A second POSITIVE case, with a different grace/probe combination from the one above, so the
        /// "branch exists at all" claim does not rest on a single assertion: a run cleared long ago whose
        /// copy has been empty since is reaped just the same.
        /// </summary>
        [TestMethod]
        public void Cleared_and_empty_run_long_past_the_grace_is_reaped()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true,
                createCompleted: true, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromMinutes(90),
                sinceCleared: TimeSpan.FromMinutes(12), now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsTrue(end);
            Assert.AreEqual("cleared and empty", reason);
        }

        /// <summary>
        /// The entry-race precondition, as a discriminating PAIR: the two calls differ in exactly one
        /// argument, so this fails both against a build that deleted the Cleared branch and against one that
        /// simply ignores playerEverObserved.
        ///
        /// "Nobody is inside" is a fact once somebody has been inside and left. For a run whose owner is
        /// still in transit it only means "not yet", and the reap's probe cannot tell those apart, because
        /// Landblock.GetAllWorldObjectsForDiagnostics does not flush pending additions. So a run nobody has
        /// ever entered is never reaped for standing empty, however long ago it cleared.
        /// </summary>
        [TestMethod]
        public void Cleared_run_is_reaped_only_after_a_player_was_observed_inside()
        {
            var neverEntered = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true,
                createCompleted: true, playerEverObserved: false, playerPresent: false, sinceStart: TimeSpan.FromMinutes(20),
                sinceCleared: TimeSpan.FromMinutes(5), now: Now, expiresUtc: NotExpired, out var neverReason);

            Assert.IsFalse(neverEntered, "a run nobody ever entered must fall to the TTL, not to the empty reap");
            Assert.IsNull(neverReason);

            var wasEntered = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true,
                createCompleted: true, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromMinutes(20),
                sinceCleared: TimeSpan.FromMinutes(5), now: Now, expiresUtc: NotExpired, out var wasReason);

            Assert.IsTrue(wasEntered, "same inputs but the run WAS entered, so it must reap");
            Assert.AreEqual("cleared and empty", wasReason);
        }

        /// <summary>
        /// The 8-field /loc form. Its tail is qw qx qy qz while the Position constructor takes its rotation w
        /// LAST (ACE.Entity/Position.cs:246), so a straight-through copy would silently rotate every entry.
        /// </summary>
        [TestMethod]
        public void TryParseEntry_reads_the_loc_quaternion_into_the_right_slots()
        {
            Assert.IsTrue(ThreadDungeonManager.TryParseEntry("0x0150018A 30 12 0.005 0.1 0.2 0.3 0.4", out var pos));

            Assert.AreEqual(0x0150018Au, pos.Cell);
            Assert.AreEqual(30f, pos.PositionX, 0.0001f);
            Assert.AreEqual(12f, pos.PositionY, 0.0001f);
            Assert.AreEqual(0.005f, pos.PositionZ, 0.0001f);
            Assert.AreEqual(0.1f, pos.RotationW, 0.0001f);
            Assert.AreEqual(0.2f, pos.RotationX, 0.0001f);
            Assert.AreEqual(0.3f, pos.RotationY, 0.0001f);
            Assert.AreEqual(0.4f, pos.RotationZ, 0.0001f);
        }

        [TestMethod]
        public void TryParseEntry_defaults_the_4_field_form_to_an_identity_rotation()
        {
            Assert.IsTrue(ThreadDungeonManager.TryParseEntry("0x0150018A 30 12 0.005", out var pos));

            Assert.AreEqual(1f, pos.RotationW, 0.0001f);
            Assert.AreEqual(0f, pos.RotationX, 0.0001f);
            Assert.AreEqual(0f, pos.RotationY, 0.0001f);
            Assert.AreEqual(0f, pos.RotationZ, 0.0001f);
        }

        [TestMethod]
        public void TryParseEntry_rejects_fewer_than_four_fields()
        {
            Assert.IsFalse(ThreadDungeonManager.TryParseEntry("0x0150018A 30 12", out var pos));
            Assert.IsNull(pos);

            Assert.IsFalse(ThreadDungeonManager.TryParseEntry("", out _));
            Assert.IsFalse(ThreadDungeonManager.TryParseEntry(null, out _));
        }

        [TestMethod]
        public void An_already_ended_run_is_never_ended_again()
        {
            // Tick removes Ended runs rather than re-ending them; ShouldEnd must not claim one needs EndRun,
            // which would try to evict players and destroy the gem a second time.
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Ended, landblockPresent: false,
                createCompleted: false, playerEverObserved: true, playerPresent: false, sinceStart: TimeSpan.FromHours(4), sinceCleared: TimeSpan.Zero, now: Now, expiresUtc: Now.AddMinutes(-1), out var reason);

            Assert.IsFalse(end);
            Assert.IsNull(reason);
        }

        /// <summary>
        /// Same run shape as ThreadDungeonRunTests.NewRun(). Kept private here too: the two test classes
        /// deliberately do not share a helper, so each stays readable on its own.
        /// </summary>
        private static ThreadDungeonRun NewRun(uint runId = 0x80001234u, uint ownerGuid = 0x50000001u, uint gemGuid = 0x80000099u)
        {
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(runId, ownerGuid, "Tester", 7, gemGuid, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        /// <summary>
        /// Owner ruling A1: the gem dies the moment the run is Cleared. A run populated with nothing reaches
        /// Cleared the instant MarkPopulated runs (0 spawned, 0 killed required), so OnRunPopulated's call
        /// into AnnounceCleared is the trigger here - no creature death needed. The announcement latch
        /// (TryClaimClearedAnnouncement) is what keeps a second OnRunPopulated call from firing the destroyer
        /// again.
        /// </summary>
        [TestMethod]
        public void Cleared_destroys_the_gem_once()
        {
            var run = NewRun();
            run.MarkPopulated(0, 0, 0);

            var calls = 0;
            var original = ThreadDungeonManager.GemDestroyer;
            ThreadDungeonManager.GemDestroyer = _ => calls++;
            try
            {
                ThreadDungeonManager.OnRunPopulated(run);
                ThreadDungeonManager.OnRunPopulated(run);
            }
            finally
            {
                ThreadDungeonManager.GemDestroyer = original;
            }

            Assert.AreEqual(1, calls);
        }

        /// <summary>
        /// A run that ends WITHOUT ever clearing (e.g. it expired mid-fight) still must destroy the gem: the
        /// Cleared trigger never fired, so OnRunEnded is the only path left. OnRunEnded calls the seam
        /// directly, unconditionally - the owner-offline check that used to gate this lives inside DestroyGem
        /// now, not in OnRunEnded, so the seam call always happens.
        /// </summary>
        [TestMethod]
        public void Ended_without_clear_still_destroys_the_gem()
        {
            var run = NewRun();
            run.MarkEnded("expired");

            var calls = 0;
            var original = ThreadDungeonManager.GemDestroyer;
            ThreadDungeonManager.GemDestroyer = _ => calls++;
            try
            {
                ThreadDungeonManager.OnRunEnded(run);
            }
            finally
            {
                ThreadDungeonManager.GemDestroyer = original;
            }

            Assert.AreEqual(1, calls);
        }

        /// <summary>
        /// Cleared THEN ended: the destroyer runs twice (once from each trigger), by design - idempotency is
        /// DestroyGem's own re-resolution of the gem, not a latch here, so the routine must tolerate a second
        /// call rather than suppress it.
        /// </summary>
        [TestMethod]
        public void Cleared_then_ended_invokes_the_destroyer_twice_and_the_routine_tolerates_it()
        {
            var run = NewRun();
            run.MarkPopulated(0, 0, 0);

            var calls = 0;
            var original = ThreadDungeonManager.GemDestroyer;
            ThreadDungeonManager.GemDestroyer = _ => calls++;
            try
            {
                ThreadDungeonManager.OnRunPopulated(run);
                run.MarkEnded("expired");
                ThreadDungeonManager.OnRunEnded(run);
            }
            finally
            {
                ThreadDungeonManager.GemDestroyer = original;
            }

            Assert.AreEqual(2, calls);
        }

        /// <summary>
        /// AnnounceCleared also calls SurveyRecorder (the daily-survey stamp hook, PHASE-2-DESIGN.md
        /// section 4.1), through the same test-seam shape as GemDestroyer. The announcement latch keeps a
        /// second OnRunPopulated call from firing it again.
        /// </summary>
        [TestMethod]
        public void Cleared_records_a_survey_through_the_seam()
        {
            var run = NewRun();
            run.MarkPopulated(0, 0, 0);

            var calls = 0;
            var original = ThreadDungeonManager.SurveyRecorder;
            ThreadDungeonManager.SurveyRecorder = _ => calls++;
            try
            {
                ThreadDungeonManager.OnRunPopulated(run);
                ThreadDungeonManager.OnRunPopulated(run);
            }
            finally
            {
                ThreadDungeonManager.SurveyRecorder = original;
            }

            Assert.AreEqual(1, calls);
        }
    }
}
