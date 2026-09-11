using System;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The measured group throughput of one run, and the pure rules that turn it into a named boss's health
    /// multiplier (TECH-DESIGN 2.15 / C16, BOSS-STANDARD.md section 3, flag
    /// world_events_boss_throughput_scaling_enabled).
    ///
    /// The metric is HP CLEARED PER ACTIVE SECOND, never damage dealt: every creature this run owns that
    /// dies contributes its Health.MaxValue - the health the players actually chewed through, read after
    /// the spawn multipliers were applied - and the denominator is the wall-clock seconds since the run went
    /// Active. Damage is deliberately not the input: DamageHistory zeroes an entry in place on a heal to
    /// full (DamageHistory.cs:140-151) and prunes entries older than three minutes (DamageHistory.cs:170-204),
    /// so a sum over it understates a long fight by an unknowable amount.
    ///
    /// Modelled on <see cref="WorldEventPaceController"/>: no clock, no world objects, no engine types, so
    /// every rule here is unit testable (TECH-DESIGN D6). The caller owns the clock and passes elapsed
    /// seconds in.
    ///
    /// Threading: <see cref="NoteCleared"/> is called from WorldEvent.OnCreatureDied, i.e. from Creature.Die
    /// on whichever thread ticked the dying creature's landblock, while <see cref="Throughput"/> and
    /// <see cref="TotalHealthCleared"/> are read from the world thread's WorldEventManager.Tick and from
    /// command handlers. The ordering WorldEventParticipation's class comment documents
    /// (WorldManager.cs:719 calls LandblockManager.Tick, which joins both of its Parallel.ForEach passes,
    /// before WorldManager.cs:742 calls WorldEventManager.Tick) already keeps those apart, but this accumulator
    /// is two fields rather than a dictionary the tick alone touches, so it takes its own monitor as well -
    /// a torn total would silently mis-size a boss, and the lock costs one uncontended acquire per death.
    /// </summary>
    public sealed class WorldEventThroughput
    {
        private readonly object sync = new object();

        private double totalHealthCleared;

        private int clearedCount;

        /// <summary>Total Health.MaxValue of every creature counted so far. Never negative.</summary>
        public double TotalHealthCleared
        {
            get { lock (sync) return totalHealthCleared; }
        }

        /// <summary>How many creatures have been counted. Reported for diagnosis, never used in the maths.</summary>
        public int ClearedCount
        {
            get { lock (sync) return clearedCount; }
        }

        /// <summary>
        /// One creature the players cleared. <paramref name="maxHealth"/> is its Health.MaxValue at death -
        /// after crowd/pace/objective scaling, which is exactly what the group had to remove. 0 is ignored
        /// (it contributes nothing and would only inflate <see cref="ClearedCount"/>).
        /// </summary>
        public void NoteCleared(uint maxHealth)
        {
            if (maxHealth == 0)
                return;

            lock (sync)
            {
                totalHealthCleared += maxHealth;
                clearedCount++;
            }
        }

        /// <summary>
        /// HP cleared per second over <paramref name="elapsedSeconds"/> of run time. 0 for a non-positive or
        /// non-finite elapsed, so a run that has not started yet can never produce a rate.
        /// </summary>
        public double Throughput(double elapsedSeconds)
        {
            if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
                return 0;

            var total = TotalHealthCleared;

            return total <= 0 ? 0 : total / elapsedSeconds;
        }

        /// <summary>
        /// Whether the run has measured enough to size a boss from: the wave phase has run for at least
        /// <paramref name="minSampleSeconds"/> AND something has actually died. Both terms matter - a long
        /// quiet run has an elapsed time but no rate to trust, and a fast one has a rate drawn from too few
        /// seconds to be a run's pace rather than one lucky pull.
        /// </summary>
        public bool HasSample(double elapsedSeconds, double minSampleSeconds)
        {
            if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
                return false;

            if (!double.IsFinite(minSampleSeconds) || minSampleSeconds < 0)
                return false;

            return elapsedSeconds >= minSampleSeconds && TotalHealthCleared > 0;
        }

        /// <summary>
        /// The boss health multiplier that makes a boss of <paramref name="floorHealth"/> base health take
        /// <paramref name="targetSeconds"/> to kill at a demonstrated rate of <paramref name="throughputHps"/>
        /// HP per second (D6 - pure):
        ///
        ///   mult = clamp(throughputHps * calibration * targetSeconds / floorHealth, 1.0, capHealth / floorHealth)
        ///
        /// The 1.0 floor is the "never weaker than authored" rule: the floor health is a REBASE applied
        /// before this multiplier (WorldEventSpawner.ApplyBaseHealthOverride reads WorldEvent.BossRebaseHealth),
        /// so a group slower than the floor implies simply gets the floor. The ceiling keeps the product
        /// inside the same bound BossDef.BaseHealth documents.
        ///
        /// Returns 1.0 rather than throwing for every input a boss cannot be sized from: floorHealth 0 (the
        /// shipped default, meaning "this boss does not do throughput scaling"), any non-finite input, and a
        /// capHealth below floorHealth (treated as cap == floor, i.e. no headroom above the floor at all).
        /// </summary>
        public static double ResolveBossMult(double throughputHps, double calibration, double targetSeconds,
            uint floorHealth, uint capHealth)
        {
            if (floorHealth == 0)
                return 1.0;

            if (!double.IsFinite(throughputHps) || !double.IsFinite(calibration) || !double.IsFinite(targetSeconds))
                return 1.0;

            var ceiling = CapRatio(floorHealth, capHealth);

            var wanted = throughputHps * calibration * targetSeconds;

            if (!double.IsFinite(wanted))
                return ceiling;

            var mult = wanted / floorHealth;

            if (!double.IsFinite(mult) || mult < 1.0)
                return 1.0;

            return mult > ceiling ? ceiling : mult;
        }

        /// <summary>
        /// The ceiling <see cref="ResolveBossMult"/> clamps to, as a multiplier on the floor (D6 - pure):
        /// capHealth / floorHealth, and 1.0 whenever that would be less than 1 (a cap below the floor, or a
        /// floor of 0).
        /// </summary>
        public static double CapRatio(uint floorHealth, uint capHealth)
        {
            if (floorHealth == 0 || capHealth <= floorHealth)
                return 1.0;

            return (double)capHealth / floorHealth;
        }

        /// <summary>
        /// The late-arrival term (D6 - pure): a multiplier frozen for <paramref name="countAtSpawn"/> players
        /// scaled to the <paramref name="countNow"/> standing in front of the boss. Both counts floor at 1, so
        /// an empty sample can never divide by zero or zero the multiplier.
        ///
        /// The caller still clamps the result to <see cref="CapRatio"/> and still passes it through
        /// WorldEvent.RatchetMult, so this can only ever RAISE a standing boss.
        /// </summary>
        public static double ScaleForAudience(double mult, int countNow, int countAtSpawn)
        {
            if (!double.IsFinite(mult))
                return mult;

            var now = Math.Max(countNow, 1);
            var atSpawn = Math.Max(countAtSpawn, 1);

            return mult * now / atSpawn;
        }

        /// <summary>
        /// Whether one death counts toward the metric (D6 - pure). Wave trash, overflow champions and the
        /// objective sources (rifts, pillars) all count - they are what the group cleared. The boss itself
        /// never does: it is the thing being sized, so counting it would let a boss's own health inflate the
        /// rate that sizes the next one. Decor and spawn-time npcs never do either; an npc cannot even reach
        /// the death hook (WorldEventSpawner.Adopt withholds its P_WorldEvent back-reference), and this is
        /// belt and braces for both.
        /// </summary>
        public static bool Counts(uint guid, uint bossGuid, bool isDecor, bool isNpc)
        {
            if (guid == 0 || isDecor || isNpc)
                return false;

            return guid != bossGuid;
        }

        public override string ToString() => $"hpCleared={TotalHealthCleared:F0} kills={ClearedCount}";
    }
}
