using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// DEFENSIVE WEAPON (owner ruling 2026-10-25): Arcane Defender renamed, widened from caster-only to every
    /// weapon class, with a PER-ITEM ceiling (100 armor level on a one-handed melee carrier, 200 on every other
    /// class), now ADDING to an equipped shield after the Shield-skill cap and counting toward Thorns.
    ///
    /// THE INVARIANT this file exists for: for any given item, every site that turns the stored fraction into
    /// a magnitude - the combat read, the tinker-set read, the appraisal line, the intensity percent, the
    /// market row, the quality-tier sum and the inverse FractionFor - reports the SAME magnitude. A site that
    /// forgot the carrier item would read the full 200 on a one-handed sword, and only a per-item sweep sees it.
    ///
    /// The combat cases drive the REAL Creature.GetShieldMod and Player.ApplyThornsReflect on uninitialized
    /// Players seeded with exactly what those paths read (the same harness shape CreatureCombatTests and
    /// ThornsCloseRangeTests use). Every PropertyManager key read here is seeded per test and restored after.
    /// </summary>
    [TestClass]
    public class DefensiveWeaponTests
    {
        private static readonly string[] SeededBools = { "weapon_mods_enabled", "shield_applies_in_noncombat", "class_abilities_enabled", "equipment_mods_enabled" };
        private static readonly string[] SeededDoubles = { "weapon_mod_magnitude_scale", "class_ability_thorns_max_range", "class_ability_thorns_percent_per_rank" };

        private static uint nextGuid = 0x7E900000;
        private static uint nextWcid = 997500;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void SeedTunables()
        {
            RestoreTunables();

            PropertyManager.ModifyBool("weapon_mods_enabled", true);
            PropertyManager.ModifyBool("equipment_mods_enabled", true);
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            foreach (var key in SeededBools)
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);

            foreach (var key in SeededDoubles)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);
        }

        private static WeaponModDefinition Definition => WeaponModRegistry.Get(WeaponModId.DefensiveWeapon);

        // ---------------- items ----------------

        private static WorldObject OneHandedSword(double? fraction = 1.0) => Carrier(ItemType.MeleeWeapon, CombatUse.Melee, EquipMask.MeleeWeapon, EquipMask.MeleeWeapon, ParentLocation.RightHand, fraction);

        private static WorldObject TwoHandedSword(double? fraction = 1.0) => Carrier(ItemType.MeleeWeapon, CombatUse.TwoHanded, EquipMask.TwoHanded, EquipMask.TwoHanded, ParentLocation.RightHand, fraction,
            extra: new Dictionary<PropertyInt, int> { { PropertyInt.WeaponSkill, (int)Skill.TwoHandedCombat } });

        private static WorldObject Bow(double? fraction = 1.0) => Carrier(ItemType.MissileWeapon, CombatUse.Missile, EquipMask.MissileWeapon, EquipMask.MissileWeapon, ParentLocation.RightHand, fraction);

        private static WorldObject Wand(double? fraction = 1.0) => Carrier(ItemType.Caster, null, EquipMask.Held, EquipMask.Held, ParentLocation.RightHand, fraction);

        private static WorldObject OffHandSword(double? fraction = 1.0) => Carrier(ItemType.MeleeWeapon, CombatUse.Melee, EquipMask.MeleeWeapon | EquipMask.Shield, EquipMask.Shield, ParentLocation.LeftHand, fraction);

        private static WorldObject Carrier(ItemType itemType, CombatUse? combatUse, EquipMask validLocations, EquipMask wielded, ParentLocation parent, double? fraction,
            Dictionary<PropertyInt, int> extra = null)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ValidLocations, (int)validLocations },
                { PropertyInt.CurrentWieldedLocation, (int)wielded },
                { PropertyInt.ParentLocation, (int)parent },
            };

            if (extra != null)
            {
                foreach (var kvp in extra)
                    ints[kvp.Key] = kvp.Value;
            }

            var floats = fraction == null ? null : new Dictionary<PropertyFloat, double> { { PropertyFloat.WeaponModDefensiveWeapon, fraction.Value } };

            return WeaponModTestKit.MakeWeapon(itemType, combatUse, ints, floats);
        }

        private static WorldObject Shield(int armorLevel = 100, bool thorns = false)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                    { PropertyInt.ArmorLevel, armorLevel },
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.Shield },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.ArmorModVsSlash, 1.0 },
                },
            };

            if (thorns)
                weenie.PropertiesFloat[PropertyFloat.GearModThorns] = 1.0;

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A body-armor piece carrying the Thorns equipment mod, so Thorns has an owner without a shield.</summary>
        private static WorldObject ThornsArmor()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.CurrentWieldedLocation, (int)EquipMask.ChestArmor } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.GearModThorns, 1.0 } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        // ---------------- the ceiling ----------------

        [TestMethod]
        public void CeilingFactor_OneHandedMeleeIsHalf_EveryOtherClassIsFull()
        {
            Assert.AreEqual(0.5, DefensiveWeaponMath.CeilingFactor(WeaponClass.Melee, twoHanded: false), 1e-12);
            Assert.AreEqual(1.0, DefensiveWeaponMath.CeilingFactor(WeaponClass.Melee, twoHanded: true), 1e-12);
            Assert.AreEqual(1.0, DefensiveWeaponMath.CeilingFactor(WeaponClass.Missile, twoHanded: false), 1e-12);
            Assert.AreEqual(1.0, DefensiveWeaponMath.CeilingFactor(WeaponClass.Caster, twoHanded: false), 1e-12);
            Assert.AreEqual(1.0, DefensiveWeaponMath.CeilingFactor((WorldObject)null), 1e-12, "a null item reads the full registry ceiling");

            Assert.AreEqual(200.0, Definition.MaxRoll, 1e-12, "the registry carries the LARGEST per-item ceiling");
            Assert.AreEqual(200.0, Definition.MaxRollFor(null), 1e-12);
        }

        /// <summary>
        /// A per-item ceiling is only meaningful on a TIER B row: Tier B stores a fraction and resolves it against
        /// MaxRollFor(item) at every read, while a Tier A row adds a fixed magnitude into a native at roll time and
        /// never re-reads a ceiling, so a CeilingFactor there would be silently ignored. Positive control: the
        /// sweep is not vacuous, because Defensive Weapon carries one.
        /// </summary>
        [TestMethod]
        public void Registry_EveryRowWithACeilingFactorIsTierB()
        {
            var withFactor = WeaponModRegistry.AllMods.Where(m => m.CeilingFactor != null).ToList();

            CollectionAssert.Contains(withFactor.Select(m => m.Id).ToList(), WeaponModId.DefensiveWeapon, "control: Defensive Weapon carries a CeilingFactor");

            foreach (var mod in withFactor)
                Assert.AreEqual(WeaponModTier.B, mod.Tier, $"{mod.Id}: a CeilingFactor on a non-Tier-B row is never read");
        }

        /// <summary>
        /// Any one of the three markers makes an item two-handed, because retail and fork data each miss one of
        /// them on some weenies (see DefensiveWeaponMath.IsTwoHanded for the measured counts).
        /// </summary>
        [TestMethod]
        public void IsTwoHanded_AnyOneOfTheThreeMarkersCounts()
        {
            Assert.IsFalse(DefensiveWeaponMath.IsTwoHanded(EquipMask.MeleeWeapon, Skill.HeavyWeapons, CombatUse.Melee), "a plain one-handed sword");
            Assert.IsTrue(DefensiveWeaponMath.IsTwoHanded(EquipMask.TwoHanded, Skill.None, CombatUse.Melee), "ValidLocations alone (the fork weenies with no WeaponSkill)");
            Assert.IsTrue(DefensiveWeaponMath.IsTwoHanded(EquipMask.MeleeWeapon, Skill.TwoHandedCombat, CombatUse.Melee), "WeaponSkill alone (retail 46830, a Nodachi with the MeleeWeapon slot)");
            Assert.IsTrue(DefensiveWeaponMath.IsTwoHanded(null, Skill.None, CombatUse.TwoHanded), "CombatUse alone");
            Assert.IsFalse(DefensiveWeaponMath.IsTwoHanded(null, Skill.None, null), "no markers at all");
            Assert.IsFalse(DefensiveWeaponMath.IsTwoHanded((WorldObject)null));
        }

        [TestMethod]
        public void Ceiling_Is100OnAOneHandedMeleeCarrier_And200OnEveryOtherClass()
        {
            var cases = new (string Name, WorldObject Item, double Ceiling)[]
            {
                ("one-handed sword", OneHandedSword(), 100.0),
                ("two-handed sword", TwoHandedSword(), 200.0),
                ("two-handed by ValidLocations only", Carrier(ItemType.MeleeWeapon, CombatUse.Melee, EquipMask.TwoHanded, EquipMask.TwoHanded, ParentLocation.RightHand, 1.0), 200.0),
                ("two-handed by WeaponSkill only", Carrier(ItemType.MeleeWeapon, CombatUse.Melee, EquipMask.MeleeWeapon, EquipMask.MeleeWeapon, ParentLocation.RightHand, 1.0,
                    extra: new Dictionary<PropertyInt, int> { { PropertyInt.WeaponSkill, (int)Skill.TwoHandedCombat } }), 200.0),
                ("bow", Bow(), 200.0),
                ("wand", Wand(), 200.0),
            };

            foreach (var (name, item, ceiling) in cases)
            {
                Assert.AreEqual(ceiling, Definition.MaxRollFor(item), 1e-12, $"{name}: per-item ceiling");
                Assert.AreEqual(ceiling, WeaponModCombat.ReadWeaponOnly(item, WeaponModId.DefensiveWeapon, true, 1.0), 1e-12, $"{name}: a full roll reads the item's ceiling");
            }
        }

        /// <summary>
        /// THE INVARIANT. For each carrier class and two rolls, every fraction-to-magnitude site reports the same
        /// magnitude: the combat read, ReadMagnitude, ReadSpecials, the appraisal line and its intensity bracket,
        /// the market row, the quality-tier sum, and the FractionFor / ApplySpecial inverse.
        /// </summary>
        [TestMethod]
        public void EverySite_ReportsTheSameMagnitudeForTheSameItem()
        {
            var scale = WeaponModValue.MagnitudeScale();

            Assert.IsTrue(scale > 0.0, "harness: a muted magnitude scale would make every site agree on 0");

            var carriers = new (string Name, Func<double?, WorldObject> Make, double Ceiling)[]
            {
                ("one-handed sword", f => OneHandedSword(f), 100.0),
                ("two-handed sword", f => TwoHandedSword(f), 200.0),
                ("bow", f => Bow(f), 200.0),
                ("wand", f => Wand(f), 200.0),
            };

            foreach (var (name, make, ceiling) in carriers)
            {
                foreach (var fraction in new[] { 1.0, 0.5 })
                {
                    var item = make(fraction);
                    var expected = ceiling * fraction * scale;
                    var percent = (int)Math.Round(fraction * 100.0, MidpointRounding.AwayFromZero);
                    var effect = Definition.Format(expected);

                    Assert.AreEqual(expected, WeaponModCombat.ReadWeaponOnly(item, WeaponModId.DefensiveWeapon, true, scale), 1e-9, $"{name} @ {fraction}: combat read");
                    Assert.AreEqual(expected, WeaponModTinkerSet.ReadMagnitude(item, Definition, scale), 1e-9, $"{name} @ {fraction}: ReadMagnitude");

                    var special = WeaponModTinkerSet.ReadSpecials(item).Single();
                    Assert.AreEqual(expected, special.Magnitude, 1e-9, $"{name} @ {fraction}: ReadSpecials");

                    Assert.AreEqual(percent, WeaponModDisplay.IntensityPercent(Definition, special.Magnitude, scale, item), $"{name} @ {fraction}: intensity is the stored fraction on every carrier");
                    CollectionAssert.Contains(WeaponModDisplay.GetAppraisalLines(item), $"- Defensive Weapon [{percent}%]: {effect}", $"{name} @ {fraction}: appraisal line");

                    var row = MarketItemMods.WeaponRow(Definition, special.Magnitude, item);
                    Assert.AreEqual("Defensive Weapon", row.Token);
                    Assert.AreEqual(percent, row.IntensityPct, $"{name} @ {fraction}: market intensity agrees with the panel");
                    Assert.AreEqual(effect, row.Effect, $"{name} @ {fraction}: market effect agrees with the panel");

                    Assert.AreEqual(percent, WeaponQualityTiers.TotalIntensity(item), $"{name} @ {fraction}: the quality-tier sum reads the same intensity");

                    Assert.AreEqual(fraction, WeaponModValue.FractionFor(Definition, expected, scale, item), 1e-12, $"{name} @ {fraction}: FractionFor inverts against the same ceiling");

                    var seeded = make(null);
                    WeaponModTinkerSet.ApplySpecial(seeded, Definition, expected);
                    Assert.AreEqual(fraction, seeded.GetProperty(PropertyFloat.WeaponModDefensiveWeapon).Value, 1e-12, $"{name} @ {fraction}: ApplySpecial stores the fraction of THIS item's ceiling");
                }
            }

            // the failure this test guards: a site that dropped the item reads the one-handed sword at 200
            Assert.AreEqual(200.0 * scale, WeaponModValue.MagnitudeFromFraction(Definition, 1.0, scale), 1e-9,
                "control: without the carrier the conversion reads the full registry ceiling, which is why every site must pass the item");
        }

        // ---------------- the shield factor (Creature.GetShieldMod) ----------------

        /// <summary>A seeded Player at (55, 45) facing +Y, so TestCreatures' attacker at (50, 50) is a frontal blow.</summary>
        private static Player DefenderPlayer(CombatMode mode, uint shieldSkill, params WorldObject[] equipped)
        {
            var player = ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SeededPlayer();
            ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SetField(typeof(WorldObject), player, "ephemeralPositions", new Dictionary<PositionType, Position>());
            ACE.Server.Tests.Pvp.PvpTemplatePlayerTests.SetField(typeof(WorldObject), player, "positionCache", new Dictionary<PositionType, Position>());
            player.Location = new Position(0x00010064, 55.0f, 45.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            // the Shield skill comes from a fully-constructed donor creature: an uninitialized Player has no
            // working skill or attribute tables, and CreatureSkill computes against its OWN creature
            var donor = TestCreatures.CreateDefender(shieldSkill: shieldSkill);
            donor.GetCreatureSkill(Skill.Shield);
            typeof(Creature).GetField(nameof(Creature.Skills)).SetValue(player, donor.Skills);

            foreach (var item in equipped)
                player.EquippedObjects[item.Guid] = item;

            typeof(Creature).GetProperty(nameof(Creature.CombatMode)).GetSetMethod(true).Invoke(player, new object[] { mode });

            return player;
        }

        /// <summary>The level CalcArmorMod was handed, recovered from its output (the inverse of ArmorMod / (AL + ArmorMod)).</summary>
        private static double LevelFrom(float shieldMod) =>
            shieldMod >= 1.0f ? 0.0 : SkillFormula.ArmorMod / shieldMod - SkillFormula.ArmorMod;

        [TestMethod]
        public void ShieldMod_NoShield_IsAVirtualShieldAtTheCarriersCeiling()
        {
            var attacker = TestCreatures.CreateAttacker();
            var ignore = attacker.GetIgnoreShieldMod(null);

            var cases = new (string Name, WorldObject Item, double Level)[]
            {
                ("one-handed sword", OneHandedSword(), 100.0),
                ("two-handed sword", TwoHandedSword(), 200.0),
                ("bow", Bow(), 200.0),
                ("wand", Wand(), 200.0),
            };

            foreach (var (name, item, level) in cases)
            {
                var mod = DefenderPlayer(CombatMode.Melee, 0, item).GetShieldMod(attacker, DamageType.Slash, null);

                Assert.AreEqual(SkillFormula.CalcArmorMod((float)(level * ignore)), mod, 1e-5f, $"{name}: no shield, the carrier's {level} rides the uncapped virtual shield");
            }

            Assert.AreEqual(1.0f, DefenderPlayer(CombatMode.Melee, 0, OneHandedSword(null)).GetShieldMod(attacker, DamageType.Slash, null), 1e-6f,
                "control: the same carrier without the record is no shield at all");
        }

        /// <summary>
        /// STANCE-BOUND on both paths: in NonCombat Defensive Weapon contributes nothing, while an equipped shield
        /// still counts there (shield_applies_in_noncombat on). Each zero has a combat-stance control.
        /// </summary>
        [TestMethod]
        public void ShieldMod_NonCombat_DefensiveWeaponIsZeroOnBothPaths()
        {
            var attacker = TestCreatures.CreateAttacker();

            PropertyManager.ModifyBool("shield_applies_in_noncombat", true);

            // no shield
            Assert.AreEqual(1.0f, DefenderPlayer(CombatMode.NonCombat, 0, TwoHandedSword()).GetShieldMod(attacker, DamageType.Slash, null), 1e-6f,
                "no shield, peace mode: Defensive Weapon reads nothing");
            Assert.IsTrue(DefenderPlayer(CombatMode.Melee, 0, TwoHandedSword()).GetShieldMod(attacker, DamageType.Slash, null) < 1.0f,
                "control: the same player in a combat stance deflects");

            // with a shield: peace mode equals the shield alone, and the shield alone still counts
            var shieldOnly = DefenderPlayer(CombatMode.NonCombat, 400, Shield(), OneHandedSword(null)).GetShieldMod(attacker, DamageType.Slash, null);
            var shieldAndDefensive = DefenderPlayer(CombatMode.NonCombat, 400, Shield(), OneHandedSword()).GetShieldMod(attacker, DamageType.Slash, null);

            Assert.IsTrue(shieldOnly < 1.0f, "harness: a peace-mode shield counts with the setting on");
            Assert.AreEqual(shieldOnly, shieldAndDefensive, 1e-6f, "with a shield, peace mode: Defensive Weapon adds nothing");

            var combat = DefenderPlayer(CombatMode.Melee, 400, Shield(), OneHandedSword()).GetShieldMod(attacker, DamageType.Slash, null);
            Assert.IsTrue(combat < shieldOnly, "control: in a combat stance the same Defensive Weapon does add to the shield");
        }

        /// <summary>
        /// With a real shield, the level is added AFTER the Shield-skill cap. The Shield skill here is low enough
        /// that the cap binds on the shield's own 100 AL, so an add BEFORE the cap would be clamped away; the
        /// assertion is that the full Defensive Weapon level survives on top of the capped shield level.
        /// </summary>
        [TestMethod]
        public void ShieldMod_WithAShield_AddsTheLevelAfterTheShieldSkillCap()
        {
            var attacker = TestCreatures.CreateAttacker();
            var ignore = attacker.GetIgnoreShieldMod(null);

            Assert.AreEqual(1.0f, ignore, 1e-6f, "harness: the test attacker ignores no shield, so the recovered levels are the raw levels");

            var shieldOnly = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), OneHandedSword(null)).GetShieldMod(attacker, DamageType.Slash, null);
            var capped = LevelFrom(shieldOnly);

            Assert.IsTrue(capped > 0.0 && capped < 100.0, $"harness: the Shield-skill cap must bind below the shield's 100 AL (recovered {capped})");

            foreach (var (name, item, level) in new (string, WorldObject, double)[]
            {
                ("one-handed sword", OneHandedSword(), 100.0),
                ("two-handed sword", TwoHandedSword(), 200.0),
                ("wand", Wand(), 200.0),
            })
            {
                var mod = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), item).GetShieldMod(attacker, DamageType.Slash, null);

                Assert.AreEqual(capped + level, LevelFrom(mod), 0.05, $"{name}: capped shield {capped} plus the full {level}, not clamped by the cap");
            }
        }

        /// <summary>The equipped-shield slot sums Defensive Weapon and the ward; the ward keeps its own larger-of-two rule.</summary>
        [TestMethod]
        public void AfterShieldCap_SumsDefensiveWeaponAndTheWard()
        {
            Assert.AreEqual(30.0 + 100.0 + 40.0, DefensiveWeaponMath.AfterShieldCap(30.0, 100.0, 40.0), 1e-12, "capped shield + Defensive Weapon + ward");
            Assert.AreEqual(30.0 + 40.0, DefensiveWeaponMath.AfterShieldCap(30.0, 0.0, 40.0), 1e-12, "no Defensive Weapon: exactly the ward-only result");
            Assert.AreEqual(WardShieldMath.AfterShieldCap(30.0, 40.0), DefensiveWeaponMath.AfterShieldCap(30.0, 0.0, 40.0), 1e-12);
            Assert.AreEqual(30.0, DefensiveWeaponMath.AfterShieldCap(30.0, -50.0, 0.0), 1e-12, "a negative level counts as 0");
            Assert.AreEqual(30.0, DefensiveWeaponMath.AfterShieldCap(30.0, double.NaN, 0.0), 1e-12, "a NaN level counts as 0");

            // the two wards are never summed with each other, only the winner joins Defensive Weapon
            Assert.AreEqual(30.0 + 100.0 + 60.0, DefensiveWeaponMath.AfterShieldCap(30.0, 100.0, WardShieldMath.Level(40.0, 60.0)), 1e-12);
        }

        // ---------------- Thorns (Player.ApplyThornsReflect) ----------------

        private static (Player player, Creature attacker) ThornsPair(CombatMode? mode, params WorldObject[] equipped)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetField(typeof(WorldObject), player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetField(typeof(WorldObject), player, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetField(typeof(Player), player, "classAbilityCache", new Dictionary<ClassAbilityId, int>());
            SetField(typeof(Creature), player, "<EquippedObjects>k__BackingField", equipped.ToDictionary(e => e.Guid, e => e));

            if (mode != null)
                typeof(Creature).GetProperty(nameof(Creature.CombatMode)).GetSetMethod(true).Invoke(player, new object[] { mode.Value });

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            attacker.Health.Current = 100;

            TestCreatures.AttachBarePhysics(player, 50.0f);
            TestCreatures.AttachBarePhysics(attacker, 51.0f);

            return (player, attacker);
        }

        private static uint Expected(Player player, double armorLevel) =>
            ThornsAbility.ComputeReflectDamage(armorLevel, 0, PropertyManager.GetDouble("class_ability_thorns_percent_per_rank").Item, 0.0,
                player.GetEquippedModValue(EquipmentModId.Thorns));

        [TestMethod]
        public void Thorns_WithAShield_ReflectsFromTheShieldPlusTheDefensiveWeaponLevel()
        {
            const int shieldArmor = 2000;

            var (withPlayer, withAttacker) = ThornsPair(null, Shield(shieldArmor, thorns: true), TwoHandedSword());
            var (plainPlayer, plainAttacker) = ThornsPair(null, Shield(shieldArmor, thorns: true), TwoHandedSword(null));
            var (oneHandPlayer, oneHandAttacker) = ThornsPair(null, Shield(shieldArmor, thorns: true), OneHandedSword());

            withPlayer.ApplyThornsReflect(withAttacker, DamageType.Slash, 1.0);
            plainPlayer.ApplyThornsReflect(plainAttacker, DamageType.Slash, 1.0);
            oneHandPlayer.ApplyThornsReflect(oneHandAttacker, DamageType.Slash, 1.0);

            var plain = Expected(plainPlayer, shieldArmor);
            var with = Expected(withPlayer, shieldArmor + 200);
            var oneHand = Expected(oneHandPlayer, shieldArmor + 100);

            Assert.IsTrue(plain > 0 && with > oneHand && oneHand > plain, $"harness: the three expected reflects must be distinct ({plain}, {oneHand}, {with})");

            Assert.AreEqual(100u - plain, plainAttacker.Health.Current, "control: shield only");
            Assert.AreEqual(100u - with, withAttacker.Health.Current, "a two-handed Defensive Weapon adds its 200 to the reflected armor level");
            Assert.AreEqual(100u - oneHand, oneHandAttacker.Health.Current, "a one-handed Defensive Weapon adds its 100");
        }

        /// <summary>
        /// WITHOUT A SHIELD, Defensive Weapon does not enable Thorns: the Thorns equipment mod is carried on body
        /// armor so Thorns is owned, and the carrier holds a full roll, yet nothing reflects. The positive control
        /// differs only by adding a shield.
        /// </summary>
        [TestMethod]
        public void Thorns_WithoutAShield_DefensiveWeaponDoesNotEnableIt()
        {
            var (shieldless, shieldlessAttacker) = ThornsPair(null, ThornsArmor(), TwoHandedSword());
            var (control, controlAttacker) = ThornsPair(null, ThornsArmor(), TwoHandedSword(), Shield(100));

            Assert.IsTrue(shieldless.GetEquippedModValue(EquipmentModId.Thorns) > 0.0, "harness: Thorns is owned through the armor's equipment mod");
            Assert.IsTrue(shieldless.GetDefensiveWeaponShieldLevel() > 0.0, "harness: the carrier's Defensive Weapon is live");

            shieldless.ApplyThornsReflect(shieldlessAttacker, DamageType.Slash, 1.0);
            control.ApplyThornsReflect(controlAttacker, DamageType.Slash, 1.0);

            Assert.AreEqual(100u, shieldlessAttacker.Health.Current, "no shield: Defensive Weapon must not enable Thorns");
            Assert.AreEqual(100u - Expected(control, 100 + 200), controlAttacker.Health.Current, "control: with a shield the same setup reflects, Defensive Weapon included");
        }

        [TestMethod]
        public void Thorns_InNonCombat_TheDefensiveWeaponLevelAddsNothing()
        {
            const int shieldArmor = 2000;

            var (peace, peaceAttacker) = ThornsPair(CombatMode.NonCombat, Shield(shieldArmor, thorns: true), TwoHandedSword());
            var (combat, combatAttacker) = ThornsPair(CombatMode.Melee, Shield(shieldArmor, thorns: true), TwoHandedSword());

            peace.ApplyThornsReflect(peaceAttacker, DamageType.Slash, 1.0);
            combat.ApplyThornsReflect(combatAttacker, DamageType.Slash, 1.0);

            Assert.AreEqual(100u - Expected(peace, shieldArmor), peaceAttacker.Health.Current, "peace mode: the shield alone");
            Assert.AreEqual(100u - Expected(combat, shieldArmor + 200), combatAttacker.Health.Current, "control: combat stance adds the 200");
        }

        // ---------------- the ward, suppression and phantom weapons ----------------

        /// <summary>A Runic Ward pool that is up for the next ten minutes, set on the field GetWardShieldLevel tests.</summary>
        private static void RaiseRunicWard(Player player) =>
            SetField(typeof(Player), player, "runicWard", new RunicWardMath.WardState { Amount = 500, ExpireTime = ACE.Common.Time.GetUnixTime() + 600.0 });

        /// <summary>
        /// DEFENSIVE WEAPON PLUS A WARD through a real Player on the equipped-shield path: the capped shield level,
        /// the Defensive Weapon level and the ward level all sum after the Shield-skill cap. Each term has a control
        /// that differs from the full case by that term alone.
        /// </summary>
        [TestMethod]
        public void ShieldMod_WithAShield_DefensiveWeaponAndTheWardBothAddAfterTheCap()
        {
            const long wardLevel = 70;

            var priorAbilities = PropertyManager.GetBool("class_abilities_enabled").Item;
            var priorRunic = PropertyManager.GetLong("class_ability_runicward_shield_level").Item;

            try
            {
                PropertyManager.ModifyBool("class_abilities_enabled", true);
                PropertyManager.ModifyLong("class_ability_runicward_shield_level", wardLevel);

                var attacker = TestCreatures.CreateAttacker();
                Assert.AreEqual(1.0f, attacker.GetIgnoreShieldMod(null), 1e-6f, "harness: the test attacker ignores no shield");

                var capped = LevelFrom(DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), TwoHandedSword(null)).GetShieldMod(attacker, DamageType.Slash, null));
                Assert.IsTrue(capped > 0.0 && capped < 100.0, $"harness: the Shield-skill cap must bind below the shield's 100 AL (recovered {capped})");

                var wardOnlyPlayer = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), TwoHandedSword(null));
                RaiseRunicWard(wardOnlyPlayer);
                Assert.AreEqual(wardLevel, wardOnlyPlayer.GetWardShieldLevel(attacker), 1e-9, "harness: the ward is up and applies to this attacker");

                var bothPlayer = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), TwoHandedSword());
                RaiseRunicWard(bothPlayer);

                var wardOnly = LevelFrom(wardOnlyPlayer.GetShieldMod(attacker, DamageType.Slash, null));
                var defensiveOnly = LevelFrom(DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), TwoHandedSword()).GetShieldMod(attacker, DamageType.Slash, null));
                var both = LevelFrom(bothPlayer.GetShieldMod(attacker, DamageType.Slash, null));

                Assert.AreEqual(capped + wardLevel, wardOnly, 0.05, "control: the ward alone adds its level after the cap");
                Assert.AreEqual(capped + 200.0, defensiveOnly, 0.05, "control: Defensive Weapon alone adds its 200 after the cap");
                Assert.AreEqual(capped + 200.0 + wardLevel, both, 0.05, "capped shield + Defensive Weapon + ward, all after the cap");
            }
            finally
            {
                PropertyManager.ModifyBool("class_abilities_enabled", priorAbilities);
                PropertyManager.ModifyLong("class_ability_runicward_shield_level", priorRunic);
            }
        }

        /// <summary>
        /// WEAPON MODS OFF (weapon_mods_enabled=false): the carrier still holds a full roll, yet the combat read is
        /// 0, the shield factor is the shield alone, and Thorns reflects from the shield alone. The positive
        /// control is the same setup with the gate on.
        /// </summary>
        [TestMethod]
        public void Suppression_WeaponModsDisabled_DefensiveWeaponReadsZeroEverywhere()
        {
            const int shieldArmor = 2000;

            var prior = PropertyManager.GetBool("weapon_mods_enabled").Item;

            try
            {
                var attacker = TestCreatures.CreateAttacker();

                PropertyManager.ModifyBool("weapon_mods_enabled", true);
                var (liveThorns, liveThornsAttacker) = ThornsPair(CombatMode.Melee, Shield(shieldArmor, thorns: true), TwoHandedSword());
                Assert.AreEqual(200.0, liveThorns.GetDefensiveWeaponShieldLevel(), 1e-9, "control: with the gate on the carrier reads 200");
                var liveMod = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), TwoHandedSword()).GetShieldMod(attacker, DamageType.Slash, null);
                liveThorns.ApplyThornsReflect(liveThornsAttacker, DamageType.Slash, 1.0);
                Assert.AreEqual(100u - Expected(liveThorns, shieldArmor + 200), liveThornsAttacker.Health.Current, "control: with the gate on Thorns includes the 200");

                PropertyManager.ModifyBool("weapon_mods_enabled", false);

                var (offThorns, offThornsAttacker) = ThornsPair(CombatMode.Melee, Shield(shieldArmor, thorns: true), TwoHandedSword());
                Assert.AreEqual(0.0, offThorns.GetDefensiveWeaponShieldLevel(), 1e-9, "weapon mods off: Defensive Weapon reads 0");

                var shieldOnly = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), TwoHandedSword(null)).GetShieldMod(attacker, DamageType.Slash, null);
                var offMod = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), TwoHandedSword()).GetShieldMod(attacker, DamageType.Slash, null);
                Assert.AreEqual(shieldOnly, offMod, 1e-6f, "weapon mods off: the shield factor is the shield alone");
                Assert.IsTrue(liveMod < offMod, "control: the same setup with the gate on deflects more");

                offThorns.ApplyThornsReflect(offThornsAttacker, DamageType.Slash, 1.0);
                var shieldOnlyReflect = Expected(offThorns, shieldArmor);
                Assert.IsTrue(shieldOnlyReflect > 0, "harness: Thorns itself is live, so the shield-only reflect is not vacuous");
                Assert.AreEqual(100u - shieldOnlyReflect, offThornsAttacker.Health.Current, "weapon mods off: Thorns reflects from the shield alone");
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", prior);
            }
        }

        /// <summary>
        /// PK FACET SUPPRESSION through the real predicate: the wielder stands on the PK facet (ActiveFacetSlot 100)
        /// with facet_enabled and facet_pk_enabled on, and the carrier's Wielder is that player, which is how
        /// WeaponModCombat.ReadWeaponOnly resolves suppression. Defensive Weapon reads 0 and the shield factor is
        /// the shield alone. Thorns is an equipment mod / class ability and the PK facet suppresses those too, so on
        /// the facet it reflects nothing with or without Defensive Weapon; the Thorns half of "adds nothing" is
        /// therefore also driven through the weapon-mod-only suppression seam (the shape the PvP arena weapon-mod
        /// mask takes), where Thorns stays live and must reflect from the shield alone.
        /// </summary>
        [TestMethod]
        public void Suppression_PkFacet_DefensiveWeaponReadsZero_AndAddsNothingToThorns()
        {
            const int shieldArmor = 2000;

            var priorFacet = PropertyManager.GetBool("facet_enabled").Item;
            var priorFacetPk = PropertyManager.GetBool("facet_pk_enabled").Item;
            var priorSource = WeaponModSuppression.PlayerSuppressedSource;

            try
            {
                PropertyManager.ModifyBool("facet_enabled", true);
                PropertyManager.ModifyBool("facet_pk_enabled", true);

                var attacker = TestCreatures.CreateAttacker();

                // the real predicate, on the shield path
                Player OnFacet(int slot, WorldObject carrier)
                {
                    var player = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), carrier);
                    player.ActiveFacetSlot = slot;
                    carrier.Wielder = player;
                    return player;
                }

                var pk = OnFacet(Player.PkFacetSlot, TwoHandedSword());
                var ordinary = OnFacet(1, TwoHandedSword());

                Assert.IsTrue(pk.IsPkFacetRuleActive && pk.WeaponModSuppressed, "harness: the PK facet rule is active for this player");
                Assert.IsFalse(ordinary.WeaponModSuppressed, "harness: the control player is not suppressed");

                Assert.AreEqual(0.0, pk.GetDefensiveWeaponShieldLevel(), 1e-9, "PK facet: Defensive Weapon reads 0");
                Assert.AreEqual(200.0, ordinary.GetDefensiveWeaponShieldLevel(), 1e-9, "control: off the PK facet the same carrier reads 200");

                var shieldOnly = OnFacet(Player.PkFacetSlot, TwoHandedSword(null)).GetShieldMod(attacker, DamageType.Slash, null);
                Assert.AreEqual(shieldOnly, pk.GetShieldMod(attacker, DamageType.Slash, null), 1e-6f, "PK facet: the shield factor is the shield alone");
                Assert.IsTrue(ordinary.GetShieldMod(attacker, DamageType.Slash, null) < shieldOnly, "control: off the PK facet Defensive Weapon adds to the shield");

                // the real predicate, on Thorns: suppressed outright on the facet, so with and without agree
                var (pkThorns, pkThornsAttacker) = ThornsPair(CombatMode.Melee, Shield(shieldArmor, thorns: true), TwoHandedSword());
                var (pkPlain, pkPlainAttacker) = ThornsPair(CombatMode.Melee, Shield(shieldArmor, thorns: true), TwoHandedSword(null));
                foreach (var p in new[] { pkThorns, pkPlain })
                {
                    p.ActiveFacetSlot = Player.PkFacetSlot;
                    foreach (var item in p.EquippedObjects.Values)
                        item.Wielder = p;
                }

                Assert.AreEqual(0.0, pkThorns.GetDefensiveWeaponShieldLevel(), 1e-9, "PK facet, Thorns harness: Defensive Weapon reads 0");
                pkThorns.ApplyThornsReflect(pkThornsAttacker, DamageType.Slash, 1.0);
                pkPlain.ApplyThornsReflect(pkPlainAttacker, DamageType.Slash, 1.0);
                Assert.AreEqual(pkPlainAttacker.Health.Current, pkThornsAttacker.Health.Current, "PK facet: Defensive Weapon adds nothing to Thorns");

                // weapon mods suppressed while Thorns stays live: the shield-only reflect, and it is not 0
                WeaponModSuppression.PlayerSuppressedSource = _ => true;

                var (masked, maskedAttacker) = ThornsPair(CombatMode.Melee, Shield(shieldArmor, thorns: true), TwoHandedSword());
                foreach (var item in masked.EquippedObjects.Values)
                    item.Wielder = masked;

                Assert.AreEqual(0.0, masked.GetDefensiveWeaponShieldLevel(), 1e-9, "weapon mods suppressed: Defensive Weapon reads 0");
                masked.ApplyThornsReflect(maskedAttacker, DamageType.Slash, 1.0);
                var shieldOnlyReflect = Expected(masked, shieldArmor);
                Assert.IsTrue(shieldOnlyReflect > 0 && shieldOnlyReflect < Expected(masked, shieldArmor + 200), "harness: the shield-only reflect is distinct and not vacuous");
                Assert.AreEqual(100u - shieldOnlyReflect, maskedAttacker.Health.Current, "weapon mods suppressed: Thorns reflects from the shield alone");
            }
            finally
            {
                WeaponModSuppression.PlayerSuppressedSource = priorSource;
                PropertyManager.ModifyBool("facet_enabled", priorFacet);
                PropertyManager.ModifyBool("facet_pk_enabled", priorFacetPk);
            }
        }

        /// <summary>
        /// A PHANTOM weapon (IgnoreAllArmor) ignores the shield and Defensive Weapon together: 1.0 on the equipped-
        /// shield path and on the no-shield virtual shield. The control swings an ordinary weapon at the same player.
        /// </summary>
        [TestMethod]
        public void ShieldMod_PhantomWeapon_IgnoresTheShieldAndTheDefensiveWeaponLevel()
        {
            var attacker = TestCreatures.CreateAttacker();

            var phantom = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.ImbuedEffect, unchecked((int)ImbuedEffectType.IgnoreAllArmor) } });
            var ordinary = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee);

            Assert.IsTrue(phantom.HasImbuedEffect(ImbuedEffectType.IgnoreAllArmor), "harness: the attacker's weapon is phantom");

            var shielded = DefenderPlayer(CombatMode.Melee, 40, Shield(armorLevel: 100), TwoHandedSword());
            Assert.AreEqual(1.0f, shielded.GetShieldMod(attacker, DamageType.Slash, phantom), 1e-6f, "shield path: a phantom weapon ignores the shield and Defensive Weapon");
            Assert.IsTrue(shielded.GetShieldMod(attacker, DamageType.Slash, ordinary) < 1.0f, "control: an ordinary weapon is deflected on the same player");

            var shieldless = DefenderPlayer(CombatMode.Melee, 0, TwoHandedSword());
            Assert.AreEqual(1.0f, shieldless.GetShieldMod(attacker, DamageType.Slash, phantom), 1e-6f, "no-shield path: a phantom weapon ignores the virtual shield");
            Assert.IsTrue(shieldless.GetShieldMod(attacker, DamageType.Slash, ordinary) < 1.0f, "control: an ordinary weapon is deflected by the virtual shield");
        }

        // ---------------- the carrier ----------------

        /// <summary>
        /// ONE carrier, never a sum. Dual wield reads the MAIN hand only, whichever swing is next: an off-hand
        /// Defensive Weapon never counts, and two one-handed carriers read 100, not 200.
        /// </summary>
        [TestMethod]
        public void Carrier_DualWieldReadsTheMainHandOnly_AndNeverSums()
        {
            var both = ThornsPair(CombatMode.Melee, OneHandedSword(), OffHandSword()).player;
            var offOnly = ThornsPair(CombatMode.Melee, OneHandedSword(null), OffHandSword()).player;
            var mainOnly = ThornsPair(CombatMode.Melee, OneHandedSword(), OffHandSword(null)).player;

            Assert.AreEqual(100.0, both.GetDefensiveWeaponModValue(), 1e-9, "two one-handed carriers read the main hand's 100, not 200");
            Assert.AreEqual(0.0, offOnly.GetDefensiveWeaponModValue(), 1e-9, "an off-hand Defensive Weapon never counts");
            Assert.AreEqual(100.0, mainOnly.GetDefensiveWeaponModValue(), 1e-9, "control: the main hand alone");

            Assert.AreEqual(200.0, ThornsPair(CombatMode.Melee, Wand()).player.GetDefensiveWeaponModValue(), 1e-9, "the wand is the carrier when no physical weapon carries it");
            Assert.AreEqual(0.0, ThornsPair(CombatMode.NonCombat, Wand()).player.GetDefensiveWeaponShieldLevel(), 1e-9, "the combat read is stance-bound");
            Assert.AreEqual(200.0, ThornsPair(CombatMode.NonCombat, Wand()).player.GetDefensiveWeaponModValue(), 1e-9, "the simulator read is stance-free");
        }

        // ---------------- wiring that a harness cannot reach ----------------

        /// <summary>
        /// Source pins for the parts no harness here can drive: Thorns adds the level only AFTER its shield check
        /// (so a shieldless player never reaches it), Shield Block's roll never reads Defensive Weapon, and the
        /// combat simulator's shield row mirrors the after-cap add on its shielded branch.
        /// </summary>
        [TestMethod]
        public void Wiring_ThornsAfterTheShieldCheck_BlockUntouched_SimulatorMirrored()
        {
            var combat = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityCombat.cs");

            var thorns = PooledLootSourceText.MethodBody(combat, "public void ApplyThornsReflect(");
            var shieldCheck = thorns.IndexOf("if (shield == null)", StringComparison.Ordinal);
            var defensive = thorns.IndexOf("DefensiveWeaponMath.ThornsArmorLevel(shieldArmorLevel, GetDefensiveWeaponShieldLevel())", StringComparison.Ordinal);
            Assert.IsTrue(shieldCheck >= 0 && defensive > shieldCheck, "Thorns must add the Defensive Weapon level only after refusing a shieldless defender");

            var block = PooledLootSourceText.MethodBody(combat, "public ClassAbilityAvoidanceOutcome RollClassAbilityAvoidance(");
            Assert.IsFalse(block.Contains("DefensiveWeapon", StringComparison.Ordinal), "Shield Block gates on a real shield and never reads Defensive Weapon");

            var profile = PooledLootSourceText.Read("Source/ACE.Server/CombatSimulator/ProfileBuilder.cs");
            var row = PooledLootSourceText.MethodBody(profile, "private static ShieldRow BuildShieldRow(");
            StringAssert.Contains(row, "var defensiveWeapon = player.GetDefensiveWeaponModValue();");
            StringAssert.Contains(row, "return new ShieldRow((float)DefensiveWeaponMath.AfterShieldCap(cappedShieldLevel, defensiveWeapon, 0.0));");
        }

        // ---------------- Panic Reload ----------------

        [TestMethod]
        public void PanicReload_CeilingIsFiftyPercent()
        {
            var panicReload = WeaponModRegistry.Get(WeaponModId.PanicReload);

            Assert.AreEqual(0.50, panicReload.MaxRoll, 1e-12, "owner ruling 2026-10-25: up to +50% attack speed");
            Assert.IsNull(panicReload.CeilingFactor, "Panic Reload has no per-item ceiling");

            var bow = WeaponModTestKit.MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile, floats: new Dictionary<PropertyFloat, double> { { PropertyFloat.WeaponModPanicReload, 1.0 } });

            Assert.AreEqual(0.50, WeaponModCombat.ReadWeaponOnly(bow, WeaponModId.PanicReload, true, 1.0), 1e-12, "a full roll reads +50%");
            Assert.AreEqual(0.25, WeaponModCombat.ReadWeaponOnly(bow, WeaponModId.PanicReload, true, 0.5), 1e-12, "the scale still applies");
            Assert.AreEqual("Panic Reload [100%]: +50% attack speed with a hostile within 6 m", WeaponModDisplay.Describe(panicReload, 0.50, 1.0, bow));
        }

        private static void SetField(Type owner, object target, string name, object value)
        {
            var field = owner.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{owner.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }
    }
}
