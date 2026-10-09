using ACE.Entity.Enum.Properties;
using ACE.Server.Network.Sequence;
using ACE.Server.WorldObjects;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessageSetStackSize : GameMessage
    {
        public GameMessageSetStackSize(WorldObject worldObject)
            : base(GameMessageOpcode.SetStackSize, GameMessageGroup.UIQueue, 17)
        {
            Writer.Write(worldObject.Sequences.GetNextSequence(SequenceType.UpdatePropertyInt, PropertyInt.StackSize));
            Writer.WriteGuid(worldObject.Guid);
            Writer.Write((uint)(worldObject.StackSize ?? 0));
            // ClientValue, not Value: a stack split or merge re-sends the item's Value, and the raw 0 of a
            // zero-value stackable would undo the sell-pane spoof (see WorldObject.ClientValue).
            Writer.Write((uint)worldObject.ClientValue);
        }
    }
}
