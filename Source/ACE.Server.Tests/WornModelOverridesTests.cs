using System.Collections.Generic;
using System.Linq;

using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers WornModelOverrides.Parse, the pure parser for PropertyString.WornModelOverrides.
    /// </summary>
    [TestClass]
    public class WornModelOverridesTests
    {
        [TestMethod]
        public void Parse_NullEmptyWhitespace_ReturnsEmpty()
        {
            Assert.AreEqual(0, WornModelOverrides.Parse(null).Count);
            Assert.AreEqual(0, WornModelOverrides.Parse(string.Empty).Count);
            Assert.AreEqual(0, WornModelOverrides.Parse("   ").Count);
        }

        [TestMethod]
        public void Parse_TwoPairs_ReturnsBoth()
        {
            var result = WornModelOverrides.Parse("0:0x01003F99,1:0x01003F9A");

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(0x01003F99u, result[0]);
            Assert.AreEqual(0x01003F9Au, result[1]);
        }

        [TestMethod]
        public void Parse_WhitespaceAndCase_AreTolerated()
        {
            var result = WornModelOverrides.Parse(" 10 : 0X01003f9f ,  13:01003FA2 ");

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(0x01003F9Fu, result[10]);
            Assert.AreEqual(0x01003FA2u, result[13]);
        }

        [TestMethod]
        public void Parse_MalformedPairs_AreIgnored()
        {
            var result = WornModelOverrides.Parse("0:0x01003F99,garbage,1:,:0x01003F9A,2:0x01003F9B:9,3-0x01003F9C,x:0x01003F9D,4:0xZZ,5:0x01003F9E");

            CollectionAssert.AreEqual(new byte[] { 0, 5 }, result.Keys.ToArray());
            Assert.AreEqual(0x01003F99u, result[0]);
            Assert.AreEqual(0x01003F9Eu, result[5]);
        }

        [TestMethod]
        public void Parse_PartAbove255OrNegative_IsIgnored()
        {
            var result = WornModelOverrides.Parse("256:0x01003F99,-1:0x01003F99,255:0x01003F99");

            CollectionAssert.AreEqual(new byte[] { 255 }, result.Keys.ToArray());
        }

        [TestMethod]
        public void Parse_IdOutsideGfxObjRange_IsIgnored()
        {
            // 0x00FFFFFF is below the GfxObj range, 0x02000001 is a Setup, 0x05002581 a SurfaceTexture.
            var result = WornModelOverrides.Parse("0:0x00FFFFFF,1:0x02000001,2:0x05002581,3:0x01000000,4:0x01FFFFFF");

            CollectionAssert.AreEqual(new byte[] { 3, 4 }, result.Keys.ToArray());
            Assert.AreEqual(0x01000000u, result[3]);
            Assert.AreEqual(0x01FFFFFFu, result[4]);
        }

        [TestMethod]
        public void Parse_RepeatedPart_LastPairWins()
        {
            var result = WornModelOverrides.Parse("7:0x01003F99,7:0x010001EC");

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(0x010001ECu, result[7]);
        }

        [TestMethod]
        public void Applicable_DropsExcludedPartsAndMissingModels()
        {
            var parsed = WornModelOverrides.Parse("0:0x01003F99,1:0x01FFFFF0,16:0x01003FA5");
            var missing = new List<(byte Part, uint Model)>();

            var result = WornModelOverrides.Applicable(parsed, new HashSet<byte> { 16 }, id => id != 0x01FFFFF0, (part, id) => missing.Add((part, id)));

            CollectionAssert.AreEqual(new byte[] { 0 }, result.Keys.ToArray());
            Assert.AreEqual(1, missing.Count, "only the missing model is reported; an excluded part is not a missing model");
            Assert.AreEqual(((byte)1, 0x01FFFFF0u), missing[0]);
        }

        [TestMethod]
        public void Applicable_AllExcludedOrMissing_ReturnsEmpty()
        {
            // The case the no-ClothingBase path must fall back on: nothing applicable, so the item
            // takes the old AddSetupAsClothingBase path instead of rendering nothing.
            var parsed = WornModelOverrides.Parse("16:0x01003FA5,2:0x01FFFFF0");

            var result = WornModelOverrides.Applicable(parsed, new HashSet<byte> { 16 }, id => id != 0x01FFFFF0);

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Applicable_NullInputs_AreSafe()
        {
            Assert.AreEqual(0, WornModelOverrides.Applicable(null, null, id => true).Count);

            var result = WornModelOverrides.Applicable(WornModelOverrides.Parse("3:0x010001EC"), null, id => true);
            CollectionAssert.AreEqual(new byte[] { 3 }, result.Keys.ToArray());
        }

        [TestMethod]
        public void Parse_ResultIsOrderedByPart()
        {
            var result = WornModelOverrides.Parse("15:0x01003FA4,0:0x01003F99,9:0x01003F9E");

            CollectionAssert.AreEqual(new byte[] { 0, 9, 15 }, result.Keys.ToArray());
        }
    }
}
