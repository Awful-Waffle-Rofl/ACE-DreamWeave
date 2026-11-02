using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>weak-points.json as <see cref="ThreadDungeonStore"/> reads it: fail-open, one diagnostic per dropped row.</summary>
    [TestClass]
    public class WeakPointStoreTests
    {
        private static ThreadDungeonStore Parse(string weakPointsJson)
            => ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}",
                new Dictionary<string, string>(), weakPointsJson: weakPointsJson);

        private static List<string> Own(ThreadDungeonStore store) => store.Diagnostics.Where(m => m.StartsWith("weak-points.json:")).ToList();

        [TestMethod]
        public void A_valid_file_loads_every_row_with_no_diagnostic()
        {
            var store = Parse("{\"version\":1,\"families\":[{\"family\":\"drudge\",\"height\":\"low\",\"bossHeight\":\"high\"},{\"family\":\"wisp\",\"height\":\"random\"},{\"family\":\"olthoi_larvae\",\"height\":\"medium\"}]}");

            Assert.AreEqual(0, Own(store).Count, string.Join("\n", store.Diagnostics));
            Assert.AreEqual(3, store.WeakPoints.Count);
            Assert.AreEqual("low", store.WeakPoints["drudge"].Height);
            Assert.AreEqual("high", store.WeakPoints["drudge"].BossHeight);
            Assert.IsNull(store.WeakPoints["wisp"].BossHeight);
        }

        [TestMethod]
        public void The_shipped_skeleton_shape_loads_empty_without_a_diagnostic()
        {
            var store = Parse("{\"version\":1,\"families\":[]}");

            Assert.AreEqual(0, store.WeakPoints.Count);
            Assert.AreEqual(0, Own(store).Count, string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void A_missing_or_empty_file_is_one_diagnostic_and_an_empty_table()
        {
            // Load passes "" for a missing file, so the server reports it.
            foreach (var text in new[] { "", "   " })
            {
                var store = Parse(text);

                Assert.AreEqual(0, store.WeakPoints.Count);
                CollectionAssert.AreEqual(new[] { "weak-points.json: empty" }, Own(store), string.Join("\n", store.Diagnostics));
            }

            Assert.AreEqual(0, ThreadDungeonStore.Empty.WeakPoints.Count);
        }

        [TestMethod]
        public void Text_that_was_never_supplied_is_silent()
        {
            var store = Parse(null);

            Assert.AreEqual(0, store.WeakPoints.Count);
            Assert.AreEqual(0, Own(store).Count, string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void An_unsupported_version_ignores_the_whole_file()
        {
            var store = Parse("{\"version\":2,\"families\":[{\"family\":\"drudge\",\"height\":\"low\"}]}");

            Assert.AreEqual(0, store.WeakPoints.Count);
            Assert.IsTrue(Own(store).Any(m => m.Contains("unsupported version 2")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void A_bad_row_is_dropped_with_a_diagnostic_and_the_good_rows_survive()
        {
            var store = Parse("{\"version\":1,\"families\":[" +
                "{\"family\":\"drudge\",\"height\":\"low\"}," +
                "{\"family\":\"Bad-Id\",\"height\":\"low\"}," +
                "{\"family\":\"\",\"height\":\"low\"}," +
                "{\"family\":\"drudge\",\"height\":\"high\"}," +
                "{\"family\":\"mite\",\"height\":\"middle\"}," +
                "{\"family\":\"tusker\",\"height\":\"high\",\"bossHeight\":\"top\"}," +
                "{\"family\":\"golem\"}," +
                "{\"family\":\"ghost\",\"height\":\"medium\"}]}");

            CollectionAssert.AreEquivalent(new[] { "drudge", "ghost" }, store.WeakPoints.Keys.ToList());
            Assert.AreEqual("low", store.WeakPoints["drudge"].Height, "the first of two duplicate rows stands");

            var own = string.Join("\n", Own(store));

            StringAssert.Contains(own, "'Bad-Id' must match [a-z0-9_]+");
            StringAssert.Contains(own, "duplicate family 'drudge'");
            StringAssert.Contains(own, "family 'mite' has unknown height 'middle'");
            StringAssert.Contains(own, "family 'tusker' has unknown bossHeight 'top'");
            StringAssert.Contains(own, "family 'golem' has unknown height ''");
            Assert.AreEqual(6, Own(store).Count, own);
        }
    }
}
