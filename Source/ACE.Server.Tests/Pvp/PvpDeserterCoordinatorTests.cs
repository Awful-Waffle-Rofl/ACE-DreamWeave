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
    /// Deserter lockout through the coordinator (Docs/Pvp/BATTLEGROUNDS.md "Deserter lockout"): which exits strike, the immediate in-memory
    /// lockout, the stored-record join check, and the scope (battlegrounds and Tugak Brawl only). The fakes implement the battleground halves
    /// of both seams (so the bg room opens) and the conduct seam (so every persisted record is recorded).
    /// </summary>
    [TestClass]
    public class PvpDeserterCoordinatorTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private const uint A = 0x50000071, B = 0x50000072, C = 0x50000073, D = 0x50000074, E = 0x50000075;

        private static readonly DateTime T0 = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        private const string TemplateKey = "duelist";

        private const string Struck15 = "[Arena] You left the match early. You cannot queue for battlegrounds or Tugak Brawl for 15 minutes.";

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

        private class FakeGateway : IPvpPlayerGateway, IBattlegroundPlayerGateway
        {
            public readonly Dictionary<uint, FakePlayer> Players = new();
            public readonly List<(uint Id, string Text)> Messages = new();
            public readonly List<(uint Id, PvpDeserterRecord Record)> Persisted = new();

            public FakePlayer Add(uint id, string name)
            {
                var p = new FakePlayer
                {
                    Facts = new PvpPlayerFacts(id, name, $"10.0.0.{id & 0xFF}", false, false, false, false, false, false, false, Array.Empty<uint>(), PreferredTemplateKey: TemplateKey, HasClearedThread: true),
                };

                Players[id] = p;
                return p;
            }

            public bool Dying(uint id) => Players.TryGetValue(id, out var p) && p.Dying;

            public PvpPlayerFacts GetFacts(uint id) => Players.TryGetValue(id, out var p) && p.Online ? p.Facts : null;

            public bool IsOnline(uint id) => Players.TryGetValue(id, out var p) && p.Online;

            public Position GetCurrentPosition(uint id) => IsOnline(id) ? new Position(Players[id].Location) : null;

            public PvpPresence GetPresence(uint id, uint matchInstance)
            {
                if (!IsOnline(id))
                    return PvpPresence.Offline;

                return Players[id].Instance == matchInstance ? PvpPresence.InInstance : PvpPresence.Elsewhere;
            }

            public void EnterAndTeleport(uint id, PvpPlayerBinding binding, Position exitTo, Position spawn) => Players[id].Instance = spawn.Instance;

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

            // ---- battleground half ----

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
                if (!Players.TryGetValue(id, out var p) || !p.Online || p.Dying)
                    return;

                p.Instance = spawn.Instance;
                p.At = (spawn.PositionX, spawn.PositionY, spawn.PositionZ);
            }

            public bool IsInDeathProcess(uint id) => Dying(id);

            public void ExitMatchFromPen(uint id, string context) { }

            /// <summary>Filled only by the conduct-seam subclass; stays empty for a gateway without the seam.</summary>

            public bool Got(uint id, string text) => Messages.Any(m => m.Id == id && m.Text == text);

            public int Count(uint id, string text) => Messages.Count(m => m.Id == id && m.Text == text);
        }

        /// <summary>The gateway with the conduct seam, as the live gateway has it: every persisted record is recorded.</summary>
        private sealed class FakeConductGateway : FakeGateway, IPvpConductGateway
        {
            public void PersistDeserterRecord(uint id, PvpDeserterRecord record) => Persisted.Add((id, record));
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

        private sealed class FakeSink : IPvpResultSink
        {
            public void LoadRatings(Action<List<PvpRatingRecord>> callback) => callback(new List<PvpRatingRecord>());

            public void SaveMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, Action<PvpMatchSaveResult, uint> callback) =>
                callback(PvpMatchSaveResult.Saved, 1);

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
            /// <summary>With the conduct seam unless a test swaps in a plain <see cref="FakeGateway"/> before Build.</summary>
            public FakeGateway Gateway = new FakeConductGateway();

            /// <summary>False keeps the coordinator's own ServerShuttingDown seam (ServerManager.ShutdownInProgress).</summary>
            public bool StubShutdown = true;
            public readonly FakeSpaces Spaces = new();
            public readonly ConcurrentQueue<PvpIntent> Intents = new();
            public PvpArenaDials Dials = PvpTunables.Defaults with { CrierEnabled = false, OvertimeEnabled = false, FfaTargetPlayers = 5, FfaMinPlayers = 5 };
            public BattlegroundDials Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0 };
            public PvpConductDials Conduct = PvpConductTunables.Defaults with { FloorEnabled = false };
            public PvpMatchCoordinator Coordinator;
            public readonly List<string> Audit = new();

            public Rig Build(params (uint Id, string Name)[] players)
            {
                foreach (var (id, name) in players)
                    Gateway.Add(id, name);

                Coordinator = new PvpMatchCoordinator(Gateway, Spaces, new FakeSink(), () => Now, () => Dials, Intents, new NoCrier(), () => Bg, conductDialSource: () => Conduct, auditSink: Audit.Add);
                if (StubShutdown)
                    Coordinator.ServerShuttingDown = () => false;
                Coordinator.BeginLoadRatings();
                Coordinator.BeginLoadTemplates();
                Coordinator.BeginLoadLatestTemplates();
                Coordinator.Tick();
                return this;
            }

            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

            public void Tick() => Coordinator.Tick();

            public PvpMatch MatchOf(uint id) => Coordinator.FindMatch(id);

            /// <summary>Joins every id to <paramref name="room"/>, forms, accepts and drives the match to Live.</summary>
            public PvpMatch Live(string room, params uint[] ids)
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
                Advance(Dials.CountdownSeconds);
                Tick();
                Assert.AreEqual(PvpMatchState.Live, MatchOf(ids[0]).State, "fixture: Live");
                return MatchOf(ids[0]);
            }

            /// <summary>Ends a match by the admin path's sibling: ticks the post-match window away after it resolved.</summary>
            public void PostMatch()
            {
                Advance(Dials.PostMatchSeconds);
                Tick();
            }

            public void RunActive(PvpMatch match, int seconds, params uint[] active)
            {
                for (var i = 0; i < seconds; i++)
                {
                    Advance(1);

                    foreach (var id in active)
                        Gateway.Players[id].Activity = new PvpActivityStamp(match.MatchId, Now);

                    Tick();
                }
            }
        }

        private static PvpParticipant Participant(PvpMatch match, uint id) => match.Teams.SelectMany(t => t.Members).Single(p => p.CharacterId == id);

        private static long Unix(DateTime t) => PvpDeserterRules.ToUnix(t);

        private static Rig TugakLive(out PvpMatch match, Action<Rig> configure = null)
        {
            var rig = new Rig();
            configure?.Invoke(rig);
            rig.Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"), (E, "Echo"));
            match = rig.Live("ffa", A, B, C, D, E);
            return rig;
        }

        /// <summary>Kills every seat but <paramref name="survivor"/> still in, so the Tugak match resolves, then closes it.</summary>
        private static void FinishTugak(Rig rig, PvpMatch match, uint survivor)
        {
            foreach (var p in match.Teams.SelectMany(t => t.Members).Where(p => p.CharacterId != survivor && !p.ExitReason.HasValue).ToList())
                rig.Intents.Enqueue(PvpMatchManager.Death(p.CharacterId, match.MatchId, rig.Now, survivor));

            rig.Tick();
            rig.PostMatch();
            Assert.AreEqual(PvpMatchState.Closed, match.State, "fixture: the match closed");
        }

        // ================= which exits strike =================

        /// <summary>
        /// A battleground idle removal (ForfeitInactive at 90 s, the shipped dials) is a strike: strike 1, locked out for lockout_1 (15
        /// minutes) from the removal, the record handed to the conduct seam, and the player told. The three who stayed active are untouched.
        /// </summary>
        [TestMethod]
        public void Battleground_IdleRemoval_StrikesAndLocksOut()
        {
            var rig = new Rig();
            rig.Conduct = PvpConductTunables.Defaults with { FloorEnabled = false };
            rig.Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"));
            var match = rig.Live("bg", A, B, C, D);
            Assert.AreEqual(BattlegroundModes.KothModeKey, match.ModeKey, "fixture: a battleground mode");

            var zone = BattlegroundMapCatalog.Bg016c;
            foreach (var id in new[] { A, B, C, D })
                rig.Gateway.Players[id].At = (zone.ZoneX + 150, zone.ZoneY + 150, zone.ZoneZ);

            var idler = match.Teams[0].Members[0].CharacterId;
            var active = new[] { A, B, C, D }.Where(id => id != idler).ToArray();

            rig.RunActive(match, 89, active);
            Assert.IsNull(Participant(match, idler).ExitReason, "fixture: still in at 89 s");
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(idler), "no strike before the removal");

            rig.RunActive(match, 1, active);

            Assert.AreEqual(ParticipantExit.ForfeitInactive, Participant(match, idler).ExitReason, "fixture: removed for idling");

            var expected = new PvpDeserterRecord(1, Unix(rig.Now), Unix(rig.Now) + 15 * 60);
            Assert.AreEqual(expected, rig.Coordinator.DeserterRecordForTest(idler));
            CollectionAssert.AreEqual(new[] { (idler, expected) }, rig.Gateway.Persisted, "stored once, as the same absolute record");
            Assert.AreEqual(1, rig.Gateway.Count(idler, Struck15), "told once");
            Assert.IsTrue(active.All(id => rig.Coordinator.DeserterRecordForTest(id) == null), "the active seats are never struck");
        }

        /// <summary>A voluntary /arena leave in a Live Tugak Brawl match is a strike, and a second exit of the same seat strikes nothing more.</summary>
        [TestMethod]
        public void Tugak_VoluntaryLeave_Strikes()
        {
            var rig = TugakLive(out var match);

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(A).Outcome);

            Assert.AreEqual(new PvpDeserterRecord(1, Unix(rig.Now), Unix(rig.Now) + 900), rig.Coordinator.DeserterRecordForTest(A));
            Assert.AreEqual(1, rig.Gateway.Persisted.Count);
            Assert.IsTrue(rig.Gateway.Got(A, Struck15));

            Assert.AreEqual(PvpForfeitOutcome.AlreadyOut, rig.Coordinator.Forfeit(A).Outcome);
            rig.Tick();
            Assert.AreEqual(1, rig.Gateway.Persisted.Count, "one strike per departure");
        }

        /// <summary>A Tugak Brawl walk-out (seen in the instance, then elsewhere) is a strike, like /arena leave.</summary>
        [TestMethod]
        public void Tugak_WalkOut_Strikes()
        {
            var rig = TugakLive(out var match);

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLeft, Participant(match, B).ExitReason, "fixture: a walk-out");
            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(B)?.Strikes);
        }

        /// <summary>
        /// A logout in a Live Tugak Brawl match strikes, but the player is offline: nothing is said (the refusal explains it later) and the
        /// record still goes to the conduct seam, which stores it on the offline character.
        /// </summary>
        [TestMethod]
        public void Tugak_Logout_Strikes_Silently_AndStillPersists()
        {
            var rig = TugakLive(out var match);

            rig.Gateway.Players[C].Online = false;
            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(C, match.MatchId, rig.Now));
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, C).ExitReason, "fixture: a logout forfeit");
            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(C)?.Strikes);
            Assert.AreEqual(C, rig.Gateway.Persisted.Single().Id);
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Id == C && m.Text.StartsWith("[Arena] You left the match early.")), "offline: no strike line");
        }

        /// <summary>The same leave in a Live 1v1 does NOT strike: 1v1 and 2v2 are out of scope.</summary>
        [TestMethod]
        public void OneVOne_Leave_DoesNotStrike()
        {
            var rig = new Rig().Build((A, "Alpha"), (B, "Bravo"));
            rig.Live("1v1", A, B);

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(A).Outcome, "fixture: a real Live forfeit");

            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(A));
            Assert.AreEqual(0, rig.Gateway.Persisted.Count);
            Assert.IsFalse(rig.Gateway.Got(A, Struck15));
        }

        /// <summary>
        /// An admin cancel of a Live battleground exits everyone without a strike, and every one of them may queue again at once.
        /// </summary>
        [TestMethod]
        public void Battleground_AdminCancel_DoesNotStrike()
        {
            var rig = new Rig().Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"));
            var match = rig.Live("bg", A, B, C, D);

            Assert.AreEqual(PvpCancelOutcome.Canceled, rig.Coordinator.Cancel(match.MatchId).Outcome);
            rig.Tick();

            Assert.AreEqual(0, rig.Gateway.Persisted.Count, "no strike stored");
            Assert.IsTrue(new[] { A, B, C, D }.All(id => rig.Coordinator.DeserterRecordForTest(id) == null), "no strike held");
            Assert.IsTrue(rig.Coordinator.Join(A, "bg", false).Joined, "free to queue again");
        }

        /// <summary>
        /// A battleground that never went Live: a walk-out during Countdown is the existing decline-style dodge, never a deserter strike.
        /// </summary>
        [TestMethod]
        public void Battleground_WalkOutBeforeLive_DoesNotStrike()
        {
            var rig = new Rig().Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"));

            foreach (var id in new[] { A, B, C, D })
                Assert.IsTrue(rig.Coordinator.Join(id, "bg", false).Joined);

            rig.Tick();

            foreach (var id in new[] { A, B, C, D })
                rig.Coordinator.Accept(id);

            rig.Tick();
            rig.Tick();
            rig.Tick();
            var match = rig.MatchOf(A);
            Assert.AreEqual(PvpMatchState.Countdown, match.State, "fixture: Countdown");

            rig.Gateway.Players[A].Instance = 0;
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLeft, Participant(match, A).ExitReason, "fixture: a walk-out before Live");
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(A));
            Assert.AreEqual(0, rig.Gateway.Persisted.Count);
        }

        /// <summary>
        /// The template backstop ejecting a Live Tugak seat (personal items it could not move: a server gate miss) is recorded as a forfeit
        /// like /arena leave, but it is the server's doing: no strike.
        /// </summary>
        [TestMethod]
        public void Tugak_TemplateBackstopEject_DoesNotStrike()
        {
            var rig = TugakLive(out var match);

            rig.Intents.Enqueue(PvpMatchManager.BackstopFired(E, match.MatchId, rig.Now, 1, 1));
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitCommand, Participant(match, E).ExitReason, "fixture: ejected as a forfeit");
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(E));
            Assert.AreEqual(0, rig.Gateway.Persisted.Count);
        }

        /// <summary>A server shutdown logs everyone off: those logout forfeits are not desertions.</summary>
        [TestMethod]
        public void Tugak_LogoutDuringShutdown_DoesNotStrike()
        {
            var rig = TugakLive(out var match);
            rig.Coordinator.ServerShuttingDown = () => true;

            rig.Gateway.Players[D].Online = false;
            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(D, match.MatchId, rig.Now));
            rig.Tick();

            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, D).ExitReason, "fixture: a logout forfeit");
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(D));
            Assert.AreEqual(0, rig.Gateway.Persisted.Count);
        }

        /// <summary>pvp_conduct_deserter_enabled off: a Tugak leave strikes nothing and stores nothing.</summary>
        [TestMethod]
        public void Tugak_Leave_WithTheSwitchOff_DoesNotStrike()
        {
            var rig = TugakLive(out _, r => r.Conduct = r.Conduct with { DeserterEnabled = false });

            rig.Coordinator.Forfeit(A);

            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(A));
            Assert.AreEqual(0, rig.Gateway.Persisted.Count);
        }

        // ================= the join refusal =================

        /// <summary>
        /// From the in-memory lockout: once the match is over, the deserter cannot queue for Tugak or battlegrounds (15 minutes left, said in
        /// whole minutes), but 1v1 is out of scope and takes them.
        /// </summary>
        [TestMethod]
        public void Join_RefusedFromTheInMemoryLockout_TugakAndBg_Not1v1()
        {
            var rig = TugakLive(out var match);
            var struckAt = rig.Now;
            rig.Coordinator.Forfeit(A);
            FinishTugak(rig, match, B);

            var tugak = rig.Coordinator.Join(A, "ffa", false);
            Assert.AreEqual(PvpJoinRefusal.DeserterLockout, tugak.Refusal);
            Assert.AreEqual(900 - (int)(rig.Now - struckAt).TotalSeconds, tugak.LockoutSecondsRemaining, "the time left from the strike");
            Assert.IsTrue(rig.Now > struckAt, "fixture: time has passed since the strike");
            Assert.AreEqual("[Arena] You left a recent match early. You can queue for battlegrounds or Tugak Brawl again in 15 minutes.", PvpMatchCoordinator.RefusalText(tugak));

            Assert.AreEqual(PvpJoinRefusal.DeserterLockout, rig.Coordinator.Join(A, "bg", false).Refusal);
            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined, "1v1 is never refused");
            Assert.IsTrue(rig.Coordinator.Join(B, "ffa", false).Joined, "the survivor is free");
        }

        /// <summary>
        /// From the stored record alone (a fresh coordinator, as after a restart): a future lockout on the facts refuses battlegrounds; an
        /// expired one does not.
        /// </summary>
        [TestMethod]
        public void Join_RefusedFromTheStoredFacts_AndNotOnceExpired()
        {
            var rig = new Rig().Build((A, "Alpha"), (B, "Bravo"));
            var now = Unix(rig.Now);
            rig.Gateway.Players[A].Facts = rig.Gateway.Players[A].Facts with { DeserterStrikes = 2, DeserterLastStrikeAt = now - 300, DeserterLockoutUntil = now + 600 };
            rig.Gateway.Players[B].Facts = rig.Gateway.Players[B].Facts with { DeserterStrikes = 2, DeserterLastStrikeAt = now - 4000, DeserterLockoutUntil = now - 400 };

            var refused = rig.Coordinator.Join(A, "bg", false);

            Assert.AreEqual(PvpJoinRefusal.DeserterLockout, refused.Refusal);
            Assert.AreEqual(600, refused.LockoutSecondsRemaining);
            Assert.AreEqual("[Arena] You left a recent match early. You can queue for battlegrounds or Tugak Brawl again in 10 minutes.", PvpMatchCoordinator.RefusalText(refused));
            Assert.IsTrue(rig.Coordinator.Join(B, "bg", false).Joined, "an expired stored lockout lets them in");
        }

        /// <summary>The lockout expires: refused one second before lockout_1 runs out, admitted at the second it does.</summary>
        [TestMethod]
        public void Lockout_Expires()
        {
            var rig = TugakLive(out var match);
            rig.Coordinator.Forfeit(A);
            var until = rig.Coordinator.DeserterRecordForTest(A).LockoutUntil;
            FinishTugak(rig, match, B);

            rig.Now = DateTimeOffset.FromUnixTimeSeconds(until - 1).UtcDateTime;
            var last = rig.Coordinator.Join(A, "ffa", false);
            Assert.AreEqual(PvpJoinRefusal.DeserterLockout, last.Refusal);
            Assert.AreEqual(1, last.LockoutSecondsRemaining);
            Assert.AreEqual("[Arena] You left a recent match early. You can queue for battlegrounds or Tugak Brawl again in 1 second.", PvpMatchCoordinator.RefusalText(last));

            rig.Now = DateTimeOffset.FromUnixTimeSeconds(until).UtcDateTime;
            Assert.IsTrue(rig.Coordinator.Join(A, "ffa", false).Joined);
        }

        // ================= server- and staff-caused logouts =================

        private static void LogoutIntent(Rig rig, PvpMatch match, uint id, bool serverCaused)
        {
            rig.Gateway.Players[id].Online = false;
            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(id, match.MatchId, rig.Now, serverCaused));
            rig.Tick();
            Assert.AreEqual(ParticipantExit.ForfeitLogout, Participant(match, id).ExitReason, "fixture: a logout forfeit");
        }

        /// <summary>A logout the server or staff forced (ServerCaused on the intent) is a forfeit, but no strike and nothing stored.</summary>
        [TestMethod]
        public void Tugak_ServerCausedLogout_DoesNotStrike()
        {
            var rig = TugakLive(out var match);

            LogoutIntent(rig, match, C, serverCaused: true);

            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(C));
            Assert.AreEqual(0, rig.Gateway.Persisted.Count);
        }

        /// <summary>
        /// The termination reason decides the flag the player side puts on the intent: a boot by staff (ForcedLogOffRequested) does not strike,
        /// a second login on the same account (AccountLoggedIn, the player's own act) does.
        /// </summary>
        [DataTestMethod]
        [DataRow(ACE.Server.Network.Enum.SessionTerminationReason.ForcedLogOffRequested, false)]
        [DataRow(ACE.Server.Network.Enum.SessionTerminationReason.AccountBooted, false)]
        [DataRow(ACE.Server.Network.Enum.SessionTerminationReason.AccountLoggedIn, true)]
        [DataRow(ACE.Server.Network.Enum.SessionTerminationReason.NetworkTimeout, true)]
        public void Tugak_LogoutByTerminationReason_StrikesOnlyWhenThePlayersOwn(ACE.Server.Network.Enum.SessionTerminationReason reason, bool strikes)
        {
            var rig = TugakLive(out var match);

            LogoutIntent(rig, match, C, PvpDeserterRules.IsServerCausedTermination(reason));

            Assert.AreEqual(strikes, rig.Coordinator.DeserterRecordForTest(C) != null, reason.ToString());
            Assert.AreEqual(strikes ? 1 : 0, rig.Gateway.Persisted.Count);
        }

        // ================= shutdown: ShutdownInProgress only =================

        /// <summary>
        /// The coordinator's own seam reads ShutdownInProgress only: during an announced countdown (ShutdownInitiated set, not yet in
        /// progress) a /arena leave still strikes. ServerManager's flag is set by reflection and restored in a finally.
        /// </summary>
        [TestMethod]
        public void ShutdownCountdown_AnnouncedOnly_ALeaveStillStrikes()
        {
            var prop = typeof(ServerManager).GetProperty(nameof(ServerManager.ShutdownInitiated));
            var was = ServerManager.ShutdownInitiated;
            var inProgress = ServerManager.ShutdownInProgress;

            try
            {
                prop.SetValue(null, true);
                Assert.IsTrue(ServerManager.ShutdownInitiated, "fixture: a countdown is announced");
                Assert.IsFalse(inProgress, "fixture: not in the final shutdown");

                var rig = TugakLive(out _, r => r.StubShutdown = false);

                rig.Coordinator.Forfeit(A);

                Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(A)?.Strikes, "a leave during the countdown strikes");
            }
            finally
            {
                prop.SetValue(null, was);
            }
        }

        // ================= one strike per departure =================

        /// <summary>/arena leave, then the logout intent of the same player: the seat is already out, so one strike and one stored record.</summary>
        [TestMethod]
        public void Tugak_LeaveThenLogout_StrikesOnce()
        {
            var rig = TugakLive(out var match);

            rig.Coordinator.Forfeit(A);
            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(A, match.MatchId, rig.Now));
            rig.Tick();

            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(A).Strikes);
            Assert.AreEqual(1, rig.Gateway.Persisted.Count(p => p.Id == A));
        }

        /// <summary>An idle removal, then the removed player's logout: still one strike.</summary>
        [TestMethod]
        public void Battleground_IdleRemovalThenLogout_StrikesOnce()
        {
            var rig = new Rig().Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"));
            var match = rig.Live("bg", A, B, C, D);
            OffTheHill(rig, match);
            var idler = match.Teams[0].Members[0].CharacterId;
            var active = new[] { A, B, C, D }.Where(id => id != idler).ToArray();

            rig.RunActive(match, 90, active);
            Assert.AreEqual(ParticipantExit.ForfeitInactive, Participant(match, idler).ExitReason, "fixture: removed for idling");

            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(idler, match.MatchId, rig.Now));
            rig.Tick();

            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(idler).Strikes);
            Assert.AreEqual(1, rig.Gateway.Persisted.Count(p => p.Id == idler));
        }

        /// <summary>Eliminated in Tugak, then logged out: the death took the seat out, so no strike at all.</summary>
        [TestMethod]
        public void Tugak_DiedThenLogout_DoesNotStrike()
        {
            var rig = TugakLive(out var match);

            rig.Intents.Enqueue(PvpMatchManager.Death(E, match.MatchId, rig.Now, A));
            rig.Tick();
            Assert.AreEqual(ParticipantExit.Died, Participant(match, E).ExitReason, "fixture: eliminated");

            rig.Gateway.Players[E].Online = false;
            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(E, match.MatchId, rig.Now));
            rig.Tick();

            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(E));
            Assert.AreEqual(0, rig.Gateway.Persisted.Count);
        }

        /// <summary>A battleground feeding removal (4 deaths with no contribution while a teammate carries) is a strike.</summary>
        [TestMethod]
        public void Battleground_FeedingRemoval_Strikes()
        {
            var rig = new Rig();
            rig.Conduct = PvpConductTunables.Defaults with { FloorEnabled = false, IdleEnabled = false, EngageRadius = 0 };
            rig.Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"));
            var match = rig.Live("bg", A, B, C, D);
            OffTheHill(rig, match);

            var west = match.Teams[0].Members.Select(p => p.CharacterId).ToArray();
            var east = match.Teams[1].Members.Select(p => p.CharacterId).ToArray();
            var feeder = west[0];

            match.Performance.CreditKillshot(west[1], null, false);
            match.Performance.CreditPlayerDamage(west[1], 400, null);

            for (var death = 1; death <= 4 && !Participant(match, feeder).ExitReason.HasValue; death++)
            {
                rig.Gateway.Players[feeder].Dying = true;
                rig.Intents.Enqueue(PvpMatchManager.Death(feeder, match.MatchId, rig.Now, east[0]));
                rig.Tick();

                if (Participant(match, feeder).ExitReason.HasValue)
                    break;

                Assert.IsNull(rig.Coordinator.DeserterRecordForTest(feeder), $"death {death}: no strike for a death");

                rig.Gateway.Players[feeder].Dying = false;
                rig.RunActive(match, rig.Bg.RespawnSeconds + 10);
            }

            Assert.AreEqual(ParticipantExit.ForfeitFeeding, Participant(match, feeder).ExitReason, "fixture: removed for feeding");
            Assert.AreEqual(1, rig.Coordinator.DeserterRecordForTest(feeder)?.Strikes);
            Assert.AreEqual(1, rig.Gateway.Persisted.Count(p => p.Id == feeder));
        }

        // ================= pruning =================

        /// <summary>
        /// The tick drops a record once its lockout is over, its strikes have fully decayed (one 7-day period for one strike) and it is at
        /// least an hour old; a day later it is still held (strike not decayed).
        /// </summary>
        [TestMethod]
        public void Prune_DropsASpentRecord_KeepsALiveOne()
        {
            var rig = TugakLive(out _);
            rig.Coordinator.Forfeit(A);

            rig.Advance(86400);
            rig.Tick();
            Assert.IsNotNull(rig.Coordinator.DeserterRecordForTest(A), "a day later the strike has not decayed: kept");

            rig.Advance(6 * 86400 + 1);
            rig.Tick();
            Assert.IsNull(rig.Coordinator.DeserterRecordForTest(A), "fully decayed and long expired: pruned");
        }

        /// <summary>Clear with a gateway that has NO conduct seam: cleared in memory, reported as not stored, and the audit line says so.</summary>
        [TestMethod]
        public void Clear_WithoutTheConductSeam_IsInMemoryOnly()
        {
            var rig = new Rig { Gateway = new FakeGateway() };
            rig.Build((A, "Alpha"));

            var result = rig.Coordinator.ClearDeserter(A, "Alpha", "Admin");

            Assert.IsFalse(result.Stored);
            StringAssert.EndsWith(rig.Audit.Single(), " (in memory only: nothing was stored)");
            Assert.AreEqual(result.Record, rig.Coordinator.DeserterRecordForTest(A));
        }

        [TestMethod]
        public void Clear_WithTheConductSeam_IsStored()
        {
            var rig = new Rig().Build((A, "Alpha"));

            var result = rig.Coordinator.ClearDeserter(A, "Alpha", "Admin");

            Assert.IsTrue(result.Stored);
            Assert.IsFalse(rig.Audit.Single().Contains("in memory only"));
        }

        private static void OffTheHill(Rig rig, PvpMatch match)
        {
            var zone = BattlegroundMapCatalog.Bg016c;

            foreach (var id in match.Teams.SelectMany(t => t.Members).Select(p => p.CharacterId))
                rig.Gateway.Players[id].At = (zone.ZoneX + 150, zone.ZoneY + 150, zone.ZoneZ);
        }

        // ================= /arenaadmin deserter clear =================

        /// <summary>
        /// Clear for an online player locked in memory: the zeroed record (stamped now) is held at once and stored through the conduct seam,
        /// one audit line names the admin and the old values, the status reads clean, and the player may queue for Tugak again.
        /// </summary>
        [TestMethod]
        public void Clear_Online_InMemoryLockout_LiftsItAndStoresZeros()
        {
            var rig = TugakLive(out var match);
            rig.Coordinator.Forfeit(A);
            var struck = rig.Coordinator.DeserterRecordForTest(A);
            FinishTugak(rig, match, B);
            Assert.AreEqual(PvpJoinRefusal.DeserterLockout, rig.Coordinator.Join(A, "ffa", false).Refusal, "fixture: locked");

            var cleared = rig.Coordinator.ClearDeserter(A, "Alpha", "Admin").Record;

            Assert.AreEqual(new PvpDeserterRecord(0, Unix(rig.Now), 0), cleared);
            Assert.AreEqual((A, cleared), rig.Gateway.Persisted.Last(), "stored through the same seam as a strike");
            CollectionAssert.AreEqual(new[] { $"[PVP-AUDIT] Admin cleared the deserter record of Alpha (0x{A:X8}); was strikes 1, locked until {struck.LockoutUntil}" }, rig.Audit);

            var status = rig.Coordinator.DeserterStatus(A, null);
            Assert.AreEqual(0, status.CurrentStrikes);
            Assert.AreEqual(0, status.SecondsRemaining);
            Assert.IsTrue(rig.Coordinator.Join(A, "ffa", false).Joined, "free to queue");
        }

        /// <summary>
        /// Clear for an OFFLINE player whose lockout exists only in storage (a fresh coordinator, as after a restart): the clear is stored
        /// through the seam while they are offline, and when they come back still carrying the stale stored values (the write not yet landed)
        /// the newer in-memory clear wins, so they may queue. Without the clear the same facts are refused (the control first).
        /// </summary>
        [TestMethod]
        public void Clear_Offline_StoredLockout_IsStored_AndWinsOverTheStaleStoredRecord()
        {
            var rig = new Rig().Build((A, "Alpha"));
            var now = Unix(rig.Now);
            var stale = new PvpDeserterRecord(2, now - 300, now + 3300);
            rig.Gateway.Players[A].Facts = rig.Gateway.Players[A].Facts with { DeserterStrikes = 2, DeserterLastStrikeAt = stale.LastStrikeAt, DeserterLockoutUntil = stale.LockoutUntil };

            var before = rig.Coordinator.DeserterStatus(A, stale);
            Assert.AreEqual(3300, before.SecondsRemaining, "fixture: the stored lockout is seen");

            rig.Gateway.Players[A].Online = false;
            rig.Advance(5);

            var cleared = rig.Coordinator.ClearDeserter(A, "Alpha", "Admin", stale).Record;

            CollectionAssert.AreEqual(new[] { (A, cleared) }, rig.Gateway.Persisted, "stored while offline");
            Assert.AreEqual(new PvpDeserterRecord(0, now + 5, 0), cleared);
            StringAssert.EndsWith(rig.Audit.Single(), $"was strikes 2, locked until {stale.LockoutUntil}");
            Assert.AreEqual(0, rig.Coordinator.DeserterStatus(A, stale).SecondsRemaining, "the clear wins over the stale stored record");

            rig.Gateway.Players[A].Online = true;
            Assert.IsTrue(rig.Coordinator.Join(A, "bg", false).Joined, "back online with stale stored values: still cleared");
        }

        /// <summary>Control for the test above: the same stale stored facts with no clear are refused.</summary>
        [TestMethod]
        public void Clear_Control_WithoutAClear_TheStoredLockoutRefuses()
        {
            var rig = new Rig().Build((A, "Alpha"));
            var now = Unix(rig.Now);
            rig.Gateway.Players[A].Facts = rig.Gateway.Players[A].Facts with { DeserterStrikes = 2, DeserterLastStrikeAt = now - 300, DeserterLockoutUntil = now + 3300 };

            var r = rig.Coordinator.Join(A, "bg", false);

            Assert.AreEqual(PvpJoinRefusal.DeserterLockout, r.Refusal);
            Assert.AreEqual("[Arena] You left a recent match early. You can queue for battlegrounds or Tugak Brawl again in 55 minutes.", PvpMatchCoordinator.RefusalText(r));
        }

        /// <summary>
        /// The ladder reads the stored record seeded at join: one strike an hour ago (not yet decayed), lockout over, so this leave is strike 2
        /// and locks out for lockout_2 (60 minutes).
        /// </summary>
        [TestMethod]
        public void SecondStrike_FromTheStoredRecord_EscalatesToLockout2()
        {
            var rig = new Rig();
            rig.Build((A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta"), (E, "Echo"));
            var now = Unix(rig.Now);
            rig.Gateway.Players[A].Facts = rig.Gateway.Players[A].Facts with { DeserterStrikes = 1, DeserterLastStrikeAt = now - 3600, DeserterLockoutUntil = now - 2700 };

            rig.Live("ffa", A, B, C, D, E);
            rig.Coordinator.Forfeit(A);

            var record = rig.Coordinator.DeserterRecordForTest(A);
            Assert.AreEqual(2, record.Strikes);
            Assert.AreEqual(60 * 60, record.LockoutUntil - record.LastStrikeAt);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] You left the match early. You cannot queue for battlegrounds or Tugak Brawl for 1 hour."));
        }
    }
}
