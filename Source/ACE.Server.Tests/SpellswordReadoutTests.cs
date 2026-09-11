using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the /abilities readout (IAbilityReadout.GetReadout) on the Spellsword handlers that can
    /// be reached from this test host.
    ///
    /// FOUR OF THE EIGHT NEVER TOUCH THE `player` PARAMETER except through a null-conditional gear read -
    /// Resonance, Spellsurge, Spellstorm and Cascade all resolve otherwise from rank plus PropertyManager
    /// tunables - so those four are called here with a null Player. That is what these tests are FOR: the
    /// gear read added for mods 40, 43, 44 and 45 must be `player?.GetEquippedModValue(...) ?? 0.0`, and an
    /// unguarded read would take every assertion below down with a NullReferenceException.
    ///
    /// SKIPPED, and why: SpellbladeAbility, RunebladeAbility, SundermarkAbility and DispellingEdgeAbility
    /// each call player.GetClassAbilityScaling for their affinity rider BEFORE reaching the gear read, and
    /// TauntAbility.GetReadout does the same for its Loyalty rider. Player's static initializer cannot run
    /// under this test host (project constraint - see CLAUDE.md's "Player statics untestable" note), so
    /// those five cannot be invoked here at all, with or without the gear term. Their arithmetic is covered
    /// through their pure Chance()/EffectiveDuration() helpers in SpellswordVanguardEquipmentModTests.
    /// </summary>
    [TestClass]
    public class SpellswordReadoutTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- Resonance (gear: Harmonics) -------------------------------------------------------------

        [TestMethod]
        public void Resonance_Readout_ReportsPerStackMagicDamageAndSurvivesANullPlayer()
        {
            var ability = new ResonanceAbility();

            var readout = ability.GetReadout(null, 3);

            Assert.IsTrue(readout.HasValue);
            Assert.AreEqual(4.0, readout.Skill, 1e-9); // class_ability_resonance_per_stack_r3 default 0.04
            Assert.AreEqual(0.0, readout.Affinity, 1e-9);
            Assert.AreEqual(0.0, readout.Gear, 1e-9, "no Player, therefore no equipped items, therefore no gear");
            Assert.AreEqual(readout.Skill, readout.Effective, 1e-9);
            Assert.AreEqual("%", readout.Unit);
            Assert.AreEqual("magic dmg", readout.Label);
            Assert.AreEqual("/stack", readout.Per);
            Assert.IsFalse(readout.Capped);
        }

        [TestMethod]
        public void Resonance_Readout_TracksTheRankLadder()
        {
            var ability = new ResonanceAbility();

            Assert.AreEqual(2.0, ability.GetReadout(null, 1).Skill, 1e-9);
            Assert.AreEqual(3.0, ability.GetReadout(null, 2).Skill, 1e-9);
            Assert.AreEqual(4.0, ability.GetReadout(null, 3).Skill, 1e-9);

            // unowned reports nothing on either column
            var unowned = ability.GetReadout(null, 0);
            Assert.AreEqual(0.0, unowned.Skill, 1e-9);
            Assert.AreEqual(0.0, unowned.Gear, 1e-9);
        }

        // ---- Spellsurge (gear: Surge) ----------------------------------------------------------------

        [TestMethod]
        public void Spellsurge_Readout_ReportsPerStackProcChanceAndSurvivesANullPlayer()
        {
            var ability = new SpellsurgeAbility();

            var readout = ability.GetReadout(null, 1);

            Assert.IsTrue(readout.HasValue);
            Assert.AreEqual(2.0, readout.Skill, 1e-9); // class_ability_spellsurge_per_stack default 0.02
            Assert.AreEqual(0.0, readout.Affinity, 1e-9);
            Assert.AreEqual(0.0, readout.Gear, 1e-9);
            Assert.AreEqual(readout.Skill, readout.Effective, 1e-9);
            Assert.AreEqual("%", readout.Unit);
            Assert.AreEqual("proc chance", readout.Label);
            Assert.AreEqual("/stack", readout.Per);
            Assert.IsFalse(readout.Capped);
        }

        [TestMethod]
        public void Spellsurge_Readout_IsSilentWhenUnowned()
        {
            var unowned = new SpellsurgeAbility().GetReadout(null, 0);

            Assert.AreEqual(0.0, unowned.Skill, 1e-9);
            Assert.AreEqual(0.0, unowned.Gear, 1e-9, "a machinery mod reports nothing without its ability");
            Assert.AreEqual(0.0, unowned.Effective, 1e-9);
        }

        // ---- Spellstorm (gear: Spellstorm) -----------------------------------------------------------

        [TestMethod]
        public void Spellstorm_Readout_ReportsTheFlatProcChanceAndSurvivesANullPlayer()
        {
            var ability = new SpellstormAbility();

            var readout = ability.GetReadout(null, 1);

            Assert.IsTrue(readout.HasValue);
            Assert.AreEqual(10.0, readout.Skill, 1e-9); // class_ability_spellstorm_chance default 0.10
            Assert.AreEqual(0.0, readout.Affinity, 1e-9, "this entry carries no affinity rider by design");
            Assert.AreEqual(0.0, readout.Gear, 1e-9);
            Assert.AreEqual(readout.Skill, readout.Effective, 1e-9);
            Assert.AreEqual("%", readout.Unit);
            Assert.AreEqual("proc", readout.Label);
            Assert.IsNull(readout.Per);
            Assert.IsFalse(readout.Capped, "no rider means no cap can ever bite");
        }

        [TestMethod]
        public void Spellstorm_Readout_IsSilentWhenUnowned()
        {
            var unowned = new SpellstormAbility().GetReadout(null, 0);

            Assert.AreEqual(0.0, unowned.Skill, 1e-9);
            Assert.AreEqual(0.0, unowned.Effective, 1e-9);
        }

        // ---- Cascade (gear: Cascade) -----------------------------------------------------------------

        [TestMethod]
        public void Cascade_Readout_ReportsTheChainChanceAndSurvivesANullPlayer()
        {
            var ability = new CascadeAbility();

            var readout = ability.GetReadout(null, 1);

            Assert.IsTrue(readout.HasValue);
            Assert.AreEqual(25.0, readout.Skill, 1e-9); // class_ability_cascade_chance default 0.25
            Assert.AreEqual(0.0, readout.Affinity, 1e-9);
            Assert.AreEqual(0.0, readout.Gear, 1e-9);
            Assert.AreEqual(readout.Skill, readout.Effective, 1e-9);
            Assert.AreEqual("%", readout.Unit);
            Assert.AreEqual("chain", readout.Label);
            Assert.IsNull(readout.Per);
            Assert.IsFalse(readout.Capped);
        }

        [TestMethod]
        public void Cascade_Readout_IsSilentWhenUnowned()
        {
            var unowned = new CascadeAbility().GetReadout(null, 0);

            Assert.AreEqual(0.0, unowned.Skill, 1e-9);
            Assert.AreEqual(0.0, unowned.Gear, 1e-9, "a machinery mod reports nothing without its ability");
            Assert.AreEqual(0.0, unowned.Effective, 1e-9);
        }
    }
}
