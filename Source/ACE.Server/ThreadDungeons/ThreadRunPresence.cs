using System;
using System.Collections.Generic;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The one definition of "who is in this run" (Group Threads invariants 1 and 5). Every presence question a
    /// group feature asks goes through here, so the inside test cannot drift between delivery, messaging, reaping
    /// and telemetry.
    ///
    /// The online-player lookup is injectable and defaults to PlayerManager.GetOnlinePlayer, so the pure parts
    /// can be exercised from the unit-test harness, where a Player cannot be constructed.
    /// </summary>
    public static class ThreadRunPresence
    {
        /// <summary>
        /// Invariant 1: <paramref name="player"/> is on the run's roster AND stands in the run's copy - the same
        /// instance and the dungeon's landblock. The location clause is ThreadCachePlacer.IsOwnerInside's body.
        /// A member the puzzle fail policy removed (ThreadDungeonRun.MarkPuzzleRemoved) is never inside, wherever they
        /// stand, so every reward and message this file feeds skips them from the moment of removal.
        /// </summary>
        public static bool IsMemberInside(ThreadDungeonRun run, Player player)
            => run?.Dungeon != null && player?.Location != null
               && run.IsRosterMember(player.Guid.Full)
               && !run.IsPuzzleRemoved(player.Guid.Full)
               && player.Location.Instance == run.Instance
               && player.Location.LandblockId.Landblock == run.Dungeon.Landblock;

        /// <summary>Online roster members who are inside, in roster order (invariant 4).</summary>
        public static List<Player> OnlineMembersInside(ThreadDungeonRun run, Func<uint, Player> onlinePlayer = null)
        {
            var inside = new List<Player>();
            if (run == null)
                return inside;

            if (onlinePlayer == null) onlinePlayer = PlayerManager.GetOnlinePlayer;

            foreach (var member in run.Roster)
            {
                var player = onlinePlayer(member.Guid);
                if (player != null && IsMemberInside(run, player))
                    inside.Add(player);
            }

            return inside;
        }

        /// <summary>
        /// Invariant 5. A SOLO run messages its online owner with no inside test, exactly as before Group Threads
        /// (ruling R30). A GROUP run messages the online members who are inside, in roster order.
        /// </summary>
        public static List<Player> MessageRecipients(ThreadDungeonRun run, Func<uint, Player> onlinePlayer = null)
        {
            if (run == null)
                return new List<Player>();

            if (onlinePlayer == null) onlinePlayer = PlayerManager.GetOnlinePlayer;

            if (!run.IsGroup)
            {
                var owner = onlinePlayer(run.OwnerGuid);
                return owner != null ? new List<Player> { owner } : new List<Player>();
            }

            return OnlineMembersInside(run, onlinePlayer);
        }

        /// <summary>A member flagged IsMemberDeliveryWanted who is online and inside right now.</summary>
        public static bool AnyWantedMemberInside(ThreadDungeonRun run, Func<uint, Player> onlinePlayer = null)
        {
            if (run == null)
                return false;

            if (onlinePlayer == null) onlinePlayer = PlayerManager.GetOnlinePlayer;

            foreach (var member in run.Roster)
            {
                if (!run.IsMemberDeliveryWanted(member.Guid))
                    continue;

                var player = onlinePlayer(member.Guid);
                if (player != null && IsMemberInside(run, player))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Ruling R29's reap guard, presence-independent: a GROUP run still owes loot while the shared pool is not
        /// fully dealt (ThreadDungeonRun.HasUndealtLoot: ledger entries, a pending boss bonus, or shared overflow), any
        /// placed cache still holds items, or any DEAL SEAT (ThreadDungeonRun.DealSeats: the owner and every keyed
        /// member, ruling R32) holds an undelivered pile or is flagged as waiting for delivery, online or not, inside
        /// or not. A keyless member cannot enter to collect, so their pile and flag hold nothing (final review F2).
        /// Always false for a solo run: a solo run is never held for loot (owner ruling, 2026-09-27 - its gem is
        /// destroyed at the clear, so nobody can re-enter it, and undelivered loot is forfeit at run end). How LONG
        /// a true answer holds a GROUP run is bounded by the empty-copy mechanism, not here: a held run's copy
        /// unloads once it has stood empty for the lock-time loot hold (ThreadDungeonRun.GroupLootHoldActive and
        /// ResolveUnloadInterval).
        ///
        /// The held-pile clause is independent of the flag on purpose. Ruling R20 does not flag a member who was
        /// inside when delivery hit NoRoom, so a member who then dies or walks out holds a pile with no flag; R29
        /// promises that pile survives until the run ends, so the pile itself, not the flag, is what is owed.
        /// </summary>
        public static bool GroupLootOwed(ThreadDungeonRun run)
        {
            if (run == null || !run.IsGroup)
                return false;

            return LootOwedCore(run);
        }

        private static bool LootOwedCore(ThreadDungeonRun run)
        {
            if (run.HasUndealtLoot)
                return true;

            // A formed cache that still holds items is owed too: a member who died before looting their cache must
            // not lose it to the reap (ruling R29 intent, Task 10 fix round 1). The hold's idle window (the copy's
            // unload override) is what stops an unlooted cache holding the copy to TTL.
            if (ThreadLootPool.AnyCacheHoldsItems(run))
                return true;

            foreach (var seat in run.DealSeats())
            {
                if (run.HasHeldPile(seat) || run.IsMemberDeliveryWanted(seat))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Should the Tick retry group delivery now? A GROUP run with a run-level placement wanted and undealt loot
        /// (ThreadDungeonRun.HasUndealtLoot), OR a waiting member who is online and inside. False for a solo run, which keeps
        /// its own retry path.
        /// </summary>
        public static bool GroupDeliveryWanted(ThreadDungeonRun run, Func<uint, Player> onlinePlayer = null)
        {
            if (run == null || !run.IsGroup)
                return false;

            // HasUndealtLoot, not just the ledger and the bonus latch: pooled boss rolls still to build, built items in
            // the deal buffers and trickle-prebuilt pieces are all loot a deal still has to hand out, and a chain cut at
            // its step cap with only those left must still be retried (found while adding the prebuilt store,
            // 2026-09-18; before it, pooled rolls and buffered items were missed by this retry).
            return (run.IsPlacementWanted && run.HasUndealtLoot)
                   || AnyWantedMemberInside(run, onlinePlayer);
        }
    }
}
