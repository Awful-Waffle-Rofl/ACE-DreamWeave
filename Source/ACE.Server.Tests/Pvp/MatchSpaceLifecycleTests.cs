using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Pvp;
using ACE.Server.Realms;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// EphemeralMatchSpaceProvider's decisions, driven through MatchSpaceLifecycle with a fake host. The live host
    /// (RealmManager.GetNewEphemeralLandblock, EphemeralRealm.Admit, InstanceRouting, the destruction queue) needs
    /// the client dats and a world database, so what these tests pin is the ORDER and the refusals: nothing is
    /// created before the cheap checks pass, every refusal after creation disposes of the copy, no failure throws,
    /// and readiness and release only ever act on the exact copy that was allocated.
    /// </summary>
    [TestClass]
    public class MatchSpaceLifecycleTests
    {
        private sealed class FakePlayer
        {
            public uint Guid;
            public string Name;
        }

        private sealed class FakeLandblock
        {
            public uint Instance;
            public uint LandblockId;
            public bool Dungeon = true;
            public bool HasRealm = true;
            public bool Completed;
            public bool Occupied;
            public readonly List<FakePlayer> Admitted = new List<FakePlayer>();
        }

        private sealed class FakeHost : IMatchInstanceHost<FakePlayer, FakeLandblock>
        {
            public bool RealmRegistered = true;
            public Func<uint, FakePlayer, ushort, FakeLandblock> Create;
            public Func<Position, FakePlayer, InstanceRejection> Arrival = (_, _) => InstanceRejection.None;
            public bool IsDungeonThrows;
            public bool HasPlayersThrows;

            public readonly Dictionary<uint, FakeLandblock> Live = new Dictionary<uint, FakeLandblock>();
            public readonly List<(uint LandblockId, FakePlayer Owner, ushort Realm)> Creates = new List<(uint, FakePlayer, ushort)>();
            public readonly List<FakeLandblock> Discarded = new List<FakeLandblock>();
            public readonly List<(FakeLandblock Landblock, bool Occupied)> Released = new List<(FakeLandblock, bool)>();
            public readonly List<(Position Destination, FakePlayer Player)> Arrivals = new List<(Position, FakePlayer)>();

            public bool IsRealmRegistered(ushort realmId) => RealmRegistered;

            public FakeLandblock CreateInstance(uint landblockId, FakePlayer owner, ushort realmId)
            {
                Creates.Add((landblockId, owner, realmId));
                var lb = Create(landblockId, owner, realmId);
                if (lb != null)
                    Live[lb.Instance] = lb;
                return lb;
            }

            public uint InstanceOf(FakeLandblock landblock) => landblock.Instance;

            public uint LandblockIdOf(FakeLandblock landblock) => landblock.LandblockId;

            public bool IsDungeon(FakeLandblock landblock) => IsDungeonThrows ? throw new InvalidOperationException("dat read failed") : landblock.Dungeon;

            public bool Admit(FakeLandblock landblock, FakePlayer player)
            {
                if (!landblock.HasRealm)
                    return false;
                landblock.Admitted.Add(player);
                return true;
            }

            public InstanceRejection CheckArrival(Position destination, FakePlayer player)
            {
                Arrivals.Add((destination, player));
                return Arrival(destination, player);
            }

            public FakeLandblock GetLive(uint instance) => Live.TryGetValue(instance, out var lb) ? lb : null;

            public bool CreateCompleted(FakeLandblock landblock) => landblock.Completed;

            public bool HasPlayers(FakeLandblock landblock) => HasPlayersThrows ? throw new InvalidOperationException("scan failed") : landblock.Occupied;

            public void Discard(FakeLandblock landblock) => Discarded.Add(landblock);

            public void QueueForDestructionWhenEmpty(FakeLandblock landblock, bool occupied) => Released.Add((landblock, occupied));

            public uint GuidOf(FakePlayer player) => player.Guid;

            public string NameOf(FakePlayer player) => player.Name;
        }

        private static readonly uint Instance1 = Position.InstanceIDFromVars(1, 0x0101, isTemporaryRuleset: true);

        private static FakeHost HealthyHost() => new FakeHost
        {
            Create = (landblockId, owner, realm) => new FakeLandblock
            {
                Instance = Position.InstanceIDFromVars(realm, 0x0101, isTemporaryRuleset: true),
                LandblockId = landblockId,
            },
        };

        private static List<FakePlayer> Players(int n) =>
            Enumerable.Range(0, n).Select(i => new FakePlayer { Guid = 0x50000001u + (uint)i, Name = $"P{i}" }).ToList();

        [TestMethod]
        public void Allocate_Healthy_CreatesAPrivateRealm1CopyOwnedByTheFirstPlayerAndAdmitsEveryone()
        {
            var host = HealthyHost();
            var players = Players(3);
            var core = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host);

            var result = core.Allocate(ArenaMapCatalog.Arena0067, players);

            Assert.IsTrue(result.Succeeded, result.Detail);
            Assert.AreEqual(MatchSpaceFailure.None, result.Failure);
            Assert.AreEqual(1, host.Creates.Count);
            Assert.AreEqual(0x0067u, host.Creates[0].LandblockId);
            Assert.AreSame(players[0], host.Creates[0].Owner);
            Assert.AreEqual((ushort)1, host.Creates[0].Realm);

            var lb = host.Live[Instance1];
            CollectionAssert.AreEqual(players, lb.Admitted);

            var space = result.Space;
            Assert.AreEqual("arena_0067", space.MapKey);
            Assert.AreEqual(0x0067u, space.LandblockId);
            Assert.AreEqual((ushort)1, space.RealmId);
            Assert.AreEqual(Instance1, space.Instance);
            Assert.AreEqual(players[0].Guid, space.OwnerGuid);
            Assert.AreSame(lb, space.Handle);
            Assert.AreEqual(0, host.Discarded.Count);
        }

        [TestMethod]
        public void Allocate_ChecksEveryArrivalAgainstARealDestinationInTheNewInstance_AfterAdmitting()
        {
            var host = HealthyHost();
            var players = Players(2);
            FakeLandblock seen = null;
            host.Arrival = (dest, p) =>
            {
                seen = host.Live[Instance1];
                Assert.IsTrue(seen.Admitted.Contains(p), "arrival was checked before the player was admitted");
                return InstanceRejection.None;
            };

            new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host).Allocate(ArenaMapCatalog.Arena0066, players);

            Assert.AreEqual(2, host.Arrivals.Count);
            CollectionAssert.AreEqual(players, host.Arrivals.Select(a => a.Player).ToList());

            foreach (var (dest, _) in host.Arrivals)
            {
                Assert.AreEqual(Instance1, dest.Instance);
                Assert.AreEqual(0x0066u, (uint)dest.LandblockId.Landblock);
                Assert.IsTrue((dest.Cell & 0xFFFF) >= 0x0100);
            }
        }

        [TestMethod]
        public void Allocate_CheapRefusals_NeverCreateAnything()
        {
            var cases = new (ArenaMap Map, IReadOnlyList<FakePlayer> Players, MatchSpaceFailure Expected)[]
            {
                (null, Players(2), MatchSpaceFailure.NoMap),
                (ArenaMapCatalog.Arena0066, null, MatchSpaceFailure.NoParticipants),
                (ArenaMapCatalog.Arena0066, new List<FakePlayer>(), MatchSpaceFailure.NoParticipants),
                (ArenaMapCatalog.Arena0066, new List<FakePlayer> { Players(1)[0], null }, MatchSpaceFailure.NullParticipant),
                (new ArenaMap("empty", 0x0066, 1, new Dictionary<string, IReadOnlyList<PvpSpawnPoint>>()), Players(2), MatchSpaceFailure.NoSpawnPoints),
                (new ArenaMap("wide", 0x00660000, 1, new Dictionary<string, IReadOnlyList<PvpSpawnPoint>> { ["1v1"] = ArenaMapCatalog.OneVOneSet }), Players(2), MatchSpaceFailure.InvalidMap),
                (new ArenaMap("outdoor", 0x0066, 1, new Dictionary<string, IReadOnlyList<PvpSpawnPoint>> { ["1v1"] = new[] { new PvpSpawnPoint("X", 0x0001, 1, 1, 0.005f, 1, 0) } }), Players(2), MatchSpaceFailure.InvalidMap),
            };

            foreach (var (map, players, expected) in cases)
            {
                var host = HealthyHost();
                var result = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host).Allocate(map, players);

                Assert.IsFalse(result.Succeeded, expected.ToString());
                Assert.IsNull(result.Space, expected.ToString());
                Assert.AreEqual(expected, result.Failure);
                Assert.IsFalse(string.IsNullOrEmpty(result.Detail), expected.ToString());
                Assert.AreEqual(0, host.Creates.Count, $"{expected}: an instance was created");
            }
        }

        [TestMethod]
        public void Allocate_UnregisteredRealm_RefusesBeforeCreating()
        {
            var host = HealthyHost();
            host.RealmRegistered = false;

            var result = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host).Allocate(ArenaMapCatalog.Arena0066, Players(2));

            Assert.AreEqual(MatchSpaceFailure.RealmNotRegistered, result.Failure);
            Assert.AreEqual(0, host.Creates.Count);
        }

        [TestMethod]
        public void Allocate_CreateThrowsOrReturnsNull_IsARefusalNotAThrow()
        {
            var throwing = HealthyHost();
            throwing.Create = (_, _, _) => throw new InvalidOperationException("dat missing");

            var r1 = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(throwing).Allocate(ArenaMapCatalog.Arena0066, Players(2));
            Assert.AreEqual(MatchSpaceFailure.CreateFailed, r1.Failure);
            StringAssert.Contains(r1.Detail, "InvalidOperationException");

            var nulling = HealthyHost();
            nulling.Create = (_, _, _) => null;

            var r2 = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(nulling).Allocate(ArenaMapCatalog.Arena0066, Players(2));
            Assert.AreEqual(MatchSpaceFailure.CreateFailed, r2.Failure);
            Assert.AreEqual(0, nulling.Discarded.Count, "nothing was created, so nothing is disposed of");
        }

        [TestMethod]
        public void Allocate_RefusalsAfterCreation_DisposeOfTheCopyAndAdmitNoOneFurther()
        {
            var cases = new (Action<FakeHost> Arrange, MatchSpaceFailure Expected)[]
            {
                (h => h.Create = (lb, o, r) => new FakeLandblock { Instance = Instance1, LandblockId = 0x0067 }, MatchSpaceFailure.LandblockMismatch),
                (h => h.Create = (lb, o, r) => new FakeLandblock { Instance = Instance1, LandblockId = lb, Dungeon = false }, MatchSpaceFailure.NotADungeon),
                (h => h.Create = (lb, o, r) => new FakeLandblock { Instance = Position.InstanceIDFromVars(1, 0, false), LandblockId = lb }, MatchSpaceFailure.InstanceMismatch),
                (h => h.Create = (lb, o, r) => new FakeLandblock { Instance = Position.InstanceIDFromVars(0, 0x0101, true), LandblockId = lb }, MatchSpaceFailure.InstanceMismatch),
                (h => h.Create = (lb, o, r) => new FakeLandblock { Instance = Instance1, LandblockId = lb, HasRealm = false }, MatchSpaceFailure.AdmitFailed),
                (h => h.Arrival = (d, p) => p.Name == "P1" ? InstanceRejection.EphemeralAccessDenied : InstanceRejection.None, MatchSpaceFailure.ParticipantRefused),
                (h => h.IsDungeonThrows = true, MatchSpaceFailure.ValidationFailed),
            };

            foreach (var (arrange, expected) in cases)
            {
                var host = HealthyHost();
                arrange(host);

                var result = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host).Allocate(ArenaMapCatalog.Arena0066, Players(2));

                Assert.AreEqual(expected, result.Failure);
                Assert.IsNull(result.Space, expected.ToString());
                Assert.AreEqual(1, host.Creates.Count, expected.ToString());
                Assert.AreEqual(1, host.Discarded.Count, $"{expected}: the refused copy was not disposed of");
                Assert.AreSame(host.Live.Values.Single(), host.Discarded[0], expected.ToString());
            }
        }

        [TestMethod]
        public void Allocate_ParticipantRefused_NamesThePlayerAndTheReason()
        {
            var host = HealthyHost();
            host.Arrival = (d, p) => p.Name == "P1" ? InstanceRejection.EphemeralAccessDenied : InstanceRejection.None;

            var result = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host).Allocate(ArenaMapCatalog.Arena0066, Players(2));

            StringAssert.Contains(result.Detail, "P1");
            StringAssert.Contains(result.Detail, nameof(InstanceRejection.EphemeralAccessDenied));
        }

        [TestMethod]
        public void GetReadiness_LoadingThenReady()
        {
            var host = HealthyHost();
            var core = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host);
            var space = core.Allocate(ArenaMapCatalog.Arena0066, Players(2)).Space;

            Assert.AreEqual(MatchSpaceReadiness.Loading, core.GetReadiness(space));

            host.Live[Instance1].Completed = true;

            Assert.AreEqual(MatchSpaceReadiness.Ready, core.GetReadiness(space));
        }

        [TestMethod]
        public void GetReadiness_UnloadedOrRecycledOrNull_IsGone()
        {
            var host = HealthyHost();
            var core = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host);
            var space = core.Allocate(ArenaMapCatalog.Arena0066, Players(2)).Space;
            host.Live[Instance1].Completed = true;

            // the same instance id now belongs to a different, fully loaded copy
            host.Live[Instance1] = new FakeLandblock { Instance = Instance1, LandblockId = 0x0066, Completed = true };
            Assert.AreEqual(MatchSpaceReadiness.Gone, core.GetReadiness(space));

            host.Live.Remove(Instance1);
            Assert.AreEqual(MatchSpaceReadiness.Gone, core.GetReadiness(space));

            Assert.AreEqual(MatchSpaceReadiness.Gone, core.GetReadiness(null));
            Assert.AreEqual(MatchSpaceReadiness.Gone, core.GetReadiness(new MatchSpace("x", 0x0066, 1, Instance1, 1)));
        }

        [TestMethod]
        public void Release_PassesOccupancyToTheSharedDestructionPath()
        {
            var host = HealthyHost();
            var core = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host);
            var space = core.Allocate(ArenaMapCatalog.Arena0066, Players(2)).Space;
            var lb = host.Live[Instance1];

            lb.Occupied = true;
            core.Release(space);

            lb.Occupied = false;
            core.Release(space);

            Assert.AreEqual(2, host.Released.Count);
            Assert.AreSame(lb, host.Released[0].Landblock);
            Assert.IsTrue(host.Released[0].Occupied);
            Assert.IsFalse(host.Released[1].Occupied);
            Assert.AreEqual(0, host.Discarded.Count, "release goes through the shared path, not the refusal disposal");
        }

        [TestMethod]
        public void Release_NeverTouchesACopyThatIsNotTheAllocatedOne()
        {
            var host = HealthyHost();
            var core = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host);
            var space = core.Allocate(ArenaMapCatalog.Arena0066, Players(2)).Space;

            host.Live[Instance1] = new FakeLandblock { Instance = Instance1, LandblockId = 0x0066 };
            core.Release(space);

            host.Live.Remove(Instance1);
            core.Release(space);

            core.Release(null);

            Assert.AreEqual(0, host.Released.Count);
        }

        [TestMethod]
        public void Release_HostThrows_DoesNotThrow()
        {
            var host = HealthyHost();
            var core = new MatchSpaceLifecycle<FakePlayer, FakeLandblock>(host);
            var space = core.Allocate(ArenaMapCatalog.Arena0066, Players(2)).Space;
            host.HasPlayersThrows = true;

            core.Release(space);

            Assert.AreEqual(0, host.Released.Count);
        }
    }
}
