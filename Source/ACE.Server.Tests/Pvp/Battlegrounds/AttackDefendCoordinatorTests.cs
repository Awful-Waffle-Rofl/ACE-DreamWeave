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
    /// Phase B of Docs/Pvp/ATTACK-DEFEND.md through the coordinator: the side flip, the crystal staging step, the destroy intent, the
    /// win and timeout, the mode rotation and the attacker-leave rescale. Its own fakes implement the objective half of the space seam
    /// (IObjectiveMatchSpaces) on top of the battleground halves; BattlegroundCoordinatorTests' fakes do not, so every King of the Hill
    /// test there still forms King of the Hill and is untouched.
    /// </summary>
    [TestClass]
    public class AttackDefendCoordinatorTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private const uint A = 0x50000021, B = 0x50000022, C = 0x50000023, D = 0x50000024, E = 0x50000025, F = 0x50000026, G = 0x50000027, H = 0x50000028;

        private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

        private const string TemplateKey = "duelist";

        private const string BothModes = "1v1,2v2,ffa,bg_koth,bg_ad";

        private static PvpTemplateRecord TemplateRow(string key, string modes) => new PvpTemplateRecord
        {
            TemplateKey = key,
            DisplayName = "Duelist",
            Version = 1,
            Enabled = true,
            Modes = modes,
            DefinitionJson = PvpTemplateJson.SerializeDefinition(new PvpTemplateDefinition { Key = key, Version = 1, DisplayName = "Duelist" }),
            SnapshotAt = T0,
            SnapshotBy = "test",
        };

        // ================= fakes =================

        private sealed class FakePlayer
        {
            public PvpPlayerFacts Facts;
            public bool Online = true;
            public uint Instance;
            public bool Dying;
            public Position Location;
            public float X, Y, Z;
        }

        private sealed class FakeGateway : IPvpPlayerGateway, IBattlegroundPlayerGateway, IPvpTeamFellowshipGateway
        {
            public readonly Dictionary<uint, FakePlayer> Players = new();
            public readonly List<(uint Id, string Text)> Messages = new();
            public readonly List<(uint Id, PvpPlayerBinding Binding, Position Spawn)> Entered = new();
            public readonly List<(uint Id, PvpPlayerBinding Binding)> Published = new();
            public readonly List<(uint Id, Guid Match, Position Spawn)> Respawns = new();
            public readonly List<(uint Id, int Amount, PvpBloodGrant Grant)> BloodGrants = new();
            public readonly List<(Guid Match, IReadOnlyList<PvpTeamFellowshipTeam> Teams)> Forms = new();

            public FakePlayer Add(uint id, string name, string template = TemplateKey)
            {
                var p = new FakePlayer
                {
                    Facts = new PvpPlayerFacts(id, name, 100, $"10.0.1.{id & 0xFF}", false, false, false, false, false, false, false, true, Array.Empty<uint>(), 0, PreferredTemplateKey: template),
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
                Entered.Add((id, binding, spawn));
                Players[id].Instance = spawn.Instance;
                Players[id].Location = new Position(spawn); // the player stands where the teleport put them: in their spawn room
            }

            public void PublishBinding(uint id, PvpPlayerBinding binding) => Published.Add((id, binding));

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

            public void GrantArenaBlood(uint id, int amount, PvpBloodGrant grant) => BloodGrants.Add((id, amount, grant));

            public string CheckTemplateRoom(uint id, PvpTemplateDefinition template) => null;

            public void RunTemplateBackstop(uint id, Guid matchId) { }

            public BattlegroundZoneSample SampleZone(uint id, uint matchInstance) =>
                IsOnline(id) ? new BattlegroundZoneSample(id, 0, null, Players[id].Instance == matchInstance, Players[id].Dying, false, Players[id].X, Players[id].Y, Players[id].Z) : null;

            public void DrainVitals(uint id, Guid matchId, uint matchInstance, int health, int stamina, int mana, bool lethal) { }

            public void Respawn(uint id, Guid matchId, Position spawn)
            {
                Respawns.Add((id, matchId, spawn));

                if (Players.TryGetValue(id, out var p) && p.Online && !p.Dying)
                {
                    p.Instance = spawn.Instance;
                    p.Location = new Position(spawn);
                    p.X = spawn.PositionX;
                    p.Y = spawn.PositionY;
                    p.Z = spawn.PositionZ;
                }
            }

            public bool IsInDeathProcess(uint id) => Players.TryGetValue(id, out var p) && p.Online && p.Dying;

            public void ExitMatchFromPen(uint id, string context) { }

            // ---- team fellowships ----

            public int FellowshipCap => 20;

            public void Form(Guid matchId, IReadOnlyList<PvpTeamFellowshipTeam> teams) => Forms.Add((matchId, teams));

            public void Release(Guid matchId, uint characterId, bool restore) { }

            public void Dispose(Guid matchId) { }

            public void Sweep(IReadOnlyCollection<Guid> activeMatchIds) { }

            public bool Got(uint id, string text) => Messages.Any(m => m.Id == id && m.Text == text);
        }

        private sealed class FakeSpaces : IPvpMatchSpaces, IBattlegroundMatchSpaces, IObjectiveMatchSpaces
        {
            public readonly List<MatchSpace> Released = new();

            private ushort next = 0x0400;

            public int Allocations;

            public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<uint> ids)
            {
                Allocations++;
                var instance = Position.InstanceIDFromVars(map.RealmId, next++, isTemporaryRuleset: true);
                return MatchSpaceAllocation.Ok(new MatchSpace(map.MapKey, map.LandblockId, map.RealmId, instance, ids[0]));
            }

            public MatchSpaceReadiness GetReadiness(MatchSpace space) => MatchSpaceReadiness.Ready;

            public void Release(MatchSpace space) => Released.Add(space);

            public BattlegroundFixtureJob SpawnFixtures(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                var job = new BattlegroundFixtureJob();
                job.Complete(pieces.Count);
                return job;
            }

            public BattlegroundFixtureJob SpawnZoneMarkers(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                MarkerCalls++;
                var job = new BattlegroundFixtureJob();
                job.Complete(pieces.Count);
                return job;
            }

            public int MarkerCalls;

            public void RemoveZoneMarkers(MatchSpace space, BattlegroundFixtureJob placed) { }

            public BattlegroundFixtureJob SpawnStartGates(MatchSpace space, IReadOnlyList<BattlegroundSealPiece> pieces)
            {
                var job = new BattlegroundFixtureJob();
                job.Complete(pieces.Count);
                return job;
            }

            public void RemoveStartGates(MatchSpace space, BattlegroundFixtureJob placed) { }

            // ---- the crystals ----

            /// <summary>Placed: the job completes as queued. Pending: the test completes it. Failed: it fails at once.</summary>
            public BattlegroundFixtureStatus ObjectiveOutcome = BattlegroundFixtureStatus.Placed;

            public bool ObjectiveReturnsNull;

            public bool ObjectiveThrows;

            public readonly List<(MatchSpace Space, uint Wcid, AttackDefendPlan Plan, List<BattlegroundObjectiveTag> Tags, BattlegroundFixtureJob Job)> ObjectiveCalls = new();

            public List<CrystalHealth> Samples = new();

            public int SampleCalls;

            public readonly List<(MatchSpace Space, BattlegroundFixtureJob Job, int SizedFor, int Current)> ScaleCalls = new();

            public BattlegroundFixtureJob SpawnObjectives(MatchSpace space, uint wcid, AttackDefendPlan plan, Func<PlannedCrystal, BattlegroundObjectiveTag> tagFactory)
            {
                if (ObjectiveThrows)
                    throw new InvalidOperationException("scripted crystal throw");

                if (ObjectiveReturnsNull)
                    return null;

                var job = new BattlegroundFixtureJob();
                var tags = plan.Crystals.Select(tagFactory).ToList();

                if (ObjectiveOutcome == BattlegroundFixtureStatus.Placed)
                    job.Complete(plan.Count);
                else if (ObjectiveOutcome == BattlegroundFixtureStatus.Failed)
                    job.Fail("scripted crystal failure");

                ObjectiveCalls.Add((space, wcid, plan, tags, job));
                return job;
            }

            /// <summary>Runs inside the tick handler's sample, i.e. AFTER this tick's intent drain: stands in for the landblock thread reporting mid-tick.</summary>
            public Action OnSample;

            public IReadOnlyList<CrystalHealth> SampleObjectives(MatchSpace space, BattlegroundFixtureJob job)
            {
                SampleCalls++;
                OnSample?.Invoke();
                return Samples;
            }

            public void ScaleObjectives(MatchSpace space, BattlegroundFixtureJob job, int sizedForAttackers, int currentAttackers) =>
                ScaleCalls.Add((space, job, sizedForAttackers, currentAttackers));

            public readonly List<(MatchSpace Space, BattlegroundFixtureJob Job, CrystalKillEffect Effect, uint KillerId)> AdjustCalls = new();

            public void AdjustObjective(MatchSpace space, BattlegroundFixtureJob job, CrystalKillEffect effect, uint killerId) =>
                AdjustCalls.Add((space, job, effect, killerId));
        }

        private sealed class FakeSink : IPvpResultSink
        {
            public readonly List<(PvpMatchRecord Match, IReadOnlyList<PvpMatchParticipantRecord> Parts, IReadOnlyList<PvpRatingRecord> Ratings)> Saves = new();

            public List<PvpTemplateRecord> TemplateRows = new() { TemplateRow(TemplateKey, BothModes) };

            public void LoadRatings(Action<List<PvpRatingRecord>> callback) => callback(new List<PvpRatingRecord>());

            public void LoadTemplates(Action<List<PvpTemplateRecord>, PvpTemplateStoreStatus> callback) =>
                callback(TemplateRows.Select(r => r.Clone()).ToList(), PvpTemplateStoreStatus.Ok);

            public void SaveParticipantTemplates(uint dbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps, Action<PvpTemplateStoreStatus> callback) =>
                callback(PvpTemplateStoreStatus.Ok);

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

        private sealed class Rig
        {
            public DateTime Now = T0;
            public readonly FakeGateway Gateway = new();
            public readonly FakeSpaces Spaces = new();
            public readonly FakeSink Sink = new();
            public readonly ConcurrentQueue<PvpIntent> Intents = new();
            public PvpArenaDials Dials = PvpTunables.Defaults with { BloodEnabled = true, CrierEnabled = false };

            /// <summary>Attack/Defend only by default (King of the Hill off), so every match formed here is Attack/Defend.</summary>
            public BattlegroundDials Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, KothEnabled = false };

            /// <summary>The scripted coin flip: true reverses the proposal's team order.</summary>
            public bool Swap;

            /// <summary>The scripted crystal-weenie lookup (the content guard): false means the crystal weenie does not resolve.</summary>
            public bool CrystalResolves = true;

            /// <summary>How many times the coordinator asked the crystal-weenie seam (it caches the answer).</summary>
            public int CrystalLookups;

            public PvpMatchCoordinator Coordinator;

            public Rig Build()
            {
                Coordinator = new PvpMatchCoordinator(Gateway, Spaces, Sink, () => Now, () => Dials, Intents, new NoCrier(), () => Bg);
                Coordinator.AttackDefendSwapSides = () => Swap;
                Coordinator.CrystalWeenieResolves = _ =>
                {
                    CrystalLookups++;
                    return CrystalResolves;
                };
                Coordinator.BeginLoadRatings();
                Coordinator.BeginLoadTemplates();
                Coordinator.BeginLoadLatestTemplates();
                Coordinator.Tick();
                return this;
            }

            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

            public void Tick() => Coordinator.Tick();

            public PvpMatch MatchOf(uint id) => Coordinator.FindMatch(id);

            public void Add(params uint[] ids)
            {
                foreach (var id in ids)
                {
                    if (!Gateway.Players.ContainsKey(id))
                        Gateway.Add(id, $"P{id & 0xFF}");
                }
            }

            /// <summary>Joined one second apart, so the matchmaker's unit order (and so its first split) is the join order.</summary>
            public void JoinAll(params uint[] ids)
            {
                foreach (var id in ids)
                {
                    Assert.IsTrue(Coordinator.Join(id, "bg", false).Joined, $"join 0x{id:X8}");
                    Advance(1);
                }
            }

            public void AcceptAll(params uint[] ids)
            {
                foreach (var id in ids)
                    Assert.AreEqual(PvpAnswerOutcome.Accepted, Coordinator.Accept(id).Outcome, $"accept 0x{id:X8}");
            }

            /// <summary>Joins and forms: the match is left in AwaitingAccept with nobody accepted yet.</summary>
            public PvpMatch Form(params uint[] ids)
            {
                Add(ids);
                JoinAll(ids);
                Tick();

                var m = MatchOf(ids[0]);
                Assert.IsNotNull(m, "formed");
                Assert.AreEqual(PvpMatchState.AwaitingAccept, m.State);
                return m;
            }

            /// <summary>Joins, forms and accepts: the match is left in AwaitingAccept with everyone accepted.</summary>
            public PvpMatch FormAndAccept(params uint[] ids)
            {
                var m = Form(ids);
                AcceptAll(ids);
                return m;
            }

            public PvpMatch DriveToLive(PvpMatch m)
            {
                Tick(); // AwaitingAccept -> Staging, allocate
                Tick(); // ready -> seals -> crystals -> dispatch
                Tick(); // arrived -> Countdown
                Assert.AreEqual(PvpMatchState.Countdown, m.State);
                Advance(Dials.CountdownSeconds);
                Tick(); // -> Live
                Assert.AreEqual(PvpMatchState.Live, m.State);
                return m;
            }

            public void Destroy(PvpMatch m, int index, uint killer = 0)
            {
                Intents.Enqueue(PvpMatchManager.ObjectiveDestroyed(m.MatchId, index, killer, Now));
                Tick();
            }

            public uint[] Team(PvpMatch m, int team) => m.Teams.Single(t => t.TeamIndex == team).Members.Select(p => p.CharacterId).ToArray();
        }

        private static readonly uint[] Four = { A, B, C, D };

        private static Rig LiveAd(out PvpMatch match, Action<Rig> configure = null, uint[] ids = null)
        {
            var rig = new Rig();
            configure?.Invoke(rig);
            rig.Build();

            match = rig.FormAndAccept(ids ?? Four);
            Assert.AreEqual(BattlegroundModes.AttackDefendModeKey, match.ModeKey, "fixture: the match is Attack/Defend");

            rig.DriveToLive(match);
            return rig;
        }

        // ================= sides =================

        /// <summary>
        /// The coin flip decides which proposed team attacks: with the same queue, a flip of false makes the proposal's first team
        /// team 0 (the attackers) and a flip of true makes it team 1, so the two outcomes swap the rosters exactly.
        /// </summary>
        [TestMethod]
        public void SideFlip_BothOutcomes_SwapWhichProposedTeamAttacks()
        {
            uint[] Attackers(bool swap, out uint[] defenders)
            {
                var rig = new Rig { Swap = swap }.Build();
                var m = rig.FormAndAccept(Four);

                Assert.AreEqual(BattlegroundModes.AttackDefendModeKey, m.ModeKey);
                defenders = rig.Team(m, 1);
                return rig.Team(m, 0);
            }

            var keptAttackers = Attackers(false, out var keptDefenders);
            var swappedAttackers = Attackers(true, out var swappedDefenders);

            CollectionAssert.AreEquivalent(keptDefenders, swappedAttackers, "a flip of true makes the other proposed team attack");
            CollectionAssert.AreEquivalent(keptAttackers, swappedDefenders);
            CollectionAssert.AreNotEquivalent(keptAttackers, swappedAttackers, "the flip changed something");
        }

        /// <summary>The team fellowships carry the mode's own team names; King of the Hill's "West Team" is pinned by BattlegroundTeamFellowshipTests.</summary>
        [TestMethod]
        public void TeamFellowships_AreNamedAttackersAndDefenders()
        {
            var rig = LiveAd(out var match);

            var form = rig.Gateway.Forms.Single(f => f.Match == match.MatchId);

            Assert.AreEqual("Attackers Team", form.Teams.Single(t => t.TeamIndex == 0).Name);
            Assert.AreEqual("Defenders Team", form.Teams.Single(t => t.TeamIndex == 1).Name);
        }

        /// <summary>At Live each attacker hears the attacker line (with the planned crystal count) and each defender the defender line, exactly once.</summary>
        [TestMethod]
        public void Live_SendsEachSideItsRoleLine_Once()
        {
            // Any-order play (pvp_bg_ad_sequential_crystals off) keeps the original role lines.
            var rig = LiveAd(out var match, r => r.Bg = r.Bg with { AdSequentialCrystals = false });
            var plan = rig.Spaces.ObjectiveCalls.Single().Plan;
            var total = plan.Count;
            var list = BattlegroundText.CrystalList(plan.Crystals.Select(c => c.Site));
            Assert.IsTrue(list.StartsWith("Crystals: Great Hall ("), "fixture: the list names the planned sites with their hints");
            var attackerLine = $"[Battleground] You are an Attacker. Destroy all {total} Warding Crystals before time runs out! {list}";
            var defenderLine = $"[Battleground] You are a Defender. Protect the Warding Crystals until time runs out! {list}";

            rig.Tick();

            foreach (var id in rig.Team(match, 0))
            {
                Assert.AreEqual(1, rig.Gateway.Messages.Count(m => m.Id == id && m.Text == attackerLine), $"attacker 0x{id:X8}");
                Assert.IsFalse(rig.Gateway.Got(id, defenderLine));
            }

            foreach (var id in rig.Team(match, 1))
            {
                Assert.AreEqual(1, rig.Gateway.Messages.Count(m => m.Id == id && m.Text == defenderLine), $"defender 0x{id:X8}");
                Assert.IsFalse(rig.Gateway.Got(id, attackerLine));
            }
        }

        // ================= sequential crystals =================

        /// <summary>
        /// Sequential mode (the default) at Live: each side hears the numbered "in order" line for the planned sites, exactly once. Reverting
        /// the handler's role-line branch makes this fail on the old text.
        /// </summary>
        [TestMethod]
        public void Sequential_Live_SendsEachSideTheOrderedRoleLine_Once()
        {
            var rig = LiveAd(out var match);
            var sites = rig.Spaces.ObjectiveCalls.Single().Plan.Crystals.Select(c => c.Site).ToList();
            var attackerLine = "[Battleground] You are an Attacker. Destroy the Warding Crystals in order: 1. Great Hall (upper level, south-east of the octagon), 2. West Cavern (lower level, south from the octagon past the Defender room, then down the stairs), 3. Pit Hall (bottom of the spiral stair, south-east of the West Cavern) before time runs out!";
            var defenderLine = attackerLine.Replace("You are an Attacker. Destroy", "You are a Defender. Protect").Replace("before time runs out!", "until time runs out!");

            Assert.AreEqual(attackerLine, BattlegroundText.RoleAttackerOrdered(sites), "fixture: the helper builds the owner's line");

            rig.Tick();

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual(1, rig.Gateway.Messages.Count(m => m.Id == id && m.Text == attackerLine), $"attacker 0x{id:X8}");

            foreach (var id in rig.Team(match, 1))
                Assert.AreEqual(1, rig.Gateway.Messages.Count(m => m.Id == id && m.Text == defenderLine), $"defender 0x{id:X8}");
        }

        /// <summary>
        /// The role line goes out when the countdown BEGINS (players read it while the start gates hold them), exactly once per player, and
        /// is not sent again at Live. Reverting to the Live-time send fails the first assert; sending at both fails the last.
        /// </summary>
        [TestMethod]
        public void RoleLines_AreSentAtCountdownStart_Once_AndNotRepeatedAtLive()
        {
            var rig = new Rig().Build();
            var match = rig.FormAndAccept(Four);

            rig.Tick(); // AwaitingAccept -> Staging
            rig.Tick(); // ready -> dispatch
            rig.Tick(); // arrived -> Countdown
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            int RoleCount(uint id) => rig.Gateway.Messages.Count(m => m.Id == id && (m.Text.StartsWith("[Battleground] You are an Attacker") || m.Text.StartsWith("[Battleground] You are a Defender")));

            foreach (var id in Four)
                Assert.AreEqual(1, RoleCount(id), $"0x{id:X8} read the role line during the countdown");

            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Text == PvpArenaText.Start), "fixture: 'go' has not been sent yet");

            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);
            rig.Tick();

            foreach (var id in Four)
                Assert.AreEqual(1, RoleCount(id), $"0x{id:X8} is not told again at Live");
        }

        /// <summary>
        /// A confirmed respawn in sequential mode tells the returning player which crystal is vulnerable; any-order play says nothing.
        /// </summary>
        [TestMethod]
        public void Respawn_Sequential_TellsThePlayerTheVulnerableCrystal()
        {
            const string firstLine = "[Battleground] Vulnerable crystal: Great Hall (upper level, south-east of the octagon).";

            foreach (var sequential in new[] { true, false })
            {
                var rig = LiveAd(out var match, r => r.Bg = r.Bg with { AdSequentialCrystals = sequential });
                var victim = rig.Team(match, 0)[0];

                rig.Gateway.Players[victim].Dying = true;
                rig.Intents.Enqueue(PvpMatchManager.Death(victim, match.MatchId, rig.Now, rig.Team(match, 1)[0]));
                rig.Tick();
                rig.Gateway.Players[victim].Dying = false;
                rig.Tick(); // in the pen
                rig.Advance(rig.Bg.AdRespawnSecondsAttackers);
                rig.Tick(); // respawn queued
                rig.Advance(1);
                rig.Tick(); // confirmed

                Assert.AreEqual(sequential, rig.Gateway.Got(victim, firstLine), sequential ? "told which crystal is vulnerable" : "any-order play has nothing to say");
                Assert.AreEqual(sequential ? 1 : 0, rig.Gateway.Messages.Count(m => m.Id == victim && m.Text.StartsWith("[Battleground] Vulnerable crystal")));
            }
        }

        /// <summary>The match carries the plan's sequence (the object the gate reads), set at formation; the dial off leaves it null.</summary>
        [TestMethod]
        public void Sequential_FormationSetsTheMatchSequence_FromTheSnapshottedDial()
        {
            var on = LiveAd(out var matchOn);
            var plan = on.Spaces.ObjectiveCalls.Single().Plan;

            Assert.IsNotNull(matchOn.CrystalSequence);
            Assert.AreSame(plan.Sequence, matchOn.CrystalSequence, "the gate and the tick handler share one sequence");
            Assert.AreEqual(0, matchOn.CrystalSequence.Current, "the first site is the vulnerable one");

            LiveAd(out var matchOff, r => r.Bg = r.Bg with { AdSequentialCrystals = false });

            Assert.IsNull(matchOff.CrystalSequence, "sequential off: any-order play, nothing sealed");
        }

        /// <summary>
        /// The dial is snapshotted at formation: flipping it after the match formed changes nothing for that match (the sequence was
        /// built from the formation snapshot).
        /// </summary>
        [TestMethod]
        public void Sequential_DialFlippedAfterFormation_DoesNotAffectTheRunningMatch()
        {
            var rig = new Rig().Build();
            var match = rig.FormAndAccept(Four);

            rig.Bg = rig.Bg with { AdSequentialCrystals = false };
            rig.DriveToLive(match);

            Assert.IsNotNull(match.CrystalSequence, "formed sequential, stays sequential");
        }

        /// <summary>
        /// End to end through the real coordinator: destroying crystal 0 moves the match's vulnerable crystal to 1 and everyone hears the
        /// destroyed line and then the unlock line; destroying 1 and 2 follows, with no unlock line after the last.
        /// </summary>
        [TestMethod]
        public void Sequential_DestroyingTheCurrentCrystal_UnlocksTheNextOne_AndBroadcastsIt()
        {
            var rig = LiveAd(out var match);
            var everyone = rig.Gateway.Players.Keys.First();

            rig.Destroy(match, 0, rig.Team(match, 0)[0]);

            Assert.AreEqual(1, match.CrystalSequence.Current);
            Assert.IsTrue(rig.Gateway.Got(everyone, "[Battleground] The West Cavern crystal (lower level, south from the octagon past the Defender room, then down the stairs) is now vulnerable!"));

            rig.Destroy(match, 1, rig.Team(match, 0)[0]);

            Assert.AreEqual(2, match.CrystalSequence.Current);
            Assert.IsTrue(rig.Gateway.Got(everyone, "[Battleground] The Pit Hall crystal (bottom of the spiral stair, south-east of the West Cavern) is now vulnerable!"));
        }

        /// <summary>King of the Hill sends no role line.</summary>
        [TestMethod]
        public void Live_Koth_SendsNoRoleLine()
        {
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, AdEnabled = false } }.Build();
            var match = rig.FormAndAccept(Four);
            Assert.AreEqual(BattlegroundModes.KothModeKey, match.ModeKey);
            rig.DriveToLive(match);

            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Text.Contains("You are an Attacker") || m.Text.Contains("You are a Defender")));
        }

        // ================= crystal staging =================

        /// <summary>
        /// Staging waits on the crystals after the seals: nobody is dispatched while the job is pending, then dispatch follows. The
        /// placement carries the map's crystal wcid and a plan sized for the attackers, and every tag names this match, its crystal
        /// index and the defending team.
        /// </summary>
        [TestMethod]
        public void Crystals_PlacedBeforeDispatch_TaggedForTheMatch()
        {
            var rig = new Rig().Build();
            rig.Spaces.ObjectiveOutcome = BattlegroundFixtureStatus.Pending;
            var m = rig.FormAndAccept(Four);

            rig.Tick(); // Staging, allocate
            rig.Tick(); // seals placed, crystals queued and pending

            var call = rig.Spaces.ObjectiveCalls.Single();
            Assert.AreEqual(BattlegroundMapCatalog.Bg003c.CrystalWcid, call.Wcid);
            Assert.AreSame(rig.Coordinator.SpaceOf(m.MatchId), call.Space, "into the match's own space");
            Assert.AreEqual(2, call.Plan.AttackerCount, "sized for the two attackers");
            Assert.AreEqual(rig.Bg.AdCrystalHealthPerAttacker * 2, call.Plan.HealthPerCrystal);
            Assert.AreEqual(call.Plan.Count, call.Tags.Count);
            Assert.IsTrue(call.Plan.Count > 0);

            for (var i = 0; i < call.Tags.Count; i++)
                Assert.AreEqual(new BattlegroundObjectiveTag(m.MatchId, call.Plan.Crystals[i].Index, CrystalWinCondition.DefenderTeam), call.Tags[i]);

            Assert.AreEqual(0, rig.Gateway.Entered.Count, "nobody dispatched before the crystals are placed");

            rig.Tick();
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "still waiting");
            Assert.AreEqual(1, rig.Spaces.ObjectiveCalls.Count, "not re-queued");

            call.Job.Complete(call.Plan.Count);
            rig.Tick();

            Assert.AreEqual(4, rig.Gateway.Entered.Count, "dispatched once placed");
        }

        /// <summary>A failed, unqueued or throwing crystal placement cancels with no fault: the space is released, everyone is back in bg, nobody is locked out.</summary>
        [DataTestMethod]
        [DataRow("failed")]
        [DataRow("null")]
        [DataRow("throws")]
        public void Crystals_PlacementFailure_CancelsWithoutFault(string how)
        {
            var rig = new Rig().Build();

            switch (how)
            {
                case "failed": rig.Spaces.ObjectiveOutcome = BattlegroundFixtureStatus.Failed; break;
                case "null": rig.Spaces.ObjectiveReturnsNull = true; break;
                default: rig.Spaces.ObjectiveThrows = true; break;
            }

            var m = rig.FormAndAccept(Four);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, m.State);
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "nobody placed");
            Assert.AreEqual(1, rig.Spaces.Released.Count, "the space is released");
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Canceled));
            CollectionAssert.AreEquivalent(Four, rig.Coordinator.QueuedIds(BattlegroundModes.RoomKey).ToArray(), "everyone back in bg");
            Assert.IsFalse(Four.Any(rig.Coordinator.IsLockedOut), "no-fault: nobody locked out");
        }

        // ================= destruction and winning =================

        /// <summary>Every crystal destroyed is an attacker win on score: rated on the battleground ladder, team 0 wins, the attackers' line goes out, Marks are paid from the battleground ledger.</summary>
        [TestMethod]
        public void AllCrystalsDestroyed_IsAnAttackerWin_OnScore()
        {
            var rig = LiveAd(out var match);
            var count = rig.Spaces.ObjectiveCalls.Single().Plan.Count;

            for (var i = 0; i < count - 1; i++)
                rig.Destroy(match, i, rig.Team(match, 0)[0]);

            Assert.AreEqual(PvpMatchState.Live, match.State, "one crystal still stands");
            Assert.AreEqual(count - 1, match.ScoreBoard[0]);

            rig.Destroy(match, count - 1);

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            var save = rig.Sink.Saves.Single();
            Assert.AreEqual(nameof(EndReason.Score), save.Match.EndReason);
            Assert.AreEqual(BattlegroundModes.AttackDefendModeKey, save.Match.Mode);
            Assert.AreEqual(BattlegroundModes.LadderKey, save.Match.Ladder);
            Assert.IsTrue(save.Match.Rated);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual("win", save.Parts.Single(p => p.CharacterId == id).Result);

            foreach (var id in rig.Team(match, 1))
                Assert.AreEqual("loss", save.Parts.Single(p => p.CharacterId == id).Result);

            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Text == BattlegroundText.AttackersWin()), "the attackers' result line");
            // Attack/Defend is a battleground mode: it pays Marks of the Hopeslayer from the battleground ledger (2026-10-08), never arena Blood.
            Assert.AreEqual(match.Teams.Sum(t => t.Members.Count), rig.Gateway.BloodGrants.Count, "every seat of the match is granted Marks");
            Assert.IsTrue(rig.Gateway.BloodGrants.All(g => g.Grant.Ledger == PvpBloodLedgerKind.Battleground), "from the battleground ledger");
            Assert.IsTrue(rig.Gateway.BloodGrants.Where(g => rig.Team(match, 0).Contains(g.Id)).All(g => g.Amount == rig.Bg.MarksWin), "the winners are paid the battleground win amount");
            Assert.IsTrue(rig.Gateway.BloodGrants.Where(g => rig.Team(match, 1).Contains(g.Id)).All(g => g.Amount == rig.Bg.MarksLoss), "the losers the loss amount");
        }

        /// <summary>The time limit with a crystal standing is a RATED defender win, never a draw.</summary>
        [TestMethod]
        public void Timeout_IsARatedDefenderWin()
        {
            var rig = LiveAd(out var match);
            rig.Destroy(match, 0);

            rig.Advance(rig.Bg.TimeLimitSecondsAd - 1);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State, "not before the time limit");

            rig.Advance(1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            var save = rig.Sink.Saves.Single();
            Assert.AreEqual(nameof(EndReason.Timeout), save.Match.EndReason);
            Assert.IsTrue(save.Match.Rated, "a defender timeout win is rated");
            Assert.AreEqual(4, save.Ratings.Count, "every player rated");

            foreach (var id in rig.Team(match, 1))
                Assert.AreEqual("win", save.Parts.Single(p => p.CharacterId == id).Result);

            foreach (var id in rig.Team(match, 0))
                Assert.AreEqual("loss", save.Parts.Single(p => p.CharacterId == id).Result);

            var total = rig.Spaces.ObjectiveCalls.Single().Plan.Count;
            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Text == $"[Battleground] Time is up! The Defenders held {total - 1} of {total} crystals."), "the partial wording: crystal 0 fell");
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Text == BattlegroundText.DefendersWin()), "not the every-crystal line");
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Text.StartsWith("[Battleground] Time is up.")), "never the King of the Hill timeout line");
        }

        /// <summary>
        /// The clock edge (review F6): the last crystal falls on the landblock thread AFTER this tick's intent drain, on the very tick the
        /// time limit is reached. It still counts before the clock check, so the attackers win on score; a crystal report for another
        /// match taken off the queue meanwhile is kept and handled on the next drain, not lost.
        /// </summary>
        [TestMethod]
        public void LastCrystalFallingMidTick_AtTheTimeLimit_IsStillAnAttackerWin()
        {
            var rig = LiveAd(out var match);
            var count = rig.Spaces.ObjectiveCalls.Single().Plan.Count;

            for (var i = 0; i < count - 1; i++)
                rig.Destroy(match, i);

            var otherMatch = Guid.NewGuid();
            var fired = false;
            rig.Spaces.OnSample = () =>
            {
                if (fired)
                    return;

                fired = true;
                rig.Intents.Enqueue(PvpMatchManager.ObjectiveDestroyed(otherMatch, 0, 0, rig.Now));
                rig.Intents.Enqueue(PvpMatchManager.ObjectiveDestroyed(match.MatchId, count - 1, 0, rig.Now));
            };

            rig.Advance(rig.Bg.TimeLimitSecondsAd);
            rig.Tick();

            Assert.IsTrue(fired, "fixture: the report arrived mid-tick");
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(nameof(EndReason.Score), rig.Sink.Saves.Single().Match.EndReason, "the crystal counted before the clock");
            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Text == BattlegroundText.AttackersWin()));

            rig.Tick();
            Assert.AreEqual(1, rig.Sink.Saves.Count, "the deferred report for another match changed nothing here, and was drained without fault");
        }

        /// <summary>
        /// Review N2: the intents the mid-tick crystal drain takes off the queue and does NOT handle are kept, in order. Two real seat
        /// deaths arrive mid-tick around a crystal report: the crystal counts on this tick, and both deaths take effect on the next drain,
        /// first victim first.
        /// </summary>
        [TestMethod]
        public void MidTickDrain_DefersSeatIntents_InOrder_AndTheyTakeEffectNextTick()
        {
            var rig = LiveAd(out var match);
            var attackers = rig.Team(match, 0);
            var defenders = rig.Team(match, 1);
            uint first = attackers[0], second = defenders[0];
            var fired = false;

            rig.Spaces.OnSample = () =>
            {
                if (fired)
                    return;

                fired = true;
                rig.Gateway.Players[first].Dying = true;
                rig.Gateway.Players[second].Dying = true;
                rig.Intents.Enqueue(PvpMatchManager.Death(first, match.MatchId, rig.Now, defenders[1]));
                rig.Intents.Enqueue(PvpMatchManager.ObjectiveDestroyed(match.MatchId, 0, 0, rig.Now));
                rig.Intents.Enqueue(PvpMatchManager.Death(second, match.MatchId, rig.Now, attackers[1]));
            };

            rig.Tick();

            Assert.IsTrue(fired, "fixture: the intents arrived mid-tick");
            Assert.AreEqual(1, match.ScoreBoard[0], "the crystal counted on this tick");

            var firstLine = BattlegroundText.Slain($"P{first & 0xFF}", $"P{defenders[1] & 0xFF}");
            var secondLine = BattlegroundText.Slain($"P{second & 0xFF}", $"P{attackers[1] & 0xFF}");
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Text == firstLine || m.Text == secondLine), "the deaths were deferred, not handled out of turn");

            rig.Tick();

            var seen = rig.Gateway.Messages.Where(m => m.Id == attackers[1] && (m.Text == firstLine || m.Text == secondLine)).Select(m => m.Text).ToList();
            CollectionAssert.AreEqual(new[] { firstLine, secondLine }, seen, "both deaths took effect, in arrival order");
            Assert.IsTrue(rig.Gateway.Published.Any(p => p.Id == first && p.Binding.Respawning), "the first victim is respawning");
            Assert.IsTrue(rig.Gateway.Published.Any(p => p.Id == second && p.Binding.Respawning), "the second victim is respawning");
            Assert.AreEqual(1, match.TeamKills[1]);
            Assert.AreEqual(1, match.TeamKills[0]);
        }

        /// <summary>
        /// Review N2, two live matches: match A's mid-tick drain takes match B's crystal report off the queue and defers it; match B's own
        /// drain, later in the SAME tick, still finds and counts it.
        /// </summary>
        [TestMethod]
        public void MidTickDrain_AnotherMatchsCrystalReport_IsCountedByThatMatch()
        {
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, KothEnabled = false, MaxConcurrentMatches = 2 } }.Build();
            var matchA = rig.FormAndAccept(A, B, C, D);
            rig.DriveToLive(matchA);
            var matchB = rig.FormAndAccept(E, F, G, H);
            rig.DriveToLive(matchB);
            Assert.AreEqual(BattlegroundModes.AttackDefendModeKey, matchB.ModeKey);

            var fired = false;

            // The first sample of the tick is match A's (matches advance in formation order); B's report lands before A's drain.
            rig.Spaces.OnSample = () =>
            {
                if (fired)
                    return;

                fired = true;
                rig.Intents.Enqueue(PvpMatchManager.ObjectiveDestroyed(matchB.MatchId, 0, 0, rig.Now));
            };

            rig.Tick();

            Assert.IsTrue(fired);
            Assert.AreEqual(0, matchA.ScoreBoard[0], "never counted for the wrong match");
            Assert.AreEqual(1, matchB.ScoreBoard[0], "match B counted its own report on the same tick");

            rig.Tick();
            Assert.AreEqual(1, matchB.ScoreBoard[0], "and only once");
        }

        /// <summary>A timeout with every crystal standing keeps the every-crystal line.</summary>
        [TestMethod]
        public void Timeout_NothingDestroyed_DefendersHeldEveryCrystal()
        {
            var rig = LiveAd(out var match);

            rig.Advance(rig.Bg.TimeLimitSecondsAd);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Messages.Any(m => m.Text == "[Battleground] The Defenders held every crystal until time ran out!"));
        }

        /// <summary>
        /// The destroy intent is honoured only while Live and only once per crystal: a duplicate, an out-of-range index, a report
        /// before Live and a report after the match resolved all change nothing (and never throw).
        /// </summary>
        [TestMethod]
        public void LateDuplicateOrForeignDestroy_IsIgnored()
        {
            var rig = new Rig().Build();
            var match = rig.FormAndAccept(Four);
            rig.Tick();
            rig.Tick();
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            rig.Destroy(match, 0);
            Assert.AreEqual(0, match.ScoreBoard[0], "a report before Live is ignored");

            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);

            var count = rig.Spaces.ObjectiveCalls.Single().Plan.Count;

            rig.Destroy(match, 0);
            rig.Destroy(match, 0);
            Assert.AreEqual(1, match.ScoreBoard[0], "a duplicate is counted once");

            rig.Destroy(match, count);
            rig.Destroy(match, -1);
            Assert.AreEqual(1, match.ScoreBoard[0], "an index outside the plan is ignored");

            var destroyedLines = rig.Gateway.Messages.Count(m => m.Id == A && m.Text.Contains("crystal") && m.Text.Contains("has been destroyed"));
            Assert.AreEqual(1, destroyedLines, "announced once");

            rig.Intents.Enqueue(PvpMatchManager.ObjectiveDestroyed(Guid.NewGuid(), 1, 0, rig.Now));
            rig.Tick();
            Assert.AreEqual(1, match.ScoreBoard[0], "another match's report never counts here");

            for (var i = 1; i < count; i++)
                rig.Destroy(match, i);

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            var resolved = match.ScoreBoard[0];

            rig.Destroy(match, 0);
            rig.Destroy(match, count - 1);
            Assert.AreEqual(resolved, match.ScoreBoard[0], "a report after the match resolved is ignored");
            Assert.AreEqual(1, rig.Sink.Saves.Count, "resolved once");
        }

        /// <summary>Respawn is per side: the attackers' pen time comes from pvp_bg_ad_respawn_seconds_attackers, not the King of the Hill delay.</summary>
        [TestMethod]
        public void AttackerRespawn_UsesTheAttackerDelay()
        {
            var rig = LiveAd(out var match, r => r.Bg = r.Bg with { AdRespawnSecondsAttackers = 7, AdRespawnSecondsDefenders = 50, RespawnSeconds = 30 });
            var victim = rig.Team(match, 0)[0];

            rig.Gateway.Players[victim].Dying = true;
            rig.Intents.Enqueue(PvpMatchManager.Death(victim, match.MatchId, rig.Now, rig.Team(match, 1)[0]));
            rig.Tick();
            rig.Gateway.Players[victim].Dying = false;
            rig.Tick(); // in the pen

            rig.Advance(6);
            rig.Tick();
            Assert.AreEqual(0, rig.Gateway.Respawns.Count, "not before the attacker delay");

            rig.Advance(1);
            rig.Tick();
            Assert.AreEqual(1, rig.Gateway.Respawns.Count, "respawned at the attacker delay");
            Assert.AreEqual(victim, rig.Gateway.Respawns[0].Id);
        }

        /// <summary>Spawn protection covers every battleground mode: an Attack/Defend respawn is protected too, from confirm, for the snapshotted window.</summary>
        [TestMethod]
        public void SpawnProtection_AttackDefendRespawn_IsProtected()
        {
            var rig = LiveAd(out var match);
            var victim = rig.Team(match, 0)[0];

            rig.Gateway.Players[victim].Dying = true;
            rig.Intents.Enqueue(PvpMatchManager.Death(victim, match.MatchId, rig.Now, rig.Team(match, 1)[0]));
            rig.Tick();
            rig.Gateway.Players[victim].Dying = false;
            rig.Tick(); // in the pen
            rig.Advance(rig.Bg.AdRespawnSecondsAttackers);
            rig.Tick(); // respawn queued
            rig.Advance(1);
            rig.Tick(); // confirmed

            var binding = rig.Gateway.Published.Last(p => p.Id == victim).Binding;

            Assert.IsFalse(binding.Respawning, "fixture: confirmed");
            Assert.IsTrue(binding.ProtectedInRoom, "the Mines have spawn rooms: protected while in the room, no end time yet");
            Assert.IsNull(binding.ProtectedUntilUtc, "the window starts when the player leaves the room");
            Assert.AreEqual(3, binding.ProtectionSeconds, "the snapshotted window after leaving");
            CollectionAssert.AreEquivalent(BattlegroundMapCatalog.Bg003c.SpawnRoomSetFor(0).ToList(), binding.SpawnRoomCells.ToList(), "the attackers' own room");
            Assert.IsTrue(rig.Gateway.Got(victim, "[Battleground] You are protected while in your spawn room and for 3 seconds after you leave. Attacking ends it."));
            Assert.IsFalse(rig.Gateway.Got(victim, "[Battleground] You are protected for 3 seconds. Attacking ends it early."), "not the plain wording");
        }

        /// <summary>
        /// A seat that walked out of its own spawn room during the countdown gets no protection and no message at Live; its teammates
        /// still do. The other seats' honest positions are in their rooms.
        /// </summary>
        [TestMethod]
        public void SpawnProtection_AtMatchStart_ASeatOutsideItsRoomGetsNothing()
        {
            var rig = new Rig().Build();
            var match = rig.FormAndAccept(Four);

            rig.Tick(); // AwaitingAccept -> Staging
            rig.Tick(); // dispatch
            rig.Tick(); // arrived -> Countdown
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            var defenders = rig.Team(match, 1);
            var walker = defenders[0];
            var stayer = defenders.Length > 1 ? defenders[1] : rig.Team(match, 0)[0];

            // outside the room by cell (the stub 0x01DE), and a second walker whose cell is in the room but whose coordinates are not
            rig.Gateway.Players[walker].Location = new Position(0x003C01DE, 77f, -80f, -5.995f, 0f, 0f, 0f, 1f, rig.Gateway.Players[walker].Location.Instance);

            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);

            var binding = rig.Gateway.Published.Last(p => p.Id == walker).Binding;
            Assert.IsFalse(binding.HasSpawnProtection, "walked out during the countdown: no protection");
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Id == walker && m.Text.Contains("protected")), "and no message");
            Assert.IsTrue(rig.Gateway.Published.Last(p => p.Id == stayer).Binding.ProtectedInRoom, "a seat still in its room is protected as before");
        }

        /// <summary>The same walker, lying: the in-room cell id with coordinates outside the footprint is outside too.</summary>
        [TestMethod]
        public void SpawnProtection_AtMatchStart_AnInRoomCellWithOutsideCoordinatesGetsNothing()
        {
            var rig = new Rig().Build();
            var match = rig.FormAndAccept(Four);

            rig.Tick();
            rig.Tick();
            rig.Tick();

            var liar = rig.Team(match, 1)[0];
            rig.Gateway.Players[liar].Location = new Position(0x003C01D1, 140f, -20f, -5.995f, 0f, 0f, 0f, 1f, rig.Gateway.Players[liar].Location.Instance);

            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();

            Assert.IsFalse(rig.Gateway.Published.Last(p => p.Id == liar).Binding.HasSpawnProtection);
        }

        /// <summary>Match start on the Mines: every seat is protected in its own team's room, with the room wording.</summary>
        [TestMethod]
        public void SpawnProtection_AtMatchStart_EverySeatIsProtectedInItsOwnRoom()
        {
            var rig = LiveAd(out var match);

            foreach (var team in new[] { 0, 1 })
            {
                foreach (var id in rig.Team(match, team))
                {
                    var binding = rig.Gateway.Published.Last(p => p.Id == id).Binding;

                    Assert.IsTrue(binding.ProtectedInRoom, $"0x{id:X8} (team {team}) is protected in room");
                    Assert.IsNull(binding.ProtectedUntilUtc);
                    CollectionAssert.AreEquivalent(BattlegroundMapCatalog.Bg003c.SpawnRoomSetFor(team).ToList(), binding.SpawnRoomCells.ToList(), "its own team's room");
                    Assert.AreEqual(1, rig.Gateway.Messages.Count(m => m.Id == id && m.Text == "[Battleground] You are protected while in your spawn room and for 3 seconds after you leave. Attacking ends it."));
                }
            }

            var off = LiveAd(out var offMatch, r => r.Bg = r.Bg with { SpawnProtectionSeconds = 0 });
            Assert.IsTrue(off.Team(offMatch, 0).All(id => !off.Gateway.Published.Last(p => p.Id == id).Binding.HasSpawnProtection), "dial 0 disables it");
        }

        // ================= mode picking =================

        /// <summary>With both modes enabled and the seats' template offered for both, consecutive matches rotate King of the Hill, then Attack/Defend.</summary>
        [TestMethod]
        public void Picker_RotatesTheEnabledModes()
        {
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, MaxConcurrentMatches = 2 } }.Build();

            var first = rig.FormAndAccept(A, B, C, D);
            var second = rig.FormAndAccept(E, F, G, H);

            Assert.AreEqual(BattlegroundModes.KothModeKey, first.ModeKey);
            Assert.AreEqual(BattlegroundModes.AttackDefendModeKey, second.ModeKey);
        }

        /// <summary>A template not offered for Attack/Defend keeps every match on King of the Hill: the rotation skips a mode the seats cannot play.</summary>
        [TestMethod]
        public void Picker_SkipsAModeTheTemplateIsNotOfferedFor()
        {
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, MaxConcurrentMatches = 2 } };
            rig.Sink.TemplateRows = new List<PvpTemplateRecord> { TemplateRow(TemplateKey, "1v1,2v2,ffa,bg_koth") };
            rig.Build();

            var first = rig.FormAndAccept(A, B, C, D);
            var second = rig.FormAndAccept(E, F, G, H);

            Assert.AreEqual(BattlegroundModes.KothModeKey, first.ModeKey);
            Assert.AreEqual(BattlegroundModes.KothModeKey, second.ModeKey, "Attack/Defend is not offered to these seats");
        }

        private const string KothOnly = "t_koth", AdOnly = "t_ad";

        /// <summary>A rig offering T1 (King of the Hill only) and T2 (Attack/Defend only), both modes enabled, room for two matches.</summary>
        private static Rig SplitTemplateRig(bool crystalResolves = true)
        {
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, MaxConcurrentMatches = 2 }, CrystalResolves = crystalResolves };
            rig.Sink.TemplateRows = new List<PvpTemplateRecord> { TemplateRow(KothOnly, "bg_koth"), TemplateRow(AdOnly, "bg_ad") };
            return rig.Build();
        }

        private static void AddWith(Rig rig, string template, params uint[] ids)
        {
            foreach (var id in ids)
                rig.Gateway.Add(id, $"P{id & 0xFF}", template);
        }

        /// <summary>
        /// T1 (King of the Hill only) and T2 (Attack/Defend only) seats each get the mode their template is offered for, whatever the
        /// rotation's cursor says, so neither match loses a seat to the template check at dispatch: all four of each are dispatched.
        /// </summary>
        [TestMethod]
        public void Picker_KothOnlyAndAdOnlyTemplates_NeverCancelAtDispatch()
        {
            var rig = SplitTemplateRig();
            AddWith(rig, AdOnly, A, B, C, D);
            AddWith(rig, KothOnly, E, F, G, H);

            // The cursor starts on King of the Hill; these seats can only play Attack/Defend.
            var ad = rig.FormAndAccept(A, B, C, D);
            Assert.AreEqual(BattlegroundModes.AttackDefendModeKey, ad.ModeKey);
            rig.DriveToLive(ad);

            // These seats can only play King of the Hill.
            var koth = rig.FormAndAccept(E, F, G, H);
            Assert.AreEqual(BattlegroundModes.KothModeKey, koth.ModeKey);
            rig.DriveToLive(koth);

            Assert.AreEqual(8, rig.Gateway.Entered.Count, "every seat dispatched");
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Text == PvpArenaText.OtherCouldNotEnter || m.Text == PvpArenaText.Canceled), "nobody was canceled by the pick");
        }

        /// <summary>
        /// A mixed match no mode is offered to in full picks the mode offered to the MOST seats (three T2 seats against one T1): Attack/Defend,
        /// not the first enabled mode, so one seat is withdrawn (without fault, by the accept-time template re-check) rather than three.
        /// </summary>
        [TestMethod]
        public void Picker_NoModeOfferedToEverySeat_PicksTheModeOfferedToTheMost()
        {
            var rig = SplitTemplateRig();
            AddWith(rig, AdOnly, A, B, C);
            AddWith(rig, KothOnly, D);

            var m = rig.Form(A, B, C, D);

            Assert.AreEqual(BattlegroundModes.AttackDefendModeKey, m.ModeKey, "three seats are offered Attack/Defend, one King of the Hill");

            Assert.AreEqual(PvpAnswerOutcome.Declined, rig.Coordinator.Accept(D).Outcome, "the one seat not offered the pick is withdrawn");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(D), "without fault");
        }

        /// <summary>The content guard: a crystal weenie that does not resolve keeps the rotation off Attack/Defend, even for seats offered both.</summary>
        [TestMethod]
        public void Picker_MissingCrystalWeenie_PicksKoth()
        {
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, MaxConcurrentMatches = 2 }, CrystalResolves = false }.Build();

            var first = rig.FormAndAccept(A, B, C, D);
            var second = rig.FormAndAccept(E, F, G, H);

            Assert.AreEqual(BattlegroundModes.KothModeKey, first.ModeKey);
            Assert.AreEqual(BattlegroundModes.KothModeKey, second.ModeKey, "the rotation would have picked Attack/Defend here; the guard skipped it");

            // Seats offered only Attack/Defend, with its crystal unresolved: no playable mode is offered to them, so they cannot even queue,
            // and no doomed King of the Hill match forms around them.
            var adOnly = SplitTemplateRig(crystalResolves: false);
            AddWith(adOnly, AdOnly, A, B, C, D);

            foreach (var id in new[] { A, B, C, D })
                Assert.IsFalse(adOnly.Coordinator.Join(id, "bg", false).Joined, $"0x{id:X8} is offered no playable mode");

            adOnly.Tick();
            Assert.IsNull(adOnly.MatchOf(A), "no match formed");
            Assert.AreEqual(0, adOnly.Spaces.Allocations, "nothing allocated");
        }

        /// <summary>
        /// Review N1: King of the Hill off and the crystal unresolved leaves NO playable battleground mode. The room reports disabled at
        /// the join, nothing forms or allocates over many ticks; re-enabling King of the Hill reopens it and a King of the Hill match forms.
        /// </summary>
        [TestMethod]
        public void NoPlayableMode_RoomDisabled_NothingForms_UntilKothIsReEnabled()
        {
            var rig = new Rig { CrystalResolves = false }.Build();
            Assert.IsFalse(rig.Bg.KothEnabled, "fixture: King of the Hill off");
            rig.Add(Four);

            foreach (var id in Four)
                Assert.AreEqual(PvpJoinRefusal.ModeDisabled, rig.Coordinator.Join(id, "bg", false).Refusal, $"0x{id:X8}");

            for (var i = 0; i < 5; i++)
            {
                rig.Advance(15);
                rig.Tick();
            }

            Assert.IsNull(rig.MatchOf(A));
            Assert.AreEqual(0, rig.Spaces.Allocations, "nothing allocated");
            Assert.AreEqual(0, rig.Spaces.ObjectiveCalls.Count);

            rig.Bg = rig.Bg with { KothEnabled = true };
            var koth = rig.FormAndAccept(Four);

            Assert.AreEqual(BattlegroundModes.KothModeKey, koth.ModeKey);
            rig.DriveToLive(koth);
            Assert.AreEqual(1, rig.Spaces.Allocations);
        }

        /// <summary>
        /// Review R1: the default seam's rule. A cached hit never reads; a cached miss (the world cache's remembered null) falls through to
        /// a real read, so a crystal weenie applied after the first lookup is found on the next refresh. The real read itself
        /// (WorldDatabaseWithEntityCache.GetWeenie overwriting the cached null) needs the world database and is not reachable here.
        /// </summary>
        [TestMethod]
        public void CrystalWeenieDefaultRule_ACachedMissFallsThroughToARealRead()
        {
            var reads = 0;

            Assert.IsTrue(PvpMatchCoordinator.CrystalWeenieResolvesWithRefresh(1006805, _ => true, _ => { reads++; return false; }));
            Assert.AreEqual(0, reads, "a cached hit never reads");

            Assert.IsTrue(PvpMatchCoordinator.CrystalWeenieResolvesWithRefresh(1006805, _ => false, _ => { reads++; return true; }), "applied since the cached miss: found");
            Assert.AreEqual(1, reads, "a cached miss reads once");

            Assert.IsFalse(PvpMatchCoordinator.CrystalWeenieResolvesWithRefresh(1006805, _ => false, _ => { reads++; return false; }), "still missing");
            Assert.AreEqual(2, reads);
        }

        /// <summary>
        /// Review R2: a pool map whose layout has no crystal wcid makes Attack/Defend unavailable without ever asking the weenie seam (the
        /// short-circuit that now logs, rate-limited). With King of the Hill off the room is then disabled.
        /// </summary>
        [TestMethod]
        public void NoCrystalWcid_AttackDefendUnavailable_WithoutAWeenieLookup()
        {
            var rig = new Rig().Build();
            rig.Coordinator.LayoutResolver = key => key == BattlegroundMapCatalog.Bg003cKey ? BattlegroundMapCatalog.Bg003c with { CrystalWcid = 0 } : BattlegroundMapCatalog.Find(key);
            rig.Coordinator.CrystalWeenieResolves = _ => { rig.CrystalLookups++; return true; };
            rig.Add(A);
            var before = rig.CrystalLookups;

            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual(PvpJoinRefusal.ModeDisabled, rig.Coordinator.Join(A, "bg", false).Refusal);
                rig.Advance(PvpMatchCoordinator.CrystalWeenieCacheTtl.TotalSeconds + 1);
                rig.Tick();
            }

            Assert.AreEqual(before, rig.CrystalLookups, "a wcid of 0 never reaches the weenie seam");
        }

        /// <summary>The crystal-weenie lookup is cached for CrystalWeenieCacheTtl on the coordinator clock: many joins and ticks inside it ask once, and it is asked again after.</summary>
        [TestMethod]
        public void CrystalWeenieLookup_IsCachedForTheTtl()
        {
            var rig = new Rig { CrystalResolves = false }.Build();
            rig.Add(Four);
            var before = rig.CrystalLookups;
            Assert.IsTrue(before <= 1, $"fixture: at most one lookup so far ({before})");

            for (var i = 0; i < 10; i++)
            {
                rig.Coordinator.Join(A, "bg", false);
                rig.Tick();
            }

            Assert.AreEqual(Math.Max(1, before), rig.CrystalLookups, "every call inside the TTL used the cached answer");

            rig.Advance(PvpMatchCoordinator.CrystalWeenieCacheTtl.TotalSeconds + 1);
            rig.Tick();
            Assert.AreEqual(Math.Max(1, before) + 1, rig.CrystalLookups, "asked again once the TTL ran out");
        }

        // ================= attacker-leave rescale (ruling 12) =================

        private static readonly uint[] Eight = { A, B, C, D, E, F, G, H };

        /// <summary>
        /// Two attackers leaving a 4v4 one after the other rescale from what the crystals are CURRENTLY sized for: 4 to 3, then 3 to 2,
        /// never 4 to 2 a second time (which would compound). Each rescale targets the match's own space and crystal job.
        /// </summary>
        [TestMethod]
        public void AttackerLeaves_RescaleFromTheCurrentSizing_TwiceInARow()
        {
            var rig = LiveAd(out var match, ids: Eight);
            var attackers = rig.Team(match, 0);
            Assert.AreEqual(4, attackers.Length, "fixture: a 4v4");

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(attackers[0]).Outcome);
            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(attackers[1]).Outcome);

            var calls = rig.Spaces.ScaleCalls;
            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual((4, 3), (calls[0].SizedFor, calls[0].Current));
            Assert.AreEqual((3, 2), (calls[1].SizedFor, calls[1].Current));
            Assert.AreSame(rig.Coordinator.SpaceOf(match.MatchId), calls[0].Space);
            Assert.AreSame(rig.Spaces.ObjectiveCalls.Single().Job, calls[0].Job, "the crystals this match placed");
            Assert.AreEqual(PvpMatchState.Live, match.State);
        }

        /// <summary>
        /// A defender leaving changes nothing. An attacker leaving during Countdown is not rescaled at the leave itself but once, at
        /// the Live transition: 4 to 3.
        /// </summary>
        [TestMethod]
        public void DefenderLeaves_NoRescale_AttackerLeavesBeforeLive_RescaledAtLive()
        {
            var rig = LiveAd(out var match, ids: Eight);

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(rig.Team(match, 1)[0]).Outcome);
            rig.Tick();
            Assert.AreEqual(0, rig.Spaces.ScaleCalls.Count, "a defender leaving does not touch the crystals");

            var countdownRig = new Rig().Build();
            var countdown = countdownRig.FormAndAccept(Eight);
            countdownRig.Tick();
            countdownRig.Tick();
            countdownRig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, countdown.State);

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, countdownRig.Coordinator.Forfeit(countdownRig.Team(countdown, 0)[0]).Outcome);
            Assert.AreEqual(0, countdownRig.Spaces.ScaleCalls.Count, "not at the leave itself");

            countdownRig.Advance(countdownRig.Dials.CountdownSeconds);
            countdownRig.Tick();
            Assert.AreEqual(PvpMatchState.Live, countdown.State);

            Assert.AreEqual(1, countdownRig.Spaces.ScaleCalls.Count, "reconciled once at the Live transition");
            Assert.AreEqual((4, 3), (countdownRig.Spaces.ScaleCalls[0].SizedFor, countdownRig.Spaces.ScaleCalls[0].Current));
        }

        /// <summary>
        /// A 4v4 attacker forfeiting at Countdown: the crystals are rescaled 4 to 3 when the match goes Live, never again on a later
        /// tick, and a further attacker leaving during Live scales from that result (3 to 2).
        /// </summary>
        [TestMethod]
        public void AttackerForfeitsAtCountdown_RescaledOnceAtLive_ThenFromThatSizing()
        {
            var rig = new Rig().Build();
            var match = rig.FormAndAccept(Eight);
            rig.Tick();
            rig.Tick();
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            var attackers = rig.Team(match, 0);
            Assert.AreEqual(4, attackers.Length, "fixture: a 4v4");
            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(attackers[0]).Outcome);

            rig.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(1, rig.Spaces.ScaleCalls.Count, "once, at the Live transition");
            Assert.AreEqual((4, 3), (rig.Spaces.ScaleCalls[0].SizedFor, rig.Spaces.ScaleCalls[0].Current));

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(attackers[1]).Outcome);
            Assert.AreEqual(2, rig.Spaces.ScaleCalls.Count);
            Assert.AreEqual((3, 2), (rig.Spaces.ScaleCalls[1].SizedFor, rig.Spaces.ScaleCalls[1].Current));
        }

        /// <summary>The last attacker leaving ends the match on elimination; there is nothing left to rescale for.</summary>
        [TestMethod]
        public void LastAttackerLeaves_EndsTheMatch_WithoutARescaleToZero()
        {
            var rig = LiveAd(out var match);
            var attackers = rig.Team(match, 0);

            rig.Coordinator.Forfeit(attackers[0]);
            rig.Coordinator.Forfeit(attackers[1]);
            rig.Tick();

            Assert.AreEqual(1, rig.Spaces.ScaleCalls.Count, "only the first leave rescaled (2 to 1)");
            Assert.AreEqual((2, 1), (rig.Spaces.ScaleCalls[0].SizedFor, rig.Spaces.ScaleCalls[0].Current));
            Assert.AreEqual(PvpMatchState.Resolving, match.State, "no attackers left: the defenders win");
        }

        // ================= kill chip and heal (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal") =================
        //
        // Every test here reaches PvpMatchCoordinator.HandleBattlegroundDeath -> ApplyKillToCrystal through a real Death intent drained by
        // the real coordinator; the fake space records what AdjustObjective was asked to do. Removing the ApplyKillToCrystal call from
        // HandleBattlegroundDeath fails KillChip_AttackerKillsDefenderNearTheVulnerableCrystal_ChipsIt and KillHeal_DefenderKillsAttacker.

        /// <summary>A position in the match's own instance, in the 0x003C landblock frame.</summary>
        private static Position At(Rig rig, PvpMatch match, uint cellLow, float x, float y, float z) =>
            new Position(0x003C0000 | cellLow, x, y, z, 0f, 0f, 0f, 1f, rig.Coordinator.SpaceOf(match.MatchId).Instance);

        /// <summary>Near the Great Hall crystal (110, -70, -6): 2 m away, in its own cell.</summary>
        private static Position NearGreatHall(Rig rig, PvpMatch match) => At(rig, match, 0x01EE, 110f, -72f, -6f);

        private static void Kill(Rig rig, PvpMatch match, uint victim, uint killer, Position where)
        {
            rig.Gateway.Players[victim].Dying = true;
            rig.Intents.Enqueue(PvpMatchManager.Death(victim, match.MatchId, rig.Now, killer, where));
            rig.Tick();
        }

        [TestMethod]
        public void KillChip_AttackerKillsDefenderNearTheVulnerableCrystal_ChipsIt()
        {
            var rig = LiveAd(out var match);
            var attacker = rig.Team(match, 0)[0];
            var defender = rig.Team(match, 1)[0];

            Kill(rig, match, defender, attacker, NearGreatHall(rig, match));

            var call = rig.Spaces.AdjustCalls.Single();
            Assert.AreEqual(new CrystalKillEffect(0, true, 1.0), call.Effect, "the vulnerable crystal (Great Hall, index 0) loses the default 1%");
            Assert.AreEqual(attacker, call.KillerId, "credited to the killer");
            Assert.AreSame(rig.Coordinator.SpaceOf(match.MatchId), call.Space, "into the match's own space");
            Assert.AreSame(rig.Spaces.ObjectiveCalls.Single().Job, call.Job, "against the crystals this match placed");
        }

        [TestMethod]
        public void KillHeal_DefenderKillsAttackerNearTheVulnerableCrystal_HealsIt()
        {
            var rig = LiveAd(out var match);
            var attacker = rig.Team(match, 0)[0];
            var defender = rig.Team(match, 1)[0];

            Kill(rig, match, attacker, defender, NearGreatHall(rig, match));

            var call = rig.Spaces.AdjustCalls.Single();
            Assert.AreEqual(new CrystalKillEffect(0, false, 0.5), call.Effect, "the vulnerable crystal regains the default 0.5%");
            Assert.AreEqual(defender, call.KillerId);
        }

        /// <summary>The percents and the range come from the formation snapshot, not a live read.</summary>
        [TestMethod]
        public void KillChip_UsesTheFormationSnapshot()
        {
            var rig = LiveAd(out var match, r => r.Bg = r.Bg with { AdKillChipPercent = 4.0, AdKillHealPercent = 3.0 });
            rig.Bg = rig.Bg with { AdKillChipPercent = 50.0, AdKillRange = 0.0 }; // changed after formation: ignored by this match

            Kill(rig, match, rig.Team(match, 1)[0], rig.Team(match, 0)[0], NearGreatHall(rig, match));
            Kill(rig, match, rig.Team(match, 0)[0], rig.Team(match, 1)[1], NearGreatHall(rig, match));

            CollectionAssert.AreEqual(new[] { new CrystalKillEffect(0, true, 4.0), new CrystalKillEffect(0, false, 3.0) }, rig.Spaces.AdjustCalls.Select(c => c.Effect).ToArray());
        }

        /// <summary>
        /// Out of range, a teamkill, a suicide and a kill by no player (an NPC, the environment, a drain) all do nothing; the kill itself still
        /// counts as before (a teamkill is a kill for the killer but never a team kill).
        /// </summary>
        [TestMethod]
        public void KillChip_OutOfRange_Teamkill_Suicide_NoKiller_DoNothing()
        {
            var rig = LiveAd(out var match);
            var attackers = rig.Team(match, 0);
            var defenders = rig.Team(match, 1);

            // 30 m from the Great Hall crystal, past the default 25 m (and the West Cavern crystal is sealed in sequential play).
            Kill(rig, match, defenders[0], attackers[0], At(rig, match, 0x01E4, 90f, -92.4f, -6f));
            Assert.AreEqual(0, rig.Spaces.AdjustCalls.Count, "out of range");
            Assert.AreEqual(1, match.TeamKills[0], "fixture: the kill itself still counted");

            Kill(rig, match, defenders[1], defenders[0], NearGreatHall(rig, match));
            Assert.AreEqual(0, rig.Spaces.AdjustCalls.Count, "teamkill");

            Kill(rig, match, attackers[0], attackers[0], NearGreatHall(rig, match));
            Assert.AreEqual(0, rig.Spaces.AdjustCalls.Count, "suicide");

            Kill(rig, match, attackers[1], 0, NearGreatHall(rig, match));
            Assert.AreEqual(0, rig.Spaces.AdjustCalls.Count, "no player killer (NPC or environment)");
        }

        /// <summary>A death before Live (the countdown) never reaches the chip, even right beside the crystal.</summary>
        [TestMethod]
        public void KillChip_NotLive_DoesNothing()
        {
            var rig = new Rig().Build();
            var match = rig.FormAndAccept(Four);
            rig.Tick();
            rig.Tick();
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            Kill(rig, match, rig.Team(match, 1)[0], rig.Team(match, 0)[0], NearGreatHall(rig, match));

            Assert.AreEqual(0, rig.Spaces.AdjustCalls.Count);
        }

        /// <summary>Sequential play follows the cursor: once the Great Hall falls, a kill there does nothing and a kill at the West Cavern chips crystal 1.</summary>
        [TestMethod]
        public void KillChip_FollowsTheVulnerableCrystal()
        {
            var rig = LiveAd(out var match);
            var attackers = rig.Team(match, 0);
            var defenders = rig.Team(match, 1);

            rig.Destroy(match, 0, attackers[0]);
            Assert.AreEqual(1, match.CrystalSequence.Current, "fixture: the West Cavern is vulnerable");

            Kill(rig, match, defenders[0], attackers[0], NearGreatHall(rig, match));
            Assert.AreEqual(0, rig.Spaces.AdjustCalls.Count, "the fallen Great Hall is never chipped or healed");

            Kill(rig, match, defenders[1], attackers[1], At(rig, match, 0x0161, 56f, -57f, -30f));
            Assert.AreEqual(new CrystalKillEffect(1, true, 1.0), rig.Spaces.AdjustCalls.Single().Effect);
        }

        /// <summary>
        /// Any-order play: the coordinator hands Decide the handler's destroyed set, so a kill beside a fallen crystal does nothing and a
        /// kill beside a standing one still lands. Dropping the IsObjectiveStanding argument makes the first kill chip the fallen crystal 0.
        /// </summary>
        [TestMethod]
        public void KillChip_AnyOrder_SkipsAFallenCrystal()
        {
            var rig = LiveAd(out var match, r => r.Bg = r.Bg with { AdSequentialCrystals = false });
            var attackers = rig.Team(match, 0);
            var defenders = rig.Team(match, 1);

            rig.Destroy(match, 0, attackers[0]);

            Kill(rig, match, defenders[0], attackers[0], NearGreatHall(rig, match));
            Assert.AreEqual(0, rig.Spaces.AdjustCalls.Count, "the fallen Great Hall is skipped");

            Kill(rig, match, defenders[1], attackers[1], At(rig, match, 0x0161, 56f, -57f, -30f));
            Assert.AreEqual(new CrystalKillEffect(1, true, 1.0), rig.Spaces.AdjustCalls.Single().Effect, "the standing West Cavern still takes a kill");
        }

        /// <summary>King of the Hill has no crystals: kills there never ask the objective seam for anything.</summary>
        [TestMethod]
        public void KillChip_Koth_DoesNothing()
        {
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, AdEnabled = false } }.Build();
            var match = rig.FormAndAccept(Four);
            Assert.AreEqual(BattlegroundModes.KothModeKey, match.ModeKey);
            rig.DriveToLive(match);

            var victim = match.Teams.Single(t => t.TeamIndex == 1).Members[0].CharacterId;
            var killer = match.Teams.Single(t => t.TeamIndex == 0).Members[0].CharacterId;
            rig.Gateway.Players[victim].Dying = true;
            rig.Intents.Enqueue(PvpMatchManager.Death(victim, match.MatchId, rig.Now, killer, rig.Gateway.Players[victim].Location));
            rig.Tick();

            Assert.AreEqual(1, match.TeamKills[0], "fixture: the kill counted");
            Assert.AreEqual(0, rig.Spaces.AdjustCalls.Count);
        }

        // ================= /arena status and testspace =================

        /// <summary>The Attack/Defend status carries its own suffix (crystals destroyed of the planned count), and the command appends it in place of the score pair.</summary>
        [TestMethod]
        public void Status_AttackDefend_CarriesTheCrystalSuffix()
        {
            // Any-order play (the sequential setting off): the original suffix, with no Vulnerable name.
            var rig = LiveAd(out var match, r => r.Bg = r.Bg with { AdSequentialCrystals = false });
            var count = rig.Spaces.ObjectiveCalls.Single().Plan.Count;
            rig.Destroy(match, 0);
            rig.Advance(100);

            var s = rig.Coordinator.Status(A);

            Assert.AreEqual(BattlegroundText.StatusCrystals(1, count), s.StatusLine);
            Assert.AreEqual(count, s.ScoreTarget, "the score target is the crystal count");

            var text = ACE.Server.Command.Handlers.PvpArenaCommands.StatusResultText(s);
            StringAssert.EndsWith(text, " remaining." + BattlegroundText.StatusCrystals(1, count));
            Assert.IsFalse(text.Contains("first to"), "never the King of the Hill score suffix: " + text);
        }

        /// <summary>Sequential mode (the default): /arena status adds the vulnerable crystal after the count, and it follows the cursor.</summary>
        [TestMethod]
        public void Status_AttackDefend_Sequential_NamesTheVulnerableCrystal()
        {
            var rig = LiveAd(out var match);
            var count = rig.Spaces.ObjectiveCalls.Single().Plan.Count;

            Assert.AreEqual(BattlegroundText.StatusCrystals(0, count) + " Vulnerable: Great Hall.", rig.Coordinator.Status(A).StatusLine);

            rig.Destroy(match, 0);

            Assert.AreEqual(BattlegroundText.StatusCrystals(1, count) + " Vulnerable: West Cavern.", rig.Coordinator.Status(A).StatusLine);
        }

        /// <summary>King of the Hill's status suffix through the mode handler is byte-identical to the score suffix the command always appended.</summary>
        [TestMethod]
        public void Status_Koth_SuffixUnchanged()
        {
            var rig = new Rig { Bg = BattlegroundTunables.Defaults with { FillWindowSeconds = 0, AdEnabled = false } }.Build();
            var match = rig.FormAndAccept(Four);
            Assert.AreEqual(BattlegroundModes.KothModeKey, match.ModeKey);
            rig.DriveToLive(match);

            match.ScoreBoard[0] = 15;
            match.ScoreBoard[1] = 4;

            var s = rig.Coordinator.Status(A);
            Assert.AreEqual(BattlegroundText.StatusScore(15, 4, rig.Bg.KothScoreTarget), s.StatusLine);

            var legacy = s with { StatusLine = null };
            Assert.AreEqual(ACE.Server.Command.Handlers.PvpArenaCommands.StatusResultText(legacy), ACE.Server.Command.Handlers.PvpArenaCommands.StatusResultText(s), "the routed suffix equals the old fallback");
            Assert.AreEqual(0, rig.Spaces.ObjectiveCalls.Count, "King of the Hill places no crystal");
        }

        /// <summary>testspace places the crystals a match would plan on the mines, and none on a King of the Hill map.</summary>
        [TestMethod]
        public void TestSpace_PlansCrystalsOnlyForACrystalMap()
        {
            var d = BattlegroundTunables.Defaults;
            var plan = ACE.Server.Command.Handlers.PvpArenaAdminCommands.TestSpaceCrystalPlan(BattlegroundMapCatalog.Bg003c, d);

            Assert.IsNotNull(plan);
            Assert.AreEqual(AttackDefendPlan.Build(BattlegroundMapCatalog.Bg003c, d, 1).Count, plan.Count);
            Assert.IsTrue(plan.Count > 0);
            Assert.IsNull(ACE.Server.Command.Handlers.PvpArenaAdminCommands.TestSpaceCrystalPlan(BattlegroundMapCatalog.Bg016c, d), "King of the Hill has no crystal wcid");
            Assert.IsNull(ACE.Server.Command.Handlers.PvpArenaAdminCommands.TestSpaceCrystalPlan(null, d));
        }

        /// <summary>testspace opens the mines by the key its usage text now lists.</summary>
        [TestMethod]
        public void TestSpace_OpensTheMinesByKey()
        {
            var map = ACE.Server.Command.Handlers.PvpArenaAdminCommands.FindTestSpaceMap("bg_003c");

            Assert.IsNotNull(map);
            Assert.AreEqual(BattlegroundMapCatalog.Bg003cKey, map.MapKey);
            Assert.IsNotNull(ACE.Server.Command.Handlers.PvpArenaAdminCommands.TestSpaceSpawn(map), "a battleground map seats the admin on its own spawn set");
        }
    }
}
