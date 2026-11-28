using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.BalanceTrials;
using ACE.Server.Command.Handlers;
using ACE.Server.ClassAbilities;

using ShardCharacter = ACE.Database.Models.Shard.Character;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// @trial promote and @trial unpromote (TrialPromote), the copy gate (TrialCopyGate) and how rebuild honours it, and the
    /// pure Normalize / consumable helpers. Every world lookup goes through a fake; the gate is a fresh instance per fake with
    /// a fake clock. Reads no PropertyManager key, so there is nothing to seed. The rep block ledger and the trial session
    /// registry are process-static and are reset around every test.
    /// </summary>
    [TestClass]
    public class TrialPromoteTests
    {
        private const string ArchSha = "aaaa000000000000000000000000000000000000000000000000000000000001";
        private const string OtherSha = "bbbb000000000000000000000000000000000000000000000000000000000009";
        private const string CalSha = "cccc000000000000000000000000000000000000000000000000000000000002";

        private const uint CallerGuid = 0x50000123;

        private static readonly DateTime Now = new DateTime(2026, 11, 22, 14, 30, 0, DateTimeKind.Utc);

        private static TrialArchetype Archetype(params TrialConsumable[] consumables)
            => new TrialArchetype("archmage-400", 3, ClassAbilityClass.Archmage, 400, null,
                new TrialLiftUp(null, null, null, null, null, consumables), "ownSpellbook");

        private static readonly uint Master = TrialRoster.MasterSlot(3);

        [TestInitialize]
        public void Init()
        {
            TrialRepSetup.ResetAllBlocksForTests();
            TrialManager.ResetForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            TrialRepSetup.ResetAllBlocksForTests();
            TrialManager.ResetForTests();
        }

        // ================================================================ fake

        private sealed class Fake
        {
            public double Clock;
            public TrialCopyGate Gate;
            public HashSet<uint> Online = new HashSet<uint>();
            public HashSet<uint> TrialSessions = new HashSet<uint>();
            public HashSet<int> SlotSessions = new HashSet<int>();
            public HashSet<uint> PurgeFailedReps = new HashSet<uint>();
            public Dictionary<uint, string> Stamps = new Dictionary<uint, string>();
            public Dictionary<uint, TrialPromote.RepFacts> Reps = new Dictionary<uint, TrialPromote.RepFacts>();
            public bool PurgeOk = true;
            public bool? CopyResult = true;
            public List<string> Calls = new List<string>();
            public List<(TrialRoster.RepSlot Rep, uint Master, string Name, CopyCharOptions Options)> Copies = new List<(TrialRoster.RepSlot, uint, string, CopyCharOptions)>();
            public List<Action<bool>> PendingDone = new List<Action<bool>>();
            public Func<uint, bool> OnlineOverride;

            /// <summary>Throw from that lookup instead of answering (F4: the gate must still be released).</summary>
            public bool PurgeThrows, CopyThrows, RepThrows;

            public Fake() => Gate = new TrialCopyGate(() => Clock);

            public int Purges => Calls.Count(c => c.StartsWith("purge", StringComparison.Ordinal));

            /// <summary>
            /// Like the live PurgeSlot, which deletes the biota (and with it the stamp) before anything else, a purge removes the
            /// guid's stamp even when it then reports failure. A landed promote copy writes a promoted stamp back.
            /// </summary>
            public TrialPromote.Lookups Build() => new TrialPromote.Lookups
            {
                Gate = Gate,
                IsOnline = g => OnlineOverride?.Invoke(g) ?? Online.Contains(g),
                HasTrialSession = g => TrialSessions.Contains(g),
                AnySessionForSlot = s => SlotSessions.Contains(s),
                RepPurgeFailed = g => PurgeFailedReps.Contains(g),
                Stamp = g => Stamps.TryGetValue(g, out var s) ? s : null,
                Rep = g => RepThrows ? throw new InvalidOperationException("rep read threw")
                    : Reps.TryGetValue(g, out var r) ? r : new TrialPromote.RepFacts(false, false, 0, new Dictionary<uint, long>()),
                Purge = g =>
                {
                    Calls.Add($"purge 0x{g:X8}");
                    Stamps.Remove(g);
                    if (PurgeThrows)
                        throw new InvalidOperationException("purge threw");
                    return PurgeOk;
                },
                Copy = (admin, rep, master, name, options, onDone) =>
                {
                    Calls.Add($"copy 0x{rep.Guid:X8} -> 0x{master:X8}");
                    Copies.Add((rep, master, name, options));

                    if (CopyThrows)
                        throw new InvalidOperationException("copy threw");

                    if (CopyResult.HasValue)
                        Land(master, CopyResult.Value, onDone);
                    else
                        PendingDone.Add(ok => Land(master, ok, onDone));
                },
                UtcNow = () => Now,
            };

            private void Land(uint master, bool ok, Action<bool> onDone)
            {
                if (ok)
                    Stamps[master] = TrialRebuild.PromotedStamp(ArchSha, CalSha, Now);

                onDone(ok);
            }

            /// <summary>A rebuild's lookups on the SAME gate, so promote and rebuild can be raced against each other.</summary>
            public TrialRebuild.Lookups RebuildLookups(int repIndex) => new TrialRebuild.Lookups
            {
                Gate = Gate,
                IsOnline = g => Online.Contains(g),
                MasterStamp = g => Stamps.TryGetValue(g, out var s) ? s : null,
                AccountId = n => 4242,
                Purge = g =>
                {
                    Calls.Add($"rebuild-purge 0x{g:X8}");
                    Stamps.Remove(g);
                    if (PurgeThrows)
                        throw new InvalidOperationException("rebuild purge threw");
                    return true;
                },
                Copy = (admin, master, rep, account, onDone) =>
                {
                    Calls.Add($"rebuild-copy 0x{master:X8} -> 0x{rep.Guid:X8}");

                    if (CopyThrows)
                        throw new InvalidOperationException("rebuild copy threw");

                    void LandRep(bool ok)
                    {
                        if (ok && Stamps.TryGetValue(master, out var masterStamp))
                            Stamps[rep.Guid] = masterStamp;

                        onDone(ok);
                    }

                    if (CopyResult.HasValue)
                        LandRep(CopyResult.Value);
                    else
                        PendingDone.Add(LandRep);
                },
            };
        }

        private static Fake Healthy(int repIndex, Dictionary<uint, long> stacks = null)
        {
            var f = new Fake();
            var rep = TrialRoster.RepSlots[repIndex - 1];

            f.Stamps[Master] = $"v1|{ArchSha}|{CalSha}";
            f.Stamps[rep.Guid] = $"v1|{ArchSha}|{CalSha}";   // inherited from the master by its last rebuild
            f.Reps[rep.Guid] = new TrialPromote.RepFacts(true, true, 12, stacks ?? new Dictionary<uint, long>());
            return f;
        }

        private static bool Promote(Fake f, int repIndex, out string refusal, out IReadOnlyList<string> reply, TrialArchetype a = null, uint? caller = CallerGuid)
            => TrialPromote.TryPromote(null, caller, a ?? Archetype(), repIndex, f.Build(), out refusal, out reply);

        // ================================================================ promote: the happy path

        [TestMethod]
        public void Promote_purges_the_master_then_copies_the_rep_onto_it_unpublished()
        {
            const int repIndex = 4;
            var rep = TrialRoster.RepSlots[repIndex - 1];
            var f = Healthy(repIndex);

            Assert.IsTrue(Promote(f, repIndex, out var refusal, out var reply), refusal);
            Assert.IsNull(refusal);

            CollectionAssert.AreEqual(new[] { $"purge 0x{Master:X8}", $"copy 0x{rep.Guid:X8} -> 0x{Master:X8}" }, f.Calls, "the master is purged once, then the rep is copied onto it");

            var copy = f.Copies.Single();
            Assert.AreEqual(rep, copy.Rep);
            Assert.AreEqual(Master, copy.Master);
            Assert.AreEqual(TrialRoster.MasterName(3), copy.Name, "the copy is parked under the master's name");
            Assert.IsFalse(copy.Options.Publish, "a promoted master is never published to PlayerManager or a session");
            Assert.IsNotNull(copy.Options.BeforeSave, "the strips run in BeforeSave");

            StringAssert.StartsWith(reply[0], $"promote of rep {repIndex} to archmage-400 (master 0x{Master:X8}) queued; wait for 'Successfully copied', check for a facet WARNING, then rebuild the reps (reps built before this promote will report drift)");
            Assert.AreEqual(1, reply.Count, "no note and no consumable warning in the healthy case: " + string.Join(" / ", reply));

            Assert.IsFalse(f.Gate.IsBusy(Master), "a landed copy releases the master");
            Assert.IsFalse(f.Gate.IsBusy(rep.Guid), "and the rep");
            Assert.IsFalse(f.Gate.IsBroken(Master, out _), "a landed copy clears the broken flag");
        }

        // ================================================================ promote: refusals in order

        /// <summary>
        /// Every refusal condition is set at once, then cleared one at a time in the documented order: each pass must refuse
        /// with the NEXT reason in the list, which pins both that each guard exists and that they run in this order. Nothing is
        /// purged or copied on any refusal, and a refusal after the gate is taken leaves the gate free.
        /// </summary>
        [TestMethod]
        public void Promote_refusals_fire_in_order_and_never_purge_or_copy()
        {
            const int repIndex = 2;
            var rep = TrialRoster.RepSlots[repIndex - 1];

            var f = new Fake();
            uint? caller = null;

            f.Online.Add(rep.Guid);
            f.TrialSessions.Add(rep.Guid);
            f.SlotSessions.Add(3);
            Assert.IsTrue(f.Gate.TryTake(Master, 0, out var foreignToken, out _), "a foreign hold on the master");
            f.PurgeFailedReps.Add(rep.Guid);
            // master stamp missing, rep stamp missing, rep facts missing: the fake's defaults

            var ladder = new (string Expect, Action Fix)[]
            {
                ("run @trial promote from an in-world character", () => caller = rep.Guid),
                ($"you are {rep.Name}", () => caller = CallerGuid),
                ("is online", () => f.Online.Clear()),
                ("still has a trial session", () => f.TrialSessions.Clear()),
                ("a trial session is running archetype slot 3", () => f.SlotSessions.Clear()),
                ($"already in flight on 0x{Master:X8}", () => f.Gate.Release(foreignToken)),
                ("failed at its purge", () => f.PurgeFailedReps.Clear()),
                ("has no stamp and no recorded lineage; seed it first", () => f.Stamps[Master] = "garbage"),
                ($"master 0x{Master:X8} has a stamp of an unknown format ('garbage'); re-run seed-trial-archetypes", () => f.Stamps[Master] = $"v1|{ArchSha}|{CalSha}"),
                ("carries no archetype stamp", () => f.Stamps[rep.Guid] = $"v1|{OtherSha}|{CalSha}"),
                ($"was built from archetype {OtherSha}", () => f.Stamps[rep.Guid] = TrialRebuild.PromotedStamp(ArchSha, CalSha, Now)),
                ("has no character row", () => f.Reps[rep.Guid] = new TrialPromote.RepFacts(true, false, 0, null)),
                ("has no biota", () => f.Reps[rep.Guid] = new TrialPromote.RepFacts(true, true, 0, null)),
                ("has no possessions", () => f.Reps[rep.Guid] = new TrialPromote.RepFacts(true, true, 3, null)),
            };

            for (var i = 0; i < ladder.Length; i++)
            {
                var ok = TrialPromote.TryPromote(null, caller, Archetype(), repIndex, f.Build(), out var refusal, out _);

                Assert.IsFalse(ok, $"step {i}: expected a refusal containing '{ladder[i].Expect}'");
                StringAssert.Contains(refusal, ladder[i].Expect, $"step {i}");
                Assert.AreEqual(0, f.Calls.Count, $"step {i}: nothing may be purged or copied on a refusal ({string.Join(", ", f.Calls)})");

                // From step 6 on the refusal comes after the gate is taken: it must have been released again.
                if (i >= 6)
                {
                    Assert.IsFalse(f.Gate.IsBusy(Master), $"step {i}: a refusal after the take must release the master");
                    Assert.IsFalse(f.Gate.IsBusy(rep.Guid), $"step {i}: and the rep");
                }

                ladder[i].Fix();
            }

            // Control: with every condition cleared, the promote goes through (purge, then copy).
            Assert.IsTrue(TrialPromote.TryPromote(null, caller, Archetype(), repIndex, f.Build(), out var last, out _), last);
            Assert.AreEqual(1, f.Purges);
            Assert.AreEqual(1, f.Copies.Count);
        }

        [TestMethod]
        public void Promote_refuses_a_rep_index_outside_1_to_12_first()
        {
            foreach (var bad in new[] { 0, 13 })
            {
                var f = Healthy(2);
                Assert.IsFalse(TrialPromote.TryPromote(null, null, Archetype(), bad, f.Build(), out var refusal, out _));
                StringAssert.Contains(refusal, "1..12", "the index is checked before the caller");
                Assert.AreEqual(0, f.Calls.Count);
            }
        }

        [TestMethod]
        public void Promote_rechecks_online_immediately_before_the_purge()
        {
            const int repIndex = 7;
            var rep = TrialRoster.RepSlots[repIndex - 1];
            var f = Healthy(repIndex);

            var calls = 0;
            f.OnlineOverride = g => g == rep.Guid && ++calls >= 2;

            Assert.IsFalse(Promote(f, repIndex, out var refusal, out _));
            StringAssert.Contains(refusal, "came online");
            Assert.AreEqual(2, calls, "online is checked up front and again right before the purge");
            Assert.AreEqual(0, f.Calls.Count, "no purge after the re-check refuses");
            Assert.IsFalse(f.Gate.IsBusy(Master), "the re-check refusal releases the gate");
        }

        [TestMethod]
        public void Promote_drift_alone_does_not_refuse()
        {
            const int repIndex = 3;
            var rep = TrialRoster.RepSlots[repIndex - 1];

            TrialRepSetup.MarkRebuildRequired(rep.Guid, "state digest drift (x)");
            Assert.IsFalse(TrialRepSetup.PurgeFailed(rep.Guid), "drift does not set PurgeFailed");

            // The live ledger answers the PurgeFailed lookup here, as it does in production (TrialPromote.Live).
            var f = Healthy(repIndex);
            var lookups = f.Build();
            lookups.RepPurgeFailed = TrialRepSetup.PurgeFailed;
            Assert.IsTrue(TrialPromote.TryPromote(null, CallerGuid, Archetype(), repIndex, lookups, out var refusal, out _), refusal);

            // Control: the same ledger, with a failed purge recorded, refuses.
            TrialRepSetup.MarkRebuildRequired(rep.Guid, "the last rebuild's purge failed", purgeFailed: true);
            var g = Healthy(repIndex);
            var failed = g.Build();
            failed.RepPurgeFailed = TrialRepSetup.PurgeFailed;
            Assert.IsFalse(TrialPromote.TryPromote(null, CallerGuid, Archetype(), repIndex, failed, out refusal, out _));
            StringAssert.Contains(refusal, "failed at its purge or copy");

            // A rebuild whose copy failed marks the rep the same way, and promote names both causes.
            TrialRepSetup.ResetBlock(rep.Guid);
            TrialRepSetup.MarkRebuildRequired(rep.Guid, "the last rebuild's copy failed", purgeFailed: true);
            Assert.IsTrue(TrialRepSetup.PurgeFailed(rep.Guid));
        }

        // ================================================================ purge and copy outcomes, the gate, the broken flag

        [TestMethod]
        public void Promote_purge_failure_marks_the_master_broken_copies_nothing_and_releases_the_gate()
        {
            const int repIndex = 1;
            var rep = TrialRoster.RepSlots[repIndex - 1];
            var f = Healthy(repIndex);
            f.PurgeOk = false;

            Assert.IsFalse(Promote(f, repIndex, out var refusal, out _));
            StringAssert.Contains(refusal, "purge of master");
            Assert.AreEqual(1, f.Purges);
            Assert.AreEqual(0, f.Copies.Count, "nothing is copied after a failed purge");
            Assert.IsTrue(f.Gate.IsBroken(Master, out var why));
            StringAssert.Contains(why, "failed at its purge");
            Assert.IsFalse(f.Gate.IsBusy(Master));
            Assert.IsFalse(f.Gate.IsBusy(rep.Guid));
        }

        [TestMethod]
        public void Promote_gate_is_released_on_true_and_false_and_lapses_when_the_copy_never_reports()
        {
            const int repIndex = 5;
            var rep = TrialRoster.RepSlots[repIndex - 1];

            // false: released, and the purged master is broken.
            var failing = Healthy(repIndex);
            failing.CopyResult = false;
            Assert.IsTrue(Promote(failing, repIndex, out var refusal, out _), refusal);
            Assert.IsFalse(failing.Gate.IsBusy(Master));
            Assert.IsFalse(failing.Gate.IsBusy(rep.Guid));
            Assert.IsTrue(failing.Gate.IsBroken(Master, out var why));
            StringAssert.Contains(why, "failed at its copy");

            // never reports: the hold stays, a second promote is refused busy, and nothing more is purged.
            var silent = Healthy(repIndex);
            silent.CopyResult = null;
            Assert.IsTrue(Promote(silent, repIndex, out refusal, out _), refusal);
            Assert.IsTrue(silent.Gate.IsBusy(Master));
            Assert.IsTrue(silent.Gate.IsBroken(Master, out _), "a copy that has not landed leaves the master broken");

            Assert.IsFalse(Promote(silent, repIndex, out refusal, out _));
            StringAssert.Contains(refusal, "already in flight");
            Assert.AreEqual(1, silent.Purges);

            // Just before the lapse it is still held; past HoldSeconds it lapses and a new promote can run.
            silent.Clock = TrialCopyGate.HoldSeconds - 1;
            Assert.IsTrue(silent.Gate.IsBusy(Master));
            silent.Clock = TrialCopyGate.HoldSeconds + 1;
            Assert.IsFalse(silent.Gate.IsBusy(Master), "an unreleased hold lapses after HoldSeconds");

            // The hold has lapsed, but the copy has not reported and may still land: the shared tail waits for it until its
            // mark is 3 x HoldSeconds old, so nothing is purged under it.
            Assert.IsFalse(Promote(silent, repIndex, out refusal, out _));
            StringAssert.Contains(refusal, "has not reported; wait for it or restart");
            Assert.AreEqual(1, silent.Purges);
            Assert.IsFalse(silent.Gate.IsBusy(Master), "the refusal released the gate");

            silent.Clock = TrialPromote.CopyInFlightWaitSeconds + 1;
            Assert.IsTrue(Promote(silent, repIndex, out refusal, out _), refusal);
            Assert.AreEqual(2, silent.PendingDone.Count);
            Assert.IsTrue(silent.Gate.IsBusy(Master), "the second promote holds the master");

            // The FIRST promote's late callback must not free the second one's hold.
            silent.PendingDone[0](true);
            Assert.IsTrue(silent.Gate.IsBusy(Master), "a lapsed token's late release must not free a newer hold");
            Assert.IsTrue(silent.Gate.IsBroken(Master, out _), "nor clear the broken flag the newer promote set");

            silent.PendingDone[1](true);
            Assert.IsFalse(silent.Gate.IsBusy(Master));
            Assert.IsFalse(silent.Gate.IsBroken(Master, out _), "the landed copy clears the broken flag");
        }

        [TestMethod]
        public void Broken_master_blocks_rebuild_until_a_promote_lands()
        {
            const int repIndex = 2;
            var f = Healthy(repIndex);
            f.PurgeOk = false;

            Assert.IsFalse(Promote(f, repIndex, out _, out _));
            Assert.IsTrue(f.Gate.IsBroken(Master, out _));

            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, f.RebuildLookups(6), out var refusal));
            StringAssert.Contains(refusal, $"master 0x{Master:X8} is broken");
            Assert.IsFalse(f.Calls.Any(c => c.StartsWith("rebuild", StringComparison.Ordinal)), "a broken master is never copied from");

            // Recovery: re-running promote, whose copy lands, clears the flag; rebuild then runs.
            f.PurgeOk = true;
            Assert.IsTrue(Promote(f, repIndex, out refusal, out _), refusal);
            Assert.IsFalse(f.Gate.IsBroken(Master, out _));
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, f.RebuildLookups(6), out refusal), refusal);
        }

        [TestMethod]
        public void Promote_and_rebuild_exclude_each_other_but_rebuilds_of_different_reps_do_not()
        {
            // A rebuild of rep 6 from the master is in flight: the master is held shared, rep 6 exclusive.
            var f = Healthy(2);
            f.CopyResult = null;
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, f.RebuildLookups(6), out var refusal), refusal);

            // Another rebuild from the same master, onto rep 7, may run: both only read the master.
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 7, f.RebuildLookups(7), out refusal), refusal);

            // A second rebuild of rep 6 may not.
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, f.RebuildLookups(6), out refusal));
            StringAssert.Contains(refusal, $"already in flight on 0x{TrialRoster.RepSlots[5].Guid:X8}");

            // A promote over the master may not: it needs the master exclusive.
            Assert.IsFalse(Promote(f, 2, out refusal, out _));
            StringAssert.Contains(refusal, $"already in flight on 0x{Master:X8}");
            Assert.AreEqual(0, f.Purges, "the refused promote purged nothing");

            // Both rebuild copies land: the promote can run.
            foreach (var done in f.PendingDone.ToList())
                done(true);
            f.PendingDone.Clear();
            Assert.IsTrue(Promote(f, 2, out refusal, out _), refusal);

            // A promote in flight blocks a rebuild of ANY rep from that master, and a rebuild of the rep being promoted.
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, f.RebuildLookups(6), out refusal));
            StringAssert.Contains(refusal, $"0x{Master:X8}");
        }

        [TestMethod]
        public void Gate_shared_holds_coexist_exclusive_conflicts_and_a_take_is_all_or_nothing()
        {
            var clock = 0.0;
            var gate = new TrialCopyGate(() => clock);
            const uint a = 1, b = 2, c = 3;

            Assert.IsTrue(gate.TryTake(a, b, out var t1, out _), "a exclusive, b shared");
            Assert.IsTrue(gate.TryTake(c, b, out var t2, out _), "b may be shared twice");

            Assert.IsFalse(gate.TryTake(b, 0, out _, out var busy), "b cannot be taken exclusive while shared");
            Assert.AreEqual(b, busy);
            Assert.IsFalse(gate.TryTake(c, a, out _, out busy), "a exclusive cannot also be shared");

            // All or nothing: the exclusive half was free (4) but the shared half (a) is not, so 4 must stay free.
            Assert.IsFalse(gate.TryTake(4, a, out _, out busy));
            Assert.AreEqual(a, busy);
            Assert.IsFalse(gate.IsBusy(4), "a refused take holds nothing");

            Assert.IsTrue(gate.Release(t1));
            Assert.IsFalse(gate.Release(t1), "a second release of the same token is a no-op");
            Assert.IsFalse(gate.IsBusy(a));
            Assert.IsTrue(gate.IsBusy(b), "t2 still shares b");

            // ReleaseAndClearBroken clears only for a live token.
            gate.MarkBroken(9, "x");
            Assert.IsTrue(gate.TryTake(9, 0, out var t3, out _));
            clock = TrialCopyGate.HoldSeconds + 1;
            Assert.IsFalse(gate.ReleaseAndClearBroken(t3, 9), "the hold lapsed");
            Assert.IsTrue(gate.IsBroken(9, out _), "a lapsed token does not clear the flag");

            Assert.IsTrue(gate.TryTake(9, 0, out var t4, out _));
            Assert.IsTrue(gate.ReleaseAndClearBroken(t4, 9));
            Assert.IsFalse(gate.IsBroken(9, out _));
            GC.KeepAlive(t2);
        }

        [TestMethod]
        public void Rebuild_releases_the_gate_when_its_online_recheck_refuses()
        {
            var f = Healthy(2);
            var lookups = f.RebuildLookups(6);
            var calls = 0;
            lookups.IsOnline = g => ++calls >= 2;

            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, lookups, out var refusal));
            StringAssert.Contains(refusal, "came online");
            Assert.IsFalse(f.Gate.IsBusy(TrialRoster.RepSlots[5].Guid), "the rep is released");
            Assert.IsFalse(f.Gate.IsBusy(Master), "and the master");
        }

        // ================================================================ F1: recovery after a purge deleted the stamp

        [TestMethod]
        public void A_failed_copy_is_recovered_by_re_running_promote_from_the_recorded_lineage()
        {
            const int repIndex = 3;
            var rep = TrialRoster.RepSlots[repIndex - 1];
            var f = Healthy(repIndex);

            // The purge succeeds (and, as live, deletes the master's stamp); the copy fails.
            f.CopyResult = false;
            Assert.IsTrue(Promote(f, repIndex, out var refusal, out _), refusal);
            Assert.IsFalse(f.Stamps.ContainsKey(Master), "positive control: the purge deleted the master's stamp");
            Assert.IsTrue(f.Gate.IsBroken(Master, out _));
            Assert.IsTrue(f.Gate.TryGetBrokenLineage(Master, out var arch, out var cal), "the lineage was recorded before the purge");
            Assert.AreEqual(ArchSha, arch);
            Assert.AreEqual(CalSha, cal);

            // Rebuild refuses the broken master and says how to recover.
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, f.RebuildLookups(6), out refusal));
            StringAssert.Contains(refusal, "re-run @trial promote");
            StringAssert.Contains(refusal, "after a server restart");

            // Re-running promote proceeds on the recorded lineage: it purges and copies again, and says where the lineage came from.
            f.CopyResult = true;
            Assert.IsTrue(Promote(f, repIndex, out refusal, out var reply), refusal);
            CollectionAssert.AreEqual(new[]
            {
                $"purge 0x{Master:X8}", $"copy 0x{rep.Guid:X8} -> 0x{Master:X8}",
                $"purge 0x{Master:X8}", $"copy 0x{rep.Guid:X8} -> 0x{Master:X8}",
            }, f.Calls);
            Assert.IsTrue(reply.Any(l => l.Contains("comes from that run's record")), string.Join(" / ", reply));
            Assert.IsFalse(f.Gate.IsBroken(Master, out _), "the landed copy clears the flag");
            Assert.IsFalse(f.Gate.TryGetBrokenLineage(Master, out _, out _), "and the lineage with it");

            // Rebuild works again.
            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, f.RebuildLookups(6), out refusal), refusal);
        }

        [TestMethod]
        public void Recorded_lineage_is_still_checked_against_the_rep()
        {
            const int repIndex = 3;
            var rep = TrialRoster.RepSlots[repIndex - 1];
            var f = Healthy(repIndex);
            f.CopyResult = false;
            Assert.IsTrue(Promote(f, repIndex, out var refusal, out _), refusal);

            f.Stamps[rep.Guid] = $"v1|{OtherSha}|{CalSha}";
            Assert.IsFalse(Promote(f, repIndex, out refusal, out _));
            StringAssert.Contains(refusal, $"was built from archetype {OtherSha}, but master 0x{Master:X8} is {ArchSha}");
            Assert.AreEqual(1, f.Purges, "the refused re-run purged nothing");
        }

        [TestMethod]
        public void With_no_recorded_lineage_a_stampless_master_names_the_reseed()
        {
            // A server restart: the gate is new, the master was left with no stamp.
            const int repIndex = 1;
            var f = Healthy(repIndex);
            f.Stamps.Remove(Master);

            Assert.IsFalse(Promote(f, repIndex, out var refusal, out _));
            StringAssert.Contains(refusal, "no recorded lineage; seed it first (seed-trial-archetypes)");
            StringAssert.Contains(refusal, "After a server restart");
            Assert.AreEqual(0, f.Calls.Count);
            Assert.IsFalse(f.Gate.IsBusy(Master));
        }

        [TestMethod]
        public void A_recorded_lineage_does_not_cover_a_master_stamp_of_unknown_format()
        {
            // The fallback is for the post-purge state (no stamp at all). A stamp that is present but unreadable is not that
            // state, so a broken record for the master must not paper over it.
            const int repIndex = 1;
            var f = Healthy(repIndex);
            f.Stamps[Master] = "garbage";
            f.Gate.MarkBroken(Master, "an earlier promote", ArchSha, CalSha);
            Assert.IsTrue(f.Gate.TryGetBrokenLineage(Master, out _, out _), "positive control: the lineage is recorded");

            Assert.IsFalse(Promote(f, repIndex, out var refusal, out _));
            Assert.AreEqual($"master 0x{Master:X8} has a stamp of an unknown format ('garbage'); re-run seed-trial-archetypes", refusal);
            Assert.AreEqual(0, f.Purges, "nothing was purged");
            Assert.AreEqual(0, f.Copies.Count, "nothing was copied");
            Assert.IsFalse(f.Gate.IsBusy(Master));
        }

        // ================================================================ F4: a throw after the take releases the gate

        [TestMethod]
        public void Promote_that_throws_releases_the_gate_and_a_thrown_purge_leaves_the_master_broken()
        {
            const int repIndex = 2;
            var rep = TrialRoster.RepSlots[repIndex - 1];

            // Before the purge (the rep read throws): released, nothing purged, master NOT broken.
            var early = Healthy(repIndex);
            early.RepThrows = true;
            Assert.IsFalse(Promote(early, repIndex, out var refusal, out _));
            StringAssert.Contains(refusal, "threw before anything was purged");
            Assert.IsFalse(early.Gate.IsBusy(Master));
            Assert.IsFalse(early.Gate.IsBusy(rep.Guid));
            Assert.IsFalse(early.Gate.IsBroken(Master, out _));
            Assert.AreEqual(0, early.Purges);

            // The purge throws: released, master broken with its lineage, nothing copied.
            var purge = Healthy(repIndex);
            purge.PurgeThrows = true;
            Assert.IsFalse(Promote(purge, repIndex, out refusal, out _));
            StringAssert.Contains(refusal, "threw after the purge");
            Assert.IsFalse(purge.Gate.IsBusy(Master));
            Assert.IsFalse(purge.Gate.IsBusy(rep.Guid));
            Assert.IsTrue(purge.Gate.IsBroken(Master, out var why));
            StringAssert.Contains(why, "threw after its purge started");
            Assert.IsTrue(purge.Gate.TryGetBrokenLineage(Master, out _, out _));
            Assert.AreEqual(0, purge.Copies.Count);

            // The copy hand-off throws: released and broken (the purge ran).
            var copy = Healthy(repIndex);
            copy.CopyThrows = true;
            Assert.IsFalse(Promote(copy, repIndex, out refusal, out _));
            Assert.IsFalse(copy.Gate.IsBusy(Master));
            Assert.IsTrue(copy.Gate.IsBroken(Master, out _));

            // Recovery after the thrown purge: the next promote runs from the recorded lineage.
            purge.PurgeThrows = false;
            Assert.IsTrue(Promote(purge, repIndex, out refusal, out _), refusal);
        }

        [TestMethod]
        public void Unpromote_that_throws_releases_the_gate()
        {
            var f = new Fake { PurgeThrows = true };
            f.Stamps[Master] = TrialRebuild.PromotedStamp(ArchSha, CalSha, Now);

            Assert.IsFalse(TrialPromote.TryUnpromote(Archetype(), f.Build(), out var refusal, out _));
            StringAssert.Contains(refusal, "threw after the purge");
            Assert.IsFalse(f.Gate.IsBusy(Master));
            Assert.IsTrue(f.Gate.IsBroken(Master, out var why));
            StringAssert.Contains(why, "unpromote threw after its purge started");
        }

        [TestMethod]
        public void Rebuild_that_throws_releases_the_gate_and_a_thrown_purge_owes_a_rebuild()
        {
            var rep = TrialRoster.RepSlots[5];

            var purge = Healthy(2);
            purge.PurgeThrows = true;
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, purge.RebuildLookups(6), out var refusal));
            StringAssert.Contains(refusal, "threw");
            Assert.IsFalse(purge.Gate.IsBusy(rep.Guid));
            Assert.IsFalse(purge.Gate.IsBusy(Master));
            Assert.IsTrue(TrialRepSetup.PurgeFailed(rep.Guid), "a thrown purge may have left the rep partly populated");

            TrialRepSetup.ResetAllBlocksForTests();

            var copy = Healthy(2);
            copy.CopyThrows = true;
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 6, copy.RebuildLookups(6), out refusal));
            Assert.IsFalse(copy.Gate.IsBusy(rep.Guid));
            Assert.IsFalse(copy.Gate.IsBusy(Master));
        }

        // ================================================================ F2: @trial start and the gate

        [TestMethod]
        public void Start_refuses_a_master_that_is_held_or_broken()
        {
            var clock = 0.0;
            var gate = new TrialCopyGate(() => clock);

            Assert.IsNull(TrialStarter.MasterCopyRefusal(gate, Master), "a free, healthy master starts");

            Assert.IsTrue(gate.TryTake(TrialRoster.RepSlots[0].Guid, Master, out var shared, out _), "a rebuild holds it shared");
            StringAssert.Contains(TrialStarter.MasterCopyRefusal(gate, Master), "is being copied or purged");
            gate.Release(shared);

            Assert.IsTrue(gate.TryTake(Master, 0, out var exclusive, out _), "a promote holds it exclusive");
            StringAssert.Contains(TrialStarter.MasterCopyRefusal(gate, Master), "is being copied or purged");
            gate.Release(exclusive);

            gate.MarkBroken(Master, "x");
            var refusal = TrialStarter.MasterCopyRefusal(gate, Master);
            StringAssert.Contains(refusal, "is broken (x)");
            StringAssert.Contains(refusal, "re-run @trial promote");

            gate.ClearBroken(Master);
            Assert.IsNull(TrialStarter.MasterCopyRefusal(gate, Master));

            Assert.IsNull(TrialStarter.MasterCopyRefusal(gate, TrialRoster.MasterSlot(4)), "another slot is unaffected");
        }

        [TestMethod]
        public void Start_checks_the_shared_gate_before_creating_or_registering_anything()
        {
            var start = Slice("Source/ACE.Server/BalanceTrials/TrialStarter.cs", "public static bool TryStart(", "internal static TrialRep PrepareRep(");

            var check = start.IndexOf("var copyRefusal = MasterCopyRefusal(TrialCopyGate.Shared, TrialRoster.MasterSlot(archetype.Slot));", StringComparison.Ordinal);
            var refuse = start.IndexOf("if (copyRefusal != null)", StringComparison.Ordinal);
            var create = start.IndexOf("RealmManager.GetNewEphemeralLandblock(", StringComparison.Ordinal);
            var register = start.IndexOf("TrialManager.Register(session, landblock);", StringComparison.Ordinal);

            Assert.IsTrue(create > 0 && register > create, "positive control: the create and the register are found");
            Assert.IsTrue(check > 0 && refuse > check, "TryStart must ask MasterCopyRefusal of the shared gate and refuse on an answer");
            Assert.IsTrue(refuse < create, "the refusal comes before the private copy is created, so nothing is left behind");
        }

        // ================================================================ consumables

        [TestMethod]
        public void Consumable_warning_fires_only_on_a_shortfall_and_never_for_an_empty_list()
        {
            // archmage-400 lists no consumables: never a warning, whatever the rep carries.
            Assert.AreEqual(0, TrialPromote.ConsumableShortfalls(Array.Empty<TrialConsumable>(), new Dictionary<uint, long>()).Count);
            Assert.AreEqual(0, TrialPromote.ConsumableShortfalls(null, null).Count);

            var wanted = new[] { new TrialConsumable(100, 10), new TrialConsumable(200, 25), new TrialConsumable(200, 5) };

            CollectionAssert.AreEqual(new[] { "wcid 100: 9/10", "wcid 200: 0/30" },
                TrialPromote.ConsumableShortfalls(wanted, new Dictionary<uint, long> { [100] = 9 }).ToArray(), "short stacks, wants summed by wcid");

            Assert.AreEqual(0, TrialPromote.ConsumableShortfalls(wanted, new Dictionary<uint, long> { [100] = 10, [200] = 31 }).Count, "enough of each");

            // Through the reply: a short rep warns, an empty list does not.
            var shortRep = Healthy(1, new Dictionary<uint, long> { [100] = 2 });
            Assert.IsTrue(Promote(shortRep, 1, out var refusal, out var reply, Archetype(new TrialConsumable(100, 10))), refusal);
            Assert.AreEqual(2, reply.Count);
            StringAssert.StartsWith(reply[1], "WARNING:");
            StringAssert.Contains(reply[1], "wcid 100: 2/10");

            var noList = Healthy(1);
            Assert.IsTrue(Promote(noList, 1, out refusal, out reply, Archetype()), refusal);
            Assert.AreEqual(1, reply.Count, string.Join(" / ", reply));
            Assert.IsFalse(reply.Any(l => l.StartsWith("WARNING:", StringComparison.Ordinal)), "no consumable warning line (the reply's own 'facet WARNING' text is not one): " + string.Join(" / ", reply));
        }

        [TestMethod]
        public void Stacks_sum_by_wcid_and_an_unstacked_item_counts_one()
        {
            var stacks = TrialPromote.StacksByWcid(new (uint, long?)[] { (100, 7), (100, 3), (200, null), (200, null) });

            Assert.AreEqual(10L, stacks[100]);
            Assert.AreEqual(2L, stacks[200]);
        }

        // ================================================================ Normalize

        [TestMethod]
        public void Normalize_drops_vitae_and_timed_enchantments_clears_foreign_casters_stamps_and_parks()
        {
            const uint playerId = 0x59000003, itemId = 0x80123456, foreign = 0x50001111;

            var player = new Biota
            {
                Id = playerId,
                PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry>
                {
                    new PropertiesEnchantmentRegistry { SpellId = (int)SpellId.Vitae, Duration = -1, CasterObjectId = playerId },
                    new PropertiesEnchantmentRegistry { SpellId = 2101, Duration = 1800, CasterObjectId = playerId },
                    new PropertiesEnchantmentRegistry { SpellId = 2102, Duration = -1, CasterObjectId = itemId },
                    new PropertiesEnchantmentRegistry { SpellId = 2103, Duration = -1, CasterObjectId = foreign },
                    new PropertiesEnchantmentRegistry { SpellId = 2104, Duration = -1, CasterObjectId = 0 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.TrialArchetypeStamp] = $"v1|{ArchSha}|{CalSha}" },
            };

            var item = new Biota
            {
                Id = itemId,
                PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry>
                {
                    new PropertiesEnchantmentRegistry { SpellId = 3001, Duration = 60, CasterObjectId = foreign },
                    new PropertiesEnchantmentRegistry { SpellId = 3002, Duration = -1, CasterObjectId = itemId },
                },
            };

            var bare = new Biota { Id = 0x80123457 };   // no enchantment registry at all

            var character = new ShardCharacter { Id = playerId, AccountId = 77, IsDeleted = false, DeleteTime = 0 };

            var stamp = TrialRebuild.PromotedStamp(ArchSha, CalSha, Now);
            var result = TrialPromote.Normalize(player, new[] { item, bare }, character, stamp, 1234567);

            Assert.AreEqual(1, result.VitaeRemoved);
            Assert.AreEqual(2, result.TimedRemoved, "one timed row on the player, one on the item");
            Assert.AreEqual(1, result.CastersCleared, "only the permanent row cast by a character outside the copy");

            CollectionAssert.AreEqual(new[] { 2102, 2103, 2104 }, player.PropertiesEnchantmentRegistry.Select(e => e.SpellId).ToArray());
            Assert.AreEqual(itemId, player.PropertiesEnchantmentRegistry.Single(e => e.SpellId == 2102).CasterObjectId, "a caster inside the copy is kept");
            Assert.AreEqual(0u, player.PropertiesEnchantmentRegistry.Single(e => e.SpellId == 2103).CasterObjectId, "a foreign caster is cleared");
            CollectionAssert.AreEqual(new[] { 3002 }, item.PropertiesEnchantmentRegistry.Select(e => e.SpellId).ToArray());

            Assert.AreEqual(stamp, player.PropertiesString[PropertyString.TrialArchetypeStamp], "the stamp is replaced by the promoted one");
            Assert.IsTrue(TrialRebuild.TryParseStamp(player.PropertiesString[PropertyString.TrialArchetypeStamp], out _, out _, out var at) && at == Now);

            Assert.AreEqual(0u, character.AccountId, "parked on account 0");
            Assert.IsTrue(character.IsDeleted, "parked as deleted");
            Assert.AreEqual(1234567ul, character.DeleteTime);
        }

        [TestMethod]
        public void Normalize_creates_the_string_table_when_the_player_has_none()
        {
            var player = new Biota { Id = 0x59000003 };
            var character = new ShardCharacter { Id = player.Id, AccountId = 5 };

            TrialPromote.Normalize(player, null, character, "v1|a|c|promoted|2026-11-22T14:30:00Z", 1);

            Assert.AreEqual("v1|a|c|promoted|2026-11-22T14:30:00Z", player.PropertiesString[PropertyString.TrialArchetypeStamp]);
        }

        // ================================================================ unpromote

        [TestMethod]
        public void Unpromote_refusals_fire_in_order_and_a_seeded_master_is_never_purged()
        {
            var f = new Fake();
            f.SlotSessions.Add(3);
            Assert.IsTrue(f.Gate.TryTake(TrialRoster.RepSlots[0].Guid, Master, out var foreignToken, out _), "a rebuild holding the master shared");
            f.Stamps[Master] = $"v1|{ArchSha}|{CalSha}";   // a SEEDED master

            var ladder = new (string Expect, Action Fix)[]
            {
                ("a trial session is running archetype slot 3", () => f.SlotSessions.Clear()),
                ($"already in flight on 0x{Master:X8}", () => f.Gate.Release(foreignToken)),
                ("is not promoted", () => f.Stamps.Remove(Master)),
                ("is not promoted (stamp 'none')", () => f.Stamps[Master] = $"v1|{ArchSha}|{CalSha}|Promoted|2026-11-22T14:30:00Z"),
                ("is not promoted", () => f.Stamps[Master] = TrialRebuild.PromotedStamp(ArchSha, CalSha, Now)),
            };

            for (var i = 0; i < ladder.Length; i++)
            {
                Assert.IsFalse(TrialPromote.TryUnpromote(Archetype(), f.Build(), out var refusal, out _), $"step {i}");
                StringAssert.Contains(refusal, ladder[i].Expect, $"step {i}");
                Assert.AreEqual(0, f.Purges, $"step {i}: nothing is purged on a refusal");

                if (i >= 2)
                    Assert.IsFalse(f.Gate.IsBusy(Master), $"step {i}: a refusal after the take releases the master");

                ladder[i].Fix();
            }

            Assert.IsTrue(TrialPromote.TryUnpromote(Archetype(), f.Build(), out var last, out var reply), last);
            CollectionAssert.AreEqual(new[] { $"purge 0x{Master:X8}" }, f.Calls);
            Assert.AreEqual($"master 0x{Master:X8} (archmage-400) purged; run seed-trial-archetypes (mode=seed) to rebuild it from the snapshot", reply);
            Assert.IsFalse(f.Gate.IsBusy(Master));
            Assert.IsFalse(f.Gate.IsBroken(Master, out _));
        }

        [TestMethod]
        public void Unpromote_purge_failure_marks_the_master_broken_and_releases_the_gate()
        {
            var f = new Fake { PurgeOk = false };
            f.Stamps[Master] = TrialRebuild.PromotedStamp(ArchSha, CalSha, Now);

            Assert.IsFalse(TrialPromote.TryUnpromote(Archetype(), f.Build(), out var refusal, out _));
            StringAssert.Contains(refusal, "purge of master");
            Assert.IsTrue(f.Gate.IsBroken(Master, out var why));
            StringAssert.Contains(why, "unpromote failed at its purge");
            Assert.IsFalse(f.Gate.IsBusy(Master));

            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 1, f.RebuildLookups(1), out refusal));
            StringAssert.Contains(refusal, "is broken");
        }

        // ================================================================ registry, ledger, parser

        [TestMethod]
        public void AnySessionForSlot_sees_a_registered_session_of_that_slot_only()
        {
            Assert.IsFalse(TrialManager.AnySessionForSlot(3));

            var session = new TrialSession("t-1", null, null, Archetype(), ArchSha, null, 0x50000999, null, _ => null);
            TrialManager.RegisterForTests(session, 0x7A010000, seenLive: false);

            Assert.IsTrue(TrialManager.AnySessionForSlot(3));
            Assert.IsFalse(TrialManager.AnySessionForSlot(4));
        }

        [TestMethod]
        public void PurgeFailed_is_set_by_a_failed_rebuild_purge_only_and_cleared_by_a_completed_rebuild()
        {
            var rep = TrialRoster.RepSlots[3];

            Assert.IsFalse(TrialRepSetup.PurgeFailed(rep.Guid), "nothing marked yet");

            var f = Healthy(2);
            var lookups = f.RebuildLookups(4);
            lookups.Purge = _ => false;
            Assert.IsFalse(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 4, lookups, out _));
            Assert.IsTrue(TrialRepSetup.PurgeFailed(rep.Guid), "a failed rebuild purge sets it");
            Assert.IsFalse(f.Gate.IsBusy(rep.Guid), "and releases the gate");

            Assert.IsTrue(TrialRebuild.TryRebuild(null, Archetype(), ArchSha, 4, f.RebuildLookups(4), out var refusal), refusal);
            Assert.IsFalse(TrialRepSetup.PurgeFailed(rep.Guid), "a completed rebuild clears it");
        }

        [TestMethod]
        public void Parser_accepts_promote_and_unpromote()
        {
            var (verb, args, error) = TrialCommandParser.Parse(new[] { "promote", "archmage-400", "3" });
            Assert.IsNull(error);
            Assert.AreEqual("promote", verb);
            CollectionAssert.AreEqual(new[] { "archmage-400", "3" }, args);

            (verb, args, error) = TrialCommandParser.Parse(new[] { "UNPROMOTE", "archmage-400" });
            Assert.IsNull(error);
            Assert.AreEqual("unpromote", verb);

            Assert.AreEqual("usage: @trial promote <archetype> <1-12>", TrialCommandParser.Parse(new[] { "promote", "archmage-400" }).Error);
            StringAssert.Contains(TrialCommandParser.Parse(new[] { "promote", "archmage-400", "13" }).Error, "1..12");
            Assert.IsNull(TrialCommandParser.Parse(new[] { "promote", "archmage-400", "12" }).Error, "rep 12 is a valid slot");
            Assert.AreEqual("usage: @trial unpromote <archetype>", TrialCommandParser.Parse(new[] { "unpromote" }).Error);
            Assert.AreEqual("usage: @trial unpromote <archetype>", TrialCommandParser.Parse(new[] { "unpromote", "a", "1" }).Error);
            Assert.AreEqual("usage: @trial rebuild <archetype> <1-12>", TrialCommandParser.Parse(new[] { "rebuild" }).Error, "rebuild's usage line tracks RepCount");

            StringAssert.Contains(TrialCommandParser.Usage, "promote <archetype> <1-12>");
            var handler = (ACE.Server.Command.CommandHandlerAttribute)Attribute.GetCustomAttribute(typeof(TrialCommands).GetMethod(nameof(TrialCommands.HandleTrial)), typeof(ACE.Server.Command.CommandHandlerAttribute));
            StringAssert.Contains(handler.Usage, $"rebuild <archetype> <1-{TrialRoster.RepCount}> | promote <archetype> <1-{TrialRoster.RepCount}>", "the handler usage text (a literal, attributes need constants) tracks RepCount");
            StringAssert.Contains(TrialCommandParser.Usage, "unpromote <archetype>");
        }

        [TestMethod]
        public void Unpromote_waits_for_an_unreported_copy_for_three_hold_lengths()
        {
            var f = new Fake();
            f.Stamps[Master] = TrialRebuild.PromotedStamp(ArchSha, CalSha, Now);
            f.Gate.MarkBroken(Master, "a promote's copy has not landed", ArchSha, CalSha, copyInFlight: true);

            // +301 s: no hold, but the copy has not reported: refused, nothing purged, the gate released.
            f.Clock = TrialCopyGate.HoldSeconds + 1;
            Assert.IsFalse(TrialPromote.TryUnpromote(Archetype(), f.Build(), out var refusal, out _));
            StringAssert.Contains(refusal, "a copy onto this master started 301 s ago and has not reported; wait for it or restart");
            Assert.AreEqual(0, f.Purges);
            Assert.IsFalse(f.Gate.IsBusy(Master));

            // +15 min +1 s: the mark is old enough, so the unpromote purges.
            f.Clock = TrialPromote.CopyInFlightWaitSeconds + 1;
            Assert.IsTrue(TrialPromote.TryUnpromote(Archetype(), f.Build(), out refusal, out _), refusal);
            Assert.AreEqual(1, f.Purges);
            Assert.IsFalse(f.Gate.IsBroken(Master, out _));
        }

        [TestMethod]
        public void Gate_clears_a_broken_record_by_generation_only_while_no_newer_mark_exists()
        {
            var gate = new TrialCopyGate(() => 0);

            var first = gate.MarkBroken(Master, "first", ArchSha, CalSha, copyInFlight: true);
            Assert.IsTrue(gate.ClearBrokenIfGeneration(Master, first));
            Assert.IsFalse(gate.IsBroken(Master, out _));

            var old = gate.MarkBroken(Master, "old", ArchSha, CalSha, copyInFlight: true);
            var newer = gate.MarkBroken(Master, "newer");
            Assert.IsTrue(newer > old, "every mark bumps the generation");
            Assert.IsFalse(gate.ClearBrokenIfGeneration(Master, old), "a stale generation clears nothing");
            Assert.IsTrue(gate.IsBroken(Master, out var why));
            Assert.AreEqual("newer", why);
            Assert.IsFalse(gate.ClearBrokenIfGeneration(TrialRoster.MasterSlot(4), newer), "another master is untouched");
        }

        // ================================================================ source pins

        [TestMethod]
        public void Live_reads_stamps_through_GetBiota_doNotAddToCache_and_copies_unpublished_with_the_options()
        {
            var live = Slice("Source/ACE.Server/BalanceTrials/TrialPromote.cs", "internal static Lookups Live() => new Lookups", "private static RepFacts LiveRepFacts(");

            StringAssert.Contains(live, "Stamp = guid => DatabaseManager.Shard.BaseDatabase.GetBiota(guid, true)?.BiotaPropertiesString");
            StringAssert.Contains(live, "AdminCommands.DoCopyChar(admin, rep.Name, rep.Guid, false, masterName, 0, master, onDone, options)");
            StringAssert.Contains(live, "Gate = TrialCopyGate.Shared");

            var facts = Slice("Source/ACE.Server/BalanceTrials/TrialPromote.cs", "private static RepFacts LiveRepFacts(", "// ================================================================ promote");
            StringAssert.Contains(facts, "db.GetCharacterStubByGuid(guid)", "the character row is read through the stub (disposed context, never cached)");
            StringAssert.Contains(facts, "db.GetBiota(guid, true)");

            var promote = Slice("Source/ACE.Server/BalanceTrials/TrialPromote.cs", "internal static bool TryPromote(Session admin, uint? callerGuid", "// ================================================================ unpromote");
            StringAssert.Contains(promote, "Publish = false,");
            StringAssert.Contains(promote, "var stamp = TrialRebuild.PromotedStamp(masterArchetype, masterCalibration, now);");
            StringAssert.Contains(promote, "Normalize(player.Biota, player.GetAllPossessions().Select(p => p.Biota), player.Character, stamp, deleteTime)");

            // The stamp is built after the purge succeeds, from the master's own shas (lineage).
            Assert.IsTrue(promote.IndexOf("if (!lookups.Purge(master))", StringComparison.Ordinal) < promote.IndexOf("var stamp = TrialRebuild.PromotedStamp(", StringComparison.Ordinal));
        }

        [TestMethod]
        public void Trial_command_routes_promote_and_unpromote()
        {
            var handler = Slice("Source/ACE.Server/Command/Handlers/TrialCommands.cs", "public static void HandleTrial(", "private static void Say(");

            StringAssert.Contains(handler, "case \"promote\":");
            StringAssert.Contains(handler, "PromoteReply(session, rest[0], int.Parse(rest[1], System.Globalization.CultureInfo.InvariantCulture))");
            StringAssert.Contains(handler, "case \"unpromote\":");
            StringAssert.Contains(handler, "UnpromoteReply(rest[0])");

            var head = Slice("Source/ACE.Server/Command/Handlers/TrialCommands.cs", "private static bool TryLoadArchetype(", "private static void List(");
            var off = head.IndexOf("TrialStarter.DisabledRefusal()", StringComparison.Ordinal);
            var dir = head.IndexOf("TrialStarter.ResolveDataDir(", StringComparison.Ordinal);
            var unknown = head.IndexOf("unknown archetype", StringComparison.Ordinal);
            Assert.IsTrue(off > 0 && off < dir && dir < unknown, "the command head runs the kill switch, then the data dir, then the archetype lookup");
        }

        private static string Slice(string relative, string start, string end)
        {
            var src = File.ReadAllText(RepoFile(relative));

            var from = src.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(from >= 0, $"not found in {relative}: {start}");
            Assert.AreEqual(from, src.LastIndexOf(start, StringComparison.Ordinal), $"ambiguous in {relative}: {start}");

            var to = src.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.IsTrue(to > from, $"end marker not found after the start in {relative}: {end}");

            return src.Substring(from, to - from);
        }

        private static string RepoFile(string relative)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
        }
    }
}
