using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Battleground rejoin through the coordinator (Docs/Pvp/BATTLEGROUNDS.md "Disconnect and rejoin"): a Live battleground logout
    /// holds the seat for pvp_bg_rejoin_grace_seconds instead of forfeiting it, a reconnect within the window puts the player back in
    /// on their own team, and every other logout (arena modes, Tugak Brawl, before Live, server-caused) forfeits exactly as before.
    /// Also the dispatch half of the Thread return (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread"): the seat's exit comes from the
    /// gateway's GetExitPoint. The rig is PvpDeserterCoordinatorTests' (the battleground and conduct halves of both seams), with the
    /// rejoin seams added: AdmitLate, the entries the coordinator sends, and participation-ended calls.
    ///
    /// <para/>
    /// The grace length is a dial (BattlegroundDials.RejoinGraceSeconds), so no PropertyManager key is read for it here; the class
    /// loads the registered defaults the coordinator falls back to, as its sibling does.
    /// </summary>
    [TestClass]
    public class PvpRejoinCoordinatorTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>The Thread away registry is process-static: every test starts and ends with it empty.</summary>
        [TestInitialize]
        public void Setup() => ACE.Server.ThreadDungeons.ThreadPvpAway.ClearForTests();

        [TestCleanup]
        public void Cleanup() => ACE.Server.ThreadDungeons.ThreadPvpAway.ClearForTests();

        private const uint A = 0x50000081, B = 0x50000082, C = 0x50000083, D = 0x50000084, E = 0x50000085;

        private static readonly DateTime T0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        private const string TemplateKey = "duelist";

        private const int Grace = 120;

        // ================= fakes =================

        private sealed class FakePlayer
        {
            public PvpPlayerFacts Facts;
            public bool Online = true;
            public uint Instance;
            public (double X, double Y, double Z) At = (0, 0, 0);
            public PvpActivityStamp Activity;
            public bool Dying;
            public Position Location = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0);
        }

        private sealed record Entry(uint Id, PvpPlayerBinding Binding, Position ExitTo, Position Spawn);

        private class FakeGateway : IPvpPlayerGateway, IBattlegroundPlayerGateway, IPvpConductGateway
        {
            public readonly Dictionary<uint, FakePlayer> Players = new();
            public readonly List<(uint Id, string Text)> Messages = new();
            public readonly List<(uint Id, PvpDeserterRecord Record)> Persisted = new();
            public readonly List<Entry> Entries = new();
            public readonly List<uint> ParticipationEnded = new();

            /// <summary>Ids whose entry does not land (the teleport never arrives), for the entry-failure and arrival-timeout cases.</summary>
            public readonly HashSet<uint> HoldEntry = new();

            /// <summary>Null keeps the interface default (GetCurrentPosition); set to stand in for the live Thread-aware exit.</summary>
            public Func<uint, Position> ExitPoint;

            public FakePlayer Add(uint id, string name)
            {
                var p = new FakePlayer
                {
                    Facts = new PvpPlayerFacts(id, name, $"10.0.1.{id & 0xFF}", false, false, false, false, false, false, false, Array.Empty<uint>(), PreferredTemplateKey: TemplateKey, HasClearedThread: true),
                };

                Players[id] = p;
                return p;
            }

            public bool Dying(uint id) => Players.TryGetValue(id, out var p) && p.Dying;

            public PvpPlayerFacts GetFacts(uint id) => Players.TryGetValue(id, out var p) && p.Online ? p.Facts : null;

            public bool IsOnline(uint id) => Players.TryGetValue(id, out var p) && p.Online;

            public Position GetCurrentPosition(uint id) => IsOnline(id) ? new Position(Players[id].Location) : null;

            public Position GetExitPoint(uint id) => ExitPoint != null ? ExitPoint(id) : GetCurrentPosition(id);

            /// <summary>As the live gateway: the shared ThreadPvpAway helper decides release or shrink (a fake player never teleports).</summary>
            public void OnParticipationEnded(uint id, uint matchInstance)
            {
                ParticipationEnded.Add(id);
                ACE.Server.ThreadDungeons.ThreadPvpAway.ApplyParticipationEnded(id, IsOnline(id), false,
                    matchInstance != 0 && Players.TryGetValue(id, out var p) && p.Instance == matchInstance, DateTime.UtcNow);
            }

            public readonly List<uint> Respawns = new();

            public PvpPresence GetPresence(uint id, uint matchInstance)
            {
                if (!IsOnline(id))
                    return PvpPresence.Offline;

                return Players[id].Instance == matchInstance ? PvpPresence.InInstance : PvpPresence.Elsewhere;
            }

            public void EnterAndTeleport(uint id, PvpPlayerBinding binding, Position exitTo, Position spawn)
            {
                Entries.Add(new Entry(id, binding, exitTo == null ? null : new Position(exitTo), spawn == null ? null : new Position(spawn)));

                if (!HoldEntry.Contains(id))
                    Players[id].Instance = spawn.Instance;
            }

            public void PublishBinding(uint id, PvpPlayerBinding binding) { }

            public void ExitMatch(uint id, string context) { }

            public void ReturnToExit(uint id, Position exitTo, uint matchInstance)
            {
                if (Players.TryGetValue(id, out var p) && p.Online && p.Instance == matchInstance)
                    p.Instance = 0;
            }

            public void Send(uint id, string text)
            {
                if (IsOnline(id))
                    Messages.Add((id, text));
            }

            public bool SendAcceptPrompt(uint id, Guid matchId, string text) => IsOnline(id);

            public void AbortAcceptPrompt(uint id, Guid matchId) { }

            public void GrantArenaBlood(uint id, int amount, PvpBloodGrant grant) { }

            public string CheckTemplateRoom(uint id, PvpTemplateDefinition template) => null;

            public void RunTemplateBackstop(uint id, Guid matchId) { }

            public BattlegroundZoneSample SampleZone(uint id, uint matchInstance)
            {
                if (!IsOnline(id))
                    return null;

                var p = Players[id];
                return new BattlegroundZoneSample(id, 0, null, p.Instance == matchInstance, p.Dying, false, p.At.X, p.At.Y, p.At.Z, p.Activity?.AtUtc, p.Activity?.MatchId);
            }

            public void DrainVitals(uint id, Guid matchId, uint matchInstance, int health, int stamina, int mana, bool lethal) { }

            public void Respawn(uint id, Guid matchId, Position spawn)
            {
                Respawns.Add(id);
                if (!Players.TryGetValue(id, out var p) || !p.Online || p.Dying)
                    return;

                p.Instance = spawn.Instance;
                p.At = (spawn.PositionX, spawn.PositionY, spawn.PositionZ);
            }

            public bool IsInDeathProcess(uint id) => Dying(id);

            public void ExitMatchFromPen(uint id, string context) { }

            public void PersistDeserterRecord(uint id, PvpDeserterRecord record) => Persisted.Add((id, record));

            public bool Got(uint id, string text) => Messages.Any(m => m.Id == id && m.Text == text);
        }

        private sealed class FakeSpaces : IPvpMatchSpaces, IBattlegroundMatchSpaces
        {
            private ushort next = 0x0400;

            /// <summary>What AdmitLate answers (the live provider is false once the space is gone).</summary>
            public bool Admits = true;
            public readonly List<uint> Admitted = new();

            public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<uint> ids)
            {
                var instance = Position.InstanceIDFromVars(map.RealmId, next++, isTemporaryRuleset: true);
                return MatchSpaceAllocation.Ok(new MatchSpace(map.MapKey, map.LandblockId, map.RealmId, instance, ids[0]));
            }

            public MatchSpaceReadiness GetReadiness(MatchSpace space) => MatchSpaceReadiness.Ready;

            public void Release(MatchSpace space) { }

            public bool AdmitLate(MatchSpace space, uint characterId)
            {
                Admitted.Add(characterId);
                return Admits;
            }

            private static BattlegroundFixtureJob Done(int count)
            {
                var job = new BattlegroundFixtureJob();
                job.Complete(count);
                return job;
            }

            public BattlegroundFixtureJob SpawnFixtures(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces) => Done(pieces.Count);

            public BattlegroundFixtureJob SpawnZoneMarkers(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces) => Done(pieces.Count);

            public void RemoveZoneMarkers(MatchSpace space, BattlegroundFixtureJob placed) { }

            public BattlegroundFixtureJob SpawnStartGates(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces) => Done(pieces.Count);

            public void RemoveStartGates(MatchSpace space, BattlegroundFixtureJob placed) { }
        }

        private sealed class CapturingSink : IPvpResultSink
        {
            public readonly List<PvpMatchParticipantRecord> Saved = new();

            public void LoadRatings(Action<List<PvpRatingRecord>> callback) => callback(new List<PvpRatingRecord>());

            public void SaveMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, Action<PvpMatchSaveResult, uint> callback)
            {
                Saved.AddRange(participants);
                callback(PvpMatchSaveResult.Saved, 1);
            }

            public void LoadTemplates(Action<List<PvpTemplateRecord>, PvpTemplateStoreStatus> callback) => callback(new List<PvpTemplateRecord>
            {
                new PvpTemplateRecord
                {
                    TemplateKey = TemplateKey,
                    DisplayName = "Duelist",
                    Version = 1,
                    Enabled = true,
                    Modes = "1v1,2v2,ffa,bg_koth",
                    DefinitionJson = PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = TemplateKey, Version = 1, DisplayName = "Duelist" }),
                    SnapshotAt = T0,
                    SnapshotBy = "test",
                },
            }, PvpTemplateStoreStatus.Ok);

            public void SaveParticipantTemplates(uint dbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps, Action<PvpTemplateStoreStatus> callback) => callback(PvpTemplateStoreStatus.Ok);

            public void LoadLatestParticipantTemplates(Action<List<PvpLatestParticipantTemplateRecord>, PvpTemplateStoreStatus> callback) =>
                callback(new List<PvpLatestParticipantTemplateRecord>(), PvpTemplateStoreStatus.Ok);
        }

        private sealed class NoCrier : IPvpArenaCrierAnnouncer
        {
            public void Announce(IReadOnlyList<string> lines, string senderName) { }
        }

        private sealed class Rig
        {
            public DateTime Now = T0;
            public readonly FakeGateway Gateway = new();
            public readonly FakeSpaces Spaces = new();
            public readonly CapturingSink Sink = new();
            public readonly ConcurrentQueue<PvpIntent> Intents = new();
            public bool ShuttingDown;
            public PvpArenaDials Dials = PvpTunables.Defaults with { CrierEnabled = false, OvertimeEnabled = false, FfaTargetPlayers = 5, FfaMinPlayers = 5 };
            public BattlegroundDials Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, RejoinGraceSeconds = Grace };

            /// <summary>Idle and the floor off unless a test turns them on, so a held seat's long absence is the only clock in play.</summary>
            public PvpConductDials Conduct = PvpConductTunables.Defaults with { FloorEnabled = false, IdleEnabled = false };
            public PvpMatchCoordinator Coordinator;

            public Rig Build(params (uint Id, string Name)[] players)
            {
                foreach (var (id, name) in players)
                    Gateway.Add(id, name);

                Coordinator = new PvpMatchCoordinator(Gateway, Spaces, Sink, () => Now, () => Dials, Intents, new NoCrier(), () => Bg, conductDialSource: () => Conduct);
                Coordinator.ServerShuttingDown = () => ShuttingDown;
                Coordinator.BeginLoadRatings();
                Coordinator.BeginLoadTemplates();
                Coordinator.BeginLoadLatestTemplates();
                Coordinator.Tick();
                return this;
            }

            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

            public void Tick() => Coordinator.Tick();

            public PvpMatch MatchOf(uint id) => Coordinator.FindMatch(id);

            public PvpMatch Countdown(string room, params uint[] ids)
            {
                foreach (var id in ids)
                    Assert.IsTrue(Coordinator.Join(id, room, false).Joined, $"fixture: join 0x{id:X8} to {room}");

                Tick();
                Assert.AreEqual(PvpMatchState.AwaitingAccept, MatchOf(ids[0])?.State, "fixture: the match formed");

                foreach (var id in ids)
                    Assert.AreEqual(PvpAnswerOutcome.Accepted, Coordinator.Accept(id).Outcome, $"fixture: accept 0x{id:X8}");

                Tick(); // AwaitingAccept -> Staging, allocate
                Tick(); // ready -> dispatch
                Tick(); // arrived -> Countdown
                Assert.AreEqual(PvpMatchState.Countdown, MatchOf(ids[0]).State, "fixture: Countdown");
                return MatchOf(ids[0]);
            }

            public PvpMatch Live(string room, params uint[] ids)
            {
                var m = Countdown(room, ids);
                Advance(Dials.CountdownSeconds);
                Tick();
                Assert.AreEqual(PvpMatchState.Live, m.State, "fixture: Live");
                return m;
            }

            /// <summary>Ticks <paramref name="seconds"/> one second at a time, stamping activity for <paramref name="active"/>.</summary>
            public void Run(PvpMatch match, int seconds, params uint[] active)
            {
                for (var i = 0; i < seconds; i++)
                {
                    Advance(1);

                    foreach (var id in active)
                        Gateway.Players[id].Activity = new PvpActivityStamp(match.MatchId, Now);

                    Tick();
                }
            }

            /// <summary>The player drops: offline, and the logout hook's forfeit intent arrives.</summary>
            public void Logout(PvpMatch match, uint id, bool serverCaused = false)
            {
                Gateway.Players[id].Online = false;
                Intents.Enqueue(PvpMatchManager.LogoutForfeit(id, match.MatchId, Now, serverCaused));
                Tick();
            }

            /// <summary>The player logs back in (a new Player, standing outside the match) and the login path reports it.</summary>
            public void Reconnect(PvpMatch match, uint id)
            {
                var p = Gateway.Players[id];
                p.Online = true;
                p.Instance = 0;
                Intents.Enqueue(PvpMatchManager.Reconnected(id, match.MatchId, Now));
                Tick();
            }
        }

        private static readonly (uint, string)[] Four = { (A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta") };

        private static PvpParticipant Participant(PvpMatch match, uint id) => match.Teams.SelectMany(t => t.Members).Single(p => p.CharacterId == id);

        private static int TeamOf(PvpMatch match, uint id) => match.Teams.Single(t => t.Members.Any(p => p.CharacterId == id)).TeamIndex;

        private static uint[] Teammates(PvpMatch match, uint id) =>
            match.Teams.Single(t => t.Members.Any(p => p.CharacterId == id)).Members.Select(p => p.CharacterId).Where(x => x != id).ToArray();

        private static uint[] Opponents(PvpMatch match, uint id) =>
            match.Teams.Where(t => t.Members.All(p => p.CharacterId != id)).SelectMany(t => t.Members).Select(p => p.CharacterId).ToArray();

        /// <summary>Everyone well off the hill, so no king-of-the-hill score ends the match while a test waits on a clock.</summary>
        private static void OffTheHill(Rig rig, PvpMatch match)
        {
            var zone = BattlegroundMapCatalog.Bg016c;

            foreach (var id in match.Teams.SelectMany(t => t.Members).Select(p => p.CharacterId))
                rig.Gateway.Players[id].At = (zone.ZoneX + 150, zone.ZoneY + 150, zone.ZoneZ);
        }

        private static Rig BgLive(out PvpMatch match, Action<Rig> configure = null)
        {
            var rig = new Rig();
            configure?.Invoke(rig);
            rig.Build(Four);
            match = rig.Live("bg", A, B, C, D);
            OffTheHill(rig, match);
            return rig;
        }

        // ================= the grace begins =================

        /// <summary>
        /// A Live battleground logout (the logout hook's intent) holds the seat: no exit, no strike, nothing stored, the grace ends
        /// pvp_bg_rejoin_grace_seconds from now, the login path can see the held match, the others are told, and participation has not
        /// ended (a Thread the player was pulled from stays held for them).
        /// </summary>
        [TestMethod]
        public void BattlegroundLogout_HoldsTheSeat_InsteadOfForfeiting()
        {
            var rig = BgLive(out var match);

            rig.Logout(match, C);

            Assert.IsNull(Participant(match, C).ExitReason, "the seat is held, not forfeited");
            Assert.AreEqual(rig.Now.AddSeconds(Grace), rig.Coordinator.GraceUntilForTest(C));
            Assert.AreEqual(match.MatchId, rig.Coordinator.GracedMatchOf(C));
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(C), "no strike while held");
            Assert.AreEqual(0, rig.Gateway.Persisted.Count);
            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, BattlegroundText.SeatHeld("Charlie", Grace)), "the others are told the seat is held");
            CollectionAssert.DoesNotContain(rig.Gateway.ParticipationEnded, C, "participation has not ended while held");
        }

        /// <summary>The walkout poll is the second caller of the one grace entry: an offline seat with no intent at all is held the same way.</summary>
        [TestMethod]
        public void BattlegroundOfflineWithoutAnIntent_TheWalkoutPollHoldsTheSeat()
        {
            var rig = BgLive(out var match);

            rig.Gateway.Players[C].Online = false;
            rig.Tick();

            Assert.IsNull(Participant(match, C).ExitReason);
            Assert.AreEqual(rig.Now.AddSeconds(Grace), rig.Coordinator.GraceUntilForTest(C));

            // The logout intent arriving after the poll took the grace changes nothing: one grace, still running from the poll.
            var until = rig.Coordinator.GraceUntilForTest(C);
            rig.Advance(5);
            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(C, match.MatchId, rig.Now));
            rig.Tick();

            Assert.IsNull(Participant(match, C).ExitReason, "a held seat's late logout intent is not a forfeit");
            Assert.AreEqual(until, rig.Coordinator.GraceUntilForTest(C));
        }

        /// <summary>The poll held the seat, then the intent says the server or staff forced the logout: forfeited at once, no strike.</summary>
        [TestMethod]
        public void PollHeldTheSeat_ThenAServerCausedIntent_ForfeitsWithoutAStrike()
        {
            var rig = BgLive(out var match);

            rig.Gateway.Players[C].Online = false;
            rig.Tick();
            Assert.IsNotNull(rig.Coordinator.GraceUntilForTest(C), "fixture: held by the poll");

            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(C, match.MatchId, rig.Now, serverCaused: true));
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C));
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(C));
        }

        /// <summary>pvp_bg_rejoin_grace_seconds 0 is today's behaviour: the logout forfeits at once, with its strike.</summary>
        [TestMethod]
        public void GraceZero_BattlegroundLogout_ForfeitsAtOnce()
        {
            var rig = BgLive(out var match, r => r.Bg = r.Bg with { RejoinGraceSeconds = 0 });

            rig.Logout(match, C);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C));
            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(C)?.Strikes);
        }

        // ================= no grace outside a Live battleground =================

        /// <summary>Tugak Brawl is an arena mode: its logout forfeits at once with a strike, grace or no grace.</summary>
        [TestMethod]
        public void TugakLogout_StillForfeitsAtOnce()
        {
            var rig = new Rig().Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"), (E, "Echo"));
            var match = rig.Live(ArenaMapCatalog.FfaKey, A, B, C, D, E);

            rig.Logout(match, C);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C));
            Assert.IsNull(rig.Coordinator.GracedMatchOf(C));
            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(C)?.Strikes);
        }

        /// <summary>A 1v1 logout forfeits at once (the arena's own rules: no deserter strike) and the match resolves for the opponent.</summary>
        [TestMethod]
        public void OneVOneLogout_StillForfeitsAtOnce()
        {
            var rig = new Rig().Build((A, "Alpha"), (B, "Bravo"));
            var match = rig.Live(ArenaMapCatalog.OneVOneKey, A, B);

            rig.Logout(match, B);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, B).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(B));
            Assert.AreNotEqual(PvpMatchState.Live, match.State, "the 1v1 is decided by the forfeit");
        }

        /// <summary>Before Live a drop is still a dodge: the Countdown logout forfeits at once and nothing is held.</summary>
        [TestMethod]
        public void CountdownLogout_IsNotHeld()
        {
            var rig = new Rig().Build(Four);
            var match = rig.Countdown("bg", A, B, C, D);

            rig.Logout(match, C);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C));
        }

        /// <summary>A logout the server or staff forced forfeits at once (no grace, no strike).</summary>
        [TestMethod]
        public void ServerCausedLogout_ForfeitsAtOnce()
        {
            var rig = BgLive(out var match);

            rig.Logout(match, C, serverCaused: true);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C));
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(C));
        }

        /// <summary>During a server shutdown every logout forfeits at once: nobody is coming back to a held seat.</summary>
        [TestMethod]
        public void ShutdownLogout_ForfeitsAtOnce()
        {
            var rig = BgLive(out var match);
            rig.ShuttingDown = true;

            rig.Logout(match, C);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C));
        }

        // ================= expiry =================

        /// <summary>
        /// The grace running out is today's logout forfeit, deserter strike and stored record included, and the player's participation
        /// ends then (not when the grace began).
        /// </summary>
        [TestMethod]
        public void GraceExpiry_ForfeitsWithAStrike()
        {
            var rig = BgLive(out var match);
            rig.Logout(match, C);

            rig.Advance(Grace - 1);
            rig.Tick();
            Assert.IsNull(Participant(match, C).ExitReason, "still held a second before the end");

            rig.Advance(1);
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C));
            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(C)?.Strikes);
            Assert.AreEqual(C, rig.Gateway.Persisted.Single().Id);
            Assert.AreEqual(1, rig.Gateway.ParticipationEnded.Count(id => id == C));
        }

        /// <summary>A whole team held does not end the match: their team is not eliminated until the graces run out.</summary>
        [TestMethod]
        public void WholeTeamHeld_TheMatchWaitsForTheGraces()
        {
            var rig = BgLive(out var match);
            var team = new[] { C }.Concat(Teammates(match, C)).ToArray();

            foreach (var id in team)
                rig.Logout(match, id);

            rig.Advance(Grace - 1);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State, "a held team is still in the match");

            rig.Advance(1);
            rig.Tick();

            foreach (var id in team)
                Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, id).ExitReason, $"0x{id:X8}");

            Assert.AreNotEqual(PvpMatchState.Live, match.State, "the expired graces eliminate the team and the match is decided");
        }

        // ================= rejoin =================

        /// <summary>
        /// A reconnect within the window: the new Player is admitted to the match realm, entered with a LIVE binding on their own team,
        /// at a spawn in the match instance, with the seat's original exit; arriving completes the rejoin and everyone is told.
        /// </summary>
        [TestMethod]
        public void Reconnect_ReplacesWithALiveBinding_OnTheirOwnTeam()
        {
            var rig = BgLive(out var match);
            var instance = rig.Gateway.Players[C].Instance;
            var dispatchExit = rig.Gateway.Entries.Single(e => e.Id == C).ExitTo;

            rig.Logout(match, C);
            rig.Advance(30);
            rig.Reconnect(match, C);

            CollectionAssert.Contains(rig.Spaces.Admitted, C, "the new Player object is admitted to the realm");

            var rejoin = rig.Gateway.Entries.Last(e => e.Id == C);
            Assert.AreEqual(2, rig.Gateway.Entries.Count(e => e.Id == C), "one dispatch, one rejoin");
            Assert.AreEqual(PvpMatchState.Live, rejoin.Binding.State, "a rejoin binds Live, not Staging");
            Assert.AreEqual(TeamOf(match, C), rejoin.Binding.TeamIndex, "their own team");
            Assert.AreEqual(instance, rejoin.Spawn.Instance, "a spawn inside the match instance");
            Assert.AreEqual(dispatchExit.LandblockId.Raw, rejoin.ExitTo.LandblockId.Raw, "the seat keeps its original exit");

            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C), "arrived: no longer held");
            Assert.IsFalse(rig.Coordinator.IsRejoiningForTest(C));
            Assert.IsNull(Participant(match, C).ExitReason);
            Assert.IsTrue(rig.Gateway.Got(C, BattlegroundText.RejoinedYou));
            Assert.IsTrue(rig.Gateway.Got(A, BattlegroundText.Rejoined("Charlie")));
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(C));
        }

        /// <summary>One grace per seat per match: after a rejoin, a second logout forfeits at once.</summary>
        [TestMethod]
        public void SecondLogoutAfterARejoin_ForfeitsAtOnce()
        {
            var rig = BgLive(out var match);
            rig.Logout(match, C);
            rig.Reconnect(match, C);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C), "fixture: rejoined");

            rig.Logout(match, C);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(C)?.Strikes);
        }

        /// <summary>
        /// Arrival restarts the idle clock: a player away longer than the idle limit is not removed for idling the moment they are back.
        /// Idle enforcement is ON here (the shipped idle dials); the three who stayed are active throughout.
        /// </summary>
        [TestMethod]
        public void Rejoin_ResetsTheIdleClock()
        {
            var rig = BgLive(out var match, r => r.Conduct = PvpConductTunables.Defaults with { FloorEnabled = false });
            var stayed = new[] { A, B, C, D }.Where(id => id != C).ToArray();

            rig.Logout(match, C);
            rig.Run(match, 100, stayed);
            Assert.IsNull(Participant(match, C).ExitReason, "fixture: a held seat is not judged idle");

            rig.Reconnect(match, C);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C), "fixture: rejoined");

            rig.Run(match, 10, stayed);

            Assert.IsNull(Participant(match, C).ExitReason, "the idle clock restarted at the rejoin");
        }

        /// <summary>A space that can no longer admit the player gives the held seat up at once, without a strike (not the player's fault).</summary>
        [TestMethod]
        public void Reconnect_SpaceRefuses_ForfeitsWithoutAStrike()
        {
            var rig = BgLive(out var match);
            rig.Spaces.Admits = false;
            rig.Logout(match, C);

            rig.Reconnect(match, C);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(C));
            Assert.IsTrue(rig.Gateway.Got(C, BattlegroundText.RejoinFailedYou));
        }

        /// <summary>
        /// The rejoin's template entry failing gives the held seat up: a player-caused failure (a pack too full, busy past the window)
        /// strikes; any other does not.
        /// </summary>
        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void RejoinEntryFailure_Forfeits(bool playerCaused)
        {
            var rig = BgLive(out var match);
            rig.Logout(match, C);
            rig.Gateway.HoldEntry.Add(C);
            rig.Reconnect(match, C);
            Assert.IsTrue(rig.Coordinator.IsRejoiningForTest(C), "fixture: on the way in");

            rig.Intents.Enqueue(PvpMatchManager.EntryFailed(C, match.MatchId, rig.Now, playerCaused: playerCaused));
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.AreEqual(playerCaused, rig.Coordinator.DeserterRecordForTest(C) != null, "a strike only when the player caused it");
            Assert.IsTrue(rig.Gateway.Got(C, BattlegroundText.RejoinFailedYou));
        }

        /// <summary>A rejoin that never lands is given up after RejoinArrivalTimeoutSeconds; online (not their fault), no strike.</summary>
        [TestMethod]
        public void RejoinThatNeverArrives_IsGivenUp()
        {
            var rig = BgLive(out var match);
            rig.Logout(match, C);
            rig.Gateway.HoldEntry.Add(C);
            rig.Reconnect(match, C);

            rig.Advance(PvpMatchCoordinator.RejoinArrivalTimeoutSeconds - 1);
            rig.Tick();
            Assert.IsNull(Participant(match, C).ExitReason, "still on the way in");

            rig.Advance(1);
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(C));
        }

        // ================= the match ends while a seat is held =================

        /// <summary>
        /// The match is decided while C is held: C is scored as PRESENT, not as a forfeiter - no forfeit reason, their team's result, and
        /// the same rating change as a teammate who stayed (the forfeiter's S = 0 is not applied).
        /// </summary>
        [TestMethod]
        public void ResolveWhileHeld_ScoresTheSeatAsPresent()
        {
            var rig = BgLive(out var match);
            var mate = Teammates(match, C).Single();

            rig.Logout(match, C);

            foreach (var id in Opponents(match, C))
                rig.Coordinator.Forfeit(id);

            rig.Tick();

            Assert.AreNotEqual(PvpMatchState.Live, match.State, "fixture: the other team's forfeits decide the match");
            Assert.IsNull(Participant(match, C).ExitReason, "the held seat is not a forfeit");

            var held = rig.Sink.Saved.Single(p => p.CharacterId == C);
            var stayed = rig.Sink.Saved.Single(p => p.CharacterId == mate);

            Assert.IsNull(held.ForfeitReason);
            Assert.AreEqual(stayed.Result, held.Result, "their team's result");
            Assert.IsNotNull(held.RatingBefore, "fixture: the match is rated");
            Assert.AreEqual(stayed.RatingAfter - stayed.RatingBefore, held.RatingAfter - held.RatingBefore, "the same rating change as the teammate: S is not forced to 0");
            Assert.IsTrue(held.RatingAfter > held.RatingBefore, "a win");
        }

        // ================= dispatch: the exit comes from the gateway =================

        /// <summary>
        /// The seat's exit is the gateway's GetExitPoint (the live one stamps a Thread's outer exit, never the copy the player stood in),
        /// not their current position.
        /// </summary>
        [TestMethod]
        public void Dispatch_StampsTheGatewaysExitPoint()
        {
            var outer = new Position(0x7D640013, 10f, 20f, 0f, 0f, 0f, 0f, 1f, 0);
            var rig = new Rig();
            rig.Gateway.ExitPoint = id => new Position(outer);
            rig.Build(Four);

            rig.Live("bg", A, B, C, D);

            foreach (var id in new[] { A, B, C, D })
            {
                var exit = rig.Gateway.Entries.First(e => e.Id == id).ExitTo;

                Assert.AreEqual(outer.LandblockId.Raw, exit.LandblockId.Raw, $"0x{id:X8}");
                Assert.IsFalse(exit.IsEphemeralRealm, "never an ephemeral exit");
            }
        }

        // ================= a Thread away entry never outlives the match that made it =================

        private static ACE.Server.ThreadDungeons.ThreadDungeonRun NewRun()
        {
            var spec = new ACE.Server.ThreadDungeons.DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new ACE.Server.ThreadDungeons.Defs.DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ACE.Server.ThreadDungeons.ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        private static void PulledFromAThread(uint id) =>
            ACE.Server.ThreadDungeons.ThreadPvpAway.BeginPull(id, NewRun(), new Position(0x01500100, 1f, 1f, 0f, 0f, 0f, 0f, 1f, 0x80001234u), DateTime.UtcNow, TimeSpan.FromMinutes(30));

        /// <summary>A walk-out (ForfeitLeft, no return resolver) releases the entry, so no later match can consume it.</summary>
        [TestMethod]
        public void WalkOut_ReleasesTheThreadAwayEntry()
        {
            var rig = BgLive(out var match);
            PulledFromAThread(C);

            rig.Gateway.Players[C].Instance = 0;
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLeft, Participant(match, C).ExitReason, "fixture: a walk-out");
            Assert.IsFalse(ACE.Server.ThreadDungeons.ThreadPvpAway.TryGet(C, out _), "released at the exit");
        }

        /// <summary>A rejoin the space refuses, with the player online at the Thread's outer exit: the entry is released.</summary>
        [TestMethod]
        public void FailedRejoin_ReleasesTheThreadAwayEntry()
        {
            var rig = BgLive(out var match);
            PulledFromAThread(C);
            rig.Spaces.Admits = false;

            rig.Logout(match, C);
            Assert.IsTrue(ACE.Server.ThreadDungeons.ThreadPvpAway.TryGet(C, out _), "kept while the seat is held");

            rig.Reconnect(match, C);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason, "fixture: the rejoin failed");
            Assert.IsFalse(ACE.Server.ThreadDungeons.ThreadPvpAway.TryGet(C, out _));
        }

        /// <summary>A player still standing in the match when it ends keeps the entry for the return resolver; an offline expiry keeps it for the login.</summary>
        [TestMethod]
        public void ExitFromInsideTheMatch_OrOffline_KeepsTheEntryForTheResolver()
        {
            var rig = BgLive(out var match);
            PulledFromAThread(A);
            PulledFromAThread(C);

            rig.Logout(match, C);
            rig.Advance(Grace);
            rig.Tick();
            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason, "fixture: the grace ran out offline");
            Assert.IsTrue(ACE.Server.ThreadDungeons.ThreadPvpAway.TryGet(C, out _), "kept for the next login's relocation");

            foreach (var id in Opponents(match, A))
                rig.Coordinator.Forfeit(id);

            rig.Tick();
            rig.Advance(rig.Dials.PostMatchSeconds);
            rig.Tick();

            CollectionAssert.Contains(rig.Gateway.ParticipationEnded, A, "fixture: A's participation ended");
            Assert.IsTrue(ACE.Server.ThreadDungeons.ThreadPvpAway.TryGet(A, out _), "kept: the return resolver consumes it");
        }

        // ================= the grace window =================

        /// <summary>
        /// The logout intent arrives while the player still stands in the instance (the PK logout delay): the window runs from the moment
        /// they actually leave, not from the intent.
        /// </summary>
        [TestMethod]
        public void GraceWindow_RunsFromTheRealDisconnect()
        {
            var rig = BgLive(out var match);

            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(C, match.MatchId, rig.Now));
            rig.Tick();
            Assert.IsNotNull(rig.Coordinator.GraceUntilForTest(C), "fixture: held from the intent");

            rig.Run(match, 20);
            rig.Gateway.Players[C].Online = false;
            var gone = rig.Now;

            rig.Advance(Grace - 1);
            rig.Tick();
            Assert.IsNull(Participant(match, C).ExitReason, "still held: the window started when they left, 20 s after the intent");

            rig.Advance(1);
            rig.Tick();
            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.AreEqual(gone.AddSeconds(Grace), rig.Now);
        }

        /// <summary>The slide is bounded: a player who never leaves the instance does not hold the seat forever.</summary>
        [TestMethod]
        public void GraceWindow_StopsSlidingAfterTheLingerBound()
        {
            var rig = BgLive(out var match);
            var began = rig.Now;

            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(C, match.MatchId, rig.Now));
            rig.Tick();

            var end = began.AddSeconds(PvpMatchCoordinator.MaxLogoutLingerSeconds + Grace);

            while (rig.Now < end.AddSeconds(-1))
                rig.Run(match, 1);

            Assert.IsNull(Participant(match, C).ExitReason, "held until the bounded window ends");

            rig.Run(match, 1);
            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
        }

        /// <summary>A drop timestamped before the Live flip but drained after it is not a Live logout: no grace, a forfeit.</summary>
        [TestMethod]
        public void LogoutBeforeTheLiveFlip_DrainedAfterIt_IsNotHeld()
        {
            var rig = BgLive(out var match);
            var liveSince = match.LiveSinceUtc.Value;

            rig.Gateway.Players[C].Online = false;
            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(C, match.MatchId, liveSince.AddSeconds(-1)));
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C));
            Assert.IsNotNull(rig.Coordinator.DeserterRecordForTest(C), "a forfeit with its lockout");
        }

        // ================= Reconnected never forfeits =================

        /// <summary>
        /// A Reconnected intent carries a placeholder exit reason that is never read: for a seat that is not held, a match that is not
        /// Live, an arena match or an unknown match, it changes nothing.
        /// </summary>
        [TestMethod]
        public void ReconnectedIntent_NeverForfeitsAnyone()
        {
            var bg = BgLive(out var live);
            bg.Intents.Enqueue(PvpMatchManager.Reconnected(C, live.MatchId, bg.Now));
            bg.Intents.Enqueue(PvpMatchManager.Reconnected(C, Guid.NewGuid(), bg.Now));
            bg.Tick();
            Assert.IsTrue(live.Teams.SelectMany(t => t.Members).All(p => !p.ExitReason.HasValue), "Live seat not held");
            Assert.AreEqual(1, bg.Gateway.Entries.Count(e => e.Id == C), "no placement");

            var cd = new Rig().Build(Four);
            var countdown = cd.Countdown("bg", A, B, C, D);
            cd.Intents.Enqueue(PvpMatchManager.Reconnected(C, countdown.MatchId, cd.Now));
            cd.Tick();
            Assert.IsTrue(countdown.Teams.SelectMany(t => t.Members).All(p => !p.ExitReason.HasValue), "Countdown");

            var ffa = new Rig().Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"), (E, "Echo"));
            var tugak = ffa.Live(ArenaMapCatalog.FfaKey, A, B, C, D, E);
            ffa.Intents.Enqueue(PvpMatchManager.Reconnected(C, tugak.MatchId, ffa.Now));
            ffa.Tick();
            Assert.IsTrue(tugak.Teams.SelectMany(t => t.Members).All(p => !p.ExitReason.HasValue), "Tugak");
        }

        // ================= intents during a rejoin =================

        /// <summary>On the way back in: a duplicate Reconnected is ignored, and a logout is a second departure (forfeit, strike).</summary>
        [TestMethod]
        public void IntentsWhileRejoining()
        {
            var rig = BgLive(out var match);
            rig.Logout(match, C);
            rig.Gateway.HoldEntry.Add(C);
            rig.Reconnect(match, C);
            Assert.IsTrue(rig.Coordinator.IsRejoiningForTest(C), "fixture: on the way in");

            rig.Intents.Enqueue(PvpMatchManager.Reconnected(C, match.MatchId, rig.Now));
            rig.Tick();
            Assert.AreEqual(2, rig.Gateway.Entries.Count(e => e.Id == C), "one dispatch and one rejoin: the duplicate is ignored");
            Assert.IsNull(Participant(match, C).ExitReason);

            rig.Logout(match, C);

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason);
            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(C)?.Strikes);
        }

        /// <summary>Died, then logged out from the pen cycle, then rejoined: a fresh spawn, not a respawning seat, and no pen respawn later.</summary>
        [TestMethod]
        public void DeathWhileHeld_ThenRejoin_ResetsThePenState()
        {
            var rig = BgLive(out var match);

            rig.Intents.Enqueue(PvpMatchManager.Death(C, match.MatchId, rig.Now, A));
            rig.Tick();
            rig.Logout(match, C);
            Assert.IsNotNull(rig.Coordinator.GraceUntilForTest(C), "fixture: held after dying");

            rig.Reconnect(match, C);
            var rejoin = rig.Gateway.Entries.Last(e => e.Id == C);
            Assert.IsFalse(rejoin.Binding.Respawning, "the rejoin binding is not a respawning one");
            Assert.IsNull(rig.Coordinator.GraceUntilForTest(C), "fixture: rejoined");

            var respawnsBefore = rig.Gateway.Respawns.Count(id => id == C);
            rig.Run(match, (int)rig.Bg.RespawnSeconds + 10);

            Assert.AreEqual(respawnsBefore, rig.Gateway.Respawns.Count(id => id == C), "no stale pen respawn after the rejoin");
            Assert.IsNull(Participant(match, C).ExitReason);
        }
    }
}
