using ACE.Entity.Enum;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// COMBAT SIMULATOR ONLY. Writes CombatMode and nothing else: no motion, no stance animation, no network
        /// message, no ammo show/hide. For an offline Player hydrated by CombatSimulator.ProfileBuilder, which has
        /// no Session - SetCombatMode reaches player.Session.Network on every real switch
        /// (Creature_Combat.cs, HandleSwitchToPeaceMode and its siblings) and would throw.
        ///
        /// It exists because several attack and defense getters read CombatMode directly and return a neutral
        /// value out of stance: GetWeaponOffenseModifier and the three weapon defense modifiers
        /// (WorldObject_Weapon.cs) return 1.0 while CombatMode is NonCombat, and Player.GetCurrentAttackSkill picks
        /// the magic skill only in CombatMode.Magic. A hydrated Player is NonCombat (Creature.SetEphemeralValues).
        ///
        /// Never call this on an object in the world. A live creature changes stance through SetCombatMode.
        /// </summary>
        internal void SetCombatModeForSimulator(CombatMode combatMode)
        {
            CombatMode = combatMode;
        }
    }
}
