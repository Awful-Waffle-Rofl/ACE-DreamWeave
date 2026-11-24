using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Readout presence for the Archmage ceilings: every declared ceiling must appear in GetReadout's Ceilings at its
    /// declared max. Run through GetReadout(null, rank), which 12 test files already do. A handler that throws on a
    /// null player is on NullPlayerUnsupported with its reason; binding for those is still proven by the probe in
    /// CeilingProbesArchmage (ClassAbilityCeilingClampTests), so only presence goes unchecked here.
    /// </summary>
    [TestClass]
    public class ArchmageCeilingReadoutTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static readonly Dictionary<ClassAbilityId, string> NullPlayerUnsupported = new Dictionary<ClassAbilityId, string>
        {
            { ClassAbilityId.ManaBarrier, "reads player.GetClassAbilityAffinityMultiplier unguarded" },
            { ClassAbilityId.SpellAoe, "reads player.GetClassAbilityAffinityMultiplier unguarded" },
            { ClassAbilityId.EchoCast, "reads player.GetClassAbilityAffinityMultiplier unguarded" },
        };

        [TestMethod]
        public void GetReadoutNullPlayer_ReportsEveryDeclaredCeiling_AtDeclaredMax() =>
            CeilingProbeSupport.AssertReadoutReportsEveryDeclaredCeiling(ClassAbilityClass.Archmage, NullPlayerUnsupported);

        [TestMethod]
        public void NullPlayerUnsupported_StaysHonest() =>
            CeilingProbeSupport.AssertNullPlayerUnsupportedIsHonest(ClassAbilityClass.Archmage, NullPlayerUnsupported);
    }
}
