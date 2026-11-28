using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

public enum AvatarBlessingRefusal
{
    None,
    NotPermitted,
    Inactive,
    OfflineBonusDisabled,
    DailyLimit,
    NoRecipients,
    InsufficientFavor,
}

/// <summary>
/// The Allegiance Blessing (owner design 2026-11-25): a Favor-priced row on the Avatar's vendor that adds
/// allegiance_avatar_blessing_bank_seconds (an hour by default) to the offline bonus bank of every member of the bound
/// allegiance who is online and not a mule at the moment of purchase. There is no buff of its own: the hour is ordinary
/// offline bonus time (Player.AddOfflineBonusSeconds), which may take a bank past offline_bonus_max_seconds and is drained
/// only by active combat. Members who log in afterwards get nothing.
///
/// RECIPIENTS AND PRICE. Every online, non-mule member of the allegiance who is not logging out is granted. The price is
/// min(blessing_price_cap, blessing_price_base + blessing_price_per_member x counted), where counted = the DISTINCT accounts
/// among those recipients, resolved live at purchase (so two characters on one account pay once but are both granted).
///
/// ORDER. claim the day's Blessing slot -> reserve the price -> queue each recipient's grant on that recipient's own thread
/// -> commit both. Anything that stops before a grant is queued releases both, so a refused or failed purchase spends no
/// Favor and does not use the day. One Blessing per Avatar per Avatar day (AvatarDay), shared by the whole allegiance.
///
/// ONCE PER CHARACTER PER DAY. A character also receives at most one Blessing per Avatar day, whichever Avatar (allegiance)
/// it is in: the grant stamps PropertyInt64.AllegianceBlessingReceivedAt on the recipient. The Avatar day is server-wide
/// tuning, so one day clock applies across all Avatars. Purchase time excludes characters already stamped today from the
/// recipients and the price; the grant itself re-checks the stamp on the recipient's own thread (the authoritative check,
/// because two Avatars can buy at nearly the same moment and both include the same player).
/// </summary>
public static class AvatarBlessing
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Test seam: every online player. Defaults to <see cref="PlayerManager.GetAllOnline"/> (a read-locked snapshot).</summary>
    internal static Func<IEnumerable<Player>> AllOnline = DefaultAllOnline;

    /// <summary>Test seam: offline_bonus_enabled. The Blessing is refused while the offline bonus is off.</summary>
    internal static Func<bool> OfflineBonusEnabled = DefaultOfflineBonusEnabled;

    /// <summary>Test seam: runs work on a recipient's own thread. Defaults to the player's action queue.</summary>
    internal static Action<Player, Action> RunOnPlayer = DefaultRunOnPlayer;

    /// <summary>Test seam: the prompt save after a grant. Defaults to <see cref="Player.SavePlayerToDatabase"/>.</summary>
    internal static Action<Player> SavePlayer = DefaultSavePlayer;

    /// <summary>Test seam: sends a line to one recipient.</summary>
    internal static Action<Player, string> TellPlayer = DefaultTellPlayer;

    /// <summary>Test seam: holds the price. Defaults to <see cref="AvatarManager.TryReserveFavor"/>.</summary>
    internal static Func<uint, ulong, bool> ReserveFavor = AvatarManager.TryReserveFavor;

    /// <summary>Test seam: the clock.</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    /// <summary>Test seam: re-sends the vendor panel so the client shows the new Favor balance.</summary>
    internal static Action<AvatarVendor, Player> ResendPanel = DefaultResendPanel;

    internal static void ResetSeams()
    {
        AllOnline = DefaultAllOnline;
        OfflineBonusEnabled = DefaultOfflineBonusEnabled;
        RunOnPlayer = DefaultRunOnPlayer;
        SavePlayer = DefaultSavePlayer;
        TellPlayer = DefaultTellPlayer;
        ReserveFavor = AvatarManager.TryReserveFavor;
        UtcNow = () => DateTime.UtcNow;
        ResendPanel = DefaultResendPanel;
    }

    private static IEnumerable<Player> DefaultAllOnline() => PlayerManager.GetAllOnline();

    /// <summary>The online players whose allegiance is led by <paramref name="monarchGuid"/>, read live (Player.Allegiance).</summary>
    internal static IReadOnlyList<Player> OnlineMembers(uint monarchGuid) =>
        (AllOnline() ?? Enumerable.Empty<Player>()).Where(p => p != null && !p.IsLoggingOut && AvatarFeed.ResolveMonarch(p) == monarchGuid).ToList();

    private static bool DefaultOfflineBonusEnabled() => PropertyManager.GetBool("offline_bonus_enabled").Item;

    private static void DefaultRunOnPlayer(Player p, Action work) => p.EnqueueAction(new ActionEventDelegate(work));

    private static void DefaultSavePlayer(Player p) => p.SavePlayerToDatabase();

    /// <summary>
    /// One recipient's grant, on that recipient's own thread: the seconds go into the offline bonus bank (uncapped), then a
    /// prompt save, because the grant is a purchase and must not wait for the next periodic save to survive a crash.
    /// </summary>
    internal static bool GrantBank(Player p, long seconds, SurveyDayClock day, uint avatarId)
    {
        // A zero-tuned Blessing grants nothing, so it must not burn the character's day.
        if (seconds <= 0)
            return false;

        if (ReceivedToday(p, day))
        {
            log.Info($"[AVATAR] Allegiance Blessing from Avatar {avatarId} skipped for {p.Name} (0x{p.Guid.Full:X8}): already blessed this Avatar day.");
            return false;
        }

        p.AddOfflineBonusSeconds(seconds);
        p.SetProperty(PropertyInt64.AllegianceBlessingReceivedAt, (long)day.Now);
        SavePlayer(p);
        return true;
    }

    /// <summary>True when the character's last Blessing stamp falls on the clock's Avatar day (or later). No stamp is false.</summary>
    internal static bool ReceivedToday(Player p, SurveyDayClock day)
    {
        var stamp = p?.GetProperty(PropertyInt64.AllegianceBlessingReceivedAt);

        if (!stamp.HasValue || stamp.Value <= 0)
            return false;

        return AvatarDay.IsToday(DateTime.UnixEpoch.AddSeconds(Math.Min(stamp.Value, uint.MaxValue)), day);
    }

    private static void DefaultTellPlayer(Player p, string text) =>
        p.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

    private static void DefaultResendPanel(AvatarVendor npc, Player p) => npc.ApproachVendor(p, VendorType.Undef);

    // ---- pure rules ------------------------------------------------------------------------------------------

    /// <summary>min(cap, base + perMember x counted), every term floored at 0 and the sum saturating.</summary>
    public static ulong Price(int counted, AvatarTuning t)
    {
        var baseP = (decimal)Math.Max(0L, t.BlessingPriceBase);
        var per = (decimal)Math.Max(0L, t.BlessingPricePerMember);
        var cap = (decimal)Math.Max(0L, t.BlessingPriceCap);

        return (ulong)Math.Min(cap, baseP + per * Math.Max(0, counted));
    }

    /// <summary>
    /// Who receives it, from the online members: every character that is not a mule, is not logging out and has an account,
    /// in guid order. Two characters on one account are both granted; <see cref="CountedAccounts"/> prices them once. When
    /// <paramref name="day"/> is given, a character already blessed on that Avatar day (any Avatar) is also excluded.
    /// </summary>
    public static IReadOnlyList<Player> Recipients(IEnumerable<Player> online, SurveyDayClock day = null) =>
        (online ?? Enumerable.Empty<Player>())
            .Where(p => p != null && !p.IsMule && !p.IsLoggingOut && p.Account != null && (day == null || !ReceivedToday(p, day)))
            .OrderBy(p => p.Guid.Full)
            .ToList();

    /// <summary>The distinct accounts among <paramref name="recipients"/>: the member count the price and the lines use.</summary>
    public static int CountedAccounts(IEnumerable<Player> recipients) =>
        (recipients ?? Enumerable.Empty<Player>()).Select(p => p.Account.AccountId).Distinct().Count();

    /// <summary>The pure rule set, in the order the buyer would most want to hear about it.</summary>
    public static AvatarBlessingRefusal Check(bool permitted, bool active, bool offlineBonusEnabled, bool usedToday, int recipients, ulong available, ulong price)
    {
        if (!permitted)
            return AvatarBlessingRefusal.NotPermitted;

        if (!active)
            return AvatarBlessingRefusal.Inactive;

        if (!offlineBonusEnabled)
            return AvatarBlessingRefusal.OfflineBonusDisabled;

        if (usedToday)
            return AvatarBlessingRefusal.DailyLimit;

        if (recipients <= 0)
            return AvatarBlessingRefusal.NoRecipients;

        if (available < price)
            return AvatarBlessingRefusal.InsufficientFavor;

        return AvatarBlessingRefusal.None;
    }

    /// <summary>"an hour" for 3600 seconds, "N hours" for whole hours, else whole minutes (rounded up). ASCII.</summary>
    internal static string Duration(long seconds)
    {
        if (seconds == 3600)
            return "an hour";

        if (seconds > 0 && seconds % 3600 == 0)
            return string.Format(CultureInfo.InvariantCulture, "{0} hours", seconds / 3600);

        var minutes = Math.Max(1L, (seconds + 59) / 60);
        return minutes == 1 ? "a minute" : string.Format(CultureInfo.InvariantCulture, "{0} minutes", minutes);
    }

    private static string Members(int n) => n == 1 ? "1 member" : string.Format(CultureInfo.InvariantCulture, "{0} members", n);

    /// <summary>One ASCII refusal line for the buyer. <paramref name="untilReset"/> is the countdown for <see cref="AvatarBlessingRefusal.DailyLimit"/>.</summary>
    public static string LineFor(AvatarBlessingRefusal refusal, ulong price, int members, string untilReset = null) => refusal switch
    {
        AvatarBlessingRefusal.NotPermitted => "You may not spend this Avatar's Favor.",
        AvatarBlessingRefusal.Inactive => "The Avatar is not active right now.",
        AvatarBlessingRefusal.OfflineBonusDisabled => "The Avatar's Blessing cannot be called while the offline experience bonus is switched off.",
        AvatarBlessingRefusal.DailyLimit => string.IsNullOrEmpty(untilReset)
            ? "Your allegiance has already received the Avatar's Blessing today. It can be called again after the daily reset."
            : $"Your allegiance has already received the Avatar's Blessing today. It can be called again in {untilReset}.",
        AvatarBlessingRefusal.NoRecipients => "No member online can receive the Blessing today.",
        AvatarBlessingRefusal.InsufficientFavor => string.Format(CultureInfo.InvariantCulture,
            "The Avatar does not have enough Favor. The Blessing costs {0} Favor for {1} online.", price, Members(members)),
        _ => "The Blessing could not be called.",
    };

    /// <summary>To the allegiance on purchase.</summary>
    public static string AllegianceLine(string buyerName, int members, long seconds) => string.Format(CultureInfo.InvariantCulture,
        "{0} has called down the Avatar's Blessing: {1} online {2} {3} of bonus experience.",
        buyerName, Members(members), members == 1 ? "gains" : "gain", Duration(seconds));

    /// <summary>To each recipient when their grant lands.</summary>
    public static string RecipientLine(long seconds) => $"The Avatar's Blessing adds {Duration(seconds)} to your offline bonus.";

    /// <summary>The price the panel shows now for this Avatar: what a purchase this instant would charge.</summary>
    internal static ulong CurrentPrice(AvatarState avatar, AvatarTuning t)
    {
        if (avatar?.MonarchGuid == null)
            return Price(0, t);

        try
        {
            return Price(CountedAccounts(Recipients(OnlineMembers(avatar.MonarchGuid.Value), AvatarDay.BuildClock(t, UtcNow()))), t);
        }
        catch (Exception ex)
        {
            log.Warn("[AVATAR] Pricing the Allegiance Blessing row failed; showing the base price.", ex);
            return Price(0, t);
        }
    }

    // ---- purchase --------------------------------------------------------------------------------------------

    public static bool TryBuy(Player buyer, AvatarVendor npc, out string refusalLine)
    {
        var t = AvatarVendor.Tuning();

        if (!t.Enabled)
        {
            refusalLine = AvatarSeatRules.SwitchedOffMessage;
            return false;
        }

        if (buyer == null || npc == null)
        {
            refusalLine = LineFor(AvatarBlessingRefusal.NotPermitted, 0, 0);
            return false;
        }

        var avatar = npc.AvatarId == 0 ? null : AvatarManager.Get(npc.AvatarId);

        var permitted = avatar != null && AvatarRights.IsMember(buyer, avatar) && AvatarRights.CanSpend(buyer, avatar);
        var active = avatar != null && avatar.State == AvatarBindState.Bound && avatar.Active && avatar.MonarchGuid.HasValue;

        var day = AvatarDay.BuildClock(t, UtcNow());
        var usedToday = false;
        IReadOnlyList<Player> recipients = Array.Empty<Player>();

        if (permitted && active)
        {
            var (used, pending) = AvatarManager.DailyCount(avatar.AvatarId, AvatarDailyKind.Blessing, day);
            usedToday = used + pending >= 1;
            recipients = Recipients(OnlineMembers(avatar.MonarchGuid.Value), day);
        }

        var counted = CountedAccounts(recipients);
        var price = Price(counted, t);

        var refusal = Check(permitted, active, OfflineBonusEnabled(), usedToday, recipients.Count, avatar?.AvailableFavor ?? 0, price);

        if (refusal != AvatarBlessingRefusal.None)
        {
            refusalLine = LineFor(refusal, price, counted, refusal == AvatarBlessingRefusal.DailyLimit ? AvatarDay.UntilReset(day) : null);
            return false;
        }

        var avatarId = avatar.AvatarId;

        // Claim -> reserve: the claim is the authoritative once-a-day gate (Check's count was only a read).
        if (!AvatarManager.TryClaimDaily(avatarId, AvatarDailyKind.Blessing, 1, day))
        {
            refusalLine = LineFor(AvatarBlessingRefusal.DailyLimit, price, counted, AvatarDay.UntilReset(day));
            return false;
        }

        if (!ReserveFavor(avatarId, price))
        {
            AvatarManager.ReleaseDaily(avatarId, AvatarDailyKind.Blessing);
            refusalLine = LineFor(AvatarBlessingRefusal.InsufficientFavor, price, counted);
            return false;
        }

        var seconds = Math.Max(0L, t.BlessingBankSeconds);
        var recipientLine = RecipientLine(seconds);
        var queued = new List<Player>();

        // ACCEPTED RACE: a recipient who logs out after their grant is queued but before their thread runs it may lose the grant
        // (the logout save can land first). Recipients already logging out at purchase are excluded above; the remaining window
        // is one action-queue hop, and the purchase is still charged for them. Accepted by review 2026-11-25.
        // ACCEPTED RACE (per-character stamp): two Avatars buying at nearly the same moment can both include one player. The
        // grant re-checks the stamp on the player's own thread, so only the first lands; the other Avatar's purchase is still
        // charged for that recipient and they get no seconds and no line. Accepted: the window is one action-queue hop.
        foreach (var r in recipients)
        {
            try
            {
                RunOnPlayer(r, () =>
                {
                    try
                    {
                        if (GrantBank(r, seconds, day, avatarId))
                            TellPlayer(r, recipientLine);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[AVATAR] Allegiance Blessing grant to {r.Name} (0x{r.Guid.Full:X8}) threw on their thread; the purchase stands.", ex);
                    }
                });
                queued.Add(r);
            }
            catch (Exception ex)
            {
                log.Error($"[AVATAR] Could not queue the Allegiance Blessing grant for {r.Name} (0x{r.Guid.Full:X8}).", ex);
            }
        }

        if (queued.Count == 0)
        {
            AvatarManager.ReleaseReservation(avatarId, price);
            AvatarManager.ReleaseDaily(avatarId, AvatarDailyKind.Blessing);
            refusalLine = LineFor(AvatarBlessingRefusal.NoRecipients, price, 0);
            return false;
        }

        AvatarManager.CommitDebit(avatarId, price);
        AvatarManager.CommitDaily(avatarId, AvatarDailyKind.Blessing, day);

        log.Info($"[AVATAR] {buyer.Name} bought the Allegiance Blessing for Avatar {avatarId}: {price} Favor for {counted} accounts, {queued.Count} of {recipients.Count} recipients queued, {seconds} s each.");

        AvatarAnnouncer.ToAllegiance(avatar.MonarchGuid.Value, AllegianceLine(buyer.Name, queued.Count, seconds));

        try
        {
            ResendPanel(npc, buyer);
        }
        catch (Exception ex)
        {
            log.Warn("[AVATAR] Could not re-send the vendor panel after an Allegiance Blessing purchase", ex);
        }

        refusalLine = null;
        return true;
    }
}
