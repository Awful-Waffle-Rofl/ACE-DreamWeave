using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Physics.Common;

namespace ACE.Server.Tests
{
    /// <summary>
    /// LandDefs.LandHeightTable used to be resolved in a static constructor only if the dats were already
    /// loaded at the first touch of LandDefs, so an earlier touch left it null for the whole process and a
    /// later geometry test died with a NullReferenceException in LandblockStruct. These pins stand in for
    /// the dat through LandDefs.HeightTableSource, so they run in CI without dat files.
    /// </summary>
    [TestClass]
    public class LandDefsHeightTableTests
    {
        private Func<List<float>> originalSource;

        [TestInitialize]
        public void Save()
        {
            originalSource = LandDefs.HeightTableSource;
            LandDefs.ResetHeightTableForTests();
        }

        [TestCleanup]
        public void Restore()
        {
            LandDefs.HeightTableSource = originalSource;
            LandDefs.ResetHeightTableForTests();
        }

        [TestMethod]
        public void Table_read_before_dats_load_resolves_once_they_do()
        {
            var dat = new List<float> { 0f, 1.5f, 3f };
            List<float> available = null;
            LandDefs.HeightTableSource = () => available;

            Assert.IsNull(LandDefs.LandHeightTable, "dats not loaded yet");

            available = dat;

            Assert.AreSame(dat, LandDefs.LandHeightTable, "the earlier null read must not stick");
        }

        [TestMethod]
        public void Resolved_table_is_cached_and_the_source_is_not_asked_again()
        {
            var calls = 0;
            var dat = new List<float> { 7f };
            LandDefs.HeightTableSource = () => { calls++; return dat; };

            Assert.AreSame(dat, LandDefs.LandHeightTable);
            Assert.AreSame(dat, LandDefs.LandHeightTable);

            Assert.AreEqual(1, calls);
        }
    }
}
