using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Factories;

namespace ACE.Server.Tests
{
    [TestClass]
    public class StarterGearTests
    {
        [TestMethod]
        public void CanParseStarterGearJson()
        {
            // exercises the production loader: the ACE.Server project reference copies
            // starterGear.json to the test output directory, and StarterGearFactory reads it
            // from the assembly location with the same serializer options the server uses
            var config = StarterGearFactory.GetStarterGearConfiguration();

            Assert.IsNotNull(config, "starterGear.json failed to load or parse");
            Assert.IsNotNull(config.Skills, "starterGear.json parsed but has no skills element");
            Assert.AreNotEqual(0, config.Skills.Count, "starterGear.json parsed but contains no skills");
        }
    }
}
