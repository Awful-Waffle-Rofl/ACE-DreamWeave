using System;
using System.Collections.Generic;
using System.Threading;

namespace ACE.Common.Cryptography
{
    /// <summary>
    /// The client-to-server checksum key stream. Every encrypted-checksum packet the client sends consumes the next
    /// ISAAC key, so a lost or reordered packet leaves keys the server has to skip over and remember.
    /// <para>
    /// The window is anchored on the newest key that actually verified (its ordinal, the key's position in the ISAAC
    /// stream): a search may generate keys up to <see cref="EffortLevel"/> + 1 positions past it, and a skipped key is
    /// remembered until it falls more than <see cref="EffortLevel"/> positions behind it. A key lost for good therefore
    /// ages out instead of permanently using up the look-ahead, which the original HashSet-only version did (its
    /// loop bound was EffortLevel minus every key ever skipped and never seen again).
    /// </para>
    /// <para>
    /// Not thread safe. One session's packets are verified on that session's connection-listener receive callback,
    /// which re-arms only after the packet is processed, so calls never overlap.
    /// </para>
    /// </summary>
    public class CryptoSystem : ISAAC
    {
        /// <summary>The window before it became configurable: 256 keys.</summary>
        public const int LegacyEffortLevel = 256;

        /// <summary>The shipped window when nothing configures one.</summary>
        public const int StandardEffortLevel = 4096;

        public const int MinEffortLevel = 256;
        public const int MaxEffortLevel = 16384;

        private static int defaultEffortLevel = StandardEffortLevel;

        /// <summary>
        /// The window given to a CryptoSystem constructed from now on (a live one keeps the value it was built with).
        /// Set by the server from its settings; clamped to <see cref="MinEffortLevel"/>..<see cref="MaxEffortLevel"/>.
        /// </summary>
        public static int DefaultEffortLevel
        {
            get => Volatile.Read(ref defaultEffortLevel);
            set => Volatile.Write(ref defaultEffortLevel, ClampEffortLevel(value));
        }

        public static int ClampEffortLevel(long value) => (int)Math.Min(Math.Max(value, MinEffortLevel), MaxEffortLevel);

        /// <summary>This instance's window, fixed at construction.</summary>
        public int EffortLevel { get; }

        // generated-but-unconsumed keys, by value, to the ordinal they were generated at (the newest, if a 32-bit value
        // repeats inside the window)
        private Dictionary<uint, long> xors = new Dictionary<uint, long>();

        // the same keys in generation order, so the oldest can be aged out without a scan. Entries for keys that were
        // consumed since are left in place and skipped when they reach the front.
        private Queue<KeyValuePair<long, uint>> generated = new Queue<KeyValuePair<long, uint>>();

        public uint CurrentKey;

        // ordinal of CurrentKey; the first key of the stream is ordinal 0
        private long currentOrdinal;

        // ordinal of the newest key that verified; -1 before the first
        private long newestVerifiedOrdinal = -1;

        public CryptoSystem(uint seed) : this(BitConverter.GetBytes(seed))
        {
        }

        public CryptoSystem(byte[] seed) : this(seed, DefaultEffortLevel)
        {
        }

        public CryptoSystem(byte[] seed, int effortLevel) : base(seed)
        {
            EffortLevel = ClampEffortLevel(effortLevel);
            CurrentKey = Next();
        }

        /// <summary>Keys generated and remembered but not yet consumed (skipped keys plus pre-generated look-ahead).</summary>
        public int OutstandingKeys => xors?.Count ?? 0;

        /// <summary>Ordinal of the newest key that verified, -1 before the first.</summary>
        public long NewestVerifiedOrdinal => newestVerifiedOrdinal;

        /// <summary>Keys a search may still generate before it stops: 0 means a miss costs one lookup.</summary>
        public long LookaheadRemaining => Math.Max(0, SearchLimit - currentOrdinal);

        // the furthest ordinal a search may advance CurrentKey to. With nothing skipped this is exactly the original
        // bound: after consuming ordinal v, CurrentKey is v + 1 and EffortLevel more keys may be generated.
        private long SearchLimit => newestVerifiedOrdinal + EffortLevel + 1;

        public void ConsumeKey(uint x)
        {
            if (CurrentKey == x)
            {
                NoteVerified(currentOrdinal);
                Advance();
            }
            else if (xors.TryGetValue(x, out var ordinal))
            {
                xors.Remove(x);
                NoteVerified(ordinal);
            }
            else
                return;

            Evict();
        }

        public bool Search(uint x)
        {
            if (CurrentKey == x)
                return true;

            if (xors.ContainsKey(x))
                return true;

            var limit = SearchLimit;

            while (currentOrdinal < limit)
            {
                Remember(CurrentKey, currentOrdinal);
                Advance();

                if (CurrentKey == x)
                    return true;
            }

            return false;
        }

        private void Advance()
        {
            CurrentKey = Next();
            currentOrdinal++;
        }

        private void Remember(uint key, long ordinal)
        {
            xors[key] = ordinal;
            generated.Enqueue(new KeyValuePair<long, uint>(ordinal, key));
        }

        private void NoteVerified(long ordinal)
        {
            // a late retransmit of an old key never moves the window back
            if (ordinal > newestVerifiedOrdinal)
                newestVerifiedOrdinal = ordinal;
        }

        // drop remembered keys more than EffortLevel behind the newest verified key
        private void Evict()
        {
            var oldestKept = newestVerifiedOrdinal - EffortLevel;

            while (generated.Count > 0 && generated.Peek().Key < oldestKept)
            {
                var entry = generated.Dequeue();

                // only if this value was not consumed and not regenerated at a newer ordinal since
                if (xors.TryGetValue(entry.Value, out var ordinal) && ordinal == entry.Key)
                    xors.Remove(entry.Value);
            }
        }

        public new void ReleaseResources()
        {
            xors?.Clear();
            xors = null;
            generated?.Clear();
            generated = null;
            base.ReleaseResources();
        }
    }
}
