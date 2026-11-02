using ACE.Server.Network.Sequence;
using ACE.Server.WorldObjects;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessagePickupEvent : GameMessage
    {
        /// <summary>The picked-up object's guid. Bookkeeping only (ClientObjectBook), never serialized again.</summary>
        public uint ObjectId { get; }

        public GameMessagePickupEvent(WorldObject targetItem)
            : base(GameMessageOpcode.PickupEvent, GameMessageGroup.SmartboxQueue, 12)
        {
            Writer.Write(targetItem.Guid.Full);
            Writer.Write(targetItem.Sequences.GetCurrentSequence(SequenceType.ObjectInstance));
            Writer.Write(targetItem.Sequences.GetNextSequence(SequenceType.ObjectPosition));

            ObjectId = targetItem.Guid.Full;
        }
    }
}
