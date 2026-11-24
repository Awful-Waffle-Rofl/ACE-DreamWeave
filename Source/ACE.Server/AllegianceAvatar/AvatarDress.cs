using System;
using System.Globalization;
using System.Text;

using log4net;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>
/// /avatar dress and /avatar dress reset: copies what the caller is wearing onto the Avatar (or restores the default
/// look), after a popup. TryBegin runs every check and sends the popup; the Yes handler runs on the world thread, re-checks
/// them, confirms the figure is unchanged, stores the COMMAND-TIME capture and redresses the live NPCs (rulings R5, R10).
/// </summary>
public static class AvatarDress
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal const string TooElaborateText = "That outfit is too elaborate to copy.";
    internal const string StandAtAvatarText = "Stand at your Avatar to dress it.";
    internal const string FinishPromptText = "Finish answering your other prompt first.";
    internal const string OutfitChangedText = "Your outfit changed; run /avatar dress again.";
    internal const string AvatarChangedText = "The Avatar changed while you were deciding; try again.";
    internal const string SummonEndedText = "Your Avatar could not stand there; summon it again.";
    internal const string AlreadyDefaultText = "The Avatar already wears its default look.";
    internal const string ResetPrompt = "Restore the Avatar's default look?";

    /// <summary>Test seam: the clock for the Yes handler.</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    /// <summary>Test seam: true when a live NPC of the Avatar (summoned or seated) is within use range of the player.</summary>
    internal static Func<Player, AvatarState, bool> NearAvatar = DefaultNearAvatar;

    /// <summary>Test seam: the redress call (it must run on the world thread).</summary>
    internal static Func<uint, AvatarNpcs.RedressResult> Redress = AvatarNpcs.Redress;

    internal static void ResetSeams()
    {
        UtcNow = () => DateTime.UtcNow;
        NearAvatar = DefaultNearAvatar;
        Redress = AvatarNpcs.Redress;
    }

    private static bool DefaultNearAvatar(Player p, AvatarState a)
    {
        foreach (var npc in new[] { AvatarNpcs.SummonedNpc(a.AvatarId), AvatarNpcs.SeatedNpc(a.AvatarId) })
        {
            if (npc != null && npc.CurrentLandblock != null && p.IsWithinUseRadiusOf(npc))
                return true;
        }

        return false;
    }

    /// <summary>The refusal shared by the command and the Yes handler: not Bound, no rights, or still in the cooldown. Null when allowed.</summary>
    private static string CheckAllowed(Player p, AvatarState a, DateTime nowUtc, AvatarTuning t)
    {
        if (a == null || a.State != AvatarBindState.Bound)
            return AllegianceAvatarCommands.NoAvatarText(t);

        if (!AvatarRights.CanSpend(p, a))
            return AvatarVendor.NotAllowedMessage;

        var look = a.Look;
        if (look != null)
        {
            var readyAt = look.DressedAtUtc.AddSeconds(t.DressCooldownSeconds);
            if (readyAt > nowUtc)
            {
                var minutes = (long)Math.Ceiling((readyAt - nowUtc).TotalMinutes);
                return string.Format(CultureInfo.InvariantCulture, "The Avatar was dressed recently. It can be dressed again in {0} minutes.", minutes);
            }
        }

        return null;
    }

    private static AvatarLook Capture(Player p, bool reset, DateTime nowUtc) =>
        reset
            ? new AvatarLook(AvatarLookJson.CurrentVersion, nowUtc, null, Array.Empty<AvatarLookPiece>())
            : AvatarLookCapture.FromPlayer(p, nowUtc);

    internal static bool TryBegin(Player p, AvatarState a, bool reset, DateTime nowUtc, out string refusal)
    {
        refusal = null;
        var tuning = AvatarManager.CurrentTuning();

        refusal = CheckAllowed(p, a, nowUtc, tuning);
        if (refusal != null)
            return false;

        if (reset && (a.Look == null || a.Look.Body == null))
        {
            refusal = AlreadyDefaultText;
            return false;
        }

        if (!NearAvatar(p, a))
        {
            refusal = StandAtAvatarText;
            return false;
        }

        var look1 = Capture(p, reset, nowUtc);
        var json = AvatarLookJson.Serialize(look1);
        if (Encoding.UTF8.GetByteCount(json) > AvatarLookJson.MaxBytes || !AvatarLookCapture.SameFigure(AvatarLookJson.Parse(json), look1))
        {
            refusal = TooElaborateText;
            return false;
        }

        var avatarId = a.AvatarId;
        var version = a.Version;
        var playerGuid = p.Guid.Full;

        var confirmation = new Confirmation_Choice(p.Guid, () => AvatarNpcs.WorldEnqueue(() =>
            OnYes(playerGuid, avatarId, version, reset, look1, json)), null, nowUtc);

        var prompt = reset ? ResetPrompt : AvatarLookCapture.Describe(look1);
        if (!AvatarNpcs.SendConfirmation(p, confirmation, prompt))
        {
            refusal = FinishPromptText;
            return false;
        }

        return true;
    }

    /// <summary>World thread. Re-checks everything, confirms the figure is unchanged, then stores look1 and redresses.</summary>
    private static void OnYes(uint playerGuid, uint avatarId, int version, bool reset, AvatarLook look1, string json)
    {
        var p = AvatarNpcs.OnlinePlayer(playerGuid);
        if (p == null)
            return;

        var tuning = AvatarManager.CurrentTuning();
        if (!tuning.Enabled)
        {
            AvatarNpcs.Tell(p, AvatarSeatRules.SwitchedOffMessage);
            return;
        }

        var now = UtcNow();
        var current = AvatarManager.Get(avatarId);
        var refusal = CheckAllowed(p, current, now, tuning);
        if (refusal != null)
        {
            AvatarNpcs.Tell(p, refusal);
            return;
        }

        if (!NearAvatar(p, current))
        {
            AvatarNpcs.Tell(p, StandAtAvatarText);
            return;
        }

        if (!reset && !AvatarLookCapture.SameFigure(look1, AvatarLookCapture.FromPlayer(p, now)))
        {
            AvatarNpcs.Tell(p, OutfitChangedText);
            return;
        }

        if (!AvatarManager.TrySetLook(avatarId, json, version))
        {
            AvatarNpcs.Tell(p, AvatarChangedText);
            return;
        }

        var result = Redress(avatarId);

        var name = p.Name;
        if (current.MonarchGuid.HasValue)
            AvatarAnnouncer.ToAllegiance(current.MonarchGuid.Value, reset ? $"{name} restored the Avatar's default look." : $"{name} dressed the Avatar.");

        string heritage = "none", gender = "none";
        if (look1.Body != null)
        {
            look1.Body.Strings.TryGetValue(ACE.Entity.Enum.Properties.PropertyString.HeritageGroup, out var h);
            look1.Body.Strings.TryGetValue(ACE.Entity.Enum.Properties.PropertyString.Sex, out var g);
            heritage = h ?? "none";
            gender = g ?? "none";
        }

        log.Info($"[AVATAR] dress avatar={avatarId} by={name}(0x{playerGuid:X8}) pieces={look1.Pieces.Count} body={heritage}/{gender}");

        if (result.SummonEnded)
            AvatarNpcs.Tell(p, SummonEndedText);
    }
}
