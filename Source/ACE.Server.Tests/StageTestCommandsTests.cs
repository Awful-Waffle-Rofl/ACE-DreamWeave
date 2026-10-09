using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers the session-independent logic behind the /mymmd and /myrespec stage self-service commands
    /// (StageTestCommands.cs). No test in this project constructs a live Player/Session (confirmed by
    /// grepping ACE.Server.Tests for "new Player(" and Player/WorldObjectFactory usage before writing
    /// this file - the closest existing harness, TestEnvironment.cs, only stands up Config.js/DB
    /// reachability, not a character), so a genuine end-to-end command test isn't possible here. These
    /// tests instead exercise the private helpers directly via reflection, and the public statics
    /// StageTestCommands.HandleMyRespec shares with SkillAlterationDevice's Gem of Forgetfulness path,
    /// rather than asserting a hardcoded literal against itself.
    /// </summary>
    [TestClass]
    public class StageTestCommandsTests
    {
        private static MethodInfo TryParseAmountMethod =>
            typeof(StageTestCommands).GetMethod("TryParseAmount", BindingFlags.NonPublic | BindingFlags.Static);

        private static FieldInfo MmdWcidField =>
            typeof(StageTestCommands).GetField("MmdWcid", BindingFlags.NonPublic | BindingFlags.Static);

        private static FieldInfo MaxMmdPerGrantField =>
            typeof(StageTestCommands).GetField("MaxMmdPerGrant", BindingFlags.NonPublic | BindingFlags.Static);

        private static FieldInfo PromissoryNoteWcidField =>
            typeof(StageTestCommands).GetField("PromissoryNoteWcid", BindingFlags.NonPublic | BindingFlags.Static);

        private static FieldInfo StipendWcidField =>
            typeof(StageTestCommands).GetField("StipendWcid", BindingFlags.NonPublic | BindingFlags.Static);

        [TestMethod]
        public void MmdWcid_MatchesTheWeenieClassNameRegistry()
        {
            var wcid = (uint)MmdWcidField.GetRawConstantValue();

            Assert.AreEqual(
                (uint)ACE.Server.Factories.Enum.WeenieClassName.tradenote250000,
                wcid,
                "StageTestCommands.MmdWcid must track the tradenote250000 weenie (Trade Note (250,000)) - " +
                "if the registry ever renumbers this wcid, /mymmd must follow it.");
        }

        [TestMethod]
        public void PromissoryNoteWcid_MatchesTheWeenieClassNameRegistry()
        {
            var wcid = (uint)PromissoryNoteWcidField.GetRawConstantValue();

            Assert.AreEqual(
                (uint)ACE.Server.Factories.Enum.WeenieClassName.ace43901_promissorynote,
                wcid,
                "StageTestCommands.PromissoryNoteWcid must track the ace43901-promissorynote weenie - " +
                "this is the same wcid the banking system uses (Player_Bank.PromissoryNoteWcid), so the " +
                "two must not drift apart.");
        }

        [TestMethod]
        public void StipendWcid_MatchesTheWeenieClassNameRegistry()
        {
            var wcid = (uint)StipendWcidField.GetRawConstantValue();

            Assert.AreEqual(
                (uint)ACE.Server.Factories.Enum.WeenieClassName.ace46423_stipend,
                wcid,
                "StageTestCommands.StipendWcid must track the ace46423-stipend weenie (Stipend) - the " +
                "currency the retail stipend vendor (Marid, 46425) charges against.");
        }

        [TestMethod]
        public void TryParseAmount_ClampsOverLargeRequests_ToTheMax()
        {
            var max = (int)MaxMmdPerGrantField.GetRawConstantValue();

            // session is only touched on the failure path, so null is safe here
            object[] args = { null, "999999", (long)max, null };

            var result = (bool)TryParseAmountMethod.Invoke(null, args);
            var amount = (long)args[3];

            Assert.IsTrue(result, "an over-large request must clamp, not be rejected outright");
            Assert.AreEqual((long)max, amount);
        }

        [TestMethod]
        public void TryParseAmount_PassesThroughValuesUnderTheMax()
        {
            object[] args = { null, "37", 250L, null };

            var result = (bool)TryParseAmountMethod.Invoke(null, args);
            var amount = (long)args[3];

            Assert.IsTrue(result);
            Assert.AreEqual(37L, amount);
        }

        [TestMethod]
        public void AlwaysTrainedSkills_MatchTheRespecExceptionList()
        {
            // Merely referencing the Player type runs its static constructor, which reaches into
            // WorldDatabaseWithEntityCache for a template weenie - so this needs a reachable world DB,
            // same as QuestManagerTests/StartupTests.
            TestEnvironment.RequireDatabases();

            // These are exactly the skills /myrespec's confirm-result message must call out as
            // "stays Trained, XP refunded only" - read from Player_Skills.cs at write time, not recalled.
            var expected = new[]
            {
                Skill.ArcaneLore, Skill.Jump, Skill.Loyalty, Skill.MagicDefense, Skill.Run, Skill.Salvaging
            };

            CollectionAssert.AreEquivalent(expected, Player.AlwaysTrained);

            foreach (var skill in expected)
                Assert.IsFalse(Player.IsSkillUntrainable(skill), $"{skill} is AlwaysTrained and must not be reported untrainable");

            Assert.IsTrue(Player.IsSkillUntrainable(Skill.Axe), "an ordinary retail skill must be untrainable");
        }

        [TestMethod]
        public void AugSpecSkills_MatchTheRespecExceptionList()
        {
            // See AlwaysTrainedSkills_MatchTheRespecExceptionList - touching Player requires a reachable world DB.
            TestEnvironment.RequireDatabases();

            // These are exactly the skills that a matching augmentation specializes for free, rather than
            // with skill credits - so /myrespec and the Gem of Forgetfulness both untrain them outright
            // and refund the TRAINED cost, never UpgradeCostFromTrainedToSpecialized.
            var expected = new[]
            {
                Skill.ArmorTinkering, Skill.ItemTinkering, Skill.MagicItemTinkering, Skill.WeaponTinkering, Skill.Salvaging
            };

            CollectionAssert.AreEquivalent(expected, Player.AugSpecSkills);
        }

        [TestMethod]
        public void AugSpecSkills_UntrainExceptForTheAlwaysTrainedOne()
        {
            // See AlwaysTrainedSkills_MatchTheRespecExceptionList - touching Player requires a reachable world DB.
            TestEnvironment.RequireDatabases();

            // Salvaging is the single skill on both lists, so it is the only augmentation specialization
            // that a Gem of Forgetfulness cannot untrain - it recovers the invested XP and stays put. The
            // four tinkering skills DO untrain, which is the behaviour /myrespec's summary and
            // SkillAlterationDevice's message selection both branch on.
            foreach (var skill in Player.AugSpecSkills)
            {
                if (skill == Skill.Salvaging)
                    Assert.IsFalse(Player.IsSkillUntrainable(skill), "Salvaging is AlwaysTrained and must stay Specialized under the augmentation");
                else
                    Assert.IsTrue(Player.IsSkillUntrainable(skill), $"{skill} is augmentation-specialized only and must untrain outright");
            }
        }
    }
}
