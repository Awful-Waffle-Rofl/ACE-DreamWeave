using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP3b, R7-R9: the outdoor content allow-list (doors and non-interactive decor only), the run's ONE
    /// click-only exit and the load-time assertion that a copy holds exactly one Portal, and doors forced open and
    /// unlocked before EnterWorld. The dungeon filter (ThreadDungeonContentFilterTests) is untouched by this mode.
    /// </summary>
    [TestClass]
    public class OutdoorContentFilterTests
    {
        private const uint OutdoorExit = 1003604;
        private const ushort Primary = 0x5456;
        private const ushort East = 0x5556;

        private static LandblockInstance Row(uint guid, uint wcid, ushort landblock = Primary) => new LandblockInstance
        {
            Guid = guid, WeenieClassId = wcid, ObjCellId = ((uint)landblock << 16) | 0x0001u, OriginX = 1, OriginY = 2, OriginZ = 3, AnglesW = 1,
            IsLinkChild = false, Landblock = landblock,
        };

        // wcid -> type. 1 = fixed (Stuck) decor Generic, 20 = interactive Generic (a sign or lever), 21 = a loose, non-Stuck Generic, 9 = generator Generic.
        private static readonly Dictionary<uint, WeenieType> Types = new Dictionary<uint, WeenieType>
        {
            [1] = WeenieType.Generic, [2] = WeenieType.Creature, [3] = WeenieType.Door, [4] = WeenieType.Switch,
            [5] = WeenieType.Portal, [6] = WeenieType.Chest, [7] = WeenieType.HotSpot, [8] = WeenieType.PressurePlate,
            [9] = WeenieType.Generic, [14] = WeenieType.HousePortal, [15] = WeenieType.LifeStone, [16] = WeenieType.Vendor,
            [17] = WeenieType.House, [20] = WeenieType.Generic, [21] = WeenieType.Generic,
        };

        private static WeenieType TypeOf(uint wcid) => Types.TryGetValue(wcid, out var t) ? t : throw new KeyNotFoundException($"wcid {wcid}");
        private static bool IsGenerator(uint wcid) => wcid == 9;
        private static bool IsFixedDecor(uint wcid) => wcid == 1;

        private static List<LandblockInstance> Filter(IReadOnlyList<LandblockInstance> source, LandblockInstance exit, out ThreadDungeonFilterStats stats)
            => ThreadDungeonContentFilter.FilterOutdoor(source, TypeOf, IsGenerator, IsFixedDecor, exit, out stats);

        // ---- the allow-list ----

        [TestMethod]
        public void Every_type_but_Door_and_decor_Generic_is_dropped()
        {
            var cases = new (uint Wcid, string Name, bool Kept)[]
            {
                (15, "LifeStone", false), (16, "Vendor", false), (17, "House", false), (14, "HousePortal", false), (5, "Portal", false),
                (7, "HotSpot", false), (8, "PressurePlate", false), (4, "Switch", false), (20, "interactive Generic", false),
                (9, "generator Generic", false), (2, "Creature", false), (6, "Chest", false), (21, "non-Stuck Generic", false),
                (1, "fixed decor Generic", true), (3, "Door", true),
            };

            foreach (var (wcid, name, kept) in cases)
            {
                var source = new List<LandblockInstance> { Row(0x7545600A, wcid) };
                var result = Filter(source, null, out _);

                if (kept)
                    Assert.IsTrue(result.Count == 1 && ReferenceEquals(result[0], source[0]), $"{name} must be kept, as the same row object");
                else
                    Assert.AreEqual(0, result.Count, $"{name} must be dropped by the outdoor allow-list");
            }
        }

        [TestMethod]
        public void Drops_are_counted_by_kind_and_unresolved_wcids_never_throw()
        {
            var source = new List<LandblockInstance>
            {
                Row(0x75456001, 1), Row(0x75456002, 3), Row(0x75456003, 5), Row(0x75456004, 14), Row(0x75456005, 9),
                Row(0x75456006, 2), Row(0x75456007, 6), Row(0x75456008, 7), Row(0x75456009, 20), Row(0x7545600A, 999),
            };

            Filter(source, null, out var stats);

            Assert.AreEqual(2, stats.Kept);
            Assert.AreEqual(2, stats.DroppedPortals, "Portal and HousePortal");
            Assert.AreEqual(1, stats.DroppedGenerators);
            Assert.AreEqual(1, stats.DroppedCreatures);
            Assert.AreEqual(1, stats.DroppedContainers);
            Assert.AreEqual(2, stats.DroppedOther, "HotSpot and the interactive Generic");
            Assert.AreEqual(1, stats.DroppedUnresolved);
            Assert.AreEqual(0, stats.PlacedExit);
            StringAssert.StartsWith(stats.OutdoorSummary(), "outdoor kept=2");
        }

        [TestMethod]
        public void The_filter_does_not_mutate_its_source()
        {
            var source = new List<LandblockInstance> { Row(0x75456001, 5), Row(0x75456002, 3) };
            Filter(source, null, out _);
            Assert.AreEqual(2, source.Count);
            Assert.AreEqual(5u, source[0].WeenieClassId);
        }

        [TestMethod]
        public void A_Generic_is_interactive_when_usable_or_carrying_emotes()
        {
            Assert.IsFalse(ThreadDungeonContentFilter.IsInteractiveGeneric(null, 0), "no ItemUseable, no emotes: decor");
            Assert.IsFalse(ThreadDungeonContentFilter.IsInteractiveGeneric(Usable.Undef, 0));
            Assert.IsFalse(ThreadDungeonContentFilter.IsInteractiveGeneric(Usable.No, 0));
            Assert.IsTrue(ThreadDungeonContentFilter.IsInteractiveGeneric(Usable.Remote, 0), "a sign or lever a player can Use");
            Assert.IsTrue(ThreadDungeonContentFilter.IsInteractiveGeneric(null, 1), "any emote row");
        }

        // ---- F10: a Generic is kept only as FIXED decor ----

        [TestMethod]
        public void A_non_Stuck_Generic_is_dropped_and_a_Stuck_non_interactive_one_is_kept()
        {
            // Jester's Marbles (37493): Generic, ItemUseable No, no emotes, not Stuck. A loose item, not scenery.
            Assert.IsFalse(ThreadDungeonContentFilter.IsFixedDecorGeneric(Usable.No, 0, stuck: false), "the marbles shape");
            Assert.IsTrue(ThreadDungeonContentFilter.IsFixedDecorGeneric(Usable.No, 0, stuck: true), "control: Stuck decor");
            Assert.IsTrue(ThreadDungeonContentFilter.IsFixedDecorGeneric(null, 0, stuck: true), "control: Stuck decor with no ItemUseable");
            Assert.IsFalse(ThreadDungeonContentFilter.IsFixedDecorGeneric(Usable.Remote, 0, stuck: true), "a Stuck lever is still interactive");
            Assert.IsFalse(ThreadDungeonContentFilter.IsFixedDecorGeneric(null, 2, stuck: true), "a Stuck sign with emotes is still interactive");

            // Through the filter: the loose Generic goes, the fixed one stays (the same row object).
            var source = new List<LandblockInstance> { Row(0x75456001, 21), Row(0x75456002, 1) };
            var kept = Filter(source, null, out var stats);
            CollectionAssert.AreEqual(new[] { source[1] }, kept);
            Assert.AreEqual(1, stats.DroppedOther);
        }

        /// <summary>
        /// The premise F10 corrects, pinned so it is not mistaken for the whole story: a landblock row is built with its OWN
        /// static guid, and the pick-up handler refuses a non-dynamic guid before it looks at Stuck. The fixed-decor rule is
        /// defence in depth on top of that, not the only thing keeping a loose static item in place.
        /// </summary>
        [TestMethod]
        public void A_landblock_static_is_built_with_its_static_guid_and_pick_up_refuses_non_dynamic_guids()
        {
            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/Factories/WorldObjectFactory.cs"), "var guid = new ObjectGuid(instance.Guid);");
            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Inventory.cs"), "if (!item.Guid.IsDynamic() || item is Creature || item.Stuck)");
            Assert.IsTrue(ObjectGuid.IsStatic(0x7482E3C8u), "the marbles' row guid is in the static range");
            Assert.IsFalse(ObjectGuid.IsDynamic(0x7482E3C8u));
        }

        // ---- the exit (R8) ----

        private const string ExitText = "0x54560014 120.5 60.25 10 0.7071 0 0 0.7071";

        [TestMethod]
        public void The_exit_row_lands_at_the_exit_position_with_its_rotation()
        {
            var row = ThreadDungeonContentFilter.OutdoorExitRow(ExitText, Primary, OutdoorExit, new List<LandblockInstance>());

            Assert.IsNotNull(row);
            Assert.AreEqual(OutdoorExit, row.WeenieClassId);
            Assert.AreEqual(0x54560014u, row.ObjCellId);
            Assert.AreEqual(120.5f, row.OriginX);
            Assert.AreEqual(60.25f, row.OriginY);
            Assert.AreEqual(10f, row.OriginZ);
            Assert.AreEqual(0.7071f, row.AnglesW);
            Assert.AreEqual(0.7071f, row.AnglesZ);
            Assert.AreEqual((int)Primary, row.Landblock.Value);
            Assert.IsFalse(row.IsLinkChild);
        }

        [TestMethod]
        public void The_exit_row_belongs_only_to_the_landblock_holding_the_exit()
        {
            Assert.IsNull(ThreadDungeonContentFilter.OutdoorExitRow(ExitText, East, OutdoorExit, new List<LandblockInstance>()));
            Assert.IsNull(ThreadDungeonContentFilter.OutdoorExitRow("not a position", Primary, OutdoorExit, new List<LandblockInstance>()));
            Assert.IsNull(ThreadDungeonContentFilter.OutdoorExitRow(null, Primary, OutdoorExit, new List<LandblockInstance>()));
        }

        [TestMethod]
        public void The_exit_takes_the_highest_static_guid_no_row_uses()
        {
            var top = 0x75456FFFu;
            var row = ThreadDungeonContentFilter.OutdoorExitRow(ExitText, Primary, OutdoorExit, new List<LandblockInstance> { Row(top, 1), Row(0x75456FFE, 3) });
            Assert.AreEqual(0x75456FFDu, row.Guid);

            Assert.AreEqual(0x75456FFFu, ThreadDungeonContentFilter.OutdoorExitRow(ExitText, Primary, OutdoorExit, null).Guid);
        }

        [TestMethod]
        public void Exactly_one_Portal_across_a_two_landblock_copy()
        {
            // Both landblocks carry a retail Portal and a HousePortal; only the exit landblock receives the exit.
            var primarySource = new List<LandblockInstance> { Row(0x75456001, 5), Row(0x75456002, 14), Row(0x75456003, 3) };
            var eastSource = new List<LandblockInstance> { Row(0x75556001, 5, East), Row(0x75556002, 1, East) };

            var primaryExit = ThreadDungeonContentFilter.OutdoorExitRow(ExitText, Primary, OutdoorExit, primarySource);
            var eastExit = ThreadDungeonContentFilter.OutdoorExitRow(ExitText, East, OutdoorExit, eastSource);

            var primary = Filter(primarySource, primaryExit, out var primaryStats);
            var east = Filter(eastSource, eastExit, out var eastStats);

            var portals = primary.Concat(east).Count(r => r.WeenieClassId == OutdoorExit || TypeOf(r.WeenieClassId) is WeenieType.Portal or WeenieType.HousePortal);
            Assert.AreEqual(1, portals, "the copy holds exactly one Portal: the exit");
            Assert.AreEqual(1, primaryStats.PlacedExit);
            Assert.AreEqual(0, eastStats.PlacedExit);
            Assert.AreSame(primaryExit, primary[primary.Count - 1], "the exit row is appended");

            Assert.IsNull(ThreadDungeonContentFilter.OutdoorPortalCountError(true, 1));
            Assert.IsNull(ThreadDungeonContentFilter.OutdoorPortalCountError(false, 0));
        }

        [TestMethod]
        public void The_portal_count_assertion_reports_every_wrong_count()
        {
            Assert.IsNotNull(ThreadDungeonContentFilter.OutdoorPortalCountError(true, 0), "the exit landblock built no exit");
            Assert.IsNotNull(ThreadDungeonContentFilter.OutdoorPortalCountError(true, 2), "a second portal beside the exit");
            Assert.IsNotNull(ThreadDungeonContentFilter.OutdoorPortalCountError(false, 1), "a portal on a non-exit member");
        }

        // ---- F5: a missing exit is detected, not read as "0 portals, as expected" ----

        [TestMethod]
        public void A_malformed_exit_position_is_an_error_on_every_member()
        {
            foreach (var bad in new[] { "garbage", "", null, "0x5456 1 2", "0xZZZZ0014 1 2 3" })
            {
                Assert.IsNotNull(ThreadDungeonContentFilter.OutdoorExitError(bad, Primary, null), $"'{bad}' on the primary");
                Assert.IsNotNull(ThreadDungeonContentFilter.OutdoorExitError(bad, East, null), $"'{bad}' on another member");
                Assert.IsFalse(ThreadDungeonContentFilter.ExitIsOnLandblock(bad, Primary), "an unparseable exit is on no landblock");
            }
        }

        [TestMethod]
        public void The_exit_landblock_without_an_exit_row_is_an_error_and_the_rest_are_not()
        {
            var row = ThreadDungeonContentFilter.OutdoorExitRow(ExitText, Primary, OutdoorExit, new List<LandblockInstance>());

            Assert.IsNull(ThreadDungeonContentFilter.OutdoorExitError(ExitText, Primary, row), "control: the exit landblock with its row");
            Assert.IsNotNull(ThreadDungeonContentFilter.OutdoorExitError(ExitText, Primary, null), "the exit landblock with no row (guid range full)");
            Assert.IsNull(ThreadDungeonContentFilter.OutdoorExitError(ExitText, East, null), "another member holds no exit and needs none");

            // The expected portal count follows where the exit IS, so a missing row reads as 0 of 1, never 0 of 0.
            Assert.IsTrue(ThreadDungeonContentFilter.ExitIsOnLandblock(ExitText, Primary));
            Assert.IsFalse(ThreadDungeonContentFilter.ExitIsOnLandblock(ExitText, East));
            Assert.IsNotNull(ThreadDungeonContentFilter.OutdoorPortalCountError(ThreadDungeonContentFilter.ExitIsOnLandblock(ExitText, Primary), 0));
        }

        // ---- the Landblock wiring (source pins: CreateWorldObjects needs the world database) ----

        private static string CreateWorldObjectsBody()
            => PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Entity/Landblock.cs"), "private void CreateWorldObjects()");

        /// <summary>Null when the outdoor branch filters with FilterOutdoor, forces doors open and asserts the portal count before the objects are queued into the world.</summary>
        internal static string LandblockWiringFault(string body)
        {
            var outdoorFilter = body.IndexOf("ThreadDungeonContentFilter.FilterOutdoor(", StringComparison.Ordinal);
            var dungeonFilter = body.IndexOf("ThreadDungeonContentFilter.Filter(", StringComparison.Ordinal);
            var instanced = body.IndexOf("fo.Location.Instance = Instance;", StringComparison.Ordinal);
            var force = body.IndexOf("door.ForceOpenUnlocked();", StringComparison.Ordinal);
            var assertion = body.IndexOf("ThreadDungeonContentFilter.OutdoorPortalCountError(outdoorHoldsExit, portals);", StringComparison.Ordinal);
            var countThrow = body.IndexOf("throw new InvalidOperationException(portalError);", StringComparison.Ordinal);
            var exitCheck = body.IndexOf("ThreadDungeonContentFilter.OutdoorExitError(outdoorSite.Exit, Id.Landblock, outdoorExitRow);", StringComparison.Ordinal);
            var exitThrow = body.IndexOf("throw new InvalidOperationException(exitError);", StringComparison.Ordinal);
            var holds = body.IndexOf("outdoorHoldsExit = ACE.Server.ThreadDungeons.ThreadDungeonContentFilter.ExitIsOnLandblock(outdoorSite.Exit, Id.Landblock);", StringComparison.Ordinal);
            var enqueue = instanced < 0 ? -1 : body.IndexOf("actionQueue.EnqueueAction(", instanced, StringComparison.Ordinal);

            if (outdoorFilter < 0) return "the outdoor branch does not call FilterOutdoor";
            if (!body.Contains("ThreadDungeonContentFilter.IsFixedDecorGeneric(", StringComparison.Ordinal)
                || !body.Contains("weenie?.GetProperty(PropertyBool.Stuck) ?? false", StringComparison.Ordinal))
                return "F10: the Generic keep test is not fed the weenie's Stuck flag";
            if (dungeonFilter < 0) return "the dungeon filter is gone";
            if (force < 0) return "doors are not forced open";
            if (assertion < 0 || countThrow < assertion) return "a wrong portal count does not abandon the copy";
            if (exitCheck < 0 || exitThrow < exitCheck || exitCheck > outdoorFilter) return "a missing exit is not refused before the filter runs";
            if (holds < 0) return "the expected portal count is not taken from where the exit is";
            if (instanced < 0 || enqueue < 0) return "the enqueue anchor moved";
            if (force < instanced || force > enqueue) return "doors are not forced open between instancing and the world enqueue";
            if (assertion > enqueue) return "the portal assertion runs after the objects are queued";
            return null;
        }

        [TestMethod]
        public void CreateWorldObjects_filters_outdoor_forces_doors_and_asserts_the_exit()
        {
            Assert.IsNull(LandblockWiringFault(CreateWorldObjectsBody()));
        }

        [TestMethod]
        public void The_landblock_wiring_check_discriminates()
        {
            var body = CreateWorldObjectsBody();
            Assert.IsNotNull(LandblockWiringFault(body.Replace("door.ForceOpenUnlocked();", "door.Reset();")));
            Assert.IsNotNull(LandblockWiringFault(body.Replace("ThreadDungeonContentFilter.FilterOutdoor(", "ThreadDungeonContentFilter.Filter(")));
            Assert.IsNotNull(LandblockWiringFault(body.Replace("ThreadDungeonContentFilter.OutdoorPortalCountError(", "string.Concat(")));
            Assert.IsNotNull(LandblockWiringFault(body.Replace("throw new InvalidOperationException(portalError);", "portalError = null;")), "a count error that only logs");
            Assert.IsNotNull(LandblockWiringFault(body.Replace("throw new InvalidOperationException(exitError);", "exitError = null;")), "an exit error that only logs");
            Assert.IsNotNull(LandblockWiringFault(body.Replace("ThreadDungeonContentFilter.ExitIsOnLandblock(outdoorSite.Exit, Id.Landblock);", "true;")));
            Assert.IsNotNull(LandblockWiringFault(body.Replace("weenie?.GetProperty(PropertyBool.Stuck) ?? false", "true")), "F10: Stuck not read");
        }

        // ---- R9: the door itself ----

        private static void SetInherited(WorldObject wo, string name, object value)
        {
            var field = typeof(WorldObject).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            field.SetValue(wo, value);
        }

        internal static T Seeded<T>() where T : WorldObject
        {
            var wo = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            SetInherited(wo, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetInherited(wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetInherited(wo, "positionCache", new Dictionary<PositionType, Position>());
            SetInherited(wo, "ephemeralPositions", new Dictionary<PositionType, Position>());
            return wo;
        }

        /// <summary>DefaultLocked and DefaultOpen are protected on WorldObject.</summary>
        private static PropertyInfo Protected(string name)
        {
            var prop = typeof(WorldObject).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(prop, $"WorldObject.{name} was not found by reflection - has it been renamed?");
            return prop;
        }

        [TestMethod]
        public void ForceOpenUnlocked_leaves_a_locked_closed_door_open_and_never_relocking()
        {
            var door = Seeded<Door>();
            door.IsLocked = true;
            Protected("DefaultLocked").SetValue(door, true);
            Protected("DefaultOpen").SetValue(door, false);
            door.IsOpen = false;

            door.ForceOpenUnlocked();

            Assert.IsFalse(door.IsLocked, "unlocked");
            Assert.AreEqual(false, Protected("DefaultLocked").GetValue(door), "Reset must not re-lock it");
            Assert.AreEqual(true, Protected("DefaultOpen").GetValue(door), "Reset must reopen it");
            Assert.IsTrue(door.IsOpen, "open on creation");
            Assert.AreEqual(true, door.Ethereal, "an open door does not block");
            Assert.AreEqual(MotionCommand.On, door.CurrentMotionState.MotionState.ForwardCommand, "created in the open motion");
        }
    }
}
