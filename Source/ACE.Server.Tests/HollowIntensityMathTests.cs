using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Hollow intensity (PropertyFloat 9012, HollowMath). Three contracts, one region each:
    ///   (a) every object that carries IgnoreMagicArmor/IgnoreMagicResist with no intensity behaves exactly as it
    ///       did when hollow was a bool - resolved 1.0, and every consumer yields the old literal;
    ///   (b) a partial intensity scales each enchantment term by (1 - i), and GetResistanceMod floors the
    ///       vulnerability at the weapon mod AFTER scaling;
    ///   (c) the PvP scalar form is bit-identical to the pre-intensity expressions at intensity 1.0.
    ///
    /// No Player is constructed (that fails in this host's static initializer) and no PropertyManager key is read:
    /// the PvP scalar is a delegate the non-PvP branches must never invoke, which <see cref="NeverRead"/> enforces.
    /// </summary>
    [TestClass]
    public class HollowIntensityMathTests
    {
        private const float Tolerance = 1e-5f;

        private static uint nextGuid = 0x70000900;

        private static double NeverRead()
        {
            Assert.Fail("a non-PvP hollow path read the PvP scalar");
            return double.NaN;
        }

        private static WorldObject CreateItem()
        {
            var biota = new Biota
            {
                Id = nextGuid++,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        private static T Hollow<T>(T o, double? intensity = null) where T : WorldObject
        {
            o.IgnoreMagicArmor = true;
            o.IgnoreMagicResist = true;
            o.HollowIntensity = intensity;
            return o;
        }

        private static void AddEnchantment(WorldObject target, SpellCategory category, EnchantmentTypeFlags type, uint key, float value)
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = 1,
                SpellCategory = category,
                LayerId = 1,
                PowerLevel = 250,
                StartTime = 0,
                Duration = 60.0,
                StatModType = type,
                StatModKey = key,
                StatModValue = value,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

            target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, target.BiotaDatabaseLock);
        }

        /// <summary>A defender carrying +50 life armor (Armor Self), a slash protection 0.5 and a slash vulnerability 1.6.</summary>
        private static Creature BuffedDefender(int baseArmor = 100)
        {
            var defender = TestCreatures.CreateDefender(baseArmor: baseArmor);

            AddEnchantment(defender, SpellCategory.ArmorRaising, EnchantmentTypeFlags.BodyArmorValue | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive, 0, 50.0f);
            AddEnchantment(defender, SpellCategory.SlashProtection, EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative, (uint)PropertyFloat.ResistSlash, 0.5f);
            AddEnchantment(defender, SpellCategory.SlashVulnerability, EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative, (uint)PropertyFloat.ResistSlash, 1.6f);

            return defender;
        }

        /// <summary>An armor piece: AL 200, slash RL 1.0, +100 impenetrability, +0.5 slash bane.</summary>
        private static WorldObject BuffedArmor()
        {
            var armor = CreateItem();
            armor.SetProperty(PropertyInt.ArmorLevel, 200);
            armor.SetProperty(PropertyFloat.ArmorModVsSlash, 1.0);

            AddEnchantment(armor, SpellCategory.ArmorValueRaising, EnchantmentTypeFlags.Int | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive, (uint)PropertyInt.ArmorLevel, 100.0f);
            AddEnchantment(armor, SpellCategory.ExtraArmorValueRaising, EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive, (uint)PropertyFloat.ArmorModVsSlash, 0.5f);

            return armor;
        }

        private static Creature_BodyPart ChestOf(Creature defender)
        {
            return new Creature_BodyPart(defender, defender.Biota.PropertiesBodyPart.First());
        }

        // ---- (a) 100% is unchanged ------------------------------------------------------------------

        [TestMethod]
        public void Resolve_FlagOnly_IsExactlyOne_FromCreatureWeaponOrBoth()
        {
            var creature = Hollow(TestCreatures.CreateAttacker());
            var weapon = Hollow(CreateItem());

            foreach (var flag in new[] { PropertyBool.IgnoreMagicArmor, PropertyBool.IgnoreMagicResist })
            {
                Assert.AreEqual(1.0, HollowMath.Resolve(null, creature, flag), $"creature {flag}");
                Assert.AreEqual(1.0, HollowMath.Resolve(weapon, TestCreatures.CreateAttacker(), flag), $"weapon {flag}");
                Assert.AreEqual(1.0, HollowMath.Resolve(weapon, creature, flag), $"both {flag}");
                Assert.AreEqual(0.0, HollowMath.Resolve(CreateItem(), TestCreatures.CreateAttacker(), flag), $"neither {flag}");
                Assert.AreEqual(0.0, HollowMath.Resolve(null, null, flag), $"nulls {flag}");
            }
        }

        [TestMethod]
        public void ScaleHelpers_AtFull_NonPvp_ReturnTheOldLiterals_WithoutReadingConfig()
        {
            foreach (var x in new[] { 0, 1, 7, 45, 250, -33 })
            {
                Assert.AreEqual(0, HollowMath.ScaleInt(x, 1.0, false, NeverRead));
                Assert.AreEqual(0, HollowMath.ScaleArmorInt(x, 1.0, false, NeverRead));
                Assert.AreEqual(0, HollowMath.ScaleInt(x, 1.0, false, null), "a null scalar is never dereferenced off the PvP branch");
            }

            foreach (var x in new[] { 0.0f, 0.3f, 1.25f, -0.75f })
            {
                Assert.AreEqual<float>(0.0f, HollowMath.ScaleFloat(x, 1.0, false, NeverRead));
            }

            Assert.IsTrue(HollowMath.IsFullResistBypass(1.0, false, NeverRead));
        }

        [TestMethod]
        public void CreatureScaledHelpers_OnANonPlayer_AreTheOldZeroes_WithNoSeededConfig()
        {
            var creature = TestCreatures.CreateAttacker();

            // the pre-intensity one-argument call shape, and the explicit full intensity
            Assert.AreEqual<float>(0.0f, creature.IgnoreMagicArmorScaled(12.5f));
            Assert.AreEqual(0, creature.IgnoreMagicResistScaled(40));
            Assert.AreEqual<float>(0.0f, creature.IgnoreMagicArmorScaled(12.5f, 1.0));
            Assert.AreEqual(0, creature.IgnoreMagicResistScaled(40, 1.0));
        }

        [TestMethod]
        public void GetResistanceMod_FlagOnlyWeaponOrAttacker_IsStillTheEarlyReturn()
        {
            var target = BuffedDefender();
            HuntersMarkAbility.ApplyMark(target, CreateItem(), 0.05, 10.0);

            // control: without hollow the protection and vulnerability are live, so the early return is discriminating
            Assert.AreEqual(Math.Max(1.6f, 1.2f) * 0.5f * 1.05f, target.GetResistanceMod(DamageType.Slash, null, null, 1.2f), Tolerance);

            Assert.AreEqual(2.5f * 1.05f, target.GetResistanceMod(DamageType.Slash, null, Hollow(CreateItem()), 2.5f), Tolerance);
            Assert.AreEqual(1.2f * 1.05f, target.GetResistanceMod(DamageType.Slash, Hollow(TestCreatures.CreateAttacker()), null, 1.2f), Tolerance);
        }

        [TestMethod]
        public void CreatureDefenderPath_FlagOnlyAttacker_StillZeroesImpenBanesAndLifeArmor()
        {
            var defender = BuffedDefender(baseArmor: 100);
            var armor = BuffedArmor();
            var chest = ChestOf(defender);
            var layers = new List<WorldObject> { armor };

            // control: base 100 + life armor 50, piece (200 + 100) * (1.0 + 0.5)
            Assert.AreEqual(150.0f + 450.0f, chest.GetEffectiveArmorVsType(DamageType.Slash, layers, TestCreatures.CreateAttacker(), null), Tolerance);

            // flag-only attacker: base 100 + 0, piece 200 * 1.0
            Assert.AreEqual(100.0f + 200.0f, chest.GetEffectiveArmorVsType(DamageType.Slash, layers, Hollow(TestCreatures.CreateAttacker()), null), Tolerance);

            // flag-only weapon, same result
            Assert.AreEqual(100.0f + 200.0f, chest.GetEffectiveArmorVsType(DamageType.Slash, layers, TestCreatures.CreateAttacker(), Hollow(CreateItem())), Tolerance);
        }

        [TestMethod]
        public void PlayerDefenderPath_NonPlayerFlagOnlyAttacker_StillZeroesImpenBanesAndLifeArmor()
        {
            // Monster_Melee's GetArmorMod is the monster-attacks-player path; it reads only the defender's
            // EnchantmentManager, so a bare creature stands in for the player defender here.
            var defender = BuffedDefender();
            var armor = BuffedArmor();
            var clean = TestCreatures.CreateAttacker();
            var hollow = Hollow(TestCreatures.CreateAttacker());

            Assert.AreEqual(450.0f, clean.GetArmorMod(armor, DamageType.Slash, 0.0), Tolerance, "control piece");
            Assert.AreEqual(200.0f, hollow.GetArmorMod(armor, DamageType.Slash, 1.0), Tolerance, "full hollow piece");

            var noLayers = new List<WorldObject>();
            Assert.AreEqual(SkillFormula.CalcArmorMod(50.0f), clean.GetArmorMod(defender, DamageType.Slash, noLayers, null), Tolerance, "control life armor");
            Assert.AreEqual(SkillFormula.CalcArmorMod(0.0f), hollow.GetArmorMod(defender, DamageType.Slash, noLayers, null), Tolerance, "full hollow life armor");
        }

        // ---- (b) partial intensity ------------------------------------------------------------------

        [TestMethod]
        public void ScaleHelpers_AtHalf_NonPvp_RoundIntsAndScaleFloats()
        {
            foreach (var x in new[] { 1, 7, 45, 50, 101, -33 })
            {
                Assert.AreEqual((int)Math.Round(x * 0.5), HollowMath.ScaleInt(x, 0.5, false, NeverRead), $"int {x}");
                Assert.AreEqual((int)Math.Round(x * 0.5), HollowMath.ScaleArmorInt(x, 0.5, false, NeverRead), $"armor int {x}");
            }

            foreach (var x in new[] { 0.3f, 1.25f, -0.75f })
                Assert.AreEqual<float>((float)(x * 0.5), HollowMath.ScaleFloat(x, 0.5, false, NeverRead), $"float {x}");

            // zero intensity leaves the term whole, and a partial never takes the resist early return
            Assert.AreEqual(45, HollowMath.ScaleInt(45, 0.0, false, NeverRead));
            Assert.AreEqual<float>(0.3f, HollowMath.ScaleFloat(0.3f, 0.0, false, NeverRead));
            Assert.IsFalse(HollowMath.IsFullResistBypass(0.5, false, NeverRead));
            Assert.IsFalse(HollowMath.IsFullResistBypass(0.0, false, NeverRead));
        }

        [TestMethod]
        public void Resolve_MaxCombinesWeaponAndAttacker()
        {
            // weapon 0.3 + flag-only creature -> the creature's full 1.0 wins
            Assert.AreEqual(1.0, HollowMath.Resolve(Hollow(CreateItem(), 0.3), Hollow(TestCreatures.CreateAttacker()), PropertyBool.IgnoreMagicResist));

            // creature 0.3 + clean weapon -> 0.3
            Assert.AreEqual(0.3, HollowMath.Resolve(CreateItem(), Hollow(TestCreatures.CreateAttacker(), 0.3), PropertyBool.IgnoreMagicArmor));

            // both partial -> the larger
            Assert.AreEqual(0.6, HollowMath.Resolve(Hollow(CreateItem(), 0.3), Hollow(TestCreatures.CreateAttacker(), 0.6), PropertyBool.IgnoreMagicArmor));

            // the intensity is read ONLY alongside the flag
            var intensityOnly = CreateItem();
            intensityOnly.HollowIntensity = 0.8;
            Assert.AreEqual(0.0, HollowMath.Resolve(intensityOnly, null, PropertyBool.IgnoreMagicArmor));

            // per-flag: an object with only IgnoreMagicArmor contributes nothing to IgnoreMagicResist
            var armorOnly = CreateItem();
            armorOnly.IgnoreMagicArmor = true;
            armorOnly.HollowIntensity = 0.4;
            Assert.AreEqual(0.4, HollowMath.Resolve(armorOnly, null, PropertyBool.IgnoreMagicArmor));
            Assert.AreEqual(0.0, HollowMath.Resolve(armorOnly, null, PropertyBool.IgnoreMagicResist));
        }

        [TestMethod]
        public void Resolve_ClampsOutOfRangeAndMapsNaNToOne()
        {
            Assert.AreEqual(1.0, HollowMath.ClampOrOne(null));
            Assert.AreEqual(1.0, HollowMath.ClampOrOne(double.NaN));
            Assert.AreEqual(1.0, HollowMath.ClampOrOne(1.7));
            Assert.AreEqual(1.0, HollowMath.ClampOrOne(double.PositiveInfinity));
            Assert.AreEqual(0.0, HollowMath.ClampOrOne(-0.2));
            Assert.AreEqual(0.25, HollowMath.ClampOrOne(0.25));

            Assert.AreEqual(1.0, HollowMath.Resolve(Hollow(CreateItem(), double.NaN), null, PropertyBool.IgnoreMagicArmor));
            Assert.AreEqual(1.0, HollowMath.Resolve(Hollow(CreateItem(), 3.0), null, PropertyBool.IgnoreMagicArmor));
            Assert.AreEqual(0.0, HollowMath.Resolve(Hollow(CreateItem(), -1.0), null, PropertyBool.IgnoreMagicArmor), "a negative intensity is not hollow at all");

            // the scale helpers clamp a raw out-of-range argument the same way
            Assert.AreEqual(0, HollowMath.ScaleInt(45, 2.0, false, NeverRead));
            Assert.AreEqual(45, HollowMath.ScaleInt(45, -2.0, false, NeverRead));
            Assert.AreEqual(0, HollowMath.ScaleInt(45, double.NaN, false, NeverRead));
        }

        [TestMethod]
        public void GetResistanceMod_AtHalf_LiesStrictlyBetweenUnhollowedAndFull()
        {
            var target = BuffedDefender();

            var unhollowed = target.GetResistanceMod(DamageType.Slash, null, null);                  // 0.5 * 1.6 = 0.8
            var full = target.GetResistanceMod(DamageType.Slash, null, Hollow(CreateItem()));        // 1.0
            var half = target.GetResistanceMod(DamageType.Slash, null, Hollow(CreateItem(), 0.5));

            Assert.AreEqual(0.8f, unhollowed, Tolerance);
            Assert.AreEqual(1.0f, full, Tolerance);

            // protection 0.5 = rating 100 -> 50 -> 100/150; vulnerability 1.6 = rating 60 -> 30 -> 1.3
            Assert.AreEqual((100.0f / 150.0f) * 1.3f, half, Tolerance);
            Assert.IsTrue(half > unhollowed && half < full, $"half {half} must lie strictly between {unhollowed} and {full}");

            // from the attacker side too
            Assert.AreEqual(half, target.GetResistanceMod(DamageType.Slash, Hollow(TestCreatures.CreateAttacker(), 0.5), null), Tolerance);
        }

        [TestMethod]
        public void GetResistanceMod_AtHalf_FloorsTheVulnerabilityAtTheWeaponModAfterScaling()
        {
            var target = BuffedDefender();
            var halfWeapon = Hollow(CreateItem(), 0.5);

            // scaled vulnerability 1.3 loses to the 1.4 rending weapon; floor-before-scaling would have kept 1.6 and
            // then scaled it to 1.3, so this discriminates the order
            Assert.AreEqual((100.0f / 150.0f) * 1.4f, target.GetResistanceMod(DamageType.Slash, null, halfWeapon, 1.4f), Tolerance);

            // a weapon mod below the scaled vulnerability does not displace it
            Assert.AreEqual((100.0f / 150.0f) * 1.3f, target.GetResistanceMod(DamageType.Slash, null, halfWeapon, 1.1f), Tolerance);

            // and the mark still multiplies once, on the fall-through final return
            HuntersMarkAbility.ApplyMark(target, CreateItem(), 0.05, 10.0);
            Assert.AreEqual((100.0f / 150.0f) * 1.4f * 1.05f, target.GetResistanceMod(DamageType.Slash, null, halfWeapon, 1.4f), Tolerance);
        }

        [TestMethod]
        public void CreatureDefenderPath_AtHalf_ScalesImpenBanesAndLifeArmor()
        {
            var defender = BuffedDefender(baseArmor: 100);
            var chest = ChestOf(defender);
            var layers = new List<WorldObject> { BuffedArmor() };

            // base 100 + round(50 * 0.5) = 125; piece (200 + round(100 * 0.5)) * (1.0 + 0.25) = 312.5
            Assert.AreEqual(125.0f + 312.5f, chest.GetEffectiveArmorVsType(DamageType.Slash, layers, Hollow(TestCreatures.CreateAttacker(), 0.5), null), Tolerance);
        }

        [TestMethod]
        public void PlayerDefenderPath_NonPlayerAttackerAtHalf_ScalesImpenBanesAndLifeArmor()
        {
            var defender = BuffedDefender();
            var halfAttacker = Hollow(TestCreatures.CreateAttacker(), 0.5);

            Assert.AreEqual(312.5f, halfAttacker.GetArmorMod(BuffedArmor(), DamageType.Slash, 0.5), Tolerance);
            Assert.AreEqual(SkillFormula.CalcArmorMod(25.0f), halfAttacker.GetArmorMod(defender, DamageType.Slash, new List<WorldObject>(), null), Tolerance);
        }

        // ---- (c) PvP scalar form --------------------------------------------------------------------

        private static float OldArmorScaled(float enchantments, double scalar)
        {
            if (scalar != 1.0)
                return (float)(enchantments * (1.0 - scalar));
            else
                return 0.0f;
        }

        private static int OldResistScaled(int enchantments, double scalar)
        {
            if (scalar != 1.0)
                return (int)Math.Round(enchantments * (1.0 - scalar));
            else
                return 0;
        }

        [TestMethod]
        public void PvpAtFullIntensity_IsBitIdenticalToThePreIntensityExpressions()
        {
            foreach (var scalar in new[] { 1.0, 0.5, 0.0, 0.3, 0.7 })
            {
                Func<double> read = () => scalar;

                foreach (var x in new[] { 0.0f, 0.3f, 1.25f, -0.75f, 0.1f, 7.7f })
                {
                    var expected = OldArmorScaled(x, scalar);
                    var actual = HollowMath.ScaleFloat(x, 1.0, true, read);
                    Assert.AreEqual(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual), $"float x={x} scalar={scalar}");
                }

                foreach (var x in new[] { 0, 1, 7, 45, 50, 101, 255, -33 })
                {
                    Assert.AreEqual(OldResistScaled(x, scalar), HollowMath.ScaleInt(x, 1.0, true, read), $"resist int x={x} scalar={scalar}");

                    // old armor-level sites rounded the float result of IgnoreMagicArmorScaled
                    Assert.AreEqual((int)Math.Round(OldArmorScaled(x, scalar)), HollowMath.ScaleArmorInt(x, 1.0, true, read), $"armor int x={x} scalar={scalar}");
                }

                Assert.AreEqual(scalar == 1.0, HollowMath.IsFullResistBypass(1.0, true, read), $"early return scalar={scalar}");
            }
        }

        [TestMethod]
        public void PvpPartialIntensity_MultipliesTheScalar()
        {
            Func<double> full = () => 1.0;
            Func<double> half = () => 0.5;

            // eff = scalar * i
            Assert.AreEqual((int)Math.Round(50 * (1.0 - 0.5)), HollowMath.ScaleInt(50, 0.5, true, full));
            Assert.AreEqual((int)Math.Round(50 * (1.0 - 0.25)), HollowMath.ScaleInt(50, 0.5, true, half));
            Assert.AreEqual<float>((float)(0.8f * (1.0 - 0.25)), HollowMath.ScaleFloat(0.8f, 0.5, true, half));
            Assert.IsFalse(HollowMath.IsFullResistBypass(0.5, true, full));

            // the scalar is never clamped: a scalar of 2.0 at half intensity is eff 1.0, the full bypass
            Assert.IsTrue(HollowMath.IsFullResistBypass(0.5, true, () => 2.0));
        }
    }
}
