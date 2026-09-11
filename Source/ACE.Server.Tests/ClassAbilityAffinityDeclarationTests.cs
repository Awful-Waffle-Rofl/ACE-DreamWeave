using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Keeps <see cref="ClassAbilityDefinition.AffinitySkill"/> honest against the code it describes.
    ///
    /// "Affinity" - the legacy trained Skill whose effective value rides an ability's magnitude - used to
    /// exist ONLY as a <c>player.GetClassAbilityScaling(Skill.X, ...)</c> call buried in a handler. It is now
    /// also declared as data on the definition so tooling can read it (the planner catalog under
    /// tools/ca-planner). Two copies of one fact drift, so this test scans every handler under
    /// ClassAbilities/Abilities for the scaling call and fails when the declaration disagrees.
    ///
    /// A file scan cannot resolve two situations on its own, and both are carried as EXPLICIT tables below
    /// rather than inferred:
    ///
    ///  - <see cref="ForeignScalingCalls"/>: a handler file whose text contains a scaling call belonging to a
    ///    DIFFERENT ability. Parry and Shield Block each compute the other's half of their pooled avoidance
    ///    roll, and Poison Weapon computes Acid Proc's poison-damage rider.
    ///  - <see cref="ScalingCallsOutsideTheHandler"/>: an ability whose only scaling call lives outside its
    ///    own handler file. Acid Proc's rider is computed in PoisonWeaponAbility, and Frenzy's in
    ///    Player_ClassAbilityBuffs.
    ///
    /// The point of the whole test is the last case, <see cref="EveryDeclaredAffinity_IsBackedByASourceCall"/>:
    /// a NEW ability cannot ship an undeclared affinity, and equally cannot declare one that no code applies.
    /// </summary>
    [TestClass]
    public class ClassAbilityAffinityDeclarationTests
    {
        private const string AbilitiesRelativePath = "Source/ACE.Server/ClassAbilities/Abilities";

        /// <summary>Matches a scaling call and captures the Skill enum member it is passed.</summary>
        private static readonly Regex ScalingCall = new Regex(
            @"GetClassAbilityScaling\s*\(\s*Skill\.(?<skill>\w+)", RegexOptions.Compiled);

        /// <summary>Matches the definition's own id, e.g. "Id = ClassAbilityId.Thorns,".</summary>
        private static readonly Regex DefinitionId = new Regex(
            @"Id\s*=\s*ClassAbilityId\.(?<id>\w+)", RegexOptions.Compiled);

        /// <summary>
        /// Handler files that legitimately contain a scaling call for an ability OTHER than the one they
        /// define. Keyed by file name; the value names the ability the file defines, the skill that ability's
        /// OWN affinity is, and which of the file's other skills belong to someone else.
        ///
        /// Every entry here is a deliberate exception. Adding one means the file really does compute another
        /// ability's rider - not that the declaration was inconvenient to get right.
        /// </summary>
        private static readonly Dictionary<string, (Skill Own, Skill[] Foreign, string Why)> ForeignScalingCalls =
            new Dictionary<string, (Skill, Skill[], string)>(StringComparer.OrdinalIgnoreCase)
        {
            // Parry and Shield Block share ONE pooled avoidance cap, and ClassAbilityAvoidance.Pooled scales
            // the two shares against each other - so neither readout can compute its own effective chance
            // without also computing the other's. Each file therefore carries both riders.
            ["ParryAbility.cs"] = (Skill.Deception, new[] { Skill.ArmorTinkering },
                "GetReadout computes Shield Block's Armor Tinkering rider to feed ClassAbilityAvoidance.Pooled"),

            ["ShieldBlockAbility.cs"] = (Skill.ArmorTinkering, new[] { Skill.Deception },
                "GetReadout computes Parry's Deception rider to feed ClassAbilityAvoidance.Pooled"),

            // The 2026-08-17 Rogue rework moved Acid Proc's Item Tinkering rider off its proc chance and onto
            // a poison-DAMAGE bonus that multiplies Poison Weapon's flat. Both live in PoisonWeaponAbility
            // because that is the single shared source the per-hit proc and every DoT tick read.
            ["PoisonWeaponAbility.cs"] = (Skill.Alchemy, new[] { Skill.ItemTinkering },
                "FlatBonus/GetReadout compute Acid Proc's Item Tinkering rider, which multiplies this ability's flat"),
        };

        /// <summary>
        /// Abilities whose scaling call is NOT in their own handler file. The value is the affinity skill and
        /// the repo-relative file that actually carries the call - asserted to still contain it, so moving or
        /// deleting the call breaks the build rather than leaving a stale declaration behind.
        /// </summary>
        private static readonly Dictionary<ClassAbilityId, (Skill Skill, string SourceFile)> ScalingCallsOutsideTheHandler =
            new Dictionary<ClassAbilityId, (Skill, string)>
        {
            [ClassAbilityId.AcidProc] = (Skill.ItemTinkering,
                "Source/ACE.Server/ClassAbilities/Abilities/PoisonWeaponAbility.cs"),

            [ClassAbilityId.Frenzy] = (Skill.Recklessness,
                "Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs"),
        };

        /// <summary>
        /// Every handler file that calls GetClassAbilityScaling declares the SAME skill that call passes.
        /// </summary>
        [TestMethod]
        public void EveryHandlerScalingCall_IsDeclaredAsAffinitySkill()
        {
            foreach (var file in AbilityHandlerFiles())
            {
                var text = File.ReadAllText(file.FullName);

                var skills = ScalingCall.Matches(text)
                    .Select(m => ParseSkill(m.Groups["skill"].Value, file.Name))
                    .Distinct()
                    .OrderBy(s => (int)s)
                    .ToArray();

                if (skills.Length == 0)
                    continue;

                var definition = DefinitionFor(file, text);

                if (ForeignScalingCalls.TryGetValue(file.Name, out var exception))
                {
                    Assert.AreEqual(exception.Own, definition.AffinitySkill,
                        $"{file.Name}: allowlisted as declaring {exception.Own} ({exception.Why}), but the definition declares {Describe(definition.AffinitySkill)}.");

                    var unexpected = skills.Except(new[] { exception.Own }).Except(exception.Foreign).ToArray();

                    Assert.AreEqual(0, unexpected.Length,
                        $"{file.Name}: scaling calls for {string.Join(", ", unexpected)} are neither this ability's affinity ({exception.Own}) nor allowlisted as another ability's rider. " +
                        "Either declare it, or add it to ForeignScalingCalls with the reason.");

                    continue;
                }

                Assert.AreEqual(1, skills.Length,
                    $"{file.Name} calls GetClassAbilityScaling with more than one skill ({string.Join(", ", skills)}) and is not in ForeignScalingCalls. " +
                    "Pick the skill that scales the ability's primary advertised effect, declare it, and add an allowlist entry saying which call belongs to whom.");

                Assert.AreEqual(skills[0], definition.AffinitySkill,
                    $"{file.Name} ({definition.Name}) calls GetClassAbilityScaling(Skill.{skills[0]}, ...) but declares AffinitySkill = {Describe(definition.AffinitySkill)}.");
            }
        }

        /// <summary>
        /// A handler file with no scaling call at all declares no affinity - unless it is in
        /// <see cref="ScalingCallsOutsideTheHandler"/>, in which case the file named there must still carry
        /// the call.
        /// </summary>
        [TestMethod]
        public void HandlersWithNoScalingCall_DeclareNoAffinity()
        {
            var repoRoot = RepoRoot();

            foreach (var file in AbilityHandlerFiles())
            {
                var text = File.ReadAllText(file.FullName);

                if (ScalingCall.IsMatch(text))
                    continue;

                var definition = DefinitionFor(file, text);

                if (definition == null)
                    continue;   // a generator (Enhanced / Bundle / Rating) rather than a single definition

                if (ScalingCallsOutsideTheHandler.TryGetValue(definition.Id, out var external))
                {
                    Assert.AreEqual(external.Skill, definition.AffinitySkill,
                        $"{definition.Name}: listed in ScalingCallsOutsideTheHandler as {external.Skill} but declares {Describe(definition.AffinitySkill)}.");

                    var sourcePath = Path.Combine(repoRoot, external.SourceFile.Replace('/', Path.DirectorySeparatorChar));

                    Assert.IsTrue(File.Exists(sourcePath), $"{definition.Name}: {external.SourceFile} does not exist.");

                    var expectedCall = $"GetClassAbilityScaling(Skill.{external.Skill}";

                    StringAssert.Contains(File.ReadAllText(sourcePath).Replace(" ", string.Empty), expectedCall.Replace(" ", string.Empty),
                        $"{definition.Name}: {external.SourceFile} no longer contains \"{expectedCall}\". The rider moved or was removed - update the declaration and this table together.");

                    continue;
                }

                Assert.IsNull(definition.AffinitySkill,
                    $"{file.Name} ({definition.Name}) declares AffinitySkill = {definition.AffinitySkill} but calls GetClassAbilityScaling nowhere in its own file. " +
                    "If the rider really is computed elsewhere, add it to ScalingCallsOutsideTheHandler with the file that carries the call.");
            }
        }

        /// <summary>
        /// The completeness half: every ability in the LIVE registry that declares an affinity is backed
        /// either by a call in its own handler file or by an explicit ScalingCallsOutsideTheHandler row. This
        /// is what stops a declaration being invented for an ability nothing scales - the failure mode a
        /// per-file scan cannot see, because a file that never mentions the ability is never visited.
        /// </summary>
        [TestMethod]
        public void EveryDeclaredAffinity_IsBackedByASourceCall()
        {
            var backed = new Dictionary<ClassAbilityId, Skill>();

            foreach (var file in AbilityHandlerFiles())
            {
                var text = File.ReadAllText(file.FullName);

                var definition = DefinitionFor(file, text);

                if (definition == null)
                    continue;

                var own = ForeignScalingCalls.TryGetValue(file.Name, out var exception)
                    ? new[] { exception.Own }
                    : ScalingCall.Matches(text).Select(m => ParseSkill(m.Groups["skill"].Value, file.Name)).Distinct().ToArray();

                if (own.Length == 1)
                    backed[definition.Id] = own[0];
            }

            foreach (var kvp in ScalingCallsOutsideTheHandler)
                backed[kvp.Key] = kvp.Value.Skill;

            var declared = ClassAbilityRegistry.Abilities.Values
                .Where(d => d.AffinitySkill.HasValue)
                .OrderBy(d => (int)d.Id)
                .ToArray();

            foreach (var definition in declared)
            {
                Assert.IsTrue(backed.TryGetValue(definition.Id, out var sourceSkill),
                    $"{definition.Name} declares AffinitySkill = {definition.AffinitySkill} but no GetClassAbilityScaling call was found for it.");

                Assert.AreEqual(sourceSkill, definition.AffinitySkill, $"{definition.Name}: declaration disagrees with the source call.");
            }

            // ...and nothing that IS backed may quietly leave the field null.
            foreach (var kvp in backed)
            {
                if (!ClassAbilityRegistry.Abilities.TryGetValue(kvp.Key, out var definition))
                    continue;   // retired / unregistered

                Assert.AreEqual(kvp.Value, definition.AffinitySkill,
                    $"{definition.Name} is scaled by Skill.{kvp.Value} in source but declares {Describe(definition.AffinitySkill)}.");
            }
        }

        /// <summary>
        /// The generated families (Enhanced stat, Training bundles, Ratings) carry no affinity: they read a
        /// skill in order to RAISE it, which is not a rider on a separate mechanic.
        /// </summary>
        [TestMethod]
        public void GeneratedFamilies_DeclareNoAffinity()
        {
            // The exact Category strings the three generators stamp: EnhancedStatAbility writes "Enhanced
            // Skill" / "Enhanced Attribute" / "Enhanced Vital", BundleStatAbility writes "Bundle", and
            // RatingAbility writes "Rating".
            var generatedCategories = new[] { "Enhanced Skill", "Enhanced Attribute", "Enhanced Vital", "Bundle", "Rating" };

            var generated = ClassAbilityRegistry.Abilities.Values
                .Where(d => generatedCategories.Contains(d.Category, StringComparer.Ordinal))
                .ToArray();

            // Guard against a vacuous pass: if a generator renames its category, this test must fail loudly
            // rather than quietly stop covering ~60 abilities.
            Assert.IsTrue(generated.Length >= 50,
                $"expected the generated families to account for most of the registry, found only {generated.Length}. Did a Category string change?");

            foreach (var definition in generated)
                Assert.IsNull(definition.AffinitySkill, $"{definition.Name} ({definition.Category}) should carry no affinity.");
        }

        private static Skill ParseSkill(string name, string file)
        {
            if (!System.Enum.TryParse<Skill>(name, out var skill))
                Assert.Fail($"{file}: GetClassAbilityScaling(Skill.{name}) names no member of ACE.Entity.Enum.Skill.");

            return skill;
        }

        private static string Describe(Skill? skill) => skill.HasValue ? skill.Value.ToString() : "null";

        /// <summary>
        /// Resolves the definition a handler file declares, by reading its "Id = ClassAbilityId.X" out of the
        /// source and looking that id up in the live registry. Returns null for a file that declares no single
        /// id (the three generators) or whose id is not registered.
        /// </summary>
        private static ClassAbilityDefinition DefinitionFor(FileInfo file, string text)
        {
            var ids = DefinitionId.Matches(text)
                .Select(m => m.Groups["id"].Value)
                .Distinct()
                .ToArray();

            if (ids.Length != 1)
                return null;

            if (!System.Enum.TryParse<ClassAbilityId>(ids[0], out var id))
            {
                Assert.Fail($"{file.Name}: ClassAbilityId.{ids[0]} names no member of the enum.");
                return null;
            }

            return ClassAbilityRegistry.Abilities.TryGetValue(id, out var definition) ? definition : null;
        }

        private static IEnumerable<FileInfo> AbilityHandlerFiles()
        {
            var dir = FindInSourceTree(AbilitiesRelativePath);

            Assert.IsNotNull(dir, $"Could not find {AbilitiesRelativePath} by walking up from {AppContext.BaseDirectory}.");

            // RECURSIVE, and that is load-bearing. GetFiles("*.cs") defaults to TopDirectoryOnly; Abilities\
            // is flat today, so a handler filed under Abilities\<Something>\FooAbility.cs would be invisible
            // to every test in this class and all three would pass green on a wrong or missing declaration.
            var files = new DirectoryInfo(dir)
                .GetFiles("*.cs", SearchOption.AllDirectories)
                .OrderBy(f => f.FullName, StringComparer.Ordinal)
                .ToArray();

            // ForeignScalingCalls is keyed by file NAME, which only identifies a file uniquely while the tree
            // has no duplicate names. Recursing makes that assumption falsifiable, so pin it: two handlers
            // called the same thing in different folders would silently share one allowlist entry.
            var duplicates = files
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();

            Assert.AreEqual(0, duplicates.Length,
                $"duplicate handler file name(s) under {AbilitiesRelativePath}: {string.Join(", ", duplicates)}. " +
                "ForeignScalingCalls is keyed by file name and cannot tell them apart - rename one.");

            return files;
        }

        internal static string RepoRoot()
        {
            var dir = FindInSourceTree(AbilitiesRelativePath);

            Assert.IsNotNull(dir, $"Could not find {AbilitiesRelativePath} by walking up from {AppContext.BaseDirectory}.");

            // AbilitiesRelativePath has four segments, so four parents up from the located directory is the
            // repo root the walk started from.
            return new DirectoryInfo(dir).Parent.Parent.Parent.Parent.FullName;
        }

        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works regardless of
        /// the bin\Debug vs bin\x64\Debug output layout. Same idiom as PropertyRegistryTests.FindInSourceTree,
        /// except it resolves a DIRECTORY. CLAUDE.md forbids --artifacts-path on test runs precisely because it
        /// moves the assembly out of the tree and breaks this walk.
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
