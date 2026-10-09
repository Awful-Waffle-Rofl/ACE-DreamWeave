
using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    internal static class CommandHandlerHelper
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// This will determine where a command handler should output to, the console or a client session.<para />
        /// If the session is null, the output will be sent to the console. If the session is not null, and the session.Player is in the world, it will be sent to the session.<para />
        /// Messages sent to the console will be sent using log.Info()
        /// </summary>
        public static void WriteOutputInfo(Session session, string output, ChatMessageType chatMessageType = ChatMessageType.Broadcast)
        {
            if (session != null)
            {
                if (session.State == Network.Enum.SessionState.WorldConnected && session.Player != null)
                    ChatPacket.SendServerMessage(session, output, chatMessageType);
            }
            else
            {
                // The session path is captured once, at NetworkSession.EnqueueSend (PLAN-P4.md 3.3).
                Web.WebCommandContext.TryCapture(null, Web.WebCommandContext.LevelInfo, output);
                log.Info(output);
            }
        }

        /// <summary>
        /// This will determine where a command handler should output to, the console or a client session.<para />
        /// If the session is null, the output will be sent to the console. If the session is not null, and the session.Player is in the world, it will be sent to the session.<para />
        /// Messages sent to the console will be sent using log.Debug()
        /// </summary>
        public static void WriteOutputDebug(Session session, string output, ChatMessageType chatMessageType = ChatMessageType.Broadcast)
        {
            if (session != null)
            {
                if (session.State == Network.Enum.SessionState.WorldConnected && session.Player != null)
                    ChatPacket.SendServerMessage(session, output, chatMessageType);
            }
            else
            {
                Web.WebCommandContext.TryCapture(null, Web.WebCommandContext.LevelDebug, output);
                log.Debug(output);
            }
        }

        /// <summary>
        /// This will determine where a command handler should output to, the console or a client session.<para />
        /// If the session is null, the output will be sent to the console. If the session is not null, and the session.Player is in the world, it will be sent to the session.<para />
        /// Messages sent to the console will be sent using log.Debug()
        /// </summary>
        public static void WriteOutputError(Session session, string output, ChatMessageType chatMessageType = ChatMessageType.Broadcast)
        {
            if (session != null)
            {
                if (session.State == Network.Enum.SessionState.WorldConnected && session.Player != null)
                    ChatPacket.SendServerMessage(session, output, chatMessageType);
            }
            else
            {
                Web.WebCommandContext.TryCapture(null, Web.WebCommandContext.LevelError, output);
                log.Error(output);
            }
        }

        /// <summary>
        /// Returns the last appraised WorldObject
        /// </summary>
        public static WorldObject GetLastAppraisedObject(Session session)
        {
            var targetID = session.Player.RequestedAppraisalTarget;
            if (targetID == null)
            {
                WriteOutputInfo(session, "GetLastAppraisedObject() - no appraisal target");
                return null;
            }

            var target = session.Player.FindObject(targetID.Value, Player.SearchLocations.Everywhere, out _, out _, out _);
            if (target == null)
            {
                WriteOutputInfo(session, $"GetLastAppraisedObject() - couldn't find {targetID:X8}");
                return null;
            }
            return target;
        }
    }
}
