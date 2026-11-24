using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-text pins for the Tier A weapon-mod suppression WIRING - the four production read sites that
    /// WeaponModSuppressionTests cannot drive, because each one needs a live Player (which this project cannot
    /// construct). The pure helpers are covered there; what is pinned here is that each site calls them with the
    /// RIGHT native. The three rating lines in Creature_Rating.cs are near-identical, so swapping two
    /// PropertyInts would still compile and still pass every behavioral test.
    ///
    /// COMMENTS CANNOT SATISFY A PIN. Each file is stripped of // and /* */ comments (string-literal aware)
    /// BEFORE the method body is located and searched, and each pin is searched only inside its own method's
    /// brace-matched body. <see cref="StripComments_ACommentedPinDoesNotSatisfyTheSearch"/> proves the
    /// stripper does what that sentence claims. Whitespace is removed from both sides so reformatting does not
    /// break a pin, but every token must still be present in order.
    /// </summary>
    [TestClass]
    public class WeaponModSuppressionWiringTests
    {
        private const string CreatureRating = "Source/ACE.Server/WorldObjects/Creature_Rating.cs";
        private const string WorldObjectWeapon = "Source/ACE.Server/WorldObjects/WorldObject_Weapon.cs";

        private static readonly string[] RatingNatives = { "GearDamage", "GearCrit", "GearCritDamage" };

        // ================= the rating sites =================

        /// <summary>Creature.GetDamageRating subtracts the GearDamage (Bloodthirst) offset, and no other.</summary>
        [TestMethod]
        public void GetDamageRating_SubtractsTheGearDamageOffset() => AssertRatingPin("public int GetDamageRating()", "GearDamage");

        /// <summary>Creature.GetCritRating subtracts the GearCrit (Weak Point) offset, and no other.</summary>
        [TestMethod]
        public void GetCritRating_SubtractsTheGearCritOffset() => AssertRatingPin("public int GetCritRating()", "GearCrit");

        /// <summary>Creature.GetCritDamageRating subtracts the GearCritDamage (Devastation) offset, and no other.</summary>
        [TestMethod]
        public void GetCritDamageRating_SubtractsTheGearCritDamageOffset() => AssertRatingPin("public int GetCritDamageRating()", "GearCritDamage");

        // ================= the shield-ignore site =================

        /// <summary>
        /// WorldObject.GetIgnoreShieldMod routes the WEAPON term through WeaponIgnoreShield with this attacker's
        /// suppression, and still takes the max against the attacker's own term.
        /// </summary>
        [TestMethod]
        public void GetIgnoreShieldMod_RoutesTheWeaponTermThroughSuppression()
        {
            var body = MethodBody(WorldObjectWeapon, "public float GetIgnoreShieldMod(WorldObject weapon)");

            AssertContains(body, "var weaponMod = WeaponModSuppression.WeaponIgnoreShield(weapon, WeaponModSuppression.SuppressedFor(this));",
                "GetIgnoreShieldMod");
            AssertContains(body, "var creatureMod = IgnoreShield ?? 0.0f;", "GetIgnoreShieldMod");
            AssertContains(body, "return 1.0f - (float)Math.Max(creatureMod, weaponMod);", "GetIgnoreShieldMod");
            Assert.IsFalse(Squash(body).Contains(Squash("weapon?.IgnoreShield"), StringComparison.Ordinal),
                "GetIgnoreShieldMod reads weapon?.IgnoreShield directly again, which bypasses Shield Bypass suppression");
        }

        // ================= Paragon weapon mods: the read-time adds =================
        //
        // EXACT-STATEMENT pins, not substring pins: the comment-stripped body is split into statements on ';' and
        // one statement must equal the expected text (whitespace-insensitive). A substring match would still pass
        // with the add moved onto a dead line or wrapped in a condition that disables it.

        private const string WeaponModCombatFile = "Source/ACE.Server/WeaponMods/WeaponModCombat.cs";

        /// <summary>Creature.GetDamageRating adds the GearDamage (Bloodthirst) Paragon amount in the equipment term.</summary>
        [TestMethod]
        public void GetDamageRating_AddsTheGearDamageParagonAmount() => AssertParagonRatingPin("public int GetDamageRating()", "GearDamage");

        /// <summary>Creature.GetCritRating adds the GearCrit (Weak Point) Paragon amount in the equipment term.</summary>
        [TestMethod]
        public void GetCritRating_AddsTheGearCritParagonAmount() => AssertParagonRatingPin("public int GetCritRating()", "GearCrit");

        /// <summary>Creature.GetCritDamageRating adds the GearCritDamage (Devastation) Paragon amount in the equipment term.</summary>
        [TestMethod]
        public void GetCritDamageRating_AddsTheGearCritDamageParagonAmount() => AssertParagonRatingPin("public int GetCritDamageRating()", "GearCritDamage");

        /// <summary>
        /// WorldObject.GetIgnoreShieldMod adds the Paragon Shield Bypass amount to the WEAPON term, resolved for this
        /// attacker, before the max against the attacker's own term is returned.
        /// </summary>
        [TestMethod]
        public void GetIgnoreShieldMod_AddsTheParagonShieldBypassAmount()
        {
            var statements = Statements(MethodBody(WorldObjectWeapon, "public float GetIgnoreShieldMod(WorldObject weapon)"));

            var add = IndexOfStatement(statements, "weaponMod += WeaponModParagon.IgnoreShieldAddFor(weapon, this)", "GetIgnoreShieldMod");
            var clamp = IndexOfStatement(statements, "weaponMod = WeaponModParagon.ClampWeaponIgnoreShield(weaponMod)", "GetIgnoreShieldMod");
            var ret = IndexOfStatement(statements, "return 1.0f - (float)Math.Max(creatureMod, weaponMod)", "GetIgnoreShieldMod");

            Assert.IsTrue(add < clamp, "GetIgnoreShieldMod: the weapon-term clamp must run after the Paragon add");
            Assert.IsTrue(clamp < ret, "GetIgnoreShieldMod: the weapon-term clamp must run before the return");
        }

        /// <summary>
        /// The Tier B accessor folds the Paragon fraction into the record read, so a Paragon-only mod is not skipped by
        /// the old "record absent, return 0" exit. The fold lives in the shared ReadCore, which both the wielder read
        /// (ReadWeaponOnly) and the Summoner focus pet read (ReadFocusModForPets) end on.
        /// </summary>
        [TestMethod]
        public void ReadWeaponOnly_FoldsTheParagonFractionIntoTheRecord()
        {
            var core = Statements(MethodBody(WeaponModCombatFile, "private static double ReadCore(WorldObject weapon, WeaponModDefinition definition, WeaponModId modId, double scale)"));

            IndexOfStatement(core,
                "var fraction = WeaponModParagon.CombinedFraction(weapon.GetProperty(definition.Record), WeaponModParagon.ParagonFraction(weapon, modId))",
                "ReadCore");
            IndexOfStatement(core, "return WeaponModValue.MagnitudeFromFraction(definition, fraction, scale, weapon)", "ReadCore");

            var wielder = Statements(MethodBody(WeaponModCombatFile, "public static double ReadWeaponOnly(WorldObject weapon, WeaponModId modId, bool enabled, double scale)"));
            IndexOfStatement(wielder, "return ReadCore(weapon, definition, modId, scale)", "ReadWeaponOnly");

            var pets = Statements(MethodBody(WeaponModCombatFile, "internal static double ReadFocusModForPets(WorldObject focus, WeaponModId modId)"));
            IndexOfStatement(pets, "return ReadCore(focus, definition, modId, WeaponModValue.MagnitudeScale())", "ReadFocusModForPets");
        }

        /// <summary>The exact-statement helper rejects a near miss that a substring pin would accept.</summary>
        [TestMethod]
        public void Statements_ExactMatchRejectsASubstringNearMiss()
        {
            var statements = Statements("{ var x = 1; if (false) weaponMod += WeaponModParagon.IgnoreShieldAddFor(weapon, this); }");

            Assert.IsFalse(statements.Contains(Squash("weaponMod += WeaponModParagon.IgnoreShieldAddFor(weapon, this)")),
                "a guarded statement must not satisfy an exact pin");
            Assert.IsTrue(statements.Contains(Squash("var x = 1")), "a plain statement must match");
        }

        private static void AssertParagonRatingPin(string signature, string native)
        {
            var statements = Statements(MethodBody(CreatureRating, signature));

            IndexOfStatement(statements,
                // The Summoner focus terms (Docs/SummonerCasters/DESIGN.md) ride the same statement: the pet bonus and the wielder strip
                $"var equipment = GetEquippedItemsRatingSum(PropertyInt.{native}) - WeaponModSuppression.RatingOffsetFor(this, PropertyInt.{native}) + WeaponModParagon.RatingAddFor(this, PropertyInt.{native}) + SummonerFocus.PetFocusRatingBonus(this, PropertyInt.{native}) - SummonerFocus.WielderFocusRatingStrip(this, PropertyInt.{native})",
                signature);

            foreach (var other in RatingNatives)
            {
                if (other == native)
                    continue;

                foreach (var statement in statements)
                    Assert.IsFalse(statement.Contains(Squash($"RatingAddFor(this,PropertyInt.{other})"), StringComparison.Ordinal),
                        $"{signature} adds the {other} Paragon amount - that is another rating's native");
            }
        }

        /// <summary>The body split into whitespace-free statements on ';', each with leading braces trimmed.</summary>
        private static System.Collections.Generic.List<string> Statements(string body)
        {
            var list = new System.Collections.Generic.List<string>();

            foreach (var part in Squash(body).Split(';'))
                list.Add(part.TrimStart('{', '}'));

            return list;
        }

        private static int IndexOfStatement(System.Collections.Generic.List<string> statements, string expected, string where)
        {
            var at = statements.IndexOf(Squash(expected));
            Assert.IsTrue(at >= 0, $"{where}: expected a live statement exactly equal to:\n  {expected}");
            return at;
        }

        // ================= the pin machinery proves itself =================

        /// <summary>
        /// The claim every pin above rests on: text inside a // or /* */ comment is removed before searching, and
        /// text inside a string literal is not mistaken for a comment.
        /// </summary>
        [TestMethod]
        public void StripComments_ACommentedPinDoesNotSatisfyTheSearch()
        {
            const string pin = "GetEquippedItemsRatingSum(PropertyInt.GearCrit) - WeaponModSuppression.RatingOffsetFor(this, PropertyInt.GearCrit)";

            var lineCommented = "int x = 1;\n// var equipment = " + pin + ";\n";
            var blockCommented = "int x = 1;\n/* var equipment = " + pin + "; */\n";
            var docCommented = "/// var equipment = " + pin + ";\nint x = 1;\n";

            foreach (var source in new[] { lineCommented, blockCommented, docCommented })
                Assert.IsFalse(Squash(StripComments(source)).Contains(Squash(pin), StringComparison.Ordinal), source);

            var live = "var equipment = " + pin + "; // trailing note";
            Assert.IsTrue(Squash(StripComments(live)).Contains(Squash(pin), StringComparison.Ordinal), "a live line must still match");

            var stringWithSlashes = "var url = \"http://example\"; var y = 2;";
            Assert.IsTrue(StripComments(stringWithSlashes).Contains("var y = 2;", StringComparison.Ordinal),
                "a // inside a string literal was treated as a comment");
        }

        // ================= helpers =================

        private static void AssertRatingPin(string signature, string native)
        {
            var body = MethodBody(CreatureRating, signature);

            var expected = $"GetEquippedItemsRatingSum(PropertyInt.{native}) - WeaponModSuppression.RatingOffsetFor(this, PropertyInt.{native})";
            AssertContains(body, expected, signature);

            foreach (var other in RatingNatives)
            {
                if (other == native)
                    continue;

                Assert.IsFalse(Squash(body).Contains(Squash($"RatingOffsetFor(this,PropertyInt.{other})"), StringComparison.Ordinal),
                    $"{signature} subtracts the {other} weapon-mod offset - that is another rating's native");
                Assert.IsFalse(Squash(body).Contains(Squash($"GetEquippedItemsRatingSum(PropertyInt.{other})"), StringComparison.Ordinal),
                    $"{signature} sums {other} - that is another rating's native");
            }
        }

        private static void AssertContains(string body, string expected, string where)
        {
            Assert.IsTrue(Squash(body).Contains(Squash(expected), StringComparison.Ordinal),
                $"{where}: expected live code (comments stripped) containing:\n  {expected}");
        }

        /// <summary>
        /// The brace-matched body of the method whose declaration is <paramref name="signature"/>, from the
        /// comment-stripped source. Fails if the signature is absent or appears more than once.
        /// </summary>
        private static string MethodBody(string relativePath, string signature)
        {
            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var source = StripComments(File.ReadAllText(path));

            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"{relativePath}: declaration not found in live code: {signature}");
            Assert.AreEqual(-1, source.IndexOf(signature, at + signature.Length, StringComparison.Ordinal),
                $"{relativePath}: declaration appears more than once: {signature}");

            var open = source.IndexOf('{', at + signature.Length);
            Assert.IsTrue(open >= 0, $"{relativePath}: no body after {signature}");

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"{relativePath}: unbalanced braces after {signature}");
            return null;
        }

        /// <summary>
        /// Removes // and /* */ comments, keeping string and char literals intact (regular, verbatim @"..."
        /// and interpolated strings). Comment text is replaced by a single space so tokens do not fuse.
        /// </summary>
        internal static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            var i = 0;

            while (i < source.Length)
            {
                var c = source[i];
                var next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                        i++;

                    sb.Append(' ');
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? source.Length : end + 2;
                    sb.Append(' ');
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    var verbatim = c == '"' && i > 0 && (source[i - 1] == '@' || (source[i - 1] == '$' && i > 1 && source[i - 2] == '@'));
                    sb.Append(c);
                    i++;

                    while (i < source.Length)
                    {
                        var s = source[i];
                        sb.Append(s);
                        i++;

                        if (!verbatim && s == '\\' && i < source.Length)
                        {
                            sb.Append(source[i]);
                            i++;
                            continue;
                        }

                        if (s == c)
                        {
                            if (verbatim && i < source.Length && source[i] == '"')
                            {
                                sb.Append('"');
                                i++;
                                continue;
                            }

                            break;
                        }

                        if (!verbatim && s == '\n')
                            break;
                    }

                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private static string Squash(string text)
        {
            var sb = new StringBuilder(text.Length);

            foreach (var c in text)
            {
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }

            return sb.ToString();
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
