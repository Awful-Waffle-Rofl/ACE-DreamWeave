using System;
using System.Security.Cryptography;
using System.Threading;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The web class ability panel's change detector: one per Player instance, bumped whenever the character's
    /// class ability ranks or class ability point balances may have changed (Player.AdjustClassAbilityPoints and
    /// Player.InvalidateClassAbilityHookCaches, which every rank-changing path already calls).
    ///
    /// <see cref="Current"/> is an opaque "epoch:counter" string. The epoch is random per instance, so a character
    /// that logs out and back in (a new Player) answers a new revision even when nothing else changed, and the
    /// counter is Interlocked, so a request thread can read it without a world-thread hop (the panel's ?since
    /// fast path). A client compares it for equality only.
    ///
    /// Bumping is deliberately over-eager: a bump with no real change only costs the client one full snapshot,
    /// while a missed bump would leave the panel showing a stale build.
    /// </summary>
    public sealed class ClassAbilityRevisionCounter
    {
        private readonly string epoch;
        private long counter;

        public ClassAbilityRevisionCounter() : this(NewEpoch())
        {
        }

        /// <summary>Test seam: a fixed epoch.</summary>
        public ClassAbilityRevisionCounter(string epoch)
        {
            this.epoch = string.IsNullOrEmpty(epoch) ? NewEpoch() : epoch;
        }

        /// <summary>Safe from any thread.</summary>
        public string Current => $"{epoch}:{Interlocked.Read(ref counter)}";

        /// <summary>Safe from any thread.</summary>
        public void Bump() => Interlocked.Increment(ref counter);

        private static string NewEpoch()
        {
            Span<byte> bytes = stackalloc byte[6];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
