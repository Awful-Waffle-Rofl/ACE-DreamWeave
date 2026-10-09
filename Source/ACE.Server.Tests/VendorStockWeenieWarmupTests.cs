using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;

using Weenie = ACE.Entity.Models.Weenie;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The boot-time vendor stock weenie warm-up (VendorStockWeenieWarmup) and the one world-DB query that feeds it
    /// (WorldDatabase.VendorShopCreateListWcidsQuery).
    ///
    /// Nothing here reads PropertyManager, opens a database connection or touches a landblock: the query is only
    /// RENDERED (ToQueryString, against an unconnected Pomelo context, as RewardClaimSchemaTests does), and the
    /// cache fill takes its world-database access as delegates. So the class passes when run alone.
    /// </summary>
    [TestClass]
    public class VendorStockWeenieWarmupTests
    {
        // ---- the collection query's shape ------------------------------------------------------------------

        private static string RenderQuery()
        {
            var options = new DbContextOptionsBuilder<WorldDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new System.Version(8, 0, 36)))
                .Options;

            using var context = new WorldDbContext(options);

            return WorldDatabase.VendorShopCreateListWcidsQuery(context).ToQueryString();
        }

        /// <summary>
        /// The query must pick exactly what Vendor.LoadInventory creates: Shop-destination rows of Vendor-type
        /// weenies, minus Mule Vendors (PersonalVendor.LoadInventory is empty). Each constant is asserted by the
        /// VALUE the enum carries, so a renumbered enum or a swapped constant fails here rather than on stage.
        /// </summary>
        [TestMethod]
        public void Query_SelectsDistinctShopRowsOfNonMuleVendors()
        {
            var sql = RenderQuery();

            StringAssert.Contains(sql, "`weenie_properties_create_list`", sql);
            StringAssert.Contains(sql, "DISTINCT", sql);
            StringAssert.Contains(sql, "`weenie_Class_Id`", sql);
            StringAssert.Contains(sql, "`destination_Type`", sql);
            StringAssert.Contains(sql, "`weenie_properties_bool`", sql);
            StringAssert.Contains(sql, "NOT EXISTS", sql);
            StringAssert.Contains(sql, "INNER JOIN `weenie`", sql);

            // Each filter must bind to the right column, not just exist somewhere in the text.
            Assert.IsTrue(Regex.IsMatch(sql, @"`destination_Type` = @\w*shop"), "Shop filter on destination_Type: " + sql);
            Assert.IsTrue(Regex.IsMatch(sql, @"`type` = @\w*vendor\w*\)"), "Vendor filter on the owning weenie's type: " + sql);
            Assert.IsTrue(Regex.IsMatch(sql, @"NOT EXISTS \(\s*SELECT 1\s*FROM `weenie_properties_bool`[\s\S]*?`type` = @\w*personalVendor"),
                "Mule Vendor exclusion must be a NOT EXISTS over the owning weenie's bools: " + sql);

            // The captured constants arrive as parameters; pin their rendered VALUES (4 and 12 are what the
            // content files write for Shop and Vendor), so a renumbered enum or a swapped constant fails here.
            Assert.IsTrue(Regex.IsMatch(sql, @"SET @\w*shop\w* = 4;"), "the destination filter must be Shop (4): " + sql);
            Assert.IsTrue(Regex.IsMatch(sql, @"SET @\w*vendor\w* = 12;"), "the owner filter must be WeenieType.Vendor (12): " + sql);
            Assert.IsTrue(Regex.IsMatch(sql, $@"SET @\w*personalVendor\w* = {(ushort)PropertyBool.PersonalVendor};"),
                "the Mule Vendor exclusion must key on PropertyBool.PersonalVendor: " + sql);
        }

        // ---- Collect: dedupe and zero-drop -----------------------------------------------------------------

        [TestMethod]
        public void Collect_DedupesDropsZeroAndSorts()
        {
            var set = VendorStockWeenieWarmup.Collect(new uint[] { 30, 0, 10, 30, 20, 10, 0 });

            CollectionAssert.AreEqual(new List<uint> { 10, 20, 30 }, set.ToList());
        }

        [TestMethod]
        public void Collect_NullIsEmpty()
        {
            Assert.AreEqual(0, VendorStockWeenieWarmup.Collect(null).Count);
        }

        // ---- the skip rules --------------------------------------------------------------------------------

        [TestMethod]
        public void ShouldRun_SkippedWhenPrecachingIsOn()
        {
            // Precaching wins even with the tunable on: every weenie is already cached, so the pass is pure waste.
            Assert.IsFalse(VendorStockWeenieWarmup.ShouldRun(true, true, out var reason));
            StringAssert.Contains(reason, "WorldDatabasePrecaching");
        }

        [TestMethod]
        public void ShouldRun_SkippedWhenTheTunableIsOff()
        {
            Assert.IsFalse(VendorStockWeenieWarmup.ShouldRun(false, false, out var reason));
            StringAssert.Contains(reason, VendorStockWeenieWarmup.EnabledKey);
        }

        [TestMethod]
        public void ShouldRun_RunsWhenPrecachingIsOffAndTheTunableIsOn()
        {
            Assert.IsTrue(VendorStockWeenieWarmup.ShouldRun(false, true, out var reason));
            Assert.IsNull(reason);
        }

        // ---- Fill: counts and per-wcid containment ---------------------------------------------------------

        /// <summary>
        /// A stand-in for the weenie cache: <c>present</c> exists, <c>cached</c> is already warm (no miss), and
        /// <c>throwOn</c> blows up. A miss is counted whether or not the row exists, as GetCachedWeenie does.
        /// </summary>
        private sealed class FakeWorld
        {
            private readonly HashSet<uint> present;
            private readonly HashSet<uint> cached;
            private readonly HashSet<uint> throwOn;

            public readonly List<uint> Asked = new List<uint>();
            public long Misses;

            public FakeWorld(IEnumerable<uint> present, IEnumerable<uint> cached = null, IEnumerable<uint> throwOn = null)
            {
                this.present = new HashSet<uint>(present);
                this.cached = new HashSet<uint>(cached ?? Enumerable.Empty<uint>());
                this.throwOn = new HashSet<uint>(throwOn ?? Enumerable.Empty<uint>());
            }

            public Weenie Get(uint wcid)
            {
                Asked.Add(wcid);

                if (throwOn.Contains(wcid))
                    throw new InvalidOperationException($"world database read failed for wcid {wcid}");

                if (!cached.Contains(wcid))
                {
                    Misses++;
                    cached.Add(wcid);
                }

                return present.Contains(wcid) ? new Weenie { WeenieClassId = wcid } : null;
            }

            public long MissCounter() => Misses;
        }

        [TestMethod]
        public void Fill_CountsLoadedCachedMissingAndThrew()
        {
            var world = new FakeWorld(present: new uint[] { 1, 2, 3 }, cached: new uint[] { 2 }, throwOn: new uint[] { 5 });

            var wcids = VendorStockWeenieWarmup.Collect(new uint[] { 1, 2, 3, 4, 5 });

            var result = VendorStockWeenieWarmup.Fill(wcids, world.Get, world.MissCounter);

            Assert.AreEqual(5, result.Wcids);
            Assert.AreEqual(3, result.Loaded, "1, 3 and the absent 4 were cold");
            Assert.AreEqual(1, result.AlreadyCached, "2 was warm");
            Assert.AreEqual(1, result.Missing, "4 has no weenie");
            CollectionAssert.AreEqual(new List<uint> { 4 }, result.MissingWcids);
            Assert.AreEqual(1, result.Threw, "5 threw, and the pass carried on past it");
            CollectionAssert.AreEqual(new List<uint> { 1, 2, 3, 4, 5 }, world.Asked, "every wcid asked for exactly once, in order");
        }

        [TestMethod]
        public void Fill_SecondPassIsAllCached()
        {
            var world = new FakeWorld(present: new uint[] { 1, 2 });
            var wcids = VendorStockWeenieWarmup.Collect(new uint[] { 1, 2 });

            VendorStockWeenieWarmup.Fill(wcids, world.Get, world.MissCounter);
            var second = VendorStockWeenieWarmup.Fill(wcids, world.Get, world.MissCounter);

            Assert.AreEqual(0, second.Loaded);
            Assert.AreEqual(2, second.AlreadyCached);
        }

        [TestMethod]
        public void SummaryLine_ReportsEveryCount()
        {
            var result = new VendorStockWeenieWarmup.WarmResult { Wcids = 10, Loaded = 7, AlreadyCached = 3, Missing = 1, Threw = 0, QueryMs = 40, FillMs = 900 };

            Assert.AreEqual("wcids=10 loaded=7 alreadyCached=3 missing=1 threw=0 queryMs=40 fillMs=900 ms=940",
                VendorStockWeenieWarmup.SummaryLine(result));
        }
    }
}
