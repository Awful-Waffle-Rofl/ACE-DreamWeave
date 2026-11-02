using System;
using System.Collections.Generic;

using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Entity
{
    /// <summary>Places an object for the paced flush: false when it has no placement (the caller's fallback is used).</summary>
    public delegate bool TeleportFlushPlacement<T>(T knownObject, out ushort landblock, out uint instance);

    /// <summary>
    /// The loop bodies of the teleport flush arms that touch the ClientObjectBook, as static methods over delegates,
    /// so tests run the production code rather than a copy of it. Player_TeleportFlush supplies the WorldObject side
    /// (ownership, tracking cuts, effect resets, the session send).
    ///
    /// Each method takes SyncRoot itself where it touches the book. Nothing here touches ObjectMaint.
    /// </summary>
    public static class TeleportFlushSteps
    {
        /// <summary>
        /// <paramref name="owns"/> made fail-safe: a throw counts as owned, so the delete is skipped. The owns check
        /// reads the player's inventory, which can be reached from another thread (TrackObject in the physics phase),
        /// where enumerating it can throw "Collection was modified". Skipping one delete is harmless; a throw that
        /// escaped would lose the deletes already taken from the ledger and the create that follows.
        /// </summary>
        public static bool OwnsFailSafe(Func<uint, bool> owns, uint guid)
        {
            try
            {
                return owns(guid);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// The client half for leftover observers already cut server-side (ObjectMaint.CutResidualObservers): each one
        /// whose client still draws the teleporter (Value true) is sent a delete when <paramref name="sendDeletes"/>
        /// (TeleportFlushPolicy.ResidualObserverDeletes), and otherwise only has its replayed-effect bookkeeping
        /// forgotten, as scope 0's silent purge does for ordinary observers. Returns how many deletes were sent.
        /// </summary>
        public static int ApplyResidualObservers<T>(IReadOnlyList<KeyValuePair<T, bool>> cut, bool sendDeletes, Action<T> delete, Action<T> forget)
        {
            var deletes = 0;

            for (var i = 0; i < cut.Count; i++)
            {
                if (!cut[i].Value)
                    continue;

                if (sendDeletes)
                {
                    delete(cut[i].Key);
                    deletes++;
                }
                else
                    forget(cut[i].Key);
            }

            return deletes;
        }

        /// <summary>Removes and sends every waiting paced delete that collides with the destination 3x3. Returns how many.</summary>
        public static int SendCollidingPending(ClientObjectBook book, IReadOnlyList<ushort> neighbourhood, uint destinationInstance, Action<GameMessage> send)
        {
            lock (book.SyncRoot)
            {
                var pulled = book.PendingDeletes.TakeColliding(neighbourhood, destinationInstance);

                foreach (var delete in pulled)
                    send(delete);

                return pulled.Count;
            }
        }

        /// <summary>
        /// Scope 2: takes everything the ledger holds from another copy of <paramref name="landblocks"/> and sends a
        /// delete for each, skipping what <paramref name="owns"/> claims (fail-safe). Never throws out of an owns
        /// check. Returns how many deletes were sent.
        /// </summary>
        public static int FireLedger(ClientObjectBook book, IReadOnlyList<ushort> landblocks, uint instance, Func<uint, bool> owns, Action<GameMessage> send, Action<uint> forget)
        {
            var fired = 0;

            lock (book.SyncRoot)
            {
                foreach (var stale in book.Ledger.TakeOtherCopies(landblocks, instance))
                {
                    if (OwnsFailSafe(owns, stale.Guid))
                        continue;

                    send(new GameMessageDeleteObject(stale.Guid, stale.InstanceSequence));
                    forget?.Invoke(stale.Guid);
                    fired++;
                }
            }

            return fired;
        }

        /// <summary>
        /// Scope 1 with pacing on, over the known objects in the legacy flush's order. Waiting deletes that collide
        /// with the destination go first. Then per object: a collision takes <paramref name="legacyRemove"/> (the
        /// legacy per-object body, at once); anything else has its deletes queued under SyncRoot BEFORE
        /// <paramref name="cutTracking"/> runs, so a create racing in after the cut always finds and cancels its
        /// delete. A teleport that deferred anything is counted against the drain cycle.
        /// </summary>
        public static void DeferredFlush<T>(ClientObjectBook book, IEnumerable<T> knownObjects, IReadOnlyList<ushort> destinationNeighbourhood, uint destinationInstance,
            ushort fallbackLandblock, uint fallbackInstance, long nowTicks,
            TeleportFlushPlacement<T> placement, Func<T, IEnumerable<KeyValuePair<uint, GameMessage>>> buildDeletes,
            Action<T> legacyRemove, Action<T> cutTracking, Action<GameMessage> send,
            out int deletedNow, out int deferred)
        {
            deletedNow = 0;
            deferred = 0;

            SendCollidingPending(book, destinationNeighbourhood, destinationInstance, send);

            foreach (var knownObject in knownObjects)
            {
                if (!placement(knownObject, out var landblock, out var instance))
                {
                    landblock = fallbackLandblock;
                    instance = fallbackInstance;
                }

                if (TeleportFlushPolicy.Collides(landblock, instance, destinationNeighbourhood, destinationInstance))
                {
                    legacyRemove(knownObject);
                    deletedNow++;
                    continue;
                }

                lock (book.SyncRoot)
                {
                    foreach (var delete in buildDeletes(knownObject))
                        book.Defer(delete.Key, landblock, instance, delete.Value, nowTicks);
                }

                cutTracking(knownObject);
                deferred++;
            }

            if (deferred > 0)
            {
                lock (book.SyncRoot)
                    book.NoteTeleport(nowTicks);
            }
        }

        /// <summary>
        /// One player tick of the paced-delete drain. Pacing off releases everything at once, even mid-teleport;
        /// pacing on waits while <paramref name="teleporting"/>, then releases <paramref name="rate"/>. Returns the
        /// [TELEPORT_DRAIN] end reason ("pacing-off" or "complete") when the cycle ended this tick, with its totals in
        /// <paramref name="ended"/>; null while it is still running (or none is open).
        /// </summary>
        public static string Drain(ClientObjectBook book, bool pacing, bool teleporting, int rate, Action<GameMessage> send, out TeleportDeleteCycle ended)
        {
            ended = default;

            lock (book.SyncRoot)
            {
                if (!book.PendingDeletes.CycleOpen)
                    return null;

                string end;

                if (!pacing)
                {
                    foreach (var delete in book.PendingDeletes.TakeAll())
                        send(delete);

                    end = "pacing-off";
                }
                else
                {
                    if (teleporting)
                        return null;

                    foreach (var delete in book.PendingDeletes.Take(rate))
                        send(delete);

                    if (book.PendingDeletes.Count > 0)
                        return null;

                    end = "complete";
                }

                ended = book.EndCycle();
                return end;
            }
        }

        /// <summary>Logout: drops every waiting delete unsent and ends the cycle. False when no cycle was open.</summary>
        public static bool Discard(ClientObjectBook book, out TeleportDeleteCycle ended)
        {
            ended = default;

            lock (book.SyncRoot)
            {
                if (!book.PendingDeletes.CycleOpen)
                    return false;

                book.PendingDeletes.Discard();
                ended = book.EndCycle();
                return true;
            }
        }
    }
}
