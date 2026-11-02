using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Combat pet movement-speed floor (WaffleACE, 2026-10-26). Every CombatPet moves at least as fast as the
    /// reference wasp swarm summon (see PetSpeedFloor for the reference constants and the arithmetic). Pets already
    /// faster are unchanged.
    ///
    /// The floor rides MovementSpeedFactor, which every monster emit site writes into both the wire and the server
    /// half of a move: the chase (Creature_Navigation.GetMoveToMotion / Monster_Navigation.GetMovementParameters),
    /// the ranged / casting turn (Creature_Navigation.TurnTo), the idle follow to the owner
    /// (CombatPet_Follow.IssueFollowMove), and the return-home leg (which CombatPet itself never runs - MoveToHome
    /// returns early for a CombatPet - but which reads the same member, so it stays consistent if that changes).
    /// </summary>
    partial class CombatPet
    {
        /// <summary>
        /// The pet's floor multiplier, &gt;= 1.0. 0 means not yet resolved. Resolved once, on the first move, from
        /// the pet's own cached MoveSpeed and the pet_speed_floor tunable - so like MoveSpeed itself it is fixed for
        /// the life of this summon, and a change to the tunable applies to pets summoned after it.
        /// </summary>
        private float speedFloorFactor;

        public override float MovementSpeedFactor
        {
            get
            {
                if (speedFloorFactor == 0.0f)
                    speedFloorFactor = ResolveSpeedFloorFactor();

                return CurrentMovementSlowFactor * speedFloorFactor;
            }
        }

        private float ResolveSpeedFloorFactor()
        {
            if (MoveSpeed == 0.0f)
                GetMovementSpeed();

            var enabled = ReadSpeedFloorEnabled();

            return PetSpeedFloor.GetFactor(enabled, enabled ? PetSpeedFloor.ReferenceSpeedSource() : 0.0f, MoveSpeed);
        }

        /// <summary>
        /// The pet_speed_floor tunable, falling back to its compiled default when PropertyManager cannot be read
        /// (no shard config, as in ACE.Server.Tests, where an uncached key throws).
        /// </summary>
        private static bool ReadSpeedFloorEnabled()
        {
            try
            {
                return PropertyManager.GetBool(PetSpeedFloor.EnabledKey).Item;
            }
            catch (System.Exception)
            {
                return DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(PetSpeedFloor.EnabledKey, out var property) && property.Item;
            }
        }
    }
}
