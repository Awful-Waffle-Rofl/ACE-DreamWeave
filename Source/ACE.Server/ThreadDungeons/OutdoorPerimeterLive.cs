using System;
using System.Collections.Generic;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>The wisp ring tunables (R21), read once per placement attempt.</summary>
    public readonly record struct OutdoorRingSettings(bool Enabled, double SpacingM)
    {
        public static OutdoorRingSettings FromTunables() => new OutdoorRingSettings(OutdoorSiteTunables.RingEnabled, OutdoorSiteTunables.RingSpacingM);
    }

    /// <summary>
    /// Production wiring for <see cref="OutdoorPerimeterTicker"/>: builds the run views from the live runs and wraps a
    /// Player as an <see cref="IPerimeterSubject"/>. Everything that needs a world lives here; the logic is in the ticker.
    /// </summary>
    public static class OutdoorPerimeterLive
    {
        /// <summary>Views for every live (not Ended) outdoor run that has a parsed site.</summary>
        public static IReadOnlyList<PerimeterRunView> BuildViews(IEnumerable<ThreadDungeonRun> runs)
        {
            var views = new List<PerimeterRunView>();

            if (runs == null)
                return views;

            foreach (var run in runs)
            {
                var site = run?.Dungeon?.Outdoor;
                if (site == null || run.Dungeon.IsOutdoor != true || site.Circle == null || run.State == ThreadDungeonRunState.Ended)
                    continue;

                var captured = run;
                var footprint = ThreadRunGeometry.RunLandblocks(run.Dungeon);

                views.Add(new PerimeterRunView
                {
                    RunId = run.RunId,
                    Circle = site.Circle,
                    Footprint = footprint,
                    Subjects = () => SubjectsFor(captured),
                    Pulse = subject => OutdoorWispRing.Pulse(captured.RunId, subject),
                    PlaceRing = () => OutdoorWispRing.TryPlace(captured),
                });
            }

            return views;
        }

        /// <summary>
        /// Roster members online and in the run's instance, on ANY landblock of it (the backstop must see a player who
        /// walked onto a buffer landblock, which IsMemberInside would not count as on the run).
        /// </summary>
        private static IReadOnlyList<IPerimeterSubject> SubjectsFor(ThreadDungeonRun run)
        {
            var list = new List<IPerimeterSubject>();

            foreach (var member in run.Roster)
            {
                var player = PlayerManager.GetOnlinePlayer(member.Guid);
                if (player?.Location == null || player.Location.Instance != run.Instance || run.IsPuzzleRemoved(member.Guid))
                    continue;

                list.Add(new PlayerPerimeterSubject(player, run));
            }

            return list;
        }

        /// <summary>The lethal write would kill: the health removed reaches the current health.</summary>
        public static bool WouldKill(long currentHealth, long amount) => amount >= currentHealth;

        /// <summary>
        /// Non-lethal clamp against the CURRENT health (re-read on the landblock thread; the verdict is a tick old):
        /// a player at 1 HP or below takes 0, otherwise at most current - 1, so health never reaches 0.
        /// </summary>
        public static long ClampNonLethal(long currentHealth, long amount)
            => currentHealth <= 1 ? 0 : Math.Min(Math.Max(amount, 0), currentHealth - 1);

        /// <summary>Runs <paramref name="action"/> only if <paramref name="stillApplicable"/> holds at execution time. Returns whether it ran.</summary>
        public static bool RunIfApplicable(Func<bool> stillApplicable, Action action)
        {
            if (!stillApplicable())
                return false;

            action();
            return true;
        }

        /// <summary>Seconds since the arrival stamp; a never-stamped player reads as "long ago" so the grace never protects an unknown.</summary>
        public static double SecondsSince(long stampTicks, long nowTicks)
            => stampTicks <= 0 ? 1e9 : Math.Max(0.0, (nowTicks - stampTicks) / (double)TimeSpan.TicksPerSecond);

        /// <summary>
        /// Seconds since the client's last inbound packet. A session with no packet on record reads as stale (1e9), the
        /// player-safe side: staleness suppresses damage.
        /// </summary>
        public static double SecondsSinceInbound(long lastPacketTicks, long nowTicks)
            => lastPacketTicks <= 0 ? 1e9 : Math.Max(0.0, (nowTicks - lastPacketTicks) / (double)TimeSpan.TicksPerSecond);
    }

    /// <summary>A Player seen through <see cref="IPerimeterSubject"/>.</summary>
    internal sealed class PlayerPerimeterSubject : IPerimeterSubject
    {
        private readonly Player player;
        private readonly ThreadDungeonRun run;

        public PlayerPerimeterSubject(Player player, ThreadDungeonRun run)
        {
            this.player = player;
            this.run = run;
        }

        public uint Guid => player.Guid.Full;

        public OutdoorPos Position
        {
            get
            {
                var loc = player.Location;
                return new OutdoorPos(loc.LandblockId.Landblock, loc.PositionX, loc.PositionY);
            }
        }

        public bool IsTeleporting => player.Teleporting;
        public bool IsDead => player.IsDead || player.IsInDeathProcess;
        public bool IsLoggingOut => player.IsLoggingOut;

        public double SecondsSinceArrival => OutdoorPerimeterLive.SecondsSince(player.LastArrivalTicks, DateTime.UtcNow.Ticks);

        public double SecondsSinceLastInbound
            => OutdoorPerimeterLive.SecondsSinceInbound(player.Session?.Network?.LastPacketReceivedTicks ?? 0, DateTime.UtcNow.Ticks);

        public long Health => player.Health.Current;
        public long MaxHealth => player.Health.MaxValue;

        public bool StillApplicable
            => player.Location != null && player.Location.Instance == run.Instance
               && !IsDead && !player.Teleporting && !player.IsLoggingOut && run.State != ThreadDungeonRunState.Ended;

        public bool RunOnOwnQueue(Action action)
        {
            var landblock = player.CurrentLandblock;
            if (landblock == null)
                return false;

            landblock.EnqueueAction(new ActionEventDelegate(action));
            return true;
        }

        public void WriteDamage(long amount, bool lethal)
        {
            // Admin / invincible protection: no damage (a snap-back still happens). Only Invincible is checked here;
            // UnderLifestoneProtection is not, so a freshly revived player outside the ring still takes Unraveling.
            if (player.Invincible || amount <= 0)
                return;

            var current = player.Health.Current;

            if (lethal)
            {
                // Lethal mode: latch the cause BEFORE the write, so Player.Die sees it (ThreadUnravelDeath).
                if (OutdoorPerimeterLive.WouldKill(current, amount))
                    ThreadUnravelDeath.Mark(player.Guid.Full);
            }
            else
            {
                amount = OutdoorPerimeterLive.ClampNonLethal(current, amount);
                if (amount <= 0)
                    return;
            }

            var applied = -player.UpdateVitalDelta(player.Health, -(int)Math.Min(amount, int.MaxValue));

            // Contribution summary: this write never records through DamageHistory, so it reports its own applied loss.
            if (applied > 0)
                run.RecordDamageTaken(player.Guid.Full, (uint)applied);

            if (player.Health.Current <= 0)
            {
                // The same tail as Player.TakeDamageOverTime: the vital write above floors at zero, so death follows here.
                player.OnDeath(player.DamageHistory.LastDamager, DamageType.Health, false);
                player.Die();
            }
        }

        public void SetHealthToOne()
        {
            // SET, never raise: a player already at 1 HP (or somehow lower) is left alone. Invincible is left alone too.
            if (player.Invincible)
                return;
            if (player.Health.Current > 1)
            {
                var lost = -player.UpdateVital(player.Health, 1);

                if (lost > 0)
                    run.RecordDamageTaken(player.Guid.Full, (uint)lost);
            }
        }

        public void TeleportToDrop()
        {
            // The stock world-queue teleport helper minus its fixed hop: the world queue runs this a tick later, so applicability is
            // re-checked there (a death or a teleport begun in between must not be teleported over). No source label: the
            // TeleportSource set is pinned by an invariant test, so the default (portal) label applies.
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                OutdoorPerimeterLive.RunIfApplicable(() => StillApplicable, () => player.Teleport(new Position(run.EntryPosition), true))));
        }

        public void Say(string text)
            => player.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
    }

    /// <summary>
    /// The live wisp ring (R21): placement once the run's whole footprint has finished loading, and the per-player pulse.
    /// Markers are plain Generic wisps (wcid 1002672: Ethereal, not Attackable, hidden from radar and UI), never adopted
    /// by the spawner, so they are not run creatures and not targetable. They live on the run's ephemeral landblocks, which
    /// destroy every non-player object when the copy unloads.
    /// </summary>
    public static class OutdoorWispRing
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const uint WispWcid = 1002672;

        /// <summary>
        /// The pulse effect: PlayScript.EnchantUpPurple (0x43), a short purple enchant swirl that matches the wisp's purple
        /// glow. Sent to the one player's session only. [?] Visibility on a wisp model is unverified in the client.
        /// </summary>
        public const PlayScript PulseScript = PlayScript.EnchantUpPurple;

        /// <summary>How many nearest markers pulse.</summary>
        public const int PulseCount = 3;

        /// <summary>Probe start height above terrain, as the world-event ring uses (Content/events/axes/sources.json probeHeight).</summary>
        private const float ProbeHeight = 60f;

        private readonly record struct Marker(ObjectGuid Guid, double Gx, double Gy);

        private static readonly object gate = new object();
        private static readonly Dictionary<uint, List<Marker>> markers = new Dictionary<uint, List<Marker>>();

        /// <summary>Drops the marker lists of runs that are no longer live.</summary>
        public static void Prune(ICollection<uint> liveRunIds)
        {
            lock (gate)
            {
                var dead = new List<uint>();
                foreach (var id in markers.Keys)
                {
                    if (!liveRunIds.Contains(id))
                        dead.Add(id);
                }

                foreach (var id in dead)
                    markers.Remove(id);
            }
        }

        /// <summary>
        /// Attempts the ring. Returns false while the footprint is still loading (try again next tick), true when it was
        /// placed or will never be (disabled, run ended, nothing plannable). World thread.
        /// </summary>
        public static bool TryPlace(ThreadDungeonRun run)
        {
            if (run?.Dungeon?.Outdoor == null || run.State == ThreadDungeonRunState.Ended)
                return true;

            var settings = OutdoorRingSettings.FromTunables();
            if (!settings.Enabled)
                return true;

            var footprint = ThreadRunGeometry.RunLandblocks(run.Dungeon);
            var instance = run.Instance;

            // The population gate (R22): every footprint member loaded and through CreateWorldObjects.
            var ready = ThreadRunGeometry.AllCreateCompleted(footprint, id =>
                LandblockManager.GetLoadedLandblock(new LandblockId((uint)id << 16), instance)?.CreateWorldObjectsCompleted);
            if (!ready)
                return false;

            var plan = OutdoorWispRingPlan.Build(run.Dungeon.Outdoor.Circle, footprint, settings.SpacingM);
            var groups = plan.ByLandblock();
            var unloaded = 0;
            var queued = 0;

            foreach (var group in groups)
            {
                // Footprint members are loaded and complete (the gate above), and the site lint guarantees the circle sits inside
                // the footprint, so a ring group on an unloaded landblock cannot happen for a valid site; the count is a belt.
                var landblock = LandblockManager.GetLoadedLandblock(new LandblockId((uint)group.Key << 16), instance);
                if (landblock == null)
                {
                    unloaded += group.Value.Count;
                    continue;
                }

                var candidates = new List<(WorldObject Wo, RingPoint Point)>(group.Value.Count);
                foreach (var point in group.Value)
                {
                    WorldObject wo = null;
                    try { wo = WorldObjectFactory.CreateNewWorldObject(WispWcid); }
                    catch (Exception ex) { log.Error($"[OWT] run {run.RunId} could not create wisp wcid {WispWcid}", ex); }

                    if (wo == null)
                        continue;

                    // No Generator back-reference, so the heartbeat would rot it otherwise (as the world-event ring does).
                    wo.TimeToRot = -1;
                    candidates.Add((wo, point));
                }

                queued += candidates.Count;
                var runRef = run;
                landblock.EnqueueAction(new ActionEventDelegate(() => PlaceGroup(runRef, landblock, candidates)));
            }

            log.Info($"[OWT] run {run.RunId} wisp ring: points={plan.Total} queued={queued} skippedOutsideFootprint={plan.SkippedOutsideFootprint} skippedOffGrid={plan.SkippedOffGrid} unloaded={unloaded}");
            return true;
        }

        /// <summary>Runs on <paramref name="landblock"/>'s own queue: probe each point to the ground, enter the world, record the marker.</summary>
        private static void PlaceGroup(ThreadDungeonRun run, Landblock landblock, List<(WorldObject Wo, RingPoint Point)> candidates)
        {
            var live = run.State != ThreadDungeonRunState.Ended && ReferenceEquals(ThreadDungeonManager.GetRun(run.Instance), run);
            var placed = 0;

            foreach (var (wo, point) in candidates)
            {
                if (wo.IsDestroyed)
                    continue;

                if (!live)
                {
                    wo.Destroy();
                    continue;
                }

                try
                {
                    var cellX = (int)(point.X / 24.0);
                    var cellY = (int)(point.Y / 24.0);
                    var cell = ((uint)point.Landblock << 16) | (uint)(cellX * 8 + cellY + 1);
                    var position = new Position(cell, (float)point.X, (float)point.Y, 0f, 0f, 0f, 0f, 1f, run.Instance);

                    var terrainZ = position.GetTerrainZ();
                    var probe = WorldEventGroundProbe.ProbeDown(position.LandblockId.Raw, position.Instance, position.PositionX, position.PositionY,
                        terrainZ, ProbeHeight, WorldEventGroundProbe.BaseSphereRadius, out _);

                    if (probe.Landed)
                    {
                        // As WorldEventSpawner.TryRingPosition: when the sweep came to rest on the same landblock take its whole
                        // resting point (cell, X, Y, Z - it may have slid off a pitched roof), else only its height.
                        if ((probe.Cell >> 16) == (position.LandblockId.Raw >> 16))
                        {
                            position.LandblockId = new LandblockId(probe.Cell);
                            position.PositionX = probe.Rest.X;
                            position.PositionY = probe.Rest.Y;
                        }

                        position.PositionZ = probe.Rest.Z;
                    }
                    else
                    {
                        position.AdjustMapCoords();
                    }

                    wo.Location = position;

                    if (!wo.EnterWorld())
                    {
                        if (!wo.IsDestroyed) wo.Destroy();
                        continue;
                    }

                    lock (gate)
                    {
                        if (!markers.TryGetValue(run.RunId, out var list))
                            markers[run.RunId] = list = new List<Marker>();
                        list.Add(new Marker(wo.Guid, point.Gx, point.Gy));
                    }

                    placed++;
                }
                catch (Exception ex)
                {
                    log.Error($"[OWT] run {run.RunId} wisp at landblock 0x{point.Landblock:X4} threw while placing", ex);
                    if (!wo.IsDestroyed) wo.Destroy();
                }
            }

            log.Debug($"[OWT] run {run.RunId} wisp ring landblock=0x{landblock.Id.Raw:X8} placed={placed} of {candidates.Count}");
        }

        /// <summary>Plays <see cref="PulseScript"/> on the <see cref="PulseCount"/> markers nearest the player, to that player's session only.</summary>
        public static void Pulse(uint runId, IPerimeterSubject subject)
        {
            List<Marker> snapshot;
            lock (gate)
            {
                if (!markers.TryGetValue(runId, out var list) || list.Count == 0)
                    return;
                snapshot = new List<Marker>(list);
            }

            var pos = subject.Position;
            var centres = new List<(double Gx, double Gy)>(snapshot.Count);
            foreach (var m in snapshot)
                centres.Add((m.Gx, m.Gy));

            var player = PlayerManager.GetOnlinePlayer(subject.Guid);
            var network = player?.Session?.Network;
            if (network == null)
                return;

            foreach (var index in OutdoorWispRingPlan.NearestIndices(centres, pos.Gx, pos.Gy, PulseCount))
                network.EnqueueSend(new GameMessageScript(snapshot[index].Guid, PulseScript));
        }
    }
}
