using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The projection behind GET /v1/class-abilities/descriptions: every registered ability's player-facing
    /// text rendered from the tunable values handed in (Live in production). Pure, so the wire shape is
    /// unit-tested without an HTTP host. The market serializes it with its snake_case policy; every property
    /// here is a single lowercase word on the wire, which is the contract:
    /// {"abilities":[{"id","description","affinity","ranks":[{"rank","text"}],"ceilings"?:[{"id","bounds","label","value","unit","text","mod"?}],"rates"?:{"trained","specialized","model","text"}}]}.
    /// ceilings and rates are optional and carry no tunable key.
    ///
    /// <c>id</c> is the registry Name, the same string tools/ca-planner/catalog.json uses as its row id.
    /// Retired abilities are not in the registry and are not emitted (their catalog rows carry no text).
    /// <c>ranks</c> is empty for an ability with no rank-tail template. <c>affinity</c> lists the display names of the
    /// skills that scale the ability (AffinitySkill, then AdditionalAffinitySkills), empty when none does.
    /// </summary>
    public static class ClassAbilityDescriptionFeed
    {
        public sealed class RankDto
        {
            public int Rank { get; init; }

            public string Text { get; init; }
        }

        /// <summary>
        /// One declared ceiling, rendered from the values handed in. <c>value</c> is in display units (42 for 42%, 30.9 for a gear
        /// ceiling) and null when the cap tunable reads "no limit"; <c>unit</c> is "%", "x" or ""; <c>bounds</c> is
        /// total | affinity | gear | stacks | level; <c>mod</c> names the gear mod for a gear ceiling and is otherwise absent.
        /// </summary>
        public sealed class CeilingDto
        {
            public string Id { get; init; }

            public string Bounds { get; init; }

            public string Label { get; init; }

            public double? Value { get; init; }

            public string Unit { get; init; }

            public string Text { get; init; }

            public string Mod { get; init; }

            /// <summary>True when the figure is a base the affinity multiplier raises (Mana Rush); absent otherwise.</summary>
            public bool? Scales { get; init; }
        }

        /// <summary>How the affinity rider is rated: model is shared | own | additive | uncapped. trained/specialized are percent of the rank bonus per 100 skill, or skill points per +1% for additive.</summary>
        public sealed class RatesDto
        {
            public double Trained { get; init; }

            public double Specialized { get; init; }

            public string Model { get; init; }

            public string Text { get; init; }
        }

        public sealed class AbilityDto
        {
            public string Id { get; init; }

            public string Description { get; init; }

            public IReadOnlyList<string> Affinity { get; init; }

            public IReadOnlyList<RankDto> Ranks { get; init; }

            /// <summary>Declared ceilings at the max rank; absent (null) for an ability that declares none.</summary>
            public IReadOnlyList<CeilingDto> Ceilings { get; init; }

            /// <summary>The affinity rate; absent (null) for an ability with no affinity rider.</summary>
            public RatesDto Rates { get; init; }
        }

        public sealed class FeedDto
        {
            public IReadOnlyList<AbilityDto> Abilities { get; init; }
        }

        public static FeedDto Build(ITunableValues values)
        {
            var abilities = ClassAbilityRegistry.Abilities.Values
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(def => new AbilityDto
                {
                    Id = def.Name,
                    Description = ClassAbilityText.Describe(def, values),
                    Affinity = ClassAbilityText.AffinityNames(def),
                    Ranks = def.RankTailTemplate == null
                        ? Array.Empty<RankDto>()
                        : Enumerable.Range(1, def.MaxRank)
                            .Select(rank => new RankDto { Rank = rank, Text = ClassAbilityText.RankTail(def, rank, values) })
                            .ToList(),
                    Ceilings = Ceilings(def, values),
                    Rates = Rates(def, values),
                })
                .ToList();

            return new FeedDto { Abilities = abilities };
        }

        private static IReadOnlyList<CeilingDto> Ceilings(ClassAbilityDefinition def, ITunableValues values)
        {
            if (def.Ceilings == null || def.Ceilings.Count == 0)
                return null;

            var rank = def.MaxRank > 0 ? def.MaxRank : 1;
            return def.Ceilings
                .Select(c => new CeilingDto
                {
                    Id = c.Id,
                    Bounds = c.Bounds.ToString().ToLowerInvariant(),
                    Label = c.Label,
                    Value = c.IsUncapped(values, rank) ? null : System.Math.Round(c.DisplayValue(values, rank), 4),
                    Unit = ClassAbilityCeilingText.UnitOf(c),
                    Text = ClassAbilityCeilingText.StaticPhrase(def, c, values),
                    Mod = c.Source == CeilingSource.GearStackCap ? c.GearMod?.ToString() : null,
                    Scales = c.AffinityScalesMax ? true : null,
                })
                .ToList();
        }

        private static RatesDto Rates(ClassAbilityDefinition def, ITunableValues values)
        {
            if (ClassAbilityCeilingText.AffinityRate(def, values) is not { } parts)
                return null;

            return new RatesDto
            {
                Trained = parts.Trained,
                Specialized = parts.Specialized,
                Model = parts.Model,
                Text = ClassAbilityCeilingText.AffinityRateLine(def, values),
            };
        }
    }
}
