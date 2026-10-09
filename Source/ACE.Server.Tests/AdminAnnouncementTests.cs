using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// AdminAnnouncement.Normalize/TryNormalize/AuditMessage and Gamecast.Format, pure and unit-testable
    /// with no live server (PLAN-P3.md section 5). The market repo's Api/AdminAnnouncementText.cs
    /// mirrors Normalize's rule byte for byte (invariant 10); this fixture table is the one the market
    /// suite is required to reproduce.
    /// </summary>
    [TestClass]
    public class AdminAnnouncementTests
    {
        [DataTestMethod]
        [DataRow("hi", "hi")]
        [DataRow("  hi  ", "hi")]
        [DataRow("a\r\nb", "a  b")]
        [DataRow("\t", "")]
        [DataRow(null, null)]
        public void Normalize_FixtureTable(string raw, string expected)
        {
            Assert.AreEqual(expected, AdminAnnouncement.Normalize(raw));
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("\r\n\t")]
        [DataRow(null)]
        public void TryNormalize_EmptyAndWhitespace_False(string raw)
        {
            Assert.IsFalse(AdminAnnouncement.TryNormalize(raw, out var text));
            Assert.IsNull(text);
        }

        [TestMethod]
        public void TryNormalize_500True_501False()
        {
            var text500 = new string('a', 500);
            var text501 = new string('a', 501);

            Assert.IsTrue(AdminAnnouncement.TryNormalize(text500, out var result500));
            Assert.AreEqual(text500, result500);

            Assert.IsFalse(AdminAnnouncement.TryNormalize(text501, out var result501));
            Assert.IsNull(result501);
        }

        [TestMethod]
        public void AuditMessage_NamesAccountAndText()
        {
            Assert.AreEqual("weft issued a world broadcast from the web admin panel: hi", AdminAnnouncement.AuditMessage("weft", "hi"));
        }

        [TestMethod]
        public void Gamecast_Format_PrefixExact()
        {
            Assert.AreEqual("Broadcast from System> hi", Gamecast.Format("System", "hi"));
        }
    }
}
