using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// Every decision the digsite encounter system takes, as pure functions over already-read values.
    ///
    /// NOTHING here reads PropertyManager, a Player, a landblock or the clock: every tunable arrives as a
    /// parameter and every time span arrives measured. That is what lets the unit tests pin the shipped
    /// arithmetic without a world (PropertyManager reads throw under the test harness), and it is why this
    /// is the code the runtime actually calls rather than a parallel copy of the rules that tests could pin
    /// while production drifted.
    ///
    /// The impure halves live in MlDigsiteManager (registry and tick), MlDigsiteSpawner (placement) and
    /// MlDigsiteRewards (payout).
    /// </summary>
    public static class MlDigsiteRules
    {
        /// <summary>
        /// Reason strings for why an encounter ended. Constants rather than literals so the log line and any
        /// later telemetry mapping cannot drift from the predicate that produced them.
        /// </summary>
        public static class EndReasons
        {
            public const string ObjectiveKilled = "objective-killed";
            public const string MeterFilled = "meter-filled";

            /// <summary>Round 16: wave 8 was cleared. A Waves encounter's WIN condition.</summary>
            public const string WavesWon = "waves-won";

            /// <summary>Round 16: the encounter's ml_digsite_corruption_kills_required'th Corrupted mob died.</summary>
            public const string CorruptionWon = "corruption-won";

            public const string Expired = "expired";
            public const string LandblockUnloaded = "landblock-unloaded";
            public const string Abandoned = "abandoned";
            public const string Shutdown = "shutdown";

            /// <summary>The owner ended the encounter with /digsite bail. Pays whatever it had reached.</summary>
            public const string Bailed = "bailed";

            /// <summary>Nobody alive inside the audience radius for ml_digsite_wipe_grace_seconds.</summary>
            public const string Wiped = "wiped";

            /// <summary>A Waves encounter went ml_digsite_stall_timeout_seconds with no encounter kill.</summary>
            public const string Stalled = "stalled";

            /// <summary>A Waves encounter's live wave outran ml_digsite_wave_time_limit_seconds.</summary>
            public const string WaveTimeLimit = "wave-time-limit";
        }

        // ---- admission ---------------------------------------------------------------------------------

        /// <summary>
        /// Whether a completed dig may open an encounter. Every refusal is silent as far as the player's
        /// property is concerned: the caller runs this AFTER the doubloon payout and AFTER the map has been
        /// consumed, so a refusal costs neither.
        ///
        /// The three refusals are the ones the design names. A digger who already has one running cannot
        /// stack a second; an anchor inside <paramref name="tooCloseToLiveEncounter"/> of a live encounter is
        /// refused so two fights do not merge into one unreadable pile; and the server-wide cap bounds how
        /// much the island can be carrying at once.
        /// </summary>
        public static bool IsAdmitted(bool enabled, bool diggerAlreadyRunning, bool tooCloseToLiveEncounter,
            int liveCount, long maxConcurrent, out string reason)
        {
            reason = null;

            if (!enabled)
            {
                reason = "digsite encounters are disabled";
                return false;
            }

            if (diggerAlreadyRunning)
            {
                reason = "the digger already has a live encounter";
                return false;
            }

            if (tooCloseToLiveEncounter)
            {
                reason = "another encounter is already running too close to this site";
                return false;
            }

            // A cap of 0 or less means "no encounters", not "unlimited" - a misconfigured cap must fail
            // closed rather than removing the bound this check exists to impose.
            if (liveCount >= maxConcurrent)
            {
                reason = $"the server-wide concurrent encounter cap ({maxConcurrent}) is full";
                return false;
            }

            return true;
        }

        // ---- the weighted type roll -------------------------------------------------------------------

        /// <summary>
        /// Picks the encounter shape from three live weights, normalised rather than assumed to sum to
        /// anything. The shipped defaults are 65 / 27 / 7, which are the design's numbers AFTER the Relaria
        /// share has been taken out of the hundred - Relaria is not a branch here at all (see
        /// <see cref="MlDigsiteType"/>), so these three split the whole of what reaches this function.
        ///
        /// Normalising rather than hardcoding a denominator is what lets an operator retune one weight
        /// without having to rebalance the other two to keep a total.
        /// </summary>
        /// <param name="roll">a uniform draw in [0, 1)</param>
        public static MlDigsiteType PickType(long wavesWeight, long corruptionWeight, long bossRushWeight, double roll)
        {
            var waves = Math.Max(0L, wavesWeight);
            var corruption = Math.Max(0L, corruptionWeight);
            var bossRush = Math.Max(0L, bossRushWeight);

            var total = waves + corruption + bossRush;

            // Every weight zeroed (or negative) leaves nothing to choose between. Falling back to the
            // majority shape keeps a completed dig producing an encounter rather than silently producing
            // none, which is the failure an operator who zeroed one weight by accident would never notice.
            if (total <= 0)
                return MlDigsiteType.WavesAndMiniBoss;

            if (!double.IsFinite(roll) || roll < 0.0)
                roll = 0.0;
            else if (roll >= 1.0)
                roll = 0.99999999;

            var scaled = roll * total;

            if (scaled < waves)
                return MlDigsiteType.WavesAndMiniBoss;

            if (scaled < waves + corruption)
                return MlDigsiteType.CorruptionMeter;

            return MlDigsiteType.BossRush;
        }

        // ---- test-tooling forced type/set encoding ------------------------------------------------------

        /// <summary>
        /// The value PropertyInt.TreasureMapForcedDigsiteType stores for <paramref name="type"/>: (int)type
        /// + 1, so 0/absent reads back as "no forced type" (<see cref="DecodeForcedType"/>) rather than
        /// colliding with MlDigsiteType.WavesAndMiniBoss, whose own numeric value is 0.
        /// </summary>
        public static int EncodeForcedType(MlDigsiteType? type) => type.HasValue ? (int)type.Value + 1 : 0;

        /// <summary>
        /// The inverse of <see cref="EncodeForcedType"/>: null for 0 or an out-of-range stored value (a
        /// corrupt or hand-edited property must fall back to rolling, never crash or pick an arbitrary
        /// type), otherwise the MlDigsiteType it names.
        /// </summary>
        public static MlDigsiteType? DecodeForcedType(int stored)
        {
            var raw = stored - 1;

            if (raw < (int)MlDigsiteType.WavesAndMiniBoss || raw > (int)MlDigsiteType.BossRush)
                return null;

            return (MlDigsiteType)raw;
        }

        // ---- the corruption meter ----------------------------------------------------------------------

        /// <summary>
        /// The meter after one tick, clamped into [0, max]. A non-positive step is treated as zero rather
        /// than allowed to run the meter backwards: the meter is defined as climbing while the priority mob
        /// lives, and a negative tunable would otherwise make the encounter unlosable.
        /// </summary>
        public static int NextMeter(int meter, int perTick, int max)
        {
            if (max <= 0)
                return 0;

            var step = Math.Max(0, perTick);
            var next = (long)meter + step;

            return (int)Math.Clamp(next, 0, max);
        }

        /// <summary>True once the meter has reached its maximum, which loses the encounter.</summary>
        public static bool MeterFailed(int meter, int max) => max > 0 && meter >= max;

        /// <summary>
        /// The warning bands - 25%, 50% and 75% of the way to failure - newly crossed by a single tick,
        /// ascending. A tick that crosses none returns empty.
        ///
        /// Returns a LIST rather than the highest band because a large ml_digsite_meter_per_tick can jump
        /// two bands at once, and the caller latches each band separately; collapsing to one would silently
        /// swallow the band that was skipped over. At the shipped 2-per-tick on a max of 100 only one band
        /// can ever be crossed at a time, so this is for the retuned case, not today's.
        /// </summary>
        public static IReadOnlyList<int> BandsCrossed(int before, int after, int max)
        {
            var crossed = new List<int>();

            if (max <= 0 || after <= before)
                return crossed;

            foreach (var band in WarningBands)
            {
                // Integer arithmetic deliberately: the threshold has to be the same value every tick, and a
                // double threshold recomputed per call could land either side of an exact meter value.
                var threshold = max * band / 100;

                if (before < threshold && after >= threshold)
                    crossed.Add(band);
            }

            return crossed;
        }

        /// <summary>The percentages of the way to failure that are announced, ascending.</summary>
        public static readonly IReadOnlyList<int> WarningBands = new[] { 25, 50, 75 };

        /// <summary>
        /// The DamageRating an encounter creature should be carrying after <paramref name="ticks"/> power
        /// gains. DamageRating is an additive percentage rating, so "each tick adds 3% damage to the rest of
        /// the field" is exactly "add 3 to the rating per tick".
        ///
        /// Written for the pre-round-16 corruption meter and REWIRED in round 17 to the Corrupted mob's power
        /// gain (MlDigsiteManager.DriveCorruptionPower): one tick per ml_digsite_corruption_power_tick_seconds
        /// while a Corrupted mob lives, reset to zero when it dies.
        ///
        /// Capped at <see cref="MaxDamageRatingGrowth"/> above the creature's base rating. A Corrupted mob can
        /// in principle live for the whole 10-minute TTL, so the cap is what stops a long-lived one - or a
        /// retuned step - from producing a one-shot field.
        /// </summary>
        public static int DamageRatingFor(int authoredRating, int ticks, double perTickFraction)
        {
            if (ticks <= 0 || !double.IsFinite(perTickFraction) || perTickFraction <= 0.0)
                return authoredRating;

            var growth = (long)Math.Round(perTickFraction * 100.0 * ticks);

            growth = Math.Clamp(growth, 0, MaxDamageRatingGrowth);

            return (int)Math.Clamp(authoredRating + growth, int.MinValue, int.MaxValue);
        }

        /// <summary>
        /// The most DamageRating the corruption power gain may add to one creature, over and above its base.
        /// 300 is +300% damage: at the shipped +3% every 5 s it binds after 100 gains (500 s) under ONE
        /// Corrupted mob, which a 600 s encounter can only reach if that mob is left alive almost the whole run.
        /// </summary>
        public const long MaxDamageRatingGrowth = 300;

        /// <summary>
        /// The whole-percent damage bonus <paramref name="stacks"/> power gains amount to - the same capped
        /// number <see cref="DamageRatingFor"/> adds, read from a zero base. This is the {N} in the
        /// announcement, so the line and the applied rating can never disagree.
        /// </summary>
        public static int CorruptionPowerPercent(int stacks, double perTickFraction)
            => DamageRatingFor(0, stacks, perTickFraction);

        /// <summary>
        /// Whether a power gain is worth a line: only when the bonus actually grew. Once the growth cap is
        /// reached the stack count still climbs, but the creatures hit no harder, and "now strike 300% harder"
        /// repeated every 5 s would be noise announcing nothing.
        /// </summary>
        public static bool ShouldAnnounceCorruptionGain(int previousPercent, int newPercent)
            => newPercent > previousPercent;

        /// <summary>The round 17 power-gain announcement (owner-specified text).</summary>
        public static string CorruptionPowerLine(int percent)
            => $"The corruption swells - the dig's creatures now strike {percent}% harder.";

        // ---- wave pacing -------------------------------------------------------------------------------
        //
        // The two wave clocks live in WaveClockExpired (the endless-waves section below): either one running
        // out ENDS an endless Waves encounter at the tier reached.

        /// <summary>
        /// How many creatures a spawn of this kind should place for <paramref name="participants"/> players
        /// present, on the shipped player-count curve
        /// (<see cref="ScaledCount.Resolve(int, double, int, int)"/>: max(base, min(cap, base + ceil(per * n)))).
        ///
        /// Wrapped rather than called directly at the spawn sites so the curve this system uses is pinned in
        /// one place by one test, and so a later change of curve is one edit rather than a search.
        /// </summary>
        public static int ScaledSpawnCount(int baseCount, double perParticipant, int cap, int participants)
        {
            return ScaledCount.Resolve(baseCount, perParticipant, cap, Math.Max(0, participants));
        }

        // ---- ending ------------------------------------------------------------------------------------

        /// <summary>
        /// The reap decision: every end that no death hook can report (unload, TTL, wipe).
        ///
        /// This has to be a poll rather than a hook because of how an encounter actually dies in the common
        /// failure case: when its landblock unloads, every creature the encounter placed is DESTROYED
        /// without Die() ever running, so no death hook fires, no objective is ever reported dead, and an
        /// encounter driven only by hooks would sit in the registry forever holding a slot against the
        /// concurrent cap. The same is true of a server restart, except that the registry itself is gone.
        ///
        /// Order matters. The landblock test comes before the TTL test so an unloaded encounter is filed as
        /// unloaded rather than as expired, and the wipe test comes last because it is the only one that
        /// depends on a scan that can be wrong (see MlDigsiteAudience.AnyPresent, which holds the encounter
        /// open when its scan throws).
        ///
        /// THE WIPE TEST. <paramref name="participantPresent"/> is "anyone ALIVE inside the audience radius"
        /// (MlDigsiteAudience does not count a dead player), so a group that has all died, and a group that
        /// has all walked off, both end as Wiped once <paramref name="wipeGrace"/> has elapsed since anyone
        /// alive was last seen. Wiped pays the tier the encounter had reached, so the grace is short: it only
        /// has to cover a corpse run back to the site, not decide whether anyone earned anything.
        ///
        /// A caller that reaches true here only REQUESTS the end (MlDigsiteEncounter.TryRequestEnd); the
        /// world-thread tick is what finishes it.
        /// </summary>
        public static bool ShouldEnd(MlDigsiteState state, bool landblockLoaded, bool participantPresent,
            TimeSpan sinceStart, TimeSpan sinceLastPresence, TimeSpan ttl, TimeSpan wipeGrace, out string reason)
        {
            reason = null;

            if (state == MlDigsiteState.Ended)
                return false;

            if (!landblockLoaded)
            {
                reason = EndReasons.LandblockUnloaded;
                return true;
            }

            if (ttl > TimeSpan.Zero && sinceStart >= ttl)
            {
                reason = EndReasons.Expired;
                return true;
            }

            if (!participantPresent && wipeGrace > TimeSpan.Zero && sinceLastPresence >= wipeGrace)
            {
                reason = EndReasons.Wiped;
                return true;
            }

            return false;
        }

        // ---- payout ------------------------------------------------------------------------------------

        /// <summary>
        /// A BINARY share of the full reward: a full clear pays all of it, anything else pays
        /// <paramref name="partialFraction"/>, clamped into [0, 1] so a mis-set tunable can neither pay
        /// negative nor pay more than a win.
        ///
        /// DEAD as of round 16: EncounterPayoutFraction no longer routes any shape through this - Corruption
        /// stopped being binary and now pays by Corrupted-mob progress like the other two shapes
        /// (<see cref="CorruptionProgressFraction"/>). Left in place, and still directly unit-tested, as a
        /// general-purpose pure function; nothing in the shipped payout path calls it any more.
        /// </summary>
        public static double PayoutFraction(MlDigsiteResult result, double partialFraction)
        {
            if (result == MlDigsiteResult.FullClear)
                return 1.0;

            if (!double.IsFinite(partialFraction))
                return 0.0;

            return Math.Clamp(partialFraction, 0.0, 1.0);
        }

        /// <summary>
        /// A reward amount scaled by <see cref="PayoutFraction"/>, rounded, and floored at zero. Shared by
        /// the XP grant, the luminance grant, the loot-roll count and the two currency stacks, so every part
        /// of a reduced payout is reduced by the same rule.
        /// </summary>
        public static long ScaleReward(long baseAmount, double fraction)
        {
            if (baseAmount <= 0 || !double.IsFinite(fraction) || fraction <= 0.0)
                return 0;

            var scaled = Math.Round(baseAmount * Math.Clamp(fraction, 0.0, 1.0));

            if (!double.IsFinite(scaled) || scaled <= 0)
                return 0;

            return (long)Math.Min(scaled, long.MaxValue);
        }

        /// <summary>
        /// A chest CURRENCY stack (doubloons, trade notes): <see cref="ScaleReward"/>, but floored at 1 whenever
        /// anything is owed at all (a positive base and a positive fraction). Round 15: with the shipped base
        /// of 5, a 0.1 fraction rounded to 0 and a partial run's chest arrived with no currency in it. The loot
        /// count and XP/luminance deliberately keep ScaleReward's plain rounding.
        /// </summary>
        public static long ScaleCurrency(long baseAmount, double fraction)
        {
            var scaled = ScaleReward(baseAmount, fraction);

            if (scaled > 0)
                return scaled;

            if (baseAmount <= 0 || !double.IsFinite(fraction) || fraction <= 0.0)
                return 0;

            return 1;
        }

        /// <summary>
        /// The fraction XP and luminance are paid at. Round 15 owner ruling: the full amounts are paid FLAT per
        /// completion - a <see cref="MlDigsiteResult.FullClear"/> pays exactly ml_digsite_full_xp and
        /// ml_digsite_full_luminance whatever the encounter's payout fraction came to - and every other outcome
        /// keeps paying its payout fraction of them. Chests are NOT paid on this; they keep the plain fraction.
        /// </summary>
        public static double XpLuminanceFraction(MlDigsiteResult result, double payoutFraction)
        {
            if (result == MlDigsiteResult.FullClear)
                return 1.0;

            if (!double.IsFinite(payoutFraction))
                return 0.0;

            return Math.Clamp(payoutFraction, 0.0, 1.0);
        }

        /// <summary>
        /// Whether a built chest earns its "a cache settles into the loose earth" line: only when it actually
        /// holds something (round 15 - an empty chest announced as a cache is a promise the chest breaks).
        /// </summary>
        public static bool ChestHasContents(int doubloons, int notes, int loot, int keptSiraluun)
            => doubloons > 0 || notes > 0 || loot > 0 || keptSiraluun > 0;

        /// <summary>
        /// The idle-monster shape MlDigsiteSpawner warns about (round 15): a Held caster in hand and no spell
        /// to cast with it. Either half alone is fine - a caster with spells casts, and a spell-less creature
        /// with a melee weapon (or none) swings.
        /// </summary>
        public static bool HeldCasterWithoutSpells(bool holdsHeldCaster, bool hasSpells)
            => holdsHeldCaster && !hasSpells;

        // ---- the Kept Siraluun rare miniboss -------------------------------------------------------------

        /// <summary>
        /// Whether this ordinary mini-boss/boss spawn should be replaced by a rare Kept Siraluun. A
        /// non-positive or non-finite chance never substitutes; a chance at or above 1.0 always does, because
        /// <paramref name="roll"/> is a uniform draw in [0, 1) (the same convention <see cref="PickType"/>
        /// uses) and so is always strictly less than a clamped chance of 1.0.
        /// </summary>
        public static bool RollKeptSiraluun(double chance, double roll)
        {
            if (!double.IsFinite(chance) || chance <= 0.0)
                return false;

            if (!double.IsFinite(roll))
                return false;

            return roll < Math.Clamp(chance, 0.0, 1.0);
        }

        /// <summary>
        /// Which Kept Siraluun to spawn in place of the ordinary pick it is replacing. Prefers a uniform draw
        /// among whichever entries in <paramref name="pool"/> share <paramref name="ordinaryLevel"/> - the
        /// authored Level (PropertyInt 25) of the roster entry the substitution is replacing - and falls back
        /// to a uniform draw across the whole pool when none does, which is the common case: today's
        /// MiniBoss/Boss roster levels rarely coincide with one of the eight Kept Siraluun's own authored
        /// levels.
        ///
        /// <paramref name="pool"/> is taken as a parameter rather than read from MlDigsiteRoster directly, so
        /// this stays a pure function over already-read values like the rest of this class.
        /// </summary>
        public static MlDigsiteRosterEntry PickKeptSiraluun(IReadOnlyList<MlDigsiteRosterEntry> pool, int ordinaryLevel, Random rng)
        {
            if (pool == null || pool.Count == 0)
                throw new ArgumentException("pool must be non-empty", nameof(pool));

            rng = rng ?? new Random();

            var banded = pool.Where(e => e.Level == ordinaryLevel).ToList();
            var candidates = banded.Count > 0 ? banded : pool;

            return candidates[rng.Next(candidates.Count)];
        }

        // ---- tracker: status lines and boss HP bands (round 13 feedback item E, part 3) ------------------

        /// <summary>
        /// The player-facing name of an encounter shape, for the opening announcement (round 14 feedback:
        /// "make the starting global include ... which type of event they are getting"). The enum token is
        /// never shown - WavesAndMiniBoss in particular is a stale name, since #1225 made that shape endless
        /// and its mini-boss a checkpoint rather than a finale.
        /// </summary>
        public static string TypeName(MlDigsiteType type)
        {
            switch (type)
            {
                case MlDigsiteType.CorruptionMeter:
                    // Round 16 owner ruling: exactly "Corruption" - the meter mechanic the old name referred
                    // to is retired (see the Corrupted-mob kill count redesign below).
                    return "Corruption";

                case MlDigsiteType.BossRush:
                    return "Boss Rush";

                default:
                    return "Waves";
            }
        }

        /// <summary>
        /// The number of waves a Waves encounter must clear to WIN outright (round 16 owner ruling: the
        /// shape is no longer endless). Clearing wave <see cref="TotalWaves"/> ends the run as a FullClear
        /// paying 100%, whatever the tier table would otherwise say.
        /// </summary>
        public const int TotalWaves = 8;

        /// <summary>Whether clearing wave <paramref name="waveNumber"/> wins a Waves encounter outright.</summary>
        public static bool WaveWon(int waveNumber, int totalWaves = TotalWaves)
            => totalWaves > 0 && waveNumber >= totalWaves;

        /// <summary>
        /// The compact line announced when a new wave begins (Waves shape only). Round 16: named against the
        /// fixed <see cref="TotalWaves"/> total now that the shape has a finish line.
        /// </summary>
        public static string WaveStartLine(int waveNumber) => $"Wave {waveNumber} of {TotalWaves} begins.";

        /// <summary>
        /// The line announced when a wave's field is emptied and the breather before the next one is armed
        /// (round 14 feedback: "nothing tells the player which wave they are on or when the next wave
        /// spawns"). It carries BOTH halves the tester asked for - the wave just finished and the one coming,
        /// plus how long the breather is.
        ///
        /// This is announced HERE rather than folded into <see cref="WaveStartLine"/> for a UX reason, not an
        /// arithmetic one. The breather itself is a fixed tunable (ml_digsite_inter_wave_seconds), so its
        /// LENGTH is perfectly well known at wave start - an earlier version of this comment claimed
        /// otherwise and was wrong. What makes a countdown at wave start the wrong line is that the clock it
        /// would name has not started: the breather is armed only when the field clears
        /// (MlDigsiteManager.OnEncounterCreatureDied), which is anything from seconds to the whole 300 s wave
        /// clock away. Told at wave start, the number is noise the player cannot act on and is stale if the
        /// tunable is retuned mid-run; told at the clear, it is a countdown that is already running.
        ///
        /// A non-positive delay reads as "right behind them" rather than "in 0 seconds", so an operator who
        /// sets ml_digsite_inter_wave_seconds to 0 still gets a sentence that means something.
        ///
        /// Round 15: <paramref name="wavesCleared"/> is the encounter's HighestWaveCleared - the same number
        /// the Waves payout tier is read from - so the running count the player sees is the one they are
        /// actually paid on. Sent as WorldBroadcast (green) by the caller.
        /// </summary>
        /// <summary>
        /// Round 16: the caller never invokes this for wave <see cref="TotalWaves"/> - clearing the last wave
        /// is a WIN, announced separately (see MlDigsiteManager's win branch), not a "next wave incoming"
        /// line. This still assumes a next wave exists, so <paramref name="wavesCleared"/> must be below
        /// <see cref="TotalWaves"/>.
        /// </summary>
        public static string WaveClearedLine(int wavesCleared, TimeSpan untilNextWave)
        {
            var seconds = (int)Math.Round(untilNextWave.TotalSeconds);

            var head = $"Wave {wavesCleared} of {TotalWaves} cleared - {wavesCleared} down so far.";

            if (seconds <= 0)
                return $"{head} Wave {wavesCleared + 1} is right behind them.";

            var plural = seconds == 1 ? "" : "s";

            return $"{head} Wave {wavesCleared + 1} rises in {seconds} second{plural}.";
        }

        /// <summary>
        /// The percent-of-max-health tiers a digsite boss/mini-boss announces once each, reusing
        /// <see cref="WorldEvent"/>'s own milestone arithmetic (75/50/25, "at or below") rather than a second
        /// copy of it - the digsite's own per-encounter latch set (<see cref="MlDigsiteEncounter"/>) is what
        /// keeps this scoped to one encounter rather than sharing WorldEvent's.
        /// </summary>
        public static readonly IReadOnlyList<int> BossHealthMilestonePercents = WorldEvent.BossHealthMilestonePercents;

        /// <summary>Whether a boss-HP milestone tier has been crossed. See <see cref="WorldEvent.BossHealthMilestoneCrossed"/>.</summary>
        public static bool BossHealthMilestoneCrossed(uint current, uint max, int pct)
            => WorldEvent.BossHealthMilestoneCrossed(current, max, pct);

        /// <summary>
        /// Which HP milestone tiers are newly due on this sample, mutating <paramref name="fired"/> so each
        /// tier is returned at most once. See <see cref="WorldEvent.DueBossHealthMilestones"/>.
        /// </summary>
        public static List<int> DueBossHealthMilestones(uint current, uint max, HashSet<int> fired)
            => WorldEvent.DueBossHealthMilestones(current, max, fired);

        /// <summary>
        /// The one progress clause a status line carries, chosen by the encounter's type: "Wave n of 8, payout
        /// tier p%." for Waves (the tier is what a bail right now would pay the owner), "Boss at n% health."
        /// for Boss Rush, and "Corrupted slain: n of m." for Corruption.
        ///
        /// Round 16: <paramref name="corruptedKills"/> and <paramref name="corruptedKillsRequired"/> replace
        /// the retired meter percent for the Corruption shape - "Corrupted slain: N of M" rather than a
        /// percent, per the round 16 redesign (a priority mob no longer fills a meter; the player kills a
        /// fixed number of Corrupted mobs one at a time instead).
        ///
        /// Round 17: <paramref name="waveLeft"/> is null for every caller except a live Waves wave (no
        /// breather scheduled) - null reproduces exactly today's clause, unchanged, which is what keeps every
        /// existing pinned expectation passing. Non-null appends how many of the live wave's creatures still
        /// stand (round 17 tester feedback: "killed everything on radar" but the wave never cleared, because
        /// digsite creatures share wcids and names with the island's own wildlife and nothing told the player
        /// a count).
        /// </summary>
        public static string ProgressClause(MlDigsiteType type, int wavesSpawned, int tierPct, int corruptedKills,
            int bossHpPct, int corruptedKillsRequired = DefaultCorruptionKillsRequired, int? waveLeft = null)
        {
            switch (type)
            {
                case MlDigsiteType.CorruptionMeter:
                    return $"Corrupted slain: {corruptedKills} of {Math.Max(1, corruptedKillsRequired)}.";

                case MlDigsiteType.WavesAndMiniBoss:
                    return waveLeft == null
                        ? $"Wave {wavesSpawned} of {TotalWaves}, payout tier {tierPct}%."
                        : $"Wave {wavesSpawned} of {TotalWaves}, payout tier {tierPct}%, {waveLeft} left.";

                default:
                    return $"Boss at {bossHpPct}% health.";
            }
        }

        /// <summary>The shipped ml_digsite_corruption_kills_required - see MlDigsiteTunables.CorruptionKillsRequired.</summary>
        public const int DefaultCorruptionKillsRequired = 3;

        /// <summary>
        /// The full periodic/[/digsite] status line: <see cref="ProgressClause"/> plus a minutes:seconds time
        /// remaining clause. <paramref name="timeLeft"/> is floored at zero rather than allowed to go
        /// negative - a stale sample from just past the deadline should read "0m00s remain", not a negative
        /// duration. <paramref name="waveLeft"/> passes straight through to <see cref="ProgressClause"/>; see
        /// its doc comment.
        /// </summary>
        public static string StatusLine(MlDigsiteType type, int wavesSpawned, int tierPct, int corruptedKills, int bossHpPct,
            TimeSpan timeLeft, string extra = null, int corruptedKillsRequired = DefaultCorruptionKillsRequired, int? waveLeft = null)
        {
            if (timeLeft < TimeSpan.Zero)
                timeLeft = TimeSpan.Zero;

            var progress = ProgressClause(type, wavesSpawned, tierPct, corruptedKills, bossHpPct, corruptedKillsRequired, waveLeft);
            var minutes = (int)timeLeft.TotalMinutes;
            var seconds = timeLeft.Seconds;

            var line = $"[Digsite] {progress} {minutes}m{seconds:D2}s remain.";

            // OPTIONAL and appended, never interleaved: a caller with nothing extra to say gets exactly the
            // line this method has always produced, which is what keeps every existing reader (and every
            // pinned expectation) unchanged. Today the only user is the Boss Rush mechanic set clause.
            return string.IsNullOrWhiteSpace(extra) ? line : $"{line} {extra}";
        }

        /// <summary>
        /// A Boss Rush mechanic's player-facing name, for the status line. Deliberately not the authored
        /// token ("immunephases") and not the enum name: a player reading /digsite should see plain words.
        /// </summary>
        public static string MechanicName(MlDigsiteMechanic mechanic)
        {
            switch (mechanic)
            {
                case MlDigsiteMechanic.Volatile: return "volatile adds";
                case MlDigsiteMechanic.Drums: return "drum cadence";
                case MlDigsiteMechanic.ImmunePhases: return "immune phases";
                case MlDigsiteMechanic.Immune50: return "immune phase at half health";
                case MlDigsiteMechanic.Interrupt: return "interrupt object";
                case MlDigsiteMechanic.SafeZones: return "shifting safe zones";
                default: return "none";
            }
        }

        // ---- crowd scaling --------------------------------------------------------------------------------

        /// <summary>
        /// The health multiplier for <paramref name="participants"/> digsite participants, on the same
        /// min(cap, 1 + perParticipant * count) curve the Bluespire ladder's D6 crowd scaling uses
        /// (<see cref="CrowdHealthDef.Resolve(int, double, int, double)"/>), with no start-at floor - the
        /// digsite tunables name none, so scaling begins from the first participant.
        /// </summary>
        public static double CrowdHealthMultiplier(int participants, double perParticipant, double cap)
            => CrowdHealthDef.Resolve(0, perParticipant, Math.Max(0, participants), cap);

        /// <summary>
        /// The DamageRating addend for <paramref name="participants"/> digsite participants, on the same
        /// formula <see cref="BluespireCrowdScaling.DamageRatingAddend"/> uses for D6, with no start-at floor
        /// for the same reason as <see cref="CrowdHealthMultiplier"/>.
        /// </summary>
        public static int CrowdDamageRatingAddend(int participants, long perParticipant, long cap)
            => BluespireCrowdScaling.DamageRatingAddend(Math.Max(0, participants), 0, perParticipant, cap);

        // ---- the Kept Siraluun's feather, delivered through the chest ----------------------------------
        //
        // A Kept Siraluun leaves no corpse (IsDigsiteCorpselessDeath, Creature_LootRolls.cs:113-114), so
        // CreateCorpse -> GenerateTreasure - the ONLY path that ever reads a creature's create_list - never
        // runs for it, and its feather (a create_list row) could never drop. This resolves the SAME create
        // list rows the corpse path would have, at death, so the encounter can hand the result to its chest
        // instead of to a corpse that will never exist.

        /// <summary>
        /// Resolves which create_list rows a Kept Siraluun's death actually drops, replicating
        /// Creature_Equipment.CreateListSelect's own algorithm (the no drop-rate-modifier branch,
        /// Creature_Equipment.cs:679-722): rows are walked in order, a TREASURE row with a non-zero Shade
        /// consumes a slice of the current [0, 1) chunk - a new chunk, and a fresh roll, starts whenever the
        /// running total reaches or passes 1.0 - and the row whose slice the roll lands in wins; every other
        /// row (Shade 0, or not a Treasure row) is kept unconditionally. This is why a 1.0-shade row is
        /// guaranteed and a 0.05-shade row stays a 5% chance: it is the SAME arithmetic the ordinary corpse
        /// path would have run, just resolved with no corpse to run it on.
        ///
        /// A selected row whose WeenieClassId is 0 - the engine's own "no drop" placeholder, e.g. the
        /// shade-0.95 half of a Kept Siraluun's disjoint feather pair - is dropped from the result: nothing
        /// is ever created from it downstream (WorldObjectFactory.CreateNewWorldObject(0) resolves no
        /// weenie), so returning it would just make every caller re-derive the same filter.
        ///
        /// <paramref name="rolls"/> supplies one uniform [0, 1) draw per chunk boundary, called lazily - the
        /// same single ThreadSafeRandom.Next(0.0f, 1.0f) draw per chunk the real (corpse-path) call site
        /// makes - so a test can hand in a fixed sequence instead of a live RNG.
        /// </summary>
        public static IReadOnlyList<PropertiesCreateList> ResolveCreateListDrops(IReadOnlyList<PropertiesCreateList> rows, Func<double> rolls)
        {
            var results = new List<PropertiesCreateList>();

            if (rows == null || rows.Count == 0)
                return results;

            rolls = rolls ?? (() => 0.0);

            var roll = rolls();
            var totalProbability = 0.0;
            var rngSelected = false;

            foreach (var row in rows)
            {
                if (row == null)
                    continue;

                var useRng = (row.DestinationType & DestinationType.Treasure) != 0 && row.Shade != 0f;

                if (useRng)
                {
                    if (totalProbability >= 1.0)
                    {
                        totalProbability = 0.0;
                        roll = rolls();
                        rngSelected = false;
                    }

                    totalProbability += row.Shade;

                    if (rngSelected || roll >= totalProbability)
                        continue;

                    rngSelected = true;
                }

                if (row.WeenieClassId != 0)
                    results.Add(row);
            }

            return results;
        }

        // ---- endless waves: the clocks ----------------------------------------------------------------------

        /// <summary>
        /// Which of the two wave clocks has run out, if either: the WAVE TIME LIMIT (a wave that is being
        /// fought but cannot be finished) is checked first, then the STALL timer (a group that stopped
        /// fighting). A zero or negative value disables that clock, matching the shipped Proving Grounds wave
        /// portal's own semantics (Content/sql/weenies/1001550, PropertyFloat 9005/9006).
        ///
        /// Either one ENDS an endless Waves encounter (it pays the tier reached); there is no longer a forced
        /// mini-boss to bring forward. The two are reported separately only so the log and the end line can
        /// say which one it was.
        /// </summary>
        public static bool WaveClockExpired(TimeSpan sinceWaveStarted, TimeSpan sinceLastKill,
            TimeSpan waveTimeLimit, TimeSpan stallTimeout, out string reason)
        {
            reason = null;

            if (waveTimeLimit > TimeSpan.Zero && sinceWaveStarted >= waveTimeLimit)
            {
                reason = EndReasons.WaveTimeLimit;
                return true;
            }

            if (stallTimeout > TimeSpan.Zero && sinceLastKill >= stallTimeout)
            {
                reason = EndReasons.Stalled;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Which of the SAME two wave clocks <see cref="WaveClockExpired"/> reads is nearer to expiry, and how
        /// much time is left on it - the status line's answer to "how long do I actually have", since the
        /// overall TTL alone (round 17 tester feedback) never told a player their wave was about to be torn
        /// down by a clock they could not see.
        ///
        /// MIRRORS WaveClockExpired's OWN semantics exactly, and must keep doing so: a clock whose tunable is
        /// zero or negative is disabled and excluded here too, and a tie between the two enabled clocks
        /// resolves to <see cref="MlDigsiteWaveClockKind.WaveTimeLimit"/> - the same clock WaveClockExpired
        /// checks first - so <see cref="MlDigsiteWaveClockKind.Stall"/> is only ever returned when the stall
        /// clock is STRICTLY the nearer of the two. This is what keeps the two methods from ever disagreeing
        /// about whether a run is about to end: <paramref name="remaining"/> is at or below zero if and only if
        /// WaveClockExpired(same inputs) would return true.
        /// </summary>
        public static MlDigsiteWaveClockKind NearestWaveClock(TimeSpan sinceWaveStarted, TimeSpan sinceLastKill,
            TimeSpan waveTimeLimit, TimeSpan stallTimeout, out TimeSpan remaining)
        {
            TimeSpan? waveRemaining = waveTimeLimit > TimeSpan.Zero ? waveTimeLimit - sinceWaveStarted : (TimeSpan?)null;
            TimeSpan? stallRemaining = stallTimeout > TimeSpan.Zero ? stallTimeout - sinceLastKill : (TimeSpan?)null;

            if (waveRemaining == null && stallRemaining == null)
            {
                remaining = TimeSpan.Zero;
                return MlDigsiteWaveClockKind.None;
            }

            if (waveRemaining != null && (stallRemaining == null || waveRemaining.Value <= stallRemaining.Value))
            {
                remaining = waveRemaining.Value;
                return MlDigsiteWaveClockKind.WaveTimeLimit;
            }

            remaining = stallRemaining.Value;
            return MlDigsiteWaveClockKind.Stall;
        }

        /// <summary>
        /// Whether an endless Waves encounter has run long enough to be owed a FORCED checkpoint mini-boss
        /// (round 14 feedback: "spawn a mini boss after 3 minutes so the waves are not fully endless").
        ///
        /// This is a second, TIME-based trigger for the mechanism <see cref="IsCheckpointWave"/> already gates
        /// on wave COUNT, not a new kind of spawn: what it forces is the ordinary
        /// <see cref="MlDigsiteRole.Checkpoint"/> mini-boss, with the ordinary consequences - it never holds a
        /// wave open and its death is not a win, it adds one checkpoint kill to the tier. The waves still end
        /// only by bail, wipe, the TTL or a wave clock.
        ///
        /// <paramref name="after"/> at or below zero disables the forced spawn entirely, the same "0 turns
        /// this off" convention the two wave clocks use.
        /// </summary>
        public static bool ForcedCheckpointDue(TimeSpan sinceEncounterStarted, TimeSpan after)
            => after > TimeSpan.Zero && sinceEncounterStarted >= after;

        /// <summary>
        /// What one tick of an endless Waves encounter resolves to, decided in ONE place so that
        /// MlDigsiteManager.DriveWaves cannot act on a different answer from the one it gates the forced
        /// checkpoint mini-boss on (see <see cref="ShouldForceCheckpoint"/> and
        /// <see cref="MlDigsiteWavesTick"/>).
        ///
        /// The clocks are asked ONLY while a wave is live. A scheduled wave means the field is clear and the
        /// breather is the game's own pause, not a stall, which is why <paramref name="clockExpired"/> is
        /// ignored whenever <paramref name="waveScheduled"/> is true rather than trusted to have been
        /// computed correctly by the caller.
        /// </summary>
        public static MlDigsiteWavesTick WavesTick(bool waveScheduled, bool waveDue, bool clockExpired)
        {
            if (waveScheduled)
                return waveDue ? MlDigsiteWavesTick.SpawnWave : MlDigsiteWavesTick.Breather;

            return clockExpired ? MlDigsiteWavesTick.EndRun : MlDigsiteWavesTick.FightOn;
        }

        /// <summary>
        /// THE whole gate on placing the time-forced checkpoint mini-boss, so that every condition lives in
        /// one testable predicate and MlDigsiteManager.DriveForcedCheckpoint is left as a thin executor.
        ///
        /// THE ORDERING CLAUSE, and why it is here rather than in the manager. An
        /// <see cref="MlDigsiteWavesTick.EndRun"/> tick is the tick that tears the encounter down, and the
        /// forced spawn must not fire on it: it would burn the encounter's one-shot latch on a mini-boss
        /// nobody gets to fight, and it would emit "Something climbs out" immediately followed by the
        /// run-ending line. Note that MlDigsiteManager.Tick's own !EndRequested gate (MlDigsiteManager.cs:304)
        /// does NOT cover this - on an EndRun tick the end has not been requested yet when DriveWaves starts;
        /// the request is made a few lines later, inside the same call. Only the tick's own resolution can
        /// see it coming, which is exactly what this parameter carries.
        ///
        /// <paramref name="checkpointAlive"/> DEFERS rather than cancels: the caller must not claim its
        /// one-shot latch when this returns false for that reason, so the forced mini-boss still arrives once
        /// the standing one is dead. Stacking a second mini-boss on the first is the one outcome
        /// MlDigsiteManager.SpawnNextWave's own checkpoint path is explicitly written to avoid.
        /// </summary>
        public static bool ShouldForceCheckpoint(MlDigsiteWavesTick tick, TimeSpan sinceEncounterStarted,
            TimeSpan after, bool checkpointAlive)
        {
            if (tick == MlDigsiteWavesTick.EndRun)
                return false;

            if (checkpointAlive)
                return false;

            return ForcedCheckpointDue(sinceEncounterStarted, after);
        }

        // ---- endless waves: the tier table ------------------------------------------------------------------

        /// <summary>
        /// The shipped ml_digsite_wave_tiers value, and the table <see cref="WaveTierFraction"/> falls back to
        /// when handed an empty one.
        ///
        /// Round 16: retuned for the now-finite 8-wave run. CLEARING wave 1 pays 15%, each further wave
        /// cleared 12% more through wave 7 (87%); wave 8 is no longer a tier row at all - clearing it is an
        /// outright WIN (MlDigsiteRules.WaveWon), which pays 100% unconditionally rather than through this
        /// table (MlDigsiteManager routes a wave-8 clear to MlDigsiteResult.FullClear before this table is
        /// ever consulted). The table therefore only prices an encounter that did NOT reach wave 8: a bail,
        /// wipe or clock expiry at wave 1-7.
        /// </summary>
        public const string DefaultWaveTiers = "1:0.15,2:0.27,3:0.39,4:0.51,5:0.63,6:0.75,7:0.87";

        /// <summary>
        /// The smallest share an encounter that reached wave 1 is ever paid, whatever the table says. The
        /// parser already refuses a non-positive tier; this is the floor under a table built by hand.
        /// </summary>
        public const double MinimumReachedFraction = 0.01;

        /// <summary>
        /// Parses a tier table of the form <c>1:0.15,2:0.25,...</c> - comma-separated <c>wave:fraction</c>
        /// pairs, whitespace tolerated. Returned ascending by wave.
        ///
        /// NOTHING IS DROPPED SILENTLY: an entry with no colon, a wave below 1, a fraction that is not a
        /// positive finite number and a repeated wave each add a line to <paramref name="errors"/> (when one is
        /// supplied) and are skipped; a fraction above 1 is clamped to 1 and also reported. Invariant culture
        /// throughout, so "0.15" means the same thing on every host locale. The caller decides what an empty
        /// result means (MlDigsiteTunables falls back to <see cref="DefaultWaveTiers"/>).
        /// </summary>
        public static IReadOnlyList<MlDigsiteWaveTier> ParseTierTable(string raw, List<string> errors = null)
        {
            var byWave = new SortedDictionary<int, double>();

            if (string.IsNullOrWhiteSpace(raw))
                return new List<MlDigsiteWaveTier>();

            foreach (var rawPart in raw.Split(','))
            {
                var part = rawPart.Trim();

                if (part.Length == 0)
                    continue;

                var colon = part.IndexOf(':');

                if (colon <= 0 || colon == part.Length - 1)
                {
                    errors?.Add($"tier '{part}' is not wave:fraction");
                    continue;
                }

                if (!int.TryParse(part.Substring(0, colon).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wave) || wave < 1)
                {
                    errors?.Add($"tier '{part}' has a wave that is not a whole number of at least 1");
                    continue;
                }

                if (!double.TryParse(part.Substring(colon + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
                    || !double.IsFinite(fraction) || fraction <= 0.0)
                {
                    errors?.Add($"tier '{part}' has a fraction that is not a positive number");
                    continue;
                }

                if (fraction > 1.0)
                {
                    errors?.Add($"tier '{part}' pays more than the full reward; clamped to 1");
                    fraction = 1.0;
                }

                if (byWave.ContainsKey(wave))
                {
                    errors?.Add($"tier '{part}' repeats wave {wave}; the first one is kept");
                    continue;
                }

                byWave[wave] = fraction;
            }

            return byWave.Select(kvp => new MlDigsiteWaveTier(kvp.Key, kvp.Value)).ToList();
        }

        /// <summary>
        /// The tier-table lookup: the fraction of the highest tier at or below <paramref name="highestWave"/>,
        /// plus <paramref name="checkpointBonus"/> per checkpoint kill, clamped into [0, 1]. The encounter
        /// payout calls it through <see cref="WavesPayoutFraction"/>, which decides WHICH wave number to hand
        /// it (the highest one cleared) and when to pay nothing at all.
        ///
        /// A wave below the table's first row pays that first row, and the result for any highestWave of at
        /// least 1 is floored at <see cref="MinimumReachedFraction"/>. A highestWave below 1 pays nothing.
        ///
        /// An empty or null <paramref name="tiers"/> reads <see cref="DefaultWaveTiers"/> instead, so a broken
        /// table cannot silently zero the payout.
        /// </summary>
        public static double WaveTierFraction(int highestWave, int checkpointKills, IReadOnlyList<MlDigsiteWaveTier> tiers, double checkpointBonus)
        {
            if (highestWave < 1)
                return 0.0;

            if (tiers == null || tiers.Count == 0)
                tiers = ParseTierTable(DefaultWaveTiers);

            var ordered = tiers.OrderBy(t => t.Wave).ToList();

            var baseFraction = ordered[0].Fraction;

            foreach (var tier in ordered)
            {
                if (tier.Wave > highestWave)
                    break;

                baseFraction = tier.Fraction;
            }

            if (!double.IsFinite(baseFraction))
                baseFraction = 0.0;

            var bonus = double.IsFinite(checkpointBonus) && checkpointBonus > 0.0 ? checkpointBonus : 0.0;

            var total = baseFraction + Math.Max(0, checkpointKills) * bonus;

            return Math.Clamp(total, MinimumReachedFraction, 1.0);
        }

        /// <summary>
        /// What an endless Waves encounter pays. A wave counts as REACHED only once it is CLEARED (its field
        /// emptied) - wave 1 is placed the moment the dig opens, so paying on the wave placed would let a
        /// /digsite bail straight after the dig collect the wave-1 tier for no fighting at all.
        ///
        ///   * n >= 1 waves cleared, whatever ended it: tier(n) plus the checkpoint bonuses
        ///     (<see cref="WaveTierFraction"/>). Bail and wipe both pay the tier reached, as the RoZ design says.
        ///   * 0 cleared and BAILED: nothing - no chest, no XP (<see cref="EmptyBailMessage"/> tells the owner).
        ///   * 0 cleared and any OTHER end (wiped, stalled, the wave clock, the TTL, an unload): the wave-1
        ///     floor plus any checkpoint bonus. That group engaged and lost; the owner did not simply walk away.
        ///   * no wave ever placed: nothing, as before.
        ///
        /// Round 16: this is called only for a NON-WIN end (EncounterPayoutFraction returns 1.0 outright on
        /// MlDigsiteResult.FullClear before this is ever reached, and clearing wave 8 is always FullClear), so
        /// what this returns is a PROGRESS fraction, not the final payout share - the caller multiplies it by
        /// ml_digsite_fail_payout_multiplier.
        /// </summary>
        public static double WavesPayoutFraction(MlDigsitePayoutSnapshot snapshot, MlDigsitePayoutTunables tunables)
        {
            tunables = tunables ?? new MlDigsitePayoutTunables();

            if (snapshot.HighestWave < 1)
                return 0.0;

            if (snapshot.HighestWaveCleared >= 1)
                return WaveTierFraction(snapshot.HighestWaveCleared, snapshot.CheckpointKills, tunables.Tiers, tunables.CheckpointBonus);

            if (snapshot.Bailed)
                return 0.0;

            return WaveTierFraction(1, snapshot.CheckpointKills, tunables.Tiers, tunables.CheckpointBonus);
        }

        /// <summary>Told to the owner when a bail ends a Waves encounter before any wave was cleared and no feather was recorded.</summary>
        public const string EmptyBailMessage = "Nothing has been won here yet. The dig closes empty.";

        /// <summary>
        /// Told to the owner instead of <see cref="EmptyBailMessage"/> when that bail DID record a Kept Siraluun
        /// feather: the feathers-only chest follows, with its own normal cache line.
        /// </summary>
        public const string FeatherOnlyBailMessage = "Nothing else was won here, but the Siraluun's feather is yours.";

        /// <summary>The owner's line for a bail that pays nothing, chosen by whether any feather was recorded.</summary>
        public static string ZeroPayoutMessage(int featherCount)
            => featherCount > 0 ? FeatherOnlyBailMessage : EmptyBailMessage;

        /// <summary>
        /// Whether wave <paramref name="waveNumber"/> carries a checkpoint mini-boss: every
        /// <paramref name="every"/>'th wave (3, 6, ... at the shipped 3 - within the round-16 8-wave cap,
        /// waves 3 and 6). A non-positive <paramref name="every"/> turns checkpoints off.
        /// </summary>
        public static bool IsCheckpointWave(int waveNumber, long every)
            => every > 0 && waveNumber > 0 && waveNumber % every == 0;

        // ---- endless waves: per-wave scaling ----------------------------------------------------------------

        /// <summary>
        /// The most creatures a single wave may ever place, however far an endless run goes. A landblock
        /// safety ceiling, not a tuning dial: at the shipped +1 per wave it binds only past wave 25 or so.
        /// </summary>
        public const int MaxWaveSpawnCount = 40;

        /// <summary>
        /// How many creatures wave <paramref name="waveNumber"/> places: the crowd curve
        /// (<see cref="ScaledSpawnCount"/>, sized from the participants present) plus
        /// <paramref name="perWave"/> for every wave after the first, capped at <see cref="MaxWaveSpawnCount"/>.
        /// Wave 1 is exactly the crowd curve, so the opening of an endless run is sized as before.
        /// </summary>
        public static int WaveSpawnCount(int baseCount, long perWave, int waveNumber, int participants, double perParticipant, int cap)
        {
            var crowd = (long)ScaledSpawnCount(baseCount, perParticipant, cap, participants);

            // In double, then capped, so a huge step cannot wrap round to a negative count.
            var extra = perWave > 0 && waveNumber > 1 ? Math.Min((double)perWave * (waveNumber - 1), MaxWaveSpawnCount) : 0.0;

            return (int)Math.Clamp(crowd + extra, 0.0, MaxWaveSpawnCount);
        }

        /// <summary>
        /// The health multiplier wave <paramref name="waveNumber"/>'s creatures spawn with:
        /// 1 + <paramref name="perWave"/> x (wave - 1), capped at <paramref name="cap"/> and never below 1.
        /// Composed MULTIPLICATIVELY with crowd scaling at the spawn site, so a big crowd at a deep wave is
        /// both. A non-finite or non-positive step, or a cap at or below 1, leaves health alone.
        /// </summary>
        public static double WaveHealthMultiplier(int waveNumber, double perWave, double cap)
        {
            if (waveNumber <= 1 || !double.IsFinite(perWave) || perWave <= 0.0)
                return 1.0;

            if (!double.IsFinite(cap) || cap <= 1.0)
                return 1.0;

            return Math.Min(cap, 1.0 + perWave * (waveNumber - 1));
        }

        /// <summary>
        /// The DamageRating addend wave <paramref name="waveNumber"/>'s creatures spawn with:
        /// <paramref name="perWave"/> x (wave - 1), capped at <paramref name="cap"/>. Added on top of crowd
        /// scaling's own addend at the spawn site. A non-positive step or cap adds nothing.
        /// </summary>
        public static int WaveDamageRatingAddend(int waveNumber, long perWave, long cap)
        {
            if (waveNumber <= 1 || perWave <= 0 || cap <= 0)
                return 0;

            // In double so a huge step cannot overflow before the cap is applied.
            var raw = (double)perWave * (waveNumber - 1);

            return (int)Math.Min(Math.Min(raw, cap), int.MaxValue);
        }

        // ---- Boss Rush ----------------------------------------------------------------------------------------

        /// <summary>
        /// The shipped ml_digsite_bossrush_mechanic: a monster-effect overlay (PropertyString 9015 grammar)
        /// added to the Boss Rush boss on top of whatever its weenie authors. A ward that goes up once, the
        /// first heartbeat (about every 5 s) the boss is BELOW 50% health - on=hpbelow reads
        /// ExecutionerAbility.IsExecuteRange, a strict less-than - absorbing 20% of its max health for 10 s.
        ///
        /// pcthp is a FRACTION of max health, in (0, 1] (WardEffect.Validate) - 0.2, never 20.
        /// </summary>
        public const string DefaultBossRushMechanic = "ward on=hpbelow trigger=0.5 pcthp=0.2 secs=10";

        /// <summary>
        /// The share a Boss Rush encounter pays: all of it on a kill; otherwise the share of the boss's health
        /// the group removed (1 - the lowest health fraction ever sampled), clamped into
        /// [<paramref name="floor"/>, 1]. The floor is what a group that tried and barely scratched it is still
        /// paid. A non-finite sample reads as "never hit".
        /// </summary>
        public static double BossRushFraction(double minHealthFraction, bool killed, double floor)
        {
            if (killed)
                return 1.0;

            var safeFloor = double.IsFinite(floor) ? Math.Clamp(floor, 0.0, 1.0) : 0.0;

            var minHp = double.IsFinite(minHealthFraction) ? Math.Clamp(minHealthFraction, 0.0, 1.0) : 1.0;

            return Math.Clamp(1.0 - minHp, safeFloor, 1.0);
        }

        // ---- group rewards ------------------------------------------------------------------------------------

        /// <summary>
        /// The share of the tier each NON-OWNER chest pays when <paramref name="helpers"/> helpers qualify:
        /// <paramref name="pool"/> / helpers, clamped into [<paramref name="min"/>, <paramref name="max"/>].
        /// At the shipped 1.5 / 0.75 / 0.20 one or two helpers get 75% each, three get 50%, and from eight up
        /// everyone gets the 20% floor. The owner's chest is never scaled by this.
        ///
        /// Mis-set bounds fail safe: both are clamped into [0, 1], a min above the max collapses onto the max,
        /// and a non-finite pool reads as zero (so every helper gets the floor).
        /// </summary>
        public static double NonOwnerShare(int helpers, double pool, double min, double max)
        {
            var safeMax = double.IsFinite(max) ? Math.Clamp(max, 0.0, 1.0) : 0.0;
            var safeMin = double.IsFinite(min) ? Math.Clamp(min, 0.0, 1.0) : 0.0;

            if (safeMin > safeMax)
                safeMin = safeMax;

            if (helpers <= 0)
                return safeMax;

            var safePool = double.IsFinite(pool) && pool > 0.0 ? pool : 0.0;

            return Math.Clamp(safePool / helpers, safeMin, safeMax);
        }

        /// <summary>
        /// Whether one player gets a chest (and the XP/luminance) from a finished encounter.
        ///
        /// The OWNER - the digger - always does, online or not: the chest is stamped to them and waits out its
        /// TTL. Anyone else must have dealt damage credit above zero, be online, and have been SEEN at the site
        /// within <paramref name="presenceWindow"/> (the reap stamps lastPresentUtc for every live audience
        /// member). That last test is deliberately NOT "present at the end": a wipe ends with nobody alive at
        /// the site, and a present-at-end rule would pay nobody for the fight they just lost.
        ///
        /// A non-positive <paramref name="presenceWindow"/> turns the presence test off (credit and online
        /// only). <paramref name="sinceLastPresent"/> is null for a player the reap never saw.
        /// </summary>
        public static bool ChestEligible(bool isOwner, double damageCredit, bool online, TimeSpan? sinceLastPresent, TimeSpan presenceWindow)
        {
            if (isOwner)
                return true;

            if (!(damageCredit > 0.0) || !online)
                return false;

            if (presenceWindow <= TimeSpan.Zero)
                return true;

            return sinceLastPresent != null && sinceLastPresent.Value <= presenceWindow;
        }

        /// <summary>
        /// Every player a finished encounter pays, OWNER FIRST: the owner always, then every credited helper
        /// <see cref="ChestEligible"/> accepts, in ascending guid order so the result is deterministic.
        /// <paramref name="damageCredit"/> is the encounter's participation ledger (MlDigsiteEncounter.
        /// DamageCreditSnapshot); a player with no entry there dealt nothing and is never paid, however long
        /// they stood at the site. The two delegates are the impure reads (online, last seen), injected so the
        /// whole selection is testable.
        /// </summary>
        public static List<uint> SelectEligible(uint ownerGuid, IReadOnlyDictionary<uint, float> damageCredit,
            Func<uint, bool> isOnline, Func<uint, TimeSpan?> sinceLastPresent, TimeSpan presenceWindow)
        {
            var eligible = new List<uint> { ownerGuid };

            if (damageCredit == null)
                return eligible;

            foreach (var guid in damageCredit.Keys.OrderBy(g => g))
            {
                if (guid == ownerGuid || guid == 0)
                    continue;

                var online = isOnline != null && isOnline(guid);
                var since = sinceLastPresent?.Invoke(guid);

                if (ChestEligible(false, damageCredit[guid], online, since, presenceWindow))
                    eligible.Add(guid);
            }

            return eligible;
        }

        // ---- /digsite bail ----------------------------------------------------------------------------------------

        /// <summary>
        /// Why /digsite bail is refused, or null when it may go ahead. Only the OWNER - the digging player - may
        /// bail, because bailing ends the fight and pays out for everybody in it.
        /// </summary>
        /// <param name="ownsLiveEncounter">the caller has a live encounter of their own that has not already been asked to end</param>
        /// <param name="standingInOwnerName">the digger's name when the caller is standing in someone else's encounter, else null</param>
        public static string BailRefusal(bool ownsLiveEncounter, string standingInOwnerName)
        {
            if (ownsLiveEncounter)
                return null;

            if (!string.IsNullOrEmpty(standingInOwnerName))
                return $"Only {standingInOwnerName}, who dug this site, can call it off.";

            return "You have no digsite encounter to call off.";
        }

        /// <summary>The Yes/No prompt /digsite bail sends, naming the share a bail right now would pay.</summary>
        public static string BailPrompt(int tierPct)
            => $"Call off your digsite encounter now? The fight ends and pays what you have reached: {Math.Clamp(tierPct, 0, 100)}% of the full cache.";

        // ---- the one payout rule --------------------------------------------------------------------------------

        /// <summary>
        /// THE share of the full reward a finished encounter pays. Every payout - XP, luminance, the owner's
        /// chest and (scaled once more by <see cref="NonOwnerShare"/>) every helper's chest - is computed from
        /// this and nothing else.
        ///
        /// ROUND 16 OWNER RULING, and the shape every encounter now shares: a WIN
        /// (<see cref="MlDigsiteResult.FullClear"/> - wave 8 cleared, the Boss Rush boss killed, or the
        /// required Corrupted-mob count reached) pays 100%, UNMULTIPLIED. Every other outcome - a timer
        /// expiry, a wipe, or a bail with SOME progress already banked - pays its PROGRESS fraction times
        /// <see cref="MlDigsitePayoutTunables.FailPayoutMultiplier"/> (shipped 0.85). A bail with NO progress
        /// at all still pays nothing (see each type's own progress rule below) - that pre-existing Waves rule
        /// is now shared by Corruption too, which has a "progress" concept for the first time.
        ///
        /// The three PROGRESS rules:
        ///   * Waves: the tier of the highest wave CLEARED (<see cref="WavesPayoutFraction"/>).
        ///   * Boss Rush: the share of the boss's health removed (<see cref="BossRushFraction"/>), floored at
        ///     ml_digsite_bossrush_min_fraction.
        ///   * Corruption: Corrupted mobs killed over the number required
        ///     (<see cref="CorruptionProgressFraction"/>), floored at the wave-1 tier for EVERY non-bail kill
        ///     count (not only zero) so a group that fought and simply ran out of time is never paid less
        ///     than a group that fought less and got the same clock.
        /// </summary>
        public static double EncounterPayoutFraction(MlDigsiteType type, MlDigsitePayoutSnapshot snapshot, MlDigsitePayoutTunables tunables)
        {
            tunables = tunables ?? new MlDigsitePayoutTunables();

            if (snapshot.Result == MlDigsiteResult.FullClear)
                return 1.0;

            double progress;

            switch (type)
            {
                case MlDigsiteType.WavesAndMiniBoss:
                    progress = WavesPayoutFraction(snapshot, tunables);
                    break;

                case MlDigsiteType.BossRush:
                    progress = BossRushFraction(snapshot.BossMinHealthFraction, snapshot.BossKilled, tunables.BossRushMinFraction);
                    break;

                default:
                    progress = CorruptionProgressFraction(snapshot.CorruptedKills, DefaultCorruptionKillsRequired, snapshot.Bailed, tunables.Tiers);
                    break;
            }

            var multiplier = double.IsFinite(tunables.FailPayoutMultiplier) ? Math.Clamp(tunables.FailPayoutMultiplier, 0.0, 1.0) : 0.0;

            return Math.Clamp(progress * multiplier, 0.0, 1.0);
        }

        /// <summary>
        /// The Corruption shape's PROGRESS fraction (round 16 redesign - Corruption stopped being binary):
        /// Corrupted mobs killed over <paramref name="required"/>, clamped into [0, 1].
        ///
        /// OWNER RULING (round 16 code review): a NON-BAIL failure (a TTL expiry, a wipe, a stall - anything
        /// that is not the owner calling it off) is floored at the SAME floor a Waves run that reached wave 1
        /// pays (<paramref name="tiers"/>'s own first row, ordinarily 15%) - max(kills / required, floor) -
        /// for EVERY kill count, not only zero. A group that killed one Corrupted mob and then ran out of
        /// time is never paid less than a group that killed none.
        ///
        /// A BAIL is different: the owner chose to stop, so there is no floor to protect them from. A bail
        /// with ZERO kills pays nothing at all, mirroring the pre-existing Waves rule
        /// (<see cref="WavesPayoutFraction"/>) that a bail before any progress pays nothing; a bail with at
        /// least one kill pays that raw progress, unfloored.
        /// </summary>
        public static double CorruptionProgressFraction(int kills, int required, bool bailed, IReadOnlyList<MlDigsiteWaveTier> tiers)
        {
            var safeKills = Math.Max(0, kills);
            var safeRequired = Math.Max(1, required);
            var raw = Math.Clamp((double)safeKills / safeRequired, 0.0, 1.0);

            if (bailed)
                return safeKills <= 0 ? 0.0 : raw;

            var floor = WaveTierFraction(1, 0, tiers, 0.0);

            return Math.Max(raw, floor);
        }

        /// <summary>Whether <paramref name="kills"/> Corrupted mobs slain reaches <paramref name="required"/> and wins the encounter.</summary>
        public static bool CorruptionKillsWon(int kills, int required) => required > 0 && kills >= required;

        /// <summary>
        /// Whether the driver should attempt to place a Corrupted mob THIS tick (round 16 code-review fix for
        /// the softlock a failed TrySpawn used to leave behind - see MlDigsiteEncounter's pending-respawn
        /// remarks). True only when a spawn is owed (<paramref name="pending"/>) AND none is currently alive
        /// (<paramref name="objectiveAlive"/>) - the second half is what guarantees the retry can never leave
        /// two Corrupted mobs alive at once, whichever order a late success and a fresh failure land in.
        /// </summary>
        public static bool ShouldRetryCorruptedSpawn(bool pending, bool objectiveAlive) => pending && !objectiveAlive;

        // ---- Kept Siraluun: one roll per encounter -----------------------------------------------------------

        /// <summary>
        /// Whether a spawn of <paramref name="role"/> may take the encounter's single Kept Siraluun roll: the
        /// Boss Rush boss, or a Waves encounter's checkpoint mini-boss. The caller pairs this with the
        /// encounter's own one-shot latch (MlDigsiteEncounter.TryClaimKeptSiraluunRoll), which is what limits
        /// an endless run to ONE roll, at its FIRST checkpoint - otherwise a run to wave 9 would roll three
        /// times and make the feather several times more common than the tuned chance says.
        /// </summary>
        public static bool KeptSiraluunRollRole(MlDigsiteRole role)
            => role == MlDigsiteRole.Boss || role == MlDigsiteRole.Checkpoint;
    }
}
