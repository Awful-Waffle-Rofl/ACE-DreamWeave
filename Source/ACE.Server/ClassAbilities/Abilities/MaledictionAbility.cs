using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Blood Mage T2: Vulnerability and Imperil enchantments the player applies land at +10/20/30% intensity
    /// by rank and last 25% longer. It benefits EVERY attacker on that target - war, void, melee, missile -
    /// so it is the class's party contribution and its strongest cross-class bridge. Scales with Life Magic
    /// (Imperil and every elemental Vulnerability are Life Magic spells: ScrollSpells.cs:207, :211+), at half
    /// rate because the effect is party-wide. BLOOD-MAGE-DESIGN.md sec 3 / 3b.
    ///
    /// IT DOES NOTHING FOR THE BLOOD MAGE'S OWN LIFE DAMAGE, AND THAT IS DELIBERATE. Vulnerability and
    /// Imperil are per-damage-type resistance debuffs; there is no Health vulnerability, so Drain/Harm are
    /// untouched. Do not "fix" this by widening the predicate below to ResistHealthBoost.
    ///
    /// Bespoke rather than hooked: the effect has to reach the enchantment registry ENTRY, not a combat
    /// callback, so the wiring is Player.TryGetMaledictionMods, called from EnchantmentManager.BuildEntry
    /// and from the refresh branch of EnchantmentManager.Add. Carries IPassiveStatAbility for the same
    /// reason Transfusion and Soul Tether do - it declares "read at its own site, dispatched from no combat
    /// hook", which is what ClassAbilityRegistryTests checks an Implemented ability for. The arithmetic
    /// lives here as pure statics so it is testable without a live Player.
    ///
    /// Trap for whoever retunes the rider: <c>Player.GetClassAbilityScaling</c> takes DIVISORS ("points of
    /// source skill per +1 unit"), not per-point rates, and every call site multiplies the result by 0.01.
    /// The design's "+1% per 50 Life Magic" is therefore a tunable of 50.0, not 0.02 and not 0.0002.
    /// AT 400 LIFE MAGIC THE RIDER IS +8% TRAINED (400/50) AND +10.8% SPECIALIZED (400/37) - check any
    /// divisor change against those two numbers rather than re-deriving the units.
    /// </summary>
    public class MaledictionAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Malediction,
            AbilityClass = ClassAbilityClass.BloodMage,
            Tier = 2,
            Name = "malediction",
            DisplayName = "Malediction",
            Description = "Vulnerability and Imperil enchantments you apply land at +10/20/30% intensity by " +
                          "rank and last 25% longer. Every attacker on that target benefits, not just you. " +
                          "Higher Life Magic increases the intensity.",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },
            Implemented = true,
            AffinitySkill = Skill.LifeMagic,
        };

        /// <summary>
        /// TRUE when a spell is one Malediction amplifies. Derived from the spell's OWN stat-mod shape
        /// rather than an id list, because the shape is what the debuff actually is and it survives new
        /// content: every elemental Vulnerability in ace_world is a Float/Multiplicative mod on one of the
        /// seven damage resistances (PropertyFloat 64-70) above the 1.0 identity, and every Imperil is a
        /// BodyArmorValue/Additive mod below the 0.0 identity. Their Protection / Armor counterparts are
        /// the identical shape on the other side of the identity, which is what the sign tests exclude.
        ///
        /// The Life Magic gate is what keeps this to "Vulnerability and Imperil" as sec 3 words it: Expose
        /// Weakness is a Void spell of byte-identical shape to Imperil and is deliberately NOT amplified.
        ///
        /// Self-cast Vulnerabilities are amplified too (the "...Self" line has the same shape as "...Other").
        /// That is left alone rather than special-cased: it only ever hurts the caster who chose to cast it.
        /// </summary>
        public static bool AffectsSpell(MagicSchool school, EnchantmentTypeFlags statModType, uint statModKey, float statModVal)
        {
            if (school != MagicSchool.LifeMagic)
                return false;

            // elemental Vulnerability: multiplicative resistance mod above 1.0 (Protection sits below it)
            if (statModType.HasFlag(EnchantmentTypeFlags.Float) &&
                statModType.HasFlag(EnchantmentTypeFlags.Multiplicative) &&
                statModKey >= (uint)PropertyFloat.ResistSlash &&
                statModKey <= (uint)PropertyFloat.ResistElectric)
            {
                return statModVal > 1.0f;
            }

            // Imperil: additive body-armor mod below 0.0 (the Armor buff line sits above it)
            if (statModType.HasFlag(EnchantmentTypeFlags.BodyArmorValue) &&
                statModType.HasFlag(EnchantmentTypeFlags.Additive))
            {
                return statModVal < 0.0f;
            }

            return false;
        }

        /// <summary>
        /// Total intensity bonus as a FRACTION (0.10 = +10%): base + step*(rank-1) plus the Life Magic
        /// rider, additive on one axis. Returns 0 for rank &lt;= 0, so an unlearned caster is byte-identical
        /// to the pre-Malediction behaviour. lifeMagicFraction is Player.GetClassAbilityScaling already
        /// converted from percentage points to a fraction.
        ///
        /// <paramref name="gear"/> is the DEEPEN equipment mod (EquipmentModId.Deepen), a FOURTH ADDITIVE
        /// SUMMAND on that one axis - never a factor (DESIGN.md 3.3). It is added OUTSIDE the rider's own
        /// Math.Max(0.0, ...) but inside the total's, because the inner floor exists to stop a negative
        /// SKILL rider eating the rank bonus, and folding gear into it would let a mis-signed rider swallow
        /// the mod instead. Defaults to 0, and adding 0.0 is exact, so an unmodded enchantment entry is
        /// bit-identical.
        /// </summary>
        public static double IntensityBonus(int rank, double intensityBase, double intensityStep, double lifeMagicFraction, double gear = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            return Math.Max(0.0, intensityBase + (rank - 1) * intensityStep + Math.Max(0.0, lifeMagicFraction) + gear);
        }

        /// <summary>
        /// The enchantment's StatModValue with the intensity bonus applied.
        ///
        /// THE BONUS SCALES THE DEBUFF, NOT THE RAW NUMBER, and for a multiplicative resistance mod those
        /// are not the same thing. A Fire Vulnerability VI is 2.5 - a 1.5 debuff on top of the 1.0 identity
        /// - so +10% intensity is 1.0 + 1.5*1.1 = 2.65, not 2.5*1.1 = 2.75. Multiplying the raw value would
        /// turn a 1.1 (Vulnerability I, +10% damage taken) into 1.21, more than doubling its actual effect,
        /// and would scale hardest exactly where the debuff is weakest. Additive mods (Imperil, identity 0)
        /// have no such distinction: -200 armor at +10% is -220.
        /// </summary>
        public static float ScaleStatModValue(EnchantmentTypeFlags statModType, float statModVal, double intensityBonus)
        {
            if (intensityBonus <= 0.0)
                return statModVal;

            var factor = 1.0 + intensityBonus;

            if (statModType.HasFlag(EnchantmentTypeFlags.Multiplicative))
                return (float)(1.0 + (statModVal - 1.0) * factor);

            return (float)(statModVal * factor);
        }

        /// <summary>
        /// The enchantment's duration extended by the duration bonus (0.25 = 25% longer). A non-positive
        /// duration is returned untouched: -1 is the "until dequipped" sentinel for equip-cast item spells,
        /// and multiplying it would silently turn a permanent enchantment into a longer negative one.
        /// </summary>
        public static double ExtendDuration(double duration, double durationBonus)
        {
            if (duration <= 0.0 || durationBonus <= 0.0)
                return duration;

            return duration * (1.0 + durationBonus);
        }

        /// <summary>
        /// Mirrors the terms fed into IntensityBonus() above. The tunable keys (intensity_base,
        /// intensity_step) and the Life Magic rider keys are read here exactly as they are at the external
        /// call site in Player_ClassAbilityMalediction.cs. Gear is the DEEPEN mod, the fourth summand on
        /// that same additive axis, so all three readout terms are already in one unit. No cap applies to
        /// this ability.
        ///
        /// The DURATION half of Malediction (+25%) is deliberately not part of this readout and takes no
        /// gear term - the readout contract is one scalar, and Deepen prices intensity only.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var intensityBase = PropertyManager.GetDouble("class_ability_malediction_intensity_base").Item;
            var intensityStep = PropertyManager.GetDouble("class_ability_malediction_intensity_step").Item;

            var skillBonus = rank <= 0 ? 0.0 : intensityBase + (rank - 1) * intensityStep;

            var lifeMagic = Math.Max(0.0, player.GetClassAbilityScaling(Skill.LifeMagic,
                PropertyManager.GetDouble("class_ability_malediction_lifemagic_per_trained").Item,
                PropertyManager.GetDouble("class_ability_malediction_lifemagic_per_spec").Item) * 0.01);

            var gearBonus = player.GetEquippedModValue(EquipmentModId.Deepen);

            var skill = skillBonus * 100.0;
            var affinity = lifeMagic * 100.0;
            var gear = gearBonus * 100.0;
            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "debuff",
                Per = null,
                CapNote = null,
            };
        }
    }
}
