using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadBoonTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Defense encounter's Hold side: the view (run ids, the active flag, the tuning, the no-op members), the pillar
    /// as a Hold knight of that view, the switch (the one write-once seeker tag and the re-cached physics run id), who may
    /// damage the pillar (TryHoldCanDamage and the knight hit sizing), the enemies' bonus-kill route, OnRunEnded, and the
    /// tunables' wiring. Reads no PropertyManager key (the code defaults are read from DefaultPropertyManager's tables).
    /// </summary>
    [TestClass]
    public class DefenseHoldViewTests
    {
        private static ThreadDefenseHoldView View(double undefended = 240, double minDeath = 45)
            => new ThreadDefenseHoldView(ThreadDefenseHoldView.BuildTuning(undefended, minDeath));

        private static Creature Pillar(ThreadDefenseHoldView view)
        {
            var pillar = HoldFixtures.MakeCreature("Test Ward", maxHealth: 6000, attackable: false);
            Assert.IsTrue(ThreadDefenseHold.PreparePillar(pillar, view, ThreadDefenseHold.PillarMaxHealth), "sanity: a fresh creature takes the knight tag");
            return pillar;
        }

        // ---- the view ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void View_run_ids_are_high_bit_distinct_and_never_zero()
        {
            var ids = Enumerable.Range(0, 50).Select(_ => View().RunId).ToList();

            Assert.IsTrue(ids.All(id => (id & ThreadDefenseHoldView.FirstRunId) != 0), "every id is in the high-bit range");
            Assert.AreEqual(ids.Count, ids.Distinct().Count());
            Assert.IsTrue(ids.All(id => id != 0));
        }

        [TestMethod]
        public void View_starts_inactive_carries_the_pacing_and_turns_every_heal_off()
        {
            var view = View(240, 45);

            Assert.IsFalse(view.IsActive, "nothing may hurt an unwoken pillar");
            view.Activate();
            Assert.IsTrue(view.IsActive);
            view.Deactivate();
            Assert.IsFalse(view.IsActive);

            var t = view.Tuning;
            Assert.AreEqual(240.0, t.UndefendedSeconds);
            Assert.AreEqual(240.0, t.UndefendedSecondsBase);
            Assert.AreEqual(45.0, t.MinDeathSeconds);
            Assert.IsFalse(t.PlayerHealEnabled);
            Assert.IsFalse(t.DefendPoseEnabled);
            Assert.IsFalse(t.ShrineEnabled);
            Assert.AreEqual(0.0, t.RegenPerSecond);
            Assert.AreEqual(1.0, t.Role(HoldDamageRole.None).NpcDamageMult, "a role-less seeker hits at the plain pacing");

            Assert.AreEqual(0, view.WaveNumber);
            Assert.AreEqual(1, view.ParticipantCount);
            Assert.AreEqual(1.0, view.FinaleEnrageMult(null, 0));
            Assert.AreEqual(0, view.RecentHealers(0).Count);
            Assert.IsFalse(view.TryUseShrine(null, null, 0, out var reply));
            Assert.IsNull(reply);
            Assert.AreEqual(0, view.Knights.Count);
            Assert.AreEqual(0, view.WaveMonsters.Count);
        }

        [TestMethod]
        public void PublishMonsters_republishes_a_fresh_immutable_list()
        {
            var view = View();
            var a = HoldFixtures.MakeCreature("A");
            var b = HoldFixtures.MakeCreature("B");

            view.PublishMonsters(new[] { a, null, b });
            var first = view.WaveMonsters;
            CollectionAssert.AreEqual(new[] { a, b }, first.ToList());

            view.PublishMonsters(new[] { b });
            CollectionAssert.AreEqual(new[] { a, b }, first.ToList(), "a list already handed out never changes");
            CollectionAssert.AreEqual(new[] { b }, view.WaveMonsters.ToList());
        }

        // ---- the pillar ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_pillar_is_a_protected_knight_of_its_view()
        {
            var view = View();
            var pillar = Pillar(view);
            var tag = pillar.WorldEventHold;

            Assert.AreEqual(HoldUnitKind.Knight, tag.Kind);
            Assert.AreEqual(view.RunId, tag.RunId);
            Assert.AreSame(view, tag.Run);
            Assert.AreEqual((uint)ThreadDefenseHold.PillarMaxHealth, pillar.Health.MaxValue);
            Assert.AreEqual(pillar.Health.MaxValue, pillar.Health.Current);
            CollectionAssert.AreEqual(new[] { pillar }, view.Knights.ToList());
            Assert.IsTrue(WorldEventHooks.RefusesPlayerHarm(pillar), "players can never hurt it");
            Assert.IsFalse(ThreadDefenseHold.PreparePillar(pillar, View(), 100), "the tag is write-once");
        }

        /// <summary>
        /// Owner playtest ruling 2026-11-11, item 1: the pillar always presents as a friendly NPC. Before this, the Defense
        /// row's showHealthBar sent the Attackable bit to the client as TRUE (WorldEventHooks.HoldClientAttackable) while the
        /// server held it false with the RubberGlue PK status, so the client was told two different things about one object.
        /// </summary>
        [TestMethod]
        public void The_pillar_is_sent_to_the_client_as_a_friendly_npc()
        {
            var pillar = Pillar(View());
            var tag = pillar.WorldEventHold;

            Assert.IsFalse(tag.ShowHealthBar);
            Assert.IsFalse(pillar.Attackable);
            Assert.AreEqual(WorldEventSpawner.ProtectedPlayerKillerStatus, pillar.PlayerKillerStatus);
            Assert.IsFalse(WorldEventHooks.HoldClientAttackable(pillar, pillar.Attackable), "the client is sent exactly what the server holds: not attackable");

            // Positive control: the World Events knight shape with a shown bar IS sent attackable (that path is unchanged).
            var weKnight = HoldFixtures.MakeCreature("Envoy", attackable: false);
            Assert.IsTrue(weKnight.SetWorldEventHoldTag(WorldEventSpawner.KnightTag(9, View(), true)));
            Assert.IsTrue(WorldEventHooks.HoldClientAttackable(weKnight, false));
        }

        /// <summary>
        /// Review R3: PillarTag is the engine's KnightTag shape plus two opt-ins. Every other tag property must match
        /// KnightTag's, so a field a future KnightTag sets cannot silently miss the pillar.
        /// </summary>
        [TestMethod]
        public void PillarTag_is_KnightTag_except_its_two_opt_ins()
        {
            var view = View();
            var pillar = ThreadDefenseHold.PillarTag(view);
            var knight = WorldEventSpawner.KnightTag(view.RunId, view, false);
            var optIns = new HashSet<string> { nameof(WorldEventHoldTag.ShowHealthBar), nameof(WorldEventHoldTag.NoAssessHealthLine) };

            var compared = 0;

            foreach (var prop in typeof(WorldEventHoldTag).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (optIns.Contains(prop.Name))
                    continue;

                Assert.AreEqual(prop.GetValue(knight), prop.GetValue(pillar), $"PillarTag.{prop.Name} must equal KnightTag's");
                compared++;
            }

            Assert.IsTrue(compared >= 10, $"sanity: the tag's properties were compared ({compared})");
            Assert.IsFalse(pillar.ShowHealthBar);
            Assert.IsTrue(pillar.NoAssessHealthLine);
        }

        /// <summary>
        /// Owner playtest ruling 2026-11-11, item 2: no health percent in chat before the defense starts. The source was the
        /// Hold engine's assess line ("Test Ward is at 100% health."), sent every 3 s while the pillar stays selected
        /// (Player.HandleActionIdentifyObject -> WorldEventHooks.SendHoldKnightHealthLine), whether or not the view was active.
        /// </summary>
        [TestMethod]
        public void Assessing_the_pillar_never_says_its_health_but_a_World_Events_knight_still_does()
        {
            var pillar = Pillar(View());

            Assert.IsTrue(pillar.WorldEventHold.NoAssessHealthLine);
            Assert.IsFalse(WorldEventHooks.IsHoldKnightHealthLineTarget(pillar), "the pillar opts out");

            var weKnight = HoldFixtures.MakeCreature("Envoy", attackable: false);
            Assert.IsTrue(weKnight.SetWorldEventHoldTag(WorldEventSpawner.KnightTag(9, View(), true)));
            Assert.IsFalse(weKnight.WorldEventHold.NoAssessHealthLine, "the engine's own knight tag leaves it off");
            Assert.IsTrue(WorldEventHooks.IsHoldKnightHealthLineTarget(weKnight), "World Events behaviour unchanged");
        }

        // ---- the switch and who may hurt the pillar ------------------------------------------------------------------

        [TestMethod]
        public void TagAsSeeker_sets_the_one_seeker_tag_and_the_physics_monster_run_id()
        {
            var view = View();
            var enemy = HoldFixtures.MakeCreature("Enemy");

            Assert.IsFalse(ThreadDefenseHold.IsTagged(enemy), "an enemy comes through untagged");
            Assert.AreEqual((0u, 0u), WorldEventHooks.HoldPhysicsRunIds(enemy));

            Assert.IsTrue(ThreadDefenseHold.TagAsSeeker(enemy, view));

            var tag = enemy.WorldEventHold;
            Assert.AreEqual(HoldUnitKind.Monster, tag.Kind);
            Assert.IsTrue(tag.Seeker);
            Assert.IsFalse(tag.Hunter);
            Assert.AreEqual(HoldDamageRole.None, tag.Role);
            Assert.AreEqual(view.RunId, tag.RunId);
            Assert.IsTrue(ThreadDefenseHold.IsTaggedFor(enemy, view));
            Assert.IsFalse(ThreadDefenseHold.IsTaggedFor(enemy, View()));
            Assert.AreEqual((0u, view.RunId), WorldEventHooks.HoldPhysicsRunIds(enemy), "the value TagAsSeeker re-caches on the physics object");

            Assert.IsFalse(ThreadDefenseHold.TagAsSeeker(enemy, View()), "write-once: a second switch changes nothing");
            Assert.AreEqual(view.RunId, enemy.WorldEventHold.RunId);
        }

        [TestMethod]
        public void RecacheMonsterRun_moves_only_the_monster_run_id_from_zero_to_the_view()
        {
            var view = View();
            var enemy = HoldFixtures.MakeCreature("Enemy");

            // What EnterWorld built for an enemy that came through untagged.
            var weenie = new ACE.Server.Physics.Common.WeenieObject(enemy);
            var knightRun = weenie.HoldKnightRunId;
            var isMonster = weenie.IsMonster;

            Assert.AreEqual(0u, weenie.HoldMonsterRunId, "untagged at EnterWorld: nothing cached");
            Assert.AreEqual(0u, knightRun);

            Assert.IsTrue(ThreadDefenseHold.TagAsSeeker(enemy, view), "no physics object in the harness: the tag only");
            Assert.AreEqual(0u, weenie.HoldMonsterRunId, "control: the tag alone does not reach a cache built before it");

            ThreadDefenseHold.RecacheMonsterRun(weenie, enemy);

            Assert.AreEqual(view.RunId, weenie.HoldMonsterRunId, "the visibility filter now lets the seeker keep the pillar");
            Assert.AreEqual(knightRun, weenie.HoldKnightRunId, "the knight run id is untouched");
            Assert.AreEqual(isMonster, weenie.IsMonster, "IsMonster is untouched");

            ThreadDefenseHold.RecacheMonsterRun(null, enemy);
            ThreadDefenseHold.RecacheMonsterRun(weenie, null);
            Assert.AreEqual(view.RunId, weenie.HoldMonsterRunId, "null-safe and changes nothing");

            // TagAsSeeker is what calls it in production (the enemy's live physics object).
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/Hold/ThreadDefenseHold.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static bool TagAsSeeker(Creature enemy, ThreadDefenseHoldView view)");
            StringAssert.Contains(body, "RecacheMonsterRun(enemy.PhysicsObj?.WeenieObj, enemy);");
        }

        [TestMethod]
        public void An_enemy_removed_without_dying_is_no_longer_standing_so_success_stays_reachable()
        {
            var alive = HoldFixtures.MakeCreature("Alive");
            var removed = HoldFixtures.MakeCreature("Removed");

            Assert.IsTrue(DefenseEncounterType.IsStanding(alive));
            Assert.IsTrue(DefenseEncounterType.IsStanding(removed), "control: standing before the delete");

            removed.Destroy();

            Assert.IsFalse(removed.IsDead, "destroyed, not killed");
            Assert.IsFalse(DefenseEncounterType.IsStanding(removed), "owner ruling: dead or removed");
            Assert.IsFalse(DefenseEncounterType.IsStanding(null));

            var standing = new[] { alive, removed }.Count(DefenseEncounterType.IsStanding);
            Assert.AreEqual(1, standing);
            Assert.AreEqual(DefenseOutcome.None, DefenseEncounterType.Decide(true, true, standing, 10, 300));

            alive.Destroy();
            standing = new[] { alive, removed }.Count(DefenseEncounterType.IsStanding);
            Assert.AreEqual(DefenseOutcome.Success, DefenseEncounterType.Decide(true, true, standing, 10, 300), "every enemy gone: success");

            // The monitor counts with this very predicate.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DefenseEncounterType.cs");
            StringAssert.Contains(src, "alive = state.Enemies.Where(e => IsStanding(e.Creature)).ToList();");
        }

        [TestMethod]
        public void SwitchToSeeker_refuses_while_the_view_is_inactive_and_on_a_dead_enemy()
        {
            var view = View();
            var enemy = HoldFixtures.MakeCreature("Enemy");

            Assert.IsFalse(ThreadDefenseHold.SwitchToSeeker(enemy, view), "inactive view");
            Assert.IsFalse(ThreadDefenseHold.IsTagged(enemy));

            view.Activate();
            Assert.IsTrue(ThreadDefenseHold.SwitchToSeeker(enemy, view), "no physics object in the harness: tagged, the retarget skipped");
            Assert.IsTrue(enemy.WorldEventHold.Seeker);
        }

        [TestMethod]
        public void Only_a_same_view_seeker_may_hurt_the_pillar_and_only_while_active()
        {
            var view = View();
            var pillar = Pillar(view);
            var seeker = HoldFixtures.MakeCreature("Seeker");
            var otherViewSeeker = HoldFixtures.MakeCreature("Other");
            var untagged = HoldFixtures.MakeCreature("Untagged");

            Assert.IsTrue(ThreadDefenseHold.TagAsSeeker(seeker, view));
            Assert.IsTrue(ThreadDefenseHold.TagAsSeeker(otherViewSeeker, View()));

            Assert.IsTrue(WorldEventHooks.TryHoldCanDamage(seeker, pillar, out var can), "a thread-tagged pillar is decided by the Hold route");
            Assert.IsFalse(can, "inactive: nothing lands");

            view.Activate();

            Assert.IsTrue(WorldEventHooks.TryHoldCanDamage(seeker, pillar, out can));
            Assert.IsTrue(can, "the view's own seeker, active");
            Assert.IsTrue(WorldEventHooks.IsHoldKnightTargetFor(seeker, pillar), "its hits always land");

            Assert.IsTrue(WorldEventHooks.TryHoldCanDamage(otherViewSeeker, pillar, out can));
            Assert.IsFalse(can, "another view's monster");

            Assert.IsTrue(WorldEventHooks.TryHoldCanDamage(untagged, pillar, out can));
            Assert.IsFalse(can, "an unswitched enemy cannot hurt it");

            Assert.IsFalse(WorldEventHooks.TryHoldCanDamage(seeker, untagged, out _), "positive control: a non-knight target is decided as before");

            view.Deactivate();
            Assert.IsTrue(WorldEventHooks.TryHoldCanDamage(seeker, pillar, out can));
            Assert.IsFalse(can, "after the outcome nothing lands");
        }

        [TestMethod]
        public void A_seekers_hit_is_paced_by_undefended_seconds_whatever_it_rolled()
        {
            // min_death 0: no cap, so the slice is exactly maxHealth x first-hit interval (2 s) / undefended.
            var view = View(undefended: 240, minDeath: 0);
            var pillar = Pillar(view);
            var seeker = HoldFixtures.MakeCreature("Seeker");
            var untagged = HoldFixtures.MakeCreature("Untagged");

            ThreadDefenseHold.TagAsSeeker(seeker, view);
            view.Activate();

            var now = 1_000_000.0;
            var expected = (float)Math.Round(10000 * 2.0 / 240.0);

            Assert.AreEqual(expected, pillar.ScaleHoldKnightHit(seeker, 5f, now), "a weak swing still takes its paced slice");
            Assert.AreEqual(37f, pillar.ScaleHoldKnightHit(untagged, 37f, now), "control: an untagged attacker is handed back unchanged");

            var hardView = View(undefended: 240, minDeath: 0);
            var hard = Pillar(hardView);
            var hardSeeker = HoldFixtures.MakeCreature("Seeker 2");
            ThreadDefenseHold.TagAsSeeker(hardSeeker, hardView);
            hardView.Activate();
            Assert.AreEqual(expected, hard.ScaleHoldKnightHit(hardSeeker, 9999f, now), "a huge swing takes the same slice");
        }

        // ---- the enemies' kills ----------------------------------------------------------------------------------------

        [TestMethod]
        public void A_defense_enemy_is_a_bonus_kill_switched_or_not_and_never_reaches_RecordKill()
        {
            var saved = ThreadDungeonManager.PooledLootTrigger;

            try
            {
                ThreadDungeonManager.PooledLootTrigger = _ => throw new AssertFailedException("not a pooled run");

                var run = Run();
                var before = (run.TrashKilled, run.ClearTarget, run.ClearProgress, run.State);

                var unswitched = RunCreature(run, bonus: true);
                var switched = RunCreature(run, bonus: true);
                var view = View();
                view.Activate();
                Assert.IsTrue(ThreadDefenseHold.TagAsSeeker(switched, view));

                Assert.IsTrue(ThreadDungeonManager.TryRecordBonusKill(run, unswitched));
                Assert.IsTrue(ThreadDungeonManager.TryRecordBonusKill(run, switched), "the Hold tag does not change the thread route");

                Assert.AreEqual(2, run.BonusKills);
                Assert.AreEqual(before, (run.TrashKilled, run.ClearTarget, run.ClearProgress, run.State), "the clear does not move");
            }
            finally
            {
                ThreadDungeonManager.PooledLootTrigger = saved;
            }

            // The enemies are shaped ExcludedFromClear, which is what sets IsThreadBonusCreature (ThreadBonusKillTests pins that).
            // The shared spawn takes the flag as a parameter (ThreadEncounterPortalWaves.TrySpawnEnemy); Defense passes true.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadEncounterPortalWaves.cs");
            var spawn = PooledLootSourceText.MethodBody(src, "public Creature TrySpawnEnemy(ThreadEncounterPlaceRequest request, DungeonSpawnPlanEntry entry, bool excludedFromClear)");
            StringAssert.Contains(spawn, "ExcludedFromClear = excludedFromClear,");
            StringAssert.Contains(spawn, "Rewards = true,");
            Assert.IsFalse(spawn.Contains("RecordKill"), "nothing here counts a kill");

            var defense = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DefenseEncounterType.cs");
            var spawnEnemy = PooledLootSourceText.MethodBody(defense, "private static void SpawnEnemy(ThreadEncounterInstance instance, DefenseState state, PortalWaveSpawn spawn)");
            StringAssert.Contains(spawnEnemy, "placed = Waves.TrySpawnEnemy(request, spawn.Entry, excludedFromClear: true);");
        }

        // ---- run end ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void OnRunEnded_deactivates_the_view_and_drops_the_request()
        {
            var run = DungeonRun(new[] { Point(DungeonCell(), 0, 0) }, max: 1);
            var type = new DefenseEncounterType();
            var pick = new ThreadEncounterPick(1, Type("defense", kind: DefenseEncounterType.KindName), Candidate(Point(DungeonCell(), 0, 0)), 7);
            var instance = run.NewEncounterInstance(pick, type);
            var view = View();
            var state = new DefenseEncounterType.DefenseState(new ThreadEncounterPlaceRequest(run, null, instance, null), new DefenseConfig(), view, null);
            instance.State = state;

            Assert.IsTrue(DefenseEncounterType.TryStart(state, 10.0), "the first use starts it");
            Assert.IsTrue(view.IsActive);
            Assert.IsFalse(DefenseEncounterType.TryStart(state, 11.0), "a second use does nothing");

            type.OnRunEnded(instance, "test");
            type.OnRunEnded(instance, "again");

            Assert.IsNull(state.Request);
            Assert.IsFalse(view.IsActive);
            Assert.IsFalse(DefenseEncounterType.TryStart(new DefenseEncounterType.DefenseState(null, new DefenseConfig(), View(), null), 1.0), "an ended run never starts");
        }

        // ---- tunables ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_tunables_clamp_and_their_code_defaults_match()
        {
            Assert.AreEqual(240.0, ThreadDungeonRun.ClampDefenseUndefendedSeconds(double.NaN));
            Assert.AreEqual(5.0, ThreadDungeonRun.ClampDefenseUndefendedSeconds(1));
            Assert.AreEqual(1800.0, ThreadDungeonRun.ClampDefenseUndefendedSeconds(5000));
            Assert.AreEqual(45.0, ThreadDungeonRun.ClampDefenseMinDeathSeconds(double.NaN));
            Assert.AreEqual(0.0, ThreadDungeonRun.ClampDefenseMinDeathSeconds(-3));
            Assert.AreEqual(600.0, ThreadDungeonRun.ClampDefenseMinDeathSeconds(900));
            Assert.AreEqual(20.0, ThreadDungeonRun.ClampDefenseSwitchSeconds(double.NaN));
            Assert.AreEqual(0.0, ThreadDungeonRun.ClampDefenseSwitchSeconds(-1));
            Assert.AreEqual(600.0, ThreadDungeonRun.ClampDefenseSwitchSeconds(601));
            Assert.AreEqual(60L, ThreadDungeonRun.ClampDefenseMaxSeconds(10));
            Assert.AreEqual(1800L, ThreadDungeonRun.ClampDefenseMaxSeconds(99999));
            Assert.AreEqual(300L, ThreadDungeonRun.ClampDefenseMaxSeconds(300));

            Assert.AreEqual(240.0, DefaultPropertyManager.DefaultDoubleProperties["dynamic_dungeons_defense_undefended_seconds"].Item);
            Assert.AreEqual(45.0, DefaultPropertyManager.DefaultDoubleProperties["dynamic_dungeons_defense_min_death_seconds"].Item);
            Assert.AreEqual(20.0, DefaultPropertyManager.DefaultDoubleProperties["dynamic_dungeons_defense_switch_seconds"].Item);
            Assert.AreEqual(300L, DefaultPropertyManager.DefaultLongProperties["dynamic_dungeons_defense_max_seconds"].Item);

            var unstamped = Run();
            Assert.AreEqual((240.0, 45.0, 20.0, 300L), (unstamped.DefenseUndefendedSeconds, unstamped.DefenseMinDeathSeconds, unstamped.DefenseSwitchSeconds, unstamped.DefenseMaxSeconds),
                "a run nobody stamped uses the shipped defaults");
        }

        [TestMethod]
        public void TryStart_reads_each_tunable_once_and_stamps_it_on_the_run()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");

            StringAssert.Contains(src, "var defenseUndefendedSeconds = ThreadDungeonRun.ClampDefenseUndefendedSeconds(PropertyManager.GetDouble(\"dynamic_dungeons_defense_undefended_seconds\", ThreadDungeonRun.DefaultDefenseUndefendedSeconds).Item);");
            StringAssert.Contains(src, "var defenseMinDeathSeconds = ThreadDungeonRun.ClampDefenseMinDeathSeconds(PropertyManager.GetDouble(\"dynamic_dungeons_defense_min_death_seconds\", ThreadDungeonRun.DefaultDefenseMinDeathSeconds).Item);");
            StringAssert.Contains(src, "var defenseSwitchSeconds = ThreadDungeonRun.ClampDefenseSwitchSeconds(PropertyManager.GetDouble(\"dynamic_dungeons_defense_switch_seconds\", ThreadDungeonRun.DefaultDefenseSwitchSeconds).Item);");
            StringAssert.Contains(src, "var defenseMaxSeconds = ThreadDungeonRun.ClampDefenseMaxSeconds(PropertyManager.GetLong(\"dynamic_dungeons_defense_max_seconds\", ThreadDungeonRun.DefaultDefenseMaxSeconds).Item);");

            StringAssert.Contains(src, "DefenseUndefendedSeconds = defenseUndefendedSeconds,");
            StringAssert.Contains(src, "DefenseMinDeathSeconds = defenseMinDeathSeconds,");
            StringAssert.Contains(src, "DefenseSwitchSeconds = defenseSwitchSeconds,");
            StringAssert.Contains(src, "DefenseMaxSeconds = defenseMaxSeconds,");

            // The encounter reads the stamped values, never the property.
            var type = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DefenseEncounterType.cs");
            Assert.IsFalse(type.Contains("PropertyManager"), "the encounter reads only the run's stamped values");
            StringAssert.Contains(type, "run.DefenseSwitchSeconds");
            StringAssert.Contains(type, "run.DefenseMaxSeconds");
            StringAssert.Contains(type, "ThreadDefenseHoldView.BuildTuning(run.DefenseUndefendedSeconds, run.DefenseMinDeathSeconds)");
        }
    }
}
