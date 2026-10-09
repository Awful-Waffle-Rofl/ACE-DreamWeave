using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure source-text guard for the 2026-10-02 owner ruling that RETIRED Soul Jump's and Void
    /// Damage's own affinity rate keys (class_ability_affinity_souljump_rate_per_trained/_per_spec and
    /// class_ability_affinity_voiddamage_rate_per_trained/_per_spec) rather than standardizing their
    /// VALUES onto the shared pair. Both abilities now call the single-argument
    /// GetClassAbilityAffinityMultiplier(Skill) overload, so no key matching
    /// class_ability_affinity_*_rate_per_* other than the shared
    /// class_ability_affinity_rate_per_trained / _per_spec pair should appear in either ability's file
    /// or in the Void Damage block of Player_ClassAbilityBuffs.cs.
    ///
    /// SCOPED TO QUOTED STRING LITERALS, deliberately: PropertyManager.GetDouble/ModifyDouble always
    /// take the key as a quoted string literal, so matching only inside quotes catches a real
    /// reintroduced CODE reference while leaving alone the doc comments in these same files that name
    /// the retired keys in prose (unquoted) to explain the 2026-10-02 history - those are expected to
    /// stay and are not the thing this guard protects against.
    ///
    /// NEGATIVE CONTROL: <see cref="Scan_DetectsAReintroducedPerAbilityKey"/> feeds the scan regex a
    /// synthetic snippet that reintroduces a per-ability key as a quoted literal, and asserts the scan
    /// DOES flag it - a scan that always returns clean because the pattern never matches anything would
    /// pass every real case here for the wrong reason.
    /// </summary>
    [TestClass]
    public class SoulJumpVoidDamageAffinityKeyScanTests
    {
        // Matches a QUOTED class_ability_affinity_*_rate_per_* key EXCEPT the shared
        // class_ability_affinity_rate_per_trained / _per_spec pair itself.
        private static readonly Regex OffStandardAffinityRateKey =
            new Regex("\"class_ability_affinity_(?!rate_per_(trained|spec)\\b)\\w*_rate_per_\\w+\"", RegexOptions.Compiled);

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "ClassAbilities", "Abilities", "SoulJumpAbility.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/ClassAbilities/Abilities/SoulJumpAbility.cs by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        [TestMethod]
        public void SoulJumpAbility_ReferencesNoOffStandardAffinityRateKey()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "ClassAbilities", "Abilities", "SoulJumpAbility.cs"));

            var match = OffStandardAffinityRateKey.Match(source);
            Assert.IsFalse(match.Success, $"SoulJumpAbility.cs still references an off-standard affinity rate key: '{match.Value}'");
        }

        [TestMethod]
        public void VoidDamageAbility_ReferencesNoOffStandardAffinityRateKey()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "ClassAbilities", "Abilities", "VoidDamageAbility.cs"));

            var match = OffStandardAffinityRateKey.Match(source);
            Assert.IsFalse(match.Success, $"VoidDamageAbility.cs still references an off-standard affinity rate key: '{match.Value}'");
        }

        /// <summary>
        /// Player_ClassAbilityBuffs.cs is a large shared file covering every migrated ability's live
        /// damage path, not just Void Damage's - so this scans the whole file rather than isolating the
        /// Void Damage block (the only off-standard keys that could appear anywhere in it are the two
        /// retired Void Damage ones; Bloodlust's own pair is read in BloodlustAbility.cs:57/:149, never
        /// in this file).
        /// </summary>
        [TestMethod]
        public void PlayerClassAbilityBuffs_ReferencesNoOffStandardAffinityRateKey()
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "WorldObjects", "Player_ClassAbilityBuffs.cs"));

            var match = OffStandardAffinityRateKey.Match(source);
            Assert.IsFalse(match.Success, $"Player_ClassAbilityBuffs.cs still references an off-standard affinity rate key: '{match.Value}'");
        }

        [TestMethod]
        public void Scan_DetectsAReintroducedPerAbilityKey()
        {
            const string reintroduced = "PropertyManager.GetDouble(\"class_ability_affinity_voiddamage_rate_per_trained\").Item";

            var match = OffStandardAffinityRateKey.Match(reintroduced);
            Assert.IsTrue(match.Success, "the scan must flag a reintroduced per-ability affinity rate key - a scan that never matches anything is not a guard");
        }

        [TestMethod]
        public void Scan_DoesNotFlagTheSharedPairItself()
        {
            const string shared = "PropertyManager.GetDouble(\"class_ability_affinity_rate_per_trained\").Item + " +
                                   "PropertyManager.GetDouble(\"class_ability_affinity_rate_per_spec\").Item";

            var match = OffStandardAffinityRateKey.Match(shared);
            Assert.IsFalse(match.Success, $"the scan must not flag the shared pair itself: '{match.Value}'");
        }
    }
}
