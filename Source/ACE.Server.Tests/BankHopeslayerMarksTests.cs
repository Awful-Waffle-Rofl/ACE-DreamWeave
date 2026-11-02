using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// /bank support for Mark of the Hopeslayer (wcid 1006650, the PvP arena currency). A live Player cannot be built
    /// in this harness (its constructor reads the auth database), so the decisions are tested through the pure
    /// statics the Player methods call, and the call sites that cannot be driven are pinned by source text, each
    /// inside its own method body (brace-matched, comments stripped, whitespace ignored). The deposit, withdraw and
    /// vendor-draw flows against a real pack are exercised in-game.
    ///
    /// This class reads no PropertyManager key, so there is nothing to seed or restore.
    /// </summary>
    [TestClass]
    public class BankHopeslayerMarksTests
    {
        private const string Bank = "Source/ACE.Server/WorldObjects/Player_Bank.cs";
        private const string Commands = "Source/ACE.Server/Command/Handlers/PlayerCommands.cs";

        // ---------------- identity ----------------

        [TestMethod]
        public void MarkWcid_IsThePvpRewardCurrency_AndTheBankPropertyIsTheRegisteredId()
        {
            Assert.AreEqual(1006650u, PvpArenaRewards.BloodWcid);
            Assert.AreEqual(PvpArenaRewards.BloodWcid, Player.HopeslayerMarkWcid, "the bank must key on the weenie the arena pays out");
            Assert.AreEqual(9027, (int)PropertyInt64.BankedHopeslayerMarks);
        }

        [TestMethod]
        public void PropertyRegistry_ReservesTheBankPropertyAsActiveBanking()
        {
            var tsv = File.ReadAllText(RepoFile("Source/property-registry.tsv"));

            StringAssert.Contains(tsv, "PropertyInt64\t9027\tBankedHopeslayerMarks\tbanking\tactive\t");
        }

        // ---------------- deposit: all / amount / stacks ----------------

        [TestMethod]
        public void PlanMarkDeposit_AllTakesEverythingHeld()
        {
            Assert.AreEqual(2500, Player.PlanMarkDeposit(2500, -1), "held across several stacks, 'all' takes the lot");
        }

        [TestMethod]
        public void PlanMarkDeposit_AmountTakesThatMany_ClampedToWhatIsHeld()
        {
            Assert.AreEqual(300, Player.PlanMarkDeposit(1000, 300));
            Assert.AreEqual(1000, Player.PlanMarkDeposit(1000, 1000));
            Assert.AreEqual(1000, Player.PlanMarkDeposit(1000, 5000), "asking for more than is held credits only what is held");
        }

        [TestMethod]
        public void PlanMarkDeposit_NothingHeldOrZeroAskedTakesNothing()
        {
            Assert.AreEqual(0, Player.PlanMarkDeposit(0, -1));
            Assert.AreEqual(0, Player.PlanMarkDeposit(0, 50));
            Assert.AreEqual(0, Player.PlanMarkDeposit(1000, 0));
            Assert.AreEqual(0, Player.PlanMarkDeposit(-5, -1), "a corrupt negative count must never credit");
        }

        [TestMethod]
        public void DepositMarks_ConsumesThroughTheInventoryAndCreditsOnlyWhatLeftThePack()
        {
            var body = MethodBody(Bank, "public long DepositHopeslayerMarks(long amount = -1, bool suppressChat = false)");

            AssertContains(body, "PlanMarkDeposit(held, amount)");
            AssertContains(body, "RunMarkDeposit(take, planned =>");
            AssertContains(body, "TryConsumeFromInventoryWithNetworking(HopeslayerMarkWcid, (int)planned)");
            AssertContains(body, "ModifyBankBalance(PropertyInt64.BankedHopeslayerMarks, deposited)");
        }

        [TestMethod]
        public void DepositMarks_ZeroAmountIsRefusedWithTheAmountMessage_BeforeAnythingIsRead()
        {
            var body = MethodBody(Bank, "public long DepositHopeslayerMarks(long amount = -1, bool suppressChat = false)");

            var zero = body.IndexOf(Squash("if (amount == 0)"), StringComparison.Ordinal);
            var message = body.IndexOf(Squash("BankMsg(\"Amount must be greater than zero.\")"), StringComparison.Ordinal);
            var held = body.IndexOf(Squash("var held ="), StringComparison.Ordinal);

            Assert.IsTrue(zero >= 0 && message > zero && held > message, "'/b d m 0' must say the amount is invalid, ahead of the 'no Marks found' path");
        }

        // ---------------- no double-credit / double-debit ----------------

        [TestMethod]
        public void RunMarkWithdraw_FailsPartway_ReportsExactlyWhatWasCreated()
        {
            // 3000 owed, stacks of 1000: the third creation fails (pack full).
            var calls = 0;
            var asked = new System.Collections.Generic.List<long>();

            var created = Player.RunMarkWithdraw(3000, remaining =>
            {
                asked.Add(remaining);
                return ++calls <= 2 ? Math.Min(1000, remaining) : 0;
            });

            Assert.AreEqual(2000, created, "the debit must equal what reached the pack, not what was asked for");
            CollectionAssert.AreEqual(new long[] { 3000, 2000, 1000 }, asked, "each call is asked only for what is still owed");
        }

        [TestMethod]
        public void RunMarkWithdraw_FullSuccessAndNothingCreated()
        {
            Assert.AreEqual(2500, Player.RunMarkWithdraw(2500, remaining => Math.Min(1000, remaining)));
            Assert.AreEqual(0, Player.RunMarkWithdraw(2500, remaining => 0), "a creator that never places anything debits nothing");
            Assert.AreEqual(0, Player.RunMarkWithdraw(0, remaining => 1000), "nothing owed, nothing created");
        }

        [TestMethod]
        public void RunMarkWithdraw_NeverCreditsMoreThanOwed()
        {
            Assert.AreEqual(500, Player.RunMarkWithdraw(500, remaining => 1000), "a creator that over-reports cannot push the debit past the request");
        }

        [TestMethod]
        public void RunMarkDeposit_ConsumeRemovesLessThanAsked_CreditsOnlyWhatLeft()
        {
            long askedFor = -1;

            var credited = Player.RunMarkDeposit(100, planned =>
            {
                askedFor = planned;
                return 60;
            });

            Assert.AreEqual(100, askedFor);
            Assert.AreEqual(60, credited, "only the Marks that left the pack are credited");
        }

        [TestMethod]
        public void RunMarkDeposit_ClampsOverReportAndSkipsAnEmptyPlan()
        {
            Assert.AreEqual(100, Player.RunMarkDeposit(100, planned => 500), "a consume that over-reports cannot credit more than was asked");
            Assert.AreEqual(0, Player.RunMarkDeposit(100, planned => -3), "a negative report never debits the bank");

            var called = false;
            Assert.AreEqual(0, Player.RunMarkDeposit(0, planned => { called = true; return 5; }));
            Assert.IsFalse(called, "an empty plan must not touch the pack");
        }

        [TestMethod]
        public void BalanceMutations_HappenExactlyOnce_AndAfterTheItemMove()
        {
            var deposit = MethodBody(Bank, "public long DepositHopeslayerMarks(long amount = -1, bool suppressChat = false)");
            var withdraw = MethodBody(Bank, "public void WithdrawHopeslayerMarks(long amount)");
            var debit = MethodBody(Bank, "public string DebitBankedAlternateCurrency(uint wcid, long amount, out bool stateUnknown)");

            var mutation = Squash("ModifyBankBalance(PropertyInt64.BankedHopeslayerMarks");

            Assert.AreEqual(1, CountOf(deposit, mutation), "deposit credits the bank exactly once");
            Assert.AreEqual(1, CountOf(withdraw, mutation), "withdraw debits the bank exactly once");
            Assert.AreEqual(1, CountOf(debit, mutation), "the vendor draw debits the bank exactly once");

            Assert.IsTrue(deposit.IndexOf(Squash("RunMarkDeposit("), StringComparison.Ordinal) < deposit.IndexOf(mutation, StringComparison.Ordinal),
                "the credit must come AFTER the consume");
            Assert.IsTrue(withdraw.IndexOf(Squash("RunMarkWithdraw("), StringComparison.Ordinal) < withdraw.IndexOf(mutation, StringComparison.Ordinal),
                "the debit must come AFTER the stacks are created");
        }

        [TestMethod]
        public void DepositAndWithdraw_PersistThePlayerRightAfterTheBalanceMoves()
        {
            foreach (var signature in new[] { "public long DepositHopeslayerMarks(long amount = -1, bool suppressChat = false)", "public void WithdrawHopeslayerMarks(long amount)" })
            {
                var body = MethodBody(Bank, signature);
                var mutation = body.IndexOf(Squash("ModifyBankBalance(PropertyInt64.BankedHopeslayerMarks"), StringComparison.Ordinal);
                var save = body.IndexOf(Squash("SaveBiotaToDatabase();"), StringComparison.Ordinal);

                Assert.IsTrue(save > mutation && mutation >= 0, $"{signature}: the balance and the pack must be saved together, right after the balance moves");
                Assert.AreEqual(1, CountOf(body, Squash("SaveBiotaToDatabase();")), signature);
            }
        }

        [TestMethod]
        public void WithdrawMessages_SayMarksNotMarkParenS()
        {
            var body = MethodBody(Bank, "public void WithdrawHopeslayerMarks(long amount)");

            AssertContains(body, "Withdrew {created:N0} Marks of the Hopeslayer. Balance:");
            AssertContains(body, "Withdrew {created:N0} of {amount:N0} Marks of the Hopeslayer (partial");
            Assert.IsFalse(body.Contains(Squash("Mark(s)"), StringComparison.Ordinal));
        }

        [TestMethod]
        public void DepositAll_SweepsMarksToo_AndReportsTheBalance()
        {
            var body = MethodBody(Bank, "public void DepositAll()");

            AssertContains(body, "DepositHopeslayerMarks(-1, suppressChat: true);");
            AssertContains(body, "Marks of the Hopeslayer: {BankedHopeslayerMarks:N0}");
        }

        [TestMethod]
        public void DepositCommand_RoutesTheMarkTokens_WithTheParsedAmount()
        {
            var body = MethodBody(Commands, "private static void HandleBankDeposit(Player player, string cur, string[] parameters)");

            AssertContains(body, "case \"m\": case \"mark\": case \"marks\": player.DepositHopeslayerMarks(amount); break;");
            AssertContains(body, "Use p, l, k, pn, n, pea, or m.");
        }

        // ---------------- withdraw: stacks, cap, partial ----------------

        [TestMethod]
        public void NextStackSize_HandsMarksOutAtTheWeeniesStackCap()
        {
            // 1006650 is a Stackable with MaxStackSize 1000 (Content/sql/weenies/1006650 Blood.sql).
            Assert.AreEqual(1000, Player.NextStackSize(2500, 1000));
            Assert.AreEqual(500, Player.NextStackSize(500, 1000));
        }

        [TestMethod]
        public void WithdrawMarks_BuildsStacks_AndDebitsOnlyWhatWasCreated()
        {
            var body = MethodBody(Bank, "public void WithdrawHopeslayerMarks(long amount)");

            AssertContains(body, "if (amount > BankedHopeslayerMarks)");
            AssertContains(body, "if (amount > MaxWithdrawItems)");
            AssertContains(body, "RunMarkWithdraw(amount, remaining => TryCreateStackInInventory(HopeslayerMarkWcid, remaining))");
            AssertContains(body, "ModifyBankBalance(PropertyInt64.BankedHopeslayerMarks, -created)");
            AssertContains(body, "(partial - out of pack space)");
            AssertContains(body, "Could not withdraw - check your pack space.");
        }

        [TestMethod]
        public void WithdrawCommand_RoutesTheMarkTokens()
        {
            var body = MethodBody(Commands, "private static void HandleBankWithdraw(Player player, string cur, string[] parameters)");

            AssertContains(body, "case \"m\": case \"mark\": case \"marks\":");
            AssertContains(body, "Usage: /b w m <amount>");
            AssertContains(body, "player.WithdrawHopeslayerMarks(mAmt);");
        }

        // ---------------- vendor draw from the bank ----------------

        [TestMethod]
        public void TryDebitBankedMarks_AffordableDebitReducesTheBalance()
        {
            Assert.IsTrue(Player.TryDebitBankedMarks(100, 40, out var after));
            Assert.AreEqual(60, after);

            Assert.IsTrue(Player.TryDebitBankedMarks(100, 100, out after));
            Assert.AreEqual(0, after, "the exact balance is affordable");

            Assert.IsTrue(Player.TryDebitBankedMarks(100, 0, out after));
            Assert.AreEqual(100, after, "a zero debit is a no-op success");
        }

        [TestMethod]
        public void TryDebitBankedMarks_RejectsOverdraftAndNegative_WithoutMutating()
        {
            Assert.IsFalse(Player.TryDebitBankedMarks(100, 101, out var after));
            Assert.AreEqual(100, after, "an unaffordable debit must leave the balance untouched");

            Assert.IsFalse(Player.TryDebitBankedMarks(100, -1, out after), "a negative debit can never credit marks back");
            Assert.AreEqual(100, after);

            Assert.IsFalse(Player.TryDebitBankedMarks(0, 1, out after));
        }

        [TestMethod]
        public void AlternateCurrencyName_NamesTheMark_SoTheShopHookTreatsItAsBankable()
        {
            // Player_Commerce.SpendCurrency treats a non-null name as "this currency is bankable".
            Assert.AreEqual("Marks of the Hopeslayer", Player.GetAlternateCurrencyName(1006650));
            Assert.IsNull(Player.GetAlternateCurrencyName(1006651), "only the Mark is added, not its neighbours");
        }

        [TestMethod]
        public void ShopHook_ReadsAndDebitsTheMarkBalance()
        {
            var read = MethodBody(Bank, "public long GetBankedAlternateCurrency(uint wcid)");
            AssertContains(read, "if (wcid == HopeslayerMarkWcid) return BankedHopeslayerMarks;");

            var debit = MethodBody(Bank, "public string DebitBankedAlternateCurrency(uint wcid, long amount, out bool stateUnknown)");
            AssertContains(debit, "if (wcid == HopeslayerMarkWcid && TryDebitBankedMarks(BankedHopeslayerMarks, amount, out _))");
            AssertContains(debit, "ModifyBankBalance(PropertyInt64.BankedHopeslayerMarks, -amount);");
            AssertContains(debit, "return HopeslayerMarkName;");
        }

        // ---------------- transfer is refused ----------------

        [TestMethod]
        public void TransferRefusal_CatchesEveryMarkToken_AndNothingElse()
        {
            foreach (var token in new[] { "m", "mark", "marks" })
            {
                Assert.IsTrue(Player.TryGetBankTransferRefusal(token, out var message), $"'{token}' must be refused");
                Assert.AreEqual("Marks of the Hopeslayer are bonded and cannot be transferred to other characters.", message);
            }

            foreach (var token in new[] { "p", "pyreals", "k", "keys", "pn", "promissory", "l", "" })
            {
                Assert.IsFalse(Player.TryGetBankTransferRefusal(token, out var message), $"'{token}' keeps its existing behaviour");
                Assert.IsNull(message);
            }
        }

        [TestMethod]
        public void TransferCommand_RefusesMarksBeforeTheUsageAmountAndTargetChecks()
        {
            var body = Squash(MethodBody(Commands, "private static void HandleBankTransfer(Player player, string cur, string[] parameters)"));

            var refusal = body.IndexOf(Squash("if (Player.TryGetBankTransferRefusal(cur, out var refusal))"), StringComparison.Ordinal);
            var usage = body.IndexOf(Squash("if (parameters.Length < 4)"), StringComparison.Ordinal);
            var amount = body.IndexOf(Squash("Player.TryParseAmount(parameters[2], out var amount)"), StringComparison.Ordinal);

            Assert.IsTrue(refusal >= 0, "the transfer handler must consult the refusal");
            Assert.IsTrue(refusal < usage && refusal < amount, "the refusal must come first, so '/b t m' alone still explains why");
            Assert.IsFalse(body.Contains(Squash("case \"m\":")), "there must be no transfer branch for Marks");
        }

        [TestMethod]
        public void TransferCommand_RefusalBlockIsOneUnit_EndingInReturn()
        {
            var body = MethodBody(Commands, "private static void HandleBankTransfer(Player player, string cur, string[] parameters)");

            AssertContains(body, "if (Player.TryGetBankTransferRefusal(cur, out var refusal))"
                + " { player.Session.Network.EnqueueSend(new GameMessageSystemChat($\"[BANK] {refusal}\", ChatMessageType.System)); return; }");
        }

        // ---------------- display ----------------

        [TestMethod]
        public void BalancesAndHelp_MentionMarks()
        {
            var balances = MethodBody(Commands, "private static void ShowBankBalances(Session session)");
            AssertContains(balances, "[BANK] Marks of the Hopeslayer: {player.BankedHopeslayerMarks:N0}");

            var help = MethodBody(Commands, "private static void ShowBankHelp(Session session)");
            AssertContains(help, "m=Marks of the Hopeslayer");
            AssertContains(help, "/b w m <amount>      - withdraw Marks of the Hopeslayer");
            AssertContains(help, "Marks of the Hopeslayer are bonded and cannot be transferred");
        }

        // ---------------- source helpers ----------------

        private static string RepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            throw new FileNotFoundException($"Could not find {relative} by walking up from {AppContext.BaseDirectory}");
        }

        private static string Squash(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);

            return sb.ToString();
        }

        /// <summary>Removes // and /* */ comments, leaving string and char literals alone.</summary>
        private static string StripComments(string src)
        {
            var sb = new StringBuilder(src.Length);

            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];

                if (c == '"' || c == '\'')
                {
                    var verbatim = c == '"' && i > 0 && src[i - 1] == '@';
                    sb.Append(c);
                    i++;

                    while (i < src.Length)
                    {
                        sb.Append(src[i]);

                        if (!verbatim && src[i] == '\\' && i + 1 < src.Length)
                        {
                            i++;
                            sb.Append(src[i]);
                        }
                        else if (src[i] == c)
                        {
                            if (verbatim && i + 1 < src.Length && src[i + 1] == '"')
                            {
                                i++;
                                sb.Append(src[i]);
                            }
                            else
                                break;
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n')
                        i++;

                    sb.Append('\n');
                    continue;
                }

                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                        i++;

                    i++;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>The brace-matched body of the one method whose signature starts with <paramref name="signature"/>, whitespace-squashed.</summary>
        private static string MethodBody(string relativePath, string signature)
        {
            var src = StripComments(File.ReadAllText(RepoFile(relativePath)));

            // Match the signature whitespace-insensitively against the raw text by scanning squashed offsets.
            var needle = Squash(signature);
            var map = new System.Collections.Generic.List<int>(src.Length);
            var squashed = new StringBuilder(src.Length);

            for (var i = 0; i < src.Length; i++)
            {
                if (char.IsWhiteSpace(src[i]))
                    continue;

                squashed.Append(src[i]);
                map.Add(i);
            }

            var squashedText = squashed.ToString();
            var at = squashedText.IndexOf(needle, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"signature not found in {relativePath}: {signature}");
            Assert.AreEqual(at, squashedText.LastIndexOf(needle, StringComparison.Ordinal), $"signature is ambiguous in {relativePath}: {signature}");

            var open = src.IndexOf('{', map[at + needle.Length - 1]);
            Assert.IsTrue(open >= 0, "no body found");

            var depth = 0;
            for (var i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return Squash(src.Substring(open, i - open + 1));
            }

            Assert.Fail($"unterminated body for {signature}");
            return null;
        }

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;
            for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
                count++;

            return count;
        }

        private static void AssertContains(string squashedBody, string expected)
        {
            Assert.IsTrue(squashedBody.Contains(Squash(expected), StringComparison.Ordinal), $"expected the method to contain: {expected}");
        }
    }
}
