using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity;
using ACE.Server.Managers;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The match coordinator (PR C3) driven end to end through its seams: a fake clock, a recording player gateway, a
    /// scripted space provider and a scripted database sink. Most of it takes no Player, no Landblock, and its own
    /// dials are injected - but PvpArenaDenylistTunables.IsDenylisted (BeginStaging, BootDenylistedBeforeLive) and
    /// DiscordRelayManager.QueuePvp (Resolve) both read PropertyManager directly, not through an injected seam, so
    /// ClassInitialize seeds every registered default through DefaultPropertyManager.LoadDefaultProperties() exactly
    /// as PvpRulesPveInvarianceTests does (defaults only - nothing to restore), rather than relying on another test
    /// class in the run having already seeded them.
    ///
    /// Each test's summary names the entry points it drove. "Tick" is PvpMatchCoordinator.Tick; intents go in through
    /// the same ConcurrentQueue shape the production hooks feed (PvpMatchManager.Report).
    /// </summary>
    [TestClass]
    public partial class PvpMatchCoordinatorTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private const uint A = 0x50000001, B = 0x50000002, C = 0x50000003, D = 0x50000004, E = 0x50000005, F = 0x50000006, G = 0x50000007;

        private static readonly DateTime T0 = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>PvP Template Facets: every fake player remembers this template, and the fake catalog offers it in every mode.</summary>
        private const string DefaultTemplateKey = "duelist";

        /// <summary>A pvp_template row whose definition is a minimal parsed template (the coordinator never reads its build).</summary>
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

        private sealed class FakeClock
        {
            public DateTime Now = T0;

            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
        }

        private sealed class FakePlayer
        {
            public PvpPlayerFacts Facts;
            public bool Online = true;
            public uint Instance;
            public bool Teleporting;
            public Position Location;
        }

        private sealed class FakeGateway : IPvpPlayerGateway
        {
            public readonly Dictionary<uint, FakePlayer> Players = new();
            public readonly List<(uint Id, string Text)> Messages = new();
            public readonly List<(uint Id, PvpPlayerBinding Binding, Position Exit, Position Spawn)> Entered = new();
            public readonly List<(uint Id, PvpMatchState State)> Published = new();
            public readonly List<uint> Exited = new();
            public readonly List<uint> Returned = new();
            public readonly List<(uint Id, Guid Match, string Text)> Prompts = new();
            public readonly List<(uint Id, Guid Match)> Aborts = new();
            public bool PromptsSucceed = true;

            /// <summary>When true, a teleport into the arena lands at once (the next presence read sees InInstance).</summary>
            public bool AutoArrive = true;

            public FakePlayer Add(uint id, string name, string ip = null, int level = 100, params uint[] fellowship)
            {
                var p = new FakePlayer
                {
                    Facts = new PvpPlayerFacts(id, name, level, ip ?? $"10.0.0.{id & 0xFF}", false, false, false, false, false, false, false, true, fellowship, PreferredTemplateKey: DefaultTemplateKey),
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

                // PvP Template Facets: the live gateway applies the template first and teleports only on success; a
                // failed apply is reported back as an EntryFailed intent and the player never moves.
                if (BusyFailuresFor.TryGetValue(id, out var busyLeft) && busyLeft > 0)
                {
                    BusyFailuresFor[id] = busyLeft - 1;
                    Intents?.Enqueue(PvpMatchManager.EntryFailed(id, binding.Match.MatchId, DateTime.UtcNow, playerCaused: true, retryable: true));
                    return;
                }

                if (PlayerCausedFailFor.Contains(id))
                {
                    Intents?.Enqueue(PvpMatchManager.EntryFailed(id, binding.Match.MatchId, DateTime.UtcNow, playerCaused: true));
                    return;
                }

                if (FailApplyFor.Contains(id))
                {
                    Intents?.Enqueue(PvpMatchManager.EntryFailed(id, binding.Match.MatchId, DateTime.UtcNow));
                    return;
                }

                if (AutoArrive)
                    Players[id].Instance = spawn.Instance;
                else
                    Players[id].Teleporting = true;
            }

            public readonly List<(uint Id, PvpPlayerBinding Binding)> PublishedBindings = new();

            public void PublishBinding(uint id, PvpPlayerBinding binding)
            {
                Published.Add((id, binding.State));
                PublishedBindings.Add((id, binding));
            }

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

            public bool SendAcceptPrompt(uint id, Guid matchId, string text)
            {
                if (!PromptsSucceed || !IsOnline(id))
                    return false;

                Prompts.Add((id, matchId, text));
                return true;
            }

            public void AbortAcceptPrompt(uint id, Guid matchId) => Aborts.Add((id, matchId));

            public readonly List<(uint Id, int Amount, PvpBloodGrant Grant)> BloodGrants = new();

            public void GrantArenaBlood(uint id, int amount, PvpBloodGrant grant) => BloodGrants.Add((id, amount, grant));

            // ---- PvP Template Facets ----

            /// <summary>The coordinator's intent queue, so a scripted apply failure reports back the way the live gateway does.</summary>
            public ConcurrentQueue<PvpIntent> Intents;

            /// <summary>Players whose template apply fails at entry.</summary>
            public readonly HashSet<uint> FailApplyFor = new();

            /// <summary>The apply reports a player-caused failure (a full pack) for these ids.</summary>
            public readonly HashSet<uint> PlayerCausedFailFor = new();

            /// <summary>The apply reports busy (player-caused, retryable) this many more times for each id, then succeeds.</summary>
            public readonly Dictionary<uint, int> BusyFailuresFor = new();

            /// <summary>A pack-room refusal line per player; absent = room.</summary>
            public readonly Dictionary<uint, string> RoomRefusal = new();

            public readonly List<(uint Id, PvpTemplateDefinition Template)> RoomChecks = new();

            public readonly List<(uint Id, Guid Match)> Backstops = new();

            public string CheckTemplateRoom(uint id, PvpTemplateDefinition template)
            {
                RoomChecks.Add((id, template));
                return IsOnline(id) && RoomRefusal.TryGetValue(id, out var line) ? line : null;
            }

            public void RunTemplateBackstop(uint id, Guid matchId) => Backstops.Add((id, matchId));

            public int ExitCount(uint id) => Exited.Count(x => x == id);

            /// <summary>Total Blood granted to <paramref name="id"/> and how many grant calls carried it.</summary>
            public (int Total, int Calls) Blood(uint id) => (BloodGrants.Where(g => g.Id == id).Sum(g => g.Amount), BloodGrants.Count(g => g.Id == id));

            public int Count(uint id, string text) => Messages.Count(m => m.Id == id && m.Text == text);

            public bool Got(uint id, string text) => Count(id, text) > 0;
        }

        private sealed class FakeSpaces : IPvpMatchSpaces
        {
            public bool Refuse;
            public MatchSpaceReadiness Readiness = MatchSpaceReadiness.Ready;
            public readonly List<(ArenaMap Map, IReadOnlyList<uint> Ids)> Allocations = new();
            public readonly List<MatchSpace> Released = new();
            private ushort next = 0x0100;

            public MatchSpaceAllocation Allocate(ArenaMap map, IReadOnlyList<uint> ids)
            {
                Allocations.Add((map, ids.ToList()));

                if (Refuse)
                    return MatchSpaceAllocation.Fail(MatchSpaceFailure.CreateFailed, "scripted refusal");

                var instance = Position.InstanceIDFromVars(map.RealmId, next++, isTemporaryRuleset: true);
                return MatchSpaceAllocation.Ok(new MatchSpace(map.MapKey, map.LandblockId, map.RealmId, instance, ids[0]));
            }

            public MatchSpaceReadiness GetReadiness(MatchSpace space) => Readiness;

            public void Release(MatchSpace space) => Released.Add(space);
        }

        private sealed class FakeSink : IPvpResultSink
        {
            /// <summary>What the boot read returns. Null = the read failed.</summary>
            public List<PvpRatingRecord> Load = new();

            public PvpMatchSaveResult SaveResult = PvpMatchSaveResult.Saved;

            /// <summary>When false, callbacks are captured and only run when the test invokes them.</summary>
            public bool AutoComplete = true;

            public readonly List<(PvpMatchRecord Match, IReadOnlyList<PvpMatchParticipantRecord> Parts, IReadOnlyList<PvpRatingRecord> Ratings)> Saves = new();
            public readonly List<Action> PendingCallbacks = new();

            /// <summary>Every save's raw callback, so a test can inject any result (or several) by hand.</summary>
            public readonly List<Action<PvpMatchSaveResult, uint>> RawSaveCallbacks = new();

            public void LoadRatings(Action<List<PvpRatingRecord>> callback)
            {
                if (AutoComplete)
                    callback(Load);
                else
                    PendingCallbacks.Add(() => callback(Load));
            }

            // ---- PvP Template Facets ----

            /// <summary>The pvp_template rows the read returns. Null = the read failed. Defaults to one template offered everywhere.</summary>
            public List<PvpTemplateRecord> TemplateRows = new() { TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1,2v2,ffa") };

            public readonly List<(uint DbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> Stamps)> StampWrites = new();

            public List<PvpLatestParticipantTemplateRecord> LatestTemplates = new();

            public void LoadTemplates(Action<List<PvpTemplateRecord>, PvpTemplateStoreStatus> callback) =>
                callback(TemplateRows?.Select(r => r.Clone()).ToList(), TemplateRows == null ? PvpTemplateStoreStatus.Failed : PvpTemplateStoreStatus.Ok);

            public void SaveParticipantTemplates(uint dbMatchId, IReadOnlyList<PvpMatchParticipantTemplateRecord> stamps, Action<PvpTemplateStoreStatus> callback)
            {
                StampWrites.Add((dbMatchId, stamps.ToList()));
                callback(PvpTemplateStoreStatus.Ok);
            }

            public void LoadLatestParticipantTemplates(Action<List<PvpLatestParticipantTemplateRecord>, PvpTemplateStoreStatus> callback) => callback(LatestTemplates, PvpTemplateStoreStatus.Ok);

            public void SaveMatchResult(PvpMatchRecord match, IReadOnlyList<PvpMatchParticipantRecord> participants, IReadOnlyList<PvpRatingRecord> ratingUpserts, Action<PvpMatchSaveResult, uint> callback)
            {
                Saves.Add((match, participants.ToList(), ratingUpserts.ToList()));
                RawSaveCallbacks.Add(callback);

                var result = SaveResult;

                if (AutoComplete)
                    callback(result, result == PvpMatchSaveResult.Saved ? 77u : 0u);
                else
                    PendingCallbacks.Add(() => callback(result, result == PvpMatchSaveResult.Saved ? 77u : 0u));
            }
        }

        private sealed class Rig
        {
            public readonly FakeClock Clock = new();
            public readonly FakeGateway Gateway = new();
            public readonly FakeSpaces Spaces = new();
            public readonly FakeSink Sink = new();
            public readonly ConcurrentQueue<PvpIntent> Intents = new();
            public PvpArenaDials Dials = PvpTunables.Defaults with { BloodEnabled = true };
            public PvpMatchCoordinator Coordinator;

            public Rig Build(bool loadRatings = true)
            {
                Coordinator = new PvpMatchCoordinator(Gateway, Spaces, Sink, () => Clock.Now, () => Dials, Intents);
                Gateway.Intents = Intents;

                if (loadRatings)
                {
                    Coordinator.BeginLoadRatings();
                    Coordinator.BeginLoadTemplates();
                    Coordinator.BeginLoadLatestTemplates();
                    Coordinator.Tick();
                }

                return this;
            }

            public void Tick() => Coordinator.Tick();

            public PvpMatch MatchOf(uint id) => Coordinator.FindMatch(id);

            public void AcceptAll(params uint[] ids)
            {
                foreach (var id in ids)
                    Assert.AreEqual(PvpAnswerOutcome.Accepted, Coordinator.Accept(id).Outcome, $"accept 0x{id:X8}");
            }

            /// <summary>From "everyone accepted" to Live: Staging (allocate), dispatch, arrival -> Countdown, countdown -> Live.</summary>
            public PvpMatch DriveAcceptedToLive(uint anyId)
            {
                Tick(); // AwaitingAccept -> Staging, allocate
                Assert.AreEqual(PvpMatchState.Staging, MatchOf(anyId).State);
                Tick(); // ready -> dispatch
                Tick(); // everyone arrived -> Countdown
                Assert.AreEqual(PvpMatchState.Countdown, MatchOf(anyId).State);
                Clock.Advance(Dials.CountdownSeconds);
                Tick(); // -> Live
                Assert.AreEqual(PvpMatchState.Live, MatchOf(anyId).State);
                return MatchOf(anyId);
            }

            public void Death(uint victim, Guid match, uint killer = 0) => Intents.Enqueue(PvpMatchManager.Death(victim, match, Clock.Now, killer));

            public void Logout(uint id, Guid match)
            {
                Gateway.Players[id].Online = false;
                Intents.Enqueue(PvpMatchManager.LogoutForfeit(id, match, Clock.Now));
            }

            public void PostMatch()
            {
                Clock.Advance(Dials.PostMatchSeconds);
                Tick();
            }

            public PvpMatchParticipantRecord Part(uint id) => Sink.Saves.Single().Parts.Single(p => p.CharacterId == id);
        }

        private static Rig OneVOneLive(out PvpMatch match)
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");

            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(B, "1v1", false).Joined);

            rig.Tick();
            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(A).State);
            rig.AcceptAll(A, B);

            match = rig.DriveAcceptedToLive(A);
            return rig;
        }

        /// <summary>Spawn protection is a battleground rule: an arena match never carries a window, from the first binding to the Live republish, and says nothing about one.</summary>
        [TestMethod]
        public void Arena_NeverCarriesSpawnProtection()
        {
            var rig = OneVOneLive(out var match);

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            var all = rig.Gateway.Entered.Select(e => (e.Id, e.Binding)).Concat(rig.Gateway.PublishedBindings).ToList();

            Assert.IsTrue(all.Count >= 4, "fixture: entry and Live bindings were published for both players");
            Assert.IsTrue(all.All(p => !p.Binding.HasSpawnProtection), "no arena binding carries a window or room protection, at match start included");
            Assert.IsFalse(rig.Gateway.Messages.Any(m => m.Text.Contains("protected for")), "and no protection line is sent");
        }

        // ================= 1v1 happy path =================

        /// <summary>
        /// Drives: Join x2, Tick (forms), Accept x2, Tick through Staging/Countdown/Live, a Death intent, Tick (resolve),
        /// Tick after post_match_seconds (close). Every state is visited in order, both players are exited and
        /// returned, the space is released, and exactly one save is queued, rated.
        /// </summary>
        [TestMethod]
        public void OneVOne_HappyPath_VisitsEveryStateToClosed_ExitsBoth_ReleasesSpace_SavesOnce()
        {
            var rig = OneVOneLive(out var match);

            Assert.AreEqual(2, rig.Gateway.Prompts.Count, "one popup per player");
            Assert.AreEqual(2, rig.Gateway.Entered.Count, "both placed");
            Assert.IsTrue(rig.Gateway.Entered.All(e => e.Binding.State == PvpMatchState.Staging && e.Binding.Match == match));
            Assert.IsTrue(rig.Gateway.Published.Any(p => p.Id == A && p.State == PvpMatchState.Countdown));
            Assert.IsTrue(rig.Gateway.Published.Any(p => p.Id == B && p.State == PvpMatchState.Live));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Start));
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] The match begins in 10..."));

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Bravo was defeated by Alpha."));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Win));
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.Loss));
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] 1v1 rating: 1500 -> 1520 (+20)."));
            Assert.IsTrue(rig.Gateway.Got(B, "[Arena] 1v1 rating: 1500 -> 1480 (-20)."));
            Assert.AreEqual(1, rig.Sink.Saves.Count, "exactly one save");
            Assert.IsTrue(rig.Sink.Saves[0].Match.Rated);
            Assert.AreEqual(1, rig.Part(A).Kills);
            Assert.AreEqual("win", rig.Part(A).Result);
            Assert.AreEqual("loss", rig.Part(B).Result);
            Assert.AreEqual(1520, rig.Coordinator.GetRating(A, "arena_1v1").Rating, "the ladder reflects the result before the save lands");
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.YouWereEliminated));
            Assert.AreEqual(0, rig.Gateway.Exited.Count, "the dead player is not exited while the death return is still under way");

            // The death path carries B out of the instance; the next tick exits B, with no teleport of its own.
            rig.Gateway.Players[B].Instance = 0;
            rig.Clock.Advance(rig.Dials.PostMatchSeconds - 1);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            CollectionAssert.AreEqual(new[] { B }, rig.Gateway.Exited, "the eliminated player exits at once; the survivor waits for post_match_seconds");

            rig.PostMatch();

            Assert.AreEqual(PvpMatchState.Closed, match.State);
            CollectionAssert.AreEqual(new[] { B, A }, rig.Gateway.Exited, "each player exited exactly once");
            CollectionAssert.AreEqual(new[] { A }, rig.Gateway.Returned, "only the survivor is teleported: the death path is the dead player's one return trip");
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Returning));
            Assert.AreEqual(1, rig.Spaces.Released.Count);
            Assert.AreEqual(1, rig.Sink.Saves.Count, "still exactly one save");
            Assert.AreEqual(PvpStatusKind.Idle, rig.Coordinator.Status(A).Kind);
            Assert.AreEqual(0, rig.Coordinator.ListMatches().Count);
        }

        /// <summary>
        /// Drives: Join x2, Tick to Live, a Death, Tick, with PvpMatchCoordinator.QueuePvpForTest set to throw.
        /// Proves the Discord feed's try/catch in Resolve() actually protects the match save: the match still
        /// reaches Resolving with exactly one save queued, rated, despite the relay throwing on every call.
        /// </summary>
        [TestMethod]
        public void OneVOne_DiscordRelayThrows_MatchResultIsStillSaved()
        {
            PvpMatchCoordinator.QueuePvpForTest = _ => throw new InvalidOperationException("simulated relay failure");

            try
            {
                var rig = OneVOneLive(out var match);

                rig.Death(B, match.MatchId, killer: A);
                rig.Tick();

                Assert.AreEqual(PvpMatchState.Resolving, match.State, "the relay throwing must not stop the match from resolving");
                Assert.AreEqual(1, rig.Sink.Saves.Count, "the save must still happen despite the relay throwing");
                Assert.IsTrue(rig.Sink.Saves[0].Match.Rated);
                Assert.AreEqual("win", rig.Part(A).Result);
                Assert.AreEqual("loss", rig.Part(B).Result);
            }
            finally
            {
                PvpMatchCoordinator.QueuePvpForTest = null;
            }
        }

        // ================= 2v2 =================

        /// <summary>
        /// Drives: Join(duo) with a 2-person fellowship, Join x2 solo, Tick before and after duo_vs_solo_after_seconds,
        /// Accept x4. The duo is one team and the two solos the other.
        /// </summary>
        [TestMethod]
        public void TwoVTwo_PremadeDuo_FacesTwoSolos_AfterTheWait()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B });
            rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B });
            rig.Gateway.Add(C, "Charlie");
            rig.Gateway.Add(D, "Delta");

            var duo = rig.Coordinator.Join(A, "2v2", true);
            Assert.IsTrue(duo.Joined);
            Assert.AreEqual("Bravo", duo.PartnerName);
            Assert.AreEqual(PvpJoinRefusal.AlreadyQueued, rig.Coordinator.Join(B, "2v2", false).Refusal, "the partner is queued with the duo");

            Assert.IsTrue(rig.Coordinator.Join(C, "2v2", false).Joined);
            Assert.AreEqual(4, rig.Coordinator.Join(D, "2v2", false).WaitingCount);

            rig.Tick();
            Assert.IsNull(rig.MatchOf(A), "duo vs solos waits for duo_vs_solo_after_seconds");

            rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
            rig.Tick();

            var match = rig.MatchOf(A);
            Assert.IsNotNull(match);
            CollectionAssert.AreEquivalent(new[] { A, B }, match.Teams[0].Members.Select(p => p.CharacterId).ToList());
            CollectionAssert.AreEquivalent(new[] { C, D }, match.Teams[1].Members.Select(p => p.CharacterId).ToList());

            rig.AcceptAll(A, B, C, D);
            rig.DriveAcceptedToLive(A);
            Assert.AreEqual(4, rig.Gateway.Entered.Count);
        }

        /// <summary>
        /// Drives: Join(duo), Join x2, Tick, Accept x4, a LogoutForfeit intent for one duo member, Death intents for both
        /// solos, Tick. The duo's team wins, but the forfeiter takes a loss and a negative delta; the partner wins.
        /// </summary>
        [TestMethod]
        public void TwoVTwo_ForfeiterLoses_EvenWhenPartnerWins()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B });
            rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B });
            rig.Gateway.Add(C, "Charlie");
            rig.Gateway.Add(D, "Delta");

            rig.Coordinator.Join(A, "2v2", true);
            rig.Coordinator.Join(C, "2v2", false);
            rig.Coordinator.Join(D, "2v2", false);
            rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
            rig.Tick();
            rig.AcceptAll(A, B, C, D);
            var match = rig.DriveAcceptedToLive(A);

            rig.Logout(B, match.MatchId);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State, "one forfeiter does not end a 2v2");

            rig.Death(C, match.MatchId, killer: A);
            rig.Death(D, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("win", rig.Part(A).Result);
            Assert.AreEqual("loss", rig.Part(B).Result, "a forfeiter always takes the loss");
            Assert.AreEqual("logout", rig.Part(B).ForfeitReason);
            Assert.IsTrue(rig.Part(A).RatingAfter > rig.Part(A).RatingBefore);
            Assert.IsTrue(rig.Part(B).RatingAfter < rig.Part(B).RatingBefore, "the forfeiter's S is 0 even though the team won");
            Assert.AreEqual(2, rig.Part(A).Kills);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Win));
        }

        // ================= FFA =================

        private static Rig FfaRig(int players, out uint[] ids)
        {
            var rig = new Rig();
            rig.Dials = PvpTunables.Defaults with { FfaTargetPlayers = 6, FfaMinPlayers = 5 };
            rig.Build();

            ids = new[] { A, B, C, D, E, F, G }.Take(players).ToArray();
            var names = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf" };

            for (var i = 0; i < ids.Length; i++)
                rig.Gateway.Add(ids[i], names[i]);

            return rig;
        }

        /// <summary>Drives: Join x6 with Tick between, Tick. The lobby forms only once it reaches the needed size.</summary>
        [TestMethod]
        public void Ffa_FormsAtTheNeededSize()
        {
            var rig = FfaRig(6, out var ids);

            foreach (var id in ids.Take(5))
                Assert.IsTrue(rig.Coordinator.Join(id, "ffa", false).Joined);

            rig.Tick();
            Assert.IsNull(rig.MatchOf(A), "5 of 6 needed: no lobby yet");
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Tugak Brawl lobby: 5 of 6 players."));

            rig.Coordinator.Join(ids[5], "ffa", false);
            rig.Tick();

            var match = rig.MatchOf(A);
            Assert.IsNotNull(match);
            Assert.AreEqual(6, match.Teams.Count);
            Assert.AreEqual(6, rig.Gateway.Prompts.Count);
        }

        /// <summary>Drives: Join x6, Tick, Accept x5, Decline x1, Tick. The lobby proceeds at 5 with the teams re-indexed.</summary>
        [TestMethod]
        public void Ffa_ProceedsAfterOneDecline()
        {
            var rig = FfaRig(6, out var ids);
            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);
            rig.Tick();

            rig.AcceptAll(A, B, C, D, E);
            Assert.AreEqual(PvpAnswerOutcome.Declined, rig.Coordinator.Decline(F).Outcome);
            Assert.IsTrue(rig.Gateway.Got(F, PvpArenaText.YouDeclined));

            var match = rig.DriveAcceptedToLive(A);

            Assert.AreEqual(5, match.Teams.Count);
            CollectionAssert.AreEqual(Enumerable.Range(0, 5).ToList(), match.Teams.Select(t => t.TeamIndex).ToList(), "TeamIndex stays each team's list position");
            Assert.IsFalse(match.Teams.SelectMany(t => t.Members).Any(p => p.CharacterId == F));
            Assert.IsNull(rig.MatchOf(F));
            Assert.AreEqual(0, rig.Coordinator.QueuedIds("ffa").Count, "an FFA decliner is dropped, not requeued");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(F), "the lockout is the all-or-nothing modes' rule");
        }

        /// <summary>
        /// Drives: Join x7 (one joins after the lobby formed), Tick, Accept x4, Decline x2, Tick. Four is below the floor
        /// of 5: the match is canceled and the four accepters go back to the FRONT, ahead of the late joiner.
        /// </summary>
        [TestMethod]
        public void Ffa_DropsBelowMinimum_RequeuesAcceptersAtTheFront()
        {
            var rig = FfaRig(7, out var ids);
            foreach (var id in ids.Take(6))
                rig.Coordinator.Join(id, "ffa", false);
            rig.Tick();

            rig.Clock.Advance(1);
            rig.Coordinator.Join(G, "ffa", false);

            rig.AcceptAll(A, B, C, D);
            rig.Coordinator.Decline(E);
            rig.Coordinator.Decline(F);
            var match = rig.Coordinator.FindMatch(A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.TooFewAccepted));
            CollectionAssert.AreEqual(new[] { A, B, C, D, G }, rig.Coordinator.QueuedIds("ffa").ToArray(), "accepters at the front, in their order, then the late joiner");
            Assert.AreEqual(0, rig.Spaces.Allocations.Count);
        }

        /// <summary>
        /// Drives: Join x5, Tick, Accept x5, Tick to Live, one Death, then Tick past the FFA time limit. The four
        /// survivors share 1st, the eliminated player is 5th, and the result is RATED.
        /// </summary>
        [TestMethod]
        public void Ffa_Timeout_SurvivorsShareFirst_Rated()
        {
            var rig = FfaRig(5, out var ids);
            rig.Dials = rig.Dials with { FfaTargetPlayers = 5, OvertimeEnabled = false };
            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);
            rig.Tick();
            rig.AcceptAll(ids);
            var match = rig.DriveAcceptedToLive(A);

            rig.Death(E, match.MatchId, killer: A);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);

            rig.Clock.Advance(rig.Dials.TimeLimitSecondsFfa);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.TimeUpFfa));
            foreach (var id in new[] { A, B, C, D })
                Assert.AreEqual(1, rig.Part(id).Placement, $"survivor 0x{id:X8} shares first");
            // DESIGN "Win condition": out first with 4 teams still alive, so 5th, even though the survivors share 1st.
            Assert.AreEqual(5, rig.Part(E).Placement);
            Assert.IsTrue(rig.Sink.Saves.Single().Match.Rated);
            Assert.AreEqual(5, rig.Sink.Saves.Single().Ratings.Count);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] You placed 1 of 5."));
        }

        // ================= accept =================

        /// <summary>
        /// Drives: Join x2, Tick, Join (a third player, later), Decline. The other player is back at the FRONT (ahead of
        /// the later joiner), the decliner is locked out, and a re-join is refused with the lockout.
        /// </summary>
        [TestMethod]
        public void OneVOne_Decline_RequeuesOtherAtFront_AndLocksOutTheDecliner()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Gateway.Add(C, "Charlie");

            rig.Coordinator.Join(A, "1v1", false);
            rig.Clock.Advance(1);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            var match = rig.MatchOf(A);

            rig.Clock.Advance(1);
            rig.Coordinator.Join(C, "1v1", false);

            rig.AcceptAll(B);
            Assert.AreEqual(PvpAnswerOutcome.Declined, rig.Coordinator.Decline(A).Outcome);

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.YouDeclined));
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.OtherDeclined));
            CollectionAssert.AreEqual(new[] { B, C }, rig.Coordinator.QueuedIds("1v1").ToArray(), "the other player is back at the FRONT");

            var rejoin = rig.Coordinator.Join(A, "1v1", false);
            Assert.AreEqual(PvpJoinRefusal.DeclineLockout, rejoin.Refusal);
            Assert.AreEqual(rig.Dials.DeclineLockoutSeconds, rejoin.LockoutSecondsRemaining);
            Assert.AreEqual("[Arena] You declined a match recently. You can queue again in 120 seconds.", PvpMatchCoordinator.RefusalText(rejoin));

            rig.Clock.Advance(rig.Dials.DeclineLockoutSeconds);
            rig.Tick();
            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined, "the lockout ends");
        }

        /// <summary>
        /// Drives: Join x2, Tick, Accept (one only), Tick past pvp_arena_accept_seconds. The silent player hears the
        /// timeout, has their popup aborted and is locked out; the accepter is requeued.
        /// </summary>
        [TestMethod]
        public void OneVOne_AcceptTimeout_CancelsAndRequeuesTheAccepter()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            var match = rig.MatchOf(A);
            rig.AcceptAll(A);

            rig.Clock.Advance(rig.Dials.AcceptSeconds - 1);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.AwaitingAccept, match.State);

            rig.Clock.Advance(1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.AcceptTimeoutSelf));
            Assert.IsTrue(rig.Gateway.Aborts.Contains((B, match.MatchId)), "the stale popup is aborted");
            Assert.IsTrue(rig.Coordinator.IsLockedOut(B));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.OtherDeclined));
            CollectionAssert.AreEqual(new[] { A }, rig.Coordinator.QueuedIds("1v1").ToArray());

            // The client's automatic No for the aborted popup arrives afterwards and must change nothing.
            Assert.AreEqual(PvpAnswerOutcome.NoPendingMatch, rig.Coordinator.AnswerFromPopup(B, match.MatchId, false).Outcome);
        }

        /// <summary>Drives: Join x2 with the popup refused, Tick. Each player gets the /arena accept fallback line instead.</summary>
        [TestMethod]
        public void AcceptPopupRefused_FallsBackToTheChatLine()
        {
            var rig = new Rig().Build();
            rig.Gateway.PromptsSucceed = false;
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();

            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Your 1v1 match is ready. Type /arena accept within 20 seconds to enter, or /arena decline."));
        }

        // ================= staging =================

        /// <summary>
        /// Drives: Join x2, Tick, Accept x2, Tick with readiness stuck at Loading, Tick past the staging timeout. The
        /// match is canceled before anyone is placed and the space is released.
        /// </summary>
        [TestMethod]
        public void StagingTimeout_WhenReadinessSticksAtLoading_Cancels()
        {
            var rig = new Rig().Build();
            rig.Spaces.Readiness = MatchSpaceReadiness.Loading;
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            rig.Tick();
            var match = rig.MatchOf(A);
            Assert.AreEqual(PvpMatchState.Staging, match.State);

            rig.Clock.Advance(rig.Dials.StagingTimeoutSeconds - 1);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Staging, match.State);
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "nobody is placed while the space is Loading");

            rig.Clock.Advance(1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(1, rig.Spaces.Released.Count);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Canceled));
            Assert.AreEqual(0, rig.Gateway.Entered.Count);
            Assert.AreEqual(0, rig.Sink.Saves.Count, "a canceled match is never saved");
        }

        /// <summary>
        /// Drives: Join x2, Tick, Accept x2, denylist B, Tick (AwaitingAccept -> Staging). The denylist is checked
        /// only at queue join otherwise, so this proves the re-check at accept time (Docs/Pvp/DESIGN.md "Joining
        /// the queue"): B is evicted BEFORE dispatch (never allocated a space or teleported), A is requeued at the
        /// FRONT, and the match is canceled with no result ever saved - not a forfeit loss for anyone.
        /// </summary>
        [TestMethod]
        public void OneVOne_DenylistedAtAcceptTime_EvictedBeforeDispatch()
        {
            var saved = PvpArenaDenylistTunables.DialSource;

            try
            {
                var rig = new Rig().Build();
                rig.Gateway.Add(A, "Alpha");
                rig.Gateway.Add(B, "Bravo");
                rig.Coordinator.Join(A, "1v1", false);
                rig.Coordinator.Join(B, "1v1", false);
                rig.Tick();
                var match = rig.MatchOf(A);
                rig.AcceptAll(A, B);

                PvpArenaDenylistTunables.DialSource = () => new PvpArenaDenylistDials(PvpArenaDenylistTunables.ParseCsv(B.ToString()));

                rig.Tick(); // AwaitingAccept -> Staging: BeginStaging re-checks the denylist here

                Assert.AreEqual(PvpMatchState.Canceled, match.State);
                Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.Denylisted));
                Assert.AreEqual(0, rig.Spaces.Allocations.Count, "never allocated - evicted before placement");
                Assert.AreEqual(0, rig.Gateway.Entered.Count, "never teleported - evicted before dispatch");
                CollectionAssert.AreEqual(new[] { A }, rig.Coordinator.QueuedIds("1v1").ToArray(), "A is requeued at the FRONT; the denylisted B is not requeued");
                Assert.IsFalse(rig.Coordinator.IsLockedOut(A), "not a decline - A is never locked out");
                Assert.IsFalse(rig.Coordinator.IsLockedOut(B), "not a decline - B is never locked out either");
                Assert.AreEqual(0, rig.Sink.Saves.Count, "no match result is ever saved - not a forfeit loss for anyone");
            }
            finally
            {
                PvpArenaDenylistTunables.DialSource = saved;
            }
        }

        /// <summary>
        /// Drives: Join x2, Tick, Accept x2, Tick to Live, then denylist B and advance past the OTHER match's
        /// Countdown timer. Proves the re-check "immediately before a match goes Live" (Docs/Pvp/DESIGN.md
        /// "Joining the queue"): the match never reaches Live, is canceled with the SAME unrated
        /// canceled-history row an admin cancel during Countdown writes (parity with
        /// AdminCancel_Countdown_IsAllowed above) - never a forfeit loss for anyone - both participants are
        /// exited and returned, and A, not denylisted, is requeued at the FRONT.
        /// </summary>
        [TestMethod]
        public void OneVOne_DenylistedDuringCountdown_CanceledBeforeLive_NoForfeit()
        {
            var saved = PvpArenaDenylistTunables.DialSource;

            try
            {
                var rig = new Rig().Build();
                rig.Gateway.Add(A, "Alpha");
                rig.Gateway.Add(B, "Bravo");
                rig.Coordinator.Join(A, "1v1", false);
                rig.Coordinator.Join(B, "1v1", false);
                rig.Tick();
                var match = rig.MatchOf(A);
                rig.AcceptAll(A, B);

                rig.Tick(); // -> Staging, allocate
                rig.Tick(); // dispatch
                rig.Tick(); // everyone arrived -> Countdown
                Assert.AreEqual(PvpMatchState.Countdown, match.State);

                PvpArenaDenylistTunables.DialSource = () => new PvpArenaDenylistDials(PvpArenaDenylistTunables.ParseCsv(B.ToString()));

                rig.Clock.Advance(rig.Dials.CountdownSeconds);
                rig.Tick(); // would go Live, but B is now denylisted

                Assert.AreEqual(PvpMatchState.Canceled, match.State);
                Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.Denylisted));
                CollectionAssert.AreEqual(new[] { A }, rig.Coordinator.QueuedIds("1v1").ToArray(), "A is requeued at the FRONT; B is not");
                CollectionAssert.AreEquivalent(new[] { A, B }, rig.Gateway.Exited, "both participants are exited, same as an admin cancel during Countdown");
                Assert.AreEqual(1, rig.Sink.Saves.Count, "an unrated canceled-history row is saved, same parity as an admin cancel during Countdown");
                Assert.IsFalse(rig.Sink.Saves.Single().Match.Rated, "never rated - not a forfeit loss for anyone");
                Assert.AreEqual("Canceled", rig.Sink.Saves.Single().Match.EndReason);
                Assert.AreEqual(0, rig.Sink.Saves.Single().Ratings.Count, "no rating upserts");
            }
            finally
            {
                PvpArenaDenylistTunables.DialSource = saved;
            }
        }

        // ================= walkouts before Live =================

        /// <summary>Lands a dispatched, still-teleporting player in the match instance (the fake's AutoArrive = false path).</summary>
        private static void Arrive(Rig rig, uint id)
        {
            var p = rig.Gateway.Players[id];
            p.Teleporting = false;
            p.Instance = rig.Gateway.Entered.Last(e => e.Id == id).Spawn.Instance;
        }

        /// <summary>1v1 formed, accepted and dispatched with both players still teleporting in: Staging, nobody arrived yet.</summary>
        private static Rig OneVOneDispatchedLoading(out PvpMatch match, PvpArenaDials dials = null)
        {
            var rig = new Rig();

            if (dials != null)
                rig.Dials = dials;

            rig.Build();
            rig.Gateway.AutoArrive = false;
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Clock.Advance(1);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            match = rig.MatchOf(A);
            rig.AcceptAll(A, B);
            rig.Tick(); // -> Staging, allocate
            rig.Tick(); // dispatch
            Assert.AreEqual(PvpMatchState.Staging, match.State);
            Assert.AreEqual(2, rig.Gateway.Entered.Count);
            return rig;
        }

        /// <summary>
        /// Drives: 1v1 dispatched, Alpha arrives (Bravo still loading), Tick, then Alpha walks out during Staging, Tick.
        /// A dodge: Alpha takes a rated loss with forfeit reason "left", Bravo wins, Alpha is locked out, and the match
        /// resolves through the normal path instead of waiting for the staging timeout.
        /// </summary>
        [TestMethod]
        public void WalkoutDuringStaging_AfterArriving_IsARatedForfeitLoss_AndLocksOutTheLeaver()
        {
            var rig = OneVOneDispatchedLoading(out var match);

            Arrive(rig, A);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Staging, match.State, "Bravo is still loading in");

            rig.Gateway.Players[A].Instance = 0;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("left", rig.Part(A).ForfeitReason);
            Assert.AreEqual("loss", rig.Part(A).Result);
            Assert.AreEqual("win", rig.Part(B).Result);
            Assert.IsTrue(rig.Sink.Saves.Single().Match.Rated, "rated per the normal rules");
            Assert.IsTrue(rig.Part(A).RatingAfter < rig.Part(A).RatingBefore);
            Assert.IsTrue(rig.Coordinator.IsLockedOut(A), "the leaver is locked out like a decliner");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(B));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.LeftTheArena));
            rig.PostMatch();
            Assert.AreEqual(PvpMatchState.Closed, match.State);
            Assert.AreEqual(PvpJoinRefusal.DeclineLockout, rig.Coordinator.Join(A, "1v1", false).Refusal, "still locked out once the match has closed");
        }

        /// <summary>
        /// Drives: 1v1 to Countdown (both arrived), then Bravo walks out, Tick. Bravo takes a rated loss with forfeit
        /// reason "left", Alpha wins, Bravo is locked out, and the match never goes Live.
        /// </summary>
        [TestMethod]
        public void WalkoutDuringCountdown_IsARatedForfeitLoss_AndLocksOutTheLeaver()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            var match = rig.MatchOf(A);
            rig.AcceptAll(A, B);
            rig.Tick(); // -> Staging
            rig.Tick(); // dispatch
            rig.Tick(); // both arrived -> Countdown
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            rig.Clock.Advance(1);
            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsNull(match.LiveSinceUtc, "never went Live");
            Assert.AreEqual("left", rig.Part(B).ForfeitReason);
            Assert.AreEqual("loss", rig.Part(B).Result);
            Assert.AreEqual("win", rig.Part(A).Result);
            Assert.IsTrue(rig.Sink.Saves.Single().Match.Rated);
            Assert.IsTrue(rig.Coordinator.IsLockedOut(B));
            Assert.IsFalse(rig.Coordinator.IsLockedOut(A));
            Assert.IsFalse(rig.Gateway.Got(A, PvpArenaText.Start), "the start line never goes out");
        }

        /// <summary>
        /// Drives: 1v1 dispatched with max_concurrent_matches 1 and two more players (Charlie, then Delta) queued behind
        /// it. Alpha arrives, Bravo never does, Tick past the staging timeout. A no-fault cancel: nobody is charged or
        /// locked out, nothing is saved, and Alpha goes back to the FRONT, so the next match is Alpha vs Charlie while
        /// Delta keeps waiting. Bravo is not requeued.
        /// </summary>
        [TestMethod]
        public void StagingTimeout_SeatNeverArrived_NoFaultCancel_RequeuesTheArrivedAtTheFront()
        {
            var rig = OneVOneDispatchedLoading(out var match, PvpTunables.Defaults with { MaxConcurrentMatches = 1 });

            rig.Gateway.Add(C, "Charlie");
            rig.Gateway.Add(D, "Delta");
            rig.Clock.Advance(1);
            rig.Coordinator.Join(C, "1v1", false);
            rig.Clock.Advance(1);
            rig.Coordinator.Join(D, "1v1", false);

            Arrive(rig, A);
            rig.Tick();
            CollectionAssert.AreEqual(new[] { C, D }, rig.Coordinator.QueuedIds("1v1").ToArray(), "held by max_concurrent_matches");

            rig.Clock.Advance(rig.Dials.StagingTimeoutSeconds);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(0, rig.Sink.Saves.Count, "no-fault: nothing is recorded");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(A));
            Assert.IsFalse(rig.Coordinator.IsLockedOut(B), "a seat that never arrived is not charged");
            Assert.IsTrue(rig.Gateway.Returned.Contains(A), "the arrived player is sent back out");

            var next = rig.MatchOf(A);
            Assert.IsNotNull(next, "Alpha was requeued and paired in the same tick");
            Assert.AreNotSame(match, next);
            Assert.AreSame(next, rig.MatchOf(C), "Alpha went to the FRONT, ahead of Charlie and Delta");
            Assert.IsNull(rig.MatchOf(B), "the seat that never arrived is not requeued");
            CollectionAssert.AreEqual(new[] { D }, rig.Coordinator.QueuedIds("1v1").ToArray());
        }

        /// <summary>
        /// Drives: 1v1 dispatched, Alpha arrives, Bravo is still loading - first in transit, then briefly Elsewhere
        /// (not yet placed on the instance) - across several ticks before the staging timeout. Never forfeited or locked
        /// out; once Bravo lands, the Countdown starts normally.
        /// </summary>
        [TestMethod]
        public void StillLoadingSeat_IsNeverForfeited()
        {
            var rig = OneVOneDispatchedLoading(out var match);

            Arrive(rig, A);
            rig.Tick();

            rig.Clock.Advance(5);
            rig.Tick(); // Bravo InTransit

            rig.Gateway.Players[B].Teleporting = false; // Elsewhere, never yet seen in the instance
            rig.Clock.Advance(5);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Staging, match.State);
            Assert.IsFalse(match.Teams.SelectMany(t => t.Members).Any(p => p.ExitReason.HasValue), "nobody forfeited");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(B));
            Assert.IsFalse(rig.Gateway.Exited.Contains(B));

            Arrive(rig, B);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, match.State);
        }
        /// <summary>
        /// Drives: Join x2, Tick, Accept x2, Tick with the allocation refused. Canceled, both back at the front, nobody
        /// placed, and the room waits out the backoff instead of re-proposing every tick.
        /// </summary>
        [TestMethod]
        public void AllocationRefused_CancelsAndRequeues_WithBackoff()
        {
            var rig = new Rig().Build();
            rig.Spaces.Refuse = true;
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            var match = rig.MatchOf(A);
            rig.AcceptAll(A, B);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(1, rig.Spaces.Allocations.Count);
            Assert.AreEqual(0, rig.Gateway.Entered.Count);
            CollectionAssert.AreEquivalent(new[] { A, B }, rig.Coordinator.QueuedIds("1v1").ToList());

            rig.Tick();
            Assert.IsNull(rig.MatchOf(A), "no new proposal during the backoff");

            rig.Spaces.Refuse = false;
            rig.Clock.Advance(PvpMatchCoordinator.AllocationBackoffSeconds);
            rig.Tick();
            Assert.IsNotNull(rig.MatchOf(A), "matched again after the backoff");
        }

        // ================= live =================

        /// <summary>
        /// Drives: the 1v1 path to Live, then a LogoutForfeit intent (the player also goes offline), Tick, post-match
        /// Tick. The offline player takes the loss with a forfeit reason, and still gets the exit call.
        /// </summary>
        [TestMethod]
        public void LogoutForfeit_MidLive_RecordsALoss_AndStillAppliesTheExit()
        {
            var rig = OneVOneLive(out var match);

            rig.Logout(B, match.MatchId);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Bravo forfeited."));
            Assert.AreEqual("loss", rig.Part(B).Result);
            Assert.AreEqual("logout", rig.Part(B).ForfeitReason);
            Assert.AreEqual("win", rig.Part(A).Result);
            Assert.IsTrue(rig.Part(B).RatingAfter < rig.Part(B).RatingBefore);

            Assert.AreEqual(1, rig.Gateway.Exited.Count(id => id == B), "the offline player gets the exit call at once");

            rig.PostMatch();

            Assert.AreEqual(PvpMatchState.Closed, match.State);
            Assert.AreEqual(1, rig.Gateway.Exited.Count(id => id == B), "and never a second one");
            Assert.IsFalse(rig.Gateway.Returned.Contains(B), "an offline player is never teleported; the login path relocates them");
        }

        /// <summary>Drives: the 1v1 path to Live, then the player's presence goes Elsewhere (recall), Tick. A forfeit.</summary>
        [TestMethod]
        public void LeavingTheInstance_MidLive_IsAForfeit()
        {
            var rig = OneVOneLive(out var match);

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("left", rig.Part(B).ForfeitReason);
            Assert.AreEqual("loss", rig.Part(B).Result);
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.LeftTheArena));
            CollectionAssert.AreEqual(new[] { B }, rig.Gateway.Exited, "exited as soon as they left");
            Assert.IsFalse(rig.Gateway.Returned.Contains(B), "already outside: never teleported");
        }

        /// <summary>Drives: the 1v1 path to Live, then an InTransit presence. A teleport in progress is not a forfeit.</summary>
        [TestMethod]
        public void InTransit_MidLive_IsNotAForfeit()
        {
            var rig = OneVOneLive(out var match);

            rig.Gateway.Players[B].Teleporting = true;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, match.State);
        }

        /// <summary>
        /// Drives: the 1v1 path to Live, then both Death intents in the same tick. Both teams share first: an UNRATED
        /// draw (owner ruling on #1399), with the double-knockout line and no rating rows.
        /// </summary>
        [TestMethod]
        public void DoubleKnockout_IsAnUnratedDraw()
        {
            var rig = OneVOneLive(out var match);

            rig.Death(A, match.MatchId, killer: B);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("draw", rig.Part(A).Result);
            Assert.AreEqual("draw", rig.Part(B).Result);
            Assert.AreEqual(1, rig.Part(A).Placement);
            Assert.AreEqual(1, rig.Part(B).Placement);
            Assert.AreEqual("draw", rig.Sink.Saves.Single().Match.Outcome);
            Assert.IsFalse(rig.Sink.Saves.Single().Match.Rated);
            Assert.AreEqual(0, rig.Sink.Saves.Single().Ratings.Count);
            Assert.IsNull(rig.Part(A).RatingAfter);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Both sides fell at once. The match is a draw."));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Unrated));
            Assert.IsFalse(rig.Gateway.Got(A, PvpArenaText.Win));
            Assert.IsFalse(rig.Gateway.Got(A, PvpArenaText.Loss));
        }

        /// <summary>
        /// Drives: the 1v1 path to Live, then Tick at limit-60 s and at the limit. The one-minute warning is sent once;
        /// at the limit the match is an UNRATED draw with no rating rows written. With pvp_arena_overtime_enabled OFF,
        /// so this pins that turning overtime off leaves the old timeout exactly as it was.
        /// </summary>
        [TestMethod]
        public void OneVOne_TimeLimit_IsAnUnratedDraw()
        {
            var rig = OneVOneLive(out var match);
            rig.Dials = rig.Dials with { OvertimeEnabled = false };

            rig.Clock.Advance(rig.Dials.TimeLimitSeconds1v1 - 60);
            rig.Tick();
            rig.Tick();
            Assert.AreEqual(1, rig.Gateway.Count(A, PvpArenaText.TimeWarning));
            Assert.AreEqual(TimeSpan.FromSeconds(60), rig.Coordinator.Status(A).Remaining);

            rig.Clock.Advance(60);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.TimeUpDraw));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Unrated));
            Assert.IsFalse(rig.Sink.Saves.Single().Match.Rated);
            Assert.AreEqual(0, rig.Sink.Saves.Single().Ratings.Count);
            Assert.AreEqual("draw", rig.Part(A).Result);
            Assert.IsNull(rig.Part(A).RatingBefore);
        }

        /// <summary>Drives: the 1v1 path to Live, then Forfeit (the post-confirmation /arena leave). Exited and returned at once.</summary>
        [TestMethod]
        public void ForfeitCommand_MidLive_ExitsAndReturnsAtOnce()
        {
            var rig = OneVOneLive(out var match);

            Assert.AreEqual(PvpLeaveOutcome.InMatchNeedsConfirm, rig.Coordinator.Leave(B).Outcome);
            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(B).Outcome);

            Assert.IsTrue(rig.Gateway.Exited.Contains(B));
            Assert.IsTrue(rig.Gateway.Returned.Contains(B));

            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("command", rig.Part(B).ForfeitReason);
        }

        /// <summary>
        /// Drives: 1v1 Live, Leave (the popup is opened for this match), then the popup's Yes carrying a DIFFERENT match
        /// id (the player left that match another way and is now in a new one). The stale Yes changes nothing; the Yes
        /// carrying the id Leave returned still forfeits.
        /// </summary>
        [TestMethod]
        public void ForfeitFromLeavePopup_ForAnotherMatch_DoesNothing()
        {
            var rig = OneVOneLive(out var match);

            var leave = rig.Coordinator.Leave(B);
            Assert.AreEqual(PvpLeaveOutcome.InMatchNeedsConfirm, leave.Outcome);
            Assert.AreEqual(match.MatchId, leave.MatchId, "the leave popup must be told which match it is about");

            Assert.AreEqual(PvpForfeitOutcome.NotInMatch, rig.Coordinator.Forfeit(B, Guid.NewGuid()).Outcome);
            Assert.IsFalse(rig.Gateway.Exited.Contains(B), "a stale popup Yes exited the player from a different match");
            Assert.AreEqual(PvpMatchState.Live, match.State);

            Assert.AreEqual(PvpForfeitOutcome.Forfeited, rig.Coordinator.Forfeit(B, leave.MatchId).Outcome);
            Assert.IsTrue(rig.Gateway.Exited.Contains(B));
        }

        /// <summary>
        /// Drives: Join x2, Tick (a NEW match forms, AwaitingAccept), then a stale leave-popup Yes from an older match.
        /// Without the match id it would decline the new match and lock the player out; with it, nothing happens.
        /// </summary>
        [TestMethod]
        public void ForfeitFromStaleLeavePopup_DoesNotDeclineANewMatch()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(B, "1v1", false).Joined);
            rig.Tick();

            var fresh = rig.MatchOf(B);
            Assert.AreEqual(PvpMatchState.AwaitingAccept, fresh.State);

            Assert.AreEqual(PvpForfeitOutcome.NotInMatch, rig.Coordinator.Forfeit(B, Guid.NewGuid()).Outcome);
            Assert.AreEqual(PvpMatchState.AwaitingAccept, fresh.State, "a stale popup Yes declined the new match");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(B), "a stale popup Yes locked the player out");

            // Positive control: the same call without a match id (the "/arena leave confirm" form) does decline it.
            Assert.AreEqual(PvpForfeitOutcome.Declined, rig.Coordinator.Forfeit(B).Outcome);
        }

        // ================= capacity and ratings =================

        /// <summary>
        /// Drives: Join x4 with max_concurrent_matches = 1, Tick x2. The second pair stays queued and hears the
        /// matches-full line exactly once; it is matched once the first match closes.
        /// </summary>
        [TestMethod]
        public void MaxConcurrentMatches_HoldsTheQueue()
        {
            var rig = new Rig();
            rig.Dials = PvpTunables.Defaults with { MaxConcurrentMatches = 1 };
            rig.Build();

            foreach (var (id, name) in new[] { (A, "Alpha"), (B, "Bravo"), (C, "Charlie"), (D, "Delta") })
            {
                rig.Gateway.Add(id, name);
                rig.Coordinator.Join(id, "1v1", false);
                rig.Clock.Advance(1);
            }

            rig.Tick();
            rig.Tick();

            Assert.IsNotNull(rig.MatchOf(A));
            Assert.IsNull(rig.MatchOf(C));
            CollectionAssert.AreEqual(new[] { C, D }, rig.Coordinator.QueuedIds("1v1").ToArray());
            Assert.AreEqual(1, rig.Gateway.Count(C, PvpArenaText.MatchesFull), "sent once");
            Assert.AreEqual(1, rig.Gateway.Count(D, PvpArenaText.MatchesFull));

            rig.Coordinator.Decline(A);
            rig.Tick(); // the canceled match is removed; B is requeued at the front and paired with C
            Assert.IsNotNull(rig.MatchOf(C));
        }

        /// <summary>
        /// Drives: BeginLoadRatings with the read FAILING (null), Tick, then a full 1v1. The chosen rule: matches run
        /// UNRATED, the history row is still saved with rated = false, no rating row is written, and the ladder reads
        /// return null.
        /// </summary>
        [TestMethod]
        public void RatingsUnavailable_MatchesRunUnrated_AndWriteNoRatingRows()
        {
            var rig = new Rig();
            rig.Sink.Load = null;
            rig.Build();

            Assert.AreEqual(PvpRatingStoreState.Unavailable, rig.Coordinator.Ratings.State);
            Assert.IsNull(rig.Coordinator.GetRating(A, "arena_1v1"));
            Assert.IsNull(rig.Coordinator.GetTop("arena_1v1", 10));

            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined, "joins are still accepted");
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            var match = rig.DriveAcceptedToLive(A);

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(1, rig.Sink.Saves.Count);
            Assert.IsFalse(rig.Sink.Saves[0].Match.Rated);
            Assert.AreEqual(0, rig.Sink.Saves[0].Ratings.Count, "no rating row may be written from an unavailable store");
            Assert.AreEqual("win", rig.Part(A).Result);
            Assert.IsNull(rig.Part(A).RatingBefore);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Unrated));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Win));
        }

        /// <summary>Drives: Join x2 while the ratings read has not come back. Matchmaking waits for it.</summary>
        [TestMethod]
        public void RatingsLoading_MatchmakingWaits()
        {
            var rig = new Rig();
            rig.Sink.AutoComplete = false;
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            Assert.IsNull(rig.MatchOf(A));

            rig.Sink.PendingCallbacks.Single()();
            rig.Tick();
            rig.Tick();
            Assert.IsNotNull(rig.MatchOf(A));
        }

        /// <summary>
        /// Drives: a full 1v1 whose save returns Failed or Ambiguous, then many Ticks. The result is handled once and
        /// the save is NEVER resubmitted; the in-memory ladder keeps the result.
        /// </summary>
        [DataTestMethod]
        [DataRow(PvpMatchSaveResult.Failed)]
        [DataRow(PvpMatchSaveResult.Ambiguous)]
        public void SaveFailedOrAmbiguous_IsNeverResubmitted(PvpMatchSaveResult result)
        {
            var rig = new Rig();
            rig.Sink.SaveResult = result;
            rig.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            var match = rig.DriveAcceptedToLive(A);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            for (var i = 0; i < 20; i++)
            {
                rig.Clock.Advance(1);
                rig.Tick();
            }

            Assert.AreEqual(1, rig.Sink.Saves.Count, "never resubmitted");
            Assert.AreEqual(1, rig.Coordinator.SavesQueued);
            Assert.AreEqual(1, rig.Coordinator.SaveResultsHandled);
            Assert.AreEqual(result, rig.Coordinator.LastSaveResult);
            Assert.AreEqual(1520, rig.Coordinator.GetRating(A, "arena_1v1").Rating, "the in-memory ladder keeps the result");
            Assert.AreEqual(PvpMatchState.Closed, match.State);
        }

        /// <summary>
        /// Drives: BeginLoadRatings and a match save with the sink's callbacks captured, then runs each callback on a
        /// thread-pool thread (as the shard worker does). Nothing is acted on until the next Tick.
        /// </summary>
        [TestMethod]
        public void DbCallbacks_AreHandledOnlyOnTheTick()
        {
            var rig = new Rig();
            rig.Sink.AutoComplete = false;
            rig.Build(loadRatings: false);
            rig.Coordinator.BeginLoadRatings();

            Task.Run(rig.Sink.PendingCallbacks.Single()).Wait();
            rig.Sink.PendingCallbacks.Clear();

            Assert.AreEqual(PvpRatingStoreState.Loading, rig.Coordinator.Ratings.State, "the callback only enqueues");
            Assert.AreEqual(1, rig.Coordinator.PendingDbResults);

            rig.Tick();
            Assert.AreEqual(PvpRatingStoreState.Available, rig.Coordinator.Ratings.State);
            Assert.AreEqual(0, rig.Coordinator.PendingDbResults);

            // PvP Template Facets: joins need the template catalog; its read also only enqueues until the tick.
            rig.Coordinator.BeginLoadTemplates();
            Assert.IsNull(rig.Coordinator.Templates, "the template callback only enqueues");
            rig.Tick();
            Assert.IsNotNull(rig.Coordinator.Templates);

            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            var match = rig.DriveAcceptedToLive(A);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(1, rig.Sink.PendingCallbacks.Count);
            Task.Run(rig.Sink.PendingCallbacks.Single()).Wait();

            Assert.AreEqual(0, rig.Coordinator.SaveResultsHandled, "the save callback only enqueues");
            Assert.IsNull(rig.Coordinator.LastSaveResult);
            Assert.AreEqual(1, rig.Coordinator.PendingDbResults);

            rig.Tick();
            Assert.AreEqual(1, rig.Coordinator.SaveResultsHandled);
            Assert.AreEqual(PvpMatchSaveResult.Saved, rig.Coordinator.LastSaveResult);
        }

        // ================= exit at elimination (owner ruling on #1399) =================

        /// <summary>
        /// Drives: an FFA of 5 to Live, one Death intent, Ticks while the death return is in transit and after it
        /// lands, then the rest of the match. The dead player is exited mid-match (the match goes on), exactly once, and
        /// is never teleported by the coordinator: the death path is their one return trip.
        /// </summary>
        [TestMethod]
        public void Death_ExitsTheEliminatedPlayerMidMatch_ExactlyOnce_WithNoSecondReturnTrip()
        {
            var rig = FfaRig(5, out var ids);
            rig.Dials = rig.Dials with { FfaTargetPlayers = 5, OvertimeEnabled = false };
            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);
            rig.Tick();
            rig.AcceptAll(ids);
            var match = rig.DriveAcceptedToLive(A);

            rig.Death(E, match.MatchId, killer: A);
            rig.Tick();
            Assert.IsTrue(rig.Gateway.Got(E, PvpArenaText.YouWereEliminated));
            Assert.IsTrue(rig.Gateway.Published.Contains((E, PvpMatchState.Closed)), "the damage gate refuses them at once");
            Assert.IsFalse(rig.Gateway.Exited.Contains(E), "not exited while still in the arena (the death chain has not moved them)");

            rig.Gateway.Players[E].Teleporting = true; // the death teleport is under way
            rig.Tick();
            Assert.IsFalse(rig.Gateway.Exited.Contains(E), "not exited while in transit");

            rig.Gateway.Players[E].Teleporting = false;
            rig.Gateway.Players[E].Instance = 0; // landed at the stamped exit
            rig.Tick();
            CollectionAssert.AreEqual(new[] { E }, rig.Gateway.Exited, "exited as soon as the death return completed, mid-match");
            Assert.AreEqual(PvpMatchState.Live, match.State);

            foreach (var id in new[] { B, C, D })
                rig.Death(id, match.MatchId, killer: A);
            rig.Tick();
            rig.PostMatch();

            Assert.AreEqual(PvpMatchState.Closed, match.State);
            foreach (var id in ids)
                Assert.AreEqual(1, rig.Gateway.Exited.Count(x => x == id), $"0x{id:X8} exited exactly once");
            CollectionAssert.AreEqual(new[] { A }, rig.Gateway.Returned, "only the survivor is teleported by the coordinator");
            Assert.AreEqual(5, rig.Part(E).Placement, "still a participant at Resolving");
        }

        // ================= admin cancel (owner ruling on #1399) =================

        /// <summary>
        /// Drives: the 1v1 path to Live, one Death (still in transit), then Cancel(matchId). Canceled and UNRATED: the
        /// admin line, the survivor exited and returned, the dead player exited without a teleport, the space released,
        /// and one history row with end reason Canceled, rated = false, no rating rows.
        /// </summary>
        [TestMethod]
        public void AdminCancel_Live_IsUnrated_ExitsEveryone_WritesACanceledHistoryRow()
        {
            var rig = OneVOneLive(out var match);

            var result = rig.Coordinator.Cancel(match.MatchId);

            Assert.AreEqual(PvpCancelOutcome.Canceled, result.Outcome);
            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] An administrator ended the match. It will not be rated."));
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.AdminCanceled));
            CollectionAssert.AreEquivalent(new[] { A, B }, rig.Gateway.Exited);
            CollectionAssert.AreEquivalent(new[] { A, B }, rig.Gateway.Returned);
            Assert.AreEqual(1, rig.Spaces.Released.Count);

            var save = rig.Sink.Saves.Single();
            Assert.AreEqual("Canceled", save.Match.EndReason);
            Assert.IsFalse(save.Match.Rated);
            Assert.AreEqual(0, save.Ratings.Count);
            Assert.AreEqual(2, save.Parts.Count);
            Assert.IsTrue(save.Parts.All(p => p.Result == "canceled" && p.RatingBefore == null));

            rig.Tick();
            Assert.AreEqual(0, rig.Coordinator.ListMatches().Count);
            Assert.AreEqual(1, rig.Sink.Saves.Count, "no second save on the next tick");
        }

        /// <summary>
        /// Drives: an FFA of 5 to Live, one Death intent drained while the death return is still in transit, then
        /// Cancel(matchId). The dead player is exited but never teleported (the death path is their one trip); the four
        /// others are exited and returned.
        /// </summary>
        [TestMethod]
        public void AdminCancel_Live_DeadPlayerInTransitIsExitedWithoutATeleport()
        {
            var rig = FfaRig(5, out var ids);
            rig.Dials = rig.Dials with { FfaTargetPlayers = 5, OvertimeEnabled = false };
            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);
            rig.Tick();
            rig.AcceptAll(ids);
            var match = rig.DriveAcceptedToLive(A);

            rig.Death(E, match.MatchId, killer: A);
            rig.Gateway.Players[E].Teleporting = true;
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.IsFalse(rig.Gateway.Exited.Contains(E));

            rig.Coordinator.Cancel(match.MatchId);

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            CollectionAssert.AreEquivalent(ids, rig.Gateway.Exited);
            CollectionAssert.AreEquivalent(new[] { A, B, C, D }, rig.Gateway.Returned);
            Assert.AreEqual(1, rig.Sink.Saves.Single().Parts.Single(p => p.CharacterId == E).Deaths);
        }

        /// <summary>Drives: Cancel(matchId) during Countdown. Canceled, unrated history row, everyone exited and returned.</summary>
        [TestMethod]
        public void AdminCancel_Countdown_IsAllowed()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            rig.Tick();
            rig.Tick();
            rig.Tick();
            var match = rig.MatchOf(A);
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            Assert.AreEqual(PvpCancelOutcome.Canceled, rig.Coordinator.Cancel(match.MatchId).Outcome);

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.IsNull(rig.Sink.Saves.Single().Match.StartedAt, "never reached Live");
            Assert.AreEqual("Canceled", rig.Sink.Saves.Single().Match.EndReason);
            CollectionAssert.AreEquivalent(new[] { A, B }, rig.Gateway.Returned);
        }

        /// <summary>Drives: Cancel(matchId) during Resolving. The result stands (one save), and everyone exits now.</summary>
        [TestMethod]
        public void AdminCancel_Resolving_KeepsTheResult_AndExitsNow()
        {
            var rig = OneVOneLive(out var match);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);

            var result = rig.Coordinator.Cancel(match.MatchId);

            Assert.AreEqual(PvpMatchState.Closed, result.State);
            Assert.AreEqual(PvpMatchState.Closed, match.State);
            Assert.AreEqual(1, rig.Sink.Saves.Count);
            Assert.IsTrue(rig.Sink.Saves[0].Match.Rated, "the decided result is kept");
            CollectionAssert.AreEquivalent(new[] { A, B }, rig.Gateway.Exited);
            CollectionAssert.AreEqual(new[] { A }, rig.Gateway.Returned, "the dead player is exited but never teleported");
            Assert.AreEqual(1, rig.Spaces.Released.Count);
        }

        // ================= map names =================

        /// <summary>Drives: Join x2, Tick. The popup and Status name the map "Arena I", not its key.</summary>
        [TestMethod]
        public void Popup_AndStatus_UseTheMapDisplayName()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();

            Assert.AreEqual("A 1v1 arena match is ready on Arena I. Enter now?", rig.Gateway.Prompts.First(p => p.Id == A).Text);
            Assert.AreEqual("Arena I", rig.Coordinator.Status(A).MapName);
            Assert.AreEqual("Arena II", ArenaMapCatalog.Arena0067.DisplayName);
        }

        // ================= admission =================

        /// <summary>Drives: Join with each refusal's fact set. Each maps to its authored line.</summary>
        [TestMethod]
        public void Join_Refusals_MapToTheirLines()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha", level: 10);
            var low = rig.Coordinator.Join(A, "1v1", false);
            Assert.AreEqual(PvpJoinRefusal.BelowMinLevel, low.Refusal);
            Assert.AreEqual("[Arena] You must be at least level 50 to enter the arena.", PvpMatchCoordinator.RefusalText(low));

            rig.Gateway.Add(B, "Bravo").Facts = rig.Gateway.Players[B].Facts with { InInstance = true };
            Assert.AreEqual(PvpArenaText.InInstance, PvpMatchCoordinator.RefusalText(rig.Coordinator.Join(B, "1v1", false)));

            rig.Gateway.Add(C, "Charlie").Facts = rig.Gateway.Players[C].Facts with { PkTimerActive = true };
            Assert.AreEqual(PvpArenaText.RecentPvp, PvpMatchCoordinator.RefusalText(rig.Coordinator.Join(C, "1v1", false)));

            rig.Gateway.Add(D, "Delta");
            Assert.AreEqual(PvpJoinRefusal.DuoNeedsPair, rig.Coordinator.Join(D, "2v2", true).Refusal);
            Assert.AreEqual(PvpJoinRefusal.UnknownMode, rig.Coordinator.Join(D, "3v3", false).Refusal);

            rig.Dials = rig.Dials with { OneVOneEnabled = false };
            Assert.AreEqual("[Arena] The 1v1 arena is closed right now.", PvpMatchCoordinator.RefusalText(rig.Coordinator.Join(D, "1v1", false)));
        }

        // ================= the PK facet is no longer required (owner ruling 2026-10-03, Docs/Pvp/TEMPLATES.md) =================

        /// <summary>
        /// Drives: Join with OnPkFacet false. DISCRIMINATES the removed admission check: before templates this exact
        /// input was refused with NotOnPkFacet; now any facet may queue.
        /// </summary>
        [TestMethod]
        public void Join_OffThePkFacet_IsAdmitted()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha").Facts = rig.Gateway.Players[A].Facts with { OnPkFacet = false };

            var result = rig.Coordinator.Join(A, "1v1", false);

            Assert.AreEqual(PvpJoinRefusal.None, result.Refusal);
            CollectionAssert.AreEqual(new[] { A }, rig.Coordinator.QueuedIds("1v1").ToList());
        }

        /// <summary>Drives: Join(duo) whose partner is off the PK facet. The pair queues (it was DuoPartnerIneligible before templates).</summary>
        [TestMethod]
        public void Join_Duo_PartnerOffThePkFacet_IsAdmitted()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B });
            rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B });
            rig.Gateway.Players[B].Facts = rig.Gateway.Players[B].Facts with { OnPkFacet = false };

            var result = rig.Coordinator.Join(A, "2v2", true);

            Assert.AreEqual(PvpJoinRefusal.None, result.Refusal);
            Assert.AreEqual("Bravo", result.PartnerName);
        }

        /// <summary>
        /// Drives: Join x2, Tick (-> AwaitingAccept), flip OnPkFacet false, Accept. The accept stands (before templates it
        /// was turned into a decline with the NotOnPkFacet line).
        /// </summary>
        [TestMethod]
        public void Accept_OffThePkFacet_IsAccepted()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();

            rig.Gateway.Players[A].Facts = rig.Gateway.Players[A].Facts with { OnPkFacet = false };

            Assert.AreEqual(PvpAnswerOutcome.Accepted, rig.Coordinator.Accept(A).Outcome);
            Assert.IsFalse(rig.Gateway.Got(A, PvpArenaText.NotOnPkFacet));
        }

        // ================= overtime (Docs/Pvp/DESIGN.md "Overtime") =================

        private const string OvertimeLine1v1Default = "[Arena] Overtime! No healing for the final 3 minutes. If no one wins, the match is a draw.";

        private static bool IsOvertimeLine(string text) => text.StartsWith("[Arena] Overtime!", StringComparison.Ordinal);

        /// <summary>The latest binding published to <paramref name="id"/>, or null.</summary>
        private static PvpPlayerBinding LastBinding(Rig rig, uint id) => rig.Gateway.PublishedBindings.LastOrDefault(p => p.Id == id).Binding;

        /// <summary>
        /// Drives: the 1v1 path to Live on the SHIPPED defaults (300 s regulation, overtime on, 180 s, healing 0, ramp
        /// off), then Ticks at regulation-60, the regulation limit, overtime-end-60 and the hard end. No warning at
        /// regulation-60 (it moved); at the limit the match stays Live, goes to overtime, republishes a Live binding
        /// carrying the snapshot to both players, and announces it; the one-minute warning fires 60 s before the END
        /// of overtime; at the hard end it is the ordinary unrated timeout draw.
        /// </summary>
        [TestMethod]
        public void OneVOne_Overtime_RegulationToOvertime_Republishes_ThenDrawsAtTheHardEnd()
        {
            var rig = OneVOneLive(out var match);
            var live = match.LiveSinceUtc.Value;

            Assert.AreEqual(300, rig.Dials.TimeLimitSeconds1v1, "the shipped regulation default");
            Assert.IsTrue(rig.Dials.OvertimeEnabled, "overtime ships ON");

            rig.Clock.Advance(240);
            rig.Tick();
            Assert.AreEqual(0, rig.Gateway.Count(A, PvpArenaText.TimeWarning), "with overtime on there is no warning 60 s before REGULATION ends");
            Assert.IsFalse(rig.Coordinator.Status(A).Overtime);
            Assert.AreEqual(TimeSpan.FromSeconds(60), rig.Coordinator.Status(A).Remaining);

            var publishedBefore = rig.Gateway.PublishedBindings.Count;

            rig.Clock.Advance(60);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, match.State, "regulation ending with no winner is overtime, not a timeout");
            Assert.AreEqual(0, rig.Sink.Saves.Count);
            Assert.IsFalse(rig.Gateway.Got(A, PvpArenaText.TimeUpDraw));
            Assert.AreEqual(1, rig.Gateway.Count(A, OvertimeLine1v1Default));
            Assert.AreEqual(1, rig.Gateway.Count(B, OvertimeLine1v1Default));

            Assert.AreEqual(2, rig.Gateway.PublishedBindings.Count - publishedBefore, "one Live-to-Live republish per active player");

            foreach (var id in new[] { A, B })
            {
                var binding = LastBinding(rig, id);
                Assert.AreEqual(PvpMatchState.Live, binding.State);
                Assert.AreSame(match, binding.Match);
                Assert.AreEqual(live.AddSeconds(300), binding.OvertimeSinceUtc, "overtime starts at the regulation limit");
                Assert.AreEqual(0.0, binding.OvertimeHealingMod);
                Assert.AreEqual(0.0, binding.OvertimeRampPerMinute);
                Assert.IsTrue(PvpPlayerRules.InOvertime(binding));
            }

            var status = rig.Coordinator.Status(A);
            Assert.IsTrue(status.Overtime);
            Assert.AreEqual(TimeSpan.FromSeconds(180), status.Remaining);

            rig.Clock.Advance(119);
            rig.Tick();
            Assert.AreEqual(0, rig.Gateway.Count(A, PvpArenaText.TimeWarning));

            rig.Clock.Advance(1);
            rig.Tick();
            Assert.AreEqual(1, rig.Gateway.Count(A, PvpArenaText.TimeWarning), "one minute before the END of overtime");
            Assert.AreEqual(TimeSpan.FromSeconds(60), rig.Coordinator.Status(A).Remaining);

            rig.Clock.Advance(59);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State, "one second before the hard end it is still Live");

            rig.Clock.Advance(1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.TimeUpDraw));
            Assert.IsFalse(rig.Sink.Saves.Single().Match.Rated);
            Assert.AreEqual(0, rig.Sink.Saves.Single().Ratings.Count);
            Assert.AreEqual("draw", rig.Part(A).Result);
            Assert.AreEqual("Timeout", rig.Sink.Saves.Single().Match.EndReason);
            Assert.AreEqual(1, rig.Gateway.Count(A, PvpArenaText.TimeWarning), "the warning is sent exactly once");
        }

        /// <summary>Drives: the 1v1 path into overtime, then a Death intent. A kill in overtime is an ordinary rated win.</summary>
        [TestMethod]
        public void OneVOne_KillDuringOvertime_IsANormalRatedWin()
        {
            var rig = OneVOneLive(out var match);

            rig.Clock.Advance(rig.Dials.TimeLimitSeconds1v1 + 30);
            rig.Tick();
            Assert.IsTrue(rig.Coordinator.Status(A).Overtime);

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Win));
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.Loss));
            Assert.IsFalse(rig.Gateway.Got(A, PvpArenaText.TimeUpDraw));
            Assert.IsTrue(rig.Sink.Saves.Single().Match.Rated);
            Assert.AreEqual("Elimination", rig.Sink.Saves.Single().Match.EndReason);
            Assert.AreEqual("win", rig.Part(A).Result);
        }

        /// <summary>
        /// Drives: the 1v1 path to Live with overtime ON, then a Death intent queued so it is drained on EXACTLY the
        /// regulation-limit tick. The kill is evaluated before overtime entry, so it is an ordinary rated win: no
        /// overtime announcement, no Live-to-Live republish carrying overtime, no draw.
        /// </summary>
        [TestMethod]
        public void KillOnTheRegulationLimitTick_IsARatedWin_NotOvertime()
        {
            var rig = OneVOneLive(out var match);
            Assert.IsTrue(rig.Dials.OvertimeEnabled);

            rig.Clock.Advance(rig.Dials.TimeLimitSeconds1v1);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.Win));
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.Loss));
            Assert.IsFalse(rig.Gateway.Messages.Any(m => IsOvertimeLine(m.Text)), "no overtime announcement");
            Assert.IsFalse(rig.Gateway.Got(A, PvpArenaText.TimeUpDraw), "no draw");
            Assert.IsTrue(rig.Gateway.PublishedBindings.All(p => p.Binding.OvertimeSinceUtc == null), "no binding ever carried overtime");
            Assert.IsTrue(rig.Sink.Saves.Single().Match.Rated);
            Assert.AreEqual("Elimination", rig.Sink.Saves.Single().Match.EndReason);
            Assert.AreEqual("win", rig.Part(A).Result);
        }

        /// <summary>
        /// Drives: the 1v1 path to Live with pvp_arena_overtime_enabled FALSE, then the regulation limit. Exactly the
        /// old behavior: the warning at limit-60, the draw AT the limit, no overtime line, and no binding republished
        /// between Live and Resolving. Repeated with the toggle on but a NEGATIVE and a ZERO duration, which count as
        /// overtime off.
        /// </summary>
        [TestMethod]
        public void OvertimeOff_IsExactlyTheOldTimeout_IncludingANonPositiveDuration()
        {
            foreach (var off in new[]
            {
                PvpTunables.Defaults with { OvertimeEnabled = false },
                PvpTunables.Defaults with { OvertimeSeconds = -30 },
                PvpTunables.Defaults with { OvertimeSeconds = 0 },
            })
            {
                var rig = OneVOneLive(out var match);
                rig.Dials = off;

                var publishedAtLive = rig.Gateway.PublishedBindings.Count;

                rig.Clock.Advance(rig.Dials.TimeLimitSeconds1v1 - 60);
                rig.Tick();
                Assert.AreEqual(1, rig.Gateway.Count(A, PvpArenaText.TimeWarning), $"{off.OvertimeEnabled}/{off.OvertimeSeconds}: the regulation warning is back with overtime off");

                rig.Clock.Advance(60);
                rig.Tick();

                Assert.AreEqual(PvpMatchState.Resolving, match.State, $"{off.OvertimeEnabled}/{off.OvertimeSeconds}: resolves AT the regulation limit");
                Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.TimeUpDraw));
                Assert.IsFalse(rig.Gateway.Messages.Any(m => IsOvertimeLine(m.Text)), "no overtime announcement");
                Assert.IsFalse(rig.Gateway.PublishedBindings.Skip(publishedAtLive).Any(p => p.Binding.State == PvpMatchState.Live), "no Live-to-Live republish");
                Assert.IsTrue(rig.Gateway.PublishedBindings.All(p => p.Binding.OvertimeSinceUtc == null), "no binding ever carries overtime");
            }
        }

        /// <summary>
        /// Drives: the 1v1 path into overtime, then EVERY overtime setting and the regulation limit edited mid-round
        /// (overtime seconds 10, healing 1.0, ramp 0.5, overtime disabled, regulation 1000). The round keeps its
        /// snapshot: it still ends at the original hard end, /arena status still counts down the original overtime,
        /// and no further binding is published.
        /// </summary>
        [TestMethod]
        public void MidRoundSettingEdit_DoesNotChangeALiveOvertimeRound()
        {
            var rig = OneVOneLive(out var match);

            rig.Clock.Advance(300);
            rig.Tick();
            Assert.IsTrue(rig.Coordinator.Status(A).Overtime);

            var published = rig.Gateway.PublishedBindings.Count;

            rig.Dials = rig.Dials with { OvertimeSeconds = 10, OvertimeHealingMod = 1.0, OvertimeDamageRampPerMinute = 0.5, OvertimeEnabled = false, TimeLimitSeconds1v1 = 1000 };

            rig.Clock.Advance(11);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State, "a shortened overtime setting must not end this round early");
            Assert.AreEqual(TimeSpan.FromSeconds(169), rig.Coordinator.Status(A).Remaining, "status counts down the snapshotted overtime");
            Assert.IsTrue(rig.Coordinator.Status(A).Overtime);
            Assert.AreEqual(published, rig.Gateway.PublishedBindings.Count, "no binding is republished by a settings edit");
            Assert.AreEqual(0.0, LastBinding(rig, A).OvertimeHealingMod, "the binding keeps the healing mod it was given");

            rig.Clock.Advance(169);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State, "ends at the ORIGINAL hard end (300 + 180), not 1000 + anything");
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.TimeUpDraw));
        }

        /// <summary>
        /// Drives: the 1v1 path to Live, the regulation limit LOWERED below the time already played, then a Tick.
        /// Overtime starts now (the current whole second), not in the past, so it still runs its full length.
        /// </summary>
        [TestMethod]
        public void RegulationLoweredMidRound_OvertimeStillRunsItsFullLength()
        {
            var rig = OneVOneLive(out var match);

            rig.Clock.Advance(200);
            rig.Tick();
            rig.Dials = rig.Dials with { TimeLimitSeconds1v1 = 100 };
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.AreEqual(match.LiveSinceUtc.Value.AddSeconds(200), LastBinding(rig, A).OvertimeSinceUtc);
            Assert.AreEqual(TimeSpan.FromSeconds(180), rig.Coordinator.Status(A).Remaining);
        }

        /// <summary>
        /// Drives: 5-player FFA to Live, one Death, then past regulation and to the hard end. FFA gets overtime too,
        /// with its own announcement, and at the hard end keeps the old FFA timeout: survivors share first, rated.
        /// </summary>
        [TestMethod]
        public void Ffa_Overtime_ThenSurvivorsShareFirst_Rated()
        {
            var rig = FfaRig(5, out var ids);
            rig.Dials = rig.Dials with { FfaTargetPlayers = 5 };
            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);
            rig.Tick();
            rig.AcceptAll(ids);
            var match = rig.DriveAcceptedToLive(A);

            rig.Death(E, match.MatchId, killer: A);
            rig.Tick();

            rig.Clock.Advance(300);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Overtime! No healing for the final 3 minutes. If time runs out, the survivors share first place."));

            rig.Clock.Advance(180);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.TimeUpFfa));
            foreach (var id in new[] { A, B, C, D })
                Assert.AreEqual(1, rig.Part(id).Placement, $"survivor 0x{id:X8} shares first");
            Assert.AreEqual(5, rig.Part(E).Placement);
            Assert.IsTrue(rig.Sink.Saves.Single().Match.Rated);
        }

        /// <summary>The ramp is mentioned only when it is on, the duration follows the setting, and a reduced (not zero) heal says so.</summary>
        [TestMethod]
        public void OvertimeAnnouncement_FollowsTheSnapshot()
        {
            var rig = OneVOneLive(out var match);
            rig.Dials = rig.Dials with { OvertimeSeconds = 60, OvertimeDamageRampPerMinute = 0.1, OvertimeHealingMod = 0.5 };

            rig.Clock.Advance(300);
            rig.Tick();

            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Overtime! Healing is reduced for the final 1 minute. Damage rises by 10% each minute. If no one wins, the match is a draw."));
            Assert.AreEqual(0.1, LastBinding(rig, A).OvertimeRampPerMinute);
            Assert.AreEqual(0.5, LastBinding(rig, A).OvertimeHealingMod);

            rig.Clock.Advance(60);
            rig.Tick();
            Assert.AreEqual(0, rig.Gateway.Count(A, PvpArenaText.TimeWarning), "a one-minute overtime has no separate one-minute warning");
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
        }

        [TestMethod]
        public void OvertimeAnnouncement_Text_Pure()
        {
            Assert.AreEqual(OvertimeLine1v1Default, PvpArenaText.OvertimeAnnouncement(180, 0.0, 0.0, false));
            Assert.AreEqual("[Arena] Overtime! The final 90 seconds begins. If no one wins, the match is a draw.", PvpArenaText.OvertimeAnnouncement(90, 1.0, 0.0, false));
            Assert.AreEqual("[Arena] Overtime! No healing for the final 2 minutes. Damage rises by 25% each minute. If time runs out, the survivors share first place.", PvpArenaText.OvertimeAnnouncement(120, 0.0, 0.25, true));
            Assert.AreEqual("1 second", PvpArenaText.DurationPhrase(1));
            Assert.AreEqual("1 minute", PvpArenaText.DurationPhrase(60));
            Assert.AreEqual("61 seconds", PvpArenaText.DurationPhrase(61));
        }

        // ================= Blood payout (Docs/Pvp/DESIGN.md "Rewards") =================

        /// <summary>
        /// Drives: the 1v1 path to Live, a Death, Tick. The winner is granted pvp_arena_blood_win and the loser
        /// pvp_arena_blood_loss, one grant call each, carrying the cap dials. Run at the shipped 2/1 and at 7/3, so a
        /// hard-coded amount fails one of the two rows.
        /// </summary>
        [DataTestMethod]
        [DataRow(2, 1)]
        [DataRow(7, 3)]
        public void Blood_OneVOneKill_WinnerGetsWin_LoserGetsLoss(int win, int loss)
        {
            var rig = OneVOneLive(out var match);
            rig.Dials = rig.Dials with { BloodWin = win, BloodLoss = loss, BloodDailyCap = 4, BloodResetHour = 6, BloodResetTimezone = "UTC" };

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual((win, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((loss, 1), rig.Gateway.Blood(B));

            var grant = rig.Gateway.BloodGrants[0].Grant;
            Assert.AreEqual(match.MatchId, grant.MatchId);
            Assert.AreEqual(4, grant.DailyCap, "the cap dial travels with the grant; the gateway applies it");
            Assert.AreEqual(6, grant.ResetHour);
            Assert.AreEqual("UTC", grant.ResetTimezone);
        }

        /// <summary>
        /// Drives: the 1v1 path to Live, a Death, Tick (Resolving, save queued with its callback held), then the save
        /// callback invoked by hand with Failed, Tick, again with Ambiguous, Tick, the post-match Tick (Closed), and more
        /// Ticks. Exactly one grant per participant throughout: no save result, and no later state, pays again.
        /// </summary>
        [TestMethod]
        public void Blood_IsGrantedExactlyOnce_ThroughResolvingAndClosed_WithFailedThenAmbiguousSaveResults()
        {
            var rig = OneVOneLive(out var match);
            rig.Sink.AutoComplete = false;

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(2, rig.Gateway.BloodGrants.Count, "paid at Resolve");

            var callback = rig.Sink.RawSaveCallbacks.Single();

            callback(PvpMatchSaveResult.Failed, 0);
            rig.Tick();
            Assert.AreEqual(PvpMatchSaveResult.Failed, rig.Coordinator.LastSaveResult);

            callback(PvpMatchSaveResult.Ambiguous, 0);
            rig.Tick();
            Assert.AreEqual(PvpMatchSaveResult.Ambiguous, rig.Coordinator.LastSaveResult);
            Assert.AreEqual(2, rig.Coordinator.SaveResultsHandled);

            rig.Gateway.Players[B].Instance = 0;
            rig.PostMatch();
            Assert.AreEqual(PvpMatchState.Closed, match.State);

            for (var i = 0; i < 5; i++)
            {
                rig.Clock.Advance(1);
                rig.Tick();
            }

            Assert.AreEqual((2, 1), rig.Gateway.Blood(A), "one grant for the winner, ever");
            Assert.AreEqual((1, 1), rig.Gateway.Blood(B), "one grant for the loser, ever");
            Assert.AreEqual(2, rig.Gateway.BloodGrants.Count);
            Assert.AreEqual(1, rig.Sink.Saves.Count);
        }

        /// <summary>Drives: the 1v1 path to Live, Bravo leaves the instance (a forfeit), Tick. Bravo gets nothing; Alpha the win amount.</summary>
        [TestMethod]
        public void Blood_Forfeiter_GetsZero_OpponentGetsWin()
        {
            var rig = OneVOneLive(out var match);

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual((2, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((0, 0), rig.Gateway.Blood(B), "a forfeiter is never even granted 0");
        }

        /// <summary>Drives: 1v1 to Countdown, Bravo walks out, Tick. Resolved (rated) but never Live: no Blood for anyone.</summary>
        [TestMethod]
        public void Blood_WalkoutBeforeLive_PaysNobody()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            var match = rig.MatchOf(A);
            rig.AcceptAll(A, B);
            rig.Tick();
            rig.Tick();
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Countdown, match.State);

            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual("win", rig.Part(A).Result, "the match did resolve with a winner");
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count, "but it never went Live");
        }

        /// <summary>Drives: the 1v1 path to Live, Tick past the time limit with overtime off. An unrated timeout draw: the draw amount each.</summary>
        [TestMethod]
        public void Blood_TimeoutDraw_PaysTheDrawAmountEach()
        {
            var rig = OneVOneLive(out var match);
            rig.Dials = rig.Dials with { OvertimeEnabled = false, BloodDraw = 5 };

            rig.Clock.Advance(rig.Dials.TimeLimitSeconds1v1);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsFalse(rig.Sink.Saves.Single().Match.Rated, "unrated matches still pay");
            Assert.AreEqual((5, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((5, 1), rig.Gateway.Blood(B));
        }

        /// <summary>Drives: the 1v1 path to Live, both Death intents in one tick. A double knockout draw: the draw amount each.</summary>
        [TestMethod]
        public void Blood_DoubleKnockout_PaysTheDrawAmountEach()
        {
            var rig = OneVOneLive(out var match);
            rig.Dials = rig.Dials with { BloodDraw = 5 };

            rig.Death(A, match.MatchId, killer: B);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual("draw", rig.Part(A).Result);
            Assert.AreEqual((5, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((5, 1), rig.Gateway.Blood(B));
        }

        /// <summary>
        /// Drives: an FFA of 5 to Live, Echo leaves (forfeit), Delta and Charlie die, Tick past the time limit (overtime
        /// off). Alpha and Bravo share first: the win amount each. Charlie and Delta: the loss amount. Echo: nothing.
        /// </summary>
        [TestMethod]
        public void Blood_Ffa_SharedFirstGetWin_OthersGetLoss_ForfeiterNothing()
        {
            var rig = FfaRig(5, out var ids);
            rig.Dials = rig.Dials with { FfaTargetPlayers = 5, OvertimeEnabled = false, BloodEnabled = true, BloodWin = 7, BloodLoss = 3 };
            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);
            rig.Tick();
            rig.AcceptAll(ids);
            var match = rig.DriveAcceptedToLive(A);

            rig.Gateway.Players[E].Instance = 0;
            rig.Tick();
            rig.Death(D, match.MatchId, killer: A);
            rig.Tick();
            rig.Death(C, match.MatchId, killer: B);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);

            rig.Clock.Advance(rig.Dials.TimeLimitSecondsFfa);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(1, rig.Part(A).Placement);
            Assert.AreEqual(1, rig.Part(B).Placement);
            Assert.AreEqual(3, rig.Part(C).Placement, "third");
            Assert.AreEqual((7, 1), rig.Gateway.Blood(A));
            Assert.AreEqual((7, 1), rig.Gateway.Blood(B));
            Assert.AreEqual((3, 1), rig.Gateway.Blood(C), "third place gets the loss amount");
            Assert.AreEqual((3, 1), rig.Gateway.Blood(D));
            Assert.AreEqual((0, 0), rig.Gateway.Blood(E));
        }

        /// <summary>Drives: the 1v1 path to Live, then an admin Cancel. A canceled match never pays.</summary>
        [TestMethod]
        public void Blood_AdminCancelLive_PaysNobody()
        {
            var rig = OneVOneLive(out var match);

            rig.Coordinator.Cancel(match.MatchId);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Canceled, match.State);
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count);
        }

        /// <summary>Drives: the 1v1 path to Live with pvp_arena_blood_enabled off, a Death, Tick. No grants.</summary>
        [TestMethod]
        public void Blood_Disabled_PaysNobody()
        {
            var rig = OneVOneLive(out var match);
            rig.Dials = rig.Dials with { BloodEnabled = false };

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count);
        }

        /// <summary>
        /// Drives: a 1v1 between two players on the same IP with pvp_arena_block_same_ip off, to Live, a Death, Tick.
        /// The existing same-IP rule (unrated) also withholds Blood from both.
        /// </summary>
        [TestMethod]
        public void Blood_SameIpOpponents_PayNobody()
        {
            var rig = new Rig();
            rig.Dials = PvpTunables.Defaults with { BlockSameIp = false, BloodEnabled = true };
            rig.Build();
            rig.Gateway.Add(A, "Alpha", ip: "10.9.9.9");
            rig.Gateway.Add(B, "Bravo", ip: "10.9.9.9");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            var match = rig.DriveAcceptedToLive(A);

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            Assert.IsFalse(rig.Sink.Saves.Single().Match.Rated);
            Assert.AreEqual(0, rig.Gateway.BloodGrants.Count);
        }
    }
}