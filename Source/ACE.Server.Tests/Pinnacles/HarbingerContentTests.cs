using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Pinnacles.Harbinger;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Content pins tying the Harbinger weenie SQL to the code (final review M3): the head's DefaultScale is
    /// HarbingerLayout.HeadScale (the scale its measured radius and melee reach assume), each alcove portal's Destination is
    /// its HarbingerData alcove's Stand point and cell, and every guardian carries the leash (TetherRadius, HomeRadius).
    /// Pure file + regex parsing of the committed SQL (no dat, no DB), the MatronBodyPartContentTests idiom.
    /// </summary>
    [TestClass]
    public class HarbingerContentTests
    {
        private const int DefaultScale = 39;
        private const int HomeRadius = 55;
        private const int TetherRadius = 9009;

        /// <summary>The value of float property <paramref name="type"/> on <paramref name="wcid"/>, or null when the row is absent.</summary>
        private static double? Float(int wcid, int type)
        {
            var m = Regex.Match(Strip(File.ReadAllText(GetFile(wcid))), $@"\(\s*{wcid}\s*,\s*{type}\s*,\s*(-?[0-9.]+)\s*\)");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        private static string Strip(string sql) => Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        [TestMethod]
        public void Head_DefaultScale_IsHeadScale()
        {
            Assert.AreEqual(HarbingerLayout.HeadScale, Float((int)HarbingerData.HeadWcid, DefaultScale) ?? -1, 1e-6);
        }

        [TestMethod]
        public void EveryAlcovePortal_Destination_IsItsAlcovesStandPointAndCell()
        {
            Assert.AreEqual(4, HarbingerData.Alcoves.Count);
            CollectionAssert.AreEquivalent(new uint[] { 1008264, 1008265, 1008266, 1008267 }, HarbingerData.Alcoves.Select(a => a.PortalWcid).ToList());

            foreach (var alcove in HarbingerData.Alcoves)
            {
                var wcid = (int)alcove.PortalWcid;
                var sql = Strip(File.ReadAllText(GetFile(wcid)));
                var m = Regex.Match(sql,
                    $@"\(\s*{wcid}\s*,\s*2\s*,\s*0x([0-9A-Fa-f]{{8}})\s*,\s*(-?[0-9.]+)\s*,\s*(-?[0-9.]+)\s*,\s*(-?[0-9.]+)\s*,\s*(-?[0-9.]+)\s*,\s*(-?[0-9.]+)\s*,\s*(-?[0-9.]+)\s*,\s*(-?[0-9.]+)\s*\)");
                Assert.IsTrue(m.Success, $"{wcid}: one Destination (position type 2) row");

                double D(int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
                var what = $"{wcid} ({alcove.Element})";
                var stand = alcove.Stand;

                Assert.AreEqual(alcove.Cell, uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber), what + " cell");
                Assert.AreEqual(stand.Cell, alcove.Cell, what + " the stand is in the alcove's cell");
                Assert.AreEqual(stand.X, D(2), 0.005, what + " x");
                Assert.AreEqual(stand.Y, D(3), 0.005, what + " y");
                Assert.AreEqual(stand.Z, D(4), 0.005, what + " z");
                Assert.AreEqual(stand.QW, D(5), 1e-4, what + " qw");
                Assert.AreEqual(stand.QX, D(6), 1e-4, what + " qx");
                Assert.AreEqual(stand.QY, D(7), 1e-4, what + " qy");
                Assert.AreEqual(stand.QZ, D(8), 1e-4, what + " qz");
            }
        }

        [TestMethod]
        public void EveryGuardian_CarriesTheLeash()
        {
            foreach (var def in HarbingerData.Elements)
            {
                var wcid = (int)def.GuardianWcid;

                Assert.IsTrue(Float(wcid, TetherRadius) > 0, $"{wcid}: TetherRadius (9009)");
                Assert.IsTrue(Float(wcid, HomeRadius) > 0, $"{wcid}: HomeRadius (55)");
            }
        }

        /// <summary>Like <see cref="Float"/> but scoped to the weenie_properties_float block, because int rows share type numbers (68 is also TargetingTactic).</summary>
        private static double? FloatInBlock(int wcid, int type)
        {
            var sql = Strip(File.ReadAllText(GetFile(wcid)));
            var start = sql.IndexOf("`weenie_properties_float`", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{wcid}: float block present");
            var end = sql.IndexOf(';', start);
            var m = Regex.Match(sql.Substring(start, end - start), $@"\(\s*{wcid}\s*,\s*{type}\s*,\s*(-?[0-9.]+)\s*\)");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        /// <summary>
        /// Round 2 A6: the crystal's monster-AI casting stays as a backstop but never out-paces the scripted cast. Each crystal's
        /// AiUseMagicDelay (float 80) is at least the script's default crystal cast interval (HarbingerSettings.CrystalCastSeconds).
        /// </summary>
        [TestMethod]
        public void EveryCrystal_AiUseMagicDelay_IsNoFasterThanTheScriptedCast()
        {
            const int AiUseMagicDelay = 80;

            foreach (var def in HarbingerData.Elements)
            {
                var v = FloatInBlock((int)def.CrystalWcid, AiUseMagicDelay);

                Assert.IsNotNull(v, $"{def.CrystalWcid}: AiUseMagicDelay (float 80) present");
                Assert.IsTrue(v >= HarbingerSettings.Defaults.CrystalCastSeconds,
                    $"{def.CrystalWcid}: AiUseMagicDelay {v} is faster than the scripted cast ({HarbingerSettings.Defaults.CrystalCastSeconds} s)");
            }
        }

        private static uint DidOf(int wcid, int type)
        {
            var m = Regex.Match(Strip(File.ReadAllText(GetFile(wcid))), $@"\(\s*{wcid}\s*,\s*{type}\s*,\s*0x([0-9A-Fa-f]{{8}})\s*\)");
            Assert.IsTrue(m.Success, $"{wcid}: d_i_d row type {type}");
            return uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
        }

        [TestMethod]
        public void EveryGuardian_IsItsElementsRetailKnightBody()
        {
            // Caustic 31832, Galvanic 31829, Glacial 31828, Incendiary 31827 (ace_world), by element
            var knightSetup = new System.Collections.Generic.Dictionary<HarbingerElement, uint>
            {
                { HarbingerElement.Acid, 0x02001481 }, { HarbingerElement.Lightning, 0x02001487 },
                { HarbingerElement.Frost, 0x02001482 }, { HarbingerElement.Fire, 0x02001483 },
            };
            Assert.AreEqual(4, HarbingerData.Elements.Count);

            foreach (var def in HarbingerData.Elements)
                Assert.AreEqual(knightSetup[def.Element], DidOf((int)def.GuardianWcid, 1), $"{def.GuardianWcid} ({def.Element}) Setup is the retail knight's");
        }

        [TestMethod]
        public void EveryAlcovePortal_SetupIsItsElementsColour()
        {
            Assert.AreEqual(0x020005D3u, DidOf(1008264, 1), "1008264 Acid green");
            Assert.AreEqual(0x020005D5u, DidOf(1008265, 1), "1008265 Fire red");
            Assert.AreEqual(0x020005D2u, DidOf(1008266, 1), "1008266 Frost blue");
            Assert.AreEqual(0x020001B3u, DidOf(1008267, 1), "1008267 Lightning purple");
        }

        [TestMethod]
        public void EveryCrystal_SpellBook_IsItsWarSpellsAndVulnerability()
        {
            foreach (var def in HarbingerData.Elements)
            {
                var wcid = (int)def.CrystalWcid;
                var sql = Strip(File.ReadAllText(GetFile(wcid)));
                var bookSql = sql.Substring(sql.IndexOf("`weenie_properties_spell_book`", StringComparison.Ordinal));
                var book = Regex.Matches(bookSql, $@"\(\s*{wcid}\s*,\s*(\d+)\s*,\s*[0-9.]+\s*\)").Select(m => uint.Parse(m.Groups[1].Value)).OrderBy(x => x).ToList();
                var expected = def.WarSpells.Append(def.VulnSpell).OrderBy(x => x).ToList();

                Assert.IsTrue(book.Count > 0, $"{wcid}: spell book rows found (control)");
                CollectionAssert.AreEqual(expected, book, $"{wcid} ({def.Element}) spell book is WarSpells + VulnSpell");
            }
        }

        [TestMethod]
        public void NoGuardian_IsImmuneToAnything()
        {
            foreach (var def in HarbingerData.Elements)
            {
                var wcid = (int)def.GuardianWcid;

                for (var type = 64; type <= 70; type++)
                {
                    var v = FloatInBlock(wcid, type);
                    Assert.IsNotNull(v, $"{wcid}: Resist float {type} present");
                    Assert.IsTrue(v > 0, $"{wcid}: Resist float {type} is {v}; a 0 is immunity and can hard-block a single-element player");
                }
            }
        }

        private static string GetFile(int wcid)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var weenies = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(weenies))
                {
                    var file = Directory.EnumerateFiles(weenies, wcid + " *.sql", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                    Assert.IsNotNull(file, $"{wcid}: no weenie file found in {weenies}.");
                    return file;
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}
