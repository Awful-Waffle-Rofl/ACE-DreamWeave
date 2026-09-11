using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The appraisal-panel lines a market snapshot carries. Mirrors AppraiseInfo's property selection
    /// and its type branches, but reads properties ONLY: anything needing a live examiner, wielder or
    /// enchantment layer is deliberately absent, so the two projection paths cannot diverge.
    /// </summary>
    public static class MarketAppraisal
    {
        // Enum.GetValues is reflection, and this runs per row of a vault view that can hold 1000.
        private static readonly ImbuedEffectType[] ImbuedFlags = (ImbuedEffectType[])Enum.GetValues(typeof(ImbuedEffectType));
        private static readonly DamageType[] DamageFlags = (DamageType[])Enum.GetValues(typeof(DamageType));

        public static List<string> PanelLines(WorldObject item)
            => item == null ? new List<string>() : Build(new ItemSource(item));

        public static List<string> PanelLines(Weenie weenie)
            => weenie == null ? new List<string>() : Build(new WeenieSource(weenie));

        /// <summary>The item's own description text, which the client draws under the panel.</summary>
        public static string Description(WorldObject item)
            => Trim(item?.GetProperty(PropertyString.LongDesc));

        public static string Description(Weenie weenie)
            => Trim(weenie?.GetProperty(PropertyString.LongDesc));

        // ---- the one line set, over either source ----

        private static List<string> Build(ISource s)
        {
            var lines = new List<string>();

            AddIdentity(s, lines);
            AddArmor(s, lines);
            AddWeapon(s, lines);
            AddBonuses(s, lines);
            AddRatings(s, lines);
            AddRequirements(s, lines);
            AddFlags(s, lines);

            Add(lines, "Burden", s.Int(PropertyInt.EncumbranceVal));
            Add(lines, "Value", s.Int(PropertyInt.Value));

            return lines;
        }

        private static void AddIdentity(ISource s, List<string> lines)
        {
            var material = s.Int(PropertyInt.MaterialType);
            if (material > 0)
                Add(lines, "Material", Spaced(((MaterialType)material.Value).ToString()));

            // NOT PropertyInt.ItemWorkmanship: that is the accumulated total, which on a salvage bag
            // of 100 units reads 117 where the player must be shown 1.17.
            var workmanship = PerUnitWorkmanship(
                s.Int(PropertyInt.ItemWorkmanship),
                s.Int(PropertyInt.NumItemsInMaterial),
                s.Int(PropertyInt.Structure));

            if (workmanship.HasValue)
                lines.Add($"Workmanship: {Number(workmanship.Value)}");

            Add(lines, "Number of Times Tinkered", s.Int(PropertyInt.NumTimesTinkered));
            Add(lines, "Imbued Effect", Imbues(s));

            var gemCount = s.Int(PropertyInt.GemCount);
            var gemType = s.Int(PropertyInt.GemType);
            if (gemCount > 0 && gemType > 0)
                lines.Add($"Gems: {gemCount.Value} {Spaced(((MaterialType)gemType.Value).ToString())}");

            var set = s.Int(PropertyInt.EquipmentSetId);
            if (set > 0)
                Add(lines, "Set", Spaced(((EquipmentSet)set.Value).ToString()));

            Add(lines, "Craftsman", s.Str(PropertyString.CraftsmanName));
            Add(lines, "Tinkerer", s.Str(PropertyString.TinkerName));
            Add(lines, "Imbuer", s.Str(PropertyString.ImbuerName));
            Add(lines, "Gear Plating", s.Str(PropertyString.GearPlatingName));
            Add(lines, "Inscription", s.Str(PropertyString.Inscription));
            Add(lines, "Scribe", s.Str(PropertyString.ScribeName));
        }

        private static void AddArmor(ISource s, List<string> lines)
        {
            var armorLevel = s.Int(PropertyInt.ArmorLevel);
            if (armorLevel > 0)
                Add(lines, "Armor Level", armorLevel);

            // AppraiseInfo:111 - clothing and shields carry the full ArmorProfile, every band present.
            if (s.Type != WeenieType.Clothing && (CombatUse)(s.Int(PropertyInt.CombatUse) ?? 0) != CombatUse.Shield)
                return;

            AddProtection(s, lines, "Slashing", PropertyFloat.ArmorModVsSlash);
            AddProtection(s, lines, "Piercing", PropertyFloat.ArmorModVsPierce);
            AddProtection(s, lines, "Bludgeoning", PropertyFloat.ArmorModVsBludgeon);
            AddProtection(s, lines, "Cold", PropertyFloat.ArmorModVsCold);
            AddProtection(s, lines, "Fire", PropertyFloat.ArmorModVsFire);
            AddProtection(s, lines, "Acid", PropertyFloat.ArmorModVsAcid);
            AddProtection(s, lines, "Nether", PropertyFloat.ArmorModVsNether);
            AddProtection(s, lines, "Lightning", PropertyFloat.ArmorModVsElectric);
        }

        private static void AddWeapon(ISource s, List<string> lines)
        {
            if (!IsWeapon(s))
                return;

            var damage = s.Int(PropertyInt.Damage) ?? 0;
            if (damage > 0)
            {
                var variance = s.Float(PropertyFloat.DamageVariance) ?? 0.0;
                lines.Add($"Damage: {(int)Math.Round(damage * (1.0 - variance))} - {damage}");
            }

            Add(lines, "Damage Type", DamageTypes(s.Int(PropertyInt.DamageType) ?? 0));
            AddPct1(lines, "Damage Modifier", s.Float(PropertyFloat.DamageMod));
            AddPct1(lines, "Attack Bonus", s.Float(PropertyFloat.WeaponOffense));
            Add(lines, "Speed", s.Int(PropertyInt.WeaponTime));

            var skill = s.Int(PropertyInt.WeaponSkill);
            if (skill > 0)
                Add(lines, "Skill", ((Skill)skill.Value).ToSentence());

            var ammo = s.Int(PropertyInt.AmmoType);
            if (ammo > 0)
                Add(lines, "Ammunition Type", Spaced(((AmmoType)ammo.Value).ToString()));

            // AppraiseInfo sends MaximumVelocity, never PropertyInt.WeaponRange, so this is the one
            // reach number a player is actually shown for a launcher.
            var velocity = s.Float(PropertyFloat.MaximumVelocity);
            if (velocity.HasValue)
                lines.Add($"Missile Velocity: {Number(velocity.Value)}");
        }

        /// <summary>Bonuses AppraiseInfo sends for ANY item, not just one that took the weapon branch.</summary>
        private static void AddBonuses(ISource s, List<string> lines)
        {
            AddSigned(lines, "Elemental Damage Bonus", s.Int(PropertyInt.ElementalDamageBonus));

            AddPct1(lines, "Melee Defense Bonus", s.Float(PropertyFloat.WeaponDefense));
            AddPct1(lines, "Missile Defense Bonus", s.Float(PropertyFloat.WeaponMissileDefense));
            AddPct1(lines, "Magic Defense Bonus", s.Float(PropertyFloat.WeaponMagicDefense));
            AddPct1(lines, "Elemental Damage Modifier", s.Float(PropertyFloat.ElementalDamageMod));

            AddPct0(lines, "Mana Conversion Bonus", s.Float(PropertyFloat.ManaConversionMod));
            AddPct0(lines, "Critical Chance", s.Float(PropertyFloat.CriticalFrequency));
            AddPct0(lines, "Critical Damage Bonus", s.Float(PropertyFloat.CriticalMultiplier));
            AddPct0(lines, "Armor Cleaving", s.Float(PropertyFloat.IgnoreArmor));
            AddPct0(lines, "Magic Damage Absorption", s.Float(PropertyFloat.AbsorbMagicDamage));
            AddPct0(lines, "Healing Kit Bonus", s.Float(PropertyFloat.HealkitMod));

            var cooldown = s.Float(PropertyFloat.CooldownDuration);
            if (cooldown.HasValue)
                lines.Add($"Cooldown: {Number(cooldown.Value)} seconds");

            Add(lines, "Cleaving", s.Int(PropertyInt.Cleaving));

            var slayer = s.Int(PropertyInt.SlayerCreatureType);
            if (slayer > 0)
                Add(lines, "Slayer", Spaced(((CreatureType)slayer.Value).ToString()));

            var rendType = s.Int(PropertyInt.ResistanceModifierType);
            var rendMod = s.Float(PropertyFloat.ResistanceModifier);
            if (rendType > 0 && rendMod.HasValue)
                lines.Add($"Resistance Cleaving: {DamageTypes(rendType.Value)} {Pct0(rendMod.Value)}");

            AddSurge(s, lines);
        }

        /// <summary>
        /// The proc spell a cloak or a piece of aetheria fires, which the game names a Surge. It lives
        /// in PropertyDataId.ProcSpell and never in the biota spellbook, so nothing else in this panel
        /// would ever mention it.
        ///
        /// The rate suffix is omitted when PropertyFloat.ProcSpellRate is absent rather than rendered
        /// as zero: aetheria sets ProcSpell without ever setting a rate (Aetheria.cs:227-228).
        /// </summary>
        private static void AddSurge(ISource s, List<string> lines)
        {
            var procSpell = s.Did(PropertyDataId.ProcSpell);

            if (!procSpell.HasValue || procSpell.Value == 0)
                return;

            var name = MarketSnapshot.SpellName(unchecked((int)procSpell.Value));
            var rate = s.Float(PropertyFloat.ProcSpellRate);

            lines.Add(rate.HasValue ? $"Surge: {name} {Pct0(rate.Value)}" : $"Surge: {name}");
        }

        /// <summary>
        /// The 1 to 10 workmanship a player is shown, per unit of material.
        ///
        /// Mirrors WorldObject.Workmanship (WorldObject_Properties.cs:1617-1658) MINUS its write:
        /// that getter calls SetProperty to rewrite ItemWorkmanship in place when the quotient falls
        /// outside [1, 10], and this projection runs for every row of a vault view, so reading it here
        /// would write to stored items on a read. AccountVaultStore.BucketKey recomputes locally for
        /// exactly the same reason (AccountVaultStore.cs:546-567) and this is the same arithmetic.
        ///
        /// The recovery branch's CLAMP is replicated deliberately. The point of the fix is to show what
        /// the game shows, and the game never displays a value outside [1, 10]; dropping the clamp
        /// would put the market back to printing a number no appraisal panel ever produces. Only the
        /// SetProperty inside that branch is dropped.
        ///
        /// A zero denominator yields Infinity, which the clamp absorbs, exactly as the two sources
        /// above do. The one case they do not reach is 0/0, which is NaN, and NaN would make
        /// JsonSerializer throw and take the whole snapshot with it - so it resolves to the floor of
        /// the legal range instead.
        /// </summary>
        internal static double? PerUnitWorkmanship(int? itemWorkmanship, int? numItemsInMaterial, int? structure)
        {
            if (itemWorkmanship == null)
                return null;

            var workmanship = (double)itemWorkmanship.Value / (numItemsInMaterial ?? 1);

            // NaN fails both comparisons, so it must be tested for explicitly or it escapes unrecovered.
            if (double.IsNaN(workmanship) || workmanship < 1.0 || workmanship > 10.0)
            {
                workmanship = (double)itemWorkmanship.Value / 10000.0 / (structure ?? 1);

                workmanship = double.IsNaN(workmanship) ? 1.0 : Math.Clamp(workmanship, 1.0, 10.0);
            }

            return Math.Round(workmanship, 2, MidpointRounding.AwayFromZero);
        }

        private static void AddRatings(ISource s, List<string> lines)
        {
            AddSigned(lines, "Damage Rating", s.Int(PropertyInt.GearDamage));
            AddSigned(lines, "Damage Reduction Rating", s.Int(PropertyInt.GearDamageResist));
            AddSigned(lines, "Critical Rating", s.Int(PropertyInt.GearCrit));
            AddSigned(lines, "Critical Resistance Rating", s.Int(PropertyInt.GearCritResist));
            AddSigned(lines, "Critical Damage Rating", s.Int(PropertyInt.GearCritDamage));
            AddSigned(lines, "Critical Damage Reduction Rating", s.Int(PropertyInt.GearCritDamageResist));
            AddSigned(lines, "Healing Boost Rating", s.Int(PropertyInt.GearHealingBoost));
            AddSigned(lines, "Nether Resistance Rating", s.Int(PropertyInt.GearNetherResist));
            AddSigned(lines, "Life Resistance Rating", s.Int(PropertyInt.GearLifeResist));
            AddSigned(lines, "Maximum Health Bonus", s.Int(PropertyInt.GearMaxHealth));
            AddSigned(lines, "PK Damage Rating", s.Int(PropertyInt.GearPKDamageRating));
            AddSigned(lines, "PK Damage Reduction Rating", s.Int(PropertyInt.GearPKDamageResistRating));
            AddSigned(lines, "Overpower Rating", s.Int(PropertyInt.GearOverpower));
            AddSigned(lines, "Overpower Resistance Rating", s.Int(PropertyInt.GearOverpowerResist));
        }

        private static void AddRequirements(ISource s, List<string> lines)
        {
            AddWield(s, lines, PropertyInt.WieldRequirements, PropertyInt.WieldSkillType, PropertyInt.WieldDifficulty);
            AddWield(s, lines, PropertyInt.WieldRequirements2, PropertyInt.WieldSkillType2, PropertyInt.WieldDifficulty2);
            AddWield(s, lines, PropertyInt.WieldRequirements3, PropertyInt.WieldSkillType3, PropertyInt.WieldDifficulty3);
            AddWield(s, lines, PropertyInt.WieldRequirements4, PropertyInt.WieldSkillType4, PropertyInt.WieldDifficulty4);

            var skillLimit = s.Did(PropertyDataId.ItemSkillLimit);
            var skillLevel = s.Int(PropertyInt.ItemSkillLevelLimit);

            if (skillLimit > 0)
                lines.Add($"Skill Required: {((Skill)skillLimit.Value).ToSentence()}{(skillLevel > 0 ? " " + skillLevel.Value : "")}");
            else if (skillLevel > 0)
                Add(lines, "Skill Level Required", skillLevel);

            Add(lines, "Difficulty", s.Int(PropertyInt.ItemDifficulty));
            Add(lines, "Spellcraft", s.Int(PropertyInt.ItemSpellcraft));

            var maxMana = s.Int(PropertyInt.ItemMaxMana);
            if (maxMana > 0)
                lines.Add($"Mana: {s.Int(PropertyInt.ItemCurMana) ?? 0} / {maxMana.Value}");

            Add(lines, "Mana Cost", s.Int(PropertyInt.ItemManaCost));

            // Stored as points per second and always negative on a drain; a period reads far better.
            var manaRate = s.Float(PropertyFloat.ManaRate);
            if (manaRate < 0)
                lines.Add($"Mana Rate: 1 point every {Number(-1.0 / manaRate.Value)} seconds");

            Add(lines, "Allegiance Rank Required", s.Int(PropertyInt.ItemAllegianceRankLimit));
            Add(lines, "Item Level Cap", s.Int(PropertyInt.ItemMaxLevel));

            var itemXp = s.Int64(PropertyInt64.ItemTotalXp);
            if (itemXp.HasValue)
                lines.Add($"Item Experience: {itemXp.Value.ToString("N0", CultureInfo.InvariantCulture)}");

            var lifespan = s.Int(PropertyInt.Lifespan);
            if (lifespan > 0)
                lines.Add($"Lifespan: {lifespan.Value} seconds");
        }

        private static void AddFlags(ISource s, List<string> lines)
        {
            if (s.Int(PropertyInt.Attuned) > 0)
                lines.Add("Attuned");

            if (s.Int(PropertyInt.Bonded) > 0)
                lines.Add("Bonded");

            var unique = s.Int(PropertyInt.Unique);
            if (unique > 0)
                Add(lines, "Unique", unique);

            if (s.Bool(PropertyBool.Retained) == true)
                lines.Add("Retained");

            if (s.Bool(PropertyBool.Dyable) == true)
                lines.Add("Dyable");

            if (s.Bool(PropertyBool.Ivoryable) == true)
                lines.Add("Ivoryable");

            if (s.Bool(PropertyBool.UnlimitedUse) == true)
                lines.Add("Unlimited use");

            if (s.Bool(PropertyBool.IsSellable) == false)
                lines.Add("Cannot be sold to a vendor");
        }

        // ---- branch tests, mirroring AppraiseInfo ----

        /// <summary>AppraiseInfo:117. A Caster is excluded because BuildWeapon never attaches its profile.</summary>
        private static bool IsWeapon(ISource s)
        {
            if (s.Type == WeenieType.Caster)
                return false;

            if (s.Type == WeenieType.MeleeWeapon || s.Type == WeenieType.Missile
                || s.Type == WeenieType.MissileLauncher || s.Type == WeenieType.Ammunition)
                return true;

            return s.Type != WeenieType.Clothing && s.Int(PropertyInt.Damage).HasValue;
        }

        // ---- formatting ----

        private static void Add(List<string> lines, string label, int? value)
        {
            if (value.HasValue)
                lines.Add($"{label}: {value.Value}");
        }

        private static void Add(List<string> lines, string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                lines.Add($"{label}: {value}");
        }

        private static void AddSigned(List<string> lines, string label, int? value)
        {
            if (value.HasValue && value.Value != 0)
                lines.Add($"{label}: {(value.Value > 0 ? "+" : string.Empty)}{value.Value}");
        }

        /// <summary>For the multipliers stored around 1.0, which the panel shows as a percentage bonus.</summary>
        private static void AddPct1(List<string> lines, string label, double? value)
        {
            if (value.HasValue)
                lines.Add($"{label}: {Pct1(value.Value)}");
        }

        /// <summary>For the fractions stored around 0.0.</summary>
        private static void AddPct0(List<string> lines, string label, double? value)
        {
            if (value.HasValue)
                lines.Add($"{label}: {Pct0(value.Value)}");
        }

        private static void AddProtection(ISource s, List<string> lines, string label, PropertyFloat key)
        {
            var value = Math.Clamp(s.Float(key) ?? 1.0, -2.0, 2.0);
            lines.Add($"{label} Protection: {value.ToString("0.00", CultureInfo.InvariantCulture)}");
        }

        private static void AddWield(ISource s, List<string> lines, PropertyInt req, PropertyInt skillType, PropertyInt difficulty)
        {
            var requirement = (WieldRequirement)(s.Int(req) ?? 0);
            var value = s.Int(difficulty);

            if (requirement == WieldRequirement.Invalid || !value.HasValue)
                return;

            var type = s.Int(skillType) ?? 0;

            if (requirement == WieldRequirement.Training)
            {
                lines.Add($"Wield Requirement: {((Skill)type).ToSentence()} {(SkillAdvancementClass)value.Value}");
                return;
            }

            var subject = WieldSubject(requirement, type);

            lines.Add(subject == null
                ? $"Wield Requirement: {value.Value}"
                : $"Wield Requirement: {subject} {value.Value}");
        }

        private static string WieldSubject(WieldRequirement requirement, int type)
        {
            switch (requirement)
            {
                case WieldRequirement.Skill:
                case WieldRequirement.RawSkill:
                    return ((Skill)type).ToSentence();
                case WieldRequirement.Attrib:
                case WieldRequirement.RawAttrib:
                    return Spaced(((PropertyAttribute)type).ToString());
                case WieldRequirement.SecondaryAttrib:
                case WieldRequirement.RawSecondaryAttrib:
                    return Spaced(((PropertyAttribute2nd)type).ToString());
                case WieldRequirement.Level:
                    return "Level";
                case WieldRequirement.CreatureType:
                    return Spaced(((CreatureType)type).ToString());
                case WieldRequirement.HeritageType:
                    return Spaced(((HeritageGroup)type).ToString());
                default:
                    return null;
            }
        }

        private static string Imbues(ISource s)
        {
            var bits = unchecked((uint)(s.Int(PropertyInt.ImbuedEffect) ?? 0))
                     | unchecked((uint)(s.Int(PropertyInt.ImbuedEffect2) ?? 0))
                     | unchecked((uint)(s.Int(PropertyInt.ImbuedEffect3) ?? 0))
                     | unchecked((uint)(s.Int(PropertyInt.ImbuedEffect4) ?? 0))
                     | unchecked((uint)(s.Int(PropertyInt.ImbuedEffect5) ?? 0));

            if (bits == 0)
                return null;

            var names = new List<string>();

            foreach (var flag in ImbuedFlags)
            {
                if (flag != ImbuedEffectType.Undef && (bits & (uint)flag) == (uint)flag)
                    names.Add(Spaced(flag.ToString()));
            }

            return names.Count == 0 ? null : string.Join(", ", names);
        }

        private static string DamageTypes(int value)
        {
            if (value == 0)
                return null;

            var names = new List<string>();

            foreach (var flag in DamageFlags)
            {
                // Physical, Elemental and Base are composites, not damage types a panel names.
                if (flag == DamageType.Undef || flag == DamageType.Physical
                    || flag == DamageType.Elemental || flag == DamageType.Base)
                    continue;

                if ((value & (int)flag) == (int)flag)
                    names.Add(flag.GetName());
            }

            return names.Count == 0 ? null : string.Join(", ", names);
        }

        private static string Pct1(double value) => Percent((value - 1.0) * 100.0);

        private static string Pct0(double value) => Percent(value * 100.0);

        private static string Percent(double points)
            => (points >= 0 ? "+" : string.Empty) + Number(points) + "%";

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Trim(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>Splits an enum member into words so a panel line reads as the game names it.</summary>
        private static string Spaced(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            var text = new StringBuilder(name.Length + 8);

            for (var i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                    text.Append(' ');

                text.Append(name[i]);
            }

            return text.ToString();
        }

        // ---- the two sources ----

        private interface ISource
        {
            WeenieType Type { get; }
            int? Int(PropertyInt property);
            long? Int64(PropertyInt64 property);
            double? Float(PropertyFloat property);
            bool? Bool(PropertyBool property);
            string Str(PropertyString property);
            uint? Did(PropertyDataId property);
        }

        private sealed class ItemSource : ISource
        {
            private readonly WorldObject item;

            public ItemSource(WorldObject worldObject) => item = worldObject;

            public WeenieType Type => item.WeenieType;
            public int? Int(PropertyInt property) => item.GetProperty(property);
            public long? Int64(PropertyInt64 property) => item.GetProperty(property);
            public double? Float(PropertyFloat property) => item.GetProperty(property);
            public bool? Bool(PropertyBool property) => item.GetProperty(property);
            public string Str(PropertyString property) => item.GetProperty(property);
            public uint? Did(PropertyDataId property) => item.GetProperty(property);
        }

        private sealed class WeenieSource : ISource
        {
            private readonly Weenie weenie;

            public WeenieSource(Weenie source) => weenie = source;

            public WeenieType Type => weenie.WeenieType;
            public int? Int(PropertyInt property) => weenie.GetProperty(property);
            public long? Int64(PropertyInt64 property) => weenie.GetProperty(property);
            public double? Float(PropertyFloat property) => weenie.GetProperty(property);
            public bool? Bool(PropertyBool property) => weenie.GetProperty(property);
            public string Str(PropertyString property) => weenie.GetProperty(property);
            public uint? Did(PropertyDataId property) => weenie.GetProperty(property);
        }
    }
}
