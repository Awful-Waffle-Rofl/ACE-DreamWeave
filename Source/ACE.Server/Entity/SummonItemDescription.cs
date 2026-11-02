using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>One absorbed summon of a Duo Gem: the type name shown to the player and the pet data it carries.</summary>
    public record SummonSlotInfo(string TypeName, PetSummonSource Source);

    /// <summary>
    /// The shared description for every custom summon item (Custom Summons Task 5): the blank and bound caster
    /// summons, the level-225 items, the Duo Gem, and the Sacrificial Dagger. It shows level, magic skill (casters
    /// only), bound spells or "unbound slot", element, and all six ratings. Written to PropertyString.LongDesc
    /// after every state change, so it replaces any static description the weenie shipped with.
    ///
    /// The level and the magic skill are NOT stored on the item: they are read from the pet weenie the item's
    /// PetClass points at, through <see cref="PetWeenieLookup"/>. The magic skill is the highest InitLevel across
    /// the five magic school rows, which is how a caster pet's skill is authored (400/500/600 for level 150/180/200).
    /// </summary>
    public static class SummonItemDescription
    {
        /// <summary>The world weenie cache. A hook so tests need no database.</summary>
        internal static Func<uint, Weenie> PetWeenieLookup = wcid => DatabaseManager.World.GetCachedWeenie(wcid);

        /// <summary>A spell id to its name. A hook so tests need no DAT.</summary>
        internal static Func<uint, string> SpellNameLookup = DefaultSpellName;

        /// <summary>
        /// The absorbed summons of a Duo Gem, in slot order: a read of its slot snapshots (DuoSummonGem.SlotInfos).
        /// A hook so the description tests can supply slots without building snapshots.
        /// </summary>
        internal static Func<WorldObject, IReadOnlyList<SummonSlotInfo>> DuoSlotsHook = DuoSummonGem.SlotInfos;

        private static readonly Skill[] MagicSchools =
        {
            Skill.WarMagic, Skill.LifeMagic, Skill.CreatureEnchantment, Skill.ItemEnchantment, Skill.VoidMagic
        };

        private static string DefaultSpellName(uint id)
        {
            var spell = new Spell(id, false);

            return spell._spellBase?.Name ?? $"spell {id}";
        }

        /// <summary>First and last wcid of the custom-summons block (Content/wcid-registry.d reservation).</summary>
        public const uint CustomWcidFirst = 1007700;
        public const uint CustomWcidLast = 1007799;

        /// <summary>
        /// TRUE for the items whose LongDesc this class owns: a device carrying SummonSpellBinder or bound spells, a Duo
        /// Gem, the dagger, or any weenie in the custom wcid block. Retail essences are not custom and keep their text.
        /// </summary>
        public static bool IsCustomSummon(WorldObject wo)
        {
            if (wo == null)
                return false;

            return wo.GetProperty(PropertyBool.SummonSpellBinder) == true
                || wo.GetProperty(PropertyDataId.SummonBoundSpell1).HasValue
                || wo.GetProperty(PropertyDataId.SummonBoundSpell2).HasValue
                || wo.GetProperty(PropertyBool.DuoSummonGem) == true
                || SacrificialDagger.IsDagger(wo)
                || (wo.WeenieClassId >= CustomWcidFirst && wo.WeenieClassId <= CustomWcidLast);
        }

        public static string Compose(WorldObject summonItem)
        {
            if (SacrificialDagger.IsDagger(summonItem))
                return ComposeDagger(summonItem);

            if (summonItem.GetProperty(PropertyBool.DuoSummonGem) == true)
                return ComposeDuo(summonItem);

            var sb = new StringBuilder();

            var source = PetSummonSource.FromDevice(summonItem as PetDevice ?? throw new ArgumentException("not a summon device", nameof(summonItem)));

            // a blank caster is not a caster by its spells yet, but by its binder mark
            var isCaster = summonItem.GetProperty(PropertyBool.SummonSpellBinder) == true || source.HasBoundSpells;

            AppendOne(sb, source, isCaster, (UiEffects?)summonItem.GetProperty(PropertyInt.UiEffects), "");

            return sb.ToString().TrimEnd('\n');
        }

        private static string ComposeDuo(WorldObject gem)
        {
            var sb = new StringBuilder();
            var slots = DuoSlotsHook(gem);

            if (slots.Count == 0)
                sb.AppendLine("Absorbs two summoning items into one.");

            var n = 1;
            foreach (var slot in slots)
            {
                sb.AppendLine($"Summon {n}: {slot.TypeName}");
                AppendOne(sb, slot.Source, slot.Source.HasBoundSpells, null, "  ");
                n++;
            }

            var element = (UiEffects?)gem.GetProperty(PropertyInt.UiEffects);
            if (slots.Count > 0)
                AppendElement(sb, element, "");

            return sb.ToString().TrimEnd('\n');
        }

        private static string ComposeDagger(WorldObject dagger)
        {
            var sb = new StringBuilder();

            if (!SacrificialDagger.IsImprinted(dagger))
            {
                sb.Append("The dagger is not imprinted. Use it on a summoning item to destroy that item and imprint its ratings.");
                return sb.ToString();
            }

            sb.AppendLine("Imprinted. Use it on a summoning item to replace that item's ratings with these, spending the dagger.");
            sb.Append(RatingsLine(SummonRatings.Read(dagger), ""));

            return sb.ToString();
        }

        private static void AppendOne(StringBuilder sb, PetSummonSource source, bool isCaster, UiEffects? element, string indent)
        {
            var weenie = source.PetWcid == 0 ? null : PetWeenieLookup(source.PetWcid);

            if (weenie?.PropertiesInt != null && weenie.PropertiesInt.TryGetValue(PropertyInt.Level, out var level))
                sb.AppendLine($"{indent}Level {level}");

            if (isCaster)
            {
                var skill = MagicSkillOf(weenie);
                if (skill > 0)
                    sb.AppendLine($"{indent}Magic skill: {skill}");

                sb.AppendLine($"{indent}Spell 1: {SpellText(source.BoundSpell1)}");
                sb.AppendLine($"{indent}Spell 2: {SpellText(source.BoundSpell2)}");
            }

            if (element.HasValue)
                AppendElement(sb, element, indent);

            sb.AppendLine(RatingsLine(new SummonRatings(
                source.GearDamage ?? 0, source.GearDamageResist ?? 0, source.GearCrit ?? 0,
                source.GearCritResist ?? 0, source.GearCritDamage ?? 0, source.GearCritDamageResist ?? 0), indent));
        }

        private static void AppendElement(StringBuilder sb, UiEffects? effects, string indent)
        {
            var name = ElementName(effects);

            if (name != null)
                sb.AppendLine($"{indent}Element: {name}");
        }

        /// <summary>The element word for a UiEffects value, or null when it carries none of the four.</summary>
        public static string ElementName(UiEffects? effects)
        {
            if (!effects.HasValue) return null;

            var e = effects.Value;

            if (e.HasFlag(UiEffects.Fire)) return "Fire";
            if (e.HasFlag(UiEffects.Frost)) return "Frost";
            if (e.HasFlag(UiEffects.Lightning)) return "Lightning";
            if (e.HasFlag(UiEffects.Acid)) return "Acid";

            return null;
        }

        private static string SpellText(uint? spellId)
        {
            return spellId.HasValue ? SpellNameLookup(spellId.Value) : "unbound slot";
        }

        private static uint MagicSkillOf(Weenie weenie)
        {
            if (weenie?.PropertiesSkill == null)
                return 0;

            uint best = 0;

            foreach (var school in MagicSchools)
            {
                if (weenie.PropertiesSkill.TryGetValue(school, out var row) && row.InitLevel > best)
                    best = row.InitLevel;
            }

            return best;
        }

        private static string RatingsLine(SummonRatings r, string indent)
        {
            return $"{indent}Ratings: Damage {r.Damage}, Damage Resist {r.DamageResist}, Crit {r.Crit}, Crit Resist {r.CritResist}, Crit Damage {r.CritDamage}, Crit Damage Resist {r.CritDamageResist}";
        }
    }
}
