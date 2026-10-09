using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the Spellweave (Spellsword T1) wiring that makes class ability procs count as casts.
    ///
    /// THE BUG THIS GUARDS (fixed 2026-09-14). Spellweave's cast side, Player.OnSpellweaveCast, was called
    /// only from WorldObject_Magic.HandleCastSpell. The Spellblade, Runeblade and Spellstorm procs cast
    /// through Player.CastClassAbilityProc -> CreateSpellProjectiles and never reach HandleCastSpell, so a
    /// proc neither consumed the spell charge a weapon hit armed nor armed the weapon charge - despite the
    /// ability's own Description promising "Casting any spell, including procs". Two further defects rode
    /// along: the proc handlers were registered AHEAD of Spellweave in the outgoing-damage bucket, so wiring
    /// the cast side alone would have let a proc arm the weapon charge that its own triggering hit then
    /// spent; and the damage sites read the player's stamp at IMPACT, so a projectile in flight took the
    /// bonus of whatever the caster cast most recently.
    ///
    /// WHY MOSTLY SOURCE GUARDS. ACE.Server.Tests cannot construct a live Player (both constructors reach
    /// DatabaseManager.Authentication), and OnSpellweaveCast resolves its rank through PropertyManager, which
    /// throws on a cache miss under this host. The dispatch ORDER, though, is a real behavioural assertion:
    /// ClassAbilityRegistry is a pure static and its bucket is the exact array Player dispatches from. The
    /// other three tests read the source, bounded by the next member declaration after comment stripping,
    /// and every one of them compiles against the pre-fix tree so a control run fails on its own assertion
    /// rather than on a missing symbol.
    /// </summary>
    [TestClass]
    public class SpellweaveProcWiringTests
    {
        private static readonly Regex NextMemberDeclaration = new Regex(@"\n        (?:private|public|internal|protected)\s");

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string ServerSource(params string[] relative)
        {
            var path = Path.Combine(new[] { RepoRoot(), "Source", "ACE.Server" }.Concat(relative).ToArray());

            Assert.IsTrue(File.Exists(path), $"missing source file {path}");

            return AccountVaultPurgeTests.StripComments(File.ReadAllText(path));
        }

        /// <summary>
        /// The text from <paramref name="signature"/> up to the next class-level member declaration. Bounded
        /// by the NEXT DECLARATION rather than by naming the member that follows, so the region never depends
        /// on code this guard does not test.
        /// </summary>
        private static string MemberBody(string strippedSource, string signature)
        {
            var start = strippedSource.IndexOf(signature, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0, $"signature not found: {signature}");

            var next = NextMemberDeclaration.Match(strippedSource, start + signature.Length);

            Assert.IsTrue(next.Success, $"no member declaration follows {signature}");

            return strippedSource.Substring(start, next.Index - start);
        }

        [TestMethod]
        public void OutgoingDamageDispatch_RunsSpellweaveBeforeEveryHandlerThatCastsAProc()
        {
            var abilitiesDir = Path.Combine(RepoRoot(), "Source", "ACE.Server", "ClassAbilities", "Abilities");

            // Derived from the source rather than listed, so a future ability that casts from this hook is
            // held to the same ordering without anyone remembering to add it here.
            var castingTypeNames = Directory.GetFiles(abilitiesDir, "*.cs")
                .Select(f => AccountVaultPurgeTests.StripComments(File.ReadAllText(f)))
                .Where(code => code.Contains("CastClassAbilityProc("))
                .Select(code => Regex.Match(code, @"public\s+class\s+(\w+)").Groups[1].Value)
                .ToList();

            // Non-vacuity: the scan must at least find the three war procs, or it is proving nothing.
            foreach (var expected in new[] { nameof(SpellbladeAbility), nameof(RunebladeAbility), nameof(SpellstormAbility) })
                Assert.IsTrue(castingTypeNames.Contains(expected), $"source scan did not find {expected} calling CastClassAbilityProc; found [{string.Join(", ", castingTypeNames)}]");

            var bucket = ClassAbilityRegistry.OutgoingDamageAbilities.Select(h => h.GetType().Name).ToList();

            var weaveIndex = bucket.IndexOf(nameof(SpellweaveAbility));

            Assert.IsTrue(weaveIndex >= 0, "SpellweaveAbility is not in the outgoing-damage bucket");

            var problems = new List<string>();

            foreach (var name in castingTypeNames)
            {
                var index = bucket.IndexOf(name);

                if (index < 0)
                    problems.Add($"{name} calls CastClassAbilityProc but is not in the outgoing-damage bucket");
                else if (index < weaveIndex)
                    problems.Add($"{name} is dispatched at index {index}, BEFORE Spellweave at {weaveIndex}: its proc cast would arm the weapon charge and Spellweave would then spend it on the proc's own triggering hit");
            }

            Assert.IsTrue(problems.Count == 0, string.Join("\n", problems));
        }

        [TestMethod]
        public void CastClassAbilityProc_RunsSpellweaveCastSideBeforeTheCast()
        {
            var body = MemberBody(ServerSource("WorldObjects", "Player_ClassAbilityBuffs.cs"), "public void CastClassAbilityProc(Action cast)");

            var weave = body.IndexOf("OnSpellweaveCast();", StringComparison.Ordinal);

            Assert.IsTrue(weave >= 0, "CastClassAbilityProc must call OnSpellweaveCast(): the war procs never reach HandleCastSpell, so this is the only place a proc can feed Spellweave");

            var castCall = Regex.Match(body, @"\bcast\(\)\s*;");

            Assert.IsTrue(castCall.Success, "CastClassAbilityProc no longer invokes its cast delegate");

            Assert.IsTrue(weave < castCall.Index, "OnSpellweaveCast() must run BEFORE cast(): LaunchSpellProjectiles captures the stamp onto each projectile as it is built");
        }

        [TestMethod]
        public void HandleCastSpell_SkipsSpellweaveCastSideWhileAProcCastIsActive()
        {
            var body = MemberBody(ServerSource("WorldObjects", "WorldObject_Magic.cs"), "protected bool HandleCastSpell(Spell spell");

            Assert.AreEqual(1, Regex.Matches(body, @"OnSpellweaveCast\(\)").Count, "HandleCastSpell must call OnSpellweaveCast exactly once");

            var gated = new Regex(@"if\s*\(\s*this\s+is\s+Player\s+(\w+)\s*&&\s*!\s*\1\.ClassAbilityProcCastActive\s*\)\s*\1\.OnSpellweaveCast\(\)\s*;");

            Assert.IsTrue(gated.IsMatch(body), "HandleCastSpell's OnSpellweaveCast call must be skipped while Player.ClassAbilityProcCastActive is set, or a proc that routes through HandleCastSpell would fire the cast side twice");
        }

        [TestMethod]
        public void SpellProjectiles_CaptureTheSpellweaveStampAtLaunchAndReadTheCaptureAtImpact()
        {
            var launch = MemberBody(ServerSource("WorldObjects", "WorldObject_Magic.cs"), "public List<SpellProjectile> LaunchSpellProjectiles(");

            Assert.IsTrue(Regex.IsMatch(launch, @"sp\.SpellweaveDamageMod\s*=\s*\(this as Player\)\?\.GetSpellweaveSpellDamageMod\(\)"),
                "LaunchSpellProjectiles must capture the caster's Spellweave stamp onto each projectile as it is built");

            var projectile = ServerSource("WorldObjects", "SpellProjectile.cs");

            Assert.IsTrue(Regex.IsMatch(projectile, @"GetClassAbilitySpellDamageMod\(\s*Spell\s*,\s*SpellweaveDamageMod\s*\)"),
                "the war/void impact site must pass the projectile's captured SpellweaveDamageMod");

            Assert.IsTrue(Regex.IsMatch(projectile, @"ApplyLifeProjectileClassAbilityDamage\([^;]*SpellweaveDamageMod\s*\)\s*;"),
                "the life-projectile impact site must pass the projectile's captured SpellweaveDamageMod");

            // Spell AOE children bypass LaunchSpellProjectiles, so without an explicit copy they would silently
            // default SpellweaveDamageMod to 1.0 (dropping a bonus the parent cast paid for) AND default
            // LifeProjectileDamage to 0 (landing a life-projectile spell like Martyr's Hecatomb for 0 damage).
            var aoeChild = MemberBody(projectile, "public void SpawnClassAbilityAoeChild(");

            Assert.IsTrue(Regex.IsMatch(aoeChild, @"CopyPerCastStamps\(\s*sp\s*,\s*this\s*\)\s*;"),
                "SpawnClassAbilityAoeChild must call CopyPerCastStamps(sp, this) to copy the parent projectile's SpellweaveDamageMod, LifeProjectileDamage and LifeProjectileStamp onto the child");
        }

        /// <summary>
        /// Echo Cast and Cascade build their children through CreateSpellProjectiles, which stamps each one
        /// from the LIVE player field. That field belongs to whatever the player cast most recently, so unless
        /// the handler overwrites it with the parent's capture, a later cast's bonus - and, for Spell AOE and
        /// Cascade children, the entire LifeProjectileDamage basis - leaks or is lost. Both handlers must call
        /// SpellProjectile.CopyPerCastStamps to copy the PARENT projectile's stamps onto the children they just
        /// created; a call to that helper removed at either site is exactly what this guard must catch.
        /// </summary>
        [TestMethod]
        public void EchoCastAndCascadeChildren_InheritTheParentProjectilesSpellweaveStamp()
        {
            // Echo Cast copies through EchoCastAbility.StampEchoCopy(echo, projectile) since 2026-09-22, which
            // itself delegates to SpellProjectile.CopyPerCastStamps (pinned just below); Cascade calls the
            // helper directly. ClassAbilityHandlerTests.EchoCast_StampEchoCopy_InheritsTheParentsPerCastValues
            // and .CopyPerCastStamps_InheritsTheParentsPerCastValues pin the copy by behavior.
            var cases = new[]
            {
                (File: "EchoCastAbility.cs", Loop: "echoes", Pattern: @"StampEchoCopy\(\s*(\w+)\s*,\s*projectile\s*\)\s*;"),
                (File: "CascadeAbility.cs", Loop: "children", Pattern: @"SpellProjectile\.CopyPerCastStamps\(\s*(\w+)\s*,\s*projectile\s*\)\s*;"),
            };

            var stamp = MemberBody(ServerSource("ClassAbilities", "Abilities", "EchoCastAbility.cs"), "public static void StampEchoCopy(");
            Assert.IsTrue(Regex.IsMatch(stamp, @"CopyPerCastStamps\(\s*echo\s*,\s*parent\s*\)\s*;"),
                "StampEchoCopy must call SpellProjectile.CopyPerCastStamps(echo, parent) to copy the PARENT's captured stamps (SpellweaveDamageMod, LifeProjectileDamage, LifeProjectileStamp) onto the echo");

            foreach (var c in cases)
            {
                var body = MemberBody(ServerSource("ClassAbilities", "Abilities", c.File), "public void OnSpellHit(");

                var loop = Regex.Match(body, $@"foreach\s*\(\s*var\s+(\w+)\s+in\s+{c.Loop}\s*\)");

                Assert.IsTrue(loop.Success, $"{c.File}: OnSpellHit no longer iterates the spawned '{c.Loop}' list");

                var copy = Regex.Match(body.Substring(loop.Index), c.Pattern);

                Assert.IsTrue(copy.Success, $"{c.File}: every spawned child must take projectile.SpellweaveDamageMod (the parent's captured stamp), not keep the live-player stamp CreateSpellProjectiles gave it");

                Assert.AreEqual(loop.Groups[1].Value, copy.Groups[1].Value,
                    $"{c.File}: the stamp must be copied onto the loop variable '{loop.Groups[1].Value}', i.e. onto each spawned child");
            }

            var buffs = ServerSource("WorldObjects", "Player_ClassAbilityBuffs.cs");

            foreach (var signature in new[] { "public float GetClassAbilitySpellDamageMod(", "public float ApplyLifeProjectileClassAbilityDamage(" })
            {
                Assert.IsFalse(MemberBody(buffs, signature).Contains("GetSpellweaveSpellDamageMod("),
                    $"{signature} runs at IMPACT and must not read the live player stamp - a later cast may have re-stamped it");
            }
        }

        /// <summary>
        /// The Blood Mage per-cast stamp follows the same capture-at-launch shape as Spellweave: captured in
        /// LaunchSpellProjectiles, passed from the projectile at impact, copied from the parent onto an echo,
        /// and NEVER read live inside ApplyLifeProjectileClassAbilityDamage (PR #1271 review: a live read let
        /// an in-flight bolt or an echo take a later cast's Exsanguinate burst).
        /// </summary>
        [TestMethod]
        public void LifeProjectileStamp_CapturedAtLaunch_PassedAtImpact_CopiedToEchoes_NeverReadLive()
        {
            var launch = MemberBody(ServerSource("WorldObjects", "WorldObject_Magic.cs"), "public List<SpellProjectile> LaunchSpellProjectiles(");
            Assert.IsTrue(Regex.IsMatch(launch, @"sp\.LifeProjectileStamp\s*=\s*\(this as Player\)\?\.GetLifeProjectileCastStamp\(\)"),
                "LaunchSpellProjectiles must capture the caster's life-projectile stamp onto each projectile as it is built");

            var projectile = ServerSource("WorldObjects", "SpellProjectile.cs");
            Assert.IsTrue(Regex.IsMatch(projectile, @"ApplyLifeProjectileClassAbilityDamage\([^;]*castStamp:\s*LifeProjectileStamp\b"),
                "the life-projectile impact site must pass the projectile's captured LifeProjectileStamp");

            var stamp = MemberBody(ServerSource("ClassAbilities", "Abilities", "EchoCastAbility.cs"), "public static void StampEchoCopy(");
            Assert.IsTrue(Regex.IsMatch(stamp, @"CopyPerCastStamps\(\s*echo\s*,\s*parent\s*\)\s*;"),
                "StampEchoCopy must call SpellProjectile.CopyPerCastStamps(echo, parent) to copy the PARENT's captured LifeProjectileStamp onto the echo");

            // The AOE child and Cascade child sites must also inherit LifeProjectileStamp (and
            // LifeProjectileDamage) from the parent via the same helper - an unstamped LifeProjectileDamage
            // lands a life-projectile spell (Martyr's Hecatomb) for 0 damage on every splash/chain hit.
            var aoeChild = MemberBody(projectile, "public void SpawnClassAbilityAoeChild(");
            Assert.IsTrue(Regex.IsMatch(aoeChild, @"CopyPerCastStamps\(\s*sp\s*,\s*this\s*\)\s*;"),
                "SpawnClassAbilityAoeChild must call CopyPerCastStamps(sp, this) to copy the parent's captured LifeProjectileStamp/LifeProjectileDamage onto the child");

            var cascadeHit = MemberBody(ServerSource("ClassAbilities", "Abilities", "CascadeAbility.cs"), "public void OnSpellHit(");
            Assert.IsTrue(Regex.IsMatch(cascadeHit, @"SpellProjectile\.CopyPerCastStamps\(\s*child\s*,\s*projectile\s*\)\s*;"),
                "CascadeAbility.OnSpellHit must call SpellProjectile.CopyPerCastStamps(child, projectile) to copy the parent's captured LifeProjectileStamp/LifeProjectileDamage onto each cascade child");

            var body = MemberBody(ServerSource("WorldObjects", "Player_ClassAbilityBuffs.cs"), "public float ApplyLifeProjectileClassAbilityDamage(");
            foreach (var live in new[] { "lifeProjectileChargeMod", "lifeProjectileOutcome", "bloodPriceDamageMod", "GetBloodPriceDamageMod(" })
                Assert.IsFalse(body.Contains(live), $"ApplyLifeProjectileClassAbilityDamage runs at IMPACT and must not read '{live}' - a later cast may have overwritten it");
        }

        /// <summary>
        /// Control for the three source guards above: the doc comments in these files name every symbol they
        /// search for, so if the stripper ever stopped stripping, the guards could pass on comment text alone.
        /// </summary>
        [TestMethod]
        public void CommentStripper_RemovesTheSymbolsTheGuardsSearchFor()
        {
            var sample = "        // OnSpellweaveCast(); cast();\n        /// SpellweaveDamageMod = GetSpellweaveSpellDamageMod()\n        /* ClassAbilityProcCastActive */\n        int x;\n";

            var stripped = AccountVaultPurgeTests.StripComments(sample);

            foreach (var symbol in new[] { "OnSpellweaveCast", "cast()", "SpellweaveDamageMod", "GetSpellweaveSpellDamageMod", "ClassAbilityProcCastActive" })
                Assert.IsFalse(stripped.Contains(symbol), $"StripComments left [{symbol}] in comment-only text");

            Assert.IsTrue(stripped.Contains("int x;"), "StripComments removed code");
        }
    }
}
