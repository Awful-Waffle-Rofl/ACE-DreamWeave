using System.Collections.Generic;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Open World Threads R18: the one-shot free re-entry owed to a player whose lethal-Unraveling death sent them out of
    /// the copy. See <see cref="ThreadUnravelDeath"/>. Guarded by the run's stateLock like the roster state.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        private readonly HashSet<uint> unravelReentryCredits = new HashSet<uint>();

        /// <summary>Credits a roster member with one free re-entry. Ignored for a non-member.</summary>
        public void GrantUnravelReentry(uint memberGuid)
        {
            if (!IsRosterMember(memberGuid))
                return;

            lock (stateLock)
                unravelReentryCredits.Add(memberGuid);
        }

        /// <summary>Spends the credit if there is one. True exactly once per grant.</summary>
        public bool TryConsumeUnravelReentry(uint memberGuid)
        {
            lock (stateLock)
                return unravelReentryCredits.Remove(memberGuid);
        }

        /// <summary>True while an unspent credit exists (tests, diagnostics).</summary>
        public bool HasUnravelReentry(uint memberGuid)
        {
            lock (stateLock)
                return unravelReentryCredits.Contains(memberGuid);
        }
    }
}
