using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ReflectEffect: sends damage back at the source, on=hit as a fraction of the damage just taken (pct=,
    /// synchronous, dispatched from AbsorbMonsterEffectDamage) and on=avoid as a flat amount (flat=, dispatched
    /// from OnAvoided, called directly here rather than through the pooled avoidance roll - see
    /// MonsterEffectAvoidIntegrationTests for on=avoid reached through the real dispatch site alongside a real
    /// avoidance granter).
    /// </summary>
    [TestClass]
    public class MonsterEffectReflectTests
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

            PropertyManager.ModifyDouble("monster_effect_reflect_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_reflect_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_damage_rider_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_damage_rider_cap"].Item);
        }

        private static readonly ReflectEffect Effect = new ReflectEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"reflect {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            // on=hit (the default) requires pct=
            Assert.IsTrue(Effect.Validate(Spec("pct=0.5"), out _));
            Assert.IsFalse(Effect.Validate(Spec("pct=0"), out var pctError), "on=hit requires pct= > 0");
            Assert.IsFalse(string.IsNullOrEmpty(pctError));

            // on=avoid requires flat=, not pct=
            Assert.IsTrue(Effect.Validate(Spec("on=avoid flat=15"), out _));
            Assert.IsFalse(Effect.Validate(Spec("on=avoid pct=0.5"), out var flatError), "on=avoid requires flat=, pct= alone is not enough");
            Assert.IsFalse(string.IsNullOrEmpty(flatError));
            Assert.IsFalse(Effect.Validate(Spec("on=avoid flat=0"), out _), "on=avoid flat= must be > 0");

            // a record naming both triggers must author both args
            Assert.IsTrue(Effect.Validate(Spec("on=hit,avoid pct=0.5 flat=15"), out _));
            Assert.IsFalse(Effect.Validate(Spec("on=hit,avoid pct=0.5"), out _), "on=hit,avoid without flat= must be rejected");
        }

        [TestMethod]
        public void Magnitude_ReflectsAFractionOfIncomingDamageBackAtTheAttacker()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;
            defender.Health.Current = 500;

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=0.5"))));

            var landed = defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20);

            Assert.AreEqual(20u, landed, "a reflect returns the incoming amount unchanged");
            Assert.AreEqual(90u, attacker.Health.Current, "20 * 0.5 = 10 reflected back at the attacker");
        }

        [TestMethod]
        public void CapClamp_PctIsClampedByTheReflectCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_reflect_cap", 0.1));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;
            defender.Health.Current = 500;

            // authored pct (0.9) is far above the cap (0.1); the cap must win
            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=0.9"))));

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20);

            Assert.AreEqual(98u, attacker.Health.Current, "20 * capped 0.1 = 2, not 20 * 0.9 = 18");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheReflect()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;
            defender.Health.Current = 500;

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=0.5"))));

            defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20);

            Assert.AreEqual(100u, attacker.Health.Current, "the master switch must stop the reflect entirely");
        }

        /// <summary>
        /// on=avoid's flat magnitude, called directly (no landed-hit damage figure exists for pct= to
        /// multiply on an avoided attack - see the class doc comment). Not routed through
        /// AbsorbMonsterEffectDamage, since on=avoid is not dispatched there at all.
        /// </summary>
        [TestMethod]
        public void OnAvoided_DealsAFlatAmountBackAtTheAttacker()
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;

            var spec = Spec("on=avoid flat=15");
            var state = new MonsterEffectState();

            Effect.OnAvoided(defender, attacker, spec, ref state);

            Assert.AreEqual(85u, attacker.Health.Current, "15 flat damage reflected back at the attacker");
        }

        /// <summary>
        /// The cap tunable for on=avoid's flat magnitude is monster_effect_damage_rider_cap, not
        /// monster_effect_reflect_cap - flat= is an absolute amount, not a fraction, so the fraction cap
        /// cannot bound it.
        /// </summary>
        [TestMethod]
        public void OnAvoided_FlatIsClampedByTheDamageRiderCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_damage_rider_cap", 5));

            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 100;

            var spec = Spec("on=avoid flat=15");
            var state = new MonsterEffectState();

            Effect.OnAvoided(defender, attacker, spec, ref state);

            Assert.AreEqual(95u, attacker.Health.Current, "flat=15 clamped to the cap of 5");
        }
    }
}
