using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The AoE/drain spell strip's two dials as one immutable value (dynamic_dungeons_strip_aoe_spells and
    /// dynamic_dungeons_banned_spell_ids), so the archetype classifier can decide on the POST-strip spell book
    /// (orchestrator ruling 2026-10-23: a creature is classified by what it will actually cast) with exactly the rule
    /// the spawner applies (<see cref="DungeonSpellFilter.Strip"/>), and so a cache of classifications can be keyed on
    /// it (<see cref="Key"/>). Read once per plan build at the spawner; an offline tool passes <see cref="Default"/> or
    /// reads its own pinned values with <see cref="FromPropertyManager"/>.
    /// </summary>
    public sealed class SpellStripConfig
    {
        public SpellStripConfig(bool enabled, IEnumerable<int> bannedIds)
        {
            Enabled = enabled;
            BannedIds = new HashSet<int>(bannedIds ?? Enumerable.Empty<int>());
            Key = $"{(enabled ? 1 : 0)}|{string.Join(",", BannedIds.OrderBy(i => i))}";
        }

        /// <summary>The compiled defaults: strip on, the default banned ids.</summary>
        public static readonly SpellStripConfig Default = new SpellStripConfig(DungeonSpellFilter.DefaultStripAoeSpellsEnabled,
            DungeonSpellFilter.ParseBannedIds(DungeonSpellFilter.DefaultBannedSpellIds));

        /// <summary>No strip at all (the classifier then reads the authored book, its pre-2026-10-23 rule).</summary>
        public static readonly SpellStripConfig None = new SpellStripConfig(false, null);

        public bool Enabled { get; }

        public ISet<int> BannedIds { get; }

        /// <summary>A stable identity of the two dials (enabled flag and the sorted banned ids).</summary>
        public string Key { get; }

        /// <summary>The live (or pinned) values, read the way the spawner's strip reads them.</summary>
        public static SpellStripConfig FromPropertyManager()
            => new SpellStripConfig(
                PropertyManager.GetBool("dynamic_dungeons_strip_aoe_spells", DungeonSpellFilter.DefaultStripAoeSpellsEnabled).Item,
                DungeonSpellFilter.ParseBannedIds(PropertyManager.GetString("dynamic_dungeons_banned_spell_ids", DungeonSpellFilter.DefaultBannedSpellIds).Item));

        /// <summary>
        /// The ids the spawner's strip WOULD remove from <paramref name="book"/> (pure: the book is copied, never
        /// touched), including its safety guard (a book that would be emptied on a creature with no damaging body part
        /// is left whole). Empty when the strip is off.
        /// </summary>
        public ISet<int> WouldRemove(IEnumerable<int> book, Func<int, int> projectilesOf, bool hasDamagingBodyPart)
        {
            if (!Enabled || book == null)
                return new HashSet<int>();

            var copy = book.Distinct().ToDictionary(id => id, id => 0f);

            return new HashSet<int>(DungeonSpellFilter.Strip(copy, BannedIds, projectilesOf, hasDamagingBodyPart));
        }
    }
}
