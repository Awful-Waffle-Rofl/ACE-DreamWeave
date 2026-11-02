using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.World;
using ACE.Server.Realms;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Realms
{
    /// <summary>
    /// The per-copy landblock_instance row exclusion the Matron Knot copy of 0x586B uses to drop every retail object
    /// inside the boss room at load. The fixture is the 36 rows the read-only world db returns for
    /// (obj_Cell_Id>>16)=0x586B AND origin_X 190..250 AND origin_Y -170..-100 (queried 2026-10-27), as literals, so the
    /// test needs no database.
    /// </summary>
    [TestClass]
    public class EphemeralRowExclusionTests
    {
        private static readonly EphemeralRowExclusion Box = new EphemeralRowExclusion(0x586B, 194f, 246f, -166f, -100f, -61f, -44f);

        // guid, wcid, obj_Cell_Id, x, y, z
        private static readonly (uint Guid, uint Wcid, uint Cell, float X, float Y, float Z)[] Raw586B =
        {
            (1971761162, 51287, 1483407692, 235.568f, -140.013f, -47.971f),
            (1971761163, 51287, 1483407673, 204.451f, -140.095f, -47.971f),
            (1971761164, 51287, 1483407689, 239.894f, -114.447f, -47.971f),
            (1971761165, 51287, 1483407670, 199.987f, -114.455f, -47.971f),
            (1971761166, 51287, 1483407678, 219.967f, -104.457f, -47.971f),
            (1971761167, 72456, 1483407619, 201.889f, -123.181f, -59.945f),
            (1971761168, 72457, 1483407619, 201.889f, -123.181f, -59.945f),
            (1971761169, 72458, 1483407635, 222.401f, -151.124f, -59.945f),
            (1971761170, 72421, 1483407625, 209.426f, -116.656f, -60.0046f),
            (1971761171, 72421, 1483407632, 222.271f, -115.442f, -60.0046f),
            (1971761172, 51215, 1483407624, 213.345f, -109.411f, -60.0046f),
            (1971761175, 51215, 1483407637, 229.773f, -109.58f, -60.0046f),
            (1971761176, 51215, 1483407641, 241.453f, -123.218f, -60.0046f),
            (1971761178, 51215, 1483407642, 241.63f, -128.412f, -60.0046f),
            (1971761180, 72459, 1483407633, 219.959f, -131.372f, -60.016f),
            (1971761181, 51215, 1483407633, 216.814f, -126.577f, -60.0046f),
            (1971761182, 51215, 1483407633, 223.717f, -126.103f, -60.0046f),
            (1971761183, 72459, 1483407638, 234.592f, -120.196f, -60.016f),
            (1971761184, 51215, 1483407639, 232.435f, -132.747f, -60.0046f),
            (1971761186, 72459, 1483407624, 210.832f, -109.229f, -60.016f),
            (1971761187, 72459, 1483407641, 240.431f, -121.261f, -60.016f),
            (1971761188, 72459, 1483407642, 240.481f, -129.741f, -60.016f),
            (1971761189, 72421, 1483407625, 207.742f, -117.863f, -60.0046f),
            (1971761190, 72421, 1483407631, 224.687f, -113.87f, -60.0046f),
            (1971761191, 72459, 1483407637, 231.448f, -109.618f, -60.016f),
            (1971761192, 72459, 1483407632, 220.059f, -119.332f, -60.016f),
            (1971761193, 72459, 1483407624, 214.76f, -113.758f, -60.016f),
            (1971761195, 72459, 1483407638, 232.035f, -118.712f, -60.016f),
            (1971761197, 72459, 1483407639, 232.015f, -132.389f, -60.016f),
            (1971761198, 72421, 1483407626, 208.243f, -133.043f, -60.0046f),
            (1971761199, 72459, 1483407626, 212.239f, -125.333f, -60.016f),
            (1971761200, 72683, 1483407635, 219.935f, -148.646f, -59.945f),
            (1971761232, 51215, 1483407929, 249.977f, -143.708f, -12.0045f),
            (1971761268, 568, 1483407715, 190f, -164.75f, -35.945f),
            (1971761269, 72439, 1483407631, 220f, -106.463f, -60.063f),
            (1971761271, 72438, 1483407721, 203.127f, -170f, -36.063f),
        };

        private static List<LandblockInstance> Rows586B(ushort? landblockOverride = null) => Raw586B
            .Select(r => new LandblockInstance
            {
                Guid = r.Guid,
                WeenieClassId = r.Wcid,
                ObjCellId = landblockOverride == null ? r.Cell : ((uint)landblockOverride.Value << 16) | (r.Cell & 0xFFFF),
                OriginX = r.X,
                OriginY = r.Y,
                OriginZ = r.Z,
            })
            .ToList();

        [TestMethod]
        public void Fixture_IsThe36QueriedRows_AllOn586B()
        {
            var rows = Rows586B();
            Assert.AreEqual(36, rows.Count);
            Assert.IsTrue(rows.All(r => (r.ObjCellId >> 16) == 0x586B));
        }

        [TestMethod]
        public void Filter_DropsThe33RoomRows_KeepsTheThreeOutside()
        {
            var ex = new EphemeralRowExclusion(0x586B, 194f, 246f, -166f, -100f, -61f, -44f);
            var kept = EphemeralRowExclusion.Filter(Rows586B(), ex, out var dropped);
            Assert.AreEqual(33, dropped);
            CollectionAssert.AreEquivalent(new uint[] { 1971761232, 1971761268, 1971761271 }, kept.Select(r => r.Guid).ToArray());
        }

        [TestMethod]
        public void Filter_ReturnsANewList_TheInputIsUntouched()
        {
            var rows = Rows586B();
            var n = rows.Count;
            var before = rows.ToArray();

            var kept = EphemeralRowExclusion.Filter(rows, Box, out var dropped);

            Assert.AreNotSame(rows, kept);
            Assert.AreEqual(33, dropped);
            Assert.AreEqual(n, rows.Count);
            CollectionAssert.AreEqual(before, rows);
        }

        [TestMethod]
        public void Filter_OtherLandblock_DropsNothing()
        {
            var rows = Rows586B(0x586C);
            var kept = EphemeralRowExclusion.Filter(rows, Box, out var dropped);
            Assert.AreEqual(0, dropped);
            Assert.AreEqual(rows.Count, kept.Count);
            Assert.AreNotSame(rows, kept);
        }

        [TestMethod]
        public void Contains_BoxIsInclusiveOnEveryFace()
        {
            LandblockInstance At(float x, float y, float z) => new LandblockInstance { ObjCellId = 0x586B0100, OriginX = x, OriginY = y, OriginZ = z };

            Assert.IsTrue(Box.Contains(At(194f, -166f, -61f)));
            Assert.IsTrue(Box.Contains(At(246f, -100f, -44f)));
            Assert.IsFalse(Box.Contains(At(193.99f, -120f, -50f)));
            Assert.IsFalse(Box.Contains(At(246.01f, -120f, -50f)));
            Assert.IsFalse(Box.Contains(At(220f, -166.01f, -50f)));
            Assert.IsFalse(Box.Contains(At(220f, -99.99f, -50f)));
            Assert.IsFalse(Box.Contains(At(220f, -120f, -61.01f)));
            Assert.IsFalse(Box.Contains(At(220f, -120f, -43.99f)));
            Assert.IsFalse(Box.Contains(null));
        }

        [TestMethod]
        public void CreateWorldObjects_FiltersBeforeTheFactory()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Entity/Landblock.cs"), "private void CreateWorldObjects()");
            var thread = body.IndexOf("ThreadDungeonContentFilter.Filter(objects", StringComparison.Ordinal);
            var f = body.IndexOf("EphemeralRowExclusion.Filter(objects", StringComparison.Ordinal);
            var fac = body.IndexOf("WorldObjectFactory.CreateNewWorldObjects(objects", StringComparison.Ordinal);
            var links = body.IndexOf("fo.ActivateLinks(objects", StringComparison.Ordinal);
            Assert.IsTrue(thread > 0 && f > thread, $"thread={thread} filter={f}: the exclusion runs after the Thread content filter");
            Assert.IsTrue(f > 0 && fac > f, $"filter={f} factory={fac}");
            Assert.IsTrue(links > fac, $"links={links}: link resolution reads the same filtered list");
        }
    }
}
