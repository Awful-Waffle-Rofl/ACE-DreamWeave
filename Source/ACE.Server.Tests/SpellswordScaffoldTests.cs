using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the Spellsword (8th class) scaffold: the ClassAbilityId/ClassAbilityClass enum
    /// additions and the registered tunable defaults for all nine Spellsword entries (eight bespoke
    /// handlers plus the generated SpellswordTraining bundle - see ClassAbilityRegistry.cs's Phase 13
    /// comment). Mirrors BloodMageScaffoldTests.cs's structure for the 7th class.
    ///
    /// Every default value asserted below was read from PropertyManager.cs at write time, not copied
    /// from the design doc, so this test catches the mirror between doc and registered default drifting
    /// apart, not just a typo relative to the doc.
    /// </summary>
    [TestClass]
    public class SpellswordScaffoldTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;
        private static long L(string key) => PropertyManager.GetLong(key).Item;

        // ---- enum additions --------------------------------------------------------------------

        [TestMethod]
        public void ClassAbilityClass_Spellsword_IsAppendedAfterBloodMage()
        {
            Assert.AreEqual(7, (int)ClassAbilityClass.BloodMage);
            Assert.AreEqual(8, (int)ClassAbilityClass.Spellsword);
        }

        [TestMethod]
        public void ClassAbilityId_SpellswordSkills_AreAppendedAfterSanguineWardWithDistinctIds()
        {
            Assert.AreEqual(61, (int)ClassAbilityId.SanguineWard);

            Assert.AreEqual(62, (int)ClassAbilityId.Spellblade);
            Assert.AreEqual(63, (int)ClassAbilityId.Resonance);
            Assert.AreEqual(64, (int)ClassAbilityId.SpellswordTraining);
            Assert.AreEqual(65, (int)ClassAbilityId.Runeblade);
            Assert.AreEqual(66, (int)ClassAbilityId.Sundermark);
            Assert.AreEqual(67, (int)ClassAbilityId.Spellsurge);
            Assert.AreEqual(68, (int)ClassAbilityId.Spellstorm);
            Assert.AreEqual(69, (int)ClassAbilityId.Cascade);
            Assert.AreEqual(70, (int)ClassAbilityId.DispellingEdge);

            var ids = new[]
            {
                ClassAbilityId.SanguineWard,
                ClassAbilityId.Spellblade,
                ClassAbilityId.Resonance,
                ClassAbilityId.SpellswordTraining,
                ClassAbilityId.Runeblade,
                ClassAbilityId.Sundermark,
                ClassAbilityId.Spellsurge,
                ClassAbilityId.Spellstorm,
                ClassAbilityId.Cascade,
                ClassAbilityId.DispellingEdge,
            };

            // every one of the nine new ids must be distinct from each other and from the last Blood Mage id
            CollectionAssert.AllItemsAreUnique(ids);
        }

        [TestMethod]
        public void ClassAbilityId_ExistingValues_AreUnchanged()
        {
            // append-only guard: renumbering any pre-existing id would silently corrupt persisted
            // CharacterPropertiesQuestRegistry rows (ClassAbility_<name>) on every live shard
            Assert.AreEqual(8, (int)ClassAbilityId.Frenzy);
            Assert.AreEqual(9, (int)ClassAbilityId.NetherRush);
            Assert.AreEqual(48, (int)ClassAbilityId.ManaBarrier);
            Assert.AreEqual(51, (int)ClassAbilityId.SanguineReserve);
            Assert.AreEqual(61, (int)ClassAbilityId.SanguineWard);
        }

        // ---- registration ------------------------------------------------------------------------

        [TestMethod]
        public void AllNineSpellswordIds_ResolveToADefinitionOfSpellswordClass()
        {
            var ids = new[]
            {
                ClassAbilityId.Spellblade,
                ClassAbilityId.Resonance,
                ClassAbilityId.SpellswordTraining,
                ClassAbilityId.Runeblade,
                ClassAbilityId.Sundermark,
                ClassAbilityId.Spellsurge,
                ClassAbilityId.Spellstorm,
                ClassAbilityId.Cascade,
                ClassAbilityId.DispellingEdge,
            };

            foreach (var id in ids)
            {
                Assert.IsTrue(ClassAbilityRegistry.Abilities.ContainsKey(id), $"{id} is not registered");

                var def = ClassAbilityRegistry.Abilities[id];

                Assert.AreEqual(ClassAbilityClass.Spellsword, def.AbilityClass, $"{id}: wrong class");
            }
        }

        // ---- tunables --------------------------------------------------------------------------

        [TestMethod]
        public void SpellbladeTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.20, D("class_ability_spellblade_chance"), 1e-9);
            Assert.AreEqual(25.0, D("class_ability_spellblade_itemench_per_trained"), 1e-9);
            Assert.AreEqual(18.0, D("class_ability_spellblade_itemench_per_spec"), 1e-9);
        }

        [TestMethod]
        public void RunebladeTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.15, D("class_ability_runeblade_chance"), 1e-9);
            Assert.AreEqual(25.0, D("class_ability_runeblade_magicitemtink_per_trained"), 1e-9);
            Assert.AreEqual(18.0, D("class_ability_runeblade_magicitemtink_per_spec"), 1e-9);
        }

        [TestMethod]
        public void SpellstormTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.10, D("class_ability_spellstorm_chance"), 1e-9);
        }

        [TestMethod]
        public void SundermarkTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.05, D("class_ability_sundermark_chance_base"), 1e-9);
            Assert.AreEqual(0.05, D("class_ability_sundermark_chance_step"), 1e-9);
            Assert.AreEqual(25.0, D("class_ability_sundermark_lifemagic_per_trained"), 1e-9);
            Assert.AreEqual(18.0, D("class_ability_sundermark_lifemagic_per_spec"), 1e-9);
            Assert.AreEqual(8, L("class_ability_sundermark_max_level"));
        }

        [TestMethod]
        public void SpellswordSharedTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            // the safety valve on SpellswordSpellTables.MinPowerForLevel - see SPELLSWORD-DESIGN.md sec 1d
            Assert.AreEqual(1.0, D("class_ability_spellsword_level_skill_scale"), 1e-9);
        }

        [TestMethod]
        public void ResonanceTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(5, L("class_ability_resonance_max_stacks"));
            Assert.AreEqual(0.02, D("class_ability_resonance_per_stack_r1"), 1e-9);
            Assert.AreEqual(0.03, D("class_ability_resonance_per_stack_r2"), 1e-9);
            Assert.AreEqual(0.04, D("class_ability_resonance_per_stack_r3"), 1e-9);
            Assert.AreEqual(6.0, D("class_ability_resonance_window_seconds"), 1e-9);
        }

        [TestMethod]
        public void SpellsurgeTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(5, L("class_ability_spellsurge_max_stacks"));
            Assert.AreEqual(0.02, D("class_ability_spellsurge_per_stack"), 1e-9);
            Assert.AreEqual(10.0, D("class_ability_spellsurge_window_seconds"), 1e-9);
        }

        [TestMethod]
        public void CascadeTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.25, D("class_ability_cascade_chance"), 1e-9);
            Assert.AreEqual(8.0, D("class_ability_cascade_radius"), 1e-9);
        }

        /// <summary>
        /// class_ability_cascade_max_generation is asserted on its own, called out explicitly: it is the
        /// ONLY thing that bounds Cascade's chain (CascadeAbility.CanCascade compares
        /// projectile.ClassAbilityGeneration against this value). If this key were ever missing,
        /// PropertyManager.GetLong would silently return 0 rather than throwing, and
        /// CanCascade(true, 0, 0) evaluates "0 &lt; 0" = false - which happens to fail closed for THIS
        /// call, but a missing tunable is still a silent misconfiguration with no error anywhere, so it
        /// is worth pinning by name rather than trusting it stays present by accident.
        /// </summary>
        [TestMethod]
        public void CascadeMaxGeneration_IsRegistered_TheSoleGuardBoundingTheChain()
        {
            Assert.AreEqual(1, L("class_ability_cascade_max_generation"));
        }

        [TestMethod]
        public void DispellingEdgeTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.10, D("class_ability_dispellingedge_chance_base"), 1e-9);
            Assert.AreEqual(0.05, D("class_ability_dispellingedge_chance_step"), 1e-9);
            Assert.AreEqual(25.0, D("class_ability_dispellingedge_arcanelore_per_trained"), 1e-9);
            Assert.AreEqual(18.0, D("class_ability_dispellingedge_arcanelore_per_spec"), 1e-9);
        }
    }
}
