using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Exercises IconKey.Resolve, mirroring WorldObject_Networking.cs:957-995. Pure: the dat is
    /// injected as an IClothingIconSource, so every test runs in CI.
    /// </summary>
    [TestClass]
    public class IconKeyTests
    {
        private const uint HumanMaleSetup = 0x02000001;
        private const uint OtherSetup = 0x02000002;
        private const uint TestClothingBase = 0x10000123;

        /// <summary>
        /// One-table fake. TemplateKeys is an ordered list so the enumeration-order fallback can be
        /// asserted without depending on Dictionary ordering luck.
        /// </summary>
        private sealed class FakeClothingSource : IClothingIconSource
        {
            private readonly uint id;
            private readonly ClothingIconTable table;

            public FakeClothingSource(uint id, IReadOnlyList<uint> setupIds, IReadOnlyList<uint> templateKeys, IReadOnlyDictionary<uint, uint> iconByTemplate)
            {
                this.id = id;
                this.table = new ClothingIconTable(setupIds, templateKeys, iconByTemplate);
            }

            public ClothingIconTable TryGetTable(uint clothingBase)
            {
                return clothingBase == id ? table : null;
            }
        }

        private static IClothingIconSource TwoTemplateSource()
        {
            // Enumeration order deliberately NOT ascending: template 8 is first, so a fallback that
            // wrongly sorts would pick 2 and fail.
            var keys = new List<uint> { 8, 2 };
            var icons = new Dictionary<uint, uint> { { 8, 0x0600AAAAu }, { 2, 0x0600BBBBu } };
            return new FakeClothingSource(TestClothingBase, new List<uint> { HumanMaleSetup }, keys, icons);
        }

        // ---- key formatting ----

        [TestMethod]
        public void Plain_FormatsAsUppercaseHex8WithPrefix()
        {
            Assert.AreEqual("0x060011F3", IconKey.Plain(0x060011F3u));
        }

        [TestMethod]
        public void Variant_PadsTemplateToTwoDigits()
        {
            Assert.AreEqual("0x0600ABCD_p02", IconKey.Variant(0x0600ABCDu, 2u));
        }

        [TestMethod]
        public void Variant_DoesNotTruncateWideTemplates()
        {
            Assert.AreEqual("0x0600ABCD_p103", IconKey.Variant(0x0600ABCDu, 103u));
        }

        // ---- plain path ----

        [TestMethod]
        public void NoClothingBase_ReturnsPlainKeysForAllThreeLayers()
        {
            var set = IconKey.Resolve(0x06001111u, 0x06002222u, 0x06003333u, null, null, TwoTemplateSource(), HumanMaleSetup, false, false);

            Assert.AreEqual("0x06001111", set.BaseKey);
            Assert.AreEqual("0x06002222", set.OverlayKey);
            Assert.AreEqual("0x06003333", set.UnderlayKey);
            Assert.AreEqual(0x06001111u, set.ResolvedIconId);
            Assert.IsNull(set.ResolvedPaletteTemplate);
        }

        [TestMethod]
        public void NoIcon_ReturnsNullBaseKey()
        {
            var set = IconKey.Resolve(null, null, null, null, null, TwoTemplateSource(), HumanMaleSetup, false, false);

            Assert.IsNull(set.BaseKey);
            Assert.IsNull(set.OverlayKey);
            Assert.IsNull(set.UnderlayKey);
            Assert.IsNull(set.ResolvedIconId);
        }

        // ---- clothing base path ----

        [TestMethod]
        public void ClothingBaseWithTemplate_ReturnsVariantKeyForThatTemplate()
        {
            var set = IconKey.Resolve(0x06001111u, null, null, 2, TestClothingBase, TwoTemplateSource(), HumanMaleSetup, false, false);

            Assert.AreEqual("0x0600BBBB_p02", set.BaseKey);
            Assert.AreEqual(0x0600BBBBu, set.ResolvedIconId);
            Assert.AreEqual(2u, set.ResolvedPaletteTemplate);
        }

        [TestMethod]
        public void ClothingBaseTemplateAbsent_KeysOnTheFirstEnumeratedTemplateNotTheRequestedOne()
        {
            // Template 77 is not in the table. The client falls back to Keys.ElementAt(0), which is 8
            // here, so the key must be _p08 - not _p77, whose file the pack would never write.
            var set = IconKey.Resolve(0x06001111u, null, null, 77, TestClothingBase, TwoTemplateSource(), HumanMaleSetup, false, false);

            Assert.AreEqual("0x0600AAAA_p08", set.BaseKey);
            Assert.AreEqual(8u, set.ResolvedPaletteTemplate);
        }

        [TestMethod]
        public void ClothingBaseShadeOnly_UsesTemplateZeroThenFallsBack()
        {
            // No PaletteTemplate but a Shade: palOption is 0, which is absent, so the first
            // enumerated template wins.
            var set = IconKey.Resolve(0x06001111u, null, null, null, TestClothingBase, TwoTemplateSource(), HumanMaleSetup, true, false);

            Assert.AreEqual("0x0600AAAA_p08", set.BaseKey);
        }

        // ---- every gate that must fall back to the plain key ----

        [TestMethod]
        public void ClothingBaseWithNeitherShadeNorTemplate_ReturnsPlainKey()
        {
            var set = IconKey.Resolve(0x06001111u, null, null, null, TestClothingBase, TwoTemplateSource(), HumanMaleSetup, false, false);

            Assert.AreEqual("0x06001111", set.BaseKey);
            Assert.IsNull(set.ResolvedPaletteTemplate);
        }

        [TestMethod]
        public void SetupNotInClothingBaseEffects_ReturnsPlainKey()
        {
            var set = IconKey.Resolve(0x06001111u, null, null, 2, TestClothingBase, TwoTemplateSource(), OtherSetup, false, false);

            Assert.AreEqual("0x06001111", set.BaseKey);
        }

        [TestMethod]
        public void UnknownClothingBase_ReturnsPlainKey()
        {
            var set = IconKey.Resolve(0x06001111u, null, null, 2, 0x10009999u, TwoTemplateSource(), HumanMaleSetup, false, false);

            Assert.AreEqual("0x06001111", set.BaseKey);
        }

        [TestMethod]
        public void IgnoreCloIcons_ReturnsPlainKey()
        {
            var set = IconKey.Resolve(0x06001111u, null, null, 2, TestClothingBase, TwoTemplateSource(), HumanMaleSetup, false, true);

            Assert.AreEqual("0x06001111", set.BaseKey);
        }

        [TestMethod]
        public void ZeroEffectIcon_ReturnsPlainKey()
        {
            var keys = new List<uint> { 2 };
            var icons = new Dictionary<uint, uint> { { 2, 0u } };
            var source = new FakeClothingSource(TestClothingBase, new List<uint> { HumanMaleSetup }, keys, icons);

            var set = IconKey.Resolve(0x06001111u, null, null, 2, TestClothingBase, source, HumanMaleSetup, false, false);

            Assert.AreEqual("0x06001111", set.BaseKey);
        }

        [TestMethod]
        public void EmptySubPalEffects_ReturnsPlainKey()
        {
            var source = new FakeClothingSource(TestClothingBase, new List<uint> { HumanMaleSetup }, new List<uint>(), new Dictionary<uint, uint>());

            var set = IconKey.Resolve(0x06001111u, null, null, 2, TestClothingBase, source, HumanMaleSetup, false, false);

            Assert.AreEqual("0x06001111", set.BaseKey);
        }

        // ---- layers are independent ----

        [TestMethod]
        public void OverlayAndUnderlayAreNeverPaletteResolved()
        {
            var set = IconKey.Resolve(0x06001111u, 0x06002222u, 0x06003333u, 2, TestClothingBase, TwoTemplateSource(), HumanMaleSetup, false, false);

            Assert.AreEqual("0x0600BBBB_p02", set.BaseKey);
            Assert.AreEqual("0x06002222", set.OverlayKey);
            Assert.AreEqual("0x06003333", set.UnderlayKey);
        }
    }
}
