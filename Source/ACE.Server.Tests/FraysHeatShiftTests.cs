using System.Linq;

using ACE.Server.Frays;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>The mechanic Shifts, the Heat curves, and no Shift paying more.</summary>
    [TestClass]
    public class FraysHeatShiftTests
    {
        [TestInitialize]
        public void Init() => FraysSettings.ResetCacheForTest();

        [TestMethod]
        public void HeatShifts_OneWeeklyShiftOnlyFromHeatFive()
        {
            // A mechanic Shift week: nothing below Heat 5, then just that one.
            Assert.AreEqual(FraysAffix.None, FraysHeatShifts.ActiveAt(FraysAffix.Raging, 4, 5));
            Assert.AreEqual(FraysAffix.Raging, FraysHeatShifts.ActiveAt(FraysAffix.Raging, 5, 5));
            Assert.AreEqual(FraysAffix.Raging, FraysHeatShifts.ActiveAt(FraysAffix.Raging, 20, 5));
            // A numbers Shift week has no mechanic at any Heat.
            Assert.AreEqual(FraysAffix.None, FraysHeatShifts.ActiveAt(FraysAffix.HeavyThread, 20, 5));

            Assert.IsTrue(FraysHeatShifts.All.All(s => FraysWeek.Shifts.Contains(s)), "the mechanic Shifts are in the weekly rotation");
            Assert.IsTrue(FraysHeatShifts.All.All(s => FraysHeatShifts.Effect(s).Length > 0 && FraysHeatShifts.Effect(s).All(c => c < 128)));
        }

        [TestMethod]
        public void Shifts_EveryOneNamedAndDescribedOnce()
        {
            foreach (var s in FraysWeek.Shifts)
            {
                Assert.AreNotEqual("none", FraysWeek.Name(s), $"{s} has a name");
                Assert.IsTrue(FraysWeek.Effect(s).Length > 0, $"{s} has an effect line");
            }

            Assert.AreEqual(FraysWeek.Shifts.Length, FraysWeek.Shifts.Distinct().Count());
            Assert.IsFalse(FraysHeatShifts.Numbers.Any(s => FraysHeatShifts.All.Contains(s)), "a Shift is numbers or a mechanic, not both");
            Assert.IsTrue(FraysHeatShifts.Numbers.All(s => FraysWeek.Shifts.Contains(s)));
            // The reward-only Threads modifiers (Radiant, Enlightened, Bounteous, the Affinities) are not Shifts.
            Assert.IsFalse(System.Enum.GetNames(typeof(FraysAffix)).Any(n => n == "Radiant" || n == "Enlightened" || n == "Bounteous"));
        }

        [TestMethod]
        public void NumberShifts_HitTheRightCreatures()
        {
            Assert.AreEqual((1.2, 1.3), FraysHeatShifts.SpawnMults(FraysAffix.Fortified, false, false), "Fortified: pack creatures");
            Assert.AreEqual((1.0, 1.0), FraysHeatShifts.SpawnMults(FraysAffix.Fortified, true, false), "not elites");
            Assert.AreEqual((1.3, 1.15), FraysHeatShifts.SpawnMults(FraysAffix.Tyrannical, true, false), "Tyrannical: elites");
            Assert.AreEqual((1.3, 1.15), FraysHeatShifts.SpawnMults(FraysAffix.Tyrannical, false, true), "and Snarls");
            Assert.AreEqual((1.0, 1.0), FraysHeatShifts.SpawnMults(FraysAffix.Tyrannical, false, false), "not packs");
            Assert.AreEqual((2.0, 1.0), FraysHeatShifts.SpawnMults(FraysAffix.Guarded, false, true));
            Assert.AreEqual((1.0, 1.4), FraysHeatShifts.SpawnMults(FraysAffix.Enraged, false, true));
            Assert.AreEqual((1.0, 1.0), FraysHeatShifts.SpawnMults(FraysAffix.Enraged, true, false), "Enraged is Snarls only");

            Assert.AreEqual((20, 0, 0), FraysHeatShifts.Ratings(FraysAffix.Savage));
            Assert.AreEqual((0, 20, 0), FraysHeatShifts.Ratings(FraysAffix.Precise));
            Assert.AreEqual((0, 0, 20), FraysHeatShifts.Ratings(FraysAffix.Stalwart));
            Assert.AreEqual((0, 0, 0), FraysHeatShifts.Ratings(FraysAffix.Swift));
        }

        [TestMethod]
        public void Linked_MovesAShareAndKeepsTheRest()
        {
            Assert.AreEqual((100.0, 0.0), FraysHeatShifts.LinkSplit(100, 0), "alone: all of it");
            var (kept, each) = FraysHeatShifts.LinkSplit(100, 3);
            Assert.AreEqual(70, kept, 1e-9);
            Assert.AreEqual(10, each, 1e-9);
            Assert.AreEqual(100, kept + each * 3, 1e-9, "no damage lost or added");

            // A killing blow moves only from the health the target had, never its overkill.
            (kept, each) = FraysHeatShifts.LinkSplit(1000, 3, 100);
            Assert.AreEqual(970, kept, 1e-9, "30% of the 100 it could take moves");
            Assert.AreEqual(10, each, 1e-9);
            Assert.AreEqual((70.0, 10.0), FraysHeatShifts.LinkSplit(100, 3, 500), "not a killing blow: as before");
        }

        [TestMethod]
        public void HeatCurve_EveryHeatHarderAndPayEndsNearTheOldTop()
        {
            for (var h = 1; h < 20; h++)
            {
                Assert.IsTrue(FraysCurve.Health(h + 1) > FraysCurve.Health(h), $"health rises past Heat {h}");
                Assert.IsTrue(FraysCurve.Damage(h + 1) > FraysCurve.Damage(h), $"damage rises past Heat {h}");
            }

            Assert.AreEqual(3.0, FraysCurve.Health(20), 1e-9, "x3 at Heat 20 (was x6)");
            Assert.AreEqual(2.4, FraysCurve.Damage(20), 1e-9);
            Assert.AreEqual(1.435, FraysCurve.Income(0), 1e-9, "XP climbs with Heat for everyone");
            Assert.AreEqual(1.64, FraysCurve.Income(20), 1e-9, "80 / 70 of Heat 0");
            Assert.AreEqual("Heat 13: the creatures grow fiercer (health x1.27, damage x1.2).", FraysCurve.FierceLine(13));
        }

        [TestMethod]
        public void Shifts_NeverPayMore()
        {
            // Shifts only make a week harder; none gives more Embers or loot.
            foreach (var s in FraysWeek.Shifts)
            {
                var m = FraysCampMods.Build(null, SnarlPact.Normal, s, null);
                Assert.AreEqual(1.0, m.Embers, 1e-9, $"{s} Embers");
                Assert.IsTrue(FraysWeek.Effect(s).All(c => c < 128), $"{s} text");
            }
        }
    }
}
