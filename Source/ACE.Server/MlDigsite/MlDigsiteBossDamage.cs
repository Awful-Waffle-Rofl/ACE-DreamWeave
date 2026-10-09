using System;
using System.Reflection;

using ACE.Server.Entity.Actions;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.MlDigsite
{
    /// <summary>Why <see cref="MlDigsiteBossDamage.TryNoteHit"/> did or did not record a hit. For tests and diagnosis only.</summary>
    public enum MlDigsiteBossHitOutcome
    {
        NotDigsite,
        NotBossRush,
        NoController,
        NotObjective,
        Disabled,
        Noted,
    }

    /// <summary>
    /// RoZ round 19 owner ruling: the digsite Boss Rush boss's damage is sized like a World Event boss's - the
    /// TOUGHEST present player survives about ml_mapevent_boss_hits_to_kill ordinary hits, low-level one-shots
    /// accepted. This reuses the World Event machinery rather than copying it: the same
    /// <see cref="WorldEventBossDamageController"/> (one per Boss Rush encounter, MlDigsiteEncounter.BossDamage)
    /// fed from the same single hook (<see cref="WorldEventBossDamageHook.NoteHit"/>, whose digsite branch sits
    /// behind its P_WorldEvent check and calls <see cref="TryNoteHit"/>).
    ///
    /// Aun Relaria has no owning tick and so no controller: it is spawn-time scaling only.
    /// </summary>
    public static class MlDigsiteBossDamage
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The digsite half of the damage hook. HOT PATH: every player-damage event in the server reaches the
        /// hook, and a creature with no world event reaches this. The first test is one field read and a null
        /// check, which is all an ordinary creature (or any non-Boss-Rush digsite creature, one more field read)
        /// ever pays. Only the Boss Rush boss itself goes on to take the encounter's lock.
        /// </summary>
        public static MlDigsiteBossHitOutcome TryNoteHit(Creature attacker, uint defenderMaxHealth, int damageTaken, bool crit)
        {
            var encounter = attacker?.P_DigsiteEncounter;

            if (encounter == null)
                return MlDigsiteBossHitOutcome.NotDigsite;

            if (encounter.Type != MlDigsiteType.BossRush)
                return MlDigsiteBossHitOutcome.NotBossRush;

            var controller = encounter.BossDamage;

            if (controller == null)
                return MlDigsiteBossHitOutcome.NoController;

            if (attacker.Guid.Full != encounter.ObjectiveGuid)
                return MlDigsiteBossHitOutcome.NotObjective;

            // Both kill switches, so a switched-off controller does not even buffer samples. Read only here,
            // after every cheaper rejection: only the Boss Rush boss's own hits ever reach these two cached reads.
            if (!MlMapEventTunables.SpreadScalingEnabled || !MlMapEventTunables.BossDamageScalingEnabled)
                return MlDigsiteBossHitOutcome.Disabled;

            controller.NoteHit(damageTaken, defenderMaxHealth, crit);

            return MlDigsiteBossHitOutcome.Noted;
        }

        /// <summary>
        /// The advise step, from the digsite world-thread tick (MlDigsiteManager.Drive). Cheap until the
        /// controller has a full sample window: the audience walk only runs once enough hits are buffered. The
        /// DamageRating write is ENQUEUED on the boss's landblock, with its try/catch INSIDE the queued action -
        /// EnqueueAction escapes the caller's try/catch, so a catch out here would never see a throw in there.
        /// </summary>
        public static void Drive(MlDigsiteEncounter encounter)
        {
            if (encounter == null || encounter.Type != MlDigsiteType.BossRush)
                return;

            var controller = encounter.BossDamage;

            if (controller == null)
                return;

            // The rating bounds, sample window and step cap are the World Event controller's shipped
            // constants; only hitsToKill has a map-event dial of its own.
            if (controller.PendingHits < WorldEventBossDamageController.DefaultSampleHits)
                return;

            var settings = MlMapEventTunables.Read();

            if (!settings.Enabled || !settings.BossDamageScalingEnabled)
                return;

            var boss = encounter.ObjectiveCreature;

            if (boss == null || boss.IsDestroyed || boss.IsDead)
                return;

            var landblock = boss.CurrentLandblock;

            // Mid-adjacency-transfer: try again next tick, without spending the sample window.
            if (landblock == null)
                return;

            var profiles = MlDigsiteAudience.Sample(encounter.Anchor, MlDigsiteTunables.AudienceRadiusMetres);
            var topMaxHealth = MlMapEventScaling.ToughestMaxHealth(profiles);

            var dials = new BossDamageDials(settings.BossHitsToKill,
                WorldEventBossDamageController.DefaultRatingMin,
                WorldEventBossDamageController.DefaultRatingMax,
                WorldEventBossDamageController.DefaultSampleHits,
                WorldEventBossDamageController.DefaultStepCap);

            var currentRating = boss.DamageRating ?? 0;

            if (!controller.TryAdvise(currentRating, topMaxHealth, dials, out var newRating, out var diag))
                return;

            var target = boss;

            landblock.EnqueueAction(new ActionEventDelegate(() =>
            {
                try
                {
                    if (target.IsDestroyed || target.IsDead || !encounter.RunValid)
                        return;

                    target.DamageRating = newRating;
                }
                catch (Exception ex)
                {
                    log.Error($"[ML_DIGSITE] {encounter} boss damage rating write threw for 0x{target.Guid.Full:X8}", ex);
                }
            }));

            log.Info($"[ML_DIGSITE] {encounter} boss damage rating {currentRating}->{newRating} topMaxHealth={topMaxHealth} players={profiles.Count} {diag} {dials}");
        }
    }
}
