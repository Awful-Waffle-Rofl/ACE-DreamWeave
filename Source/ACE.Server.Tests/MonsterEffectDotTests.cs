using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

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
    /// DotEffect: a landed hit has a chance to apply a damage-over-time to the defender. The ticks
    /// themselves run from inside a deferred ActionChain, which a bare test Creature (no landblock, so
    /// EnqueueAction silently discards) cannot be made to run - see WorldObject_Tick.EnqueueAction's
    /// detached-Creature branch. Magnitude and clamping are therefore asserted against
    /// DotEffect.ComputePerTickAmount, the pure helper OnOutgoingHit itself calls, and the disabled-tunable
    /// case is asserted against MonsterEffectState.Announced, which OnOutgoingHit sets the instant a roll
    /// succeeds - before scheduling - for exactly this reason.
    /// </summary>
    [TestClass]
    public class MonsterEffectDotTests
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

            PropertyManager.ModifyDouble("monster_effect_damage_rider_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_damage_rider_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_proc_chance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_proc_chance_cap"].Item);
        }

        private static readonly DotEffect Effect = new DotEffect();

        private static MonsterEffectSpec Spec(string args)
        {
            MonsterEffectParser.Parse($"dot {args}", out var specs, out _);
            return specs[0];
        }

        [TestMethod]
        public void Parse_AcceptsAValidRecordAndRejectsBadOnes()
        {
            Assert.IsTrue(Effect.Validate(Spec("type=acid amount=10"), out _), "ticks/interval/chance all default");

            Assert.IsFalse(Effect.Validate(Spec("amount=10"), out var noType), "type= is required");
            Assert.IsFalse(string.IsNullOrEmpty(noType));

            Assert.IsFalse(Effect.Validate(Spec("type=acid amount=0"), out _), "amount= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("type=acid amount=10 ticks=0"), out _), "ticks= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("type=acid amount=10 interval=0"), out _), "interval= must be > 0");
            Assert.IsFalse(Effect.Validate(Spec("type=acid amount=10 chance=1.5"), out _), "chance= must be <= 1");
        }

        [TestMethod]
        public void Magnitude_PerTickAmountMatchesTheAuthoredAmount()
        {
            Assert.AreEqual(10u, DotEffect.ComputePerTickAmount(10.0));

            // an authored chance=1.0 to actually be certain (default cap is 0.75)
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("type=acid amount=10 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.IsTrue(attacker.MonsterEffectStates[0].Announced, "a certain roll must mark the DoT as having procced");
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

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("type=acid amount=10 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 0.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.IsFalse(attacker.MonsterEffectStates[0].Announced, "a hit that dealt nothing must not start a damage-over-time");
        }

        [TestMethod]
        public void CapClamp_PerTickAmountIsClampedByTheDamageRiderCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_damage_rider_cap", 50.0));

            Assert.AreEqual(50u, DotEffect.ComputePerTickAmount(10000.0), "the runaway guard must clamp an oversized authored amount");
            Assert.AreEqual(20u, DotEffect.ComputePerTickAmount(20.0), "an amount under the cap is untouched");
        }

        [TestMethod]
        public void Disabled_TunableStopsTheEffectFromRollingAtAll()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("type=acid amount=10 chance=1.0"))));

            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);

            Assert.IsFalse(attacker.MonsterEffectStates[0].Announced, "the master switch must stop dispatch before the handler ever rolls");
        }

        [TestMethod]
        public void Restart_SupersededTickDealsNothingAndLeavesTheCurrentKeyIntact()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();
            var guid = defender.Guid.Full;

            var first = DotEffect.BeginDot(attacker, guid);
            var second = DotEffect.BeginDot(attacker, guid);
            var healthBefore = defender.Health.Current;

            var outcome = DotEffect.RunTick(attacker, defender, DamageType.Acid, 5, 3, first);

            Assert.AreEqual(DotEffect.TickOutcome.Superseded, outcome, "the old chain must quit once a newer proc restarts the DoT");
            Assert.AreEqual(healthBefore, defender.Health.Current, "a superseded tick must deal no damage");
            Assert.IsTrue(DotEffect.IsCurrent(attacker, guid, second), "the superseded tick must not touch the current key");

            var current = DotEffect.RunTick(attacker, defender, DamageType.Acid, 5, 3, second);

            Assert.AreEqual(DotEffect.TickOutcome.Dealt, current, "the current chain keeps ticking");
            Assert.IsTrue(defender.Health.Current < healthBefore, "the current tick deals its damage");
        }

        [TestMethod]
        public void Restart_LastTickFinishesAndRemovesTheKey()
        {
            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();
            var guid = defender.Guid.Full;

            var token = DotEffect.BeginDot(attacker, guid);

            Assert.AreEqual(DotEffect.TickOutcome.Finished, DotEffect.RunTick(attacker, defender, DamageType.Acid, 5, 1, token));
            Assert.AreEqual(0, DotEffect.ActiveDotCount(attacker), "a naturally ended chain must not leak its key");
        }

        [TestMethod]
        public void Restart_SupersededChainEndingDoesNotEvictTheNewerDot()
        {
            var attacker = TestCreatures.CreateAttacker();
            var guid = 0xB0000001u;

            var first = DotEffect.BeginDot(attacker, guid);
            var second = DotEffect.BeginDot(attacker, guid);

            DotEffect.EndDot(attacker, guid, first);

            Assert.IsTrue(DotEffect.IsCurrent(attacker, guid, second));
        }

        [TestMethod]
        public void Restart_DifferentAttackersDoNotSupersedeEachOther()
        {
            var attackerA = TestCreatures.CreateAttacker();
            var attackerB = TestCreatures.CreateAttacker();
            var guid = 0xB0000002u;

            var a = DotEffect.BeginDot(attackerA, guid);
            var b = DotEffect.BeginDot(attackerB, guid);

            Assert.IsTrue(DotEffect.IsCurrent(attackerA, guid, a), "attacker A's DoT survives attacker B's proc on the same defender");
            Assert.IsTrue(DotEffect.IsCurrent(attackerB, guid, b));
        }

        [TestMethod]
        public void Restart_TokenStoreIsMonotonic()
        {
            var attacker = TestCreatures.CreateAttacker();
            var guid = 0xB0000003u;

            var token = DotEffect.BeginDot(attacker, guid);
            DotEffect.EndDot(attacker, guid, token);
            var next = DotEffect.BeginDot(attacker, guid);

            Assert.IsTrue(next > token, "tokens only grow, so a stale token can never match a later key");
        }

        [TestMethod]
        public void Restart_ASecondProcOnTheSamePairChangesTheCurrentToken()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_proc_chance_cap", 1.0));

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender();
            var guid = defender.Guid.Full;
            var damageEvent = new DamageEvent { Damage = 10.0f, CombatType = CombatType.Melee };

            attacker.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((Effect, Spec("type=acid amount=10 chance=1.0"))));

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            var first = DotEffect.CurrentToken(attacker, guid);
            Assert.AreNotEqual(0L, first, "the first proc registers a DoT for the pair");

            attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);
            var second = DotEffect.CurrentToken(attacker, guid);

            Assert.AreNotEqual(first, second, "the second proc must restart the DoT with a fresh token");
            Assert.AreEqual(1, DotEffect.ActiveDotCount(attacker), "one key per pair");
        }

        [TestMethod]
        public void Restart_BookkeepingIsCollectedWithTheAttacker()
        {
            var weak = RegisterOnAThrowawayAttacker();

            for (var i = 0; i < 3 && weak.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.IsFalse(weak.IsAlive, "a monster that dies before its last tick must not be kept alive by the DoT bookkeeping");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RegisterOnAThrowawayAttacker()
        {
            var attacker = TestCreatures.CreateAttacker();
            DotEffect.BeginDot(attacker, 0xB0000004u);
            return new WeakReference(attacker);
        }

        [TestMethod]
        public void NotificationSuppression_IsPassedOnlyByTheTwoRiderEffects()
        {
            string serverDir = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && serverDir == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");
                if (Directory.Exists(candidate))
                    serverDir = candidate;
            }

            Assert.IsNotNull(serverDir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}.");

            var sep = Path.DirectorySeparatorChar;
            var hits = new List<string>();

            foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}"))
                    continue;

                if (File.ReadAllText(file).Contains("sendDefenderNotification: false", StringComparison.Ordinal))
                    hits.Add(Path.GetFileName(file));
            }

            hits.Sort(StringComparer.Ordinal);

            CollectionAssert.AreEqual(new[] { "DotEffect.cs", "FlatDamageEffect.cs" }, hits,
                "only the flat-damage and DoT riders (which send their own chat line) may suppress the generic defender notification");
        }    }
}
