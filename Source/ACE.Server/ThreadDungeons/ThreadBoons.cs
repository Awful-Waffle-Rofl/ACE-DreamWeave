using System;
using System.Globalization;
using System.Threading;

using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>The six blessings a Wayfarer's Shrine can grant (Docs/Threads/ENCOUNTERS.md "Shrine"). Never persisted.</summary>
    public enum ThreadBoonKind
    {
        Damage = 0,
        Mitigation = 1,
        AttackSpeed = 2,
        Xp = 3,
        Luminance = 4,
        Loot = 5,
    }

    /// <summary>
    /// The one read side of the Thread Shrine blessings. A blessing lives only in its run's in-memory ledger
    /// (ThreadDungeonRun_Boons.cs) and is read here, at the moment it applies, as a magnitude: 1.0 means no effect.
    /// It applies only while its holder is a roster member standing inside that run's copy
    /// (<see cref="ThreadRunPresence.IsMemberInside"/>), and never once the run has ended. Leaving, logging out,
    /// a puzzle removal and the end of the run therefore all remove it with no cleanup: there is no enchantment and
    /// no persisted property to take away.
    ///
    /// Hot path. Every hook (damage rating, damage resist rating, attack speed, kill XP, kill luminance, kill loot)
    /// first reads one process-wide counter of runs that hold any blessing, so a server where no shrine has been
    /// used pays one volatile read per call and nothing else.
    /// </summary>
    public static class ThreadBoons
    {
        /// <summary>How many runs currently hold at least one granted blessing (incremented on a run's first grant, decremented when it releases).</summary>
        private static int liveBoonRuns;

        /// <summary>Test seam: the counter itself.</summary>
        internal static int LiveBoonRuns => Volatile.Read(ref liveBoonRuns);

        /// <summary>The fast gate every hook reads first.</summary>
        public static bool AnyLive => Volatile.Read(ref liveBoonRuns) > 0;

        internal static void OnRunGainedBoons() => Interlocked.Increment(ref liveBoonRuns);

        internal static void OnRunReleasedBoons()
        {
            if (Interlocked.Decrement(ref liveBoonRuns) < 0)
                Interlocked.Exchange(ref liveBoonRuns, 0);
        }

        /// <summary>Test seam: the run registered under an instance, in any state, lock-free. Production is ThreadDungeonManager.GetRegisteredRun.</summary>
        internal static Func<uint, ThreadDungeonRun> RunLookup = ThreadDungeonManager.GetRegisteredRun;

        /// <summary>
        /// The magnitude of <paramref name="kind"/> for <paramref name="player"/> right now: the blessing of the run whose
        /// copy they stand in, if they hold one and are inside it; 1.0 otherwise.
        /// </summary>
        public static double For(Player player, ThreadBoonKind kind)
        {
            if (!AnyLive || player == null)
                return 1.0;

            return For(player.Guid.Full, player.Location, kind);
        }

        /// <summary>The Player-free form of <see cref="For(Player, ThreadBoonKind)"/>: the run is the one registered for the position's instance.</summary>
        internal static double For(uint guid, ACE.Entity.Position location, ThreadBoonKind kind)
        {
            if (!AnyLive || location == null || location.Instance == 0)
                return 1.0;

            return ForRun(RunLookup(location.Instance), guid, location, kind);
        }

        /// <summary>As <see cref="For(Player, ThreadBoonKind)"/>, but for a known run (the loot hooks know the dying creature's run).</summary>
        public static double ForRun(ThreadDungeonRun run, Player player, ThreadBoonKind kind)
        {
            if (!AnyLive || run == null || player == null || !run.HoldsAnyBoon)
                return 1.0;

            return ForRun(run, player.Guid.Full, player.Location, kind);
        }

        /// <summary>The Player-free core: inside is exactly ThreadRunPresence.IsMemberInside (roster, not puzzle-removed, the copy's instance and a run landblock).</summary>
        internal static double ForRun(ThreadDungeonRun run, uint guid, ACE.Entity.Position location, ThreadBoonKind kind)
        {
            if (!AnyLive || run == null || !run.HoldsAnyBoon)
                return 1.0;

            return Resolve(run, guid, ThreadRunPresence.IsMemberInside(run, guid, location), kind);
        }

        /// <summary>The Player-free core: neutral unless <paramref name="inside"/>; otherwise the run ledger's magnitude (neutral after the run ended). Pure over the run's locked read.</summary>
        internal static double Resolve(ThreadDungeonRun run, uint guid, bool inside, ThreadBoonKind kind)
            => run == null || !inside ? 1.0 : run.BoonMagnitude(guid, kind);

        // ---- the hook arithmetic (pure) ------------------------------------------------------------------------

        /// <summary>A rating-pool blessing as rating points: 1.25 -> 25. Neutral or below -> 0.</summary>
        public static int RatingBonus(double magnitude)
            => double.IsNaN(magnitude) || magnitude <= 1.0 ? 0 : (int)Math.Round((Math.Min(magnitude, 100.0) - 1.0) * 100.0, MidpointRounding.AwayFromZero);

        /// <summary>Damage rating points <paramref name="player"/> gains from a damage blessing right now.</summary>
        public static int DamageRatingFor(Player player) => RatingBonus(For(player, ThreadBoonKind.Damage));

        /// <summary>Damage resist rating points <paramref name="player"/> gains from a mitigation blessing right now.</summary>
        public static int DamageResistRatingFor(Player player) => RatingBonus(For(player, ThreadBoonKind.Mitigation));

        /// <summary>
        /// The attack-speed factor for the class-ability product (Player.ApplyClassAbilityAttackSpeed, combat call only).
        /// Still inside class_ability_attack_speed_ceiling: the product is clamped after this factor joins it.
        /// </summary>
        public static float AttackSpeedFactorFor(Player player)
        {
            var magnitude = For(player, ThreadBoonKind.AttackSpeed);
            return magnitude > 1.0 ? (float)magnitude : 1.0f;
        }

        /// <summary>
        /// A kill XP or luminance amount scaled by a blessing: <paramref name="amount"/> unchanged when the magnitude is
        /// neutral, the amount is not positive, or the product would not fit (the EarnXP overflow guard's rule).
        /// </summary>
        public static long ScaleAmount(long amount, double magnitude)
        {
            if (amount <= 0 || double.IsNaN(magnitude) || magnitude <= 1.0)
                return amount;

            var product = amount * magnitude;

            if (!double.IsFinite(product) || product >= long.MaxValue)
                return amount;

            return (long)Math.Round(product);
        }

        /// <summary>The percentage a blessing shows the player. Mitigation is the real damage reduction its rating gives (25 rating -> 20).</summary>
        public static int PercentFor(ThreadBoonKind kind, double magnitude)
        {
            if (kind == ThreadBoonKind.Mitigation)
            {
                var rating = RatingBonus(magnitude);
                return (int)Math.Round(100.0 - 10000.0 / (100.0 + rating), MidpointRounding.AwayFromZero);
            }

            if (kind == ThreadBoonKind.Damage)
                return RatingBonus(magnitude);

            return double.IsNaN(magnitude) || magnitude <= 1.0 ? 0 : (int)Math.Round((magnitude - 1.0) * 100.0, MidpointRounding.AwayFromZero);
        }

        // ---- player-facing text (orchestrator-owned strings, verbatim) -----------------------------------------

        public const string GuardiansLine = "Guardians stir around the shrine! Their kills do not count toward clearing the thread.";
        public const string SilentLine = "The shrine is silent. Your thread already holds every blessing it can give.";
        public const string SpentLine = "The shrine is spent.";
        public const string RefusedLine = "Only members of this thread can use the shrine.";

        /// <summary>The one-line blessing message a recipient is sent.</summary>
        public static string GrantLine(ThreadBoonKind kind, double magnitude)
        {
            var pct = PercentFor(kind, magnitude).ToString(CultureInfo.InvariantCulture);

            switch (kind)
            {
                case ThreadBoonKind.Damage: return string.Format(CultureInfo.InvariantCulture, "The shrine blesses you: +{0}% damage dealt for the rest of this thread.", pct);
                case ThreadBoonKind.Mitigation: return string.Format(CultureInfo.InvariantCulture, "The shrine blesses you: {0}% less damage taken for the rest of this thread.", pct);
                case ThreadBoonKind.AttackSpeed: return string.Format(CultureInfo.InvariantCulture, "The shrine blesses you: +{0}% attack speed for the rest of this thread.", pct);
                case ThreadBoonKind.Xp: return string.Format(CultureInfo.InvariantCulture, "The shrine blesses you: +{0}% experience for the rest of this thread.", pct);
                case ThreadBoonKind.Luminance: return string.Format(CultureInfo.InvariantCulture, "The shrine blesses you: +{0}% luminance for the rest of this thread.", pct);
                case ThreadBoonKind.Loot: return string.Format(CultureInfo.InvariantCulture, "The shrine blesses you: +{0}% more items from kills for the rest of this thread.", pct);
                default: return null;
            }
        }

        /// <summary>The /dd status line for a held blessing, e.g. "Blessing: +25% damage dealt".</summary>
        public static string StatusLine(ThreadBoonKind kind, double magnitude)
        {
            var pct = PercentFor(kind, magnitude).ToString(CultureInfo.InvariantCulture);

            switch (kind)
            {
                case ThreadBoonKind.Damage: return $"Blessing: +{pct}% damage dealt";
                case ThreadBoonKind.Mitigation: return $"Blessing: {pct}% less damage taken";
                case ThreadBoonKind.AttackSpeed: return $"Blessing: +{pct}% attack speed";
                case ThreadBoonKind.Xp: return $"Blessing: +{pct}% experience";
                case ThreadBoonKind.Luminance: return $"Blessing: +{pct}% luminance";
                case ThreadBoonKind.Loot: return $"Blessing: +{pct}% more items from kills";
                default: return null;
            }
        }

        /// <summary>encounters.json boon kind names -> kind. Case-insensitive. False for an unknown name.</summary>
        public static bool TryParseKind(string name, out ThreadBoonKind kind)
        {
            kind = default;

            switch (name?.Trim().ToLowerInvariant())
            {
                case "damage": kind = ThreadBoonKind.Damage; return true;
                case "mitigation": kind = ThreadBoonKind.Mitigation; return true;
                case "attackspeed":
                case "attack_speed": kind = ThreadBoonKind.AttackSpeed; return true;
                case "xp": kind = ThreadBoonKind.Xp; return true;
                case "luminance": kind = ThreadBoonKind.Luminance; return true;
                case "loot": kind = ThreadBoonKind.Loot; return true;
                default: return false;
            }
        }
    }
}
