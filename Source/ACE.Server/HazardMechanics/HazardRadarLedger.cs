using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Which creatures a run has hidden from the radar, and what each one's radar behaviour was BEFORE: the authored
    /// value (null = the weenie never authored one). The framework restores every entry when the run ends
    /// (HazardContext.RestoreAllRadar), whatever the outcome, so an aborted run still gives the blips back.
    /// Landblock thread only.
    /// </summary>
    internal sealed class HazardRadarLedger<T> where T : class
    {
        private readonly Dictionary<uint, (T Creature, RadarBehavior? Authored)> entries = new Dictionary<uint, (T, RadarBehavior?)>();

        /// <summary>
        /// Records a creature about to be hidden. False, and nothing changed, when the guid is already recorded: the
        /// FIRST authored value wins (a null is a real authored value too), because by the second call the creature
        /// already carries the hidden value.
        /// </summary>
        public bool Record(uint guid, T creature, RadarBehavior? authored)
        {
            if (entries.ContainsKey(guid))
                return false;

            entries[guid] = (creature, authored);
            return true;
        }

        /// <summary>Removes and returns one entry; false when the guid was not recorded.</summary>
        public bool TryTake(uint guid, out T creature, out RadarBehavior? authored)
        {
            if (entries.TryGetValue(guid, out var entry))
            {
                entries.Remove(guid);
                creature = entry.Creature;
                authored = entry.Authored;
                return true;
            }

            creature = null;
            authored = null;
            return false;
        }

        /// <summary>The guids currently hidden.</summary>
        public IReadOnlyCollection<uint> Hidden => entries.Keys.ToList();

        /// <summary>Removes and returns every remaining entry.</summary>
        public List<(T Creature, RadarBehavior? Authored)> TakeAll()
        {
            var all = entries.Values.ToList();

            entries.Clear();
            return all;
        }
    }
}
