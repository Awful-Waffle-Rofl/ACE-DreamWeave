using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars.Gauntlet;

/// <summary>When the Avatar fighter speaks.</summary>
public enum FighterVoiceTrigger
{
    /// <summary>The fighter first takes the field (Gauntlet Staging, or a private event going Active).</summary>
    Spawn,

    /// <summary>Every allegiance_avatar_fighter_voice_seconds while it stands (0 turns it off).</summary>
    Periodic,

    /// <summary>A Gauntlet wave was cleared.</summary>
    WaveClear,

    /// <summary>Its leader fell or left, and it now follows another ({0} is the new leader's name).</summary>
    LeaderSwitch,

    /// <summary>Every participant it could follow is down.</summary>
    AllDown,

    /// <summary>The fighter itself fell.</summary>
    Death,

    /// <summary>The fighter returns after its respawn hold.</summary>
    Return,
}

/// <summary>
/// The Avatar fighter's voice (owner ruling 2026-11-23): short lines in the allegiance's own chat colour, to the people
/// fighting beside it and nobody else (the Gauntlet copy's members, or the private event's participants; never the whole
/// allegiance). Every line is in <see cref="Lines"/>, one table the owner can reword. <see cref="Send"/> is the ONE place
/// a voice line becomes a chat message, so it can move into AvatarAnnouncer once that file grows a participants send
/// (AvatarFighterVoiceTests pins it as the only send).
/// </summary>
internal static class AvatarFighterVoice
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The voice table. Each line is under 200 characters, ASCII, with no dashes. LeaderSwitch lines carry {0}, the new leader's name.</summary>
    internal static readonly IReadOnlyDictionary<FighterVoiceTrigger, string[]> Lines = new Dictionary<FighterVoiceTrigger, string[]>
    {
        [FighterVoiceTrigger.Spawn] = new[]
        {
            "The Allegiance Avatar takes the field: \"I stand with you, kin of the oath. Let them come.\"",
            "The Allegiance Avatar raises its blade: \"Your fealty called me here. Now we fight as one.\"",
            "The Allegiance Avatar steps forward: \"Every oath sworn to this house is a stone in my shield.\"",
            "The Allegiance Avatar speaks: \"Patron and vassal, side by side. Show me the foe.\"",
            "The Allegiance Avatar takes up its shield: \"Stand fast. This ground is ours to hold.\"",
        },
        [FighterVoiceTrigger.Periodic] = new[]
        {
            "The Allegiance Avatar calls out: \"Hold the line! No shadow of Dereth will break this house.\"",
            "The Allegiance Avatar calls out: \"Strike true and guard your brothers and sisters. Our oath is our armor.\"",
            "The Allegiance Avatar calls out: \"Steady now. Breathe, and strike again.\"",
            "The Allegiance Avatar calls out: \"Your patrons stood where you stand. Make them proud.\"",
            "The Allegiance Avatar calls out: \"Courage! Even the Olthoi learned to fear a sworn allegiance.\"",
            "The Allegiance Avatar calls out: \"Mend your wounds and press on. We are not finished.\"",
        },
        [FighterVoiceTrigger.WaveClear] = new[]
        {
            "The Allegiance Avatar lowers its blade: \"Well fought! Catch your breath, the next will come.\"",
            "The Allegiance Avatar lowers its blade: \"They break against us like the sea on Arwic's walls.\"",
            "The Allegiance Avatar lowers its blade: \"That is how a sworn house fights. Ready yourselves.\"",
            "The Allegiance Avatar lowers its blade: \"One more wave behind us. Look to your kin and your kits.\"",
            "The Allegiance Avatar lowers its blade: \"Victory, for now. Rest your arms, but not your eyes.\"",
        },
        [FighterVoiceTrigger.LeaderSwitch] = new[]
        {
            "The Allegiance Avatar turns: \"{0}, I am with you now. Lead and I will follow.\"",
            "The Allegiance Avatar turns: \"Our champion has fallen. {0}, take up the charge!\"",
            "The Allegiance Avatar turns: \"To {0}! The oath does not break while one of us still stands.\"",
            "The Allegiance Avatar turns: \"{0}, the fight is yours to lead. My blade is at your side.\"",
        },
        [FighterVoiceTrigger.AllDown] = new[]
        {
            "The Allegiance Avatar stands alone: \"Rise, my kin! I will hold until you return.\"",
            "The Allegiance Avatar stands alone: \"You are fallen, not lost. Come back to me.\"",
            "The Allegiance Avatar stands alone: \"I stand over the fallen. Hurry back, sworn ones.\"",
            "The Allegiance Avatar stands alone: \"No one stands but me. Then I will stand for all of you.\"",
        },
        [FighterVoiceTrigger.Death] = new[]
        {
            "The Allegiance Avatar falls: \"I am not ended... only scattered. Hold until I return.\"",
            "The Allegiance Avatar falls: \"Carry the fight without me, for a little while.\"",
            "The Allegiance Avatar falls: \"While your oaths endure, so do I. I will return.\"",
            "The Allegiance Avatar falls: \"Do not grieve. Fight! I will be back at your side.\"",
        },
        [FighterVoiceTrigger.Return] = new[]
        {
            "The Allegiance Avatar returns, whole again: \"Did you think one blow could unmake an allegiance?\"",
            "The Allegiance Avatar returns, whole again: \"Your oaths called me back. Where is the foe?\"",
            "The Allegiance Avatar returns, whole again: \"I am here. Close ranks!\"",
            "The Allegiance Avatar returns, whole again: \"Again we stand together. Let us finish this.\"",
        },
    };

    /// <summary>Test seam: picks an index in [0, count).</summary>
    internal static Func<int, int> PickIndex = DefaultPick;

    /// <summary>Test seam: allegiance_avatar_fighter_voice_seconds (0 = the periodic line is off).</summary>
    internal static Func<long> PeriodSeconds = PeriodFromPropertyManager;

    /// <summary>Test seam: receives each send (audience, text) instead of the sessions.</summary>
    internal static Action<IReadOnlyList<Player>, string> Sink;

    private static int DefaultPick(int count) => ThreadSafeRandom.Next(0, count - 1);

    private static long PeriodFromPropertyManager() => PropertyManager.GetLong("allegiance_avatar_fighter_voice_seconds").Item;

    internal static void ResetSeams()
    {
        PickIndex = DefaultPick;
        PeriodSeconds = PeriodFromPropertyManager;
        Sink = null;
    }

    /// <summary>One line for <paramref name="trigger"/>, with the leader's name filled in. Null for an empty trigger.</summary>
    internal static string Line(FighterVoiceTrigger trigger, string leaderName = null)
    {
        if (!Lines.TryGetValue(trigger, out var options) || options.Length == 0)
            return null;

        var i = Math.Clamp(PickIndex(options.Length), 0, options.Length - 1);

        return string.Format(CultureInfo.InvariantCulture, options[i], leaderName ?? "friend");
    }

    /// <summary>
    /// Says a <paramref name="trigger"/> line to the fighter's audience (its Audience snapshot). Returns how many players it
    /// reached. Never throws.
    /// </summary>
    public static int Say(GauntletAvatarFighter fighter, FighterVoiceTrigger trigger, string leaderName = null)
    {
        try
        {
            var audience = fighter?.Audience?.Invoke();

            if (audience == null)
                return 0;

            return Send(audience, Line(trigger, leaderName));
        }
        catch (Exception ex)
        {
            log.Error($"[AVATAR] fighter voice ({trigger}) threw", ex);
            return 0;
        }
    }

    /// <summary>
    /// THE voice send: <paramref name="text"/> as allegiance chat (0x12) to each listed player, once each, with no prefix.
    /// Returns how many it reached. Any thread (EnqueueSend is thread-safe).
    /// </summary>
    public static int Send(IEnumerable<Player> to, string text)
    {
        if (to == null || string.IsNullOrEmpty(text))
            return 0;

        var audience = to.Where(p => p != null).GroupBy(p => p.Guid.Full).Select(g => g.First()).ToList();

        if (Sink != null)
        {
            Sink(audience, text);
            return audience.Count;
        }

        var reached = 0;

        foreach (var p in audience)
        {
            var network = p.Session?.Network;

            if (network == null)
                continue;

            network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Allegiance));
            reached++;
        }

        return reached;
    }

    /// <summary>A hidden or cloaked admin: never present to the fighter (never followed), never named in a line, and left out of a private event's audience.</summary>
    internal static bool IsHidden(Player p) => p != null && (p.CloakStatus == CloakStatus.On || p.Visibility || p.HiddenAdmin);

    /// <summary>The periodic clock (pure): true when a line is due. A period of 0 or less is off.</summary>
    internal static bool PeriodicDue(double now, double nextAt, long periodSeconds) => periodSeconds > 0 && nextAt > 0 && now >= nextAt;
}
