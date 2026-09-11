using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Fix round 1, F1: MuleCommands.Dispatch had zero test coverage, and it sits on the authorization
    /// surface - the Grant and Revoke arms could be transposed (@mule grant Bob withdraw REVOKES,
    /// @mule revoke Bob GRANTS WITHDRAW ACCESS) with the full suite staying green, because every
    /// existing test drove either MuleCommandParser.Parse or AccountVaultStore directly and nothing
    /// executed the switch itself.
    ///
    /// Driven through the reviewer's seam (IMuleCommandTarget), so it needs no live Session or Player.
    /// A RecordingTarget logs exactly which method was called with which arguments, one call per
    /// MuleCommandKind.
    /// </summary>
    [TestClass]
    public class MuleCommandDispatchTests
    {
        /// <summary>
        /// One recording method per IMuleCommandTarget member, deliberately - see the interface's own
        /// remarks. A new arm added to the dispatch switch without a case here fails to compile, which
        /// is how MuleHelp was caught the moment it was added.
        /// </summary>
        private sealed class RecordingTarget : IMuleCommandTarget
        {
            public string LastCall;
            public string LastName;
            public bool LastCanWithdraw;
            public string LastUsageMessage;
            public string LastPattern;
            public int CallCount;

            public void SummonMule(string playerName)
            {
                LastCall = nameof(SummonMule);
                LastName = playerName;
                CallCount++;
            }

            public void MuleGrant(string name, bool canWithdraw)
            {
                LastCall = nameof(MuleGrant);
                LastName = name;
                LastCanWithdraw = canWithdraw;
                CallCount++;
            }

            public void MuleRevoke(string name)
            {
                LastCall = nameof(MuleRevoke);
                LastName = name;
                CallCount++;
            }

            public void MuleAccess()
            {
                LastCall = nameof(MuleAccess);
                CallCount++;
            }

            public void MuleLog()
            {
                LastCall = nameof(MuleLog);
                CallCount++;
            }

            public void MuleSearch(string pattern)
            {
                LastCall = nameof(MuleSearch);
                LastPattern = pattern;
                CallCount++;
            }

            public void MuleHelp()
            {
                LastCall = nameof(MuleHelp);
                CallCount++;
            }

            public void UsageError(string message)
            {
                LastCall = nameof(UsageError);
                LastUsageMessage = message;
                CallCount++;
            }
        }

        [TestMethod]
        public void SummonOwn_CallsSummonMule_WithAnEmptyName()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.SummonOwn(), target);

            Assert.AreEqual(1, target.CallCount);
            Assert.AreEqual(nameof(RecordingTarget.SummonMule), target.LastCall);
            Assert.AreEqual("", target.LastName, "SummonOwn must pass an empty name, not null, matching HandleActionSummonMule's contract");
        }

        [TestMethod]
        public void SummonOther_CallsSummonMule_WithTheParsedName()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.SummonOther("Bob"), target);

            Assert.AreEqual(1, target.CallCount);
            Assert.AreEqual(nameof(RecordingTarget.SummonMule), target.LastCall);
            Assert.AreEqual("Bob", target.LastName);
        }

        /// <summary>
        /// The finding itself: Grant must call MuleGrant, never MuleRevoke. Before the fix, transposing
        /// the two switch arms left this (and Revoke_CallsMuleRevoke_NeverMuleGrant below) as the only
        /// tests in the whole suite that could catch it.
        /// </summary>
        [TestMethod]
        public void Grant_CallsMuleGrant_NeverMuleRevoke()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.Grant("Bob", true), target);

            Assert.AreEqual(1, target.CallCount);
            Assert.AreEqual(nameof(RecordingTarget.MuleGrant), target.LastCall,
                "a Grant result must dispatch to MuleGrant - a transposed switch arm would call MuleRevoke instead and this must fail");
            Assert.AreEqual("Bob", target.LastName);
            Assert.IsTrue(target.LastCanWithdraw);
        }

        [TestMethod]
        public void Revoke_CallsMuleRevoke_NeverMuleGrant()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.Revoke("Bob"), target);

            Assert.AreEqual(1, target.CallCount);
            Assert.AreEqual(nameof(RecordingTarget.MuleRevoke), target.LastCall,
                "a Revoke result must dispatch to MuleRevoke - a transposed switch arm would call MuleGrant (with withdraw access) instead and this must fail");
            Assert.AreEqual("Bob", target.LastName);
        }

        [TestMethod]
        public void Access_CallsMuleAccess()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.Access(), target);

            Assert.AreEqual(1, target.CallCount);
            Assert.AreEqual(nameof(RecordingTarget.MuleAccess), target.LastCall);
        }

        [TestMethod]
        public void Log_CallsMuleLog()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.Log(), target);

            Assert.AreEqual(1, target.CallCount);
            Assert.AreEqual(nameof(RecordingTarget.MuleLog), target.LastCall);
        }

        [TestMethod]
        public void UsageError_CallsUsageError_WithTheMessage()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.Error("Usage: /mule grant <name> [withdraw]"), target);

            Assert.AreEqual(1, target.CallCount);
            Assert.AreEqual(nameof(RecordingTarget.UsageError), target.LastCall);
            Assert.AreEqual("Usage: /mule grant <name> [withdraw]", target.LastUsageMessage);
        }
        [TestMethod]
        public void Search_DispatchesToMuleSearch_WithTheParsedPattern_ExactlyOnce()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.Search("legendary coord|legendary quick"), target);

            Assert.AreEqual(1, target.CallCount);
            Assert.AreEqual(nameof(RecordingTarget.MuleSearch), target.LastCall);
            Assert.AreEqual("legendary coord|legendary quick", target.LastPattern);
        }

        /// <summary>
        /// /mule help is a dispatch arm like any other rather than something the command handler does
        /// inline, so a future refactor of the switch cannot silently drop it.
        /// </summary>
        [TestMethod]
        public void Help_DispatchesToMuleHelp()
        {
            var target = new RecordingTarget();

            MuleCommands.Dispatch(MuleCommandResult.Help(), target);

            Assert.AreEqual("MuleHelp", target.LastCall);
            Assert.AreEqual(1, target.CallCount);
        }

    }
}
