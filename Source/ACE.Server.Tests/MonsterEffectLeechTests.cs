using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// LeechEffect: heals the attacking monster for a fraction of the damage it just dealt. Unlike
    /// FlatDamageEffect/DotEffect the heal is synchronous (a direct UpdateVitalDelta call, no ActionChain),
    /// so it is fully exercisable through the real dispatch site.
    /// </summary>
    [TestClass]
    public class MonsterEffectLeechTests
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

            PropertyManager.ModifyDouble("monster_effect_leech_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_leech_cap"].Item);
        }

        private static readonly LeechEffect Effect = new LeechEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"leech {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("vital=health pct=0.1"), out _));
            Assert.IsTrue(Effect.Validate(Spec("pct=0.1"), out _), "vital= defaults to health");

            Assert.IsFalse(Effect.Validate(Spec("vital=health pct=0"), out var error), "pct= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("vital=health"), out _), "pct= is required");
        }

        [TestMethod]
        public void Magnitude_HealsTheAttackerAFractionOfTheDamageDealt()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 1;

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("vital=health pct=0.1"))));

            var damageEvent = new DamageEvent { Damage = 50.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            // 50 * 0.1 = 5.0 exactly, no carry needed
            Assert.AreEqual(6u, attacker.Health.Current, "1 starting health + a 5-point heal");
        }

        [TestMethod]
        public void CapClamp_PctIsClampedByTheLeechCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_leech_cap", 0.1));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 1;

            // authored pct (0.9) is far above the cap (0.1); the cap must win
            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("vital=health pct=0.9"))));

            var damageEvent = new DamageEvent { Damage = 50.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(6u, attacker.Health.Current, "50 * capped 0.1 = 5.0, not 50 * 0.9 = 45");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheHeal()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 1;

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("vital=health pct=0.5"))));

            var damageEvent = new DamageEvent { Damage = 50.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.AreEqual(1u, attacker.Health.Current, "the master switch must stop the heal entirely");
        }
    }
}
