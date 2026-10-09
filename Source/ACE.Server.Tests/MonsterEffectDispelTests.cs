using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// DispelEffect: a landed hit has a chance to strip one beneficial enchantment from the defender.
    /// Strips synchronously through EnchantmentManager.Dispel, no ActionChain involved, so it is fully
    /// exercisable through the real dispatch site. The defender here is a bare non-Player Creature (this
    /// project cannot construct a live Player - see DamageEventTests), so the enchantment fixture is
    /// injected directly into Biota.PropertiesEnchantmentRegistry rather than cast via a real spell/dat
    /// lookup, and the victim-message branch (Player-only) is not exercised.
    /// </summary>
    [TestClass]
    public class MonsterEffectDispelTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            PropertyManager.ModifyDouble("monster_effect_proc_chance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_proc_chance_cap"].Item);
        }

        private static readonly DispelEffect Effect = new DispelEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"dispel {args}", out var specs, out _);
            return specs[0];
        }

        /// <summary>A single beneficial, dispellable enchantment - not item-sourced, not a pseudo-spell.</summary>
        private static PropertiesEnchantmentRegistry BeneficialEnchantment() => new PropertiesEnchantmentRegistry
        {
            SpellId = 1,
            SpellCategory = SpellCategory.StrengthRaising,
            PowerLevel = 1,
            StatModType = EnchantmentTypeFlags.Beneficial,
            Duration = 300,
            StartTime = 0,
            CasterObjectId = 1,
        };

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("chance=0.2"), out _));

            Assert.IsFalse(Effect.Validate(Spec("chance=0"), out var error), "chance= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("chance=1.5"), out _), "chance= must be <= 1");
        }

        [TestMethod]
        public void Magnitude_StripsABeneficialEnchantmentOnACertainRoll()
        {
            // the pooled chance is clamped by the cap before it is rolled, so the cap has to be lifted for
            // an authored chance=1.0 to actually be certain (default cap is 0.75)
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            defender.Biota.PropertiesEnchantmentRegistry.Add(BeneficialEnchantment());

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(0, defender.Biota.PropertiesEnchantmentRegistry.Count, "a certain roll must strip the enchantment");
        }

        /// <summary>
        /// A hit that landed for NOTHING must proc nothing. DamageEvent.HasDamage stays true when the
        /// defender is Invincible - DoCalculateDamage returns 0 damage without setting Evaded, Blocked,
        /// Parried or LifestoneProtection - so a rider with no zero-damage guard fires off a hit the
        /// defender never felt. leech, execute, rangeramp, ramp and debuff always had this guard; this
        /// kind did not.
        /// </summary>
        [TestMethod]
        public void ZeroDamageHit_ProcsNothing()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            defender.Biota.PropertiesEnchantmentRegistry.Add(BeneficialEnchantment());

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 0.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count, "a hit that dealt nothing must not strip a buff");
        }

        [TestMethod]
        public void CapClamp_ChanceIsClampedByTheProcChanceCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 0.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            defender.Biota.PropertiesEnchantmentRegistry.Add(BeneficialEnchantment());

            // authored chance (1.0) is far above the cap (0.0); the cap must win and nothing may be stripped
            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count, "a cap of zero must let nothing through");
        }

        [TestMethod]
        public void Validate_RejectsOnAvoid()
        {
            Assert.IsFalse(Effect.Validate(Spec("chance=0.2 on=avoid"), out var error), "dispel has no avoidance hook");
            Assert.IsFalse(string.IsNullOrEmpty(error));

            // a mixed list containing avoid must be rejected too, not just a bare on=avoid
            Assert.IsFalse(Effect.Validate(Spec("chance=0.2 on=hit,avoid"), out _));
        }

        [TestMethod]
        public void DefaultOn_FiresOnHitOnlyNotSpellHit()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            defender.Biota.PropertiesEnchantmentRegistry.Add(BeneficialEnchantment());

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("chance=1.0"))));

            var damage = 10.0f;
            attacker.ApplySpellHitMonsterEffects(defender, null, ref damage);

            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count, "on= defaults to hit, so a spell hit must not strip a buff");

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(0, defender.Biota.PropertiesEnchantmentRegistry.Count, "on= defaults to hit, so a landed physical hit must still strip a buff");
        }

        [TestMethod]
        public void OnSpellHit_FiresOnSpellHitOnlyNotPhysicalHit()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            defender.Biota.PropertiesEnchantmentRegistry.Add(BeneficialEnchantment());

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("chance=1.0 on=spellhit"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count, "on=spellhit must not fire on a landed physical hit");

            var damage = 10.0f;
            attacker.ApplySpellHitMonsterEffects(defender, null, ref damage);

            Assert.AreEqual(0, defender.Biota.PropertiesEnchantmentRegistry.Count, "on=spellhit must strip a buff on a landed spell hit");
        }

        [TestMethod]
        public void OnHitAndSpellHit_FiresOnBothTriggers()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            defender.Biota.PropertiesEnchantmentRegistry.Add(BeneficialEnchantment());
            defender.Biota.PropertiesEnchantmentRegistry.Add(BeneficialEnchantment());

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("chance=1.0 on=hit,spellhit"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };
            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count, "on=hit,spellhit must strip a buff on a landed physical hit");

            var damage = 10.0f;
            attacker.ApplySpellHitMonsterEffects(defender, null, ref damage);

            Assert.AreEqual(0, defender.Biota.PropertiesEnchantmentRegistry.Count, "on=hit,spellhit must also strip a buff on a landed spell hit");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheDispel()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            defender.Biota.PropertiesEnchantmentRegistry.Add(BeneficialEnchantment());

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(1, defender.Biota.PropertiesEnchantmentRegistry.Count, "the master switch must stop the dispel entirely");
        }
    }
}
