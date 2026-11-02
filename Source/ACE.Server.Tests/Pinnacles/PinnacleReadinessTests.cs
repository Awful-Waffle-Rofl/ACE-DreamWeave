using System.Linq;

using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Grael;
using ACE.Server.Pinnacles.Matron;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>The entry readiness check covers every wcid a script spawns (RequiredWcids). Pure.</summary>
    [TestClass]
    public class PinnacleReadinessTests
    {
        [TestMethod]
        public void MatronRequiredWcids_ListEveryPropHostAndWaveAdd()
        {
            var required = MatronData.Script().RequiredWcids;

            foreach (var wcid in new[] { 1007551u, 1007552u, 1007553u, 1007557u, 1007558u, 4451u, 1006967u, 1006968u, 1006952u, 1007554u, 1007555u, 1007556u, 1006961u })
                CollectionAssert.Contains(required.ToList(), wcid);

            foreach (var wave in MatronData.Waves)
                foreach (var (wcid, _) in wave)
                    CollectionAssert.Contains(required.ToList(), wcid);

            Assert.IsFalse(required.Contains(1007550u), "the head is checked separately");
            Assert.AreEqual(required.Count, required.Distinct().Count());
        }

        [TestMethod]
        public void MissingPlatformWcid_RefusesEntry_AndEveryOtherWcidIsFound()
        {
            var data = MatronData.Script();

            Assert.AreEqual(MatronData.PlatformWcid, PinnacleEncounter.FirstMissingWeenie(data.HeadWcid, w => w != MatronData.PlatformWcid, data.RequiredWcids));
            Assert.AreEqual(0u, PinnacleEncounter.FirstMissingWeenie(data.HeadWcid, _ => true, data.RequiredWcids));

            foreach (var wcid in data.RequiredWcids)
                Assert.AreEqual(wcid, PinnacleEncounter.FirstMissingWeenie(data.HeadWcid, w => w != wcid, data.RequiredWcids));
        }

        [TestMethod]
        public void GraelReadiness_IsUnchanged()
        {
            var data = new GraelScript().Data;

            Assert.AreEqual(0, data.RequiredWcids.Count);
            Assert.AreEqual(data.HeadWcid, PinnacleEncounter.FirstMissingWeenie(data.HeadWcid, _ => false, data.RequiredWcids));
            Assert.AreEqual(0u, PinnacleEncounter.FirstMissingWeenie(data.HeadWcid, _ => true, data.RequiredWcids));
        }
    }
}