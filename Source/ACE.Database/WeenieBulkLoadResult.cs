namespace ACE.Database
{
    /// <summary>
    /// What one <see cref="WorldDatabaseWithEntityCache.PrefetchWeenies"/> call did. Callers take their summary counts
    /// from here, never from a before/after read of the cache size, which moves under every other thread's misses.
    /// </summary>
    public class WeenieBulkLoadResult
    {
        /// <summary>Distinct non-zero wcids asked for.</summary>
        public int Requested { get; set; }

        /// <summary>Of <see cref="Requested"/>, already in the cache (as a weenie or as a cached miss) when the call began.</summary>
        public int AlreadyCached { get; set; }

        /// <summary>Weenies this call published into the cache.</summary>
        public int Loaded { get; set; }

        /// <summary>Wcids with no weenie row, published as cached misses (null), exactly as the per-id path does.</summary>
        public int NegativelyCached { get; set; }

        /// <summary>Wcids another thread cached between this call's read and its publish; the existing instance was kept.</summary>
        public int LostRace { get; set; }

        /// <summary>Chunks that reached the database.</summary>
        public int ChunksAttempted { get; set; }

        /// <summary>Chunks whose contents were published.</summary>
        public int ChunksPublished { get; set; }

        /// <summary>Chunks dropped because the cache was cleared while they were being read.</summary>
        public int ChunksStale { get; set; }

        /// <summary>Chunks dropped because a self-checked weenie differed from its legacy per-id read.</summary>
        public int ChunksRejected { get; set; }

        /// <summary>Chunks dropped because reading them threw.</summary>
        public int ChunksFailed { get; set; }

        /// <summary>
        /// Chunks whose publish threw. Wcids added before the throw stay cached (each passed the self-check, and TryAdd
        /// never replaces); the rest load lazily.
        /// </summary>
        public int ChunksPublishFailed { get; set; }

        /// <summary>Times the bulk gate could not be entered within the caller's wait. The call stops at the first one.</summary>
        public int GateTimeouts { get; set; }

        /// <summary>Wcids never attempted because the gate timed out; they fall back to the lazy per-id path.</summary>
        public int IdsSkipped { get; set; }

        /// <summary>Weenies compared against a legacy per-id read before publishing.</summary>
        public int SelfChecked { get; set; }

        /// <summary>Self-checked weenies that differed. Each one rejects its whole chunk.</summary>
        public int SelfCheckMismatches { get; set; }

        /// <summary>The first mismatch seen, as "wcid N: member path: a vs b", or null.</summary>
        public string FirstMismatch { get; set; }

        /// <summary>True when weenie_bulk_load is off: the call did nothing, and callers fall through to their per-id loops.</summary>
        public bool Disabled { get; set; }

        /// <summary>Wall time of the whole call, gate waits included.</summary>
        public double ElapsedMs { get; set; }

        public override string ToString()
        {
            if (Disabled)
                return $"disabled (weenie_bulk_load off) requested={Requested}";

            return $"requested={Requested} cached={AlreadyCached} loaded={Loaded} missing={NegativelyCached} lostRace={LostRace} " +
                   $"chunks={ChunksAttempted} published={ChunksPublished} stale={ChunksStale} rejected={ChunksRejected} failed={ChunksFailed} publishFailed={ChunksPublishFailed} " +
                   $"gateTimeouts={GateTimeouts} skipped={IdsSkipped} checked={SelfChecked} mismatches={SelfCheckMismatches} ms={ElapsedMs:N0}";
        }
    }
}
