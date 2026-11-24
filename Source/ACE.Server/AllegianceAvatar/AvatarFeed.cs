using System;
using System.Collections.Generic;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.Managers;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>
/// Which XP counts toward an Avatar and under which source multiplier. Pure: the caller gathers the context.
/// </summary>
public static class AvatarFeedRules
{
    /// <summary>
    /// Null means no contribution: allegiance pass-up, proficiency, admin and emote XP never count, and nothing
    /// counts inside a private Avatar event. Otherwise the HIGHEST applicable source wins (Fellowship with at
    /// least two same-allegiance accounts, then WorldEvent, then Thread, then Base); multipliers do not stack.
    /// </summary>
    public static AvatarFeedSource? Classify(XpType xpType, bool inThreadRun, bool inPublicEventAsParticipant, bool inPrivateAvatarEvent, int sameAllegianceAccountsInFellowship)
    {
        if (xpType != XpType.Kill && xpType != XpType.Quest && xpType != XpType.Fellowship)
            return null;

        if (inPrivateAvatarEvent)
            return null;

        if (sameAllegianceAccountsInFellowship >= 2)
            return AvatarFeedSource.Fellowship;

        if (inPublicEventAsParticipant)
            return AvatarFeedSource.WorldEvent;

        if (inThreadRun)
            return AvatarFeedSource.Thread;

        return AvatarFeedSource.Base;
    }
}

/// <summary>Which XP types feed an Avatar's level pool. Pure.</summary>
public static class AvatarLevelFeedRules
{
    /// <summary>
    /// True for Kill, Quest, Fellowship and Proficiency. Allegiance (the pass-up, already derived from the original
    /// grant), Admin and Emote XP never count.
    /// </summary>
    public static bool Counts(XpType xpType) =>
        xpType == XpType.Kill || xpType == XpType.Quest || xpType == XpType.Fellowship || xpType == XpType.Proficiency;
}

/// <summary>
/// The GrantXP feed hook. Runs on the receiving member's landblock thread, reads only memory and ends in a
/// lock-free enqueue; it never touches the store, and a failure in it never aborts the grant.
/// </summary>
public static class AvatarFeed
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Test seam. Production is the manager's lock-free queue.</summary>
    internal static Action<AvatarFeedEvent> Sink = e => AvatarManager.Enqueue(e);

    /// <summary>Test seam for the level pool feed. Production is the manager's lock-free level queue.</summary>
    internal static Action<uint, long> LevelSink = (monarch, amount) => AvatarManager.EnqueueLevel(monarch, amount);

    internal static void ResetSeams()
    {
        Sink = e => AvatarManager.Enqueue(e);
        LevelSink = (monarch, amount) => AvatarManager.EnqueueLevel(monarch, amount);
    }

    /// <summary>Test seam: the level ceiling and the total XP it needs. Production reads the chart, which needs the client dat.</summary>
    internal static Func<Player, (int MaxLevel, long MaxLevelXp)> CeilingSource = p =>
    {
        var max = p.GetPlayerMaxLevel();
        return (max, (long)EnlightenmentXpCurve.GetTotalXPRequiredForLevel(max));
    };

    /// <summary>
    /// The earner whose Fellowship.SplitXp is running on this thread, else null. SplitXp re-enters GrantXP
    /// synchronously for every fellow, and a fellow's share of a private-event kill must not feed even though the
    /// fellow has no participation record of their own.
    /// </summary>
    [ThreadStatic]
    private static Player splitEarner;

    internal static Player EnterSplit(Player earner)
    {
        var prev = splitEarner;
        splitEarner = earner;
        return prev;
    }

    internal static void ExitSplit(Player prev) => splitEarner = prev;

    /// <summary>
    /// Where fellowship range is measured from: the earner while their split is running (Fellowship.SplitXp measures
    /// from the earner), otherwise the receiver (quest grants and unsplit kills).
    /// </summary>
    internal static Position RangeAnchor(Position receiverLocation) => splitEarner != null ? splitEarner.Location : receiverLocation;

    /// <summary>
    /// What the member actually receives: the grant clamped to the XP left before the level ceiling (0 at it). Several
    /// grants in one tick near the cap can over-feed by at most one tick's grants, because Capture runs before the
    /// queued XP apply.
    /// </summary>
    internal static long ReceivedAfterCap(long amount, int level, long totalExperience, int maxLevel, long maxLevelXp)
    {
        if (level >= maxLevel)
            return 0;

        var left = maxLevelXp - totalExperience;

        return left <= 0 ? 0 : Math.Min(amount, left);
    }

    /// <summary>
    /// True when the receiver (state already read by the caller), or the earner whose split this share came from
    /// (earnerGuid 0 when no split is running), is in a private Avatar event.
    /// </summary>
    internal static bool PrivateEventApplies((bool Participant, bool PrivateAvatarEvent) receiverState, uint receiver, uint earnerGuid,
        Func<uint, (bool Participant, bool PrivateAvatarEvent)> state)
    {
        if (receiverState.PrivateAvatarEvent)
            return true;

        return earnerGuid != 0 && earnerGuid != receiver && state(earnerGuid).PrivateAvatarEvent;
    }

    /// <summary>
    /// True when the receiver (state already read by the caller), or the earner whose split this share came from (earnerGuid 0
    /// when no split is running), is standing in an Allegiance Gauntlet copy. The earner is only asked when it is a different
    /// character from the receiver.
    /// </summary>
    internal static bool GauntletCopyApplies(bool receiverInCopy, uint receiver, uint earnerGuid, Func<uint, bool> earnerInCopy)
    {
        if (receiverInCopy)
            return true;

        return earnerGuid != 0 && earnerGuid != receiver && earnerInCopy(earnerGuid);
    }

    /// <summary>
    /// One fellowship member as the tally sees them: account, monarch (a monarch's null MonarchId resolved through
    /// the allegiance) and whether they are in XP-share range of the anchor (the receiver always is).
    /// </summary>
    internal static (uint? accountId, uint? monarch, bool inRange) MemberFacts(Player self, Position anchor, Player member)
        => (member.Account?.AccountId, ResolveMonarch(member), member == self || Fellowship.GetDistanceScalar(anchor, member.Location) > 0);

    /// <summary>The monarch of a member's allegiance. A monarch's own MonarchId is null (AllegianceManager.cs:36-37), so the allegiance object answers.</summary>
    internal static uint? ResolveMonarch(Player player) => player.Allegiance?.MonarchId;

    /// <summary>
    /// Distinct accounts among members of this allegiance that are in XP-share range, saturating at 2 (all the
    /// Fellowship source needs). Allocation free: no list, no set.
    /// </summary>
    internal struct SameAllegianceTally
    {
        private uint first;
        private bool hasFirst;

        public int Count { get; private set; }

        public void Add(uint? accountId, uint? memberMonarch, bool inRange, uint monarch)
        {
            if (Count >= 2 || !inRange || accountId == null || memberMonarch != monarch)
                return;

            if (!hasFirst)
            {
                hasFirst = true;
                first = accountId.Value;
                Count = 1;
            }
            else if (accountId.Value != first)
                Count = 2;
        }
    }

    public static void Capture(Player player, long receivedAmount, XpType xpType)
    {
        // Allegiance pass-up, proficiency, admin and emote XP never count: keep them off the path entirely.
        if (xpType != XpType.Kill && xpType != XpType.Quest && xpType != XpType.Fellowship)
            return;

        if (receivedAmount <= 0 || player.Allegiance == null || !AvatarManager.Enabled)
            return;

        try
        {
            if (!PropertyManager.GetBool("allegiance_avatar_enabled").Item)
                return;

            var monarch = ResolveMonarch(player);
            var account = player.Account;

            if (monarch == null || account == null)
                return;

            var (maxLevel, maxLevelXp) = CeilingSource(player);
            var amount = ReceivedAfterCap(receivedAmount, player.Level ?? 0, player.TotalExperience ?? 0, maxLevel, maxLevelXp);

            if (amount <= 0)
                return;

            var guid = player.Guid.Full;

            var eventState = WorldEventHooks.AvatarFeedState(guid);

            if (PrivateEventApplies(eventState, guid, splitEarner?.Guid.Full ?? 0, WorldEventHooks.AvatarFeedState))
                return;

            // Nothing earned inside an Allegiance Gauntlet copy feeds Favor: the receiver standing in one, or the earner whose
            // split this share came from (the same two-sided rule as a private event).
            if (GauntletCopyApplies(GauntletHooks.InGauntletCopy(player), guid, splitEarner?.Guid.Full ?? 0, _ => GauntletHooks.InGauntletCopy(splitEarner)))
                return;

            var location = player.Location;
            var inThread = location != null && location.Instance != 0 && ThreadDungeonManager.GetRun(location.Instance) != null;

            var source = AvatarFeedRules.Classify(xpType, inThread, eventState.Participant, false, SameAllegianceAccounts(player, RangeAnchor(location), monarch.Value));

            if (source == null)
                return;

            Sink(new AvatarFeedEvent(monarch.Value, account.AccountId, guid, amount, source.Value));
        }
        catch (Exception ex)
        {
            log.Error("[AVATAR] feed capture threw", ex);
        }
    }

    /// <summary>
    /// The level pool hook: offers what the member actually received (after their own bonuses and the level-cap clamp)
    /// to their allegiance's Avatar. Unlike <see cref="Capture"/> it has no private-event or Gauntlet skips and no
    /// source multiplier: those shape Favor, not the level. Never throws; ends in a lock-free enqueue.
    /// </summary>
    public static void CaptureLevel(Player player, long added, XpType xpType)
    {
        if (added <= 0 || !AvatarLevelFeedRules.Counts(xpType) || player.Allegiance == null || !AvatarManager.Enabled)
            return;

        try
        {
            if (!PropertyManager.GetBool("allegiance_avatar_enabled").Item)
                return;

            var monarch = ResolveMonarch(player);
            if (monarch == null)
                return;

            LevelSink(monarch.Value, added);
        }
        catch (Exception ex)
        {
            log.Error("[AVATAR] level feed capture threw", ex);
        }
    }

    private static int SameAllegianceAccounts(Player player, Position anchor, uint monarch)
    {
        var fellowship = player.Fellowship;

        if (fellowship == null)
            return 0;

        var tally = new SameAllegianceTally();

        try
        {
            foreach (var weak in fellowship.FellowshipMembers.Values)
            {
                if (tally.Count >= 2)
                    break;

                if (!weak.TryGetTarget(out var member))
                    continue;

                var (accountId, memberMonarch, inRange) = MemberFacts(player, anchor, member);

                tally.Add(accountId, memberMonarch, inRange, monarch);
            }
        }
        catch (InvalidOperationException)
        {
            // The plain dictionary changed under the walk (a join or drop on another thread): count what was seen.
        }

        return tally.Count;
    }
}
