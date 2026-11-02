using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvpDailyCatalog.StatusLines: the pure standing report behind /arena dailies and the post-match status. Dictionary-backed fake source;
    /// reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class PvpDailyStatusTests
    {
        private sealed class FakeSource : IPvpDailyStatusSource
        {
            public readonly Dictionary<string, int> Held = new();
            public readonly Dictionary<string, TimeSpan> Cooldowns = new();

            public bool HasQuest(string questName) => Held.ContainsKey(questName);

            public int GetSolves(string questName) => Held.TryGetValue(questName, out var n) ? n : 0;

            public TimeSpan? CooldownRemaining(string questName) => Cooldowns.TryGetValue(questName, out var t) ? t : null;
        }

        private static string Counter(PvpDailyTask t) => PvpDailyCatalog.Get(t).CounterName;

        [TestMethod]
        public void HeldMidProgress_PrintsProgressLineUnderHeader()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.BloodInTheSand)] = 1;

            var lines = PvpDailyCatalog.StatusLines(s);

            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeader, "[Arena] Blood in the Sand: 1 of 3." }, lines.ToArray());
        }

        [TestMethod]
        public void HeldAtZero_StillListed()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.KingsGround)] = 0;

            var lines = PvpDailyCatalog.StatusLines(s);

            Assert.AreEqual("[Arena] King's Ground: 0 of 180 seconds.", lines.Single(l => l.Contains("King's Ground")));
        }

        [TestMethod]
        public void CompleteAwaitingTurnIn_PrintsReturnToGiver()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.FirstBlood)] = 1;

            var lines = PvpDailyCatalog.StatusLines(s);

            Assert.AreEqual("[Arena] First Blood is complete. Return to Black Ferah.", lines[1]);
        }

        [TestMethod]
        public void OnCooldownNotHeld_PrintsAvailableAgainInFriendlyTime()
        {
            var s = new FakeSource();
            s.Cooldowns[PvpDailyCatalog.Get(PvpDailyTask.Usurper).DoneName] = TimeSpan.FromMinutes(90);

            var lines = PvpDailyCatalog.StatusLines(s);

            Assert.AreEqual("[Arena] Usurper: done, available again in 1h 30m.", lines[1]);
        }

        /// <summary>Models a turned-in task as the live pay chain leaves it: Done stamped (cooling) and the counter RE-ARMED at <paramref name="solves"/>.</summary>
        private static FakeSource Cooling(PvpDailyTask task, TimeSpan remaining, int? rearmedSolves)
        {
            var s = new FakeSource();
            s.Cooldowns[PvpDailyCatalog.Get(task).DoneName] = remaining;

            if (rearmedSolves.HasValue)
                s.Held[Counter(task)] = rearmedSolves.Value;

            return s;
        }

        [TestMethod]
        public void CoolingAndRearmedAtZero_ShowsOnlyTheCooldown()
        {
            var lines = PvpDailyCatalog.StatusLines(Cooling(PvpDailyTask.BloodInTheSand, TimeSpan.FromHours(19) + TimeSpan.FromMinutes(59), 0));

            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeader, "[Arena] Blood in the Sand: done, available again in 19h 59m." }, lines.ToArray());
        }

        [TestMethod]
        public void CoolingAndHeldMid_AppendsNextProgress()
        {
            var lines = PvpDailyCatalog.StatusLines(Cooling(PvpDailyTask.BloodInTheSand, TimeSpan.FromHours(2), 1));

            Assert.AreEqual("[Arena] Blood in the Sand: done, available again in 2h. Next: 1 of 3.", lines[1]);
            Assert.AreEqual(2, lines.Count);
        }

        [TestMethod]
        public void CoolingAndHeldAtTarget_SaysNextAlreadyComplete_NotReturnToGiver()
        {
            var lines = PvpDailyCatalog.StatusLines(Cooling(PvpDailyTask.FirstBlood, TimeSpan.FromMinutes(30), 1));

            Assert.AreEqual("[Arena] First Blood: done, available again in 30m. Next: already complete.", lines[1]);
            Assert.IsFalse(lines.Any(l => l.Contains("Return to")));
        }

        [TestMethod]
        public void CoolingWithUnit_NextCarriesTheUnit()
        {
            var lines = PvpDailyCatalog.StatusLines(Cooling(PvpDailyTask.KingsGround, TimeSpan.FromMinutes(5), 90));

            Assert.AreEqual("[Arena] King's Ground: done, available again in 5m. Next: 90 of 180 seconds.", lines[1]);
        }

        [TestMethod]
        public void HeldWithNoCooldown_ShowsProgressAsBefore()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.Carnage)] = 2;
            s.Cooldowns[PvpDailyCatalog.Get(PvpDailyTask.Carnage).DoneName] = TimeSpan.Zero;

            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeader, "[Arena] Carnage: 2 of 5." }, PvpDailyCatalog.StatusLines(s).ToArray());
        }

        [TestMethod]
        public void PostMatch_CoolingTaskThatMoved_KeepsCooldownLineWithoutRepeatingProgress()
        {
            var s = Cooling(PvpDailyTask.BloodInTheSand, TimeSpan.FromHours(3), 1);
            var moved = new[] { "[Arena] Blood in the Sand: 1 of 3." };

            var lines = PvpDailyCatalog.StatusLines(s, null, moved);

            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeaderOthers, "[Arena] Blood in the Sand: done, available again in 3h." }, lines.ToArray());
        }

        [TestMethod]
        public void PostMatch_CoolingTaskThatDidNotMove_KeepsNext()
        {
            var s = Cooling(PvpDailyTask.BloodInTheSand, TimeSpan.FromHours(3), 1);
            s.Held[Counter(PvpDailyTask.Carnage)] = 2;
            var moved = new[] { "[Arena] Carnage: 2 of 5." };

            var lines = PvpDailyCatalog.StatusLines(s, null, moved);

            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeaderOthers, "[Arena] Blood in the Sand: done, available again in 3h. Next: 1 of 3." }, lines.ToArray());
        }

        private static PvpDailyMove Move(PvpDailyTask task, int count)
        {
            var def = PvpDailyCatalog.Get(task);

            return new PvpDailyMove(def, def.ProgressLine(count));
        }

        /// <summary>What the player is sent after a match, in order: the "this match" lines, then the status block built with exactly those lines as alreadyShown.</summary>
        private static List<string> PostMatch(FakeSource s, params PvpDailyMove[] moved)
        {
            var send = PvpDailyCatalog.MatchLines(moved, s);
            var all = new List<string>(send);
            all.AddRange(PvpDailyCatalog.StatusLines(s, null, send));
            return all;
        }

        [TestMethod]
        public void PostMatch_CoolingTaskReachesTarget_NeverSaysReturnTo_StatusSaysNextAlreadyComplete()
        {
            // Turned in First Blood, then won a duel: the re-armed counter hit its target of 1 while the giver still refuses it.
            var s = Cooling(PvpDailyTask.FirstBlood, TimeSpan.FromHours(17) + TimeSpan.FromMinutes(59), 1);

            var sent = PostMatch(s, Move(PvpDailyTask.FirstBlood, 1));

            Assert.IsFalse(sent.Any(l => l.Contains("Return to")), string.Join(" | ", sent));
            Assert.IsFalse(sent.Any(l => l.Contains("is complete")), string.Join(" | ", sent));
            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeader, "[Arena] First Blood: done, available again in 17h 59m. Next: already complete." }, sent.ToArray());
        }

        [TestMethod]
        public void PostMatch_CoolingTaskMidProgress_NoMovedLine_StatusCarriesNext()
        {
            var s = Cooling(PvpDailyTask.BloodInTheSand, TimeSpan.FromHours(3), 1);

            var sent = PostMatch(s, Move(PvpDailyTask.BloodInTheSand, 1));

            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeader, "[Arena] Blood in the Sand: done, available again in 3h. Next: 1 of 3." }, sent.ToArray());
        }

        [TestMethod]
        public void PostMatch_NonCoolingTask_StillGetsItsMovedLine()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.Carnage)] = 2;
            s.Held[Counter(PvpDailyTask.Bulwark)] = 3;

            var sent = PostMatch(s, Move(PvpDailyTask.Carnage, 2));

            CollectionAssert.AreEqual(new[] { "[Arena] Carnage: 2 of 5.", PvpDailyCatalog.StatusHeaderOthers, "[Arena] Bulwark: 3 of 5." }, sent.ToArray());
        }

        [TestMethod]
        public void PostMatch_MixedCoolingAndLive_OnlyTheLiveOneIsMovedLine()
        {
            var s = Cooling(PvpDailyTask.FirstBlood, TimeSpan.FromHours(1), 1);
            s.Held[Counter(PvpDailyTask.BloodInTheSand)] = 1;

            var send = PvpDailyCatalog.MatchLines(new[] { Move(PvpDailyTask.FirstBlood, 1), Move(PvpDailyTask.BloodInTheSand, 1) }, s);

            CollectionAssert.AreEqual(new[] { "[Arena] Blood in the Sand: 1 of 3." }, send.ToArray());
            Assert.AreEqual(0, PvpDailyCatalog.MatchLines(null, s).Count);
        }

        [TestMethod]
        public void MatchLines_ZeroCooldown_AtTarget_StillSendsTheReturnToLine()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.FirstBlood)] = 1;
            s.Cooldowns[PvpDailyCatalog.Get(PvpDailyTask.FirstBlood).DoneName] = TimeSpan.Zero;

            var send = PvpDailyCatalog.MatchLines(new[] { Move(PvpDailyTask.FirstBlood, 1) }, s);

            CollectionAssert.AreEqual(new[] { "[Arena] First Blood is complete. Return to Black Ferah." }, send.ToArray());
        }

        /// <summary>A cooldown that reads positive once and null afterwards, like a wall-clock source whose cooldown ends between two reads.</summary>
        private sealed class ExpiringSource : IPvpDailyStatusSource
        {
            private readonly Dictionary<string, int> reads = new();

            public bool HasQuest(string questName) => questName == PvpDailyCatalog.Get(PvpDailyTask.FirstBlood).CounterName;

            public int GetSolves(string questName) => 1;

            public TimeSpan? CooldownRemaining(string questName)
            {
                reads[questName] = reads.GetValueOrDefault(questName) + 1;

                return reads[questName] == 1 && questName == PvpDailyCatalog.Get(PvpDailyTask.FirstBlood).DoneName ? TimeSpan.FromSeconds(1) : null;
            }

            public int MaxReadsOfAnyName => reads.Values.Max();
        }

        [TestMethod]
        public void CooldownExpiringBetweenReads_NeverThrows_AndEachNameIsReadOncePerCall()
        {
            var statusSource = new ExpiringSource();
            var lines = PvpDailyCatalog.StatusLines(statusSource);

            Assert.IsTrue(lines.Any(l => l.Contains("done, available again in 1s")), string.Join(" | ", lines));
            Assert.AreEqual(1, statusSource.MaxReadsOfAnyName, "StatusLines reads each cooldown once");

            var matchSource = new ExpiringSource();
            var send = PvpDailyCatalog.MatchLines(new[] { Move(PvpDailyTask.FirstBlood, 1) }, matchSource);

            Assert.AreEqual(0, send.Count, "read once, it was cooling");
            Assert.AreEqual(1, matchSource.MaxReadsOfAnyName, "MatchLines reads the cooldown once");
        }

        [TestMethod]
        public void ToCountdown_MapsTheRawSolveTime()
        {
            Assert.IsNull(PvpDailyCatalog.ToCountdown(TimeSpan.MinValue), "can solve now / never done");
            Assert.IsNull(PvpDailyCatalog.ToCountdown(TimeSpan.MaxValue), "max solves reached or no world row");
            Assert.IsNull(PvpDailyCatalog.ToCountdown(TimeSpan.Zero));
            Assert.IsNull(PvpDailyCatalog.ToCountdown(TimeSpan.FromSeconds(-1)));
            Assert.AreEqual(TimeSpan.FromMinutes(5), PvpDailyCatalog.ToCountdown(TimeSpan.FromMinutes(5)));
        }

        [TestMethod]
        public void NothingHeld_SingleLineNamingAllThreeGivers()
        {
            var lines = PvpDailyCatalog.StatusLines(new FakeSource());

            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "no PvP dailies");
            StringAssert.Contains(lines[0], PvpDailyCatalog.Vidorian);
            StringAssert.Contains(lines[0], PvpDailyCatalog.Ferah);
            StringAssert.Contains(lines[0], PvpDailyCatalog.LerRhan);
        }

        [TestMethod]
        public void ZeroOrExpiredCooldown_IsNotShown()
        {
            var s = new FakeSource();
            s.Cooldowns[PvpDailyCatalog.Get(PvpDailyTask.Holdfast).DoneName] = TimeSpan.Zero;

            Assert.AreEqual(1, PvpDailyCatalog.StatusLines(s).Count);
            StringAssert.Contains(PvpDailyCatalog.StatusLines(s)[0], "no PvP dailies");
        }

        [TestMethod]
        public void GiverFilter_KeepsOnlyThatGiversTasks_AndNamesThemWhenEmpty()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.BloodInTheSand)] = 1;
            s.Held[Counter(PvpDailyTask.HoldTheCrown)] = 1;
            s.Held[Counter(PvpDailyTask.WarmongersDue)] = 4;

            var ler = PvpDailyCatalog.StatusLines(s, PvpDailyCatalog.LerRhan);
            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeader, "[Arena] Hold the Crown: 1 of 2." }, ler.ToArray());

            var none = PvpDailyCatalog.StatusLines(new FakeSource(), "ler rhan");
            Assert.AreEqual(1, none.Count);
            StringAssert.Contains(none[0], PvpDailyCatalog.LerRhan, "echoes the canonical catalog name, not the caller's casing");
            Assert.IsFalse(none[0].Contains("ler rhan"));
            Assert.IsFalse(none[0].Contains(PvpDailyCatalog.Ferah));

            Assert.AreEqual(0, PvpDailyCatalog.StatusLines(s, "Nobody").Count, "an unknown giver yields nothing");
        }

        [TestMethod]
        public void CatalogOrder_IsKept()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.Holdfast)] = 0;
            s.Held[Counter(PvpDailyTask.WarmongersDue)] = 2;
            s.Held[Counter(PvpDailyTask.Carnage)] = 1;

            var lines = PvpDailyCatalog.StatusLines(s);

            Assert.IsTrue(lines[1].Contains("Warmonger's Due"));
            Assert.IsTrue(lines[2].Contains("Carnage"));
            Assert.IsTrue(lines[3].Contains("Holdfast"));
        }

        [TestMethod]
        public void AlreadyShown_DropsThoseLines_OthersGetOtherHeader()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.BloodInTheSand)] = 2;
            s.Held[Counter(PvpDailyTask.Carnage)] = 1;
            var moved = new[] { "[Arena] Blood in the Sand: 2 of 3." };

            var lines = PvpDailyCatalog.StatusLines(s, null, moved);

            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeaderOthers, "[Arena] Carnage: 1 of 5." }, lines.ToArray());
        }

        [TestMethod]
        public void AlreadyShown_EverythingMoved_ReturnsNothing_AndNoDailiesIsSilent()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.BloodInTheSand)] = 2;

            Assert.AreEqual(0, PvpDailyCatalog.StatusLines(s, null, new[] { "[Arena] Blood in the Sand: 2 of 3." }).Count);
            Assert.AreEqual(0, PvpDailyCatalog.StatusLines(new FakeSource(), null, Array.Empty<string>()).Count);
        }

        [TestMethod]
        public void AlreadyShownEmpty_NothingMoved_UsesPlainHeader()
        {
            var s = new FakeSource();
            s.Held[Counter(PvpDailyTask.Bulwark)] = 3;

            var lines = PvpDailyCatalog.StatusLines(s, null, Array.Empty<string>());

            CollectionAssert.AreEqual(new[] { PvpDailyCatalog.StatusHeader, "[Arena] Bulwark: 3 of 5." }, lines.ToArray());
        }
    }
}
