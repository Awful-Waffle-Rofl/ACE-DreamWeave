using System;
using System.Collections.Generic;

using ACE.Database.Models.World;
using ACE.Entity.Enum;

namespace ACE.Server.ThreadDungeons
{
    public sealed class ThreadDungeonFilterStats
    {
        public int Kept;
        public int DroppedGenerators;
        public int DroppedCreatures;
        public int DroppedContainers;
        public int ReplacedPortals;
        public int DroppedUnresolved;

        public override string ToString()
            => $"kept={Kept} generators={DroppedGenerators} creatures={DroppedCreatures} containers={DroppedContainers} portals->exit={ReplacedPortals} unresolved={DroppedUnresolved}";
    }

    /// <summary>
    /// Row-level filter over a landblock's cached instance list for a Thread copy (TECH-DESIGN S3).
    /// PURE and NON-MUTATING: the input is the list WorldDatabaseWithEntityCache hands to EVERY copy of the
    /// landblock, so the output is a new list, kept rows are the same objects, and replaced rows are clones.
    ///
    /// Decision order per row, and the order matters:
    ///  1. A wcid the world db cannot resolve is dropped and counted under DroppedUnresolved. ONLY a
    ///     KeyNotFoundException means that; any other exception is a real world-db failure and propagates,
    ///     because a half-built copy is worse than a landblock that loudly fails to load.
    ///  2. Portal and HousePortal rows are REPLACED with a clone carrying the run's exit portal wcid, and
    ///     this happens BEFORE the generator drop below. Some retail portal weenies carry a generator table,
    ///     and dropping one of those would leave the run with no way out. The clone discards the generator
    ///     table along with everything else, so replacing is always the safe answer for a portal.
    ///  3. Everything else whose weenie has a generator table is dropped, which drops its whole spawn tree
    ///     (ActivateLinks only walks kept parents).
    ///  4. Creature rows are dropped, removing the hand-placed statics.
    ///  5. The container family is dropped, removing the retail loot and every retail storage surface:
    ///     Chest, Container, Storage, SlumLord and Hook (TECH-DESIGN rule 2).
    ///  6. Everything else - Door, Switch, PressurePlate, HotSpot, Generic, LifeStone and so on - is kept
    ///     as the SAME object reference. IsLinkChild plays no part in any of this: a link child is judged
    ///     on its own weenie exactly like a top-level row.
    /// </summary>
    public static class ThreadDungeonContentFilter
    {
        public static List<LandblockInstance> Filter(IReadOnlyList<LandblockInstance> source, Func<uint, WeenieType> weenieTypeOf,
            Func<uint, bool> isGenerator, uint exitPortalWcid, out ThreadDungeonFilterStats stats)
        {
            stats = new ThreadDungeonFilterStats();
            var result = new List<LandblockInstance>(source.Count);

            foreach (var row in source)
            {
                WeenieType type;
                bool generator;
                try
                {
                    type = weenieTypeOf(row.WeenieClassId);
                    generator = isGenerator(row.WeenieClassId);
                }
                catch (KeyNotFoundException)
                {
                    // the world db has no such weenie - the row is content debris, not a db failure
                    stats.DroppedUnresolved++;
                    continue;
                }

                // Portals are decided BEFORE the generator drop: a portal weenie that also carries a
                // generator table would otherwise be dropped and leave the run with no exit.
                switch (type)
                {
                    case WeenieType.Portal:
                    case WeenieType.HousePortal:
                        result.Add(CloneAsExit(row, exitPortalWcid));
                        stats.ReplacedPortals++;
                        continue;
                }

                if (generator) { stats.DroppedGenerators++; continue; }

                switch (type)
                {
                    case WeenieType.Creature:
                        stats.DroppedCreatures++;
                        continue;

                    case WeenieType.Chest:
                    case WeenieType.Container:
                    case WeenieType.Storage:
                    case WeenieType.SlumLord:
                    case WeenieType.Hook:
                        stats.DroppedContainers++;
                        continue;

                    default:
                        result.Add(row);
                        stats.Kept++;
                        continue;
                }
            }

            return result;
        }

        private static LandblockInstance CloneAsExit(LandblockInstance row, uint exitPortalWcid)
        {
            return new LandblockInstance
            {
                Guid = row.Guid,
                WeenieClassId = exitPortalWcid,
                ObjCellId = row.ObjCellId,
                OriginX = row.OriginX,
                OriginY = row.OriginY,
                OriginZ = row.OriginZ,
                AnglesW = row.AnglesW,
                AnglesX = row.AnglesX,
                AnglesY = row.AnglesY,
                AnglesZ = row.AnglesZ,
                IsLinkChild = row.IsLinkChild,
                Landblock = row.Landblock,
                // LandblockInstanceLink stays the empty default collection: an exit portal links to nothing
            };
        }
    }
}
