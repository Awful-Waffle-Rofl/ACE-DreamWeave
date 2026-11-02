using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ScatterEffect: shortens the Taunt hold, never cancels it. The handler returns (1 - pct); the product
    /// and the monster_effect_taunt_hold_floor clamp live at Creature.GetMonsterEffectTauntHoldMultiplier,
    /// and Creature.ApplyTaunt scales the duration it writes into TauntExpiry by that multiplier.
    ///
    /// Reads monster_effects_enabled and monster_effect_taunt_hold_floor; both are seeded from the code
    /// defaults in ClassInitialize and every test that moves one restores it in a finally block.
    /// </summary>
    [TestClass]
    public class MonsterEffectScatterTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            PropertyManager.ModifyDouble("monster_effect_taunt_hold_floor",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_taunt_hold_floor"].Item);
        }

        private static readonly ScatterEffect Effect = new ScatterEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"scatter {args}", out var specs, out _);
            return specs[0];
        }

        private static Creature CarrierWith(params string[] args)
        {
            var handlers = new (IMonsterEffect, MonsterEffectSpec)[args.Length];

            for (var i = 0; i < args.Length; i++)
                handlers[i] = (Effect, Spec(args[i]));

            var creature = TestCreatures.CreateDefender();
            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers(handlers));
            return creature;
        }

        [TestMethod]
        public void Default_FloorIsAQuarter()
        {
            Assert.AreEqual(0.25, DefaultPropertyManager.DefaultDoubleProperties["monster_effect_taunt_hold_floor"].Item, 0.0001);
        }

        [TestMethod]
        public void Parse_AcceptsAFractionAndRejectsEverythingElse()
        {
            Assert.IsTrue(Effect.Validate(Spec("pct=0.4"), out _));
            Assert.IsTrue(Effect.Validate(Spec("pct=0.99"), out _));

            Assert.IsFalse(Effect.Validate(Spec(""), out var missing), "pct= is required");
            Assert.IsFalse(string.IsNullOrEmpty(missing));
            Assert.IsFalse(Effect.Validate(Spec("pct=0"), out _), "0 is a no-op");
            Assert.IsFalse(Effect.Validate(Spec("pct=1"), out _), "1 would be a zero-length hold");
            Assert.IsFalse(Effect.Validate(Spec("pct=1.5"), out _));
            Assert.IsFalse(Effect.Validate(Spec("pct=-0.2"), out _));
        }

        [TestMethod]
        public void Registry_KnowsTheKind_AndTheSetBucketsIt()
        {
            Assert.IsTrue(MonsterEffectRegistry.IsKnownKind("scatter"));

            var set = MonsterEffectSet.BuildFromHandlers((Effect, Spec("pct=0.4")));

            Assert.AreEqual(1, set.TauntHoldIndexes.Count);
        }

        [TestMethod]
        public void Multiplier_NoEffects_IsOne()
        {
            Assert.AreEqual(1.0, TestCreatures.CreateDefender().GetMonsterEffectTauntHoldMultiplier(), 0.0001);
        }

        [TestMethod]
        public void Multiplier_OneRecord_IsOneMinusPct()
        {
            Assert.AreEqual(0.6, CarrierWith("pct=0.4").GetMonsterEffectTauntHoldMultiplier(), 0.0001);
        }

        [TestMethod]
        public void Multiplier_TwoRecords_ComposeByProduct()
        {
            Assert.AreEqual(0.6 * 0.5, CarrierWith("pct=0.4", "pct=0.5").GetMonsterEffectTauntHoldMultiplier(), 0.0001);
        }

        [TestMethod]
        public void Multiplier_IsFlooredByTheTunable()
        {
            var carrier = CarrierWith("pct=0.9", "pct=0.9");

            Assert.AreEqual(0.25, carrier.GetMonsterEffectTauntHoldMultiplier(), 0.0001, "0.1 * 0.1 = 0.01 is clamped up to the default floor");

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_taunt_hold_floor", 0.5));
                Assert.AreEqual(0.5, carrier.GetMonsterEffectTauntHoldMultiplier(), 0.0001);

                Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_taunt_hold_floor", 0.0));
                Assert.AreEqual(0.25, carrier.GetMonsterEffectTauntHoldMultiplier(), 0.0001, "a non-positive floor falls back to the default, never to a zero hold");

                Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_taunt_hold_floor", 7.0));
                Assert.AreEqual(1.0, carrier.GetMonsterEffectTauntHoldMultiplier(), 0.0001, "a floor above 1 clamps to 1 (scatter off), never a longer hold");
            }
            finally
            {
                PropertyManager.ModifyDouble("monster_effect_taunt_hold_floor",
                    DefaultPropertyManager.DefaultDoubleProperties["monster_effect_taunt_hold_floor"].Item);
            }
        }

        [TestMethod]
        public void Multiplier_MasterSwitchOff_IsOne()
        {
            var carrier = CarrierWith("pct=0.4");

            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));
                Assert.AreEqual(1.0, carrier.GetMonsterEffectTauntHoldMultiplier(), 0.0001);
            }
            finally
            {
                PropertyManager.ModifyBool("monster_effects_enabled",
                    DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);
            }
        }

        [TestMethod]
        public void ApplyTaunt_WithScatter_HoldsForShorterThanWithout()
        {
            var plain = TestCreatures.CreateDefender();
            var scattered = CarrierWith("pct=0.5");

            // Player cannot be constructed in this harness, so the taunter is null: the hold arithmetic does
            // not read it. Both start Awake so ApplyTaunt skips WakeUp (which needs a landblock) and, with a
            // null target on both sides, the new-enemy emote branch.
            plain.MonsterState = Creature.State.Awake;
            scattered.MonsterState = Creature.State.Awake;

            plain.ApplyTaunt(null, 10.0);
            scattered.ApplyTaunt(null, 10.0);

            var plainHold = plain.TauntExpiry - ACE.Server.Entity.Timers.RunningTime;
            var scatteredHold = scattered.TauntExpiry - ACE.Server.Entity.Timers.RunningTime;

            Assert.IsTrue(scatteredHold < plainHold, $"scattered {scatteredHold} should be shorter than plain {plainHold}");
            Assert.IsTrue(scatteredHold > 0.0, "scatter never cancels the hold");
            Assert.AreEqual(plainHold * 0.5, scatteredHold, 0.05);
        }
    }
}
