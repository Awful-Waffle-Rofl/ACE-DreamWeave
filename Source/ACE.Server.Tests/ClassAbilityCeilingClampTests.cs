using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    [TestClass]
    public class ClassAbilityCeilingClampTests
    {
        [TestInitialize]
        public void Init() => DefaultPropertyManager.LoadDefaultProperties();

        private static IEnumerable<CeilingProbe> AllProbes() =>
            CeilingProbesArcher.All
                .Concat(CeilingProbesRogue.All)
                .Concat(CeilingProbesVanguard.All)
                .Concat(CeilingProbesBerserker.All)
                .Concat(CeilingProbesArchmage.All)
                .Concat(CeilingProbesVoidSummon.All)
                .Concat(CeilingProbesBloodMage.All)
                .Concat(CeilingProbesSpellsword.All);

        [TestMethod]
        public void EveryDeclaredCeiling_ClampsAtItsDeclaredValue()
        {
            foreach (var p in AllProbes())
                Assert.AreEqual(p.DeclaredMax(), p.ClampedAtSaturation(), 1e-9, $"{p.Ability}/{p.CeilingId}");
        }

        [TestMethod]
        public void EveryDeclaredCeiling_HasExactlyOneProbe_AndViceVersa()
        {
            var declared = ClassAbilityRegistry.Abilities.Values
                .Where(d => d.Ceilings != null)
                .SelectMany(d => d.Ceilings.Select(c => (d.Id, c.Id)))
                .OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();

            var probed = AllProbes().Select(p => (p.Ability, p.CeilingId)).OrderBy(x => x.Ability).ThenBy(x => x.CeilingId).ToList();

            CollectionAssert.AreEqual(declared, probed, "declared (ability, ceiling id) pairs must match probes one to one");
            Assert.AreEqual(probed.Count, probed.Distinct().Count(), "duplicate probe");
        }
    }
}
