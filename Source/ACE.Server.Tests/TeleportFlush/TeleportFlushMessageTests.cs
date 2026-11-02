using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Sequence;
using ACE.Server.WorldObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.TeleportFlush
{
    /// <summary>
    /// The message-side and settings-side pieces of the teleport flush switch: the guid-and-sequence delete the
    /// ledger rebuilds, the placement a CreateObject is filed under, the three registered settings, and the
    /// never-throws settings seam.
    /// </summary>
    [TestClass]
    public class TeleportFlushMessageTests
    {
        [ClassInitialize]
        public static void RegisterCodePages(TestContext context)
        {
            // CreateObject serializes the name with code page 1252, which the test host does not register on its own
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        private static WorldObject CreateItem()
        {
            var weenie = new Weenie
            {
                WeenieClassId = TestCreatures.NextWcid(),
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string>
                {
                    { ACE.Entity.Enum.Properties.PropertyString.Name, "Test Item" },
                },
            };

            return new GenericObject(weenie, new ObjectGuid(0x7E100000 + TestCreatures.NextWcid() % 0x10000));
        }

        [TestMethod]
        public void LedgerDelete_IsByteIdenticalToTheWorldObjectDelete()
        {
            var creature = TestCreatures.CreateQuestBearer();

            var fromObject = new GameMessageDeleteObject(creature).Data.ToArray();
            var fromLedger = new GameMessageDeleteObject(creature.Guid.Full, creature.Sequences.GetCurrentSequence(SequenceType.ObjectInstance)).Data.ToArray();

            CollectionAssert.AreEqual(fromObject, fromLedger);

            // and still identical once the instance sequence has moved off its starting value
            creature.Sequences.GetNextSequence(SequenceType.ObjectInstance);
            creature.Sequences.GetNextSequence(SequenceType.ObjectInstance);

            var movedObject = new GameMessageDeleteObject(creature).Data.ToArray();
            var movedLedger = new GameMessageDeleteObject(creature.Guid.Full, creature.Sequences.GetCurrentSequence(SequenceType.ObjectInstance)).Data.ToArray();

            CollectionAssert.AreEqual(movedObject, movedLedger);
            CollectionAssert.AreNotEqual(fromObject, movedObject, "the sequence really moved, so the comparison above is not vacuous");
        }

        [TestMethod]
        public void MessageGuids_MatchTheObject()
        {
            var creature = TestCreatures.CreateQuestBearer();

            Assert.AreEqual(creature.Guid.Full, new GameMessageDeleteObject(creature).ObjectId);
            Assert.AreEqual(creature.Guid.Full, new GameMessagePickupEvent(creature).ObjectId);
            Assert.AreEqual(0x12345678u, new GameMessageDeleteObject(0x12345678, new byte[] { 1, 0 }).ObjectId);
        }

        /// <summary>
        /// What GameMessageCreateObject wrote at d582c149b, before the bookkeeping capture existed: the base
        /// constructor's opcode, then SerializeCreateObject, nothing else.
        /// </summary>
        private static byte[] CreateBytesWithoutCapture(WorldObject worldObject)
        {
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write((uint)ACE.Server.Network.GameMessages.GameMessageOpcode.ObjectCreate);
                worldObject.SerializeCreateObject(writer, false, false);
                writer.Flush();
                return stream.ToArray();
            }
        }

        [TestMethod]
        public void RealCreate_CapturesGuidPlacementAndSequence_AndWritesTheSameBytes()
        {
            var creature = TestCreatures.CreateQuestBearer();
            var realm1 = Position.InstanceIDFromVars(1, 0, false);
            creature.Location = new Position(0x00070147, 1, 2, 3, 0, 0, 0, 1, realm1);

            var expected = CreateBytesWithoutCapture(creature);
            var create = new GameMessageCreateObject(creature);

            CollectionAssert.AreEqual(expected, create.Data.ToArray(), "the capture must not change a single byte");
            CollectionAssert.AreEqual(expected, CreateBytesWithoutCapture(creature), "serializing is repeatable, so the comparison is meaningful");

            Assert.AreEqual(creature.Guid.Full, create.ObjectId);
            Assert.AreEqual(0u, create.WielderId);
            Assert.IsTrue(create.HasPlacement);
            Assert.AreEqual((ushort)0x0007, create.PlacementLandblock);
            Assert.AreEqual(realm1, create.PlacementInstance);
            CollectionAssert.AreEqual(creature.Sequences.GetCurrentSequence(SequenceType.ObjectInstance), create.InstanceSequence);
        }

        [TestMethod]
        public void RealCreate_WieldedItem_StandsWhereItsWielderDoes()
        {
            var creature = TestCreatures.CreateQuestBearer();
            var realm1 = Position.InstanceIDFromVars(1, 0, false);
            creature.Location = new Position(0x00070147, 1, 2, 3, 0, 0, 0, 1, realm1);

            var item = CreateItem();
            item.Wielder = creature;

            var expected = CreateBytesWithoutCapture(item);
            var create = new GameMessageCreateObject(item);

            CollectionAssert.AreEqual(expected, create.Data.ToArray());
            Assert.AreEqual(item.Guid.Full, create.ObjectId);
            Assert.AreEqual(creature.Guid.Full, create.WielderId);
            Assert.IsTrue(create.HasPlacement);
            Assert.AreEqual((ushort)0x0007, create.PlacementLandblock);
            Assert.AreEqual(realm1, create.PlacementInstance);

            // and a contained item (no Location, no wielder) has no placement
            var contained = new GameMessageCreateObject(CreateItem());
            Assert.IsFalse(contained.HasPlacement);
        }

        [TestMethod]
        public void RealCreate_ThroughTheBook_FilesItAndCancelsItsWaitingDelete()
        {
            var creature = TestCreatures.CreateQuestBearer();
            creature.Location = new Position(0x00070147, 1, 2, 3, 0, 0, 0, 1, 0);

            var book = new ClientObjectBook(0x50000001);
            var enqueued = new System.Collections.Generic.List<ACE.Server.Network.GameMessages.GameMessage>();

            lock (book.SyncRoot)
                book.Defer(creature.Guid.Full, 0x0007, 0, new GameMessageDeleteObject(creature), 1000);

            var create = new GameMessageCreateObject(creature);
            ClientObjectBook.EnqueueFollowed(book, create, enqueued.Add, ex => Assert.Fail(ex.ToString()));

            lock (book.SyncRoot)
            {
                Assert.IsFalse(book.PendingDeletes.IsPending(creature.Guid.Full), "the create cancelled its waiting delete");
                Assert.AreEqual(1, book.PendingDeletes.Cycle.Cancelled);
                Assert.IsTrue(book.Ledger.Contains(creature.Guid.Full));
                Assert.AreEqual(0u, book.Ledger.HeldInstance(0x0007));
            }

            Assert.AreEqual(1, enqueued.Count);
        }

        [TestMethod]
        public void Placement_IsOwnLocation_OrTheWieldersWhenWielded_OrNoneWhenContained()
        {
            var creature = TestCreatures.CreateQuestBearer();
            creature.Location = new Position(0x00070147, 1, 2, 3, 0, 0, 0, 1, Position.InstanceIDFromVars(1, 0, false));

            Assert.IsTrue(ClientObjectPlacement.TryGet(creature, out var landblock, out var instance));
            Assert.AreEqual((ushort)0x0007, landblock);
            Assert.AreEqual(Position.InstanceIDFromVars(1, 0, false), instance);

            var item = CreateItem();
            Assert.IsFalse(ClientObjectPlacement.TryGet(item, out _, out _), "no Location and no wielder: contained, never filed");

            item.Wielder = creature;
            Assert.IsTrue(ClientObjectPlacement.TryGet(item, out landblock, out instance), "a wielded item stands where its wielder does");
            Assert.AreEqual((ushort)0x0007, landblock);

            Assert.IsFalse(ClientObjectPlacement.TryGet(null, out _, out _));
        }

        [TestMethod]
        public void Settings_AreRegisteredWithTheNoOpDefaults()
        {
            Assert.AreEqual(1L, DefaultPropertyManager.DefaultLongProperties["teleport_flush_scope"].Item);
            Assert.AreEqual(false, DefaultPropertyManager.DefaultBooleanProperties["teleport_flush_delete_pacing"].Item);
            Assert.AreEqual(10L, DefaultPropertyManager.DefaultLongProperties["teleport_flush_delete_rate"].Item);

            Assert.AreEqual(TeleportFlushPolicy.DefaultScope, DefaultPropertyManager.DefaultLongProperties["teleport_flush_scope"].Item);
            Assert.AreEqual(TeleportFlushPolicy.DefaultPacing, DefaultPropertyManager.DefaultBooleanProperties["teleport_flush_delete_pacing"].Item);
            Assert.AreEqual(TeleportFlushPolicy.DefaultRate, DefaultPropertyManager.DefaultLongProperties["teleport_flush_delete_rate"].Item);

            foreach (var key in new[] { "teleport_flush_scope", "teleport_flush_delete_rate" })
                StringAssert.StartsWith(DefaultPropertyManager.DefaultLongProperties[key].Description, "(non-retail function)");

            StringAssert.StartsWith(DefaultPropertyManager.DefaultBooleanProperties["teleport_flush_delete_pacing"].Description, "(non-retail function)");
        }

        [TestMethod]
        public void ReadSettings_ReadsAndNormalizesSeededValues()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyLong("teleport_flush_scope", 2));
                Assert.IsTrue(PropertyManager.ModifyBool("teleport_flush_delete_pacing", true));
                Assert.IsTrue(PropertyManager.ModifyLong("teleport_flush_delete_rate", 0));

                var settings = Player.ReadTeleportFlushSettings();

                Assert.AreEqual(TeleportFlushScope.Ledger, settings.Scope);
                Assert.IsTrue(settings.Pacing);
                Assert.AreEqual(1, settings.Rate, "rate 0 counts as 1");
                Assert.AreEqual(TeleportFlushScope.Ledger, Player.ReadTeleportFlushScope());

                Assert.IsTrue(PropertyManager.ModifyLong("teleport_flush_scope", 7));
                Assert.AreEqual(TeleportFlushScope.Legacy, Player.ReadTeleportFlushSettings().Scope, "out of range reads as 1");
                Assert.AreEqual(TeleportFlushScope.Legacy, Player.ReadTeleportFlushScope());
            }
            finally
            {
                PropertyManager.ModifyLong("teleport_flush_scope", TeleportFlushPolicy.DefaultScope);
                PropertyManager.ModifyBool("teleport_flush_delete_pacing", TeleportFlushPolicy.DefaultPacing);
                PropertyManager.ModifyLong("teleport_flush_delete_rate", TeleportFlushPolicy.DefaultRate);
            }
        }
    }
}
