using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Entity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.TeleportFlush
{
    /// <summary>
    /// Structural invariants of the teleport flush switch that no unit test can reach at runtime, because no test in
    /// this project can build a live Player or Session. Each check binds the method it guards: the text must appear
    /// INSIDE the named method's body, so moving or deleting it fails the test. Each matcher is also run against a
    /// copy with the guarded line removed, to prove it discriminates.
    ///
    /// The most important one is the default arm: teleport_flush_scope 1 with pacing off must run the legacy flush
    /// unchanged, so the client sees byte-for-byte what it saw before this switch existed.
    /// </summary>
    [TestClass]
    public class TeleportFlushSourceInvariantTests
    {
        private static string ReadServerSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "repo root (a directory containing Source/ACE.Server/WorldObjects) not found above the test output dir");

            var path = Path.Combine(dir.FullName, "Source", relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"expected source file not found: {path}");

            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        /// <summary>
        /// The body of the method whose declaration contains <paramref name="signature"/> (the text from its opening
        /// brace to the matching close). Fails when the signature is missing or appears more than once.
        /// </summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"method not found: {signature}");
            Assert.AreEqual(-1, source.IndexOf(signature, at + signature.Length, StringComparison.Ordinal), $"signature is ambiguous: {signature}");

            var open = source.IndexOf('{', at);
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

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }

        /// <summary>True when every needle appears in <paramref name="body"/>, in the given order.</summary>
        private static bool InOrder(string body, params string[] needles)
        {
            var from = 0;

            foreach (var needle in needles)
            {
                var at = body.IndexOf(needle, from, StringComparison.Ordinal);
                if (at < 0)
                    return false;

                from = at + needle.Length;
            }

            return true;
        }

        /// <summary>Whitespace and braces stripped: compares statement sequences regardless of formatting.</summary>
        private static string Squash(string text)
        {
            return Regex.Replace(text, @"[\s{}]", "");
        }

        private static readonly string[] EnqueueHook =
        {
            "if (message.Opcode == GameMessageOpcode.ObjectCreate)",
            "Interlocked.Increment(ref createObjectsEnqueued);",
            "NoteDistinctCreate(message);",
            "var book = ClientObjectBook.Follows(message) ? session.Player?.ClientObjectBook : null;",
            "if (book != null)",
            "ClientObjectBook.EnqueueFollowed(book, message, enqueueIntoBundle, noteBookFailure);",
            "else",
            "EnqueueIntoBundle(message);",
        };

        private static string HookTail(string body)
        {
            var at = body.IndexOf("if (message.Opcode == GameMessageOpcode.ObjectCreate)", StringComparison.Ordinal);
            return at < 0 ? "" : body.Substring(at);
        }

        [TestMethod]
        public void BothEnqueueSendLoops_RunTheIdenticalBookkeepingHook()
        {
            var source = ReadServerSource("ACE.Server/Network/NetworkSession.cs");

            var paramsBody = MethodBody(source, "public void EnqueueSend(params GameMessage[] messages)");
            var enumerableBody = MethodBody(source, "public void EnqueueSend(IEnumerable<GameMessage> messages)");

            Assert.IsTrue(InOrder(paramsBody, EnqueueHook), "params EnqueueSend lost or reordered the hook");
            Assert.IsTrue(InOrder(enumerableBody, EnqueueHook), "IEnumerable EnqueueSend lost or reordered the hook");

            Assert.AreEqual(HookTail(paramsBody), HookTail(enumerableBody), "the two loops' hook code must be identical");

            // the hook never reads a setting and never sends anything of its own
            Assert.IsFalse(paramsBody.Contains("PropertyManager"));
            Assert.IsFalse(enumerableBody.Contains("PropertyManager"));
            Assert.IsFalse(ReadServerSource("ACE.Server/Entity/ClientObjectBook.cs").Contains("PropertyManager"));
            Assert.IsFalse(ReadServerSource("ACE.Server/Entity/ClientObjectBook.cs").Contains("EnqueueSend("));

            // the shared hook: bookkeeping isolated in try/catch, and the enqueue ALWAYS runs after it, under the lock
            var followed = MethodBody(ReadServerSource("ACE.Server/Entity/ClientObjectBook.cs"),
                "public static void EnqueueFollowed(ClientObjectBook book, GameMessage message, Action<GameMessage> enqueue, Action<Exception> onBookFailure)");
            var followedOrder = new[] { "lock (book.SyncRoot)", "try", "book.BeforeEnqueue(message);", "catch (Exception ex)", "onBookFailure?.Invoke(ex);", "enqueue(message);" };
            Assert.IsTrue(InOrder(followed, followedOrder));
            Assert.AreEqual(1, Regex.Matches(followed, Regex.Escape("enqueue(message);")).Count, "exactly one enqueue, outside the try");

            // the delegates are built once per session, not per message
            var ctor = MethodBody(source, "public NetworkSession(Session session, ConnectionListener connectionListener, ushort clientId, ushort serverId)");
            Assert.IsTrue(InOrder(ctor, "enqueueIntoBundle = EnqueueIntoBundle;", "noteBookFailure = NoteBookFailure;"));

            // discrimination: the matcher fails on a body missing the followed enqueue, or on a hook with the enqueue inside the try
            Assert.IsFalse(InOrder(paramsBody.Replace("ClientObjectBook.EnqueueFollowed(book, message, enqueueIntoBundle, noteBookFailure);", ""), EnqueueHook));
            Assert.IsFalse(InOrder(followed.Replace("enqueue(message);", ""), followedOrder));
        }

        [TestMethod]
        public void BundleEnqueue_IsTheOriginalInlineCode()
        {
            var source = ReadServerSource("ACE.Server/Network/NetworkSession.cs");
            var body = MethodBody(source, "private void EnqueueIntoBundle(GameMessage message)");

            const string original =
                "var grp = message.Group;" +
                "var currentBundleLock = currentBundleLocks[(int)grp];" +
                "lock (currentBundleLock)" +
                "{" +
                "var currentBundle = currentBundles[(int)grp];" +
                "currentBundle.EncryptedChecksum = true;" +
                "packetLog.DebugFormat(\"[{0}] Enqueuing Message {1}\", session.LoggingIdentifier, message.Opcode);" +
                "currentBundle.Enqueue(message);" +
                "}";

            Assert.AreEqual(Squash(original), Squash(body));
        }

        [TestMethod]
        public void LegacyFlushBody_IsUnchangedApartFromItsCounts()
        {
            var source = ReadServerSource("ACE.Server/WorldObjects/Player_Tracking.cs");
            var body = MethodBody(source, "public TeleportFlushCounts FlushKnownObjectsForInstanceChange()");

            // the method as it was before teleport_flush_scope, verbatim
            const string legacy = @"
            if (PhysicsObj?.ObjMaint == null)
                return;

            foreach (var knownObj in GetKnownObjects())
            {
                if (knownObj.PhysicsObj != null)
                {
                    knownObj.PhysicsObj.ObjMaint.RemoveObject(PhysicsObj);
                    ObjMaint.RemoveObject(knownObj.PhysicsObj);
                }

                if (knownObj is Player knownPlayer)
                    knownPlayer.RemoveTrackedObject(this, false);

                RemoveTrackedObject(knownObj, false);
            }";

            Assert.AreEqual(Squash(legacy), Squash(WithoutCounts(body)));

            // discrimination: dropping the observer delete, or reordering the self delete, is caught
            Assert.AreNotEqual(Squash(legacy), Squash(WithoutCounts(body.Replace("knownPlayer.RemoveTrackedObject(this, false);", ""))));
            Assert.AreNotEqual(Squash(legacy), Squash(WithoutCounts(body.Replace("RemoveTrackedObject(knownObj, false);", ""))));
        }

        /// <summary>
        /// The body minus the count bookkeeping: the trailing "return counts;" goes, the early one reads "return;" as it
        /// did, and every other line naming counts goes.
        /// </summary>
        private static string WithoutCounts(string body)
        {
            var last = body.LastIndexOf("return counts;", StringComparison.Ordinal);
            if (last >= 0)
                body = body.Remove(last, "return counts;".Length);

            var lines = body.Replace("return counts;", "return;").Split('\n')
                .Where(line => !line.Contains("counts"));

            return string.Join("\n", lines);
        }

        [TestMethod]
        public void Teleport_DefaultArm_IsTheLiteralLegacyGateBeforeTheRelocate()
        {
            var source = ReadServerSource("ACE.Server/WorldObjects/Player_Location.cs");
            var body = MethodBody(source, "public void Teleport(Position _newPosition, bool fromPortal = false, string source = null)");

            var ordered = new[]
            {
                "HandlePreTeleportVisibility(newPosition);",
                "var originInstancedLandblock = Location.InstancedLandblock;",
                "var flushSettings = ReadTeleportFlushSettings();",
                "if (flushSettings.Scope == TeleportFlushScope.Legacy && !flushSettings.Pacing)",
                "if (TeleportRequiresClientObjectFlush(originInstancedLandblock, newPosition.InstancedLandblock))",
                "flushCounts = FlushKnownObjectsForInstanceChange();",
                "else",
                "RunTeleportFlushVariant(flushAction, originInstancedLandblock, newPosition.InstancedLandblock, out var ledgerFired);",
                "var landblockUpdate = UpdatePlayerPosition(new Position(newPosition), true);",
            };

            Assert.IsTrue(InOrder(body, ordered), "the scope gate, the literal legacy flush and the variants must all run before UpdatePlayerPosition");

            // settings are read once per teleport, through the never-throws seam
            Assert.AreEqual(1, Regex.Matches(body, Regex.Escape("ReadTeleportFlushSettings()")).Count);
            Assert.IsFalse(body.Contains("PropertyManager.GetLong(\"teleport_flush"));

            // the fog re-queue carries the label (and, as before, not fromPortal)
            Assert.IsTrue(body.Contains("WorldManager.ThreadSafeTeleport(this, _newPosition, source: teleportSource)"));

            // discrimination
            Assert.IsFalse(InOrder(body.Replace("flushCounts = FlushKnownObjectsForInstanceChange();", ""), ordered));
        }

        [TestMethod]
        public void TeleportLogLine_AppendsTheFlushFields()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_Location.cs"),
                "public void Teleport(Position _newPosition, bool fromPortal = false, string source = null)");

            Assert.IsTrue(InOrder(body,
                "log.Info($\"[TELEPORT] {Name}",
                "(landblockUpdate {landblockUpdate}) ",
                "src={teleportSource} scope=",
                "pacing=",
                "delNow=",
                "delDeferred=",
                "obsDel=",
                "purged=",
                "ledger={ledgerNote} pending="));
        }

        [TestMethod]
        public void TrackObject_ChecksTheLedgerBeforeTheCreate()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_Tracking.cs"),
                "public void TrackObject(WorldObject worldObject, bool delay = false, GameMessageCreateObject sharedCreate = null, bool resend = false)");

            Assert.IsTrue(InOrder(body, "DeleteOtherCopyBeforeCreate(worldObject);", "Session.Network.EnqueueSend(sharedCreate != null"));
            Assert.IsFalse(InOrder(body.Replace("DeleteOtherCopyBeforeCreate(worldObject);", ""), "DeleteOtherCopyBeforeCreate(worldObject);", "Session.Network.EnqueueSend(sharedCreate != null"));
        }

        [TestMethod]
        public void ScopeGatedPaths_SendNothingUnderScope1()
        {
            var flush = ReadServerSource("ACE.Server/WorldObjects/Player_TeleportFlush.cs");

            // the TrackObject check only reads the ledger unless scope 2 is set, and nothing in it can stop the create
            var otherCopy = MethodBody(flush, "private void DeleteOtherCopyBeforeCreate(WorldObject worldObject)");
            Assert.IsTrue(InOrder(otherCopy, "try", "if (ReadTeleportFlushScope() != TeleportFlushScope.Ledger)", "return;",
                "TeleportFlushSteps.FireLedger(book, new[] { landblock }, instance, OwnsClientObjectCheck, SendToOwnClient, ForgetEffectsByGuid);",
                "catch (Exception ex)"));

            var fire = MethodBody(flush, "private int FireClientObjectLedger(ushort destinationLandblock, uint destinationInstance)");
            Assert.IsTrue(InOrder(fire, "TeleportFlushSteps.SendCollidingPending(", "TeleportFlushSteps.FireLedger(book, neighbourhood, destinationInstance, OwnsClientObjectCheck,"));

            // the book's property is a plain field read on the fast path (it runs for every create, delete and pickup)
            Assert.IsTrue(flush.Contains("public ClientObjectBook ClientObjectBook => clientObjectBook ?? CreateClientObjectBook();"));
            Assert.IsFalse(flush.Contains("LazyInitializer"));

            // the cull Forget is gated off under scope 1
            var culled = MethodBody(flush, "public void OnClientObjectsCulled(List<PhysicsObj> expiredObjs)");
            Assert.IsTrue(InOrder(culled, "if (ReadTeleportFlushScope() == TeleportFlushScope.Legacy)", "return;", "ForgetObjectAndEquipped(wo);"));

            // the ledger never deletes the player, its inventory or its equipment
            var owns = MethodBody(flush, "private bool OwnsClientObject(uint guid)");
            Assert.IsTrue(InOrder(owns, "guid == Guid.Full", "EquippedObjects.ContainsKey(", "Inventory.ContainsKey(", "GetInventoryItem(guid) != null"));

            // the one ledger-delete loop: the fail-safe ownership check precedes every delete
            var fireLedger = MethodBody(ReadServerSource("ACE.Server/Entity/TeleportFlushSteps.cs"),
                "public static int FireLedger(ClientObjectBook book, IReadOnlyList<ushort> landblocks, uint instance, Func<uint, bool> owns, Action<GameMessage> send, Action<uint> forget)");
            Assert.IsTrue(InOrder(fireLedger, "if (OwnsFailSafe(owns, stale.Guid))", "continue;", "send(new GameMessageDeleteObject(stale.Guid, stale.InstanceSequence));"));
            Assert.IsFalse(InOrder(fireLedger.Replace("if (OwnsFailSafe(owns, stale.Guid))", ""), "if (OwnsFailSafe(owns, stale.Guid))", "continue;", "send(new GameMessageDeleteObject("));

            // discrimination
            Assert.IsFalse(InOrder(culled.Replace("if (ReadTeleportFlushScope() == TeleportFlushScope.Legacy)", ""), "if (ReadTeleportFlushScope() == TeleportFlushScope.Legacy)", "return;", "ForgetObjectAndEquipped(wo);"));
        }

        [TestMethod]
        public void CullSites_BothRouteTheirExpiredObjects()
        {
            var physics = MethodBody(ReadServerSource("ACE.Server/Physics/PhysicsObj.cs"), "public List<PhysicsObj> handle_visible_cells()");
            Assert.IsTrue(InOrder(physics, "var expiredObjs = ObjMaint.DestroyObjects();", "cullingPlayer.OnClientObjectsCulled(expiredObjs);"));

            var heartbeat = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_Tick.cs"), "public override void Heartbeat(double currentUnixTime)");
            Assert.IsTrue(heartbeat.Contains("OnClientObjectsCulled(PhysicsObj.ObjMaint.DestroyObjects());"));
        }

        [TestMethod]
        public void Drain_RunsRightAfterQueuedActions_AndLogoutDiscards()
        {
            var tick = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_Tick.cs"), "public void Player_Tick(double currentUnixTime)");
            var afterActions = tick.Substring(tick.IndexOf("actionQueue.RunActions();", StringComparison.Ordinal));
            var nextStatement = Regex.Replace(afterActions.Substring("actionQueue.RunActions();".Length), @"^(\s*//[^\n]*\n|\s+)*", "");

            StringAssert.StartsWith(nextStatement, "DrainTeleportDeletes();", "the drain must be the first thing after the queued actions");

            var logout = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player.cs"), "public void LogOut_Inner(bool clientSessionTerminatedAbruptly = false)");
            Assert.IsTrue(InOrder(logout, "TeleportWatchOnLogOut();", "DiscardTeleportDeletes();", "IsLoggingOut = true;"));

            // production runs the tested steps (behaviour: ClientObjectBookTests Drain_* and Logout_*)
            var flush = ReadServerSource("ACE.Server/WorldObjects/Player_TeleportFlush.cs");
            Assert.IsTrue(MethodBody(flush, "public void DrainTeleportDeletes()").Contains("TeleportFlushSteps.Drain(book, settings.Pacing, Teleporting, settings.Rate, SendToOwnClient, out var ended);"));
            Assert.IsTrue(MethodBody(flush, "public void DiscardTeleportDeletes()").Contains("TeleportFlushSteps.Discard(book, out var ended)"));

            // the drain never holds anything while pacing is off
            var drain = MethodBody(ReadServerSource("ACE.Server/Entity/TeleportFlushSteps.cs"),
                "public static string Drain(ClientObjectBook book, bool pacing, bool teleporting, int rate, Action<GameMessage> send, out TeleportDeleteCycle ended)");
            Assert.IsTrue(InOrder(drain, "if (!pacing)", "book.PendingDeletes.TakeAll()", "end = \"pacing-off\";", "if (teleporting)", "return null;", "book.PendingDeletes.Take(rate)"));
        }

        [TestMethod]
        public void DeferredFlush_QueuesBeforeCuttingTracking()
        {
            // the step itself (behaviour: ClientObjectBookTests.Pacing_CollisionsGoUpFront_TheRestWait)
            var step = MethodBody(ReadServerSource("ACE.Server/Entity/TeleportFlushSteps.cs"), "public static void DeferredFlush<T>(");
            Assert.IsTrue(InOrder(step,
                "SendCollidingPending(book, destinationNeighbourhood, destinationInstance, send);",
                "foreach (var knownObject in knownObjects)",
                "if (TeleportFlushPolicy.Collides(landblock, instance, destinationNeighbourhood, destinationInstance))",
                "legacyRemove(knownObject);",
                "continue;",
                "lock (book.SyncRoot)",
                "book.Defer(delete.Key, landblock, instance, delete.Value, nowTicks);",
                "cutTracking(knownObject);"));

            // the production call: the legacy per-object body for a collision, then the cut for a deferral
            var body = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_TeleportFlush.cs"),
                "private TeleportFlushCounts DeferredFlushKnownObjects(ulong originInstancedLandblock, ushort destinationLandblock, uint destinationInstance)");

            Assert.IsTrue(InOrder(body,
                "TeleportFlushSteps.DeferredFlush(ClientObjectBook, GetKnownObjects(),",
                "ClientObjectPlacement.TryGet,",
                "BuildFlushDeletes,",
                "collidingPlayer.RemoveTrackedObject(this, false);",
                "RemoveTrackedObject(knownObj, false);",
                "knownObj.PhysicsObj.ObjMaint.RemoveObject(PhysicsObj);",
                "knownPlayer.RemoveTrackedObject(this, false);",
                "ForgetObjectAndEquipped(knownObj);"));

            var deletes = MethodBody(ReadServerSource("ACE.Server/WorldObjects/Player_TeleportFlush.cs"),
                "private IEnumerable<KeyValuePair<uint, GameMessage>> BuildFlushDeletes(WorldObject knownObj)");
            Assert.IsTrue(InOrder(deletes, "new GameMessageDeleteObject(knownObj)", "if (SendsEquippedDelete(creature, wieldedItem))", "new GameMessageDeleteObject(wieldedItem)"));
        }

        [TestMethod]
        public void DroppedSessionLines_AppendOnly()
        {
            var body = MethodBody(ReadServerSource("ACE.Server/Network/Session.cs"), "public void DropSession()");

            Assert.IsTrue(body.Contains("log.Info($\"Session {Network?.ClientId}\\\\{EndPointC2S} dropped. Account: {Account}, Player: {Player?.Name}{reas}{DropDiagnostics()}\");"));
            Assert.IsTrue(body.Contains("log.DebugFormat(\"Session {0}\\\\{1} dropped. Account: {2}, Player: {3}{4}{5}\", Network?.ClientId, EndPointC2S, Account, Player?.Name, reas, DropDiagnostics());"));

            var diagnostics = MethodBody(ReadServerSource("ACE.Server/Network/Session.cs"), "private string DropDiagnostics()");
            Assert.IsTrue(InOrder(diagnostics, "\" | cell=0x", "inst=0x", "sinceInboundS=", "connAgeS=", "creates=", "distinctCreated=", "pendingDeletes=", "teleporting=", "ledgerEvictions="));
        }

        [TestMethod]
        public void TeleportSources_AreTheFifteenLabels_AndEveryNamedSiteIsTagged()
        {
            CollectionAssert.AreEqual(
                new[] { "portal", "lifestone", "house", "mansion", "marketplace", "drift", "town", "allegiance", "pkarena", "pklarena", "spell", "gem", "death", "emote", "other" },
                TeleportSource.All);

            Assert.AreEqual("portal", TeleportSource.Resolve(null, true));
            Assert.AreEqual("other", TeleportSource.Resolve(null, false));
            Assert.AreEqual("gem", TeleportSource.Resolve(TeleportSource.Gem, true));

            void Tagged(string file, string label, int count)
            {
                var text = ReadServerSource(file);
                Assert.AreEqual(count, Regex.Matches(text, @"\bTeleportSource\." + label + @"\b").Count, $"{file}: TeleportSource.{label}");
            }

            const string location = "ACE.Server/WorldObjects/Player_Location.cs";
            Tagged(location, "House", 1);
            Tagged(location, "Lifestone", 1);
            Tagged(location, "Marketplace", 1);
            Tagged(location, "Drift", 1);
            Tagged(location, "Town", 1);
            Tagged(location, "Allegiance", 1);
            Tagged(location, "Mansion", 1);
            Tagged(location, "PkArena", 1);
            Tagged(location, "PklArena", 1);
            Tagged("ACE.Server/WorldObjects/WorldObject_Magic.cs", "Spell", 3);
            Tagged("ACE.Server/WorldObjects/Portal.cs", "Portal", 1);
            Tagged("ACE.Server/WorldObjects/Gem.cs", "Gem", 1);
            Tagged("ACE.Server/WorldObjects/Player_Death.cs", "Death", 1);
            Tagged("ACE.Server/WorldObjects/Managers/EmoteManager.cs", "Emote", 2);

            // the network hub teleport passes its caller's label through
            var hub = MethodBody(ReadServerSource(location), "private void HandleActionTeleToNetworkHub(string networkName, Position drop, WorldRealm realm, string unavailableMessage, string source)");
            Assert.IsTrue(hub.Contains("Teleport(new Position(drop, realm.DefaultInstanceID), source: source);"));

            var threadSafe = MethodBody(ReadServerSource("ACE.Server/Managers/WorldManager.cs"),
                "public static void ThreadSafeTeleport(Player player, Position newPosition, IAction actionToFollowUpWith = null, bool fromPortal = false, string source = null)");
            Assert.IsTrue(threadSafe.Contains("player.Teleport(newPosition, fromPortal, source);"));
        }
    }
}
