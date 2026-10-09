using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The /vaultclassfold command's own decisions: argument parsing, the reciprocal guard against the
    /// read-only class commands, and how a report renders for an operator.
    ///
    /// These are the parts that can be driven without a Session, a world loop or a shard.
    /// AccountVaultFoldMigrationTests covers the engine; this covers the layer between the engine and the
    /// person typing.
    /// </summary>
    [TestClass]
    public class VaultClassFoldCommandTests
    {
        [TestInitialize]
        public void Setup()
        {
            // TryBeginRun reads account_vault_class_storage through AccountVaultStore.ClassStorageEnabled,
            // and an uncached PropertyManager read reaches a shard no unit test has.
            VaultClassTestConfig.Seed();
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Both flags are process-static. Released unconditionally, because a test that leaked either
            // one would break every later class in the run rather than itself.
            AccountVaultFoldMigration.EndRun();

            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
        }

        // ---------------------------------------------------------------- --batch parsing

        /// <summary>
        /// --batch is pulled out of the argument list wherever it sits, and the positional arguments keep
        /// their order and their meaning.
        ///
        /// The two orderings are both asserted because the flag has to be removed BEFORE the positional
        /// arguments are read: `all --batch=50 25` and `all 25 --batch=50` have to mean the same run, and
        /// a parser that consumed arguments left to right would read "--batch=50" as the account count in
        /// the first spelling.
        /// </summary>
        [TestMethod]
        public void BatchArgument_IsTakenFromAnyPosition_AndLeavesThePositionalsInOrder()
        {
            Assert.IsTrue(VaultClassCommands.TryTakeBatchArgument(new[] { "all", "--batch=50", "25" },
                out var first, out var firstBatch, out var firstError), firstError);

            Assert.AreEqual(50, firstBatch);
            CollectionAssert.AreEqual(new[] { "all", "25" }, first.ToArray());

            Assert.IsTrue(VaultClassCommands.TryTakeBatchArgument(new[] { "all", "25", "--batch=50" },
                out var second, out var secondBatch, out var secondError), secondError);

            Assert.AreEqual(50, secondBatch);
            CollectionAssert.AreEqual(new[] { "all", "25" }, second.ToArray());
        }

        /// <summary>
        /// With no --batch the default applies, and it is the migration's constant rather than a second
        /// copy of the number - a literal here would drift from the engine silently.
        /// </summary>
        [TestMethod]
        public void BatchArgument_DefaultsToTheMigrationsOwnDefault()
        {
            Assert.IsTrue(VaultClassCommands.TryTakeBatchArgument(new[] { "all" }, out var positional, out var batch, out var error), error);

            Assert.AreEqual(AccountVaultFoldMigration.DefaultBatchSize, batch);
            Assert.AreEqual(25, batch, "the shipped default is 25; change this assertion deliberately, not to make a test pass");
            CollectionAssert.AreEqual(new[] { "all" }, positional.ToArray());
        }

        /// <summary>
        /// A batch above the maximum is REJECTED BY NAME, never clamped.
        ///
        /// The clamping alternative is the thing being ruled out, and it is why the assertions are in this
        /// shape: the call must FAIL (not succeed with 200), the message must carry the maximum so the
        /// operator does not have to guess it, and the out batch must not be left holding a value a caller
        /// could use by mistake. AccountVaultFoldMigration.ClampBatch still clamps, deliberately, for
        /// programmatic callers - asserted here so the two behaviours are visibly different rather than
        /// accidentally the same.
        /// </summary>
        [TestMethod]
        public void BatchArgument_AboveTheMaximum_IsRejectedByName_NotClamped()
        {
            Assert.IsFalse(VaultClassCommands.TryTakeBatchArgument(new[] { "all", "--batch=2000" },
                out _, out _, out var error));

            Assert.IsNotNull(error);
            Assert.IsTrue(error.Contains(AccountVaultFoldMigration.MaxBatchSize.ToString()),
                $"the refusal must name the maximum; got: {error}");
            Assert.IsTrue(error.Contains("2000"), $"and echo what was asked for; got: {error}");

            // The exact boundary is accepted, so the rejection is a ceiling and not an off-by-one.
            Assert.IsTrue(VaultClassCommands.TryTakeBatchArgument(new[] { "all", $"--batch={AccountVaultFoldMigration.MaxBatchSize}" },
                out _, out var atMax, out var atMaxError), atMaxError);

            Assert.AreEqual(AccountVaultFoldMigration.MaxBatchSize, atMax);

            // CONTROL on the sibling behaviour: the programmatic path DOES clamp. If these two ever agree,
            // one of them is wrong.
            Assert.AreEqual(AccountVaultFoldMigration.MaxBatchSize, AccountVaultFoldMigration.ClampBatch(2000));
            Assert.AreEqual(AccountVaultFoldMigration.DefaultBatchSize, AccountVaultFoldMigration.ClampBatch(0));
            Assert.AreEqual(7, AccountVaultFoldMigration.ClampBatch(7));
        }

        /// <summary>
        /// The malformed spellings. Each is refused with a message rather than silently absorbed, because
        /// every one of them would otherwise change which run happens.
        /// </summary>
        [TestMethod]
        public void BatchArgument_MalformedSpellings_AreRefused()
        {
            // Space-separated. Refused rather than read loosely: the loose reading would have to consume
            // the next token, and in `all --batch 50` the next token is where the account count goes.
            Assert.IsFalse(VaultClassCommands.TryTakeBatchArgument(new[] { "all", "--batch", "50" }, out _, out _, out var spaced));
            Assert.IsTrue(spaced.Contains("--batch=n"), spaced);

            Assert.IsFalse(VaultClassCommands.TryTakeBatchArgument(new[] { "all", "--batch=abc" }, out _, out _, out var notNumeric));
            Assert.IsNotNull(notNumeric);

            Assert.IsFalse(VaultClassCommands.TryTakeBatchArgument(new[] { "all", "--batch=0" }, out _, out _, out var zero));
            Assert.IsNotNull(zero);

            Assert.IsFalse(VaultClassCommands.TryTakeBatchArgument(new[] { "all", "--batch=-5" }, out _, out _, out var negative));
            Assert.IsNotNull(negative);

            Assert.IsFalse(VaultClassCommands.TryTakeBatchArgument(new[] { "all", "--batch=10", "--batch=20" }, out _, out _, out var twice));
            Assert.IsTrue(twice.Contains("once"), twice);

            // CONTROL: an ordinary account name that merely starts with a dash is not mistaken for the
            // flag, and a name is never consumed as one.
            Assert.IsTrue(VaultClassCommands.TryTakeBatchArgument(new[] { "Someguy" }, out var positional, out _, out var nameError), nameError);
            CollectionAssert.AreEqual(new[] { "Someguy" }, positional.ToArray());
        }

        // ---------------------------------------------------------------- the reciprocal guard

        /// <summary>
        /// The read-only class commands refuse while a migration is in flight, and permit otherwise.
        ///
        /// WHAT THIS DOES AND DOES NOT COVER, stated plainly because the difference matters. It drives the
        /// DECISION - TryRefuseWhileMigrating - and proves it is gated on AccountVaultFoldMigration's run
        /// flag in both directions. It does NOT prove that HandleVaultClassDryRun and
        /// HandleVaultClassInspect call it: both return immediately on a null session.Player, so a unit
        /// test cannot reach past their first line, and the call sites are covered only by review. The
        /// guard was extracted into one named method precisely so there is a single body to get right and
        /// two one-line call sites to read.
        ///
        /// The both-directions half is what makes it a test rather than a tautology: a guard that refused
        /// unconditionally would break /vaultclassdryrun outright while passing any refusal-only
        /// assertion.
        /// </summary>
        [TestMethod]
        public void ReadOnlyClassCommands_RefuseWhileAMigrationRuns_AndPermitWhenItIsNot()
        {
            Assert.IsFalse(AccountVaultFoldMigration.IsRunning, "another test leaked the migration run flag");

            // Permitted when nothing is running.
            Assert.IsFalse(VaultClassCommands.TryRefuseWhileMigrating("measure", out var quiet));
            Assert.IsNull(quiet);

            try
            {
                Assert.IsTrue(AccountVaultFoldMigration.TryBeginRun(out var beginRefusal), beginRefusal);
                Assert.IsTrue(AccountVaultFoldMigration.IsRunning);

                Assert.IsTrue(VaultClassCommands.TryRefuseWhileMigrating("measure", out var busy));
                Assert.IsNotNull(busy);
                Assert.IsTrue(busy.Contains("measure"), $"the refusal must name what was refused; got: {busy}");
                Assert.IsTrue(busy.Contains("vaultclassfold status"), $"and where to find the result; got: {busy}");

                // The verb is the caller's, so the two commands do not share one vague message.
                Assert.IsTrue(VaultClassCommands.TryRefuseWhileMigrating("inspect", out var inspectBusy));
                Assert.IsTrue(inspectBusy.Contains("inspect"), inspectBusy);
            }
            finally
            {
                AccountVaultFoldMigration.EndRun();
            }

            // And permitted again once it ends, or the guard would be a permanent outage.
            Assert.IsFalse(AccountVaultFoldMigration.IsRunning);
            Assert.IsFalse(VaultClassCommands.TryRefuseWhileMigrating("measure", out _));
        }

        // ---------------------------------------------------------------- how a report reads

        private static VaultFoldMigrationReport ReportWith(int owning, int visited, params VaultFoldAccountTerminal[] terminals)
        {
            var report = new VaultFoldMigrationReport
            {
                AccountsOwningAVault = owning,
                AccountsVisited = visited,
                Batch = 25,
                Scope = "whole fleet",
                FleetWide = true,
                FinishedUtc = DateTime.UtcNow,
            };

            var id = 900u;

            foreach (var terminal in terminals)
                report.Accounts.Add(new VaultFoldAccountResult { AccountId = id++, Terminal = terminal });

            return report;
        }

        /// <summary>
        /// A SCOPED run disclaims itself in the report, and a fleet-wide one does not.
        ///
        /// This is the guard against the headline being misread. `[VAULT] fold migration COMPLETE` is the
        /// string a Runbook tells an operator to grep for, so a COMPLETE produced by
        /// `/vaultclassfold Someguy` or by a capped `all 25` has to say in the same block that it covered
        /// one scope and not the shard. Asserted in both directions: the disclaimer must be ABSENT from a
        /// fleet-wide report, or it would be noise that gets skipped on the run where it matters.
        /// </summary>
        [TestMethod]
        public void Render_DisclaimsAScopedRun_AndDoesNotDisclaimAFleetWideOne()
        {
            var fleet = ReportWith(2, 2, VaultFoldAccountTerminal.Exhausted, VaultFoldAccountTerminal.Exhausted);

            var fleetText = fleet.Render();

            Assert.IsTrue(fleetText.StartsWith("[VAULT] fold migration COMPLETE: whole fleet:", StringComparison.Ordinal), fleetText);
            Assert.IsFalse(fleetText.Contains("SCOPED RUN"), $"a fleet-wide report must not carry the scoped disclaimer; got: {fleetText}");

            var scoped = ReportWith(1, 1, VaultFoldAccountTerminal.Exhausted);

            scoped.Scope = "account Someguy (7701)";
            scoped.FleetWide = false;

            var scopedText = scoped.Render();

            Assert.IsTrue(scopedText.StartsWith("[VAULT] fold migration COMPLETE: account Someguy (7701):", StringComparison.Ordinal), scopedText);
            Assert.IsTrue(scopedText.Contains("SCOPED RUN"), scopedText);
            Assert.IsTrue(scopedText.Contains("NOT the whole fleet"), scopedText);

            // A scoped run that finished its scope really is complete FOR that scope. The disclaimer is
            // what carries the caveat; IsComplete is not quietly falsified, because a false COMPLETE and a
            // false INCOMPLETE are both lies and only one of them is loud.
            Assert.IsTrue(scoped.IsComplete);
        }

        /// <summary>
        /// The chat copy of a report caps its per-account lines and SAYS it did; the log copy never does.
        ///
        /// The cap exists because the prod fleet is large enough that a fully expanded INCOMPLETE report is
        /// past what a client will render. The assertion that matters is the one about the "and N more"
        /// line: a report the client silently truncated and a report that was deliberately truncated look
        /// identical to the reader unless the cut announces itself.
        /// </summary>
        [TestMethod]
        public void Render_CapsAccountLinesForChat_AndSaysHowManyItHid()
        {
            var terminals = Enumerable.Repeat(VaultFoldAccountTerminal.NotWarm, 50).ToArray();

            var report = ReportWith(50, 50, terminals);

            var capped = report.Render(5);

            Assert.AreEqual("INCOMPLETE", report.Headline);

            var cappedLines = capped.Split('\n').Count(l => l.TrimStart().StartsWith("account ", StringComparison.Ordinal));

            Assert.AreEqual(5, cappedLines, $"exactly the cap, not more; got:\n{capped}");
            Assert.IsTrue(capped.Contains("and 45 more"), $"the cut must name what it hid; got:\n{capped}");

            // The log copy is whole, and carries no truncation notice at all.
            var whole = report.Render();

            var wholeLines = whole.Split('\n').Count(l => l.TrimStart().StartsWith("account ", StringComparison.Ordinal));

            Assert.AreEqual(50, wholeLines, "the log copy must never be truncated");
            Assert.IsFalse(whole.Contains("more not-complete"), "and must not claim a truncation it did not make");
        }

        /// <summary>
        /// A progress snapshot carries NO verdict, and that absence is the point.
        ///
        /// Progress is what an operator stares at while waiting, and reading a plateau in it as a finish is
        /// the exact inference that let the original bug reach prod. So the line must not contain either
        /// headline word - not as a convenience, and not as a "looks done" hint - and the assertion is
        /// written as an absence because nothing functional would ever notice one creeping in.
        /// </summary>
        [TestMethod]
        public void Progress_CarriesNoVerdict()
        {
            var progress = new VaultFoldMigrationProgress
            {
                Scope = "whole fleet",
                AccountsOwningAVault = 100,
                AccountsVisited = 100,
                AccountsComplete = 100,
                CurrentAccountId = 7701,
                Folded = 15423,
                Examined = 20000,
                Batches = 900,
                Elapsed = TimeSpan.FromSeconds(42),
            };

            var line = progress.Render();

            // Every counter is at its terminal value here, which is the hardest case: this snapshot looks
            // finished and must still not say so.
            Assert.IsFalse(line.Contains("COMPLETE"), $"a progress line must never carry a verdict; got: {line}");
            Assert.IsFalse(line.Contains("INCOMPLETE"), $"got: {line}");
            Assert.IsTrue(line.Contains("in progress"), $"and must say what it is; got: {line}");
            Assert.IsTrue(line.Contains("15423"), line);
            Assert.IsTrue(line.Contains("7701"), "the account being worked on is what makes a stall diagnosable");
        }
    }
}
