using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// THREADS ONLY (owner ruling 2026-11-21; World Events do not strip these and the shared CombatTraitGuard never calls this).
    /// Called from ThreadDungeonSpawner.PrepareForPlacement for a creature with a Thread run or with
    /// CreatureShapeOptions.StripCasterVitalSpells (Thread speed courses, and the Allegiance Gauntlet by owner ruling 2026-11-22). Balance Trials,
    /// which shape through the same method with neither, keep their spell books.
    /// Removes from an enemy's spell book every spell that scales with the CASTER's own vital: a LifeProjectile with
    /// DrainPercentage above zero (Martyr's Hecatomb / Tenacity / Blight, Curse of Raven Fury). Its damage is the caster's
    /// current vital x DrainPercentage x DamageRatio as an unmitigated Health/Stamina/Mana hit (WorldObject_Magic.cs, the
    /// life-projectile block; SpellProjectile.cs), so a creature the Threads uplift gave 10-100x its authored health casts a
    /// 30,000-point hit with a full-health Hecatomb V. Selected by the spell's own properties, never a hardcoded id list.
    /// Fixed-intensity Health spells (Harm Other, Heart Rend, Exsanguinating Wave, Dark Vortex) have no DrainPercentage and stay.
    ///
    /// PER-INSTANCE SAFETY. Creature.Biota.PropertiesSpellBook is a fresh Dictionary copy for each instance
    /// (WeenieConverter.ConvertToBiota, WeenieConverter.cs:46-47: new Dictionary(weenie.PropertiesSpellBook)), unlike the
    /// reference-shared PropertiesBodyPart / CreateList, so removing from it reaches neither the cached weenie nor a sibling
    /// instance. Pinned by ThreadEnemySpellStripTests against the real converter.
    /// </summary>
    internal static class ThreadEnemySpellStrip
    {
        /// <summary>The selection rule, pure: a life projectile whose damage is a fraction of the caster's own vital.</summary>
        internal static bool IsCasterVitalScaled(SpellType metaSpellType, float drainPercentage)
            => metaSpellType == SpellType.LifeProjectile && drainPercentage > 0f;

        private static readonly ConcurrentDictionary<int, bool> liveCache = new ConcurrentDictionary<int, bool>();

        /// <summary>The rule over the live spell (portal dat MetaSpellType, world DB DrainPercentage); false for an unknown id.</summary>
        internal static bool IsCasterVitalScaled(int spellId)
        {
            if (spellId <= 0)
                return false;

            if (liveCache.TryGetValue(spellId, out var cached))
                return cached;

            var spell = new Spell(spellId);

            // Not cached when the lookup finds nothing: a spell the world DB has not loaded yet must not be remembered as
            // "not caster-scaled" for the life of the process.
            if (spell.NotFound)
                return false;

            var scaled = IsCasterVitalScaled(spell.MetaSpellType, spell.DrainPercentage);
            liveCache[spellId] = scaled;

            return scaled;
        }

        /// <summary>Removes every selected spell from <paramref name="book"/> in place; returns the removed ids in ascending order.</summary>
        internal static List<int> StripBook(IDictionary<int, float> book, Func<int, bool> isCasterVital)
        {
            var removed = new List<int>();

            if (book == null || book.Count == 0)
                return removed;

            foreach (var id in book.Keys.Where(isCasterVital).OrderBy(i => i).ToList())
            {
                book.Remove(id);
                removed.Add(id);
            }

            return removed;
        }

        /// <summary>
        /// The creature's own book, plus the spell data ids of every equipped and Inventory item that would cast one
        /// (ProcSpell, Spell). Changes are tagged "Spell{id}" and "ProcSpell{id}@{item wcid}" for the "combat traits normalized" line.
        /// </summary>
        internal static List<string> Apply(Creature creature, Func<int, bool> isCasterVital = null)
        {
            var changes = new List<string>();

            if (creature == null)
                return changes;

            isCasterVital ??= IsCasterVitalScaled;

            changes.AddRange(StripBook(creature.Biota.PropertiesSpellBook, isCasterVital).Select(id => $"Spell{id}"));

            foreach (var item in creature.EquippedObjects.Values.Concat(creature.Inventory.Values).Where(i => i != null).Distinct())
            {
                foreach (var property in new[] { PropertyDataId.ProcSpell, PropertyDataId.Spell })
                {
                    var id = item.GetProperty(property);

                    if (id.HasValue && isCasterVital((int)id.Value))
                    {
                        item.RemoveProperty(property);
                        changes.Add($"{property}{id.Value}@{item.WeenieClassId}");
                    }
                }
            }

            return changes;
        }
    }
}
