using System;

using ACE.Server.BalanceTrials;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>Pins the stage data-dir resolution. Pure: reads no PropertyManager key and touches no disk.</summary>
    [TestClass]
    public class TrialDataDirTests
    {
        [TestMethod]
        public void Resolve_prefers_configured_then_external_BalanceTrials_then_null_with_reason()
        {
            // a non-empty configured value wins even when it does not exist and the external dir has one
            var dir = TrialDataDir.Resolve("/custom/trials", "/ace/Config", p => true, out var reason);
            Assert.AreEqual("/custom/trials", dir);
            Assert.IsNull(reason);

            // the configured value is trimmed
            Assert.AreEqual("/custom/trials", TrialDataDir.Resolve("  /custom/trials \t", "/ace/Config", p => true, out _));

            // otherwise <external>/BalanceTrials when it exists
            string probed = null;
            dir = TrialDataDir.Resolve("", "/ace/Config", p => { probed = p; return true; }, out reason);
            Assert.AreEqual(System.IO.Path.Combine("/ace/Config", "BalanceTrials"), dir);
            Assert.AreEqual(dir, probed);
            Assert.IsNull(reason);

            // whitespace counts as unset
            dir = TrialDataDir.Resolve("  ", "/ace/Config", p => true, out reason);
            Assert.AreEqual(System.IO.Path.Combine("/ace/Config", "BalanceTrials"), dir);

            // directory missing: null, and the reason names the setting
            dir = TrialDataDir.Resolve("", "/ace/Config", p => false, out reason);
            Assert.IsNull(dir);
            StringAssert.Contains(reason, "balance_trials_data_dir");

            // no external dir at all: null, reason names the setting, and the probe is never asked about tools/
            var asked = false;
            dir = TrialDataDir.Resolve("", null, p => { asked = true; return true; }, out reason);
            Assert.IsNull(dir);
            Assert.IsFalse(asked);
            StringAssert.Contains(reason, "balance_trials_data_dir");
        }
    }
}
