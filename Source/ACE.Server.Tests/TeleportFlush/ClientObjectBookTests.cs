using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.TeleportFlush
{
    /// <summary>
    /// ClientObjectBook: the bookkeeping both NetworkSession.EnqueueSend loops run under its lock, and the scope
    /// scenarios driven through it.
    ///
    /// The scenario tests drive the book with the same pure calls Player_TeleportFlush makes (Decide, then
    /// TakeOtherCopies over the destination 3x3, then the arrival creates through NoteCreate). They do not run
    /// Player.Teleport: no test in this project can build a live Player (see TestEnvironment.cs). The Player glue is
    /// pinned by TeleportFlushSourceInvariantTests instead.
    /// </summary>
    [TestClass]
    public class ClientObjectBookTests
    {
        private const uint Owner = 0x50000001;
        private static readonly uint Realm0 = Position.InstanceIDFromVars(0, 0, false);
        private static readonly uint Realm1 = Position.InstanceIDFromVars(1, 0, false);

        private const ushort TownNetwork = 0x0007;
        private const ushort Lifestone = 0x0016;

        private static readonly byte[] Seq = { 1, 0 };

        private static ulong At(ushort landblock, uint instance)
        {
            return new Position((uint)landblock << 16 | 0x0100, 0, 0, 0, 0, 0, 0, 1, instance).InstancedLandblock;
        }

        /// <summary>The arrival creates for a landblock copy, as the hook files them.</summary>
        private static void ArrivalCreates(ClientObjectBook book, ushort landblock, uint instance, params uint[] guids)
        {
            lock (book.SyncRoot)
            {
                foreach (var guid in guids)
                    book.NoteCreate(guid, 0, true, landblock, instance, Seq);
            }
        }

        /// <summary>Scope 2's part of a teleport, through the production FireLedger: the deletes it sends, in order.</summary>
        private static List<uint> Scope2Teleport(ClientObjectBook book, ulong origin, ulong destination)
        {
            var fired = new List<uint>();
            var action = TeleportFlushPolicy.Decide(TeleportFlushScope.Ledger, false, origin, destination);

            Assert.IsTrue((action & TeleportFlushAction.LedgerFire) != 0, "scope 2 fires on every teleport");

            var neighbourhood = TeleportFlushPolicy.Neighbourhood(TeleportFlushPolicy.LandblockOf(destination));

            TeleportFlushSteps.FireLedger(book, neighbourhood, TeleportFlushPolicy.InstanceOf(destination), guid => guid == Owner,
                message =>
                {
                    var delete = (GameMessageDeleteObject)message;
                    fired.Add(delete.ObjectId);

                    // the delete goes back through the hook, exactly as NetworkSession would run it
                    ClientObjectBook.EnqueueFollowed(book, message, _ => { }, ex => Assert.Fail(ex.ToString()));
                },
                null);

            return fired;
        }
        [TestMethod]
        public void Follows_OnlyTheThreeObjectOpcodes()
        {
            var creature = TestCreatures.CreateQuestBearer();

            Assert.IsTrue(ClientObjectBook.Follows(new GameMessageDeleteObject(creature)));
            Assert.IsTrue(ClientObjectBook.Follows(new GameMessagePickupEvent(creature)));
            Assert.IsFalse(ClientObjectBook.Follows(new GameMessagePlayScriptId(creature.Guid, 1)));
        }

        [TestMethod]
        public void NoteCreate_FilesWorldObjects_NeverTheOwnerOrWhatItCarries()
        {
            var book = new ClientObjectBook(Owner);

            lock (book.SyncRoot)
            {
                book.NoteCreate(0x80000001, 0, true, TownNetwork, Realm1, Seq);        // a world object: filed
                book.NoteCreate(Owner, 0, true, TownNetwork, Realm1, Seq);             // the player itself
                book.NoteCreate(0x80000002, Owner, true, TownNetwork, Realm1, Seq);    // wielded by the player
                book.NoteCreate(0x80000003, 0, false, 0, 0, Seq);                      // contained: no placement
                book.NoteCreate(0x80000004, 0x80000001, true, TownNetwork, Realm1, Seq); // wielded by someone else: filed
            }

            Assert.IsTrue(book.Ledger.Contains(0x80000001));
            Assert.IsFalse(book.Ledger.Contains(Owner));
            Assert.IsFalse(book.Ledger.Contains(0x80000002));
            Assert.IsFalse(book.Ledger.Contains(0x80000003));
            Assert.IsTrue(book.Ledger.Contains(0x80000004));
        }

        [TestMethod]
        public void NoteCreate_WithoutPlacement_UnfilesAnEarlierWorldFiling()
        {
            var book = new ClientObjectBook(Owner);

            lock (book.SyncRoot)
            {
                book.NoteCreate(0x80000001, 0, true, TownNetwork, Realm1, Seq);
                book.NoteCreate(0x80000001, 0, false, 0, 0, Seq); // since moved into a container
            }

            Assert.IsFalse(book.Ledger.Contains(0x80000001));
        }

        [TestMethod]
        public void DeleteAndPickup_Unfile()
        {
            var creature = TestCreatures.CreateQuestBearer();
            var book = new ClientObjectBook(Owner);

            lock (book.SyncRoot)
            {
                book.NoteCreate(creature.Guid.Full, 0, true, TownNetwork, Realm1, Seq);
                book.BeforeEnqueue(new GameMessageDeleteObject(creature));
            }

            Assert.IsFalse(book.Ledger.Contains(creature.Guid.Full));

            lock (book.SyncRoot)
            {
                book.NoteCreate(creature.Guid.Full, 0, true, TownNetwork, Realm1, Seq);
                book.BeforeEnqueue(new GameMessagePickupEvent(creature));
            }

            Assert.IsFalse(book.Ledger.Contains(creature.Guid.Full));
        }

        [TestMethod]
        public void CancelOnCreate_PendingDeleteIsNeverSentAfterTheCreate()
        {
            var book = new ClientObjectBook(Owner);
            GameMessage deleteX = new GameMessageDeleteObject(TestCreatures.CreateQuestBearer());
            var x = ((GameMessageDeleteObject)deleteX).ObjectId;

            lock (book.SyncRoot)
            {
                book.Defer(x, Lifestone, Realm1, deleteX, 1000);
                book.Defer(0x80000099, Lifestone, Realm1, new GameMessageDeleteObject(TestCreatures.CreateQuestBearer()), 1000);

                // the arrival create for X passes the hook first
                book.NoteCreate(x, 0, true, Lifestone, Realm1, Seq);

                var released = book.PendingDeletes.TakeAll();

                Assert.IsFalse(released.Contains(deleteX), "X was re-created, so its waiting delete must never go out");
                Assert.AreEqual(1, released.Count);
            }
        }

        [TestMethod]
        public void LaterDeleteSupersedesAWaitingOne()
        {
            var book = new ClientObjectBook(Owner);
            var creature = TestCreatures.CreateQuestBearer();

            lock (book.SyncRoot)
            {
                book.Defer(creature.Guid.Full, Lifestone, Realm1, new GameMessageDeleteObject(creature), 1000);
                book.BeforeEnqueue(new GameMessageDeleteObject(creature));

                Assert.AreEqual(0, book.PendingDeletes.Count, "the client already got a delete: a second one would be a duplicate");
            }
        }

        [TestMethod]
        public void DrainCycleOpen_FollowsDeferAndEnd()
        {
            var book = new ClientObjectBook(Owner);

            Assert.IsFalse(book.DrainCycleOpen);

            lock (book.SyncRoot)
            {
                book.Defer(1, Lifestone, Realm1, new GameMessageDeleteObject(TestCreatures.CreateQuestBearer()), 1000);
                book.NoteTeleport(1000);
            }

            Assert.IsTrue(book.DrainCycleOpen);

            lock (book.SyncRoot)
            {
                book.PendingDeletes.TakeAll();
                var cycle = book.EndCycle();
                Assert.AreEqual(1, cycle.Teleports);
            }

            Assert.IsFalse(book.DrainCycleOpen);
        }

        [TestMethod]
        public void GhostPath_Scope2_DeletesTheStaleRealm1Objects()
        {
            // realm-1 Town Network (the Drift trainers) -> a lifestone elsewhere -> realm-0 Town Network. Under the
            // legacy flush the trainers are deleted at the first hop; scope 2 must still delete them at the last hop,
            // where the realm-0 copy of the same landblock id would otherwise render them as ghosts.
            var book = new ClientObjectBook(Owner);

            ArrivalCreates(book, TownNetwork, Realm1, 0x80000001, 0x80000002, 0x80000003);

            CollectionAssert.AreEqual(new uint[0], Scope2Teleport(book, At(TownNetwork, Realm1), At(Lifestone, Realm1)),
                "nothing near the lifestone is held from another copy");
            ArrivalCreates(book, Lifestone, Realm1, 0x80000010);

            var fired = Scope2Teleport(book, At(Lifestone, Realm1), At(TownNetwork, Realm0));

            CollectionAssert.AreEqual(new uint[] { 0x80000001, 0x80000002, 0x80000003 }, fired);

            // and the realm-0 arrival files under the new copy
            ArrivalCreates(book, TownNetwork, Realm0, 0x80000001);
            Assert.AreEqual(Realm0, book.Ledger.HeldInstance(TownNetwork));
        }

        [TestMethod]
        public void GhostPath_Shadow_CountsWhatScope2WouldFire()
        {
            // under scopes 0 and 1 the [TELEPORT] line reports ledger=shadow:N, counted before any flush
            var book = new ClientObjectBook(Owner);

            ArrivalCreates(book, TownNetwork, Realm1, 0x80000001, 0x80000002, 0x80000003);

            lock (book.SyncRoot)
            {
                Assert.AreEqual(3, book.Ledger.CountOtherCopies(TeleportFlushPolicy.Neighbourhood(TownNetwork), Realm0));
                Assert.AreEqual(3, book.Ledger.Count, "a shadow count takes nothing");
            }
        }

        [TestMethod]
        public void SameCopyRoundTrip_Scope2_SendsNoDeletes()
        {
            var book = new ClientObjectBook(Owner);

            ArrivalCreates(book, TownNetwork, Realm0, 0x80000001, 0x80000002);

            Assert.AreEqual(0, Scope2Teleport(book, At(TownNetwork, Realm0), At(Lifestone, Realm0)).Count);
            ArrivalCreates(book, Lifestone, Realm0, 0x80000010);

            Assert.AreEqual(0, Scope2Teleport(book, At(Lifestone, Realm0), At(TownNetwork, Realm0)).Count);
            ArrivalCreates(book, TownNetwork, Realm0, 0x80000001, 0x80000002);

            Assert.AreEqual(3, book.Ledger.Count);
        }

        private sealed class KnownStub
        {
            public uint Guid;
            public bool Placed = true;
            public ushort Landblock;
            public uint Instance;
        }

        private static bool PlaceStub(KnownStub stub, out ushort landblock, out uint instance)
        {
            landblock = stub.Landblock;
            instance = stub.Instance;
            return stub.Placed;
        }

        private static IEnumerable<KeyValuePair<uint, GameMessage>> StubDeletes(KnownStub stub)
        {
            yield return new KeyValuePair<uint, GameMessage>(stub.Guid, new GameMessageDeleteObject(stub.Guid, Seq));
        }

        /// <summary>Runs the production paced flush over stubs; returns (sent at once, legacy-removed, tracking-cut) guids.</summary>
        private static (List<uint> sent, List<uint> legacy, List<uint> cut) PacedFlush(ClientObjectBook book, ushort destinationLandblock, uint destinationInstance, params KnownStub[] known)
        {
            var sent = new List<uint>();
            var legacy = new List<uint>();
            var cut = new List<uint>();

            TeleportFlushSteps.DeferredFlush(book, known, TeleportFlushPolicy.Neighbourhood(destinationLandblock), destinationInstance,
                Lifestone, Realm1, 1000,
                PlaceStub, StubDeletes,
                stub => legacy.Add(stub.Guid),
                stub =>
                {
                    // the deletes must already be queued when tracking is cut
                    lock (book.SyncRoot)
                        Assert.IsTrue(book.PendingDeletes.IsPending(stub.Guid), "queued before the cut");
                    cut.Add(stub.Guid);
                },
                message => sent.Add(((GameMessageDeleteObject)message).ObjectId),
                out var deletedNow, out var deferred);

            Assert.AreEqual(legacy.Count, deletedNow);
            Assert.AreEqual(cut.Count, deferred);

            return (sent, legacy, cut);
        }

        [TestMethod]
        public void Pacing_CollisionsGoUpFront_TheRestWait()
        {
            // a paced scope-1 flush from the realm-1 lifestone into realm-0 Town Network: deletes still waiting from the
            // earlier realm-1 Town Network visit are pulled to the front, known objects that are another copy of the
            // destination 3x3 take the legacy path at once, and the rest are deferred
            var book = new ClientObjectBook(Owner);

            lock (book.SyncRoot)
            {
                book.Defer(0x80000001, TownNetwork, Realm1, new GameMessageDeleteObject(0x80000001, Seq), 500);
                book.Defer(0x80000005, Lifestone, Realm1, new GameMessageDeleteObject(0x80000005, Seq), 500);
            }

            var (sent, legacy, cut) = PacedFlush(book, TownNetwork, Realm0,
                new KnownStub { Guid = 0x80000010, Landblock = Lifestone, Instance = Realm1 },
                new KnownStub { Guid = 0x80000011, Landblock = 0x0108, Instance = Realm1 },
                new KnownStub { Guid = 0x80000012, Placed = false });

            CollectionAssert.AreEqual(new uint[] { 0x80000001 }, sent, "only the realm-1 Town Network delete collides with realm-0 Town Network");
            CollectionAssert.AreEqual(new uint[] { 0x80000011 }, legacy, "the colliding neighbour goes up front");
            CollectionAssert.AreEqual(new uint[] { 0x80000010, 0x80000012 }, cut, "an unplaced object falls back to the origin and waits");

            lock (book.SyncRoot)
            {
                Assert.IsTrue(book.PendingDeletes.IsPending(0x80000005));
                Assert.IsTrue(book.PendingDeletes.IsPending(0x80000010));
                Assert.IsTrue(book.PendingDeletes.IsPending(0x80000012));
                Assert.IsFalse(book.PendingDeletes.IsPending(0x80000011));
                Assert.AreEqual(1, book.PendingDeletes.Cycle.Teleports);
            }
        }

        [TestMethod]
        public void Drain_PacingOn_WaitsForTheTeleport_ThenReleasesRatePerTick_AndSwitchingOffReleasesAll()
        {
            var book = new ClientObjectBook(Owner);
            var sent = new List<uint>();
            Action<GameMessage> send = m => sent.Add(((GameMessageDeleteObject)m).ObjectId);

            PacedFlush(book, TownNetwork, Realm1,
                new KnownStub { Guid = 1, Landblock = Lifestone, Instance = Realm1 },
                new KnownStub { Guid = 2, Landblock = Lifestone, Instance = Realm1 },
                new KnownStub { Guid = 3, Landblock = Lifestone, Instance = Realm1 },
                new KnownStub { Guid = 4, Landblock = Lifestone, Instance = Realm1 },
                new KnownStub { Guid = 5, Landblock = Lifestone, Instance = Realm1 });

            Assert.IsTrue(book.DrainCycleOpen);

            Assert.IsNull(TeleportFlushSteps.Drain(book, true, true, 2, send, out _), "mid-teleport, paced: nothing goes");
            Assert.AreEqual(0, sent.Count);

            Assert.IsNull(TeleportFlushSteps.Drain(book, true, false, 2, send, out _));
            CollectionAssert.AreEqual(new uint[] { 1, 2 }, sent);

            // the arrival create for 3 cancels its delete
            lock (book.SyncRoot)
                book.NoteCreate(3, 0, true, Lifestone, Realm1, Seq);

            // pacing switched off mid-drain: everything left goes on the next tick, even mid-teleport
            var end = TeleportFlushSteps.Drain(book, false, true, 2, send, out var cycle);

            Assert.AreEqual("pacing-off", end);
            CollectionAssert.AreEqual(new uint[] { 1, 2, 4, 5 }, sent, "3 was re-created, so it is never deleted");
            Assert.AreEqual(4, cycle.Sent);
            Assert.AreEqual(1, cycle.Cancelled);
            Assert.AreEqual(5, cycle.Deferred);
            Assert.AreEqual(1, cycle.Teleports);
            Assert.IsFalse(book.DrainCycleOpen);
            Assert.IsNull(TeleportFlushSteps.Drain(book, false, false, 2, send, out _), "no open cycle: nothing to do");
        }

        [TestMethod]
        public void Drain_PacingOn_EndsCompleteWhenEmpty()
        {
            var book = new ClientObjectBook(Owner);
            var sent = new List<GameMessage>();

            PacedFlush(book, TownNetwork, Realm1,
                new KnownStub { Guid = 1, Landblock = Lifestone, Instance = Realm1 },
                new KnownStub { Guid = 2, Landblock = Lifestone, Instance = Realm1 });

            Assert.IsNull(TeleportFlushSteps.Drain(book, true, false, 1, sent.Add, out _));
            Assert.AreEqual("complete", TeleportFlushSteps.Drain(book, true, false, 1, sent.Add, out var cycle));
            Assert.AreEqual(2, sent.Count);
            Assert.AreEqual(2, cycle.Sent);
        }

        [TestMethod]
        public void Logout_DiscardSendsNothing_AndEndsTheCycle()
        {
            var book = new ClientObjectBook(Owner);
            var sent = new List<GameMessage>();

            PacedFlush(book, TownNetwork, Realm1,
                new KnownStub { Guid = 1, Landblock = Lifestone, Instance = Realm1 },
                new KnownStub { Guid = 2, Landblock = Lifestone, Instance = Realm1 });

            Assert.IsTrue(TeleportFlushSteps.Discard(book, out var cycle));
            Assert.AreEqual(0, cycle.Sent);
            Assert.AreEqual(2, cycle.Deferred);
            Assert.IsFalse(book.DrainCycleOpen);

            Assert.IsNull(TeleportFlushSteps.Drain(book, false, false, 10, sent.Add, out _));
            Assert.AreEqual(0, sent.Count, "nothing discarded is ever sent");
            Assert.IsFalse(TeleportFlushSteps.Discard(book, out _), "a second discard has nothing to end");
        }

        [TestMethod]
        public void EnqueueFollowed_ThrowingBookkeeping_StillEnqueuesTheMessage()
        {
            var book = new ClientObjectBook(Owner);
            var enqueued = new List<GameMessage>();
            var failures = new List<Exception>();

            book.BookkeepingFaultForTests = _ => throw new InvalidOperationException("injected");

            var create = new GameMessageDeleteObject(0x80000001, Seq);
            ClientObjectBook.EnqueueFollowed(book, create, enqueued.Add, failures.Add);

            CollectionAssert.AreEqual(new GameMessage[] { create }, enqueued, "the message goes out even when the bookkeeping throws");
            Assert.AreEqual(1, failures.Count);
            Assert.AreEqual("injected", failures[0].Message);

            // and without the fault the bookkeeping runs and the message still goes out once
            book.BookkeepingFaultForTests = null;
            lock (book.SyncRoot)
                book.NoteCreate(0x80000002, 0, true, Lifestone, Realm1, Seq);

            var delete = new GameMessageDeleteObject(0x80000002, Seq);
            ClientObjectBook.EnqueueFollowed(book, delete, enqueued.Add, failures.Add);

            Assert.AreEqual(2, enqueued.Count);
            Assert.AreEqual(1, failures.Count);
            Assert.IsFalse(book.Ledger.Contains(0x80000002));
        }

        [TestMethod]
        public void FireLedger_ThrowingOwnsCheck_SkipsThatDelete_AndTheCreateStillGoesOut()
        {
            // TrackObject runs FireLedger (through DeleteOtherCopyBeforeCreate) from the physics phase, where the owns
            // check enumerates the player's inventory and can throw. A throw counts as owned; nothing escapes.
            var book = new ClientObjectBook(Owner);
            ArrivalCreates(book, TownNetwork, Realm1, 0x80000001, 0x80000002, 0x80000003);

            var sent = new List<uint>();

            var fired = TeleportFlushSteps.FireLedger(book, new ushort[] { TownNetwork }, Realm0,
                guid => guid == 0x80000002 ? throw new InvalidOperationException("Collection was modified") : false,
                m => sent.Add(((GameMessageDeleteObject)m).ObjectId), null);

            // the create that TrackObject sends next
            ArrivalCreates(book, TownNetwork, Realm0, 0x80000001);

            Assert.AreEqual(2, fired);
            CollectionAssert.AreEqual(new uint[] { 0x80000001, 0x80000003 }, sent, "the throwing guid is skipped, the rest still go");
            Assert.AreEqual(Realm0, book.Ledger.HeldInstance(TownNetwork), "the create was filed");
            Assert.IsTrue(TeleportFlushSteps.OwnsFailSafe(_ => throw new InvalidOperationException(), 1));
            Assert.IsFalse(TeleportFlushSteps.OwnsFailSafe(_ => false, 1));
        }
    }
}