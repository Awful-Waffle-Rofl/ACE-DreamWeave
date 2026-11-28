using System;

using log4net;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// Shapes and places Balance Trials creatures. A trial creature is a solo Thread creature in every way that matters to the
    /// fight and the payout: it goes through <see cref="ThreadDungeonSpawner.ShapeCreature"/> with Rewards = true (live XP and
    /// luminance, live health, damage rating and creature abilities), and only its loot is removed afterwards
    /// (<see cref="Creature.StripLootRewards"/>). XP and luminance are then paid by the ordinary death path from XpOverride and
    /// LuminanceAward; nothing here prices them.
    ///
    /// Run = null drops only what PrepareForPlacement reads off the run: the group (Solo for a trial; GroupEffort carries
    /// Solo.Effort), the log tag (LogTag stands in) and the boss-health telemetry row (TrialSpawnerTests pins that set).
    ///
    /// Persistence: a trial room is an ephemeral landblock copy. Landblock.SaveDB returns before iterating any object when
    /// IsEphemeral, and Landblock.Unload destroys every non-player object of an ephemeral copy, so no trial creature is ever
    /// written to the shard. A trial creature carries no ThreadDungeonRunId and no generator, so on a PERSISTENT landblock
    /// IsDynamicThatShouldPersistToShard would let it through: the ephemeral room is the guarantee, and <see cref="Spawn"/>
    /// refuses any landblock that is not ephemeral before it creates anything.
    /// </summary>
    internal static class TrialSpawner
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The live solo shape: no run, rewards on, the solo group effort.</summary>
        internal static CreatureShapeOptions ShapeOptions(string tag) => new CreatureShapeOptions
        {
            Run = null,
            Rewards = true,
            GroupEffort = GroupScaling.Solo.Effort,
            LogTag = tag,
        };

        /// <summary>
        /// The off-world half of a trial spawn, in LiveSpeedCourseWorld.Spawn's order: the Thread shaping with rewards on, the
        /// loot strip, the kill-task strip, then the rep stamp. Touches no Location, landblock or EnterWorld. Reads the
        /// PropertyManager keys PrepareForPlacement reads.
        /// </summary>
        internal static void Prepare(Creature c, DungeonSpawnPlan plan, DungeonSpawnPlanEntry e, uint observedMaxNonBoss, TrialRep rep, string tag)
        {
            ThreadDungeonSpawner.ShapeCreature(c, plan, e, observedMaxNonBoss, ShapeOptions(tag));

            // Rewards = true wrote the plan's loot profile and salvage affinities; a trial pays XP and luminance only.
            c.StripLootRewards();

            // Trial kills must not advance retail kill tasks (the speed-course strip).
            c.RemoveProperty(PropertyString.KillQuest);
            c.RemoveProperty(PropertyString.KillQuest2);
            c.RemoveProperty(PropertyString.KillQuest3);

            // Membership: feeds no kill-fill vessel and no mule-form token (Creature.FeedsKillProgressHooks).
            c.P_TrialRep = rep;

            // The role the recorder's hooks report (the trial path never sets Creature.DungeonRole).
            c.P_TrialRole = e.Role;
        }

        /// <summary>
        /// Creates, shapes and places one trial creature at its plan point in <paramref name="instance"/>. Returns the creature
        /// that entered the world, or null (every refusal destroys what it created). Must be called on <paramref name="lb"/>'s
        /// action queue: it enters the world synchronously and does not enqueue. <paramref name="holdUntil"/> above 0 (wave 0 of a rep
        /// with a start grace) is set as the creature's <see cref="Creature.P_TrialHoldUntil"/> BEFORE EnterWorld, so the spawn scan
        /// EnterWorld schedules (Creature.CheckTargets) already finds it held.
        /// </summary>
        internal static Creature Spawn(DungeonSpawnPlan plan, DungeonSpawnPlanEntry e, uint observedMaxNonBoss, TrialRep rep, Landblock lb, uint instance, string tag, double holdUntil = 0.0)
        {
            if (plan == null || e?.Point == null || lb == null)
                return null;

            // Fail closed: off an ephemeral copy nothing stops the shard save (see the class summary). Before any create.
            if (!lb.IsEphemeral)
            {
                log.Error($"[TRIAL] {tag} landblock 0x{lb.Id.Landblock:X4} instance {lb.Instance} is not ephemeral; nothing spawned");
                return null;
            }

            WorldObject wo;

            try
            {
                wo = WorldObjectFactory.CreateNewWorldObject(e.Wcid);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] {tag} could not create wcid {e.Wcid}", ex);
                return null;
            }

            if (wo is not Creature creature)
            {
                log.Warn($"[TRIAL] {tag} wcid {e.Wcid} is not a Creature; skipped");
                wo?.Destroy();
                return null;
            }

            if (!DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus))
            {
                log.Error($"[TRIAL] {tag} wcid {e.Wcid} ({e.Role}) is not attackable or is an NPC; not placed");
                creature.Destroy();
                return null;
            }

            var p = e.Point;
            creature.Location = new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, instance);

            if (creature.Location.LandblockId.Landblock != lb.Id.Landblock)
            {
                log.Warn($"[TRIAL] {tag} point {creature.Location.ToLOCString()} is not on the room landblock 0x{lb.Id.Landblock:X4}; dropped");
                creature.Destroy();
                return null;
            }

            try
            {
                Prepare(creature, plan, e, observedMaxNonBoss, rep, tag);
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] {tag} shaping threw for wcid {e.Wcid}", ex);
                try { creature.Destroy(); } catch { }
                return null;
            }

            if (holdUntil > 0)
                creature.P_TrialHoldUntil = holdUntil;

            var pred = Predict(creature, e, rep, tag);

            try
            {
                if (!creature.EnterWorld())
                {
                    log.Warn($"[TRIAL] {tag} EnterWorld refused wcid {e.Wcid} at {creature.Location.ToLOCString()}");
                    creature.Destroy();
                    return null;
                }
            }
            catch (Exception ex)
            {
                log.Error($"[TRIAL] {tag} EnterWorld threw for wcid {e.Wcid}", ex);
                try { creature.Destroy(); } catch { }
                return null;
            }

            EmitSpawn(e, rep, pred, tag);

            return creature;
        }

        /// <summary>
        /// The simulator prediction for the shaped creature, before it enters the world: null when the rep has no player side
        /// yet (or it failed), when the creature has no attack, or when the simulator throws (a spawn never fails on a prediction).
        /// Nothing is stored on the rep here: the boss's prediction is recorded by <see cref="EmitSpawn"/>, once the creature is in.
        /// </summary>
        private static TrialPrediction Predict(Creature creature, DungeonSpawnPlanEntry e, TrialRep rep, string tag)
        {
            try
            {
                var pred = TrialPrediction.For(creature, rep?.PlayerSide, out var note);

                if (pred == null)
                {
                    if (rep?.PlayerSide != null)
                        log.Warn($"[TRIAL] {tag} no prediction for wcid {e.Wcid} ({e.Role}): {note}");

                    return null;
                }

                return pred;
            }
            catch (Exception ex)
            {
                log.Warn($"[TRIAL] {tag} the prediction for wcid {e.Wcid} ({e.Role}) threw; spawning without one", ex);
                return null;
            }
        }

        /// <summary>
        /// The spawn event, and the boss's prediction for the run row's pred_* columns, for a creature that actually entered the
        /// world: a refused boss leaves the columns null.
        /// </summary>
        private static void EmitSpawn(DungeonSpawnPlanEntry e, TrialRep rep, TrialPrediction pred, string tag)
        {
            try
            {
                if (rep != null && pred != null && e.Role == DungeonRole.Boss)
                    rep.BossPrediction = pred;

                rep?.Recorder?.OnSpawn(e.Wcid, e.Role, pred?.ToJson(), TrialManager.Now());
            }
            catch (Exception ex)
            {
                log.Warn($"[TRIAL] {tag} the spawn event for wcid {e.Wcid} threw", ex);
            }
        }

        /// <summary>
        /// The boss health floor's pack term, over every non-boss entry of the WHOLE plan (not one wave): each is created,
        /// shaped exactly as <see cref="Prepare"/> shapes a placed one (observedMaxNonBoss 0 and no rep; neither changes a
        /// non-boss creature's health), measured and destroyed. A Thread run reaches the same value by placing the pack before
        /// its boss. Never a silent undercount: a null create throws, and a create or shaping that throws propagates, so the
        /// caller (prepareRep) refuses the rep rather than build a boss floor that is too low. Whatever was created is destroyed.
        /// </summary>
        internal static uint ObservedMaxNonBoss(DungeonSpawnPlan plan, Func<uint, Creature> create, string tag)
        {
            var max = 0u;

            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            if (create == null)
                throw new ArgumentNullException(nameof(create));

            foreach (var e in plan.Entries)
            {
                if (e == null || e.Role == DungeonRole.Boss)
                    continue;

                Creature c = null;

                try
                {
                    c = create(e.Wcid) ?? throw new InvalidOperationException($"[TRIAL] {tag} observing the pack max: wcid {e.Wcid} ({e.Role}) could not be created");

                    Prepare(c, plan, e, observedMaxNonBoss: 0, rep: null, tag);

                    if (c.Health.MaxValue > max)
                        max = c.Health.MaxValue;
                }
                finally
                {
                    if (c != null)
                    {
                        try { c.Destroy(); } catch { }
                    }
                }
            }

            return max;
        }
    }
}
