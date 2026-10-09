using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Attack/Defend crystals (Docs/Pvp/ATTACK-DEFEND.md "Crystals"): the in-memory match tag a defender crystal carries,
    /// and the death-path predicate that reads it. Follows the P_WorldEvent back-reference pattern (Creature.cs): a plain
    /// in-memory field, set by the spawner before EnterWorld and NEVER persisted - a crystal exists only in its match's own
    /// ephemeral copy, which is never saved. No WorldObject subclass and no property id.
    /// </summary>
    partial class Creature
    {
        private BattlegroundObjectiveTag battlegroundObjective;

        /// <summary>The crystal's match tag, or null for every other creature. Immutable once set.</summary>
        public BattlegroundObjectiveTag BattlegroundObjective => battlegroundObjective;

        /// <summary>
        /// Tags this creature as a match objective. Write-once: returns false (and changes nothing) for a null tag or when a tag
        /// is already set. The spawner calls it before EnterWorld, on the instance landblock's own queue, which is also the only
        /// thread that reads it afterwards (combat and death resolve there).
        /// </summary>
        public bool SetBattlegroundObjective(BattlegroundObjectiveTag tag)
        {
            if (tag == null || battlegroundObjective != null)
                return false;

            battlegroundObjective = tag;
            return true;
        }

        /// <summary>
        /// True only while <see cref="LivePvpMatchSpaces.ApplyKillEffect"/> runs a kill chip through TakeDamage: the chip is a percent of the
        /// crystal's maximum, not an attacker's hit, so the engaged-defender reduction must not shrink it. Landblock thread only.
        /// </summary>
        internal bool BattlegroundKillChipActive;

        /// <summary>
        /// Where the engaged-defender reduction reads its candidate defenders: every player on the crystal's own landblock, sampled on that
        /// landblock's thread (this method is only ever called from a damage sink running there). A seam so a test can supply samples
        /// without a live landblock; production never reassigns it.
        /// </summary>
        internal static Func<Creature, IEnumerable<DefenderSample>> DefenderSampleSource = crystal =>
            crystal.CurrentLandblock?.PlayersOnLandblockThread.Select(p => p.ToDefenderSample()) ?? Enumerable.Empty<DefenderSample>();

        /// <summary>
        /// The engaged-defender reduction (Docs/Pvp/ATTACK-DEFEND.md "Engaged defenders") for a hit of <paramref name="amount"/> from
        /// <paramref name="source"/> on this creature. Unchanged for every creature but a tagged crystal, for the kill chip, for a source
        /// that is not a player, and for anything <see cref="CrystalDefenderReduction.Apply"/> does not reduce. Called by the two crystal
        /// damage sinks, Creature.TakeDamage and SpellProjectile.DamageTarget, before the health write; both run on this crystal's
        /// landblock thread, which is also where every defender's position and binding are read here.
        /// </summary>
        /// <remarks>
        /// Every creature hit in the game runs this, so it is only the null-tag guard (small enough for the JIT to inline at both sinks);
        /// the work and its try/catch live in <see cref="ApplyBattlegroundDefenderReductionToCrystal"/>, reached only for a crystal.
        /// </remarks>
        internal float ApplyBattlegroundDefenderReduction(WorldObject source, float amount)
        {
            if (battlegroundObjective == null)
                return amount;

            return ApplyBattlegroundDefenderReductionToCrystal(source, amount);
        }

        /// <summary>The body of <see cref="ApplyBattlegroundDefenderReduction"/> for a tagged crystal.</summary>
        private float ApplyBattlegroundDefenderReductionToCrystal(WorldObject source, float amount)
        {
            var tag = battlegroundObjective;

            if (tag == null || BattlegroundKillChipActive || !(amount > 0f) || source is not Player attacker)
                return amount;

            try
            {
                var binding = attacker.PvpBinding;

                if (!CrystalDefenderReduction.IsAttackerHit(tag, binding) || binding.Match.DefenderReduction == null)
                    return amount;

                return CrystalDefenderReduction.Apply(tag, binding, Location, DefenderSampleSource(this), PvpArenaHookSettings.UtcNow(), amount);
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] crystal {tag.Index} of match {tag.MatchId} (0x{Guid}): the engaged-defender reduction threw; the hit lands unreduced", ex);
                return amount;
            }
        }

        /// <summary>
        /// True when this death is a match objective's: the crystal leaves no corpse, rolls no treasure, credits no creature kill,
        /// and skips every killer hook, kill quest, death spawn and XP grant. Untagged creatures (every creature but a crystal)
        /// answer false and keep the full death path.
        ///
        /// <para/>
        /// Named "Corpseless" for the same reason as its siblings in Creature_LootRolls.cs: PooledLootWiringTests asserts that
        /// Creature.Die's body never names the engine's corpse-suppression property, because that property still rolls treasure.
        /// </summary>
        internal static bool IsBattlegroundObjectiveCorpselessDeath(BattlegroundObjectiveTag tag) => tag != null;

        /// <summary>
        /// Reports the crystal's destruction to the match coordinator, once. Called from Die after its exactly-once dieEntered
        /// guard, so a crystal is never reported twice. The killer is the last damager when it is a player, else 0, the same
        /// rule a match death uses (Player.LatchPvpMatchDeath). The coordinator decides everything else on the world thread.
        /// </summary>
        private void ReportBattlegroundObjectiveDestroyed(DamageHistoryInfo lastDamager)
        {
            var tag = battlegroundObjective;

            if (tag == null)
                return;

            try
            {
                var killerId = lastDamager != null && lastDamager.IsPlayer ? lastDamager.Guid.Full : 0u;

                PvpMatchManager.Report(PvpMatchManager.ObjectiveDestroyed(tag.MatchId, tag.Index, killerId, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                log.Error($"[PVP] crystal {tag.Index} of match {tag.MatchId} (0x{Guid}): reporting its destruction threw", ex);
            }
        }
    }
}
