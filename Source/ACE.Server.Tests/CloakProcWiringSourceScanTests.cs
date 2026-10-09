using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SOURCE SCANS, not behaviour tests, and named so. Nothing here runs a cloak proc; each method reads
    /// ACE.Server's own text and asserts a structural property that no unit test could reach, because the
    /// thing being protected is "a site added in a future commit still gets this", which by definition has no
    /// call to exercise today.
    ///
    /// WHAT THEY PROTECT. Cloaked in Power (Vanguard T2, 2026-09-29) reads its chance floor inside
    /// Cloak.TryProcSpell rather than having each of the four cloak spell-proc call sites pass one in. That
    /// makes a FIFTH site inherit the floor automatically - but only for as long as TryProcSpell stays the one
    /// way to reach HandleProcSpell, and only for as long as TryProcSpell keeps reading the floor. Those two
    /// facts are exactly what a source scan can pin and a behavioural test cannot.
    ///
    /// A source scan is weaker evidence than an executed assertion and it is worth being explicit about how:
    /// it matches TEXT, so a call routed through a local alias, an interface or reflection would slip past it.
    /// It is the right tool here anyway, because the failure being guarded against is a human adding a plain
    /// `Cloak.RollProc(...)` + `Cloak.HandleProcSpell(...)` pair by copy-paste, which is textual.
    /// </summary>
    [TestClass]
    public class CloakProcWiringSourceScanTests
    {
        private static string ServerRoot()
        {
            var root = ClassAbilityAffinityDeclarationTests.RepoRoot();
            var dir = Path.Combine(root, "Source", "ACE.Server");

            Assert.IsTrue(Directory.Exists(dir), $"Could not find Source/ACE.Server under repo root {root}.");

            return dir;
        }

        private static string ReadServerFile(string relative)
        {
            var path = Path.Combine(ServerRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"{relative} does not exist under Source/ACE.Server.");

            return File.ReadAllText(path);
        }

        /// <summary>
        /// Cloak.HandleProcSpell - the method that actually casts the proc spell - is reachable from exactly
        /// ONE place in ACE.Server, the call inside Cloak.TryProcSpell. That is what makes TryProcSpell the
        /// single chokepoint the floor and the Taunt hook can sit in: a new site cannot roll and cast a cloak
        /// proc without going through it.
        /// </summary>
        [TestMethod]
        public void HandleProcSpell_IsCalledOnlyFromTryProcSpellInsideCloak_SourceScan()
        {
            var offenders = Directory
                .GetFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "Cloak.cs", StringComparison.OrdinalIgnoreCase))
                .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bHandleProcSpell\s*\("))
                .Select(f => Path.GetRelativePath(ServerRoot(), f))
                .OrderBy(f => f)
                .ToArray();

            Assert.AreEqual(0, offenders.Length,
                "Cloak.HandleProcSpell is called outside Cloak.cs, which means a cloak spell proc can now be " +
                "rolled and cast without passing through Cloak.TryProcSpell - and therefore without the " +
                "Cloaked in Power floor or the Taunt hook. Route it through TryProcSpell instead:\n" +
                string.Join("\n", offenders));

            // and inside Cloak.cs it is called exactly once
            var cloak = ReadServerFile("Entity/Cloak.cs");
            var calls = Regex.Matches(cloak, @"\bHandleProcSpell\s*\(").Count;

            Assert.AreEqual(2, calls,
                "expected exactly two 'HandleProcSpell(' occurrences in Cloak.cs - its declaration and the one " +
                "call from TryProcSpell. A third is a second cast path that bypasses the floor.");
        }

        /// <summary>
        /// TryProcSpell still reads the floor itself and still hands it to RollProc. Without this, the
        /// chokepoint above is a chokepoint for nothing.
        /// </summary>
        [TestMethod]
        public void TryProcSpell_ReadsTheCloakedInPowerFloorAndPassesItToRollProc_SourceScan()
        {
            var cloak = ReadServerFile("Entity/Cloak.cs");

            var body = Regex.Match(cloak,
                @"public static bool TryProcSpell\s*\([^)]*\)\s*\{(?<body>.*?)\n        \}",
                RegexOptions.Singleline);

            Assert.IsTrue(body.Success, "could not locate the body of Cloak.TryProcSpell");

            var text = body.Groups["body"].Value;

            var read = Regex.Match(text, @"var\s+(?<var>[A-Za-z_]\w*)\s*=\s*CloakedInPowerFloor\s*\(\s*defender\s*\)");

            Assert.IsTrue(read.Success,
                "Cloak.TryProcSpell no longer reads the Cloaked in Power floor from its defender into a local. " +
                "Every cloak spell-proc site relies on it doing so - none of them passes a floor of its own.");

            // THE THIRD ARGUMENT MUST BE THAT LOCAL BY NAME, not merely "some third argument". An earlier draft
            // of this scan accepted \w+ there and a discrimination run passed a literal 0f straight through it -
            // the floor was read and then thrown away, and the test stayed green. Binding the name is the fix.
            Assert.IsTrue(Regex.IsMatch(text, @"RollProc\s*\(\s*cloak\s*,\s*damage_percent\s*,\s*" + Regex.Escape(read.Groups["var"].Value) + @"\s*\)"),
                $"Cloak.TryProcSpell reads the floor into '{read.Groups["var"].Value}' but does not pass THAT value " +
                "to RollProc as its third argument, so the floor it read is being discarded.");

            Assert.IsTrue(Regex.IsMatch(text, @"OnClassAbilityItemProc\s*\(\s*cloak\s*\)"),
                "Cloak.TryProcSpell no longer dispatches the IItemProcAbility hook, so Taunt stops firing on a " +
                "cloak proc.");
        }

        /// <summary>
        /// The four cloak spell-proc sites, enumerated so a fifth one showing up is a visible, deliberate
        /// change to this list rather than a silent addition. The floor reaches all of them through
        /// TryProcSpell, so a fifth site is NOT a bug - this test exists to make it noticed, and its failure
        /// message says so.
        /// </summary>
        [TestMethod]
        public void CloakSpellProcSites_AreTheFourKnownFiles_SourceScan()
        {
            var expected = new[]
            {
                "WorldObjects/Player_Combat.cs",        // melee / missile / hotspot, inside Player.TakeDamage
                "WorldObjects/SpellProjectile.cs",      // damaging spell projectiles
                "WorldObjects/WorldObject_Magic.cs",    // harmful life boost (Harm) AND Drain Health
            };

            var found = Directory
                .GetFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(File.ReadAllText(f), @"Cloak\.TryProcSpell\s*\("))
                .Select(f => Path.GetRelativePath(ServerRoot(), f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEquivalent(expected, found,
                "the set of files calling Cloak.TryProcSpell changed. That is not automatically wrong - a new " +
                "site inherits the Cloaked in Power floor and the Taunt hook for free, because TryProcSpell " +
                "computes both itself. Confirm the new site passes the CLOAK WEARER as its defender argument " +
                "(every existing one does, via defender.EquippedCloak), then update this list.\nexpected: " +
                string.Join(", ", expected) + "\nfound: " + string.Join(", ", found));

            // four CALL sites across those three files - WorldObject_Magic carries two (Harm and Drain Health)
            var total = found.Sum(f => Regex.Matches(
                File.ReadAllText(Path.Combine(ServerRoot(), f.Replace('/', Path.DirectorySeparatorChar))),
                @"Cloak\.TryProcSpell\s*\(").Count);

            Assert.AreEqual(4, total,
                "expected exactly 4 Cloak.TryProcSpell call sites (Player_Combat 1, SpellProjectile 1, " +
                "WorldObject_Magic 2). A change here needs the same confirmation as above.");
        }

        /// <summary>
        /// TakeDamageOverTime deliberately carries no cloak call, so a DoT tick does not proc a cloak. Cloaked
        /// in Power raises a chance at sites that already rolled and must not add a new site - a 10% floor on
        /// every DoT tick would be a different ability.
        /// </summary>
        [TestMethod]
        public void TakeDamageOverTime_StillHasNoCloakCall_SourceScan()
        {
            var text = ReadServerFile("WorldObjects/Player_Combat.cs");

            var body = Regex.Match(text,
                @"public (?:override )?void TakeDamageOverTime\s*\([^)]*\)\s*\{(?<body>.*?)\n        \}",
                RegexOptions.Singleline);

            Assert.IsTrue(body.Success,
                "could not locate Player.TakeDamageOverTime - if it moved or was renamed, re-point this scan " +
                "rather than deleting it.");

            Assert.IsFalse(body.Groups["body"].Value.Contains("Cloak."),
                "Player.TakeDamageOverTime now references Cloak, which would make damage-over-time ticks roll " +
                "a cloak proc. That was deliberately excluded and Cloaked in Power did not change it.");
        }
    }
}
