using ACE.Entity.Enum;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessageSystemChat : GameMessage
    {
        /// <summary>
        /// The text as given, for the web command console's capture at NetworkSession.EnqueueSend
        /// (PLAN-P4.md section 3.3). No wire change. Any new constructor or subclass must set it too, or
        /// its sends silently escape capture.
        /// </summary>
        public string Text { get; }

        public GameMessageSystemChat(string message, ChatMessageType chatMessageType)
            : base(GameMessageOpcode.ServerMessage, GameMessageGroup.UIQueue)
        {
            Text = message;
            Writer.WriteString16L(message);
            Writer.Write((int)chatMessageType);
        }
    }
}
