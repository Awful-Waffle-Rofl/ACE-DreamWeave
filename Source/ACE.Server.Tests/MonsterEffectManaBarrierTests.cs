using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ManaBarrierEffect: a share of incoming damage is paid from this monster's own Mana instead of its
    /// Health, reusing ManaBarrierAbility.Resolve as a pre-deduction FILTER. The player-side ability now
    /// uses the same shape; it was a post-deduction refund until 2026-09-08, and an earlier revision of
    /// this comment called the two "the same arithmetic reached from opposite ends of the vital write",
    /// which was false and is what let the overkill bug ship - a vital write clamps at zero, so a refund
    /// after it is sized off the victim's remaining health rather than the damage thrown. See
    /// ManaBarrierEffect's class doc comment. Exercised through the real dispatch site
    /// (Creature.AbsorbMonsterEffectDamage), same as MonsterEffectReflectTests/MonsterEffectWardTests, with a
    /// defender authored with an explicit MaxMana (TestCreatures' fixtures author no PropertiesAttribute2nd
    /// for Mana, and the real attribute formula alone would leave it near zero).
    /// </summary>
    [TestClass]
    public class MonsterEffectManaBarrierTests
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

            PropertyManager.ModifyDouble("monster_effect_manabarrier_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_manabarrier_cap"].Item);
        }

        private static readonly ManaBarrierEffect Effect = new ManaBarrierEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"manabarrier {args}", out var specs, out _);
            return specs[0];
        }

        private static uint nextWcid = 991000;

        /// <summary>A defender with an explicit, generous MaxMana - see the class doc comment.</summary>
        private static Creature CreateManaDefender(uint maxMana)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesAttribute2nd = new System.Collections.Generic.Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = 500 } },
                    { PropertyAttribute2nd.MaxMana, new PropertiesAttribute2nd { InitLevel = maxMana } },
                },
            };

            var creature = new Creature(weenie, new ObjectGuid(0x7F000000 + nextWcid));
            creature.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            return creature;
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("pct=0.25"), out _), "rate= defaults to 1.0");
            Assert.IsTrue(Effect.Validate(Spec("pct=0.25 rate=2.0"), out _));

            Assert.IsFalse(Effect.Validate(Spec("pct=0"), out var error), "pct= must be > 0");
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(Effect.Validate(Spec("pct=1.5"), out _), "pct= must be <= 1");
            Assert.IsFalse(Effect.Validate(Spec("pct=0.25 rate=0"), out _), "rate= must be > 0");
        }

        [TestMethod]
        public void Magnitude_DivertsAShareOfIncomingDamageFromManaAtOneManaPerHealth()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = CreateManaDefender(maxMana: 100);
            defender.Mana.Current = 100;

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=0.5 rate=1.0"))));

            var landed = defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(10u, landed, "half of the 20 incoming damage (10) is diverted to Mana");
            Assert.AreEqual(90u, defender.Mana.Current, "10 health-equivalent at rate=1.0 costs 10 Mana");
        }

        [TestMethod]
        public void CapClamp_PaysWhatItCanWhenManaRunsShort()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = CreateManaDefender(maxMana: 100);
            defender.Mana.Current = 3;

            // pct=0.5 of 20 wants to divert 10 health-equivalent at rate=1.0 (10 mana), but only 3 is on hand
            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=0.5 rate=1.0"))));

            var landed = defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(17u, landed, "only 3 health-equivalent could be afforded with 3 mana on hand");
            Assert.AreEqual(0u, defender.Mana.Current, "every last point of mana on hand was spent, never more");
        }

        [TestMethod]
        public void CapClamp_PctIsClampedByTheManaBarrierCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_manabarrier_cap", 0.5));

            var attacker = TestCreatures.CreateAttacker();
            var defender = CreateManaDefender(maxMana: 1000);
            defender.Mana.Current = 1000;

            // authored pct=1.0 would divert everything unclamped - the cap must hold it to 0.5 regardless
            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=1.0 rate=1.0"))));

            var landed = defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(10u, landed, "an authored pct=1.0 must be clamped to the 0.5 cap, diverting only half");
            Assert.AreEqual(990u, defender.Mana.Current, "only the capped 10 health-equivalent should be spent from mana");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheDivert()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateAttacker();
            var defender = CreateManaDefender(maxMana: 100);
            defender.Mana.Current = 100;

            defender.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=0.5 rate=1.0"))));

            var landed = defender.AbsorbMonsterEffectDamage(attacker, DamageType.Slash, 20, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(20u, landed, "the master switch must stop the divert entirely");
            Assert.AreEqual(100u, defender.Mana.Current, "no mana may be spent while the switch is off");
        }
    }
}
