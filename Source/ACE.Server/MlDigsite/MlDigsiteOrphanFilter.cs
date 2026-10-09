using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Database;
using ACE.Entity.Enum.Properties;

using log4net;

using ShardBiota = ACE.Database.Models.Shard.Biota;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// The BACKSTOP against a digsite object reaching the shard (invariant 7). The primary guarantee is the
    /// persistence exclusion in WorldObject.IsDynamicThatShouldPersistToShard, which refuses to write any
    /// object carrying PropertyInt.MlDigsiteEncounterId; this catches what that cannot.
    ///
    /// WHY A BACKSTOP IS NEEDED HERE AND NOT FOR A THREAD CACHE. A Thread run lives inside an EPHEMERAL
    /// landblock, and Landblock.SaveDB returns on `if (IsEphemeral)` before it iterates world objects at all,
    /// so nothing inside a Thread copy is ever offered to the save. A digsite runs on an ordinary
    /// PERSISTENT, SHARED outdoor Marae Lassel landblock, where that early return never fires and the
    /// property exclusion is the only thing standing between an encounter creature and a biota row. A hard
    /// kill mid-encounter, or a SaveDB that raced the exclusion, can still leave one behind - and that biota
    /// would walk back into the world the next time the landblock activated, as a monster nobody summoned
    /// standing on a hillside forever.
    ///
    /// There is deliberately NO boot scan: a stray biota cannot matter until its landblock loads, so
    /// filtering inside Landblock.SpawnDynamicShardObjects has the same coverage at zero startup cost. This
    /// mirrors WorldEventOrphanFilter exactly, including that decision.
    ///
    /// THE BIOTA TRAP APPLIES HERE. GetDynamicObjectsByLandblock returns ACE.Database.Models.Shard.Biota -
    /// the EF entity, with navigation collections of Type/Value rows - NOT the runtime
    /// ACE.Entity.Models.Biota with its PropertiesInt dictionary. The alias below pins that down; nothing
    /// here ever converts between the two shapes.
    /// </summary>
    public static class MlDigsiteOrphanFilter
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The property-table row type of PropertyInt.MlDigsiteEncounterId. BiotaPropertiesInt.Type is a
        /// ushort, so the enum is narrowed once here rather than at every comparison.
        /// </summary>
        private static readonly ushort EncounterIdPropertyType = (ushort)PropertyInt.MlDigsiteEncounterId;

        /// <summary>
        /// True when this persisted biota carries the digsite stamp. PRESENCE is the marker: the value is the
        /// id of whichever encounter placed it, and an encounter id from a previous process is meaningless
        /// now, so any value at all - including 0 - makes the biota an orphan.
        ///
        /// Unlike the Relaria exclusion, this needs NO `is Creature` narrowing anywhere, and the reason is
        /// that 9068 has exactly one carrier class: objects an encounter itself placed. Nothing a player can
        /// hold, drop or loot ever carries it - the reward chest carries it, but its CONTENTS deliberately do
        /// not, so an item pulled out of the chest persists normally by the ordinary pickup path.
        /// Pure: no database, no logging, safe to call on a hand-built entity.
        /// </summary>
        public static bool IsDigsiteOrphan(ShardBiota biota)
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
        /// that must be deleted from the shard instead. Pure.
        ///
        /// The no-orphan case - which is every landblock load on a server that has not crashed mid-encounter
        /// - hands back the SAME list instance rather than a copy, so the normal path costs one scan and no
        /// allocation. Callers must therefore treat <paramref name="kept"/> as possibly aliasing
        /// <paramref name="dynamics"/>.
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
                if (IsDigsiteOrphan(biota))
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
                if (IsDigsiteOrphan(biota))
                    orphanIds.Add(biota.Id);
                else
                    kept.Add(biota);
            }
        }

        /// <summary>
        /// The landblock-facing entry point: returns the list that should be handed to WorldObjectFactory,
        /// and as a side effect logs and queues the shard delete for anything removed.
        ///
        /// Two things this must never do, both copied deliberately from WorldEventOrphanFilter. It must never
        /// throw - an orphan that slips back into the world is a cosmetic bug, a landblock that fails to load
        /// is an outage - so every failure falls back to the unfiltered list. And it must never delete
        /// synchronously: this runs on the landblock's load task, while every shard write belongs on
        /// SerializedShardDatabase's single worker thread, so the delete is queued through
        /// DatabaseManager.Shard.RemoveBiotasInParallel and the landblock does not wait for it.
        /// </summary>
        public static List<ShardBiota> FilterAndSweep(ushort landblockId, uint instance, List<ShardBiota> dynamics)
        {
            try
            {
                Partition(dynamics, out var kept, out var orphanIds);

                if (orphanIds.Count == 0)
                    return dynamics;

                var guids = string.Join(",", orphanIds.Select(id => $"0x{id:X8}"));

                log.Info($"[ML_DIGSITE] landblock 0x{landblockId:X4} instance {instance} orphan sweep removed={orphanIds.Count} guids={guids}");

                DatabaseManager.Shard.RemoveBiotasInParallel(orphanIds, result =>
                {
                    if (!result)
                        log.Error($"[ML_DIGSITE] landblock 0x{landblockId:X4} instance {instance} orphan sweep failed to delete {orphanIds.Count} biota(s) from the shard: {guids}");
                }, null);

                return kept;
            }
            catch (Exception ex)
            {
                log.Error($"[ML_DIGSITE] landblock 0x{landblockId:X4} instance {instance} orphan sweep failed; loading the landblock unfiltered", ex);

                return dynamics;
            }
        }
    }
}
