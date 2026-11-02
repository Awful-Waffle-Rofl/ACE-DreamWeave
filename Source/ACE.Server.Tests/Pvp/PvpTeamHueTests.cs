using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>The team hue pure core (Docs/Pvp/TEMPLATES.md "Team hue"): KitHueFor, the override parser, ResolveItem and Apply. No DAT, no PropertyManager.</summary>
    [TestClass]
    public class PvpTeamHueTests
    {
        private const uint CbA = 0x1000018E;
        private const uint CbB = 0x100002D5;
        private const uint CbOther = 0x10000200;

        private static readonly int CbKey = (int)PropertyDataId.ClothingBase;
        private static readonly int IconKey = (int)PropertyDataId.Icon;
        private static readonly int PaletteKey = (int)PropertyInt.PaletteTemplate;
        private static readonly int ShadeKey = (int)PropertyFloat.Shade;

        /// <summary>CbA lists 2, 85, 88, 90; CbB lists 2, 85, 88, 90 and NOT 20; CbOther lists only 2 and 13.</summary>
        private static IReadOnlyCollection<uint> Valid(uint cb)
        {
            if (cb == CbA || cb == CbB)
                return new uint[] { 2, 85, 88, 90 };

            if (cb == CbOther)
                return new uint[] { 2, 13 };

            return null;
        }

        private static uint TableIcon(uint cb, uint palette) => 0x06000000u + palette;

        private static PvpTeamHueDials Dials(bool enabled = true, int red = 85, int blue = 88, int neutral = 90,
            IReadOnlyDictionary<(uint, KitHue), PvpTeamHueLook> overrides = null) =>
            new PvpTeamHueDials(enabled, new PvpTeamHueLook(red, 0.0), new PvpTeamHueLook(blue, 0.0), new PvpTeamHueLook(neutral, 0.0),
                overrides ?? new Dictionary<(uint, KitHue), PvpTeamHueLook>());

        private static PvpTemplateKitItem Worn(uint cb, int location = 0x200, uint wcid = 100, string name = "Plate")
        {
            var item = new PvpTemplateKitItem { WeenieClassId = wcid, WieldLocation = location };
            item.DataIds[CbKey] = cb;
            item.DataIds[IconKey] = 0x06001111;
            item.Ints[PaletteKey] = 2;
            item.Floats[ShadeKey] = 0.25;
            item.Strings[(int)PropertyString.Name] = name;
            return item;
        }

        private static PvpTemplateDefinition Definition(params PvpTemplateKitItem[] items)
        {
            var d = new PvpTemplateDefinition { Key = "duelist", Version = 3, DisplayName = "Duelist" };
            d.Spells.Add(7);
            d.PowerInts[1] = 2;
            d.Kit.AddRange(items);
            return d;
        }

        private static string Json(PvpTemplateDefinition d) => PvpTemplateJson.SerializeDefinition(d);

        // ---------------- KitHueFor ----------------

        [TestMethod]
        public void KitHueFor_TwoTeams_RedIsRed_BlueIsBlue_ForEveryRule()
        {
            foreach (var rule in new[] { PvpColourRule.CoinFlip, PvpColourRule.AttackDefend })
            {
                Assert.AreEqual(KitHue.Red, PvpTeamHue.KitHueFor(rule, 2, PvpTeamColour.Red), rule.ToString());
                Assert.AreEqual(KitHue.Blue, PvpTeamHue.KitHueFor(rule, 2, PvpTeamColour.Blue), rule.ToString());
            }
        }

        [TestMethod]
        public void KitHueFor_AnyOtherTeamCount_IsNeutral_ExceptTugak()
        {
            foreach (var count in new[] { 0, 1, 3, 4, 8 })
            {
                foreach (var colour in new[] { PvpTeamColour.Red, PvpTeamColour.Blue })
                {
                    Assert.AreEqual(KitHue.Neutral, PvpTeamHue.KitHueFor(PvpColourRule.CoinFlip, count, colour), $"coin flip, {count} teams");
                    Assert.AreEqual(KitHue.Neutral, PvpTeamHue.KitHueFor(PvpColourRule.AttackDefend, count, colour), $"attack/defend, {count} teams");
                }
            }
        }

        [TestMethod]
        public void KitHueFor_Tugak_KeepsTheCoinFlippedColour_WhateverTheTeamCount()
        {
            foreach (var count in new[] { 1, 2, 3, 6, 8 })
            {
                Assert.AreEqual(KitHue.Red, PvpTeamHue.KitHueFor(PvpColourRule.Tugak, count, PvpTeamColour.Red), $"{count} teams");
                Assert.AreEqual(KitHue.Blue, PvpTeamHue.KitHueFor(PvpColourRule.Tugak, count, PvpTeamColour.Blue), $"{count} teams");
            }
        }

        /// <summary>The hue is derived from the SAME colour rules the match uses, so the two cannot drift apart.</summary>
        [TestMethod]
        public void KitHueFor_AgreesWithAssign_ForEveryModeAndCoin()
        {
            foreach (var mode in new[] { ArenaMapCatalog.OneVOneKey, ArenaMapCatalog.TwoVTwoKey, ArenaMapCatalog.FfaKey, "bg_koth", "bg_ad" })
            {
                var rule = PvpTeamColours.RuleFor(mode);

                foreach (var coin in new[] { true, false })
                {
                    for (var team = 0; team < 2; team++)
                    {
                        var colour = PvpTeamColours.Assign(rule, 2, team, coin);
                        var expected = colour == PvpTeamColour.Red ? KitHue.Red : KitHue.Blue;

                        Assert.AreEqual(expected, PvpTeamHue.KitHueFor(rule, 2, colour), $"{mode} coin {coin} team {team}");
                    }
                }
            }
        }

        // ---------------- ParseOverrides ----------------

        [TestMethod]
        public void ParseOverrides_ReadsHexClothingBasesTeamsPalettesAndShades()
        {
            var warns = new List<string>();
            var map = PvpTeamHue.ParseOverrides("1000018E:red=85/0.4,blue=88/0.6; 0x100002D5:neutral=90", warns.Add);

            Assert.AreEqual(0, warns.Count, string.Join("|", warns));
            Assert.AreEqual(3, map.Count);
            Assert.AreEqual(new PvpTeamHueLook(85, 0.4), map[(0x1000018Eu, KitHue.Red)]);
            Assert.AreEqual(new PvpTeamHueLook(88, 0.6), map[(0x1000018Eu, KitHue.Blue)]);
            Assert.AreEqual(new PvpTeamHueLook(90, 0.0), map[(0x100002D5u, KitHue.Neutral)], "a missing shade is 0");
        }

        [TestMethod]
        public void ParseOverrides_EmptyOrBlank_IsEmpty_WithNoWarning()
        {
            var warns = new List<string>();

            Assert.AreEqual(0, PvpTeamHue.ParseOverrides(null, warns.Add).Count);
            Assert.AreEqual(0, PvpTeamHue.ParseOverrides("", warns.Add).Count);
            Assert.AreEqual(0, PvpTeamHue.ParseOverrides("  ; ;", warns.Add).Count);
            Assert.AreEqual(0, warns.Count);
        }

        [TestMethod]
        public void ParseOverrides_ABadEntry_IsWarnedAndSkipped_TheGoodOnesSurvive()
        {
            var bad = new[]
            {
                "nothex:red=85", "1000018E", "1000018E:", "1000018E:green=85", "1000018E:red", "1000018E:red=abc", "1000018E:red=-5",
                "1000018E:red=85/2", "1000018E:red=85/-0.1", "1000018E:red=85/nan", "1000018E:red=85/x", "0:red=85", "1000018E:red=85,blue=zz",
            };

            foreach (var entry in bad)
            {
                var warns = new List<string>();
                var map = PvpTeamHue.ParseOverrides(entry + ";10000200:blue=13/0.5", warns.Add);

                Assert.AreEqual(1, warns.Count, $"'{entry}' warns once: {string.Join("|", warns)}");
                Assert.AreEqual(1, map.Count, $"'{entry}' is skipped whole, the good entry stays");
                Assert.AreEqual(new PvpTeamHueLook(13, 0.5), map[(0x10000200u, KitHue.Blue)], entry);
            }
        }

        [TestMethod]
        public void ParseOverrides_NeverThrows_OnGarbage()
        {
            foreach (var garbage in new[] { ":::", ";;;:", "=,=,=", "1000018E:=", "\0", "1000018E:red=85/0.4/0.5", new string('x', 10000) })
                PvpTeamHue.ParseOverrides(garbage, _ => { });
        }

        [TestMethod]
        public void ParseOverrides_ADuplicateTeam_IsWarned_AndTheLaterWins()
        {
            var warns = new List<string>();
            var map = PvpTeamHue.ParseOverrides("1000018E:red=85;1000018E:red=88", warns.Add);

            Assert.AreEqual(1, warns.Count);
            Assert.AreEqual(88, map[(0x1000018Eu, KitHue.Red)].Palette);
        }

        // ---------------- ResolveItem ----------------

        [TestMethod]
        public void ResolveItem_AGlobalPalette_IsUsed_WhenTheClothingBaseListsIt()
        {
            var r = PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Blue, Dials(), Valid);

            Assert.IsTrue(r.Applied);
            Assert.AreEqual(88, r.Palette);
            Assert.AreEqual(HueSource.Global, r.Source);
        }

        [TestMethod]
        public void ResolveItem_EachHueReadsItsOwnGlobal()
        {
            var dials = new PvpTeamHueDials(true, new PvpTeamHueLook(85, 0.1), new PvpTeamHueLook(88, 0.2), new PvpTeamHueLook(90, 0.3), new Dictionary<(uint, KitHue), PvpTeamHueLook>());

            Assert.AreEqual(new PvpTeamHueLook(85, 0.1), Look(PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, dials, Valid)));
            Assert.AreEqual(new PvpTeamHueLook(88, 0.2), Look(PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Blue, dials, Valid)));
            Assert.AreEqual(new PvpTeamHueLook(90, 0.3), Look(PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Neutral, dials, Valid)));
        }

        private static PvpTeamHueLook Look(HueResolution r) => new PvpTeamHueLook(r.Palette, r.Shade);

        [TestMethod]
        public void ResolveItem_AValidOverride_BeatsTheGlobal()
        {
            var dials = Dials(overrides: new Dictionary<(uint, KitHue), PvpTeamHueLook> { [(CbA, KitHue.Red)] = new PvpTeamHueLook(88, 0.7) });
            var r = PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, dials, Valid);

            Assert.IsTrue(r.Applied);
            Assert.AreEqual(new PvpTeamHueLook(88, 0.7), Look(r));
            Assert.AreEqual(HueSource.Override, r.Source);

            // The override is per ClothingBase AND per hue: another ClothingBase, and another hue, read the global.
            Assert.AreEqual(HueSource.Global, PvpTeamHue.ResolveItem(Worn(CbB), KitHue.Red, dials, Valid).Source);
            Assert.AreEqual(HueSource.Global, PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Blue, dials, Valid).Source);
        }

        [TestMethod]
        public void ResolveItem_AnInvalidOverride_FallsBackToTheGlobal()
        {
            var dials = Dials(overrides: new Dictionary<(uint, KitHue), PvpTeamHueLook> { [(CbA, KitHue.Red)] = new PvpTeamHueLook(20, 0.7) });
            var r = PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, dials, Valid);

            Assert.IsTrue(r.Applied);
            Assert.AreEqual(85, r.Palette, "20 is not listed, so the global 85 wins");
            Assert.AreEqual(HueSource.Global, r.Source);
        }

        [TestMethod]
        public void ResolveItem_AnOverrideOfZero_KeepsTheOwnLook_EvenWhenTheGlobalIsValid()
        {
            var dials = Dials(overrides: new Dictionary<(uint, KitHue), PvpTeamHueLook> { [(CbA, KitHue.Red)] = new PvpTeamHueLook(0, 0) });
            var r = PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, dials, Valid);

            Assert.IsFalse(r.Applied);
            Assert.AreEqual(HueSkipKind.KeepOwn, r.Skip);
        }

        [TestMethod]
        public void ResolveItem_AGlobalPaletteOfZero_KeepsTheOwnLook()
        {
            var r = PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, Dials(red: 0), Valid);

            Assert.IsFalse(r.Applied);
            Assert.AreEqual(HueSkipKind.KeepOwn, r.Skip);
        }

        [TestMethod]
        public void ResolveItem_AnInvalidGlobal_KeepsTheOwnLook_AndNamesTheReason()
        {
            var r = PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, Dials(red: 20), Valid);

            Assert.IsFalse(r.Applied);
            Assert.AreEqual(HueSkipKind.NoValidPalette, r.Skip);
            StringAssert.Contains(r.Detail, "20");
            StringAssert.Contains(r.Detail, "0x1000018E");
        }

        [TestMethod]
        public void ResolveItem_AnInvalidOverrideAndAnInvalidGlobal_ReportsBoth()
        {
            var dials = Dials(red: 20, overrides: new Dictionary<(uint, KitHue), PvpTeamHueLook> { [(CbA, KitHue.Red)] = new PvpTeamHueLook(21, 0) });
            var r = PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, dials, Valid);

            Assert.IsFalse(r.Applied);
            StringAssert.Contains(r.Detail, "override palette 21");
            StringAssert.Contains(r.Detail, "red palette 20");
        }

        [TestMethod]
        public void ResolveItem_AnUnreadableClothingBase_KeepsTheOwnLook()
        {
            // CbOther exists but lists only 2 and 13; an unknown ClothingBase returns null (no dat entry).
            Assert.AreEqual(HueSkipKind.NoValidPalette, PvpTeamHue.ResolveItem(Worn(CbOther), KitHue.Red, Dials(), Valid).Skip);
            Assert.AreEqual(HueSkipKind.NoValidPalette, PvpTeamHue.ResolveItem(Worn(0x10009999), KitHue.Red, Dials(), Valid).Skip);
            Assert.AreEqual(HueSkipKind.NoValidPalette, PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, Dials(), null).Skip, "no lookup at all");
            Assert.AreEqual(HueSkipKind.NoValidPalette, PvpTeamHue.ResolveItem(Worn(CbA), KitHue.Red, Dials(), _ => throw new InvalidOperationException("dat")).Skip, "a throwing lookup");
        }

        [TestMethod]
        public void ResolveItem_SkipsAPackItem_AnItemWithNoClothingBase_AndAnItemWithRawRows()
        {
            var pack = Worn(CbA);
            pack.WieldLocation = null;
            Assert.AreEqual(HueSkipKind.NotWorn, PvpTeamHue.ResolveItem(pack, KitHue.Red, Dials(), Valid).Skip);

            var zeroLoc = Worn(CbA, location: 0);
            Assert.AreEqual(HueSkipKind.NotWorn, PvpTeamHue.ResolveItem(zeroLoc, KitHue.Red, Dials(), Valid).Skip);

            var noCb = Worn(CbA);
            noCb.DataIds.Remove(CbKey);
            Assert.AreEqual(HueSkipKind.NoClothingBase, PvpTeamHue.ResolveItem(noCb, KitHue.Red, Dials(), Valid).Skip);

            var zeroCb = Worn(0);
            Assert.AreEqual(HueSkipKind.NoClothingBase, PvpTeamHue.ResolveItem(zeroCb, KitHue.Red, Dials(), Valid).Skip);

            var withPalette = Worn(CbA);
            withPalette.Palettes.Add(new PvpTemplatePalette { SubPaletteId = 1 });
            var withTexture = Worn(CbA);
            withTexture.TextureMaps.Add(new PvpTemplateTextureMap { PartIndex = 1 });
            var withAnim = Worn(CbA);
            withAnim.AnimParts.Add(new PvpTemplateAnimPart { Index = 1 });

            foreach (var raw in new[] { withPalette, withTexture, withAnim })
                Assert.AreEqual(HueSkipKind.RawRows, PvpTeamHue.ResolveItem(raw, KitHue.Red, Dials(), Valid).Skip);

            Assert.AreEqual(HueSkipKind.NotWorn, PvpTeamHue.ResolveItem(null, KitHue.Red, Dials(), Valid).Skip);
        }

        /// <summary>Whatever the dials, an applied look is always a palette the ClothingBase lists.</summary>
        [TestMethod]
        public void ResolveItem_NeverReturnsAnUnlistedPalette()
        {
            foreach (var cb in new[] { CbA, CbB, CbOther })
            {
                var listed = Valid(cb);

                foreach (var palette in Enumerable.Range(0, 100))
                {
                    var overrides = new Dictionary<(uint, KitHue), PvpTeamHueLook> { [(cb, KitHue.Red)] = new PvpTeamHueLook(palette, 0.5) };

                    foreach (var hue in new[] { KitHue.Red, KitHue.Blue, KitHue.Neutral })
                    {
                        var r = PvpTeamHue.ResolveItem(Worn(cb), hue, Dials(red: palette, blue: palette, neutral: palette, overrides: overrides), Valid);

                        if (r.Applied)
                            Assert.IsTrue(listed.Contains((uint)r.Palette), $"cb 0x{cb:X8} hue {hue} palette {palette} applied {r.Palette}");
                    }
                }
            }
        }

        // ---------------- Apply ----------------

        [TestMethod]
        public void Apply_DressesAWornItem_WritingOnlyPaletteShadeAndIcon()
        {
            var original = Definition(Worn(CbA, 0x200, 100, "Chest"));
            var before = Json(original);

            var result = PvpTeamHue.Apply(original, KitHue.Blue, Dials(), Valid, TableIcon);
            var item = result.Definition.Kit.Single();

            Assert.AreEqual(1, result.AppliedCount);
            Assert.AreEqual(88, item.Ints[PaletteKey]);
            Assert.AreEqual(0.0, item.Floats[ShadeKey]);
            Assert.AreEqual(0x06000000u + 88u, item.DataIds[IconKey]);
            Assert.AreEqual(CbA, item.DataIds[CbKey], "the ClothingBase is never changed");
            Assert.AreEqual(before, Json(original), "the input is not mutated");
            Assert.AreNotSame(original, result.Definition);
            Assert.AreNotSame(original.Kit[0], result.Definition.Kit[0]);
        }

        /// <summary>Everything except the three written properties is byte-identical to the input.</summary>
        [TestMethod]
        public void Apply_EveryOtherFieldOfTheKitAndTheDefinition_IsIdentical()
        {
            var worn = Worn(CbA, 0x200, 100, "Chest");
            worn.Ints[999] = 5;
            worn.Floats[998] = 1.5;
            worn.Bools[997] = true;
            worn.Strings[996] = "x";
            worn.Int64s[995] = 7;
            worn.DataIds[994] = 9;
            worn.SpellBook[993] = 2f;
            worn.Enchantments.Add(new PvpTemplateEnchantment { SpellId = 5 });

            var pack = Worn(CbA, 0x200, 101, "Potion");
            pack.WieldLocation = null;

            var input = Definition(worn, pack);
            var expected = Json(input);
            var result = PvpTeamHue.Apply(input, KitHue.Red, Dials(), Valid, TableIcon);

            // Rewind the three written properties on the output, and the whole definition must equal the input.
            var rewound = PvpTemplateJson.TryDeserializeDefinition(Json(result.Definition), out var copy, out _) ? copy : null;
            Assert.IsNotNull(rewound);
            rewound.Kit[0].Ints[PaletteKey] = 2;
            rewound.Kit[0].Floats[ShadeKey] = 0.25;
            rewound.Kit[0].DataIds[IconKey] = 0x06001111;

            Assert.AreEqual(expected, Json(rewound));
            Assert.AreEqual(Json(Definition(pack)), Json(Definition(result.Definition.Kit[1])), "a pack item is untouched");
        }

        [TestMethod]
        public void Apply_AnInvalidPalette_IsNeverWritten_AndTheItemIsReported()
        {
            var input = Definition(Worn(CbA, 0x200, 100, "Chest"), Worn(CbOther, 0x1, 101, "Helm"));
            var result = PvpTeamHue.Apply(input, KitHue.Red, Dials(), Valid, TableIcon);

            Assert.AreEqual(1, result.AppliedCount);
            Assert.AreEqual(85, result.Definition.Kit[0].Ints[PaletteKey]);

            // CbOther does not list 85: the helm keeps the exact look it had.
            Assert.AreEqual(2, result.Definition.Kit[1].Ints[PaletteKey]);
            Assert.AreEqual(0.25, result.Definition.Kit[1].Floats[ShadeKey]);
            Assert.AreEqual(0x06001111u, result.Definition.Kit[1].DataIds[IconKey]);

            var skipped = result.Skipped.Single();
            Assert.AreEqual("Helm", skipped.Name);
            Assert.AreEqual(HueSkipKind.NoValidPalette, skipped.Kind);
        }

        [TestMethod]
        public void Apply_DoesNotReportAPackItemAsSkipped_ButReportsAWornItemItCouldNotDress()
        {
            var pack = Worn(CbA, 0x200, 101, "Potion");
            pack.WieldLocation = null;
            var noCb = Worn(CbA, 0x1, 102, "Cloak");
            noCb.DataIds.Remove(CbKey);

            var result = PvpTeamHue.Apply(Definition(pack, noCb), KitHue.Red, Dials(), Valid, TableIcon);

            Assert.AreEqual(0, result.AppliedCount);
            Assert.AreEqual(1, result.Skipped.Count);
            Assert.AreEqual("Cloak", result.Skipped[0].Name);
            Assert.AreEqual(HueSkipKind.NoClothingBase, result.Skipped[0].Kind);
        }

        [TestMethod]
        public void Apply_TheIcon_IsWrittenOnlyWhereTheTableHasOneAndTheItemDoesNotOptOut()
        {
            var optOut = Worn(CbA, 0x200, 100, "Chest");
            optOut.Bools[(int)PropertyBool.IgnoreCloIcons] = true;
            var plain = Worn(CbA, 0x1, 101, "Helm");

            var withIcons = PvpTeamHue.Apply(Definition(optOut, plain), KitHue.Red, Dials(), Valid, TableIcon);
            Assert.AreEqual(0x06001111u, withIcons.Definition.Kit[0].DataIds[IconKey], "IgnoreCloIcons keeps the icon");
            Assert.AreEqual(0x06000000u + 85u, withIcons.Definition.Kit[1].DataIds[IconKey]);
            Assert.AreEqual(85, withIcons.Definition.Kit[0].Ints[PaletteKey], "the palette still lands on the opt-out item");

            var noIcon = PvpTeamHue.Apply(Definition(plain), KitHue.Red, Dials(), Valid, (cb, p) => 0);
            Assert.AreEqual(0x06001111u, noIcon.Definition.Kit[0].DataIds[IconKey], "a table with no icon keeps the item's own");

            var noFunc = PvpTeamHue.Apply(Definition(plain), KitHue.Red, Dials(), Valid);
            Assert.AreEqual(0x06001111u, noFunc.Definition.Kit[0].DataIds[IconKey], "no icon lookup keeps the item's own");
            Assert.AreEqual(85, noFunc.Definition.Kit[0].Ints[PaletteKey]);
        }

        [TestMethod]
        public void Apply_DisabledDials_ReturnADefinitionEqualToTheInput()
        {
            var input = Definition(Worn(CbA), Worn(CbOther, 0x1, 101, "Helm"));
            var before = Json(input);

            var result = PvpTeamHue.Apply(input, KitHue.Red, Dials(enabled: false), Valid, TableIcon);

            Assert.AreEqual(before, Json(result.Definition));
            Assert.AreEqual(0, result.AppliedCount);
            Assert.AreEqual(0, result.Skipped.Count);
            Assert.AreEqual(before, Json(input));
        }

        [TestMethod]
        public void Apply_NullDials_AndNullDefinition_AreSafe()
        {
            var input = Definition(Worn(CbA));
            var before = Json(input);

            Assert.AreEqual(before, Json(PvpTeamHue.Apply(input, KitHue.Red, null, Valid).Definition));
            Assert.IsNull(PvpTeamHue.Apply(null, KitHue.Red, Dials(), Valid).Definition);
        }

        [TestMethod]
        public void Apply_TwoHues_GiveDifferentLooks_FromTheSameInput()
        {
            var input = Definition(Worn(CbA));
            var red = PvpTeamHue.Apply(input, KitHue.Red, Dials(), Valid, TableIcon).Definition;
            var blue = PvpTeamHue.Apply(input, KitHue.Blue, Dials(), Valid, TableIcon).Definition;

            Assert.AreEqual(85, red.Kit[0].Ints[PaletteKey]);
            Assert.AreEqual(88, blue.Kit[0].Ints[PaletteKey]);
            Assert.AreEqual(2, input.Kit[0].Ints[PaletteKey], "the input still has its own look");
        }

        // ---------------- /pvphue audit and suit ----------------

        [TestMethod]
        public void AuditLines_ShowsEachHueForEachWornPiece_WithSourceOrSkipReason()
        {
            var dials = Dials(overrides: new Dictionary<(uint, KitHue), PvpTeamHueLook> { [(CbA, KitHue.Blue)] = new PvpTeamHueLook(90, 0.4) });
            var pack = Worn(CbA, 0x200, 103, "Potion");
            pack.WieldLocation = null;

            var lines = PvpTeamHue.AuditLines(Definition(Worn(CbA, 0x200, 100, "Chest"), Worn(CbOther, 0x1, 101, "Helm"), pack), dials, Valid);

            Assert.AreEqual(2, lines.Count, "the pack item is not listed");
            Assert.AreEqual("  Chest loc=0x200 cb=0x1000018E: red 85/0 global | blue 90/0.4 override | neutral 90/0 global", lines[0]);

            StringAssert.StartsWith(lines[1], "  Helm loc=0x1 cb=0x10000200: red skipped (");
            StringAssert.Contains(lines[1], "blue skipped (");
            StringAssert.Contains(lines[1], "neutral skipped (");
        }

        /// <summary>The audit and the production path agree: every cell the audit calls applied is exactly what Apply writes.</summary>
        [TestMethod]
        public void AuditLines_AgreeWithApply_ForEveryHue()
        {
            var input = Definition(Worn(CbA, 0x200, 100, "Chest"), Worn(CbOther, 0x1, 101, "Helm"));
            var dials = Dials();
            var lines = PvpTeamHue.AuditLines(input, dials, Valid);

            foreach (var hue in new[] { KitHue.Red, KitHue.Blue, KitHue.Neutral })
            {
                var applied = PvpTeamHue.Apply(input, hue, dials, Valid, TableIcon);

                for (var i = 0; i < input.Kit.Count; i++)
                {
                    var resolved = PvpTeamHue.ResolveItem(input.Kit[i], hue, dials, Valid);
                    var changed = applied.Definition.Kit[i].Ints[PaletteKey] != input.Kit[i].Ints[PaletteKey];

                    Assert.AreEqual(resolved.Applied, changed, $"{hue} item {i}");
                    StringAssert.Contains(lines[i], resolved.Applied ? $"{PvpTeamHue.Token(hue)} {resolved.Palette}/" : $"{PvpTeamHue.Token(hue)} skipped");
                }
            }
        }

        private static LookFields Palette(PvpTemplateKitItem kit) => PvpTeamHue.LookOf(kit);

        [TestMethod]
        public void LookOf_ReadsTheLookFieldsOfAKitItem()
        {
            var item = Worn(CbA);
            item.DataIds[(int)PropertyDataId.Setup] = 0x02000001;
            item.DataIds[(int)PropertyDataId.PaletteBase] = 0x04000999;
            item.Floats[(int)PropertyFloat.Shade2] = 0.2;
            item.Bools[(int)PropertyBool.IgnoreCloIcons] = true;

            var look = Palette(item);

            Assert.AreEqual(0x02000001u, look.Setup);
            Assert.AreEqual(CbA, look.ClothingBase);
            Assert.AreEqual(0x04000999u, look.PaletteBase);
            Assert.AreEqual(0x06001111u, look.Icon);
            Assert.AreEqual(2, look.PaletteTemplate);
            Assert.AreEqual(0.25, look.Shade);
            Assert.AreEqual(0.2, look.Shade2);
            Assert.IsNull(look.Shade3);
            Assert.AreEqual(true, look.IgnoreCloIcons);
        }

        [TestMethod]
        public void PlanSuit_CopiesEachKitPieceOntoTheWornPieceInTheSameSlot()
        {
            var dressed = PvpTeamHue.Apply(Definition(Worn(CbA, 0x200, 100, "Chest"), Worn(CbB, 0x1, 101, "Helm")), KitHue.Red, Dials(), Valid, TableIcon).Definition;
            var worn = new[] { new HueWorn(11, "Old Helm", 0x1), new HueWorn(12, "Old Chest", 0x200), new HueWorn(13, "Old Boots", 0x100) };

            var plan = PvpTeamHue.PlanSuit(dressed, worn);

            CollectionAssert.AreEquivalent(new uint[] { 12, 11 }, plan.Steps.Select(s => s.TargetGuid).ToList());
            Assert.AreEqual(85, plan.Steps.Single(s => s.TargetGuid == 12).Source.PaletteTemplate, "the chest takes the dressed palette");
            Assert.AreEqual(CbB, plan.Steps.Single(s => s.TargetGuid == 11).Source.ClothingBase);
            Assert.AreEqual(1, plan.Left.Count);
            StringAssert.Contains(plan.Left[0], "Old Boots");
            StringAssert.Contains(plan.Left[0], "no kit piece in its slot");
        }

        [TestMethod]
        public void PlanSuit_AWornPieceTakesAtMostOneKitPiece_AndASlotWithNoWornPieceIsReported()
        {
            // Two kit pieces in the chest slot, one worn chest: the first takes it, the second is reported.
            var dressed = Definition(Worn(CbA, 0x200, 100, "Chest A"), Worn(CbB, 0x200, 101, "Chest B"));
            var plan = PvpTeamHue.PlanSuit(dressed, new[] { new HueWorn(12, "Old Chest", 0x200) });

            Assert.AreEqual(1, plan.Steps.Count);
            Assert.AreEqual("Chest A", PvpTeamHue.ItemName(plan.Steps[0].Kit));
            Assert.AreEqual(1, plan.Left.Count);
            StringAssert.Contains(plan.Left[0], "Chest B");
        }

        [TestMethod]
        public void PlanSuit_APieceWithItsOwnRows_IsNeverTargeted_AndAPackItemIsIgnored()
        {
            var pack = Worn(CbA, 0x200, 103, "Potion");
            pack.WieldLocation = null;
            var plan = PvpTeamHue.PlanSuit(Definition(Worn(CbA, 0x200, 100, "Chest"), pack), new[] { new HueWorn(12, "Old Chest", 0x200, hasOwnRows: true) });

            Assert.AreEqual(0, plan.Steps.Count);
            Assert.AreEqual(2, plan.Left.Count, string.Join("|", plan.Left));
            Assert.IsTrue(plan.Left.Any(l => l.Contains("Old Chest") && l.Contains("own palette")));
        }

        /// <summary>
        /// A kit weapon (no ClothingBase) and a kit piece with raw rows are never dressed by a match, so the preview must not copy their
        /// look: copying a no-ClothingBase piece would null the worn piece's ClothingBase and swap its Setup.
        /// </summary>
        [TestMethod]
        public void PlanSuit_AKitWeaponWithNoClothingBase_AndAKitPieceWithRawRows_AreNotCopied_AndAreReported()
        {
            var weapon = Worn(CbA, 0x100000, 200, "Sword");
            weapon.DataIds.Remove(CbKey);

            var raw = Worn(CbA, 0x200, 201, "Raw Chest");
            raw.Palettes.Add(new PvpTemplatePalette { SubPaletteId = 1 });

            var plan = PvpTeamHue.PlanSuit(Definition(weapon, raw), new[] { new HueWorn(21, "Old Sword", 0x100000), new HueWorn(22, "Old Chest", 0x200) });

            Assert.AreEqual(0, plan.Steps.Count, "neither is copied");
            Assert.IsTrue(plan.Left.Any(l => l.Contains("Sword") && l.Contains("no ClothingBase")), string.Join("|", plan.Left));
            Assert.IsTrue(plan.Left.Any(l => l.Contains("Raw Chest") && l.Contains("raw")), string.Join("|", plan.Left));

            // Each raw-row kind counts, and a clean piece in the same plan is still copied (control: the skip is the rule, not a blanket).
            var texture = Worn(CbA, 0x200, 202, "Texture Chest");
            texture.TextureMaps.Add(new PvpTemplateTextureMap { PartIndex = 1 });
            var anim = Worn(CbA, 0x200, 203, "Anim Chest");
            anim.AnimParts.Add(new PvpTemplateAnimPart { Index = 1 });

            Assert.AreEqual(0, PvpTeamHue.PlanSuit(Definition(texture), new[] { new HueWorn(22, "Old Chest", 0x200) }).Steps.Count);
            Assert.AreEqual(0, PvpTeamHue.PlanSuit(Definition(anim), new[] { new HueWorn(22, "Old Chest", 0x200) }).Steps.Count);
            Assert.AreEqual(1, PvpTeamHue.PlanSuit(Definition(Worn(CbA, 0x200, 204, "Clean Chest")), new[] { new HueWorn(22, "Old Chest", 0x200) }).Steps.Count);
        }

        [TestMethod]
        public void PlanSuit_APieceSpanningSlotsOutsideTheKitPiece_IsNotTargeted()
        {
            // A worn piece covering chest AND abdomen (0x600) is not inside a chest-only kit piece (0x200).
            var plan = PvpTeamHue.PlanSuit(Definition(Worn(CbA, 0x200, 100, "Chest")), new[] { new HueWorn(12, "Robe", 0x600) });

            Assert.AreEqual(0, plan.Steps.Count);
        }

        [TestMethod]
        public void Apply_TheOverrideShade_IsWritten()
        {
            var dials = Dials(overrides: new Dictionary<(uint, KitHue), PvpTeamHueLook> { [(CbA, KitHue.Red)] = new PvpTeamHueLook(88, 0.4) });
            var result = PvpTeamHue.Apply(Definition(Worn(CbA)), KitHue.Red, dials, Valid, TableIcon);

            Assert.AreEqual(88, result.Definition.Kit[0].Ints[PaletteKey]);
            Assert.AreEqual(0.4, result.Definition.Kit[0].Floats[ShadeKey]);
        }

        // ---------------- Tugak Brawl: the per-seat palette pool ----------------

        [TestMethod]
        public void ParseFfaPalettes_ReadsTheShippedDefault_InOrder_WithNoWarning()
        {
            var warns = new List<string>();
            var pool = PvpTeamHue.ParseFfaPalettes("2,13,14,84,85,86,88,89,90", warns.Add);

            CollectionAssert.AreEqual(new[] { 2, 13, 14, 84, 85, 86, 88, 89, 90 }, pool.ToArray());
            Assert.AreEqual(0, warns.Count);
        }

        [TestMethod]
        public void ParseFfaPalettes_EmptyOrBlank_IsEmpty_WithoutWarning()
        {
            foreach (var text in new[] { null, "", "   ", ",", " , ,, " })
            {
                var warns = new List<string>();

                Assert.AreEqual(0, PvpTeamHue.ParseFfaPalettes(text, warns.Add).Count, $"'{text}'");
                Assert.AreEqual(0, warns.Count, $"'{text}' is empty, not bad");
            }
        }

        [TestMethod]
        public void ParseFfaPalettes_ABadEntry_IsWarnedAndSkipped_TheRestSurvive()
        {
            var warns = new List<string>();
            var pool = PvpTeamHue.ParseFfaPalettes(" 13 ,abc,-5,0,1.5,99999999999,14,0x10", warns.Add);

            CollectionAssert.AreEqual(new[] { 13, 14 }, pool.ToArray());
            Assert.AreEqual(6, warns.Count, "abc, -5, 0, 1.5, the overflow and 0x10 each warn");
            Assert.IsTrue(warns.All(w => w.Contains("pvp_team_hue_ffa_palettes")), "every warning names the setting");
            StringAssert.Contains(warns[0], "abc");
        }

        [TestMethod]
        public void ParseFfaPalettes_ARepeat_IsWarnedAndDropped_SoNoTwoSeatsCanShareAColour()
        {
            var warns = new List<string>();
            var pool = PvpTeamHue.ParseFfaPalettes("85,88,85,85", warns.Add);

            CollectionAssert.AreEqual(new[] { 85, 88 }, pool.ToArray());
            Assert.AreEqual(2, warns.Count);
        }

        [TestMethod]
        public void ParseFfaPalettes_AllInvalid_IsEmpty_AndNeverThrows_EvenWithoutAWarnSink()
        {
            Assert.AreEqual(0, PvpTeamHue.ParseFfaPalettes("x,y,0,-1").Count);
        }

        [TestMethod]
        public void FfaPaletteDeck_OnePass_DealsEveryPaletteOnce_ForAnyRandomSource()
        {
            var pool = new[] { 2, 13, 14, 84, 85, 86, 88, 89, 90 };

            var sources = new List<Func<int, int>> { n => 0, n => n - 1 };
            for (var seed = 1; seed <= 40; seed++)
                sources.Add(new Random(seed).Next);

            foreach (var source in sources)
            {
                var deck = new PvpTeamHue.FfaPaletteDeck(pool, source);
                var dealt = Enumerable.Range(0, pool.Length).Select(_ => deck.Next()).ToList();

                CollectionAssert.AreEquivalent(pool, dealt, "one pass over the pool deals each palette exactly once");
            }
        }

        [TestMethod]
        public void FfaPaletteDeck_PastThePool_ReshufflesAndDealsAgain_NeverRepeatingWithinAPass()
        {
            var pool = new[] { 13, 85, 88 };
            var deck = new PvpTeamHue.FfaPaletteDeck(pool, new Random(5).Next);

            var first = Enumerable.Range(0, 3).Select(_ => deck.Next()).ToList();
            var second = Enumerable.Range(0, 3).Select(_ => deck.Next()).ToList();
            var third = deck.Next();

            CollectionAssert.AreEquivalent(pool, first);
            CollectionAssert.AreEquivalent(pool, second);
            Assert.IsTrue(pool.Contains(third));
        }

        [TestMethod]
        public void FfaPaletteDeck_ShufflesTheOrder_NotJustThePoolOrder()
        {
            var pool = Enumerable.Range(1, 12).ToArray();
            var orders = new HashSet<string>();

            for (var seed = 1; seed <= 20; seed++)
            {
                var deck = new PvpTeamHue.FfaPaletteDeck(pool, new Random(seed).Next);
                orders.Add(string.Join(",", Enumerable.Range(0, pool.Length).Select(_ => deck.Next())));
            }

            Assert.IsTrue(orders.Count > 10, $"20 seeds gave {orders.Count} distinct orders: the deal is shuffled");
        }

        [TestMethod]
        public void FfaPaletteDeck_ABadRandomSource_StillDealsAPermutation()
        {
            var pool = new[] { 2, 13, 14, 85 };

            foreach (var source in new Func<int, int>[] { n => -7, n => 9999, n => throw new InvalidOperationException("rng"), null })
            {
                var deck = new PvpTeamHue.FfaPaletteDeck(pool, source);
                CollectionAssert.AreEquivalent(pool, Enumerable.Range(0, 4).Select(_ => deck.Next()).ToList());
            }
        }

        [TestMethod]
        public void FfaPaletteDeck_AnEmptyPool_IsRefused()
        {
            Assert.Throws<ArgumentException>(() => new PvpTeamHue.FfaPaletteDeck(new int[0], n => 0));
            Assert.Throws<ArgumentException>(() => new PvpTeamHue.FfaPaletteDeck(null, n => 0));
        }

        [TestMethod]
        public void FfaSeatDials_ClearsTheOverrides_AndPutsTheSeatsPaletteAndShadeInTheNeutralLook()
        {
            var overrides = new Dictionary<(uint, KitHue), PvpTeamHueLook> { [(CbA, KitHue.Neutral)] = new PvpTeamHueLook(88, 0.4) };
            var dials = Dials(overrides: overrides);
            var seat = PvpTeamHue.FfaSeatDials(dials, 13, 0.6);

            Assert.AreEqual(new PvpTeamHueLook(13, 0.6), seat.Neutral);
            Assert.AreEqual(0, seat.Overrides.Count, "per-team overrides never apply to a Tugak seat");
            Assert.AreEqual(dials.Red, seat.Red);
            Assert.AreEqual(1, dials.Overrides.Count, "the shared dials are not changed");
            Assert.AreEqual(new PvpTeamHueLook(90, 0.0), dials.Neutral, "the shared dials are not changed");
            Assert.AreEqual(1.0, PvpTeamHue.FfaSeatDials(dials, 13, 9.0).Neutral.Shade);
            Assert.AreEqual(0.0, PvpTeamHue.FfaSeatDials(dials, 13, double.NaN).Neutral.Shade);
        }

        /// <summary>Per piece, the Tugak palette goes through the ordinary resolver: a listed palette is applied, an unlisted one is never written and the piece is reported.</summary>
        [TestMethod]
        public void FfaSeat_APieceThatCannotTakeThePalette_KeepsItsOwnLook_AndIsReported()
        {
            var helm = Worn(CbOther, 0x1, 101, "Helm");
            var chest = Worn(CbA, 0x200, 100, "Chest");
            var result = PvpTeamHue.Apply(Definition(chest, helm), KitHue.Neutral, PvpTeamHue.FfaSeatDials(Dials(), 85, 0.3), Valid, TableIcon);

            var dressedChest = result.Definition.Kit.Single(k => k.WeenieClassId == 100);
            var keptHelm = result.Definition.Kit.Single(k => k.WeenieClassId == 101);

            Assert.AreEqual(85, dressedChest.Ints[PaletteKey]);
            Assert.AreEqual(0.3, dressedChest.Floats[ShadeKey]);
            Assert.AreEqual(2, keptHelm.Ints[PaletteKey], "CbOther does not list 85, so the helm keeps its own palette");
            Assert.AreEqual(0.25, keptHelm.Floats[ShadeKey]);
            Assert.AreEqual(1, result.AppliedCount);
            Assert.AreEqual(1, result.Skipped.Count);
            Assert.AreEqual(HueSkipKind.NoValidPalette, result.Skipped[0].Kind);
        }

        [TestMethod]
        public void AuditLines_WithAnFfaPool_ListsWhichPoolPalettesEachPieceCanTake()
        {
            var dials = Dials() with { FfaPalettes = new[] { 2, 13, 85, 90 } };
            var lines = PvpTeamHue.AuditLines(Definition(Worn(CbA, 0x200, 100, "Chest"), Worn(CbOther, 0x1, 101, "Helm")), dials, Valid);

            StringAssert.Contains(lines.Single(l => l.Contains("Chest")), "ffa pool ok [2,85,90] skipped [13]");
            StringAssert.Contains(lines.Single(l => l.Contains("Helm")), "ffa pool ok [2,13] skipped [85,90]");
        }

        [TestMethod]
        public void AuditLines_WithNoFfaPool_AddsNoFfaCell()
        {
            var lines = PvpTeamHue.AuditLines(Definition(Worn(CbA, 0x200, 100, "Chest")), Dials(), Valid);

            Assert.IsFalse(lines.Single().Contains("ffa pool"));
        }

        [TestMethod]
        public void KitHueFor_Tugak_IsTheEmptyPoolFallback_AndTheOtherModesAreUntouchedByThePool()
        {
            // The pool never reaches KitHueFor: with no usable pool Tugak keeps the coin-flipped Red or Blue; every other rule is as before.
            Assert.AreEqual(KitHue.Red, PvpTeamHue.KitHueFor(PvpColourRule.Tugak, 5, PvpTeamColour.Red));
            Assert.AreEqual(KitHue.Blue, PvpTeamHue.KitHueFor(PvpColourRule.Tugak, 5, PvpTeamColour.Blue));
            Assert.AreEqual(KitHue.Red, PvpTeamHue.KitHueFor(PvpColourRule.CoinFlip, 2, PvpTeamColour.Red));
            Assert.AreEqual(KitHue.Blue, PvpTeamHue.KitHueFor(PvpColourRule.AttackDefend, 2, PvpTeamColour.Blue));
            Assert.AreEqual(KitHue.Neutral, PvpTeamHue.KitHueFor(PvpColourRule.CoinFlip, 3, PvpTeamColour.Red));
        }
    }
}
