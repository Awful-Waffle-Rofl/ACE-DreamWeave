using System;
using System.Collections.Generic;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Avatars;

/// <summary>Chat lines to a whole allegiance about its Avatar.</summary>
public static class AvatarAnnouncer
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Test seam. When set, it receives the line instead of the online members.</summary>
    internal static Action<uint, string> Sink;

    /// <summary>The message every Avatar line goes out as: 0x12 Allegiance (orange), so it reads as allegiance traffic. Both sends below build it here.</summary>
    internal static GameMessageSystemChat Line(string text) => new GameMessageSystemChat(text, ChatMessageType.Allegiance);

    /// <summary>
    /// Sends <paramref name="text"/> to every online member of the allegiance led by <paramref name="monarchGuid"/>,
    /// the monarch included (FindAllPlayers matches MonarchId, which is null on the monarch). Returns the guids reached.
    /// </summary>
    public static HashSet<uint> ToAllegiance(uint monarchGuid, string text)
    {
        var sent = new HashSet<uint>();

        if (Sink != null)
        {
            Sink(monarchGuid, text);
            return sent;
        }

        try
        {
            var monarch = PlayerManager.GetOnlinePlayer(monarchGuid);
            if (monarch != null && sent.Add(monarchGuid))
                monarch.Session.Network.EnqueueSend(Line(text));

            foreach (var member in AllegianceManager.FindAllPlayers(new ObjectGuid(monarchGuid)))
            {
                if (!sent.Add(member.Guid.Full))
                    continue;

                var online = PlayerManager.GetOnlinePlayer(member.Guid);
                online?.Session.Network.EnqueueSend(Line(text));
            }
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] Announcement to allegiance 0x{monarchGuid:X8} failed: {text}", ex);
        }

        return sent;
    }
}
