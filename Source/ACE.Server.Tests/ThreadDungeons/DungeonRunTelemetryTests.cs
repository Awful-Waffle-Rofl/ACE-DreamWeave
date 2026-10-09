using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The pure half of the Thread run telemetry: the end-state mapping, the placement ledger's
    /// aggregation rule, and the run-to-row projection. Everything covered here is reachable without a
    /// world - the harness cannot construct a live Player, a PropertyManager read throws under it, and an
    /// EnqueueAction body is discarded, so the emitters themselves are out of reach by construction and the
    /// logic they call into is what is tested instead.
    /// </summary>
    [TestClass]
    public class DungeonRunTelemetryTests
    {
        private static ThreadDungeonRun NewRun(params (string Id, double Magnitude)[] modifiers)
        {
            var spec = new DungeonGemSpec("filos_doom", 185, 6, "olthoi", 42, modifiers, 0, 0, presses: 3);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), ownerLevelAtStart: 180);
        }

        private static ThreadDungeonRun NewRunInGroup(string startGroup)
        {
            var spec = new DungeonGemSpec("filos_doom", 185, 6, "olthoi", 42, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, 0x50000001u, "Tester", 7, 0x80000099u, spec, dungeon,
                DateTime.UtcNow, TimeSpan.FromMinutes(180), ownerLevelAtStart: 180, startGroup: startGroup);
        }

        // ---- start group ----

        /// <summary>
        /// The whole point of start_group: several runs from ONE gem use share it, so
        /// COUNT(DISTINCT start_group) counts what the player did while COUNT(*) counts what the server
        /// tried. The retry path that will supply a shared id is not landed yet, so this asserts the
        /// carrying contract directly - if the value did not survive BuildRow unchanged, the divergence
        /// between those two counts would silently be the fault rate plus a bug.
        /// </summary>
        [TestMethod]
        public void A_supplied_start_group_is_carried_through_unchanged()
        {
            var group = ThreadDungeonRun.NewStartGroup();

            var firstAttempt = DungeonRunTelemetry.BuildRow(NewRunInGroup(group), ThreadDungeonRunState.Starting,
                DungeonRunTelemetry.EndReasons.Unwinnable, DateTime.UtcNow, charLevel: 180);
            var secondAttempt = DungeonRunTelemetry.BuildRow(NewRunInGroup(group), ThreadDungeonRunState.Cleared,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, DateTime.UtcNow, charLevel: 180);

            Assert.AreEqual(group, firstAttempt.StartGroup);
            Assert.AreEqual(group, secondAttempt.StartGroup, "both attempts of one gem use group together");
        }

        /// <summary>
        /// The default path mints a FRESH group per run, so with nothing retrying today every run is its own
        /// group of one and the two counts are equal. A shared default would fuse every run on the server
        /// into one group, which is the failure mode that makes the column worse than useless.
        /// </summary>
        [TestMethod]
        public void The_default_path_mints_a_distinct_group_per_run()
        {
            var first = NewRun();
            var second = NewRun();

            Assert.AreNotEqual(first.StartGroup, second.StartGroup);
            Assert.AreEqual(32, first.StartGroup.Length, "start_group is CHAR(32); the N format has no braces or hyphens");
            Assert.IsTrue(first.StartGroup.All(Uri.IsHexDigit), first.StartGroup);

            // Null and empty both mean "mint one", so a caller that forgets degrades to today's behaviour
            // rather than writing a broken key.
            Assert.AreEqual(32, NewRunInGroup(null).StartGroup.Length);
            Assert.AreEqual(32, NewRunInGroup(string.Empty).StartGroup.Length);
        }

        // ---- end-state mapping ----

        /// <summary>
        /// Cleared beats every reason, and that ordering is the whole reason EndRun takes the prior state out
        /// of MarkEnded's own critical section. A run the player finished and then let expire, or that an
        /// admin then ended, is still a clear; filing it as 'expired' would undercount completions by exactly
        /// the runs players walked away from after winning.
        /// </summary>
        [TestMethod]
        public void Cleared_state_wins_over_every_end_reason()
        {
            foreach (var reason in new[]
            {
                DungeonRunTelemetry.EndReasons.Expired,
                DungeonRunTelemetry.EndReasons.LandblockUnloaded,
                DungeonRunTelemetry.EndReasons.LoadFailed,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty,
                DungeonRunTelemetry.EndReasons.Superseded,
                "ended by Admin",
            })
            {
                Assert.AreEqual(DungeonRunTelemetry.EndStates.Cleared,
                    DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Cleared, reason), reason);
            }
        }

        [TestMethod]
        public void Unfinished_runs_map_by_reason()
        {
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Expired,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.Expired));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Abandoned,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.LandblockUnloaded));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Abandoned,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, DungeonRunTelemetry.EndReasons.LoadFailed));

            // The abort family. Three states, not one, because the three causes have three different owners
            // - content, curation, server - and a dashboard that could not separate them measures nothing
            // actionable. All three are reserved; the retry branch is what will write them.
            Assert.AreEqual(DungeonRunTelemetry.EndStates.AbortedUnwinnable,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, DungeonRunTelemetry.EndReasons.Unwinnable));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.AbortedUnderpopulated,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, DungeonRunTelemetry.EndReasons.Underpopulated));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.AbortedTimeout,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, DungeonRunTelemetry.EndReasons.PopulateTimeout));

            // The populate timeout and the landblock load failure are DIFFERENT events and must not fuse:
            // one is the retry loop giving up before the player is let in, the other is the reap of a
            // registered run whose landblock never came up.
            Assert.AreNotEqual(
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, DungeonRunTelemetry.EndReasons.PopulateTimeout),
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, DungeonRunTelemetry.EndReasons.LoadFailed));
        }

        /// <summary>
        /// The /dd admin end builds its reason as "ended by &lt;name&gt;", and that file belongs to another
        /// branch, so the mapping recognises it by PREFIX rather than by a shared constant. If the command's
        /// wording ever changes, this assertion is what fails rather than the runs quietly reclassifying
        /// themselves as abandoned.
        /// </summary>
        [TestMethod]
        public void Admin_end_is_recognised_by_its_prefix()
        {
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Aborted,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, "ended by Morrigan"));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Aborted,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, "ended by A Name With Spaces"));
        }

        /// <summary>
        /// The player closing their own run at the Fragment Press is an abort, in the same state an admin
        /// /dd end produces - but it must get there through its OWN branch, not through the admin prefix and
        /// not through the 'abandoned' default. Both of those are asserted, because either would be silent:
        /// the reason string would still be written, only the state would be wrong.
        ///
        /// No eighth end state was added. The abort family is split by fault owner and a player's own
        /// decision is not a fault; end_reason is the field that separates the two aborts.
        /// </summary>
        [TestMethod]
        public void Closed_by_owner_is_an_abort_in_its_own_right()
        {
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Aborted,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.ClosedByOwner));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Aborted,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, DungeonRunTelemetry.EndReasons.ClosedByOwner));

            Assert.AreNotEqual(DungeonRunTelemetry.EndStates.Abandoned,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.ClosedByOwner),
                "it must not fall through to the least-claiming default");

            Assert.IsFalse(DungeonRunTelemetry.EndReasons.ClosedByOwner.StartsWith(DungeonRunTelemetry.EndReasons.AdminEndPrefix, StringComparison.Ordinal),
                "the reason must not be swallowed by the admin prefix match, or the two aborts could not be told apart");

            // A cleared run stays cleared however it was disposed of, this reason included.
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Cleared,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Cleared, DungeonRunTelemetry.EndReasons.ClosedByOwner));
        }

        /// <summary>
        /// 'closed by member' (owner ruling, 2026-09-17, reversing R7/R32) reaches the SAME end state as 'closed by
        /// owner' through its own branch: both are a deliberate player abort, and which of the two it was is a
        /// question for end_reason, not for the state column. It must not fall through to the 'abandoned' default,
        /// which would file a member's decision as the copy simply going away, and it must not be swallowed by the
        /// admin prefix, which would lose the distinction from staff ending the run.
        /// </summary>
        [TestMethod]
        public void Closed_by_member_shares_the_owner_close_end_state_through_its_own_branch()
        {
            Assert.AreEqual("closed by member", DungeonRunTelemetry.EndReasons.ClosedByMember);
            Assert.AreNotEqual(DungeonRunTelemetry.EndReasons.ClosedByOwner, DungeonRunTelemetry.EndReasons.ClosedByMember,
                "the two closes stay distinguishable on the row");

            Assert.AreEqual(
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.ClosedByOwner),
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.ClosedByMember));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Aborted,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.ClosedByMember));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Aborted,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Starting, DungeonRunTelemetry.EndReasons.ClosedByMember));

            Assert.AreNotEqual(DungeonRunTelemetry.EndStates.Abandoned,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.ClosedByMember),
                "it must not fall through to the least-claiming default");

            Assert.IsFalse(DungeonRunTelemetry.EndReasons.ClosedByMember.StartsWith(DungeonRunTelemetry.EndReasons.AdminEndPrefix, StringComparison.Ordinal));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Cleared,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Cleared, DungeonRunTelemetry.EndReasons.ClosedByMember));
        }

        /// <summary>
        /// The default is 'abandoned', the bucket that claims the least. A reason nobody taught the mapping
        /// about is far more likely to be the copy going away than a player decision, and reading it as
        /// 'aborted' would put a deliberate meaning on a mechanical event. Null must not throw either: EndRun
        /// takes free text.
        /// </summary>
        [TestMethod]
        public void Unknown_and_null_reasons_fall_back_to_abandoned()
        {
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Abandoned,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, "something nobody has written yet"));

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Abandoned,
                DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Active, null));
        }

        /// <summary>Every value the mapping can produce is in the closed set the column documents.</summary>
        [TestMethod]
        public void Every_mapped_state_is_in_the_closed_set()
        {
            var reasons = new[]
            {
                DungeonRunTelemetry.EndReasons.Expired,
                DungeonRunTelemetry.EndReasons.LandblockUnloaded,
                DungeonRunTelemetry.EndReasons.LoadFailed,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty,
                DungeonRunTelemetry.EndReasons.Superseded,
                DungeonRunTelemetry.EndReasons.Unwinnable,
                DungeonRunTelemetry.EndReasons.Underpopulated,
                DungeonRunTelemetry.EndReasons.PopulateTimeout,
                DungeonRunTelemetry.EndReasons.ClosedByOwner,
                DungeonRunTelemetry.EndReasons.ClosedByMember,
                "ended by Someone",
                "unmapped",
                null,
            };

            Assert.AreEqual(7, DungeonRunTelemetry.AllEndStates.Length, "end_state is a closed set of seven");
            Assert.AreEqual(7, DungeonRunTelemetry.AllEndStates.Distinct().Count(), "end states must be distinct");

            foreach (var state in Enum.GetValues<ThreadDungeonRunState>())
            {
                foreach (var reason in reasons)
                {
                    var mapped = DungeonRunTelemetry.EndStateFor(state, reason);
                    CollectionAssert.Contains(DungeonRunTelemetry.AllEndStates, mapped, $"{state} / {reason ?? "null"} -> {mapped}");
                }
            }
        }

        /// <summary>
        /// anchor_fallback_used is the ONE code that records a recovery rather than a loss, and a panel that
        /// summed attempts without filtering on is_failure would count it as one of the failures it
        /// recovered from.
        /// </summary>
        [TestMethod]
        public void Only_anchor_fallback_is_not_a_failure()
        {
            Assert.AreEqual(9, DungeonRunTelemetry.AllReasons.Length);
            Assert.AreEqual(9, DungeonRunTelemetry.AllReasons.Distinct().Count(), "reason codes must be distinct");

            foreach (var reason in DungeonRunTelemetry.AllReasons)
            {
                var expected = reason != DungeonRunTelemetry.Reasons.AnchorFallbackUsed;
                Assert.AreEqual(expected, DungeonRunTelemetry.IsFailure(reason), reason);
            }
        }

        // ---- placement ledger ----

        /// <summary>
        /// The aggregation rule: the SAME (reason, wcid, role) triple twice is one entry with attempts = 2,
        /// not two entries. Without it a pack of identical trash creatures failing the same way would fill
        /// the child table with near-duplicate rows.
        /// </summary>
        [TestMethod]
        public void Repeating_a_triple_increments_attempts_rather_than_adding_a_row()
        {
            var run = NewRun();

            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, 12345, DungeonRole.Trash);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, 12345, DungeonRole.Trash);

            var snapshot = run.SnapshotPlacements();

            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual(2, snapshot[0].Attempts);
            Assert.AreEqual(DungeonRunTelemetry.Reasons.EnterWorldRefused, snapshot[0].Reason);
            Assert.AreEqual(12345u, snapshot[0].Wcid);
            Assert.AreEqual(DungeonRole.Trash, snapshot[0].Role);
            Assert.IsTrue(snapshot[0].IsFailure);
            Assert.IsFalse(snapshot[0].EntryPlaced);
        }

        /// <summary>Each of the four key parts on its own is enough to separate two entries.</summary>
        [TestMethod]
        public void Differing_phase_reason_wcid_or_role_are_separate_entries()
        {
            var run = NewRun();

            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, 12345, DungeonRole.Trash);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.CreateThrew, 12345, DungeonRole.Trash);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, 99999, DungeonRole.Trash);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, 12345, DungeonRole.Elite);

            var snapshot = run.SnapshotPlacements();

            Assert.AreEqual(4, snapshot.Count);
            Assert.IsTrue(snapshot.All(p => p.Attempts == 1));
        }

        /// <summary>
        /// Phase is part of the aggregation KEY, not a decoration. Boss killability checking moves to plan
        /// time on an in-flight branch, at which point the same reason string is emitted by both the builder
        /// and the spawner meaning different things - at plan time the content roster is bad, at place time
        /// the world refused a creature - with different owners and different fixes. If phase were carried
        /// alongside the key instead of inside it, those two would merge into one row and the distinction
        /// would be gone for good.
        /// </summary>
        [TestMethod]
        public void The_same_reason_at_two_phases_stays_two_rows()
        {
            var run = NewRun();

            run.RecordPlacement(DungeonRunTelemetry.Phases.Plan, DungeonRunTelemetry.Reasons.NotKillable, 12345, DungeonRole.Boss);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.NotKillable, 12345, DungeonRole.Boss);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.NotKillable, 12345, DungeonRole.Boss);

            var snapshot = run.SnapshotPlacements();

            Assert.AreEqual(2, snapshot.Count, "identical reason, wcid and role, but two phases");

            var planRow = snapshot.Single(p => p.Phase == DungeonRunTelemetry.Phases.Plan);
            var placeRow = snapshot.Single(p => p.Phase == DungeonRunTelemetry.Phases.Place);

            Assert.AreEqual(1, planRow.Attempts);
            Assert.AreEqual(2, placeRow.Attempts, "and each phase aggregates on its own");
        }

        /// <summary>
        /// A brand new run has an empty ledger, so a healthy run contributes no child rows at all rather
        /// than a row saying nothing went wrong.
        /// </summary>
        [TestMethod]
        public void A_run_with_no_failures_has_an_empty_ledger()
        {
            Assert.AreEqual(0, NewRun().SnapshotPlacements().Count);
        }

        /// <summary>
        /// MarkEntryPlaced touches RECOVERY rows only. Terminal failure codes destroy the creature and end
        /// the plan entry, so crediting one because a SIBLING entry of the same wcid and role placed
        /// elsewhere - the common case, since a pack draws the same wcid many times - would be a lie, and
        /// would break "is_failure = 1 AND entry_placed = 0" as a count of creatures the run never got.
        /// </summary>
        [TestMethod]
        public void Marking_an_entry_placed_updates_recovery_rows_and_never_terminal_ones()
        {
            var run = NewRun();

            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.AnchorFallbackUsed, 12345, DungeonRole.Trash);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, 12345, DungeonRole.Trash);

            run.MarkEntryPlaced(12345, DungeonRole.Trash);

            var byReason = run.SnapshotPlacements().ToDictionary(p => p.Reason);

            Assert.IsTrue(byReason[DungeonRunTelemetry.Reasons.AnchorFallbackUsed].EntryPlaced, "the recovery row records that the retry worked");
            Assert.IsFalse(byReason[DungeonRunTelemetry.Reasons.AnchorFallbackUsed].IsFailure);
            Assert.IsFalse(byReason[DungeonRunTelemetry.Reasons.EnterWorldRefused].EntryPlaced, "a terminal refusal is never retroactively rescued");
        }

        /// <summary>A different wcid or role placing leaves this entry's ledger row alone.</summary>
        [TestMethod]
        public void Marking_a_different_wcid_or_role_leaves_the_entry_alone()
        {
            var run = NewRun();

            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.AnchorFallbackUsed, 12345, DungeonRole.Trash);

            run.MarkEntryPlaced(99999, DungeonRole.Trash);
            run.MarkEntryPlaced(12345, DungeonRole.Boss);

            Assert.IsFalse(run.SnapshotPlacements().Single().EntryPlaced);
        }

        // ---- earnings counters ----

        /// <summary>
        /// The two earnings counters are independent and additive. They sit outside the run's stateLock, so
        /// what matters is only that each accumulates its own total and neither leaks into the other.
        /// </summary>
        [TestMethod]
        public void Xp_and_luminance_accumulate_independently()
        {
            var run = NewRun();

            Assert.AreEqual(0L, run.XpEarned);
            Assert.AreEqual(0L, run.LumEarned);

            run.AddXp(1000);
            run.AddXp(250);
            run.AddLum(40);

            Assert.AreEqual(1250L, run.XpEarned);
            Assert.AreEqual(40L, run.LumEarned);
        }

        // ---- run-to-row projection ----

        /// <summary>
        /// The projection carries every column the row promises, off a run driven through a realistic
        /// lifecycle. The two boss wcid columns are the load-bearing pair: intended is what the plan chose,
        /// placed is what reached the world, and only their difference distinguishes a boss that failed to
        /// place from a plan that never had one.
        /// </summary>
        [TestMethod]
        public void Projection_carries_the_run_onto_its_row()
        {
            var run = NewRun(("boss_guarded", 1.5), ("salvage_affinity", 0.25));

            run.MarkPopulateReached();
            run.MarkPopulated(planned: 10, spawned: 8, bossWcid: 0u, bossWcidIntended: 41229u);
            run.RecordBossHealth(1.75, clamped: true);
            run.RecordKill(isBoss: false);
            run.RecordKill(isBoss: false);
            run.MarkPlayerObserved();
            run.MarkSurveyFiled();
            run.AddXp(123456);
            run.AddLum(789);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, 12345, DungeonRole.Trash);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Place, DungeonRunTelemetry.Reasons.EnterWorldRefused, 12345, DungeonRole.Trash);
            run.RecordPlacement(DungeonRunTelemetry.Phases.Plan, DungeonRunTelemetry.Reasons.NoCandidate, 0, DungeonRole.Boss);

            var ended = run.StartedUtc.AddSeconds(600);
            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active, DungeonRunTelemetry.EndReasons.Expired, ended, charLevel: 199);

            Assert.AreEqual(run.RunId, row.RunId);
            Assert.AreEqual(run.StartedUtc, row.StartedUtc);
            Assert.AreEqual(ended, row.EndedUtc);
            Assert.AreEqual(600.0, row.DurationSecs, 1e-6);
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Expired, row.EndState);
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.Expired, row.EndReason);
            Assert.IsTrue(row.Entered);
            Assert.AreEqual(0x50000001u, row.CharacterId);
            Assert.AreEqual("Tester", row.Name);
            Assert.AreEqual(180, row.CharLevelStart, "the level the player walked in with");
            Assert.AreEqual(199, row.CharLevel, "and the level they finished at");
            Assert.AreEqual("filos_doom", row.DungeonId);
            Assert.AreEqual(185, row.GemLevel);
            Assert.AreEqual(6, row.Tier);
            Assert.AreEqual("olthoi", row.Family);
            // The instability mechanic was removed (owner ruling, 2026-09-07) but the ace_analytics column
            // survives, because that schema freezes on first deploy and has no ALTER path. Pinned at 0 so a
            // future writer cannot quietly start filling a retired column again without this test noticing.
            Assert.AreEqual(0, row.Instability, "the retired instability column must be written as 0");
            Assert.AreEqual(3, row.Presses);
            Assert.AreEqual(42, row.Seed, "the reproduction key; retrying re-seeds the gem and destroys it");
            Assert.AreEqual(0, row.StartAttempts, "start_attempts is reserved for the gem-reroll design and has no writer");
            Assert.IsTrue(row.PopulateReached);
            Assert.IsNotNull(row.PopulateMs, "population completed, so it has a duration");
            Assert.IsTrue(row.PopulateMs >= 0, $"populate_ms was {row.PopulateMs}");
            Assert.AreEqual(10, row.Planned);
            Assert.AreEqual(8, row.Spawned);
            Assert.AreEqual(2, row.Killed);
            Assert.AreEqual(run.ClearTarget, row.ClearTarget);
            Assert.AreEqual(41229u, row.BossWcidIntended, "the plan drew a boss");
            Assert.AreEqual(0u, row.BossWcidPlaced, "and it failed to place");
            Assert.IsFalse(row.BossKilled);
            Assert.AreEqual(1.75, row.BossHealthRatio, 1e-9);
            Assert.IsTrue(row.BossHealthClamped);
            Assert.IsTrue(row.SurveyFiled);
            Assert.AreEqual(0, row.CreditedKills, "credited_kills is reserved and has no writer");
            Assert.AreEqual(123456L, row.XpGained);
            Assert.AreEqual(789L, row.LumGained);

            CollectionAssert.AreEquivalent(new[] { "boss_guarded", "salvage_affinity" }, row.Modifiers.Select(m => m.ModifierId).ToList());
            Assert.AreEqual(1.5, row.Modifiers.Single(m => m.ModifierId == "boss_guarded").Magnitude, 1e-9);

            Assert.AreEqual(2, row.Placements.Count);

            var refused = row.Placements.Single(p => p.Reason == DungeonRunTelemetry.Reasons.EnterWorldRefused);
            Assert.AreEqual(DungeonRunTelemetry.Phases.Place, refused.Phase);
            Assert.AreEqual(2, refused.Attempts);
            Assert.IsTrue(refused.IsFailure);
            Assert.IsFalse(refused.EntryPlaced);
            Assert.IsFalse(refused.Credited, "credited is reserved and has no writer");
            Assert.AreEqual("Trash", refused.Role);

            var noCandidate = row.Placements.Single(p => p.Reason == DungeonRunTelemetry.Reasons.NoCandidate);
            Assert.AreEqual(DungeonRunTelemetry.Phases.Plan, noCandidate.Phase, "decided by the builder, before any object existed");
            Assert.AreEqual(0u, noCandidate.Wcid, "no_candidate carries no wcid; there was none to try");
            Assert.AreEqual("Boss", noCandidate.Role);
        }

        /// <summary>
        /// A run whose copy never came up projects cleanly, and populate_reached false is the ONLY thing
        /// that distinguishes it from a run whose plan ran and placed nothing - both report spawned = 0, and
        /// they are entirely different faults.
        /// </summary>
        [TestMethod]
        public void A_run_that_never_populated_projects_zeroes()
        {
            var run = NewRun();
            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Starting,
                DungeonRunTelemetry.EndReasons.LoadFailed, run.StartedUtc.AddSeconds(60), charLevel: 0);

            Assert.AreEqual(DungeonRunTelemetry.EndStates.Abandoned, row.EndState);
            Assert.IsFalse(row.PopulateReached, "nothing was ever attempted");
            Assert.IsNull(row.PopulateMs, "no duration, so NULL rather than a sentinel that would drag AVG toward zero");
            Assert.AreEqual(0, row.StartAttempts);
            Assert.AreEqual(0, row.Planned);
            Assert.AreEqual(0, row.Spawned);
            Assert.AreEqual(0, row.Killed);
            Assert.AreEqual(0u, row.BossWcidIntended);
            Assert.AreEqual(0u, row.BossWcidPlaced);
            Assert.IsFalse(row.Entered);
            Assert.IsFalse(row.SurveyFiled);
            Assert.AreEqual(0, row.Modifiers.Count);
            Assert.AreEqual(0, row.Placements.Count);
        }

        /// <summary>
        /// The three-outcome invariant the boss wcid columns document, driven through the REAL builder
        /// rather than by hand: a gem whose level band matches no species table and whose curated boss row
        /// falls outside the window leaves the plan with no boss to name, so boss_wcid_intended is 0 exactly
        /// as it is for a genuinely bossless dungeon. The no_candidate ledger row is the ONLY thing that
        /// separates the two, which is why it is asserted here and not merely exercised.
        /// </summary>
        [TestMethod]
        public void No_candidate_leaves_both_boss_wcids_zero_and_is_visible_only_in_the_ledger()
        {
            // Level 250 puts the boss window at [250, 400], which the level-110 curated row misses, and puts
            // the trash band out of reach of the only species table - so there is no family to promote from
            // either. The dungeon DOES have a boss anchor, so a boss was genuinely wanted.
            var spec = new DungeonGemSpec("filos_doom", 250, 6, "any", 1, new (string, double)[0], 0, 0);
            var plan = DungeonPopulationBuilder.Build(spec, PlanDungeon(), PlanStore(), PlanSpecies(), PlanLevelOf,
                w => 0u, new DungeonPopulationLimits(120, 2.0, 3.0, 8, 0.5, bossAnyAllBands: false), new Random(3));

            // bossAnyAllBands OFF: with the shipped default ON, the level-110 ANY-family row is eligible at every
            // band (spec D), so a boss would be fielded and the no-candidate outcome this test pins cannot arise.

            Assert.IsTrue(plan.BossNoCandidate, "a boss was wanted and none could be fielded");
            Assert.AreEqual(0u, plan.BossWcid, "so the plan never named one");

            // Exactly what ThreadDungeonSpawner.TryPopulate does with that plan.
            var run = NewRun();
            if (plan.BossNoCandidate)
                run.RecordPlacement(DungeonRunTelemetry.Phases.Plan, DungeonRunTelemetry.Reasons.NoCandidate, 0, DungeonRole.Boss);
            run.MarkPopulated(plan.Entries.Count, spawned: 0, bossWcid: 0u, bossWcidIntended: plan.BossWcid);

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.Expired, run.StartedUtc.AddSeconds(30), charLevel: 250);

            Assert.AreEqual(0u, row.BossWcidIntended, "indistinguishable from a bossless dungeon by the wcids alone");
            Assert.AreEqual(0u, row.BossWcidPlaced);

            var ledgerRow = row.Placements.Single();
            Assert.AreEqual(DungeonRunTelemetry.Reasons.NoCandidate, ledgerRow.Reason, "and the ledger row is what tells them apart");
            Assert.AreEqual(0u, ledgerRow.Wcid);
            Assert.AreEqual("Boss", ledgerRow.Role);
            Assert.IsTrue(ledgerRow.IsFailure);
        }

        /// <summary>
        /// dungeon_run_modifier is keyed on (run_fk, modifier_id), so two rows sharing an id would throw on
        /// insert and roll back the whole batch - losing every UNRELATED run in it, not just this one.
        /// DungeonGemSpec.TryParse refuses a repeated id but its constructor does not, so a spec built in
        /// code can carry one and the projection has to be the guard.
        /// </summary>
        [TestMethod]
        public void Duplicate_modifier_ids_collapse_to_one_row()
        {
            var run = NewRun(("hardy", 1.3), ("hardy", 2.0), ("teeming", 1.25));
            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.Expired, run.StartedUtc.AddSeconds(30), charLevel: 185);

            Assert.AreEqual(2, row.Modifiers.Count);
            Assert.AreEqual(1, row.Modifiers.Count(m => m.ModifierId == "hardy"));
            Assert.AreEqual(1.3, row.Modifiers.Single(m => m.ModifierId == "hardy").Magnitude, 1e-9, "first wins");
            Assert.AreEqual(1.25, row.Modifiers.Single(m => m.ModifierId == "teeming").Magnitude, 1e-9);
        }

        // Fixtures for the one test that drives the real DungeonPopulationBuilder. Deliberately local rather
        // than shared with DungeonPopulationBuilderTests: that class owns its fixtures for its own
        // assertions, and coupling the two would let a change made for one silently reshape the other.

        private static readonly Dictionary<uint, int> PlanLevels = new Dictionary<uint, int> { [100] = 100, [110] = 130, [10981] = 110 };
        private static int PlanLevelOf(uint w) => PlanLevels[w];

        private static DungeonSpawnPointDef PlanPoint(int i) => new DungeonSpawnPointDef { Cell = 0x01500100u + (uint)i, X = i, Y = 0, Z = 0, Clearance = 2, Curated = true };

        private static DungeonEntryDef PlanDungeon() => new DungeonEntryDef
        {
            Id = "filos_doom", Landblock = 0x0150, Name = "Filos' Doom", ExitPortalWcid = 1003601,
            Points = Enumerable.Range(1, 4).Select(PlanPoint).ToList(),
            BossAnchor = PlanPoint(99),
            CreatureTypes = new List<string> { "Banderling" },
        };

        private static Dictionary<string, SpeciesTableDef> PlanSpecies() => new Dictionary<string, SpeciesTableDef>
        {
            ["banderling"] = new SpeciesTableDef
            {
                Id = "banderling", CreatureType = "Banderling",
                Members = new List<SpeciesMemberDef>
                {
                    new SpeciesMemberDef { Wcid = 100, Role = 0 },
                    new SpeciesMemberDef { Wcid = 110, Role = 1 },
                }
            }
        };

        private static ThreadDungeonStore PlanStore() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}",
            "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":[],\"modifiers\":[]}]}",
            "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500},{\"level\":100,\"xp\":80000},{\"level\":200,\"xp\":1100000}]}",
            new Dictionary<string, string>());

        /// <summary>
        /// Every string column is fixed width and MySQL in strict mode ERRORS on an overlong value rather
        /// than truncating it, which would roll back the whole batch and lose every run in it. The projection
        /// clips instead, the same choice RecordChat makes for chat_event.message.
        /// </summary>
        [TestMethod]
        public void Overlong_strings_are_clipped_to_their_column_width()
        {
            Assert.AreEqual(new string('x', 64), DungeonRunTelemetry.Clip(new string('x', 200), 64));
            Assert.AreEqual("short", DungeonRunTelemetry.Clip("short", 64));
            Assert.AreEqual(string.Empty, DungeonRunTelemetry.Clip(null, 64));

            var run = NewRun();
            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active, new string('r', 300), DateTime.UtcNow, 1);

            Assert.AreEqual(64, row.EndReason.Length, "end_reason is VARCHAR(64)");
        }

        // ---- dungeon_run_detail: DungeonRunPlanRecord / MarkPlan / the detail projection ----

        private static DungeonSpawnPlan FullPlan(string familyId = "olthoi")
        {
            var plan = new DungeonSpawnPlan
            {
                BossWcid = 41229,
                FamilyId = familyId,
                HealthMultiplier = 1.4,
                BossHealthMultiplier = 1.6,
                TrashHealthFloor = 300,
                HealthNormalizeRatio = 0.75,
                HealthCurveTarget = 900.5,
                NaturalBandLow = 165,
                EffectiveBandLow = 140,
                DamageRating = 10,
                CritRating = 5,
                CritDamageRating = 15,
                DamageResistRating = 20,
                BossDamageRating = 30,
                BossDamageResistRating = 40,
                BossLevel = 236,
                RunSpeedMult = 1.1,
                IgnoreShield = 0.25,
                HollowIntensity = 0.55,
                XpMultiplier = 2.0,
                BossXpMultiplier = 1.5,
                LumMultiplier = 1.9,
                XpScale = 2.0,
                LumScale = 2.0,
                LootQuantityMult = 1.3,
                BossNormalize = true,
                PoolMaxBase = 591,
                BossHealthBase = 3544,
                StripCombatTraits = true,
                Profile = new TreasureDeath { Tier = 7 },
                BandStandard = DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), 0, 0, sampleCount: 12),
            };

            var point = new DungeonSpawnPointDef { Cell = 0x01500100u, X = 0, Y = 0, Z = 0, Clearance = 2, Curated = true };

            plan.Entries.Add(new DungeonSpawnPlanEntry(100, DungeonRole.Trash, point));
            plan.Entries.Add(new DungeonSpawnPlanEntry(101, DungeonRole.Trash, point, upliftLevel: 200));
            plan.Entries.Add(new DungeonSpawnPlanEntry(102, DungeonRole.Elite, point));
            plan.Entries.Add(new DungeonSpawnPlanEntry(41229, DungeonRole.Boss, point));

            return plan;
        }

        /// <summary>
        /// Every field BuildPlanRecord derives from a real, fully-populated plan lands on the detail row
        /// unchanged, including the two counted-not-carried fields (trash_planned/elite_planned, the same
        /// "filter Entries by an existing field" footing as uplifted_planned) and the log line's own
        /// arithmetic for band_standard_samples (BandStandard.SampleCount) and loot_tier (Profile.Tier).
        /// </summary>
        [TestMethod]
        public void A_plan_record_projects_correctly_onto_the_detail_row()
        {
            var plan = FullPlan();
            var run = NewRun();

            run.MarkPlan(DungeonRunTelemetry.BuildPlanRecord(plan));

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.Expired, run.StartedUtc.AddSeconds(30), charLevel: 185);

            var d = row.Detail;
            Assert.IsNotNull(d);
            Assert.AreEqual("live", d.Source);
            Assert.IsTrue(d.PlanBuilt);
            Assert.AreEqual("olthoi", d.FamilyId);
            Assert.AreEqual(2, d.TrashPlanned, "two Trash-role entries");
            Assert.AreEqual(1, d.ElitePlanned, "one Elite-role entry");
            Assert.AreEqual(4, d.EntriesPlanned, "trash + elite + boss");
            Assert.AreEqual(1, d.UpliftedPlanned, "one entry with UpliftLevel > 0");
            Assert.AreEqual(165, d.NaturalBandLow);
            Assert.AreEqual(140, d.EffectiveBandLow);
            Assert.AreEqual(236, d.BossLevel);
            Assert.AreEqual(true, d.BossNormalize);
            Assert.AreEqual(3544.0, d.BossHealthBase.Value, 1e-9);
            Assert.AreEqual(591u, d.PoolMaxBase);
            Assert.AreEqual(1.4, d.HealthMultiplier.Value, 1e-9);
            Assert.AreEqual(1.6, d.BossHealthMultiplier.Value, 1e-9);
            Assert.AreEqual(900.5, d.HealthCurveTarget.Value, 1e-9);
            Assert.AreEqual(0.75, d.HealthNormalizeRatio.Value, 1e-9);
            Assert.AreEqual(300u, d.TrashHealthFloor);
            Assert.AreEqual(12, d.BandStandardSamples);
            Assert.AreEqual(10, d.DamageRating);
            Assert.AreEqual(5, d.CritRating);
            Assert.AreEqual(15, d.CritDamageRating);
            Assert.AreEqual(20, d.DamageResistRating);
            Assert.AreEqual(30, d.BossDamageRating);
            Assert.AreEqual(40, d.BossDamageResistRating);
            Assert.AreEqual(1.1, d.RunSpeedMult.Value, 1e-9);
            Assert.AreEqual(0.25, d.IgnoreShield.Value, 1e-9);
            Assert.AreEqual(0.55, d.HollowIntensity.Value, 1e-9);
            Assert.AreEqual(true, d.StripCombatTraits);
            Assert.AreEqual(2.0, d.XpMultiplier.Value, 1e-9);
            Assert.AreEqual(1.5, d.BossXpMultiplier.Value, 1e-9);
            Assert.AreEqual(1.9, d.LumMultiplier.Value, 1e-9);
            Assert.AreEqual(2.0, d.XpScale.Value, 1e-9);
            Assert.AreEqual(2.0, d.LumScale.Value, 1e-9);
            Assert.AreEqual(7, d.LootTier);
            Assert.AreEqual(1.3, d.LootQuantityMult.Value, 1e-9);
        }

        /// <summary>
        /// A run whose population threw before a plan was ever built (MarkPlan never called) still gets a
        /// detail row - dungeon_run_detail is 1:1 and always present - but plan_built is false and every
        /// plan-derived column is null rather than a zero sentinel.
        /// </summary>
        [TestMethod]
        public void A_run_with_no_plan_still_gets_a_detail_row_with_null_plan_columns()
        {
            var run = NewRun();
            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Starting,
                DungeonRunTelemetry.EndReasons.LoadFailed, run.StartedUtc.AddSeconds(5), charLevel: 0);

            var d = row.Detail;
            Assert.IsNotNull(d, "dungeon_run_detail is 1:1 with dungeon_run and always present");
            Assert.AreEqual("live", d.Source);
            Assert.IsFalse(d.PlanBuilt);
            Assert.IsNull(d.FamilyId);
            Assert.IsNull(d.TrashPlanned);
            Assert.IsNull(d.ElitePlanned);
            Assert.IsNull(d.EntriesPlanned);
            Assert.IsNull(d.UpliftedPlanned);
            Assert.IsNull(d.BossLevel);
            Assert.IsNull(d.BossNormalize);
            Assert.IsNull(d.HealthMultiplier);
            Assert.IsNull(d.HollowIntensity);
            Assert.IsNull(d.StripCombatTraits);
            Assert.IsNull(d.LootTier);
            Assert.IsNull(d.LootQuantityMult);
        }

        /// <summary>
        /// First-write-wins: a second MarkPlan call must not replace the record a run has already latched,
        /// on the same CompareExchange-against-null contract MarkPopulateReached's timestamp uses.
        /// </summary>
        [TestMethod]
        public void MarkPlan_is_write_once()
        {
            var run = NewRun();

            run.MarkPlan(DungeonRunTelemetry.BuildPlanRecord(FullPlan("olthoi")));
            Assert.AreEqual("olthoi", run.PlanRecord.FamilyId);

            run.MarkPlan(DungeonRunTelemetry.BuildPlanRecord(FullPlan("tusker")));
            Assert.AreEqual("olthoi", run.PlanRecord.FamilyId, "the second call must not overwrite the first");

            run.MarkPlan(null);
            Assert.AreEqual("olthoi", run.PlanRecord.FamilyId, "a null record is ignored, not latched");
        }

        /// <summary>
        /// cleared_secs is null until the run actually clears, and then reads ClearedUtc - StartedUtc, not
        /// dungeon_run.duration_secs' start-to-END time.
        /// </summary>
        [TestMethod]
        public void Cleared_secs_is_null_until_cleared_then_computes_correctly()
        {
            var run = NewRun();

            var beforeClear = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.Expired, run.StartedUtc.AddSeconds(30), charLevel: 185);
            Assert.IsNull(beforeClear.Detail.ClearedSecs, "the run has not cleared yet");

            run.MarkPopulated(planned: 1, spawned: 1, bossWcid: 0);
            run.RecordKill(isBoss: false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.IsNotNull(run.ClearedUtc);

            var expectedSecs = (run.ClearedUtc.Value - run.StartedUtc).TotalSeconds;

            // EndedUtc deliberately differs from ClearedUtc, so a bug that read duration_secs (start-to-end)
            // instead of ClearedUtc - StartedUtc would show up as a mismatch rather than an accidental match.
            var afterClear = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Cleared,
                DungeonRunTelemetry.EndReasons.ClearedAndEmpty, run.StartedUtc.AddSeconds(600), charLevel: 185);

            Assert.IsNotNull(afterClear.Detail.ClearedSecs);
            Assert.AreEqual(expectedSecs, afterClear.Detail.ClearedSecs.Value, 1e-6);
            Assert.AreNotEqual(600.0, afterClear.Detail.ClearedSecs.Value, "must not be duration_secs (start-to-end)");
        }

        /// <summary>
        /// family_id is clipped to 32 chars, the same fixed-width contract every other string column here
        /// uses - MySQL in strict mode errors on an overlong value rather than truncating it.
        /// </summary>
        [TestMethod]
        public void Family_id_longer_than_32_chars_is_clipped()
        {
            var plan = FullPlan(new string('f', 50));
            var run = NewRun();
            run.MarkPlan(DungeonRunTelemetry.BuildPlanRecord(plan));

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.Expired, run.StartedUtc.AddSeconds(30), charLevel: 185);

            Assert.AreEqual(32, row.Detail.FamilyId.Length);
            Assert.AreEqual(new string('f', 32), row.Detail.FamilyId);
        }

        /// <summary>
        /// A plan that drew no family (plan.FamilyId == null, the "no family" case ThreadDungeonSpawner
        /// logs as "family=none") must produce a real NULL family_id, never the empty string. Clip() turns
        /// null into "" for every OTHER string column here - by design, since MySQL strict mode errors on
        /// overlong rather than truncating - but family_id must stay a true SQL NULL so "no family" is
        /// distinguishable from "an empty-string family" and so KEY ix_family and any GROUP BY family_id
        /// panel treat it consistently with the backfill script's own 'none' -> NULL mapping.
        /// </summary>
        [TestMethod]
        public void A_plan_with_no_family_gives_a_null_family_id_not_empty_string()
        {
            var plan = FullPlan(familyId: null);
            var run = NewRun();
            run.MarkPlan(DungeonRunTelemetry.BuildPlanRecord(plan));

            var row = DungeonRunTelemetry.BuildRow(run, ThreadDungeonRunState.Active,
                DungeonRunTelemetry.EndReasons.Expired, run.StartedUtc.AddSeconds(30), charLevel: 185);

            Assert.IsNull(row.Detail.FamilyId, "no family must round-trip to a real NULL, not an empty string");
        }
    }
}
