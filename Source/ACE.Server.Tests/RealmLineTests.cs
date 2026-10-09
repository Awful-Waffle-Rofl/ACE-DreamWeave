using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Realms;

namespace ACE.Server.Tests
{
    /// <summary>
    /// RealmLine.Format only - it's pure. ForPosition needs RealmManager, which the unit-test
    /// harness can't stand up (no live Player/manager state), so it is not covered here.
    ///
    /// The format is a consumed contract (see RealmLine's doc comment): name= must stay last and
    /// survive intact even when it contains spaces, since a parser takes the whole line remainder
    /// as the name.
    /// </summary>
    [TestClass]
    public class RealmLineTests
    {
        [TestMethod]
        public void Format_BaseRealm()
        {
            var line = RealmLine.Format(0, "Base", 0, false);

            Assert.AreEqual("[REALM] id=0 inst=0 eph=0 name=Base", line);
        }

        [TestMethod]
        public void Format_NonZeroRealmAndInstance()
        {
            var line = RealmLine.Format(7, "Weave", 42, false);

            Assert.AreEqual("[REALM] id=7 inst=42 eph=0 name=Weave", line);
        }

        [TestMethod]
        public void Format_Ephemeral()
        {
            var line = RealmLine.Format(3, "Loom", 1, true);

            Assert.AreEqual("[REALM] id=3 inst=1 eph=1 name=Loom", line);
        }

        [TestMethod]
        public void Format_NullName_RendersUnregistered()
        {
            var line = RealmLine.Format(5, null, 0, false);

            Assert.AreEqual("[REALM] id=5 inst=0 eph=0 name=unregistered", line);
        }

        [TestMethod]
        public void Format_EmptyName_RendersUnregistered()
        {
            var line = RealmLine.Format(5, "", 0, false);

            Assert.AreEqual("[REALM] id=5 inst=0 eph=0 name=unregistered", line);
        }

        [TestMethod]
        public void Format_WhitespaceName_RendersUnregistered()
        {
            var line = RealmLine.Format(5, "   ", 0, false);

            Assert.AreEqual("[REALM] id=5 inst=0 eph=0 name=unregistered", line);
        }

        [TestMethod]
        public void Format_NameWithSpaces_SurvivesIntactAndStaysLast()
        {
            var line = RealmLine.Format(9, "Marae Lassel", 12, false);

            Assert.AreEqual("[REALM] id=9 inst=12 eph=0 name=Marae Lassel", line);
            StringAssert.StartsWith(line, RealmLine.Prefix);
        }
    }
}
