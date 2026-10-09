using System;
using System.IO;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Market's configuration surface (DESIGN section 12). Two things are worth a test: that
    /// the module is OFF by default, and that the shipped example file parses into the section the
    /// server reads. Both fail silently otherwise.
    /// </summary>
    [TestClass]
    public class MarketConfigTests
    {
        [TestMethod]
        public void Market_IsDisabledByDefault()
        {
            var config = new MasterConfiguration();

            Assert.IsNotNull(config.Market, "MasterConfiguration must carry a Market section");
            Assert.IsFalse(config.Market.Enabled,
                "the module is config-gated and adds nothing to the server's behaviour when disabled");
            Assert.AreEqual("http://127.0.0.1:5090", config.Market.ListenUrl,
                "the default binding is loopback, so a misconfigured deployment fails closed");
            Assert.AreEqual(8, config.Market.MaxInFlight);
            Assert.AreEqual(120, config.Market.WriteRatePerMinute);
            Assert.AreEqual(3000, config.Market.RequestTimeoutMs);
            Assert.AreEqual(5, config.Market.LoginRatePerMinutePerAccount);
            Assert.AreEqual(30, config.Market.LoginRatePerMinutePerIp);
            Assert.AreEqual(string.Empty, config.Market.SharedKey,
                "no default key: an empty key must refuse to start rather than ship a known secret");
        }

        [TestMethod]
        public void ConfigExample_ParsesAndCarriesTheMarketSection()
        {
            var path = FindConfigExample();
            var text = File.ReadAllText(path);

            var parsed = JsonSerializer.Deserialize<MasterConfiguration>(text, ConfigManager.SerializerOptions);

            Assert.IsNotNull(parsed, "Config.js.example must deserialize");
            Assert.IsNotNull(parsed.Market, "Config.js.example must document the Market section");
            Assert.IsFalse(parsed.Market.Enabled, "the shipped example must not enable the market");
            Assert.AreEqual(string.Empty, parsed.Market.SharedKey, "the example must never carry a real key");
        }

        [TestMethod]
        public void ConfigExample_MatchesTheDesignedDefaults()
        {
            // A documented key whose value disagrees with the code default is a key that quietly
            // does nothing; DESIGN section 7 fixes these five numbers and the web app assumes them.
            var parsed = JsonSerializer.Deserialize<MasterConfiguration>(File.ReadAllText(FindConfigExample()), ConfigManager.SerializerOptions);

            Assert.AreEqual("http://127.0.0.1:5090", parsed.Market.ListenUrl);
            Assert.AreEqual(8, parsed.Market.MaxInFlight);
            Assert.AreEqual(120, parsed.Market.WriteRatePerMinute);
            Assert.AreEqual(3000, parsed.Market.RequestTimeoutMs);
            Assert.AreEqual(5, parsed.Market.LoginRatePerMinutePerAccount);
            Assert.AreEqual(30, parsed.Market.LoginRatePerMinutePerIp);
        }

        [TestMethod]
        public void Tunables_AreRegistered()
        {
            // ModifyLong/ModifyBool return false for a key that is not in the default table, which
            // is how a test proves the key was registered rather than merely written down.
            Assert.IsTrue(PropertyManager.ModifyBool("market_enabled", false), "market_enabled is missing from DefaultBooleanProperties");
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_listings_per_account", 100), "market_max_listings_per_account is missing from DefaultLongProperties");
            Assert.IsTrue(PropertyManager.ModifyLong("market_max_price_mmd", 1000), "market_max_price_mmd is missing from DefaultLongProperties");
        }

        [TestMethod]
        public void MarketEnabled_DefaultsToFalseInTheDefaultTable()
        {
            // The runtime kill switch must ship off too, or a shard with no config row turns the
            // market on the moment Market.Enabled is set in Config.js.
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.TryGetValue("market_enabled", out var property));
            Assert.IsFalse(property.Item);
        }

        private static string FindConfigExample()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "Config.js.example")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server/Config.js.example by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Server", "Config.js.example");
        }
    }
}
