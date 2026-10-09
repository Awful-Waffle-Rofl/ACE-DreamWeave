using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market.Suit
{
    /// <summary>
    /// The single projection of an equippable item into <see cref="SuitItem"/>. Mirrors
    /// <see cref="MarketSnapshot"/>: a stored item (<see cref="FromItem"/>), a template weenie
    /// (<see cref="FromWeenie"/>) and a collapsed ledger/class row (<see cref="FromLedgerProbe"/>,
    /// <see cref="FromWeenie"/>) all run ONE body over a <see cref="Props"/> reader, so the three
    /// cannot drift field by field.
    ///
    /// PURE. Every read goes through GetProperty; nothing here may call a getter that writes. In
    /// particular NEVER read <c>WorldObject.Workmanship</c> (it rewrites ItemWorkmanship in place) and
    /// never <c>WorldObject.ItemLevel</c> (throws when ItemTotalXp is unset); the level is derived
    /// from the raw properties instead. Reads the spellbook via Biota.GetKnownSpellsIdsWhere, which
    /// takes the object's BiotaDatabaseLock - never call this while already holding it.
    /// </summary>
    public static class SuitItemProjector
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Every EquipMask bit a suit can use: wear layers, armor layers, neck, wrists, fingers, trinket, cloak.</summary>
        private const EquipMask SuitSlots =
            EquipMask.HeadWear | EquipMask.ChestWear | EquipMask.AbdomenWear | EquipMask.UpperArmWear | EquipMask.LowerArmWear
            | EquipMask.HandWear | EquipMask.UpperLegWear | EquipMask.LowerLegWear | EquipMask.FootWear
            | EquipMask.ChestArmor | EquipMask.AbdomenArmor | EquipMask.UpperArmArmor | EquipMask.LowerArmArmor
            | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor
            | EquipMask.NeckWear | EquipMask.WristWearLeft | EquipMask.WristWearRight
            | EquipMask.FingerWearLeft | EquipMask.FingerWearRight
            | EquipMask.TrinketOne | EquipMask.Cloak;

        /// <summary>
        /// True when the ValidLocations mask names at least one suit slot. Shields, weapons, ammo,
        /// aetheria and sigils carry only bits outside <see cref="SuitSlots"/> and so are false, as is a
        /// null or empty mask.
        /// </summary>
        public static bool IsSuitRelevant(int? validLocations)
            => validLocations.HasValue && (unchecked((EquipMask)validLocations.Value) & SuitSlots) != 0;

        public static bool IsSuitRelevant(WorldObject item)
            => item != null && IsSuitRelevant(item.GetProperty(PropertyInt.ValidLocations));

        public static bool IsSuitRelevant(Weenie weenie)
            => weenie != null && IsSuitRelevant(weenie.GetProperty(PropertyInt.ValidLocations));

        /// <summary>Projects a live item. <paramref name="count"/> is the units the line stands for; item_guid is the item's own guid.</summary>
        public static SuitItem FromItem(WorldObject item, string source = SuitItem.SourceVault, string classKey = null, int count = 1)
        {
            if (item == null)
                return null;

            var props = new Props
            {
                Int = item.GetProperty,
                Int64 = item.GetProperty,
                Did = item.GetProperty,
                Iid = item.GetProperty,
                Str = item.GetProperty,
                SpellBook = () => ReadSpellBook(item),
            };

            var icons = MarketSnapshot.ResolveIcons(item);

            return Build(props, item.WeenieClassId, item.Guid.Full, icons?.ResolvedIconId, source, classKey, count) with { Reductions = ReductionsOf(item) };
        }

        /// <summary>
        /// Every reduction tool that would succeed on the item AND change its slots or coverage priority, from Tailoring.ReductionResult (the in-game
        /// decision itself). Null when none would, so the JSON omits the key.
        /// </summary>
        private static IReadOnlyList<SuitReduction> ReductionsOf(WorldObject item)
        {
            List<SuitReduction> list = null;

            foreach (var (tool, name) in new[]
            {
                (Tailoring.ReductionTool.Main, "main"),
                (Tailoring.ReductionTool.Lower, "lower"),
                (Tailoring.ReductionTool.Middle, "middle"),
            })
            {
                var result = Tailoring.ReductionResult(item, tool);
                // A tool that would change neither the slots nor the coverage priority (main on a chest-only piece
                // that already carries the chest priority) is no choice for the optimizer, so it is not reported;
                // this is what keeps single-slot items key-free. A priority-only change IS reported: in game the
                // tool still rewrites ClothingPriority.
                if (result == null
                    || ((int)result.Value.validLocations == (item.GetProperty(PropertyInt.ValidLocations) ?? 0)
                        && (int)result.Value.clothingPriority == (item.GetProperty(PropertyInt.ClothingPriority) ?? 0)))
                    continue;

                list ??= new List<SuitReduction>(3);
                list.Add(new SuitReduction
                {
                    Tool = name,
                    ValidLocations = (int)result.Value.validLocations,
                    ClothingPriority = (int)result.Value.clothingPriority,
                });
            }

            return list;
        }

        /// <summary>Projects a template weenie, for a ledger/class row that has no WorldObject.</summary>
        public static SuitItem FromWeenie(Weenie weenie, string source = SuitItem.SourceVault, string classKey = null, int count = 1)
        {
            if (weenie == null)
                return null;

            var props = new Props
            {
                Int = weenie.GetProperty,
                Int64 = weenie.GetProperty,
                Did = weenie.GetProperty,
                Iid = weenie.GetProperty,
                Str = weenie.GetProperty,
                SpellBook = () => weenie.PropertiesSpellBook != null ? weenie.PropertiesSpellBook.Keys.ToList() : new List<int>(),
            };

            var icons = MarketSnapshot.ResolveIcons(weenie);

            return Build(props, weenie.WeenieClassId, null, icons?.ResolvedIconId, source, classKey, count);
        }

        /// <summary>
        /// Projects a collapsed ledger stack from a freshly instantiated probe of the wcid, as
        /// <see cref="MarketSnapshot.FromLedgerProbe"/> does: the probe's own guid is meaningless, so
        /// item_guid is cleared and the wcid and count come from the ledger row.
        /// </summary>
        public static SuitItem FromLedgerProbe(WorldObject probe, uint wcid, string classKey, int count, string source = SuitItem.SourceVault)
        {
            var projected = FromItem(probe, source, classKey, count);

            return projected == null ? null : projected with { ItemGuid = null, Wcid = wcid };
        }

        private static SuitItem Build(Props p, uint wcid, uint? guid, uint? resolvedIconId, string source, string classKey, int count)
        {
            var spellDid = p.Did(PropertyDataId.Spell);
            var procSpell = p.Did(PropertyDataId.ProcSpell);

            var wornSpells = new List<uint>();
            foreach (var id in p.SpellBook())
            {
                var spell = unchecked((uint)id);
                if (spell != spellDid && spell != procSpell)
                    wornSpells.Add(spell);
            }
            wornSpells.Sort();

            var setId = p.Int(PropertyInt.EquipmentSetId);

            return new SuitItem
            {
                Source = source,
                ItemGuid = guid,
                Wcid = wcid,
                ClassKey = classKey,
                Count = count,
                Listed = false,
                Name = p.Str(PropertyString.Name) ?? string.Empty,
                IconId = resolvedIconId ?? p.Did(PropertyDataId.Icon) ?? 0,
                IconOverlayId = p.Did(PropertyDataId.IconOverlay),
                IconUnderlayId = p.Did(PropertyDataId.IconUnderlay),
                IsClothing = p.Int(PropertyInt.ItemType) == (int)ItemType.Clothing,
                ValidLocations = p.Int(PropertyInt.ValidLocations) ?? 0,
                ClothingPriority = p.Int(PropertyInt.ClothingPriority) ?? 0,
                ArmorLevel = p.Int(PropertyInt.ArmorLevel),
                EquipmentSetId = setId,
                ItemLevel = ItemLevelOf(p),
                WornSpellIds = wornSpells.ToArray(),
                ItemCurMana = p.Int(PropertyInt.ItemCurMana),
                ItemMaxMana = p.Int(PropertyInt.ItemMaxMana),
                ItemDifficulty = p.Int(PropertyInt.ItemDifficulty),
                ItemSkillLimit = p.Did(PropertyDataId.ItemSkillLimit),
                ItemSkillLevelLimit = p.Int(PropertyInt.ItemSkillLevelLimit),
                WieldReqs = WieldReqsOf(p),
                HeritageSpecificArmor = p.Int(PropertyInt.HeritageSpecificArmor),
                AllowedWielder = p.Iid(PropertyInstanceId.AllowedWielder),
                Encumbrance = p.Int(PropertyInt.EncumbranceVal),
            };
        }

        /// <summary>
        /// All four slots, because a level requirement can sit in slot 2 when slot 1 is occupied
        /// (LootGenerationFactory_Clothing.SetWieldLevelReq) and Player_Inventory checks all four.
        /// </summary>
        private static IReadOnlyList<WieldReq> WieldReqsOf(Props p)
        {
            var slots = new[]
            {
                (PropertyInt.WieldRequirements,  PropertyInt.WieldSkillType,  PropertyInt.WieldDifficulty),
                (PropertyInt.WieldRequirements2, PropertyInt.WieldSkillType2, PropertyInt.WieldDifficulty2),
                (PropertyInt.WieldRequirements3, PropertyInt.WieldSkillType3, PropertyInt.WieldDifficulty3),
                (PropertyInt.WieldRequirements4, PropertyInt.WieldSkillType4, PropertyInt.WieldDifficulty4),
            };

            var reqs = new List<WieldReq>(4);

            foreach (var (kind, skill, difficulty) in slots)
            {
                var k = p.Int(kind) ?? 0;
                if (k == (int)WieldRequirement.Invalid)
                    continue;

                reqs.Add(new WieldReq { Kind = k, SkillOrAttr = p.Int(skill) ?? 0, Difficulty = p.Int(difficulty) ?? 0 });
            }

            return reqs;
        }

        /// <summary>
        /// WorldObject.ItemLevel re-derived from raw reads: the getter calls ItemTotalXp.Value, which
        /// throws for an item that has never gained XP, and ExperienceSystem.ItemTotalXPToLevel's
        /// FixedPlusBase arm underflows a ulong for XP below one base step. Below one base step the
        /// level is 0 under every style, so that case short-circuits.
        /// </summary>
        private static int? ItemLevelOf(Props p)
        {
            var baseXp = p.Int64(PropertyInt64.ItemBaseXp);
            var maxLevel = p.Int(PropertyInt.ItemMaxLevel);
            var style = p.Int(PropertyInt.ItemXpStyle);

            if (!(baseXp > 0) || !(maxLevel > 0) || !(style > 0))
                return null;

            var total = p.Int64(PropertyInt64.ItemTotalXp) ?? 0;

            if (total < baseXp.Value)
                return 0;

            return ExperienceSystem.ItemTotalXPToLevel((ulong)total, (ulong)baseXp.Value, maxLevel.Value, (ItemXpStyle)style.Value);
        }

        private static List<int> ReadSpellBook(WorldObject item)
        {
            try
            {
                if (item.Biota != null)
                    return item.Biota.GetKnownSpellsIdsWhere(i => true, item.BiotaDatabaseLock) ?? new List<int>();
            }
            catch (Exception ex)
            {
                log.Error($"[SUIT] could not read the spell book of 0x{item.Guid.Full:X8}; projecting it with no spells: {ex.GetFullMessage()}");
            }

            return new List<int>();
        }

        /// <summary>The property reads both projections share, so one body serves WorldObject and Weenie.</summary>
        private sealed class Props
        {
            public Func<PropertyInt, int?> Int;
            public Func<PropertyInt64, long?> Int64;
            public Func<PropertyDataId, uint?> Did;
            public Func<PropertyInstanceId, uint?> Iid;
            public Func<PropertyString, string> Str;
            public Func<List<int>> SpellBook;
        }
    }
}
