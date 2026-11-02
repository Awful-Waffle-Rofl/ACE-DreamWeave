using System.Collections.Generic;

using ACE.Server.Command.Handlers;
using ACE.Server.KnotLoot;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>/knotloot parsing and summary text (pure; no PropertyManager, no world).</summary>
    [TestClass]
    public class KnotLootCommandTests
    {
        private static readonly KnotWindowSettings W = KnotWindowSettings.Default;

        private static KnotTraitSettings DefaultsOf(KnotTraitDefinition d) => d.Defaults;

        [TestMethod]
        public void Parse_Valid()
        {
            var r = KnotLootCommandRequest.Parse(new[] { "grael", "300", "5" });

            Assert.IsTrue(r.IsValid);
            Assert.AreEqual("grael", r.Table);
            Assert.AreEqual(300, r.Level);
            Assert.AreEqual(5, r.Count);
            Assert.IsFalse(r.Give);
        }

        [TestMethod]
        public void Parse_DefaultsToOneRoll_AndLowercasesTable()
        {
            var r = KnotLootCommandRequest.Parse(new[] { "GRAEL", "1" });

            Assert.IsTrue(r.IsValid);
            Assert.AreEqual("grael", r.Table);
            Assert.AreEqual(1, r.Count);
        }

        [TestMethod]
        public void Parse_Give()
        {
            var r = KnotLootCommandRequest.Parse(new[] { "grael", "400", "give" });

            Assert.IsTrue(r.IsValid);
            Assert.IsTrue(r.Give);
        }

        [TestMethod]
        public void Parse_Rejects()
        {
            var bad = new[]
            {
                new[] { "grael", "0" },
                new[] { "grael", "1000" },
                new[] { "grael", "300", "101" },
                new[] { "grael", "300", "0" },
                new[] { "grael", "300", "lots" },
                new[] { "grael", "-5" },
                new[] { "grael" },
                new string[0],
                new[] { "grael", "300", "5", "give" },
            };

            foreach (var args in bad)
            {
                var r = KnotLootCommandRequest.Parse(args);

                Assert.IsFalse(r.IsValid, string.Join(" ", args));
                Assert.AreEqual(KnotLootCommandRequest.Usage, r.Error, string.Join(" ", args));
            }

            var unknown = KnotLootCommandRequest.Parse(new[] { "nope", "300" });

            Assert.IsFalse(unknown.IsValid);
            Assert.AreEqual("Unknown knot loot table: nope", unknown.Error);
        }

        [TestMethod]
        public void SummaryLine_Format_PlainAndTraitDrops()
        {
            var drops = new List<KnotLootDrop>
            {
                new KnotLootDrop(1007400, 1, null, 0.0),
                new KnotLootDrop(1007450, 1, KnotTraitId.CastSpeed, 1.0),
                new KnotLootDrop(20630, 42, null, 0.5),
            };

            var line = KnotLootSummary.FormatLine("grael", 400, 3, drops, W, DefaultsOf);

            Assert.IsTrue(line.StartsWith("Knot grael L400 #3: 1007400 x1; 1007450 Mirrorglass Haste ["), line);
            Assert.IsTrue(line.EndsWith("; 20630 x42"), line);
            Assert.IsTrue(line.Contains("% buff cast speed"), line);

            System.Console.WriteLine("EXAMPLE: " + line);
        }

        [TestMethod]
        public void SummaryLine_EmptyRoll()
        {
            Assert.AreEqual("Knot grael L300 #1: (no drops)",
                KnotLootSummary.FormatLine("grael", 300, 1, new List<KnotLootDrop>(), W, DefaultsOf));
        }

        [TestMethod]
        public void SummaryLine_Under250Chars()
        {
            // The real worst case: the wrist trade (longest effect text) at the top of the window, plus both currencies.
            var worst = new List<KnotLootDrop>
            {
                new KnotLootDrop(1007402, 1, null, 0.0),
                new KnotLootDrop(1007453, 1, KnotTraitId.WristTrade, 1.0),
                new KnotLootDrop(20630, 50, null, 1.0),
                new KnotLootDrop(43901, 50, null, 1.0),
            };

            var line = KnotLootSummary.FormatLine("grael", 999, 100, worst, W, DefaultsOf);

            Assert.IsTrue(line.Length <= KnotLootSummary.MaxLineLength, line.Length + ": " + line);
            Assert.IsTrue(line.Contains("20630 x50; 43901 x50"), line);

            // Synthetic overflow: four trait drops under a long table key must shorten trait text, not split or drop a drop.
            var many = new List<KnotLootDrop>
            {
                new KnotLootDrop(1007453, 1, KnotTraitId.WristTrade, 1.0),
                new KnotLootDrop(1007451, 1, KnotTraitId.ShieldTrade, 1.0),
                new KnotLootDrop(1007452, 1, KnotTraitId.FrugalHand, 1.0),
                new KnotLootDrop(1007450, 1, KnotTraitId.CastSpeed, 1.0),
            };

            var cut = KnotLootSummary.FormatLine("grael", 999, 100, many, W, DefaultsOf);

            Assert.IsTrue(cut.Length <= KnotLootSummary.MaxLineLength, cut.Length + ": " + cut);
            Assert.IsFalse(cut.Contains("\n"));
            Assert.IsTrue(cut.Contains("..."), cut);

            foreach (var wcid in new[] { "1007453 ", "1007451 ", "1007452 ", "1007450 " })
                Assert.IsTrue(cut.Contains(wcid), wcid + " missing: " + cut);

            foreach (var ch in cut)
                Assert.IsTrue(ch >= 0x20 && ch < 0x7F, "non-ASCII in: " + cut);
        }

        [TestMethod]
        public void Parse_UnknownTable_EchoCappedAt40()
        {
            var r = KnotLootCommandRequest.Parse(new[] { new string('x', 200), "300" });

            Assert.AreEqual("Unknown knot loot table: " + new string('x', 40), r.Error);
        }

        [TestMethod]
        public void PlaceOrDestroy_FalseDestroysThatItem()
        {
            var destroyed = new List<int>();

            var res = KnotLootCommands.PlaceOrDestroy(new[] { 1, 2, 3 }, i => i != 2, destroyed.Add);

            Assert.AreEqual(2, res.Placed);
            Assert.AreEqual(1, res.Destroyed);
            Assert.IsNull(res.Error);
            CollectionAssert.AreEqual(new[] { 2 }, destroyed);
        }

        [TestMethod]
        public void PlaceOrDestroy_ThrowDestroysCurrentAndRemaining_NotPlaced()
        {
            var destroyed = new List<int>();

            var res = KnotLootCommands.PlaceOrDestroy(new[] { 1, 2, 3, 4 },
                i => i == 2 ? throw new System.InvalidOperationException("boom") : true,
                destroyed.Add);

            Assert.AreEqual(1, res.Placed);
            Assert.AreEqual(3, res.Destroyed);
            Assert.IsInstanceOfType(res.Error, typeof(System.InvalidOperationException));
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, destroyed);
            Assert.IsFalse(destroyed.Contains(1));
        }

        [TestMethod]
        public void PlaceOrDestroy_DestroyThrowing_StillDestroysRest()
        {
            var destroyed = new List<int>();

            var res = KnotLootCommands.PlaceOrDestroy(new[] { 1, 2, 3 },
                i => i == 1 ? throw new System.InvalidOperationException() : true,
                i => { destroyed.Add(i); if (i == 1) throw new System.InvalidOperationException(); });

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, destroyed);
            Assert.AreEqual(0, res.Placed);
        }
    }
}
