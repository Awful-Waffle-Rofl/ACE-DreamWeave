using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SpeedEffect: a flat multiplier on this monster's attack or cast speed, gated by axis=. NO RUNTIME
    /// CLAMP in the handler - Creature.GetMonsterEffectSpeedMultiplier (the dispatch site) takes the product
    /// of every IMonsterSpeedMod a monster carries and clamps that product once, symmetrically, against
    /// monster_effect_speed_cap; this is the one kind in the catalog with no per-handler clamp, by design.
    /// </summary>
    [TestClass]
    public class MonsterEffectSpeedTests
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

            PropertyManager.ModifyDouble("monster_effect_speed_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_speed_cap"].Item);
        }

        private static readonly SpeedEffect Effect = new SpeedEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"speed {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("axis=attack pct=0.2"), out _));
            Assert.IsTrue(Effect.Validate(Spec("axis=cast pct=-0.2"), out _), "a negative pct= (a slow) is valid");

            Assert.IsFalse(Effect.Validate(Spec("pct=0.2"), out var noAxis), "axis= is required");
            Assert.IsFalse(string.IsNullOrEmpty(noAxis));
            Assert.IsFalse(Effect.Validate(Spec("axis=bogus pct=0.2"), out _), "axis= must be attack or cast");
            Assert.IsFalse(Effect.Validate(Spec("axis=attack pct=0"), out _), "pct= must not be 0 (a no-op)");
            Assert.IsFalse(Effect.Validate(Spec("axis=attack pct=-1.0"), out _), "pct= must keep the multiplier positive");
        }

        [TestMethod]
        public void Magnitude_GatesOnAxisAndReturnsOnePlusPct()
        {
            var attackOnly = Spec("axis=attack pct=0.25");
            var state = new MonsterEffectState();

            Assert.AreEqual(1.25, Effect.GetSpeedMultiplier(null, MonsterSpeedAxis.Attack, attackOnly, ref state), 0.0001);
            Assert.AreEqual(1.0, Effect.GetSpeedMultiplier(null, MonsterSpeedAxis.Cast, attackOnly, ref state), 0.0001,
                "an axis=attack record must not touch the cast axis");
        }

        [TestMethod]
        public void CapClamp_TheDispatchSiteClampsTheComposedProductNotThisHandler()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_speed_cap", 1.1));

            var attacker = TestCreatures.CreateDefender();

            var baseSpeed = attacker.GetAnimSpeed();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("axis=attack pct=4.0"))));

            Assert.AreEqual(baseSpeed * 1.1f, attacker.GetAnimSpeed(), 0.001f,
                "GetSpeedMultiplier itself returns the uncapped 5.0x; the cap is applied at the dispatch site");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheSpeedChange()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateDefender();

            var baseSpeed = attacker.GetAnimSpeed();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("axis=attack pct=0.5"))));

            Assert.AreEqual(baseSpeed, attacker.GetAnimSpeed(), 0.001f, "the master switch must stop the speed change entirely");
        }
    }
}
