using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Physics;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Creature melee combat for players and monsters
    /// </summary>
    partial class Creature
    {
        /// <summary>
        /// Returns TRUE for DualWieldCombat mode
        /// </summary>
        public bool IsDualWieldAttack { get => CurrentMotionState?.Stance == MotionStance.DualWieldCombat; }

        /// <summary>
        /// Dual wield alternate will be true if *next* attack is offhand
        /// </summary>
        public bool DualWieldAlternate;

        /// <summary>
        /// Returns TRUE for TwoHandedCombat mode
        /// </summary>
        public bool TwoHandedCombat { get => CurrentMotionState?.Stance == MotionStance.TwoHandedSwordCombat || CurrentMotionState?.Stance == MotionStance.TwoHandedStaffCombat; }

        /// <summary>
        /// Determines if a weapon can double or triple strike,
        /// and appends the appropriate multistrike prefix to a MotionCommand
        /// </summary>
        public string MultiStrike(AttackType attackType, string action)
        {
            if ((attackType & AttackType.MultiStrike) == 0)
                return action;

            var doubleStrike = action.EndsWith("Thrust") ? AttackType.DoubleThrust : AttackType.DoubleSlash;
            var tripleStrike = (AttackType)((int)doubleStrike * 2);

            if (attackType.HasFlag(tripleStrike))
                return $"Triple{action}";
            if (attackType.HasFlag(doubleStrike))
                return $"Double{action}";
            else
                return action;
        }

        /// <summary>
        /// Returns the attack types for a weapon
        /// </summary>
        public AttackType GetWeaponAttackType(WorldObject weapon)
        {
            if (weapon == null)
                return AttackType.Undef;

            return weapon.W_AttackType;
        }

        /// <summary>
        /// Returns the number of strikes for a weapon
        /// between 1-3 strikes
        /// </summary>
        public int GetNumStrikes(WorldObject weapon)
        {
            var attackType = GetWeaponAttackType(weapon);

            return GetNumStrikes(attackType);
        }

        /// <summary>
        /// Returns the number of strikes for an AttackType
        /// between 1-3 strikes
        /// </summary>
        public int GetNumStrikes(AttackType attackType)
        {
            if (CurrentMotionState.Stance == MotionStance.TwoHandedSwordCombat || CurrentMotionState.Stance == MotionStance.TwoHandedStaffCombat)
                return 2;

            if ((attackType & AttackType.MultiStrike) == 0)
                return 1;

            if (attackType.HasFlag(AttackType.TripleSlash) || attackType.HasFlag(AttackType.TripleThrust) || attackType.HasFlag(AttackType.OffhandTripleSlash) || attackType.HasFlag(AttackType.OffhandTripleThrust))
                return 3;
            else if (attackType.HasFlag(AttackType.DoubleSlash) || attackType.HasFlag(AttackType.DoubleThrust) || attackType.HasFlag(AttackType.OffhandDoubleSlash) || attackType.HasFlag(AttackType.OffhandDoubleThrust))
                return 2;
            else
                return 1;
        }

        public int DistanceComparator(PhysicsObj a, PhysicsObj b)
        {
            // use square distance to make things a bit faster
            var dist1 = Location.SquaredDistanceTo(a.WeenieObj.WorldObject.Location);
            var dist2 = Location.SquaredDistanceTo(b.WeenieObj.WorldObject.Location);

            return dist1.CompareTo(dist2);
        }

        public const float CleaveRange = 5.0f;
        public const float CleaveRangeSq = CleaveRange * CleaveRange;
        public const float CleaveAngle = 180.0f;

        public const float CleaveCylRange = 2.0f;

        /// <summary>
        /// Performs a cleaving attack for two-handed weapons
        /// </summary>
        /// <returns>The list of cleave targets to hit with this attack</returns>
        public List<Creature> GetCleaveTarget(Creature target, WorldObject weapon)
        {
            var player = this as Player;

            // class ability: Whirlwind (paid for this swing) cleaves in a full 360° arc for one extra target -
            // even on a weapon with no innate cleave, in which case Whirlwind is the whole reason we cleave.
            var whirlwind = player != null && player.WhirlwindSwingActive;

            // class ability: Kinetic Charge's melee spend (this same strike) cleaves EXTRA targets on top of
            // the weapon's own CleaveTargets and Whirlwind's +1, even on a weapon with no innate cleave. It
            // does not widen the arc - the normal front arc below still applies unless Whirlwind is also
            // active. See Player.KineticChargeCleaveTargets for why this is safe to read here.
            var kineticExtra = player != null ? player.KineticChargeCleaveTargets : 0;

            if (!weapon.IsCleaving && !whirlwind && kineticExtra <= 0) return null;

            // sort visible objects by ascending distance
            var visible = PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null);
            visible.Sort(DistanceComparator);

            var cleaveTargets = new List<Creature>();
            var totalCleaves = TotalCleaveTargets(weapon.CleaveTargets, whirlwind, kineticExtra);

            foreach (var obj in visible)
            {
                // cleaving skips original target
                if (obj.ID == target.PhysicsObj.ID)
                    continue;

                // only cleave creatures
                var creature = obj.WeenieObj.WorldObject as Creature;
                if (creature == null || creature.Teleporting || creature.IsDead) continue;

                // PvP rules choke point CLV1 (Docs/Pvp/DESIGN.md "PvP rules (levers)"): with
                // pvp_cleave_enabled off, a cleave never hits a player target, unless the attacker is an
                // admin. Classifies first inside PvpRules.ShouldSkipCleaveTarget - a non-player candidate,
                // or a non-PvP pair, never reads the setting.
                if (player != null && Pvp.Rules.PvpRules.ShouldSkipCleaveTarget(player, creature))
                    continue;

                if (player != null && player.IsBattlegroundObjectiveRefused(creature))
                    continue;

                if (player != null && player.CheckPKStatusVsTarget(creature, null) != null)
                    continue;

                if (!creature.Attackable && creature.TargetingTactic == TargetingTactic.None || creature.Teleporting)
                    continue;

                if (creature is CombatPet && (player != null || this is CombatPet))
                    continue;

                // no objects in cleave range
                var cylDist = GetCylinderDistance(creature);
                if (cylDist > CleaveCylRange)
                    return cleaveTargets;

                // only cleave in front of attacker (Whirlwind widens this to a full 360° arc)
                if (!whirlwind)
                {
                    var angle = GetAngle(creature);
                    if (Math.Abs(angle) > CleaveAngle / 2.0f)
                        continue;
                }

                // found cleavable object
                cleaveTargets.Add(creature);
                if (cleaveTargets.Count == totalCleaves)
                    break;
            }
            return cleaveTargets;
        }

        /// <summary>
        /// The total number of extra creatures one cleaving strike may hit: the weapon's own CleaveTargets,
        /// plus Whirlwind's flat +1, plus Kinetic Charge's per-strike extra (floored at 0, in case a
        /// negative value ever reaches here by some path other than KineticChargeAbility.FlooredCleaveTargets).
        /// Pure, so the stacking rules are unit-testable without a live Player or Creature.
        /// </summary>
        public static int TotalCleaveTargets(int weaponCleaves, bool whirlwind, int kineticExtra) =>
            weaponCleaves + (whirlwind ? 1 : 0) + Math.Max(0, kineticExtra);
    }
}
