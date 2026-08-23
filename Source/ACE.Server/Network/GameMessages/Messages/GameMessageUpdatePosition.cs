using System;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessageUpdatePosition : GameMessage
    {
        public PositionPack PositionPack;

        /// <param name="adminMove">advance ObjectTeleport for every recipient - a real teleport</param>
        /// <param name="audience">for a Player, who this message goes to - a Player broadcast must be built once
        /// per audience, never Shared (see PositionPack)</param>
        public GameMessageUpdatePosition(WorldObject worldObject, bool adminMove = false, PositionAudience audience = PositionAudience.Shared)
            : base(GameMessageOpcode.UpdatePosition, GameMessageGroup.SmartboxQueue, 68) // 68 is the max seen in retail pcaps
        {
            //Console.WriteLine($"Sending UpdatePosition for {worldObject.Name}");

            // todo: avoid create intermediate object
            PositionPack = new PositionPack(worldObject, adminMove, audience);

            Writer.WriteGuid(worldObject.Guid);
            Writer.Write(PositionPack);
        }
    }
}
