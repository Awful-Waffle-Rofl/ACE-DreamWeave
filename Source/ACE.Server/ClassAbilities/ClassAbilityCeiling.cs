using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.EquipmentMods;

namespace ACE.Server.ClassAbilities
{
    /// <summary>What a ceiling bounds. Descriptive only; the renderer does not branch on it.</summary>
    public enum CeilingBounds { Total, Affinity, Gear, Stacks, Level }

    /// <summary>Where a ceiling's value comes from.</summary>
    public enum CeilingSource
    {
        /// <summary>One tunable, named by <see cref="ClassAbilityCeiling.Key"/>.</summary>
        Tunable,

        /// <summary>One tunable per rank: <see cref="ClassAbilityCeiling.Key"/> is a prefix and the rank is appended.</summary>
        TunablePerRank,

        /// <summary>The gear mod's StackCap x MaxMagnitude x equipment_mod_potency_scale x DisplayScale.</summary>
        GearStackCap,

        /// <summary>A code constant or pure helper, <see cref="ClassAbilityCeiling.ConstantValue"/> (rank in) or <see cref="ClassAbilityCeiling.ConstantValueFor"/> (tunables and rank in).</summary>
        Constant,
    }

    /// <summary>
    /// One declared ceiling of a class ability: the number a player can never exceed however much skill,
    /// affinity or gear they stack. Pure data plus a value reader; <see cref="ClassAbilityCeilingText"/> renders it.
    ///
    /// <see cref="Value"/> returns the ceiling in its RAW unit: a fraction for a pct tunable (0.50), the
    /// rank's number for an int, and for a gear ceiling the already display-scaled number (30.9 for Acid
    /// Proc's +30.9%). A tunable cap of 0 means "uncapped" (house convention), see <see cref="IsUncapped"/>.
    /// </summary>
    public sealed class ClassAbilityCeiling
    {
        /// <summary>Id from the fixed vocabulary (total, affinity, gear, stacks, level, ...). Unique per ability.</summary>
        public string Id;

        public CeilingBounds Bounds;

        public CeilingSource Source;

        /// <summary>Tunable key (Tunable) or key prefix to which the rank is appended (TunablePerRank).</summary>
        public string Key;

        /// <summary>The mod whose StackCap bounds the gear contribution (GearStackCap).</summary>
        public EquipmentModId? GearMod;

        /// <summary>Value by rank for a Constant ceiling. Must call the same pure helper the handler calls.</summary>
        public Func<int, double> ConstantValue;

        /// <summary>
        /// Value by rank for a Constant ceiling that depends on tunables, read through the <see cref="ITunableValues"/> the ceiling is
        /// rendered with (so a Defaults surface shows defaults, not live values). When set it wins over <see cref="ConstantValue"/>.
        /// Must call the same pure helper the handler calls.
        /// </summary>
        public Func<ITunableValues, int, double> ConstantValueFor;

        /// <summary>Human-readable name of the constant or helper, for example "PinReductionMath.MaxReduction".</summary>
        public string ConstantRef;

        /// <summary>Lowercase noun phrase, for example "max combined block and parry chance".</summary>
        public string Label;

        /// <summary>One of <see cref="ClassAbilityText.Formatters"/>.</summary>
        public string Format;

        /// <summary>True when the ceiling grows with the ability's affinity multiplier (it is not a flat number).</summary>
        public bool AffinityScalesMax;

        /// <summary>
        /// True when the handler's clamp treats a tunable cap of 0 as a real bound (Math.Max(0, cap), Math.Min(x, 1 + cap),
        /// stacks &lt; cap), so 0 must render as "0" and bind, not as "no limit". False (default) where the code reads
        /// <c>cap &gt; 0 ? Math.Min(x, cap) : x</c>, i.e. 0 means uncapped.
        /// </summary>
        public bool ZeroIsLimit;

        /// <summary>
        /// True when the declared value is the TOP-rank figure of something this ability's own rank does not set (Exsanguinate
        /// quotes Sanguine Reserve's top-rank charge cap), so a static line must say "at top rank" rather than read as the
        /// caster's own cap. A ceiling whose value differs between rank 1 and the max rank is treated the same way automatically.
        /// </summary>
        public bool TopRankOnly;

        /// <summary>
        /// The ceiling in its RAW unit: a fraction for a pct tunable (0.50), the plain number for an int or x,
        /// and for a gear ceiling the already display-scaled number (30.9). See <see cref="DisplayValue"/>.
        /// </summary>
        public double Value(ITunableValues v, int rank)
        {
            switch (Source)
            {
                case CeilingSource.Tunable:
                    return ClassAbilityText.Read(v, Key);
                case CeilingSource.TunablePerRank:
                    return ClassAbilityText.Read(v, Key + rank);
                case CeilingSource.GearStackCap:
                {
                    var mod = EquipmentModRegistry.Get(GearMod.Value);
                    return mod.StackCap * mod.MaxMagnitude * v.GetDouble("equipment_mod_potency_scale") * mod.DisplayScale;
                }
                case CeilingSource.Constant:
                    return ConstantValueFor != null ? ConstantValueFor(v, rank) : ConstantValue(rank);
                default:
                    throw new InvalidOperationException($"unknown ceiling source {Source}");
            }
        }

        /// <summary>A tunable cap at 0 or below means "no limit" (the house convention for cap tunables).</summary>
        public bool IsUncapped(ITunableValues v, int rank) =>
            !ZeroIsLimit && (Source == CeilingSource.Tunable || Source == CeilingSource.TunablePerRank) && Value(v, rank) <= 0.0;

        /// <summary>True when a static (rankless) line shows the TOP-rank value of a figure that is lower at lower ranks.</summary>
        public bool ShowsTopRank(ITunableValues v, int maxRank) =>
            TopRankOnly || (maxRank > 1 && Value(v, 1) != Value(v, maxRank));

        internal bool IsPctFormat => Format != null && Format.StartsWith("pct", StringComparison.Ordinal);

        /// <summary>The ceiling in DISPLAY units, the unit readings use (50 for a 50% pct tunable). Gear values are display-scaled already.</summary>
        public double DisplayValue(ITunableValues v, int rank)
        {
            var raw = Value(v, rank);
            return Source != CeilingSource.GearStackCap && IsPctFormat ? raw * 100.0 : raw;
        }
    }

    /// <summary>One reading of a ceiling against the current raw value, in display units.</summary>
    public readonly struct ClassAbilityCeilingReading
    {
        public string Id { get; init; }

        /// <summary>The raw (pre-clamp) value, display units.</summary>
        public double Current { get; init; }

        /// <summary>The ceiling, display units. 0 when the ceiling is uncapped.</summary>
        public double Max { get; init; }

        /// <summary>True when Current is at or above a real ceiling, so the cap is what the player is hitting.</summary>
        public bool Binding { get; init; }
    }

    public static class ClassAbilityCeilings
    {
        /// <summary>
        /// <see cref="Read(ClassAbilityDefinition, string, double, int, ITunableValues)"/> for an <see cref="ClassAbilityCeiling.AffinityScalesMax"/>
        /// ceiling: the real cap is the declared one times max(1, <paramref name="multiplier"/>), so Max is scaled and
        /// <paramref name="current"/> must be the value reaching that scaled clamp.
        /// </summary>
        public static ClassAbilityCeilingReading ReadScaled(ClassAbilityDefinition def, string id, double current, int rank, ITunableValues v, double multiplier)
        {
            var reading = Read(def, id, current, rank, v);
            var scale = System.Math.Max(1.0, multiplier);
            var max = reading.Max * scale;
            var uncapped = max <= 0.0 && def.Ceilings.First(c => c.Id == id).IsUncapped(v, rank);
            return new ClassAbilityCeilingReading { Id = id, Current = current, Max = max, Binding = !uncapped && current >= max - 1e-9 };
        }

        public static ClassAbilityCeilingReading Read(ClassAbilityDefinition def, string id, double current, int rank, ITunableValues v)
        {
            var ceiling = def?.Ceilings?.FirstOrDefault(c => c.Id == id)
                ?? throw new KeyNotFoundException($"class ability {def?.Name} declares no ceiling '{id}'");

            if (ceiling.IsUncapped(v, rank))
                return new ClassAbilityCeilingReading { Id = id, Current = current, Max = 0.0, Binding = false };

            var max = ceiling.DisplayValue(v, rank);
            return new ClassAbilityCeilingReading { Id = id, Current = current, Max = max, Binding = current >= max - 1e-9 };
        }
    }

    /// <summary>How an ability's affinity rider is rated, for the "Affinity adds ..." line.</summary>
    public sealed class AffinityRateSpec
    {
        public enum RateKind { Shared, Own, LegacyAdditive, Uncapped }

        public RateKind Kind { get; private init; }

        /// <summary>Tunable key for the Trained source rate (null for Shared/Uncapped).</summary>
        public string TrainedKey { get; private init; }

        /// <summary>Tunable key for the Specialized source rate (null for Shared/Uncapped).</summary>
        public string SpecKey { get; private init; }

        /// <summary>The skill the rate is quoted for, when it differs from the ability's AffinitySkill (null = use AffinitySkill).</summary>
        public ACE.Entity.Enum.Skill? Skill { get; private init; }

        /// <summary>What a legacy additive rate adds, when "+1%" alone is ambiguous ("pet duration"); null quotes "+1%" bare.</summary>
        public string Noun { get; private init; }

        /// <summary>The shared multiplicative rates (class_ability_affinity_rate_per_trained / _per_spec).</summary>
        public static AffinityRateSpec Shared() => new AffinityRateSpec { Kind = RateKind.Shared };

        /// <summary>An ability-specific multiplicative rate pair.</summary>
        public static AffinityRateSpec Own(string trainedKey, string specKey, ACE.Entity.Enum.Skill? skill = null) =>
            Keyed(RateKind.Own, trainedKey, specKey, skill);

        private static AffinityRateSpec Keyed(RateKind kind, string trainedKey, string specKey, ACE.Entity.Enum.Skill? skill)
        {
            ArgumentNullException.ThrowIfNull(trainedKey);
            ArgumentNullException.ThrowIfNull(specKey);
            return new AffinityRateSpec { Kind = kind, TrainedKey = trainedKey, SpecKey = specKey, Skill = skill };
        }

        /// <summary>A rate pair applied additively (the legacy quotient shape): the keys are skill points per +1%, not fractions.</summary>
        public static AffinityRateSpec LegacyAdditive(string trainedKey, string specKey, ACE.Entity.Enum.Skill? skill = null, string noun = null)
        {
            var spec = Keyed(RateKind.LegacyAdditive, trainedKey, specKey, skill);
            return new AffinityRateSpec { Kind = spec.Kind, TrainedKey = trainedKey, SpecKey = specKey, Skill = skill, Noun = noun };
        }

        /// <summary>Quotes the shared rates and states that nothing bounds what affinity adds.</summary>
        public static readonly AffinityRateSpec Uncapped = new AffinityRateSpec { Kind = RateKind.Uncapped };
    }
}
