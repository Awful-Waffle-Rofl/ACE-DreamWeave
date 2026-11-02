using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>Pure tests of the memory game: settings, sequence growth, the state machine, the touch edge trigger and the shared room.</summary>
    [TestClass]
    public class MemoryRulesTests
    {
        // pads at four well-separated corners; radius 1
        private static readonly Vector2[] Pads =
        {
            new Vector2(10, 10), new Vector2(30, 10), new Vector2(30, 30), new Vector2(10, 30),
        };

        private const float PadRadius = 1f;
        private static readonly Vector2 Away = new Vector2(20, 20);

        private static MemoryGame Game(int[] sequence, int maxLen, double on = 1.0, double off = 0.5, double pause = 1.5, double timeout = 20)
        {
            var game = new MemoryGame(sequence, maxLen, on, off, pause, timeout);
            game.BeginRound(0); // a fresh game is Done until its first round begins
            return game;
        }

        private static IReadOnlyList<(uint Id, Vector2 Position)> At(params (uint Id, Vector2 Position)[] players) => players;

        private static MemorySession Session(MemoryGame game, int seed = 1) => new MemorySession(game, Pads, PadRadius, new Random(seed));

        /// <summary>Runs the clock forward until playback is over and the answer window is open; returns the time it opened.</summary>
        private static double FinishPlayback(MemorySession s, uint player, double from)
        {
            var now = from;

            while (s.Game.Phase == MemoryPhase.Playback)
            {
                now += 0.1;
                s.Step(now, At((player, Away)));
                Assert.IsTrue(now < from + 200, "playback never finished");
            }

            return now;
        }

        /// <summary>Walk onto pad p and back off, one tick each, returning the events of the step onto the pad.</summary>
        private static List<MemoryEvent> Press(MemorySession s, uint player, int pad, ref double now)
        {
            now += 0.1;
            var onto = s.Step(now, At((player, Pads[pad])));
            now += 0.1;
            s.Step(now, At((player, Away)));
            return onto;
        }

        // ---- settings ------------------------------------------------------------------------------------------

        [TestMethod]
        public void ShippedDefaultsString_ParsesToTheDocumentedDefaults_AndHasNoDeadKeys()
        {
            var args = HazardArgs.Parse(HazardDefaults.Memory);
            var s = MemorySettings.Read(args);

            Assert.AreEqual(MemorySettings.DefaultLen, s.Len);
            Assert.AreEqual(MemorySettings.DefaultMaxLen, s.MaxLen);
            Assert.AreEqual(MemorySettings.DefaultBeamsPerPulse, s.BeamsPerPulse);
            Assert.AreEqual(6, s.BeamsPerPulse, "twice the old 3");
            Assert.IsTrue(s.BeamsFromCeiling, "beams hang from the ceiling by default");
            Assert.IsFalse(s.BeamFlip, "the swap is off by default");
            Assert.IsFalse(MemoryLayout.BeamTurned(true, false), "a ceiling host is unturned");
            Assert.IsTrue(MemoryLayout.BeamTurned(false, false), "a floor host is turned");
            Assert.IsTrue(MemoryLayout.BeamTurned(true, true), "the swap inverts a ceiling host");
            Assert.IsFalse(MemoryLayout.BeamTurned(false, true), "the swap inverts a floor host");
            Assert.AreEqual(MemorySettings.DefaultPulseOn, s.PulseOn);
            Assert.AreEqual(MemorySettings.DefaultPulseOff, s.PulseOff);
            Assert.AreEqual(MemorySettings.DefaultAnswerTimeout, s.AnswerTimeout);
            Assert.AreEqual((float)MemorySettings.DefaultPadRadius, s.PadRadius);
            Assert.AreEqual((float)MemorySettings.DefaultPadGap, s.PadGap);
            Assert.AreEqual((float)MemorySettings.DefaultPadSpacing, s.PadSpacing);
            Assert.IsFalse(s.RandomLayout);
            Assert.IsNull(s.Check(), "the shipped string must itself be a legal game");

            Assert.AreEqual(4, s.Len);
            Assert.AreEqual(8, s.MaxLen);
            Assert.AreEqual(1.0f, s.PadRadius, "2 m across");

            foreach (var dead in new[] { "pct", "touch_radius", "spacing" })
                Assert.IsFalse(args.Has(dead), $"'{dead}' is retired from the memory settings string");

            CollectionAssert.AreEquivalent(
                new[] { "len", "max_len", "beams_per_pulse", "pulse_on", "pulse_off", "answer_timeout", "pad_radius", "pad_gap", "pad_spacing" },
                args.Keys.ToArray());
        }

        [TestMethod]
        public void Settings_AreClamped_AndTheMissingOrGarbageFallBackToDefaults()
        {
            var hi = MemorySettings.Read(HazardArgs.Parse("len=99 max_len=99 beams_per_pulse=99 pulse_on=99 pulse_off=99 answer_timeout=999 pad_radius=99 pad_gap=999 pad_spacing=99"));
            Assert.AreEqual(16, hi.Len);
            Assert.AreEqual(16, hi.MaxLen);
            Assert.AreEqual(16, hi.BeamsPerPulse, "round 4: raised from 8 so a pulse can carry twice the default 6 plus the corners");
            Assert.AreEqual(10.0, hi.PulseOn);
            Assert.AreEqual(10.0, hi.PulseOff);
            Assert.AreEqual(120.0, hi.AnswerTimeout);
            Assert.AreEqual(3f, hi.PadRadius);
            Assert.AreEqual(40f, hi.PadGap);
            Assert.AreEqual(3f, hi.PadSpacing);

            var lo = MemorySettings.Read(HazardArgs.Parse("len=-5 max_len=0 beams_per_pulse=0 pulse_on=0 pulse_off=-1 answer_timeout=0 pad_radius=0 pad_gap=0 pad_spacing=0"));
            Assert.AreEqual(1, lo.Len);
            Assert.AreEqual(1, lo.MaxLen);
            Assert.AreEqual(1, lo.BeamsPerPulse);
            Assert.AreEqual(0.2, lo.PulseOn);
            Assert.AreEqual(0.0, lo.PulseOff);
            Assert.AreEqual(3.0, lo.AnswerTimeout);
            Assert.AreEqual(0.5f, lo.PadRadius);
            Assert.AreEqual(2f, lo.PadGap);
            Assert.AreEqual(0.2f, lo.PadSpacing);

            var junk = MemorySettings.Read(HazardArgs.Parse("len=abc pad_radius=nan layout=spiral"));
            Assert.AreEqual(MemorySettings.DefaultLen, junk.Len);
            Assert.AreEqual((float)MemorySettings.DefaultPadRadius, junk.PadRadius);
            Assert.IsFalse(junk.RandomLayout);

            Assert.IsTrue(MemorySettings.Read(HazardArgs.Parse("layout=RANDOM")).RandomLayout);
            Assert.AreEqual(MemorySettings.DefaultLen, MemorySettings.Read(null).Len);
        }

        [TestMethod]
        public void Check_RefusesLenOverMaxLen_AndPadsThatWouldTouch()
        {
            StringAssert.Contains(MemorySettings.Read(HazardArgs.Parse("len=9 max_len=8")).Check(), "max_len");
            Assert.IsNull(MemorySettings.Read(HazardArgs.Parse("len=8 max_len=8")).Check(), "len == max_len is a legal one-round game");

            // radius 3 needs a gap of 6.5; the default gap of 6 would overlap
            StringAssert.Contains(MemorySettings.Read(HazardArgs.Parse("pad_radius=3")).Check(), "pad_gap");
            Assert.IsNull(MemorySettings.Read(HazardArgs.Parse("pad_radius=3 pad_gap=6.5")).Check());
            StringAssert.Contains(MemorySettings.Read(HazardArgs.Parse("pad_radius=1 pad_gap=2")).Check(), "pad_gap");
        }

        // ---- sequences -------------------------------------------------------------------------------------------

        [TestMethod]
        public void NewSequence_IsSeededReproducible_InRange_NeverBackToBack()
        {
            var a = MemoryRules.NewSequence(new Random(42), 16);
            var b = MemoryRules.NewSequence(new Random(42), 16);

            CollectionAssert.AreEqual(a, b);
            Assert.AreEqual(16, a.Count);
            Assert.IsTrue(a.All(c => c >= 0 && c < MemoryRules.PadCount));

            var other = MemoryRules.NewSequence(new Random(43), 16);
            CollectionAssert.AreNotEqual(a, other, "a different seed gives a different sequence");

            // 16 colours from 4 cannot all be distinct: a colour comes back later, but never directly after itself
            Assert.IsTrue(a.Distinct().Count() < a.Count);

            for (var seed = 0; seed < 50; seed++)
            {
                var long1 = MemoryRules.NewSequence(new Random(seed), 200);
                Assert.IsTrue(long1.All(c => c >= 0 && c < MemoryRules.PadCount), $"seed {seed}: in range");
                Assert.IsFalse(Enumerable.Range(1, 199).Any(i => long1[i] == long1[i - 1]), $"seed {seed}: no adjacent repeat");
            }

            // the skip-over draw is uniform over the other three: each of the 12 ordered pairs is about 1/12 of transitions
            var big = MemoryRules.NewSequence(new Random(0), 12001);
            var pairs = new int[MemoryRules.PadCount, MemoryRules.PadCount];
            for (var i = 1; i < big.Count; i++)
                pairs[big[i - 1], big[i]]++;
            for (var from = 0; from < MemoryRules.PadCount; from++)
                for (var to = 0; to < MemoryRules.PadCount; to++)
                    if (from != to)
                        Assert.IsTrue(pairs[from, to] > 750 && pairs[from, to] < 1250, $"{from}->{to} count {pairs[from, to]} is near 1000");
        }

        [TestMethod]
        public void Format_NamesTheColours()
        {
            Assert.AreEqual("red blue green purple blue", MemoryRules.Format(new[] { 0, 1, 2, 3, 1 }));
            Assert.AreEqual("", MemoryRules.Format(new int[0]));
        }

        [TestMethod]
        public void RoundLength_GrowsOneColourPerRound_EachRoundAFreshSequence_AndStopsAtMaxLen()
        {
            var rng = new Random(7);
            var replay = new Random(7);
            var game = Game(MemoryRules.NewSequence(rng, 4).ToArray(), 8);
            var s = new MemorySession(game, Pads, PadRadius, rng); // the same stream: round n+1 is the draw right after round n's
            var now = 0.0;
            var seen = new List<int[]>();

            game.BeginRound(now);

            for (var expectedLen = 4; expectedLen <= 8; expectedLen++)
            {
                Assert.AreEqual(expectedLen, game.Length, $"round {expectedLen - 3}");
                Assert.AreEqual(expectedLen - 3, game.Round);

                var before = game.Sequence.ToArray();

                // the sequence is dealt anew each round: it is the next draw of the run rng, never the old one plus a colour
                if (seen.Count > 0)
                    CollectionAssert.AreEqual(MemoryRules.NewSequence(replay, expectedLen).ToArray(), before, $"round {expectedLen - 3} is the next fresh draw");
                else
                    CollectionAssert.AreEqual(MemoryRules.NewSequence(replay, expectedLen).ToArray(), before);

                seen.Add(before);

                now = FinishPlayback(s, 1, now);

                List<MemoryEvent> last = null;

                for (var i = 0; i < expectedLen; i++)
                    last = Press(s, 1, game.ColourAt(i), ref now);

                if (expectedLen < 8)
                {
                    CollectionAssert.AreEqual(
                        new[] { MemoryEventKind.Correct, MemoryEventKind.RoundWon, MemoryEventKind.NextRound },
                        last.Select(e => e.Kind).ToArray());
                    Assert.AreEqual(MemoryPhase.Playback, game.Phase, "the next round plays at once");
                }
                else
                {
                    CollectionAssert.AreEqual(new[] { MemoryEventKind.Correct, MemoryEventKind.Won }, last.Select(e => e.Kind).ToArray());
                    Assert.AreEqual(MemoryPhase.Done, game.Phase);
                }
            }

            Assert.AreEqual(8, game.Length, "never grows past max_len");
            Assert.AreEqual(5, game.Round);
        }

        [TestMethod]
        public void NextRound_DealsAFreshSequence_NotThePreviousOnePlusAColour()
        {
            // find a seed where the fresh round-2 draw does NOT start with the round-1 sequence (the old Simon behaviour would)
            var found = 0;

            for (var seed = 1; seed < 60 && found < 5; seed++)
            {
                var rng = new Random(seed);
                var first = MemoryRules.NewSequence(rng, 3);
                var game = Game(first.ToArray(), 8);
                var peek = new Random(seed);

                MemoryRules.NewSequence(peek, 3);
                var expected = MemoryRules.NewSequence(peek, 4);

                game.NextRound(rng, 0);

                CollectionAssert.AreEqual(expected, game.Sequence.ToArray(), $"seed {seed}: round 2 is the next 4-colour draw");
                Assert.AreEqual(4, game.Length);
                Assert.AreEqual(2, game.Round);

                if (!expected.Take(3).SequenceEqual(first))
                    found++;
            }

            Assert.IsTrue(found >= 5, "most seeds deal a round 2 that does not begin with round 1: the sequence is not carried over");
        }

        [TestMethod]
        public void RoundBoundary_PressingTheOldFirstColour_IsWrong_WhenTheNewRoundStartsWithAnother()
        {
            var tested = 0;

            for (var seed = 1; seed < 80 && tested < 3; seed++)
            {
                var rng = new Random(seed);
                var firstRound = MemoryRules.NewSequence(rng, 1);
                var game = Game(firstRound.ToArray(), 3);
                var s = new MemorySession(game, Pads, PadRadius, rng);
                var now = FinishPlayback(s, 1, 0);
                var oldFirst = firstRound[0];

                CollectionAssert.AreEqual(
                    new[] { MemoryEventKind.Correct, MemoryEventKind.RoundWon, MemoryEventKind.NextRound },
                    Press(s, 1, oldFirst, ref now).Select(e => e.Kind).ToArray());

                if (game.ColourAt(0) == oldFirst)
                    continue; // this seed's new round happens to start with the same colour: not a discriminating case

                tested++;
                now = FinishPlayback(s, 1, now);

                var events = Press(s, 1, oldFirst, ref now);

                Assert.AreEqual(MemoryEventKind.Wrong, events.Single().Kind, $"seed {seed}: the old round's first colour is not the new round's");
                Assert.AreEqual(game.ColourAt(0), events.Single().Expected);
                Assert.AreEqual(MemoryPhase.Done, game.Phase);
            }

            Assert.AreEqual(3, tested, "found three discriminating seeds");
        }

        [TestMethod]
        public void NextRoundSequence_IsOneLonger_CappedAtMaxLen_AndSeedReproducible()
        {
            Assert.AreEqual(5, MemoryRules.NextRoundSequence(new Random(3), 4, 8).Count);
            Assert.AreEqual(8, MemoryRules.NextRoundSequence(new Random(3), 8, 8).Count, "capped");
            CollectionAssert.AreEqual(MemoryRules.NextRoundSequence(new Random(3), 4, 8), MemoryRules.NextRoundSequence(new Random(3), 4, 8));
        }

        [TestMethod]
        public void LenEqualToMaxLen_IsOneRound_AndCompletesOnTheLastEntry()
        {
            var game = Game(new[] { 0, 1 }, 2);
            var s = Session(game);
            var now = FinishPlayback(s, 1, 0);

            Assert.AreEqual(MemoryEventKind.Correct, Press(s, 1, 0, ref now).Last().Kind);
            Assert.AreEqual(MemoryEventKind.Won, Press(s, 1, 1, ref now).Last().Kind);
        }

        [TestMethod]
        public void EveryRoundsColours_AreDrawnFromTheRunRng_SoASeedReproducesTheWholeGame()
        {
            string Play(int seed)
            {
                var rng = new Random(seed);
                var game = Game(MemoryRules.NewSequence(rng, 3).ToArray(), 6);
                var s = new MemorySession(game, Pads, PadRadius, rng);
                var now = 0.0;
                game.BeginRound(now);

                while (game.Phase != MemoryPhase.Done)
                {
                    now = FinishPlayback(s, 1, now);

                    for (var i = 0; i < game.Length && game.Phase == MemoryPhase.Answer; i++)
                        Press(s, 1, game.ColourAt(i), ref now);
                }

                return MemoryRules.Format(game.Sequence);
            }

            Assert.AreEqual(Play(11), Play(11));
            Assert.AreEqual(6, Play(11).Split(' ').Length);
            Assert.AreNotEqual(Play(11), Play(12));
        }

        // ---- playback timeline --------------------------------------------------------------------------------

        [TestMethod]
        public void Playback_FiresEachColourOnTheIdealSchedule_ThenOpensTheAnswerWindow()
        {
            var game = Game(new[] { 2, 0, 3 }, 8, on: 1.0, off: 0.5, pause: 1.5, timeout: 20);
            game.BeginRound(10.0);

            Assert.AreEqual(MemoryPlayback.None, game.Poll(11.49, out _), "not before the round pause");

            Assert.AreEqual(MemoryPlayback.Fire, game.Poll(11.5, out var c0));
            Assert.AreEqual(2, c0);
            Assert.AreEqual(MemoryPlayback.None, game.Poll(11.5, out _), "one colour per slot");

            // on + off = 1.5 s per colour: 11.5, 13.0. A laggy tick at 13.2 fires the second colour late, and the
            // schedule is keyed off the ACTUAL fire time: the third is due at 13.2 + 1.5 = 14.7, not at the ideal 14.5.
            Assert.AreEqual(MemoryPlayback.Fire, game.Poll(13.2, out var c1));
            Assert.AreEqual(0, c1);
            Assert.AreEqual(MemoryPlayback.None, game.Poll(13.2, out _));

            Assert.AreEqual(MemoryPlayback.None, game.Poll(14.5, out _), "not at the ideal time any more");
            Assert.AreEqual(MemoryPlayback.Fire, game.Poll(14.7, out var c2));
            Assert.AreEqual(3, c2);

            // the last colour stands for pulse_on (1.0) after it ACTUALLY fired at 14.7
            Assert.AreEqual(MemoryPlayback.None, game.Poll(15.6, out _));
            Assert.AreEqual(MemoryPhase.Playback, game.Phase);

            Assert.AreEqual(MemoryPlayback.Finished, game.Poll(15.7, out _));
            Assert.AreEqual(MemoryPhase.Answer, game.Phase);
            Assert.AreEqual(35.7, game.Deadline, 1e-9, "the answer window runs from the end of playback");
        }

        [TestMethod]
        public void ALargeTick_FiresAtMostOneColourPerStep_AndSpacesThemOneTickApart()
        {
            // tick_ms = 1000 with pulse_on 0.2 and pulse_off 0: the ideal gap (0.2 s) is far below the tick
            var game = Game(new[] { 0, 1, 2 }, 8, on: 0.2, off: 0, pause: 1.0);
            var s = Session(game);
            var kinds = new List<string>();

            for (var tick = 1; tick <= 6; tick++)
            {
                var events = s.Step(tick, At((1, Away)));

                Assert.IsTrue(events.Count(e => e.Kind == MemoryEventKind.Fire) <= 1, $"tick {tick}: at most one Fire per step");
                Assert.IsTrue(events.Count <= 1, $"tick {tick}: one event per step");
                kinds.Add(events.Count == 0 ? "-" : events[0].Kind + (events[0].Kind == MemoryEventKind.Fire ? events[0].Colour.ToString() : ""));
            }

            CollectionAssert.AreEqual(new[] { "Fire0", "Fire1", "Fire2", "YourTurn", "-", "-" }, kinds.ToArray(),
                "colours one tick apart, then the answer window one tick after the last");
        }

        [TestMethod]
        public void TheAnswerWindowOpens_PulseOnAfterTheLastColourActuallyFired_NotAfterItsIdealTime()
        {
            var game = Game(new[] { 0 }, 8, on: 1.0, off: 0.5, pause: 1.5);

            Assert.AreEqual(MemoryPlayback.Fire, game.Poll(5.0, out _), "fired 3.5 s late");
            Assert.AreEqual(MemoryPlayback.None, game.Poll(5.9, out _), "the ideal finish (2.5) is long past, the real one is 6.0");
            Assert.AreEqual(MemoryPlayback.Finished, game.Poll(6.0, out _));
        }

        // ---- the touch edge trigger -------------------------------------------------------------------------------

        [TestMethod]
        public void Touch_EnterCounts_StayingDoesNot_ExitAndReenterCounts()
        {
            var t = new MemoryTouchTracker();

            Assert.AreEqual(0, t.Update(At((1, Away)), Pads, PadRadius).Count);

            var entered = t.Update(At((1, Pads[2])), Pads, PadRadius);
            Assert.AreEqual(1, entered.Count);
            Assert.AreEqual((1u, 2), entered[0]);

            Assert.AreEqual(0, t.Update(At((1, Pads[2])), Pads, PadRadius).Count, "staying does not count");
            Assert.AreEqual(0, t.Update(At((1, Pads[2] + new Vector2(0.4f, 0f))), Pads, PadRadius).Count, "moving inside the same circle does not count");

            Assert.AreEqual(0, t.Update(At((1, Away)), Pads, PadRadius).Count);
            Assert.AreEqual(1, t.Update(At((1, Pads[2])), Pads, PadRadius).Count, "exit and re-enter counts");
        }

        [TestMethod]
        public void Touch_IsInclusiveAtTheRadius_AndOutsideItIsNothing()
        {
            Assert.AreEqual(0, MemoryTouchTracker.PadAt(Pads[0] + new Vector2(1f, 0f), Pads, PadRadius));
            Assert.AreEqual(-1, MemoryTouchTracker.PadAt(Pads[0] + new Vector2(1.01f, 0f), Pads, PadRadius));
            Assert.AreEqual(-1, MemoryTouchTracker.PadAt(Away, Pads, PadRadius));
        }

        [TestMethod]
        public void Touch_APlayerWhoLeftTheRegionIsForgotten_SoComingBackCounts()
        {
            var t = new MemoryTouchTracker();

            Assert.AreEqual(1, t.Update(At((1, Pads[0])), Pads, PadRadius).Count);
            Assert.AreEqual(0, t.Update(At(), Pads, PadRadius).Count, "gone from the region");
            Assert.AreEqual(1, t.Update(At((1, Pads[0])), Pads, PadRadius).Count, "back on the same pad: a fresh entry");
        }

        [TestMethod]
        public void Touch_PlayersAreTrackedIndependently()
        {
            var t = new MemoryTouchTracker();

            t.Update(At((1, Pads[0])), Pads, PadRadius);

            var entered = t.Update(At((1, Pads[0]), (2, Pads[0])), Pads, PadRadius);

            CollectionAssert.AreEqual(new[] { (2u, 0) }, entered, "player 1 stays, player 2 arrives");
        }

        // ---- the state machine ------------------------------------------------------------------------------------

        [TestMethod]
        public void ATouchDuringPlayback_IsIgnored()
        {
            var game = Game(new[] { 1, 1 }, 8);
            var s = Session(game);
            game.BeginRound(0);

            var events = s.Step(0.1, At((1, Pads[3])));

            Assert.AreEqual(0, events.Count, "still in the round pause: nothing fires, nothing is judged");
            Assert.AreEqual(MemoryPhase.Playback, game.Phase);
            Assert.AreEqual(0, game.Position, "an ignored touch advances nothing");
            Assert.AreEqual(MemoryTouch.Ignored, game.Touch(1, 0.1), "even the RIGHT pad is ignored in playback");
        }

        [TestMethod]
        public void StandingOnAPadWhenTheAnswerWindowOpens_IsNotAnEntry()
        {
            var game = Game(new[] { 0 }, 8);
            var s = Session(game);
            game.BeginRound(0);

            // stand on the right pad from the first tick of playback
            var now = 0.0;
            var events = new List<MemoryEvent>();

            while (game.Phase == MemoryPhase.Playback)
            {
                now += 0.1;
                events.AddRange(s.Step(now, At((1, Pads[0]))));
            }

            Assert.IsTrue(events.Any(e => e.Kind == MemoryEventKind.YourTurn));
            Assert.AreEqual(0, game.Position, "having stood there all along is not an entry");

            now += 0.1;
            Assert.AreEqual(0, s.Step(now, At((1, Pads[0]))).Count, "still standing: nothing");

            now += 0.1;
            s.Step(now, At((1, Away)));
            now += 0.1;
            Assert.AreEqual(MemoryEventKind.Correct, s.Step(now, At((1, Pads[0]))).First().Kind, "leaving and re-entering answers");
        }

        [TestMethod]
        public void RepeatingAColour_NeedsASteppingOutAndBackIn()
        {
            var game = Game(new[] { 2, 2 }, 2);
            var s = Session(game);
            var now = FinishPlayback(s, 1, 0);

            now += 0.1;
            Assert.AreEqual(1, s.Step(now, At((1, Pads[2]))).Count(e => e.Kind == MemoryEventKind.Correct));
            now += 0.1;
            Assert.AreEqual(0, s.Step(now, At((1, Pads[2]))).Count, "standing on the pad does not repeat the colour");
            Assert.AreEqual(1, game.Position);

            now += 0.1;
            s.Step(now, At((1, Away)));
            now += 0.1;
            Assert.IsTrue(s.Step(now, At((1, Pads[2]))).Any(e => e.Kind == MemoryEventKind.Won));
        }

        [TestMethod]
        public void AWrongPad_FailsTheRun_NamesWhoAndWhatWasWanted_AndEndsTheGame()
        {
            var game = Game(new[] { 0, 1, 2 }, 8);
            var s = Session(game);
            var now = FinishPlayback(s, 7, 0);

            Press(s, 7, 0, ref now);
            var events = Press(s, 7, 3, ref now); // wanted blue (1), stepped on purple (3)

            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(MemoryEventKind.Wrong, events[0].Kind);
            Assert.AreEqual(7u, events[0].Player);
            Assert.AreEqual(3, events[0].Pad);
            Assert.AreEqual(1, events[0].Expected);
            Assert.AreEqual(1, events[0].Step);
            Assert.AreEqual(MemoryPhase.Done, game.Phase);

            Assert.AreEqual(0, Press(s, 7, 1, ref now).Count, "after a failure even the right pad does nothing");
        }

        [TestMethod]
        public void NoCorrectEntryInTime_FailsTheRun_ButExactlyAtTheDeadlineIsStillInTime()
        {
            var game = Game(new[] { 0, 1 }, 8, timeout: 20);
            var s = Session(game);
            game.BeginRound(0);

            var opened = FinishPlayback(s, 1, 0);

            Assert.AreEqual(opened + 20, game.Deadline, 1e-9);
            Assert.IsFalse(game.TimedOut(game.Deadline), "exactly at the deadline is still allowed");
            Assert.AreEqual(0, s.Step(game.Deadline, At((1, Away))).Count);

            var events = s.Step(game.Deadline + 0.1, At((1, Away)));
            Assert.AreEqual(MemoryEventKind.TimedOut, events.Single().Kind);
            Assert.AreEqual(0, events.Single().Step);
            Assert.AreEqual(MemoryPhase.Done, game.Phase);
        }

        [TestMethod]
        public void ACorrectEntry_RestartsTheAnswerWindow()
        {
            var game = Game(new[] { 0, 1 }, 8, timeout: 20);
            var s = Session(game);
            var opened = FinishPlayback(s, 1, 0);
            var now = opened + 15;

            Press(s, 1, 0, ref now);

            Assert.AreEqual(opened + 15.1 + 20, game.Deadline, 1e-9, "20 s from the correct entry, not from the end of playback");

            // 25 s after playback ended is past the original deadline but inside the restarted one
            Assert.IsFalse(game.TimedOut(opened + 25));
            Assert.IsTrue(game.TimedOut(game.Deadline + 0.01));
        }

        [TestMethod]
        public void TheTimeoutDoesNotRunDuringPlayback()
        {
            var game = Game(new[] { 0, 1, 2, 3, 0, 1 }, 8, timeout: 3);
            var s = Session(game);
            game.BeginRound(0);

            // playback lasts 1.5 + 5 x 1.5 + 1.0 = 10 s, longer than the 3 s answer timeout
            for (var now = 0.1; now < 9.9; now += 0.1)
            {
                var events = s.Step(now, At((1, Away)));
                Assert.IsFalse(events.Any(e => e.Kind == MemoryEventKind.TimedOut), $"timed out during playback at {now}");
            }

            Assert.AreEqual(MemoryPhase.Playback, game.Phase);
        }

        // ---- the shared room ---------------------------------------------------------------------------------------

        [TestMethod]
        public void TwoPlayersAlternatingCorrectEntries_AdvanceOneSharedSequence()
        {
            var game = Game(new[] { 0, 1, 2, 3 }, 4);
            var s = Session(game);
            var now = FinishPlayback(s, 1, 0);

            var who = new List<uint>();

            for (var i = 0; i < 4; i++)
            {
                var player = i % 2 == 0 ? 1u : 2u;
                now += 0.1;
                var events = s.Step(now, At((1, i % 2 == 0 ? Pads[game.ColourAt(i)] : Away), (2, i % 2 == 1 ? Pads[game.ColourAt(i)] : Away)));

                var correct = events.First(e => e.Kind == MemoryEventKind.Correct);
                Assert.AreEqual(player, correct.Player);
                Assert.AreEqual(i + 1, correct.Step, "one sequence: the second player's entry is step i+1, not step 1");
                who.Add(correct.Player);
            }

            CollectionAssert.AreEqual(new[] { 1u, 2u, 1u, 2u }, who);
            Assert.AreEqual(MemoryPhase.Done, game.Phase);
        }

        [TestMethod]
        public void OnePlayersWrongEntry_FailsItForEveryone_EvenAfterAnotherEnteredCorrectly()
        {
            var game = Game(new[] { 0, 1, 2 }, 8);
            var s = Session(game);
            var now = FinishPlayback(s, 1, 0);

            // both step onto pads on the same tick: player 1 right (red), player 2 wrong (green)
            now += 0.1;
            var events = s.Step(now, At((1, Pads[0]), (2, Pads[2])));

            CollectionAssert.AreEqual(new[] { MemoryEventKind.Wrong }, events.Select(e => e.Kind).ToArray(), "a wrong entry in the tick fails the room; the right one does not get to advance first");
            Assert.AreEqual(2u, events[0].Player);
            Assert.AreEqual(0, game.Position);
            Assert.AreEqual(MemoryPhase.Done, game.Phase);
        }

        [TestMethod]
        public void AWrongEntryBeforeARightOneInTheSameTick_StopsTheTick()
        {
            var game = Game(new[] { 0, 1 }, 8);
            var s = Session(game);
            var now = FinishPlayback(s, 1, 0);

            now += 0.1;
            var events = s.Step(now, At((1, Pads[3]), (2, Pads[0])));

            Assert.AreEqual(MemoryEventKind.Wrong, events.Single().Kind, "player 1's wrong entry ends it; player 2's right one is never counted");
            Assert.AreEqual(0, game.Position);
        }

        [TestMethod]
        public void EntriesAfterTheRoundEndingOne_BelongToTheOldRound_AndAreDropped()
        {
            var game = Game(new[] { 0 }, 8);
            var s = Session(game);
            var now = FinishPlayback(s, 1, 0);

            now += 0.1;
            var events = s.Step(now, At((1, Pads[0]), (2, Pads[0])));

            CollectionAssert.AreEqual(new[] { MemoryEventKind.Correct, MemoryEventKind.RoundWon, MemoryEventKind.NextRound }, events.Select(e => e.Kind).ToArray());
            Assert.AreEqual(0, game.Position);
            Assert.AreEqual(2, game.Length);
            Assert.AreEqual(MemoryPhase.Playback, game.Phase);
        }

        [TestMethod]
        public void Game_RefusesAnEmptySequence()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new MemoryGame(new int[0], 8, 1, 0.5, 1.5, 20));
        }

        // ---- review fixes ------------------------------------------------------------------------------------------

        /// <summary>The time of the tick on which playback ends, found by a dry run with nobody on a pad.</summary>
        private static double PlaybackEndsAt(double dt)
        {
            var s = Session(Game(new[] { 0, 1 }, 8));

            for (var now = dt; now < 100; now += dt)
                if (s.Step(now, At((1, Away))).Any(e => e.Kind == MemoryEventKind.YourTurn))
                    return now;

            Assert.Fail("playback never ended");
            return 0;
        }

        [DataTestMethod]
        [DataRow(0.1)]
        [DataRow(1.0)]
        public void AWrongPadTouchedOnTheTickThatEndsPlayback_IsDiscarded(double dt)
        {
            var endsAt = PlaybackEndsAt(dt);
            var game = Game(new[] { 0, 1 }, 8);
            var s = Session(game);

            for (var now = dt; now < endsAt - dt / 2; now += dt)
                s.Step(now, At((1, Away)));

            // stand on a WRONG pad (purple) exactly on the tick that opens the answer window
            var events = s.Step(endsAt, At((1, Pads[3])));

            CollectionAssert.AreEqual(new[] { MemoryEventKind.YourTurn }, events.Select(e => e.Kind).ToArray(), "a playback-era touch is not judged");
            Assert.AreEqual(MemoryPhase.Answer, game.Phase);

            Assert.AreEqual(0, s.Step(endsAt + dt, At((1, Pads[3]))).Count, "still standing there: not an entry either");
            Assert.AreEqual(MemoryPhase.Answer, game.Phase);
            Assert.AreEqual(0, game.Position);
        }

        [TestMethod]
        public void AnEntryOnATickPastTheDeadline_StillCounts_ButNoEntryTimesOut()
        {
            MemorySession Opened(out double deadline)
            {
                // pause 0, on 0.2, off 0, timeout 4: fires at ticks 0 and 1, the window opens at tick 2 -> deadline 6
                var g = Game(new[] { 0, 1 }, 8, on: 0.2, off: 0, pause: 0, timeout: 4);
                var s = Session(g);

                for (var tick = 0; tick <= 5; tick++)
                    s.Step(tick, At((1, Away)));

                deadline = g.Deadline;
                return s;
            }

            var entering = Opened(out var deadline);
            Assert.AreEqual(6.0, deadline, 1e-9);

            var events = entering.Step(6.5, At((1, Pads[0])));
            Assert.AreEqual(MemoryEventKind.Correct, events.Single().Kind, "a correct entry on the 6.5 tick beats the 6.0 deadline");
            Assert.AreEqual(10.5, entering.Game.Deadline, 1e-9, "and restarts the window");

            var idle = Opened(out _);
            Assert.AreEqual(MemoryEventKind.TimedOut, idle.Step(6.5, At((1, Away))).Single().Kind, "nobody entered: timed out");
        }

        [TestMethod]
        public void ASegmentThroughAPad_IsAnEntry_EvenWhenBothEndsAreOutsideIt()
        {
            var pads = new[] { new Vector2(10, 10) };
            var t = new MemoryTouchTracker();

            t.Update(At((1, new Vector2(0, 10))), pads, 1f);
            var entered = t.Update(At((1, new Vector2(20, 10))), pads, 1f);

            Assert.AreEqual(1, entered.Count, "ran through the pad within one 1000 ms tick");
            Assert.AreEqual((1u, 0), entered[0]);

            // control: the same end points with the pad well off the line are no entry
            var off = new MemoryTouchTracker();
            off.Update(At((1, new Vector2(0, 10))), new[] { new Vector2(10, 14) }, 1f);
            Assert.AreEqual(0, off.Update(At((1, new Vector2(20, 10))), new[] { new Vector2(10, 14) }, 1f).Count);

            // and a segment longer than MaxSegmentMetres is a teleport: only the end point counts
            var tele = new MemoryTouchTracker();
            tele.Update(At((1, new Vector2(-30, 10))), pads, 1f);
            Assert.AreEqual(0, tele.Update(At((1, new Vector2(20, 10))), pads, 1f).Count, "no pad is walked through on a teleport");
        }

        [TestMethod]
        public void TheSegmentTest_UsesTheClosestPoint_AtTheRadiusEdge()
        {
            Assert.AreEqual(0, MemoryTouchTracker.PadTouched(new Vector2(0, 11), new Vector2(20, 11), new[] { new Vector2(10, 10) }, 1f), "grazing at exactly the radius");
            Assert.AreEqual(-1, MemoryTouchTracker.PadTouched(new Vector2(0, 11.01f), new Vector2(20, 11.01f), new[] { new Vector2(10, 10) }, 1f));

            // two pads on the line: the end point's pad wins; with neither under the end point, the first reached
            var two = new[] { new Vector2(10, 10), new Vector2(30, 10) };
            Assert.AreEqual(0, MemoryTouchTracker.PadTouched(new Vector2(0, 10), new Vector2(20, 10), two, 1f), "first reached");
            Assert.AreEqual(1, MemoryTouchTracker.PadTouched(new Vector2(0, 10), new Vector2(30, 10), two, 1f), "the pad under the end point");
        }

        [TestMethod]
        public void MovingStraightFromOnePadToAnother_IsAnEntryToTheSecond()
        {
            var t = new MemoryTouchTracker();

            t.Update(At((1, Pads[0])), Pads, PadRadius);
            var entered = t.Update(At((1, Pads[1])), Pads, PadRadius);

            Assert.AreEqual((1u, 1), entered.Single());
        }

        [TestMethod]
        public void SameTickEntries_GiveTheSameOutcome_InEitherListOrder()
        {
            List<MemoryEvent> Play(bool aFirst)
            {
                var game = Game(new[] { 0, 1, 2 }, 8);
                var s = Session(game);
                var now = FinishPlayback(s, 9, 0);

                now += 0.1;
                var a = (1u, Pads[0]);  // right (red)
                var b = (2u, Pads[2]);  // wrong (green)

                return s.Step(now, aFirst ? At(a, b) : At(b, a));
            }

            var ab = Play(true);
            var ba = Play(false);

            Assert.AreEqual(1, ab.Count);
            Assert.AreEqual(MemoryEventKind.Wrong, ab[0].Kind);
            Assert.AreEqual(ab.Count, ba.Count);

            for (var i = 0; i < ab.Count; i++)
            {
                Assert.AreEqual(ab[i].Kind, ba[i].Kind);
                Assert.AreEqual(ab[i].Player, ba[i].Player);
                Assert.AreEqual(ab[i].Pad, ba[i].Pad);
                Assert.AreEqual(ab[i].Expected, ba[i].Expected);
            }

            Assert.AreEqual(2u, ab[0].Player, "the wrong player is named whatever the order");
        }

        [TestMethod]
        public void TwoPlayersOnTheSameRightPadInOneTick_AreOneEntry()
        {
            var game = Game(new[] { 0, 1 }, 8);
            var s = Session(game);
            var now = FinishPlayback(s, 9, 0);

            now += 0.1;
            var events = s.Step(now, At((2, Pads[0]), (1, Pads[0])));

            Assert.AreEqual(1, events.Count(e => e.Kind == MemoryEventKind.Correct));
            Assert.AreEqual(1u, events.Single().Player, "the lowest player id takes the entry");
            Assert.AreEqual(1, game.Position, "the sequence advanced once, not twice");
        }

        [DataTestMethod]
        [DataRow(true, true, false)]
        [DataRow(true, false, true)]
        [DataRow(false, false, false)]
        [DataRow(false, true, false)]
        public void MarkerRepair_RespawnsOnlyAPlacedMarkerThatDied(bool placed, bool alive, bool expected)
        {
            Assert.AreEqual(expected, MemoryMarkers.NeedsRespawn(placed, alive));
        }

        [TestMethod]
        public void MinimumPlaced_IsMaxOfThreeAndHalfTheRingRoundedUp()
        {
            Assert.AreEqual(4, MemoryMarkers.MinimumPlaced(8));
            Assert.AreEqual(6, MemoryMarkers.MinimumPlaced(11));
            Assert.AreEqual(16, MemoryMarkers.MinimumPlaced(32));
            Assert.AreEqual(3, MemoryMarkers.MinimumPlaced(2), "never under three");
        }

        [TestMethod]
        public void Planar_KeepsXAsXAndYAsY()
        {
            Assert.AreEqual(new Vector2(3, 7), MemoryMarkers.Planar(new Vector3(3, 7, 99)));
        }
    }
}
