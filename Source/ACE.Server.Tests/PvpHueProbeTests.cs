using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests
{
    /// <summary>Pure rules behind /pvphue: palette acceptance, the common-palette intersection, remember-once.</summary>
    [TestClass]
    public class PvpHueProbeTests
    {
        private static HueItem Item(uint guid, uint? cb, params uint[] palettes) =>
            new HueItem(guid, "item" + guid, cb, palettes);

        [TestMethod]
        public void Classify_UnsupportedPalette_IsSkippedNotApplied()
        {
            var item = Item(1, 0x10000001, 3, 5, 7);
            Assert.AreEqual(HueSkipReason.UnsupportedPalette, PvpHueProbe.Classify(item, 4));
            Assert.AreEqual(HueSkipReason.None, PvpHueProbe.Classify(item, 5));
            Assert.AreEqual(HueSkipReason.UnsupportedPalette, PvpHueProbe.Classify(item, -1));
        }

        [TestMethod]
        public void Classify_NoClothingBaseOrNoPalettes_AreDistinctSkips()
        {
            Assert.AreEqual(HueSkipReason.NoClothingBase, PvpHueProbe.Classify(Item(1, null, 3), 3));
            Assert.AreEqual(HueSkipReason.NoClothingBase, PvpHueProbe.Classify(Item(2, 0, 3), 3));
            Assert.AreEqual(HueSkipReason.NoSubPalettes, PvpHueProbe.Classify(Item(3, 0x10000002), 3));
        }

        [TestMethod]
        public void CommonPalettes_IsSortedIntersectionOfRecolourableItemsOnly()
        {
            var items = new[]
            {
                Item(1, 0x10000001, 9, 3, 5, 7),
                Item(2, 0x10000002, 5, 7, 9, 11),
                Item(3, null, 100),            // no ClothingBase: must not constrain the result
                Item(4, 0x10000004),           // no sub-palettes: must not constrain the result
            };

            CollectionAssert.AreEqual(new uint[] { 5, 7, 9 }, PvpHueProbe.CommonPalettes(items));
        }

        [TestMethod]
        public void CommonPalettes_NoRecolourableItem_IsEmpty()
        {
            Assert.AreEqual(0, PvpHueProbe.CommonPalettes(new[] { Item(1, null) }).Count);
        }

        [TestMethod]
        public void Unrecolourable_ListsOnlyTheItemsThatCannotBeRecoloured()
        {
            var items = new[] { Item(1, 0x10000001, 3), Item(2, null), Item(3, 0x10000003) };
            var guids = PvpHueProbe.Unrecolourable(items).ConvertAll(i => i.Guid);
            CollectionAssert.AreEqual(new uint[] { 2, 3 }, guids);
        }

        [TestMethod]
        public void Memory_KeepsTheFirstOriginalOnly()
        {
            var mem = new HueMemory();
            Assert.IsTrue(mem.Remember(10, new HueOriginal(1, 2, 0.5)));
            Assert.IsFalse(mem.Remember(10, new HueOriginal(99, 98, 0.1)));

            Assert.IsTrue(mem.TryGet(10, out var o));
            Assert.AreEqual(2, o.PaletteTemplate);
            Assert.AreEqual(0.5, o.Shade);
            Assert.AreEqual(1u, o.Icon);
        }

        [DataTestMethod]
        [DataRow("-1")]
        [DataRow("abc")]
        public void TryParseArgs_BadPalette_IsRejected(string palette)
        {
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { palette }, out _));
        }

        [DataTestMethod]
        [DataRow("1.5")]
        [DataRow("-0.1")]
        [DataRow("NaN")]
        [DataRow("Infinity")]
        [DataRow("0,5")]
        [DataRow("x")]
        public void TryParseArgs_BadShade_IsRejected(string shade)
        {
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "85", shade }, out _));
        }

        [TestMethod]
        public void TryParseArgs_ResetOrListWithExtraArgument_IsRejected()
        {
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "reset", "0.5" }, out _));
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "list", "1" }, out _));
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new string[0], out _));
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "1", "0.5", "x" }, out _));
        }

        [TestMethod]
        public void TryParseArgs_ValidForms_Parse()
        {
            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "85", "0.3" }, out var c));
            Assert.AreEqual(HueCommandKind.Apply, c.Kind);
            Assert.AreEqual(85, c.Palette);
            Assert.AreEqual(0.3, c.Shade);

            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "85" }, out c));
            Assert.IsNull(c.Shade);

            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "RESET" }, out c));
            Assert.AreEqual(HueCommandKind.Reset, c.Kind);

            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "list" }, out c));
            Assert.AreEqual(HueCommandKind.List, c.Kind);

            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "5", "1" }, out c));
            Assert.AreEqual(1.0, c.Shade);
        }

        [TestMethod]
        public void TryParseArgs_Audit_TakesNoFurtherArgument()
        {
            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "audit" }, out var c));
            Assert.AreEqual(HueCommandKind.Audit, c.Kind);

            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "AUDIT" }, out c));
            Assert.AreEqual(HueCommandKind.Audit, c.Kind);

            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "audit", "duelist" }, out _));
        }

        [TestMethod]
        public void TryParseArgs_Suit_ParsesTheKeyAndEachHue()
        {
            foreach (var (word, hue) in new[] { ("red", KitHue.Red), ("blue", KitHue.Blue), ("neutral", KitHue.Neutral), ("RED", KitHue.Red) })
            {
                Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "suit", "Duelist", word }, out var c), word);
                Assert.AreEqual(HueCommandKind.Suit, c.Kind);
                Assert.AreEqual("duelist", c.TemplateKey, "the key is lowercased");
                Assert.AreEqual(hue, c.Hue);
            }
        }

        [TestMethod]
        public void TryParseArgs_SuitOwn_HasNoHue()
        {
            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "suit", "mage", "own" }, out var c));
            Assert.AreEqual(HueCommandKind.Suit, c.Kind);
            Assert.AreEqual("mage", c.TemplateKey);
            Assert.IsNull(c.Hue);
        }

        [TestMethod]
        public void TryParseArgs_SuitBadForms_AreRejected()
        {
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit" }, out _));
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "duelist" }, out _), "no hue");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "duelist", "green" }, out _), "not a hue");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "duelist", "red", "extra" }, out _));
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", " ", "red" }, out _), "blank key");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "red" }, out _), "a hue is not a key");
        }

        [TestMethod]
        public void TryParseArgs_SuitNumericPalette_ParsesPaletteAndOptionalShade_WithNoHue()
        {
            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "suit", "Light", "13", "0.2" }, out var c));
            Assert.AreEqual(HueCommandKind.Suit, c.Kind);
            Assert.AreEqual("light", c.TemplateKey);
            Assert.IsTrue(c.NumericPalette);
            Assert.AreEqual(13, c.Palette);
            Assert.AreEqual(0.2, c.Shade);
            Assert.IsNull(c.Hue, "a numeric palette is not a team hue");

            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "84" }, out c));
            Assert.IsTrue(c.NumericPalette);
            Assert.AreEqual(84, c.Palette);
            Assert.IsNull(c.Shade, "no shade given");

            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "0", "1" }, out c));
            Assert.AreEqual(0, c.Palette);
            Assert.AreEqual(1.0, c.Shade);
        }

        [TestMethod]
        public void TryParseArgs_SuitHueWords_AreNotNumericPalettes()
        {
            foreach (var word in new[] { "red", "blue", "neutral", "own" })
            {
                Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "suit", "light", word }, out var c), word);
                Assert.IsFalse(c.NumericPalette, word);
            }
        }

        [TestMethod]
        public void TryParseArgs_SuitNumericBadForms_AreRejected()
        {
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "-1" }, out _), "negative palette");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "13", "1.5" }, out _), "shade above 1");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "13", "-0.1" }, out _), "shade below 0");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "13", "x" }, out _), "shade not a number");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "13", "NaN" }, out _), "shade NaN");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "13", "0.2", "x" }, out _), "too many arguments");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "own", "0.2" }, out _), "a shade only follows a palette");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "red", "0.2" }, out _), "a shade only follows a palette");
            Assert.IsFalse(PvpHueProbe.TryParseArgs(new[] { "suit", "light", "13.5" }, out _), "palette must be an integer");
        }

        [TestMethod]
        public void TryParseArgs_SuitAndAudit_DoNotDisturbTheOtherForms()
        {
            Assert.IsTrue(PvpHueProbe.TryParseArgs(new[] { "85", "0.3" }, out var c));
            Assert.AreEqual(HueCommandKind.Apply, c.Kind);
            Assert.IsNull(c.TemplateKey);
            Assert.IsNull(c.Hue);
        }

        [TestMethod]
        public void RestoreRemoves_AbsentOriginalRemoves_ZeroOriginalWrites()
        {
            var mem = new HueMemory();
            mem.Remember(5, new HueOriginal(null, null, null));
            Assert.IsTrue(mem.TryGet(5, out var o));
            Assert.IsTrue(PvpHueProbe.RestoreRemoves(o.Icon));
            Assert.IsTrue(PvpHueProbe.RestoreRemoves(o.PaletteTemplate));
            Assert.IsTrue(PvpHueProbe.RestoreRemoves(o.Shade));

            // a real original of zero is a value to write back, not an absence
            Assert.IsFalse(PvpHueProbe.RestoreRemoves<double>(0.0));
            Assert.IsFalse(PvpHueProbe.RestoreRemoves<int>(0));
        }

        [TestMethod]
        public void TryParseSlots_MapsNamesToEquipMaskBits_AndRejectsUnknown()
        {
            Assert.IsTrue(PvpHueProbe.TryParseSlots("helm, Chest,sollerets", out var masks, out _));
            CollectionAssert.AreEqual(new[] { 1, 512, 256 }, masks);

            Assert.IsTrue(PvpHueProbe.TryParseSlots("pauldrons,bracers,gauntlets,girth,tassets,greaves", out masks, out _));
            CollectionAssert.AreEqual(new[] { 2048, 4096, 32, 1024, 8192, 16384 }, masks);

            Assert.IsFalse(PvpHueProbe.TryParseSlots("helm,cape", out _, out var bad));
            Assert.AreEqual("cape", bad);
            Assert.IsFalse(PvpHueProbe.TryParseSlots("", out _, out _));
            Assert.IsFalse(PvpHueProbe.TryParseSlots("helm,,chest", out _, out _));
        }

        [TestMethod]
        public void SlotName_IsTheInverseOfTheMaskTable()
        {
            Assert.AreEqual("greaves", PvpHueProbe.SlotName(16384));
            Assert.AreEqual("gauntlets", PvpHueProbe.SlotName(32));
            Assert.IsNull(PvpHueProbe.SlotName(0x80));
        }

        [TestMethod]
        public void ValidateLookSource_RefusesMissingClothingBaseAndRawRows()
        {
            Assert.IsNull(PvpHueProbe.ValidateLookSource(new LookFields { ClothingBase = 0x10000001 }, false, false, false));
            Assert.IsNotNull(PvpHueProbe.ValidateLookSource(new LookFields(), false, false, false));
            Assert.IsNotNull(PvpHueProbe.ValidateLookSource(new LookFields { ClothingBase = 0 }, false, false, false));

            var msg = PvpHueProbe.ValidateLookSource(new LookFields { ClothingBase = 0x10000001 }, true, false, true);
            StringAssert.Contains(msg, "palette");
            StringAssert.Contains(msg, "anim-part");
            Assert.IsFalse(msg.Contains("texture-map"));
        }

        [TestMethod]
        public void SelectLookTargets_DefaultFollowsSourceCoverage()
        {
            var worn = new[]
            {
                new HueWorn(1, "legs", 8192),    // tassets
                new HueWorn(2, "greaves", 16384),
                new HueWorn(3, "helm", 1),
                new HueWorn(4, "loose", null),
            };

            var sel = PvpHueProbe.SelectLookTargets(worn, 1024 | 8192 | 16384, null);
            CollectionAssert.AreEqual(new uint[] { 1, 2 }, sel.Chosen);
            Assert.AreEqual(2, sel.Skipped.Count);
        }

        [TestMethod]
        public void SelectLookTargets_ExplicitSlotsOverrideSourceCoverage()
        {
            var worn = new[] { new HueWorn(1, "legs", 8192), new HueWorn(3, "helm", 1) };

            // the source covers only the legs, but the explicit list asks for the helm
            var sel = PvpHueProbe.SelectLookTargets(worn, 8192, new[] { 1 });
            CollectionAssert.AreEqual(new uint[] { 3 }, sel.Chosen);
            Assert.AreEqual(1u, sel.Skipped[0].Key);
        }

        [TestMethod]
        public void PlanLook_CopiesSourceFieldsAndRemovesTheOnesTheSourceLacks()
        {
            var src = new LookFields
            {
                Setup = 11, ClothingBase = 22, PaletteBase = 33, PaletteTemplate = 85, Icon = 99,
                Shade = 0.3, Shade2 = 0.4, Shade3 = 0.5, Shade4 = 0.6,
            };
            var plan = PvpHueProbe.PlanLook(src, 0x06000001);

            Assert.AreEqual(22u, plan.Target.ClothingBase);
            Assert.AreEqual(11u, plan.Target.Setup);
            Assert.AreEqual(33u, plan.Target.PaletteBase);
            Assert.AreEqual(85, plan.Target.PaletteTemplate);
            Assert.AreEqual(0.3, plan.Target.Shade);
            Assert.AreEqual(0.4, plan.Target.Shade2);
            Assert.AreEqual(0.5, plan.Target.Shade3);
            Assert.AreEqual(0.6, plan.Target.Shade4);
            Assert.AreEqual(0x06000001u, plan.Target.Icon); // table icon wins
            Assert.IsFalse(plan.KeepSetup);
            Assert.IsFalse(plan.KeepIcon);
        }

        [TestMethod]
        public void PlanLook_AbsentSourceFieldsPlanRemoval()
        {
            var plan = PvpHueProbe.PlanLook(new LookFields { Setup = 11, ClothingBase = 22 }, 0x06000001);

            Assert.IsNull(plan.Target.PaletteBase);
            Assert.IsNull(plan.Target.PaletteTemplate);
            Assert.IsNull(plan.Target.Shade);
            Assert.IsNull(plan.Target.Shade2);
            Assert.IsNull(plan.Target.Shade3);
            Assert.IsNull(plan.Target.Shade4);
            Assert.IsNull(plan.Target.IgnoreCloIcons);
        }

        [TestMethod]
        public void PlanLook_SourceIgnoringCloIcons_WritesSourceIconAndSetsFlag()
        {
            var plan = PvpHueProbe.PlanLook(new LookFields { ClothingBase = 22, Icon = 99, IgnoreCloIcons = true }, 0x06000001);

            Assert.AreEqual(99u, plan.Target.Icon);          // not the table icon
            Assert.AreEqual(true, plan.Target.IgnoreCloIcons);

            // flag with no source icon: keep the item's own icon rather than writing the table's
            plan = PvpHueProbe.PlanLook(new LookFields { ClothingBase = 22, IgnoreCloIcons = true }, 0x06000001);
            Assert.IsTrue(plan.KeepIcon);
        }

        [TestMethod]
        public void PlanLook_SourceNotIgnoringCloIcons_UsesTableIconAndRemovesFlag()
        {
            foreach (bool? flag in new bool?[] { false, null })
            {
                var plan = PvpHueProbe.PlanLook(new LookFields { ClothingBase = 22, Icon = 99, IgnoreCloIcons = flag }, 0x06000001);
                Assert.AreEqual(0x06000001u, plan.Target.Icon);
                Assert.IsNull(plan.Target.IgnoreCloIcons);
            }
        }

        [TestMethod]
        public void Memory_RemembersIgnoreCloIconsWithAbsentMeansRemove()
        {
            var mem = new HueMemory();
            mem.Remember(1, new HueOriginal(null, null, null, ignoreCloIcons: true));
            mem.Remember(2, new HueOriginal(null, null, null));
            mem.TryGet(1, out var a);
            mem.TryGet(2, out var b);
            Assert.AreEqual(true, a.IgnoreCloIcons);
            Assert.IsFalse(PvpHueProbe.RestoreRemoves(a.IgnoreCloIcons));
            Assert.IsTrue(PvpHueProbe.RestoreRemoves(b.IgnoreCloIcons));
        }

        [TestMethod]
        public void SelectLookTargets_SubsetRule_SkipsItemsSpanningOutsideCoverage()
        {
            var worn = new[]
            {
                new HueWorn(1, "chest+girth", 0x200 | 0x400),
                new HueWorn(2, "girth", 0x400),
            };

            // source covers 0x6400 = girth + tassets + greaves: the chest+girth item spills outside it
            var sel = PvpHueProbe.SelectLookTargets(worn, 0x6400, null);
            CollectionAssert.AreEqual(new uint[] { 2 }, sel.Chosen);
            Assert.AreEqual(1u, sel.Skipped[0].Key);
            Assert.AreEqual("spans slots outside the source's coverage", sel.Skipped[0].Value);

            // explicit list: the same subset rule against the OR of the named slots
            sel = PvpHueProbe.SelectLookTargets(worn, 0, new[] { 0x400 });
            CollectionAssert.AreEqual(new uint[] { 2 }, sel.Chosen);
            sel = PvpHueProbe.SelectLookTargets(worn, 0, new[] { 0x200, 0x400 });
            CollectionAssert.AreEqual(new uint[] { 1, 2 }, sel.Chosen);
        }

        [TestMethod]
        public void SelectLookTargets_SkipsItemsWithOwnPaletteRows()
        {
            var worn = new[] { new HueWorn(1, "owned", 0x400, hasOwnRows: true), new HueWorn(2, "plain", 0x400) };
            var sel = PvpHueProbe.SelectLookTargets(worn, 0x400, null);

            CollectionAssert.AreEqual(new uint[] { 2 }, sel.Chosen);
            Assert.AreEqual("has its own palette/texture/anim rows", sel.Skipped[0].Value);
        }

        [TestMethod]
        public void PlanLook_IconFallsBackToSourceIconThenKeeps_AndSetupIsKeptWhenSourceHasNone()
        {
            var plan = PvpHueProbe.PlanLook(new LookFields { ClothingBase = 22, Icon = 99 }, 0);
            Assert.AreEqual(99u, plan.Target.Icon);
            Assert.IsTrue(plan.KeepSetup);

            plan = PvpHueProbe.PlanLook(new LookFields { ClothingBase = 22, Setup = 5 }, 0);
            Assert.IsTrue(plan.KeepIcon);
        }

        [TestMethod]
        public void Memory_RoundTripKeepsLookFieldsThroughHueAfterLook()
        {
            var mem = new HueMemory();
            var original = new HueOriginal(1, null, null, setup: 10, clothingBase: 20, paletteBase: null, shade2: 0.4);

            // look remembers first; a later hue must not replace the true original
            Assert.IsTrue(mem.Remember(7, original));
            Assert.IsFalse(mem.Remember(7, new HueOriginal(2, 3, 0.9, setup: 77, clothingBase: 88)));

            Assert.IsTrue(mem.TryGet(7, out var o));
            Assert.AreEqual(10u, o.Setup);
            Assert.AreEqual(20u, o.ClothingBase);
            Assert.IsTrue(PvpHueProbe.RestoreRemoves(o.PaletteBase));
            Assert.IsTrue(PvpHueProbe.RestoreRemoves(o.PaletteTemplate));
            Assert.AreEqual(0.4, o.Shade2);
            Assert.IsTrue(PvpHueProbe.RestoreRemoves(o.Shade3));
        }

        [TestMethod]
        public void ShouldWriteIcon_NeedsNonZeroIconAndNoIgnoreFlag()
        {
            Assert.IsTrue(PvpHueProbe.ShouldWriteIcon(0x06001234, null));
            Assert.IsTrue(PvpHueProbe.ShouldWriteIcon(0x06001234, false));
            Assert.IsFalse(PvpHueProbe.ShouldWriteIcon(0x06001234, true));
            Assert.IsFalse(PvpHueProbe.ShouldWriteIcon(0, null));
        }

        [TestMethod]
        public void Memory_ForgetClearsSoNextChangeIsRemembered()
        {
            var mem = new HueMemory();
            mem.Remember(10, new HueOriginal(1, 2, 0.5));
            Assert.IsTrue(mem.Forget(10));
            Assert.IsFalse(mem.TryGet(10, out _));
            Assert.IsTrue(mem.Remember(10, new HueOriginal(7, 8, 0.9)));
            Assert.AreEqual(1, mem.Count);
        }
    }
}
