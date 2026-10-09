namespace ACE.Server.CombatSimulator
{
    /// <summary>
    /// One measured character. LandedHitsToKill is the tuning target; HitChance is reported
    /// alongside it because it separates the roster without moving the damage knob.
    /// </summary>
    public class SimResult
    {
        public string Label { get; init; }
        public int Level { get; init; }
        public uint MaxHealth { get; init; }

        public float HitChance { get; init; }
        public float MeanDamagePerLandedHit { get; init; }

        /// <summary>Populated in sampling mode only; zero in analytic mode.</summary>
        public float P50Damage { get; init; }
        public float P90Damage { get; init; }

        public float LandedHitsToKill { get; init; }

        /// <summary>LandedHitsToKill divided by HitChance. Reported for context.</summary>
        public float SwingsToKill { get; init; }
    }
}
