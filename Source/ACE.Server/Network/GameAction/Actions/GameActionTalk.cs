using System;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Network.GameAction.Actions
{
    public static class GameActionTalk
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [GameAction(GameActionType.Talk)]
        public static void Handle(ClientMessage clientMessage, Session session)
        {
            var message = clientMessage.Payload.ReadString16L();

            // Fix round B, B1: /mule is spelled with a slash everywhere it is documented (MuleCommands.cs's
            // own usage strings, DESIGN 13, every other player command in this repo), but acclient only
            // recognises commands it ships with baked in. If "mule" is not in that built-in list, "/mule
            // grant Bob withdraw" is not executed at all - it falls all the way through to
            // session.Player.HandleActionTalk(message) below and is broadcast as ordinary LOCAL CHAT,
            // exactly like the /blink case just below except worse: it discloses who the player shares
            // vault access with to everyone in range. Intercepted here, before the "@" branch, and
            // rewritten onto the same @-command path /mule already uses - this closes both failure modes
            // at once (the command works no matter what the client recognises, and the mistyped form can
            // never reach chat) without needing anything from the client. No other command gets this
            // treatment; IsMuleSlashCommand's own remarks explain why this one earned it.
            if (MuleCommands.IsMuleSlashCommand(message))
                message = "@" + message.Substring(1);

            // The blink plugin's own command is unknown to acclient, so the client sends it on as ordinary
            // chat. That makes it free attribution: it names the account before anyone walks through anything.
            if (message.StartsWith("/blink", StringComparison.OrdinalIgnoreCase))
            {
                var talker = session.Player;
                var loc = talker?.Location?.ToLOCString() ?? "unknown";

                PlayerManager.BroadcastToAuditChannel((ACE.Server.WorldObjects.Player)null, $"[BLINK] {talker?.Name} (account {session.Account}) used the /blink command at {loc}");
            }

            if (message.StartsWith("@"))
            {
                // WaffleACE slow-tick capture: from here on this is a command, not chat, so the ga_Talk scope
                // is re-labelled - cmd_unknown until a handler is resolved and about to run, then that
                // handler's registered name just before it does. A relabel only, never a second charge; see
                // InboundOpcodeProfile.RelabelAsCommand.
                InboundOpcodeProfile.RelabelAsCommand(null);

                string commandRaw = message.Remove(0, 1);
                CommandHandlerResponse response = CommandHandlerResponse.InvalidCommand;
                CommandHandlerInfo commandHandler = null;
                string command = null;
                string[] parameters = null;

                try
                {
                    CommandManager.ParseCommand(message.Remove(0, 1), out command, out parameters);
                }
                catch (Exception ex)
                {
                    log.Error($"Exception while parsing command: {commandRaw}", ex);
                    return;
                }

                try
                {
                    response = CommandManager.GetCommandHandler(session, command, parameters, out commandHandler);
                }
                catch (Exception ex)
                {
                    log.Error($"Exception while getting command handler for: {commandRaw}", ex);
                }

                if (response == CommandHandlerResponse.Ok || response == CommandHandlerResponse.SudoOk)
                    InboundOpcodeProfile.RelabelAsCommand(commandHandler);

                if (response == CommandHandlerResponse.Ok)
                {
                    try
                    {
                        CommandManager.LogCommandAudit(session, commandHandler, parameters, false);
                        if (commandHandler.Attribute.IncludeRaw)
                        {
                            parameters = CommandManager.StuffRawIntoParameters(message.Remove(0, 1), command, parameters);
                        }
                        ((CommandHandler)commandHandler.Handler).Invoke(session, parameters);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Exception while invoking command handler for: {commandRaw}", ex);
                    }
                }
                else if (response == CommandHandlerResponse.SudoOk)
                {
                    string[] sudoParameters = new string[parameters.Length - 1];
                    for (int i = 1; i < parameters.Length; i++)
                        sudoParameters[i - 1] = parameters[i];
                    try
                    {
                        CommandManager.LogCommandAudit(session, commandHandler, sudoParameters, true);
                        if (commandHandler.Attribute.IncludeRaw)
                        {
                            parameters = CommandManager.StuffRawIntoParameters(message.Remove(0, 1), command, parameters);
                        }
                        ((CommandHandler)commandHandler.Handler).Invoke(session, sudoParameters);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Exception while invoking command handler for: {commandRaw}", ex);
                    }
                }
                else
                {
                    switch (response)
                    {
                        case CommandHandlerResponse.InvalidCommand:
                            session.Network.EnqueueSend(new GameMessageSystemChat($"Unknown command: {command}", ChatMessageType.Help));
                            break;
                        case CommandHandlerResponse.InvalidParameterCount:
                            session.Network.EnqueueSend(new GameMessageSystemChat($"Invalid parameter count, got {parameters.Length}, expected {commandHandler.Attribute.ParameterCount}!", ChatMessageType.Help));
                            session.Network.EnqueueSend(new GameMessageSystemChat($"@{commandHandler.Attribute.Command} - {commandHandler.Attribute.Description}", ChatMessageType.Broadcast));
                            session.Network.EnqueueSend(new GameMessageSystemChat($"Usage: @{commandHandler.Attribute.Command} {commandHandler.Attribute.Usage}", ChatMessageType.Broadcast));
                            break;
                        default:
                            break;
                    }
                }
            }
            else
            {
                session.Player.HandleActionTalk(message);
            }
        }
    }
}
