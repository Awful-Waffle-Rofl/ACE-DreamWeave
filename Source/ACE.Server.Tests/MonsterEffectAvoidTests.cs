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
    /// AvoidEffect: a flat chance for this monster to avoid an incoming attack outright - the real avoidance
    /// GRANTER, unlike DebuffEffect/ReflectEffect's on=avoid riders. GetAvoidChance is a pure read, so
    /// magnitude and the cap clamp are exercised by calling it directly; the master-switch test goes through
    /// the real dispatch site (Creature.RollMonsterEffectAvoidance), since the switch is a dispatch-site
    /// precondition, not something the handler itself checks.
    /// </summary>
    [TestClass]
    public class MonsterEffectAvoidTests
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

            PropertyManager.ModifyDouble("monster_effect_avoidance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_avoidance_cap"].Item);
        }

        private static readonly AvoidEffect Effect = new AvoidEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"avoid {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("pct=0.15"), out _), "type= defaults to any");
            Assert.IsTrue(Effect.Validate(Spec("pct=0.15 type=melee"), out _));

            Assert.IsFalse(Effect.Validate(Spec("pct=0"), out var error), "pct= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("pct=0.15 type=bogus"), out _), "type= must be any or melee");
        }

        [TestMethod]
        public void Magnitude_GatesOnTypeAndReturnsTheAuthoredPct()
        {
            var defender = TestCreatures.CreateDefender();
            var attacker = TestCreatures.CreateAttacker();
            var state = new MonsterEffectState();

            var meleeOnly = Spec("pct=0.2 type=melee");

            Assert.AreEqual(0.2, Effect.GetAvoidChance(defender, attacker, CombatType.Melee, meleeOnly, ref state), 0.0001);
            Assert.AreEqual(0.0, Effect.GetAvoidChance(defender, attacker, CombatType.Missile, meleeOnly, ref state), 0.0001,
                "type=melee must not contribute against a missile attack");

            var any = Spec("pct=0.2");

            Assert.AreEqual(0.2, Effect.GetAvoidChance(defender, attacker, CombatType.Missile, any, ref state), 0.0001,
                "type=any (the default) contributes against any combat type");
        }

        [TestMethod]
        public void CapClamp_PctIsClampedByTheAvoidanceCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 0.1));

            var defender = TestCreatures.CreateDefender();
            var attacker = TestCreatures.CreateAttacker();
            var state = new MonsterEffectState();

            var chance = Effect.GetAvoidChance(defender, attacker, CombatType.Melee, Spec("pct=0.9"), ref state);

            Assert.AreEqual(0.1, chance, 0.0001, "the cap must win over the authored 0.9");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheAvoidance()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 1.0));

            // no overpower: overpower skips the avoidance block entirely, which would defeat this test
            var attacker = TestCreatures.CreateAttacker(overpower: false);
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=1.0"))));

            var avoided = defender.RollMonsterEffectAvoidance(attacker, CombatType.Melee);

            Assert.IsFalse(avoided, "the master switch must stop the avoidance entirely");
        }
    }
}
