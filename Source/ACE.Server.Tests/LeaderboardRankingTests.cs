using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Auth;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    [TestClass]
    public class LeaderboardRankingTests
    {
        private sealed record C(string Name, uint Account, int Level, long Xp, long Score, uint Guid, bool Exempt = false);

        /// <summary>
        /// The smallest IPlayer the wave board's real TieBreaker/Detail delegates need: a guid, a name, and
        /// PropertyInt64 storage for BestWaveScoreCenti and (optionally) BestWaveClearTimeCenti. Exercises the
        /// SAME lambdas LeaderboardRanking.Boards wires up for "wave", not a hand-copied formula, so a wiring
        /// bug in the real board (wrong property, wrong sign, wrong null-handling) fails these tests.
        /// </summary>
        internal sealed class WaveBoardFakePlayer : IPlayer
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
            public Dictionary<PropertyInt, int> IntProps { get; } = new Dictionary<PropertyInt, int>();
            public Dictionary<PropertyInt64, long> Int64Props => int64Props;
            public int? GetProperty(PropertyInt property) => IntProps.TryGetValue(property, out var v) ? v : (int?)null;
            public long? GetProperty(PropertyInt64 property) => int64Props.TryGetValue(property, out var v) ? v : (long?)null;
            public Dictionary<PropertyString, string> StringProps { get; } = new Dictionary<PropertyString, string>();
            public string GetProperty(PropertyString property) => StringProps.TryGetValue(property, out var v) ? v : null;

            public void SetProperty(PropertyBool property, bool value) { }
            public void SetProperty(PropertyDataId property, uint value) { }
            public void SetProperty(PropertyFloat property, double value) { }
            public void SetProperty(PropertyInstanceId property, uint value) { }
            public void SetProperty(PropertyInt property, int value) => IntProps[property] = value;
            public void SetProperty(PropertyInt64 property, long value) => int64Props[property] = value;
            public void SetProperty(PropertyString property, string value) => StringProps[property] = value;

            public void RemoveProperty(PropertyBool property) { }
            public void RemoveProperty(PropertyDataId property) { }
            public void RemoveProperty(PropertyFloat property) { }
            public void RemoveProperty(PropertyInstanceId property) { }
            public void RemoveProperty(PropertyInt property) => IntProps.Remove(property);
            public void RemoveProperty(PropertyInt64 property) => int64Props.Remove(property);
            public void RemoveProperty(PropertyString property) => StringProps.Remove(property);

            public uint GetCurrentLoyalty() => 0;
            public uint GetCurrentLeadership() => 0;

            public void SaveBiotaToDatabase(bool enqueueSave = true) { }
            public void UpdateProperty(PropertyInstanceId prop, uint? value, bool broadcast = false) { }
        }

        private static WaveBoardFakePlayer KnotPlayer(string name, uint guid, int? level, long? centi, string key)
        {
            var p = new WaveBoardFakePlayer(name, guid, 0, null);
            if (level.HasValue) p.IntProps[PropertyInt.BestKnotClearLevel] = level.Value;
            if (centi.HasValue) p.Int64Props[PropertyInt64.BestKnotClearTimeCenti] = centi.Value;
            if (key != null) p.StringProps[PropertyString.BestKnotClearPinnacleKey] = key;
            return p;
        }

        private static List<string> RankKnot(params WaveBoardFakePlayer[] players)
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "knot");
            return LeaderboardRanking.Rank(
                players.Cast<IPlayer>(),
                _ => false,
                board.Score,
                board.TieBreaker,
                null,
                p => p.Level ?? 0,
                p => 0L,
                p => p.Guid.Full).Select(p => p.Name).ToList();
        }

        [TestMethod]
        public void KnotBoard_HigherLevelBeatsFasterLowerLevel()
        {
            var r = RankKnot(
                KnotPlayer("FastLow", 1, 300, 1000, "grael"),
                KnotPlayer("SlowHigh", 2, 312, 90000, "grael"));

            CollectionAssert.AreEqual(new[] { "SlowHigh", "FastLow" }, r);
        }

        [TestMethod]
        public void KnotBoard_AtTheSameLevel_FasterComesFirst_AndNoRecordIsOffTheBoard()
        {
            var r = RankKnot(
                KnotPlayer("Slow", 1, 312, 27720, "grael"),
                KnotPlayer("Fast", 2, 312, 20000, "grael"),
                KnotPlayer("NoTime", 3, 312, null, "grael"),
                KnotPlayer("Never", 4, null, null, null));

            CollectionAssert.AreEqual(new[] { "Fast", "Slow", "NoTime" }, r);
        }

        [TestMethod]
        public void KnotBoard_Detail_RendersNameAndTime_AndNullWithoutATime()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "knot");

            // 27720 cs = 4:37.20; an unknown key has no store entry (loaded or not), so the name falls back to the key
            Assert.AreEqual(" - zz_unknown_knot - 4:37.20", board.Detail(KnotPlayer("A", 1, 312, 27720, "zz_unknown_knot")));
            Assert.AreEqual(" - Knot - 4:37.20", board.Detail(KnotPlayer("B", 2, 312, 27720, null)));
            Assert.IsNull(board.Detail(KnotPlayer("C", 3, 312, null, "grael")));
        }

        [TestMethod]
        public void WaveBoard_RealTieBreaker_FasterTimeBeatsSlowerBeatsNoTime()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "wave");

            var fast = new WaveBoardFakePlayer("Fast", 1, 2000, 3000);
            var slow = new WaveBoardFakePlayer("Slow", 2, 2000, 5000);
            var none = new WaveBoardFakePlayer("NoTime", 3, 2000, null);

            Assert.IsTrue(board.TieBreaker(fast) > board.TieBreaker(slow), "a faster clear time must tiebreak-rank above a slower one");
            Assert.IsTrue(board.TieBreaker(slow) > board.TieBreaker(none), "any recorded time must tiebreak-rank above no recorded time");
        }

        [TestMethod]
        public void WaveBoard_RealDetail_RendersTimeWhenSetAndNullWhenUnset()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "wave");

            var timed = new WaveBoardFakePlayer("Timed", 1, 2000, 75423);
            var untimed = new WaveBoardFakePlayer("Untimed", 2, 2000, null);

            Assert.AreEqual(" - 12:34.23", board.Detail(timed));
            Assert.IsNull(board.Detail(untimed));
        }

        [TestMethod]
        public void WaveBoard_RealDelegates_RankOrdersFasterFirstNoTimeLast()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "wave");

            // all three tied on score (2000); times 5000 / 3000 / none
            var cs = new IPlayer[]
            {
                new WaveBoardFakePlayer("Slow", 1, 2000, 5000),
                new WaveBoardFakePlayer("Fast", 2, 2000, 3000),
                new WaveBoardFakePlayer("NoTime", 3, 2000, null),
            };

            var r = LeaderboardRanking.Rank(
                cs,
                _ => false,
                board.Score,
                board.TieBreaker,
                null,
                p => p.Level ?? 0,
                p => p.GetProperty(PropertyInt64.TotalExperience) ?? 0,
                p => p.Guid.Full);

            CollectionAssert.AreEqual(new[] { "Fast", "Slow", "NoTime" }, r.Select(p => p.Name).ToArray());
        }

        private static WaveBoardFakePlayer ThreadPlayer(string name, uint guid, int rung, int clears)
        {
            var p = new WaveBoardFakePlayer(name, guid, 0, null);
            if (rung != 0) p.IntProps[PropertyInt.ThreadGuideLevel] = rung;
            if (clears != 0) p.IntProps[PropertyInt.ThreadClearsLifetime] = clears;
            return p;
        }

        private static List<IPlayer> RankThread(bool exemptNone, params IPlayer[] players)
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "thread");
            return LeaderboardRanking.Rank(
                players,
                _ => !exemptNone,
                board.Score,
                board.TieBreaker,
                null,
                p => p.Level ?? 0,
                p => p.GetProperty(PropertyInt64.TotalExperience) ?? 0,
                p => p.Guid.Full);
        }

        private static WaveBoardFakePlayer TitlePlayer(string name, uint guid, int titles, long xp)
        {
            var p = new WaveBoardFakePlayer(name, guid, 0, null);
            if (titles != 0) p.IntProps[PropertyInt.NumCharacterTitles] = titles;
            p.SetProperty(PropertyInt64.TotalExperience, xp);
            return p;
        }

        [TestMethod]
        public void TitleBoard_RealDelegates_OrdersByTitlesThenXpAndDropsNone()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "title");

            var cs = new IPlayer[]
            {
                TitlePlayer("FiveLowXp", 1, 5, 100),
                TitlePlayer("FiveHighXp", 2, 5, 900),
                TitlePlayer("Nine", 3, 9, 1),
                TitlePlayer("NoTitles", 4, 0, 5000),
            };

            var r = LeaderboardRanking.Rank(
                cs,
                _ => false,
                board.Score,
                board.TieBreaker,
                null,
                p => p.Level ?? 0,
                p => p.GetProperty(PropertyInt64.TotalExperience) ?? 0,
                p => p.Guid.Full);

            CollectionAssert.AreEqual(new[] { "Nine", "FiveHighXp", "FiveLowXp" }, r.Select(p => p.Name).ToArray());
            Assert.AreEqual(9, board.Score(cs[2]));
        }

        private static WaveBoardFakePlayer DeathsPlayer(string name, uint guid, int deaths, long xp)
        {
            var p = new WaveBoardFakePlayer(name, guid, 0, null);
            if (deaths != 0) p.IntProps[PropertyInt.NumDeaths] = deaths;
            p.SetProperty(PropertyInt64.TotalExperience, xp);
            return p;
        }

        [TestMethod]
        public void DeathsBoard_RealDelegates_OrdersByDeathsThenXpAndDropsZeroAndHidden()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "deaths");
            var hidden = new HashSet<uint> { 5 };

            var cs = new IPlayer[]
            {
                DeathsPlayer("FiveLowXp", 1, 5, 100),
                DeathsPlayer("FiveHighXp", 2, 5, 900),
                DeathsPlayer("Nine", 3, 9, 1),
                DeathsPlayer("NoDeaths", 4, 0, 5000),
                DeathsPlayer("HiddenOption", 5, 50, 5000),
            };

            var r = LeaderboardRanking.Rank(
                cs,
                _ => false,
                p => LeaderboardRanking.DeathsBoardScore(p, q => !hidden.Contains(q.Guid.Full)),
                board.TieBreaker,
                null,
                p => p.Level ?? 0,
                p => p.GetProperty(PropertyInt64.TotalExperience) ?? 0,
                p => p.Guid.Full);

            CollectionAssert.AreEqual(new[] { "Nine", "FiveHighXp", "FiveLowXp" }, r.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void DeathsBoard_UndeterminedOption_IsExcluded()
        {
            var p = DeathsPlayer("Unknown", 1, 7, 1);
            Assert.AreEqual(0, LeaderboardRanking.DeathsBoardScore(p, _ => null), "unknown option: privacy-safe exclusion");
            Assert.AreEqual(7, LeaderboardRanking.DeathsBoardScore(p, _ => true));
            Assert.AreEqual(0, LeaderboardRanking.DeathsBoardScore(p, _ => false));

            // The real board's Score reads the option through ReadDeathsVisible, which cannot resolve a test double.
            var board = LeaderboardRanking.Boards.First(b => b.Key == "deaths");
            Assert.AreEqual(0, board.Score(p));
        }

        [TestMethod]
        public void DeathsBoard_IsExcludedFromTheSheetSnapshot_OthersAreNot()
        {
            Assert.IsTrue(LeaderboardRanking.Boards.First(b => b.Key == "deaths").ExcludeFromSheet);
            CollectionAssert.AreEqual(new[] { "deaths" }, LeaderboardRanking.Boards.Where(b => b.ExcludeFromSheet).Select(b => b.Key).ToArray());

            var sheet = LeaderboardRanking.SheetBoards().Select(b => b.Key).ToArray();
            CollectionAssert.DoesNotContain(sheet, "deaths");
            Assert.AreEqual(LeaderboardRanking.Boards.Count - 1, sheet.Length, "every other board stays on the sheet");
        }

        private const int DeathsOptionBit = (int)CharacterOptions2.DisplayNumberDeaths;

        private static void SetField(object target, string name, object value)
        {
            var f = target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(f, name);
            f.SetValue(target, value);
        }

        private static OfflinePlayer OfflineWithDeaths(int deaths, int options1, int options2, bool hasOptions = true)
        {
            var o = (OfflinePlayer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(OfflinePlayer));
            var biota = new ACE.Entity.Models.Biota { Id = 0x50000123 };
            biota.PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.NumDeaths] = deaths };
            SetField(o, "<Biota>k__BackingField", biota);
            SetField(o, "BiotaDatabaseLock", new System.Threading.ReaderWriterLockSlim());
            o.CharacterOptions1 = options1;
            o.CharacterOptions2 = options2;
            o.HasCharacterOptions = hasOptions;
            return o;
        }

        [TestMethod]
        public void DeathsBoard_OfflineBranch_ReadsTheCarriedOptionBits_NoDatabase()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "deaths");

            Assert.AreEqual(7, board.Score(OfflineWithDeaths(7, 0, DeathsOptionBit)), "option on: visible");
            Assert.AreEqual(0, board.Score(OfflineWithDeaths(7, 0, ~DeathsOptionBit)), "option off: hidden");
            Assert.AreEqual(0, board.Score(OfflineWithDeaths(7, -1, -1, hasOptions: false)), "options unknown: excluded");
            Assert.AreEqual(0, board.Score(OfflineWithDeaths(0, 0, DeathsOptionBit)), "no deaths: dropped");
        }

        [TestMethod]
        public void DeathsOption_SharedMapping_UsedByPlayerAndOfflineReads()
        {
            // Player's static initializer opens a database context, so a Player cannot be built in this harness. Player.GetCharacterOption
            // and the offline read both go through CharacterOption.IsSet, which is what is pinned here.
            var opt = CharacterOption.AllowOthersToSeeYourNumberOfDeaths;
            Assert.IsTrue(opt.IsSet(0, DeathsOptionBit));
            Assert.IsFalse(opt.IsSet(-1, ~DeathsOptionBit));
            Assert.IsFalse(opt.IsSet(-1, 0));
        }

        [TestMethod]
        public void ThreadBoard_OrdersByRungDescending()
        {
            var r = RankThread(true,
                ThreadPlayer("Low", 1, 100, 99),
                ThreadPlayer("High", 2, 275, 1),
                ThreadPlayer("Mid", 3, 150, 50));
            CollectionAssert.AreEqual(new[] { "High", "Mid", "Low" }, r.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void ThreadBoard_SameRungBreaksTieOnLifetimeClears()
        {
            var r = RankThread(true,
                ThreadPlayer("FewClears", 1, 275, 3),
                ThreadPlayer("ManyClears", 2, 275, 41),
                ThreadPlayer("LowerRung", 3, 250, 500));
            CollectionAssert.AreEqual(new[] { "ManyClears", "FewClears", "LowerRung" }, r.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void ThreadBoard_ExcludesZeroZeroButKeepsClearsOnlyBelowEveryRung()
        {
            var r = RankThread(true,
                ThreadPlayer("Nothing", 1, 0, 0),
                ThreadPlayer("ClearsOnly", 2, 0, 7),
                ThreadPlayer("Rung50", 3, 50, 0));
            CollectionAssert.AreEqual(new[] { "Rung50", "ClearsOnly" }, r.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void ThreadBoard_ExemptCharactersAreDropped()
        {
            var r = RankThread(false, ThreadPlayer("Staff", 1, 375, 999));
            Assert.AreEqual(0, r.Count);
        }

        [TestMethod]
        public void ThreadBoard_RowTextShowsRungThenClears()
        {
            var prior = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var board = LeaderboardRanking.Boards.First(b => b.Key == "thread");
                var p = ThreadPlayer("P", 1, 275, 41);
                Assert.AreEqual("rung 275 (41 clears)", board.Format(board.Score(p)) + board.Detail(p));

                var clearsOnly = ThreadPlayer("Q", 2, 0, 1200);
                Assert.AreEqual("no rung (1,200 clears)", board.Format(board.Score(clearsOnly)) + board.Detail(clearsOnly));
            }
            finally { CultureInfo.CurrentCulture = prior; }
        }

        [TestMethod]
        public void ThreadBoard_IsGatedOnTheThreadsFlag()
        {
            var board = LeaderboardRanking.Boards.First(b => b.Key == "thread");
            Assert.AreEqual("Thread-Guide", board.Title);
            Assert.AreEqual("dynamic_dungeons_enabled", board.FeatureGate);
        }

        private static List<C> RankPerCharacter(params C[] cs) =>
            LeaderboardRanking.Rank(cs, c => c.Exempt, c => c.Score, c => c.Xp, null, c => c.Level, c => c.Xp, c => c.Guid);

        [TestMethod]
        public void Rank_DropsZeroScoresAndExemptBeforeOrdering()
        {
            var r = RankPerCharacter(new C("Zero", 1, 10, 0, 0, 1), new C("Staff", 2, 10, 0, 999, 2, Exempt: true), new C("A", 3, 10, 0, 5, 3));
            CollectionAssert.AreEqual(new[] { "A" }, r.Select(c => c.Name).ToArray());
        }

        [TestMethod]
        public void Rank_OrdersByScoreThenTieBreakDescending()
        {
            var r = RankPerCharacter(new C("Low", 1, 1, 10, 5, 1), new C("TieHiXp", 2, 1, 99, 7, 2), new C("TieLoXp", 3, 1, 1, 7, 3));
            CollectionAssert.AreEqual(new[] { "TieHiXp", "TieLoXp", "Low" }, r.Select(c => c.Name).ToArray());
        }

        /// <summary>
        /// The exact tiebreak formula the wave board uses (LeaderboardRanking.Boards["wave"].TieBreaker):
        /// t > 0 ? -t : long.MinValue, so ThenByDescending sorts the smallest time first and no-time last.
        /// </summary>
        private static long WaveClearTimeTieBreaker(long clearTimeCenti) => clearTimeCenti > 0 ? -clearTimeCenti : long.MinValue;

        [TestMethod]
        public void Rank_WaveTiebreak_FasterClearTimeBeatsSlower_NoTimeRanksLast()
        {
            // All three tied at the same full-clear score; times 50000 / 30000 / none (0 = never recorded).
            var cs = new[]
            {
                new C("Slow", 1, 1, 50000, 2000, 1),
                new C("Fast", 2, 1, 30000, 2000, 2),
                new C("NoTime", 3, 1, 0, 2000, 3),
            };

            var r = LeaderboardRanking.Rank(cs, c => c.Exempt, c => c.Score, c => WaveClearTimeTieBreaker(c.Xp), null, c => c.Level, c => c.Xp, c => c.Guid);

            CollectionAssert.AreEqual(new[] { "Fast", "Slow", "NoTime" }, r.Select(c => c.Name).ToArray());
        }

        [TestMethod]
        public void Rank_WaveTiebreak_HigherScoreBeatsLowerScoreEvenWithNoTimeVsFastTime()
        {
            // A higher score always wins outright - the tiebreak only ever applies between EQUAL scores.
            var cs = new[]
            {
                new C("HigherScoreNoTime", 1, 1, 0, 2100, 1),
                new C("LowerScoreFastTime", 2, 1, 500, 2000, 2),
            };

            var r = LeaderboardRanking.Rank(cs, c => c.Exempt, c => c.Score, c => WaveClearTimeTieBreaker(c.Xp), null, c => c.Level, c => c.Xp, c => c.Guid);

            CollectionAssert.AreEqual(new[] { "HigherScoreNoTime", "LowerScoreFastTime" }, r.Select(c => c.Name).ToArray());
        }

        [TestMethod]
        public void Rank_WithAccountKey_CollapsesBeforeScoreFilter()
        {
            // Main (higher level) has score 0, alt has score 50: the account drops off entirely, exactly as /top bank rules.
            var cs = new[] { new C("Alt", 7, 40, 0, 50, 1), new C("Main", 7, 220, 0, 0, 2) };
            var r = LeaderboardRanking.Rank(cs, c => c.Exempt, c => c.Score, c => 0, c => c.Account, c => c.Level, c => c.Xp, c => c.Guid);
            Assert.AreEqual(0, r.Count);
        }

        [TestMethod]
        public void Boards_PinTheTopTitlesKeysAndGates()
        {
            var b = LeaderboardRanking.Boards.ToDictionary(x => x.Key);
            CollectionAssert.AreEquivalent(new[] { "dps", "defense", "wave", "knot", "level", "cap", "stamp", "thread", "title", "deaths" }, b.Keys.ToArray());
            Assert.AreEqual("Titles", b["title"].Title);
            Assert.IsNull(b["title"].FeatureGate);
            Assert.IsNotNull(b["title"].TieBreaker);
            Assert.AreEqual("Deaths", b["deaths"].Title);
            Assert.IsNull(b["deaths"].FeatureGate);
            Assert.IsNotNull(b["deaths"].TieBreaker);
            Assert.AreEqual("DPS Challenge", b["dps"].Title);
            Assert.AreEqual("Defense Challenge", b["defense"].Title);
            Assert.AreEqual("Wave Challenge", b["wave"].Title);
            Assert.AreEqual("Knot Clears", b["knot"].Title);
            Assert.IsNull(b["knot"].FeatureGate);
            Assert.IsNotNull(b["knot"].TieBreaker);
            Assert.IsNotNull(b["knot"].Detail);
            Assert.AreEqual("Level", b["level"].Title);
            Assert.AreEqual("Class Ability Points", b["cap"].Title);
            Assert.AreEqual("Quest Stamps", b["stamp"].Title);
            Assert.AreEqual("class_abilities_enabled", b["cap"].FeatureGate);
            Assert.AreEqual("quest_stamps_enabled", b["stamp"].FeatureGate);
            Assert.IsNull(b["dps"].FeatureGate);
            Assert.IsNotNull(b["level"].TieBreaker);
        }

        [TestMethod]
        public void Boards_FormatsMatchTopText()
        {
            var prior = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var b = LeaderboardRanking.Boards.ToDictionary(x => x.Key);
                Assert.AreEqual("6,000 damage (100 DPS)", b["dps"].Format(6000));
                Assert.AreEqual("42s", b["defense"].Format(42));
                Assert.AreEqual("Level 275", b["level"].Format(275));
                Assert.AreEqual("Level 312", b["knot"].Format(312));
                Assert.AreEqual("Level 1,204", b["knot"].Format(1204));
                Assert.AreEqual("12 earned", b["cap"].Format(12));
                Assert.AreEqual("3 stamps", b["stamp"].Format(3));
                Assert.AreEqual("1 title", b["title"].Format(1));
                Assert.AreEqual("1,204 titles", b["title"].Format(1204));
                Assert.AreEqual("1 death", b["deaths"].Format(1));
                Assert.AreEqual("1,204 deaths", b["deaths"].Format(1204));
            }
            finally { CultureInfo.CurrentCulture = prior; }
        }

        [TestMethod]
        public void RankSpeed_KeepsOrderDropsZeroAndResolvedExempt()
        {
            var board = new[] { (Name: "Fast", Cs: 900, Exempt: false), (Name: "Zero", Cs: 0, Exempt: false), (Name: "Staff", Cs: 950, Exempt: true), (Name: "Slow", Cs: 1200, Exempt: false) };
            var r = LeaderboardRanking.RankSpeed(board, e => e.Cs, e => e.Exempt);
            CollectionAssert.AreEqual(new[] { "Fast", "Slow" }, r.Select(e => e.Name).ToArray());
        }

        [TestMethod]
        public void SpeedSeasonTitle_MatchesTopText()
        {
            // /top speed's header and winners lines and the sheet's speed rank all read this one rule
            Assert.AreEqual("Speed Trial", LeaderboardRanking.SpeedSeasonTitle(null));
            Assert.AreEqual("Season 4 - Crypt of Ash", LeaderboardRanking.SpeedSeasonTitle(new ACE.Database.Models.World.SpeedSeason { Name = "Season 4", DungeonName = "Crypt of Ash" }));
            Assert.AreEqual("Season 4", LeaderboardRanking.SpeedSeasonTitle(new ACE.Database.Models.World.SpeedSeason { Name = "Season 4", DungeonName = "  " }));
            Assert.AreEqual("Season 4", LeaderboardRanking.SpeedSeasonTitle(new ACE.Database.Models.World.SpeedSeason { Name = "Season 4", DungeonName = null }));
        }
    }
}
