using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The four cloak tunables and the key names Cloak.cs actually reads.
    ///
    /// Cloak.cs read <c>cloak_min_proc_base</c> for years while the registered key is <c>cloak_min_proc</c>.
    /// Nothing caught it, because the shipped default of that lever is 0 and PropertyManager.GetDouble falls
    /// back to the caller's literal for an unregistered key - so the wrong name produced exactly the right
    /// number and the only observable symptom was that ModifyDouble (and therefore /config) refused to set the
    /// lever at all. This class is the regression guard: it pins the registered set, and it scans Cloak.cs for
    /// any cloak_* key it reads that is not in that set.
    /// </summary>
    [TestClass]
    public class CloakTunableKeyTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// The registered cloak doubles and their defaults, as PropertyManager declares them. Pinned as
        /// literals so a default moving is a visible edit here, and so the NEGATIVE half below has something
        /// to be negative about.
        /// </summary>
        [TestMethod]
        public void RegisteredCloakDoubles_AreTheFourExpectedKeys()
        {
            var cloakKeys = DefaultPropertyManager.DefaultDoubleProperties.Keys
                .Where(k => k.StartsWith("cloak_"))
                .OrderBy(k => k, System.StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { "cloak_cooldown_seconds", "cloak_max_proc_base", "cloak_max_proc_damage_percentage", "cloak_min_proc" },
                cloakKeys,
                "the set of registered cloak_* double tunables changed");

            Assert.AreEqual(5.0, DefaultPropertyManager.DefaultDoubleProperties["cloak_cooldown_seconds"].Item, 1e-9);
            Assert.AreEqual(0.25, DefaultPropertyManager.DefaultDoubleProperties["cloak_max_proc_base"].Item, 1e-9);
            Assert.AreEqual(0.30, DefaultPropertyManager.DefaultDoubleProperties["cloak_max_proc_damage_percentage"].Item, 1e-9);
            Assert.AreEqual(0.0, DefaultPropertyManager.DefaultDoubleProperties["cloak_min_proc"].Item, 1e-9);

            // THE NEGATIVE CONTROL, and the actual bug: the name Cloak.cs used to read is not registered, and
            // never was. ModifyDouble is the discriminator, because it refuses an unregistered key outright
            // where GetDouble silently falls back to the caller's literal.
            Assert.IsFalse(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("cloak_min_proc_base"));
            Assert.IsFalse(PropertyManager.ModifyDouble("cloak_min_proc_base", 0.05),
                "control: an unregistered key cannot be set at all, which is why the old read was an unusable lever");
            Assert.IsTrue(PropertyManager.ModifyDouble("cloak_min_proc", 0.0),
                "the registered key must be settable");
        }

        /// <summary>
        /// SOURCE SCAN: every cloak_* key Cloak.cs reads is one of the registered four. A GetDouble against an
        /// unregistered key compiles, runs, and returns the caller's literal, so only reading the source
        /// catches a typo like the one this test exists for.
        /// </summary>
        [TestMethod]
        public void CloakSourceReadsOnlyRegisteredCloakKeys_SourceScan()
        {
            var path = Path.Combine(
                ClassAbilityAffinityDeclarationTests.RepoRoot(),
                "Source", "ACE.Server", "Entity", "Cloak.cs");

            Assert.IsTrue(File.Exists(path), $"Cloak.cs not found at {path}");

            var text = File.ReadAllText(path);

            // only the key names actually passed to a PropertyManager getter, so the doc comments in this file
            // (which legitimately NAME the unregistered key in order to explain the bug) are not scanned
            var read = Regex.Matches(text, @"PropertyManager\.Get\w+\(\s*""(?<key>cloak_[a-z0-9_]+)""")
                .Select(m => m.Groups["key"].Value)
                .Distinct()
                .OrderBy(k => k, System.StringComparer.Ordinal)
                .ToArray();

            Assert.AreNotEqual(0, read.Length, "control: the scan must actually find some cloak_* reads");

            var unregistered = read
                .Where(k => !DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(k))
                .ToArray();

            Assert.AreEqual(0, unregistered.Length,
                "Cloak.cs reads cloak_* tunable key(s) that PropertyManager does not register, so they can " +
                "never be set in-game and silently return the caller's literal fallback: " +
                string.Join(", ", unregistered));
        }
    }
}
