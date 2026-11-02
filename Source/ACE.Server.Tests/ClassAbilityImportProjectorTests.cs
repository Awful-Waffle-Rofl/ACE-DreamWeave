using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The ProjectOwner glue for the class ability import block: the input-gathering seam, the failure guard,
    /// and a source scan that pins the assignment into ProjectOwner (the fake world fills the block itself, so no
    /// behavioral test would notice the line being deleted or put behind a flag).
    /// </summary>
    [TestClass]
    public class ClassAbilityImportProjectorTests
    {
        private static CharacterPropertiesQuestRegistry Row(string name, int n) => new CharacterPropertiesQuestRegistry { QuestName = name, NumTimesCompleted = n };

        [TestMethod]
        public void Seam_MapsQuestRowsAndInts()
        {
            var def = ClassAbilityRegistry.Abilities.Values.First();

            var block = CharacterSheetProjector.BuildClassAbilityImport(
                new[] { Row(ClassAbilityRegistry.QuestKeyPrefix + def.Name, 1), Row("DoneQuest", 1) }, 7, 3, 40, 11, 1, true);

            Assert.AreEqual(def.Name, block.Ranks.Single().Id);
            CollectionAssert.AreEquivalent(new[] { "DoneQuest" }, block.CompletedQuests.ToArray());
            Assert.AreEqual(7, block.LumPointsBought);
            Assert.AreEqual(3, block.XpPointsBought);
            Assert.AreEqual(40, block.PointsEarned);
            Assert.AreEqual(11, block.PointsAvailable);
        }

        [DataTestMethod]
        [DataRow(0, true, true)]
        [DataRow(0, false, false)]
        [DataRow(1, true, false)]
        [DataRow(1, false, false)]
        public void Seam_RespecPending_IsStampUnsetAndSweepOn(int stamp, bool sweep, bool expected)
        {
            var block = CharacterSheetProjector.BuildClassAbilityImport(Array.Empty<CharacterPropertiesQuestRegistry>(), 0, 0, 0, 0, stamp, sweep);

            Assert.AreEqual(expected, block.RespecPending);
        }

        [TestMethod]
        public void Guard_ABuildThatThrows_YieldsNull()
        {
            Assert.IsNull(CharacterSheetProjector.GuardedClassAbilityImport(() => throw new InvalidOperationException("boom"), "Tester"));
        }

        [TestMethod]
        public void Guard_ASuccessfulBuild_PassesThrough()
        {
            var block = new ClassAbilityImportBlock();

            Assert.AreSame(block, CharacterSheetProjector.GuardedClassAbilityImport(() => block, "Tester"));
        }

        /// <summary>
        /// ProjectOwner's own initializer must assign ClassAbilityImport from GuardedClassAbilityImport, with no
        /// `if` anywhere in the method (a flag gate would hand some joined offline callers an empty block).
        /// </summary>
        [TestMethod]
        public void ProjectOwner_AssignsTheImportBlockUnconditionally_SourceScan()
        {
            var body = ProjectOwnerBody();

            StringAssert.Matches(body, new Regex(@"return\s+new\s+OwnerProfile\s*\{[^{}]*\bClassAbilityImport\s*=\s*GuardedClassAbilityImport\(\s*\(\)\s*=>\s*ProjectClassAbilityImport\(\s*player\s*\)", RegexOptions.Singleline),
                "ProjectOwner must set OwnerProfile.ClassAbilityImport = GuardedClassAbilityImport(() => ProjectClassAbilityImport(player), ...) in its initializer.");
            Assert.IsFalse(Regex.IsMatch(body, @"\bif\b|\?[^?.]|\bswitch\b"), "ProjectOwner must not gate the import block behind a condition.");
        }

        [TestMethod]
        public void ProjectOwner_ScanBody_IsTheRealMethod()
        {
            var body = ProjectOwnerBody();

            StringAssert.Contains(body, "ProjectOwnerGear(");
            StringAssert.Contains(body, "HeritageId");
        }

        private static string ProjectOwnerBody()
        {
            var path = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server", "Managers", "CharacterSheets", "CharacterSheetProjector.cs");
            var text = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var start = text.IndexOf("private static OwnerProfile ProjectOwner(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "ProjectOwner not found in CharacterSheetProjector.cs");

            var open = text.IndexOf('{', start);
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(open, i - open + 1);
            }

            Assert.Fail("ProjectOwner body is unbalanced");
            return null;
        }
    }
}
