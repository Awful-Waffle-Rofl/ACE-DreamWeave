using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure arithmetic for the MANA BARRIER CASTING SHIELD (owner ruling 2026-11-23): while an Archmage who has
    /// learned Mana Barrier is casting a spell (from cast start through spell release (turn, windup and gesture; not the recoil), MagicState.IsCasting), the arcane cloud acts as a
    /// shield of a FLAT armor level against frontal physical attacks from PvE attackers. The level is the same at
    /// every rank and is NOT touched by the Mana Barrier equipment mod. The mana-divert half of Mana Barrier is
    /// unrelated and unchanged.
    ///
    /// Enters the shield factor exactly where the ward shield does (Creature.GetShieldMod): summed into the
    /// uncapped virtual shield when no real shield is equipped, and added after the Shield-skill cap and before
    /// the ignore-shield term when one is (<see cref="ACE.Server.WeaponMods.DefensiveWeaponMath.AfterShieldCap"/>).
    /// Physical only: SpellProjectile.GetAbsorbMod never calls GetShieldMod. Kept pure because Player's static
    /// initializer cannot run under the unit test host.
    /// </summary>
    public static class ManaBarrierShieldMath
    {
        /// <summary>
        /// THE WHOLE DECISION (Player.GetManaBarrierShieldLevel only gathers the inputs). 0 unless class abilities
        /// are enabled, Mana Barrier is learned (rank 1 or better), the player is casting, and the attacker is an
        /// enemy creature (a live non-player creature other than the defender, so PvP and self give 0). Otherwise
        /// the tunable level, never below 0 and never scaled by rank.
        /// </summary>
        public static double LevelFor(bool enabled, int rank, bool casting, bool attackerIsEnemyCreature, long tunableLevel)
        {
            if (!enabled || rank < 1 || !casting || !attackerIsEnemyCreature)
                return 0.0;

            return Math.Max(0L, tunableLevel);
        }

        /// <summary>
        /// The no-shield path's virtual shield level: Defensive Weapon's level, the ward shield level and this
        /// casting level all SUM. Negative terms count as 0 for the casting term only (the other two are
        /// sanitized at their own sources).
        /// </summary>
        public static double VirtualShieldLevel(double defensiveWeaponLevel, double wardShieldLevel, double manaBarrierShieldLevel) =>
            defensiveWeaponLevel + wardShieldLevel + Math.Max(0.0, manaBarrierShieldLevel);
    }
}
