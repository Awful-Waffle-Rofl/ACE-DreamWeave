using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// ThreadCacheSort: the solo Thread Cache display order (user ruling 2026-09-21 - solo caches get the
    /// FULL composite /mule sort).
    ///
    /// Deliberately DAT-FREE. The composite key's material rung calls RecipeManager.GetMaterialName, which
    /// reads DatManager.PortalDat, and CI has no client dat files - so not one item any test here builds
    /// carries a MaterialType, every such item falls into the same empty-material run, and these cases keep
    /// discriminating on CI instead of going Inconclusive. The material rung itself is already covered by
    /// PersonalVendorTests.ForEachItem_GroupsSameMaterialFamilyByMaterialName, against the same shared key.
    ///
    /// PropertyManager seeding: SortRunCaches reads dynamic_dungeons_cache_display_sort, and an unseeded
    /// GetBool falls through to DatabaseManager.ShardConfig with no shard database here. It is seeded in
    /// TestInitialize and every test that flips it restores it in a finally, so this class passes when run
    /// alone as well as inside the full suite (CLAUDE.md, "A green full ACE.Server.Tests run does not prove
    /// a new test class is isolated").
    /// </summary>
    [TestClass]
    public class ThreadCacheSortTests
    {
        [TestInitialize]
        public void SeedTunables()
        {
            Assert.IsTrue(PropertyManager.ModifyBool(ThreadCacheSort.TunableKey, ThreadCacheSort.DefaultEnabled),
                $"{ThreadCacheSort.TunableKey} is missing from PropertyManager's DefaultBooleanProperties");
        }

        // ------------------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A sortable in-memory item. Every rung the mixed set below exercises is an optional argument, and
        /// ItemType is mandatory on purpose: an item built with no ItemType arrives as ItemType.None and sorts
        /// first, which would make an assertion about the key pass for the wrong reason.
        /// </summary>
        private static WorldObject SortItem(
            ItemType itemType,
            string name,
            int? value = null,
            WeenieType weenieType = WeenieType.Generic,
            int? weaponSkill = null,
            int? ammoType = null,
            int? validLocations = null,
            int? damageType = null,
            int? wieldRequirements = null,
            int? wieldDifficulty = null,
            int? stackSize = null,
            int? uiEffects = null,
            int? useRequiresSkillLevel = null)
        {
            var properties = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)itemType } };

            if (value.HasValue) properties[PropertyInt.Value] = value.Value;
            if (weaponSkill.HasValue) properties[PropertyInt.WeaponSkill] = weaponSkill.Value;
            if (ammoType.HasValue) properties[PropertyInt.AmmoType] = ammoType.Value;
            if (validLocations.HasValue) properties[PropertyInt.ValidLocations] = validLocations.Value;
            if (damageType.HasValue) properties[PropertyInt.DamageType] = damageType.Value;
            if (wieldRequirements.HasValue) properties[PropertyInt.WieldRequirements] = wieldRequirements.Value;
            if (wieldDifficulty.HasValue) properties[PropertyInt.WieldDifficulty] = wieldDifficulty.Value;
            if (stackSize.HasValue) properties[PropertyInt.StackSize] = stackSize.Value;
            if (uiEffects.HasValue) properties[PropertyInt.UiEffects] = uiEffects.Value;
            if (useRequiresSkillLevel.HasValue) properties[PropertyInt.UseRequiresSkillLevel] = useRequiresSkillLevel.Value;

            var weenie = new Weenie
            {
                WeenieClassId = NextGuid(),
                WeenieType = weenieType,
                PropertiesInt = properties,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new GenericObject(weenie, new ObjectGuid(NextGuid()));
        }

        /// <summary>
        /// The representative mixed set, in the order the composite key must produce. Weapons of several
        /// classes (two heavy, one light, one finesse, a bow, a caster), armor by slot (head, two chest),
        /// jewelry (neck, finger), a stackable and a rare-shaped gem.
        ///
        /// Why this is the expected order, rung by rung (see VaultDisplaySort's doc comment):
        ///   - category: weapons (1) before armor/clothing/jewelry (2) before everything else by raw
        ///     ItemType (Money 0x40 before Gem 0x800);
        ///   - within weapons, WeaponClassOrdinal: heavy(0), light(1), finesse(2), bow(11), caster(16);
        ///   - the two heavy blades tie on class AND on DamageType (both Slash), so the minimum SKILL
        ///     requirement decides, descending with absent LAST: 400 before none;
        ///   - within equipment, SlotOrdinal head(0) before chest(1) before neck(9) before finger(11);
        ///   - the two chest pieces tie on slot, so the minimum LEVEL requirement decides the same way:
        ///     150 before none.
        /// </summary>
        private static List<WorldObject> MixedSetInExpectedOrder()
        {
            var heavyWithSkillReq = SortItem(ItemType.MeleeWeapon, "Axe of Ire", value: 900, weaponSkill: (int)Skill.HeavyWeapons,
                damageType: (int)DamageType.Slash, wieldRequirements: (int)WieldRequirement.Skill, wieldDifficulty: 400);

            var heavyNoReq = SortItem(ItemType.MeleeWeapon, "Zephyr Blade", value: 120, weaponSkill: (int)Skill.HeavyWeapons,
                damageType: (int)DamageType.Slash);

            var light = SortItem(ItemType.MeleeWeapon, "Lightbringer", value: 300, weaponSkill: (int)Skill.LightWeapons,
                damageType: (int)DamageType.Pierce);

            var finesse = SortItem(ItemType.MeleeWeapon, "Quickthorn", value: 250, weaponSkill: (int)Skill.FinesseWeapons,
                damageType: (int)DamageType.Pierce);

            var bow = SortItem(ItemType.MissileWeapon, "Yew Bow", value: 400, weenieType: WeenieType.MissileLauncher,
                ammoType: (int)AmmoType.Arrow);

            var caster = SortItem(ItemType.Caster, "Wand of Frost", value: 500, weenieType: WeenieType.Caster);

            var helm = SortItem(ItemType.Armor, "Iron Helm", value: 80, validLocations: (int)EquipMask.HeadWear);

            var breastplateWithLevelReq = SortItem(ItemType.Armor, "Breastplate", value: 700, validLocations: (int)EquipMask.ChestArmor,
                wieldRequirements: (int)WieldRequirement.Level, wieldDifficulty: 150);

            var hauberkNoReq = SortItem(ItemType.Armor, "Hauberk", value: 650, validLocations: (int)EquipMask.ChestArmor);

            var amulet = SortItem(ItemType.Jewelry, "Amulet of Calm", value: 200, validLocations: (int)EquipMask.NeckWear);

            var ring = SortItem(ItemType.Jewelry, "Band of Embers", value: 210,
                validLocations: (int)(EquipMask.FingerWearLeft | EquipMask.FingerWearRight));

            var stackable = SortItem(ItemType.Money, "Pyreal", value: 5000, stackSize: 5000);

            var rare = SortItem(ItemType.Gem, "Ulgrim's Lucky Coin", value: 25000);

            return new List<WorldObject>
            {
                heavyWithSkillReq, heavyNoReq, light, finesse, bow, caster,
                helm, breastplateWithLevelReq, hauberkNoReq, amulet, ring,
                stackable, rare,
            };
        }

        /// <summary>The cache's contents by name, in the order GameEventViewContents would write them.</summary>
        private static string[] NamesInSlotOrder(Container cache)
            => cache.Inventory.Values.OrderBy(i => i.PlacementPosition ?? 0).Select(i => i.Name).ToArray();

        /// <summary>Adds items at consecutive positions, exactly as ThreadCacheFiller.Place numbers them.</summary>
        private static void PlaceInArrivalOrder(Container cache, IEnumerable<WorldObject> items)
        {
            var next = ThreadCacheFiller.NextPlacementPosition(cache);

            foreach (var item in items)
                Assert.IsTrue(cache.TryAddToInventory(item, next++), $"fixture: the cache refused {item.Name}");
        }

        private static ThreadDungeonRun SoloRunWithCache(Container cache)
        {
            var run = PooledRun();
            Assert.IsFalse(run.IsGroup, "fixture: PooledRun must be a SOLO run - the sort is solo-only");
            Assert.IsTrue(run.TryRegisterCache(cache));

            return run;
        }

        private static ThreadDungeonRun GroupRunWithCache(Container cache)
        {
            var seats = new List<RosterSeat>
            {
                new RosterSeat(0x50000010u, "Owner", 7, 150),
                new RosterSeat(0x50000020u, "Member", 8, 150),
            };

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };

            var group = GroupScaling.Compute(2, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

            var run = new ThreadDungeonRun(0x80005678u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
            run.MarkPooledLoot(true);
            Assert.IsTrue(run.IsGroup, "fixture: this must be a GROUP run");
            Assert.IsTrue(run.TryRegisterCache(cache));

            return run;
        }

        // ------------------------------------------------------------------------------------------------------
        // The order itself
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The whole composite key over a representative mixed set. The expected sequence is written out by
        /// hand from VaultDisplaySort's documented rungs (see MixedSetInExpectedOrder), NOT read back out of
        /// the code under test, so a rung that silently changes direction or drops out fails here.
        ///
        /// The items are inserted REVERSED, so the arrival order is the exact opposite of the expected one and
        /// a pass cannot be explained by insertion order leaking through.
        /// </summary>
        [TestMethod]
        public void Reorder_PutsAMixedSetIntoTheDocumentedVaultOrder()
        {
            var expected = MixedSetInExpectedOrder();
            var cache = Cache();

            PlaceInArrivalOrder(cache, Enumerable.Reverse(expected));

            CollectionAssert.AreEqual(expected.Select(i => i.Name).Reverse().ToArray(), NamesInSlotOrder(cache),
                "fixture: arrival order must be the reverse of the expected order before the sort runs");

            var renumbered = ThreadCacheSort.Reorder(cache);

            Assert.AreEqual(expected.Count, renumbered);
            CollectionAssert.AreEqual(expected.Select(i => i.Name).ToArray(), NamesInSlotOrder(cache),
                "category asc, then weapon class / armor slot, then damage type, then minimum requirement desc (absent last), then material/name/workmanship/value/guid");
        }

        /// <summary>
        /// Conservation: the reorder changes positions and nothing else. Same guids, same count, and the
        /// positions it writes are exactly 0..n-1 with no duplicate and no gap - a duplicate position would
        /// make GameEventViewContents' own OrderBy non-deterministic between two items.
        /// </summary>
        [TestMethod]
        public void Reorder_ChangesOnlyPositions_NotContents()
        {
            var items = MixedSetInExpectedOrder();
            var cache = Cache();

            PlaceInArrivalOrder(cache, Enumerable.Reverse(items));

            var before = cache.Inventory.Keys.OrderBy(g => g.Full).ToList();

            ThreadCacheSort.Reorder(cache);

            CollectionAssert.AreEqual(before, cache.Inventory.Keys.OrderBy(g => g.Full).ToList(), "the reorder must neither add nor drop an item");
            Assert.AreEqual(items.Count, cache.Inventory.Count);
            Assert.IsFalse(items.Any(i => i.IsDestroyed), "the reorder must destroy nothing");

            CollectionAssert.AreEqual(Enumerable.Range(0, items.Count).ToArray(),
                cache.Inventory.Values.Select(i => i.PlacementPosition ?? -1).OrderBy(p => p).ToArray(),
                "positions must be a dense 0..n-1 sequence with no duplicate");
        }

        // ------------------------------------------------------------------------------------------------------
        // Misc-family clustering (rungs 2-4 for items outside the weapon/equipment categories)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Essences (WeenieType.PetDevice) cluster together, ahead of the level-8 tinkering glyphs
        /// (WeenieType.Stackable) - both share ItemType.Misc, so before this change they fell through
        /// rungs 2-4 as no-ops and interleaved alphabetically. FamilyOrdinal puts PetDevice (0) before
        /// Stackable (2), so category ties on ItemType.Misc and rung 2 alone separates the two families
        /// regardless of name.
        /// </summary>
        [TestMethod]
        public void Order_ClustersEssencesAwayFromStackablesInTheSameMiscTab()
        {
            var glyphOfAlchemy = SortItem(ItemType.Misc, "Glyph of Alchemy", weenieType: WeenieType.Stackable);
            var acidChildEssence = SortItem(ItemType.Misc, "Acid Child Essence (125)", weenieType: WeenieType.PetDevice,
                uiEffects: (int)UiEffects.Acid, useRequiresSkillLevel: 430);
            var fireChildEssence = SortItem(ItemType.Misc, "Fire Child Essence (100)", weenieType: WeenieType.PetDevice,
                uiEffects: (int)UiEffects.Fire, useRequiresSkillLevel: 400);
            var powderedLead = SortItem(ItemType.Misc, "Powdered Lead", weenieType: WeenieType.Stackable);

            var items = new List<WorldObject> { glyphOfAlchemy, powderedLead, acidChildEssence, fireChildEssence };
            var ordered = VaultDisplaySort.Order(items).ToList();

            // Both essences (family 0) must precede both stackables (family 2), independent of name -
            // "Acid Child Essence" would otherwise sort before "Glyph of Alchemy" alphabetically, and
            // "Fire Child Essence" after it, which would put a stackable between the two essences under
            // a bare Name sort.
            var essenceIndexes = new[] { ordered.IndexOf(acidChildEssence), ordered.IndexOf(fireChildEssence) };
            var stackableIndexes = new[] { ordered.IndexOf(glyphOfAlchemy), ordered.IndexOf(powderedLead) };

            Assert.IsTrue(essenceIndexes.Max() < stackableIndexes.Min(),
                "both PetDevice essences must sort ahead of both Stackable glyphs within the shared Misc category");
        }

        /// <summary>
        /// Within the PetDevice family, rung 3 (UiEffects ascending) clusters same-element essences
        /// together, and rung 4 (UseRequiresSkillLevel descending, absent last) then orders within an
        /// element by the Summoning level it needs, strongest first. Fire (0x20) sorts before Frost
        /// (0x80); within Fire, the 400 essence sorts before the 300 one; the essence with no
        /// UseRequiresSkillLevel sorts LAST within its element under the descending, absent-last rule.
        /// </summary>
        [TestMethod]
        public void Order_EssencesSortByElementThenRequirementDescendingThenName()
        {
            var fire400 = SortItem(ItemType.Misc, "Fire Child Essence (100)", weenieType: WeenieType.PetDevice,
                uiEffects: (int)UiEffects.Fire, useRequiresSkillLevel: 400);
            var fire300 = SortItem(ItemType.Misc, "Fire Whelp Essence (75)", weenieType: WeenieType.PetDevice,
                uiEffects: (int)UiEffects.Fire, useRequiresSkillLevel: 300);
            var fireNoReq = SortItem(ItemType.Misc, "Fire Cub Essence (50)", weenieType: WeenieType.PetDevice,
                uiEffects: (int)UiEffects.Fire);
            var frost350 = SortItem(ItemType.Misc, "Frost Child Essence (100)", weenieType: WeenieType.PetDevice,
                uiEffects: (int)UiEffects.Frost, useRequiresSkillLevel: 350);

            var expected = new[] { fire400, fire300, fireNoReq, frost350 };
            var items = new List<WorldObject> { frost350, fireNoReq, fire300, fire400 };

            var ordered = VaultDisplaySort.Order(items).ToList();

            CollectionAssert.AreEqual(expected.Select(i => i.Name).ToArray(), ordered.Select(i => i.Name).ToArray(),
                "Fire (element asc) before Frost; within Fire, requirement desc with the absent one LAST");
        }

        /// <summary>
        /// A WeenieType not in VaultDisplayOrder's curated FamilyOrder list (e.g. Coin) is NOT special-
        /// cased into an arbitrary position - it sorts, deterministically, after every curated family
        /// (PetDevice .. Scroll) that shares its ItemType category, per FamilyOrdinal's 1000+ offset.
        /// </summary>
        [TestMethod]
        public void Order_AnUncuratedWeenieTypeSortsAfterEveryCuratedFamily()
        {
            var essence = SortItem(ItemType.Misc, "Acid Child Essence (125)", weenieType: WeenieType.PetDevice, uiEffects: (int)UiEffects.Acid);
            var glyph = SortItem(ItemType.Misc, "Glyph of Alchemy", weenieType: WeenieType.Stackable);
            var scroll = SortItem(ItemType.Misc, "Scroll of Recall", weenieType: WeenieType.Scroll);
            var uncurated = SortItem(ItemType.Misc, "Aardvark Token", weenieType: WeenieType.Coin);

            var items = new List<WorldObject> { uncurated, scroll, glyph, essence };
            var ordered = VaultDisplaySort.Order(items).ToList();

            var uncuratedIndex = ordered.IndexOf(uncurated);

            Assert.IsTrue(uncuratedIndex > ordered.IndexOf(essence), "Coin (uncurated) must sort after PetDevice (curated)");
            Assert.IsTrue(uncuratedIndex > ordered.IndexOf(glyph), "Coin (uncurated) must sort after Stackable (curated)");
            Assert.IsTrue(uncuratedIndex > ordered.IndexOf(scroll), "Coin (uncurated) must sort after Scroll (curated), even though 'Aardvark' is alphabetically first");
        }

        /// <summary>
        /// Code review finding (b38e2b1d6): rung 3's PetDevice branch lacked the EquipmentCategory
        /// guard rung 4 already had, so an item carrying BOTH an equipment ItemType bit AND
        /// WeenieType.PetDevice would key on UiEffects instead of the equipment no-op. Two items,
        /// same slot (HeadWear) and same ItemType.Armor category, both WeenieType.PetDevice, no wield
        /// requirement (so rung 4 ties too): "Zed Charm" carries the LOWER UiEffects (Fire=0x20),
        /// "Alpha Charm" the HIGHER (Acid=0x100). Hand-derived expectation WITH the guard: rungs 2-4
        /// all tie (same slot, equipment no-op 0, both requirements absent), so rung 6 (Name) decides
        /// and "Alpha Charm" sorts first. WITHOUT the guard, rung 3 would key 0x20 vs 0x100 and put
        /// "Zed Charm" first instead - the wrong order this test pins against.
        /// </summary>
        [TestMethod]
        public void Order_EquipmentCategoryPetDeviceIgnoresUiEffects_SortsAsOrdinaryEquipment()
        {
            var zedCharm = SortItem(ItemType.Armor, "Zed Charm", weenieType: WeenieType.PetDevice,
                validLocations: (int)EquipMask.HeadWear, uiEffects: (int)UiEffects.Fire);
            var alphaCharm = SortItem(ItemType.Armor, "Alpha Charm", weenieType: WeenieType.PetDevice,
                validLocations: (int)EquipMask.HeadWear, uiEffects: (int)UiEffects.Acid);

            var ordered = VaultDisplaySort.Order(new List<WorldObject> { zedCharm, alphaCharm }).ToList();

            CollectionAssert.AreEqual(new[] { "Alpha Charm", "Zed Charm" }, ordered.Select(i => i.Name).ToArray(),
                "an equipment-category PetDevice must sort by the ordinary equipment rungs (here: Name), never by UiEffects");
        }

        /// <summary>
        /// Weapon and equipment category items are untouched by this change: MixedSetInExpectedOrder's
        /// documented order (weapon class/armor slot, then damage type, then requirement) still holds
        /// exactly, because rungs 2-4's weapon/equipment branches were not touched - only their shared
        /// "everything else" fallthrough grew real behaviour.
        /// </summary>
        [TestMethod]
        public void Order_WeaponAndEquipmentOrderIsUnchanged()
        {
            var expected = MixedSetInExpectedOrder();

            var ordered = VaultDisplaySort.Order(Enumerable.Reverse(expected)).ToList();

            CollectionAssert.AreEqual(expected.Select(i => i.Name).ToArray(), ordered.Select(i => i.Name).ToArray());
        }

        /// <summary>
        /// Superseded by round 4 (owner ruling 2026-09-22): solo no longer reaches ThreadCacheSort at all - see
        /// <see cref="SortRunCaches_NeverTouchesAnyRun_SoloIncludedSinceRound4"/>. Kept, renamed rather than
        /// deleted, as a record of what the OLD per-batch-vs-whole-cache distinction used to prove; the fixture
        /// (a two-step arrival with an out-of-order value item) now demonstrates the opposite fact - nothing
        /// here reorders anything any more, so items stay in exactly the arrival order the filler wrote.
        /// </summary>
        [TestMethod]
        public void SortRunCaches_NoLongerOrdersAnything_EvenAcrossSeveralSteps()
        {
            var caster = SortItem(ItemType.Caster, "Wand of Frost", value: 500, weenieType: WeenieType.Caster);
            var hauberk = SortItem(ItemType.Armor, "Hauberk", value: 650, validLocations: (int)EquipMask.ChestArmor);
            var heavy = SortItem(ItemType.MeleeWeapon, "Axe of Ire", value: 900, weaponSkill: (int)Skill.HeavyWeapons, damageType: (int)DamageType.Slash);
            var ring = SortItem(ItemType.Jewelry, "Band of Embers", value: 210,
                validLocations: (int)(EquipMask.FingerWearLeft | EquipMask.FingerWearRight));

            var cache = Cache();
            var run = SoloRunWithCache(cache);

            Assert.IsTrue(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, new ACE.Database.Models.World.TreasureDeath { Tier = 1 }, null, null, null)));
            Assert.IsTrue(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, new ACE.Database.Models.World.TreasureDeath { Tier = 1 }, null, null, null)));

            var batches = new Queue<List<WorldObject>>();
            batches.Enqueue(new List<WorldObject> { caster, hauberk });
            batches.Enqueue(new List<WorldObject> { heavy, ring });

            Func<ThreadLootLedgerEntry, List<WorldObject>> materialize = _ => batches.Count > 0 ? batches.Dequeue() : new List<WorldObject>();
            Func<ThreadDungeonRun, List<WorldObject>> noBonus = _ => new List<WorldObject>();

            ThreadCacheFiller.Fill(run, cache, materialize, noBonus, new ThreadLootRollBudget(1));
            ThreadCacheFiller.Fill(run, cache, materialize, noBonus, new ThreadLootRollBudget(1));

            CollectionAssert.AreEqual(new[] { "Wand of Frost", "Hauberk", "Axe of Ire", "Band of Embers" }, NamesInSlotOrder(cache),
                "fixture: the filler places in arrival order across steps");

            var pass = ThreadCacheSort.SortRunCaches(run);

            Assert.AreEqual(0, pass.Caches, "round 4: solo is excluded, same as group");
            CollectionAssert.AreEqual(new[] { "Wand of Frost", "Hauberk", "Axe of Ire", "Band of Embers" }, NamesInSlotOrder(cache),
                "arrival order is untouched - the merge-sort-before-placement path orders a solo cache now, not this pass");
        }

        // ------------------------------------------------------------------------------------------------------
        // A cache opened mid-delivery
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Superseded by round 4 (owner ruling 2026-09-22): with SortRunCaches inert for every run, an open
        /// cache is no different from a closed one - neither is ever touched, so neither can defer anything.
        /// Kept, renamed, to record that the open-cache guard's OWN code is gone with the per-cache loop, not
        /// merely unreachable through this path.
        /// </summary>
        [TestMethod]
        public void SortRunCaches_AnOpenCacheIsNoLongerSpecial_NeitherOpenNorClosedIsEverTouched()
        {
            var expected = MixedSetInExpectedOrder();
            var arrival = Enumerable.Reverse(expected).Select(i => i.Name).ToArray();

            var cache = Cache();
            PlaceInArrivalOrder(cache, Enumerable.Reverse(expected));

            var run = SoloRunWithCache(cache);

            cache.IsOpen = true;
            cache.Viewer = 0x50000201u;

            var pass = ThreadCacheSort.SortRunCaches(run);

            Assert.AreEqual(0, pass.Caches);
            Assert.AreEqual(0, pass.Deferred, "round 4: nothing is deferred either - there is no work to defer");
            CollectionAssert.AreEqual(arrival, NamesInSlotOrder(cache));
            Assert.IsFalse(run.IsCacheSortWanted, "nothing marks the run any more");

            cache.IsOpen = false;

            var afterClose = ThreadCacheSort.SortDeferred(run);

            Assert.AreEqual(0, afterClose.Caches, "SortDeferred is equally inert: it claims a flag nothing sets, then finds nothing to do");
            CollectionAssert.AreEqual(arrival, NamesInSlotOrder(cache));
        }

        /// <summary>An unmarked run costs nothing: the deferred pass claims the flag first and returns immediately.</summary>
        [TestMethod]
        public void SortDeferred_DoesNothingWhenTheRunWasNeverMarked()
        {
            var expected = MixedSetInExpectedOrder();
            var arrival = Enumerable.Reverse(expected).Select(i => i.Name).ToArray();

            var cache = Cache();
            PlaceInArrivalOrder(cache, Enumerable.Reverse(expected));

            var run = SoloRunWithCache(cache);

            Assert.IsFalse(run.IsCacheSortWanted);

            var pass = ThreadCacheSort.SortDeferred(run);

            Assert.AreEqual(0, pass.Caches);
            Assert.AreEqual(0, pass.Deferred);
            CollectionAssert.AreEqual(arrival, NamesInSlotOrder(cache));
        }

        // ------------------------------------------------------------------------------------------------------
        // The kill switch, and the group carve-out
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The kill switch leaves placement order exactly as the filler produced it - which is master's
        /// behaviour, since master has no sort at all. Asserted on the positions themselves, not just on the
        /// pass counters, so an implementation that reordered and then reported zero would still fail.
        /// </summary>
        [TestMethod]
        public void SortRunCaches_WithTheKillSwitchOff_LeavesPlacementOrderExactlyAsTheFillerWroteIt()
        {
            var expected = MixedSetInExpectedOrder();
            var arrival = Enumerable.Reverse(expected).ToList();

            var cache = Cache();
            PlaceInArrivalOrder(cache, arrival);

            var run = SoloRunWithCache(cache);

            Assert.IsTrue(PropertyManager.ModifyBool(ThreadCacheSort.TunableKey, false));

            try
            {
                var pass = ThreadCacheSort.SortRunCaches(run);

                Assert.AreEqual(0, pass.Caches);
                Assert.AreEqual(0, pass.Items);
                Assert.AreEqual(0, pass.Deferred);
                Assert.IsFalse(run.IsCacheSortWanted);

                CollectionAssert.AreEqual(arrival.Select(i => i.Name).ToArray(), NamesInSlotOrder(cache));

                for (var i = 0; i < arrival.Count; i++)
                    Assert.AreEqual(i, arrival[i].PlacementPosition, $"{arrival[i].Name} must keep the position the filler gave it");
            }
            finally
            {
                Assert.IsTrue(PropertyManager.ModifyBool(ThreadCacheSort.TunableKey, ThreadCacheSort.DefaultEnabled));
            }
        }

        /// <summary>
        /// Group runs are out of scope for this change: a group cache keeps the value ordering its per-member
        /// deal produces. Pinned because the gate is one `run.IsGroup` clause and nothing else in the suite
        /// would notice if it were dropped.
        /// </summary>
        [TestMethod]
        public void SortRunCaches_NeverTouchesAGroupRun()
        {
            var expected = MixedSetInExpectedOrder();
            var arrival = Enumerable.Reverse(expected).Select(i => i.Name).ToArray();

            var cache = Cache();
            PlaceInArrivalOrder(cache, Enumerable.Reverse(expected));

            var run = GroupRunWithCache(cache);

            var pass = ThreadCacheSort.SortRunCaches(run);

            Assert.AreEqual(0, pass.Caches);
            Assert.AreEqual(0, pass.Deferred);
            Assert.IsFalse(run.IsCacheSortWanted);
            CollectionAssert.AreEqual(arrival, NamesInSlotOrder(cache), "a group cache keeps the order its deal produced");
        }

        /// <summary>A cache already in order writes no PlacementPosition at all - the re-sort is free.</summary>
        [TestMethod]
        public void Reorder_WritesNothingWhenTheCacheIsAlreadyInOrder()
        {
            var expected = MixedSetInExpectedOrder();
            var cache = Cache();

            PlaceInArrivalOrder(cache, expected);

            var changed = new List<string>();

            foreach (var item in expected)
            {
                var position = item.PlacementPosition;
                item.ChangesDetected = false;
                Assert.AreEqual(position, item.PlacementPosition, "sanity: clearing the change flag must not move the item");
            }

            ThreadCacheSort.Reorder(cache);

            foreach (var item in expected)
                if (item.ChangesDetected)
                    changed.Add(item.Name);

            Assert.AreEqual(0, changed.Count, $"a re-sort of an ordered cache must write nothing; it wrote: {string.Join(", ", changed)}");
        }

        // ------------------------------------------------------------------------------------------------------
        // Round 4 (owner ruling 2026-09-22): the per-pass cap, dirty/signature tracking, resume cursor and
        // open-cache deferral all lived INSIDE SortRunCaches' per-cache loop, which is deleted along with the
        // rest of that method's body now that every run is excluded (see ThreadCacheSort.cs's class and method
        // doc comments). The tests that used to exercise those mechanisms
        // (SortRunCaches_SortsAtMostTheCapPerPass_AndTheDeferredRetryReachesTheRest,
        // SortRunCaches_ResortsACacheThatReceivedNewItemsAfterItWasSorted,
        // SortRunCaches_TreatsAReRegisteredCacheAsDirty,
        // SortRunCaches_AnOpenDirtyCacheKeepsTheRetryArmed_AnOpenCleanOneDoesNot, and
        // SortDeferred_GoesQuiescentOnceEveryCacheIsOrdered_WithMoreCachesThanOnePassMaySort) proved mechanisms
        // that no longer exist in source, not mechanisms this suite could still discriminate on, so they are
        // replaced by the one test below rather than kept as five different ways of asserting "nothing happens".
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The single fact all five superseded tests above reduce to: SortRunCaches and SortDeferred are both
        /// fully inert now, for ANY number of caches, open or closed, sorted before or not, and this holds
        /// whether the run is solo or group. `MaxCachesPerSortPass` and `CacheSortResume` remain defined
        /// (source-compat for anything that still reads them) but nothing populates or consults them from this
        /// entry point any more; the run's own per-cache signature store is gone entirely (CLEANUP item 3, code
        /// review of PR #1284, 2026-09-22) - `CacheSortSignature` the struct still exists in ThreadCacheSort.cs as
        /// an independent fingerprint primitive, just with nothing left storing one per cache.
        /// </summary>
        [TestMethod]
        public void SortRunCaches_NeverTouchesAnyRun_SoloIncludedSinceRound4()
        {
            void AssertInert(ThreadDungeonRun run, Container cache, string[] arrival)
            {
                var pass = ThreadCacheSort.SortRunCaches(run);

                Assert.AreEqual(0, pass.Caches);
                Assert.AreEqual(0, pass.Deferred);
                Assert.AreEqual(0, pass.Capped);
                Assert.IsFalse(pass.LeftWork);
                Assert.IsFalse(run.IsCacheSortWanted);
                CollectionAssert.AreEqual(arrival, NamesInSlotOrder(cache));

                var deferred = ThreadCacheSort.SortDeferred(run);
                Assert.AreEqual(0, deferred.Caches);
                CollectionAssert.AreEqual(arrival, NamesInSlotOrder(cache));
            }

            var soloExpected = MixedSetInExpectedOrder();
            var soloArrival = Enumerable.Reverse(soloExpected).Select(i => i.Name).ToArray();
            var soloCache = Cache();
            PlaceInArrivalOrder(soloCache, Enumerable.Reverse(soloExpected));
            AssertInert(SoloRunWithCache(soloCache), soloCache, soloArrival);

            var groupExpected = MixedSetInExpectedOrder();
            var groupArrival = Enumerable.Reverse(groupExpected).Select(i => i.Name).ToArray();
            var groupCache = Cache();
            PlaceInArrivalOrder(groupCache, Enumerable.Reverse(groupExpected));
            AssertInert(GroupRunWithCache(groupCache), groupCache, groupArrival);
        }

        // ------------------------------------------------------------------------------------------------------
        // The chain's guard around the sort (code review finding 1)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The sort's call site in the delivery chain sits AFTER the step's own try/catch and BEFORE
        /// `if (more) run.MarkPlacementWanted();` - the line that hands a chain cut at MaxChainSteps to
        /// ThreadDungeonManager.Tick's retry. An unguarded throw out of the sort would skip that line, and
        /// ActionQueue.RunActions would swallow and log it: the world survives, but the run sits with
        /// IsPlacementWanted false and loot still pooled until an unrelated kill starts a fresh chain.
        ///
        /// The chain itself needs a landblock action queue no test here can stand up, so this composes the
        /// same two statements in the same order against a real run, with the sort forced to throw. The
        /// SOURCE SCAN below is what pins that the chain really is written this way round.
        /// </summary>
        [TestMethod]
        public void SortGuarded_SwallowsAThrowingSort_SoTheChainStillMarksPlacementWanted()
        {
            var expected = MixedSetInExpectedOrder();
            var arrival = Enumerable.Reverse(expected).Select(i => i.Name).ToArray();

            var cache = Cache();
            PlaceInArrivalOrder(cache, Enumerable.Reverse(expected));

            var run = SoloRunWithCache(cache);

            Assert.IsFalse(run.IsPlacementWanted, "fixture: nothing has asked for a retry yet");

            var original = ThreadCacheSort.Reorderer;
            ThreadCacheSort.Reorderer = _ => throw new InvalidOperationException("a sort rung threw");

            try
            {
                // The chain's terminal tail, in its own order: guarded sort, then the retry bookkeeping.
                var sort = ThreadCachePlacer.SortGuarded(run);

                const bool more = true;   // a chain cut at MaxChainSteps with loot still pooled

                if (more)
                    run.MarkPlacementWanted();

                Assert.AreEqual(0, sort.Caches, "a thrown sort reports nothing sorted");
                Assert.AreEqual(0, sort.Items);
            }
            finally
            {
                ThreadCacheSort.Reorderer = original;
            }

            Assert.IsTrue(run.IsPlacementWanted, "the retry bookkeeping must still run after a sort that threw");
            CollectionAssert.AreEqual(arrival, NamesInSlotOrder(cache), "a thrown sort leaves contents in arrival order, it does not half-order them");
        }

        /// <summary>
        /// Round 4 (owner ruling 2026-09-22): a non-throwing sort now returns an empty pass and reorders
        /// nothing, since SortRunCaches (what SortGuarded calls) is inert for every run.
        /// </summary>
        [TestMethod]
        public void SortGuarded_ReturnsAnEmptyPassNowThatTheSortIsInertForEveryRun()
        {
            var expected = MixedSetInExpectedOrder();
            var arrival = Enumerable.Reverse(expected).Select(i => i.Name).ToArray();

            var cache = Cache();
            PlaceInArrivalOrder(cache, Enumerable.Reverse(expected));

            var run = SoloRunWithCache(cache);

            var sort = ThreadCachePlacer.SortGuarded(run);

            Assert.AreEqual(0, sort.Caches);
            Assert.AreEqual(0, sort.Items);
            CollectionAssert.AreEqual(arrival, NamesInSlotOrder(cache));
        }

        // ------------------------------------------------------------------------------------------------------
        // Wiring, pinned on source (the delivery chain needs a live landblock action queue to run)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// THIS IS A SOURCE SCAN AND IT PROVES PLACEMENT, NOT BEHAVIOUR. The delivery chain runs on a
        /// landblock action queue that no test in this project can stand up, so the wiring facts this change
        /// depends on are pinned as text:
        ///
        ///   1. the sort is computed from a `terminal` flag, and the re-enqueue reads that SAME flag - if the
        ///      two conditions were spelled out separately they could drift, and a sort that ran on a
        ///      non-terminal step is exactly the per-batch sort this change exists to avoid;
        ///   2. it sits ABOVE watch.Stop(), so its cost is charged to the step's own budget accounting;
        ///   3. it goes through SortGuarded, never ThreadCacheSort.SortRunCaches directly, and it comes
        ///      BEFORE `run.MarkPlacementWanted()` - the pairing that makes a thrown sort unable to cost the
        ///      chain its retry bookkeeping (code review finding 1);
        ///   4. the finish line carries the sort's cost.
        /// </summary>
        [TestMethod]
        public void CallSite_TheDeliveryChainSortsOnceAtItsTerminalStep()
        {
            var relativePath = Path.Combine("Source", "ACE.Server", "ThreadDungeons", "ThreadCachePlacer.cs");
            var source = ReadFromSourceTree(relativePath);

            var request = source.IndexOf("public static CacheRequestOutcome RequestPlacement(ThreadDungeonRun run, CacheRequestMode mode, uint requesterGuid)", StringComparison.Ordinal);
            Assert.IsTrue(request >= 0, "ThreadCachePlacer must still declare the three-argument RequestPlacement - if it was resignatured, update this test rather than deleting it");

            var terminal = source.IndexOf("var terminal = !(more && steps < MaxChainSteps);", StringComparison.Ordinal);
            var sort = source.IndexOf("var sort = terminal ? SortGuarded(run) : default;", StringComparison.Ordinal);
            var stop = source.IndexOf("watch.Stop();", StringComparison.Ordinal);
            var requeue = source.IndexOf("if (!terminal)", StringComparison.Ordinal);
            var wanted = source.IndexOf("run.MarkPlacementWanted();", request, StringComparison.Ordinal);

            Assert.IsTrue(terminal >= 0, "ThreadCachePlacer must compute a `terminal` flag from the chain's own continuation test");
            Assert.IsTrue(sort > terminal, "the sort must be gated on that flag, and must go through SortGuarded rather than ThreadCacheSort.SortRunCaches");
            Assert.IsTrue(stop > sort, "the sort must run INSIDE the step's Stopwatch, so its cost lands in totalMs/maxStepMs and the step metrics");
            Assert.IsTrue(requeue > sort, "the re-enqueue must read the same `terminal` flag the sort was gated on");
            Assert.IsTrue(wanted > sort, "MarkPlacementWanted must come AFTER the sort - a throw skipping it is the whole reason the sort is guarded");

            // The guarded call is the ONLY way the chain reaches the sort. A direct SortRunCaches call from
            // this file would reintroduce finding 1 with every other assertion above still passing.
            var direct = source.IndexOf("ThreadCacheSort.SortRunCaches(", StringComparison.Ordinal);
            var guard = source.IndexOf("internal static CacheSortPass SortGuarded(ThreadDungeonRun run)", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "ThreadCachePlacer must declare SortGuarded");
            Assert.IsTrue(direct > guard, "the only ThreadCacheSort.SortRunCaches call in this file must be the one INSIDE SortGuarded's try");
            Assert.AreEqual(direct, source.LastIndexOf("ThreadCacheSort.SortRunCaches(", StringComparison.Ordinal), "there must be exactly one ThreadCacheSort.SortRunCaches call site in this file");

            StringAssert.Contains(source, "ThreadCacheSort.LogSuffix(sort)", "the delivery finish line must report what the sort cost");
        }

        /// <summary>
        /// THIS IS A SOURCE SCAN AND IT PROVES PLACEMENT, NOT BEHAVIOUR. The whole point of
        /// VaultDisplaySort is that the mule panel and Thread Caches share ONE composite key, so a change to
        /// the vault ordering reaches both. That guarantee is destroyed silently the moment
        /// PersonalVendor.forEachItem grows its own sort chain again: every mule sort test would still pass,
        /// and so would every test in this class, while the two orders quietly diverged.
        /// </summary>
        [TestMethod]
        public void CallSite_PersonalVendorForEachItemUsesTheSharedKey()
        {
            var relativePath = Path.Combine("Source", "ACE.Server", "WorldObjects", "PersonalVendor.cs");
            var code = AccountVaultPurgeTests.StripComments(ReadFromSourceTree(relativePath));

            var declaration = code.IndexOf("public override void forEachItem(Action<WorldObject> action)", StringComparison.Ordinal);
            Assert.IsTrue(declaration >= 0, "PersonalVendor must still declare forEachItem - if it was resignatured, update this test rather than deleting it");

            var body = code.Substring(declaration);
            var end = body.IndexOf("\n        internal void RegisterSortName", StringComparison.Ordinal);
            if (end > 0)
                body = body.Substring(0, end);

            StringAssert.Contains(body, "VaultDisplaySort.Order(", "forEachItem must order through the SHARED key, not a chain of its own");
            Assert.IsFalse(body.Contains(".ThenBy"), "forEachItem must not carry sort rungs of its own - they belong in VaultDisplaySort, where Thread Caches read them too");
        }

        private static string ReadFromSourceTree(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}
