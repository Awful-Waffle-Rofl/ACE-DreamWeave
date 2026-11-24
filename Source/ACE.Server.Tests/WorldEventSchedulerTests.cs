using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The firing rules of the World Events daily scheduler, run against fakes: no Player, Session, world or
    /// PropertyManager. Slot "10:00" in UTC keeps the clock arithmetic obvious.
    /// </summary>
    [TestClass]
    public class WorldEventSchedulerTests
    {
        private sealed class Rig
        {
            public DateTime Now = new DateTime(2027, 5, 5, 9, 59, 0, DateTimeKind.Utc);
            public bool Enabled = true;
            public string Schedule = "10:00";
            public long Late = 15;
            public bool Shutdown;
            public bool Running;
            public List<AnchorDef> Anchors = Make("a", "b", "c", "d");
            public Dictionary<string, float> Clearance = new Dictionary<string, float> { ["small"] = 5f, ["big"] = 15f };
            public List<string> AllowedFromBuild = new List<string> { "small", "big" };
            public Func<AnchorDef, string> BuildRefusal = _ => null;
            public Func<WorldEventRequest, (WorldEventSchedulerStart, uint, string)> StartFn;
            public List<WorldEventRequest> Started = new List<WorldEventRequest>();
            public List<string> BuiltFor = new List<string>();
            public List<string> Info = new List<string>();
            public List<string> Warn = new List<string>();
            public List<string> Audits = new List<string>();
            public uint NextRun = 100;
            public WorldEventScheduler Scheduler;

            public Rig()
            {
                StartFn = request =>
                {
                    Started.Add(request);
                    return (WorldEventSchedulerStart.Started, NextRun++, null);
                };

                Scheduler = new WorldEventScheduler(new WorldEventSchedulerSeams
                {
                    UtcNow = () => Now,
                    Enabled = () => Enabled,
                    ScheduleText = () => Schedule,
                    DefaultZoneId = () => "UTC",
                    LateMinutes = () => Late,
                    ResolveZone = _ => TimeZoneInfo.Utc,
                    ShutdownPending = () => Shutdown,
                    EventRunning = () => Running,
                    Anchors = () => Anchors,
                    BuildRequest = anchor =>
                    {
                        BuiltFor.Add(anchor.Id);
                        var refusal = BuildRefusal(anchor);
                        return refusal != null
                            ? (null, refusal)
                            : (new WorldEventRequest { Random = true, AllowedSourceIds = AllowedFromBuild.ToList() }, null);
                    },
                    ClearanceOf = id => Clearance.TryGetValue(id, out var c) ? c : null,
                    Start = request => StartFn(request),
                    NextInt = _ => 0,
                    LogInfo = Info.Add,
                    LogWarn = Warn.Add,
                    Audit = (actor, message) => Audits.Add(actor + "|" + message)
                });
            }

            public void At(int day, int hour, int minute, int second = 0)
                => Now = new DateTime(2027, 5, day, hour, minute, second, DateTimeKind.Utc);
        }

        private static List<AnchorDef> Make(params string[] ids)
            => ids.Select(id => new AnchorDef { Id = id, DisplayName = id.ToUpperInvariant(), CellId = 1, MaxRadius = 10 }).ToList();

        // ---- rule a: enabled --------------------------------------------------------------------------------

        [TestMethod]
        public void Disabled_DoesNothing()
        {
            var rig = new Rig { Enabled = false };
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();

            Assert.AreEqual(0, rig.Started.Count);
            Assert.AreEqual(0, rig.BuiltFor.Count);
            Assert.IsNull(rig.Scheduler.LastOutcome);
        }

        [TestMethod]
        public void NotYetDue_DoesNothing()
        {
            var rig = new Rig();

            rig.Scheduler.Tick();

            Assert.AreEqual(0, rig.Started.Count);
        }

        // ---- rule b: at most once ---------------------------------------------------------------------------

        [TestMethod]
        public void Fires_AtMostOncePerOccurrence_AcrossManyTicks()
        {
            var rig = new Rig();

            for (var second = 0; second < 600; second++)
            {
                rig.At(5, 10, 0);
                rig.Now = rig.Now.AddSeconds(second);
                rig.Scheduler.Tick();
            }

            Assert.AreEqual(1, rig.Started.Count);
        }

        [TestMethod]
        public void FiresAgainTheNextDay()
        {
            var rig = new Rig();

            rig.At(5, 10, 0);
            rig.Scheduler.Tick();
            rig.At(6, 10, 0);
            rig.Scheduler.Tick();

            Assert.AreEqual(2, rig.Started.Count);
        }

        [TestMethod]
        public void TwoSlots_EachFireOnce()
        {
            var rig = new Rig { Schedule = "10:00, 10:05" };

            for (var minute = 0; minute < 30; minute++)
            {
                rig.At(5, 10, minute);
                rig.Scheduler.Tick();
            }

            Assert.AreEqual(2, rig.Started.Count);
        }

        // ---- rule c: shutdown -------------------------------------------------------------------------------

        [TestMethod]
        public void ShutdownPending_SkipsTheOccurrenceWithAReason()
        {
            var rig = new Rig { Shutdown = true };
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();
            rig.Shutdown = false;
            rig.At(5, 10, 1);
            rig.Scheduler.Tick();

            Assert.AreEqual(0, rig.Started.Count, "a skipped occurrence is final");
            Assert.IsFalse(rig.Scheduler.LastOutcome.Started);
            Assert.AreEqual("shutdown pending", rig.Scheduler.LastOutcome.Reason);
            Assert.IsTrue(rig.Info.Any(l => l.Contains("[WORLDEVENT-SCHED]") && l.Contains("shutdown pending")));
        }

        // ---- rule d: running event --------------------------------------------------------------------------

        [TestMethod]
        public void RunningEvent_WaitsWithoutMarking_ThenFiresWhenItEndsInsideTheWindow()
        {
            var rig = new Rig { Running = true };
            rig.At(5, 10, 0);
            rig.Scheduler.Tick();
            rig.At(5, 10, 8);
            rig.Scheduler.Tick();

            Assert.AreEqual(0, rig.Started.Count);
            Assert.IsNull(rig.Scheduler.LastOutcome, "waiting is not an outcome");

            rig.Running = false;
            rig.At(5, 10, 9);
            rig.Scheduler.Tick();

            Assert.AreEqual(1, rig.Started.Count);
        }

        [TestMethod]
        public void RunningEvent_PastTheLateWindow_IsSkippedWithEventRunning()
        {
            var rig = new Rig { Running = true };
            rig.At(5, 10, 0);
            rig.Scheduler.Tick();

            rig.At(5, 10, 16);
            rig.Scheduler.Tick();

            Assert.AreEqual(0, rig.Started.Count);
            Assert.IsFalse(rig.Scheduler.LastOutcome.Started);
            Assert.AreEqual("event running", rig.Scheduler.LastOutcome.Reason);

            // And it stays final even if the event ends later.
            rig.Running = false;
            rig.At(5, 10, 17);
            rig.Scheduler.Tick();
            Assert.AreEqual(0, rig.Started.Count);
        }

        [TestMethod]
        public void StartReportingBusy_WaitsInsteadOfBurningTheOccurrence()
        {
            var rig = new Rig();
            var busy = true;
            rig.StartFn = request => busy
                ? (WorldEventSchedulerStart.Busy, 7u, "an event is already running")
                : (WorldEventSchedulerStart.Started, 55u, null);

            rig.At(5, 10, 0);
            rig.Scheduler.Tick();
            Assert.IsNull(rig.Scheduler.LastOutcome);

            busy = false;
            rig.At(5, 10, 3);
            rig.Scheduler.Tick();

            Assert.IsTrue(rig.Scheduler.LastOutcome.Started);
            Assert.AreEqual(55u, rig.Scheduler.LastOutcome.RunId);
        }

        // ---- rule e: location -------------------------------------------------------------------------------

        [TestMethod]
        public void AnchorWithZeroMaxRadius_IsNeverPicked()
        {
            var rig = new Rig { Anchors = Make("a", "b") };
            rig.Anchors.Add(new AnchorDef { Id = "dryreach", DisplayName = "Dryreach", CellId = 1, MaxRadius = 0 });

            for (var day = 1; day <= 12; day++)
            {
                rig.At(day, 10, 0);
                rig.Scheduler.Tick();
            }

            Assert.IsFalse(rig.BuiltFor.Contains("dryreach"));
            Assert.IsTrue(rig.BuiltFor.Count > 0);
        }

        [TestMethod]
        public void Rotation_NeverRepeatsOneOfTheLastThreePicks()
        {
            var rig = new Rig { Anchors = Make("a", "b", "c", "d", "e") };
            var picks = new List<string>();

            for (var day = 1; day <= 20; day++)
            {
                rig.At(day, 10, 0);
                rig.Scheduler.Tick();
                picks.Add(rig.Scheduler.RecentAnchors.Last());
            }

            Assert.AreEqual(20, picks.Count);

            for (var i = 3; i < picks.Count; i++)
                CollectionAssert.DoesNotContain(picks.GetRange(i - 3, 3), picks[i], $"pick {i} repeated one of the previous three");
        }

        [TestMethod]
        public void Rotation_WithThreeOrFewerAnchors_FallsBackToAllRatherThanStalling()
        {
            var rig = new Rig { Anchors = Make("a", "b", "c") };

            for (var day = 1; day <= 6; day++)
            {
                rig.At(day, 10, 0);
                rig.Scheduler.Tick();
            }

            Assert.AreEqual(6, rig.Started.Count);
        }

        [TestMethod]
        public void RecentList_KeepsOnlyTheLastThree()
        {
            var rig = new Rig { Anchors = Make("a", "b", "c", "d", "e") };

            for (var day = 1; day <= 5; day++)
            {
                rig.At(day, 10, 0);
                rig.Scheduler.Tick();
            }

            Assert.AreEqual(3, rig.Scheduler.RecentAnchors.Count);
        }

        [TestMethod]
        public void NarrowSources_KeepsOnlyClearancesWithinMaxRadius()
        {
            float? Clearance(string id) => id switch { "tiny" => 3f, "edge" => 10f, "huge" => 25f, _ => null };

            var kept = WorldEventAnchorRules.NarrowSources(new[] { "tiny", "huge", "edge", "unknown" }, 10f, Clearance);

            CollectionAssert.AreEqual(new[] { "tiny", "edge" }, kept);
        }

        [TestMethod]
        public void NarrowedSourcesReachTheStart_NotTheFullWebList()
        {
            var rig = new Rig { Anchors = Make("a") };
            rig.Anchors[0].MaxRadius = 10;
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();

            CollectionAssert.AreEqual(new[] { "small" }, rig.Started.Single().AllowedSourceIds.ToArray());
        }

        [TestMethod]
        public void AnchorWhoseRadiusAdmitsNoSource_MovesOnToTheNext()
        {
            var rig = new Rig { Anchors = Make("a", "b") };
            rig.Anchors[0].MaxRadius = 10;
            rig.Anchors[1].MaxRadius = 2;    // nothing has clearance <= 2
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();

            // The rig's NextInt is fixed at 0, which puts the shuffle in the order b, a.
            CollectionAssert.AreEqual(new[] { "b", "a" }, rig.BuiltFor);
            Assert.AreEqual(1, rig.Started.Count);
            Assert.AreEqual("A", rig.Scheduler.LastOutcome.Anchor);
        }

        [TestMethod]
        public void ThreeRefusals_SkipTheOccurrenceWithTheLastRefusalText()
        {
            var rig = new Rig { BuildRefusal = anchor => $"no fit at {anchor.Id}" };
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();

            Assert.AreEqual(3, rig.BuiltFor.Count, "exactly three anchors are tried");
            Assert.AreEqual(0, rig.Started.Count);
            Assert.IsFalse(rig.Scheduler.LastOutcome.Started);
            StringAssert.Contains(rig.Scheduler.LastOutcome.Reason, $"no fit at {rig.BuiltFor[2]}");

            rig.At(5, 10, 1);
            rig.Scheduler.Tick();
            Assert.AreEqual(3, rig.BuiltFor.Count, "a skipped occurrence is not retried");
        }

        [TestMethod]
        public void StartRefusals_AreTriedOnAnotherAnchor_AndTheThirdSkips()
        {
            var rig = new Rig();
            rig.StartFn = request => (WorldEventSchedulerStart.Refused, 0u, "composition failed");
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();

            Assert.AreEqual(3, rig.BuiltFor.Count);
            StringAssert.Contains(rig.Scheduler.LastOutcome.Reason, "composition failed");
        }

        [TestMethod]
        public void RefusalThenSuccess_StartsOnTheSecondAnchor()
        {
            var rig = new Rig();
            rig.BuildRefusal = anchor => anchor.Id == "b" ? "no fit" : null;   // the fixed shuffle builds b first
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();

            Assert.AreEqual(1, rig.Started.Count);
            Assert.AreEqual(2, rig.BuiltFor.Count);
            Assert.AreEqual("b", rig.BuiltFor[0]);
            Assert.AreNotEqual("B", rig.Scheduler.LastOutcome.Anchor);
        }

        [TestMethod]
        public void NoAnchorsAtAll_SkipsWithAReason()
        {
            var rig = new Rig { Anchors = new List<AnchorDef>() };
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();

            Assert.IsFalse(rig.Scheduler.LastOutcome.Started);
            StringAssert.Contains(rig.Scheduler.LastOutcome.Reason, "no anchor");
        }

        // ---- success, logging, status ------------------------------------------------------------------------

        [TestMethod]
        public void Success_RecordsTheRunAndAnchor_LogsAndAudits()
        {
            var rig = new Rig();
            rig.At(5, 10, 0);

            rig.Scheduler.Tick();

            var last = rig.Scheduler.LastOutcome;
            Assert.IsTrue(last.Started);
            Assert.AreEqual(100u, last.RunId);
            Assert.AreEqual("UTC 10:00 2027-05-05", last.SlotKey);
            Assert.AreEqual(1, rig.Scheduler.RecentAnchors.Count);

            Assert.IsTrue(rig.Info.Any(l => l.Contains("[WORLDEVENT-SCHED]") && l.Contains("UTC 10:00 2027-05-05") && l.Contains("run 100")));
            Assert.AreEqual(1, rig.Audits.Count);
            StringAssert.StartsWith(rig.Audits[0], "scheduler|");
            StringAssert.Contains(rig.Audits[0], "run 100");
        }

        [TestMethod]
        public void MalformedScheduleEntry_IsLoggedOnce_NotEveryTick()
        {
            var rig = new Rig { Schedule = "10:00, 25:99" };

            for (var i = 0; i < 5; i++)
            {
                rig.At(5, 10, 0, i);
                rig.Scheduler.Tick();
            }

            Assert.AreEqual(1, rig.Warn.Count(w => w.Contains("25:99")));
        }

        [TestMethod]
        public void LateWindowIsClampedToTwoHours()
        {
            var rig = new Rig { Late = 100000 };
            rig.At(5, 12, 1);

            rig.Scheduler.Tick();

            Assert.AreEqual(0, rig.Started.Count, "10:00 + 120 minutes is the most a slot can be late");

            rig.At(5, 11, 59);
            rig.Scheduler.Tick();
            Assert.AreEqual(1, rig.Started.Count);
        }

        [TestMethod]
        public void StatusLines_ShowNextAndLast()
        {
            var rig = new Rig();
            rig.At(5, 9, 0);

            var before = string.Join("\n", rig.Scheduler.StatusLines());
            StringAssert.Contains(before, "next 2027-05-05 10:00 UTC");
            StringAssert.Contains(before, "nothing since boot");

            rig.At(5, 10, 0);
            rig.Scheduler.Tick();

            var after = string.Join("\n", rig.Scheduler.StatusLines());
            StringAssert.Contains(after, "started run 100");
            StringAssert.Contains(after, "next 2027-05-06 10:00 UTC");
        }

        [TestMethod]
        public void KeysOlderThanTwoDays_ArePruned_SoTheSetStaysBounded()
        {
            var rig = new Rig();

            rig.At(1, 10, 0);
            rig.Scheduler.Tick();
            Assert.AreEqual(1, rig.Scheduler.ResolvedCount);

            // A day on, the key is still held; three days on it is gone. 12:00 is outside every window, so these ticks fire
            // nothing and only prune.
            rig.At(2, 12, 0);
            rig.Scheduler.Tick();
            Assert.AreEqual(1, rig.Scheduler.ResolvedCount);

            rig.At(4, 12, 0);
            rig.Scheduler.Tick();
            Assert.AreEqual(0, rig.Scheduler.ResolvedCount);
        }

        // ---- a seam that throws ------------------------------------------------------------------------------

        [TestMethod]
        public void ASeamThatThrows_SkipsTheOccurrenceOnce_AndIsNeverCalledAgain()
        {
            var rig = new Rig { BuildRefusal = _ => throw new InvalidOperationException("boom") };

            for (var second = 0; second < 300; second++)
            {
                rig.At(5, 10, 0);
                rig.Now = rig.Now.AddSeconds(second);
                rig.Scheduler.Tick();
            }

            Assert.AreEqual(1, rig.BuiltFor.Count, "the throwing seam is called once, not every second of the late window");
            Assert.AreEqual(0, rig.Started.Count);
            Assert.AreEqual(1, rig.Info.Count(l => l.Contains("skipped")), "exactly one skip");
            Assert.IsFalse(rig.Scheduler.LastOutcome.Started);
            StringAssert.StartsWith(rig.Scheduler.LastOutcome.Reason, "error: boom");
            Assert.AreEqual(1, rig.Warn.Count(w => w.Contains("InvalidOperationException")), "the stack is logged once");
        }

        [TestMethod]
        public void AStartThatThrows_NeverLetsASecondEventFire()
        {
            var rig = new Rig();
            rig.StartFn = request =>
            {
                rig.Started.Add(request);
                throw new InvalidOperationException("after the run took hold");
            };

            for (var second = 0; second < 120; second++)
            {
                rig.At(5, 10, 0);
                rig.Now = rig.Now.AddSeconds(second);
                rig.Scheduler.Tick();
            }

            Assert.AreEqual(1, rig.Started.Count);
        }

        // ---- the live Start seam's mapping -------------------------------------------------------------------

        [TestMethod]
        public void MapStart_AlreadyRunning_IsBusy_WithTheRunningRunId()
        {
            var (result, runId, error) = WorldEventScheduler.MapStart(false, 0, "an event is already running", WorldEventStartRefusal.AlreadyRunning, 41);

            Assert.AreEqual(WorldEventSchedulerStart.Busy, result);
            Assert.AreEqual(41u, runId);
            Assert.AreEqual("an event is already running", error);
        }

        [TestMethod]
        [DataRow(WorldEventStartRefusal.Disabled)]
        [DataRow(WorldEventStartRefusal.NoAxes)]
        [DataRow(WorldEventStartRefusal.None)]
        public void MapStart_AnyOtherRefusal_IsRefused(WorldEventStartRefusal refusal)
        {
            var (result, runId, error) = WorldEventScheduler.MapStart(false, 0, "no", refusal, 0);

            Assert.AreEqual(WorldEventSchedulerStart.Refused, result);
            Assert.AreEqual(0u, runId);
            Assert.AreEqual("no", error);
        }

        [TestMethod]
        public void MapStart_Started_CarriesTheRunId_AndARunlessSuccessIsRefused()
        {
            Assert.AreEqual((WorldEventSchedulerStart.Started, 77u, (string)null), WorldEventScheduler.MapStart(true, 77, null, WorldEventStartRefusal.None, 0));
            Assert.AreEqual(WorldEventSchedulerStart.Refused, WorldEventScheduler.MapStart(true, 0, null, WorldEventStartRefusal.None, 0).Result);
        }
    }
}
