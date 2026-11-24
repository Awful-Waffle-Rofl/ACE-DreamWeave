using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Readout presence for the BloodMage ceilings: every declared ceiling must appear in GetReadout's Ceilings at its
    /// declared max. Run through GetReadout(null, rank), which 12 test files already do. A handler that throws on a
    /// null player is on NullPlayerUnsupported with its reason; binding for those is still proven by the probe in
    /// CeilingProbesBloodMage (ClassAbilityCeilingClampTests), so only presence goes unchecked here.
    /// </summary>
    [TestClass]
    public class BloodMageCeilingReadoutTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static readonly Dictionary<ClassAbilityId, string> NullPlayerUnsupported = new Dictionary<ClassAbilityId, string>();

        [TestMethod]
        public void GetReadoutNullPlayer_ReportsEveryDeclaredCeiling_AtDeclaredMax() =>
            CeilingProbeSupport.AssertReadoutReportsEveryDeclaredCeiling(ClassAbilityClass.BloodMage, NullPlayerUnsupported);

        [TestMethod]
        public void NullPlayerUnsupported_StaysHonest() =>
            CeilingProbeSupport.AssertNullPlayerUnsupportedIsHonest(ClassAbilityClass.BloodMage, NullPlayerUnsupported);
    }
}
