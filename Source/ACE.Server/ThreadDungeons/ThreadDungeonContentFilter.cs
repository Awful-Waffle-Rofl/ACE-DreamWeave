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

        /// <summary>Outdoor mode only: Portal and HousePortal rows dropped (R7: no exit copies).</summary>
        public int DroppedPortals;

        /// <summary>Outdoor mode only: every other row the allow-list does not keep (HotSpot, Switch, Vendor, LifeStone, House, interactive Generic, ...).</summary>
        public int DroppedOther;

        /// <summary>Outdoor mode only: 1 when this landblock received the run's one synthesized exit row, else 0.</summary>
        public int PlacedExit;

        public override string ToString()
            => $"kept={Kept} generators={DroppedGenerators} creatures={DroppedCreatures} containers={DroppedContainers} portals->exit={ReplacedPortals} unresolved={DroppedUnresolved}";

        /// <summary>The outdoor filter's log summary. The dungeon summary (<see cref="ToString"/>) is unchanged.</summary>
        public string OutdoorSummary()
            => $"outdoor kept={Kept} generators={DroppedGenerators} creatures={DroppedCreatures} containers={DroppedContainers} portals={DroppedPortals} other={DroppedOther} unresolved={DroppedUnresolved} exit={PlacedExit}";
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

        /// <summary>
        /// The OUTDOOR allow-list (Open World Threads R7), for every footprint landblock of an outdoor run's copy. Pure and
        /// non-mutating, exactly like <see cref="Filter"/>. Deny by default: a row is kept only when it is
        ///   - a Door (R9 forces it open and unlocked later, before EnterWorld), or
        ///   - a Generic that is FIXED DECOR (<paramref name="isFixedDecor"/> true, <see cref="IsFixedDecorGeneric"/>: Stuck, and not a sign or lever).
        /// Everything else is dropped and counted: unresolved wcids, every generator (any type), Portal and HousePortal
        /// (no exit copies: R8 places exactly one exit, below), creatures, the container family, and the rest
        /// (HotSpot, PressurePlate, Switch, Vendor, LifeStone, House, SlumLord, interactive or non-Stuck Generic, ...).
        ///
        /// <paramref name="exitRow"/> is the run's ONE click-only exit (R8), built by <see cref="OutdoorExitRow"/> for the
        /// landblock that holds the site's exit position and null for every other landblock. It is appended last, so the
        /// copy's total Portal count is exactly one (<see cref="OutdoorPortalCountError"/> asserts it at load).
        /// </summary>
        public static List<LandblockInstance> FilterOutdoor(IReadOnlyList<LandblockInstance> source, Func<uint, WeenieType> weenieTypeOf,
            Func<uint, bool> isGenerator, Func<uint, bool> isFixedDecor, LandblockInstance exitRow, out ThreadDungeonFilterStats stats)
        {
            stats = new ThreadDungeonFilterStats();
            var result = new List<LandblockInstance>(source.Count + 1);

            foreach (var row in source)
            {
                WeenieType type;
                bool generator;
                bool fixedDecor;
                try
                {
                    type = weenieTypeOf(row.WeenieClassId);
                    generator = isGenerator(row.WeenieClassId);
                    fixedDecor = type == WeenieType.Generic && isFixedDecor(row.WeenieClassId);
                }
                catch (KeyNotFoundException)
                {
                    stats.DroppedUnresolved++;
                    continue;
                }

                if (generator) { stats.DroppedGenerators++; continue; }

                switch (type)
                {
                    case WeenieType.Door:
                        result.Add(row);
                        stats.Kept++;
                        continue;

                    case WeenieType.Generic:
                        if (!fixedDecor) { stats.DroppedOther++; continue; }
                        result.Add(row);
                        stats.Kept++;
                        continue;

                    case WeenieType.Portal:
                    case WeenieType.HousePortal:
                        stats.DroppedPortals++;
                        continue;

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
                        stats.DroppedOther++;
                        continue;
                }
            }

            if (exitRow != null)
            {
                result.Add(exitRow);
                stats.PlacedExit = 1;
            }

            return result;
        }

        /// <summary>
        /// R7's keep test for a Generic, pure (F10): FIXED decor only. Stuck is the per-weenie half of the server's own
        /// pick-up refusal (Player.HandleActionPutItemInContainer refuses a non-dynamic guid, a Creature, or a Stuck item),
        /// so a non-Stuck Generic is a loose item, not scenery, and is dropped; an interactive one (<see cref="IsInteractiveGeneric"/>)
        /// is dropped whatever its flags.
        /// </summary>
        public static bool IsFixedDecorGeneric(Usable? itemUseable, int emoteCount, bool stuck)
            => stuck && !IsInteractiveGeneric(itemUseable, emoteCount);

        /// <summary>
        /// R7's "non-interactive" test for a Generic weenie, pure over the two facts that make one interactive: an
        /// ItemUseable other than Undef/No (a player can Use it), or any emote rows (a Use, Activation or Talk reply).
        /// </summary>
        public static bool IsInteractiveGeneric(Usable? itemUseable, int emoteCount)
        {
            if (emoteCount > 0)
                return true;

            var usable = itemUseable ?? Usable.Undef;
            return usable != Usable.Undef && usable != Usable.No;
        }

        /// <summary>The first static guid of a landblock's range: 0x7XXYY000.</summary>
        internal static uint FirstStaticGuid(ushort landblock) => 0x70000000u | ((uint)landblock << 12);

        /// <summary>
        /// R8: the outdoor run's one exit row for <paramref name="landblock"/>, or null when the site's exit position
        /// (<paramref name="exitText"/>, the entry-text shape "0xLLLLCCCC x y z [qw qx qy qz]") is on another landblock or
        /// does not parse. The row takes the HIGHEST static guid of the landblock's range that no row in
        /// <paramref name="source"/> uses, so it can never collide with a retail static it shares the copy with; null
        /// when the whole range is taken. Pure.
        /// </summary>
        public static LandblockInstance OutdoorExitRow(string exitText, ushort landblock, uint exitPortalWcid, IReadOnlyList<LandblockInstance> source)
        {
            if (!OutdoorSiteGeometry.TryParsePositionText(exitText, out var cell, out var x, out var y, out var z))
                return null;

            if ((ushort)(cell >> 16) != landblock)
                return null;

            var parts = exitText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            float qw = 1f, qx = 0f, qy = 0f, qz = 0f;
            if (parts.Length >= 8
                && float.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pw)
                && float.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px)
                && float.TryParse(parts[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var py)
                && float.TryParse(parts[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pz))
            {
                qw = pw; qx = px; qy = py; qz = pz;
            }

            var used = new HashSet<uint>();
            if (source != null)
                foreach (var row in source)
                    used.Add(row.Guid);

            var first = FirstStaticGuid(landblock);
            uint guid = 0;
            for (var index = 0xFFFu; ; index--)
            {
                if (!used.Contains(first | index)) { guid = first | index; break; }
                if (index == 0) return null;
            }

            return new LandblockInstance
            {
                Guid = guid,
                WeenieClassId = exitPortalWcid,
                ObjCellId = cell,
                OriginX = (float)x,
                OriginY = (float)y,
                OriginZ = (float)z,
                AnglesW = qw,
                AnglesX = qx,
                AnglesY = qy,
                AnglesZ = qz,
                IsLinkChild = false,
                Landblock = landblock,
            };
        }

        /// <summary>Does the site's exit position (<paramref name="exitText"/>) lie on <paramref name="landblock"/>? False when it does not parse.</summary>
        public static bool ExitIsOnLandblock(string exitText, ushort landblock)
            => OutdoorSiteGeometry.TryParsePositionText(exitText, out var cell, out _, out _, out _) && (ushort)(cell >> 16) == landblock;

        /// <summary>
        /// R8, before anything is built, pure: null when this footprint landblock's exit is in order, else the error that
        /// must abandon the copy. Every member refuses an exit position that does not parse (no member could hold the
        /// exit, and a count check alone would read that as "0 portals everywhere, as expected"), and the member the exit
        /// names refuses a null exit row (its whole static guid range is taken).
        /// </summary>
        public static string OutdoorExitError(string exitText, ushort landblock, LandblockInstance exitRow)
        {
            if (!OutdoorSiteGeometry.TryParsePositionText(exitText, out var cell, out _, out _, out _))
                return $"outdoor copy: the site's exit position '{exitText}' does not parse, so the run would have no exit";

            if ((ushort)(cell >> 16) == landblock && exitRow == null)
                return $"outdoor copy: landblock 0x{landblock:X4} holds the exit position but built no exit row (no free static guid)";

            return null;
        }

        /// <summary>
        /// R8's load-time assertion for one footprint landblock, pure: the landblock holding the site's exit must have
        /// built exactly ONE Portal, every other footprint landblock none, so the copy as a whole holds exactly one.
        /// <paramref name="holdsExit"/> is where the exit IS (<see cref="ExitIsOnLandblock"/>), never whether a row was
        /// built, so a missing exit reads as 0 of 1. Null when the count is right, else the error that abandons the copy.
        /// </summary>
        public static string OutdoorPortalCountError(bool holdsExit, int portalCount)
        {
            var expected = holdsExit ? 1 : 0;
            if (portalCount == expected)
                return null;

            return holdsExit
                ? $"outdoor copy: the exit landblock built {portalCount} Portal object(s); exactly 1 (the click-only exit) is required"
                : $"outdoor copy: a non-exit footprint landblock built {portalCount} Portal object(s); it must hold none";
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
