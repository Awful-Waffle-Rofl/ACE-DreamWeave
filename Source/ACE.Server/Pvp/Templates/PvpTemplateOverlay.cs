using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.Facets;
using ACE.Server.Network.Structure;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// The pure half of the template overlay (Docs/Pvp/TEMPLATES.md "Model: in-place overlay"): capture, apply
    /// and restore against an ACE.Entity.Models.Biota (the RUNTIME biota type, never the EF entity), with no
    /// Player, no session and no database. Player_PvpTemplate.cs calls these for the data writes and then does
    /// the client pushes itself; ACE.Server.Tests calls them directly on a seeded biota.
    ///
    /// EVERY WRITE IS IN PLACE. A CreatureAttribute, CreatureVital or CreatureSkill wraps the biota's own
    /// PropertiesAttribute / PropertiesAttribute2nd / PropertiesSkill object by reference, so replacing a
    /// dictionary entry with a new object would leave the live wrapper pointing at an orphan and silently lose
    /// every later write. Existing rows are mutated; a missing row is added once and then mutated.
    ///
    /// WHAT IS NEVER TOUCHED: experience, skill credits, class ability points, AvailableExperience,
    /// TotalExperience, luminance, level, enlightenment, and every PropertyInt outside
    /// PvpTemplatePowerProperties.ReplacedInts. Attribute and vital CP spent are the XP already INVESTED in a
    /// rank, swapped with the rank; no pool is credited or debited (TEMPLATES.md "Model").
    ///
    /// ABSOLUTE VALUES. The restore writes the saved values, never a delta, so running it twice is the same as
    /// running it once (crash invariant 3).
    /// </summary>
    public static class PvpTemplateOverlay
    {
        public static readonly PropertyAttribute2nd[] MaxVitals =
        {
            PropertyAttribute2nd.MaxHealth,
            PropertyAttribute2nd.MaxStamina,
            PropertyAttribute2nd.MaxMana,
        };

        /// <summary>SpellId.Vitae. Vitae always comes back at exit, whatever pvp_template_keep_own_buffs says.</summary>
        public const int VitaeSpellId = 666;

        /// <summary>
        /// An enchantment granted by an equipped item: active until the item is dequipped (Duration -1, as
        /// EnchantmentManager.BuildEntry writes it for an equip). These belong to the gear, not to the player's
        /// buff bar: own gear's rows leave with the strip and come back with the re-equip, the kit's rows leave
        /// when the kit is destroyed. The overlay never saves, clears or restores them.
        ///
        /// VITAE IS NOT ITEM-GRANTED, though it also carries Duration -1: the vitae row is written with the
        /// same "until removed" duration (Player_Spells.AuditItemSpells and EnchantmentManager both exclude it
        /// from the item-spell set by SpellId for exactly this reason). Left as item-granted, a player with vitae
        /// would carry the penalty into the match and it would never be held in the record.
        /// </summary>
        public static bool IsItemGranted(PropertiesEnchantmentRegistry e) => e.Duration == -1 && !IsVitae(e);

        /// <summary>A shared-cooldown row (spell id 0x8000 + cooldown id, EnchantmentManager.StartCooldown).</summary>
        public static bool IsCooldown(PropertiesEnchantmentRegistry e) => e.SpellId > short.MaxValue;

        public static bool IsVitae(PropertiesEnchantmentRegistry e) => e.SpellId == VitaeSpellId;

        // ======================================================================================
        // capture
        // ======================================================================================

        /// <summary>
        /// Builds the restore record from the player's CURRENT state, before anything is written. Pure read.
        /// </summary>
        public static PvpTemplateRestoreRecord Capture(Biota biota, ReaderWriterLockSlim rwLock, PvpTemplateDefinition definition,
            Guid matchId, DateTime appliedAtUtc, bool returnOwnBuffs, List<FacetEquipEntry> ownEquip)
        {
            if (biota == null) throw new ArgumentNullException(nameof(biota));
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            var record = new PvpTemplateRestoreRecord
            {
                MatchId = matchId,
                TemplateKey = definition.Key,
                TemplateVersion = definition.Version,
                AppliedAtUtc = appliedAtUtc,
                ReturnOwnBuffs = returnOwnBuffs,
                OwnEquip = ownEquip ?? new List<FacetEquipEntry>(),
                TemplateSpells = definition.Spells.Distinct().ToList(),
            };

            rwLock.EnterReadLock();
            try
            {
                foreach (var attribute in FacetAttributes.PrimaryAttributes)
                {
                    PropertiesAttribute row = null;
                    biota.PropertiesAttribute?.TryGetValue(attribute, out row);

                    record.Attributes[(int)attribute] = new PvpTemplateAttributeEntry
                    {
                        InitLevel = row?.InitLevel ?? 0,
                        Ranks = row?.LevelFromCP ?? 0,
                        CpSpent = row?.CPSpent ?? 0,
                    };
                }

                foreach (var vital in MaxVitals)
                {
                    PropertiesAttribute2nd row = null;
                    biota.PropertiesAttribute2nd?.TryGetValue(vital, out row);

                    record.Vitals[(int)vital] = new PvpTemplateVitalEntry
                    {
                        InitLevel = row?.InitLevel ?? 0,
                        Ranks = row?.LevelFromCP ?? 0,
                        CpSpent = row?.CPSpent ?? 0,
                        Current = row?.CurrentLevel ?? 0,
                    };
                }

                if (biota.PropertiesSkill != null)
                {
                    foreach (var kvp in biota.PropertiesSkill)
                    {
                        record.Skills.Add(new FacetSkillEntry
                        {
                            Skill = kvp.Key,
                            Sac = kvp.Value.SAC,
                            Ranks = kvp.Value.LevelFromPP,
                            Pp = kvp.Value.PP,
                            InitLevel = kvp.Value.InitLevel,
                        });
                    }
                }

                foreach (var property in PvpTemplatePowerProperties.ReplacedInts)
                {
                    int? value = null;

                    if (biota.PropertiesInt != null && biota.PropertiesInt.TryGetValue(property, out var v))
                        value = v;

                    record.Ints[(int)property] = value;
                }

                if (biota.PropertiesEnchantmentRegistry != null)
                {
                    foreach (var entry in biota.PropertiesEnchantmentRegistry)
                    {
                        if (!IsItemGranted(entry))
                            record.RemovedEnchantments.Add(PvpTemplateEnchantment.From(entry));
                    }
                }

                foreach (var spell in record.TemplateSpells)
                {
                    if (biota.PropertiesSpellBook == null || !biota.PropertiesSpellBook.ContainsKey(spell))
                        record.AddedSpells.Add(spell);
                }
            }
            finally
            {
                rwLock.ExitReadLock();
            }

            return record;
        }

        // ======================================================================================
        // apply / restore: the build data (attributes, vitals, skills, ints, spells)
        // ======================================================================================

        /// <summary>
        /// Writes the template's build onto the biota: the six attributes (innate, ranks, CP spent), the three
        /// vitals' ranks and CP spent, every skill (authoritatively: a skill the template does not name becomes
        /// Untrained with nothing invested), every replaced PropertyInt (removed where the template has none), and
        /// the template spells the player lacks. Current vitals and enchantments are the caller's: the maximum a
        /// full vital fills to depends on enchantments and gear, which only the live player can compute.
        /// </summary>
        public static void ApplyBuild(Biota biota, ReaderWriterLockSlim rwLock, PvpTemplateDefinition definition)
        {
            if (biota == null) throw new ArgumentNullException(nameof(biota));
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            rwLock.EnterWriteLock();
            try
            {
                foreach (var attribute in FacetAttributes.PrimaryAttributes)
                {
                    if (!definition.Attributes.TryGetValue((int)attribute, out var entry) || entry == null)
                        continue;

                    var row = GetOrAddAttribute(biota, attribute);
                    row.InitLevel = entry.InitLevel;
                    row.LevelFromCP = entry.Ranks;
                    row.CPSpent = entry.CpSpent;
                }

                foreach (var vital in MaxVitals)
                {
                    if (!definition.Vitals.TryGetValue((int)vital, out var entry) || entry == null)
                        continue;

                    var row = GetOrAddVital(biota, vital);
                    row.InitLevel = entry.InitLevel;
                    row.LevelFromCP = entry.Ranks;
                    row.CPSpent = entry.CpSpent;
                }

                WriteSkillsAuthoritative(biota, definition.Skills);

                biota.PropertiesInt ??= new Dictionary<PropertyInt, int>();

                foreach (var property in PvpTemplatePowerProperties.ReplacedInts)
                {
                    if (definition.PowerInts.TryGetValue((int)property, out var value))
                        biota.PropertiesInt[property] = value;
                    else
                        biota.PropertiesInt.Remove(property);
                }

                foreach (var spell in definition.Spells)
                {
                    biota.PropertiesSpellBook ??= new Dictionary<int, float>();

                    if (!biota.PropertiesSpellBook.ContainsKey(spell))
                        biota.PropertiesSpellBook[spell] = 2.0f;
                }
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Writes the record's saved build back: attributes, vitals (ranks, CP spent AND the saved current value,
        /// unclamped - the live caller clamps it against the restored maximum), skills authoritatively, every
        /// replaced PropertyInt (removed where the player had none), and removes the spells the apply added.
        /// Enchantments are the caller's (see <see cref="PlanRestoredEnchantments"/>).
        /// </summary>
        public static void RestoreBuild(Biota biota, ReaderWriterLockSlim rwLock, PvpTemplateRestoreRecord record)
        {
            if (biota == null) throw new ArgumentNullException(nameof(biota));
            if (record == null) throw new ArgumentNullException(nameof(record));

            rwLock.EnterWriteLock();
            try
            {
                foreach (var attribute in FacetAttributes.PrimaryAttributes)
                {
                    if (!record.Attributes.TryGetValue((int)attribute, out var entry) || entry == null)
                        continue;

                    var row = GetOrAddAttribute(biota, attribute);
                    row.InitLevel = entry.InitLevel;
                    row.LevelFromCP = entry.Ranks;
                    row.CPSpent = entry.CpSpent;
                }

                foreach (var vital in MaxVitals)
                {
                    if (!record.Vitals.TryGetValue((int)vital, out var entry) || entry == null)
                        continue;

                    var row = GetOrAddVital(biota, vital);
                    row.InitLevel = entry.InitLevel;
                    row.LevelFromCP = entry.Ranks;
                    row.CPSpent = entry.CpSpent;
                    row.CurrentLevel = entry.Current;
                }

                WriteSkillsAuthoritative(biota, record.Skills);

                biota.PropertiesInt ??= new Dictionary<PropertyInt, int>();

                foreach (var pair in record.Ints)
                {
                    var property = (PropertyInt)pair.Key;

                    // Only ever a replaced property: a tampered or stale record naming anything else (a pool, a
                    // level) is ignored rather than trusted, so the restore can never write outside the table.
                    if (!PvpTemplatePowerProperties.ReplacedInts.Contains(property))
                        continue;

                    if (pair.Value.HasValue)
                        biota.PropertiesInt[property] = pair.Value.Value;
                    else
                        biota.PropertiesInt.Remove(property);
                }

                if (biota.PropertiesSpellBook != null)
                {
                    foreach (var spell in record.AddedSpells)
                        biota.PropertiesSpellBook.Remove(spell);
                }
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// The authoritative skill sweep, on the biota. Same contract as Player.ApplyFacetSkills (which the live
        /// path then calls for the client push): every named skill is written, every other skill row is reset to
        /// Untrained with no ranks, no PP and no init level. Rows are never removed, because a live CreatureSkill
        /// may already wrap them.
        /// </summary>
        private static void WriteSkillsAuthoritative(Biota biota, List<FacetSkillEntry> incoming)
        {
            if (incoming == null)
                return;

            biota.PropertiesSkill ??= new Dictionary<Skill, PropertiesSkill>();

            var named = new HashSet<Skill>();

            foreach (var entry in incoming)
            {
                if (entry == null)
                    continue;

                if (!biota.PropertiesSkill.TryGetValue(entry.Skill, out var row))
                {
                    row = new PropertiesSkill();
                    biota.PropertiesSkill[entry.Skill] = row;
                }

                row.SAC = entry.Sac;
                row.LevelFromPP = entry.Ranks;
                row.PP = entry.Pp;
                row.InitLevel = entry.InitLevel;

                named.Add(entry.Skill);
            }

            foreach (var kvp in biota.PropertiesSkill)
            {
                if (named.Contains(kvp.Key))
                    continue;

                kvp.Value.SAC = SkillAdvancementClass.Untrained;
                kvp.Value.LevelFromPP = 0;
                kvp.Value.PP = 0;
                kvp.Value.InitLevel = 0;
            }
        }

        private static PropertiesAttribute GetOrAddAttribute(Biota biota, PropertyAttribute attribute)
        {
            biota.PropertiesAttribute ??= new Dictionary<PropertyAttribute, PropertiesAttribute>();

            if (!biota.PropertiesAttribute.TryGetValue(attribute, out var row))
            {
                row = new PropertiesAttribute();
                biota.PropertiesAttribute[attribute] = row;
            }

            return row;
        }

        private static PropertiesAttribute2nd GetOrAddVital(Biota biota, PropertyAttribute2nd vital)
        {
            biota.PropertiesAttribute2nd ??= new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>();

            if (!biota.PropertiesAttribute2nd.TryGetValue(vital, out var row))
            {
                row = new PropertiesAttribute2nd();
                biota.PropertiesAttribute2nd[vital] = row;
            }

            return row;
        }

        // ======================================================================================
        // enchantments
        // ======================================================================================

        /// <summary>Every enchantment row that is not item-granted: what the apply clears and the restore replaces.</summary>
        public static List<PropertiesEnchantmentRegistry> NonItemEnchantments(Biota biota, ReaderWriterLockSlim rwLock)
        {
            if (biota?.PropertiesEnchantmentRegistry == null)
                return new List<PropertiesEnchantmentRegistry>();

            rwLock.EnterReadLock();
            try
            {
                return biota.PropertiesEnchantmentRegistry.Where(e => !IsItemGranted(e)).ToList();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// The template buff set as fresh registry rows: full duration (StartTime 0), cast by the player
        /// themselves (<paramref name="casterGuid"/>). Item rows, cooldowns and vitae never reach a definition
        /// (PvpTemplateCapture filters them) and are filtered again here in case a definition was hand-edited.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> BuildTemplateBuffs(PvpTemplateDefinition definition, uint casterGuid)
        {
            var result = new List<PropertiesEnchantmentRegistry>();

            foreach (var buff in definition.Buffs)
            {
                var entry = buff.ToRegistry();

                if (IsItemGranted(entry) || IsCooldown(entry) || IsVitae(entry))
                    continue;

                entry.StartTime = 0;
                entry.LastTimeDegraded = 0;
                entry.CasterObjectId = casterGuid;

                result.Add(entry);
            }

            return result;
        }

        /// <summary>
        /// The entries of <paramref name="entries"/> whose SpellId is NOT already in the player's enchantment registry,
        /// read under <paramref name="rwLock"/>. The battleground respawn re-apply (Player.ReapplyPvpTemplateBuffsAfterRespawn)
        /// adds only these: a template buff still present (the death purge is off, which is the default) is never added
        /// alongside itself and never has its duration refreshed; one the purge removed, or one that expired, comes back.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> AbsentBySpellId(Biota biota, ReaderWriterLockSlim rwLock, IEnumerable<PropertiesEnchantmentRegistry> entries)
        {
            var candidates = entries?.Where(e => e != null).ToList() ?? new List<PropertiesEnchantmentRegistry>();

            if (candidates.Count == 0 || biota?.PropertiesEnchantmentRegistry == null)
                return candidates;

            rwLock.EnterReadLock();
            try
            {
                var present = biota.PropertiesEnchantmentRegistry.Select(e => e.SpellId).ToHashSet();
                return candidates.Where(e => !present.Contains(e.SpellId)).ToList();
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// The rows the restore puts back: every saved row, or with ReturnOwnBuffs off only vitae and cooldowns.
        /// Saved StartTime is kept as is, and that is the whole duration semantics: a registry row's StartTime
        /// counts DOWN from 0 by each heartbeat interval while its holder is active
        /// (PropertiesEnchantmentRegistryExtensions.HeartBeatEnchantmentsAndReturnExpired), and the remaining time
        /// is Duration + StartTime. A row held in the record is not ticked, so an own buff comes back with exactly
        /// the time it had left at match entry - the match does not consume it.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> PlanRestoredEnchantments(PvpTemplateRestoreRecord record)
        {
            var result = new List<PropertiesEnchantmentRegistry>();

            foreach (var saved in record.RemovedEnchantments)
            {
                var entry = saved.ToRegistry();

                if (!record.ReturnOwnBuffs && !IsVitae(entry) && !IsCooldown(entry))
                    continue;

                result.Add(entry);
            }

            return result;
        }

        /// <summary>
        /// The client-side keys for a dispel of these rows. Vitae is stored at LayerId 1 (so the shard's unique index
        /// accepts it) but every wire path presents it at layer 0 to match retail (Enchantment.Write,
        /// EnchantmentManager.SendUpdateVitae), so a dispel naming (666, layer 1) would miss the client's (666, layer 0)
        /// and the vitae display would stay. Every other row keeps its own LayerId.
        /// </summary>
        public static List<LayeredSpell> ToDispelWire(IEnumerable<PropertiesEnchantmentRegistry> entries)
        {
            return (entries ?? Enumerable.Empty<PropertiesEnchantmentRegistry>())
                .Where(e => e != null)
                .Select(e => new LayeredSpell((ushort)e.SpellId, IsVitae(e) ? (ushort)0 : e.LayerId))
                .ToList();
        }
        /// <summary>Removes the given rows from the registry (by reference, else by key). Pure registry edit.</summary>
        public static void RemoveEnchantments(Biota biota, ReaderWriterLockSlim rwLock, IEnumerable<PropertiesEnchantmentRegistry> entries)
        {
            if (biota?.PropertiesEnchantmentRegistry == null || entries == null)
                return;

            foreach (var entry in entries.ToList())
                biota.PropertiesEnchantmentRegistry.TryRemoveEnchantment(entry, rwLock);
        }

        /// <summary>
        /// Appends each row at its own LayerId when no row for the same spell holds that layer, else at the lowest
        /// free layer for that spell. The shard's unique index over (object_Id, spell_Id, layer_Id) cannot store
        /// two rows for one spell at one layer, and a failed biota save disconnects the player
        /// (PropertiesEnchantmentRegistryExtensions.AddEnchantmentAtFreeLayer's remarks); keeping the saved layer
        /// when it is free keeps top-layer selection exactly as it was. Returns the rows as added.
        /// </summary>
        public static List<PropertiesEnchantmentRegistry> AddEnchantmentsPreservingLayer(Biota biota, ReaderWriterLockSlim rwLock, IEnumerable<PropertiesEnchantmentRegistry> entries)
        {
            var added = new List<PropertiesEnchantmentRegistry>();

            if (biota == null || entries == null)
                return added;

            rwLock.EnterWriteLock();
            try
            {
                biota.PropertiesEnchantmentRegistry ??= new List<PropertiesEnchantmentRegistry>();

                var registry = biota.PropertiesEnchantmentRegistry;

                foreach (var entry in entries)
                {
                    if (entry == null)
                        continue;

                    if (entry.LayerId == 0 || registry.Any(e => e.SpellId == entry.SpellId && e.LayerId == entry.LayerId))
                    {
                        ushort layer = 1;

                        while (registry.Any(e => e.SpellId == entry.SpellId && e.LayerId == layer))
                            layer++;

                        entry.LayerId = layer;
                    }

                    registry.Add(entry);
                    added.Add(entry);
                }
            }
            finally
            {
                rwLock.ExitWriteLock();
            }

            return added;
        }
    }
}
