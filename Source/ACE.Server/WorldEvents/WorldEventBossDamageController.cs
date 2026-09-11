using System;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The five dials the damage controller reads (TECH-DESIGN 2.16, BOSS-STANDARD.md 3.2). Held as a
    /// plain value so <see cref="WorldEventBossDamageController"/> itself never touches PropertyManager -
    /// the impure read lives on WorldEvent.ReadBossDamageDials, behind the WorldEvent.BossDamageDialSource
    /// seam, exactly as WorldEventRosterSelector.DialSource does for the band dials.
    /// </summary>
    public readonly struct BossDamageDials
    {
        /// <summary>How many ordinary hits the TOP player should survive. 3 means "2 to 4".</summary>
        public readonly double HitsToKill;

        /// <summary>Lower bound on the boss's DamageRating. Negative is legal and meaningful.</summary>
        public readonly int RatingMin;

        /// <summary>Upper bound on the boss's DamageRating.</summary>
        public readonly int RatingMax;

        /// <summary>Non-crit hits that must land before an adjustment may be proposed.</summary>
        public readonly int SampleHits;

        /// <summary>Largest rating CHANGE one adjustment may make, after the first one of a run.</summary>
        public readonly int StepCap;

        public BossDamageDials(double hitsToKill, int ratingMin, int ratingMax, int sampleHits, int stepCap)
        {
            HitsToKill = hitsToKill;
            RatingMin = ratingMin;
            RatingMax = ratingMax;
            SampleHits = sampleHits;
            StepCap = stepCap;
        }

        /// <summary>The built-in constants, which are also the shipped property defaults.</summary>
        public static BossDamageDials Defaults => new BossDamageDials(
            WorldEventBossDamageController.DefaultHitsToKill,
            WorldEventBossDamageController.DefaultRatingMin,
            WorldEventBossDamageController.DefaultRatingMax,
            WorldEventBossDamageController.DefaultSampleHits,
            WorldEventBossDamageController.DefaultStepCap);

        public override string ToString() =>
            $"hitsToKill={HitsToKill:F2} rating=[{RatingMin},{RatingMax}] sampleHits={SampleHits} stepCap={StepCap}";
    }

    /// <summary>
    /// What one <see cref="WorldEventBossDamageController.TryAdvise"/> call looked at, for the log line and
    /// "/worldevent status". Carries no decision - the return value is the decision.
    /// </summary>
    public readonly struct BossDamageDiagnostics
    {
        /// <summary>topMaxHealth / hitsToKill: the damage ONE ordinary hit should do. 0 when not computed.</summary>
        public readonly double TargetHit;

        /// <summary>Mean damage of the buffered non-crit hits, AFTER mitigation. 0 when nothing is buffered.</summary>
        public readonly double ObservedMean;

        /// <summary>Buffered non-crit hits the mean was taken over.</summary>
        public readonly int Hits;

        /// <summary>Crits seen since the last advice. Excluded from the mean; reported so a wildly swingy
        /// fight is visible in the log rather than silently absent.</summary>
        public readonly int Crits;

        /// <summary>True when this would be the FIRST adjustment of the run, i.e. the step cap does not apply.</summary>
        public readonly bool FirstAdjustment;

        public BossDamageDiagnostics(double targetHit, double observedMean, int hits, int crits, bool firstAdjustment)
        {
            TargetHit = targetHit;
            ObservedMean = observedMean;
            Hits = hits;
            Crits = crits;
            FirstAdjustment = firstAdjustment;
        }

        public override string ToString() =>
            $"target={TargetHit:F0} observed={ObservedMean:F0} hits={Hits} crits={Crits}";
    }

    /// <summary>
    /// MEASURED boss damage scaling (TECH-DESIGN 2.16, BOSS-STANDARD.md 3.2, flag
    /// world_events_boss_damage_scaling_enabled). The owner requirement is that the TOP player present -
    /// the highest Health.MaxValue among counted participants - dies in roughly hitsToKill ordinary hits.
    /// A low-level player being one-shot is accepted.
    ///
    /// The lever is the boss's DamageRating PropertyInt, which multiplies outgoing damage on every path the
    /// boss actually uses: melee/missile through DamageEvent (Source/ACE.Server/Entity/DamageEvent.cs:256
    /// DamageRatingBaseMod, :270 DamageBeforeMitigation) and spells through
    /// WorldObject_Magic.cs:2745 / SpellProjectile.cs:869.
    ///
    /// **The input is MEASURED, never predicted.** A boss's damage per swing cannot be read off its weenie:
    /// the defender's armour, resistances, shield, the body part struck and the crit roll move the number
    /// that actually lands by 2-3x. So this measures what the boss really did to real players, exactly the
    /// way WorldEventThroughput measures HP cleared instead of predicting it, and inverts the rating from
    /// the observation. The consequence is a controller with a warm-up: nothing happens until the boss has
    /// landed <see cref="BossDamageDials.SampleHits"/> non-crit hits.
    ///
    /// **Crits are excluded from the mean.** The requirement is about an ORDINARY hit; a crit is several
    /// times an ordinary hit, and letting one into a four-sample mean would drive the rating down hard on a
    /// lucky roll. They are counted separately so the log still shows they happened.
    ///
    /// Pure, in the TECH-DESIGN D6 sense that <see cref="WorldEventThroughput"/> and
    /// <see cref="WorldEventPaceController"/> are: no engine types, no PropertyManager, no clock. The caller
    /// owns the dials, the boss and the queue.
    ///
    /// Threading: <see cref="NoteHit"/> is called from the damage sites (Player.TakeDamage,
    /// SpellProjectile.DamageTarget, WorldObject_Magic's life-boost path), i.e. from landblock-group
    /// threads, while <see cref="TryAdvise"/> runs on the world thread from WorldEvent.TickActive. Like
    /// WorldEventThroughput this takes its own monitor rather than relying on the tick ordering: a torn
    /// window would mis-size the boss's damage, and the lock costs one uncontended acquire per hit.
    /// </summary>
    public sealed class WorldEventBossDamageController
    {
        /// <summary>
        /// Ring capacity. A window this long is never needed by the shipped SampleHits of 4; it exists so a
        /// dial set to something large still has somewhere to put its samples, and so a boss that hits
        /// while nobody is advising (the flag off, no landblock) can never grow this unboundedly.
        /// </summary>
        public const int MaxTrackedHits = 64;

        public const double DefaultHitsToKill = 3.0;
        public const int DefaultRatingMin = -80;
        public const int DefaultRatingMax = 300;
        public const int DefaultSampleHits = 4;
        public const int DefaultStepCap = 50;

        /// <summary>
        /// Rating moves smaller than this are not worth a queued write on a live boss: a rating of 5 is a
        /// 5% damage change, which is inside the noise of the very sample that produced it.
        /// </summary>
        public const int DeadBand = 5;

        private readonly object sync = new object();

        private readonly int[] window = new int[MaxTrackedHits];

        private int windowNext;

        private int windowCount;

        private int critsSinceAdvice;

        private int totalHits;

        private bool hasAdvised;

        /// <summary>Every hit accepted over the life of the run, crits included. Never reset; diagnosis only.</summary>
        public int TotalHits
        {
            get { lock (sync) return totalHits; }
        }

        /// <summary>Buffered non-crit hits available to the next advice.</summary>
        public int PendingHits
        {
            get { lock (sync) return windowCount; }
        }

        /// <summary>Crits seen since the last advice.</summary>
        public int PendingCrits
        {
            get { lock (sync) return critsSinceAdvice; }
        }

        /// <summary>Mean of every buffered non-crit hit, or 0 when nothing is buffered.</summary>
        public double PendingMean
        {
            get { lock (sync) return Mean(windowCount); }
        }

        /// <summary>
        /// <see cref="PendingHits"/>, <see cref="PendingCrits"/> and <see cref="PendingMean"/> together,
        /// under ONE <c>lock (sync)</c>, following <see cref="WorldEventSpawner.SnapshotBossPlacement"/>'s
        /// convention. ANY caller that reports more than one of the three - "/worldevent status", a log
        /// line - must use this instead of the individual properties.
        ///
        /// The three properties above are each individually locked, so reading them as three separate calls
        /// can observe a torn set: hits arrive on landblock threads throughout, and a true
        /// <see cref="TryAdvise"/> resets all three at once, so a status line built from three reads can
        /// show a hit count from before the reset beside a mean from after it - or, worse, a non-zero hit
        /// count beside a mean of 0, which reads as "the boss is doing no damage" when it is simply a
        /// spent window. Nothing here is derived by the caller; the mean is computed inside the same lock
        /// as the count it belongs to.
        ///
        /// The individual properties are kept for single-value reads (the tick's own sample gate reads only
        /// <see cref="PendingHits"/>), where there is nothing to tear against.
        /// </summary>
        public (int Hits, int Crits, double Mean) PendingSnapshot()
        {
            lock (sync)
                return (windowCount, critsSinceAdvice, Mean(windowCount));
        }

        /// <summary>True once an advice has been issued, i.e. the step cap is armed from here on.</summary>
        public bool HasAdvised
        {
            get { lock (sync) return hasAdvised; }
        }

        /// <summary>
        /// One landed hit from the boss on a counted participant. <paramref name="damage"/> is the damage
        /// ACTUALLY TAKEN, after every mitigation, which is the honest signal - it is the number the player
        /// felt. <paramref name="defenderMaxHealth"/> is that defender's Health.MaxValue.
        ///
        /// A non-positive damage and a zero max health are both ignored outright: a fully-mitigated or
        /// invincible hit measures nothing, and a zero max health means the defender was not readable, so
        /// counting it would only pull the mean toward a number that never happened.
        ///
        /// The max health is NOT used in the maths here - the target is set from the TOP player in the
        /// audience, not from whoever happened to be hit - but a hit with no readable defender is exactly
        /// the hit worth dropping, so it is the gate.
        /// </summary>
        public void NoteHit(int damage, uint defenderMaxHealth, bool crit)
        {
            if (damage <= 0 || defenderMaxHealth == 0)
                return;

            lock (sync)
            {
                totalHits++;

                if (crit)
                {
                    critsSinceAdvice++;
                    return;
                }

                window[windowNext] = damage;
                windowNext = (windowNext + 1) % MaxTrackedHits;

                if (windowCount < MaxTrackedHits)
                    windowCount++;
            }
        }

        /// <summary>
        /// The whole decision (pure apart from this object's own buffer).
        ///
        ///   targetHit   = topMaxHealth / hitsToKill
        ///   wantedMult  = currentMult * targetHit / observedMean
        ///   newRating   = MultToRating(wantedMult), then step-capped, then clamped to [Min, Max]
        ///
        /// It works because damage is PROPORTIONAL to the rating multiplier: everything else in the damage
        /// pipeline - base damage, attribute, power, armour, resistance - is unchanged between the hits that
        /// were measured and the hits that follow, so the ratio targetHit/observedMean is exactly the factor
        /// the multiplier must move by. Nothing about the boss's weenie is needed, which is the point.
        ///
        /// Returns false, changing nothing, whenever there is no decision to make: fewer than SampleHits
        /// non-crit hits are buffered, the top max health is unknown, the observed mean is not positive, or
        /// the proposal lands inside <see cref="DeadBand"/> of the current rating.
        ///
        /// The FIRST adjustment of a run is exempt from the step cap, so a boss authored at a badly wrong
        /// rating converges in one step instead of walking there 50 points at a time while players die.
        /// Every later one is capped, which is what stops one unlucky window from swinging the fight.
        ///
        /// The buffer is reset ONLY on a true return. A dead-band refusal deliberately leaves the window
        /// rolling: it is not an adjustment, and throwing the samples away would make the next decision wait
        /// out another whole warm-up for no reason.
        /// </summary>
        public bool TryAdvise(int currentRating, uint topMaxHealth, BossDamageDials d, out int newRating,
            out BossDamageDiagnostics diag)
        {
            newRating = currentRating;

            lock (sync)
            {
                var sampleHits = Clamp(d.SampleHits <= 0 ? DefaultSampleHits : d.SampleHits, 1, MaxTrackedHits);

                var mean = Mean(sampleHits);

                diag = new BossDamageDiagnostics(0, mean, windowCount, critsSinceAdvice, !hasAdvised);

                if (windowCount < sampleHits)
                    return false;

                if (topMaxHealth == 0)
                    return false;

                var hitsToKill = d.HitsToKill;

                if (!double.IsFinite(hitsToKill) || hitsToKill <= 0)
                    hitsToKill = DefaultHitsToKill;

                var targetHit = topMaxHealth / hitsToKill;

                diag = new BossDamageDiagnostics(targetHit, mean, windowCount, critsSinceAdvice, !hasAdvised);

                if (!(mean > 0) || !double.IsFinite(targetHit))
                    return false;

                var wantedMult = RatingToMult(currentRating) * targetHit / mean;

                if (!double.IsFinite(wantedMult) || wantedMult <= 0)
                    return false;

                var proposed = MultToRating(wantedMult);

                if (hasAdvised)
                {
                    var stepCap = d.StepCap > 0 ? d.StepCap : DefaultStepCap;
                    var delta = proposed - currentRating;

                    if (delta > stepCap)
                        proposed = currentRating + stepCap;
                    else if (delta < -stepCap)
                        proposed = currentRating - stepCap;
                }

                var min = d.RatingMin;
                var max = d.RatingMax < min ? min : d.RatingMax;

                proposed = Clamp(proposed, min, max);

                if (Math.Abs(proposed - currentRating) < DeadBand)
                    return false;

                newRating = proposed;

                hasAdvised = true;
                windowCount = 0;
                windowNext = 0;
                critsSinceAdvice = 0;

                return true;
            }
        }

        /// <summary>
        /// The damage multiplier one DamageRating produces (D6 - pure). Mirrors the engine exactly:
        /// Creature.GetPositiveRatingMod is (100 + r) / 100, and it forwards a NEGATIVE rating to
        /// GetNegativeRatingMod, which is 100 / (100 + |r|) - NOT (100 + r) / 100
        /// [Source/ACE.Server/WorldObjects/Creature_Rating.cs:26-46]. So -50 is 0.667x, not 0.5x, and a
        /// controller that inverted the positive formula on both sides would consistently over-shoot every
        /// downward correction.
        /// </summary>
        public static double RatingToMult(int rating)
        {
            if (rating >= 0)
                return (100.0 + rating) / 100.0;

            return 100.0 / (100.0 - rating);
        }

        /// <summary>
        /// The exact inverse of <see cref="RatingToMult"/> (D6 - pure): a multiplier at or above 1 is
        /// mult * 100 - 100, and one below 1 is 100 - 100 / mult, both rounded. A non-finite or
        /// non-positive multiplier reads as rating 0 rather than throwing.
        /// </summary>
        public static int MultToRating(double mult)
        {
            if (!double.IsFinite(mult) || mult <= 0)
                return 0;

            var rating = mult >= 1.0
                ? mult * 100.0 - 100.0
                : 100.0 - 100.0 / mult;

            if (rating > int.MaxValue)
                return int.MaxValue;

            if (rating < int.MinValue)
                return int.MinValue;

            return (int)Math.Round(rating, MidpointRounding.AwayFromZero);
        }

        /// <summary>Caller must hold <see cref="sync"/>. Mean of the most recent <paramref name="n"/> entries.</summary>
        private double Mean(int n)
        {
            if (n <= 0 || windowCount == 0)
                return 0;

            var take = Math.Min(n, windowCount);

            double sum = 0;

            for (var i = 1; i <= take; i++)
            {
                var index = windowNext - i;

                if (index < 0)
                    index += MaxTrackedHits;

                sum += window[index];
            }

            return sum / take;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;

            return value > max ? max : value;
        }

        public override string ToString()
        {
            // One snapshot, not three property reads - see PendingSnapshot's remarks.
            var (hits, crits, mean) = PendingSnapshot();

            return $"hits={hits} crits={crits} mean={mean:F0} total={TotalHits}";
        }
    }
}
