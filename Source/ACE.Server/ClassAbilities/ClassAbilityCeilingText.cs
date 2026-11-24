using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Renders declared ceilings as player-facing lines. Every number goes through
    /// <see cref="ClassAbilityText.Format"/>, so the wording can never disagree with the formatter vocabulary.
    /// </summary>
    public static class ClassAbilityCeilingText
    {
        private const string NoLimit = "no limit";

        private static string Unit(ClassAbilityCeiling c) => c.IsPctFormat ? "%" : string.Empty;

        // One formatting path for Lines, Sentence and ReadingLine. Takes DISPLAY units (50 for 50%, 30.9 for a
        // gear ceiling) and hands ClassAbilityText.Format the raw unit it expects (a fraction for pct).
        private static string Number(ClassAbilityCeiling c, double display) =>
            ClassAbilityText.Format(c.IsPctFormat ? display / 100.0 : display, c.Format)
            + (c.Format == "x" ? string.Empty : Unit(c));

        private static string Phrase(ClassAbilityCeiling c, ITunableValues v, int rank) =>
            c.IsUncapped(v, rank) ? $"{c.Label}: {NoLimit}" : $"{c.Label} {Number(c, c.DisplayValue(v, rank))}";

        /// <summary>
        /// ", raised by Loyalty" for a ceiling whose max is scaled by the affinity multiplier (<see cref="ClassAbilityCeiling.AffinityScalesMax"/>),
        /// so the quoted base figure is not read as the final clamp. Empty otherwise or when the ability has no affinity skill.
        /// </summary>
        private static string ScalesSuffix(ClassAbilityDefinition def, ClassAbilityCeiling c) =>
            c.AffinityScalesMax && def.AffinitySkill.HasValue
                ? ", raised by " + ACE.Entity.Enum.SkillExtensions.ToSentence(def.AffinitySkill.Value)
                : string.Empty;

        /// <summary>The unit a ceiling's number carries on the wire and in text: "%", "x" or "".</summary>
        public static string UnitOf(ClassAbilityCeiling c) => c.IsPctFormat ? "%" : c.Format == "x" ? "x" : string.Empty;

        /// <summary>
        /// The player-independent phrase for one ceiling at the ability's max rank ("max stacks 5"). A figure that is lower
        /// at lower ranks, or that quotes another ability's top rank, ends "at top rank" so it is not read as the caster's own cap.
        /// </summary>
        public static string StaticPhrase(ClassAbilityDefinition def, ClassAbilityCeiling c, ITunableValues v)
        {
            var rank = def.MaxRank > 0 ? def.MaxRank : 1;
            var phrase = Phrase(c, v, rank) + ScalesSuffix(def, c);
            return !c.IsUncapped(v, rank) && c.ShowsTopRank(v, rank) ? phrase + " at top rank" : phrase;
        }

        /// <summary>
        /// The /abilities info limit lines, one per ceiling, empty for none. A reader who owns the ability (<paramref name="rank"/> above 0) sees
        /// their OWN rank's values; a ceiling that quotes another ability's top rank (<see cref="ClassAbilityCeiling.TopRankOnly"/>) and a
        /// reader with no rank both get the max-rank figure with its "at top rank" wording (<see cref="StaticPhrase"/>).
        /// </summary>
        public static IReadOnlyList<string> InfoLines(ClassAbilityDefinition def, int rank, ITunableValues v) =>
            (def.Ceilings ?? new ClassAbilityCeiling[0])
                .Select(c => rank > 0 && !c.TopRankOnly
                    ? $"  Limit: {Phrase(c, v, System.Math.Min(rank, def.MaxRank > 0 ? def.MaxRank : 1))}{ScalesSuffix(def, c)}."
                    : $"  Limit: {StaticPhrase(def, c, v)}.")
                .ToList();

        public static IReadOnlyList<string> Lines(ClassAbilityDefinition def, int rank, ITunableValues v) =>
            (def.Ceilings ?? new ClassAbilityCeiling[0]).Select(c => $"  Limit: {Phrase(c, v, rank)}{ScalesSuffix(def, c)}.").ToList();

        /// <summary>One token-length sentence summarising every ceiling at the ability's max rank, or null for none.</summary>
        public static string Sentence(ClassAbilityDefinition def, ITunableValues v)
        {
            if (def.Ceilings == null || def.Ceilings.Count == 0)
                return null;

            return "Limits: " + string.Join("; ", def.Ceilings.Select(c => StaticPhrase(def, c, v))) + ".";
        }

        /// <summary>
        /// The rate spec an ability gets when it declares none: the shared rates when it has an affinity skill, nothing
        /// otherwise. "No limit" is never derived (a Total ceiling can still bound the affinity-scaled value); an
        /// unbounded rider declares <see cref="AffinityRateSpec.Uncapped"/>, and own or legacy-additive rates are declared too.
        /// </summary>
        private static AffinityRateSpec DeriveRate(ClassAbilityDefinition def) =>
            def.AffinitySkill == null ? null : AffinityRateSpec.Shared();

        /// <summary>The structured rate behind <see cref="AffinityRateLine"/>: Model is shared | own | additive | uncapped.</summary>
        public readonly struct RateParts
        {
            public string Model { get; init; }

            /// <summary>Per-100-skill share of the rank bonus in percent (shared, own, uncapped), or skill points per +1% (additive).</summary>
            public double Trained { get; init; }

            public double Specialized { get; init; }
        }

        /// <summary>The rate numbers the affinity line quotes, or null when the ability has no rate to quote.</summary>
        public static RateParts? AffinityRate(ClassAbilityDefinition def, ITunableValues v)
        {
            var spec = def.AffinityRate ?? DeriveRate(def);
            if (spec == null)
                return null;

            if (spec.Kind == AffinityRateSpec.RateKind.LegacyAdditive)
                return new RateParts { Model = "additive", Trained = ClassAbilityText.Read(v, spec.TrainedKey), Specialized = ClassAbilityText.Read(v, spec.SpecKey) };

            var shared = spec.Kind == AffinityRateSpec.RateKind.Shared || spec.Kind == AffinityRateSpec.RateKind.Uncapped;
            var trainedKey = shared ? "class_ability_affinity_rate_per_trained" : spec.TrainedKey;
            var specKey = shared ? "class_ability_affinity_rate_per_spec" : spec.SpecKey;
            var model = spec.Kind == AffinityRateSpec.RateKind.Uncapped ? "uncapped" : shared ? "shared" : "own";
            return new RateParts { Model = model, Trained = System.Math.Round(ClassAbilityText.Read(v, trainedKey) * 100.0, 4), Specialized = System.Math.Round(ClassAbilityText.Read(v, specKey) * 100.0, 4) };
        }

        /// <summary>The "Affinity adds ..." line, or null when the ability declares no rate to quote. Built from <see cref="AffinityRate"/>, the one place the rate keys are resolved.</summary>
        public static string AffinityRateLine(ClassAbilityDefinition def, ITunableValues v)
        {
            if (AffinityRate(def, v) is not { } parts)
                return null;

            var explicitLine = ExplicitRateLine(def, parts);

            // An explicit rate quoted for a DIFFERENT skill than the ability's main affinity rider (Empowered Summons: Loyalty rates the
            // duration half, Leadership rides the pet stats) must not hide the main rider: its shared line comes first.
            var spec = def.AffinityRate;
            if (spec != null && spec.Skill.HasValue && def.AffinitySkill.HasValue && spec.Skill.Value != def.AffinitySkill.Value
                && AffinityRate(new ClassAbilityDefinition { AffinitySkill = def.AffinitySkill }, v) is { } shared)
                return ExplicitRateLine(new ClassAbilityDefinition { AffinitySkill = def.AffinitySkill }, shared) + "; " + explicitLine;

            return explicitLine;
        }

        private static string ExplicitRateLine(ClassAbilityDefinition def, RateParts parts)
        {
            // Uncapped quotes the shared rates, then says nothing bounds what affinity adds.
            var rateSkill = (def.AffinityRate ?? DeriveRate(def)).Skill ?? def.AffinitySkill;
            var skillName = rateSkill.HasValue ? ACE.Entity.Enum.SkillExtensions.ToSentence(rateSkill.Value) : "Affinity";

            // The legacy additive keys are skill points per +1%, not fractions: they read as a divisor.
            if (parts.Model == "additive")
            {
                var noun = def.AffinityRate?.Noun;
                return $"{skillName} adds +1%{(noun == null ? string.Empty : " " + noun)} per {ClassAbilityText.Format(parts.Trained, "num")} skill (trained) / per {ClassAbilityText.Format(parts.Specialized, "num")} skill (specialized)";
            }

            var trained = ClassAbilityText.Format(parts.Trained / 100.0, "pct");
            var specialized = ClassAbilityText.Format(parts.Specialized / 100.0, "pct");
            var line = $"{skillName} adds {trained}% (trained) / {specialized}% (specialized) of the rank bonus per 100 skill";
            return parts.Model == "uncapped" ? line + ". No limit on what affinity adds." : line;
        }

        /// <summary>One line per reading: "Limit: label now / max" with an at-limit marker when binding.</summary>
        public static string ReadingLine(ClassAbilityDefinition def, ClassAbilityReadout readout)
        {
            if (readout.Ceilings == null || readout.Ceilings.Count == 0 || def.Ceilings == null)
                return null;

            var parts = new List<string>();
            foreach (var r in readout.Ceilings)
            {
                var c = def.Ceilings.FirstOrDefault(x => x.Id == r.Id);
                if (c == null)
                    continue;

                var cur = Number(c, r.Current);
                var max = CanBeUncapped(c) && r.Max <= 0.0 ? NoLimit : Number(c, r.Max);
                parts.Add($"{c.Label} {cur} / {max}{(ShowsAtLimit(c, r) ? " (at limit)" : string.Empty)}");
            }

            return parts.Count == 0 ? null : "Limit: " + string.Join("; ", parts);
        }

        /// <summary>
        /// The "(at limit)" marker rule. A Level reading with nothing to compare (Current 0: Cascade's generation is structural, the
        /// player is not on any hop) is a description of the bound, not a player at it, so it never reads "at limit".
        /// </summary>
        private static bool ShowsAtLimit(ClassAbilityCeiling c, ClassAbilityCeilingReading r) =>
            r.Binding && !(c.Bounds == CeilingBounds.Level && r.Current <= 0.0);

        /// <summary>True when a reading's cap is "no limit" (Max 0 on a ceiling where 0 is not a real bound).</summary>
        public static bool IsUnlimited(ClassAbilityDefinition def, ClassAbilityCeilingReading r)
        {
            var c = def?.Ceilings?.FirstOrDefault(x => x.Id == r.Id);
            return c != null && CanBeUncapped(c) && r.Max <= 0.0;
        }

        /// <summary>
        /// Only a tunable cap can read "no limit" at 0. A fixed or gear-stack ceiling at 0 is a real bound (the equipment potency scale at 0 makes the
        /// gear ceiling 0), unless the declaration says 0 is a limit.
        /// </summary>
        private static bool CanBeUncapped(ClassAbilityCeiling c) =>
            !c.ZeroIsLimit && (c.Source == CeilingSource.Tunable || c.Source == CeilingSource.TunablePerRank);

        /// <summary>
        /// The /abilities list companion line: ONE compact line for every reading, "  limits: chance from affinity 47/50%, ...".
        /// Labels drop their leading "max " and the unit is written once, after the max. Null for none. The line is kept within
        /// <paramref name="maxLength"/> by dropping trailing readings, so a chat Reply never truncates mid-number.
        /// </summary>
        public static string CompactReadingLine(ClassAbilityDefinition def, ClassAbilityReadout readout, int maxLength = 255)
        {
            if (readout.Ceilings == null || readout.Ceilings.Count == 0 || def?.Ceilings == null)
                return null;

            var parts = new List<string>();
            foreach (var r in readout.Ceilings)
            {
                var c = def.Ceilings.FirstOrDefault(x => x.Id == r.Id);
                if (c == null)
                    continue;

                var unit = UnitOf(c);
                var cur = StripUnit(Number(c, r.Current), unit);
                var max = IsUnlimited(def, r) ? NoLimit : Number(c, r.Max);
                var label = c.Label.StartsWith("max ", System.StringComparison.Ordinal) ? c.Label.Substring(4) : c.Label;
                parts.Add($"{label} {cur}/{max}{(ShowsAtLimit(c, r) ? " (at limit)" : string.Empty)}");
            }

            while (parts.Count > 0)
            {
                var line = "  limits: " + string.Join(", ", parts);
                if (line.Length <= maxLength)
                    return line;

                parts.RemoveAt(parts.Count - 1);
            }

            return null;
        }

        private static string StripUnit(string number, string unit) =>
            unit.Length > 0 && number.EndsWith(unit, System.StringComparison.Ordinal) ? number.Substring(0, number.Length - unit.Length) : number;
    }
}
