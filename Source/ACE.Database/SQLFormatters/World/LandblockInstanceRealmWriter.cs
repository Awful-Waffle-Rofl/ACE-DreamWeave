using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Database.Models.World;

namespace ACE.Database.SQLFormatters.World
{
    /// <summary>
    /// Realms Phase 3: exports `landblock_instance_realm` / `landblock_instance_link_realm` rows
    /// for one (landblock, realm) pair, mirroring <see cref="LandblockInstanceWriter"/>'s
    /// idempotent DELETE-then-INSERT shape for the base landblock_instance tables.
    /// </summary>
    public class LandblockInstanceRealmWriter : SQLWriter
    {
        /// <summary>
        /// Default is formed from: (input.ObjCellId >> 16).ToString("X4") + "_realm" + realmId
        /// </summary>
        public string GetDefaultFileName(LandblockInstance input, ushort realmId)
        {
            string fileName = (input.ObjCellId >> 16).ToString("X4") + "_realm" + realmId;
            fileName = IllegalInFileName.Replace(fileName, "_");
            fileName += ".sql";

            return fileName;
        }

        public void CreateSQLDELETEStatement(IList<LandblockInstance> input, ushort realmId, StreamWriter writer)
        {
            var landblock = input[0].ObjCellId >> 16;

            writer.WriteLine($"DELETE FROM `landblock_instance_link_realm` WHERE `realm_id` = {realmId} AND `parent_GUID` IN (SELECT `guid` FROM `landblock_instance_realm` WHERE `realm_id` = {realmId} AND (`obj_Cell_Id` >> 16) = 0x{landblock:X4});");
            writer.WriteLine();
            writer.WriteLine($"DELETE FROM `landblock_instance_realm` WHERE `realm_id` = {realmId} AND (`obj_Cell_Id` >> 16) = 0x{landblock:X4};");
        }

        /// <exception cref="System.Exception">WeenieClassNames must be set, and must have a record for input.ClassId.</exception>
        public void CreateSQLINSERTStatement(IList<LandblockInstance> input, ushort realmId, StreamWriter writer)
        {
            var instanceWcids = input.ToDictionary(i => i.Guid, i => i.WeenieClassId);

            input = input.OrderBy(r => r.Guid).ToList();

            foreach (var value in input)
            {
                if (value != input[0])
                    writer.WriteLine();

                writer.WriteLine("INSERT INTO `landblock_instance_realm` (`realm_id`, `guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`, `is_Link_Child`)");

                string label = null;

                if (WeenieNames != null)
                    WeenieNames.TryGetValue(value.WeenieClassId, out label);

                var output = "VALUES (" +
                             $"{realmId}, " +
                             $"0x{value.Guid.ToString("X8")}, " +
                             $"{value.WeenieClassId.ToString().PadLeft(5)}, " +
                             $"0x{value.ObjCellId:X8}, " +
                             $"{TrimNegativeZero(value.OriginX):0.######}, " +
                             $"{TrimNegativeZero(value.OriginY):0.######}, " +
                             $"{TrimNegativeZero(value.OriginZ):0.######}, " +
                             $"{TrimNegativeZero(value.AnglesW):0.######}, " +
                             $"{TrimNegativeZero(value.AnglesX):0.######}, " +
                             $"{TrimNegativeZero(value.AnglesY):0.######}, " +
                             $"{TrimNegativeZero(value.AnglesZ):0.######}, " +
                             $"{(value.IsLinkChild ? 1 : 0)}" +
                             $"); /* {label} */" +
                             Environment.NewLine + $"/* @teleloc 0x{value.ObjCellId:X8} [{TrimNegativeZero(value.OriginX):F6} {TrimNegativeZero(value.OriginY):F6} {TrimNegativeZero(value.OriginZ):F6}] {TrimNegativeZero(value.AnglesW):F6} {TrimNegativeZero(value.AnglesX):F6} {TrimNegativeZero(value.AnglesY):F6} {TrimNegativeZero(value.AnglesZ):F6} */";

                output = FixNullFields(output);

                writer.WriteLine(output);

                if (value.LandblockInstanceLink != null && value.LandblockInstanceLink.Count > 0)
                {
                    writer.WriteLine();
                    CreateSQLINSERTStatement(value.LandblockInstanceLink.OrderBy(r => r.ChildGuid).ToList(), realmId, instanceWcids, writer);
                }
            }
        }

        private void CreateSQLINSERTStatement(IList<LandblockInstanceLink> input, ushort realmId, Dictionary<uint, uint> instanceWcids, StreamWriter writer)
        {
            writer.WriteLine("INSERT INTO `landblock_instance_link_realm` (`realm_id`, `parent_GUID`, `child_GUID`)");

            var lineGenerator = new Func<int, string>(i =>
            {
                string label = null;

                if (WeenieNames != null && instanceWcids.TryGetValue(input[i].ChildGuid, out var wcid) && WeenieNames.TryGetValue(wcid, out var weenieName))
                    label = $"{weenieName} ({wcid})";

                if (label != null)
                {
                    label = $" /* {label} */";
                }

                return $"{realmId}, 0x{input[i].ParentGuid.ToString("X8")}, 0x{input[i].ChildGuid.ToString("X8")}){label}";
            });

            ValuesWriter(input.Count, lineGenerator, writer);
        }
    }
}
