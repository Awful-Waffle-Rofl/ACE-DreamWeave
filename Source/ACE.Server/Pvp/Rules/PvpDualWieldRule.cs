using ACE.Entity.Enum;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// pvp_match_block_dual_wield (Docs/Pvp/TEMPLATES.md "Dual wield", owner ruling 2026-10-16): while a player is in a PvP match, a
    /// weapon may not go in the off-hand. The off-hand is <see cref="EquipMask.Shield"/>; a shield belongs there and is always
    /// allowed, anything else in it is dual wielding. Pure, so the decision is unit tested on every input combination; the call
    /// sites (Player_Inventory.DoHandleActionGetAndWieldItem, and the off-hand clear at match entry) are pinned on source.
    /// </summary>
    public static class PvpDualWieldRule
    {
        /// <summary>
        /// TRUE when equipping an item into <paramref name="slot"/> must be refused. All four must hold: the player is in a match
        /// (<paramref name="inMatch"/>: a match binding or an issued template kit worn), the setting is on, the target slot
        /// includes the off-hand, and the item is not a shield.
        /// </summary>
        public static bool Refuses(bool inMatch, bool settingOn, EquipMask slot, bool itemIsShield)
            => inMatch && settingOn && (slot & EquipMask.Shield) != 0 && !itemIsShield;
    }
}
