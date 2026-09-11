using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.Entity
{
    /// <summary>
    /// How badly a caster monster wants to cast one spell against the target it is currently facing.
    /// Ascending value is descending desirability, so the tiers can be walked with a plain int loop.
    /// </summary>
    public enum MonsterSpellPriority
    {
        /// <summary>
        /// The spell does something the target is not already carrying: a Vulnerability it lacks, an
        /// Imperil it lacks, or elemental damage of a type it IS already vulnerable to.
        /// </summary>
        Preferred = 0,

        /// <summary>
        /// The spell is neither obviously wasted nor obviously good right now. Everything whose shape
        /// this selector does not model lands here, which is the point: an unmodelled spell keeps
        /// exactly the standing it had before this selector existed.
        /// </summary>
        Neutral = 1,

        /// <summary>
        /// The target already carries this debuff at equal or greater strength, so casting it again
        /// buys nothing. Demoted, NEVER removed - see <see cref="TrySelect"/>.
        /// </summary>
        Redundant = 2,
    }

    /// <summary>
    /// A snapshot of the debuffs already standing on one target, taken ONCE per spell roll.
    ///
    /// The seven floats are the multiplicative resistance modifiers contributed by Vulnerability
    /// enchantments alone (EnchantmentManager.GetVulnerabilityResistanceMod), so 1.0 means "no
    /// Vulnerability of that element is up" and larger means a stronger one is. Protection is
    /// deliberately NOT folded in: this answers "has my debuff landed", not "how tough is this target".
    ///
    /// <see cref="BodyArmorMod"/> is the NET body-armor modifier (EnchantmentManager.GetBodyArmorMod),
    /// so a target carrying both Armor Self and Imperil can read as non-negative and be treated as
    /// un-imperiled. That under-detection is the safe direction: it falls back to casting the debuff,
    /// which is what the server did before this existed. Over-detection would suppress a debuff that
    /// had not actually landed.
    /// </summary>
    public readonly struct VulnerabilityProfile
    {
        public float ResistSlash { get; }
        public float ResistPierce { get; }
        public float ResistBludgeon { get; }
        public float ResistCold { get; }
        public float ResistFire { get; }
        public float ResistAcid { get; }
        public float ResistElectric { get; }

        /// <summary>
        /// Net body-armor modifier from enchantments. Negative means an Imperil-class debuff is up.
        /// </summary>
        public int BodyArmorMod { get; }

        public VulnerabilityProfile(float resistSlash, float resistPierce, float resistBludgeon, float resistCold,
            float resistFire, float resistAcid, float resistElectric, int bodyArmorMod)
        {
            ResistSlash = resistSlash;
            ResistPierce = resistPierce;
            ResistBludgeon = resistBludgeon;
            ResistCold = resistCold;
            ResistFire = resistFire;
            ResistAcid = resistAcid;
            ResistElectric = resistElectric;
            BodyArmorMod = bodyArmorMod;
        }

        /// <summary>
        /// A target carrying nothing at all: every resistance at the 1.0 identity, no body-armor mod.
        /// Used whenever the target has no enchantments, so the common case does zero registry work.
        /// </summary>
        public static readonly VulnerabilityProfile None =
            new VulnerabilityProfile(1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 0);

        /// <summary>
        /// The standing Vulnerability multiplier for one damage type. Anything outside the seven
        /// elemental/physical resistances the profile models - Health, Stamina, Mana, Nether, Undef,
        /// or a combined multi-bit DamageType - returns the 1.0 identity, which classifies Neutral.
        /// </summary>
        public float GetMod(DamageType damageType)
        {
            switch (damageType)
            {
                case DamageType.Slash:
                    return ResistSlash;
                case DamageType.Pierce:
                    return ResistPierce;
                case DamageType.Bludgeon:
                    return ResistBludgeon;
                case DamageType.Cold:
                    return ResistCold;
                case DamageType.Fire:
                    return ResistFire;
                case DamageType.Acid:
                    return ResistAcid;
                case DamageType.Electric:
                    return ResistElectric;
                default:
                    return 1.0f;
            }
        }
    }

    /// <summary>
    /// The spell-intrinsic facts this selector needs, reduced to a value type so the classification is
    /// pure arithmetic over a struct rather than a lookup against a live <see cref="Spell"/>.
    /// Every field is a property of the spell alone, never of a caster or a target, which is what makes
    /// <see cref="MonsterSpellShapeCache"/> safe to key on spell id globally.
    /// </summary>
    public readonly struct MonsterSpellShape
    {
        /// <summary>
        /// The spell's own damage element, for a spell that is neither a Vulnerability nor an Imperil.
        /// DamageType.Undef for the debuff shapes and for anything with no element (Harm, Nether drains).
        /// </summary>
        public DamageType ElementalDamageType { get; }

        /// <summary>
        /// The element this spell makes the target vulnerable TO, or DamageType.Undef if the spell is
        /// not an elemental Vulnerability.
        /// </summary>
        public DamageType VulnElement { get; }

        /// <summary>
        /// The multiplicative resistance value the Vulnerability would apply (2.5 for Fire Vulnerability
        /// Other VI). Zero when the spell is not a Vulnerability.
        /// </summary>
        public float VulnStatModVal { get; }

        /// <summary>
        /// TRUE for the Imperil class: an additive body-armor mod below the 0.0 identity.
        /// </summary>
        public bool IsBodyArmorDebuff { get; }

        public MonsterSpellShape(DamageType elementalDamageType, DamageType vulnElement, float vulnStatModVal,
            bool isBodyArmorDebuff)
        {
            ElementalDamageType = elementalDamageType;
            VulnElement = vulnElement;
            VulnStatModVal = vulnStatModVal;
            IsBodyArmorDebuff = isBodyArmorDebuff;
        }

        /// <summary>
        /// TRUE when this spell is an elemental Vulnerability.
        /// </summary>
        public bool IsVulnerability => VulnElement != DamageType.Undef;

        /// <summary>
        /// Reduces a spell's raw stat-mod fields to a shape.
        ///
        /// The two debuff predicates are a deliberate COPY of MaledictionAbility.AffectsSpell, minus its
        /// Life Magic school gate (a monster caring whether the target is already vulnerable does not
        /// care which school produced the Vulnerability, and Expose Weakness debuffs body armor exactly
        /// like Imperil does). Keeping them shape-derived rather than id-derived is what makes them
        /// survive new content, and the sign tests are what separate a debuff from its buff counterpart:
        /// Fire Vulnerability Other VI and Fire Protection Other VI are BOTH stat_Mod_Type 20488 on
        /// stat_Mod_Key 67, differing only in stat_Mod_Val (2.5 against 0.4).
        ///
        /// Anything matching neither predicate keeps its own damage element, which is how a damaging
        /// spell gets promoted when the matching Vulnerability is already up. A spell with no element
        /// at all (Harm, DamageType.Undef) falls through with Undef and classifies Neutral.
        /// </summary>
        public static MonsterSpellShape FromSpellFields(EnchantmentTypeFlags statModType, uint statModKey,
            float statModVal, DamageType damageType)
        {
            // elemental Vulnerability: multiplicative resistance mod above 1.0 (Protection sits below it)
            if (statModType.HasFlag(EnchantmentTypeFlags.Float) &&
                statModType.HasFlag(EnchantmentTypeFlags.Multiplicative) &&
                statModKey >= (uint)PropertyFloat.ResistSlash &&
                statModKey <= (uint)PropertyFloat.ResistElectric &&
                statModVal > 1.0f)
            {
                return new MonsterSpellShape(DamageType.Undef, ResistKeyToDamageType(statModKey), statModVal, false);
            }

            // Imperil: additive body-armor mod below 0.0 (the Armor buff line sits above it)
            if (statModType.HasFlag(EnchantmentTypeFlags.BodyArmorValue) &&
                statModType.HasFlag(EnchantmentTypeFlags.Additive) &&
                statModVal < 0.0f)
            {
                return new MonsterSpellShape(DamageType.Undef, DamageType.Undef, 0.0f, true);
            }

            return new MonsterSpellShape(damageType, DamageType.Undef, 0.0f, false);
        }

        /// <summary>
        /// Maps a PropertyFloat resistance key (64-70) back to its DamageType. This is the inverse of
        /// EnchantmentManager.GetResistanceKey and mirrors it case for case, so a target's stored
        /// Vulnerability modifier and a candidate spell's element are compared on the same axis.
        /// Nether is deliberately absent: PropertyFloat.ResistNether sits outside the 64-70 window the
        /// Vulnerability predicate accepts, so this is never reached with it.
        /// </summary>
        private static DamageType ResistKeyToDamageType(uint statModKey)
        {
            switch ((PropertyFloat)statModKey)
            {
                case PropertyFloat.ResistSlash:
                    return DamageType.Slash;
                case PropertyFloat.ResistPierce:
                    return DamageType.Pierce;
                case PropertyFloat.ResistBludgeon:
                    return DamageType.Bludgeon;
                case PropertyFloat.ResistCold:
                    return DamageType.Cold;
                case PropertyFloat.ResistFire:
                    return DamageType.Fire;
                case PropertyFloat.ResistAcid:
                    return DamageType.Acid;
                case PropertyFloat.ResistElectric:
                    return DamageType.Electric;
                default:
                    return DamageType.Undef;
            }
        }
    }

    /// <summary>
    /// Target-aware spell choice for caster monsters.
    ///
    /// THE CENTRAL INVARIANT: this changes WHICH spell a monster casts, never HOW OFTEN it casts.
    /// The pre-existing roll is an independent Bernoulli trial per book entry, so the chance of casting
    /// nothing at all is prod(1 - p_i) over the whole book - a product, and therefore ORDER-INDEPENDENT.
    /// Reordering the book leaves the cast rate mathematically identical while changing the winner.
    /// REMOVING a candidate does not: it lowers P(cast at all), which would quietly turn vuln-heavy
    /// casters into melee mobs and is a combat-balance regression on every casting monster in the game.
    /// So <see cref="TrySelect"/> only ever reorders. It never drops a candidate, not even one every
    /// rule here calls useless, and a fully-debuffed caster facing a fully-debuffed target still probes
    /// its whole book and still casts at exactly the rate it did before.
    ///
    /// The type is deliberately free of Creature, Player, DatManager, DatabaseManager and
    /// PropertyManager so the whole decision is unit-testable with no live objects: the caller supplies
    /// the book, a shape lookup, the target snapshot and the random source.
    /// </summary>
    public static class MonsterSpellSelector
    {
        /// <summary>
        /// The compile-time default for the monster_conditional_spell_selection tunable: on. False
        /// reproduces the previous behaviour exactly - a single flat pass over the book in its own
        /// order. Held as a const so PropertyManager's registration and this class cannot drift apart,
        /// the same contract DungeonSpellFilter.DefaultStripAoeSpellsEnabled uses.
        /// </summary>
        public const bool DefaultConditionalSelectionEnabled = true;

        /// <summary>
        /// Rates one candidate spell against one target snapshot.
        ///
        /// Vulnerability: no Vulnerability of that element standing (mod at or below the 1.0 identity)
        /// is a gap worth filling; one already standing at equal or greater strength is wasted; one
        /// standing at LESS than this spell would apply is a genuine upgrade, but a weaker want than
        /// filling an empty slot, so it sits at Neutral.
        ///
        /// Imperil: net body armor already negative means one is up.
        ///
        /// Damage: promoted when the target is already vulnerable to that element, which is the second
        /// half of the owner's ask - land the vuln, then exploit it.
        ///
        /// Everything else is Neutral. Never Preferred, never Redundant, and never an exception.
        /// </summary>
        public static MonsterSpellPriority Classify(in MonsterSpellShape shape, in VulnerabilityProfile profile)
        {
            if (shape.IsVulnerability)
            {
                var standing = profile.GetMod(shape.VulnElement);

                if (standing <= 1.0f)
                    return MonsterSpellPriority.Preferred;

                if (standing >= shape.VulnStatModVal)
                    return MonsterSpellPriority.Redundant;

                return MonsterSpellPriority.Neutral;
            }

            if (shape.IsBodyArmorDebuff)
            {
                return profile.BodyArmorMod < 0
                    ? MonsterSpellPriority.Redundant
                    : MonsterSpellPriority.Preferred;
            }

            if (profile.GetMod(shape.ElementalDamageType) > 1.0f)
                return MonsterSpellPriority.Preferred;

            return MonsterSpellPriority.Neutral;
        }

        /// <summary>
        /// Walks the book three times, once per tier in ascending priority order, running the original
        /// per-entry Bernoulli trial on every entry belonging to the current tier. The first entry whose
        /// trial succeeds wins.
        ///
        /// Because <see cref="Classify"/> is a pure function of a fixed shape and a fixed profile, each
        /// book entry belongs to exactly one tier, so across the three passes every entry is rolled
        /// EXACTLY ONCE, with the probability it always had. That is the whole frequency-neutrality
        /// argument, and it is why <paramref name="profile"/> must be computed once by the caller and
        /// held constant for the duration of this call: a profile re-read between passes could put one
        /// entry in two tiers (rolled twice, raising P(cast)) or in none (never rolled, lowering it).
        ///
        /// <paramref name="book"/> is enumerated three times, so it must be a stable collection rather
        /// than a lazy one-shot sequence. Order within a tier is book order.
        ///
        /// ALLOCATION NOTE, considered and accepted. Biota.PropertiesSpellBook is declared as the
        /// INTERFACE IDictionary&lt;int, float&gt;, so foreach binds through it and boxes Dictionary's struct
        /// enumerator. The single-pass roll this replaces boxed one per call; three passes box up to
        /// three, and TryRollSpell can run ten times in a tick, so the delta is a couple of dozen small
        /// Gen0 objects per bad-line-of-sight tick per caster. Removing it would mean either a concrete
        /// Dictionary-typed duplicate of this loop selected by a type test at the call site, or folding
        /// the three passes into one - the first duplicates the exact code the frequency-neutrality
        /// argument rests on, and the second is a different algorithm. Neither is worth it at this size;
        /// the Spell allocated on a successful roll is far larger than everything saved.
        /// </summary>
        /// <param name="book">Spell id to the raw spell-book probability, base 2.0 as ace_world stores it.</param>
        /// <param name="shapeLookup">Resolves a spell id to its shape. Expected to be memoized by the caller.</param>
        /// <param name="profile">The target snapshot, computed once before the first pass.</param>
        /// <param name="rng">
        /// Draws a uniform value in [0, 1). Called at most once per book entry. Typed double rather than
        /// float on purpose: the caller feeds it ThreadSafeRandom.Next(0.0f, 1.0f), which RETURNS a
        /// double, and the original roll compared that double against the float probability directly.
        /// Narrowing the draw to float here would re-round it and could flip the comparison at a
        /// boundary, which is a behaviour change however rare.
        /// </param>
        /// <param name="spellId">The chosen spell id, or 0 when nothing was chosen.</param>
        public static bool TrySelect(IEnumerable<KeyValuePair<int, float>> book, Func<int, MonsterSpellShape> shapeLookup,
            in VulnerabilityProfile profile, Func<double> rng, out int spellId)
        {
            spellId = 0;

            if (book == null || shapeLookup == null || rng == null)
                return false;

            for (var tier = MonsterSpellPriority.Preferred; tier <= MonsterSpellPriority.Redundant; tier++)
            {
                foreach (var spell in book)
                {
                    // Classify is re-run per pass rather than cached into a list: the shape lookup is a
                    // memoized dictionary hit, and this path runs up to ten times per tick per monster,
                    // so allocating a per-roll bucket structure would cost far more than it saves.
                    if (Classify(shapeLookup(spell.Key), profile) != tier)
                        continue;

                    // monster spellbooks have probabilities with base 2.0
                    // ie. a 5% chance would be 2.05 instead of 0.05
                    // carried verbatim from the original Creature.TryRollSpell loop, and the only copy
                    // of this expression in the new code.
                    var probability = spell.Value > 2.0f ? spell.Value - 2.0f : spell.Value / 100.0f;

                    if (rng() < probability)
                    {
                        spellId = spell.Key;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
