using System;
using System.Globalization;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule defense skills (Discord bug: "Mules created from the Mule NPC cannot wield requirement items").
    /// Conversion used to untrain Melee/Missile Defense, so a mule met no defense wield requirement. It now
    /// trains all three defense skills and raises them to 'mule_defense_skill_level', at conversion and on
    /// every mule login (which tops up mules converted earlier).
    ///
    /// A Player cannot be constructed in this harness (static type initializer needs a live world DB), so the
    /// consumer is pinned in two halves: the pure record top-up and the pure wield decision
    /// (Player.SkillWieldRequirementMet, the exact function CheckWieldRequirement calls) are exercised
    /// directly, and the two call sites (conversion, PlayerEnterWorld) are pinned by source scan. Base is
    /// attribute formula + InitLevel + Ranks, so InitLevel + Ranks is a lower bound on Base and Current -
    /// the wield checks below use that floor, which is the conservative reading.
    /// </summary>
    [TestClass]
    public class MuleDefenseSkillTests
    {
        /// <summary>
        /// The highest Melee/Missile/Magic Defense wield requirement found in ace_world on 2026-11-25
        /// (weenie_properties_int type 159 in {6,7,15} joined to 160): Melee Defense, RawSkill, 375.
        /// </summary>
        private const uint HighestKnownDefenseRequirement = 375;

        private static readonly Skill[] DefenseSkills = { Skill.MeleeDefense, Skill.MissileDefense, Skill.MagicDefense };

        private static PropertiesSkill UntrainedRecord() => new PropertiesSkill { SAC = SkillAdvancementClass.Untrained, InitLevel = 0, LevelFromPP = 0 };

        private static uint DefaultTarget()
        {
            var row = PropertyManager.EnumerateWithPrefix("mule_defense_skill_level").Single(r => r.key == "mule_defense_skill_level");
            Assert.AreEqual("long", row.type);
            return uint.Parse(row.@default, CultureInfo.InvariantCulture);
        }

        private static bool Meets(PropertiesSkill r, WieldRequirement kind, uint difficulty)
        {
            var floor = r.InitLevel + r.LevelFromPP;
            return Player.SkillWieldRequirementMet(kind, () => floor, () => floor, r.SAC, difficulty);
        }

        [TestMethod]
        public void Default_Exceeds_The_Highest_Defense_Requirement()
            => Assert.IsTrue(DefaultTarget() > HighestKnownDefenseRequirement);

        [TestMethod]
        public void Converted_Mule_Meets_Every_Skill_Requirement_Kind_At_The_Maximum()
        {
            var target = DefaultTarget();

            foreach (var skill in DefenseSkills)
            {
                var record = UntrainedRecord();
                Assert.IsFalse(Meets(record, WieldRequirement.RawSkill, HighestKnownDefenseRequirement), skill + " control: untrained mule must fail");

                Assert.IsTrue(Player.TopUpMuleDefenseRecord(record, target));

                Assert.IsTrue(Meets(record, WieldRequirement.Skill, HighestKnownDefenseRequirement), skill + " Skill (current)");
                Assert.IsTrue(Meets(record, WieldRequirement.RawSkill, HighestKnownDefenseRequirement), skill + " RawSkill (base)");
                Assert.IsTrue(Meets(record, WieldRequirement.Training, (uint)SkillAdvancementClass.Trained), skill + " Training");
            }
        }

        [TestMethod]
        public void Already_Converted_Mule_Is_Topped_Up_On_Login_And_It_Is_Idempotent()
        {
            // a mule converted by the old code: Melee/Missile untrained at 0; Magic Defense (AlwaysTrained) trained at its creation value
            var melee = UntrainedRecord();
            var magic = new PropertiesSkill { SAC = SkillAdvancementClass.Trained, InitLevel = 10, LevelFromPP = 0 };
            var target = DefaultTarget();

            Assert.IsFalse(Meets(melee, WieldRequirement.Skill, HighestKnownDefenseRequirement));
            Assert.IsFalse(Meets(magic, WieldRequirement.RawSkill, HighestKnownDefenseRequirement));

            Assert.IsTrue(Player.TopUpMuleDefenseRecord(melee, target));
            Assert.IsTrue(Player.TopUpMuleDefenseRecord(magic, target));

            Assert.IsTrue(Meets(melee, WieldRequirement.Skill, HighestKnownDefenseRequirement));
            Assert.IsTrue(Meets(magic, WieldRequirement.RawSkill, HighestKnownDefenseRequirement));
            Assert.AreEqual(SkillAdvancementClass.Trained, melee.SAC);

            Assert.IsFalse(Player.TopUpMuleDefenseRecord(melee, target), "second run must change nothing");
            Assert.IsFalse(Player.TopUpMuleDefenseRecord(magic, target), "second run must change nothing");
        }

        [TestMethod]
        public void TopUp_Never_Lowers_A_Skill()
        {
            var record = new PropertiesSkill { SAC = SkillAdvancementClass.Specialized, InitLevel = 900, LevelFromPP = 5 };

            Assert.IsFalse(Player.TopUpMuleDefenseRecord(record, 400));
            Assert.AreEqual(SkillAdvancementClass.Specialized, record.SAC);
            Assert.AreEqual(900u, record.InitLevel);
        }

        [TestMethod]
        public void Defense_Skill_Set_Is_Exactly_Melee_Missile_Magic()
        {
            foreach (var s in Enum.GetValues(typeof(Skill)).Cast<Skill>())
                Assert.AreEqual(DefenseSkills.Contains(s), Player.IsMuleDefenseSkill(s), s.ToString());
        }


        // ---- golden cases for the wield decision: current and base deliberately differ ----

        private static bool Decide(WieldRequirement kind, uint current, uint baseValue, SkillAdvancementClass sac, uint difficulty)
            => Player.SkillWieldRequirementMet(kind, () => current, () => baseValue, sac, difficulty);

        [TestMethod]
        public void Skill_Reads_Current_Not_Base()
        {
            Assert.IsFalse(Decide(WieldRequirement.Skill, 100, 500, SkillAdvancementClass.Trained, 300));
            Assert.IsTrue(Decide(WieldRequirement.Skill, 500, 100, SkillAdvancementClass.Trained, 300));
        }

        [TestMethod]
        public void RawSkill_Reads_Base_Not_Current()
        {
            Assert.IsTrue(Decide(WieldRequirement.RawSkill, 100, 500, SkillAdvancementClass.Trained, 300));
            Assert.IsFalse(Decide(WieldRequirement.RawSkill, 500, 100, SkillAdvancementClass.Trained, 300));
        }

        [TestMethod]
        public void Training_Reads_Advancement_Class()
        {
            // difficulty 2 = Trained, 3 = Specialized
            Assert.IsFalse(Decide(WieldRequirement.Training, 999, 999, SkillAdvancementClass.Untrained, 2));
            Assert.IsTrue(Decide(WieldRequirement.Training, 0, 0, SkillAdvancementClass.Trained, 2));
            Assert.IsTrue(Decide(WieldRequirement.Training, 0, 0, SkillAdvancementClass.Specialized, 2));
            Assert.IsFalse(Decide(WieldRequirement.Training, 999, 999, SkillAdvancementClass.Trained, 3));
            Assert.IsTrue(Decide(WieldRequirement.Training, 0, 0, SkillAdvancementClass.Specialized, 3));
        }

        [TestMethod]
        public void Each_Kind_Evaluates_Only_What_It_Reads()
        {
            Func<uint> boom = () => throw new InvalidOperationException("evaluated a value this kind does not read");

            Assert.IsTrue(Player.SkillWieldRequirementMet(WieldRequirement.Skill, () => 5, boom, SkillAdvancementClass.Trained, 1));
            Assert.IsTrue(Player.SkillWieldRequirementMet(WieldRequirement.RawSkill, boom, () => 5, SkillAdvancementClass.Trained, 1));
            Assert.IsTrue(Player.SkillWieldRequirementMet(WieldRequirement.Training, boom, boom, SkillAdvancementClass.Trained, 2));
        }

        // ---- F1: a mule can neither reset (credit refund) nor train a skill ----

        [TestMethod]
        public void Skill_Reset_And_Train_Are_Refused_For_Mules()
        {
            var skills = Read("Player_Skills.cs");

            var reset = Slice(skills, "public bool ResetSkill(Skill skill, bool refund = true)", "var creatureSkill = GetCreatureSkill(skill, false);");
            StringAssert.Contains(reset, "if (MuleBlocked(MuleAction.ChangeSkills, notify: false))\n                return false;");

            var train = Slice(skills, "public bool HandleActionTrainSkill(Skill skill, int creditsSpent)", "// PvP template");
            StringAssert.Contains(train, "if (MuleBlocked(MuleAction.ChangeSkills))\n                return false;");
        }

        [TestMethod]
        public void Skill_Alteration_Gems_Are_Refused_For_Mules_Before_Any_Prompt()
        {
            var body = Slice(Read("SkillAlterationDevice.cs"), "public void ActOnUse(WorldObject activator, bool confirmed)", "// verify skill");
            StringAssert.Contains(body, "if (player.MuleBlocked(MuleAction.ChangeSkills))\n                return;");
        }

        [TestMethod]
        public void Top_Up_Zeroes_Skill_Credits_And_Creates_Missing_Rows()
        {
            var body = Slice(Read("Player_Mule.cs"), "public List<GameMessage> ApplyMuleDefenseSkills()", "/// Applies the whole mule stat package");
            StringAssert.Contains(body, "AvailableSkillCredits = 0;");
            StringAssert.Contains(body, "GetCreatureSkill(skill);");
        }

        // ---- consumer pins: the call sites that make the above matter at runtime ----

        [TestMethod]
        public void Conversion_Keeps_Defense_Skills_And_Tops_Them_Up()
        {
            var body = Slice(Read("Player_Mule.cs"), "public void ApplyMuleConversion()", "// 7. bind to the mule lifestone");

            var loop = body.IndexOf("foreach (var skill in PlayerSkills)", StringComparison.Ordinal);
            var skip = body.IndexOf("IsMuleDefenseSkill(skill)", StringComparison.Ordinal);
            var untrain = body.IndexOf("SkillAdvancementClass.Untrained", StringComparison.Ordinal);
            Assert.IsTrue(loop >= 0 && skip > loop && untrain > skip, "the untrain loop must skip the defense skills before it untrains anything");
            Assert.IsTrue(body.Contains("ApplyMuleDefenseSkills()", StringComparison.Ordinal), "conversion must raise the defense skills");
        }

        [TestMethod]
        public void Login_Tops_Up_Every_Mule_Before_SendSelf()
        {
            var body = Slice(Read("Player_Networking.cs"), "public void PlayerEnterWorld()", "SendSelf();");

            var gate = body.IndexOf("if (IsMule)", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0, "login must be gated on IsMule");
            Assert.IsTrue(body.IndexOf("ApplyMuleDefenseSkills();", gate, StringComparison.Ordinal) > gate, "login must call ApplyMuleDefenseSkills under the IsMule gate, before SendSelf()");
        }

        [TestMethod]
        public void Wield_Check_Reads_Skills_Through_The_Pure_Decision()
        {
            var body = Slice(Read("Player_Inventory.cs"), "private WeenieError CheckWieldRequirement(", "case WieldRequirement.IntStat:");

            Assert.AreEqual(3, CountOf(body, "SkillWieldRequirementMet(wieldRequirement, () => skill.Current, () => skill.Base, skill.AdvancementClass, difficulty)"),
                "Skill, RawSkill and Training must each call the tested decision with exactly this argument order");
        }

        [TestMethod]
        public void Tunable_Is_Registered_In_Both_Snapshots()
        {
            var root = FindRepoRoot();
            StringAssert.Contains(File.ReadAllText(Path.Combine(root, "Source", "config-defaults.tsv")), "mule_defense_skill_level\tlong\t" + DefaultTarget());
            StringAssert.Contains(File.ReadAllText(Path.Combine(root, "Source", "ACE.Server", "config-metadata.tsv")), "mule_defense_skill_level\t");
        }

        private static int CountOf(string text, string needle)
        {
            var n = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        private static string Slice(string text, string from, string to)
        {
            var a = text.IndexOf(from, StringComparison.Ordinal);
            Assert.IsTrue(a >= 0, "missing: " + from);
            var b = text.IndexOf(to, a, StringComparison.Ordinal);
            Assert.IsTrue(b > a, "missing: " + to);
            return text.Substring(a, b - a);
        }

        private static string Read(string fileName)
            => File.ReadAllText(Path.Combine(FindRepoRoot(), "Source", "ACE.Server", "WorldObjects", fileName)).Replace("\r\n", "\n");

        private static string FindRepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Source", "config-defaults.tsv")))
                    return dir.FullName;

            throw new InvalidOperationException("Could not find Source/config-defaults.tsv by walking up from " + AppContext.BaseDirectory);
        }
    }
}
