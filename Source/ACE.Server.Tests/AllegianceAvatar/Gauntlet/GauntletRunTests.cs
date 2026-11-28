using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Managers;
using ACE.Server.Tests;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Position = ACE.Entity.Position;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// GauntletManager and GauntletRun, the run lifecycle, all through seams: a stepped clock, a fake world whose landblock
    /// queue the test drains by hand (so queue ordering is explicit), a fake spawn sink, a fake history store, a fake Favor
    /// side and a fake or real record cache. No PropertyManager key is read (the tuning is handed in) except by the one tuning
    /// snapshot test, which seeds every key from the compiled defaults and restores them in a finally. The two tests that use
    /// the real GauntletRecords save and restore every one of its seams.
    /// </summary>
    [TestClass]
    public class GauntletRunTests
    {
        private const uint AvatarId = 4100;
        private const uint Monarch = 0x50000041;
        private const ulong Price = 3000;
        private const uint MemberGuid = 0x50000099;

        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        // ---- fakes -----------------------------------------------------------------------------------------------------

        private sealed class FakeWorld : IGauntletWorld
        {
            public bool OpenOk = true;
            public bool Loaded = true;
            public bool EnqueueOk = true;
            public uint NextInstance = 0x7F010001;
            public List<GauntletOccupant> Inside = new List<GauntletOccupant>();
            public readonly List<Action> Queue = new List<Action>();
            public readonly List<string> Lines = new List<string>();
            public readonly List<(uint, string)> AllegianceLines = new List<(uint, string)>();
            public readonly List<(uint RunKey, string Line)> Evictions = new List<(uint, string)>();

            public bool OpenCopy(GauntletRun run, out uint instance, out string failure)
            {
                instance = OpenOk ? NextInstance++ : 0;
                failure = OpenOk ? null : "fake load failure";
                return OpenOk;
            }

            public bool IsCopyLoaded(uint instance) => Loaded;

            public bool Enqueue(GauntletRun run, Action action)
            {
                if (!EnqueueOk) return false;
                Queue.Add(action);
                return true;
            }

            public IReadOnlyList<GauntletOccupant> Occupants(GauntletRun run) => Inside.ToList();
            public void TellInside(GauntletRun run, string text) => Lines.Add(text);
            /// <summary>The guids an allegiance send reaches (the online members it found).</summary>
            public HashSet<uint> AllegianceReach = new HashSet<uint>();
            public ICollection<uint> ToAllegiance(uint monarchGuid, string text) { AllegianceLines.Add((monarchGuid, text)); return new HashSet<uint>(AllegianceReach); }

            /// <summary>Lines told inside, per occupant, minus the excepted ones (the per-member view of TellInsideExcept).</summary>
            public readonly List<(uint Guid, string Line)> InsideLinesByMember = new List<(uint, string)>();
            public void TellInsideExcept(GauntletRun run, string text, ICollection<uint> except)
            {
                Lines.Add(text);
                foreach (var o in Inside)
                    if (except == null || !except.Contains(o.Guid))
                        InsideLinesByMember.Add((o.Guid, text));
            }
            public readonly List<(uint Guid, string Line)> CharacterLines = new List<(uint, string)>();
            public void TellCharacter(uint characterGuid, string text) => CharacterLines.Add((characterGuid, text));
            public void EvictAndRelease(GauntletRun run, string line) => Evictions.Add((run.RunKey, line));

            public void Drain()
            {
                while (Queue.Count > 0)
                {
                    var batch = Queue.ToList();
                    Queue.Clear();
                    foreach (var a in batch) a();
                }
            }
        }

        private sealed class FakeStore : IGauntletStore
        {
            public uint NextId = 501;
            public bool InsertAnswers = true;
            public readonly List<string> Calls = new List<string>();
            public readonly List<AllegianceGauntletRun> Inserted = new List<AllegianceGauntletRun>();
            public readonly List<AllegianceGauntletRun> RunWrites = new List<AllegianceGauntletRun>();
            public readonly List<AllegianceGauntletWave> Waves = new List<AllegianceGauntletWave>();
            public readonly List<DateTime> Closes = new List<DateTime>();

            public void InsertRun(AllegianceGauntletRun row, Action<bool, uint> callback)
            {
                Calls.Add("insert");
                Inserted.Add(row);
                if (InsertAnswers) callback(true, NextId++);
            }

            public void SaveRunEnd(AllegianceGauntletRun row) { Calls.Add("run"); RunWrites.Add(row); }
            public void SaveWave(AllegianceGauntletWave row) { Calls.Add("wave"); Waves.Add(row); }

            public bool CloseOpenRuns(DateTime nowUtc, out int closed)
            {
                Calls.Add("close");
                Closes.Add(nowUtc);
                closed = 2;
                return true;
            }

            public AllegianceGauntletRun LastRunRow => RunWrites.LastOrDefault();
        }

        private sealed class FakeCreature : IGauntletSpawnedCreature
        {
            public uint Guid { get; init; }
            public uint SpawnMax { get; init; } = 1000;
            public DungeonRole Role { get; init; }
            public bool Alive = true;
            public double Dealt;
            public long Stamp = 7;
            public int Pulls;
            public int Engages;
            public bool Destroyed;

            public bool IsAlive => Alive && !Destroyed;
            public double SampleDealt() => Dealt;
            public long ActivityStamp() => Stamp;
            public void Engage() => Engages++;
            public bool PullTo(Position centre) { Pulls++; return true; }
            public void Destroy() { Destroyed = true; }
        }

        private sealed class FakeSpawner : IGauntletSpawnSink
        {
            private uint nextGuid = 0x7F2D0001;
            public readonly List<FakeCreature> Spawned = new List<FakeCreature>();
            public readonly List<string> Calls = new List<string>();
            public readonly HashSet<uint> FailWcids = new HashSet<uint>();
            public uint ObservedValue = 4321;
            public readonly List<uint> ObservedPassed = new List<uint>();

            public uint ObservedMaxNonBoss(GauntletWavePlan wave) { Calls.Add("observed"); return ObservedValue; }

            public IGauntletSpawnedCreature Spawn(GauntletWavePlan wave, PortalWaveSpawn spawn, uint observedMaxNonBoss, GauntletRun owner)
            {
                Calls.Add($"spawn:{spawn.Entry.Role}");
                ObservedPassed.Add(observedMaxNonBoss);

                if (FailWcids.Contains(spawn.Entry.Wcid))
                    return null;

                var c = new FakeCreature { Guid = nextGuid++, Role = spawn.Entry.Role };
                Spawned.Add(c);
                return c;
            }
        }

        private sealed class FakeFavor : IGauntletFavor
        {
            public readonly List<(uint, ulong)> Commits = new List<(uint, ulong)>();
            public readonly List<(uint, ulong)> Releases = new List<(uint, ulong)>();
            public readonly List<(uint, ulong)> Refunds = new List<(uint, ulong)>();
            public void Commit(uint avatarId, ulong price) => Commits.Add((avatarId, price));
            public void Release(uint avatarId, ulong price) => Releases.Add((avatarId, price));
            public bool RefundCommitted(uint avatarId, ulong price) { Refunds.Add((avatarId, price)); return true; }
        }

        private sealed class FakeRecords : IGauntletRecordSink
        {
            public long Top = 0;
            public readonly List<string> Calls = new List<string>();
            public readonly List<(uint Avatar, long Score, uint? RunId)> Banks = new List<(uint, long, uint?)>();
            public readonly List<(uint RunId, uint Avatar, uint? Monarch, long Score, long Snapshot, int Fought)> Announces = new();
            public readonly List<(uint Avatar, DateTime At)> Commits = new List<(uint, DateTime)>();
            public readonly Dictionary<uint, DateTime> Last = new Dictionary<uint, DateTime>();

            public long SnapshotTop() { Calls.Add("snapshot"); return Top; }

            public bool Bank(uint avatarId, long scoreCenti, int participants, uint? runId, DateTime nowUtc)
            {
                Calls.Add("bank");
                Banks.Add((avatarId, scoreCenti, runId));
                return true;
            }

            public bool TryAnnounce(uint runId, uint avatarId, uint? monarchGuid, long scoreCenti, long snapshotTop, int fought)
            {
                Calls.Add("announce");
                Announces.Add((runId, avatarId, monarchGuid, scoreCenti, snapshotTop, fought));
                return false;
            }

            public void NoteRunCommitted(uint avatarId, DateTime nowUtc)
            {
                Calls.Add("commit");
                Commits.Add((avatarId, nowUtc));
                Last[avatarId] = nowUtc;
            }

            public DateTime? LastRunAt(uint avatarId) => Last.TryGetValue(avatarId, out var at) ? at : null;
        }

        /// <summary>Forwards to an inner sink and records what TryAnnounce was handed.</summary>
        private sealed class RecordingRecords : IGauntletRecordSink
        {
            private readonly IGauntletRecordSink inner;
            public readonly List<(uint RunId, long Score, long Snapshot, int Fought)> Announces = new();
            public RecordingRecords(IGauntletRecordSink inner) { this.inner = inner; }
            public long SnapshotTop() => inner.SnapshotTop();
            public bool Bank(uint a, long s, int p, uint? r, DateTime n) => inner.Bank(a, s, p, r, n);
            public bool TryAnnounce(uint runId, uint avatarId, uint? monarchGuid, long scoreCenti, long snapshotTop, int fought)
            {
                Announces.Add((runId, scoreCenti, snapshotTop, fought));
                return inner.TryAnnounce(runId, avatarId, monarchGuid, scoreCenti, snapshotTop, fought);
            }
            public void NoteRunCommitted(uint a, DateTime n) => inner.NoteRunCommitted(a, n);
            public DateTime? LastRunAt(uint a) => inner.LastRunAt(a);
        }

        private sealed class RecordingHooks : IGauntletFighterHooks, IGauntletDeathHooks, IGauntletRewardHooks, IGauntletGateHooks, IGauntletRingHooks
        {
            void IGauntletRingHooks.Place(GauntletRun run) => Calls.Add("ring.place");
            void IGauntletRingHooks.Remove(GauntletRun run) => Calls.Add("ring.remove");
            public readonly List<string> Calls = new List<string>();
            public readonly List<GauntletWavePlan> Restamps = new List<GauntletWavePlan>();
            public int Window = NoGauntletHooks.DefaultClaimWindowSeconds;

            public void Spawn(GauntletRun run) => Calls.Add("fighter.spawn");
            public readonly List<bool> RestampClears = new List<bool>();
            public void Restamp(GauntletRun run, GauntletWavePlan wave, bool clearBuffs) { Calls.Add("fighter.restamp"); Restamps.Add(wave); RestampClears.Add(clearBuffs); }
            public void Respawn(GauntletRun run) => Calls.Add("fighter.respawn");
            void IGauntletFighterHooks.Remove(GauntletRun run) => Calls.Add("fighter.remove");
            public void Ensure(GauntletRun run) { }
            public readonly HashSet<uint> Held = new HashSet<uint>();
            public double HoldSecondsLeft(GauntletRun run, Player player) => player != null && Held.Contains(player.Guid.Full) ? 30 : 0;
            public void ReleaseAll(GauntletRun run) => Calls.Add("death.releaseall");
            public void ReleaseToCaches(GauntletRun run, Position caches) { Calls.Add("death.tocaches"); CacheTargets.Add(caches); }
            public readonly List<Position> CacheTargets = new List<Position>();
            public int BeginRewarding(GauntletRun run) { Calls.Add("rewards.begin"); return Window; }
            public void DeliverDirect(GauntletRun run) => Calls.Add("rewards.direct");
            public void EndRewarding(GauntletRun run) => Calls.Add("rewards.end");
            public void Place(GauntletRun run, Position npcLocation) => Calls.Add("gate.place");
            void IGauntletGateHooks.Remove(GauntletRun run) => Calls.Add("gate.remove");
            public void Ensure(GauntletRun run, Position npcLocation) => Calls.Add("gate.ensure");
        }

        // ---- harness -------------------------------------------------------------------------------------------------------

        private sealed class Harness
        {
            public DateTime Now = T0;
            public readonly FakeWorld World = new FakeWorld();
            public readonly FakeStore Store = new FakeStore();
            public readonly FakeSpawner Spawner = new FakeSpawner();
            public readonly FakeFavor Favor = new FakeFavor();
            public IGauntletRecordSink Records = new FakeRecords();
            public readonly RecordingHooks Hooks = new RecordingHooks();
            public GauntletTuning Tuning = GauntletTuning.Defaults;
            public Func<int, int, GauntletWavePlan> Plans;
            public readonly List<(int Wave, int Headcount, int Seed)> PlanCalls = new List<(int, int, int)>();
            public Func<uint, Func<Player, bool>> MemberLookup = _ => _ => true;
            public GauntletSiteDef Site = GauntletFixtureSites.Site(GauntletFixtureSites.SixPortalRow());
            public bool UseNoOpHooks;
            public Func<GauntletTuning> TuningSource;
            public GauntletWavePlanSource PlannerOverride;
            private GauntletManager manager;

            public FakeRecords FakeRecords => (FakeRecords)Records;

            public GauntletManager Manager => manager ??= new GauntletManager(new GauntletEnvironment
            {
                UtcNow = () => Now,
                Tuning = TuningSource ?? (() => Tuning),
                Sites = () => new[] { Site },
                World = World,
                Store = Store,
                Spawner = Spawner,
                Favor = Favor,
                Records = Records,
                Planner = (tuning, site, wave, headcount, seed, label) =>
                {
                    PlanCalls.Add((wave, headcount, seed));
                    return PlannerOverride != null
                        ? PlannerOverride(tuning, site, wave, headcount, seed, label)
                        : (Plans ?? ((w, h) => TrashPlan(w, 3)))(wave, headcount);
                },
                Hooks = UseNoOpHooks ? new GauntletRunHooks() : new GauntletRunHooks { Fighter = Hooks, Death = Hooks, Rewards = Hooks, Gate = Hooks, Ring = Hooks },
                MemberLookup = MemberLookup,
                Random = new Random(5),
            });

            public GauntletRun Start(uint avatarId = AvatarId)
            {
                Assert.IsTrue(Manager.TryStart(Request(avatarId), out var refusal), refusal);
                return Manager.FindByAvatar(avatarId);
            }

            public GauntletStartRequest Request(uint avatarId = AvatarId) => new GauntletStartRequest
            {
                AvatarId = avatarId,
                MonarchGuid = Monarch,
                BuyerGuid = MemberGuid,
                Price = Price,
                NowUtc = Now,
                NpcLocation = NpcAt,
            };

            public Position NpcAt;

            public void Inside(params uint[] guids) => World.Inside = guids.Select(g => new GauntletOccupant(g, null)).ToList();

            /// <summary>Steps the clock in 0.5 s ticks, draining the landblock queue after each, until <paramref name="seconds"/> have passed.</summary>
            public void Advance(double seconds)
            {
                var end = Now.AddSeconds(seconds);

                while (Now < end)
                {
                    Now = Now.AddSeconds(0.5);
                    if (Now > end) Now = end;
                    Manager.TickAt(Now);
                    World.Drain();
                }
            }

            /// <summary>Starts a run with one member inside and steps it into Active(1) (staging 120 s).</summary>
            public GauntletRun StartActive()
            {
                Inside(MemberGuid);
                var run = Start();
                Advance(Tuning.StagingSeconds + 0.5);
                Assert.AreEqual(GauntletState.Active, run.State, "fixture: the run is Active after staging");
                return run;
            }

            public void Kill(GauntletRun run, FakeCreature c)
            {
                c.Alive = false;
                run.NoteCreatureDied(c.Guid, run.CurrentWave);
            }
        }

        private static readonly GauntletRules Rules = new GauntletRules(GauntletTuning.Defaults);

        /// <summary>A wave of <paramref name="trash"/> trash at the centre point, delays 2.0, 2.5, ...; a boss at 2.0 when asked.</summary>
        private static GauntletWavePlan TrashPlan(int wave, int trash, bool boss = false, string refusal = null)
        {
            var point = GauntletFixtureSites.At(100, 96);
            var spawns = new List<PortalWaveSpawn>();

            if (refusal == null)
            {
                for (var i = 0; i < trash; i++)
                    spawns.Add(new PortalWaveSpawn(0, 2.0 + 0.5 * i, new DungeonSpawnPlanEntry((uint)(0x7F1E0000 + i), DungeonRole.Trash, point)));

                if (boss)
                    spawns.Add(new PortalWaveSpawn(0, 2.0, new DungeonSpawnPlanEntry(0x7F1EB055, DungeonRole.Boss, point)));
            }

            return new GauntletWavePlan
            {
                Wave = wave,
                Level = Rules.LevelOf(wave),
                IsBoss = boss,
                Headcount = 1,
                Plan = new DungeonSpawnPlan { FamilyId = "testfamily", HealthCurveTarget = 1000 },
                TimeLimitSeconds = Rules.TimeLimitSeconds(wave),
                Spawns = spawns.OrderBy(s => s.Delay).ToList(),
                UnspawnedHealthEstimate = new Dictionary<DungeonRole, double> { [DungeonRole.Trash] = 1000, [DungeonRole.Elite] = 1000, [DungeonRole.Boss] = 5000 },
                Refusal = refusal,
            };
        }

        // ---- Staging ----------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Staging_NobodyInside_AbortsAndReleases()
        {
            var h = new Harness();
            var run = h.Start();

            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Staging, run.State, "the copy loaded");
            CollectionAssert.Contains(h.Hooks.Calls, "gate.place");
            CollectionAssert.Contains(h.Hooks.Calls, "fighter.spawn");
            CollectionAssert.Contains(h.Hooks.Calls, "ring.place", "the circle is marked at Staging");
            CollectionAssert.DoesNotContain(h.Hooks.Calls, "ring.remove");

            h.Advance(119);
            Assert.AreEqual(GauntletState.Staging, run.State, "still counting down");

            h.Advance(2);
            Assert.AreEqual(GauntletState.Aborted, run.State);
            Assert.AreEqual(GauntletEndReason.Aborted, run.EndReason);
            CollectionAssert.AreEqual(new[] { (AvatarId, Price) }, h.Favor.Releases, "the reservation is released exactly once");
            Assert.AreEqual(0, h.Favor.Commits.Count, "nothing committed");
            Assert.AreEqual(0, h.FakeRecords.Commits.Count, "no cooldown for a run that never began");
            Assert.AreEqual(0, h.PlanCalls.Count, "no wave was planned");
            Assert.AreEqual((byte)GauntletEndReason.Aborted, h.Store.LastRunRow.EndReason, "the history row is closed as Aborted");
            Assert.AreEqual((byte)0, h.Store.LastRunRow.FavorCommitted);
            Assert.AreEqual(1, h.World.Evictions.Count, "the copy is let go");
            Assert.IsTrue(h.World.AllegianceLines.Any(l => l.Item2.Contains("called off")));
            CollectionAssert.Contains(h.Hooks.Calls, "ring.remove", "the ring is taken out at close");
            Assert.AreEqual(0, h.Manager.RunCount, "the manager drops the run");
            Assert.IsFalse(run.Hold.HoldsMembers, "the member hold is released");
        }

        [TestMethod]
        public void Staging_OneInside_CommitsAtWave1()
        {
            var h = new Harness();
            h.Inside(MemberGuid);
            var run = h.Start();

            h.Advance(119);
            Assert.AreEqual(GauntletState.Staging, run.State);
            Assert.AreEqual(0, h.Favor.Commits.Count, "nothing is committed during staging");
            Assert.IsTrue(h.World.Lines.Any(l => l.Contains("begins in 60 seconds")), "the 60 s line");
            Assert.IsFalse(h.World.Lines.Any(l => l.Contains("begins in 1 second.")), "control: 1.5 s left");

            h.Advance(1.5);
            Assert.IsTrue(h.World.Lines.Any(l => l.Contains("begins in 1 second.")), "the 1 s line");
            Assert.AreEqual(GauntletState.Active, run.State);
            Assert.AreEqual(1, run.CurrentWave);
            CollectionAssert.AreEqual(new[] { (AvatarId, Price) }, h.Favor.Commits, "committed once, at wave 1");
            Assert.AreEqual(0, h.Favor.Releases.Count, "a committed run never releases");
            Assert.AreEqual(1, h.FakeRecords.Commits.Count);
            Assert.AreEqual(1, h.PlanCalls.Count);
            Assert.AreEqual((1, 1), (h.PlanCalls[0].Wave, h.PlanCalls[0].Headcount), "wave 1 planned for the one member inside");
            Assert.IsNotNull(run.CommittedAt);

            h.Advance(4);
            Assert.AreEqual(3, h.Spawner.Spawned.Count, "the stream entered through the landblock queue");
            Assert.AreEqual((byte)1, h.Store.LastRunRow.FavorCommitted, "the commit is written to the history row");
            Assert.IsNotNull(h.Store.LastRunRow.ActiveAt);
            Assert.IsNull(h.Store.LastRunRow.EndedAt);
        }

        // ---- waves ----------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Wave_ClearedBeforeLimit_GoesToIntermission_BanksRecord()
        {
            var h = new Harness();
            var run = h.StartActive();

            h.Advance(4);
            Assert.AreEqual(3, h.Spawner.Spawned.Count);

            h.Kill(run, h.Spawner.Spawned[0]);
            h.Kill(run, h.Spawner.Spawned[1]);
            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Active, run.State, "one still alive");

            h.Kill(run, h.Spawner.Spawned[2]);
            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Intermission, run.State);
            Assert.AreEqual(1, run.WavesCleared);
            Assert.IsTrue(h.FakeRecords.Banks.Any(b => b.Avatar == AvatarId && b.Score == 100 && b.RunId == run.RunId), "the clear is banked at 100");

            h.Advance(0.5);
            var row = h.Store.Waves.Single();
            Assert.AreEqual((ushort)1, row.Wave);
            Assert.AreEqual((byte)0, row.Outcome, "Cleared");
            Assert.AreEqual((ushort)3, row.Killed);
            Assert.AreEqual("testfamily", row.Family);
            Assert.AreEqual(run.RunId, row.RunId);

            h.Advance(20);
            Assert.AreEqual(GauntletState.Active, run.State, "the intermission is over");
            Assert.AreEqual(2, run.CurrentWave);
            Assert.AreEqual(2, h.PlanCalls.Last().Wave);
            Assert.AreEqual(2, h.Hooks.Restamps.Count, "the fighter is restamped for wave 1 and for wave 2");
        }

        [TestMethod]
        public void Headcount_MembersLeaveInTheBreak_NextWavePlansForThoseWhoFought()
        {
            var h = new Harness();
            var run = h.StartActive();
            Assert.AreEqual(1, h.PlanCalls[0].Headcount, "fixture: wave 1 planned for the one inside");

            // 30 members fought wave 1 (contact), then all but one left during the intermission.
            for (uint g = 0; g < 30; g++)
                run.RecordContact(0x50003000 + g, 1);
            run.RecordContact(0x50003100, 0);   // control: a wave-0 contact is ignored

            h.Advance(4);
            foreach (var c in h.Spawner.Spawned.ToList()) h.Kill(run, c);
            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Intermission, run.State, "fixture");

            h.Inside(MemberGuid);
            h.Advance(21);
            Assert.AreEqual(2, run.CurrentWave, "fixture: wave 2 planned");
            Assert.AreEqual(30, h.PlanCalls.Last().Headcount, "max(1 inside, 30 who fought wave 1)");

            Assert.AreEqual(5, GauntletRun.NextWaveHeadcount(5, 2), "more inside than fought: the occupants");
        }

        [TestMethod]
        public void Headcount_MembersJoinInTheBreak_NextWavePlansForThemToo()
        {
            var h = new Harness();
            var run = h.StartActive();
            h.Inside(0x50003201, 0x50003202);   // StartActive seats one member; two are inside when wave 1 clears
            run.RecordContact(0x50003201, 1);
            run.RecordContact(0x50003202, 1);

            h.Advance(4);
            foreach (var c in h.Spawner.Spawned.ToList()) h.Kill(run, c);
            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Intermission, run.State, "fixture");
            Assert.AreEqual(2, h.PlanCalls.Last().Headcount, "fixture: pre-planned at the break's start for the 2 inside");
            Assert.IsTrue(h.Hooks.RestampClears.Last(), "fixture: the break-start restamp cleared buffs");
            var restamps = h.Hooks.RestampClears.Count;

            h.Inside(0x50003201, 0x50003202, 0x50003203, 0x50003204, 0x50003205, 0x50003206, 0x50003207, 0x50003208);
            h.Advance(21);
            Assert.AreEqual(2, run.CurrentWave, "fixture: wave 2 started");
            Assert.AreEqual(8, h.PlanCalls.Last().Headcount, "the 6 who joined in the break count: re-planned for 8");
            Assert.AreEqual(8, h.Manager.ListRuns().Single().Headcount, "the wave runs with 8");
            CollectionAssert.AreEqual(new[] { false }, h.Hooks.RestampClears.Skip(restamps).ToArray(), "the re-plan's stats arrive without a second buff clear");
        }

        [TestMethod]
        public void Wave_TimesOut_EndsRun_ScoresPartial()
        {
            var h = new Harness { Plans = (w, hc) => TrashPlan(w, 4) };
            var run = h.StartActive();

            h.Advance(5);
            var c = h.Spawner.Spawned;
            Assert.AreEqual(4, c.Count);

            h.Kill(run, c[0]);
            c[1].Dealt = 500;
            c[2].Dealt = 5000;   // overkill on a living creature clamps to its max

            h.Advance(200);
            Assert.AreEqual(GauntletState.Active, run.State, "not yet");
            Assert.IsFalse(run.FightOver, "a live wave is a live fight");

            h.Advance(40);
            Assert.AreEqual(GauntletState.Rewarding, run.State, "the timer ended it; Ending passes straight to Rewarding");
            Assert.IsTrue(run.FightOver, "Rewarding: the fighting is over, so a later death goes to the caches, not the pen");
            Assert.AreEqual(GauntletEndReason.TimeLimit, run.EndReason);

            // (1000 dead + 500 + 1000 clamped + 0) / 4000 = 0.625 -> partial 62
            Assert.AreEqual(62L, run.ScoreCenti);
            Assert.AreEqual(62L, h.FakeRecords.Banks.Last().Score, "banked again at Ending with the partial");
            Assert.AreEqual(62L, h.FakeRecords.Announces.Single().Score);
            Assert.IsTrue(c.Skip(1).All(x => x.Destroyed), "the remaining monsters are cleared");
            CollectionAssert.Contains(h.Hooks.Calls, "rewards.begin");

            // the fight is over: the death pen is emptied to the cache area (the centre) right after the caches are placed
            Assert.AreEqual(1, h.Hooks.Calls.Count(x => x == "death.tocaches"), "held members are released to the caches once");
            Assert.IsTrue(h.Hooks.Calls.IndexOf("death.tocaches") > h.Hooks.Calls.IndexOf("rewards.begin"), "after the caches exist");
            Assert.AreEqual(run.CentrePosition.PositionX, h.Hooks.CacheTargets.Single().PositionX, "to the arena centre, where the caches are");
            Assert.AreEqual(run.Instance, h.Hooks.CacheTargets.Single().Instance, "in the arena copy");

            h.Advance(0.5);
            var wave = h.Store.Waves.Single();
            Assert.AreEqual((byte)1, wave.Outcome, "TimedOut");
            Assert.AreEqual((byte)62, wave.FractionCenti);
            var end = h.Store.LastRunRow;
            Assert.AreEqual((byte)GauntletEndReason.TimeLimit, end.EndReason);
            Assert.AreEqual(62u, end.ScoreCenti);
            Assert.IsNotNull(end.EndedAt);
        }

        [TestMethod]
        public void EmptyArena_RunsUntilTimeout()
        {
            var h = new Harness();
            var run = h.StartActive();

            h.Inside();   // everyone leaves
            h.Advance(235);
            Assert.AreEqual(GauntletState.Active, run.State, "an empty arena never ends a run");
            Assert.IsTrue(run.Hold.HoldsMembers, "the copy is held while the run lives");
            Assert.IsNotNull(run.Hold.UnloadIntervalOverride);

            h.Advance(10);
            Assert.AreEqual(GauntletEndReason.TimeLimit, run.EndReason, "only the timer ends it");
            Assert.AreEqual(GauntletState.Rewarding, run.State);
        }

        [TestMethod]
        public void Stragglers_PulledAfterIdle()
        {
            var h = new Harness { Plans = (w, hc) => TrashPlan(w, 5) };
            var run = h.StartActive();

            h.Advance(5);
            var c = h.Spawner.Spawned;
            Assert.AreEqual(5, c.Count);
            h.Kill(run, c[0]);
            h.Kill(run, c[1]);

            h.Advance(30);
            Assert.IsTrue(c.All(x => x.Pulls == 0), "not idle long enough");

            // One straggler is still fighting: only the idle ones are pulled.
            c[4].Stamp = 8;
            h.Advance(20);
            Assert.AreEqual(1, c[2].Pulls, "idle 45 s: pulled");
            Assert.AreEqual(1, c[3].Pulls);
            Assert.AreEqual(0, c[4].Pulls, "the one still fighting is left where it is");
            Assert.AreEqual(0, c[0].Pulls + c[1].Pulls, "the dead are not pulled");

            h.Advance(47);
            Assert.AreEqual(1, c[4].Pulls, "pulled once it too has idled");
            Assert.AreEqual(2, c[2].Pulls, "at most one pull per straggler_seconds");
            Assert.AreEqual(GauntletState.Active, run.State, "a pull never ends the wave");
            Assert.IsTrue(c[2].Engages > 0, "the wake is retried on the monitor tick");
        }

        [TestMethod]
        public void Stragglers_PulledWhateverTheCount_OnlyOnceTheStreamIsDone()
        {
            var h = new Harness { Plans = (w, hc) => TrashPlan(w, 8) };
            var run = h.StartActive();

            h.Advance(60);
            Assert.AreEqual(8, h.Spawner.Spawned.Count, "fixture");
            Assert.IsTrue(h.Spawner.Spawned.All(x => x.Pulls == 1), "eight alive, above straggler_count 3, all idle: all pulled");

            // Control: while the stream is still issuing, nobody is pulled however idle.
            var slow = new Harness { Plans = (w, hc) => { var p = TrashPlan(w, 2); return new GauntletWavePlan { Wave = p.Wave, Level = p.Level, Plan = p.Plan, TimeLimitSeconds = p.TimeLimitSeconds, UnspawnedHealthEstimate = p.UnspawnedHealthEstimate,
                Spawns = new List<PortalWaveSpawn> { p.Spawns[0], new PortalWaveSpawn(0, 200, p.Spawns[1].Entry) } }; } };
            slow.StartActive();
            slow.Advance(100);
            Assert.AreEqual(1, slow.Spawner.Spawned.Count, "fixture: the second spawn is not due yet");
            Assert.AreEqual(0, slow.Spawner.Spawned[0].Pulls, "the stream is not done: no pull");
        }

        [TestMethod]
        public void Wave_Refusal_NeverCountsAsCleared()
        {
            var h = new Harness { Plans = (w, hc) => TrashPlan(w, 0, refusal: "too few portal points") };
            var run = h.StartActive();

            h.Advance(100);
            Assert.AreEqual(GauntletState.Active, run.State, "an empty, refused wave is not Cleared");
            Assert.AreEqual(0, run.WavesCleared);
            Assert.AreEqual(0, h.FakeRecords.Banks.Count, "nothing banked");
            Assert.IsTrue(h.PlanCalls.Count >= 10, "re-planned while the timer runs");
            Assert.IsTrue(h.PlanCalls.Select(p => p.Seed).Distinct().Count() > 1, "each re-plan varies the seed");
            StringAssert.Contains(h.Manager.ListRuns().Single().LastRefusal, "too few portal points", "admin-visible");

            h.Advance(141);
            Assert.AreEqual(GauntletEndReason.TimeLimit, run.EndReason, "the timer still ends it");
            Assert.AreEqual(0L, run.ScoreCenti);
            Assert.AreEqual(0, run.WavesCleared);
        }

        [TestMethod]
        public void Wave_EmptyPlanWithoutRefusal_NeverCountsAsCleared()
        {
            var h = new Harness { Plans = (w, hc) => TrashPlan(w, 0) };
            var run = h.StartActive();

            h.Advance(30);
            Assert.AreEqual(GauntletState.Active, run.State);
            Assert.AreEqual(0, run.WavesCleared);
        }

        [TestMethod]
        public void Wave_RefusedThenReplanned_StreamsOnTheOriginalTimer()
        {
            var calls = 0;
            var h = new Harness { Plans = (w, hc) => ++calls == 1 ? TrashPlan(w, 0, refusal: "picked none") : TrashPlan(w, 2) };
            var run = h.StartActive();

            h.Advance(8);
            Assert.AreEqual(2, h.Spawner.Spawned.Count, "the re-plan streamed");

            h.Advance(230);
            Assert.AreEqual(GauntletState.Active, run.State, "control: not yet at the limit");
            h.Advance(3);
            Assert.AreEqual(GauntletEndReason.TimeLimit, run.EndReason, "timed from the wave's start, not from the re-plan (that would be 245 s)");
        }

        [TestMethod]
        public void BossWave_ObservedMaxNonBoss_BeforeTheBossSpawns()
        {
            var h = new Harness { Plans = (w, hc) => TrashPlan(w, 2, boss: true) };
            h.StartActive();

            h.Advance(4);
            var observed = h.Spawner.Calls.IndexOf("observed");
            var boss = h.Spawner.Calls.IndexOf("spawn:Boss");
            Assert.IsTrue(observed >= 0 && boss > observed, string.Join(",", h.Spawner.Calls));
            Assert.AreEqual(1, h.Spawner.Calls.Count(x => x == "observed"), "measured once per wave");
            Assert.IsTrue(h.Spawner.ObservedPassed.All(v => v == h.Spawner.ObservedValue), "the measured floor reaches every spawn");
        }

        [TestMethod]
        public void FailedPlacement_CountsAsDoneAndDead()
        {
            var h = new Harness();
            h.Spawner.FailWcids.Add(0x7F1E0001);
            var run = h.StartActive();

            h.Advance(4);
            Assert.AreEqual(2, h.Spawner.Spawned.Count);
            foreach (var c in h.Spawner.Spawned) h.Kill(run, c);

            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Intermission, run.State);
        }

        [TestMethod]
        public void FailedPlacement_AllSpawns_NeverCountsAsCleared()
        {
            var h = new Harness();
            for (uint i = 0; i < 3; i++)
                h.Spawner.FailWcids.Add(0x7F1E0000 + i);
            var run = h.StartActive();

            h.Advance(100);
            Assert.AreEqual(0, h.Spawner.Spawned.Count, "fixture: every placement failed");
            Assert.IsTrue(h.Spawner.Calls.Count(c => c.StartsWith("spawn:")) >= 6, "fixture: the plan was retried and failed again");
            Assert.AreEqual(GauntletState.Active, run.State, "a wave with no creature in the world is not Cleared");
            Assert.AreEqual(0, run.WavesCleared);
            Assert.AreEqual(0, h.FakeRecords.Banks.Count, "nothing banked");
            Assert.IsTrue(h.PlanCalls.Count > 1, "re-planned while the timer runs");
            StringAssert.Contains(h.Manager.ListRuns().Single().LastRefusal, "could be placed", "admin-visible");

            h.Advance(145);
            Assert.AreEqual(GauntletEndReason.TimeLimit, run.EndReason, "only the timer ends it");
            Assert.AreEqual(0, run.WavesCleared);
            Assert.AreEqual(0L, run.ScoreCenti);
            Assert.IsTrue(h.FakeRecords.Banks.All(b => b.Score == 0), "no record above zero is banked");
        }

        [TestMethod]
        public void EnqueueRefused_AllBatches_NeverCountsAsCleared()
        {
            var h = new Harness();
            var run = h.StartActive();
            h.World.EnqueueOk = false;

            h.Advance(30);
            Assert.AreEqual(0, h.Spawner.Spawned.Count, "fixture: no batch reached the landblock");
            Assert.AreEqual(GauntletState.Active, run.State, "a wave whose batches were never queued is not Cleared");
            Assert.AreEqual(0, run.WavesCleared);
            Assert.AreEqual(0, h.FakeRecords.Banks.Count);
        }

        // ---- instance loss, restart, gates ------------------------------------------------------------------------------------

        [TestMethod]
        public void InstanceLost_EndsWithClearedOnly()
        {
            var h = new Harness();
            var run = h.StartActive();

            h.Advance(4);
            foreach (var c in h.Spawner.Spawned.ToList()) h.Kill(run, c);
            h.Advance(21);
            Assert.AreEqual(2, run.CurrentWave);

            h.Advance(4);
            h.Spawner.Spawned.Last().Dealt = 900;
            h.Kill(run, h.Spawner.Spawned[3]);
            h.Advance(2);

            h.World.Loaded = false;
            h.Advance(0.5);

            Assert.AreEqual(GauntletState.Closed, run.State);
            Assert.AreEqual(GauntletEndReason.InstanceLost, run.EndReason);
            Assert.AreEqual(100L, run.ScoreCenti, "the cleared wave only; no partial");
            Assert.AreEqual(100L, h.FakeRecords.Banks.Last().Score);
            Assert.AreEqual(1, h.Hooks.Calls.Count(x => x == "rewards.direct"), "eligible online members are paid to their packs");
            Assert.IsFalse(h.Hooks.Calls.Contains("rewards.begin"), "no caches: there is no arena");
            Assert.IsFalse(h.Hooks.Calls.Contains("death.tocaches"), "no arena to bring anyone to");
            Assert.AreEqual((byte)GauntletEndReason.InstanceLost, h.Store.LastRunRow.EndReason);
            Assert.AreEqual(100u, h.Store.LastRunRow.ScoreCenti);
            Assert.AreEqual(0, h.Manager.RunCount);
        }

        [TestMethod]
        public void Startup_ClosesOpenRunsAsRestart()
        {
            var h = new Harness();
            h.Manager.Start();

            CollectionAssert.AreEqual(new[] { T0 }, h.Store.Closes);
            h.Start();
            CollectionAssert.AreEqual(new[] { "close", "insert" }, h.Store.Calls, "the close runs before any run of this process exists");
            Assert.AreEqual((byte)2, (byte)GauntletEndReason.Restart, "the Restart code the DAO writes");
        }

        [TestMethod]
        public void CapReached_StartRefused()
        {
            var h = new Harness { Tuning = GauntletTuning.Defaults with { MaxConcurrent = 2 } };
            h.Start(1);
            h.Start(2);

            Assert.AreEqual(GauntletManager.CapLine, h.Manager.CheckCanStart(3, h.Now));
            Assert.IsFalse(h.Manager.TryStart(h.Request(3), out var refusal));
            Assert.AreEqual(GauntletManager.CapLine, refusal);
            Assert.AreEqual(2, h.Store.Inserted.Count, "no row for a refused start");
            Assert.AreEqual(GauntletManager.AlreadyRunningLine, h.Manager.CheckCanStart(1, h.Now), "one run per Avatar");

            // Both abort on an empty staging; the cap frees.
            h.Advance(122);
            Assert.IsNull(h.Manager.CheckCanStart(3, h.Now));
        }

        [TestMethod]
        public void NoArena_StartRefused()
        {
            var h = new Harness { Tuning = GauntletTuning.Defaults with { Sites = "not_a_site" } };
            Assert.AreEqual(GauntletManager.NoArenaLine, h.Manager.CheckCanStart(AvatarId, h.Now));
        }

        [TestMethod]
        public void TryStart_NeverThrows_UndoesItsRegistration()
        {
            var h = new Harness();
            var broken = new GauntletManager(new GauntletEnvironment
            {
                UtcNow = () => h.Now,
                Tuning = () => GauntletTuning.Defaults,
                Sites = () => new[] { h.Site },
                World = h.World,
                Store = new ThrowingStore(),
                Records = new FakeRecords(),
                Favor = h.Favor,
            });

            Assert.IsFalse(broken.TryStart(h.Request(), out var refusal), "a fault is a false, never a throw");
            Assert.IsNotNull(refusal);
            Assert.AreEqual(0, broken.RunCount, "its own registration is undone");
            Assert.AreEqual(0, h.Favor.Releases.Count, "the caller releases on false, so the manager does not");
        }

        private sealed class ThrowingStore : IGauntletStore
        {
            public void InsertRun(AllegianceGauntletRun row, Action<bool, uint> callback) => throw new InvalidOperationException("db down");
            public void SaveRunEnd(AllegianceGauntletRun row) { }
            public void SaveWave(AllegianceGauntletWave row) { }
            public bool CloseOpenRuns(DateTime nowUtc, out int closed) { closed = 0; return false; }
        }

        [TestMethod]
        public void Rewarding_ClosesOnClaimWindow_WithNoOpHooks()
        {
            var h = new Harness { UseNoOpHooks = true };
            var run = h.StartActive();

            h.Advance(243);
            Assert.AreEqual(GauntletState.Rewarding, run.State);
            Assert.IsTrue(GauntletArenaHold.AdmitsIn(run.State), "eligible members may still enter to claim");

            h.Advance(NoGauntletHooks.DefaultClaimWindowSeconds - 5);
            Assert.AreEqual(GauntletState.Rewarding, run.State);

            h.Advance(6);
            Assert.AreEqual(GauntletState.Closed, run.State, "the claim window closes it with no reward hook");
            Assert.AreEqual(1, h.World.Evictions.Count);
            Assert.AreEqual(0, h.Manager.RunCount);
        }

        [TestMethod]
        public void Admin_AbortFromActive_EndsWithPartialAndReasonAdmin()
        {
            var h = new Harness();
            var run = h.StartActive();
            h.Advance(4);
            h.Spawner.Spawned[0].Dealt = 1500;
            h.Advance(1.5);

            var line = GauntletManager_Abort(h, refund: true);
            StringAssert.Contains(line, "ended by admin");
            Assert.AreEqual(GauntletEndReason.Admin, run.EndReason);
            Assert.AreEqual(33L, run.ScoreCenti, "1000 of 3000: the partial counts, like a timer expiry");
            CollectionAssert.AreEqual(new[] { (AvatarId, Price) }, h.Favor.Refunds, "refund returns the committed price");
        }

        [TestMethod]
        public void Admin_AbortWithRefundTwice_RefundsOnce()
        {
            var h = new Harness();
            var run = h.StartActive();
            h.Advance(4);

            StringAssert.Contains(GauntletManager_Abort(h, refund: true), "Refund of");
            Assert.AreEqual(GauntletState.Rewarding, run.State, "fixture: the first abort leaves the run in Rewarding, where it can still be found");
            Assert.AreSame(run, h.Manager.FindByAvatar(AvatarId), "fixture: the second abort reaches the same run");

            var second = GauntletManager_Abort(h, refund: true);
            CollectionAssert.AreEqual(new[] { (AvatarId, Price) }, h.Favor.Refunds, "exactly one refund across both aborts");
            Assert.AreEqual(GauntletState.Closed, run.State, "the second abort still closes the run");
            StringAssert.Contains(second, "already refunded");
        }

        [TestMethod]
        public void Admin_AbortInStaging_Releases()
        {
            var h = new Harness();
            var run = h.Start();
            h.Advance(1);

            GauntletManager_Abort(h, refund: false);
            Assert.AreEqual(GauntletState.Aborted, run.State);
            Assert.AreEqual(GauntletEndReason.Admin, run.EndReason);
            CollectionAssert.AreEqual(new[] { (AvatarId, Price) }, h.Favor.Releases);
        }

        private static string GauntletManager_Abort(Harness h, bool refund) => h.Manager.AbortRun(AvatarId, refund);

        private sealed class OpenClaimStore : IGauntletClaimStore
        {
            public void Insert(uint accountId, DateTime utcDay, uint runId, uint characterId, string token, Action<GauntletClaimInsertResult> callback)
                => callback(GauntletClaimInsertResult.Inserted);

            public void Delete(uint accountId, DateTime utcDay, uint runId, uint characterId, Action<bool, int> callback) => callback(true, 1);
        }

        private static uint nextCrateGuid = 0x7ED80000;

        [TestMethod]
        public void CratesClaimed_TwoPaidClaims_WrittenToTheRunRow()
        {
            var h = new Harness { UseNoOpHooks = true };
            var run = h.StartActive();

            var a = SalvageRollerVendorTests.MakePlayer(0x50000201);
            var b = SalvageRollerVendorTests.MakePlayer(0x50000202);
            run.RecordContact(a.Guid.Full, 1);
            run.RecordContact(b.Guid.Full, 1);

            h.Advance(243);
            Assert.AreEqual(GauntletState.Rewarding, run.State, "fixture");
            Assert.AreEqual((ushort)0, h.Store.LastRunRow.CratesClaimed, "control: nothing claimed yet");

            try
            {
                GauntletClaims.Store = new OpenClaimStore();
                GauntletClaims.UtcNow = () => h.Now;
                GauntletClaims.RunOnWorld = work => work();
                GauntletClaims.Tell = (p, t) => { };
                GauntletClaims.Give = (cache, p, crate) => true;
                GauntletClaims.IsGone = p => false;   // the seeded players have no session
                GauntletClaims.CreateCrate = wcid => new GenericObject(new ACE.Entity.Models.Weenie
                {
                    WeenieClassId = wcid,
                    WeenieType = ACE.Entity.Enum.WeenieType.Generic,
                    PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string> { { ACE.Entity.Enum.Properties.PropertyString.Name, "Gauntlet Crate" } },
                }, new ACE.Entity.ObjectGuid(System.Threading.Interlocked.Increment(ref nextCrateGuid)));

                // The real claim pipeline over the real adapter: what the run row receives is what the claims paid.
                var w = new GauntletClaimWindow(new GauntletRunClaimAdapter(run), "gauntlet_t0", 1.0, GauntletRewards.CrateWcid, h.Now.AddMinutes(5));
                var results = new List<GauntletClaimResult>();
                GauntletClaims.Claim(w, null, a, 9001, "10.0.0.1", "a", results.Add);
                GauntletClaims.Claim(w, null, b, 9002, "10.0.0.2", "b", results.Add);
                CollectionAssert.AreEqual(new[] { GauntletClaimResult.Paid, GauntletClaimResult.Paid }, results, "fixture: both claims paid");

                h.Advance(0.5);
                Assert.AreEqual((ushort)2, h.Store.LastRunRow.CratesClaimed, "two paid claims are written as crates_Claimed 2");
                Assert.AreEqual(2, run.CratesClaimed);

                // A claim that answers after the run closed still reaches the row.
                h.Advance(NoGauntletHooks.DefaultClaimWindowSeconds);
                Assert.AreEqual(GauntletState.Closed, run.State, "fixture");
                run.NoteCrateClaimed();
                Assert.AreEqual((ushort)3, h.Store.LastRunRow.CratesClaimed, "a late claim on a closed run writes the row itself");
            }
            finally
            {
                GauntletClaims.ResetSeams();
            }
        }

        [TestMethod]
        public void Tuning_ChangedMidRun_NextWavePlansWithTheRunSnapshot()
        {
            const string StepKey = "allegiance_gauntlet_level_step";
            var levels = new List<int>();

            ThreadDungeonSpawner.LivePlanBuildStats noStats = default;
            GauntletWavePlanner.PlanBuild build = (DungeonGemSpec spec, DungeonEntryDef site, GroupScaling group,
                Func<DungeonPopulationLimits, DungeonPopulationLimits> adjust, out ThreadDungeonSpawner.LivePlanBuildStats stats) =>
            {
                levels.Add(spec.Level);
                stats = noStats;
                var plan = new DungeonSpawnPlan { FamilyId = "fake", HealthCurveTarget = 1000, HealthMultiplier = 1, BossHealthMultiplier = 1 };
                plan.Entries.Add(new DungeonSpawnPlanEntry(0x7F1E0100, DungeonRole.Trash, null));
                return plan;
            };

            try
            {
                // Every key FromPropertyManager reads is seeded from the compiled defaults; level_step is pinned to 25.
                DefaultPropertyManager.LoadDefaultProperties();
                PropertyManager.ModifyLong(StepKey, 25);

                var h = new Harness
                {
                    TuningSource = GauntletTuning.FromPropertyManager,
                    PlannerOverride = LiveGauntletEnvironment.PlannerFor(build, hc => GroupScaling.Solo),
                };
                var run = h.StartActive();

                // The step changes after the run started.
                PropertyManager.ModifyLong(StepKey, 100);
                Assert.AreEqual(100L, GauntletTuning.FromPropertyManager().LevelStep, "control: the live value moved");

                h.Advance(60);
                Assert.IsTrue(h.Spawner.Spawned.Count > 0, "fixture: wave 1 streamed");
                foreach (var c in h.Spawner.Spawned.ToList()) h.Kill(run, c);
                h.Advance(0.5);
                Assert.AreEqual(GauntletState.Intermission, run.State, "fixture: wave 1 cleared");

                h.Advance(21);
                Assert.AreEqual(2, run.CurrentWave, "fixture: wave 2 planned");

                CollectionAssert.AreEqual(new[] { 200, 225 }, levels, "wave 2 is planned at the run's own step (25), not the live one (100 would give 300)");
                Assert.AreEqual(225, run.Info(h.Now).Level);
            }
            finally
            {
                DefaultPropertyManager.LoadDefaultProperties();
            }
        }

        [TestMethod]
        public void GateBind_RunWentTerminal_GateTakenBackOut()
        {
            var h = new Harness();
            var live = h.Start();
            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Staging, live.State, "fixture");

            var ended = new Harness().Start();
            ended.Hold.State = GauntletState.Aborted;   // the run closed while the gate was being placed

            var destroyed = new List<WorldObject>();
            var hooks = new LiveGauntletGateHooks { DestroyGate = destroyed.Add };

            try
            {
                var lateGate = new GenericObject(new ACE.Entity.Models.Weenie { WeenieClassId = LiveGauntletGateHooks.GateWcid, WeenieType = ACE.Entity.Enum.WeenieType.Generic },
                    new ACE.Entity.ObjectGuid(System.Threading.Interlocked.Increment(ref nextCrateGuid)));

                Assert.IsFalse(hooks.TryBind(ended, lateGate), "a gate for a terminal run is not kept");
                Assert.IsFalse(hooks.HoldsGate(ended.RunKey), "no entry is left for Remove to miss");
                Assert.IsFalse(GauntletGate.TryGetTarget(lateGate.Guid.Full, out _), "the gate is unbound");
                CollectionAssert.AreEqual(new WorldObject[] { lateGate }, destroyed, "the late gate is destroyed");

                var gate = new GenericObject(new ACE.Entity.Models.Weenie { WeenieClassId = LiveGauntletGateHooks.GateWcid, WeenieType = ACE.Entity.Enum.WeenieType.Generic },
                    new ACE.Entity.ObjectGuid(System.Threading.Interlocked.Increment(ref nextCrateGuid)));

                Assert.IsTrue(hooks.TryBind(live, gate), "control: a live run keeps its gate");
                Assert.IsTrue(hooks.HoldsGate(live.RunKey));
                Assert.IsTrue(GauntletGate.TryGetTarget(gate.Guid.Full, out var target) && ReferenceEquals(target, live));
            }
            finally
            {
                GauntletGate.ResetSeams();
            }
        }

        [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "<IsDestroyed>k__BackingField")]
        private static extern ref bool IsDestroyedField(WorldObject wo);

        private static uint nextGateGuid = 0x8F5D0000;   // dynamic: the range the landblock heartbeat rots

        private sealed class GateWorld
        {
            public readonly List<Action> Queue = new List<Action>();
            public readonly List<WorldObject> Placed = new List<WorldObject>();
            public readonly List<WorldObject> Destroyed = new List<WorldObject>();

            public LiveGauntletGateHooks Hooks() => new LiveGauntletGateHooks
            {
                EnqueueAt = (at, action) => { Queue.Add(action); return true; },
                CreateGate = () => new GenericObject(new ACE.Entity.Models.Weenie { WeenieClassId = LiveGauntletGateHooks.GateWcid, WeenieType = ACE.Entity.Enum.WeenieType.Generic },
                    new ACE.Entity.ObjectGuid(System.Threading.Interlocked.Increment(ref nextGateGuid))),
                PlaceGate = (gate, npc) => { Placed.Add(gate); return true; },
                DestroyGate = Destroyed.Add,
            };

            public void Drain() { var q = Queue.ToList(); Queue.Clear(); foreach (var a in q) a(); }
        }

        private static readonly Position Npc = new Position(0xA9B40019u, 84f, 96f, 20f, 0f, 0f, 0f, 1f, 0);

        [TestMethod]
        public void Gate_Placed_NeverRots_AndIsReplacedWhenGone()
        {
            var h = new Harness();
            var run = h.Start();
            h.Advance(0.5);
            var world = new GateWorld();
            var gates = world.Hooks();

            try
            {
                gates.Place(run, Npc);
                gates.Place(run, Npc);
                world.Drain();
                Assert.AreEqual(1, world.Placed.Count, "a second placement while one is pending is dropped");
                var gate = world.Placed.Single();

                Assert.IsTrue(gate.Guid.IsDynamic(), "fixture: the rotting guid range");
                Assert.IsFalse(gate.IsDecayable(), "a placed gate is never decayable");
                Assert.IsTrue(GauntletGate.TryGetTarget(gate.Guid.Full, out _), "bound");

                gates.Ensure(run, Npc);
                world.Drain();
                Assert.AreEqual(1, world.Placed.Count, "a standing gate is left alone");

                IsDestroyedField(gate) = true;   // its landblock unloaded, or it rotted
                gates.Ensure(run, Npc);
                gates.Ensure(run, Npc);
                world.Drain();
                Assert.AreEqual(2, world.Placed.Count, "a gone gate is placed again, once");
                Assert.IsFalse(GauntletGate.TryGetTarget(gate.Guid.Full, out _), "the old gate is unbound");
                Assert.IsTrue(GauntletGate.TryGetTarget(world.Placed[1].Guid.Full, out _), "the new gate is bound");

                IsDestroyedField(world.Placed[1]) = true;
                run.Hold.State = GauntletState.Closed;
                gates.Ensure(run, Npc);
                world.Drain();
                Assert.AreEqual(2, world.Placed.Count, "nothing is placed once the run no longer admits");
            }
            finally
            {
                GauntletGate.ResetSeams();
            }
        }

        private sealed class RingWorld
        {
            public readonly List<(ushort Landblock, Action Action)> Queue = new List<(ushort, Action)>();
            public readonly List<(WorldObject Wo, RingPoint Point, uint Instance)> Placed = new List<(WorldObject, RingPoint, uint)>();
            public readonly List<(WorldObject Wo, ushort? OnQueueOf)> Destroyed = new List<(WorldObject, ushort?)>();
            public readonly HashSet<ushort> Unloaded = new HashSet<ushort>();
            public ushort? Running;

            public LiveGauntletRingHooks Hooks() => new LiveGauntletRingHooks
            {
                SpacingM = () => 10,
                EnqueueOn = (lb, instance, action) => { if (Unloaded.Contains(lb)) return false; Queue.Add((lb, action)); return true; },
                CreateWisp = () => new GenericObject(new ACE.Entity.Models.Weenie { WeenieClassId = OutdoorWispRing.WispWcid, WeenieType = ACE.Entity.Enum.WeenieType.Generic },
                    new ACE.Entity.ObjectGuid(System.Threading.Interlocked.Increment(ref nextGateGuid))),
                PlaceWisp = (wo, point, instance) => { Placed.Add((wo, point, instance)); return true; },
                DestroyWisp = wo => Destroyed.Add((wo, Running)),
            };

            public void Drain()
            {
                var q = Queue.ToList();
                Queue.Clear();
                foreach (var (lb, a) in q) { Running = lb; a(); Running = null; }
            }
        }

        [TestMethod]
        public void Ring_MarksTheFightCircle_RemovedAtClose()
        {
            var h = new Harness();
            var run = h.Start();
            h.Advance(0.5);

            var world = new RingWorld();
            var ring = world.Hooks();

            ring.Place(run);
            world.Drain();

            Assert.AreEqual(38, world.Placed.Count, "ceil(2 pi 60 / 10) markers");
            foreach (var (wo, point, instance) in world.Placed)
            {
                var d = Math.Sqrt(Math.Pow(point.Gx - run.CentreGx, 2) + Math.Pow(point.Gy - run.CentreGy, 2));
                Assert.AreEqual(run.Radius, d, 1e-6, "each marker stands on the fight circle");
                Assert.AreEqual(run.Instance, instance, "in the arena copy");
                Assert.IsFalse(wo.IsDecayable(), "a marker never rots");
            }
            Assert.AreEqual(38, ring.MarkerCount(run.RunKey));

            // Teardown: queued per landblock, never run on the caller's (world) thread.
            var byLandblock = world.Placed.GroupBy(p => p.Point.Landblock).ToDictionary(g => g.Key, g => g.Select(p => p.Wo).ToList());
            ring.Remove(run);
            Assert.AreEqual(0, world.Destroyed.Count, "nothing destroyed on the world thread");
            Assert.AreEqual(byLandblock.Count, world.Queue.Count, "one teardown per landblock");
            world.Drain();
            Assert.AreEqual(38, world.Destroyed.Count, "every marker is taken out");
            foreach (var (wo, on) in world.Destroyed)
                Assert.IsTrue(on.HasValue && byLandblock[on.Value].Contains(wo), "each on its own landblock's queue");
            Assert.AreEqual(0, ring.MarkerCount(run.RunKey));

            // A placement still queued when the run closes places nothing.
            ring.Place(run);
            run.Hold.State = GauntletState.Closed;
            world.Drain();
            Assert.AreEqual(38, world.Placed.Count, "nothing placed for a closed run");
        }

        [TestMethod]
        public void Ring_LateMarker_DestroysItself_AndAnUnloadedLandblockIsTornDownDirectly()
        {
            var h = new Harness();
            var run = h.Start();
            h.Advance(0.5);

            var world = new RingWorld();
            LiveGauntletRingHooks ring = null;
            var removedDuring = false;
            ring = new LiveGauntletRingHooks
            {
                SpacingM = () => 10,
                EnqueueOn = (lb, instance, action) => { world.Queue.Add((lb, action)); return true; },
                CreateWisp = () => new GenericObject(new ACE.Entity.Models.Weenie { WeenieClassId = OutdoorWispRing.WispWcid, WeenieType = ACE.Entity.Enum.WeenieType.Generic },
                    new ACE.Entity.ObjectGuid(System.Threading.Interlocked.Increment(ref nextGateGuid))),
                // The run closes while the first marker is entering the world (Remove on the world thread, mid-placement).
                PlaceWisp = (wo, point, instance) =>
                {
                    world.Placed.Add((wo, point, instance));
                    if (!removedDuring) { removedDuring = true; ring.Remove(run); }
                    return true;
                },
                DestroyWisp = wo => world.Destroyed.Add((wo, world.Running)),
            };

            ring.Place(run);
            world.Drain();
            Assert.AreEqual(1, world.Placed.Count, "nothing more is placed once the ring is removed");
            Assert.AreEqual(1, world.Destroyed.Count, "the marker that entered after the Remove took itself out");
            Assert.AreSame(world.Placed[0].Wo, world.Destroyed[0].Wo);
            Assert.IsTrue(world.Destroyed[0].OnQueueOf.HasValue, "on its landblock's thread");
            Assert.AreEqual(0, ring.MarkerCount(run.RunKey));
            world.Placed.Clear();
            world.Destroyed.Clear();

            // A landblock no longer loaded at teardown: its markers are destroyed directly.
            var second = world.Hooks();
            second.Place(run);
            world.Drain();
            var gone = world.Placed.First().Point.Landblock;
            world.Unloaded.Add(gone);
            second.Remove(run);
            var direct = world.Destroyed.Where(d => d.OnQueueOf == null).Select(d => d.Wo).ToList();
            CollectionAssert.AreEquivalent(world.Placed.Where(p => p.Point.Landblock == gone).Select(p => p.Wo).ToList(), direct, "the unloaded landblock's markers, directly");
            world.Drain();
            Assert.AreEqual(world.Placed.Count, world.Destroyed.Count, "and the rest on their queues");
        }

        [TestMethod]
        public void RefusedStrike_GauntletCreature_GetsTheArenaLine_ThreadKeepsItsOwn()
        {
            Assert.AreEqual("You must stand inside the arena circle to strike.", GauntletSpawner.RefusedStrikeLine);
            Assert.AreEqual(OutdoorCreaturePerimeter.RefusedStrikeText, new OutdoorCreaturePerimeter(0, 0, 30, false, null).RefusedStrikeLine,
                "a Thread perimeter keeps the Thread line");

            var gauntletSpawner = PooledLootSourceText.Read("Source/ACE.Server/AllegianceAvatar/Gauntlet/GauntletSpawner.cs");
            StringAssert.Contains(gauntletSpawner, "perimeterSettings ?? OutdoorPerimeterSettings.FromTunables(), RefusedStrikeLine);",
                "the Gauntlet binding carries the arena line");

            var player = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_OutdoorPerimeter.cs");
            var creature = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_OutdoorPerimeter.cs");
            StringAssert.Contains(player, "NotifyOutdoorStrikeRefused(perimeter.RefusedStrikeLine);", "the attack gate sends the binding's line");
            StringAssert.Contains(creature, "player.NotifyOutdoorStrikeRefused(perimeter.RefusedStrikeLine);", "the damage intake sends the binding's line");
            StringAssert.Contains(player, "new GameMessageSystemChat(line ?? OutdoorCreaturePerimeter.RefusedStrikeText, ChatMessageType.Broadcast)");
        }

        [TestMethod]
        public void Gate_EnsureAskedWhileAdmitting_NotAfterClose()
        {
            var h = new Harness { UseNoOpHooks = false };
            var run = h.Start();

            h.Advance(12);
            Assert.IsTrue(h.Hooks.Calls.Count(c => c == "gate.ensure") >= 2, "asked every few seconds in Staging");

            h.Advance(120);   // nobody entered: aborted
            Assert.IsTrue(run.IsTerminal, "fixture");
            var asked = h.Hooks.Calls.Count(c => c == "gate.ensure");
            h.Advance(20);
            Assert.AreEqual(asked, h.Hooks.Calls.Count(c => c == "gate.ensure"), "a closed run never asks");
        }

        [TestMethod]
        public void Eligibility_HitOnly_NotEligible_DamageDealerAndHealer_Eligible()
        {
            var h = new Harness();
            var run = h.StartActive();

            const uint Afk = 0x50004001, Fighter = 0x50004002, Healer = 0x50004003;

            run.RecordContact(Afk, 1, active: false);       // a creature hit them, they did nothing
            run.RecordContact(Fighter, 1, active: true);    // they (or their pet) dealt damage

            // The healer acts through the real support hook, standing inside the circle.
            var healer = PlayerAt(Healer, run.Instance, 100, 96);
            var patient = PlayerAt(0x50004004, run.Instance, 101, 96);
            var outsider = PlayerAt(0x50004005, run.Instance, 100, 96 + 61);   // past the 60 m circle

            try
            {
                GauntletHooks.RunForInstance = i => i == run.Instance ? run : null;
                GauntletHooks.OnBeneficialAct(healer, patient, beneficial: true);
                GauntletHooks.OnBeneficialAct(outsider, patient, beneficial: true);
                GauntletHooks.OnBeneficialAct(patient, healer, beneficial: false);   // control: a harmful spell
                GauntletHooks.OnBeneficialAct(patient, patient, beneficial: true);   // control: a self-buff
            }
            finally
            {
                GauntletHooks.RunForInstance = i => GauntletManager.ForInstance(i);
            }

            Assert.IsTrue(run.Fought.Contains(Afk), "being hit still counts as having fought");
            Assert.IsFalse(run.IsEligible(Afk), "hit-only is not eligible");
            Assert.IsTrue(run.IsEligible(Fighter), "a damage dealer is eligible");
            Assert.IsTrue(run.IsEligible(Healer), "a healer inside the circle is eligible");
            Assert.IsFalse(run.IsEligible(outsider.Guid.Full), "a heal from outside the circle does not count");
            Assert.IsFalse(run.Fought.Contains(patient.Guid.Full), "neither a harmful spell nor a self-buff counts");
            CollectionAssert.AreEquivalent(new[] { Fighter, Healer }, run.EligibleCharacters().ToArray());
        }

        [TestMethod]
        public void StartLines_SayWhereTheAvatarStands_AndRepeatAtWave1()
        {
            var h = new Harness { NpcAt = new Position(0xA9B40019u, 84f, 96f, 20f, 0f, 0f, 0f, 1f, 0) };
            var run = h.StartActive();
            const string where = "landblock 0xA9B4 (0xA9B4001D [84.0 96.0 20.0])";   // the Position recomputes the outdoor cell from x, y

            Assert.AreEqual(GauntletState.Active, run.State, "fixture");
            CollectionAssert.AreEqual(new[]
            {
                (Monarch, $"[Allegiance Gauntlet] Your allegiance's Gauntlet opens in {h.Tuning.StagingSeconds} seconds. Use the Gauntlet Gate beside the Avatar at {where} to enter."),
                (Monarch, $"[Allegiance Gauntlet] Wave 1 has begun. Join through the Gauntlet Gate beside the Avatar at {where}."),
            }, h.World.AllegianceLines.ToArray(), "the start line names the place, and is sent again when wave 1 begins");
            Assert.IsTrue(h.World.AllegianceLines.All(l => l.Item2.Length < 255));

            var info = run.Info(h.Now);
            Assert.AreEqual(where, info.NpcAt);
            CollectionAssert.Contains(GauntletStatusText.Lines(true, info, 0, null, 0).ToArray(), $"The Gauntlet Gate stands beside the Avatar at {where}.", "the status names it");
            Assert.IsNull(GauntletRun.NpcLocationText(null));
        }

        [TestMethod]
        public void Rewarding_TellsTheArenaTheAllegianceAndEachFighter()
        {
            var h = new Harness { Plans = (w, hc) => TrashPlan(w, 2) };
            h.Hooks.Window = 300;
            var run = h.StartActive();

            const uint Earned = 0x50006001, Short = 0x50006002, Idle = 0x50006003;
            run.RecordContact(Earned, 1, active: true);
            run.RecordContact(Short, 1, active: true);
            run.RecordContact(Idle, 1, active: false);

            h.Advance(5);
            foreach (var c in h.Spawner.Spawned.ToList())
                h.Kill(run, c);
            h.Advance(1);
            Assert.AreEqual(GauntletState.Intermission, run.State, "fixture: wave 1 cleared");

            h.Advance(h.Tuning.IntermissionSeconds + 1);
            Assert.AreEqual(2, run.CurrentWave, "fixture: wave 2 started");
            run.RecordContact(Earned, 2, active: true);

            h.Advance(245);
            Assert.AreEqual(GauntletState.Rewarding, run.State, "fixture: the timer ended wave 2");

            const string caches = "Gauntlet Caches have appeared at the arena centre. Members who fought or helped in 2 waves may claim one crate within 5 minutes.";
            CollectionAssert.Contains(h.World.Lines, caches, "told inside");
            CollectionAssert.Contains(h.World.AllegianceLines, (Monarch, caches), "told to the allegiance");

            CollectionAssert.AreEquivalent(new[]
            {
                (Earned, "You have earned a Gauntlet crate."),
                (Short, "You fought or helped others in 1 of the 2 waves needed for a crate."),
                (Idle, "You fought or helped others in 0 of the 2 waves needed for a crate."),
            }, h.World.CharacterLines.ToArray(), "each fought member hears where they stand");
            Assert.AreEqual((1, 2), run.WaveCredit(Short));
        }

        [TestMethod]
        public void Rewarding_CachesLine_ReachesEachMemberOnce()
        {
            var h = new Harness { Plans = (w, hc) => TrashPlan(w, 2) };
            const uint InsideAndOnline = 0x50008001, InsideLookupFailed = 0x50008002, OutsideOnline = 0x50008003;
            h.World.AllegianceReach = new HashSet<uint> { InsideAndOnline, OutsideOnline };   // the lookup missed 0x50008002
            var run = h.StartActive();
            h.Inside(InsideAndOnline, InsideLookupFailed);

            h.Advance(245);
            Assert.AreEqual(GauntletState.Rewarding, run.State, "fixture");

            var caches = h.World.Lines.Single(l => l.StartsWith("Gauntlet Caches"));
            Assert.AreEqual(1, h.World.AllegianceLines.Count(l => l.Item2 == caches), "sent to the allegiance once");

            var inArena = h.World.InsideLinesByMember.Where(l => l.Line == caches).Select(l => l.Guid).ToArray();
            CollectionAssert.AreEqual(new[] { InsideLookupFailed }, inArena,
                "inside, only the member the allegiance send did not reach is told: nobody hears it twice, nobody misses it");
        }

        [TestMethod]
        public void RewardLines_CountsAndPlurals_UnderTheChatLimit()
        {
            Assert.AreEqual("Gauntlet Caches have appeared at the arena centre. Members who fought or helped in 3 waves may claim one crate within 1 minute.",
                GauntletRun.CachesLine(3, 45), "a part minute rounds up");
            Assert.AreEqual("You fought or helped others in 2 of the 3 waves needed for a crate.", GauntletRun.ShortLine(2, 3));
            Assert.AreEqual(GauntletRun.ShortLine(2, 3), GauntletClaims.NotEligibleLine(2, 3), "the claim refusal is the same line");
            Assert.AreEqual("You fought or helped others in 0 of the 1 wave needed for a crate.", GauntletRun.ShortLine(0, 1), "singular for one wave");
            Assert.IsTrue(GauntletRun.CachesLine(int.MaxValue, int.MaxValue).Length < 255);
            Assert.IsTrue(GauntletRun.ShortLine(int.MaxValue, int.MaxValue).Length < 255);
        }

        [TestMethod]
        public void Support_InTheBreak_EarnsNoCredit()
        {
            var h = new Harness();
            var run = h.StartActive();

            h.Advance(4);
            foreach (var c in h.Spawner.Spawned.ToList()) h.Kill(run, c);
            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Intermission, run.State, "fixture");

            var healer = PlayerAt(0x50007001, run.Instance, 100, 96);
            var patient = PlayerAt(0x50007002, run.Instance, 101, 96);

            try
            {
                GauntletHooks.RunForInstance = i => i == run.Instance ? run : null;

                GauntletHooks.OnBeneficialAct(healer, patient, beneficial: true);
                Assert.AreEqual(0, run.ActiveWaves(healer.Guid.Full).Count, "a heal in the break credits no wave");
                Assert.IsFalse(run.Fought.Contains(healer.Guid.Full));

                h.Advance(21);
                Assert.AreEqual(GauntletState.Active, run.State, "fixture: wave 2 is live");
                GauntletHooks.OnBeneficialAct(healer, patient, beneficial: true);
                CollectionAssert.AreEqual(new[] { 2 }, run.ActiveWaves(healer.Guid.Full).ToArray(), "control: the same heal in a live wave credits it");
            }
            finally
            {
                GauntletHooks.RunForInstance = i => GauntletManager.ForInstance(i);
            }
        }

        [TestMethod]
        public void HeldMember_NoContact_NoSupportIntoTheCircle()
        {
            var h = new Harness();
            var run = h.StartActive();

            var held = PlayerAt(0x50005001, run.Instance, 100, 96 + 62);     // at the edge, just past the circle
            var patient = PlayerAt(0x50005002, run.Instance, 100, 96);       // inside the circle
            var penned = PlayerAt(0x50005003, run.Instance, 100, 96 + 170);   // in the pen
            var free = PlayerAt(0x50005004, run.Instance, 100, 96 + 10);
            h.Hooks.Held.Add(held.Guid.Full);

            run.OnMemberContact(held, 1, active: true);
            Assert.IsFalse(run.Fought.Contains(held.Guid.Full), "a held member's hit is not contact");
            run.OnMemberContact(free, 1, active: true);
            Assert.IsTrue(run.IsEligible(free.Guid.Full), "control: a member who is not held is credited");

            try
            {
                GauntletHooks.RunForInstance = i => i == run.Instance ? run : null;

                Assert.IsTrue(GauntletHooks.RefusesHeldSupport(held, patient, beneficial: true), "a held member may not heal into the circle");
                Assert.IsFalse(GauntletHooks.RefusesHeldSupport(held, penned, beneficial: true), "a held member may heal someone in the pen");
                Assert.IsFalse(GauntletHooks.RefusesHeldSupport(held, held, beneficial: true), "a held member may buff themselves outside the circle");
                Assert.IsFalse(GauntletHooks.RefusesHeldSupport(free, patient, beneficial: true), "control: a free member may");
                Assert.IsFalse(GauntletHooks.RefusesHeldSupport(held, patient, beneficial: false), "a harmful spell is not this rule's business");

                GauntletHooks.OnBeneficialAct(held, patient, beneficial: true);
                Assert.IsFalse(run.Fought.Contains(held.Guid.Full), "nor does a held member's support count");
            }
            finally
            {
                GauntletHooks.RunForInstance = i => GauntletManager.ForInstance(i);
            }
        }

        [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "positionCache")]
        private static extern ref Dictionary<ACE.Entity.Enum.Properties.PositionType, Position> PositionCache(WorldObject wo);

        [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "ephemeralPositions")]
        private static extern ref Dictionary<ACE.Entity.Enum.Properties.PositionType, Position> EphemeralPositions(WorldObject wo);

        /// <summary>A seeded player at (x, y) on the fixture landblock in <paramref name="instance"/>, positions written through UnsafeAccessor.</summary>
        internal static Player PlayerAt(uint guid, uint instance, float x, float y)
        {
            var p = SalvageRollerVendorTests.MakePlayer(guid);
            PositionCache(p) = new Dictionary<ACE.Entity.Enum.Properties.PositionType, Position>();
            EphemeralPositions(p) = new Dictionary<ACE.Entity.Enum.Properties.PositionType, Position>();
            PositionCache(p)[ACE.Entity.Enum.Properties.PositionType.Location] = new Position(GauntletFixtureSites.Cell(GauntletFixtureSites.Primary), x, y, 50, 0, 0, 0, 1, instance);
            return p;
        }

        // ---- binding contracts ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Ending_HandsRunStartSnapshotAndScoreToTryAnnounce()
        {
            // The real GauntletRecords behind a recording wrapper: the run-start snapshot is 30; another allegiance banks 500
            // mid-run; this run ends at 62. Only a snapshot captured at Active(1) (30) announces; a live read (500) would not.
            var saved = SaveRecordSeams();
            var broadcasts = new List<string>();

            try
            {
                GauntletRecords.Loader = () => new List<AllegianceGauntletRecord> { new AllegianceGauntletRecord { AvatarId = 900, BestCenti = 30, BestAt = T0.AddDays(-1) } };
                GauntletRecords.Persist = _ => { };
                GauntletRecords.Broadcast = broadcasts.Add;
                GauntletRecords.ToAllegiance = (m, t) => { };
                GauntletRecords.NameSource = id => ("Test Allegiance", null);
                GauntletRecords.Initialize();

                var recording = new RecordingRecords(new LiveGauntletRecords());
                var h = new Harness { Records = recording, Plans = (w, hc) => TrashPlan(w, 4) };
                var run = h.StartActive();

                GauntletRecords.Bank(901, 500, 10, 77, h.Now);
                Assert.AreEqual(500L, GauntletRecords.SnapshotTop(), "control: the live top moved");

                h.Advance(5);
                h.Kill(run, h.Spawner.Spawned[0]);
                h.Spawner.Spawned[1].Dealt = 500;
                h.Spawner.Spawned[2].Dealt = 1000;
                h.Advance(240);

                var a = recording.Announces.Single();
                Assert.AreEqual(30L, a.Snapshot, "the snapshot captured at Active(1)");
                Assert.AreEqual(run.ScoreCenti, a.Score, "the run's own final score");
                Assert.AreEqual(62L, a.Score);
                Assert.AreEqual(run.RunId, a.RunId);
                Assert.IsTrue(broadcasts.Any(b => b.Contains("wave 0.62 - a new record!")), "the real TryAnnounce broadcast on the run-start snapshot");
            }
            finally
            {
                RestoreRecordSeams(saved);
            }
        }

        [TestMethod]
        public void Cooldown_GoesOnlyThroughNoteRunCommitted()
        {
            var h = new Harness();
            var run = h.StartActive();

            var commit = h.FakeRecords.Commits.Single();
            Assert.AreEqual(AvatarId, commit.Avatar);
            Assert.AreEqual(run.CommittedAt, commit.At, "the cooldown starts at the commit");
            Assert.AreEqual("commit", h.FakeRecords.Calls.First(c => c != "snapshot"), "written through NoteRunCommitted before anything else");

            // The start gate reads it back through LastRunAt.
            var fresh = new Harness { Records = new FakeRecords() };
            ((FakeRecords)fresh.Records).Last[AvatarId] = commit.At;
            fresh.Now = commit.At.AddHours(23);
            StringAssert.Contains(fresh.Manager.CheckCanStart(AvatarId, fresh.Now), "must rest");
            fresh.Now = commit.At.AddHours(24);
            Assert.IsNull(fresh.Manager.CheckCanStart(AvatarId, fresh.Now));
        }

        [TestMethod]
        public void Cooldown_PersistedAcrossReload()
        {
            var saved = SaveRecordSeams();
            var persisted = new List<AllegianceGauntletRecord>();

            try
            {
                GauntletRecords.Loader = () => new List<AllegianceGauntletRecord>();
                GauntletRecords.Persist = persisted.Add;
                GauntletRecords.Broadcast = _ => { };
                GauntletRecords.ToAllegiance = (m, t) => { };
                GauntletRecords.NameSource = id => (null, null);
                GauntletRecords.Initialize();

                var h = new Harness { Records = new LiveGauntletRecords() };
                var run = h.StartActive();
                var committedAt = run.CommittedAt.Value;

                var row = persisted.Last(r => r.AvatarId == AvatarId);
                Assert.AreEqual(committedAt, row.LastRunAt, "the commit reached the record row");
                Assert.AreEqual(1u, row.RunCount);

                // A restart: a new cache from what was persisted, and a new manager with no session memory.
                GauntletRecords.Loader = () => new List<AllegianceGauntletRecord> { row };
                GauntletRecords.Initialize();

                var after = new Harness { Records = new LiveGauntletRecords(), Now = committedAt.AddHours(1) };
                StringAssert.Contains(after.Manager.CheckCanStart(AvatarId, after.Now), "must rest", "the cooldown survives the reload");
                StringAssert.Contains(after.Manager.CheckCanStart(AvatarId, after.Now), "23h 0m");

                after.Now = committedAt.AddHours(24).AddSeconds(1);
                Assert.IsNull(after.Manager.CheckCanStart(AvatarId, after.Now));
            }
            finally
            {
                RestoreRecordSeams(saved);
            }
        }

        [TestMethod]
        public void Accepts_FailsClosed_WhenAdmissionLookupThrows()
        {
            var h = new Harness { MemberLookup = _ => p => throw new InvalidOperationException("Collection was modified") };
            var run = h.Start();
            h.Advance(0.5);
            Assert.AreEqual(GauntletState.Staging, run.State, "fixture: an admitting state");

            var realm = RealmManager.NewGauntletOutdoorRealm(h.Site.Outdoor, run.Hold);
            var player = SalvageRollerVendorTests.MakePlayer(MemberGuid);

            Assert.IsFalse(realm.Accepts(player), "a throwing lookup refuses, and does not throw");
            Assert.IsFalse(run.Admits(player));

            var control = new Harness();
            var open = control.Start();
            control.Advance(0.5);
            Assert.IsTrue(RealmManager.NewGauntletOutdoorRealm(control.Site.Outdoor, open.Hold).Accepts(player), "control: the same path admits when the lookup answers");
        }

        // ---- contact and the gate target ------------------------------------------------------------------------------------

        [TestMethod]
        public void ContactWaves_FromManyThreads_DistinctPerCharacter_Eligibility()
        {
            var h = new Harness();
            var run = h.StartActive();

            Parallel.For(0, 4000, i => run.RecordContact((uint)(0x50001000 + i % 4), 1 + i % 3));

            for (uint g = 0x50001000; g < 0x50001004; g++)
                CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, run.ContactWaves(g).ToArray());

            Assert.AreEqual(4, run.FoughtCount);
            Assert.AreEqual(0, run.ContactWaves(0x5000FFFF).Count, "no contact");
            Assert.IsTrue(run.IsEligible(0x50001000), "a run that reached 1 wave needs only contact in it");
        }

        [TestMethod]
        public void ExemptParticipant_RunStillBanksAndAnnounces()
        {
            // Owner ruling 2026-11-23: /top gauntlet ignores the leaderboard exemption, so an admin in the allegiance (or the
            // run) must not stop the record being written or announced. Real GauntletRecords, a real exempt Player.
            var saved = SaveRecordSeams();
            var broadcasts = new List<string>();
            var persisted = new List<AllegianceGauntletRecord>();

            try
            {
                GauntletRecords.Loader = () => new List<AllegianceGauntletRecord>();
                GauntletRecords.Persist = persisted.Add;
                GauntletRecords.Broadcast = broadcasts.Add;
                GauntletRecords.ToAllegiance = (m, t) => { };
                GauntletRecords.NameSource = id => ("Test Allegiance", null);
                GauntletRecords.Initialize();

                var h = new Harness { Records = new LiveGauntletRecords(), Plans = (w, hc) => TrashPlan(w, 4) };
                var run = h.StartActive();

                var admin = PlayerAt(0x50006001, run.Instance, 100, 96 + 10);
                admin.SetProperty(ACE.Entity.Enum.Properties.PropertyBool.IsAdmin, true);
                Assert.IsTrue(LeaderboardExemptionManager.IsExempt(admin, new HashSet<string>()), "fixture: this player is /top-exempt");

                run.OnMemberContact(admin, 1, active: true);
                Assert.IsTrue(run.Fought.Contains(admin.Guid.Full), "fixture: the exempt member's contact was recorded");

                h.Advance(5);
                h.Kill(run, h.Spawner.Spawned[0]);
                h.Spawner.Spawned[1].Dealt = 500;
                h.Spawner.Spawned[2].Dealt = 1000;
                h.Advance(240);

                Assert.AreEqual(62L, run.ScoreCenti);
                Assert.IsTrue(persisted.Any(r => r.AvatarId == AvatarId && r.BestCenti == 62), "the record is written");
                Assert.AreEqual(1, GauntletRecords.Board().Count(r => r.AvatarId == AvatarId), "and the Avatar is on the board");
                Assert.IsTrue(broadcasts.Any(b => b.Contains("wave 0.62 - a new record!")), "and the server is told");
            }
            finally
            {
                RestoreRecordSeams(saved);
            }
        }

        [TestMethod]
        public void GateTarget_RotatesEntries_InTheArenaInstance()
        {
            var h = new Harness();
            h.Site.Entries = new List<DungeonSpawnPointDef> { GauntletFixtureSites.At(110, 96), GauntletFixtureSites.At(90, 96) };
            var run = h.Start();

            IGauntletGateTarget target = run;
            Assert.IsNull(target.NextEntryPoint(), "no arena loaded yet");

            h.Advance(0.5);
            var a = target.NextEntryPoint();
            var b = target.NextEntryPoint();
            var c = target.NextEntryPoint();

            Assert.AreEqual(run.Instance, a.Instance);
            Assert.AreEqual(110f, a.PositionX);
            Assert.AreEqual(90f, b.PositionX);
            Assert.AreEqual(110f, c.PositionX, "the rotation wraps");
            Assert.AreEqual(0.0, target.DeathHoldSecondsLeft(null), "0 until Task 9's hold");
            Assert.AreEqual(GauntletState.Staging, target.State);
        }

        [TestMethod]
        public void FighterDeath_NeverEndsTheRun_RespawnsAfterHold()
        {
            var h = new Harness();
            var run = h.StartActive();

            run.OnFighterDied();
            Assert.IsTrue(h.World.Lines.Any(l => l.StartsWith("The Avatar has fallen.")));

            h.Advance(59);
            Assert.IsFalse(h.Hooks.Calls.Contains("fighter.respawn"));
            Assert.AreEqual(GauntletState.Active, run.State);

            h.Advance(1.5);
            Assert.AreEqual(1, h.Hooks.Calls.Count(x => x == "fighter.respawn"));
            Assert.IsTrue(h.World.Lines.Contains("The Avatar rejoins the fight."));
        }

        // ---- seams of GauntletRecords ---------------------------------------------------------------------------------------

        private sealed record RecordSeams(
            Func<List<AllegianceGauntletRecord>> Loader,
            Action<AllegianceGauntletRecord> Persist,
            Func<uint, (string, string)> NameSource,
            Action<string> Broadcast,
            Action<uint, string> ToAllegiance);

        private static RecordSeams SaveRecordSeams() => new RecordSeams(GauntletRecords.Loader, GauntletRecords.Persist,
            GauntletRecords.NameSource, GauntletRecords.Broadcast, GauntletRecords.ToAllegiance);

        private static void RestoreRecordSeams(RecordSeams s)
        {
            GauntletRecords.Loader = s.Loader;
            GauntletRecords.Persist = s.Persist;
            GauntletRecords.NameSource = s.NameSource;
            GauntletRecords.Broadcast = s.Broadcast;
            GauntletRecords.ToAllegiance = s.ToAllegiance;
            GauntletRecords.ResetForTests();
        }
    }
}
