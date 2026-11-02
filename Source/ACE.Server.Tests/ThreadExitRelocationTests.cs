using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /spawncache moves the run's exit portal with the cache. The swap rule is ThreadDungeonRewardSpawner.TryRelocateExit,
    /// a seam over injected create/destroy so it runs without a landblock.
    /// </summary>
    [TestClass]
    public class ThreadExitRelocationTests
    {
        [TestMethod]
        public void NoOldPortal_NoCreateCall()
        {
            var calls = new List<string>();

            var moved = ThreadDungeonRewardSpawner.TryRelocateExit(new List<string>(), true, () => { calls.Add("create"); return new object(); }, p => calls.Add("destroy " + p));

            Assert.IsFalse(moved);
            Assert.AreEqual(0, calls.Count);
        }

        [TestMethod]
        public void CreateFailure_LeavesTheOldPortalsUntouched()
        {
            var calls = new List<string>();

            var moved = ThreadDungeonRewardSpawner.TryRelocateExit(new List<string> { "old1", "old2" }, true, () => { calls.Add("create"); return null; }, p => calls.Add("destroy " + p));

            Assert.IsFalse(moved);
            CollectionAssert.AreEqual(new[] { "create" }, calls);
        }

        [TestMethod]
        public void Success_CreatesBeforeItDestroys()
        {
            var calls = new List<string>();

            var moved = ThreadDungeonRewardSpawner.TryRelocateExit(new List<string> { "old1", "old2" }, true, () => { calls.Add("create"); return new object(); }, p => calls.Add("destroy " + p));

            Assert.IsTrue(moved);
            CollectionAssert.AreEqual(new[] { "create", "destroy old1", "destroy old2" }, calls);
        }

        [TestMethod]
        public void GroupNonOwner_LeavesTheExitUntouched()
        {
            Assert.IsFalse(ThreadDungeonRewardSpawner.AllowsExitRelocation(isGroupRun: true, isOwner: false));

            var calls = new List<string>();
            var allowed = ThreadDungeonRewardSpawner.AllowsExitRelocation(true, false);

            var moved = ThreadDungeonRewardSpawner.TryRelocateExit(new List<string> { "old" }, allowed, () => { calls.Add("create"); return new object(); }, p => calls.Add("destroy"));

            Assert.IsFalse(moved);
            Assert.AreEqual(0, calls.Count);
        }

        [DataTestMethod]
        [DataRow(false, false, true)]
        [DataRow(false, true, true)]
        [DataRow(true, true, true)]
        [DataRow(true, false, false)]
        public void AllowsExitRelocation_Table(bool isGroup, bool isOwner, bool expected)
        {
            Assert.AreEqual(expected, ThreadDungeonRewardSpawner.AllowsExitRelocation(isGroup, isOwner));
        }
    }
}
