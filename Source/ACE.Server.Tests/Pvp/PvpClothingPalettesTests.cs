using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The ClothingTable palette cache behind team hue: only a successful read is cached, so one early failure (dat not loaded yet, a
    /// throw) cannot leave every later match undressed until restart. The reader is swapped and put back in a finally.
    /// </summary>
    [TestClass]
    public class PvpClothingPalettesTests
    {
        private const uint Cb = 0x1000018E;

        private static void With(Func<uint, IReadOnlyDictionary<uint, uint>> reader, Action body)
        {
            var original = PvpClothingPalettes.TableReader;

            try
            {
                PvpClothingPalettes.ClearCache();
                PvpClothingPalettes.TableReader = reader;
                body();
            }
            finally
            {
                PvpClothingPalettes.TableReader = original;
                PvpClothingPalettes.ClearCache();
            }
        }

        [TestMethod]
        public void AThrowingRead_IsNotCached_AndIsRetriedOnTheNextCall()
        {
            var calls = 0;

            With(id =>
            {
                calls++;

                if (calls == 1)
                    throw new InvalidOperationException("dat not ready");

                return new Dictionary<uint, uint> { [85] = 0x06000055, [88] = 0x06000058 };
            }, () =>
            {
                Assert.IsNull(PvpClothingPalettes.Palettes(Cb), "the failed read reports no palettes");
                Assert.AreEqual(1, calls);

                CollectionAssert.AreEquivalent(new uint[] { 85, 88 }, new List<uint>(PvpClothingPalettes.Palettes(Cb)), "the next call retries and succeeds");
                Assert.AreEqual(2, calls);
                Assert.AreEqual(0x06000058u, PvpClothingPalettes.Icon(Cb, 88));
            });
        }

        [TestMethod]
        public void ANullRead_IsNotCached_AndIsRetriedOnTheNextCall()
        {
            var calls = 0;

            With(id => ++calls <= 2 ? null : new Dictionary<uint, uint> { [85] = 1 }, () =>
            {
                Assert.IsNull(PvpClothingPalettes.Palettes(Cb));
                Assert.AreEqual(0u, PvpClothingPalettes.Icon(Cb, 85), "an unreadable table has no icons (second failed read)");
                Assert.IsNotNull(PvpClothingPalettes.Palettes(Cb), "the dat became readable");
            });
        }

        [TestMethod]
        public void ASuccessfulRead_IsCached_SoTheDatIsReadOnce()
        {
            var calls = 0;

            With(id =>
            {
                calls++;
                return new Dictionary<uint, uint> { [85] = 7 };
            }, () =>
            {
                PvpClothingPalettes.Palettes(Cb);
                PvpClothingPalettes.Palettes(Cb);
                PvpClothingPalettes.Icon(Cb, 85);

                Assert.AreEqual(1, calls);
            });
        }
    }
}