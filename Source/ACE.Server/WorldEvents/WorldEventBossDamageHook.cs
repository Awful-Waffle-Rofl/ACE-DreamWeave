using System;
using System.Reflection;

using ACE.Server.MlDigsite;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// THE single entry point every "a world-event boss hit a player" call site funnels into
    /// (TECH-DESIGN 2.16, BOSS-STANDARD.md 3.2). There is deliberately exactly one of these rather than a
    /// guard repeated at each site: the audience rule and the boss-identity rule must not be able to drift
    /// apart between the melee path and the spell path.
    ///
    /// Call sites, all of them the point at which the FINAL damage taken is known:
    ///
    ///   * Source/ACE.Server/WorldObjects/Player_Combat.cs, in
    ///     TakeDamage(WorldObject, DamageType, float, BodyPart, bool, AttackConditions), immediately after
    ///     DamageHistory.Add. This is monster MELEE (Monster_Melee.cs) and monster MISSILE
    ///     (ProjectileCollisionHelper.cs), both of which arrive through the DamageEvent overload that
    ///     forwards here.
    ///   * Source/ACE.Server/WorldObjects/SpellProjectile.cs, in DamageTarget, immediately after the
    ///     health UpdateVitalDelta. War and void bolts never reach Player.TakeDamage at all - they write
    ///     the vital directly - so this site is not redundant with the one above.
    ///   * Source/ACE.Server/WorldObjects/WorldObject_Magic.cs, in HandleCastSpell_Boost's Health case.
    ///     That is life magic resolved without a projectile (Harm / drain health).
    ///
    /// NOT hooked, and both are deliberate:
    ///
    ///   * DAMAGE OVER TIME. Player.TakeDamageOverTime (Player_Combat.cs:455) takes no source at all, and
    ///     its one caller (EnchantmentManager.cs:1570) has already SUMMED the tick across every damager on
    ///     the player, so there is no per-boss number at that point to measure. A DoT tick is also not an
    ///     "ordinary hit" in the sense the requirement is written in, so folding one into the mean would
    ///     drag the mean down and train the boss UPWARD. DoT damage is still scaled by the rating this
    ///     controller sets (WorldObject_Magic.cs:2803), it just does not vote on it.
    ///   * HOTSPOTS. Hotspot.cs:182 damages players through the same Player.TakeDamage overload, but its
    ///     source is a Hotspot, not a Creature, so it never passes the type test below.
    /// </summary>
    public static class WorldEventBossDamageHook
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Records one landed hit against the run's damage controller, if and only if the attacker IS this
        /// run's named boss and the defender counts toward the scaling audience.
        ///
        /// The staff exclusion is the same rule the audience sampler applies
        /// (WorldEventAudienceSampler.CountsTowardAudience): an admin standing in a run to watch it must
        /// not train the boss, in either direction. Their max health is not in the target either, so
        /// letting their hits into the mean would size the boss against a player who is not in the fight.
        ///
        /// HOT PATH. Every player-damage event in the server passes through here, so the ordering matters:
        /// the two field reads that reject a normal hit - "is the attacker a Creature" and "does that
        /// Creature carry a P_WorldEvent back-reference" - come first and cost a type test and a null
        /// check. Everything else is behind them and only runs while a world event actually owns the
        /// attacker.
        /// </summary>
        public static void NoteHit(WorldObject source, Player defender, int damageTaken, bool crit)
        {
            if (damageTaken <= 0 || defender == null)
                return;

            if (!(source is Creature attacker))
                return;

            var evt = attacker.P_WorldEvent;

            if (evt == null)
            {
                // RoZ round 19: the digsite Boss Rush boss uses the same measured controller. Behind the world
                // event check, and first thing inside it is one field read and a null check
                // (MlDigsiteBossDamage.TryNoteHit), so an ordinary creature pays exactly that and nothing more.
                if (attacker.P_DigsiteEncounter == null)
                    return;

                try
                {
                    MlDigsiteBossDamage.TryNoteHit(attacker, defender.Health.MaxValue, damageTaken, crit);
                }
                catch (Exception ex)
                {
                    // A damage hook must never be able to abort the damage that triggered it.
                    log.Error("[ML_DIGSITE] boss damage hook threw", ex);
                }

                return;
            }

            try
            {
                if (attacker.Guid.Full != evt.Spawner.BossGuid)
                    return;

                if (!WorldEventAudienceSampler.CountsTowardAudience(defender.Session?.AccessLevel))
                    return;

                evt.OnBossHitPlayer(damageTaken, defender.Health.MaxValue, crit);
            }
            catch (Exception ex)
            {
                // A damage hook must never be able to abort the damage that triggered it.
                log.Error($"[WORLDEVENT] run={evt.RunId} boss damage hook threw", ex);
            }
        }
    }
}
