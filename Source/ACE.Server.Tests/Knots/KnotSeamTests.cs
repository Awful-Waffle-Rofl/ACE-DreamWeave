using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Knots;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// The pure decisions of the Pattern-fill and Knot-entry seam: every refusal and success branch of
    /// PatternFill.Decide and KnotUse.Decide, the Knot state helpers, and the encounter registry. The Gem and
    /// Player glue (KnotSeam, KnotEncounterEvents) needs a live Player and is covered by the in-game checks.
    /// </summary>
    [TestClass]
    public class KnotSeamTests
    {
        private static PinnacleDef Def(string key, string kind = "test", int entries = 3, bool enabled = true)
            => new PinnacleDef
            {
                Key = key,
                Name = key + " name",
                Destination = new PinnacleDestinationDef { Kind = kind },
                Entries = entries,
                Enabled = enabled,
            };

        private sealed class FakeEncounter : IPinnacleEncounter
        {
            public FakeEncounter(string kind) { Kind = kind; }
            public string Kind { get; }
            public bool TryEnter(Player player, WorldObject knot, PinnacleDef def, int knotLevel, int phase, out string refusal) { refusal = null; return true; }
        }

        [TestCleanup]
        public void Cleanup() => PinnacleEncounters.Clear();

        // ---- PatternFill.Decide ----

        [TestMethod]
        public void Fill_TargetNotInPack_Refused()
        {
            var d = PatternFill.Decide(false, true, 100, 0, 0, Def("a"), true);
            Assert.IsFalse(d.Accepted);
            Assert.AreEqual(KnotItems.PatternNeedsFiber, d.Refusal);
        }

        [TestMethod]
        public void Fill_TargetNotFiber_Refused()
        {
            var d = PatternFill.Decide(true, false, 100, 0, 0, Def("a"), true);
            Assert.IsFalse(d.Accepted);
            Assert.AreEqual(KnotItems.PatternNeedsFiber, d.Refusal);
        }

        [TestMethod]
        public void Fill_MissingPinnacle_Refused()
        {
            var d = PatternFill.Decide(true, true, 100, 0, 0, null, true);
            Assert.IsFalse(d.Accepted);
            Assert.AreEqual(KnotItems.PinnacleDisabled, d.Refusal);
        }

        [TestMethod]
        public void Fill_DisabledPinnacle_Refused()
        {
            var d = PatternFill.Decide(true, true, 100, 0, 0, Def("a", enabled: false), true);
            Assert.IsFalse(d.Accepted);
            Assert.AreEqual(KnotItems.PinnacleDisabled, d.Refusal);
        }

        [TestMethod]
        public void Fill_EnabledPinnacleWithoutEncounter_Refused_AndTakesNothing()
        {
            var d = PatternFill.Decide(true, true, 100, 0, 0, Def("a"), false);
            Assert.IsFalse(d.Accepted);
            Assert.IsFalse(d.TakesFiber);
            Assert.AreEqual(KnotItems.NoEncounterRegistered, d.Refusal);
        }

        [TestMethod]
        public void Fill_FirstFiber_StartsTheAverage()
        {
            var d = PatternFill.Decide(true, true, 120, 0, 0, Def("a"), true);
            Assert.IsTrue(d.Accepted);
            Assert.IsNull(d.Refusal);
            Assert.IsTrue(d.TakesFiber);
            Assert.AreEqual(1, d.NewCount);
            Assert.AreEqual(120, d.NewSum);
            Assert.AreEqual(120, d.NewAverage);
            Assert.IsFalse(d.Completes);
        }

        [TestMethod]
        public void Fill_AverageFloorsAcrossMixedLevels()
        {
            // 100 then 101: sum 201, 201 / 2 = 100.5 -> 100 (floor, not round)
            var first = PatternFill.Decide(true, true, 100, 0, 0, Def("a"), true);
            var second = PatternFill.Decide(true, true, 101, first.NewCount, first.NewSum, Def("a"), true);
            Assert.AreEqual(2, second.NewCount);
            Assert.AreEqual(201, second.NewSum);
            Assert.AreEqual(100, second.NewAverage);

            // a third at 102: sum 303 / 3 = 101
            var third = PatternFill.Decide(true, true, 102, second.NewCount, second.NewSum, Def("a"), true);
            Assert.AreEqual(101, third.NewAverage);
        }

        [TestMethod]
        public void Fill_CompletesExactlyAtTen_AndAveragesTheTen()
        {
            var count = 0;
            var sum = 0;
            PatternFillDecision d = default;

            // nine at 300 and a tenth at 309: 3009 / 10 = 300.9 -> 300
            for (var i = 1; i <= KnotItems.FibersPerKnot; i++)
            {
                d = PatternFill.Decide(true, true, i == KnotItems.FibersPerKnot ? 309 : 300, count, sum, Def("a"), true);
                Assert.IsTrue(d.Accepted);
                Assert.AreEqual(i, d.NewCount);
                Assert.AreEqual(i == KnotItems.FibersPerKnot, d.Completes, $"fiber {i}");
                count = d.NewCount;
                sum = d.NewSum;
            }

            Assert.AreEqual(3009, d.NewSum);
            Assert.AreEqual(300, d.NewAverage);
        }

        [TestMethod]
        public void Fill_FullPattern_NeverTakesAnotherFiber_ButStillCompletes()
        {
            var d = PatternFill.Decide(true, true, 500, KnotItems.FibersPerKnot, 3009, Def("a"), true);
            Assert.IsTrue(d.Accepted);
            Assert.IsFalse(d.TakesFiber, "a full Pattern does not take the Fiber");
            Assert.IsTrue(d.Completes);
            Assert.AreEqual(KnotItems.FibersPerKnot, d.NewCount);
            Assert.AreEqual(3009, d.NewSum, "the sum is untouched");
            Assert.AreEqual(300, d.NewAverage);

            // a corrupt over-full count is still capped at ten
            var over = PatternFill.Decide(true, true, 500, 14, 4200, Def("a"), true);
            Assert.AreEqual(KnotItems.FibersPerKnot, over.NewCount);
            Assert.IsFalse(over.TakesFiber);
        }

        [TestMethod]
        public void Fill_CountNeverExceedsTen_AcrossAnyStartingCount()
        {
            for (var start = 0; start <= 20; start++)
            {
                var d = PatternFill.Decide(true, true, 100, start, start * 100, Def("a"), true);
                Assert.IsTrue(d.NewCount <= KnotItems.FibersPerKnot, $"start {start}");
                Assert.AreEqual(d.NewCount >= KnotItems.FibersPerKnot, d.Completes, $"start {start}");
            }
        }

        [TestMethod]
        public void Fill_RefusalOrder_WrongTargetBeatsPinnacleState()
        {
            var d = PatternFill.Decide(true, false, 100, 0, 0, null, false);
            Assert.AreEqual(KnotItems.PatternNeedsFiber, d.Refusal);
        }

        [TestMethod]
        public void CompletionRefusal_OnlyNullForAnEnabledWiredPinnacle()
        {
            Assert.IsNull(PatternFill.CompletionRefusal(Def("a"), true));
            Assert.AreEqual(KnotItems.PinnacleDisabled, PatternFill.CompletionRefusal(null, true));
            Assert.AreEqual(KnotItems.PinnacleDisabled, PatternFill.CompletionRefusal(Def("a", enabled: false), true));
            Assert.AreEqual(KnotItems.NoEncounterRegistered, PatternFill.CompletionRefusal(Def("a"), false));
        }

        [TestMethod]
        public void KnotLevelFromSum_FloorsAndHandlesEmpty()
        {
            Assert.AreEqual(0, KnotWeaving.KnotLevelFromSum(0, 0));
            Assert.AreEqual(100, KnotWeaving.KnotLevelFromSum(201, 2));
            Assert.AreEqual(145, KnotWeaving.KnotLevelFromSum(1450, 10));
        }

        [TestMethod]
        public void Available_RequiresEnabledAndEncounter()
        {
            var wired = Def("w", kind: "live");
            var unwired = Def("u", kind: "none");
            var off = Def("o", kind: "live", enabled: false);
            var list = new List<PinnacleDef> { wired, unwired, off };
            var got = KnotPinnacles.Available(list, k => k == "live");
            CollectionAssert.AreEqual(new[] { wired }, got);
            Assert.IsTrue(KnotPinnacles.IsAvailable(wired, k => k == "live"));
            Assert.IsFalse(KnotPinnacles.IsAvailable(unwired, k => k == "live"));
            Assert.IsFalse(KnotPinnacles.IsAvailable(off, k => k == "live"));
            Assert.IsFalse(KnotPinnacles.IsAvailable(null, k => true));
            Assert.AreEqual(0, KnotPinnacles.Available(null, k => true).Count);
        }

        [TestMethod]
        public void Available_FeedsDropRoll_UnwiredPinnacleDrawsNoPattern()
        {
            var unwired = new List<PinnacleDef> { Def("u", kind: "none") };
            var available = KnotPinnacles.Available(unwired, k => false);
            var outcome = KnotDrops.Roll(0, 0.0, 0.0, 1.0, available.Count, () => 0.0, _ => 0);
            Assert.IsFalse(outcome.PatternDropped);
        }

        [TestMethod]
        public void Texts_AreDistinct()
        {
            Assert.AreNotEqual(KnotItems.NoPinnacleAvailable, KnotItems.NoEncounterRegistered);
        }

        // ---- KnotUse.Gate ----

        [TestMethod]
        public void Gate_ReportsEachRefusalInOrder()
        {
            Assert.AreEqual(KnotGate.Open, KnotUse.Gate(false, false, false));
            Assert.AreEqual(KnotGate.InCombat, KnotUse.Gate(true, true, true));
            Assert.AreEqual(KnotGate.PkTimer, KnotUse.Gate(false, true, true));
            Assert.AreEqual(KnotGate.InsideInstance, KnotUse.Gate(false, false, true));
        }

        // ---- KnotUse.Decide ----

        private static KnotUseDecision Decide(string key, int? structure, PinnacleDef bound, IReadOnlyList<PinnacleDef> enabled,
            Func<int, int> pick = null, Func<string, bool> has = null)
            => KnotUse.Decide(key, structure, bound, enabled, pick ?? (n => 0), has ?? (k => true));

        [TestMethod]
        public void Use_Bound_DisabledOrMissing_Refused()
        {
            var off = Decide("a", 3, Def("a", enabled: false), new List<PinnacleDef>());
            Assert.IsFalse(off.Allowed);
            Assert.AreEqual(KnotItems.PinnacleDisabled, off.Refusal);

            var gone = Decide("a", 3, null, new List<PinnacleDef>());
            Assert.AreEqual(KnotItems.PinnacleDisabled, gone.Refusal);
        }

        [TestMethod]
        public void Use_Bound_NoEncounter_Refused()
        {
            var d = Decide("a", 3, Def("a"), new List<PinnacleDef> { Def("a") }, has: k => false);
            Assert.IsFalse(d.Allowed);
            Assert.AreEqual(KnotItems.NoEncounterRegistered, d.Refusal);
        }


        [TestMethod]
        public void Use_Bound_Success_NoRebind_ReportsEntries()
        {
            var def = Def("a");
            var d = Decide("a", 2, def, new List<PinnacleDef> { def });
            Assert.IsTrue(d.Allowed);
            Assert.IsFalse(d.NeedsBind);
            Assert.AreSame(def, d.Def);
            Assert.AreEqual(2, d.Entries);
        }

        [TestMethod]
        public void Use_Bound_NullStructure_FallsBackToDefEntries()
        {
            var def = Def("a", entries: 5);
            Assert.AreEqual(5, Decide("a", null, def, new List<PinnacleDef> { def }).Entries);
        }

        [TestMethod]
        public void Use_Unbound_NoEnabledPinnacle_Refused()
        {
            var d = Decide(null, null, null, new List<PinnacleDef>());
            Assert.IsFalse(d.Allowed);
            Assert.AreEqual(KnotItems.NoPinnacleAvailable, d.Refusal);
        }

        [TestMethod]
        public void Use_Unbound_NoEncounterForAny_Refused()
        {
            var d = Decide(null, null, null, new List<PinnacleDef> { Def("a") }, has: k => false);
            Assert.IsFalse(d.Allowed);
            Assert.AreEqual(KnotItems.NoEncounterRegistered, d.Refusal);
        }

        [TestMethod]
        public void Use_Unbound_PicksFromPool_AndTakesDefEntries()
        {
            var a = Def("a", kind: "k1", entries: 2);
            var b = Def("b", kind: "k2", entries: 4);
            var d = Decide(null, null, null, new List<PinnacleDef> { a, b }, pick: n => n - 1);
            Assert.IsTrue(d.Allowed);
            Assert.IsTrue(d.NeedsBind);
            Assert.AreSame(b, d.Def);
            Assert.AreEqual(4, d.Entries);
        }

        [TestMethod]
        public void Use_Unbound_PoolExcludesKindsWithNoEncounter()
        {
            var a = Def("a", kind: "none");
            var b = Def("b", kind: "live");
            var sizes = new List<int>();
            var d = Decide(null, null, null, new List<PinnacleDef> { a, b }, pick: n => { sizes.Add(n); return 0; }, has: k => k == "live");
            Assert.AreSame(b, d.Def);
            CollectionAssert.AreEqual(new[] { 1 }, sizes);
        }

        [TestMethod]
        public void Use_Unbound_OutOfRangePick_IsClamped()
        {
            var a = Def("a");
            var d = Decide(null, null, null, new List<PinnacleDef> { a }, pick: n => 99);
            Assert.AreSame(a, d.Def);
        }

        [TestMethod]
        public void Decide_BoundZeroEntries_FreeReturn_Enters()
        {
            // C1: a won fight being collected re-enters for free, at zero entries too
            var def = Def("a");
            var d = KnotUse.Decide("a", 0, def, new List<PinnacleDef> { def }, n => 0, k => true, resume: KnotResume.Free);
            Assert.IsTrue(d.Allowed);
            Assert.IsFalse(d.NeedsBind);
            Assert.AreSame(def, d.Def);
            Assert.AreEqual(0, d.Entries);
            Assert.AreEqual(KnotResume.Free, d.Resume);
        }

        [TestMethod]
        public void Decide_BoundZeroEntries_PaidReturn_RefusedAndForfeits()
        {
            // R1: a held attempt at zero entries is not resumable; the attempt is lost and the Knot consumed
            var def = Def("a");
            var d = KnotUse.Decide("a", 0, def, new List<PinnacleDef> { def }, n => 0, k => true, resume: KnotResume.Paid);
            Assert.IsFalse(d.Allowed);
            Assert.IsTrue(d.NoEntries, "the seam consumes the Knot");
            Assert.IsTrue(d.ForfeitsAttempt);
            Assert.AreEqual(KnotItems.KnotNoEntriesTrialLost, d.Refusal);
        }

        [TestMethod]
        public void Decide_BoundZeroEntries_OwedReturn_EntersFree()
        {
            // owner ruling 2026-10-19: returning to collect after a win is always free, the owed stage included
            var def = Def("a");
            var d = KnotUse.Decide("a", 0, def, new List<PinnacleDef> { def }, n => 0, k => true, resume: KnotResume.Owed);
            Assert.IsTrue(d.Allowed);
            Assert.IsFalse(d.NoEntries);
            Assert.IsFalse(d.ForfeitsAttempt);
            Assert.AreEqual(0, d.Entries);
            Assert.AreEqual(KnotResume.Owed, d.Resume);
        }

        [TestMethod]
        public void Decide_BoundWithEntries_PaidAndOwed_Enter()
        {
            var def = Def("a");

            foreach (var r in new[] { KnotResume.Paid, KnotResume.Owed, KnotResume.Free, KnotResume.None })
            {
                var d = KnotUse.Decide("a", 2, def, new List<PinnacleDef> { def }, n => 0, k => true, resume: r);
                Assert.IsTrue(d.Allowed, r.ToString());
                Assert.AreEqual(2, d.Entries, r.ToString());
                Assert.AreEqual(r, d.Resume, r.ToString());
            }
        }

        [TestMethod]
        public void Decide_BoundZeroEntries_NotResuming_RefusedNoEntries()
        {
            var def = Def("a");
            var d = KnotUse.Decide("a", 0, def, new List<PinnacleDef> { def }, n => 0, k => true, resume: KnotResume.None);
            Assert.IsFalse(d.Allowed);
            Assert.AreEqual(KnotItems.KnotHasNoEntries, d.Refusal);
            Assert.IsTrue(d.NoEntries);
            Assert.IsFalse(d.ForfeitsAttempt);
        }

        [TestMethod]
        public void Decide_NoEntriesFlag_SetOnlyForTheNoEntriesRefusal()
        {
            var def = Def("a");
            Assert.IsFalse(KnotUse.Decide("a", 0, Def("a", enabled: false), new List<PinnacleDef>(), n => 0, k => true).NoEntries);
            Assert.IsFalse(KnotUse.Decide("a", 0, def, new List<PinnacleDef> { def }, n => 0, k => false).NoEntries);
            Assert.IsFalse(KnotUse.Decide(null, null, null, new List<PinnacleDef>(), n => 0, k => true).NoEntries);
            Assert.IsFalse(KnotUse.Decide("a", 2, def, new List<PinnacleDef> { def }, n => 0, k => true).NoEntries);
        }

        [TestMethod]
        public void Decide_Resuming_DoesNotBypassDisabledOrMissingEncounter()
        {
            var off = KnotUse.Decide("a", 0, Def("a", enabled: false), new List<PinnacleDef>(), n => 0, k => true, resume: KnotResume.Free);
            Assert.AreEqual(KnotItems.PinnacleDisabled, off.Refusal);

            var def = Def("a");
            var none = KnotUse.Decide("a", 0, def, new List<PinnacleDef> { def }, n => 0, k => false, resume: KnotResume.Free);
            Assert.AreEqual(KnotItems.NoEncounterRegistered, none.Refusal);
        }

        [TestMethod]
        public void Encounter_DefaultMembers_ChargeAndNoResume()
        {
            IPinnacleEncounter enc = new FakeEncounter("grove");
            Assert.IsTrue(enc.SeamChargesEntry);
            Assert.AreEqual(KnotResume.None, enc.ResumableAttempt(null, null));
            enc.ForfeitAttempt(null, null); // a no-op default, never throws
        }

        [TestMethod]
        public void EntriesAfterRefund_AddsOne_CappedAtMax()
        {
            Assert.AreEqual(3, KnotState.EntriesAfterRefund(2, 5));
            Assert.AreEqual(5, KnotState.EntriesAfterRefund(5, 5));
            Assert.AreEqual(1, KnotState.EntriesAfterRefund(0, 3));
            Assert.AreEqual(1, KnotState.EntriesAfterRefund(-2, 3));
            Assert.AreEqual(4, KnotState.EntriesAfterRefund(3, null));
            Assert.AreEqual(4, KnotState.EntriesAfterRefund(3, 0));
        }

        [TestMethod]
        public void Texts_ResumeSpentLine_SaysAnEntryWasSpentAndHowManyRemain()
        {
            Assert.AreEqual("Your Knot pulls you back into the fight you left. One entry is spent; 2 entries remain.", KnotItems.KnotResumeSpent(2));
            Assert.AreEqual("Your Knot pulls you back into the fight you left. One entry is spent; 1 entry remains.", KnotItems.KnotResumeSpent(1));
            Assert.AreEqual("Your Knot pulls you back into the fight you left. One entry is spent; 0 entries remain.", KnotItems.KnotResumeSpent(0));
        }

        [TestMethod]
        public void Texts_EncounterEntryLine_PicksBySpendAndResume()
        {
            Assert.AreEqual(KnotItems.KnotResumeSpent(1), KnotItems.EncounterEntryLine(KnotResume.Paid, true, 1));
            Assert.AreEqual(KnotItems.KnotReturnToCache, KnotItems.EncounterEntryLine(KnotResume.Owed, false, 0), "a won return is free and says so");
            Assert.AreEqual(KnotItems.KnotReturnToCache, KnotItems.EncounterEntryLine(KnotResume.Free, false, 0));
            Assert.AreEqual(KnotItems.KnotEnterArrival, KnotItems.EncounterEntryLine(KnotResume.None, false, 2));
            Assert.AreEqual(KnotItems.KnotResumeHeld, KnotItems.EncounterEntryLine(KnotResume.Paid, false, 2));
        }

        [TestMethod]
        public void Texts_ResumeAndArrivalLines_AreExactAndDistinct()
        {
            Assert.AreEqual("Your Knot pulls you back into the fight you left.", KnotItems.KnotResumeHeld);
            Assert.AreEqual("The Knot unravels around you, and you stand at the threshold of the pinnacle.", KnotItems.KnotEnterArrival);
            Assert.AreNotEqual(KnotItems.KnotResumeHeld, KnotItems.KnotEnterArrival);
        }

        // ---- spending ----

        [TestMethod]
        public void EntriesAfterSpend_DecrementsAndFloorsAtZero()
        {
            Assert.AreEqual(2, KnotState.EntriesAfterSpend(3));
            Assert.AreEqual(0, KnotState.EntriesAfterSpend(1));
            Assert.AreEqual(0, KnotState.EntriesAfterSpend(0));
        }

        [TestMethod]
        public void Decide_DoesNotSpend_SoRepeatedDecisionsAreEqual()
        {
            var def = Def("a");
            var list = new List<PinnacleDef> { def };
            Assert.AreEqual(Decide("a", 3, def, list).Entries, Decide("a", 3, def, list).Entries);
        }

        // ---- phase upward only ----

        [TestMethod]
        public void PhaseAfter_OnlyMovesUp()
        {
            Assert.AreEqual(2, KnotState.PhaseAfter(null, 2));
            Assert.AreEqual(2, KnotState.PhaseAfter(1, 2));
            Assert.AreEqual(2, KnotState.PhaseAfter(2, 1));
            Assert.AreEqual(2, KnotState.PhaseAfter(2, 2));
            Assert.AreEqual(0, KnotState.PhaseAfter(null, 0));
        }

        // ---- registry ----

        [TestMethod]
        public void Registry_RegisterAndLookup()
        {
            var enc = new FakeEncounter("grove");
            PinnacleEncounters.Register(enc);
            Assert.IsTrue(PinnacleEncounters.TryGet("grove", out var got));
            Assert.AreSame(enc, got);
            Assert.IsFalse(PinnacleEncounters.TryGet("other", out var none));
            Assert.IsNull(none);
            Assert.IsFalse(PinnacleEncounters.TryGet(null, out _));
        }

        [TestMethod]
        public void Registry_ReRegisterReplaces_AndIgnoresBlank()
        {
            var first = new FakeEncounter("grove");
            var second = new FakeEncounter("grove");
            PinnacleEncounters.Register(first);
            PinnacleEncounters.Register(second);
            PinnacleEncounters.Register(null);
            PinnacleEncounters.Register(new FakeEncounter(" "));
            PinnacleEncounters.TryGet("grove", out var got);
            Assert.AreSame(second, got);
            Assert.IsFalse(PinnacleEncounters.TryGet(" ", out _));
        }

        [TestMethod]
        public void Registry_ConcurrentRegisterIsSafe()
        {
            System.Threading.Tasks.Parallel.For(0, 200, i => PinnacleEncounters.Register(new FakeEncounter("k" + (i % 10))));
            for (var i = 0; i < 10; i++)
                Assert.IsTrue(PinnacleEncounters.TryGet("k" + i, out _));
        }
    
    }

    /// <summary>Scans of the shipped weenie rows and Gem.cs that keep a Fiber, Pattern or Knot from being eaten by the generic consume.</summary>
    [TestClass]
    public class KnotConsumeGuardTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate).Replace("\r\n", "\n");
                dir = dir.Parent;
            }
            Assert.Fail("not found by walking up: " + string.Join("/", parts));
            return null;
        }

        [TestMethod]
        public void TryHandleUse_SkipsSpend_WhenEncounterDoesNotCharge()
        {
            var src = RepoFile("Source", "ACE.Server", "Knots", "KnotSeam.cs");
            var start = src.IndexOf("private static bool RunUse(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var end = src.IndexOf("private static void Bind(", start, StringComparison.Ordinal);
            var body = src.Substring(start, end - start);

            var check = body.IndexOf("!encounter.SeamChargesEntry", StringComparison.Ordinal);
            var spend = body.IndexOf("KnotState.EntriesAfterSpend(", StringComparison.Ordinal);
            var tryEnter = body.IndexOf("encounter.TryEnter(", StringComparison.Ordinal);
            Assert.IsTrue(check >= 0, "SeamChargesEntry check missing");
            Assert.IsTrue(spend >= 0, "spend missing");
            Assert.IsTrue(tryEnter >= 0 && tryEnter < check, "the check must follow TryEnter");
            Assert.IsTrue(check < spend, "the SeamChargesEntry check must precede the spend");
            var ret = body.IndexOf("return true;", check, StringComparison.Ordinal);
            Assert.IsTrue(ret > check && ret < spend, "the SeamChargesEntry block must return before the spend");
            Assert.IsTrue(body.Contains("resume: resume"), "Decide must receive the resume kind");

            // R1: a Paid return refused at zero entries forfeits the attempt BEFORE the Knot is consumed
            var forfeit = body.IndexOf("resumeEncounter?.ForfeitAttempt(player, knot)", StringComparison.Ordinal);
            var consume = body.IndexOf("player.TryConsumeFromInventoryWithNetworking(knot)", StringComparison.Ordinal);
            Assert.IsTrue(forfeit >= 0 && consume > forfeit, "the forfeit must run before the Knot is consumed");

            // the entry line reports the count read before TryEnter against the count after it
            var before = body.IndexOf("var entriesBefore", StringComparison.Ordinal);
            Assert.IsTrue(before >= 0 && before < tryEnter, "entriesBefore must be read before TryEnter");
            Assert.IsTrue(body.Contains("KnotItems.EncounterEntryLine(resume, entriesAfter < entriesBefore, entriesAfter)"), "the line must report the spend");
        }

        [TestMethod]
        public void KnotEncounterEvents_SpendEntryNeverConsumes_OnAttemptLostConsumesOnlyAtZero()
        {
            var src = RepoFile("Source", "ACE.Server", "Knots", "KnotEncounterEvents.cs");
            var s = src.IndexOf("public static int SpendEntry(", StringComparison.Ordinal);
            var l = src.IndexOf("public static void OnAttemptLost(", StringComparison.Ordinal);
            var f = src.IndexOf("private static WorldObject Find(", StringComparison.Ordinal);
            Assert.IsTrue(s >= 0 && l > s && f > l);
            Assert.IsFalse(src.Substring(s, l - s).Contains("TryConsumeFromInventoryWithNetworking"), "SpendEntry must never consume");
            var lost = src.Substring(l, f - l);
            var guard = lost.IndexOf("<= 0", StringComparison.Ordinal);
            var consume = lost.IndexOf("TryConsumeFromInventoryWithNetworking", StringComparison.Ordinal);
            Assert.IsTrue(guard >= 0, "the Structure <= 0 guard is missing");
            Assert.IsTrue(consume >= 0, "the consume is missing");
            Assert.IsTrue(guard < consume, "the guard must precede the consume");
            Assert.IsTrue(src.Substring(s, l - s).Contains("PropertyInt.Structure) ?? knot.GetProperty(PropertyInt.MaxStructure) ?? 0"), "SpendEntry must fall back to MaxStructure");
            Assert.IsTrue(src.Substring(s, l - s).Contains("Session == null") && lost.Contains("Session == null"), "both must no-op without a session");
        }

        [TestMethod]
        public void TryHandleUse_BoundNoEntriesNotResumable_ConsumesKnot()
        {
            var src = RepoFile("Source", "ACE.Server", "Knots", "KnotSeam.cs");
            var start = src.IndexOf("private static bool RunUse(", StringComparison.Ordinal);
            var end = src.IndexOf("private static void Bind(", start, StringComparison.Ordinal);
            var body = src.Substring(start, end - start);

            var notAllowed = body.IndexOf("if (!decision.Allowed)", StringComparison.Ordinal);
            var guard = body.IndexOf("if (bound && decision.NoEntries)", StringComparison.Ordinal);
            Assert.IsTrue(notAllowed >= 0 && guard > notAllowed, "the bound && NoEntries guard must sit inside the refusal branch");
            var consume = body.IndexOf("player.TryConsumeFromInventoryWithNetworking(knot);", guard, StringComparison.Ordinal);
            var spent = body.IndexOf("Say(player, decision.ForfeitsAttempt ? decision.Refusal : KnotItems.KnotSpent);", guard, StringComparison.Ordinal);
            var ret = body.IndexOf("return true;", guard, StringComparison.Ordinal);
            Assert.IsTrue(consume > guard && spent > consume && ret > spent, "guard must consume, say KnotSpent, then return");
            Assert.IsTrue(ret < body.IndexOf("Say(player, decision.Refusal);", guard, StringComparison.Ordinal), "the consume must return before the generic refusal line");
        }

        [TestMethod]
        public void PatternSeam_ConsumesTheFiberBeforeAnyPatternWrite_AndCompletesInOrder()
        {
            var src = RepoFile("Source", "ACE.Server", "Knots", "KnotSeam.cs");

            var use = src.Substring(src.IndexOf("public static void UsePatternOnTarget(", StringComparison.Ordinal));
            use = use.Substring(0, use.IndexOf("public static bool TryCompleteFullPattern(", StringComparison.Ordinal));
            var consumeFiber = use.IndexOf("player.TryConsumeFromInventoryWithNetworking(target, 1)", StringComparison.Ordinal);
            var firstWrite = use.IndexOf("player.UpdateProperty(pattern,", StringComparison.Ordinal);
            var failReturn = use.IndexOf("return;", consumeFiber, StringComparison.Ordinal);
            Assert.IsTrue(consumeFiber > 0 && firstWrite > consumeFiber, "the Fiber is consumed before the Pattern is written");
            Assert.IsTrue(failReturn > consumeFiber && failReturn < firstWrite, "a failed consume returns before any write");
            Assert.IsTrue(use.IndexOf("if (decision.TakesFiber)", StringComparison.Ordinal) < consumeFiber, "a full Pattern never reaches the Fiber consume");

            var done = src.Substring(src.IndexOf("internal static void TryCompletePattern(", StringComparison.Ordinal));
            done = done.Substring(0, done.IndexOf("/// <summary>Knot double-click.", StringComparison.Ordinal));
            var place = done.IndexOf("KnotWeaving.TryPlaceKnot(", StringComparison.Ordinal);
            var bind = done.IndexOf("Bind(player, knot, def);", StringComparison.Ordinal);
            var consumePattern = done.IndexOf("player.TryConsumeFromInventoryWithNetworking(pattern, 1)", StringComparison.Ordinal);
            var takeBack = done.IndexOf("player.TryConsumeFromInventoryWithNetworking(knot)", StringComparison.Ordinal);
            var record = done.IndexOf("KnotTelemetry.RecordWeave(", StringComparison.Ordinal);
            var recordBind = done.IndexOf("KnotTelemetry.RecordBind(player, knot, def, pattern);", StringComparison.Ordinal);
            var completed = done.IndexOf("KnotItems.PatternCompleted(", StringComparison.Ordinal);
            Assert.IsTrue(place >= 0 && bind > place && consumePattern > bind, "place, then bind, then consume the Pattern");
            Assert.IsTrue(takeBack > consumePattern, "a failed Pattern consume takes the placed Knot back");
            Assert.IsTrue(record > takeBack && recordBind > record && completed > recordBind, "telemetry and the line only on success");
            var noRoom = done.IndexOf("KnotItems.PatternNoRoom", StringComparison.Ordinal);
            Assert.IsTrue(noRoom > place && noRoom < bind, "no room refuses before the bind");
            StringAssert.Contains(done.Substring(noRoom, bind - noRoom), "return;", "no room must return before the bind");

            var refused = done.IndexOf("KnotItems.KnotEncounterRefused", StringComparison.Ordinal);
            Assert.IsTrue(refused > consumePattern && refused < record, "the failed Pattern consume refuses before telemetry");
            StringAssert.Contains(done.Substring(refused, record - refused), "return;", "a failed Pattern consume must return before RecordWeave");

            // Fiber ownership: the target must be found in the player's own pack, never the landblock or anywhere
            StringAssert.Contains(use, "SearchLocations.MyInventory)");
            Assert.IsFalse(use.Contains("SearchLocations.Landblock"), "the Fiber is never taken from the landblock");
            Assert.IsFalse(use.Contains("SearchLocations.Everywhere"), "the Fiber is never taken from anywhere");
        }

        [TestMethod]
        public void UseGem_FullPatternWithNoTargetRetriesCompletion_BeforeTheGenericConsume()
        {
            var src = RepoFile("Source", "ACE.Server", "WorldObjects", "Gem.cs");
            var start = src.IndexOf("public void UseGem(Player player)", StringComparison.Ordinal);
            var body = src.Substring(start, src.IndexOf("/// <summary>", start, StringComparison.Ordinal) - start);

            var retry = body.IndexOf("KnotSeam.TryCompleteFullPattern(player, this)", StringComparison.Ordinal);
            var consume = body.LastIndexOf("TryConsumeFromInventoryWithNetworking(this, 1)", StringComparison.Ordinal);
            Assert.IsTrue(retry > 0 && retry < consume);

            var needsTarget = body.IndexOf("KnotItems.PatternNeedsTarget", StringComparison.Ordinal);
            var nextIf = body.IndexOf("if (", needsTarget, StringComparison.Ordinal);
            Assert.IsTrue(needsTarget > retry && nextIf > needsTarget);
            StringAssert.Contains(body.Substring(needsTarget, nextIf - needsTarget), "return;", "the Pattern branch must return before the generic consume");
            StringAssert.Contains(body, "KnotItems.FiberNeedsPattern");
        }

        [DataTestMethod]
        [DataRow("1007000 Fiber", 1007000u)]
        [DataRow("1007003 Pattern", 1007003u)]
        public void FiberAndPattern_SeedUnlimitedUse(string file, uint wcid)
        {
            var sql = RepoFile("Content", "sql", "weenies", file + ".sql");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(sql, @"\(\s*" + wcid + @",\s*63,\s*True\s*\)"), "PropertyBool 63 UnlimitedUse True");
        }

        [TestMethod]
        public void UseGem_FiberPatternAndKnotBranchesSitBeforeTheGenericConsume()
        {
            var src = RepoFile("Source", "ACE.Server", "WorldObjects", "Gem.cs");
            var start = src.IndexOf("public void UseGem(Player player)", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0);
            var end = src.IndexOf("/// <summary>", start, StringComparison.Ordinal);
            var body = src.Substring(start, end - start);

            var consume = body.LastIndexOf("TryConsumeFromInventoryWithNetworking(this, 1)", StringComparison.Ordinal);
            Assert.IsTrue(consume > 0, "generic consume present");

            foreach (var marker in new[] { "KnotItems.IsFiber(this)", "KnotItems.IsPattern(this)", "KnotSeam.TryHandleUse(this, player)" })
            {
                var i = body.IndexOf(marker, StringComparison.Ordinal);
                Assert.IsTrue(i >= 0, marker + " missing from UseGem");
                Assert.IsTrue(i < consume, marker + " must precede the generic consume");
            }
        }
    }
}
