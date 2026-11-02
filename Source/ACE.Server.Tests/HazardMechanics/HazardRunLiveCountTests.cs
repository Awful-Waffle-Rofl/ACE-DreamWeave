using System;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HAZARDS-PLAN S0 contract surface on the run and the context: LiveCount, DamageType, HitVerb, the settings-layer
    /// seam, and the rain stub registration. Pure: runs are built with no landblock and no PropertyManager read.
    /// </summary>
    [TestClass]
    public class HazardRunLiveCountTests
    {
        private static HazardRun RunWith(string args = "")
        {
            HazardMechanicRegistry.TryCreate(HazardMechanicRegistry.Wisps, out var mechanic);
            var region = HazardRegion.Rect("t", 0x0069, 0, false, 0, 10, 0, 10, HazardGroundMode.Plane, 0, null, 0, null, 1, 1);
            return new HazardRun(1, HazardMechanicRegistry.Wisps, "scatter", region, HazardArgs.Parse(args), 1, null, mechanic, null, DateTime.UtcNow);
        }

        [TestMethod]
        public void LiveCount_IsNullByDefault()
        {
            var run = RunWith();

            Assert.IsNull(run.LiveCount);
            Assert.IsNull(run.Context.LiveCount);
        }

        [TestMethod]
        public void SetLiveCount_RoundTrips_AndNullClears()
        {
            var run = RunWith();

            run.SetLiveCount(7);
            Assert.AreEqual(7, run.LiveCount);
            Assert.AreEqual(7, run.Context.LiveCount);

            run.SetLiveCount(0);
            Assert.AreEqual(0, run.LiveCount, "zero is a count, not 'unset'");

            run.SetLiveCount(-3);
            Assert.AreEqual(-3, run.LiveCount, "the run stores what it is given; mechanics clamp");

            run.SetLiveCount(null);
            Assert.IsNull(run.LiveCount);
            Assert.IsNull(run.Context.LiveCount);
        }

        [TestMethod]
        public void SetLiveCount_IsVisibleAcrossThreads()
        {
            var run = RunWith();
            int? seen = null;

            run.SetLiveCount(12);
            var t = new System.Threading.Thread(() => seen = run.LiveCount);
            t.Start();
            t.Join();

            Assert.AreEqual(12, seen);
        }

        [TestMethod]
        public void DamageType_DefaultsToFire_AndReadsTheFiveWords()
        {
            Assert.AreEqual(DamageType.Fire, RunWith().Context.DamageType);
            Assert.AreEqual(DamageType.Fire, RunWith("damage_type=fire").Context.DamageType);
            Assert.AreEqual(DamageType.Acid, RunWith("damage_type=acid").Context.DamageType);
            Assert.AreEqual(DamageType.Cold, RunWith("damage_type=cold").Context.DamageType);
            Assert.AreEqual(DamageType.Electric, RunWith("damage_type=ELECTRIC").Context.DamageType);
            Assert.AreEqual(DamageType.Bludgeon, RunWith("damage_type=bludgeon").Context.DamageType);
            Assert.AreEqual(DamageType.Fire, RunWith("damage_type=plasma").Context.DamageType, "unknown reads as fire; Validate refuses it elsewhere");
        }

        [TestMethod]
        public void HitVerb_DefaultsToSears_AndReadsTheArg()
        {
            Assert.AreEqual("sears", RunWith().Context.HitVerb);
            Assert.AreEqual("sears", RunWith("hit_verb=").Context.HitVerb);
            Assert.AreEqual("freezes", RunWith("hit_verb=freezes").Context.HitVerb);
            Assert.AreEqual(HazardDefaults.HitVerb, "sears");
            Assert.AreEqual("damage_type", HazardDefaults.DamageTypeKey);
            Assert.AreEqual("hit_verb", HazardDefaults.HitVerbKey);
        }

        [TestMethod]
        public void SettingsLayerFor_ReturnsAProvidedLayerAsIs_WithoutReadingSettings()
        {
            var layer = HazardArgs.Parse("speed=2.5 lead=4");

            Assert.AreSame(layer, HazardManager.SettingsLayerFor(HazardMechanicRegistry.Balls, layer));

            // an empty layer is still a layer: the live row is NOT consulted
            Assert.AreSame(HazardArgs.Empty, HazardManager.SettingsLayerFor(HazardMechanicRegistry.Balls, HazardArgs.Empty));
        }

        [TestMethod]
        public void Rain_IsRegisteredAsTheRainMechanic_ButIsNotALibraryMechanicKind()
        {
            Assert.AreEqual("rain", HazardMechanicRegistry.Rain);
            Assert.IsTrue(HazardMechanicRegistry.TryCreate("rain", out var mechanic));
            Assert.AreEqual("rain", mechanic.Kind);
            Assert.IsInstanceOfType(mechanic, typeof(RainMechanic));
            CollectionAssert.DoesNotContain(new System.Collections.Generic.List<string>(HazardMechanicRegistry.MechanicKinds), "rain");
            CollectionAssert.Contains(new System.Collections.Generic.List<string>(HazardMechanicRegistry.Kinds), "rain");
            Assert.IsNull(HazardTunables.SettingsKeyFor("rain"));
        }
    }
}
