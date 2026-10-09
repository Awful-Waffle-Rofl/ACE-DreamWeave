using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

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
        /// exactly the standing it had before this selector existed. A debuff that would UPGRADE a
        /// weaker one already standing in its own category also lands here.
        /// </summary>
        Neutral = 1,

        /// <summary>
        /// The target already carries this debuff, in the same spell category, at equal or greater
        /// strength, so casting it again buys nothing. Skipped by <see cref="MonsterSpellSelector.TrySelect"/>,
        /// with its share of the cast chance handed to the surviving entries.
        /// </summary>
        Redundant = 2,
    }

    /// <summary>
    /// The top-layer enchantment one spell category holds on a target: the one the engine actually applies.
    /// Copied out of the registry so a classification is pure arithmetic over values, never a read of a
    /// mutable registry entry.
    /// </summary>
    public readonly struct CarriedEnchantment
    {
        public EnchantmentTypeFlags StatModType { get; }
        public uint StatModKey { get; }
        public float StatModValue { get; }
        public uint PowerLevel { get; }
        public double StartTime { get; }

        public CarriedEnchantment(EnchantmentTypeFlags statModType, uint statModKey, float statModValue, uint powerLevel = 0, double startTime = 0.0)
        {
            StatModType = statModType;
            StatModKey = statModKey;
            StatModValue = statModValue;
            PowerLevel = powerLevel;
            StartTime = startTime;
        }
    }

    /// <summary>
    /// A snapshot of the debuffs already standing on one target, taken ONCE per spell roll.
    ///
    /// The seven floats are the multiplicative resistance modifiers contributed by Vulnerability
    /// enchantments alone (EnchantmentManager.GetVulnerabilityResistanceMod), so 1.0 means "no
    /// Vulnerability of that element is up" and larger means a stronger one is. Protection is
    /// deliberately NOT folded in: this answers "has my debuff landed", not "how tough is this target".
    ///
    /// <see cref="NegativeBodyArmorMod"/> is the NEGATIVE-ONLY body-armor modifier
    /// (EnchantmentManager.GetBodyArmorMod(false)). It used to be the NET modifier, and Armor Self VIII
    /// (+250) then cancelled a standing Imperil (-225), so a buffed player read as un-imperiled and the
    /// monster promoted Imperil to the front of its book on every roll.
    ///
    /// <see cref="TopLayers"/> is the top-layer entry of every spell category on the target, which is the
    /// stacking unit the engine itself uses (GetEnchantmentsTopLayerByStatModType groups by SpellCategory
    /// and applies only the highest PowerLevel in each). It is what decides Redundant: a debuff is wasted
    /// only when its OWN category already holds an entry at least as strong. An entry in a different
    /// category stacks with it in the engine, so it never makes a candidate Redundant - which is what keeps
    /// Hunter's Mark (its own class-ability category) and any other intentionally-stacking debuff out.
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
        /// Sum of the NEGATIVE body-armor enchantments only. Below zero means an Imperil-class debuff is up,
        /// whatever Armor Self the target also carries.
        /// </summary>
        public int NegativeBodyArmorMod { get; }

        /// <summary>
        /// Top-layer entry per spell category, or null for a target with no enchantments.
        /// </summary>
        public IReadOnlyDictionary<SpellCategory, CarriedEnchantment> TopLayers { get; }

        public VulnerabilityProfile(float resistSlash, float resistPierce, float resistBludgeon, float resistCold,
            float resistFire, float resistAcid, float resistElectric, int negativeBodyArmorMod,
            IReadOnlyDictionary<SpellCategory, CarriedEnchantment> topLayers = null)
        {
            ResistSlash = resistSlash;
            ResistPierce = resistPierce;
            ResistBludgeon = resistBludgeon;
            ResistCold = resistCold;
            ResistFire = resistFire;
            ResistAcid = resistAcid;
            ResistElectric = resistElectric;
            NegativeBodyArmorMod = negativeBodyArmorMod;
            TopLayers = topLayers;
        }

        /// <summary>
        /// A target carrying nothing at all: every resistance at the 1.0 identity, no body-armor mod, no layers.
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

        public bool TryGetTopLayer(SpellCategory category, out CarriedEnchantment carried)
        {
            if (TopLayers != null && category != SpellCategory.Undef)
                return TopLayers.TryGetValue(category, out carried);

            carried = default;
            return false;
        }

        /// <summary>
        /// Reduces a registry snapshot to its top layer per spell category, with the engine's own ordering:
        /// highest PowerLevel first, then the most recently cast (StartTime counts DOWN from 0, so the larger
        /// StartTime is the newer entry). Cooldown entries are skipped; they share one synthetic category and
        /// carry no stat. Pure, so it is tested without a live Creature.
        /// </summary>
        public static IReadOnlyDictionary<SpellCategory, CarriedEnchantment> BuildTopLayers(IEnumerable<PropertiesEnchantmentRegistry> entries)
        {
            if (entries == null)
                return null;

            Dictionary<SpellCategory, CarriedEnchantment> result = null;

            foreach (var e in entries)
            {
                if (e == null || (e.StatModType & EnchantmentTypeFlags.Cooldown) != 0)
                    continue;

                if (result == null)
                    result = new Dictionary<SpellCategory, CarriedEnchantment>();

                if (result.TryGetValue(e.SpellCategory, out var current) &&
                    (current.PowerLevel > e.PowerLevel || (current.PowerLevel == e.PowerLevel && current.StartTime >= e.StartTime)))
                {
                    continue;
                }

                result[e.SpellCategory] = new CarriedEnchantment(e.StatModType, e.StatModKey, e.StatModValue, e.PowerLevel, e.StartTime);
            }

            return result;
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
        /// The bits of EnchantmentTypeFlags that name WHAT a stat mod touches. Two entries compare as the same
        /// stat only when these bits, the additive/multiplicative bit and the key all agree.
        /// </summary>
        private const EnchantmentTypeFlags StatKindMask = EnchantmentTypeFlags.StatTypes;

        private const EnchantmentTypeFlags AttributeSkillOrVital =
            EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.SecondAtt | EnchantmentTypeFlags.Skill;

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

        /// <summary>
        /// TRUE for a non-beneficial attribute, vital or skill mod pointing the harmful way: additive below 0,
        /// or multiplicative below 1. Weakness, Slowness, Feeblemind, Clumsiness, Defenselessness, Magic Yield.
        /// </summary>
        public bool IsAttributeSkillOrVitalDebuff { get; }

        /// <summary>
        /// The spell's stacking group. SpellCategory.Undef disables the same-category Redundant check.
        /// </summary>
        public SpellCategory Category { get; }

        public EnchantmentTypeFlags StatModType { get; }
        public uint StatModKey { get; }
        public float StatModVal { get; }

        public MonsterSpellShape(DamageType elementalDamageType, DamageType vulnElement, float vulnStatModVal,
            bool isBodyArmorDebuff, bool isAttributeSkillOrVitalDebuff = false, SpellCategory category = SpellCategory.Undef,
            EnchantmentTypeFlags statModType = EnchantmentTypeFlags.Undef, uint statModKey = 0, float statModVal = 0.0f)
        {
            ElementalDamageType = elementalDamageType;
            VulnElement = vulnElement;
            VulnStatModVal = vulnStatModVal;
            IsBodyArmorDebuff = isBodyArmorDebuff;
            IsAttributeSkillOrVitalDebuff = isAttributeSkillOrVitalDebuff;
            Category = category;
            StatModType = statModType;
            StatModKey = statModKey;
            StatModVal = statModVal;
        }

        /// <summary>
        /// TRUE when this spell is an elemental Vulnerability.
        /// </summary>
        public bool IsVulnerability => VulnElement != DamageType.Undef;

        /// <summary>
        /// TRUE for every shape the same-category Redundant check applies to.
        /// </summary>
        public bool IsDebuff => IsVulnerability || IsBodyArmorDebuff || IsAttributeSkillOrVitalDebuff;

        /// <summary>
        /// Reduces a spell's raw stat-mod fields to a shape.
        ///
        /// The Vulnerability and Imperil predicates are a deliberate COPY of MaledictionAbility.AffectsSpell,
        /// minus its Life Magic school gate (a monster caring whether the target is already vulnerable does
        /// not care which school produced the Vulnerability, and Expose Weakness debuffs body armor exactly
        /// like Imperil does). Keeping them shape-derived rather than id-derived is what makes them survive
        /// new content, and the sign tests are what separate a debuff from its buff counterpart: Fire
        /// Vulnerability Other VI and Fire Protection Other VI are BOTH stat_Mod_Type 20488 on stat_Mod_Key 67,
        /// differing only in stat_Mod_Val (2.5 against 0.4).
        ///
        /// The attribute/skill/vital predicate follows the same sign rule, and also refuses anything carrying
        /// the Beneficial flag, so Strength Self and its Weakness Other counterpart cannot be confused.
        ///
        /// Anything matching no predicate keeps its own damage element, which is how a damaging spell gets
        /// promoted when the matching Vulnerability is already up. A spell with no element at all (Harm,
        /// DamageType.Undef) falls through with Undef and classifies Neutral.
        /// </summary>
        public static MonsterSpellShape FromSpellFields(EnchantmentTypeFlags statModType, uint statModKey,
            float statModVal, DamageType damageType, SpellCategory category = SpellCategory.Undef)
        {
            // elemental Vulnerability: multiplicative resistance mod above 1.0 (Protection sits below it)
            if (statModType.HasFlag(EnchantmentTypeFlags.Float) &&
                statModType.HasFlag(EnchantmentTypeFlags.Multiplicative) &&
                statModKey >= (uint)PropertyFloat.ResistSlash &&
                statModKey <= (uint)PropertyFloat.ResistElectric &&
                statModVal > 1.0f)
            {
                return new MonsterSpellShape(DamageType.Undef, ResistKeyToDamageType(statModKey), statModVal, false,
                    false, category, statModType, statModKey, statModVal);
            }

            // Imperil: additive body-armor mod below 0.0 (the Armor buff line sits above it)
            if (statModType.HasFlag(EnchantmentTypeFlags.BodyArmorValue) &&
                statModType.HasFlag(EnchantmentTypeFlags.Additive) &&
                statModVal < 0.0f)
            {
                return new MonsterSpellShape(DamageType.Undef, DamageType.Undef, 0.0f, true,
                    false, category, statModType, statModKey, statModVal);
            }

            // attribute / vital / skill debuff
            if ((statModType & AttributeSkillOrVital) != 0 &&
                (statModType & (EnchantmentTypeFlags.Beneficial | EnchantmentTypeFlags.Vitae | EnchantmentTypeFlags.Cooldown)) == 0 &&
                ((statModType.HasFlag(EnchantmentTypeFlags.Additive) && statModVal < 0.0f) ||
                 (statModType.HasFlag(EnchantmentTypeFlags.Multiplicative) && statModVal > 0.0f && statModVal < 1.0f)))
            {
                return new MonsterSpellShape(DamageType.Undef, DamageType.Undef, 0.0f, false,
                    true, category, statModType, statModKey, statModVal);
            }

            return new MonsterSpellShape(damageType, DamageType.Undef, 0.0f, false);
        }

        /// <summary>
        /// TRUE when <paramref name="carried"/> is the same stat as this spell (same stat-kind bits, same
        /// additive/multiplicative bit, same key) pushed the same direction at least as far.
        /// "As far" is distance from the identity (0 additive, 1 multiplicative), so it works for Imperil
        /// (-225 covers -200), Vulnerability (2.85 covers 2.5) and multiplicative attribute shrinks alike.
        /// </summary>
        public bool IsCoveredBy(in CarriedEnchantment carried)
        {
            if ((carried.StatModType & StatKindMask) != (StatModType & StatKindMask))
                return false;

            var multiplicative = StatModType.HasFlag(EnchantmentTypeFlags.Multiplicative);

            if (carried.StatModType.HasFlag(EnchantmentTypeFlags.Multiplicative) != multiplicative ||
                carried.StatModType.HasFlag(EnchantmentTypeFlags.Additive) != StatModType.HasFlag(EnchantmentTypeFlags.Additive))
                return false;

            if (carried.StatModKey != StatModKey)
                return false;

            var identity = multiplicative ? 1.0f : 0.0f;
            var want = StatModVal - identity;
            var have = carried.StatModValue - identity;

            if (want == 0.0f)
                return false;

            // same direction, and at least as far from the identity (tiny epsilon for float storage)
            return want > 0.0f
                ? have >= want - 1e-4f
                : have <= want + 1e-4f;
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
    /// The original roll is an independent Bernoulli trial per book entry, so P(cast at all) is
    /// 1 - prod(1 - p_i) over the whole book.
    ///
    /// #1042 kept that invariant by only REORDERING the book, and that turned out to buy almost nothing:
    /// real spell books carry ~5% per entry, so moving a wasted debuff to the back of the queue barely
    /// lowers how often it wins. A target already carrying every debuff kept eating repeat casts at close to
    /// the old rate (owner report 2026-10-06).
    ///
    /// So a Redundant entry is now SKIPPED, and its share of the cast chance is handed to the survivors by
    /// a uniform hazard scale: each survivor's miss chance (1 - p) is raised to one common exponent a >= 1,
    /// chosen so prod over survivors of (1 - p_i') equals prod over the whole book of (1 - p_i). The cast
    /// rate is exactly unchanged; the casts simply go to spells that do something. A uniform hazard scale is
    /// the same as each survivor taking the same multiple of "rolls", it has a closed form
    /// (a = ln Q_all / ln Q_survivors), and it can never push a probability past 1.
    ///
    /// One case has no finite exponent: a GUARANTEED entry (p >= 1) is Redundant and no survivor is
    /// guaranteed, so the book always cast and the survivors must now always cast too. Raising every
    /// survivor to p = 1 would hand 100% of casts to whichever survivor rolls first. Instead the cast is
    /// shared out in proportion to the survivors' ORIGINAL chances, sequentially: in roll order, each live
    /// survivor hits with p_i / (sum of p_j over the live survivors not yet rolled), so the last one hits
    /// with 1.0 and each survivor's overall share is exactly p_i / (sum of all live survivors' p).
    ///
    /// Edge cases: a book with no Redundant entry rolls with its original probabilities, untouched. A book
    /// that is ALL Redundant, or whose survivors all have zero chance (so no rescale can carry the rate),
    /// falls back to the original single flat pass over the whole book - today's roll, at today's rate.
    ///
    /// The type is deliberately free of Creature, Player, DatManager, DatabaseManager and
    /// PropertyManager so the whole decision is unit-testable with no live objects: the caller supplies
    /// the book, a shape lookup, the target snapshot and the random source.
    /// </summary>
    public static class MonsterSpellSelector
    {
        /// <summary>
        /// The compile-time default for the monster_conditional_spell_selection tunable: on. False
        /// reproduces the original behaviour exactly - a single flat pass over the book in its own
        /// order. Held as a const so PropertyManager's registration and this class cannot drift apart,
        /// the same contract DungeonSpellFilter.DefaultStripAoeSpellsEnabled uses.
        /// </summary>
        public const bool DefaultConditionalSelectionEnabled = true;

        /// <summary>
        /// Rates one candidate spell against one target snapshot.
        ///
        /// Redundant first, for every debuff shape: the spell's OWN category already holds a top-layer entry
        /// of the same stat at least as strong. Only the same category counts, because that is where the
        /// engine stacks; anything in another category adds on top of it and is not wasted.
        ///
        /// Vulnerability: no Vulnerability of that element standing at all is a gap worth filling
        /// (Preferred); one standing elsewhere or weaker in its own category is Neutral.
        ///
        /// Imperil: no negative body armor standing is Preferred; otherwise Neutral. Read from the
        /// NEGATIVE-only modifier, so Armor Self can no longer make a standing Imperil look absent.
        ///
        /// Attribute / skill / vital debuff: Neutral unless Redundant. They were Neutral before this existed
        /// and are not promoted, so a fresh target sees the same priorities it always did.
        ///
        /// Damage: promoted when the target is already vulnerable to that element.
        ///
        /// Everything else is Neutral. Never an exception.
        /// </summary>
        public static MonsterSpellPriority Classify(in MonsterSpellShape shape, in VulnerabilityProfile profile)
        {
            if (shape.IsDebuff && profile.TryGetTopLayer(shape.Category, out var carried) && shape.IsCoveredBy(carried))
                return MonsterSpellPriority.Redundant;

            if (shape.IsVulnerability)
            {
                return profile.GetMod(shape.VulnElement) <= 1.0f
                    ? MonsterSpellPriority.Preferred
                    : MonsterSpellPriority.Neutral;
            }

            if (shape.IsBodyArmorDebuff)
            {
                return profile.NegativeBodyArmorMod < 0
                    ? MonsterSpellPriority.Neutral
                    : MonsterSpellPriority.Preferred;
            }

            if (shape.IsAttributeSkillOrVitalDebuff)
                return MonsterSpellPriority.Neutral;

            if (profile.GetMod(shape.ElementalDamageType) > 1.0f)
                return MonsterSpellPriority.Preferred;

            return MonsterSpellPriority.Neutral;
        }

        /// <summary>
        /// The per-entry chance encoded in a monster spell book value, carried verbatim from the original
        /// Creature.TryRollSpell loop: base 2.0, so 2.05 is 5%, while a raw value at or below 2.0 is a
        /// percentage (0.5 is 0.5%).
        /// </summary>
        public static float ToProbability(float bookValue)
        {
            return bookValue > 2.0f ? bookValue - 2.0f : bookValue / 100.0f;
        }

        private static double Clamp01(double p) => p < 0.0 ? 0.0 : (p > 1.0 ? 1.0 : p);

        /// <summary>
        /// Picks the spell to cast.
        ///
        /// One classification pass first, which counts the Redundant entries and accumulates the two miss
        /// products the rescale needs. Then:
        ///   - no Redundant entry: Preferred, then Neutral, each at its ORIGINAL float probability, compared
        ///     against the double draw exactly as the original loop did;
        ///   - every entry Redundant, or no survivor with a non-zero chance: the original flat pass;
        ///   - otherwise: Preferred, then Neutral, at the rescaled probability, Redundant never rolled.
        ///
        /// <paramref name="profile"/> must be computed once by the caller and held constant for the call:
        /// a profile re-read between passes could put one entry in two tiers or in none, and the rescale
        /// exponent would no longer match the entries actually rolled.
        ///
        /// <paramref name="book"/> is enumerated several times, so it must be a stable collection rather
        /// than a lazy one-shot sequence. Order within a tier is book order. Biota.PropertiesSpellBook is
        /// declared as IDictionary, so each foreach boxes one enumerator; accepted, the Spell allocated on a
        /// successful roll is far larger.
        /// </summary>
        /// <param name="book">Spell id to the raw spell-book probability, base 2.0 as ace_world stores it.</param>
        /// <param name="shapeLookup">Resolves a spell id to its shape. Expected to be memoized by the caller.</param>
        /// <param name="profile">The target snapshot, computed once before the first pass.</param>
        /// <param name="rng">
        /// Draws a uniform value in [0, 1). Called at most once per rolled entry. Typed double on purpose: the
        /// caller feeds it ThreadSafeRandom.Next(0.0f, 1.0f), which RETURNS a double, and the original roll
        /// compared that double against the float probability directly.
        /// </param>
        /// <param name="spellId">The chosen spell id, or 0 when nothing was chosen.</param>
        public static bool TrySelect(IEnumerable<KeyValuePair<int, float>> book, Func<int, MonsterSpellShape> shapeLookup,
            in VulnerabilityProfile profile, Func<double> rng, out int spellId)
        {
            spellId = 0;

            if (book == null || shapeLookup == null || rng == null)
                return false;

            var redundant = 0;
            var survivors = 0;

            // miss products, in log space so a long book cannot underflow
            var logMissAll = 0.0;
            var logMissSurvivors = 0.0;
            var allCertain = false;         // some entry in the whole book has p >= 1
            var survivorCertain = false;    // some SURVIVOR has p >= 1
            var liveSurvivorSum = 0.0;      // sum of p over survivors with 0 < p < 1
            var liveSurvivorCount = 0;

            foreach (var spell in book)
            {
                var p = Clamp01(ToProbability(spell.Value));
                var tier = Classify(shapeLookup(spell.Key), profile);

                if (p >= 1.0)
                    allCertain = true;
                else
                    logMissAll += Math.Log(1.0 - p);

                if (tier == MonsterSpellPriority.Redundant)
                {
                    redundant++;
                    continue;
                }

                survivors++;

                if (p >= 1.0)
                    survivorCertain = true;
                else
                    logMissSurvivors += Math.Log(1.0 - p);

                if (p > 0.0 && p < 1.0)
                {
                    liveSurvivorSum += p;
                    liveSurvivorCount++;
                }
            }

            if (redundant == 0)
                return RollTiers(book, shapeLookup, profile, rng, double.NaN, 0.0, 0, out spellId);

            // all Redundant, or survivors that cannot carry any cast chance at all (every p == 0 and none
            // certain): no rescale can preserve the rate, so roll the whole book exactly as today
            if (survivors == 0 || (!survivorCertain && logMissSurvivors == 0.0))
                return RollFlat(book, rng, out spellId);

            double exponent;

            if (survivorCertain)
                exponent = 1.0;                         // a survivor already casts every time; nothing to carry
            else if (allCertain)
                exponent = double.PositiveInfinity;     // the book always cast: share the cast proportionally, see RollTiers
            else
                exponent = logMissAll / logMissSurvivors;   // both negative, so >= 1

            return RollTiers(book, shapeLookup, profile, rng, exponent, liveSurvivorSum, liveSurvivorCount, out spellId);
        }

        /// <summary>
        /// Preferred then Neutral, Redundant never. <paramref name="exponent"/> NaN means "no rescale": the
        /// original float probability is compared directly, bit for bit what #1042 did when nothing was
        /// Redundant (and with nothing Redundant the third pass it used to run is empty).
        ///
        /// <paramref name="exponent"/> +infinity means the proportional share-out (see the class summary):
        /// <paramref name="liveSum"/> and <paramref name="liveCount"/> are the sum and count of the survivors'
        /// original p over 0 &lt; p &lt; 1, and each live survivor hits with p / (remaining sum), the last with 1.0.
        /// </summary>
        private static bool RollTiers(IEnumerable<KeyValuePair<int, float>> book, Func<int, MonsterSpellShape> shapeLookup,
            in VulnerabilityProfile profile, Func<double> rng, double exponent, double liveSum, int liveCount, out int spellId)
        {
            spellId = 0;

            var proportional = double.IsPositiveInfinity(exponent);
            var remainingSum = liveSum;
            var remainingCount = liveCount;

            for (var tier = MonsterSpellPriority.Preferred; tier <= MonsterSpellPriority.Neutral; tier++)
            {
                foreach (var spell in book)
                {
                    // re-classified per pass rather than cached into a list: the shape lookup is a memoized
                    // dictionary hit, and a per-roll bucket structure would cost more than it saves
                    if (Classify(shapeLookup(spell.Key), profile) != tier)
                        continue;

                    var probability = ToProbability(spell.Value);

                    bool hit;

                    if (double.IsNaN(exponent))
                    {
                        hit = rng() < probability;
                    }
                    else
                    {
                        var p = Clamp01(probability);

                        double scaled;
                        if (p <= 0.0)
                            scaled = 0.0;
                        else if (p >= 1.0)
                            scaled = 1.0;
                        else if (proportional)
                        {
                            // the last live survivor takes whatever is left, exactly 1.0, so float drift in the
                            // running sum can never leave the book short of a cast
                            scaled = remainingCount <= 1 || remainingSum <= p ? 1.0 : p / remainingSum;
                            remainingSum -= p;
                            remainingCount--;
                        }
                        else
                            scaled = 1.0 - Math.Pow(1.0 - p, exponent);

                        hit = rng() < scaled;
                    }

                    if (hit)
                    {
                        spellId = spell.Key;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The original roll: one flat pass in book order at the original probability.
        /// </summary>
        private static bool RollFlat(IEnumerable<KeyValuePair<int, float>> book, Func<double> rng, out int spellId)
        {
            spellId = 0;

            foreach (var spell in book)
            {
                if (rng() < ToProbability(spell.Value))
                {
                    spellId = spell.Key;
                    return true;
                }
            }

            return false;
        }
    }
}
