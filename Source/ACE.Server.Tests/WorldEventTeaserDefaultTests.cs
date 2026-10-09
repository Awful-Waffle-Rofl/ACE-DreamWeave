using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.WorldEvents;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the World Events teaser lead default (60 s) at its three sources: the PropertyManager code
    /// default, the compiled fallback constant, and the resolved tunable. Kept in its own class because it
    /// seeds PropertyManager statics, which would change the no-config catch path other WorldEvent tests rely on.
    /// </summary>
    [TestClass]
    public class WorldEventTeaserDefaultTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void TeaserLeadDefault_IsSixtySeconds_AtEverySource()
        {
            var seeded = PropertyManager.GetLong(WorldEvent.TeaserLeadTunable).Item;

            Assert.AreEqual(60L, seeded, "PropertyManager code default");
            Assert.AreEqual((int)seeded, WorldEvent.DefaultTeaserLeadSeconds, "compiled fallback constant must match the code default");
            Assert.AreEqual(60, WorldEvent.TunableTeaserLeadSeconds(), "resolved tunable");
        }
    }
}