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
        public void RollSpellProc_ReadsTheCloakedInPowerFloorAndPassesItToRollProc_SourceScan()
        {
            var cloak = ReadServerFile("Entity/Cloak.cs");

            var body = Regex.Match(cloak,
                @"public static bool RollSpellProc\s*\([^)]*\)\s*\{(?<body>.*?)\n        \}",
                RegexOptions.Singleline);

            Assert.IsTrue(body.Success, "could not locate the body of Cloak.RollSpellProc");

            var text = body.Groups["body"].Value;

            var read = Regex.Match(text, @"var\s+(?<var>[A-Za-z_]\w*)\s*=\s*CloakedInPowerFloor\s*\(\s*defender\s*\)");

            Assert.IsTrue(read.Success,
                "Cloak.RollSpellProc no longer reads the Cloaked in Power floor from its defender into a local. " +
                "Every cloak spell-proc site relies on it doing so - none of them passes a floor of its own.");

            // THE THIRD ARGUMENT MUST BE THAT LOCAL BY NAME, not merely "some third argument". An earlier draft
            // of this scan accepted \w+ there and a discrimination run passed a literal 0f straight through it -
            // the floor was read and then thrown away, and the test stayed green. Binding the name is the fix.
            Assert.IsTrue(Regex.IsMatch(text, @"RollProc\s*\(\s*cloak\s*,\s*damage_percent\s*,\s*" + Regex.Escape(read.Groups["var"].Value) + @"\s*\)"),
                $"Cloak.RollSpellProc reads the floor into '{read.Groups["var"].Value}' but does not pass THAT value " +
                "to RollProc as its third argument, so the floor it read is being discarded.");

            // the Critical Thorns window (Vanguard pass 2026-11-06) opens on the roll's success, inside the same
            // method, after RollProc and before the return - so every site that rolls through here opens it
            var rollAt = text.IndexOf("RollProc(", StringComparison.Ordinal);
            var openAt = text.IndexOf("OpenCriticalThornsWindow(", StringComparison.Ordinal);
            var retAt = text.LastIndexOf("return true;", StringComparison.Ordinal);

            Assert.IsTrue(rollAt >= 0 && openAt > rollAt && retAt > openAt,
                "Cloak.RollSpellProc must open the Critical Thorns window after a SUCCESSFUL RollProc and before it returns true.");

            // the damage-reduction (CloakWeaveProc = 2) roll never opens it: it calls RollProc directly, and no
            // file other than Cloak.cs may call OpenCriticalThornsWindow
            var offenders = Directory
                .GetFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(StripLineComments(File.ReadAllText(f)), @"\.OpenCriticalThornsWindow\s*\("))
                .Select(f => Path.GetFileName(f))
                .OrderBy(f => f)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "Cloak.cs" }, offenders,
                "only Cloak.RollSpellProc may open the Critical Thorns window; the damage-reduction cloak roll must not.");
        }

        /// <summary>
        /// TryProcSpell is the two phases back to back, and the Taunt hook (OnClassAbilityItemProc) plus the cast
        /// live in the second phase, CompleteSpellProc.
        /// </summary>
        [TestMethod]
        public void TryProcSpell_IsRollThenComplete_AndCompleteDispatchesTauntThenCasts_SourceScan()
        {
            var cloak = ReadServerFile("Entity/Cloak.cs");

            var tryBody = Regex.Match(cloak,
                @"public static bool TryProcSpell\s*\([^)]*\)\s*\{(?<body>.*?)\n        \}",
                RegexOptions.Singleline);

            Assert.IsTrue(tryBody.Success, "could not locate the body of Cloak.TryProcSpell");

            var t = tryBody.Groups["body"].Value;
            var rollAt = t.IndexOf("RollSpellProc(defender, cloak, damage_percent)", StringComparison.Ordinal);
            var completeAt = t.IndexOf("CompleteSpellProc(defender, attacker, cloak)", StringComparison.Ordinal);

            Assert.IsTrue(rollAt >= 0 && completeAt > rollAt, "TryProcSpell must roll first and then complete the proc.");

            var completeBody = Regex.Match(cloak,
                @"public static bool CompleteSpellProc\s*\([^)]*\)\s*\{(?<body>.*?)\n        \}",
                RegexOptions.Singleline);

            Assert.IsTrue(completeBody.Success, "could not locate the body of Cloak.CompleteSpellProc");

            var c = completeBody.Groups["body"].Value;
            var taunt = c.IndexOf("OnClassAbilityItemProc(cloak)", StringComparison.Ordinal);
            var cast = c.IndexOf("HandleProcSpell(defender, attacker, cloak)", StringComparison.Ordinal);

            Assert.IsTrue(taunt >= 0 && cast > taunt,
                "Cloak.CompleteSpellProc must dispatch the IItemProcAbility hook (Taunt) before it casts.");
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
                .Where(f => Regex.IsMatch(StripLineComments(File.ReadAllText(f)), @"Cloak\.(?:TryProcSpell|RollSpellProc)\s*\("))
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
                @"Cloak\.(?:TryProcSpell|RollSpellProc)\s*\(").Count);

            // phase one is RollSpellProc at every site (TryProcSpell is the back-to-back form, used by none of them)
            Assert.AreEqual(6, total,
                "expected exactly 6 Cloak.RollSpellProc (or TryProcSpell) call sites (Player_Combat 1, SpellProjectile 1, " +
                "WorldObject_Magic 2, Reflect bounce 1, Shield Block 1). A change here needs the same " +
                "confirmation as above.");
        }

        /// <summary>The Cloak.RollSpellProc call statements in a file, each as its full argument text.</summary>
        private static string[] TryProcSpellCalls(string relative)
        {
            return Regex.Matches(ReadServerFile(relative), @"Cloak\.RollSpellProc\s*\((?<args>[^;]*)\)\s*;")
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
            var call = b.IndexOf("Cloak.RollSpellProc(", StringComparison.Ordinal);
            var ret = b.LastIndexOf("return true;", StringComparison.Ordinal);

            Assert.IsTrue(call >= 0, "TryReflectProjectile no longer rolls the cloak spell proc on a successful bounce.");
            Assert.IsTrue(roll >= 0 && roll < call && call < ret,
                "the cloak roll must come after the bounce chance roll and before the success return.");

            Assert.IsTrue(Regex.IsMatch(b, @"Cloak\.HasProcSpell\s*\(\s*\w+\s*\)"),
                "the bounce roll must be gated on Cloak.HasProcSpell like every other site.");

            Assert.IsTrue(Regex.IsMatch(b, @"Cloak\.RollSpellProc\s*\(\s*this\s*,\s*\w+\s*,\s*damage\s*/\s*Health\.MaxValue\s*\)"),
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

            Assert.IsTrue(Regex.IsMatch(blockBranch, @"Cloak\.RollSpellProc\s*\(\s*this\s*,\s*\w+\s*,\s*0f\s*\)"),
                "the Block branch must call Cloak.RollSpellProc(this, cloak, 0f) - floor only, by owner ruling.");

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
            CollectionAssert.AreEqual(new[] { "this, equippedCloak, cloakProcPercent" }, TryProcSpellCalls("WorldObjects/Player_Combat.cs"),
                "Player.TakeDamage must roll the cloak spell proc on cloakProcPercent, not on the post-mitigation percent.");

            // SpellProjectile: nullable, set only for a player target inside the pre-write block
            var sp = ReadServerFile("WorldObjects/SpellProjectile.cs");
            var spAssign = Regex.Match(sp, @"cloakProcPercent\s*=\s*damage\s*/\s*target\.Health\.MaxValue\s*;");
            var spHook = sp.IndexOf("targetPlayer.ApplyPreWriteDamageClassAbilities(", StringComparison.Ordinal);

            Assert.IsTrue(spAssign.Success && spHook > spAssign.Index,
                "SpellProjectile.DamageTarget must capture cloakProcPercent from the unmitigated damage BEFORE ApplyPreWriteDamageClassAbilities.");
            CollectionAssert.AreEqual(new[] { "target, equippedCloak, cloakProcPercent ?? percent" }, TryProcSpellCalls("WorldObjects/SpellProjectile.cs"),
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

            CollectionAssert.AreEqual(new[]
                {
                    "targetCreature, equippedCloak, cloakProcPercent ?? (float)-boost / targetCreature.Health.MaxValue",
                    "targetCreature, equippedCloak, cloakProcPercent ?? (float)srcVitalChange / targetCreature.Health.MaxValue",
                },
                TryProcSpellCalls("WorldObjects/WorldObject_Magic.cs"),
                "both WorldObject_Magic roll sites (Harm, Drain Health) must roll on cloakProcPercent, falling back to the applied amount.");

            // a fully absorbed Harm leaves boost at 0, so its roll gate must admit the mitigated case
            Assert.IsTrue(Regex.IsMatch(magic, @"\(boost < 0 \|\| cloakProcPercent\.HasValue\)"),
                "the Harm cloak roll must still fire when Ward or Barrier absorbed the hit to 0.");
        }

        /// <summary>
        /// Cloak.CompleteSpellProc is the cast half of a split proc, so outside Cloak.cs it may be called only
        /// by files that ALSO roll (Cloak.RollSpellProc), and each such file must complete exactly as many procs
        /// as it rolls. A completion with no roll would cast a proc spell without the chance roll, the floor or
        /// the Critical Thorns window; a roll with no completion would burn the cooldown and never cast.
        /// </summary>
        [TestMethod]
        public void CompleteSpellProc_IsCalledOnlyByFilesThatRoll_AndOncePerRoll_SourceScan()
        {
            var perFile = Directory
                .GetFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "Cloak.cs", StringComparison.OrdinalIgnoreCase))
                .Select(f =>
                {
                    var text = StripLineComments(File.ReadAllText(f));
                    return (File: Path.GetRelativePath(ServerRoot(), f).Replace('\\', '/'),
                            Roll: Regex.Matches(text, @"Cloak\.RollSpellProc\s*\(").Count,
                            Complete: Regex.Matches(text, @"Cloak\.CompleteSpellProc\s*\(").Count);
                })
                .Where(t => t.Roll > 0 || t.Complete > 0)
                .OrderBy(t => t.File, StringComparer.Ordinal)
                .ToArray();

            var unbalanced = perFile.Where(t => t.Roll != t.Complete).Select(t => $"{t.File}: {t.Roll} roll(s), {t.Complete} complete(s)").ToArray();

            Assert.AreEqual(0, unbalanced.Length,
                "every file must call Cloak.CompleteSpellProc exactly as often as Cloak.RollSpellProc:\n" + string.Join("\n", unbalanced));

            CollectionAssert.AreEqual(
                new[]
                {
                    "WorldObjects/Player_ClassAbilityCombat.cs=1",
                    "WorldObjects/Player_ClassAbilityReflectAndPin.cs=1",
                    "WorldObjects/Player_Combat.cs=1",
                    "WorldObjects/SpellProjectile.cs=1",
                    "WorldObjects/WorldObject_Magic.cs=2",
                },
                perFile.Select(t => $"{t.File}={t.Complete}").ToArray(),
                "the files (and counts) that split a cloak proc changed; a new site is not automatically wrong, but it must roll and complete in pairs and be added here.");
        }

        // ---------------------------------------------------------------- ORDERING: the roll precedes the Thorns reflect

        /// <summary>The text of one method, signature to matching close brace, comments already stripped.</summary>
        private static string MethodText(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"signature not found - has it changed? {signature}");

            var open = source.IndexOf('{', start + signature.Length);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        private static void AssertRollBeforeThorns(string body, string rollCall, string thornsCall, string site)
        {
            var roll = body.IndexOf(rollCall, StringComparison.Ordinal);
            var thorns = body.IndexOf(thornsCall, StringComparison.Ordinal);

            Assert.IsTrue(roll >= 0, $"{site}: the cloak spell proc roll [{rollCall}] is missing");
            Assert.IsTrue(thorns >= 0, $"{site}: the Thorns call [{thornsCall}] is missing");
            Assert.IsTrue(roll < thorns,
                $"{site}: the cloak spell proc must be ROLLED before Thorns reflects, so the reflect of the triggering " +
                "hit sees Cloaked in Power's Critical Thorns window. Do not move the roll back behind the reflect.");
        }

        /// <summary>
        /// Cloaked in Power's Critical Thorns window is opened by the proc roll, and the reflect of the very hit that
        /// triggered the proc must see it open. So at every site where Thorns reflects, the roll comes first. The
        /// four sites that used to cast AFTER Thorns (the player's own TakeDamage, a spell projectile, Harm and
        /// Drain Health) now split the proc: RollSpellProc ahead of Thorns, CompleteSpellProc where the cast always
        /// was. The block and the Reflect bounce complete AFTER their reflect too, so the proc spell cannot kill the target first; nothing calls TryProcSpell
        /// first.
        /// </summary>
        [TestMethod]
        public void EverySite_RollsTheCloakSpellProcBeforeItsThornsReflect_SourceScan()
        {
            // Player.TakeDamage: the physical hook AND the rank-0 equipment-mod path both sit after the roll
            var combat = ReadServerFile("WorldObjects/Player_Combat.cs");
            var takeDamage = MethodText(combat, "public int TakeDamage(WorldObject source, DamageType damageType, float _amount, BodyPart bodyPart");
            AssertRollBeforeThorns(takeDamage, "Cloak.RollSpellProc(", "ApplyIncomingDamageClassAbilities(", "Player.TakeDamage (physical hook)");
            AssertRollBeforeThorns(takeDamage, "Cloak.RollSpellProc(", "ApplyEquipmentModIncomingDamage(", "Player.TakeDamage (equipment-mod path)");
            AssertRollBeforeThorns(takeDamage, "ApplyEquipmentModIncomingDamage(", "Cloak.CompleteSpellProc(", "Player.TakeDamage (cast stays after the reflect and the death check)");
            Assert.IsTrue(takeDamage.IndexOf("Health.Current <= 0", StringComparison.Ordinal) < takeDamage.IndexOf("Cloak.CompleteSpellProc(", StringComparison.Ordinal),
                "Player.TakeDamage: the cast must stay AFTER the death check, so a killing blow never casts the proc spell");

            // SpellProjectile.DamageTarget: the direct magic Thorns site
            var sp = ReadServerFile("WorldObjects/SpellProjectile.cs");
            var damageTarget = MethodText(sp, "public void DamageTarget(Creature target, float damage, bool critical, bool critDefended, bool overpower)");
            AssertRollBeforeThorns(damageTarget, "Cloak.RollSpellProc(", "ApplyThornsOnMagicHit(", "SpellProjectile.DamageTarget");
            AssertRollBeforeThorns(damageTarget, "ApplyThornsOnMagicHit(", "Cloak.CompleteSpellProc(", "SpellProjectile.DamageTarget (cast stays in the IsAlive branch)");

            // WorldObject_Magic: Harm and Drain Health
            var magic = ReadServerFile("WorldObjects/WorldObject_Magic.cs");
            var boost = MethodText(magic, "private void HandleCastSpell_Boost(Spell spell, Creature targetCreature, bool fromProc = false)");
            AssertRollBeforeThorns(boost, "Cloak.RollSpellProc(", "ApplyThornsOnMagicHit(", "HandleCastSpell_Boost (Harm)");
            AssertRollBeforeThorns(boost, "ApplyThornsOnMagicHit(", "Cloak.CompleteSpellProc(", "HandleCastSpell_Boost (cast stays deferred)");

            var transfer = MethodText(magic, "private void HandleCastSpell_Transfer(Spell spell, Creature targetCreature, bool isSecondaryStrike = false)");
            AssertRollBeforeThorns(transfer, "Cloak.RollSpellProc(", "ApplyThornsOnMagicHit(", "HandleCastSpell_Transfer (Drain Health)");
            AssertRollBeforeThorns(transfer, "ApplyThornsOnMagicHit(", "Cloak.CompleteSpellProc(", "HandleCastSpell_Transfer (cast stays deferred)");

            // the Reflect bounce and the Shield Block split the proc around their Thorns call: Roll < Thorns < Complete,
            // so the proc spell can neither land ahead of the reflect nor kill the target before it
            var bounce = MethodText(ReadServerFile("WorldObjects/Player_ClassAbilityReflectAndPin.cs"), "public bool TryReflectProjectile(");
            AssertRollBeforeThorns(bounce, "Cloak.RollSpellProc(", "ApplyThornsReflect(", "Player.TryReflectProjectile");
            AssertRollBeforeThorns(bounce, "ApplyThornsReflect(", "Cloak.CompleteSpellProc(", "Player.TryReflectProjectile (complete after the reflect)");

            var avoided = MethodText(ReadServerFile("WorldObjects/Player_ClassAbilityCombat.cs"), "public void OnClassAbilityAttackAvoided(");
            AssertRollBeforeThorns(avoided, "Cloak.RollSpellProc(", "ApplyThornsReflect(", "Player.OnClassAbilityAttackAvoided (Shield Block)");
            AssertRollBeforeThorns(avoided, "ApplyThornsReflect(", "Cloak.CompleteSpellProc(", "Player.OnClassAbilityAttackAvoided (complete after the reflect)");
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
