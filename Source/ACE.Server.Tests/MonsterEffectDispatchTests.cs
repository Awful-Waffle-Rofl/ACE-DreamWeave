using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Dispatch half of the monster combat effect system: the eight combat sites that ask a monster's
    /// effects to run. MonsterEffectRegistry ships no handlers, so in production every one of these is
    /// provably a no-op - which is exactly why the wiring needs a test of its own. Nothing else would
    /// notice a site that was never connected, or one that was quietly deleted later.
    ///
    /// The fixture registers its own recording handler through the InternalsVisibleTo seams
    /// (MonsterEffectSet.BuildFromHandlers + Creature.AttachMonsterEffectsForTest) rather than making the
    /// production registry mutable, so the handler catalogue stays exactly as empty as phase 0 says it is.
    ///
    /// FOUR of the eight sites are driven end to end - the outgoing hit and the avoidance roll through
    /// DamageEvent.CalculateDamage, incoming damage through Creature.TakeDamage, attack speed through
    /// Creature.GetAnimSpeed - because those fixtures need no database, dat files or landblock. The four
    /// magic-side sites (spell projectile in, spell projectile out, life magic, cast cadence) need a live
    /// Spell / SpellProjectile / ActionChain, so their dispatch method is tested directly and the call
    /// itself is asserted by <see cref="AllEightSites_AreWiredIntoTheCombatFiles"/>, which reads the combat
    /// sources. No test in this project constructs a live Player (see DamageEventTests' class remarks), so
    /// the player exclusions on those sites are likewise covered by the source assertion and not by a live
    /// Player instance.
    /// </summary>
    [TestClass]
    public class MonsterEffectDispatchTests
    {
        private const float Epsilon = 0.001f;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved:
            // every dispatch method reads monster_effects_enabled and most read a cap
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            // PropertyManager's cache is process-wide static state, so a test that moves a tunable has to
            // put it back or the next class to run inherits it
            PropertyManager.ModifyBool("monster_effects_enabled",
                DefaultPropertyManager.DefaultBooleanProperties["monster_effects_enabled"].Item);

            PropertyManager.ModifyDouble("monster_effect_avoidance_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_avoidance_cap"].Item);

            PropertyManager.ModifyDouble("monster_effect_speed_cap",
                DefaultPropertyManager.DefaultDoubleProperties["monster_effect_speed_cap"].Item);

            PropertyManager.ModifyLong("monster_effect_recast_cap",
                DefaultPropertyManager.DefaultLongProperties["monster_effect_recast_cap"].Item);
        }

        // ---- S1: outgoing hit, DamageEvent.CalculateDamage --------------------------------------------

        [TestMethod]
        public void OutgoingHit_IsDispatchedForAMonsterAttacker()
        {
            var recorder = new RecordingEffect();

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            Attach(attacker, recorder);

            var damageEvent = DamageEvent.CalculateDamage(attacker, defender, null);

            Assert.IsTrue(damageEvent.HasDamage, "the overpower fixture must land its hit");
            Assert.AreEqual(1, recorder.OutgoingHits);
            Assert.AreSame(defender, recorder.LastDefender);
        }

        [TestMethod]
        public void OutgoingHit_IsNotDispatchedForACreatureCarryingNoEffects()
        {
            var recorder = new RecordingEffect();

            var attacker = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            // attached and then cleared: the null field is the first early-out of every dispatch method,
            // and is the state nearly every monster in the world is in
            Attach(attacker, recorder);
            attacker.AttachMonsterEffectsForTest(null);

            DamageEvent.CalculateDamage(attacker, defender, null);

            Assert.AreEqual(0, recorder.OutgoingHits);
        }

        // ---- S2: avoidance, DamageEvent.DoCalculateDamage ---------------------------------------------

        [TestMethod]
        public void Avoidance_IsRolledForAMonsterDefenderAndCanAvoidTheHit()
        {
            // the pooled chance is clamped by the cap before it is rolled, so the cap has to be lifted for
            // a certain avoid to actually be certain
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 1.0));

            var recorder = new RecordingEffect { AvoidChance = 1.0 };

            // no overpower: overpower skips the avoidance block and the evade roll alike
            var attacker = TestCreatures.CreateAttacker(overpower: false);
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            Attach(defender, recorder);

            var damageEvent = DamageEvent.CalculateDamage(attacker, defender, null);

            Assert.IsTrue(damageEvent.Evaded, "a certain avoid must stop the hit");
            Assert.IsFalse(damageEvent.HasDamage);
            Assert.IsTrue(recorder.AvoidChanceReads > 0);
            Assert.AreEqual(1, recorder.Avoided);
        }

        [TestMethod]
        public void Avoidance_PoolIsClampedByTheCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_avoidance_cap", 0.0));

            var recorder = new RecordingEffect { AvoidChance = 1.0 };

            var attacker = TestCreatures.CreateAttacker(overpower: false);
            var defender = TestCreatures.CreateDefender(baseArmor: 100, meleeDefense: 0);

            Attach(defender, recorder);

            DamageEvent.CalculateDamage(attacker, defender, null);

            Assert.IsTrue(recorder.AvoidChanceReads > 0, "the chance must still be asked for");
            Assert.AreEqual(0, recorder.Avoided, "a cap of zero must let nothing through");
        }

        // ---- S3: incoming damage, Creature.TakeDamage -------------------------------------------------

        [TestMethod]
        public void IncomingDamage_FiltersTheHitBeforeTheVitalWrite()
        {
            var recorder = new RecordingEffect { IncomingDamageMultiplier = 0.5 };

            var source = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            Attach(defender, recorder);

            var startingHealth = defender.Health.Current;

            var applied = defender.TakeDamage(source, DamageType.Slash, 10.0f);

            Assert.AreEqual(1, recorder.IncomingDamage);
            Assert.AreEqual(5u, applied, "the filtered amount is what lands");
            Assert.AreEqual(startingHealth - 5, defender.Health.Current, "and what the vital write sees");
        }

        [TestMethod]
        public void IncomingDamage_IsNotDispatchedForAHeal()
        {
            var recorder = new RecordingEffect { IncomingDamageMultiplier = 0.5 };

            var source = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            Attach(defender, recorder);

            defender.TakeDamage(source, DamageType.Health, -10.0f);

            Assert.AreEqual(0, recorder.IncomingDamage, "a negative amount is a heal and no filter may eat it");
        }

        /// <summary>
        /// The reflect latch, which is the whole reason this dispatch is not a plain loop. A handler that
        /// answers damage by damaging its attacker re-enters this method from inside itself; the latch
        /// refuses the second entry so the two systems cannot trade damage until the stack ends.
        /// </summary>
        [TestMethod]
        public void IncomingDamage_ReentrantDispatchIsRefusedByTheReflectLatch()
        {
            var recorder = new RecordingEffect();

            var source = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            Attach(defender, recorder);

            // Stands in for a reflect handler: answers the hit by driving damage back through the same
            // dispatch. Self-limited at ten so that REMOVING the latch fails this assertion instead of
            // overflowing the stack and taking the whole test host with it.
            recorder.OnIncomingDamageAction = (creature, amount) =>
            {
                if (recorder.IncomingDamage < 10)
                    creature.AbsorbMonsterEffectDamage(source, DamageType.Slash, amount, IncomingDamageOrigin.DirectHit);
            };

            defender.TakeDamage(source, DamageType.Slash, 10.0f);

            Assert.AreEqual(1, recorder.IncomingDamage, "the re-entrant dispatch must be refused outright");
        }

        // ---- S4a / S4b: the other two paths to the same method -----------------------------------------

        [TestMethod]
        public void IncomingDamage_IsNotDispatchedForACreatureCarryingNoEffects()
        {
            var recorder = new RecordingEffect { IncomingDamageMultiplier = 0.5 };

            var source = TestCreatures.CreateAttacker();
            var defender = TestCreatures.CreateDefender(maxHealth: 500);

            var unfiltered = defender.AbsorbMonsterEffectDamage(source, DamageType.Slash, 40, IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(40u, unfiltered);
            Assert.AreEqual(0, recorder.IncomingDamage);
        }

        // ---- S5: spell hit ----------------------------------------------------------------------------

        [TestMethod]
        public void SpellHit_ScalesTheDamageBeforeItIsApplied()
        {
            var recorder = new RecordingEffect { SpellDamageMultiplier = 2.0f };

            var caster = TestCreatures.CreateAttacker();
            var target = TestCreatures.CreateDefender();

            Attach(caster, recorder);

            var damage = 50.0f;

            caster.ApplySpellHitMonsterEffects(target, null, ref damage);

            Assert.AreEqual(1, recorder.SpellHits);
            Assert.AreEqual(100.0f, damage, Epsilon, "the hook takes damage by ref so a ramp can scale it");
        }

        // ---- S6: attack speed, Creature.GetAnimSpeed ---------------------------------------------------

        [TestMethod]
        public void AttackSpeed_IsDispatchedFromGetAnimSpeed()
        {
            var recorder = new RecordingEffect { SpeedMultiplier = 1.5 };

            var creature = TestCreatures.CreateDefender();

            var baseSpeed = creature.GetAnimSpeed();

            Attach(creature, recorder);

            var modified = creature.GetAnimSpeed();

            Assert.IsTrue(recorder.SpeedReads > 0);
            Assert.AreEqual(MonsterSpeedAxis.Attack, recorder.LastSpeedAxis);
            Assert.AreEqual(baseSpeed * 1.5f, modified, Epsilon);
        }

        [TestMethod]
        public void AttackSpeed_ComposedMultiplierIsClampedByTheSpeedCap()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("monster_effect_speed_cap", 1.25));

            var recorder = new RecordingEffect { SpeedMultiplier = 4.0 };

            var creature = TestCreatures.CreateDefender();

            var baseSpeed = creature.GetAnimSpeed();

            Attach(creature, recorder);

            Assert.AreEqual(baseSpeed * 1.25f, creature.GetAnimSpeed(), Epsilon);
        }

        // ---- S7: cast cadence --------------------------------------------------------------------------

        [TestMethod]
        public void CastTime_IsDividedByTheCastSpeedMultiplier()
        {
            var recorder = new RecordingEffect { SpeedMultiplier = 2.0 };

            var creature = TestCreatures.CreateDefender();

            Assert.AreEqual(3.0f, creature.ScaleMonsterEffectCastTime(3.0f), Epsilon, "no effects, no change");

            Attach(creature, recorder);

            Assert.AreEqual(1.5f, creature.ScaleMonsterEffectCastTime(3.0f), Epsilon, "a cast time is a duration, so the multiplier divides");
            Assert.AreEqual(MonsterSpeedAxis.Cast, recorder.LastSpeedAxis);
        }

        [TestMethod]
        public void CastComplete_IsDispatchedAndTellsAHandlerWhenItMayChain()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("monster_effect_recast_cap", 2));

            var recorder = new RecordingEffect();

            var creature = TestCreatures.CreateDefender();

            Attach(creature, recorder);

            // a well-behaved recast handler: asks first, and chains only while the answer is yes
            recorder.OnCastCompleteAction = caster =>
            {
                if (Creature.CanChainMonsterEffectCast)
                    caster.OnMonsterEffectCastComplete(null);
            };

            creature.OnMonsterEffectCastComplete(null);

            // the monster's own cast plus exactly monster_effect_recast_cast chained ones
            Assert.AreEqual(3, recorder.CastCompletions);
        }

        /// <summary>
        /// The backstop half of the recast guard: a handler that chains without asking is still bounded,
        /// because the completion dispatch of an unauthorised chained cast is refused.
        /// </summary>
        [TestMethod]
        public void CastComplete_BoundsAHandlerThatChainsWithoutAsking()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("monster_effect_recast_cap", 2));

            var recorder = new RecordingEffect();

            var creature = TestCreatures.CreateDefender();

            Attach(creature, recorder);

            recorder.OnCastCompleteAction = caster => caster.OnMonsterEffectCastComplete(null);

            creature.OnMonsterEffectCastComplete(null);

            // one dispatch per authorised depth, and the first unauthorised one is refused
            Assert.AreEqual(3, recorder.CastCompletions);
        }

        [TestMethod]
        public void CastComplete_ACapOfZeroStillDispatchesTheHookAndForbidsChaining()
        {
            Assert.IsTrue(PropertyManager.ModifyLong("monster_effect_recast_cap", 0));

            var recorder = new RecordingEffect();

            var creature = TestCreatures.CreateDefender();

            Attach(creature, recorder);

            recorder.OnCastCompleteAction = caster => Assert.IsFalse(Creature.CanChainMonsterEffectCast);

            creature.OnMonsterEffectCastComplete(null);

            Assert.AreEqual(1, recorder.CastCompletions, "the cap bounds chaining, it does not disable the hook");
        }

        // ---- S8: heartbeat, Creature.Heartbeat ---------------------------------------------------------

        [TestMethod]
        public void Heartbeat_IsDispatchedFromTheCreatureHeartbeat()
        {
            var recorder = new RecordingEffect();

            var creature = TestCreatures.CreateDefender();

            Attach(creature, recorder);

            creature.MonsterEffectHeartbeat();

            Assert.AreEqual(1, recorder.Heartbeats);
        }

        // ---- shared preconditions ----------------------------------------------------------------------

        [TestMethod]
        public void EveryHook_IsSilentWhenTheSystemIsDisabled()
        {
            Assert.IsTrue(PropertyManager.ModifyBool("monster_effects_enabled", false));

            var recorder = new RecordingEffect { AvoidChance = 1.0, SpeedMultiplier = 2.0, IncomingDamageMultiplier = 0.0, SpellDamageMultiplier = 2.0f };

            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            var other = TestCreatures.CreateAttacker();

            Attach(creature, recorder);

            DispatchEveryHook(creature, other);

            AssertNothingRan(recorder);
        }

        [TestMethod]
        public void EveryHook_IsSilentWhenTheCreatureCarriesNoEffects()
        {
            var recorder = new RecordingEffect { AvoidChance = 1.0, SpeedMultiplier = 2.0, IncomingDamageMultiplier = 0.0, SpellDamageMultiplier = 2.0f };

            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            var other = TestCreatures.CreateAttacker();

            Attach(creature, recorder);
            creature.AttachMonsterEffectsForTest(null);

            DispatchEveryHook(creature, other);

            AssertNothingRan(recorder);
        }

        /// <summary>
        /// The third early-out: a monster whose effects implement none of the hooks walks an empty index
        /// array at every site rather than testing each entry's interfaces.
        /// </summary>
        [TestMethod]
        public void EveryHook_IsSilentWhenNoEffectImplementsIt()
        {
            var creature = TestCreatures.CreateDefender(maxHealth: 500);
            var other = TestCreatures.CreateAttacker();

            creature.AttachMonsterEffectsForTest(
                MonsterEffectSet.BuildFromHandlers((new HooklessEffect(), Spec("hookless"))));

            var set = creature.MonsterEffects;

            Assert.AreEqual(1, set.Count, "the effect still holds its slot in the state array");

            Assert.AreEqual(0, set.OutgoingHitIndexes.Count, "outgoing hit");
            Assert.AreEqual(0, set.AvoidanceIndexes.Count, "avoidance");
            Assert.AreEqual(0, set.IncomingDamageIndexes.Count, "incoming damage");
            Assert.AreEqual(0, set.SpellHitIndexes.Count, "spell hit");
            Assert.AreEqual(0, set.SpeedModIndexes.Count, "speed");
            Assert.AreEqual(0, set.CastHookIndexes.Count, "cast complete");
            Assert.AreEqual(0, set.HeartbeatIndexes.Count, "heartbeat");

            // every site walks an empty array and returns; the assertion is that none of them throws
            DispatchEveryHook(creature, other);
        }

        // ---- the sites themselves ----------------------------------------------------------------------

        /// <summary>
        /// The four magic-side sites cannot be driven from this project (they need a live Spell,
        /// SpellProjectile and ActionChain), and no site's player exclusion can be, because no test here
        /// constructs a Player. This asserts the call and its guard are present in the combat source
        /// instead - which is also the guard against a later change quietly dropping one of the eight.
        /// </summary>
        [TestMethod]
        public void AllEightSites_AreWiredIntoTheCombatFiles()
        {
            AssertSource("S1 outgoing hit", "Source/ACE.Server/Entity/DamageEvent.cs",
                "if (attacker is not Player && damageEvent.HasDamage)",
                "attacker.ApplyOutgoingHitMonsterEffects(defender, damageEvent);");

            AssertSource("S2 avoidance", "Source/ACE.Server/Entity/DamageEvent.cs",
                "if (!Overpower && playerDefender == null && defender.RollMonsterEffectAvoidance(attacker, CombatType))");

            AssertSource("S3 incoming physical", "Source/ACE.Server/WorldObjects/Monster_Combat.cs",
                "tryDamage = tryDamage > 0 ? (int)AbsorbMonsterEffectDamage(source, damageType, (uint)tryDamage, origin) : tryDamage;");

            // S3's origin: TakeDamage defaults to Secondary, so a landed strike reaches reflect on=hit only where
            // its call site passes DirectHit. These four are the physical strike sites that do (the spell and
            // life-magic sites carry their own origin, pinned above). Dropping the argument at any of them would
            // silently turn that attack type's reflect off; adding it to a DoT or proc site would make it reflect.
            AssertSource("S3 origin, player strike", "Source/ACE.Server/WorldObjects/Player_Combat.cs",
                "target.TakeDamage(this, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);");

            AssertSource("S3 origin, monster/pet missile", "Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs",
                "targetCreature.TakeDamage(sourceCreature, damageEvent.DamageType, damageEvent.Damage, false, IncomingDamageOrigin.DirectHit);");

            AssertSource("S3 origin, monster/pet melee", "Source/ACE.Server/WorldObjects/Monster_Melee.cs",
                "target.TakeDamage(this, damageEvent.DamageType, damageEvent.Damage, false, IncomingDamageOrigin.DirectHit);");

            AssertSource("S3 origin, monster riposte counter", "Source/ACE.Server/MonsterEffects/Effects/RiposteEffect.cs",
                "counterTarget.TakeDamage(counterAttacker, damageEvent.DamageType, damageEvent.Damage, damageEvent.IsCritical, IncomingDamageOrigin.DirectHit);");

            AssertSource("S4a incoming spell projectile", "Source/ACE.Server/WorldObjects/SpellProjectile.cs",
                "if (targetPlayer == null && damage > 0.0f)",
                "target.AbsorbMonsterEffectDamage(ProjectileSource, Spell.DamageType, beforeMonsterEffects, IncomingDamageOrigin);");

            AssertSource("S4b incoming life magic", "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs",
                "targetCreature is not Player",
                "targetCreature.AbsorbMonsterEffectDamage(this, DamageType.Health, (uint)-tryBoost, boostOrigin);");

            AssertSource("S5 monster spell hit", "Source/ACE.Server/WorldObjects/SpellProjectile.cs",
                "if (sourceCreature != null && player == null)",
                "sourceCreature.ApplySpellHitMonsterEffects(creatureTarget, this, ref spellDamage);");

            AssertSource("S6 attack speed", "Source/ACE.Server/WorldObjects/Creature_Combat.cs",
                "animSpeed = player.ApplyClassAbilityAttackSpeed(animSpeed, includeConditional: true);",
                "animSpeed = ApplyMonsterEffectAttackSpeed(animSpeed);");

            AssertSource("S7 cast cadence", "Source/ACE.Server/WorldObjects/Monster_Magic.cs",
                "var preCastTime = ScaleMonsterEffectCastTime(PreCastMotion(AttackTarget));",
                "OnMonsterEffectCastComplete(spell);",
                "var postCastTime = ScaleMonsterEffectCastTime(GetPostCastTime(spell));");

            AssertSource("S8 lifecycle", "Source/ACE.Server/WorldObjects/Creature_Tick.cs",
                "MonsterEffectHeartbeat();");
        }

        // ---- fixture helpers ---------------------------------------------------------------------------

        private static MonsterEffectSpec Spec(string kind) =>
            new MonsterEffectSpec(kind, new Dictionary<string, string>());

        private static void Attach(Creature creature, RecordingEffect recorder) =>
            creature.AttachMonsterEffectsForTest(MonsterEffectSet.BuildFromHandlers((recorder, Spec(recorder.Kind))));

        /// <summary>
        /// Calls every dispatch method once, so a precondition test asserts the precondition for all eight
        /// hooks rather than for whichever one it happened to pick.
        /// </summary>
        private static void DispatchEveryHook(Creature creature, Creature other)
        {
            var damage = 10.0f;

            creature.ApplyOutgoingHitMonsterEffects(other, new DamageEvent());
            creature.RollMonsterEffectAvoidance(other, CombatType.Melee);
            creature.AbsorbMonsterEffectDamage(other, DamageType.Slash, 10, IncomingDamageOrigin.DirectHit);
            creature.ApplySpellHitMonsterEffects(other, null, ref damage);
            creature.ApplyMonsterEffectAttackSpeed(1.0f);
            creature.ScaleMonsterEffectCastTime(1.0f);
            creature.OnMonsterEffectCastComplete(null);
            creature.MonsterEffectHeartbeat();
        }

        private static void AssertNothingRan(RecordingEffect recorder)
        {
            Assert.AreEqual(0, recorder.OutgoingHits, "outgoing hit");
            Assert.AreEqual(0, recorder.AvoidChanceReads, "avoidance");
            Assert.AreEqual(0, recorder.IncomingDamage, "incoming damage");
            Assert.AreEqual(0, recorder.SpellHits, "spell hit");
            Assert.AreEqual(0, recorder.SpeedReads, "speed");
            Assert.AreEqual(0, recorder.CastCompletions, "cast complete");
            Assert.AreEqual(0, recorder.Heartbeats, "heartbeat");
        }

        private static void AssertSource(string site, string relativePath, params string[] required)
        {
            var path = FindInSourceTree(relativePath);

            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var source = File.ReadAllText(path);

            foreach (var fragment in required)
                Assert.IsTrue(source.Contains(fragment, StringComparison.Ordinal),
                    $"{site}: {relativePath} no longer contains \"{fragment}\".");
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        // ---- fakes --------------------------------------------------------------------------------------

        /// <summary>
        /// One handler implementing every hook, so a single attached effect lands in all eight buckets and
        /// one fixture serves every site. Records what it was asked and returns whatever the test set.
        /// </summary>
        private sealed class RecordingEffect : IMonsterOutgoingHit, IMonsterIncomingDamage, IMonsterAvoidance,
                                               IMonsterSpellHit, IMonsterSpeedMod, IMonsterCastHook, IMonsterHeartbeat
        {
            public string Kind => "testrecorder";

            public bool Validate(MonsterEffectSpec spec, out string error)
            {
                error = null;
                return true;
            }

            public int OutgoingHits;
            public int IncomingDamage;
            public int AvoidChanceReads;
            public int Avoided;
            public int SpellHits;
            public int SpeedReads;
            public int CastCompletions;
            public int Heartbeats;

            public Creature LastDefender;
            public MonsterSpeedAxis LastSpeedAxis;

            public double AvoidChance;
            public double IncomingDamageMultiplier = 1.0;
            public double SpeedMultiplier = 1.0;
            public float SpellDamageMultiplier = 1.0f;

            public Action<Creature, uint> OnIncomingDamageAction;
            public Action<Creature> OnCastCompleteAction;

            public void OnOutgoingHit(Creature attacker, Creature defender, DamageEvent damageEvent, MonsterEffectSpec spec, ref MonsterEffectState state)
            {
                OutgoingHits++;
                LastDefender = defender;
            }

            public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount, IncomingDamageOrigin origin, MonsterEffectSpec spec, ref MonsterEffectState state)
            {
                IncomingDamage++;

                OnIncomingDamageAction?.Invoke(defender, amount);

                return (uint)Math.Round(amount * IncomingDamageMultiplier);
            }

            public double GetAvoidChance(Creature defender, Creature attacker, CombatType combatType, MonsterEffectSpec spec, ref MonsterEffectState state)
            {
                AvoidChanceReads++;
                return AvoidChance;
            }

            public void OnAvoided(Creature defender, Creature attacker, MonsterEffectSpec spec, ref MonsterEffectState state) => Avoided++;

            public void OnSpellHit(Creature caster, Creature target, SpellProjectile projectile, ref float damage, MonsterEffectSpec spec, ref MonsterEffectState state)
            {
                SpellHits++;
                damage *= SpellDamageMultiplier;
            }

            public double GetSpeedMultiplier(Creature creature, MonsterSpeedAxis axis, MonsterEffectSpec spec, ref MonsterEffectState state)
            {
                SpeedReads++;
                LastSpeedAxis = axis;
                return SpeedMultiplier;
            }

            public void OnCastComplete(Creature caster, Spell spell, MonsterEffectSpec spec, ref MonsterEffectState state)
            {
                CastCompletions++;
                OnCastCompleteAction?.Invoke(caster);
            }

            public void OnHeartbeat(Creature creature, MonsterEffectSpec spec, ref MonsterEffectState state) => Heartbeats++;
        }

        /// <summary>
        /// A handler with no hooks at all - the shape of a reserved kind whose phase has shipped nothing.
        /// It occupies a state slot and appears in no bucket.
        /// </summary>
        private sealed class HooklessEffect : IMonsterEffect
        {
            public string Kind => "testhookless";

            public bool Validate(MonsterEffectSpec spec, out string error)
            {
                error = null;
                return true;
            }
        }
    }
}
