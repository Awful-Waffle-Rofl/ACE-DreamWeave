using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.SpellReroll;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Serpentine spell reroll's decision layer.
    ///
    /// THE RULE UNDER TEST IS EXACT PARITY WITH LOOT GENERATION, not "a sensible type-aware pool". A spell may
    /// only be replaced by one that could have rolled on THAT item during generation, which means the pool is
    /// the union of exactly two things and nothing else: the item-spell table
    /// LootGenerationFactory_Spells.RollItemSpells would have chosen (:62-88, behind its outer
    /// `HasArmorLevel(wo) || IsWeapon` gate at :33), and the enchantment group
    /// GetSpellSelectionCode_Dynamic (:431) would have chosen. Where the item cannot be placed in both, the
    /// reroll REFUSES - there is no fallback pool, because a narrower pool is still the wrong pool.
    ///
    /// Everything here runs through SpellRerollTables, the pure half. A Player cannot be constructed in a test
    /// in this repo, so the manager owns only plumbing - guards, animation chain, consume, networking - and
    /// holds no rule of its own. TreasureRoll is a plain public-field data holder, so the real generation
    /// predicates (IsWeapon / IsMeleeWeapon / IsCaster) are exercised directly rather than mocked.
    ///
    /// Randomness is injected as Func&lt;int,int&gt;: n =&gt; 0 takes the first legal candidate, n =&gt; n - 1
    /// the last, and a seeded Random walks the middle. Nothing below touches ThreadSafeRandom.
    /// </summary>
    [TestClass]
    public class SpellRerollTests
    {
        /// <summary>Always take the first legal candidate.</summary>
        private static readonly Func<int, int> First = n => 0;

        /// <summary>Always take the last legal candidate.</summary>
        private static readonly Func<int, int> Last = n => n - 1;

        /// <summary>Enchantment group 7 - upper armor, what GetSpellCode_Dynamic_ClothingArmor gives a cuirass.</summary>
        private const int ArmorGroup = 7;

        /// <summary>Enchantment group 5 - a non-nether caster.</summary>
        private const int CasterGroup = 5;

        /// <summary>Enchantment group 6 - a one-handed melee weapon.</summary>
        private const int MeleeGroup = 6;

        /// <summary>Enchantment group 2 - jewelry with NO armor level, i.e. a plain ring.</summary>
        private const int PlainJewelryGroup = 2;

        private static int Level(SpellId spell)
        {
            var levels = SpellLevelProgression.GetSpellLevels(spell);

            Assert.IsNotNull(levels, $"{spell} has no level progression");

            return levels.IndexOf(spell) + 1;
        }

        private static int LevelForm(SpellId family, int level)
        {
            var levels = SpellLevelProgression.GetSpellLevels(family);

            Assert.IsNotNull(levels, $"{family} has no level progression");
            Assert.AreEqual(8, levels.Count, $"{family} progression is not 8 long");

            return (int)levels[level - 1];
        }

        /// <summary>
        /// The repo owner's worked example: armor carrying Incantation of Armor Self (the level 8 form of the
        /// Armor Self family), Flame Bane 7 and Invulnerability 7.
        /// </summary>
        private static List<int> WorkedExample()
        {
            return new List<int>
            {
                LevelForm(SpellId.ArmorSelf1, 8),
                LevelForm(SpellId.FlameBane1, 7),
                LevelForm(SpellId.InvulnerabilitySelf1, 7),
            };
        }

        private static List<SpellId[]> ArmorPool() => SpellRerollTables.BuildCandidatePool(RerollItemSpellSource.Armor, ArmorGroup);

        private static HashSet<SpellId> Heads(IEnumerable<SpellId[]> pool) => pool.Select(f => f[0]).ToHashSet();

        private static HashSet<SpellId> TableHeads(SpellId[][] table) => table.Select(f => f[0]).Where(h => h != SpellId.Undef).ToHashSet();

        /// <summary>The families a bare enchantment group contributes, with no item-spell table at all.</summary>
        private static HashSet<SpellId> GroupHeads(int code) => Heads(SpellRerollTables.BuildCandidatePool(RerollItemSpellSource.None, code));

        // ---------------- level preservation ----------------

        /// <summary>
        /// THE CENTRAL CONTRACT. A reroll trades the spell, never its level - an 8 comes back an 8 and a 7
        /// comes back a 7 - which is also what lets ItemSpellcraft and ItemDifficulty be left untouched.
        /// Asserted against the progression tables directly rather than against the plan's own bookkeeping, so
        /// a bug in SpellRerollSwap.Level cannot hide the real defect.
        /// </summary>
        [TestMethod]
        public void Reroll_PreservesLevel_ForEverySwap()
        {
            foreach (var picker in new[] { First, Last })
            {
                var plan = SpellRerollTables.Plan(WorkedExample(), ArmorPool(), null, null, picker);

                Assert.AreEqual(3, plan.Swaps.Count, "all three enchantments should have been rerolled");

                foreach (var swap in plan.Swaps)
                {
                    var oldLevel = Level((SpellId)swap.OldSpell);
                    var newLevel = Level((SpellId)swap.NewSpell);

                    Assert.AreEqual(oldLevel, newLevel, $"level changed: {(SpellId)swap.OldSpell} ({oldLevel}) -> {(SpellId)swap.NewSpell} ({newLevel})");
                    Assert.AreEqual(oldLevel, swap.Level, "the plan reported a level it did not use");
                }

                var levels = plan.Swaps.Select(s => s.Level).OrderBy(l => l).ToList();

                CollectionAssert.AreEqual(new[] { 7, 7, 8 }, levels, "the level multiset must survive the reroll");
            }
        }

        /// <summary>
        /// A reroll always produces a DIFFERENT family. Answering Flame Bane 7 with Flame Bane 7 would be a
        /// no-op the player still paid a bag for.
        /// </summary>
        [TestMethod]
        public void Reroll_NeverReturnsTheSameFamily()
        {
            var rng = new Random(20260806);

            for (var i = 0; i < 200; i++)
            {
                var plan = SpellRerollTables.Plan(WorkedExample(), ArmorPool(), null, null, n => rng.Next(n));

                foreach (var swap in plan.Swaps)
                {
                    var oldFamily = SpellLevelProgression.GetSpellLevels((SpellId)swap.OldSpell)[0];
                    var newFamily = SpellLevelProgression.GetSpellLevels((SpellId)swap.NewSpell)[0];

                    Assert.AreNotEqual(oldFamily, newFamily, "a reroll returned the same family");
                }
            }
        }

        // ---------------- no duplicates ----------------

        /// <summary>
        /// No reroll may produce a spell the item already has, whether that spell is staying put or was
        /// written earlier in the same pass. A duplicate would be a silently lost enchantment: the spell book
        /// is a dictionary keyed by spell id, so writing a spell that is already present overwrites rather
        /// than adds, and the item comes back with fewer enchantments than it started with.
        /// </summary>
        [TestMethod]
        public void Reroll_NeverProducesADuplicate()
        {
            var rng = new Random(4711);

            var book = new List<int>
            {
                LevelForm(SpellId.ArmorSelf1, 7),
                LevelForm(SpellId.FlameBane1, 7),
                LevelForm(SpellId.InvulnerabilitySelf1, 7),
                LevelForm(SpellId.ImpregnabilitySelf1, 7),
                LevelForm(SpellId.StrengthSelf1, 8),
                LevelForm(SpellId.EnduranceSelf1, 8),
            };

            for (var i = 0; i < 500; i++)
            {
                var plan = SpellRerollTables.Plan(book, ArmorPool(), null, null, n => rng.Next(n));

                var final = new List<int>();

                final.AddRange(plan.PreservedCantrips);
                final.AddRange(plan.PreservedProcs);
                final.AddRange(plan.Skipped);
                final.AddRange(plan.Swaps.Select(s => s.NewSpell));

                Assert.AreEqual(book.Count, final.Count, "the reroll changed how many spells the item carries");
                Assert.AreEqual(final.Count, final.Distinct().Count(), "the reroll produced a duplicate spell");

                foreach (var swap in plan.Swaps)
                    Assert.IsFalse(book.Contains(swap.NewSpell), $"reroll produced {(SpellId)swap.NewSpell}, which was already on the item");
            }
        }

        /// <summary>
        /// Every id that went in comes out in exactly one bucket. This is the invariant behind "a spell is
        /// never silently lost".
        /// </summary>
        [TestMethod]
        public void Reroll_AccountsForEverySpell()
        {
            var book = WorkedExample();

            book.Add((int)ArmorCantrips.Table[0][0]);   // a minor cantrip
            book.Add(99999);                            // no progression at all

            var plan = SpellRerollTables.Plan(book, ArmorPool(), null, null, First);

            var accounted = new List<int>();

            accounted.AddRange(plan.PreservedCantrips);
            accounted.AddRange(plan.PreservedProcs);
            accounted.AddRange(plan.Skipped);
            accounted.AddRange(plan.Swaps.Select(s => s.OldSpell));

            CollectionAssert.AreEquivalent(book, accounted, "a spell went missing between the book and the plan");
        }

        // ---------------- cantrips ----------------

        /// <summary>
        /// Cantrips of ALL FOUR tiers are preserved exactly. The trap this pins down is real and already in the
        /// codebase: the only pre-existing cantrip accessors (WorldObject_Magic.cs) test Epic and Legendary
        /// alone, because they exist to log rare drops. Reusing that shape would reroll away every minor and
        /// major cantrip on the item.
        /// </summary>
        [TestMethod]
        public void Reroll_PreservesCantripsOfAllFourTiers()
        {
            var family = ArmorCantrips.Table[0];

            Assert.AreEqual(4, family.Length, "a cantrip family should carry four tiers");

            var book = WorkedExample();

            foreach (var tier in family)
                book.Add((int)tier);

            Assert.IsTrue(LootTables.MinorCantrips.Contains((int)family[0]));
            Assert.IsTrue(LootTables.MajorCantrips.Contains((int)family[1]));
            Assert.IsTrue(LootTables.EpicCantrips.Contains((int)family[2]));
            Assert.IsTrue(LootTables.LegendaryCantrips.Contains((int)family[3]));

            var plan = SpellRerollTables.Plan(book, ArmorPool(), null, null, First);

            foreach (var tier in family)
            {
                Assert.IsTrue(plan.PreservedCantrips.Contains((int)tier), $"cantrip {tier} was not preserved");
                Assert.IsFalse(plan.Swaps.Any(s => s.OldSpell == (int)tier), $"cantrip {tier} was rerolled away");
                Assert.IsFalse(plan.Skipped.Contains((int)tier), $"cantrip {tier} landed in the skipped bucket");
            }

            Assert.AreEqual(3, plan.Swaps.Count, "the non-cantrip enchantments should still have been rerolled");
        }

        /// <summary>
        /// A cantrip is never PRODUCED either - it must not appear anywhere in a candidate pool.
        /// </summary>
        [TestMethod]
        public void CandidatePool_ContainsNoCantrips()
        {
            foreach (var source in AllSources())
            {
                for (var code = 1; code <= SpellSelectionTable.NumGroups; code++)
                {
                    foreach (var family in SpellRerollTables.BuildCandidatePool(source, code))
                        foreach (var form in family)
                            Assert.IsFalse(SpellRerollTables.IsCantrip((int)form), $"{source}/{code} pool offered cantrip {form}");
                }
            }
        }

        // ---------------- broken progressions ----------------

        /// <summary>
        /// A spell with no usable level progression is LEFT IN PLACE and reported, never dropped and never
        /// replaced by a guess.
        /// </summary>
        [TestMethod]
        public void Reroll_LeavesASpellWithNoProgressionInPlace()
        {
            Assert.IsNull(SpellLevelProgression.GetSpellLevels((SpellId)99999));

            var book = WorkedExample();
            book.Add(99999);

            var plan = SpellRerollTables.Plan(book, ArmorPool(), null, null, First);

            Assert.IsTrue(plan.Skipped.Contains(99999), "the unprogressed spell was not reported as skipped");
            Assert.IsFalse(plan.Swaps.Any(s => s.OldSpell == 99999), "the unprogressed spell was rerolled");
            Assert.IsFalse(plan.Swaps.Any(s => s.NewSpell == 99999));
            Assert.AreEqual(3, plan.Swaps.Count, "the healthy enchantments should still have been rerolled");
        }

        /// <summary>
        /// An empty candidate pool skips every swap rather than throwing or dropping spells. The manager
        /// refuses before it ever reaches this, but the planner must not misbehave if it does.
        /// </summary>
        [TestMethod]
        public void Reroll_WithNoCandidates_SkipsEverything()
        {
            var book = WorkedExample();

            var plan = SpellRerollTables.Plan(book, new List<SpellId[]>(), null, null, First);

            Assert.AreEqual(0, plan.Swaps.Count);
            CollectionAssert.AreEquivalent(book, plan.Skipped);
        }

        // ---------------- procs ----------------

        /// <summary>
        /// SpellDID and ProcSpell are preserved. They are separate item properties (AppraiseInfo.BuildSpells
        /// handles them apart from the biota spell book), and rerolling one would silently reassign a weapon's
        /// proc.
        /// </summary>
        [TestMethod]
        public void Reroll_PreservesSpellDidAndProcSpell()
        {
            var spellDID = (uint)LevelForm(SpellId.ArmorSelf1, 8);
            var procSpell = (uint)LevelForm(SpellId.FlameBane1, 7);

            var plan = SpellRerollTables.Plan(WorkedExample(), ArmorPool(), spellDID, procSpell, First);

            Assert.IsTrue(plan.PreservedProcs.Contains((int)spellDID), "SpellDID was not preserved");
            Assert.IsTrue(plan.PreservedProcs.Contains((int)procSpell), "ProcSpell was not preserved");

            Assert.AreEqual(1, plan.Swaps.Count, "only the one remaining enchantment should have been rerolled");
            Assert.AreEqual(LevelForm(SpellId.InvulnerabilitySelf1, 7), plan.Swaps[0].OldSpell);
        }

        // ---------------- exact parity: the item-spell source ----------------

        private static IEnumerable<RerollItemSpellSource> AllSources()
        {
            yield return RerollItemSpellSource.None;
            yield return RerollItemSpellSource.Armor;
            yield return RerollItemSpellSource.Melee;
            yield return RerollItemSpellSource.Missile;
            yield return RerollItemSpellSource.Caster;
        }

        /// <summary>
        /// A PLAIN RING GETS NO ITEM SPELLS AT ALL. This is the gap that the type-aware-but-not-parity version
        /// of this feature had: it routed ItemType.Jewelry to JewelrySpells.Table, a table generation never
        /// reads. RollItemSpells is gated on `HasArmorLevel(wo) || IsWeapon` (:33), and a ring is neither, so
        /// generation gives it enchantments and cantrips only. Offering it Impenetrability or a bane on a
        /// reroll would be handing it a spell it could never have been born with.
        /// </summary>
        [TestMethod]
        public void ItemSpellSource_PlainJewelry_IsNone()
        {
            var ring = new TreasureRoll(TreasureItemType.Jewelry);

            Assert.IsFalse(ring.IsWeapon, "a jewelry roll carries no weapon type");
            Assert.AreEqual(RerollItemSpellSource.None, SpellRerollTables.GetItemSpellSource(ring, hasArmorLevel: false));

            // and a crown - jewelry WITH an armor level - takes the armor branch, because armor level is
            // tested first, exactly as RollItemSpells does
            Assert.AreEqual(RerollItemSpellSource.Armor, SpellRerollTables.GetItemSpellSource(ring, hasArmorLevel: true));
        }

        /// <summary>
        /// A plain ring's whole pool is its enchantment group and nothing else - no item-spell table leaks in.
        /// </summary>
        [TestMethod]
        public void CandidatePool_PlainJewelry_IsExactlyItsEnchantmentGroup()
        {
            var pool = SpellRerollTables.BuildCandidatePool(RerollItemSpellSource.None, PlainJewelryGroup);

            Assert.IsTrue(pool.Count > 0, "group 2 should contribute families");

            CollectionAssert.AreEquivalent(GroupHeads(PlainJewelryGroup).ToList(), Heads(pool).ToList(),
                "a plain ring's pool must be its enchantment group exactly");

            // the specific regression: JewelrySpells.Table must contribute nothing anywhere
            var jewelryOnly = TableHeads(JewelrySpells.Table);
            jewelryOnly.ExceptWith(GroupHeads(PlainJewelryGroup));

            Assert.IsTrue(jewelryOnly.Count > 0, "JewelrySpells has families the group does not - otherwise this proves nothing");

            foreach (var head in jewelryOnly)
                Assert.IsFalse(Heads(pool).Contains(head), $"JewelrySpells family {head} leaked into a plain ring's pool");
        }

        /// <summary>
        /// An armor item's pool never offers a weapon or caster item spell. Computed as a set difference so
        /// the assertion keeps its meaning if the tables are edited.
        /// </summary>
        [TestMethod]
        public void CandidatePool_ForArmor_ExcludesWeaponAndCasterItemSpells()
        {
            var poolHeads = Heads(ArmorPool());

            var allowed = TableHeads(ArmorSpells.Table);
            allowed.UnionWith(GroupHeads(ArmorGroup));

            var foreign = 0;

            foreach (var table in new[] { MeleeSpells.Table, MissileSpells.Table, WandSpells.Table })
            {
                foreach (var head in TableHeads(table))
                {
                    if (allowed.Contains(head))
                        continue;

                    Assert.IsFalse(poolHeads.Contains(head), $"an armor pool offered the weapon/caster item spell {head}");
                    foreign++;
                }
            }

            Assert.IsTrue(foreign > 0, "no weapon/caster-exclusive families found - the assertion above proved nothing");
            Assert.IsTrue(poolHeads.Contains(SpellId.ArmorSelf1), "the armor pool lost its own item spells");
            Assert.IsFalse(poolHeads.Contains(SpellId.HermeticLinkSelf1), "Hermetic Link is a caster item spell");
            Assert.IsFalse(poolHeads.Contains(SpellId.BloodDrinkerSelf1), "Blood Drinker is a melee item spell");
        }

        /// <summary>
        /// A CASTER NEVER DRAWS IMPENETRABILITY OR A BANE. Those are armor item spells; a wand could not have
        /// rolled one at generation, from either WandSpells or enchantment group 5.
        /// </summary>
        [TestMethod]
        public void CandidatePool_ForCaster_ExcludesArmorItemSpells()
        {
            var poolHeads = Heads(SpellRerollTables.BuildCandidatePool(RerollItemSpellSource.Caster, CasterGroup));

            Assert.IsTrue(poolHeads.Contains(SpellId.HermeticLinkSelf1), "the caster pool lost its own item spells");

            var forbidden = new[]
            {
                SpellId.Impenetrability1,
                SpellId.BladeBane1,
                SpellId.PiercingBane1,
                SpellId.BludgeonBane1,
                SpellId.FlameBane1,
                SpellId.FrostBane1,
                SpellId.AcidBane1,
                SpellId.LightningBane1,
                SpellId.InvulnerabilitySelf1,
                SpellId.ImpregnabilitySelf1,
            };

            foreach (var head in forbidden)
                Assert.IsFalse(poolHeads.Contains(head), $"a caster pool offered the armor item spell {head}");

            // and the armor pool really does carry them, so the list above is not stale
            var armorHeads = Heads(ArmorPool());

            foreach (var head in forbidden)
                Assert.IsTrue(armorHeads.Contains(head), $"{head} is not an armor item spell after all - the test list is stale");
        }

        /// <summary>
        /// A melee weapon never draws an armor bane or a caster item spell either.
        /// </summary>
        [TestMethod]
        public void CandidatePool_ForMelee_ExcludesArmorAndCasterItemSpells()
        {
            var poolHeads = Heads(SpellRerollTables.BuildCandidatePool(RerollItemSpellSource.Melee, MeleeGroup));

            Assert.IsTrue(poolHeads.Contains(SpellId.BloodDrinkerSelf1), "the melee pool lost its own item spells");

            Assert.IsFalse(poolHeads.Contains(SpellId.Impenetrability1));
            Assert.IsFalse(poolHeads.Contains(SpellId.FlameBane1));
            Assert.IsFalse(poolHeads.Contains(SpellId.HermeticLinkSelf1));
        }

        /// <summary>
        /// The four item-spell sources map to the four tables RollItemSpells uses, and NOTHING maps to
        /// JewelrySpells.
        /// </summary>
        [TestMethod]
        public void ItemSpellFamilies_MatchGenerationsFourTables()
        {
            Assert.AreSame(ArmorSpells.Table, SpellRerollTables.GetItemSpellFamilies(RerollItemSpellSource.Armor));
            Assert.AreSame(MeleeSpells.Table, SpellRerollTables.GetItemSpellFamilies(RerollItemSpellSource.Melee));
            Assert.AreSame(MissileSpells.Table, SpellRerollTables.GetItemSpellFamilies(RerollItemSpellSource.Missile));
            Assert.AreSame(WandSpells.Table, SpellRerollTables.GetItemSpellFamilies(RerollItemSpellSource.Caster));

            Assert.AreEqual(0, SpellRerollTables.GetItemSpellFamilies(RerollItemSpellSource.None).Count);

            // JewelrySpells is never any source's table
            foreach (var source in AllSources())
                Assert.AreNotSame(JewelrySpells.Table, SpellRerollTables.GetItemSpellFamilies(source));
        }

        /// <summary>
        /// The weapon branches are driven by generation's own predicates, over real TreasureWeaponType values,
        /// rather than by a re-derivation from PropertyInt.ItemType.
        /// </summary>
        [TestMethod]
        public void ItemSpellSource_UsesGenerationsWeaponPredicates()
        {
            var melee = new TreasureRoll(TreasureItemType.Weapon) { WeaponType = TreasureWeaponType.Dagger };
            var twoHanded = new TreasureRoll(TreasureItemType.Weapon) { WeaponType = TreasureWeaponType.TwoHandedSword };
            var missile = new TreasureRoll(TreasureItemType.Weapon) { WeaponType = TreasureWeaponType.Crossbow };
            var caster = new TreasureRoll(TreasureItemType.Weapon) { WeaponType = TreasureWeaponType.Caster };

            Assert.AreEqual(RerollItemSpellSource.Melee, SpellRerollTables.GetItemSpellSource(melee, false));

            // two-handers are MELEE by TreasureWeaponTypeExtensions.IsMeleeWeapon, even though their
            // ENCHANTMENT group is 17 rather than 6 - the two classifications are independent
            Assert.AreEqual(RerollItemSpellSource.Melee, SpellRerollTables.GetItemSpellSource(twoHanded, false));

            Assert.AreEqual(RerollItemSpellSource.Missile, SpellRerollTables.GetItemSpellSource(missile, false));
            Assert.AreEqual(RerollItemSpellSource.Caster, SpellRerollTables.GetItemSpellSource(caster, false));

            // an armor level still wins over all of them, matching RollItemSpells' branch order
            Assert.AreEqual(RerollItemSpellSource.Armor, SpellRerollTables.GetItemSpellSource(melee, true));

            // and every non-Undef weapon type resolves to one of the three, so the outer gate can never admit
            // a weapon that then falls through to None
            foreach (TreasureWeaponType weaponType in System.Enum.GetValues(typeof(TreasureWeaponType)))
            {
                if (weaponType == TreasureWeaponType.Undef)
                    continue;

                var roll = new TreasureRoll(TreasureItemType.Weapon) { WeaponType = weaponType };

                Assert.AreNotEqual(RerollItemSpellSource.None, SpellRerollTables.GetItemSpellSource(roll, false),
                    $"{weaponType} passed the IsWeapon gate but matched no branch");
            }
        }

        /// <summary>
        /// Clothing with no armor level and no weapon type gets no item spells - the same outer-gate answer as
        /// a ring, reached by a different route.
        /// </summary>
        [TestMethod]
        public void ItemSpellSource_ClothingWithoutArmorLevel_IsNone()
        {
            var shirt = new TreasureRoll(TreasureItemType.Clothing);

            Assert.AreEqual(RerollItemSpellSource.None, SpellRerollTables.GetItemSpellSource(shirt, hasArmorLevel: false));
            Assert.AreEqual(RerollItemSpellSource.Armor, SpellRerollTables.GetItemSpellSource(shirt, hasArmorLevel: true));
        }

        // ---------------- exact parity: the enchantment code ----------------

        /// <summary>
        /// Only 1..20 are real groups. A 0 - which is what GetSpellSelectionCode_Dynamic returns when it
        /// cannot classify an item, and what a simple flask legitimately gets - is NOT a group, and the
        /// manager refuses on it rather than proceeding on the item-spell table alone.
        /// </summary>
        [TestMethod]
        public void SpellSelectionCode_ValidRangeIsExactlyTheGroupsThatExist()
        {
            Assert.AreEqual(20, SpellSelectionTable.NumGroups, "the group count moved; the refusal range must move with it");

            Assert.IsFalse(SpellRerollTables.IsValidSpellSelectionCode(0));
            Assert.IsFalse(SpellRerollTables.IsValidSpellSelectionCode(-1));
            Assert.IsFalse(SpellRerollTables.IsValidSpellSelectionCode(21));

            for (var code = 1; code <= SpellSelectionTable.NumGroups; code++)
                Assert.IsTrue(SpellRerollTables.IsValidSpellSelectionCode(code), $"code {code} should be valid");
        }

        /// <summary>
        /// An invalid code contributes NOTHING - there is deliberately no substitute group. Paired with the
        /// manager's refusal, that means an unclassifiable item is never rerolled from a partial pool.
        /// </summary>
        [TestMethod]
        public void CandidatePool_InvalidCode_ContributesNothing()
        {
            var itemTableOnly = Heads(SpellRerollTables.BuildCandidatePool(RerollItemSpellSource.Armor, 0));

            CollectionAssert.AreEquivalent(TableHeads(ArmorSpells.Table).ToList(), itemTableOnly.ToList(),
                "an invalid code must add nothing at all, not a default group");

            foreach (var code in new[] { -1, 0, 21, 999 })
                Assert.AreEqual(0, SpellRerollTables.BuildCandidatePool(RerollItemSpellSource.None, code).Count,
                    $"code {code} with no item table must yield an empty pool, which the manager refuses on");
        }

        /// <summary>
        /// The enchantment group genuinely widens the pool and never shrinks it.
        /// </summary>
        [TestMethod]
        public void CandidatePool_EnchantmentGroup_Widens()
        {
            var itemOnly = TableHeads(ArmorSpells.Table);
            var withGroup = Heads(ArmorPool());

            Assert.IsTrue(withGroup.Count >= itemOnly.Count, "adding an enchantment group must never shrink the pool");

            foreach (var head in itemOnly)
                Assert.IsTrue(withGroup.Contains(head), $"the group pool dropped item spell {head}");

            foreach (var head in GroupHeads(ArmorGroup))
                Assert.IsTrue(withGroup.Contains(head), $"the pool dropped group spell {head}");
        }

        // ---------------- pool hygiene ----------------

        /// <summary>
        /// Every family in every pool is a complete eight-level progression. This is what makes
        /// family[level - 1] safe for any level 1..8 in the planner.
        /// </summary>
        [TestMethod]
        public void CandidatePool_FamiliesAreAlwaysEightLevels()
        {
            foreach (var source in AllSources())
            {
                for (var code = 1; code <= SpellSelectionTable.NumGroups; code++)
                {
                    foreach (var family in SpellRerollTables.BuildCandidatePool(source, code))
                    {
                        Assert.AreEqual(SpellRerollTables.NumLevels, family.Length);

                        foreach (var form in family)
                            Assert.AreNotEqual(SpellId.Undef, form, $"{source}/{code} pool carried an incomplete family");
                    }
                }
            }
        }

        /// <summary>
        /// The pool is deduplicated by family. The item tables and the enchantment groups overlap heavily
        /// (Strength Self, the protections), and a duplicated family would weight itself twice in the draw.
        /// </summary>
        [TestMethod]
        public void CandidatePool_IsDeduplicatedByFamily()
        {
            foreach (var source in AllSources())
            {
                for (var code = 1; code <= SpellSelectionTable.NumGroups; code++)
                {
                    var heads = SpellRerollTables.BuildCandidatePool(source, code).Select(f => f[0]).ToList();

                    Assert.AreEqual(heads.Count, heads.Distinct().Count(), $"the {source}/{code} pool carried a family twice");
                }
            }
        }

        /// <summary>
        /// Every pool is drawn ONLY from the two sanctioned sources. This is the exact-parity rule stated as a
        /// single assertion over all 100 source/group combinations: nothing may appear in a pool that is not
        /// either in that source's item-spell table or in that enchantment group.
        /// </summary>
        [TestMethod]
        public void CandidatePool_IsExactlyTheUnionOfItsTwoSanctionedSources()
        {
            foreach (var source in AllSources())
            {
                var itemTable = TableHeads(SpellRerollTables.GetItemSpellFamilies(source).ToArray());

                for (var code = 1; code <= SpellSelectionTable.NumGroups; code++)
                {
                    var allowed = new HashSet<SpellId>(itemTable);
                    allowed.UnionWith(GroupHeads(code));

                    foreach (var head in Heads(SpellRerollTables.BuildCandidatePool(source, code)))
                        Assert.IsTrue(allowed.Contains(head), $"{source}/{code} pool offered {head}, which is in neither sanctioned source");
                }
            }
        }
    }
}
