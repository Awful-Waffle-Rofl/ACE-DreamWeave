using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the exact wording of the [PET] summoned / removed diagnostic lines (prod memory-ramp
    /// investigation) through the pure Pet.Format* helpers the live path calls. Reads no PropertyManager keys.
    /// </summary>
    [TestClass]
    public class PetLifecycleLogTests
    {
        [TestMethod]
        public void FormatSummoned_PinsExactLine()
        {
            var line = Pet.FormatSummoned("Bob's Wisp", 0x80001234, 12345, "CombatPet", "Bob", 0x50000001, "wcid12345", 0xA9B40012, 0, 43);

            Assert.AreEqual("[PET] summoned pet=Bob's Wisp (0x80001234) wcid=12345 type=CombatPet owner=Bob (0x50000001) source=wcid12345 cell=0xA9B40012 inst=0x00000000 lifespan=43", line);
        }

        [TestMethod]
        public void FormatSummoned_NoLifespanPrintsNone()
        {
            var line = Pet.FormatSummoned("Bob's Cow", 0x80001235, 7, "Pet", "Bob", 0x50000001, "wcid7", 0xA9B40012, 0x1A, null);

            Assert.IsTrue(line.EndsWith("inst=0x0000001A lifespan=none"), line);
        }

        [TestMethod]
        public void FormatRemoved_PinsExactLine()
        {
            var line = Pet.FormatRemoved("Bob's Wisp", 0x80001234, 12345, "CombatPet", "Bob", 0x50000001, "expire", 43.04);

            Assert.AreEqual("[PET] removed pet=Bob's Wisp (0x80001234) wcid=12345 type=CombatPet owner=Bob (0x50000001) reason=expire aliveS=43.0", line);
        }

        [TestMethod]
        public void FormatRemoved_NoOwnerAndUnknownAge()
        {
            var line = Pet.FormatRemoved("Wisp", 0x80001234, 12345, "CombatPet", null, null, "destroy", null);

            Assert.AreEqual("[PET] removed pet=Wisp (0x80001234) wcid=12345 type=CombatPet owner=none (0x00000000) reason=destroy aliveS=unknown", line);
        }

        [TestMethod]
        public void FormatSource_DeviceSpellsAndDuo()
        {
            var plain = new PetSummonSource(99, null, null, null, null, null, null, null, null);
            var caster = new PetSummonSource(99, null, null, null, null, null, null, 5, null);

            Assert.AreEqual("device4000", Pet.FormatSource(plain, 4000, false));
            Assert.AreEqual("device4000+spells", Pet.FormatSource(caster, 4000, false));
            Assert.AreEqual("device4000+duo", Pet.FormatSource(plain, 4000, true));
            Assert.AreEqual("device4000+spells+duo", Pet.FormatSource(caster, 4000, true));
            Assert.AreEqual("none", Pet.FormatSource(null, null, false));
        }

        [TestMethod]
        public void FormatRemoved_CarriesEveryReasonToken()
        {
            foreach (var reason in new[] { "death", "destroy", "dismiss", "swap", "expire", "ownerlogout", "unload", "facetpk", "ownerlost", "leash" })
            {
                var line = Pet.FormatRemoved("Wisp", 0x80001234, 1, "Pet", "Bob", 0x50000001, reason, 1.25);

                Assert.IsTrue(line.EndsWith($"reason={reason} aliveS=1.2") || line.EndsWith($"reason={reason} aliveS=1.3"), line);
            }
        }

        // ---- wiring pins: a source scan, so a refactor that drops a hook fails here rather than going quiet ----

        [TestMethod]
        public void WorldObjectDestroy_CallsLogRemoved()
        {
            var body = MethodBody("Source/ACE.Server/WorldObjects/WorldObject.cs", "public void Destroy(");

            Assert.IsTrue(body.Contains("pet.LogRemoved("), "WorldObject.Destroy no longer calls pet.LogRemoved(.");
        }

        [TestMethod]
        public void PetInit_GuardsLogSummonedForCombatPet()
        {
            var body = MethodBody("Source/ACE.Server/WorldObjects/Pet.cs", "public virtual bool? Init(");

            var guard = body.IndexOf("!(this is CombatPet)");
            var call = body.IndexOf("LogSummoned(");

            Assert.IsTrue(guard >= 0, "Pet.Init lost the !(this is CombatPet) guard.");
            Assert.IsTrue(call > guard, "Pet.Init must call LogSummoned( after the !(this is CombatPet) guard.");
        }

        [TestMethod]
        public void CombatPetInit_CallsLogSummoned()
        {
            var body = MethodBody("Source/ACE.Server/WorldObjects/CombatPet.cs", "public override bool? Init(");

            Assert.IsTrue(body.Contains("LogSummoned("), "CombatPet.Init no longer calls LogSummoned(.");
        }

        /// <summary>Text of the member whose declaration contains <paramref name="signature"/>, up to its closing brace at class-member indent.</summary>
        private static string MethodBody(string relativePath, string signature)
        {
            var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"{relativePath} not found at {path}.");

            var source = File.ReadAllText(path).Replace("\r\n", "\n");
            var start = source.IndexOf(signature);

            Assert.IsTrue(start >= 0, $"{signature} not found in {relativePath}.");

            var end = source.IndexOf("\n        }\n", start);

            Assert.IsTrue(end > start, $"closing brace of {signature} not found in {relativePath}.");

            return source.Substring(start, end - start);
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
