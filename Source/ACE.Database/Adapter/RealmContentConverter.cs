using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.World;

namespace ACE.Database.Adapter
{
    /// <summary>
    /// Realms Phase 3: maps per-realm content rows (landblock_instance_realm) into the
    /// base LandblockInstance shape, so WorldObjectFactory and every other downstream
    /// consumer of landblock content stays untouched.
    /// </summary>
    public static class RealmContentConverter
    {
        public static LandblockInstance ConvertToLandblockInstance(LandblockInstanceRealm row)
        {
            return new LandblockInstance
            {
                Guid = row.Guid,
                Landblock = row.Landblock,
                WeenieClassId = row.WeenieClassId,
                ObjCellId = row.ObjCellId,
                OriginX = row.OriginX,
                OriginY = row.OriginY,
                OriginZ = row.OriginZ,
                AnglesW = row.AnglesW,
                AnglesX = row.AnglesX,
                AnglesY = row.AnglesY,
                AnglesZ = row.AnglesZ,
                IsLinkChild = row.IsLinkChild,
                LastModified = row.LastModified,

                LandblockInstanceLink = row.LandblockInstanceLinkRealm.Select(link => new LandblockInstanceLink
                {
                    ParentGuid = link.ParentGuid,
                    ChildGuid = link.ChildGuid,
                    LastModified = link.LastModified,
                }).ToList(),
            };
        }

        public static List<LandblockInstance> ConvertToLandblockInstances(IEnumerable<LandblockInstanceRealm> rows)
        {
            return rows.Select(ConvertToLandblockInstance).ToList();
        }
    }
}
