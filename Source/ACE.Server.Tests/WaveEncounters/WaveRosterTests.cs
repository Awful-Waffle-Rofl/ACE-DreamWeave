using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WaveEncounters;

namespace ACE.Server.Tests.WaveEncounters
{
    /// <summary>
    /// WaveRoster: the pure read of a roster weenie's generator rows into (wcid, absolute point) entries,
    /// lifted from Player_WaveChallenge.SpawnWave. No PropertyManager key is read anywhere on this path.
    /// </summary>
    [TestClass]
    public class WaveRosterTests
    {
        private const ushort D6 = 0x003F;

        private static PropertiesGenerator Row(uint wcid, uint cell, float x = 310.10f, float y = -70.55f, float z = -47.995f)
            => new PropertiesGenerator
            {
                Probability = -1,
                WeenieClassId = wcid,
                Delay = 0,
                InitCreate = 1,
                MaxCreate = 1,
                WhenCreate = RegenerationType.Destruction,
                WhereCreate = RegenLocationType.Specific,
                StackSize = -1,
                PaletteId = 0,
                Shade = 0,
                ObjCellId = cell,
                OriginX = x,
                OriginY = y,
                OriginZ = z,
                AnglesW = 1,
                AnglesX = 0,
                AnglesY = 0,
                AnglesZ = 0,
            };

        [TestMethod]
        public void EveryRowOnTheAnchorLandblock_BecomesOneEntry_InTableOrder()
        {
            var rows = new List<PropertiesGenerator>
            {
                Row(1005612, 0x003F014A, 310.10f, -70.55f),
                Row(1005613, 0x003F0152, 316.10f, -76.55f),
                Row(1005616, 0x003F0147, 302.60f, -76.55f),
            };

            var entries = WaveRoster.Read(rows, D6);

            Assert.AreEqual(3, entries.Count);
            Assert.AreEqual(1005612u, entries[0].Wcid);
            Assert.AreEqual(1005613u, entries[1].Wcid);
            Assert.AreEqual(1005616u, entries[2].Wcid);
            Assert.AreEqual(0x003F0152u, entries[1].ObjCellId);
            Assert.AreEqual(316.10f, entries[1].X, 0.0001f);
            Assert.AreEqual(-76.55f, entries[1].Y, 0.0001f);
            Assert.AreEqual(-47.995f, entries[1].Z, 0.0001f);
        }

        [TestMethod]
        public void RowOnAnotherLandblock_IsRejected_AndReported()
        {
            var rejections = new List<string>();

            var entries = WaveRoster.Read(new[] { Row(1005612, 0x003F014A), Row(1005613, 0x0040014A) }, D6, rejections);

            Assert.AreEqual(1, entries.Count, "a row on landblock 0x0040 must not be placed for a 0x003F anchor");
            Assert.AreEqual(1005612u, entries[0].Wcid);
            Assert.AreEqual(1, rejections.Count);
            StringAssert.Contains(rejections[0], "0x0040014A");
        }

        [TestMethod]
        public void RowWithWcidZero_IsRejected_AndReported()
        {
            var rejections = new List<string>();

            var entries = WaveRoster.Read(new[] { Row(0, 0x003F014A), Row(1005614, 0x003F014B) }, D6, rejections);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(1005614u, entries[0].Wcid);
            Assert.AreEqual(1, rejections.Count);
            StringAssert.Contains(rejections[0], "wcid 0");
        }

        [TestMethod]
        public void RowWithNoCell_IsRejected_RatherThanPlacedAtCellZero()
        {
            var row = Row(1005612, 0);
            row.ObjCellId = null;

            Assert.AreEqual(0, WaveRoster.Read(new[] { row }, D6).Count);
        }

        [TestMethod]
        public void NullRows_ReadAsAnEmptyRoster()
        {
            Assert.AreEqual(0, WaveRoster.Read(null, D6).Count);
            Assert.AreEqual(0, WaveRoster.Read(new PropertiesGenerator[] { null }, D6).Count);
        }

        [TestMethod]
        public void ToPosition_KeepsTheCellAndOrigin_AndTakesTheAnchorInstance()
        {
            var entry = WaveRoster.Read(new[] { Row(1005612, 0x003F0147, 302.60f, -76.55f) }, D6)[0];

            var position = entry.ToPosition(0x0001003F);

            Assert.AreEqual(0x003F0147u, position.Cell);
            Assert.AreEqual(302.60f, position.PositionX, 0.0001f);
            Assert.AreEqual(-76.55f, position.PositionY, 0.0001f);
            Assert.AreEqual(0x0001003Fu, position.Instance);
        }

        [TestMethod]
        public void RosterWcid_IsBasePlusWaveMinusOne_LikeTheProvingGrounds()
        {
            Assert.AreEqual(1006510u, WaveRoster.RosterWcid(1006510, 1));
            Assert.AreEqual(1006519u, WaveRoster.RosterWcid(1006510, 10));
            Assert.AreEqual(0u, WaveRoster.RosterWcid(1006510, 0));
            Assert.AreEqual(0u, WaveRoster.RosterWcid(0, 3));
        }
    }
}
