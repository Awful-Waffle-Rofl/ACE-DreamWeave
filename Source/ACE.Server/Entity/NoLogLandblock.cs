using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Retail's "lifestone on relog" rule, kept off <see cref="WorldObjects.Player"/> so it can be unit tested:
    /// Player's static initializer reads the world database, which no test can provide.
    /// </summary>
    public static class NoLogLandblock
    {
        /// <summary>
        /// A list of landblocks the player cannot relog directly into
        /// 
        /// If a regular player logs out in one of these landblocks,
        /// they will be transported back to the lifestone when they log back in.
        /// </summary>
        public static readonly HashSet<ushort> Landblocks = new HashSet<ushort>()
        {
            // https://asheron.fandom.com/wiki/Special:Search?query=Lifestone+on+Relog%3A+Yes+
            // https://docs.google.com/spreadsheets/d/122xOw3IKCezaTDjC_hggWSVzYJ_9M_zUUtGEXkwNXfs/edit#gid=846612575

            0x0002,     // Viamontian Garrison
            0x0007,     // Town Network
            0x0056,     // Augmentation Realm Main Level
            0x005F,     // Tanada House of Pancakes (Seasonal)
            0x0067,     // PKL Arena
            0x006D,     // Augmentation Realm Upper Level
            0x007D,     // Augmentation Realm Lower Level
            0x00AB,     // Derethian Combat Arena
            0x00AC,     // Derethian Combat Arena
            0x00C3,     // Blighted Putrid Moarsman Tunnels
            0x00D7,     // Jester's Prison
            0x00EA,     // Mhoire Armory
            0x015D,     // Mountain Cavern
            0x027F,     // East Fork Dam Hive
            0x03A7,     // Mount Elyrii Hive
            0x5764,     // Oubliette of Mhoire Castle
            0x634C,     // Tainted Grotto
            0x6544,     // Greater Battle Dungeon
            0x6651,     // Hoshino Tower
            0x7E04,     // Thug Hideout
            0x8A04,     // Night Club (Seasonal Anniversary)
            0x8B04,     // Frozen Wight Lair
            0x9EE5,     // Northwatch Castle Black Market
            0xB5F0,     // Aerfalle's Sanctum
            0xF92F,     // Freebooter Keep Black Market
            0x00B0,     // Colosseum Arena One
            0x00B1,     // Colosseum Arena Two
            0x00B2,     // Colosseum Arena Three
            0x00B3,     // Colosseum Arena Four
            0x00B4,     // Colosseum Arena Five
            0x00B6,     // Colosseum Arena Mini-Bosses
            0x5960,     // Gauntlet Arena One (Celestial Hand)
            0x5961,     // Gauntlet Arena Two (Celestial Hand)
            0x5962,     // Gauntlet Arena One (Eldritch Web)
            0x5963,     // Gauntlet Arena Two (Eldritch Web)
            0x5964,     // Gauntlet Arena One (Radiant Blood)
            0x5965,     // Gauntlet Arena Two (Radiant Blood)
        };

        /// <summary>
        /// Called when a player first logs in
        /// </summary>
        public static void Apply(Biota biota, out bool playerWasMovedFromNoLogLandblock)
        {
            playerWasMovedFromNoLogLandblock = false;

            if (biota.WeenieType == WeenieType.Sentinel || biota.WeenieType == WeenieType.Admin) return;

            if (!biota.PropertiesPosition.TryGetValue(PositionType.Location, out var location))
                return;

            var landblock = (ushort)(location.ObjCellId >> 16);

            if (!Landblocks.Contains(landblock))
                return;

            if (!biota.PropertiesPosition.TryGetValue(PositionType.Sanctuary, out var lifestone))
                return;

            location.ObjCellId = lifestone.ObjCellId;
            location.PositionX = lifestone.PositionX;
            location.PositionY = lifestone.PositionY;
            location.PositionZ = lifestone.PositionZ;
            location.RotationX = lifestone.RotationX;
            location.RotationY = lifestone.RotationY;
            location.RotationZ = lifestone.RotationZ;
            location.RotationW = lifestone.RotationW;
            // WaffleACE: the lifestone's INSTANCE has to travel with its cell. Without this a player who
            // logged out in a realm copy of a no-log block (the Drift Network hub is realm 1's 0x0007)
            // kept the realm-1 instance on a realm-0 lifestone cell, and logged in to an empty realm-1
            // copy of their lifestone's landblock with no server objects at all.
            location.Instance = lifestone.Instance;

            playerWasMovedFromNoLogLandblock = true;

            return;
        }
    }
}
