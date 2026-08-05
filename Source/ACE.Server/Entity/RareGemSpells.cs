using System;
using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.Entity
{
    /// <summary>
    /// WaffleACE: the "which buffs did a rare gem give you" question, answered in ACE.Server because the answer
    /// lives in the client dat rather than the world DB.
    /// <para/>
    /// WorldDatabaseWithEntityCache.GetRareGemSpellIds returns the BROAD candidate set - every spell cast by a
    /// Gem-type weenie carrying a RareId. That set (138 ids in ace_world) mixes two very different things: the
    /// ordinary player-castable level-8 line (the Incantations and the "Aura of Incantation" family, all of them
    /// teachable from Scroll weenies that ace_world ships), and the Prodigal set, which no scroll teaches and no
    /// player can ever cast. Only the second half should ever be stripped.
    /// <para/>
    /// The discriminator is the spell's SpellCategory, read from client_portal.dat. Probing all 138 candidates
    /// directly showed the mapping is exact in both directions: all 67 "Prodigal" spells carry a rare category
    /// and zero of the other 71 do. Corroborating the intent, a DB check found 0 of the 67 Prodigal spells
    /// teachable by any Scroll weenie, against 64 of 65 Incantations and 4 of 6 Auras - so "rare category" is
    /// precisely "a buff the player could never have cast themselves".
    /// </summary>
    public static class RareGemSpells
    {
        private static volatile HashSet<uint> strippableSpellIds;
        private static readonly object strippableSpellIdsLock = new object();

        /// <summary>
        /// True if <paramref name="category"/> is one of the client's rare-item spell categories.
        /// <para/>
        /// This is a NAME test on the enum member rather than a hand-listed set of values, deliberately. The
        /// rare categories are not a contiguous range and never will be: 76 of SpellCategory's members are rare,
        /// spread over two naming shapes and four disjoint id runs (AcidProtectionRare 448 through
        /// WeaponTimeRaisingRare 512, then TwoHandedRaisingRare 598 / GearCraftRaisingRare 605 /
        /// RareDamageRatingRaising 633 / RareDamageReductionRatingRaising 634 / VoidMagicRaisingRare 648, then
        /// RareDirtyFightingRaising 679 through RareSneakAttackRaising 683, then RareDamageRatingRaising2 694).
        /// An enumerated set would be 76 lines of hand-maintained data that silently goes stale every time the
        /// enum grows to match a new dat, and going stale here means a Prodigal buff quietly surviving the strip.
        /// The name test is derived from the same enum it is testing, so it cannot drift out of sync with it.
        /// <para/>
        /// The cost of that choice, stated plainly: this depends on the enum MEMBER NAMES carrying the rare
        /// marker. Both shapes must be handled - most are "*Rare"-SUFFIXED, but five skill categories plus the
        /// three damage-rating ones are "Rare*"-PREFIXED. The unit tests pin representative members of both
        /// shapes, and assert the predicate matches exactly the 76 rare-named members, so a rename shows up as a
        /// test failure rather than as a live behaviour change.
        /// <para/>
        /// The comparison MUST stay case-sensitive (Ordinal). SpellCategory.ExtraRecklessnessRaising (672)
        /// contains the substring "rare" case-insensitively - "ext-RA-RE-cklessness" - and is not a rare
        /// category; an OrdinalIgnoreCase StartsWith would not catch it, but a careless Contains would.
        /// </summary>
        public static bool IsRareSpellCategory(SpellCategory category)
        {
            var name = Enum.GetName(typeof(SpellCategory), category);

            if (name == null)
                return false;

            return name.EndsWith("Rare", StringComparison.Ordinal)
                || name.StartsWith("Rare", StringComparison.Ordinal);
        }

        /// <summary>
        /// The rare-gem spell ids that are actually safe to strip: the DB candidate set narrowed to spells with a
        /// rare SpellCategory, i.e. the Prodigal set. Computed once and cached, because building it constructs a
        /// <see cref="Spell"/> per candidate id to read the dat category, which is far too much work to repeat on
        /// every arena arrival. Same lazy double-checked idiom as GetRareGemSpellIds itself.
        /// <para/>
        /// Candidates missing from the client dat are skipped - no SpellBase means no category to judge by, and
        /// the safe default is to leave the player's buff alone.
        /// </summary>
        public static HashSet<uint> GetStrippableSpellIds()
        {
            if (strippableSpellIds != null)
                return strippableSpellIds;

            lock (strippableSpellIdsLock)
            {
                if (strippableSpellIds != null)
                    return strippableSpellIds;

                var filtered = new HashSet<uint>();

                foreach (var spellId in ACE.Database.DatabaseManager.World.GetRareGemSpellIds())
                {
                    // loadDB: false - only the dat half (SpellBase.Category) is needed here
                    var spell = new Spell(spellId, false);

                    if (spell._spellBase == null)
                        continue;

                    if (IsRareSpellCategory(spell.Category))
                        filtered.Add(spellId);
                }

                strippableSpellIds = filtered;

                return strippableSpellIds;
            }
        }
    }
}
