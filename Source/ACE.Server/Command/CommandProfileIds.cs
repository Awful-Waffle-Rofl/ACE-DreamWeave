using System;
using System.Collections.Generic;
using System.Threading;

namespace ACE.Server.Command
{
    /// <summary>
    /// WaffleACE slow-tick capture: a small integer id per REGISTERED command name, so the inbound-message
    /// attribution (InboundOpcodeProfile) can charge a chat @command to cmd_&lt;name&gt; with a key in its
    /// existing 64-bit space and no string on the dispatch path.
    ///
    /// Ids are assigned lazily, the first time a resolved handler is charged, from the handler's
    /// CommandHandlerAttribute.Command - never from what the player typed - and cached on the
    /// CommandHandlerInfo, so every later charge of that handler is one field read. Lazy rather than built at
    /// CommandManager.Initialize because commands can also be registered later (CommandManager.TryAddCommand);
    /// the first charge of each name takes this class's lock once and allocates one dictionary entry, and
    /// nothing after that allocates. Two handlers registered under the same name (an override) share one id.
    ///
    /// The set is bounded by the command registry itself, and additionally capped at <see cref="MaxIds"/>:
    /// past that every new name is charged to <see cref="Unknown"/> rather than growing without bound.
    ///
    /// Names are lowercased and every character outside [a-z0-9_-] becomes '_' when the id is assigned, so
    /// the rendered value is always a single logfmt token.
    /// </summary>
    public static class CommandProfileIds
    {
        /// <summary>The id charged for a command that was not run: unknown, unauthorized, bad parameters, not in world.</summary>
        public const int Unknown = 0;

        public const string UnknownName = "unknown";

        public const int MaxIds = 4096;

        private static readonly object sync = new object();

        private static readonly Dictionary<string, int> idsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // index = id; slot 0 is Unknown and never holds a name
        private static readonly List<string> names = new List<string> { null };

        // Bumped by ResetForTests. A CommandHandlerInfo caches the generation its id was assigned in, so an id
        // cached before a reset is re-assigned rather than pointing at a slot that no longer names it.
        private static int generation = 1;

        /// <summary>
        /// The id of <paramref name="info"/>'s registered command name, assigning one on first use. Returns
        /// <see cref="Unknown"/> for a null handler or one with no command name.
        /// </summary>
        public static int IdFor(CommandHandlerInfo info)
        {
            if (info == null)
                return Unknown;

            var id = info.ProfileId;

            if (id != Unknown && info.ProfileGeneration == Volatile.Read(ref generation))
                return id;

            return Assign(info);
        }

        /// <summary>
        /// Test hook: forgets every assigned id, restoring the initial state, so names registered by one test
        /// class cannot leak into the next. Ids already cached on CommandHandlerInfo instances are invalidated
        /// through the generation stamp, not by touching the instances.
        /// </summary>
        internal static void ResetForTests()
        {
            lock (sync)
            {
                idsByName.Clear();
                names.Clear();
                names.Add(null);
                Volatile.Write(ref generation, generation + 1);
            }
        }

        /// <summary>The sanitized registered name for <paramref name="id"/>, or null when it has none. Emit path only.</summary>
        public static string NameOf(int id)
        {
            lock (sync)
                return id > Unknown && id < names.Count ? names[id] : null;
        }

        private static int Assign(CommandHandlerInfo info)
        {
            var command = info.Attribute?.Command;

            if (string.IsNullOrEmpty(command))
                return Unknown;

            lock (sync)
            {
                if (!idsByName.TryGetValue(command, out var id))
                {
                    if (names.Count >= MaxIds)
                        return Unknown;

                    id = names.Count;
                    names.Add(Sanitize(command));
                    idsByName.Add(command, id);
                }

                info.ProfileId = id;
                info.ProfileGeneration = generation;

                return id;
            }
        }

        internal static string Sanitize(string command)
        {
            var chars = command.ToLowerInvariant().ToCharArray();

            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];

                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    chars[i] = '_';
            }

            return new string(chars);
        }
    }
}
