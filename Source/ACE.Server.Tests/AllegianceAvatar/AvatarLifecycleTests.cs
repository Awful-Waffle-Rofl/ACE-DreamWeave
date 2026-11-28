using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The lifecycle through the manager seam: every assertion reads the state the manager holds and the rows it hands
    /// the store at the next flush, not a classifier's output.
    /// </summary>
    [TestClass]
    public class AvatarLifecycleTests
    {
        private const double Start = 1_800_000_000.0; // 2027-01-15 08:00:00 UTC
        private const double Day = 86400.0;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        private static readonly AvatarTuning T = AvatarTuning.Defaults with { BaseRate = 0.000001 };

        private const uint AvatarA = 7;
        private const uint AvatarB = 8;
        private const uint Varric = 1000;      // monarch of A
        private const uint VarricAcct = 100;
        private const uint Brannoc = 1001;     // Varric's direct vassal
        private const uint BrannocAcct = 101;
        private const uint Cora = 1002;        // Brannoc's vassal
        private const uint CoraAcct = 102;
        private const uint Kell = 2000;        // monarch of B
        private const uint KellAcct = 200;
        private const uint VarricAlt = 3000;   // a solo character on Varric's account

        private Store store;
        private double now;
        private AccountActivityIndex activity;
        private Dictionary<uint, AvatarRoster> rosters;
        private List<(uint Monarch, string Text)> announced;
        private List<uint> despawned;
        private List<AvatarState> revalidated;
        private List<AvatarState> unseated;
        private Dictionary<uint, uint> monarchOf;
        private Dictionary<uint, AvatarMonarchStatus> monarchStatus;

        private static readonly Dictionary<uint, string> Names = new Dictionary<uint, string>
        {
            [Varric] = "Varric", [Brannoc] = "Brannoc", [Cora] = "Cora", [Kell] = "Kell", [VarricAlt] = "Varra",
        };

        [TestInitialize]
        public void Setup()
        {
            AvatarTestChart.Install();
            store = new Store();
            now = Start;
            activity = new AccountActivityIndex();
            rosters = new Dictionary<uint, AvatarRoster>();
            announced = new List<(uint, string)>();
            despawned = new List<uint>();
            revalidated = new List<AvatarState>();
            unseated = new List<AvatarState>();
            monarchOf = new Dictionary<uint, uint>();
            monarchStatus = new Dictionary<uint, AvatarMonarchStatus>();

            AvatarAnnouncer.Sink = (m, text) => announced.Add((m, text));
            AvatarNpcs.DespawnSummonedSink = id => despawned.Add(id);
            AvatarNpcs.RevalidateSeatSink = s => revalidated.Add(s);
            AvatarNpcs.UnseatSink = s => unseated.Add(s);
            AvatarLifecycle.NameOf = g => Names.TryGetValue(g, out var n) ? n : null;
            AvatarLifecycle.MonarchOf = g => monarchOf.TryGetValue(g, out var m) ? m : g;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarTestChart.Reset();
            AvatarAnnouncer.Sink = null;
            AvatarNpcs.DespawnSummonedSink = null;
            AvatarNpcs.RevalidateSeatSink = null;
            AvatarNpcs.UnseatSink = null;
            AvatarLifecycle.ResetSeams();
            AvatarManager.Initialize(new Store { LoadAllReturnsNull = true }, () => T, () => Start, () => new AccountActivityIndex(), () => new List<AvatarRoster>());
        }

        // ---- fixture -------------------------------------------------------------------------------------------

        /// <summary>Loads and runs the first tick (live index, Active, the first Keeper settle). Captures are cleared unless asked to keep them.</summary>
        private void Init(bool keepCaptures = false)
        {
            AvatarManager.Initialize(store, () => T, () => now, () => activity, () => rosters.Values.ToList(),
                m => rosters.TryGetValue(m, out var r) ? r : null,
                g => monarchStatus.TryGetValue(g, out var st) ? st : AvatarMonarchStatus.Monarch);
            AvatarManager.Tick();
            if (!keepCaptures)
                ClearCaptures();
        }

        private void ClearCaptures()
        {
            announced.Clear();
            despawned.Clear();
            revalidated.Clear();
            unseated.Clear();
        }

        private void Seen(uint account, double daysAgo = 0) => activity.Touch(account, now - daysAgo * Day);

        private static KeeperCandidate Member(uint guid, uint account, int depth, uint rank = 1, int officer = 0) =>
            new KeeperCandidate(guid, account, rank, officer, depth);

        private void Roster(uint monarch, uint monarchAcct, params KeeperCandidate[] vassals)
        {
            var members = new List<KeeperCandidate> { Member(monarch, monarchAcct, 0, rank: 5) };
            members.AddRange(vassals);
            rosters[monarch] = new AvatarRoster(monarch, monarchAcct, false, members);
        }

        private void SeedAvatar(uint id, uint monarch, ulong lifetime = 0, ulong favor = 0)
        {
            store.Avatars.Add(new AllegianceAvatar
            {
                AvatarId = id, MonarchGuid = monarch, State = (byte)AvatarBindState.Bound,
                LifetimeXp = lifetime, LevelXp = lifetime, Favor = favor, LastDecayDay = Today,
            });
        }

        /// <summary>Varric (idle 31 days) - Brannoc - Cora, both vassals active: Brannoc acts as Keeper.</summary>
        private void SeedIdleMonarchAllegiance()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1, rank: 3), Member(Cora, CoraAcct, 2, rank: 1));
            Seen(VarricAcct, daysAgo: 31);
            Seen(BrannocAcct);
            Seen(CoraAcct);
        }

        private void Flush()
        {
            now += T.FlushSeconds;
            AvatarManager.Tick();
        }

        private AllegianceAvatar LastRow(uint avatarId) =>
            store.Batches.SelectMany(b => b.Avatars).Last(a => a.AvatarId == avatarId);

        // ---- absorb and bind -----------------------------------------------------------------------------------

        /// <summary>The level pool absorbs like LifetimeXp: the target gains floor(absorb_fraction x the source's pool); the source keeps its own.</summary>
        [TestMethod]
        public void Absorb_AddsFloorOf25PercentOfTheSourcesLevelPool()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 500);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000, favor: 300);
            store.Avatars.Single(a => a.AvatarId == AvatarA).LevelXp = 1_001;
            store.Avatars.Single(a => a.AvatarId == AvatarB).LevelXp = 4_000;
            Init();

            var source = AvatarManager.Get(AvatarA);
            var target = AvatarManager.Get(AvatarB);

            Assert.IsTrue(AvatarLifecycle.Absorb(source, target));

            Assert.AreEqual(4_000UL + 250UL, target.LevelXp, "4000 + floor(0.25 x 1001)");
            Assert.AreEqual(1_001UL, source.LevelXp, "the source row keeps its own history");
        }

        [TestMethod]
        public void Absorb_Adds25Percent_DropsFavor_MarksAbsorbed()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 500);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000, favor: 300);
            Init();

            var source = AvatarManager.Get(AvatarA);
            var target = AvatarManager.Get(AvatarB);
            var sourceVersion = source.Version;
            var targetVersion = target.Version;

            Assert.IsTrue(AvatarLifecycle.Absorb(source, target));

            Assert.AreEqual(6_500UL, target.LifetimeXp, "4000 + 25% of 10000");
            Assert.AreEqual(300UL, target.Favor, "the target's Favor is untouched; the source's is not carried over");
            Assert.AreEqual(AvatarBindState.Absorbed, source.State);
            Assert.IsNull(source.MonarchGuid);
            Assert.AreEqual(AvatarB, source.AbsorbedIntoId);
            Assert.AreEqual(0UL, source.Favor);
            Assert.AreEqual(10_000UL, source.LifetimeXp, "the source row keeps its own history");
            Assert.IsTrue(source.Version > sourceVersion && target.Version > targetVersion);
            Assert.IsNull(AvatarManager.ForMonarch(Varric));
            Assert.AreSame(target, AvatarManager.ForMonarch(Kell));

            // Persisted by the next flush, as the absolute values.
            Flush();
            var srcRow = LastRow(AvatarA);
            var tgtRow = LastRow(AvatarB);
            Assert.AreEqual((byte)AvatarBindState.Absorbed, srcRow.State);
            Assert.IsNull(srcRow.MonarchGuid);
            Assert.AreEqual(AvatarB, srcRow.AbsorbedIntoId);
            Assert.AreEqual(0UL, srcRow.Favor);
            Assert.AreEqual(6_500UL, tgtRow.LifetimeXp);

            Assert.IsTrue(announced.Any(a => a.Monarch == Kell && a.Text.Contains("Varric's Avatar") && a.Text.Contains("absorbed") && a.Text.Contains("2500")), string.Join(" | ", announced));
            CollectionAssert.IsSubsetOf(new[] { AvatarA, AvatarB }, despawned);
            Assert.AreEqual(0, unseated.Count);
        }

        [TestMethod]
        public void Bind_TargetHasAvatar_Absorbs()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 500);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000);
            Init();

            Assert.IsTrue(AvatarLifecycle.Bind(AvatarManager.Get(AvatarA), Kell));

            Assert.AreEqual(AvatarBindState.Absorbed, AvatarManager.Get(AvatarA).State);
            Assert.AreEqual(6_500UL, AvatarManager.Get(AvatarB).LifetimeXp);
            Assert.AreEqual(AvatarB, AvatarManager.ForMonarch(Kell).AvatarId);
        }

        [TestMethod]
        public void Bind_TargetHasNoAvatar_Adopts()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 500);
            Init();

            var a = AvatarManager.Get(AvatarA);
            var before = a.Version;
            Assert.IsTrue(AvatarLifecycle.Bind(a, Kell));

            Assert.AreEqual(Kell, a.MonarchGuid);
            Assert.AreEqual(AvatarBindState.Bound, a.State);
            Assert.AreEqual(500UL, a.Favor, "adopted whole");
            Assert.AreEqual(Kell, a.KeeperGuid);
            Assert.IsTrue(a.Version > before);
            Assert.AreSame(a, AvatarManager.ForMonarch(Kell));
            Assert.IsNull(AvatarManager.ForMonarch(Varric));

            Flush();
            Assert.AreEqual(Kell, LastRow(AvatarA).MonarchGuid);
            Assert.IsTrue(announced.Any(x => x.Monarch == Kell && x.Text.Contains("now belongs to Kell's allegiance")));
        }

        // ---- swear ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void PlanSwear_TextsNameTheLevelAndTheRate()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000);
            Init();

            var plan = AvatarLifecycle.PlanSwear(Varric, true, "Varric", Kell);
            var level = AvatarManager.Get(AvatarA).Level(T);

            Assert.AreEqual(SwearAvatarEffect.Absorb, plan.Effect);
            Assert.AreEqual($"Your Avatar (level {level}) will be absorbed into Kell's at 25%. This can't be undone.", plan.SwearerText);
            Assert.AreEqual($"Varric's Avatar (level {level}) will be absorbed into Kell's at 25%. This can't be undone.", plan.PatronLine);
        }

        [TestMethod]
        public void ConfirmedSwear_TargetAvatarChanged_IsCancelled()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000);
            Init();

            var plan = AvatarLifecycle.PlanSwear(Varric, true, "Varric", Kell);
            Assert.AreEqual(SwearAvatarEffect.Absorb, plan.Effect);

            // Between the swearing monarch's Yes and the patron's confirm, the target Avatar's binding changes.
            Assert.IsTrue(AvatarLifecycle.SetHeir(AvatarManager.Get(AvatarB), 2001));

            Assert.IsFalse(AvatarLifecycle.StillMatches(plan.Snapshot));
            Assert.IsFalse(AvatarLifecycle.CheckConfirmedSwear(plan.Snapshot, Varric, true, "Varric", Kell, out var reason));
            StringAssert.Contains(reason, "Avatar of the allegiance being joined has changed");

            // The swear returns before any property change, so CompleteSwear never runs: nothing was absorbed.
            Assert.AreEqual(AvatarBindState.Bound, AvatarManager.Get(AvatarA).State);
            Assert.AreEqual(Varric, AvatarManager.Get(AvatarA).MonarchGuid);
            Assert.AreEqual(4_000UL, AvatarManager.Get(AvatarB).LifetimeXp);
        }

        [TestMethod]
        public void ConfirmedSwear_ChangeAfterTheRecheck_StillAbsorbs()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000);
            Init();

            var plan = AvatarLifecycle.PlanSwear(Varric, true, "Varric", Kell);
            Assert.IsTrue(AvatarLifecycle.CheckConfirmedSwear(plan.Snapshot, Varric, true, "Varric", Kell, out _));

            // The swear has now happened; a binding change on the target lands before the bind.
            Assert.IsTrue(AvatarLifecycle.SetHeir(AvatarManager.Get(AvatarB), 2001));
            AvatarLifecycle.CompleteSwear(Kell, plan.Snapshot);

            Assert.AreEqual(AvatarBindState.Absorbed, AvatarManager.Get(AvatarA).State, "a sworn monarch's Avatar must never stay bound to a non-monarch");
            Assert.AreEqual(AvatarB, AvatarManager.Get(AvatarA).AbsorbedIntoId);
            Assert.AreEqual(6_500UL, AvatarManager.Get(AvatarB).LifetimeXp);
        }

        [TestMethod]
        public void ConfirmedSwear_Unchanged_AbsorbsOnlyAtCompleteSwear()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000);
            Init();

            var plan = AvatarLifecycle.PlanSwear(Varric, true, "Varric", Kell);
            Assert.IsTrue(AvatarLifecycle.CheckConfirmedSwear(plan.Snapshot, Varric, true, "Varric", Kell, out _));
            Assert.AreEqual(4_000UL, AvatarManager.Get(AvatarB).LifetimeXp, "the confirm check itself absorbs nothing");

            AvatarLifecycle.CompleteSwear(Kell, plan.Snapshot);

            Assert.AreEqual(AvatarBindState.Absorbed, AvatarManager.Get(AvatarA).State);
            Assert.AreEqual(6_500UL, AvatarManager.Get(AvatarB).LifetimeXp);
        }

        [TestMethod]
        public void ConfirmedSwear_TargetAllegianceChanged_IsCancelled()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            Init();

            var plan = AvatarLifecycle.PlanSwear(Varric, true, "Varric", Kell);
            Assert.AreEqual(SwearAvatarEffect.Adopt, plan.Effect);

            // The patron now sits under a different monarch.
            Assert.IsFalse(AvatarLifecycle.CheckConfirmedSwear(plan.Snapshot, Varric, true, "Varric", 2500, out var reason));
            StringAssert.Contains(reason, "allegiance being joined has changed");
        }

        [TestMethod]
        public void ConfirmedSwear_NoSnapshot_ButAvatarNowInvolved_IsCancelled()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            Init();

            // The plain swear popup (no snapshot) confirmed by a monarch who holds an Avatar must not skip its popup.
            Assert.IsFalse(AvatarLifecycle.CheckConfirmedSwear(null, Varric, true, "Varric", Kell, out var reason));
            Assert.IsNotNull(reason);

            // A swearer with no Avatar passes untouched.
            Assert.IsTrue(AvatarLifecycle.CheckConfirmedSwear(null, Cora, true, "Cora", Kell, out _));
        }

        // ---- break and inheritance -----------------------------------------------------------------------------

        [TestMethod]
        public void KeeperBreak_Yes_InheritsAvatarPermanently()
        {
            SeedIdleMonarchAllegiance();
            Init();
            Assert.AreEqual(Brannoc, AvatarManager.Get(AvatarA).KeeperGuid);

            var gate = AvatarLifecycle.PlanBreak(Brannoc, Varric, actorLeaves: true, Brannoc, new[] { Brannoc, Cora });
            Assert.AreEqual(BreakAvatarEffect.Inheritance, gate.Effect);
            var level = AvatarManager.Get(AvatarA).Level(T);
            Assert.AreEqual($"Varric has been away 31 days. Breaking will take the Avatar (level {level}) with you to your new allegiance, permanently. Continue?", gate.Text);

            var again = AvatarLifecycle.PlanBreak(Brannoc, Varric, true, Brannoc, new[] { Brannoc, Cora });
            Assert.IsTrue(AvatarLifecycle.BreakStillMatches(gate, again, out _));
            AvatarLifecycle.CompleteBreak(again, Brannoc);

            var a = AvatarManager.Get(AvatarA);
            Assert.AreEqual(Brannoc, a.MonarchGuid);
            Assert.AreSame(a, AvatarManager.ForMonarch(Brannoc));
            Assert.IsNull(AvatarManager.ForMonarch(Varric));
            Assert.AreEqual(Brannoc, a.KeeperGuid);
            Assert.IsFalse(a.KeeperIsActing);
            Assert.IsTrue(announced.Any(x => x.Monarch == Varric && x.Text.StartsWith("Brannoc has broken away and taken the Avatar")), string.Join(" | ", announced));
            Assert.IsTrue(announced.Any(x => x.Monarch == Brannoc && x.Text.Contains("now belongs to Brannoc's allegiance")));

            Flush();
            Assert.AreEqual(Brannoc, LastRow(AvatarA).MonarchGuid);
        }

        [TestMethod]
        public void InheritanceYes_MonarchReturnedMeanwhile_NoTransfer()
        {
            SeedIdleMonarchAllegiance();
            Init();

            var gate = AvatarLifecycle.PlanBreak(Brannoc, Varric, true, Brannoc, new[] { Brannoc, Cora });
            Assert.AreEqual(BreakAvatarEffect.Inheritance, gate.Effect);

            // Varric logs in while Brannoc's popup is open.
            AvatarLifecycle.OnLogin(VarricAcct, Varric);
            Assert.AreEqual(Varric, AvatarManager.Get(AvatarA).KeeperGuid);

            var again = AvatarLifecycle.PlanBreak(Brannoc, Varric, true, Brannoc, new[] { Brannoc, Cora });
            Assert.AreEqual(BreakAvatarEffect.InfoStays, again.Effect);
            Assert.IsFalse(AvatarLifecycle.BreakStillMatches(gate, again, out var reason));
            StringAssert.Contains(reason, "no longer hold the Avatar's Keeper rights");

            // The Yes path cancels on the mismatch (GateAvatarBreak returns before doBreak); had it gone on with the
            // re-planned gate, that gate is InfoStays and still moves nothing.
            AvatarLifecycle.CompleteBreak(again, Brannoc);
            Assert.AreEqual(Varric, AvatarManager.Get(AvatarA).MonarchGuid);
            Assert.IsNull(AvatarManager.ForMonarch(Brannoc));
        }

        [TestMethod]
        public void InheritanceBreak_ChangeAfterTheRecheck_StillInherits()
        {
            SeedIdleMonarchAllegiance();
            Init();

            var gate = AvatarLifecycle.PlanBreak(Brannoc, Varric, true, Brannoc, new[] { Brannoc, Cora });
            var again = AvatarLifecycle.PlanBreak(Brannoc, Varric, true, Brannoc, new[] { Brannoc, Cora });
            Assert.IsTrue(AvatarLifecycle.BreakStillMatches(gate, again, out _));

            // The break has happened; a binding change lands before the bind.
            Assert.IsTrue(AvatarLifecycle.SetHeir(AvatarManager.Get(AvatarA), Cora));
            AvatarLifecycle.CompleteBreak(again, Brannoc);

            Assert.AreEqual(Brannoc, AvatarManager.Get(AvatarA).MonarchGuid);
        }

        [TestMethod]
        public void Inheritance_DoesNotBlockFoundingForTheOldMonarch()
        {
            const uint Dain = 1003, DainAcct = 103, Eve = 1004, EveAcct = 104;
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1, rank: 3), Member(Cora, CoraAcct, 2),
                Member(Dain, DainAcct, 1), Member(Eve, EveAcct, 1));
            Seen(VarricAcct, daysAgo: 31);
            Seen(BrannocAcct);
            Seen(CoraAcct);
            Seen(DainAcct);
            Seen(EveAcct);
            Init();

            var gate = AvatarLifecycle.PlanBreak(Brannoc, Varric, true, Brannoc, new[] { Brannoc, Cora });
            Assert.AreEqual(BreakAvatarEffect.Inheritance, gate.Effect);
            AvatarLifecycle.CompleteBreak(gate, Brannoc);

            // The split: Varric keeps Dain and Eve (two active accounts, no Avatar); Brannoc leads Cora.
            Roster(Varric, VarricAcct, Member(Dain, DainAcct, 1), Member(Eve, EveAcct, 1));
            Roster(Brannoc, BrannocAcct, Member(Cora, CoraAcct, 1));
            Assert.AreEqual(Varric, store.Avatars.Single(r => r.AvatarId == AvatarA).MonarchGuid, "the stored row still holds Varric");

            now += 601; // recompute: founding for Varric is due while the released row is still unflushed
            AvatarManager.Tick();

            Assert.IsFalse(AvatarManager.FoundingBlocked(Varric), "an Insert against the unflushed row would block Varric for the session");
            Assert.AreEqual(Brannoc, store.Avatars.Single(r => r.AvatarId == AvatarA).MonarchGuid, "the same tick's flush released the guid");

            now += 601;
            AvatarManager.Tick();

            var founded = AvatarManager.ForMonarch(Varric);
            Assert.IsNotNull(founded, "founded once the release was flushed");
            Assert.AreNotEqual(AvatarA, founded.AvatarId);
            Assert.AreEqual(AvatarA, AvatarManager.ForMonarch(Brannoc).AvatarId);
        }

        [TestMethod]
        public void KeeperDropsOwnVassal_NoInheritance()
        {
            SeedIdleMonarchAllegiance();
            Init();

            // Brannoc is the acting Keeper but breaks from Cora: Brannoc stays, so does the Avatar.
            var gate = AvatarLifecycle.PlanBreak(Brannoc, Varric, actorLeaves: false, Cora, new[] { Cora });
            Assert.AreEqual(BreakAvatarEffect.InfoStays, gate.Effect);
            Assert.AreEqual("The Avatar stays with Varric's allegiance.", gate.Text);
        }

        [TestMethod]
        public void MonarchBootsLastActiveVassal_PlanIsGoesInactive_WithSpecText()
        {
            SeedAvatar(AvatarA, Varric);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1));
            Seen(VarricAcct);
            Seen(BrannocAcct);
            Init();

            var gate = AvatarLifecycle.PlanBreak(Varric, Varric, false, Brannoc, new[] { Brannoc });

            Assert.AreEqual(BreakAvatarEffect.GoesInactive, gate.Effect);
            Assert.AreEqual("This leaves the allegiance with fewer than 2 active accounts. The Avatar will go inactive and its Favor will decay.", gate.Text);

            AvatarLifecycle.CompleteBreak(gate, Varric);
            Assert.AreEqual(Varric, AvatarManager.Get(AvatarA).MonarchGuid, "going inactive keeps the binding");
            Assert.IsTrue(announced.Any(x => x.Monarch == Varric && x.Text.Contains("will go inactive")));
        }

        [TestMethod]
        public void MonarchBreak_AllegianceAlreadyBelowMin_DoesNotAsk()
        {
            SeedAvatar(AvatarA, Varric);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1));
            Seen(VarricAcct); // Brannoc is not active: the allegiance is already below two active accounts
            Init();

            var gate = AvatarLifecycle.PlanBreak(Varric, Varric, false, Brannoc, new[] { Brannoc });

            Assert.AreEqual(BreakAvatarEffect.InfoStays, gate.Effect);
            Assert.AreEqual("The Avatar stays with your allegiance.", gate.Text);
        }

        [TestMethod]
        public void Absorb_CreditsTheSourcesPendingContributionFirst()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1));
            Seen(VarricAcct);
            Seen(BrannocAcct);
            Init();

            AvatarManager.Enqueue(new AvatarFeedEvent(Varric, BrannocAcct, Brannoc, 400 * 1_000_000L, AvatarFeedSource.Base));
            now += 1;
            AvatarManager.Tick(); // drained into pending, not credited: the flush interval has not passed

            var source = AvatarManager.Get(AvatarA);
            Assert.AreEqual(10_000UL, source.LifetimeXp);

            Assert.IsTrue(AvatarLifecycle.Absorb(source, AvatarManager.Get(AvatarB)));

            Assert.IsTrue(source.LifetimeXp > 10_000UL, "the pending contribution was credited before the absorb");
            Assert.AreEqual(4_000UL + AvatarCurves.AbsorbXp(source.LifetimeXp, T), AvatarManager.Get(AvatarB).LifetimeXp);
        }

        [TestMethod]
        public void MemberLeaves_ContributionRowsUntouched()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 900);
            store.Contributions.Add(new AllegianceAvatarContribution { AvatarId = AvatarA, AccountId = CoraAcct, Day = Today, RawXp = 600, Credited = 525, LastCharacterId = Cora });
            store.Contributions.Add(new AllegianceAvatarContribution { AvatarId = AvatarA, AccountId = BrannocAcct, Day = Today.AddDays(-1), RawXp = 300, Credited = 300, LastCharacterId = Brannoc });
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1), Member(Cora, CoraAcct, 1));
            Seen(VarricAcct);
            Seen(BrannocAcct);
            Seen(CoraAcct);
            Init();

            var before = AvatarManager.TopContributors(AvatarA, 10).ToList();

            // Cora breaks away while Varric is active: an info line, no state change.
            var gate = AvatarLifecycle.PlanBreak(Cora, Varric, true, Cora, new[] { Cora });
            Assert.AreEqual(BreakAvatarEffect.InfoStays, gate.Effect);
            Assert.AreEqual("The Avatar stays with Varric's allegiance.", gate.Text);
            AvatarLifecycle.CompleteBreak(gate, Cora);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1));

            now += 601; // past the recompute interval, so the roster without Cora is read
            AvatarManager.Tick();
            Flush();

            CollectionAssert.AreEqual(before, AvatarManager.TopContributors(AvatarA, 10).ToList());
            Assert.IsTrue(before.Any(c => c.AccountId == CoraAcct && c.Credited == 525));
            Assert.AreEqual(900UL, AvatarManager.Get(AvatarA).LifetimeXp);
            Assert.IsFalse(store.Batches.SelectMany(b => b.Rows).Any(), "no contribution row is rewritten or removed");
            Assert.AreEqual(Varric, AvatarManager.Get(AvatarA).MonarchGuid);
        }

        // ---- monarch delete ------------------------------------------------------------------------------------

        [TestMethod]
        public void MonarchDelete_WithVassals_GoesToKeepersBranch()
        {
            const uint Dain = 1003;      // Seneschal under Brannoc
            const uint DainAcct = 103;
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 400);
            Roster(Varric, VarricAcct,
                Member(Brannoc, BrannocAcct, 1, rank: 3),
                Member(Dain, DainAcct, 2, rank: 1, officer: 2),
                Member(Cora, CoraAcct, 1, rank: 2));
            Seen(VarricAcct); // active: the delete, not idleness, makes the monarch idle
            Seen(BrannocAcct);
            Seen(DainAcct);
            Seen(CoraAcct);
            Init();

            var plan = AvatarLifecycle.PlanMonarchDelete(Varric);
            Assert.AreEqual(Dain, plan.KeeperGuid, "the officer keeps it, on the pre-split tree");

            // After the split Dain's allegiance is Brannoc's branch.
            monarchOf[Dain] = Brannoc;
            AvatarLifecycle.ApplyMonarchDelete(plan);

            var a = AvatarManager.Get(AvatarA);
            Assert.AreEqual(Brannoc, a.MonarchGuid);
            Assert.AreEqual(AvatarBindState.Bound, a.State);
            Assert.AreEqual(400UL, a.Favor);
            Assert.AreSame(a, AvatarManager.ForMonarch(Brannoc));
            Assert.IsTrue(announced.Any(x => x.Monarch == Brannoc && x.Text.StartsWith("Varric is gone")), string.Join(" | ", announced));

            Flush();
            Assert.AreEqual(Brannoc, LastRow(AvatarA).MonarchGuid);
        }

        [TestMethod]
        public void MonarchDelete_KeepersBranchHasAvatar_Absorbs()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1));
            Seen(BrannocAcct);
            Init();

            var plan = AvatarLifecycle.PlanMonarchDelete(Varric);
            monarchOf[Brannoc] = Kell;
            AvatarLifecycle.ApplyMonarchDelete(plan);

            Assert.AreEqual(AvatarBindState.Absorbed, AvatarManager.Get(AvatarA).State);
            Assert.AreEqual(6_500UL, AvatarManager.Get(AvatarB).LifetimeXp);
        }

        [TestMethod]
        public void MonarchDelete_NoVassals_Retires()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 400);
            Init(); // no roster: a solo monarch has no allegiance

            var plan = AvatarLifecycle.PlanMonarchDelete(Varric);
            Assert.IsNull(plan.KeeperGuid);
            AvatarLifecycle.ApplyMonarchDelete(plan);

            var a = AvatarManager.Get(AvatarA);
            Assert.AreEqual(AvatarBindState.Retired, a.State);
            Assert.IsNull(a.MonarchGuid);
            Assert.AreEqual(10_000UL, a.LifetimeXp);
            Assert.IsNull(AvatarManager.ForMonarch(Varric));

            Flush();
            Assert.AreEqual((byte)AvatarBindState.Retired, LastRow(AvatarA).State);
            Assert.IsNull(LastRow(AvatarA).MonarchGuid);
        }

        [TestMethod]
        public void MonarchDelete_NoActiveVassal_InactiveHeirFirst()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1, rank: 3, officer: 2), Member(Cora, CoraAcct, 1, rank: 1));
            Init(); // nobody seen
            Assert.IsTrue(AvatarLifecycle.SetHeir(AvatarManager.Get(AvatarA), Cora));

            Assert.AreEqual(Cora, AvatarLifecycle.PlanMonarchDelete(Varric).KeeperGuid, "the named heir, though inactive, before an officer");
        }

        [TestMethod]
        public void MonarchDelete_NoActiveVassal_InactiveOfficerBeforeRank()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1, rank: 3), Member(Cora, CoraAcct, 1, rank: 1, officer: 1));
            Init(); // nobody seen

            Assert.AreEqual(Cora, AvatarLifecycle.PlanMonarchDelete(Varric).KeeperGuid, "an inactive officer before a higher-ranked member");
        }

        [TestMethod]
        public void MonarchDelete_NoActiveVassal_StillInherits()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1, rank: 3), Member(Cora, CoraAcct, 1, rank: 2));
            Init(); // nobody seen

            var plan = AvatarLifecycle.PlanMonarchDelete(Varric);
            Assert.AreEqual(Brannoc, plan.KeeperGuid, "highest rank, activity ignored, rather than retiring an Avatar with vassals");
        }

        // ---- the recompute's binding repair --------------------------------------------------------------------

        /// <summary>Runs the next 10-minute recompute.</summary>
        private void Recompute()
        {
            now += 600;
            AvatarManager.Tick();
        }

        [TestMethod]
        public void Recompute_AvatarBoundToNonMonarch_IsAdopted()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 500);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1));
            Seen(VarricAcct);
            Seen(BrannocAcct);
            Seen(KellAcct);
            Init();
            Assert.AreEqual(Varric, AvatarManager.Get(AvatarA).MonarchGuid);

            // A missed swear hook: Varric is now Kell's vassal, and Kell's allegiance (two active accounts, no Avatar)
            // would found one this recompute. The repair runs first, so Kell's allegiance adopts Varric's Avatar.
            rosters.Remove(Varric);
            Roster(Kell, KellAcct, Member(Varric, VarricAcct, 1), Member(Brannoc, BrannocAcct, 2));
            monarchStatus[Varric] = AvatarMonarchStatus.Vassal;
            monarchOf[Varric] = Kell;
            Recompute();

            var a = AvatarManager.Get(AvatarA);
            Assert.AreEqual(AvatarBindState.Bound, a.State);
            Assert.AreEqual(Kell, a.MonarchGuid);
            Assert.AreEqual(500UL, a.Favor, "adopted whole");
            Assert.AreSame(a, AvatarManager.ForMonarch(Kell));
            Assert.IsNull(AvatarManager.ForMonarch(Varric));
            Assert.IsFalse(store.Inserts.Any(i => i.MonarchGuid == Kell), "no second Avatar is founded for Kell");

            Flush();
            Assert.AreEqual(Kell, LastRow(AvatarA).MonarchGuid);
        }

        [TestMethod]
        public void Recompute_AvatarBoundToNonMonarch_IsAbsorbed()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 500);
            SeedAvatar(AvatarB, Kell, lifetime: 4_000, favor: 300);
            Init();

            monarchStatus[Varric] = AvatarMonarchStatus.Vassal;
            monarchOf[Varric] = Kell;
            Recompute();

            var source = AvatarManager.Get(AvatarA);
            var target = AvatarManager.Get(AvatarB);
            Assert.AreEqual(AvatarBindState.Absorbed, source.State);
            Assert.AreEqual(AvatarB, source.AbsorbedIntoId);
            Assert.AreEqual(6_500UL, target.LifetimeXp, "4000 + 25% of 10000");
            Assert.AreEqual(300UL, target.Favor);
            Assert.IsNull(AvatarManager.ForMonarch(Varric));

            Flush();
            Assert.AreEqual((byte)AvatarBindState.Absorbed, LastRow(AvatarA).State);
            Assert.AreEqual(6_500UL, LastRow(AvatarB).LifetimeXp);
        }

        [TestMethod]
        public void Recompute_AvatarBoundToNonMonarch_MonarchDeleted()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000, favor: 400);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1, rank: 3));
            Seen(VarricAcct);
            Seen(BrannocAcct);
            Init();

            // A missed delete hook: Varric's record is gone. The monarch-delete plan gives the Avatar to Brannoc.
            monarchStatus[Varric] = AvatarMonarchStatus.Gone;
            Recompute();

            var a = AvatarManager.Get(AvatarA);
            Assert.AreEqual(AvatarBindState.Bound, a.State);
            Assert.AreEqual(Brannoc, a.MonarchGuid);
            Assert.AreEqual(400UL, a.Favor);
            Assert.IsTrue(announced.Any(x => x.Monarch == Brannoc && x.Text.StartsWith("Varric is gone")), string.Join(" | ", announced));
            Assert.IsFalse(store.Inserts.Any(i => i.MonarchGuid == Varric), "the vacated guid is not founded again");

            Flush();
            Assert.AreEqual(Brannoc, LastRow(AvatarA).MonarchGuid);
        }

        [TestMethod]
        public void Recompute_MonarchStillLeads_NothingMoves()
        {
            SeedAvatar(AvatarA, Varric, lifetime: 10_000);
            Init();
            var before = AvatarManager.Get(AvatarA).Version;

            Recompute();

            Assert.AreEqual(Varric, AvatarManager.Get(AvatarA).MonarchGuid);
            Assert.AreEqual(before, AvatarManager.Get(AvatarA).Version);
        }

        // ---- the Keeper ----------------------------------------------------------------------------------------

        [TestMethod]
        public void Login_OnMonarchAccountAlt_RestoresMonarchKeeper()
        {
            SeedIdleMonarchAllegiance();
            Init();
            var a = AvatarManager.Get(AvatarA);
            Assert.AreEqual(Brannoc, a.KeeperGuid);
            Assert.IsTrue(a.KeeperIsActing);
            var before = a.Version;

            // Varric's account logs in on a solo alt: not in the allegiance, not the monarch character.
            AvatarLifecycle.OnLogin(VarricAcct, VarricAlt);

            Assert.AreEqual(Varric, a.KeeperGuid);
            Assert.IsFalse(a.KeeperIsActing);
            Assert.IsTrue(a.Version > before);
            Assert.IsTrue(announced.Contains((Varric, "Varric keeps the Avatar again.")), string.Join(" | ", announced));
        }

        [TestMethod]
        public void KeeperChange_AnnouncesToAllegiance_AndDespawnsSummonedOnly()
        {
            SeedAvatar(AvatarA, Varric);
            Roster(Varric, VarricAcct, Member(Brannoc, BrannocAcct, 1, rank: 3), Member(Cora, CoraAcct, 1, rank: 1));
            Seen(VarricAcct);
            Seen(BrannocAcct);
            Seen(CoraAcct);
            Init();
            var a = AvatarManager.Get(AvatarA);
            Assert.AreEqual(Varric, a.KeeperGuid);

            // 31 days on, the vassals still play and Varric does not.
            now += 31 * Day;
            Seen(BrannocAcct);
            Seen(CoraAcct);
            var before = a.Version;
            AvatarManager.Tick();

            Assert.AreEqual(Brannoc, a.KeeperGuid);
            Assert.IsTrue(a.Version > before);
            CollectionAssert.AreEqual(new[] { (Varric, "Brannoc keeps the Avatar while Varric is away.") }, announced);
            CollectionAssert.AreEqual(new[] { AvatarA }, despawned);
            Assert.AreEqual(1, revalidated.Count);
            Assert.AreSame(a, revalidated[0]);
            Assert.AreEqual(0, unseated.Count, "the seated NPC is revalidated, never just removed");
        }

        [TestMethod]
        public void Restart_WithActingKeeper_IsNotReannounced()
        {
            SeedIdleMonarchAllegiance();
            Init(keepCaptures: true);

            // The load starts the Keeper at the monarch; the first settle moves it to Brannoc without a chat line.
            Assert.AreEqual(Brannoc, AvatarManager.Get(AvatarA).KeeperGuid);
            Assert.AreEqual(0, announced.Count, string.Join(" | ", announced));
        }

        [TestMethod]
        public void SetHeir_BumpsVersion_Persists_AndDecidesTheKeeper()
        {
            SeedIdleMonarchAllegiance();
            Init();
            var a = AvatarManager.Get(AvatarA);
            Assert.AreEqual(Brannoc, a.KeeperGuid, "higher rank without an heir");
            var before = a.Version;

            // Naming the current acting Keeper changes no Keeper: the Version bump is the heir write's own.
            Assert.IsTrue(AvatarLifecycle.SetHeir(a, Brannoc));
            Assert.AreEqual(Brannoc, a.KeeperGuid);
            Assert.IsTrue(a.Version > before, "naming an heir is a binding change");
            before = a.Version;

            Assert.IsTrue(AvatarLifecycle.SetHeir(a, Cora));

            Assert.AreEqual(Cora, a.HeirGuid);
            Assert.AreEqual(Cora, a.KeeperGuid, "an active heir comes first");
            Assert.IsTrue(a.Version > before);
            Assert.IsTrue(announced.Contains((Varric, "Cora is now heir to the Avatar.")));
            Assert.IsTrue(announced.Contains((Varric, "Cora keeps the Avatar while Varric is away.")));

            Flush();
            Assert.AreEqual(Cora, LastRow(AvatarA).HeirGuid);
        }

        // ---- store ---------------------------------------------------------------------------------------------

        private sealed class Store : IAvatarStore
        {
            public readonly List<AllegianceAvatar> Avatars = new List<AllegianceAvatar>();
            public readonly List<AllegianceAvatarContribution> Contributions = new List<AllegianceAvatarContribution>();
            public readonly List<(List<AllegianceAvatar> Avatars, List<AllegianceAvatarContribution> Rows)> Batches = new();
            public bool LoadAllReturnsNull;

            public List<AllegianceAvatar> LoadAll() => LoadAllReturnsNull ? null : Avatars.Select(Copy).ToList();

            public List<AllegianceAvatarContribution> LoadContributionsSince(DateTime dayUtc) => Contributions.Where(c => c.Day >= dayUtc).ToList();

            public readonly List<AllegianceAvatar> Inserts = new List<AllegianceAvatar>();

            /// <summary>Upserts by avatar id, so Avatars is what the store holds after each flush.</summary>
            public void SaveBatch(IReadOnlyList<AllegianceAvatar> a, IReadOnlyList<AllegianceAvatarContribution> r, Action<bool> done)
            {
                Batches.Add((a.Select(Copy).ToList(), r.ToList()));
                foreach (var row in a)
                {
                    Avatars.RemoveAll(x => x.AvatarId == row.AvatarId);
                    Avatars.Add(Copy(row));
                }
                done(true);
            }

            /// <summary>allegiance_avatar_monarch_uidx: fails while any stored row still holds the guid.</summary>
            public void Insert(AllegianceAvatar row, Action<bool, uint> done)
            {
                Inserts.Add(Copy(row));
                if (Avatars.Any(x => x.MonarchGuid == row.MonarchGuid))
                {
                    done(false, 0);
                    return;
                }
                var id = (uint)(100 + Inserts.Count);
                var stored = Copy(row);
                stored.AvatarId = id;
                Avatars.Add(stored);
                done(true, id);
            }

            private static AllegianceAvatar Copy(AllegianceAvatar a) => new AllegianceAvatar
            {
                AvatarId = a.AvatarId, MonarchGuid = a.MonarchGuid, State = a.State, LifetimeXp = a.LifetimeXp, LevelXp = a.LevelXp, Favor = a.Favor,
                HeirGuid = a.HeirGuid, AbsorbedIntoId = a.AbsorbedIntoId, LastContributionAt = a.LastContributionAt, LastDecayDay = a.LastDecayDay,
            };
        }
    }
}
