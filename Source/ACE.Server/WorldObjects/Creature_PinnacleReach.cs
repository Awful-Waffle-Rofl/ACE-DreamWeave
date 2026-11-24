namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        /// <summary>
        /// A per-creature melee reach in metres that a Pinnacle fight sets on its own boss (IPinnacleWorld.SetMeleeReach):
        /// a huge creature whose body keeps the player far from its centre would otherwise never get within the retail
        /// 0.75 m and never swing. Runtime only, never persisted; null (the default, every other creature) is retail.
        /// </summary>
        public float? PinnacleMeleeReach { get; set; }

        /// <summary>
        /// The melee range the monster AI gates on (IsMeleeRange, and GetMaxRange's melee branch): the Pinnacle reach
        /// when one is set and wider than retail, else exactly <see cref="MaxMeleeRange"/>.
        /// </summary>
        public float MeleeRange => MeleeRangeFor(PinnacleMeleeReach);

        /// <summary>The pure half of <see cref="MeleeRange"/>: a finite reach wider than retail, else retail.</summary>
        internal static float MeleeRangeFor(float? reach) => reach is float r && float.IsFinite(r) && r > MaxMeleeRange ? r : MaxMeleeRange;
    }
}
