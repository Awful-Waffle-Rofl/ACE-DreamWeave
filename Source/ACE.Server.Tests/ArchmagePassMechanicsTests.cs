using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Archmage pass (2026-10-24, owner rulings): the pure halves of Elemental Inversion, Arcane Hollow, Volatile
    /// Wall and Mana Rush's cap, the two retirements' refunds, plus source pins for the call sites a live Player
    /// is needed to exercise (Player's static initializer does not run under the test host). Every
    /// PropertyManager key read here is seeded from the code defaults in ClassInitialize.
    /// </summary>
    [TestClass]
    public class ArchmagePassMechanicsTests
    {
        [ClassInitialize]
        public static void Setup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();   // a bare Creature needs the synthetic dat tables
        }

        // ---- Elemental Inversion ----------------------------------------------------------------------

        [TestMethod]
        public void ElementalInversion_Definition_IsArcaneLoreT1FiveRanksAtOne()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.ElementalInversion);
            Assert.AreEqual(93, (int)ClassAbilityId.ElementalInversion);
            Assert.AreEqual(ClassAbilityClass.Archmage, def.AbilityClass);
            Assert.AreEqual(1, def.Tier);
            Assert.AreEqual(5, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1 }, def.CostPerRank);
            Assert.AreEqual(Skill.ArcaneLore, def.AffinitySkill);
        }

        [TestMethod]
        public void ElementalInversion_ProcChance_IsTwoPercentPerRank_TimesAffinity_Clamped()
        {
            var perRank = PropertyManager.GetDouble("class_ability_elementalinversion_chance_per_rank").Item;
            Assert.AreEqual(0.02, perRank, 1e-12);

            Assert.AreEqual(0.0, ElementalInversionAbility.ProcChance(0, perRank, 1.0), 1e-12);
            Assert.AreEqual(0.02, ElementalInversionAbility.ProcChance(1, perRank, 1.0), 1e-12);
            Assert.AreEqual(0.10, ElementalInversionAbility.ProcChance(5, perRank, 1.0), 1e-12);
            Assert.AreEqual(0.15, ElementalInversionAbility.ProcChance(5, perRank, 1.5), 1e-12, "affinity multiplies");
            Assert.AreEqual(1.0, ElementalInversionAbility.ProcChance(5, 0.5, 2.0), 1e-12, "clamped to 1");
        }

        private static readonly ResistanceType[] TheSeven =
        {
            ResistanceType.Fire, ResistanceType.Cold, ResistanceType.Acid, ResistanceType.Electric,
            ResistanceType.Pierce, ResistanceType.Slash, ResistanceType.Bludgeon,
        };

        [TestMethod]
        public void InnateResistTypes_AreExactlyTheSevenPhysicalAndElementalTypes_NeverNetherOrLife()
        {
            CollectionAssert.AreEquivalent(TheSeven, ArchmageResistanceMath.InnateResistTypes);

            foreach (var type in TheSeven)
                Assert.IsTrue(ArchmageResistanceMath.IsCovered(type), type.ToString());

            Assert.IsFalse(ArchmageResistanceMath.IsCovered(ResistanceType.Nether));
            Assert.IsFalse(ArchmageResistanceMath.IsCovered(ResistanceType.HealthDrain));
            Assert.IsFalse(ArchmageResistanceMath.IsCovered(ResistanceType.StaminaDrain));
            Assert.IsFalse(ArchmageResistanceMath.IsCovered(ResistanceType.ManaDrain));
        }

        [TestMethod]
        public void WeakestInnate_IsTheHighestOfTheSeven_AndIgnoresNether()
        {
            var innate = new Dictionary<ResistanceType, double>
            {
                [ResistanceType.Fire] = 0.5,
                [ResistanceType.Cold] = 1.2,
                [ResistanceType.Acid] = 0.8,
                [ResistanceType.Electric] = 0.9,
                [ResistanceType.Pierce] = 0.6,
                [ResistanceType.Slash] = 1.4,
                [ResistanceType.Bludgeon] = 1.0,
                [ResistanceType.Nether] = 5.0,   // never a candidate
            };

            Assert.AreEqual(1.4, ArchmageResistanceMath.WeakestInnate(t => innate[t]), 1e-12,
                "Slash (physical) is the weakest; Nether's 5.0 is ignored");
        }

        /// <summary>
        /// The owner's no-downside guarantee: the substituted innate value is the max of a set that includes the
        /// original type, so it is never below the original for any of the seven, over many random monsters.
        /// </summary>
        [TestMethod]
        public void ElementalInversion_SubstitutedInnate_IsNeverBelowTheOriginal_ForAllSeven()
        {
            var rng = new Random(20261024);

            for (var trial = 0; trial < 2000; trial++)
            {
                var innate = TheSeven.ToDictionary(t => t, t => rng.NextDouble() * 2.0);
                Func<ResistanceType, double> lookup = t => innate.TryGetValue(t, out var v) ? v : 1.0;

                foreach (var original in TheSeven)
                {
                    var over = ArchmageResistanceMath.InnateOverride(original, lookup, 1.0, 0.0, 0.0);
                    var substituted = over ?? innate[original];
                    Assert.IsTrue(substituted >= innate[original], $"trial {trial} {original}: {substituted} < {innate[original]}");
                }
            }
        }

        [TestMethod]
        public void ElementalInversion_Override_OnlyUnderTheChance_AndNullWhenNothingMoves()
        {
            Func<ResistanceType, double> innate = t => t == ResistanceType.Acid ? 1.5 : 0.7;

            Assert.AreEqual(1.5, ArchmageResistanceMath.InnateOverride(ResistanceType.Slash, innate, 0.10, 0.05, 0.0).Value, 1e-12);
            Assert.IsNull(ArchmageResistanceMath.InnateOverride(ResistanceType.Slash, innate, 0.10, 0.10, 0.0), "roll == chance misses");
            Assert.IsNull(ArchmageResistanceMath.InnateOverride(ResistanceType.Slash, innate, 0.0, 0.0, 0.0), "zero chance never procs");
            Assert.IsNull(ArchmageResistanceMath.InnateOverride(ResistanceType.Acid, innate, 1.0, 0.0, 0.0),
                "a proc on the type that is already the weakest changes nothing");
        }

        /// <summary>(c) A Nether hit never rolls: null for any chance and roll, with Hollow too.</summary>
        [TestMethod]
        public void ElementalInversion_NetherAndLifeHits_NeverRollOrTakeTheIgnore()
        {
            Func<ResistanceType, double> innate = t => t == ResistanceType.Slash ? 2.0 : 0.5;

            foreach (var type in new[] { ResistanceType.Nether, ResistanceType.HealthDrain })
            {
                Assert.IsNull(ArchmageResistanceMath.InnateOverride(type, innate, 1.0, 0.0, 0.0), type + ": inversion");
                Assert.IsNull(ArchmageResistanceMath.InnateOverride(type, innate, 0.0, 1.0, 1.0), type + ": hollow");
            }

        }

        /// <summary>Immunity is respected: an original type at innate 0 is never overridden, even at chance 1.</summary>
        [TestMethod]
        public void ElementalInversion_ImmuneOriginalType_IsNeverOverridden()
        {
            Func<ResistanceType, double> innate = t => t == ResistanceType.Fire ? 0.0 : (t == ResistanceType.Slash ? 1.0 : 0.5);

            Assert.IsNull(ArchmageResistanceMath.InnateOverride(ResistanceType.Fire, innate, 1.0, 0.0, 0.0), "inversion");
            Assert.IsNull(ArchmageResistanceMath.InnateOverride(ResistanceType.Fire, innate, 1.0, 0.0, 1.0), "inversion + hollow");
            Assert.IsNull(ArchmageResistanceMath.InnateOverride(ResistanceType.Fire, t => t == ResistanceType.Fire ? -0.5 : 1.0, 1.0, 0.0, 0.0), "negative");

            // control: a non-immune original type with the same weakest value does substitute
            Assert.AreEqual(1.0, ArchmageResistanceMath.InnateOverride(ResistanceType.Cold, innate, 1.0, 0.0, 0.0).Value, 1e-12);
        }

        // ---- the pure per-hit decision (F4), with a counting roll --------------------------------------

        private static Func<double> CountingRoll(double value, int[] calls) => () => { calls[0]++; return value; };

        [TestMethod]
        public void Decide_NetherAndNonPve_NeverInvokeTheRoll()
        {
            Func<ResistanceType, double> innate = t => t == ResistanceType.Slash ? 2.0 : 0.5;
            var calls = new int[1];

            Assert.IsNull(ArchmageResistanceMath.Decide(true, ResistanceType.Nether, true, 1.0, 1.0, CountingRoll(0.0, calls), innate));
            Assert.IsNull(ArchmageResistanceMath.Decide(true, ResistanceType.HealthDrain, true, 1.0, 1.0, CountingRoll(0.0, calls), innate));
            Assert.IsNull(ArchmageResistanceMath.Decide(false, ResistanceType.Fire, true, 1.0, 1.0, CountingRoll(0.0, calls), innate));
            Assert.AreEqual(0, calls[0], "no roll drawn for Nether, life or a non-PvE target");

            // positive control: an eligible PvE fire hit draws exactly one roll and substitutes
            Assert.AreEqual(2.0, ArchmageResistanceMath.Decide(true, ResistanceType.Fire, false, 1.0, 0.0, CountingRoll(0.0, calls), innate).Value, 1e-12);
            Assert.AreEqual(1, calls[0]);
        }

        [TestMethod]
        public void Decide_WeaponHitWithHollowOnlyAndNoChance_ReturnsNull()
        {
            Func<ResistanceType, double> innate = t => 0.5;
            var calls = new int[1];

            Assert.IsNull(ArchmageResistanceMath.Decide(true, ResistanceType.Slash, false, 0.0, 0.3, CountingRoll(0.0, calls), innate),
                "Arcane Hollow is magic only");
            Assert.AreEqual(0, calls[0]);

            // control: the same hit as magic damage takes the ignore, still without a roll
            Assert.AreEqual(0.65, ArchmageResistanceMath.Decide(true, ResistanceType.Slash, true, 0.0, 0.3, CountingRoll(0.0, calls), innate).Value, 1e-12);
            Assert.AreEqual(0, calls[0], "zero chance never rolls");
        }

        [TestMethod]
        public void Decide_ImmuneOriginal_ReturnsNull_WithoutRolling()
        {
            Func<ResistanceType, double> innate = t => t == ResistanceType.Acid ? 0.0 : 1.5;
            var calls = new int[1];

            Assert.IsNull(ArchmageResistanceMath.Decide(true, ResistanceType.Acid, true, 1.0, 1.0, CountingRoll(0.0, calls), innate));
            Assert.AreEqual(0, calls[0]);
        }

        [TestMethod]
        public void Decide_IsWhatThePlayerMethodReturns()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ArchmageResistance.cs"),
                "public double? GetArchmageInnateOverride(");
            StringAssert.Contains(body, "return ArchmageResistanceMath.Decide(ArchmageResistanceMath.IsPveTarget(target), originalType, magicDamage,");
            Assert.AreEqual(1, CountOf(body, "ThreadSafeRandom.Next"), "the only roll is the one handed to Decide");
            Assert.IsFalse(body.Contains("InnateOverride("), "the Player method does not bypass Decide");
        }

        /// <summary>(b) A monster with all seven innate values at 0 is unchanged: still 0, with or without Hollow.</summary>
        [TestMethod]
        public void ElementalInversion_AllSevenImmune_StaysZero()
        {
            Func<ResistanceType, double> innate = t => 0.0;

            foreach (var type in TheSeven)
            {
                var over = ArchmageResistanceMath.InnateOverride(type, innate, 1.0, 0.0, 1.0);
                Assert.AreEqual(0.0, over ?? innate(type), 1e-12, type.ToString());
            }

            var creature = NewCreature(0x7F300002);
            foreach (var type in TheSeven)
                SetInnate(creature, type, 0.0);

            Assert.AreEqual(0.0, creature.GetResistanceMod(ResistanceType.Fire, null, null, 1.0f,
                ArchmageResistanceMath.InnateOverride(ResistanceType.Fire, t => ArchmageResistanceMath.InnateResistance(creature, t), 1.0, 0.0, 1.0)), 1e-12);
        }

        /// <summary>
        /// (a) The owner example: a fire hit on a monster weakest to slash. On a proc the hit keeps the FIRE
        /// vulnerability enchantment and the fire rend; only the fire innate term is read as the slash value.
        /// The type-swap reading (slash innate x slash enchantments) gives a different number and is refuted.
        /// </summary>
        [TestMethod]
        public void ElementalInversion_FireHitWithFireVuln_KeepsTheVulnAndRend_OnAProc()
        {
            var creature = NewCreature(0x7F300003);
            creature.ResistFire = 0.5;
            creature.ResistSlash = 2.0;

            creature.Biota.PropertiesEnchantmentRegistry.AddEnchantment(new PropertiesEnchantmentRegistry
            {
                SpellId = 1,
                SpellCategory = SpellCategory.FireVulnerability,
                LayerId = 1,
                PowerLevel = 100,
                StartTime = 0,
                Duration = 60.0,
                StatModType = EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative,
                StatModKey = (uint)ACE.Entity.Enum.Properties.PropertyFloat.ResistFire,
                StatModValue = 1.5f,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            }, creature.BiotaDatabaseLock);

            Assert.AreEqual(1.5f, creature.EnchantmentManager.GetVulnerabilityResistanceMod(DamageType.Fire), 1e-6f, "control: the fire vuln is read");

            var over = ArchmageResistanceMath.InnateOverride(ResistanceType.Fire,
                t => ArchmageResistanceMath.InnateResistance(creature, t), 1.0, 0.0, 0.0);
            Assert.AreEqual(2.0, over.Value, 1e-12, "the substituted innate is the slash value");

            Assert.AreEqual(0.5 * 1.5, creature.GetResistanceMod(ResistanceType.Fire), 1e-6, "no proc: fire innate x fire vuln");
            Assert.AreEqual(2.0 * 1.5, creature.GetResistanceMod(ResistanceType.Fire, null, null, 1.0f, over), 1e-6, "proc: slash innate x FIRE vuln");
            Assert.AreNotEqual(creature.GetResistanceMod(ResistanceType.Slash), creature.GetResistanceMod(ResistanceType.Fire, null, null, 1.0f, over),
                "not a type swap: the slash lookup would drop the fire vuln");

            // a fire rend stronger than the vuln still wins the original type's max (rend is not displaced)
            Assert.AreEqual(2.0 * 2.0, creature.GetResistanceMod(ResistanceType.Fire, null, null, 2.0f, over), 1e-6, "proc keeps the fire rend");
        }

        [TestMethod]
        public void ElementalInversion_InnateResistance_ReadsTheWeenieProperty_AbsentIsOne()
        {
            var creature = NewCreature(0x7F300001);
            creature.ResistFire = 0.25;

            Assert.AreEqual(0.25, ArchmageResistanceMath.InnateResistance(creature, ResistanceType.Fire), 1e-12);
            Assert.AreEqual(1.0, ArchmageResistanceMath.InnateResistance(creature, ResistanceType.Cold), 1e-12, "absent = 1.0");
            Assert.AreEqual(1.0, ArchmageResistanceMath.InnateResistance(creature, ResistanceType.HealthDrain), 1e-12);
        }

        [TestMethod]
        public void InnateOverride_ReplacesOnlyTheInnateTerm_ForTheSevenOnly()
        {
            var creature = NewCreature(0x7F300004);
            creature.ResistCold = 0.4;
            creature.ResistHealthDrain = 0.6;

            Assert.AreEqual(0.4, creature.GetResistanceMod(ResistanceType.Cold), 1e-9);
            Assert.AreEqual(0.9, creature.GetResistanceMod(ResistanceType.Cold, null, null, 1.0f, 0.9), 1e-9);
            Assert.AreEqual(creature.GetResistanceMod(ResistanceType.HealthDrain),
                creature.GetResistanceMod(ResistanceType.HealthDrain, null, null, 1.0f, 0.9), 1e-9, "ignored for life");

            creature.ResistNether = 0.3;
            Assert.AreEqual(0.3, creature.GetResistanceMod(ResistanceType.Nether), 1e-9);
            Assert.AreEqual(0.3, creature.GetResistanceMod(ResistanceType.Nether, null, null, 1.0f, 0.9), 1e-9, "ignored for Nether");
        }

        [TestMethod]
        public void ElementalInversion_IsWiredAsAnInnateOverride_AtWeaponAndWarSitesOnly()
        {
            var spell = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs");
            StringAssert.Contains(spell, "archmageCaster.GetArchmageInnateOverride(target, resistanceType, true, out elementalPierceShape)");
            StringAssert.Contains(spell, "target.GetResistanceMod(resistanceType, this, null, weaponResistanceMod, archmageInnate)");
            Assert.IsFalse(spell.Contains("RollElementalInversion") || spell.Contains("GetArcaneHollowResistanceMultiplier"),
                "no type swap and no post-hoc ratio remain");
            Assert.AreEqual(1, CountOf(spell, "GetArchmageInnateOverride("), "war/void branch only - never the life branch");

            var melee = PooledLootSourceText.Read("Source/ACE.Server/Entity/DamageEvent.cs");
            StringAssert.Contains(melee, "inversionAttacker.GetArchmageInnateOverride(defender, resistanceType, false)");
            StringAssert.Contains(melee, "defender.GetResistanceMod(resistanceType, Attacker, Weapon, WeaponResistanceMod, inversionInnate)");

            var magic = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs");
            Assert.IsFalse(magic.Contains("GetArchmageInnateOverride") || magic.Contains("harmResistanceType"),
                "Harm resolves through HealthDrain and is not covered");
        }

        // ---- PvE gate (F6) ----------------------------------------------------------------------------

        [TestMethod]
        public void IsPveTarget_TrueForAMonster_FalseForAPlayerOrNull()
        {
            Assert.IsTrue(ArchmageResistanceMath.IsPveTarget(NewCreature(0x7F300005)));
            Assert.IsFalse(ArchmageResistanceMath.IsPveTarget(null));

            var player = (Player)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Player));
            Assert.IsFalse(ArchmageResistanceMath.IsPveTarget(player));
        }

        [TestMethod]
        public void PlayerArchmageMethods_GateOnIsPveTargetFirst()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ArchmageResistance.cs");

            foreach (var signature in new[] { "public double? GetArchmageInnateOverride(", "public float ApplyVolatileWall(" })
            {
                var body = PooledLootSourceText.MethodBody(src, signature);
                var gate = body.IndexOf("if (!ArchmageResistanceMath.IsPveTarget(target)", StringComparison.Ordinal);
                Assert.IsTrue(gate >= 0, signature + " has the PvE gate");
                Assert.IsTrue(gate < body.IndexOf("TryGetClassAbility", StringComparison.Ordinal), signature + ": gate precedes the rank read");
            }

            Assert.IsTrue(src.Contains("RecordLanded") &&
                PooledLootSourceText.MethodBody(src, "public float ApplyVolatileWall(").IndexOf("IsPveTarget", StringComparison.Ordinal)
                < PooledLootSourceText.MethodBody(src, "public float ApplyVolatileWall(").IndexOf("RecordLanded", StringComparison.Ordinal),
                "a PvP hit never advances the wall counter");
        }

        // ---- Arcane Hollow ----------------------------------------------------------------------------

        [TestMethod]
        public void ArcaneHollow_Definition_IsMagicItemTinkeringT2ThreeRanksAtOne()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.ArcaneHollow);
            Assert.AreEqual(94, (int)ClassAbilityId.ArcaneHollow);
            Assert.AreEqual(ClassAbilityClass.Archmage, def.AbilityClass);
            Assert.AreEqual(2, def.Tier);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
            Assert.AreEqual(Skill.MagicItemTinkering, def.AffinitySkill);
            Assert.AreEqual("Arcane Hollow", def.DisplayName);
            StringAssert.Contains(def.Description, "not resistance from enchantments");
            StringAssert.Contains(def.Description, "war magic's elemental and physical damage types");
        }

        [TestMethod]
        public void ArcaneHollow_ExsanguinateShape_OnTheInnateTermOnly()
        {
            var perRank = PropertyManager.GetDouble("class_ability_arcanehollow_ignore_per_rank").Item;
            Assert.AreEqual(0.10, perRank, 1e-12);

            var f3 = ArcaneHollowAbility.IgnoreFraction(3, perRank, 1.0);
            Assert.AreEqual(0.30, f3, 1e-12);
            Assert.AreEqual(1.0, ArcaneHollowAbility.IgnoreFraction(3, 0.5, 2.0), 1e-12, "clamped to 1");
            Assert.AreEqual(0.0, ArcaneHollowAbility.IgnoreFraction(0, perRank, 2.0), 1e-12);

            // innate 0.5, ignore 30%: r' = 0.5 + 0.3 x 0.5 = 0.65 - the same shape as the Exsanguinate ratio
            Assert.AreEqual(0.65, ArchmageResistanceMath.HollowInnate(0.5, f3), 1e-12);
            Assert.AreEqual(0.5 * ExsanguinateMath.ResistanceIgnoreMultiplier(0.5, f3), ArchmageResistanceMath.HollowInnate(0.5, f3), 1e-6);

            // no effect when already weak (r >= 1) or immune (r = 0)
            Assert.AreEqual(1.2, ArchmageResistanceMath.HollowInnate(1.2, f3), 1e-12);
            Assert.AreEqual(1.0, ArchmageResistanceMath.HollowInnate(1.0, f3), 1e-12);
            Assert.AreEqual(0.0, ArchmageResistanceMath.HollowInnate(0.0, f3), 1e-12);

            // a physical type is covered (seven types); Nether is not
            Func<ResistanceType, double> innate = t => 0.5;
            Assert.AreEqual(0.65, ArchmageResistanceMath.InnateOverride(ResistanceType.Slash, innate, 0.0, 1.0, f3).Value, 1e-12);
            Assert.IsNull(ArchmageResistanceMath.InnateOverride(ResistanceType.Nether, innate, 0.0, 1.0, f3));
        }

        [TestMethod]
        public void ArcaneHollow_AndInversionTogether_SubstituteFirstThenIgnore()
        {
            // fire 0.2, weakest (bludgeon) 0.6, ignore 50%: substitute 0.6, then 0.6 + 0.5 x 0.4 = 0.8
            Func<ResistanceType, double> innate = t => t == ResistanceType.Bludgeon ? 0.6 : 0.2;
            Assert.AreEqual(0.8, ArchmageResistanceMath.InnateOverride(ResistanceType.Fire, innate, 1.0, 0.0, 0.5).Value, 1e-12);

            // no proc: the ignore applies to the original innate value
            Assert.AreEqual(0.6, ArchmageResistanceMath.InnateOverride(ResistanceType.Fire, innate, 0.5, 0.9, 0.5).Value, 1e-12);
        }

        [TestMethod]
        public void ArcaneHollow_IsMagicOnly_TheWeaponSitePassesFalse()
        {
            var melee = PooledLootSourceText.Read("Source/ACE.Server/Entity/DamageEvent.cs");
            Assert.IsFalse(melee.Contains("GetArchmageInnateOverride(defender, resistanceType, true)"));

            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ArchmageResistance.cs");
            StringAssert.Contains(src, "if (magicDamage && TryGetClassAbility(ClassAbilityId.ArcaneHollow, out var hollowRank))");
        }

        // ---- Volatile Wall ----------------------------------------------------------------------------

        [TestMethod]
        public void VolatileWall_Definition_IsWarMagicT3ThreeRanksAtOne()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.VolatileWall);
            Assert.AreEqual(95, (int)ClassAbilityId.VolatileWall);
            Assert.AreEqual(ClassAbilityClass.Archmage, def.AbilityClass);
            Assert.AreEqual(3, def.Tier);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
            Assert.AreEqual(Skill.WarMagic, def.AffinitySkill);
        }

        [TestMethod]
        public void VolatileWall_IsAdditive_FirstLandedIsBase_OwnerExampleAtTenPercent()
        {
            var r1 = PropertyManager.GetDouble(VolatileWallAbility.BonusKey(1)).Item;
            var r2 = PropertyManager.GetDouble(VolatileWallAbility.BonusKey(2)).Item;
            var r3 = PropertyManager.GetDouble(VolatileWallAbility.BonusKey(3)).Item;
            Assert.AreEqual(0.03, r1, 1e-12);
            Assert.AreEqual(0.06, r2, 1e-12);
            Assert.AreEqual(0.10, r3, 1e-12);

            var b = VolatileWallAbility.PerProjectileBonus(3, r3, 1.0);
            Assert.AreEqual(1.0f, VolatileWallAbility.DamageMultiplier(0, b), 1e-6f);
            Assert.AreEqual(1.1f, VolatileWallAbility.DamageMultiplier(1, b), 1e-6f);
            Assert.AreEqual(1.2f, VolatileWallAbility.DamageMultiplier(2, b), 1e-6f);
            Assert.AreEqual(1.5f, VolatileWallAbility.DamageMultiplier(5, b), 1e-6f, "additive, never compounding");

            Assert.AreEqual(0.15, VolatileWallAbility.PerProjectileBonus(3, r3, 1.5), 1e-12, "affinity multiplies b");
            Assert.AreEqual(0.0, VolatileWallAbility.PerProjectileBonus(0, r3, 1.5), 1e-12);
        }

        [TestMethod]
        public void VolatileWall_AoeAndCascadeCopiesGetNoCounter_ButAnEchoKeepsItsOwn()
        {
            var counter = new VolatileWallAbility.CastCounter();
            Assert.AreEqual(0, counter.RecordLanded());
            Assert.AreEqual(1, counter.RecordLanded());
            Assert.AreEqual(2, counter.Landed);

            var parent = NewProjectile();
            parent.VolatileWallCounter = counter;

            // a Spell AOE / Cascade child is NOT one of the wall's own projectiles: no counter, no ramp
            var child = NewProjectile();
            SpellProjectile.CopyPerCastStamps(child, parent);
            Assert.IsNull(child.VolatileWallCounter);

            // and a copy that already held a counter is left alone (Echo keeps its own)
            var cascadeLike = NewProjectile();
            var fresh = new VolatileWallAbility.CastCounter();
            cascadeLike.VolatileWallCounter = fresh;
            SpellProjectile.CopyPerCastStamps(cascadeLike, parent);
            Assert.AreSame(fresh, cascadeLike.VolatileWallCounter);

            var cascade = PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/CascadeAbility.cs");
            StringAssert.Contains(cascade, "child.VolatileWallCounter = null;", "Cascade clears a wall re-fire's own fresh counter");

            // an Echo Cast recast is a separate cast: it keeps the fresh counter its own launch created
            var echo = NewProjectile();
            var echoOwn = new VolatileWallAbility.CastCounter();
            echo.VolatileWallCounter = echoOwn;
            EchoCastAbility.StampEchoCopy(echo, parent);
            Assert.AreSame(echoOwn, echo.VolatileWallCounter);
            Assert.IsTrue(echo.IsEchoCopy);
        }

        [TestMethod]
        public void VolatileWall_CounterIsCreatedOncePerPlayerWallLaunch_AndReadInThePvEBlock()
        {
            var magic = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs");
            StringAssert.Contains(magic, "var volatileWallCounter = spellType == ProjectileSpellType.Wall && this is Player ? new VolatileWallAbility.CastCounter() : null;");
            StringAssert.Contains(magic, "sp.VolatileWallCounter = volatileWallCounter;");

            var spell = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs");
            var pveBlock = spell.IndexOf("if (sourceCreature is Player classAbilityCaster && target is not Player)", StringComparison.Ordinal);
            var call = spell.IndexOf("finalDamage *= classAbilityCaster.ApplyVolatileWall(this, target);", StringComparison.Ordinal);
            Assert.IsTrue(pveBlock >= 0 && call > pveBlock, "Volatile Wall is applied inside the PvE war/void block");
        }

        /// <summary>
        /// F3: the counter is atomic. N parallel hits on one cast's counter get priors that are exactly a
        /// permutation of 0..N-1 - no two hits share a prior, none is skipped - and Landed ends at N.
        /// </summary>
        [TestMethod]
        public void VolatileWall_Counter_IsAtomicUnderParallelHits()
        {
            const int n = 20000;
            var counter = new VolatileWallAbility.CastCounter();
            var priors = new int[n];

            System.Threading.Tasks.Parallel.For(0, n, i => priors[i] = counter.RecordLanded());

            Assert.AreEqual(n, counter.Landed);
            CollectionAssert.AreEqual(Enumerable.Range(0, n).ToArray(), priors.OrderBy(p => p).ToArray());
        }

        // ---- Mana Rush (internal name netherrush) -----------------------------------------------------

        [TestMethod]
        public void ManaRush_KeepsItsPersistedName_ButDisplaysAsManaRush()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.NetherRush);
            Assert.AreEqual("netherrush", def.Name, "ownership persists by Name - never rename it");
            Assert.AreEqual("Mana Rush", def.DisplayName);
            Assert.AreEqual(Skill.Loyalty, def.AffinitySkill);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
        }

        [TestMethod]
        public void ManaRush_CapIsFiftyPercentTimesTheLoyaltyMultiplier()
        {
            var perRank = PropertyManager.GetDouble("class_ability_netherrush_percent_per_rank").Item;
            var baseCap = PropertyManager.GetDouble("class_ability_netherrush_max_bonus").Item;
            Assert.AreEqual(0.50, baseCap, 1e-12);

            // no Loyalty: rank 3 full stacks (75%) held at +50%
            var cap = NetherRushAbility.MaxBonus(baseCap, 1.0);
            Assert.AreEqual(1.50f, NetherRushAbility.CastSpeedMultiplier(5, 3, perRank, 0.0, cap), 1e-5f);

            // Loyalty 1.2: per-stack 0.18, ramp 0.90, cap 0.60
            var aff = 1.2;
            var added = 3 * perRank * aff - 3 * perRank;
            Assert.AreEqual(1.60f, NetherRushAbility.CastSpeedMultiplier(5, 3, perRank, added, NetherRushAbility.MaxBonus(baseCap, aff)), 1e-5f);

            // rank 1 never reaches the cap (5 x 5% = 25%)
            Assert.AreEqual(1.25f, NetherRushAbility.CastSpeedMultiplier(5, 1, perRank, 0.0, cap), 1e-5f);
        }

        [TestMethod]
        public void ManaRush_HasNoSchoolGate_IsPvEOnly_AndIsTheOnlyCastSpeedTerm()
        {
            var buffs = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");
            var body = PooledLootSourceText.MethodBody(buffs, "public float ApplyNetherRushCastSpeed(");
            Assert.IsFalse(body.Contains("MagicSchool.VoidMagic"), "the Void-only school gate came off");
            StringAssert.Contains(body, "if (spell.IsHarmful && PvpClassifier.IsPvp(this, target))");

            var combined = PooledLootSourceText.MethodBody(buffs, "public float ApplyClassAbilityCastSpeed(");
            StringAssert.Contains(combined, "return ApplyNetherRushCastSpeed(spell, target);");
            Assert.IsFalse(buffs.Contains("GetFlatCastSpeedMod") || buffs.Contains("GetQuickenedCastingMod"));
        }

        // ---- retirements ------------------------------------------------------------------------------

        [TestMethod]
        public void RetiredQuickenedCastingAndFlatCastSpeed_AreUnregistered_AndRefundByNameAndToken()
        {
            Assert.IsFalse(ClassAbilityRegistry.Abilities.ContainsKey(ClassAbilityId.QuickenedCasting));
            Assert.IsFalse(ClassAbilityRegistry.Abilities.ContainsKey(ClassAbilityId.FlatCastSpeed));

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("quickened_casting", 5, out var qc, out var qcName));
            Assert.AreEqual(5, qc);
            Assert.AreEqual("Quickened Casting", qcName);

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("flatcastspeed", 3, out var fcs, out var fcsName));
            Assert.AreEqual(3, fcs);
            Assert.AreEqual("Flat Cast Speed", fcsName);

            // an unused prepaid token is refunded by its numeric id, one rank's price
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.QuickenedCasting, 5, out var qcToken, out _));
            Assert.AreEqual(1, qcToken);
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.FlatCastSpeed, 3, out var fcsToken, out _));
            Assert.AreEqual(1, fcsToken);
        }

        [TestMethod]
        public void NewTokens_MintInsideTheirReservedSlots()
        {
            Assert.AreEqual(1002192u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ElementalInversion, 1));
            Assert.AreEqual(1002196u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ElementalInversion, 5));
            Assert.AreEqual(1002198u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ArcaneHollow, 1));
            Assert.AreEqual(1002204u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.VolatileWall, 1));

            // the retired slots keep their wcids
            Assert.AreEqual(1002102u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.QuickenedCasting, 1));
            Assert.AreEqual(1000794u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.FlatCastSpeed, 1));
        }

        private static Creature NewCreature(uint guid) =>
            new Creature(new Weenie { WeenieClassId = 993500, WeenieType = WeenieType.Creature }, new ACE.Entity.ObjectGuid(guid));

        private static void SetInnate(Creature creature, ResistanceType type, double value)
        {
            switch (type)
            {
                case ResistanceType.Fire: creature.ResistFire = value; break;
                case ResistanceType.Cold: creature.ResistCold = value; break;
                case ResistanceType.Acid: creature.ResistAcid = value; break;
                case ResistanceType.Electric: creature.ResistElectric = value; break;
                case ResistanceType.Pierce: creature.ResistPierce = value; break;
                case ResistanceType.Slash: creature.ResistSlash = value; break;
                case ResistanceType.Bludgeon: creature.ResistBludgeon = value; break;
            }
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static uint nextProjectileWcid = 993600;

        private static SpellProjectile NewProjectile()
        {
            var weenie = new Weenie { WeenieClassId = nextProjectileWcid++, WeenieType = WeenieType.ProjectileSpell };
            return new SpellProjectile(weenie, new ACE.Entity.ObjectGuid(0x7F310000 + nextProjectileWcid));
        }
    }
}
