using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Knots;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// PatternExchange: the pure decision and partner selection of the Pattern Exchanger NPC. No PropertyManager,
    /// no Player; the inventory work lives in PatternExchangeStation and is on the live-verification list.
    /// </summary>
    [TestClass]
    public class PatternExchangeTests
    {
        private static readonly string[] Two = { "grael", "matron" };

        private static PatternExchangeDecision Decide(int kind = KnotItemKind.Pattern, int fibers = 0, string key = "grael",
            bool partner = true, IEnumerable<string> available = null, Func<int, int> pick = null)
            => PatternExchange.Decide(kind, fibers, key, partner, available ?? Two, pick ?? (_ => 0));

        [TestMethod]
        public void NotAPattern_IsRefused()
        {
            foreach (var kind in new[] { KnotItemKind.None, KnotItemKind.Fiber, KnotItemKind.Knot, KnotItemKind.Pouch })
            {
                var d = Decide(kind: kind);
                Assert.IsFalse(d.Accepted, $"kind {kind}");
                Assert.AreEqual(PatternExchange.NotAPatternLine, d.Refusal);
                Assert.IsNull(d.NewKey);
            }
        }

        [TestMethod]
        public void WovenGiven_IsRefused()
        {
            var d = Decide(fibers: 3);
            Assert.IsFalse(d.Accepted);
            Assert.AreEqual(PatternExchange.WovenLine, d.Refusal);
        }

        [TestMethod]
        public void NoPartner_IsRefused()
        {
            var d = Decide(partner: false);
            Assert.IsFalse(d.Accepted);
            Assert.AreEqual(PatternExchange.NoPartnerLine, d.Refusal);
        }

        [TestMethod]
        public void OnlySameKeyAvailable_IsRefused()
        {
            var d = Decide(available: new[] { "grael" });
            Assert.IsFalse(d.Accepted);
            Assert.AreEqual(PatternExchange.NoOtherPinnacleLine, d.Refusal);
        }

        [TestMethod]
        public void NothingAvailable_IsRefused()
        {
            Assert.AreEqual(PatternExchange.NoOtherPinnacleLine, Decide(available: Array.Empty<string>()).Refusal);
            Assert.AreEqual(PatternExchange.NoOtherPinnacleLine, PatternExchange.Decide(KnotItemKind.Pattern, 0, "grael", true, null, _ => 0).Refusal);
        }

        [TestMethod]
        public void TwoPinnacles_AlwaysTheOther()
        {
            foreach (var pick in new Func<int, int>[] { _ => 0, _ => 1, n => n - 1, _ => 99, _ => -5 })
            {
                var fromGrael = Decide(key: "grael", pick: pick);
                Assert.IsTrue(fromGrael.Accepted);
                Assert.AreEqual("matron", fromGrael.NewKey);

                var fromMatron = Decide(key: "matron", pick: pick);
                Assert.IsTrue(fromMatron.Accepted);
                Assert.AreEqual("grael", fromMatron.NewKey);
            }
        }

        [TestMethod]
        public void ManyPinnacles_NeverTheGivenKey_AndEveryOtherReachable()
        {
            var keys = new[] { "a", "b", "c", "d", "e" };
            var reached = new HashSet<string>();

            for (var i = 0; i < 10; i++)
            {
                var idx = i;
                var d = Decide(key: "c", available: keys, pick: n => idx % n);
                Assert.IsTrue(d.Accepted);
                Assert.AreNotEqual("c", d.NewKey);
                reached.Add(d.NewKey);
            }

            CollectionAssert.AreEquivalent(new[] { "a", "b", "d", "e" }, reached.ToList());
        }

        [TestMethod]
        public void Picker_IsGivenTheCandidateCount_NotTheListLength()
        {
            var seen = 0;
            Decide(key: "c", available: new[] { "a", "b", "c", "d" }, pick: n => { seen = n; return 0; });
            Assert.AreEqual(3, seen);
        }

        [TestMethod]
        public void DuplicateAvailableKeys_AreNotDoubleWeighted()
        {
            var seen = 0;
            Decide(key: "a", available: new[] { "b", "b", "a", "c" }, pick: n => { seen = n; return 0; });
            Assert.AreEqual(2, seen);
        }

        [TestMethod]
        public void UnknownGivenKey_StillExchangesToAnAvailableOne()
        {
            var d = Decide(key: "retired-boss", pick: n => n - 1);
            Assert.IsTrue(d.Accepted);
            Assert.AreEqual("matron", d.NewKey);
        }

        [TestMethod]
        public void EmptyGivenKey_IsRefusedAsNoPartner()
        {
            Assert.AreEqual(PatternExchange.NoPartnerLine, Decide(key: "").Refusal);
            Assert.AreEqual(PatternExchange.NoPartnerLine, Decide(key: null).Refusal);
        }

        [TestMethod]
        public void SuccessLine_IsTheOwnerWording()
            => Assert.AreEqual("Two Patterns of the Grael. Here is one of the Matron.", PatternExchange.SuccessLine("Grael", "Matron"));

        [TestMethod]
        public void Lines_AreAsciiOnly()
        {
            foreach (var s in new[] { PatternExchange.NotAPatternLine, PatternExchange.WovenLine, PatternExchange.NoPartnerLine,
                PatternExchange.NoOtherPinnacleLine, PatternExchange.NoRoomLine, PatternExchange.SuccessLine("A", "B"),
                PatternExchangeStation.FailedNothingTakenLine, PatternExchangeStation.FailedRestoredLine, PatternExchangeStation.FailedLostLine,
                PatternExchange.ConfirmPrompt("Wenna", "Pattern: Grael", "Grael", new[] { "Matron" }),
                PatternExchange.ConfirmPrompt("Wenna", "Pattern: Grael", "Grael", new[] { "Matron", "Other" }),
                PatternExchange.ConfirmPrompt("Wenna", null, "Grael", Array.Empty<string>()) })
                Assert.IsTrue(s.All(c => c >= 32 && c < 127), s);
        }

        // ---- confirmation prompt and the shared candidate filter ----

        [TestMethod]
        public void ConfirmPrompt_OneCandidate_NamesTheGivenPatternAndTheTarget()
        {
            var s = PatternExchange.ConfirmPrompt("Wenna Tarrow", "Pattern: Drift Olthoi Matron", "Drift Olthoi Matron", new[] { "Grael" });

            Assert.AreEqual("Trade your Pattern: Drift Olthoi Matron and one more Pattern of the Drift Olthoi Matron to Wenna Tarrow for one Pattern of the Grael?", s);
            StringAssert.Contains(s, "Pattern: Drift Olthoi Matron");
            StringAssert.Contains(s, "of the Grael?");
        }

        [TestMethod]
        public void ConfirmPrompt_SeveralCandidates_SaysRandom_AndNamesNone()
        {
            var s = PatternExchange.ConfirmPrompt("Wenna Tarrow", "Pattern: Grael", "Grael", new[] { "Matron", "Knight" });

            Assert.AreEqual("Trade your Pattern: Grael and one more Pattern of the Grael to Wenna Tarrow for one Pattern of another pinnacle, chosen at random?", s);
            Assert.IsFalse(s.Contains("Matron"));
            Assert.IsFalse(s.Contains("Knight"));
        }

        [TestMethod]
        public void ConfirmPrompt_EmptyOrNullGivenName_FallsBackToThePatternName()
        {
            foreach (var given in new string[] { null, "" })
            {
                var s = PatternExchange.ConfirmPrompt("Wenna", given, "Grael", new[] { "Matron" });
                StringAssert.StartsWith(s, "Trade your Pattern: Grael and one more");
            }
        }

        [TestMethod]
        public void Candidates_ExcludesGivenKey_DedupesAndDropsEmpty()
        {
            var c = PatternExchange.Candidates(new[] { "a", "", null, "b", "b", "c", "a" }, "c");
            CollectionAssert.AreEqual(new[] { "a", "b" }, c);

            Assert.AreEqual(0, PatternExchange.Candidates(null, "x").Count);
            CollectionAssert.AreEqual(new[] { "A", "a" }, PatternExchange.Candidates(new[] { "A", "a" }, "z"), "ordinal, case-sensitive");
        }

        [TestMethod]
        public void Decide_PicksOnlyFromCandidates()
        {
            var keys = new[] { "a", "", "b", "b", "c" };
            var cands = PatternExchange.Candidates(keys, "c");

            for (var i = 0; i < cands.Count; i++)
            {
                var idx = i;
                Assert.AreEqual(cands[i], Decide(key: "c", available: keys, pick: _ => idx).NewKey);
            }
        }

        [TestMethod]
        public void PromptStillValid_NullNamedKey_True_Equal_True_Different_False()
        {
            Assert.IsTrue(PatternExchange.PromptStillValid(null, "matron"));
            Assert.IsTrue(PatternExchange.PromptStillValid("matron", "matron"));
            Assert.IsFalse(PatternExchange.PromptStillValid("matron", "grael"));
            Assert.IsFalse(PatternExchange.PromptStillValid("matron", "Matron"));
        }

        // ---- confirmation wiring pin ----

        private static string StationSource()
            => ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/Knots/PatternExchangeStation.cs");

        [TestMethod]
        public void HandleGive_OnlyAsksToConfirm_AndTheConsumeLivesInHandleConfirm()
        {
            var source = StationSource();
            var give = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(source, "public static void HandleGive(");
            var confirm = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(source, "private static void HandleConfirm(");

            StringAssert.Contains(give, "new Confirmation_Custom(");
            Assert.IsFalse(give.Contains("TryConsumeFromInventoryWithNetworking"), "HandleGive must not consume anything");
            Assert.IsFalse(give.Contains("TryCreateInInventoryWithNetworking"), "HandleGive must not give anything");
            StringAssert.Contains(confirm, "TryConsumeFromInventoryWithNetworking");

            StringAssert.Contains(give, "EnqueueSend(");
            StringAssert.Contains(give, "() => HandleConfirm(player, npc, givenGuid, namedKey)");

            var gateCall = confirm.IndexOf("TryGate(player, npc, givenGuid, out var gate)", StringComparison.Ordinal);
            Assert.IsTrue(gateCall >= 0, "HandleConfirm re-runs TryGate");
            Assert.IsTrue(gateCall < confirm.IndexOf("TryConsumeFromInventoryWithNetworking", StringComparison.Ordinal), "the gate runs before any consume");
            StringAssert.Contains(confirm, "given.IsBeingTradedOrContainsItemBeingTraded(");
            StringAssert.Contains(confirm, "partner.IsBeingTradedOrContainsItemBeingTraded(");
            StringAssert.Contains(confirm, "PatternExchange.PromptStillValid(namedKey, gate.NewDef.Key)");

            var tryGate = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(source, "private static bool TryGate(");
            StringAssert.Contains(tryGate, "PatternExchange.Candidates(");
        }

        // ---- shipped content and wiring pins ----

        private static string WeenieSql(string wcidAndName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies", wcidAndName + ".sql");

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                dir = dir.Parent;
            }

            Assert.Fail($"Content/sql/weenies/{wcidAndName}.sql not found by walking up from the test assembly");
            return null;
        }

        private const string ExchangerWeenie = "1007006 Pattern Exchanger Wenna Tarrow";

        private static bool HasBool(string sql, uint wcid, int type, bool value)
            => Regex.IsMatch(sql, @"\(\s*" + wcid + @",\s*" + type + @",\s*" + (value ? "True" : "False") + @"\s*\)");

        [TestMethod]
        public void ExchangerWeenie_CarriesTheMarker_IsNotAttackable_AndHasNoGiveEmote()
        {
            var sql = WeenieSql(ExchangerWeenie);

            Assert.IsTrue(HasBool(sql, 1007006, (int)PropertyBool.PatternExchanger, true), "PatternExchanger (9094) = True");
            Assert.AreEqual(9094, (int)PropertyBool.PatternExchanger);
            Assert.IsTrue(HasBool(sql, 1007006, (int)PropertyBool.Attackable, false), "Attackable (19) = False");
            Assert.IsFalse(Regex.IsMatch(sql, @"\(\s*1007006,\s*79,"), "no AiAcceptEverything (79) row: without the intercept it would destroy gifts");
        }

        [TestMethod]
        public void ExchangerWeenie_HasOnlyUseEmoteRows()
        {
            var sql = WeenieSql(ExchangerWeenie);
            var m = Regex.Match(sql, @"INSERT INTO `weenie_properties_emote` .*?VALUES(.*?);", RegexOptions.Singleline);

            Assert.IsTrue(m.Success, "emote INSERT present");
            Assert.IsFalse(Regex.IsMatch(m.Groups[1].Value, @"1007006,\s*" + (int)EmoteCategory.Give + @"\b"), "the only emote category is Use (7), never Give (6)");
            Assert.IsTrue(Regex.IsMatch(m.Groups[1].Value, @"1007006,\s*" + (int)EmoteCategory.Use + @"\b"), "a Use emote row exists");
        }

        [TestMethod]
        public void GiveToNpc_HasTheExactPatternExchangerInterceptHead()
        {
            var source = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Inventory.cs");
            var lines = source.Split('\n').Select(l => l.Trim()).ToList();
            var head = lines.FindIndex(l => l == "if (target.GetProperty(PropertyBool.PatternExchanger) == true)");

            Assert.IsTrue(head >= 0, "the exact `if (target.GetProperty(PropertyBool.PatternExchanger) == true)` head is present");
            Assert.AreEqual(1, lines.Count(l => l == "if (target.GetProperty(PropertyBool.PatternExchanger) == true)"), "exactly one such head");
            Assert.AreEqual("{", lines[head + 1]);
            Assert.AreEqual("Session.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(Session, item.Guid.Full));", lines[head + 2], "the bounce comes first");
            Assert.AreEqual("ACE.Server.Knots.PatternExchangeStation.HandleGive(this, target, item);", lines[head + 3]);
            Assert.AreEqual("return;", lines[head + 4]);
        }

        // ---- partner selection ----

        private static PatternCandidate P(uint guid, string key, int fibers = 0, int kind = KnotItemKind.Pattern, bool traded = false)
            => new PatternCandidate(guid, kind, fibers, key, traded);

        [TestMethod]
        public void FindPartner_TakesASameKeyBareOtherPattern()
            => Assert.AreEqual(20u, PatternExchange.FindPartner(10, "grael", new[] { P(10, "grael"), P(20, "grael") }));

        [TestMethod]
        public void FindPartner_SkipsTheGivenGuid()
            => Assert.AreEqual(0u, PatternExchange.FindPartner(10, "grael", new[] { P(10, "grael") }));

        [TestMethod]
        public void FindPartner_SkipsWoven()
            => Assert.AreEqual(30u, PatternExchange.FindPartner(10, "grael", new[] { P(10, "grael"), P(20, "grael", fibers: 2), P(30, "grael") }));

        [TestMethod]
        public void FindPartner_SkipsOtherKeys()
            => Assert.AreEqual(0u, PatternExchange.FindPartner(10, "grael", new[] { P(10, "grael"), P(20, "matron") }));

        [TestMethod]
        public void FindPartner_SkipsATradedPattern_AndTakesTheUntradedOne()
        {
            Assert.AreEqual(0u, PatternExchange.FindPartner(10, "grael", new[] { P(10, "grael"), P(20, "grael", traded: true) }));
            Assert.AreEqual(30u, PatternExchange.FindPartner(10, "grael", new[] { P(10, "grael"), P(20, "grael", traded: true), P(30, "grael") }));
        }

        [TestMethod]
        public void FindPartner_SkipsNonPatterns()
            => Assert.AreEqual(0u, PatternExchange.FindPartner(10, "grael", new[] { P(10, "grael"), P(20, "grael", kind: KnotItemKind.Knot), P(30, "grael", kind: KnotItemKind.None) }));

        [TestMethod]
        public void FindPartner_KeyIsCaseSensitive_AndEmptyKeyNeverMatches()
        {
            Assert.AreEqual(0u, PatternExchange.FindPartner(10, "grael", new[] { P(20, "Grael") }));
            Assert.AreEqual(0u, PatternExchange.FindPartner(10, "", new[] { P(20, "") }));
            Assert.AreEqual(0u, PatternExchange.FindPartner(10, null, new[] { P(20, null) }));
            Assert.AreEqual(0u, PatternExchange.FindPartner(10, "grael", null));
        }

        [TestMethod]
        public void FindPartner_NeverReturnsGuidZero()
            => Assert.AreEqual(0u, PatternExchange.FindPartner(10, "grael", new[] { P(0, "grael") }));
    }
}
