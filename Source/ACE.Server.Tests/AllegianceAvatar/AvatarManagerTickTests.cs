using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Server.Avatars;
using ACE.Server.Entity;

namespace ACE.Server.Tests.Avatars
{
    [TestClass]
    public class AvatarManagerTickTests
    {
        // 2027-01-15 08:00:00 UTC.
        private const double Start = 1_800_000_000.0;
        private const double Day = 86400.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Defaults except the base rate, seeded here at 1e-6 (1,000,000 raw XP = 1 point) so the XP-denominated numbers below do not move when the shipped rate does; soft cap 500, flush 60 s, min 2 active accounts.
        private static readonly AvatarTuning T = AvatarTuning.Defaults with { BaseRate = 0.000001 };
        private const long OnePoint = 1_000_000;

        private const uint AvatarId = 7;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint Vassal = 1001;
        private const uint VassalAcct = 101;

        private FakeStore store;
        private double now;
        private double Now { get => Volatile.Read(ref now); set => Volatile.Write(ref now, value); }
        private AccountActivityIndex activity;
        private List<AvatarRoster> rosters;

        [TestInitialize]
        public void Setup()
        {
            store = new FakeStore();
            now = Start;
            activity = new AccountActivityIndex();
            rosters = new List<AvatarRoster>();
            AvatarTestChart.Install();
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarTestChart.Reset();
            // Leave the static manager disabled and empty for whichever class runs next.
            AvatarManager.Initialize(new FakeStore { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        // ---- fixture helpers ---------------------------------------------------------------------------------

        private void Init() => AvatarManager.Initialize(store, () => T, () => Now, () => activity, () => rosters);

        private void Advance(double seconds) => Now = Now + seconds;

        private void Seen(uint account, double daysAgo = 0) => activity.Touch(account, now - daysAgo * Day);

        private static KeeperCandidate Member(uint guid, uint account, int depth) => new KeeperCandidate(guid, account, 1, 0, depth);

        private void AddRoster(uint monarch, uint monarchAcct, params KeeperCandidate[] vassals)
        {
            var members = new List<KeeperCandidate> { Member(monarch, monarchAcct, 0) };
            members.AddRange(vassals);
            rosters.Add(new AvatarRoster(monarch, monarchAcct, false, members));
        }

        private void SeedAvatar(uint avatarId = AvatarId, uint monarch = Monarch, ulong favor = 0, ulong lifetime = 0, DateTime? lastDecayDay = null, DateTime? lastContribution = null)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = avatarId,
                MonarchGuid = monarch,
                State = (byte)AvatarBindState.Bound,
                Favor = favor,
                LifetimeXp = lifetime,
                LastDecayDay = lastDecayDay ?? Today,
                LastContributionAt = lastContribution,
            });
        }

        /// <summary>The standard live allegiance: Avatar 7 bound to monarch 1000 with one vassal, both accounts active.</summary>
        private void SeedActiveAllegiance(ulong favor = 0)
        {
            SeedAvatar(favor: favor);
            AddRoster(Monarch, MonarchAcct, Member(Vassal, VassalAcct, 1));
            Seen(MonarchAcct);
            Seen(VassalAcct);
        }

        private static void Feed(uint monarch, uint account, uint character, long amount, AvatarFeedSource source = AvatarFeedSource.Base) =>
            AvatarManager.Enqueue(new AvatarFeedEvent(monarch, account, character, amount, source));

        // ---- level pool ------------------------------------------------------------------------------------

        private AvatarState TickLevelFeed(params long[] grants)
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            foreach (var g in grants)
                AvatarManager.EnqueueLevel(Monarch, g);

            Advance(1);
            AvatarManager.Tick();
            return AvatarManager.Get(AvatarId);
        }

        [TestMethod]
        public void LevelPool_MillionRawAtOnePercent_Gives10000()
        {
            Assert.AreEqual(10_000UL, TickLevelFeed(1_000_000).LevelXp);
        }

        [TestMethod]
        public void LevelPool_TwoGrantsOf150_Give3_WithTheRemainderCarried()
        {
            var avatar = TickLevelFeed(150); // 1.5 -> 1, remainder 0.5
            Assert.AreEqual(1UL, avatar.LevelXp);
            Assert.AreEqual(0.5, avatar.LevelXpRemainder, 1e-9);

            AvatarManager.EnqueueLevel(Monarch, 150); // 0.5 + 1.5 = 2.0
            Advance(1);
            AvatarManager.Tick();

            Assert.AreEqual(3UL, avatar.LevelXp);
            Assert.AreEqual(0.0, avatar.LevelXpRemainder, 1e-9);
        }

        [TestMethod]
        public void LevelPool_HasNoMultiplierDailyCapOrRosterScale()
        {
            // 100 billion raw XP in one go: the points pipeline would cap this account at 1,500 for the day; the pool does not.
            var avatar = TickLevelFeed(100_000_000_000);

            Assert.AreEqual(1_000_000_000UL, avatar.LevelXp);
            Assert.AreEqual(0UL, avatar.LifetimeXp, "the pool feed does not touch the contribution points");
        }

        [TestMethod]
        public void LevelPool_AFractionOfZero_AddsNothing()
        {
            SeedActiveAllegiance();
            AvatarManager.Initialize(store, () => T with { LevelXpFraction = 0.0 }, () => Now, () => activity, () => rosters);
            AvatarManager.Tick();

            AvatarManager.EnqueueLevel(Monarch, 1_000_000);
            Advance(1);
            AvatarManager.Tick();

            Assert.AreEqual(0UL, AvatarManager.Get(AvatarId).LevelXp);
        }

        [TestMethod]
        public void LevelPool_SaturatesAtUlongMax()
        {
            SeedActiveAllegiance();
            store.Avatars[0].LevelXp = ulong.MaxValue - 5;
            Init();
            AvatarManager.Tick();

            AvatarManager.EnqueueLevel(Monarch, 100_000); // +1000
            Advance(1);
            AvatarManager.Tick();

            Assert.AreEqual(ulong.MaxValue, AvatarManager.Get(AvatarId).LevelXp);
        }

        [TestMethod]
        public void LevelPool_NonBoundAvatar_IsSkipped()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();
            var avatar = AvatarManager.Get(AvatarId);
            avatar.State = AvatarBindState.Retired;

            AvatarManager.EnqueueLevel(Monarch, 1_000_000);
            Advance(1);
            AvatarManager.Tick();

            Assert.AreEqual(0UL, avatar.LevelXp);
        }

        [TestMethod]
        public void LevelPool_UnknownMonarch_IsIgnored()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            AvatarManager.EnqueueLevel(99999, 1_000_000);
            Advance(1);
            AvatarManager.Tick();

            Assert.AreEqual(0UL, AvatarManager.Get(AvatarId).LevelXp);
        }

        [TestMethod]
        public void LevelPool_RecordsALevelUp_FromThePool_AndMarksTheAvatarDirty()
        {
            var seen = new List<AvatarLevelUpEvent>();
            AvatarLevelUp.Dispatch = (e, t) => seen.Add(e);
            try
            {
                SeedActiveAllegiance();
                Init();
                AvatarManager.Tick();

                // Level 1 -> 4 on the synthetic chart (9,256 <= 10,000 < 15,810).
                AvatarManager.EnqueueLevel(Monarch, 1_000_000);
                Advance(T.FlushSeconds);
                AvatarManager.Tick();

                Assert.AreEqual(1, seen.Count);
                Assert.AreEqual(1, seen[0].OldLevel);
                Assert.AreEqual(4, seen[0].NewLevel);
                Assert.AreEqual(10_000UL, store.Batches.Last().Avatars.Single().LevelXp, "the flushed row carries the pool");
            }
            finally
            {
                AvatarLevelUp.ResetSeams();
            }
        }

        [TestMethod]
        public void LifetimeXp_NoLongerDrivesTheLevel()
        {
            var state = new AvatarState { LifetimeXp = 1_000_000_000_000UL, LevelXp = 0 };

            Assert.AreEqual(1, state.Level(T));
        }

        [TestMethod]
        public void PointsCredit_DoesNotRaiseTheLevel()
        {
            var seen = new List<AvatarLevelUpEvent>();
            AvatarLevelUp.Dispatch = (e, t) => seen.Add(e);
            try
            {
                SeedActiveAllegiance();
                Init();
                AvatarManager.Tick();

                Feed(Monarch, MonarchAcct, Monarch, 400 * OnePoint);
                Advance(T.FlushSeconds);
                AvatarManager.Tick();

                Assert.AreEqual(400UL, AvatarManager.Get(AvatarId).LifetimeXp);
                Assert.AreEqual(1, AvatarManager.Get(AvatarId).Level(T));
                Assert.AreEqual(0, seen.Count);
            }
            finally
            {
                AvatarLevelUp.ResetSeams();
            }
        }

        // ---- feed ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Tick_DrainsQueue_IntoLifetimeXpAndFavor()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            Feed(Monarch, VassalAcct, Vassal, 100 * OnePoint);
            Feed(Monarch, MonarchAcct, Monarch, 20 * OnePoint, AvatarFeedSource.Thread); // 20 x 4 = 80

            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            var avatar = AvatarManager.Get(AvatarId);
            Assert.AreEqual(180UL, avatar.LifetimeXp);
            Assert.AreEqual(180UL, avatar.Favor);
            Assert.IsNotNull(avatar.LastContributionUtc);

            // Pin the consumer: the flushed batch carries the same absolute totals.
            Assert.AreEqual(1, store.Batches.Count);
            var batch = store.Batches[0];
            Assert.AreEqual(180UL, batch.Avatars.Single().LifetimeXp);
            Assert.AreEqual(180UL, batch.Avatars.Single().Favor);
            Assert.AreEqual(100UL, batch.Rows.Single(r => r.AccountId == VassalAcct).Credited);
            Assert.AreEqual(80UL, batch.Rows.Single(r => r.AccountId == MonarchAcct).Credited);
            Assert.AreEqual(Today, batch.Rows[0].Day);
        }

        [TestMethod]
        public void Tick_FractionalPoints_CarryOverUntilTheyAddUp()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            // 0.4 points per flush interval: floored alone it would never credit anything.
            for (var i = 0; i < 3; i++)
            {
                Feed(Monarch, VassalAcct, Vassal, 400_000);
                Advance(T.FlushSeconds);
                AvatarManager.Tick();
            }

            Assert.AreEqual(1UL, AvatarManager.Get(AvatarId).LifetimeXp, "0.4 + 0.4 + 0.4 = 1.2 credits 1 point, carrying 0.2");
        }

        [TestMethod]
        public void Tick_InactiveAvatar_StillAccrues_ButCannotSell()
        {
            SeedAvatar();
            AddRoster(Monarch, MonarchAcct, Member(Vassal, VassalAcct, 1));
            Seen(MonarchAcct);
            Seen(VassalAcct, daysAgo: 8); // outside the 7-day window
            Init();
            AvatarManager.Tick();

            Assert.IsFalse(AvatarManager.Get(AvatarId).Active);

            Feed(Monarch, MonarchAcct, Monarch, 100 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            var avatar = AvatarManager.Get(AvatarId);
            Assert.AreEqual(100UL, avatar.LifetimeXp, "every grant to a sworn member feeds the Avatar, Inactive or not");
            Assert.AreEqual(100UL, avatar.Favor);
            Assert.AreEqual(100UL, store.Batches.SelectMany(b => b.Rows).Single().Credited, "the contribution is recorded");
            Assert.IsFalse(avatar.Active);
            Assert.IsFalse(AvatarVendor.IsLive(avatar), "Inactive: the vendor sells nothing");
        }

        [TestMethod]
        public void Tick_RosterScale_UsesTheAllegianceAndServerActiveCounts()
        {
            // 10 active accounts in this allegiance, 10 on the server: threshold max(2, 0.2 x 10) = 2,
            // effective = 2 + 8^0.5 = 4.828, scale 0.4828. 1000 points arrive as 482.
            SeedAvatar();
            var vassals = Enumerable.Range(1, 9).Select(i => Member(Monarch + (uint)i, MonarchAcct + (uint)i, 1)).ToArray();
            AddRoster(Monarch, MonarchAcct, vassals);
            for (uint i = 0; i < 10; i++)
                Seen(MonarchAcct + i);
            Init();
            AvatarManager.Tick();

            Feed(Monarch, VassalAcct, Vassal, 1000 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            // 482 points raw against the 500 soft cap: all linear.
            var row = store.Batches.Single().Rows.Single();
            Assert.AreEqual(482UL, row.RawXp);
            Assert.AreEqual(482UL, row.Credited);
            Assert.AreEqual(482UL, AvatarManager.Get(AvatarId).LifetimeXp);
        }

        [TestMethod]
        public void Tick_DailyCap_AppliesToSummedBatch_NotPerKill()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            // 1000 kills of 1 point each in one interval: one call with 1000 gives 500 + 125 = 625.
            // Per-kill calls past the soft cap would floor 0.25 to 0 every time and give 500.
            for (var i = 0; i < 1000; i++)
                Feed(Monarch, VassalAcct, Vassal, OnePoint);

            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(625UL, AvatarManager.Get(AvatarId).LifetimeXp);
        }

        [TestMethod]
        public void Tick_SameAccountTwoAvatars_ShareOneDailyCap()
        {
            SeedActiveAllegiance();
            SeedAvatar(avatarId: 8, monarch: 2000);
            AddRoster(2000, 200, Member(2001, 201, 1), Member(2002, VassalAcct, 1));
            Seen(200);
            Seen(201);
            Init();
            AvatarManager.Tick();

            Feed(Monarch, VassalAcct, Vassal, 400 * OnePoint);
            Feed(2000, VassalAcct, 2002, 400 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            var total = AvatarManager.Get(AvatarId).LifetimeXp + AvatarManager.Get(8).LifetimeXp;
            Assert.AreEqual(575UL, total, "one account-day cap across both Avatars: 500 linear + 300 x 0.25");
        }

        [TestMethod]
        public void Initialize_RestoresTodaysCap_FromStoredRows()
        {
            SeedActiveAllegiance();
            // Raw and credited differ, so a swap of the two columns on restore fails.
            store.Contributions.Add(new AllegianceAvatarContribution { AvatarId = AvatarId, AccountId = VassalAcct, Day = Today, RawXp = 600, Credited = 525 });
            store.Contributions.Add(new AllegianceAvatarContribution { AvatarId = AvatarId, AccountId = MonarchAcct, Day = Today.AddDays(-1), RawXp = 500, Credited = 500 });
            Init();
            AvatarManager.Tick();

            Feed(Monarch, VassalAcct, Vassal, 100 * OnePoint);
            Feed(Monarch, MonarchAcct, Monarch, 100 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            var rows = store.Batches.Single().Rows;
            Assert.AreEqual(550UL, rows.Single(r => r.AccountId == VassalAcct).Credited, "today's 600 raw / 525 credited survived the restart, so 100 more credits curve(700) - 525 = 25");
            Assert.AreEqual(700UL, rows.Single(r => r.AccountId == VassalAcct).RawXp);
            Assert.AreEqual(100UL, rows.Single(r => r.AccountId == MonarchAcct).Credited, "yesterday's row does not count toward today's cap");
            Assert.AreEqual(125UL, AvatarManager.Get(AvatarId).LifetimeXp);

            var top = AvatarManager.TopContributors(AvatarId, 5);
            // The 30-day list survives the restart too: yesterday's 500 plus today's 100 for the monarch's account.
            Assert.AreEqual(2, top.Count);
            Assert.AreEqual((MonarchAcct, 600L), (top[0].AccountId, top[0].Credited));
            Assert.AreEqual((VassalAcct, 550L), (top[1].AccountId, top[1].Credited));
        }

        // ---- flush -----------------------------------------------------------------------------------------

        [TestMethod]
        public void Tick_FlushesOnInterval_OneBatchCall()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            for (var i = 0; i < 100; i++)
                Feed(Monarch, i % 2 == 0 ? VassalAcct : MonarchAcct, i % 2 == 0 ? Vassal : Monarch, OnePoint);

            Advance(1);
            AvatarManager.Tick();
            Assert.AreEqual(0, store.Batches.Count, "nothing is written before flushSeconds");

            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(1, store.Batches.Count, "100 events produce one SaveBatch call");
            Assert.AreEqual(1, store.Batches[0].Avatars.Count);
            Assert.AreEqual(2, store.Batches[0].Rows.Count, "one row per account per day");
            Assert.AreEqual(100UL, store.Batches[0].Avatars[0].LifetimeXp);

            Advance(T.FlushSeconds);
            AvatarManager.Tick();
            Assert.AreEqual(1, store.Batches.Count, "nothing dirty, nothing written");
        }

        [TestMethod]
        public void Flush_Failure_KeepsRowsDirty_AndRetriesWithAbsoluteValues()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            store.SaveResults.Enqueue(false);
            Feed(Monarch, VassalAcct, Vassal, 100 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(1, store.Batches.Count);

            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(2, store.Batches.Count, "the failed batch is retried on the next interval");
            var retry = store.Batches[1];
            Assert.AreEqual(100UL, retry.Rows.Single().Credited, "same credited total, not doubled");
            Assert.AreEqual(100UL, retry.Rows.Single().RawXp);
            Assert.AreEqual(100UL, retry.Avatars.Single().LifetimeXp);
            Assert.AreEqual(100UL, AvatarManager.Get(AvatarId).LifetimeXp);

            Advance(T.FlushSeconds);
            AvatarManager.Tick();
            Assert.AreEqual(2, store.Batches.Count, "a successful retry leaves nothing dirty");
        }

        [TestMethod]
        public void Flush_InFlight_IsNotIssuedTwice()
        {
            SeedActiveAllegiance();
            store.DeferSaves = true;
            Init();
            AvatarManager.Tick();

            Feed(Monarch, VassalAcct, Vassal, 10 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();
            Feed(Monarch, VassalAcct, Vassal, 10 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(1, store.Batches.Count, "the second interval waits for the first batch's callback");

            store.CompleteSaves(true);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(2, store.Batches.Count);
            Assert.AreEqual(20UL, store.Batches[1].Rows.Single().Credited);
        }

        [TestMethod]
        public void OnShutdown_ForcesFlush_BeforeTheInterval()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            Feed(Monarch, VassalAcct, Vassal, 30 * OnePoint);
            Advance(1);
            AvatarManager.Tick();
            Assert.AreEqual(0, store.Batches.Count);

            Feed(Monarch, VassalAcct, Vassal, 5 * OnePoint); // still in the queue at shutdown
            AvatarManager.OnShutdown();

            Assert.AreEqual(1, store.Batches.Count);
            Assert.AreEqual(35UL, store.Batches[0].Rows.Single().Credited);
            Assert.AreEqual(35UL, store.Batches[0].Avatars.Single().LifetimeXp);
        }

        // ---- founding --------------------------------------------------------------------------------------

        [TestMethod]
        public void Tick_FoundsAvatar_AtTwoActiveAccounts()
        {
            store.DeferInserts = true;
            AddRoster(2000, 200, Member(2001, 201, 1));
            Seen(200);
            Seen(201);
            // One active account only: the monarch plus an alt on the monarch's own account, and a stale vassal.
            AddRoster(3000, 300, Member(3001, 300, 1), Member(3002, 302, 1));
            Seen(300);
            Seen(302, daysAgo: 8);
            Init();

            AvatarManager.Tick();

            Assert.AreEqual(1, store.Inserts.Count, "only the allegiance with 2 distinct active accounts is founded");
            Assert.AreEqual(2000U, store.Inserts[0].Row.MonarchGuid);
            Assert.AreEqual((byte)AvatarBindState.Bound, store.Inserts[0].Row.State);
            Assert.IsNull(AvatarManager.ForMonarch(2000), "added to memory only in the callback");

            store.CompleteInsert(0, true, 50);

            var founded = AvatarManager.ForMonarch(2000);
            Assert.IsNotNull(founded);
            Assert.AreEqual(50U, founded.AvatarId);
            Assert.AreSame(founded, AvatarManager.Get(50));
            Assert.IsTrue(founded.Active);
            Assert.AreEqual(2000U, founded.KeeperGuid);
            Assert.AreEqual(1, AvatarManager.All.Count);
        }

        [TestMethod]
        public void Tick_StaleAllegianceRoster_DoesNotFoundAnAvatar()
        {
            // 3000 is a former monarch now sworn into 2000's allegiance: AllegianceManager.Allegiances still holds the
            // old allegiance, so the raw walk yields a roster for 3000 as well. Fed through the filter it must not found.
            var players = new Dictionary<uint, IPlayer>();
            var real = new object();
            var stale = new object();
            AllegianceNode Node(uint g, AllegianceNode monarch = null) => new AllegianceNode(new ObjectGuid(g), null, monarch, monarch);
            var realMonarch = Node(2000);
            var realVassal = Node(2001, realMonarch);
            var staleMonarch = Node(3000);
            var staleVassal = Node(3001, staleMonarch);
            foreach (var g in new uint[] { 2000, 2001, 3000, 3001 })
                players[g] = AvatarRostersTests.FakePlayer.Make(g, g);
            Seen(2000); Seen(2001); Seen(3000); Seen(3001);

            var entries = new List<(object Allegiance, AllegianceNode Monarch, IEnumerable<AllegianceNode> Members)>
            {
                (stale, staleMonarch, new[] { staleMonarch, staleVassal }),
                (real, realMonarch, new[] { realMonarch, realVassal }),
            };
            object CurrentOf(uint g) => g == 3000 || g == 2000 ? real : null;

            store.DeferInserts = true;
            AvatarManager.Initialize(store, () => T, () => Now, () => activity,
                () => AvatarRosters.FromAllegiances(entries, CurrentOf, p => false, n => players[n.PlayerGuid.Full]));

            AvatarManager.Tick();

            Assert.AreEqual(1, store.Inserts.Count, "only the real allegiance founds");
            Assert.AreEqual(2000U, store.Inserts[0].Row.MonarchGuid);
        }

        [TestMethod]
        public void Tick_DoesNotFoundTwice_WhileInsertPending()
        {
            store.DeferInserts = true;
            AddRoster(2000, 200, Member(2001, 201, 1));
            Seen(200);
            Seen(201);
            Init();

            AvatarManager.Tick();
            Advance(601);
            Seen(200);
            Seen(201);
            AvatarManager.Tick();
            Advance(601);
            AvatarManager.Tick();

            Assert.AreEqual(1, store.Inserts.Count, "a monarch with an Insert in flight is never founded again");

            store.CompleteInsert(0, true, 50);
            Advance(601);
            AvatarManager.Tick();

            Assert.AreEqual(1, store.Inserts.Count, "and once founded it has an Avatar");
        }

        [TestMethod]
        public void Insert_FalseButRowCommitted_AdoptsReloadedRow_NeverFoundsAgain()
        {
            store.DeferInserts = true;
            AddRoster(2000, 200, Member(2001, 201, 1));
            Seen(200);
            Seen(201);
            Init();
            AvatarManager.Tick();

            // The retry hit our own committed row: the store holds it, but Insert reported false.
            store.Avatars.Add(new AllegianceAvatar { AvatarId = 51, MonarchGuid = 2000, State = (byte)AvatarBindState.Bound });
            store.CompleteInsert(0, false, 0);

            Assert.AreEqual(51U, AvatarManager.ForMonarch(2000)?.AvatarId);

            Advance(601);
            AvatarManager.Tick();
            Assert.AreEqual(1, store.Inserts.Count);
        }

        [TestMethod]
        public void Insert_FalseAndReloadFails_BlocksFoundingForTheSession()
        {
            store.DeferInserts = true;
            AddRoster(2000, 200, Member(2001, 201, 1));
            Seen(200);
            Seen(201);
            Init();
            AvatarManager.Tick();

            store.LoadAllReturnsNull = true;
            store.CompleteInsert(0, false, 0);

            Advance(601);
            AvatarManager.Tick();

            Assert.IsNull(AvatarManager.ForMonarch(2000));
            Assert.AreEqual(1, store.Inserts.Count, "unknown outcome: never risk a second Avatar");
        }

        [TestMethod]
        public void Tick_NonBoundRowHoldingTheMonarchGuid_IsNotFoundedForever()
        {
            store.Avatars.Add(new AllegianceAvatar { AvatarId = 60, MonarchGuid = 2000, State = (byte)AvatarBindState.Absorbed, AbsorbedIntoId = 7 });
            AddRoster(2000, 200, Member(2001, 201, 1));
            Init();

            for (var i = 0; i < 3; i++)
            {
                Seen(200);
                Seen(201);
                AvatarManager.Tick();
                Advance(601);
            }
            AvatarManager.Tick();

            Assert.AreEqual(1, store.Inserts.Count, "the unique guid is held by a row the manager will not bind: one try, then blocked");
            Assert.IsNull(AvatarManager.ForMonarch(2000));
            Assert.AreEqual(AvatarBindState.Absorbed, AvatarManager.Get(60).State);
        }

        // ---- decay and Favor reservation -------------------------------------------------------------------

        [TestMethod]
        public void Tick_DecaysOncePerDay()
        {
            // No contribution ever, so the grace period counts as elapsed: 10% a day.
            SeedAvatar(favor: 1000, lastDecayDay: Today.AddDays(-1));
            AddRoster(Monarch, MonarchAcct, Member(Vassal, VassalAcct, 1));
            Seen(MonarchAcct);
            Seen(VassalAcct);
            Init();

            AvatarManager.Tick();
            Assert.AreEqual(900UL, AvatarManager.Get(AvatarId).Favor);
            Assert.AreEqual(Today, AvatarManager.Get(AvatarId).LastDecayDay);

            for (var i = 0; i < 5; i++)
            {
                Advance(3600);
                AvatarManager.Tick();
            }
            Assert.AreEqual(900UL, AvatarManager.Get(AvatarId).Favor, "once per UTC day");

            Advance(Day);
            AvatarManager.Tick();
            Assert.AreEqual(810UL, AvatarManager.Get(AvatarId).Favor);
            Assert.AreEqual(Today.AddDays(1), AvatarManager.Get(AvatarId).LastDecayDay);

            Advance(T.FlushSeconds);
            AvatarManager.Tick();
            Assert.AreEqual(810UL, store.Batches.Last().Avatars.Single().Favor, "the decayed Favor is flushed");
        }

        [TestMethod]
        public void Tick_WithinGrace_ActiveAvatar_DoesNotDecay()
        {
            SeedAvatar(favor: 1000, lastDecayDay: Today.AddDays(-1), lastContribution: Today.AddDays(-1));
            AddRoster(Monarch, MonarchAcct, Member(Vassal, VassalAcct, 1));
            Seen(MonarchAcct);
            Seen(VassalAcct);
            Init();

            AvatarManager.Tick();

            Assert.AreEqual(1000UL, AvatarManager.Get(AvatarId).Favor);
            Assert.AreEqual(Today, AvatarManager.Get(AvatarId).LastDecayDay);
        }

        [TestMethod]
        public void Decay_BetweenReserveAndCommit_NeverUnderflows()
        {
            SeedActiveAllegiance(favor: 1000);
            Init();
            AvatarManager.Tick();

            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 1000));

            Advance(Day);
            AvatarManager.Tick();

            var avatar = AvatarManager.Get(AvatarId);
            Assert.AreEqual(1000UL, avatar.Favor, "reserved Favor never decays");
            Assert.AreEqual(1000UL, avatar.ReservedFavor);

            AvatarManager.CommitDebit(AvatarId, 1000);

            Assert.AreEqual(0UL, avatar.Favor);
            Assert.AreEqual(0UL, avatar.ReservedFavor);

            // Saturates rather than wrapping when asked for more than is there.
            AvatarManager.CommitDebit(AvatarId, 5);
            Assert.AreEqual(0UL, avatar.Favor);
            Assert.AreEqual(0UL, avatar.ReservedFavor);
        }

        [TestMethod]
        public void Decay_PartialReserve_DecaysOnlyTheUnreservedPart()
        {
            SeedActiveAllegiance(favor: 1000);
            Init();
            AvatarManager.Tick();

            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 400));
            Advance(Day);
            AvatarManager.Tick();

            Assert.AreEqual(400UL + 540UL, AvatarManager.Get(AvatarId).Favor, "600 unreserved decays 10% to 540");
        }

        [TestMethod]
        public void TryReserveFavor_Insufficient_ReturnsFalse()
        {
            SeedActiveAllegiance(favor: 100);
            Init();
            AvatarManager.Tick();

            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 60));
            Assert.IsFalse(AvatarManager.TryReserveFavor(AvatarId, 50), "only 40 is still available");
            Assert.AreEqual(60UL, AvatarManager.Get(AvatarId).ReservedFavor);
            Assert.AreEqual(100UL, AvatarManager.Get(AvatarId).Favor);
            Assert.IsFalse(AvatarManager.TryReserveFavor(999, 1), "an unknown Avatar reserves nothing");
        }

        [TestMethod]
        public void ReleaseReservation_RestoresAvailable()
        {
            SeedActiveAllegiance(favor: 100);
            Init();
            AvatarManager.Tick();

            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 60));
            AvatarManager.ReleaseReservation(AvatarId, 60);

            Assert.AreEqual(0UL, AvatarManager.Get(AvatarId).ReservedFavor);
            Assert.AreEqual(100UL, AvatarManager.Get(AvatarId).Favor, "a release refunds without spending");
            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 100));

            AvatarManager.ReleaseReservation(AvatarId, 500);
            Assert.AreEqual(0UL, AvatarManager.Get(AvatarId).ReservedFavor, "saturates at 0");
        }

        [TestMethod]
        public void CommitDebit_IsFlushedAsTheNewAbsoluteFavor()
        {
            SeedActiveAllegiance(favor: 100);
            Init();
            AvatarManager.Tick();

            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 70));
            AvatarManager.CommitDebit(AvatarId, 70);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(30UL, store.Batches.Single().Avatars.Single().Favor);
        }

        // ---- RefundFavor (Allegiance Gauntlet admin refund) -------------------------------------------------

        [TestMethod]
        public void RefundFavor_CreditsTheCommittedPrice_AndFlushesTheAbsoluteFavor()
        {
            SeedActiveAllegiance(favor: 5000);
            Init();
            AvatarManager.Tick();

            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 3000));
            AvatarManager.CommitDebit(AvatarId, 3000);
            Assert.AreEqual(2000UL, AvatarManager.Get(AvatarId).Favor, "fixture: the price is spent");

            Assert.IsTrue(AvatarManager.RefundFavor(AvatarId, 3000));

            var avatar = AvatarManager.Get(AvatarId);
            Assert.AreEqual(5000UL, avatar.Favor, "the committed price is back");
            Assert.AreEqual(0UL, avatar.ReservedFavor, "a refund touches no reservation");

            // Pin the consumer: the persisted row carries the refunded absolute value.
            Advance(T.FlushSeconds);
            AvatarManager.Tick();
            Assert.AreEqual(5000UL, store.Batches.Single().Avatars.Single().Favor);
        }

        [TestMethod]
        public void LiveGauntletFavor_RefundCommitted_IsAvatarManagerRefundFavor()
        {
            // The adapter GauntletRun.Abort(refund: true) calls once wave 1 has committed the price. It used to answer false.
            SeedActiveAllegiance(favor: 5000);
            Init();
            AvatarManager.Tick();

            var favor = new ACE.Server.Avatars.Gauntlet.LiveGauntletFavor();
            Assert.IsTrue(AvatarManager.TryReserveFavor(AvatarId, 3000));
            favor.Commit(AvatarId, 3000);
            Assert.AreEqual(2000UL, AvatarManager.Get(AvatarId).Favor);

            Assert.IsTrue(favor.RefundCommitted(AvatarId, 3000));
            Assert.AreEqual(5000UL, AvatarManager.Get(AvatarId).Favor, "the committed price is credited back");

            Assert.IsFalse(favor.RefundCommitted(999, 3000), "an unknown Avatar cannot be refunded");
        }

        [TestMethod]
        public void RefundFavor_RespectsTheFavorCap()
        {
            var cap = (ulong)T.FavorCap;
            SeedActiveAllegiance(favor: cap - 1000);
            Init();
            AvatarManager.Tick();

            // 3000 comes back to an Avatar that is 1000 under the cap: only 1000 fits.
            Assert.IsTrue(AvatarManager.RefundFavor(AvatarId, 3000));
            Assert.AreEqual(cap, AvatarManager.Get(AvatarId).Favor, "min(cap, favor + amount)");

            // already at the cap: nothing more, and the call still reports the Avatar was found
            Assert.IsTrue(AvatarManager.RefundFavor(AvatarId, 1));
            Assert.AreEqual(cap, AvatarManager.Get(AvatarId).Favor);

            // saturating, never wrapping
            Assert.IsTrue(AvatarManager.RefundFavor(AvatarId, ulong.MaxValue));
            Assert.AreEqual(cap, AvatarManager.Get(AvatarId).Favor);
        }

        // ---- the per-Avatar Favor cap scales with the Avatar's largest price (owner ruling 2026-11-26) ----------------

        /// <summary>Avatar 7 on a roster of <paramref name="r"/> active accounts in a server of <paramref name="s"/>.</summary>
        private void SeedScaledAllegiance(int r, int s, ulong favor)
        {
            SeedAvatar(favor: favor);
            rosters.Add(AvatarScaleFixture.Roster(activity, r, s, Monarch, MonarchAcct, Vassal, VassalAcct, Start));
        }

        [TestMethod]
        public void FavorCapFor_ReadsTheAvatarsRosterAndTheServerCount()
        {
            SeedScaledAllegiance(73, 230, 0);
            Init();
            AvatarManager.Tick();
            var state = AvatarManager.Get(AvatarId);

            Assert.AreEqual(230, AvatarManager.ServerActiveAccounts());
            Assert.AreEqual(30_718UL, AvatarManager.FavorCapFor(state, T), "2 x the 15,359 announced and Gauntlet price");

            state.ActiveAccounts = 2;
            Assert.AreEqual(20_000UL, AvatarManager.FavorCapFor(state, T), "a small roster: the flat cap");
        }

        [TestMethod]
        public void Tick_CreditStopsAtThePerAvatarCap_NotTheFlatCap()
        {
            SeedScaledAllegiance(73, 230, favor: 30_500);
            Init();
            AvatarManager.Tick();

            // 1000 points x the roster scale 51.196/73 = 701 points; the account-day curve gives 500 + 201 x 0.25 = 550 Favor.
            Feed(Monarch, MonarchAcct, Monarch, 1000 * OnePoint);
            Advance(61);
            AvatarManager.Tick();

            var state = AvatarManager.Get(AvatarId);
            Assert.AreEqual(30_718UL, state.Favor, "30,500 + 550 is clamped to the per-Avatar cap; the flat 20,000 would have clamped it lower");
        }

        [TestMethod]
        public void Tick_SmallRoster_StillStopsAtTheFlatCap()
        {
            SeedActiveAllegiance(favor: 19_900);
            Init();
            AvatarManager.Tick();

            Feed(Monarch, MonarchAcct, Monarch, 1000 * OnePoint);
            Advance(61);
            AvatarManager.Tick();

            Assert.AreEqual(20_000UL, AvatarManager.Get(AvatarId).Favor, "R = 2: the cap is the flat 20,000");
        }

        [TestMethod]
        public void Tick_CreditNeverLowersFavor_WhenTheRosterShrinksAndTheCapFalls()
        {
            SeedScaledAllegiance(73, 230, favor: 30_718);
            Init();
            AvatarManager.Tick();
            var state = AvatarManager.Get(AvatarId);
            Assert.AreEqual(30_718UL, AvatarManager.FavorCapFor(state, T));

            // The roster shrinks to 20 accounts (the other 53 stay active on the server, so S is still 230).
            rosters.Clear();
            rosters.Add(AvatarScaleFixture.Roster(activity, 20, 20, Monarch, MonarchAcct, Vassal, VassalAcct, Start));
            Advance(3700); // past the recompute interval
            Seen(MonarchAcct);
            AvatarManager.Tick();
            Assert.AreEqual(20, state.ActiveAccounts, "the recompute read the smaller roster");
            Assert.AreEqual(20_000UL, AvatarManager.FavorCapFor(state, T), "2 x 6,000 is under the flat floor");

            Feed(Monarch, MonarchAcct, Monarch, 1000 * OnePoint);
            Advance(61);
            AvatarManager.Tick();

            Assert.AreEqual(30_718UL, AvatarManager.Get(AvatarId).Favor, "crediting stops growth at the cap but never takes Favor away");
        }

        [TestMethod]
        public void PriceInputs_ReadsRAndSTogether()
        {
            SeedScaledAllegiance(73, 230, 0);
            Init();
            AvatarManager.Tick();

            Assert.AreEqual((73, 230), AvatarManager.PriceInputs(AvatarManager.Get(AvatarId)));
            Assert.AreEqual((0, 230), AvatarManager.PriceInputs(null));
        }

        [TestMethod]
        public void RefundFavor_UsesThePerAvatarCap()
        {
            SeedScaledAllegiance(73, 230, favor: 30_000);
            Init();
            AvatarManager.Tick();

            Assert.IsTrue(AvatarManager.RefundFavor(AvatarId, 3_000));

            Assert.AreEqual(30_718UL, AvatarManager.Get(AvatarId).Favor, "min(30,718, 33,000)");
        }

        [TestMethod]
        public void RefundFavor_NeverLowersFavorAboveTheCap_AndUnknownAvatarIsFalse()
        {
            // a cap cut at runtime below the Avatar's Favor: the refund adds nothing but also takes nothing away
            var lowCap = T with { FavorCap = 100 }; // the cap falls to the 1000 floor price (R = 2), under the Avatar's Favor
            SeedActiveAllegiance(favor: 5000);
            AvatarManager.Initialize(store, () => lowCap, () => Now, () => activity, () => rosters);
            AvatarManager.Tick();

            var before = AvatarManager.Get(AvatarId).Favor;
            Assert.IsTrue(AvatarManager.RefundFavor(AvatarId, 50));
            Assert.AreEqual(before, AvatarManager.Get(AvatarId).Favor);

            Assert.IsFalse(AvatarManager.RefundFavor(999, 10), "an unknown Avatar credits nothing");
        }

        // ---- keeper ----------------------------------------------------------------------------------------

        [TestMethod]
        public void Tick_RecomputesKeeper_IdleMonarch_ActingKeeper()
        {
            SeedAvatar();
            AddRoster(Monarch, MonarchAcct, Member(Vassal, VassalAcct, 1), Member(1002, 102, 1));
            Seen(MonarchAcct, daysAgo: 31);
            Seen(VassalAcct);
            Seen(102);
            Init();

            AvatarManager.Tick();

            var avatar = AvatarManager.Get(AvatarId);
            Assert.AreEqual(Vassal, avatar.KeeperGuid, "equal rank, equal depth: lower guid acts");
            Assert.IsTrue(avatar.KeeperIsActing);
            Assert.IsTrue(avatar.Active, "two active vassal accounts");
        }

        [TestMethod]
        public void Tick_ActiveFlip_BumpsVersion()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            var avatar = AvatarManager.Get(AvatarId);
            Assert.IsTrue(avatar.Active);
            var before = avatar.Version;

            Advance(8 * Day); // both accounts fall out of the window
            AvatarManager.Tick();

            Assert.IsFalse(avatar.Active);
            Assert.IsTrue(avatar.Version > before);
        }

        // ---- startup failure and the kill path -------------------------------------------------------------

        [TestMethod]
        public void Initialize_LoadFailure_DisablesManager_NoFounding()
        {
            store.LoadAllReturnsNull = true;
            AddRoster(2000, 200, Member(2001, 201, 1));
            Seen(200);
            Seen(201);
            Init();

            Assert.IsFalse(AvatarManager.Enabled);

            Feed(2000, 200, 2000, 100 * OnePoint);
            AvatarManager.Tick();
            Advance(601);
            AvatarManager.Tick();
            AvatarManager.OnShutdown();

            Assert.AreEqual(0, store.Inserts.Count, "an empty start would re-found every Avatar");
            Assert.AreEqual(0, store.Batches.Count);
            Assert.AreEqual(0, AvatarManager.All.Count);
            Assert.IsFalse(AvatarManager.TryReserveFavor(AvatarId, 0));
        }

        [TestMethod]
        public void Initialize_ContributionLoadFailure_DisablesManager()
        {
            SeedActiveAllegiance();
            store.ContributionsReturnNull = true;
            Init();

            Assert.IsFalse(AvatarManager.Enabled);
            Assert.IsNull(AvatarManager.Get(AvatarId));

            AvatarManager.Tick();
            Assert.AreEqual(0, store.Batches.Count + store.Inserts.Count);
        }

        [TestMethod]
        public void Initialize_Success_Enabled_LoadsAvatarsAndContributionWindow()
        {
            SeedActiveAllegiance();
            Init();

            Assert.IsTrue(AvatarManager.Enabled);
            Assert.AreEqual(Today.AddDays(-T.TopContributorsDays), store.ContributionsSinceAsked);
            Assert.AreEqual(AvatarId, AvatarManager.ForMonarch(Monarch)?.AvatarId);
        }

        [TestMethod]
        public void Enqueue_NeverTouchesStore()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            // Four kill threads released together, racing a fifth that ticks with the clock advancing. The store
            // throws (and counts) if any KILL thread reaches it; the ticking thread is allowed to flush.
            // Each producer stops halfway until the ticker has run MinTicks ticks, so at least that many ticks land
            // inside the producer window, never all before or after it.
            const int threads = 4;
            const int perThread = 250;
            const int MinTicks = 10;
            var producersDone = 0;
            var ticks = 0;
            var ticksInWindow = 0;
            var stalled = 0;
            using (var barrier = new Barrier(threads + 1))
            {
                var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
                {
                    store.ForbidCurrentThread();
                    barrier.SignalAndWait();
                    for (var i = 0; i < perThread; i++)
                    {
                        if (i == perThread / 2)
                        {
                            var deadline = DateTime.UtcNow.AddSeconds(30);
                            while (Volatile.Read(ref ticks) < MinTicks)
                            {
                                if (DateTime.UtcNow > deadline)
                                {
                                    Interlocked.Increment(ref stalled);
                                    break;
                                }
                                Thread.Yield();
                            }
                        }
                        Feed(Monarch, VassalAcct, Vassal, OnePoint / 10);
                    }
                    Interlocked.Increment(ref producersDone);
                })).ToList();

                var ticker = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    while (Volatile.Read(ref producersDone) < threads)
                    {
                        Advance(1);
                        AvatarManager.Tick();
                        Interlocked.Increment(ref ticks);
                        if (Volatile.Read(ref producersDone) == 0)
                            Interlocked.Increment(ref ticksInWindow);
                    }
                });

                workers.ForEach(w => w.Start());
                ticker.Start();
                workers.ForEach(w => w.Join());
                ticker.Join();
            }

            Assert.AreEqual(0, stalled, "a producer gave up waiting for the ticker");
            Assert.IsTrue(ticksInWindow >= MinTicks, $"only {ticksInWindow} ticks ran while every producer was still feeding");
            Assert.AreEqual(0, store.CallsWhileThrowing, "Enqueue reached the store");

            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(100UL, AvatarManager.Get(AvatarId).LifetimeXp, "all 1000 events of 0.1 point were drained and credited once");
            Assert.AreEqual(100UL, store.Batches.Last().Rows.Single().Credited, "the last batch carries the absolute total");
        }

        [TestMethod]
        public void Flush_DeferredBatchFails_NextIntervalCarriesTheCombinedTotal()
        {
            SeedActiveAllegiance();
            store.DeferSaves = true;
            Init();
            AvatarManager.Tick();

            Feed(Monarch, VassalAcct, Vassal, 10 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick(); // batch 1 (10) handed over, callback pending
            Feed(Monarch, VassalAcct, Vassal, 10 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick(); // credited to 20, flush waits

            store.CompleteSaves(false);
            store.DeferSaves = false;
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            Assert.AreEqual(2, store.Batches.Count);
            Assert.AreEqual(10UL, store.Batches[0].Rows.Single().Credited);
            Assert.AreEqual(1, store.Batches.Count(b => b.Rows.Any(r => r.Credited == 20)), "exactly one batch carries 20");
            Assert.AreEqual(20UL, store.Batches[1].Avatars.Single().LifetimeXp);
        }

        [TestMethod]
        public void OnShutdown_WithBatchInFlight_CarriesItsKeys_SoALaterFailureLosesNothing()
        {
            SeedActiveAllegiance();
            store.DeferSaves = true;
            Init();
            AvatarManager.Tick();

            Feed(Monarch, VassalAcct, Vassal, 40 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick(); // batch 1 in flight

            AvatarManager.OnShutdown();
            store.CompleteSaves(false); // batch 1 fails after shutdown

            Assert.AreEqual(2, store.Batches.Count, "the forced flush is issued although batch 1 is in flight");
            Assert.AreEqual(40UL, store.Batches[1].Rows.Single().Credited, "batch 2 carries batch 1's row");
            Assert.AreEqual(40UL, store.Batches[1].Avatars.Single().LifetimeXp, "and its Avatar");
        }

        [TestMethod]
        public void Tick_DayRollover_YesterdaysKeyUsesYesterdaysCap_TodayStartsAtZero()
        {
            SeedActiveAllegiance();
            Init();
            AvatarManager.Tick();

            // Fill yesterday's soft cap.
            Feed(Monarch, VassalAcct, Vassal, 500 * OnePoint);
            Advance(T.FlushSeconds);
            AvatarManager.Tick();

            // 23:59:00: an interval boundary, then 100 more points drained at 23:59:30, not yet credited.
            Now = Start + (16 * 3600 - 60);
            AvatarManager.Tick();
            Feed(Monarch, VassalAcct, Vassal, 100 * OnePoint);
            Advance(30);
            AvatarManager.Tick();

            // 00:00:30 the next day: 100 points for today, then the credit step runs for both keys.
            Advance(60);
            Feed(Monarch, VassalAcct, Vassal, 100 * OnePoint);
            AvatarManager.Tick();

            var rows = store.Batches.Last().Rows;
            Assert.AreEqual(525UL, rows.Single(r => r.Day == Today).Credited, "yesterday's key: 100 past the soft cap credits 25");
            Assert.AreEqual(100UL, rows.Single(r => r.Day == Today.AddDays(1)).Credited, "today's cap starts at 0");
            Assert.AreEqual(625UL, AvatarManager.Get(AvatarId).LifetimeXp);
        }

        // ---- entity mapping --------------------------------------------------------------------------------

        [TestMethod]
        public void ShardAvatarStore_Mapping_RoundTripsEveryPersistedField()
        {
            var row = new AllegianceAvatar
            {
                AvatarId = 9, MonarchGuid = 0x50000001, State = (byte)AvatarBindState.Absorbed, LifetimeXp = 123456789012, Favor = 4321,
                HeirGuid = 0x50000002, SeatHouseGuid = 0x7A000001, SeatCell = 0xA9B40001, SeatInstance = 3,
                SeatX = 1.5f, SeatY = 2.5f, SeatZ = 3.5f, SeatQw = 0.5f, SeatQx = 0.25f, SeatQy = 0.125f, SeatQz = 0.0625f,
                SeatedAt = new DateTime(2027, 1, 10, 12, 0, 0), AbsorbedIntoId = 4,
                LastContributionAt = new DateTime(2027, 1, 14, 6, 30, 0), LastDecayDay = new DateTime(2027, 1, 14),
                LookJson = "{\"v\":1}",
            };

            var state = ShardAvatarStore.FromEntity(row);
            Assert.AreEqual(AvatarBindState.Absorbed, state.State);
            Assert.AreEqual(0x7A000001U, state.Seat.HouseGuid);
            Assert.AreEqual(0xA9B40001U, state.Seat.Position.Cell); // outdoor cell 1 matches x 1.5, y 2.5: Position recomputes outdoor cells from the coordinates
            Assert.AreEqual(3U, state.Seat.Position.Instance);
            Assert.AreEqual(DateTimeKind.Utc, state.LastContributionUtc.Value.Kind);

            var back = ShardAvatarStore.ToEntity(state);
            foreach (var p in typeof(AllegianceAvatar).GetProperties().Where(p => p.Name != nameof(AllegianceAvatar.CreatedAt) && p.Name != nameof(AllegianceAvatar.UpdatedAt)))
                Assert.AreEqual(p.GetValue(row), p.GetValue(back), p.Name);

            var unseated = ShardAvatarStore.ToEntity(ShardAvatarStore.FromEntity(new AllegianceAvatar { AvatarId = 10, SeatHouseGuid = 1 }));
            Assert.IsNull(unseated.SeatHouseGuid, "a partial seat is not restored");
            Assert.IsNull(unseated.SeatCell);
        }

        // ---- fake store ------------------------------------------------------------------------------------

        private sealed class FakeStore : IAvatarStore
        {
            private readonly object sync = new object();

            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatarContribution> Contributions = new List<AllegianceAvatarContribution>();
            public readonly List<(List<AllegianceAvatar> Avatars, List<AllegianceAvatarContribution> Rows)> Batches = new();
            public readonly List<(AllegianceAvatar Row, Action<bool, uint> Done)> Inserts = new();
            public readonly Queue<bool> SaveResults = new Queue<bool>();
            private readonly List<Action<bool>> deferredSaves = new List<Action<bool>>();

            public bool LoadAllReturnsNull;
            public bool ContributionsReturnNull;
            public bool DeferInserts;
            public bool DeferSaves;
            public DateTime? ContributionsSinceAsked;

            private volatile bool throwOnAnyCall;
            public bool ThrowOnAnyCall { get => throwOnAnyCall; set => throwOnAnyCall = value; }
            private int callsWhileThrowing;
            public int CallsWhileThrowing => Volatile.Read(ref callsWhileThrowing);

            private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> forbidden = new();

            /// <summary>Any later store call from the calling thread throws and is counted.</summary>
            public void ForbidCurrentThread() => forbidden[Environment.CurrentManagedThreadId] = true;

            private void Guard()
            {
                if (!throwOnAnyCall && !forbidden.ContainsKey(Environment.CurrentManagedThreadId))
                    return;
                Interlocked.Increment(ref callsWhileThrowing);
                throw new InvalidOperationException("the store was called while it must not be");
            }

            public List<AllegianceAvatar> LoadAll()
            {
                Guard();
                lock (sync)
                    return LoadAllReturnsNull ? null : Avatars.Select(Copy).ToList();
            }

            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc)
            {
                Guard();
                lock (sync)
                {
                    ContributionsSinceAsked = dayUtc;
                    return ContributionsReturnNull ? null : Contributions.Where(c => c.Day >= dayUtc).ToList();
                }
            }

            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done)
            {
                Guard();
                bool result;
                lock (sync)
                {
                    Batches.Add((a.Select(Copy).ToList(), r.Select(Copy).ToList()));
                    if (DeferSaves)
                    {
                        deferredSaves.Add(done);
                        return;
                    }
                    result = SaveResults.Count > 0 ? SaveResults.Dequeue() : true;
                }
                done(result);
            }

            public void Insert(AllegianceAvatar row, Action<bool, uint> done)
            {
                Guard();
                bool ok;
                lock (sync)
                {
                    Inserts.Add((Copy(row), done));
                    if (DeferInserts)
                        return;
                    // allegiance_avatar_monarch_uidx: a guid already held by a stored row fails the insert.
                    ok = !Avatars.Any(a => a.MonarchGuid == row.MonarchGuid);
                }
                done(ok, ok ? (uint)(100 + Inserts.Count) : 0);
            }

            public void CompleteInsert(int index, bool ok, uint id)
            {
                Action<bool, uint> done;
                lock (sync)
                    done = Inserts[index].Done;
                done(ok, id);
            }

            public void CompleteSaves(bool ok)
            {
                List<Action<bool>> pending;
                lock (sync)
                {
                    pending = deferredSaves.ToList();
                    deferredSaves.Clear();
                }
                pending.ForEach(d => d(ok));
            }

            private static AllegianceAvatar Copy(AllegianceAvatar a) => new AllegianceAvatar
            {
                AvatarId = a.AvatarId, MonarchGuid = a.MonarchGuid, State = a.State, LifetimeXp = a.LifetimeXp, LevelXp = a.LevelXp, Favor = a.Favor,
                HeirGuid = a.HeirGuid, SeatHouseGuid = a.SeatHouseGuid, SeatCell = a.SeatCell, SeatInstance = a.SeatInstance,
                SeatX = a.SeatX, SeatY = a.SeatY, SeatZ = a.SeatZ, SeatQw = a.SeatQw, SeatQx = a.SeatQx, SeatQy = a.SeatQy, SeatQz = a.SeatQz,
                SeatedAt = a.SeatedAt, AbsorbedIntoId = a.AbsorbedIntoId, LastContributionAt = a.LastContributionAt, LastDecayDay = a.LastDecayDay,
                CreatedAt = a.CreatedAt, UpdatedAt = a.UpdatedAt,
            };

            private static AllegianceAvatarContribution Copy(AllegianceAvatarContribution c) => new AllegianceAvatarContribution
            {
                AvatarId = c.AvatarId, AccountId = c.AccountId, Day = c.Day, RawXp = c.RawXp, Credited = c.Credited, LastCharacterId = c.LastCharacterId,
            };
        }
    }
}
