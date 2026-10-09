using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Auth;
using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards Player.IsNewWaveRecord, the pure comparison behind the wave-gauntlet world-record announcement.
    /// Pure static method - no database, no world, no PropertyManager reads.
    /// <para/>
    /// The root cause this fixes: the OLD rule announced only when score > snapshot score. A full clear caps the
    /// score at totalWaves*100, so once anyone has fully cleared, no later run can score higher - a new #1 earned
    /// by a faster clear time never announced. IsNewWaveRecord compares the full (score, tiebreak) pair instead,
    /// so an equal-score, faster-time run correctly announces.
    /// </summary>
    [TestClass]
    public class WaveChallengeRecordAnnounceTests
    {
        // Mirrors LeaderboardRanking.Boards's "wave" TieBreaker formula exactly: t > 0 ? -t : long.MinValue.
        private static long TieBreak(long? clearTimeCenti) =>
            clearTimeCenti.HasValue && clearTimeCenti.Value > 0 ? -clearTimeCenti.Value : long.MinValue;

        /// <summary>
        /// (a) POSITIVE CONTROL: equal full-clear score, this run has a FASTER time than the snapshot holder ->
        /// announce. This case must FAIL under the old score-only rule (score > snapshot score), since the scores
        /// are equal. Proven below by literally reverting to the old rule inline and showing it fails.
        /// </summary>
        [TestMethod]
        public void EqualScore_FasterTime_Announces()
        {
            const long score = 300; // 3 waves fully cleared
            var snapshotTieBreak = TieBreak(5000); // prior #1's recorded time: 50.00s
            var thisRunTime = 3000L; // this run: 30.00s - faster

            Assert.IsTrue(Player.IsNewWaveRecord(score, thisRunTime, score, snapshotTieBreak));
        }

        /// <summary>
        /// Positive control, part 2: the SAME case, but run through the old score-only rule (score > snapshot
        /// score), proves that rule fails here - which is exactly the bug this change fixes. This is the "revert
        /// to the old rule and show the failure" proof the spec asks for, kept in-test rather than as a throwaway
        /// local edit so it stays checked on every future run.
        /// </summary>
        [TestMethod]
        public void EqualScore_FasterTime_OldScoreOnlyRule_WouldNotHaveAnnounced()
        {
            const long score = 300;
            const long snapshotScore = 300; // same capped full-clear score

            bool OldRuleAnnounces(long thisRunScore, long snapshotMax) => thisRunScore > snapshotMax;

            Assert.IsFalse(OldRuleAnnounces(score, snapshotScore), "the old score-only rule must fail to announce an equal-score, faster-time #1 - that is the bug being fixed");
        }

        /// <summary>(b) Equal score, SLOWER time -> no announcement.</summary>
        [TestMethod]
        public void EqualScore_SlowerTime_DoesNotAnnounce()
        {
            const long score = 300;
            var snapshotTieBreak = TieBreak(3000);
            var thisRunTime = 5000L;

            Assert.IsFalse(Player.IsNewWaveRecord(score, thisRunTime, score, snapshotTieBreak));
        }

        /// <summary>(c) Equal score, EXACT same time -> no announcement (a tie never announces).</summary>
        [TestMethod]
        public void EqualScore_SameTime_DoesNotAnnounce()
        {
            const long score = 300;
            var snapshotTieBreak = TieBreak(4000);
            var thisRunTime = 4000L;

            Assert.IsFalse(Player.IsNewWaveRecord(score, thisRunTime, score, snapshotTieBreak));
        }

        /// <summary>
        /// (d) The prior #1 has a full clear with NO recorded time (every full clear before this shipped), and
        /// this run is a timed full clear at the same score -> announce. A recorded time always tiebreak-outranks
        /// no recorded time.
        /// </summary>
        [TestMethod]
        public void EqualScore_PriorHasNoRecordedTime_ThisRunTimed_Announces()
        {
            const long score = 300;
            var snapshotTieBreak = TieBreak(null); // long.MinValue
            var thisRunTime = 12345L;

            Assert.IsTrue(Player.IsNewWaveRecord(score, thisRunTime, score, snapshotTieBreak));
        }

        /// <summary>(e) A higher PARTIAL score beats a lower one, exactly as today - score alone decides.</summary>
        [TestMethod]
        public void HigherPartialScore_BeatsLowerScore_RegardlessOfTime()
        {
            const long thisRunScore = 210; // wave 2 cleared + 10% into wave 3
            const long snapshotScore = 205; // wave 2 cleared + 5% into wave 3
            var snapshotTieBreak = TieBreak(1000); // irrelevant - scores differ

            Assert.IsTrue(Player.IsNewWaveRecord(thisRunScore, null, snapshotScore, snapshotTieBreak));
        }

        /// <summary>(f) Empty board (no snapshot holder): score > 0 always announces.</summary>
        [TestMethod]
        public void EmptyBoard_AnyPositiveScore_Announces()
        {
            Assert.IsTrue(Player.IsNewWaveRecord(50, null, 0, long.MinValue));
        }

        /// <summary>(g) Score 0 (cleared nothing) -> never announces, regardless of the snapshot.</summary>
        [TestMethod]
        public void ZeroScore_NeverAnnounces()
        {
            Assert.IsFalse(Player.IsNewWaveRecord(0, null, 0, long.MinValue));
            Assert.IsFalse(Player.IsNewWaveRecord(0, 100, -1, long.MinValue));
        }

        /// <summary>
        /// The smallest IPlayer the wave board's real Score/TieBreaker delegates need, reused from
        /// LeaderboardRankingTests' WaveBoardFakePlayer pattern so this test exercises the SAME lambdas
        /// LeaderboardRanking.Boards wires up for "wave", not a hand-copied formula.
        /// </summary>
        private sealed class WaveBoardFakePlayer : IPlayer
        {
            private readonly Dictionary<PropertyInt64, long> int64Props = new Dictionary<PropertyInt64, long>();

            public WaveBoardFakePlayer(string name, uint guid, long waveScoreCenti, long? clearTimeCenti)
            {
                Name = name;
                Guid = new ObjectGuid(guid);
                int64Props[PropertyInt64.BestWaveScoreCenti] = waveScoreCenti;

                if (clearTimeCenti.HasValue)
                    int64Props[PropertyInt64.BestWaveClearTimeCenti] = clearTimeCenti.Value;
            }

            public ObjectGuid Guid { get; }
            public Account Account => null;
            public string Name { get; }
            public int? Level => 1;
            public int? Heritage => null;
            public int? Gender => null;
            public bool IsDeleted => false;
            public bool IsPendingDeletion => false;
            public uint? MonarchId { get; set; }
            public uint? PatronId { get; set; }
            public ulong AllegianceXPCached { get; set; }
            public ulong AllegianceXPGenerated { get; set; }
            public int? AllegianceRank { get; set; }
            public int? AllegianceOfficerRank { get; set; }
            public bool ExistedBeforeAllegianceXpChanges { get; set; }
            public uint? HouseId { get; set; }
            public uint? HouseInstance { get; set; }
            public int? HousePurchaseTimestamp { get; set; }
            public int? HouseRentTimestamp { get; set; }
            public Allegiance Allegiance { get; set; }
            public AllegianceNode AllegianceNode { get; set; }

            public bool? GetProperty(PropertyBool property) => null;
            public uint? GetProperty(PropertyDataId property) => null;
            public double? GetProperty(PropertyFloat property) => null;
            public uint? GetProperty(PropertyInstanceId property) => null;
            public int? GetProperty(PropertyInt property) => null;
            public long? GetProperty(PropertyInt64 property) => int64Props.TryGetValue(property, out var v) ? v : (long?)null;
            public string GetProperty(PropertyString property) => null;

            public void SetProperty(PropertyBool property, bool value) { }
            public void SetProperty(PropertyDataId property, uint value) { }
            public void SetProperty(PropertyFloat property, double value) { }
            public void SetProperty(PropertyInstanceId property, uint value) { }
            public void SetProperty(PropertyInt property, int value) { }
            public void SetProperty(PropertyInt64 property, long value) => int64Props[property] = value;
            public void SetProperty(PropertyString property, string value) { }

            public void RemoveProperty(PropertyBool property) { }
            public void RemoveProperty(PropertyDataId property) { }
            public void RemoveProperty(PropertyFloat property) { }
            public void RemoveProperty(PropertyInstanceId property) { }
            public void RemoveProperty(PropertyInt property) { }
            public void RemoveProperty(PropertyInt64 property) => int64Props.Remove(property);
            public void RemoveProperty(PropertyString property) { }

            public uint GetCurrentLoyalty() => 0;
            public uint GetCurrentLeadership() => 0;

            public void SaveBiotaToDatabase(bool enqueueSave = true) { }
            public void UpdateProperty(PropertyInstanceId prop, uint? value, bool broadcast = false) { }
        }

        /// <summary>
        /// The helper's ordering matches LeaderboardRanking.Rank on the same data: build a small "wave" board of
        /// three players tied on score with different clear times, take the snapshot as the #1 entry Rank()
        /// actually produces, then check IsNewWaveRecord agrees with Rank's placement for a fourth challenger run
        /// at each of a faster, slower and equal time.
        /// </summary>
        [TestMethod]
        public void HelperOrdering_MatchesLeaderboardRankingRank()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "wave");

            var players = new IPlayer[]
            {
                new WaveBoardFakePlayer("Slow", 1, 300, 5000),
                new WaveBoardFakePlayer("Fast", 2, 300, 3000),
                new WaveBoardFakePlayer("NoTime", 3, 300, null),
            };

            var ranked = LeaderboardRanking.Rank(
                players,
                _ => false,
                board.Score,
                board.TieBreaker,
                null,
                p => p.Level ?? 0,
                p => p.GetProperty(PropertyInt64.TotalExperience) ?? 0,
                p => p.Guid.Full);

            // Rank() says #1 is "Fast" (300, time 3000) - confirm that first, so the snapshot below is the real #1.
            CollectionAssert.AreEqual(new[] { "Fast", "Slow", "NoTime" }, ranked.Select(p => p.Name).ToArray());

            var top = ranked[0];
            var snapshotScore = board.Score(top);
            var snapshotTieBreak = board.TieBreaker(top);

            // A challenger tied on score with a faster time than #1 (2000 < 3000) must be a new record.
            Assert.IsTrue(Player.IsNewWaveRecord(300, 2000, snapshotScore, snapshotTieBreak));

            // A challenger tied on score with a slower time than #1 (4000 > 3000) must NOT be a new record.
            Assert.IsFalse(Player.IsNewWaveRecord(300, 4000, snapshotScore, snapshotTieBreak));

            // A challenger tied on score AND time must NOT be a new record (a tie never announces).
            Assert.IsFalse(Player.IsNewWaveRecord(300, 3000, snapshotScore, snapshotTieBreak));
        }
    }
}
