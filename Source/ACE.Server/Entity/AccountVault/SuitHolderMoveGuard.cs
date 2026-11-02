using System;
using System.Collections.Generic;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Mutual exclusion between a character ENTERING THE WORLD and the suit builder moving an item OUT of that
    /// character's pack while it is offline (SuitTransferService's holder lines). The one rule it enforces: a login's
    /// possession read and a move's item save are never both in flight for the same character.
    ///
    /// WHY IT EXISTS. A move reads the offline holder's possessions, builds the item, adds it to the online target and
    /// saves it with the target as its Container. A login of the holder reads the same possessions
    /// (WorldManager.PlayerEnterWorld, GetPossessedBiotasInParallel) and only becomes "online" (PlayerManager) once that
    /// read has come back and DoPlayerEnterWorld has run. If that login read were queued BEFORE the move's save, the
    /// holder would load the item with its old Container while the target also holds it: one guid, two live copies.
    /// The account's one-session rule does not close this on its own: a booted session is removed from NetworkManager
    /// while its player is still in the world logging out (Session.DropSession), so a new session for the same account
    /// can enter the world with another character before the old player has finished (Player.FinalizeLogout's own
    /// remark on "a logout that overlaps another character on the same account entering the world").
    ///
    /// HOW. CharacterHandler.CharacterEnterWorld calls <see cref="TryBeginEnterWorld"/> before it queues the login read,
    /// and the login ends its mark (<see cref="EndEnterWorld"/>) in WorldManager.PlayerEnterWorld, on its early return
    /// and in a finally around DoPlayerEnterWorld, after PlayerManager has switched the character online, so between the two marks the character is always visible as
    /// either "entering" or "online". A move calls <see cref="TryClaimForMove"/> BEFORE it checks the holder is offline
    /// and before it queues its own read, and releases the claim only after its item save has CALLED BACK. So either
    /// the login marked first (the move's claim fails), or the move claimed first (the login is refused until the save
    /// has run, and the shard queue is FIFO, so the next login read sees the item under the target).
    ///
    /// A move whose save could not be confirmed (it returned false, threw, or never called back) PINS the holder:
    /// that character cannot enter the world again until the server restarts, because the item may be live on the
    /// target while the database still names the holder. A late save callback that reports success unpins it.
    ///
    /// NOTHING HERE AGES OUT. A claim, an entering mark and a pin each hold until the code that took it lets go,
    /// because every age-out is a way for a login read and a move save to overlap again:
    ///  - an entering mark ends only in WorldManager.PlayerEnterWorld (its early return, or the finally around
    ///    DoPlayerEnterWorld). The login's possession read always calls back: SerializedShardDatabase answers a read
    ///    that throws with null (DoPlayerEnterWorld then aborts the login), so the finally always runs;
    ///  - a claim ends only through the transfer: a refused or confirmed unit releases it, an unconfirmed save turns it
    ///    into a pin, and a unit whose character's action queue stopped is closed by the world-heartbeat sweep
    ///    (SuitTransferService.SweepLive), which releases a reading or delivering unit's claim and pins a saving one;
    ///  - a pin ends only on a late save callback that reports success, or a restart.
    /// The timestamps are kept for one purpose: a refusal by a mark or claim held longer than
    /// <see cref="LongHeldWarningAge"/> is logged, so a leak is visible to an operator instead of silent.
    ///
    /// Process-local and in memory, like every other suit transfer rule. Never touches the database.
    /// </summary>
    internal static class SuitHolderMoveGuard
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>A refusal by a mark or claim held at least this long is logged as a warning. Never an expiry.</summary>
        public static readonly TimeSpan LongHeldWarningAge = TimeSpan.FromMinutes(2);

        /// <summary>The guard's clock, read only for <see cref="LongHeldWarningAge"/>. A test seam; production reads DateTime.UtcNow.</summary>
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;

        private static readonly object gate = new object();

        /// <summary>Characters whose login read is queued or running: a count in case two sessions race, and when the last one marked.</summary>
        private static readonly Dictionary<uint, (int Count, DateTime MarkedAt)> entering = new Dictionary<uint, (int, DateTime)>();

        /// <summary>Characters an item move is reading from or saving out of right now, with when it claimed.</summary>
        private static readonly Dictionary<uint, DateTime> moving = new Dictionary<uint, DateTime>();

        /// <summary>Characters whose last move could not be confirmed saved: login refused until restart.</summary>
        private static readonly HashSet<uint> pinned = new HashSet<uint>();

        /// <summary>
        /// The login side. False when an item move holds (or has pinned) this character: the caller refuses the login
        /// and queues nothing. True records the login as entering; the caller MUST pair it with <see cref="EndEnterWorld"/>.
        /// A claim refuses for as long as it is held, however old.
        /// </summary>
        public static bool TryBeginEnterWorld(uint characterGuid)
        {
            lock (gate)
            {
                var now = Clock();

                if (pinned.Contains(characterGuid))
                    return false;

                if (moving.TryGetValue(characterGuid, out var claimedAt))
                {
                    if (now - claimedAt >= LongHeldWarningAge)
                        log.Warn($"[SUIT] login of 0x{characterGuid:X8} refused by a suit builder move claim held since {claimedAt:u}; the transfer watchdog should have closed it.");

                    return false;
                }

                entering.TryGetValue(characterGuid, out var mark);
                entering[characterGuid] = (mark.Count + 1, now);

                return true;
            }
        }

        /// <summary>Ends one entering mark. Called once the character is online, or the login was abandoned.</summary>
        public static void EndEnterWorld(uint characterGuid)
        {
            lock (gate)
            {
                if (!entering.TryGetValue(characterGuid, out var mark))
                    return;

                if (mark.Count <= 1)
                    entering.Remove(characterGuid);
                else
                    entering[characterGuid] = (mark.Count - 1, mark.MarkedAt);
            }
        }

        /// <summary>
        /// The move side. False while the character is entering the world (any mark, however old), already being moved
        /// from, or pinned. True claims it; the caller then checks the character is offline and MUST later call
        /// <see cref="ReleaseMove"/> or <see cref="PinUnconfirmed"/>.
        /// </summary>
        public static bool TryClaimForMove(uint characterGuid)
        {
            lock (gate)
            {
                var now = Clock();

                if (entering.TryGetValue(characterGuid, out var mark))
                {
                    // A slow login is still a login: its possession read may yet return the item's old row.
                    if (now - mark.MarkedAt >= LongHeldWarningAge)
                        log.Warn($"[SUIT] move from 0x{characterGuid:X8} refused by a login entering mark held since {mark.MarkedAt:u}.");

                    return false;
                }

                if (moving.ContainsKey(characterGuid) || pinned.Contains(characterGuid))
                    return false;

                moving[characterGuid] = now;
                return true;
            }
        }

        /// <summary>Releases a claim (nothing was saved, or the save was confirmed). Also lifts a pin: a confirmed save ends the doubt.</summary>
        public static void ReleaseMove(uint characterGuid)
        {
            lock (gate)
            {
                moving.Remove(characterGuid);
                pinned.Remove(characterGuid);
            }
        }

        /// <summary>The move's save could not be confirmed: keep the character out of the world until a restart (or a late confirmed save).</summary>
        public static void PinUnconfirmed(uint characterGuid)
        {
            lock (gate)
            {
                moving.Remove(characterGuid);
                pinned.Add(characterGuid);
            }
        }

        internal static bool IsEnteringForTests(uint characterGuid)
        {
            lock (gate)
                return entering.ContainsKey(characterGuid);
        }

        internal static void ResetForTests()
        {
            lock (gate)
            {
                entering.Clear();
                moving.Clear();
                pinned.Clear();
            }

            Clock = () => DateTime.UtcNow;
        }
    }
}