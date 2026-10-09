using System;

namespace ACE.Server.Command
{
    public class CommandHandlerInfo
    {
        public Delegate Handler { get; set; }
        public CommandHandlerAttribute Attribute { get; set; }

        /// <summary>
        /// WaffleACE slow-tick capture: this handler's id in <see cref="CommandProfileIds"/>, 0 until first
        /// assigned. Cached here so charging a dispatch to its command is a field read, not a lookup.
        /// </summary>
        internal int ProfileId;

        /// <summary>The CommandProfileIds generation <see cref="ProfileId"/> was assigned in; see CommandProfileIds.ResetForTests.</summary>
        internal int ProfileGeneration;
    }
}
