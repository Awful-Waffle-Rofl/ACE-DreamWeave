namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// PropertyBool.PveOnly (9081): an item marked PvE-only is never used against another player. Two pure decisions, each unit
    /// tested on every input combination; the call sites (Player_Inventory.DoHandleActionGetAndWieldItem, the match-entry and
    /// kit-apply clears, Player.DamageTarget and SpellProjectile.OnCollideObject) are pinned on source. The flag on the item is the
    /// only switch: there is no server setting.
    /// </summary>
    public static class PvpPveOnlyRule
    {
        /// <summary>TRUE when equipping the item must be refused: the player is in a PvP match (a binding or a worn issued kit) and the item is PvE-only.</summary>
        public static bool RefusesEquip(bool inMatch, bool itemIsPveOnly)
            => inMatch && itemIsPveOnly;

        /// <summary>TRUE when a hit must deal nothing: the target is a player and the weapon that produced the hit is PvE-only. Applies anywhere, in or out of a match.</summary>
        public static bool BlocksDamage(bool targetIsPlayer, bool weaponIsPveOnly)
            => targetIsPlayer && weaponIsPveOnly;
    }
}
