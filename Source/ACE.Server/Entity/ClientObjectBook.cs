using System;

using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Entity
{
    /// <summary>
    /// One player's client-object bookkeeping for the teleport flush switch (teleport_flush_scope): the
    /// <see cref="ClientObjectLedger"/> of which landblock copies the client holds objects from, and the
    /// <see cref="TeleportDeleteQueue{T}"/> of paced flush deletes still waiting.
    ///
    /// Fed by both NetworkSession.EnqueueSend loops through <see cref="BeforeEnqueue"/>, which the caller runs and
    /// follows with the bundle enqueue inside one <c>lock (SyncRoot)</c>, so "cancel the waiting delete, then
    /// enqueue the create" is atomic against a drain on another thread. BOOKKEEPING ONLY: nothing here sends, holds
    /// or reorders a message, which is what keeps the default (scope 1, pacing off) byte-for-byte unchanged.
    ///
    /// Lock rules: SyncRoot is a re-entrant Monitor taken BEFORE the bundle lock, never after; code holding it must
    /// never touch ObjectMaint (whose locks are held around TrackObject, which ends in EnqueueSend).
    /// </summary>
    public sealed class ClientObjectBook
    {
        public object SyncRoot { get; } = new object();

        /// <summary>The player this book belongs to: its own guid, and anything it wields, is never filed.</summary>
        public uint OwnerGuid { get; }

        public ClientObjectLedger Ledger { get; }

        public TeleportDeleteQueue<GameMessage> PendingDeletes { get; } = new TeleportDeleteQueue<GameMessage>();

        private volatile bool drainCycleOpen;

        /// <summary>Lock-free: true while a drain cycle is open, so the player tick skips the lock otherwise.</summary>
        public bool DrainCycleOpen => drainCycleOpen;

        public ClientObjectBook(uint ownerGuid, int ledgerCapacity = ClientObjectLedger.DefaultCapacity)
        {
            OwnerGuid = ownerGuid;
            Ledger = new ClientObjectLedger(ledgerCapacity);
        }

        /// <summary>
        /// True for the three opcodes the book follows. Every other message skips the book (and its lock) entirely.
        /// </summary>
        public static bool Follows(GameMessage message)
        {
            var opcode = message.Opcode;

            return opcode == GameMessageOpcode.ObjectCreate || opcode == GameMessageOpcode.ObjectDelete || opcode == GameMessageOpcode.PickupEvent;
        }

        /// <summary>
        /// The whole hook both NetworkSession.EnqueueSend loops run for a followed message: under SyncRoot, the
        /// bookkeeping, then the bundle enqueue. The bookkeeping is isolated: if it throws, <paramref name="onBookFailure"/>
        /// hears about it and the message is STILL enqueued, so a bookkeeping bug can never drop or delay a message.
        /// The callers pass cached delegates, so this allocates nothing per message.
        /// </summary>
        public static void EnqueueFollowed(ClientObjectBook book, GameMessage message, Action<GameMessage> enqueue, Action<Exception> onBookFailure)
        {
            lock (book.SyncRoot)
            {
                try
                {
                    book.BeforeEnqueue(message);
                }
                catch (Exception ex)
                {
                    onBookFailure?.Invoke(ex);
                }

                enqueue(message);
            }
        }

        /// <summary>Tests only: runs first in <see cref="BeforeEnqueue"/>, so a test can make the bookkeeping throw.</summary>
        internal Action<GameMessage> BookkeepingFaultForTests;

        /// <summary>
        /// Caller holds SyncRoot. CreateObject: file (or unfile) the guid, then cancel any waiting delete for it.
        /// DeleteObject / PickupEvent: unfile it, and drop a waiting delete for it, which this message supersedes.
        /// </summary>
        public void BeforeEnqueue(GameMessage message)
        {
            BookkeepingFaultForTests?.Invoke(message);

            switch (message)
            {
                case GameMessageCreateObject create:
                    NoteCreate(create.ObjectId, create.WielderId, create.HasPlacement, create.PlacementLandblock, create.PlacementInstance, create.InstanceSequence);
                    break;

                case GameMessageDeleteObject delete:
                    NoteRemove(delete.ObjectId);
                    break;

                case GameMessagePickupEvent pickup:
                    NoteRemove(pickup.ObjectId);
                    break;
            }
        }

        /// <summary>
        /// Caller holds SyncRoot. A CreateObject is going to this client: file the guid where the object stands, unless
        /// it is the owner itself, something the owner wields, or has no placement (contained), in which case any
        /// older filing is dropped. Then cancel a waiting delete for the guid: the client must keep what it was just sent.
        /// </summary>
        public void NoteCreate(uint guid, uint wielderGuid, bool hasPlacement, ushort landblock, uint instance, byte[] instanceSequence)
        {
            if (hasPlacement && guid != OwnerGuid && wielderGuid != OwnerGuid)
                Ledger.Record(guid, landblock, instance, instanceSequence);
            else
                Ledger.Remove(guid);

            PendingDeletes.Cancel(guid);
        }

        /// <summary>
        /// Caller holds SyncRoot. A DeleteObject or PickupEvent is going to this client: unfile the guid, and drop a
        /// waiting delete for it, which this message supersedes.
        /// </summary>
        public void NoteRemove(uint guid)
        {
            Ledger.Remove(guid);
            PendingDeletes.Cancel(guid);
        }

        /// <summary>Caller holds SyncRoot. Queues one paced flush delete and opens the drain cycle.</summary>
        public void Defer(uint guid, ushort landblock, uint instance, GameMessage delete, long nowTicks)
        {
            PendingDeletes.Defer(guid, landblock, instance, delete, nowTicks);
            drainCycleOpen = true;
        }

        /// <summary>Caller holds SyncRoot. Counts a teleport that deferred deletes against the open cycle.</summary>
        public void NoteTeleport(long nowTicks)
        {
            PendingDeletes.NoteTeleport(nowTicks);
            drainCycleOpen = true;
        }

        /// <summary>Caller holds SyncRoot. Closes the drain cycle and returns its totals.</summary>
        public TeleportDeleteCycle EndCycle()
        {
            drainCycleOpen = false;
            return PendingDeletes.EndCycle();
        }
    }
}
