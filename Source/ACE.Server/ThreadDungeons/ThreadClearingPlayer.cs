using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Who cleared a run, for placing its reward scene at arming. PURE over player guids, so the order and the
    /// eligibility rule are unit-tested without a Player (which this harness cannot construct).
    ///
    /// Candidate order: the killing blow's attacker, the killing blow's pet owner (a pet kill credits its owner),
    /// the top damager (pet resolved to owner), every damager by total damage descending (pet resolved to owner),
    /// the run owner, then the roster in order. The first candidate that is <see cref="IsEligible"/> wins.
    /// </summary>
    public static class ThreadClearingPlayer
    {
        /// <summary>One damage-history entry reduced to what the chain needs: who to credit, and how much they did.</summary>
        public readonly record struct Damager(uint Guid, float TotalDamage);

        /// <summary>The candidate guids in priority order, duplicates and zeros removed (first occurrence kept).</summary>
        public static List<uint> Candidates(uint? killer, uint? killerPetOwner, uint? topDamager, IEnumerable<Damager> damagers, uint owner, IEnumerable<uint> roster)
        {
            var ordered = new List<uint>();
            var seen = new HashSet<uint>();

            void Add(uint? guid)
            {
                if (guid is uint g && g != 0 && seen.Add(g))
                    ordered.Add(g);
            }

            Add(killer);
            Add(killerPetOwner);
            Add(topDamager);

            // OrderByDescending is a stable sort, so equal damage keeps the history's own order.
            foreach (var d in (damagers ?? Enumerable.Empty<Damager>()).OrderByDescending(d => d.TotalDamage))
                Add(d.Guid);

            Add(owner);

            foreach (var g in roster ?? Enumerable.Empty<uint>())
                Add(g);

            return ordered;
        }

        /// <summary>The first eligible candidate, or null when none is.</summary>
        public static uint? Resolve(IEnumerable<uint> candidates, Func<uint, bool> eligible)
        {
            if (candidates == null || eligible == null)
                return null;

            foreach (var guid in candidates)
                if (eligible(guid))
                    return guid;

            return null;
        }

        /// <summary>
        /// May this guid be the clearing player? ThreadRunPresence.IsMemberInside's clauses with the Player split
        /// out: online (the damage history's WeakReference can outlive a logout), a roster member, NOT removed by the
        /// puzzle fail policy (MarkPuzzleRemoved lands before the eject teleport does, so a removed member can still
        /// be standing in the copy), and standing in the run's copy. A non-roster player inside the copy (an admin
        /// visiting) is never chosen.
        /// </summary>
        public static bool IsEligible(ThreadDungeonRun run, uint guid, bool online, bool inCopy)
            => run != null
               && online
               && inCopy
               && run.IsRosterMember(guid)
               && !run.IsPuzzleRemoved(guid);
    }
}
