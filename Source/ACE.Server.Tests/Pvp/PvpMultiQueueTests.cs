using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Multi-queue (pvp_arena_multi_queue_enabled; Docs/Pvp/DESIGN.md "Joining the queue"): one entry per room, held while a match
    /// the player holds a seat in is pending, removed from the rest at placement, removed from all on a player-caused failure,
    /// kept on a no-fault one, and offline players pruned. The rig implements BOTH halves of each seam (as BattlegroundCoordinatorTests
    /// does), so every room - 1v1, 2v2, Tugak Brawl and the battleground room - is open in one coordinator. Its dials are injected;
    /// ClassInitialize seeds every registered PropertyManager default (the denylist and template switch reads go through it), as
    /// PvpMatchCoordinatorTests does - defaults only, nothing to restore.
    /// </summary>
    [TestClass]
    public class PvpMultiQueueTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private const uint P = 0x50000101, Q = 0x50000102, R = 0x50000103, S = 0x50000104, X = 0x50000105;
        private const uint B1 = 0x50000111, B2 = 0x50000112, B3 = 0x50000113, F1 = 0x50000121, F2 = 0x50000122, F3 = 0x50000123;

        private static readonly DateTime T0 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        private const string DefaultTemplateKey = "duelist";

        private static PvpTemplateRecord TemplateRow(string key, string displayName, uint version, bool enabled, string modes) => new PvpTemplateRecord
        {
            TemplateKey = key,
            DisplayName = displayName,
            Version = version,
            Enabled = enabled,
            Modes = modes,
            DefinitionJson = PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = key, Version = version, DisplayName = displayName }),
            SnapshotAt = T0,
            SnapshotBy = "test",
        };

        // ================= fakes =================

        private sealed class FakePlayer
        {
            public PvpPlayerFacts Facts;
            public bool Online = true;
            public uint Instance;
            public Position Location;
        }

        private sealed class FakeGateway : IPvpPlayerGateway, IBattlegroundPlayerGateway
        {
            public readonly Dictionary<uint, FakePlayer> Players = new();
            public readonly List<(uint Id, string Text)> Messages = new();
            public readonly List<(uint Id, PvpPlayerBinding Binding, Position Exit, Position Spawn)> Entered = new();
            public readonly List<(uint Id, Guid Match)> Prompts = new();

            public FakePlayer Add(uint id, string name, params uint[] fellowship)
            {
                var p = new FakePlayer
                {
                    Facts = new PvpPlayerFacts(id, name, $"10.0.1.{id & 0xFF}", false, false, false, false, false, false, false, fellowship, PreferredTemplateKey: DefaultTemplateKey, HasClearedThread: true),
                    Location = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0),
                };

                Players[id] = p;
                return p;
            }

            public PvpPlayerFacts GetFacts(uint id) => Players.TryGetValue(id, out var p) && p.Online ? p.Facts : null;

            public bool IsOnline(uint id) => Players.TryGetValue(id, out var p) && p.Online;

            public Position GetCurrentPosition(uint id) => IsOnline(id) ? new Position(Players[id].Location) : null;

            public PvpPresence GetPresence(uint id, uint matchInstance) =>
                !IsOnline(id) ? PvpPresence.Offline : Players[id].Instance == matchInstance ? PvpPresence.InInstance : PvpPresence.Elsewhere;

            public void EnterAndTeleport(uint id, PvpPlayerBinding binding, Position exitTo, Position spawn)
            {
                Entered.Add((id, binding, exitTo, spawn));
                Players[id].Instance = spawn.Instance;
            }

            public void PublishBinding(uint id, PvpPlayerBinding binding) { }

            public void ExitMatch(uint id, string context) { }

            public void ReturnToExit(uint id, Position exitTo, uint matchInstance)
            {
                if (Players.TryGetValue(id, out var p) && p.Online && p.Instance == matchInstance)
                    p.Instance = 0;
            }

            /// <summary>Every Send the coordinator made, recorded BEFORE the online check (Messages holds only what an online player saw).</summary>
            public readonly List<(uint Id, string Text)> RawSends = new();

            public void Send(uint id, string text)
            {
                RawSends.Add((id, text));

                if (IsOnline(id))
                    Messages.Add((id, text));
            }

            public bool SendAcceptPrompt(uint id, Guid matchId, string text)
            {
                if (!IsOnline(id))
                    return false;

                Prompts.Add((id, matchId));
                return true;
            }

            public void AbortAcceptPrompt(uint id, Guid matchId) { }

            public void GrantArenaBlood(uint id, int amount, PvpBloodGrant grant) { }

            public void ApplyDailyFacts(PvpMatchFacts facts) { }

            // ---- battleground half ----

            public BattlegroundZoneSample SampleZone(uint id, uint matchInstance) =>
                IsOnline(id) ? new BattlegroundZoneSample(id, 0, null, Players[id].Instance == matchInstance, false, false, 0, 0, 0) : null;

            public void DrainVitals(uint id, Guid matchId, uint matchInstance, int health, int stamina, int mana, bool lethal) { }

            public void Respawn(uint id, Guid matchId, Position spawn)
            {
                if (Players.TryGetValue(id, out var p) && p.Online)
                    p.Instance = spawn.Instance;
            }

            public bool IsInDeathProcess(uint id) => false;

            public void ExitMatchFromPen(uint id, string context) { }

            // ---- PvP Template Facets ----

            public string CheckTemplateRoom(uint id, PvpTemplateDefinition template) => null;

            public void RunTemplateBackstop(uint id, Guid matchId) { }

            public bool Got(uint id, string text) => Messages.Any(m => m.Id == id && m.Text == text);

            public int Count(uint id, string text) => Messages.Count(m => m.Id == id && m.Text == text);
        }

        private sealed class FakeSpaces : IPvpMatchSpaces, IBattlegroundMatchSpaces
        {
            private ushort next = 0x0300;

            public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<uint> ids)
            {
                var instance = Position.InstanceIDFromVars(map.RealmId, next++, isTemporaryRuleset: true);
                return MatchSpaceAllocation.Ok(new MatchSpace(map.MapKey, map.LandblockId, map.RealmId, instance, ids[0]));
            }

            public MatchSpaceReadiness GetReadiness(MatchSpace space) => MatchSpaceReadiness.Ready;

            public void Release(MatchSpace space) { }

            private static BattlegroundFixtureJob Done(IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                var job = new BattlegroundFixtureJob();
                job.Complete(pieces.Count);
                return job;
            }

            public BattlegroundFixtureJob SpawnFixtures(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces) => Done(pieces);

            public BattlegroundFixtureJob SpawnStartGates(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces) => Done(pieces);

            public void RemoveStartGates(MatchSpace space, BattlegroundFixtureJob placed) { }

            public BattlegroundFixtureJob SpawnZoneMarkers(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces) => Done(pieces);

            public void RemoveZoneMarkers(MatchSpace space, BattlegroundFixtureJob placed) { }
        }

        private sealed class FakeSink : IPvpResultSink
        {
            public List<PvpTemplateRecord> TemplateRows = new() { TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1,2v2,ffa,bg_koth") };

            public void LoadRatings(Action<List<PvpRatingRecord>> callback) => callback(new List<PvpRatingRecord>());

            public void LoadTemplates(Action<List<PvpTemplateRecord>, PvpTemplateStoreStatus> callback) =>
                callback(TemplateRows.Select(r => r.Clone()).ToList(), PvpTemplateStoreStatus.Ok);

            public void SaveParticipantTemplates(uint dbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps, Action<PvpTemplateStoreStatus> callback) => callback(PvpTemplateStoreStatus.Ok);

            public void LoadLatestParticipantTemplates(Action<List<PvpLatestParticipantTemplateRecord>, PvpTemplateStoreStatus> callback) =>
                callback(new List<PvpLatestParticipantTemplateRecord>(), PvpTemplateStoreStatus.Ok);

            public void SaveMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, Action<PvpMatchSaveResult, uint> callback) =>
                callback(PvpMatchSaveResult.Saved, 1);
        }

        private sealed class RecordingCrier : IPvpArenaCrierAnnouncer
        {
            public readonly List<string> Lines = new();

            public void Announce(IReadOnlyList<string> lines, string senderName) => Lines.AddRange(lines);
        }

        private sealed class Rig
        {
            public DateTime Now = T0;
            public readonly FakeGateway Gateway = new();
            public readonly FakeSpaces Spaces = new();
            public readonly FakeSink Sink = new();
            public readonly RecordingCrier Crier = new();
            public readonly ConcurrentQueue<PvpIntent> Intents = new();
            public PvpArenaDials Dials = PvpTunables.Defaults with { CrierEnabled = false };
            public BattlegroundDials Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0 };
            public PvpConductDials Conduct = PvpConductTunables.Defaults with { FloorEnabled = false };
            public PvpMatchCoordinator Coordinator;

            public Rig Build()
            {
                Coordinator = new PvpMatchCoordinator(Gateway, Spaces, Sink, () => Now, () => Dials, Intents, Crier, () => Bg, conductDialSource: () => Conduct);
                Coordinator.TeamColourCoin = () => true;
                Coordinator.BeginLoadRatings();
                Coordinator.BeginLoadTemplates();
                Coordinator.BeginLoadLatestTemplates();
                Coordinator.Tick();
                return this;
            }

            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

            public void Tick() => Coordinator.Tick();

            public PvpMatch MatchOf(uint id) => Coordinator.FindMatch(id);

            public void Join(uint id, string mode, bool duo = false) =>
                Assert.IsTrue(Coordinator.Join(id, mode, duo).Joined, $"join 0x{id:X8} {mode}");

            public void AcceptAll(params uint[] ids)
            {
                foreach (var id in ids)
                    Assert.AreEqual(PvpAnswerOutcome.Accepted, Coordinator.Accept(id).Outcome, $"accept 0x{id:X8}");
            }

            public IReadOnlyList<uint> Queued(string mode) => Coordinator.QueuedIds(mode);
        }

        private static Rig NewRig(Action<Rig> configure = null)
        {
            var rig = new Rig();
            configure?.Invoke(rig);
            rig.Build();

            foreach (var (id, name) in new[] { (P, "Papa"), (Q, "Quebec"), (R, "Romeo"), (S, "Sierra"), (B1, "Bone"), (B2, "Btwo"), (B3, "Bthree"), (F1, "Fone"), (F2, "Ftwo"), (F3, "Fthree") })
                rig.Gateway.Add(id, name);

            return rig;
        }

        /// <summary>P and X become a fellowship of two (a 2v2 duo).</summary>
        private static void AddDuo(Rig rig)
        {
            rig.Gateway.Add(P, "Papa", P, X);
            rig.Gateway.Add(X, "Xray", P, X);
        }

        // ================= joining =================

        [TestMethod]
        public void Join_TwoRooms_Ok_SameRoomTwice_Refused_WithTheSameModeLine()
        {
            var rig = NewRig();

            var first = rig.Coordinator.Join(P, "1v1", false);
            var second = rig.Coordinator.Join(P, "2v2", false);

            Assert.IsTrue(first.Joined);
            Assert.IsTrue(second.Joined, "a second room is accepted with the switch on");
            CollectionAssert.AreEqual(new[] { "2v2", "1v1" }, second.QueuedModeKeys.ToList(), "the queues, in matchmaking order");

            var again = rig.Coordinator.Join(P, "1v1", false);

            Assert.AreEqual(PvpJoinRefusal.AlreadyQueued, again.Refusal);
            Assert.AreEqual("[Arena] You are already in the 1v1 queue. Type /arena status to see your queues.", PvpMatchCoordinator.RefusalText(again));
            CollectionAssert.AreEqual(new[] { P }, rig.Queued("1v1").ToList(), "still one entry in 1v1");
            CollectionAssert.AreEqual(new[] { P }, rig.Queued("2v2").ToList());
        }

        [TestMethod]
        public void Join_SwitchOff_RefusesASecondRoom_AsBefore()
        {
            var rig = NewRig(r => r.Dials = r.Dials with { MultiQueueEnabled = false });

            rig.Join(P, "1v1");
            var second = rig.Coordinator.Join(P, "2v2", false);

            Assert.AreEqual(PvpJoinRefusal.AlreadyQueued, second.Refusal);
            Assert.AreEqual("1v1", second.ModeKey, "names the queue they are in");
            Assert.AreEqual("[Arena] You are already in the 1v1 queue. Type /arena leave first.", PvpMatchCoordinator.RefusalText(second), "the old line");
            Assert.AreEqual(0, rig.Queued("2v2").Count);
        }

        [TestMethod]
        public void Join_SwitchTurnedOff_LeavesExistingMultiQueuesAlone_AndStillHoldsAndWithdraws()
        {
            var rig = NewRig();

            rig.Join(P, "1v1");
            rig.Join(P, "2v2");

            rig.Dials = rig.Dials with { MultiQueueEnabled = false };
            rig.Tick();

            CollectionAssert.AreEqual(new[] { P }, rig.Queued("1v1").ToList(), "the existing entries are left alone");
            CollectionAssert.AreEqual(new[] { P }, rig.Queued("2v2").ToList());
            Assert.AreEqual(PvpJoinRefusal.AlreadyQueued, rig.Coordinator.Join(P, "ffa", false).Refusal, "but a third is refused");

            // A match still forms with the switch off, and the hold and the placement withdrawal still apply.
            rig.Join(Q, "1v1");
            rig.Tick();

            Assert.IsNotNull(rig.MatchOf(P));
            Assert.IsTrue(rig.Coordinator.Status(P).Queues.Single(q => q.ModeKey == "2v2").Held, "P's 2v2 entry is held while the 1v1 pop is pending");
            Assert.AreEqual(1, rig.Coordinator.Join(R, "2v2", false).WaitingCount, "the held entry is not counted: R is alone");

            rig.AcceptAll(P, Q);
            rig.Tick(); // Staging
            rig.Tick(); // dispatch

            CollectionAssert.AreEqual(new[] { R }, rig.Queued("2v2").ToList(), "P left 2v2 at placement");
            Assert.IsTrue(rig.Gateway.Got(P, "[Arena] You entered a 1v1 match, so you left your other queues: 2v2."));
        }

        // ================= same tick =================

        [TestMethod]
        public void TwoRoomsReadySameTick_ExactlyOneMatch_BattlegroundWinsOverArena()
        {
            var rig = NewRig();

            // P is ready in the bg room (4 players) AND in 1v1 (with Q). The arena room is first in dictionary order; bg must still win.
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");

            foreach (var id in new[] { P, B1, B2, B3 })
                rig.Join(id, "bg");

            rig.Tick();

            var m = rig.MatchOf(P);

            Assert.IsNotNull(m);
            Assert.AreEqual(BattlegroundModes.KothModeKey, m.ModeKey, "the battleground formed");
            Assert.IsNull(rig.MatchOf(Q), "1v1 did not form: P is held");
            Assert.AreEqual(1, rig.Gateway.Prompts.Count(p => p.Id == P), "P has exactly one popup");
            CollectionAssert.AreEquivalent(new[] { P, Q }, rig.Queued("1v1").ToList(), "P's 1v1 entry is held, not removed, until placement");
        }

        // ================= no-fault failure =================

        [TestMethod]
        public void OpponentDeclines_PlayerBackAtFrontOfPoppedRoom_AndStillInTheOthers()
        {
            var rig = NewRig();

            rig.Join(R, "1v1");
            rig.Advance(1);
            rig.Join(P, "2v2");
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");

            // R and P are the two oldest 1v1 entries; Q waits.
            rig.Tick();

            var m = rig.MatchOf(P);
            Assert.IsNotNull(m);
            var opponent = m.Teams.SelectMany(t => t.Members).Select(x => x.CharacterId).Single(id => id != P);

            rig.Coordinator.Decline(opponent);

            Assert.IsNull(rig.MatchOf(P));
            Assert.AreEqual(P, rig.Queued("1v1").First(), "P is at the front of the popped room");
            CollectionAssert.AreEqual(new[] { P }, rig.Queued("2v2").ToList(), "and still in 2v2");
            Assert.IsFalse(rig.Gateway.Messages.Any(msg => msg.Id == P && msg.Text.Contains("left all your arena queues")));
        }

        // ================= player-caused failure =================

        [TestMethod]
        public void OwnDecline_RemovesFromAllQueues_AndNamesThem()
        {
            var rig = NewRig();

            rig.Join(P, "1v1");
            rig.Join(P, "2v2");
            rig.Join(P, "ffa");
            rig.Join(Q, "1v1");
            rig.Tick();

            Assert.IsNotNull(rig.MatchOf(P));

            rig.Coordinator.Decline(P);

            Assert.AreEqual(0, rig.Queued("2v2").Count);
            Assert.AreEqual(0, rig.Queued("ffa").Count);
            Assert.IsFalse(rig.Queued("1v1").Contains(P), "the decliner is never requeued");
            CollectionAssert.AreEqual(new[] { Q }, rig.Queued("1v1").ToList(), "the opponent is back in 1v1");
            Assert.IsTrue(rig.Gateway.Got(P, "[Arena] You declined, so you left all your arena queues: Tugak Brawl, 2v2."));
        }

        [TestMethod]
        public void AcceptTimeout_AllOrNothing_RemovesFromAllQueues()
        {
            var rig = NewRig();

            rig.Join(P, "1v1");
            rig.Join(P, "2v2");
            rig.Join(Q, "1v1");
            rig.Tick();

            rig.AcceptAll(Q);
            rig.Advance(rig.Dials.AcceptSeconds);
            rig.Tick();

            Assert.IsNull(rig.MatchOf(P));
            Assert.AreEqual(0, rig.Queued("2v2").Count);
            CollectionAssert.AreEqual(new[] { Q }, rig.Queued("1v1").ToList());
            Assert.IsTrue(rig.Gateway.Got(P, "[Arena] You did not answer in time, so you left all your arena queues: 2v2."));
            Assert.IsFalse(rig.Gateway.Messages.Any(msg => msg.Id == Q && msg.Text.Contains("left all your arena queues")), "the player who answered keeps theirs");
        }

        [TestMethod]
        public void AcceptTimeout_TugakDroppedSeat_RemovesFromAllQueues_MatchProceeds()
        {
            var rig = NewRig(r => r.Dials = r.Dials with { FfaMinPlayers = 3, FfaTargetPlayers = 4, FfaMaxPlayers = 4 });

            foreach (var id in new[] { F1, F2, F3, P })
                rig.Join(id, "ffa");

            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick();

            var m = rig.MatchOf(P);
            Assert.IsNotNull(m);
            Assert.AreEqual(ArenaMapCatalog.FfaKey, m.ModeKey, "Tugak Brawl is matched before 1v1");
            Assert.IsNull(rig.MatchOf(Q));

            rig.AcceptAll(F1, F2, F3);
            rig.Advance(rig.Dials.AcceptSeconds);
            rig.Tick();

            Assert.IsNull(rig.MatchOf(P), "P's seat was dropped");
            Assert.IsNotNull(rig.MatchOf(F1), "the lobby proceeds with three");
            CollectionAssert.AreEqual(new[] { Q }, rig.Queued("1v1").ToList(), "P left 1v1");
            Assert.IsTrue(rig.Gateway.Got(P, "[Arena] You did not answer in time, so you left all your arena queues: 1v1."));
        }

        [TestMethod]
        public void TemplateWithdrawnByAdmin_AtAccept_KeepsOtherQueues()
        {
            var rig = NewRig();

            rig.Join(P, "1v1");
            rig.Join(P, "2v2");
            rig.Join(Q, "1v1");
            rig.Tick();
            Assert.IsNotNull(rig.MatchOf(P));

            // An admin takes the template off 1v1 after the pop; the accept re-check refuses it without fault (lockOut false).
            rig.Sink.TemplateRows = new() { TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "2v2,ffa,bg_koth") };
            rig.Coordinator.BeginLoadTemplates();
            rig.Tick();

            Assert.AreEqual(PvpAnswerOutcome.Declined, rig.Coordinator.Accept(P).Outcome);

            CollectionAssert.AreEqual(new[] { P }, rig.Queued("2v2").ToList(), "P keeps the 2v2 queue");
            Assert.IsFalse(rig.Gateway.Messages.Any(msg => msg.Id == P && msg.Text.Contains("left all your arena queues")));
            Assert.IsFalse(rig.Coordinator.IsLockedOut(P));
        }

        // ================= placement =================

        [TestMethod]
        public void Placement_RemovesOtherQueues_DuoElsewhereShrinksToSolo_PartnerTold()
        {
            var rig = NewRig();
            AddDuo(rig);

            rig.Join(P, "2v2", duo: true);
            rig.Advance(5);
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick();

            Assert.IsNotNull(rig.MatchOf(P));
            Assert.IsTrue(rig.Coordinator.Status(X).Queues.Single().Held, "the partner's duo entry is paused while P's match is pending");

            rig.AcceptAll(P, Q);
            rig.Tick(); // Staging, allocate
            rig.Tick(); // dispatch

            Assert.IsTrue(rig.Gateway.Entered.Any(e => e.Id == P), "P was placed");
            CollectionAssert.AreEqual(new[] { X }, rig.Queued("2v2").ToList(), "the duo shrank to X");
            Assert.IsTrue(rig.Gateway.Got(P, "[Arena] You entered a 1v1 match, so you left your other queues: 2v2."));
            Assert.IsTrue(rig.Gateway.Got(X, "[Arena] Papa entered another match and left your 2v2 group. You are still in the 2v2 queue."));

            var xEntry = rig.Coordinator.Status(X).Queues.Single();
            Assert.IsFalse(xEntry.Held);
            Assert.AreEqual(rig.Now - T0, xEntry.Waited, "the shrunk unit keeps the duo's queue time");
        }

        // ================= counts =================

        [TestMethod]
        public void Crier_CountExcludesHeldUnits()
        {
            var rig = NewRig(r => r.Dials = r.Dials with { CrierEnabled = true, CrierAnnounceOnJoin = true });

            rig.Join(P, "2v2");
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick(); // 1v1 forms; P is held, so 2v2 counts 0

            rig.Join(R, "2v2");
            rig.Tick();

            var last2v2 = rig.Crier.Lines.Last(l => l.StartsWith("2v2 arena queue:"));
            StringAssert.Contains(last2v2, "1 queued", "P's held 2v2 entry is not counted");
        }

        [TestMethod]
        public void FfaLobby_AndJoinAndStatusCounts_ExcludeHeldUnits()
        {
            var rig = NewRig();

            rig.Join(P, "ffa");
            rig.Join(S, "ffa");
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick(); // Tugak (2 waiting) is announced before 1v1 forms and holds P
            rig.Tick(); // now the lobby is 1

            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Id == S && m.Text.StartsWith("[Arena] Tugak Brawl lobby: 1 of")), "S hears the lobby without P");
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Id == P && m.Text.StartsWith("[Arena] Tugak Brawl lobby: 1 of")), "the held player is not told");

            var join = rig.Coordinator.Join(R, "ffa", false);
            Assert.AreEqual(2, join.WaitingCount, "S and R; P is held");

            var status = rig.Coordinator.Status(S);
            Assert.AreEqual(2, status.WaitingCount);
            Assert.AreEqual(2, status.Queues.Single().WaitingCount);
        }

        // ================= status and leave =================

        [TestMethod]
        public void Status_ChecksTheMatchFirst_AndListsHeldQueues()
        {
            var rig = NewRig();

            rig.Join(P, "2v2");
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick();

            var status = rig.Coordinator.Status(P);

            Assert.AreEqual(PvpStatusKind.AwaitingAccept, status.Kind, "the pending match, not the queue");
            Assert.AreEqual("1v1", status.ModeKey);
            var entry = status.Queues.Single();
            Assert.AreEqual("2v2", entry.ModeKey);
            Assert.IsTrue(entry.Held);

            var text = PvpArenaCommands.StatusResultText(status);
            StringAssert.Contains(text, "[Arena] Your queues:");
            StringAssert.Contains(text, "  2v2: paused while your 1v1 match is pending.");
        }

        [TestMethod]
        public void Leave_NoMode_ChecksTheMatchFirst()
        {
            var rig = NewRig();

            rig.Join(P, "2v2");
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick();

            var result = rig.Coordinator.Leave(P);

            Assert.AreEqual(PvpLeaveOutcome.DeclinedMatch, result.Outcome, "the pending match is declined, not the 2v2 queue left");
            Assert.IsNull(rig.MatchOf(P));
            Assert.AreEqual(0, rig.Queued("2v2").Count, "a decline is player-caused: every queue goes");
        }

        [TestMethod]
        public void Leave_OneMode_ThenAll()
        {
            var rig = NewRig();

            rig.Join(P, "1v1");
            rig.Join(P, "2v2");
            rig.Join(P, "ffa");

            var one = rig.Coordinator.Leave(P, "2v2");
            Assert.AreEqual(PvpLeaveOutcome.LeftQueue, one.Outcome);
            CollectionAssert.AreEqual(new[] { "2v2" }, one.ModeKeys.ToList());
            Assert.AreEqual("[Arena] You left the 2v2 queue.", PvpArenaCommands.LeaveResultText(one));
            Assert.AreEqual(0, rig.Queued("2v2").Count);
            CollectionAssert.AreEqual(new[] { P }, rig.Queued("1v1").ToList());

            var again = rig.Coordinator.Leave(P, "2v2");
            Assert.AreEqual(PvpLeaveOutcome.NotInMode, again.Outcome);
            Assert.AreEqual("[Arena] You are not in the 2v2 queue.", PvpArenaCommands.LeaveResultText(again));

            var all = rig.Coordinator.Leave(P);
            Assert.AreEqual(PvpLeaveOutcome.LeftQueue, all.Outcome);
            CollectionAssert.AreEqual(new[] { "ffa", "1v1" }, all.ModeKeys.ToList());
            Assert.AreEqual("[Arena] You left all your arena queues: Tugak Brawl, 1v1.", PvpArenaCommands.LeaveResultText(all));
            Assert.AreEqual(PvpStatusKind.Idle, rig.Coordinator.Status(P).Kind);

            Assert.AreEqual(PvpLeaveOutcome.NotQueued, rig.Coordinator.Leave(P).Outcome);
        }

        // ================= offline =================

        [TestMethod]
        public void OfflinePrune_ShrinksADuo_PartnerTold_OfflinePlayerSilent()
        {
            var rig = NewRig();
            AddDuo(rig);

            rig.Join(P, "2v2", duo: true);
            rig.Join(X, "1v1");

            rig.Gateway.Players[X].Online = false;
            rig.Tick();

            CollectionAssert.AreEqual(new[] { P }, rig.Queued("2v2").ToList(), "the duo shrank to P");
            Assert.AreEqual(0, rig.Queued("1v1").Count, "X left every queue");
            Assert.IsTrue(rig.Gateway.Got(P, "[Arena] Xray went offline and left your 2v2 group. You are still in the 2v2 queue."));
            Assert.IsFalse(rig.Gateway.RawSends.Any(m => m.Id == X), "nothing is even attempted for the offline player");
        }

        // ================= battleground rejoin grace =================

        [TestMethod]
        public void BattlegroundRejoinGrace_JoinElsewhere_IsAlreadyInMatch()
        {
            var rig = NewRig();

            foreach (var id in new[] { P, B1, B2, B3 })
                rig.Join(id, "bg");

            rig.Tick();
            rig.AcceptAll(P, B1, B2, B3);
            rig.Tick(); // Staging
            rig.Tick(); // dispatch
            rig.Tick(); // Countdown
            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick(); // Live
            Assert.AreEqual(PvpMatchState.Live, rig.MatchOf(P).State);

            rig.Gateway.Players[P].Online = false;
            rig.Tick(); // grace begins: the seat is held

            rig.Gateway.Players[P].Online = true;

            Assert.AreEqual(PvpJoinRefusal.AlreadyInMatch, rig.Coordinator.Join(P, "1v1", false).Refusal);
            Assert.AreEqual(0, rig.Queued("1v1").Count);
        }

        // ================= review round 1 =================

        [TestMethod]
        public void DenylistedAtStaging_LeavesEveryQueue_AndCannotPopASecondMatch()
        {
            var saved = PvpArenaDenylistTunables.DialSource;

            try
            {
                var rig = NewRig();

                rig.Join(P, "1v1");
                rig.Join(P, "2v2");
                rig.Join(Q, "1v1");
                rig.Tick();
                Assert.IsNotNull(rig.MatchOf(P));

                rig.AcceptAll(P, Q);

                // Denylisted after the accept: the staging re-check catches it.
                PvpArenaDenylistTunables.DialSource = () => new PvpArenaDenylistDials(new HashSet<uint> { P });
                rig.Tick();

                Assert.IsNull(rig.MatchOf(P), "the match failed before placement");
                Assert.IsTrue(rig.Gateway.Got(P, PvpArenaText.Denylisted));
                Assert.AreEqual(0, rig.Coordinator.Status(P).Queues.Count, "P is in no room");
                Assert.IsFalse(rig.Queued("2v2").Contains(P));
                CollectionAssert.AreEqual(new[] { Q }, rig.Queued("1v1").ToList(), "Q is back in 1v1");

                // A full 2v2 room forms without P: there is no held entry left to pop a second match.
                foreach (var id in new[] { R, S, F1, F2 })
                    rig.Join(id, "2v2");

                rig.Tick();

                Assert.IsNull(rig.MatchOf(P));
                Assert.IsNotNull(rig.MatchOf(R));
            }
            finally
            {
                PvpArenaDenylistTunables.DialSource = saved;
            }
        }

        [TestMethod]
        public void OwnDecline_DuoPartnerElsewhere_HearsPartnerLeftGroup()
        {
            var rig = NewRig();
            AddDuo(rig);

            rig.Join(P, "2v2", duo: true);
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick();

            rig.Coordinator.Decline(P);

            CollectionAssert.AreEqual(new[] { X }, rig.Queued("2v2").ToList(), "the duo shrank to X");
            Assert.IsTrue(rig.Gateway.Got(X, "[Arena] Papa left your 2v2 group. You are still in the 2v2 queue."));
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Id == X && m.Text.Contains("entered another match")), "the placement line is not used for a decline");
        }

        [TestMethod]
        public void Placement_BattlegroundGroupElsewhere_ShrinksViaSubset()
        {
            var rig = NewRig(r => r.Bg = r.Bg with { GroupJoinEnabled = true });
            rig.Gateway.Add(P, "Papa", P, X, R);
            rig.Gateway.Add(X, "Xray", P, X, R);
            rig.Gateway.Add(R, "Romeo", P, X, R);

            Assert.IsTrue(rig.Coordinator.Join(P, "bg", false, null, group: true).Joined);
            rig.Advance(7);
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick();

            Assert.AreEqual("1v1", rig.MatchOf(P).ModeKey, "a group of 3 is under the bg minimum, so 1v1 pops");

            rig.AcceptAll(P, Q);
            rig.Tick(); // Staging
            rig.Tick(); // dispatch

            CollectionAssert.AreEquivalent(new[] { X, R }, rig.Queued("bg").ToList(), "the group shrank to X and R");
            Assert.IsTrue(rig.Gateway.Got(X, "[Arena] Papa entered another match and left your Battleground group. You are still in the Battleground queue."));
            Assert.IsTrue(rig.Gateway.Got(R, "[Arena] Papa entered another match and left your Battleground group. You are still in the Battleground queue."));
            Assert.AreEqual(rig.Now - T0, rig.Coordinator.Status(X).Queues.Single().Waited, "the shrunk group keeps its queue time");
            Assert.IsTrue(rig.Gateway.Got(P, "[Arena] You entered a 1v1 match, so you left your other queues: Battleground."));
        }

        [TestMethod]
        public void LeaveMode_WhileAMatchIsPending_LeavesThatQueue_MatchUntouched()
        {
            var rig = NewRig();

            rig.Join(P, "2v2");
            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick();

            var result = rig.Coordinator.Leave(P, "2v2");

            Assert.AreEqual(PvpLeaveOutcome.LeftQueue, result.Outcome);
            Assert.AreEqual(0, rig.Queued("2v2").Count);
            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(P).State, "the pending match is not declined");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(P));
        }

        [TestMethod]
        public void FormMatchGuard_RefusesAnAlreadyHeldCharacter_EvenWithTheHoldFilterOff()
        {
            var rig = NewRig();
            rig.Coordinator.DisableHoldFilterForTest = true;

            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");

            foreach (var id in new[] { P, B1, B2, B3 })
                rig.Join(id, "bg");

            rig.Tick();

            Assert.AreEqual(BattlegroundModes.KothModeKey, rig.MatchOf(P).ModeKey, "bg formed first");
            Assert.IsNull(rig.MatchOf(Q), "the 1v1 matchmaker proposed held P; the guard formed nothing");
            Assert.AreEqual(1, rig.Gateway.Prompts.Count(p => p.Id == P), "P has exactly one popup");
        }

        [TestMethod]
        public void TugakDecline_RemovesFromAllQueues_LobbyProceeds()
        {
            var rig = NewRig(r => r.Dials = r.Dials with { FfaMinPlayers = 3, FfaTargetPlayers = 4, FfaMaxPlayers = 4 });

            foreach (var id in new[] { F1, F2, F3, P })
                rig.Join(id, "ffa");

            rig.Join(P, "1v1");
            rig.Join(Q, "1v1");
            rig.Tick();
            Assert.AreEqual(ArenaMapCatalog.FfaKey, rig.MatchOf(P).ModeKey);

            rig.Coordinator.Decline(P);

            CollectionAssert.AreEqual(new[] { Q }, rig.Queued("1v1").ToList(), "P left 1v1 on the decline itself");
            Assert.IsTrue(rig.Gateway.Got(P, "[Arena] You declined, so you left all your arena queues: 1v1."));

            rig.AcceptAll(F1, F2, F3);
            rig.Tick();

            Assert.IsNull(rig.MatchOf(P), "the decliner's seat was dropped");
            Assert.IsNotNull(rig.MatchOf(F1), "the lobby proceeds with three");
        }

        // ================= text helpers =================

        [TestMethod]
        public void Text_JoinedMulti_OnlyForTwoOrMoreQueues()
        {
            var one = new PvpJoinResult(PvpJoinRefusal.None, "1v1", 1, QueuedModeKeys: new[] { "1v1" });
            var two = new PvpJoinResult(PvpJoinRefusal.None, "1v1", 1, QueuedModeKeys: new[] { "bg", "1v1" });

            Assert.IsNull(PvpArenaCommands.JoinedMultiText(one));
            Assert.IsNull(PvpArenaCommands.JoinedMultiText(new PvpJoinResult(PvpJoinRefusal.AlreadyQueued, "1v1", QueuedModeKeys: new[] { "bg", "1v1" })));
            Assert.AreEqual("[Arena] You are now in 2 queues: Battleground, 1v1. Entering a match from any of them removes you from the others.", PvpArenaCommands.JoinedMultiText(two));
        }

        [TestMethod]
        public void Text_ParseLeave()
        {
            Assert.AreEqual(PvpArenaCommands.LeaveParseOutcome.Default, PvpArenaCommands.ParseLeave(Array.Empty<string>()).Outcome);
            Assert.AreEqual(PvpArenaCommands.LeaveParseOutcome.Confirm, PvpArenaCommands.ParseLeave(new[] { "CONFIRM" }).Outcome);
            Assert.AreEqual((PvpArenaCommands.LeaveParseOutcome.Mode, "ffa"), PvpArenaCommands.ParseLeave(new[] { "Tugak" }));
            Assert.AreEqual((PvpArenaCommands.LeaveParseOutcome.Mode, "bg"), PvpArenaCommands.ParseLeave(new[] { "bg" }));
            Assert.AreEqual((PvpArenaCommands.LeaveParseOutcome.Mode, "2v2"), PvpArenaCommands.ParseLeave(new[] { "2v2" }));
            Assert.AreEqual(PvpArenaCommands.LeaveParseOutcome.BadUsage, PvpArenaCommands.ParseLeave(new[] { "3v3" }).Outcome);
            Assert.AreEqual(PvpArenaCommands.LeaveParseOutcome.BadUsage, PvpArenaCommands.ParseLeave(new[] { "1v1", "confirm" }).Outcome);
        }

        [TestMethod]
        public void Text_StatusLines()
        {
            var waiting = new PvpQueueEntry("2v2", TimeSpan.FromSeconds(90), 3, null, null, false);
            var templated = new PvpQueueEntry("ffa", TimeSpan.FromSeconds(30), 4, "duelist", "Duelist", false);

            Assert.AreEqual("  2v2: waiting 1 minute, 3 queued.", PvpArenaCommands.StatusQueueLineText(waiting, null));
            Assert.AreEqual("  Tugak Brawl on the Duelist template: waiting 30 seconds, 4 queued.", PvpArenaCommands.StatusQueueLineText(templated, null));

            var single = new PvpStatusResult(PvpStatusKind.Queued, "2v2", TimeSpan.FromSeconds(90), 3, Queues: new[] { waiting });
            Assert.AreEqual("[Arena] You are in the 2v2 queue (1 minute waited). Players waiting: 3.", PvpArenaCommands.StatusResultText(single), "one queue keeps the old line");

            var multi = new PvpStatusResult(PvpStatusKind.Queued, "ffa", TimeSpan.FromSeconds(30), 4, Queues: new[] { templated, waiting });
            Assert.AreEqual("[Arena] Your queues:\n  Tugak Brawl on the Duelist template: waiting 30 seconds, 4 queued.\n  2v2: waiting 1 minute, 3 queued.", PvpArenaCommands.StatusResultText(multi));
        }

        [TestMethod]
        public void Text_AllNewLines_AreAsciiAndPrefixed()
        {
            var lines = new[]
            {
                PvpArenaText.AlreadyQueuedSameMode, PvpArenaText.JoinedMulti, PvpArenaText.RemovedOnPlacement, PvpArenaText.RemovedOnDecline,
                PvpArenaText.RemovedOnTimeout, PvpArenaText.PartnerWithdrawn, PvpArenaText.PartnerOffline, PvpArenaText.LeftAll, PvpArenaText.NotInMode,
                PvpArenaText.StatusQueuesHeader,
            };

            foreach (var line in lines.Append(PvpArenaText.PartnerLeftGroup))
            {
                StringAssert.StartsWith(line, "[Arena] ");
                Assert.IsTrue(line.All(c => c >= 0x20 && c < 0x7F), line);
            }

            // The per-queue status lines are indented continuations of the header, so they carry no prefix; ASCII all the same.
            foreach (var line in new[] { PvpArenaText.StatusQueueLine, PvpArenaText.StatusQueueLineTemplate, PvpArenaText.StatusQueueHeld })
            {
                StringAssert.StartsWith(line, "  ");
                Assert.IsTrue(line.All(c => c >= 0x20 && c < 0x7F), line);
            }

            Assert.AreEqual("Tugak Brawl, 2v2, 1v1", PvpArenaText.ModeList(new[] { "ffa", "2v2", "1v1" }));
            Assert.IsTrue(PvpTunables.Defaults.MultiQueueEnabled, "the switch ships on");
        }
    }
}
