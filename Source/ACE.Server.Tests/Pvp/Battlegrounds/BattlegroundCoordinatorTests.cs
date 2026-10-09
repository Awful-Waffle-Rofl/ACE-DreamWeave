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

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// Step 4/5 of Docs/Pvp/BATTLEGROUNDS.md: the coordinator driving a King of the Hill match end to end through its
    /// seams. The fakes implement BOTH halves of each seam (IPvpPlayerGateway + IBattlegroundPlayerGateway,
    /// IPvpMatchSpaces + IBattlegroundMatchSpaces), which is what opens the bg room; PvpMatchCoordinatorTests' arena
    /// fakes implement only the arena halves and are untouched.
    /// </summary>
    [TestClass]
    public class BattlegroundCoordinatorTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private const uint A = 0x50000011, B = 0x50000012, C = 0x50000013, D = 0x50000014, E = 0x50000015, F = 0x50000016, G = 0x50000017, H = 0x50000018;

        private static readonly DateTime T0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// PvP Template Facets (owner ruling 2026-10-03: every battleground mode is templated by default): every fake
        /// player remembers this template, and the fake catalog offers it for King of the Hill.
        /// </summary>
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
            public bool Teleporting;
            public bool Dying;
            public Position Location;
            public (double X, double Y, double Z) At = (0, 0, 0);
        }

        private sealed class FakeGateway : IPvpPlayerGateway, IBattlegroundPlayerGateway
        {
            public readonly Dictionary<uint, FakePlayer> Players = new();
            public readonly List<(uint Id, string Text)> Messages = new();
            public readonly List<(uint Id, PvpPlayerBinding Binding, Position Exit, Position Spawn)> Entered = new();
            public readonly List<(uint Id, PvpPlayerBinding Binding)> Published = new();
            public readonly List<uint> Exited = new();
            public readonly List<uint> ExitedFromPen = new();
            public readonly List<uint> Returned = new();
            public readonly List<(uint Id, Guid Match, Position Spawn)> Respawns = new();
            public readonly List<(uint Id, int Health, bool Lethal)> Drains = new();
            public readonly List<(uint Id, int Amount, PvpBloodGrant Grant)> BloodGrants = new();

            public FakePlayer Add(uint id, string name, string ip = null, uint monarch = 0, params uint[] fellowship)
            {
                var p = new FakePlayer
                {
                    Facts = new PvpPlayerFacts(id, name, 100, ip ?? $"10.0.0.{id & 0xFF}", false, false, false, false, false, false, false, true, fellowship, monarch, PreferredTemplateKey: DefaultTemplateKey),
                    Location = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0),
                };

                Players[id] = p;
                return p;
            }

            public PvpPlayerFacts GetFacts(uint id) => Players.TryGetValue(id, out var p) && p.Online ? p.Facts : null;

            public bool IsOnline(uint id) => Players.TryGetValue(id, out var p) && p.Online;

            public Position GetCurrentPosition(uint id) => IsOnline(id) ? new Position(Players[id].Location) : null;

            public PvpPresence GetPresence(uint id, uint matchInstance)
            {
                if (!IsOnline(id))
                    return PvpPresence.Offline;

                var p = Players[id];

                if (p.Teleporting)
                    return PvpPresence.InTransit;

                return p.Instance == matchInstance ? PvpPresence.InInstance : PvpPresence.Elsewhere;
            }

            public void EnterAndTeleport(uint id, PvpPlayerBinding binding, Position exitTo, Position spawn)
            {
                Entered.Add((id, binding, exitTo, spawn));
                Players[id].Instance = spawn.Instance;
            }

            public void PublishBinding(uint id, PvpPlayerBinding binding) => Published.Add((id, binding));

            public void ExitMatch(uint id, string context) => Exited.Add(id);

            public void ReturnToExit(uint id, Position exitTo, uint matchInstance)
            {
                Returned.Add(id);

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

            public void GrantArenaBlood(uint id, int amount, PvpBloodGrant grant) => BloodGrants.Add((id, amount, grant));

            // ---- battleground half ----

            public BattlegroundZoneSample SampleZone(uint id, uint matchInstance)
            {
                if (!IsOnline(id))
                    return null;

                var p = Players[id];
                return new BattlegroundZoneSample(id, 0, null, p.Instance == matchInstance, p.Dying, p.Teleporting, p.At.X, p.At.Y, p.At.Z);
            }

            public void DrainVitals(uint id, Guid matchId, uint matchInstance, int health, int stamina, int mana, bool lethal) => Drains.Add((id, health, lethal));

            /// <summary>Players whose queued respawn the player-side guard refuses (they stay where they are).</summary>
            public readonly HashSet<uint> RefuseRespawn = new();

            /// <summary>Records the queued respawn and, unless refused, plays the player's queue: the player lands at the spawn.</summary>
            public void Respawn(uint id, Guid matchId, Position spawn)
            {
                Respawns.Add((id, matchId, spawn));

                if (RefuseRespawn.Contains(id) || !Players.TryGetValue(id, out var p) || !p.Online || p.Dying)
                    return;

                p.Instance = spawn.Instance;
                p.At = (spawn.PositionX, spawn.PositionY, spawn.PositionZ);
            }

            public bool IsInDeathProcess(uint id) => Players.TryGetValue(id, out var p) && p.Online && p.Dying;

            public void ExitMatchFromPen(uint id, string context) => ExitedFromPen.Add(id);

            // ---- PvP Template Facets ----

            public readonly List<(uint Id, PvpTemplateDefinition Template)> RoomChecks = new();

            public readonly List<(uint Id, Guid Match)> Backstops = new();

            public string CheckTemplateRoom(uint id, PvpTemplateDefinition template)
            {
                RoomChecks.Add((id, template));
                return null;
            }

            public void RunTemplateBackstop(uint id, Guid matchId) => Backstops.Add((id, matchId));

            public bool Got(uint id, string text) => Messages.Any(m => m.Id == id && m.Text == text);

            public PvpPlayerBinding LastBinding(uint id) => Published.Where(p => p.Id == id).Select(p => p.Binding).LastOrDefault();
        }

        private sealed class FakeSpaces : IPvpMatchSpaces, IBattlegroundMatchSpaces
        {
            public readonly List<(ArenaMap Map, IReadOnlyList<uint> Ids)> Allocations = new();
            public readonly List<MatchSpace> Released = new();
            public readonly List<(MatchSpace Space, IReadOnlyList<BattlegroundSealPiece> Pieces, BattlegroundFixtureJob Job)> FixtureCalls = new();

            /// <summary>Placed: the job completes as it is queued. Pending: the test completes it. Failed: it fails at once.</summary>
            public BattlegroundFixtureStatus FixtureOutcome = BattlegroundFixtureStatus.Placed;

            public bool ReturnNullJob;

            private ushort next = 0x0200;

            public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<uint> ids)
            {
                Allocations.Add((map, ids.ToList()));
                var instance = Position.InstanceIDFromVars(map.RealmId, next++, isTemporaryRuleset: true);
                return MatchSpaceAllocation.Ok(new MatchSpace(map.MapKey, map.LandblockId, map.RealmId, instance, ids[0]));
            }

            public MatchSpaceReadiness GetReadiness(MatchSpace space) => MatchSpaceReadiness.Ready;

            public void Release(MatchSpace space) => Released.Add(space);

            public BattlegroundFixtureJob SpawnFixtures(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                if (ReturnNullJob)
                    return null;

                var job = new BattlegroundFixtureJob();

                if (FixtureOutcome == BattlegroundFixtureStatus.Placed)
                    job.Complete(pieces.Count);
                else if (FixtureOutcome == BattlegroundFixtureStatus.Failed)
                    job.Fail("scripted failure");

                FixtureCalls.Add((space, pieces, job));
                return job;
            }

            // ---- zone markers: recorded apart from FixtureCalls, which stays seals-only ----

            public readonly List<(MatchSpace Space, IReadOnlyList<BattlegroundSealPiece> Pieces, BattlegroundFixtureJob Job, int EnteredAtCall, int SealCallsAtCall)> MarkerCalls = new();

            /// <summary>Placed: completes as queued. Pending: never completes unless the test does. Failed: fails at once.</summary>
            public BattlegroundFixtureStatus MarkerOutcome = BattlegroundFixtureStatus.Placed;

            public bool MarkerReturnNull;

            public bool MarkerThrows;

            /// <summary>Set by the rig: lets a marker call record how many players had been dispatched when it was made.</summary>
            public FakeGateway Gateway;

            /// <summary>Every RemoveZoneMarkers call: the job whose markers were to be removed, and how many marker placements had been queued by then.</summary>
            public readonly List<(BattlegroundFixtureJob Placed, int MarkerCallsAtCall)> RemoveCalls = new();

            public bool RemoveThrows;

            public void RemoveZoneMarkers(MatchSpace space, BattlegroundFixtureJob placed)
            {
                RemoveCalls.Add((placed, MarkerCalls.Count));

                if (RemoveThrows)
                    throw new InvalidOperationException("scripted remove throw");
            }

            // ---- start gates: recorded apart from FixtureCalls (pen seals) and MarkerCalls ----

            /// <summary>Every SpawnStartGates call, with how many pen-seal calls had been made and how many players dispatched by then.</summary>
            public readonly List<(MatchSpace Space, IReadOnlyList<BattlegroundSealPiece> Pieces, BattlegroundFixtureJob Job, int SealCallsAtCall, int EnteredAtCall)> GateCalls = new();

            /// <summary>Every RemoveStartGates call: the job whose gates were to come down, and the match state the test recorded by then.</summary>
            public readonly List<(BattlegroundFixtureJob Placed, PvpMatchState? StateAtCall)> GateRemoveCalls = new();

            public BattlegroundFixtureStatus GateOutcome = BattlegroundFixtureStatus.Placed;

            public bool GateReturnNull;

            public bool GateRemoveThrows;

            /// <summary>Set by the test: lets a gate removal record the match state it happened in.</summary>
            public Func<PvpMatchState?> StateProbe;

            public BattlegroundFixtureJob SpawnStartGates(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                if (GateReturnNull)
                    return null;

                var job = new BattlegroundFixtureJob();

                if (GateOutcome == BattlegroundFixtureStatus.Placed)
                    job.Complete(pieces.Count);
                else if (GateOutcome == BattlegroundFixtureStatus.Failed)
                    job.Fail("scripted gate failure");

                GateCalls.Add((space, pieces, job, FixtureCalls.Count, Gateway?.Entered.Count ?? -1));
                return job;
            }

            public void RemoveStartGates(MatchSpace space, BattlegroundFixtureJob placed)
            {
                GateRemoveCalls.Add((placed, StateProbe?.Invoke()));

                if (GateRemoveThrows)
                    throw new InvalidOperationException("scripted gate remove throw");
            }

            public BattlegroundFixtureJob SpawnZoneMarkers(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                var job = new BattlegroundFixtureJob();
                MarkerCalls.Add((space, pieces, job, Gateway?.Entered.Count ?? -1, FixtureCalls.Count));

                if (MarkerThrows)
                    throw new InvalidOperationException("scripted marker throw");

                if (MarkerReturnNull)
                    return null;

                if (MarkerOutcome == BattlegroundFixtureStatus.Placed)
                    job.Complete(pieces.Count);
                else if (MarkerOutcome == BattlegroundFixtureStatus.Failed)
                    job.Fail("scripted marker failure");

                return job;
            }
        }

        private sealed class FakeSink : IPvpResultSink
        {
            public readonly List<(PvpMatchRecord Match, IReadOnlyList<PvpMatchParticipantRecord> Parts, IReadOnlyList<PvpRatingRecord> Ratings)> Saves = new();

            public void LoadRatings(Action<List<PvpRatingRecord>> callback) => callback(new List<PvpRatingRecord>());

            /// <summary>The pvp_template rows the read returns: one template offered in every arena mode AND King of the Hill.</summary>
            public List<PvpTemplateRecord> TemplateRows = new() { TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1,2v2,ffa,bg_koth") };

            public readonly List<(uint DbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> Stamps)> StampWrites = new();

            public void LoadTemplates(Action<List<PvpTemplateRecord>, PvpTemplateStoreStatus> callback) =>
                callback(TemplateRows?.Select(r => r.Clone()).ToList(), TemplateRows == null ? PvpTemplateStoreStatus.Failed : PvpTemplateStoreStatus.Ok);

            public void SaveParticipantTemplates(uint dbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps, Action<PvpTemplateStoreStatus> callback)
            {
                StampWrites.Add((dbMatchId, stamps.ToList()));
                callback(PvpTemplateStoreStatus.Ok);
            }

            public void LoadLatestParticipantTemplates(Action<List<PvpLatestParticipantTemplateRecord>, PvpTemplateStoreStatus> callback) =>
                callback(new List<PvpLatestParticipantTemplateRecord>(), PvpTemplateStoreStatus.Ok);

            public void SaveMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, Action<PvpMatchSaveResult, uint> callback)
            {
                Saves.Add((match, participants.ToList(), ratingUpserts.ToList()));
                callback(PvpMatchSaveResult.Saved, 1);
            }
        }

        private sealed class NoCrier : IPvpArenaCrierAnnouncer
        {
            public void Announce(IReadOnlyList<string> lines, string senderName) { }
        }

        private sealed class RecordingCrier : IPvpArenaCrierAnnouncer
        {
            public readonly List<string> Lines = new();

            public void Announce(IReadOnlyList<string> lines, string senderName) => Lines.AddRange(lines);
        }

        private sealed class Rig
        {
            public IPvpArenaCrierAnnouncer Crier = new NoCrier();
            public DateTime Now = T0;
            public readonly FakeGateway Gateway = new();
            public readonly FakeSpaces Spaces = new();
            public readonly FakeSink Sink = new();
            public readonly ConcurrentQueue<PvpIntent> Intents = new();
            public PvpArenaDials Dials = PvpTunables.Defaults with { BloodEnabled = true, CrierEnabled = false };
            public BattlegroundDials Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0 };
            public PvpMatchCoordinator Coordinator;

            /// <summary>When set, the coordinator finds battleground layouts here instead of the shipped catalog.</summary>
            public Func<string, BattlegroundLayout> Layouts;

            public Rig Build()
            {
                Coordinator = new PvpMatchCoordinator(Gateway, Spaces, Sink, () => Now, () => Dials, Intents, Crier, () => Bg);
                Spaces.Gateway = Gateway;

                if (Layouts != null)
                    Coordinator.LayoutResolver = Layouts;

                Coordinator.BeginLoadRatings();
                Coordinator.BeginLoadTemplates();
                Coordinator.BeginLoadLatestTemplates();
                Coordinator.Tick();
                return this;
            }

            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

            public void Tick() => Coordinator.Tick();

            public PvpMatch MatchOf(uint id) => Coordinator.FindMatch(id);

            public void JoinAll(params uint[] ids)
            {
                foreach (var id in ids)
                    Assert.IsTrue(Coordinator.Join(id, "bg", false).Joined, $"join 0x{id:X8}");
            }

            public void AcceptAll(params uint[] ids)
            {
                foreach (var id in ids)
                    Assert.AreEqual(PvpAnswerOutcome.Accepted, Coordinator.Accept(id).Outcome, $"accept 0x{id:X8}");
            }

            public PvpMatch DriveAcceptedToLive(uint anyId)
            {
                Tick(); // AwaitingAccept -> Staging, allocate
                Tick(); // ready -> seals placed -> dispatch
                Tick(); // arrived -> Countdown
                Assert.AreEqual(PvpMatchState.Countdown, MatchOf(anyId).State);
                Advance(Dials.CountdownSeconds);
                Tick(); // -> Live
                Assert.AreEqual(PvpMatchState.Live, MatchOf(anyId).State);
                return MatchOf(anyId);
            }

            public void Death(uint victim, Guid match, uint killer = 0) => Intents.Enqueue(PvpMatchManager.Death(victim, match, Now, killer));

            public int TeamOf(uint id) => MatchOf(id).Teams.Single(t => t.Members.Any(p => p.CharacterId == id)).TeamIndex;

            public uint[] Team(PvpMatch m, int team) => m.Teams[team].Members.Select(p => p.CharacterId).ToArray();
        }

        /// <summary>Four solos, formed, accepted and driven to Live.</summary>
        private static Rig LiveKoth(out PvpMatch match, Action<Rig> configure = null, IReadOnlyDictionary<uint, string> prefer = null, bool groupAB = false, string sharedIp = null)
        {
            var rig = new Rig();
            configure?.Invoke(rig);
            rig.Build();

            foreach (var (id, name) in new[] { (A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta") })
            {
                var p = groupAB && (id == A || id == B) ? rig.Gateway.Add(id, name, sharedIp, fellowship: new[] { A, B }) : rig.Gateway.Add(id, name, sharedIp);

                if (prefer != null && prefer.TryGetValue(id, out var key))
                    p.Facts = p.Facts with { PreferredTemplateKey = key };
            }

            if (groupAB)
            {
                Assert.IsTrue(rig.Coordinator.Join(A, "bg", false, group: true).Joined, "group join A + B");
                rig.JoinAll(C, D);
            }
            else
                rig.JoinAll(A, B, C, D);

            rig.Tick();

            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(A).State, "the four solos formed a match");
            Assert.AreEqual(BattlegroundModes.KothModeKey, rig.MatchOf(A).ModeKey);

            rig.AcceptAll(A, B, C, D);
            match = rig.DriveAcceptedToLive(A);
            return rig;
        }

        /// <summary>A Live death: the death sequence starts (the probe reports dying), the intent is reported, one tick drains it.</summary>
        private static void Kill(Rig rig, uint victim, PvpMatch match, uint killer = 0)
        {
            rig.Gateway.Players[victim].Dying = true;
            rig.Death(victim, match.MatchId, killer);
            rig.Tick();
        }

        /// <summary>DyingToPen to InPen: the death sequence ends with the player standing in the instance (the pen).</summary>
        private static void ArriveInPen(Rig rig, uint id)
        {
            rig.Gateway.Players[id].Dying = false;
            rig.Tick();
        }

        // ================= step 6: Crier line and clearqueue =================

        /// <summary>
        /// The bg room is in the Crier's snapshot with needed = pvp_bg_min_players - queued: two of four queued
        /// announces "2 more needed" with the bg join arg, and a queue at the minimum (needed 0) is not announced.
        /// </summary>
        [TestMethod]
        public void Crier_AnnouncesTheBgQueue_NeededIsMinPlayersMinusQueued()
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Crier = crier };
            rig.Dials = rig.Dials with { CrierEnabled = true, CrierAnnounceOnJoin = false, CrierIntervalSeconds = 60 };
            rig.Bg = rig.Bg with { FillWindowSeconds = 3600 };
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.JoinAll(A, B);

            rig.Tick();
            rig.Advance(61);
            rig.Tick();

            Assert.IsTrue(crier.Lines.Contains("Battleground queue: 2 queued, 2 more needed. Type /arena join bg to play."), string.Join(" | ", crier.Lines));

            crier.Lines.Clear();
            rig.Gateway.Add(C, "Charlie");
            rig.Gateway.Add(D, "Delta");
            rig.JoinAll(C, D);
            rig.Advance(61);
            rig.Tick();

            Assert.IsFalse(crier.Lines.Any(l => l.StartsWith("Battleground queue:")), "a queue at the minimum needs no one, so the status line is not announced (the fill-window line is a separate announcement): " + string.Join(" | ", crier.Lines));
            Assert.IsFalse(crier.Lines.Any(l => l.StartsWith("Battleground: 1 more")), "and no last call either: " + string.Join(" | ", crier.Lines));
        }

        /// <summary>
        /// The coordinator feeds the Crier the bg fill window from BattlegroundMatchmaker.FillWindowCloseUtc: when the
        /// queue reaches pvp_bg_min_players inside its window, the filling line goes out (seconds rounded up from the
        /// helper's close time, room = max players - queued), both in per-join mode and in interval mode.
        /// </summary>
        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void Crier_AnnouncesTheBgFillWindow_WhenTheQueueReachesMinPlayers(bool announceOnJoin)
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Crier = crier };
            rig.Dials = rig.Dials with { CrierEnabled = true, CrierAnnounceOnJoin = announceOnJoin, CrierIntervalSeconds = 600 };
            rig.Bg = rig.Bg with { FillWindowSeconds = 3600 };
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Gateway.Add(C, "Charlie");
            rig.Gateway.Add(D, "Delta");
            rig.JoinAll(A, B);
            rig.Tick();
            crier.Lines.Clear();

            rig.JoinAll(C, D);
            rig.Tick();

            var expectedRoom = rig.Bg.MaxPlayers - 4;
            var filling = crier.Lines.Where(l => l.StartsWith("Battleground starting in about ")).ToList();

            Assert.AreEqual(1, filling.Count, string.Join(" | ", crier.Lines));
            StringAssert.Contains(filling[0], " seconds with 4 players - room for " + expectedRoom + " more!");
        }
        /// <summary>
        /// Seconds in the filling line round UP: a 3600 s window observed half a second after the queue reached
        /// MinPlayers has 3599.5 s left and announces 3600, never 3599.
        /// </summary>
        [TestMethod]
        public void Crier_BgFillSeconds_RoundUp()
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Crier = crier };
            rig.Dials = rig.Dials with { CrierEnabled = true, CrierAnnounceOnJoin = true };
            rig.Bg = rig.Bg with { FillWindowSeconds = 3600 };
            rig.Build();
            foreach (var (id, name) in new[] { (A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta") })
                rig.Gateway.Add(id, name);
            rig.JoinAll(A, B, C, D);
            rig.Advance(0.5);
            rig.Tick();

            Assert.IsTrue(crier.Lines.Any(l => l.StartsWith("Battleground starting in about 3600 seconds ")), string.Join(" | ", crier.Lines));
        }

        /// <summary>
        /// A room the matchmaker is holding back (here the bg concurrent cap, 0) is not about to start, so the Crier must
        /// not promise it; the control (cap 1) shows the same queue does announce when it is not held.
        /// </summary>
        [DataTestMethod]
        [DataRow(0, false)]
        [DataRow(1, true)]
        public void Crier_BgFillLine_NotAnnounced_WhileTheBgConcurrentCapHoldsTheRoom(int bgCap, bool expectLine)
        {
            var crier = new RecordingCrier();
            var rig = new Rig { Crier = crier };
            rig.Dials = rig.Dials with { CrierEnabled = true, CrierAnnounceOnJoin = true };
            rig.Bg = rig.Bg with { FillWindowSeconds = 3600, MaxConcurrentMatches = bgCap };
            rig.Build();
            foreach (var (id, name) in new[] { (A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"), (E, "Echo"), (F, "Foxtrot") })
                rig.Gateway.Add(id, name);
            rig.JoinAll(A, B, C, D, E, F);
            rig.Tick();

            Assert.AreEqual(expectLine, crier.Lines.Any(l => l.StartsWith("Battleground starting in about ")), string.Join(" | ", crier.Lines));
        }
        /// <summary>/arenaadmin clearqueue bg: the coordinator empties the bg room and reports how many players left it.</summary>
        [TestMethod]
        public void ClearQueue_Bg_EmptiesTheRoom()
        {
            var rig = new Rig();
            rig.Bg = rig.Bg with { FillWindowSeconds = 3600 };
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.JoinAll(A, B);
            Assert.AreEqual(2, rig.Coordinator.QueuedIds("bg").Count);

            Assert.AreEqual(2, rig.Coordinator.ClearQueue("bg"));
            Assert.AreEqual(0, rig.Coordinator.QueuedIds("bg").Count);
        }

        // ================= respawn =================

        /// <summary>
        /// Drives: a Live KOTH death with a killer from the other team. The seat is NOT eliminated (the match stays Live,
        /// nobody exits), its binding is republished Live with Respawning and the pen, the killer's team gets a kill,
        /// and once the death sequence lands it in the pen and the respawn time passes, the coordinator queues the
        /// respawn into the same instance and republishes Respawning false.
        /// </summary>
        [TestMethod]
        public void Death_RespawnsWithoutElimination()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            var killer = rig.Team(match, 1)[0];

            Kill(rig, victim, match, killer);

            Assert.AreEqual(PvpMatchState.Live, match.State, "a battleground death never ends the match");
            Assert.IsFalse(match.Teams.SelectMany(t => t.Members).Any(p => p.ExitReason.HasValue), "nobody is out");
            Assert.AreEqual(0, rig.Gateway.Exited.Count + rig.Gateway.ExitedFromPen.Count + rig.Gateway.Returned.Count, "nobody exited or teleported");
            Assert.AreEqual(1, match.TeamKills[1], "the killer's team scored the kill");
            Assert.AreEqual(0, match.TeamKills[0]);

            var dying = rig.Gateway.LastBinding(victim);
            Assert.AreEqual(PvpMatchState.Live, dying.State);
            Assert.IsTrue(dying.Respawning, "republished as respawning");
            Assert.IsNotNull(dying.RespawnPen, "the pen rides on the binding");

            ArriveInPen(rig, victim);
            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Id == victim && m.Text.StartsWith("[Battleground] You will return")), "the pen countdown was sent");
            Assert.AreEqual(0, rig.Gateway.Respawns.Count, "not before the respawn time");

            rig.Advance(rig.Bg.RespawnSeconds);
            rig.Tick();

            Assert.AreEqual(1, rig.Gateway.Respawns.Count);
            Assert.AreEqual(victim, rig.Gateway.Respawns[0].Id);
            Assert.AreEqual(rig.Coordinator.SpaceOf(match.MatchId).Instance, rig.Gateway.Respawns[0].Spawn.Instance, "respawned inside the match instance");
            Assert.AreEqual(BattlegroundMapCatalog.Bg016c.SpawnsFor(0)[0].X, rig.Gateway.Respawns[0].Spawn.PositionX, "round-robin starts at the team's first spawn");
            Assert.IsTrue(rig.Gateway.LastBinding(victim).Respawning, "still respawning until the probe confirms the landing");

            rig.Tick();

            Assert.IsFalse(rig.Gateway.LastBinding(victim).Respawning, "confirmed at the spawn on the next probe, republished alive");
            Assert.AreEqual(1, rig.Gateway.Respawns.Count, "a landed respawn is never queued again");
            Assert.AreEqual(PvpMatchState.Live, match.State);
        }

        /// <summary>Two respawns on one team with no fixed spawn index go round-robin: the team's spawn 0, then spawn 1.</summary>
        [TestMethod]
        public void Respawns_RoundRobinThroughTheTeamSpawns()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            var spawns = BattlegroundMapCatalog.Bg016c.SpawnsFor(0);
            Assert.AreNotEqual(spawns[0].X, spawns[1].X, "fixture: the first two team spawns are distinguishable by X");

            DieAndQueueRespawn(rig, victim, match);
            rig.Tick(); // confirmed
            Assert.IsFalse(rig.Gateway.LastBinding(victim).Respawning);

            DieAndQueueRespawn(rig, victim, match);

            Assert.AreEqual(2, rig.Gateway.Respawns.Count);
            Assert.AreEqual(spawns[0].X, rig.Gateway.Respawns[0].Spawn.PositionX, "first respawn: spawn 0");
            Assert.AreEqual(spawns[1].X, rig.Gateway.Respawns[1].Spawn.PositionX, "second respawn: spawn 1");
        }

        /// <summary>
        /// The InPen gate: while the death probe still reports the death sequence running, the seat stays DyingToPen
        /// even standing in the instance - no arrival line and no respawn, however long the respawn time has passed.
        /// </summary>
        [TestMethod]
        public void StillDying_StaysDyingToPen_NoArrivalNoRespawn()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];

            Kill(rig, victim, match); // Dying stays true from here
            rig.Tick();
            rig.Advance(rig.Bg.RespawnSeconds + 5);
            rig.Tick();

            var arrivalLines = Enumerable.Range(0, 120).Select(BattlegroundText.PenArrival).ToHashSet();
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Id == victim && arrivalLines.Contains(m.Text)), "no arrival while dying");
            Assert.AreEqual(0, rig.Gateway.Respawns.Count, "no respawn while dying");

            ArriveInPen(rig, victim);
            Assert.AreEqual(1, rig.Gateway.Respawns.Count, "control: once the death sequence ends the overdue respawn is queued");
        }

        /// <summary>Drives a death to the point where the respawn is queued (pen reached, respawn time passed).</summary>
        private static void DieAndQueueRespawn(Rig rig, uint victim, PvpMatch match)
        {
            Kill(rig, victim, match);
            ArriveInPen(rig, victim);
            rig.Advance(rig.Bg.RespawnSeconds);
            rig.Tick();
        }

        /// <summary>
        /// THE LOST-RESPAWN ROW: the player-side guard refuses the queued respawn and the player stays alive in the pen.
        /// The seat is never marked alive (Respawning stays true, it is never sampled), the respawn is queued again once
        /// the retry grace passes, and the seat is confirmed alive only once the player is actually at the spawn.
        /// </summary>
        [TestMethod]
        public void RefusedRespawn_AliveInPen_IsRetried_NotStranded()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            rig.Gateway.RefuseRespawn.Add(victim);

            DieAndQueueRespawn(rig, victim, match);

            Assert.AreEqual(1, rig.Gateway.Respawns.Count, "the first respawn was queued");
            rig.Tick();
            Assert.IsTrue(rig.Gateway.LastBinding(victim).Respawning, "a refused respawn never marks the seat alive");
            Assert.AreEqual(1, rig.Gateway.Respawns.Count, "no retry inside the grace");

            rig.Advance(PvpMatchCoordinator.RespawnRetrySeconds);
            rig.Tick();

            Assert.AreEqual(2, rig.Gateway.Respawns.Count, "retried after the grace");
            Assert.IsTrue(rig.Gateway.LastBinding(victim).Respawning);

            rig.Gateway.RefuseRespawn.Remove(victim);
            rig.Advance(PvpMatchCoordinator.RespawnRetrySeconds);
            rig.Tick();

            Assert.AreEqual(3, rig.Gateway.Respawns.Count, "retried again; this one lands");
            Assert.IsTrue(rig.Gateway.LastBinding(victim).Respawning, "not confirmed until the probe sees the landing");

            rig.Tick();

            Assert.IsFalse(rig.Gateway.LastBinding(victim).Respawning, "confirmed alive at the spawn");
            Assert.AreEqual(3, rig.Gateway.Respawns.Count);
            Assert.IsTrue(rig.Gateway.Respawns.All(r => r.Spawn.PositionX == rig.Gateway.Respawns[0].Spawn.PositionX), "every retry targets the same spawn");
        }

        /// <summary>
        /// The other refusal: the player died again (a /die) before the queued respawn ran. The seat re-enters the pen
        /// cycle (DyingToPen, a fresh respawn delay from the moment it was seen dying), never retries into a dying player,
        /// and respawns normally once the new death sequence lands in the pen.
        /// </summary>
        [TestMethod]
        public void RefusedRespawn_DiedAgain_ReentersThePenCycle()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            rig.Gateway.RefuseRespawn.Add(victim);

            DieAndQueueRespawn(rig, victim, match);
            Assert.AreEqual(1, rig.Gateway.Respawns.Count);

            rig.Gateway.Players[victim].Dying = true;
            rig.Tick();
            rig.Advance(PvpMatchCoordinator.RespawnRetrySeconds);
            rig.Tick();

            Assert.AreEqual(1, rig.Gateway.Respawns.Count, "never re-queued into a dying player");
            Assert.IsTrue(rig.Gateway.LastBinding(victim).Respawning);

            rig.Gateway.RefuseRespawn.Remove(victim);
            ArriveInPen(rig, victim);
            Assert.AreEqual(1, rig.Gateway.Respawns.Count, "the fresh delay is not over");

            rig.Advance(rig.Bg.RespawnSeconds);
            rig.Tick();
            Assert.AreEqual(2, rig.Gateway.Respawns.Count, "respawned after the fresh delay");

            rig.Tick();
            Assert.IsFalse(rig.Gateway.LastBinding(victim).Respawning, "confirmed alive");
        }

        /// <summary>
        /// Forfeit while the seat is Respawning but the player is dying again, before any tick has probed it (the forfeit
        /// is synchronous): treated as dying, never teleported by the coordinator.
        /// </summary>
        [TestMethod]
        public void ForfeitWhileRespawningButDyingAgain_NoTeleport()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            rig.Gateway.RefuseRespawn.Add(victim);

            DieAndQueueRespawn(rig, victim, match);
            rig.Gateway.Players[victim].Dying = true;

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(victim).Outcome);

            CollectionAssert.DoesNotContain(rig.Gateway.Returned, victim, "a dying seat is never teleported");
            CollectionAssert.DoesNotContain(rig.Gateway.Exited, victim);
            CollectionAssert.Contains(rig.Gateway.ExitedFromPen, victim);
        }

        [TestMethod]
        public void SecondDeathWhileRespawning_IsIgnored()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            var killer = rig.Team(match, 1)[0];

            Kill(rig, victim, match, killer);
            Kill(rig, victim, match, killer);

            Assert.AreEqual(1, match.TeamKills[1], "the second death credited nothing");
            Assert.AreEqual(1, rig.Gateway.Messages.Count(m => m.Id == killer && m.Text.Contains("was slain")), "one slain line");
            Assert.AreEqual(PvpMatchState.Live, match.State);
        }

        /// <summary>A drain death names the victim as their own killer: no kill is credited to anyone.</summary>
        [TestMethod]
        public void DrainDeath_CreditsNoKiller()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];

            Kill(rig, victim, match, victim);

            Assert.AreEqual(0, match.TeamKills[0]);
            Assert.AreEqual(0, match.TeamKills[1]);
            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Text == BattlegroundText.SlainNoKiller(rig.Gateway.Players[victim].Facts.Name)));
            Assert.IsTrue(rig.Gateway.LastBinding(victim).Respawning);
        }

        /// <summary>
        /// THE RISK ROW: a republish while a seat respawns must keep Respawning. Reached through Resolve's Publish
        /// (Resolving) with one seat dying and one in the pen: both bindings carry Respawning = true and the pen.
        /// </summary>
        [TestMethod]
        public void Republish_WhileRespawning_KeepsRespawning()
        {
            var rig = LiveKoth(out var match);
            var dying = rig.Team(match, 0)[0];
            var inPen = rig.Team(match, 1)[0];
            var alive = rig.Team(match, 0)[1];

            Kill(rig, inPen, match);
            ArriveInPen(rig, inPen);

            Kill(rig, dying, match);

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);

            foreach (var id in new[] { dying, inPen })
            {
                var b = rig.Gateway.LastBinding(id);
                Assert.AreEqual(PvpMatchState.Resolving, b.State, $"0x{id:X8} got the Resolving republish");
                Assert.IsTrue(b.Respawning, $"0x{id:X8}: the republish kept Respawning");
                Assert.IsNotNull(b.RespawnPen);
            }

            Assert.IsFalse(rig.Gateway.LastBinding(alive).Respawning, "control: a living seat is not respawning");
        }

        // ================= exits =================

        /// <summary>
        /// Forfeit (/arena leave) while DyingToPen: exited through ExitMatchFromPen, NEVER teleported by the coordinator
        /// (ReturnToExit would clear the stamp the death teleport needs), and the plain ExitMatch is not used either.
        /// </summary>
        [TestMethod]
        public void ForfeitWhileDyingToPen_NoTeleport()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];

            Kill(rig, victim, match);

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(victim).Outcome);

            CollectionAssert.DoesNotContain(rig.Gateway.Returned, victim, "the coordinator never teleports a dying seat");
            CollectionAssert.DoesNotContain(rig.Gateway.Exited, victim);
            CollectionAssert.Contains(rig.Gateway.ExitedFromPen, victim);
        }

        /// <summary>Close while one seat is InPen (alive in the instance: returned) and another is still DyingToPen (not teleported).</summary>
        [TestMethod]
        public void CloseWhileInPen_ReturnsThePlayer_ButNotADyingSeat()
        {
            var rig = LiveKoth(out var match);
            var inPen = rig.Team(match, 0)[0];
            var dying = rig.Team(match, 1)[0];

            Kill(rig, inPen, match);
            ArriveInPen(rig, inPen);

            Kill(rig, dying, match);

            match.ScoreBoard[1] = rig.Bg.KothScoreTarget;
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);

            rig.Advance(rig.Dials.PostMatchSeconds);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Closed, match.State);
            CollectionAssert.Contains(rig.Gateway.Returned, inPen, "an InPen seat is alive in the instance and gets the normal return");
            CollectionAssert.Contains(rig.Gateway.Exited, inPen);
            CollectionAssert.DoesNotContain(rig.Gateway.Returned, dying, "a dying seat is never teleported");
            CollectionAssert.Contains(rig.Gateway.ExitedFromPen, dying);
        }

        [TestMethod]
        public void AdminCancelWhileDyingToPen_NoTeleport()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];

            Kill(rig, victim, match);

            Assert.AreEqual(PvpCancelOutcome.Canceled, rig.Coordinator.Cancel(match.MatchId).Outcome);

            CollectionAssert.DoesNotContain(rig.Gateway.Returned, victim);
            CollectionAssert.Contains(rig.Gateway.ExitedFromPen, victim);
            Assert.AreEqual(3, rig.Gateway.Returned.Count, "everyone else is returned");
        }

        // ================= requeue =================

        /// <summary>A decline requeues the accepters at the FRONT of the bg room (not a room named after the mode), ahead of a later joiner.</summary>
        [TestMethod]
        public void RequeueAfterDecline_LandsInTheBgRoomAtTheFront()
        {
            var rig = new Rig().Build();

            foreach (var id in new[] { A, B, C, D, E })
                rig.Gateway.Add(id, $"P{id & 0xFF}");

            rig.JoinAll(A, B, C, D);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(A).State);

            rig.Advance(1);
            rig.JoinAll(E);

            Assert.AreEqual(PvpAnswerOutcome.Declined, rig.Coordinator.Decline(D).Outcome);

            var queued = rig.Coordinator.QueuedIds(BattlegroundModes.RoomKey).ToArray();

            Assert.AreEqual(4, queued.Length, "the three accepters and E, all in the bg room");
            CollectionAssert.AreEquivalent(new[] { A, B, C }, queued.Take(3).ToArray(), "the accepters are back at the FRONT of bg");
            Assert.AreEqual(E, queued[3], "the later joiner stays behind them");
            Assert.IsTrue(rig.Coordinator.IsLockedOut(D), "the decliner is locked out");
        }

        // ================= winning =================

        [TestMethod]
        public void ScoreWin_ResolvesOnScore_RatedOnTheBattlegroundLadder()
        {
            var rig = LiveKoth(out var match);

            match.ScoreBoard[1] = rig.Bg.KothScoreTarget;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);

            var save = rig.Sink.Saves.Single();
            Assert.AreEqual(nameof(EndReason.Score), save.Match.EndReason);
            Assert.AreEqual(BattlegroundModes.LadderKey, save.Match.Ladder);
            Assert.AreEqual(BattlegroundModes.KothModeKey, save.Match.Mode);
            Assert.IsTrue(save.Match.Rated);
            Assert.AreEqual(4, save.Ratings.Count, "every player rated");
            Assert.IsTrue(save.Ratings.All(r => r.Ladder == BattlegroundModes.LadderKey));

            foreach (var id in rig.Team(match, 1))
                Assert.AreEqual("win", save.Parts.Single(p => p.CharacterId == id).Result);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual("loss", save.Parts.Single(p => p.CharacterId == id).Result);
        }

        [TestMethod]
        public void Timeout_MorePointsWins()
        {
            var rig = LiveKoth(out var match);

            match.ScoreBoard[0] = 40;
            match.ScoreBoard[1] = 10;

            rig.Advance(rig.Bg.TimeLimitSecondsKoth - 1);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State, "not before the BG time limit (and no overtime)");

            rig.Advance(1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            var save = rig.Sink.Saves.Single();
            Assert.AreEqual(nameof(EndReason.Timeout), save.Match.EndReason);
            Assert.AreEqual("win", save.Match.Outcome);
            Assert.AreEqual("win", save.Parts.Single(p => p.CharacterId == rig.Team(match, 0)[0]).Result);
            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Text == BattlegroundText.TimeUpWin(0, 40, 10)), "the battleground timeout line, not the FFA one");
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Text == PvpArenaText.TimeUpFfa));
        }

        [TestMethod]
        public void Timeout_TiedPoints_MoreTeamKillsWins()
        {
            var rig = LiveKoth(out var match);
            var killer = rig.Team(match, 1)[0];
            var victim = rig.Team(match, 0)[0];

            Kill(rig, victim, match, killer);

            match.ScoreBoard[0] = 20;
            match.ScoreBoard[1] = 20;

            rig.Advance(rig.Bg.TimeLimitSecondsKoth);
            rig.Tick();

            var save = rig.Sink.Saves.Single();
            Assert.AreEqual(nameof(EndReason.Timeout), save.Match.EndReason);
            Assert.AreEqual("win", save.Parts.Single(p => p.CharacterId == killer).Result, "the kills tiebreak");
            Assert.AreEqual("loss", save.Parts.Single(p => p.CharacterId == victim).Result);
        }

        [TestMethod]
        public void Timeout_FullTie_IsARatedDraw()
        {
            var rig = LiveKoth(out var match);

            rig.Advance(rig.Bg.TimeLimitSecondsKoth);
            rig.Tick();

            var save = rig.Sink.Saves.Single();
            Assert.AreEqual("draw", save.Match.Outcome);
            Assert.IsTrue(save.Match.Rated, "a timeout draw is rated (ruling 11)");
            Assert.AreEqual(4, save.Ratings.Count);
            Assert.IsTrue(save.Ratings.All(r => r.Draws == 1 && r.Games == 1));
            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Text == BattlegroundText.TimeUpDraw(0, 0)));
        }

        /// <summary>The rating line names the shared battleground ladder ("Battleground rating"), never the mode ("King of the Hill rating").</summary>
        [TestMethod]
        public void Resolve_RatingLine_NamesTheBattlegroundLadder()
        {
            var rig = LiveKoth(out var match);

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();

            var ratingLines = rig.Gateway.Messages.Where(m => m.Text.Contains(" rating: ")).ToList();
            Assert.AreEqual(4, ratingLines.Count, "every placed player gets a rating line");
            Assert.IsTrue(ratingLines.All(m => m.Text.Contains(BattlegroundText.ModeLabelBattleground + " rating:")), ratingLines.FirstOrDefault().Text);
            Assert.IsFalse(ratingLines.Any(m => m.Text.Contains(BattlegroundText.ModeLabelKoth)));
        }

        // ================= Mark of the Hopeslayer payout (Docs/Pvp/BATTLEGROUNDS.md "Rewards", 2026-10-08) =================

        /// <summary>Distinct from the arena's 2/1/1 so a grant that read the wrong dials cannot pass.</summary>
        private static void BgMarksDials(Rig r, bool enabled = true)
        {
            r.Dials = r.Dials with { BloodEnabled = enabled, BloodWin = 90, BloodLoss = 80, BloodDraw = 70, BloodDailyCap = 3 };
            r.Bg = r.Bg with { MarksWin = 7, MarksLoss = 3, MarksDraw = 5, MarksDailyCap = 4 };
        }

        /// <summary>Drives: the switch off (the shipped default), a KOTH score win. No grants. DISCRIMINATES the shared master switch.</summary>
        [TestMethod]
        public void Marks_PayNothing_WhenTheSharedSwitchIsOff()
        {
            var rig = LiveKoth(out var match, r => BgMarksDials(r, enabled: false));

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count);
        }

        /// <summary>
        /// Drives: the switch on, a KOTH score win by team 0. Winners get pvp_bg_marks_win, losers pvp_bg_marks_loss, every grant
        /// on the battleground ledger carrying pvp_bg_marks_daily_cap. DISCRIMINATES from the arena dials (90/80/70, cap 3).
        /// </summary>
        [TestMethod]
        public void Marks_PayWinAndLoss_OnTheBattlegroundLedger()
        {
            var rig = LiveKoth(out var match, r => BgMarksDials(r));

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();

            Assert.AreEqual(4, rig.Gateway.BloodGrants.Count);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(7, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount, "winner");

            foreach (var id in rig.Team(match, 1))
                Assert.AreEqual(3, rig.Gateway.BloodGrants.Single(g => g.Id == id).Amount, "loser");

            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Grant.Ledger == PvpBloodLedgerKind.Battleground));
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Grant.DailyCap == 4), "the battleground cap, not the arena's");
        }

        /// <summary>Drives: the switch on, a rated timeout tie. Everyone is paid pvp_bg_marks_draw.</summary>
        [TestMethod]
        public void Marks_PayTheDrawAmount_OnADraw()
        {
            var rig = LiveKoth(out var match, r => BgMarksDials(r));

            rig.Advance(rig.Bg.TimeLimitSecondsKoth);
            rig.Tick();

            Assert.AreEqual("draw", rig.Sink.Saves.Single().Match.Outcome);
            Assert.AreEqual(4, rig.Gateway.BloodGrants.Count);
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Amount == 5));
        }

        /// <summary>Drives: one losing-team member forfeits mid-match, then a team 0 score win. The forfeiter is paid nothing; everyone else is.</summary>
        [TestMethod]
        public void Marks_ForfeiterGetsNothing()
        {
            var rig = LiveKoth(out var match, r => BgMarksDials(r));
            var forfeiter = rig.Team(match, 1)[0];

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(forfeiter).Outcome);
            rig.Tick();

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsFalse(rig.Gateway.BloodGrants.Any(g => g.Id == forfeiter), "the forfeiter is paid nothing");
            Assert.AreEqual(3, rig.Gateway.BloodGrants.Count, "the other three are paid");
        }

        /// <summary>Drives: a whole team walks out during the Countdown, so the match resolves without ever going Live. Nobody is paid.</summary>
        [TestMethod]
        public void Marks_PayNothing_WhenTheMatchNeverWentLive()
        {
            var rig = GateRig(r => BgMarksDials(r));
            ToCountdown(rig);
            var match = rig.MatchOf(A);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(id).Outcome);

            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsNull(match.LiveSinceUtc, "fixture: never Live");
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count);
        }

        /// <summary>
        /// Drives: pvp_arena_block_same_ip off (the only way opposing teams can share an IP), every seat on one IP, a score win.
        /// Same-IP opposing teams are unpaid, the owner's strict rule.
        /// </summary>
        [TestMethod]
        public void Marks_PayNothing_ForSameIpOpposingTeams()
        {
            var rig = LiveKoth(out var match, r => { BgMarksDials(r); r.Dials = r.Dials with { BlockSameIp = false }; }, sharedIp: "10.9.9.9");

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count);
        }

        /// <summary>Drives: a score win, then the post-match close and extra ticks. The match is paid exactly once.</summary>
        [TestMethod]
        public void Marks_ArePaidExactlyOnce()
        {
            var rig = LiveKoth(out var match, r => BgMarksDials(r));

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(4, rig.Gateway.BloodGrants.Count);

            rig.Advance(rig.Dials.PostMatchSeconds + 1);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Closed, match.State);
            Assert.AreEqual(4, rig.Gateway.BloodGrants.Count, "closing the match pays nothing more");
        }

        /// <summary>
        /// The real King of the Hill tick through the runtime context: two team-0 players on the point score every tick
        /// and are drained; a team-1 player off the point is not; the match is won on score with no direct ScoreBoard write.
        /// </summary>
        [TestMethod]
        public void Koth_EndToEnd_HoldScoresDrainsAndWins()
        {
            var rig = LiveKoth(out var match, r => r.Bg = r.Bg with { KothScoreTarget = 10, KothHoldPoints = 5, KothHillMoves = 0 }); // the hill staying put is what this test is about; the moving hill has its own tests
            var zone = BattlegroundMapCatalog.Bg016c;

            foreach (var id in rig.Team(match, 0))
                rig.Gateway.Players[id].At = (zone.ZoneX, zone.ZoneY, zone.ZoneZ);

            rig.Advance(rig.Bg.KothTickSeconds);
            rig.Tick();

            Assert.AreEqual(5, match.ScoreBoard[0]);
            Assert.AreEqual(2, rig.Gateway.Drains.Count, "both holders drained");
            Assert.IsTrue(rig.Gateway.Drains.All(d => rig.Team(match, 0).Contains(d.Id) && d.Lethal));
            Assert.AreEqual(PvpMatchState.Live, match.State);

            rig.Advance(rig.Bg.KothTickSeconds);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(nameof(EndReason.Score), rig.Sink.Saves.Single().Match.EndReason);
        }

        [TestMethod]
        public void Koth_RespawningSeat_IsNotSampledOrDrained()
        {
            var rig = LiveKoth(out var match);
            var zone = BattlegroundMapCatalog.Bg016c;
            var holder = rig.Team(match, 0)[0];

            rig.Gateway.Players[holder].At = (zone.ZoneX, zone.ZoneY, zone.ZoneZ);
            Kill(rig, holder, match);

            rig.Advance(rig.Bg.KothTickSeconds);
            rig.Tick();

            Assert.AreEqual(0, rig.Gateway.Drains.Count);
            Assert.AreEqual(0, match.ScoreBoard[0]);
        }

        // ================= status =================

        [TestMethod]
        public void Status_ShowsScoresTargetAndTimeLeft()
        {
            var rig = LiveKoth(out var match);

            match.ScoreBoard[0] = 15;
            rig.Advance(100);

            var s = rig.Coordinator.Status(A);

            Assert.AreEqual(PvpStatusKind.InMatch, s.Kind);
            Assert.AreEqual(15, s.Scores[0]);
            Assert.AreEqual(0, s.Scores[1]);
            Assert.AreEqual(rig.Bg.KothScoreTarget, s.ScoreTarget);
            Assert.AreEqual(TimeSpan.FromSeconds(rig.Bg.TimeLimitSecondsKoth - 100), s.Remaining);
            Assert.IsFalse(s.Overtime);
        }

        // ================= matchmaking inputs =================

        /// <summary>
        /// MonarchIds reach the matchmaker: with clan splitting on, two same-allegiance pairs are split across the teams.
        /// Without per-member monarchs the matchmaker's first split (the two oldest together) would be chosen.
        /// </summary>
        [TestMethod]
        public void ToEntrant_CarriesMonarchIds()
        {
            var rig = new Rig().Build();

            rig.Gateway.Add(A, "Alpha", monarch: 0x7000);
            rig.Gateway.Add(B, "Bravo", monarch: 0x7000);
            rig.Gateway.Add(C, "Charlie", monarch: 0x8000);
            rig.Gateway.Add(D, "Delta", monarch: 0x8000);

            // Joined one second apart, so the matchmaker's unit order is A, B, C, D and its first candidate split
            // (every key equal without monarchs) is A+B against C+D.
            foreach (var id in new[] { A, B, C, D })
            {
                rig.JoinAll(id);
                rig.Advance(1);
            }

            rig.Tick();

            Assert.IsNotNull(rig.MatchOf(A), "formed");
            Assert.AreNotEqual(rig.TeamOf(A), rig.TeamOf(B), "clanmates split");
            Assert.AreNotEqual(rig.TeamOf(C), rig.TeamOf(D), "clanmates split");
        }

        /// <summary>
        /// Per-member IPs reach the matchmaker: a premade pair whose SECOND member shares an IP with a solo cannot be put
        /// opposite that solo, so no match forms. ToEntrant's unit-wide IpKey alone (the first member's) would miss it.
        /// The control, with distinct IPs, forms.
        /// </summary>
        [TestMethod]
        public void ToEntrant_CarriesPerMemberIps()
        {
            static Rig Run(string bravoIp)
            {
                var rig = new Rig().Build();

                rig.Gateway.Add(A, "Alpha", ip: "1.1.1.1", fellowship: new[] { A, B });
                rig.Gateway.Add(B, "Bravo", ip: bravoIp, fellowship: new[] { A, B });
                rig.Gateway.Add(C, "Charlie", ip: "3.3.3.3");
                rig.Gateway.Add(D, "Delta", ip: "4.4.4.4");

                Assert.IsTrue(rig.Coordinator.Join(A, "bg", false, group: true).Joined, "group join");
                rig.JoinAll(C, D);
                rig.Tick();
                return rig;
            }

            Assert.IsNotNull(Run("2.2.2.2").MatchOf(A), "control: distinct IPs form");
            Assert.IsNull(Run("3.3.3.3").MatchOf(A), "Bravo shares Charlie's IP: no legal split, no match");
        }

        // ================= gating, cap, group join =================

        [TestMethod]
        public void BgRoom_GatedByTheBgAndKothSwitches()
        {
            // Attack/Defend is a second battleground mode with its own switch: the room is closed only when EVERY mode is off.
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { KothEnabled = false, AdEnabled = false } }.Build();
            rig.Gateway.Add(A, "Alpha");
            Assert.AreEqual(PvpJoinRefusal.ModeDisabled, rig.Coordinator.Join(A, "bg", false).Refusal);

            rig.Bg = BattlegroundTunables.Defaults with { Enabled = false };
            Assert.AreEqual(PvpJoinRefusal.ModeDisabled, rig.Coordinator.Join(A, "bg", false).Refusal);

            rig.Bg = BattlegroundTunables.Defaults;
            rig.Dials = rig.Dials with { Enabled = false };
            Assert.AreEqual(PvpJoinRefusal.ArenaDisabled, rig.Coordinator.Join(A, "bg", false).Refusal);

            rig.Dials = rig.Dials with { Enabled = true };
            Assert.IsTrue(rig.Coordinator.Join(A, "bg", false).Joined, "control: every switch on");
        }

        [TestMethod]
        public void BgConcurrencyCap_HoldsTheSecondMatch()
        {
            static Rig Run(int cap)
            {
                var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, MaxConcurrentMatches = cap } }.Build();

                foreach (var id in new[] { A, B, C, D, E, F, G, H })
                    rig.Gateway.Add(id, $"P{id & 0xFF}");

                rig.JoinAll(A, B, C, D);
                rig.Tick();
                rig.JoinAll(E, F, G, H);
                rig.Tick();
                return rig;
            }

            var capped = Run(1);
            Assert.IsNotNull(capped.MatchOf(A));
            Assert.IsNull(capped.MatchOf(E), "the bg cap holds the second match");
            Assert.IsTrue(capped.Gateway.Got(E, PvpArenaText.MatchesFull));

            Assert.IsNotNull(Run(2).MatchOf(E), "control: a cap of 2 forms the second match");
        }

        [TestMethod]
        public void GroupJoin_QueuesTheWholeFellowship_AndRefusesASoloOrOutsideBg()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B, C });
            rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B, C });
            rig.Gateway.Add(C, "Charlie", fellowship: new[] { A, B, C });
            rig.Gateway.Add(D, "Delta");

            Assert.AreEqual(PvpJoinRefusal.GroupNeedsFellowship, rig.Coordinator.Join(D, "bg", false, group: true).Refusal);
            Assert.AreEqual(PvpJoinRefusal.GroupNotSupportedForMode, rig.Coordinator.Join(A, "2v2", false, group: true).Refusal);

            var r = rig.Coordinator.Join(B, "bg", false, group: true);
            Assert.IsTrue(r.Joined);
            Assert.AreEqual(3, r.WaitingCount);
            CollectionAssert.AreEquivalent(new[] { A, B, C }, rig.Coordinator.QueuedIds(BattlegroundModes.RoomKey).ToArray());
        }

        /// <summary>A fellowship larger than pvp_bg_max_premade_size is refused with the max named; one at the max joins.</summary>
        [TestMethod]
        public void GroupJoin_FellowshipLargerThanMax_IsRefused()
        {
            var rig = new Rig();
            rig.Bg = rig.Bg with { MaxPremadeSize = 3 };
            rig.Build();

            var four = new[] { A, B, C, D };
            foreach (var (id, name) in new[] { (A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta") })
                rig.Gateway.Add(id, name, fellowship: four);

            var r = rig.Coordinator.Join(A, "bg", false, group: true);
            Assert.AreEqual(PvpJoinRefusal.GroupNeedsFellowship, r.Refusal, "4 members against a max of 3");
            Assert.AreEqual(3, r.GroupMax);
            Assert.AreEqual(0, rig.Coordinator.QueuedIds(BattlegroundModes.RoomKey).Count());

            var three = new[] { E, F, G };
            foreach (var (id, name) in new[] { (E, "Echo"), (F, "Foxtrot"), (G, "Golf") })
                rig.Gateway.Add(id, name, fellowship: three);

            Assert.IsTrue(rig.Coordinator.Join(E, "bg", false, group: true).Joined, "control: a group at the max joins");
        }

        // ================= pen seals =================

        /// <summary>Staging waits on the seal placement: nobody is dispatched while the job is pending; once it completes, dispatch follows.</summary>
        [TestMethod]
        public void Fixtures_PlacedBeforeDispatch()
        {
            var rig = new Rig().Build();
            rig.Spaces.FixtureOutcome = BattlegroundFixtureStatus.Pending;

            foreach (var id in new[] { A, B, C, D })
                rig.Gateway.Add(id, $"P{id & 0xFF}");

            rig.JoinAll(A, B, C, D);
            rig.Tick();
            rig.AcceptAll(A, B, C, D);
            rig.Tick(); // Staging, allocate
            rig.Tick(); // Ready: seals queued, pending

            Assert.AreEqual(1, rig.Spaces.FixtureCalls.Count, "queued once");
            Assert.AreEqual(BattlegroundMapCatalog.Bg016c.Seals.Count, rig.Spaces.FixtureCalls[0].Pieces.Count);
            Assert.AreSame(rig.Coordinator.SpaceOf(rig.MatchOf(A).MatchId), rig.Spaces.FixtureCalls[0].Space, "into the match's own space");
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "nobody dispatched before the seals are placed");

            rig.Tick();
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "still waiting");
            Assert.AreEqual(1, rig.Spaces.FixtureCalls.Count, "not re-queued");

            rig.Spaces.FixtureCalls[0].Job.Complete(4);
            rig.Tick();

            Assert.AreEqual(4, rig.Gateway.Entered.Count, "dispatched once placed");
            Assert.IsTrue(rig.Gateway.Entered.All(e => e.Binding.RespawnPen != null && e.Binding.RespawnPen.Instance == e.Spawn.Instance), "every binding carries its pen in the match instance");
        }

        [TestMethod]
        public void Fixtures_Failure_CancelsBeforePlacement_AndRequeuesAtTheFront()
        {
            var rig = new Rig().Build();
            rig.Spaces.FixtureOutcome = BattlegroundFixtureStatus.Failed;

            foreach (var id in new[] { A, B, C, D })
                rig.Gateway.Add(id, $"P{id & 0xFF}");

            rig.JoinAll(A, B, C, D);
            rig.Tick();
            var match = rig.MatchOf(A);
            rig.AcceptAll(A, B, C, D);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "nobody placed");
            Assert.AreEqual(1, rig.Spaces.Released.Count, "the space is released");
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Canceled));
            CollectionAssert.AreEquivalent(new[] { A, B, C, D }, rig.Coordinator.QueuedIds(BattlegroundModes.RoomKey).ToArray(), "everyone back in bg");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(A), "no-fault: nobody locked out");
        }

        [TestMethod]
        public void Fixtures_NotQueued_CancelsBeforePlacement()
        {
            var rig = new Rig().Build();
            rig.Spaces.ReturnNullJob = true;

            foreach (var id in new[] { A, B, C, D })
                rig.Gateway.Add(id, $"P{id & 0xFF}");

            rig.JoinAll(A, B, C, D);
            rig.Tick();
            var match = rig.MatchOf(A);
            rig.AcceptAll(A, B, C, D);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(0, rig.Gateway.Entered.Count);
        }

        // ================= start gates =================

        /// <summary>A KotH rig with four players joined and accepted, staging not yet ticked. The gate probe records the match state at removal.</summary>
        private static Rig GateRig(Action<Rig> configure = null)
        {
            var rig = new Rig();
            configure?.Invoke(rig);
            rig.Build();
            rig.Spaces.StateProbe = () => rig.MatchOf(A)?.State;

            foreach (var id in new[] { A, B, C, D })
                rig.Gateway.Add(id, $"P{id & 0xFF}");

            rig.JoinAll(A, B, C, D);
            rig.Tick();
            rig.AcceptAll(A, B, C, D);
            return rig;
        }

        private static void ToCountdown(Rig rig)
        {
            rig.Tick(); // Staging, allocate
            rig.Tick(); // Ready: seals and gates placed, dispatch
            rig.Tick(); // arrived -> Countdown
            Assert.AreEqual(PvpMatchState.Countdown, rig.MatchOf(A).State);
        }

        [TestMethod]
        public void StartGates_BothShippedMapsDeclareThem_KothFivePerRoom_AttackDefendOneAtEachDoor()
        {
            Assert.AreEqual(10, BattlegroundMapCatalog.Bg016c.StartGates.Count, "five apertures per start room, two rooms");
            Assert.IsTrue(BattlegroundMapCatalog.Bg016c.StartGates.All(g => g.Wcid == 1001088), "the barrier weenie");
            Assert.AreEqual(2, BattlegroundMapCatalog.Bg003c.StartGates.Count, "Attack/Defend: one gate at each spawn room's only door");
            Assert.IsTrue(BattlegroundMapCatalog.Bg003c.StartGates.All(g => g.Wcid == 1001088), "the same barrier weenie");
            Assert.AreEqual(4, BattlegroundMapCatalog.Bg016c.Seals.Count, "the pen seals stay a separate list");
        }

        /// <summary>The gates go in after the seals, are waited on, and nobody is dispatched until they are placed.</summary>
        [TestMethod]
        public void StartGates_PlacedAtStaging_AfterTheSeals_BeforeDispatch()
        {
            var rig = GateRig(r => { });
            rig.Spaces.GateOutcome = BattlegroundFixtureStatus.Pending;

            rig.Tick(); // Staging, allocate
            rig.Tick(); // seals placed, gates queued and pending

            Assert.AreEqual(1, rig.Spaces.GateCalls.Count, "queued once");
            Assert.AreEqual(1, rig.Spaces.GateCalls[0].SealCallsAtCall, "after the pen seals");
            Assert.AreSame(BattlegroundMapCatalog.Bg016c.StartGates, rig.Spaces.GateCalls[0].Pieces, "the layout's own gate list, not the seals");
            Assert.AreSame(rig.Coordinator.SpaceOf(rig.MatchOf(A).MatchId), rig.Spaces.GateCalls[0].Space, "into the match's own space");
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "nobody dispatched while the gates are pending");

            rig.Tick();
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "still waiting");
            Assert.AreEqual(1, rig.Spaces.GateCalls.Count, "not re-queued");

            rig.Spaces.GateCalls[0].Job.Complete(10);
            rig.Tick();

            Assert.AreEqual(4, rig.Gateway.Entered.Count, "dispatched once the gates are placed");
        }

        /// <summary>The gates stay up through the whole Countdown and come down in the same tick the match goes Live.</summary>
        [TestMethod]
        public void StartGates_RemovedExactlyAtLive_NotBefore()
        {
            var rig = GateRig();
            ToCountdown(rig);

            Assert.AreEqual(0, rig.Spaces.GateRemoveCalls.Count, "up at the start of the Countdown");

            rig.Advance(rig.Dials.CountdownSeconds - 1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Countdown, rig.MatchOf(A).State);
            Assert.AreEqual(0, rig.Spaces.GateRemoveCalls.Count, "still up with one second to go");

            rig.Advance(1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, rig.MatchOf(A).State);
            Assert.AreEqual(1, rig.Spaces.GateRemoveCalls.Count, "down at Live");
            Assert.AreSame(rig.Spaces.GateCalls[0].Job, rig.Spaces.GateRemoveCalls[0].Placed, "the job that placed them");
            Assert.AreEqual(PvpMatchState.Live, rig.Spaces.GateRemoveCalls[0].StateAtCall, "removed in the Live transition, not earlier");
        }

        /// <summary>The pen seals are a different job and are never removed; the removal names only the gate job.</summary>
        [TestMethod]
        public void StartGates_RemovalLeavesThePenSealsAlone()
        {
            var rig = GateRig();
            ToCountdown(rig);
            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();

            Assert.AreEqual(1, rig.Spaces.FixtureCalls.Count);
            Assert.AreEqual(1, rig.Spaces.GateRemoveCalls.Count);
            Assert.AreNotSame(rig.Spaces.FixtureCalls[0].Job, rig.Spaces.GateRemoveCalls[0].Placed, "the seal job is not the one removed");
            Assert.IsFalse(rig.Spaces.RemoveCalls.Any(c => ReferenceEquals(c.Placed, rig.Spaces.FixtureCalls[0].Job)), "and the zone-marker removal never touches it");
        }

        /// <summary>An admin cancel during the Countdown takes the gates down once, and ending the match later does not remove them again.</summary>
        [TestMethod]
        public void StartGates_RemovedOnCancelDuringCountdown()
        {
            var rig = GateRig();
            ToCountdown(rig);

            Assert.AreEqual(PvpCancelOutcome.Canceled, rig.Coordinator.Cancel(rig.MatchOf(A).MatchId).Outcome);

            Assert.AreEqual(1, rig.Spaces.GateRemoveCalls.Count);
            Assert.AreSame(rig.Spaces.GateCalls[0].Job, rig.Spaces.GateRemoveCalls[0].Placed);
            Assert.AreEqual(1, rig.Spaces.Released.Count, "the space is released after");

            rig.Tick();
            Assert.AreEqual(1, rig.Spaces.GateRemoveCalls.Count, "not removed twice");
        }

        /// <summary>A gate placement that fails is the same no-fault cancel as a failed seal, and any gate that did land is taken down.</summary>
        [TestMethod]
        public void StartGates_PlacementFailure_CancelsBeforePlacement_AndRemovesWhatLanded()
        {
            var rig = GateRig();
            rig.Spaces.GateOutcome = BattlegroundFixtureStatus.Failed;
            var match = rig.MatchOf(A);

            rig.Tick();
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "nobody placed");
            Assert.AreEqual(1, rig.Spaces.GateRemoveCalls.Count, "the partial placement is taken down");
            Assert.AreEqual(1, rig.Spaces.Released.Count);
            CollectionAssert.AreEquivalent(new[] { A, B, C, D }, rig.Coordinator.QueuedIds(BattlegroundModes.RoomKey).ToArray(), "everyone back in bg");
        }

        [TestMethod]
        public void StartGates_NotQueued_CancelsBeforePlacement()
        {
            var rig = GateRig();
            rig.Spaces.GateReturnNull = true;
            var match = rig.MatchOf(A);

            rig.Tick();
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(0, rig.Gateway.Entered.Count);
            Assert.AreEqual(0, rig.Spaces.GateRemoveCalls.Count, "nothing was queued, so nothing to remove");
        }

        /// <summary>A removal that throws never stops the match going Live, nor a cancel from releasing the space.</summary>
        [TestMethod]
        public void StartGates_RemovalThrow_DoesNotStopLive_OrCancel()
        {
            var rig = GateRig(r => { });
            rig.Spaces.GateRemoveThrows = true;
            ToCountdown(rig);
            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, rig.MatchOf(A).State);

            var rig2 = GateRig();
            rig2.Spaces.GateRemoveThrows = true;
            ToCountdown(rig2);
            var match2 = rig2.MatchOf(A);
            rig2.Coordinator.Cancel(match2.MatchId);

            Assert.AreEqual(PvpMatchState.Canceled, match2.State);
            Assert.AreEqual(1, rig2.Spaces.Released.Count, "the space is still released");
        }

        /// <summary>A pre-Live resolve (the whole of one team walks out during the Countdown) takes the gates down at the resolve, and CloseNow does not remove them again.</summary>
        [TestMethod]
        public void StartGates_RemovedAtResolve_WhenAWalkoutDecidesTheMatchBeforeLive()
        {
            var rig = GateRig();
            ToCountdown(rig);
            var match = rig.MatchOf(A);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(id).Outcome);

            rig.Tick(); // the walkout poll resolves the match

            Assert.AreEqual(PvpMatchState.Resolving, match.State, "the match resolved before Live");
            Assert.AreEqual(1, rig.Spaces.GateRemoveCalls.Count, "down at the resolve, not held until CloseNow");
            Assert.AreSame(rig.Spaces.GateCalls[0].Job, rig.Spaces.GateRemoveCalls[0].Placed);

            rig.Advance(rig.Dials.PostMatchSeconds + 1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Closed, match.State);
            Assert.AreEqual(1, rig.Spaces.GateRemoveCalls.Count, "CloseNow does not remove them a second time");
        }

        /// <summary>Staging times out while the gate job is still pending: the cancel removes that pending job's gates and releases the space once.</summary>
        [TestMethod]
        public void StartGates_StagingTimeoutWithPendingGates_RemovesThePendingJob()
        {
            var rig = GateRig();
            rig.Spaces.GateOutcome = BattlegroundFixtureStatus.Pending;
            var match = rig.MatchOf(A);

            rig.Tick(); // Staging, allocate
            rig.Tick(); // seals placed, gates pending
            Assert.AreEqual(1, rig.Spaces.GateCalls.Count);

            rig.Advance(rig.Dials.StagingTimeoutSeconds + 1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(1, rig.Spaces.GateRemoveCalls.Count);
            Assert.AreSame(rig.Spaces.GateCalls[0].Job, rig.Spaces.GateRemoveCalls[0].Placed, "the pending job");
            Assert.AreEqual(1, rig.Spaces.Released.Count);
        }

        /// <summary>Pure geometry of the shipped gates: a mirrored east piece for every west piece, none on a spawn, none shared with the pen seals.</summary>
        [TestMethod]
        public void StartGates_Catalog_MirrorsEastToWest_AvoidsSpawns_AndSharesNothingWithTheSeals()
        {
            var layout = BattlegroundMapCatalog.Bg016c;
            var west = layout.StartGates.Where(g => g.CellId == 0x016C0155 || g.CellId == 0x016C0135).ToList();

            Assert.AreEqual(5, west.Count);

            foreach (var g in west)
            {
                var eastCell = g.CellId == 0x016C0155 ? 0x016C0239u : 0x016C0275u;

                Assert.IsTrue(layout.StartGates.Any(e => e.CellId == eastCell && Math.Abs(e.X - (110f - g.X)) < 0.001f && e.Y == g.Y
                    && (e.RotationZ != 0f) == (g.RotationZ != 0f)), $"an east mirror of the gate at ({g.X}, {g.Y}) in 0x{g.CellId:X8}");
            }

            foreach (var g in layout.StartGates)
            foreach (var team in layout.TeamSpawns)
            foreach (var s in team)
            {
                var d = Math.Sqrt((g.X - s.X) * (g.X - s.X) + (g.Y - s.Y) * (g.Y - s.Y));
                Assert.IsTrue(d >= 0.5, $"spawn {s.Label} is {d:0.00} m from the gate at ({g.X}, {g.Y})");
            }

            Assert.IsFalse(layout.StartGates.Any(g => layout.Seals.Contains(g)), "no piece is both a seal and a gate");
        }

        /// <summary>A layout with no start gates behaves exactly as before: no gate call, no removal, the same tick count to Live.</summary>        [TestMethod]
        public void StartGates_LayoutWithNone_BehavesAsBefore()
        {
            var bare = BattlegroundMapCatalog.Bg016c with { StartGates = Array.Empty<BattlegroundSealPiece>() };
            var rig = GateRig(r => r.Layouts = key => BattlegroundMapCatalog.Find(key) == null ? null : bare);

            ToCountdown(rig);
            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, rig.MatchOf(A).State);
            Assert.AreEqual(0, rig.Spaces.GateCalls.Count);
            Assert.AreEqual(0, rig.Spaces.GateRemoveCalls.Count);
            Assert.AreEqual(1, rig.Spaces.FixtureCalls.Count, "the pen seals are placed as ever");
        }

        // ================= zone markers =================

        private const uint MarkerWcid = 4242;

        /// <summary>The shipped map with a non-zero marker wcid (the catalog still carries the placeholder 0).</summary>
        private static readonly BattlegroundLayout MarkerLayout = BattlegroundMapCatalog.Bg016c with { ZoneMarkerWcid = MarkerWcid };

        /// <summary>Every battleground map key resolves to <see cref="MarkerLayout"/>; any other key to null, as the catalog does.</summary>
        private static Func<string, BattlegroundLayout> MarkerLayouts() => key => BattlegroundMapCatalog.Find(key) == null ? null : MarkerLayout;

        private static Rig MarkerRig(Action<Rig> configure = null)
        {
            var rig = new Rig { Layouts = MarkerLayouts() };
            configure?.Invoke(rig);
            return rig;
        }

        /// <summary>
        /// The ring is queued exactly once, after the seals completed and before anyone is dispatched, into the match's own
        /// space, planned from the match's dials snapshot (16 at the defaults, every one on the zone edge).
        /// </summary>
        [TestMethod]
        public void ZoneMarkers_QueuedOnce_AfterTheSeals_IntoTheMatchSpace()
        {
            var rig = MarkerRig().Build();
            rig.Spaces.FixtureOutcome = BattlegroundFixtureStatus.Pending;

            foreach (var id in new[] { A, B, C, D })
                rig.Gateway.Add(id, $"P{id & 0xFF}");

            rig.JoinAll(A, B, C, D);
            rig.Tick();
            rig.AcceptAll(A, B, C, D);
            rig.Tick(); // Staging, allocate
            rig.Tick(); // seals queued, pending

            Assert.AreEqual(1, rig.Spaces.FixtureCalls.Count);
            Assert.AreEqual(0, rig.Spaces.MarkerCalls.Count, "nothing before the seals are placed");

            rig.Tick();
            Assert.AreEqual(0, rig.Spaces.MarkerCalls.Count, "still nothing while the seals are pending");

            rig.Spaces.FixtureCalls[0].Job.Complete(4);
            rig.Tick();

            Assert.AreEqual(1, rig.Spaces.MarkerCalls.Count, "queued once the seals are placed");
            var call = rig.Spaces.MarkerCalls[0];
            Assert.AreEqual(0, call.EnteredAtCall, "queued before dispatch");
            Assert.AreEqual(1, call.SealCallsAtCall, "queued after the seals");
            Assert.AreEqual(4, rig.Gateway.Entered.Count, "dispatched on the same tick");
            Assert.AreSame(rig.Coordinator.SpaceOf(rig.MatchOf(A).MatchId), call.Space, "into the match's own space");

            var zone = BattlegroundModes.KothZoneFor(MarkerLayout, rig.Bg);
            Assert.AreEqual(16, call.Pieces.Count);
            Assert.IsTrue(call.Pieces.All(p => p.Wcid == MarkerWcid && zone.Contains(p.X, p.Y, p.Z)));

            rig.Tick();
            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, rig.MatchOf(A).State);
            Assert.AreEqual(1, rig.Spaces.MarkerCalls.Count, "never re-queued");
            Assert.AreEqual(1, rig.Spaces.FixtureCalls.Count, "FixtureCalls stays seals-only");
        }

        /// <summary>
        /// The ring is the one planned at FORMATION: the live dials moving to radius 9 before the seals complete do not
        /// change what is queued (still 16 markers at 6 m, on the zone the match scores).
        /// </summary>
        [TestMethod]
        public void ZoneMarkers_UseTheFormationSnapshot_NotTheLiveDials()
        {
            var rig = MarkerRig().Build();
            rig.Spaces.FixtureOutcome = BattlegroundFixtureStatus.Pending;

            foreach (var id in new[] { A, B, C, D })
                rig.Gateway.Add(id, $"P{id & 0xFF}");

            rig.JoinAll(A, B, C, D);
            rig.Tick(); // formed with radius 6
            var formedZone = BattlegroundModes.KothZoneFor(MarkerLayout, rig.Bg);
            rig.AcceptAll(A, B, C, D);
            rig.Tick();
            rig.Tick(); // seals pending

            rig.Bg = rig.Bg with { KothZoneRadius = 9 };
            rig.Spaces.FixtureCalls[0].Job.Complete(4);
            rig.Tick();

            Assert.AreEqual(1, rig.Spaces.MarkerCalls.Count);
            var pieces = rig.Spaces.MarkerCalls[0].Pieces;
            Assert.AreEqual(16, pieces.Count, "still the radius-6 ring");

            foreach (var p in pieces)
            {
                var dist = Math.Sqrt((p.X - formedZone.CenterX) * (p.X - formedZone.CenterX) + (p.Y - formedZone.CenterY) * (p.Y - formedZone.CenterY));
                Assert.AreEqual(6.0, dist, 1e-4);
            }
        }

        /// <summary>The ring is planned from the dials snapshot the match formed with: radius 9 gives 23 markers at 9 m.</summary>
        [TestMethod]
        public void ZoneMarkers_FollowTheMatchDials()
        {
            var rig = LiveKoth(out _, r => { r.Layouts = MarkerLayouts(); r.Bg = r.Bg with { KothZoneRadius = 9 }; });

            Assert.AreEqual(1, rig.Spaces.MarkerCalls.Count);
            Assert.AreEqual(23, rig.Spaces.MarkerCalls[0].Pieces.Count);
        }

        /// <summary>
        /// The ring a match queues carries pvp_bg_koth_marker_z_offset from the formation snapshot: at the shipped default
        /// every marker sits at the zone z plus that offset, and a custom dial moves it.
        /// </summary>
        [TestMethod]
        public void ZoneMarkers_CarryTheZOffsetFromTheMatchDials()
        {
            var shipped = LiveKoth(out _, r => r.Layouts = MarkerLayouts());
            var shippedZ = (float)(MarkerLayout.ZoneZ + BattlegroundTunables.Defaults.KothMarkerZOffset);

            Assert.AreEqual(1, shipped.Spaces.MarkerCalls.Count);
            Assert.IsTrue(shipped.Spaces.MarkerCalls[0].Pieces.All(p => Math.Abs(p.Z - shippedZ) < 1e-6), "the shipped default offset");
            Assert.AreNotEqual(MarkerLayout.ZoneZ, shipped.Spaces.MarkerCalls[0].Pieces[0].Z, "not at the zone z");

            var custom = LiveKoth(out _, r => { r.Layouts = MarkerLayouts(); r.Bg = r.Bg with { KothMarkerZOffset = 0.75 }; });

            Assert.IsTrue(custom.Spaces.MarkerCalls[0].Pieces.All(p => Math.Abs(p.Z - (MarkerLayout.ZoneZ + 0.75f)) < 1e-6), "a custom dial moves the ring");
        }

        // ================= moving hill =================

        /// <summary>
        /// The hill moves when the leader reaches 1/3 of the target: the old ring is removed (the job that placed it), a new ring
        /// is queued on the trailing team's site, every seat is told the compass word, and the match stays Live.
        /// </summary>
        [TestMethod]
        public void MovingHill_ReplansTheMarkerRing_RemovingTheOldOne_AndAnnounces()
        {
            var rig = LiveKoth(out var match, r => r.Layouts = MarkerLayouts());
            var first = rig.Spaces.MarkerCalls[0];

            Assert.AreEqual(1, rig.Spaces.MarkerCalls.Count);
            Assert.AreEqual(0, rig.Spaces.RemoveCalls.Count);

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget / 3;
            match.ScoreBoard[1] = 0;
            rig.Tick();

            Assert.AreEqual(1, rig.Spaces.RemoveCalls.Count, "the old ring is removed once");
            Assert.AreSame(first.Job, rig.Spaces.RemoveCalls[0].Placed, "exactly the job that placed the old ring");
            Assert.AreEqual(2, rig.Spaces.MarkerCalls.Count, "a new ring is queued");
            Assert.AreEqual(1, rig.Spaces.RemoveCalls[0].MarkerCallsAtCall, "removed before the new ring was queued");

            var pieces = rig.Spaces.MarkerCalls[1].Pieces;
            Assert.AreEqual(16, pieces.Count);
            Assert.AreEqual(95.0, pieces.Average(p => p.X), 1e-3, "centred on the east room: west leads, east trails");
            Assert.AreEqual(-15.0, pieces.Average(p => p.Y), 1e-3);
            Assert.IsTrue(pieces.All(p => p.Wcid == MarkerWcid), "the same wcid");
            Assert.AreEqual(first.Pieces[0].Z, pieces[0].Z, 1e-6, "the same height offset");
            Assert.AreSame(rig.Coordinator.SpaceOf(match.MatchId), rig.Spaces.MarkerCalls[1].Space);

            Assert.IsTrue(rig.Gateway.Messages.Any(msg => msg.Text == "[Battleground] The hill has moved! Head to the north-east."), "every seat is told");
            Assert.AreEqual(PvpMatchState.Live, match.State);

            rig.Tick();
            rig.Tick();
            Assert.AreEqual(2, rig.Spaces.MarkerCalls.Count, "the step fires once");
        }

        /// <summary>
        /// The scored zone and the marker ring come from the match's resolved layout, not a hard-wired one: with a Layouts hook
        /// returning a different centre and pair, a team-0 player standing at the first ring's centre scores, and after the
        /// move the player at the NEW ring's centre scores.
        /// </summary>
        [TestMethod]
        public void MovingHill_ScoredZoneAndRingShareTheMatchsResolvedLayout()
        {
            var custom = MarkerLayout with { ZoneX = 70f, ZoneY = -35f, ZonePairs = new[] { new KothSitePair(30f, -20f) } };
            var rig = LiveKoth(out var match, r => r.Layouts = key => BattlegroundMapCatalog.Find(key) == null ? null : custom);
            var ring0 = rig.Spaces.MarkerCalls[0].Pieces;
            var c0 = (X: ring0.Average(p => p.X), Y: ring0.Average(p => p.Y));

            Assert.AreEqual(70.0, c0.X, 1e-3, "the ring is on the resolved layout's centre");

            var team0 = rig.Team(match, 0);
            foreach (var id in team0)
                rig.Gateway.Players[id].At = ((float)c0.X, (float)c0.Y, custom.ZoneZ);

            rig.Advance(rig.Bg.KothTickSeconds);
            rig.Tick();
            Assert.AreEqual(rig.Bg.KothHoldPoints, match.ScoreBoard[0], "the handler scores the same zone the ring sits on");

            // West leads, so the single pair's east site is the destination: (2 x 70 - 30, -20).
            match.ScoreBoard[0] = rig.Bg.KothScoreTarget / 2;
            rig.Tick();

            Assert.AreEqual(1, rig.Spaces.RemoveCalls.Count);
            var ring1 = rig.Spaces.MarkerCalls[1].Pieces;
            var c1 = (X: ring1.Average(p => p.X), Y: ring1.Average(p => p.Y));
            Assert.AreEqual(110.0, c1.X, 1e-3);
            Assert.AreEqual(-20.0, c1.Y, 1e-3);

            foreach (var id in team0)
                rig.Gateway.Players[id].At = ((float)c1.X, (float)c1.Y, custom.ZoneZ);

            var before = match.ScoreBoard[0];
            rig.Advance(rig.Bg.KothTickSeconds);
            rig.Tick();
            Assert.AreEqual(before + rig.Bg.KothHoldPoints, match.ScoreBoard[0], "the handler now scores the new ring's zone");
        }

        /// <summary>A failing removal or a refused placement never touches the match.</summary>
        [TestMethod]
        public void MovingHill_RemovalOrPlacementFailing_NeverBlocksTheMatch()
        {
            var rig = LiveKoth(out var match, r => r.Layouts = MarkerLayouts());
            rig.Spaces.RemoveThrows = true;
            rig.Spaces.MarkerReturnNull = true;

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget / 3;
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(1, rig.Spaces.RemoveCalls.Count);
            Assert.AreEqual(PvpMatchState.Live, match.State, "the match carries on");
        }

        /// <summary>With the markers off or no marker wcid there is no ring to re-plan: the hill still moves, nothing is removed or placed.</summary>
        [TestMethod]
        public void MovingHill_WithNoRing_StillMoves_ButTouchesNoMarkers()
        {
            var rig = LiveKoth(out var match, r => r.Layouts = key => BattlegroundMapCatalog.Find(key) == null ? null : MarkerLayout with { ZoneMarkerWcid = 0 });

            Assert.AreEqual(0, rig.Spaces.MarkerCalls.Count);

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget / 3;
            rig.Tick();

            Assert.AreEqual(0, rig.Spaces.RemoveCalls.Count);
            Assert.AreEqual(0, rig.Spaces.MarkerCalls.Count);
            Assert.IsTrue(rig.Gateway.Messages.Any(msg => msg.Text.Contains("The hill has moved!")), "the announcement does not depend on the ring");
        }

        /// <summary>pvp_bg_koth_hill_moves = 0 keeps today's single hill: no removal, no re-plan, no announcement.</summary>
        [TestMethod]
        public void MovingHill_ZeroMoves_NeverReplans()
        {
            var rig = LiveKoth(out var match, r => { r.Layouts = MarkerLayouts(); r.Bg = r.Bg with { KothHillMoves = 0 }; });

            match.ScoreBoard[0] = rig.Bg.KothScoreTarget - 1;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.AreEqual(0, rig.Spaces.RemoveCalls.Count);
            Assert.AreEqual(1, rig.Spaces.MarkerCalls.Count);
            Assert.IsFalse(rig.Gateway.Messages.Any(msg => msg.Text.Contains("The hill has moved!")));
        }

        /// <summary>A marker job that never finishes, fails, comes back null or throws: the match still goes Live, uncanceled.</summary>
        [TestMethod]
        public void ZoneMarkers_PendingFailedNullOrThrowing_NeverDelayOrCancel()
        {
            var cases = new (string Name, Action<FakeSpaces> Set)[]
            {
                ("pending", s => s.MarkerOutcome = BattlegroundFixtureStatus.Pending),
                ("failed", s => s.MarkerOutcome = BattlegroundFixtureStatus.Failed),
                ("null job", s => s.MarkerReturnNull = true),
                ("throws", s => s.MarkerThrows = true),
            };

            foreach (var (name, set) in cases)
            {
                // LiveKoth asserts Countdown after the dispatch tick and Live after the countdown: a delay or a cancel fails it.
                var rig = LiveKoth(out var match, r => { r.Layouts = MarkerLayouts(); set(r.Spaces); });

                Assert.AreEqual(1, rig.Spaces.MarkerCalls.Count, name);
                Assert.AreEqual(PvpMatchState.Live, match.State, name);
                Assert.AreEqual(4, rig.Gateway.Entered.Count, name);
                Assert.AreEqual(0, rig.Spaces.Released.Count, $"{name}: the space is not released");

                rig.Tick();
                Assert.AreEqual(PvpMatchState.Live, match.State, $"{name}: still Live a tick later");
                Assert.AreEqual(1, rig.Spaces.MarkerCalls.Count, $"{name}: never retried");
            }
        }

        [TestMethod]
        public void ZoneMarkers_SettingOff_NoCall()
        {
            var rig = LiveKoth(out _, r => { r.Layouts = MarkerLayouts(); r.Bg = r.Bg with { KothMarkersEnabled = false }; });

            Assert.AreEqual(0, rig.Spaces.MarkerCalls.Count);
            Assert.AreEqual(1, rig.Spaces.FixtureCalls.Count, "control: the seals still went in");
        }

        [TestMethod]
        public void ZoneMarkers_WcidZero_NoCall()
        {
            var explicitZero = LiveKoth(out _, r => r.Layouts = key => BattlegroundMapCatalog.Find(key) == null ? null : MarkerLayout with { ZoneMarkerWcid = 0 });
            Assert.AreEqual(0, explicitZero.Spaces.MarkerCalls.Count);

            // Control: the shipped catalog (purple, 1006803) does queue the ring.
            var shipped = LiveKoth(out _);
            Assert.AreEqual(1, shipped.Spaces.MarkerCalls.Count);
            Assert.IsTrue(shipped.Spaces.MarkerCalls[0].Pieces.All(p => p.Wcid == 1006803u));
        }

        /// <summary>An arena match never plans or queues markers, even when every layout lookup would hand back a marker layout.</summary>
        [TestMethod]
        public void ZoneMarkers_ArenaMode_NoCall()
        {
            var rig = new Rig { Layouts = _ => MarkerLayout }.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");

            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(B, "1v1", false).Joined);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(A).State);
            rig.AcceptAll(A, B);
            rig.DriveAcceptedToLive(A);

            Assert.AreEqual(2, rig.Gateway.Entered.Count, "control: the arena match dispatched");
            Assert.AreEqual(0, rig.Spaces.MarkerCalls.Count);
            Assert.AreEqual(0, rig.Spaces.FixtureCalls.Count);
        }

        // ================= PvP Template Facets: King of the Hill is templated (owner ruling 2026-10-03) =================

        /// <summary>
        /// Drives: a bg join with no key and no remembered template, then one whose template is not offered for bg_koth.
        /// DISCRIMINATES the join-time template check on the bg room: both refused, nothing queued. A key offered for
        /// bg_koth joins (control).
        /// </summary>
        [TestMethod]
        public void Koth_Join_RequiresATemplateOfferedForKoth()
        {
            var rig = new Rig();
            rig.Sink.TemplateRows = new List<PvpTemplateRecord>
            {
                TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1,2v2,ffa"),
                TemplateRow("brawler", "Brawler", 2, true, "bg_koth"),
            };
            rig.Build();
            rig.Gateway.Add(A, "Alpha").Facts = rig.Gateway.Players[A].Facts with { PreferredTemplateKey = null };
            rig.Gateway.Add(B, "Bravo");

            Assert.AreEqual(PvpJoinRefusal.NoTemplateChosen, rig.Coordinator.Join(A, "bg", false).Refusal);

            var notOffered = rig.Coordinator.Join(B, "bg", false);
            Assert.AreEqual(PvpJoinRefusal.TemplateNotOffered, notOffered.Refusal, "duelist is offered only in the arena modes");
            Assert.AreEqual(DefaultTemplateKey, notOffered.TemplateKey);
            Assert.AreEqual(0, rig.Coordinator.QueuedIds(BattlegroundModes.RoomKey).Count());

            var ok = rig.Coordinator.Join(B, "bg", false, "Brawler");
            Assert.IsTrue(ok.Joined, "control: a template offered for bg_koth joins");
            Assert.AreEqual("brawler", ok.TemplateKey);
        }

        /// <summary>Drives: a bg join by a template-account character, and by one still carrying a restore record. Both refused.</summary>
        [TestMethod]
        public void Koth_Join_TemplateAccountAndStillTemplated_AreRefused()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha").Facts = rig.Gateway.Players[A].Facts with { IsTemplateAccount = true };
            rig.Gateway.Add(B, "Bravo").Facts = rig.Gateway.Players[B].Facts with { IsPvpTemplated = true };

            Assert.AreEqual(PvpJoinRefusal.TemplateAccount, rig.Coordinator.Join(A, "bg", false).Refusal);
            Assert.AreNotEqual(PvpJoinRefusal.None, rig.Coordinator.Join(B, "bg", false).Refusal);
            Assert.AreEqual(0, rig.Coordinator.QueuedIds(BattlegroundModes.RoomKey).Count());
        }

        /// <summary>
        /// Drives: four players with NO PK facet join, accept and go Live. DISCRIMINATES "the PK facet is not required on
        /// a templated mode" at both admission (RequiresPkFacet) and accept (the untemplated-only facet re-check).
        /// </summary>
        [TestMethod]
        public void Koth_PkFacet_IsNotRequired()
        {
            var noFacet = new Rig().Build();

            foreach (var (id, name) in new[] { (E, "Echo"), (F, "Foxtrot"), (G, "Golf"), (H, "Hotel") })
            {
                var p = noFacet.Gateway.Add(id, name);
                p.Facts = p.Facts with { OnPkFacet = false };
            }

            noFacet.JoinAll(E, F, G, H);
            noFacet.Tick();
            noFacet.AcceptAll(E, F, G, H);
            Assert.AreEqual(PvpMatchState.Live, noFacet.DriveAcceptedToLive(E).State);
        }

        /// <summary>
        /// Drives: KOTH to Live with every pvp_arena_suppress_* dial off and B on a second template. Every binding (entry
        /// and every republish) carries its OWN frozen template, the five masks forced on, and is not Untemplated.
        /// </summary>
        [TestMethod]
        public void Koth_Bindings_CarryTheFrozenTemplate_AndForcedMasks()
        {
            var rig = LiveKoth(out var match, r =>
            {
                r.Dials = r.Dials with { SuppressClassAbilities = false, SuppressEquipmentMods = false, SuppressWeaponMods = false, SuppressPickupBoons = false, SuppressTurnSpeed = false };
                r.Sink.TemplateRows.Add(TemplateRow("brawler", "Brawler", 2, true, "bg_koth"));
            }, prefer: new Dictionary<uint, string> { [B] = "brawler" });

            Assert.AreEqual(4, rig.Gateway.Entered.Count);

            foreach (var (id, b) in rig.Gateway.Entered.Select(e => (e.Id, e.Binding)).Concat(rig.Gateway.Published.Select(p => (p.Id, p.Binding))))
            {
                Assert.IsTrue(b.ClassAbilitiesSuppressed && b.EquipmentModsSuppressed && b.WeaponModsSuppressed && b.PickupBoonsSuppressed && b.TurnSpeedSuppressed, $"0x{id:X8}: masks forced");
                Assert.IsFalse(b.Untemplated, $"0x{id:X8}");
                Assert.AreEqual(id == B ? "brawler" : DefaultTemplateKey, b.Template?.Key, $"0x{id:X8}: its own template");
            }
        }

        /// <summary>
        /// Drives: a Live KOTH death, the pen, the respawn. DISCRIMINATES "a respawn death never ends participation": the
        /// victim is never exited (so never restored) across the whole pen cycle, and every binding it is republished with
        /// (dying, in the pen, alive again) still carries the frozen template. Only leaving the match exits it, once.
        /// </summary>
        [TestMethod]
        public void Koth_RespawnKeepsTheTemplate_LeavingExitsOnce()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];

            DieAndQueueRespawn(rig, victim, match);
            rig.Tick(); // confirmed alive

            Assert.IsFalse(rig.Gateway.LastBinding(victim).Respawning, "fixture: respawned");
            Assert.AreEqual(0, rig.Gateway.Exited.Count(x => x == victim) + rig.Gateway.ExitedFromPen.Count(x => x == victim), "a respawn death never exits (so never restores)");
            Assert.IsTrue(rig.Gateway.Published.Where(p => p.Id == victim).All(p => p.Binding.Template?.Key == DefaultTemplateKey), "every republish keeps the template");

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(victim).Outcome);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(1, rig.Gateway.Exited.Count(x => x == victim) + rig.Gateway.ExitedFromPen.Count(x => x == victim), "leaving the match exits exactly once");
        }

        /// <summary>
        /// Drives: a Live KOTH death; backstop ticks through DyingToPen, InPen and after the respawn. DISCRIMINATES the
        /// per-tick template backstop's pen-cycle skip: the victim is never backstopped while dying or in the pen (its
        /// kit is not to be moved mid-death), the living are, and the victim is again once it is alive at a spawn.
        /// </summary>
        [TestMethod]
        public void Koth_Backstop_SkipsSeatsInThePenCycle()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            var others = match.Teams.SelectMany(t => t.Members).Select(p => p.CharacterId).Where(id => id != victim).ToArray();

            Kill(rig, victim, match);
            rig.Gateway.Backstops.Clear();
            rig.Advance(1);
            rig.Tick();

            CollectionAssert.AreEquivalent(others, rig.Gateway.Backstops.Select(b => b.Id).ToList(), "dying: skipped");

            ArriveInPen(rig, victim);
            rig.Gateway.Backstops.Clear();
            rig.Advance(1);
            rig.Tick();
            CollectionAssert.DoesNotContain(rig.Gateway.Backstops.Select(b => b.Id).ToList(), victim, "in the pen: skipped");

            rig.Advance(rig.Bg.RespawnSeconds);
            rig.Tick(); // respawn queued
            rig.Tick(); // confirmed alive
            rig.Gateway.Backstops.Clear();
            rig.Advance(1);
            rig.Tick();
            CollectionAssert.Contains(rig.Gateway.Backstops.Select(b => b.Id).ToList(), victim, "alive again: backstopped");
        }

        /// <summary>
        /// Drives: a group join (A + B fellowship) where B remembers a second template, then one whose member has none.
        /// Each member fights on their OWN template; a member with no usable template refuses the whole group.
        /// </summary>
        [TestMethod]
        public void Koth_GroupJoin_EachMemberUsesTheirOwnTemplate_AndAMemberWithoutOneRefusesTheGroup()
        {
            var rig = LiveKoth(out var match, r => r.Sink.TemplateRows.Add(TemplateRow("brawler", "Brawler", 2, true, "bg_koth")),
                prefer: new Dictionary<uint, string> { [B] = "brawler" }, groupAB: true);

            Assert.AreEqual(DefaultTemplateKey, rig.Gateway.Entered.Single(e => e.Id == A).Binding.Template.Key);
            Assert.AreEqual("brawler", rig.Gateway.Entered.Single(e => e.Id == B).Binding.Template.Key, "not the caller's template");

            var refused = new Rig().Build();
            refused.Gateway.Add(E, "Echo", fellowship: new[] { E, F });
            refused.Gateway.Add(F, "Foxtrot", fellowship: new[] { E, F }).Facts = refused.Gateway.Players[F].Facts with { PreferredTemplateKey = null };

            var r = refused.Coordinator.Join(E, "bg", false, group: true);
            Assert.AreEqual(PvpJoinRefusal.GroupMemberIneligible, r.Refusal);
            Assert.AreEqual("Foxtrot", r.PartnerName);
            Assert.AreEqual(0, refused.Coordinator.QueuedIds(BattlegroundModes.RoomKey).Count());
        }

        /// <summary>Drives: KOTH to a score win. Each participant's template is stamped onto the saved match and /top bg can name it.</summary>
        [TestMethod]
        public void Koth_Results_StampEachTemplate_OnTheBattlegroundLadder()
        {
            var rig = LiveKoth(out var match);

            match.ScoreBoard[1] = rig.Bg.KothScoreTarget;
            rig.Tick();
            rig.Tick();

            var write = rig.Sink.StampWrites.Single();
            Assert.AreEqual(4, write.Stamps.Count);
            Assert.IsTrue(write.Stamps.All(s => s.TemplateKey == DefaultTemplateKey));
            Assert.AreEqual("Duelist", rig.Coordinator.LatestTemplateLabel(A, BattlegroundModes.LadderKey));
        }

        // ================= spawn protection (Docs/Pvp/BATTLEGROUNDS.md "Spawn protection") =================

        /// <summary>Drives a death, the pen, the respawn queue, a delay for the trip, then the confirming tick. Returns the issue time and the confirm time.</summary>
        private static (DateTime Issued, DateTime Confirmed) RespawnWithTrip(Rig rig, uint victim, PvpMatch match, double tripSeconds)
        {
            DieAndQueueRespawn(rig, victim, match);

            var issued = rig.Now;

            rig.Advance(tripSeconds);
            rig.Tick();

            return (issued, rig.Now);
        }

        /// <summary>The window opens when the probe confirms the player at the spawn, not when the teleport was issued: a 2 s trip leaves the full 3 s.</summary>
        [TestMethod]
        public void SpawnProtection_StartsAtConfirm_NotAtTheTeleport_AndSaysSo()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            var atStart = rig.Gateway.Messages.Count(m => m.Id == victim && m.Text == "[Battleground] You are protected for 3 seconds. Attacking ends it early.");

            Assert.AreEqual(1, atStart, "fixture: the match-start line was sent once");

            var (issued, confirmed) = RespawnWithTrip(rig, victim, match, tripSeconds: 2);
            var binding = rig.Gateway.LastBinding(victim);

            Assert.IsFalse(binding.Respawning, "fixture: confirmed");
            Assert.AreEqual(confirmed.AddSeconds(3), binding.ProtectedUntilUtc, "confirm time plus the window");
            Assert.AreNotEqual(issued.AddSeconds(3), binding.ProtectedUntilUtc, "not the issue time plus the window");
            Assert.AreEqual(atStart + 1, rig.Gateway.Messages.Count(m => m.Id == victim && m.Text == "[Battleground] You are protected for 3 seconds. Attacking ends it early."), "and the respawn confirm sent one more");
        }

        [TestMethod]
        public void SpawnProtection_RefusesHitsInsideTheWindow_AndExpiresAfterNSeconds()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];
            var enemy = rig.Team(match, 1)[0];

            var (_, confirmed) = RespawnWithTrip(rig, victim, match, 0);
            var target = rig.Gateway.LastBinding(victim);
            var attacker = rig.Gateway.LastBinding(enemy);

            Assert.AreEqual(PvpGateDecision.RefuseProtected, PvpArenaGate.Evaluate(attacker, target, false, nowUtc: confirmed.AddSeconds(2.9)));
            Assert.AreEqual(PvpGateDecision.Allow, PvpArenaGate.Evaluate(attacker, target, false, nowUtc: confirmed.AddSeconds(3.1)));
        }

        [TestMethod]
        public void SpawnProtection_DialZero_Disables_AndTheLiveValueIsInTheMessage()
        {
            var off = LiveKoth(out var offMatch, r => r.Bg = r.Bg with { SpawnProtectionSeconds = 0 });
            var offVictim = off.Team(offMatch, 0)[0];
            RespawnWithTrip(off, offVictim, offMatch, 0);

            Assert.IsNull(off.Gateway.LastBinding(offVictim).ProtectedUntilUtc, "0 disables it");
            Assert.IsFalse(off.Gateway.Messages.Any(m => m.Text.Contains("protected for")), "and says nothing");

            var five = LiveKoth(out var fiveMatch, r => r.Bg = r.Bg with { SpawnProtectionSeconds = 5 });
            var fiveVictim = five.Team(fiveMatch, 0)[0];
            var (_, confirmed) = RespawnWithTrip(five, fiveVictim, fiveMatch, 0);

            Assert.AreEqual(confirmed.AddSeconds(5), five.Gateway.LastBinding(fiveVictim).ProtectedUntilUtc);
            Assert.IsTrue(five.Gateway.Got(fiveVictim, "[Battleground] You are protected for 5 seconds. Attacking ends it early."), "the live value, not a literal 3");
        }

        /// <summary>The window is snapshotted at formation like the other bg dials: raising the setting mid-match changes nothing for this match.</summary>
        [TestMethod]
        public void SpawnProtection_IsSnapshottedAtFormation()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];

            rig.Bg = rig.Bg with { SpawnProtectionSeconds = 30 };
            var (_, confirmed) = RespawnWithTrip(rig, victim, match, 0);

            Assert.AreEqual(confirmed.AddSeconds(3), rig.Gateway.LastBinding(victim).ProtectedUntilUtc);
        }

        /// <summary>
        /// Match start: every King of the Hill seat carries the plain N-second window when the match goes Live (this map has no spawn
        /// rooms), says so, and the first Live binding already holds it. Dial 0 gives nothing.
        /// </summary>
        [TestMethod]
        public void SpawnProtection_AtMatchStart_KothSeatsGetThePlainWindow()
        {
            var rig = LiveKoth(out var match);

            foreach (var id in match.Teams.SelectMany(t => t.Members).Select(p => p.CharacterId))
            {
                var binding = rig.Gateway.LastBinding(id);

                Assert.IsFalse(binding.ProtectedInRoom, "no spawn room on this map: a plain window");
                Assert.IsNotNull(binding.ProtectedUntilUtc, $"0x{id:X8} is protected at Live");
                Assert.IsTrue(binding.IsSpawnProtected(binding.ProtectedUntilUtc.Value.AddSeconds(-2.9)), "for the window");
                Assert.IsFalse(binding.IsSpawnProtected(binding.ProtectedUntilUtc.Value), "and not beyond it");
                Assert.AreEqual(1, rig.Gateway.Messages.Count(m => m.Id == id && m.Text == "[Battleground] You are protected for 3 seconds. Attacking ends it early."), "the plain wording, once");
            }

            var off = LiveKoth(out var offMatch, r => r.Bg = r.Bg with { SpawnProtectionSeconds = 0 });

            foreach (var id in offMatch.Teams.SelectMany(t => t.Members).Select(p => p.CharacterId))
                Assert.IsFalse(off.Gateway.LastBinding(id).HasSpawnProtection, "dial 0 disables the match-start grant too");

            Assert.IsFalse(off.Gateway.Messages.Any(m => m.Text.Contains("protected for")), "and says nothing");
        }

        /// <summary>
        /// The Live guard on the window: a respawn the probe confirms while the match is no longer Live opens none. AdvanceRespawns only
        /// runs from the Live tick, so no public Tick reaches this; the private confirm is driven directly with the match set Resolving.
        /// </summary>
        [TestMethod]
        public void SpawnProtection_ARespawnConfirmedWhileResolving_GetsNoWindow()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];

            DieAndQueueRespawn(rig, victim, match); // queued, landed, not yet confirmed

            match.SetState(PvpMatchState.Resolving);

            var linesBefore = rig.Gateway.Messages.Count(m => m.Id == victim && m.Text.Contains("protected for"));

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var matches = (System.Collections.IEnumerable)typeof(PvpMatchCoordinator).GetField("matches", flags).GetValue(rig.Coordinator);
            object runtime = null;

            foreach (var candidate in matches)
                runtime = candidate;

            var seats = (System.Collections.IDictionary)runtime.GetType().GetProperty("Seats").GetValue(runtime);
            var seat = seats[victim];

            typeof(PvpMatchCoordinator).GetMethod("ConfirmOrRetryRespawn", flags).Invoke(rig.Coordinator, new[] { runtime, seat, (object)rig.Now });

            Assert.IsFalse(rig.Gateway.LastBinding(victim).Respawning, "fixture: the respawn did confirm");
            Assert.IsNull(rig.Gateway.LastBinding(victim).ProtectedUntilUtc, "but a match that is not Live opens no window");
            Assert.AreEqual(linesBefore, rig.Gateway.Messages.Count(m => m.Id == victim && m.Text.Contains("protected for")), "and sends no new protection line (the match-start one is already counted)");
        }

        /// <summary>A death inside the window drops it: the respawning binding carries none, and the next confirm opens a fresh one.</summary>
        [TestMethod]
        public void SpawnProtection_DeathClearsIt_TheNextRespawnOpensAFreshWindow()
        {
            var rig = LiveKoth(out var match);
            var victim = rig.Team(match, 0)[0];

            RespawnWithTrip(rig, victim, match, 0);
            Assert.IsNotNull(rig.Gateway.LastBinding(victim).ProtectedUntilUtc, "fixture: protected");

            Kill(rig, victim, match);
            Assert.IsNull(rig.Gateway.LastBinding(victim).ProtectedUntilUtc, "dead: no window");
            Assert.IsTrue(rig.Gateway.LastBinding(victim).Respawning);

            ArriveInPen(rig, victim);
            rig.Advance(rig.Bg.RespawnSeconds);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(rig.Now.AddSeconds(3), rig.Gateway.LastBinding(victim).ProtectedUntilUtc, "a second respawn is protected again");
        }

        /// <summary>
        /// The early-end intent drops the seat's window, proven through a republish that rebuilds the binding from the seat (match end):
        /// without the intent the Resolving binding still carries the window, with it the binding does not.
        /// </summary>
        [TestMethod]
        public void SpawnProtection_EndedIntent_DropsTheSeatsWindow()
        {
            PvpPlayerBinding Resolving(bool sendIntent)
            {
                var rig = LiveKoth(out var match);
                var victim = rig.Team(match, 0)[0];

                RespawnWithTrip(rig, victim, match, 0);

                if (sendIntent)
                    rig.Intents.Enqueue(PvpMatchManager.SpawnProtectionEnded(victim, match.MatchId, rig.Now));

                match.ScoreBoard[1] = rig.Bg.KothScoreTarget;
                rig.Tick();
                rig.Tick();

                return rig.Gateway.LastBinding(victim);
            }

            Assert.IsNotNull(Resolving(false).ProtectedUntilUtc, "control: with no intent the rebuilt binding still carries the window");
            Assert.IsNull(Resolving(true).ProtectedUntilUtc, "the intent cleared the seat's window");
        }

        /// <summary>The scope ruling: every shipped arena and battleground mode is templated, and a new mode is templated unless it opts out.</summary>
        [TestMethod]
        public void Modes_AreTemplatedByDefault_KothIncluded()
        {
            Assert.IsTrue(BattlegroundModes.Koth(BattlegroundTunables.Defaults).Templated);
            Assert.IsTrue(PvpModes.All(PvpTunables.Defaults).All(m => m.Templated));
            CollectionAssert.Contains(PvpTemplateCatalog.ModeKeys.ToList(), BattlegroundModes.KothModeKey);
        }
    }
}