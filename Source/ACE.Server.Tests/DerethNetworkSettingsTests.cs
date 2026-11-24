using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Pins the three Dereth Network live settings in the static defaults table. Reads the dictionary only; PropertyManager.GetBool throws on a cache miss in unit tests.</summary>
    [TestClass]
    public class DerethNetworkSettingsTests
    {
        [DataTestMethod]
        [DataRow("dereth_network_enabled")]
        [DataRow("dereth_network_traffic_log")]
        [DataRow("dereth_network_room_mirroring")]
        public void DerethNetworkKey_IsRegisteredBoolDefaultingToTrue(string key)
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(key, out var property), key + " is not registered in DefaultBooleanProperties");
            Assert.IsTrue(property.Item, key + " must default to true");
            StringAssert.Contains(property.Description, "Dereth.Network mod", key + " description must say it needs the mod");
        }
    }
}
