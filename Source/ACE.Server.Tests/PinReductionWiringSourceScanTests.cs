using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan pins for Pinning Shot's damage-reduction rider: its four READ SITES and its ONE WRITE SITE. The read sites must stay
    /// identical in the rules PinReductionMath documents: the same gate (PinReductionMath.AppliesTo), the same
    /// accessor on the attacker (GetPinOutgoingDamageMod), applied once per hit, before the defender's
    /// pre-write mitigation and before any cloakProcPercent capture. A fifth, ungated or reordered copy would
    /// compile and pass every behavioural test, so the text is the only thing that can catch it.
    /// </summary>
    [TestClass]
    public class PinReductionWiringSourceScanTests
    {
        private const string Accessor = "GetPinOutgoingDamageMod()";
        private const string Gate = "PinReductionMath.AppliesTo(";

        private static string Read(string serverRelativePath) =>
            PooledLootSourceText.Read("Source/ACE.Server/" + serverRelativePath);

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;

            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;

            return count;
        }

        private static void AssertBefore(string body, string earlier, string later, string because)
        {
            var a = body.IndexOf(earlier, StringComparison.Ordinal);
            var b = body.IndexOf(later, StringComparison.Ordinal);

            Assert.IsTrue(a >= 0, $"missing: {earlier}");
            Assert.IsTrue(b >= 0, $"missing: {later}");
            Assert.IsTrue(a < b, because);
        }

        /// <summary>
        /// The gate must be the CONDITION of an `if` whose body holds the accessor read, written out in full. A
        /// bare substring pin is not enough: `_ = PinReductionMath.AppliesTo(...)` followed by an unconditional
        /// multiply contains every token a loose scan looks for, and so does a condition that lost a clause whose
        /// text also appears elsewhere in the method.
        /// </summary>
        private static void AssertOneGatedRead(string body, string site, string fullCondition)
        {
            Assert.AreEqual(1, CountOf(body, Gate), $"{site}: exactly one gate");
            Assert.AreEqual(1, CountOf(body, Accessor), $"{site}: exactly one read of the accessor, once per hit");

            var ifHead = "if (" + fullCondition + ")";
            Assert.AreEqual(1, CountOf(body, ifHead), $"{site}: the gate must be exactly `{ifHead}`");

            // the accessor is the first thing the if's body does: nothing (no statement end, no block end) between
            // the condition and the read
            var afterCondition = body.IndexOf(ifHead, StringComparison.Ordinal) + ifHead.Length;
            var read = body.IndexOf(Accessor, StringComparison.Ordinal);

            Assert.IsTrue(read > afterCondition, $"{site}: the read sits after the gate");

            var between = body.Substring(afterCondition, read - afterCondition);
            Assert.IsFalse(between.Contains(';') || between.Contains('}'),
                $"{site}: the read must be the first statement under the gate, found `{between.Trim()}` between them");
        }

        // ---- site 1: physical ------------------------------------------------------------------------

        [TestMethod]
        public void Site1_Physical_SitsBetweenDoCalculateDamageAndFinishOutgoingHit()
        {
            var src = Read("Entity/DamageEvent.cs");
            var body = PooledLootSourceText.MethodBody(src, "public static DamageEvent CalculateDamage(");

            AssertOneGatedRead(body, "DamageEvent.CalculateDamage", "PinReductionMath.AppliesTo(attacker, defender)");
            AssertBefore(body, "damageEvent.DoCalculateDamage(", Gate, "site 1 runs after the whole defender half");
            AssertBefore(body, Gate, "FinishOutgoingHit(attacker, defender, damageEvent);", "site 1 runs before the outgoing-hit riders and logging");
            AssertBefore(body, Gate, "damageEvent.DamageMitigated =", "DamageMitigated is rewritten with Damage");
        }

        [TestMethod]
        public void Site1_LeavesTheDefenderHalfAndTheReflectBounceUntouched()
        {
            var src = Read("Entity/DamageEvent.cs");

            var half = PooledLootSourceText.MethodBody(src, "private float ResolveDefenderHalf(");
            Assert.IsFalse(half.Contains("PinReduction", StringComparison.Ordinal),
                "inside ResolveDefenderHalf the Reflect bounce (which calls it directly on the firer) would be reduced too");

            var reflect = PooledLootSourceText.MethodBody(src, "public static DamageEvent CalculateReflectedMissile(");
            Assert.IsFalse(reflect.Contains("PinReduction", StringComparison.Ordinal), "a Reflect bounce stays at full strength");

            Assert.AreEqual(1, CountOf(src, Accessor), "DamageEvent.cs reads the accessor in exactly one place");
        }

        // ---- site 2: spell projectile ----------------------------------------------------------------

        [TestMethod]
        public void Site2_SpellProjectile_SitsInTheHealthBranchBesideSoulTether()
        {
            var src = Read("WorldObjects/SpellProjectile.cs");
            var body = PooledLootSourceText.MethodBody(src, "public void DamageTarget(Creature target, float damage");

            AssertOneGatedRead(body, "SpellProjectile.DamageTarget", "PinReductionMath.AppliesTo(sourceCreature, target)");

            AssertBefore(body, "Spell.Category == SpellCategory.ManaLowering", Gate, "site 2 is in the health (else) branch, after the stamina and mana branches");
            AssertBefore(body, "GetSoulTetherDamageReductionMod()", Gate, "site 2 sits right after the Soul Tether pet line");
            AssertBefore(body, Gate, "PvpRules.ApplyDamageCap(", "site 2 runs before the PvP damage cap");
            AssertBefore(body, Gate, "Cloak.RollProc(", "site 2 runs before the cloak damage proc");
            AssertBefore(body, Gate, "ApplyPreWriteDamageClassAbilities(", "site 2 runs before the pre-write mitigations");
            AssertBefore(body, Gate, "HoldPlayerDamageMult(", "site 2 runs before the World Events multiplier");
        }

        // ---- site 3: Harm ----------------------------------------------------------------------------

        [TestMethod]
        public void Site3_Harm_RunsBeforeTheCloakProcAndItsPercentCapture()
        {
            var src = Read("WorldObjects/WorldObject_Magic.cs");
            var body = PooledLootSourceText.MethodBody(src, "private void HandleCastSpell_Boost(");

            AssertOneGatedRead(body, "HandleCastSpell_Boost",
                "spell.VitalDamageType == DamageType.Health && tryBoost < 0 && PinReductionMath.AppliesTo(creature, targetCreature)");

            AssertBefore(body, "PvpChokePoint.OT3", Gate, "site 3 runs after the PvP shaping of the final roll");
            AssertBefore(body, Gate, "var equippedCloak = targetCreature?.EquippedCloak;", "site 3 runs before the cloak damage proc");
            AssertBefore(body, Gate, "float? cloakProcPercent = null;", "site 3 runs before the cloakProcPercent capture");
            AssertBefore(body, Gate, "ApplyPreWriteDamageClassAbilities(", "site 3 runs before the pre-write mitigations");
        }

        // ---- site 4: Drain Health --------------------------------------------------------------------

        [TestMethod]
        public void Site4_Drain_RunsBeforeTheCloakProcAndItsPercentCapture()
        {
            var src = Read("WorldObjects/WorldObject_Magic.cs");
            var body = PooledLootSourceText.MethodBody(src, "private void HandleCastSpell_Transfer(");

            AssertOneGatedRead(body, "HandleCastSpell_Transfer",
                "isDrain && spell.Source == PropertyAttribute2nd.Health && srcVitalChange > 0 && PinReductionMath.AppliesTo(caster, transferSource)");

            AssertBefore(body, "PvpChokePoint.C4", Gate, "site 4 runs after the PvP shaping of the loss");
            AssertBefore(body, Gate, "var equippedCloak = targetCreature?.EquippedCloak;", "site 4 runs before the cloak damage proc");
            AssertBefore(body, Gate, "float? cloakProcPercent = null;", "site 4 runs before the cloakProcPercent capture");
            AssertBefore(body, Gate, "ApplyPreWriteDamageClassAbilities(", "site 4 runs before the pre-write mitigations");
        }

        // ---- the whole tree --------------------------------------------------------------------------

        [TestMethod]
        public void ExactlyFourReadSites_ExistInTheServerTree()
        {
            var root = FindServerRoot();
            var hits = new System.Collections.Generic.List<string>();

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    continue;

                var name = Path.GetFileName(file);

                // the definition itself, and the pure-math class that documents the sites
                if (name == "Creature_ClassAbilityDebuffs.cs" || name == "PinReductionMath.cs")
                    continue;

                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) ||
                        line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                        continue;

                    if (line.Contains(Accessor, StringComparison.Ordinal))
                        hits.Add(name);
                }
            }

            CollectionAssert.AreEquivalent(
                new[] { "DamageEvent.cs", "SpellProjectile.cs", "WorldObject_Magic.cs", "WorldObject_Magic.cs" },
                hits,
                "The pin reduction is read at exactly four sites; a fifth must be added to this test and to PinReductionMath's doc deliberately. Found: " + string.Join(", ", hits));
        }

        private static string FindServerRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "ClassAbilities", "PinReductionMath.cs");

                if (File.Exists(candidate))
                    return Path.Combine(dir.FullName, "Source", "ACE.Server");
            }

            Assert.Fail("Could not find Source/ACE.Server by walking up from " + AppContext.BaseDirectory);
            return null;
        }

        // ---- write sites -----------------------------------------------------------------------------

        [TestMethod]
        public void PinWrite_IsTheOnlyWriter_CalledOnceFromTryPin_AfterThePinAndTheSlowDecision()
        {
            var src = Read("ClassAbilities/Abilities/PinningShotAbility.cs");
            var body = PooledLootSourceText.MethodBody(src, "private static void TryPin(");

            Assert.AreEqual(1, CountOf(body, "TryApplyPinDamageReduction("), "one site covers the weapon and the spell pin");
            AssertBefore(body, "TryApplyClassAbilityPin(", "TryApplyPinDamageReduction(", "the write runs only after the pin is confirmed applied");
            AssertBefore(body, "var slowApplied = target.TryApplyClassAbilityMoveSlow(slowSeconds, slowFactor);", "TryApplyPinDamageReduction(",
                "the window is decided AFTER the slow was attempted: it follows what actually happened to the creature");

            // the window: the slow's own end when the slow landed, the pin's end when it did not
            StringAssert.Contains(body, "var reductionEnd = PinReductionEnd(target, slowApplied, slowSeconds);");

            var endBody = src.Substring(src.IndexOf("internal static double PinReductionEnd(", StringComparison.Ordinal));
            StringAssert.Contains(endBody, "slowApplied ? target.PinSlowWindowEnd(slowSeconds) : target.ClassAbilityPinnedUntil;");
            StringAssert.Contains(body, "target.TryApplyPinDamageReduction(PinDamageReductionFor(attacker), reductionEnd);");

            // no other writer anywhere in the server tree
            var hits = 0;

            foreach (var file in Directory.EnumerateFiles(FindServerRoot(), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    continue;

                if (Path.GetFileName(file) == "Creature_ClassAbilityDebuffs.cs")
                    continue;

                foreach (var line in File.ReadAllLines(file))
                {
                    var t = line.TrimStart();

                    if (!t.StartsWith("//", StringComparison.Ordinal) && line.Contains(".TryApplyPinDamageReduction(", StringComparison.Ordinal))
                        hits++;
                }
            }

            Assert.AreEqual(1, hits, "the pin is the only writer of the reduction");
        }

        [TestMethod]
        public void SlowWindowEnd_HasASingleSource_SharedByTheSlowAndTheReduction()
        {
            var pin = Read("WorldObjects/Creature_ClassAbilityPin.cs");

            var codeLines = pin.Split((char)10).Count(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Contains("ClassAbilityPinnedUntil + slowSeconds", StringComparison.Ordinal));
            Assert.AreEqual(1, codeLines, "the sum is made in exactly one place");
            StringAssert.Contains(PooledLootSourceText.MethodBody(pin, "internal bool ApplyPinSlowState("), "PinSlowWindowEnd(slowSeconds)");
        }
    }
}
