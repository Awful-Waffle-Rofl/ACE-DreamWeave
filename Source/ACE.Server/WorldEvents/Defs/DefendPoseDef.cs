using System.Text.Json.Serialization;

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
        public double SwingSeconds { get; set; } = 2.0;

        /// <summary>The longest time between two swings, in seconds (swingSeconds to 10). Default 3.</summary>
        [JsonPropertyName("swingMaxSeconds")]
        public double SwingMaxSeconds { get; set; } = 3.0;

        /// <summary>How long after the last monster leaves reach the target turns back outward, in seconds (0 to 30). Default 3.</summary>
        [JsonPropertyName("turnBackSeconds")]
        public double TurnBackSeconds { get; set; } = 3.0;

        /// <summary>A swing is preceded by a turn when the monster is more than this far off the target's facing, in degrees (5 to 90). Default 20.</summary>
        [JsonPropertyName("faceDegrees")]
        public double FaceDegrees { get; set; } = 20;
    }
}