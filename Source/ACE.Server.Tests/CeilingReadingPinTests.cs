using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins what each ceiling reading reports as Current and Max for fixed inputs (default tunables), through the pure
    /// BuildReadout cores, so a reading can no longer drift from the value that reaches its clamp.
    /// </summary>
    [TestClass]
    public class CeilingReadingPinTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context) => DefaultPropertyManager.LoadDefaultProperties();

        private static ClassAbilityCeilingReading Reading(ClassAbilityReadout readout, string id) => readout.Ceilings.Single(r => r.Id == id);

        private static void Pin(ClassAbilityCeilingReading r, double current, double max, bool binding)
        {
            Assert.AreEqual(current, r.Current, 1e-9, "Current");
            Assert.AreEqual(max, r.Max, 1e-9, "Max");
            Assert.AreEqual(binding, r.Binding, "Binding");
        }

        [TestMethod]
        public void ManaBarrier_Current_IsThePreClampShare()
        {
            var a = new ManaBarrierAbility();
            // rank 3: 0.06 + 2 x 0.06 = 0.18, x1.3 adds 0.054, plus 0.10 gear = 0.334 against the 0.30 cap
            Pin(Reading(a.BuildReadout(3, 1.3, 0.10), "total"), 33.4, 30.0, true);
            // rank 1, no affinity, no gear: 0.06
            Pin(Reading(a.BuildReadout(1, 1.0, 0.0), "total"), 6.0, 30.0, false);
        }

        [TestMethod]
        public void SoulTether_Current_IsThePreClampReduction()
        {
            var a = new SoulTetherAbility();
            // rank 3: 0.105 + 2 x 0.085 = 0.275, x1.3 adds 0.0825, plus 0.20 gear = 0.5575 against 0.50
            Pin(Reading(a.BuildReadout(3, 1.3, 0.20), "total"), 55.75, 50.0, true);
            Pin(Reading(a.BuildReadout(1, 1.0, 0.0), "total"), 10.5, 50.0, false);
        }

        [TestMethod]
        public void SpellAoe_Current_IsThePreClampRadiatedFraction()
        {
            var a = new SpellAoeAbility();
            // rank 3 base 0.50, x1.3 rider 0.15, plus 0.20 gear = 0.85 against 0.75
            Pin(Reading(a.BuildReadout(3, 1.3, 0.20), "total"), 85.0, 75.0, true);
            Pin(Reading(a.BuildReadout(1, 1.0, 0.0), "total"), 15.0, 75.0, false);
        }

        [TestMethod]
        public void ManaRush_Total_ReadsScaled_AtMultiplier1Point3()
        {
            var a = new NetherRushAbility();
            // rank 3: 0.05 x 3 = 0.15 per stack, x1.3 = 0.195, five stacks = 0.975 against 0.50 x 1.3 = 0.65
            Pin(Reading(a.BuildReadout(3, 1.3, 0), "total"), 97.5, 65.0, true);
            // rank 1 at 1.3: 0.065 x 5 = 0.325 against 0.65, not binding (the unscaled 0.25 vs 0.50 agrees)
            Pin(Reading(a.BuildReadout(1, 1.3, 0), "total"), 32.5, 65.0, false);
            // no Loyalty: unscaled
            Pin(Reading(a.BuildReadout(3, 1.0, 0), "total"), 75.0, 50.0, true);
        }

        [TestMethod]
        public void ManaRush_Stacks_ReportsHeldStacks()
        {
            var a = new NetherRushAbility();
            Pin(Reading(a.BuildReadout(3, 1.0, 4), "stacks"), 4.0, 5.0, false);
            Pin(Reading(a.BuildReadout(3, 1.0, 5), "stacks"), 5.0, 5.0, true);
        }

        [TestMethod]
        public void ReadScaled_ScalesMax_AndNeverBelowOne()
        {
            var def = ClassAbilityRegistry.Abilities[ClassAbilityId.NetherRush];
            Pin(ClassAbilityCeilings.ReadScaled(def, "total", 60.0, 1, ClassAbilityText.Live, 1.3), 60.0, 65.0, false);
            Pin(ClassAbilityCeilings.ReadScaled(def, "total", 65.0, 1, ClassAbilityText.Live, 1.3), 65.0, 65.0, true);
            Pin(ClassAbilityCeilings.ReadScaled(def, "total", 49.0, 1, ClassAbilityText.Live, 0.5), 49.0, 50.0, false);
        }
    }
}
