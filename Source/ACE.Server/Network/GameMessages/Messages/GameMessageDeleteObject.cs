using ACE.Server.Network.Sequence;
using ACE.Server.WorldObjects;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessageDeleteObject : GameMessage
    {
        /// <summary>The deleted object's guid. Bookkeeping only (ClientObjectBook), never serialized again.</summary>
        public uint ObjectId { get; }

        public GameMessageDeleteObject(WorldObject worldObject) : base(GameMessageOpcode.ObjectDelete, GameMessageGroup.SmartboxQueue, 12)
        {
            Writer.WriteGuid(worldObject.Guid);
            Writer.Write(worldObject.Sequences.GetCurrentSequence(SequenceType.ObjectInstance));
            Writer.Align();

            ObjectId = worldObject.Guid.Full;
        }

        /// <summary>
        /// A delete for an object this server may no longer hold (teleport_flush_scope 2 ledger fire), rebuilt from
        /// the guid and the ObjectInstance sequence captured when its CreateObject was built. Writes exactly what
        /// the WorldObject overload writes for the same guid and sequence.
        /// </summary>
        public GameMessageDeleteObject(uint guid, byte[] instanceSequence) : base(GameMessageOpcode.ObjectDelete, GameMessageGroup.SmartboxQueue, 12)
        {
            Writer.Write(guid);
            Writer.Write(instanceSequence);
            Writer.Align();

            ObjectId = guid;
        }
    }
}
