using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// DoCopyChar's CopyCharOptions (BeforeSave, Publish). DoCopyChar needs a database and a Session, so its
    /// wiring is pinned by source shape, on the same comment-stripped, whitespace-squashed body that
    /// CopyCharItemPropertyCarryTests reads. The properties pinned:
    /// - BeforeSave runs on the built copy after every id swap and before anything is gathered or saved;
    /// - every publish call sits inside the options.Publish block;
    /// - a null options means the historical behaviour (publish on, no hook).
    ///
    /// This class reads no PropertyManager key, so there is nothing to seed or restore.
    /// </summary>
    [TestClass]
    public class CopyCharOptionsWiringTests
    {
        private const string AdminCommandsFile = "Source/ACE.Server/Command/Handlers/AdminCommands.cs";
        private const string Signature = "internal static void DoCopyChar(";

        private static string Body() => CopyCharItemPropertyCarryTests.MethodBody(AdminCommandsFile, Signature);

        [TestMethod]
        public void Defaults_publish_with_no_hook()
        {
            var options = new CopyCharOptions();

            Assert.IsTrue(options.Publish, "Publish must default to true, or every existing caller stops publishing its copy");
            Assert.IsNull(options.BeforeSave);
        }

        [TestMethod]
        public void Null_options_fall_back_to_the_defaults_before_anything_runs()
        {
            var body = Body();

            Assert.IsTrue(body.StartsWith("{options??=newCopyCharOptions();", StringComparison.Ordinal),
                "DoCopyChar must replace a null options with the defaults as its first statement, so existing callers keep their behaviour");
        }

        [TestMethod]
        public void BeforeSave_runs_after_the_id_swaps_and_before_the_save()
        {
            var body = Body();

            var construct = body.LastIndexOf("newPlayer=newPlayer(newPlayerBiota,", StringComparison.Ordinal);
            var casterSwap = body.IndexOf("foreach(varentryinnewPlayer.Biota.PropertiesEnchantmentRegistry)", StringComparison.Ordinal);
            var hook = body.IndexOf("options.BeforeSave(newPlayer);", StringComparison.Ordinal);
            var gather = body.IndexOf("newPlayer.GetAllPossessions()", StringComparison.Ordinal);
            var save = body.IndexOf("DatabaseManager.Shard.AddCharacterInParallel(", StringComparison.Ordinal);

            // positive controls: every landmark must be found, or the ordering asserts below prove nothing
            Assert.IsTrue(construct > 0, "new Player( construction not found");
            Assert.IsTrue(casterSwap > 0, "player enchantment caster swap not found");
            Assert.IsTrue(gather > 0, "GetAllPossessions not found");
            Assert.IsTrue(save > 0, "AddCharacterInParallel not found");
            Assert.IsTrue(hook > 0, "options.BeforeSave(newPlayer) is not called in DoCopyChar");

            Assert.AreEqual(hook, body.LastIndexOf("options.BeforeSave(newPlayer);", StringComparison.Ordinal), "BeforeSave must run exactly once");

            Assert.IsTrue(construct < casterSwap && casterSwap < hook, "BeforeSave must run after the copy is built and its enchantment casters are swapped");
            Assert.IsTrue(hook < gather && gather < save, "BeforeSave must run before the possessions are gathered and the character is saved");
        }

        /// <summary>
        /// A throwing BeforeSave abandons the copy: its catch block must report onDone(false) and then return, so nothing is
        /// saved and the caller's gate hold is released (TrialPromote releases it in onDone).
        /// </summary>
        [TestMethod]
        public void A_throwing_BeforeSave_reports_failure_and_returns_before_the_save()
        {
            var body = Body();

            const string tryHead = "try{options.BeforeSave(newPlayer);}catch(Exceptionex){";
            var tryAt = body.IndexOf(tryHead, StringComparison.Ordinal);
            Assert.IsTrue(tryAt > 0, "BeforeSave must be called inside a try whose catch takes Exception");

            var catchOpen = tryAt + tryHead.Length - 1;
            var catchEnd = MatchingBrace(body, catchOpen);
            var catchBody = body.Substring(catchOpen, catchEnd - catchOpen + 1);

            var done = catchBody.IndexOf("onDone?.Invoke(false);", StringComparison.Ordinal);
            var ret = catchBody.IndexOf("return;", StringComparison.Ordinal);

            Assert.IsTrue(done > 0, "the catch must report onDone(false)");
            Assert.IsTrue(ret > done, "the catch must return after reporting the failure");
            Assert.IsTrue(catchBody.EndsWith("return;}", StringComparison.Ordinal), "return must be the catch block's last statement");

            Assert.IsTrue(catchEnd < body.IndexOf("DatabaseManager.Shard.AddCharacterInParallel(", StringComparison.Ordinal), "the catch sits before the save");
        }

        [TestMethod]
        public void Every_publish_call_is_under_options_Publish()
        {
            var body = Body();

            const string guard = "if(options.Publish){";
            var guardAt = body.IndexOf(guard, StringComparison.Ordinal);
            Assert.IsTrue(guardAt > 0, "the options.Publish block was not found");
            Assert.AreEqual(guardAt, body.LastIndexOf(guard, StringComparison.Ordinal), "expected exactly one options.Publish block");

            var blockStart = guardAt + guard.Length - 1;
            var blockEnd = MatchingBrace(body, blockStart);

            var publishCalls = new[] { "PlayerManager.AddOfflinePlayer(", "AddCharacterToSession(" };
            var expectedCounts = new Dictionary<string, int> { ["PlayerManager.AddOfflinePlayer("] = 1, ["AddCharacterToSession("] = 2 };

            foreach (var call in publishCalls)
            {
                var found = 0;

                for (var at = body.IndexOf(call, StringComparison.Ordinal); at >= 0; at = body.IndexOf(call, at + 1, StringComparison.Ordinal))
                {
                    found++;
                    Assert.IsTrue(at > blockStart && at < blockEnd, $"{call} at squashed offset {at} sits outside the options.Publish block ({blockStart}..{blockEnd})");
                }

                // positive control: a publish call that went missing (or was renamed) must not pass as "none outside"
                Assert.AreEqual(expectedCounts[call], found, $"expected {expectedCounts[call]} {call} call(s) in DoCopyChar");
            }

            // The facet write must still precede publishing (FacetCarryTests pins the order on the raw text too).
            var facetSave = body.IndexOf("FacetCarry.SaveAll(newFacets", StringComparison.Ordinal);
            Assert.IsTrue(facetSave > 0 && facetSave < blockStart, "the facet save must run before the options.Publish block");
        }

        private static int MatchingBrace(string text, int open)
        {
            Assert.AreEqual('{', text[open], "block start is not a brace");

            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                    return i;
            }

            Assert.Fail("unterminated options.Publish block");
            return -1;
        }
    }
}
