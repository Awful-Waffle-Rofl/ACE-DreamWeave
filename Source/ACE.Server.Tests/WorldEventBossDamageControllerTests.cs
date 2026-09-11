using System;
using System.Threading;
using System.Threading.Tasks;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the measured boss damage scaling (TECH-DESIGN 2.16, BOSS-STANDARD.md 3.2, flag
    /// world_events_boss_damage_scaling_enabled): the hit accumulator, the sample gate, the rating
    /// inversion, the step cap and its first-adjustment exemption, the bounds, and the dead band.
    ///
    /// Every case here is a plain object or a pure static - no clock, no Creature, no Player, no landblock
    /// (TECH-DESIGN D6). The one exception is the dial seam test at the bottom, which drives
    /// WorldEvent's static property readers and never constructs a WorldEvent.
    /// </summary>
    [TestClass]
    public class WorldEventBossDamageControllerTests
    {
        // The shipped dials, so the numbers below are the real ones.
        private static BossDamageDials Shipped => BossDamageDials.Defaults;

        private static void NoteHits(WorldEventBossDamageController c, int count, int damage, bool crit = false)
        {
            for (var i = 0; i < count; i++)
                c.NoteHit(damage, 3000, crit);
        }

        // ---- the accumulator ---------------------------------------------------------------------------

        [TestMethod]
        public void NoteHit_IgnoresNonPositiveDamageAndAZeroMaxHealth()
        {
            var c = new WorldEventBossDamageController();

            c.NoteHit(0, 3000, false);
            c.NoteHit(-5, 3000, false);
            c.NoteHit(500, 0, false);
            c.NoteHit(0, 0, true);

            Assert.AreEqual(0, c.TotalHits, "none of those four is a measurable hit");
            Assert.AreEqual(0, c.PendingHits);
            Assert.AreEqual(0, c.PendingCrits);
            Assert.AreEqual(0.0, c.PendingMean, 0.0000001);

            c.NoteHit(500, 3000, false);

            Assert.AreEqual(1, c.TotalHits);
            Assert.AreEqual(1, c.PendingHits);
            Assert.AreEqual(500.0, c.PendingMean, 0.0000001);
        }

        [TestMethod]
        public void NoteHit_CountsCritsSeparatelyAndKeepsThemOutOfTheMean()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 4, 500);
            NoteHits(c, 2, 5000, crit: true);

            Assert.AreEqual(6, c.TotalHits, "a crit is still a hit that landed");
            Assert.AreEqual(4, c.PendingHits, "but only the non-crits are buffered for the mean");
            Assert.AreEqual(2, c.PendingCrits);
            Assert.AreEqual(500.0, c.PendingMean, 0.0000001, "two 5000 crits must not move a 500 mean");
        }

        [TestMethod]
        public void NoteHit_RingBufferNeverGrowsPastItsCapacity()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, WorldEventBossDamageController.MaxTrackedHits + 25, 400);

            Assert.AreEqual(WorldEventBossDamageController.MaxTrackedHits + 25, c.TotalHits);
            Assert.AreEqual(WorldEventBossDamageController.MaxTrackedHits, c.PendingHits);
        }

        // ---- the rating <-> multiplier mapping ---------------------------------------------------------

        [TestMethod]
        public void RatingToMult_MirrorsTheEngineInBothDirections()
        {
            // Creature.GetPositiveRatingMod: (100 + r) / 100.
            Assert.AreEqual(1.00, WorldEventBossDamageController.RatingToMult(0), 0.0000001);
            Assert.AreEqual(2.00, WorldEventBossDamageController.RatingToMult(100), 0.0000001);
            Assert.AreEqual(4.00, WorldEventBossDamageController.RatingToMult(300), 0.0000001);

            // Creature.GetNegativeRatingMod: 100 / (100 + |r|) - NOT (100 + r) / 100. -50 is 0.667x, not 0.5x.
            Assert.AreEqual(100.0 / 150.0, WorldEventBossDamageController.RatingToMult(-50), 0.0000001);
            Assert.AreEqual(100.0 / 180.0, WorldEventBossDamageController.RatingToMult(-80), 0.0000001);
        }

        [TestMethod]
        public void MultToRating_IsTheExactInverse()
        {
            foreach (var rating in new[] { -80, -50, -20, -5, 0, 5, 37, 100, 300 })
            {
                var mult = WorldEventBossDamageController.RatingToMult(rating);

                Assert.AreEqual(rating, WorldEventBossDamageController.MultToRating(mult),
                    $"rating {rating} did not survive the round trip");
            }

            Assert.AreEqual(0, WorldEventBossDamageController.MultToRating(double.NaN));
            Assert.AreEqual(0, WorldEventBossDamageController.MultToRating(0));
            Assert.AreEqual(0, WorldEventBossDamageController.MultToRating(-1));
        }

        // ---- the sample gate ---------------------------------------------------------------------------

        [TestMethod]
        public void TryAdvise_NeedsSampleHitsNonCritHitsBeforeItWillDecide()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 3, 500);

            Assert.IsFalse(c.TryAdvise(0, 3000, Shipped, out var rating, out var diag),
                "three hits is one short of the shipped sample of four");
            Assert.AreEqual(0, rating, "a refusal must hand back the rating it was given");
            Assert.AreEqual(3, diag.Hits);

            NoteHits(c, 1, 500);

            Assert.IsTrue(c.TryAdvise(0, 3000, Shipped, out rating, out diag), "the fourth hit completes the window");
            Assert.AreEqual(4, diag.Hits);
        }

        [TestMethod]
        public void TryAdvise_CritsDoNotCountTowardTheSampleGate()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 2, 500);
            NoteHits(c, 10, 5000, crit: true);

            Assert.IsFalse(c.TryAdvise(0, 3000, Shipped, out _, out var diag),
                "ten crits are not a sample; the window still holds two non-crit hits");
            Assert.AreEqual(2, diag.Hits);
            Assert.AreEqual(10, diag.Crits);
        }

        [TestMethod]
        public void TryAdvise_RefusesWhenTheTopMaxHealthIsUnknown()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 4, 500);

            Assert.IsFalse(c.TryAdvise(0, 0, Shipped, out var rating, out _));
            Assert.AreEqual(0, rating);
            Assert.AreEqual(4, c.PendingHits, "and the window is kept, because nothing was decided");
        }

        // ---- the decision ------------------------------------------------------------------------------

        [TestMethod]
        public void TryAdvise_RaisesTheRatingWhenTheBossHitsTooSoftly()
        {
            var c = new WorldEventBossDamageController();

            // top max health 3000 / hitsToKill 3 = a target of 1000 per hit; the boss is doing 500.
            NoteHits(c, 4, 500);

            Assert.IsTrue(c.TryAdvise(0, 3000, Shipped, out var rating, out var diag));

            Assert.AreEqual(1000.0, diag.TargetHit, 0.0000001);
            Assert.AreEqual(500.0, diag.ObservedMean, 0.0000001);
            Assert.IsTrue(diag.FirstAdjustment);

            // wanted multiplier 1.0 * 1000 / 500 = 2.0 -> rating 100.
            Assert.AreEqual(100, rating);
        }

        [TestMethod]
        public void TryAdvise_LowersTheRatingBelowZeroWhenTheBossHitsTooHard()
        {
            var c = new WorldEventBossDamageController();

            // target 1000, observed 1200 -> wanted 0.8333x -> 100 - 100/0.8333 = -20.
            NoteHits(c, 4, 1200);

            Assert.IsTrue(c.TryAdvise(0, 3000, Shipped, out var rating, out _));
            Assert.AreEqual(-20, rating);

            // and the rating really does mean that multiplier, in the engine's own terms.
            Assert.AreEqual(1000.0 / 1200.0, WorldEventBossDamageController.RatingToMult(rating), 0.005);
        }

        [TestMethod]
        public void TryAdvise_DecidesFromTheCurrentRating_NotFromZero()
        {
            var c = new WorldEventBossDamageController();

            // The boss is already at rating 100 (2.0x) and doing 500 against a target of 1000. The rating
            // that produces twice as much damage is not 100, it is 300 - the correction is a multiplier on
            // the CURRENT multiplier, never on 1.0.
            NoteHits(c, 4, 500);

            Assert.IsTrue(c.TryAdvise(100, 3000, Shipped, out var rating, out _));
            Assert.AreEqual(300, rating);
        }

        [TestMethod]
        public void TryAdvise_DeadBand_RefusesAChangeSmallerThanFivePoints()
        {
            var c = new WorldEventBossDamageController();

            // observed 980 against a target of 1000 -> wanted 1.0204x -> rating 2, which is inside the band.
            NoteHits(c, 4, 980);

            Assert.IsFalse(c.TryAdvise(0, 3000, Shipped, out var rating, out var diag));
            Assert.AreEqual(0, rating);
            Assert.AreEqual(980.0, diag.ObservedMean, 0.0000001, "the diagnostics are still filled in on a refusal");

            // exactly on target is the same refusal.
            var onTarget = new WorldEventBossDamageController();
            NoteHits(onTarget, 4, 1000);

            Assert.IsFalse(onTarget.TryAdvise(0, 3000, Shipped, out _, out _));
        }

        // ---- the step cap ------------------------------------------------------------------------------

        [TestMethod]
        public void TryAdvise_FirstAdjustmentIsExemptFromTheStepCap()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 4, 500);

            Assert.IsTrue(c.TryAdvise(0, 3000, Shipped, out var rating, out var diag));
            Assert.IsTrue(diag.FirstAdjustment);
            Assert.AreEqual(100, rating, "a 100-point first move, well past the 50-point step cap");
        }

        [TestMethod]
        public void TryAdvise_LaterAdjustmentsAreClampedToTheStepCap()
        {
            var c = new WorldEventBossDamageController();

            // Spend the exemption on the first advice.
            NoteHits(c, 4, 500);
            Assert.IsTrue(c.TryAdvise(0, 3000, Shipped, out var first, out _));
            Assert.AreEqual(100, first);

            // Second window, same evidence, now from rating 100: the raw proposal is 300 (see
            // DecidesFromTheCurrentRating above), which is a 200-point move and must be cut to 50.
            NoteHits(c, 4, 500);

            Assert.IsTrue(c.TryAdvise(100, 3000, Shipped, out var second, out var diag));
            Assert.IsFalse(diag.FirstAdjustment);
            Assert.AreEqual(150, second, "100 + the 50-point step cap");
        }

        [TestMethod]
        public void TryAdvise_TheStepCapClampsDownwardMovesToo()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 4, 500);
            Assert.IsTrue(c.TryAdvise(0, 3000, Shipped, out _, out _));

            // Now the boss is hitting far too hard: target 1000, observed 4000 from rating 100 ->
            // wanted 2.0 * 0.25 = 0.5x -> rating -100, a 200-point drop, cut to 50.
            NoteHits(c, 4, 4000);

            Assert.IsTrue(c.TryAdvise(100, 3000, Shipped, out var rating, out _));
            Assert.AreEqual(50, rating, "100 - the 50-point step cap");
        }

        // ---- the bounds --------------------------------------------------------------------------------

        [TestMethod]
        public void TryAdvise_ClampsToTheRatingMaximum()
        {
            var c = new WorldEventBossDamageController();

            // target 10000 against an observed 500 -> wanted 20x -> rating 1900, far past the 300 ceiling.
            NoteHits(c, 4, 500);

            Assert.IsTrue(c.TryAdvise(0, 30000, Shipped, out var rating, out _));
            Assert.AreEqual(Shipped.RatingMax, rating);
        }

        [TestMethod]
        public void TryAdvise_ClampsToTheRatingMinimum()
        {
            var c = new WorldEventBossDamageController();

            // target 1000 against an observed 5000 -> wanted 0.2x -> rating -400, past the -80 floor.
            NoteHits(c, 4, 5000);

            Assert.IsTrue(c.TryAdvise(0, 3000, Shipped, out var rating, out _));
            Assert.AreEqual(Shipped.RatingMin, rating);
        }

        [TestMethod]
        public void TryAdvise_AnInvertedBoundPairIsRepairedRatherThanThrowing()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 4, 500);

            var inverted = new BossDamageDials(3.0, 200, 10, 4, 500);

            Assert.IsTrue(c.TryAdvise(0, 3000, inverted, out var rating, out _));
            Assert.AreEqual(200, rating, "a max below the min reads as max == min, never as an empty range");
        }

        // ---- window lifecycle --------------------------------------------------------------------------

        [TestMethod]
        public void TryAdvise_ResetsTheWindowAfterAnAdvice()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 4, 500);
            NoteHits(c, 3, 5000, crit: true);

            Assert.IsTrue(c.TryAdvise(0, 3000, Shipped, out _, out _));

            Assert.AreEqual(0, c.PendingHits, "the window is spent");
            Assert.AreEqual(0, c.PendingCrits, "and so is the crit tally that went with it");
            Assert.AreEqual(0.0, c.PendingMean, 0.0000001);
            Assert.AreEqual(7, c.TotalHits, "the lifetime count is never reset");

            Assert.IsFalse(c.TryAdvise(100, 3000, Shipped, out _, out _),
                "and the next decision has to wait for a fresh window");
        }

        [TestMethod]
        public void TryAdvise_DeadBandLeavesTheWindowRolling()
        {
            var c = new WorldEventBossDamageController();

            NoteHits(c, 4, 1000);

            Assert.IsFalse(c.TryAdvise(0, 3000, Shipped, out _, out _), "on target - nothing to do");
            Assert.AreEqual(4, c.PendingHits, "a refusal is not an adjustment, so the samples are kept");

            Assert.IsFalse(c.HasAdvised, "and the first-adjustment exemption is NOT spent by a refusal");
        }

        // ---- degenerate dials --------------------------------------------------------------------------

        [TestMethod]
        public void TryAdvise_ANonPositiveOrNonFiniteHitsToKillFallsBackToTheBuiltInDefault()
        {
            foreach (var bad in new[] { 0.0, -3.0, double.NaN, double.PositiveInfinity })
            {
                var c = new WorldEventBossDamageController();

                NoteHits(c, 4, 500);

                var dials = new BossDamageDials(bad, -80, 300, 4, 50);

                Assert.IsTrue(c.TryAdvise(0, 3000, dials, out var rating, out var diag), $"hitsToKill {bad}");
                Assert.AreEqual(1000.0, diag.TargetHit, 0.0000001, $"hitsToKill {bad} must read as the default 3.0");
                Assert.AreEqual(100, rating);
            }
        }

        [TestMethod]
        public void TryAdvise_ANonPositiveSampleHitsOrStepCapFallsBackToTheBuiltInDefault()
        {
            var c = new WorldEventBossDamageController();

            var zeroed = new BossDamageDials(3.0, -80, 300, 0, 0);

            NoteHits(c, 3, 500);
            Assert.IsFalse(c.TryAdvise(0, 3000, zeroed, out _, out _), "sampleHits 0 reads as the default 4, not as 'no gate'");

            NoteHits(c, 1, 500);
            Assert.IsTrue(c.TryAdvise(0, 3000, zeroed, out var first, out _));
            Assert.AreEqual(100, first);

            NoteHits(c, 4, 500);
            Assert.IsTrue(c.TryAdvise(100, 3000, zeroed, out var second, out _));
            Assert.AreEqual(150, second, "stepCap 0 reads as the default 50, not as 'no movement allowed'");
        }

        // ---- threading ---------------------------------------------------------------------------------

        [TestMethod]
        public void NoteHit_IsSafeFromManyThreadsAtOnce()
        {
            // Hits arrive from landblock-group threads while the decision runs on the world thread; the
            // controller takes its own monitor rather than relying on that ordering.
            var c = new WorldEventBossDamageController();

            const int workers = 8;
            const int perWorker = 1000;

            var tasks = new Task[workers];

            for (var w = 0; w < workers; w++)
            {
                var crit = w % 2 == 0;

                tasks[w] = Task.Run(() =>
                {
                    for (var i = 0; i < perWorker; i++)
                        c.NoteHit(500, 3000, crit);
                });
            }

            Task.WaitAll(tasks);

            Assert.AreEqual(workers * perWorker, c.TotalHits, "every hit is counted exactly once");
            Assert.AreEqual(workers / 2 * perWorker, c.PendingCrits);
            Assert.AreEqual(WorldEventBossDamageController.MaxTrackedHits, c.PendingHits,
                "the non-crit window fills to capacity and stops there");
            Assert.AreEqual(500.0, c.PendingMean, 0.0000001, "and every buffered entry is a whole, untorn sample");
        }

        [TestMethod]
        public void PendingSnapshot_HitsAndMeanAlwaysCorrespond_UnderConcurrentHitsAndAdvices()
        {
            // The tear this covers: "/worldevent status" used to read PendingHits and PendingMean as two
            // separately-locked properties, so a TryAdvise landing between them could produce a readout
            // saying the boss had landed four hits for an average of nothing. Every hit fed here is worth
            // exactly the same, so a snapshot is self-consistent if and only if a non-zero hit count comes
            // with a mean of exactly that value, and a zero hit count comes with a mean of exactly 0.
            const int damage = 750;

            var c = new WorldEventBossDamageController();

            var stop = 0;
            var torn = 0;
            var seenWithHits = 0;
            var advices = 0;

            // LongRunning gets these their own threads rather than a pool slot. A two-core CI agent starts
            // the pool with two threads and injects more only after a delay, so a pooled writer and adviser
            // could still be queued when a pooled reader had already burned through a fixed iteration count -
            // which is how this test used to fail its own liveness asserts on CI while passing everywhere else.
            var writer = Task.Factory.StartNew(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                    c.NoteHit(damage, 3000, false);
            }, TaskCreationOptions.LongRunning);

            // A second writer that also RESETS the window, which is the state change a two-read caller
            // could otherwise catch halfway through.
            var adviser = Task.Factory.StartNew(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    if (c.TryAdvise(0, 3000, Shipped, out _, out _))
                        Interlocked.Increment(ref advices);
                }
            }, TaskCreationOptions.LongRunning);

            // The reader keeps going until it has actually WITNESSED both states it is here to check - a
            // filled window and a reset one - rather than stopping at a fixed count and asserting it got
            // lucky. The iteration floor keeps the tear check's volume; the deadline is the failure path.
            const int minReads = 200000;
            var deadline = DateTime.UtcNow.AddSeconds(30);

            var reader = Task.Factory.StartNew(() =>
            {
                for (var i = 0; ; i++)
                {
                    var pending = c.PendingSnapshot();

                    var expected = pending.Hits > 0 ? damage : 0.0;

                    if (Math.Abs(pending.Mean - expected) > 0.0000001)
                        torn++;

                    if (pending.Hits > 0)
                        seenWithHits++;

                    if (i < minReads)
                        continue;

                    if (seenWithHits > 0 && Volatile.Read(ref advices) > 0)
                        break;

                    if (DateTime.UtcNow > deadline)
                        break;
                }
            }, TaskCreationOptions.LongRunning);

            reader.Wait();
            Volatile.Write(ref stop, 1);
            Task.WaitAll(writer, adviser);

            Assert.AreEqual(0, torn, "a snapshot's hit count and mean must always describe the same window");
            Assert.IsTrue(seenWithHits > 0, "the reader never observed a filled window, so it proved nothing");
            Assert.IsTrue(Volatile.Read(ref advices) > 0, "the adviser never reset the window, so it proved nothing");

            // The empty half of the invariant, asserted deterministically rather than relied on the reader
            // happening to catch a window mid-reset: a spent window snapshots as three zeroes together.
            var settled = new WorldEventBossDamageController();

            NoteHits(settled, 4, damage);
            NoteHits(settled, 2, damage * 4, crit: true);

            Assert.IsTrue(settled.TryAdvise(0, 3000, Shipped, out _, out _));

            var after = settled.PendingSnapshot();

            Assert.AreEqual(0, after.Hits);
            Assert.AreEqual(0, after.Crits);
            Assert.AreEqual(0.0, after.Mean, 0.0000001, "no hits must never report a mean");
        }

        // ---- the queue-readiness gate ------------------------------------------------------------------

        [TestMethod]
        public void BossDamageWriteReady_RefusesADeadBossAndOneWithNoLandblock()
        {
            Assert.IsTrue(WorldEventSpawner.BossDamageWriteReady(deadOrDestroyed: false, hasLandblock: true));
            Assert.IsFalse(WorldEventSpawner.BossDamageWriteReady(deadOrDestroyed: true, hasLandblock: true));
            Assert.IsFalse(WorldEventSpawner.BossDamageWriteReady(deadOrDestroyed: false, hasLandblock: false),
                "no landblock means SKIP, never 'write it inline'");
            Assert.IsFalse(WorldEventSpawner.BossDamageWriteReady(deadOrDestroyed: true, hasLandblock: false));
        }

        // ---- the dial seam -----------------------------------------------------------------------------

        [TestMethod]
        public void ReadBossDamageDials_FallsBackToTheBuiltInDefaultsWhenAPropertyReadThrows()
        {
            var originalDouble = WorldEvent.BossDamageDoubleReader;
            var originalLong = WorldEvent.BossDamageLongReader;

            try
            {
                WorldEvent.BossDamageDoubleReader = (key, fallback) => throw new InvalidOperationException("no shard config");
                WorldEvent.BossDamageLongReader = (key, fallback) => throw new InvalidOperationException("no shard config");

                var dials = WorldEvent.ReadBossDamageDials();

                Assert.AreEqual(BossDamageDials.Defaults.HitsToKill, dials.HitsToKill, 0.0000001);
                Assert.AreEqual(BossDamageDials.Defaults.RatingMin, dials.RatingMin);
                Assert.AreEqual(BossDamageDials.Defaults.RatingMax, dials.RatingMax);
                Assert.AreEqual(BossDamageDials.Defaults.SampleHits, dials.SampleHits);
                Assert.AreEqual(BossDamageDials.Defaults.StepCap, dials.StepCap);
            }
            finally
            {
                WorldEvent.BossDamageDoubleReader = originalDouble;
                WorldEvent.BossDamageLongReader = originalLong;
            }
        }

        [TestMethod]
        public void ReadBossDamageDials_ReadsEachDialFromItsOwnProperty()
        {
            var originalDouble = WorldEvent.BossDamageDoubleReader;
            var originalLong = WorldEvent.BossDamageLongReader;

            try
            {
                WorldEvent.BossDamageDoubleReader = (key, fallback) => key == "world_events_boss_hits_to_kill" ? 5.0 : fallback;
                WorldEvent.BossDamageLongReader = (key, fallback) =>
                {
                    switch (key)
                    {
                        case "world_events_boss_damage_rating_min": return -10;
                        case "world_events_boss_damage_rating_max": return 111;
                        case "world_events_boss_damage_sample_hits": return 7;
                        case "world_events_boss_damage_step_cap": return 22;
                        default: return fallback;
                    }
                };

                var dials = WorldEvent.ReadBossDamageDials();

                Assert.AreEqual(5.0, dials.HitsToKill, 0.0000001);
                Assert.AreEqual(-10, dials.RatingMin);
                Assert.AreEqual(111, dials.RatingMax);
                Assert.AreEqual(7, dials.SampleHits);
                Assert.AreEqual(22, dials.StepCap);
            }
            finally
            {
                WorldEvent.BossDamageDoubleReader = originalDouble;
                WorldEvent.BossDamageLongReader = originalLong;
            }
        }

        [TestMethod]
        public void BossDamageDialSource_DefaultsToTheRealReader_AndNeverThrows()
        {
            // With no test override the seam reads PropertyManager (or, absent shard config, falls back to
            // the built-in defaults) - either way it must produce a usable dial set rather than throwing.
            var dials = WorldEvent.BossDamageDialSource();

            Assert.IsTrue(dials.HitsToKill > 0);
            Assert.IsTrue(dials.SampleHits > 0);
            Assert.IsTrue(dials.RatingMax >= dials.RatingMin);
        }
    }
}
