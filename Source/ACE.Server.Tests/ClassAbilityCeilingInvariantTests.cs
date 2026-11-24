using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.EquipmentMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Structural invariants over every registered ability's declared ceilings, so the metadata stays uniform without a manual
    /// reconciliation after each batch. None of these read a tunable, so none needs PropertyManager seeded.
    /// </summary>
    [TestClass]
    public class ClassAbilityCeilingInvariantTests
    {
        private static readonly string[] Vocabulary =
        {
            "total", "affinity", "gear", "stacks", "level", "avoidance", "attackspeed", "wisps", "generation", "slowfloor", "pinmax",
        };

        private static readonly string[] KnownFormats = { "pct", "int", "x", "num" };

        /// <summary>The only ability allowed to declare an unbounded affinity rider, with why.</summary>
        private static readonly IReadOnlyDictionary<ClassAbilityId, string> UncappedAllowlist = new Dictionary<ClassAbilityId, string>
        {
            [ClassAbilityId.DoubleVolley] = "the affinity contribution to the proc chance is applied with no clamp at all (provably unbounded by affinity; only the gear term is capped)",
        };

        private static IEnumerable<(ClassAbilityDefinition Def, ClassAbilityCeiling Ceiling)> AllCeilings() =>
            ClassAbilityRegistry.Handlers.Select(h => h.Definition)
                .SelectMany(d => (d.Ceilings ?? Array.Empty<ClassAbilityCeiling>()).Select(c => (d, c)));

        [TestMethod]
        public void Ids_AreFromTheVocabulary()
        {
            var all = AllCeilings().ToList();
            Assert.IsTrue(all.Count > 0, "no ability declares a ceiling");

            var bad = all.Where(x => !Vocabulary.Any(v => x.Ceiling.Id == v || (x.Ceiling.Id ?? "").StartsWith(v + "_", StringComparison.Ordinal)))
                .Select(x => $"{x.Def.Name}: id '{x.Ceiling.Id}'").ToList();

            Assert.AreEqual(0, bad.Count, "ceiling ids outside the vocabulary [" + string.Join(", ", Vocabulary) + "]: " + string.Join("; ", bad));
        }

        [TestMethod]
        public void Ids_AreUniquePerAbility()
        {
            var dupes = ClassAbilityRegistry.Handlers.Select(h => h.Definition)
                .SelectMany(d => (d.Ceilings ?? Array.Empty<ClassAbilityCeiling>()).GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => $"{d.Name}: '{g.Key}' x{g.Count()}"))
                .ToList();

            Assert.AreEqual(0, dupes.Count, string.Join("; ", dupes));
        }

        [TestMethod]
        public void Labels_FollowThePattern()
        {
            var bad = new List<string>();

            foreach (var (def, c) in AllCeilings())
            {
                var l = c.Label ?? "";

                if (l.Length == 0 || l.Length > 40)
                    bad.Add($"{def.Name}/{c.Id}: label '{l}' is empty or over 40 characters ({l.Length})");

                if (l != l.ToLowerInvariant())
                    bad.Add($"{def.Name}/{c.Id}: label '{l}' is not lowercase");

                if (l.Any(char.IsDigit))
                    bad.Add($"{def.Name}/{c.Id}: label '{l}' contains a digit");

                if (!l.StartsWith("max", StringComparison.Ordinal))
                    bad.Add($"{def.Name}/{c.Id}: label '{l}' does not start with 'max'");

                if (c.Bounds == CeilingBounds.Affinity && !Regex.IsMatch(l, "^max .+ from affinity$"))
                    bad.Add($"{def.Name}/{c.Id}: affinity label '{l}' does not match '^max .+ from affinity$'");
            }

            Assert.AreEqual(0, bad.Count, string.Join(Environment.NewLine, bad));
        }

        [TestMethod]
        public void Formats_AreKnownFormatters()
        {
            var bad = AllCeilings()
                .Where(x => !ClassAbilityText.Formatters.Contains(x.Ceiling.Format, StringComparer.Ordinal) || !KnownFormats.Contains(x.Ceiling.Format))
                .Select(x => $"{x.Def.Name}/{x.Ceiling.Id}: format '{x.Ceiling.Format}'").ToList();

            Assert.AreEqual(0, bad.Count, "ceiling formats must be a ClassAbilityText formatter and one of [" + string.Join(", ", KnownFormats) + "]: " + string.Join("; ", bad));
        }

        [TestMethod]
        public void GearCeilings_MatchRegistry()
        {
            var bad = new List<string>();

            foreach (var (def, c) in AllCeilings())
            {
                var isGearBounds = c.Bounds == CeilingBounds.Gear;
                var isGearSource = c.Source == CeilingSource.GearStackCap;

                if (isGearBounds != isGearSource)
                {
                    bad.Add($"{def.Name}/{c.Id}: Bounds={c.Bounds} but Source={c.Source} (Gear bounds and GearStackCap source go together)");
                    continue;
                }

                if (!isGearSource)
                    continue;

                if (c.GearMod == null)
                {
                    bad.Add($"{def.Name}/{c.Id}: GearStackCap ceiling names no GearMod");
                    continue;
                }

                if (!EquipmentModRegistry.TryGet(c.GearMod.Value, out var mod))
                {
                    bad.Add($"{def.Name}/{c.Id}: GearMod {c.GearMod} is not in the equipment mod registry");
                    continue;
                }

                if (!(mod.StackCap > 0.0))
                    bad.Add($"{def.Name}/{c.Id}: {mod.Id} has StackCap {mod.StackCap}, so it has no gear ceiling to declare");

                if (mod.LinkedAbility != def.Id)
                    bad.Add($"{def.Name}/{c.Id}: {mod.Id} links to {mod.LinkedAbility}, not this ability");
            }

            Assert.AreEqual(0, bad.Count, string.Join(Environment.NewLine, bad));
        }

        [TestMethod]
        public void NonSharedRateReads_DeclareAffinityRate()
        {
            var shared = new[] { "class_ability_affinity_rate_per_trained", "class_ability_affinity_rate_per_spec" };
            var keyed = ClassAbilityRegistry.Handlers.Select(h => h.Definition)
                .Where(d => d.AffinityRate != null && (d.AffinityRate.Kind == AffinityRateSpec.RateKind.Own || d.AffinityRate.Kind == AffinityRateSpec.RateKind.LegacyAdditive))
                .ToList();

            var problems = new List<string>();
            var literalsSeen = new HashSet<string>();
            var nonSharedSites = 0;

            foreach (var (path, text) in CeilingCensusSupport.ScannedSources())
            {
                var file = Path.GetFileName(path);

                foreach (var site in NonSharedRateCalls(text))
                {
                    nonSharedSites++;
                    foreach (var lit in site.Literals)
                        literalsSeen.Add(lit);

                    if (site.Literals.Count > 0 && site.Literals.All(l => shared.Contains(l)))
                        continue; // the standard-rate overload forwarding to the three-argument one

                    var owner = keyed.FirstOrDefault(d => site.Literals.Contains(d.AffinityRate.TrainedKey) || site.Literals.Contains(d.AffinityRate.SpecKey));
                    if (owner == null)
                        problems.Add($"{file}:{site.Line}: {site.Method} is called with its own rate arguments [{string.Join(", ", site.Literals)}] but no ability declares an Own/LegacyAdditive AffinityRate over those keys");
                }
            }

            Assert.IsTrue(nonSharedSites > 0, "the rate-call scan found nothing: the parser or the file set is broken");

            // the reverse direction: a declared Own/LegacyAdditive rate must be one some call actually reads
            foreach (var d in keyed)
            {
                if (!literalsSeen.Contains(d.AffinityRate.TrainedKey))
                    problems.Add($"{d.Name}: declares {d.AffinityRate.Kind} rate key {d.AffinityRate.TrainedKey}, which no scanned call reads");
                if (!literalsSeen.Contains(d.AffinityRate.SpecKey))
                    problems.Add($"{d.Name}: declares {d.AffinityRate.Kind} rate key {d.AffinityRate.SpecKey}, which no scanned call reads");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void ExplicitUncapped_IsAllowlisted()
        {
            var uncapped = ClassAbilityRegistry.Handlers.Select(h => h.Definition)
                .Where(d => d.AffinityRate != null && d.AffinityRate.Kind == AffinityRateSpec.RateKind.Uncapped)
                .Select(d => d.Id)
                .ToList();

            CollectionAssert.AreEquivalent(UncappedAllowlist.Keys.ToList(), uncapped,
                "only an allowlisted ability may declare AffinityRateSpec.Uncapped (and each allowlisted one must still declare it): " +
                string.Join("; ", UncappedAllowlist.Select(kv => kv.Key + " - " + kv.Value)));
        }

        // ---------------------------------------------------------------------------------------------
        // call scanner
        // ---------------------------------------------------------------------------------------------

        private sealed record RateCall(string Method, int Line, List<string> Literals);

        /// <summary>
        /// Calls that pass their OWN rates: GetClassAbilityAffinityMultiplier with more than one argument, and every
        /// GetClassAbilityScaling call. Skips declarations and comment lines. Literals are the string literals inside the call.
        /// </summary>
        private static IEnumerable<RateCall> NonSharedRateCalls(string text)
        {
            foreach (Match m in Regex.Matches(text, @"\b(GetClassAbilityAffinityMultiplier|GetClassAbilityScaling)\("))
            {
                var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                var prefix = text.Substring(lineStart, m.Index - lineStart);

                if (prefix.TrimStart().StartsWith("//", StringComparison.Ordinal) || Regex.IsMatch(prefix, @"\bdouble\s+(?:\w+\.)?$"))
                    continue; // a comment or the method declaration itself

                var open = m.Index + m.Length - 1;
                var (args, topLevelCommas, literals) = ParseArgs(text, open);

                if (m.Groups[1].Value == "GetClassAbilityAffinityMultiplier" && topLevelCommas == 0)
                    continue; // the one-argument standard-rate overload

                yield return new RateCall(m.Groups[1].Value, CeilingCensusSupport.LineOf(text, m.Index), literals);
            }
        }

        /// <summary>Walks from an open paren to its match, counting top-level commas and collecting string literals; strings and nesting respected.</summary>
        private static (string Args, int TopLevelCommas, List<string> Literals) ParseArgs(string text, int openParen)
        {
            var depth = 0;
            var commas = 0;
            var literals = new List<string>();
            var sb = new StringBuilder();

            for (var i = openParen; i < text.Length; i++)
            {
                var ch = text[i];

                if (ch == '"')
                {
                    var j = i + 1;
                    var lit = new StringBuilder();
                    while (j < text.Length && text[j] != '"')
                    {
                        if (text[j] == '\\')
                            j++;
                        else
                            lit.Append(text[j]);
                        j++;
                    }
                    literals.Add(lit.ToString());
                    i = j;
                    continue;
                }

                if (ch == '(')
                    depth++;
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                        return (sb.ToString(), commas, literals);
                }
                else if (ch == ',' && depth == 1)
                    commas++;

                if (depth >= 1 && i > openParen)
                    sb.Append(ch);
            }

            throw new InvalidOperationException("unbalanced parentheses while scanning a rate call");
        }
    }
}
