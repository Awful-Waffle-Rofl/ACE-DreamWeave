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
    /// The granter/reactor split, end to end through the real dispatch site.
    ///
    /// avoid is the only GRANTER of the three - riposte, reflect on=avoid and debuff on=avoid all return a
    /// pure 0.0 from GetAvoidChance, because they answer an avoid rather than causing one. So the pooled roll
    /// succeeds on avoid's weight alone, and every carried handler must then be told, granter and reactor
    /// alike.
    ///
    /// THIS TEST IS THE REGRESSION GUARD FOR A REAL SHIPPED DEFECT. RollMonsterEffectAvoidance used to
    /// attribute a pooled win to exactly ONE contributor by a weighted pick over those same chances, which
    /// gave every pure-0.0 reactor zero width on the wheel: riposte, reflect on=avoid and debuff on=avoid
    /// were mathematically unable to fire, in live play, for their whole first phase. If that weighting is
    /// ever restored as an "optimisation", this test fails.
    /// </summary>
    [TestClass]
    public class MonsterEffectAvoidIntegrationTests
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

            PropertyManager.ModifyDouble("monster_effect_proc_chance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_proc_chance_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_damage_rider_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_damage_rider_cap"].Item);
        }

        private static MonsterEffectSpec Spec(string kind, string args)
        {
            MonsterEffectParser.Parse($"{kind} {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void AvoidPlusDebuffPlusReflect_EveryCarriedHandlerReachesOnAvoided()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 1.0));
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var avoid = new AvoidEffect();
            var debuff = new DebuffEffect();
            var reflect = new ReflectEffect();

            // the attacker needs real headroom on its health pool: reflect's flat= lands through
            // Creature.TakeDamage, which re-clamps the vital against its MAXIMUM, so a bare CreateAttacker
            // (endurance 1, so a max health of almost nothing) would report the cap rather than the hit
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            attacker.Health.Current = 500;

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(
                (avoid, Spec("avoid", "pct=1.0")),
                (debuff, Spec("debuff", "what=attackskills mag=30 secs=20 chance=1.0 on=avoid")),
                (reflect, Spec("reflect", "on=avoid flat=15"))));

            var avoided = defender.RollMonsterEffectAvoidance(attacker, CombatType.Melee);

            Assert.IsTrue(avoided, "the pooled roll must succeed - avoid is the only real granter, at pct=1.0");

            // avoid's own OnAvoided is a no-op unless the attacker is a Player and silent=false (not
            // exercised here), so the two reactors are what the assertions can see: debuff wrote its
            // enchantment onto the attacker, and reflect sent its flat 15 back.
            Assert.AreEqual(1, attacker.Biota.PropertiesEnchantmentRegistry.Count,
                "debuff on=avoid must reach OnAvoided - a reactor is notified on every pooled win, not on a weighted share");

            Assert.AreEqual(485u, attacker.Health.Current,
                "reflect on=avoid must reach OnAvoided too, for the same reason (flat=15 off 500)");
        }
    }
}
