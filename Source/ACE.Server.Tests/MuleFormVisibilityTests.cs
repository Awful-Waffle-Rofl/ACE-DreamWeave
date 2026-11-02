using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The byte-level texel scanners and the mesh-free verdicts behind the "draws nothing" gate, over
    /// synthetic bytes. No dat file is needed, which matters: the dat-backed path cannot run in a
    /// unit test host, so these are the only pins on the alpha decoding.
    /// </summary>
    [TestClass]
    public class MuleFormVisibilityTests
    {
        private static byte[] Dxt1Block(ushort c0, ushort c1, uint lookup)
        {
            var bytes = new byte[8];
            BitConverter.GetBytes(c0).CopyTo(bytes, 0);
            BitConverter.GetBytes(c1).CopyTo(bytes, 2);
            BitConverter.GetBytes(lookup).CopyTo(bytes, 4);
            return bytes;
        }

        // ---------------- DXT1 ----------------

        [TestMethod]
        public void Dxt1_OpaqueBlock_IsVisible_EvenWhenEveryTexelUsesIndex3()
        {
            // c0 > c1 is 4-colour mode: index 3 is a real colour, never transparent.
            Assert.IsTrue(MuleFormVisibility.Dxt1HasVisibleTexel(Dxt1Block(0x0002, 0x0001, 0xFFFFFFFF), 4, 4));
        }

        [TestMethod]
        public void Dxt1_PunchThroughBlock_WithEveryTexelIndex3_IsInvisible()
        {
            // The Dark Sarcophagus quad: c0 = 0, c1 = 1, lookup all ones.
            Assert.IsFalse(MuleFormVisibility.Dxt1HasVisibleTexel(Dxt1Block(0x0000, 0x0001, 0xFFFFFFFF), 4, 4));
        }

        [TestMethod]
        public void Dxt1_PunchThroughBlock_WithOneNonIndex3Texel_IsVisible()
        {
            Assert.IsTrue(MuleFormVisibility.Dxt1HasVisibleTexel(Dxt1Block(0x0000, 0x0001, 0xFFFFFFFC), 4, 4));
        }

        [TestMethod]
        public void Dxt1_EqualColours_AreStillPunchThroughMode()
        {
            // c0 <= c1 includes equality.
            Assert.IsFalse(MuleFormVisibility.Dxt1HasVisibleTexel(Dxt1Block(0x0005, 0x0005, 0xFFFFFFFF), 4, 4));
        }

        [TestMethod]
        public void Dxt1_PaddingTexelsPastTheImageEdgeDoNotCount()
        {
            // A 2x2 image in one 4x4 block: only texels (0,0) (1,0) (0,1) (1,1) are real. They are all
            // index 3 (0x0F0F); every padding texel is index 0 and must be ignored.
            Assert.IsFalse(MuleFormVisibility.Dxt1HasVisibleTexel(Dxt1Block(0x0000, 0x0001, 0x00000F0F), 2, 2));
        }

        [TestMethod]
        public void Dxt1_ARealTexelInAPartialBlock_IsVisible()
        {
            // Same 2x2 image, but texel (1,1) (bit pair 5) is index 0.
            Assert.IsTrue(MuleFormVisibility.Dxt1HasVisibleTexel(Dxt1Block(0x0000, 0x0001, 0x0000030F), 2, 2));
        }

        [TestMethod]
        public void Dxt1_AShortBuffer_IsUnknown_SoVisible()
        {
            Assert.IsTrue(MuleFormVisibility.Dxt1HasVisibleTexel(new byte[4], 4, 4));
        }

        // ---------------- indexed ----------------

        private static readonly uint[] Palette = { 0x00000000u, 0xFF000000u };

        private static byte[] Wide(params short[] indices)
        {
            var bytes = new byte[indices.Length * 2];
            for (var i = 0; i < indices.Length; i++)
                BitConverter.GetBytes(indices[i]).CopyTo(bytes, i * 2);
            return bytes;
        }

        [TestMethod]
        public void Index16_EveryTexelOnATransparentEntry_IsInvisible()
        {
            Assert.IsFalse(MuleFormVisibility.IndexedHasVisibleTexel(Wide(0, 0, 0, 0), 4, true, Palette));
        }

        [TestMethod]
        public void Index16_OneTexelOnAnOpaqueEntry_IsVisible()
        {
            Assert.IsTrue(MuleFormVisibility.IndexedHasVisibleTexel(Wide(0, 0, 1, 0), 4, true, Palette));
        }

        [TestMethod]
        public void Index16_OutOfRangeAndNegativeIndices_AreTransparent()
        {
            Assert.IsFalse(MuleFormVisibility.IndexedHasVisibleTexel(Wide(5, -1, 2, 99), 4, true, Palette));
        }

        [TestMethod]
        public void P8_UsesOneByteIndices()
        {
            Assert.IsFalse(MuleFormVisibility.IndexedHasVisibleTexel(new byte[] { 0, 0, 7, 200 }, 4, false, Palette));
            Assert.IsTrue(MuleFormVisibility.IndexedHasVisibleTexel(new byte[] { 0, 0, 7, 1 }, 4, false, Palette));
        }

        [TestMethod]
        public void Indexed_AShortBuffer_IsUnknown_SoVisible()
        {
            Assert.IsTrue(MuleFormVisibility.IndexedHasVisibleTexel(new byte[2], 4, true, Palette));
        }

        // ---------------- A8R8G8B8 ----------------

        [TestMethod]
        public void Argb8_AllAlphaZero_IsInvisible()
        {
            // Colour bytes are non-zero on purpose: only the alpha byte (index 3 of each texel) counts.
            var data = new byte[] { 9, 9, 9, 0, 9, 9, 9, 0, 9, 9, 9, 0, 9, 9, 9, 0 };

            Assert.IsFalse(MuleFormVisibility.Argb8HasVisibleTexel(data, 4));
        }

        [TestMethod]
        public void Argb8_OneNonZeroAlpha_IsVisible()
        {
            var data = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };

            Assert.IsTrue(MuleFormVisibility.Argb8HasVisibleTexel(data, 4));
        }

        [TestMethod]
        public void Argb8_AShortBuffer_IsUnknown_SoVisible()
        {
            Assert.IsTrue(MuleFormVisibility.Argb8HasVisibleTexel(new byte[4], 4));
        }

        // ---------------- mesh-free verdicts ----------------

        [TestMethod]
        public void NoParts_WithABakedScript_Draws()
        {
            Assert.IsTrue(MuleFormVisibility.TryDecideWithoutMesh(0, 0x33000001, out var verdict));
            Assert.AreEqual(false, verdict, "the script is checked first: it plays whatever the mesh looks like");
        }

        [TestMethod]
        public void NoParts_WithNoScript_IsUnknown_NotDrawsNothing()
        {
            // TryGetSetup's fallback for a missing or invalid id is an empty model. That must be
            // unknown (null: never cached, fails open), not a memoized "draws nothing".
            Assert.IsTrue(MuleFormVisibility.TryDecideWithoutMesh(0, 0, out var verdict));
            Assert.IsNull(verdict);
        }

        [TestMethod]
        public void SomeParts_NoScript_NeedsTheMesh()
        {
            Assert.IsFalse(MuleFormVisibility.TryDecideWithoutMesh(17, 0, out _));
        }

        [TestMethod]
        public void SomeParts_WithAScript_Draws()
        {
            Assert.IsTrue(MuleFormVisibility.TryDecideWithoutMesh(17, 0x33000001, out var verdict));
            Assert.AreEqual(false, verdict);
        }
    }
}
