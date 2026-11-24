using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadBoonTestFixtures;
using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Thread Shrine loot blessing: more ITEMS per kill, never better ones. Pooled (ThreadLootPool.BankKill through
    /// LootRollsFor) and corpse (CorpseExtraRolls, read by Creature.GenerateTreasure). The killer's blessing is read
    /// through the KillerLootBoon seam here, because its production body needs a Player; the presence half of it is
    /// ThreadBoons.ForRun, proven in ThreadBoonLedgerTests. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadLootBoonTests
    {
        private static Func<double> NeverDraw => () => throw new AssertFailedException("the uniform source must not be called");

        private Func<ThreadDungeonRun, DamageHistoryInfo, double> savedBoon;

        [TestInitialize]
        public void SaveSeams() => savedBoon = ThreadLootPool.KillerLootBoon;

        [TestCleanup]
        public void ResetSeams()
        {
            ThreadLootPool.KillerLootBoon = savedBoon;
            ThreadLootPool.UniformSource = null;
            ThreadLootPool.IndexSource = null;
            ThreadLootPool.RareCandidates = null;
            ThreadLootPool.HeldRareRoller = null;
        }

        // ---- LootRollsFor --------------------------------------------------------------------------------------

        [TestMethod]
        public void Without_a_blessing_the_roll_counts_and_draws_are_exactly_the_pre_shrine_ones()
        {
            Assert.AreEqual(1, ThreadLootPool.LootRollsFor(false, () => throw new AssertFailedException("solo never reads the group factor"), 1.0, NeverDraw));
            Assert.AreEqual(1, ThreadLootPool.LootRollsFor(false, () => 5.0, double.NaN, NeverDraw));
            Assert.AreEqual(1, ThreadLootPool.LootRollsFor(false, () => 5.0, 0.5, NeverDraw));

            foreach (var factor in new[] { 1.0, 2.0, 2.1, 4.0 / 3.0 * 1.05, 99.5 })
            {
                foreach (var u in new[] { 0.0, 0.05, 0.5, 0.99 })
                {
                    int a = 0, b = 0;
                    var before = ThreadLootPool.RollCount(factor, () => { a++; return u; });
                    var now = ThreadLootPool.LootRollsFor(true, () => factor, 1.0, () => { b++; return u; });

                    Assert.AreEqual(before, now, $"factor {factor} u {u}");
                    Assert.AreEqual(a, b, $"same number of draws for factor {factor}");
                }
            }
        }

        [TestMethod]
        public void A_solo_one_and_a_quarter_blessing_adds_a_roll_in_about_a_quarter_of_kills()
        {
            const int Kills = 20000;
            var rng = new Random(20261109);
            var extra = 0;

            for (var i = 0; i < Kills; i++)
            {
                var rolls = ThreadLootPool.LootRollsFor(false, () => 1.0, 1.25, rng.NextDouble);
                Assert.IsTrue(rolls == 1 || rolls == 2, rolls.ToString());

                if (rolls == 2)
                    extra++;
            }

            var share = (double)extra / Kills;
            Assert.AreEqual(0.25, share, 0.015, $"observed {share:0.0000}");
        }

        [TestMethod]
        public void The_group_factor_multiplies_and_the_100_roll_cap_holds()
        {
            Assert.AreEqual(3, ThreadLootPool.LootRollsFor(true, () => 2.0, 1.25, () => 0.4), "2 x 1.25 = 2.5: 2, plus one for 0.4 < 0.5");
            Assert.AreEqual(2, ThreadLootPool.LootRollsFor(true, () => 2.0, 1.25, () => 0.6));
            Assert.AreEqual(ThreadLootPool.MaxRollsPerKill, ThreadLootPool.LootRollsFor(true, () => 90.0, 1.25, NeverDraw), "112.5 is capped at 100 without a draw");
            Assert.AreEqual(ThreadLootPool.MaxRollsPerKill, ThreadLootPool.LootRollsFor(true, () => 99.5, 1.25, () => 0.0));
        }

        // ---- BankKill (pooled) ----------------------------------------------------------------------------------

        [TestMethod]
        public void BankKill_without_a_blessing_banks_one_roll_with_no_draw()
        {
            ThreadLootPool.KillerLootBoon = (_, __) => 1.0;
            ThreadLootPool.UniformSource = NeverDraw;
            ThreadLootPool.HeldRareRoller = (_, __) => null;

            var run = Run(pooled: true);
            Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run), new DamageHistoryInfo(Named(Owner, "Owner"))));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));
            Assert.AreEqual(1, entry.Rolls);
        }

        [TestMethod]
        public void BankKill_reads_the_killers_blessing_and_rolls_the_rare_once()
        {
            var seen = new List<DamageHistoryInfo>();
            ThreadLootPool.KillerLootBoon = (_, k) => { seen.Add(k); return 2.0; };
            ThreadLootPool.UniformSource = NeverDraw; // 2.0 is whole: no draw

            var rareRolls = 0;
            var rare = Item(40050);
            ThreadLootPool.HeldRareRoller = (_, __) => { rareRolls++; return rare; };

            var run = Run(pooled: true);
            var killer = new DamageHistoryInfo(Named(Owner, "Owner"));

            Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run), killer));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));

            Assert.AreEqual(2, entry.Rolls, "solo: factor 1 x blessing 2");
            Assert.AreEqual(1, rareRolls, "the rare is still once per kill");
            Assert.AreSame(rare, entry.HeldRare);
            Assert.AreSame(killer, seen.Single(), "a non-pet killer is passed as itself");
        }

        [TestMethod]
        public void BankKill_with_a_quarter_blessing_draws_once_for_the_extra_roll()
        {
            ThreadLootPool.KillerLootBoon = (_, __) => 1.25;
            ThreadLootPool.HeldRareRoller = (_, __) => null;

            foreach (var (u, expected) in new[] { (0.1, 2), (0.3, 1) })
            {
                var draws = 0;
                ThreadLootPool.UniformSource = () => { draws++; return u; };

                var run = Run(pooled: true);
                Assert.IsTrue(ThreadLootPool.BankKill(RunCreature(run), new DamageHistoryInfo(Named(Owner, "Owner"))));
                Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));

                Assert.AreEqual(expected, entry.Rolls, $"u {u}");
                Assert.AreEqual(1, draws);
            }
        }

        [TestMethod]
        public void The_killers_blessing_is_read_after_the_pet_owner_substitution()
        {
            // A CombatPet with a live owner cannot be built here (it needs a Player); the order is pinned instead, and the
            // substitution itself is DamageHistoryInfo.ResolvePetOwnerAsKiller (CreatureLootRollTests).
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadLootPool.cs");
            var bank = PooledLootSourceText.MethodBody(src, "public static bool BankKill(Creature creature, DamageHistoryInfo killer)");

            var resolve = bank.IndexOf("killer = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);", StringComparison.Ordinal);
            var boon = bank.IndexOf("KillerLootBoon(run, killer)", StringComparison.Ordinal);
            Assert.IsTrue(resolve >= 0 && resolve < boon, "BankKill: owner first, then the blessing");

            var corpse = PooledLootSourceText.MethodBody(src, "internal static int CorpseExtraRolls(ThreadDungeonRun run, int? runIdStamp, DamageHistoryInfo killer)");
            StringAssert.Contains(corpse, "KillerLootBoon(run, DamageHistoryInfo.ResolvePetOwnerAsKiller(killer))");

            // Production: only a Player killer inside THIS run counts (ThreadBoons.ForRun); a non-player is 1.0.
            StringAssert.Contains(src, "ThreadBoons.AnyLive && killer != null && killer.IsPlayer && killer.TryGetAttacker() is Player player\n                ? ThreadBoons.ForRun(run, player, ThreadBoonKind.Loot)\n                : 1.0;");
        }

        [TestMethod]
        public void A_killer_outside_the_run_gets_no_boost()
        {
            using var scope = new Releaser();
            var run = scope.Track(Run(Member));
            Use(run, Only(ThreadBoonKind.Loot), new[] { Owner });

            var inside = ThreadBoons.ForRun(run, Owner, Inside(run), ThreadBoonKind.Loot);
            var outside = ThreadBoons.ForRun(run, Owner, OtherInstance(run), ThreadBoonKind.Loot);

            Assert.AreEqual(1.25, inside, 1e-12);
            Assert.AreEqual(1, ThreadLootPool.LootRollsFor(false, () => 1.0, outside, NeverDraw), "outside: one roll, no draw");

            // The production seam's own fallback: a killer that is not a Player (a monster, a vanished attacker) is 1.0.
            ThreadLootPool.KillerLootBoon = savedBoon;
            Assert.AreEqual(1.0, ThreadLootPool.KillerLootBoon(run, new DamageHistoryInfo(Named(Owner, "Owner"))), "a GenericObject is not a Player");
            Assert.AreEqual(1.0, ThreadLootPool.KillerLootBoon(run, null));
        }

        // ---- the corpse model --------------------------------------------------------------------------------------

        [TestMethod]
        public void The_corpse_model_adds_rollcount_minus_one_extra_rolls_and_nothing_for_a_non_run_creature()
        {
            var run = Run();
            var killer = new DamageHistoryInfo(Named(Owner, "Owner"));

            ThreadLootPool.KillerLootBoon = (_, __) => throw new AssertFailedException("a creature no run owns never asks");
            Assert.AreEqual(0, ThreadLootPool.CorpseExtraRolls(null, (int)run.RunId, killer));
            Assert.AreEqual(0, ThreadLootPool.CorpseExtraRolls(run, null, killer), "both keys, as the kill hook");

            ThreadLootPool.KillerLootBoon = (_, __) => 1.0;
            ThreadLootPool.UniformSource = NeverDraw;
            Assert.AreEqual(0, ThreadLootPool.CorpseExtraRolls(run, (int)run.RunId, killer), "no blessing, no draw");

            ThreadLootPool.KillerLootBoon = (_, __) => 1.25;
            ThreadLootPool.UniformSource = () => 0.1;
            Assert.AreEqual(1, ThreadLootPool.CorpseExtraRolls(run, (int)run.RunId, killer));
            ThreadLootPool.UniformSource = () => 0.9;
            Assert.AreEqual(0, ThreadLootPool.CorpseExtraRolls(run, (int)run.RunId, killer));

            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");
            var treasure = PooledLootSourceText.MethodBody(src, "private List<WorldObject> GenerateTreasure(DamageHistoryInfo killer, Corpse corpse)");
            StringAssert.Contains(treasure, "var extraRolls = ACE.Server.ThreadDungeons.ThreadLootPool.CorpseExtraRolls(P_DungeonRun, GetProperty(PropertyInt.ThreadDungeonRunId), killer);");
            // An extra item a full corpse refuses is collected for the ground drop like every other roll, never destroyed
            // (CorpseOverflowLootTests pins that GenerateTreasure adds to the corpse only through the helper).
            StringAssert.Contains(treasure, "foreach (var wo in RollDeathTreasureItems(DeathTreasure))\n                    {\n                        AddToCorpseOrCollect(corpse, wo, droppedItems);\n\n                        DoCantripLogging(killer, wo);");
            Assert.IsFalse(treasure.Contains("wo.Destroy();", StringComparison.Ordinal), "no rolled item is destroyed on refusal");
        }
    }
}
