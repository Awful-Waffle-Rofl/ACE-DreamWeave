using System;
using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Knots half of a run (Docs/Knots/KNOTS-DESIGN.md section 3): the planned Fiber level, stamped once beside
    /// the run's other populate-time stamps, and the held-pile query the cache placer uses to pick the Knotted Thread
    /// Cache. Every field is guarded by the run's private stateLock.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        private bool knotLevelSet;
        private int? knotLevel;

        /// <summary>
        /// Stamps the run's Fiber level (KnotLevelMath.PlannedAverage over the population plan). Write-once, latched
        /// like <see cref="MarkRewardProfile"/>: a retried populate step cannot move it, and an average over kills is
        /// never substituted for it (design section 3.2: a clear needs only part of the trash). Null records "no
        /// level" (an empty plan), which makes the run roll no Knot drops.
        /// </summary>
        public void MarkKnotLevel(int? level)
        {
            lock (stateLock)
            {
                if (knotLevelSet) return;
                knotLevelSet = true;
                knotLevel = level;
            }
        }

        /// <summary>The stamped Fiber level; null until <see cref="MarkKnotLevel"/> has run, or when the plan was empty.</summary>
        public int? KnotLevel { get { lock (stateLock) return knotLevel; } }

        /// <summary>
        /// True when the member's held pile holds an item matching <paramref name="predicate"/>. Read under the run's
        /// state lock, so it sees the pile as one consistent snapshot. False for a non-member or a null predicate.
        /// </summary>
        public bool HeldPileContains(uint guid, Func<WorldObject, bool> predicate)
        {
            if (predicate == null || !members.TryGetValue(guid, out var ms))
                return false;

            lock (stateLock)
                return ms.HeldPile.Any(h => h.Item != null && predicate(h.Item));
        }
    }
}
