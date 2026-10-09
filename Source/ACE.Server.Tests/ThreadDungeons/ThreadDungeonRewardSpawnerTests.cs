using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Database.Models.World;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables;
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
        /// current DungeonGemSpec.MaxGemLevel and is the number a player actually sees today; 350 and 100 are the
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
        /// anchor sits at 300 while DungeonGemSpec.MaxGemLevel is 275 precisely so that raising the gem ceiling
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

        // ------------------------------------------------------------------------------------------------
        // BuildCacheProfile - the retail Legendary Chest's magic-item table swapped in over the run profile.
        // ------------------------------------------------------------------------------------------------

        private static TreasureDeath FullRunProfile() => new TreasureDeath
        {
            Id = 999,
            TreasureType = 111,
            Tier = 6,
            LootQualityMod = 0.42f,
            UnknownChances = 21,
            ItemChance = 100,
            ItemMinAmount = 1,
            ItemMaxAmount = 3,
            ItemTreasureTypeSelectionChances = 8,
            MagicItemChance = 100,
            MagicItemMinAmount = 1,
            MagicItemMaxAmount = 4,
            MagicItemTreasureTypeSelectionChances = 8,
            MundaneItemChance = 100,
            MundaneItemMinAmount = 1,
            MundaneItemMaxAmount = 2,
            MundaneItemTypeSelectionChances = 7,
            LastModified = new DateTime(2026, 1, 1),
        };

        /// <summary>
        /// Every public property except MagicItemTreasureTypeSelectionChances must survive the copy
        /// unchanged. Reflection-driven so a field added to TreasureDeath later is covered automatically,
        /// rather than this test silently passing while a new field is never actually verified.
        /// </summary>
        [TestMethod]
        public void BuildCacheProfile_copies_every_field_except_the_magic_item_profile()
        {
            var runProfile = FullRunProfile();
            var cacheProfile = ThreadDungeonRewardSpawner.BuildCacheProfile(runProfile);

            Assert.IsNotNull(cacheProfile);
            Assert.AreNotSame(runProfile, cacheProfile, "must be a new instance");
            Assert.AreEqual(ThreadDungeonRewardSpawner.CacheMagicItemProfile, cacheProfile.MagicItemTreasureTypeSelectionChances);

            foreach (var prop in typeof(TreasureDeath).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name == nameof(TreasureDeath.MagicItemTreasureTypeSelectionChances))
                    continue;

                var expected = prop.GetValue(runProfile);
                var actual = prop.GetValue(cacheProfile);
                Assert.AreEqual(expected, actual, $"field {prop.Name} must be copied unchanged");
            }
        }

        [TestMethod]
        public void BuildCacheProfile_does_not_mutate_the_run_profile()
        {
            var runProfile = FullRunProfile();
            var snapshotMagicChances = runProfile.MagicItemTreasureTypeSelectionChances;

            ThreadDungeonRewardSpawner.BuildCacheProfile(runProfile);

            Assert.AreEqual(snapshotMagicChances, runProfile.MagicItemTreasureTypeSelectionChances,
                "the run profile is shared with every run creature's DeathTreasureOverride and must never be edited in place");
        }

        [TestMethod]
        public void BuildCacheProfile_null_in_null_out()
        {
            Assert.IsNull(ThreadDungeonRewardSpawner.BuildCacheProfile(null));
        }

        /// <summary>
        /// The cache's item table (profile 22, the retail Legendary Chest's) must never roll a Scroll, Gem,
        /// or ArtObject - the low-value types this whole change exists to exclude - and every draw must land
        /// in the six types profile 22 actually declares. 5000 draws is comfortably enough to catch a single
        /// misrouted entry in a six-way table without the test being flaky.
        /// </summary>
        [TestMethod]
        public void Cache_magic_item_profile_never_rolls_low_value_types()
        {
            var allowed = new HashSet<TreasureItemType>
            {
                TreasureItemType.Weapon,
                TreasureItemType.Armor,
                TreasureItemType.Clothing,
                TreasureItemType.Jewelry,
                TreasureItemType.Cloak,
                TreasureItemType.PetDevice,
            };

            var excluded = new HashSet<TreasureItemType>
            {
                TreasureItemType.Scroll,
                TreasureItemType.Gem,
                TreasureItemType.ArtObject,
            };

            for (var i = 0; i < 5000; i++)
            {
                var roll = TreasureProfile_MagicItem.Roll(ThreadDungeonRewardSpawner.CacheMagicItemProfile);

                Assert.IsFalse(excluded.Contains(roll), $"draw {i} produced excluded type {roll}");
                Assert.IsTrue(allowed.Contains(roll), $"draw {i} produced {roll}, outside profile 22's declared types");
            }
        }

        // ------------------------------------------------------------------------------------------------
        // CacheSalvageMaterials - which materials the cache carries a full bag of.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Cache_salvage_materials_empty_when_no_affinities()
        {
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.CacheSalvageMaterials(null).Count, "null affinities");
            Assert.AreEqual(0, ThreadDungeonRewardSpawner.CacheSalvageMaterials(
                new List<(int, uint, double)>()).Count, "empty affinities");
        }

        /// <summary>
        /// A literal stand-in for Player.MaterialSalvage, with the same shape at the three entries these
        /// tests touch (12 Amethyst -> 21036, 23 Green Garnet -> 21050, 3 Cloth -> 0 header). Passed
        /// explicitly rather than letting CacheSalvageMaterials default to the real table, because touching
        /// ANY static member of Player - including just reading the dictionary - runs Player's static
        /// constructor, which loads a weenie through WorldDatabase and throws under this harness (verified:
        /// System.TypeInitializationException on ACE.Server.WorldObjects.Player, from WorldDbContext.OnConfiguring
        /// via Player_Location.cs's static ctor, with no world DB configured).
        /// </summary>
        private static readonly Dictionary<int, int> TestMaterialSalvage = new Dictionary<int, int>
        {
            { 3, 0 },       // Cloth - category header, no real bag
            { 12, 21036 },  // Amethyst
            { 23, 21050 },  // Green Garnet
        };

        [TestMethod]
        public void Cache_salvage_materials_returns_the_one_targeted_material()
        {
            var affinities = new List<(int MaterialId, uint BaseWcid, double Chance)> { (12, 12007, 0.1) };

            var materials = ThreadDungeonRewardSpawner.CacheSalvageMaterials(affinities, TestMaterialSalvage);

            CollectionAssert.AreEqual(new[] { 12 }, materials.ToList());
        }

        [TestMethod]
        public void Cache_salvage_materials_collapses_duplicates_and_preserves_order()
        {
            // 12 Amethyst, 23 Green Garnet, 12 again - the duplicate must not produce a second bag, and the
            // surviving order must be the gem's own modifier order (12 before 23), not sorted.
            var affinities = new List<(int MaterialId, uint BaseWcid, double Chance)>
            {
                (12, 12007, 0.1),
                (23, 12008, 0.05),
                (12, 12007, 0.1),
            };

            var materials = ThreadDungeonRewardSpawner.CacheSalvageMaterials(affinities, TestMaterialSalvage);

            CollectionAssert.AreEqual(new[] { 12, 23 }, materials.ToList());
        }

        [TestMethod]
        public void Cache_salvage_materials_skips_an_unmapped_material()
        {
            // 3 = Cloth, a category header with wcid 0 - never a real bag.
            var affinities = new List<(int MaterialId, uint BaseWcid, double Chance)>
            {
                (3, 0, 0.1),
                (12, 12007, 0.1),
            };

            var materials = ThreadDungeonRewardSpawner.CacheSalvageMaterials(affinities, TestMaterialSalvage);

            CollectionAssert.AreEqual(new[] { 12 }, materials.ToList());
        }

        // ------------------------------------------------------------------------------------------------
        // MarkRewardProfile - the salvage-affinity half of the write-once stamp.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Reward_profile_salvage_affinities_default_to_empty()
        {
            var run = NewRun();
            Assert.AreEqual(0, run.SalvageAffinities.Count, "unstamped");
        }

        [TestMethod]
        public void Reward_profile_salvage_affinities_are_write_once()
        {
            var run = NewRun();

            var first = new List<(int MaterialId, uint BaseWcid, double Chance)> { (12, 12007, 0.1) };
            var second = new List<(int MaterialId, uint BaseWcid, double Chance)> { (23, 12008, 0.2) };

            run.MarkRewardProfile(new TreasureDeath { Tier = 6 }, 2.0, first);
            CollectionAssert.AreEqual(new[] { 12 }, run.SalvageAffinities.Select(a => a.MaterialId).ToList());

            // A second stamp - same call as an existing two-arg caller with a third argument added - must
            // not overwrite the first, exactly like LootProfile and LootQuantityMult above.
            run.MarkRewardProfile(new TreasureDeath { Tier = 1 }, 9.0, second);
            CollectionAssert.AreEqual(new[] { 12 }, run.SalvageAffinities.Select(a => a.MaterialId).ToList());
        }

        /// <summary>
        /// The pre-existing two-argument call form (every earlier test in this file, and the run's own
        /// unaffiliated callers) must still compile and behave exactly as before now that a third optional
        /// parameter exists.
        /// </summary>
        [TestMethod]
        public void Reward_profile_two_argument_form_still_leaves_salvage_affinities_empty()
        {
            var run = NewRun();
            run.MarkRewardProfile(new TreasureDeath { Tier = 6 }, 2.5);

            Assert.AreEqual(2.5, run.LootQuantityMult);
            Assert.AreEqual(0, run.SalvageAffinities.Count);
        }
    }
}
