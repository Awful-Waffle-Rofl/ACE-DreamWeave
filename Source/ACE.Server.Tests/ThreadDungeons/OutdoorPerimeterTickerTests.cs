using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP4b: the ticker that drives OutdoorPerimeterRules for live runs. Fakes stand in for Player
    /// (it cannot be constructed here). The rules themselves are pinned by OutdoorPerimeterRulesTests; these tests pin
    /// input building, the state lifecycle and the dispatch of each verdict.
    /// </summary>
    [TestClass]
    public class OutdoorPerimeterTickerTests
    {
        private const ushort Lb = 0x5456;
        private const double Radius = 60.0;

        private static OutdoorPerimeterSettings Settings(bool lethal = false, double graceS = 10.0)
            => new OutdoorPerimeterSettings(15.0, 40.0, true, 5.0, 5.0, lethal, graceS, true, 2.0, 12.0);

        private static OutdoorCircleDef Circle() => new OutdoorCircleDef { Landblock = Lb, X = 96, Y = 96, Radius = Radius };

        private sealed class FakeSubject : IPerimeterSubject
        {
            public uint Guid { get; set; } = 0x50000001u;
            public OutdoorPos Position { get; set; } = new OutdoorPos(Lb, 96, 96);
            public bool IsTeleporting { get; set; }
            public bool IsDead { get; set; }
            public bool IsLoggingOut { get; set; }
            public double SecondsSinceArrival { get; set; } = 100;
            public double SecondsSinceLastInbound { get; set; } = 0.1;
            public long Health { get; set; } = 1000;
            public long MaxHealth { get; set; } = 1000;
            public bool StillApplicable { get; set; } = true;
            public bool HasQueue { get; set; } = true;

            public List<Action> Queue { get; } = new List<Action>();
            public List<string> Calls { get; } = new List<string>();

            public bool RunOnOwnQueue(Action action)
            {
                if (!HasQueue) return false;
                Queue.Add(action);
                return true;
            }

            public void Drain()
            {
                var pending = Queue.ToList();
                Queue.Clear();
                foreach (var a in pending) a();
            }

            public void WriteDamage(long amount, bool lethal) => Calls.Add($"damage:{amount}:{(lethal ? "mark" : "nomark")}");
            public void SetHealthToOne() => Calls.Add("sethealth1");
            public void TeleportToDrop() => Calls.Add("teleport");
            public void Say(string text) => Calls.Add("say:" + text);

            /// <summary>Distance d east of the centre.</summary>
            public FakeSubject At(double d) { Position = new OutdoorPos(Lb, 96 + d, 96); return this; }
        }

        private static PerimeterRunView View(uint runId, Func<IReadOnlyList<IPerimeterSubject>> subjects, Action<IPerimeterSubject> pulse = null, Func<bool> ring = null)
            => new PerimeterRunView { RunId = runId, Circle = Circle(), Footprint = new ushort[] { Lb }, Subjects = subjects, Pulse = pulse, PlaceRing = ring };

        private static OutdoorPerimeterTicker NewTicker() => new OutdoorPerimeterTicker { CadenceLog = null };

        private static void Pass(OutdoorPerimeterTicker t, PerimeterRunView v, double now, OutdoorPerimeterSettings s = null)
            => t.TickPass(new[] { v }, now, s ?? Settings());

        // ---- input building: one test per field source -------------------------------------------------------

        [TestMethod]
        public void Zone_comes_from_horizontal_distance_to_the_circle_centre()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5); // Outside
            Pass(t, View(1, () => new[] { s }), 100);
            s.Drain();
            CollectionAssert.AreEqual(new[] { "damage:50:nomark", "say:" + OutdoorPerimeterText.FirstDamage }, s.Calls);

            var t2 = NewTicker();
            var inside = new FakeSubject().At(10);
            Pass(t2, View(1, () => new[] { inside }), 100);
            inside.Drain();
            Assert.AreEqual(0, inside.Calls.Count, "inside the circle: nothing happens");
        }

        [TestMethod]
        public void OffFootprintLandblock_IsBackstop()
        {
            var t = NewTicker();
            // Two landblocks east of the footprint: not footprint, not buffer. Close to the centre horizontally is irrelevant.
            var s = new FakeSubject { Position = new OutdoorPos(0x5656, 0, 96) };
            Pass(t, View(1, () => new[] { s }), 100);
            s.Drain();
            CollectionAssert.AreEqual(new[] { "teleport", "say:" + OutdoorPerimeterText.Backstop }, s.Calls);
        }

        [TestMethod]
        public void OffFootprintLandblock_IsBackstop_even_when_horizontally_inside_the_circle()
        {
            // x = -300 on 0x5656 is global 0x54*192 + 84: 12 m from the centre, but two landblocks from the footprint.
            var s = new FakeSubject { Position = new OutdoorPos(0x5656, -300, 96) };
            Pass(NewTicker(), View(1, () => new[] { s }), 100);
            s.Drain();
            CollectionAssert.AreEqual(new[] { "teleport", "say:" + OutdoorPerimeterText.Backstop }, s.Calls);
        }

        [TestMethod]
        public void BufferLandblock_IsNotBackstopByItself()
        {
            var t = NewTicker();
            // Buffer landblock east of the footprint: x = 2 there is 98 m east of the centre, past the circle (60) but inside
            // radius + backstop (100): Outside (damage), not Backstop.
            var s = new FakeSubject { Position = new OutdoorPos(0x5556, 2, 96) };
            Pass(t, View(1, () => new[] { s }), 100);
            s.Drain();
            Assert.AreEqual("damage:50:nomark", s.Calls[0]);
        }

        [TestMethod]
        public void Teleporting_dead_and_logging_out_each_hold_the_tick()
        {
            foreach (var which in new[] { "tele", "dead", "logout" })
            {
                var t = NewTicker();
                var s = new FakeSubject().At(Radius + 5);
                if (which == "tele") s.IsTeleporting = true;
                if (which == "dead") s.IsDead = true;
                if (which == "logout") s.IsLoggingOut = true;
                Pass(t, View(1, () => new[] { s }), 100);
                s.Drain();
                Assert.AreEqual(0, s.Calls.Count, which);
            }
        }

        [TestMethod]
        public void SecondsSinceArrival_inside_the_grace_suppresses_damage()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            s.SecondsSinceArrival = 3;
            Pass(t, View(1, () => new[] { s }), 100);
            s.Drain();
            Assert.AreEqual(0, s.Calls.Count);
        }

        [TestMethod]
        public void SecondsSinceLastInbound_stale_suppresses_damage()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            s.SecondsSinceLastInbound = 5;
            Pass(t, View(1, () => new[] { s }), 100);
            s.Drain();
            Assert.AreEqual(0, s.Calls.Count);
        }

        [TestMethod]
        public void Health_and_max_health_size_the_damage_and_the_snap_back()
        {
            var t = NewTicker();
            var s = new FakeSubject { Health = 3000, MaxHealth = 2000 }.At(Radius + 5);
            Pass(t, View(1, () => new[] { s }), 100);
            s.Drain();
            Assert.AreEqual("damage:100:nomark", s.Calls[0], "5% of MaxHealth 2000");

            var t2 = NewTicker();
            var low = new FakeSubject { Health = 40, MaxHealth = 1000 }.At(Radius + 5);
            Pass(t2, View(1, () => new[] { low }), 100);
            low.Drain();
            CollectionAssert.AreEqual(new[] { "sethealth1", "teleport", "say:" + OutdoorPerimeterText.SnapBack }, low.Calls, "non-lethal: health SET to 1, then drop, then the line");
        }

        [TestMethod]
        public void NowSeconds_drives_the_one_second_rate_limit()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            var v = View(1, () => new[] { s });
            Pass(t, v, 100.0);
            Pass(t, v, 100.4); // inside 0.9 s: no-op
            s.Drain();
            Assert.AreEqual(2, s.Calls.Count, "one damage write and the first-tick line only");
            Pass(t, v, 101.0);
            s.Drain();
            Assert.AreEqual("damage:100:nomark", s.Calls[2], "second tick escalates to 10%");
        }

        // ---- state lifecycle -----------------------------------------------------------------------------------

        [TestMethod]
        public void First_tick_is_not_rate_limited_at_a_small_clock_value()
        {
            // default(PlayerPerimeterState) has LastTickAt = 0, which would swallow a first tick at NowSeconds below 0.9.
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            Pass(t, View(1, () => new[] { s }), 0.5);
            s.Drain();
            Assert.AreEqual("damage:50:nomark", s.Calls[0]);
        }

        [TestMethod]
        public void State_persists_between_ticks_and_the_stack_escalates()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            var v = View(1, () => new[] { s });
            for (var i = 0; i < 3; i++) Pass(t, v, 100 + i);
            s.Drain();
            var damages = s.Calls.Where(c => c.StartsWith("damage:")).ToList();
            CollectionAssert.AreEqual(new[] { "damage:50:nomark", "damage:100:nomark", "damage:150:nomark" }, damages);
        }

        [TestMethod]
        public void The_first_damage_line_is_sent_once_per_excursion()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            var v = View(1, () => new[] { s });
            Pass(t, v, 100); Pass(t, v, 101); Pass(t, v, 102);
            s.Drain();
            Assert.AreEqual(1, s.Calls.Count(c => c == "say:" + OutdoorPerimeterText.FirstDamage));

            // Back inside, then out again: a new excursion, so the line is sent again.
            s.Calls.Clear();
            s.At(10); Pass(t, v, 103); s.Drain();
            s.At(Radius + 5); Pass(t, v, 104); s.Drain();
            Assert.AreEqual(1, s.Calls.Count(c => c == "say:" + OutdoorPerimeterText.FirstDamage));
        }

        [TestMethod]
        public void A_player_who_leaves_is_dropped_and_re_entry_starts_fresh()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            IReadOnlyList<IPerimeterSubject> present = new[] { s };
            var v = View(1, () => present);
            for (var i = 0; i < 3; i++) Pass(t, v, 100 + i);
            s.Drain();
            Assert.AreEqual(1, t.RecordCount);

            present = Array.Empty<IPerimeterSubject>();
            Pass(t, v, 103);
            Assert.AreEqual(0, t.RecordCount, "exit, logout or leaving the instance drops the record");

            present = new[] { s };
            s.Calls.Clear();
            Pass(t, v, 104);
            s.Drain();
            Assert.AreEqual("damage:50:nomark", s.Calls[0], "re-entry resets the Unraveling stack");
        }

        [TestMethod]
        public void A_run_that_is_no_longer_live_loses_its_records_and_its_ring_latch()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            Pass(t, View(1, () => new[] { s }, ring: () => true), 100);
            Assert.AreEqual(1, t.RecordCount);
            Assert.AreEqual(1, t.RingDoneCount);

            t.TickPass(Array.Empty<PerimeterRunView>(), 101, Settings());
            Assert.AreEqual(0, t.RecordCount);
            Assert.AreEqual(0, t.RingDoneCount);
        }

        [TestMethod]
        public void Records_are_per_run_and_per_player()
        {
            var t = NewTicker();
            var a = new FakeSubject { Guid = 1 }.At(Radius + 5);
            var b = new FakeSubject { Guid = 2 }.At(Radius + 5);
            t.TickPass(new[] { View(1, () => new[] { a }), View(2, () => new[] { b }) }, 100, Settings());
            t.TickPass(new[] { View(1, () => new[] { a }) }, 101, Settings());
            Assert.AreEqual(1, t.RecordCount, "run 2 ended: only run 1's record remains");
            a.Drain(); b.Drain();
            Assert.AreEqual("damage:100:nomark", a.Calls.Last(c => c.StartsWith("damage:")));
        }

        // ---- dispatch ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Every_verdict_is_applied_on_the_subjects_own_queue_never_inline()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            Pass(t, View(1, () => new[] { s }), 100);
            Assert.AreEqual(0, s.Calls.Count, "nothing is applied until the player's queue runs it");
            Assert.AreEqual(1, s.Queue.Count);

            var warn = new FakeSubject().At(Radius - 5);
            Pass(NewTicker(), View(1, () => new[] { warn }), 100);
            Assert.AreEqual(0, warn.Calls.Count);
            Assert.AreEqual(1, warn.Queue.Count);

            var back = new FakeSubject { Position = new OutdoorPos(0x5656, 0, 96) };
            Pass(NewTicker(), View(1, () => new[] { back }), 100);
            Assert.AreEqual(0, back.Calls.Count);
            Assert.AreEqual(1, back.Queue.Count);
        }

        [TestMethod]
        public void Warn_sends_the_line_and_pulses_for_that_player_only()
        {
            var t = NewTicker();
            var s = new FakeSubject { Guid = 7 }.At(Radius - 5);
            var other = new FakeSubject { Guid = 8 }.At(10);
            var pulsed = new List<uint>();
            Pass(t, View(1, () => new IPerimeterSubject[] { s, other }, pulse: p => pulsed.Add(p.Guid)), 100);
            s.Drain(); other.Drain();
            CollectionAssert.AreEqual(new[] { "say:" + OutdoorPerimeterText.Warn }, s.Calls);
            CollectionAssert.AreEqual(new uint[] { 7 }, pulsed);
            Assert.AreEqual(0, other.Calls.Count);

            // Once per visit: a second tick in the band does not warn or pulse again.
            Pass(t, View(1, () => new IPerimeterSubject[] { s, other }, pulse: p => pulsed.Add(p.Guid)), 101);
            s.Drain();
            Assert.AreEqual(1, pulsed.Count);
        }

        [TestMethod]
        public void Backstop_teleports_with_no_damage_and_its_own_line()
        {
            var t = NewTicker();
            var s = new FakeSubject { Position = new OutdoorPos(Lb, 96 + Radius + 41, 96) };
            Pass(t, View(1, () => new[] { s }), 100);
            s.Drain();
            CollectionAssert.AreEqual(new[] { "teleport", "say:" + OutdoorPerimeterText.Backstop }, s.Calls);
        }

        [TestMethod]
        public void Lethal_mode_passes_the_death_marker_flag_with_the_full_amount()
        {
            var t = NewTicker();
            var s = new FakeSubject { Health = 40 }.At(Radius + 5);
            Pass(t, View(1, () => new[] { s }), 100, Settings(lethal: true));
            s.Drain();
            Assert.AreEqual("damage:50:mark", s.Calls[0], "lethal: the whole 50 is written (health 40), flagged so a death is marked");
            Assert.IsFalse(s.Calls.Contains("teleport"));
        }

        [TestMethod]
        public void A_verdict_is_dropped_when_the_player_is_no_longer_applicable()
        {
            var t = NewTicker();
            var s = new FakeSubject().At(Radius + 5);
            Pass(t, View(1, () => new[] { s }), 100);
            s.StillApplicable = false; // died, started a teleport or left in the second since the verdict
            s.Drain();
            Assert.AreEqual(0, s.Calls.Count);
        }

        [TestMethod]
        public void A_subject_with_no_queue_is_skipped_without_throwing()
        {
            var t = NewTicker();
            var s = new FakeSubject { HasQueue = false }.At(Radius + 5);
            Pass(t, View(1, () => new[] { s }), 100);
            Assert.AreEqual(0, s.Calls.Count);
        }

        [TestMethod]
        public void A_throwing_subject_does_not_stop_the_pass()
        {
            var t = NewTicker();
            var good = new FakeSubject { Guid = 2 }.At(Radius + 5);
            var view = View(1, () => new[] { good });
            var bad = View(2, () => throw new InvalidOperationException("boom"));
            t.TickPass(new[] { bad, view }, 100, Settings());
            good.Drain();
            Assert.AreEqual("damage:50:nomark", good.Calls[0]);
        }

        [TestMethod]
        public void The_ring_is_retried_until_it_reports_done_then_never_again()
        {
            var t = NewTicker();
            var attempts = 0;
            var v = View(1, () => Array.Empty<IPerimeterSubject>(), ring: () => ++attempts >= 3);
            for (var i = 0; i < 6; i++) Pass(t, v, 100 + i);
            Assert.AreEqual(3, attempts);
        }

        // ---- text and cadence ------------------------------------------------------------------------------------

        [TestMethod]
        public void The_four_lines_are_exactly_the_specified_text()
        {
            Assert.AreEqual("The Thread thins here. Turn back toward its heart.", OutdoorPerimeterText.For(OutdoorPerimeterRules.MsgWarnBand));
            Assert.AreEqual("The Thread unravels around you!", OutdoorPerimeterText.For(OutdoorPerimeterRules.MsgDamage));
            Assert.AreEqual("The Thread pulls you back to where you entered.", OutdoorPerimeterText.For(OutdoorPerimeterRules.MsgSnapBack));
            Assert.AreEqual("You have strayed beyond the Thread. It pulls you back.", OutdoorPerimeterText.For(OutdoorPerimeterRules.MsgBackstop));
            Assert.IsNull(OutdoorPerimeterText.For("nope"));
        }

        [TestMethod]
        public void Cadence_tracker_reports_one_line_per_window_with_the_max_and_stale_count()
        {
            var c = new InboundCadenceTracker();
            Assert.IsNull(c.Observe(1, 0, 0.2));
            Assert.IsNull(c.Observe(1, 10, 3.5));  // stale
            Assert.IsNull(c.Observe(1, 20, 0.4));
            var report = c.Observe(1, 31, 0.1);
            StringAssert.Contains(report.Text, "samples=3");
            StringAssert.Contains(report.Text, "maxSinceInboundS=3.50");
            StringAssert.Contains(report.Text, "staleTicks=1");
            Assert.AreEqual(1, report.StaleTicks);
            Assert.IsNull(c.Observe(1, 32, 0.1), "the window restarted");
        }

        [TestMethod]
        public void The_ticker_forwards_cadence_reports_to_its_log_seam()
        {
            var lines = new List<string>();
            var stale = new List<string>();
            var t = new OutdoorPerimeterTicker { CadenceLog = lines.Add, CadenceStaleLog = stale.Add };
            var s = new FakeSubject().At(10);
            var v = View(1, () => new[] { s });
            for (var i = 0; i <= 31; i++) Pass(t, v, 100 + i);
            Assert.AreEqual(1, lines.Count, "a clean window goes to the debug seam");
            Assert.AreEqual(0, stale.Count);
            StringAssert.Contains(lines[0], "[OWT] inbound cadence");
        }

        [TestMethod]
        public void A_cadence_window_with_stale_ticks_goes_to_the_info_seam_only()
        {
            var lines = new List<string>();
            var stale = new List<string>();
            var t = new OutdoorPerimeterTicker { CadenceLog = lines.Add, CadenceStaleLog = stale.Add };
            var s = new FakeSubject().At(10);
            var v = View(1, () => new[] { s });
            for (var i = 0; i <= 31; i++)
            {
                s.SecondsSinceLastInbound = i == 5 ? 4.0 : 0.1;
                Pass(t, v, 100 + i);
            }

            Assert.AreEqual(0, lines.Count);
            Assert.AreEqual(1, stale.Count);
            StringAssert.Contains(stale[0], "staleTicks=1");
        }

        [TestMethod]
        public void Band_edge_oscillation_warns_once_per_cooldown_then_again()
        {
            var t = NewTicker();
            var s = new FakeSubject { Guid = 7 };
            var pulsed = new List<double>();
            double now = 100;
            var v = View(1, () => new[] { s }, pulse: p => pulsed.Add(now));
            // Out of the band and back in every other second for 8 s: the rules re-arm the warning each visit.
            for (var i = 0; i < 9; i++, now++)
            {
                s.At(i % 2 == 0 ? Radius - 5 : 10);
                Pass(t, v, now);
                s.Drain();
            }

            Assert.AreEqual(1, pulsed.Count, "one warn and pulse inside the 10 s cooldown");
            Assert.AreEqual(1, s.Calls.Count(c => c == "say:" + OutdoorPerimeterText.Warn));

            // Past the cooldown the next visit warns again.
            now = 111; s.At(Radius - 5); Pass(t, v, now); s.Drain();
            now = 112; s.At(10); Pass(t, v, now); s.Drain();
            now = 113; s.At(Radius - 5); Pass(t, v, now); s.Drain();
            Assert.AreEqual(2, pulsed.Count);
        }
    }
}
