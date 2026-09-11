using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /vaultrestore parsing, dispatch and rendering, with no Session and no shard.
    ///
    /// The dispatch half exists for the reason MarketAdminCommandParseTests' does: a transposed switch
    /// arm compiles and ships. It matters more here, because this command WRITES - a bare row id that
    /// parsed as something else, or an arm that fell through to the usage default, would either
    /// restore nothing or restore the wrong row.
    /// </summary>
    [TestClass]
    public class VaultRestoreCommandParseTests
    {
        private static VaultRestoreCommandResult Parse(params string[] parameters)
            => VaultRestoreCommandParser.Parse(parameters);

        [TestMethod]
        public void NoArguments_IsHelp()
        {
            Assert.AreEqual(VaultRestoreCommandKind.Help, Parse().Kind);
            Assert.AreEqual(VaultRestoreCommandKind.Help, VaultRestoreCommandParser.Parse(null).Kind);
            Assert.AreEqual(VaultRestoreCommandKind.Help, Parse("help").Kind);
            Assert.AreEqual(VaultRestoreCommandKind.Help, Parse("?").Kind);
        }

        [TestMethod]
        public void List_ParsesTheNameAndTheDefaultPageSize()
        {
            var result = Parse("list", "Someaccount");

            Assert.AreEqual(VaultRestoreCommandKind.List, result.Kind);
            Assert.AreEqual("Someaccount", result.Name);
            Assert.AreEqual(VaultRestoreCommandParser.DefaultLimit, result.Limit);
        }

        [TestMethod]
        public void List_ParsesAnExplicitPageSizeAndClampsIt()
        {
            Assert.AreEqual(5, Parse("list", "Someaccount", "5").Limit);
            Assert.AreEqual(VaultRestoreCommandParser.MaxLimit, Parse("list", "Someaccount", "5000").Limit);
        }

        [TestMethod]
        public void List_WithNoName_IsAUsageError()
        {
            Assert.AreEqual(VaultRestoreCommandKind.UsageError, Parse("list").Kind);
        }

        [TestMethod]
        public void List_WithAnUnparsablePageSize_IsAUsageErrorRatherThanTheDefault()
        {
            // Silently showing 20 rows to somebody who typed "five" answers a question they did not
            // ask, and on a command whose next step is a destructive-looking write that matters.
            Assert.AreEqual(VaultRestoreCommandKind.UsageError, Parse("list", "Someaccount", "five").Kind);
            Assert.AreEqual(VaultRestoreCommandKind.UsageError, Parse("list", "Someaccount", "0").Kind);
        }

        [TestMethod]
        public void ABareRowId_ParsesAsARestore()
        {
            var result = Parse("42");

            Assert.AreEqual(VaultRestoreCommandKind.Restore, result.Kind);
            Assert.AreEqual(42u, result.RowId);
        }

        [TestMethod]
        public void ANonNumericRowId_IsAUsageError()
        {
            Assert.AreEqual(VaultRestoreCommandKind.UsageError, Parse("refund").Kind);
            Assert.AreEqual(VaultRestoreCommandKind.UsageError, Parse("-1").Kind);

            // Zero is not a row id: account_vault_barrel.id is AUTO_INCREMENT from 1, so a 0 here is
            // a misread argument rather than a row, and this command writes.
            Assert.AreEqual(VaultRestoreCommandKind.UsageError, Parse("0").Kind);
        }

        [TestMethod]
        public void Subcommands_AreCaseInsensitive()
        {
            Assert.AreEqual(VaultRestoreCommandKind.List, Parse("LIST", "Someaccount").Kind);
            Assert.AreEqual(VaultRestoreCommandKind.Help, Parse("HELP").Kind);
        }

        // ---- dispatch ----

        private sealed class RecordingRestoreTarget : IVaultRestoreCommandTarget
        {
            public readonly List<string> Calls = new List<string>();

            public void ShowList(string name, int limit) => Calls.Add($"list:{name}:{limit}");
            public void Restore(uint rowId) => Calls.Add($"restore:{rowId}");
            public void ShowHelp() => Calls.Add("help");
            public void UsageError(string message) => Calls.Add($"usage:{message}");
        }

        private static string DispatchOne(params string[] parameters)
        {
            var target = new RecordingRestoreTarget();

            VaultRestoreCommands.Dispatch(VaultRestoreCommandParser.Parse(parameters), target);

            Assert.AreEqual(1, target.Calls.Count, "exactly one target call per invocation");

            return target.Calls[0];
        }

        [TestMethod]
        public void Dispatch_RoutesEveryArmToItsOwnMethod()
        {
            Assert.AreEqual("list:Someaccount:5", DispatchOne("list", "Someaccount", "5"));
            Assert.AreEqual("restore:42", DispatchOne("42"));
            Assert.AreEqual("help", DispatchOne("help"));
        }

        [TestMethod]
        public void Dispatch_SendsAUsageErrorToUsageErrorAndNowhereElse()
        {
            StringAssert.StartsWith(DispatchOne("refund"), "usage:");
            StringAssert.StartsWith(DispatchOne("list"), "usage:");
        }

        /// <summary>
        /// Every kind must have an arm. A new kind falling through to the default would silently
        /// render a usage error instead of doing what it was added for.
        /// </summary>
        [TestMethod]
        public void Dispatch_HasAnArmForEveryKind()
        {
            foreach (VaultRestoreCommandKind kind in Enum.GetValues(typeof(VaultRestoreCommandKind)))
            {
                var target = new RecordingRestoreTarget();

                VaultRestoreCommands.Dispatch(new VaultRestoreCommandResult { Kind = kind, Name = "Someaccount", RowId = 1 }, target);

                Assert.AreEqual(1, target.Calls.Count, $"{kind} did not reach exactly one target method");

                if (kind != VaultRestoreCommandKind.UsageError)
                    Assert.IsFalse(target.Calls[0].StartsWith("usage:"), $"{kind} fell through to the default arm");
            }
        }

        // ---- rendering ----

        [TestMethod]
        public void Render_SaysWhyAClosedRowIsClosedRatherThanOmittingIt()
        {
            // An administrator chasing something a player asked about needs "you already got that
            // back" and "retention destroyed that" as ANSWERS, not as an absence.
            var rows = new List<AccountVaultBarrel>
            {
                new AccountVaultBarrel
                {
                    Id = 1, Wcid = 9001, ItemGuid = 0x50000001, Count = 1, ItemName = "Open Item",
                    BarreledAt = DateTime.UtcNow.AddDays(-10), ActorCharacterName = "Someplayer",
                },
                new AccountVaultBarrel
                {
                    Id = 2, Wcid = 9002, Count = 20, ItemName = "Given Back",
                    BarreledAt = DateTime.UtcNow.AddDays(-10), ActorCharacterName = "Someplayer",
                    RestoredAt = DateTime.UtcNow,
                },
                new AccountVaultBarrel
                {
                    Id = 3, Wcid = 9003, Count = 1, ItemName = "Long Gone",
                    BarreledAt = DateTime.UtcNow.AddDays(-40), ActorCharacterName = "Someplayer",
                    PurgedAt = DateTime.UtcNow,
                },
            };

            var text = PlayerVaultRestoreCommandTarget.Render("account Someaccount (7)", rows, 30);

            StringAssert.Contains(text, "account Someaccount (7)");

            StringAssert.Contains(text, "1: Open Item x1");
            StringAssert.Contains(text, "20d left", "an open row shows the retention it has left");

            StringAssert.Contains(text, "2: Given Back x20");
            StringAssert.Contains(text, "RESTORED");

            StringAssert.Contains(text, "3: Long Gone x1");
            StringAssert.Contains(text, "PURGED");

            // A ledger row has no biota, so it is named by wcid rather than by a guid that does not
            // exist. Printing 0x00000000 there would read as a real item.
            StringAssert.Contains(text, "ledger wcid 9002");
            StringAssert.Contains(text, "0x50000001");
        }

        [TestMethod]
        public void Render_NeverShowsNegativeRetentionRemaining()
        {
            // A row past its window that the sweep has not reached yet - the sweep runs hourly and
            // takes a bounded batch - must read as 0 days left, not as a negative number.
            var rows = new List<AccountVaultBarrel>
            {
                new AccountVaultBarrel
                {
                    Id = 9, Wcid = 9001, ItemGuid = 0x50000009, Count = 1, ItemName = "Overdue",
                    BarreledAt = DateTime.UtcNow.AddDays(-45), ActorCharacterName = "Someplayer",
                },
            };

            StringAssert.Contains(PlayerVaultRestoreCommandTarget.Render("account Someaccount (7)", rows, 30), "0d left");
        }
    }
}
