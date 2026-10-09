using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Database;
using ACE.Entity.Enum.Properties;

using log4net;

using ShardBiota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.WaveEncounters
{
    /// <summary>
    /// The BACKSTOP against a wave-encounter creature reaching the shard; the sibling of
    /// MlDigsiteOrphanFilter, same shape and same rules. The primary guarantee is the persistence exclusion
    /// in WorldObject.IsDynamicThatShouldPersistToShard, which refuses to write any object carrying
    /// PropertyInt.WaveEncounterId; this catches what that cannot (a hard kill, a save racing the stamp).
    /// An encounter id from a previous process is meaningless, so PRESENCE of the stamp makes an orphan.
    ///
    /// THE BIOTA TRAP APPLIES HERE. GetDynamicObjectsByLandblock returns ACE.Database.Models.Shard.Biota -
    /// the EF entity, with a BiotaPropertiesInt collection of Type/Value rows - NOT the runtime
    /// ACE.Entity.Models.Biota. The alias pins that down; nothing here converts between the two.
    /// </summary>
    public static class WaveEncounterOrphanFilter
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ushort EncounterIdPropertyType = (ushort)PropertyInt.WaveEncounterId;

        /// <summary>True when this persisted biota carries the wave-encounter stamp (any value). Pure.</summary>
        public static bool IsWaveEncounterOrphan(ShardBiota biota)
        {
            var properties = biota?.BiotaPropertiesInt;

            if (properties == null)
                return false;

            foreach (var property in properties)
            {
                if (property != null && property.Type == EncounterIdPropertyType)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Splits a landblock's dynamic biotas into the ones that may enter the world and the ids of the ones
        /// that must be deleted instead. Pure. The no-orphan case returns the SAME list instance, so callers
        /// must treat <paramref name="kept"/> as possibly aliasing <paramref name="dynamics"/>.
        /// </summary>
        public static void Partition(List<ShardBiota> dynamics, out List<ShardBiota> kept, out List<uint> orphanIds)
        {
            orphanIds = new List<uint>();

            if (dynamics == null || dynamics.Count == 0)
            {
                kept = dynamics ?? new List<ShardBiota>();
                return;
            }

            var orphanCount = 0;

            foreach (var biota in dynamics)
            {
                if (IsWaveEncounterOrphan(biota))
                    orphanCount++;
            }

            if (orphanCount == 0)
            {
                kept = dynamics;
                return;
            }

            kept = new List<ShardBiota>(dynamics.Count - orphanCount);

            foreach (var biota in dynamics)
            {
                if (IsWaveEncounterOrphan(biota))
                    orphanIds.Add(biota.Id);
                else
                    kept.Add(biota);
            }
        }

        /// <summary>
        /// The landblock-facing entry point. Never throws (a failure loads the landblock unfiltered) and never
        /// deletes synchronously: the delete is queued on SerializedShardDatabase's worker thread, exactly as
        /// MlDigsiteOrphanFilter does it.
        /// </summary>
        public static List<ShardBiota> FilterAndSweep(ushort landblockId, uint instance, List<ShardBiota> dynamics)
        {
            try
            {
                Partition(dynamics, out var kept, out var orphanIds);

                if (orphanIds.Count == 0)
                    return dynamics;

                var guids = string.Join(",", orphanIds.Select(id => $"0x{id:X8}"));

                log.Info($"[WAVE_ENCOUNTER] landblock 0x{landblockId:X4} instance {instance} orphan sweep removed={orphanIds.Count} guids={guids}");

                DatabaseManager.Shard.RemoveBiotasInParallel(orphanIds, result =>
                {
                    if (!result)
                        log.Error($"[WAVE_ENCOUNTER] landblock 0x{landblockId:X4} instance {instance} orphan sweep failed to delete {orphanIds.Count} biota(s) from the shard: {guids}");
                }, null);

                return kept;
            }
            catch (Exception ex)
            {
                log.Error($"[WAVE_ENCOUNTER] landblock 0x{landblockId:X4} instance {instance} orphan sweep failed; loading the landblock unfiltered", ex);

                return dynamics;
            }
        }
    }
}
