using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// The Balance Trials start-of-rep aggro grace (scenario startGraceSecs): wave 0 spawns held, refuses every wake until the
    /// hold expires, an attack on one held creature wakes only that one, later waves are never held, and a creature with no
    /// trial rep is never held whatever its field says. The live wake paths that need a landblock and physics (AlertFriendly,
    /// the spawn scan, Player.OnAttackMonster) are pinned by source scan; the gate itself runs for real through WakeUp(false).
    /// Reads no PropertyManager key directly (creature construction only, as TauntOnHitTests).
    /// </summary>
    [TestClass]
    public class TrialStartGraceTests
    {
        private static uint nextGuid = 0x7F5E0000;
        private static uint nextWcid = 995900;

        private static readonly Landblock SpawnedLandblock = (Landblock)RuntimeHelpers.GetUninitializedObject(typeof(Landblock));

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static Creature NewCreature(string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 100 },
                },
            };

            var c = new Creature(weenie, new ObjectGuid(nextGuid++));
            c.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            c.CurrentLandblock = SpawnedLandblock;
            return c;
        }

        /// <summary>A wave-0 trial creature, asleep, held until now + <paramref name="holdSecs"/> (negative: already expired).</summary>
        private static Creature HeldTrialCreature(TrialRep rep, double holdSecs)
        {
            var c = NewCreature("Trial Creature");
            c.P_TrialRep = rep;
            c.P_TrialHoldUntil = TrialManager.Now() + holdSecs;
            return c;
        }

        /// <summary>What every live WakeUp caller does: point the creature at the alerter, then wake it (no friend alert: that needs physics).</summary>
        private static void TryWake(Creature monster, Creature target)
        {
            monster.AttackTarget = target;
            monster.WakeUp(alertNearby: false);
        }

        // ------------------------------------------------------------------ the gate, run for real

        [TestMethod]
        public void A_held_creature_does_not_wake_or_keep_a_target_before_expiry()
        {
            var player = NewCreature("Player Stand-In");
            var held = HeldTrialCreature(new TrialRep(), 60.0);

            TryWake(held, player);

            Assert.IsFalse(held.IsAwake, "a held creature refuses the wake");
            Assert.AreEqual(Creature.State.Idle, held.MonsterState);
            Assert.IsNull(held.AttackTarget, "the caller's target is dropped, so nothing is acquired");
            Assert.IsTrue(held.IsTrialHeld(TrialManager.Now()), "the hold is still in force");
        }

        [TestMethod]
        public void A_held_creature_wakes_and_keeps_its_target_once_the_hold_expires()
        {
            var player = NewCreature("Player Stand-In");
            var held = HeldTrialCreature(new TrialRep(), -0.5);

            Assert.IsTrue(held.TrialHoldExpired(TrialManager.Now()), "the tick's release step sees the expiry");
            Assert.AreEqual(0.0, held.P_TrialHoldUntil, "and clears the hold");

            TryWake(held, player);

            Assert.IsTrue(held.IsAwake, "after expiry the ordinary wake goes through");
            Assert.AreEqual(Creature.State.Awake, held.MonsterState);
            Assert.AreSame(player, held.AttackTarget);
        }

        [TestMethod]
        public void The_release_step_is_false_while_held_and_for_a_creature_never_held()
        {
            var held = HeldTrialCreature(new TrialRep(), 60.0);
            Assert.IsFalse(held.TrialHoldExpired(TrialManager.Now()));
            Assert.AreNotEqual(0.0, held.P_TrialHoldUntil);

            var plain = NewCreature("Plain");
            Assert.IsFalse(plain.TrialHoldExpired(TrialManager.Now()), "no hold, nothing to release");
        }

        [TestMethod]
        public void An_attacked_held_creature_wakes_early_while_its_sibling_stays_held()
        {
            var rep = new TrialRep();
            var player = NewCreature("Player Stand-In");
            var struck = HeldTrialCreature(rep, 60.0);
            var sibling = HeldTrialCreature(rep, 60.0);

            // Player.OnAttackMonster / CombatPet.PetOnAttackMonster: break the hold, then the retaliation wake.
            struck.BreakTrialHold();
            TryWake(struck, player);

            // The struck creature's AlertFriendly reaches its sibling as AttackTarget + WakeUp(false).
            TryWake(sibling, player);

            Assert.IsTrue(struck.IsAwake, "the struck creature retaliates now");
            Assert.AreSame(player, struck.AttackTarget);
            Assert.IsFalse(sibling.IsAwake, "its sibling keeps waiting for the timer");
            Assert.IsNull(sibling.AttackTarget);
            Assert.AreEqual(1, rep.GraceEarlyWakes, "one early wake is counted on the rep");
        }

        [TestMethod]
        public void Breaking_an_already_expired_hold_is_not_an_early_wake()
        {
            var rep = new TrialRep();
            var c = HeldTrialCreature(rep, -1.0);

            c.BreakTrialHold();

            Assert.AreEqual(0.0, c.P_TrialHoldUntil);
            Assert.AreEqual(0, rep.GraceEarlyWakes);
        }

        [TestMethod]
        public void A_creature_with_no_trial_rep_is_never_held()
        {
            var player = NewCreature("Player Stand-In");

            // The field alone does not hold: only a trial creature (P_TrialRep set) can be held.
            var stray = NewCreature("Not A Trial Creature");
            stray.P_TrialHoldUntil = TrialManager.Now() + 60.0;

            Assert.IsFalse(stray.IsTrialHeld(TrialManager.Now()));
            TryWake(stray, player);
            Assert.IsTrue(stray.IsAwake, "a non-trial creature wakes exactly as before");
            Assert.AreSame(player, stray.AttackTarget);

            var plain = NewCreature("Ordinary Monster");
            Assert.AreEqual(0.0, plain.P_TrialHoldUntil, "no creature starts held");
            TryWake(plain, player);
            Assert.IsTrue(plain.IsAwake);
        }

        private static CombatPet NewPet()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.CombatPet,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Ranged Pet" } },
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
            };

            var pet = new CombatPet(weenie, new ObjectGuid(nextGuid++));
            pet.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            pet.CurrentLandblock = SpawnedLandblock;
            return pet;
        }

        /// <summary>
        /// A held creature that cannot be woken (Tolerance.NoAttack), so MonsterOnAttackMonster stops short of WakeUp (whose friend
        /// alert needs physics) and the test sees the hold break alone.
        /// </summary>
        private static Creature HeldUnwakeable(TrialRep rep)
        {
            var c = HeldTrialCreature(rep, 60.0);
            c.Tolerance = Tolerance.NoAttack;
            return c;
        }

        [TestMethod]
        public void A_pet_ranged_or_spell_hit_breaks_the_hold_through_MonsterOnAttackMonster()
        {
            // Creature_Missile, Monster_Magic and SpellProjectile route a pet ranged or spell hit here, not PetOnAttackMonster.
            var rep = new TrialRep();
            var struck = HeldUnwakeable(rep);
            var sibling = HeldUnwakeable(rep);

            NewPet().MonsterOnAttackMonster(struck);

            Assert.AreEqual(0.0, struck.P_TrialHoldUntil, "the pet hit ends the struck creature's hold");
            Assert.IsTrue(sibling.IsTrialHeld(TrialManager.Now()), "its sibling stays held");
            Assert.AreEqual(1, rep.GraceEarlyWakes);
        }

        [TestMethod]
        public void A_non_pet_creature_hit_leaves_the_hold_in_place()
        {
            var rep = new TrialRep();
            var held = HeldUnwakeable(rep);

            NewCreature("Faction Mob").MonsterOnAttackMonster(held);

            Assert.IsTrue(held.IsTrialHeld(TrialManager.Now()), "only the player side breaks a hold");
            Assert.AreEqual(0, rep.GraceEarlyWakes);
        }

        // ------------------------------------------------------------------ which waves are held

        [TestMethod]
        public void Only_wave_zero_gets_the_grace()
        {
            var rep = new TrialRep { };
            rep.StartGraceSecs = 6;
            Assert.AreEqual(6, rep.StartGraceSecsFor(0));
            Assert.AreEqual(0, rep.StartGraceSecsFor(1));
            Assert.AreEqual(0, rep.StartGraceSecsFor(2));

            rep.StartGraceSecs = 0;
            Assert.AreEqual(0, rep.StartGraceSecsFor(0), "startGraceSecs 0 is off");
        }

        private sealed class GraceWorld : ITrialWorld
        {
            public bool RoomLoaded => true;
            public bool PlayerOnline => true;
            public bool PlayerInRoom => true;
            public readonly List<int> GraceByWave = new List<int>();

            /// <summary>Above 0: wave 0 records a held wave with this GraceUntil, as LiveTrialWorld.SpawnWave does.</summary>
            public double GraceUntilForWaveZero;
            public readonly HashSet<uint> Dead = new HashSet<uint>();
            public List<uint> Last = new List<uint>();
            private uint next = 500;

            public int LiveForeignCreatures() => 0;
            public bool IsAlive(uint guid) => !Dead.Contains(guid);
            public void DestroyAll() { }
            public void ReturnPlayerToStart() { }

            public IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep)
            {
                GraceByWave.Add(rep.StartGraceSecsFor(rep.WaveIndex));

                if (rep.WaveIndex == 0 && GraceUntilForWaveZero > 0)
                {
                    rep.GraceHeld = 1;
                    rep.GraceUntil = GraceUntilForWaveZero;
                }

                Last = new List<uint> { next++ };
                return Last;
            }
        }

        private static TrialRep ThreeWaveRep() => new TrialRep
        {
            Waves = Enumerable.Range(0, 3).Select(_ => (IReadOnlyList<DungeonSpawnPlanEntry>)new List<DungeonSpawnPlanEntry>()).ToList(),
        };

        [TestMethod]
        public void The_session_hands_the_scenario_grace_to_wave_zero_of_every_rep_and_to_no_later_wave()
        {
            var world = new GraceWorld();
            var scenario = new TrialScenario("s", "r", null, 1, null, null, 2, 60, 0, StartGraceSecs: 9);
            var session = new TrialSession("u", scenario, "sha", null, "asha", null, 1u, world, _ => ThreeWaveRep());
            session.Tick(0);

            for (var rep = 0; rep < 2; rep++)
            {
                Assert.IsTrue(session.TryNext(rep * 10 + 1, out var refusal), refusal);

                for (var wave = 0; wave < 3; wave++)
                {
                    world.Dead.UnionWith(world.Last);
                    session.OnCreatureDied(0, rep * 10 + 2 + wave);
                }
            }

            CollectionAssert.AreEqual(new[] { 9, 0, 0, 9, 0, 0 }, world.GraceByWave);
        }

        private static (GraceWorld World, TrialSession Session, List<TrialRep> Reps) GraceSession(double graceUntil)
        {
            var world = new GraceWorld { GraceUntilForWaveZero = graceUntil };
            var reps = new List<TrialRep>();
            var scenario = new TrialScenario("s", "r", null, 1, null, null, 1, 600, 0, StartGraceSecs: 6);
            var session = new TrialSession("u", scenario, "sha", null, "asha", null, 1u, world, _ => { var r = ThreeWaveRep(); reps.Add(r); return r; });
            session.Tick(0);
            return (world, session, reps);
        }

        [TestMethod]
        public void A_rep_that_ends_inside_the_grace_still_logs_its_release_once()
        {
            var (world, session, reps) = GraceSession(graceUntil: 7.0);
            Assert.IsTrue(session.TryNext(1, out var refusal), refusal);

            // Every wave dies at t = 2..4, well inside the grace (GraceUntil 7): the rep clears and ends first.
            for (var wave = 0; wave < 3; wave++)
            {
                world.Dead.UnionWith(world.Last);
                session.OnCreatureDied(0, 2 + wave);
            }

            Assert.AreEqual(TrialPhase.Done, session.Phase);
            Assert.IsTrue(reps[0].GraceReleaseLogged, "EndRep logs the release the tick never reached");
        }

        [TestMethod]
        public void The_tick_logs_the_release_at_expiry_and_not_before()
        {
            var (_, session, reps) = GraceSession(graceUntil: 7.0);
            Assert.IsTrue(session.TryNext(1, out var refusal), refusal);

            session.Tick(6.9);
            Assert.IsFalse(reps[0].GraceReleaseLogged, "not before GraceUntil");

            session.Tick(7.0);
            Assert.IsTrue(reps[0].GraceReleaseLogged);
        }

        [TestMethod]
        public void A_rep_with_nothing_held_logs_no_release()
        {
            var (world, session, reps) = GraceSession(graceUntil: 0.0);
            Assert.IsTrue(session.TryNext(1, out var refusal), refusal);

            session.Tick(50);
            for (var wave = 0; wave < 3; wave++)
            {
                world.Dead.UnionWith(world.Last);
                session.OnCreatureDied(0, 51 + wave);
            }

            Assert.IsFalse(reps[0].GraceReleaseLogged, "no applied line, so no released line");
        }

        [TestMethod]
        public void The_release_line_carries_the_real_elapsed_time_and_the_early_end()
        {
            var rep = new TrialRep { RunKey = "rk" };
            rep.Number = 3;
            rep.StartGraceSecs = 6;
            rep.GraceHeld = 6;

            var early = TrialSession.GraceReleaseLine("u", rep, 2.46, "died");
            StringAssert.Contains(early, "released after 2.5s");
            StringAssert.Contains(early, "rep ended died before the 6s grace ran out");
            StringAssert.Contains(early, "6 held, 0 woken early");

            var onTime = TrialSession.GraceReleaseLine("u", rep, 6.04, null);
            StringAssert.Contains(onTime, "released after 6.0s:");
            Assert.IsFalse(onTime.Contains("rep ended"));
        }

        [TestMethod]
        public void A_scenario_built_without_the_field_gets_the_default_grace()
        {
            var scenario = new TrialScenario("s", "r", null, 1, null, null, 1, 60, 0);
            Assert.AreEqual(TrialScenarioLoader.DefaultStartGraceSecs, scenario.StartGraceSecs);
            Assert.AreEqual(6, TrialScenarioLoader.DefaultStartGraceSecs);
        }

        // ------------------------------------------------------------------ the live wiring, by source scan

        private const string Awareness = "Source/ACE.Server/WorldObjects/Monster_Awareness.cs";
        private const string Combat = "Source/ACE.Server/WorldObjects/Creature_Combat.cs";
        private const string Tick = "Source/ACE.Server/WorldObjects/Monster_Tick.cs";
        private const string PlayerMonster = "Source/ACE.Server/WorldObjects/Player_Monster.cs";
        private const string PetMonster = "Source/ACE.Server/WorldObjects/Pet_Monster.cs";
        private const string Spawner = "Source/ACE.Server/BalanceTrials/TrialSpawner.cs";
        private const string World = "Source/ACE.Server/BalanceTrials/LiveTrialWorld.cs";

        private static void AssertBefore(string body, string first, string then, string why)
        {
            var a = body.IndexOf(first, StringComparison.Ordinal);
            var b = body.IndexOf(then, StringComparison.Ordinal);
            Assert.IsTrue(a >= 0, $"missing '{first}': {why}");
            Assert.IsTrue(b >= 0, $"missing '{then}': {why}");
            Assert.IsTrue(a < b, $"'{first}' must come before '{then}': {why}");
        }

        [TestMethod]
        public void WakeUp_checks_the_hold_before_it_changes_any_state()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Awareness), "public void WakeUp(bool alertNearby = true)");
            AssertBefore(body, "if (TrialHoldRefusesWake())", "MonsterState = State.Awake;", "every wake path funnels through WakeUp");
        }

        [TestMethod]
        public void A_player_or_pet_attack_breaks_the_hold_before_the_retaliation_wake()
        {
            var player = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PlayerMonster), "public void OnAttackMonster(Creature monster, bool primaryTarget = true)");
            AssertBefore(player, "monster.BreakTrialHold();", "monster.WakeUp();", "a player attack wakes the struck creature at once");

            var pet = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PetMonster), "public void PetOnAttackMonster(Creature monster)");
            AssertBefore(pet, "monster.BreakTrialHold();", "monster.WakeUp();", "a pet attack wakes the struck creature at once");

            var monsterHit = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Combat), "public void MonsterOnAttackMonster(Creature monster)");
            AssertBefore(monsterHit, "monster.BreakTrialHold();", "monster.WakeUp();", "a pet ranged or spell hit wakes the struck creature at once");

            var taunt = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Awareness), "public bool ApplyTaunt(Creature taunter, double duration, bool extendOnly = false, bool onHit = false)");
            AssertBefore(taunt, "BreakTrialHold();", "WakeUp();", "a taunt that takes wakes the taunted creature");
        }

        [TestMethod]
        public void The_tick_reruns_the_spawn_scan_when_the_hold_expires_before_the_sleep_return()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Tick), "public void Monster_Tick(double currentUnixTime)");
            AssertBefore(body, "TrialHoldExpired(", "if (!IsAwake)", "a sleeping held creature must still reach the release step");
            AssertBefore(body, "TrialHoldExpired(", "CheckTargets_Inner();", "the release acquires as the spawn scan does");
        }

        [TestMethod]
        public void The_spawner_sets_the_hold_before_EnterWorld_and_only_the_world_passes_one()
        {
            var spawn = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), TrialSpawnerTests.SpawnSignature);
            AssertBefore(spawn, "creature.P_TrialHoldUntil = holdUntil;", "creature.EnterWorld()", "EnterWorld schedules the spawn scan, which must find the creature held");

            var wave = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(World), "public IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep)");
            StringAssert.Contains(wave, "rep.StartGraceSecsFor(rep.WaveIndex)", "the hold is wave-scoped through StartGraceSecsFor");
        }
    }
}
