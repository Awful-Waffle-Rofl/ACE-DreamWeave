using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Avatars;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.Avatars.Gauntlet.GauntletFighterFixtures;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// The Avatar fighter in its own private World Event (AvatarEventFighterHost, design part B): spawn at Active with the
    /// vendor gone, the 60 m leash, strength at min(A, band top), the 60 s return, removal at the end (forced included), the
    /// World Events wiring (Transition and the forced-Done path through WorldEventHooks), and the objective guard. The world
    /// goes through the host's seams; no PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class AvatarEventFighterHostTests
    {
        private const uint AvatarId = 77;
        private const double T0 = 1_700_000_000d;

        [ClassInitialize]
        public static void Init(TestContext _) => EnsureInitialized();

        private readonly List<string> lines = new List<string>();
        private readonly List<IReadOnlyList<Player>> heard = new List<IReadOnlyList<Player>>();

        [TestInitialize]
        public void Setup()
        {
            AvatarFighterVoice.ResetSeams();
            AvatarFighterVoice.Sink = (to, text) => { lines.Add(text); heard.Add(to); };
            AvatarFighterVoice.PickIndex = _ => 0;
            AvatarFighterProbe.Enabled = () => false;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AvatarFighterVoice.ResetSeams();
            AvatarFighterProbe.ResetSeams();
        }

        // ---- the harness ----------------------------------------------------------------------------------------------------

        private sealed class Harness
        {
            public int Avatar = 150;
            public List<Player> NearPlayers = new List<Player>();
            public readonly List<uint> Despawned = new List<uint>();
            public readonly List<(GauntletAvatarFighter Fighter, ACE.Entity.Position At)> Placed = new List<(GauntletAvatarFighter, ACE.Entity.Position)>();
            public readonly List<GauntletAvatarFighter> Destroyed = new List<GauntletAvatarFighter>();
            public readonly List<(double Seconds, Action Action)> Later = new List<(double, Action)>();
            public readonly List<int> StampLevels = new List<int>();
            public readonly HashSet<uint> CreditedGuids = new HashSet<uint>();
            public readonly HashSet<uint> SwornGuids = new HashSet<uint>();
            public readonly HashSet<GauntletAvatarFighter> Gone = new HashSet<GauntletAvatarFighter>();
            public int FailPlacements;
            public int DropAnchorActions;
            public int DeferAnchorActions;
            public readonly List<Action> Deferred = new List<Action>();
            public double Now = 1_000_000.0;
            public readonly AvatarEventFighterHost Host;

            /// <param name="liveFilter">true: the Credited and Sworn live defaults (a real Participation record, AvatarRights).</param>
            public Harness(bool liveFilter = false, Func<uint, AvatarState> avatarOf = null)
            {
                var defaults = new AvatarEventFighterHost();

                Host = new AvatarEventFighterHost
                {
                    Build = id => Fighter(health: 1000),
                    Place = (f, at) => { if (FailPlacements > 0) { FailPlacements--; return false; } Placed.Add((f, at)); return true; },
                    OnAnchorThread = (at, a) => { if (DropAnchorActions > 0) { DropAnchorActions--; return; } if (DeferAnchorActions > 0) { DeferAnchorActions--; Deferred.Add(a); return; } a(); },
                    Now = () => Now,
                    AvatarOf = avatarOf ?? (_ => null),
                    After = (s, a) => Later.Add((s, a)),
                    Destroy = f => Destroyed.Add(f),
                    AvatarLevel = _ => Avatar,
                    StampAt = level => { StampLevels.Add(level); return new GauntletFighterStamp { Level = level, MaxHealth = 900, MinDeathSeconds = 20, BurstSeconds = 5 }; },
                    Near = _ => NearPlayers,
                    Credited = liveFilter ? defaults.Credited : (evt, g) => CreditedGuids.Contains(g),
                    Sworn = liveFilter ? defaults.Sworn : (id, p) => SwornGuids.Contains(p.Guid.Full),
                    InWorld = f => !Gone.Contains(f) && !Destroyed.Contains(f),
                    AnchorOf = evt => evt.Composition?.AnchorPosition,
                    DespawnVendor = id => Despawned.Add(id),
                };
            }

            public void RunLater()
            {
                var batch = Later.ToList();
                Later.Clear();
                foreach (var l in batch) l.Action();
            }
        }

        internal static WorldEvent Event(uint runId, uint? avatarId, bool announced = true)
        {
            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 30,
                Invoker = "test",
                AvatarId = avatarId,
                AnnounceServerWide = announced,
            };

            return new WorldEvent(runId, WorldEventCombatCreditTests.BuildComposition(), request, new WorldEventCombatCreditTests.OpenObjective(),
                () => new AudienceEstimate(3, 100, 150), () => T0);
        }

        // ---- the level (pure) -------------------------------------------------------------------------------------------

        [TestMethod]
        public void FightingLevel_IsMinOfTheAvatarAndTheBandTop()
        {
            Assert.AreEqual(80, AvatarEventFighterHost.FightingLevel(150, 80, 0), "a band below the Avatar caps it");
            Assert.AreEqual(150, AvatarEventFighterHost.FightingLevel(150, 300, 0), "a band above the Avatar: the Avatar's own level");
            Assert.AreEqual(120, AvatarEventFighterHost.FightingLevel(150, 0, 120), "before the first wave: the top player near the anchor");
            Assert.AreEqual(150, AvatarEventFighterHost.FightingLevel(150, 0, 0), "nobody and no band: the Avatar");
            Assert.AreEqual(1, AvatarEventFighterHost.FightingLevel(0, 0, 0));
        }

        // ---- the lifetime -----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Active_DespawnsTheVendor_AndPlacesALeashedFighterAtTheAnchor()
        {
            var h = new Harness();
            var evt = Event(11, AvatarId);

            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);

            CollectionAssert.AreEqual(new[] { AvatarId }, h.Despawned, "the summoned vendor goes at Active");
            var (fighter, at) = h.Placed.Single();
            Assert.AreEqual(evt.Composition.AnchorPosition.Cell, at.Cell, "placed at the event anchor");
            Assert.AreEqual(evt.Composition.AnchorPosition.PositionX, fighter.Anchor.PositionX, "leashed to the anchor");
            Assert.AreEqual(60.0f, fighter.LeashRadius, "the 60 m event radius");
            Assert.IsNull(fighter.TauntScope, "the Warden taunt pulse is the Gauntlet's: a private event's fighter is never armed");
            Assert.AreEqual(0.0, fighter.SummonTauntInterval);
            Assert.IsNotNull(fighter.Participants);
            Assert.IsNotNull(fighter.Audience);
            Assert.AreEqual(150, fighter.Stamp.Level, "no band yet and nobody near: the Avatar's level");
            Assert.IsTrue(fighter.AcceptsBeneficialSpells, "members may heal it while the event is on");
            Assert.IsTrue(h.Host.IsUp(AvatarId));
            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.Spawn][0], lines.Single(), "the spawn line");

            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            Assert.AreEqual(1, h.Placed.Count, "a repeated Active places nothing");
        }

        [TestMethod]
        public void AnOrdinaryRun_GetsNoFighter()
        {
            var h = new Harness();

            h.Host.OnStateChanged(Event(12, null), WorldEventState.Announced, WorldEventState.Active);

            Assert.AreEqual(0, h.Placed.Count);
            Assert.AreEqual(0, h.Despawned.Count);
        }

        [TestMethod]
        public void EachWave_RestampsAt_MinOfTheAvatarAndTheBandTop()
        {
            var h = new Harness();
            var evt = Event(13, AvatarId);
            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            var fighter = h.Placed.Single().Fighter;

            h.Host.OnWave(evt, 1, 80);
            Assert.AreEqual(80, fighter.Stamp.Level, "a band topping at 80 caps an L150 Avatar");

            h.Host.OnWave(evt, 2, 400);
            Assert.AreEqual(150, fighter.Stamp.Level, "a band above it: the Avatar's own level");
        }

        [TestMethod]
        public void AFall_ReturnsAfter60Seconds_AtTheAnchor()
        {
            var h = new Harness();
            var evt = Event(14, AvatarId);
            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            h.Host.OnWave(evt, 1, 90);
            var first = h.Placed.Single().Fighter;

            first.OnDeath(null, DamageType.Slash);

            Assert.AreEqual(1, h.Later.Count(l => l.Seconds == 60.0), "the return is 60 s out");
            Assert.IsTrue(h.Host.IsUp(AvatarId), "a fallen fighter still holds the summon refusal until the event ends");

            h.RunLater();

            Assert.AreEqual(2, h.Placed.Count);
            var second = h.Placed[1].Fighter;
            Assert.AreNotSame(first, second);
            Assert.AreEqual(90, second.Stamp.Level, "it comes back with the last wave's stamp");
            Assert.AreEqual(second.Health.MaxValue, second.Health.Current, "at full health");
            CollectionAssert.AreEqual(new[]
            {
                AvatarFighterVoice.Lines[FighterVoiceTrigger.Spawn][0],
                AvatarFighterVoice.Lines[FighterVoiceTrigger.Death][0],
                AvatarFighterVoice.Lines[FighterVoiceTrigger.Return][0],
            }, lines);
        }

        [TestMethod]
        public void TheEnd_RemovesIt_WithoutADeath_AndAPendingReturnPlacesNothing()
        {
            var h = new Harness();
            var evt = Event(15, AvatarId);
            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            var first = h.Placed.Single().Fighter;
            first.OnDeath(null, DamageType.Slash);

            h.Host.OnStateChanged(evt, WorldEventState.Active, WorldEventState.Resolved);
            Assert.IsFalse(h.Host.IsUp(AvatarId), "summon is allowed again");

            h.RunLater();
            Assert.AreEqual(1, h.Placed.Count, "the return scheduled before the end places nothing");
        }

        [TestMethod]
        public void AForcedDone_RemovesALiveFighter()
        {
            var h = new Harness();
            var evt = Event(16, AvatarId);
            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            var fighter = h.Placed.Single().Fighter;

            h.Host.OnStateChanged(evt, WorldEventState.Active, WorldEventState.Done);

            CollectionAssert.AreEqual(new[] { fighter }, h.Destroyed);
            Assert.IsNull(fighter.Owner, "unbound before the destroy: never read as a death");
            Assert.IsFalse(h.Host.IsUp(AvatarId));

            h.Host.OnStateChanged(evt, WorldEventState.Done, WorldEventState.Done);
            Assert.AreEqual(1, h.Destroyed.Count, "a second end is a no-op");
        }

        // ---- the reconcile ----------------------------------------------------------------------------------------------------

        [TestMethod]
        public void AFailedFirstPlacement_IsRetried_AndIsNotUpMeanwhile()
        {
            var h = new Harness { FailPlacements = 2 };
            var evt = Event(31, AvatarId);

            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            Assert.AreEqual(0, h.Placed.Count);
            Assert.IsTrue(h.Host.IsUp(AvatarId), "the event is live, placed or not: /avatar summon stays refused");

            h.RunLater();
            Assert.AreEqual(0, h.Placed.Count, "fixture: the second attempt is refused too");
            Assert.AreEqual(AvatarEventFighterHost.ReconcileSeconds, h.Later.Single().Seconds, "the reconcile keeps running");

            h.RunLater();
            Assert.AreEqual(1, h.Placed.Count, "the third attempt places it");
            Assert.IsTrue(h.Host.IsUp(AvatarId));
            Assert.AreEqual(AvatarFighterVoice.Lines[FighterVoiceTrigger.Spawn][0], lines.Single(), "its first placement is the spawn line");
        }

        [TestMethod]
        public void AFighterDestroyedWithoutADeath_IsReplaced()
        {
            var h = new Harness();
            var evt = Event(32, AvatarId);
            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            var first = h.Placed.Single().Fighter;

            h.Gone.Add(first);
            h.RunLater();

            Assert.AreEqual(2, h.Placed.Count, "the reconcile places a new one");
            Assert.AreSame(h.Placed[1].Fighter, h.Host.SlotFor(evt).Fighter);
            Assert.IsNull(first.Owner, "the stale one is unbound");
            Assert.IsTrue(h.Host.IsUp(AvatarId));

            h.RunLater();
            Assert.AreEqual(2, h.Placed.Count, "a fighter in the world is left alone");
        }

        [TestMethod]
        public void TheWave_AlsoReconciles()
        {
            var h = new Harness { FailPlacements = 1 };
            var evt = Event(33, AvatarId);
            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            Assert.AreEqual(0, h.Placed.Count);

            h.Host.OnWave(evt, 1, 90);

            Assert.AreEqual(90, h.Placed.Single().Fighter.Stamp.Level, "placed at the wave's stamp");
        }

        [TestMethod]
        public void AReturnPending_DoesNotDoublePlace()
        {
            var h = new Harness();
            var evt = Event(34, AvatarId);
            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            h.Placed.Single().Fighter.OnDeath(null, DamageType.Slash);

            Assert.IsFalse(h.Host.Reconcile(h.Host.SlotFor(evt)), "the return is pending: the reconcile waits");
            h.RunLater();
            Assert.AreEqual(2, h.Placed.Count, "exactly one return");
        }

        // ---- who it fights beside (F4) ---------------------------------------------------------------------------------------

        [TestMethod]
        public void ABystander_IsNeitherLeaderNorListener()
        {
            var h = new Harness();
            var sworn = AvatarFighterVoiceTests.Named(0x50000101, "Sworn");
            var credited = AvatarFighterVoiceTests.Named(0x50000102, "Credited");
            var bystander = AvatarFighterVoiceTests.Named(0x50000103, "Bystander");
            sworn.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 100);
            credited.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 120);
            bystander.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 300);
            h.NearPlayers = new List<Player> { sworn, credited, bystander };
            h.SwornGuids.Add(sworn.Guid.Full);
            h.CreditedGuids.Add(credited.Guid.Full);

            h.Host.OnStateChanged(Event(41, AvatarId), WorldEventState.Announced, WorldEventState.Active);
            var f = h.Placed.Single().Fighter;
            f.IsPresentSeam = _ => true;
            f.IsDown = _ => false;

            CollectionAssert.AreEqual(new[] { sworn, credited }, f.Participants().ToList());
            f.RefreshLeader(1.0);
            Assert.AreSame(credited, f.Leader, "the highest of the two, never the higher bystander");
            CollectionAssert.AreEqual(new[] { sworn, credited }, heard.Single().ToList(), "the spawn line skips the bystander");
            Assert.AreEqual(120, f.Stamp.Level, "and so does the pre-wave level read");
        }

        [TestMethod]
        public void AHiddenAdmin_IsNotAListener()
        {
            var h = new Harness();
            var member = AvatarFighterVoiceTests.Named(0x50000111, "Member");
            var admin = AvatarFighterVoiceTests.Named(0x50000112, "Hidden Admin");
            admin.CloakStatus = CloakStatus.On;
            h.NearPlayers = new List<Player> { member, admin };
            h.SwornGuids.Add(member.Guid.Full);
            h.SwornGuids.Add(admin.Guid.Full);

            h.Host.OnStateChanged(Event(42, AvatarId), WorldEventState.Announced, WorldEventState.Active);

            CollectionAssert.AreEqual(new[] { member }, heard.Single().ToList(), "a cloaked admin hears nothing");

            member.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 100);
            admin.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 300);
            var f = h.Placed.Single().Fighter;
            f.IsPresentSeam = _ => true;
            f.IsDown = _ => false;
            f.RefreshLeader(1.0);
            Assert.AreSame(member, f.Leader, "a hidden admin is never present, so never followed, though the highest level");

            member.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 1);
            f.RefreshLeader(3.0);
            Assert.AreSame(member, f.Leader, "nor after a re-check");

            var hiddenFlag = AvatarFighterVoiceTests.Named(0x50000113, "Hidden Flag");
            hiddenFlag.HiddenAdmin = true;
            Assert.IsTrue(AvatarFighterVoice.IsHidden(hiddenFlag), "HiddenAdmin counts as hidden too");
        }

        // ---- quiet events are confined to the allegiance (owner ruling, stage run 1 at Holtburg) -----------------------------

        private const uint QuietMonarch = 0x50000160;

        /// <summary>
        /// Runs every event voice trigger (spawn, periodic, leader switch, all down, death, return) through the live filter
        /// defaults, with two sworn members and a CREDITED non-member near the anchor (a real Participation record, as a run
        /// creature's landed hit makes one). Returns the outsider, every audience and the fighter that led after the first pick.
        /// </summary>
        private (Player Outsider, Player FirstLeader, List<Player> FirstParticipants) RunEveryTrigger(bool announced)
        {
            var member = AvatarFighterVoiceTests.Named(0x50000161, "Member");
            var second = AvatarFighterVoiceTests.Named(0x50000162, "Second");
            var outsider = AvatarFighterVoiceTests.Named(0x50000163, "Outsider");
            member.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 100);
            second.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 50);
            outsider.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.Level, 300);
            var savedFacts = ACE.Server.Avatars.AvatarRights.AllegianceFacts;

            try
            {
                ACE.Server.Avatars.AvatarRights.AllegianceFacts = p => (p == member || p == second ? QuietMonarch : p.Guid.Full, false);
                AvatarFighterVoice.PeriodSeconds = () => 120;

                var h = new Harness(liveFilter: true, avatarOf: id => id == AvatarId ? new AvatarState { AvatarId = AvatarId, MonarchGuid = QuietMonarch, State = AvatarBindState.Bound } : null);
                h.NearPlayers = new List<Player> { member, second, outsider };
                var evt = Event(announced ? 62u : 61u, AvatarId, announced);
                Assert.IsTrue(evt.Participation.CreditCombatContact(outsider.Guid.Full, 9, "127.0.0.9", "Outsider"), "fixture: the outsider is credited");

                h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);                // spawn
                var f = h.Placed.Single().Fighter;
                var down = new HashSet<Player>();
                f.IsPresentSeam = _ => true;
                f.IsDown = p => down.Contains(p);

                var firstParticipants = f.Participants().ToList();
                f.RefreshLeader(1.0);
                var firstLeader = f.Leader;

                f.TickVoice(1000.0);
                f.TickVoice(1120.0);                                                                            // periodic

                if (!announced)
                {
                    down.Add(member);
                    f.RefreshLeader(2.0);                                                                       // leader switch to Second
                    down.Add(second);
                    f.RefreshLeader(3.0);                                                                       // all down
                }

                f.OnDeath(null, DamageType.Slash);                                                              // death
                h.RunLater();                                                                                   // return (and a reconcile)

                return (outsider, firstLeader, firstParticipants);
            }
            finally
            {
                ACE.Server.Avatars.AvatarRights.AllegianceFacts = savedFacts;
            }
        }

        [TestMethod]
        public void AQuietEvent_ACreditedNonMember_HearsNoLine_AndIsNeverLeader()
        {
            var (outsider, firstLeader, participants) = RunEveryTrigger(announced: false);

            foreach (var t in new[] { FighterVoiceTrigger.Spawn, FighterVoiceTrigger.Periodic, FighterVoiceTrigger.LeaderSwitch, FighterVoiceTrigger.AllDown, FighterVoiceTrigger.Death, FighterVoiceTrigger.Return })
                Assert.IsTrue(lines.Any(l => l.StartsWith(AvatarFighterVoice.Lines[t][0].Split('{')[0], StringComparison.Ordinal)), $"fixture: the {t} line played");

            Assert.AreEqual(lines.Count, heard.Count);
            Assert.IsFalse(heard.Any(to => to.Contains(outsider)), "a quiet event's lines reach the allegiance only");
            CollectionAssert.DoesNotContain(participants, outsider, "nor is a credited non-member someone it fights beside");
            Assert.AreNotSame(outsider, firstLeader, "so it never leads, though the highest level");
            Assert.IsNotNull(firstLeader, "fixture: a member leads");
        }

        [TestMethod]
        public void AnAnnouncedEvent_ACreditedNonMember_IsIncluded()
        {
            var (outsider, firstLeader, participants) = RunEveryTrigger(announced: true);

            CollectionAssert.Contains(participants, outsider, "an announced event counts its credited players");
            Assert.AreSame(outsider, firstLeader, "the highest-level credited player leads");
            Assert.IsTrue(heard.Count > 0 && heard.All(to => to.Contains(outsider)), "and hears every line");
        }

        [TestMethod]
        public void TheLiveFilterDefaults_ARealRecordAndAnAllegiance_ExcludeAThirdPlayer()
        {
            const uint monarch = 0x50000140;
            var credited = AvatarFighterVoiceTests.Named(0x50000141, "Credited");
            var sworn = AvatarFighterVoiceTests.Named(0x50000142, "Sworn");
            var third = AvatarFighterVoiceTests.Named(0x50000143, "Third");
            var savedFacts = ACE.Server.Avatars.AvatarRights.AllegianceFacts;

            try
            {
                ACE.Server.Avatars.AvatarRights.AllegianceFacts = p => (p == sworn ? monarch : p.Guid.Full, false);
                var h = new Harness(liveFilter: true, avatarOf: id => id == AvatarId ? new AvatarState { AvatarId = AvatarId, MonarchGuid = monarch, State = AvatarBindState.Bound } : null);
                h.NearPlayers = new List<Player> { credited, sworn, third };
                var evt = Event(43, AvatarId);
                Assert.IsTrue(evt.Participation.CreditCombatContact(credited.Guid.Full, 1, "127.0.0.1", "Credited"), "fixture: a real participation record");

                h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);

                CollectionAssert.AreEqual(new[] { credited, sworn }, h.Placed.Single().Fighter.Participants().ToList(), "credited or sworn; the third is neither");
                CollectionAssert.AreEqual(new[] { credited, sworn }, heard.Single().ToList());
            }
            finally
            {
                ACE.Server.Avatars.AvatarRights.AllegianceFacts = savedFacts;
            }
        }

        // ---- the pending deadline and the failure log ------------------------------------------------------------------------

        [TestMethod]
        public void ADroppedPlacement_ExpiresAtItsDeadline_ThenTheReconcilePlaces()
        {
            var h = new Harness { DropAnchorActions = 1 };
            var evt = Event(51, AvatarId);

            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            Assert.AreEqual(0, h.Placed.Count, "fixture: the queued placement never ran");

            h.RunLater();
            Assert.AreEqual(0, h.Placed.Count, "inside the deadline the reconcile waits for it");

            h.Now += AvatarEventFighterHost.RespawnSeconds + AvatarEventFighterHost.PendingSlackSeconds + 1;
            h.RunLater();
            Assert.AreEqual(1, h.Placed.Count, "past the deadline the reconcile places it");
        }

        [TestMethod]
        public void ALatePlacement_AfterTheReconcilePlaced_NeverMakesASecondFighter()
        {
            var h = new Harness { DeferAnchorActions = 1 };
            var evt = Event(54, AvatarId);

            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            h.Now += AvatarEventFighterHost.RespawnSeconds + AvatarEventFighterHost.PendingSlackSeconds + 1;
            h.RunLater();
            Assert.AreEqual(1, h.Placed.Count, "fixture: the reconcile placed one past the deadline");

            h.Deferred.Single()();

            Assert.AreEqual(1, h.Placed.Count, "the late queued placement finds a fighter in the world and places nothing");
        }

        [TestMethod]
        public void AnUnqueueablePlacement_ClearsThePending_AtOnce()
        {
            var thrown = new Harness();
            var host = new AvatarEventFighterHost
            {
                Build = _ => Fighter(health: 1000),
                Place = (f, at) => { thrown.Placed.Add((f, at)); return true; },
                OnAnchorThread = (at, a) => { if (thrown.DropAnchorActions-- > 0) throw new InvalidOperationException("queue"); a(); },
                After = (sec, a) => thrown.Later.Add((sec, a)),
                Destroy = f => { },
                AvatarLevel = _ => 100,
                StampAt = level => new GauntletFighterStamp { Level = level, MaxHealth = 900, MinDeathSeconds = 20, BurstSeconds = 5 },
                Near = _ => new List<Player>(),
                Credited = (e, g) => false,
                Sworn = (id, p) => false,
                InWorld = _ => true,
                Now = () => thrown.Now,
                DespawnVendor = _ => { },
            };
            thrown.DropAnchorActions = 1;

            host.OnStateChanged(Event(52, AvatarId), WorldEventState.Announced, WorldEventState.Active);
            Assert.AreEqual(0, thrown.Placed.Count);

            thrown.RunLater();
            Assert.AreEqual(1, thrown.Placed.Count, "the enqueue throw cleared the pending, so the next reconcile places without waiting out the deadline");
        }

        [TestMethod]
        public void APersistentPlacementFailure_WarnsOnce_ThenDebug_UntilASuccess()
        {
            var h = new Harness { FailPlacements = 3 };
            var evt = Event(53, AvatarId);
            h.Host.OnStateChanged(evt, WorldEventState.Announced, WorldEventState.Active);
            var slot = h.Host.SlotFor(evt);

            h.RunLater();
            h.RunLater();
            Assert.AreEqual(3, slot.PlaceFailures, "three in a row counted");

            h.RunLater();
            Assert.AreEqual(1, h.Placed.Count);
            Assert.AreEqual(0, slot.PlaceFailures, "reset by the success");

            var fresh = new AvatarEventFighterHost.Slot(h.Host, evt, AvatarId, evt.Composition.AnchorPosition);
            Assert.IsTrue(AvatarEventFighterHost.LogPlaceFailure(fresh, "test: first"), "the first failure warns");
            Assert.IsFalse(AvatarEventFighterHost.LogPlaceFailure(fresh, "test: second"), "the rest are Debug");

            var src = PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/AvatarEventFighterHost.cs").Replace("\r\n", "\n");
            var core = PooledLootSourceText.MethodBody(src, "private GauntletAvatarFighter PlaceNewCore(Slot slot)");
            Assert.AreEqual(4, System.Text.RegularExpressions.Regex.Matches(core, "LogPlaceFailure\\(slot, ").Count, "the four failure paths");
            Assert.IsFalse(core.Contains("log.Warn(", StringComparison.Ordinal) || core.Contains("log.Error(", StringComparison.Ordinal), "no failure path logs at Warn or Error directly");
        }

        [TestMethod]
        public void SelectAvatarEventPlayers_CreditedOrSworn_Distinct()
        {
            var a = AvatarFighterVoiceTests.Named(0x50000121, "A");
            var b = AvatarFighterVoiceTests.Named(0x50000122, "B");
            var c = AvatarFighterVoiceTests.Named(0x50000123, "C");

            var picked = WorldEventHooks.SelectAvatarEventPlayers(new[] { a, null, b, c, a }, g => g == a.Guid.Full, p => p == b);

            CollectionAssert.AreEqual(new[] { a, b }, picked);
            Assert.AreEqual(0, WorldEventHooks.SelectAvatarEventPlayers(null, _ => true, _ => true).Count);
        }

        // ---- the World Events wiring (the consumer) -------------------------------------------------------------------------

        private sealed class RecordingHost : IAvatarEventHost
        {
            public readonly List<(uint Run, WorldEventState From, WorldEventState To)> States = new List<(uint, WorldEventState, WorldEventState)>();
            public void OnStateChanged(WorldEvent evt, WorldEventState from, WorldEventState to) => States.Add((evt.RunId, from, to));
            public void OnWave(WorldEvent evt, int wave, int bandTop) { }
        }

        [TestMethod]
        public void ARealPrivateRun_ReachesTheHost_AtActive_AnOrdinaryRunNever()
        {
            var saved = WorldEventHooks.AvatarEventHost;
            var host = new RecordingHost();

            try
            {
                WorldEventHooks.AvatarEventHost = host;

                var avatarRun = Event(21, AvatarId);
                avatarRun.Stage(T0);
                avatarRun.Tick(T0 + 30);
                Assert.AreEqual(WorldEventState.Active, avatarRun.State, "fixture: the run is Active");

                var plain = Event(22, null);
                plain.Stage(T0);
                plain.Tick(T0 + 30);
                Assert.AreEqual(WorldEventState.Active, plain.State);

                Assert.IsTrue(host.States.Contains((21u, WorldEventState.Announced, WorldEventState.Active)), "the private run's Transition told the host");
                Assert.IsFalse(host.States.Any(s => s.Run == 22), "an ordinary run never reaches the host");
            }
            finally
            {
                WorldEventHooks.AvatarEventHost = saved;
            }
        }

        [TestMethod]
        public void TheHost_IsCalledFromTransition_TheForcedDone_AndTheWaveSpawn()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEvent.cs").Replace("\r\n", "\n");

            var transition = PooledLootSourceText.MethodBody(src, "private bool Transition(WorldEventState to, string reason)");
            StringAssert.Contains(transition, "WorldEventHooks.NotifyAvatarEventState(this, from, to);");

            var forced = src.IndexOf("forcing Done", StringComparison.Ordinal);
            Assert.IsTrue(forced > 0);
            var forcedBlock = src.Substring(forced, 400);
            StringAssert.Contains(forcedBlock, "WorldEventHooks.NotifyAvatarEventState(this, forcedFrom, WorldEventState.Done);", "the forced-Done fallback tells the host too");

            StringAssert.Contains(src, "WorldEventHooks.NotifyAvatarEventWave(this, WaveIndex, band.High);", "each wave tells the host its band top");
        }

        [TestMethod]
        public void TheHook_ContainsAThrowingHost()
        {
            var saved = WorldEventHooks.AvatarEventHost;

            try
            {
                WorldEventHooks.AvatarEventHost = new ThrowingHost();
                WorldEventHooks.NotifyAvatarEventState(Event(23, AvatarId), WorldEventState.Announced, WorldEventState.Active);
                WorldEventHooks.NotifyAvatarEventWave(Event(23, AvatarId), 1, 50);
            }
            finally
            {
                WorldEventHooks.AvatarEventHost = saved;
            }
        }

        private sealed class ThrowingHost : IAvatarEventHost
        {
            public void OnStateChanged(WorldEvent evt, WorldEventState from, WorldEventState to) => throw new InvalidOperationException("host");
            public void OnWave(WorldEvent evt, int wave, int bandTop) => throw new InvalidOperationException("host");
        }

        // ---- the objectives ---------------------------------------------------------------------------------------------------

        [TestMethod]
        public void AnEventFighter_NeverTargetsAnObjectiveSource_TheGauntletFighterIsUnchanged()
        {
            var source = Monster();
            var wave = Monster(x: 56.0f);

            var eventFighter = Fighter();
            eventFighter.Anchor = At(50.0f, InstanceA);
            eventFighter.IsObjectiveSource = c => ReferenceEquals(c, source);
            eventFighter.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { source.PhysicsObj, wave.PhysicsObj });

            CollectionAssert.AreEqual(new[] { wave }, eventFighter.GetNearbyMonsters(false),
                "a Destroy the Source rift is never its target, so it can never destroy the sources by itself");

            var gauntletFighter = Fighter();
            gauntletFighter.IsObjectiveSource = c => ReferenceEquals(c, source);
            gauntletFighter.PhysicsObj.ObjMaint.AddVisibleTargets(new[] { source.PhysicsObj, wave.PhysicsObj });
            Assert.AreEqual(2, gauntletFighter.GetNearbyMonsters(false).Count, "control: no anchor (the Gauntlet), no refusal");
        }

        [TestMethod]
        public void Hold_AFighterKill_IsNeverARescueKill()
        {
            // the Hold's rescue heal pays only for a player's or an owned pet's kill; the fighter is neither (no PetOwner)
            Assert.IsFalse(ACE.Server.WorldEvents.WorldEventHoldRules.IsRescueKill(killerIsPlayer: false, killerHasPetOwner: Fighter().P_PetOwner != null));
        }
    }
}
