using System;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The boss cache's arithmetic and its exactly-once latch.
    ///
    /// Everything asserted here is derived from the pure functions and their SHIPPED default anchors passed
    /// in as literals, never from PropertyManager: PropertyManager reads throw under this harness, which is
    /// exactly why MmdMaxForLevel and ScaledLootCount take their tunables as parameters instead of reading
    /// them. The defaults are pinned separately below, so a change to one of them fails a named test rather
    /// than silently re-baselining every expectation in the file.
    /// </summary>
    [TestClass]
    public class ThreadDungeonRewardSpawnerTests
    {
        // The shipped anchors, as the PropertyManager rows declare them.
        private const int LowLevel = 185;
        private const int LowMax = 2;
        private const int HighLevel = 300;
        private const int HighMax = 5;

        private static int Mmd(int gemLevel) => ThreadDungeonRewardSpawner.MmdMaxForLevel(gemLevel, LowLevel, LowMax, HighLevel, HighMax);

        private static ThreadDungeonRun NewRun()
        {
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        /// <summary>
        /// The constants the PropertyManager rows are built from. Pinned so that moving a default is a
        /// deliberate two-file edit rather than something that quietly re-baselines the curve below.
        /// </summary>
        [TestMethod]
        public void Shipped_mmd_anchors_are_185_2_and_300_5()
        {
            Assert.AreEqual(185L, ThreadDungeonRewardSpawner.DefaultMmdLowLevel);
            Assert.AreEqual(2L, ThreadDungeonRewardSpawner.DefaultMmdLowMax);
            Assert.AreEqual(300L, ThreadDungeonRewardSpawner.DefaultMmdHighLevel);
            Assert.AreEqual(5L, ThreadDungeonRewardSpawner.DefaultMmdHighMax);
            Assert.AreEqual(10L, ThreadDungeonRewardSpawner.DefaultBossCacheLootCount);
        }

        /// <summary>
        /// The five points the design fixes the line by. 185 and 300 are the anchors themselves; 275 is the
        /// current DungeonGemSpec.MaxLevel and is the number a player actually sees today; 350 and 100 are the
        /// two extrapolations, and they are the whole reason the clamp is one-sided.
        /// </summary>
        [TestMethod]
        public void Mmd_max_follows_the_shipped_anchors()
        {
            // 2 + 3 * (185-185)/115 = 2
            Assert.AreEqual(2, Mmd(185), "at the low anchor");

            // 2 + 3 * (275-185)/115 = 4.347..., floored
            Assert.AreEqual(4, Mmd(275), "at the current gem-level ceiling");

            // 2 + 3 * (300-185)/115 = 5
            Assert.AreEqual(5, Mmd(300), "at the high anchor");
        }

        /// <summary>
        /// Above the high anchor the line KEEPS RISING. This is the half a ceiling clamp would break: the
        /// anchor sits at 300 while DungeonGemSpec.MaxLevel is 275 precisely so that raising the gem ceiling
        /// later does not need this curve edited, and a clamp at highMax would flatten it the moment it did.
        /// </summary>
        [TestMethod]
        public void Mmd_max_is_not_clamped_above_the_high_anchor()
        {
            // 2 + 3 * (350-185)/115 = 6.304..., floored
            Assert.AreEqual(6, Mmd(350));
            Assert.IsTrue(Mmd(350) > Mmd(300), "the line must still be rising past the high anchor");
        }

        /// <summary>
        /// Below the low anchor the line is FLOORED at lowMax. Extrapolating downward would put a level-100
        /// gem at 2 + 3 * (100-185)/115 = -0.217, i.e. a floored -1, and a negative stack top would make the
        /// 1..max roll throw rather than pay nothing.
        /// </summary>
        [TestMethod]
        public void Mmd_max_is_clamped_below_at_the_low_anchor()
        {
            Assert.AreEqual(2, Mmd(100));
            Assert.AreEqual(2, Mmd(1));
            Assert.AreEqual(2, Mmd(0));
        }

        /// <summary>
        /// A degenerate span has no slope, and dividing by it would produce Infinity or NaN and then throw at
        /// the (int) cast. The guard returns the low anchor, which is already the function's floor at every
        /// other level.
        /// </summary>
        [TestMethod]
        public void Mmd_max_guards_a_zero_width_anchor_span()
        {
            Assert.AreEqual(2, ThreadDungeonRewardSpawner.MmdMaxForLevel(275, 200, 2, 200, 5), "above the coincident anchors");
            Assert.AreEqual(2, ThreadDungeonRewardSpawner.MmdMaxForLevel(200, 200, 2, 200, 5), "on them");
            Assert.AreEqual(2, ThreadDungeonRewardSpawner.MmdMaxForLevel(10, 200, 2, 200, 5), "below them");
        }

        /// <summary>
        /// The loot count is ceil(base * mult), so a fractional multiplier rounds UP - a gem that bought a
        /// 1.05x quantity axis is not allowed to buy nothing. The neutral multiplier must be exactly
        /// transparent, and the 3.0 case is the one that would have gone to 31 without the 1e-9 absorber if
        /// the product came out a hair over.
        /// </summary>
        [TestMethod]
        public void Scaled_loot_count_rounds_up()
        {
            Assert.AreEqual(10, ThreadDungeonRewardSpawner.ScaledLootCount(10, 1.0), "neutral multiplier");
            Assert.AreEqual(11, ThreadDungeonRewardSpawner.ScaledLootCount(10, 1.05));
            Assert.AreEqual(15, ThreadDungeonRewardSpawner.ScaledLootCount(10, 1.5));
            Assert.AreEqual(16, ThreadDungeonRewardSpawner.ScaledLootCount(10, 1.51));
            Assert.AreEqual(30, ThreadDungeonRewardSpawner.ScaledLootCount(10, 3.0), "the shipped loot-quantity cap");
        }

        /// <summary>
        /// The three ways the inputs can be nonsense, and what each degrades to. A zero or negative base is
        /// the admin's own "no loot, notes only" setting and is honoured; a garbled multiplier reads as the
        /// neutral 1.0 rather than zeroing the cache; and the 100-item ceiling bounds how many rolls a
        /// landblock thread can be asked for however large a tunable is set.
        /// </summary>
        [TestMethod]
        public void Scaled_loot_count_degrades_safely()
        {
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.ScaledLootCount(0, 2.0), "an explicit zero means no loot");
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.ScaledLootCount(-5, 2.0), "a negative reads as zero");

            Assert.AreEqual(10, ThreadDungeonRewardSpawner.ScaledLootCount(10, double.NaN), "NaN reads as neutral");
            Assert.AreEqual(10, ThreadDungeonRewardSpawner.ScaledLootCount(10, 0.0), "zero reads as neutral");
            Assert.AreEqual(10, ThreadDungeonRewardSpawner.ScaledLootCount(10, -3.0), "negative reads as neutral");

            Assert.AreEqual(100, ThreadDungeonRewardSpawner.ScaledLootCount(1000, 1.0), "capped at 100 items");
            Assert.AreEqual(100, ThreadDungeonRewardSpawner.ScaledLootCount(50, 10.0), "capped after scaling too");
        }

        /// <summary>
        /// The latch that makes the cache exactly-once. Modelled on TryClaimClearedAnnouncement, and tested
        /// the same way: without it a second boss death - a plan that placed two, or a re-entrant Die - would
        /// mint a second chest full of loot.
        /// </summary>
        [TestMethod]
        public void Boss_chest_can_be_claimed_exactly_once()
        {
            var run = NewRun();

            Assert.IsTrue(run.TryClaimBossChest(), "the first caller spawns the cache");
            Assert.IsFalse(run.TryClaimBossChest(), "the second must not");
            Assert.IsFalse(run.TryClaimBossChest(), "and it stays latched");
        }

        /// <summary>
        /// Unlike the clear announcement, the cache latch carries NO state gate: a boss usually dies long
        /// before the run reaches Cleared, because the trash share of the weighted clear rule is still
        /// outstanding. A latch that required Cleared would suppress the cache in the normal case.
        /// </summary>
        [TestMethod]
        public void Boss_chest_claim_does_not_require_a_cleared_run()
        {
            var run = NewRun();
            run.MarkPopulated(planned: 10, spawned: 10, bossWcid: 42u);

            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);
            Assert.IsTrue(run.TryClaimBossChest(), "an Active run must still be able to spawn its cache");
        }

        /// <summary>
        /// The run carries the loot profile its creatures were built with, write-once, so the cache cannot
        /// drift from the copy. The unset default matters as much as the set value: a run whose plan build
        /// threw never stamps one, and the cache has to read that as "notes only" rather than as a profile.
        /// </summary>
        [TestMethod]
        public void Reward_profile_is_write_once_and_defaults_to_neutral()
        {
            var run = NewRun();

            Assert.IsNull(run.LootProfile, "unstamped");
            Assert.AreEqual(1.0, run.LootQuantityMult, "the neutral multiplier");

            var first = new ACE.Database.Models.World.TreasureDeath { Tier = 6 };
            var second = new ACE.Database.Models.World.TreasureDeath { Tier = 1 };

            run.MarkRewardProfile(first, 2.5);
            Assert.AreSame(first, run.LootProfile);
            Assert.AreEqual(2.5, run.LootQuantityMult);

            run.MarkRewardProfile(second, 9.0);
            Assert.AreSame(first, run.LootProfile, "a second stamp must not overwrite the first");
            Assert.AreEqual(2.5, run.LootQuantityMult);
        }

        /// <summary>
        /// A garbled multiplier is normalised AT THE STAMP, not at every read, so every consumer of
        /// LootQuantityMult sees a usable scalar rather than each having to re-guard it.
        /// </summary>
        [TestMethod]
        public void Reward_profile_normalises_a_garbled_multiplier()
        {
            var run = NewRun();
            run.MarkRewardProfile(new ACE.Database.Models.World.TreasureDeath { Tier = 6 }, double.NaN);

            Assert.AreEqual(1.0, run.LootQuantityMult);
        }
    }
}
