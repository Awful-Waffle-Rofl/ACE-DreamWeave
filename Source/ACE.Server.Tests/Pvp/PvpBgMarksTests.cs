using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The pure half of the battleground Mark of the Hopeslayer payout (Docs/Pvp/BATTLEGROUNDS.md "Rewards"): the per-seat
    /// amount with its scale, and the separate battleground daily-cap ledger. The coordinator half (who is paid, exactly
    /// once, the exclusions) is in BattlegroundCoordinatorTests.
    /// </summary>
    [TestClass]
    public class PvpBgMarksTests
    {
        /// <summary>The arena's amounts are 90 / 80 / 70 here, so a payout that read the wrong dials cannot pass.</summary>
        private sealed record Setup(bool Enabled, BattlegroundDials Bg);

        private static Setup Dials(double scale = 1.0) =>
            new Setup(true, BattlegroundTunables.Defaults with { MarksWin = 7, MarksLoss = 3, MarksDraw = 5, MarksScale = scale });

        private static int Marks(PvpBloodResult result, bool forfeited, bool wentLive, bool sameIp, Setup s) =>
            PvpArenaRewards.BgMarksFor(result, forfeited, wentLive, sameIp, s.Enabled, s.Bg, withheld: false);

        // ---------------- defaults ----------------

        [TestMethod]
        public void Defaults_AreTheOwnerRuling()
        {
            var d = BattlegroundTunables.Defaults;
            Assert.AreEqual(2, d.MarksWin);
            Assert.AreEqual(1, d.MarksLoss);
            Assert.AreEqual(1, d.MarksDraw);
            Assert.AreEqual(1.0, d.MarksScale);
            Assert.AreEqual(0, d.MarksDailyCap, "owner spec 2026-10-15: no cap on battleground Marks");
        }

        [TestMethod]
        public void Defaults_PayOneMarkPerBattleground_PlusOneForAWin_WithNoDailyCap()
        {
            var bg = BattlegroundTunables.Defaults;

            Assert.AreEqual(2, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, true, bg, withheld: false), "1 for completing + 1 for the win");
            Assert.AreEqual(1, PvpArenaRewards.BgMarksFor(PvpBloodResult.Loss, false, true, false, true, bg, withheld: false));
            Assert.AreEqual(1, PvpArenaRewards.BgMarksFor(PvpBloodResult.Draw, false, true, false, true, bg, withheld: false));

            var today = 100;
            var decision = PvpArenaRewards.DecideCap(storedCount: 500, storedDay: today, today: today, dailyCap: bg.MarksDailyCap);
            Assert.IsTrue(decision.Pay, "the default cap never refuses, however many matches were paid today");
        }

        // ---------------- Double Marks window multiplier (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows") ----------------

        private static int Doubled(PvpBloodResult result, double scale, double bonus) =>
            PvpArenaRewards.BgMarksFor(result, false, true, false, true, BattlegroundTunables.Defaults with { MarksScale = scale }, withheld: false, bonusMultiplier: bonus);

        [TestMethod]
        public void BonusMultiplier_DoublesWinLossAndDraw_AtTheShippedAmounts()
        {
            Assert.AreEqual(4, Doubled(PvpBloodResult.Win, 1.0, 2.0));
            Assert.AreEqual(2, Doubled(PvpBloodResult.Loss, 1.0, 2.0));
            Assert.AreEqual(2, Doubled(PvpBloodResult.Draw, 1.0, 2.0));
        }

        [TestMethod]
        public void BonusMultiplier_DefaultOfOne_ChangesNothing()
        {
            var bg = BattlegroundTunables.Defaults;

            Assert.AreEqual(PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, true, bg, withheld: false),
                PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, true, bg, withheld: false, bonusMultiplier: 1.0));
        }

        /// <summary>Scale 1.5 times bonus 2.0 on a base of 1 is exactly 3; scale 1.25 times bonus 2.0 on a base of 1 is 2.5, which rounds away from zero to 3.</summary>
        [TestMethod]
        public void BonusMultiplier_MultipliesWithTheScale_ThenRoundsHalvesAwayFromZero()
        {
            Assert.AreEqual(6, Doubled(PvpBloodResult.Win, 1.5, 2.0), "2 x 1.5 x 2");
            Assert.AreEqual(3, Doubled(PvpBloodResult.Loss, 1.5, 2.0), "1 x 1.5 x 2");
            Assert.AreEqual(3, Doubled(PvpBloodResult.Loss, 1.25, 2.0), "1 x 1.25 x 2 = 2.5 rounds up");
            Assert.AreEqual(2, Doubled(PvpBloodResult.Loss, 0.9, 2.0), "1 x 0.9 x 2 = 1.8");
            Assert.AreEqual(8, Doubled(PvpBloodResult.Win, 1.0, 4.0), "a larger multiplier is honoured");
        }

        [TestMethod]
        public void SanitizeBonusMultiplier_NanInfinityAndBelowOne_ReadAsOne()
        {
            Assert.AreEqual(1.0, PvpArenaRewards.SanitizeBonusMultiplier(double.NaN));
            Assert.AreEqual(1.0, PvpArenaRewards.SanitizeBonusMultiplier(double.PositiveInfinity));
            Assert.AreEqual(1.0, PvpArenaRewards.SanitizeBonusMultiplier(double.NegativeInfinity));
            Assert.AreEqual(1.0, PvpArenaRewards.SanitizeBonusMultiplier(0.5));
            Assert.AreEqual(1.0, PvpArenaRewards.SanitizeBonusMultiplier(-3.0));
            Assert.AreEqual(1.0, PvpArenaRewards.SanitizeBonusMultiplier(1.0));
            Assert.AreEqual(2.0, PvpArenaRewards.SanitizeBonusMultiplier(2.0));
            Assert.AreEqual(2, Doubled(PvpBloodResult.Win, 1.0, double.NaN), "a NaN bonus pays the ordinary amount");
            Assert.AreEqual(2, Doubled(PvpBloodResult.Win, 1.0, 0.1), "a bonus below 1 never reduces pay");
        }

        [TestMethod]
        public void BonusMultiplier_DoesNotRescueTheZeroCases()
        {
            var bg = BattlegroundTunables.Defaults;

            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, true, true, false, true, bg, withheld: false, bonusMultiplier: 2.0), "forfeit");
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, false, false, true, bg, withheld: false, bonusMultiplier: 2.0), "never Live");
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, true, true, bg, withheld: false, bonusMultiplier: 2.0), "same IP");
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, false, bg, withheld: false, bonusMultiplier: 2.0), "switch off");
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, true, bg, withheld: true, bonusMultiplier: 2.0), "withheld");
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, true, bg with { MarksWin = 0 }, withheld: false, bonusMultiplier: 2.0), "a zero base");
            Assert.AreEqual(int.MaxValue, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, true, bg with { MarksScale = 1e300 }, withheld: false, bonusMultiplier: 1e300), "saturates");
        }

        // ---------------- amount ----------------

        [TestMethod]
        public void BgMarksFor_MapsEachOutcome_ToItsOwnDial_NotTheArenasBlood()
        {
            var d = Dials();
            Assert.AreEqual(7, Marks(PvpBloodResult.Win, false, true, false, d));
            Assert.AreEqual(3, Marks(PvpBloodResult.Loss, false, true, false, d));
            Assert.AreEqual(5, Marks(PvpBloodResult.Draw, false, true, false, d));
        }

        [TestMethod]
        public void BgMarksFor_PaysZero_ForSwitchOff_NeverLive_SameIp_Forfeit_AndNullDials()
        {
            var d = Dials();
            Assert.AreEqual(0, Marks(PvpBloodResult.Win, false, true, false, d with { Enabled = false }), "the shared master switch");
            Assert.AreEqual(0, Marks(PvpBloodResult.Win, false, false, false, d), "never went Live");
            Assert.AreEqual(0, Marks(PvpBloodResult.Win, false, true, true, d), "same-IP opponents");
            Assert.AreEqual(0, Marks(PvpBloodResult.Win, true, true, false, d), "forfeiter");
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, true, null, false), "no dials");
        }

        [TestMethod]
        public void BgMarksFor_Withheld_PaysZero_WhateverTheOutcome_AndFalseChangesNothing()
        {
            var d = Dials();
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, d.Enabled, d.Bg, withheld: true));
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Loss, false, true, false, d.Enabled, d.Bg, withheld: true));
            Assert.AreEqual(0, PvpArenaRewards.BgMarksFor(PvpBloodResult.Draw, false, true, false, d.Enabled, d.Bg, withheld: true));
            Assert.AreEqual(7, PvpArenaRewards.BgMarksFor(PvpBloodResult.Win, false, true, false, d.Enabled, d.Bg, withheld: false), "control: the same call not withheld pays");
        }

        [TestMethod]
        public void BgMarksFor_NonPositiveBase_PaysZero_WhateverTheScale()
        {
            Assert.AreEqual(0, Marks(PvpBloodResult.Win, false, true, false, Dials(5.0) with { Bg = Dials(5.0).Bg with { MarksWin = 0 } }));
            Assert.AreEqual(0, Marks(PvpBloodResult.Win, false, true, false, Dials(5.0) with { Bg = Dials(5.0).Bg with { MarksWin = -4 } }));
        }

        [TestMethod]
        public void BgMarksFor_ScaleRounds_ToTheNearestWholeMark_HalvesAwayFromZero()
        {
            // base win 7
            Assert.AreEqual(14, Marks(PvpBloodResult.Win, false, true, false, Dials(2.0)));
            Assert.AreEqual(4, Marks(PvpBloodResult.Win, false, true, false, Dials(0.5)), "3.5 rounds up");
            Assert.AreEqual(2, Marks(PvpBloodResult.Win, false, true, false, Dials(0.3)), "2.1 rounds down");
            Assert.AreEqual(0, Marks(PvpBloodResult.Win, false, true, false, Dials(0.0)), "scale 0 pays nothing");
            // base loss 3 and draw 5: 1.5 rounds the same either way, 2.5 distinguishes away-from-zero from to-even
            Assert.AreEqual(2, Marks(PvpBloodResult.Loss, false, true, false, Dials(0.5)), "1.5 rounds up");
            Assert.AreEqual(3, Marks(PvpBloodResult.Draw, false, true, false, Dials(0.5)), "2.5 rounds AWAY from zero, not to even");
        }

        [TestMethod]
        public void BgMarksFor_ScaleSanitization_NanInfinityNegative()
        {
            Assert.AreEqual(7, Marks(PvpBloodResult.Win, false, true, false, Dials(double.NaN)), "NaN = 1.0");
            Assert.AreEqual(7, Marks(PvpBloodResult.Win, false, true, false, Dials(double.PositiveInfinity)), "+inf = 1.0");
            Assert.AreEqual(7, Marks(PvpBloodResult.Win, false, true, false, Dials(double.NegativeInfinity)), "-inf = 1.0");
            Assert.AreEqual(0, Marks(PvpBloodResult.Win, false, true, false, Dials(-2.0)), "negative = 0");
            Assert.AreEqual(1.0, PvpArenaRewards.SanitizeMarksScale(double.NaN));
            Assert.AreEqual(0.0, PvpArenaRewards.SanitizeMarksScale(-0.25));
            Assert.AreEqual(2.5, PvpArenaRewards.SanitizeMarksScale(2.5));
        }

        [TestMethod]
        public void BgMarksFor_HugeScale_SaturatesAtIntMax_NeverThrowsOrGoesNegative()
        {
            Assert.AreEqual(int.MaxValue, Marks(PvpBloodResult.Win, false, true, false, Dials(1e300)));
        }

        // ---------------- the separate daily-cap ledger ----------------

        [TestMethod]
        public void LedgerProperties_ArenaAndBattleground_AreDisjointFromEachOtherAndFromOwed()
        {
            var arena = Player.LedgerProperties(PvpBloodLedgerKind.Arena);
            var bg = Player.LedgerProperties(PvpBloodLedgerKind.Battleground);

            Assert.AreEqual((PropertyInt.PvpArenaBloodPaidCount, PropertyInt.PvpArenaBloodPaidDay), arena, "the arena's existing 9077 / 9078, unchanged");
            Assert.AreEqual((PropertyInt.PvpBgMarksPaidCount, PropertyInt.PvpBgMarksPaidDay), bg);
            Assert.AreEqual(9083, (int)PropertyInt.PvpBgMarksPaidCount);
            Assert.AreEqual(9084, (int)PropertyInt.PvpBgMarksPaidDay);

            var all = new HashSet<PropertyInt> { arena.Count, arena.Day, bg.Count, bg.Day, PropertyInt.PvpArenaBloodOwed };
            Assert.AreEqual(5, all.Count, "five distinct properties: owed is shared, the two caps are not");
        }

        /// <summary>The same read-Earn-write the player does, over an in-memory property store keyed by the REAL ledger properties.</summary>
        private static bool EarnOn(Dictionary<PropertyInt, int> store, PvpBloodLedgerKind kind, int cap, int today)
        {
            var (countProp, dayProp) = Player.LedgerProperties(kind);
            var ledger = new PvpBloodLedger(store.GetValueOrDefault(countProp), store.GetValueOrDefault(dayProp), store.GetValueOrDefault(PropertyInt.PvpArenaBloodOwed));
            var result = PvpArenaRewards.Earn(ledger, 1, today, cap, (int amount, ref int delivered) => delivered += amount);

            if (result.Capped)
                return false;

            store[countProp] = result.Ledger.PaidCount;
            store[dayProp] = result.Ledger.PaidDay;
            return true;
        }

        [TestMethod]
        public void DailyCaps_AreIndependent_ArenaFullDoesNotBlockBattleground()
        {
            var store = new Dictionary<PropertyInt, int>();

            Assert.IsTrue(EarnOn(store, PvpBloodLedgerKind.Arena, 2, 100));
            Assert.IsTrue(EarnOn(store, PvpBloodLedgerKind.Arena, 2, 100));
            Assert.IsFalse(EarnOn(store, PvpBloodLedgerKind.Arena, 2, 100), "control: the arena cap of 2 is reached");

            Assert.IsTrue(EarnOn(store, PvpBloodLedgerKind.Battleground, 2, 100), "the battleground still pays");
            Assert.IsTrue(EarnOn(store, PvpBloodLedgerKind.Battleground, 2, 100));
            Assert.IsFalse(EarnOn(store, PvpBloodLedgerKind.Battleground, 2, 100), "and has its own cap");
        }

        [TestMethod]
        public void DailyCaps_AreIndependent_BattlegroundFullDoesNotBlockArena()
        {
            var store = new Dictionary<PropertyInt, int>();

            Assert.IsTrue(EarnOn(store, PvpBloodLedgerKind.Battleground, 1, 100));
            Assert.IsFalse(EarnOn(store, PvpBloodLedgerKind.Battleground, 1, 100), "control: the battleground cap of 1 is reached");

            Assert.IsTrue(EarnOn(store, PvpBloodLedgerKind.Arena, 1, 100), "the arena still pays");
            Assert.IsFalse(EarnOn(store, PvpBloodLedgerKind.Arena, 1, 100));
        }

        [TestMethod]
        public void DailyCaps_BothTurnOverOnTheSameDay()
        {
            var store = new Dictionary<PropertyInt, int>();

            Assert.IsTrue(EarnOn(store, PvpBloodLedgerKind.Battleground, 1, 100));
            Assert.IsFalse(EarnOn(store, PvpBloodLedgerKind.Battleground, 1, 100));
            Assert.IsTrue(EarnOn(store, PvpBloodLedgerKind.Battleground, 1, 101), "the next day reopens the battleground cap");
        }

        [TestMethod]
        public void Grant_DefaultsToTheArenaLedger_SoEveryExistingCallSiteIsUnchanged()
        {
            Assert.AreEqual(PvpBloodLedgerKind.Arena, new PvpBloodGrant(Guid.NewGuid(), 10, "UTC", 0).Ledger);
        }
    }
}
