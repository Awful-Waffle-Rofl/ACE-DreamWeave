using System;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The boss bonus is built once and consumed by two callers: the corpse model's boss-death cache and the
    /// pooled model's first cache. The builders read PropertyManager and the weenie factory, so their order
    /// and the unchanged boss-cache sequence are pinned against source; the null edge is driven.
    /// </summary>
    [TestClass]
    public class ThreadRewardBonusBuilderTests
    {
        private static string Src() => PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");

        private static double B(int seats)
            => GroupScaling.RewardBonusFor(seats, GroupScaling.DefaultRewardBonusPerMember, GroupScaling.DefaultRewardBonusCap);

        /// <summary>The per-seat share of a pooled build at seats * B, capped at DefaultLootCountCap * seats.</summary>
        private static double PooledShare(long baseCount, double quantityMult, int seats)
            => ThreadDungeonRewardSpawner.ScaledLootCount(baseCount, quantityMult * seats * B(seats),
                   ThreadDungeonRewardSpawner.DefaultLootCountCap * seats) / (double)seats;

        /// <summary>What one player got before the split: their own build at B, under the single-cache cap.</summary>
        private static int SoloTimesB(long baseCount, double quantityMult, int seats)
            => ThreadDungeonRewardSpawner.ScaledLootCount(baseCount, quantityMult * B(seats));

        /// <summary>B at a non-default reward-bonus cap, for the sweep that varies the tunables.</summary>
        private static double BAt(int seats, double bonusCap)
            => GroupScaling.RewardBonusFor(seats, GroupScaling.DefaultRewardBonusPerMember, bonusCap);

        /// <summary>PooledShare with the bonus supplied rather than read at the default tunables.</summary>
        private static double PooledShareAt(long baseCount, double quantityMult, int seats, double bonus)
            => ThreadDungeonRewardSpawner.ScaledLootCount(baseCount, quantityMult * seats * bonus,
                   ThreadDungeonRewardSpawner.DefaultLootCountCap * seats) / (double)seats;

        /// <summary>SoloTimesB with the bonus supplied rather than read at the default tunables.</summary>
        private static int SoloTimesBAt(long baseCount, double quantityMult, double bonus)
            => ThreadDungeonRewardSpawner.ScaledLootCount(baseCount, quantityMult * bonus);

        /// <summary>
        /// Owner ruling, 2026-09-17: the Legendary item rolls are pooled and shared, so each player's item share is
        /// B times a solo boss bonus - the same per-player multiplier the snake-dealt trash loot already pays.
        ///
        /// Measured against the arithmetic the deal actually runs: ScaledLootCount(base, q * seats * B, cap * seats)
        /// divided by the seats, against the old per-seat ScaledLootCount(base, q * B). The factor is seats * B and
        /// NOT run.BossLootFactor (E * B): E is built from the LOCKED ROSTER and carries the tunable g, so E * B over
        /// DealSeats() is only B at default tuning with every member keyed.
        ///
        /// ScaledLootCount ceilings, so a seat can land one item either side of the target; that is documented, not
        /// engineered around. The assertion is therefore "within one item", with the exact cases called out.
        /// </summary>
        [TestMethod]
        public void Per_player_pooled_item_yield_is_B_times_a_solo_bonus()
        {
            const long Base = ThreadDungeonRewardSpawner.DefaultBossCacheLootCount;

            foreach (var seats in new[] { 2, 3, 4, 6, 9, 20 })
            {
                var share = PooledShare(Base, 1.0, seats);
                var target = SoloTimesB(Base, 1.0, seats);

                Assert.IsTrue(Math.Abs(share - target) <= 1.0,
                    $"N={seats}: per-player share {share} against B times solo {target} (B={B(seats):0.###})");
            }

            // The exact cases, spelled out so a regression in the arithmetic is visible rather than merely "within one".
            Assert.AreEqual(1.10, B(3), 1e-9);
            Assert.AreEqual(11.0, PooledShare(Base, 1.0, 3), 1e-9, "N=3: ceil(10 * 3 * 1.10) = 33, three ways = 11");
            Assert.AreEqual(11, SoloTimesB(Base, 1.0, 3));

            Assert.AreEqual(1.05, B(2), 1e-9);
            Assert.AreEqual(10.5, PooledShare(Base, 1.0, 2), 1e-9, "N=2: ceil(10 * 2 * 1.05) = 21, two ways = 10.5");
            Assert.AreEqual(11, SoloTimesB(Base, 1.0, 2), "the odd item goes to whichever seat the snake cursor favours");

            Assert.AreEqual(1.40, B(9), 1e-9);
            Assert.AreEqual(14.0, PooledShare(Base, 1.0, 9), 1e-9, "N=9: ceil(10 * 9 * 1.40) = 126, nine ways = 14");

            Assert.AreEqual(1.50, B(20), 1e-9, "B is capped at DefaultRewardBonusCap");
            Assert.AreEqual(15.0, PooledShare(Base, 1.0, 20), 1e-9, "N=20: ceil(10 * 20 * 1.50) = 300, twenty ways = 15");
        }

        /// <summary>
        /// Review finding 2, 2026-09-17: the yield invariant was only ever exercised at LootQuantityMult 1.0 and the
        /// default reward-bonus cap. It holds for arbitrary values because both terms are common multiplicative
        /// factors on each side of the comparison - but that symmetry is exactly what a later edit could break
        /// silently, so it is driven rather than argued. A loot-boosted gem (quantityMult up to 3.0) and a raised
        /// dynamic_dungeons_group_reward_bonus_cap (up to 3.0) are both swept.
        ///
        /// Both counts are asserted to sit under their caps, so the invariant cannot pass by both sides clamping.
        /// </summary>
        [TestMethod]
        public void The_per_player_yield_holds_at_non_default_quantity_and_bonus_cap()
        {
            const long Base = ThreadDungeonRewardSpawner.DefaultBossCacheLootCount;

            var checks = 0;

            foreach (var quantityMult in new[] { 1.0, 1.5, 2.0, 3.0 })
            {
                foreach (var bonusCap in new[] { GroupScaling.DefaultRewardBonusCap, 1.25, 2.0, 3.0 })
                {
                    foreach (var seats in new[] { 2, 3, 6, 9, 20 })
                    {
                        var bonus = BAt(seats, bonusCap);
                        var share = PooledShareAt(Base, quantityMult, seats, bonus);
                        var target = SoloTimesBAt(Base, quantityMult, bonus);
                        var where = $"q={quantityMult} cap={bonusCap} N={seats} (B={bonus:0.###})";

                        Assert.IsTrue(target < ThreadDungeonRewardSpawner.DefaultLootCountCap,
                            $"{where}: the solo side must not be clamped, or the comparison proves nothing");
                        Assert.IsTrue(share * seats < ThreadDungeonRewardSpawner.DefaultLootCountCap * seats,
                            $"{where}: the pooled side must not be clamped either");

                        Assert.IsTrue(Math.Abs(share - target) <= 1.0,
                            $"{where}: per-player share {share} against B times solo {target}");

                        checks++;
                    }
                }
            }

            Assert.AreEqual(80, checks, "the whole sweep ran");

            // One case spelled out: a Bounteous gem (q = 2.0) at the raised cap, six seats.
            Assert.AreEqual(1.25, BAt(6, 3.0), 1e-9, "six seats is still under a raised cap");
            Assert.AreEqual(25.0, PooledShareAt(Base, 2.0, 6, 1.25), 1e-9, "ceil(10 * 2 * 6 * 1.25) = 150, six ways = 25");
            Assert.AreEqual(25, SoloTimesBAt(Base, 2.0, 1.25));
        }

        /// <summary>
        /// Review finding 1, 2026-09-17: pooling the item rolls concentrated the blast radius of a throwing roll.
        /// Before the split each seat built its own bonus inside its own try/catch, so a throw cost one seat; after
        /// it, one build serves every seat, and the caller's single catch would have zeroed the item share for the
        /// whole fellowship. The guard is per roll, at the call site, which fixes the solo path at the same time.
        ///
        /// Drives a throw in the MIDDLE of a batch and asserts the rolls on both sides of it survive.
        /// </summary>
        [TestMethod]
        public void A_throwing_roll_costs_that_roll_and_not_the_batch()
        {
            var attempted = 0;

            var built = ThreadDungeonRewardSpawner.RollCacheItems(5, () =>
            {
                attempted++;

                if (attempted == 3)
                    throw new InvalidOperationException("the loot factory threw mid-batch");

                // Roll 4 comes back null, the normal no-wcid outcome, which must stay tolerated alongside the throw.
                return attempted == 4 ? null : ThreadLootTestFixtures.Item();
            }, "test-run");

            Assert.AreEqual(5, attempted, "every roll is attempted; the throw does not end the loop");
            Assert.AreEqual(3, built.Count, "rolls 1, 2 and 5 survive - the throw and the null are skipped");
            CollectionAssert.AllItemsAreNotNull(built);
            CollectionAssert.AllItemsAreUnique(built);

            Assert.AreEqual(0, ThreadDungeonRewardSpawner.RollCacheItems(5, null, "test-run").Count, "a null roll builds nothing");
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.RollCacheItems(0, () => ThreadLootTestFixtures.Item(), "test-run").Count);

            // The guard is on the SHIPPED path, not only on a helper a test can reach.
            StringAssert.Contains(Src(),
                "return RollCacheItems(count, () => LootGenerationFactory.CreateRandomLootObjects(cacheProfile, TreasureItemCategory.MagicItem), run);");
            StringAssert.Contains(PooledLootSourceText.MethodBody(Src(), "internal static List<WorldObject> RollCacheItems(int count, Func<WorldObject> roll, object runLabel)"),
                "continue;");
        }

        /// <summary>
        /// The reason the cap is parameterised. Left at the single-cache 100, a pooled build would have started
        /// clamping around eight seats and quietly paid a full fellowship a fifth of its boss items - the failure
        /// that made this change worth stopping for. These are the numbers that would have shipped.
        /// </summary>
        [TestMethod]
        public void The_roll_cap_no_longer_binds_on_a_pooled_build()
        {
            const long Base = ThreadDungeonRewardSpawner.DefaultBossCacheLootCount;

            foreach (var seats in new[] { 9, 20 })
            {
                var uncapped = Math.Ceiling(Base * seats * B(seats));
                var pooled = ThreadDungeonRewardSpawner.ScaledLootCount(Base, seats * B(seats),
                    ThreadDungeonRewardSpawner.DefaultLootCountCap * seats);

                Assert.AreEqual((int)uncapped, pooled, $"N={seats}: the seats-scaled cap does not bind");

                var wouldHaveClamped = ThreadDungeonRewardSpawner.ScaledLootCount(Base, seats * B(seats));
                Assert.AreEqual(ThreadDungeonRewardSpawner.DefaultLootCountCap, wouldHaveClamped,
                    $"N={seats}: the single-cache cap WOULD have bound");
                Assert.IsTrue(wouldHaveClamped < pooled);
            }

            // N=9 with the old cap: 100 items over nine seats is 11.1 each against a target of 14, a 21% shortfall.
            Assert.AreEqual(11.1, ThreadDungeonRewardSpawner.DefaultLootCountCap / 9.0, 0.05);
            Assert.AreEqual(14, SoloTimesB(Base, 1.0, 9));

            // N=20: 5 each against 15, a 67% shortfall.
            Assert.AreEqual(5.0, ThreadDungeonRewardSpawner.DefaultLootCountCap / 20.0, 1e-9);
            Assert.AreEqual(15, SoloTimesB(Base, 1.0, 20));
        }

        /// <summary>
        /// The default cap is unchanged in value and in meaning for every existing caller: the two-argument
        /// ScaledLootCount is the three-argument one at DefaultLootCountCap, and a negative cap reads as zero.
        /// </summary>
        [TestMethod]
        public void The_default_roll_cap_is_unchanged_for_every_existing_caller()
        {
            Assert.AreEqual(100, ThreadDungeonRewardSpawner.DefaultLootCountCap);

            foreach (var mult in new[] { 0.5, 1.0, 2.0, 7.5, 50.0 })
            {
                Assert.AreEqual(
                    ThreadDungeonRewardSpawner.ScaledLootCount(10, mult, ThreadDungeonRewardSpawner.DefaultLootCountCap),
                    ThreadDungeonRewardSpawner.ScaledLootCount(10, mult),
                    $"mult {mult}");
            }

            Assert.AreEqual(0, ThreadDungeonRewardSpawner.ScaledLootCount(10, 1.0, -5), "a negative cap reads as zero");
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.ScaledLootCount(0, 1.0, 500), "a base of zero still rolls nothing");
            Assert.AreEqual(5, ThreadDungeonRewardSpawner.ScaledLootCount(10, 1.0, 5), "an explicit cap still binds");
        }

        /// <summary>
        /// The two halves of the group bonus, by their step lists. The duplicated half is the currency and the
        /// salvage; the pooled half is the Legendary rolls; the SOLO builder still carries all three and is
        /// untouched, which is what keeps a solo boss cache identical.
        /// </summary>
        [TestMethod]
        public void The_group_bonus_splits_into_currency_plus_salvage_and_the_item_rolls()
        {
            var src = Src();
            var perSeat = PooledLootSourceText.MethodBody(src, "public static List<WorldObject> BuildBossBonusPerSeatItems(ThreadDungeonRun run, double rewardBonus)");

            StringAssert.Contains(perSeat, "AddBonusTradeNotes(run, items, rewardBonus);");
            StringAssert.Contains(perSeat, "AddBonusTradeNotes(run, items);");
            StringAssert.Contains(perSeat, "AddBonusSalvageBags(run, items);");
            Assert.IsFalse(perSeat.Contains("AddBonusLegendaryItems"), "the item rolls are not duplicated per seat any more");

            StringAssert.Contains(src,
                "public static List<WorldObject> BuildBossBonusPooledRolls(ThreadDungeonRun run, double factor, int cap)\n"
                + "            => run == null ? new List<WorldObject>() : BuildCacheLoot(run, factor, cap);");

            // The solo path is unchanged: all three steps, in order.
            var solo = PooledLootSourceText.MethodBody(src, "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run)");
            StringAssert.Contains(solo, "AddBonusTradeNotes(run, items);");
            StringAssert.Contains(solo, "AddBonusSalvageBags(run, items);");
            StringAssert.Contains(solo, "AddBonusLegendaryItems(run, items);");

            Assert.AreEqual(0, ThreadDungeonRewardSpawner.BuildBossBonusPerSeatItems(null, 1.25).Count, "a null run builds nothing");
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.BuildBossBonusPooledRolls(null, 3.3, 300).Count);
        }

        /// <summary>
        /// Salvage bags never receive B, and that is deliberate-as-shipped rather than an oversight. Pinned so a
        /// future reader does not "fix" it: scaling them is a separate balance decision nobody has made.
        /// </summary>
        [TestMethod]
        public void Salvage_bags_are_deliberately_unscaled()
        {
            var src = Src();

            StringAssert.Contains(src, "private static void AddBonusSalvageBags(ThreadDungeonRun run, List<WorldObject> items) => items.AddRange(BuildSalvageBags(run));");
            Assert.AreEqual(1, src.Split(new[] { "AddBonusSalvageBags(ThreadDungeonRun" }, StringSplitOptions.None).Length - 1,
                "exactly one salvage step, with no scaled overload beside it");

            var scaled = PooledLootSourceText.MethodBody(src, "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run, double rewardBonus)");
            StringAssert.Contains(scaled, "AddBonusSalvageBags(run, items);");
            Assert.IsFalse(scaled.Contains("AddBonusSalvageBags(run, items, rewardBonus)"));
        }

        [TestMethod]
        public void Bonus_for_a_null_run_is_empty()
        {
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.BuildBossBonusItems(null).Count);
        }

        [TestMethod]
        public void Bonus_order_is_notes_then_bags_then_legendary_items_each_a_named_step()
        {
            var src = Src();
            var body = PooledLootSourceText.MethodBody(src, "public static List<WorldObject> BuildBossBonusItems(ThreadDungeonRun run)");

            var note = body.IndexOf("AddBonusTradeNotes(run, items);", StringComparison.Ordinal);
            var bags = body.IndexOf("AddBonusSalvageBags(run, items);", StringComparison.Ordinal);
            var loot = body.IndexOf("AddBonusLegendaryItems(run, items);", StringComparison.Ordinal);

            Assert.IsTrue(note >= 0 && note < bags && bags < loot, "MMD slot 1, bags next, Legendary-table items last (#1118)");
            Assert.IsFalse(body.Contains("PropertyManager"), "user ruling 2026-09-14: the pooled bonus reads no switch");

            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "private static void AddBonusTradeNotes(ThreadDungeonRun run, List<WorldObject> items)"), "BuildMmdNote(run, out _)");
            StringAssert.Contains(src, "private static void AddBonusSalvageBags(ThreadDungeonRun run, List<WorldObject> items) => items.AddRange(BuildSalvageBags(run));");
            StringAssert.Contains(src, "private static void AddBonusLegendaryItems(ThreadDungeonRun run, List<WorldObject> items) => items.AddRange(BuildCacheLoot(run));");
        }

        [TestMethod]
        public void Boss_cache_keeps_its_latch_fill_order_and_enter_world_sequence()
        {
            var body = PooledLootSourceText.MethodBody(Src(), "public static void TrySpawnBossCache(ThreadDungeonRun run)");

            var enabled = body.IndexOf("dynamic_dungeons_boss_chest_enabled", StringComparison.Ordinal);
            var latch = body.IndexOf("run.TryClaimBossChest()", StringComparison.Ordinal);
            var create = body.IndexOf("CreateCacheChest(run, \"boss cache\")", StringComparison.Ordinal);
            var mmd = body.IndexOf("FillMmd(run, chest, ref nextPlacement)", StringComparison.Ordinal);
            var salvage = body.IndexOf("FillSalvage(run, chest, ref nextPlacement)", StringComparison.Ordinal);
            var loot = body.IndexOf("FillLoot(run, chest, ref nextPlacement)", StringComparison.Ordinal);
            var enter = body.IndexOf("chest.EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(enabled >= 0 && enabled < latch && latch < create && create < mmd && mmd < salvage && salvage < loot && loot < enter);
            StringAssert.Contains(body, "A Thread Cache has formed where the boss fell.");
            Assert.IsFalse(body.Contains("Ethereal"), "user ruling 2026-09-14: only pooled caches are ethereal; the boss-death cache stays solid");
        }

        [TestMethod]
        public void Cache_chest_factory_carries_every_stamp()
        {
            var body = PooledLootSourceText.MethodBody(Src(), "internal static Chest CreateCacheChest(ThreadDungeonRun run, string purpose)");

            // The boss path passes "boss cache", so its error line reads exactly as it did before the extraction.
            StringAssert.Contains(body, "log.Error($\"[DYNDUNGEON] {run} {purpose} wcid {BossCacheWcid} did not resolve to a Chest; no cache spawned\");");
            Assert.IsFalse(body.Contains("Written BEFORE EnterWorld"), "the factory has no EnterWorld of its own; the moved comment names the caller's");
            StringAssert.Contains(body, "WorldObjectFactory.CreateNewWorldObject(BossCacheWcid)");
            StringAssert.Contains(body, "SetProperty(PropertyInt.ThreadDungeonRunId, (int)run.RunId)");
            StringAssert.Contains(body, "P_DungeonCacheOwnerGuid = run.OwnerGuid");
            StringAssert.Contains(body, "TimeToRot = -1");
            StringAssert.Contains(body, "SetProperty(PropertyFloat.ResetInterval, double.PositiveInfinity)");
            StringAssert.Contains(body, "ApplyCacheName(chest, run.OwnerName);");
            Assert.IsFalse(body.Contains("Ethereal"), "the shared factory also builds the OFF-path boss cache, so ethereal is set by the pooled placer, not here");
        }

        /// <summary>
        /// Owner ruling, 2026-09-17: "Chests should be labeled '[Name]'s Thread Cache' instead of just Thread Cache."
        /// The possessive has ONE definition, and this drives it rather than pinning a spelling at two call sites.
        ///
        /// The blank-holder branch is the one that matters most: the per-member overload looks a guid up on the
        /// roster and can miss, and the thing it must never produce is a headless "'s Thread Cache".
        /// </summary>
        [TestMethod]
        public void A_cache_is_named_for_its_holder_and_never_headless()
        {
            Assert.AreEqual("Awfulwaffle's Thread Cache",
                ThreadDungeonRewardSpawner.ComposeCacheName("Awfulwaffle", ThreadDungeonRewardSpawner.CacheBaseName));

            // An admin's name carries its own prefix, and that is the name they are known by: pass it through.
            Assert.AreEqual("+Corma Lan's Thread Cache",
                ThreadDungeonRewardSpawner.ComposeCacheName("+Corma Lan", ThreadDungeonRewardSpawner.CacheBaseName));

            // A name already ending in s or S takes a bare apostrophe, not "s's". Pinned so the choice is explicit.
            Assert.AreEqual("Kess' Thread Cache", ThreadDungeonRewardSpawner.ComposeCacheName("Kess", ThreadDungeonRewardSpawner.CacheBaseName));
            Assert.AreEqual("BORS' Thread Cache", ThreadDungeonRewardSpawner.ComposeCacheName("BORS", ThreadDungeonRewardSpawner.CacheBaseName));

            // No holder: the plain name, never a headless possessive.
            foreach (var missing in new[] { null, string.Empty, "   " })
            {
                var composed = ThreadDungeonRewardSpawner.ComposeCacheName(missing, ThreadDungeonRewardSpawner.CacheBaseName);

                Assert.AreEqual(ThreadDungeonRewardSpawner.CacheBaseName, composed, $"holder '{missing ?? "null"}'");
                Assert.IsFalse(composed.StartsWith("'", StringComparison.Ordinal));
            }

            // Surrounding whitespace on a holder name never reaches the label.
            Assert.AreEqual("Ann's Thread Cache", ThreadDungeonRewardSpawner.ComposeCacheName("  Ann  ", ThreadDungeonRewardSpawner.CacheBaseName));

            // A missing base name is handed back untouched rather than turned into a possessive of nothing.
            Assert.IsNull(ThreadDungeonRewardSpawner.ComposeCacheName("Ann", null));
            Assert.AreEqual("   ", ThreadDungeonRewardSpawner.ComposeCacheName("Ann", "   "));
        }

        /// <summary>
        /// The label is rebuilt from the CONSTANT, never from the chest's current name, so the per-member overload
        /// can re-label a chest the factory already labelled. Composing from the instance would have produced
        /// "Member's Owner's Thread Cache" on every group cache.
        /// </summary>
        [TestMethod]
        public void Relabelling_a_named_cache_does_not_stack_possessives()
        {
            var first = ThreadDungeonRewardSpawner.ComposeCacheName("Owner", ThreadDungeonRewardSpawner.CacheBaseName);
            var second = ThreadDungeonRewardSpawner.ComposeCacheName("Member", ThreadDungeonRewardSpawner.CacheBaseName);

            Assert.AreEqual("Owner's Thread Cache", first);
            Assert.AreEqual("Member's Thread Cache", second);
            Assert.IsFalse(second.Contains("Owner"), "the second label replaces the first outright");

            var src = Src();

            StringAssert.Contains(src,
                "private static void ApplyCacheName(Chest chest, string holderName)\n"
                + "            => chest.SetProperty(PropertyString.Name, ComposeCacheName(holderName, CacheBaseName));");

            var member = PooledLootSourceText.MethodBody(src, "internal static Chest CreateCacheChest(ThreadDungeonRun run, string purpose, uint ownerGuid)");

            StringAssert.Contains(member, "chest.P_DungeonCacheOwnerGuid = ownerGuid;");
            StringAssert.Contains(member, "ApplyCacheName(chest, run.GetMember(ownerGuid)?.Name);");
        }

        /// <summary>
        /// The label is composed from a code constant, so that constant and the weenie row it stands for are
        /// checked against each other here rather than drifting silently. Reads the shipped SQL.
        /// </summary>
        [TestMethod]
        public void The_cache_base_name_matches_the_shipped_weenie_row()
        {
            var sql = PooledLootSourceText.Read("Content/sql/weenies/1003603 Thread Cache.sql");

            StringAssert.Contains(sql, $"(1003603,   1, '{ThreadDungeonRewardSpawner.CacheBaseName}') /* Name */");
            Assert.AreEqual("Thread Cache", ThreadDungeonRewardSpawner.CacheBaseName);

            // The LongDesc used to say the cache answers to "the adventurer whose thread opened this dungeon",
            // which was wrong for a group member's cache before this label existed and is wrong twice over now.
            Assert.IsFalse(sql.Contains("whose thread opened this dungeon"), "the owner-only wording is corrected");
            StringAssert.Contains(sql, "whose name it bears");
        }

        /// <summary>
        /// Chest.cs's owner-only refusal is ONE string with TWO users, and the Thread cache's name became a
        /// possessive. "The Awfulwaffle's Thread Cache answers only to..." is ungrammatical, so the article is
        /// dropped; both readings are pinned here so a later edit for one caller cannot quietly break the other.
        /// </summary>
        [TestMethod]
        public void The_owner_only_refusal_reads_for_a_possessive_name_and_for_the_digsite_chest()
        {
            var chest = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Chest.cs");

            StringAssert.Contains(chest, "player.SendTransientError($\"{Name} answers only to the adventurer it opened for.\");");
            Assert.IsFalse(chest.Contains("$\"The {Name} answers only"), "the leading article is gone");

            // The two readings, spelled out. The digsite chest is wcid 1004150, "Unearthed Cache".
            const string tail = " answers only to the adventurer it opened for.";

            Assert.AreEqual("Awfulwaffle's Thread Cache answers only to the adventurer it opened for.",
                ThreadDungeonRewardSpawner.ComposeCacheName("Awfulwaffle", ThreadDungeonRewardSpawner.CacheBaseName) + tail);

            Assert.AreEqual("Unearthed Cache answers only to the adventurer it opened for.", "Unearthed Cache" + tail);

            var digsite = PooledLootSourceText.Read("Content/sql/weenies/1004150 Unearthed Cache.sql");
            StringAssert.Contains(digsite, "(1004150,   1, 'Unearthed Cache') /* Name */");
        }

        [TestMethod]
        public void Fill_methods_add_builder_output_at_increasing_positions()
        {
            var src = Src();

            var mmd = PooledLootSourceText.MethodBody(src, "private static int FillMmd(ThreadDungeonRun run, Chest chest, ref int nextPlacement)");
            StringAssert.Contains(mmd, "BuildMmdNote(run, out var count)");
            StringAssert.Contains(mmd, "chest.TryAddToInventory(note, nextPlacement)");

            var salvage = PooledLootSourceText.MethodBody(src, "private static int FillSalvage(ThreadDungeonRun run, Chest chest, ref int nextPlacement)");
            StringAssert.Contains(salvage, "BuildSalvageBags(run)");
            StringAssert.Contains(salvage, "chest.TryAddToInventory(bag, nextPlacement)");

            var loot = PooledLootSourceText.MethodBody(src, "private static int FillLoot(ThreadDungeonRun run, Chest chest, ref int nextPlacement)");
            StringAssert.Contains(loot, "BuildCacheLoot(run)");
            StringAssert.Contains(loot, "chest.TryAddToInventory(item, nextPlacement)");
        }
    }
}
