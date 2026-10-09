using System;
using System.Collections.Generic;
using System.Numerics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.PuzzleGates;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure pieces of the puzzle-gate live layer: activation routing across a reshuffle, the reap
    /// decision, the telemetry line, the reroll grammar and the anchor-yaw convention.
    /// </summary>
    [TestClass]
    public class PuzzleGatePlacementTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        private static PuzzleGatePlacement NewPlacement(string type = "sigil", params string[] extra)
        {
            var args = new List<string> { type };
            args.AddRange(extra);
            Assert.IsTrue(PuzzleGateOptions.TryParse(args, out var options, out var error), error);

            var anchor = new Position(0xA9B40001, 96f, 96f, 10f, 0f, 0f, 0f, 1f, 0);
            return new PuzzleGatePlacement(7, options, 1234, anchor, 0f, null, 0x50000001, null, T0);
        }

        private static PuzzlePlan Plan(int answerSlot) => new PuzzlePlan { Type = PuzzleGateType.Sigil, RoundIndex = 1, AnswerSlot = answerSlot, Specs = Array.Empty<PuzzleObjectSpec>() };

        [TestMethod]
        public void Route_LeverOfARetiredRound_IsStale_AndNeverScored()
        {
            var p = NewPlacement();
            p.MarkLive(T0);

            p.BeginRound(Plan(1), new[] { (0x80000001u, 0), (0x80000002u, 1), (0x80000003u, 2) }, T0);
            Assert.AreEqual(PuzzleActivationRoute.Current, p.Route(0x80000002u, false, out var slot));
            Assert.AreEqual(1, slot);
            Assert.IsTrue(p.IsCorrect(slot));

            // Reshuffle: new guids. An activation queued against round 1's correct lever runs afterwards.
            p.RetireRound();
            p.BeginRound(Plan(0), new[] { (0x80000011u, 0), (0x80000012u, 1), (0x80000013u, 2) }, T0.AddSeconds(1));

            Assert.AreEqual(PuzzleActivationRoute.StaleRound, p.Route(0x80000002u, false, out slot));
            Assert.AreEqual(-1, slot);
            Assert.AreEqual(PuzzleActivationRoute.Current, p.Route(0x80000011u, false, out slot));
            Assert.AreEqual(0, slot);
        }

        [TestMethod]
        public void Route_BetweenRetireAndBeginRound_EveryLeverIsStale()
        {
            var p = NewPlacement();
            p.MarkLive(T0);
            p.BeginRound(Plan(0), new[] { (0x80000001u, 0) }, T0);
            p.RetireRound();

            Assert.AreEqual(PuzzleActivationRoute.StaleRound, p.Route(0x80000001u, false, out _));
        }

        [TestMethod]
        public void Route_DestroyedLever_IsStale_EvenIfStillListed()
        {
            var p = NewPlacement();
            p.MarkLive(T0);
            p.BeginRound(Plan(0), new[] { (0x80000001u, 0) }, T0);

            Assert.AreEqual(PuzzleActivationRoute.StaleRound, p.Route(0x80000001u, true, out _));
        }

        [TestMethod]
        public void Route_NotLiveStates_AreRefused_SolvedStillRoutes()
        {
            var p = NewPlacement();
            p.BeginRound(Plan(0), new[] { (0x80000001u, 0) }, T0);

            Assert.AreEqual(PuzzleActivationRoute.NotLive, p.Route(0x80000001u, false, out _), "spawning");

            p.MarkLive(T0);
            Assert.AreEqual(PuzzleActivationRoute.Current, p.Route(0x80000001u, false, out _));

            // Solved keeps routing so the lever answers with the "spent" line.
            p.MarkSolved("Tester", T0);
            Assert.AreEqual(PuzzleActivationRoute.Current, p.Route(0x80000001u, false, out _));

            p.MarkCleared();
            Assert.AreEqual(PuzzleActivationRoute.NotLive, p.Route(0x80000001u, false, out _));
        }

        [TestMethod]
        public void ShouldReap_Table()
        {
            var fresh = TimeSpan.FromSeconds(1);
            var stale = TimeSpan.FromSeconds(PuzzleGateTunables.SpawnTimeoutSeconds + 1);

            Assert.IsFalse(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Live, false, stale));
            Assert.IsTrue(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Live, true, fresh), "gate destroyed behind our back");
            Assert.IsTrue(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Solved, true, fresh));
            Assert.IsFalse(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Solved, false, stale));

            // A queued spawn that has not run yet is not reaped for lacking a gate...
            Assert.IsFalse(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Spawning, true, fresh));
            // ...until it is old enough that it never will (unload cleared the action queue).
            Assert.IsTrue(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Spawning, true, stale));

            Assert.IsTrue(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Failed, false, fresh));
            Assert.IsTrue(PuzzleGatePlacement.ShouldReap(PuzzlePlacementState.Cleared, false, fresh));
        }

        [TestMethod]
        public void TelemetryLine_CarriesEveryField()
        {
            var p = NewPlacement("odd", "rounds=2");
            p.MarkLive(T0);
            p.RecordActivation(T0.AddMilliseconds(1500));
            p.Rules.Activate(false, T0.AddMilliseconds(1500));
            p.Rules.Activate(true, T0.AddSeconds(10));
            p.Rules.Activate(true, T0.AddSeconds(12));
            p.MarkSolved("Tester", T0.AddSeconds(12));

            var line = p.TelemetryLine("solved", T0.AddSeconds(12));

            Assert.AreEqual("[PUZZLE_GATE] id=7 type=odd seed=1234 outcome=solved solver=Tester ms_first=1500 ms_solve=12000 wrong=1 rounds=2/2", line);
        }

        [TestMethod]
        public void TelemetryLine_UnsolvedClear_UsesMinusOne()
        {
            var p = NewPlacement();
            var line = p.TelemetryLine("cleared", T0);

            StringAssert.Contains(line, "outcome=cleared solver=- ms_first=-1 ms_solve=-1 wrong=0 rounds=0/3");
        }

        [TestMethod]
        public void RerollGrammar()
        {
            Assert.IsTrue(PuzzleGateCommands.TryParseReroll(new string[0], out var id, out var seed, out _));
            Assert.IsNull(id);
            Assert.IsNull(seed);

            Assert.IsTrue(PuzzleGateCommands.TryParseReroll(new[] { "seed=-5", "3" }, out id, out seed, out _));
            Assert.AreEqual(3, id);
            Assert.AreEqual(-5, seed);

            Assert.IsFalse(PuzzleGateCommands.TryParseReroll(new[] { "3", "4" }, out _, out _, out var error));
            StringAssert.Contains(error, "'4'");

            Assert.IsFalse(PuzzleGateCommands.TryParseReroll(new[] { "seed=1", "seed=2" }, out _, out _, out _));
            Assert.IsFalse(PuzzleGateCommands.TryParseReroll(new[] { "bogus" }, out _, out _, out _));
        }

        /// <summary>
        /// The anchor yaw the live layer derives from a player's rotation must put the layout's "forward" on the
        /// same side as Position.InFrontOf, the existing in-game notion of "in front of me". Checked at headings
        /// in all four quadrants, so a sign flip or a 90 degree offset cannot pass.
        /// </summary>
        [TestMethod]
        public void AnchorYaw_ForwardMatchesPositionInFrontOf()
        {
            foreach (var headingDeg in new[] { 0f, 37f, 90f, 143f, 180f, 251f, 300f })
            {
                var h = headingDeg * Math.PI / 180.0;
                var q = new Quaternion(0f, 0f, (float)Math.Sin(h / 2), (float)Math.Cos(h / 2));
                var pos = new Position(0xA9B40001, 96f, 96f, 10f, q.X, q.Y, q.Z, q.W, 0);

                var yaw = PuzzleGateManager.AnchorYawDeg(pos);
                var layoutForward = PuzzleGateGenerator.ToWorld(Vector3.Zero, yaw, new Vector3(0, 1, 0));

                var ahead = pos.InFrontOf(1.0);
                var inFrontOf = new Vector3(ahead.PositionX - pos.PositionX, ahead.PositionY - pos.PositionY, 0);

                Assert.AreEqual(inFrontOf.X, layoutForward.X, 1e-4, $"heading {headingDeg}: x");
                Assert.AreEqual(inFrontOf.Y, layoutForward.Y, 1e-4, $"heading {headingDeg}: y");
            }
        }
    }
}
