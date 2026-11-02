using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// One summon item absorbed into a Duo Summon Gem (Custom Summons Task 7), stored as versioned JSON in
    /// PropertyString.DuoSummonSlotA / DuoSummonSlotB:
    ///
    ///   {"v":1,"src":&lt;guid of the absorbed item&gt;,"scribe":&lt;inscriber guid or null&gt;,"item":{...}}
    ///
    /// The body is the item's mutable property tables in the PvP template kit-item shape (PvpTemplateKit.CaptureItem
    /// is the prior art), so a tinkered, dagger-rated, inscribed or partly spent item comes back exactly as it was.
    /// Unlike the PvP kit, NOTHING economic is dropped or stamped: Attuned, Bonded and Value survive the round trip,
    /// and the restore never writes PvpTemplateIssued. Only placement (CurrentWieldedLocation, PlacementPosition) is
    /// left out, because the restored item is a fresh object in a pack.
    ///
    /// Runtime ACE.Entity.Models.Biota only. Nothing here throws on bad input: a reader refuses (false) on a
    /// schema version it does not know, malformed JSON or a missing body, because a misread snapshot would
    /// restore the wrong item.
    /// </summary>
    public sealed class DuoGemSnapshot
    {
        public const int SchemaVersion = 1;

        /// <summary>A PropertyString is a TEXT column (64 KB); a snapshot above this is refused at capture.</summary>
        public const int MaxBytes = 60 * 1024;

        [JsonPropertyName("v")] public int Version { get; set; }

        /// <summary>The guid the absorbed item had. Used to refuse the same item twice.</summary>
        [JsonPropertyName("src")] public uint SourceGuid { get; set; }

        /// <summary>PropertyInstanceId.Scribe of the absorbed item (who inscribed it), when it had one.</summary>
        [JsonPropertyName("scribe")] public uint? Scribe { get; set; }

        [JsonPropertyName("item")] public PvpTemplateKitItem Item { get; set; }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        /// <summary>Placement only: the restored item is a new object with its own place in a pack.</summary>
        private static readonly HashSet<PropertyInt> DroppedInts = new HashSet<PropertyInt>
        {
            PropertyInt.CurrentWieldedLocation,
            PropertyInt.PlacementPosition,
        };

        /// <summary>Captures every mutable table of <paramref name="item"/>, under its biota read lock.</summary>
        public static DuoGemSnapshot Capture(WorldObject item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            item.BiotaDatabaseLock.EnterReadLock();
            try
            {
                return Capture(item.Biota);
            }
            finally
            {
                item.BiotaDatabaseLock.ExitReadLock();
            }
        }

        /// <summary>Captures every mutable table of a runtime biota. The caller holds whatever lock it needs.</summary>
        public static DuoGemSnapshot Capture(Biota biota)
        {
            if (biota == null) throw new ArgumentNullException(nameof(biota));

            var kit = new PvpTemplateKitItem
            {
                WeenieClassId = biota.WeenieClassId,
                WeenieType = (int)biota.WeenieType,
                WieldLocation = null,
            };

            if (biota.PropertiesInt != null)
                foreach (var kvp in biota.PropertiesInt)
                    if (!DroppedInts.Contains(kvp.Key))
                        kit.Ints[(int)kvp.Key] = kvp.Value;

            if (biota.PropertiesInt64 != null)
                foreach (var kvp in biota.PropertiesInt64) kit.Int64s[(int)kvp.Key] = kvp.Value;

            if (biota.PropertiesBool != null)
                foreach (var kvp in biota.PropertiesBool) kit.Bools[(int)kvp.Key] = kvp.Value;

            if (biota.PropertiesFloat != null)
                foreach (var kvp in biota.PropertiesFloat) kit.Floats[(int)kvp.Key] = kvp.Value;

            if (biota.PropertiesString != null)
                foreach (var kvp in biota.PropertiesString) kit.Strings[(int)kvp.Key] = kvp.Value;

            if (biota.PropertiesDID != null)
                foreach (var kvp in biota.PropertiesDID) kit.DataIds[(int)kvp.Key] = kvp.Value;

            if (biota.PropertiesSpellBook != null)
                foreach (var kvp in biota.PropertiesSpellBook) kit.SpellBook[kvp.Key] = kvp.Value;

            if (biota.PropertiesAnimPart != null)
                foreach (var p in biota.PropertiesAnimPart) kit.AnimParts.Add(new PvpTemplateAnimPart { Index = p.Index, AnimationId = p.AnimationId });

            if (biota.PropertiesPalette != null)
                foreach (var p in biota.PropertiesPalette) kit.Palettes.Add(new PvpTemplatePalette { SubPaletteId = p.SubPaletteId, Offset = p.Offset, Length = p.Length });

            if (biota.PropertiesTextureMap != null)
                foreach (var t in biota.PropertiesTextureMap) kit.TextureMaps.Add(new PvpTemplateTextureMap { PartIndex = t.PartIndex, OldTexture = t.OldTexture, NewTexture = t.NewTexture });

            // item enchantments kept exactly as they were (no PvP full-duration normalization)
            if (biota.PropertiesEnchantmentRegistry != null)
                foreach (var entry in biota.PropertiesEnchantmentRegistry) kit.Enchantments.Add(PvpTemplateEnchantment.From(entry));

            uint? scribe = null;
            if (biota.PropertiesIID != null && biota.PropertiesIID.TryGetValue(PropertyInstanceId.Scribe, out var s))
                scribe = s;

            return new DuoGemSnapshot { Version = SchemaVersion, SourceGuid = biota.Id, Scribe = scribe, Item = kit };
        }

        /// <summary>The JSON for a slot, or false (json null) when it would exceed <see cref="MaxBytes"/>.</summary>
        public bool TrySerialize(out string json)
        {
            var text = JsonSerializer.Serialize(this, Options);

            if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            {
                json = null;
                return false;
            }

            json = text;
            return true;
        }

        /// <summary>False, with a reason, for null/blank, malformed JSON, an unknown version or no body. Never throws.</summary>
        public static bool TryRead(string json, out DuoGemSnapshot snapshot, out string error)
        {
            snapshot = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the slot is empty";
                return false;
            }

            DuoGemSnapshot read;

            try
            {
                read = JsonSerializer.Deserialize<DuoGemSnapshot>(json, Options);
            }
            catch (Exception ex)
            {
                error = $"the slot is not valid JSON ({ex.Message})";
                return false;
            }

            if (read == null || read.Item == null)
            {
                error = "the slot has no item";
                return false;
            }

            if (read.Version != SchemaVersion)
            {
                error = $"the slot has schema version {read.Version}, this server reads {SchemaVersion}";
                return false;
            }

            var item = read.Item;
            item.Ints ??= new();
            item.Int64s ??= new();
            item.Bools ??= new();
            item.Floats ??= new();
            item.Strings ??= new();
            item.DataIds ??= new();
            item.SpellBook ??= new();
            item.AnimParts ??= new();
            item.Palettes ??= new();
            item.TextureMaps ??= new();
            item.Enchantments ??= new();
            item.Enchantments.RemoveAll(e => e == null);

            snapshot = read;
            return true;
        }

        /// <summary>
        /// Builds the runtime biota of the restored item: <paramref name="newGuid"/>, the captured tables laid over
        /// <paramref name="weenieBase"/> (the item's weenie converted to a biota, which supplies the emotes, create
        /// list and other weenie-level collections a snapshot does not carry; null builds a bare biota), no positions,
        /// no instance ids other than the inscriber. An item enchantment the absorbed item cast on itself is re-owned
        /// by the new guid. NO PvP issued stamps of any kind.
        ///
        /// Every collection is a NEW object, so a weenie base whose collections are shared with the world cache is
        /// never mutated through the restore.
        /// </summary>
        public Biota BuildRestoredBiota(uint newGuid, Biota weenieBase = null)
        {
            var biota = weenieBase ?? new Biota();
            var item = Item;

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
            if (Scribe.HasValue)
                biota.PropertiesIID[PropertyInstanceId.Scribe] = Scribe.Value;

            biota.PropertiesPosition = null;
            biota.PropertiesSpellBook = item.SpellBook.Count > 0 ? new Dictionary<int, float>(item.SpellBook) : null;
            biota.PropertiesAnimPart = item.AnimParts.Select(p => new PropertiesAnimPart { Index = p.Index, AnimationId = p.AnimationId }).ToList();
            biota.PropertiesPalette = item.Palettes.Select(p => new PropertiesPalette { SubPaletteId = p.SubPaletteId, Offset = p.Offset, Length = p.Length }).ToList();
            biota.PropertiesTextureMap = item.TextureMaps.Select(t => new PropertiesTextureMap { PartIndex = t.PartIndex, OldTexture = t.OldTexture, NewTexture = t.NewTexture }).ToList();

            biota.PropertiesEnchantmentRegistry = item.Enchantments.Count == 0 ? null : item.Enchantments.Select(e =>
            {
                var entry = e.ToRegistry();
                if (entry.CasterObjectId == SourceGuid)
                    entry.CasterObjectId = newGuid;
                return entry;
            }).ToList();

            return biota;
        }

        // ---- reads the gem needs, straight off the captured tables -------------------------------------------

        public string Name => Item.Strings.TryGetValue((int)PropertyString.Name, out var n) ? n : $"wcid {Item.WeenieClassId}";

        public int? Int(PropertyInt key) => Item.Ints.TryGetValue((int)key, out var v) ? v : (int?)null;

        public uint? DataId(PropertyDataId key) => Item.DataIds.TryGetValue((int)key, out var v) ? v : (uint?)null;

        /// <summary>The pet this absorbed item summons, exactly as PetSummonSource.FromDevice would describe it.</summary>
        public PetSummonSource ToSource()
        {
            return new PetSummonSource(
                (uint)(Int(PropertyInt.PetClass) ?? 0),
                Int(PropertyInt.GearDamage),
                Int(PropertyInt.GearDamageResist),
                Int(PropertyInt.GearCrit),
                Int(PropertyInt.GearCritResist),
                Int(PropertyInt.GearCritDamage),
                Int(PropertyInt.GearCritDamageResist),
                DataId(PropertyDataId.SummonBoundSpell1),
                DataId(PropertyDataId.SummonBoundSpell2));
        }
    }
}
