using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Database.LoadTest
{
    /// <summary>
    /// Builds throwaway, non-player Biota entities for load testing. Guids are drawn from the top of the
    /// dynamic object range (0xFFF00000+) to avoid colliding with real dynamic objects on a live-populated database.
    /// Never run this against a database you care about.
    /// </summary>
    public static class SyntheticBiotaFactory
    {
        public const uint TestGuidRangeStart = 0xFFF00000;

        /// <summary>
        /// Separate, lower reserved range for persistent seed data (landblock population etc.), so it never
        /// collides with the transient per-run ids above, which other scenarios create and clean up.
        /// </summary>
        public const uint SeedDynamicRangeMin = 0xFFE00000;
        public const uint SeedDynamicRangeMax = 0xFFEFFFFF;

        /// <summary>
        /// Reserved range for bulk-volume seed data (inflating total table size to test unindexed queries).
        /// Below SeedDynamicRangeMin so it never overlaps.
        /// </summary>
        public const uint SeedBulkRangeMin = 0xFFD00000;
        public const uint SeedBulkRangeMax = 0xFFDFFFFF;

        /// <summary>
        /// propertyGroups controls how many of the property-table collections get populated (0-7), so scenarios
        /// can vary how many extra round trips ShardDatabase.GetBiota has to make to reassemble the object.
        /// </summary>
        public static Biota Create(uint id, int propertyGroups = 3)
        {
            var biota = new Biota
            {
                Id = id,
                WeenieClassId = 1, // "clay", a lightweight always-present weenie
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, $"LoadTestObject_{id:X8}" } }
            };

            if (propertyGroups >= 1)
                biota.PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.EncumbranceVal, 1 }, { PropertyInt.Value, 1 } };

            if (propertyGroups >= 2)
                biota.PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.HealthRate, 1.0 } };

            if (propertyGroups >= 3)
                biota.PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.Stuck, false } };

            if (propertyGroups >= 4)
                biota.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.Setup, 0x02000001 } };

            if (propertyGroups >= 5)
                biota.PropertiesIID = new Dictionary<PropertyInstanceId, uint> { { PropertyInstanceId.Container, 0 } };

            if (propertyGroups >= 6)
                biota.PropertiesInt64 = new Dictionary<PropertyInt64, long> { { PropertyInt64.TotalExperience, 0 } };

            if (propertyGroups >= 7)
            {
                biota.PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
                {
                    { PositionType.Location, new PropertiesPosition { ObjCellId = 0x7D640001, PositionX = 0, PositionY = 0, PositionZ = 0, RotationW = 1 } }
                };
            }

            return biota;
        }

        /// <summary>
        /// Maximally-populated biota - multiple entries per dictionary/collection, not just one - for
        /// integrity-check's round-trip comparison. The point is to stress every property table ShardDatabase.GetBiota
        /// has to reassemble, so a future rewrite of that N+1 pattern has something thorough to be checked against.
        /// </summary>
        public static Biota CreateRich(uint id)
        {
            return new Biota
            {
                Id = id,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, $"LoadTestObject_{id:X8}" },
                    { PropertyString.Title, "Integrity Check Title" },
                    { PropertyString.Inscription, "Integrity Check Inscription" }
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.EncumbranceVal, 111 },
                    { PropertyInt.Value, 222 },
                    { PropertyInt.Mass, 333 }
                },
                PropertiesInt64 = new Dictionary<PropertyInt64, long>
                {
                    { PropertyInt64.TotalExperience, 1_000_000 },
                    { PropertyInt64.AvailableExperience, 2_000_000 },
                    { PropertyInt64.AvailableLuminance, 3_000_000 }
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.HeartbeatInterval, 1.5 },
                    { PropertyFloat.HeartbeatTimestamp, 2.5 },
                    { PropertyFloat.HealthRate, 3.5 }
                },
                PropertiesBool = new Dictionary<PropertyBool, bool>
                {
                    { PropertyBool.Stuck, true },
                    { PropertyBool.Open, false },
                    { PropertyBool.Locked, true }
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Setup, 0x02000001 },
                    { PropertyDataId.MotionTable, 0x09000002 },
                    { PropertyDataId.SoundTable, 0x09000003 }
                },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>
                {
                    { PropertyInstanceId.Container, 0x50000001 },
                    { PropertyInstanceId.Wielder, 0x50000002 },
                    { PropertyInstanceId.Viewer, 0x50000003 }
                },
                PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
                {
                    { PositionType.Location, new PropertiesPosition { ObjCellId = 0x7D640001, PositionX = 10.1f, PositionY = 20.2f, PositionZ = 30.3f, RotationW = 1, RotationX = 0.1f, RotationY = 0.2f, RotationZ = 0.3f } },
                    { PositionType.Destination, new PropertiesPosition { ObjCellId = 0x7D640002, PositionX = 40.4f, PositionY = 50.5f, PositionZ = 60.6f, RotationW = 1 } }
                },
                // These three exercise the trickiest paths in ShardDatabase.PopulateBiotaCollections: Emote is a
                // nested one-to-many under a one-to-many, and Allegiance/HousePermissions are keyed by
                // AllegianceId/HouseId rather than ObjectId.
                PropertiesEmote = new List<PropertiesEmote>
                {
                    new PropertiesEmote
                    {
                        Category = EmoteCategory.Refuse,
                        Probability = 1.0f,
                        PropertiesEmoteAction = new List<PropertiesEmoteAction>
                        {
                            new PropertiesEmoteAction { Type = 1, Delay = 0.5f, Extent = 1.0f, Message = "Integrity check emote action" }
                        }
                    }
                },
                PropertiesAllegiance = new Dictionary<uint, PropertiesAllegiance>
                {
                    { 0x50000001, new PropertiesAllegiance { Banned = true, ApprovedVassal = false } }
                },
                HousePermissions = new Dictionary<uint, bool>
                {
                    { 0x50000002, true }
                }
            };
        }

        /// <summary>
        /// Same as Create, but stamps the object into a container's possession chain by setting
        /// PropertiesIID[Container] = containerId - ShardDatabase.GetInventoryInParallel finds inventory by
        /// querying BiotaPropertiesIID for Type == PropertyInstanceId.Container && Value == parentId, and recurses
        /// into any child whose WeenieType == WeenieType.Container. isContainer marks this object as one of those
        /// recursion points (a "pack" holding further items), rather than a leaf item.
        /// </summary>
        public static Biota CreatePossession(uint id, uint containerId, bool isContainer, int propertyGroups = 7)
        {
            var biota = Create(id, propertyGroups);

            if (isContainer)
                biota.WeenieType = WeenieType.Container;

            // Create only populates PropertiesIID (with Container=0) at propertyGroups >= 5, so the dictionary
            // may not exist yet here - ensure it does, then overwrite Container with the real parent id regardless
            // of propertyGroups, since the whole point of this factory method is to be found by that lookup.
            biota.PropertiesIID ??= new Dictionary<PropertyInstanceId, uint>();
            biota.PropertiesIID[PropertyInstanceId.Container] = containerId;

            return biota;
        }

        /// <summary>
        /// Same as Create, but also stamps a Location position inside the given landblock, so it shows up in
        /// ShardDatabase.GetDynamicObjectsByLandblock (which matches on BiotaPropertiesPosition, not on guid range).
        /// </summary>
        public static Biota CreateDynamic(uint id, ushort landblockId, uint cellOffset, int propertyGroups = 3)
        {
            var biota = Create(id, propertyGroups);

            biota.PropertiesPosition = new Dictionary<PositionType, PropertiesPosition>
            {
                {
                    PositionType.Location, new PropertiesPosition
                    {
                        ObjCellId = (uint)(landblockId << 16) | (cellOffset & 0xFFFF),
                        PositionX = 0, PositionY = 0, PositionZ = 0, RotationW = 1
                    }
                }
            };

            return biota;
        }
    }
}
