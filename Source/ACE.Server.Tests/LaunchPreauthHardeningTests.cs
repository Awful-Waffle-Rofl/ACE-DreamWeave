using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common.Extensions;
using ACE.Database.Models.Auth;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Launch-night pre-auth hardening: the client string length clamp (BinaryReaderExtensions), the
    /// one-LoginRequest-per-session guard (Session.TryBeginLoginRequest), the market login ban check
    /// (MarketAccountAuth) and the /facet cooldown (FacetCommands.TryStartFacetCommandCore).
    /// </summary>
    [TestClass]
    public class LaunchPreauthHardeningTests
    {
        // ---- helpers ------------------------------------------------------------------------------

        private static BinaryReader Reader(byte[] bytes) => new BinaryReader(new MemoryStream(bytes, 0, bytes.Length, false, true));

        /// <summary>A String32L as the login packet carries it: DWORD data length, packed-word prefix, chars. No padding appended.</summary>
        private static byte[] String32L(uint dataLength, byte[] prefix, string chars)
        {
            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(dataLength), 0, 4);
            ms.Write(prefix, 0, prefix.Length);
            var c = Encoding.ASCII.GetBytes(chars);
            ms.Write(c, 0, c.Length);
            return ms.ToArray();
        }

        private static byte[] String16L(ushort length, string chars)
        {
            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(length), 0, 2);
            var c = Encoding.ASCII.GetBytes(chars);
            ms.Write(c, 0, c.Length);
            return ms.ToArray();
        }

        private static string RepoFile(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, Path.Combine(relative)));
        }

        /// <summary>The body of one method, by brace balance from its signature; fails loudly if the signature moved.</summary>
        private static string MethodBody(string text, string signature)
        {
            var start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"'{signature}' was not found - update this test's anchor.");

            var open = text.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0)
                    return text.Substring(open, i - open + 1);
            }

            Assert.Fail($"No closing brace for '{signature}'.");
            return null;
        }

        // ---- 1. string length clamp -----------------------------------------------------------------

        [TestMethod]
        public void ReadString32L_LengthEqualToRemaining_ReadsTheString()
        {
            // data length 4 = 1 prefix byte + "abc"; after the prefix exactly 3 bytes remain.
            var reader = Reader(String32L(4, new byte[] { 3 }, "abc"));

            Assert.AreEqual("abc", reader.ReadString32L());
        }

        [TestMethod]
        public void ReadString32L_LengthOnePastRemaining_ThrowsEndOfStream()
        {
            // data length 5 claims 4 chars after the prefix, but only 3 bytes remain.
            var reader = Reader(String32L(5, new byte[] { 4 }, "abc"));

            Assert.ThrowsExactly<EndOfStreamException>(() => reader.ReadString32L());
        }

        [TestMethod]
        public void ReadString32L_TwoBytePrefixOver255_ReadsTheString()
        {
            // 300 chars: data length 302 = 2-byte packed-word prefix + 300; the >255 path skips the second prefix byte.
            var text = new string('x', 300);
            var reader = Reader(String32L(302, new byte[] { 0x81, 0x2C }, text));

            Assert.AreEqual(text, reader.ReadString32L());
        }

        [TestMethod]
        public void ReadString32L_PaddingStillSkipped_NextFieldReadsCorrectly()
        {
            // "abc" with a 1-prefix: 4 + 3 = 7 bytes, padded to 8, then a sentinel uint.
            var bytes = String32L(4, new byte[] { 3 }, "abc").Concat(new byte[] { 0 }).Concat(BitConverter.GetBytes(0xDEADBEEFu)).ToArray();
            var reader = Reader(bytes);

            Assert.AreEqual("abc", reader.ReadString32L());
            Assert.AreEqual(0xDEADBEEFu, reader.ReadUInt32());
        }

        [TestMethod]
        public void ReadString32L_ZeroLength_ReturnsEmpty()
        {
            Assert.AreEqual("", Reader(BitConverter.GetBytes(0u)).ReadString32L());
        }

        [TestMethod]
        public void ReadString32L_HugeLengthOnTinyStream_ThrowsWithoutLargeAllocation()
        {
            var reader = Reader(String32L(0x40000000, new byte[] { 1 }, "a"));

            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.ThrowsExactly<EndOfStreamException>(() => reader.ReadString32L());
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.IsTrue(allocated < 1024 * 1024, $"Refusing a 0x40000000 length allocated {allocated} bytes; it must refuse before ReadChars allocates.");
        }

        [TestMethod]
        public void ReadString32L_LengthAboveIntMax_ThrowsEndOfStream()
        {
            // A uint above int.MaxValue used to reach ReadChars as a negative int.
            var reader = Reader(String32L(0xFFFFFFFF, new byte[] { 1, 1 }, "a"));

            Assert.ThrowsExactly<EndOfStreamException>(() => reader.ReadString32L());
        }

        [TestMethod]
        public void ReadString16L_LengthEqualToRemaining_ReadsTheString()
        {
            Assert.AreEqual("1802", Reader(String16L(4, "1802")).ReadString16L());
        }

        [TestMethod]
        public void ReadString16L_LengthOnePastRemaining_ThrowsEndOfStream()
        {
            Assert.ThrowsExactly<EndOfStreamException>(() => Reader(String16L(5, "1802")).ReadString16L());
        }

        [TestMethod]
        public void ReadString16L_MaxLengthOnTinyStream_ThrowsEndOfStream()
        {
            Assert.ThrowsExactly<EndOfStreamException>(() => Reader(String16L(0xFFFF, "a")).ReadString16L());
        }

        [TestMethod]
        public void ReadString16L_ZeroLength_ReturnsEmptyAndSkipsPadding()
        {
            // 2-byte length + 2 pad bytes, then a sentinel.
            var bytes = String16L(0, "").Concat(new byte[] { 0, 0 }).Concat(BitConverter.GetBytes(0xCAFEF00Du)).ToArray();
            var reader = Reader(bytes);

            Assert.AreEqual("", reader.ReadString16L());
            Assert.AreEqual(0xCAFEF00Du, reader.ReadUInt32());
        }

        // ---- 2. one LoginRequest per session --------------------------------------------------------

        [TestMethod]
        public void TryBeginLoginRequest_FirstTrue_EveryLaterCallFalse()
        {
            var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));

            Assert.IsTrue(session.TryBeginLoginRequest());
            Assert.IsFalse(session.TryBeginLoginRequest());
            Assert.IsFalse(session.TryBeginLoginRequest());
        }

        [TestMethod]
        public void TryBeginLoginRequest_Concurrent_ExactlyOneWinner()
        {
            for (var round = 0; round < 50; round++)
            {
                var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
                var winners = 0;

                using (var gate = new ManualResetEventSlim(false))
                {
                    var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                    {
                        gate.Wait();
                        if (session.TryBeginLoginRequest())
                            Interlocked.Increment(ref winners);
                    })).ToArray();

                    gate.Set();
                    Task.WaitAll(tasks);
                }

                Assert.AreEqual(1, winners, $"round {round}");
            }
        }

        [TestMethod]
        public void HandleLoginRequest_GuardRunsBeforeParseAndTask()
        {
            var body = MethodBody(RepoFile("Source", "ACE.Server", "Network", "Handlers", "AuthenticationHandler.cs"), "public static void HandleLoginRequest(");

            var guard = body.IndexOf("session.TryBeginLoginRequest()", StringComparison.Ordinal);
            var parse = body.IndexOf("new PacketInboundLoginRequest(", StringComparison.Ordinal);
            var task = body.IndexOf("new Task(", StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "HandleLoginRequest must call session.TryBeginLoginRequest().");
            Assert.IsTrue(parse > guard && task > guard, "The guard must run before the packet is parsed and before the login Task starts.");
        }

        // ---- 3. market login ban check --------------------------------------------------------------

        private static readonly DateTime Now = new DateTime(2026, 10, 2, 20, 0, 0, DateTimeKind.Utc);

        private static Account MarketAccount(DateTime? banExpire) => new Account { AccountId = 42, AccountName = "tester", BanExpireTime = banExpire };

        private static bool RightPassword(Account a, string p) => p == "right";

        [TestMethod]
        public void MarketLogin_BannedNow_Rejected()
        {
            Assert.AreEqual(0u, MarketAccountAuth.ResolveLogin(MarketAccount(Now.AddDays(1)), "right", Now, RightPassword));
        }

        [TestMethod]
        public void MarketLogin_BanExpired_Accepted()
        {
            Assert.AreEqual(42u, MarketAccountAuth.ResolveLogin(MarketAccount(Now.AddSeconds(-1)), "right", Now, RightPassword));
        }

        [TestMethod]
        public void MarketLogin_BanExpiringExactlyNow_Accepted()
        {
            // AdminAuthorizer's original rule was "expiry > now", so an expiry equal to now is not banned.
            Assert.AreEqual(42u, MarketAccountAuth.ResolveLogin(MarketAccount(Now), "right", Now, RightPassword));
        }

        [TestMethod]
        public void MarketLogin_NeverBanned_Accepted()
        {
            Assert.AreEqual(42u, MarketAccountAuth.ResolveLogin(MarketAccount(null), "right", Now, RightPassword));
        }

        [TestMethod]
        public void MarketLogin_WrongPasswordOrUnknownAccount_Rejected()
        {
            Assert.AreEqual(0u, MarketAccountAuth.ResolveLogin(MarketAccount(null), "wrong", Now, RightPassword));
            Assert.AreEqual(0u, MarketAccountAuth.ResolveLogin(null, "right", Now, RightPassword));
        }

        [TestMethod]
        public void MarketLogin_BannedAccount_StillPaysThePasswordCheck()
        {
            var calls = 0;
            MarketAccountAuth.ResolveLogin(MarketAccount(Now.AddDays(1)), "right", Now, (a, p) => { calls++; return true; });

            Assert.AreEqual(1, calls, "The ban is checked after the password, so a banned account's response time matches any other.");
        }

        [TestMethod]
        public void AdminAuthorizer_UsesTheSharedBanRule()
        {
            var admin = new Account { AccountId = 9, AccountName = "admin", AccessLevel = (uint)ACE.Entity.Enum.AccessLevel.Admin };

            admin.BanExpireTime = Now.AddDays(1);
            Assert.AreEqual(AdminCheck.NotAdmin, new AdminAuthorizer(_ => admin, () => Now).Check(9, out _));

            admin.BanExpireTime = Now.AddDays(-1);
            Assert.AreEqual(AdminCheck.Admin, new AdminAuthorizer(_ => admin, () => Now).Check(9, out _));

            var body = MethodBody(RepoFile("Source", "ACE.Server", "Managers", "Market", "AdminAuthorizer.cs"), "public AdminCheck Check(");
            StringAssert.Contains(body, "MarketAccountAuth.IsBanned(", "AdminAuthorizer must use the shared ban rule so the two cannot drift.");
        }

        [TestMethod]
        public void MarketLogin_ProgramWiresTheSharedHelper()
        {
            var program = RepoFile("Source", "ACE.Server", "Program.cs");
            var i = program.IndexOf("AuthenticateAccount = (name, password) =>", StringComparison.Ordinal);
            Assert.IsTrue(i >= 0, "AuthenticateAccount delegate not found - update this test's anchor.");

            var window = program.Substring(i, Math.Min(600, program.Length - i));
            StringAssert.Contains(window, "MarketAccountAuth.ResolveLogin(", "The market login must go through the ban-checking helper.");
        }

        // ---- 4. /facet cooldown -----------------------------------------------------------------------

        [TestMethod]
        public void FacetCooldown_SecondCallInsideWindowRefused_AfterWindowAllowed()
        {
            // Safe uninitialized: TryStartFacetCommandCore touches only PrevFacetCommand, a plain field.
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            Assert.IsTrue(FacetCommands.TryStartFacetCommandCore(player, Now, out var r1));
            Assert.IsNull(r1);

            Assert.IsFalse(FacetCommands.TryStartFacetCommandCore(player, Now.AddSeconds(4.9), out var r2));
            Assert.AreEqual("You must wait a few seconds before using /facet again.", r2);

            // The refused call did not restamp, so the window still ends 5 s after the FIRST call.
            Assert.IsTrue(FacetCommands.TryStartFacetCommandCore(player, Now.AddSeconds(5), out _));
        }

        [TestMethod]
        public void FacetCooldown_GateRunsBeforeEverySubcommand()
        {
            var body = MethodBody(RepoFile("Source", "ACE.Server", "Command", "Handlers", "FacetCommands.cs"), "public static void HandleFacet(");

            var gate = body.IndexOf("TryStartFacetCommandCore(player, DateTime.UtcNow", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0, "HandleFacet must call TryStartFacetCommandCore.");

            foreach (var dispatch in new[] { "HandleList(player)", "HandleName(player", "ComposeHelp(", "HandleSwitch(session" })
            {
                var at = body.IndexOf(dispatch, StringComparison.Ordinal);
                Assert.IsTrue(at > gate, $"'{dispatch}' must come after the cooldown gate.");
            }
        }
    }
}
