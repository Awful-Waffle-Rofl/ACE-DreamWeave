using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>One town a player can reach from the Town Network, keyed by where its portal drops them.</summary>
    public readonly struct WorldEventTown
    {
        public string Name { get; }

        /// <summary>Global (world-space) X/Y in metres, from PositionExtensions.ToGlobal.</summary>
        public float GlobalX { get; }
        public float GlobalY { get; }

        public WorldEventTown(string name, float globalX, float globalY)
        {
            Name = name;
            GlobalX = globalX;
            GlobalY = globalY;
        }
    }

    /// <summary>
    /// Maps a world position to the nearest town a player already knows how to reach, so an announcement can
    /// say "Holtburg (43.2N, 34.0E)" instead of bare coordinates. Players do not navigate to
    /// coordinates; they navigate to Town Network portals - so the town list IS the Town Network:
    /// every Portal placed in landblock 0x0007, named from its portal name, positioned at its destination.
    ///
    /// The list is loaded from the world database once at boot (WorldEventManager.Initialize) and again on
    /// "/worldevent reload", so a portal added to the network (the fork's Crater Portal, wcid 1001920, is
    /// one) joins the mapping without a code change. Everything below the load is pure and unit-tested (D6):
    /// tests hand <see cref="Describe(Position, IReadOnlyList{WorldEventTown})"/> their own town list.
    /// </summary>
    public static class WorldEventTownIndex
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The Town Network dungeon landblock.</summary>
        public const ushort TownNetworkLandblock = 0x0007;

        private static readonly object sync = new object();

        private static IReadOnlyList<WorldEventTown> towns = Array.Empty<WorldEventTown>();

        /// <summary>The loaded town list; empty until <see cref="Load"/> succeeds.</summary>
        public static IReadOnlyList<WorldEventTown> Towns
        {
            get { lock (sync) return towns; }
        }

        /// <summary>Test seam - swaps the live list. Pass null to clear.</summary>
        public static void SetTowns(IEnumerable<WorldEventTown> list)
        {
            lock (sync) towns = list?.ToList() ?? (IReadOnlyList<WorldEventTown>)Array.Empty<WorldEventTown>();
        }

        // ---- load (DB-touching, thin) ---------------------------------------------------------------

        /// <summary>
        /// Rebuilds the list from the world database's landblock_instance rows for the Town Network. Never
        /// throws: a failure logs and keeps whatever list was there before (empty on first boot), and
        /// <see cref="Describe(Position)"/> then degrades to bare coordinates.
        /// </summary>
        public static int Load()
        {
            try
            {
                var instances = DatabaseManager.World.GetCachedInstancesByLandblock(TownNetworkLandblock);

                var found = new List<WorldEventTown>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var instance in instances)
                {
                    var weenie = DatabaseManager.World.GetCachedWeenie(instance.WeenieClassId);

                    if (weenie == null || weenie.WeenieType != WeenieType.Portal)
                        continue;

                    var town = FromPortal(weenie.GetName(), weenie.GetPosition(PositionType.Destination));

                    if (town == null || !seen.Add(town.Value.Name))
                        continue;

                    found.Add(town.Value);
                }

                found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                lock (sync) towns = found;

                log.Info($"[WORLDEVENT] town index: {found.Count} towns from Town Network landblock 0x{TownNetworkLandblock:X4}");

                return found.Count;
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] town index load failed; announcements fall back to coordinates", ex);
                return -1;
            }
        }

        // ---- pure ---------------------------------------------------------------------------------

        /// <summary>
        /// One Town Network portal -> one town, or null when the portal is not a usable landmark: no name,
        /// no destination, or a destination that is indoors (the Marketplace and the Facility Hub have no
        /// map position, so naming them as the nearest town would be meaningless).
        /// </summary>
        public static WorldEventTown? FromPortal(string portalName, Position destination)
        {
            if (destination == null || destination.Indoors)
                return null;

            var name = NormalizeTownName(portalName);

            if (string.IsNullOrEmpty(name))
                return null;

            var global = destination.ToGlobal();

            return new WorldEventTown(name, global.X, global.Y);
        }

        /// <summary>
        /// "Portal to Holtburg" / "Holtburg Portal" -> "Holtburg"; anything else ("Asheron's Castle",
        /// "Danby's Outpost") is kept whole. Null/blank -> null.
        /// </summary>
        public static string NormalizeTownName(string portalName)
        {
            if (string.IsNullOrWhiteSpace(portalName))
                return null;

            var name = portalName.Trim();

            const string prefix = "Portal to";
            const string suffix = " Portal";

            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(prefix.Length);
            else if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - suffix.Length);

            name = name.Trim();

            return name.Length == 0 ? null : name;
        }

        /// <summary>The nearest town to a position, with the 2D distance in metres; null when the list is empty or the position is indoors.</summary>
        public static (WorldEventTown Town, float Distance)? Nearest(Position position, IReadOnlyList<WorldEventTown> list)
        {
            if (position == null || position.Indoors || list == null || list.Count == 0)
                return null;

            var global = position.ToGlobal();

            WorldEventTown? best = null;
            var bestSq = float.MaxValue;

            foreach (var town in list)
            {
                var dx = town.GlobalX - global.X;
                var dy = town.GlobalY - global.Y;
                var sq = dx * dx + dy * dy;

                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = town;
                }
            }

            return best == null ? null : (best.Value, (float)Math.Sqrt(bestSq));
        }

        /// <summary>
        /// Player-facing place text against the LIVE town list: the nearest town, then the exact map
        /// coordinates of the event centre in brackets. Spliced after "at" and "near" by the announcement
        /// composers.
        ///   "Holtburg (43.2N, 34.0E)"     - nearest town + the event's own coordinates
        ///   "43.2N, 34.0E"                - no towns loaded
        ///   "an underground location"     - indoors, no map coordinates
        /// </summary>
        public static string Describe(Position position) => Describe(position, Towns);

        /// <summary>Pure form of <see cref="Describe(Position)"/> against an explicit town list.</summary>
        public static string Describe(Position position, IReadOnlyList<WorldEventTown> list)
        {
            var coords = position?.GetMapCoordStr();

            if (coords == null)
                return "an underground location";

            var nearest = Nearest(position, list);

            if (nearest == null)
                return coords;

            return $"{nearest.Value.Town.Name} ({coords})";
        }
    }
}
