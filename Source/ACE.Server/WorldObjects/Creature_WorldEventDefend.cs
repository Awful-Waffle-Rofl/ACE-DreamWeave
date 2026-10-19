using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using MotionTable = ACE.Server.Physics.Animation.MotionTable;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// World Events Hold, protect.defendPose (2026-10-19): a protected target that looks like it defends itself. It keeps its
    /// combat stance, turns to the run monster in reach and plays swing animations at it, then turns back outward.
    ///
    /// Purely visual, by construction: a swing is one motion broadcast whose facing target is the monster. Nothing here
    /// calls a damage, proc, spell or threat path, sets AttackTarget on either side, or writes to the monster at all, so it
    /// cannot damage, debuff, pull or earn kill credit, and the target never moves (its spot is never left). Only the run's
    /// monsters are ever offered (WorldEvent.TickHoldDefendPose), so it never faces or swings at a player.
    /// </summary>
    partial class Creature
    {
        /// <summary>Where the target stood, facing outward, when its defense began (the turn back aims here). Landblock thread only.</summary>
        public Position WorldEventDefendHome { get; private set; }

        /// <summary>The monster it faces now (0 = none).</summary>
        public uint WorldEventDefendFoe { get; private set; }

        /// <summary>When the next swing may start (unix time).</summary>
        public double WorldEventDefendNextSwingAt { get; private set; }

        /// <summary>When a monster was last in reach (unix time).</summary>
        private double worldEventDefendLastFoeAt;

        /// <summary>True while it faces outward (no monster since the turn back).</summary>
        public bool WorldEventDefendFacingOutward { get; private set; } = true;

        /// <summary>How many swings it has played this run (the log and the tests read it).</summary>
        public int WorldEventDefendSwings { get; private set; }

        /// <summary>The swing animations each motion table has in each stance, found once.</summary>
        private static readonly ConcurrentDictionary<(uint Table, MotionStance Stance), MotionCommand[]> defendSwingCache =
            new ConcurrentDictionary<(uint, MotionStance), MotionCommand[]>();

        /// <summary>A swing's length when the motion table gives none.</summary>
        private const float DefaultDefendSwingSeconds = 1.5f;

        /// <summary>
        /// One defense step (on the target's own landblock): <paramref name="foe"/> is the run monster in reach, or null.
        /// Returns what it did. Never touches <paramref name="foe"/>.
        /// </summary>
        public WorldEventHoldDefense.PoseAction TickWorldEventDefendPose(Creature foe, double now, DefendPoseDef pose, Random rng)
        {
            if (IsDead || !WorldEventProtected || pose == null)
                return WorldEventHoldDefense.PoseAction.None;

            WorldEventDefendHome ??= Location != null ? new Position(Location) : null;

            var inStance = CurrentMotionState != null && CurrentMotionState.Stance != MotionStance.NonCombat
                           && CurrentMotionState.Stance != MotionStance.Invalid;

            if (foe != null && (foe.IsDead || foe.Guid == Guid || foe is Player))
                foe = null;

            if (foe != null)
            {
                worldEventDefendLastFoeAt = now;

                if (foe.Guid.Full != WorldEventDefendFoe)
                    WorldEventDefendFoe = foe.Guid.Full;
            }
            else
                WorldEventDefendFoe = 0;

            var angle = foe != null && Location != null && foe.Location != null ? GetAngle(foe) : 0;

            var action = WorldEventHoldDefense.Decide(inStance, foe != null, angle, now, WorldEventDefendNextSwingAt,
                worldEventDefendLastFoeAt, WorldEventDefendFacingOutward, pose);

            switch (action)
            {
                case WorldEventHoldDefense.PoseAction.DrawWeapon:
                    DrawWorldEventDefendWeapon();
                    break;

                case WorldEventHoldDefense.PoseAction.Face:
                    WorldEventDefendFacingOutward = false;

                    if (CurrentLandblock != null)
                    {
                        var turn = Rotate(foe);
                        WorldEventDefendNextSwingAt = Math.Max(WorldEventDefendNextSwingAt, now + turn);
                    }
                    break;

                case WorldEventHoldDefense.PoseAction.Swing:
                    WorldEventDefendFacingOutward = false;
                    PlayWorldEventDefendSwing(foe, now, pose, rng);
                    break;

                case WorldEventHoldDefense.PoseAction.FaceOutward:
                    WorldEventDefendFacingOutward = true;

                    if (CurrentLandblock != null && WorldEventDefendHome != null)
                        TurnTo(WorldEventDefendHome);
                    break;
            }

            return action;
        }

        /// <summary>Enters the melee stance with whatever it wields (fists when nothing), and keeps it.</summary>
        private void DrawWorldEventDefendWeapon()
        {
            if (CurrentLandblock == null)
            {
                // No world (a test): the stance alone, nothing broadcast.
                CurrentMotionState = new Motion(MotionStance.HandCombat);
                return;
            }

            SetCombatMode(CombatMode.Melee);
        }

        /// <summary>
        /// The swing (an animation only): one of the stance's swing motions the motion table has, aimed at <paramref name="foe"/>,
        /// then the next swing after the animation and a normal attack rhythm. Returns the motion played.
        /// </summary>
        public MotionCommand PlayWorldEventDefendSwing(Creature foe, double now, DefendPoseDef pose, Random rng)
        {
            rng ??= new Random();

            var stance = CurrentMotionState?.Stance ?? MotionStance.HandCombat;
            var swings = WorldEventDefendSwingMotions(stance);
            var command = swings[rng.Next(swings.Length)];

            float length = 0;

            try
            {
                length = MotionTable.GetAnimationLength(MotionTableId, stance, command);
            }
            catch (Exception)
            {
                // No motion table readable: the default length keeps the rhythm.
            }

            if (!(length > 0))
                length = DefaultDefendSwingSeconds;

            if (CurrentLandblock != null && foe != null)
            {
                var motion = new Motion(this, command, 1.0f);
                motion.MotionState.TurnSpeed = 2.25f;
                motion.TargetGuid = foe.Guid;

                CurrentMotionState = motion;
                EnqueueBroadcastMotion(motion);
            }

            WorldEventDefendSwings++;
            WorldEventDefendNextSwingAt = WorldEventHoldDefense.NextSwingAt(now, length, rng, pose);

            return command;
        }

        /// <summary>The swing motions this target's motion table has in <paramref name="stance"/>; the stance's first candidate when none is found.</summary>
        private MotionCommand[] WorldEventDefendSwingMotions(MotionStance stance)
        {
            return defendSwingCache.GetOrAdd((MotionTableId, stance), key =>
            {
                var candidates = WorldEventHoldDefense.SwingCandidates(key.Stance);
                var found = new List<MotionCommand>();

                foreach (var candidate in candidates)
                {
                    try
                    {
                        if (key.Table != 0 && MotionTable.GetAnimationLength(key.Table, key.Stance, candidate) > 0)
                            found.Add(candidate);
                    }
                    catch (Exception)
                    {
                        // An unreadable table: fall through to the first candidate.
                    }
                }

                return found.Count > 0 ? found.ToArray() : new[] { candidates.First() };
            });
        }
    }
}
