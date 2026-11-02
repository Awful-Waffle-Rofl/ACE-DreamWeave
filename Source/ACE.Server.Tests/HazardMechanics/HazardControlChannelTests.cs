using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>HazardRun.Control (any thread) and HazardContext.TakeControls (landblock thread): the runtime control inbox.</summary>
    [TestClass]
    public class HazardControlChannelTests
    {
        private static HazardRun NewRun()
            => new HazardRun(1, "test", null, HazardRegionPresets.Pinnacle(0), HazardArgs.Empty, 1, null, null, null, DateTime.UtcNow);

        [TestMethod]
        public void Control_DrainsInOrder_ThenEmpty()
        {
            var run = NewRun();

            Assert.IsTrue(run.Control("rate", 0.1));
            Assert.IsTrue(run.Control("rate", 0.2));
            Assert.IsTrue(run.Control("stop", 1));

            var first = run.Context.TakeControls();

            Assert.AreEqual(3, first.Count);
            Assert.AreEqual("rate", first[0].Key);
            Assert.AreEqual(0.1, first[0].Value, 1e-12);
            Assert.AreEqual("rate", first[1].Key);
            Assert.AreEqual(0.2, first[1].Value, 1e-12);
            Assert.AreEqual("stop", first[2].Key);
            Assert.AreEqual(1.0, first[2].Value, 1e-12);

            Assert.AreEqual(0, run.Context.TakeControls().Count, "the second drain is empty");
            Assert.AreEqual(0, run.PendingControlCount);
        }

        [TestMethod]
        public void Control_NormalizesKey_RefusesBlankNonFiniteAndEnded()
        {
            var run = NewRun();

            Assert.IsTrue(run.Control("  RATE ", 0.5));
            Assert.AreEqual("rate", run.Context.TakeControls().Single().Key);

            Assert.IsFalse(run.Control("", 1));
            Assert.IsFalse(run.Control("   ", 1));
            Assert.IsFalse(run.Control(null, 1));
            Assert.IsFalse(run.Control("rate", double.NaN));
            Assert.IsFalse(run.Control("rate", double.PositiveInfinity));
            Assert.AreEqual(0, run.PendingControlCount, "a refused control is never enqueued");

            Assert.IsTrue(run.TryClaimEnd(HazardOutcome.Stopped, "t", out _));
            Assert.IsFalse(run.Control("rate", 1), "an ended run takes no controls");
            Assert.AreEqual(0, run.PendingControlCount);
        }

        [TestMethod]
        public void Control_RefusesThe65th()
        {
            var run = NewRun();

            for (var i = 0; i < HazardDefaults.MaxPendingControls; i++)
                Assert.IsTrue(run.Control("rate", i), "control " + i);

            Assert.IsFalse(run.Control("rate", 99));
            Assert.AreEqual(64, run.PendingControlCount);
            Assert.AreEqual(64, run.Context.TakeControls().Count);
            Assert.IsTrue(run.Control("rate", 1), "draining makes room again");
        }

        [TestMethod]
        public void Control_IsThreadSafe()
        {
            var run = NewRun();
            var accepted = 0;
            var threads = new List<Thread>();

            for (var t = 0; t < 4; t++)
            {
                var th = new Thread(() =>
                {
                    for (var i = 0; i < 16; i++)
                        if (run.Control("rate", i))
                            Interlocked.Increment(ref accepted);
                });
                threads.Add(th);
                th.Start();
            }

            foreach (var th in threads)
                th.Join();

            Assert.AreEqual(64, accepted, "4 threads x 16 all fit under the cap");
            Assert.AreEqual(64, run.Context.TakeControls().Count);
        }
    }
}
