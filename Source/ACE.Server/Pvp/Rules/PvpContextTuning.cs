using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>The weapon / spell category a context tuning key is keyed on. None = no context key applies.</summary>
    public enum PvpCombatCategory
    {
        None,
        Light,
        Heavy,
        Finesse,
        TwoHanded,
        Bow,
        Crossbow,
        Thrown,
        War
    }

    /// <summary>The war magic projectile shape a per-shape damage key is keyed on. None = no shape damage key (Volley, Strike, Undef).</summary>
    public enum PvpWarShape
    {
        None,
        Bolt,
        Arc,
        Streak,
        Blast,
        Ring,
        Wall
    }

    /// <summary>The special weapon variants that carry their own multipliers. Flags: one weapon can be both.</summary>
    [Flags]
    public enum PvpWeaponVariant
    {
        None = 0,
        /// <summary>Ignores magic resist or magic armor (PropertyBool.IgnoreMagicResist / IgnoreMagicArmor).</summary>
        Hollow = 1,
        /// <summary>A Human slayer: SlayerCreatureType Human with a SlayerDamageBonus.</summary>
        Weeping = 2
    }

    /// <summary>The two match contexts that carry context tuning keys. Open-world PK has none.</summary>
    public enum PvpContext
    {
        Arena,
        Battleground
    }

    /// <summary>
    /// What a hit is, described WITHOUT reading any setting: the weapon (and whether it is a missile hit) for a
    /// physical hit, or the projectile's school and shape for a spell. Resolved to a category only after the pair is
    /// classified as an Arena / Battleground interaction. Default = nothing known (no context key applies).
    /// </summary>
    public readonly struct PvpHitProfile
    {
        public WorldObject Weapon { get; }
        public bool IsMissile { get; }
        public bool IsSpell { get; }
        public MagicSchool School { get; }
        public ProjectileSpellType SpellType { get; }
        public bool IsKnown { get; }

        private PvpHitProfile(WorldObject weapon, bool isMissile, bool isSpell, MagicSchool school, ProjectileSpellType spellType)
        {
            Weapon = weapon;
            IsMissile = isMissile;
            IsSpell = isSpell;
            School = school;
            SpellType = spellType;
            IsKnown = true;
        }

        /// <summary>A physical hit. <paramref name="weapon"/> is DamageEvent.Weapon: the swinging weapon (the offhand one on an offhand swing), or launcher ?? ammo for a missile; null when unarmed.</summary>
        public static PvpHitProfile ForWeapon(WorldObject weapon, CombatType combatType)
            => new PvpHitProfile(weapon, combatType == CombatType.Missile, false, MagicSchool.None, ProjectileSpellType.Undef);

        /// <summary>A spell projectile hit: <paramref name="school"/> is Spell.School, <paramref name="spellType"/> the projectile's SpellType.</summary>
        public static PvpHitProfile ForSpell(MagicSchool school, ProjectileSpellType spellType)
            => new PvpHitProfile(null, false, true, school, spellType);
    }

    /// <summary>
    /// A resolved hit: which context it is in, its category and war shape, and the dials snapshot to read. Default =
    /// inactive (every multiplier 1.0). Built by <see cref="PvpContextTuning.Resolve"/> only for an Arena or
    /// Battleground pair with the master switch on and a category, so an inactive hit never read a context key.
    /// </summary>
    public readonly struct PvpContextHit
    {
        public PvpContext Context { get; }
        public PvpCombatCategory Category { get; }
        public PvpWarShape Shape { get; }
        public PvpContextDials Dials { get; }
        public PvpWeaponVariant Variants { get; }

        public PvpContextHit(PvpContext context, PvpCombatCategory category, PvpWarShape shape, PvpContextDials dials, PvpWeaponVariant variants = PvpWeaponVariant.None)
        {
            Context = context;
            Category = category;
            Shape = shape;
            Dials = dials;
            Variants = variants;
        }

        public bool IsActive => Dials != null && Category != PvpCombatCategory.None;
    }

    /// <summary>
    /// The immutable snapshot of every context tuning key (arena and bg x damage / crit chance / crit damage / variant damage / magic absorb), in
    /// <see cref="PvpContextTuning.Keys"/> order. A value-by-key table rather than a many-field record, so the key set
    /// lives in exactly one place (the token tables in <see cref="PvpContextTuning"/>).
    /// </summary>
    public sealed class PvpContextDials
    {
        private readonly double[] values;

        /// <summary>Every key at the neutral 1.0.</summary>
        public static readonly PvpContextDials Neutral = new PvpContextDials(Enumerable.Repeat(1.0, PvpContextTuning.Keys.Count).ToArray());

        public PvpContextDials(double[] values)
        {
            if (values == null || values.Length != PvpContextTuning.Keys.Count)
                throw new ArgumentException($"expected {PvpContextTuning.Keys.Count} values");

            this.values = (double[])values.Clone();
        }

        /// <summary>The value of one key; 1.0 for a key that is not a context key.</summary>
        public double Get(string key)
            => PvpContextTuning.TryIndexOf(key, out var index) ? values[index] : 1.0;

        /// <summary>A copy with one key changed. Throws for a key that is not a context key.</summary>
        public PvpContextDials With(string key, double value)
        {
            if (!PvpContextTuning.TryIndexOf(key, out var index))
                throw new ArgumentException($"{key} is not a PvP context tuning key");

            var copy = (double[])values.Clone();
            copy[index] = value;

            return new PvpContextDials(copy);
        }

        internal double At(int index) => values[index];

        /// <summary>The (key, value-text) rows whose value differs from the registered default text, for the log line and /pvprules.</summary>
        public IReadOnlyList<(string Key, string Value)> NonDefault()
        {
            var rows = new List<(string, string)>();
            var defaultText = PropertyManager.FormatDouble(PvpContextTuning.NeutralValue);

            for (var i = 0; i < values.Length; i++)
            {
                var text = PropertyManager.FormatDouble(values[i]);

                if (text != defaultText)
                    rows.Add((PvpContextTuning.Keys[i], text));
            }

            return rows;
        }
    }

    /// <summary>
    /// Per-context, per-weapon-category PvP damage and crit tunables (Docs/Pvp/DESIGN.md "PvP rules (levers)", context
    /// tuning). Contexts: arena and bg (battleground); open-world PK has NONE of these keys. Categories: light,
    /// heavy, finesse, twohanded, bow, crossbow, thrown, war. Stats: dmg (per category; war is per SHAPE: bolt, arc,
    /// streak, blast, ring, walls), crit_chance and crit_dmg (per category, war included). Hollow and Weeping weapon
    /// variants add {cat}_{hollow|weeping}_dmg and {cat}_crit_dmg_{hollow|weeping}_dmg for the seven physical categories,
    /// and each context has one pvp_{ctx}_magic_absorb. Key scheme pvp_{ctx}_{category}_{stat}; 116 keys, all neutral at 1.0.
    ///
    /// THE KEY LIST IS GENERATED, never typed per key: the token tables below are the only place a context, category,
    /// shape or stat name appears, and <see cref="Keys"/>, the registered defaults
    /// (<see cref="RegisteredDefaults"/>), the dial reader and the lookups all derive from them.
    ///
    /// Pure functions only here; settings are read through <see cref="PvpContextTunables"/> and only after the
    /// interaction is classified as an Arena / Battleground pair (<see cref="Resolve"/>).
    /// </summary>
    public static class PvpContextTuning
    {
        /// <summary>The neutral value of every key.</summary>
        public const double NeutralValue = 1.0;

        // ---- the ONE place the names live ----

        private static readonly string[] ContextTokens = { "arena", "bg" };

        private static readonly string[] ContextLabels = { "arena", "battleground" };

        /// <summary>Category tokens in <see cref="PvpCombatCategory"/> order, skipping None (index = category - 1).</summary>
        private static readonly string[] CategoryTokens = { "light", "heavy", "finesse", "twohanded", "bow", "crossbow", "thrown", "war" };

        /// <summary>Shape tokens in <see cref="PvpWarShape"/> order, skipping None (index = shape - 1).</summary>
        private static readonly string[] ShapeTokens = { "bolt", "arc", "streak", "blast", "ring", "walls" };

        /// <summary>Variant tokens in <see cref="PvpWeaponVariant"/> bit order (index 0 = Hollow, 1 = Weeping).</summary>
        private static readonly string[] VariantTokens = { "hollow", "weeping" };

        private const string StatDmg = "dmg";
        private const string StatCritChance = "crit_chance";
        private const string StatCritDmg = "crit_dmg";
        private const string StatMagicAbsorb = "magic_absorb";

        // per-context slot layout, in order:
        //   damage: the seven non-war categories, then the six war shapes
        //   crit chance (8 categories), crit damage (8 categories)
        //   variant damage: per variant, the seven non-war categories (hollow x7, weeping x7)
        //   variant crit damage: per variant, the seven non-war categories (hollow x7, weeping x7)
        //   magic absorb (1)
        private const int NonWarCategories = 7;
        private const int DmgSlots = NonWarCategories + 6;
        private const int CritSlots = 8;
        private const int Variants = 2;
        private const int VariantDmgBase = DmgSlots + CritSlots + CritSlots;
        private const int VariantCritBase = VariantDmgBase + Variants * NonWarCategories;
        private const int AbsorbSlot = VariantCritBase + Variants * NonWarCategories;
        private const int PerContext = AbsorbSlot + 1;

        private static readonly string[] keys = BuildKeys();
        private static readonly Dictionary<string, int> indexByKey = BuildIndex(keys);

        /// <summary>All context tuning keys (116), in the order the snapshot, the log line and /pvprules use.</summary>
        public static IReadOnlyList<string> Keys => keys;

        public static bool TryIndexOf(string key, out int index)
        {
            index = -1;

            return key != null && indexByKey.TryGetValue(key, out index);
        }

        public static bool IsContextKey(string key) => key != null && indexByKey.ContainsKey(key);

        private static string[] BuildKeys()
        {
            var list = new List<string>(ContextTokens.Length * PerContext);

            foreach (var ctx in ContextTokens)
            {
                for (var i = 0; i < NonWarCategories; i++)
                    list.Add($"pvp_{ctx}_{CategoryTokens[i]}_{StatDmg}");

                foreach (var shape in ShapeTokens)
                    list.Add($"pvp_{ctx}_{CategoryTokens[CategoryTokens.Length - 1]}_{shape}_{StatDmg}");

                foreach (var category in CategoryTokens)
                    list.Add($"pvp_{ctx}_{category}_{StatCritChance}");

                foreach (var category in CategoryTokens)
                    list.Add($"pvp_{ctx}_{category}_{StatCritDmg}");

                foreach (var variant in VariantTokens)
                {
                    for (var i = 0; i < NonWarCategories; i++)
                        list.Add($"pvp_{ctx}_{CategoryTokens[i]}_{variant}_{StatDmg}");
                }

                foreach (var variant in VariantTokens)
                {
                    for (var i = 0; i < NonWarCategories; i++)
                        list.Add($"pvp_{ctx}_{CategoryTokens[i]}_{StatCritDmg}_{variant}_{StatDmg}");
                }

                list.Add($"pvp_{ctx}_{StatMagicAbsorb}");
            }

            return list.ToArray();
        }

        private static Dictionary<string, int> BuildIndex(string[] all)
        {
            var map = new Dictionary<string, int>(all.Length, StringComparer.Ordinal);

            for (var i = 0; i < all.Length; i++)
                map.Add(all[i], i);

            return map;
        }

        private static int DmgIndex(PvpContext ctx, PvpCombatCategory category, PvpWarShape shape)
        {
            var baseIndex = (int)ctx * PerContext;

            if (category == PvpCombatCategory.War)
                return shape == PvpWarShape.None ? -1 : baseIndex + NonWarCategories + ((int)shape - 1);

            return category == PvpCombatCategory.None ? -1 : baseIndex + ((int)category - 1);
        }

        private static int CritChanceIndex(PvpContext ctx, PvpCombatCategory category)
            => category == PvpCombatCategory.None ? -1 : (int)ctx * PerContext + DmgSlots + ((int)category - 1);

        private static int CritDmgIndex(PvpContext ctx, PvpCombatCategory category)
            => category == PvpCombatCategory.None ? -1 : (int)ctx * PerContext + DmgSlots + CritSlots + ((int)category - 1);

        /// <summary>Slot of a variant multiplier: -1 for war / None (variants exist only for the seven physical categories). <paramref name="variantIndex"/> 0 = Hollow, 1 = Weeping.</summary>
        private static int VariantIndex(int variantBase, PvpContext ctx, PvpCombatCategory category, int variantIndex)
            => category == PvpCombatCategory.None || category == PvpCombatCategory.War
                ? -1
                : (int)ctx * PerContext + variantBase + variantIndex * NonWarCategories + ((int)category - 1);

        private static int MagicAbsorbIndex(PvpContext ctx) => (int)ctx * PerContext + AbsorbSlot;

        /// <summary>The registered key of a (context, physical category, variant) damage multiplier (variantIndex 0 = hollow, 1 = weeping); null for war / None.</summary>
        public static string VariantDamageKey(PvpContext ctx, PvpCombatCategory category, PvpWeaponVariant variant)
        {
            var index = VariantIndex(VariantDmgBase, ctx, category, VariantBit(variant));

            return index < 0 ? null : keys[index];
        }

        /// <summary>The registered key of a (context, physical category, variant) crit multiplier; null for war / None.</summary>
        public static string VariantCritDamageKey(PvpContext ctx, PvpCombatCategory category, PvpWeaponVariant variant)
        {
            var index = VariantIndex(VariantCritBase, ctx, category, VariantBit(variant));

            return index < 0 ? null : keys[index];
        }

        /// <summary>The registered key of a context's magic absorb mod.</summary>
        public static string MagicAbsorbKey(PvpContext ctx) => keys[MagicAbsorbIndex(ctx)];

        private static int VariantBit(PvpWeaponVariant variant)
        {
            if (variant == PvpWeaponVariant.Hollow)
                return 0;

            if (variant == PvpWeaponVariant.Weeping)
                return 1;

            throw new ArgumentException("variant must be exactly Hollow or Weeping", nameof(variant));
        }

        /// <summary>The registered key of a (context, category, shape) damage mod, or null when there is none (war with no shape, None).</summary>
        public static string DamageKey(PvpContext ctx, PvpCombatCategory category, PvpWarShape shape)
        {
            var index = DmgIndex(ctx, category, shape);

            return index < 0 ? null : keys[index];
        }

        public static string CritChanceKey(PvpContext ctx, PvpCombatCategory category)
        {
            var index = CritChanceIndex(ctx, category);

            return index < 0 ? null : keys[index];
        }

        public static string CritDamageKey(PvpContext ctx, PvpCombatCategory category)
        {
            var index = CritDmgIndex(ctx, category);

            return index < 0 ? null : keys[index];
        }

        // ---- registered defaults (generated; DefaultPropertyManager merges these in) ----

        /// <summary>One (key, description) per key. Every default is <see cref="NeutralValue"/>.</summary>
        public static IEnumerable<(string Key, string Description)> RegisteredDefaults()
        {
            for (var i = 0; i < keys.Length; i++)
                yield return (keys[i], Describe(i));
        }

        private static string Describe(int index)
        {
            var ctx = index / PerContext;
            var slot = index % PerContext;
            var label = ContextLabels[ctx];
            string what;

            if (slot < DmgSlots)
            {
                what = slot < NonWarCategories
                    ? $"multiplier on the damage of a {CategoryTokens[slot]} weapon hit"
                    : $"multiplier on the damage of a WAR magic {ShapeTokens[slot - NonWarCategories]} projectile hit (volley and strike have no shape key)";
            }
            else if (slot < DmgSlots + CritSlots)
            {
                var c = CategoryTokens[slot - DmgSlots];
                what = $"multiplier on the CRIT CHANCE of a {(c == "war" ? "war magic projectile" : c + " weapon hit")}, applied to the final chance and clamped to [0, 1] (before the logout always-crit rule for physical hits)";
            }
            else if (slot < VariantDmgBase)
            {
                var c = CategoryTokens[slot - DmgSlots - CritSlots];
                what = $"multiplier on the WHOLE of a critical {(c == "war" ? "war magic projectile" : c + " weapon")} hit, on top of pvp_crit_damage_mod";
            }
            else if (slot < VariantCritBase)
            {
                var rel = slot - VariantDmgBase;
                var v = VariantTokens[rel / NonWarCategories];
                what = $"multiplier on the damage of a {CategoryTokens[rel % NonWarCategories]} weapon hit made with a {VariantDescription(v)}, on top of pvp_{ContextTokens[ctx]}_{CategoryTokens[rel % NonWarCategories]}_{StatDmg}";
            }
            else if (slot < AbsorbSlot)
            {
                var rel = slot - VariantCritBase;
                var v = VariantTokens[rel / NonWarCategories];
                what = $"multiplier on the WHOLE of a critical {CategoryTokens[rel % NonWarCategories]} weapon hit made with a {VariantDescription(v)}, on top of the {CategoryTokens[rel % NonWarCategories]} crit_dmg key";
            }
            else
            {
                return $"PvP rules, {label} context tuning (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on the magic absorb REDUCTION fraction (shield, launcher or wand AbsorbMagicDamage) on a projectile hit between two players in the same Live {label} match, at choke point AB1: absorb = 1 - (1 - absorb) x mod, clamped to [0, 1]. Applied AFTER the global pvp_magic_absorb_mod (the two compose). Open-world PK is never affected. 0 = absorption does nothing, 2 = twice the fraction. NaN, infinite or negative = unchanged. 1.0 = unchanged";
            }

            return $"PvP rules, {label} context tuning (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): {what}, between two players in the same Live {label} match. Open-world PK is never affected. Stacks with the global pvp_*_damage_mod keys and pvp_arena_dmg_mod_1v1; applied before the PvP damage cap. NaN, infinite or negative = unchanged. 1.0 = unchanged";
        }

        private static string VariantDescription(string variantToken)
            => variantToken == "hollow"
                ? "HOLLOW weapon (ignores magic resist or magic armor)"
                : "WEEPING weapon (Human slayer)";

        // ---- weapon variants (hollow / weeping) ----

        /// <summary>
        /// The variants of a weapon: Hollow when PropertyBool.IgnoreMagicResist or IgnoreMagicArmor is true, Weeping when
        /// SlayerCreatureType is Human and a SlayerDamageBonus is present. None for null. Reads item properties only,
        /// never a setting.
        /// </summary>
        public static PvpWeaponVariant VariantsOf(WorldObject weapon)
        {
            if (weapon == null)
                return PvpWeaponVariant.None;

            System.Threading.Interlocked.Increment(ref VariantReadCount);

            var variants = PvpWeaponVariant.None;

            if (weapon.GetProperty(PropertyBool.IgnoreMagicResist) == true || weapon.GetProperty(PropertyBool.IgnoreMagicArmor) == true)
                variants |= PvpWeaponVariant.Hollow;

            if (weapon.GetProperty(PropertyInt.SlayerCreatureType) == (int)CreatureType.Human && weapon.GetProperty(PropertyFloat.SlayerDamageBonus) != null)
                variants |= PvpWeaponVariant.Weeping;

            return variants;
        }

        /// <summary>The variants of a physical hit, read from profile.Weapon only: the same object combat uses for hollow and slayer (DamageEvent.Weapon, launcher ?? ammo for a missile). A flagged ammo under a plain launcher is NOT a variant hit.</summary>
        public static PvpWeaponVariant VariantsOf(PvpHitProfile profile)
            => VariantsOf(profile.Weapon);

        /// <summary>Test seam: how many times a weapon's variant properties were read. Crit-chance resolution must never move it.</summary>
        internal static long VariantReadCount;

        // ---- pure categorization ----

        /// <summary>
        /// PURE. The category of a PHYSICAL hit. <paramref name="hasWeapon"/> false = unarmed: the category of
        /// <paramref name="highestMelee"/> (the attacker's best light / heavy / finesse skill); an unarmed MISSILE hit
        /// has none. Otherwise <paramref name="weaponSkill"/> (already through ConvertToMoASkill) picks Light, Heavy,
        /// Finesse or TwoHanded, and the missile skills split on <paramref name="weaponType"/> (Bow, Crossbow, Thrown -
        /// atlatls carry Thrown). A melee hit never lands in a missile category or the reverse, and any other skill
        /// (a wand's war or void skill) is None. Never GetCurrentWeaponSkill: that is DualWield for an offhand swing.
        /// </summary>
        public static PvpCombatCategory WeaponCategory(bool hasWeapon, Skill weaponSkill, WeaponType weaponType, bool isMissile, Skill highestMelee)
        {
            if (!hasWeapon && isMissile)
                return PvpCombatCategory.None;

            var skill = hasWeapon ? weaponSkill : highestMelee;
            var category = PvpCombatCategory.None;

            switch (skill)
            {
                case Skill.LightWeapons:
                    category = PvpCombatCategory.Light;
                    break;
                case Skill.HeavyWeapons:
                    category = PvpCombatCategory.Heavy;
                    break;
                case Skill.FinesseWeapons:
                    category = PvpCombatCategory.Finesse;
                    break;
                case Skill.TwoHandedCombat:
                    category = PvpCombatCategory.TwoHanded;
                    break;
                case Skill.MissileWeapons:
                case Skill.Bow:
                case Skill.Crossbow:
                case Skill.ThrownWeapon:
                    category = MissileCategory(skill, weaponType);
                    break;
            }

            var missileCategory = category == PvpCombatCategory.Bow || category == PvpCombatCategory.Crossbow || category == PvpCombatCategory.Thrown;
            var meleeCategory = category != PvpCombatCategory.None && !missileCategory;

            if (isMissile && meleeCategory || !isMissile && missileCategory)
                return PvpCombatCategory.None;

            return category;
        }

        private static PvpCombatCategory MissileCategory(Skill skill, WeaponType weaponType)
        {
            switch (weaponType)
            {
                case WeaponType.Bow:
                    return PvpCombatCategory.Bow;
                case WeaponType.Crossbow:
                    return PvpCombatCategory.Crossbow;
                case WeaponType.Thrown:
                    return PvpCombatCategory.Thrown;
            }

            // no usable WeaponType: fall back on a legacy per-weapon skill, else the hit has no category
            switch (skill)
            {
                case Skill.Bow:
                    return PvpCombatCategory.Bow;
                case Skill.Crossbow:
                    return PvpCombatCategory.Crossbow;
                case Skill.ThrownWeapon:
                    return PvpCombatCategory.Thrown;
                default:
                    return PvpCombatCategory.None;
            }
        }

        /// <summary>PURE. War for the War Magic school, None for everything else (void and life projectiles get no context key).</summary>
        public static PvpCombatCategory SpellCategory(MagicSchool school)
            => school == MagicSchool.WarMagic ? PvpCombatCategory.War : PvpCombatCategory.None;

        /// <summary>PURE. The shape of a war projectile for the six shapes that carry a damage key (Wall's key is "walls"); None for Volley, Strike and Undef.</summary>
        public static PvpWarShape SpellShape(ProjectileSpellType spellType)
        {
            switch (spellType)
            {
                case ProjectileSpellType.Bolt:
                    return PvpWarShape.Bolt;
                case ProjectileSpellType.Arc:
                    return PvpWarShape.Arc;
                case ProjectileSpellType.Streak:
                    return PvpWarShape.Streak;
                case ProjectileSpellType.Blast:
                    return PvpWarShape.Blast;
                case ProjectileSpellType.Ring:
                    return PvpWarShape.Ring;
                case ProjectileSpellType.Wall:
                    return PvpWarShape.Wall;
                default:
                    return PvpWarShape.None;
            }
        }

        /// <summary>PURE. The context a scope maps to; null for None and OpenWorld (no context key applies).</summary>
        public static PvpContext? ContextOf(PvpScope scope)
        {
            switch (scope)
            {
                case PvpScope.Arena:
                    return PvpContext.Arena;
                case PvpScope.Battleground:
                    return PvpContext.Battleground;
                default:
                    return null;
            }
        }

        // ---- pure math ----

        /// <summary>
        /// PURE. The crit chance after a context mod: exactly <paramref name="chance"/> at the identity 1.0 (or for a
        /// NaN chance), otherwise chance x mod clamped to [0, 1]. The mod goes through PvpRules.SanitizeMod (NaN,
        /// infinite or negative = 1.0); 0 removes crits.
        /// </summary>
        public static double ScaleCritChance(double chance, double mod)
        {
            mod = PvpRules.SanitizeMod(mod);

            if (mod == 1.0 || double.IsNaN(chance))
                return chance;

            var scaled = chance * mod;

            if (scaled < 0.0)
                return 0.0;

            return scaled > 1.0 ? 1.0 : scaled;
        }

        /// <summary>PURE. The damage multiplier of a resolved hit: the (category, shape) damage key, times the crit-damage key on a crit. 1.0 for an inactive hit.</summary>
        public static double DamageMultiplier(PvpContextHit hit, bool isCritical)
        {
            if (!hit.IsActive)
                return 1.0;

            var multiplier = 1.0;

            var dmg = DmgIndex(hit.Context, hit.Category, hit.Shape);

            if (dmg >= 0)
                multiplier *= PvpRules.SanitizeMod(hit.Dials.At(dmg));

            // a variant weapon multiplies on top, in the same slot; a plain weapon (None) reads no variant key
            for (var v = 0; v < Variants; v++)
            {
                if ((hit.Variants & (PvpWeaponVariant)(1 << v)) == PvpWeaponVariant.None)
                    continue;

                var variantDmg = VariantIndex(VariantDmgBase, hit.Context, hit.Category, v);

                if (variantDmg >= 0)
                    multiplier *= PvpRules.SanitizeMod(hit.Dials.At(variantDmg));
            }

            if (isCritical)
            {
                multiplier *= PvpRules.SanitizeMod(hit.Dials.At(CritDmgIndex(hit.Context, hit.Category)));

                for (var v = 0; v < Variants; v++)
                {
                    if ((hit.Variants & (PvpWeaponVariant)(1 << v)) == PvpWeaponVariant.None)
                        continue;

                    var variantCrit = VariantIndex(VariantCritBase, hit.Context, hit.Category, v);

                    if (variantCrit >= 0)
                        multiplier *= PvpRules.SanitizeMod(hit.Dials.At(variantCrit));
                }
            }

            return multiplier;
        }

        /// <summary>
        /// PURE. The context's magic absorb mod from a snapshot, sanitized (NaN, infinite or negative = 1.0). The
        /// choke point (PvpRules.ApplyMagicAbsorbMod) reads the snapshot only for an Arena / Battleground pair.
        /// </summary>
        public static double MagicAbsorbMod(PvpContext ctx, PvpContextDials dials)
            => dials == null ? 1.0 : PvpRules.SanitizeMod(dials.At(MagicAbsorbIndex(ctx)));

        /// <summary>PURE. The crit-chance multiplier key value of a resolved hit (1.0 for an inactive hit).</summary>
        public static double CritChanceMod(PvpContextHit hit)
            => hit.IsActive ? hit.Dials.At(CritChanceIndex(hit.Context, hit.Category)) : 1.0;

        // ---- resolution (classify first, read second) ----

        /// <summary>Seam: the attacker's highest melee skill, used only for an unarmed melee hit. Tests swap it because a seeded Player has no skills.</summary>
        internal static Func<Player, Skill> HighestMeleeSource = p => p.GetHighestMeleeSkill();

        /// <summary>
        /// Resolves a hit for an already-classified interaction. Inactive (default) unless the pair is an Arena or
        /// Battleground interaction AND <paramref name="ruleDials"/> has the master switch on AND the hit has a
        /// category. The context dials are read through <see cref="PvpContextTunables.DialSource"/> only then, so a
        /// PvE, open-world or switched-off hit reads no context key.
        /// </summary>
        public static PvpContextHit Resolve(PvpInteraction interaction, PvpRuleDials ruleDials, PvpHitProfile profile, bool includeVariants = true)
        {
            if (!profile.IsKnown || !interaction.IsPvp || ruleDials == null || !ruleDials.Enabled)
                return default;

            var context = ContextOf(interaction.Scope);

            if (context == null)
                return default;

            PvpCombatCategory category;
            var shape = PvpWarShape.None;

            if (profile.IsSpell)
            {
                category = SpellCategory(profile.School);

                if (category == PvpCombatCategory.War)
                    shape = SpellShape(profile.SpellType);
            }
            else
            {
                var weapon = profile.Weapon;
                var hasWeapon = weapon != null;

                var weaponSkill = hasWeapon ? interaction.Attacker.ConvertToMoASkill(weapon.WeaponSkill) : Skill.None;
                var weaponType = hasWeapon ? weapon.W_WeaponType : WeaponType.Undef;
                var highestMelee = !hasWeapon && !profile.IsMissile ? HighestMeleeSource(interaction.Attacker) : Skill.None;

                category = WeaponCategory(hasWeapon, weaponSkill, weaponType, profile.IsMissile, highestMelee);
            }

            if (category == PvpCombatCategory.None)
                return default;

            // variants are item properties, not settings; only a physical hit has any, and only the damage path
            // (M1/M2) uses them: the crit-chance path (CC1/CC2) passes includeVariants false and never reads them
            var variants = includeVariants && !profile.IsSpell ? VariantsOf(profile) : PvpWeaponVariant.None;

            return new PvpContextHit(context.Value, category, shape, PvpContextTunables.Read(), variants);
        }

        /// <summary>
        /// CHOKE-POINT ENTRY for CC1 (DamageEvent, after the crit chance is computed and BEFORE the logout always-crit
        /// override) and CC2 (SpellProjectile.CalculateDamage, at the crit roll's call site): classifies first, reads the dials only for
        /// an Arena / Battleground pair, then scales <paramref name="chance"/> by the (context, category) crit_chance
        /// key, clamped to [0, 1]. Anything else hands the chance back unchanged. Reported only when it changed.
        /// </summary>
        public static float ApplyCritChance(PvpChokePoint point, WorldObject source, Creature target, PvpHitProfile profile, float chance)
        {
            var interaction = PvpClassifier.Classify(source, target);

            if (!interaction.IsPvp || !interaction.IsMatchScope)
                return chance;

            var hit = Resolve(interaction, PvpRules.ReadDials(), profile, includeVariants: false);

            if (!hit.IsActive)
                return chance;

            var scaled = (float)ScaleCritChance(chance, CritChanceMod(hit));

            if (scaled == chance)
                return chance;

            PvpRules.Report(point, chance, scaled);

            return scaled;
        }
    }

    /// <summary>
    /// Test seam for the context tuning keys, mirroring <see cref="PvpRuleTunables"/>: PropertyManager.Get* throws in
    /// ACE.Server.Tests for any uncached key, so the whole read is wrapped and falls back to
    /// <see cref="PvpContextDials.Neutral"/> - the same 1.0 every key is registered with. NEVER call it before the
    /// interaction is classified: <see cref="PvpContextTuning.Resolve"/> is the only caller on a hit path.
    /// </summary>
    public static class PvpContextTunables
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(PvpContextTunables));

        private static readonly Func<PvpContextDials> DefaultSource = ReadFromProperties;

        /// <summary>
        /// The dials source. Left at its default, <see cref="Read"/> serves a cached snapshot (see
        /// <see cref="Read"/>); swapping it for anything else (the test seam) bypasses the cache entirely, and
        /// restoring the saved default turns the cache back on.
        /// </summary>
        public static Func<PvpContextDials> DialSource = DefaultSource;

        /// <summary>Seam for the per-key reads of the default source; tests swap it to count reads. Default: PropertyManager.</summary>
        internal static Func<string, double> PropertyReader = key => PropertyManager.GetDouble(key, PvpContextTuning.NeutralValue).Item;

        /// <summary>Seam for the "have any doubles changed" counter the cache is keyed on. Default: <see cref="PropertyManager.DoubleSettingsEpoch"/>.</summary>
        internal static Func<long> EpochSource = () => PropertyManager.DoubleSettingsEpoch;

        private sealed class Snapshot
        {
            public PvpContextDials Dials;
            public long Epoch;
        }

        private static volatile Snapshot cached;

        /// <summary>Drops the cached snapshot so the next <see cref="Read"/> re-reads every key.</summary>
        public static void Invalidate() => cached = null;

        /// <summary>
        /// The resolved snapshot. Through the default source it is cached and re-read only when invalidated: by
        /// <see cref="OnPropertyModified"/>, or because <see cref="PropertyManager.DoubleSettingsEpoch"/> moved
        /// (ModifyDouble from any path, and the DB reload that /resyncproperties and the periodic worker run, which
        /// can change a context key with no /modify* call). A failed read is never cached. Through a swapped
        /// <see cref="DialSource"/> it calls the source every time; a missing, null or throwing source is neutral.
        /// </summary>
        public static PvpContextDials Read()
        {
            try
            {
                var source = DialSource;

                if (!ReferenceEquals(source, DefaultSource))
                    return source?.Invoke() ?? PvpContextDials.Neutral;

                var epoch = EpochSource();
                var snapshot = cached;

                if (snapshot != null && snapshot.Epoch == epoch)
                    return snapshot.Dials;

                var fresh = source();

                if (fresh == null)
                    return PvpContextDials.Neutral;

                // epoch captured BEFORE the reads: a change racing the reads leaves the entry stale-marked, so the next hit refreshes
                cached = new Snapshot { Dials = fresh, Epoch = epoch };

                return fresh;
            }
            catch (Exception ex)
            {
                log.Error("[PVP] PvpContextTunables.DialSource threw; using neutral context tuning", ex);
                return PvpContextDials.Neutral;
            }
        }

        /// <summary>Reads every context key; null (never cached) when the property store is unreadable.</summary>
        private static PvpContextDials ReadFromProperties()
        {
            try
            {
                var values = new double[PvpContextTuning.Keys.Count];

                for (var i = 0; i < values.Length; i++)
                    values[i] = PropertyReader(PvpContextTuning.Keys[i]);

                return new PvpContextDials(values);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not read the pvp_ context tuning keys; falling back to neutral", ex);
                return null;
            }
        }
        /// <summary>`[PVP] context tuning key=value ... reason=&lt;reason&gt;` listing only the keys that differ from neutral.</summary>
        public static string FormatLogLine(PvpContextDials dials, string reason)
        {
            var rows = (dials ?? PvpContextDials.Neutral).NonDefault();
            var pairs = rows.Count == 0 ? "(all neutral)" : string.Join(" ", rows.Select(r => $"{r.Key}={r.Value}"));

            return string.IsNullOrEmpty(reason) ? $"[PVP] context tuning {pairs}" : $"[PVP] context tuning {pairs} reason={reason}";
        }

        /// <summary>Called (via PvpRuleTunables.OnPropertyModified) after a successful /modify* of any key. Never throws.</summary>
        public static void OnPropertyModified(string key)
        {
            if (!PvpContextTuning.IsContextKey(key))
                return;

            Invalidate();

            try
            {
                log.Info(FormatLogLine(Read(), "modify:" + key));
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not log the resolved pvp_ context tuning keys", ex);
            }
        }
    }
}
