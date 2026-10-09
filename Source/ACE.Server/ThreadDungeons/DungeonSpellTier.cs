using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.Factories.Tables;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The spell-tier axis of the band-standard uplift (<see cref="ThreadDungeonSpawner"/>). Extends the
    /// existing level/skills/melee/armour normalization to a caster's spell book, so a creature reached by
    /// the adaptive band's downward extension throws the band's spells, not the spells its own authored
    /// level carries.
    ///
    /// Pure and side-effect free by design, matching <see cref="DungeonSpellFilter"/>: no database, no dat
    /// files required to exercise the decision logic in a unit test.
    /// </summary>
    public static class DungeonSpellTier
    {
        /// <summary>
        /// A tierable spell family - one Life Magic/War Magic/etc progression with a form at every tier from
        /// I to VIII (Incantation of X through Infliction of X, roughly). <see cref="SpellLevelProgression"/>
        /// also carries 4-long cantrip families (LootGenerationFactory_Spells.cs:288 asserts exactly this
        /// length for a cantrip) and SpellRerollTables.NumLevels pins the SAME 8-long guard for its own
        /// enchantment families - this constant keeps all three from drifting apart.
        /// </summary>
        public const int TierableFamilyLength = 8;

        /// <summary>
        /// The tier of one spell id within its family: 1-based index into the ordered family list
        /// (<see cref="SpellLevelProgression.GetSpellLevels"/>), or 0 ("not tierable") when the spell has no
        /// progression, or its family is not exactly <see cref="TierableFamilyLength"/> long - a cantrip
        /// family (4 long) or any other non-standard progression. 0 is never a valid tier, so callers can
        /// treat it as "leave this id alone" without a separate null check.
        /// </summary>
        public static int TierOf(int spellId)
        {
            var levels = SpellLevelProgression.GetSpellLevels((SpellId)spellId);

            if (levels == null || levels.Count != TierableFamilyLength)
                return 0;

            var index = levels.IndexOf((SpellId)spellId);

            return index < 0 ? 0 : index + 1;
        }

        /// <summary>
        /// Raises every tierable entry in <paramref name="book"/> below <paramref name="tier"/> up to that
        /// tier's form, in place, and returns how many entries were raised.
        ///
        /// Walks the book in its ORIGINAL enumeration order and rebuilds it in that same order - order
        /// matters because Monster_Magic.TryRollSpell_Unconditional walks the book in dictionary order and
        /// casts the first spell that rolls under its probability, so scrambling the order would change
        /// which spell a creature favours even when the set of ids is unchanged.
        ///
        /// A non-tierable id (<see cref="TierOf"/> returns 0) and an id already at or above <paramref
        /// name="tier"/> are left exactly as authored. When raising an id collides with an id already in the
        /// book - either an original entry or an earlier raise's result - the two collapse into ONE entry at
        /// the FIRST position either occupied, keeping the MAXIMUM of their probabilities; nothing here can
        /// silently drop the higher of two probabilities for the same spell.
        ///
        /// <paramref name="tier"/> &lt;= 0 is a no-op (returns 0 without touching the book); a value above 8
        /// is clamped to 8. A null or empty book is a no-op.
        /// </summary>
        public static int Raise(IDictionary<int, float> book, int tier) => Retier(book, tier, lowerToo: false);

        /// <summary>
        /// The TWO-WAY form of <see cref="Raise"/>, for boss normalization (dynamic_dungeons_boss_normalize):
        /// every tierable entry in <paramref name="book"/> is moved to exactly <paramref name="tier"/>'s form,
        /// raised when it sits below and LOWERED when it sits above, and the return value counts how many
        /// entries changed id.
        ///
        /// Identical to Raise in every other respect, and deliberately so - it is the same walk with the
        /// "only upward" test removed: original enumeration order is preserved (Monster_Magic casts the first
        /// spell that rolls), a non-tierable id is left exactly as authored, a collision collapses into ONE
        /// entry at the first position either occupied keeping the MAXIMUM probability, <paramref name="tier"/>
        /// &lt;= 0 is a no-op, and a value above 8 is clamped to 8.
        /// </summary>
        public static int SetTier(IDictionary<int, float> book, int tier) => Retier(book, tier, lowerToo: true);

        private static int Retier(IDictionary<int, float> book, int tier, bool lowerToo)
        {
            if (book == null || book.Count == 0 || tier <= 0)
                return 0;

            if (tier > TierableFamilyLength)
                tier = TierableFamilyLength;

            var raisedCount = 0;
            var orderedIds = new List<int>();
            var probabilityOf = new Dictionary<int, float>();

            foreach (var kvp in book)
            {
                var id = kvp.Key;
                var probability = kvp.Value;
                var spellTier = TierOf(id);

                var finalId = id;

                if (spellTier >= 1 && (spellTier < tier || (lowerToo && spellTier > tier)))
                {
                    var levels = SpellLevelProgression.GetSpellLevels((SpellId)id);
                    finalId = (int)levels[tier - 1];
                    raisedCount++;
                }

                if (probabilityOf.TryGetValue(finalId, out var existingProbability))
                {
                    if (probability > existingProbability)
                        probabilityOf[finalId] = probability;
                }
                else
                {
                    probabilityOf[finalId] = probability;
                    orderedIds.Add(finalId);
                }
            }

            book.Clear();

            foreach (var id in orderedIds)
                book[id] = probabilityOf[id];

            return raisedCount;
        }
    }
}
