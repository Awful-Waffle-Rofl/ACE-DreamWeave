using System.Collections.Generic;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// Which of the three encounter shapes a completed ordinary dig rolled. The Aun Relaria boss dig is
    /// deliberately NOT a member: it is the pre-existing boss-variant map mechanism
    /// (MlTreasure.MlRelariaSpawner), reached from a different arm of TreasureMapHandler.FinishDig and
    /// rebalanced only by lowering ml_treasure_relaria_chance. Adding it here would have built a second,
    /// parallel Relaria path.
    /// </summary>
    public enum MlDigsiteType
    {
        /// <summary>Three waves of trash, then a mini-boss. The common case.</summary>
        WavesAndMiniBoss,

        /// <summary>A priority mob whose survival fills a corruption meter while the rest of the field hardens.</summary>
        CorruptionMeter,

        /// <summary>One hard single-target boss, no waves.</summary>
        BossRush,
    }

    /// <summary>
    /// What an encounter is doing right now. There is no Starting state: the digger is standing on the
    /// anchor with the landblock already loaded, so the first spawn happens inside TryStart and the
    /// encounter is Active from the moment it is registered.
    /// </summary>
    public enum MlDigsiteState
    {
        Active,
        Ended,
    }

    /// <summary>
    /// How an encounter finished, which is the only input to the payout tier. Partial and Failed pay the
    /// same reduced fraction (ml_digsite_partial_payout_fraction) and are kept distinct only so the log
    /// line and the player-facing wording can tell a timed-out clear from a lost one.
    /// </summary>
    public enum MlDigsiteResult
    {
        /// <summary>Objective killed inside its limits. Pays the full tier.</summary>
        FullClear,

        /// <summary>Objective killed, but only after a wave timer or the stall timer had already expired.</summary>
        PartialClear,

        /// <summary>Meter filled, TTL expired, landblock unloaded, or everyone left. Pays the reduced tier.</summary>
        Failed,

        /// <summary>
        /// Ended without a binary win or loss, and paid by how far the fight got instead: the wave tier reached
        /// (Waves) or the share of the boss's health removed (Boss Rush). See
        /// MlDigsiteRules.EncounterPayoutFraction, which is the only reader that turns this into a number.
        /// </summary>
        Scored,
    }

    /// <summary>
    /// What one reported death turned out to be, from MlDigsiteEncounter.NoteCreatureDeath. None covers both
    /// "not one of ours" and "this encounter has already ended" - either way the report is inert.
    /// </summary>
    public enum MlDigsiteDeathKind
    {
        None,

        /// <summary>A creature of the live wave (or a Corruption encounter's field).</summary>
        Wave,

        /// <summary>The encounter's objective: the priority mob or the Boss Rush boss. A win.</summary>
        Objective,

        /// <summary>A Waves encounter's checkpoint mini-boss. Not a win; adds a checkpoint kill.</summary>
        Checkpoint,

        /// <summary>
        /// A Boss Rush mechanic add (MlDigsiteRole.Add). Not a win, not a wave kill, and NOT a checkpoint: it
        /// moves no payout bookkeeping at all. The Boss Rush mechanic driver is the only thing that reacts to
        /// it - a volatile add detonates where it fell, an immune phase's add set shrinks by one.
        /// </summary>
        Add,
    }

    /// <summary>
    /// What one tick of an endless Waves encounter resolves to (MlDigsiteRules.WavesTick). The point of
    /// naming it is that the SAME value drives both what the tick does and whether the time-forced
    /// checkpoint mini-boss may go up on it (MlDigsiteRules.ShouldForceCheckpoint), so the two can never
    /// drift apart: before this existed, MlDigsiteManager.DriveWaves decided the forced spawn BEFORE it had
    /// asked the wave clocks whether the run was about to end, and could summon a mini-boss onto the tick
    /// that tore the encounter down.
    /// </summary>
    public enum MlDigsiteWavesTick
    {
        /// <summary>A wave is scheduled and its breather has not elapsed. Nothing to place yet.</summary>
        Breather,

        /// <summary>The scheduled wave is due now.</summary>
        SpawnWave,

        /// <summary>A live wave has run out one of its two clocks: THIS TICK ENDS THE RUN.</summary>
        EndRun,

        /// <summary>A live wave is still being fought and neither clock has expired.</summary>
        FightOn,
    }

    /// <summary>
    /// Which of the two wave-only clocks <see cref="MlDigsiteRules.NearestWaveClock"/> reports as nearer to
    /// expiry, if either is even enabled. Named the same way <see cref="MlDigsiteRules.WaveClockExpired"/>'s
    /// out reason strings are, so a log line or status line reading this can say which clock it means.
    /// </summary>
    public enum MlDigsiteWaveClockKind
    {
        /// <summary>Neither clock is enabled (both tunables are 0 or less).</summary>
        None,

        /// <summary>The per-wave time limit (ml_digsite_wave_time_limit_seconds).</summary>
        WaveTimeLimit,

        /// <summary>The no-kill stall timeout (ml_digsite_stall_timeout_seconds).</summary>
        Stall,
    }

    /// <summary>
    /// One row of the ml_digsite_wave_tiers table: reaching <see cref="Wave"/> pays <see cref="Fraction"/> of
    /// the full reward. Built only by MlDigsiteRules.ParseTierTable, which rejects a non-positive fraction, so
    /// every tier a live encounter reads pays something.
    /// </summary>
    public readonly struct MlDigsiteWaveTier
    {
        public MlDigsiteWaveTier(int wave, double fraction)
        {
            Wave = wave;
            Fraction = fraction;
        }

        public int Wave { get; }

        public double Fraction { get; }

        public override string ToString() => $"{Wave}:{Fraction}";
    }

    /// <summary>
    /// Everything about a finished encounter that its payout depends on, read once under the encounter's lock
    /// (MlDigsiteEncounter.PayoutSnapshot) so the fraction is computed from one consistent moment.
    /// </summary>
    public readonly struct MlDigsitePayoutSnapshot
    {
        public MlDigsitePayoutSnapshot(MlDigsiteResult result, int highestWave, int checkpointKills,
            double bossMinHealthFraction, bool bossKilled, int highestWaveCleared, bool bailed,
            int corruptedKills = 0)
        {
            Result = result;
            HighestWave = highestWave;
            CheckpointKills = checkpointKills;
            BossMinHealthFraction = bossMinHealthFraction;
            BossKilled = bossKilled;
            HighestWaveCleared = highestWaveCleared;
            Bailed = bailed;
            CorruptedKills = corruptedKills;
        }

        public MlDigsiteResult Result { get; }

        /// <summary>
        /// The highest wave number that was ever placed. 0 when no wave was. NOT what the Waves tier is paid
        /// from - wave 1 is placed the moment the dig opens, so paying on this would pay for no fighting.
        /// </summary>
        public int HighestWave { get; }

        /// <summary>
        /// The highest wave whose field was emptied (every wave creature dead). THE number the Waves tier is
        /// paid from (MlDigsiteRules.EncounterPayoutFraction). 0 until the first wave is cleared.
        /// </summary>
        public int HighestWaveCleared { get; }

        /// <summary>True when the end being paid (or priced, for the bail prompt) is the owner's /digsite bail.</summary>
        public bool Bailed { get; }

        /// <summary>Checkpoint mini-bosses killed.</summary>
        public int CheckpointKills { get; }

        /// <summary>The lowest health fraction the Boss Rush boss was ever sampled at, in [0, 1]. 1.0 when never hit.</summary>
        public double BossMinHealthFraction { get; }

        /// <summary>True once the Boss Rush boss has died.</summary>
        public bool BossKilled { get; }

        /// <summary>
        /// Corrupted mobs killed so far (Corruption shape only, round 16 redesign). Reaching
        /// ml_digsite_corruption_kills_required wins. 0 for every other shape.
        /// </summary>
        public int CorruptedKills { get; }
    }

    /// <summary>
    /// The tunables EncounterPayoutFraction reads, gathered into one value so the rule stays pure.
    /// </summary>
    public sealed class MlDigsitePayoutTunables
    {
        public IReadOnlyList<MlDigsiteWaveTier> Tiers { get; set; }

        public double CheckpointBonus { get; set; }

        public double BossRushMinFraction { get; set; }

        /// <summary>
        /// Round 16: no longer read by EncounterPayoutFraction (Corruption stopped being binary), but left
        /// on this class and still wired from MlDigsiteTunables.Payout so the many test call sites that set
        /// it do not need to change. ml_digsite_partial_payout_fraction is now a dead tunable.
        /// </summary>
        public double PartialFraction { get; set; }

        /// <summary>
        /// Round 16 owner ruling: every non-WIN outcome (timer expiry, wipe, bail with progress) pays its
        /// progress fraction times this, instead of the progress fraction straight. A WIN (FullClear) is
        /// never multiplied by this - see EncounterPayoutFraction.
        /// </summary>
        public double FailPayoutMultiplier { get; set; } = 1.0;
    }

    /// <summary>
    /// What a spawned creature is FOR, which decides its roster band and whether it gets an intro emote.
    /// Every role is corpse-suppressed and persistence-excluded identically; the role changes only which
    /// wcid band it is drawn from and how the encounter counts its death.
    /// </summary>
    public enum MlDigsiteRole
    {
        /// <summary>Ordinary wave trash, and the surrounding field in a Corruption encounter.</summary>
        Wave,

        /// <summary>The Corruption encounter's priority mob: killing it wins immediately.</summary>
        Priority,

        /// <summary>The Waves encounter's closing mini-boss.</summary>
        MiniBoss,

        /// <summary>The Boss Rush encounter's single target.</summary>
        Boss,

        /// <summary>
        /// A Waves encounter's checkpoint mini-boss, placed ALONGSIDE every ml_digsite_checkpoint_every'th
        /// wave. Drawn from the MiniBoss band. Tracked apart from the live wave, so it never holds a wave open,
        /// and its death is not a win: it only adds a checkpoint kill to the payout.
        /// </summary>
        Checkpoint,

        /// <summary>
        /// A Boss Rush mechanic add: the ONE shared add weenie the volatile-adds and immune-phases mechanics
        /// both place (owner ruling 2026-09-20 - one reused weenie whose health and speed scale per SET, never
        /// a distinct weenie per set). Tracked apart from the wave (MlDigsiteEncounter.TrackAdd) so its death
        /// moves no payout bookkeeping, and worth nothing at all to the tier: an encounter cannot be farmed by
        /// letting the driver spawn adds.
        /// </summary>
        Add,
    }
}
