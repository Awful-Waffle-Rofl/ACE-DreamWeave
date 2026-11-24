using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A source scan of the class ability code that keeps the declared ceiling metadata honest. Two ways a ceiling can
    /// go undeclared, and one test for each:
    ///
    ///  - <see cref="EveryCapShapedKeyRead_IsDeclaredOrAllowlisted"/>: a cap-shaped tunable key (<c>_cap</c>, <c>_max</c>,
    ///    <c>max_</c>, <c>_ceiling</c>, <c>_floor</c>) is read in code and no ability declares a ceiling over it.
    ///  - <see cref="EveryInventoriedConstant_IsDeclaredOrGuard"/>: a Max/Min constant or a StackCap helper exists in code and
    ///    is neither a declared <see cref="ClassAbilityCeiling.ConstantRef"/> nor on the explicit guard list.
    ///
    /// The scan walks up from the test assembly to find the source tree, like the other source-scan tests; it never hardcodes
    /// a path. Allowlist entries are not a free pass: each carries its reason, and a stale entry (one no code reads any more)
    /// fails <see cref="Allowlists_AndConstantKeyMap_HaveNoStaleEntries"/>.
    /// </summary>
    [TestClass]
    public class ClassAbilityCeilingCensusTests
    {
        /// <summary>Keys that look like a cap but bound nothing a player could exceed, each with why.</summary>
        internal static readonly IReadOnlyDictionary<string, string> NotACeilingKeys = new Dictionary<string, string>
        {
            ["class_ability_tier2_cap_required"] = "unlock gate: points earned before tier 2 opens, not a bound on a magnitude",
            ["class_ability_tier3_cap_required"] = "unlock gate: points earned before tier 3 opens, not a bound on a magnitude",
            ["class_ability_thorns_max_range"] = "condition: how far away an attacker can be and still be reflected at, not a bound on a magnitude",
            ["class_ability_longdraw_max_distance"] = "ramp endpoint: the distance at which Long Draw's bonus stops growing, the ramp's own shape",
            ["class_ability_echocast_wisp_interval_max_seconds"] = "timing: the longest gap between wisp casts, a schedule not a bound on damage",
            ["class_ability_cloakedinpower_floor"] = "the ability's own base value (a minimum proc chance it grants); the affinity amount added to it is bounded by the declared class_ability_affinity_chance_cap",
            ["class_ability_manabarrier_mana_floor"] = "resource floor: Mana at which the barrier stops absorbing, a guard on the player's own pool not a cap on the effect",
        };

        /// <summary>
        /// Ceilings whose number is read through a pure helper over a tunable, declared with a Constant source rather than a Key.
        /// The map names the helper the ceiling's ConstantRef must carry, so the declaration is found without a Key.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, (string ConstantRef, string Reason)> ConstantKeyMap = new Dictionary<string, (string, string)>
        {
            ["class_ability_weakenedblood_slow_floor"] = ("WeakenedBloodMath.MaxSlow", "Weakened Blood declares its slow floor as a Constant over this tunable"),
            ["class_ability_sundermark_max_level"] = ("SundermarkAbility.VulnerabilityMaxLevel", "Sundermark declares the vulnerability level cap as a Constant over this tunable (clamped 1..8 by the helper)"),
            ["class_ability_sundermark_nether_max_level"] = ("SundermarkAbility.NetherMaxLevel", "Sundermark declares the nether curse level cap as a Constant over this tunable (clamped 1..8 by the helper)"),
            ["class_ability_bloodmage_charge_stack_cap_r"] = ("BloodChargeMath.StackCap", "Exsanguinate declares the Blood Charge cap as a Constant over the per-rank tunables at the Sanguine Reserve top rank"),
        };

        /// <summary>
        /// A handler file reading a cap that another ability owns and declares. (file, key) -> why the read is not the
        /// reading ability's own ceiling.
        /// </summary>
        internal static readonly IReadOnlyDictionary<(string File, string Key), string> CrossReads = new Dictionary<(string, string), string>
        {
            [("ParryAbility.cs", "class_ability_affinity_chance_cap")] = "Parry's readout computes Shield Block's half of the pooled roll, which is Shield Block's ceiling, declared there",
            [("PoisonWeaponAbility.cs", "class_ability_acidproc_damage_affinity_cap")] = "Poison Weapon applies Acid Proc's poison-damage rider, which is Acid Proc's ceiling, declared there",
        };

        /// <summary>
        /// Max/Min constants and StackCap helpers under ClassAbilities that bound no player-facing magnitude. "File.Member",
        /// each with why. Anything else the constant scan finds must be a declared ConstantRef.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string> GuardConstants = new Dictionary<string, string>
        {
            ["HuntersEchoMath.MaxTranslucency"] = "visual: the most translucent a Hunter's Echo rendering is drawn (Appendix B)",
            ["HuntersEchoMath.MinMitigationFactor"] = "physics guard: lower clamp on the pet mitigation factor (Appendix B)",
            ["HuntersEchoMath.MaxMitigationFactor"] = "physics guard: upper clamp on the pet mitigation factor (Appendix B)",
            ["HuntersEchoMath.MaxSightTestsPerPick"] = "performance guard: line-of-sight tests per target pick per tick, no player-facing effect",
            ["HuntersEchoMath.MaxRememberedFailures"] = "memory guard: size of the sight-failure memo, no player-facing effect",
            ["WeakenedBloodMath.MinSpeedFactor"] = "tunable sanity bound: the slowest speed factor a mis-tuned pair could produce (Appendix B); the player-facing slow cap is the declared slowfloor ceiling",
            ["ClassAbilityRepricing.MaxChatLineLength"] = "text guard: the chat line length limit for repricing announcements",
            ["SoulJumpAbility.MinimumCooldownSeconds"] = "runaway guard: shortest cooldown any tunable or affinity combination may produce; a hardcoded floor on a mis-tune, not a balance dial (see its doc comment)",
            ["EnhancedStatAbility.MaxTier"] = "rank/index bound: how many tiers the generated Enhanced stat family has",
        };

        private const string KeyRegex = "\"(class_ability_[a-z0-9_]*(?:_cap|_max|max_|_ceiling|_floor)[a-z0-9_]*)\"";
        private const string InterpolatedKeyRegex = "\\$\"(class_ability_[a-z0-9_]*(?:_cap|_max|max_|_ceiling|_floor)[a-z0-9_]*)\\{";

        /// <summary>Max/Min numeric constants (any access, const or static readonly) and StackCap helpers.</summary>
        internal const string ConstantRegex = @"(?:public|internal|private|protected)\s+(?:static\s+)?(?:const|readonly)\s+(?:int|long|uint|double|float|decimal)\s+((?:Max|Min)\w*)\b|static int (StackCap)\(";

        private const string PrefixConcatRegex = "KeyPrefix\\s*\\+\\s*\"([a-z0-9_]*(?:cap|max|floor)[a-z0-9_]*)\"";

        // ---------------------------------------------------------------------------------------------
        // key census
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void EveryCapShapedKeyRead_IsDeclaredOrAllowlisted()
        {
            var problems = new List<string>();
            var any = false;

            foreach (var (path, text) in CeilingCensusSupport.ScannedSources())
            {
                var fileName = Path.GetFileName(path);
                var owner = CeilingCensusSupport.HandlerFor(fileName);

                foreach (var (key, line) in CapShapedKeys(text))
                {
                    any = true;

                    if (NotACeilingKeys.ContainsKey(key))
                        continue;

                    if (owner != null)
                    {
                        if (IsDeclaredBy(owner, key) || CrossReads.ContainsKey((fileName, key)))
                            continue;

                        var declaredElsewhere = DeclaredAnywhere(key);
                        problems.Add($"{fileName}:{line}: {owner.Name} reads {key}, which it declares no ceiling over" +
                            (declaredElsewhere ? " (another ability declares it: if this read is that ability's, list it in CrossReads with a reason)" : " (no ability declares it)"));
                    }
                    else if (!DeclaredAnywhere(key))
                    {
                        problems.Add($"{fileName}:{line}: {key} is read and no ability declares a ceiling over it (declare it, or allowlist it in NotACeilingKeys with a reason)");
                    }
                }
            }

            Assert.IsTrue(any, "the key scan found nothing: the regex or the file set is broken");
            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void EveryInventoriedConstant_IsDeclaredOrGuard()
        {
            var refs = Abilities().SelectMany(a => a.Ceilings ?? Array.Empty<ClassAbilityCeiling>()).Select(c => c.ConstantRef).Where(r => r != null).ToList();
            var problems = new List<string>();
            var seen = new List<string>();

            foreach (var (path, text) in CeilingCensusSupport.ScannedSources().Where(s => s.Path.Replace('\\', '/').Contains("/ClassAbilities/")))
            {
                var file = Path.GetFileNameWithoutExtension(path);

                foreach (var m in Regex.Matches(text, ConstantRegex).Cast<Match>())
                {
                    var member = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    var id = file + "." + member;
                    seen.Add(id);

                    if (GuardConstants.ContainsKey(id) || refs.Any(r => r.Contains(id, StringComparison.Ordinal)))
                        continue;

                    problems.Add($"{id} ({file}.cs:{CeilingCensusSupport.LineOf(text, m.Index)}) is neither a declared ConstantRef nor on the guard list");
                }
            }

            Assert.IsTrue(seen.Count > 0, "the constant scan found nothing: the regex or the file set is broken");
            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Allowlists_AndConstantKeyMap_HaveNoStaleEntries()
        {
            var keysRead = new HashSet<string>();
            var fileKeys = new HashSet<(string, string)>();
            var constants = new HashSet<string>();

            foreach (var (path, text) in CeilingCensusSupport.ScannedSources())
            {
                foreach (var (key, _) in CapShapedKeys(text))
                {
                    keysRead.Add(key);
                    fileKeys.Add((Path.GetFileName(path), key));
                }

                if (path.Replace('\\', '/').Contains("/ClassAbilities/"))
                {
                    foreach (var m in Regex.Matches(text, ConstantRegex).Cast<Match>())
                        constants.Add(Path.GetFileNameWithoutExtension(path) + "." + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
                }
            }

            var stale = new List<string>();
            stale.AddRange(NotACeilingKeys.Keys.Where(k => !keysRead.Contains(k)).Select(k => $"NotACeilingKeys: {k} is no longer read"));
            stale.AddRange(CrossReads.Keys.Where(k => !fileKeys.Contains(k)).Select(k => $"CrossReads: {k.File} no longer reads {k.Key}"));
            stale.AddRange(GuardConstants.Keys.Where(k => !constants.Contains(k)).Select(k => $"GuardConstants: {k} no longer exists"));

            var refs = Abilities().SelectMany(a => a.Ceilings ?? Array.Empty<ClassAbilityCeiling>()).Where(c => c.Source == CeilingSource.Constant).ToList();
            foreach (var (key, entry) in ConstantKeyMap)
            {
                if (!keysRead.Contains(key) && !keysRead.Any(k => k.StartsWith(key, StringComparison.Ordinal)))
                    stale.Add($"ConstantKeyMap: {key} is no longer read");

                if (!refs.Any(c => c.ConstantRef != null && c.ConstantRef.Contains(entry.ConstantRef, StringComparison.Ordinal)))
                    stale.Add($"ConstantKeyMap: no Constant ceiling carries ConstantRef '{entry.ConstantRef}' for {key}");
            }

            Assert.AreEqual(0, stale.Count, string.Join(Environment.NewLine, stale));
        }

        [TestMethod]
        public void ConstantRegex_MatchesTheDeclarationShapes()
        {
            var samples = new[]
            {
                ("public const int MaxFoo = 3;", "MaxFoo"),
                ("internal const int MaxFoo = 3;", "MaxFoo"),
                ("public const long MaxFoo = 3;", "MaxFoo"),
                ("public static readonly double MaxX = 3.0;", "MaxX"),
                ("private const double MinX = 0.1;", "MinX"),
                ("public static int StackCap(int rank)", "StackCap"),
            };

            foreach (var (text, member) in samples)
            {
                var m = Regex.Match(text, ConstantRegex);
                Assert.IsTrue(m.Success, $"'{text}' not matched");
                Assert.AreEqual(member, m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, text);
            }

            Assert.IsFalse(Regex.IsMatch("public const string MaxKey = \"x\";", ConstantRegex), "string constants are not numeric caps");
        }

        [TestMethod]
        public void InterpolatedKeyRegex_FindsAPerRankPrefix()
        {
            var m = Regex.Match("PropertyManager.GetLong($\"class_ability_foo_stack_cap_r{rank}\")", InterpolatedKeyRegex);
            Assert.IsTrue(m.Success);
            Assert.AreEqual("class_ability_foo_stack_cap_r", m.Groups[1].Value);
        }

        // ---------------------------------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------------------------------

        private static IEnumerable<ClassAbilityDefinition> Abilities() => ClassAbilityRegistry.Handlers.Select(h => h.Definition);

        /// <summary>Every cap-shaped key in <paramref name="text"/> with its 1-based line, KeyPrefix concatenations resolved.</summary>
        private static IEnumerable<(string Key, int Line)> CapShapedKeys(string text)
        {
            foreach (Match m in Regex.Matches(text, KeyRegex))
                yield return (m.Groups[1].Value, CeilingCensusSupport.LineOf(text, m.Index));

            // interpolated per-rank keys: the text before the first brace is the prefix
            foreach (Match m in Regex.Matches(text, InterpolatedKeyRegex))
                yield return (m.Groups[1].Value, CeilingCensusSupport.LineOf(text, m.Index));

            foreach (Match m in Regex.Matches(text, PrefixConcatRegex))
                yield return (ACE.Server.ClassAbilities.ElementalProficiencyMath.KeyPrefix + m.Groups[1].Value, CeilingCensusSupport.LineOf(text, m.Index));
        }

        private static bool DeclaredAnywhere(string key) => Abilities().Any(a => IsDeclaredBy(a, key));

        /// <summary>
        /// True when <paramref name="def"/> declares a ceiling over <paramref name="key"/>: a Tunable with that Key, a TunablePerRank
        /// whose Key is the key's prefix before an _rN suffix (or the prefix itself), or a Constant whose ConstantRef the map pairs with it.
        /// </summary>
        private static bool IsDeclaredBy(ClassAbilityDefinition def, string key)
        {
            var perRankPrefix = Regex.Replace(key, @"\d+$", "");

            foreach (var c in def.Ceilings ?? Array.Empty<ClassAbilityCeiling>())
            {
                if (c.Source == CeilingSource.Tunable && c.Key == key)
                    return true;

                if (c.Source == CeilingSource.TunablePerRank && (c.Key == key || (perRankPrefix != key && c.Key == perRankPrefix)))
                    return true;

                if (c.Source == CeilingSource.Constant && c.ConstantRef != null)
                {
                    if (ConstantKeyMap.TryGetValue(key, out var entry) && c.ConstantRef.Contains(entry.ConstantRef, StringComparison.Ordinal))
                        return true;

                    if (perRankPrefix != key && ConstantKeyMap.TryGetValue(perRankPrefix, out var perRank) && c.ConstantRef.Contains(perRank.ConstantRef, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }
    }

    /// <summary>The file set and scan helpers the ceiling census and invariant tests share.</summary>
    internal static class CeilingCensusSupport
    {
        /// <summary>
        /// ClassAbilities/** plus the Player partials that carry class ability clamps. Every listed file must exist, so a rename
        /// fails loudly instead of silently shrinking the scan.
        /// </summary>
        public static IEnumerable<(string Path, string Text)> ScannedSources()
        {
            var server = FindServerSourceDir();

            var files = Directory.GetFiles(Path.Combine(server, "ClassAbilities"), "*.cs", SearchOption.AllDirectories).ToList();

            var worldObjects = Path.Combine(server, "WorldObjects");
            files.AddRange(Directory.GetFiles(worldObjects, "Player_ClassAbility*.cs"));

            foreach (var name in new[] { "Player_ClassAbilities.cs", "Player_EchoWisp.cs", "Player_VolatileElements.cs", "Player_EquipmentMods.cs" })
            {
                var f = Path.Combine(worldObjects, name);
                Assert.IsTrue(File.Exists(f), $"census file set names {name}, which no longer exists: update the set");
                files.Add(f);
            }

            return files.Distinct().OrderBy(f => f, StringComparer.Ordinal).Select(f => (f, File.ReadAllText(f))).ToList();
        }

        /// <summary>The registered ability whose handler class is declared in <paramref name="fileName"/> (Abilities/&lt;Type&gt;.cs), or null.</summary>
        public static ClassAbilityDefinition HandlerFor(string fileName)
        {
            var typeName = Path.GetFileNameWithoutExtension(fileName);
            return ClassAbilityRegistry.Handlers.FirstOrDefault(h => h.GetType().Name == typeName)?.Definition;
        }

        public static int LineOf(string text, int index)
        {
            var line = 1;
            for (var i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n')
                    line++;
            return line;
        }

        private static string FindServerSourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "ClassAbilities")))
                dir = dir.Parent;

            if (dir == null)
                Assert.Fail("Could not locate Source/ACE.Server/ClassAbilities by walking up from " + AppContext.BaseDirectory + "; the census cannot run");

            return Path.Combine(dir.FullName, "Source", "ACE.Server");
        }
    }
}
