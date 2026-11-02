using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H0: the cross-slice contract types. The creature tag is write-once; the run view's any-thread
    /// members hold up under concurrent use and publish whole snapshots; the tuning snapshot reproduces the shipped goals.json
    /// values for defend_envoys when no property is set, and the run keeps NextRun rows frozen while it re-reads Live ones.
    ///
    /// No PropertyManager and no database: every settings resolve goes through an in-memory lookup. No Player is built, and
    /// no field is set by reflection; a Creature is made with RuntimeHelpers.GetUninitializedObject only where the tag's
    /// write-once rule, which touches nothing else, is under test.
    /// </summary>
    [TestClass]
    public class WorldEventHoldContractTests
    {
        private static Creature BareCreature() => (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

        private static string AxesFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Content", "events", "axes", "goals.json")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/events/axes/goals.json by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Content", "events", "axes");
        }

        private static GoalDef DefendEnvoys()
        {
            var store = WorldEventAxisStore.Load(AxesFolder());

            Assert.IsTrue(store.Goals.TryGetValue("defend_envoys", out var goal), "goals.json did not yield defend_envoys");

            return goal;
        }

        private static Func<GoalDef, WorldEventHoldSettings> Resolver(Dictionary<string, string> props) =>
            g => WorldEventHoldSettingsResolver.Resolve(g, p => props.TryGetValue(p, out var v) ? v : null);

        // ---- the tag ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Tag_IsWriteOnce_SecondSetReturnsFalseAndKeepsTheFirst()
        {
            var creature = BareCreature();
            var first = new WorldEventHoldTag { RunId = 7, Kind = HoldUnitKind.Knight };
            var second = new WorldEventHoldTag { RunId = 8, Kind = HoldUnitKind.Monster };

            Assert.IsNull(creature.WorldEventHold, "an untagged creature reads null");

            Assert.IsTrue(creature.SetWorldEventHoldTag(first));
            Assert.AreSame(first, creature.WorldEventHold);

            Assert.IsFalse(creature.SetWorldEventHoldTag(second), "a second set is refused");
            Assert.AreSame(first, creature.WorldEventHold, "and leaves the first in place");
            Assert.AreEqual(7u, creature.WorldEventHold.RunId);
        }

        [TestMethod]
        public void Tag_NullIsRefused_AndDoesNotUseUpTheSlot()
        {
            var creature = BareCreature();
            var tag = new WorldEventHoldTag { RunId = 3 };

            Assert.IsFalse(creature.SetWorldEventHoldTag(null));
            Assert.IsNull(creature.WorldEventHold);

            Assert.IsTrue(creature.SetWorldEventHoldTag(tag), "a refused null leaves the creature taggable");
            Assert.AreSame(tag, creature.WorldEventHold);
        }

        [TestMethod]
        public void Tag_RacingSets_ExactlyOneWins()
        {
            for (var round = 0; round < 20; round++)
            {
                var creature = BareCreature();
                var tags = Enumerable.Range(0, 32).Select(i => new WorldEventHoldTag { RunId = (uint)i + 1 }).ToArray();
                var wins = new bool[tags.Length];

                Parallel.For(0, tags.Length, i => wins[i] = creature.SetWorldEventHoldTag(tags[i]));

                Assert.AreEqual(1, wins.Count(w => w), $"round {round}: exactly one set wins");
                Assert.AreSame(tags[Array.IndexOf(wins, true)], creature.WorldEventHold, $"round {round}: the winner's tag is the one kept");
            }
        }

        [TestMethod]
        public void Tag_Defaults_AreTheIdentity()
        {
            var tag = new WorldEventHoldTag();

            Assert.AreEqual(1f, tag.RunRateMult);
            Assert.AreEqual(1f, tag.AttackSpeedMult);
            Assert.AreEqual(0f, tag.HoldDistance);
            Assert.AreEqual(HoldDamageRole.None, tag.Role);
            Assert.AreEqual(0u, tag.ForcedTargetGuid);
            Assert.IsNull(tag.Run);
        }

        // ---- the run view -------------------------------------------------------------------------------------------

        [TestMethod]
        public void Run_BeforeStage_IsInactiveWithTheDefaultTuning()
        {
            var run = new WorldEventHoldRun(42, Resolver(new Dictionary<string, string>()));

            Assert.AreEqual(42u, run.RunId);
            Assert.IsFalse(run.IsActive);
            Assert.AreEqual(0, run.WaveNumber);
            Assert.AreEqual(1, run.ParticipantCount);
            Assert.AreSame(WorldEventHoldTuning.Defaults, run.Tuning);
            Assert.AreEqual(0, run.Knights.Count);
            Assert.AreEqual(0, run.WaveMonsters.Count);
            Assert.IsFalse(run.Refresh(1000), "nothing to refresh before Stage");
        }

        [TestMethod]
        public void Run_PublishedLists_AreSnapshots_ANewPublishNeverChangesOneAlreadyRead()
        {
            var run = new WorldEventHoldRun(1);
            var a = BareCreature();
            var b = BareCreature();

            run.PublishWaveMonsters(new[] { a, null });
            var before = run.WaveMonsters;

            run.PublishWaveMonsters(new[] { a, b });

            Assert.AreEqual(1, before.Count, "the list read earlier is unchanged, and nulls were dropped");
            Assert.AreSame(a, before[0]);
            Assert.AreEqual(2, run.WaveMonsters.Count);

            run.PublishKnights(new[] { a });
            var knights = run.Knights;
            run.PublishKnights(Array.Empty<Creature>());

            Assert.AreEqual(1, knights.Count);
            Assert.AreEqual(0, run.Knights.Count);
            Assert.AreEqual(1, run.PlacedKnightGuids.Count, "placed knights never shrink");
        }

        [TestMethod]
        public void Run_RecentHealers_ForgetsAfterTheMemoryWindow_MostRecentFirst()
        {
            var run = new WorldEventHoldRun(1);

            Assert.AreEqual(20.0, run.Tuning.HealerMemorySeconds, "healer_memory_seconds code default");

            run.NoteKnightHealer(11u, 100);
            run.NoteKnightHealer(22u, 110);
            run.NoteKnightHealer(0u, 110);

            CollectionAssert.AreEqual(new List<uint> { 22, 11 }, run.RecentHealers(115).ToList());
            CollectionAssert.AreEqual(new List<uint> { 22 }, run.RecentHealers(125).ToList(), "11 healed 25 s ago");
            Assert.AreEqual(0, run.RecentHealers(200).Count);
        }

        [TestMethod]
        public void Run_ViewMembers_AreSafeUnderConcurrentWritersAndReaders()
        {
            var run = new WorldEventHoldRun(1);
            var boss = BareCreature();
            var errors = 0;

            Parallel.For(0, 64, i =>
            {
                try
                {
                    for (var n = 0; n < 200; n++)
                    {
                        run.NoteKnightHealer((uint)(i * 1000 + n + 1), 100);
                        _ = run.RecentHealers(101);
                        _ = run.FinaleEnrageMult(boss, 100);
                        _ = run.Tuning.HealerMemorySeconds;
                        _ = run.Knights.Count;

                        if (n % 50 == 0)
                            run.PublishWaveMonsters(new[] { boss });
                    }
                }
                catch
                {
                    System.Threading.Interlocked.Increment(ref errors);
                }
            });

            Assert.AreEqual(0, errors);
            Assert.AreEqual(64 * 200, run.RecentHealers(101).Count, "no write was lost");
        }

        [TestMethod]
        public void Run_FinaleEnrage_StartsAfterItsSecondsFromTheRecordedSpawn()
        {
            var props = new Dictionary<string, string>
            {
                [K.PropertyPrefix + K.FinaleEnrageSeconds] = "60",
                [K.PropertyPrefix + K.FinaleEnrageMult] = "2",
            };

            var run = new WorldEventHoldRun(1, Resolver(props));
            run.Stage(DefendEnvoys(), 3, 1000);

            Assert.AreEqual(1.0, run.FinaleEnrageMult(7u, 2000), "a boss the run never saw spawn never enrages");

            run.NoteFinaleBossSpawned(7u, 1000);

            Assert.AreEqual(1.0, run.FinaleEnrageMult(7u, 1059));
            Assert.AreEqual(2.0, run.FinaleEnrageMult(7u, 1060));

            Assert.AreEqual(1.0, WorldEventHoldRun.EnrageMult(1000, 5000, 0, 2), "0 seconds = never");
            Assert.AreEqual(1.0, WorldEventHoldRun.EnrageMult(1000, 5000, 60, 0.5), "a multiplier below 1 is the identity");
        }

        [TestMethod]
        public void Run_TryUseShrine_HandlesNothingYet()
        {
            var run = new WorldEventHoldRun(1);

            Assert.IsFalse(run.TryUseShrine(BareCreature(), null, 100, out var reply));
            Assert.IsNull(reply);
        }

        [TestMethod]
        public void Run_Refresh_TakesLiveRows_KeepsNextRunRowsFrozen_AtMostOnceASecond()
        {
            var props = new Dictionary<string, string>
            {
                [K.PropertyPrefix + K.MaxWaves] = "12",                // NextRun
                [K.PropertyPrefix + K.KnightDamageTakenMult] = "2",    // Live
            };

            var run = new WorldEventHoldRun(1, Resolver(props));
            run.Stage(DefendEnvoys(), 1, 1000);

            Assert.AreEqual(12, run.Tuning.MaxWaves);
            Assert.AreEqual(2.0, run.Tuning.KnightDamageTakenMult);

            props[K.PropertyPrefix + K.MaxWaves] = "15";
            props[K.PropertyPrefix + K.KnightDamageTakenMult] = "3";

            Assert.IsFalse(run.Refresh(1000.5), "less than a second since the last resolve");
            Assert.AreEqual(2.0, run.Tuning.KnightDamageTakenMult);

            Assert.IsTrue(run.Refresh(1001));
            Assert.AreEqual(3.0, run.Tuning.KnightDamageTakenMult, "a Live row reaches the running run");
            Assert.AreEqual(12, run.Tuning.MaxWaves, "a NextRun row stays as staged");
        }

        [TestMethod]
        public void Run_StageResolveThrows_RefreshRetriesIt_AndTheTuningGoesLive()
        {
            var props = new Dictionary<string, string> { [K.PropertyPrefix + K.KnightDamageTakenMult] = "2" };
            var inner = Resolver(props);
            var calls = 0;

            var run = new WorldEventHoldRun(1, g =>
            {
                calls++;

                if (calls == 1)
                    throw new InvalidOperationException("first resolve fails");

                return inner(g);
            });

            run.Stage(DefendEnvoys(), 1, 1000);

            Assert.AreSame(WorldEventHoldTuning.Defaults, run.Tuning, "a failed stage resolve leaves the defaults");

            Assert.IsFalse(run.Refresh(1000.5), "the retry waits out the refresh interval too");
            Assert.AreEqual(1, calls);

            Assert.IsTrue(run.Refresh(1001), "the retry succeeds");
            Assert.AreEqual(2, calls);
            Assert.AreNotSame(WorldEventHoldTuning.Defaults, run.Tuning);
            Assert.AreEqual(2.0, run.Tuning.KnightDamageTakenMult);
            Assert.AreEqual(60.0, run.Tuning.MinDeathSeconds, "goals.json values are live once resolved");

            // From here it is an ordinary refresh: Live rows move.
            props[K.PropertyPrefix + K.KnightDamageTakenMult] = "3";
            Assert.IsTrue(run.Refresh(1002));
            Assert.AreEqual(3.0, run.Tuning.KnightDamageTakenMult);
        }

        [TestMethod]
        public void Run_StageResolveKeepsThrowing_RefreshKeepsTheDefaults_WithoutThrowing()
        {
            var run = new WorldEventHoldRun(1, _ => throw new InvalidOperationException("always fails"));

            run.Stage(DefendEnvoys(), 1, 1000);

            for (var t = 1001; t < 1006; t++)
                Assert.IsFalse(run.Refresh(t));

            Assert.AreSame(WorldEventHoldTuning.Defaults, run.Tuning);
        }

        // ---- the tuning snapshot ------------------------------------------------------------------------------------

        [TestMethod]
        public void Tuning_From_DefendEnvoys_NoPropertySet_ReproducesGoalsJson()
        {
            var goal = DefendEnvoys();
            var t = WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, _ => null), goal, 1);

            // Knights (protect.*).
            Assert.AreEqual(200.0, t.UndefendedSecondsBase);
            Assert.AreEqual(65.0, t.UndefendedSecondsPerParticipant);
            Assert.AreEqual(100.0, t.UndefendedSecondsFloor);
            Assert.AreEqual(200.0, t.UndefendedSeconds, "one participant: no reduction");
            Assert.AreEqual(60.0, t.MinDeathSeconds);
            Assert.AreEqual(1, t.MinSurvivors);
            Assert.AreEqual(480.0, t.HoldSeconds);

            // Healing.
            Assert.AreEqual(0.5, t.HealReductionAfterHit);
            Assert.AreEqual(5.0, t.HealReductionSeconds);
            Assert.AreEqual(0.35, t.HealBudgetPerWave);
            Assert.AreEqual(0.01, t.RescueHeal);
            Assert.AreEqual(0.1, t.RescueCapPerWave);
            Assert.AreEqual(0.1, t.CorruptedKillHeal);
            Assert.AreEqual(0.1, t.HealBetweenWaves);
            Assert.AreEqual(0.005, t.RegenPerSecond);
            Assert.AreEqual(10.0, t.RegenClearRadius);
            Assert.AreEqual(0.1, t.PlayerHealCap);
            Assert.IsTrue(t.ShrineEnabled, "goals.json has a shrine block");
            Assert.AreEqual(0.15, t.ShrineHeal);
            Assert.AreEqual(45.0, t.ShrineCooldownSeconds);

            // Monsters.
            Assert.AreEqual(1.5, t.RunnerSpeedMult);
            Assert.AreEqual(1.5, t.RunnerAttackSpeedMult);
            Assert.AreEqual(0.6, t.RunnerHealthMult);
            Assert.AreEqual(18.0, t.RangedKeepDistance);
            Assert.AreEqual(3.0, t.CorruptedSelfMult);
            Assert.AreEqual(1.5, t.CorruptedEmpowerMult);
            Assert.AreEqual(12.0, t.CorruptedEmpowerRadius);
            Assert.AreEqual(0x04001381u, t.CorruptedTintPalette);
            Assert.AreEqual(0.6, t.Role(HoldDamageRole.Runner).HealthMult);
            Assert.AreEqual(1.5, t.Role(HoldDamageRole.Runner).AttackSpeedMult);
            Assert.AreEqual(1.5, t.Role(HoldDamageRole.Corrupted).HealthMult);

            // Ward.
            Assert.IsTrue(t.WardEnabled);
            Assert.AreEqual(0.75, t.WardReduction);
            Assert.AreEqual(10.0, t.WardShatterSeconds);
            Assert.AreEqual(WorldEventCombatKinds.Missile | WorldEventCombatKinds.Magic, t.WardReduces);
            Assert.AreEqual(WorldEventCombatKinds.Melee, t.WardBreakOn);

            // Finale.
            Assert.AreEqual(50.0, t.FinaleMinKillSeconds);
            Assert.AreEqual(15.0, t.FinaleSwapSeconds);
            Assert.AreEqual(20.0, t.FinaleSwapMaxSeconds);
            Assert.AreEqual(0.06, t.FinalePullShare);
            Assert.AreEqual(8.0, t.FinalePullWindowSeconds);
            Assert.AreEqual(10.0, t.FinalePullSeconds);
            Assert.AreEqual(ProtectDef.FinaleTargetNpcs, t.FinaleTarget, "bossTargetsProtected true reads as npcs");
        }

        [TestMethod]
        public void Tuning_From_NoPropertySet_RowsWithNoGoalValue_TakeTheirCodeDefaults()
        {
            var goal = DefendEnvoys();
            var t = WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, _ => null), goal, 1);

            Assert.AreEqual(10000L, t.KnightMaxHealth);
            Assert.AreEqual(1.0, t.KnightDamageTakenMult);
            Assert.AreEqual(2.0, t.CapBurstSeconds);
            Assert.AreEqual(2.0, t.HitIntervalSeconds);
            Assert.AreEqual(4.0, t.HitIntervalMaxSeconds);
            Assert.AreEqual(1.5, t.RangedSlack);
            Assert.AreEqual(20.0, t.HealerMemorySeconds);
            Assert.IsTrue(t.PlayerHealEnabled);
            Assert.AreEqual(1.0, t.PlayerHealMult);
            Assert.AreEqual(0.0, t.FinaleEnrageSeconds);
            Assert.AreEqual(1.5, t.FinaleEnrageMult);
            Assert.AreEqual(1.0, t.FinalePlayerDamageMult);
            Assert.AreEqual(ProtectDef.MaxHoldWaves, t.MaxWaves);
            Assert.IsNull(t.ParticipantRadius, "not set: the source's radius");
            Assert.AreEqual(0.0, t.AfkSeconds);
            Assert.IsFalse(t.AnnouncesLocally);

            foreach (HoldDamageRole role in Enum.GetValues(typeof(HoldDamageRole)))
            {
                Assert.AreEqual(1.0, t.Role(role).PlayerDamageMult, role.ToString());
                Assert.AreEqual(1.0, t.Role(role).NpcDamageMult, role.ToString());
                Assert.AreEqual(0, t.Role(role).LevelOffset, role.ToString());
            }

            Assert.AreSame(WorldEventHoldRoleTuning.Identity, t.Role(HoldDamageRole.None));
            Assert.IsNull(t.Role(HoldDamageRole.Melee).HealthMult, "not set: no change");
        }

        [TestMethod]
        public void Tuning_From_UndefendedSeconds_ShrinksPerParticipant_DownToTheFloor()
        {
            var goal = DefendEnvoys();
            var settings = WorldEventHoldSettingsResolver.Resolve(goal, _ => null);

            Assert.AreEqual(135.0, WorldEventHoldTuning.From(settings, goal, 2).UndefendedSeconds, "200 - 65");
            Assert.AreEqual(100.0, WorldEventHoldTuning.From(settings, goal, 3).UndefendedSeconds, "200 - 130 = 70, floored at 100");
            Assert.AreEqual(200.0, WorldEventHoldTuning.From(settings, goal, 0).UndefendedSeconds, "below 1 counts as 1");
        }

        [TestMethod]
        public void Tuning_From_ASetProperty_Wins()
        {
            var goal = DefendEnvoys();
            var props = new Dictionary<string, string>
            {
                [K.PropertyPrefix + K.MinDeathSeconds] = "90",
                [K.PropertyPrefix + K.Role("caster", K.RolePlayerDamageMult)] = "1.25",
                [K.PropertyPrefix + K.ParticipantRadius] = "45",
                [K.PropertyPrefix + K.Broadcast] = "local",
            };

            var t = WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, p => props.TryGetValue(p, out var v) ? v : null), goal, 1);

            Assert.AreEqual(90.0, t.MinDeathSeconds);
            Assert.AreEqual(1.25, t.Role(HoldDamageRole.Caster).PlayerDamageMult);
            Assert.AreEqual(45.0, t.ParticipantRadius);
            Assert.IsTrue(t.AnnouncesLocally);
        }

        [TestMethod]
        public void Tuning_Defaults_HasNoGoalValues()
        {
            var t = WorldEventHoldTuning.Defaults;

            Assert.IsNotNull(t);
            Assert.AreEqual(0.0, t.UndefendedSeconds, "no goal: monster damage unscaled");
            Assert.IsFalse(t.WardEnabled);
            Assert.IsTrue(t.ShrineEnabled, "no goal value: the shrine row's code default, on");
            Assert.AreEqual(1, t.Participants);
            Assert.AreEqual(ProtectDef.MaxHoldWaves, t.MaxWaves);
        }
    }
}
