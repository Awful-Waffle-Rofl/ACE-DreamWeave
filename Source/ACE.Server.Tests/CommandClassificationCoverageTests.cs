using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Web;
using ACE.Server.Network;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The EMBEDDED Source/ACE.Server/command-classification.tsv against reflection over ACE.Server
    /// (PLAN-P4.md sections 4.5-4.6 and 7). Reflects the attributes itself, exactly as
    /// CommandManager.Initialize does, and never calls Initialize (it would start the console thread).
    /// </summary>
    [TestClass]
    public class CommandClassificationCoverageTests
    {
        private static CommandClassification table;
        private static List<(string Command, string Handler, CommandHandlerAttribute Attribute, MethodInfo Method)> registered;

        [ClassInitialize]
        public static void ClassInit(TestContext _)
        {
            table = CommandClassification.LoadEmbedded();
            registered = CommandClassification.ReflectRegistered(typeof(CommandManager).Assembly.GetTypes());
        }

        /// <summary>A handler that lives in the TEST assembly, so it resolves as a mod.</summary>
        public static void FakeModHandler(Session session, params string[] parameters)
        {
        }

        private static CommandHandlerInfo Live(string command)
        {
            var matches = registered.Where(r => r.Command == command).ToList();
            Assert.AreEqual(1, matches.Count, $"expected exactly one registered handler for '{command}'");

            return new CommandHandlerInfo
            {
                Attribute = matches[0].Attribute,
                Handler = Delegate.CreateDelegate(typeof(CommandHandler), matches[0].Method),
            };
        }

        private static CommandHandler FakeModDelegate() =>
            (CommandHandler)Delegate.CreateDelegate(typeof(CommandHandler), typeof(CommandClassificationCoverageTests).GetMethod(nameof(FakeModHandler)));

        private static List<CommandClassificationRow> RowsFor(string command)
        {
            var rows = table.Rows.Where(r => r.Command == command).ToList();
            Assert.IsTrue(rows.Count > 0, $"no command-classification.tsv row for '{command}'");
            return rows;
        }

        [TestMethod]
        public void EveryRegisteredHandler_HasExactlyOneRow()
        {
            Assert.IsTrue(registered.Count > 300, $"reflection found only {registered.Count} handler(s) - is it scanning the right assembly?");

            var report = CommandClassification.CheckCoverage(registered.Select(r => (r.Command, r.Handler)), table);

            Assert.AreEqual(0, report.Unclassified.Count,
                $"{report.Unclassified.Count} registered handler(s) have no command-classification.tsv row: {string.Join("; ", report.Unclassified)}. Run `ACE.Content.Tools commandclassify --write`, review the new rows, and commit the result.");

            // Parse throws on a duplicate pair, so equal counts plus zero unclassified means exactly one row each.
            Assert.AreEqual(registered.Count, table.Rows.Count, "row count differs from the registered (command, handler) count");
        }

        [TestMethod]
        public void CoverageCheck_FailsOnUnclassifiedCommand()
        {
            var synthetic = registered.Select(r => (r.Command, r.Handler))
                .Concat(new[] { ("p4a-synthetic-command", "ACE.Server.Tests.Synthetic.Handle") })
                .ToList();

            var report = CommandClassification.CheckCoverage(synthetic, table);

            Assert.IsFalse(report.IsComplete);
            CollectionAssert.AreEqual(new[] { "p4a-synthetic-command ACE.Server.Tests.Synthetic.Handle" }, report.Unclassified.ToList());
        }

        [TestMethod]
        public void NoRowForMissingHandler()
        {
            var report = CommandClassification.CheckCoverage(registered.Select(r => (r.Command, r.Handler)), table);
            Assert.AreEqual(0, report.Orphaned.Count, $"row(s) for handlers that no longer exist: {string.Join("; ", report.Orphaned)}");

            // negative control: the same check over a table with one extra row reports it
            var extra = EmbeddedText().TrimEnd('\n') + "\nzz-gone\tACE.Server.Gone.Handle\tin_game_only\tscan_unsafe\tcaptured\t-\t\n";
            var extraReport = CommandClassification.CheckCoverage(registered.Select(r => (r.Command, r.Handler)), CommandClassification.Parse(extra));
            CollectionAssert.AreEqual(new[] { "zz-gone ACE.Server.Gone.Handle" }, extraReport.Orphaned.ToList());
        }

        [TestMethod]
        public void RequiresWorldRows_NeverWeb()
        {
            foreach (var r in registered.Where(r => (r.Attribute.Flags & CommandHandlerFlag.RequiresWorld) != 0))
            {
                Assert.IsTrue(table.TryGetRow(r.Command, r.Handler, out var row), $"{r.Command} {r.Handler} has no row");
                Assert.AreNotEqual(WebCommandBuckets.Web, row.Bucket, $"{r.Command} ({r.Handler}) is RequiresWorld and must never be web");
            }
        }

        [TestMethod]
        public void ConsoleInvokeRows_NeverCharacter()
        {
            foreach (var r in registered.Where(r => (r.Attribute.Flags & CommandHandlerFlag.ConsoleInvoke) != 0))
            {
                Assert.IsTrue(table.TryGetRow(r.Command, r.Handler, out var row), $"{r.Command} {r.Handler} has no row");
                Assert.AreNotEqual(WebCommandBuckets.InGameCharacter, row.Bucket, $"{r.Command} ({r.Handler}) is ConsoleInvoke and must never be in_game_character");
            }
        }

        [TestMethod]
        public void ConsoleReadRows_InGameOnly()
        {
            var consoleRead = table.Rows.Where(r => r.Basis == "console_read").ToList();
            Assert.IsTrue(consoleRead.Count > 0, "expected at least fix-spell-bars to be basis console_read");

            foreach (var row in consoleRead)
                Assert.AreEqual(WebCommandBuckets.InGameOnly, row.Bucket, $"{row.Command} reads the console and must be in_game_only");

            Assert.AreEqual("console_read", RowsFor("fix-spell-bars").Single().Basis);
        }

        [TestMethod]
        public void OverrideRows_HaveNotes()
        {
            foreach (var row in table.Rows.Where(r => r.Basis == "override"))
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.Note), $"{row.Command}: an override row must carry its ruling in note");
        }

        [TestMethod]
        public void FlagsRows_HaveNotes()
        {
            foreach (var row in table.Rows.Where(r => r.Flags.Count > 0))
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.Note), $"{row.Command}: a flagged row must say why in note");
        }

        [TestMethod]
        public void ModRegisteredCommand_DefaultsInGameOnly()
        {
            const string name = "p4a-coverage-mod-command";
            var method = typeof(CommandClassificationCoverageTests).GetMethod(nameof(FakeModHandler));

            try
            {
                Assert.IsTrue(CommandManager.TryAddCommand(method, name, AccessLevel.Admin));
                var info = CommandManager.GetCommandByName(name).Single();

                var resolved = table.Resolve(info);

                Assert.AreEqual(WebCommandBuckets.InGameOnly, resolved.Bucket);
                Assert.AreEqual(CommandClassification.SourceMod, resolved.Source);
            }
            finally
            {
                CommandManager.TryRemoveCommand(name);
            }
        }

        [TestMethod]
        public void ModOverridesWebCommand_InGameOnly()
        {
            var web = Live("serverstatus");
            Assert.AreEqual(WebCommandBuckets.Web, table.Resolve(web).Bucket, "control: the real serverstatus handler is web");

            var overridden = new CommandHandlerInfo { Attribute = web.Attribute, Handler = FakeModDelegate() };
            var resolved = table.Resolve(overridden);

            Assert.AreEqual(WebCommandBuckets.InGameOnly, resolved.Bucket, "a mod replacing a web command's handler must not inherit its bucket");
            Assert.AreEqual(CommandClassification.SourceMod, resolved.Source);
        }

        [TestMethod]
        public void DuplicateName_LiveHandlerWithoutRow_InGameOnly()
        {
            // The web serverstatus NAME, served by a different ACE.Server handler that has no row for that name.
            var web = Live("serverstatus");
            var other = Live("teleallto");
            var mismatched = new CommandHandlerInfo { Attribute = web.Attribute, Handler = other.Handler };

            var resolved = table.Resolve(mismatched);

            Assert.AreEqual(WebCommandBuckets.InGameOnly, resolved.Bucket);
            Assert.AreEqual(CommandClassification.SourceServer, resolved.Source);
            Assert.IsNull(resolved.Row);

            // netstats really is registered by two handlers; each resolves by its own identity.
            var netstats = registered.Where(r => r.Command == "netstats").ToList();
            Assert.AreEqual(2, netstats.Count, "netstats is expected to be registered twice (PLAN-P4.md section 4.5)");
            Assert.AreEqual(2, table.Rows.Count(r => r.Command == "netstats"));
        }

        [TestMethod]
        public void LiveFlagsDisagreeWithRow_InGameOnly()
        {
            var web = Live("serverstatus");
            var withWorld = new CommandHandlerInfo
            {
                Attribute = new CommandHandlerAttribute(web.Attribute.Command, web.Attribute.Access, CommandHandlerFlag.RequiresWorld),
                Handler = web.Handler,
            };

            Assert.AreEqual(WebCommandBuckets.InGameOnly, table.Resolve(withWorld).Bucket, "a web row whose live attribute requires the world must not stay web");

            var character = Live("teleallto");
            var consoleOnly = new CommandHandlerInfo
            {
                Attribute = new CommandHandlerAttribute(character.Attribute.Command, character.Attribute.Access, CommandHandlerFlag.ConsoleInvoke),
                Handler = character.Handler,
            };

            Assert.AreEqual(WebCommandBuckets.InGameOnly, table.Resolve(consoleOnly).Bucket, "an in_game_character row whose live attribute is console-only must not stay in_game_character");
        }

        [TestMethod]
        public void OrchestratorOverrides_Pinned()
        {
            // O1: the shutdown family
            foreach (var name in new[] { "shutdown", "set-shutdown-interval", "stop-now", "exit" })
            {
                foreach (var row in RowsFor(name))
                {
                    Assert.AreEqual(WebCommandBuckets.InGameOnly, row.Bucket, $"O1: {name}");
                    Assert.AreEqual("override", row.Basis, $"O1: {name}");
                }
                Assert.AreEqual(WebCommandBuckets.InGameOnly, table.Resolve(Live(name)).Bucket, $"O1: {name} (live)");
            }

            // O2: the *-export family in ConsoleCommands
            foreach (var name in new[] { "cell-export", "portal-export", "highres-export", "language-export", "wave-export", "image-export" })
            {
                foreach (var row in RowsFor(name))
                {
                    Assert.AreEqual(WebCommandBuckets.InGameOnly, row.Bucket, $"O2: {name}");
                    Assert.AreEqual("override", row.Basis, $"O2: {name}");
                }
                Assert.AreEqual(WebCommandBuckets.InGameOnly, table.Resolve(Live(name)).Bucket, $"O2: {name} (live)");
            }

            // O5: cancel-shutdown stays web, through an explicit override row
            var cancel = RowsFor("cancel-shutdown").Single();
            Assert.AreEqual(WebCommandBuckets.Web, cancel.Bucket, "O5");
            Assert.AreEqual("override", cancel.Basis, "O5");
            Assert.AreEqual(WebCommandBuckets.Web, table.Resolve(Live("cancel-shutdown")).Bucket, "O5 (live)");

            // motionstate: off the web bucket, because its null-session (console) path defers to the player's action
            // queue and that output would land after the web context is sealed
            var motionState = RowsFor("motionstate").Single();
            Assert.AreEqual(WebCommandBuckets.InGameOnly, motionState.Bucket, "motionstate");
            Assert.AreEqual("override", motionState.Basis, "motionstate");
            Assert.AreEqual(WebCommandBuckets.InGameOnly, table.Resolve(Live("motionstate")).Bucket, "motionstate (live)");

            // O3/O4: the 18 bulk fix-*/verify-* commands are web (rule 3) and long_running
            var bulk = new[]
            {
                "fix-allegiances", "fix-shortcut-bars", "fix-gear-plating", "fix-biota-emote-delay",
                "verify-player-data", "verify-attributes", "verify-vitals", "verify-skills", "verify-skill-credits",
                "verify-heritage-augs", "verify-max-augs", "verify-xp", "verify-armor-levels", "verify-clothing-wield-level",
                "verify-legendary-wield-level", "verify-shield-rating", "verify-melee-rares", "verify-beneficial-enchantments",
            };
            Assert.AreEqual(18, bulk.Length);

            foreach (var name in bulk)
            {
                var row = RowsFor(name).Single();
                Assert.AreEqual(WebCommandBuckets.Web, row.Bucket, $"O3: {name}");
                Assert.AreEqual("console_invoke", row.Basis, $"O3: {name}");
                Assert.IsTrue(row.HasFlag(CommandClassification.FlagLongRunning), $"O4: {name} must be long_running");
            }

            var spellBars = RowsFor("fix-spell-bars").Single();
            Assert.AreEqual(WebCommandBuckets.InGameOnly, spellBars.Bucket);
            Assert.IsTrue(spellBars.HasFlag(CommandClassification.FlagLongRunning));

            // R11: the four modifyX commands carry self_audit
            foreach (var name in new[] { "modifybool", "modifylong", "modifydouble", "modifystring" })
                Assert.IsTrue(RowsFor(name).Single().HasFlag(CommandClassification.FlagSelfAudit), $"R11: {name}");

            // Audit round 1 (PR #1152): O1-class and O2-class rows the scan put on web
            foreach (var name in new[]
            {
                "loadalllandblocks",
                "export-sql", "export-sql-folders", "export-sql-realm", "export-json", "export-json-folders", "generate-classnames", "vloc2loc",
                "testlootgen", "testlootgencorpse",
                "reload-loot-tables",
            })
            {
                var row = RowsFor(name).Single();
                Assert.AreEqual(WebCommandBuckets.InGameOnly, row.Bucket, $"audit: {name}");
                Assert.AreEqual("override", row.Basis, $"audit: {name}");
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.Note), $"audit: {name} needs its reason");
                Assert.AreEqual(WebCommandBuckets.InGameOnly, table.Resolve(Live(name)).Bucket, $"audit: {name} (live)");
            }

            // Audit round 1: reviewed null-safe past the scanner, so web by override
            foreach (var name in new[] { "modifybool", "modifylong", "modifydouble", "modifystring", "world" })
            {
                var row = RowsFor(name).Single();
                Assert.AreEqual(WebCommandBuckets.Web, row.Bucket, $"audit: {name}");
                Assert.AreEqual("override", row.Basis, $"audit: {name}");
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.Note), $"audit: {name} needs its reason");
                Assert.AreEqual(WebCommandBuckets.Web, table.Resolve(Live(name)).Bucket, $"audit: {name} (live)");
            }

            foreach (var name in new[] { "modifybool", "modifylong", "modifydouble", "modifystring" })
                Assert.IsTrue(table.Resolve(Live(name)).Row.HasFlag(CommandClassification.FlagSelfAudit), $"R11: {name} keeps self_audit through its web override");

            Assert.IsFalse(RowsFor("world").Single().HasFlag(CommandClassification.FlagSelfAudit), "world posts the generic audit line");

            // long_running comes only from this list
            var longRunning = table.Rows.Where(r => r.HasFlag(CommandClassification.FlagLongRunning)).Select(r => r.Command).OrderBy(c => c, StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(bulk.Concat(new[] { "fix-spell-bars" }).OrderBy(c => c, StringComparer.Ordinal).ToList(), longRunning);
        }

        [TestMethod]
        public void StayAvailableCommands_NeverInGameOnly()
        {
            foreach (var name in new[] { "world", "deletecharacter", "boot", "ban", "set-accountaccess", "accountcreate", "gamecast", "teleallto", "smite" })
            {
                foreach (var row in RowsFor(name))
                    Assert.AreNotEqual(WebCommandBuckets.InGameOnly, row.Bucket, $"O3: {name} must stay available");

                Assert.AreNotEqual(WebCommandBuckets.InGameOnly, table.Resolve(Live(name)).Bucket, $"O3: {name} (live)");
            }
        }

        [TestMethod]
        public void EmbeddedTable_NoBom_LfOnly_Sorted()
        {
            using var stream = typeof(CommandClassification).Assembly.GetManifestResourceStream(CommandClassification.EmbeddedResourceName);
            Assert.IsNotNull(stream);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();

            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "command-classification.tsv must not start with a BOM");

            var lines = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
            Assert.AreEqual(CommandClassification.HeaderLine, lines[0]);

            var keys = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t')).Select(f => (f[0], f[1])).ToList();
            var sorted = keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(sorted, keys, "command-classification.tsv must be sorted by command, then handler (ordinal)");
        }

        [TestMethod]
        public void Parse_RejectsUnknownBucket_AndMissingOverrideNote()
        {
            var header = CommandClassification.HeaderLine + "\n";

            Assert.ThrowsExactly<FormatException>(() => CommandClassification.Parse(header + "a\tX.Y\tanywhere\tscan_safe\tcaptured\t-\t\n"));
            Assert.ThrowsExactly<FormatException>(() => CommandClassification.Parse(header + "a\tX.Y\tweb\toverride\tcaptured\t-\t\n"));
            Assert.ThrowsExactly<FormatException>(() => CommandClassification.Parse(header + "a\tX.Y\tweb\tscan_safe\tcaptured\tlong_running\t\n"));
            Assert.ThrowsExactly<FormatException>(() => CommandClassification.Parse(header + "a\tX.Y\tweb\tscan_safe\tcaptured\t-\t\na\tX.Y\tweb\tscan_safe\tcaptured\t-\t\n"));

            // control: a well-formed row parses
            Assert.AreEqual(1, CommandClassification.Parse(header + "a\tX.Y\tweb\tscan_safe\tcaptured\t-\t\n").Rows.Count);
        }

        private static string EmbeddedText()
        {
            using var stream = typeof(CommandClassification).Assembly.GetManifestResourceStream(CommandClassification.EmbeddedResourceName);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return reader.ReadToEnd().Replace("\r\n", "\n");
        }
    }
}
