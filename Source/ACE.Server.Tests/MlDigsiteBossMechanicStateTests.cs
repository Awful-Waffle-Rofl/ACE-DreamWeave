using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Boss Rush driver's state machine: the step queue that IS the scheduler, the per-slot cadence, the
    /// immune-phase latches, the interrupt window's single latch, and the safe-zone miss ledger.
    ///
    /// Every transition in here is one that goes wrong SILENTLY in a live fight - a phase that never ends, a
    /// window that resolves twice, a cadence that fires a backlog - which is precisely why it is all in one
    /// lock-guarded object with no world dependency, and why it can be driven here with a fake clock.
    ///
    /// Order-independent: state is built fresh per test and the only PropertyManager state touched is the code
    /// defaults loaded once below (MlDigsiteEncounter construction reads them), which no test modifies.
    /// </summary>
    [TestClass]
    public class MlDigsiteBossMechanicStateTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static readonly DateTime T0 = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        private static MlDigsiteMechanicSet Set(MlDigsiteMechanic main, MlDigsiteMechanic secondary = MlDigsiteMechanic.None)
            => new MlDigsiteMechanicSet(1, main, secondary, 0.25, 1.30);

        private static MlDigsiteBossMechanicState NewState(MlDigsiteMechanic main = MlDigsiteMechanic.Volatile,
            MlDigsiteMechanic secondary = MlDigsiteMechanic.None)
            => new MlDigsiteBossMechanicState(Set(main, secondary), T0);

        // ---- the step queue ------------------------------------------------------------------------------

        [TestMethod]
        public void A_step_due_in_the_past_fires_exactly_once()
        {
            var state = NewState();
            var fired = 0;

            state.Schedule(T0.AddSeconds(2), () => fired++);

            Assert.IsNull(state.TakeDueSteps(T0.AddSeconds(1)), "not due yet");
            Assert.AreEqual(0, fired);
            Assert.AreEqual(1, state.PendingStepCount);

            var due = state.TakeDueSteps(T0.AddSeconds(5));

            Assert.IsNotNull(due);
            Assert.AreEqual(1, due.Count);

            foreach (var step in due)
                step();

            Assert.AreEqual(1, fired);
            Assert.AreEqual(0, state.PendingStepCount, "a taken step is off the queue");
            Assert.IsNull(state.TakeDueSteps(T0.AddSeconds(60)), "and never comes back");
        }

        [TestMethod]
        public void TakeDueSteps_returns_null_rather_than_an_empty_list_on_the_common_path()
        {
            // The driver runs on the digsite tick for every live encounter; "nothing is due" is the case that
            // happens on nearly every tick and it must not allocate.
            var state = NewState();

            Assert.IsNull(state.TakeDueSteps(T0), "nothing scheduled at all");

            state.Schedule(T0.AddSeconds(30), () => { });

            Assert.IsNull(state.TakeDueSteps(T0.AddSeconds(1)), "scheduled but not due");
        }

        [TestMethod]
        public void Steps_come_back_in_the_order_they_were_scheduled_not_in_due_order()
        {
            // The drum cadence schedules beat 1, beat 2 and the resolve up front. A tick that catches two of
            // them at once must still run them in that order or the resolve lands before its own telegraph.
            var state = NewState();
            var order = new List<string>();

            state.Schedule(T0.AddSeconds(1), () => order.Add("beat1"));
            state.Schedule(T0.AddSeconds(2), () => order.Add("beat2"));
            state.Schedule(T0.AddSeconds(4), () => order.Add("resolve"));

            foreach (var step in state.TakeDueSteps(T0.AddSeconds(2)))
                step();

            CollectionAssert.AreEqual(new[] { "beat1", "beat2" }, order.ToArray());
            Assert.AreEqual(1, state.PendingStepCount, "the resolve is still pending");

            foreach (var step in state.TakeDueSteps(T0.AddSeconds(4)))
                step();

            CollectionAssert.AreEqual(new[] { "beat1", "beat2", "resolve" }, order.ToArray());
        }

        [TestMethod]
        public void A_partly_due_queue_keeps_the_steps_that_are_not_due_yet()
        {
            var state = NewState();
            var ran = new List<int>();

            for (var i = 1; i <= 5; i++)
            {
                var n = i;
                state.Schedule(T0.AddSeconds(n), () => ran.Add(n));
            }

            foreach (var step in state.TakeDueSteps(T0.AddSeconds(3)))
                step();

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ran.ToArray());
            Assert.AreEqual(2, state.PendingStepCount);

            foreach (var step in state.TakeDueSteps(T0.AddSeconds(99)))
                step();

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, ran.ToArray());
            Assert.AreEqual(0, state.PendingStepCount);
        }

        [TestMethod]
        public void ClearSteps_empties_the_queue_and_refuses_every_later_step()
        {
            // This is the whole of the driver's teardown guarantee: after MlDigsiteManager stops it, no step
            // can fire at an object the encounter has already destroyed.
            var state = NewState();
            var fired = 0;

            state.Schedule(T0.AddSeconds(1), () => fired++);
            state.Schedule(T0.AddSeconds(2), () => fired++);
            state.Schedule(T0.AddSeconds(3), () => fired++);

            Assert.AreEqual(3, state.ClearSteps(), "reports how many it dropped, for the log line");
            Assert.AreEqual(0, state.PendingStepCount);
            Assert.IsNull(state.TakeDueSteps(T0.AddSeconds(99)));
            Assert.AreEqual(0, fired);

            // and a step scheduled afterwards - by a step that was already running, or by a module that had
            // not noticed - cannot resurrect the driver
            state.Schedule(T0.AddSeconds(4), () => fired++);

            Assert.AreEqual(0, state.PendingStepCount);
            Assert.IsNull(state.TakeDueSteps(T0.AddSeconds(999)));
            Assert.AreEqual(0, fired);
            Assert.AreEqual(0, state.ClearSteps(), "a second stop drops nothing");
        }

        [TestMethod]
        public void A_null_step_is_ignored_rather_than_queued_as_a_null_reference()
        {
            var state = NewState();

            state.Schedule(T0, null);

            Assert.AreEqual(0, state.PendingStepCount);
            Assert.IsNull(state.TakeDueSteps(T0.AddSeconds(1)));
        }

        // ---- the per-slot cadence ------------------------------------------------------------------------

        [TestMethod]
        public void The_first_cadence_claim_only_arms_the_clock_and_never_fires()
        {
            // A mechanic firing in the same instant the boss climbs out of the hole gives a player no time to
            // read the opening line, let alone the mechanic.
            var state = NewState();

            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0, 20), "the opening claim arms only");
            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0.AddSeconds(19), 20), "still inside the first window");
            Assert.IsTrue(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0.AddSeconds(20), 20), "the first real firing");
        }

        [TestMethod]
        public void A_cadence_claim_arms_the_next_one_from_NOW_so_a_starved_driver_does_not_fire_a_backlog()
        {
            var state = NewState();

            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0, 20));

            // a 10 minute stall - a landblock hitch, a long reap - and then one tick
            var late = T0.AddSeconds(600);

            Assert.IsTrue(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, late, 20), "one cycle is owed");
            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, late, 20), "and only one");
            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, late.AddSeconds(19), 20));
            Assert.IsTrue(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, late.AddSeconds(20), 20));
        }

        [TestMethod]
        public void The_two_slots_keep_independent_clocks()
        {
            var state = NewState(MlDigsiteMechanic.Drums, MlDigsiteMechanic.Volatile);

            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0, 10));
            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Secondary, T0, 40));

            Assert.IsTrue(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0.AddSeconds(10), 10));
            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Secondary, T0.AddSeconds(10), 40),
                "the main firing must not advance the secondary");

            Assert.IsTrue(state.TryClaimCadence(MlDigsiteMechanicSlot.Secondary, T0.AddSeconds(40), 40));
        }

        [TestMethod]
        public void A_nonsense_cadence_is_floored_at_the_one_second_driver_tick_rather_than_firing_every_tick()
        {
            var state = NewState();

            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0, 0));
            Assert.IsFalse(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0.AddSeconds(0.5), 0), "still inside the 1 s floor");
            Assert.IsTrue(state.TryClaimCadence(MlDigsiteMechanicSlot.Main, T0.AddSeconds(1), 0));
        }

        // ---- the immune phase ----------------------------------------------------------------------------

        [TestMethod]
        public void An_immune_threshold_fires_once_even_if_the_boss_is_healed_back_over_it()
        {
            // The Boss Rush tether heal restores the boss to FULL on a leash, so every threshold would be
            // re-crossed on the way back down. Latching is what stops a kited boss running its 85% phase five
            // times.
            var state = NewState(MlDigsiteMechanic.ImmunePhases);

            Assert.IsTrue(state.TryLatchImmuneThreshold(0.85));
            Assert.IsFalse(state.TryLatchImmuneThreshold(0.85));
            Assert.IsFalse(state.TryLatchImmuneThreshold(0.85));

            Assert.IsTrue(state.TryLatchImmuneThreshold(0.50), "a different threshold has its own latch");
            Assert.IsFalse(state.TryLatchImmuneThreshold(0.5), "0.5 and 0.50 are the same latch");
        }

        [TestMethod]
        public void A_phase_ends_on_the_death_that_empties_its_add_set_and_only_then()
        {
            var state = NewState(MlDigsiteMechanic.ImmunePhases);

            Assert.IsFalse(state.ImmunePhaseActive);

            Assert.IsTrue(state.BeginImmunePhase(new uint[] { 101, 102, 103 }, T0.AddSeconds(45)));
            Assert.IsTrue(state.ImmunePhaseActive);

            Assert.IsTrue(state.NoteImmunePhaseAddDeath(101, out var cleared));
            Assert.IsFalse(cleared);
            Assert.IsTrue(state.ImmunePhaseActive, "two adds still up");

            Assert.IsTrue(state.NoteImmunePhaseAddDeath(102, out cleared));
            Assert.IsFalse(cleared);
            Assert.IsTrue(state.ImmunePhaseActive);

            Assert.IsTrue(state.NoteImmunePhaseAddDeath(103, out cleared));
            Assert.IsTrue(cleared, "the shell cracks exactly once, on the last add");
            Assert.IsFalse(state.ImmunePhaseActive);
        }

        [TestMethod]
        public void A_death_that_is_not_part_of_the_phase_does_not_end_it()
        {
            // Set 3 runs immune phases alongside a drum cadence, and set 5 alongside safe zones; both can have
            // volatile adds from an earlier cycle still on the field. Killing one of those must not crack the
            // shell.
            var state = NewState(MlDigsiteMechanic.ImmunePhases);

            state.BeginImmunePhase(new uint[] { 101, 102 }, T0.AddSeconds(45));

            Assert.IsFalse(state.NoteImmunePhaseAddDeath(999, out var cleared), "not a phase add");
            Assert.IsFalse(cleared);
            Assert.IsTrue(state.ImmunePhaseActive);

            state.NoteImmunePhaseAddDeath(101, out _);

            Assert.IsFalse(state.NoteImmunePhaseAddDeath(101, out cleared), "and a second report of the same add is not a second kill");
            Assert.IsFalse(cleared);
            Assert.IsTrue(state.ImmunePhaseActive);
        }

        [TestMethod]
        public void A_phase_with_no_adds_is_refused_outright()
        {
            // An immunity with nothing to kill is an unbreakable one. If every add failed to spawn, the boss
            // must simply stay killable.
            var state = NewState(MlDigsiteMechanic.ImmunePhases);

            Assert.IsFalse(state.BeginImmunePhase(new uint[0], T0.AddSeconds(45)));
            Assert.IsFalse(state.BeginImmunePhase(null, T0.AddSeconds(45)));
            Assert.IsFalse(state.ImmunePhaseActive);
        }

        [TestMethod]
        public void The_failsafe_closes_a_phase_whose_adds_never_died_and_reports_it_once()
        {
            // One add lost to terrain or to a landblock unload would otherwise hold the boss immortal until the
            // encounter's own TTL.
            var state = NewState(MlDigsiteMechanic.ImmunePhases);

            state.BeginImmunePhase(new uint[] { 101, 102 }, T0.AddSeconds(45));

            Assert.IsFalse(state.TryTimeOutImmunePhase(T0.AddSeconds(44)), "not yet");
            Assert.IsTrue(state.ImmunePhaseActive);

            Assert.IsTrue(state.TryTimeOutImmunePhase(T0.AddSeconds(45)));
            Assert.IsFalse(state.ImmunePhaseActive);
            Assert.IsFalse(state.TryTimeOutImmunePhase(T0.AddSeconds(90)), "and reports the timeout only once");

            // a stale add dying after the timeout is not a second phase clear
            Assert.IsFalse(state.NoteImmunePhaseAddDeath(101, out var cleared));
            Assert.IsFalse(cleared);
        }

        [TestMethod]
        public void TryTimeOutImmunePhase_is_inert_when_no_phase_is_running()
        {
            var state = NewState(MlDigsiteMechanic.ImmunePhases);

            Assert.IsFalse(state.TryTimeOutImmunePhase(T0.AddSeconds(9999)));
        }

        [TestMethod]
        public void A_second_phase_replaces_the_first_ones_add_set_rather_than_merging_with_it()
        {
            var state = NewState(MlDigsiteMechanic.ImmunePhases);

            state.BeginImmunePhase(new uint[] { 101, 102 }, T0.AddSeconds(45));
            state.TryTimeOutImmunePhase(T0.AddSeconds(45));

            Assert.IsTrue(state.BeginImmunePhase(new uint[] { 201 }, T0.AddSeconds(90)));
            Assert.IsTrue(state.NoteImmunePhaseAddDeath(201, out var cleared));
            Assert.IsTrue(cleared, "the new phase is satisfied by its own add alone");
        }

        // ---- the interrupt window ------------------------------------------------------------------------

        [TestMethod]
        public void Used_and_expired_are_mutually_exclusive_because_they_share_one_latch()
        {
            // A player using the drum on the same tick the window runs out must produce exactly one outcome -
            // either the boss skips the hit or everyone takes it, never both.
            var state = NewState(MlDigsiteMechanic.Interrupt);

            Assert.IsTrue(state.TryOpenInterrupt(T0.AddSeconds(12)));
            Assert.IsTrue(state.InterruptOpen);

            Assert.IsTrue(state.TryCloseInterrupt(out _), "the player's use wins the latch");
            Assert.IsFalse(state.InterruptOpen);
            Assert.IsFalse(state.TryCloseInterrupt(out _), "and the expiry finds it already closed");
        }

        [TestMethod]
        public void A_cadence_that_fires_while_a_window_is_open_is_skipped_not_stacked()
        {
            var state = NewState(MlDigsiteMechanic.Interrupt);

            Assert.IsTrue(state.TryOpenInterrupt(T0.AddSeconds(12)));
            Assert.IsFalse(state.TryOpenInterrupt(T0.AddSeconds(24)), "two unavoidable hits queued at once is not a mechanic");

            state.TryCloseInterrupt(out _);

            Assert.IsTrue(state.TryOpenInterrupt(T0.AddSeconds(60)), "and the next cycle opens normally");
        }

        [TestMethod]
        public void InterruptExpired_and_SecondsLeft_track_the_deadline_and_go_quiet_when_it_is_closed()
        {
            var state = NewState(MlDigsiteMechanic.Interrupt);

            Assert.AreEqual(-1, state.InterruptSecondsLeft(T0), "no window open");
            Assert.IsFalse(state.InterruptExpired(T0));

            state.TryOpenInterrupt(T0.AddSeconds(12));

            Assert.AreEqual(12, state.InterruptSecondsLeft(T0));
            Assert.AreEqual(6, state.InterruptSecondsLeft(T0.AddSeconds(6)));
            Assert.IsFalse(state.InterruptExpired(T0.AddSeconds(11.9)));

            Assert.IsTrue(state.InterruptExpired(T0.AddSeconds(12)));
            Assert.AreEqual(0, state.InterruptSecondsLeft(T0.AddSeconds(30)), "never negative");

            state.TryCloseInterrupt(out _);

            Assert.IsFalse(state.InterruptExpired(T0.AddSeconds(30)), "a closed window never expires again");
            Assert.AreEqual(-1, state.InterruptSecondsLeft(T0.AddSeconds(30)));
        }

        [TestMethod]
        public void Each_countdown_reminder_is_sent_once_per_window_not_once_per_tick()
        {
            var state = NewState(MlDigsiteMechanic.Interrupt);

            Assert.IsFalse(state.TryLatchInterruptWarning(6), "no window open, no warning");

            state.TryOpenInterrupt(T0.AddSeconds(12));

            Assert.IsTrue(state.TryLatchInterruptWarning(6));
            Assert.IsFalse(state.TryLatchInterruptWarning(6), "the driver reaches this second on more than one tick");
            Assert.IsTrue(state.TryLatchInterruptWarning(3), "a different mark has its own latch");

            state.TryCloseInterrupt(out _);
            state.TryOpenInterrupt(T0.AddSeconds(60));

            Assert.IsTrue(state.TryLatchInterruptWarning(6), "the next window starts its countdown again");

            // A STARVED TICK JUMPS, and the latch alone cannot save a reminder the driver never offers it.
            // The driver tick is nominally 1 s, but a stalled landblock or a long reap can take a window from
            // 7 s left straight to 5 s with nothing in between; when the mark test was an equality that
            // window simply lost its 6 s line. The two halves are asserted together here because together
            // they are the expression InterruptObjectMechanic.DriveOpenWindow evaluates.
            state.TryCloseInterrupt(out _);
            state.TryOpenInterrupt(T0.AddSeconds(90));

            Assert.IsFalse(MlDigsiteBossMechanicRules.CountdownMarkDue(7, 6), "the 7 s tick offers nothing");
            Assert.IsTrue(MlDigsiteBossMechanicRules.CountdownMarkDue(5, 6), "the 5 s tick after the jump still offers the 6 s mark");
            Assert.IsTrue(state.TryLatchInterruptWarning(6), "so the reminder is sent rather than skipped");
            Assert.IsFalse(state.TryLatchInterruptWarning(6), "and it is still sent exactly once per window");

            Assert.IsFalse(MlDigsiteBossMechanicRules.CountdownMarkDue(5, 3), "the 3 s mark is not yet due at 5 s left");
            Assert.IsTrue(MlDigsiteBossMechanicRules.CountdownMarkDue(2, 3), "and is due once the window is past it");
        }

        [TestMethod]
        public void The_placed_object_is_handed_back_to_whoever_closes_the_window_and_then_forgotten()
        {
            // The object is placed asynchronously on the landblock queue, so the state is where "which drum is
            // this window's drum" lives, and the close is what takes it back out of the world.
            var state = NewState(MlDigsiteMechanic.Interrupt);

            state.TryOpenInterrupt(T0.AddSeconds(12));

            Assert.IsNull(state.InterruptObject, "nothing placed yet");

            state.SetInterruptObject(null);

            Assert.IsTrue(state.TryCloseInterrupt(out var placed));
            Assert.IsNull(placed);
            Assert.IsNull(state.InterruptObject, "cleared with the window");
        }

        [TestMethod]
        public void An_object_arriving_after_the_window_closed_is_not_adopted()
        {
            // The build is asynchronous: a window that was used or expired before the landblock got to the
            // build must not end up holding an object nothing will ever remove through it.
            var state = NewState(MlDigsiteMechanic.Interrupt);

            state.TryOpenInterrupt(T0.AddSeconds(12));
            state.TryCloseInterrupt(out _);

            state.SetInterruptObject(null);

            Assert.IsNull(state.InterruptObject);
            Assert.IsFalse(state.InterruptOpen);
        }

        // ---- safe-zone misses ----------------------------------------------------------------------------

        [TestMethod]
        public void Safe_zone_misses_accrue_per_player_and_the_count_returned_is_the_one_BEFORE_this_miss()
        {
            // MlDigsiteBossMechanicRules.MissIsLethal takes the count before the miss, so the first miss of a
            // fight has to arrive as 0 or the forgiveness window is off by one.
            var state = NewState(MlDigsiteMechanic.SafeZones);

            Assert.AreEqual(0, state.NoteSafeZoneMiss(0x50000001, T0, 60));
            Assert.AreEqual(1, state.NoteSafeZoneMiss(0x50000001, T0.AddSeconds(15), 60));
            Assert.AreEqual(2, state.NoteSafeZoneMiss(0x50000001, T0.AddSeconds(30), 60));

            Assert.AreEqual(0, state.NoteSafeZoneMiss(0x50000002, T0.AddSeconds(30), 60),
                "another player's misses are their own");
        }

        [TestMethod]
        public void A_miss_decays_so_a_long_fight_does_not_accumulate_a_death_sentence()
        {
            var state = NewState(MlDigsiteMechanic.SafeZones);

            Assert.AreEqual(0, state.NoteSafeZoneMiss(0x50000001, T0, 60));
            Assert.AreEqual(1, state.NoteSafeZoneMiss(0x50000001, T0.AddSeconds(10), 60));

            // 2 whole decay windows later, both stacks are gone
            Assert.AreEqual(0, state.NoteSafeZoneMiss(0x50000001, T0.AddSeconds(10 + 120), 60));

            // and a single window removes exactly one stack
            Assert.AreEqual(1, state.NoteSafeZoneMiss(0x50000001, T0.AddSeconds(10 + 121), 60));
            Assert.AreEqual(2, state.NoteSafeZoneMiss(0x50000001, T0.AddSeconds(10 + 122), 60));
            Assert.AreEqual(2, state.NoteSafeZoneMiss(0x50000001, T0.AddSeconds(10 + 122 + 60), 60),
                "three accrued, one decayed away");
        }

        [TestMethod]
        public void A_zero_decay_makes_misses_permanent_for_the_fight()
        {
            var state = NewState(MlDigsiteMechanic.SafeZones);

            state.NoteSafeZoneMiss(0x50000001, T0, 0);

            Assert.AreEqual(1, state.NoteSafeZoneMiss(0x50000001, T0.AddHours(1), 0));
        }

        [TestMethod]
        public void The_miss_ledger_and_the_forgiveness_rule_agree_about_which_miss_kills()
        {
            // The two halves live in different files, so this is the only place the whole forgiveness
            // behaviour - "room for 2 mistakes" - is actually asserted end to end.
            var state = NewState(MlDigsiteMechanic.SafeZones);
            var guid = 0x50000001u;
            var lethal = new List<bool>();

            for (var i = 0; i < 4; i++)
            {
                var before = state.NoteSafeZoneMiss(guid, T0.AddSeconds(i), 60);

                lethal.Add(MlDigsiteBossMechanicRules.MissIsLethal(before, 2));
            }

            CollectionAssert.AreEqual(new[] { false, false, true, true }, lethal.ToArray());
        }

        // ---- markers and marked points --------------------------------------------------------------------

        [TestMethod]
        public void TakeMarkers_hands_the_list_over_once_and_never_returns_null()
        {
            var state = NewState();

            Assert.IsNotNull(state.TakeMarkers());
            Assert.AreEqual(0, state.TakeMarkers().Count);

            state.AddMarker(null);

            Assert.AreEqual(0, state.TakeMarkers().Count, "a marker that failed to build is not adopted");
        }

        [TestMethod]
        public void Marked_points_are_taken_once_and_a_new_cycle_overwrites_an_unresolved_one()
        {
            // The drum volley resolves where the marks were DRAWN, not where the players now are - that
            // distinction is the whole mechanic - so the points have to survive from telegraph to resolve, and
            // a cycle that never reached its resolve must not leak marks into the next one.
            var state = NewState(MlDigsiteMechanic.Drums);

            Assert.IsNotNull(state.TakeMarkedPoints());
            Assert.AreEqual(0, state.TakeMarkedPoints().Count);

            state.SetMarkedPoints(new List<Position> { new Position(), new Position() });

            var taken = state.TakeMarkedPoints();

            Assert.AreEqual(2, taken.Count);
            Assert.AreEqual(0, state.TakeMarkedPoints().Count, "cleared as they are handed out");

            state.SetMarkedPoints(new List<Position> { new Position(), new Position(), new Position() });
            state.SetMarkedPoints(new List<Position> { new Position() });

            Assert.AreEqual(1, state.TakeMarkedPoints().Count, "the second cycle replaced the first");

            state.SetMarkedPoints(new List<Position> { new Position() });
            state.SetMarkedPoints(null);

            Assert.AreEqual(0, state.TakeMarkedPoints().Count);
        }

        // ---- the encounter-side latches the driver depends on ---------------------------------------------

        [TestMethod]
        public void The_tether_heal_fires_once_per_return_trip_and_rearms_when_the_boss_reengages()
        {
            // "The leash heal applies only to Boss Rush bosses" (owner ruling 2026-09-20). The latch is what
            // makes it a heal-on-reset rather than a heal every tick the boss spends walking home.
            var encounter = NewBossRush();

            Assert.IsFalse(encounter.TryClaimTetherHeal(false), "fighting: nothing to claim");

            Assert.IsTrue(encounter.TryClaimTetherHeal(true), "the boss has given up and is walking home");
            Assert.IsFalse(encounter.TryClaimTetherHeal(true), "every later tick of the same walk home");
            Assert.IsFalse(encounter.TryClaimTetherHeal(true));

            Assert.IsFalse(encounter.TryClaimTetherHeal(false), "it arrived and re-engaged");
            Assert.IsTrue(encounter.TryClaimTetherHeal(true), "a second leash heals again");
        }

        [TestMethod]
        public void Driver_state_attaches_exactly_once_so_a_second_roll_cannot_reset_a_running_fight()
        {
            var encounter = NewBossRush();

            Assert.IsNull(encounter.BossMechanics, "no mechanics until one is rolled");

            var state = NewState();

            Assert.IsTrue(encounter.TryAttachBossMechanics(state));
            Assert.AreSame(state, encounter.BossMechanics);

            Assert.IsFalse(encounter.TryAttachBossMechanics(NewState(MlDigsiteMechanic.SafeZones)),
                "a second attach would silently replace a running set's cadences and phase bookkeeping");
            Assert.AreSame(state, encounter.BossMechanics);

            Assert.IsFalse(encounter.TryAttachBossMechanics(null));
        }

        private static MlDigsiteEncounter NewBossRush()
            => new MlDigsiteEncounter(1, 0x50000001, "Digger", MlDigsiteType.BossRush, new Position(), T0);
    }
}
