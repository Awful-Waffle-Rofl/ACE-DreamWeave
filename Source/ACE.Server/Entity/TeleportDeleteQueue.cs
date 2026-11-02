using System.Collections.Generic;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Totals for one drain cycle of a <see cref="TeleportDeleteQueue{T}"/>: from the first delete deferred into an
    /// empty queue until the queue is empty again. Logging only ([TELEPORT_DRAIN]).
    /// </summary>
    public struct TeleportDeleteCycle
    {
        public int Sent;
        public int Cancelled;
        public int Deferred;
        public int Teleports;
        public long StartTicks;
    }

    /// <summary>
    /// The paced teleport flush's pending own-client deletes (teleport_flush_scope 1 with
    /// teleport_flush_delete_pacing on), one entry per deleted guid, in the order the legacy flush would have sent
    /// them.
    ///
    /// Re-deferring a guid replaces its entry; a CreateObject for a guid cancels it (the client must keep the object
    /// it was just sent). Cancelled and replaced entries stay in the FIFO as stale items and are skipped when they
    /// reach the front, so every operation is O(1) amortised except <see cref="TakeColliding"/>.
    ///
    /// Pure bookkeeping: no WorldObject references, no locking (ClientObjectBook.SyncRoot guards it), no sends.
    /// </summary>
    public sealed class TeleportDeleteQueue<T>
    {
        private sealed class Entry
        {
            public uint Guid;
            public ushort Landblock;
            public uint Instance;
            public T Payload;
        }

        private Queue<Entry> queue = new Queue<Entry>();
        private readonly Dictionary<uint, Entry> live = new Dictionary<uint, Entry>();
        private TeleportDeleteCycle cycle;

        /// <summary>Deletes still waiting.</summary>
        public int Count => live.Count;

        /// <summary>
        /// True from the first deferral until <see cref="EndCycle"/>. Can be true with <see cref="Count"/> 0, when
        /// every waiting delete was cancelled: the cycle still needs ending (and logging).
        /// </summary>
        public bool CycleOpen { get; private set; }

        public TeleportDeleteCycle Cycle => cycle;

        public bool IsPending(uint guid) => live.ContainsKey(guid);

        /// <summary>Counts one teleport against the open cycle, opening it if needed.</summary>
        public void NoteTeleport(long nowTicks)
        {
            OpenCycle(nowTicks);
            cycle.Teleports++;
        }

        public void Defer(uint guid, ushort landblock, uint instance, T payload, long nowTicks)
        {
            OpenCycle(nowTicks);

            var entry = new Entry { Guid = guid, Landblock = landblock, Instance = instance, Payload = payload };
            live[guid] = entry;
            queue.Enqueue(entry);
            cycle.Deferred++;
        }

        /// <summary>Drops the waiting delete for <paramref name="guid"/>, if any. True when one was dropped.</summary>
        public bool Cancel(uint guid)
        {
            if (!live.Remove(guid))
                return false;

            cycle.Cancelled++;

            if (live.Count == 0)
                queue.Clear();

            return true;
        }

        /// <summary>
        /// Removes and returns, in FIFO order, every waiting delete filed under one of <paramref name="landblockIds"/>
        /// from an instance other than <paramref name="instance"/>: a delete that must reach the client before a
        /// same-guid object of the destination copy can be created.
        /// </summary>
        public List<T> TakeColliding(IReadOnlyList<ushort> landblockIds, uint instance)
        {
            var taken = new List<T>();

            if (live.Count == 0)
                return taken;

            var kept = new Queue<Entry>(queue.Count);

            foreach (var entry in queue)
            {
                if (!live.TryGetValue(entry.Guid, out var current) || !ReferenceEquals(current, entry))
                    continue; // stale: cancelled or replaced

                if (entry.Instance != instance && Contains(landblockIds, entry.Landblock))
                {
                    live.Remove(entry.Guid);
                    taken.Add(entry.Payload);
                    cycle.Sent++;
                }
                else
                    kept.Enqueue(entry);
            }

            queue = kept;
            return taken;
        }

        /// <summary>Removes and returns up to <paramref name="max"/> waiting deletes in FIFO order (at least 1).</summary>
        public List<T> Take(int max)
        {
            if (max < 1)
                max = 1;

            var taken = new List<T>();

            while (taken.Count < max && queue.Count > 0)
            {
                var entry = queue.Dequeue();

                if (!live.TryGetValue(entry.Guid, out var current) || !ReferenceEquals(current, entry))
                    continue;

                live.Remove(entry.Guid);
                taken.Add(entry.Payload);
                cycle.Sent++;
            }

            if (live.Count == 0)
                queue.Clear();

            return taken;
        }

        /// <summary>Removes and returns every waiting delete in FIFO order.</summary>
        public List<T> TakeAll()
        {
            return Take(int.MaxValue);
        }

        /// <summary>Drops every waiting delete without returning it (logout). Returns how many were dropped.</summary>
        public int Discard()
        {
            var dropped = live.Count;

            live.Clear();
            queue.Clear();

            return dropped;
        }

        /// <summary>Closes the open cycle and returns its totals.</summary>
        public TeleportDeleteCycle EndCycle()
        {
            var ended = cycle;

            cycle = default;
            CycleOpen = false;

            return ended;
        }

        private void OpenCycle(long nowTicks)
        {
            if (CycleOpen)
                return;

            CycleOpen = true;
            cycle = default;
            cycle.StartTicks = nowTicks;
        }

        private static bool Contains(IReadOnlyList<ushort> landblockIds, ushort landblock)
        {
            for (var i = 0; i < landblockIds.Count; i++)
            {
                if (landblockIds[i] == landblock)
                    return true;
            }

            return false;
        }
    }
}
