using System;

using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Grael;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The plate-floor lift decision (PinnaclePlateLift) on Grael's floor, every boundary with a positive and a negative
    /// control, plus the data and wiring that carry it to the live world. Pure: no PropertyManager key, no dats.
    /// </summary>
    [TestClass]
    public class PinnaclePlateLiftTests
    {
        private static readonly PinnaclePlateFloor F = GraelData.PlateFloor;

        private const float InX = 32.5f;
        private const float InY = -20.0f;

        /// <summary>The prod case, 2026-10-22: Nyls stuck at 0x00690119 [32.5 -20 -47.995].</summary>
        private const float StuckZ = -47.995f;

        /// <summary>Where a body standing on a plate rests (PinnacleArenaGeometryTests).</summary>
        private const float OnPlateZ = -47.745f;

        private const float Eps = 0.001f;

        private static int Count(string s, string sub)
        {
            var n = 0;

            for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal))
                n++;

            return n;
        }

        private static bool Lift(float x, float y, float z, bool teleporting = false, bool dead = false, double? last = null, double now = 100)
            => PinnaclePlateLift.ShouldLift(F, x, y, z, teleporting, dead, last, now);

        [TestMethod]
        public void TheProdCase_IsLifted_AndABodyOnThePlates_IsNot()
        {
            Assert.IsTrue(Lift(InX, InY, StuckZ), "standing on the block top under the plate");
            Assert.IsFalse(Lift(InX, InY, OnPlateZ), "standing on the plate");
            Assert.IsFalse(Lift(InX, InY, GraelData.FightEntryZ), "in the air above the plates (the entry point)");
        }

        [TestMethod]
        public void PlateBottom_IsExclusive()
        {
            Assert.IsFalse(Lift(InX, InY, F.BottomZ), "exactly at the bottom: not under");
            Assert.IsTrue(Lift(InX, InY, F.BottomZ - Eps), "just under the bottom");
        }

        [TestMethod]
        public void LowestZ_IsExclusive_AndThePitFloorIsAboveIt()
        {
            Assert.IsFalse(Lift(InX, InY, F.LowestZ), "exactly at the lower limit: not lifted");
            Assert.IsFalse(Lift(InX, InY, F.LowestZ - 5f), "far below: not lifted");
            Assert.IsTrue(Lift(InX, InY, F.LowestZ + Eps), "just above the lower limit");
            Assert.IsTrue(Lift(InX, -10f, -53.995f), "standing on the pit floor (z -54)");
        }

        [TestMethod]
        public void Rectangle_EdgesAreInclusive_AndJustOutsideIsNot()
        {
            Assert.IsTrue(Lift(F.MinX, InY, StuckZ));
            Assert.IsFalse(Lift(F.MinX - Eps, InY, StuckZ));
            Assert.IsTrue(Lift(F.MaxX, InY, StuckZ));
            Assert.IsFalse(Lift(F.MaxX + Eps, InY, StuckZ), "the east doorway's ramp end (x 35, z -48) is outside the plates");
            Assert.IsTrue(Lift(InX, F.MinY, StuckZ));
            Assert.IsFalse(Lift(InX, F.MinY - Eps, StuckZ));
            Assert.IsTrue(Lift(InX, F.MaxY, StuckZ));
            Assert.IsFalse(Lift(InX, F.MaxY + Eps, StuckZ));
        }

        [TestMethod]
        public void Teleporting_AndDead_AreNeverLifted()
        {
            Assert.IsTrue(Lift(InX, InY, StuckZ, teleporting: false, dead: false), "control");
            Assert.IsFalse(Lift(InX, InY, StuckZ, teleporting: true));
            Assert.IsFalse(Lift(InX, InY, StuckZ, dead: true));
        }

        [TestMethod]
        public void RateLimit_OnePerTwoSecondsPerPlayer()
        {
            Assert.IsTrue(Lift(InX, InY, StuckZ, last: null, now: 100), "never lifted");
            Assert.IsFalse(Lift(InX, InY, StuckZ, last: 100, now: 100), "just lifted");
            Assert.IsFalse(Lift(InX, InY, StuckZ, last: 100, now: 100 + PinnaclePlateLift.MinSecondsBetweenLifts - 0.001), "just under the limit");
            Assert.IsTrue(Lift(InX, InY, StuckZ, last: 100, now: 100 + PinnaclePlateLift.MinSecondsBetweenLifts), "at the limit");
            Assert.AreEqual(2.0, PinnaclePlateLift.MinSecondsBetweenLifts);
        }

        [TestMethod]
        public void NoFloor_NeverLifts()
        {
            Assert.IsFalse(PinnaclePlateLift.IsUnderPlates(null, InX, InY, StuckZ));
            Assert.IsFalse(PinnaclePlateLift.ShouldLift(null, InX, InY, StuckZ, false, false, null, 100));
        }

        /// <summary>The numbers are pinnacle_arena.sql's: 20 plates at origin z -47.85 spanning origin-0.05..+0.10.</summary>
        [TestMethod]
        public void GraelFloor_MatchesTheArenaContent_AndRidesOnTheScriptData()
        {
            Assert.AreEqual(-4.943f, F.MinX);
            Assert.AreEqual(34.943f, F.MaxX);
            Assert.AreEqual(-34.959f, F.MinY);
            Assert.AreEqual(-5.041f, F.MaxY);
            Assert.AreEqual(-47.85f - 0.05f, F.BottomZ, 1e-5f);
            Assert.AreEqual(-55.0f, F.LowestZ);
            Assert.AreEqual(GraelData.SpawnZ, F.LiftZ, "lifted to the proven spawn z");
            Assert.IsTrue(F.LiftZ > F.BottomZ + 0.15f, "the lift z is above the plate top (-47.75)");

            Assert.AreSame(GraelData.PlateFloor, GraelData.Script().PlateFloor, "Grael's script data carries the floor");
        }

        /// <summary>
        /// Item 2: the entry z is one and a half metres above the spawn z, and only the entry moved: every creature point
        /// still uses SpawnZ.
        /// </summary>
        [TestMethod]
        public void FightEntry_IsOneAndAHalfMetresAboveSpawnZ_AndTheCreaturePointsAreUnchanged()
        {
            Assert.AreEqual(-46.20f, GraelData.FightEntryPoint.Z);
            Assert.AreEqual(GraelData.SpawnZ + 1.5f, GraelData.FightEntryPoint.Z, 1e-4f);
            Assert.AreEqual(-47.70f, GraelData.SpawnZ, "SpawnZ is unchanged");
            Assert.AreEqual(GraelData.FightEntryPoint, GraelData.Script().FightEntryPoint);

            foreach (var v in GraelData.Variants)
                Assert.AreEqual(GraelData.SpawnZ, v.At.Z, v.Tag);
            foreach (var p in GraelData.VariantPoints)
                Assert.AreEqual(GraelData.SpawnZ, p.Z);
            Assert.AreEqual(GraelData.SpawnZ, GraelData.GraelSpawn.Z);
        }

        // ---- escalation: the 3rd lift inside 30 s is a teleport ----

        [TestMethod]
        public void Escalation_TheSecondLiftIsForced_TheThirdInsideTheWindowTeleports()
        {
            Assert.AreEqual(30.0, PinnaclePlateLift.EscalationWindowSeconds);
            Assert.AreEqual(3, PinnaclePlateLift.TeleportOnLift);

            Assert.IsFalse(PinnaclePlateLift.ShouldTeleport(null, 100), "1st lift (no history): forced position");
            Assert.IsFalse(PinnaclePlateLift.ShouldTeleport(new double[0], 100), "1st lift (empty history): forced position");
            Assert.IsFalse(PinnaclePlateLift.ShouldTeleport(new[] { 98.0 }, 100), "2nd lift: forced position");
            Assert.IsTrue(PinnaclePlateLift.ShouldTeleport(new[] { 96.0, 98.0 }, 100), "3rd lift inside the window: teleport");
            Assert.IsTrue(PinnaclePlateLift.ShouldTeleport(new[] { 94.0, 96.0, 98.0 }, 100), "4th lift inside the window: teleport again");
        }

        [TestMethod]
        public void Decide_ForcesThenTeleports_AndGivesUpPastTwoTeleportsIn60Seconds()
        {
            Assert.AreEqual(60.0, PinnaclePlateLift.TeleportCapWindowSeconds);
            Assert.AreEqual(2, PinnaclePlateLift.MaxTeleportsPerWindow);

            var twoLifts = new[] { 96.0, 98.0 };

            Assert.AreEqual(PinnaclePlateLift.LiftAction.Force, PinnaclePlateLift.Decide(new[] { 98.0 }, null, 100), "2nd lift: forced");
            Assert.AreEqual(PinnaclePlateLift.LiftAction.Force, PinnaclePlateLift.Decide(new[] { 98.0 }, new[] { 90.0, 95.0 }, 100), "a forced lift is never capped");
            Assert.AreEqual(PinnaclePlateLift.LiftAction.Teleport, PinnaclePlateLift.Decide(twoLifts, null, 100), "1st escalation");
            Assert.AreEqual(PinnaclePlateLift.LiftAction.Teleport, PinnaclePlateLift.Decide(twoLifts, new[] { 70.0 }, 100), "2nd escalation (one in the window)");
            Assert.AreEqual(PinnaclePlateLift.LiftAction.GiveUp, PinnaclePlateLift.Decide(twoLifts, new[] { 70.0, 90.0 }, 100), "3rd escalation inside 60 s: give up");
            Assert.AreEqual(PinnaclePlateLift.LiftAction.Teleport, PinnaclePlateLift.Decide(twoLifts, new[] { 40.0, 90.0 }, 100), "a teleport exactly 60 s old no longer counts");
            Assert.AreEqual(PinnaclePlateLift.LiftAction.GiveUp, PinnaclePlateLift.Decide(twoLifts, new[] { 40.001, 90.0 }, 100), "just inside 60 s still counts");
        }

        /// <summary>Second review: the escalation teleport goes to the entry z, past the placement's step-down reach.</summary>
        [TestMethod]
        public void TeleportZ_IsTheFightEntryZ_PastTheStepDownBoundary()
        {
            Assert.AreEqual(GraelData.FightEntryZ, F.TeleportZ);
            Assert.IsTrue(F.TeleportZ > -46.45f, "above -46.45, the first z the placement keeps (measured 2026-10-22)");
            Assert.AreEqual(GraelData.SpawnZ, F.LiftZ, "the forced lift stays at the spawn z");
        }

        [TestMethod]
        public void Escalation_AnOldLiftFallsOutOfTheWindow()
        {
            const double now = 100;
            var w = PinnaclePlateLift.EscalationWindowSeconds;

            Assert.IsTrue(PinnaclePlateLift.ShouldTeleport(new[] { now - w + 0.001, now - 2 }, now), "just inside the window: still counts");
            Assert.IsFalse(PinnaclePlateLift.ShouldTeleport(new[] { now - w, now - 2 }, now), "exactly the window old: no longer counts");
            Assert.IsFalse(PinnaclePlateLift.ShouldTeleport(new[] { now - w - 10, now - 2 }, now), "long gone: no longer counts");

            Assert.AreEqual(1, PinnaclePlateLift.CountInWindow(new[] { now - w, now - 2 }, now));
            Assert.AreEqual(2, PinnaclePlateLift.CountInWindow(new[] { now - w + 0.001, now - 2 }, now));
            Assert.AreEqual(0, PinnaclePlateLift.CountInWindow(null, now));
        }

        /// <summary>
        /// The live wiring: the floor reaches the live world, the lift is a server-forced move (physics copy, Location, a new
        /// ObjectForcePosition sequence, then the position) that sends nothing when the cell is not loaded, it escalates to a
        /// teleport on the 3rd lift in the window, and it logs its INFO / WARN lines.
        /// </summary>
        [TestMethod]
        public void LiveWorld_LiftsByForcedPosition_WithTheFloorFromTheScriptData()
        {
            var copy = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleEncounter.cs"), "private bool CreateCopy(");
            var ctor = copy.IndexOf("new LivePinnacleWorld(", StringComparison.Ordinal);
            var end = copy.IndexOf(");", ctor, StringComparison.Ordinal);
            Assert.IsTrue(ctor >= 0 && end > ctor);
            StringAssert.Contains(copy.Substring(ctor, end - ctor), "data.PlateFloor", "the encounter passes the floor");

            var live = PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/LivePinnacleWorld.cs");
            StringAssert.Contains(live, "this.plateFloor = plateFloor;");

            var check = PooledLootSourceText.MethodBody(live, "public void LiftPlayersUnderPlateFloor(");
            StringAssert.Contains(check, "landblock.PlayersOnLandblockThread");
            var givenUp = check.IndexOf("plateLiftGivenUp.Contains(p.Guid.Full)", StringComparison.Ordinal);
            var prune = check.IndexOf("lifts.RemoveAll(t => now - t >= PinnaclePlateLift.EscalationWindowSeconds);", StringComparison.Ordinal);
            var should = check.IndexOf("PinnaclePlateLift.ShouldLift(plateFloor, loc.PositionX, loc.PositionY, loc.PositionZ, p.Teleporting, p.IsDead, last, now)", StringComparison.Ordinal);
            var capPrune = check.IndexOf("teleports.RemoveAll(t => now - t >= PinnaclePlateLift.TeleportCapWindowSeconds);", StringComparison.Ordinal);
            var decide = check.IndexOf("var action = PinnaclePlateLift.Decide(lifts, teleports, now);", StringComparison.Ordinal);
            var giveUp = check.IndexOf("plateLiftGivenUp.Add(p.Guid.Full);", StringComparison.Ordinal);
            var stamp = check.IndexOf("lifts.Add(now);", StringComparison.Ordinal);
            var tpStamp = check.IndexOf("teleports.Add(now);", StringComparison.Ordinal);
            var tpTarget = check.IndexOf("var to = LiftTarget(loc, plateFloor.TeleportZ);", StringComparison.Ordinal);
            var enqueue = check.IndexOf("WorldManager.EnqueueAction(new ActionEventDelegate(() =>", StringComparison.Ordinal);
            var tpGuard = check.IndexOf("if (who.Teleporting || who.IsDead || who.Session == null || who.Location == null || who.Location.Instance != instance)", StringComparison.Ordinal);
            var teleport = check.IndexOf("who.Teleport(to, source: TeleportSource.Other);", StringComparison.Ordinal);
            var move = check.IndexOf("else if (LiftTo(p, LiftTarget(loc, plateFloor.LiftZ)))", StringComparison.Ordinal);
            Assert.IsTrue(givenUp >= 0 && prune > givenUp && should > prune && capPrune > should && decide > capPrune && giveUp > decide && stamp > giveUp && tpStamp > stamp
                && tpTarget > tpStamp && enqueue > tpTarget && tpGuard > enqueue && teleport > tpGuard && move > teleport,
                $"givenUp {givenUp} prune {prune} should {should} capPrune {capPrune} decide {decide} giveUp {giveUp} stamp {stamp} tpStamp {tpStamp} tpTarget {tpTarget} enqueue {enqueue} guard {tpGuard} teleport {teleport} move {move}");
            Assert.IsFalse(check.Contains("ThreadSafeTeleport("), "the teleport is re-validated when it runs, not queued blind");
            Assert.AreEqual(1, Count(check, "plateFloor.TeleportZ"), "only the teleport branch uses TeleportZ");
            Assert.AreEqual(1, Count(check, "plateFloor.LiftZ"), "the forced lift uses LiftZ");
            StringAssert.Contains(check, "[PINNACLE] lift did not take for {p.Name}, teleporting onto the plates");
            Assert.IsTrue(check.IndexOf("log.Warn($\"[PINNACLE] lift did not take", StringComparison.Ordinal) >= 0, "the escalation logs at WARN");
            Assert.IsTrue(check.IndexOf("log.Error($\"[PINNACLE] gave up lifting {p.Name}", StringComparison.Ordinal) > decide, "giving up logs at ERROR");
            StringAssert.Contains(check, "log.Info($\"[PINNACLE] lifted {p.Name} from {from} onto the plates\")");
            var failed = check.IndexOf("log.Warn($\"[PINNACLE] could not lift {p.Name}", StringComparison.Ordinal);
            Assert.IsTrue(failed > move, "a lift that did not happen logs WARN, not the INFO line");

            var lift = PooledLootSourceText.MethodBody(live, "private static bool LiftTo(");
            var getCell = lift.IndexOf("LScape.get_landcell(to.Cell, to.Instance)", StringComparison.Ordinal);
            var guard = lift.IndexOf("if (cell == null)\n                    return false;", StringComparison.Ordinal);
            if (guard < 0)
                guard = lift.IndexOf("if (cell == null)\r\n                    return false;", StringComparison.Ordinal);
            var physics = lift.IndexOf("p.PhysicsObj.set_current_pos(", StringComparison.Ordinal);
            var location = lift.IndexOf("p.Location = to;", StringComparison.Ordinal);
            var seq = lift.IndexOf("SequenceType.ObjectForcePosition", StringComparison.Ordinal);
            var send = lift.IndexOf("p.SendUpdatePosition();", StringComparison.Ordinal);
            Assert.IsTrue(getCell >= 0 && guard > getCell && physics > guard && location > physics && seq > location && send > seq,
                $"cell {getCell} guard {guard} physics {physics} location {location} seq {seq} send {send}: a missing cell returns before Location, the sequence and the packet");
            StringAssert.Contains(lift, "return true;");
            Assert.IsFalse(lift.Contains("Teleport"), "LiftTo is a forced position, never a teleport");

            var target = PooledLootSourceText.MethodBody(live, "internal static Position LiftTarget(");
            StringAssert.Contains(target, "WorldObject.AdjustDungeonCells(to);", "the cell is re-resolved for the lifted point");
        }
    }
}