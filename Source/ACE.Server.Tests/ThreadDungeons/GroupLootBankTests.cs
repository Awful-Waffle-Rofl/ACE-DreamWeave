using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

using TreasureDeath = ACE.Database.Models.World.TreasureDeath;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Group Threads Task 7: loot bank roll counts (ruling R17), the group rare recipient (R18), the per-member boss
    /// bonus scaling (R14) and the per-member cache owner stamp. Solo must stay exactly as it was, with no new random
    /// draw, so every group branch is proven both ways: the seams throw on a solo run and are consumed on a group run.
    /// No PropertyManager key is read by any driven path here (GroupScaling.Compute takes every tunable as an
    /// argument, and BankKill's rare roll goes through the HeldRareRoller seam).
    /// </summary>
    [TestClass]
    public class GroupLootBankTests
    {
        private const uint Owner = 0x50000010u;
        private const uint Member = 0x50000020u;
        private const uint Member2 = 0x50000030u;

        private static Func<double> NeverDraw => () => throw new AssertFailedException("the uniform source must not be called");

        [TestCleanup]
        public void ResetSeams()
        {
            ThreadLootPool.UniformSource = null;
            ThreadLootPool.IndexSource = null;
            ThreadLootPool.RareCandidates = null;
            ThreadLootPool.HeldRareRoller = null;
            ThreadCacheFiller.DeathTreasureRoller = null;
            ThreadCacheFiller.SalvageAffinityRoller = null;
        }

        // ------------------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------------------

        private static WorldObject Named(uint guid, string name)
            => new GenericObject(new Weenie
            {
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            }, new ObjectGuid(guid));

        private static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        /// <summary>A pooled group run of Owner plus the given members, at the default tunables.</summary>
        private static ThreadDungeonRun PooledGroupRun(params uint[] others)
        {
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150) };
            seats.AddRange(others.Select(g => new RosterSeat(g, $"P{g:X}", 8, 150)));

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(0x80005678u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));
            run.MarkPooledLoot(true);
            return run;
        }

        private static Creature RunCreature(ThreadDungeonRun run, DungeonRole role, int level = 100)
        {
            // The Creature constructor reads the vital formulas (Creature.SetEphemeralValues -> GameTables).
            TestGameTables.EnsureInitialized();

            var creature = new Creature(new Weenie { WeenieClassId = 42, WeenieType = WeenieType.Creature }, new ObjectGuid(NextGuid()));
            creature.P_DungeonRun = run;
            creature.DungeonRole = role;
            creature.Level = level;
            creature.DeathTreasureOverride = new TreasureDeath { Tier = 6 };
            return creature;
        }

        // ------------------------------------------------------------------------------------------------------
        // RollCount (R17)
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void RollCount_at_or_below_one_is_one_roll_and_never_draws()
        {
            Assert.AreEqual(1, ThreadLootPool.RollCount(1.0, NeverDraw));
            Assert.AreEqual(1, ThreadLootPool.RollCount(0.5, NeverDraw));
            Assert.AreEqual(1, ThreadLootPool.RollCount(0.0, NeverDraw));
            Assert.AreEqual(1, ThreadLootPool.RollCount(-3.0, NeverDraw));
            Assert.AreEqual(1, ThreadLootPool.RollCount(double.NaN, NeverDraw));
            Assert.AreEqual(1, ThreadLootPool.RollCount(double.NegativeInfinity, NeverDraw));
        }

        [TestMethod]
        public void RollCount_fraction_adds_one_roll_when_the_draw_is_below_it()
        {
            var calls = 0;

            Assert.AreEqual(2, ThreadLootPool.RollCount(1.33, () => { calls++; return 0.2; }), "0.2 < 0.33 -> the extra roll");
            Assert.AreEqual(1, calls);

            Assert.AreEqual(1, ThreadLootPool.RollCount(1.33, () => { calls++; return 0.5; }), "0.5 >= 0.33 -> no extra roll");
            Assert.AreEqual(2, calls, "exactly one draw per fractional factor");

            Assert.AreEqual(3, ThreadLootPool.RollCount(2.5, () => 0.0));
            Assert.AreEqual(2, ThreadLootPool.RollCount(2.5, () => 0.5), "a draw equal to the fraction is not below it");
        }

        [TestMethod]
        public void RollCount_whole_factor_is_exact_and_never_draws()
        {
            Assert.AreEqual(2, ThreadLootPool.RollCount(2.0, NeverDraw));
            Assert.AreEqual(6, ThreadLootPool.RollCount(6.0, NeverDraw));
            Assert.AreEqual(2, ThreadLootPool.RollCount(2.0 + 1e-12, NeverDraw), "float noise above an integer is not a fraction");
        }

        [TestMethod]
        public void RollCount_is_capped_without_a_draw()
        {
            Assert.AreEqual(ThreadLootPool.MaxRollsPerKill, ThreadLootPool.RollCount(double.PositiveInfinity, NeverDraw));
            Assert.AreEqual(ThreadLootPool.MaxRollsPerKill, ThreadLootPool.RollCount(250.5, NeverDraw));
            Assert.AreEqual(ThreadLootPool.MaxRollsPerKill, ThreadLootPool.RollCount(ThreadLootPool.MaxRollsPerKill, NeverDraw));
            Assert.AreEqual(ThreadLootPool.MaxRollsPerKill, ThreadLootPool.RollCount(99.5, () => 0.0), "the extra roll cannot pass the cap");
        }

        // ------------------------------------------------------------------------------------------------------
        // Ledger entry (R17, R18)
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Legacy_constructor_and_FromKill_are_one_roll_and_no_recipient()
        {
            var entry = new ThreadLootLedgerEntry(false, false, null, null, Item(), "Tester");
            Assert.AreEqual(1, entry.Rolls);
            Assert.AreEqual(0u, entry.RareRecipientGuid);

            var killer = new DamageHistoryInfo(Named(Owner, "+Owner"));
            var rolledFor = new List<DamageHistoryInfo>();
            var rare = Item(40001);

            var fromKill = ThreadLootLedgerEntry.FromKill(false, killer, true, null, null, k => { rolledFor.Add(k); return rare; });
            Assert.AreEqual(1, fromKill.Rolls);
            Assert.AreEqual(0u, fromKill.RareRecipientGuid);
            Assert.AreSame(killer, rolledFor.Single());
            Assert.AreEqual("Owner", fromKill.HeldRareFinderName);
        }

        [TestMethod]
        public void Rolls_below_one_read_as_one()
        {
            Assert.AreEqual(1, new ThreadLootLedgerEntry(false, false, null, null, null, null, 0, 0).Rolls);
            Assert.AreEqual(1, new ThreadLootLedgerEntry(false, false, null, null, null, null, -4, 0).Rolls);
            Assert.AreEqual(3, new ThreadLootLedgerEntry(false, false, null, null, null, null, 3, 0).Rolls);
        }

        [TestMethod]
        public void FromKill_with_rareFor_rolls_for_the_recipient_and_names_them()
        {
            var killer = new DamageHistoryInfo(Named(Owner, "+Owner"));
            var recipient = new DamageHistoryInfo(Named(Member, "+Member"));
            var rolledFor = new List<DamageHistoryInfo>();
            var rare = Item(40002);

            var entry = ThreadLootLedgerEntry.FromKill(true, killer, true, null, null, k => { rolledFor.Add(k); return rare; }, 3, recipient, Member);

            Assert.AreSame(recipient, rolledFor.Single(), "the roll (and so the rare timer and luck) is the recipient's");
            Assert.AreSame(rare, entry.HeldRare);
            Assert.AreEqual("Member", entry.HeldRareFinderName, "finder name follows the recipient, '+' trimmed");
            Assert.AreEqual(Member, entry.RareRecipientGuid);
            Assert.AreEqual(3, entry.Rolls);
            Assert.IsTrue(entry.IsBoss);
            Assert.IsFalse(entry.KillerIsOlthoiPlayer);
        }

        [TestMethod]
        public void FromKill_eligibility_stays_with_the_killer()
        {
            var recipient = new DamageHistoryInfo(Named(Member, "Member"));
            var rolls = 0;
            Func<DamageHistoryInfo, WorldObject> roller = _ => { rolls++; return Item(); };

            var ineligible = ThreadLootLedgerEntry.FromKill(false, new DamageHistoryInfo(Named(Owner, "Owner")), false, null, null, roller, 2, recipient, Member);
            Assert.AreEqual(0, rolls, "an ineligible kill rolls for nobody, recipient or not");
            Assert.IsNull(ineligible.HeldRare);
            Assert.AreEqual(0u, ineligible.RareRecipientGuid, "no rare, no recipient");
            Assert.AreEqual(2, ineligible.Rolls, "the loot roll count is independent of the rare");

            var noKiller = ThreadLootLedgerEntry.FromKill(false, null, true, null, null, roller, 2, recipient, Member);
            Assert.AreEqual(0, rolls, "no killer, no rare roll");
            Assert.AreEqual(0u, noKiller.RareRecipientGuid);
        }

        [TestMethod]
        public void FromKill_that_rolls_no_rare_stores_no_recipient()
        {
            var entry = ThreadLootLedgerEntry.FromKill(false, new DamageHistoryInfo(Named(Owner, "Owner")), true, null, null, _ => null,
                1, new DamageHistoryInfo(Named(Member, "Member")), Member);

            Assert.IsNull(entry.HeldRare);
            Assert.AreEqual(0u, entry.RareRecipientGuid);
            Assert.IsNull(entry.HeldRareFinderName);
        }

        // ------------------------------------------------------------------------------------------------------
        // Rare recipient (R18)
        // ------------------------------------------------------------------------------------------------------

        private static Func<IReadOnlyList<WorldObject>> NeverCandidates => () => throw new AssertFailedException("candidates must not be read");
        private static Func<int, int, int> NeverIndex => (_, _) => throw new AssertFailedException("the index must not be drawn");

        [TestMethod]
        public void ResolveRareFor_reads_nothing_for_an_ineligible_or_missing_killer()
        {
            var killer = new DamageHistoryInfo(Named(Owner, "Owner"));

            Assert.IsNull(ThreadLootPool.ResolveRareFor(false, killer, NeverCandidates, NeverIndex, out var g1));
            Assert.AreEqual(0u, g1);

            Assert.IsNull(ThreadLootPool.ResolveRareFor(true, null, NeverCandidates, NeverIndex, out var g2));
            Assert.AreEqual(0u, g2);
        }

        [TestMethod]
        public void ResolveRareFor_with_nobody_inside_keeps_the_killer_roll()
        {
            var killer = new DamageHistoryInfo(Named(Owner, "Owner"));

            Assert.IsNull(ThreadLootPool.ResolveRareFor(true, killer, () => new List<WorldObject>(), NeverIndex, out var g1));
            Assert.AreEqual(0u, g1);

            Assert.IsNull(ThreadLootPool.ResolveRareFor(true, killer, () => null, NeverIndex, out var g2));
            Assert.AreEqual(0u, g2);
        }

        [TestMethod]
        public void ResolveRareFor_single_candidate_is_taken_without_a_draw()
        {
            var member = Named(Member, "Member");

            var rareFor = ThreadLootPool.ResolveRareFor(true, new DamageHistoryInfo(Named(Owner, "Owner")), () => new List<WorldObject> { member }, NeverIndex, out var guid);

            Assert.AreEqual(Member, guid);
            Assert.AreEqual(Member, rareFor.Guid.Full);
            Assert.AreEqual("Member", rareFor.Name);
        }

        [TestMethod]
        public void ResolveRareFor_picks_uniformly_over_the_inclusive_index_range()
        {
            var candidates = new List<WorldObject> { Named(Owner, "Owner"), Named(Member, "Member"), Named(Member2, "Member2") };
            var bounds = new List<(int, int)>();

            var rareFor = ThreadLootPool.ResolveRareFor(true, new DamageHistoryInfo(candidates[0]), () => candidates,
                (lo, hi) => { bounds.Add((lo, hi)); return 2; }, out var guid);

            CollectionAssert.AreEqual(new[] { (0, 2) }, bounds, "one inclusive draw over every candidate");
            Assert.AreEqual(Member2, guid);
            Assert.AreEqual(Member2, rareFor.Guid.Full);

            ThreadLootPool.ResolveRareFor(true, new DamageHistoryInfo(candidates[0]), () => candidates, (_, _) => 9, out var clamped);
            Assert.AreEqual(Member2, clamped, "an out-of-range index clamps rather than throwing on the landblock thread");
        }

        // ------------------------------------------------------------------------------------------------------
        // BankKill: solo makes no new draw, group rolls and picks
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void BankKill_on_a_solo_run_banks_one_roll_with_no_draw_and_no_presence_lookup()
        {
            ThreadLootPool.UniformSource = NeverDraw;
            ThreadLootPool.IndexSource = NeverIndex;
            ThreadLootPool.RareCandidates = _ => throw new AssertFailedException("a solo run must not look up presence");

            var rare = Item(40010);
            var rolledFor = new List<DamageHistoryInfo>();
            ThreadLootPool.HeldRareRoller = (_, k) => { rolledFor.Add(k); return rare; };

            foreach (var role in new[] { DungeonRole.Boss, DungeonRole.Elite, DungeonRole.Trash })
            {
                var run = PooledRun();
                Assert.IsFalse(run.IsGroup);

                var creature = RunCreature(run, role);
                var killer = new DamageHistoryInfo(Named(Owner, "+Owner"));

                Assert.IsTrue(ThreadLootPool.BankKill(creature, killer));
                Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));
                Assert.AreEqual(1, entry.Rolls, role.ToString());
                Assert.AreEqual(0u, entry.RareRecipientGuid, role.ToString());
                Assert.AreSame(killer, rolledFor.Last(), "the solo rare is the killer's own roll");
                Assert.AreEqual("Owner", entry.HeldRareFinderName);
            }
        }

        [TestMethod]
        public void BankKill_on_a_group_boss_rolls_E_times_B()
        {
            var run = PooledGroupRun(Member);
            Assert.AreEqual(2.0 * 1.05, run.BossLootFactor, 1e-12, "N = 2 at defaults: E 2, B 1.05");

            var draws = 0;
            ThreadLootPool.UniformSource = () => { draws++; return 0.05; };
            ThreadLootPool.RareCandidates = _ => throw new AssertFailedException("no killer means no rare and no presence lookup");

            Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run, DungeonRole.Boss), killer: null));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));

            Assert.AreEqual(1, draws);
            Assert.AreEqual(3, entry.Rolls, "floor(2.1) = 2, plus one because 0.05 < 0.1");
            Assert.AreEqual(0u, entry.RareRecipientGuid);
        }

        [TestMethod]
        public void BankKill_on_a_group_trash_kill_rolls_H_times_B()
        {
            var run = PooledGroupRun(Member);
            run.MarkGroupResolved(1.5, 4.0 / 3.0);
            Assert.AreEqual(4.0 / 3.0 * 1.05, run.TrashLootFactor, 1e-12);

            var draws = 0;
            ThreadLootPool.UniformSource = () => { draws++; return 0.9; };

            Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run, DungeonRole.Trash), killer: null));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));

            Assert.AreEqual(1, draws);
            Assert.AreEqual(1, entry.Rolls, "floor(1.4) = 1, and 0.9 is not below 0.4");
        }

        [TestMethod]
        public void BankKill_on_a_group_run_rolls_the_rare_for_the_picked_member()
        {
            var run = PooledGroupRun(Member, Member2);
            var killer = new DamageHistoryInfo(Named(Owner, "+Owner"));
            var candidates = new List<WorldObject> { Named(Owner, "Owner"), Named(Member, "Member"), Named(Member2, "+Member2") };

            ThreadLootPool.UniformSource = () => 0.99;
            ThreadLootPool.RareCandidates = r => { Assert.AreSame(run, r); return candidates; };
            ThreadLootPool.IndexSource = (lo, hi) => { Assert.AreEqual(0, lo); Assert.AreEqual(2, hi); return 2; };

            var rare = Item(40020);
            var rolledFor = new List<DamageHistoryInfo>();
            ThreadLootPool.HeldRareRoller = (_, k) => { rolledFor.Add(k); return rare; };

            Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run, DungeonRole.Elite), killer));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));

            Assert.AreEqual(Member2, rolledFor.Single().Guid.Full, "the rare is rolled for the recipient, not the top damager");
            Assert.AreSame(rare, entry.HeldRare);
            Assert.AreEqual(Member2, entry.RareRecipientGuid);
            Assert.AreEqual("Member2", entry.HeldRareFinderName);
        }

        [TestMethod]
        public void BankKill_on_a_group_run_with_nobody_inside_gives_a_roster_killer_their_own_rare()
        {
            var run = PooledGroupRun(Member);
            var killer = new DamageHistoryInfo(Named(Member, "+Member"));

            ThreadLootPool.UniformSource = () => 0.99;
            ThreadLootPool.RareCandidates = _ => new List<WorldObject>();
            ThreadLootPool.IndexSource = NeverIndex;

            var rolledFor = new List<DamageHistoryInfo>();
            ThreadLootPool.HeldRareRoller = (_, k) => { rolledFor.Add(k); return Item(40030); };

            Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run, DungeonRole.Trash), killer));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));

            Assert.AreSame(killer, rolledFor.Single(), "nobody inside: today's top-damager roll");
            Assert.AreEqual(Member, entry.RareRecipientGuid, "ruling Q-T7-1: the rare goes to the roster killer whose rare timer it spent");
            Assert.AreEqual("Member", entry.HeldRareFinderName);
        }

        [TestMethod]
        public void BankKill_on_a_group_run_with_nobody_inside_and_a_non_roster_killer_names_no_recipient()
        {
            const uint Stranger = 0x50000099u;
            var run = PooledGroupRun(Member);
            Assert.IsFalse(run.IsRosterMember(Stranger));
            var killer = new DamageHistoryInfo(Named(Stranger, "Stranger"));

            ThreadLootPool.UniformSource = () => 0.99;
            ThreadLootPool.RareCandidates = _ => new List<WorldObject>();
            ThreadLootPool.IndexSource = NeverIndex;

            var rolledFor = new List<DamageHistoryInfo>();
            ThreadLootPool.HeldRareRoller = (_, k) => { rolledFor.Add(k); return Item(40031); };

            Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run, DungeonRole.Trash), killer));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));

            Assert.AreSame(killer, rolledFor.Single());
            Assert.IsNotNull(entry.HeldRare);
            Assert.AreEqual(0u, entry.RareRecipientGuid, "a killer off the roster keeps the solo rule");
        }

        [TestMethod]
        public void FallbackRecipient_keeps_a_pick_and_otherwise_names_only_a_roster_killer()
        {
            var killer = new DamageHistoryInfo(Named(Member, "Member"));
            var picked = new DamageHistoryInfo(Named(Member2, "Member2"));
            Func<uint, bool> never = _ => throw new AssertFailedException("a pick needs no roster lookup");

            Assert.AreEqual(Member2, ThreadLootPool.FallbackRecipient(picked, Member2, killer, never), "a pick stands");
            Assert.AreEqual(Member, ThreadLootPool.FallbackRecipient(null, 0, killer, g => g == Member), "roster killer");
            Assert.AreEqual(0u, ThreadLootPool.FallbackRecipient(null, 0, killer, _ => false), "killer off the roster");
            Assert.AreEqual(0u, ThreadLootPool.FallbackRecipient(null, 0, null, _ => true), "no killer");
        }

        [TestMethod]
        public void BankKill_with_an_ineligible_roster_killer_stores_no_recipient()
        {
            var run = PooledGroupRun(Member);
            ThreadLootPool.UniformSource = () => 0.99;
            ThreadLootPool.RareCandidates = _ => throw new AssertFailedException("an ineligible kill reads no presence");
            ThreadLootPool.HeldRareRoller = (_, _) => throw new AssertFailedException("an ineligible kill rolls no rare");

            // Level 10 with CanGenerateRare unset and a killer whose level cannot be read keeps the flag false.
            var creature = RunCreature(run, DungeonRole.Trash, level: 10);

            Assert.IsTrue(ThreadLootPool.BankKill(creature, new DamageHistoryInfo(Named(Member, "Member"))));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));

            Assert.IsNull(entry.HeldRare);
            Assert.AreEqual(0u, entry.RareRecipientGuid, "the fallback guid is dropped when no rare is rolled");
        }

        [TestMethod]
        public void BankKill_resolves_the_pet_owner_before_ResolveCanGenerateRare()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadLootPool.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static bool BankKill(Creature creature, DamageHistoryInfo killer)");

            var resolve = body.IndexOf("killer = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);", StringComparison.Ordinal);
            var canRare = body.IndexOf("Creature.ResolveCanGenerateRare(", StringComparison.Ordinal);

            Assert.IsTrue(resolve >= 0, "anchor moved; re-read BankKill");
            Assert.IsTrue(resolve < canRare, "the pet-owner substitution must happen before the rare gate reads killer");
        }

        [TestMethod]
        public void BankKill_draws_only_for_a_group_run()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadLootPool.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static bool BankKill(Creature creature, DamageHistoryInfo killer)");

            StringAssert.Contains(body, "var rolls = run.IsGroup ? RollCount(isBoss ? run.BossLootFactor : run.TrashLootFactor, DrawUniform01) : 1;");

            var guard = body.IndexOf("if (run.IsGroup)\n            {\n                rareFor = ResolveRareFor(", StringComparison.Ordinal);
            Assert.IsTrue(guard >= 0, "the recipient pick sits under the group guard");
            var fallback = body.IndexOf("rareRecipientGuid = FallbackRecipient(rareFor, rareRecipientGuid, killer, run.IsRosterMember);", StringComparison.Ordinal);
            var groupEnd = body.IndexOf("}", fallback, StringComparison.Ordinal);
            var roll = body.IndexOf("var entry = ThreadLootLedgerEntry.FromKill(", StringComparison.Ordinal);
            Assert.IsTrue(guard < fallback && fallback < groupEnd && groupEnd < roll, "the roster-killer fallback is inside the group block, before the roll");
            Assert.AreEqual(1, Occurrences(body, "RollCount("), "one roll-count site");
            Assert.AreEqual(1, Occurrences(body, "ResolveRareFor("), "one recipient site");
            Assert.IsFalse(body.Contains("ThreadSafeRandom"), "BankKill draws only through the group-guarded helpers");

            // The only ThreadSafeRandom calls in the file's CODE (comments stripped) are the two production draw helpers.
            Assert.AreEqual(2, Occurrences(CodeOnly(src), "ThreadSafeRandom."));
            StringAssert.Contains(src, "private static double DrawUniform01() => UniformSource != null ? UniformSource() : ThreadSafeRandom.Next(0f, 1f);");
            StringAssert.Contains(src, "private static int DrawIndexInclusive(int min, int max) => IndexSource != null ? IndexSource(min, max) : ThreadSafeRandom.Next(min, max);");
            StringAssert.Contains(src, "private static IReadOnlyList<WorldObject> DefaultRareCandidates(ThreadDungeonRun run) => ThreadRunPresence.OnlineMembersInside(run);");

            // The rare booking still follows the roll: RollRareFor reports the player it rolled FOR.
            StringAssert.Contains(body, "Corpse.RollRareFor(k, creature.Name, creature.Guid, out rareKiller,");
            StringAssert.Contains(body, "rolls, rareFor, rareRecipientGuid);");
        }

        // ------------------------------------------------------------------------------------------------------
        // MaterializeEntry (R17)
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void MaterializeEntry_runs_both_rolls_Rolls_times_and_adds_the_rare_once_last()
        {
            var calls = new List<string>();
            ThreadCacheFiller.DeathTreasureRoller = p => { calls.Add("dt"); return new List<WorldObject> { Item(100u + (uint)calls.Count) }; };
            ThreadCacheFiller.SalvageAffinityRoller = (a, tier) => { calls.Add($"sa{tier}"); return new List<WorldObject> { Item(200u + (uint)calls.Count) }; };

            var rare = Item(9999);
            var entry = new ThreadLootLedgerEntry(false, false, new TreasureDeath { Tier = 7 }, null, rare, "Tester", 3, Member);

            var items = ThreadCacheFiller.MaterializeEntry(entry);

            CollectionAssert.AreEqual(new[] { "dt", "sa7", "dt", "sa7", "dt", "sa7" }, calls);
            Assert.AreEqual(7, items.Count);
            Assert.AreSame(rare, items.Last());
            Assert.AreEqual(1, items.Count(i => ReferenceEquals(i, rare)), "the held rare is never multiplied");
        }

        [TestMethod]
        public void MaterializeEntry_solo_entry_rolls_once()
        {
            var calls = 0;
            ThreadCacheFiller.DeathTreasureRoller = _ => { calls++; return null; };
            ThreadCacheFiller.SalvageAffinityRoller = (_, _) => { calls++; return null; };

            var items = ThreadCacheFiller.MaterializeEntry(new ThreadLootLedgerEntry(false, false, new TreasureDeath { Tier = 2 }, null, null, null));

            Assert.AreEqual(2, calls, "one DeathTreasure roll and one affinity roll");
            Assert.AreEqual(0, items.Count, "null roll results are skipped");
        }

        [TestMethod]
        public void MaterializeEntry_olthoi_entry_rolls_nothing_at_any_roll_count()
        {
            ThreadCacheFiller.DeathTreasureRoller = _ => throw new AssertFailedException("an Olthoi kill materialises nothing");
            ThreadCacheFiller.SalvageAffinityRoller = (_, _) => throw new AssertFailedException("an Olthoi kill materialises nothing");

            var entry = new ThreadLootLedgerEntry(false, true, new TreasureDeath { Tier = 6 }, null, null, null, 5, 0);

            Assert.AreEqual(0, ThreadCacheFiller.MaterializeEntry(entry).Count);
        }

        [TestMethod]
        public void MaterializeEntry_production_path_is_the_shared_rolls()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCacheFiller.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static List<WorldObject> MaterializeEntry(ThreadLootLedgerEntry entry)");

            var loop = body.IndexOf("for (var roll = 0; roll < entry.Rolls; roll++)", StringComparison.Ordinal);
            var dt = body.IndexOf(": Creature.RollDeathTreasureItems(entry.Profile);", StringComparison.Ordinal);
            var sa = body.IndexOf(": Creature.RollSalvageAffinityItems(entry.SalvageAffinities, entry.Profile?.Tier ?? 1);", StringComparison.Ordinal);
            var rare = body.IndexOf("items.Add(entry.HeldRare)", StringComparison.Ordinal);
            var loopEnd = body.LastIndexOf("}", rare, StringComparison.Ordinal);

            Assert.IsTrue(loop >= 0 && loop < dt && dt < sa && sa < loopEnd && loopEnd < rare, "both rolls inside the loop, the rare after it");
        }

        // ------------------------------------------------------------------------------------------------------
        // Boss bonus scaling (R14) and the member cache owner
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void IsScaledBonus_only_for_a_finite_bonus_above_one()
        {
            Assert.IsFalse(ThreadDungeonRewardSpawner.IsScaledBonus(1.0));
            Assert.IsFalse(ThreadDungeonRewardSpawner.IsScaledBonus(0.9));
            Assert.IsFalse(ThreadDungeonRewardSpawner.IsScaledBonus(0.0));
            Assert.IsFalse(ThreadDungeonRewardSpawner.IsScaledBonus(double.NaN));
            Assert.IsFalse(ThreadDungeonRewardSpawner.IsScaledBonus(double.PositiveInfinity));
            Assert.IsTrue(ThreadDungeonRewardSpawner.IsScaledBonus(1.05));
            Assert.IsTrue(ThreadDungeonRewardSpawner.IsScaledBonus(1.5));
        }

        [TestMethod]
        public void ScaledNoteStack_rounds_away_from_zero_and_never_pays_zero()
        {
            Assert.AreEqual(2, ThreadDungeonRewardSpawner.ScaledNoteStack(2, 1.05), "2.1 -> 2");
            Assert.AreEqual(6, ThreadDungeonRewardSpawner.ScaledNoteStack(5, 1.1), "5.5 -> 6");
            Assert.AreEqual(5, ThreadDungeonRewardSpawner.ScaledNoteStack(3, 1.5), "4.5 -> 5, midpoint away from zero");
            Assert.AreEqual(8, ThreadDungeonRewardSpawner.ScaledNoteStack(5, 1.5), "7.5 -> 8");
            Assert.AreEqual(1, ThreadDungeonRewardSpawner.ScaledNoteStack(1, 1.45), "1.45 -> 1");
            Assert.AreEqual(1, ThreadDungeonRewardSpawner.ScaledNoteStack(0, 1.5), "never below 1");
            Assert.AreEqual(int.MaxValue, ThreadDungeonRewardSpawner.ScaledNoteStack(int.MaxValue, 1.5), "saturates");
        }

        [TestMethod]
        public void ScaledNoteStack_with_an_unscaled_bonus_returns_the_roll()
        {
            Assert.AreEqual(4, ThreadDungeonRewardSpawner.ScaledNoteStack(4, 1.0));
            Assert.AreEqual(4, ThreadDungeonRewardSpawner.ScaledNoteStack(4, double.NaN));
            Assert.AreEqual(4, ThreadDungeonRewardSpawner.ScaledNoteStack(4, 0.5));
            Assert.AreEqual(1, ThreadDungeonRewardSpawner.ScaledNoteStack(0, 1.0));
        }

        [TestMethod]
        public void Scaled_bonus_for_a_null_run_is_empty()
        {
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.BuildBossBonusItems(null, 1.25).Count);
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.BuildBossBonusItems(null, 1.0).Count);
        }

        [TestMethod]
        public void Scaled_bonus_keeps_the_step_order_and_takes_todays_path_when_unscaled()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run, double rewardBonus)");

            var unscaled = body.IndexOf("if (!IsScaledBonus(rewardBonus))\n                return BuildBossBonusItems(run);", StringComparison.Ordinal);
            var note = body.IndexOf("AddBonusTradeNotes(run, items, rewardBonus);", StringComparison.Ordinal);
            var bags = body.IndexOf("AddBonusSalvageBags(run, items);", StringComparison.Ordinal);
            var loot = body.IndexOf("AddBonusLegendaryItems(run, items, rewardBonus);", StringComparison.Ordinal);

            Assert.IsTrue(unscaled >= 0 && unscaled < note && note < bags && bags < loot, "unscaled first; then notes, bags, Legendary items as the 1-arg builder");
            Assert.IsFalse(body.Contains("PropertyManager"), "the pooled bonus reads no switch");

            StringAssert.Contains(src, "private static void AddBonusLegendaryItems(ThreadDungeonRun run, List<WorldObject> items, double rewardBonus) => items.AddRange(BuildCacheLoot(run, rewardBonus));");
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "private static void AddBonusTradeNotes(ThreadDungeonRun run, List<WorldObject> items, double rewardBonus)"),
                "BuildMmdNote(run, out _, rewardBonus)");

            // Loot count: LootQuantityMult * B, and exactly LootQuantityMult when unscaled. The two-argument
            // BuildCacheLoot is an expression-bodied delegator since the cap was parameterised (2026-09-17), so the
            // arithmetic is pinned on the three-argument body that actually carries it.
            StringAssert.Contains(src, "internal static List<WorldObject> BuildCacheLoot(ThreadDungeonRun run) => BuildCacheLoot(run, 1.0);");
            StringAssert.Contains(src,
                "internal static List<WorldObject> BuildCacheLoot(ThreadDungeonRun run, double rewardBonus)\n"
                + "            => BuildCacheLoot(run, rewardBonus, DefaultLootCountCap);");
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "internal static List<WorldObject> BuildCacheLoot(ThreadDungeonRun run, double rewardBonus, int cap)"),
                "run.LootQuantityMult * (IsScaledBonus(rewardBonus) ? rewardBonus : 1.0),\n                cap);");
        }

        /// <summary>
        /// Final review F7: the one- and two-argument bonus builders must list the same steps in the same order, so a
        /// step added to or deleted from one (the one-argument body's "delete this one line" note) cannot silently
        /// leave the group bonus different from the solo one. Compares the ordered AddBonus step NAMES; the arguments
        /// legitimately differ (the two-argument steps take rewardBonus where they scale).
        /// </summary>
        [TestMethod]
        public void Both_bonus_builders_list_the_same_ordered_steps()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");

            var one = BonusSteps(PooledLootSourceText.MethodBody(src, "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run)"));
            var two = BonusSteps(PooledLootSourceText.MethodBody(src, "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run, double rewardBonus)"));

            CollectionAssert.AreEqual(new[] { "AddBonusTradeNotes", "AddBonusSalvageBags", "AddBonusLegendaryItems" }, one, "the one-argument steps");
            CollectionAssert.AreEqual(one, two, "the two builders' ordered step names match");

            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run)"),
                "AND its twin in the two-argument", "the one-argument deletion note names its twin");
        }

        private static List<string> BonusSteps(string body)
        {
            var code = string.Join("\n", body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            return System.Text.RegularExpressions.Regex.Matches(code, @"\b(AddBonus\w+)\(").Select(m => m.Groups[1].Value).ToList();
        }

        [TestMethod]
        public void Scaled_note_uses_the_same_single_stack_draw()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");
            StringAssert.Contains(src, "internal static WorldObject BuildMmdNote(ThreadDungeonRun run, out int count) => BuildMmdNote(run, out count, 1.0);");

            var body = PooledLootSourceText.MethodBody(src, "internal static WorldObject BuildMmdNote(ThreadDungeonRun run, out int count, double rewardBonus)");

            var draw = body.IndexOf("var stack = ThreadSafeRandom.Next(1, max);", StringComparison.Ordinal);
            var scale = body.IndexOf("stack = ScaledNoteStack(stack, rewardBonus);", StringComparison.Ordinal);
            var clamp = body.IndexOf("stack = maxStack;", StringComparison.Ordinal);
            var set = body.IndexOf("note.SetStackSize(stack);", StringComparison.Ordinal);

            Assert.IsTrue(draw >= 0 && draw < scale && scale < clamp && clamp < set);
            Assert.AreEqual(1, Occurrences(body, "ThreadSafeRandom."), "scaling adds no draw");
            StringAssert.Contains(body, "if (IsScaledBonus(rewardBonus))");
        }

        [TestMethod]
        public void Member_cache_chest_is_the_shared_factory_with_the_owner_restamped()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");
            var body = PooledLootSourceText.MethodBody(src, "internal static Chest CreateCacheChest(ThreadDungeonRun run, string purpose, uint ownerGuid)");

            var create = body.IndexOf("var chest = CreateCacheChest(run, purpose);", StringComparison.Ordinal);
            var stamp = body.IndexOf("chest.P_DungeonCacheOwnerGuid = ownerGuid;", StringComparison.Ordinal);

            Assert.IsTrue(create >= 0 && create < stamp, "every stamp from the shared factory first, then the member owner");
            Assert.IsTrue(body.Contains("if (chest != null)"), "a failed create stays null");
        }

        private static string CodeOnly(string source)
            => string.Join("\n", source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        private static int Occurrences(string text, string token)
            => (text.Length - text.Replace(token, string.Empty).Length) / token.Length;
    }
}
