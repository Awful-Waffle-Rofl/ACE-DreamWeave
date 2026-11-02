using System;
using System.Collections.Generic;

using ACE.Server.Entity.Actions;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.Knots
{
    /// <summary>
    /// Knot bookkeeping an encounter could not deliver because the owner was offline when the attempt ended (review
    /// 2026-10-19): an entry refund (a win whose reward was never placed), a loss report (which consumes a Knot left
    /// at zero entries) or a boss-kill report (which consumes the Knot: dropped, it would leave the winner a free
    /// extra fight). <see cref="KnotEncounterEvents"/> needs the owner's thread and a live session, so the event is
    /// recorded here, keyed by owner guid and Knot guid, and applied the next time that owner is online: on their next
    /// Knot use (<see cref="KnotSeam.TryHandleUse"/>, before anything is read from the Knot) and a few seconds after
    /// login (<see cref="ScheduleApplyOnLogin"/>), whichever comes first. Each event is applied once.
    ///
    /// IN MEMORY ONLY: a server restart before the owner returns loses what is pending here. Accepted (review
    /// 2026-10-19); the seam's own zero-entry clean-up still removes a dead Knot on its next use, so a lost loss
    /// report strands nothing, and a lost refund costs at most one entry. A lost kill report leaves the Knot with
    /// the winner until its entries run out.
    /// </summary>
    public static class KnotPendingEvents
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public enum Kind
        {
            /// <summary><see cref="KnotEncounterEvents.RefundEntry"/>.</summary>
            Refund,

            /// <summary><see cref="KnotEncounterEvents.OnAttemptLost"/>.</summary>
            AttemptLost,

            /// <summary><see cref="KnotEncounterEvents.OnBossKilled"/> (consumes the Knot).</summary>
            BossKilled,
        }

        /// <summary>Seconds after entering the world before pending events are applied (the same delay the owed-Blood and PvP streak resets use).</summary>
        private const float LoginDelaySeconds = 5.0f;

        private static readonly object Gate = new object();

        private static readonly Dictionary<uint, List<(uint KnotGuid, Kind Kind)>> byOwner = new Dictionary<uint, List<(uint, Kind)>>();

        /// <summary>Records one event for the owner's Knot. Any thread.</summary>
        public static void Record(uint ownerGuid, uint knotGuid, Kind kind)
        {
            lock (Gate)
            {
                if (!byOwner.TryGetValue(ownerGuid, out var list))
                    byOwner[ownerGuid] = list = new List<(uint, Kind)>();

                list.Add((knotGuid, kind));
            }

            log.Info($"[KNOTS] owner 0x{ownerGuid:X8} is offline: {kind} for Knot 0x{knotGuid:X8} is held until they return");
        }

        /// <summary>The owner's pending events in the order they were recorded, without removing them. Any thread.</summary>
        internal static IReadOnlyList<(uint KnotGuid, Kind Kind)> Peek(uint ownerGuid)
        {
            lock (Gate)
                return byOwner.TryGetValue(ownerGuid, out var list) ? list.ToArray() : Array.Empty<(uint, Kind)>();
        }

        /// <summary>Removes and returns the owner's pending events, in recorded order. Any thread.</summary>
        internal static IReadOnlyList<(uint KnotGuid, Kind Kind)> Take(uint ownerGuid)
        {
            lock (Gate)
            {
                if (!byOwner.TryGetValue(ownerGuid, out var list))
                    return Array.Empty<(uint, Kind)>();

                byOwner.Remove(ownerGuid);
                return list;
            }
        }

        /// <summary>Test seam: forgets everything.</summary>
        internal static void ClearForTest()
        {
            lock (Gate)
                byOwner.Clear();
        }

        /// <summary>
        /// Applies and forgets the owner's pending events, in recorded order, and returns how many were applied. OWNER's
        /// thread with a live session; without a session nothing is taken (the events wait for the next chance). A Knot
        /// that has left the inventory meanwhile makes its event a logged no-op inside KnotEncounterEvents.
        /// </summary>
        public static int Apply(Player owner)
        {
            if (owner?.Session == null)
                return 0;

            return ApplyTaken(owner, Take(owner.Guid.Full));
        }

        /// <summary>
        /// The login race (review F1): an event recorded just as its owner came online, after their login check found
        /// nothing pending. The caller has already taken it (<see cref="Take"/>); this queues it on the online owner's
        /// own chain. An owner who has gone again gets it recorded back, so nothing is lost.
        /// </summary>
        public static void DeliverToOnline(uint ownerGuid, IReadOnlyList<(uint KnotGuid, Kind Kind)> due)
        {
            if (due == null || due.Count == 0)
                return;

            var owner = ACE.Server.Managers.PlayerManager.GetOnlinePlayer(ownerGuid);

            if (owner == null)
            {
                RecordAll(ownerGuid, due);
                return;
            }

            owner.EnqueueAction(new ActionEventDelegate(() => ApplyTaken(owner, due)));
        }

        private static void RecordAll(uint ownerGuid, IReadOnlyList<(uint KnotGuid, Kind Kind)> due)
        {
            foreach (var (knotGuid, kind) in due)
                Record(ownerGuid, knotGuid, kind);
        }

        /// <summary>Applies events already taken from the store, in order. OWNER's thread; without a session they are recorded back.</summary>
        private static int ApplyTaken(Player owner, IReadOnlyList<(uint KnotGuid, Kind Kind)> due)
        {
            if (owner?.Session == null)
            {
                if (owner != null)
                    RecordAll(owner.Guid.Full, due);

                return 0;
            }

            foreach (var (knotGuid, kind) in due)
            {
                try
                {
                    switch (kind)
                    {
                        case Kind.Refund:
                            KnotEncounterEvents.RefundEntry(owner, knotGuid);
                            break;

                        case Kind.BossKilled:
                            KnotEncounterEvents.OnBossKilled(owner, knotGuid);
                            break;

                        default:
                            KnotEncounterEvents.OnAttemptLost(owner, knotGuid);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[KNOTS] applying a pending {kind} for Knot 0x{knotGuid:X8} threw for {owner.Name}", ex);
                }
            }

            if (due.Count > 0)
                log.Info($"[KNOTS] applied {due.Count} pending Knot event(s) for {owner.Name}");

            return due.Count;
        }

        /// <summary>Login (WorldManager): queues <see cref="Apply"/> on the player's own chain a few seconds in. A no-op when nothing is pending.</summary>
        public static void ScheduleApplyOnLogin(Player player)
        {
            if (player == null || Peek(player.Guid.Full).Count == 0)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(LoginDelaySeconds);
            chain.AddAction(player, () => Apply(player));
            chain.EnqueueChain();
        }
    }
}
