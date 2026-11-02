using ACE.Server.Managers;
using ACE.Server.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Outbound
{
    /// <summary>
    /// The outbound_ring_* settings: registered defaults (new settings ship ON), clamping, and Refresh reading seeded
    /// values. Every key Refresh reads is seeded here and restored in a finally, because PropertyManager's caches are
    /// process-wide.
    /// </summary>
    [TestClass]
    public class OutboundRingSettingsTests
    {
        [TestMethod]
        public void Settings_AreRegisteredWithTheirDefaults_AndMarkedNonRetail()
        {
            Assert.AreEqual(true, DefaultPropertyManager.DefaultBooleanProperties[OutboundRingSettings.EnabledKey].Item);
            Assert.AreEqual(512L, DefaultPropertyManager.DefaultLongProperties[OutboundRingSettings.EntriesKey].Item);
            Assert.AreEqual(128L, DefaultPropertyManager.DefaultLongProperties[OutboundRingSettings.DumpEntriesKey].Item);
            Assert.AreEqual(300L, DefaultPropertyManager.DefaultLongProperties[OutboundRingSettings.ControlSecondsKey].Item);

            StringAssert.StartsWith(DefaultPropertyManager.DefaultBooleanProperties[OutboundRingSettings.EnabledKey].Description, "(non-retail function)");
            foreach (var key in new[] { OutboundRingSettings.EntriesKey, OutboundRingSettings.DumpEntriesKey, OutboundRingSettings.ControlSecondsKey })
                StringAssert.StartsWith(DefaultPropertyManager.DefaultLongProperties[key].Description, "(non-retail function)");
        }

        [TestMethod]
        public void ClampEntries_64To4096_RoundedUpToAPowerOfTwo()
        {
            Assert.AreEqual(64, OutboundRingSettings.ClampEntries(0));
            Assert.AreEqual(64, OutboundRingSettings.ClampEntries(-5));
            Assert.AreEqual(64, OutboundRingSettings.ClampEntries(64));
            Assert.AreEqual(128, OutboundRingSettings.ClampEntries(65));
            Assert.AreEqual(512, OutboundRingSettings.ClampEntries(512));
            Assert.AreEqual(1024, OutboundRingSettings.ClampEntries(600));
            Assert.AreEqual(4096, OutboundRingSettings.ClampEntries(4096));
            Assert.AreEqual(4096, OutboundRingSettings.ClampEntries(long.MaxValue));
        }

        [TestMethod]
        public void ClampDumpEntries_And_ControlSeconds()
        {
            Assert.AreEqual(16, OutboundRingSettings.ClampDumpEntries(1));
            Assert.AreEqual(128, OutboundRingSettings.ClampDumpEntries(128));
            Assert.AreEqual(512, OutboundRingSettings.ClampDumpEntries(100000));

            Assert.AreEqual(0, OutboundRingSettings.ClampControlSeconds(0));
            Assert.AreEqual(0, OutboundRingSettings.ClampControlSeconds(-1));
            Assert.AreEqual(300, OutboundRingSettings.ClampControlSeconds(300));
            Assert.AreEqual(86400, OutboundRingSettings.ClampControlSeconds(long.MaxValue));
        }

        [TestMethod]
        public void Refresh_ReadsAndClampsSeededValues_ThenDefaults()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool(OutboundRingSettings.EnabledKey, false));
                Assert.IsTrue(PropertyManager.ModifyLong(OutboundRingSettings.EntriesKey, 600));
                Assert.IsTrue(PropertyManager.ModifyLong(OutboundRingSettings.DumpEntriesKey, 9999));
                Assert.IsTrue(PropertyManager.ModifyLong(OutboundRingSettings.ControlSecondsKey, -3));

                OutboundRingSettings.Refresh();

                Assert.IsFalse(OutboundRingSettings.Enabled);
                Assert.AreEqual(1024, OutboundRingSettings.Entries);
                Assert.AreEqual(512, OutboundRingSettings.DumpEntries);
                Assert.AreEqual(0, OutboundRingSettings.ControlSeconds);

                Assert.IsTrue(PropertyManager.ModifyBool(OutboundRingSettings.EnabledKey, OutboundRingSettings.DefaultEnabled));
                Assert.IsTrue(PropertyManager.ModifyLong(OutboundRingSettings.EntriesKey, OutboundRingSettings.DefaultEntries));
                Assert.IsTrue(PropertyManager.ModifyLong(OutboundRingSettings.DumpEntriesKey, OutboundRingSettings.DefaultDumpEntries));
                Assert.IsTrue(PropertyManager.ModifyLong(OutboundRingSettings.ControlSecondsKey, OutboundRingSettings.DefaultControlSeconds));

                OutboundRingSettings.Refresh();

                Assert.IsTrue(OutboundRingSettings.Enabled);
                Assert.AreEqual(512, OutboundRingSettings.Entries);
                Assert.AreEqual(128, OutboundRingSettings.DumpEntries);
                Assert.AreEqual(300, OutboundRingSettings.ControlSeconds);
            }
            finally
            {
                PropertyManager.ModifyBool(OutboundRingSettings.EnabledKey, OutboundRingSettings.DefaultEnabled);
                PropertyManager.ModifyLong(OutboundRingSettings.EntriesKey, OutboundRingSettings.DefaultEntries);
                PropertyManager.ModifyLong(OutboundRingSettings.DumpEntriesKey, OutboundRingSettings.DefaultDumpEntries);
                PropertyManager.ModifyLong(OutboundRingSettings.ControlSecondsKey, OutboundRingSettings.DefaultControlSeconds);
                OutboundRingSettings.Refresh();
            }
        }
    }
}
