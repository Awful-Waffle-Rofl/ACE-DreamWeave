using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// Live level-up of an Avatar: the manager collects rises while draining the level pool feed and hands them to
    /// AvatarLevelUp after its lock is released. The dispatch seam is the runtime consumer pinned here. No PropertyManager
    /// key is read: the tuning is passed in, the chart is a synthetic seam, and the announcer and NPC lookups are seams, all
    /// restored in cleanup.
    /// </summary>
    [TestClass]
    public class AvatarLevelUpTests
    {
        private const double Start = 1_800_000_000.0; // 2027-01-15 08:00:00 UTC
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Chart totals by level: L2=10, L3=26, L4=51, L5=92, L6=158 (the maximum). Fraction 1.0, so a pool grant is the raw XP.
        private static readonly ulong[] Chart = { 0, 0, 10, 26, 51, 92, 158 };
        // Events unlock at level 3.
        private static readonly AvatarTuning T = AvatarTuning.Defaults with { LevelXpFraction = 1.0, EventMinLevel = 3 };

        private const uint AvatarId = 7;
        private const uint OtherAvatarId = 8;
        private const uint Monarch = 1000;
        private const uint MonarchAcct = 100;
        private const uint Vassal = 1001;
        private const uint VassalAcct = 101;
        private const uint OtherMonarch = 2000;
        private const uint OtherMonarchAcct = 200;
        private const uint OtherVassal = 2001;
        private const uint OtherVassalAcct = 201;

        private AvatarTuning cur;
        private Store store;
        private double now;
        private AccountActivityIndex activity;
        private List<AvatarRoster> rosters;
        private List<(AvatarLevelUpEvent Event, bool SyncHeld)> dispatched;
        private List<(uint Monarch, string Text)> announced;

        [TestInitialize]
        public void Setup()
        {
            store = new Store();
            cur = T;
            now = Start;
            activity = new AccountActivityIndex();
            rosters = new List<AvatarRoster>();
            dispatched = new List<(AvatarLevelUpEvent, bool)>();
            announced = new List<(uint, string)>();

            AvatarCurves.ChartSource = () => Chart;
            AvatarLevelUp.GauntletMinLevel = () => 1000;
            AvatarLevelUp.Dispatch = (e, t) => dispatched.Add((e, AvatarManager.SyncHeldByCurrentThread));
            AvatarAnnouncer.Sink = (m, text) => announced.Add((m, text));
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarLevelUp.ResetSeams();
            AvatarCurves.ResetSeams();
            AvatarAnnouncer.Sink = null;
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        private void Seed(uint id, uint monarch, ulong levelXp = 0, ulong lifetime = 0)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = id, MonarchGuid = monarch, State = (byte)AvatarBindState.Bound, LevelXp = levelXp, LifetimeXp = lifetime, LastDecayDay = Today,
            });
        }

        private void Roster(uint monarch, uint monarchAcct, uint vassal, uint vassalAcct)
        {
            rosters.Add(new AvatarRoster(monarch, monarchAcct, false, new List<KeeperCandidate>
            {
                new KeeperCandidate(monarch, monarchAcct, 1, 0, 0),
                new KeeperCandidate(vassal, vassalAcct, 1, 0, 1),
            }));
            activity.Touch(monarchAcct, now);
            activity.Touch(vassalAcct, now);
        }

        private void Init()
        {
            AvatarManager.Initialize(store, () => cur, () => now, () => activity, () => rosters);
            AvatarManager.Tick();
        }

        private static void Feed(uint monarch, long rawXp) => AvatarManager.EnqueueLevel(monarch, rawXp);

        private void FlushTick()
        {
            now += T.FlushSeconds;
            AvatarManager.Tick();
        }

        [TestMethod]
        public void Flush_CrossingALevel_DispatchesOnceWithOldAndNewLevel()
        {
            Seed(AvatarId, Monarch, levelXp: 5);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();

            Feed(Monarch, 10); // 5 -> 15: level 1 -> 2
            FlushTick();

            Assert.AreEqual(1, dispatched.Count);
            Assert.AreEqual(new AvatarLevelUpEvent(AvatarId, Monarch, 1, 2), dispatched[0].Event);
        }

        [TestMethod]
        public void Flush_NotCrossingALevel_DispatchesNothing()
        {
            Seed(AvatarId, Monarch, levelXp: 12); // level 2, next at 26
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();

            Feed(Monarch, 5); // 17
            FlushTick();

            Assert.AreEqual(17UL, AvatarManager.Get(AvatarId).LevelXp);
            Assert.AreEqual(0, dispatched.Count);
        }

        [TestMethod]
        public void Flush_MultiLevelJump_IsOneEvent()
        {
            Seed(AvatarId, Monarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();

            Feed(Monarch, 100); // 0 -> 100: level 5
            FlushTick();

            Assert.AreEqual(1, dispatched.Count);
            Assert.AreEqual(new AvatarLevelUpEvent(AvatarId, Monarch, 1, 5), dispatched[0].Event);
        }

        [TestMethod]
        public void Flush_TwoGrantsInOneFlush_ShareOneEventCoveringTheWholeRise()
        {
            Seed(AvatarId, Monarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();

            Feed(Monarch, 12); // level 2 alone
            Feed(Monarch, 40); // 52 together: level 4
            FlushTick();

            Assert.AreEqual(1, dispatched.Count);
            Assert.AreEqual(new AvatarLevelUpEvent(AvatarId, Monarch, 1, 4), dispatched[0].Event);
        }

        [TestMethod]
        public void Dispatch_RunsOutsideTheManagerLock_AndAfterTheTickCredit()
        {
            Seed(AvatarId, Monarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();

            Feed(Monarch, 100);
            FlushTick();

            Assert.AreEqual(1, dispatched.Count);
            Assert.IsFalse(dispatched[0].SyncHeld, "the dispatch touches NPCs, the world and chat, so the manager lock must be released");
        }

        [TestMethod]
        public void Absorb_RaisingTheTarget_DispatchesForTheTargetOnly_OutsideTheLock()
        {
            Seed(AvatarId, Monarch, levelXp: 400, lifetime: 400);
            Seed(OtherAvatarId, OtherMonarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Roster(OtherMonarch, OtherMonarchAcct, OtherVassal, OtherVassalAcct);
            Init();

            var added = AvatarManager.TryAbsorb(AvatarId, OtherAvatarId, null, null);

            Assert.AreEqual(100UL, added, "400 x 0.25 of the lifetime XP");
            Assert.AreEqual(100UL, AvatarManager.Get(OtherAvatarId).LevelXp, "400 x 0.25 of the level pool");
            Assert.AreEqual(1, dispatched.Count);
            Assert.AreEqual(new AvatarLevelUpEvent(OtherAvatarId, OtherMonarch, 1, 5), dispatched[0].Event);
            Assert.IsFalse(dispatched[0].SyncHeld);
        }

        /// <summary>Pool XP still queued for the source when the absorb runs (no tick in between) is drained first, so the target's share includes it.</summary>
        [TestMethod]
        public void Absorb_DrainsTheSourcesQueuedLevelFeed_BeforeComputingTheShare()
        {
            Seed(AvatarId, Monarch, levelXp: 400, lifetime: 400);
            Seed(OtherAvatarId, OtherMonarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Roster(OtherMonarch, OtherMonarchAcct, OtherVassal, OtherVassalAcct);
            Init();

            Feed(Monarch, 400); // queued, never ticked: the pool is 400 until the absorb drains it

            AvatarManager.TryAbsorb(AvatarId, OtherAvatarId, null, null);

            Assert.AreEqual(200UL, AvatarManager.Get(OtherAvatarId).LevelXp, "25% of (400 + the 400 still queued)");
        }

        [TestMethod]
        public void Absorb_NotCrossingALevel_DispatchesNothing()
        {
            Seed(AvatarId, Monarch, levelXp: 4, lifetime: 4);
            Seed(OtherAvatarId, OtherMonarch, levelXp: 20, lifetime: 20);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Roster(OtherMonarch, OtherMonarchAcct, OtherVassal, OtherVassalAcct);
            Init();

            Assert.AreEqual(1UL, AvatarManager.TryAbsorb(AvatarId, OtherAvatarId, null, null)); // 4 x 0.25
            Assert.AreEqual(21UL, AvatarManager.Get(OtherAvatarId).LevelXp);
            Assert.AreEqual(0, dispatched.Count);
        }

        [TestMethod]
        public void LevelUp_DoesNotBumpVersion()
        {
            Seed(AvatarId, Monarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();
            var version = AvatarManager.Get(AvatarId).Version;

            Feed(Monarch, 100);
            FlushTick();

            Assert.AreEqual(1, dispatched.Count);
            Assert.AreEqual(version, AvatarManager.Get(AvatarId).Version);
        }

        [TestMethod]
        public void Shutdown_DrainsAndDispatches()
        {
            Seed(AvatarId, Monarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();

            Feed(Monarch, 100);
            now += 5; // inside the flush interval: drained, not yet dispatched
            AvatarManager.Tick();
            Assert.AreEqual(0, dispatched.Count);

            AvatarManager.OnShutdown();

            Assert.AreEqual(1, dispatched.Count);
            Assert.AreEqual(new AvatarLevelUpEvent(AvatarId, Monarch, 1, 5), dispatched[0].Event);
            Assert.IsFalse(dispatched[0].SyncHeld);
        }

        [TestMethod]
        public void Flush_AtTheTopOfTheChart_DispatchesNothing()
        {
            Seed(AvatarId, Monarch, levelXp: 200); // level 6 = the chart's last level
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();

            Feed(Monarch, 100);
            FlushTick();

            Assert.AreEqual(300UL, AvatarManager.Get(AvatarId).LevelXp);
            Assert.AreEqual(0, dispatched.Count);
        }

        [TestMethod]
        public void Flush_TwoAvatarsRising_GiveTwoEvents()
        {
            Seed(AvatarId, Monarch);
            Seed(OtherAvatarId, OtherMonarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Roster(OtherMonarch, OtherMonarchAcct, OtherVassal, OtherVassalAcct);
            Init();

            Feed(Monarch, 100);
            Feed(OtherMonarch, 30);
            FlushTick();

            CollectionAssert.AreEquivalent(
                new[] { new AvatarLevelUpEvent(AvatarId, Monarch, 1, 5), new AvatarLevelUpEvent(OtherAvatarId, OtherMonarch, 1, 3) },
                dispatched.Select(d => d.Event).ToArray());
        }

        [TestMethod]
        public void ThrowingDispatch_DoesNotBreakTick_AndTheOtherEventIsStillDelivered()
        {
            Seed(AvatarId, Monarch);
            Seed(OtherAvatarId, OtherMonarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Roster(OtherMonarch, OtherMonarchAcct, OtherVassal, OtherVassalAcct);
            Init();

            var calls = 0;
            AvatarLevelUp.Dispatch = (e, t) =>
            {
                calls++;
                if (calls == 1)
                    throw new InvalidOperationException("boom");
                dispatched.Add((e, AvatarManager.SyncHeldByCurrentThread));
            };

            Feed(Monarch, 100);
            Feed(OtherMonarch, 30);
            FlushTick(); // must not throw

            Assert.AreEqual(2, calls, "both events were attempted");
            Assert.AreEqual(1, dispatched.Count, "the event after the throw is still delivered");

            // And a later flush still dispatches.
            Feed(Monarch, 100);
            FlushTick();
            Assert.AreEqual(2, dispatched.Count);
        }

        [TestMethod]
        public void Initialize_DropsAnUndispatchedEvent()
        {
            Seed(AvatarId, Monarch);
            Roster(Monarch, MonarchAcct, Vassal, VassalAcct);
            Init();

            var field = typeof(AvatarManager).GetField("levelUps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var list = (List<AvatarLevelUpEvent>)field.GetValue(null);
            list.Add(new AvatarLevelUpEvent(AvatarId, Monarch, 1, 2));

            Init(); // a re-initialize replaces the world the event described
            FlushTick(); // reaches the credit-and-dispatch step

            Assert.AreEqual(0, dispatched.Count);
        }

        // ---- announcement policy --------------------------------------------------------------------------------

        [TestMethod]
        public void ShouldAnnounce_Policy()
        {
            Assert.IsFalse(AvatarLevelUp.ShouldAnnounce(23, 24, 40, 75), "an ordinary rise is silent");
            Assert.IsTrue(AvatarLevelUp.ShouldAnnounce(24, 25, 40, 75), "a 25-level milestone announces");
            Assert.IsTrue(AvatarLevelUp.ShouldAnnounce(24, 26, 40, 75), "a jump over a milestone announces");
            Assert.IsFalse(AvatarLevelUp.ShouldAnnounce(25, 26, 40, 75), "just past a milestone is silent");
            Assert.IsTrue(AvatarLevelUp.ShouldAnnounce(39, 40, 40, 75), "crossing the event unlock announces");
            Assert.IsFalse(AvatarLevelUp.ShouldAnnounce(40, 41, 40, 75), "already past the unlock");
            Assert.IsTrue(AvatarLevelUp.ShouldAnnounce(74, 75, 40, 75), "crossing the Gauntlet unlock announces");
            Assert.IsFalse(AvatarLevelUp.ShouldAnnounce(30, 30, 40, 75));
            Assert.IsFalse(AvatarLevelUp.ShouldAnnounce(30, 29, 40, 75));
        }

        [TestMethod]
        public void Handle_BelowAnyMilestone_AppliesTheNpcLevelButAnnouncesNothing()
        {
            var applied = new List<int>();
            AvatarLevelUp.NpcsOf = id => new AvatarVendor[] { null };
            AvatarLevelUp.ApplyToNpc = (npc, level) => applied.Add(level);

            AvatarLevelUp.Handle(new AvatarLevelUpEvent(AvatarId, Monarch, 3, 4), T);

            CollectionAssert.AreEqual(new[] { 4 }, applied, "the NPC level updates on every rise");
            Assert.AreEqual(0, announced.Count);
        }

        [TestMethod]
        public void Handle_AMilestone_Announces_WithoutASuffixPastTheUnlocks()
        {
            AvatarLevelUp.Handle(new AvatarLevelUpEvent(AvatarId, Monarch, 24, 25), T);

            CollectionAssert.AreEqual(new[] { (Monarch, "Your Allegiance Avatar has reached level 25.") }, announced);
        }

        [TestMethod]
        public void Handle_AppendsTheEventUnlock_WhenTheJumpCrossesEventMinLevel()
        {
            AvatarLevelUp.Handle(new AvatarLevelUpEvent(AvatarId, Monarch, 2, 3), T);
            AvatarLevelUp.Handle(new AvatarLevelUpEvent(AvatarId, Monarch, 1, 5), T);

            var expected = "Your Allegiance Avatar has reached level {0}. It can now host Private World Events.";
            Assert.AreEqual(string.Format(expected, 3), announced[0].Text);
            Assert.AreEqual(string.Format(expected, 5), announced[1].Text);
        }

        [TestMethod]
        public void Handle_AppendsTheGauntletUnlock_WhenTheRiseCrossesItsGate()
        {
            AvatarLevelUp.GauntletMinLevel = () => 10;

            AvatarLevelUp.Handle(new AvatarLevelUpEvent(AvatarId, Monarch, 9, 10), T);

            Assert.AreEqual("Your Allegiance Avatar has reached level 10. It can now call an Allegiance Gauntlet.", announced.Single().Text);
        }

        [TestMethod]
        public void Handle_AThrowingNpcLookup_StillAnnounces()
        {
            AvatarLevelUp.NpcsOf = id => throw new InvalidOperationException("boom");

            AvatarLevelUp.Handle(new AvatarLevelUpEvent(AvatarId, Monarch, 24, 25), T);

            Assert.AreEqual(1, announced.Count);
        }

        [TestMethod]
        public void Handle_NoMonarch_AnnouncesNothing()
        {
            AvatarLevelUp.Handle(new AvatarLevelUpEvent(AvatarId, null, 24, 25), T);

            Assert.AreEqual(0, announced.Count);
        }

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.ToList();
            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => new List<AllegianceAvatarContribution>();
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done) => done(true);
            public void Insert(AllegianceAvatar row, Action<bool, uint> done) => done(true, 0);
        }
    }
}
