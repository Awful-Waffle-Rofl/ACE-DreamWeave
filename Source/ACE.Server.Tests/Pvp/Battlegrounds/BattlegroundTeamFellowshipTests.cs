using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
    /// Battleground team fellowships (Docs/Pvp/BATTLEGROUNDS.md "Team fellowships"): the pure rules (plan, leader, restore,
    /// gate), the coordinator's calls into IPvpTeamFellowshipGateway through a recording fake (Form once at Countdown,
    /// Release on every exit, Dispose once, the sweep), and source scans of every player-side gate site. Fellowship has no
    /// unit-testable constructor (PropertyManager reads throw under the test host), so the live gateway's decisions are the
    /// pure functions tested here.
    /// </summary>
    [TestClass]
    public class BattlegroundTeamFellowshipTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private const uint A = 0x50000021, B = 0x50000022, C = 0x50000023, D = 0x50000024;

        private static readonly DateTime T0 = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

        private const string TemplateKey = "duelist";

        // ======================================================================================
        // pure rules
        // ======================================================================================

        [TestMethod]
        public void Plan_Decide_Table()
        {
            Assert.AreEqual(TeamFellowshipPlanDecision.Form, TeamFellowshipPlan.Decide(TeamFellowshipPolicy.TeamFellowship, true, 6, 20));
            Assert.AreEqual(TeamFellowshipPlanDecision.Form, TeamFellowshipPlan.Decide(TeamFellowshipPolicy.TeamFellowship, true, 6, 6), "a team exactly at the cap fits");
            Assert.AreEqual(TeamFellowshipPlanDecision.SkipOverCap, TeamFellowshipPlan.Decide(TeamFellowshipPolicy.TeamFellowship, true, 7, 6), "above the cap skips");
            Assert.AreEqual(TeamFellowshipPlanDecision.SkipDisabled, TeamFellowshipPlan.Decide(TeamFellowshipPolicy.TeamFellowship, false, 2, 20), "the off switch");
            Assert.AreEqual(TeamFellowshipPlanDecision.SkipPolicy, TeamFellowshipPlan.Decide(TeamFellowshipPolicy.PremadeAllowed, true, 2, 20), "2v2's policy");
            Assert.AreEqual(TeamFellowshipPlanDecision.SkipPolicy, TeamFellowshipPlan.Decide(TeamFellowshipPolicy.Ignored, true, 1, 20), "1v1 and FFA's policy");
        }

        [TestMethod]
        public void Modes_OnlyKothBuildsTeamFellowships()
        {
            Assert.AreEqual(TeamFellowshipPolicy.TeamFellowship, BattlegroundModes.Koth(BattlegroundTunables.Defaults).Fellowship);

            foreach (var mode in PvpModes.All(PvpTunables.Defaults))
                Assert.AreNotEqual(TeamFellowshipPolicy.TeamFellowship, mode.Fellowship, $"{mode.ModeKey} must never build team fellowships");
        }

        [TestMethod]
        public void Plan_ChooseLeader_FirstEligibleInTeamOrder()
        {
            Assert.AreEqual(B, TeamFellowshipPlan.ChooseLeader(new[] { B, A, C }, _ => true), "the first in team order, not the lowest id");
            Assert.AreEqual(A, TeamFellowshipPlan.ChooseLeader(new[] { B, A, C }, id => id != B), "an ineligible first member is passed over");
            Assert.AreEqual(0u, TeamFellowshipPlan.ChooseLeader(new[] { B, A }, _ => false), "nobody eligible");
            Assert.AreEqual(0u, TeamFellowshipPlan.ChooseLeader(Array.Empty<uint>(), _ => true));
        }

        [TestMethod]
        public void Restore_Classify_PremadeOnlyWhenEveryLiveMemberIsInTheMatch()
        {
            var match = new HashSet<uint> { A, B, C, D };

            Assert.AreEqual(TeamFellowshipRecordKind.Premade, TeamFellowshipRestore.Classify(new[] { A, B }, match));
            Assert.AreEqual(TeamFellowshipRecordKind.Outside, TeamFellowshipRestore.Classify(new[] { A, 0x50000099u }, match));
            Assert.AreEqual(TeamFellowshipRecordKind.None, TeamFellowshipRestore.Classify(Array.Empty<uint>(), match));
        }

        private static TeamFellowshipRestoreFacts Outside(bool alive = true, bool locked = false, bool full = false, bool leech = false) =>
            new TeamFellowshipRestoreFacts(true, true, false, false, false, TeamFellowshipRecordKind.Outside, alive, locked, full, leech);

        private static TeamFellowshipRestoreFacts Premade(bool copyAlive = false, bool copyLocked = false, bool copyFull = false, bool copyLeech = false) =>
            new TeamFellowshipRestoreFacts(true, true, false, false, false, TeamFellowshipRecordKind.Premade,
                RebuiltCopyAlive: copyAlive, RebuiltCopyLocked: copyLocked, RebuiltCopyFull: copyFull, RebuiltCopyLeechLocked: copyLeech);

        [TestMethod]
        public void Restore_Decide_Table()
        {
            var rows = new (string Name, TeamFellowshipRestoreFacts Facts, TeamFellowshipRestoreAction Action, bool Told)[]
            {
                ("outside alive", Outside(), TeamFellowshipRestoreAction.Rejoin, false),
                ("outside gone", Outside(alive: false), TeamFellowshipRestoreAction.None, true),
                ("outside locked", Outside(locked: true), TeamFellowshipRestoreAction.None, true),
                ("outside full", Outside(full: true), TeamFellowshipRestoreAction.None, true),
                ("outside leech-locked", Outside(leech: true), TeamFellowshipRestoreAction.None, true),
                ("premade, first home", Premade(), TeamFellowshipRestoreAction.Rebuild, false),
                ("premade, re-formed copy stands", Premade(copyAlive: true), TeamFellowshipRestoreAction.JoinRebuilt, false),
                ("premade, re-formed copy full", Premade(copyAlive: true, copyFull: true), TeamFellowshipRestoreAction.None, true),
                ("premade, re-formed copy locked", Premade(copyAlive: true, copyLocked: true), TeamFellowshipRestoreAction.None, true),
                ("premade, re-formed copy leech-locked", Premade(copyAlive: true, copyLeech: true), TeamFellowshipRestoreAction.None, true),
                ("logging out", Outside() with { LoggingOut = true }, TeamFellowshipRestoreAction.None, false),
                ("PK logout", Outside() with { PkLogout = true }, TeamFellowshipRestoreAction.None, false),
                ("offline", Outside() with { Online = false }, TeamFellowshipRestoreAction.None, false),
                ("already fellowed", Outside() with { AlreadyInFellowship = true }, TeamFellowshipRestoreAction.None, false),
                ("already fellowed, premade", Premade() with { AlreadyInFellowship = true }, TeamFellowshipRestoreAction.None, false),
                ("restore off", Outside() with { RestoreEnabled = false }, TeamFellowshipRestoreAction.None, false),
                ("nothing recorded", Outside() with { Kind = TeamFellowshipRecordKind.None }, TeamFellowshipRestoreAction.None, false),
            };

            foreach (var row in rows)
            {
                var d = TeamFellowshipRestore.Decide(row.Facts);
                Assert.AreEqual(row.Action, d.Action, row.Name);
                Assert.AreEqual(row.Told, d.TellEnded, row.Name + " (told)");
            }
        }

        [TestMethod]
        public void Restore_BuildFacts_FullAtExactlyTheCap()
        {
            TeamFellowshipRestoreDecision OutsideWith(int liveCount, int cap) => TeamFellowshipRestore.Decide(TeamFellowshipRestore.BuildFacts(
                true, true, false, false, false, TeamFellowshipRecordKind.Outside, liveCount, false, false, null, false, false, cap));

            TeamFellowshipRestoreDecision CopyWith(int liveCount, int cap) => TeamFellowshipRestore.Decide(TeamFellowshipRestore.BuildFacts(
                true, true, false, false, false, TeamFellowshipRecordKind.Premade, null, false, false, liveCount, false, false, cap));

            Assert.AreEqual(TeamFellowshipRestoreAction.Rejoin, OutsideWith(19, 20).Action, "one below the cap: room for one more");
            Assert.AreEqual(new TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction.None, true), OutsideWith(20, 20), "exactly at the cap: full, told it ended");
            Assert.AreEqual(new TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction.None, true), OutsideWith(0, 20), "no live member: gone");

            Assert.AreEqual(TeamFellowshipRestoreAction.JoinRebuilt, CopyWith(19, 20).Action);
            Assert.AreEqual(new TeamFellowshipRestoreDecision(TeamFellowshipRestoreAction.None, true), CopyWith(20, 20), "the re-formed copy at the cap is full");
            Assert.AreEqual(TeamFellowshipRestoreAction.Rebuild, CopyWith(0, 20).Action, "no live copy: re-form it");
            Assert.AreEqual(TeamFellowshipRestoreAction.Rebuild, TeamFellowshipRestore.Decide(TeamFellowshipRestore.BuildFacts(
                true, true, false, false, false, TeamFellowshipRecordKind.Premade, null, false, false, null, false, false, 20)).Action, "never re-formed yet");

            Assert.IsTrue(TeamFellowshipRestore.IsFull(6, 6));
            Assert.IsFalse(TeamFellowshipRestore.IsFull(5, 6));
        }

        private const uint E = 0x50000025, F = 0x50000026, Outsider = 0x50000099;

        [TestMethod]
        public void PlanForm_ClassifiesArrivals_RecordsAndPicksLeaders()
        {
            // West: B (first, in a LOCKED fellowship), A and C (a premade together), D (stale team fellowship).
            // East: E (offline), F (in an outside fellowship with someone not in the match).
            var teams = new[]
            {
                new PvpTeamFellowshipTeam(0, "West Team", new[] { B, A, C, D }),
                new PvpTeamFellowshipTeam(1, "East Team", new[] { E, F }),
            };

            var arrivals = new Dictionary<uint, TeamFellowshipArrival>
            {
                [A] = new TeamFellowshipArrival(A, true, 1),
                [B] = new TeamFellowshipArrival(B, true, 2, FellowshipLocked: true),
                [C] = new TeamFellowshipArrival(C, true, 1),
                [D] = new TeamFellowshipArrival(D, true, 3, FellowshipIsTeam: true),
                [E] = new TeamFellowshipArrival(E, false),
                [F] = new TeamFellowshipArrival(F, true, 4),
            };

            var live = new Dictionary<int, IReadOnlyList<uint>>
            {
                [1] = new[] { A, C },
                [2] = new[] { B },
                [3] = new[] { D },
                [4] = new[] { F, Outsider },
            };

            var plan = TeamFellowshipPlan.PlanForm(teams, id => arrivals[id], live);

            CollectionAssert.AreEqual(new[] { B }, plan.LockedSkips.ToArray(), "the locked fellowship is left alone");
            CollectionAssert.AreEqual(new[] { D }, plan.LeaveStaleTeam.ToArray(), "a leftover team fellowship is left");

            CollectionAssert.AreEquivalent(new[] { A, C, F }, plan.Records.Keys.ToArray(), "recorded: every ordinary fellowship member, nobody else");
            Assert.AreEqual(new TeamFellowshipArrivalRecord(TeamFellowshipRecordKind.Premade, 1), plan.Records[A]);
            Assert.AreEqual(new TeamFellowshipArrivalRecord(TeamFellowshipRecordKind.Premade, 1), plan.Records[C]);
            Assert.AreEqual(new TeamFellowshipArrivalRecord(TeamFellowshipRecordKind.Outside, 4), plan.Records[F], "a member outside the match makes it an outside fellowship");

            var west = plan.Teams.Single(t => t.TeamIndex == 0);
            Assert.AreEqual(A, west.LeaderId, "the first ELIGIBLE member in team order (B is locked out)");
            CollectionAssert.AreEqual(new[] { C, D }, west.MemberIds.ToArray(), "the rest of the eligible members, in team order");

            var east = plan.Teams.Single(t => t.TeamIndex == 1);
            Assert.AreEqual(F, east.LeaderId, "the offline member is passed over");
            Assert.AreEqual(0, east.MemberIds.Count);
        }

        [TestMethod]
        public void PlanForm_TeamWithNoEligibleMember_GetsNoFellowship()
        {
            var teams = new[]
            {
                new PvpTeamFellowshipTeam(0, "West Team", new[] { A, B }),
                new PvpTeamFellowshipTeam(1, "East Team", new[] { C, D }),
            };

            var arrivals = new Dictionary<uint, TeamFellowshipArrival>
            {
                [A] = new TeamFellowshipArrival(A, true, 1, FellowshipLocked: true),
                [B] = new TeamFellowshipArrival(B, true, 1, FellowshipLocked: true),
                [C] = new TeamFellowshipArrival(C, true),
                [D] = new TeamFellowshipArrival(D, true),
            };

            var plan = TeamFellowshipPlan.PlanForm(teams, id => arrivals[id], new Dictionary<int, IReadOnlyList<uint>> { [1] = new[] { A, B } });

            Assert.AreEqual(0u, plan.Teams.Single(t => t.TeamIndex == 0).LeaderId, "everyone locked: no West fellowship (documented, accepted)");
            Assert.AreEqual(C, plan.Teams.Single(t => t.TeamIndex == 1).LeaderId);
            CollectionAssert.AreEqual(new[] { D }, plan.Teams.Single(t => t.TeamIndex == 1).MemberIds.ToArray());
            Assert.AreEqual(0, plan.Records.Count, "nobody quit anything");
        }

        [TestMethod]
        public void Plan_Orphans_AreTheInactiveMatches()
        {
            var live = Guid.NewGuid();
            var gone = Guid.NewGuid();

            CollectionAssert.AreEqual(new[] { gone }, TeamFellowshipPlan.Orphans(new[] { live, gone, gone }, new[] { live }).ToArray());
            Assert.AreEqual(0, TeamFellowshipPlan.Orphans(new[] { live }, new[] { live }).Count);
        }

        private static PvpPlayerBinding BindingFor(string modeKey, PvpMatchState state, bool teamFellowship) =>
            new PvpPlayerBinding(new PvpMatch(Guid.NewGuid(), modeKey, new List<PvpTeam>(), T0), 0, state, true, true, true, true, true, teamFellowship: teamFellowship);

        [TestMethod]
        public void Gate_RefusesFellowshipChange_Table()
        {
            Assert.IsFalse(PvpPlayerRules.RefusesFellowshipChange(null), "no binding");

            foreach (PvpMatchState state in Enum.GetValues(typeof(PvpMatchState)))
            {
                // Closed is the death hold (MarkOut(Died) publishes it while the dying player is still in the team fellowship).
                var expected = state == PvpMatchState.Staging || state == PvpMatchState.Countdown || state == PvpMatchState.Live || state == PvpMatchState.Resolving
                    || state == PvpMatchState.Closed;

                Assert.AreEqual(expected, PvpPlayerRules.RefusesFellowshipChange(BindingFor(BattlegroundModes.KothModeKey, state, true)), $"bg {state}");
                Assert.IsFalse(PvpPlayerRules.RefusesFellowshipChange(BindingFor(BattlegroundModes.KothModeKey, state, false)), $"bg {state} without the flag");
                Assert.IsFalse(PvpPlayerRules.RefusesFellowshipChange(BindingFor(ArenaMapCatalog.TwoVTwoKey, state, true)), $"arena {state}, even flagged");
            }
        }

        // ======================================================================================
        // coordinator, with a recording fake
        // ======================================================================================

        private sealed class FakePlayer
        {
            public PvpPlayerFacts Facts;
            public bool Online = true;
            public uint Instance;
            public bool Dying;
            public Position Location = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0);
        }

        /// <summary>The arena and battleground halves only: no team fellowship seam, so the feature is off.</summary>
        private class PlainGateway : IPvpPlayerGateway, IBattlegroundPlayerGateway
        {
            public readonly Dictionary<uint, FakePlayer> Players = new();
            public readonly List<(uint Id, PvpPlayerBinding Binding)> Bindings = new();
            public readonly List<uint> ExitedFromPen = new();

            public FakePlayer Add(uint id, string name)
            {
                var p = new FakePlayer { Facts = new PvpPlayerFacts(id, name, 100, $"10.0.1.{id & 0xFF}", false, false, false, false, false, false, false, true, Array.Empty<uint>(), PreferredTemplateKey: TemplateKey) };
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
                Bindings.Add((id, binding));
                Players[id].Instance = spawn.Instance;
            }

            public void PublishBinding(uint id, PvpPlayerBinding binding) => Bindings.Add((id, binding));

            public void ExitMatch(uint id, string context) { }

            public void ReturnToExit(uint id, Position exitTo, uint matchInstance)
            {
                if (Players.TryGetValue(id, out var p) && p.Online && p.Instance == matchInstance)
                    p.Instance = 0;
            }

            public void Send(uint id, string text) { }

            public bool SendAcceptPrompt(uint id, Guid matchId, string text) => IsOnline(id);

            public void AbortAcceptPrompt(uint id, Guid matchId) { }

            public void GrantArenaBlood(uint id, int amount, PvpBloodGrant grant) { }

            public string CheckTemplateRoom(uint id, PvpTemplateDefinition template) => null;

            public void RunTemplateBackstop(uint id, Guid matchId) { }

            public BattlegroundZoneSample SampleZone(uint id, uint matchInstance) =>
                IsOnline(id) ? new BattlegroundZoneSample(id, 0, null, Players[id].Instance == matchInstance, Players[id].Dying, false, 0, 0, 0) : null;

            public void DrainVitals(uint id, Guid matchId, uint matchInstance, int health, int stamina, int mana, bool lethal) { }

            public void Respawn(uint id, Guid matchId, Position spawn) { }

            public bool IsInDeathProcess(uint id) => Players.TryGetValue(id, out var p) && p.Online && p.Dying;

            public void ExitMatchFromPen(uint id, string context) => ExitedFromPen.Add(id);
        }

        /// <summary>Records every team fellowship call; keeps the formed match ids the way the live gateway keeps its fellowships.</summary>
        private sealed class TeamGateway : PlainGateway, IPvpTeamFellowshipGateway
        {
            public int Cap = 20;
            public bool IgnoreDispose;

            public readonly List<(Guid Match, IReadOnlyList<PvpTeamFellowshipTeam> Teams)> Forms = new();
            public readonly List<(Guid Match, uint Id, bool Restore)> Releases = new();
            public readonly List<Guid> Disposes = new();
            public readonly List<IReadOnlyCollection<Guid>> Sweeps = new();
            public readonly HashSet<Guid> Standing = new();
            public readonly List<Guid> Dissolved = new();

            public int FellowshipCap => Cap;

            public void Form(Guid matchId, IReadOnlyList<PvpTeamFellowshipTeam> teams)
            {
                Forms.Add((matchId, teams));
                Standing.Add(matchId);
            }

            public void Release(Guid matchId, uint characterId, bool restore) => Releases.Add((matchId, characterId, restore));

            public void Dispose(Guid matchId)
            {
                Disposes.Add(matchId);

                if (!IgnoreDispose)
                    Standing.Remove(matchId);
            }

            public void Sweep(IReadOnlyCollection<Guid> activeMatchIds)
            {
                Sweeps.Add(activeMatchIds.ToList());

                foreach (var orphan in TeamFellowshipPlan.Orphans(Standing.ToList(), activeMatchIds))
                {
                    Standing.Remove(orphan);
                    Dissolved.Add(orphan);
                }
            }

            public bool Released(uint id) => Releases.Any(r => r.Id == id);
        }

        private sealed class FakeSpaces : IPvpMatchSpaces, IBattlegroundMatchSpaces
        {
            private ushort next = 0x0300;

            public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<uint> ids) =>
                MatchSpaceAllocation.Ok(new MatchSpace(map.MapKey, map.LandblockId, map.RealmId, Position.InstanceIDFromVars(map.RealmId, next++, isTemporaryRuleset: true), ids[0]));

            public MatchSpaceReadiness GetReadiness(MatchSpace space) => MatchSpaceReadiness.Ready;

            public void Release(MatchSpace space) { }

            public BattlegroundFixtureJob SpawnFixtures(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                var job = new BattlegroundFixtureJob();
                job.Complete(pieces.Count);
                return job;
            }

            public void RemoveZoneMarkers(MatchSpace space, BattlegroundFixtureJob placed) { }

            public BattlegroundFixtureJob SpawnStartGates(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                var job = new BattlegroundFixtureJob();
                job.Complete(pieces.Count);
                return job;
            }

            public void RemoveStartGates(MatchSpace space, BattlegroundFixtureJob placed) { }

            public BattlegroundFixtureJob SpawnZoneMarkers(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                var job = new BattlegroundFixtureJob();
                job.Complete(pieces.Count);
                return job;
            }
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
                    TemplateKey = TemplateKey, DisplayName = "Duelist", Version = 1, Enabled = true, Modes = "1v1,2v2,ffa,bg_koth",
                    DefinitionJson = PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = TemplateKey, Version = 1, DisplayName = "Duelist" }),
                    SnapshotAt = T0, SnapshotBy = "test",
                }
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
            public PlainGateway Gateway;
            public readonly FakeSpaces Spaces = new();
            public readonly ConcurrentQueue<PvpIntent> Intents = new();
            public PvpArenaDials Dials = PvpTunables.Defaults with { CrierEnabled = false };
            public BattlegroundDials Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0 };
            public PvpMatchCoordinator Coordinator;

            public Rig(bool withSeam = true)
            {
                Gateway = withSeam ? new TeamGateway() : new PlainGateway();
            }

            public TeamGateway Team => (TeamGateway)Gateway;

            public Rig Build(params uint[] ids)
            {
                Coordinator = new PvpMatchCoordinator(Gateway, Spaces, new FakeSink(), () => Now, () => Dials, Intents, new NoCrier(), () => Bg);
                Coordinator.BeginLoadRatings();
                Coordinator.BeginLoadTemplates();
                Coordinator.BeginLoadLatestTemplates();
                Coordinator.Tick();

                foreach (var id in ids)
                    Gateway.Add(id, $"P{id & 0xFF:X2}");

                return this;
            }

            public void Tick() => Coordinator.Tick();

            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

            public PvpMatch MatchOf(uint id) => Coordinator.FindMatch(id);

            /// <summary>Joins, forms, accepts and ticks to Staging-dispatched (the tick before Countdown).</summary>
            public PvpMatch ToDispatched(string room, params uint[] ids)
            {
                foreach (var id in ids)
                    Assert.IsTrue(Coordinator.Join(id, room, false).Joined, $"join 0x{id:X8}");

                Tick();
                Assert.AreEqual(PvpMatchState.AwaitingAccept, MatchOf(ids[0]).State);

                foreach (var id in ids)
                    Assert.AreEqual(PvpAnswerOutcome.Accepted, Coordinator.Accept(id).Outcome);

                Tick(); // Staging, allocate
                Tick(); // dispatch
                Assert.AreEqual(PvpMatchState.Staging, MatchOf(ids[0]).State);
                return MatchOf(ids[0]);
            }

            public PvpMatch ToCountdown(string room, params uint[] ids)
            {
                var m = ToDispatched(room, ids);
                Tick(); // arrived -> Countdown
                Assert.AreEqual(PvpMatchState.Countdown, m.State);
                return m;
            }

            public PvpMatch ToLive(string room, params uint[] ids)
            {
                var m = ToCountdown(room, ids);
                Advance(Dials.CountdownSeconds);
                Tick();
                Assert.AreEqual(PvpMatchState.Live, m.State);
                return m;
            }
        }

        private static uint[] TeamIds(PvpMatch m, int team) => m.Teams[team].Members.Select(p => p.CharacterId).ToArray();

        [TestMethod]
        public void Form_IsCalledOnceAtCountdown_NeverAtStaging()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToDispatched("bg", A, B, C, D);

            Assert.AreEqual(0, rig.Team.Forms.Count, "nothing is formed in Staging");
            Assert.IsTrue(rig.Gateway.Bindings.Where(b => b.Binding.State == PvpMatchState.Staging).All(b => b.Binding.TeamFellowship), "the Staging bindings already carry the flag (the gate covers Staging)");

            rig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, m.State);
            Assert.AreEqual(1, rig.Team.Forms.Count, "formed at Countdown");

            var form = rig.Team.Forms[0];
            Assert.AreEqual(m.MatchId, form.Match);
            Assert.AreEqual(2, form.Teams.Count, "both teams");
            Assert.AreEqual("West Team", form.Teams.Single(t => t.TeamIndex == 0).Name);
            Assert.AreEqual("East Team", form.Teams.Single(t => t.TeamIndex == 1).Name);

            foreach (var team in form.Teams)
                CollectionAssert.AreEqual(TeamIds(m, team.TeamIndex), team.MemberIds.ToArray(), "every active member, in team order");

            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, m.State);
            Assert.AreEqual(1, rig.Team.Forms.Count, "never formed again");
            Assert.IsTrue(rig.Gateway.Bindings.Where(b => b.Binding.State == PvpMatchState.Live).All(b => b.Binding.TeamFellowship), "the Live republish derives the flag again");
        }

        [TestMethod]
        public void Arena_NeverFormsOrFlags()
        {
            var rig = new Rig().Build(A, B);
            rig.ToLive(ArenaMapCatalog.OneVOneKey, A, B);

            Assert.AreEqual(0, rig.Team.Forms.Count);
            Assert.IsTrue(rig.Gateway.Bindings.Count > 0);
            Assert.IsFalse(rig.Gateway.Bindings.Any(b => b.Binding.TeamFellowship), "no arena binding carries the flag");

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(A).Outcome);
            Assert.AreEqual(0, rig.Team.Releases.Count, "no release for an arena exit");
        }

        [TestMethod]
        public void GatewayWithoutTheSeam_LeavesTheFeatureOff()
        {
            var rig = new Rig(withSeam: false).Build(A, B, C, D);
            rig.ToLive("bg", A, B, C, D);

            Assert.IsFalse(rig.Gateway.Bindings.Any(b => b.Binding.TeamFellowship), "no seam, no flag");
        }

        [TestMethod]
        public void OffSwitch_AndAboveCap_FormNothing_ForEitherTeam()
        {
            var off = new Rig();
            off.Bg = off.Bg with { TeamFellowship = false };
            off.Build(A, B, C, D).ToLive("bg", A, B, C, D);

            Assert.AreEqual(0, off.Team.Forms.Count, "pvp_bg_team_fellowship off");
            Assert.IsFalse(off.Gateway.Bindings.Any(b => b.Binding.TeamFellowship));

            var capped = new Rig();
            capped.Build(A, B, C, D);
            capped.Team.Cap = 1;
            capped.ToLive("bg", A, B, C, D);

            Assert.AreEqual(0, capped.Team.Forms.Count, "teams of 2 against a cap of 1: both skipped");
            Assert.IsFalse(capped.Gateway.Bindings.Any(b => b.Binding.TeamFellowship));
        }

        [TestMethod]
        public void Release_LeaveCommand()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToLive("bg", A, B, C, D);

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(A).Outcome);

            CollectionAssert.AreEqual(new[] { (m.MatchId, A, true) }, rig.Team.Releases.ToArray(), "released once, with the match's restore snapshot");
        }

        [TestMethod]
        public void Release_LeftTheInstance()
        {
            var rig = new Rig().Build(A, B, C, D);
            rig.ToLive("bg", A, B, C, D);

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.IsTrue(rig.Team.Released(B));
        }

        [TestMethod]
        public void Release_LogoutForfeit()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToLive("bg", A, B, C, D);

            rig.Gateway.Players[C].Online = false;
            rig.Intents.Enqueue(PvpMatchManager.LogoutForfeit(C, m.MatchId, rig.Now));
            rig.Tick();

            Assert.IsTrue(rig.Team.Released(C));
        }

        [TestMethod]
        public void Release_CountdownDeathReturn()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToCountdown("bg", A, B, C, D);

            rig.Intents.Enqueue(PvpMatchManager.Death(A, m.MatchId, rig.Now));
            rig.Tick();
            Assert.IsFalse(rig.Team.Released(A), "still in the instance: the death return has not landed");

            rig.Gateway.Players[A].Instance = 0;
            rig.Tick();
            Assert.IsTrue(rig.Team.Released(A), "released once the death return lands");
        }

        [TestMethod]
        public void Release_OfflineAfterDying_IsReleased()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToCountdown("bg", A, B, C, D);

            rig.Intents.Enqueue(PvpMatchManager.Death(A, m.MatchId, rig.Now));
            rig.Tick();
            Assert.IsFalse(rig.Team.Released(A), "still in the instance");

            // Logged out while the death return was in flight: ProcessDeathReturns marks the seat exited without ExitSeat.
            rig.Gateway.Players[A].Online = false;
            rig.Tick();
            Assert.IsTrue(rig.Team.Released(A), "the offline-after-dying branch releases the seat");

            rig.Coordinator.Cancel(m.MatchId);
            Assert.AreEqual(1, rig.Team.Releases.Count(r => r.Id == A), "never released twice");
        }

        [TestMethod]
        public void Release_DyingToPen_BeforeTheEarlyReturn()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToLive("bg", A, B, C, D);
            var victim = TeamIds(m, 0)[0];

            rig.Gateway.Players[victim].Dying = true;
            rig.Intents.Enqueue(PvpMatchManager.Death(victim, m.MatchId, rig.Now));
            rig.Tick();

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(victim).Outcome);

            CollectionAssert.Contains(rig.Gateway.ExitedFromPen, victim, "the dying-to-pen exit path ran");
            Assert.IsTrue(rig.Team.Released(victim), "and the release ran before its early return");
        }

        [TestMethod]
        public void Release_Close_EveryoneOnce_ThenDisposeOnce()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToLive("bg", A, B, C, D);

            m.ScoreBoard[0] = rig.Bg.KothScoreTarget;
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, m.State);
            Assert.AreEqual(0, rig.Team.Disposes.Count);

            rig.Advance(rig.Dials.PostMatchSeconds);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Closed, m.State);

            CollectionAssert.AreEquivalent(new[] { A, B, C, D }, rig.Team.Releases.Select(r => r.Id).ToArray(), "each released exactly once");
            CollectionAssert.AreEqual(new[] { m.MatchId }, rig.Team.Disposes.ToArray(), "disposed once, on Closed");
        }

        [TestMethod]
        public void Release_AdminCancelDuringLive_ThenDisposeOnce()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToLive("bg", A, B, C, D);

            Assert.AreEqual(PvpCancelOutcome.Canceled, rig.Coordinator.Cancel(m.MatchId).Outcome);

            CollectionAssert.AreEquivalent(new[] { A, B, C, D }, rig.Team.Releases.Select(r => r.Id).ToArray());
            CollectionAssert.AreEqual(new[] { m.MatchId }, rig.Team.Disposes.ToArray(), "disposed once, on Canceled");

            rig.Tick();
            rig.Advance(60);
            rig.Tick();
            Assert.AreEqual(1, rig.Team.Disposes.Count, "never disposed again");
        }

        [TestMethod]
        public void Release_DenylistCancelBeforeLive()
        {
            var saved = PvpArenaDenylistTunables.DialSource;

            try
            {
                var rig = new Rig().Build(A, B, C, D);
                var m = rig.ToCountdown("bg", A, B, C, D);

                PvpArenaDenylistTunables.DialSource = () => new PvpArenaDenylistDials(new HashSet<uint> { A });
                rig.Advance(rig.Dials.CountdownSeconds);
                rig.Tick();

                Assert.AreEqual(PvpMatchState.Canceled, m.State, "the denylist cancel ran");
                CollectionAssert.AreEquivalent(new[] { A, B, C, D }, rig.Team.Releases.Select(r => r.Id).ToArray());
                CollectionAssert.AreEqual(new[] { m.MatchId }, rig.Team.Disposes.ToArray());
            }
            finally
            {
                PvpArenaDenylistTunables.DialSource = saved;
            }
        }

        [TestMethod]
        public void Release_CarriesTheRestoreSnapshot()
        {
            var rig = new Rig();
            rig.Bg = rig.Bg with { TeamFellowshipRestore = false };
            rig.Build(A, B, C, D);
            rig.ToLive("bg", A, B, C, D);

            rig.Coordinator.Forfeit(A);

            Assert.IsFalse(rig.Team.Releases.Single().Restore, "pvp_bg_team_fellowship_restore off at formation");
        }

        [TestMethod]
        public void Dispose_CanceledBeforeCountdown_IsCalledOnce()
        {
            var rig = new Rig().Build(A, B, C, D);
            var m = rig.ToDispatched("bg", A, B, C, D);

            Assert.AreEqual(PvpCancelOutcome.Canceled, rig.Coordinator.Cancel(m.MatchId).Outcome);

            Assert.AreEqual(0, rig.Team.Forms.Count);
            CollectionAssert.AreEqual(new[] { m.MatchId }, rig.Team.Disposes.ToArray());
        }

        [TestMethod]
        public void Sweep_IsRateLimited_AndDissolvesOrphans()
        {
            var rig = new Rig().Build(A, B, C, D);
            var sweepsAtBoot = rig.Team.Sweeps.Count;

            rig.Tick();
            Assert.AreEqual(sweepsAtBoot, rig.Team.Sweeps.Count, "rate-limited: no second sweep within the interval");

            rig.Team.IgnoreDispose = true; // a Dispose that never took: the team fellowship outlives its match
            var m = rig.ToLive("bg", A, B, C, D);

            rig.Advance(PvpMatchCoordinator.TeamFellowshipSweepIntervalSeconds);
            rig.Tick();
            Assert.IsTrue(rig.Team.Sweeps.Last().Contains(m.MatchId), "a live match is active");
            Assert.AreEqual(0, rig.Team.Dissolved.Count, "nothing to dissolve while it is live");

            rig.Coordinator.Cancel(m.MatchId);
            Assert.IsTrue(rig.Team.Standing.Contains(m.MatchId), "the dispose did not take");

            rig.Advance(PvpMatchCoordinator.TeamFellowshipSweepIntervalSeconds);
            rig.Tick();

            Assert.IsFalse(rig.Team.Sweeps.Last().Contains(m.MatchId), "the canceled match is no longer active");
            CollectionAssert.AreEqual(new[] { m.MatchId }, rig.Team.Dissolved.ToArray(), "the sweep dissolved the orphan");
        }

        // ======================================================================================
        // source scans: every player-side gate site, and the system paths
        // ======================================================================================

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Server");
        }

        /// <summary>Source text, line endings normalised, comments and string literals blanked.</summary>
        private static string Code(string relativePath)
        {
            var path = Path.Combine(SourceRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"{path} not found");

            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            text = Regex.Replace(text, @"//[^\n]*", "");
            text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            text = Regex.Replace(text, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");

            return text;
        }

        /// <summary>The brace-matched body of the first method declared as <paramref name="name"/>(...).</summary>
        private static string Body(string code, string name)
        {
            var declaration = Regex.Match(code, @"(?m)^\s*(?:public|private|internal|protected)[^;{=\n]*\b" + Regex.Escape(name) + @"\(");
            Assert.IsTrue(declaration.Success, $"declaration of {name} not found");

            var open = code.IndexOf('{', declaration.Index);
            var depth = 0;

            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces in {name}");
            return null;
        }

        /// <summary>The gate call is in the method's FIRST statement: it appears before the body's first semicolon.</summary>
        private static bool GateIsFirst(string body, string gate)
        {
            var at = body.IndexOf(gate, StringComparison.Ordinal);
            var firstStatementEnd = body.IndexOf(';');

            return at >= 0 && firstStatementEnd >= 0 && at < firstStatementEnd;
        }

        /// <summary>The real body passes, and the same body with the gate identifier substituted fails: the scan binds the identifier.</summary>
        private static void AssertGateFirst(string relativePath, string method, string gate)
        {
            var body = Body(Code(relativePath), method);

            Assert.IsTrue(GateIsFirst(body, gate), $"{relativePath}: {method} does not open with `{gate}`");
            Assert.IsFalse(GateIsFirst(body.Replace(gate, "SomethingElse("), gate), $"{relativePath}: the scan for {method} does not discriminate");
        }

        private const string PlayerGate = "PvpTeamFellowshipBlocked(";

        [TestMethod]
        public void Scan_PlayerFellowshipHandlers_OpenWithTheGate()
        {
            foreach (var method in new[] { "FellowshipCreate", "FellowshipRecruit", "FellowshipDismissPlayer", "FellowshipNewLeader", "HandleActionFellowshipChangeOpenness", "HandleActionFellowshipChangeLock", "FellowshipQuit" })
                AssertGateFirst("WorldObjects/Player_Fellowship.cs", method, PlayerGate);
        }

        [TestMethod]
        public void Scan_Quit_SystemBypassesTheGate_AndLogoutPassesSystemTrue()
        {
            var quit = Body(Code("WorldObjects/Player_Fellowship.cs"), "FellowshipQuit");
            StringAssert.Contains(quit, "!system && PvpTeamFellowshipBlocked(", "only a non-system quit is gated");

            var logout = Body(Code("WorldObjects/Player.cs"), "LogOut_Inner");
            StringAssert.Contains(logout, "FellowshipQuit(false, system: true)");
            Assert.IsFalse(Regex.IsMatch(logout.Replace("FellowshipQuit(false, system: true)", ""), @"FellowshipQuit\("), "LogOut_Inner has no other, gated quit");
            Assert.IsFalse(logout.Replace("system: true", "system: false").Contains("FellowshipQuit(false, system: true)"), "the scan discriminates");
        }

        [TestMethod]
        public void Scan_FshipDispatcher_GatesEveryMutatingSubcommand_BeforeTheSwitch()
        {
            var body = Body(Code("Command/Handlers/FellowshipCommands.cs"), "HandleFship");
            var gate = "IsMutatingSubcommand(sub) && player.PvpTeamFellowshipBlocked(";
            var at = body.IndexOf(gate, StringComparison.Ordinal);
            var sw = body.IndexOf("switch (sub)", StringComparison.Ordinal);

            Assert.IsTrue(at >= 0 && sw > at, "the /fship gate runs before the dispatch switch");
            Assert.IsTrue(body.Replace(gate, "X(").IndexOf(gate, StringComparison.Ordinal) < 0, "the scan discriminates");

            foreach (var sub in new[] { "create", "add", "join", "addlandblock", "joinlandblock", "password", "noleech", "leech", "quit", "disband" })
                Assert.IsTrue(ACE.Server.Command.Handlers.FellowshipCommands.IsMutatingSubcommand(sub), sub);

            Assert.IsFalse(ACE.Server.Command.Handlers.FellowshipCommands.IsMutatingSubcommand("list"), "list only reads");

            // every case of the dispatch switch is either classified mutating or is list
            var cases = Regex.Matches(body, "case \"\"").Count;
            Assert.AreEqual(11, cases, "the /fship switch gained or lost a subcommand; classify it in IsMutatingSubcommand and update this count");
        }

        [TestMethod]
        public void Scan_TryDirectJoin_OpensWithTheGate()
        {
            AssertGateFirst("Command/Handlers/FellowshipCommands.cs", "TryDirectJoin", PlayerGate);
        }

        [TestMethod]
        public void Scan_FellowshipAddPaths_RefuseATeamFellowship_BeforeAnyWrite()
        {
            var code = Code("Entity/Fellowship.cs");

            foreach (var (method, firstWrite) in new[] { ("AddFellowshipMember", "AddConfirmedMember("), ("AddConfirmedMember", "FellowshipMembers.TryAdd(") })
            {
                var body = Body(code, method);
                var at = body.IndexOf("RefusePvpTeamAdd(", StringComparison.Ordinal);
                var write = body.IndexOf(firstWrite, StringComparison.Ordinal);

                Assert.IsTrue(at >= 0, $"{method} lost its team refusal");
                Assert.IsTrue(write > at, $"{method}: the team refusal must run before `{firstWrite}`");
            }
        }

        [TestMethod]
        public void Scan_ExitSeat_ReleasesFirst_BeforeTheDyingToPenReturn()
        {
            var body = Body(Code("Pvp/PvpMatchCoordinator.cs"), "ExitSeat");

            Assert.IsTrue(GateIsFirst(body, "ReleaseTeamFellowship("), "ExitSeat's first statement is the release");
            Assert.IsTrue(body.IndexOf("ReleaseTeamFellowship(", StringComparison.Ordinal) < body.IndexOf("ExitMatchFromPen(", StringComparison.Ordinal));
            Assert.IsFalse(GateIsFirst(body.Replace("ReleaseTeamFellowship(", "Other("), "ReleaseTeamFellowship("), "the scan discriminates");
        }
    }
}
