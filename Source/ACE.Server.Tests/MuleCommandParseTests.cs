using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor (Docs/MuleVendor/DESIGN.md section 13): the pure parse of /mule's parameters,
    /// extracted into MuleCommandParser.Parse so it needs no live Session or Player.
    ///
    /// The parse is ambiguous by construction: "grant", "revoke", "access" and "log" are reserved
    /// words that could also be character names. These tests pin the resolution order (reserved words
    /// first) and the usage-error shape for every malformed input.
    /// </summary>
    [TestClass]
    public class MuleCommandParseTests
    {
        [TestMethod]
        public void NoArguments_IsSummonYourOwn()
        {
            var result = MuleCommandParser.Parse(new string[0]);

            Assert.AreEqual(MuleCommandKind.SummonOwn, result.Kind);
        }

        [TestMethod]
        public void NullParameters_IsSummonYourOwn()
        {
            // Fix round 1, F8: the live /mule path can never actually deliver null here -
            // CommandManager.GetCommandHandler returns InvalidCommand for a null parameters array, and
            // GameActionTalk only invokes a handler on Ok. This guard is defence for Parse's public
            // signature, not a reachable production path, and it must not throw regardless.
            var result = MuleCommandParser.Parse(null);

            Assert.AreEqual(MuleCommandKind.SummonOwn, result.Kind);
        }

        [TestMethod]
        public void OneName_IsSummonSomeoneElses()
        {
            var result = MuleCommandParser.Parse(new[] { "Bob" });

            Assert.AreEqual(MuleCommandKind.SummonOther, result.Kind);
            Assert.AreEqual("Bob", result.Name);
        }

        /// <summary>
        /// A QUOTED multi-word name reaches Parse as a single element, because
        /// CommandManager.ParseCommand has already reassembled it - on the console path and on the
        /// in-game path alike. This test is written the way the parser actually receives
        /// `/mule "Bob Smith"`, not the way the raw chat line looks.
        /// </summary>
        [TestMethod]
        public void OneQuotedMultiWordName_IsSummonSomeoneElses()
        {
            var result = MuleCommandParser.Parse(new[] { "Bob Smith" });

            Assert.AreEqual(MuleCommandKind.SummonOther, result.Kind);
            Assert.AreEqual("Bob Smith", result.Name);
        }

        /// <summary>
        /// The admin "+" prefix needs no quoting of its own and must survive at face value. Two forms:
        /// a bare "+Awfulwaffle" (no space, so one token) and a quoted "+Testing thing" (reassembled by
        /// ParseCommand into one token before it gets here).
        /// </summary>
        [TestMethod]
        public void AdminPrefixedNames_PassThroughAtFaceValue()
        {
            var bare = MuleCommandParser.Parse(new[] { "+Awfulwaffle" });

            Assert.AreEqual(MuleCommandKind.SummonOther, bare.Kind);
            Assert.AreEqual("+Awfulwaffle", bare.Name);

            var quoted = MuleCommandParser.Parse(new[] { "+Testing thing" });

            Assert.AreEqual(MuleCommandKind.SummonOther, quoted.Kind);
            Assert.AreEqual("+Testing thing", quoted.Name);
        }

        /// <summary>
        /// UNQUOTED extra tokens are a usage error rather than a silent join (repo owner's ruling,
        /// 2026-08-28). The error must name the fix - the quotes - because reprinting the grammar tells
        /// a player who already knows it nothing.
        /// </summary>
        [TestMethod]
        public void UnquotedMultiWordName_IsAQuotingError()
        {
            var result = MuleCommandParser.Parse(new[] { "Bob", "Smith" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
            Assert.AreEqual(MuleCommandParser.QuoteNameMessage, result.UsageError);
        }

        [TestMethod]
        public void Grant_WithoutWithdraw_IsDepositOnly()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "Bob" });

            Assert.AreEqual(MuleCommandKind.Grant, result.Kind);
            Assert.AreEqual("Bob", result.Name);
            Assert.IsFalse(result.CanWithdraw);
        }

        [TestMethod]
        public void Grant_WithWithdraw_IsDepositPlusWithdraw()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "Bob", "withdraw" });

            Assert.AreEqual(MuleCommandKind.Grant, result.Kind);
            Assert.AreEqual("Bob", result.Name);
            Assert.IsTrue(result.CanWithdraw);
        }

        [TestMethod]
        public void Grant_WithdrawIsCaseInsensitive()
        {
            var result = MuleCommandParser.Parse(new[] { "GRANT", "Bob", "WITHDRAW" });

            Assert.AreEqual(MuleCommandKind.Grant, result.Kind);
            Assert.AreEqual("Bob", result.Name);
            Assert.IsTrue(result.CanWithdraw);
        }

        [TestMethod]
        public void Grant_WithoutAName_IsAUsageError()
        {
            var result = MuleCommandParser.Parse(new[] { "grant" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
            Assert.IsFalse(string.IsNullOrEmpty(result.UsageError));
        }

        /// <summary>
        /// Fix round 1, F2: a trailing non-"withdraw" token is no longer unknown syntax - it joins the
        /// name. What was "grant Bob everything" (a usage error before this fix) is now a grant to the
        /// two-word character "Bob Everything".
        /// </summary>
        [TestMethod]
        public void Grant_WithAQuotedMultiWordName_ParsesTheWholeName()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "Bob Everything" });

            Assert.AreEqual(MuleCommandKind.Grant, result.Kind);
            Assert.AreEqual("Bob Everything", result.Name);
            Assert.IsFalse(result.CanWithdraw);
        }

        /// <summary>The repo owner's own example: /mule grant "+Testing thing" withdraw</summary>
        [TestMethod]
        public void Grant_WithAQuotedAdminName_AndWithdraw()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "+Testing thing", "withdraw" });

            Assert.AreEqual(MuleCommandKind.Grant, result.Kind);
            Assert.AreEqual("+Testing thing", result.Name);
            Assert.IsTrue(result.CanWithdraw);
        }

        [TestMethod]
        public void Grant_WithAnUnquotedMultiWordName_IsAQuotingError()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "Bob", "Smith" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
            Assert.AreEqual(MuleCommandParser.QuoteNameMessage, result.UsageError);
        }

        [TestMethod]
        public void Grant_WithAnUnquotedMultiWordName_AndWithdraw_IsAQuotingError()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "Bob", "Smith", "withdraw" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
            Assert.AreEqual(MuleCommandParser.QuoteNameMessage, result.UsageError);
        }

        /// <summary>
        /// The ambiguity requiring quotes was introduced to remove. A character literally named
        /// "Bob Withdraw" could not be granted DEPOSIT-ONLY under the old greedy join - the parser had
        /// no way to tell that last token from the flag. Quoted, the name is one argument and the
        /// absent flag is unambiguous.
        /// </summary>
        [TestMethod]
        public void Grant_ToACharacterNamedWithdraw_IsDepositOnlyWhenQuoted()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "Bob Withdraw" });

            Assert.AreEqual(MuleCommandKind.Grant, result.Kind);
            Assert.AreEqual("Bob Withdraw", result.Name);
            Assert.IsFalse(result.CanWithdraw, "the name ends in the flag word, but it is inside one quoted argument and is not the flag");
        }

        /// <summary>
        /// The same character, granted deposit AND withdraw: the quoted name plus a bare trailing flag.
        /// Together with the test above this pins both readings of the same words.
        /// </summary>
        [TestMethod]
        public void Grant_ToACharacterNamedWithdraw_CanStillAddTheFlag()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "Bob Withdraw", "withdraw" });

            Assert.AreEqual(MuleCommandKind.Grant, result.Kind);
            Assert.AreEqual("Bob Withdraw", result.Name);
            Assert.IsTrue(result.CanWithdraw);
        }

        /// <summary>
        /// A single-word character named "Withdraw" must still be grantable deposit-only. The flag is
        /// only consumed when something remains in front of it.
        /// </summary>
        [TestMethod]
        public void Grant_ToASingleWordCharacterNamedWithdraw_IsDepositOnly()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "Withdraw" });

            Assert.AreEqual(MuleCommandKind.Grant, result.Kind);
            Assert.AreEqual("Withdraw", result.Name);
            Assert.IsFalse(result.CanWithdraw);
        }

        [TestMethod]
        public void Revoke_ParsesTheName()
        {
            var result = MuleCommandParser.Parse(new[] { "revoke", "Bob" });

            Assert.AreEqual(MuleCommandKind.Revoke, result.Kind);
            Assert.AreEqual("Bob", result.Name);
        }

        [TestMethod]
        public void Revoke_WithAQuotedMultiWordName()
        {
            var result = MuleCommandParser.Parse(new[] { "revoke", "+Testing thing" });

            Assert.AreEqual(MuleCommandKind.Revoke, result.Kind);
            Assert.AreEqual("+Testing thing", result.Name);
        }

        [TestMethod]
        public void Revoke_WithAnUnquotedMultiWordName_IsAQuotingError()
        {
            var result = MuleCommandParser.Parse(new[] { "revoke", "Bob", "Smith" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
            Assert.AreEqual(MuleCommandParser.QuoteNameMessage, result.UsageError);
        }

        [TestMethod]
        public void Revoke_WithoutAName_IsAUsageError()
        {
            var result = MuleCommandParser.Parse(new[] { "revoke" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
        }

        [TestMethod]
        public void Access_TakesNoName()
        {
            var result = MuleCommandParser.Parse(new[] { "access" });

            Assert.AreEqual(MuleCommandKind.Access, result.Kind);
        }

        [TestMethod]
        public void Access_WithAnExtraArgument_IsAUsageError()
        {
            var result = MuleCommandParser.Parse(new[] { "access", "Bob" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
        }

        [TestMethod]
        public void Log_TakesNoName()
        {
            var result = MuleCommandParser.Parse(new[] { "log" });

            Assert.AreEqual(MuleCommandKind.Log, result.Kind);
        }

        [TestMethod]
        public void Log_WithAnExtraArgument_IsAUsageError()
        {
            var result = MuleCommandParser.Parse(new[] { "log", "Bob" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
        }

        /// <summary>
        /// DESIGN 13's accepted cost: a character literally named "Log" (or Grant/Revoke/Access)
        /// cannot be summoned by /mule &lt;name&gt;, because the reserved word wins over "just a name"
        /// resolution. This is a single token that is BOTH a valid reserved word and a plausible
        /// character name, so it is the one input that actually discriminates the resolution order.
        /// </summary>
        [TestMethod]
        public void SubcommandsWin_OverASameNamedCharacter()
        {
            var result = MuleCommandParser.Parse(new[] { "Log" });

            Assert.AreEqual(MuleCommandKind.Log, result.Kind,
                "the reserved word 'log' must win over treating it as a player name, even though 'Log' is also a plausible character name");
        }

        /// <summary>
        /// Fix round 1, F2 repoint: this test's original premise (a second token after a name is
        /// unknown syntax) no longer holds - see OneMultiWordName_IsSummonSomeoneElses above, which now
        /// covers exactly that input as a real case rather than an error. What remains a genuine usage
        /// error is a name that is whitespace-only once the reserved word is stripped off.
        /// </summary>
        [TestMethod]
        public void UnknownExtraArguments_AreAUsageError()
        {
            var result = MuleCommandParser.Parse(new[] { "grant", "   " });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
        }

        // --------------------------------------------------------------------------- /mule search

        [TestMethod]
        public void Search_Alone_IsSearchWithAnEmptyPattern()
        {
            var result = MuleCommandParser.Parse(new[] { "search" });

            Assert.AreEqual(MuleCommandKind.Search, result.Kind);
            Assert.AreEqual("", result.Pattern);
        }

        /// <summary>
        /// The client splits chat on spaces, so a pattern containing spaces arrives as several separate
        /// parameters - "search legendary coord|legendary quick" is a 3-element array here, not one
        /// pre-joined string. This is free text, NOT the quoted-name rule the rest of this parser uses,
        /// so reassembly must happen unconditionally rather than only when quotes were seen.
        /// </summary>
        [TestMethod]
        public void Search_WithASpacedPattern_RejoinsWithSingleSpaces()
        {
            var result = MuleCommandParser.Parse(new[] { "search", "legendary", "coord|legendary", "quick" });

            Assert.AreEqual(MuleCommandKind.Search, result.Kind);
            Assert.AreEqual("legendary coord|legendary quick", result.Pattern);
        }

        [TestMethod]
        public void Search_IsCaseInsensitive()
        {
            var result = MuleCommandParser.Parse(new[] { "SEARCH", "pattern" });

            Assert.AreEqual(MuleCommandKind.Search, result.Kind);
            Assert.AreEqual("pattern", result.Pattern);
        }

        /// <summary>
        /// "search" is a reserved word like "grant"/"revoke"/"access"/"log"/"help" - a character
        /// literally named Search cannot be summoned by /mule Search.
        /// </summary>
        [TestMethod]
        public void Search_WinsOverASameNamedCharacter()
        {
            var result = MuleCommandParser.Parse(new[] { "Search" });

            Assert.AreEqual(MuleCommandKind.Search, result.Kind,
                "the reserved word 'search' must win over treating it as a player name");
        }

        // ----------------------------------------------------------------------------- /mule help

        [TestMethod]
        public void Help_TakesNoName()
        {
            var result = MuleCommandParser.Parse(new[] { "help" });

            Assert.AreEqual(MuleCommandKind.Help, result.Kind);
        }

        [TestMethod]
        public void Help_IsCaseInsensitive()
        {
            Assert.AreEqual(MuleCommandKind.Help, MuleCommandParser.Parse(new[] { "HELP" }).Kind);
        }

        [TestMethod]
        public void Help_WithAnExtraArgument_IsAUsageError()
        {
            var result = MuleCommandParser.Parse(new[] { "help", "me" });

            Assert.AreEqual(MuleCommandKind.UsageError, result.Kind);
        }

        /// <summary>
        /// The help text is used BOTH as the CommandHandler attribute's usage string and as what
        /// /mule help prints, from one constant, so the two cannot drift. This asserts the property
        /// that makes that worth doing: every subcommand the parser accepts appears in it. A new
        /// subcommand added without a help line fails here rather than shipping undiscoverable.
        /// </summary>
        [TestMethod]
        public void HelpText_MentionsEverySubcommand()
        {
            foreach (var subcommand in new[] { "/mule grant", "/mule revoke", "/mule access", "/mule log", "/mule search", "/mule help" })
                StringAssert.Contains(MuleCommandParser.HelpText, subcommand);

            StringAssert.Contains(MuleCommandParser.HelpText, "must be quoted", "the quoting rule is the one piece of grammar a player cannot guess");
        }

        /// <summary>
        /// HelpLines is what actually reaches the client - one system chat line each, because a single
        /// message with embedded newlines is not reliably rendered as the aligned block this text is
        /// written to be. Derived from HelpText rather than maintained beside it.
        /// </summary>
        [TestMethod]
        public void HelpLines_SplitsTheHelpTextAndIsNeverEmpty()
        {
            Assert.IsTrue(MuleCommandParser.HelpLines.Length >= 7, $"expected a line per subcommand plus the quoting note; got {MuleCommandParser.HelpLines.Length}");

            foreach (var line in MuleCommandParser.HelpLines)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(line), "a blank help line would render as an empty chat message");
                Assert.IsFalse(line.Contains("\n"), "each element must already be one line");
            }
        }

        [TestMethod]
        public void HelpHint_PointsAtTheHelpCommand()
        {
            StringAssert.Contains(MuleCommandParser.HelpHint, "/mule help");
        }

        // ------------------------------------------------- QuoteNameIfNeeded (the grant notification)

        /// <summary>
        /// The inverse of the parser's rule, used to build the command the grant notification tells a
        /// grantee to type. Round-tripped rather than asserted on the string alone: whatever
        /// QuoteNameIfNeeded emits must parse back to the same name, which is the property that
        /// actually matters and the one a hand-written quote would break.
        /// </summary>
        [TestMethod]
        public void QuoteNameIfNeeded_RoundTripsThroughTheParser()
        {
            foreach (var name in new[] { "Bob", "+Awfulwaffle", "Bob Smith", "+Testing thing" })
            {
                var quoted = MuleCommandParser.QuoteNameIfNeeded(name);

                // What CommandManager.ParseCommand does to that argument before a handler sees it:
                // a quoted run becomes one element with the quotes stripped.
                var asParameters = quoted.StartsWith("\"")
                    ? new[] { quoted.Trim('"') }
                    : quoted.Split(' ');

                var result = MuleCommandParser.Parse(asParameters);

                Assert.AreEqual(MuleCommandKind.SummonOther, result.Kind, $"'{quoted}' must parse as a summon");
                Assert.AreEqual(name, result.Name, $"'{quoted}' must round-trip back to '{name}'");
            }
        }

        [TestMethod]
        public void QuoteNameIfNeeded_LeavesASpacelessNameBare()
        {
            Assert.AreEqual("Bob", MuleCommandParser.QuoteNameIfNeeded("Bob"));
            Assert.AreEqual("+Awfulwaffle", MuleCommandParser.QuoteNameIfNeeded("+Awfulwaffle"), "the admin prefix is not whitespace and needs no quoting");
        }

        [TestMethod]
        public void QuoteNameIfNeeded_WrapsANameWithASpace()
        {
            Assert.AreEqual("\"+Testing thing\"", MuleCommandParser.QuoteNameIfNeeded("+Testing thing"));
        }

        // ---------------------------------------------------------------- B1: /mule slash interception
        //
        // Fix round B, B1: GameActionTalk.Handle only recognises a command when it starts with "@" -
        // everything else is broadcast as ordinary local chat. MuleCommands.IsMuleSlashCommand is the
        // pure recognition GameActionTalk intercepts on BEFORE that branch, so it needs no live Session
        // to test. Before this fix, "/mule grant Bob withdraw" was not a command at all: it fell through
        // to HandleActionTalk and was broadcast, disclosing who the player shares vault access with.

        [TestMethod]
        public void IsMuleSlashCommand_MatchesABareSlashMule()
        {
            Assert.IsTrue(MuleCommands.IsMuleSlashCommand("/mule"));
        }

        [TestMethod]
        public void IsMuleSlashCommand_IsCaseInsensitive()
        {
            Assert.IsTrue(MuleCommands.IsMuleSlashCommand("/MULE"));
        }

        [TestMethod]
        public void IsMuleSlashCommand_MatchesWithArguments()
        {
            Assert.IsTrue(MuleCommands.IsMuleSlashCommand("/mule grant Bob"));
        }

        [TestMethod]
        public void IsMuleSlashCommand_DoesNotMatchTheAtForm()
        {
            // The "@" form never reaches this helper in production (it is handled by the existing "@"
            // branch already), but the helper itself must not claim it - "/mule" specifically means a
            // leading slash.
            Assert.IsFalse(MuleCommands.IsMuleSlashCommand("@mule"));
        }

        [TestMethod]
        public void IsMuleSlashCommand_DoesNotMatchALongerWord()
        {
            Assert.IsFalse(MuleCommands.IsMuleSlashCommand("/mulish"));
        }

        [TestMethod]
        public void IsMuleSlashCommand_DoesNotMatchAShorterPrefix()
        {
            Assert.IsFalse(MuleCommands.IsMuleSlashCommand("/mul"));
        }

        [TestMethod]
        public void IsMuleSlashCommand_DoesNotMatchABareSlash()
        {
            Assert.IsFalse(MuleCommands.IsMuleSlashCommand("/"));
        }
    }
}
