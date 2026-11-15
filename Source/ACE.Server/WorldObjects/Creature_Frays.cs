using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Frays;
using ACE.Server.Physics;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Frays on a creature: the fire tag, the leash, the wake, and the per-viewer attackable bit. Every hook here is
    /// a no-op (one null check) for a creature no fire spawned.
    /// </summary>
    partial class Creature
    {
        /// <summary>Runtime only: on a Great Snarl, the Fray (landblock) it belongs to (0 = none).</summary>
        public ushort FraysGreatSnarl;

        /// <summary>Runtime only: set on a monster a Frays fire spawned.</summary>
        public FraysCreatureTag FraysTag;

        /// <summary>Runtime only: a Frays follower pet's run-speed multiplier (keeps pace with its owner). 1 for everything else.</summary>
        public float FraysRunRateMult = 1.0f;

        /// <summary>A fire's monster is described per viewer: attackable to its owners, not attackable to everyone else.</summary>
        public override bool HasPerViewerDescription => FraysTag != null;

        /// <summary>
        /// The client Attackable bit for a fire's monster. While a create message is serialized for one viewer
        /// (<see cref="WorldObject.SerializingFor"/>), true only for an owner. With no viewer known (a broadcast) it is
        /// false: a player passing by must never see it as a target, and owners get their own create.
        /// </summary>
        /// <summary>The client Attackable bit, per viewer, for a fire's monster (see FraysClientAttackable).</summary>
        public override bool FraysAttackableBit => FraysTag != null ? FraysClientAttackable() : Attackable;

        private bool FraysClientAttackable()
        {
            if (!Attackable)
                return false;

            var viewer = SerializingFor;

            return viewer != null && FraysTag.Fire.IsOwner(viewer);
        }

        /// <summary>
        /// The leash, on the monster tick. Outside the zone past the grace time, it walks home (at the return speed, and
        /// heals when it gets there); a target that is no longer acceptable (left the zone, left the fellowship, Last
        /// Ember) is dropped for another owner in the zone, else it walks home. True when it took over this tick.
        /// </summary>
        public bool TickFraysLeash()
        {
            var tag = FraysTag;

            if (tag == null || IsDead || MonsterState == State.Return || Location == null)
                return false;

            var fire = tag.Fire;
            var id = fire.Def.Id;
            var now = Time.GetUnixTime();

            fire.RecordDistance(fire.DistanceToFire(Location));

            // Fallen off a raised floor (the Sky Fray's plateau): it can never walk home, so it is gone (its point rests anew).
            if (fire.Def.FloorAbove != null && Location.PositionZ < fire.Def.FloorAbove.Value - 5)
            {
                fire.OnLeashReturn(this, escaped: true);
                Destroy();
                return true;
            }

            // The pack tether (pack_tether): a pack's creature pulled too far from its own point walks home too.
            if (!fire.InZone(Location, fire.Margin, fire.MaxChase) || fire.BeyondTether(tag, Location.Pos))
            {
                if (tag.OutsideSince <= 0)
                    tag.OutsideSince = now;

                if (now - tag.OutsideSince >= FraysSettings.Number(FraysSettings.LeashGraceSeconds, id))
                {
                    tag.OutsideSince = 0;
                    fire.OnLeashReturn(this, escaped: true);
                    MoveToHome();
                    return true;
                }
            }
            else
                tag.OutsideSince = 0;

            if (AttackTarget is Creature target && !tag.Accepts(target))
            {
                AttackTarget = null;
                TauntTarget = null;

                // FindNextTarget only sees acceptable targets (GetAttackTargets) and walks home itself when none is left.
                if (!FindNextTarget())
                {
                    if (MonsterState != State.Return)
                        MoveToHome();

                    return true;
                }
            }

            return false;
        }

        /// <summary>Called from MoveToHome: a fire's monster is now walking home on its leash.</summary>
        private void OnFraysMoveToHome()
        {
            if (FraysTag == null)
                return;

            FraysTag.Returning = true;

            // Counted only when it is actually away from its spawn point (an idle monster re-homed in place is no return).
            var home = GetPosition(PositionType.Home);

            if (home != null && Location != null && System.Numerics.Vector3.Distance(home.Pos, Location.Pos) > 1.0f)
                FraysTag.Fire.OnLeashReturn(this, escaped: false);
        }

        /// <summary>Called from Sleep: a fire's monster that walked home heals.</summary>
        private void OnFraysSleep()
        {
            var tag = FraysTag;

            if (tag == null || !tag.Returning)
                return;

            tag.Returning = false;

            var share = FraysSettings.Number(FraysSettings.LeashReturnHeal, tag.Fire.Def.Id);

            if (share <= 0 || IsDead)
                return;

            var target = (uint)System.Math.Round(Health.MaxValue * share);

            if (Health.Current < target)
                UpdateVitalDelta(Health, (int)(target - Health.Current));
        }

        /// <summary>The return-home speed (times the run speed) while a fire's monster walks home.</summary>
        private float FraysReturnSpeedFactor =>
            FraysTag != null && MonsterState == State.Return ? (float)FraysSettings.Number(FraysSettings.LeashReturnSpeed, FraysTag.Fire.Def.Id) : 1.0f;

        /// <summary>Wakes this idle monster onto the nearest acceptable owner among <paramref name="present"/>.</summary>
        public bool TryFraysWake(List<Player> present)
        {
            if (FraysTag == null || IsDead || IsDestroyed || PhysicsObj == null || MonsterState != State.Idle)
                return false;

            var pick = present.Where(p => FraysTag.Accepts(p))
                .OrderBy(p => PhysicsObj.get_distance_sq_to_object(p.PhysicsObj, true)).FirstOrDefault();

            if (pick == null)
                return false;

            if (!IsVisibleTarget(pick))
                PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { pick.PhysicsObj });

            if (!IsVisibleTarget(pick))
                return false;

            AttackTarget = pick;
            WakeUp(alertNearby: false);
            return true;
        }
    }
}
