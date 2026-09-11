using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the landblock-unloaded-under-a-player defect (stage, 2026-09-09): a player who exited
    /// a dungeon back onto the realm-1 copy of an outdoor landblock arrived about three seconds before that
    /// landblock's own 5 minute idle deadline - a deadline they had set themselves, when they entered the
    /// dungeon five minutes earlier and stopped refreshing it. The landblock heartbeat queued it for
    /// destruction in the same world tick as the arrival, because the idle clock is refreshed only by
    /// Player.Heartbeat (a ~5 second cadence) and never by the object-add path, and Unload() then removed
    /// the player from it: CurrentLandblock null, PhysicsObj destroyed, Location still naming the landblock.
    ///
    /// A Landblock cannot be constructed in this harness (its constructor reads CellLandblock and
    /// LandblockInfo out of the client dat via DatManager and calls DBObj), and this harness can never
    /// build a live Player. So the fix is expressed as a pure static predicate, and this is a direct
    /// behavioural test of it rather than a source-shape tripwire.
    /// </summary>
    [TestClass]
    public class LandblockUnloadGuardTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 9, 23, 32, 19, DateTimeKind.Utc);

        /// <summary>
        /// The regression itself: idle clock long expired, but a player is standing on it.
        /// </summary>
        [TestMethod]
        public void ShouldQueueForUnload_PlayerPresentOnExpiredLandblock_DoesNotQueue()
        {
            var lastActive = Now - Landblock.UnloadInterval - TimeSpan.FromMinutes(1);

            Assert.IsFalse(Landblock.ShouldQueueForUnload(1, lastActive, Now));
        }

        /// <summary>
        /// The exact stage timing: the player left at 23:27:17, re-arrived at 23:32:13.9, and the first
        /// landblock heartbeat past the 23:32:17 deadline ran a couple of seconds later - by which time
        /// they were already standing on the landblock.
        /// </summary>
        [TestMethod]
        public void ShouldQueueForUnload_ArrivalJustInsideTheDeadline_DoesNotQueue()
        {
            var lastActive = new DateTime(2026, 9, 9, 23, 27, 17, DateTimeKind.Utc);

            Assert.IsTrue(lastActive + Landblock.UnloadInterval < Now, "test setup: the idle clock must be expired at Now");
            Assert.IsFalse(Landblock.ShouldQueueForUnload(1, lastActive, Now));
        }

        /// <summary>
        /// More than one player is no different - any occupant blocks the unload.
        /// </summary>
        [TestMethod]
        public void ShouldQueueForUnload_SeveralPlayersPresent_DoesNotQueue()
        {
            var lastActive = Now - Landblock.UnloadInterval - TimeSpan.FromHours(1);

            Assert.IsFalse(Landblock.ShouldQueueForUnload(7, lastActive, Now));
        }

        /// <summary>
        /// The guard must not keep empty landblocks alive - that would leak every landblock a player has
        /// ever visited.
        /// </summary>
        [TestMethod]
        public void ShouldQueueForUnload_EmptyAndExpired_Queues()
        {
            var lastActive = Now - Landblock.UnloadInterval - TimeSpan.FromSeconds(1);

            Assert.IsTrue(Landblock.ShouldQueueForUnload(0, lastActive, Now));
        }

        /// <summary>
        /// An empty landblock inside its idle window is still not a candidate - the deadline is strict,
        /// so exactly UnloadInterval of idleness is not yet enough.
        /// </summary>
        [TestMethod]
        public void ShouldQueueForUnload_EmptyAndNotYetExpired_DoesNotQueue()
        {
            Assert.IsFalse(Landblock.ShouldQueueForUnload(0, Now - Landblock.UnloadInterval, Now));
            Assert.IsFalse(Landblock.ShouldQueueForUnload(0, Now - TimeSpan.FromSeconds(1), Now));
        }

        #region the drain-loop half of the guard

        // ShouldQueueForUnload above is a pure predicate and is tested as one. Its partner
        // TryClaimForDestruction is not reducible to one: its whole decision is instance state - the
        // players list, pendingAdditions, lastActiveTime - on a type this harness cannot construct, since
        // Landblock's constructor reads CellLandblock and LandblockInfo out of the client dat via DatManager
        // and calls DBObj. Extracting "playerCount == 0" from it would test a tautology and leave the part
        // that actually matters uncovered. What matters is the SHAPE the reviewer called out: the refusal
        // has to be honoured by the caller, before the destructive work, or it deregisters a landblock that
        // still holds its objects, its player and its physics cells. So these are source-level tripwires
        // against that shape being lost, brace-scoped to the two methods involved, and they are labelled as
        // tripwires rather than dressed up as behavioural tests.

        private static string FindInSourceTree(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        /// <summary>
        /// Returns the source text of one method body by brace matching, so a file-wide match on an
        /// unrelated occurrence elsewhere in these very large files cannot satisfy an assertion.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find '{signature}' in the source under test");

            var open = source.IndexOf('{', start);
            Assert.IsTrue(open >= 0, $"Could not find the opening brace of '{signature}'");

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"Unbalanced braces while extracting '{signature}'");
            return null;
        }

        private static string LandblockSource()
        {
            return File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "Entity", "Landblock.cs")));
        }

        private static string LandblockManagerSource()
        {
            return File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "Managers", "LandblockManager.cs")));
        }

        /// <summary>
        /// The trap the reviewer named: the claim must gate Unload from the OUTSIDE. If this ever becomes
        /// an early return inside Unload, UnloadLandblocks still runs loadedLandblocks.Remove, the group and
        /// adjacency bookkeeping and LandblockDictCommit(null) on a landblock that was never torn down.
        /// </summary>
        [TestMethod]
        public void UnloadLandblocks_RefusesTheClaimBeforeUnloading()
        {
            var body = ExtractMethodBody(LandblockManagerSource(), "private static void UnloadLandblocks()");

            var claim = body.IndexOf("TryClaimForDestruction()", StringComparison.Ordinal);
            var unload = body.IndexOf("landblock.Unload()", StringComparison.Ordinal);

            Assert.IsTrue(claim >= 0, "UnloadLandblocks no longer re-checks TryClaimForDestruction before tearing a landblock down");
            Assert.IsTrue(unload >= 0, "UnloadLandblocks no longer calls landblock.Unload()");
            Assert.IsTrue(claim < unload, "the claim must be taken BEFORE Unload(), not after it");

            var skip = body.IndexOf("continue;", claim, StringComparison.Ordinal);

            Assert.IsTrue(skip >= 0 && skip < unload, "a refused claim must skip this landblock entirely, leaving it registered");
        }

        /// <summary>
        /// The claim must return a value the caller can honour, and must count players only after the
        /// pending-addition staging has been flushed - a player who arrived during this tick is in
        /// pendingAdditions, not in players.
        /// </summary>
        [TestMethod]
        public void TryClaimForDestruction_ReturnsBoolAndFlushesPendingBeforeCounting()
        {
            var source = LandblockSource();

            Assert.IsTrue(source.Contains("public bool TryClaimForDestruction()"),
                "TryClaimForDestruction must return bool - a void version cannot stop UnloadLandblocks deregistering the landblock");

            var body = ExtractMethodBody(source, "public bool TryClaimForDestruction()");

            var flush = body.IndexOf("ProcessPendingWorldObjectAdditionsAndRemovals()", StringComparison.Ordinal);
            var count = body.IndexOf("players.Count", StringComparison.Ordinal);

            Assert.IsTrue(flush >= 0, "TryClaimForDestruction must flush pending additions before counting players");
            Assert.IsTrue(count >= 0, "TryClaimForDestruction no longer checks players.Count");
            Assert.IsTrue(flush < count, "the flush must happen BEFORE players.Count is read");
        }

        /// <summary>
        /// Shutdown must stay able to tear down unconditionally: ServerManager blocks until every landblock
        /// is gone, so a refusal that could reach that path would hang the shutdown instead of finishing it.
        /// </summary>
        [TestMethod]
        public void ShutdownTeardown_IsExemptFromTheClaim()
        {
            var source = LandblockManagerSource();
            var body = ExtractMethodBody(source, "public static void AddAllActiveLandblocksToDestructionQueue()");

            Assert.IsTrue(body.Contains("unloadingForShutdown = true"),
                "the shutdown teardown must mark itself, or the arrived-since-queued refusal can hang ServerManager's unload wait");

            var drain = ExtractMethodBody(source, "private static void UnloadLandblocks()");

            Assert.IsTrue(drain.Contains("unloadingForShutdown"),
                "UnloadLandblocks must honour the shutdown exemption");
        }

        #endregion
    }
}
