using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /marketbackfill parsing and dispatch, with no Session and no shard. The dispatch half exists
    /// for the reason MarketAdminCommands' does, only more so: the two arms here are "report" and
    /// "write", and a transposed switch arm compiles, ships, and silently rewrites 100 listings for
    /// somebody who typed preview.
    /// </summary>
    [TestClass]
    public class MarketBackfillCommandParseTests
    {
        private static MarketBackfillCommandResult Parse(params string[] parameters)
            => MarketBackfillCommandParser.Parse(parameters);

        [TestMethod]
        public void NoArguments_IsHelp()
        {
            Assert.AreEqual(MarketBackfillCommandKind.Help, Parse().Kind);
            Assert.AreEqual(MarketBackfillCommandKind.Help, MarketBackfillCommandParser.Parse(null).Kind);
            Assert.AreEqual(MarketBackfillCommandKind.Help, Parse("help").Kind);
            Assert.AreEqual(MarketBackfillCommandKind.Help, Parse("?").Kind);
        }

        [TestMethod]
        public void UnknownSubcommand_IsAUsageError()
        {
            var result = Parse("rebuild");

            Assert.AreEqual(MarketBackfillCommandKind.UsageError, result.Kind);
            Assert.AreEqual(MarketBackfillCommandParser.UsageMessage, result.UsageError);
        }

        [TestMethod]
        public void Preview_TakesTheDefaultPassSize()
        {
            var result = Parse("preview");

            Assert.AreEqual(MarketBackfillCommandKind.Preview, result.Kind);
            Assert.AreEqual(MarketBackfillCommandParser.DefaultMax, result.Max);
        }

        [TestMethod]
        public void Run_TakesTheDefaultPassSize()
        {
            var result = Parse("run");

            Assert.AreEqual(MarketBackfillCommandKind.Run, result.Kind);
            Assert.AreEqual(MarketBackfillCommandParser.DefaultMax, result.Max);
        }

        [TestMethod]
        public void BothArms_TakeAnExplicitPassSize()
        {
            Assert.AreEqual(25, Parse("preview", "25").Max);
            Assert.AreEqual(25, Parse("run", "25").Max);
        }

        [TestMethod]
        public void APassSizeOverTheCap_IsClampedRatherThanRefused()
        {
            Assert.AreEqual(MarketBackfillCommandParser.MaxMax, Parse("run", "5000").Max);
            Assert.AreEqual(MarketBackfillCommandParser.MaxMax, Parse("preview", "5000").Max);
        }

        [TestMethod]
        public void ThePassSizeCap_IsTheManagersOwn()
        {
            // Two constants for one ceiling is how a command starts asking for more rows than the
            // manager will walk and quietly reporting a short pass as a complete one.
            Assert.AreEqual(MarketManager.MaxBackfillRows, MarketBackfillCommandParser.MaxMax);
        }

        [TestMethod]
        public void AnUnparsablePassSize_IsAUsageErrorAndNeverTheDefault()
        {
            // This command WRITES. Silently walking 100 listings for somebody who asked for 5 does
            // something they did not ask for.
            Assert.AreEqual(MarketBackfillCommandKind.UsageError, Parse("run", "lots").Kind);
            Assert.AreEqual(MarketBackfillCommandKind.UsageError, Parse("run", "0").Kind);
            Assert.AreEqual(MarketBackfillCommandKind.UsageError, Parse("run", "-3").Kind);
            Assert.AreEqual(MarketBackfillCommandKind.UsageError, Parse("preview", "soon").Kind);
        }

        [TestMethod]
        public void Subcommands_AreCaseInsensitive()
        {
            Assert.AreEqual(MarketBackfillCommandKind.Preview, Parse("PREVIEW").Kind);
            Assert.AreEqual(MarketBackfillCommandKind.Run, Parse("Run").Kind);
        }

        // ---- dispatch ----

        private sealed class RecordingBackfillTarget : IMarketBackfillCommandTarget
        {
            public readonly List<string> Calls = new List<string>();

            // The full-sweep arm gets its OWN recorded name rather than a trailing flag, so a test
            // that means "an ordinary version-gated run" cannot pass when a full sweep was
            // dispatched instead. Those two differ by how much work they do against a live market.
            public void Preview(int max, bool allVersions) => Calls.Add($"preview{(allVersions ? "-all" : string.Empty)}:{max}");
            public void Run(int max, bool allVersions) => Calls.Add($"run{(allVersions ? "-all" : string.Empty)}:{max}");
            public void Reset() => Calls.Add("reset");
            public void ShowHelp() => Calls.Add("help");
            public void UsageError(string message) => Calls.Add($"usage:{message}");
        }

        private static string DispatchOne(params string[] parameters)
        {
            var target = new RecordingBackfillTarget();

            MarketBackfillCommands.Dispatch(MarketBackfillCommandParser.Parse(parameters), target);

            Assert.AreEqual(1, target.Calls.Count, "exactly one target call per invocation");

            return target.Calls[0];
        }

        [TestMethod]
        public void Dispatch_RoutesEveryArmToItsOwnMethod()
        {
            Assert.AreEqual("preview:25", DispatchOne("preview", "25"));
            Assert.AreEqual("run:25", DispatchOne("run", "25"));
            Assert.AreEqual("reset", DispatchOne("reset"));
            Assert.AreEqual("help", DispatchOne("help"));
        }

        [TestMethod]
        public void Reset_TakesNoArguments()
        {
            Assert.AreEqual(MarketBackfillCommandKind.Reset, Parse("reset").Kind);
            Assert.AreEqual(MarketBackfillCommandKind.Reset, Parse("RESET").Kind);
        }

        [TestMethod]
        public void Dispatch_NeverTurnsAPreviewIntoARun()
        {
            StringAssert.StartsWith(DispatchOne("preview"), "preview:");
            StringAssert.StartsWith(DispatchOne("run"), "run:");
        }

        [TestMethod]
        public void Dispatch_SendsAUsageErrorToUsageErrorAndNowhereElse()
        {
            StringAssert.StartsWith(DispatchOne("rebuild"), "usage:");
            StringAssert.StartsWith(DispatchOne("run", "0"), "usage:");
        }

        /// <summary>
        /// Every kind must have an arm. A new kind that fell through to the default would silently
        /// render a usage error instead of doing what it was added for.
        /// </summary>
        [TestMethod]
        public void Dispatch_HasAnArmForEveryKind()
        {
            foreach (MarketBackfillCommandKind kind in Enum.GetValues(typeof(MarketBackfillCommandKind)))
            {
                var target = new RecordingBackfillTarget();

                MarketBackfillCommands.Dispatch(new MarketBackfillCommandResult { Kind = kind, Max = 10 }, target);

                Assert.AreEqual(1, target.Calls.Count, $"{kind} did not reach exactly one target method");

                if (kind != MarketBackfillCommandKind.UsageError)
                    Assert.IsFalse(target.Calls[0].StartsWith("usage:"), $"{kind} fell through to the default arm");
            }
        }

        // ---- rendering ----

        [TestMethod]
        public void AnUninitializedIndex_RendersAsSuchAndNotAsACleanPass()
        {
            var text = PlayerMarketBackfillCommandTarget.Render(new MarketBackfillReport { NotInitialized = true }, false);

            Assert.AreEqual(PlayerMarketBackfillCommandTarget.NotInitializedMessage, text);
        }

        [TestMethod]
        public void ARemainingCount_TellsTheOperatorToRepeatTheSameCommand()
        {
            var report = new MarketBackfillReport { Considered = 500, Rewritten = 500, RemainingActive = 42 };

            var text = PlayerMarketBackfillCommandTarget.Render(report, false);

            StringAssert.Contains(text, "RUN THE SAME COMMAND AGAIN");
            StringAssert.Contains(text, "42 Active listing(s) still ahead");

            // The advice must not name a bigger n. The walk carries a cursor, so repeating the
            // command advances; telling an operator at the cap to "use a larger n" was a no-op that
            // re-walked the identical page forever.
            StringAssert.Contains(text, "Each pass resumes; it does not restart.");
            Assert.IsFalse(text.Contains("larger n"), "a bigger n is not the remedy and must not be suggested");
        }

        [TestMethod]
        public void ACompletedSweep_SaysSoAndPromisesAFreshStart()
        {
            var report = new MarketBackfillReport { Considered = 12, Rewritten = 3, SweepComplete = true };

            var text = PlayerMarketBackfillCommandTarget.Render(report, false);

            StringAssert.Contains(text, "Sweep complete");
            Assert.IsFalse(text.Contains("RUN THE SAME COMMAND AGAIN"));
        }

        [TestMethod]
        public void AResumedPass_SaysWhereItPickedUp()
        {
            var report = new MarketBackfillReport
            {
                Considered = 5,
                ResumedFromAccountId = 7001,
                ResumedFromListingId = 500,
                SweepComplete = true,
            };

            StringAssert.Contains(PlayerMarketBackfillCommandTarget.Render(report, false),
                                  "Resumed after listing #500 (account 7001)");
        }

        [TestMethod]
        public void AWriteFailure_IsRenderedAsAFailureAndNotAsAClose()
        {
            var report = new MarketBackfillReport { Considered = 3, Rewritten = 2, WriteFailed = 1, SweepComplete = true };

            var text = PlayerMarketBackfillCommandTarget.Render(report, false);

            StringAssert.Contains(text, "WRITE FAILURES");
            StringAssert.Contains(text, "NOT concurrent closes");
        }

        [TestMethod]
        public void APreview_SaysItWroteNothing()
        {
            var report = new MarketBackfillReport { Considered = 3, Rewritten = 3 };

            var text = PlayerMarketBackfillCommandTarget.Render(report, true);

            StringAssert.Contains(text, "PREVIEW");
            StringAssert.Contains(text, "nothing was written");
        }

        [TestMethod]
        public void EverySkipCounter_IsRenderedEvenAtZero()
        {
            // A report that hides its zeros makes "nothing was skipped" indistinguishable from "the
            // counter was not printed", which is the one thing an operator reads this output for.
            var text = PlayerMarketBackfillCommandTarget.Render(new MarketBackfillReport(), false);

            foreach (var label in new[]
                     {
                         "considered", "already current", "vault not ready", "item unresolved",
                         "projection failed", "projection worse", "closed mid pass", "write failed",
                     })
            {
                StringAssert.Contains(text, label);
            }
        }

        // ---- the full-sweep arm ----

        [TestMethod]
        public void TheAllKeyword_TurnsAPassIntoAFullSweep()
        {
            var run = Parse("run", "all");

            Assert.AreEqual(MarketBackfillCommandKind.Run, run.Kind);
            Assert.IsTrue(run.AllVersions, "`all` is what ignores the version stamp");
            Assert.AreEqual(MarketBackfillCommandParser.DefaultMax, run.Max, "and it still defaults n");

            var preview = Parse("preview", "all");

            Assert.AreEqual(MarketBackfillCommandKind.Preview, preview.Kind);
            Assert.IsTrue(preview.AllVersions);
        }

        [TestMethod]
        public void AFullSweep_StillTakesACount()
        {
            var result = Parse("run", "all", "250");

            Assert.IsTrue(result.AllVersions);
            Assert.AreEqual(250, result.Max);
        }

        [TestMethod]
        public void WithoutAll_APassIsVersionGated()
        {
            // The default has to be the CHEAP arm. A full sweep re-reads every seller's vault and
            // re-projects every listed item, so getting this backwards makes the automatic pass do
            // that on every world start.
            Assert.IsFalse(Parse("run").AllVersions);
            Assert.IsFalse(Parse("run", "250").AllVersions);
            Assert.IsFalse(Parse("preview").AllVersions);
        }

        [TestMethod]
        public void Dispatch_KeepsTheFullSweepArmDistinct()
        {
            Assert.AreEqual("run-all:100", DispatchOne("run", "all"));
            Assert.AreEqual("preview-all:100", DispatchOne("preview", "all"));
            Assert.AreEqual("run:100", DispatchOne("run"));
        }

        [TestMethod]
        public void TheStaleCount_IsAlwaysRendered()
        {
            // On a full sweep RemainingActive counts every Active row rather than the out-of-date
            // ones, so without this line the operator has no number at all for "how much is actually
            // legacy" - which is the question the whole versioning scheme exists to answer.
            var report = new MarketBackfillReport { StaleActive = 7 };

            StringAssert.Contains(PlayerMarketBackfillCommandTarget.Render(report, true), "7");
            StringAssert.Contains(PlayerMarketBackfillCommandTarget.Render(report, true), "out of date");
            StringAssert.Contains(PlayerMarketBackfillCommandTarget.Render(report, false, true), "out of date");
        }

        [TestMethod]
        public void AFullSweep_SaysItIgnoredTheVersionGate()
        {
            var text = PlayerMarketBackfillCommandTarget.Render(new MarketBackfillReport(), false, true);

            StringAssert.Contains(text, "FULL SWEEP",
                                  "an operator must be able to tell the expensive arm from the cheap one in the output");
        }

        [TestMethod]
        public void TheHelpText_DocumentsTheFullSweepArm()
        {
            StringAssert.Contains(MarketBackfillCommandParser.HelpText, "all");
        }
    }
}
