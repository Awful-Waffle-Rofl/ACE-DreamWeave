using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers LeaderboardExemptionManager's pure static surface: account-list parsing/formatting and the
    /// IsExemptCore decision function. No database, no world - everything under test here takes its inputs as
    /// plain parameters rather than reading PropertyManager or IPlayer.
    /// </summary>
    [TestClass]
    public class LeaderboardExemptionTests
    {
        [TestMethod]
        public void ParseAccountList_CommaSeparated()
        {
            var result = LeaderboardExemptionManager.ParseAccountList("alice,bob,carol");
            CollectionAssert.AreEqual(new[] { "alice", "bob", "carol" }, result.ToList());
        }

        [TestMethod]
        public void ParseAccountList_TrimsSurroundingWhitespace()
        {
            var result = LeaderboardExemptionManager.ParseAccountList("  alice ,  bob  ");
            CollectionAssert.AreEqual(new[] { "alice", "bob" }, result.ToList());
        }

        [TestMethod]
        public void ParseAccountList_TrailingComma_DropsEmptyToken()
        {
            var result = LeaderboardExemptionManager.ParseAccountList("alice,bob,");
            CollectionAssert.AreEqual(new[] { "alice", "bob" }, result.ToList());
        }

        [TestMethod]
        public void ParseAccountList_DuplicatesDifferingOnlyInCase_CollapseToFirstSpelling()
        {
            var result = LeaderboardExemptionManager.ParseAccountList("Alice,alice,ALICE");
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Alice", result[0]);
        }

        [TestMethod]
        public void ParseAccountList_SemicolonSeparator()
        {
            var result = LeaderboardExemptionManager.ParseAccountList("alice;bob");
            CollectionAssert.AreEqual(new[] { "alice", "bob" }, result.ToList());
        }

        [TestMethod]
        public void ParseAccountList_NewlineSeparator()
        {
            var result = LeaderboardExemptionManager.ParseAccountList("alice\nbob");
            CollectionAssert.AreEqual(new[] { "alice", "bob" }, result.ToList());
        }

        [TestMethod]
        public void ParseAccountList_Null_ReturnsEmpty()
        {
            Assert.AreEqual(0, LeaderboardExemptionManager.ParseAccountList(null).Count);
        }

        [TestMethod]
        public void ParseAccountList_EmptyString_ReturnsEmpty()
        {
            Assert.AreEqual(0, LeaderboardExemptionManager.ParseAccountList("").Count);
        }

        [TestMethod]
        public void ParseAccountList_WhitespaceOnly_ReturnsEmpty()
        {
            Assert.AreEqual(0, LeaderboardExemptionManager.ParseAccountList("   ").Count);
        }

        [TestMethod]
        public void FormatAccountList_RoundTripsThroughParseAccountList()
        {
            var original = new List<string> { "alice", "bob", "carol" };
            var formatted = LeaderboardExemptionManager.FormatAccountList(original);
            var reparsed = LeaderboardExemptionManager.ParseAccountList(formatted);
            CollectionAssert.AreEqual(original, reparsed.ToList());
        }

        [TestMethod]
        public void IsExemptCore_AccessLevelZero_UnlistedName_NotExempt()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.IsFalse(LeaderboardExemptionManager.IsExemptCore(0, "bob", false, exempt));
        }

        [TestMethod]
        public void IsExemptCore_Advocate_NotExempt()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.IsFalse(LeaderboardExemptionManager.IsExemptCore((uint)AccessLevel.Advocate, "bob", false, exempt));
        }

        [TestMethod]
        public void IsExemptCore_Mule_IsExempt()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(LeaderboardExemptionManager.IsExemptCore(0, "bob", false, true, exempt));
        }

        [TestMethod]
        public void IsExemptCore_NotMule_PlayerAccess_NotExempt()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.IsFalse(LeaderboardExemptionManager.IsExemptCore(0, "bob", false, false, exempt));
        }

        [TestMethod]
        public void IsExemptCore_Sentinel_IsExempt()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(LeaderboardExemptionManager.IsExemptCore((uint)AccessLevel.Sentinel, "bob", false, exempt));
        }

        [TestMethod]
        public void IsExemptCore_Admin_IsExempt()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(LeaderboardExemptionManager.IsExemptCore((uint)AccessLevel.Admin, "bob", false, exempt));
        }

        [TestMethod]
        public void IsExemptCore_StaffCharacterBoolAlone_IsExempt_EvenWithNullAccount()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(LeaderboardExemptionManager.IsExemptCore(null, null, true, exempt));
        }

        [TestMethod]
        public void IsExemptCore_ListedNameDifferentCase_IsExempt()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "StaffAlt" };
            Assert.IsTrue(LeaderboardExemptionManager.IsExemptCore(0, "staffalt", false, exempt));
        }

        [TestMethod]
        public void IsExemptCore_NullAccountName_WithNonEmptyList_NotExempt()
        {
            var exempt = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "staffalt" };
            Assert.IsFalse(LeaderboardExemptionManager.IsExemptCore(0, null, false, exempt));
        }

        [TestMethod]
        public void IsExemptCore_NullExemptNames_DoesNotThrow()
        {
            Assert.IsFalse(LeaderboardExemptionManager.IsExemptCore(0, "bob", false, null));
        }

        // Enum-renumber guards: asserts against literal ids so a silent renumber of AccessLevel or PropertyBool
        // fails this test the same way ProvingGroundsLeaderboardResetTests catches a renumber of PropertyInt64.
        [TestMethod]
        public void AccessLevel_Advocate_Is_1()
        {
            Assert.AreEqual(1, (int)AccessLevel.Advocate);
        }

        [TestMethod]
        public void AccessLevel_Sentinel_Is_2()
        {
            Assert.AreEqual(2, (int)AccessLevel.Sentinel);
        }

        [TestMethod]
        public void InherentExemptAccessLevel_Is_Sentinel()
        {
            Assert.AreEqual(AccessLevel.Sentinel, LeaderboardExemptionManager.InherentExemptAccessLevel);
        }

        [TestMethod]
        public void StaffCharacterBools_IsExactlyTheFourExpectedBools_InOrder_AndExcludesAdvocate()
        {
            var ids = LeaderboardExemptionManager.StaffCharacterBools.Select(b => (int)b).ToList();
            CollectionAssert.AreEqual(new[] { 44, 45, 9005, 46 }, ids);

            Assert.IsFalse(LeaderboardExemptionManager.StaffCharacterBools.Contains(PropertyBool.IsAdvocate));
            Assert.AreEqual(47, (int)PropertyBool.IsAdvocate);
        }
    }
}
