using ACE.Entity.Enum;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// CombatSimulator only (CombatSimulator.PlayerLoadout): whether this player meets an item's wield
        /// requirements, by the production rule the wield handlers use (CheckWieldRequirements, with no buffed
        /// requirement waived), so the simulator never picks a weapon the character could not actually equip.
        /// A pure read.
        /// </summary>
        internal bool CanWieldForSimulator(WorldObject item)
        {
            return item != null && CheckWieldRequirements(item) == WeenieError.None;
        }
    }
}
