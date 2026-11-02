using System;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Attack/Defend engaged defenders (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"): when this player last dealt or took damage against
    /// an opposing player of the same Live match, and the sample the crystal damage sink reads. In memory only, never saved, scoped by match
    /// id so a stamp from an earlier match never counts in the next.
    /// </summary>
    partial class Player
    {
        private volatile PvpEngagementStamp pvpEngagement;

        /// <summary>The last engagement stamp, or null when this player has not fought an opposing match player since login.</summary>
        internal PvpEngagementStamp PvpEngagement => pvpEngagement;

        /// <summary>Replaces the engagement stamp (one reference swap).</summary>
        internal void StampPvpEngagement(Guid matchId, DateTime atUtc) => pvpEngagement = new PvpEngagementStamp(matchId, atUtc);

        private volatile PvpActivityStamp pvpActivity;

        /// <summary>
        /// The conduct activity stamp (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"), or null when this player has done nothing that counts
        /// since login. In memory only, never saved; the coordinator's sampler reads it through the gateway's zone sample.
        /// </summary>
        internal PvpActivityStamp PvpActivity => pvpActivity;

        /// <summary>Replaces the activity stamp (one reference swap). Never called for movement.</summary>
        internal void StampPvpActivity(Guid matchId, DateTime atUtc) => pvpActivity = new PvpActivityStamp(matchId, atUtc);

        /// <summary>
        /// The heal half of the conduct signals (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"), called at the two heal choke points AFTER the
        /// vital write with <paramref name="restored"/>, the health the write actually added (overheal is never in it): a heal spell
        /// (WorldObject_Magic HandleCastSpell_Boost) and a healing kit (Healer). A heal of a TEAMMATE in the same Live match stamps the healer
        /// and credits HealingDone; a self-heal, an enemy, another match or no match earns nothing
        /// (<see cref="PvpConductRules.DecideHeal"/>). Collection only; best effort.
        /// </summary>
        internal static void OnPvpHealApplied(Player healer, Player healed, uint restored)
        {
            if (healer == null || healed == null || restored == 0)
                return;

            try
            {
                var healerBinding = healer.PvpBinding;
                var credit = PvpConductRules.DecideHeal(healerBinding, healed.PvpBinding, ReferenceEquals(healer, healed), restored);

                if (credit == PvpHealCredit.None)
                    return;

                healer.StampPvpActivity(healerBinding.Match.MatchId, PvpArenaHookSettings.UtcNow());

                if (credit == PvpHealCredit.ActivityAndHealing)
                    healerBinding.Match.Performance?.CreditHealing(healer.Guid.Full, restored);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] conduct signals: crediting {healer?.Name}'s heal of {healed?.Name} threw; the heal is unaffected", ex);
            }
        }

        /// <summary>
        /// A beneficial enchantment <paramref name="caster"/> cast on <paramref name="target"/> (a player, or an item a player wields): activity
        /// for the caster when the target is a TEAMMATE in the same Live match (<see cref="PvpConductRules.IsTeammateSupport"/>), never for a
        /// self-buff. No HealingDone. Collection only; best effort.
        /// </summary>
        internal static void OnPvpSupportCast(Player caster, WorldObject target)
        {
            if (caster == null || target == null)
                return;

            try
            {
                var recipient = target as Player ?? target.Wielder as Player;

                if (recipient == null || ReferenceEquals(recipient, caster))
                    return;

                var casterBinding = caster.PvpBinding;

                if (PvpConductRules.IsTeammateSupport(casterBinding, recipient.PvpBinding))
                    caster.StampPvpActivity(casterBinding.Match.MatchId, PvpArenaHookSettings.UtcNow());
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] conduct signals: stamping {caster?.Name}'s support cast threw; the cast is unaffected", ex);
            }
        }

        /// <summary>
        /// A melee swing or missile shot this player actually began against <paramref name="target"/> (Docs/Pvp/BATTLEGROUNDS.md "Conduct
        /// signals"): activity when the target is an ENEMY player of the same Live match (<see cref="PvpConductRules.IsHostileAttempt"/>), whether or
        /// not the swing connects or the arrow hits, since a hit alone reaches the damage gate in DamageTarget. A teammate, a player of another
        /// match, a monster, self and a Countdown target stamp nothing. Collection only; best effort.
        /// </summary>
        internal void StampPvpAttackAttempt(WorldObject target)
        {
            try
            {
                if (target is not Player targetPlayer || ReferenceEquals(targetPlayer, this))
                    return;

                var mine = PvpBinding;

                var theirs = targetPlayer.PvpBinding;

                if (!PvpConductRules.IsHostileAttempt(mine, theirs))
                    return;

                // Gate parity: the same Evaluate call TryPvpArenaGate makes, so a swing or shot the damage gate would refuse (a spawn-protected
                // or pen-waiting enemy) is not activity either. The teams differ here, so friendly fire cannot change the answer.
                var now = PvpArenaHookSettings.UtcNow();

                if (PvpArenaGate.Evaluate(mine, theirs, false, false, now, targetPlayer.Location) != PvpGateDecision.Allow)
                    return;

                // A swing or shot the Tugak Brawl spell-only rule refuses (TryPvpArenaGate, pvp_arena_ffa_spell_only) is not activity either.
                // Curse of Raven Fury is a cast, not an attack attempt: it stamps through TryPvpArenaGate itself.
                var inFfa = mine.Match.ModeKey == ArenaMapCatalog.FfaKey;

                if (inFfa && ACE.Server.Pvp.Rules.PvpArenaSpellRules.IsPhysicalRefused(inFfa, PvpArenaHookSettings.FfaSpellOnlySource()))
                    return;

                StampPvpActivity(mine.Match.MatchId, now);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] conduct signals: stamping {Name}'s attack attempt on {target?.Name} threw; the attack is unaffected", ex);
            }
        }

        /// <summary>
        /// The one PvP branch of DamageHistory.Add, called for every damaging health write that applied at least one point (melee, missile,
        /// spell projectile, life magic, DoT ticks, pet damage) with <paramref name="amount"/>, the health actually removed. Two jobs:
        ///
        /// <para/>
        /// 1. Engagement (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders"): when <paramref name="victim"/> and <paramref name="attacker"/> are
        /// two players bound to the same Live match on different teams (<see cref="CrystalDefenderReduction.CountsAsEngagement"/>), stamps
        /// BOTH, so one site records damage dealt and damage taken. Only a player's own hit counts; a pet's does not.
        ///
        /// <para/>
        /// 2. Performance stats (Docs/Pvp/BATTLEGROUNDS.md "Performance stats"): the same player-on-player hit credits player damage to the attacker in the match's ledger, and a defender's damage near / far the
        /// vulnerable crystal by the VICTIM's position; a hit on a crystal of the attacker's own Live match by a non-defender
        /// (<see cref="CrystalDefenderReduction.IsAttackerHit"/>) credits crystal damage, except the kill chip
        /// (<see cref="Creature.BattlegroundKillChipActive"/>). Collection only.
        ///
        /// <para/>
        /// HOT PATH: a creature victim that is no crystal costs one field read; a player victim hit by anything but a player costs
        /// two type tests. Nothing allocates before a hit is known to be PvP.
        /// </summary>
        /// <remarks>
        /// Threading: this runs on the thread applying the damage, which for a match is the match space's one landblock (every bound, Live
        /// player and every crystal is in it). The victim's Location is read there, on the thread that owns it. The ledger is lock-free.
        /// </remarks>
        internal static void OnPvpDamageRecorded(Creature victim, WorldObject attacker, uint amount)
        {
            if (victim is Player target)
            {
                // A combat pet's hit is not a player's (owner ruling 2026-10-08, consistent with PvpClassifier): it credits nobody.
                var source = attacker as Player;

                if (source == null || ReferenceEquals(target, source))
                    return;

                var targetBinding = target.PvpBinding;
                var sourceBinding = source.PvpBinding;

                if (!CrystalDefenderReduction.CountsAsEngagement(targetBinding, sourceBinding))
                    return;

                var now = PvpArenaHookSettings.UtcNow();
                var matchId = targetBinding.Match.MatchId;

                target.StampPvpEngagement(matchId, now);
                source.StampPvpEngagement(matchId, now);

                CreditPvpPlayerDamage(target, targetBinding, source, sourceBinding, amount, now);
                return;
            }

            var tag = victim?.BattlegroundObjective;

            if (tag != null)
                CreditPvpCrystalDamage(victim, tag, attacker, amount);
        }

        /// <summary>
        /// The player half of <see cref="OnPvpDamageRecorded"/>'s credit: the conduct activity stamp for BOTH players (Docs/Pvp/BATTLEGROUNDS.md
        /// "Conduct signals") and the performance credit. Only for two bindings to the SAME match instance, and never for damage the victim does
        /// not take (Invincible, or lifestone protection, which a DoT tick records before discarding): a refused write stamps nobody. The stamp
        /// does not need a ledger; the credit does.
        /// </summary>
        private static void CreditPvpPlayerDamage(Player target, PvpPlayerBinding targetBinding, Player source, PvpPlayerBinding sourceBinding, uint amount, DateTime now)
        {
            try
            {
                var match = sourceBinding.Match;

                if (!ReferenceEquals(match, targetBinding.Match))
                    return;

                if (ACE.Server.WorldEvents.WorldEventHooks.VictimStateRefusesCredit(target.Invincible, target.UnderLifestoneProtection))
                    return;

                // Conduct signals: dealing AND taking enemy damage that landed are both activity (a DoT tick from an enemy included).
                target.StampPvpActivity(match.MatchId, now);
                source.StampPvpActivity(match.MatchId, now);

                match.Performance?.CreditPlayerDamage(source.Guid.Full, amount, target.Location);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] performance stats: crediting {source?.Name}'s hit on {target?.Name} threw; the hit is unaffected", ex);
            }
        }

        /// <summary>The crystal half of <see cref="OnPvpDamageRecorded"/>'s performance credit.</summary>
        private static void CreditPvpCrystalDamage(Creature crystal, BattlegroundObjectiveTag tag, WorldObject attacker, uint amount)
        {
            try
            {
                if (crystal.BattlegroundKillChipActive)
                    return;

                var source = attacker as Player;
                var binding = source?.PvpBinding;

                if (!CrystalDefenderReduction.IsAttackerHit(tag, binding))
                    return;

                // Conduct signals: a crystal hit is activity for the attacker.
                source.StampPvpActivity(binding.Match.MatchId, PvpArenaHookSettings.UtcNow());

                binding.Match.Performance?.CreditCrystalDamage(source.Guid.Full, amount);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] performance stats: crediting crystal {tag.Index} of match {tag.MatchId} threw; the hit is unaffected", ex);
            }
        }

        /// <summary>
        /// This player as a candidate defender for the engaged-defender reduction: binding, alive (not dead and not in the death process),
        /// position and engagement stamp. Read on the crystal's landblock thread, which is this player's own while they stand in the match copy.
        /// </summary>
        internal DefenderSample ToDefenderSample() => new DefenderSample(PvpBinding, !IsDead && !IsInDeathProcess, Location, pvpEngagement);
    }
}
