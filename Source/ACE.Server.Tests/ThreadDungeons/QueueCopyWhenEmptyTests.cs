using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// ThreadDungeonManager.QueueCopyForDestructionWhenEmpty, the tail of EndRun that the PvP arena's
    /// EphemeralMatchSpaceProvider.Release now shares. A Landblock cannot be constructed here, and the real
    /// destruction queue and watch set are process-wide statics, so the branch is tested through the generic seam
    /// with stand-ins. The production overload's wiring (queue now = LandblockManager.AddToDestructionQueue, watch =
    /// endedOccupiedCopies) is pinned by a comment-stripped source-text check, and the "queued on the first tick that
    /// finds it empty" half is driven through DecideEndedCopy, the function QueueEmptiedEndedCopies switches on.
    /// </summary>
    [TestClass]
    public class QueueCopyWhenEmptyTests
    {
        private sealed class Copy
        {
        }

        [TestMethod]
        public void Empty_GoesStraightToTheDestructionQueue()
        {
            var copy = new Copy();
            var queued = new List<Copy>();
            var watched = new List<Copy>();

            ThreadDungeonManager.QueueCopyForDestructionWhenEmpty(copy, occupied: false, queued.Add, watched.Add);

            CollectionAssert.AreEqual(new[] { copy }, queued);
            Assert.AreEqual(0, watched.Count);
        }

        [TestMethod]
        public void Occupied_IsWatchedNotQueued()
        {
            var copy = new Copy();
            var queued = new List<Copy>();
            var watched = new List<Copy>();

            ThreadDungeonManager.QueueCopyForDestructionWhenEmpty(copy, occupied: true, queued.Add, watched.Add);

            Assert.AreEqual(0, queued.Count);
            CollectionAssert.AreEqual(new[] { copy }, watched);
        }

        [TestMethod]
        public void NullCopy_DoesNothing()
        {
            var calls = 0;

            ThreadDungeonManager.QueueCopyForDestructionWhenEmpty<Copy>(null, occupied: false, _ => calls++, _ => calls++);
            ThreadDungeonManager.QueueCopyForDestructionWhenEmpty<Copy>(null, occupied: true, _ => calls++, _ => calls++);

            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void WatchedCopy_IsQueuedOnTheFirstTickThatFindsItEmpty()
        {
            // Drives the per-entry decision of QueueEmptiedEndedCopies across successive ticks: the copy is still
            // live (same reference), occupied for two ticks, then empty. Queue must first appear on tick 3.
            var copy = new Copy();
            var occupancyByTick = new[] { true, true, false, false };

            var actions = occupancyByTick.Select(inside => ThreadDungeonManager.DecideEndedCopy(copy, copy, inside)).ToList();

            CollectionAssert.AreEqual(new[]
            {
                ThreadDungeonManager.EndedCopyAction.Wait,
                ThreadDungeonManager.EndedCopyAction.Wait,
                ThreadDungeonManager.EndedCopyAction.Queue,
                ThreadDungeonManager.EndedCopyAction.Queue,
            }, actions);
            Assert.AreEqual(2, actions.IndexOf(ThreadDungeonManager.EndedCopyAction.Queue));
        }

        [TestMethod]
        public void ProductionOverload_WiresTheRealQueueAndTheWatchSet()
        {
            var source = StripComments(File.ReadAllText(FindSource("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs")));
            var compact = Regex.Replace(source, @"\s+", "");

            StringAssert.Contains(compact,
                "QueueCopyForDestructionWhenEmpty(landblock,occupied,LandblockManager.AddToDestructionQueue,copy=>endedOccupiedCopies[copy.Instance]=copy)",
                "the Landblock overload must queue now through AddToDestructionQueue and watch through endedOccupiedCopies, in that argument order");

            StringAssert.Contains(compact, "QueueCopyForDestructionWhenEmpty(landblock,occupied:players.Count>0)",
                "EndRun must still route through the shared helper with its occupancy scan");
        }

        private static string StripComments(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(text, @"//[^\n]*", "");
        }

        private static string FindSource(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
