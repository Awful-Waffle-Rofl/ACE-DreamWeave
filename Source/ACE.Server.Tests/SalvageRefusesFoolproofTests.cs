using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Ultimate Salvaging Tool must refuse a foolproof tinkering bag. HandleSalvaging needs a live Player
    /// (see SalvageToolTests), so the decision lives in <see cref="SalvageTool.IsRefusedForSalvage"/> and one
    /// test pins that HandleSalvaging's loop actually calls it.
    /// </summary>
    [TestClass]
    public class SalvageRefusesFoolproofTests
    {
        private static uint nextGuid = 0x7D200000;   // static guid range, clear of GuidManager

        // 1002700 = Foolproof White Quartz (fork); 36619 = retail Foolproof Aquamarine; 36634 = retail Foolproof Peridot
        private static WorldObject MakeBag(uint wcid, int? salvageToolCharges = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.CraftTool,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },   // the shipped foolproof weenies are ItemType.Misc
                    { PropertyInt.ItemWorkmanship, 8 },
                    { PropertyInt.MaterialType, (int)MaterialType.Obsidian },
                    { PropertyInt.Structure, 100 },
                    { PropertyInt.MaxStructure, 100 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Bag" } },
            };

            if (salvageToolCharges != null)
                weenie.PropertiesInt[PropertyInt.SalvageToolCharges] = salvageToolCharges.Value;

            return new CraftTool(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void IsRefusedForSalvage_RefusesForkAndRetailFoolproofBags()
        {
            Assert.IsTrue(SalvageTool.IsRefusedForSalvage(MakeBag(1002700)));
            Assert.IsTrue(SalvageTool.IsRefusedForSalvage(MakeBag(36619)));
            Assert.IsTrue(SalvageTool.IsRefusedForSalvage(MakeBag(36634)));
        }

        [TestMethod]
        public void IsRefusedForSalvage_StillRefusesSalvageTools_AndAllowsOrdinaryBags()
        {
            // the existing tool refusal is preserved by the combined predicate
            Assert.IsTrue(SalvageTool.IsRefusedForSalvage(MakeBag(992100, salvageToolCharges: 10)));

            // negative controls: an ordinary salvage-eligible wcid, and null
            Assert.IsFalse(SalvageTool.IsRefusedForSalvage(MakeBag(992101)));
            Assert.IsFalse(SalvageTool.IsRefusedForSalvage(null));
        }

        /// <summary>
        /// The live loop's decision, replayed over a batch exactly as HandleSalvaging drives it: one flag kept
        /// across the loop, one message per operation, everything not refused goes on to be salvaged.
        /// </summary>
        private static (int Refused, int Messages, int Eligible, List<string> Lines) RunBatch(params WorldObject[] items)
        {
            var sent = false;
            var refused = 0;
            var eligible = 0;
            var lines = new List<string>();

            foreach (var item in items)
            {
                if (SalvageTool.TryRefuseForSalvage(item, ref sent, out var message))
                {
                    refused++;

                    if (message != null)
                        lines.Add(message);

                    continue;
                }

                eligible++;
            }

            return (refused, lines.Count, eligible, lines);
        }

        [TestMethod]
        public void MixedBatch_RefusesToolAndFoolproof_SendsOneMessage_SalvagesTheRest()
        {
            var result = RunBatch(MakeBag(992100, salvageToolCharges: 10), MakeBag(1002700), MakeBag(992101), MakeBag(992102));

            Assert.AreEqual(2, result.Refused);
            Assert.AreEqual(1, result.Messages);
            Assert.AreEqual(2, result.Eligible);
            Assert.AreEqual("The Test Bag cannot be salvaged.", result.Lines[0]);
        }

        [TestMethod]
        public void TryRefuseForSalvage_MessageGoesOutOncePerOperation_AndTheFlagIsSet()
        {
            var sent = false;

            Assert.IsFalse(SalvageTool.TryRefuseForSalvage(MakeBag(992101), ref sent, out var none));
            Assert.IsNull(none);
            Assert.IsFalse(sent, "an allowed item must not set the once-flag");

            Assert.IsTrue(SalvageTool.TryRefuseForSalvage(MakeBag(1002700), ref sent, out var first));
            Assert.IsNotNull(first);
            Assert.IsTrue(sent);

            Assert.IsTrue(SalvageTool.TryRefuseForSalvage(MakeBag(36619), ref sent, out var second));
            Assert.IsNull(second, "the second refusal in one operation must be silent");

            // a fresh operation starts with a fresh flag
            sent = false;
            Assert.IsTrue(SalvageTool.TryRefuseForSalvage(MakeBag(36619), ref sent, out var again));
            Assert.IsNotNull(again);
        }

        /// <summary>
        /// Thin consumer pin: HandleSalvaging's loop must act on TryRefuseForSalvage's result, after the
        /// trading check and before the Workmanship/Retained check. Comments are stripped first so a call that
        /// survives only in a comment does not count.
        /// </summary>
        [TestMethod]
        public void HandleSalvaging_LoopActsOnTryRefuseForSalvage_InTheRightPlace()
        {
            var path = Path.Combine(FindRepoRoot(), "Source", "ACE.Server", "WorldObjects", "Player_Crafting.cs");

            Assert.IsTrue(File.Exists(path), $"Player_Crafting.cs not found at {path}.");

            var source = File.ReadAllText(path).Replace("\r\n", "\n");
            var start = source.IndexOf("public void HandleSalvaging(", StringComparison.Ordinal);

            Assert.IsTrue(start >= 0, "HandleSalvaging not found.");

            var body = Regex.Replace(source.Substring(start), "//[^\n]*", "");

            var trading = body.IndexOf("IsBeingTradedOrContainsItemBeingTraded", StringComparison.Ordinal);
            var call = body.IndexOf("if (SalvageTool.TryRefuseForSalvage(item, ref toolRefusalSent, out var refusalMessage))", StringComparison.Ordinal);
            var workmanship = body.IndexOf("item.Workmanship == null || item.Retained", StringComparison.Ordinal);

            Assert.IsTrue(trading >= 0 && workmanship >= 0, "anchors missing in HandleSalvaging.");
            Assert.IsTrue(call >= 0, "the loop must branch on the un-negated result of SalvageTool.TryRefuseForSalvage(item, ref toolRefusalSent, out var refusalMessage).");
            Assert.IsTrue(call > trading, "the refusal must come after the trading check.");
            Assert.IsTrue(call < workmanship, "the refusal must come before the Workmanship/Retained check.");

            // the branch must skip the item and send the line only when one is due
            var branch = body.Substring(call, workmanship - call);
            var guard = Regex.Match(branch, @"if \(refusalMessage != null\)\s*Session\.Network\.EnqueueSend\(new GameMessageSystemChat\(refusalMessage,");

            Assert.IsTrue(guard.Success, "the refusal branch must send refusalMessage, and only when it is non-null.");
            Assert.IsTrue(Regex.IsMatch(branch, @"\bcontinue;"), "the refusal branch must skip the item.");
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Source", "property-registry.tsv")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new AssertFailedException($"Could not find the repo root by walking up from {AppContext.BaseDirectory}.");
        }
    }
}
