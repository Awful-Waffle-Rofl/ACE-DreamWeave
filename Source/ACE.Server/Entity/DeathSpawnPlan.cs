using System;

namespace ACE.Server.Entity
{
    /// <summary>
    /// "What to spawn when this creature dies, and how far to scatter it" as a PURE decision over three
    /// already-read weenie properties, so the clamping rules are unit testable without a live Creature or a
    /// live landblock (D6 - no test on this server may stand up a Player, and by the same token none stands
    /// up a world).
    ///
    /// The primitive behind the Bluespire rung 5 shell swap - a first form dies, adds step out and the real
    /// boss with them - expressed generically so it carries any later "adds on death" with no per-boss code.
    ///
    /// TWO ALTERNATIVES WERE REJECTED AND SHOULD NOT BE REDISCOVERED. The MonsterEffects system has no death
    /// hook at all (its interfaces are outgoing-hit, incoming-damage, avoidance, spell-hit, speed-mod, cast,
    /// heartbeat and ramp-source). And the data-only EmoteType.StartEvent route is unsound because
    /// EventManager.Events is a single GLOBAL dictionary with no instance or landblock scoping and no auto
    /// stop, so one player triggering phase 2 would leave phase 2 standing for everyone who came after them.
    /// </summary>
    public readonly struct DeathSpawnPlan
    {
        /// <summary>
        /// Most copies one death may place. A cap rather than a validation error because the death has
        /// already happened by the time this is read - there is nothing to refuse - and because the failure
        /// this guards against is a typo (a count authored where a wcid was meant) filling a room.
        /// </summary>
        public const int MaxCount = 20;

        /// <summary>
        /// Scatter radius used when the weenie names a wcid but no radius, or names an unusable one. Small
        /// on purpose: a shell swap wants its adds beside the shell, and the caller retries at the dying
        /// creature's exact position when a scattered point will not take a body.
        /// </summary>
        public const float DefaultRadius = 3.0f;

        /// <summary>
        /// Ceiling on the scatter radius. A dungeon room is the unit here, and a radius large enough to
        /// cross a wall produces adds that either fail to place or appear in the next room.
        /// </summary>
        public const float MaxRadius = 30.0f;

        /// <summary>False when this creature spawns nothing on death, which is every creature in the game bar a handful.</summary>
        public bool ShouldSpawn { get; }

        /// <summary>The weenie to place. Meaningless unless <see cref="ShouldSpawn"/>.</summary>
        public uint Wcid { get; }

        /// <summary>How many copies, always 1..<see cref="MaxCount"/> when <see cref="ShouldSpawn"/>.</summary>
        public int Count { get; }

        /// <summary>Scatter radius in metres, always in (0, <see cref="MaxRadius"/>] when <see cref="ShouldSpawn"/>.</summary>
        public float Radius { get; }

        private DeathSpawnPlan(bool shouldSpawn, uint wcid, int count, float radius)
        {
            ShouldSpawn = shouldSpawn;
            Wcid = wcid;
            Count = count;
            Radius = radius;
        }

        /// <summary>A creature that spawns nothing. The answer for almost every death on the server.</summary>
        public static DeathSpawnPlan None => new DeathSpawnPlan(false, 0, 0, 0f);

        /// <summary>
        /// The plan for one dying creature.
        ///
        /// An absent, zero or negative wcid is the OFF state and yields <see cref="None"/> - the whole cost
        /// on every other death in the game. A wcid is also rejected above uint.MaxValue, which a PropertyInt
        /// cannot reach, but the cast is explicit rather than implicit so the range check is visible.
        ///
        /// An absent count reads as 1 rather than 0: a weenie that names something to spawn and forgets to
        /// say how many plainly means one, and reading it as zero would make the whole feature silently
        /// inert on the most likely authoring mistake.
        /// </summary>
        /// <param name="wcid">PropertyInt 9070 DeathSpawnWcid</param>
        /// <param name="count">PropertyInt 9071 DeathSpawnCount</param>
        /// <param name="radius">PropertyFloat 9013 DeathSpawnRadius</param>
        public static DeathSpawnPlan Decide(int? wcid, int? count, double? radius)
        {
            if (wcid == null || wcid.Value <= 0)
                return None;

            var resolvedCount = count == null || count.Value < 1 ? 1 : Math.Min(count.Value, MaxCount);

            var resolvedRadius = DefaultRadius;

            if (radius != null)
            {
                var r = radius.Value;

                if (!double.IsNaN(r) && !double.IsInfinity(r) && r > 0.0)
                    resolvedRadius = (float)Math.Min(r, MaxRadius);
            }

            return new DeathSpawnPlan(true, (uint)wcid.Value, resolvedCount, resolvedRadius);
        }

        public override string ToString()
        {
            return ShouldSpawn ? $"{Count}x wcid {Wcid} within {Radius:F1}m" : "none";
        }
    }
}
