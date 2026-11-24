using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using log4net;

using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The registry of live Balance Trials sessions, one per private room instance. <see cref="Register"/> is called by
    /// <see cref="TrialStarter.TryStart"/> before the entry teleport. <see cref="Tick"/> runs on the world thread (WorldManager,
    /// after the speed-course tick) every heartbeat, throttles itself to <see cref="TickSeconds"/>, ends any session whose room
    /// landblock went away, and enqueues each live session's tick and vital sample onto its own landblock, where every world
    /// mutation happens. Modelled on SpeedCourseManager.
    ///
    /// A session leaves the registry (and its player is unbound) when its room is gone, when its player goes offline or leaves
    /// the room, or through <see cref="Remove"/>. A Done or Aborted session whose player is still in the room stays registered,
    /// so its last state can still be read; a new start replaces it.
    /// </summary>
    public static class TrialManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The session tick and vital sample cadence.</summary>
        public const double TickSeconds = 0.5;

        /// <summary>How long a room may be missing from the ephemeral registry, or its player outside it, before either was ever seen.</summary>
        public const double ArrivalGraceSeconds = 60.0;

        /// <summary>
        /// The last vital sample of a session's player, as fractions of each maximum. SampleVitals also routes it to the rep's recorder.
        /// Immutable and published by one reference write (Volatile), so a reader on another thread never sees a torn sample.
        /// </summary>
        public sealed record VitalSample(double Health, double Stamina, double Mana, double At);

        private sealed class Entry
        {
            public TrialSession Session;
            public ITrialWorld World;
            public Landblock Landblock;
            public uint Instance;
            public double RegisteredAt;
            public bool SeenLive;
            public bool SeenPlayer;
            public int TickQueued;
            public VitalSample LastVitals;
        }

        private static readonly ConcurrentDictionary<uint, Entry> sessions = new ConcurrentDictionary<uint, Entry>();

        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static double nextTickAt;

        /// <summary>Seconds on the manager's monotonic clock (the clock every session method is given).</summary>
        public static double Now() => clock.Elapsed.TotalSeconds;

        public static int SessionCount => sessions.Count;

        /// <summary>Test seam: the live landblock of an instance, or null when it is gone.</summary>
        internal static Func<uint, Landblock> LandblockLookup = LandblockManager.GetEphemeralLandblock;

        /// <summary>Test seam: the online player for a guid.</summary>
        internal static Func<uint, Player> OnlinePlayer = PlayerManager.GetOnlinePlayer;

        // ================================================================ registry

        /// <summary>Registers <paramref name="session"/> for its room landblock. A session already registered for the same player is ended and replaced.</summary>
        public static void Register(TrialSession session, Landblock landblock)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(landblock);

            Add(session, session.World, landblock, landblock.Instance, seenLive: false);
        }

        /// <summary>Test seam: registers a session against an instance with no landblock object (the lookup seam answers for it).</summary>
        internal static void RegisterForTests(TrialSession session, uint instance, bool seenLive)
            => Add(session, session.World, null, instance, seenLive);

        /// <summary>Test seam: empties the registry and resets the tick throttle and the seams.</summary>
        internal static void ResetForTests()
        {
            sessions.Clear();
            nextTickAt = 0;
            LandblockLookup = LandblockManager.GetEphemeralLandblock;
            OnlinePlayer = PlayerManager.GetOnlinePlayer;
        }

        private static void Add(TrialSession session, ITrialWorld world, Landblock landblock, uint instance, bool seenLive)
        {
            foreach (var other in sessions.Values)
            {
                if (other.Session.PlayerGuid == session.PlayerGuid && other.Instance != instance)
                    EndSession(other.Session, "the player started another trial");
            }

            if (sessions.TryGetValue(instance, out var previous) && !ReferenceEquals(previous.Session, session))
                EndSession(previous.Session, "instance re-registered");

            sessions[instance] = new Entry
            {
                Session = session,
                World = world,
                Landblock = landblock,
                Instance = instance,
                RegisteredAt = Now(),
                SeenLive = seenLive,
            };
        }

        public static bool TryGetByPlayer(uint guid, out TrialSession session)
        {
            foreach (var e in sessions.Values)
            {
                if (e.Session.PlayerGuid == guid)
                {
                    session = e.Session;
                    return true;
                }
            }

            session = null;
            return false;
        }

        /// <summary>
        /// True when any registered session, in any phase, runs an archetype of <paramref name="slot"/>. Such a session reads that
        /// slot's master (its digest and its consumables), so @trial promote and unpromote refuse while one exists.
        /// </summary>
        internal static bool AnySessionForSlot(int slot)
        {
            foreach (var e in sessions.Values)
            {
                if (e.Session.Archetype != null && e.Session.Archetype.Slot == slot)
                    return true;
            }

            return false;
        }

        public static bool TryGetByInstance(uint instance, out TrialSession session)
        {
            if (instance != 0 && sessions.TryGetValue(instance, out var e))
            {
                session = e.Session;
                return true;
            }

            session = null;
            return false;
        }

        /// <summary>The room instance <paramref name="session"/> is registered for.</summary>
        public static bool TryGetInstance(TrialSession session, out uint instance)
        {
            var e = Find(session);
            instance = e?.Instance ?? 0;
            return e != null;
        }

        /// <summary>The last vital sample taken for <paramref name="session"/>'s player, or null.</summary>
        public static VitalSample LastVitals(TrialSession session)
        {
            var e = Find(session);
            return e == null ? null : Volatile.Read(ref e.LastVitals);
        }

        private static Entry Find(TrialSession session)
        {
            if (session == null)
                return null;

            foreach (var e in sessions.Values)
            {
                if (ReferenceEquals(e.Session, session))
                    return e;
            }

            return null;
        }

        /// <summary>
        /// Removes <paramref name="session"/> from the registry and unbinds its player (when the online player is still bound to
        /// it). Does not end the session: callers end it first. When the player is no longer in the room, its
        /// EphemeralRealmExitTo stamp is cleared on the player's own thread (Player.ClearTrialExitStampIfLeft, which re-checks
        /// that the player is outside the room and bound to no other trial when it runs).
        /// </summary>
        public static void Remove(TrialSession session)
        {
            var e = Find(session);

            if (e != null)
                sessions.TryRemove(new KeyValuePair<uint, Entry>(e.Instance, e));

            try
            {
                var player = session == null ? null : OnlinePlayer(session.PlayerGuid);

                if (player != null && ReferenceEquals(player.TrialSession, session))
                {
                    player.TrialSession = null;
                    player.TrialEntryPending = false;
                }

                if (player != null && e != null)
                {
                    var instance = e.Instance;
                    var chain = new ActionChain();
                    chain.AddAction(player, () => player.ClearTrialExitStampIfLeft(instance));
                    chain.EnqueueChain();
                }
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] unbinding the player of trial {session?.TrialUuid} threw", ex);
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on <paramref name="session"/>'s room landblock thread. False (nothing queued) when the
        /// session is not registered or its room is gone. Every session method a command calls (TryNext, Abort) goes through here.
        /// </summary>
        public static bool Enqueue(TrialSession session, Action action)
        {
            var e = Find(session);
            var lb = e == null ? null : (e.Landblock ?? LandblockLookup(e.Instance));

            if (lb == null || action == null)
                return false;

            lb.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error($"[TRIAL] trial {session.TrialUuid} action threw", ex);
                }
            }));

            return true;
        }

        /// <summary>
        /// Ends <paramref name="session"/> (Abort: a live rep ends forfeit and its creatures are destroyed; a player who has
        /// left the room is never pulled back) and removes it. On the room's thread when the room is live, otherwise here.
        /// </summary>
        public static void EndSession(TrialSession session, string reason)
        {
            if (session == null)
                return;

            void End()
            {
                try
                {
                    session.Abort(reason, Now());
                }
                catch (Exception ex)
                {
                    log.Error($"[TRIAL] ending trial {session.TrialUuid} ({reason}) threw", ex);
                }

                Remove(session);
            }

            if (!Enqueue(session, End))
                End();
        }

        // ================================================================ hooks

        /// <summary>
        /// Creature.Die, after the Threads hook (so after OnDeath_GrantXP): a trial creature died. Advances the session that
        /// owns the room on the room's thread. A creature of an earlier rep (its P_TrialRep is not the live rep) is ignored.
        /// </summary>
        public static void OnTrialCreatureDied(Creature creature)
        {
            try
            {
                var rep = creature?.P_TrialRep;
                var instance = creature?.Location?.Instance ?? 0;

                if (rep == null || !TryGetByInstance(instance, out var session))
                    return;

                var guid = creature.Guid.Full;

                // The kill event's award was captured by OnDeath_GrantXP (TrialHooks.OnKillAward), which runs before Die.
                var wcid = creature.WeenieClassId;
                var role = creature.P_TrialRole;
                var xp = creature.P_TrialKillXp;
                var lum = creature.P_TrialKillLum;

                Enqueue(session, () => TrialHooks.RecordKillAndAdvance(session, rep, guid, wcid, role, xp, lum, Now()));
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnTrialCreatureDied threw for 0x{creature?.Guid.Full:X8}", ex);
            }
        }

        /// <summary>Player.TryBeginTrialDeath: the bound player died in the room. A live rep ends died, on the room's thread.</summary>
        public static void OnPlayerDied(Player player)
        {
            try
            {
                var session = player?.TrialSession;

                if (session == null || Find(session) == null)
                    return;

                Enqueue(session, () => session.OnPlayerDied(Now()));
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] OnPlayerDied threw for 0x{player?.Guid.Full:X8}", ex);
            }
        }

        /// <summary>Where a trial death respawns: the room start of the player's session, in its instance. Null when the player has no live session.</summary>
        public static Position StartPositionFor(Player player)
        {
            var session = player?.TrialSession;
            var e = Find(session);

            if (e == null || session.Room?.Start == null)
                return null;

            return LiveTrialWorld.StartPosition(session.Room, e.Instance);
        }

        // ================================================================ tick (world thread)

        public static void Tick()
        {
            var now = Now();

            if (now < nextTickAt)
                return;

            nextTickAt = now + TickSeconds;

            TickAt(now);
        }

        /// <summary>One sweep at <paramref name="now"/>, without the throttle (the test entry point).</summary>
        internal static void TickAt(double now)
        {
            // The run-row safety net: a deferred row the player's queue never wrote (TrialRunLog's class summary).
            try
            {
                TrialRunLog.SweepOverdue(now);
            }
            catch (Exception ex)
            {
                log.Error("[TRIAL] the run-row sweep threw", ex);
            }

            foreach (var entry in sessions.Values)
            {
                var session = entry.Session;

                try
                {
                    var live = LandblockLookup(entry.Instance);

                    if (live != null && (entry.Landblock == null || ReferenceEquals(live, entry.Landblock)))
                    {
                        entry.SeenLive = true;
                    }
                    else if (entry.SeenLive || now - entry.RegisteredAt > ArrivalGraceSeconds)
                    {
                        // World thread: the room is gone, so its creatures went with it and there is no thread to enqueue onto.
                        session.Abort("room landblock gone", now);
                        Remove(session);
                        log.Info($"[TRIAL] trial {session.TrialUuid} ended: room instance 0x{entry.Instance:X8} is gone");
                        continue;
                    }
                    else
                    {
                        continue;
                    }

                    if (Interlocked.CompareExchange(ref entry.TickQueued, 1, 0) != 0)
                        continue;

                    var tickAt = now;

                    live.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        try
                        {
                            TickSession(entry, tickAt);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"[TRIAL] trial {session.TrialUuid} tick threw", ex);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref entry.TickQueued, 0);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref entry.TickQueued, 0);
                    log.Error($"[TRIAL] trial {session?.TrialUuid} manager tick threw", ex);
                }
            }
        }

        /// <summary>Test seam: runs the room-thread half of a tick for the session registered on <paramref name="instance"/>, inline.</summary>
        internal static void TickSessionForTests(uint instance, double now)
        {
            if (sessions.TryGetValue(instance, out var entry))
                TickSession(entry, now);
        }

        /// <summary>The room-thread half of a tick: the session's own tick, the vital sample, then the player-gone sweep.</summary>
        private static void TickSession(Entry entry, double now)
        {
            var session = entry.Session;
            var world = entry.World;

            session.Tick(now);

            SampleVitals(entry, now);

            if (world == null)
                return;

            var online = world.PlayerOnline;
            var inRoom = online && world.PlayerInRoom;

            if (inRoom)
            {
                entry.SeenPlayer = true;
                return;
            }

            // Before the player has ever arrived (the entry teleport is in flight) only going offline ends the session.
            if (!online || entry.SeenPlayer || now - entry.RegisteredAt > ArrivalGraceSeconds)
            {
                session.Abort(online ? "player left the room" : "player went offline", now);
                Remove(session);
            }
        }

        /// <summary>
        /// Reads the bound player's vitals as fractions of their maximums, keeps it as the session's last sample and hands it to
        /// the running rep's recorder (TrialHooks.OnVitalSample -> TrialRecorder.OnVitals).
        /// </summary>
        private static void SampleVitals(Entry entry, double now)
        {
            if (entry.Session.Phase != TrialPhase.Running)
                return;

            var player = OnlinePlayer(entry.Session.PlayerGuid);

            if (player == null || !ReferenceEquals(player.TrialSession, entry.Session))
                return;

            static double Frac(uint cur, uint max) => max == 0 ? 0 : (double)cur / max;

            var sample = new VitalSample(
                Frac(player.Health.Current, player.Health.MaxValue),
                Frac(player.Stamina.Current, player.Stamina.MaxValue),
                Frac(player.Mana.Current, player.Mana.MaxValue),
                now);

            Volatile.Write(ref entry.LastVitals, sample);
            TrialHooks.OnVitalSample(entry.Session, sample);
        }
    }
}
