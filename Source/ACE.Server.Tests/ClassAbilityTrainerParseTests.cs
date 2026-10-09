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
        public void Parse_ArcherTrainer_ResolvesElevenArcherAbilities()
        {
            const string trainerAbilities = "multishot,archer_training,enhanced_missileweapons,enhanced_coordination,hunters_mark,deadeye,fastaim,pinning_shot,crit_rating,doublevolley,longdraw";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            // ELEVEN, not nine, since 2026-09-12: the class ability overhaul's new-ability wave adds
            // Hunter's Mark (T1) and Pinning Shot (T2, the slot Heavy Draw vacated). enhanced_missiledefense
            // was RETIRED 2026-09-16 (Missile Defense is owned solely by Archer Training) and removed from
            // the trainer's committed 9008 value along with it, so this string is once again byte-identical
            // to that value. eagleeye -> fastaim 2026-09-29 (ability id 24 redefined in place).
            Assert.AreEqual(11, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Archer));
        }

        /// <summary>
        /// ELEVEN, not nine, since 2026-08-17: the Berserker/Rogue balance pass adds Surefooted (T2) and
        /// Pocket Sand (T3) to this trainer's stocked list.
        /// </summary>
        [TestMethod]
        public void Parse_RogueTrainer_ResolvesTwelveRogueAbilities()
        {
            const string trainerAbilities = "poisonweapon,rogue_training,enhanced_finesseweapons,enhanced_quickness,opportunist,parry,riposte,crit_damage_rating,attackspeed,acidproc,killer_instinct,pocketsand";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            // TWELVE, not ten, since 2026-09-12: the class ability overhaul's new-ability wave adds
            // Opportunist (T1) and Killer Instinct (T3).
            Assert.AreEqual(12, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Rogue));
        }

        [TestMethod]
        public void Parse_VanguardTrainer_ResolvesFourteenVanguardAbilities()
        {
            const string trainerAbilities = "thorns,taunt,battlehardened,vanguard_training,enhanced_heavyweapons,enhanced_endurance,rallying_presence,damage_resist_rating,shieldblock,kinetic_charge,crit_resist_rating,enhanced_health,reflect_magic,cloaked_in_power";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            // THIRTEEN since 2026-09-12: the class ability overhaul's new-ability wave added Rallying Presence
            // (T1), Kinetic Charge (entered at T2, the slot Shield Check vacated) and Reflect (T3, canonical
            // token name reflect_magic). FOURTEEN since 2026-09-29: cloaked_in_power (Vanguard T2). Kinetic
            // Charge moved T2 -> T1 the same day, which does not change this count - the 9008 list is
            // tier-agnostic.
            Assert.AreEqual(14, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Vanguard));
        }

        /// <summary>
        /// ELEVEN again, since 2026-08-17: Blood Fury was retired and dropped from this trainer's stocked
        /// list, then the Berserker/Rogue balance pass added Break Armor in its place.
        /// </summary>
        [TestMethod]
        public void Parse_BerserkerTrainer_ResolvesThirteenBerserkerAbilities()
        {
            const string trainerAbilities = "frenzy,berserker_training,enhanced_twohandedcombat,enhanced_strength,adrenaline,savageblows,breakarmor,executioner,enhanced_stamina,vengeance,damage_rating,whirlwind,bloodlust";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            // THIRTEEN, not twelve, since 2026-09-14: Enhanced Stamina was re-homed to Berserker T1 (owner
            // ruling - it had been UN-HOMED by the 2026-09-12 overhaul, still registered and stocked but
            // excluded from the class-filtered list, the way an unhomed Enhanced entry is). It is now
            // included here because it counts as a Berserker ability again.
            Assert.AreEqual(13, skills.Count);
            Assert.IsTrue(skills.All(s => s.AbilityClass == ClassAbilityClass.Berserker));
        }

        /// <summary>
        /// The Archmage trainer string below is the exact value committed for that NPC. Enhanced Life Magic
        /// moved to Blood Mage T1 on 2026-08-03 and the concurrent Archmage-trainer session has since
        /// dropped it from this string, so every ability it stocks is once again an Archmage ability.
        /// </summary>
        [TestMethod]
        public void Parse_ArchmageTrainer_ResolvesTwelveArchmageAbilities()
        {
            const string trainerAbilities = "spellaoe,archmage_training,enhanced_warmagic,enhanced_focus,quickened_casting,enhanced_mana,flatcastspeed,overchannel,enhanced_magicdefense,manabarrier,echocast,elementalrend";

            var skills = ClassAbilityTrainer.ParseTrainerAbilities(trainerAbilities);

            // TWELVE, not eleven, since 2026-09-14: Enhanced Mana was re-homed to Archmage T1 (owner ruling -
            // it had been UN-HOMED by the 2026-09-12 overhaul, still registered and stocked - see the
            // Berserker note). It is now included here because it counts as an Archmage entry again, alongside
            // quickened_casting (added 2026-09-12, new-ability wave), which this string had been missing.
            Assert.AreEqual(12, skills.Count);
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
                // Archer trainer (Content/realms/driftnetwork_hub.sql, NPC 1001000). hunters_mark and
                // pinning_shot added 2026-09-12 (class ability overhaul, new-ability wave).
                "multishot,archer_training,enhanced_missileweapons,enhanced_coordination,hunters_mark,deadeye,fastaim,pinning_shot,crit_rating,doublevolley,longdraw",
                // Rogue trainer (Content/realms/driftnetwork_hub.sql, NPC 1001001). Verbatim copy of that
                // weenie's PropertyString.ClassAbilityTrainerAbilities (9008) value. enhanced_meleedefense
                // was added back here 2026-09-12 when the class ability overhaul returned it from Spellsword
                // to Rogue T2; opportunist and killer_instinct added the same day (new-ability wave).
                "poisonweapon,rogue_training,enhanced_finesseweapons,enhanced_quickness,opportunist,parry,riposte,enhanced_meleedefense,crit_damage_rating,attackspeed,acidproc,killer_instinct,pocketsand",
                // Vanguard trainer (NPC 1001002). rallying_presence, kinetic_charge and reflect_magic added
                // 2026-09-12 (new-ability wave); cloaked_in_power added 2026-09-29 (Vanguard T2).
                "thorns,taunt,battlehardened,vanguard_training,enhanced_heavyweapons,enhanced_endurance,rallying_presence,damage_resist_rating,shieldblock,kinetic_charge,crit_resist_rating,enhanced_health,reflect_magic,cloaked_in_power",
                // Berserker trainer (NPC 1001003). adrenaline and vengeance added 2026-09-12 (new-ability
                // wave); enhanced_stamina re-homed here 2026-09-14 (owner ruling).
                "frenzy,berserker_training,enhanced_twohandedcombat,enhanced_strength,adrenaline,savageblows,breakarmor,executioner,enhanced_stamina,vengeance,damage_rating,whirlwind,bloodlust",
                // Archmage trainer (NPC 1001004). quickened_casting added 2026-09-12 (new-ability wave);
                // enhanced_mana re-homed here 2026-09-14 (owner ruling).
                "spellaoe,archmage_training,enhanced_warmagic,enhanced_focus,enhanced_mana,quickened_casting,flatcastspeed,overchannel,enhanced_magicdefense,manabarrier,echocast,elementalrend",
                // VoidSummon trainer (NPC 1001005). umbral_siphon and soul_jump added 2026-09-12 (new-ability
                // wave).
                "netherrush,void_training,enhanced_voidmagic,enhanced_self,umbral_siphon,empoweredsummons,enhanced_summoning,summon2x,voiddamage,withering,soul_jump,netherbloom,soultether",
                // Blood Mage trainer (Content/sql/weenies/1001950_npc_bloodmage_trainer.sql), added 2026-08-03.
                // Verbatim copy of that weenie's PropertyString.ClassAbilityTrainerAbilities (9008) value.
                // hemomancy added 2026-09-12 (new-ability wave).
                "sanguine_reserve,enhanced_lifemagic,transfusion,bloodmage_training,hemomancy,weakened_blood,malediction,crimson_harvest,heal_boost_rating,exsanguinate,blood_price,sanguine_ward",
                // Spellsword trainer (Content/sql/weenies/1001960_npc_spellsword_trainer.sql), added
                // 2026-08-03. Verbatim copy of that weenie's PropertyString.ClassAbilityTrainerAbilities
                // (9008) value. enhanced_meleedefense moved back to Rogue T2 2026-09-12 in the class
                // ability overhaul, so it no longer appears here. spellweave and runic_ward added the same
                // day (new-ability wave).
                "spellblade,enhanced_lightweapons,resonance,spellsword_training,spellweave,runeblade,sundermark,spellsurge,runic_ward,spellstorm,cascade,dispellingedge",
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
