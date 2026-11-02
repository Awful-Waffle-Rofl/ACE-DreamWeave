using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;

using log4net;

namespace ACE.Server.WorldEvents.Hold
{
    /// <summary>
    /// World Events Hold H5: where a Hold run may really start. Applied by WorldEventManager.TryStart (through
    /// WorldEventObjectiveFactory.StartSpotRuleFor) after composing and before the run is built:
    ///
    ///   1. protect.clearArea: the asked anchor, else the nearest open spot within clearArea.searchRadius, must have room for
    ///      the knights' ring and the portals (WorldEventSpawnArea.TrySearch);
    ///   2. townReach: that spot must be within townReach.maxWalk of a Town Network drop; when it is not (or the clear-area
    ///      search failed), the start re-rolls to spots spotMinDistance to spotMaxDistance from the nearest townReach.towns
    ///      towns, each checked the same way.
    ///
    /// A spot that passes moves the composition's anchor IN PLACE (the AnchorPosition object is shared with nothing yet) and
    /// leaves the portal plan on WorldEventComposition.AreaPlan for the objective. The asked spot is always tried first (an
    /// admin's "--here" included); when it fails reach or the clear area the start re-rolls near towns, and when that finds
    /// nothing either the start stands at the asked spot with a warning. A start is never refused here. The portals checked
    /// are protect.portals with portal_count / portal_radius applied (<see cref="EffectivePortals"/>).
    ///
    /// THREADING (traced; see WorldEventDatLandscape's remarks for the citations): TryStart calls this UNDER the manager's lock
    /// on the caller's thread. Everything read here is the client dat (DatDatabase.ReadFromDat, thread safe) and the town list
    /// (WorldEventTownIndex.Towns, read under its own lock). No physics landblock is read or loaded, no Position helper that
    /// reaches LScape (FromGlobal, GetCell, GetTerrainZ, IsWalkable) is called; the moved anchor is built from global metres
    /// by arithmetic (<see cref="AnchorAt"/>).
    /// </summary>
    public sealed class WorldEventHoldStartSpotRule : IWorldEventStartSpotRule
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventHoldStartSpotRule));

        private readonly Func<IWorldEventLandscape> landscape;
        private readonly Func<IReadOnlyList<WorldEventTown>> towns;
        private readonly Random rng;
        private readonly Func<GoalDef, WorldEventHoldSettings> settings;

        /// <summary>The production rule: the dat landscape (a fresh one per resolve) and the loaded Town Network.</summary>
        public static readonly WorldEventHoldStartSpotRule Instance = new WorldEventHoldStartSpotRule(null, null, null, null);

        /// <summary>The testable shape: <paramref name="landscape"/>, <paramref name="towns"/> and <paramref name="settings"/> replace the dat, the town index and the property-backed settings resolver.</summary>
        public WorldEventHoldStartSpotRule(Func<IWorldEventLandscape> landscape, Func<IReadOnlyList<WorldEventTown>> towns, Random rng,
            Func<GoalDef, WorldEventHoldSettings> settings = null)
        {
            this.settings = settings ?? WorldEventHoldSettingsResolver.Resolve;
            this.landscape = landscape ?? (() => new WorldEventDatLandscape());
            this.towns = towns ?? (() => WorldEventTownIndex.Towns);
            this.rng = rng;
        }

        public bool TryResolve(WorldEventComposition composition, WorldEventRequest request, out string error)
        {
            error = null;

            var goal = composition?.Goal;
            var protect = goal?.Protect;
            var anchor = composition?.AnchorPosition;

            if (goal == null || protect == null || anchor == null)
                return true;

            var def = protect.ClearArea;
            var reach = goal.TownReach;

            // Nothing to check, or nothing that can be checked: an indoor anchor has no terrain to search.
            if ((def == null && reach == null) || anchor.Indoors)
                return true;

            var land = landscape();
            var townList = reach != null ? (towns() ?? Array.Empty<WorldEventTown>()) : Array.Empty<WorldEventTown>();
            var checkReach = reach != null && townList.Count > 0;
            var random = rng ?? new Random();

            // portal_count / portal_radius over protect.portals, resolved now, so the plan is built from the portals that
            // will really stand (review finding F4).
            var portals = EffectivePortals(protect.Portals, SpawnerTuning(goal));

            // Conservative: room for the most knights on the widest ring any crowd can get.
            var targets = WorldEventHoldRules.RingOffsets(ProtectDef.MaxProtectedTargets, protect.MaxRingRadius);

            var g = anchor.ToGlobal();

            Func<double, double, bool> accept = checkReach ? (x, y) => WorldEventTownReach.IsReachable(x, y, townList, reach.MaxWalk) : null;

            // 1. the asked spot, or the nearest open spot around it
            string why = null;

            if (def != null)
            {
                if (WorldEventSpawnArea.TrySearch(land, g.X, g.Y, targets, portals, def, out var plan, out var ax, out var ay, out why, accept)
                    && (!checkReach || WorldEventTownReach.IsReachable(ax, ay, townList, reach.MaxWalk)))
                    return Apply(composition, anchor, land, ax, ay, plan, "clear area");
            }
            else if (!checkReach || WorldEventTownReach.IsReachable(g.X, g.Y, townList, reach.MaxWalk))
                return true;

            why ??= $"no town drop within {reach?.MaxWalk ?? 0:0} m";

            // 2. re-roll near the nearest towns
            var tried = 0;

            if (checkReach)
            {
                foreach (var town in WorldEventTownReach.RerollTowns(townList, g.X, g.Y, reach))
                {
                    foreach (var (x, y) in WorldEventTownReach.SpotsNear(town, reach, random))
                    {
                        tried++;

                        if (!land.GroundOk(x, y))
                            continue;

                        if (def == null)
                            return Apply(composition, anchor, land, x, y, null, $"near {town.Name}, the asked spot failed: {why}");

                        var radiusTargets = targets.Count > 0 ? targets.Max(t => Math.Sqrt(t.Dx * t.Dx + t.Dy * t.Dy)) : 0;
                        var plan = WorldEventSpawnArea.CheckAt(land, x, y, targets, portals, def, WorldEventSpawnArea.Reach(portals, radiusTargets, def));

                        if (plan.Ok)
                            return Apply(composition, anchor, land, x, y, plan, $"near {town.Name}, the asked spot failed: {why}");
                    }
                }
            }

            // 3. nothing passed: the start is never refused (every start, admin or scheduled, names a spot); it stands as asked.
            log.Warn($"[WORLDEVENT] hold start: the asked spot {anchor.ToLOCString()} failed ({why})" +
                     (checkReach ? $" and no spot near {Math.Min(reach.Towns, townList.Count)} towns passed ({tried} tried)" : "") + "; it stands as asked");

            return true;
        }

        /// <summary>
        /// The portals the plan is built from (pure): protect.portals with portal_count / portal_radius applied when set. Null
        /// for a goal with no portals. A copy; the goal's own def is never changed.
        /// </summary>
        public static PortalsDef EffectivePortals(PortalsDef portals, WorldEventHoldSpawnerTuning tuning)
        {
            if (portals == null)
                return null;

            return new PortalsDef
            {
                Count = tuning?.PortalCount ?? portals.Count,
                Radius = tuning?.PortalRadius ?? portals.Radius,
                RotationDegrees = portals.RotationDegrees,
                Scale = portals.Scale,
                Inset = portals.Inset,
                Parts = portals.Parts,
                PulseEffect = portals.PulseEffect
            };
        }

        private WorldEventHoldSpawnerTuning SpawnerTuning(GoalDef goal)
        {
            try
            {
                return WorldEventHoldSpawnerTuning.From(settings(goal), goal);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] hold start: could not resolve the portal settings for goal '{goal?.Id}'; using protect.portals", ex);
                return null;
            }
        }

        /// <summary>How far the spot may move (metres) and still keep the asked place's name.</summary>
        public const double RenameDistance = 5.0;

        private bool Apply(WorldEventComposition composition, Position anchor, IWorldEventLandscape land, double gx, double gy,
            WorldEventAreaPlan plan, string how)
        {
            var before = anchor.ToLOCString();
            var from = anchor.ToGlobal();
            var z = land.TerrainZ(gx, gy) ?? anchor.PositionZ;

            if (!AnchorAt(anchor, gx, gy, z))
                return true;

            composition.AreaPlan = plan;

            var moved = Math.Sqrt((gx - from.X) * (gx - from.X) + (gy - from.Y) * (gy - from.Y));

            // The announcements and the admin label read Composition.Anchor.DisplayName: a spot that really moved is named
            // where it now is (review finding N1). A clone - the store's anchors are shared and never written.
            if (moved > RenameDistance)
                composition.Anchor = Renamed(composition.Anchor, WorldEventTownIndex.Describe(anchor, towns() ?? Array.Empty<WorldEventTown>()));

            log.Info($"[WORLDEVENT] hold start spot ({how}): {before} -> {anchor.ToLOCString()} moved={moved:0} m label='{composition.Anchor?.DisplayName}'" +
                     (plan != null ? $" portals={plan.Portals.Count} skipped={plan.Skipped}{(plan.Notes.Count > 0 ? " notes=" + string.Join("; ", plan.Notes) : "")}" : ""));

            return true;
        }

        /// <summary>A copy of <paramref name="anchor"/> with <paramref name="displayName"/> (pure; the original is untouched).</summary>
        public static AnchorDef Renamed(AnchorDef anchor, string displayName)
        {
            if (anchor == null)
                return null;

            return new AnchorDef
            {
                Id = anchor.Id,
                DisplayName = displayName,
                CellId = anchor.CellId,
                OriginX = anchor.OriginX,
                OriginY = anchor.OriginY,
                OriginZ = anchor.OriginZ,
                AnglesW = anchor.AnglesW,
                AnglesX = anchor.AnglesX,
                AnglesY = anchor.AnglesY,
                AnglesZ = anchor.AnglesZ,
                BiomeTags = anchor.BiomeTags != null ? new List<string>(anchor.BiomeTags) : new List<string>(),
                CooldownSeconds = anchor.CooldownSeconds
            };
        }

        /// <summary>
        /// Moves an OUTDOOR <paramref name="anchor"/> in place to the global point (<paramref name="gx"/>, <paramref name="gy"/>)
        /// at height <paramref name="z"/>, keeping its rotation and instance. Pure arithmetic: the landblock is gx / 192, gy / 192
        /// and the land cell the same split the engine's SetLandCell uses (Position.cs:211-225). False (and unchanged) for an
        /// indoor anchor or a point outside the world grid.
        /// </summary>
        public static bool AnchorAt(Position anchor, double gx, double gy, float z)
        {
            if (anchor == null || anchor.Indoors || !double.IsFinite(gx) || !double.IsFinite(gy))
                return false;

            var bx = (int)Math.Floor(gx / Position.BlockLength);
            var by = (int)Math.Floor(gy / Position.BlockLength);

            if (bx < 0 || bx > 254 || by < 0 || by > 254)
                return false;

            var lx = (float)(gx - bx * Position.BlockLength);
            var ly = (float)(gy - by * Position.BlockLength);

            var cellX = (uint)Math.Clamp((int)(lx / Position.CellLength), 0, 7);
            var cellY = (uint)Math.Clamp((int)(ly / Position.CellLength), 0, 7);

            anchor.LandblockId = new LandblockId(((uint)bx << 24) | ((uint)by << 16) | (cellX * 8 + cellY + 1));
            anchor.PositionX = lx;
            anchor.PositionY = ly;
            anchor.PositionZ = z;

            return true;
        }
    }
}
