using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The live <see cref="ITrialWorld"/>: one trial's private room, an ephemeral copy of the room landblock. Every member runs
    /// on that landblock's thread (TrialManager enqueues the session's calls there), except <see cref="PlayerOnline"/> and
    /// <see cref="PlayerInRoom"/>, which only read.
    /// </summary>
    public sealed class LiveTrialWorld : ITrialWorld
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Landblock landblock;
        private readonly uint instance;
        private readonly uint playerGuid;
        private readonly TrialRoom room;
        private readonly Func<bool> roomLoaded;
        private readonly Func<IEnumerable<WorldObject>> roomObjects;
        private readonly Func<uint, Player> onlinePlayer;

        /// <summary>Spawned creatures by guid. Written on the landblock thread; concurrent so the status command can read the alive counts.</summary>
        private readonly ConcurrentDictionary<uint, Creature> creatures = new ConcurrentDictionary<uint, Creature>();

        /// <summary>The plan role each spawned creature was placed as, by guid (the trial path never sets Creature.DungeonRole).</summary>
        private readonly ConcurrentDictionary<uint, DungeonRole> roles = new ConcurrentDictionary<uint, DungeonRole>();

        public LiveTrialWorld(Landblock landblock, uint playerGuid, TrialRoom room)
            : this(landblock ?? throw new ArgumentNullException(nameof(landblock)), landblock.Instance, playerGuid, room,
                  () => landblock.CreateWorldObjectsCompleted,
                  () => landblock.GetWorldObjectsForPhysicsHandling().ToList(),
                  PlayerManager.GetOnlinePlayer)
        {
        }

        /// <summary>Test seam: the room's load state, its objects and the player lookup are supplied by the caller.</summary>
        internal LiveTrialWorld(Landblock landblock, uint instance, uint playerGuid, TrialRoom room, Func<bool> roomLoaded,
            Func<IEnumerable<WorldObject>> roomObjects, Func<uint, Player> onlinePlayer)
        {
            this.landblock = landblock;
            this.instance = instance;
            this.playerGuid = playerGuid;
            this.room = room;
            this.roomLoaded = roomLoaded ?? throw new ArgumentNullException(nameof(roomLoaded));
            this.roomObjects = roomObjects ?? throw new ArgumentNullException(nameof(roomObjects));
            this.onlinePlayer = onlinePlayer ?? throw new ArgumentNullException(nameof(onlinePlayer));
        }

        public Landblock Landblock => landblock;

        public uint Instance => instance;

        /// <summary>The session this world serves. Set once by TrialStarter, before the session is registered.</summary>
        public TrialSession Session { get; private set; }

        internal void Bind(TrialSession session) => Session = session;

        /// <summary>The session's player-side source for spawn predictions. Set once by TrialStarter; null means no predictions.</summary>
        internal TrialPlayerSideSource PlayerSides { get; set; }

        private string Tag => $"trial:0x{instance:X8}";

        public bool RoomLoaded => roomLoaded();

        /// <summary>Attackable, non-player creatures in the copy that no trial spawned (P_TrialRep null). Pets are the player's own and are not base content.</summary>
        public int LiveForeignCreatures() => CountForeign(roomObjects());

        internal static int CountForeign(IEnumerable<WorldObject> objects)
        {
            if (objects == null)
                return 0;

            return objects.Count(wo => wo is Creature c && c is not Player && c is not Pet && !c.IsDestroyed
                && c.P_TrialRep == null && c.Attackable);
        }

        public IReadOnlyList<uint> SpawnWave(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRep rep)
        {
            var guids = new List<uint>();

            if (wave == null || rep == null || landblock == null)
                return guids;

            // Whatever has arrived by now; a spawn never waits for it.
            rep.PlayerSide = PlayerSides?.Side;

            foreach (var entry in wave)
            {
                var creature = TrialSpawner.Spawn(rep.Plan, entry, rep.ObservedMaxNonBoss, rep, landblock, instance, Tag);

                if (creature == null)
                    continue;

                creatures[creature.Guid.Full] = creature;
                roles[creature.Guid.Full] = entry.Role;
                guids.Add(creature.Guid.Full);
            }

            if (guids.Count < wave.Count)
                log.Warn($"[TRIAL] {Tag} rep {rep.Number} wave {rep.WaveIndex}: {guids.Count} of {wave.Count} creatures entered the world");

            return guids;
        }

        /// <summary>Alive spawned creatures per plan role. Read-only; safe from any thread (the status command's source).</summary>
        public (int Trash, int Elite, int Boss) AliveByRole()
        {
            int trash = 0, elite = 0, boss = 0;

            foreach (var kv in creatures)
            {
                var c = kv.Value;

                if (c == null || c.IsDead || c.IsDestroyed || !roles.TryGetValue(kv.Key, out var role))
                    continue;

                if (role == DungeonRole.Boss) boss++;
                else if (role == DungeonRole.Elite) elite++;
                else trash++;
            }

            return (trash, elite, boss);
        }

        public bool IsAlive(uint guid)
            => creatures.TryGetValue(guid, out var c) && c != null && !c.IsDead && !c.IsDestroyed;

        public void DestroyAll()
        {
            foreach (var c in creatures.Values.ToList())
            {
                if (c == null || c.IsDestroyed)
                    continue;

                try
                {
                    c.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[TRIAL] {Tag} destroying 0x{c.Guid.Full:X8} threw", ex);
                }
            }

            creatures.Clear();
            roles.Clear();
        }

        /// <summary>The bound player object is online. A relogged character is a new Player with no session, and reads as gone.</summary>
        public bool PlayerOnline
        {
            get
            {
                var player = onlinePlayer(playerGuid);
                return player != null && (Session == null || ReferenceEquals(player.TrialSession, Session));
            }
        }

        public bool PlayerInRoom
        {
            get
            {
                var player = onlinePlayer(playerGuid);
                var location = player?.Location;
                return location != null && location.Instance == instance;
            }
        }

        /// <summary>Sends the player back to the room start. Only a player still in the room is moved: a player who has left is never pulled back.</summary>
        public void ReturnPlayerToStart()
        {
            if (!PlayerInRoom || room?.Start == null)
                return;

            var player = onlinePlayer(playerGuid);

            if (player == null)
                return;

            WorldManager.ThreadSafeTeleport(player, StartPosition(room, instance), source: "trial-start");
        }

        /// <summary>The room's start point in <paramref name="instance"/>.</summary>
        public static Position StartPosition(TrialRoom room, uint instance)
        {
            var p = room.Start;
            return new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, instance);
        }
    }
}
