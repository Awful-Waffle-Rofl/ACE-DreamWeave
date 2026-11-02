using System;
using System.Linq;

using ACE.Server.Knots;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// The Yes/No asked before a Knot use starts a fresh run: which uses ask, which answers go through (Yes in time
    /// only), the prompt text, and source pins for the wiring that needs a live Player. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class KnotUseConfirmationTests
    {
        private static readonly DateTime Sent = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void A_fresh_unconfirmed_use_asks()
            => Assert.IsTrue(KnotSeam.ShouldAskToStart(KnotResume.None, false, true));

        [TestMethod]
        public void A_disabled_pinnacle_does_not_ask()
            => Assert.IsFalse(KnotSeam.ShouldAskToStart(KnotResume.None, false, false));

        [TestMethod]
        public void A_confirmed_use_does_not_ask_again()
            => Assert.IsFalse(KnotSeam.ShouldAskToStart(KnotResume.None, true, true));

        [TestMethod]
        public void A_return_to_a_live_attempt_never_asks()
        {
            foreach (var resume in Enum.GetValues(typeof(KnotResume)).Cast<KnotResume>().Where(r => r != KnotResume.None))
            {
                Assert.IsFalse(KnotSeam.ShouldAskToStart(resume, false, true), resume.ToString());
                Assert.IsFalse(KnotSeam.ShouldAskToStart(resume, true, true), resume.ToString());
            }
        }

        [TestMethod]
        public void Yes_in_time_is_accepted()
        {
            Assert.IsTrue(Confirmation_KnotUse.Accepts(true, false, Sent, Sent));
            Assert.IsTrue(Confirmation_KnotUse.Accepts(true, false, Sent, Sent.AddSeconds(29.0)));
        }

        [TestMethod]
        public void No_is_never_accepted()
        {
            Assert.IsFalse(Confirmation_KnotUse.Accepts(false, false, Sent, Sent));
            Assert.IsFalse(Confirmation_KnotUse.Accepts(false, true, Sent, Sent));
        }

        [TestMethod]
        public void A_timeout_or_late_yes_is_not_accepted()
        {
            Assert.IsFalse(Confirmation_KnotUse.Accepts(true, true, Sent, Sent), "a timeout is late whenever it arrives");
            Assert.IsFalse(Confirmation_KnotUse.Accepts(true, false, Sent, Sent.AddSeconds(29.5)));
            Assert.IsFalse(Confirmation_KnotUse.Accepts(true, false, Sent, Sent.AddSeconds(45)));
        }

        [TestMethod]
        public void The_prompt_is_plain_ascii_and_short()
        {
            foreach (var name in new[] { "Knot", "Grove Knot (Level 3)", new string('x', 500), null, "  ", "Kn\u00F6t \u2013 caf\u00E9" })
            {
                var prompt = KnotItems.UsePrompt(name);
                Assert.IsTrue(prompt.Length < 250, prompt.Length.ToString());
                Assert.IsTrue(prompt.All(c => c >= 0x20 && c < 0x7F), "ASCII only");
                StringAssert.EndsWith(prompt, "The run begins immediately.");
            }

            Assert.AreEqual("Start a Knot run with Grove Knot? The run begins immediately.", KnotItems.UsePrompt("Grove Knot"));
        }

        [TestMethod]
        public void The_confirmation_gates_on_Accepts_then_hops_onto_the_player_queue()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Knots/KnotSeam.cs").Replace("\r\n", "\n");
            var body = PooledLootSourceText.MethodBody(source, "public override void ProcessConfirmation(bool response, bool timeout = false)");

            var gate = body.IndexOf("if (!Accepts(response, timeout, SentUtc, DateTime.UtcNow))\n                return;", StringComparison.Ordinal);
            var hop = body.IndexOf("player.EnqueueAction(new ActionEventDelegate(() => KnotSeam.OnUseConfirmed(player, knotGuid)));", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "answer gate present");
            Assert.IsTrue(hop > gate, "the use is queued only after the gate");
            StringAssert.Contains(source, "ACE.Server.ThreadDungeons.Confirmation_ThreadGroupStart.IsLate(sentUtc, nowUtc, timeout)");
        }

        [TestMethod]
        public void The_question_sits_before_TryEnter_and_the_answer_reruns_the_whole_use()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Knots/KnotSeam.cs").Replace("\r\n", "\n");
            var body = PooledLootSourceText.MethodBody(source, "private static bool RunUse(WorldObject knot, Player player, bool confirmed)");

            var ask = body.IndexOf("{\n                AskToStart(knot, player);\n                return true;\n            }", StringComparison.Ordinal);
            Assert.IsTrue(body.IndexOf("if (ShouldAskToStart(resume, confirmed, ", StringComparison.Ordinal) is var cond && cond >= 0 && cond < ask, "the condition guards the ask and its return");
            var enter = body.IndexOf("encounter.TryEnter(", StringComparison.Ordinal);
            Assert.IsTrue(ask >= 0 && enter > ask, "the question precedes TryEnter");

            var answer = PooledLootSourceText.MethodBody(source, "internal static void OnUseConfirmed(Player player, uint knotGuid)");
            StringAssert.Contains(answer, "Player.SearchLocations.MyInventory");
            StringAssert.Contains(answer, "RunUse(knot, player, true);");

            var pub = PooledLootSourceText.MethodBody(source, "public static bool TryHandleUse(WorldObject knot, Player player)");
            StringAssert.Contains(pub, "RunUse(knot, player, false);");
            Assert.IsFalse(source.Contains("bool confirmed = false"), "no public way to skip the question");
        }

        [TestMethod]
        public void The_answer_repeats_every_gate_the_original_use_passed()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Knots/KnotSeam.cs").Replace("\r\n", "\n");
            var body = PooledLootSourceText.MethodBody(source, "internal static void OnUseConfirmed(Player player, uint knotGuid)");

            var find = body.IndexOf("Player.SearchLocations.MyInventory", StringComparison.Ordinal);
            var last = find;
            foreach (var marker in new[]
            {
                "if (player.PKLogout)",
                "player.IsTrading && knot.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow)",
                "WeenieError.TradeItemBeingTraded",
                "player.PvpTemplateBlocked(PvpTemplateAction.Use, knot)",
                "if (!knot.Active)",
                "knot.CheckUseRequirements(player)",
                "player.IsBusy || player.Teleporting || player.suicideInProgress",
                "if (player.IsJumping)",
                "WeenieError.YouCantDoThatWhileInTheAir",
                "RunUse(knot, player, true);",
            })
            {
                var i = body.IndexOf(marker, last, StringComparison.Ordinal);
                Assert.IsTrue(i > last - 1 && i >= 0, marker + " missing or out of order");
                last = i;
            }
        }

        [TestMethod]
        public void A_second_question_is_refused_by_the_single_yes_no_slot()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Knots/KnotSeam.cs").Replace("\r\n", "\n");
            var body = PooledLootSourceText.MethodBody(source, "private static void AskToStart(WorldObject knot, Player player)");

            StringAssert.Contains(body, "if (!player.ConfirmationManager.EnqueueSend(new Confirmation_KnotUse(");
            StringAssert.Contains(body, "WeenieError.ConfirmationInProgress");
        }
    }
}
