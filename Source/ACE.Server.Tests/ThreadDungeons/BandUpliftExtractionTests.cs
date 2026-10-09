using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Pins the commit-1 extraction of the band uplift and the combat-trait guard out of the Threads spawner
    /// so World Events can share them: the helpers' pure halves behave as the Threads originals did (the
    /// body-part clone above all), and Threads still CALLS them with plan-derived arguments. A live Creature
    /// cannot be built in this harness, so the Apply wrappers are pinned by a source scan on code lines only.
    /// </summary>
    [TestClass]
    public class BandUpliftExtractionTests
    {
        private static PropertiesBodyPart Part(int dval, int armor) => new PropertiesBodyPart { DVal = dval, BaseArmor = armor };

        [TestMethod]
        public void ApplyBodyPartUplift_ClonesTheSharedCollection_AndScalesTheCopy()
        {
            var shared = new Dictionary<CombatBodyPart, PropertiesBodyPart>
            {
                [CombatBodyPart.Head] = Part(10, 100),
                [CombatBodyPart.Chest] = Part(20, 200),
            };
            var biota = new Biota { PropertiesBodyPart = shared };

            var scaled = BandUplift.ApplyBodyPartUplift(biota, standardMaxDamage: 40, standardMaxArmor: 400);

            Assert.AreEqual(2, scaled);
            Assert.AreNotSame(shared, biota.PropertiesBodyPart, "the shared (cached-weenie) collection must be replaced, not mutated");
            Assert.AreEqual(10, shared[CombatBodyPart.Head].DVal, "the cached weenie's part must be untouched");
            Assert.AreEqual(200, shared[CombatBodyPart.Chest].BaseArmor, "the cached weenie's part must be untouched");
            Assert.AreEqual(40, biota.PropertiesBodyPart[CombatBodyPart.Chest].DVal, "own max 20 -> standard 40 is a x2 ratio on every part");
            Assert.AreEqual(20, biota.PropertiesBodyPart[CombatBodyPart.Head].DVal);
        }

        [TestMethod]
        public void ApplyBodyPartUplift_AtOrAboveStandard_ChangesNothingAndDoesNotClone()
        {
            var shared = new Dictionary<CombatBodyPart, PropertiesBodyPart> { [CombatBodyPart.Head] = Part(50, 500) };
            var biota = new Biota { PropertiesBodyPart = shared };

            Assert.AreEqual(0, BandUplift.ApplyBodyPartUplift(biota, 40, 400));
            Assert.AreSame(shared, biota.PropertiesBodyPart);
        }

        [TestMethod]
        public void SpawnerStubs_DelegateToTheExtractedHelpers()
        {
            Assert.AreEqual(BandUplift.UpliftRatio(10, 25), ThreadDungeonSpawner.UpliftRatio(10, 25));
            Assert.AreEqual(BandUplift.ScaleBodyValue(10, 2.5), ThreadDungeonSpawner.ScaleBodyValue(10, 2.5));
            Assert.AreEqual(DungeonStatProfile.HealthFromAttributes(100, 20, 41), ThreadDungeonSpawner.HealthFromAttributes(100, 20, 41));
            Assert.AreEqual(140u, ThreadDungeonSpawner.HealthFromAttributes(100, 20, 41), "100 + 20 + floor(41 / 2)");
        }

        [TestMethod]
        public void FromWeenie_ReadsHealthSkillsAndBodyPartMaxima()
        {
            var weenie = new Weenie
            {
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 90 },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    [PropertyAttribute2nd.MaxHealth] = new PropertiesAttribute2nd { InitLevel = 300, LevelFromCP = 10 },
                },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    [PropertyAttribute.Endurance] = new PropertiesAttribute { InitLevel = 100 },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill> { [Skill.MeleeDefense] = new PropertiesSkill { InitLevel = 250 } },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    [CombatBodyPart.Head] = Part(30, 80),
                    [CombatBodyPart.Chest] = Part(55, 60),
                },
            };

            var profile = DungeonStatProfile.FromWeenie(weenie);

            Assert.AreEqual(90, profile.Level);
            Assert.AreEqual(360u, profile.Health, "300 + 10 + floor(100 / 2)");
            Assert.AreEqual(250u, profile.Skills[Skill.MeleeDefense]);
            Assert.AreEqual(55u, profile.MaxBodyDamage);
            Assert.AreEqual(80u, profile.MaxBaseArmor);
            Assert.AreEqual(360u, DungeonStatProfile.HealthOf(weenie));
            Assert.AreSame(DungeonStatProfile.Empty, DungeonStatProfile.FromWeenie(null));
        }

        // ---- source-text pins: Threads still calls the helpers with plan-derived arguments ----------

        [TestMethod]
        public void ThreadsSpawner_StillCallsBandUplift_WithThePlanStandard()
        {
            AssertCodeLine("ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs", "var standard = plan.BandStandard;");
            AssertCodeLine("ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs", "var (skillsRaised, partsScaled) = BandUplift.Apply(creature, entry.UpliftLevel, standard);");
            AssertCodeLine("ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs", "=> DungeonStatProfile.FromWeenie(weenie);");
            AssertCodeLine("ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs", "=> DungeonStatProfile.HealthOf(weenie);");
        }

        [TestMethod]
        public void StripCombatTraits_StillForwardsPlanDerivedDials()
        {
            const string file = "ACE.Server/ThreadDungeons/DungeonCreatureNormalizer.cs";
            AssertCodeLine(file, "var dials = new CombatGuardDials(plan.CategoryResistFloor, plan.CategoryArmorModCeiling, plan.DefenseSkillCapOffset);");
            // Re-pinned 2026-10-08 (run ceiling 500): a reach-up STAMPED entry passes the curve standard at its
            // own stamp level as capStandard, so its defense cap is measured where it actually fights. Every
            // other caller passes null and still gets the plan's standard, which is what this pin guards.
            AssertCodeLine(file, "return CombatTraitGuard.Apply(creature, dials, capStandard ?? plan.BandStandard, isBoss);");
            AssertCodeLine("ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs",
                "var capStandard = DefenseCapStandard(entry, plan);");
        }

        private static void AssertCodeLine(string file, string code)
        {
            var path = Path.Combine(FindSourceRoot(), file.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"missing {path}");

            var hits = 0;

            foreach (var raw in File.ReadAllLines(path))
            {
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var codeOnly = (commentAt >= 0 ? raw.Substring(0, commentAt) : raw).Trim();

                if (codeOnly == code)
                    hits++;
            }

            Assert.AreEqual(1, hits, $"expected exactly 1 code line in {file} matching `{code}`");
        }

        internal static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "ThreadDungeons", "BandUplift.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/ThreadDungeons/BandUplift.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
