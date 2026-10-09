using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The v3 weapon-mod catalog expansion (2026-08-06): six new Tier B rows - Heft, Tension, Leverage,
    /// Attunement, Focus, Execution - plus the CHANGE 2 retune of Quickening, Ambush, DefaultMinPotency and
    /// the special-chance/guarantee tunables.
    ///
    /// ALL SIX ARE TIER B, NOT TIER A. The original same-day draft shipped Heft/Tension/Leverage/Attunement as
    /// Tier A (writing PropertyInt.Damage / PropertyFloat.DamageMod / PropertyFloat.ElementalDamageMod
    /// directly), which collided with the structural invariant that no Tier A special may share a native with
    /// a layer 1 tinker material - all three natives are already owned by Iron, Mahogany and Green Garnet
    /// respectively (see WeaponModInteractionTests.Registry_NoSpecialSharesANativePropertyWithALayerOneMaterial).
    /// Focus/Execution were blocked from the start for a different collision, with the HARD "crit routes
    /// through ratings" invariant. Routing all six through Tier B - no native property, read live at combat
    /// time - resolves both: see WeaponModRegistry.cs's Tier B v3 remarks for the hook sites.
    ///
    /// WHAT IS COVERED HERE: the registry shape of all six rows, and the four combat-time hooks that are
    /// reachable without a live Player/session - BaseDamageMod's constructor (Heft, Tension, Leverage),
    /// GetCasterElementalDamageModifier (Attunement) and GetWeaponCriticalChance / GetWeaponMagicCritFrequency
    /// (Focus), all static and callable against TestCreatures' bare Creature objects.
    ///
    /// WHAT IS NOT: Execution's hooks (DamageEvent.cs and SpellProjectile.cs) run deep inside the live combat
    /// resolution path (DamageEvent's constructor needs a full attacker/defender/weapon triple with body parts
    /// and a random crit roll; SpellProjectile needs a cast in flight) and are not reachable from a unit test
    /// either way, matching the existing precedent for Tier B v2's own combat hooks in Player_WeaponMods.cs.
    /// Live-loop verification is queued in Docs/VERIFY-QUEUE.md.
    /// </summary>
    [TestClass]
    public class WeaponModCatalogV3Tests
    {
        private static uint nextGuid = 0x7E100000;
        private static uint nextWcid = 993000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>Bare in-memory weapon, same shape as WeaponModTests.MakeWeapon.</summary>
        private static WorldObject MakeWeapon(ItemType itemType, CombatUse combatUse)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                    { PropertyInt.ItemWorkmanship, 10 },
                    { PropertyInt.CombatUse, (int)combatUse },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Weapon" } },
            };

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A bare Caster weapon (wand), needed for the Attunement/Focus magic-side hooks.</summary>
        private static WorldObject MakeCasterWeapon(DamageType damageType)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                    { PropertyInt.ItemWorkmanship, 10 },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                    { PropertyInt.DamageType, (int)damageType },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Wand" } },
            };

            return new Caster(weenie, new ObjectGuid(nextGuid++));
        }

        private static void WithGate(bool enabled, System.Action body)
        {
            var prior = PropertyManager.GetBool("weapon_mods_enabled").Item;

            PropertyManager.ModifyBool("weapon_mods_enabled", enabled);

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", prior);
            }
        }

        // ================= the six rows, as a table =================

        /// <summary>
        /// The six v3 rows, with the magnitude and class set each must carry. Pinned as a table rather than
        /// read off the registry, so a retune has to move this file deliberately.
        /// </summary>
        private static readonly (WeaponModId Id, string Name, PropertyFloat Record, double MaxRoll, WeaponClass Classes)[] Expected =
        {
            (WeaponModId.Heft,       "Heft",       PropertyFloat.WeaponModHeft,       22,   WeaponClass.Melee | WeaponClass.Missile),
            (WeaponModId.Tension,    "Tension",    PropertyFloat.WeaponModTension,    0.50, WeaponClass.Missile),
            (WeaponModId.Leverage,   "Leverage",   PropertyFloat.WeaponModLeverage,   0.30, WeaponClass.Melee),
            (WeaponModId.Attunement, "Attunement", PropertyFloat.WeaponModAttunement, 0.28, WeaponClass.Caster),
            (WeaponModId.Focus,      "Focus",      PropertyFloat.WeaponModFocus,      0.25, WeaponClass.All),
            (WeaponModId.Execution,  "Execution",  PropertyFloat.WeaponModExecution,  0.50, WeaponClass.All),
        };

        [TestMethod]
        public void CatalogV3_HoldsExactlyTheseSixRowsAsTierB()
        {
            foreach (var (id, name, record, maxRoll, classes) in Expected)
            {
                Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id}: missing registry row");

                Assert.AreEqual(WeaponModTier.B, definition.Tier, $"{id}: must be Tier B - it writes NO native property");
                Assert.AreEqual(record, definition.Record, $"{id}: reserved record id moved");
                Assert.AreEqual(maxRoll, definition.MaxRoll, 1e-12, $"{id}: MaxRoll must be {maxRoll}");
                Assert.AreEqual(classes, definition.Classes, $"{id}: class set moved");
                Assert.AreEqual(name, definition.DisplayName, $"{id}: renaming changes the appraisal panel, so it must be deliberate");
                Assert.IsTrue(definition.AffectsSingleTargetDamage, $"{id}: every v3 row is damage-relevant by design");
                Assert.IsFalse(definition.Binary, $"{id}: rolls a magnitude");
                Assert.AreEqual(WeaponModDefinition.DefaultMinPotency, definition.MinPotency, 1e-12, $"{id}: uses the class default potency floor");

                Assert.IsNull(definition.NativeInt, $"{id}: a Tier B row must set no native property");
                Assert.IsNull(definition.NativeFloat, $"{id}: a Tier B row must set no native property");
                Assert.IsFalse(definition.WritesNative, $"{id}: a Tier B row must not write a native property");
                Assert.IsFalse(definition.IsInteger, $"{id}: a Tier B magnitude is never the integer branch");

                // record id falls in the v3 EXPANSION Tier B band, not the original v2 Tier B band - the two
                // are disjoint
                var recordId = (int)definition.Record;
                Assert.IsTrue(recordId >= WeaponModRegistry.TierBExpansionBandStart && recordId <= WeaponModRegistry.TierBExpansionBandEnd,
                    $"{id}: record {recordId} is outside the v3 expansion band {WeaponModRegistry.TierBExpansionBandStart}-{WeaponModRegistry.TierBExpansionBandEnd}");
            }

            // Was 13 (seven v2 rows plus six v3 rows) before 2026-08-17: catalog v4 retired four v2 rows
            // (LifeLeech, ManaLeech, StaminaLeech, Overload), leaving three v2 survivors, and added nine new
            // v4 rows (currently inert). 3 + 6 + 9 = 18.
            Assert.AreEqual(18, WeaponModRegistry.TierBMods.Count, "three surviving v2 rows plus six v3 rows plus nine v4 rows");
        }

        /// <summary>Neither Focus nor Execution ever writes the retail crit properties - the invariant they were blocked over.</summary>
        [TestMethod]
        public void CatalogV3_NeitherFocusNorExecutionWritesTheRetailCritProperties()
        {
            foreach (var mod in WeaponModRegistry.AllMods)
            {
                Assert.AreNotEqual(PropertyFloat.CriticalFrequency, mod.NativeFloat, $"{mod.Id} must not write the retail CriticalFrequency");
                Assert.AreNotEqual(PropertyFloat.CriticalMultiplier, mod.NativeFloat, $"{mod.Id} must not write the retail CriticalMultiplier");
            }
        }

        /// <summary>None of the six v3 rows shares a native with a layer 1 tinker material - trivially true since none writes a native at all.</summary>
        [TestMethod]
        public void CatalogV3_NoneOfTheSixWritesAnyNativePropertyAtAll()
        {
            foreach (var (id, _, _, _, _) in Expected)
            {
                var definition = WeaponModRegistry.Get(id);

                Assert.IsFalse(definition.WritesNative, $"{id}: must write no native property, which is what resolves the layer-1-collision that blocked the original Tier A draft");
            }
        }

        // ================= class eligibility =================

        [TestMethod]
        public void CatalogV3_ClassEligibilityMatchesTheTable()
        {
            var heft = WeaponModRegistry.Get(WeaponModId.Heft);
            Assert.IsTrue(heft.AppliesTo(WeaponClass.Melee));
            Assert.IsTrue(heft.AppliesTo(WeaponClass.Missile));
            Assert.IsFalse(heft.AppliesTo(WeaponClass.Caster));

            var tension = WeaponModRegistry.Get(WeaponModId.Tension);
            Assert.IsFalse(tension.AppliesTo(WeaponClass.Melee));
            Assert.IsTrue(tension.AppliesTo(WeaponClass.Missile));
            Assert.IsFalse(tension.AppliesTo(WeaponClass.Caster));

            var leverage = WeaponModRegistry.Get(WeaponModId.Leverage);
            Assert.IsTrue(leverage.AppliesTo(WeaponClass.Melee));
            Assert.IsFalse(leverage.AppliesTo(WeaponClass.Missile));
            Assert.IsFalse(leverage.AppliesTo(WeaponClass.Caster));

            var attunement = WeaponModRegistry.Get(WeaponModId.Attunement);
            Assert.IsFalse(attunement.AppliesTo(WeaponClass.Melee));
            Assert.IsFalse(attunement.AppliesTo(WeaponClass.Missile));
            Assert.IsTrue(attunement.AppliesTo(WeaponClass.Caster));

            foreach (var id in new[] { WeaponModId.Focus, WeaponModId.Execution })
            {
                var definition = WeaponModRegistry.Get(id);
                Assert.IsTrue(definition.AppliesTo(WeaponClass.Melee), $"{id} must apply to Melee");
                Assert.IsTrue(definition.AppliesTo(WeaponClass.Missile), $"{id} must apply to Missile");
                Assert.IsTrue(definition.AppliesTo(WeaponClass.Caster), $"{id} must apply to Caster");
            }
        }

        /// <summary>
        /// Puts a Tier B modifier on a weapon at a chosen MAGNITUDE, which is what every hook test below wants
        /// to talk about.
        ///
        /// NOT A RAW SetProperty, and that is the whole reason this helper exists. Since the 2026-08-07 storage
        /// split a Tier B record holds a ROLL FRACTION, not a magnitude, so seeding the record with 15.0
        /// directly would clamp to a full-strength roll and read back as Heft's MaxRoll of 22 - a test that
        /// still compiles, still looks right, and asserts the wrong number. ApplySpecial does the conversion.
        /// </summary>
        private static void Seed(WorldObject weapon, WeaponModId id, double magnitude) =>
            WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(id), magnitude);

        // ================= combat hooks: BaseDamageMod (Heft, Tension, Leverage) =================

        [TestMethod]
        public void Hook_HeftAddsToDamageBonusInsideTheDamageModBracket()
        {
            var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee);
            Seed(weapon, WeaponModId.Heft, 15.0);

            // BaseDamageMod's constructor dereferences wielder when weapon.IsEnchantable is true (the default
            // for a bare test item), so a real Creature is needed even though the weapon-mod terms themselves
            // do not depend on it
            var wielder = TestCreatures.CreateAttacker();

            try
            {
                WithGate(false, () =>
                {
                    var off = new BaseDamageMod(new BaseDamage(10, 0.2f), wielder, weapon);
                    WeaponModCombat.ApplyBaseDamageMods(off, weapon);
                    Assert.AreEqual(0.0f, off.DamageBonus, 1e-6f, "gate off: Heft must not fire");
                });

                WithGate(true, () =>
                {
                    var on = new BaseDamageMod(new BaseDamage(10, 0.2f), wielder, weapon);
                    WeaponModCombat.ApplyBaseDamageMods(on, weapon);
                    Assert.AreEqual(15.0f, on.DamageBonus, 1e-6f, "gate on: Heft's magnitude must land in DamageBonus");

                    // DamageBonus sits inside the "(base + bonus + elemental) * DamageMod" bracket
                    Assert.AreEqual((10.0f + 15.0f) * 1.0f, on.MaxDamage, 1e-3f);
                });
            }
            finally
            {
                wielder.Destroy();
            }
        }

        [TestMethod]
        public void Hook_TensionAndLeverageAddToDamageModAndStackMultiplicativelyWithHeft()
        {
            var weapon = MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile);
            Seed(weapon, WeaponModId.Heft, 10.0);
            Seed(weapon, WeaponModId.Tension, 0.40);

            var wielder = TestCreatures.CreateAttacker();

            try
            {
                WithGate(true, () =>
                {
                    var mod = new BaseDamageMod(new BaseDamage(10, 0.2f), wielder, weapon);
                    WeaponModCombat.ApplyBaseDamageMods(mod, weapon);

                    Assert.AreEqual(10.0f, mod.DamageBonus, 1e-6f);
                    Assert.AreEqual(1.40f, mod.DamageMod, 1e-6f, "Tension must ADD to the engine default DamageMod of 1.0");

                    // Heft's contribution multiplies with Tension's DamageMod, not adds alongside it
                    Assert.AreEqual((10.0f + 10.0f) * 1.40f, mod.MaxDamage, 1e-3f);
                });

                var melee = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee);
                Seed(melee, WeaponModId.Leverage, 0.25);

                WithGate(true, () =>
                {
                    var mod = new BaseDamageMod(new BaseDamage(10, 0.2f), wielder, melee);
                    WeaponModCombat.ApplyBaseDamageMods(mod, melee);
                    Assert.AreEqual(1.25f, mod.DamageMod, 1e-6f, "Leverage must ADD to the engine default DamageMod of 1.0, on the melee side");
                });
            }
            finally
            {
                wielder.Destroy();
            }
        }

        [TestMethod]
        public void Hook_HeftAndTensionAreInertOnAWeaponWithNoRecord()
        {
            var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee);
            var wielder = TestCreatures.CreateAttacker();

            try
            {
                WithGate(true, () =>
                {
                    var mod = new BaseDamageMod(new BaseDamage(10, 0.2f), wielder, weapon);

                    Assert.AreEqual(0.0f, mod.DamageBonus, 1e-6f, "no Heft record: DamageBonus must be untouched");
                    Assert.AreEqual(1.0f, mod.DamageMod, 1e-6f, "no Tension/Leverage record: DamageMod must be untouched");
                });
            }
            finally
            {
                wielder.Destroy();
            }
        }

        // ================= combat hooks: GetCasterElementalDamageModifier (Attunement) =================

        [TestMethod]
        public void Hook_AttunementAddsIntoTheElementalDamageEnchantmentSum()
        {
            var wand = MakeCasterWeapon(DamageType.Fire);
            Seed(wand, WeaponModId.Attunement, 0.18);

            var wielder = TestCreatures.CreateAttacker();
            var target = TestCreatures.CreateDefender();

            try
            {
                float off = 0, on = 0;

                WithGate(false, () => off = WorldObject.GetCasterElementalDamageModifier(wand, wielder, target, DamageType.Fire));
                WithGate(true, () => on = WorldObject.GetCasterElementalDamageModifier(wand, wielder, target, DamageType.Fire));

                Assert.AreEqual(1.0f, off, 1e-6f, "gate off: Attunement must not fire");
                Assert.AreEqual(1.18f, on, 1e-3f, "gate on: Attunement must add into the enchantment sum");
            }
            finally
            {
                wielder.Destroy();
                target.Destroy();
            }
        }

        [TestMethod]
        public void Hook_AttunementNeverFiresOnAMeleeOrMissileWeapon()
        {
            var melee = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee);
            Seed(melee, WeaponModId.Attunement, 0.18);

            var wielder = TestCreatures.CreateAttacker();
            var target = TestCreatures.CreateDefender();

            try
            {
                WithGate(true, () =>
                {
                    // GetCasterElementalDamageModifier returns 1.0f unconditionally when the item is not a Caster
                    var result = WorldObject.GetCasterElementalDamageModifier(melee, wielder, target, DamageType.Slash);
                    Assert.AreEqual(1.0f, result, 1e-6f, "a melee weapon must never reach the elemental-damage hook");
                });
            }
            finally
            {
                wielder.Destroy();
                target.Destroy();
            }
        }

        // ================= combat hooks: crit chance (Focus) =================

        [TestMethod]
        public void Hook_FocusAddsToPhysicalCritChanceAfterTheImbueMax()
        {
            var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee);
            Seed(weapon, WeaponModId.Focus, 0.20);

            var wielder = TestCreatures.CreateAttacker();
            var target = TestCreatures.CreateDefender();
            var skill = wielder.GetCreatureSkill(Skill.UnarmedCombat);

            try
            {
                float off = 0, on = 0;

                WithGate(false, () => off = WorldObject.GetWeaponCriticalChance(weapon, wielder, skill, target));
                WithGate(true, () => on = WorldObject.GetWeaponCriticalChance(weapon, wielder, skill, target));

                Assert.AreEqual(on - off, 0.20f, 1e-3f, "the gate-on/gate-off delta must be exactly Focus's magnitude");
            }
            finally
            {
                wielder.Destroy();
                target.Destroy();
            }
        }

        [TestMethod]
        public void Hook_FocusAddsToMagicCritChanceToo()
        {
            var wand = MakeCasterWeapon(DamageType.Fire);
            Seed(wand, WeaponModId.Focus, 0.20);

            var wielder = TestCreatures.CreateAttacker();
            var target = TestCreatures.CreateDefender();
            var skill = wielder.GetCreatureSkill(Skill.WarMagic);

            try
            {
                float off = 0, on = 0;

                WithGate(false, () => off = WorldObject.GetWeaponMagicCritFrequency(wand, wielder, skill, target));
                WithGate(true, () => on = WorldObject.GetWeaponMagicCritFrequency(wand, wielder, skill, target));

                Assert.AreEqual(on - off, 0.20f, 1e-3f,
                    "Focus must fire on the MAGIC crit-chance function too, not just the physical one - it is Caster-eligible and GetWeaponCriticalChance is never reached by a spell cast");
            }
            finally
            {
                wielder.Destroy();
                target.Destroy();
            }
        }

        // ================= CHANGE 2 - the retune =================

        [TestMethod]
        public void Retune_QuickeningAndAmbushMaxRollsMoved()
        {
            Assert.AreEqual(0.24, WeaponModRegistry.Get(WeaponModId.Quickening).MaxRoll, 1e-12, "Quickening: 0.06 -> 0.24");
            Assert.AreEqual(0.30, WeaponModRegistry.Get(WeaponModId.Ambush).MaxRoll, 1e-12, "Ambush: 0.15 -> 0.30");
        }

        /// <summary>
        /// RENAMED 2026-08-07 from ...AppliesToEveryRowExceptCleave. Cleave was the one row that overrode
        /// MinPotency (to 0, because it was Binary and had no magnitude axis for a floor to sit on), and it was
        /// removed from the catalog that day. With it gone the rule has no exception left, so the test asserts
        /// the stronger thing: EVERY row uses the class default, and an override appearing on any row is a
        /// change that has to be made here deliberately.
        /// </summary>
        [TestMethod]
        public void Retune_DefaultMinPotencyMovedAndAppliesToEveryRow()
        {
            Assert.AreEqual(0.60, WeaponModDefinition.DefaultMinPotency, 1e-12, "the class default: 0.25 -> 0.60");

            foreach (var mod in WeaponModRegistry.AllMods)
            {
                Assert.AreEqual(WeaponModDefinition.DefaultMinPotency, mod.MinPotency, 1e-12,
                    $"{mod.Id}: must use the class default. Since Cleave was removed on 2026-08-07 NO row overrides it, so this is now an every-row rule rather than an all-but-one rule");
            }
        }

        [TestMethod]
        public void Retune_SpecialChanceTunablesMovedToTheNewDefaults()
        {
            Assert.AreEqual(1.00, PropertyManager.GetDouble("weapon_mod_special_chance_1").Item, 1e-12);
            Assert.AreEqual(0.60, PropertyManager.GetDouble("weapon_mod_special_chance_2").Item, 1e-12);
            Assert.AreEqual(0.30, PropertyManager.GetDouble("weapon_mod_special_chance_3").Item, 1e-12);
            Assert.AreEqual(0.10, PropertyManager.GetDouble("weapon_mod_special_chance_4").Item, 1e-12);
        }

        [TestMethod]
        public void Retune_GuaranteeDamageSpecialDefaultsToTrue()
        {
            Assert.IsTrue(PropertyManager.GetBool("weapon_mod_guarantee_damage_special").Item);
            Assert.IsTrue(WeaponModRoller.GuaranteeDamageSpecial());
        }
    }
}
