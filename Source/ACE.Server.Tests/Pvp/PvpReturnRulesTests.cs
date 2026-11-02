using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Server.Pvp;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The PvP return (Docs/Pvp/BATTLEGROUNDS.md "Pulled from a Thread"): the exit taken at dispatch (PvpReturnRules.ChooseExit), the one
    /// return decision (PvpReturnRules.Decide), and a source pin that every path sending a player home from a match goes through the one
    /// resolver (Player.ResolvePvpReturn) and none reads EphemeralRealmExitTo itself. Pure; no PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class PvpReturnRulesTests
    {
        private static readonly uint ThreadInstance = Position.InstanceIDFromVars(1, 0x0301, isTemporaryRuleset: true);

        private static readonly Position World = new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0);
        private static readonly Position Outer = new Position(0x7D640013, 10f, 20f, 0f, 0f, 0f, 0f, 1f, 0);
        private static readonly Position RunExit = new Position(0x8B340021, 1f, 2f, 0f, 0f, 0f, 0f, 1f, 0);
        private static readonly Position Sanctuary = new Position(0xC6A90009, 3f, 4f, 0f, 0f, 0f, 0f, 1f, 0);
        private static readonly Position Instantiation = new Position(0xD3380028, 5f, 6f, 0f, 0f, 0f, 0f, 1f, 0);
        private static readonly Position SeatExit = new Position(0x12340001, 7f, 8f, 0f, 0f, 0f, 0f, 1f, 0);
        private static readonly Position InThread = new Position(0x01500100, 10f, 20f, 0f, 0f, 0f, 0f, 1f, ThreadInstance);
        private static readonly Position OtherEphemeral = new Position(0x01600100, 1f, 1f, 0f, 0f, 0f, 0f, 1f, Position.InstanceIDFromVars(1, 0x0302, isTemporaryRuleset: true));

        private static void Same(Position expected, Position actual, string what)
        {
            Assert.IsNotNull(actual, what);
            Assert.AreEqual(expected.LandblockId.Raw, actual.LandblockId.Raw, what);
            Assert.AreEqual(expected.Instance, actual.Instance, what);
            Assert.AreEqual(expected.PositionX, actual.PositionX, what);
        }

        [TestMethod]
        public void Fixture_TheThreadPositionsAreEphemeral()
        {
            Assert.IsTrue(InThread.IsEphemeralRealm);
            Assert.IsTrue(OtherEphemeral.IsEphemeralRealm);
            Assert.IsFalse(World.IsEphemeralRealm);
        }

        // ================= ChooseExit (dispatch) =================

        [TestMethod]
        public void ChooseExit_InTheWorld_ExitsWhereTheyStand()
        {
            var c = PvpReturnRules.ChooseExit(World, Outer, false, null, Sanctuary, Instantiation);

            Same(World, c.Exit, "where they stand");
            Assert.IsFalse(c.RegisterThreadAway);
        }

        /// <summary>A roster member inside a Thread exits to the Thread's outer stamp (never the copy itself) and is recorded as away.</summary>
        [TestMethod]
        public void ChooseExit_InAThreadAsAMember_ExitsToTheOuterStamp_AndRegisters()
        {
            var c = PvpReturnRules.ChooseExit(InThread, Outer, true, RunExit, Sanctuary, Instantiation);

            Same(Outer, c.Exit, "the outer stamp");
            Assert.IsTrue(c.RegisterThreadAway);
        }

        [TestMethod]
        public void ChooseExit_InAThread_WithoutAStamp_FallsToTheRunsExitThenHome()
        {
            Same(RunExit, PvpReturnRules.ChooseExit(InThread, null, true, RunExit, Sanctuary, Instantiation).Exit, "the run's own exit");
            Same(Sanctuary, PvpReturnRules.ChooseExit(InThread, null, true, null, Sanctuary, Instantiation).Exit, "the lifestone");
            Same(Instantiation, PvpReturnRules.ChooseExit(InThread, OtherEphemeral, true, null, null, Instantiation).Exit, "an ephemeral stamp is skipped");
        }

        /// <summary>Another ephemeral copy (not a Thread they belong to): its stamp, else home; not registered.</summary>
        [TestMethod]
        public void ChooseExit_InAnotherEphemeralCopy_StampElseHome_NotRegistered()
        {
            var c = PvpReturnRules.ChooseExit(OtherEphemeral, Outer, false, RunExit, Sanctuary, Instantiation);
            Same(Outer, c.Exit, "the stamp");
            Assert.IsFalse(c.RegisterThreadAway);

            Same(Sanctuary, PvpReturnRules.ChooseExit(OtherEphemeral, null, false, RunExit, Sanctuary, Instantiation).Exit, "home, never the run exit");
        }

        [TestMethod]
        public void ChooseExit_NothingElse_WhereTheyStand_AndOfflineIsNull()
        {
            Same(InThread, PvpReturnRules.ChooseExit(InThread, null, true, null, null, null).Exit, "the last resort");
            Assert.IsNull(PvpReturnRules.ChooseExit(null, Outer, false, null, Sanctuary, Instantiation).Exit);
        }

        // ================= Decide (every return) =================

        /// <summary>
        /// The decision table, in priority order: a valid Thread entry goes back inside and keeps the stamp; else a usable stamp; else the
        /// seat's exit (when not ephemeral); else home when there was a stamp or home is allowed; else nothing. Every outcome but the
        /// Thread return clears the stamp, and releases the entry when there was one.
        /// </summary>
        [DataTestMethod]
        // entry, valid, stamp, usable, seatExit, homeFallback -> kind, destination key
        [DataRow(true, true, true, true, true, true, PvpReturnKind.ThreadInside, "inside", DisplayName = "valid Thread entry: back inside")]
        [DataRow(true, false, true, true, true, true, PvpReturnKind.Stamp, "outer", DisplayName = "invalid Thread entry: the stamp")]
        [DataRow(false, false, true, true, true, true, PvpReturnKind.Stamp, "outer", DisplayName = "no entry: the stamp")]
        [DataRow(false, false, true, false, true, true, PvpReturnKind.SeatExit, "seat", DisplayName = "unusable stamp: the seat exit")]
        [DataRow(false, false, false, false, true, true, PvpReturnKind.SeatExit, "seat", DisplayName = "no stamp: the seat exit")]
        [DataRow(false, false, true, false, false, false, PvpReturnKind.Home, "sanctuary", DisplayName = "unusable stamp, no seat exit, no home fallback: still home")]
        [DataRow(false, false, false, false, false, true, PvpReturnKind.Home, "sanctuary", DisplayName = "nothing, home allowed: home")]
        [DataRow(false, false, false, false, false, false, PvpReturnKind.None, null, DisplayName = "nothing, login path: none")]
        [DataRow(true, false, false, false, false, false, PvpReturnKind.None, null, DisplayName = "invalid entry only, login path: none")]
        public void Decide_Table(bool entry, bool valid, bool stamp, bool usable, bool seat, bool homeFallback, PvpReturnKind kind, string destination)
        {
            var d = PvpReturnRules.Decide(entry, valid, entry ? InThread : null, stamp ? Outer : null, usable, seat ? SeatExit : null, Sanctuary, Instantiation, homeFallback);

            Assert.AreEqual(kind, d.Kind);

            var expected = destination switch
            {
                "inside" => InThread,
                "outer" => Outer,
                "seat" => SeatExit,
                "sanctuary" => Sanctuary,
                _ => null,
            };

            if (expected == null)
                Assert.IsNull(d.Destination);
            else
                Same(expected, d.Destination, kind.ToString());

            Assert.AreEqual(kind == PvpReturnKind.ThreadInside, d.KeepStamp, "only the Thread return keeps the outer stamp");
            Assert.AreEqual(entry && kind != PvpReturnKind.ThreadInside, d.ReleaseThreadEntry, "an entry not used is released");
        }

        [TestMethod]
        public void Decide_AnEphemeralSeatExit_IsNeverUsed_AndHomeFallsToInstantiation()
        {
            var d = PvpReturnRules.Decide(false, false, null, null, false, OtherEphemeral, null, Instantiation, true);

            Assert.AreEqual(PvpReturnKind.Home, d.Kind);
            Same(Instantiation, d.Destination, "no lifestone: the instantiation point");
        }

        // ================= one resolver, four callers =================

        private const string Adapters = "Source/ACE.Server/Pvp/PvpLiveAdapters.cs";
        private const string Death = "Source/ACE.Server/WorldObjects/Player_Death.cs";
        private const string Arena = "Source/ACE.Server/WorldObjects/Player_PvpArena.cs";
        private const string Login = "Source/ACE.Server/Managers/WorldManager.cs";

        /// <summary>The four return paths, each sliced to the PvP return itself.</summary>
        private static (string Name, string Text)[] ReturnSites()
        {
            var deathBody = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Death), "public void ThreadSafeTeleportOnDeath()");
            var pvpBranch = PooledLootSourceText.MethodBody(deathBody, "else if (PvpMatchDeathInProgress)");

            return new[]
            {
                ("match exit (LivePvpPlayerGateway.ReturnToExit)", PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Adapters), "public void ReturnToExit(uint characterId, Position exitTo, uint matchInstance)")),
                ("match death (ThreadSafeTeleportOnDeath)", pvpBranch),
                ("battleground pen (ReturnFromBattlegroundPenNow)", PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Arena), "internal void ReturnFromBattlegroundPenNow(string why)")),
                ("login relocation (DoPlayerEnterWorld_Inner)", PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Login), "if (validatedLocation.Instance != session.Player.Location.Instance)")),
            };
        }

        [TestMethod]
        public void EveryReturnSite_CallsTheResolver_AndNeverReadsTheStamp()
        {
            foreach (var (name, text) in ReturnSites())
            {
                Assert.IsTrue(text.Contains("ResolvePvpReturn(", StringComparison.Ordinal), $"{name} must call ResolvePvpReturn");
                Assert.IsFalse(Regex.IsMatch(text, @"GetPosition\(\s*PositionType\.EphemeralRealmExitTo\s*\)"), $"{name} must not read EphemeralRealmExitTo itself");
            }
        }

        /// <summary>
        /// The login path (it needs a live world, so it is pinned against the source): the relocation goes through the resolver with the
        /// held-seat flag, and only after the player is in the world is the Reconnected intent reported, for a held seat only.
        /// </summary>
        [TestMethod]
        public void LoginPath_RelocatesThroughTheResolver_ThenReportsTheRejoin()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Login), "private static void DoPlayerEnterWorld_Inner(");

            var held = body.IndexOf("var heldForRejoin = ACE.Server.Pvp.PvpMatchManager.GracedMatchOf(session.Player.Guid.Full).HasValue;", StringComparison.Ordinal);
            var resolve = body.IndexOf("session.Player.ResolvePvpReturn(\"login relocation\", keepAwayEntry: heldForRejoin, homeFallback: false)", StringComparison.Ordinal);
            var add = body.IndexOf("var success = LandblockManager.AddObject(session.Player, true);", StringComparison.Ordinal);
            var report = body.IndexOf("if (success && ACE.Server.Pvp.PvpMatchManager.GracedMatchOf(session.Player.Guid.Full) is Guid rejoinMatch)", StringComparison.Ordinal);
            var reconnected = body.IndexOf("PvpMatchManager.Reconnected(session.Player.Guid.Full, rejoinMatch,", StringComparison.Ordinal);

            Assert.IsTrue(held >= 0 && held < resolve, "the held flag is read before the resolver runs");
            Assert.IsTrue(resolve < add, "the relocation happens before the player enters the world");
            Assert.IsTrue(add < report && report < reconnected, "the rejoin is reported only once the player is in the world");
        }

        /// <summary>The dispatch exit always goes through BeginPull (a new pull supersedes), never a bare Register.</summary>
        [TestMethod]
        public void GetExitPoint_AlwaysSupersedesThroughBeginPull()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Adapters), "public Position GetExitPoint(uint characterId)");

            Assert.IsTrue(body.Contains("ThreadPvpAway.BeginPull(", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("ThreadPvpAway.Register(", StringComparison.Ordinal));
            Assert.IsTrue(body.IndexOf("BeginPull(", StringComparison.Ordinal) < body.IndexOf("return choice.Exit;", StringComparison.Ordinal),
                "on every path that returns an exit");
        }

        /// <summary>Exactly four call sites in the server, so a fifth return path cannot appear without this pin being revisited.</summary>
        [TestMethod]
        public void TheResolver_HasExactlyFourCallers()
        {
            var root = FindServerSource();

            var calls = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => File.ReadAllLines(f).Select(l => (File: Path.GetFileName(f), Line: l.Trim())))
                .Where(x => x.Line.Contains("ResolvePvpReturn(", StringComparison.Ordinal)
                    && !x.Line.StartsWith("//", StringComparison.Ordinal)
                    && !x.Line.StartsWith("internal Position ResolvePvpReturn(", StringComparison.Ordinal))
                .ToList();

            Assert.AreEqual(4, calls.Count, "callers: " + string.Join(" | ", calls.Select(c => $"{c.File}: {c.Line}")));
        }

        private static string FindServerSource()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");

                if (File.Exists(Path.Combine(candidate, "ACE.Server.csproj")))
                    return candidate;
            }

            Assert.Fail($"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
