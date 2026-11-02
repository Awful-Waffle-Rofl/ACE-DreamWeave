using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The group-run contribution summary: the per-member DEALT / TAKEN ledger (ThreadDungeonRun_Contribution.cs), its
    /// feed from DamageHistory.Add (ThreadContributionHook), the pure formatter, and the one-shot send from
    /// ThreadDungeonManager.AnnounceCleared. The DEALT tests drive the real DamageHistory.Add on a real test Creature, so
    /// they prove the call site reaches the hook. Players cannot be constructed in this tree, so attackers are
    /// bare-reflection Players (the WorldEventCombatCreditTests pattern) and the TAKEN tests go through the hook's
    /// player-free half with the run lookup seam swapped. No PropertyManager key is read by anything here.
    /// </summary>
    [TestClass]
    public class ThreadContributionTests
    {
        private static uint nextGuid = 0x50E00000;

        private const uint RunInstance = 0x80007711u;

        // ---- builders ----------------------------------------------------------------------------------

        private static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        private static ThreadDungeonRun NewRun(params WorldObject[] members)
        {
            var seats = members.Select(m => new RosterSeat(m.Guid.Full, m.Name, 7, 150)).ToList();

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Name = "Filos Doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var group = seats.Count > 1 ? GroupOf(seats.Count) : GroupScaling.Solo;

            return new ThreadDungeonRun(RunInstance, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
        }

        private static void SetField(object target, Type declaring, string name, object value)
        {
            var field = declaring.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{declaring.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }

        private static T MakeBare<T>(string name) where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            SetField(wo, typeof(WorldObject), "<Guid>k__BackingField", new ObjectGuid(nextGuid++));
            SetField(wo, typeof(WorldObject), "<Biota>k__BackingField", new ACE.Entity.Models.Biota
            {
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } }
            });
            SetField(wo, typeof(WorldObject), "BiotaDatabaseLock", new ReaderWriterLockSlim());

            return wo;
        }

        private static Player MakePlayer(string name) => MakeBare<Player>(name);

        private static CombatPet MakePet(Player owner)
        {
            var pet = MakeBare<CombatPet>("Test Pet");
            pet.P_PetOwner = owner;
            return pet;
        }

        /// <summary>A real test Creature (60 health: 50 base + endurance 20 / 2) belonging to <paramref name="run"/>.</summary>
        private static Creature RunCreature(ThreadDungeonRun run)
        {
            var creature = TestCreatures.CreateDefender();
            creature.P_DungeonRun = run;
            return creature;
        }

        private static ContributionRow Row(ThreadDungeonRun run, WorldObject member)
            => run.SnapshotContributions().Single(r => r.Guid == member.Guid.Full);

        // ---- the formatter ------------------------------------------------------------------------------

        [TestMethod]
        public void Formatter_matches_the_approved_example_shape()
        {
            var lines = ThreadDungeonRun.FormatContributionSummary(new[] { new ContributionRow(1, "Aldric", 184_220, 41_305) });

            CollectionAssert.AreEqual(new[] { "Thread cleared - contributions:", "  Aldric  dealt 184,220   taken 41,305" }, lines.ToArray());
        }

        [TestMethod]
        public void Formatter_sorts_by_dealt_descending_and_keeps_given_order_on_ties()
        {
            var rows = new[]
            {
                new ContributionRow(1, "Owner", 500, 10),
                new ContributionRow(2, "Bo", 9_000, 0),
                new ContributionRow(3, "Cy", 500, 99),
                new ContributionRow(4, "Di", 0, 0),
            };

            var lines = ThreadDungeonRun.FormatContributionSummary(rows);

            Assert.AreEqual(5, lines.Count);
            Assert.AreEqual(ThreadDungeonRun.ContributionHeader, lines[0]);
            StringAssert.StartsWith(lines[1], "  Bo ");
            StringAssert.StartsWith(lines[2], "  Owner ");
            StringAssert.StartsWith(lines[3], "  Cy ");
            StringAssert.StartsWith(lines[4], "  Di ");
        }

        [TestMethod]
        public void Formatter_pads_one_name_column_and_right_aligns_dealt()
        {
            var lines = ThreadDungeonRun.FormatContributionSummary(new[]
            {
                new ContributionRow(1, "Aldric", 184_220, 41_305),
                new ContributionRow(2, "Bo", 7, 1_000_000),
            });

            Assert.AreEqual("  Aldric  dealt 184,220   taken 41,305", lines[1]);
            Assert.AreEqual("  Bo      dealt       7   taken 1,000,000", lines[2]);
        }

        [TestMethod]
        public void Formatter_gives_an_all_zero_member_a_row()
        {
            var lines = ThreadDungeonRun.FormatContributionSummary(new[] { new ContributionRow(1, "Idle", 0, 0) });

            Assert.AreEqual("  Idle  dealt 0   taken 0", lines[1]);
        }

        [TestMethod]
        public void Formatter_uses_invariant_separators_whatever_the_thread_culture()
        {
            var saved = Thread.CurrentThread.CurrentCulture;

            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var lines = ThreadDungeonRun.FormatContributionSummary(new[] { new ContributionRow(1, "A", 1_234_567, 89_012) });

                Assert.AreEqual("  A  dealt 1,234,567   taken 89,012", lines[1]);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [TestMethod]
        public void Formatter_cuts_long_names_and_every_line_fits_the_chat_limit()
        {
            var longName = new string('N', 300);
            var lines = ThreadDungeonRun.FormatContributionSummary(new[]
            {
                new ContributionRow(1, longName, long.MaxValue, long.MaxValue),
                new ContributionRow(2, null, 0, 0),
            });

            var cut = new string('N', ThreadDungeonRun.ContributionMaxNameChars - 3) + "...";
            StringAssert.StartsWith(lines[1], "  " + cut + "  dealt ");
            StringAssert.StartsWith(lines[2], "  ?");

            foreach (var line in lines)
                Assert.IsTrue(line.Length <= ThreadDungeonRun.ContributionMaxLineChars, $"{line.Length} chars: {line}");
        }

        [TestMethod]
        public void Formatter_returns_nothing_for_no_rows()
        {
            Assert.AreEqual(0, ThreadDungeonRun.FormatContributionSummary(null).Count);
            Assert.AreEqual(0, ThreadDungeonRun.FormatContributionSummary(new ContributionRow[0]).Count);
        }

        // ---- attribution --------------------------------------------------------------------------------

        [TestMethod]
        public void Attribution_player_is_itself_pet_is_its_owner_anything_else_is_nobody()
        {
            var player = MakePlayer("Hitter");
            var pet = MakePet(player);
            var ownerless = MakePet(null);

            Assert.AreEqual(player.Guid.Full, ThreadContributionHook.CreditedPlayerGuid(player));
            Assert.AreEqual(player.Guid.Full, ThreadContributionHook.CreditedPlayerGuid(pet));
            Assert.AreEqual(0u, ThreadContributionHook.CreditedPlayerGuid(ownerless));
            Assert.AreEqual(0u, ThreadContributionHook.CreditedPlayerGuid(TestCreatures.CreateAttacker()));
            Assert.AreEqual(0u, ThreadContributionHook.CreditedPlayerGuid(null));
        }

        // ---- DEALT through the real DamageHistory.Add ---------------------------------------------------

        [TestMethod]
        public void Dealt_player_and_pet_damage_is_credited_to_the_player_through_DamageHistory()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);
            var creature = RunCreature(run);

            creature.DamageHistory.Add(a, DamageType.Slash, 25);
            creature.DamageHistory.Add(MakePet(b), DamageType.Cold, 12);
            creature.DamageHistory.Add(b, DamageType.Fire, 3);

            Assert.AreEqual(25L, Row(run, a).Dealt);
            Assert.AreEqual(15L, Row(run, b).Dealt, "pet damage counts toward the owner");
            Assert.AreEqual(0L, Row(run, a).Taken);
        }

        [TestMethod]
        public void Dealt_ignores_non_roster_players_monsters_and_zero_hits()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);
            var creature = RunCreature(run);

            creature.DamageHistory.Add(MakePlayer("Stranger"), DamageType.Slash, 40);
            creature.DamageHistory.Add(TestCreatures.CreateAttacker(), DamageType.Slash, 40);
            creature.DamageHistory.Add(MakePet(MakePlayer("Stranger's owner")), DamageType.Slash, 40);
            creature.DamageHistory.Add(a, DamageType.Slash, 0);

            Assert.IsTrue(run.SnapshotContributions().All(r => r.Dealt == 0 && r.Taken == 0));
        }

        [TestMethod]
        public void Dealt_does_not_count_a_creature_outside_any_run()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);
            var stray = TestCreatures.CreateDefender();

            stray.DamageHistory.Add(a, DamageType.Slash, 25);

            Assert.AreEqual(0L, Row(run, a).Dealt);
        }

        /// <summary>
        /// The overkill clamp lives at the sinks: every one records the figure UpdateVitalDelta returned, which is floored at the
        /// health the victim had. This drives that exact composition on a real 60-health creature with a 1000-point hit.
        /// A lethal hit through the real Creature.TakeDamage cannot run here: its death check calls Creature.OnDeath, which
        /// walks the killer's class abilities and throws on a bare-reflection Player (no Character record). The non-lethal
        /// real-sink case is Dealt_through_the_real_TakeDamage_counts_the_applied_hit.
        /// </summary>
        [TestMethod]
        public void Dealt_counts_only_the_health_the_target_had_on_an_overkill()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);
            var creature = RunCreature(run);
            var had = creature.Health.Current;
            Assert.IsTrue(had > 0 && had < 1000, $"sanity: test creature health {had}");

            var applied = (uint)-creature.UpdateVitalDelta(creature.Health, -1000);
            creature.DamageHistory.Add(a, DamageType.Slash, applied);

            Assert.AreEqual((long)had, Row(run, a).Dealt);
        }

        // ---- TAKEN --------------------------------------------------------------------------------------

        [TestMethod]
        public void Taken_counts_a_roster_member_hurt_in_the_run_instance_only()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);
            var original = ThreadContributionHook.RunForInstance;
            ThreadContributionHook.RunForInstance = i => i == RunInstance ? run : null;

            try
            {
                ThreadContributionHook.OnPlayerDamaged(a.Guid.Full, RunInstance, 70);
                ThreadContributionHook.OnPlayerDamaged(a.Guid.Full, RunInstance, 5);
                ThreadContributionHook.OnPlayerDamaged(a.Guid.Full, 0x80009999u, 1000);     // another instance
                ThreadContributionHook.OnPlayerDamaged(a.Guid.Full, 0, 1000);               // the open world
                ThreadContributionHook.OnPlayerDamaged(0x50FFFFFFu, RunInstance, 1000);     // not on the roster

                Assert.AreEqual(75L, Row(run, a).Taken);
                Assert.AreEqual(0L, Row(run, b).Taken);
                Assert.AreEqual(0L, Row(run, a).Dealt);
            }
            finally
            {
                ThreadContributionHook.RunForInstance = original;
            }
        }

        [TestMethod]
        public void Production_run_lookup_never_resolves_instance_zero()
        {
            Assert.IsNull(ThreadDungeonManager.GetRegisteredRun(0));
        }

        // ---- the ledger's lifetime ----------------------------------------------------------------------

        [TestMethod]
        public void Ledger_counts_while_Starting_and_Active_and_stops_at_Cleared()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);

            run.RecordDamageDealt(a.Guid.Full, 10);              // Starting
            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);

            run.RecordDamageDealt(a.Guid.Full, 20);
            run.RecordDamageTaken(b.Guid.Full, 7);

            run.RecordKill(isBoss: false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);

            run.RecordDamageDealt(a.Guid.Full, 5000);
            run.RecordDamageTaken(b.Guid.Full, 5000);

            Assert.AreEqual(30L, Row(run, a).Dealt);
            Assert.AreEqual(7L, Row(run, b).Taken);
        }

        [TestMethod]
        public void Ledger_closes_when_a_run_ends_uncleared()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);
            run.MarkPopulated(planned: 3, spawned: 3, bossWcid: 0);

            run.RecordDamageDealt(a.Guid.Full, 10);
            run.MarkEnded(DungeonRunTelemetry.EndReasons.Expired);
            run.RecordDamageDealt(a.Guid.Full, 10);

            Assert.AreEqual(10L, Row(run, a).Dealt);
        }

        [TestMethod]
        public void Ledger_records_nothing_for_a_solo_run()
        {
            var a = MakePlayer("Solo");
            var run = NewRun(a);
            Assert.IsFalse(run.IsGroup);

            run.RecordDamageDealt(a.Guid.Full, 10);
            run.RecordDamageTaken(a.Guid.Full, 10);

            Assert.AreEqual(0L, Row(run, a).Dealt);
            Assert.AreEqual(0L, Row(run, a).Taken);
        }

        [TestMethod]
        public void Snapshot_lists_every_roster_member_in_roster_order_zeros_included()
        {
            var a = MakePlayer("Owner");
            var b = MakePlayer("Bo");
            var c = MakePlayer("Cy");
            var run = NewRun(a, c, b);

            run.RecordDamageDealt(c.Guid.Full, 3);

            var rows = run.SnapshotContributions();
            CollectionAssert.AreEqual(run.Roster.Select(m => m.Guid).ToArray(), rows.Select(r => r.Guid).ToArray());
            Assert.AreEqual(3, rows.Count);
        }

        // ---- the clear-time send ------------------------------------------------------------------------

        [TestMethod]
        public void Group_clear_sends_the_summary_once_and_solo_never()
        {
            var calls = new List<ThreadDungeonRun>();
            var originalSender = ThreadDungeonManager.ContributionSummarySender;
            var originalSurvey = ThreadDungeonManager.SurveyRecorder;
            var originalReward = ThreadDungeonManager.ClearRewardHandler;
            var originalKnots = ThreadDungeonManager.KnotDropRoller;
            ThreadDungeonManager.ContributionSummarySender = calls.Add;
            ThreadDungeonManager.SurveyRecorder = _ => { };
            ThreadDungeonManager.ClearRewardHandler = (_, __) => { };
            ThreadDungeonManager.KnotDropRoller = _ => { };

            try
            {
                var group = NewRun(MakePlayer("Owner"), MakePlayer("Bo"));
                group.MarkPopulated(0, 0, 0);
                ThreadDungeonManager.OnRunPopulated(group);
                ThreadDungeonManager.OnRunPopulated(group);

                var solo = NewRun(MakePlayer("Solo"));
                solo.MarkPopulated(0, 0, 0);
                ThreadDungeonManager.OnRunPopulated(solo);

                Assert.AreEqual(1, calls.Count, "exactly once, group only");
                Assert.AreSame(group, calls[0]);
            }
            finally
            {
                ThreadDungeonManager.ContributionSummarySender = originalSender;
                ThreadDungeonManager.SurveyRecorder = originalSurvey;
                ThreadDungeonManager.ClearRewardHandler = originalReward;
                ThreadDungeonManager.KnotDropRoller = originalKnots;
            }
        }

        [TestMethod]
        public void Production_sender_tolerates_an_all_offline_roster()
        {
            var run = NewRun(MakePlayer("Owner"), MakePlayer("Bo"));
            run.RecordDamageDealt(run.OwnerGuid, 10);

            ThreadDungeonManager.SendContributionSummary(run);   // every guid is offline under the harness
        }

        [TestMethod]
        public void Delivery_sends_every_line_as_Broadcast_to_online_members_and_skips_offline_ones()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var c = MakePlayer("Cy");
            var run = NewRun(a, b, c);
            run.RecordDamageDealt(b.Guid.Full, 900);
            run.RecordDamageTaken(a.Guid.Full, 12);

            var received = new Dictionary<uint, List<(string Text, ChatMessageType Type)>>
            {
                [a.Guid.Full] = new List<(string, ChatMessageType)>(),
                [b.Guid.Full] = new List<(string, ChatMessageType)>(),
            };

            // c is offline: the lookup answers null for it
            var sent = ThreadDungeonManager.DeliverContributionSummary(run,
                guid => received.TryGetValue(guid, out var inbox) ? (text, type) => inbox.Add((text, type)) : null);

            var expected = ThreadDungeonRun.FormatContributionSummary(run.SnapshotContributions());

            Assert.AreEqual(2, sent);
            Assert.AreEqual(4, expected.Count, "header plus one row per roster member, the offline one included");

            foreach (var inbox in received.Values)
            {
                CollectionAssert.AreEqual(expected.ToArray(), inbox.Select(m => m.Text).ToArray());
                Assert.IsTrue(inbox.All(m => m.Type == ChatMessageType.Broadcast));
            }

            StringAssert.StartsWith(expected[1], "  Bo ", "sorted by dealt");
        }

        [TestMethod]
        public void A_throwing_summary_sender_does_not_skip_the_clear_bookkeeping()
        {
            var surveys = 0;
            var originalSender = ThreadDungeonManager.ContributionSummarySender;
            var originalSurvey = ThreadDungeonManager.SurveyRecorder;
            var originalReward = ThreadDungeonManager.ClearRewardHandler;
            var originalKnots = ThreadDungeonManager.KnotDropRoller;
            ThreadDungeonManager.ContributionSummarySender = _ => throw new InvalidOperationException("boom");
            ThreadDungeonManager.SurveyRecorder = _ => surveys++;
            ThreadDungeonManager.ClearRewardHandler = (_, __) => { };
            ThreadDungeonManager.KnotDropRoller = _ => { };

            try
            {
                var group = NewRun(MakePlayer("Owner"), MakePlayer("Bo"));
                group.MarkPopulated(0, 0, 0);
                ThreadDungeonManager.OnRunPopulated(group);

                Assert.AreEqual(1, surveys, "the survey still records after the summary threw");
            }
            finally
            {
                ThreadDungeonManager.ContributionSummarySender = originalSender;
                ThreadDungeonManager.SurveyRecorder = originalSurvey;
                ThreadDungeonManager.ClearRewardHandler = originalReward;
                ThreadDungeonManager.KnotDropRoller = originalKnots;
            }
        }

        // ---- TAKEN through the real Player branch of the hook ---------------------------------------------

        /// <summary>A bare Player standing on a bare landblock of <paramref name="instance"/>: the two fields the TAKEN branch reads.</summary>
        private static Player PlayerInInstance(string name, uint instance)
        {
            var player = MakePlayer(name);
            var landblock = (ACE.Server.Entity.Landblock)RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.Entity.Landblock));
            SetField(landblock, typeof(ACE.Server.Entity.Landblock), "<Instance>k__BackingField", instance);
            SetField(player, typeof(WorldObject), "<CurrentLandblock>k__BackingField", landblock);
            return player;
        }

        [TestMethod]
        public void Taken_reaches_the_ledger_through_the_hooks_player_branch()
        {
            var a = PlayerInInstance("Aldric", RunInstance);
            var b = PlayerInInstance("Bo", 0);   // the open world
            var run = NewRun(a, b);
            var shielded = PlayerInInstance("Shielded", RunInstance);
            var original = ThreadContributionHook.RunForInstance;
            ThreadContributionHook.RunForInstance = i => i == RunInstance ? run : null;

            try
            {
                var monster = TestCreatures.CreateAttacker();

                // the line DamageHistory.Add runs for a player victim; a bare Player cannot run the rest of Add
                ThreadContributionHook.OnDamageRecorded(a, monster, 40);
                ThreadContributionHook.OnDamageRecorded(b, monster, 40);

                Assert.AreEqual(40L, Row(run, a).Taken);
                Assert.AreEqual(0L, Row(run, b).Taken, "outside the run's instance");

                // an invincible member's recorded DoT credit is never taken (Player.TakeDamageOverTime discards it)
                var guarded = NewRun(shielded, MakePlayer("Other"));
                ThreadContributionHook.RunForInstance = i => i == RunInstance ? guarded : null;
                shielded.Invincible = true;
                ThreadContributionHook.OnDamageRecorded(shielded, monster, 40);
                Assert.AreEqual(0L, Row(guarded, shielded).Taken);
            }
            finally
            {
                ThreadContributionHook.RunForInstance = original;
            }
        }

        // ---- DEALT exactness on the DoT path with a ward --------------------------------------------------

        /// <summary>A ward that eats half of every incoming hit.</summary>
        private sealed class HalfWard : ACE.Server.MonsterEffects.IMonsterIncomingDamage
        {
            public string Kind => "testhalfward";

            public bool Validate(ACE.Server.MonsterEffects.MonsterEffectSpec spec, out string error)
            {
                error = null;
                return true;
            }

            public uint OnIncomingDamage(Creature defender, WorldObject source, DamageType damageType, uint amount,
                ACE.Server.MonsterEffects.IncomingDamageOrigin origin, ACE.Server.MonsterEffects.MonsterEffectSpec spec,
                ref ACE.Server.MonsterEffects.MonsterEffectState state) => amount / 2;
        }

        /// <summary>Seeds monster_effects_enabled on (the ward's dispatch switch), runs the body, restores the key.</summary>
        private static void WithMonsterEffectsEnabled(Action body)
        {
            ACE.Server.Managers.DefaultPropertyManager.LoadDefaultProperties();
            var saved = ACE.Server.Managers.PropertyManager.GetBool("monster_effects_enabled").Item;

            try
            {
                Assert.IsTrue(ACE.Server.Managers.PropertyManager.ModifyBool("monster_effects_enabled", true));
                body();
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyBool("monster_effects_enabled", saved);
            }
        }

        private static Creature WardedRunCreature(ThreadDungeonRun run, uint maxHealth)
        {
            var ward = new HalfWard();
            var creature = TestCreatures.CreateDefender(maxHealth: maxHealth);
            creature.AttachMonsterEffectsForTest(ACE.Server.MonsterEffects.MonsterEffectSet.BuildFromHandlers(
                (ward, new ACE.Server.MonsterEffects.MonsterEffectSpec(ward.Kind, new Dictionary<string, string>()))));
            creature.P_DungeonRun = run;
            return creature;
        }

        /// <summary>
        /// The exact sequence EnchantmentManager.ApplyDamageTick runs from its credit step onward (it cannot be driven here: it
        /// resolves its damagers through the live landblock). Its order is pinned by ApplyDamageTick_stages_the_tick_around_the_write.
        /// </summary>
        private static void ApplyTickLikeApplyDamageTick(Creature creature, IReadOnlyList<WorldObject> damagers, IReadOnlyList<float> raw, bool stage)
        {
            var total = ACE.Server.Entity.DotTickCredits.Total(raw);
            var applied = (uint)Math.Round(Math.Max(0.0f, Math.Min(total, creature.Health.Current)));
            var credits = ACE.Server.Entity.DotTickCredits.Split(raw, applied);

            if (stage)
                ThreadContributionHook.BeginDotTick(creature, damagers, raw);

            try
            {
                for (var i = 0; i < damagers.Count; i++)
                    creature.DamageHistory.Add(damagers[i], DamageType.Fire, credits[i]);

                creature.TakeDamageOverTime(total, DamageType.Fire);
            }
            finally
            {
                ThreadContributionHook.EndDotTick(creature);
            }
        }

        [TestMethod]
        public void Dealt_on_a_warded_run_creatures_DoT_tick_equals_the_health_it_lost()
        {
            WithMonsterEffectsEnabled(() =>
            {
                var a = MakePlayer("Aldric");
                var b = MakePlayer("Bo");
                var run = NewRun(a, b);
                var creature = WardedRunCreature(run, 500);
                var before = creature.Health.Current;

                ApplyTickLikeApplyDamageTick(creature, new WorldObject[] { a, MakePet(b) }, new[] { 30f, 10f }, stage: true);

                var lost = before - creature.Health.Current;
                Assert.AreEqual(20u, lost, "sanity: the half ward ate 20 of the 40-point tick");
                Assert.AreEqual((long)lost, Row(run, a).Dealt + Row(run, b).Dealt, "dealt is what the creature lost, not the pre-ward credit");
                Assert.AreEqual(15L, Row(run, a).Dealt, "split by raw share: 30 of 40");
                Assert.AreEqual(5L, Row(run, b).Dealt, "the pet's share goes to its owner");
                Assert.IsNull(creature.P_PendingThreadDotTick, "the staged tick is consumed");

                // control: the same tick WITHOUT staging is credited from DamageHistory at the pre-ward figure
                var control = NewRun(MakePlayer("Ctl"), MakePlayer("Ctl2"));
                var controlCreature = WardedRunCreature(control, 500);
                var ctlPlayer = MakePlayerFor(control, 0);
                ApplyTickLikeApplyDamageTick(controlCreature, new WorldObject[] { ctlPlayer }, new[] { 40f }, stage: false);
                Assert.AreEqual(40L, Row(control, ctlPlayer).Dealt, "control: unstaged credits over-count, which is the bug being fixed");
            });
        }

        /// <summary>A bare Player whose guid is the run's roster seat <paramref name="index"/>.</summary>
        private static Player MakePlayerFor(ThreadDungeonRun run, int index)
        {
            var player = MakePlayer(run.Roster[index].Name);
            SetField(player, typeof(WorldObject), "<Guid>k__BackingField", new ObjectGuid(run.Roster[index].Guid));
            return player;
        }

        [TestMethod]
        public void Staging_is_a_no_op_outside_a_run_and_for_a_player_victim()
        {
            var stray = TestCreatures.CreateDefender();
            Assert.IsFalse(ThreadContributionHook.BeginDotTick(stray, new WorldObject[] { MakePlayer("A") }, new[] { 1f }));
            Assert.IsNull(stray.P_PendingThreadDotTick);

            var victim = MakePlayer("Victim");
            Assert.IsFalse(ThreadContributionHook.BeginDotTick(victim, new WorldObject[] { TestCreatures.CreateAttacker() }, new[] { 1f }));
        }

        [TestMethod]
        public void Staging_is_a_no_op_for_a_solo_run_and_a_closed_ledger()
        {
            var solo = NewRun(MakePlayer("Solo"));
            var soloCreature = RunCreature(solo);
            Assert.IsFalse(solo.IsContributionOpen);
            Assert.IsFalse(ThreadContributionHook.BeginDotTick(soloCreature, new WorldObject[] { MakePlayer("A") }, new[] { 1f }));
            Assert.IsNull(soloCreature.P_PendingThreadDotTick);

            var group = NewRun(MakePlayer("Owner"), MakePlayer("Bo"));
            Assert.IsTrue(group.IsContributionOpen, "control: an open group ledger stages");
            group.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, group.State);
            Assert.IsFalse(group.IsContributionOpen);

            var leftover = RunCreature(group);
            Assert.IsFalse(ThreadContributionHook.BeginDotTick(leftover, new WorldObject[] { MakePlayer("A") }, new[] { 1f }));
            Assert.IsNull(leftover.P_PendingThreadDotTick);
        }

        [TestMethod]
        public void A_staged_tick_only_suppresses_credits_on_its_own_thread()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);
            var creature = RunCreature(run);

            Assert.IsTrue(ThreadContributionHook.BeginDotTick(creature, new WorldObject[] { a }, new[] { 10f }));

            try
            {
                // the staging thread: this is one of the tick's own pre-write credits, ignored
                ThreadContributionHook.OnDamageRecorded(creature, a, 10);
                Assert.AreEqual(0L, Row(run, a).Dealt);

                // another thread meanwhile: an ordinary hit, credited, and it cannot consume or clear the staged tick
                var other = new Thread(() =>
                {
                    ThreadContributionHook.OnDamageRecorded(creature, b, 7);
                    ThreadContributionHook.OnDotTickApplied(creature, 99);
                    ThreadContributionHook.EndDotTick(creature);
                });
                other.Start();
                Assert.IsTrue(other.Join(TimeSpan.FromSeconds(10)), "the second thread did not finish");

                Assert.AreEqual(7L, Row(run, b).Dealt, "a cross-thread hit lands");
                Assert.AreEqual(0L, Row(run, a).Dealt, "the other thread did not apply the staged tick");
                Assert.IsNotNull(creature.P_PendingThreadDotTick, "nor clear it");

                // back on the staging thread the tick applies normally
                ThreadContributionHook.OnDotTickApplied(creature, 4);
                Assert.AreEqual(4L, Row(run, a).Dealt);
                Assert.IsNull(creature.P_PendingThreadDotTick);
            }
            finally
            {
                ThreadContributionHook.EndDotTick(creature);
            }
        }

        // ---- overkill through the real sink ---------------------------------------------------------------

        [TestMethod]
        public void Dealt_through_the_real_TakeDamage_counts_the_applied_hit()
        {
            var a = MakePlayer("Aldric");
            var b = MakePlayer("Bo");
            var run = NewRun(a, b);
            var creature = RunCreature(run);
            var before = creature.Health.Current;

            var applied = creature.TakeDamage(a, DamageType.Slash, 25f, false, ACE.Server.MonsterEffects.IncomingDamageOrigin.DirectHit);

            Assert.AreEqual(25u, applied);
            Assert.AreEqual((long)(before - creature.Health.Current), Row(run, a).Dealt);
        }

        // ---- source pins: the two call sites ------------------------------------------------------------

        private static string RepoText(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "property-registry.tsv")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/property-registry.tsv by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(new[] { dir.FullName, "Source", "ACE.Server" }.Concat(relative).ToArray())).Replace("\r\n", "\n");
        }

        private const string HistoryHook = "ACE.Server.ThreadDungeons.ThreadContributionHook.OnDamageRecorded(Creature, attacker, amount);";

        /// <summary>Null when DamageHistory.Add calls the hook once, after the zero return and after the hit is logged.</summary>
        private static string HistoryHookFault(string body)
        {
            var zero = body.IndexOf("if (amount == 0)", StringComparison.Ordinal);
            var logged = body.IndexOf("Log.Add(entry);", StringComparison.Ordinal);
            var hook = body.IndexOf(HistoryHook, StringComparison.Ordinal);

            if (zero < 0 || logged < 0)
                return "the pin's anchors were not found (the pin needs updating)";
            if (hook < 0)
                return "DamageHistory.Add does not call the contribution hook with the recorded amount";
            if (hook < zero || hook < logged)
                return "the hook runs before the zero return or before the hit is logged";
            if (body.IndexOf(HistoryHook, hook + 1, StringComparison.Ordinal) >= 0)
                return "the hook runs twice";

            return null;
        }

        [TestMethod]
        public void DamageHistoryAdd_calls_the_contribution_hook_once_after_logging()
        {
            var text = RepoText("Entity", "DamageHistory.cs");
            const string signature = "public void Add(WorldObject attacker, DamageType damageType, uint amount)";
            var start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{signature} not found");
            var end = text.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            var body = text.Substring(start, end - start);

            Assert.IsNull(HistoryHookFault(body));
            Assert.IsNotNull(HistoryHookFault(body.Replace(HistoryHook, "")), "removing the hook must fail the check");
            Assert.IsNotNull(HistoryHookFault(body.Replace(HistoryHook, HistoryHook + "\n" + HistoryHook)), "a second call must fail the check");
        }

        private static string Between(string text, string startMarker, string endMarker)
        {
            var start = text.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{startMarker} not found");
            var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            Assert.IsTrue(end > start, $"{endMarker} not found after {startMarker}");
            return text.Substring(start, end - start);
        }

        /// <summary>Null when each perimeter write reports its applied loss after the write and before its death or return.</summary>
        internal static string PerimeterFault(string text)
        {
            var write = Between(text, "public void WriteDamage(long amount, bool lethal)", "public void SetHealthToOne()");
            var w = write.IndexOf("var applied = -player.UpdateVitalDelta(player.Health,", StringComparison.Ordinal);
            var wr = write.IndexOf("run.RecordDamageTaken(player.Guid.Full, (uint)applied);", StringComparison.Ordinal);
            var wd = write.IndexOf("player.Die();", StringComparison.Ordinal);
            if (w < 0 || wr < w || wd < wr) return "WriteDamage does not record its applied loss between the write and the death";

            var one = Between(text, "public void SetHealthToOne()", "public void TeleportToDrop()");
            var o = one.IndexOf("var lost = -player.UpdateVital(player.Health, 1);", StringComparison.Ordinal);
            var oneRecord = one.IndexOf("run.RecordDamageTaken(player.Guid.Full, (uint)lost);", StringComparison.Ordinal);
            if (o < 0 || oneRecord < o) return "SetHealthToOne does not record the health it removed";

            return null;
        }

        [TestMethod]
        public void OutdoorPerimeter_writes_report_their_applied_loss()
        {
            var text = RepoText("ThreadDungeons", "OutdoorPerimeterLive.cs");

            Assert.IsNull(PerimeterFault(text));
            Assert.IsNotNull(PerimeterFault(text.Replace("run.RecordDamageTaken(player.Guid.Full, (uint)applied);", "")), "dropping the WriteDamage record must fail");
            Assert.IsNotNull(PerimeterFault(text.Replace("run.RecordDamageTaken(player.Guid.Full, (uint)lost);", "")), "dropping the SetHealthToOne record must fail");
        }

        /// <summary>Null when ApplyDamageTick stages the tick before its DamageHistory credits and clears it in a finally after the write.</summary>
        internal static string DotStagingFault(string enchantments)
        {
            var tick = Between(enchantments, "public void ApplyDamageTick(List<PropertiesEnchantmentRegistry> enchantments, DamageType damageType, bool aetheria = false)",
                "float amount = credits[i];");
            var begin = tick.IndexOf("ThreadContributionHook.BeginDotTick(creature,", StringComparison.Ordinal);
            var add = tick.IndexOf("creature.DamageHistory.Add(entries[i].Key, damageType, credits[i]);", StringComparison.Ordinal);
            var take = tick.IndexOf("creature.TakeDamageOverTime(damageTotal, damageType);", StringComparison.Ordinal);
            var fin = tick.IndexOf("finally", take < 0 ? 0 : take, StringComparison.Ordinal);
            var end = tick.IndexOf("ThreadContributionHook.EndDotTick(creature);", StringComparison.Ordinal);

            if (begin < 0 || add < 0 || begin > add) return "the tick is not staged before its DamageHistory credits";
            if (take < 0 || fin < 0 || end < fin) return "the staged tick is not cleared in a finally after the write";
            return null;
        }

        [TestMethod]
        public void ApplyDamageTick_stages_the_tick_around_the_write()
        {
            var text = RepoText("WorldObjects", "Managers", "EnchantmentManager.cs");

            Assert.IsNull(DotStagingFault(text));
            Assert.IsNotNull(DotStagingFault(text.Replace("ThreadContributionHook.BeginDotTick(creature,", "ThreadContributionHook.Nothing(creature,")), "no staging must fail");
            Assert.IsNotNull(DotStagingFault(text.Replace("ThreadContributionHook.EndDotTick(creature);", "")), "no clear must fail");
        }

        [TestMethod]
        public void TakeDamage_credits_a_staged_tick_after_the_write_and_before_the_death_check()
        {
            var text = RepoText("WorldObjects", "Monster_Combat.cs");
            var body = Between(text, "public virtual uint TakeDamage(WorldObject source, DamageType damageType, float amount,", "public void EmitSplatter(");

            var ward = body.IndexOf("AbsorbMonsterEffectDamage(", StringComparison.Ordinal);
            var write = body.IndexOf("var damage = -UpdateVitalDelta(Health, -tryDamage);", StringComparison.Ordinal);
            var credit = body.IndexOf("ThreadContributionHook.OnDotTickApplied(this, damage);", StringComparison.Ordinal);
            var death = body.IndexOf("if (Health.Current <= 0)", StringComparison.Ordinal);

            Assert.IsTrue(ward >= 0 && write > ward && credit > write && death > credit, $"ward={ward} write={write} credit={credit} death={death}");
        }

        [TestMethod]
        public void AnnounceCleared_sends_the_summary_after_the_group_clear_lines()
        {
            var text = RepoText("ThreadDungeons", "ThreadDungeonManager.cs");
            var method = text.IndexOf("private static void AnnounceCleared(ThreadDungeonRun run)", StringComparison.Ordinal);
            Assert.IsTrue(method >= 0, "AnnounceCleared not found");

            var groupLoopReward = text.IndexOf("ClearRewardHandler(run, member);", method, StringComparison.Ordinal);
            var send = text.IndexOf("ContributionSummarySender(run);", method, StringComparison.Ordinal);
            var soloBranch = text.IndexOf("SoloClearedMessage(run.Dungeon.Name)", method, StringComparison.Ordinal);

            Assert.IsTrue(groupLoopReward > method && send > groupLoopReward, "the summary goes after the per-member clear lines");
            Assert.IsTrue(soloBranch > send, "and inside the group branch, before the solo branch");
        }
    }
}
