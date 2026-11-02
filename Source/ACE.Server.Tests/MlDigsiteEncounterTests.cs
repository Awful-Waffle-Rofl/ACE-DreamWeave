using System;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// MlDigsiteEncounter's latches and bookkeeping: the end-request latch the whole end pipeline hangs on,
    /// the per-wave checkpoint latch, the one-roll Kept Siraluun latch, the boss-health minimum, and the
    /// presence and damage-credit records group rewards read.
    ///
    /// Order-independent: every encounter is built fresh per test, and the only PropertyManager state touched
    /// is the code defaults loaded once below (creature construction reads them), which no test modifies.
    /// </summary>
    [TestClass]
    public class MlDigsiteEncounterTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static MlDigsiteEncounter NewEncounter(MlDigsiteType type = MlDigsiteType.WavesAndMiniBoss)
            => new MlDigsiteEncounter(1, 0x50000001, "Digger", type, new Position(), DateTime.UtcNow);

        // ---- the end-request latch -----------------------------------------------------------------------

        [TestMethod]
        public void TryRequestEnd_the_first_request_wins()
        {
            var encounter = NewEncounter();

            Assert.IsFalse(encounter.EndRequested);
            Assert.IsFalse(encounter.TryGetEndRequest(out _, out _), "nothing requested yet");

            Assert.IsTrue(encounter.TryRequestEnd(MlDigsiteRules.EndReasons.Wiped, MlDigsiteResult.Scored));
            Assert.IsFalse(encounter.TryRequestEnd(MlDigsiteRules.EndReasons.Expired, MlDigsiteResult.Failed),
                "a second request must be refused");

            Assert.IsTrue(encounter.EndRequested);
            Assert.IsTrue(encounter.TryGetEndRequest(out var reason, out var result));
            Assert.AreEqual(MlDigsiteRules.EndReasons.Wiped, reason);
            Assert.AreEqual(MlDigsiteResult.Scored, result);

            // a request does not END anything by itself
            Assert.AreEqual(MlDigsiteState.Active, encounter.State);
        }

        [TestMethod]
        public void TryRequestEnd_a_win_and_a_bail_on_the_same_tick_resolve_to_whichever_arrived_first()
        {
            var winFirst = NewEncounter(MlDigsiteType.BossRush);

            Assert.IsTrue(winFirst.TryRequestEnd(MlDigsiteRules.EndReasons.ObjectiveKilled, MlDigsiteResult.FullClear));
            Assert.IsFalse(winFirst.TryRequestEnd(MlDigsiteRules.EndReasons.Bailed, MlDigsiteResult.Scored));

            Assert.IsTrue(winFirst.TryGetEndRequest(out var winReason, out var winResult));
            Assert.AreEqual(MlDigsiteRules.EndReasons.ObjectiveKilled, winReason);
            Assert.AreEqual(MlDigsiteResult.FullClear, winResult);

            var bailFirst = NewEncounter(MlDigsiteType.BossRush);

            Assert.IsTrue(bailFirst.TryRequestEnd(MlDigsiteRules.EndReasons.Bailed, MlDigsiteResult.Scored));
            Assert.IsFalse(bailFirst.TryRequestEnd(MlDigsiteRules.EndReasons.ObjectiveKilled, MlDigsiteResult.FullClear));

            Assert.IsTrue(bailFirst.TryGetEndRequest(out var bailReason, out var bailResult));
            Assert.AreEqual(MlDigsiteRules.EndReasons.Bailed, bailReason);
            Assert.AreEqual(MlDigsiteResult.Scored, bailResult);
        }

        [TestMethod]
        public void TryRequestEnd_is_ignored_once_the_encounter_has_ended()
        {
            var encounter = NewEncounter();

            Assert.IsTrue(encounter.MarkEnded(MlDigsiteRules.EndReasons.Expired, out _));

            Assert.IsFalse(encounter.TryRequestEnd(MlDigsiteRules.EndReasons.Bailed, MlDigsiteResult.Scored));
            Assert.IsFalse(encounter.EndRequested);
            Assert.IsFalse(encounter.TryGetEndRequest(out _, out _));
        }

        [TestMethod]
        public void TryGetEndRequest_stops_reporting_a_request_once_Finish_has_ended_the_encounter()
        {
            var encounter = NewEncounter();

            Assert.IsTrue(encounter.TryRequestEnd(MlDigsiteRules.EndReasons.Stalled, MlDigsiteResult.Scored));
            Assert.IsTrue(encounter.TryGetEndRequest(out _, out _));

            // Finish takes the one-shot latch; the same request read again cannot end it twice
            Assert.IsTrue(encounter.MarkEnded(MlDigsiteRules.EndReasons.Stalled, out _));
            Assert.IsFalse(encounter.MarkEnded(MlDigsiteRules.EndReasons.Stalled, out _));
            Assert.IsFalse(encounter.TryGetEndRequest(out _, out _));
        }

        // ---- the feather ordering ------------------------------------------------------------------------

        private static PropertiesCreateList Row(uint wcid) => new PropertiesCreateList
        {
            WeenieClassId = wcid,
            DestinationType = DestinationType.Treasure,
            Shade = 1.0f,
            StackSize = 1,
        };

        [TestMethod]
        public void NoteCreatureDeath_records_a_drop_atomically_with_the_death()
        {
            var encounter = NewEncounter();
            var creature = TestCreatures.CreateEffectCarrier();

            encounter.TrackWaveCreature(creature);

            var kind = encounter.NoteCreatureDeath(creature.Guid.Full, DateTime.UtcNow, creature, new[] { Row(29898) }, out var waveNowEmpty);

            Assert.AreEqual(MlDigsiteDeathKind.Wave, kind);
            Assert.IsTrue(waveNowEmpty);
            CollectionAssert.AreEqual(new uint[] { 29898 }, encounter.KeptSiraluunDropsSnapshot().Select(r => r.WeenieClassId).ToArray());
        }

        [TestMethod]
        public void NoteCreatureDeath_records_nothing_once_the_encounter_has_ended()
        {
            // THE race the #1213 review found: a Kept Siraluun dying as the encounter ends. Deliver reads the
            // drops only after MarkEnded; once MarkEnded has run, a death records nothing at all, so the list
            // Deliver reads is final. Before MarkEnded, the drop is in the list (the test above).
            var encounter = NewEncounter();
            var before = TestCreatures.CreateEffectCarrier();
            var after = TestCreatures.CreateEffectCarrier();

            encounter.TrackWaveCreature(before);
            encounter.TrackWaveCreature(after);

            Assert.AreEqual(MlDigsiteDeathKind.Wave,
                encounter.NoteCreatureDeath(before.Guid.Full, DateTime.UtcNow, before, new[] { Row(29898) }, out _));

            Assert.IsTrue(encounter.MarkEnded(MlDigsiteRules.EndReasons.Bailed, out _));

            var snapshotAtDeliver = encounter.KeptSiraluunDropsSnapshot();

            Assert.AreEqual(MlDigsiteDeathKind.None,
                encounter.NoteCreatureDeath(after.Guid.Full, DateTime.UtcNow, after, new[] { Row(29899) }, out _));

            CollectionAssert.AreEqual(new uint[] { 29898 }, snapshotAtDeliver.Select(r => r.WeenieClassId).ToArray());
            CollectionAssert.AreEqual(new uint[] { 29898 }, encounter.KeptSiraluunDropsSnapshot().Select(r => r.WeenieClassId).ToArray(),
                "a death after MarkEnded must not add to the list Deliver already read");
        }

        [TestMethod]
        public void NoteCreatureDeath_is_inert_for_a_creature_the_encounter_never_tracked()
        {
            var encounter = NewEncounter();
            var stranger = TestCreatures.CreateEffectCarrier();

            Assert.AreEqual(MlDigsiteDeathKind.None,
                encounter.NoteCreatureDeath(stranger.Guid.Full, DateTime.UtcNow, stranger, new[] { Row(29898) }, out _));

            Assert.AreEqual(0, encounter.KeptSiraluunDropsSnapshot().Count, "an untracked death must record no drop");
        }

        [TestMethod]
        public void A_boss_rush_objective_death_marks_the_boss_killed_for_the_payout()
        {
            var encounter = NewEncounter(MlDigsiteType.BossRush);
            var boss = TestCreatures.CreateEffectCarrier();

            encounter.TrackObjective(boss);

            Assert.AreEqual(MlDigsiteDeathKind.Objective,
                encounter.NoteCreatureDeath(boss.Guid.Full, DateTime.UtcNow, boss, null, out _));

            Assert.IsTrue(encounter.PayoutSnapshot(MlDigsiteResult.FullClear).BossKilled);
        }

        // ---- round 16: Corrupted mob kill count ----------------------------------------------------------

        [TestMethod]
        public void A_corrupted_mob_death_increments_the_kill_count_and_re_arms_the_objective()
        {
            var encounter = NewEncounter(MlDigsiteType.CorruptionMeter);
            var firstCorrupted = TestCreatures.CreateEffectCarrier();
            var secondCorrupted = TestCreatures.CreateEffectCarrier();

            encounter.TrackObjective(firstCorrupted);
            Assert.AreEqual(0, encounter.CorruptedKills);
            Assert.IsTrue(encounter.ObjectiveAlive);

            Assert.AreEqual(MlDigsiteDeathKind.Objective,
                encounter.NoteCreatureDeath(firstCorrupted.Guid.Full, DateTime.UtcNow, firstCorrupted, null, out _));

            Assert.AreEqual(1, encounter.CorruptedKills, "the first Corrupted mob's death must count toward the total");
            Assert.IsFalse(encounter.ObjectiveAlive, "no next Corrupted mob is tracked until the manager places one");

            // the manager re-arms the objective for the next Corrupted mob (MlDigsiteSpawner.TrySpawn ->
            // TrackObjective); TrackObjective is re-callable for exactly this reason.
            encounter.TrackObjective(secondCorrupted);
            Assert.IsTrue(encounter.ObjectiveAlive);

            Assert.AreEqual(MlDigsiteDeathKind.Objective,
                encounter.NoteCreatureDeath(secondCorrupted.Guid.Full, DateTime.UtcNow, secondCorrupted, null, out _));

            Assert.AreEqual(2, encounter.CorruptedKills);
        }

        [TestMethod]
        public void A_boss_rush_objective_death_never_touches_the_corrupted_kill_count()
        {
            // The corruptedKills increment inside NoteCreatureDeath is gated on Type == CorruptionMeter, so a
            // Boss Rush kill (the same MlDigsiteDeathKind.Objective) must not bump it.
            var encounter = NewEncounter(MlDigsiteType.BossRush);
            var boss = TestCreatures.CreateEffectCarrier();

            encounter.TrackObjective(boss);
            encounter.NoteCreatureDeath(boss.Guid.Full, DateTime.UtcNow, boss, null, out _);

            Assert.AreEqual(0, encounter.CorruptedKills);
        }

        [TestMethod]
        public void TryClaimFieldSpawnTick_first_call_only_arms_the_clock()
        {
            var encounter = NewEncounter(MlDigsiteType.CorruptionMeter);
            var now = DateTime.UtcNow;
            var interval = TimeSpan.FromSeconds(20);

            Assert.IsFalse(encounter.TryClaimFieldSpawnTick(now, interval), "the first call must only arm the clock");
            Assert.IsFalse(encounter.TryClaimFieldSpawnTick(now + TimeSpan.FromSeconds(5), interval), "not due yet");
            Assert.IsTrue(encounter.TryClaimFieldSpawnTick(now + interval, interval), "due at the interval");
            Assert.IsFalse(encounter.TryClaimFieldSpawnTick(now + interval + TimeSpan.FromSeconds(5), interval), "re-armed for the next interval");
            Assert.IsTrue(encounter.TryClaimFieldSpawnTick(now + interval + interval, interval));
        }

        // ---- round 16 code review: the Corrupted-mob respawn retry (softlock fix) -----------------------
        //
        // Real bug: MlDigsiteManager.OnCorruptedDied (and the opening spawn in OpenEncounter) called
        // MlDigsiteSpawner.TrySpawn once; on a null return (a roster miss, a scatter-point/terrain snap
        // failure, EnterWorld failing, or the held list closed under a race with Finish) it only logged a
        // warning, leaving the encounter with NO live objective and NO win path - it would idle to its TTL.
        // The fix is this pending-respawn flag plus MlDigsiteRules.ShouldRetryCorruptedSpawn, driven every
        // 1s tick by MlDigsiteManager.DriveCorruption's TryRespawnPendingCorrupted. These tests exercise the
        // state machine directly (MlDigsiteSpawner.TrySpawn needs a live world the test harness cannot build),
        // which is exactly what the state machine is FOR: it makes the retry logic testable without one.

        [TestMethod]
        public void MarkCorruptedSpawnPending_a_failed_spawn_is_retried_on_a_later_tick_until_it_lands()
        {
            var encounter = NewEncounter(MlDigsiteType.CorruptionMeter);

            // Tick 1: the spawn attempt failed (whatever TrySpawn's caller would have done on a null return).
            Assert.IsFalse(encounter.CorruptedSpawnPending, "nothing owed yet");
            encounter.MarkCorruptedSpawnPending();
            Assert.IsTrue(encounter.CorruptedSpawnPending);

            // The driver's gate says retry, because one is owed and none is alive.
            Assert.IsTrue(MlDigsiteRules.ShouldRetryCorruptedSpawn(encounter.CorruptedSpawnPending, encounter.ObjectiveAlive),
                "tick 1's retry gate must be open");

            // Tick 2: the retry attempt ALSO fails. Idempotent - marking it pending again changes nothing.
            encounter.MarkCorruptedSpawnPending();
            Assert.IsTrue(MlDigsiteRules.ShouldRetryCorruptedSpawn(encounter.CorruptedSpawnPending, encounter.ObjectiveAlive),
                "tick 2 must still retry - the debt is not cleared by a repeated failure");

            // Tick 3: the retry SUCCEEDS - the manager's real call is MlDigsiteSpawner.TrySpawn, which tracks
            // the new creature via TrackObjective; that is the one and only place the debt is cleared.
            var corrupted = TestCreatures.CreateEffectCarrier();
            encounter.TrackObjective(corrupted);

            Assert.IsFalse(encounter.CorruptedSpawnPending, "a landed spawn must clear the debt");
            Assert.IsTrue(encounter.ObjectiveAlive);
            Assert.IsFalse(MlDigsiteRules.ShouldRetryCorruptedSpawn(encounter.CorruptedSpawnPending, encounter.ObjectiveAlive),
                "no more retries once the debt is cleared");
        }

        [TestMethod]
        public void ShouldRetryCorruptedSpawn_never_fires_while_a_Corrupted_mob_is_already_alive()
        {
            // Guards against a pending flag surviving alongside a live objective (e.g. a late-arriving retry
            // success racing a fresh MarkCorruptedSpawnPending from another failed attempt): the encounter
            // must never place a second Corrupted mob next to a live one.
            var encounter = NewEncounter(MlDigsiteType.CorruptionMeter);
            var corrupted = TestCreatures.CreateEffectCarrier();

            encounter.TrackObjective(corrupted);
            encounter.MarkCorruptedSpawnPending();

            Assert.IsTrue(encounter.CorruptedSpawnPending);
            Assert.IsTrue(encounter.ObjectiveAlive);
            Assert.IsFalse(MlDigsiteRules.ShouldRetryCorruptedSpawn(encounter.CorruptedSpawnPending, encounter.ObjectiveAlive),
                "a live Corrupted mob must block the retry even if pending was (wrongly) set");
        }

        [TestMethod]
        public void A_checkpoint_death_counts_a_checkpoint_kill_and_never_holds_the_wave_open()
        {
            var encounter = NewEncounter();
            var trash = TestCreatures.CreateEffectCarrier();
            var checkpoint = TestCreatures.CreateEffectCarrier();

            encounter.TrackWaveCreature(trash);
            encounter.TrackCheckpoint(checkpoint);

            Assert.IsTrue(encounter.CheckpointAlive);

            // the wave empties on its trash alone, with the checkpoint still standing
            Assert.AreEqual(MlDigsiteDeathKind.Wave,
                encounter.NoteCreatureDeath(trash.Guid.Full, DateTime.UtcNow, trash, null, out var waveNowEmpty));
            Assert.IsTrue(waveNowEmpty, "the checkpoint mini-boss must not hold the wave open");
            Assert.IsTrue(encounter.CheckpointAlive);

            Assert.AreEqual(MlDigsiteDeathKind.Checkpoint,
                encounter.NoteCreatureDeath(checkpoint.Guid.Full, DateTime.UtcNow, checkpoint, null, out _));
            Assert.IsFalse(encounter.CheckpointAlive);
            Assert.AreEqual(1, encounter.CheckpointKills);
            Assert.IsFalse(encounter.PayoutSnapshot(MlDigsiteResult.Scored).BossKilled, "a checkpoint is not a win");
        }

        // ---- checkpoints and the Kept Siraluun roll ------------------------------------------------------

        [TestMethod]
        public void TryClaimCheckpoint_latches_each_wave_once()
        {
            var encounter = NewEncounter();

            Assert.IsTrue(encounter.TryClaimCheckpoint(3));
            Assert.IsFalse(encounter.TryClaimCheckpoint(3));
            Assert.IsTrue(encounter.TryClaimCheckpoint(6), "a later checkpoint wave has its own latch");
            Assert.IsFalse(encounter.TryClaimCheckpoint(6));
        }

        [TestMethod]
        public void TryClaimKeptSiraluunRoll_is_one_roll_per_encounter()
        {
            var encounter = NewEncounter();

            Assert.IsTrue(encounter.TryClaimKeptSiraluunRoll());
            Assert.IsFalse(encounter.TryClaimKeptSiraluunRoll());
            Assert.IsFalse(encounter.TryClaimKeptSiraluunRoll());
        }

        // ---- boss health ---------------------------------------------------------------------------------

        [TestMethod]
        public void NoteBossHealthSample_keeps_the_lowest_sample_and_ignores_broken_ones()
        {
            var encounter = NewEncounter(MlDigsiteType.BossRush);

            Assert.AreEqual(1.0, encounter.BossMinHealthFraction, 1e-9, "never hit reads as full health");

            encounter.NoteBossHealthSample(0.8);
            encounter.NoteBossHealthSample(0.45);
            encounter.NoteBossHealthSample(0.9); // healed back up: the minimum stays
            encounter.NoteBossHealthSample(double.NaN);

            Assert.AreEqual(0.45, encounter.BossMinHealthFraction, 1e-9);

            encounter.NoteBossHealthSample(-2.0);
            Assert.AreEqual(0.0, encounter.BossMinHealthFraction, 1e-9, "a sample below zero clamps to zero");
        }

        // ---- presence and damage credit ------------------------------------------------------------------

        [TestMethod]
        public void MarkPlayersPresent_records_when_each_player_was_last_seen()
        {
            var encounter = NewEncounter();
            var t0 = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsNull(encounter.LastPresentUtc(7));

            encounter.MarkPlayersPresent(new uint[] { 7, 8 }, t0);
            encounter.MarkPlayersPresent(new uint[] { 8 }, t0.AddSeconds(15));

            Assert.AreEqual(t0, encounter.LastPresentUtc(7));
            Assert.AreEqual(t0.AddSeconds(15), encounter.LastPresentUtc(8));
        }

        [TestMethod]
        public void DamageCreditSnapshot_accumulates_per_player()
        {
            var encounter = NewEncounter();

            encounter.CreditDamageForTest(7, 100f);
            encounter.CreditDamageForTest(7, 50f);
            encounter.CreditDamageForTest(8, 10f);
            encounter.CreditDamageForTest(9, 0f); // the ledger never creates a record for zero damage

            var credit = encounter.DamageCreditSnapshot();

            Assert.AreEqual(150f, credit[7]);
            Assert.AreEqual(10f, credit[8]);
            Assert.IsFalse(credit.ContainsKey(9));
        }

        // ---- waves cleared (the instant-bail ruling) ------------------------------------------------------

        [TestMethod]
        public void A_placed_wave_is_not_cleared_until_its_field_empties()
        {
            var encounter = NewEncounter();
            var a = TestCreatures.CreateEffectCarrier();
            var b = TestCreatures.CreateEffectCarrier();

            encounter.NoteWaveSpawned(DateTime.UtcNow);
            encounter.TrackWaveCreature(a);
            encounter.TrackWaveCreature(b);

            Assert.AreEqual(1, encounter.WavesSpawned);
            Assert.AreEqual(0, encounter.HighestWaveCleared, "wave 1 is placed at open, not cleared");

            encounter.NoteCreatureDeath(a.Guid.Full, DateTime.UtcNow, a, null, out _);
            Assert.AreEqual(0, encounter.HighestWaveCleared, "one creature still stands");

            encounter.NoteCreatureDeath(b.Guid.Full, DateTime.UtcNow, b, null, out var empty);
            Assert.IsTrue(empty);
            Assert.AreEqual(1, encounter.HighestWaveCleared);

            var snapshot = encounter.PayoutSnapshot(MlDigsiteResult.Scored);
            Assert.AreEqual(1, snapshot.HighestWave);
            Assert.AreEqual(1, snapshot.HighestWaveCleared);
        }

        [TestMethod]
        public void A_checkpoint_death_never_counts_as_clearing_a_wave()
        {
            var encounter = NewEncounter();
            var trash = TestCreatures.CreateEffectCarrier();
            var checkpoint = TestCreatures.CreateEffectCarrier();

            encounter.NoteWaveSpawned(DateTime.UtcNow);
            encounter.TrackWaveCreature(trash);
            encounter.TrackCheckpoint(checkpoint);

            encounter.NoteCreatureDeath(checkpoint.Guid.Full, DateTime.UtcNow, checkpoint, null, out _);

            Assert.AreEqual(0, encounter.HighestWaveCleared);
        }

        [TestMethod]
        public void PayoutSnapshot_reads_the_bail_from_the_latched_end_request()
        {
            var encounter = NewEncounter();

            Assert.IsFalse(encounter.PayoutSnapshot(MlDigsiteResult.Scored).Bailed);
            Assert.IsTrue(encounter.PayoutSnapshot(MlDigsiteResult.Scored, assumeBailed: true).Bailed, "the bail prompt prices a bail");

            Assert.IsTrue(encounter.TryRequestEnd(MlDigsiteRules.EndReasons.Bailed, MlDigsiteResult.Scored));
            Assert.IsTrue(encounter.PayoutSnapshot(MlDigsiteResult.Scored).Bailed);

            var wiped = NewEncounter();
            Assert.IsTrue(wiped.TryRequestEnd(MlDigsiteRules.EndReasons.Wiped, MlDigsiteResult.Scored));
            Assert.IsFalse(wiped.PayoutSnapshot(MlDigsiteResult.Scored).Bailed, "a wipe is not a bail");
        }

        [TestMethod]
        public void PayoutSnapshot_carries_the_progress_the_payout_reads()
        {
            var encounter = NewEncounter(MlDigsiteType.BossRush);

            encounter.NoteBossHealthSample(0.3);

            var snapshot = encounter.PayoutSnapshot(MlDigsiteResult.Scored);

            Assert.AreEqual(MlDigsiteResult.Scored, snapshot.Result);
            Assert.AreEqual(0.3, snapshot.BossMinHealthFraction, 1e-9);
            Assert.AreEqual(0, snapshot.HighestWave);
            Assert.AreEqual(0, snapshot.CheckpointKills);
            Assert.IsFalse(snapshot.BossKilled);
        }

        // ---- vanished wave creatures (destroyed without dying) ---------------------------------------------

        [TestMethod]
        public void PruneVanishedWaveCreatures_drops_a_destroyed_creature_and_reports_the_emptied_wave_without_crediting_it()
        {
            var encounter = NewEncounter();
            var creature = TestCreatures.CreateEffectCarrier();

            encounter.NoteWaveSpawned(DateTime.UtcNow);
            encounter.TrackWaveCreature(creature);

            Assert.AreEqual(1, encounter.LiveWaveCount);

            creature.Destroy();

            var vanished = encounter.PruneVanishedWaveCreatures(DateTime.UtcNow, out var waveNowEmpty, out _);

            Assert.AreEqual(1, vanished.Count);
            Assert.AreSame(creature, vanished[0]);
            Assert.IsTrue(waveNowEmpty);
            Assert.AreEqual(0, encounter.LiveWaveCount);
            Assert.AreEqual(0, encounter.HighestWaveCleared, "a vanished wave is skipped, never credited as cleared");
        }

        [TestMethod]
        public void PruneVanishedWaveCreatures_leaves_the_wave_open_while_a_live_creature_remains()
        {
            var encounter = NewEncounter();
            var gone = TestCreatures.CreateEffectCarrier();
            var standing = TestCreatures.CreateEffectCarrier();

            encounter.NoteWaveSpawned(DateTime.UtcNow);
            encounter.TrackWaveCreature(gone);
            encounter.TrackWaveCreature(standing);

            gone.Destroy();

            var vanished = encounter.PruneVanishedWaveCreatures(DateTime.UtcNow, out var waveNowEmpty, out _);

            Assert.AreEqual(1, vanished.Count);
            Assert.IsFalse(waveNowEmpty);
            Assert.AreEqual(1, encounter.LiveWaveCount);
            Assert.AreEqual(0, encounter.HighestWaveCleared);
        }

        [TestMethod]
        public void PruneVanishedWaveCreatures_is_a_no_op_for_an_intact_or_ended_encounter()
        {
            var encounter = NewEncounter();
            var creature = TestCreatures.CreateEffectCarrier();

            encounter.NoteWaveSpawned(DateTime.UtcNow);
            encounter.TrackWaveCreature(creature);

            Assert.AreEqual(0, encounter.PruneVanishedWaveCreatures(DateTime.UtcNow, out var emptied, out _).Count);
            Assert.IsFalse(emptied);
            Assert.AreEqual(1, encounter.LiveWaveCount);

            creature.Destroy();
            Assert.IsTrue(encounter.MarkEnded(MlDigsiteRules.EndReasons.Bailed, out _));

            Assert.AreEqual(0, encounter.PruneVanishedWaveCreatures(DateTime.UtcNow, out emptied, out _).Count);
            Assert.IsFalse(emptied);
            Assert.AreEqual(0, encounter.HighestWaveCleared);
        }

        [TestMethod]
        public void PruneVanishedWaveCreatures_leaves_a_destroyed_AND_dead_creature_to_its_death_hook()
        {
            var encounter = NewEncounter();
            var creature = TestCreatures.CreateEffectCarrier();

            encounter.NoteWaveSpawned(DateTime.UtcNow);
            encounter.TrackWaveCreature(creature);

            creature.Health.Current = 0;
            creature.Destroy();

            Assert.IsTrue(creature.IsDead);
            Assert.IsTrue(creature.IsDestroyed);

            Assert.AreEqual(0, encounter.PruneVanishedWaveCreatures(DateTime.UtcNow, out var emptied, out _).Count);
            Assert.IsFalse(emptied);
            Assert.AreEqual(1, encounter.LiveWaveCount, "a dead creature is removed by NoteCreatureDeath, with its credit, not by the prune");
        }

        [TestMethod]
        public void PruneVanishedCheckpoint_clears_a_destroyed_checkpoint_but_not_a_dead_or_intact_one()
        {
            var encounter = NewEncounter();
            var intact = TestCreatures.CreateEffectCarrier();

            encounter.TrackCheckpoint(intact);
            Assert.IsNull(encounter.PruneVanishedCheckpoint());
            Assert.IsTrue(encounter.CheckpointAlive);

            var dead = TestCreatures.CreateEffectCarrier();
            encounter.TrackCheckpoint(dead);
            dead.Health.Current = 0;
            dead.Destroy();
            Assert.IsNull(encounter.PruneVanishedCheckpoint());
            Assert.IsTrue(encounter.CheckpointAlive);

            var gone = TestCreatures.CreateEffectCarrier();
            encounter.TrackCheckpoint(gone);
            gone.Destroy();
            Assert.AreSame(gone, encounter.PruneVanishedCheckpoint());
            Assert.IsFalse(encounter.CheckpointAlive);
            Assert.AreEqual(0, encounter.PayoutSnapshot(MlDigsiteResult.Scored).CheckpointKills, "no kill credit");
        }

        [TestMethod]
        public void TryClaimLastWaveRespawn_is_granted_once_per_run_and_pends_for_one_spawn()
        {
            var encounter = NewEncounter();

            Assert.IsFalse(encounter.LastWaveRespawnUsed);
            Assert.IsFalse(encounter.TakeLastWaveRespawnPending());

            Assert.IsTrue(encounter.TryClaimLastWaveRespawn());
            Assert.IsTrue(encounter.LastWaveRespawnUsed);
            Assert.IsFalse(encounter.TryClaimLastWaveRespawn(), "bounded: a second claim is refused");

            Assert.IsTrue(encounter.TakeLastWaveRespawnPending());
            Assert.IsFalse(encounter.TakeLastWaveRespawnPending(), "read once");
        }

        [TestMethod]
        public void NoteWaveRespawned_restarts_the_clocks_without_advancing_the_wave_count()
        {
            var encounter = NewEncounter();
            var t0 = DateTime.UtcNow;

            encounter.NoteWaveSpawned(t0);
            encounter.ScheduleNextWave(t0.AddSeconds(5));
            encounter.NoteWaveRespawned(t0.AddSeconds(9));

            Assert.AreEqual(1, encounter.WavesSpawned);
            Assert.AreEqual(t0.AddSeconds(9), encounter.WaveStartedUtc);
            Assert.IsNull(encounter.NextWaveDueUtc);
        }

        // ---- half-killed vanish = clear; vanish skip caps the tier ----------------------------------------

        private static MlDigsiteEncounter WaveOf(int count, out System.Collections.Generic.List<ACE.Server.WorldObjects.Creature> creatures)
        {
            var encounter = NewEncounter();

            creatures = new System.Collections.Generic.List<ACE.Server.WorldObjects.Creature>();

            for (var i = 0; i < count; i++)
            {
                var c = TestCreatures.CreateEffectCarrier();
                creatures.Add(c);
                encounter.TrackWaveCreature(c);
            }

            encounter.NoteWaveSpawned(DateTime.UtcNow, count);

            return encounter;
        }

        [TestMethod]
        public void Prune_credits_a_vanish_emptied_wave_when_at_least_half_was_killed()
        {
            var encounter = WaveOf(4, out var wave);

            encounter.NoteCreatureDeath(wave[0].Guid.Full, DateTime.UtcNow, wave[0], null, out _);
            encounter.NoteCreatureDeath(wave[1].Guid.Full, DateTime.UtcNow, wave[1], null, out _);

            wave[2].Destroy();
            wave[3].Destroy();

            encounter.PruneVanishedWaveCreatures(DateTime.UtcNow, out var emptied, out var realClear);

            Assert.IsTrue(emptied);
            Assert.IsTrue(realClear, "2 of 4 killed meets ceil(4 / 2)");
            Assert.AreEqual(1, encounter.HighestWaveCleared);
            Assert.AreEqual(1, encounter.RealClears);
            Assert.AreEqual(0, encounter.VanishSkips);
        }

        [TestMethod]
        public void Prune_skips_a_vanish_emptied_wave_when_under_half_was_killed()
        {
            var encounter = WaveOf(8, out var wave);

            encounter.NoteCreatureDeath(wave[0].Guid.Full, DateTime.UtcNow, wave[0], null, out _);

            for (var i = 1; i < 8; i++)
                wave[i].Destroy();

            encounter.PruneVanishedWaveCreatures(DateTime.UtcNow, out var emptied, out var realClear);

            Assert.IsTrue(emptied);
            Assert.IsFalse(realClear, "1 of 8 killed is under ceil(8 / 2)");
            Assert.AreEqual(0, encounter.HighestWaveCleared);
            Assert.AreEqual(0, encounter.RealClears);
            Assert.AreEqual(1, encounter.VanishSkips);
        }

        [TestMethod]
        public void A_vanish_skip_caps_the_paid_tier_at_the_waves_actually_cleared_by_kills()
        {
            var encounter = WaveOf(1, out var first);

            first[0].Destroy();
            encounter.PruneVanishedWaveCreatures(DateTime.UtcNow, out _, out _);   // wave 1 skipped

            var second = TestCreatures.CreateEffectCarrier();
            encounter.NoteWaveSpawned(DateTime.UtcNow, 1);
            encounter.TrackWaveCreature(second);
            encounter.NoteCreatureDeath(second.Guid.Full, DateTime.UtcNow, second, null, out _);   // wave 2 killed

            Assert.AreEqual(2, encounter.HighestWaveCleared, "the raw record still says wave 2");
            Assert.AreEqual(1, encounter.PayoutSnapshot(MlDigsiteResult.Scored).HighestWaveCleared, "but only one wave was cleared by kills");
        }

        [TestMethod]
        public void Without_a_vanish_skip_the_tier_is_not_capped_so_placed_zero_skips_are_unaffected()
        {
            var encounter = NewEncounter();

            encounter.NoteWaveSpawned(DateTime.UtcNow, 0);   // wave 1: placed nothing, skipped by the manager

            var second = TestCreatures.CreateEffectCarrier();
            encounter.NoteWaveSpawned(DateTime.UtcNow, 1);
            encounter.TrackWaveCreature(second);
            encounter.NoteCreatureDeath(second.Guid.Full, DateTime.UtcNow, second, null, out _);

            Assert.AreEqual(0, encounter.VanishSkips);
            Assert.AreEqual(2, encounter.PayoutSnapshot(MlDigsiteResult.Scored).HighestWaveCleared);
        }
    }
}
