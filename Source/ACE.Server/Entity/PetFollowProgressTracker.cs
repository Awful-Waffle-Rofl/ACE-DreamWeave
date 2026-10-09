using System;
using System.Collections.Generic;
using System.Numerics;

using ACE.Server.Managers;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Tuning and decisions for a combat pet's idle follow (WorldObjects/CombatPet_Follow.cs). Pure - no
    /// WorldObject, Player or physics dependency - so every rule here is unit-testable without a live world.
    /// </summary>
    public static class PetFollowRules
    {
        /// <summary>
        /// A pet with no target that is farther than this from its owner starts running back to them. Measured
        /// as a cylinder distance (edge to edge), the same measure WorldObject.GetCylinderDistance returns.
        /// </summary>
        public const float FollowStartDistance = 4.0f;

        /// <summary>
        /// Where the follow MoveTo stops (MovementParameters.DistanceToObject). Deliberately below
        /// FollowStartDistance: the gap between the two is the hysteresis band, so an owner who shuffles a
        /// step does not restart the pet's run on every tick.
        /// </summary>
        public const float FollowArriveDistance = 2.5f;

        /// <summary>
        /// Minimum seconds between two follow MoveTo issues. A MoveTo that ends early (MoveToManager gave up
        /// against geometry) would otherwise be re-issued - and re-broadcast to every nearby client - on every
        /// 0.2 s monster tick until the stuck rule teleports the pet.
        /// </summary>
        public const double ReissueIntervalSeconds = 1.0;

        /// <summary>
        /// Minimum seconds between two teleport attempts. The distance trigger fires on every tick while the pet
        /// is far away, so a refused or failed teleport (owner dead, no valid spot) would otherwise run the
        /// physics placement five times a second.
        /// </summary>
        public const double TeleportRetryIntervalSeconds = 1.0;

        /// <summary>
        /// How far (horizontally, metres) a teleported or summoned pet may end up from the point it was placed
        /// at before the placement counts as failed. SetPosition with the Slide flag accepts whatever spot the
        /// transition resolves to, which can be the far side of a wall.
        /// </summary>
        public const float PlacementHorizontalTolerance = 1.0f;

        /// <summary>
        /// How far (vertically, metres) a placed pet may end up from the requested point. Looser than the
        /// horizontal tolerance because the point in front of the owner keeps the owner's own Z, and on a
        /// slope or stairs the transition legitimately lifts the pet onto the ground ahead.
        /// </summary>
        public const float PlacementVerticalTolerance = 2.0f;

        /// <summary>The low 16 bits of an ObjCellID at or above this are an EnvCell (dungeon or building interior).</summary>
        public const uint FirstIndoorCell = 0x100;

        /// <summary>
        /// How far (metres) Pet.Init lifts the owner's own spot for its last-chance summon attempt, when both
        /// the spot in front of the owner and the owner's spot as-is have been refused.
        ///
        /// A standing player's Z sits a few millimetres above the floor plane under them, and at exactly that
        /// height the placement insert refuses a physics sphere larger than a player's own while accepting the
        /// player. Measured offline through the real spawn path (WorldObject.InitPhysicsObj +
        /// WorldObject.AddPhysicsObj) at the prod failure position 0x00AE0149
        /// [84.748344 -59.842636 -17.995001]: a 0.480 m sphere (the human setups' own) enters, a 0.720 m and a
        /// 0.960 m one do not, and lifting the requested point by as little as 0.005 m lets both of them in.
        /// See PetIndoorPlacementTests for that control and its remedy.
        ///
        /// The value is the SAME lift Position.InFrontOf already applies to the outdoor spot for the same
        /// reason - "move the Z slightly up and let gravity pull it down. just makes things easier."
        /// (ACE.Entity/Position.cs:129-137) - which is why only the indoor path ever needed this: indoors
        /// GetInFrontPlacement always declines (<see cref="InFrontCandidateUsable"/>), so the unlifted owner
        /// spot is the only attempt a dungeon summon ever got.
        /// </summary>
        public const float OwnerSpotBumpHeight = 0.05f;

        /// <summary>
        /// How far (horizontally and vertically, metres) the lifted owner-spot landing may be from the point it
        /// was requested at before <see cref="AcceptBumpedOwnerSpot"/> refuses it. DELIBERATELY NOT
        /// <see cref="PlacementHorizontalTolerance"/> / <see cref="PlacementVerticalTolerance"/>, which are
        /// sized for the in-front candidate: that one is metres from the owner, so a slope or a stair tread can
        /// legitimately step the pet onto ground well above or below the requested Z, and 1 m by 2 m is the
        /// right latitude for it.
        ///
        /// The lifted owner spot has neither rationale. It asks for the owner's own cell and own X/Y, 0.05 m up,
        /// and the measured landings came back 0.000 m horizontally with |dz| never above the 0.05 m lift
        /// itself. Inheriting 2 m of vertical latitude there would accept a real failure: one EnvCell spanning a
        /// sunken pit or a mezzanine (a common AC room shape) can resolve the lifted request to the lower floor
        /// at the SAME cell id with a small dx/dy and a dz of 1.5 m or so, and the pet would be kept somewhere
        /// the player can neither reach nor see - exactly the outcome this check exists to prevent.
        ///
        /// 0.3 m is six times the lift, so it is not brittle against a transition that settles the pet a few
        /// centimetres onto the floor plane, and far below any step a room's own geometry could produce.
        /// </summary>
        public const float OwnerSpotPlacementTolerance = 0.3f;

        public static bool ShouldStartFollow(float distanceToOwner)
        {
            return distanceToOwner > FollowStartDistance;
        }

        /// <summary>
        /// The distance trigger: a pet with no target farther than <paramref name="teleportDistance"/> from its
        /// owner is teleported at once rather than asked to run the whole way. A non-positive distance disables it.
        /// </summary>
        public static bool ShouldTeleportForDistance(float distanceToOwner, double teleportDistance)
        {
            return teleportDistance > 0.0 && distanceToOwner > teleportDistance;
        }

        /// <summary>
        /// The max-time trigger: a follow that started at <paramref name="followStartTime"/> and has not arrived
        /// by <paramref name="maxSeconds"/> later is ended by a teleport, whatever progress it shows. A
        /// non-positive limit disables it.
        /// </summary>
        public static bool FollowTimedOut(double followStartTime, double now, double maxSeconds)
        {
            return maxSeconds > 0.0 && now - followStartTime >= maxSeconds;
        }

        /// <summary>
        /// Whether a pet may be teleported to its owner. Never while the owner is mid-teleport (their
        /// Location is about to change under the pet) or dead, never across a landblock instance, and never
        /// unless the two are ticked on the same thread (see <see cref="SharesTickThread{TGroup}"/>).
        /// </summary>
        public static bool CanTeleportToOwner(bool ownerTeleporting, bool ownerDead, bool sameInstance, bool sharesTickThread)
        {
            return !ownerTeleporting && !ownerDead && sameInstance && sharesTickThread;
        }

        /// <summary>
        /// TRUE when the pet's landblock and the owner's landblock are ticked on the same thread: the same
        /// landblock, or two landblocks in the same non-null LandblockGroup. The same group comparison
        /// Creature.CastSpell makes before a monster casts on a target (Monster_Magic.cs). When it holds, a
        /// teleport into the owner's landblock adds the pet to a landblock whose group is the one the current
        /// thread is ticking, which is exactly what Landblock.AddWorldObjectInternal requires to avoid its
        /// cross-thread error path.
        ///
        /// Generic over the group type only so it can be tested without constructing a LandblockGroup, whose
        /// static initializer reaches into Landblock.
        /// </summary>
        public static bool SharesTickThread<TGroup>(bool sameLandblock, TGroup petGroup, TGroup ownerGroup) where TGroup : class
        {
            return sameLandblock || (petGroup != null && ReferenceEquals(petGroup, ownerGroup));
        }

        public static bool IsIndoorCell(uint objCellId)
        {
            return (objCellId & 0xFFFF) >= FirstIndoorCell;
        }

        /// <summary>
        /// Whether the spot in front of the owner is worth trying at all, before any physics runs. Never when
        /// the owner is indoors: there the point ahead resolves to whichever EnvCell contains it, which past a
        /// wall is simply the next room (AdjustCell.GetCell), and a placement there succeeds cleanly behind
        /// the wall. Never when the lookup failed (cell 0), left the owner's landblock, or - for an owner
        /// standing outdoors - fell inside a building interior, which is the outdoor form of the same trap.
        /// In every refused case the caller uses the owner's own spot instead.
        /// </summary>
        public static bool InFrontCandidateUsable(uint ownerCell, uint candidateCell)
        {
            if (IsIndoorCell(ownerCell))
                return false;

            if (candidateCell == 0 || (candidateCell >> 16) != (ownerCell >> 16))
                return false;

            return !IsIndoorCell(candidateCell);
        }

        /// <summary>
        /// The full in-front decision: the cheap cell rules of <see cref="InFrontCandidateUsable"/> first, then
        /// line of sight from the owner to the spot. A usable outdoor cell can still be on the far side of a
        /// fence or an exterior wall - the pet then lands exactly where it was sent, so the landing check
        /// (<see cref="AcceptPlacement"/>) cannot tell it from a clean placement. The sight test is a physics
        /// transition, so it is passed in lazily and only runs once the cell rules have passed.
        /// </summary>
        public static bool InFrontPlacementAllowed(uint ownerCell, uint candidateCell, Func<bool> ownerSeesCandidate)
        {
            if (!InFrontCandidateUsable(ownerCell, candidateCell))
                return false;

            return ownerSeesCandidate != null && ownerSeesCandidate();
        }

        /// <summary>
        /// Whether a placement that SetPosition reported as OK actually put the pet where it was asked to go:
        /// in the requested cell, with a live physics cell, and within the placement tolerances of the
        /// requested point. Both origins are cell-local, which is only meaningful because the cells match.
        /// A FALSE here means the transition slid the pet somewhere else - typically through or around a wall -
        /// and the caller falls back to the owner's own spot.
        /// </summary>
        public static bool AcceptPlacement(uint requestedCell, Vector3 requestedOrigin, uint landedCell, Vector3 landedOrigin, bool landedHasCurCell)
        {
            return AcceptPlacement(requestedCell, requestedOrigin, landedCell, landedOrigin, landedHasCurCell,
                PlacementHorizontalTolerance, PlacementVerticalTolerance);
        }

        /// <summary>
        /// <see cref="AcceptPlacement(uint, Vector3, uint, Vector3, bool)"/> with the tolerances supplied rather
        /// than inherited. The in-front candidate and the lifted owner spot are not the same question - one is
        /// metres away over ground the transition may legitimately step the pet onto, the other is the owner's
        /// own X/Y a few centimetres up - so they do not share one pair of numbers.
        /// </summary>
        public static bool AcceptPlacement(uint requestedCell, Vector3 requestedOrigin, uint landedCell, Vector3 landedOrigin,
            bool landedHasCurCell, float horizontalTolerance, float verticalTolerance)
        {
            if (!landedHasCurCell || requestedCell != landedCell)
                return false;

            var dx = landedOrigin.X - requestedOrigin.X;
            var dy = landedOrigin.Y - requestedOrigin.Y;
            var dz = landedOrigin.Z - requestedOrigin.Z;

            if (float.IsNaN(dx) || float.IsNaN(dy) || float.IsNaN(dz))
                return false;

            return dx * dx + dy * dy <= horizontalTolerance * horizontalTolerance
                && Math.Abs(dz) <= verticalTolerance;
        }

        /// <summary>
        /// Whether Pet.Init keeps the pet that its last-chance lifted owner-spot attempt
        /// (<see cref="OwnerSpotBumpHeight"/>) just put in the world, or tears it down and reports the same
        /// clean failure the summon reports today.
        ///
        /// The structural rules <see cref="AcceptPlacement"/> applies - the requested cell and a live physics
        /// cell - measured against <see cref="OwnerSpotPlacementTolerance"/> rather than the in-front
        /// tolerances, plus the landblock INSTANCE, which AcceptPlacement cannot see because an ObjCellID does
        /// not carry one. A pet that entered a different instance from its owner is invisible to them and
        /// unusable, which is strictly worse for the player than no pet plus an error message, so it is refused
        /// here rather than kept because SetPosition said OK.
        ///
        /// The requested point is always the owner's own cell and own X/Y, so "within the tolerance of the
        /// requested point" is also "within the tolerance of the owner" - the sane-distance rule needs no
        /// separate measure.
        /// </summary>
        public static bool AcceptBumpedOwnerSpot(uint requestedCell, Vector3 requestedOrigin, uint requestedInstance,
            uint landedCell, Vector3 landedOrigin, uint landedInstance, bool landedHasCurCell)
        {
            if (requestedInstance != landedInstance)
                return false;

            return AcceptPlacement(requestedCell, requestedOrigin, landedCell, landedOrigin, landedHasCurCell,
                OwnerSpotPlacementTolerance, OwnerSpotPlacementTolerance);
        }
    }

    /// <summary>
    /// The combat-pet follow and reach tunables, read once per pet tick (CombatPet.HandleFindTarget) so one tick
    /// never mixes two values of the same setting.
    /// </summary>
    public struct PetFollowSettings
    {
        public const double DefaultTeleportDistance = 40.0;
        public const double DefaultMaxFollowSeconds = 15.0;
        public const double DefaultStuckSeconds = 5.0;
        public const double DefaultUnreachableSeconds = 8.0;
        public const double DefaultUnreachableIgnoreSeconds = 10.0;

        public const string TeleportDistanceKey = "pet_follow_teleport_distance";
        public const string MaxFollowSecondsKey = "pet_follow_max_seconds";
        public const string StuckSecondsKey = "pet_follow_stuck_seconds";
        public const string UnreachableSecondsKey = "pet_combat_unreachable_seconds";
        public const string UnreachableIgnoreSecondsKey = "pet_combat_unreachable_ignore_seconds";

        /// <summary>No target and farther than this from the owner: teleport at once. 0 or less disables.</summary>
        public double TeleportDistance;

        /// <summary>A follow that has not arrived within this many seconds teleports. 0 or less disables.</summary>
        public double MaxFollowSeconds;

        /// <summary>No progress toward the owner for this many seconds teleports. 0 or less disables.</summary>
        public double StuckSeconds;

        /// <summary>No progress toward the target and no attack for this long drops the target. 0 or less disables.</summary>
        public double UnreachableSeconds;

        /// <summary>How long a target dropped as unreachable is skipped by target selection.</summary>
        public double UnreachableIgnoreSeconds;

        public static PetFollowSettings Defaults => new PetFollowSettings
        {
            TeleportDistance = DefaultTeleportDistance,
            MaxFollowSeconds = DefaultMaxFollowSeconds,
            StuckSeconds = DefaultStuckSeconds,
            UnreachableSeconds = DefaultUnreachableSeconds,
            UnreachableIgnoreSeconds = DefaultUnreachableIgnoreSeconds,
        };

        /// <summary>
        /// Reads all five settings. Each read falls back to its compiled default when PropertyManager throws
        /// (it does in unit tests for an uncached key) or returns a non-finite value.
        /// </summary>
        public static PetFollowSettings Read()
        {
            return new PetFollowSettings
            {
                TeleportDistance = ReadDouble(TeleportDistanceKey, DefaultTeleportDistance),
                MaxFollowSeconds = ReadDouble(MaxFollowSecondsKey, DefaultMaxFollowSeconds),
                StuckSeconds = ReadDouble(StuckSecondsKey, DefaultStuckSeconds),
                UnreachableSeconds = ReadDouble(UnreachableSecondsKey, DefaultUnreachableSeconds),
                UnreachableIgnoreSeconds = Math.Max(0.0, ReadDouble(UnreachableIgnoreSecondsKey, DefaultUnreachableIgnoreSeconds)),
            };
        }

        private static double ReadDouble(string key, double fallback)
        {
            try
            {
                var value = PropertyManager.GetDouble(key, fallback).Item;
                return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }

    /// <summary>
    /// Stuck detection for a pet moving toward something that may itself be moving - its owner (the idle
    /// follow) or its target (the unreachable-target rule). Pure, so the rule is unit-testable.
    ///
    /// Each sample credits the pet with the EXACT distance it closed since the previous sample, measured
    /// against where the goal is now: |goal - previousPet| - |goal - pet|. Using the current goal position on
    /// both sides cancels the goal's own movement, so a pet running flat out after an owner who is faster than
    /// it earns full credit for every metre it runs even though the gap grows, and a stationary pet earns
    /// nothing however far the goal walks. The sum is signed, so any back-and-forth - along the goal line or
    /// sideways across it - nets to zero instead of drifting upward. Measured in 3D global coordinates, so
    /// climbing stairs toward an owner above counts.
    ///
    /// Once the running sum reaches <see cref="ProgressEpsilon"/> that is progress: the clock and the sum
    /// both restart. No progress for the caller's timeout reports stuck.
    ///
    /// The caller resets the tracker on arrival, on acquiring or losing a target, and after every teleport attempt.
    /// </summary>
    public sealed class PetFollowProgressTracker
    {
        /// <summary>Metres of net distance closed on the goal that count as progress.</summary>
        public const float ProgressEpsilon = 0.5f;

        private bool tracking;
        private Vector3 previousPetPosition;
        private float closed;
        private double lastProgressTime;

        public bool IsTracking => tracking;

        /// <summary>Stops tracking; the next <see cref="Update"/> opens a fresh window.</summary>
        public void Reset()
        {
            tracking = false;
        }

        /// <summary>
        /// Counts as progress without any movement: the clock and the running sum restart. The reach rule calls
        /// this when the pet attacks its target, since a pet swinging at something plainly reached it. A no-op
        /// while not tracking.
        /// </summary>
        public void MarkProgress(double now)
        {
            if (!tracking)
                return;

            closed = 0.0f;
            lastProgressTime = now;
        }

        /// <summary>
        /// The exact distance closed on <paramref name="goal"/> by a move from <paramref name="previousPet"/>
        /// to <paramref name="pet"/>. Positive toward, negative away, zero for any move that keeps the distance.
        /// </summary>
        public static float DistanceClosed(Vector3 previousPet, Vector3 pet, Vector3 goal)
        {
            return (goal - previousPet).Length() - (goal - pet).Length();
        }

        /// <summary>
        /// Feeds one sample taken at <paramref name="now"/> (seconds, any monotonic clock), with the pet's and
        /// the goal's positions in the same global frame. Returns TRUE when there has been no progress toward
        /// the goal for at least <paramref name="stuckTimeoutSeconds"/>; a non-positive timeout never reports
        /// stuck. The first sample after a reset only opens the window and never reports stuck.
        /// </summary>
        public bool Update(double now, Vector3 petPosition, Vector3 goalPosition, double stuckTimeoutSeconds)
        {
            if (!tracking)
            {
                tracking = true;
                previousPetPosition = petPosition;
                closed = 0.0f;
                lastProgressTime = now;
                return false;
            }

            var step = DistanceClosed(previousPetPosition, petPosition, goalPosition);

            if (!float.IsNaN(step) && !float.IsInfinity(step))
                closed += step;

            previousPetPosition = petPosition;

            if (closed >= ProgressEpsilon)
            {
                closed = 0.0f;
                lastProgressTime = now;
                return false;
            }

            return stuckTimeoutSeconds > 0.0 && now - lastProgressTime >= stuckTimeoutSeconds;
        }
    }

    /// <summary>
    /// The unreachable-target rule for a combat pet (CombatPet.HandleFindTarget): a target the pet has neither
    /// closed on (the same exact-distance measure as the follow, <see cref="PetFollowProgressTracker"/>) nor
    /// attacked for pet_combat_unreachable_seconds is dropped. Deliberately NOT a leash: the owner's position
    /// plays no part, and a pet trading blows with a target far from its owner is never touched.
    /// </summary>
    public sealed class PetTargetReachTracker
    {
        private readonly PetFollowProgressTracker progress = new PetFollowProgressTracker();

        private object target;

        public object Target => target;

        /// <summary>Forgets the current target; the next <see cref="Update"/> opens a fresh window.</summary>
        public void Reset()
        {
            target = null;
            progress.Reset();
        }

        /// <summary>The pet attacked its current target: that proves it can reach it, so the clock restarts.</summary>
        public void NotifyAttack(object attacked, double now)
        {
            if (attacked != null && ReferenceEquals(attacked, target))
                progress.MarkProgress(now);
        }

        /// <summary>
        /// One sample for <paramref name="currentTarget"/>. A target different from the last one restarts the
        /// window. Returns TRUE when the pet has made no progress toward it and has not attacked it for
        /// <paramref name="unreachableSeconds"/> (a non-positive value disables the rule).
        /// </summary>
        public bool Update(object currentTarget, double now, Vector3 petPosition, Vector3 targetPosition, double unreachableSeconds)
        {
            if (!ReferenceEquals(currentTarget, target))
            {
                target = currentTarget;
                progress.Reset();
            }

            if (target == null)
                return false;

            return progress.Update(now, petPosition, targetPosition, unreachableSeconds);
        }
    }

    /// <summary>
    /// Targets a combat pet has given up on as unreachable, each skipped by target selection until its own
    /// expiry, so the nearest-monster pick (and the assist pick) does not hand the pet straight back the target
    /// it just dropped. Keyed by object guid; entries are pruned lazily.
    /// </summary>
    public sealed class PetTargetIgnoreList
    {
        private readonly Dictionary<uint, double> expiries = new Dictionary<uint, double>();

        public int Count => expiries.Count;

        /// <summary>Skips <paramref name="guid"/> until <paramref name="now"/> + <paramref name="seconds"/>. A non-positive duration ignores nothing.</summary>
        public void Ignore(uint guid, double now, double seconds)
        {
            if (seconds <= 0.0)
                return;

            expiries[guid] = now + seconds;
        }

        public bool IsIgnored(uint guid, double now)
        {
            if (expiries.Count == 0 || !expiries.TryGetValue(guid, out var expiry))
                return false;

            if (now < expiry)
                return true;

            expiries.Remove(guid);
            return false;
        }

        public void Clear()
        {
            expiries.Clear();
        }
    }
}
