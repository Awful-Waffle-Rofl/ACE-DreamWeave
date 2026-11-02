using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Motion = ACE.Server.Entity.Motion;

namespace ACE.Server.Tests.Outbound
{
    /// <summary>
    /// Every offset OutboundMessageDecode reads, proven against a REAL constructed message of each opcode it names,
    /// plus the never-throws cases and the CreateObject capture (WeenieClassId, SetupId) byte identity.
    /// </summary>
    [TestClass]
    public class OutboundMessageDecodeTests
    {
        [ClassInitialize]
        public static void RegisterCodePages(TestContext context)
        {
            // CreateObject serializes the name with code page 1252, which the test host does not register on its own
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        // no SetupTableId: a creature's model data reads its setup from the portal dat, which tests do not load
        private static Creature Creature() => TestCreatures.CreateQuestBearer();

        private static WorldObject Item()
        {
            var weenie = new Weenie
            {
                WeenieClassId = TestCreatures.NextWcid(),
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Item" } },
            };

            return new GenericObject(weenie, new ObjectGuid(0x7E200000 + TestCreatures.NextWcid() % 0x10000));
        }

        private static (uint guid, uint aux, ushort ev) Decode(GameMessage message)
        {
            OutboundMessageDecode.Decode(message, out var guid, out var aux, out var ev);
            return (guid, aux, ev);
        }

        private static void AssertObject(GameMessage message, GameMessageOpcode opcode, uint guid, uint aux = 0)
        {
            Assert.AreEqual(opcode, message.Opcode);
            Assert.AreEqual((uint)opcode, BitConverter.ToUInt32(message.Data.ToArray(), 0), "the buffer starts with the opcode");

            var decoded = Decode(message);
            Assert.AreEqual(guid, decoded.guid, $"{opcode} guid");
            Assert.AreEqual(aux, decoded.aux, $"{opcode} aux");
            Assert.AreEqual((ushort)0, decoded.ev, $"{opcode} event type");
        }

        [TestMethod]
        public void ObjectCreate_GuidAtFour_AuxIsTheSetup()
        {
            // a generic item's model data does not read the dat, so it can carry a real setup id
            var item = Item();
            item.SetupTableId = 0x02000A1C;
            var create = new GameMessageCreateObject(item);

            Assert.AreEqual(item.WeenieClassId, create.WeenieClassId);
            Assert.AreEqual(0x02000A1Cu, create.SetupId);
            AssertObject(create, GameMessageOpcode.ObjectCreate, item.Guid.Full, 0x02000A1Cu);

            // a creature without a setup: aux 0
            var creature = Creature();
            var creatureCreate = new GameMessageCreateObject(creature);
            Assert.AreEqual(creature.WeenieClassId, creatureCreate.WeenieClassId);
            AssertObject(creatureCreate, GameMessageOpcode.ObjectCreate, creature.Guid.Full, 0);
        }

        /// <summary>The capture of WeenieClassId and SetupId must not change a byte of the create.</summary>
        [TestMethod]
        public void ObjectCreate_CaptureLeavesTheBytesUnchanged()
        {
            var item = Item();
            item.SetupTableId = 0x02000A1C;

            byte[] expected;
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write((uint)GameMessageOpcode.ObjectCreate);
                item.SerializeCreateObject(writer, false, false);
                writer.Flush();
                expected = stream.ToArray();
            }

            CollectionAssert.AreEqual(expected, new GameMessageCreateObject(item).Data.ToArray());
        }

        [TestMethod]
        public void ObjectMessages_GuidAtFour()
        {
            var creature = Creature();
            var guid = creature.Guid.Full;

            AssertObject(new GameMessageUpdateObject(creature), GameMessageOpcode.UpdateObject, guid);
            AssertObject(new GameMessageObjDescEvent(creature), GameMessageOpcode.ObjDescEvent, guid);
            AssertObject(new GameMessageDeleteObject(creature), GameMessageOpcode.ObjectDelete, guid);
            AssertObject(new GameMessageUpdateMotion(creature, new Motion(MotionStance.NonCombat)), GameMessageOpcode.Motion, guid);
            AssertObject(new GameMessageUpdatePosition(creature), GameMessageOpcode.UpdatePosition, guid);
            AssertObject(new GameMessageVectorUpdate(creature), GameMessageOpcode.VectorUpdate, guid);
            AssertObject(new GameMessagePickupEvent(creature), GameMessageOpcode.PickupEvent, guid);
        }

        [TestMethod]
        public void EffectAndStateMessages_GuidAtFour_AuxAtEight()
        {
            var creature = Creature();
            var guid = creature.Guid.Full;

            AssertObject(new GameMessageScript(creature.Guid, PlayScript.Launch), GameMessageOpcode.PlayEffect, guid, (uint)PlayScript.Launch);
            AssertObject(new GameMessagePlayScriptId(creature.Guid, 0x33000123), GameMessageOpcode.PlayScriptId, guid, 0x33000123);
            AssertObject(new GameMessageSound(creature.Guid, Sound.HitFlesh1), GameMessageOpcode.Sound, guid, (uint)Sound.HitFlesh1);
            AssertObject(new GameMessageSetState(creature, PhysicsState.Ethereal | PhysicsState.ReportCollisions), GameMessageOpcode.SetState, guid, (uint)(PhysicsState.Ethereal | PhysicsState.ReportCollisions));

            var item = Item();
            AssertObject(new GameMessageParentEvent(creature, item), GameMessageOpcode.ParentEvent, guid, item.Guid.Full);
        }

        [TestMethod]
        public void PublicUpdates_GuidAfterTheSequence_ThenThePropertyId()
        {
            var creature = Creature();
            var guid = creature.Guid.Full;

            AssertObject(new GameMessagePublicUpdatePropertyInt(creature, PropertyInt.Level, 50), GameMessageOpcode.PublicUpdatePropertyInt, guid, (uint)PropertyInt.Level);
            AssertObject(new GameMessagePublicUpdatePropertyInt64(creature, PropertyInt64.TotalExperience, 5), GameMessageOpcode.PublicUpdatePropertyInt64, guid, (uint)PropertyInt64.TotalExperience);
            AssertObject(new GameMessagePublicUpdatePropertyBool(creature, PropertyBool.Stuck, true), GameMessageOpcode.PublicUpdatePropertyBool, guid, (uint)PropertyBool.Stuck);
            AssertObject(new GameMessagePublicUpdatePropertyFloat(creature, PropertyFloat.HeartbeatInterval, 5.0), GameMessageOpcode.PublicUpdatePropertyFloat, guid, (uint)PropertyFloat.HeartbeatInterval);
            AssertObject(new GameMessagePublicUpdatePropertyDataID(creature, PropertyDataId.Icon, 0x06001234), GameMessageOpcode.PublicUpdatePropertyDataID, guid, (uint)PropertyDataId.Icon);
            AssertObject(new GameMessagePublicUpdateInstanceID(creature, PropertyInstanceId.Wielder, new ObjectGuid(0x50000001)), GameMessageOpcode.PublicUpdateInstanceId, guid, (uint)PropertyInstanceId.Wielder);
            AssertObject(new GameMessagePublicUpdatePosition(creature, PositionType.Instantiation, creature.Location), GameMessageOpcode.PublicUpdatePosition, guid, (uint)PositionType.Instantiation);
            AssertObject(new GameMessagePublicUpdateVital(creature, PropertyAttribute2nd.MaxHealth, 1, 2, 3, 4), GameMessageOpcode.PublicUpdateVital, guid, (uint)PropertyAttribute2nd.MaxHealth);
        }

        [TestMethod]
        public void PublicUpdatePropertyString_WritesThePropertyIdBeforeTheGuid()
        {
            var creature = Creature();

            AssertObject(new GameMessagePublicUpdatePropertyString(creature, PropertyString.Name, "Renamed"), GameMessageOpcode.PublicUpdatePropertyString,
                creature.Guid.Full, (uint)PropertyString.Name);
        }

        [TestMethod]
        public void PrivateUpdates_PropertyIdAfterTheSequence_NoGuid()
        {
            var creature = Creature();

            AssertObject(new GameMessagePrivateUpdatePropertyInt(creature, PropertyInt.Level, 50), GameMessageOpcode.PrivateUpdatePropertyInt, 0, (uint)PropertyInt.Level);
            AssertObject(new GameMessagePrivateUpdatePropertyInt64(creature, PropertyInt64.TotalExperience, 5), GameMessageOpcode.PrivateUpdatePropertyInt64, 0, (uint)PropertyInt64.TotalExperience);
            AssertObject(new GameMessagePrivateUpdatePropertyBool(creature, PropertyBool.Stuck, true), GameMessageOpcode.PrivateUpdatePropertyBool, 0, (uint)PropertyBool.Stuck);
            AssertObject(new GameMessagePrivateUpdatePropertyFloat(creature, PropertyFloat.HeartbeatInterval, 5.0), GameMessageOpcode.PrivateUpdatePropertyFloat, 0, (uint)PropertyFloat.HeartbeatInterval);
            AssertObject(new GameMessagePrivateUpdatePropertyString(creature, PropertyString.Name, "Renamed"), GameMessageOpcode.PrivateUpdatePropertyString, 0, (uint)PropertyString.Name);
            AssertObject(new GameMessagePrivateUpdateDataID(creature, PropertyDataId.Icon, 0x06001234), GameMessageOpcode.PrivateUpdatePropertyDataID, 0, (uint)PropertyDataId.Icon);
            AssertObject(new GameMessagePrivateUpdateInstanceID(creature, PropertyInstanceId.Wielder, 0x50000001), GameMessageOpcode.PrivateUpdatePropertyInstanceID, 0, (uint)PropertyInstanceId.Wielder);
            AssertObject(new GameMessagePrivateUpdatePosition(creature, PositionType.Instantiation, creature.Location), GameMessageOpcode.PrivateUpdatePosition, 0, (uint)PositionType.Instantiation);
            AssertObject(new GameMessagePrivateUpdateAttribute(creature, creature.Attributes[PropertyAttribute.Strength]), GameMessageOpcode.PrivateUpdateAttribute, 0, (uint)PropertyAttribute.Strength);
            AssertObject(new GameMessagePrivateUpdateAttribute2ndLevel(creature, Vital.Health, 5), GameMessageOpcode.PrivateUpdateAttribute2ndLevel, 0, (uint)Vital.Health);
            AssertObject(new GameMessagePrivateUpdateVital(creature, creature.Health), GameMessageOpcode.PrivateUpdateVital, 0, (uint)creature.Health.Vital);

            var skill = creature.GetCreatureSkill(Skill.UnarmedCombat);
            AssertObject(new GameMessagePrivateUpdateSkill(creature, skill), GameMessageOpcode.PrivateUpdateSkill, 0, (uint)skill.Skill);
        }

        /// <summary>The odd/even rule for 0x02CD-0x02E9 matches every opcode name in that range.</summary>
        [TestMethod]
        public void PropertyUpdateRange_OddIsPrivate_EvenIsPublic()
        {
            var inRange = Enum.GetValues(typeof(GameMessageOpcode)).Cast<GameMessageOpcode>()
                .Where(op => (ushort)op >= 0x02CD && (ushort)op <= 0x02E9)
                .Distinct()
                .ToList();

            Assert.IsTrue(inRange.Count >= 20);

            foreach (var op in inRange)
            {
                var name = op.ToString();
                if (((ushort)op & 1) != 0)
                    StringAssert.StartsWith(name, "PrivateUpdate", $"0x{(ushort)op:X4}");
                else
                    StringAssert.StartsWith(name, "PublicUpdate", $"0x{(ushort)op:X4}");
            }
        }

        [TestMethod]
        public void GameEvent_EventTypeAndFirstBodyValue_NoGuid()
        {
            var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
            session.GameEventSequence = 7;

            var message = new GameEventWeenieError(session, WeenieError.YouAreTooEncumbered);

            Assert.AreEqual(GameMessageOpcode.GameEvent, message.Opcode);

            var decoded = Decode(message);
            Assert.AreEqual((ushort)message.EventType, decoded.ev);
            Assert.AreEqual((ushort)GameEventType.WeenieError, decoded.ev);
            Assert.AreEqual((uint)WeenieError.YouAreTooEncumbered, decoded.aux);
            Assert.AreEqual(0u, decoded.guid);

            // a body-less event: no aux
            var ping = new GameEventPingResponse(session);
            Assert.AreEqual(((ushort)GameEventType.PingResponse, 0u), (Decode(ping).ev, Decode(ping).aux));
        }

        [TestMethod]
        public void UnknownOpcode_AndTruncatedBuffers_GiveZeros_AndNeverThrow()
        {
            var chat = new GameMessageSystemChat("hello", ChatMessageType.Broadcast);
            Assert.AreEqual((0u, 0u, (ushort)0), Decode(chat));

            var bytes = new byte[] { 0x45, 0xF7, 0, 0, 0x01, 0x02 }; // a create cut off inside its guid
            OutboundMessageDecode.Decode(0xF745, bytes, 0, bytes.Length, out var guid, out var aux, out var ev);
            Assert.AreEqual((0u, 0u, (ushort)0), (guid, aux, ev));

            // a sound with its guid but no aux
            var sound = new byte[] { 0x50, 0xF7, 0, 0, 0x78, 0x56, 0x34, 0x12, 0x01 };
            OutboundMessageDecode.Decode(0xF750, sound, 0, sound.Length, out guid, out aux, out ev);
            Assert.AreEqual((0x12345678u, 0u), (guid, aux));

            // a length shorter than the array is honoured
            OutboundMessageDecode.Decode(0xF750, sound, 0, 7, out guid, out aux, out ev);
            Assert.AreEqual(0u, guid);

            // a game event cut off before its body
            var gameEvent = new byte[14];
            OutboundMessageDecode.Decode(0xF7B0, gameEvent, 0, gameEvent.Length, out guid, out aux, out ev);
            Assert.AreEqual((0u, 0u, (ushort)0), (guid, aux, ev));

            // nonsense arguments
            OutboundMessageDecode.Decode(0xF745, null, 0, 10, out guid, out aux, out ev);
            OutboundMessageDecode.Decode(0xF745, bytes, -1, 2, out guid, out aux, out ev);
            OutboundMessageDecode.Decode(0xF745, bytes, 4, 100, out guid, out aux, out ev);
            OutboundMessageDecode.Decode(0xF745, bytes, 100, 0, out guid, out aux, out ev);
            OutboundMessageDecode.Decode(0xF745, bytes, 0, -5, out guid, out aux, out ev);
            OutboundMessageDecode.Decode(null, out guid, out aux, out ev);
            Assert.AreEqual((0u, 0u, (ushort)0), (guid, aux, ev));
        }
    }
}
