using System.Globalization;

namespace ACE.Server.PuzzleGates
{
    /// <summary>
    /// Per-site layout numbers of the puzzle gate: how far ahead of the anchor the gate stands and how high the
    /// sigil lights, the indicator beams and the beam hub hang. The defaults are the constants an admin
    /// /puzzlegate placement uses; the site tool derives different numbers where a ceiling is low or a corridor
    /// needs the lever row further from the gate. Pure and immutable; <see cref="TryValidate"/> names the first
    /// out-of-range field. The lever row distance stays a constant (3 m), so the gate distance never moves the levers.
    /// </summary>
    public sealed class PuzzleLayoutParams
    {
        public static readonly PuzzleLayoutParams Default = new PuzzleLayoutParams();

        public float GateDistance { get; init; } = PuzzleGateTunables.GateDistance;
        public float LightHeight { get; init; } = PuzzleGateTunables.SigilLightHeight;
        public float IndicatorHeight { get; init; } = PuzzleGateTunables.IndicatorHeight;
        public float HubHeight { get; init; } = PuzzleGateTunables.HubHeight;

        public bool IsDefault => GateDistance == PuzzleGateTunables.GateDistance && LightHeight == PuzzleGateTunables.SigilLightHeight
            && IndicatorHeight == PuzzleGateTunables.IndicatorHeight && HubHeight == PuzzleGateTunables.HubHeight;

        public bool TryValidate(out string error)
        {
            error = Check("gateDistance", GateDistance, PuzzleGateTunables.MinGateDistance, PuzzleGateTunables.MaxGateDistance)
                ?? Check("lightHeight", LightHeight, PuzzleGateTunables.MinLightHeight, PuzzleGateTunables.MaxLightHeight)
                ?? Check("indicatorHeight", IndicatorHeight, PuzzleGateTunables.MinIndicatorHeight, PuzzleGateTunables.MaxIndicatorHeight)
                ?? Check("hubHeight", HubHeight, PuzzleGateTunables.MinHubHeight, PuzzleGateTunables.MaxHubHeight);
            return error == null;
        }

        private static string Check(string name, float value, float min, float max)
        {
            // NaN fails both comparisons, so it is rejected too
            if (value >= min && value <= max)
                return null;
            return $"layout.{name} {value.ToString(CultureInfo.InvariantCulture)} must be {min.ToString(CultureInfo.InvariantCulture)}-{max.ToString(CultureInfo.InvariantCulture)}";
        }
    }
}
