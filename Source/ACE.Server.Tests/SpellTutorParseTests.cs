using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the pure parts of the Marketplace spell tutor NPC (SpellTutor.cs): parsing a tell into a
    /// school + level request, and the note-value-times-stack-size cost helper. The live offer resolution
    /// (SpellTutor.TryHandleTalkDirect, reading the retail professor NPCs) needs a Player + world weenie cache
    /// and is exercised in-game via the NPC.
    /// </summary>
    [TestClass]
    public class SpellTutorParseTests
    {
        [TestMethod]
        public void Parse_SchoolThenLevel_Succeeds()
        {
            Assert.IsTrue(SpellTutor.TryParseRequest("item 3", out var school, out var level));
            Assert.AreEqual("item", school);
            Assert.AreEqual(3, level);
        }

        [TestMethod]
        public void Parse_IsCaseInsensitive()
        {
            Assert.IsTrue(SpellTutor.TryParseRequest("Creature 7", out var school, out var level));
            Assert.AreEqual("creature", school);
            Assert.AreEqual(7, level);
        }

        [TestMethod]
        public void Parse_SchoolLevelLevel_Succeeds()
        {
            Assert.IsTrue(SpellTutor.TryParseRequest("war level 2", out var school, out var level));
            Assert.AreEqual("war", school);
            Assert.AreEqual(2, level);
        }

        [TestMethod]
        public void Parse_LevelLevelSchool_Succeeds()
        {
            Assert.IsTrue(SpellTutor.TryParseRequest("level 4 life", out var school, out var level));
            Assert.AreEqual("life", school);
            Assert.AreEqual(4, level);
        }

        [TestMethod]
        public void Parse_LevelAsWord_Succeeds()
        {
            Assert.IsTrue(SpellTutor.TryParseRequest("void seven", out var school, out var level));
            Assert.AreEqual("void", school);
            Assert.AreEqual(7, level);
        }

        [TestMethod]
        public void Parse_TwoWordSchoolName_Succeeds()
        {
            Assert.IsTrue(SpellTutor.TryParseRequest("life magic 1", out var school, out var level));
            Assert.AreEqual("life", school);
            Assert.AreEqual(1, level);
        }

        [TestMethod]
        public void Parse_Empty_Rejects()
        {
            Assert.IsFalse(SpellTutor.TryParseRequest("", out _, out _));
        }

        [TestMethod]
        public void Parse_SchoolWithNoLevel_Rejects()
        {
            Assert.IsFalse(SpellTutor.TryParseRequest("item", out _, out _));
        }

        [TestMethod]
        public void Parse_LevelOutOfRangeHigh_Rejects()
        {
            Assert.IsFalse(SpellTutor.TryParseRequest("item 8", out _, out _));
        }

        [TestMethod]
        public void Parse_LevelOutOfRangeLow_Rejects()
        {
            Assert.IsFalse(SpellTutor.TryParseRequest("item 0", out _, out _));
        }

        [TestMethod]
        public void Parse_UnknownSchool_Rejects()
        {
            Assert.IsFalse(SpellTutor.TryParseRequest("fire 3", out _, out _));
        }

        [TestMethod]
        public void ComputeCost_MultipliesValueByStackSize()
        {
            Assert.AreEqual(4_000_000L, SpellTutor.ComputeCost(250000, 16));
            Assert.AreEqual(10_000_000L, SpellTutor.ComputeCost(250000, 40));
        }
    }
}
