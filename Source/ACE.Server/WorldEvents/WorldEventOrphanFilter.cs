using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Database;
using ACE.Entity.Enum.Properties;

using log4net;

using ShardBiota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The orphan sweep (TECH-DESIGN 2.9, decision C9). Everything a running event spawns carries
    /// PropertyInt.WorldEventId, and WorldObject_Database's persistence exclusion refuses to write any object
    /// carrying it, so a normally running server never persists one. A hard kill mid-event (or a SaveDB that
    /// raced the exclusion) can still leave a stamped biota in the shard, and that biota would otherwise walk
    /// back into the world the next time its landblock activates.
    ///
    /// There is deliberately NO boot scan. A stray biota cannot matter until its landblock loads, so filtering
    /// inside Landblock.SpawnDynamicShardObjects has the same coverage as a startup sweep at zero startup cost.
    ///
    /// THE BIOTA TRAP APPLIES HERE. GetDynamicObjectsByLandblock returns ACE.Database.Models.Shard.Biota - the EF
    /// entity, with navigation collections (BiotaPropertiesInt, rows of Type/Value) - NOT the runtime
    /// ACE.Entity.Models.Biota with its PropertiesInt dictionary. The alias below pins that down so a later reader
    /// cannot mistake which Biota this file means; nothing here ever converts between the two shapes.
    ///
    /// <see cref="IsWorldEventOrphan"/> and <see cref="Partition"/> are pure and are what the tests exercise with
    /// hand-built EF entities. <see cref="FilterAndSweep"/> is the impure wrapper the landblock calls: it logs,
    /// queues the shard delete, and never throws.
    /// </summary>
    public static class WorldEventOrphanFilter
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The property-table row type of PropertyInt.WorldEventId (9041). BiotaPropertiesInt.Type is a ushort,
        /// so the enum is narrowed once here rather than at every comparison.
        /// </summary>
        private static readonly ushort WorldEventIdPropertyType = (ushort)PropertyInt.WorldEventId;

        /// <summary>
        /// True when this persisted biota carries the world-event stamp. PRESENCE is the marker (TECH-DESIGN
        /// 2.13): the value is the RunId of whichever run spawned it, and a run id from a previous process is
        /// meaningless now, so any value at all - including 0 - makes the biota an orphan.
        /// Pure: no database, no logging, safe to call on a hand-built entity.
        /// </summary>
        public static bool IsWorldEventOrphan(ShardBiota biota)
        {
            var properties = biota?.BiotaPropertiesInt;

            if (properties == null)
                return false;

            foreach (var property in properties)
            {
                if (property != null && property.Type == WorldEventIdPropertyType)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Splits a landblock's dynamic biotas into the ones that may enter the world and the ids of the ones that
        /// must be deleted from the shard instead. Pure.
        ///
        /// The no-orphan case - which is every landblock load on a server that has not crashed mid-event - hands
        /// back the SAME list instance rather than a copy, so the normal path costs one scan and no allocation.
        /// Callers must therefore treat <paramref name="kept"/> as possibly aliasing <paramref name="dynamics"/>.
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
                if (IsWorldEventOrphan(biota))
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
                if (IsWorldEventOrphan(biota))
                    orphanIds.Add(biota.Id);
                else
                    kept.Add(biota);
            }
        }

        /// <summary>
        /// The landblock-facing entry point: returns the list that should be handed to WorldObjectFactory, and as
        /// a side effect logs and queues the shard delete for anything removed.
        ///
        /// Two things this must never do. It must never throw - an orphan that slips back into the world is a
        /// cosmetic bug, a landblock that fails to load is an outage - so every failure falls back to the
        /// unfiltered list. And it must never delete synchronously: this runs on the landblock's load task, while
        /// every shard write belongs on SerializedShardDatabase's single worker thread, so the delete is queued
        /// through DatabaseManager.Shard.RemoveBiotasInParallel and the landblock does not wait for it. That path
        /// reaches ShardDatabase.RemoveBiotasInParallel -> the virtual RemoveBiotaBatch, which
        /// ShardDatabaseWithCaching overrides to evict the ids from its in-memory biota cache first, so no extra
        /// cache invalidation is needed here.
        /// </summary>
        public static List<ShardBiota> FilterAndSweep(ushort landblockId, uint instance, List<ShardBiota> dynamics)
        {
            try
            {
                Partition(dynamics, out var kept, out var orphanIds);

                if (orphanIds.Count == 0)
                    return dynamics;

                var guids = string.Join(",", orphanIds.Select(id => $"0x{id:X8}"));

                log.Info($"[WORLDEVENT] landblock 0x{landblockId:X4} instance {instance} orphan sweep removed={orphanIds.Count} guids={guids}");

                DatabaseManager.Shard.RemoveBiotasInParallel(orphanIds, result =>
                {
                    if (!result)
                        log.Error($"[WORLDEVENT] landblock 0x{landblockId:X4} instance {instance} orphan sweep failed to delete {orphanIds.Count} biota(s) from the shard: {guids}");
                }, null);

                return kept;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] landblock 0x{landblockId:X4} instance {instance} orphan sweep failed; loading the landblock unfiltered", ex);

                return dynamics;
            }
        }
    }
}
