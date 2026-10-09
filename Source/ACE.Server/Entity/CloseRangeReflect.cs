using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// The close-range gate shared by the two reflect mechanics: the monster "reflect" combat effect
    /// (MonsterEffects.Effects.ReflectEffect, on=hit, monster_effect_reflect_max_range) and the player's
    /// Thorns (Player.ApplyThornsReflect, class_ability_thorns_max_range). Both are close-range punishes
    /// for ANY attack type - melee, missile or a cast spell - so the range, not the attack type, is what
    /// decides whether a landed hit is answered. One measure in one place keeps the two from drifting.
    ///
    /// DISTANCE MEASURE. WorldObject.GetCylinderDistance - the edge-to-edge cylinder distance
    /// Player.HandleActionTargetedMeleeAttack_Inner uses for its melee reach check and Monster.
    /// GetDistanceToTarget uses for IsMeleeRange - so a larger creature effectively reaches further.
    /// Either side lacking a PhysicsObj (not in the world) fails closed: no reflect.
    /// </summary>
    public static class CloseRangeReflect
    {
        /// <summary>
        /// True when <paramref name="attacker"/> is within <paramref name="maxRange"/> metres of
        /// <paramref name="defender"/>, edge to edge. Fails closed when either side has no PhysicsObj.
        /// </summary>
        public static bool IsWithinRange(Creature defender, Creature attacker, double maxRange)
        {
            if (defender?.PhysicsObj == null || attacker?.PhysicsObj == null)
                return false;

            return defender.GetCylinderDistance(attacker) <= maxRange;
        }
    }
}
