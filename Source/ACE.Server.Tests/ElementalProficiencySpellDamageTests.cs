using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

using PierceShape = ACE.Server.ClassAbilities.ElementalProficiencySpellMath.PierceShape;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Elemental Proficiency's instant spell-damage effects (Fire, Slash, Pierce): the pure core in
    /// ElementalProficiencySpellMath fed by ElementalProficiencyMath.Magnitude, plus source-scan pins on the
    /// SpellProjectile / Player wiring (Player's static initializer cannot run in this host, so the Player
    /// partial itself is pinned by text rather than called). Every key read is seeded to its shipped default
    /// and put back, so the class passes alone and in any order.
    ///
    /// Pierce takes the LARGER of its two shapes on each hit (owner ruling 2026-11-02): the resistance ignore or
    /// the flat x(1 + p), never both, decided once per hit after any Elemental Inversion substitution.
    /// </summary>
    [TestClass]
    public class ElementalProficiencySpellDamageTests
    {
        private const double Tol = 1e-9;

        private static readonly string[] DoubleKeys =
        {
            "fire_percent_per_rank", "pierce_ignore_per_rank", "slashcrit_points_per_rank", "affinity_cap",
        };

        [TestInitialize]
        public void Seed()
        {
            foreach (var k in DoubleKeys)
            {
                var full = ElementalProficiencyMath.KeyPrefix + k;
                Assert.IsTrue(PropertyManager.ModifyDouble(full, DefaultPropertyManager.DefaultDoubleProperties[full].Item), full);
            }
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (var k in DoubleKeys)
            {
                var full = ElementalProficiencyMath.KeyPrefix + k;
                PropertyManager.ModifyDouble(full, DefaultPropertyManager.DefaultDoubleProperties[full].Item);
            }
        }

        private static double Mult(DamageType type, int rank, PierceShape shape, double affinity = 1.0, double gear = 0.0)
        {
            return ElementalProficiencySpellMath.DamageMultiplier(type, ElementalProficiencyMath.Magnitude(type, rank, affinity, gear), shape);
        }

        /// <summary>A Pierce hit through the pure decision: every other type's innate is <paramref name="others"/>.</summary>
        private static double? DecidePierce(double innatePierce, double magnitude, out PierceShape shape,
            double chance = 0.0, double hollow = 0.0, double others = 1.0, int[] rollCalls = null)
        {
            Func<ResistanceType, double> innate = t => t == ResistanceType.Pierce ? innatePierce : others;
            Func<double> roll = () =>
            {
                if (rollCalls != null)
                    rollCalls[0]++;
                return 0.0;
            };

            return ElementalProficiencySpellMath.DecideSpellInnate(true, ResistanceType.Pierce, chance, hollow, magnitude, roll, innate, out shape);
        }

        /// <summary>The whole damage factor a Pierce hit ends up with: the innate term used times the final multiplier.</summary>
        private static double PierceFactor(double innatePierce, double magnitude, out PierceShape shape,
            double chance = 0.0, double hollow = 0.0, double others = 1.0)
        {
            var over = DecidePierce(innatePierce, magnitude, out shape, chance, hollow, others);
            return (over ?? innatePierce) * ElementalProficiencySpellMath.DamageMultiplier(DamageType.Pierce, magnitude, shape);
        }

        [TestMethod]
        public void Fire_MultipliesDamageByOnePlusMagnitude_AtEachRank()
        {
            Assert.AreEqual(1.10, Mult(DamageType.Fire, 1, PierceShape.None), Tol);
            Assert.AreEqual(1.20, Mult(DamageType.Fire, 2, PierceShape.None), Tol);
            Assert.AreEqual(1.30, Mult(DamageType.Fire, 3, PierceShape.None), Tol);
        }

        [TestMethod]
        public void Fire_ScalesWithAffinityCapAndGear()
        {
            // affinity 1.5 clamps to 1.25 (cap 0.25); gear amplifier 0.2 -> 0.10 * 1.25 * 1.2 = 0.15
            Assert.AreEqual(1.15, Mult(DamageType.Fire, 1, PierceShape.None, affinity: 1.5, gear: 0.2), Tol);
        }

        [TestMethod]
        public void Fire_IgnoresThePierceShape()
        {
            foreach (var shape in new[] { PierceShape.None, PierceShape.Ignore, PierceShape.Flat })
                Assert.AreEqual(1.20, Mult(DamageType.Fire, 2, shape), Tol, shape.ToString());
        }

        [TestMethod]
        public void Rank0_GivesNeutralResults_ForEveryEffect()
        {
            foreach (var type in new[] { DamageType.Fire, DamageType.Pierce, DamageType.Slash })
            {
                Assert.AreEqual(1.0, Mult(type, 0, PierceShape.Flat), Tol, type.ToString());
                Assert.AreEqual(0.0, ElementalProficiencyMath.Magnitude(type, 0, 1.25, 0.5), Tol, type.ToString());
            }

            // no magnitude: the decision is plain Decide, which leaves the lookup alone with nothing else active
            Assert.IsNull(DecidePierce(0.5, 0.0, out var shape));
            Assert.AreEqual(PierceShape.None, shape);
        }

        [TestMethod]
        public void Slash_AddsFivePointsPerRank_AndIsNeverScaled()
        {
            Assert.AreEqual(0.05, ElementalProficiencyMath.Magnitude(DamageType.Slash, 1, 1.25, 0.5), Tol);
            Assert.AreEqual(0.10, ElementalProficiencyMath.Magnitude(DamageType.Slash, 2, 1.25, 0.5), Tol);
            Assert.AreEqual(0.15, ElementalProficiencyMath.Magnitude(DamageType.Slash, 3, 1.25, 0.5), Tol);

            // a Slash spell takes no damage multiplier at all - crit chance is its whole effect
            Assert.AreEqual(1.0, Mult(DamageType.Slash, 3, PierceShape.Flat), Tol);
        }

        [TestMethod]
        public void Pierce_FlatMultiplier_AppliesOnlyWhenTheHitChoseFlat()
        {
            Assert.AreEqual(1.30, Mult(DamageType.Pierce, 3, PierceShape.Flat), Tol);
            Assert.AreEqual(1.0, Mult(DamageType.Pierce, 3, PierceShape.Ignore), Tol, "the ignore is already in the innate term");
            Assert.AreEqual(1.0, Mult(DamageType.Pierce, 3, PierceShape.None), Tol);

            // affinity and gear scale the flat bonus: 0.10 * 1.25 * 1.2
            Assert.AreEqual(1.15, Mult(DamageType.Pierce, 1, PierceShape.Flat, affinity: 2.0, gear: 0.2), Tol);
        }

        [TestMethod]
        public void Pierce_HeavilyResisted_r03_IgnoreWins()
        {
            var calls = new int[1];
            var over = DecidePierce(0.3, 0.30, out var shape, rollCalls: calls);

            Assert.AreEqual(0, calls[0], "no Inversion chance: the RNG is never drawn");
            Assert.AreEqual(PierceShape.Ignore, shape);
            Assert.AreEqual(0.3 + 0.3 * 0.7, over.Value, Tol, "r' = r + p(1 - r)");
            Assert.AreEqual(0.51, PierceFactor(0.3, 0.30, out _), Tol, "a 1.7x gain beats the flat 1.3x, and no flat on top");
        }

        [TestMethod]
        public void Pierce_LightlyResisted_r08_FlatWins()
        {
            var calls = new int[1];
            var over = DecidePierce(0.8, 0.30, out var shape, rollCalls: calls);

            Assert.AreEqual(0, calls[0], "no Inversion chance: the RNG is never drawn");
            Assert.AreEqual(PierceShape.Flat, shape, "0.86 / 0.8 = 1.075 loses to 1.3");
            Assert.IsNull(over, "the flat shape leaves the innate term alone");
            Assert.AreEqual(0.8 * 1.3, PierceFactor(0.8, 0.30, out _), Tol);
        }

        [TestMethod]
        public void Pierce_Unresisted_r10_And_Weak_r15_FlatWins()
        {
            foreach (var r in new[] { 1.0, 1.5 })
            {
                var over = DecidePierce(r, 0.30, out var shape);
                Assert.AreEqual(PierceShape.Flat, shape, r.ToString());
                Assert.IsNull(over, r.ToString());
                Assert.AreEqual(r * 1.3, PierceFactor(r, 0.30, out _), Tol, r.ToString());
            }
        }

        [TestMethod]
        public void Pierce_Immune_GetsNeither_AndNeverRolls()
        {
            foreach (var r in new[] { 0.0, -0.5 })
            {
                var calls = new int[1];
                var over = DecidePierce(r, 0.30, out var shape, chance: 1.0, hollow: 0.5, others: 2.0, rollCalls: calls);

                Assert.IsNull(over, r.ToString());
                Assert.AreEqual(PierceShape.None, shape, r.ToString());
                Assert.AreEqual(0, calls[0], r.ToString());
                Assert.AreEqual(1.0, ElementalProficiencySpellMath.DamageMultiplier(DamageType.Pierce, 0.30, shape), Tol);
            }
        }

        [TestMethod]
        public void Pierce_InversionSubstitution_ToAWeakType_TakesTheFlatBonus()
        {
            // resisted to pierce (0.3), weakest to everything else (1.5): the proc lifts the innate term to 1.5,
            // where there is no resistance left to ignore, so the hit must take the flat bonus (the reviewer's case)
            var calls = new int[1];
            var over = DecidePierce(0.3, 0.30, out var shape, chance: 1.0, others: 1.5, rollCalls: calls);

            Assert.AreEqual(PierceShape.Flat, shape);
            Assert.AreEqual(1.5, over.Value, Tol, "the substituted innate, without a pierce fold");
            Assert.AreEqual(1.5 * 1.3, PierceFactor(0.3, 0.30, out _, chance: 1.0, others: 1.5), Tol);
            Assert.AreEqual(1, calls[0], "one draw per hit, exactly as ArchmageResistanceMath.Decide draws for any positive chance");
        }

        [TestMethod]
        public void Pierce_InversionSubstitution_StillResisted_JudgesTheSubstitutedValue()
        {
            // pierce 0.2, weakest 0.3: the proc substitutes 0.3, and the ignore is judged on 0.3, not on 0.2
            var over = DecidePierce(0.2, 0.30, out var shape, chance: 1.0, others: 0.3);

            Assert.AreEqual(PierceShape.Ignore, shape);
            Assert.AreEqual(0.3 + 0.3 * 0.7, over.Value, Tol);
        }

        [TestMethod]
        public void Pierce_InversionChance_RollsOnce_AndAMissKeepsTheOriginal()
        {
            var calls = new int[1];
            Func<ResistanceType, double> innate = t => t == ResistanceType.Pierce ? 0.8 : 1.5;

            // a 0.5 chance against a roll of 0.9 misses: judged on the original 0.8, so flat, no override
            var over = ElementalProficiencySpellMath.DecideSpellInnate(true, ResistanceType.Pierce, 0.5, 0.0, 0.30,
                () => { calls[0]++; return 0.9; }, innate, out var shape);

            Assert.AreEqual(1, calls[0]);
            Assert.AreEqual(PierceShape.Flat, shape);
            Assert.IsNull(over);

            // the same chance against a roll of 0.1 lands
            over = ElementalProficiencySpellMath.DecideSpellInnate(true, ResistanceType.Pierce, 0.5, 0.0, 0.30,
                () => { calls[0]++; return 0.1; }, innate, out shape);

            Assert.AreEqual(2, calls[0]);
            Assert.AreEqual(1.5, over.Value, Tol);
            Assert.AreEqual(PierceShape.Flat, shape);
        }

        [TestMethod]
        public void Pierce_WithArcaneHollow_ComparesHollowAloneAgainstHollowPlusPierce()
        {
            // r 0.5, h 0.3: without = 0.65, with = 0.5 + 0.51 x 0.5 = 0.755, gain 1.1615 < 1.3 -> flat on top of Hollow
            var over = DecidePierce(0.5, 0.30, out var shape, hollow: 0.3);
            Assert.AreEqual(PierceShape.Flat, shape);
            Assert.AreEqual(0.65, over.Value, Tol, "Hollow still applies; only the pierce fold is withheld");

            // r 0.2, h 0.3: without = 0.44, with = 0.2 + 0.51 x 0.8 = 0.608, gain 1.38 > 1.3 -> ignore
            over = DecidePierce(0.2, 0.30, out shape, hollow: 0.3);
            Assert.AreEqual(PierceShape.Ignore, shape);
            Assert.AreEqual(0.608, over.Value, Tol);

            // Inversion and Hollow together: pierce 0.3 -> weakest 0.4, without 0.58, with 0.706, gain 1.217 -> flat
            over = DecidePierce(0.3, 0.30, out shape, chance: 1.0, hollow: 0.3, others: 0.4);
            Assert.AreEqual(PierceShape.Flat, shape);
            Assert.AreEqual(0.58, over.Value, Tol);
        }

        [TestMethod]
        public void Pierce_Invariant_ExactlyOneShape_AndAlwaysTheLarger_AcrossTheRange()
        {
            foreach (var chance in new[] { 0.0, 1.0 })
            foreach (var hollow in new[] { 0.0, 0.3 })
            foreach (var others in new[] { 0.2, 0.9, 1.5 })
            {
                for (var r = 0.05; r <= 2.0; r += 0.05)
                {
                    var label = $"r {r:0.00} chance {chance} hollow {hollow} others {others}";

                    var over = DecidePierce(r, 0.30, out var shape, chance, hollow, others);
                    Assert.AreNotEqual(PierceShape.None, shape, label);

                    var inverted = chance >= 1.0 ? Math.Max(r, others) : r;
                    var without = ArchmageResistanceMath.HollowInnate(inverted, hollow);
                    var with = ArchmageResistanceMath.HollowInnate(inverted, ElementalProficiencySpellMath.CombineIgnoreFractions(hollow, 0.30));

                    var innateUsed = over ?? r;
                    var factor = innateUsed * ElementalProficiencySpellMath.DamageMultiplier(DamageType.Pierce, 0.30, shape);

                    Assert.AreEqual(Math.Max(without * 1.3, with), factor, 1e-9, label + ": the larger of the two");

                    // never both: a flat hit's innate carries no pierce fold, an ignore hit gets no flat multiplier
                    if (shape == PierceShape.Flat)
                        Assert.AreEqual(without, innateUsed, 1e-12, label);
                    else
                        Assert.AreEqual(with, innateUsed, 1e-12, label);
                }
            }
        }

        [TestMethod]
        public void Pierce_HasNoCliffAtAnInnateOfOne()
        {
            var below = PierceFactor(0.99, 0.30, out _);
            var at = PierceFactor(1.0, 0.30, out _);

            Assert.AreEqual(0.99 * 1.3, below, Tol, "the old rule gave 0.99 + 0.003 here, about +0.3%");
            Assert.IsTrue(at - below < 0.02, "continuous across 1.0");
        }

        [TestMethod]
        public void DecideSpellInnate_NonPierceType_IsExactlyDecide_WithNoShape()
        {
            Func<ResistanceType, double> innate = t => t == ResistanceType.Fire ? 0.5 : 1.5;

            foreach (var type in new[] { ResistanceType.Fire, ResistanceType.Cold, ResistanceType.Nether })
            {
                var expected = ArchmageResistanceMath.Decide(true, type, true, 1.0, 0.3, () => 0.0, innate);
                var actual = ElementalProficiencySpellMath.DecideSpellInnate(true, type, 1.0, 0.3, 0.30, () => 0.0, innate, out var shape);

                Assert.AreEqual(expected, actual, type.ToString());
                Assert.AreEqual(PierceShape.None, shape, type.ToString());
            }
        }

        [TestMethod]
        public void DecideSpellInnate_NonPveTarget_IsNull_WithNoShape()
        {
            var calls = new int[1];
            var over = ElementalProficiencySpellMath.DecideSpellInnate(false, ResistanceType.Pierce, 1.0, 0.3, 0.30,
                () => { calls[0]++; return 0.0; }, t => 0.5, out var shape);

            Assert.IsNull(over);
            Assert.AreEqual(PierceShape.None, shape);
            Assert.AreEqual(0, calls[0]);
        }

        [TestMethod]
        public void ChoosePierceShape_TieGoesToFlat_AndNoMagnitudeIsNone()
        {
            Assert.AreEqual(PierceShape.Flat, ElementalProficiencySpellMath.ChoosePierceShape(0.3, 1.0, 1.3));
            Assert.AreEqual(PierceShape.Ignore, ElementalProficiencySpellMath.ChoosePierceShape(0.3, 1.0, 1.31));
            Assert.AreEqual(PierceShape.None, ElementalProficiencySpellMath.ChoosePierceShape(0.0, 0.5, 0.5));
            Assert.AreEqual(PierceShape.None, ElementalProficiencySpellMath.ChoosePierceShape(0.3, 0.0, 0.0));
        }

        [TestMethod]
        public void CombineIgnoreFractions_IsOneMinusProductOfComplements()
        {
            Assert.AreEqual(0.0, ElementalProficiencySpellMath.CombineIgnoreFractions(0.0, 0.0), Tol);
            Assert.AreEqual(0.3, ElementalProficiencySpellMath.CombineIgnoreFractions(0.0, 0.3), Tol);
            Assert.AreEqual(0.3, ElementalProficiencySpellMath.CombineIgnoreFractions(0.3, 0.0), Tol);
            Assert.AreEqual(1.0 - 0.7 * 0.8, ElementalProficiencySpellMath.CombineIgnoreFractions(0.3, 0.2), Tol);
            Assert.AreEqual(1.0, ElementalProficiencySpellMath.CombineIgnoreFractions(1.7, 0.2), Tol, "clamped");
        }

        [TestMethod]
        public void OtherDamageTypes_GetNothing()
        {
            foreach (var type in new[] { DamageType.Nether, DamageType.Health, DamageType.Cold, DamageType.Electric, DamageType.Acid, DamageType.Bludgeon })
                Assert.AreEqual(1.0, ElementalProficiencySpellMath.DamageMultiplier(type, 0.3, PierceShape.Flat), Tol, type.ToString());
        }

        [TestMethod]
        public void IneligibleTargets_AreExcludedByTheSharedPredicate()
        {
            Assert.IsFalse(ElementalProficiencyMath.IsEligibleTarget(null));

            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ElementalProficiency.cs"),
                "public int GetElementalProficiencyRank(");
            StringAssert.Contains(body, "ElementalProficiencyMath.IsEligibleTarget(target)");
            StringAssert.Contains(body, "TryGetClassAbility(ClassAbilityId.ElementalRend");
        }

        [TestMethod]
        public void Wiring_SpellProjectile_AppliesInsideDamageCalc_NotThroughTheHitHook()
        {
            var spell = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs");

            var wall = spell.IndexOf("classAbilityCaster.ApplyVolatileWall(this, target)", StringComparison.Ordinal);
            var fire = spell.IndexOf("classAbilityCaster.GetElementalProficiencyDamageMod(Spell.DamageType, target, elementalPierceShape)", StringComparison.Ordinal);
            Assert.IsTrue(wall > 0 && fire > wall, "Fire/Pierce-flat multiplier sits after Volatile Wall");

            // both of those are inside the player-caster, non-player-target block
            var block = spell.LastIndexOf("if (sourceCreature is Player classAbilityCaster && target is not Player)", fire, StringComparison.Ordinal);
            Assert.IsTrue(block > 0 && fire - block < 3000);

            StringAssert.Contains(spell, "if (sourceCreature is Player proficiencyCritCaster)");
            StringAssert.Contains(spell, "criticalChance += proficiencyCritCaster.GetElementalProficiencySlashCritBonus(Spell.DamageType, target);");

            // the existing pinned crit line is untouched
            StringAssert.Contains(spell, "criticalChance = PvpContextTuning.ApplyCritChance(PvpChokePoint.CC2, source, target, PvpHitProfile.ForSpell(Spell.School, SpellType), criticalChance);");

            Assert.AreEqual(1, CountOf(spell, "GetArchmageInnateOverride("), "Pierce rides the existing single override call");

            // the one decision feeds the final-damage site: decided at the override call, read after it
            var decide = spell.IndexOf("archmageCaster.GetArchmageInnateOverride(target, resistanceType, true, out elementalPierceShape)", StringComparison.Ordinal);
            Assert.IsTrue(decide > 0 && decide < fire, "the Pierce shape is decided before the final-damage site reads it");
            Assert.AreEqual(2, CountOf(spell, "elementalPierceShape)"), "written once by the override call, read once by the damage mod");

            var damageTarget = PooledLootSourceText.MethodBody(spell, "public void DamageTarget(");
            StringAssert.Contains(damageTarget, "LastAppliedDamage = amount;");
            StringAssert.Contains(spell, "public uint LastAppliedDamage { get; private set; }");
        }

        [TestMethod]
        public void Wiring_PierceIsDecidedOnceInTheOverride_AndTheDamageModReadsThatDecision()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ArchmageResistance.cs");
            var body = PooledLootSourceText.MethodBody(src, "public double? GetArchmageInnateOverride(");

            // the FIRST overload (the one MethodBody reads) is the one that reports the shape
            var signature = src.Substring(src.IndexOf("public double? GetArchmageInnateOverride(", StringComparison.Ordinal), 220);
            StringAssert.Contains(signature, "out ElementalProficiencySpellMath.PierceShape pierceShape");

            StringAssert.Contains(body, "GetElementalProficiencyPierceMagnitude(originalType, target)");
            StringAssert.Contains(body, "ElementalProficiencySpellMath.DecideSpellInnate(");
            Assert.IsTrue(body.IndexOf("DecideSpellInnate(", StringComparison.Ordinal) < body.IndexOf("return ArchmageResistanceMath.Decide(", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("CombineIgnoreFractions"), "the fold lives in the pure decision, not the Player method");

            var mod = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ElementalProficiency.cs"),
                "public float GetElementalProficiencyDamageMod(");
            StringAssert.Contains(mod, "pierceShape)");
            Assert.IsFalse(mod.Contains("InnateResistance"), "the damage mod never re-derives the shape from the innate value");
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
