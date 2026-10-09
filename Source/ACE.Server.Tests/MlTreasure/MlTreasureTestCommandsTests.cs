using ACE.Server.Command.Handlers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// /testtreasuremap's pure argument parser (MlTreasureTestCommands.ParseArgs) - every named type, Boss
    /// Rush with and without a set id, and every malformed input. Everything else the command does (the
    /// outdoors/Marae Lassel gates, the live Boss Rush set-id validation against
    /// MlDigsiteTunables.BossRushSets, object creation) needs PropertyManager, a live Player and a
    /// landblock, none of which ACE.Server.Tests can supply - PropertyManager reads throw here - which is
    /// exactly why the parse decision is factored out as a function over the raw parameter array.
    /// </summary>
    [TestClass]
    public class MlTreasureTestCommandsTests
    {
        // ---- each named type ------------------------------------------------------------------------------

        [TestMethod]
        public void Waves_Parses()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "waves" });

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(MlTreasureTestCommands.TestMapKind.Waves, parsed.Kind);
            Assert.IsNull(parsed.BossRushSetId);
        }

        [TestMethod]
        public void Corruption_Parses()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "corruption" });

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(MlTreasureTestCommands.TestMapKind.Corruption, parsed.Kind);
            Assert.IsNull(parsed.BossRushSetId);
        }

        [TestMethod]
        public void Relaria_Parses()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "relaria" });

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(MlTreasureTestCommands.TestMapKind.Relaria, parsed.Kind);
            Assert.IsNull(parsed.BossRushSetId);
        }

        [TestMethod]
        public void Random_Parses()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "random" });

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(MlTreasureTestCommands.TestMapKind.Random, parsed.Kind);
            Assert.IsNull(parsed.BossRushSetId);
        }

        /// <summary>Case-insensitive, matching every other command's first-token switch in this codebase.</summary>
        [TestMethod]
        public void Waves_IsCaseInsensitive()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "WaVeS" });

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(MlTreasureTestCommands.TestMapKind.Waves, parsed.Kind);
        }

        // ---- bossrush, with and without a set id -----------------------------------------------------------

        [TestMethod]
        public void BossRush_NoSetId_Parses()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "bossrush" });

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(MlTreasureTestCommands.TestMapKind.BossRush, parsed.Kind);
            Assert.IsNull(parsed.BossRushSetId);
        }

        [TestMethod]
        public void BossRush_WithSetId_Parses()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "bossrush", "3" });

            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(MlTreasureTestCommands.TestMapKind.BossRush, parsed.Kind);
            Assert.AreEqual(3L, parsed.BossRushSetId);
        }

        [TestMethod]
        public void BossRush_NonNumericSetId_IsInvalid()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "bossrush", "abc" });

            Assert.IsFalse(parsed.IsValid);
            Assert.IsNotNull(parsed.Error);
        }

        [TestMethod]
        public void BossRush_ZeroSetId_IsInvalid()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "bossrush", "0" });

            Assert.IsFalse(parsed.IsValid);
        }

        [TestMethod]
        public void BossRush_NegativeSetId_IsInvalid()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "bossrush", "-1" });

            Assert.IsFalse(parsed.IsValid);
        }

        // ---- bad input --------------------------------------------------------------------------------------

        [TestMethod]
        public void NoArgs_IsInvalid()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new string[0]);

            Assert.IsFalse(parsed.IsValid);
            Assert.IsNotNull(parsed.Error);
        }

        [TestMethod]
        public void Null_IsInvalid()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(null);

            Assert.IsFalse(parsed.IsValid);
            Assert.IsNotNull(parsed.Error);
        }

        [TestMethod]
        public void UnknownFirstArg_IsInvalid()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "nonsense" });

            Assert.IsFalse(parsed.IsValid);
            Assert.IsNotNull(parsed.Error);
        }

        [TestMethod]
        public void BlankFirstArg_IsInvalid()
        {
            var parsed = MlTreasureTestCommands.ParseArgs(new[] { "   " });

            Assert.IsFalse(parsed.IsValid);
        }
    }
}
