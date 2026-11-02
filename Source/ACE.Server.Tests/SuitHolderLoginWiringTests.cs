using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-text pins for the login half of SuitHolderMoveGuard, which no test here can drive (each site needs a live
    /// Session and the world loop): CharacterEnterWorld must ask the guard BEFORE it queues the login read, and
    /// WorldManager.PlayerEnterWorld must end the entering mark on its early return and in a finally around
    /// DoPlayerEnterWorld. Also pins the world-heartbeat sweep and its wiring. Same machinery as PvpArenaWiringTests:
    /// comments stripped (WeaponModSuppressionWiringTests.StripComments), each pin searched inside its own method's
    /// brace-matched body, whitespace ignored on both sides.
    /// </summary>
    [TestClass]
    public class SuitHolderLoginWiringTests
    {
        private const string CharacterHandler = "Source/ACE.Server/Network/Handlers/CharacterHandler.cs";
        private const string WorldManager = "Source/ACE.Server/Managers/WorldManager.cs";
        private const string Program = "Source/ACE.Server/Program.cs";

        [TestMethod]
        public void CharacterEnterWorld_AsksTheGuard_BeforeItQueuesTheLoginRead_AndRefusesWithAReturn()
        {
            var body = Squash(MethodBody(CharacterHandler, "public static void CharacterEnterWorld(ClientMessage message, Session session)"));

            var guard = body.IndexOf(Squash(
                "if (!ACE.Server.Entity.AccountVault.SuitHolderMoveGuard.TryBeginEnterWorld(guid)) { session.SendCharacterError(CharacterError.EnterGameCharacterInWorld); return; }"),
                StringComparison.Ordinal);
            var state = body.IndexOf(Squash("session.State = SessionState.WorldConnected;"), StringComparison.Ordinal);
            var enter = body.IndexOf(Squash("WorldManager.PlayerEnterWorld(session, character);"), StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0, "CharacterEnterWorld must refuse (and return) when SuitHolderMoveGuard.TryBeginEnterWorld(guid) is false");
            Assert.IsTrue(enter > guard, "the guard must run BEFORE WorldManager.PlayerEnterWorld queues the login read");
            Assert.IsTrue(state > guard, "the guard must run before the session is marked WorldConnected");
        }

        [TestMethod]
        public void PlayerEnterWorld_EndsTheEnteringMark_OnTheEarlyReturn_AndInAFinally()
        {
            var body = Squash(MethodBody(WorldManager, "public static void PlayerEnterWorld(Session session, Character character)"));

            var nullBranch = body.IndexOf(Squash("if (offlinePlayer == null) {"), StringComparison.Ordinal);
            Assert.IsTrue(nullBranch >= 0, "the offlinePlayer == null early return is missing");

            var branchReturn = body.IndexOf("return;", nullBranch, StringComparison.Ordinal);
            var branch = body.Substring(nullBranch, branchReturn - nullBranch);

            Assert.IsTrue(branch.Contains(Squash("ACE.Server.Entity.AccountVault.SuitHolderMoveGuard.EndEnterWorld(character.Id);"), StringComparison.Ordinal),
                "the offlinePlayer == null early return must end the entering mark before it returns");

            Assert.IsTrue(body.Contains(Squash(
                "try { DoPlayerEnterWorld(session, character, offlinePlayer.Biota, biotas); } finally { ACE.Server.Entity.AccountVault.SuitHolderMoveGuard.EndEnterWorld(character.Id); }"),
                StringComparison.Ordinal),
                "DoPlayerEnterWorld must be wrapped in a finally that ends the entering mark");
        }

        /// <summary>
        /// The sweep is the only thing that closes a unit whose character's action queue stopped, so its call must be
        /// UNCONDITIONAL on every heartbeat that runs: the call is the sole statement of its own try, and between the
        /// method's one rate-limit early return (pinned exactly) and that try there is no return, if, goto or loop.
        /// </summary>
        [TestMethod]
        public void TheWorldHeartbeat_RunsTheTransferSweep_Unconditionally_AndProgramWiresTheLiveService()
        {
            var world = Squash(MethodBody(WorldManager, "public static bool UpdateGameWorld()"));

            var opening = Squash("{ if (updateGameWorldRateLimiter.GetSecondsToWaitBeforeNextEvent() > 0) return false;");
            Assert.IsTrue(world.StartsWith(opening, StringComparison.Ordinal),
                "UpdateGameWorld must open with exactly its rate-limit early return; anything else before the sweep must be re-reviewed");

            var sweepTry = world.IndexOf(Squash("try { ACE.Server.Entity.AccountVault.SuitTransferService.SweepLive(); } catch (Exception"), StringComparison.Ordinal);
            Assert.IsTrue(sweepTry > 0, "SweepLive() must be the sole statement of its own try, so nothing in the try can skip it");
            Assert.AreEqual(world.IndexOf("SweepLive();", StringComparison.Ordinal), world.LastIndexOf("SweepLive();", StringComparison.Ordinal), "exactly one sweep call");

            var between = world.Substring(opening.Length, sweepTry - opening.Length);

            foreach (var skip in new[] { "return", "if(", "goto", "while(", "for(", "foreach(", "switch(" })
                Assert.IsFalse(between.Contains(skip, StringComparison.Ordinal), $"'{skip}' between the rate limit and the sweep could skip the sweep on a heartbeat");

            var program = Squash(LiveSource(Program));
            Assert.IsTrue(program.Contains(Squash("ACE.Server.Entity.AccountVault.SuitTransferService.Live = apiOptions.SuitTransfers;"), StringComparison.Ordinal),
                "Program.cs must hand the transfer service to SuitTransferService.Live, or the sweep has nothing to sweep");
        }

        // ---- machinery (PvpArenaWiringTests') ----

        private static string LiveSource(string relativePath)
        {
            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            return WeaponModSuppressionWiringTests.StripComments(File.ReadAllText(path));
        }

        private static string MethodBody(string relativePath, string signature)
        {
            var source = LiveSource(relativePath);
            var at = source.IndexOf(signature, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, $"{relativePath}: declaration not found in live code: {signature}");
            Assert.AreEqual(-1, source.IndexOf(signature, at + signature.Length, StringComparison.Ordinal), $"{relativePath}: declaration appears more than once: {signature}");

            var open = source.IndexOf('{', at + signature.Length);
            Assert.IsTrue(open >= 0, $"{relativePath}: no body after {signature}");

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"{relativePath}: unbalanced braces after {signature}");
            return null;
        }

        private static string Squash(string text)
        {
            var sb = new StringBuilder(text.Length);

            foreach (var c in text)
            {
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }

            return sb.ToString();
        }

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
