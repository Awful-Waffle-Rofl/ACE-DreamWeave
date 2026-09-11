using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the pure part of the Drift Network trainer front end: parsing a trainer weenie's
    /// ClassAbilityTrainerAbilities string (comma/semicolon list of ClassAbilityId names) into skill definitions.
    /// The live buy/refund interaction (ClassAbilityTrainer.TryHandleUse) needs a Player + landblock and is
    /// exercised in-game via the trainer NPCs and /cavoucher.
    /// </summary>
    [TestClass]
    public class ClassAbilityTrainerParseTests
    {
        [TestMethod]
        public void Parse_NullOrBlank_ReturnsEmpty()
        {
            Assert.AreEqual(0, ClassAbilityTrainer.ParseTrainerAbilities(null).Count);
            Assert.AreEqual(0, ClassAbilityTrainer.ParseTrainerAbilities("").Count);
            Assert.AreEqual(0, ClassAbilityTrainer.ParseTrainerAbilities("   ").Count);
        }

        [TestMethod]
        public void Parse_ResolvesKnownNames_InListedOrder()
        {
            var skills = ClassAbilityTrainer.ParseTrainerAbilities("multishot,frenzy");

            CollectionAssert.AreEqual(
                new[] { ClassAbilityId.Multishot, ClassAbilityId.Frenzy },
                skills.Select(s => s.Id).ToArray());
        }

        [TestMethod]
        public void Parse_SkipsUnknownNames()
        {
            var skills = ClassAbilityTrainer.ParseTrainerAbilities("multishot,notaskill,thorns");

            CollectionAssert.AreEqual(
                new[] { ClassAbilityId.Multishot, ClassAbilityId.Thorns },
                skills.Select(s => s.Id).ToArray());
        }

        [TestMethod]
        public void Parse_IsCaseInsensitive_AndDeduplicates()
        {
            var skills = ClassAbilityTrainer.ParseTrainerAbilities("Multishot, multishot, MULTISHOT");

            Assert.AreEqual(1, skills.Count);
            Assert.AreEqual(ClassAbilityId.Multishot, skills[0].Id);
        }

        [TestMethod]
        public void Parse_ToleratesSemicolonsBlanksAndWhitespace()
        {
            var skills = ClassAbilityTrainer.ParseTrainerAbilities("  multishot ;; , thorns , ");

            CollectionAssert.AreEqual(
                new[] { ClassAbilityId.Multishot, ClassAbilityId.Thorns },
                skills.Select(s => s.Id).ToArray());
        }

        /// <summary>
        /// Guards the six Drift Network trainers (Content/realms/driftnetwork_hub.sql) against the parser's
        /// silent-skip trap: a typo'd token in a trainer's ClassAbilityTrainerAbilities string resolves to
        /// nothing, with no error at load or runtime, so an understocked trainer is otherwise invisible. Each
        /// string here is the exact PropertyString.ClassAbilityTrainerAbilities (type 9008) value committed for
        /// that trainer's wcid, derived from ClassAbilityTokenCatalog.TokenAbilities filtered by AbilityClass.
        /// </summary>
        [TestMethod]
        public void Parse_ArcherTrainer_ResolvesTenArcherAbilities()
        {
            const string trainerAbilities = "multishot,archer_training,enhanced_missileweapons,enhanced_coordination,deadeye,eagleeye,heavydraw,crit_rating,doublevolley,longdraw";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            Assert.AreEqual(10, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Archer));
        }

        /// <summary>
        /// ELEVEN, not nine, since 2026-08-17: the Berserker/Rogue balance pass adds Surefooted (T2) and
        /// Pocket Sand (T3) to this trainer's stocked list.
        /// </summary>
        [TestMethod]
        public void Parse_RogueTrainer_ResolvesElevenRogueAbilities()
        {
            const string trainerAbilities = "poisonweapon,rogue_training,enhanced_finesseweapons,enhanced_quickness,parry,riposte,surefooted,crit_damage_rating,attackspeed,acidproc,pocketsand";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            Assert.AreEqual(11, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Rogue));
        }

        [TestMethod]
        public void Parse_VanguardTrainer_ResolvesElevenVanguardAbilities()
        {
            const string trainerAbilities = "thorns,taunt,battlehardened,vanguard_training,enhanced_heavyweapons,enhanced_endurance,damage_resist_rating,shieldblock,shieldcheck,crit_resist_rating,enhanced_health";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            Assert.AreEqual(11, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Vanguard));
        }

        /// <summary>
        /// ELEVEN again, since 2026-08-17: Blood Fury was retired and dropped from this trainer's stocked
        /// list, then the Berserker/Rogue balance pass added Break Armor in its place.
        /// </summary>
        [TestMethod]
        public void Parse_BerserkerTrainer_ResolvesElevenBerserkerAbilities()
        {
            const string trainerAbilities = "frenzy,berserker_training,enhanced_twohandedcombat,enhanced_strength,savageblows,breakarmor,executioner,enhanced_stamina,damage_rating,whirlwind,bloodlust";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            Assert.AreEqual(11, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Berserker));
        }

        /// <summary>
        /// The Archmage trainer string below is the exact value committed for that NPC. Enhanced Life Magic
        /// moved to Blood Mage T1 on 2026-08-03 and the concurrent Archmage-trainer session has since
        /// dropped it from this string, so every ability it stocks is once again an Archmage ability.
        /// </summary>
        [TestMethod]
        public void Parse_ArchmageTrainer_ResolvesElevenArchmageAbilities()
        {
            const string trainerAbilities = "spellaoe,archmage_training,enhanced_warmagic,enhanced_focus,enhanced_mana,flatcastspeed,overchannel,enhanced_magicdefense,manabarrier,echocast,elementalrend";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            Assert.AreEqual(11, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Archmage));
        }

        [TestMethod]
        public void Parse_VoidSummonTrainer_ResolvesElevenVoidSummonAbilities()
        {
            const string trainerAbilities = "netherrush,void_training,enhanced_voidmagic,enhanced_self,empoweredsummons,enhanced_summoning,summon2x,voiddamage,withering,netherbloom,soultether";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            Assert.AreEqual(11, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.VoidSummon));
        }

        /// <summary>
        /// Catches a future ability added to TokenAbilities for one of the eight trainer-bearing classes but
        /// never added to any trainer's stocked list above - the coverage half of the silent-skip guard.
        ///
        /// NOTE THE FAILURE MODE OF THIS TEST ITSELF: trainerClasses and trainerLists are FIXED ARRAYS, not
        /// derived from the ClassAbilityClass enum, so adding a NINTH class without extending both arrays
        /// leaves this test passing while covering nothing of that class. That is a silent false negative,
        /// and it is the reason both arrays carry a per-class comment naming the weenie file they came from.
        /// </summary>
        [TestMethod]
        public void Parse_EveryTokenAbilityForTheEightClasses_AppearsInExactlyOneTrainerList()
        {
            var trainerClasses = new[]
            {
                ClassAbilityClass.Archer,
                ClassAbilityClass.Rogue,
                ClassAbilityClass.Vanguard,
                ClassAbilityClass.Berserker,
                ClassAbilityClass.Archmage,
                ClassAbilityClass.VoidSummon,
                ClassAbilityClass.BloodMage,
                ClassAbilityClass.Spellsword,
            };

            var trainerLists = new[]
            {
                "multishot,archer_training,enhanced_missileweapons,enhanced_coordination,deadeye,eagleeye,heavydraw,crit_rating,doublevolley,longdraw",
                "poisonweapon,rogue_training,enhanced_finesseweapons,enhanced_quickness,parry,riposte,surefooted,crit_damage_rating,attackspeed,acidproc,pocketsand",
                "thorns,taunt,battlehardened,vanguard_training,enhanced_heavyweapons,enhanced_endurance,damage_resist_rating,shieldblock,shieldcheck,crit_resist_rating,enhanced_health",
                "frenzy,berserker_training,enhanced_twohandedcombat,enhanced_strength,savageblows,breakarmor,executioner,enhanced_stamina,damage_rating,whirlwind,bloodlust",
                "spellaoe,archmage_training,enhanced_warmagic,enhanced_focus,enhanced_mana,flatcastspeed,overchannel,enhanced_magicdefense,manabarrier,echocast,elementalrend",
                "netherrush,void_training,enhanced_voidmagic,enhanced_self,empoweredsummons,enhanced_summoning,summon2x,voiddamage,withering,netherbloom,soultether",
                // Blood Mage trainer (Content/sql/weenies/1001950_npc_bloodmage_trainer.sql), added 2026-08-03.
                // Verbatim copy of that weenie's PropertyString.ClassAbilityTrainerAbilities (9008) value.
                "sanguine_reserve,enhanced_lifemagic,transfusion,bloodmage_training,weakened_blood,malediction,crimson_harvest,heal_boost_rating,exsanguinate,blood_price,sanguine_ward",
                // Spellsword trainer (Content/sql/weenies/1001960_npc_spellsword_trainer.sql), added
                // 2026-08-03. Verbatim copy of that weenie's PropertyString.ClassAbilityTrainerAbilities
                // (9008) value. Note enhanced_meleedefense moves here from nobody - Phase 1 (PR #459) left
                // it unhomed after taking it off Rogue, so this is its first trainer, not a double-stock.
                "spellblade,enhanced_lightweapons,resonance,spellsword_training,runeblade,enhanced_meleedefense,sundermark,spellsurge,spellstorm,cascade,dispellingedge",
            };

            var stockedIds = trainerLists
                .SelectMany(list => ClassAbilityTrainer.ParseTrainerAbilities(list))
                .Select(s => s.Id)
                .ToList();

            // The enhanced_lifemagic double-stock tolerated here until 2026-08-03 is GONE: the concurrent
            // Archmage-trainer session dropped it from the Archmage string, so Enhanced Life Magic is now
            // stocked only by the Blood Mage trainer, matching its (BloodMage, 1) homing. Back to the plain
            // no-duplicates assertion - if this fails again, two trainers are stocking the same ability.
            var distinctStockedIds = stockedIds.Distinct().ToList();
            Assert.AreEqual(stockedIds.Count, distinctStockedIds.Count, "no ability id should be stocked by more than one trainer");

            var expectedIds = ClassAbilityTokenCatalog.TokenAbilities
                .Where(id => ClassAbilityRegistry.Abilities.TryGetValue(id, out var def) && trainerClasses.Contains(def.AbilityClass))
                .ToList();

            CollectionAssert.AreEquivalent(expectedIds, distinctStockedIds);
        }
    }
}
