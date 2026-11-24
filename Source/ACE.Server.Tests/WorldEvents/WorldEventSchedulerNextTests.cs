using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.Tests.WorldEvents
{
    /// <summary>WorldEventScheduler.NextOccurrenceUtc, the private-event purchase's schedule-buffer input.</summary>
    [TestClass]
    public class WorldEventSchedulerNextTests
    {
        private static WorldEventScheduler Make(DateTime now, bool enabled, string schedule, bool eventRunning = false,
            Func<WorldEventRequest, (WorldEventSchedulerStart, uint, string)> start = null)
        {
            return new WorldEventScheduler(new WorldEventSchedulerSeams
            {
                UtcNow = () => now,
                Enabled = () => enabled,
                ScheduleText = () => schedule,
                DefaultZoneId = () => "UTC",
                LateMinutes = () => 5,
                ResolveZone = _ => TimeZoneInfo.Utc,
                ShutdownPending = () => false,
                EventRunning = () => eventRunning,
                Anchors = () => new List<AnchorDef> { new AnchorDef { Id = "a", DisplayName = "A", CellId = 1, MaxRadius = 10 } },
                BuildRequest = anchor => (new WorldEventRequest { Random = true }, null),
                ClearanceOf = id => null,
                Start = start ?? (r => (WorldEventSchedulerStart.Started, 1u, null)),
                NextInt = _ => 0,
            });
        }

        [TestMethod]
        public void Next_ReturnsEarliestFutureSlot()
        {
            var now = new DateTime(2027, 5, 5, 10, 30, 0, DateTimeKind.Utc);

            var next = Make(now, true, "09:00, 14:00, 12:15").NextOccurrenceUtc();

            Assert.AreEqual(new DateTime(2027, 5, 5, 12, 15, 0, DateTimeKind.Utc), next);
        }

        [TestMethod]
        public void Next_NullWhenDisabled()
        {
            var now = new DateTime(2027, 5, 5, 10, 30, 0, DateTimeKind.Utc);

            Assert.IsNull(Make(now, false, "12:15").NextOccurrenceUtc());
        }

        [TestMethod]
        public void Next_DueButUnresolvedSlot_ReturnsItsPastStart()
        {
            // 12:00 is inside its late window at 12:02 and has not fired (no Tick ran): it is imminent, so the delta is negative.
            var now = new DateTime(2027, 5, 5, 12, 2, 0, DateTimeKind.Utc);

            var next = Make(now, true, "12:00, 18:00").NextOccurrenceUtc();

            Assert.AreEqual(new DateTime(2027, 5, 5, 12, 0, 0, DateTimeKind.Utc), next);
            Assert.IsTrue(next < now);
        }

        [TestMethod]
        public void Next_ResolvedSlot_IsNotReturned()
        {
            var now = new DateTime(2027, 5, 5, 12, 2, 0, DateTimeKind.Utc);
            var scheduler = Make(now, true, "12:00, 18:00", eventRunning: false, start: r => (WorldEventSchedulerStart.Started, 1, null));

            scheduler.Tick();

            Assert.AreEqual(new DateTime(2027, 5, 5, 18, 0, 0, DateTimeKind.Utc), scheduler.NextOccurrenceUtc(), "the fired slot is resolved, so the next one is 18:00");
        }

        [TestMethod]
        public void Next_SlotWaitingBehindARunningEvent_ReturnsItsPastStart()
        {
            var now = new DateTime(2027, 5, 5, 12, 2, 0, DateTimeKind.Utc);
            var scheduler = Make(now, true, "12:00, 18:00", eventRunning: true);

            scheduler.Tick();

            Assert.AreEqual(new DateTime(2027, 5, 5, 12, 0, 0, DateTimeKind.Utc), scheduler.NextOccurrenceUtc());
        }
    }
}
