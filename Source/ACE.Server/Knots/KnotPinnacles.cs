using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.Knots
{
    /// <summary>
    /// The one definition of an available pinnacle: enabled AND an encounter registered for its destination kind.
    /// Pattern drops, the random Knot pick and Pattern binding all read it, so a Pattern can never name a pinnacle
    /// a Knot cannot enter.
    /// </summary>
    public static class KnotPinnacles
    {
        public static bool IsAvailable(PinnacleDef def, Func<string, bool> hasEncounter)
            => def != null && def.Enabled && hasEncounter(def.Destination?.Kind);

        public static List<PinnacleDef> Available(IReadOnlyList<PinnacleDef> enabled, Func<string, bool> hasEncounter)
            => (enabled ?? Array.Empty<PinnacleDef>()).Where(p => IsAvailable(p, hasEncounter)).ToList();

        public static bool EncounterRegistered(string kind) => PinnacleEncounters.TryGet(kind, out _);

        /// <summary>Available pinnacles of the live store against the live encounter registry.</summary>
        public static List<PinnacleDef> Available(ThreadDungeonStore store)
            => Available(store?.EnabledPinnacles, EncounterRegistered);
    }
}
