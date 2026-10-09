using ACE.Server.WorldObjects;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessagePlayerTeleport : GameMessage
    {
        public GameMessagePlayerTeleport(Player player)
            : base(GameMessageOpcode.PlayerTeleport, GameMessageGroup.SmartboxQueue, 21)
        {
            var teleportSequence = player.Sequences.GetNextSequence(Sequence.SequenceType.ObjectTeleport);

            // the player's own client will accept this value; every self-bound position packet from now on
            // must carry it, even while observer-only teleport packets advance the live sequence past it
            player.SelfTeleportSequence = teleportSequence;

            Writer.Write(teleportSequence);
            Writer.Align();
        }
    }
}
