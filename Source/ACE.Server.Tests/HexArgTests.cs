using System.Linq;

using ACE.Common;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers HexArg.TryParseUIntList, including the PowerShell unquoted-comma corruption guard added
    /// 2026-08-01: an unquoted "--landblocks=0x0032,0x017D" gets its later tokens evaluated as decimal
    /// numbers by PowerShell 5.1's array operator and restringified without their "0x" prefix, so the
    /// list silently parses as the wrong landblocks. The guard refuses that specific mixed-prefix shape.
    /// </summary>
    [TestClass]
    public class HexArgTests
    {
        [TestMethod]
        public void TryParseUIntList_AllPrefixed_Parses()
        {
            var ok = HexArg.TryParseUIntList("0x0032,0x017D", out var values, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { 0x32u, 0x17Du }, values);
        }

        [TestMethod]
        public void TryParseUIntList_AllBare_StillParses()
        {
            var ok = HexArg.TryParseUIntList("0032,017D", out var values, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { 0x32u, 0x17Du }, values);
        }

        [TestMethod]
        public void TryParseUIntList_SingleTokenPrefixed_Parses()
        {
            var ok = HexArg.TryParseUIntList("0x0032", out var values, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { 0x32u }, values);
        }

        [TestMethod]
        public void TryParseUIntList_SingleTokenBare_Parses()
        {
            var ok = HexArg.TryParseUIntList("0032", out var values, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { 0x32u }, values);
        }

        [TestMethod]
        public void TryParseUIntList_CorruptedRealWorldString_IsRefused()
        {
            // the exact PowerShell-mangled form of 0x0032,0x017D,0x0021,0x01A8,0x01BA,0x0043,0x00AE
            var ok = HexArg.TryParseUIntList("0x0032,381,33,424,442,67,174", out var values, out var error);

            Assert.IsFalse(ok);
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void TryParseUIntList_CorrectRealWorldString_IsAccepted()
        {
            var ok = HexArg.TryParseUIntList("0x0032,0x017D,0x0021,0x01A8,0x01BA,0x0043,0x00AE", out var values, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.AreEqual(7, values.Count);
            Assert.AreEqual(0x00AEu, values.Last());
        }

        [TestMethod]
        public void TryParseUIntList_MinimalMixedCase_IsRefused()
        {
            var ok = HexArg.TryParseUIntList("0x0032,381", out var values, out var error);

            Assert.IsFalse(ok);
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void TryParseUIntList_MixedPrefixError_MentionsQuoting()
        {
            var ok = HexArg.TryParseUIntList("0x0032,381", out var values, out var error);

            Assert.IsFalse(ok);
            StringAssert.Contains(error, "quote the whole argument");
        }

        [TestMethod]
        public void TryParseUIntList_MixedPrefixError_DoesNotSuggestQuotingTheCorruptedValue()
        {
            // the example in the error must NOT be built from the corrupted input (that would tell the
            // user to quote an already-wrong value and reproduce the exact bug the guard prevents)
            var ok = HexArg.TryParseUIntList("0x0032,381", out var values, out var error);

            Assert.IsFalse(ok);
            StringAssert.DoesNotMatch(error, new System.Text.RegularExpressions.Regex("e\\.g\\.[^\n]*381"));
            StringAssert.Contains(error, "LOST");
        }

        [TestMethod]
        public void TryParseUIntList_UnparseableToken_NamesItInError()
        {
            var ok = HexArg.TryParseUIntList("0x0032,zzz", out var values, out var error);

            Assert.IsFalse(ok);
            StringAssert.Contains(error, "'zzz'");
        }

        [TestMethod]
        public void TryParseUIntList_ToleratesSurroundingWhitespace()
        {
            var ok = HexArg.TryParseUIntList("0x0032, 0x017D", out var values, out var error);

            Assert.IsTrue(ok);
            Assert.IsNull(error);
            CollectionAssert.AreEqual(new[] { 0x32u, 0x17Du }, values);
        }
    }
}
