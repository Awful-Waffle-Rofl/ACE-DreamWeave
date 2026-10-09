using System;
using System.Reflection;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Managers.Market;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;

using log4net;

namespace ACE.Server.Network.Handlers
{
    public static class TurbineChatHandler
    {
        [GameMessage(GameMessageOpcode.TurbineChat, SessionState.WorldConnected)]
        public static void TurbineChatReceived(ClientMessage clientMessage, Session session)
        {
            if (!PropertyManager.GetBool("use_turbine_chat").Item)
                return;

            clientMessage.Payload.ReadUInt32(); // Bytes to follow
            var chatBlobType = (ChatNetworkBlobType)clientMessage.Payload.ReadUInt32();
            var chatBlobDispatchType = (ChatNetworkBlobDispatchType)clientMessage.Payload.ReadUInt32();
            clientMessage.Payload.ReadUInt32(); // Always 1
            clientMessage.Payload.ReadUInt32(); // Always 0
            clientMessage.Payload.ReadUInt32(); // Always 0
            clientMessage.Payload.ReadUInt32(); // Always 0
            clientMessage.Payload.ReadUInt32(); // Always 0
            clientMessage.Payload.ReadUInt32(); // Bytes to follow

            if (session.Player.IsGagged)
            {
                session.Player.SendGagError();
                return;
            }

            if (chatBlobType == ChatNetworkBlobType.NETBLOB_REQUEST_BINARY)
            {
                var contextId = clientMessage.Payload.ReadUInt32(); // 0x01 - 0x71 (maybe higher), typically though 0x01 - 0x0F
                clientMessage.Payload.ReadUInt32(); // Always 2
                clientMessage.Payload.ReadUInt32(); // Always 2
                var channelID = clientMessage.Payload.ReadUInt32();

                int messageLen = clientMessage.Payload.ReadByte();
                if ((messageLen & 0x80) > 0) // PackedByte
                {
                    byte lowbyte = clientMessage.Payload.ReadByte();
                    messageLen = ((messageLen & 0x7F) << 8) | lowbyte;
                }
                var messageBytes = clientMessage.Payload.ReadBytes(messageLen * 2);
                var message = Encoding.Unicode.GetString(messageBytes);

                clientMessage.Payload.ReadUInt32(); // Always 0x0C
                var senderID = clientMessage.Payload.ReadUInt32();
                clientMessage.Payload.ReadUInt32(); // Always 0
                var chatType = (ChatType)clientMessage.Payload.ReadUInt32();


                if (!TryResolveChatType(chatBlobDispatchType, channelID, chatType, out var adjustedchatType, out var adjustedChannelID))
                {
                    // channelID does not name a known room. Refuse rather than deliver: the old code left
                    // the client-supplied chatType untouched here, which - for the "available to all
                    // players" delivery branch this falls into - reproduced the same channel-spoofing bug
                    // this handler exists to close. Nothing is delivered, relayed, or logged.
                    log.DebugFormat("[CHAT] Refused TurbineChat: ChannelID ({0}) is not a known room | ChatType: {1} | ChatNetworkBlobDispatchType: {2}", channelID, chatType, chatBlobDispatchType);
                    return;
                }

                if (channelID != adjustedChannelID)
                    log.DebugFormat("[CHAT] ChannelID ({0}) was adjusted to {1} | ChatNetworkBlobDispatchType: {2}", channelID, adjustedChannelID, chatBlobDispatchType);

                if (chatType != adjustedchatType)
                    log.DebugFormat("[CHAT] ChatType ({0}) was adjusted to {1} | ChatNetworkBlobDispatchType: {2}", chatType, adjustedchatType, chatBlobDispatchType);

                // Analytics is keyed off adjustedchatType, NEVER the raw client-supplied chatType.
                // The handler re-derives the channel above precisely because the client value is not
                // trusted; logging the raw one would let a client file a message under any channel
                // it liked. Allegiance maps to null and is dropped by the allowlist in RecordChat.
                AnalyticsManager.RecordChat(session.Player, adjustedchatType switch
                {
                    ChatType.General => "general",
                    ChatType.Trade => "trade",
                    ChatType.LFG => "lfg",
                    ChatType.Roleplay => "roleplay",
                    ChatType.Society or ChatType.SocietyCelHan or ChatType.SocietyEldWeb or ChatType.SocietyRadBlo => "society",
                    ChatType.Olthoi => "olthoi",
                    _ => null
                }, message);

                var gameMessageTurbineChat = new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_EVENT_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, adjustedChannelID, session.Player.Name, message, senderID, adjustedchatType);

                if (adjustedChannelID > TurbineChatChannel.Olthoi || adjustedChannelID == TurbineChatChannel.Allegiance) // Channel must be an allegiance channel
                {
                    //var allegiance = AllegianceManager.FindAllegiance(channelID);
                    var allegiance = AllegianceManager.GetAllegiance(session.Player);
                    if (allegiance != null)
                    {
                        // is sender booted / gagged?
                        if (!allegiance.IsMember(session.Player.Guid)) return;
                        if (allegiance.IsFiltered(session.Player.Guid)) return;

                        // iterate through all allegiance members
                        foreach (var member in allegiance.Members.Keys)
                        {
                            // is this allegiance member online?
                            var online = PlayerManager.GetOnlinePlayer(member);
                            if (online == null)
                                continue;

                            // is this member booted / gagged?
                            if (allegiance.IsFiltered(member) || online.SquelchManager.Squelches.Contains(session.Player, ChatMessageType.Allegiance)) continue;

                            // does this player have allegiance chat filtered?
                            if (!online.GetCharacterOption(CharacterOption.ListenToAllegianceChat)) continue;

                            online.Session.Network.EnqueueSend(gameMessageTurbineChat);
                        }

                        session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, adjustedchatType));
                    }
                }
                else if (adjustedChannelID == TurbineChatChannel.Olthoi) // Channel must be the Olthoi play channel
                {
                    if (!session.Player.IsOlthoiPlayer) return;

                    if (PropertyManager.GetBool("chat_disable_olthoi").Item)
                    {
                        HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, string.Empty);
                        return;
                    }

                    if (PropertyManager.GetBool("chat_echo_only").Item)
                    {
                        session.Network.EnqueueSend(gameMessageTurbineChat);
                        session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, adjustedchatType));
                        return;
                    }

                    //if (PropertyManager.GetBool("chat_requires_account_15days").Item && !session.Player.Account15Days)
                    //{
                    //    HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, "because this account is not 15 days old");
                    //    return;
                    //}

                    //var chat_requires_account_time_seconds = PropertyManager.GetLong("chat_requires_account_time_seconds").Item;
                    //if (chat_requires_account_time_seconds > 0 && (DateTime.UtcNow - session.Player.Account.CreateTime).TotalSeconds < chat_requires_account_time_seconds)
                    //{
                    //    HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, "because this account is not old enough");
                    //    return;
                    //}

                    //var chat_requires_player_age = PropertyManager.GetLong("chat_requires_player_age").Item;
                    //if (chat_requires_player_age > 0 && session.Player.Age < chat_requires_player_age)
                    //{
                    //    HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, "because this character has not been played enough");
                    //    return;
                    //}

                    //var chat_requires_player_level = PropertyManager.GetLong("chat_requires_player_level").Item;
                    //if (chat_requires_player_level > 0 && session.Player.Level < chat_requires_player_level)
                    //{
                    //    HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, $"because this character has reached level {chat_requires_player_level}");
                    //    return;
                    //}

                    foreach (var recipient in PlayerManager.GetAllOnline())
                    {
                        // handle filters
                        if (!recipient.IsOlthoiPlayer && !recipient.IsAdmin)
                            continue;

                        if (PropertyManager.GetBool("chat_disable_olthoi").Item)
                        {
                            if (PropertyManager.GetBool("chat_echo_reject").Item)
                                session.Network.EnqueueSend(gameMessageTurbineChat);

                            session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, adjustedchatType));
                            return;
                        }

                        if (recipient.SquelchManager.Squelches.Contains(session.Player, ChatMessageType.AllChannels))
                            continue;

                        recipient.Session.Network.EnqueueSend(gameMessageTurbineChat);
                    }

                    session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, adjustedchatType));
                }
                else if (adjustedChannelID >= TurbineChatChannel.Society) // Channel must be a society restricted channel
                {
                    var senderSociety = session.Player.Society;

                    //var adjustedChatType = senderSociety switch
                    //{
                    //    FactionBits.CelestialHand => ChatType.SocietyCelHan,
                    //    FactionBits.EldrytchWeb => ChatType.SocietyEldWeb,
                    //    FactionBits.RadiantBlood => ChatType.SocietyRadBlo,
                    //    _ => ChatType.Society
                    //};

                    //gameMessageTurbineChat = new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_EVENT_BINARY, channelID, session.Player.Name, message, senderID, adjustedChatType);

                    if (senderSociety == FactionBits.None)
                    {
                        ChatPacket.SendServerMessage(session, "You do not belong to a society.", ChatMessageType.Broadcast); // I don't know if this is how it was done on the live servers
                        return;
                    }

                    foreach (var recipient in PlayerManager.GetAllOnline())
                    {
                        // handle filters
                        if (senderSociety != recipient.Society && !recipient.IsAdmin)
                            continue;

                        if (!recipient.GetCharacterOption(CharacterOption.ListenToSocietyChat))
                            continue;

                        if (recipient.SquelchManager.Squelches.Contains(session.Player, ChatMessageType.AllChannels))
                            continue;

                        recipient.Session.Network.EnqueueSend(gameMessageTurbineChat);
                    }

                    session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, adjustedchatType));
                }
                else // Channel must be one of the channels available to all players
                {
                    if (session.Player.IsOlthoiPlayer)
                    {
                        //HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, "because this account is not 15 days old");
                        return;
                    }

                    if (PropertyManager.GetBool("chat_echo_only").Item)
                    {
                        session.Network.EnqueueSend(gameMessageTurbineChat);
                        session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, adjustedchatType));
                        return;
                    }

                    if (PropertyManager.GetBool("chat_requires_account_15days").Item && !session.Player.Account15Days)
                    {
                        HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, "because this account is not 15 days old");
                        return;
                    }

                    var chat_requires_account_time_seconds = PropertyManager.GetLong("chat_requires_account_time_seconds").Item;
                    if (chat_requires_account_time_seconds > 0 && (DateTime.UtcNow - session.Player.Account.CreateTime).TotalSeconds < chat_requires_account_time_seconds)
                    {
                        HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, "because this account is not old enough");
                        return;
                    }

                    var chat_requires_player_age = PropertyManager.GetLong("chat_requires_player_age").Item;
                    if (chat_requires_player_age > 0 && session.Player.Age < chat_requires_player_age)
                    {
                        HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, "because this character has not been played enough");
                        return;
                    }

                    var chat_requires_player_level = PropertyManager.GetLong("chat_requires_player_level").Item;
                    if (chat_requires_player_level > 0 && session.Player.Level < chat_requires_player_level)
                    {
                        HandleChatReject(session, contextId, chatType, gameMessageTurbineChat, $"because this character has not reached level {chat_requires_player_level}");
                        return;
                    }

                    foreach (var recipient in PlayerManager.GetAllOnline())
                    {
                        // handle filters
                        if (channelID == TurbineChatChannel.General && !recipient.GetCharacterOption(CharacterOption.ListenToGeneralChat) ||
                            channelID == TurbineChatChannel.Trade && !recipient.GetCharacterOption(CharacterOption.ListenToTradeChat) ||
                            channelID == TurbineChatChannel.LFG && !recipient.GetCharacterOption(CharacterOption.ListenToLFGChat) ||
                            channelID == TurbineChatChannel.Roleplay && !recipient.GetCharacterOption(CharacterOption.ListenToRoleplayChat))
                            continue;

                        if ((channelID == TurbineChatChannel.General && PropertyManager.GetBool("chat_disable_general").Item)
                            || (channelID == TurbineChatChannel.Trade && PropertyManager.GetBool("chat_disable_trade").Item)
                            || (channelID == TurbineChatChannel.LFG && PropertyManager.GetBool("chat_disable_lfg").Item)
                            || (channelID == TurbineChatChannel.Roleplay && PropertyManager.GetBool("chat_disable_roleplay").Item))
                        {
                            if (PropertyManager.GetBool("chat_echo_reject").Item)
                                session.Network.EnqueueSend(gameMessageTurbineChat);

                            session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, adjustedchatType));
                            return;
                        }

                        if (recipient.IsOlthoiPlayer)
                            continue;

                        if (recipient.SquelchManager.Squelches.Contains(session.Player, ChatMessageType.AllChannels))
                            continue;

                        recipient.Session.Network.EnqueueSend(gameMessageTurbineChat);
                    }

                    session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, adjustedchatType));
                }

                LogTurbineChat(adjustedChannelID, session.Player.Name, message, senderID, adjustedchatType);
            }
            else
                Console.WriteLine($"Unhandled TurbineChatHandler ChatNetworkBlobType: 0x{(uint)chatBlobType:X4}");
        }

        /// <summary>
        /// Resolves the chat type (and, for ASYNCMETHOD_SENDTOROOMBYNAME, the channel id) that a TurbineChat
        /// packet is actually delivered and logged/relayed under. Returns false, with no other out value
        /// trustworthy, when channelId does not name a known room - the caller must stop before delivery,
        /// analytics, and LogTurbineChat in that case.
        ///
        /// ASYNCMETHOD_SENDTOROOMBYNAME derives the channel id from the client-supplied chatType (there is no
        /// channel id to trust yet), then re-derives chatType from the resulting channel id. Its default arm
        /// always maps to TurbineChatChannel.General, so BYNAME always resolves.
        ///
        /// Every other dispatch type - including ASYNCMETHOD_SENDTOROOMBYID and any dispatch type not yet
        /// defined - derives chatType from channelId using the BYID rules, never from the client-supplied
        /// chatType. Delivery (see the ASYNCMETHOD_SENDTOROOMBYNAME-branch calls in TurbineChatReceived) is
        /// chosen by adjustedChannelId, not by chatType, so trusting the raw client chatType here - as the
        /// code used to for every dispatch type except BYNAME/BYID - let a crafted packet with channelID =
        /// Allegiance (or a society channel) and chatType = General deliver privately to the allegiance/society
        /// while being relayed to the public Discord General webhook and filed in analytics as "general".
        ///
        /// The BYID rules cover every channelId except 0: 1 (or any id above TurbineChatChannel.Olthoi) is
        /// Allegiance, 2-5 are General/Trade/LFG/Roleplay, 6-9 are Society, 10 is Olthoi. channelId 0 (and
        /// only channelId 0) falls through all of those checks - the old inline code left adjustedChatType at
        /// its client-supplied initial value in that case, which is the same trust-the-client bug for a
        /// channel that resolves to the unfiltered "available to all players" delivery branch. That is now
        /// refused instead of resolved.
        /// </summary>
        internal static bool TryResolveChatType(ChatNetworkBlobDispatchType dispatchType, uint channelId, ChatType clientChatType, out ChatType chatType, out uint adjustedChannelId)
        {
            adjustedChannelId = channelId;

            if (dispatchType == ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME)
            {
                adjustedChannelId = clientChatType switch
                {
                    ChatType.Allegiance     => TurbineChatChannel.Allegiance,
                    ChatType.General        => TurbineChatChannel.General,
                    ChatType.Trade          => TurbineChatChannel.Trade,
                    ChatType.LFG            => TurbineChatChannel.LFG,
                    ChatType.Roleplay       => TurbineChatChannel.Roleplay,
                    ChatType.Society        => TurbineChatChannel.Society,
                    ChatType.SocietyCelHan  => TurbineChatChannel.Society,
                    ChatType.SocietyEldWeb  => TurbineChatChannel.Society,
                    ChatType.SocietyRadBlo  => TurbineChatChannel.Society,
                    ChatType.Olthoi         => TurbineChatChannel.Olthoi,
                    _                       => TurbineChatChannel.General
                };

                chatType = (ChatType)adjustedChannelId;
                return true;
            }

            // BYID, and every other dispatch type: derive chatType from channelId, never from clientChatType.
            if (channelId > TurbineChatChannel.Olthoi || channelId == TurbineChatChannel.Allegiance) // Channel must be an allegiance channel
            {
                chatType = ChatType.Allegiance;
                return true;
            }
            if (channelId == TurbineChatChannel.Olthoi) // Channel must be the Olthoi play channel
            {
                chatType = ChatType.Olthoi;
                return true;
            }
            if (channelId >= TurbineChatChannel.Society) // Channel must be a society restricted channel
            {
                chatType = ChatType.Society;
                return true;
            }
            if (channelId == TurbineChatChannel.General) // Channel must be one of the channels available to all players
            {
                chatType = ChatType.General;
                return true;
            }
            if (channelId == TurbineChatChannel.Trade)
            {
                chatType = ChatType.Trade;
                return true;
            }
            if (channelId == TurbineChatChannel.LFG)
            {
                chatType = ChatType.LFG;
                return true;
            }
            if (channelId == TurbineChatChannel.Roleplay)
            {
                chatType = ChatType.Roleplay;
                return true;
            }

            // channelId does not name a known room (channelId == 0 is the only such value today).
            chatType = default;
            return false;
        }

        private static void HandleChatReject(Session session, uint contextId, ChatType chatType, GameMessageTurbineChat gameMessageTurbineChat, string rejectReason)
        {
            if (PropertyManager.GetBool("chat_echo_reject").Item)
                session.Network.EnqueueSend(gameMessageTurbineChat);

            if (PropertyManager.GetBool("chat_inform_reject").Item)
            {
                session.Network.EnqueueSend(new GameEventCommunicationTransientString(session, $"{chatType} is currently disabled{(string.IsNullOrEmpty(rejectReason) ? "" : $" for you {rejectReason}")}."));
                session.Network.EnqueueSend(new GameMessageSystemChat($"{chatType} is currently disabled{(string.IsNullOrEmpty(rejectReason) ? "" : $" for you {rejectReason}")}.", ChatMessageType.Broadcast));
            }

            session.Network.EnqueueSend(new GameMessageTurbineChat(ChatNetworkBlobType.NETBLOB_RESPONSE_BINARY, ChatNetworkBlobDispatchType.ASYNCMETHOD_SENDTOROOMBYNAME, contextId, null, null, 0, chatType));
        }

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static void LogTurbineChat(uint channelID, string name, string message, uint senderID, ChatType chatType)
        {
            AdminChatFeed.Shared.Capture(chatType, channelID, name, message);

            // Relay public global channels to Discord (no-ops unless the relay is enabled + configured).
            DiscordRelayManager.QueueMessage(chatType, name, message);

            switch (chatType)
            {
                case ChatType.Allegiance:
                    if (!PropertyManager.GetBool("chat_log_allegiance").Item)
                        return;
                    break;
                case ChatType.General:
                    if (!PropertyManager.GetBool("chat_log_general").Item)
                        return;
                    break;
                case ChatType.LFG:
                    if (!PropertyManager.GetBool("chat_log_lfg").Item)
                        return;
                    break;
                case ChatType.Olthoi:
                    if (!PropertyManager.GetBool("chat_log_olthoi").Item)
                        return;
                    break;
                case ChatType.Roleplay:
                    if (!PropertyManager.GetBool("chat_log_roleplay").Item)
                        return;
                    break;
                case ChatType.Society:
                case ChatType.SocietyCelHan:
                case ChatType.SocietyEldWeb:
                case ChatType.SocietyRadBlo:
                    if (!PropertyManager.GetBool("chat_log_society").Item)
                        return;
                    break;
                case ChatType.Trade:
                    if (!PropertyManager.GetBool("chat_log_trade").Item)
                        return;
                    break;
                default:
                    return;
            }

            log.Info($"[CHAT][{chatType}]{(chatType == ChatType.Allegiance ? $"[{channelID}]" : "")} {name} says, \"{message}\"");
        }
    }
}
