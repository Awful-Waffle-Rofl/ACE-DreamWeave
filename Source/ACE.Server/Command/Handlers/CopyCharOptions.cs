using System;

using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Optional behaviour for <see cref="AdminCommands.DoCopyChar"/>. Leaving it null keeps the
    /// historical behaviour of @copychar, @restorechar and tester roster seeding exactly.
    /// </summary>
    internal sealed class CopyCharOptions
    {
        /// <summary>
        /// Runs once on the fully built copy, after every instance-id reference has been rewritten
        /// (including the enchantment caster swaps) and before its possessions are gathered and the
        /// character is saved. It is the ONLY place a caller may strip or change what is copied:
        /// DoCopyChar's own body is pinned to remove nothing but identity links
        /// (CopyCharItemPropertyCarryTests), so a strip belongs here.
        ///
        /// A throw is caught: the copy is abandoned before anything is saved, the failure is reported
        /// to the session and logged, and onDone(false) runs.
        /// </summary>
        public Action<Player> BeforeSave;

        /// <summary>
        /// True (the default) publishes the saved copy: PlayerManager.AddOfflinePlayer, then the
        /// character-select list of the receiving session. False saves the character and its facets
        /// and reports success, but publishes it nowhere, for a copy that is parked rather than played
        /// (a Balance Trials master slot).
        /// </summary>
        public bool Publish = true;
    }
}
