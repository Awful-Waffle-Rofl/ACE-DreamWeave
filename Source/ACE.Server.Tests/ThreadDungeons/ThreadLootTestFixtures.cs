using System;
using System.Collections.Generic;
using System.Threading;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The one set of builders the pooled-loot tests share, so every test class builds the same run shape and
    /// draws guids from one counter. Task 1 creates it with the run builder; Task 2 adds Item and Task 4 adds
    /// PooledRun and Cache. Test classes import it with `using static` and keep no private copies.
    /// </summary>
    internal static class ThreadLootTestFixtures
    {
        // Static-range guids: in-memory objects that carry one Destroy() without the database (ContainerStackTests.cs:14-20).
        private static int nextGuid = 0x7F100000;

        public static uint NextGuid() => (uint)Interlocked.Increment(ref nextGuid);

        /// <summary>A generic in-memory item with a static-range guid.</summary>
        public static WorldObject Item(uint wcid = 30000)
            => new GenericObject(new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic }, new ObjectGuid(NextGuid()));

        /// <summary>A run stamped with the pooled-loot switch ON.</summary>
        public static ThreadDungeonRun PooledRun() => NewRun(pooled: true);

        /// <summary>An in-memory Thread Cache stand-in: a wcid 1003603 Container with ItemsCapacity.</summary>
        public static Container Cache(int capacity = 120)
            => new Container(new Weenie
            {
                WeenieClassId = 1003603,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemsCapacity, capacity } },
            }, new ObjectGuid(NextGuid()));

        /// <summary>
        /// The filos_doom run shape (ThreadDungeonManagerRulesTests.NewRun builds the same one at gem level 100).
        /// Stamped with the pooled-loot switch only when <paramref name="pooled"/> has a value, so NewRun() is a
        /// run that never reached populate.
        /// </summary>
        public static ThreadDungeonRun NewRun(bool? pooled = null)
        {
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));

            if (pooled.HasValue)
                run.MarkPooledLoot(pooled.Value);

            return run;
        }
    }
}
