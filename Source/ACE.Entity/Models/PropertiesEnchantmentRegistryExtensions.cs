using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Entity.Enum;

namespace ACE.Entity.Models
{
    public static class PropertiesEnchantmentRegistryExtensions
    {
        public static List<PropertiesEnchantmentRegistry> Clone(this ICollection<PropertiesEnchantmentRegistry> value, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                return value.ToList();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }


        public static bool HasEnchantments(this ICollection<PropertiesEnchantmentRegistry> value, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return false;

            rwLock.EnterReadLock();
            try
            {
                return value.Any();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public static bool HasEnchantment(this ICollection<PropertiesEnchantmentRegistry> value, uint spellId, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return false;

            rwLock.EnterReadLock();
            try
            {
                return value.Any(e => e.SpellId == spellId);
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// TRUE if any entry's SpellId is in <paramref name="spellIds"/> - a membership test against a whole set
        /// in one pass, with no allocation (unlike <see cref="Clone"/>, which materializes a new List). Used as
        /// a cheap pre-check before a caller that WOULD clone the registry (e.g. Player.StripRareGemBuffs), so a
        /// player carrying none of the set's spells never pays that clone's cost.
        /// </summary>
        public static bool HasAnyEnchantment(this ICollection<PropertiesEnchantmentRegistry> value, ICollection<uint> spellIds, ReaderWriterLockSlim rwLock)
        {
            if (value == null || spellIds == null || spellIds.Count == 0)
                return false;

            rwLock.EnterReadLock();
            try
            {
                return value.Any(e => spellIds.Contains((uint)e.SpellId));
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public static PropertiesEnchantmentRegistry GetEnchantmentBySpell(this ICollection<PropertiesEnchantmentRegistry> value, int spellId, uint? casterGuid, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                var results = value.Where(e => e.SpellId == spellId);

                if (casterGuid != null)
                    results = results.Where(e => e.CasterObjectId == casterGuid);

                return results.FirstOrDefault();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public static PropertiesEnchantmentRegistry GetEnchantmentBySpellSet(this ICollection<PropertiesEnchantmentRegistry> value, int spellId, EquipmentSet spellSetId, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                return value.FirstOrDefault(e => e.SpellId == spellId && e.SpellSetId == spellSetId);
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public static List<PropertiesEnchantmentRegistry> GetEnchantmentsByCategory(this ICollection<PropertiesEnchantmentRegistry> value, SpellCategory spellCategory, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                return value.Where(e => e.SpellCategory == spellCategory).ToList();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public static List<PropertiesEnchantmentRegistry> GetEnchantmentsByStatModType(this ICollection<PropertiesEnchantmentRegistry> value, EnchantmentTypeFlags statModType, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                return value.Where(e => (e.StatModType & statModType) == statModType).ToList();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        // this ensures level 8 item self spells always take precedence over level 8 item other spells
        private static HashSet<int> Level8AuraSelfSpells = new HashSet<int>
        {
            (int)SpellId.BloodDrinkerSelf8,
            (int)SpellId.DefenderSelf8,
            (int)SpellId.HeartSeekerSelf8,
            (int)SpellId.SpiritDrinkerSelf8,
            (int)SpellId.SwiftKillerSelf8,
            (int)SpellId.HermeticLinkSelf8,
        };

        public static List<PropertiesEnchantmentRegistry> GetEnchantmentsTopLayer(this ICollection<PropertiesEnchantmentRegistry> value, ReaderWriterLockSlim rwLock, HashSet<int> setSpells)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                var results = from e in value
                    group e by e.SpellCategory
                    into categories
                    //select categories.OrderByDescending(c => c.LayerId).First();
                    select categories.OrderByDescending(c => c.PowerLevel)
                        .ThenByDescending(c => Level8AuraSelfSpells.Contains(c.SpellId))
                        .ThenByDescending(c => setSpells.Contains(c.SpellId) ? c.SpellId : c.StartTime).First();

                return results.ToList();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Returns the top layers in each spell category for a StatMod type
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> GetEnchantmentsTopLayerByStatModType(this ICollection<PropertiesEnchantmentRegistry> value, EnchantmentTypeFlags statModType, ReaderWriterLockSlim rwLock, HashSet<int> setSpells)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                var valuesByStatModType = value.Where(e => (e.StatModType & statModType) == statModType);

                var results = from e in valuesByStatModType
                    group e by e.SpellCategory
                    into categories
                    //select categories.OrderByDescending(c => c.LayerId).First();
                    select categories.OrderByDescending(c => c.PowerLevel)
                        .ThenByDescending(c => Level8AuraSelfSpells.Contains(c.SpellId))
                        .ThenByDescending(c => setSpells.Contains(c.SpellId) ? c.SpellId : c.StartTime).First();

                return results.ToList();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Returns the top layers in each spell category for a StatMod type + key
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> GetEnchantmentsTopLayerByStatModType(this ICollection<PropertiesEnchantmentRegistry> value, EnchantmentTypeFlags statModType, uint statModKey, ReaderWriterLockSlim rwLock, HashSet<int> setSpells, bool handleMultiple = false)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                var multipleStat = EnchantmentTypeFlags.Undef;

                if (handleMultiple)
                {
                    // todo: this is starting to get a bit messy here, EnchantmentTypeFlags handling should be more adaptable
                    // perhaps the enchantment registry in acclient should be investigated for reference logic

                    multipleStat = statModType | EnchantmentTypeFlags.MultipleStat;

                    statModType |= EnchantmentTypeFlags.SingleStat;
                }

                var valuesByStatModTypeAndKey = value.Where(e => (e.StatModType & statModType) == statModType && e.StatModKey == statModKey || (handleMultiple && (e.StatModType & multipleStat) == multipleStat && (e.StatModType & EnchantmentTypeFlags.Vitae) == 0 && e.StatModKey == 0));

                // 3rd spell id sort added for Gauntlet Damage Boost I / Gauntlet Damage Boost II, which is contained in multiple sets, and can overlap
                // without this sorting criteria, it's already matched up to the client, but produces logically incorrect results for server spell stacking
                // confirmed this bug still exists in acclient Enchantment.Duel(), unknown if it existed in retail server

                var results = from e in valuesByStatModTypeAndKey
                    group e by e.SpellCategory
                    into categories
                    //select categories.OrderByDescending(c => c.LayerId).First();
                    select categories.OrderByDescending(c => c.PowerLevel)
                        .ThenByDescending(c => Level8AuraSelfSpells.Contains(c.SpellId))
                        .ThenByDescending(c => setSpells.Contains(c.SpellId) ? c.SpellId : c.StartTime).First();

                return results.ToList();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public static List<PropertiesEnchantmentRegistry> HeartBeatEnchantmentsAndReturnExpired(this ICollection<PropertiesEnchantmentRegistry> value, double heartbeatInterval, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return null;

            rwLock.EnterReadLock();
            try
            {
                var expired = new List<PropertiesEnchantmentRegistry>();

                foreach (var enchantment in value)
                {
                    enchantment.StartTime -= heartbeatInterval;

                    // StartTime ticks backwards to -Duration
                    if (enchantment.Duration >= 0 && enchantment.StartTime <= -enchantment.Duration)
                        expired.Add(enchantment);
                }

                return expired;
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public static void AddEnchantment(this ICollection<PropertiesEnchantmentRegistry> value, PropertiesEnchantmentRegistry entity, ReaderWriterLockSlim rwLock)
        {
            rwLock.EnterWriteLock();
            try
            {
                value.Add(entity);
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Appends an entry after assigning it the lowest layer id, starting at 1, that no other entry for the
        /// SAME spell id is already using. Returns the layer that was assigned.
        ///
        /// The shard table has a unique index over (object_Id, spell_Id, layer_Id) on top of its wider primary
        /// key, so two entries for one spell at one layer with different casters are a runtime state the
        /// database cannot store: the save fails with a duplicate-entry error, and a failed biota save
        /// disconnects a player. Any caller that appends an entry with a hand-picked layer instead of one
        /// derived from the entries already present can produce that state.
        ///
        /// Only same-spell layers are skipped, deliberately. Layer numbers are compared per spell by the
        /// unique index and per category by top-layer selection, and widening the search to the whole category
        /// would renumber unrelated entries - all cooldowns share one synthetic category and every one of them
        /// legitimately sits at layer 1, since their spell ids differ.
        ///
        /// The read and the append happen under ONE write lock, so two concurrent callers cannot both pick the
        /// same layer.
        /// </summary>
        public static ushort AddEnchantmentAtFreeLayer(this ICollection<PropertiesEnchantmentRegistry> value, PropertiesEnchantmentRegistry entity, ReaderWriterLockSlim rwLock)
        {
            rwLock.EnterWriteLock();
            try
            {
                ushort layer = 1;

                while (value.Any(e => e.SpellId == entity.SpellId && e.LayerId == layer))
                    layer++;

                entity.LayerId = layer;

                value.Add(entity);

                return layer;
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Makes <paramref name="entity"/>'s spell id represented by EXACTLY ONE entry, and returns that entry.
        ///
        /// With no entry for the spell id, <paramref name="entity"/> is appended as given. Otherwise the FIRST
        /// existing entry for that spell id is refreshed IN PLACE from <paramref name="entity"/> - every field
        /// except SpellId, LayerId and CasterObjectId is copied - and any further entries for the same spell id
        /// are removed from the collection and returned through <paramref name="removed"/>.
        ///
        /// The surviving entry deliberately keeps its own LayerId and CasterObjectId. Together with SpellId and
        /// the object id those are the shard table's primary key, and BiotaUpdater matches persisted rows on
        /// (SpellId, LayerId, CasterObjectId). Changing either would make the next save delete one row and
        /// insert another with the same (object_Id, spell_Id, layer_Id) under the table's unique index, which
        /// relies on EF Core ordering the delete first. Keeping the key stable makes the save a plain UPDATE.
        ///
        /// The first entry is the one kept because it is the one BiotaUpdater persists when several share a
        /// (SpellId, LayerId): it stages the first and drops the rest. So the row already in the database, if
        /// any, is the one refreshed here, and the removed extras were never stored.
        ///
        /// The lookup, removal and append all happen under ONE write lock.
        /// </summary>
        public static PropertiesEnchantmentRegistry AddOrRefreshBySpell(this ICollection<PropertiesEnchantmentRegistry> value, PropertiesEnchantmentRegistry entity, ReaderWriterLockSlim rwLock, out List<PropertiesEnchantmentRegistry> removed)
        {
            removed = null;

            rwLock.EnterWriteLock();
            try
            {
                PropertiesEnchantmentRegistry survivor = null;

                foreach (var existing in value)
                {
                    if (existing.SpellId != entity.SpellId)
                        continue;

                    if (survivor == null)
                        survivor = existing;
                    else
                    {
                        // ACE.Entity targets netstandard2.0 (C# 7.3), so no ??= here
                        if (removed == null)
                            removed = new List<PropertiesEnchantmentRegistry>();

                        removed.Add(existing);
                    }
                }

                if (survivor == null)
                {
                    value.Add(entity);
                    return entity;
                }

                if (removed != null)
                {
                    foreach (var extra in removed)
                        value.Remove(extra);
                }

                survivor.EnchantmentCategory = entity.EnchantmentCategory;
                survivor.HasSpellSetId = entity.HasSpellSetId;
                survivor.SpellCategory = entity.SpellCategory;
                survivor.PowerLevel = entity.PowerLevel;
                survivor.StartTime = entity.StartTime;
                survivor.Duration = entity.Duration;
                survivor.DegradeModifier = entity.DegradeModifier;
                survivor.DegradeLimit = entity.DegradeLimit;
                survivor.LastTimeDegraded = entity.LastTimeDegraded;
                survivor.StatModType = entity.StatModType;
                survivor.StatModKey = entity.StatModKey;
                survivor.StatModValue = entity.StatModValue;
                survivor.SpellSetId = entity.SpellSetId;

                return survivor;
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Removes exactly the registry entry <paramref name="entry"/> identifies, and returns TRUE if one was removed.
        ///
        /// (SpellId, CasterObjectId) is NOT an identity for an entry, and this used to match on nothing else. Two
        /// entries can share that pair legitimately:
        ///   - in different CATEGORIES: a class-ability debuff borrows a real spell id into a synthetic category
        ///     (Pocket Sand lays DF_Specialized_AttackDebuff in SpellCategory_ClassAbility_PocketSand, beside the
        ///     same player's real Dirty Fighting debuff in DFAttackSkillDebuff);
        ///   - at different LAYERS of one category: a monster DebuffEffect lays ImperilOther4 in ArmorValueLowering,
        ///     and the same monster's real Imperil IV cast then surpasses it onto the next layer.
        /// With the old match, expiring or dispelling either entry deleted whichever of the two came first, so a
        /// live debuff was stripped early and the expired one lingered until the next heartbeat.
        ///
        /// The match is by REFERENCE first. Every caller passes an object it read out of this same registry
        /// (HeartBeat's expired list, a Clone snapshot, a GetEnchantment* lookup), and a reference cannot pick a
        /// sibling. Only when the object itself is no longer present does it fall back to the full identity
        /// (SpellId, CasterObjectId, SpellCategory, LayerId), so a value-equal copy still removes its original.
        /// The fallback can never cross a category or layer boundary, which is the whole fix; it CAN still take a
        /// true duplicate that agrees on all four fields, a state the shard's unique index over
        /// (object_Id, spell_Id, layer_Id) cannot store anyway.
        /// </summary>
        public static bool TryRemoveEnchantment(this ICollection<PropertiesEnchantmentRegistry> value, PropertiesEnchantmentRegistry entry, ReaderWriterLockSlim rwLock)
        {
            if (value == null || entry == null)
                return false;

            rwLock.EnterWriteLock();
            try
            {
                PropertiesEnchantmentRegistry entity = null;

                foreach (var existing in value)
                {
                    if (ReferenceEquals(existing, entry))
                    {
                        entity = existing;
                        break;
                    }
                }

                if (entity == null)
                {
                    foreach (var existing in value)
                    {
                        if (existing.SpellId == entry.SpellId && existing.CasterObjectId == entry.CasterObjectId
                            && existing.SpellCategory == entry.SpellCategory && existing.LayerId == entry.LayerId)
                        {
                            entity = existing;
                            break;
                        }
                    }
                }

                if (entity != null)
                {
                    // PropertiesEnchantmentRegistry does not override Equals, so this Remove is by reference too
                    value.Remove(entity);

                    return true;
                }

                return false;
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        public static void RemoveAllEnchantments(this ICollection<PropertiesEnchantmentRegistry> value, IEnumerable<int> spellsToExclude, ReaderWriterLockSlim rwLock)
        {
            if (value == null)
                return;

            rwLock.EnterWriteLock();
            try
            {
                var enchantments = value.Where(e => !spellsToExclude.Contains(e.SpellId)).ToList();

                foreach (var enchantment in enchantments)
                    value.Remove(enchantment);
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }
    }
}
