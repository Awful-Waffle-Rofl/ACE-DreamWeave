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
    /// Same-hook dispatch order, which used to be whatever order a content author happened to type the
    /// records in.
    ///
    /// THE BUG THIS PINS. execute adds to DamageEvent.Damage; leech heals off it. Authored
    /// "leech ...; execute ..." the monster healed off the PRE-execute number and authored
    /// "execute ...; leech ..." off the POST-execute number - two different monsters from two strings that
    /// read as the same monster, with no warning and no other difference. MonsterEffectSet now sorts entries
    /// by IMonsterEffect.DispatchOrder at construction, so every damage mutator runs ahead of every damage
    /// reader and the authored order decides only what the rule leaves open.
    /// </summary>
    [TestClass]
    public class MonsterEffectOrderingTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static readonly LeechEffect Leech = new LeechEffect();
        private static readonly ExecuteEffect Execute = new ExecuteEffect();

        private static MonsterEffectSpec Spec(string record)
        {
            MonsterEffectParser.Parse(record, out var specs, out _);
            return specs[0];
        }

        /// <summary>
        /// Runs the same exchange twice, differing ONLY in the order the two records were authored, and
        /// returns the health the leeching monster ended on.
        /// </summary>
        private static uint HealFor(params (IMonsterEffect Handler, MonsterEffectSpec Spec)[] authored)
        {
            var attacker = TestCreatures.CreateDefender(maxHealth: 500);
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            attacker.Health.Current = 1;

            // inside execute range: 10 of 500 is well under the authored hp=0.25 window
            defender.Health.Current = 10;

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(authored));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            return attacker.Health.Current;
        }

        [TestMethod]
        public void LeechAndExecute_HealTheSameAmountWhicheverOrderTheyWereAuthoredIn()
        {
            var leechFirst = HealFor(
                (Leech, Spec("leech vital=health pct=0.5")),
                (Execute, Spec("execute bonus=1.0 hp=0.25")));

            var executeFirst = HealFor(
                (Execute, Spec("execute bonus=1.0 hp=0.25")),
                (Leech, Spec("leech vital=health pct=0.5")));

            Assert.AreEqual(executeFirst, leechFirst,
                "the authored order of a mutator and a reader must not change the outcome");

            // and it is the POST-mutator number both times, not merely a consistent one: a 10-damage hit
            // doubled by execute is 20, leeched at 0.5 for a 10-point heal on top of 1 starting health.
            // Were the rule inverted (reader first) both would read 6 instead, and the equality above would
            // still pass - which is why this second assertion exists.
            Assert.AreEqual(11u, leechFirst, "leech must size itself from the damage execute already added to");
        }
    }
}
