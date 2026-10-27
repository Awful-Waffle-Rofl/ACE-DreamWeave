using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents.Defs
{
    /// <summary>
    /// protect.defendPose: the protected targets look like they fight back, and only look like it. Each stands in its
    /// combat stance, weapon drawn, for the whole run; when one of the run's monsters is within <see cref="Reach"/> it
    /// turns to face it and swings at it every <see cref="SwingSeconds"/> to <see cref="SwingMaxSeconds"/>. The swings are
    /// animations only: no damage, no debuff, no threat or target change, no kill credit, and never at a player. The
    /// target never leaves its spot, and turns back outward <see cref="TurnBackSeconds"/> after nothing is in reach.
    /// Absent: the targets stand at peace, as before.
    /// </summary>
    public class DefendPoseDef
    {
        /// <summary>How close a monster must be, edge to edge, to be swung at, in metres (0.5 to 6). Default 2.5.</summary>
        [JsonPropertyName("reach")]
        public double Reach { get; set; } = 2.5;

        /// <summary>The shortest time between two swings, in seconds (1 to 10). Default 2.</summary>
        [JsonPropertyName("swingSeconds")]
        public double SwingSecondsJson { get; set; } = 2.0;

        /// <summary>In force: world_events_hold_defend_swing_seconds when set, else goals.json (live).</summary>
        [JsonIgnore]
        public double SwingSeconds { get => HoldSettings.Number(HoldKeys.DefendSwingSeconds, SwingSecondsJson); set => SwingSecondsJson = value; }

        /// <summary>The longest time between two swings, in seconds (swingSeconds to 10). Default 3.</summary>
        [JsonPropertyName("swingMaxSeconds")]
        public double SwingMaxSecondsJson { get; set; } = 3.0;

        /// <summary>In force: world_events_hold_defend_swing_max_seconds when set, else goals.json; never below <see cref="SwingSeconds"/> (live).</summary>
        [JsonIgnore]
        public double SwingMaxSeconds
        {
            get => Math.Max(SwingSeconds, HoldSettings.Number(HoldKeys.DefendSwingMaxSeconds, SwingMaxSecondsJson));
            set => SwingMaxSecondsJson = value;
        }

        /// <summary>How long after the last monster leaves reach the target turns back outward, in seconds (0 to 30). Default 3.</summary>
        [JsonPropertyName("turnBackSeconds")]
        public double TurnBackSeconds { get; set; } = 3.0;

        /// <summary>A swing is preceded by a turn when the monster is more than this far off the target's facing, in degrees (5 to 90). Default 20.</summary>
        [JsonPropertyName("faceDegrees")]
        public double FaceDegrees { get; set; } = 20;
    }
}

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The cosmetic defense of protected targets (protect.defendPose, 2026-10-19). Pure rules (D6); Creature.TickWorldEventDefendPose
    /// applies them on the target's landblock, and WorldEvent.TickHoldDefendPose hands each target the monsters near it.
    /// Nothing here can touch a monster: a swing is a motion broadcast with the monster as its facing target, never a
    /// damage event.
    /// </summary>
    public static class WorldEventHoldDefense
    {
        /// <summary>What a target does this tick.</summary>
        public enum PoseAction
        {
            /// <summary>Nothing: waiting for the next swing, or already facing outward.</summary>
            None,

            /// <summary>Draw its weapon (enter the combat stance).</summary>
            DrawWeapon,

            /// <summary>Turn to face the monster first.</summary>
            Face,

            /// <summary>Swing at the monster (an animation only).</summary>
            Swing,

            /// <summary>Turn back outward.</summary>
            FaceOutward
        }

        /// <summary>
        /// The monster a target swings at (D6 - pure): the one it already faces while that one stays in reach, else the nearest
        /// in reach. <paramref name="foes"/> are the run's living monsters with their edge-to-edge gap in metres; players are
        /// never offered. 0 when none is in reach.
        /// </summary>
        public static uint PickFoe(IEnumerable<(uint Guid, double Gap)> foes, double reach, uint current)
        {
            var inReach = (foes ?? Enumerable.Empty<(uint, double)>())
                .Where(f => f.Guid != 0 && double.IsFinite(f.Gap) && f.Gap <= reach)
                .ToList();

            if (inReach.Count == 0)
                return 0;

            if (current != 0 && inReach.Any(f => f.Guid == current))
                return current;

            return inReach.OrderBy(f => f.Gap).ThenBy(f => f.Guid).First().Guid;
        }

        /// <summary>
        /// The next step (D6 - pure). In order: draw the weapon when at peace; with a foe, turn to it when it is off the
        /// facing, else swing when the rhythm allows; with none, turn back outward once <see cref="DefendPoseDef.TurnBackSeconds"/>
        /// have passed since the last foe.
        /// </summary>
        public static PoseAction Decide(bool inCombatStance, bool hasFoe, double angleToFoe, double now, double nextSwingAt,
            double lastFoeAt, bool facingOutward, DefendPoseDef pose)
        {
            pose ??= new DefendPoseDef();

            if (!inCombatStance)
                return PoseAction.DrawWeapon;

            if (hasFoe)
            {
                if (angleToFoe > pose.FaceDegrees)
                    return PoseAction.Face;

                return now >= nextSwingAt ? PoseAction.Swing : PoseAction.None;
            }

            if (!facingOutward && now - lastFoeAt >= pose.TurnBackSeconds)
                return PoseAction.FaceOutward;

            return PoseAction.None;
        }

        /// <summary>
        /// When the next swing may start (D6 - pure): after this swing's animation, and a normal attack rhythm of
        /// swingSeconds to swingMaxSeconds from now, whichever is later.
        /// </summary>
        public static double NextSwingAt(double now, double animSeconds, Random rng, DefendPoseDef pose)
        {
            pose ??= new DefendPoseDef();
            rng ??= new Random();

            var lo = Math.Max(0.5, pose.SwingSeconds);
            var hi = Math.Max(lo, pose.SwingMaxSeconds);
            var rhythm = lo + rng.NextDouble() * (hi - lo);

            return now + Math.Max(Math.Max(0, animSeconds), rhythm);
        }

        /// <summary>
        /// The swing animations a stance may use, best first (D6 - pure). The caller keeps the ones the target's motion table has.
        /// </summary>
        public static IReadOnlyList<MotionCommand> SwingCandidates(MotionStance stance)
        {
            switch (stance)
            {
                case MotionStance.HandCombat:
                    return new[] { MotionCommand.AttackHigh1, MotionCommand.AttackMed1, MotionCommand.AttackLow1, MotionCommand.AttackMed2 };

                case MotionStance.DualWieldCombat:
                case MotionStance.SwordCombat:
                case MotionStance.SwordShieldCombat:
                case MotionStance.TwoHandedSwordCombat:
                case MotionStance.TwoHandedStaffCombat:
                    return new[] { MotionCommand.SlashHigh, MotionCommand.SlashMed, MotionCommand.SlashLow, MotionCommand.BackhandMed,
                                   MotionCommand.ThrustMed, MotionCommand.ThrustHigh };

                default:
                    return new[] { MotionCommand.SlashMed, MotionCommand.ThrustMed, MotionCommand.BackhandMed, MotionCommand.AttackMed1 };
            }
        }

        /// <summary>The goals.json check (D6 - pure). Null when valid.</summary>
        public static string Validate(DefendPoseDef pose)
        {
            if (pose == null)
                return null;

            if (!double.IsFinite(pose.Reach) || pose.Reach < 0.5 || pose.Reach > 6)
                return "has an invalid protect.defendPose.reach (0.5 to 6)";

            if (!double.IsFinite(pose.SwingSecondsJson) || !double.IsFinite(pose.SwingMaxSecondsJson) || pose.SwingSecondsJson < 1
                || pose.SwingMaxSecondsJson < pose.SwingSecondsJson || pose.SwingMaxSecondsJson > 10)
                return "has an invalid protect.defendPose swing rhythm (swingSeconds 1 to 10, swingMaxSeconds not below it and up to 10)";

            if (!double.IsFinite(pose.TurnBackSeconds) || pose.TurnBackSeconds < 0 || pose.TurnBackSeconds > 30)
                return "has an invalid protect.defendPose.turnBackSeconds (0 to 30)";

            if (!double.IsFinite(pose.FaceDegrees) || pose.FaceDegrees < 5 || pose.FaceDegrees > 90)
                return "has an invalid protect.defendPose.faceDegrees (5 to 90)";

            return null;
        }
    }
}
