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
    /// Cloak.TryProcSpell rather than having each of the six cloak spell-proc call sites (the Block one passes 0f, floor only)
    /// pass one in. That makes a SEVENTH site inherit the floor automatically - but only for as long as TryProcSpell stays the one
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

        /// <summary>
        /// Drops `//` and `///` comment text, so a comment that merely MENTIONS Cloak.TryProcSpell( cannot
        /// inflate a count or satisfy an assertion. Line-based and deliberately crude: it would also eat a
        /// "//" inside a string literal, which nothing in the scanned regions contains.
        /// </summary>
        private static string StripLineComments(string text) =>
            Regex.Replace(text, @"//[^\r\n]*", string.Empty);

        private static string ReadServerFile(string relative)
        {
            var path = Path.Combine(ServerRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"{relative} does not exist under Source/ACE.Server.");

            return StripLineComments(File.ReadAllText(path));
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
                .Where(f => Regex.IsMatch(StripLineComments(File.ReadAllText(f)), @"\bHandleProcSpell\s*\("))
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
        /// The six cloak spell-proc sites, enumerated so a seventh one showing up is a visible, deliberate
        /// change to this list rather than a silent addition. The floor reaches all of them through
        /// TryProcSpell, so a new site is NOT a bug - this test exists to make it noticed, and its failure
        /// message says so.
        /// </summary>
        [TestMethod]
        public void CloakSpellProcSites_AreTheSixKnownFiles_SourceScan()
        {
            var expected = new[]
            {
                "WorldObjects/Player_Combat.cs",                    // melee / missile / hotspot, inside Player.TakeDamage
                "WorldObjects/SpellProjectile.cs",                  // damaging spell projectiles
                "WorldObjects/WorldObject_Magic.cs",                // harmful life boost (Harm) AND Drain Health
                "WorldObjects/Player_ClassAbilityReflectAndPin.cs", // Reflect (Vanguard T3) projectile bounce
                "WorldObjects/Player_ClassAbilityCombat.cs",        // Shield Block, floor only
            };

            var found = Directory
                .GetFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(StripLineComments(File.ReadAllText(f)), @"Cloak\.TryProcSpell\s*\("))
                .Select(f => Path.GetRelativePath(ServerRoot(), f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEquivalent(expected, found,
                "the set of files calling Cloak.TryProcSpell changed. That is not automatically wrong - a new " +
                "site inherits the Cloaked in Power floor and the Taunt hook for free, because TryProcSpell " +
                "computes both itself. Confirm the new site passes the CLOAK WEARER as its defender argument " +
                "(every existing one does, via defender.EquippedCloak), then update this list.\nexpected: " +
                string.Join(", ", expected) + "\nfound: " + string.Join(", ", found));

            // six CALL sites across those five files - WorldObject_Magic carries two (Harm and Drain Health)
            var total = found.Sum(f => Regex.Matches(
                StripLineComments(File.ReadAllText(Path.Combine(ServerRoot(), f.Replace('/', Path.DirectorySeparatorChar)))),
                @"Cloak\.TryProcSpell\s*\(").Count);

            Assert.AreEqual(6, total,
                "expected exactly 6 Cloak.TryProcSpell call sites (Player_Combat 1, SpellProjectile 1, " +
                "WorldObject_Magic 2, Reflect bounce 1, Shield Block 1). A change here needs the same " +
                "confirmation as above.");
        }

        /// <summary>The Cloak.TryProcSpell call statements in a file, each as its full argument text.</summary>
        private static string[] TryProcSpellCalls(string relative)
        {
            return Regex.Matches(ReadServerFile(relative), @"Cloak\.TryProcSpell\s*\((?<args>[^;]*)\)\s*;")
                .Select(m => Regex.Replace(m.Groups["args"].Value, @"\s+", " ").Trim())
                .ToArray();
        }

        /// <summary>
        /// The Reflect bounce rolls the cloak spell proc itself, because a bounce returns TRUE to its caller
        /// before TakeDamage / DamageTarget run. The roll must sit inside TryReflectProjectile, after the
        /// bounce chance roll has succeeded and before the success return, and must roll on the bounced damage.
        /// (The bounce held a latch until #1819; the anchor is the chance roll now, so a refused bounce never
        /// reaches the cloak.)
        /// </summary>
        [TestMethod]
        public void ReflectBounce_RollsCloakSpellProcAfterASuccessfulBounceRoll_SourceScan()
        {
            var text = ReadServerFile("WorldObjects/Player_ClassAbilityReflectAndPin.cs");

            var body = Regex.Match(text,
                @"public bool TryReflectProjectile\s*\([^)]*\)\s*\{(?<body>.*?)\n        \}",
                RegexOptions.Singleline);

            Assert.IsTrue(body.Success, "could not locate Player.TryReflectProjectile");

            var b = body.Groups["body"].Value;

            var roll = b.IndexOf("ReflectMagicAbility.ChanceFor(", StringComparison.Ordinal);
            var call = b.IndexOf("Cloak.TryProcSpell(", StringComparison.Ordinal);
            var ret = b.LastIndexOf("return true;", StringComparison.Ordinal);

            Assert.IsTrue(call >= 0, "TryReflectProjectile no longer rolls the cloak spell proc on a successful bounce.");
            Assert.IsTrue(roll >= 0 && roll < call && call < ret,
                "the cloak roll must come after the bounce chance roll and before the success return.");

            Assert.IsTrue(Regex.IsMatch(b, @"Cloak\.HasProcSpell\s*\(\s*\w+\s*\)"),
                "the bounce roll must be gated on Cloak.HasProcSpell like every other site.");

            Assert.IsTrue(Regex.IsMatch(b, @"Cloak\.TryProcSpell\s*\(\s*this\s*,\s*firer\s*,\s*\w+\s*,\s*damage\s*/\s*Health\.MaxValue\s*\)"),
                "the bounce must roll on the bounced damage (damage / Health.MaxValue) with this player as the defender and the firer as the attacker.");
        }

        /// <summary>
        /// Shield Block, FLOOR ONLY: the Block branch of OnClassAbilityAttackAvoided rolls the cloak spell
        /// proc with a literal 0f (a blocked hit has no damage figure), and the Parry branch does not.
        /// </summary>
        [TestMethod]
        public void ShieldBlock_RollsCloakSpellProcWithZeroPercentAndParryDoesNot_SourceScan()
        {
            var text = ReadServerFile("WorldObjects/Player_ClassAbilityCombat.cs");

            var body = Regex.Match(text,
                @"public void OnClassAbilityAttackAvoided\s*\([^)]*\)\s*\{(?<body>.*?)\n        \}",
                RegexOptions.Singleline);

            Assert.IsTrue(body.Success, "could not locate Player.OnClassAbilityAttackAvoided");

            var b = body.Groups["body"].Value;

            var blockAt = b.IndexOf("ClassAbilityAvoidanceOutcome.Block)", StringComparison.Ordinal);
            var parryAt = b.IndexOf("ClassAbilityAvoidanceOutcome.Parry)", StringComparison.Ordinal);

            Assert.IsTrue(blockAt >= 0 && parryAt > blockAt, "could not locate the Block and Parry branches in order.");

            var blockBranch = b.Substring(blockAt, parryAt - blockAt);
            var parryBranch = b.Substring(parryAt);

            Assert.IsTrue(Regex.IsMatch(blockBranch, @"Cloak\.TryProcSpell\s*\(\s*this\s*,\s*attacker\s*,\s*\w+\s*,\s*0f\s*\)"),
                "the Block branch must call Cloak.TryProcSpell(this, attacker, cloak, 0f) - floor only, by owner ruling.");

            Assert.IsTrue(Regex.IsMatch(blockBranch, @"Cloak\.HasProcSpell\s*\(\s*\w+\s*\)"),
                "the Block branch's cloak roll must be gated on Cloak.HasProcSpell.");

            Assert.IsFalse(parryBranch.Contains("Cloak."),
                "the Parry branch must not roll a cloak spell proc - only a Shield Block does.");
        }

        /// <summary>
        /// The four pre-existing roll sites roll on the damage going INTO the pre-write class-ability
        /// mitigation (cloakProcPercent), not on the post-mitigation percent, so Ward, Barrier and the Reflect
        /// immunity window do not erase the proc. Each file must declare the local, assign it from the
        /// unmitigated figure BEFORE the ApplyPreWriteDamageClassAbilities call, and pass it to TryProcSpell.
        /// </summary>
        [TestMethod]
        public void ExistingRollSites_RollOnThePreMitigationPercent_SourceScan()
        {
            // Player.TakeDamage: plain float local, assigned from `amount` immediately before the hook
            var combat = ReadServerFile("WorldObjects/Player_Combat.cs");
            var combatAssign = Regex.Match(combat, @"var cloakProcPercent\s*=\s*\(float\)\s*amount\s*/\s*Health\.MaxValue\s*;");
            var combatHook = combat.IndexOf("ApplyPreWriteDamageClassAbilities(", StringComparison.Ordinal);

            Assert.IsTrue(combatAssign.Success && combatHook > combatAssign.Index,
                "Player.TakeDamage must capture cloakProcPercent from the unmitigated amount BEFORE ApplyPreWriteDamageClassAbilities.");
            CollectionAssert.AreEqual(new[] { "this, source, equippedCloak, cloakProcPercent" }, TryProcSpellCalls("WorldObjects/Player_Combat.cs"),
                "Player.TakeDamage must roll the cloak spell proc on cloakProcPercent, not on the post-mitigation percent.");

            // SpellProjectile: nullable, set only for a player target inside the pre-write block
            var sp = ReadServerFile("WorldObjects/SpellProjectile.cs");
            var spAssign = Regex.Match(sp, @"cloakProcPercent\s*=\s*damage\s*/\s*target\.Health\.MaxValue\s*;");
            var spHook = sp.IndexOf("targetPlayer.ApplyPreWriteDamageClassAbilities(", StringComparison.Ordinal);

            Assert.IsTrue(spAssign.Success && spHook > spAssign.Index,
                "SpellProjectile.DamageTarget must capture cloakProcPercent from the unmitigated damage BEFORE ApplyPreWriteDamageClassAbilities.");
            CollectionAssert.AreEqual(new[] { "target, ProjectileSource, equippedCloak, cloakProcPercent ?? percent" }, TryProcSpellCalls("WorldObjects/SpellProjectile.cs"),
                "SpellProjectile must roll on cloakProcPercent, falling back to percent only for a non-player target.");

            // WorldObject_Magic: Harm captures before the mitigation call; Drain Health likewise
            var magic = ReadServerFile("WorldObjects/WorldObject_Magic.cs");

            var harmAssign = Regex.Match(magic, @"cloakProcPercent\s*=\s*\(float\)-tryBoost\s*/\s*mitigationTarget\.Health\.MaxValue\s*;");
            var harmHook = magic.IndexOf("mitigationTarget.ApplyPreWriteDamageClassAbilities(this, DamageType.Health, (uint)-tryBoost", StringComparison.Ordinal);

            Assert.IsTrue(harmAssign.Success && harmHook > harmAssign.Index,
                "Harm must capture cloakProcPercent from -tryBoost BEFORE ApplyPreWriteDamageClassAbilities.");

            var drainAssign = Regex.Match(magic, @"cloakProcPercent\s*=\s*\(float\)srcVitalChange\s*/\s*mitigationTarget\.Health\.MaxValue\s*;");
            var drainHook = magic.IndexOf("mitigationTarget.ApplyPreWriteDamageClassAbilities(this, DamageType.Health, srcVitalChange", StringComparison.Ordinal);

            Assert.IsTrue(drainAssign.Success && drainHook > drainAssign.Index,
                "Drain Health must capture cloakProcPercent from srcVitalChange BEFORE ApplyPreWriteDamageClassAbilities.");

            Assert.AreEqual(2, Regex.Matches(magic, @"var pct = cloakProcPercent \?\? ").Count,
                "both WorldObject_Magic roll sites (Harm, Drain Health) must roll on cloakProcPercent, falling back to the applied amount.");

            // a fully absorbed Harm leaves boost at 0, so its roll gate must admit the mitigated case
            Assert.IsTrue(Regex.IsMatch(magic, @"\(boost < 0 \|\| cloakProcPercent\.HasValue\)"),
                "the Harm cloak roll must still fire when Ward or Barrier absorbed the hit to 0.");
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
