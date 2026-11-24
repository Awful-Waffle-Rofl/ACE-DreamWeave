using System;
using System.Numerics;
using System.Reflection;

using log4net;

using ACE.Server.Physics;
using ACE.Server.Physics.Animation;
using ACE.Server.Physics.Common;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Would a course object (a Jump pad or the goal) enter the world where it is put? Answers WITHOUT entering it: the
    /// same steps the spawn takes (Landblock.AddWorldObjectInternal -> WorldObject.InitPhysicsObj -> AddPhysicsObj ->
    /// PhysicsObj.enter_world -> SetPosition -> SetPositionInternal), stopping right before the commit. The commit is
    /// SetPositionInternal(Transition) (change_cell, the shadow cells) and handle_all_collisions (the collision
    /// callbacks); neither runs here, so the probe body never joins a cell, the object never joins a landblock, and
    /// nothing is broadcast. Teardown is the spawn's own failure teardown (PhysicsObj.DestroyObject, which also drops the
    /// ServerObjectManager entry InitPhysicsObj made) followed by WorldObject.Destroy, which recycles the dynamic guid.
    ///
    /// The verdict is Form's: for a pad, entered AND landed within <see cref="JumpEncounterType.PlacementTolerance"/> on
    /// the same landblock (TryEnter + LandedAt); for the goal, entered (TryEnter). Runs on the point's landblock queue,
    /// as Place does.
    ///
    /// A probe FAULT (a throw from the check) fails open: it is logged at Error and the object counts as clear, so a probe
    /// defect never costs the run its slot; Form's own landing check (CouldNotForm) still guards the Use. A create that
    /// returns null or throws refuses instead, because Form calls the same factory and would fail the same way.
    /// </summary>
    internal static class JumpPlacementProbe
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Null when an object of <paramref name="wcid"/> would enter at <paramref name="intended"/> (and, with
        /// <paramref name="mustLandExactly"/>, stay within the tolerance, or the probe itself faulted); otherwise why not.
        /// Never throws.
        /// </summary>
        public static string Probe(Func<uint, WorldObject> create, uint wcid, Position intended, bool mustLandExactly)
        {
            WorldObject wo;

            try
            {
                wo = create(wcid);
            }
            catch (Exception ex)
            {
                // Not a probe fault: Form calls the same factory, so a create that throws here would throw at Use time too.
                log.Error($"[DYNDUNGEON] placement probe: wcid {wcid} threw on create at {intended?.ToLOCString()}", ex);
                return $"wcid {wcid} threw on create ({ex.GetType().Name})";
            }

            if (wo == null)
                return $"wcid {wcid} did not create";

            try
            {
                wo.Location = new Position(intended);
                return Check(wo, intended, mustLandExactly);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] placement probe threw for wcid {wcid} at {intended?.ToLOCString()}", ex);
                return null;
            }
            finally
            {
                try
                {
                    // The probe object is discarded with its WorldObject; nothing else holds either.
                    var body = wo.PhysicsObj;

                    if (body != null)
                    {
                        body.entering_world = false;
                        body.DestroyObject();
                    }
                }
                catch (Exception ex)
                {
                    log.Warn($"[DYNDUNGEON] placement probe teardown: the physics body of wcid {wcid} did not destroy cleanly", ex);
                }

                try
                {
                    wo.Destroy();
                }
                catch (Exception ex)
                {
                    log.Warn($"[DYNDUNGEON] placement probe teardown: wcid {wcid} did not destroy cleanly", ex);
                }
            }
        }

        private static string Check(WorldObject wo, Position intended, bool mustLandExactly)
        {
            // Landblock.AddWorldObjectInternal refuses a bad Setup before any physics.
            if (!WorldObject.IsValidSetupId(wo.SetupTableId))
                return $"setup 0x{wo.SetupTableId:X8} is not a valid setup model";

            if (wo.ScatterPos != null)
                return "a scatter placement cannot be probed";

            wo.InitPhysicsObj();

            var obj = wo.PhysicsObj;

            // WorldObject.AddPhysicsObj.
            var location = wo.Location;
            WorldObject.AdjustDungeon(location);

            var cell = LScape.get_landcell(location.Cell, location.Instance);

            if (cell == null)
                return "no landcell";

            var pos = new Physics.Common.Position();
            pos.ObjCellID = cell.ID;
            pos.Frame.Origin = location.Pos;
            pos.Frame.Orientation = location.Rotation;

            var instance = location.Instance;

            // PhysicsObj.enter_world(Position, instance): the object's own position is the placement target.
            if (obj.Parent != null)
                return "has a parent";

            obj.entering_world = true;
            obj.store_position(pos);

            var setPos = new SetPosition(instance) { Pos = obj.Position, Flags = SetPositionFlags.Placement };

            if (obj.ProjectileTarget == null || wo is SpellProjectile)
                setPos.Flags |= SetPositionFlags.Slide;

            // PhysicsObj.SetPosition.
            var transition = Transition.MakeTransition(instance);

            if (transition == null)
                return "no transition";

            try
            {
                transition.InitObject(obj, ObjectInfoState.Default);

                if (obj.PartArray != null && obj.PartArray.GetNumSphere() != 0)
                    transition.InitSphere(obj.PartArray.GetNumSphere(), obj.PartArray.GetSphere(), obj.Scale);
                else
                    transition.InitSphere(1, PhysicsGlobals.DummySphere, 1.0f);

                // PhysicsObj.SetPositionInternal(Position, SetPosition, Transition), up to the commit.
                var target = setPos.Pos;
                var newCell = obj.AdjustPosition(target, transition.SpherePath.LocalSphere[0].Center, setPos.Flags.HasFlag(SetPositionFlags.DontCreateCells), true, instance);

                if (newCell == null)
                    return "no cell at that spot";

                Vector3 landed;
                uint landedCell;

                if (obj.WeenieObj != null && (obj.WeenieObj.IsStorage() || obj.WeenieObj.IsCorpse()) || obj.ExactPlacement)
                {
                    // ForceIntoCell / ForceIntoCellExact: put exactly where asked.
                    landed = target.Frame.Origin;
                    landedCell = newCell.ID;
                }
                else
                {
                    if (!obj.CheckPositionInternal(newCell, target, transition, setPos))
                        return transition.CollisionInfo.CollidedWithEnvironment ? "collides with the environment" : "collides";

                    if (transition.SpherePath.CurCell == null)
                        return "no cell after placement";

                    var cur = transition.SpherePath.CurPos;

                    if (cur.Landblock != target.Landblock)
                        return "placement pushed it onto another landblock";

                    landed = cur.Frame.Origin;
                    landedCell = cur.ObjCellID;
                }

                if (!mustLandExactly)
                    return null;

                // JumpEncounterType.LandedAt, on what SyncLocation would copy into the Location.
                if (landedCell >> 16 != intended.LandblockId.Landblock)
                    return "placement moved it onto another landblock";

                var moved = Vector3.Distance(landed, intended.Pos);

                return moved <= JumpEncounterType.PlacementTolerance ? null : $"placement moves it {moved:0.00} m";
            }
            finally
            {
                transition.CleanupTransition();
            }
        }
    }
}
