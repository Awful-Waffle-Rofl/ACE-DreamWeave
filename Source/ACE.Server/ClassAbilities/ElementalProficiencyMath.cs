using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for Elemental Proficiency (Archmage T3, replaces Elemental Rend): every landed damaging
    /// Archmage spell applies its element's effect, no roll. Values are rank x a per-rank tunable.
    ///
    /// Affinity (War Magic) and the gear amplifier scale ONLY Fire, Lightning (Electric), Acid and Pierce:
    /// magnitude = rankValue * min(affinityMultiplier, 1 + cap) * (1 + gearAmplifier). Frost (Cold),
    /// Bludgeon and Slash are never scaled by either, so <see cref="IsAffinityScaled"/> is the single
    /// place that decides which elements the multiplier reaches.
    ///
    /// Rank 0 (or less) returns no effect for every element. A damage type with no proficiency effect
    /// (Nether, Health, combos) also returns no effect.
    /// </summary>
    public static class ElementalProficiencyMath
    {
        public const string KeyPrefix = "class_ability_elementalproficiency_";

        /// <summary>The effect carried on a target: its strength and the time it ends (running-time seconds).</summary>
        public readonly struct Effect
        {
            public readonly double Value;
            public readonly double EndTime;

            public Effect(double value, double endTime)
            {
                Value = value;
                EndTime = endTime;
            }
        }

        /// <summary>True for the four elements the War Magic affinity and gear amplifier reach.</summary>
        public static bool IsAffinityScaled(DamageType type)
        {
            return type == DamageType.Fire || type == DamageType.Electric || type == DamageType.Acid || type == DamageType.Pierce;
        }

        /// <summary>True for the seven damage types that carry a proficiency effect.</summary>
        public static bool HasEffect(DamageType type)
        {
            switch (type)
            {
                case DamageType.Fire:
                case DamageType.Electric:
                case DamageType.Cold:
                case DamageType.Acid:
                case DamageType.Bludgeon:
                case DamageType.Pierce:
                case DamageType.Slash:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True for the four elements whose effect is TIMED and applied after the hit: Lightning, Frost, Acid, Bludgeon.</summary>
        public static bool IsTimedEffect(DamageType type)
        {
            return type == DamageType.Electric || type == DamageType.Cold || type == DamageType.Acid || type == DamageType.Bludgeon;
        }

        /// <summary>
        /// The gate in front of the timed effects (Player.ApplyElementalProficiencySpellHit), as a pure function:
        /// an eligible target (<see cref="IsEligibleTarget"/>), not a player (class abilities never apply in PvP,
        /// checked on its own so a change to the eligibility rule cannot open PvP), a held rank, and a damage type
        /// with a timed effect (<see cref="IsTimedEffect"/>). Fire, Pierce and Slash are never applied here.
        /// </summary>
        public static bool ShouldApplyTimedEffect(bool eligibleTarget, bool playerTarget, int rank, DamageType type)
        {
            return eligibleTarget && !playerTarget && rank > 0 && IsTimedEffect(type);
        }

        /// <summary>
        /// The pure core: perRank * rank, times min(affinityMultiplier, 1 + cap) and (1 + gearAmplifier)
        /// when <paramref name="scaled"/>. Rank &lt;= 0 gives 0. A negative gear amplifier counts as 0.
        /// </summary>
        public static double Magnitude(double perRank, int rank, bool scaled, double affinityMultiplier, double gearAmplifier, double cap)
        {
            if (rank <= 0)
                return 0.0;

            var value = perRank * rank;

            if (!scaled)
                return value;

            var affinity = Math.Min(affinityMultiplier, 1.0 + Math.Max(0.0, cap));

            return value * affinity * (1.0 + Math.Max(0.0, gearAmplifier));
        }

        /// <summary>
        /// The tunable that carries this element's per-rank value, or null for Bludgeon (whose stun is a
        /// base+step in seconds, see <see cref="StunSeconds"/>) and for types with no effect.
        /// </summary>
        public static string PerRankKey(DamageType type)
        {
            switch (type)
            {
                case DamageType.Fire: return KeyPrefix + "fire_percent_per_rank";
                case DamageType.Electric: return KeyPrefix + "lightning_percent_per_rank";
                case DamageType.Cold: return KeyPrefix + "frost_slow_per_rank";
                case DamageType.Acid: return KeyPrefix + "acid_fraction_per_rank";
                case DamageType.Pierce: return KeyPrefix + "pierce_ignore_per_rank";
                case DamageType.Slash: return KeyPrefix + "slashcrit_points_per_rank";
                default: return null;
            }
        }

        /// <summary>
        /// The effect strength for a landed spell of <paramref name="type"/> at <paramref name="rank"/>, reading
        /// the per-rank and cap tunables: Fire/Lightning = damage fraction, Frost = slow fraction (see
        /// <see cref="FrostSpeedFactor"/>), Acid = DoT total as a fraction of the hit's applied damage,
        /// Pierce = resistance fraction ignored, Slash = crit chance points as a fraction. Bludgeon has no
        /// magnitude (use <see cref="StunSeconds"/>) and returns 0, as does any type without an effect.
        /// </summary>
        public static double Magnitude(DamageType type, int rank, double affinityMultiplier, double gearAmplifier)
        {
            var key = PerRankKey(type);
            if (key == null)
                return 0.0;

            var perRank = PropertyManager.GetDouble(key).Item;
            var cap = PropertyManager.GetDouble(KeyPrefix + "affinity_cap").Item;

            return Magnitude(perRank, rank, IsAffinityScaled(type), affinityMultiplier, gearAmplifier, cap);
        }

        /// <summary>Movement speed multiplier for a Frost slow of <paramref name="slowFraction"/> (0.2 -> 0.8), clamped to 0..1.</summary>
        public static double FrostSpeedFactor(double slowFraction)
        {
            return Math.Clamp(1.0 - slowFraction, 0.0, 1.0);
        }

        /// <summary>Frost speed factor at a rank, reading the tunable. Rank 0 is 1.0 (no slow). Never scaled.</summary>
        public static double FrostSpeedFactor(int rank)
        {
            return FrostSpeedFactor(Magnitude(DamageType.Cold, rank, 1.0, 0.0));
        }

        /// <summary>Pure core of the Bludgeon stun: base + step * (rank - 1), 0 for rank &lt;= 0.</summary>
        public static double StunSeconds(int rank, double baseSeconds, double stepSeconds)
        {
            if (rank <= 0)
                return 0.0;

            return Math.Max(0.0, baseSeconds + (rank - 1) * stepSeconds);
        }

        /// <summary>Bludgeon stun seconds at a rank (2/3/4 by default), reading the tunables. Never scaled.</summary>
        public static double StunSeconds(int rank)
        {
            return StunSeconds(rank,
                PropertyManager.GetDouble(KeyPrefix + "bludgeon_stun_base_seconds").Item,
                PropertyManager.GetDouble(KeyPrefix + "bludgeon_stun_step_seconds").Item);
        }

        /// <summary>Seconds of Bludgeon stun immunity, counted from the moment the stun ENDS.</summary>
        public static double StunImmunitySeconds()
        {
            return PropertyManager.GetDouble(KeyPrefix + "bludgeon_immunity_seconds").Item;
        }

        /// <summary>Duration of the Lightning damage-taken debuff.</summary>
        public static double LightningSeconds()
        {
            return PropertyManager.GetDouble(KeyPrefix + "lightning_seconds").Item;
        }

        /// <summary>Duration of the Frost slow.</summary>
        public static double FrostSeconds()
        {
            return PropertyManager.GetDouble(KeyPrefix + "frost_seconds").Item;
        }

        /// <summary>Total Acid DoT duration in seconds.</summary>
        public static double AcidSeconds()
        {
            return PropertyManager.GetDouble(KeyPrefix + "acid_seconds").Item;
        }

        /// <summary>Acid DoT tick count (at least 1).</summary>
        public static int AcidTicks()
        {
            return (int)Math.Max(1L, PropertyManager.GetLong(KeyPrefix + "acid_ticks").Item);
        }

        /// <summary>
        /// Damage of ONE Acid tick: the hit's applied damage times the DoT total fraction, split evenly over
        /// <paramref name="ticks"/>. Zero for a non-positive damage, fraction or tick count.
        /// </summary>
        public static double AcidDamagePerTick(double appliedDamage, double totalFraction, int ticks)
        {
            if (appliedDamage <= 0.0 || totalFraction <= 0.0 || ticks <= 0)
                return 0.0;

            return appliedDamage * totalFraction / ticks;
        }

        /// <summary>
        /// Refresh merge for a re-applied timed effect: the stronger value wins and the end time is the
        /// later of the two, so a weaker or older hit can neither downgrade nor shorten an existing effect.
        /// </summary>
        public static Effect Merge(Effect existing, Effect incoming)
        {
            return new Effect(Math.Max(existing.Value, incoming.Value), Math.Max(existing.EndTime, incoming.EndTime));
        }

        /// <summary>
        /// Proficiency effects land on monsters only: not a Player, not a Pet (CombatPet derives from it),
        /// not a GamePiece. Mirrors <c>Creature.CanBeClassAbilityPinned</c>'s exclusions, minus its
        /// dead and timer checks, which are per-effect decisions.
        /// </summary>
        public static bool IsEligibleTarget(Creature target)
        {
            if (target == null)
                return false;

            return !(target is Player || target is Pet || target is GamePiece);
        }
    }
}
