using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins what each BloodMage/Spellsword ceiling reading reports as Current and Max for fixed inputs (default tunables), through the
    /// pure BuildReadout cores (or GetReadout(null) where the handler is null-safe), so a reading can no longer drift from the value
    /// that reaches its clamp. The expected readout numbers are the pre-extraction inline arithmetic worked by hand from the defaults.
    /// </summary>
    [TestClass]
    public class CeilingReadingPin2cTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context) => DefaultPropertyManager.LoadDefaultProperties();

        private static ClassAbilityCeilingReading Reading(ClassAbilityReadout readout, string id) => readout.Ceilings.Single(r => r.Id == id);

        private static void Pin(ClassAbilityReadout readout, string id, double current, double max, bool binding)
        {
            var r = Reading(readout, id);
            Assert.AreEqual(current, r.Current, 1e-9, id + " Current");
            Assert.AreEqual(max, r.Max, 1e-9, id + " Max");
            Assert.AreEqual(binding, r.Binding, id + " Binding");
        }

        private static void PinTerms(ClassAbilityReadout readout, double skill, double affinity, double gear, string capNote)
        {
            Assert.AreEqual(skill, readout.Skill, 1e-9, "Skill");
            Assert.AreEqual(affinity, readout.Affinity, 1e-9, "Affinity");
            Assert.AreEqual(gear, readout.Gear, 1e-9, "Gear");
            Assert.AreEqual(skill + affinity + gear, readout.Effective, 1e-9, "Effective");
            Assert.AreEqual(capNote, readout.CapNote, "CapNote");
        }

        [TestMethod]
        public void Spellblade_Current_IsThePreClampAffinityAdded()
        {
            var a = new SpellbladeAbility();
            // base 0.25; x1.3 adds 0.075 (under the 0.20 cap); 0.05 gear against 3 x 0.036 = 0.108
            var under = a.BuildReadout(3, 1.3, 0.05);
            PinTerms(under, 25.0, 7.5, 5.0, null);
            Pin(under, "affinity", 7.5, 20.0, false);
            Pin(under, "gear", 5.0, 10.8, false);

            // x2.0 adds 0.25: the readout shows the capped 20, the reading keeps the raw 25
            var over = a.BuildReadout(3, 2.0, 0.108);
            PinTerms(over, 25.0, 20.0, 10.8, "affinity cap");
            Pin(over, "affinity", 25.0, 20.0, true);
            Pin(over, "gear", 10.8, 10.8, true);

            // rank 0: nothing owned, nothing reads
            PinTerms(a.BuildReadout(0, 2.0, 0.05), 0.0, 0.0, 0.0, null);
        }

        [TestMethod]
        public void Runeblade_Current_IsThePreClampAffinityAdded()
        {
            var a = new RunebladeAbility();
            // base 0.22; x1.5 adds 0.11
            var r = a.BuildReadout(3, 1.5, 0.108);
            PinTerms(r, 22.0, 11.0, 10.8, null);
            Pin(r, "affinity", 11.0, 20.0, false);
            Pin(r, "gear", 10.8, 10.8, true);

            // x3.0 adds 0.44 against the 0.20 cap
            var capped = a.BuildReadout(3, 3.0, 0.0);
            PinTerms(capped, 22.0, 20.0, 0.0, "affinity cap");
            Pin(capped, "affinity", 44.0, 20.0, true);
        }

        [TestMethod]
        public void DispellingEdge_Current_IsThePreClampAffinityAdded()
        {
            var a = new DispellingEdgeAbility();
            // rank 2: 0.10 + 0.06 = 0.16; x2.0 adds 0.16 (under the cap); gear 0.06 = 3 x 0.02
            var r = a.BuildReadout(2, 2.0, 0.06);
            PinTerms(r, 16.0, 16.0, 6.0, null);
            Pin(r, "affinity", 16.0, 20.0, false);
            Pin(r, "gear", 6.0, 6.0, true);

            // x3.0 adds 0.32 against the cap
            Pin(a.BuildReadout(2, 3.0, 0.0), "affinity", 32.0, 20.0, true);
        }

        [TestMethod]
        public void Cascade_Readings_AffinityGearAndGeneration()
        {
            var a = new CascadeAbility();
            // rank 3 base 0.15; x2.0 adds 0.15; gear cap is 3 x 0.025 = 0.075, so 0.025 is a third of it
            var r = a.BuildReadout(3, 2.0, 0.025);
            PinTerms(r, 15.0, 15.0, 2.5, null);
            Pin(r, "affinity", 15.0, 20.0, false);
            Pin(r, "gear", 2.5, 7.5, false);
            // structural reading: the ability is stateless, so Current is 0 and the reading only states the budget
            Pin(r, "generation", 0.0, 1.0, false);

            Pin(a.BuildReadout(3, 3.0, 0.0), "affinity", 30.0, 20.0, true);

            // null player: neutral affinity, no gear
            Pin(a.GetReadout(null, 3), "affinity", 0.0, 20.0, false);
        }

        [TestMethod]
        public void Sundermark_Readings_AffinityGearAndReachedLevel()
        {
            var a = new SundermarkAbility();
            // rank 2: 0.07 + 0.06 = 0.13; x2.0 adds 0.13; Life Magic 250 reaches level 6 of 8; gear 0.045 = 3 x 0.015
            var r = a.BuildReadout(2, 2.0, 250u, 0.045);
            PinTerms(r, 13.0, 13.0, 4.5, null);
            Pin(r, "affinity", 13.0, 20.0, false);
            Pin(r, "level", 6.0, 8.0, false);
            Pin(r, "level_nether", 6.0, 8.0, false);
            Pin(r, "gear", 4.5, 4.5, true);

            Pin(a.BuildReadout(2, 1.0, 400u, 0.0), "level", 8.0, 8.0, true);
        }

        [TestMethod]
        public void Sundermark_ReachedLevel_FollowsTheLadderThresholds()
        {
            Assert.AreEqual(1, SundermarkAbility.ReachedLevel(0u, 1.0), "below every threshold still reaches rung 1");
            Assert.AreEqual(5, SundermarkAbility.ReachedLevel(249u, 1.0), "249 meets 200 (level 5), not 250");
            Assert.AreEqual(6, SundermarkAbility.ReachedLevel(250u, 1.0));
            Assert.AreEqual(8, SundermarkAbility.ReachedLevel(400u, 1.0));
            Assert.AreEqual(8, SundermarkAbility.ReachedLevel(100000u, 1.0), "never above the ladder's top, whatever the tunables");
            Assert.AreEqual(4, SundermarkAbility.ReachedLevel(250u, 1.5), "a 1.5 threshold scale needs 225 for level 5, so 250 reaches level 4");
        }

        [TestMethod]
        public void VolatileElements_Current_IsThePreClampAffinityAdded()
        {
            var a = new VolatileElementsAbility();
            // rank 3: 3 x 0.08 = 0.24; x1.5 adds 0.12
            var r = a.BuildReadout(3, 1.5);
            PinTerms(r, 24.0, 12.0, 0.0, null);
            Pin(r, "affinity", 12.0, 20.0, false);

            // x3.0 adds 0.48 against the 0.20 cap
            var capped = a.BuildReadout(3, 3.0);
            PinTerms(capped, 24.0, 20.0, 0.0, "affinity cap");
            Pin(capped, "affinity", 48.0, 20.0, true);
        }

        [TestMethod]
        public void Spellstorm_SpellsurgeAndResonance_ReadThroughNullPlayer()
        {
            Pin(new SpellstormAbility().GetReadout(null, 3), "gear", 0.0, 9.9, false);

            var surge = new SpellsurgeAbility().GetReadout(null, 3);
            Pin(surge, "stacks", 0.0, 5.0, false);
            Pin(surge, "gear", 0.0, 1.53, false);

            Pin(new ResonanceAbility().GetReadout(null, 3), "stacks", 0.0, 5.0, false);
        }

        [TestMethod]
        public void BloodCharge_ReadsTheReserveRankCap_AndNullPlayerHoldsNone()
        {
            // the cap per rank is 3 / 4 / 5
            Pin(new SanguineReserveAbility().GetReadout(null, 1), "stacks", 0.0, 3.0, false);
            Pin(new SanguineReserveAbility().GetReadout(null, 3), "stacks", 0.0, 5.0, false);
            // the static ceiling is the Reserve top-rank cap (5), whatever the Exsanguinate rank
            Pin(new ExsanguinateAbility().GetReadout(null, 2), "stacks", 0.0, 5.0, false);
        }

        [TestMethod]
        public void WeakenedBlood_Current_IsTheRawSlowBeforeTheFloor()
        {
            // rank 3 slow 0.30 at the neutral multiplier, against a floor that allows 0.50
            Pin(new WeakenedBloodAbility().GetReadout(null, 3), "slowfloor", 30.0, 50.0, false);
        }

        private static ITunableValues With(string key, double value) =>
            new DictionaryTunableValues(new Dictionary<string, double> { { key, value } });

        [TestMethod]
        public void WeakenedBlood_SlowCeiling_ReadsThroughTheTunablesItIsRenderedWith()
        {
            var c = Ceiling(ClassAbilityId.WeakenedBlood, "slowfloor");
            Assert.AreEqual(0.50, c.Value(new DictionaryTunableValues(new Dictionary<string, double>()), 3), 1e-12, "defaults");
            Assert.AreEqual(0.60, c.Value(With("class_ability_weakenedblood_slow_floor", 0.40), 3), 1e-12, "a 0.40 floor allows a 0.60 slow, whatever the live value is");
            Assert.AreEqual("max attack and cast slow", c.Label);
        }

        [TestMethod]
        public void Sundermark_LevelCeilings_AreTheClampedTunables()
        {
            foreach (var (id, key) in new[] { ("level", "class_ability_sundermark_max_level"), ("level_nether", "class_ability_sundermark_nether_max_level") })
            {
                var c = Ceiling(ClassAbilityId.Sundermark, id);
                Assert.AreEqual(8.0, c.Value(With(key, 12), 3), id + " tunable 12 clamps to 8");
                Assert.AreEqual(1.0, c.Value(With(key, 0), 3), id + " tunable 0 clamps to 1");
                Assert.AreEqual(5.0, c.Value(With(key, 5), 3), id + " tunable 5 stays 5");
                Assert.AreEqual(8.0, c.Value(new DictionaryTunableValues(new Dictionary<string, double>()), 3), id + " defaults");
            }
        }

        [TestMethod]
        public void Exsanguinate_StaticCeilingIsReservesTopRank_LiveReadingIsRankExact()
        {
            var c = Ceiling(ClassAbilityId.Exsanguinate, "stacks");
            Assert.AreEqual(5.0, c.Value(new DictionaryTunableValues(new Dictionary<string, double>()), 1), "top rank cap whatever rank it is asked at");
            Assert.AreEqual(7.0, c.Value(With("class_ability_bloodmage_charge_stack_cap_r3", 7), 1), "reads through the values it is rendered with");

            var live = ClassAbilityText.Live;
            var r1 = ExsanguinateAbility.StacksReading(1, 3, live);
            Assert.AreEqual(3.0, r1.Max, 1e-9, "Reserve rank 1 caps at 3");
            Assert.IsTrue(r1.Binding);
            var r3 = ExsanguinateAbility.StacksReading(3, 3, live);
            Assert.AreEqual(5.0, r3.Max, 1e-9, "Reserve rank 3 caps at 5");
            Assert.IsFalse(r3.Binding);
            var none = ExsanguinateAbility.StacksReading(0, 0, live);
            Assert.AreEqual(0.0, none.Max, 1e-9, "no Reserve, no pool");
            Assert.IsFalse(none.Binding);
        }

        [TestMethod]
        public void RunicWard_TotalReading_IsThePercentOfMaxHealthHeld()
        {
            var a = new RunicWardAbility();
            // rank 3 cap 0.30 of max health; a ward at 12% is not at the limit, at 30% it is
            var under = a.BuildReadout(3, 1.0, 12.0);
            Pin(under, "total", 12.0, 30.0, false);
            var full = a.BuildReadout(3, 1.0, 30.0);
            Pin(full, "total", 30.0, 30.0, true);
            // rank 1 caps at 10%
            Pin(a.BuildReadout(1, 1.0, 10.0), "total", 10.0, 10.0, true);
        }

        private static ClassAbilityCeiling Ceiling(ClassAbilityId id, string ceilingId) =>
            ClassAbilityRegistry.Abilities[id].Ceilings.First(c => c.Id == ceilingId);
    }
}
