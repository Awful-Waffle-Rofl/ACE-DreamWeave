using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Server.Entity;
using ACE.Server.HazardMechanics;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The hit telemetry of World Event hazards: a real ctx.HitPlayers call through RollingBallMechanic.Hit reaches the balls
    /// stop line's hits= field, and WorldEventHazardHost's IHazardHost.OnPlayerHit forwards into its tally (a source pin,
    /// because a Player cannot be built without a world).
    /// </summary>
    [TestClass]
    public class WorldEventHazardTelemetryTests
    {
        private const ushort Anchor = 0x7F7F;

        private sealed class FakePlayers : HazardContext.IPlayerSource
        {
            public readonly Dictionary<Landblock, List<HazardContext.PlayerSighting>> Lists = new Dictionary<Landblock, List<HazardContext.PlayerSighting>>(ReferenceEqualityComparer.Instance);

            public int Count(Landblock landblock) => Lists.TryGetValue(landblock, out var l) ? l.Count : 0;

            public bool TryGet(Landblock landblock, int index, out HazardContext.PlayerSighting sighting)
            {
                sighting = Lists[landblock][index];
                return true;
            }

            public void Put(Landblock lb, uint guid, float x, float y, float z)
            {
                if (!Lists.TryGetValue(lb, out var l))
                    Lists[lb] = l = new List<HazardContext.PlayerSighting>();

                l.Add(new HazardContext.PlayerSighting(null, guid, new Position(((uint)Anchor << 16) | 0x0001, x, y, z, 0f, 0f, 0f, 1f, 0), false, false));
            }
        }

        [TestMethod]
        public void BallStopLine_CountsTheHitsAnActualHitPlayersCallLanded()
        {
            var world = new HazardSpanTests.FakeWorld();
            var group = new object();
            var lb = world.Add(Anchor, group);
            var region = HazardRegion.ArenaCircle("arena", Anchor, 0, 96f, 96f, 20f, 1.5f, 0f, 20f);
            var run = new HazardRun(1, "balls", null, region, HazardArgs.Empty, 1, null, null, null, DateTime.UtcNow);
            var ctx = run.Context;
            var players = new FakePlayers();

            ctx.Span = HazardSpan.For(region, lb, world);
            ctx.Span.Refresh();
            ctx.PlayerSource = players;
            ctx.DamageForTest = (src, seen, fraction, type, text) => 7;
            ctx.SetNow(1.0);

            var lines = new List<string>();

            ctx.LogForTest = lines.Add;

            players.Put(lb, 1, 96f, 96f, 5f);
            players.Put(lb, 2, 97f, 96f, 5f);
            players.Put(lb, 3, 190f, 190f, 5f); // out of reach

            var mechanic = new RollingBallMechanic();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var typeOfMechanic = typeof(RollingBallMechanic);

            typeOfMechanic.GetField("p", flags).SetValue(mechanic, BallRules.Read(HazardArgs.Merge(HazardArgs.Empty, HazardArgs.Parse(HazardDefaults.Balls)), BallPatterns.Row));

            var ballType = typeOfMechanic.GetNestedType("Ball", BindingFlags.NonPublic);
            var ball = Activator.CreateInstance(ballType, true);
            var wo = (GenericObject)RuntimeHelpers.GetUninitializedObject(typeof(GenericObject));

            ballType.GetField("Handle").SetValue(ball, new HazardHandle(1, wo, new HazardSpawnSpec { Wcid = 1006951, Tag = "ball" }, Vector3.Zero));

            var hit = typeOfMechanic.GetMethod("Hit", flags);
            var landed = (int)hit.Invoke(mechanic, new object[] { ctx, ball, new Vector3(96f, 96f, 5f) });

            Assert.AreEqual(2, landed, "control: two players are in reach of the ball");

            mechanic.OnStop(ctx, HazardOutcome.Stopped);

            var stop = lines.Single(l => l.StartsWith("balls stop outcome=Stopped", StringComparison.Ordinal));

            Assert.IsTrue(stop.EndsWith(" hits=2", StringComparison.Ordinal), stop);
        }

        [TestMethod]
        public void WorldEventHazardHost_OnPlayerHit_ForwardsPlayerGuidDamageAndMaxHealthIntoTheTally()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventHazards.cs");
            var body = PooledLootSourceText.MethodBody(text, "void IHazardHost.OnPlayerHit(HazardRun run, Player player, int amount)");

            StringAssert.Contains(body, "RecordHit(run, player.Guid.Full, amount, player.Health.MaxValue)");
        }
    }
}