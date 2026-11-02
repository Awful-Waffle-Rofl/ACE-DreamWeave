using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The summon-source seam (Custom Summons Task 1): PetSummonSource.FromDevice, PetDevice.GetSummonSources
    /// and CombatPet.ApplySummonStats. Everything here is pure - no Player, no world, no PropertyManager
    /// read (the Empowered Summons multipliers arrive as plain arguments), so no tunable needs seeding.
    ///
    /// The retail-parity case holds ApplySummonStats against a verbatim copy of the pre-seam CombatPet.Init
    /// stat block, so "a retail device comes out identical" is checked against the old arithmetic rather
    /// than restated from the new code.
    /// </summary>
    [TestClass]
    public class PetSummonSourceTests
    {
        private static int nextGuid = 0x7F5A0000;
        private static uint nextWcid = 995000;

        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        private static ObjectGuid NextGuid() => new ObjectGuid((uint)System.Threading.Interlocked.Increment(ref nextGuid));

        private static PetDevice MakeDevice(Dictionary<PropertyDataId, uint> dids = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.PetDevice,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.PetClass, 1 },
                    { PropertyInt.GearDamage, 18 },
                    { PropertyInt.GearDamageResist, 7 },
                    { PropertyInt.GearCrit, 14 },
                    { PropertyInt.GearCritResist, 3 },
                    { PropertyInt.GearCritDamage, 20 },
                    { PropertyInt.GearCritDamageResist, 5 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Essence" } },
                PropertiesDID = dids,
            };

            return new PetDevice(weenie, NextGuid());
        }

        private static Weenie PetWeenie(bool withWarMagic = false)
        {
            var skills = new Dictionary<Skill, PropertiesSkill>
            {
                { Skill.UnarmedCombat, new PropertiesSkill { InitLevel = 120, SAC = SkillAdvancementClass.Trained } },
            };

            if (withWarMagic)
                skills[Skill.WarMagic] = new PropertiesSkill { InitLevel = 200, SAC = SkillAdvancementClass.Trained };

            return new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.Level, 100 },
                    { PropertyInt.Lifespan, 43 },
                    { PropertyInt.ArmorLevel, 400 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.ResistSlash, 0.9 },
                    { PropertyFloat.ResistFire, 0.8 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Pet" } },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
                    [PropertyAttribute.Focus] = new PropertiesAttribute { InitLevel = 100 },
                    [PropertyAttribute.Self] = new PropertiesAttribute { InitLevel = 100 },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 1000 },
                },
                PropertiesSkill = skills,
            };
        }

        private static CombatPet MakePet(bool withWarMagic = false)
        {
            var pet = new CombatPet(PetWeenie(withWarMagic), NextGuid());
            pet.Health.Current = pet.Health.MaxValue;
            return pet;
        }

        private static PetSummonSource Retail(int? damage = 18, int? crit = 14) =>
            new PetSummonSource(1, damage, 7, crit, 3, 20, 5, null, null);

        // ---- FromDevice / GetSummonSources ----------------------------------------------------------------

        [TestMethod]
        public void FromDevice_CopiesSixRatings()
        {
            var device = MakeDevice();

            var source = PetSummonSource.FromDevice(device);

            Assert.AreEqual(1u, source.PetWcid, "PetClass is the pet wcid");
            Assert.AreEqual(18, source.GearDamage);
            Assert.AreEqual(7, source.GearDamageResist);
            Assert.AreEqual(14, source.GearCrit);
            Assert.AreEqual(3, source.GearCritResist);
            Assert.AreEqual(20, source.GearCritDamage);
            Assert.AreEqual(5, source.GearCritDamageResist);
            Assert.IsNull(source.BoundSpell1, "a retail device binds no spell");
            Assert.IsNull(source.BoundSpell2);
            Assert.IsFalse(source.HasBoundSpells);
        }

        [TestMethod]
        public void FromDevice_ReadsBoundSpellsFromTheDataIds()
        {
            var device = MakeDevice(new Dictionary<PropertyDataId, uint>
            {
                { PropertyDataId.SummonBoundSpell1, 2076 },
                { PropertyDataId.SummonBoundSpell2, 2101 },
            });

            var source = PetSummonSource.FromDevice(device);

            Assert.AreEqual(2076u, source.BoundSpell1);
            Assert.AreEqual(2101u, source.BoundSpell2);
            Assert.IsTrue(source.HasBoundSpells);
        }

        [TestMethod]
        public void RetailDevice_TwoSourcesIdentical()
        {
            var device = MakeDevice();

            var sources = device.GetSummonSources();

            Assert.AreEqual(2, sources.Count, "index 0 is the first summon, index 1 the Summon 2x one");
            Assert.AreEqual(sources[0], sources[1], "today Summon 2x brings out the same pet with the same ratings");
            Assert.AreEqual(PetSummonSource.FromDevice(device), sources[0]);
        }

        // ---- ApplySummonStats -----------------------------------------------------------------------------

        /// <summary>
        /// A verbatim copy of the stat block CombatPet.Init ran before the seam existed (statMod and
        /// durationMod supplied instead of read from the owner). Kept here as the reference the new code is
        /// held to; do NOT "simplify" it toward the new implementation.
        /// </summary>
        private static void LegacyInit(CombatPet pet, PetSummonSource device, float statMod, float durationMod)
        {
            // (the old Init also called SetCombatMode(CombatMode.Melee); that switch animates and needs the world, so
            // the stance is compared through SummonStance below instead)

            pet.DamageRating = device.GearDamage;
            pet.DamageResistRating = device.GearDamageResist;
            pet.CritDamageRating = device.GearCritDamage;
            pet.CritDamageResistRating = device.GearCritDamageResist;
            pet.CritRating = device.GearCrit;
            pet.CritResistRating = device.GearCritResist;

            if (statMod > 1.0f)
            {
                var bonusFraction = statMod - 1.0f;

                pet.Health.StartingValue += (uint)Math.Round(pet.Health.MaxValue * bonusFraction);
                pet.Health.Current = pet.Health.MaxValue;

                var ratingBonus = (int)Math.Round(bonusFraction * 100.0f);
                pet.DamageRating = (pet.DamageRating ?? 0) + ratingBonus;
                pet.DamageResistRating = (pet.DamageResistRating ?? 0) + ratingBonus;
                pet.CritDamageResistRating = (pet.CritDamageResistRating ?? 0) + ratingBonus;

                if (pet.ArmorLevel.HasValue)
                    pet.ArmorLevel = (int)Math.Round(pet.ArmorLevel.Value * statMod);

                var resistMod = ACE.Server.ClassAbilities.Abilities.EmpoweredSummonsAbility.ResistMultiplier(bonusFraction, 0.05);
                pet.ResistSlash = (pet.ResistSlash ?? 1.0) * resistMod;
                pet.ResistPierce = (pet.ResistPierce ?? 1.0) * resistMod;
                pet.ResistBludgeon = (pet.ResistBludgeon ?? 1.0) * resistMod;
                pet.ResistFire = (pet.ResistFire ?? 1.0) * resistMod;
                pet.ResistCold = (pet.ResistCold ?? 1.0) * resistMod;
                pet.ResistAcid = (pet.ResistAcid ?? 1.0) * resistMod;
                pet.ResistElectric = (pet.ResistElectric ?? 1.0) * resistMod;
                pet.ResistNether = (pet.ResistNether ?? 1.0) * resistMod;

                var attackSkill = pet.GetCreatureSkill(pet.GetCurrentAttackSkill());
                if (attackSkill != null)
                    attackSkill.InitLevel += (uint)Math.Round(attackSkill.Current * bonusFraction);
            }

            if (durationMod > 1.0f && pet.Lifespan.HasValue && pet.Lifespan.Value > 0)
                pet.Lifespan = (int)Math.Round(pet.Lifespan.Value * durationMod);
        }

        private static void AssertSamePet(CombatPet expected, CombatPet actual, string label)
        {
            Assert.AreEqual(expected.DamageRating, actual.DamageRating, label + " DamageRating");
            Assert.AreEqual(expected.DamageResistRating, actual.DamageResistRating, label + " DamageResistRating");
            Assert.AreEqual(expected.CritDamageRating, actual.CritDamageRating, label + " CritDamageRating");
            Assert.AreEqual(expected.CritDamageResistRating, actual.CritDamageResistRating, label + " CritDamageResistRating");
            Assert.AreEqual(expected.CritRating, actual.CritRating, label + " CritRating");
            Assert.AreEqual(expected.CritResistRating, actual.CritResistRating, label + " CritResistRating");
            Assert.AreEqual(expected.Health.MaxValue, actual.Health.MaxValue, label + " Health.MaxValue");
            Assert.AreEqual(expected.Health.Current, actual.Health.Current, label + " Health.Current");
            Assert.AreEqual(expected.ArmorLevel, actual.ArmorLevel, label + " ArmorLevel");
            Assert.AreEqual(expected.ResistSlash, actual.ResistSlash, label + " ResistSlash");
            Assert.AreEqual(expected.ResistFire, actual.ResistFire, label + " ResistFire");
            Assert.AreEqual(expected.ResistNether, actual.ResistNether, label + " ResistNether");
            Assert.AreEqual(expected.Lifespan, actual.Lifespan, label + " Lifespan");
            Assert.AreEqual(CombatMode.Melee, actual.SummonStance, label + " stance (old Init forced Melee)");

            var expectedAttack = expected.GetCreatureSkill(expected.GetCurrentAttackSkill());
            var actualAttack = actual.GetCreatureSkill(actual.GetCurrentAttackSkill());
            Assert.AreEqual(expectedAttack.InitLevel, actualAttack.InitLevel, label + " attack skill InitLevel");
            Assert.AreEqual(expectedAttack.Current, actualAttack.Current, label + " attack skill Current");

            Assert.AreEqual(expected.Biota.PropertiesSpellBook?.Count ?? 0, actual.Biota.PropertiesSpellBook?.Count ?? 0, label + " spellbook size");
        }

        [TestMethod]
        public void ApplySummonStats_RetailParity()
        {
            // no Empowered Summons at all, then a mixed case that exercises every branch of the old block
            foreach (var (statMod, durationMod) in new[] { (1.0f, 1.0f), (1.6f, 1.25f), (1.0f, 1.5f), (2.4f, 1.0f) })
            {
                var expected = MakePet();
                var actual = MakePet();

                LegacyInit(expected, Retail(), statMod, durationMod);
                actual.ApplySummonStats(Retail(), statMod, durationMod);

                AssertSamePet(expected, actual, $"stat {statMod} dur {durationMod}");
            }

            // positive control: the bump is actually applied, so the comparison above is not two untouched pets
            var bumped = MakePet();
            bumped.ApplySummonStats(Retail(), 1.6f, 1.25f);
            Assert.AreEqual(18 + 60, bumped.DamageRating, "0.6 of bonus fraction is 60 rating points");
            Assert.AreEqual(54, bumped.Lifespan, "43 * 1.25 rounded");
            Assert.AreEqual(CombatMode.Melee, bumped.SummonStance, "a non-ranged retail pet stays Melee");
        }

        [TestMethod]
        public void ApplySummonStats_WithNullRatings_LeavesThemNull()
        {
            var pet = MakePet();

            pet.ApplySummonStats(new PetSummonSource(1, null, null, null, null, null, null, null, null), 1.0f, 1.0f);

            Assert.IsNull(pet.DamageRating, "a source with no crit/damage ratings clears them, exactly as the old copy did");
            Assert.IsNull(pet.CritRating);
        }

        [TestMethod]
        public void ApplySummonStats_EmpoweredBumpsMagicSkill()
        {
            var pet = MakePet(withWarMagic: true);

            var attackBefore = pet.GetCreatureSkill(pet.GetCurrentAttackSkill());
            var attackCurrent = attackBefore.Current;
            var attackInit = attackBefore.InitLevel;

            var war = pet.GetCreatureSkill(Skill.WarMagic, false);
            Assert.IsNotNull(war, "positive control: the pet has a War Magic row");
            var warCurrent = war.Current;
            var warInit = war.InitLevel;
            Assert.IsTrue(warCurrent > 0, "positive control: a non-zero skill to scale");

            pet.ApplySummonStats(Retail(), 1.5f, 1.0f);

            Assert.AreEqual(attackInit + (uint)Math.Round(attackCurrent * 0.5), pet.GetCreatureSkill(pet.GetCurrentAttackSkill()).InitLevel, "attack skill bump unchanged");
            Assert.AreEqual(warInit + (uint)Math.Round(warCurrent * 0.5), pet.GetCreatureSkill(Skill.WarMagic, false).InitLevel, "War Magic gets the same fraction");

            Assert.IsNull(pet.GetCreatureSkill(Skill.LifeMagic, false), "a school the pet never had is not created by the bump");
        }

        [TestMethod]
        public void ApplySummonStats_NoEmpowerment_LeavesMagicSkillAlone()
        {
            var pet = MakePet(withWarMagic: true);
            var warInit = pet.GetCreatureSkill(Skill.WarMagic, false).InitLevel;

            pet.ApplySummonStats(Retail(), 1.0f, 1.0f);

            Assert.AreEqual(warInit, pet.GetCreatureSkill(Skill.WarMagic, false).InitLevel);
        }

        [TestMethod]
        public void ApplySummonStats_BoundSpellsInSpellbook()
        {
            var pet = MakePet();
            var source = new PetSummonSource(1, 18, 7, 14, 3, 20, 5, 2076, 2101);

            pet.ApplySummonStats(source, 1.0f, 1.0f);

            var book = pet.Biota.PropertiesSpellBook;
            Assert.IsNotNull(book);
            Assert.IsTrue(book.ContainsKey(2076), "bound spell 1");
            Assert.IsTrue(book.ContainsKey(2101), "bound spell 2");
            Assert.AreEqual(2, book.Count);
            Assert.AreEqual(CombatMode.Magic, pet.SummonStance, "a caster never melees: Magic stance");
        }

        [TestMethod]
        public void ApplySummonStats_SingleBoundSpell_AddsOnlyThatOne()
        {
            var pet = MakePet();

            pet.ApplySummonStats(new PetSummonSource(1, null, null, null, null, null, null, null, 2101), 1.0f, 1.0f);

            Assert.AreEqual(1, pet.Biota.PropertiesSpellBook.Count);
            Assert.IsTrue(pet.Biota.PropertiesSpellBook.ContainsKey(2101));
            Assert.AreEqual(CombatMode.Magic, pet.SummonStance);
        }

        [TestMethod]
        public void ApplySummonStats_RangedPetMissileStance()
        {
            var pet = MakePet();

            var bowWeenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MissileLauncher,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileWeapon },
                    { PropertyInt.DefaultCombatStyle, (int)CombatStyle.Bow },
                    { PropertyInt.WeaponSkill, (int)Skill.MissileWeapons },
                    { PropertyInt.AmmoType, (int)AmmoType.Arrow },
                },
            };

            var bow = new MissileLauncher(bowWeenie, NextGuid());
            pet.EquippedObjects[bow.Guid] = bow;
            bow.Wielder = pet;

            Assert.IsTrue(pet.IsRanged, "positive control: the fixture pet is ranged");

            pet.ApplySummonStats(Retail(), 1.0f, 1.0f);

            Assert.AreEqual(CombatMode.Missile, pet.SummonStance);
        }

        [TestMethod]
        public void ChooseSummonStance_CasterBeatsRanged_AndMeleeIsTheDefault()
        {
            Assert.AreEqual(CombatMode.Melee, CombatPet.ChooseSummonStance(false, false));
            Assert.AreEqual(CombatMode.Missile, CombatPet.ChooseSummonStance(false, true));
            Assert.AreEqual(CombatMode.Magic, CombatPet.ChooseSummonStance(true, false));
            Assert.AreEqual(CombatMode.Magic, CombatPet.ChooseSummonStance(true, true), "a ranged caster is still a caster");
        }
    }
}
