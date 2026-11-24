using System;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Luminance earned this login (ephemeral PropertyInt64 9102) is pushed to the owning client so client-side
    /// luminance-per-hour trackers can read it. The banked balance (9005) is deliberately NOT sent. A live Player
    /// cannot be built in this harness (its constructor reads the auth database) and the harness cannot observe a
    /// player's enqueued GameMessages, so behaviour is pinned by reflection on the property attributes and by
    /// source text inside method bodies (brace-matched, comments stripped, whitespace ignored). The wire behaviour
    /// is exercised in game.
    ///
    /// This class reads no PropertyManager key, so there is nothing to seed or restore.
    /// </summary>
    [TestClass]
    public class BankedLuminanceClientUpdateTests
    {
        private const string Bank = "Source/ACE.Server/WorldObjects/Player_Bank.cs";
        private const string Lum = "Source/ACE.Server/WorldObjects/Player_Luminance.cs";

        [TestMethod]
        public void Property9102_IsEphemeral_NeverSendOnLogin_AndTheBankIdsAreUntouched()
        {
            Assert.AreEqual(9102, (int)PropertyInt64.LuminanceEarnedThisSession);
            Assert.IsTrue(EphemeralProperties.PropertiesInt64.Contains(PropertyInt64.LuminanceEarnedThisSession), "9102 must be [Ephemeral] so it is never saved to the biota");
            Assert.IsFalse(SendOnLoginProperties.PropertiesInt64.Contains((ushort)PropertyInt64.LuminanceEarnedThisSession), "custom ids stay out of the login PlayerDescription");

            // Positive controls for the two sets: a known ephemeral, and the persisted bank balance.
            Assert.IsTrue(EphemeralProperties.PropertiesInt64.Contains(PropertyInt64.AccountQuestStampCount));
            Assert.IsFalse(EphemeralProperties.PropertiesInt64.Contains(PropertyInt64.BankedLuminance), "the bank balance itself must stay persisted");
            Assert.IsFalse(SendOnLoginProperties.PropertiesInt64.Contains((ushort)PropertyInt64.BankedLuminance), "9005 is not sent at login");
            Assert.IsTrue(SendOnLoginProperties.PropertiesInt64.Contains((ushort)PropertyInt64.AvailableLuminance), "the retail luminance bar keeps its login send");
        }

        [TestMethod]
        public void Property9102_IsReservedInTheRegistry()
        {
            var tsv = File.ReadAllLines(RepoFile("Source/property-registry.tsv"));
            var rows = tsv.Where(l => l.StartsWith("PropertyInt64\t9102\t", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(1, rows.Count);
            StringAssert.StartsWith(rows[0], "PropertyInt64\t9102\tLuminanceEarnedThisSession\tbanking\t");
        }

        [TestMethod]
        public void BankFile_HasNoClientSend_AndNeverMentionsTheNewProperty()
        {
            var src = StripComments(File.ReadAllText(RepoFile(Bank)));
            Assert.IsFalse(src.Contains("SendBankedLuminance", StringComparison.Ordinal), "no 9005 send helper");
            Assert.IsFalse(src.Contains("GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.BankedLuminance", StringComparison.Ordinal), "no 9005 send");
            Assert.IsFalse(src.Contains("LuminanceEarnedThisSession", StringComparison.Ordinal), "bank code does not touch the session counter");

            // ModifyBankBalance is the master shape: the lock holds only the read-modify-write.
            var body = MethodBody(Bank, "private long ModifyBankBalance(PropertyInt64 prop, long delta)");
            AssertContains(body, "lock (bankBalanceLock) { var updated = SaturatingAdd(GetProperty(prop) ?? 0, delta); SetProperty(prop, updated); return updated; }");

            // Negative control: the shape this PR used to have is not the shape above.
            Assert.IsFalse(body.Contains(Squash("SendBankedLuminanceUpdate(updated)"), StringComparison.Ordinal));
        }

        [TestMethod]
        public void Transfer_WritesTheBalancesDirectly_WithNoClientSend()
        {
            var transfer = MethodBody(Bank, "private bool Transfer(BankCurrency currency, string label, long amount, string targetName)");
            AssertContains(transfer, "SetProperty(prop, GetBankBalance(currency) - amount); online.SetProperty(prop, (online.GetProperty(prop) ?? 0) + amount); } }");
            Assert.IsFalse(transfer.Contains("PrivateUpdatePropertyInt64", StringComparison.Ordinal));
        }

        [TestMethod]
        public void TheCounter_IsTouchedOnlyInPlayerLuminance_AndOnlyAddLuminanceCountsIt_UnderTheLock()
        {
            var all = Directory.EnumerateFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace(Path.DirectorySeparatorChar, '/').Contains("/obj/") && !f.Replace(Path.DirectorySeparatorChar, '/').Contains("/bin/"))
                .ToList();

            var outside = all.Where(f => !f.EndsWith("Player_Luminance.cs", StringComparison.Ordinal))
                .Where(f => File.ReadAllText(f).Contains("LuminanceEarnedThisSession", StringComparison.Ordinal))
                .ToList();
            Assert.AreEqual(0, outside.Count, "the counter must be touched only from Player_Luminance.cs: " + string.Join("; ", outside));

            // The helper is called from exactly one place: AddLuminance.
            var callers = all.SelectMany(f => File.ReadAllLines(f).Select((l, n) => (f, l, n)))
                .Where(x => x.l.Contains("CountEarnedLuminance(", StringComparison.Ordinal) && !x.l.Contains("private void CountEarnedLuminance", StringComparison.Ordinal))
                .ToList();
            Assert.AreEqual(1, callers.Count, "unexpected callers: " + string.Join("; ", callers.Select(c => c.f + ":" + (c.n + 1))));

            var add = MethodBody(Lum, "private void AddLuminance(long amount, XpType xpType)");
            AssertContains(add, "ModifyBankBalance(PropertyInt64.BankedLuminance, amount); CountEarnedLuminance(amount);");

            var counting = MethodBody(Lum, "private void CountEarnedLuminance(long amount)");
            AssertContains(counting, "var total = SaturatingAdd(LuminanceEarnedThisSession, amount);");
            Assert.IsTrue(SendIsInsideBalanceLock(counting, "var total = SaturatingAdd(LuminanceEarnedThisSession, amount);"), "the add must sit inside the balance lock");
            Assert.IsTrue(SendIsInsideBalanceLock(counting, "LuminanceEarnedThisSession = total;"), "the store must sit inside the balance lock");
            Assert.IsTrue(SendIsInsideBalanceLock(counting, "SendLuminanceEarnedUpdate(total)"), "the send must sit inside the balance lock");

            var catchAt = counting.IndexOf("catch(Exceptionex)", StringComparison.Ordinal);
            Assert.IsTrue(catchAt > 0);
            Assert.IsFalse(counting.Substring(catchAt).Contains("throw", StringComparison.Ordinal), "a send failure must never propagate into the grant");
        }

        [TestMethod]
        public void NonEarningPaths_DoNotTouchTheCounter()
        {
            // Spend and the retail bar update never mention it; deposit, transfer and admin adds live in
            // Player_Bank.cs, which the bank-file test pins as free of the counter.
            foreach (var method in new[] { "public bool SpendLuminance(long amount)", "private void UpdateLuminance()" })
                Assert.IsFalse(MethodBody(Lum, method).Contains("LuminanceEarned", StringComparison.Ordinal), method);

            // Negative control for the reference scan: the earning helper does mention it.
            Assert.IsTrue(MethodBody(Lum, "private void CountEarnedLuminance(long amount)").Contains("LuminanceEarned", StringComparison.Ordinal));
        }

        [TestMethod]
        public void SendIsInsideBalanceLock_RejectsASendHoistedOutOfTheLock()
        {
            // Negative control: the same predicate must fail for the hoisted shape, or the lock tests prove nothing.
            var hoisted = Squash("lock (bankBalanceLock) { var total = 1; LuminanceEarnedThisSession = total; } SendLuminanceEarnedUpdate(total);");
            Assert.IsFalse(SendIsInsideBalanceLock(hoisted, "SendLuminanceEarnedUpdate(total)"));

            var inside = Squash("lock (bankBalanceLock) { var total = 1; LuminanceEarnedThisSession = total; SendLuminanceEarnedUpdate(total); }");
            Assert.IsTrue(SendIsInsideBalanceLock(inside, "SendLuminanceEarnedUpdate(total)"));
        }

        [TestMethod]
        public void TheSendHelper_GuardsANullSessionOrNetwork_AndSendsInt64_9102()
        {
            var body = MethodBody(Lum, "private void SendLuminanceEarnedUpdate(long total)");
            AssertContains(body, "var network = Session?.Network;");
            AssertContains(body, "if (network == null) return;");
            AssertContains(body, "network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.LuminanceEarnedThisSession, total));");
        }

        [TestMethod]
        public void TheLoginPush_IsUnconditional_AndReachedFromTheFirstEnterWorldBlock()
        {
            var hook = MethodBody("Source/ACE.Server/WorldObjects/Player_Networking.cs", "public void SendPropertyUpdatesAndOverrides()");
            AssertContains(hook, "SendLuminanceEarnedOnLogin();");

            // Unconditional: the call sits at the method body's own brace depth (1), not inside an if or other block.
            var callAt = hook.IndexOf("SendLuminanceEarnedOnLogin();", StringComparison.Ordinal);
            var depth = hook.Take(callAt).Count(c => c == '{') - hook.Take(callAt).Count(c => c == '}');
            Assert.AreEqual(1, depth, "the login send must not be conditional");

            var arrival = MethodBody("Source/ACE.Server/WorldObjects/Player_TeleportArrival.cs", "public void CompleteArrival(TeleportCompletionSource source)");
            AssertContains(arrival, "FirstEnterWorldDone = true; SendPropertyUpdatesAndOverrides();");

            var login = MethodBody(Lum, "private void SendLuminanceEarnedOnLogin()");
            AssertContains(login, "lock (bankBalanceLock) { LuminanceEarnedThisSession = LuminanceEarnedThisSession; SendLuminanceEarnedUpdate(LuminanceEarnedThisSession); }");
            var seed = login.IndexOf("LuminanceEarnedThisSession=LuminanceEarnedThisSession;", StringComparison.Ordinal);
            var send = login.IndexOf("SendLuminanceEarnedUpdate(", StringComparison.Ordinal);
            Assert.IsTrue(seed > 0 && send > seed, "the key must be seeded on the player's thread before the send");
            Assert.IsFalse(login.Contains("if(", StringComparison.Ordinal), "no zero-balance guard: a zero is the baseline");
        }

        private static string ServerRoot()
        {
            var serverDir = Path.GetDirectoryName(RepoFile(Bank)).Replace(Path.DirectorySeparatorChar, '/');
            return serverDir.Substring(0, serverDir.IndexOf("/WorldObjects", StringComparison.Ordinal));
        }

        /// <summary>Index of the brace matching the one at <paramref name="open"/> in squashed code.</summary>
        private static int CloseOf(string squashed, int open)
        {
            var depth = 0;
            for (var i = open; i < squashed.Length; i++)
            {
                if (squashed[i] == '{') depth++;
                else if (squashed[i] == '}' && --depth == 0)
                    return i;
            }

            return -1;
        }

        /// <summary>True when <paramref name="sendCall"/> falls between lock(bankBalanceLock){ and that block's matching brace.</summary>
        private static bool SendIsInsideBalanceLock(string squashedBody, string sendCall)
        {
            var head = "lock(bankBalanceLock){";
            var open = squashedBody.IndexOf(head, StringComparison.Ordinal);
            if (open < 0)
                return false;

            open += head.Length - 1;
            var close = CloseOf(squashedBody, open);
            var send = squashedBody.IndexOf(Squash(sendCall), StringComparison.Ordinal);
            return close > 0 && send > open && send < close;
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
