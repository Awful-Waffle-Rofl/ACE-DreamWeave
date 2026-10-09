using System;
using System.IO;
using System.Linq;

using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Rare-drop eligibility of band-uplifted World Event monsters is gated on the AUTHORED level
    /// (Creature.LootGateLevel), not the raised one. Creature_Death recomputes CanGenerateRare from the level at
    /// death, so the gate is applied to the rule's INPUT.
    /// </summary>
    [TestClass]
    public class WorldEventLootGateTests
    {
        private static bool Eligible(int? levelRuleReads, int killerLevel)
            => Creature.ResolveCanGenerateRare(false, levelRuleReads, true, true, false, () => killerLevel);

        [TestMethod]
        public void LootGateLevelFor_PrefersTheGate_AndFallsBackToTheLiveLevel()
        {
            Assert.AreEqual(80, Creature.LootGateLevelFor(80, 275));
            Assert.AreEqual(275, Creature.LootGateLevelFor(null, 275));
            Assert.IsNull(Creature.LootGateLevelFor(null, null));
        }

        [TestMethod]
        public void AnUpliftedMonster_IsRareEligibleOnItsAuthoredLevel_NotItsRaisedOne()
        {
            // Authored 80 raised to 275, killed by a level-200 player.
            Assert.IsTrue(Eligible(275, 200), "the raised level alone would make it eligible (>= 100)");
            Assert.IsFalse(Eligible(Creature.LootGateLevelFor(80, 275), 200), "gated on 80: below 100 and not above the killer's 200");
        }

        [TestMethod]
        public void AGatedMonster_StillQualifiesWhenItsAuthoredLevelDoes()
        {
            Assert.IsTrue(Eligible(Creature.LootGateLevelFor(120, 275), 200), "authored 120 >= 100");
            Assert.IsTrue(Eligible(Creature.LootGateLevelFor(60, 275), 50), "authored 60 is above a level-50 killer");
        }

        [TestMethod]
        public void CreateCorpse_ReadsTheGate_ForTheRareRule()
        {
            var text = File.ReadAllText(Path.Combine(FindSourceRoot(), "WorldObjects", "Creature_Death.cs"));

            Assert.IsTrue(text.Contains("ResolveCanGenerateRare(false, LootGateLevelFor(LootGateLevel, Level), true, true, false,"));
        }

        [TestMethod]
        public void OnlyTheWorldEventUpliftPath_SetsTheGate_BeforeApply()
        {
            var root = FindSourceRoot();
            var spawner = File.ReadAllLines(Path.Combine(root, "WorldEvents", "WorldEventSpawner.cs"));

            int Find(string code) => Array.FindIndex(spawner, l => l.Trim() == code);

            var set = Find("creature.LootGateLevel = authoredLevel;");
            var apply = Find("var (skillsRaised, partsScaled) = BandUplift.Apply(creature, upliftLevel, standard);");

            Assert.IsTrue(set > 0 && apply > 0, "anchor lines must exist exactly once");
            Assert.IsTrue(set < apply, "the authored level is recorded before Apply raises Level");

            foreach (var file in Directory.GetFiles(Path.Combine(root, "ThreadDungeons"), "*.cs", SearchOption.AllDirectories))
                Assert.IsFalse(File.ReadAllText(file).Contains("LootGateLevel"), $"{Path.GetFileName(file)}: Threads must not set the gate");
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "WorldObjects", "Creature_LootRolls.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/WorldObjects/Creature_LootRolls.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "ACE.Server");
        }
    }
}