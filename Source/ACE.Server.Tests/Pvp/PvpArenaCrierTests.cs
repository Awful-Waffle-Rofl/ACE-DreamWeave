using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvpArenaCrier's decision logic (Docs/Pvp/DESIGN.md "Arena Crier"): pure, driven with a fake clock and
    /// hand-built CrierQueueSnapshot rows, so no live player, chat session or PvpMatchCoordinator is needed.
    /// Every test's summary names the behaviour it would catch a regression in.
    /// </summary>
    [TestClass]
    public class PvpArenaCrierTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

        // announceOnJoin is pinned FALSE by default so every pre-existing test keeps exercising the interval timer path;
        // the per-join tests below opt in explicitly.
        private static PvpArenaDials Dials(bool enabled = true, int interval = 900, int delay = 15, int cooldown = 120, bool announceOnJoin = false) =>
            PvpTunables.Defaults with
            {
                CrierEnabled = enabled,
                CrierAnnounceOnJoin = announceOnJoin,
                CrierIntervalSeconds = interval,
                CrierLastCallDelaySeconds = delay,
                CrierLastCallCooldownSeconds = cooldown,
            };

        private static CrierQueueSnapshot Snap(string mode, bool enabled, int count, int needed) =>
            new CrierQueueSnapshot(mode, enabled, count, needed);

        // The close time identifies the fill episode: the default is one fixed close time while a window is open.
        private static readonly DateTime FixedClose = T0.AddSeconds(3600);

        private static CrierQueueSnapshot BgSnap(int count, int needed, int? secondsLeft, int room, DateTime? close = null, bool formed = false) =>
            new CrierQueueSnapshot(ACE.Server.Pvp.Battlegrounds.BattlegroundModes.RoomKey, true, count, needed, secondsLeft, room,
                secondsLeft.HasValue ? close ?? FixedClose : null, formed);

        // ================= periodic =================

        [TestMethod]
        public void Periodic_FiresAtTheInterval_OneLinePerNonEmptyQueue()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(interval: 900);

            // Needed is deliberately never exactly 1 here, so this test cannot be confused by the last-call arm
            // (a fresh queue's first joiner at Needed == 1 arms independently of the periodic timer - see the
            // LastCall_FreshQueue_FirstJoinerArms test for that behaviour on its own).
            var queues = new[]
            {
                Snap(ArenaMapCatalog.OneVOneKey, true, 1, 2),
                Snap(ArenaMapCatalog.TwoVTwoKey, true, 2, 2),
                Snap(ArenaMapCatalog.FfaKey, true, 0, 5),
            };

            // The first Tick call establishes the periodic baseline (now + interval); it never itself fires.
            var baseline = crier.Tick(T0, dials, queues);
            Assert.AreEqual(0, baseline.Count, "the call that establishes the baseline must not itself fire");

            // Before the interval elapses: nothing.
            var early = crier.Tick(T0.AddSeconds(1), dials, queues);
            Assert.AreEqual(0, early.Count, "must not fire before the interval elapses (removing the timer gate would fire every tick)");

            var due = crier.Tick(T0.AddSeconds(900), dials, queues);

            Assert.AreEqual(2, due.Count, "one line per non-empty queue; the empty Tugak Brawl queue must be skipped");
            Assert.IsTrue(due.Any(l => l.Contains("1v1 arena queue: 1 queued, 2 more needed") && l.Contains("/arena join 1v1")));
            Assert.IsTrue(due.Any(l => l.Contains("2v2 arena queue: 2 queued, 2 more needed") && l.Contains("/arena join 2v2")));
        }

        [TestMethod]
        public void Periodic_SkipsQueueWithNeededAtOrBelowZero()
        {
            // The matchmaker is deliberately holding a formed-size queue back (rating window, same-IP block).
            var crier = new PvpArenaCrier();
            var dials = Dials();

            var queues = new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 2, 0) };

            crier.Tick(T0, dials, queues);
            var lines = crier.Tick(T0.AddSeconds(dials.CrierIntervalSeconds), dials, queues);

            Assert.AreEqual(0, lines.Count, "removing this guard would announce '0 more needed'");
        }

        [TestMethod]
        public void Periodic_SkipsDisabledMode()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials();

            var queues = new[] { Snap(ArenaMapCatalog.OneVOneKey, false, 3, 1) };

            crier.Tick(T0, dials, queues);
            var lines = crier.Tick(T0.AddSeconds(dials.CrierIntervalSeconds), dials, queues);

            Assert.AreEqual(0, lines.Count, "removing the enabled check would announce a closed queue");
        }

        [TestMethod]
        public void Periodic_TwoVTwo_DuoCountsAsTwoQueuedPlayers()
        {
            // The coordinator folds a premade duo into Count already; the crier must not re-derive it and must
            // report the raw player count it was handed.
            var crier = new PvpArenaCrier();
            var dials = Dials();

            var queues = new[] { Snap(ArenaMapCatalog.TwoVTwoKey, true, 2, 2) };

            crier.Tick(T0, dials, queues);
            var lines = crier.Tick(T0.AddSeconds(dials.CrierIntervalSeconds), dials, queues);

            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "2v2 arena queue: 2 queued, 2 more needed");
        }

        [TestMethod]
        public void Periodic_FfaNeeded_MatchesTheSharedHelper()
        {
            // Pins that the FFA line the crier would compose agrees with ArenaMapCatalog.FfaDecayedTargetSize -
            // the same formula the coordinator feeds it and the FFA lobby line uses.
            var crier = new PvpArenaCrier();
            var dials = Dials();
            var waited = TimeSpan.FromSeconds(125); // two decay steps at the 60 s default
            var expectedTarget = ArenaMapCatalog.FfaDecayedTargetSize(waited, dials);
            var count = 3;
            var expectedNeeded = Math.Max(0, expectedTarget - count);

            var queues = new[] { Snap(ArenaMapCatalog.FfaKey, true, count, expectedNeeded) };

            crier.Tick(T0, dials, queues);
            var lines = crier.Tick(T0.AddSeconds(dials.CrierIntervalSeconds), dials, queues);

            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], $"Tugak Brawl arena queue: {count} queued, {expectedNeeded} more needed");
            StringAssert.Contains(lines[0], "/arena join tugak");
        }

        [TestMethod]
        public void Disabled_SendsNothingEvenAtTheInterval()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(enabled: false);

            var queues = new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) };

            var lines = crier.Tick(T0.AddSeconds(10_000), dials, queues);

            Assert.AreEqual(0, lines.Count, "removing the master switch check would announce while pvp_arena_crier_enabled is false");
        }

        // ================= last call =================

        [TestMethod]
        public void LastCall_FiresAfterTheDelay_WhenStillAtOne()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(delay: 15);

            // t0: empty. t1: one player joins - the arming transition (increase, needed == 1).
            crier.Tick(T0, dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 0, 2) });
            var armTick = crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            Assert.AreEqual(0, armTick.Count, "arming must not itself announce anything");

            // Before the delay: nothing yet.
            var early = crier.Tick(T0.AddSeconds(1 + 14), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            Assert.AreEqual(0, early.Count, "must not fire before the delay elapses");

            // At the delay, still needing exactly 1: fires.
            var due = crier.Tick(T0.AddSeconds(1 + 15), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });

            Assert.AreEqual(1, due.Count);
            StringAssert.Contains(due[0], "1v1 arena: 1 more player needed to start a match!");
            StringAssert.Contains(due[0], "/arena join 1v1 now");
        }

        [TestMethod]
        public void LastCall_DoesNotFire_WhenTheQueueFormedBeforeTheDelay()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(delay: 15);

            crier.Tick(T0, dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 0, 2) });
            crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) }); // arms

            // A match formed before the delay: the room is empty again and needs a fresh 2.
            var due = crier.Tick(T0.AddSeconds(1 + 15), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 0, 2) });

            Assert.AreEqual(0, due.Count, "checking the deadline snapshot, not the arm-time snapshot, must matter here");
        }

        [TestMethod]
        public void LastCall_DoesNotFire_WhenTheQueueEmptiedBeforeTheDelay()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(delay: 15);

            crier.Tick(T0, dials, new[] { Snap(ArenaMapCatalog.TwoVTwoKey, true, 2, 2) });
            crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(ArenaMapCatalog.TwoVTwoKey, true, 3, 1) }); // arms

            // The lone extra player left before the delay elapsed.
            var due = crier.Tick(T0.AddSeconds(1 + 15), dials, new[] { Snap(ArenaMapCatalog.TwoVTwoKey, true, 0, 4) });

            Assert.AreEqual(0, due.Count);
        }

        [TestMethod]
        public void LastCall_DoesNotArm_OnAShrinkToOne_FfaLeave()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(delay: 15);

            // FFA starts at a lobby of 2 (needed computed to 1 already is impossible here without a leave, so
            // model it directly): 2 queued, then one leaves, leaving 1 queued and needed recomputed to 1.
            crier.Tick(T0, dials, new[] { Snap(ArenaMapCatalog.FfaKey, true, 2, 0) });
            var afterLeave = crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(ArenaMapCatalog.FfaKey, true, 1, 1) });
            Assert.AreEqual(0, afterLeave.Count);

            // At what would have been the delay deadline had it armed, still needing 1: no call, because a
            // DECREASE must never arm one.
            var due = crier.Tick(T0.AddSeconds(1 + 15), dials, new[] { Snap(ArenaMapCatalog.FfaKey, true, 1, 1) });

            Assert.AreEqual(0, due.Count, "a shrink to 1 (a leave) must never arm a last call");
        }

        [TestMethod]
        public void LastCall_FreshQueue_FirstJoinerArms()
        {
            // 1v1's own note: a lone queued player is "1 needed", so a FRESH queue's first joiner (0 -> 1, a
            // count increase from the crier's initial state) triggers a last call unless matched. Intended.
            var crier = new PvpArenaCrier();
            var dials = Dials(delay: 15);

            var armTick = crier.Tick(T0, dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            Assert.AreEqual(0, armTick.Count);

            var due = crier.Tick(T0.AddSeconds(15), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });

            Assert.AreEqual(1, due.Count, "a fresh 1v1 queue's first joiner must arm from the crier's initial (0) baseline");
        }

        [TestMethod]
        public void LastCall_RespectsTheCooldown_AfterFiring()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(delay: 15, cooldown: 120);

            // First cycle: fires normally.
            crier.Tick(T0, dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            var firstFire = crier.Tick(T0.AddSeconds(15), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            Assert.AreEqual(1, firstFire.Count, "sanity: the first last call must fire");

            // The match formed and emptied the queue, then someone rejoined ALONE (count 0 -> 1, needed -> 1)
            // well within the cooldown window.
            crier.Tick(T0.AddSeconds(16), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 0, 2) });
            crier.Tick(T0.AddSeconds(17), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) }); // would arm, but cooldown

            var wouldHaveFiredAt = crier.Tick(T0.AddSeconds(17 + 15), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });

            Assert.AreEqual(0, wouldHaveFiredAt.Count, "the cooldown after the first fire must block re-arming this soon");

            // Past the cooldown (120 s after the first fire, at T0+15), a fresh join can arm and fire again.
            crier.Tick(T0.AddSeconds(200), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 0, 2) });
            crier.Tick(T0.AddSeconds(201), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            var secondFire = crier.Tick(T0.AddSeconds(201 + 15), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });

            Assert.AreEqual(1, secondFire.Count, "once the cooldown has elapsed, a new transition must be able to arm and fire");
        }

        [TestMethod]
        public void ToggleBurst_ReenablingAfterADueTime_SendsNothingOnTheFirstTick_AndNeedsAFreshInterval()
        {
            // A stale nextPeriodicUtc or an armed last call from before the toggle must never fire the moment the
            // mode comes back, however long it sat disabled.
            var crier = new PvpArenaCrier();
            var dials = Dials(interval: 900, delay: 15);
            var disabledDials = dials with { CrierEnabled = false };

            // Prime the timer, then arm a last call (1v1 needs exactly 1) just before disabling.
            crier.Tick(T0, dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });

            // Disable while an arm and the periodic timer are both live, and skip well past both due times.
            crier.Tick(T0.AddSeconds(1), disabledDials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            var whileDisabled = crier.Tick(T0.AddSeconds(10_000), disabledDials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            Assert.AreEqual(0, whileDisabled.Count, "sanity: nothing sent while disabled");

            // Re-enable, still with the queue needing exactly 1 (the state a stale arm would fire on): the very
            // first post-enable tick must send nothing.
            var firstAfterEnable = crier.Tick(T0.AddSeconds(10_001), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            Assert.AreEqual(0, firstAfterEnable.Count, "re-enabling must not fire a stale periodic line or a stale armed last call");

            // A full fresh interval must be needed from the re-enable tick, not from whenever the old timer was due.
            var justUnderAFreshInterval = crier.Tick(T0.AddSeconds(10_001 + 899), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            Assert.AreEqual(0, justUnderAFreshInterval.Count, "removing the re-baseline would let the old due-time (already long past) fire immediately");

            var dueAFreshIntervalLater = crier.Tick(T0.AddSeconds(10_001 + 900), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            Assert.IsTrue(dueAFreshIntervalLater.Count > 0, "a full fresh interval after re-enabling must fire the periodic line");
        }

        [TestMethod]
        public void LastCall_SendsNothing_WhenDisabled()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(enabled: false, delay: 15);

            crier.Tick(T0, dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });
            var due = crier.Tick(T0.AddSeconds(15), dials, new[] { Snap(ArenaMapCatalog.OneVOneKey, true, 1, 1) });

            Assert.AreEqual(0, due.Count);
        }

        // ================= per-join announcements =================

        private const string OneV = ArenaMapCatalog.OneVOneKey;
        private const string TwoV = ArenaMapCatalog.TwoVTwoKey;

        [TestMethod]
        public void PerJoin_Increase_FiresTheStatusLine_AndTheTimerFiresNothing()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true, interval: 900);

            crier.Tick(T0, dials, new[] { Snap(TwoV, true, 0, 4) });

            var joined = crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(TwoV, true, 1, 3) });
            Assert.AreEqual(1, joined.Count, "a count increase with Needed > 0 must announce");
            StringAssert.Contains(joined[0], "2v2 arena queue: 1 queued, 3 more needed");

            // The interval timer is idle in this mode: well past the interval with an unchanged count, nothing.
            var timerTick = crier.Tick(T0.AddSeconds(5000), dials, new[] { Snap(TwoV, true, 1, 3) });
            Assert.AreEqual(0, timerTick.Count, "per-join mode must not also fire on the interval timer");
        }

        [TestMethod]
        public void PerJoin_DecreaseAndUnchanged_FireNothing()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true);

            crier.Tick(T0, dials, new[] { Snap(TwoV, true, 2, 2) });

            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(TwoV, true, 2, 2) }).Count, "unchanged count must not announce");
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(2), dials, new[] { Snap(TwoV, true, 1, 3) }).Count, "a decrease must not announce");
        }

        [TestMethod]
        public void PerJoin_NeededZero_AndDisabledMode_FireNothing()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true);

            crier.Tick(T0, dials, new[] { Snap(OneV, true, 0, 2), Snap(TwoV, false, 0, 4) });

            var lines = crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(OneV, true, 2, 0), Snap(TwoV, false, 3, 1) });

            Assert.AreEqual(0, lines.Count, "Needed <= 0 (held back) and a disabled mode must never announce a join");
        }

        [TestMethod]
        public void PerJoin_EachJoinAnnouncesOnce()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true);

            crier.Tick(T0, dials, new[] { Snap(TwoV, true, 0, 4) });

            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(TwoV, true, 1, 3) }).Count);
            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(2), dials, new[] { Snap(TwoV, true, 2, 2) }).Count);
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(3), dials, new[] { Snap(TwoV, true, 2, 2) }).Count, "PreviousCount must be updated so a held count does not re-announce");
        }

        [TestMethod]
        public void PerJoin_ReenableTick_EmitsNothing_EvenWithAnIncreaseVersusTheStaleBaseline()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true);
            var disabled = dials with { CrierEnabled = false };

            crier.Tick(T0, dials, new[] { Snap(TwoV, true, 0, 4) });
            crier.Tick(T0.AddSeconds(1), disabled, new[] { Snap(TwoV, true, 0, 4) });

            // players queued while the Crier was off: versus the stale baseline of 0 that is an increase.
            var reenable = crier.Tick(T0.AddSeconds(2), dials, new[] { Snap(TwoV, true, 1, 3) });
            Assert.AreEqual(0, reenable.Count, "the re-enable tick must re-baseline, not announce a join");

            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(3), dials, new[] { Snap(TwoV, true, 2, 2) }).Count, "a genuine join after re-enable announces again");
        }

        [TestMethod]
        public void Toggle_IntervalToPerJoin_EmitsNoBurst_AndPerJoinTakesOver()
        {
            var crier = new PvpArenaCrier();
            var interval = Dials(announceOnJoin: false, interval: 900);
            var perJoin = interval with { CrierAnnounceOnJoin = true };

            crier.Tick(T0, interval, new[] { Snap(TwoV, true, 2, 2) });

            // Switch mid-interval, queue unchanged: nothing extra fires.
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(10), perJoin, new[] { Snap(TwoV, true, 2, 2) }).Count, "false -> true must not fire anything on the switch tick");

            // Even past the old timer due time, per-join mode stays silent without a join.
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(2000), perJoin, new[] { Snap(TwoV, true, 2, 2) }).Count);
        }

        [TestMethod]
        public void Toggle_PerJoinToInterval_StartsAFreshFullInterval_NoImmediateLine()
        {
            var crier = new PvpArenaCrier();
            var perJoin = Dials(announceOnJoin: true, interval: 900);
            var interval = perJoin with { CrierAnnounceOnJoin = false };

            // Start in interval mode so a timer exists, then run per-join long enough that it would be stale.
            crier.Tick(T0, interval, new[] { Snap(TwoV, true, 2, 2) });
            crier.Tick(T0.AddSeconds(10), perJoin, new[] { Snap(TwoV, true, 2, 2) });
            crier.Tick(T0.AddSeconds(5000), perJoin, new[] { Snap(TwoV, true, 2, 2) });

            // Switch long after T0: a timer left over from T0 would be due. It must instead start fresh.
            var switchTick = crier.Tick(T0.AddSeconds(6000), interval, new[] { Snap(TwoV, true, 2, 2) });
            Assert.AreEqual(0, switchTick.Count, "true -> false must not fire an immediate periodic line");

            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(6000 + 899), interval, new[] { Snap(TwoV, true, 2, 2) }).Count, "fresh interval not yet elapsed");
            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(6000 + 900), interval, new[] { Snap(TwoV, true, 2, 2) }).Count, "a full fresh interval later the timer fires");
        }

        [TestMethod]
        public void IntervalMode_IgnoresJoins_AndLastCallStillArmsInBothModes()
        {
            var interval = Dials(announceOnJoin: false, delay: 15);
            var perJoin = Dials(announceOnJoin: true, delay: 15);

            var a = new PvpArenaCrier();
            a.Tick(T0, interval, new[] { Snap(TwoV, true, 0, 4) });
            Assert.AreEqual(0, a.Tick(T0.AddSeconds(1), interval, new[] { Snap(TwoV, true, 1, 3) }).Count, "interval mode must not announce a join");

            foreach (var d in new[] { interval, perJoin })
            {
                var crier = new PvpArenaCrier();
                crier.Tick(T0, d, new[] { Snap(OneV, true, 0, 2) });
                crier.Tick(T0.AddSeconds(1), d, new[] { Snap(OneV, true, 1, 1) });
                var due = crier.Tick(T0.AddSeconds(16), d, new[] { Snap(OneV, true, 1, 1) });

                Assert.IsTrue(due.Any(l => l.Contains("1 more player needed")), "last call is unchanged in both modes");
            }
        }

        // ================= battleground filling =================

        [TestMethod]
        public void BgFilling_IntervalMode_FiresOnceOnEnteringTheWindow()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: false);

            crier.Tick(T0, dials, new[] { BgSnap(5, 1, null, 0) });

            var entered = crier.Tick(T0.AddSeconds(1), dials, new[] { BgSnap(6, 0, 60, 6) });
            Assert.AreEqual(1, entered.Count);
            Assert.AreEqual("Battleground starting in about 60 seconds with 6 players - room for 6 more! Type /arena join bg to tag along.", entered[0]);

            // A further join in interval mode, and a held tick: no repeat.
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(2), dials, new[] { BgSnap(7, 0, 59, 5) }).Count, "interval mode announces only the transition");
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(3), dials, new[] { BgSnap(7, 0, 58, 5) }).Count);
        }

        [TestMethod]
        public void BgFilling_PerJoinMode_RefiresOnEveryJoinWithUpdatedNumbers()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true);

            crier.Tick(T0, dials, new[] { BgSnap(5, 1, null, 0) });

            var entered = crier.Tick(T0.AddSeconds(1), dials, new[] { BgSnap(6, 0, 60, 6) });
            Assert.AreEqual(1, entered.Count, "entering the window announces exactly one filling line (Needed is 0, so no status line)");

            var joined = crier.Tick(T0.AddSeconds(2), dials, new[] { BgSnap(7, 0, 59, 5) });
            Assert.AreEqual(1, joined.Count);
            Assert.AreEqual("Battleground starting in about 59 seconds with 7 players - room for 5 more! Type /arena join bg to tag along.", joined[0]);

            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(3), dials, new[] { BgSnap(7, 0, 58, 5) }).Count, "no join, no line");
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(4), dials, new[] { BgSnap(6, 0, 57, 6) }).Count, "a leave announces nothing");
        }

        [TestMethod]
        public void BgFilling_NotAnnounced_WhenSecondsOrRoomAreNotPositive()
        {
            var dials = Dials(announceOnJoin: true);

            var closed = new PvpArenaCrier();
            closed.Tick(T0, dials, new[] { BgSnap(5, 1, null, 0) });
            Assert.AreEqual(0, closed.Tick(T0.AddSeconds(1), dials, new[] { BgSnap(6, 0, 0, 6) }).Count, "seconds <= 0 must not announce");

            var full = new PvpArenaCrier();
            full.Tick(T0, dials, new[] { BgSnap(5, 1, null, 0) });
            Assert.AreEqual(0, full.Tick(T0.AddSeconds(1), dials, new[] { BgSnap(6, 0, 30, 0) }).Count, "room <= 0 must not announce");
        }

        [TestMethod]
        public void BgFilling_CanFireAgain_AfterTheQueueDropsBelowMinPlayers_ButNotWhileTheEpisodeContinues()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: false);

            crier.Tick(T0, dials, new[] { BgSnap(5, 1, null, 0) });
            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(1), dials, new[] { BgSnap(6, 0, 60, 6) }).Count, "first episode fires");
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(2), dials, new[] { BgSnap(6, 0, 59, 6) }).Count, "same episode does not re-fire");

            // Drops below MinPlayers (null FillSecondsLeft) then refills: a new episode.
            crier.Tick(T0.AddSeconds(3), dials, new[] { BgSnap(5, 1, null, 0) });
            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(4), dials, new[] { BgSnap(6, 0, 60, 6) }).Count, "a fresh episode after dropping below MinPlayers must fire again");
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void BgFilling_FiresAgain_WhenAMatchFormsAndLeavesMinPlayersQueued_BecauseTheCloseTimeChanged(bool announceOnJoin)
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: announceOnJoin);

            crier.Tick(T0, dials, new[] { BgSnap(5, 1, null, 0) });
            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(1), dials, new[] { BgSnap(10, 0, 60, 2) }).Count, "first episode fires");

            // A match formed (the coordinator sets MatchFormedSinceLastTick) and left 6 (>= MinPlayers) queued: the window
            // never went null, but its t4 moved, so the close time differs. The count FELL, so only the formed flag explains it.
            var again = crier.Tick(T0.AddSeconds(2), dials, new[] { BgSnap(6, 0, 45, 6, T0.AddSeconds(3700), formed: true) });
            Assert.AreEqual(1, again.Count, "a changed close time is a new episode and announces once, in both modes");
            StringAssert.Contains(again[0], "with 6 players - room for 6 more!");

            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(3), dials, new[] { BgSnap(6, 0, 44, 6, T0.AddSeconds(3700)) }).Count, "the same new close time does not repeat");
        }

        [TestMethod]
        public void PerJoin_TheJoinThatArmsALastCall_AlsoAnnouncesItsStatusLine_AndACooldownBlockedArmAnnouncesToo()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true, delay: 15, cooldown: 120);

            crier.Tick(T0, dials, new[] { Snap(OneV, true, 0, 2) });
            var arming = crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(OneV, true, 1, 1) });
            Assert.AreEqual(1, arming.Count, "the join that arms a last call must also send the status line");
            StringAssert.Contains(arming[0], "1v1 arena queue: 1 queued, 1 more needed");

            var due = crier.Tick(T0.AddSeconds(16), dials, new[] { Snap(OneV, true, 1, 1) });
            Assert.AreEqual(1, due.Count);
            StringAssert.Contains(due[0], "1 more player needed");

            // Inside the cooldown a new Needed == 1 join cannot arm, so no last call is coming: the status line goes out.
            crier.Tick(T0.AddSeconds(17), dials, new[] { Snap(OneV, true, 0, 2) });
            var blocked = crier.Tick(T0.AddSeconds(18), dials, new[] { Snap(OneV, true, 1, 1) });
            Assert.AreEqual(1, blocked.Count, "a join that does NOT arm (cooldown) still announces");
            StringAssert.Contains(blocked[0], "1v1 arena queue: 1 queued, 1 more needed");
        }

        [TestMethod]
        public void PerJoin_TwoVTwo_FourthPlayerJoiningBeforeTheLastCallDeadline_StillHearsThreeQueuedOneMoreNeeded()
        {
            // The reported sequence: joins at counts 1, 2, 3 (this one arms the last call), then a 4th before the 15 s deadline.
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true, delay: 15, cooldown: 120);
            var two = ArenaMapCatalog.TwoVTwoKey;

            crier.Tick(T0, dials, new[] { Snap(two, true, 0, 4) });
            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(1), dials, new[] { Snap(two, true, 1, 3) }).Count);
            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(2), dials, new[] { Snap(two, true, 2, 2) }).Count);

            var third = crier.Tick(T0.AddSeconds(3), dials, new[] { Snap(two, true, 3, 1) });
            Assert.AreEqual(1, third.Count, "the join that brings the queue to 1 more needed must be announced");
            StringAssert.Contains(third[0], "2v2 arena queue: 3 queued, 1 more needed");

            // The fourth player arrives before the deadline: the queue fills (Needed 0), nothing further to say, no stale last call.
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(8), dials, new[] { Snap(two, true, 4, 0) }).Count);
            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(30), dials, new[] { Snap(two, true, 0, 4) }).Count);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void BgFilling_AnEarlierQueuerLeaving_MovesTheCloseTime_ButAnnouncesNothing(bool announceOnJoin)
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: announceOnJoin);

            crier.Tick(T0, dials, new[] { BgSnap(5, 1, null, 0) });
            Assert.AreEqual(1, crier.Tick(T0.AddSeconds(1), dials, new[] { BgSnap(6, 0, 60, 6) }).Count, "first episode fires");

            // The earliest queuer left: count stays at or above Min, the close time moves later, no match formed.
            var left = crier.Tick(T0.AddSeconds(2), dials, new[] { BgSnap(6, 0, 70, 6, T0.AddSeconds(3800)) });
            Assert.AreEqual(0, left.Count, "a departure must not announce a new fill window");

            // A real join afterwards is judged against the moved close time, with no repeat of the episode line in interval mode.
            var joined = crier.Tick(T0.AddSeconds(3), dials, new[] { BgSnap(7, 0, 69, 5, T0.AddSeconds(3800)) });
            Assert.AreEqual(announceOnJoin ? 1 : 0, joined.Count);
        }
        [TestMethod]
        public void BgFilling_ReenableTick_DoesNotAnnounceAnExistingWindow()
        {
            var crier = new PvpArenaCrier();
            var dials = Dials(announceOnJoin: true);
            var disabled = dials with { CrierEnabled = false };

            crier.Tick(T0, dials, new[] { BgSnap(5, 1, null, 0) });
            crier.Tick(T0.AddSeconds(1), disabled, new[] { BgSnap(5, 1, null, 0) });

            Assert.AreEqual(0, crier.Tick(T0.AddSeconds(2), dials, new[] { BgSnap(7, 0, 40, 5) }).Count, "re-enabling into an open window must not burst");
        }
    }
}
