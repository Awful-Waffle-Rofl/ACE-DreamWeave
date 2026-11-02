using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>HazardContext's radar API (hide once, restore exactly, restore on teardown) through the radar write seam.</summary>
    [TestClass]
    public class HazardContextRadarTests
    {
        private static HazardContext NewContext(int id)
            => new HazardRun(id, "test", null, HazardRegionPresets.Pinnacle(0), HazardArgs.Empty, 1, null, null, null, DateTime.UtcNow).Context;

        [TestMethod]
        public void OverlappingRuns_RestoreTheAuthoredValue_WhenEitherEnds()
        {
            var c = TestCreatures.CreateDefender();
            c.RadarBehavior = RadarBehavior.ShowAlways;
            ACE.Entity.Models.BiotaExtensions.SetProperty(c.Biota, ACE.Entity.Enum.Properties.PropertyInt.ShowableOnRadar, (int)RadarBehavior.ShowAlways, c.BiotaDatabaseLock, out _);

            var a = NewContext(1);
            var b = NewContext(2);

            Assert.IsTrue(a.HideFromRadar(c));
            Assert.AreEqual(RadarBehavior.ShowNever, c.RadarBehavior);
            Assert.IsTrue(b.HideFromRadar(c), "the second run hides it too; it records the biota value, not the first run's overlay");

            a.Teardown(HazardOutcome.Stopped, wasStarted: true);
            Assert.AreEqual(RadarBehavior.ShowAlways, c.RadarBehavior, "A restores the authored value (B re-hides on its next scan)");

            b.Teardown(HazardOutcome.Stopped, wasStarted: true);
            Assert.AreEqual(RadarBehavior.ShowAlways, c.RadarBehavior, "B restores the same authored value");
        }

        [TestMethod]
        public void AbandonedRun_DoesNotPoisonLaterRuns()
        {
            var c = TestCreatures.CreateDefender();
            ACE.Entity.Models.BiotaExtensions.SetProperty(c.Biota, ACE.Entity.Enum.Properties.PropertyInt.ShowableOnRadar, (int)RadarBehavior.ShowAlways, c.BiotaDatabaseLock, out _);
            c.RadarBehavior = RadarBehavior.ShowAlways;

            var a = NewContext(1);
            var b = NewContext(2);

            Assert.IsTrue(a.HideFromRadar(c));   // A is reaped without a teardown
            Assert.IsTrue(b.HideFromRadar(c));
            b.Teardown(HazardOutcome.Stopped, wasStarted: true);

            Assert.AreEqual(RadarBehavior.ShowAlways, c.RadarBehavior);
        }

        [TestMethod]
        public void RouteRadarWrite_WritesInlineQueuesOrSkips()
        {
            Assert.AreEqual(HazardDestroyRoute.Now, HazardContext.RouteRadarWrite(true, true));
            Assert.AreEqual(HazardDestroyRoute.Now, HazardContext.RouteRadarWrite(true, false), "both null: a test or unloaded world writes inline");
            Assert.AreEqual(HazardDestroyRoute.EnqueueOnItsLandblock, HazardContext.RouteRadarWrite(false, true));
            Assert.AreEqual(HazardDestroyRoute.Skip, HazardContext.RouteRadarWrite(false, false));
        }
        private static (HazardContext Ctx, List<(Creature Creature, RadarBehavior? Value)> Writes) Fixture()
        {
            var run = new HazardRun(1, "test", null, HazardRegionPresets.Pinnacle(0), HazardArgs.Empty, 1, null, null, null, DateTime.UtcNow);
            var writes = new List<(Creature, RadarBehavior?)>();

            run.Context.RadarWriteForTest = (c, v) =>
            {
                writes.Add((c, v));
                return true;
            };

            return (run.Context, writes);
        }

        [TestMethod]
        public void HideFromRadar_WritesShowNeverOnce()
        {
            var (ctx, writes) = Fixture();
            var c = TestCreatures.CreateDefender();

            Assert.IsTrue(ctx.HideFromRadar(c));
            Assert.IsFalse(ctx.HideFromRadar(c), "the second call does nothing");

            Assert.AreEqual(1, writes.Count);
            Assert.AreSame(c, writes[0].Creature);
            Assert.AreEqual(RadarBehavior.ShowNever, writes[0].Value);
            CollectionAssert.AreEqual(new[] { c.Guid.Full }, ctx.RadarHidden.ToList());
        }

        [TestMethod]
        public void RestoreRadar_GivesBackTheAuthoredValue_IncludingNone()
        {
            var (ctx, writes) = Fixture();
            var authored = TestCreatures.CreateDefender();
            ACE.Entity.Models.BiotaExtensions.SetProperty(authored.Biota, ACE.Entity.Enum.Properties.PropertyInt.ShowableOnRadar, (int)RadarBehavior.ShowMovement, authored.BiotaDatabaseLock, out _);
            var none = TestCreatures.CreateDefender();

            Assert.IsNull(none.RadarBehavior, "harness control: this creature authored no radar behaviour");

            ctx.HideFromRadar(authored);
            ctx.HideFromRadar(none);
            writes.Clear();

            Assert.IsTrue(ctx.RestoreRadar(authored.Guid.Full));
            Assert.IsTrue(ctx.RestoreRadar(none.Guid.Full));
            Assert.IsFalse(ctx.RestoreRadar(none.Guid.Full), "restored once");

            Assert.AreEqual(RadarBehavior.ShowMovement, writes.Single(w => w.Creature == authored).Value);
            Assert.IsNull(writes.Single(w => w.Creature == none).Value);
            Assert.AreEqual(0, ctx.RadarHidden.Count);
        }

        [TestMethod]
        public void HazardContextRadar_Teardown_RestoresEveryHiddenCreature()
        {
            var (ctx, writes) = Fixture();
            var c = TestCreatures.CreateDefender();

            ctx.HideFromRadar(c);
            writes.Clear();

            ctx.Teardown(HazardOutcome.Aborted, wasStarted: true);

            Assert.AreEqual(1, writes.Count);
            Assert.AreSame(c, writes[0].Creature);
            Assert.IsNull(writes[0].Value, "the authored null comes back");
            Assert.AreEqual(0, ctx.RadarHidden.Count);
        }

        [TestMethod]
        public void RestoreRadar_OnADestroyedCreature_WritesNothing_AndDropsTheEntry()
        {
            var (ctx, writes) = Fixture();
            var c = TestCreatures.CreateDefender();

            ctx.HideFromRadar(c);
            writes.Clear();
            c.Destroy();

            Assert.IsTrue(c.IsDestroyed, "harness control");
            Assert.IsFalse(ctx.RestoreRadar(c.Guid.Full));
            Assert.AreEqual(0, writes.Count);
            Assert.AreEqual(0, ctx.RadarHidden.Count);
        }
    }
}
