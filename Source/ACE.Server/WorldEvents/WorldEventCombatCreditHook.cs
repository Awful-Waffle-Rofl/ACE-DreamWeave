using System;
using System.Reflection;

using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The hit-time half of the World Events reward-credit rule (owner ruling 2026-10-04, TECH-DESIGN 2.6):
    /// a player who deals ANY damage to, or takes ANY damage from, a creature that belongs to a run gets a
    /// participation record, and so earns the ordinary reward rather than the booby prize.
    ///
    /// The main call site is <see cref="ACE.Server.Entity.DamageHistory.Add"/>, which every damaging write to a
    /// Creature's or Player's health records through. Because a creature's DamageHistory records the player
    /// attacker and a player's records the creature attacker, the one site covers both directions:
    ///
    ///   * victim is a run creature, attacker resolves to a Player (directly, or a CombatPet's owner - the
    ///     same resolution DamageHistoryInfo and the death walk use) -> credit that player;
    ///   * victim is a Player, attacker is a run creature (melee, its missile, its spell projectile, its
    ///     Harm/Drain, its DoT - every one of those records the creature itself as the attacker) -> credit
    ///     the victim.
    ///
    /// Since the 2026-10-04 follow-up ruling a landed hit that deals NO health damage (fully absorbed) and a
    /// stamina- or mana-only attack also count, through <see cref="OnCombatContact"/>. DamageHistory.Add
    /// routes its own amount-0 case there; the stamina/mana and absorbed-Harm paths, which never reach
    /// DamageHistory.Add, call it directly (SpellProjectile.DamageTarget, HandleCastSpell_Boost,
    /// HandleCastSpell_Transfer).
    ///
    /// A player victim who is Invincible or under lifestone protection is never credited (see
    /// <see cref="VictimStateRefusesCredit"/>): the damage was recorded but never taken.
    ///
    /// Anything else returns after a field read and a type test or two: another player, a self-inflicted
    /// tick, a hotspot or trap (not a Creature), falling (Player_Move records the player as its own
    /// attacker), and every creature that carries no P_WorldEvent back-reference.
    ///
    /// HOT PATH. DamageHistory.Add runs for every damaging hit in the server. The rejecting reads come first
    /// and cost a field read, a type test and a null check; the state gate and the ledger lock are reached
    /// only when a run's creature is actually involved.
    /// </summary>
    public static class WorldEventCombatCreditHook
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// A health hit of <paramref name="amount"/> was recorded. Amount 0 is ignored here: DamageHistory.Add
        /// routes a landed-for-zero hit to <see cref="OnCombatContact"/> itself, so the two entry points never
        /// both fire for one hit.
        /// </summary>
        public static void OnDamageRecorded(Creature victim, WorldObject attacker, uint amount)
        {
            if (amount == 0)
                return;

            OnCombatContact(victim, attacker);
        }

        /// <summary>
        /// Contact WITHOUT a health amount (2026-10-04 follow-up ruling): a hit that LANDED on
        /// <paramref name="victim"/> from <paramref name="attacker"/> but wrote no health - fully absorbed by a
        /// ward, Mana Barrier, Sanguine Ward or similar - or an attack that lowered stamina or mana instead of
        /// health. Same direction rules, same run-state gate, same victim-state refusal as a damaging hit, and
        /// it never touches a damage total. Callers invoke it only on paths that do NOT also reach
        /// DamageHistory.Add with a positive amount.
        /// </summary>
        public static void OnCombatContact(Creature victim, WorldObject attacker)
        {
            if (victim == null || attacker == null)
                return;

            // Self-contact is never credit - a caster-source transfer, a self-inflicted tick, falling. Refused
            // here explicitly rather than relying on the direction rules happening to reject it.
            if (ReferenceEquals(victim, attacker))
                return;

            WorldEvent evt = null;

            try
            {
                Player player;

                // Each back-reference is read ONCE into a local: WorldEventSpawner.DestroyAll nulls it from
                // the world thread, so a second read could see null where the first saw a run.
                evt = victim.P_WorldEvent;

                if (evt != null)
                {
                    // A player (or their pet) hurt a run creature.
                    player = ResolveAttackingPlayer(attacker);
                }
                else if (victim is Player hurt && attacker is Creature source && (evt = source.P_WorldEvent) != null)
                {
                    // A run creature hurt a player - but only if the hit could actually land. See
                    // VictimStateRefusesCredit for which states, and why IsDead is not one of them.
                    if (VictimStateRefusesCredit(hurt.Invincible, hurt.UnderLifestoneProtection))
                        return;

                    player = hurt;
                }
                else
                    return;

                if (player == null)
                    return;

                evt.CreditCombatContact(player);
            }
            catch (Exception ex)
            {
                // A damage hook must never be able to abort the damage that triggered it.
                log.Error($"[WORLDEVENT] run={evt?.RunId} combat credit hook threw", ex);
            }
        }

        /// <summary>
        /// True when a player VICTIM in this state never actually takes the damage being recorded, so a
        /// run creature's hit on them must not count as combat contact.
        ///
        /// Why this exists: EnchantmentManager.ApplyDamageTick writes every DoT tick's credits into
        /// DamageHistory (and so into this hook) BEFORE it calls Player.TakeDamageOverTime, and that method
        /// then discards the tick for an Invincible player or one under lifestone protection
        /// (Player_Combat.cs TakeDamageOverTime). Without this check a run creature's DoT on such a player
        /// would credit damage that never landed. Player.TakeDamage returns before DamageHistory.Add in both
        /// states, and the spell paths check both upstream (SpellProjectile, WorldObject_Magic), so for direct
        /// hits this is belt and braces.
        ///
        /// IsDead is DELIBERATELY not an input, although TakeDamageOverTime also returns on it:
        ///   * for a DoT tick it is unreachable - ApplyDamageTick returns at its own top on IsDead, before
        ///     any DamageHistory.Add, so a dead player never reaches this hook from a tick;
        ///   * for every direct hit the vital is written BEFORE DamageHistory.Add, so on a killing blow the
        ///     victim already reads IsDead here. Refusing on it would deny credit to a player one-shot by a
        ///     run creature - a player who demonstrably took damage from it.
        /// Pure and static so the rule is testable without a live Player.
        /// </summary>
        public static bool VictimStateRefusesCredit(bool invincible, bool underLifestoneProtection)
            => invincible || underLifestoneProtection;

        /// <summary>
        /// The player behind <paramref name="attacker"/>: the attacker itself, or a CombatPet's owner. The
        /// same resolution DamageHistoryInfo applies when it records a pet (it captures PetOwner only for a
        /// CombatPet), so the hit-time credit and the death-time walk agree on who a pet's damage belongs to.
        /// </summary>
        public static Player ResolveAttackingPlayer(WorldObject attacker)
        {
            if (attacker is Player player)
                return player;

            if (attacker is CombatPet pet)
                return pet.P_PetOwner;

            return null;
        }
    }
}
