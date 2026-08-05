using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.World;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.Tests
{
    [TestClass]
    public class WeenieSearchTests
    {
        private static readonly WorldDatabase worldDb = new WorldDatabase();

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void GetWeenie_Pyreal_ById_ReturnsObject()
        {
            TestEnvironment.RequireWorldDatabase(worldDb);

            var result = worldDb.GetWeenie(273);
            Assert.IsNotNull(result);

            var stringName = result.WeeniePropertiesString.FirstOrDefault(x => x.Type == (ushort)PropertyString.Name)?.Value;

            Assert.AreEqual("Pyreal", stringName);
            Assert.AreEqual("Pyreal", result.GetProperty(PropertyString.Name));
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void GetWeenie_Pyreal_ByName_ReturnsObject()
        {
            TestEnvironment.RequireWorldDatabase(worldDb);

            var result = worldDb.GetWeenie("coinstack");
            Assert.IsNotNull(result);

            var stringName = result.WeeniePropertiesString.FirstOrDefault(x => x.Type == (ushort)PropertyString.Name)?.Value;

            Assert.AreEqual("Pyreal", stringName);
            Assert.AreEqual("Pyreal", result.GetProperty(PropertyString.Name));
        }
    }
}
