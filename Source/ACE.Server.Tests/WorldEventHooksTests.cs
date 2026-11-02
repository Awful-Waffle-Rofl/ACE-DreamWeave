using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ShardBiota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WorldEventHooks (refactor slice R5): every member returns the identity value - false, the same list, or a
    /// no-op - for an object no run owns. Pure cores are exercised over plain values; adapters over a live,
    /// never-evented Creature (constructible here: TestGameTables seeds the vital formula tables its ctor reads).
    ///
    /// Not invoked here, because they have no WorldObject input and drive the live manager (PropertyManager
    /// reads, the axis-store load, the shard): Initialize, Tick and OnShutdown. Their forwarding is pinned by
    /// <see cref="Hook_ForwardsToItsImplementation"/> instead.
    /// </summary>
    [TestClass]
    public class WorldEventHooksTests
    {
        private static uint nextGuid = 0x50E50000;

        private static Creature MakeCreature()
        {
            var wcid = 0x7E100000u + (nextGuid & 0xFFFF);

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"weventhookstest{wcid}",
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Attackable, true } },
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "World Event Hooks Test Creature" } }
            };

            TestGameTables.EnsureInitialized();

            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));
            Assert.IsNull(creature.P_WorldEvent, "sanity: a fresh creature belongs to no run");
            return creature;
        }

        /// <summary>A bare-reflection Player: Guid, Biota with a Name, and the biota lock - no Session needed.</summary>
        private static Player MakePlayer(string name)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetField(player, "<Guid>k__BackingField", new ObjectGuid(nextGuid++));
            SetField(player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota
            {
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } }
            });
            SetField(player, "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return player;
        }

        private static void SetField(object target, string name, object value) => SetField(target, typeof(WorldObject), name, value);

        private static void SetField(object target, Type declaring, string name, object value)
        {
            var field = declaring.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(field, $"{declaring.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }

        private static readonly System.Reflection.PropertyInfo CurrentProperty = typeof(WorldEventManager).GetProperty(nameof(WorldEventManager.Current));

        /// <summary>
        /// Runs <paramref name="body"/> with <see cref="WorldEventManager.Current"/> set to <paramref name="run"/>
        /// (null = no run), and restores whatever was there afterwards, so these tests never depend on, or leak, the
        /// process-wide run state another test class may have left behind.
        /// </summary>
        private static void WithCurrentRun(WorldEvent run, Action body)
        {
            var saved = WorldEventManager.Current;

            try
            {
                CurrentProperty.SetValue(null, run);
                Assert.AreSame(run, WorldEventManager.Current, "sanity: the run seam took");
                body();
            }
            finally
            {
                CurrentProperty.SetValue(null, saved);
            }
        }

        /// <summary>A lightweight run in Idle with a fixed clock; enough for the death report and the pooled-loot latch.</summary>
        private static WorldEvent NewRun(uint runId, bool latchPooledLoot = false)
        {
            var run = new WorldEvent(runId, null, null, null, clock: () => 1234d);

            if (latchPooledLoot)
                run.LatchPooledLoot(true);

            return run;
        }

        /// <summary>A creature that belongs to <paramref name="run"/>: back-reference plus the persisted run stamp.</summary>
        private static Creature MakeEventCreature(WorldEvent run)
        {
            var creature = MakeCreature();
            creature.P_WorldEvent = run;
            creature.SetProperty(PropertyInt.WorldEventId, (int)run.RunId);
            return creature;
        }

        // ---- lifecycle --------------------------------------------------------------------------------------

        [TestMethod]
        public void Store_NonEventCreature_Unchanged()
        {
            Assert.AreSame(WorldEventManager.Store, WorldEventHooks.Store);
            Assert.IsNotNull(WorldEventHooks.Store, "the store starts as WorldEventAxisStore.Empty, never null");
        }

        // ---- landblock load and persistence -----------------------------------------------------------------

        [TestMethod]
        public void FilterOrphanedDynamics_NonEventCreature_Unchanged()
        {
            var plain = new ShardBiota { Id = 0x80000001, WeenieClassId = 1, WeenieType = 10 };
            plain.BiotaPropertiesInt.Add(new ACE.Database.Models.Shard.BiotaPropertiesInt { ObjectId = 0x80000001, Type = (ushort)PropertyInt.Level, Value = 20 });

            var dynamics = new List<ShardBiota> { plain, new ShardBiota { Id = 0x80000002, WeenieClassId = 1, WeenieType = 10 } };

            var result = WorldEventHooks.FilterOrphanedDynamics(0x016C, 0, dynamics);

            Assert.AreSame(dynamics, result, "no orphan: the very same list comes back, no copy");
            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void IsTransientEventObject_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsTransientEventObject((int?)null));
            Assert.IsTrue(WorldEventHooks.IsTransientEventObject((int?)7), "positive control: a run stamp is transient");
            Assert.IsTrue(WorldEventHooks.IsTransientEventObject((int?)0), "any stamp, even 0, is transient (the original test was != null)");

            Assert.IsFalse(WorldEventHooks.IsTransientEventObject(MakeCreature()));

            // Adapter positive control: the adapter really reads the stamp.
            var stamped = MakeCreature();
            stamped.SetProperty(PropertyInt.WorldEventId, 7);
            Assert.IsTrue(WorldEventHooks.IsTransientEventObject(stamped));
        }

        [TestMethod]
        public void OnGeneratedIntoWorld_NonEventCreature_Unchanged()
        {
            // No pure core: the decision needs the live run (Current). With no run it is a no-op.
            WithCurrentRun(null, () =>
            {
                var child = MakeCreature();
                var generator = MakeCreature();

                WorldEventHooks.OnGeneratedIntoWorld(child, generator);

                Assert.IsNull(child.P_WorldEvent, "a generated child is not adopted when no run exists");
            });
        }

        // ---- combat credit and boss damage ------------------------------------------------------------------

        [TestMethod]
        public void OnDamageRecorded_NonEventCreature_Unchanged()
        {
            // No pure core: the credit target is resolved from live objects. A hit between objects no run owns
            // is a no-op - nothing thrown, no back-reference appears. The run-creature direction is covered end
            // to end by WorldEventCombatCreditTests, whose DamageHistory.Add tests now reach the credit hook
            // through this member.
            var victim = MakeCreature();
            var attacker = MakeCreature();

            WorldEventHooks.OnDamageRecorded(victim, attacker, 25);
            WorldEventHooks.OnDamageRecorded(victim, MakePlayer("Hitter"), 25);
            WorldEventHooks.OnDamageRecorded(MakePlayer("Victim"), attacker, 25);

            Assert.IsNull(victim.P_WorldEvent);
            Assert.IsNull(attacker.P_WorldEvent);
        }

        [TestMethod]
        public void OnCombatContact_NonEventCreature_Unchanged()
        {
            var victim = MakeCreature();
            var attacker = MakeCreature();

            WorldEventHooks.OnCombatContact(victim, attacker);
            WorldEventHooks.OnCombatContact(victim, MakePlayer("Hitter"));
            WorldEventHooks.OnCombatContact(MakePlayer("Victim"), attacker);
            WorldEventHooks.OnCombatContact(null, attacker);
            WorldEventHooks.OnCombatContact(victim, null);

            Assert.IsNull(victim.P_WorldEvent);
            Assert.IsNull(attacker.P_WorldEvent);
        }

        [TestMethod]
        public void VictimStateRefusesCredit_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.VictimStateRefusesCredit(false, false), "an ordinary victim is never refused");
            Assert.IsTrue(WorldEventHooks.VictimStateRefusesCredit(true, false), "positive control: Invincible");
            Assert.IsTrue(WorldEventHooks.VictimStateRefusesCredit(false, true), "positive control: lifestone protection");
        }

        [TestMethod]
        public void NoteBossHit_NonEventCreature_Unchanged()
        {
            // No pure core: the boss controller lives on the run. A non-event attacker returns after its field
            // reads (P_WorldEvent, then P_DigsiteEncounter), so the defender is never read.
            var attacker = MakeCreature();

            WorldEventHooks.NoteBossHit(attacker, MakePlayer("Defender"), 120, false);
            WorldEventHooks.NoteBossHit(attacker, null, 120, true);
            WorldEventHooks.NoteBossHit(null, MakePlayer("Defender"), 120, false);

            Assert.IsNull(attacker.P_WorldEvent);
        }

        // ---- creature death and pooled loot -----------------------------------------------------------------

        [TestMethod]
        public void OnCreatureDied_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.ReportsDeathToRun(false, null));
            Assert.IsFalse(WorldEventHooks.ReportsDeathToRun(false, 7), "a stamp without the back-reference is inert");
            Assert.IsFalse(WorldEventHooks.ReportsDeathToRun(true, null), "a back-reference without the stamp is inert");
            Assert.IsTrue(WorldEventHooks.ReportsDeathToRun(true, 7), "positive control: both keys report");

            // Adapter, driven through the live manager. WorldEvent.OnCreatureDied's first statement stamps
            // LastCreditAt from the run's clock (1234), so a reported death is observable even though the rest of
            // the bookkeeping throws on this composition-less run (the manager catches and logs that).
            var run = NewRun(41);

            WithCurrentRun(run, () =>
            {
                WorldEventHooks.OnCreatureDied(MakeCreature(), null, null);
                Assert.AreEqual(0d, run.LastCreditAt, "a creature no run owns reports nothing");

                var refOnly = MakeCreature();
                refOnly.P_WorldEvent = run;
                WorldEventHooks.OnCreatureDied(refOnly, null, null);
                Assert.AreEqual(0d, run.LastCreditAt, "a back-reference without the stamp reports nothing");

                WorldEventHooks.OnCreatureDied(MakeEventCreature(run), null, null);
                Assert.AreEqual(1234d, run.LastCreditAt, "positive control: both keys report the death to the run");
            });
        }

        [TestMethod]
        public void IsPooledLootDeath_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsPooledLootDeath(false, null, false, false));
            Assert.IsTrue(WorldEventHooks.IsPooledLootDeath(true, 7, true, false), "positive control: a latched run's non-boss pools");

            Assert.IsFalse(WorldEventHooks.IsPooledLootDeath(MakeCreature()));

            // Adapter positive and latch-off controls.
            Assert.IsTrue(WorldEventHooks.IsPooledLootDeath(MakeEventCreature(NewRun(42, latchPooledLoot: true))), "a latched run's creature pools");
            Assert.IsFalse(WorldEventHooks.IsPooledLootDeath(MakeEventCreature(NewRun(43))), "an unlatched run's creature keeps its corpse");
        }

        [TestMethod]
        public void BankPooledKill_NonEventCreature_Unchanged()
        {
            // No pure core: the bank is the run's pool. A creature with no run has no pool, so nothing is banked.
            Assert.IsFalse(WorldEventHooks.BankPooledKill(MakeCreature(), null));
        }

        // ---- player death -----------------------------------------------------------------------------------

        [TestMethod]
        public void ProtectsDeathAt_NonEventCreature_Unchanged()
        {
            var at = new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0);

            Assert.IsFalse(WorldEventHooks.ProtectsDeathAt(true, true, null, at, at, 60f), "no run, no protection");
            Assert.IsTrue(WorldEventHooks.ProtectsDeathAt(true, true, WorldEventState.Active, at, at, 60f), "positive control: inside an Active run");

            WithCurrentRun(null, () =>
                Assert.IsFalse(WorldEventHooks.ProtectsDeathAt(at), "the live adapter with no run protects nothing"));
        }

        [TestMethod]
        public void IsProtectedDeathInProgress_NonEventCreature_Unchanged()
        {
            // No pure core: the member IS the player's latched-state read. The identity is the latch's default.
            var player = MakePlayer("Dying");
            Assert.IsFalse(WorldEventHooks.IsProtectedDeathInProgress(player));

            // FieldInfo.SetValue on a Player field forces Player's static constructor, which reads the world
            // database. A compiled field assignment does not run the type initializer, so set the latch that way.
            var field = typeof(Player).GetField("worldEventDeathInProgress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(field, "Player.worldEventDeathInProgress was not found by reflection - has it been renamed?");
            var target = System.Linq.Expressions.Expression.Parameter(typeof(Player));
            var latch = System.Linq.Expressions.Expression.Lambda<Action<Player>>(
                System.Linq.Expressions.Expression.Assign(System.Linq.Expressions.Expression.Field(target, field), System.Linq.Expressions.Expression.Constant(true)),
                target).Compile();
            latch(player);

            Assert.IsTrue(WorldEventHooks.IsProtectedDeathInProgress(player), "positive control: the latch is what is read");
        }

        [TestMethod]
        public void OnPlayerDied_NonEventCreature_Unchanged()
        {
            // No pure core: the grace lives on the run. With no run it is one field read and a return.
            WithCurrentRun(null, () =>
            {
                WorldEventHooks.OnPlayerDied(MakePlayer("Dead"));
                WorldEventHooks.OnPlayerDied(null);
            });
        }

        // ---- use handlers -----------------------------------------------------------------------------------

        [TestMethod]
        public void TryUseRewardCrate_NonEventCreature_Unchanged()
        {
            // The marker test is the handler's first line; an unmarked object is not handled.
            Assert.IsFalse(WorldEventHooks.TryUseRewardCrate(MakeCreature(), MakePlayer("User")));
        }

        [TestMethod]
        public void TryUseCache_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsEventCache(null));
            Assert.IsFalse(WorldEventHooks.IsEventCache(false));
            Assert.IsTrue(WorldEventHooks.IsEventCache(true), "positive control: the opt-in flag");

            Assert.IsFalse(WorldEventHooks.TryUseCache(MakeCreature(), MakePlayer("User")), "an object without the opt-in is never handled");
        }

        // ---- objective creatures and the monster tick -------------------------------------------------------

        [TestMethod]
        public void IsObjectiveCreature_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsObjectiveCreature((bool?)null));
            Assert.IsFalse(WorldEventHooks.IsObjectiveCreature((bool?)false));
            Assert.IsTrue(WorldEventHooks.IsObjectiveCreature((bool?)true), "positive control: the flag");

            var creature = MakeCreature();
            Assert.IsFalse(WorldEventHooks.IsObjectiveCreature(creature));
            Assert.IsFalse(creature.WorldEventObjective, "the cached flag SetEphemeralValues stored through the hook");

            // Adapter positive control: the adapter really reads PropertyBool.WorldEventObjective.
            creature.SetProperty(PropertyBool.WorldEventObjective, true);
            Assert.IsTrue(WorldEventHooks.IsObjectiveCreature(creature));
        }

        [TestMethod]
        public void SkipsMonsterTick_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.SkipsMonsterTick(false));
            Assert.IsTrue(WorldEventHooks.SkipsMonsterTick(true), "positive control: an objective creature never thinks");

            var creature = MakeCreature();
            Assert.IsFalse(WorldEventHooks.SkipsMonsterTick(creature));

            creature.WorldEventObjective = true;
            Assert.IsTrue(WorldEventHooks.SkipsMonsterTick(creature), "adapter positive control: the cached flag is what is read");
        }

        // ---- sky drop ---------------------------------------------------------------------------------------

        [TestMethod]
        public void IsSkyDropping_NonEventCreature_Unchanged()
        {
            // No pure core: the member IS a field read. The identity is the field's default.
            var creature = MakeCreature();
            Assert.IsFalse(WorldEventHooks.IsSkyDropping(creature));

            creature.WorldEventSkyDrop = true;
            Assert.IsTrue(WorldEventHooks.IsSkyDropping(creature), "positive control: the flag is what is read");
        }

        [TestMethod]
        public void CompleteSkyDropAcquire_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.CompletesSkyDropAcquire(false));
            Assert.IsTrue(WorldEventHooks.CompletesSkyDropAcquire(true), "positive control: a landing raised the flag");

            var creature = MakeCreature();
            WorldEventHooks.CompleteSkyDropAcquire(creature);
            Assert.IsFalse(creature.WorldEventSkyDropPendingAcquire);
            Assert.IsNull(creature.AttackTarget, "no acquisition ran");
        }

        [TestMethod]
        public void SkyDropSettled_NonEventCreature_Unchanged()
        {
            // The physics tick reaches this member only for a creature IsSkyDropping already said is falling, so
            // there is no "non-event" short-circuit to preserve. Unchanged here means: it is exactly the pure
            // rule WorldObject.SkyDropSettled over the creature's own deadline field.
            var creature = MakeCreature();
            creature.WorldEventSkyDropDeadline = 112d;

            Assert.IsTrue(WorldEventHooks.SkyDropSettled(true, 100d, creature), "on walkable ground");
            Assert.IsFalse(WorldEventHooks.SkyDropSettled(false, 100d, creature), "still falling before the deadline");
            Assert.IsTrue(WorldEventHooks.SkyDropSettled(false, 112d, creature), "the deadline itself settles");

            foreach (var (onWalkable, now) in new[] { (true, 0d), (false, 111.9d), (false, 112d), (false, 500d) })
                Assert.AreEqual(WorldObject.SkyDropSettled(onWalkable, now, 112d), WorldEventHooks.SkyDropSettled(onWalkable, now, creature));
        }

        // ---- event NPCs -------------------------------------------------------------------------------------

        [TestMethod]
        public void IsStyledIdleNpc_NonEventCreature_Unchanged()
        {
            // No pure core: the member IS a field read. The identity is the field's default.
            var creature = MakeCreature();
            Assert.IsFalse(WorldEventHooks.IsStyledIdleNpc(creature));

            SetField(creature, typeof(Creature), "<WorldEventStyledIdle>k__BackingField", true);
            Assert.IsTrue(WorldEventHooks.IsStyledIdleNpc(creature), "positive control: the mark is what is read");
        }

        // ---- admin service ----------------------------------------------------------------------------------

        [TestMethod]
        public void CreateAdminService_NonEventCreature_Unchanged()
        {
            // No WorldObject input and no decision: the identity is "the same service CreateLive builds". A fresh
            // instance per call, nothing cached, nothing enqueued at construction.
            var enqueued = 0;
            var a = WorldEventHooks.CreateAdminService(_ => enqueued++, (_, _) => { }, 5000);
            var b = WorldEventHooks.CreateAdminService(_ => enqueued++, (_, _) => { }, 5000);

            Assert.IsNotNull(a);
            Assert.AreNotSame(a, b);
            Assert.AreEqual(0, enqueued);
        }

        // ---- Hold: reach and damage (H4a) -------------------------------------------------------------------

        private static FakeHoldRunView HoldRun(uint id) => new FakeHoldRunView(id);

        [TestMethod]
        public void HoldPhysicsRunIds_NonEventCreature_Unchanged()
        {
            Assert.AreEqual((0u, 0u), WorldEventHooks.HoldPhysicsRunIds((HoldUnitKind?)null, 9));
            Assert.AreEqual((0u, 0u), WorldEventHooks.HoldPhysicsRunIds(MakeCreature()));

            var run = HoldRun(9);
            Assert.AreEqual((9u, 0u), WorldEventHooks.HoldPhysicsRunIds(HoldFixtures.Tagged(run, HoldUnitKind.Knight)), "positive control: a knight");
            Assert.AreEqual((0u, 9u), WorldEventHooks.HoldPhysicsRunIds(HoldFixtures.Tagged(run, HoldUnitKind.Monster)), "positive control: a wave monster");
        }

        [TestMethod]
        public void PhysicsIsHoldKnightTargetFor_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.PhysicsIsHoldKnightTargetFor(0, true, 0), "an object that is not a knight adds nothing");
            Assert.IsFalse(WorldEventHooks.PhysicsIsHoldKnightTargetFor(0, false, 0));
            Assert.IsTrue(WorldEventHooks.PhysicsIsHoldKnightTargetFor(9, true, 9), "positive control: its own run's monster");
        }

        [TestMethod]
        public void PhysicsIsHoldKnight_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.PhysicsIsHoldKnight(0));
            Assert.IsTrue(WorldEventHooks.PhysicsIsHoldKnight(9), "positive control");
        }

        [TestMethod]
        public void TryHoldCanDamage_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.HoldMayDamageKnight(0, false, null, 0, true));
            Assert.IsTrue(WorldEventHooks.HoldMayDamageKnight(9, false, HoldUnitKind.Monster, 9, true), "positive control");

            Assert.IsFalse(WorldEventHooks.TryHoldCanDamage(MakeCreature(), MakeCreature(), out var can), "not a knight: decide as before");
            Assert.IsFalse(can);

            var run = HoldRun(9);
            var knight = HoldFixtures.Tagged(run, HoldUnitKind.Knight);
            Assert.IsTrue(WorldEventHooks.TryHoldCanDamage(HoldFixtures.Tagged(run, HoldUnitKind.Monster), knight, out can), "positive control: a knight is decided here");
            Assert.IsTrue(can);
            Assert.IsTrue(WorldEventHooks.TryHoldCanDamage(MakeCreature(), knight, out can));
            Assert.IsFalse(can, "an outside monster may not");

            // A wave monster that is not a knight is decided as before, even for a run monster attacking it.
            Assert.IsFalse(WorldEventHooks.TryHoldCanDamage(HoldFixtures.Tagged(run, HoldUnitKind.Monster), HoldFixtures.Tagged(run, HoldUnitKind.Monster), out _));
        }

        [TestMethod]
        public void IsHoldKnight_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsHoldKnight((HoldUnitKind?)null));
            Assert.IsFalse(WorldEventHooks.IsHoldKnight(MakeCreature()));
            Assert.IsFalse(WorldEventHooks.IsHoldKnight((Creature)null));
            Assert.IsFalse(WorldEventHooks.IsHoldKnight(HoldFixtures.Tagged(HoldRun(9), HoldUnitKind.Monster)));
            Assert.IsTrue(WorldEventHooks.IsHoldKnight(HoldFixtures.Tagged(HoldRun(9), HoldUnitKind.Knight)), "positive control");
        }

        [TestMethod]
        public void RefusesPlayerHarm_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.RefusesPlayerHarm(MakeCreature()));
            Assert.IsFalse(WorldEventHooks.RefusesPlayerHarm(null));
            Assert.IsTrue(WorldEventHooks.RefusesPlayerHarm(HoldFixtures.Tagged(HoldRun(9), HoldUnitKind.Knight)), "positive control");
        }

        [TestMethod]
        public void IsHoldKnightTargetFor_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsHoldKnightTargetFor(MakeCreature(), MakeCreature()));
            Assert.IsFalse(WorldEventHooks.IsHoldKnightTargetFor(null, MakeCreature()));
            Assert.IsFalse(WorldEventHooks.IsHoldKnightTargetFor(MakeCreature(), null));

            var run = HoldRun(9);
            Assert.IsTrue(WorldEventHooks.IsHoldKnightTargetFor(HoldFixtures.Tagged(run, HoldUnitKind.Monster), HoldFixtures.Tagged(run, HoldUnitKind.Knight)), "positive control");
        }

        [TestMethod]
        public void HoldKnightNeverResists_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.HoldKnightNeverResists(MakeCreature(), MakeCreature()));
            Assert.IsFalse(WorldEventHooks.HoldKnightNeverResists(null, MakeCreature()));

            var run = HoldRun(9);
            Assert.IsTrue(WorldEventHooks.HoldKnightNeverResists(HoldFixtures.Tagged(run, HoldUnitKind.Monster), HoldFixtures.Tagged(run, HoldUnitKind.Knight)), "positive control");
        }

        [TestMethod]
        public void HoldPlayerDamageMult_NonEventCreature_Unchanged()
        {
            Assert.AreEqual(1.0, WorldEventHooks.HoldPlayerDamageMult(MakeCreature()));
            Assert.AreEqual(1.0, WorldEventHooks.HoldPlayerDamageMult(null));

            var run = new FakeHoldRunView(9) { Tuning = HoldFixtures.Tuning(new Dictionary<string, string> { [HoldKeys.FinalePlayerDamageMult] = "2" }) };
            Assert.AreEqual(2.0, WorldEventHooks.HoldPlayerDamageMult(HoldFixtures.Tagged(run, HoldUnitKind.FinaleBoss)), "positive control: the finale boss's multiplier");
        }

        [TestMethod]
        public void HoldWardDamage_NonEventCreature_Unchanged()
        {
            Assert.AreEqual(77.5f, WorldEventHooks.HoldWardDamage(MakeCreature(), WorldEventCombatKinds.Missile, 77.5f));
            Assert.AreEqual(77.5f, WorldEventHooks.HoldWardDamage(null, WorldEventCombatKinds.Missile, 77.5f));
            Assert.AreEqual(77.5f, WorldEventHooks.HoldWardDamage(HoldFixtures.Tagged(HoldRun(9), HoldUnitKind.Monster), WorldEventCombatKinds.Missile, 77.5f), "tagged but not warded");

            var goal = new GoalDef { Protect = new ProtectDef { Ward = new WardDef() } };
            var run = new FakeHoldRunView(9) { Tuning = HoldFixtures.Tuning(null, goal) };
            Assert.AreEqual(25f, WorldEventHooks.HoldWardDamage(HoldFixtures.Tagged(run, HoldUnitKind.Monster, warded: true), WorldEventCombatKinds.Missile, 100f), "positive control");
        }

        [TestMethod]
        public void ScaleHoldKnightHit_NonEventCreature_Unchanged()
        {
            Assert.AreEqual(41.25f, WorldEventHooks.ScaleHoldKnightHit(MakeCreature(), MakeCreature(), 41.25f));
            Assert.AreEqual(41.25f, WorldEventHooks.ScaleHoldKnightHit(MakeCreature(), null, 41.25f));

            var run = new FakeHoldRunView(9) { Tuning = HoldFixtures.Tuning(new Dictionary<string, string> { [HoldKeys.KnightDamageTakenMult] = "2" }) };
            Assert.AreEqual(41.25f, WorldEventHooks.ScaleHoldKnightHit(HoldFixtures.Tagged(run, HoldUnitKind.Monster), HoldFixtures.Tagged(run, HoldUnitKind.Monster), 41.25f), "a wave monster is not a knight");
            Assert.AreEqual(82.5f, WorldEventHooks.ScaleHoldKnightHit(HoldFixtures.Tagged(run, HoldUnitKind.Knight), HoldFixtures.Tagged(run, HoldUnitKind.Monster), 41.25f), "positive control");
        }

        [TestMethod]
        public void TrySelectHoldTarget_NonEventCreature_Unchanged()
        {
            var creature = MakeCreature();
            Assert.IsFalse(WorldEventHooks.TrySelectHoldTarget(creature));
            Assert.IsNull(creature.AttackTarget, "no target was chosen");
        }

        [TestMethod]
        public void KeepsHoldSeekerLock_NonEventCreature_Unchanged()
        {
            var creature = MakeCreature();
            Assert.IsFalse(WorldEventHooks.KeepsHoldSeekerLock(creature, MakeCreature()));
            Assert.IsNull(creature.AttackTarget);
            Assert.IsFalse(WorldEventHooks.KeepsHoldSeekerLock(HoldFixtures.Tagged(HoldRun(9), HoldUnitKind.Monster), MakeCreature()), "a run monster that is not a seeker");
        }

        [TestMethod]
        public void NoteHoldBossDamage_NonEventCreature_Unchanged()
        {
            var creature = MakeCreature();
            WorldEventHooks.NoteHoldBossDamage(creature, MakeCreature(), 500);
            WorldEventHooks.NoteHoldBossDamage(null, MakeCreature(), 500);
            Assert.AreEqual(0, creature.HoldBossDamageSnapshot(ACE.Common.Time.GetUnixTime()).Count);

            var monster = HoldFixtures.Tagged(HoldRun(9), HoldUnitKind.Monster);
            monster.NoteHoldBossDamage(0x50000001u, 500, 1000);
            Assert.AreEqual(0, monster.HoldBossDamageSnapshot(1000).Count, "a wave monster keeps no damage log");

            var boss = HoldFixtures.Tagged(HoldRun(9), HoldUnitKind.FinaleBoss);
            WorldEventHooks.NoteHoldBossDamage(boss, MakeCreature(), 500);
            Assert.AreEqual(0, boss.HoldBossDamageSnapshot(ACE.Common.Time.GetUnixTime()).Count, "a creature that is not a player or pet is not credited");
            boss.NoteHoldBossDamage(0x50000001u, 500, 1000);
            Assert.AreEqual(1, boss.HoldBossDamageSnapshot(1000).Count, "positive control: a player's damage on the finale boss");
        }

        // ---- Hold: heals, pacing and cosmetics (H4b) ----------------------------------------------------------

        private static FakeHoldRunView HealRun(uint id, bool playerHeal = true)
            => new FakeHoldRunView(id) { Tuning = WorldEventHoldTuning.Defaults with { PlayerHealEnabled = playerHeal, PlayerHealCap = 1.0 } };

        [TestMethod]
        public void TryQueuePlayerHeal_NonEventCreature_Unchanged()
        {
            var queued = new List<Action>();
            var saved = WorldEventHoldHeals.Enqueue;

            try
            {
                WorldEventHoldHeals.Enqueue = (k, a) => { queued.Add(a); return true; };

                var creature = MakeCreature();
                creature.Health.Current = creature.Health.MaxValue / 2;
                var before = creature.Health.Current;

                Assert.IsFalse(WorldEventHooks.TryQueuePlayerHeal(null, creature, 100, false, null, ChatMessageType.Magic), "not a knight: the caller heals as before");
                Assert.IsFalse(WorldEventHooks.TryQueuePlayerHeal(null, null, 100, false, null, ChatMessageType.Magic));
                Assert.IsFalse(WorldEventHooks.TryQueuePlayerHeal(null, HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster), 100, false, null, ChatMessageType.Magic), "a wave monster");
                Assert.AreEqual(before, creature.Health.Current);
                Assert.AreEqual(0, queued.Count, "nothing was queued");

                var knight = HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Knight);
                knight.Health.Current = knight.Health.MaxValue / 2;
                Assert.IsTrue(WorldEventHooks.TryQueuePlayerHeal(null, knight, 100, false, null, ChatMessageType.Magic), "positive control: a knight is handled here");
                Assert.AreEqual(1, queued.Count, "positive control: queued, not written");
            }
            finally
            {
                WorldEventHoldHeals.Enqueue = saved;
            }
        }

        [TestMethod]
        public void IsInvalidHoldSpellTarget_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsInvalidHoldSpellTarget(MakeCreature(), null, out var invalid), "not a knight: decide as before");
            Assert.IsFalse(invalid);
            Assert.IsFalse(WorldEventHooks.IsInvalidHoldSpellTarget(null, null, out invalid));
            Assert.IsFalse(WorldEventHooks.IsInvalidHoldSpellTarget(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster), null, out invalid));

            Assert.IsTrue(WorldEventHooks.IsInvalidHoldSpellTarget(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Knight), null, out invalid), "positive control: a knight is decided here");
            Assert.IsTrue(invalid, "no spell is not a heal");

            Assert.IsFalse(WorldEventHooks.IsInvalidHoldKnightSpell(true, true), "a health heal while player_heal is on is the one valid spell");
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldKnightSpell(false, true));
            Assert.IsTrue(WorldEventHooks.IsInvalidHoldKnightSpell(true, false));
        }

        [TestMethod]
        public void HoldAcceptsPlayerHeal_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.HoldAcceptsPlayerHeal(MakeCreature()));
            Assert.IsFalse(WorldEventHooks.HoldAcceptsPlayerHeal(null));
            Assert.IsFalse(WorldEventHooks.HoldAcceptsPlayerHeal(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster)));
            Assert.IsFalse(WorldEventHooks.HoldAcceptsPlayerHeal(HoldFixtures.Tagged(HealRun(9, playerHeal: false), HoldUnitKind.Knight)), "player_heal off");
            Assert.IsTrue(WorldEventHooks.HoldAcceptsPlayerHeal(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Knight)), "positive control");
        }

        [TestMethod]
        public void IsHoldKnightHealTarget_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsHoldKnightHealTarget(MakeCreature()));
            Assert.IsFalse(WorldEventHooks.IsHoldKnightHealTarget(null));
            Assert.IsFalse(WorldEventHooks.IsHoldKnightHealTarget(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster)));
            Assert.IsTrue(WorldEventHooks.IsHoldKnightHealTarget(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Knight)), "positive control");
        }

        [TestMethod]
        public void TryUseHoldShrine_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.TryUseHoldShrine(MakeCreature(), null), "an object no run stamped falls through");
            Assert.IsFalse(WorldEventHooks.MayBeHoldShrine(null));
            Assert.IsFalse(WorldEventHooks.MayBeHoldShrine(0));
            Assert.IsTrue(WorldEventHooks.MayBeHoldShrine(5), "positive control: a stamp");
        }

        [TestMethod]
        public void HoldRunRate_NonEventCreature_Unchanged()
        {
            Assert.AreEqual(1.37f, WorldEventHooks.HoldRunRate(MakeCreature(), 1.37f));
            Assert.AreEqual(1.37f, WorldEventHooks.HoldRunRate(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster), 1.37f), "a tag at 1x");
            Assert.AreEqual(2.0f, WorldEventHooks.HoldRunRate(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster, runRateMult: 1.6f), 1.25f), 0.0001f, "positive control: a runner");
        }

        [TestMethod]
        public void HoldMeleeDelay_NonEventCreature_Unchanged()
        {
            Assert.AreEqual(0.73, WorldEventHooks.HoldMeleeDelay(MakeCreature(), 0.73));
            Assert.AreEqual(0.4, WorldEventHooks.HoldMeleeDelay(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster, attackSpeedMult: 2f), 0.8), 1e-9, "positive control: a runner waits less");
        }

        [TestMethod]
        public void HoldAnimSpeed_NonEventCreature_Unchanged()
        {
            Assert.AreEqual(1.2f, WorldEventHooks.HoldAnimSpeed(MakeCreature(), 1.2f));
            Assert.AreEqual(2.4f, WorldEventHooks.HoldAnimSpeed(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster, attackSpeedMult: 2f), 1.2f), 0.0001f, "positive control: a runner swings faster");
        }

        [TestMethod]
        public void TickHoldRangedKeepDistance_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.TickHoldRangedKeepDistance(MakeCreature(), 20f));
            Assert.IsFalse(WorldEventHooks.TickHoldRangedKeepDistance(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster), 20f), "a run monster with no hold distance keeps the stock AI");
        }

        [TestMethod]
        public void HoldClientAttackable_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.HoldClientAttackable(HoldFixtures.MakeCreature(attackable: false), false));
            Assert.IsTrue(WorldEventHooks.HoldClientAttackable(MakeCreature(), true));
            Assert.IsFalse(WorldEventHooks.HoldClientAttackable((WorldObject)null, false));
            Assert.IsFalse(WorldEventHooks.HoldClientAttackable(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Knight), false), "a knight with no health bar");
            Assert.IsTrue(WorldEventHooks.HoldClientAttackable(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Knight, showHealthBar: true), false), "positive control");
        }

        [TestMethod]
        public void ApplyHoldTint_NonEventCreature_Unchanged()
        {
            var objDesc = new ACE.Entity.ObjDesc();
            WorldEventHooks.ApplyHoldTint(MakeCreature(), objDesc);
            WorldEventHooks.ApplyHoldTint(null, objDesc);
            WorldEventHooks.ApplyHoldTint(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster), objDesc);
            Assert.AreEqual(0, objDesc.SubPalettes.Count);
            Assert.AreEqual(0u, objDesc.PaletteID);

            WorldEventHooks.ApplyHoldTint(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster, corrupted: true, tint: 0x04001234), objDesc);
            Assert.AreEqual(1, objDesc.SubPalettes.Count, "positive control");
            Assert.AreEqual(0x04001234u, objDesc.SubPalettes[0].SubPaletteId);
        }

        [TestMethod]
        public void SendHoldKnightHealthLine_NonEventCreature_Unchanged()
        {
            Assert.IsFalse(WorldEventHooks.IsHoldKnightHealthLineTarget(MakeCreature()));
            Assert.IsFalse(WorldEventHooks.IsHoldKnightHealthLineTarget(null));
            Assert.IsFalse(WorldEventHooks.IsHoldKnightHealthLineTarget(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Monster)), "a wave monster");
            Assert.IsTrue(WorldEventHooks.IsHoldKnightHealthLineTarget(HoldFixtures.Tagged(HealRun(9), HoldUnitKind.Knight)), "positive control: a knight");

            // No Player can be built in this harness: an untagged target returns before the (null) player is touched.
            WorldEventHooks.SendHoldKnightHealthLine(null, MakeCreature());
            WorldEventHooks.SendHoldKnightHealthLine(null, null);
        }

        // ---- forwarding ---------------------------------------------------------------------------------------

        /// <summary>
        /// Each forwarding member, bound to its own declaration with comments stripped, must call the
        /// implementation it fronts with its arguments in order. Covers the members no test above can drive to
        /// a positive result (lifecycle, and the run-side halves of the void hooks).
        /// </summary>
        [TestMethod]
        [DataRow("public static void Initialize()", "WorldEventManager.Initialize();")]
        [DataRow("public static void Tick()", "WorldEventManager.Tick();")]
        [DataRow("public static void OnShutdown()", "WorldEventManager.OnShutdown();")]
        [DataRow("public static WorldEventAxisStore Store", "get => WorldEventManager.Store;")]
        [DataRow("public static List<ShardBiota> FilterOrphanedDynamics(", "WorldEventOrphanFilter.FilterAndSweep(landblockId, instance, dynamics);")]
        [DataRow("public static void OnGeneratedIntoWorld(", "WorldEventManager.OnGeneratedIntoWorld(wo, generator);")]
        [DataRow("public static void OnDamageRecorded(", "WorldEventCombatCreditHook.OnDamageRecorded(victim, attacker, amount);")]
        [DataRow("public static void OnCombatContact(", "WorldEventCombatCreditHook.OnCombatContact(victim, attacker);")]
        [DataRow("public static bool VictimStateRefusesCredit(", "WorldEventCombatCreditHook.VictimStateRefusesCredit(invincible, underLifestoneProtection);")]
        [DataRow("public static void NoteBossHit(", "WorldEventBossDamageHook.NoteHit(source, defender, damageTaken, crit);")]
        [DataRow("public static WorldEventAdminService CreateAdminService(", "WorldEventAdminService.CreateLive(enqueue, writeAudit, timeoutMs);")]
        [DataRow("public static void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)", "if (creature.P_WorldEvent == null) return; if (ReportsDeathToRun(true, creature.GetProperty(PropertyInt.WorldEventId))) WorldEventManager.OnEventCreatureDied(creature, lastDamager, topDamager);")]
        [DataRow("public static bool IsProtectedDeathInProgress(Player player)", "player.WorldEventDeathInProgress;")]
        [DataRow("public static bool IsObjectiveCreature(WorldObject wo)", "IsObjectiveCreature(wo.GetProperty(PropertyBool.WorldEventObjective));")]
        [DataRow("public static bool IsTransientEventObject(WorldObject wo)", "IsTransientEventObject(wo.GetProperty(PropertyInt.WorldEventId));")]
        [DataRow("public static bool IsPooledLootDeath(Creature creature)", "creature.P_WorldEvent != null && Creature.IsWorldEventPooledDeath(creature.P_WorldEvent, creature.GetProperty(PropertyInt.WorldEventId), creature.Guid.Full);")]
        [DataRow("public static bool BankPooledKill(", "WorldEventLootBank.BankKill(creature, killer);")]
        [DataRow("public static bool ProtectsDeathAt(Position loc)", "WorldEventManager.ProtectsDeathAt(loc);")]
        [DataRow("public static void OnPlayerDied(", "WorldEventManager.OnPlayerDied(player);")]
        [DataRow("public static bool TryUseRewardCrate(", "WorldEventRewardCrate.TryHandleUse(crate, player);")]
        [DataRow("public static bool TryUseCache(", "IsEventCache(cache.GetProperty(PropertyBool.WorldEventCache)) && WorldEventCacheHandler.TryHandleUse(cache, player);")]
        [DataRow("public static bool SkipsMonsterTick(bool worldEventObjective)", "WorldEventObjectiveRules.SkipsMonsterTick(worldEventObjective);")]
        [DataRow("public static void CompleteSkyDropAcquire(Creature creature)", "creature.CompleteSkyDropAcquire();")]
        [DataRow("public static bool SkyDropSettled(", "WorldObject.SkyDropSettled(onWalkable, now, creature.WorldEventSkyDropDeadline);")]
        [DataRow("public static (uint KnightRun, uint MonsterRun) HoldPhysicsRunIds(HoldUnitKind? kind, uint runId)", "WorldEventHoldRules.PhysicsRunIds(kind, runId);")]
        [DataRow("public static (uint KnightRun, uint MonsterRun) HoldPhysicsRunIds(Creature creature)", "var tag = creature.WorldEventHold; return tag == null ? default : HoldPhysicsRunIds(tag.Kind, tag.RunId);")]
        [DataRow("public static bool PhysicsIsHoldKnightTargetFor(", "WorldEventHoldRules.PhysicsIsKnightTargetFor(knightRun, attackerIsMonster, attackerRun);")]
        [DataRow("public static bool PhysicsIsHoldKnight(", "knightRun != 0;")]
        [DataRow("public static bool HoldMayDamageKnight(", "WorldEventHoldRules.MayDamageKnight(knightRun, attackerIsPetOrPlayer, attackerKind, attackerRun, runActive);")]
        [DataRow("public static bool TryHoldCanDamage(", "canDamage = attacker.IsHoldKnightAttackerOf(target);")]
        [DataRow("public static bool IsHoldKnight(Creature creature)", "IsHoldKnight(creature?.WorldEventHold?.Kind);")]
        [DataRow("public static bool RefusesPlayerHarm(", "IsHoldKnight(target);")]
        [DataRow("public static bool IsHoldKnightTargetFor(", "return attacker.IsHoldKnightAttackerOf(target);")]
        [DataRow("public static bool HoldKnightNeverResists(", "IsHoldKnightTargetFor(attacker, knight);")]
        [DataRow("public static double HoldPlayerDamageMult(", "return attacker.HoldPlayerDamageMult(ACE.Common.Time.GetUnixTime());")]
        [DataRow("public static float HoldWardDamage(", "return defender.ApplyHoldWard(kind, damage, ACE.Common.Time.GetUnixTime());")]
        [DataRow("public static float ScaleHoldKnightHit(", "return knight.ScaleHoldKnightHit(source, amount, ACE.Common.Time.GetUnixTime());")]
        [DataRow("public static bool TrySelectHoldTarget(", "return monster.TrySelectHoldTarget();")]
        [DataRow("public static bool KeepsHoldSeekerLock(", "return monster.TryKeepHoldSeekerLock(disturbedBy);")]
        [DataRow("public static void NoteHoldBossDamage(", "victim.NoteHoldBossDamage(attacker, amount, ACE.Common.Time.GetUnixTime());")]
        [DataRow("public static bool TryQueuePlayerHeal(", "if (!WorldEventHoldHeals.QueuePlayerHeal(target, raw, Reply))")]
        [DataRow("public static bool IsInvalidHoldKnightSpell(", "!(acceptsPlayerHeal && isHealSpell);")]
        [DataRow("public static bool IsInvalidHoldSpellTarget(", "invalid = IsInvalidHoldKnightSpell(target.AcceptsHoldPlayerHeal, isHeal);")]
        [DataRow("public static bool HoldAcceptsPlayerHeal(", "target?.WorldEventHold != null && target.AcceptsHoldPlayerHeal;")]
        [DataRow("public static bool IsHoldKnightHealTarget(", "target is Creature c && IsHoldKnight(c);")]
        [DataRow("public static bool TryUseHoldShrine(", "hold.Run.TryUseShrine(shrine, player, ACE.Common.Time.GetUnixTime(), out var reply)")]
        [DataRow("public static float HoldRunRate(", "return tag == null ? runRate : WorldEventHoldRules.ScaledRunRate(runRate, tag.RunRateMult);")]
        [DataRow("public static double HoldMeleeDelay(", "return tag == null ? meleeDelay : WorldEventHoldRules.ScaledMeleeDelay(meleeDelay, tag.AttackSpeedMult);")]
        [DataRow("public static float HoldAnimSpeed(", "return tag == null ? animSpeed : WorldEventHoldRules.ScaledAnimSpeed(animSpeed, tag.AttackSpeedMult);")]
        [DataRow("public static bool TickHoldRangedKeepDistance(", "return monster.TickHoldRangedKeepDistance(targetDist);")]
        [DataRow("public static bool HoldClientAttackable(WorldObject wo, bool attackable)", "return tag != null && HoldClientAttackable(attackable, tag.Kind == HoldUnitKind.Knight, tag.ShowHealthBar);")]
        [DataRow("public static void ApplyHoldTint(", "WorldEventTint.Apply(objDesc, tag.TintPalette);")]
        [DataRow("public static void SendHoldKnightHealthLine(", "if (!IsHoldKnightHealthLineTarget(target)) return; player.SendHoldKnightHealthLine((Creature)target, ACE.Common.Time.GetUnixTime());")]
        public void Hook_ForwardsToItsImplementation(string declaration, string forward)
        {
            var source = WorldEventHooksCallSiteTests.StripComments(File.ReadAllText(HooksPath()));

            var start = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"WorldEventHooks member not found: {declaration}");

            // The member ends where the next public member begins (or at the end of the class).
            var next = source.IndexOf("public static ", start + declaration.Length, StringComparison.Ordinal);
            var body = next < 0 ? source.Substring(start) : source.Substring(start, next - start);

            Assert.IsTrue(Regex.Replace(body, @"\s+", " ").Contains(Regex.Replace(forward, @"\s+", " ")),
                $"{declaration} must forward with: {forward}");
        }

        private static string HooksPath()
            => Path.Combine(WorldEventHooksCallSiteTests.RepoRoot(), "Source", "ACE.Server", "WorldEvents", "WorldEventHooks.cs");
    }
}
