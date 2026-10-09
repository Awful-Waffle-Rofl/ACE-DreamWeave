using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.Facets;

namespace ACE.Server.Pvp.Templates
{
    /// <summary>
    /// Snapshot capture and kit cloning, the pure halves (TEMPLATES.md "Templates" and "Issued kit").
    ///
    /// BOTH TAKE THE RUNTIME BIOTA (ACE.Entity.Models.Biota). The live snapshot reads the template character's
    /// possessions from the shard as EF entities (ACE.Database.Models.Shard.Biota) and converts each one with
    /// ACE.Database.Adapter.BiotaConverter.ConvertToEntityBiota BEFORE anything here sees it - the two Biota types
    /// are not interchangeable (repo CLAUDE.md), and nothing in this file may take the EF one.
    /// </summary>
    public static class PvpTemplateKit
    {
        /// <summary>
        /// Item PropertyInts never carried into a kit item. Location and ownership are the clone's own, and the
        /// economy stamps are overwritten by <see cref="BuildIssuedBiota"/> anyway.
        /// </summary>
        private static readonly HashSet<PropertyInt> DroppedItemInts = new HashSet<PropertyInt>
        {
            PropertyInt.CurrentWieldedLocation,
            PropertyInt.PlacementPosition,
            PropertyInt.Attuned,
            PropertyInt.Bonded,
            PropertyInt.Value,
        };

        // ======================================================================================
        // capture
        // ======================================================================================

        /// <summary>
        /// Builds a definition from a template character and its possessions. <paramref name="skipped"/> names each
        /// possession left out of the kit and why (containers, coins, items already issued), for the admin's reply.
        ///
        /// The character side: the six attributes, the three vitals, every skill, every replaced PropertyInt it
        /// holds, its spellbook, and its BUFF SET - the beneficial, non-item, non-cooldown, non-vitae enchantment
        /// rows, normalized to full duration (StartTime 0), because a snapshot is taken at some arbitrary point of
        /// each buff's countdown and every match should start with the same build.
        ///
        /// The kit: every wielded item at its wielded location, and every other non-container, non-coin
        /// possession as a pack item. Side packs themselves are left out; their contents are flattened into the
        /// kit, and are issued into the main pack.
        /// </summary>
        public static PvpTemplateDefinition CaptureDefinition(string key, string displayName, Biota character,
            IEnumerable<Biota> wieldedItems, IEnumerable<Biota> inventoryItems, out List<string> skipped)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));

            skipped = new List<string>();

            var definition = new PvpTemplateDefinition
            {
                Key = key,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName,
            };

            foreach (var attribute in FacetAttributes.PrimaryAttributes)
            {
                if (character.PropertiesAttribute == null || !character.PropertiesAttribute.TryGetValue(attribute, out var row))
                    continue;

                definition.Attributes[(int)attribute] = new PvpTemplateAttributeEntry { InitLevel = row.InitLevel, Ranks = row.LevelFromCP, CpSpent = row.CPSpent };
            }

            foreach (var vital in PvpTemplateOverlay.MaxVitals)
            {
                if (character.PropertiesAttribute2nd == null || !character.PropertiesAttribute2nd.TryGetValue(vital, out var row))
                    continue;

                definition.Vitals[(int)vital] = new PvpTemplateVitalEntry { InitLevel = row.InitLevel, Ranks = row.LevelFromCP, CpSpent = row.CPSpent };
            }

            if (character.PropertiesSkill != null)
            {
                foreach (var kvp in character.PropertiesSkill.OrderBy(k => (int)k.Key))
                {
                    definition.Skills.Add(new FacetSkillEntry
                    {
                        Skill = kvp.Key,
                        Sac = kvp.Value.SAC,
                        Ranks = kvp.Value.LevelFromPP,
                        Pp = kvp.Value.PP,
                        InitLevel = kvp.Value.InitLevel,
                    });
                }
            }

            if (character.PropertiesInt != null)
            {
                foreach (var property in PvpTemplatePowerProperties.ReplacedInts)
                {
                    if (character.PropertiesInt.TryGetValue(property, out var value))
                        definition.PowerInts[(int)property] = value;
                }
            }

            if (character.PropertiesSpellBook != null)
                definition.Spells.AddRange(character.PropertiesSpellBook.Keys.OrderBy(s => s));

            if (character.PropertiesEnchantmentRegistry != null)
            {
                foreach (var entry in character.PropertiesEnchantmentRegistry)
                {
                    if (PvpTemplateOverlay.IsItemGranted(entry) || PvpTemplateOverlay.IsCooldown(entry) || PvpTemplateOverlay.IsVitae(entry))
                        continue;

                    if (!entry.StatModType.HasFlag(EnchantmentTypeFlags.Beneficial))
                        continue;

                    var buff = PvpTemplateEnchantment.From(entry);
                    buff.StartTime = 0;
                    buff.LastTimeDegraded = 0;
                    buff.CasterObjectId = 0;

                    definition.Buffs.Add(buff);
                }
            }

            foreach (var item in wieldedItems ?? Enumerable.Empty<Biota>())
            {
                if (TrySkip(item, out var reason))
                {
                    skipped.Add(reason);
                    continue;
                }

                int? location = null;

                if (item.PropertiesInt != null && item.PropertiesInt.TryGetValue(PropertyInt.CurrentWieldedLocation, out var loc) && loc != 0)
                    location = loc;

                definition.Kit.Add(CaptureItem(item, location));
            }

            foreach (var item in inventoryItems ?? Enumerable.Empty<Biota>())
            {
                if (TrySkip(item, out var reason))
                {
                    skipped.Add(reason);
                    continue;
                }

                definition.Kit.Add(CaptureItem(item, null));
            }

            return definition;
        }

        private static bool TrySkip(Biota item, out string reason)
        {
            reason = null;

            if (item == null)
            {
                reason = "a null possession";
                return true;
            }

            var name = item.GetName() ?? $"wcid {item.WeenieClassId}";

            if (item.WeenieType == WeenieType.Container)
            {
                reason = $"{name} (a container; its contents are kept)";
                return true;
            }

            if (item.WeenieType == WeenieType.Coin)
            {
                reason = $"{name} (currency)";
                return true;
            }

            if (item.PropertiesBool != null && item.PropertiesBool.TryGetValue(PropertyBool.PvpTemplateIssued, out var issued) && issued)
            {
                reason = $"{name} (already an issued template item)";
                return true;
            }

            return false;
        }

        /// <summary>Copies one possession's mutable tables into a kit item, minus identity and ownership.</summary>
        public static PvpTemplateKitItem CaptureItem(Biota item, int? wieldLocation)
        {
            var kit = new PvpTemplateKitItem
            {
                WeenieClassId = item.WeenieClassId,
                WeenieType = (int)item.WeenieType,
                WieldLocation = wieldLocation,
            };

            if (item.PropertiesInt != null)
            {
                foreach (var kvp in item.PropertiesInt)
                {
                    if (!DroppedItemInts.Contains(kvp.Key))
                        kit.Ints[(int)kvp.Key] = kvp.Value;
                }
            }

            if (item.PropertiesInt64 != null)
                foreach (var kvp in item.PropertiesInt64) kit.Int64s[(int)kvp.Key] = kvp.Value;

            if (item.PropertiesBool != null)
                foreach (var kvp in item.PropertiesBool) kit.Bools[(int)kvp.Key] = kvp.Value;

            if (item.PropertiesFloat != null)
                foreach (var kvp in item.PropertiesFloat) kit.Floats[(int)kvp.Key] = kvp.Value;

            if (item.PropertiesString != null)
                foreach (var kvp in item.PropertiesString) kit.Strings[(int)kvp.Key] = kvp.Value;

            if (item.PropertiesDID != null)
                foreach (var kvp in item.PropertiesDID) kit.DataIds[(int)kvp.Key] = kvp.Value;

            if (item.PropertiesSpellBook != null)
                foreach (var kvp in item.PropertiesSpellBook) kit.SpellBook[kvp.Key] = kvp.Value;

            if (item.PropertiesAnimPart != null)
                foreach (var p in item.PropertiesAnimPart) kit.AnimParts.Add(new PvpTemplateAnimPart { Index = p.Index, AnimationId = p.AnimationId });

            if (item.PropertiesPalette != null)
                foreach (var p in item.PropertiesPalette) kit.Palettes.Add(new PvpTemplatePalette { SubPaletteId = p.SubPaletteId, Offset = p.Offset, Length = p.Length });

            if (item.PropertiesTextureMap != null)
                foreach (var t in item.PropertiesTextureMap) kit.TextureMaps.Add(new PvpTemplateTextureMap { PartIndex = t.PartIndex, OldTexture = t.OldTexture, NewTexture = t.NewTexture });

            if (item.PropertiesEnchantmentRegistry != null)
            {
                foreach (var entry in item.PropertiesEnchantmentRegistry)
                {
                    var e = PvpTemplateEnchantment.From(entry);

                    if (e.Duration > 0)
                    {
                        e.StartTime = 0;
                        e.LastTimeDegraded = 0;
                    }

                    kit.Enchantments.Add(e);
                }
            }

            return kit;
        }

        // ======================================================================================
        // cloning
        // ======================================================================================

        /// <summary>
        /// Builds the biota for one issued copy of <paramref name="item"/>: a fresh guid, the captured tables laid
        /// over <paramref name="weenieBase"/> (the item's weenie converted to a biota, which supplies the emotes,
        /// create list and other weenie-level collections a kit item does not carry; null builds a bare biota,
        /// which is what the tests use), no instance ids and no positions, and the issued stamps:
        /// PvpTemplateIssued = true, Attuned, Bonded, Value 0. Item enchantments are re-owned by
        /// <paramref name="ownerGuid"/>, the player the kit is issued to.
        ///
        /// Every collection is a NEW object, so a weenie base whose collections are shared with the world cache is
        /// never mutated through the clone.
        /// </summary>
        public static Biota BuildIssuedBiota(PvpTemplateKitItem item, uint newGuid, uint ownerGuid, Biota weenieBase = null)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var biota = weenieBase ?? new Biota();

            biota.Id = newGuid;
            biota.WeenieClassId = item.WeenieClassId;
            biota.WeenieType = (WeenieType)item.WeenieType;

            biota.PropertiesInt = item.Ints.ToDictionary(k => (PropertyInt)k.Key, k => k.Value);
            biota.PropertiesInt64 = item.Int64s.ToDictionary(k => (PropertyInt64)k.Key, k => k.Value);
            biota.PropertiesBool = item.Bools.ToDictionary(k => (PropertyBool)k.Key, k => k.Value);
            biota.PropertiesFloat = item.Floats.ToDictionary(k => (PropertyFloat)k.Key, k => k.Value);
            biota.PropertiesString = item.Strings.ToDictionary(k => (PropertyString)k.Key, k => k.Value);
            biota.PropertiesDID = item.DataIds.ToDictionary(k => (PropertyDataId)k.Key, k => k.Value);
            biota.PropertiesIID = new Dictionary<PropertyInstanceId, uint>();
            biota.PropertiesPosition = null;
            biota.PropertiesSpellBook = item.SpellBook.Count > 0 ? new Dictionary<int, float>(item.SpellBook) : null;
            biota.PropertiesAnimPart = item.AnimParts.Select(p => new PropertiesAnimPart { Index = p.Index, AnimationId = p.AnimationId }).ToList();
            biota.PropertiesPalette = item.Palettes.Select(p => new PropertiesPalette { SubPaletteId = p.SubPaletteId, Offset = p.Offset, Length = p.Length }).ToList();
            biota.PropertiesTextureMap = item.TextureMaps.Select(t => new PropertiesTextureMap { PartIndex = t.PartIndex, OldTexture = t.OldTexture, NewTexture = t.NewTexture }).ToList();

            biota.PropertiesEnchantmentRegistry = item.Enchantments.Count == 0 ? null : item.Enchantments.Select(e =>
            {
                var entry = e.ToRegistry();
                entry.CasterObjectId = ownerGuid;
                return entry;
            }).ToList();

            biota.PropertiesBool[PropertyBool.PvpTemplateIssued] = true;
            biota.PropertiesInt[PropertyInt.Attuned] = (int)AttunedStatus.Attuned;
            biota.PropertiesInt[PropertyInt.Bonded] = (int)BondedStatus.Bonded;
            biota.PropertiesInt[PropertyInt.Value] = 0;

            // Issued ammunition and spell components weigh nothing: the loadout is not the player's own gear, and a full
            // quiver of arrows plus chorizite would otherwise burden them. Both figures are zeroed so every recompute
            // (Stackable load, SetStackSize, AdjustStack on merge / consumption) derives 0 from StackUnitEncumbrance.
            if (IsWeightlessIssuedType(biota.WeenieType))
            {
                biota.PropertiesInt[PropertyInt.EncumbranceVal] = 0;
                biota.PropertiesInt[PropertyInt.StackUnitEncumbrance] = 0;
            }

            return biota;
        }

        /// <summary>
        /// TRUE for the weenie types an issued kit hands out weightless: Ammunition (arrows, quarrels, atlatl darts) and
        /// SpellComponent (chorizite and the other components). Every other issued item keeps its captured burden.
        /// </summary>
        public static bool IsWeightlessIssuedType(WeenieType type) => type == WeenieType.Ammunition || type == WeenieType.SpellComponent;

        /// <summary>True when the biota carries the issued mark. The biota-level twin of PvpTemplate.IsIssued.</summary>
        public static bool IsIssuedBiota(Biota biota, ReaderWriterLockSlim rwLock = null)
        {
            if (biota?.PropertiesBool == null)
                return false;

            if (rwLock == null)
                return biota.PropertiesBool.TryGetValue(PropertyBool.PvpTemplateIssued, out var v) && v;

            return biota.GetProperty(PropertyBool.PvpTemplateIssued, rwLock) == true;
        }
    }
}
