using System.Collections.Concurrent;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Memoized spell id to <see cref="MonsterSpellShape"/> lookup for
    /// <see cref="MonsterSpellSelector.TrySelect"/>.
    ///
    /// WHY THIS EXISTS: constructing a Spell heap-allocates a SpellFormula every single time
    /// (Spell.Init), and Creature.TryRollSpell can run TEN times in one tick for one monster when a
    /// projectile spell keeps failing its line-of-sight check (Monster_Combat's reroll loop, capped at
    /// it >= 10). Building a shape per book entry per roll would be 10 * N constructions per tick per
    /// caster. The shape is spell-intrinsic - it reads only the spell's own stat-mod fields and damage
    /// type, never anything about a caster or a target - so one global cache keyed on spell id is
    /// correct for every monster on the server.
    ///
    /// The direct in-subsystem precedent is ThreadDungeonSpawner.spellProjectilesCache, a
    /// ConcurrentDictionary memo over the same GetCachedSpell line, and Factories'
    /// SpellLevelCache, whose shape this follows.
    /// </summary>
    public static class MonsterSpellShapeCache
    {
        private static readonly ConcurrentDictionary<int, MonsterSpellShape> shapes =
            new ConcurrentDictionary<int, MonsterSpellShape>();

        /// <summary>
        /// The shape of one spell, constructed on first request and cached thereafter.
        ///
        /// A spell missing from either the client DAT or the server spell table (Spell.NotFound) caches
        /// the default all-Undef shape, which classifies Neutral. That guard is load-bearing rather than
        /// defensive: Spell's StatModType/StatModKey/StatModVal accessors dereference the server row, so
        /// reading them on a not-found spell throws. A monster whose book names a spell the world
        /// database does not have must keep rolling that entry exactly as it did before, not crash the
        /// tick.
        /// </summary>
        public static MonsterSpellShape Get(int spellId)
        {
            if (shapes.TryGetValue(spellId, out var shape))
                return shape;

            var spell = new Spell(spellId);

            shape = spell.NotFound
                ? default
                : MonsterSpellShape.FromSpellFields(spell.StatModType, spell.StatModKey, spell.StatModVal, spell.DamageType, spell.Category);

            shapes[spellId] = shape;

            return shape;
        }

        /// <summary>
        /// Drops every cached shape. Wired into the same admin paths that already clear the sibling
        /// spell caches (/importsql of a spell, and /clearcache with the Spell bit), so a spell row
        /// edited on a live server is re-read here too.
        /// </summary>
        public static void Clear()
        {
            shapes.Clear();
        }
    }
}
