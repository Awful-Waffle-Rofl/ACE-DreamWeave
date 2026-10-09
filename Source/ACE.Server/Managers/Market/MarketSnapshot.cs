using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Builds the searchable projection stored in market_listing.snapshot_Json (DESIGN 5.1), captured
    /// at listing time and refreshed by the snapshot backfill - automatically after a world start,
    /// or on demand via /marketbackfill run - which touches ACTIVE listings only, so a closed listing
    /// keeps exactly what it captured while an Active one may have been re-projected since. Reads spells via Biota.GetKnownSpellsIds, which takes the object's own BiotaDatabaseLock (NoRecursion) - never call this while already holding it.
    /// </summary>
    public static class MarketSnapshot
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The ClothingTable oracle IconKey.Resolve needs; one shared instance because it caches per
        /// instance. Settable so a test can substitute a fake; null is legal and means no ClothingBase
        /// override is possible, which is what a test with no dats gets.
        /// </summary>
        public static ACE.DatLoader.IClothingIconSource ClothingIcons { get; set; }
            = new ACE.DatLoader.DatClothingIconSource();

        /// <summary>
        /// The version stamped into every snapshot this code builds, and the whole basis on which a
        /// stored snapshot is judged stale: a listing whose stored
        /// <see cref="ListingSnapshot.SnapshotVersion"/> is BELOW this is out of date by definition.
        ///
        /// BUMP THIS WHENEVER THE PROJECTION CHANGES MEANING - a field added, a field's value
        /// computed differently, a token set extended. The server then repairs legacy listings on
        /// the next world start, and nothing else has to be remembered.
        ///
        /// FORGETTING TO BUMP IT IS THE ONE FAILURE THIS SCHEME CANNOT DETECT. Nothing derives the
        /// version from the shape of the code, so a projection change shipped without a bump leaves
        /// every stored snapshot claiming to be current and version-gated selection will skip all of
        /// them, silently and forever. The escape hatch for exactly that case is the full-sweep arm,
        /// /marketbackfill run all, which re-projects regardless of version.
        ///
        /// 1 (2026-09-06) - the first versioned projection. Everything stored before this
        /// deserializes to 0, which is correct: those documents predate slots, equipment_set and
        /// weapon_class.
        ///
        /// 2 (2026-09-06) - Wanted orders added structure, max_structure and salvage_tool_charges.
        /// Bumped rather than left at 1 because those three are what lets a consumer tell a FULL
        /// salvage bag from a partial one, which is the entire matching rule the Wanted board runs
        /// on: a listing left claiming version 1 without them would show and match wrong forever,
        /// with nothing in a log to say why. Version 1 therefore names two shapes on disk - one
        /// written before this branch and one after - and this is the bump that lets the automatic
        /// sweep repair the first without an operator having to know it exists.
        ///
        /// NOT stamped on a placeholder. FromItem(null), FromWeenie(null) and MarketManager's
        /// FallbackSnapshot all return a document that describes nothing, and leaving those at 0
        /// keeps them selected so a later pass can replace them with a real projection once the
        /// oracle behind them works again. The version means "a real projection of this version
        /// built this", never "some code ran".
        ///
        /// 3 (2026-09-12) - equipment_set_name now carries the retail dat name (MarketEquipmentSet.
        /// Label consults EquipmentSetNames first) instead of the spaced enum token for every id the
        /// catalog covers. Bumped so /mule search's and the web market's set-name matching sees the
        /// retail spelling on every listing once the automatic sweep repairs stored snapshots.
        ///
        /// 4 (2026-09-14) - mods, mod_capacity and weapon_quality_tier added (MarketItemMods), so the web
        /// market and the character sheet can show an item's weapon and equipment mods and filter on them.
        /// Bumped so the automatic sweep re-projects every Active listing and current listings gain their mods
        /// on the first world start after the deploy. A CLOSED listing keeps null in all three forever.
        /// </summary>
        public const int CurrentVersion = 4;

        /// <summary>snake_case per Docs/Market/market-api-v1.yaml; the web app reads snapshot_json verbatim, so this policy is contract.</summary>
        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        public static string Serialize(ListingSnapshot snapshot)
            => JsonSerializer.Serialize(snapshot ?? new ListingSnapshot(), JsonOptions);

        /// <summary>Never throws and never returns null: an unparseable snapshot becomes an empty one rather than failing the whole index rebuild.</summary>
        public static ListingSnapshot Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ListingSnapshot();

            try
            {
                return JsonSerializer.Deserialize<ListingSnapshot>(json, JsonOptions) ?? new ListingSnapshot();
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] could not parse a listing snapshot; substituting an empty one: {ex.GetFullMessage()}");
                return new ListingSnapshot();
            }
        }

        /// <summary>Projects a live stored item. Must be called OUTSIDE the item's BiotaDatabaseLock.</summary>
        public static ListingSnapshot FromItem(WorldObject item)
        {
            if (item == null)
                return new ListingSnapshot();

            // GetProperty, never WorldObject.Workmanship: that getter WRITES (it rewrites
            // ItemWorkmanship in place), and this runs for every row of a vault view.
            var numItemsInMaterial = item.GetProperty(PropertyInt.NumItemsInMaterial);
            var procSpell = item.ProcSpell;

            var snapshot = new ListingSnapshot
            {
                // Stamped HERE and not in the null guard above: this is the branch that actually
                // projects something.
                SnapshotVersion = CurrentVersion,
                Name = item.Name ?? string.Empty,
                Wcid = item.WeenieClassId,
                ItemType = (int)item.ItemType,
                ItemTypeName = item.ItemType.ToString(),
                MaterialType = item.MaterialType.HasValue ? (int?)item.MaterialType.Value : null,
                MaterialName = item.MaterialType.HasValue ? item.MaterialType.Value.ToString() : null,
                Workmanship = MarketAppraisal.PerUnitWorkmanship(
                    item.GetProperty(PropertyInt.ItemWorkmanship),
                    numItemsInMaterial,
                    item.GetProperty(PropertyInt.Structure)),
                NumItemsInMaterial = numItemsInMaterial,
                UiEffects = item.GetProperty(PropertyInt.UiEffects),
                // Never gated on IsWeapon: a Caster reaches the web through no other channel.
                DamageType = item.GetProperty(PropertyInt.DamageType),
                DamageMod = item.GetProperty(PropertyFloat.DamageMod),
                ElementalDamageBonus = item.GetProperty(PropertyInt.ElementalDamageBonus),
                ElementalDamageMod = item.GetProperty(PropertyFloat.ElementalDamageMod),
                ProcSpellId = procSpell.HasValue ? (int?)unchecked((int)procSpell.Value) : null,
                ProcSpellName = procSpell.HasValue ? SpellName(unchecked((int)procSpell.Value)) : null,
                WieldSkillType = item.WieldSkillType,
                WieldDifficulty = item.WieldDifficulty,
                ArmorLevel = item.ArmorLevel,
                DamageHigh = item.Damage,
                DamageLow = item.Damage.HasValue && item.DamageVariance.HasValue
                    ? (int?)Math.Round(item.Damage.Value * (1.0 - item.DamageVariance.Value))
                    : null,
                Value = item.Value,
                EncumbranceVal = item.EncumbranceVal,
                MaxStackSize = item.MaxStackSize,

                // The units in THIS biota, which a stored stack listed whole at count 1 needs so a
                // client is not left dividing a whole-stack price by 1. FromWeenie and
                // FromLedgerProbe deliberately leave it null: a ledger row's units are the listing's
                // own count, not a stack size.
                StackSize = item.StackSize,

                // GetProperty on all three, per the rule at the top of this method. Structure and
                // MaxStructure let the web tell a full bag from a partial one; SalvageToolCharges lets
                // it exclude a Hammer, which is otherwise indistinguishable from a full bag in this projection.
                Structure = item.GetProperty(PropertyInt.Structure),
                MaxStructure = item.GetProperty(PropertyInt.MaxStructure),
                SalvageToolCharges = item.GetProperty(PropertyInt.SalvageToolCharges),
                LongDesc = MarketAppraisal.Description(item),
                // Overlay and underlay are never palette resolved, so they carry straight through.
                IconOverlayId = item.IconOverlayId,
                IconUnderlayId = item.IconUnderlayId,
            };

            snapshot.WeaponClass = MarketWeaponClass.Classify(
                item.WeenieType,
                item.GetProperty(PropertyInt.WeaponSkill),
                item.GetProperty(PropertyInt.AmmoType),
                item.GetProperty(PropertyInt.WeaponType));
            snapshot.WeaponClassName = MarketWeaponClass.Label(snapshot.WeaponClass);

            snapshot.Slots = MarketEquipSlots.Classify(item.GetProperty(PropertyInt.ValidLocations));
            snapshot.EquipmentSet = MarketEquipmentSet.Classify(item.GetProperty(PropertyInt.EquipmentSetId));
            snapshot.EquipmentSetName = MarketEquipmentSet.Label(item.GetProperty(PropertyInt.EquipmentSetId));

            var iconKeys = ResolveIcons(item);

            // Null ResolvedPaletteTemplate means no override applied; never substitute the item's own
            // PaletteTemplate here - that is the defect this block exists to prevent.
            snapshot.IconId = iconKeys.ResolvedIconId ?? item.IconId;
            snapshot.PaletteTemplate = ToPaletteTemplate(iconKeys);

            // AppraiseInfo.BuildSpells (AppraiseInfo.cs:592-612) is the order the game shows: the
            // primary spell first, then the proc spell, then the spellbook with those two filtered
            // out. Neither of the first two lives in the spellbook, which is why a cloak's Surge and a
            // wand's primary spell were missing here entirely.
            var spellDid = item.SpellDID;
            var spellIds = new List<int>();

            if (spellDid.HasValue)
                spellIds.Add(unchecked((int)spellDid.Value));

            if (procSpell.HasValue)
                spellIds.Add(unchecked((int)procSpell.Value));

            try
            {
                if (item.Biota != null)
                    spellIds.AddRange(item.Biota.GetKnownSpellsIdsWhere(i => i != spellDid && i != procSpell, item.BiotaDatabaseLock)
                        ?? new List<int>());
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] could not read the spell book of 0x{item.Guid.Full:X8}; listing it with no spells: {ex.GetFullMessage()}");
            }

            snapshot.SpellIds = spellIds;
            snapshot.SpellNames = snapshot.SpellIds.Select(SpellName).ToList();
            snapshot.PanelLines = MarketAppraisal.PanelLines(item);

            // Mods, capacity and quality tier (projection version 4). Null - never empty - when there is
            // nothing to show, which FromWeenie's all-null mirror depends on.
            MarketItemMods.Apply(item, snapshot);

            return snapshot;
        }

        /// <summary>
        /// The same projection from a weenie, for a collapsed ledger row that has no WorldObject.
        /// Mirrors FromItem field for field, exactly as ResolveIcons mirrors its WorldObject overload.
        /// </summary>
        public static ListingSnapshot FromWeenie(Weenie weenie)
        {
            if (weenie == null)
                return new ListingSnapshot();

            var itemType = weenie.GetProperty(PropertyInt.ItemType) ?? 0;
            var material = weenie.GetProperty(PropertyInt.MaterialType);
            var damage = weenie.GetProperty(PropertyInt.Damage);
            var variance = weenie.GetProperty(PropertyFloat.DamageVariance);
            var numItemsInMaterial = weenie.GetProperty(PropertyInt.NumItemsInMaterial);
            var spellDid = weenie.GetProperty(PropertyDataId.Spell);
            var procSpell = weenie.GetProperty(PropertyDataId.ProcSpell);

            var snapshot = new ListingSnapshot
            {
                // The same stamp FromItem writes, from the same constant. If these two ever carry
                // different versions the gate is meaningless: a ledger listing and a stored listing
                // would age at different rates.
                SnapshotVersion = CurrentVersion,
                Name = weenie.GetName() ?? string.Empty,
                Wcid = weenie.WeenieClassId,
                ItemType = itemType,
                ItemTypeName = ((ACE.Entity.Enum.ItemType)itemType).ToString(),
                MaterialType = material,
                MaterialName = material.HasValue ? ((ACE.Entity.Enum.MaterialType)material.Value).ToString() : null,
                Workmanship = MarketAppraisal.PerUnitWorkmanship(
                    weenie.GetProperty(PropertyInt.ItemWorkmanship),
                    numItemsInMaterial,
                    weenie.GetProperty(PropertyInt.Structure)),
                NumItemsInMaterial = numItemsInMaterial,
                UiEffects = weenie.GetProperty(PropertyInt.UiEffects),
                // Never gated on IsWeapon: a Caster reaches the web through no other channel.
                DamageType = weenie.GetProperty(PropertyInt.DamageType),
                DamageMod = weenie.GetProperty(PropertyFloat.DamageMod),
                ElementalDamageBonus = weenie.GetProperty(PropertyInt.ElementalDamageBonus),
                ElementalDamageMod = weenie.GetProperty(PropertyFloat.ElementalDamageMod),
                ProcSpellId = procSpell.HasValue ? (int?)unchecked((int)procSpell.Value) : null,
                ProcSpellName = procSpell.HasValue ? SpellName(unchecked((int)procSpell.Value)) : null,
                WieldSkillType = weenie.GetProperty(PropertyInt.WieldSkillType),
                WieldDifficulty = weenie.GetProperty(PropertyInt.WieldDifficulty),
                ArmorLevel = weenie.GetProperty(PropertyInt.ArmorLevel),
                DamageHigh = damage,
                DamageLow = damage.HasValue && variance.HasValue
                    ? (int?)Math.Round(damage.Value * (1.0 - variance.Value))
                    : null,
                Value = weenie.GetProperty(PropertyInt.Value),
                EncumbranceVal = weenie.GetProperty(PropertyInt.EncumbranceVal),
                MaxStackSize = weenie.GetProperty(PropertyInt.MaxStackSize),
                Structure = weenie.GetProperty(PropertyInt.Structure),
                MaxStructure = weenie.GetProperty(PropertyInt.MaxStructure),
                SalvageToolCharges = weenie.GetProperty(PropertyInt.SalvageToolCharges),
                LongDesc = MarketAppraisal.Description(weenie),
                // Overlay and underlay are never palette resolved, so they carry straight through.
                IconOverlayId = weenie.GetProperty(PropertyDataId.IconOverlay),
                IconUnderlayId = weenie.GetProperty(PropertyDataId.IconUnderlay),
            };

            snapshot.WeaponClass = MarketWeaponClass.Classify(
                weenie.WeenieType,
                weenie.GetProperty(PropertyInt.WeaponSkill),
                weenie.GetProperty(PropertyInt.AmmoType),
                weenie.GetProperty(PropertyInt.WeaponType));
            snapshot.WeaponClassName = MarketWeaponClass.Label(snapshot.WeaponClass);

            snapshot.Slots = MarketEquipSlots.Classify(weenie.GetProperty(PropertyInt.ValidLocations));
            snapshot.EquipmentSet = MarketEquipmentSet.Classify(weenie.GetProperty(PropertyInt.EquipmentSetId));
            snapshot.EquipmentSetName = MarketEquipmentSet.Label(weenie.GetProperty(PropertyInt.EquipmentSetId));

            var iconKeys = ResolveIcons(weenie);

            snapshot.IconId = iconKeys?.ResolvedIconId ?? weenie.GetProperty(PropertyDataId.Icon) ?? 0;
            snapshot.PaletteTemplate = ToPaletteTemplate(iconKeys);

            // The same order FromItem uses, from AppraiseInfo.BuildSpells: primary, proc, then the
            // spellbook with those two filtered out.
            var spellIds = new List<int>();

            if (spellDid.HasValue)
                spellIds.Add(unchecked((int)spellDid.Value));

            if (procSpell.HasValue)
                spellIds.Add(unchecked((int)procSpell.Value));

            if (weenie.PropertiesSpellBook != null)
                spellIds.AddRange(weenie.PropertiesSpellBook.Keys.Where(i => i != spellDid && i != procSpell));

            snapshot.SpellIds = spellIds;
            snapshot.SpellNames = snapshot.SpellIds.Select(SpellName).ToList();
            snapshot.PanelLines = MarketAppraisal.PanelLines(weenie);

            // Mods, ModCapacity and WeaponQualityTier stay null here, deliberately: a ledger row is a pristine
            // item from its weenie, the fork's mods are applied to item INSTANCES by salvage, and no weenie
            // authors a mod record. FromItem emits null for a mod-less item, so the two projections mirror.

            return snapshot;
        }

        /// <summary>
        /// Projects a collapsed ledger stack, which has no biota behind it. The caller passes a freshly
        /// instantiated probe of the wcid, as AccountVaultStore's ledger withdraw does.
        /// </summary>
        public static ListingSnapshot FromLedgerProbe(WorldObject probe, uint wcid)
        {
            var snapshot = FromItem(probe);
            snapshot.Wcid = wcid;

            // Cleared, not carried through. The probe is a fresh instance of the wcid and its own
            // StackSize describes the probe, never the ledger row: a ledger listing's units ARE its
            // count, so a stack size here would be a second, wrong denominator for the same price.
            snapshot.StackSize = null;

            return snapshot;
        }

        /// <summary>
        /// THE ICON KEYS MUST BE RESOLVED, NOT COPIED: a ClothingBase item's drawn icon is often not
        /// its own Icon property, and the pack's file names key on the resolved pair. IconKey.Resolve
        /// is the one rule shared with the pack exporter and the web app so the three cannot drift.
        /// </summary>
        public static ACE.DatLoader.IconKeySet ResolveIcons(WorldObject item)
        {
            if (item == null)
                return null;

            return ACE.DatLoader.IconKey.Resolve(
                item.IconId,
                item.IconOverlayId,
                item.IconUnderlayId,
                item.PaletteTemplate,
                item.ClothingBase,
                ClothingIcons,
                item.SetupTableId,
                item.Shade.HasValue,
                item.IgnoreCloIcons ?? false);
        }

        /// <summary>The same rule from a weenie, for a collapsed ledger row that has no WorldObject.</summary>
        public static ACE.DatLoader.IconKeySet ResolveIcons(Weenie weenie)
        {
            if (weenie == null)
                return null;

            return ACE.DatLoader.IconKey.Resolve(
                weenie.GetProperty(PropertyDataId.Icon),
                weenie.GetProperty(PropertyDataId.IconOverlay),
                weenie.GetProperty(PropertyDataId.IconUnderlay),
                weenie.GetProperty(PropertyInt.PaletteTemplate),
                weenie.GetProperty(PropertyDataId.ClothingBase),
                ClothingIcons,
                weenie.GetProperty(PropertyDataId.Setup) ?? 0,
                weenie.GetProperty(PropertyFloat.Shade).HasValue,
                weenie.GetProperty(PropertyBool.IgnoreCloIcons) ?? false);
        }

        /// <summary>Null means no ClothingBase override applied; never substitute the item's own PaletteTemplate.</summary>
        public static int? ToPaletteTemplate(ACE.DatLoader.IconKeySet keys)
            => keys != null && keys.ResolvedPaletteTemplate.HasValue
                ? (int?)unchecked((int)keys.ResolvedPaletteTemplate.Value)
                : null;

        /// <summary>
        /// Made internal (was private) so <see cref="ACE.Server.Managers.Market.ItemTextSearch"/> can
        /// reuse the SAME spell-name resolution rather than duplicating it - see that class's remarks.
        /// </summary>
        internal static string SpellName(int spellId)
        {
            try
            {
                var spell = new ACE.Server.Entity.Spell(spellId);
                return spell?.Name ?? $"Spell {spellId}";
            }
            catch (Exception)
            {
                return $"Spell {spellId}";
            }
        }
    }
}
