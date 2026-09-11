using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="ACE.Server.Program.EvaluateDbPatchScriptResult"/>, the pure per-script
    /// decision helper used by the boot-time SQL update patcher (Program_DbUpdates.cs). No database or
    /// filesystem is touched -- the helper is a pure function of its inputs.
    /// </summary>
    [TestClass]
    public class DbPatcherTests
    {
        [TestMethod]
        public void Success_RecordsNameAndContinues()
        {
            var remaining = new List<string> { "0002.sql", "0003.sql" };

            var result = Program.EvaluateDbPatchScriptResult("0001.sql", remaining, applied: true);

            Assert.AreEqual("0001.sql", result.RecordedFileName);
            Assert.AreEqual(Program.DbPatchOutcome.Continue, result.Outcome);
            Assert.AreEqual(0, result.SkippedCount);
        }

        [TestMethod]
        public void Failure_RecordsNothingAndStops()
        {
            var remaining = new List<string> { "0002.sql", "0003.sql" };

            var result = Program.EvaluateDbPatchScriptResult("0001.sql", remaining, applied: false);

            Assert.IsNull(result.RecordedFileName);
            Assert.AreEqual(Program.DbPatchOutcome.Stop, result.Outcome);
        }

        [TestMethod]
        public void Failure_SkippedCountEqualsRemainingScripts()
        {
            var remaining = new List<string> { "0002.sql", "0003.sql", "0004.sql" };

            var result = Program.EvaluateDbPatchScriptResult("0001.sql", remaining, applied: false);

            Assert.AreEqual(remaining.Count, result.SkippedCount);
        }

        [TestMethod]
        public void Failure_AlreadyAppliedRemainingScriptsExcludedFromSkippedCount()
        {
            // 0001 fails; 0002 is already recorded as applied; 0003 and 0004 are still pending.
            // Only 0003 and 0004 are really going to be skipped by the Stop.
            var remainingOnDisk = new List<string> { "0002.sql", "0003.sql", "0004.sql" };
            var appliedUpdates = new List<string> { "0002.sql" };

            var pending = Program.GetPendingFileNames(remainingOnDisk, appliedUpdates);
            var result = Program.EvaluateDbPatchScriptResult("0001.sql", pending, applied: false);

            CollectionAssert.AreEqual(new List<string> { "0003.sql", "0004.sql" }, pending);
            Assert.AreEqual(2, result.SkippedCount);
        }
    }
}
