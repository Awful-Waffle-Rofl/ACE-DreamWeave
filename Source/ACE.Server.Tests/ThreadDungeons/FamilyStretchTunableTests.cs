using System;
using System.IO;

using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Pins the dynamic_dungeons_family_stretch tunable and the limits field. Reads DefaultPropertyManager's immutable
    /// default tables and source text only, never the PropertyManager caches, so it needs no seeding.
    /// </summary>
    [TestClass]
    public class FamilyStretchTunableTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestMethod]
        public void Tunable_registered_default_true()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["dynamic_dungeons_family_stretch"].Item);
        }

        [TestMethod]
        public void Compiled_default_constant_is_true()
        {
            Assert.AreEqual(true, typeof(DungeonPopulationLimits).GetField(nameof(DungeonPopulationLimits.DefaultFamilyStretch)).GetRawConstantValue());
        }

        [TestMethod]
        public void Code_built_limits_default_off_and_copies_keep_the_flag()
        {
            Assert.IsFalse(new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5).FamilyStretch);

            var on = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, familyStretch: true);
            Assert.IsTrue(on.WithMonsterCounts(1, 2).FamilyStretch);
            Assert.IsTrue(on.WithForcedFamilyWidening().FamilyStretch);
            Assert.IsTrue(on.WithoutGroup().FamilyStretch);
        }

        [TestMethod]
        public void ReadLimits_passes_the_tunable()
        {
            var path = Path.Combine(FindRepoRoot(), "Source", "ACE.Server", "ThreadDungeons", "ThreadDungeonSpawner.cs");
            Assert.IsTrue(File.Exists(path), $"ThreadDungeonSpawner.cs not found at {path}.");

            StringAssert.Contains(File.ReadAllText(path),
                "familyStretch: PropertyManager.GetBool(\"dynamic_dungeons_family_stretch\", DungeonPopulationLimits.DefaultFamilyStretch).Item");
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Source", "property-registry.tsv")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new AssertFailedException($"Could not find the repo root by walking up from {AppContext.BaseDirectory}.");
        }
    }
}
