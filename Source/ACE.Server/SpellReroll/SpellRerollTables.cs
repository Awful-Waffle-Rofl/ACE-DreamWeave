using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Tables;

namespace ACE.Server.SpellReroll
{
    /// <summary>
    /// Which item-spell table an item draws from, reproducing the four branches of
    /// LootGenerationFactory_Spells.RollItemSpells one for one.
    ///
    /// THERE IS NO JEWELRY MEMBER, AND THAT IS THE POINT. Factories/Tables/Spells/JewelrySpells.cs exists but
    /// generation never reads it: RollItemSpells has exactly four branches - armor level, melee, missile,
    /// caster - and its caller gates them behind `HasArmorLevel(wo) || IsWeapon`. So a plain ring, necklace or
    /// bracelet receives NO item spells at all during generation and can only ever carry enchantments and
    /// cantrips. Offering it JewelrySpells on a reroll would hand it spells it could never have been born
    /// with, which is exactly the class of error this whole file exists to prevent. <see cref="None"/> is the
    /// correct and common answer for that item, not a failure.
    /// </summary>
    public enum RerollItemSpellSource
    {
        /// <summary>No item-spell table applies. The enchantment group is the entire pool.</summary>
        None,

        Armor,
        Melee,
        Missile,
        Caster,
    }

    /// <summary>
    /// The pure, data-only half of the Serpentine spell reroll: which spells an item may legitimately be given,
    /// and what a reroll of a given spell book would produce. Everything here is a static function over plain
    /// values, with no Player, WorldObject, session or database anywhere, so the whole rule set is unit
    /// testable (a Player cannot be constructed in a test in this repo - a static field on it reaches for a
    /// live world database).
    ///
    /// LEVEL IS RECOVERED WITHOUT THE DAT. SpellLevelProgression.GetSpellLevels returns a family's ordered
    /// eight forms, so a spell's level is simply its index in that list, plus one. Spell.Level and
    /// Spell.Formula.Level are both DAT-derived and are known to disagree with each other - that disagreement
    /// is precisely what ACE.Server.Entity.Spell.LevelMatch exists to report - so neither is used here.
    /// </summary>
    public static class SpellRerollTables
    {
        /// <summary>
        /// Every item-spell family carries exactly eight forms, one per level. A family that does not is a
        /// data defect, and is skipped rather than guessed at.
        /// </summary>
        public const int NumLevels = 8;

        /// <summary>
        /// TRUE if a spell is a cantrip of ANY tier. A reroll preserves cantrips exactly and never produces
        /// one, so this is both the skip test on the way in and the pool filter on the way out.
        ///
        /// THE UNION OF ALL FOUR TIERS IS REQUIRED. The only pre-existing accessors in the server
        /// (WorldObject_Magic.cs) test Epic and Legendary alone, because they exist to log rare drops. Reusing
        /// that shape here would quietly reroll away every minor and major cantrip on the item, which is the
        /// one outcome this feature must never produce.
        /// </summary>
        public static bool IsCantrip(int spellId)
        {
            return LootTables.MinorCantrips.Contains(spellId)
                || LootTables.MajorCantrips.Contains(spellId)
                || LootTables.EpicCantrips.Contains(spellId)
                || LootTables.LegendaryCantrips.Contains(spellId);
        }

        /// <summary>
        /// TRUE if a spell selection code names one of the twenty groups that actually exist. Generation
        /// returns 0 from GetSpellSelectionCode_Dynamic when it cannot classify an item, and gives that item
        /// no enchantments at all; a reroll REFUSES instead, because an item whose enchantment pool cannot be
        /// determined is exactly the item whose enchantments must not be replaced by guesswork.
        /// </summary>
        public static bool IsValidSpellSelectionCode(int spellSelectionCode)
        {
            return spellSelectionCode >= 1 && spellSelectionCode <= SpellSelectionTable.NumGroups;
        }

        /// <summary>
        /// Which item-spell table an item draws from, reproducing generation exactly.
        ///
        /// TWO THINGS ARE BEING REPRODUCED, and both matter:
        ///   - the OUTER GATE at LootGenerationFactory_Spells.cs:33, `HasArmorLevel(wo) || IsWeapon`. An item
        ///     that fails it gets NO item spells during generation, so its item-spell source is
        ///     <see cref="RerollItemSpellSource.None"/> - a plain ring or necklace lands here, and that is
        ///     correct rather than a failure to classify;
        ///   - the four branches of RollItemSpells itself (:62-88), IN ORDER. Armor level is tested FIRST,
        ///     which is why a crown - classified as jewelry by generation, yet carrying an armor level - draws
        ///     ARMOR item spells.
        ///
        /// The predicates come off the TreasureRoll rather than being re-derived from PropertyInt.ItemType, so
        /// there is no second opinion to drift. IsMeleeWeapon / IsMissileWeapon / IsCaster partition every
        /// non-Undef TreasureWeaponType between them (TreasureWeaponTypeExtensions), so the final None is
        /// unreachable for a weapon and is a defensive floor only.
        /// </summary>
        /// <param name="roll">from LootGenerationFactory.GetTreasureRoll</param>
        /// <param name="hasArmorLevel">roll.HasArmorLevel(wo), passed in so this stays free of WorldObject</param>
        public static RerollItemSpellSource GetItemSpellSource(TreasureRoll roll, bool hasArmorLevel)
        {
            if (roll == null)
                return RerollItemSpellSource.None;

            // the outer gate: no armor level and not a weapon means no item spells were ever rolled
            if (!hasArmorLevel && !roll.IsWeapon)
                return RerollItemSpellSource.None;

            if (hasArmorLevel)
                return RerollItemSpellSource.Armor;

            if (roll.IsMeleeWeapon)
                return RerollItemSpellSource.Melee;

            if (roll.IsMissileWeapon)
                return RerollItemSpellSource.Missile;

            if (roll.IsCaster)
                return RerollItemSpellSource.Caster;

            return RerollItemSpellSource.None;
        }

        /// <summary>
        /// The item-spell families for a source, each an eight-entry array indexed by level minus one. These
        /// tables are disjoint by construction, which is the whole reason this is keyed at all: mixing them
        /// produces spells the item could never have rolled, such as Hermetic Link or Blood Drinker on a robe.
        ///
        /// JewelrySpells.Table is deliberately absent - generation reads it nowhere. See
        /// <see cref="RerollItemSpellSource"/>.
        /// </summary>
        public static IReadOnlyList<SpellId[]> GetItemSpellFamilies(RerollItemSpellSource source)
        {
            switch (source)
            {
                case RerollItemSpellSource.Armor:
                    return ArmorSpells.Table;

                case RerollItemSpellSource.Melee:
                    return MeleeSpells.Table;

                case RerollItemSpellSource.Missile:
                    return MissileSpells.Table;

                case RerollItemSpellSource.Caster:
                    return WandSpells.Table;

                default:
                    return Array.Empty<SpellId[]>();
            }
        }

        /// <summary>
        /// The candidate pool: the union of the item's item-spell table and its enchantment selection group,
        /// deduplicated by family and with every cantrip family removed. Between them those two ARE the set of
        /// spells generation could have put on this item - item spells from RollItemSpells, enchantments from
        /// RollEnchantments - and the union is required because an item's spell book does not record which
        /// source each spell came from.
        ///
        /// THERE IS NO FALLBACK. <paramref name="spellSelectionCode"/> must already satisfy
        /// <see cref="IsValidSpellSelectionCode"/>; an invalid one contributes nothing here, and the caller is
        /// expected to have refused the reroll outright rather than proceed on a partial pool. Silently
        /// rerolling from a pool the item could not have rolled from is the exact failure this design exists
        /// to prevent, so a narrower pool is never quietly substituted for the right one.
        /// </summary>
        public static List<SpellId[]> BuildCandidatePool(RerollItemSpellSource source, int spellSelectionCode)
        {
            var pool = new List<SpellId[]>();
            var seenFamilies = new HashSet<SpellId>();

            foreach (var family in GetItemSpellFamilies(source))
                TryAddFamily(pool, seenFamilies, family);

            if (IsValidSpellSelectionCode(spellSelectionCode))
            {
                var groupSpells = SpellSelectionTable.GetGroupSpells(spellSelectionCode);

                if (groupSpells != null)
                {
                    foreach (var head in groupSpells)
                    {
                        // the selection tables store the level 1 form; expand it into the whole family so a
                        // level 7 enchantment can be answered with a level 7 replacement
                        var levels = SpellLevelProgression.GetSpellLevels(head);

                        if (levels == null || levels.Count != NumLevels)
                            continue;

                        TryAddFamily(pool, seenFamilies, levels.ToArray());
                    }
                }
            }

            return pool;
        }

        private static void TryAddFamily(List<SpellId[]> pool, HashSet<SpellId> seenFamilies, SpellId[] family)
        {
            if (family == null || family.Length != NumLevels)
                return;

            var head = family[0];

            // the generation-side tables allocate a row before they populate it, so a family whose progression
            // lookup failed at startup is present but left entirely Undef
            if (head == SpellId.Undef)
                return;

            if (IsCantrip((int)head))
                return;

            if (!seenFamilies.Add(head))
                return;

            pool.Add(family);
        }

        /// <summary>
        /// Plans a reroll over an item's spell book. PURE: it decides everything and writes nothing, so the
        /// caller can inspect the plan (and refuse) before any state changes.
        ///
        /// THE RULE, per spell:
        ///   - a cantrip of any tier is preserved exactly;
        ///   - the item's SpellDID and ProcSpell are preserved - they are separate properties that a weapon's
        ///     proc lives on, and rerolling one would silently reassign the weapon's proc;
        ///   - anything else keeps its LEVEL and is replaced by the same-level form of a DIFFERENT family
        ///     drawn from <paramref name="candidatePool"/>;
        ///   - a spell whose family progression is missing, is not <see cref="NumLevels"/> long, or offers no
        ///     legal replacement is LEFT IN PLACE and reported in <see cref="SpellRerollPlan.Skipped"/>. A
        ///     spell is never dropped.
        ///
        /// NO DUPLICATES. The occupied set is seeded with the item's ENTIRE starting spell book and grows with
        /// every replacement written, so a reroll can neither collide with a spell that is staying nor with one
        /// it has just written, and cannot swap two spells for each other and call it a reroll.
        /// </summary>
        /// <param name="currentSpells">the item's biota spell book ids</param>
        /// <param name="candidatePool">from <see cref="BuildCandidatePool"/></param>
        /// <param name="spellDID">the item's PropertyDataId.Spell, if any</param>
        /// <param name="procSpell">the item's PropertyDataId.ProcSpell, if any</param>
        /// <param name="next">returns a value in [0, n) - injected so the plan is reproducible under test</param>
        public static SpellRerollPlan Plan(IEnumerable<int> currentSpells, IReadOnlyList<SpellId[]> candidatePool, uint? spellDID, uint? procSpell, Func<int, int> next)
        {
            var plan = new SpellRerollPlan();

            if (currentSpells == null)
                return plan;

            // a stable order keeps the plan reproducible for a given RNG sequence, which is what makes the
            // tests below meaningful; a dictionary's enumeration order is not a contract
            var ordered = new List<int>(new HashSet<int>(currentSpells));
            ordered.Sort();

            var occupied = new HashSet<int>(ordered);

            var pending = new List<(int spell, int level, SpellId family)>();

            foreach (var spell in ordered)
            {
                if (IsCantrip(spell))
                {
                    plan.PreservedCantrips.Add(spell);
                    continue;
                }

                if ((spellDID.HasValue && spellDID.Value == (uint)spell) || (procSpell.HasValue && procSpell.Value == (uint)spell))
                {
                    plan.PreservedProcs.Add(spell);
                    continue;
                }

                var levels = SpellLevelProgression.GetSpellLevels((SpellId)spell);

                if (levels == null || levels.Count != NumLevels)
                {
                    plan.Skipped.Add(spell);
                    continue;
                }

                var index = levels.IndexOf((SpellId)spell);

                if (index < 0)
                {
                    plan.Skipped.Add(spell);
                    continue;
                }

                pending.Add((spell, index + 1, levels[0]));
            }

            if (candidatePool == null || candidatePool.Count == 0)
            {
                foreach (var entry in pending)
                    plan.Skipped.Add(entry.spell);

                return plan;
            }

            var choices = new List<SpellId>();

            foreach (var entry in pending)
            {
                choices.Clear();

                foreach (var family in candidatePool)
                {
                    // "a DIFFERENT spell" is read at family granularity: answering Flame Bane 7 with
                    // Flame Bane 7 is a no-op, and answering it with another level of the same family would
                    // break the level-preserving contract from the other direction
                    if (family[0] == entry.family)
                        continue;

                    var candidate = family[entry.level - 1];

                    if (candidate == SpellId.Undef || occupied.Contains((int)candidate))
                        continue;

                    choices.Add(candidate);
                }

                if (choices.Count == 0)
                {
                    plan.Skipped.Add(entry.spell);
                    continue;
                }

                var picked = choices[next(choices.Count)];

                occupied.Add((int)picked);

                plan.Swaps.Add(new SpellRerollSwap(entry.spell, (int)picked, entry.level));
            }

            return plan;
        }
    }

    /// <summary>
    /// Why a reroll is refused outright. EVERY ONE OF THESE MEANS "the pool this item could legitimately have
    /// rolled from cannot be determined", and the answer to that is always refusal, never a narrower or
    /// invented pool - rerolling into spells the item could never have been generated with is a worse outcome
    /// than declining the reroll and leaving the player their salvage.
    /// </summary>
    public enum RerollRefusal
    {
        None,

        /// <summary>The wcid is in none of the loot tables, so generation would never have given it spells.</summary>
        Unclassifiable,

        /// <summary>GetSpellSelectionCode_Dynamic could not place the item in one of the twenty groups.</summary>
        NoSpellSelectionCode,

        /// <summary>Classified, but nothing survived the cantrip filter. Should be unreachable; refuse anyway.</summary>
        EmptyPool,

        /// <summary>Classified fine, but the item carries no rerollable enchantment.</summary>
        NothingToReroll,
    }

    /// <summary>
    /// One spell replaced by another at the same level.
    /// </summary>
    public readonly struct SpellRerollSwap
    {
        public readonly int OldSpell;
        public readonly int NewSpell;

        /// <summary>The level both spells share. Carried for logging and for the tests' level assertion.</summary>
        public readonly int Level;

        public SpellRerollSwap(int oldSpell, int newSpell, int level)
        {
            OldSpell = oldSpell;
            NewSpell = newSpell;
            Level = level;
        }
    }

    /// <summary>
    /// The complete outcome of a planned reroll. Every id in the item's starting spell book appears in exactly
    /// one of the four lists, which is the invariant that guarantees no spell is lost.
    /// </summary>
    public class SpellRerollPlan
    {
        public readonly List<SpellRerollSwap> Swaps = new List<SpellRerollSwap>();

        /// <summary>Cantrips of any tier, preserved exactly.</summary>
        public readonly List<int> PreservedCantrips = new List<int>();

        /// <summary>The item's SpellDID / ProcSpell, preserved exactly.</summary>
        public readonly List<int> PreservedProcs = new List<int>();

        /// <summary>Left in place: no usable progression, or no legal replacement. Reported, never dropped.</summary>
        public readonly List<int> Skipped = new List<int>();
    }
}
