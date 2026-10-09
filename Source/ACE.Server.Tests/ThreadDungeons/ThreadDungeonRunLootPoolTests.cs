using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class ThreadDungeonRunLootPoolTests
    {
        [TestMethod]
        public void Pooled_loot_is_off_until_stamped()
        {
            Assert.IsFalse(NewRun().PooledLoot, "an unstamped run must read as the corpse model");
        }

        [TestMethod]
        public void Pooled_loot_stamp_is_write_once()
        {
            var on = NewRun();
            on.MarkPooledLoot(true);
            on.MarkPooledLoot(false);
            Assert.IsTrue(on.PooledLoot, "a second stamp must not flip an ON run");

            var off = NewRun();
            off.MarkPooledLoot(false);
            off.MarkPooledLoot(true);
            Assert.IsFalse(off.PooledLoot, "a second stamp must not flip an OFF run");
        }

        [TestMethod]
        public void Populate_stamps_the_switch_before_any_creature_exists()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static void TryPopulate(ThreadDungeonRun run, Landblock landblock)");

            var stamp = body.IndexOf("run.MarkPooledLoot(PropertyManager.GetBool(\"dynamic_dungeons_pooled_loot_enabled\", true).Item);", StringComparison.Ordinal);
            var reached = body.IndexOf("run.MarkPopulateReached();", StringComparison.Ordinal);
            var plan = body.IndexOf("DungeonSpawnPlan plan;", StringComparison.Ordinal);

            Assert.IsTrue(stamp >= 0, "TryPopulate must stamp the switch");
            Assert.IsTrue(reached >= 0 && plan >= 0, "anchors moved; re-read TryPopulate");
            Assert.IsTrue(reached < stamp && stamp < plan, "the stamp belongs after MarkPopulateReached and before the plan is built");
        }

        [TestMethod]
        public void Switch_row_follows_the_boss_chest_row_and_defaults_true()
        {
            var pm = PooledLootSourceText.Read("Source/ACE.Server/Managers/PropertyManager.cs");
            var boss = pm.IndexOf("(\"dynamic_dungeons_boss_chest_enabled\", new Property<bool>(true,", StringComparison.Ordinal);
            var pooled = pm.IndexOf("(\"dynamic_dungeons_pooled_loot_enabled\", new Property<bool>(true,", StringComparison.Ordinal);

            Assert.IsTrue(boss >= 0, "boss chest row moved");
            Assert.IsTrue(pooled > boss, "the pooled row must exist, default true, after the boss chest row");

            var tsv = PooledLootSourceText.Read("Source/config-defaults.tsv");
            StringAssert.Contains(tsv, "dynamic_dungeons_pooled_loot_enabled\tbool\ttrue\ttrue\t");
        }

        private static ThreadLootLedgerEntry Entry(bool boss = false, WorldObject rare = null)
            => new ThreadLootLedgerEntry(boss, false, null, null, rare, rare == null ? null : "Tester");

        [TestMethod]
        public void Ledger_accepts_starting_active_and_cleared_but_not_ended_or_unpooled()
        {
            Assert.IsFalse(NewRun().TryAppendLootEntry(Entry()), "a run not stamped pooled banks nothing");

            var run = PooledRun();
            Assert.IsTrue(run.TryAppendLootEntry(Entry()), "Starting");
            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            Assert.IsTrue(run.TryAppendLootEntry(Entry()), "Active");
            run.RecordKill(false);
            run.RecordKill(false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.IsTrue(run.TryAppendLootEntry(Entry()), "Cleared - late kills pool too");
            run.MarkEnded("test");
            Assert.IsFalse(run.TryAppendLootEntry(Entry()), "Ended");
            Assert.AreEqual(3, run.LedgerCount);
        }

        [TestMethod]
        public void Invariant1_each_entry_is_claimed_once_in_kill_order()
        {
            var run = PooledRun();
            var first = Entry();
            var second = Entry(boss: true);
            run.TryAppendLootEntry(first);
            run.TryAppendLootEntry(second);

            Assert.IsTrue(run.TryClaimNextLootEntry(out var a));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var b));
            Assert.IsFalse(run.TryClaimNextLootEntry(out var none));
            Assert.AreSame(first, a);
            Assert.AreSame(second, b);
            Assert.IsNull(none);
            Assert.AreEqual(0, run.LedgerCount);
        }

        [TestMethod]
        public void Invariant1_concurrent_claims_hand_out_each_entry_exactly_once()
        {
            var run = PooledRun();
            for (var i = 0; i < 500; i++)
                run.TryAppendLootEntry(Entry());

            var claimed = new System.Collections.Concurrent.ConcurrentBag<ThreadLootLedgerEntry>();
            Parallel.For(0, 1000, _ => { if (run.TryClaimNextLootEntry(out var e)) claimed.Add(e); });

            Assert.AreEqual(500, claimed.Count);
            Assert.AreEqual(500, claimed.Distinct().Count());
        }

        [TestMethod]
        public void Has_unclaimed_loot_tracks_ledger_bonus_and_overflow()
        {
            var run = PooledRun();
            Assert.IsFalse(run.HasUnclaimedLoot);

            run.TryAppendLootEntry(Entry());
            Assert.IsTrue(run.HasUnclaimedLoot, "ledger");
            run.TryClaimNextLootEntry(out _);
            Assert.IsFalse(run.HasUnclaimedLoot);

            Assert.IsTrue(run.TryMarkLootBonusPending());
            Assert.IsTrue(run.HasUnclaimedLoot, "bonus");
            Assert.IsTrue(run.TryClaimLootBonus());
            Assert.IsFalse(run.HasUnclaimedLoot);

            Assert.IsTrue(run.TryAddOverflow(Item(), null));
            Assert.IsTrue(run.HasUnclaimedLoot, "overflow");
        }

        [TestMethod]
        public void Boss_bonus_shares_the_boss_chest_latch_and_is_claimed_once()
        {
            var run = PooledRun();

            Assert.IsTrue(run.TryMarkLootBonusPending());
            Assert.IsFalse(run.TryMarkLootBonusPending(), "a second boss death adds no second bonus");
            Assert.IsFalse(run.TryClaimBossChest(), "the corpse-model latch is the same latch");
            Assert.IsTrue(run.TryClaimLootBonus());
            Assert.IsFalse(run.TryClaimLootBonus());
        }

        [TestMethod]
        public void Overflow_is_fifo_carries_the_rare_finder_and_refuses_after_end()
        {
            var run = PooledRun();
            var a = Item();
            var b = Item();
            run.TryAddOverflow(a, null);
            run.TryAddOverflow(b, "Tester");

            var taken = run.TakeAllOverflow();
            Assert.AreEqual(0, run.OverflowCount);
            Assert.AreSame(a, taken[0].Item);
            Assert.AreSame(b, taken[1].Item);
            Assert.AreEqual("Tester", taken[1].RareFinderName);

            run.MarkEnded("test");
            Assert.IsFalse(run.TryAddOverflow(Item(), null));
        }

        [TestMethod]
        public void Invariant3_concurrent_placement_requests_admit_exactly_one()
        {
            var run = PooledRun();
            var now = DateTime.UtcNow;
            var tokens = new System.Collections.Concurrent.ConcurrentBag<long>();

            Parallel.For(0, 64, _ => { var t = run.TryBeginCachePlacement(now); if (t != 0) tokens.Add(t); });

            Assert.AreEqual(1, tokens.Count);
            Assert.IsTrue(run.IsCachePlacementInProgress(now));
            run.EndCachePlacement(tokens.Single());
            Assert.IsFalse(run.IsCachePlacementInProgress(now));
            Assert.AreNotEqual(0L, run.TryBeginCachePlacement(now), "released latch admits the next request");
        }

        [TestMethod]
        public void Stale_latch_is_taken_over_and_the_old_token_can_neither_run_nor_release()
        {
            var run = PooledRun();
            var t0 = DateTime.UtcNow;
            var old = run.TryBeginCachePlacement(t0);

            Assert.AreEqual(0L, run.TryBeginCachePlacement(t0.AddSeconds(29)), "not stale yet");
            var fresh = run.TryBeginCachePlacement(t0 + ThreadDungeonRun.CachePlacementStaleAfter);
            Assert.AreNotEqual(0L, fresh);
            Assert.IsFalse(run.IsCachePlacementCurrent(old));
            run.EndCachePlacement(old);
            Assert.IsTrue(run.IsCachePlacementCurrent(fresh), "a superseded action must not release its successor");
        }

        [TestMethod]
        public void Placement_latch_is_refused_once_the_run_has_ended()
        {
            var run = PooledRun();
            run.MarkEnded("test");
            Assert.AreEqual(0L, run.TryBeginCachePlacement(DateTime.UtcNow));
        }

        [TestMethod]
        public void Spawncache_cooldown_is_five_seconds()
        {
            var run = PooledRun();
            var t0 = DateTime.UtcNow;

            Assert.IsTrue(run.SpawnCacheCooldownElapsed(t0));
            run.StampSpawnCacheCooldown(t0);
            Assert.IsFalse(run.SpawnCacheCooldownElapsed(t0.AddSeconds(4.9)));
            Assert.IsTrue(run.SpawnCacheCooldownElapsed(t0 + ThreadDungeonRun.SpawnCacheCooldown));
        }

        [TestMethod]
        public void Placement_wanted_is_claimed_once()
        {
            var run = PooledRun();
            Assert.IsFalse(run.TryClaimPlacementWanted());
            run.MarkPlacementWanted();
            Assert.IsTrue(run.IsPlacementWanted);
            Assert.IsTrue(run.TryClaimPlacementWanted());
            Assert.IsFalse(run.TryClaimPlacementWanted());
        }

        [TestMethod]
        public void Caches_register_once_drop_when_destroyed_and_refuse_after_end()
        {
            var run = PooledRun();
            var cache = Cache();

            Assert.IsTrue(run.TryRegisterCache(cache));
            Assert.IsFalse(run.TryRegisterCache(cache), "no duplicates");
            Assert.AreEqual(1, run.PlacedCachesSnapshot().Count);

            Assert.IsFalse(ThreadLootPool.AnyCacheHoldsItems(run));
            Assert.IsTrue(cache.TryAddToInventory(Item()));
            Assert.IsTrue(ThreadLootPool.AnyCacheHoldsItems(run));

            cache.Destroy();
            Assert.AreEqual(0, run.PlacedCachesSnapshot().Count, "a destroyed cache is not a placed cache");

            run.MarkEnded("test");
            Assert.IsFalse(run.TryRegisterCache(Cache()));
        }

        [TestMethod]
        public void Invariant5_run_end_destroys_overflow_and_unclaimed_held_rares_only()
        {
            var run = PooledRun();
            var claimedRare = Item(40001);
            var unclaimedRare = Item(40002);
            var overflowItem = Item(40003);

            run.TryAppendLootEntry(Entry(rare: claimedRare));
            run.TryAppendLootEntry(Entry(rare: unclaimedRare));
            run.TryAppendLootEntry(Entry());
            run.TryClaimNextLootEntry(out _);
            run.TryMarkLootBonusPending();
            run.TryAddOverflow(overflowItem, null);

            run.MarkEnded("test");
            var drained = ThreadLootPool.DisposeForRunEnd(run);

            Assert.AreEqual(2, drained.LedgerEntries);
            Assert.IsTrue(drained.BonusPending);
            CollectionAssert.AreEqual(new[] { unclaimedRare }, drained.HeldRares);
            CollectionAssert.AreEqual(new[] { overflowItem }, drained.Overflow);
            Assert.IsTrue(unclaimedRare.IsDestroyed);
            Assert.IsTrue(overflowItem.IsDestroyed);
            Assert.IsFalse(claimedRare.IsDestroyed, "a claimed rare belongs to whoever claimed it");
            Assert.IsFalse(run.HasUnclaimedLoot);
            Assert.AreEqual(0, run.LedgerCount);
        }

        [TestMethod]
        public void A_refused_append_destroys_the_held_rare()
        {
            var run = PooledRun();
            run.MarkEnded("test");
            var rare = Item(40004);

            Assert.IsFalse(ThreadLootPool.AppendOrDiscard(run, Entry(rare: rare)));
            Assert.IsTrue(rare.IsDestroyed);
        }

        [TestMethod]
        public void Olthoi_entry_cannot_carry_a_rare()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new ThreadLootLedgerEntry(false, true, null, null, Item(), "Tester"));
        }

        [TestMethod]
        public void FromKill_rolls_a_rare_only_for_an_eligible_killer_and_keeps_the_finder_name()
        {
            var killerObject = new GenericObject(new Weenie { WeenieClassId = 1, WeenieType = WeenieType.Generic, PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string> { { ACE.Entity.Enum.Properties.PropertyString.Name, "+Tester" } } }, new ObjectGuid(0x50000001));
            var killer = new ACE.Server.Entity.DamageHistoryInfo(killerObject);
            var rolls = 0;
            var rare = Item(40005);

            var ineligible = ThreadLootLedgerEntry.FromKill(false, killer, canGenerateRare: false, null, null, _ => { rolls++; return rare; });
            Assert.AreEqual(0, rolls);
            Assert.IsNull(ineligible.HeldRare);

            var noKiller = ThreadLootLedgerEntry.FromKill(false, null, canGenerateRare: true, null, null, _ => { rolls++; return rare; });
            Assert.AreEqual(0, rolls);
            Assert.IsNull(noKiller.HeldRare);

            var eligible = ThreadLootLedgerEntry.FromKill(true, killer, canGenerateRare: true, null, null, _ => { rolls++; return rare; });
            Assert.AreEqual(1, rolls);
            Assert.AreSame(rare, eligible.HeldRare);
            Assert.AreEqual("Tester", eligible.HeldRareFinderName, "leading + trimmed, as the corpse broadcast does");
            Assert.IsTrue(eligible.IsBoss);
            Assert.IsFalse(eligible.KillerIsOlthoiPlayer);
        }

        [TestMethod]
        public void BankKill_banks_the_creatures_own_profile_role_and_affinities()
        {
            // The Creature constructor reads the vital formulas (Creature.SetEphemeralValues -> GameTables).
            TestGameTables.EnsureInitialized();

            var run = PooledRun();
            var profile = new ACE.Database.Models.World.TreasureDeath { Tier = 6 };
            var affinities = new List<(int MaterialId, uint BaseWcid, double Chance)> { (12, 12007u, 0.1) };
            var creature = new Creature(new Weenie { WeenieClassId = 42, WeenieType = WeenieType.Creature }, new ObjectGuid(NextGuid()));
            creature.P_DungeonRun = run;
            creature.DungeonRole = DungeonRole.Boss;
            creature.DeathTreasureOverride = profile;
            creature.P_DungeonSalvageAffinities = affinities;

            Assert.IsTrue(ThreadLootPool.BankKill(creature, killer: null));
            Assert.IsTrue(run.TryClaimNextLootEntry(out var entry));
            Assert.IsTrue(entry.IsBoss);
            Assert.AreSame(profile, entry.Profile);
            Assert.AreSame(affinities, entry.SalvageAffinities);
            Assert.IsNull(entry.HeldRare, "no killer, no rare roll, no PropertyManager read");

            run.MarkEnded("test");
            Assert.IsFalse(ThreadLootPool.BankKill(creature, killer: null));
        }

        [TestMethod]
        public void BankKill_books_a_found_rare_only_after_the_ledger_append_succeeds()
        {
            // Controller ruling (Task 4): the corpse books only after the rare lands on the corpse, so the pooled
            // model books only after the append; a refused append destroys the rare and books nothing. Booking
            // needs a live Player, which this harness cannot build, so the order is pinned against the source.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadLootPool.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static bool BankKill(Creature creature, DamageHistoryInfo killer)");

            var append = body.IndexOf("if (AppendOrDiscard(run, entry))", StringComparison.Ordinal);
            var book = body.IndexOf("Corpse.ApplyRareFoundBookkeeping(", StringComparison.Ordinal);
            Assert.IsTrue(append >= 0 && book >= 0, "anchors moved; re-read BankKill");

            var successReturn = body.IndexOf("return true;", append, StringComparison.Ordinal);
            Assert.IsTrue(append < book && book < successReturn, "bookkeeping belongs inside the append-success branch");

            var occurrences = src.Split(new[] { "ApplyRareFoundBookkeeping(" }, StringSplitOptions.None).Length - 1;
            Assert.AreEqual(1, occurrences, "nothing else in ThreadLootPool may book a rare, in particular not the roller");
        }
    }
}
