using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvP prestige purchase gate (PvpPrestigeGate, PropertyBool.PvpPrestigeGated). Every behavioural test drives the pure core,
    /// PvpPrestigeGate.Refusal(items, readRecord, minPeak, minWins), with explicit thresholds, so no PropertyManager key is read
    /// here at all. Every "allowed" case carries its own positive control (the same basket one short of the threshold IS refused),
    /// so each test fails if the guard is removed rather than passing vacuously.
    ///
    /// The Vendor.BuyItems_ValidateTransaction call site needs a live Player (whose constructor reaches the auth database), so it is
    /// pinned by a source scan instead: the call exists, it comes before pricing, and its refusal branch messages the player,
    /// destroys the priced items and returns false.
    /// </summary>
    [TestClass]
    public class PvpPrestigeGateTests
    {
        private const long MinPeak = 1700;
        private const long MinWins = 100;

        private static WorldObject GatedItem()
        {
            var item = TestCreatures.CreateQuestBearer("Guise of the Hopeslayer");
            item.SetProperty(PropertyBool.PvpPrestigeGated, true);
            return item;
        }

        private static WorldObject PlainItem() => TestCreatures.CreateQuestBearer("plain robe");

        private static string Gate(IEnumerable<WorldObject> items, PvpPrestigeRecord record) =>
            PvpPrestigeGate.Refusal(items, () => record, MinPeak, MinWins);

        private static readonly string ExpectedRefusal =
            "Only those proven in the Hopeslayer's wars may claim his likeness. You need a peak rating of 1700 on any ladder or 100 Battleground victories.";

        [TestMethod]
        public void BelowBothThresholds_IsRefused_WithTheVerbatimText()
        {
            var refusal = Gate(new[] { GatedItem() }, new PvpPrestigeRecord(1699, 1699, 99, 99));

            Assert.AreEqual(ExpectedRefusal, refusal);
        }

        [TestMethod]
        public void RefusalText_TakesBothNumbersFromTheSettings()
        {
            var refusal = PvpPrestigeGate.Refusal(new[] { GatedItem() }, () => default, 2100, 250);

            Assert.AreEqual("Only those proven in the Hopeslayer's wars may claim his likeness. You need a peak rating of 2100 on any ladder or 250 Battleground victories.", refusal);
        }

        [TestMethod]
        public void ExactlyThePeakThreshold_IsAllowed()
        {
            Assert.IsNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(1700, 0, 0, 0)));
            Assert.IsNotNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(1699, 0, 0, 0)), "positive control: one point short is refused");
        }

        [TestMethod]
        public void ExactlyTheWinsThreshold_IsAllowed()
        {
            Assert.IsNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(0, 0, 100, 0)));
            Assert.IsNotNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(0, 0, 99, 0)), "positive control: one win short is refused");
        }

        [TestMethod]
        public void OnlyTheStampQualifies_IsAllowed()
        {
            Assert.IsNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(StampPeakRating: 1800, StorePeakRating: 0, StampBattlegroundWins: 0, StoreBattlegroundWins: 0)), "peak stamp alone");
            Assert.IsNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(StampPeakRating: 0, StorePeakRating: 0, StampBattlegroundWins: 150, StoreBattlegroundWins: 0)), "wins stamp alone");
            Assert.IsNotNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(0, 0, 0, 0)), "positive control: with the stamps removed it is refused");
        }

        [TestMethod]
        public void OnlyTheStoreQualifies_IsAllowed()
        {
            Assert.IsNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(StampPeakRating: 0, StorePeakRating: 1750, StampBattlegroundWins: 0, StoreBattlegroundWins: 0)), "store peak alone");
            Assert.IsNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(StampPeakRating: 0, StorePeakRating: 0, StampBattlegroundWins: 0, StoreBattlegroundWins: 120)), "store wins alone");
            Assert.IsNotNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(1500, 1699, 40, 99)), "positive control: both sources short is refused");
        }

        [TestMethod]
        public void TheTwoSourcesAreNotSummed()
        {
            // 900 + 900 and 60 + 60 would each clear the bar if the sources were added; the gate takes the better of the two.
            Assert.IsNotNull(Gate(new[] { GatedItem() }, new PvpPrestigeRecord(900, 900, 60, 60)));
        }

        [TestMethod]
        public void UngatedBasket_IsUnaffected_AndNeverReadsTheRecord()
        {
            var reads = 0;
            var refusal = PvpPrestigeGate.Refusal(new[] { PlainItem(), PlainItem() }, () => { reads++; return default; }, MinPeak, MinWins);

            Assert.IsNull(refusal, "no gated item, so an unproven buyer still buys");
            Assert.AreEqual(0, reads, "an ordinary basket never touches the rating store");

            // Positive control: the same unproven buyer with a gated item IS refused, and the record was read.
            var gatedRefusal = PvpPrestigeGate.Refusal(new[] { GatedItem() }, () => { reads++; return default; }, MinPeak, MinWins);
            Assert.IsNotNull(gatedRefusal);
            Assert.AreEqual(1, reads);
        }

        [TestMethod]
        public void ItemWithTheBoolFalse_IsUngated()
        {
            var item = PlainItem();
            item.SetProperty(PropertyBool.PvpPrestigeGated, false);

            Assert.IsNull(Gate(new[] { item }, default));
            Assert.IsNotNull(Gate(new[] { GatedItem() }, default), "positive control");
        }

        [TestMethod]
        public void MixedBasket_IsRefusedWhole()
        {
            var refusal = Gate(new[] { PlainItem(), GatedItem(), PlainItem() }, new PvpPrestigeRecord(1200, 1300, 10, 12));

            Assert.AreEqual(ExpectedRefusal, refusal, "one gated item refuses the whole transaction");
            Assert.IsNull(Gate(new[] { PlainItem(), PlainItem() }, new PvpPrestigeRecord(1200, 1300, 10, 12)), "positive control: the same basket without it passes");
        }

        // ---- the live store's battleground win count ----

        [TestMethod]
        public void RatingStore_WinsOn_ReadsOnlyTheNamedLadder()
        {
            var store = new PvpRatingStore();
            store.Publish(new List<PvpRatingRecord>
            {
                new PvpRatingRecord { CharacterId = 7, Ladder = BattlegroundModes.LadderKey, Games = 140, Wins = 104, Peak = 1610 },
                new PvpRatingRecord { CharacterId = 7, Ladder = "arena_1v1", Games = 30, Wins = 25, Peak = 1720 },
                new PvpRatingRecord { CharacterId = 8, Ladder = BattlegroundModes.LadderKey, Games = 5, Wins = 3, Peak = 1510 },
            });

            Assert.AreEqual(104, store.WinsOn(7, BattlegroundModes.LadderKey));
            Assert.AreEqual(25, store.WinsOn(7, "arena_1v1"));
            Assert.AreEqual(0, store.WinsOn(9, BattlegroundModes.LadderKey), "no row reads 0");
            Assert.AreEqual(0, store.WinsOn(7, null));
            Assert.AreEqual(1720, store.PeakAcrossLadders(7), "the peak half reads across every ladder");
        }

        [TestMethod]
        public void RatingStore_WinsOn_UnavailableStoreReadsZero()
        {
            var store = new PvpRatingStore();
            store.Publish(null);

            Assert.AreEqual(0, store.WinsOn(7, BattlegroundModes.LadderKey));
        }

        // ---- the live record ----

        [TestMethod]
        public void ReadRecord_MapsEachStampToItsOwnField()
        {
            var buyer = TestCreatures.CreateQuestBearer("veteran");
            buyer.SetProperty(PropertyInt.PvpPeakRating, 1800);
            buyer.SetProperty(PropertyInt.PvpLifetimeBattlegroundWins, 120);

            var record = PvpPrestigeGate.ReadRecord(buyer);

            Assert.AreEqual(1800, record.StampPeakRating, "PropertyInt 9091 PvpPeakRating is the peak stamp");
            Assert.AreEqual(120, record.StampBattlegroundWins, "PropertyInt 9090 PvpLifetimeBattlegroundWins is the wins stamp");

            // The store half is whatever the live manager reports for this id (0 for a creature no ladder has seen).
            Assert.AreEqual(PvpMatchManager.PeakRatingAcrossLadders(buyer.Guid.Full), record.StorePeakRating);
            Assert.AreEqual(PvpMatchManager.BattlegroundLadderWins(buyer.Guid.Full), record.StoreBattlegroundWins);

            // Positive control: a creature with neither stamp reads zero stamps, so the two figures above came from the properties.
            var rookie = PvpPrestigeGate.ReadRecord(TestCreatures.CreateQuestBearer("rookie"));
            Assert.AreEqual(0, rookie.StampPeakRating);
            Assert.AreEqual(0, rookie.StampBattlegroundWins);
            Assert.AreEqual(default(PvpPrestigeRecord), PvpPrestigeGate.ReadRecord(null));
        }

        private static string ReadServerSource(params string[] parts)
        {
            var native = Path.Combine(new[] { "Source", "ACE.Server" }.Concat(parts).ToArray());

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate))
                    return AccountVaultPurgeTests.StripComments(File.ReadAllText(candidate));
            }

            Assert.Fail($"Could not find {native} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        /// <summary>Everything from a signature to the end of its line (expression-bodied members).</summary>
        private static string Line(string code, string signatureAnchor)
        {
            var start = code.IndexOf(signatureAnchor, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"could not find {signatureAnchor}");
            var end = code.IndexOf('\n', start);
            return code.Substring(start, (end < 0 ? code.Length : end) - start);
        }

        [TestMethod]
        public void StoreHelpers_ReadTheRightStoreFigure()
        {
            var manager = ReadServerSource("Pvp", "PvpMatchManager.cs");

            var peak = Line(manager, "public static int PeakRatingAcrossLadders(uint characterId)");
            StringAssert.Contains(peak, "Ratings.PeakAcrossLadders(characterId)", "the peak helper reads the peak across EVERY ladder");

            var wins = Line(manager, "public static int BattlegroundLadderWins(uint characterId)");
            StringAssert.Contains(wins, "Ratings.WinsOn(characterId, Battlegrounds.BattlegroundModes.LadderKey)", "the wins helper reads the battleground ladder, not an arena one");

            // Positive control: the two lines are distinct, so each assertion is about its own helper.
            Assert.IsFalse(peak.Contains("WinsOn("));
            Assert.IsFalse(wins.Contains("PeakAcrossLadders("));

            var gate = ReadServerSource("Pvp", "PvpPrestigeGate.cs");
            StringAssert.Contains(gate, "StorePeakRating: PvpMatchManager.PeakRatingAcrossLadders(id)", "the record's store peak comes from the peak helper");
            StringAssert.Contains(gate, "StoreBattlegroundWins: PvpMatchManager.BattlegroundLadderWins(id)", "the record's store wins come from the battleground wins helper");
        }

        // ---- the call site ----

        private static string ReadVendorSource()
        {
            var native = Path.Combine("Source", "ACE.Server", "WorldObjects", "Vendor.cs");

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);
                if (File.Exists(candidate))
                    return AccountVaultPurgeTests.StripComments(File.ReadAllText(candidate));
            }

            Assert.Fail($"Could not find {native} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static string MethodBody(string code, string signatureAnchor)
        {
            var start = code.IndexOf(signatureAnchor, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"could not find {signatureAnchor}");

            var open = code.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after {signatureAnchor}");
            return null;
        }

        [TestMethod]
        public void BuyItemsValidateTransaction_CallsTheGateBeforePricing_AndRefusesTheWholeBasket()
        {
            var body = MethodBody(ReadVendorSource(), "public virtual bool BuyItems_ValidateTransaction(");

            var iGate = body.IndexOf("PvpPrestigeGate.Refusal(purchaseItems, player)", StringComparison.Ordinal);
            var iPrice = body.IndexOf("GetSellCost(item)", StringComparison.Ordinal);
            var iFinalize = body.IndexOf("player.FinalizeBuyTransaction(", StringComparison.Ordinal);

            Assert.IsTrue(iGate >= 0, "the vendor must run the prestige gate on the whole basket");
            Assert.IsTrue(iPrice > iGate, "the gate runs before pricing");
            Assert.IsTrue(iFinalize > iGate, "and before the purchase is finalized");

            var branch = body.Substring(iGate, iPrice - iGate);
            var iSend = branch.IndexOf("player.SendTransientError(prestigeRefusal)", StringComparison.Ordinal);
            var iCleanup = branch.IndexOf("CleanupCreatedItems(defaultItems)", StringComparison.Ordinal);
            var iReturn = branch.IndexOf("return false;", StringComparison.Ordinal);

            Assert.IsTrue(iSend >= 0, "the refusal is sent to the player");
            Assert.IsTrue(iCleanup > iSend, "the items built to price the basket are destroyed");
            Assert.IsTrue(iReturn > iCleanup, "and the whole transaction is refused");
        }
    }
}
