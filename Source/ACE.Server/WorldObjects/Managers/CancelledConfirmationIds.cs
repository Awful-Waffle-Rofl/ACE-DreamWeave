using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.WorldObjects.Managers
{
    /// <summary>
    /// The confirmations ConfirmationManager.TryCancel closed, remembered so the client's automatic reply to each
    /// closed dialog can be recognised and dropped once, instead of reaching HandleResponse's "not found" /
    /// "contextId mismatch" error paths. Context ids come from one per-player increasing sequence, so a
    /// (type, contextId) pair names exactly one dialog and can never match a live one.
    ///
    /// Bounded: the oldest entry is forgotten past Capacity, so a client that never replies cannot grow it. Each
    /// entry is consumed by the first matching reply, so only that one reply is ever swallowed.
    /// </summary>
    public sealed class CancelledConfirmationIds
    {
        public const int DefaultCapacity = 8;

        private readonly int capacity;
        private readonly LinkedList<(ConfirmationType type, uint contextId)> entries = new LinkedList<(ConfirmationType, uint)>();
        private readonly object sync = new object();

        public CancelledConfirmationIds(int capacity = DefaultCapacity)
        {
            this.capacity = capacity < 1 ? 1 : capacity;
        }

        public int Count
        {
            get { lock (sync) return entries.Count; }
        }

        public void Remember(ConfirmationType type, uint contextId)
        {
            lock (sync)
            {
                entries.AddLast((type, contextId));

                while (entries.Count > capacity)
                    entries.RemoveFirst();
            }
        }

        /// <summary>True, and forgets the entry, when this exact (type, contextId) was cancelled and not yet consumed.</summary>
        public bool TryConsume(ConfirmationType type, uint contextId)
        {
            lock (sync)
            {
                for (var node = entries.First; node != null; node = node.Next)
                {
                    if (node.Value.type == type && node.Value.contextId == contextId)
                    {
                        entries.Remove(node);
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
