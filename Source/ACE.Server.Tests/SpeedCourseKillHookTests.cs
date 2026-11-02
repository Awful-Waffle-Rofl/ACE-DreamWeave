using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Speed-course kills feed no kill-fill vessel charge and no mule-form token progress. The hooks themselves need a
    /// live Player (see KillFillVesselTests / MuleFormTokenTests), so this pins the predicate and the wiring by source text:
    /// the two hooks are gated on it, the combat hooks are not, the gate is not IsRewardlessDeath, and the speed-course
    /// spawner sets the flag.
    /// </summary>
    [TestClass]
    public class SpeedCourseKillHookTests
    {
        private static string Read(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Creature_Death.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server/WorldObjects/Creature_Death.cs by walking up from {AppContext.BaseDirectory}");

            var path = Path.Combine(dir.FullName, "Source", "ACE.Server");

            foreach (var p in parts)
                path = Path.Combine(path, p);

            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        [TestMethod]
        public void Predicate_OnlyASpeedCourseCreatureSkipsTheProgressHooks()
        {
            Assert.IsTrue(Creature.FeedsKillProgressHooks(false));
            Assert.IsFalse(Creature.FeedsKillProgressHooks(true));
        }

        [TestMethod]
        public void OnDeath_GatesTheVesselAndMuleTokenHooks_AndNothingElse()
        {
            var src = Read("WorldObjects", "Creature_Death.cs");

            StringAssert.Contains(src, "var feedsProgress = FeedsKillProgressHooks(P_SpeedCourseCreature);");
            Assert.IsTrue(Regex.IsMatch(src, @"if \(feedsProgress\)\s*\n\s*killingPlayer\.ApplyKillFillVesselCreatureDeath\(this\);"), "the vessel hook is gated");
            Assert.IsTrue(Regex.IsMatch(src, @"if \(feedsProgress\)\s*\n\s*killingPlayer\.ApplyMuleFormTokenCreatureDeath\(this\);"), "the mule-token hook is gated");

            Assert.IsFalse(Regex.IsMatch(src, @"if \(feedsProgress\)\s*\n\s*killingPlayer\.ApplyCreatureDeathClassAbilities"), "class abilities still fire");
            Assert.IsFalse(Regex.IsMatch(src, @"if \(feedsProgress\)\s*\n\s*killingPlayer\.ApplyWeaponModCreatureDeath"), "weapon mods still fire");

            Assert.AreEqual(1, Regex.Matches(src, @"FeedsKillProgressHooks\(").Count, "one gate, read once");
            Assert.IsFalse(src.Contains("IsRewardlessDeath(P_SpeedCourseCreature"), "not routed through IsRewardlessDeath");
        }

        [TestMethod]
        public void SpeedCourseSpawner_FlagsEveryCreature()
        {
            var src = Read("SpeedCourses", "LiveSpeedCourseWorld.cs");

            StringAssert.Contains(src, "creature.P_SpeedCourseCreature = true;");
            Assert.IsTrue(src.IndexOf("creature.P_SpeedCourseCreature = true;", StringComparison.Ordinal) < src.IndexOf("creature.EnterWorld()", StringComparison.Ordinal),
                "flagged before it enters the world");
        }
    }
}
