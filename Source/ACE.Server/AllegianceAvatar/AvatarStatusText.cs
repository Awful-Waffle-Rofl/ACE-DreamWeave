using System;
using System.Collections.Generic;
using System.Globalization;

namespace ACE.Server.Avatars;

/// <summary>The status lines a member of the bound allegiance sees when they use the Avatar. ASCII only.</summary>
public static class AvatarStatusText
{
    public const string FutureUnlockLine = "More perks are coming.";
    public const string EventUnlockLine = "Private World Events";
    public const string GauntletUnlockLine = "Allegiance Gauntlet";

    /// <summary>Test seam: a character's name (the Keeper line). Defaults to the lifecycle's name lookup.</summary>
    internal static Func<uint, string> KeeperName = guid => AvatarLifecycle.NameOf?.Invoke(guid);

    /// <summary>Test seam: the top contributors, defaulting to the manager's ledger over the passed window.</summary>
    internal static Func<uint, int, long, IReadOnlyList<(uint AccountId, long Credited)>> Top = AvatarManager.TopContributors;

    /// <summary>Test seam: the Gauntlet's minimum Avatar level, read for the next-unlock line.</summary>
    internal static Func<long> GauntletMinLevel = () => ACE.Server.Managers.PropertyManager.GetLong("allegiance_gauntlet_min_avatar_level").Item;

    /// <summary>Test seam: the clock for the daily-limit line.</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    internal static void ResetSeams()
    {
        UtcNow = () => DateTime.UtcNow;
        GauntletMinLevel = () => ACE.Server.Managers.PropertyManager.GetLong("allegiance_gauntlet_min_avatar_level").Item;
        KeeperName = guid => AvatarLifecycle.NameOf?.Invoke(guid);
        Top = AvatarManager.TopContributors;
    }

    /// <param name="favorCap">The Avatar's own Favor cap (<see cref="AvatarManager.FavorCapFor"/>), not the flat floor.</param>
    public static IReadOnlyList<string> Lines(AvatarState a, AvatarTuning t, ulong favorCap, Func<uint, string> accountDisplayName)
    {
        var lines = new List<string>();
        var level = a.Level(t);

        lines.Add(string.Format(CultureInfo.InvariantCulture, "Allegiance Avatar - level {0}.", level));
        // Available Favor: what a pending event purchase holds is not spendable, so it is shown apart.
        lines.Add(a.ReservedFavor > 0
            ? string.Format(CultureInfo.InvariantCulture, "Favor: {0} / {1} ({2} held for an event).", a.AvailableFavor, favorCap, a.ReservedFavor)
            : string.Format(CultureInfo.InvariantCulture, "Favor: {0} / {1}.", a.AvailableFavor, favorCap));

        var keeper = KeeperName(a.KeeperGuid);
        if (string.IsNullOrEmpty(keeper))
            keeper = "unknown";
        lines.Add("Keeper: " + keeper + (a.KeeperIsActing ? " (acting Keeper while the Monarch is away)." : "."));

        var top = Top(a.AvatarId, 3, t.TopContributorsDays);
        if (top == null || top.Count == 0)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, "Top contributors, last {0} days: none yet.", t.TopContributorsDays));
        }
        else
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, "Top contributors, last {0} days:", t.TopContributorsDays));
            for (var i = 0; i < top.Count; i++)
            {
                var name = accountDisplayName?.Invoke(top[i].AccountId);
                if (string.IsNullOrEmpty(name))
                    name = "an unnamed account";
                lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}. {1} - {2}", i + 1, name, top[i].Credited));
            }
        }

        var daily = EventDailyLine(a, t, level);
        if (daily != null)
            lines.Add(daily);

        lines.Add(BlessingDailyLine(a, t));

        lines.Add("Next unlock: " + NextUnlock(level, t.EventMinLevel, GauntletMinLevel()));

        return lines;
    }

    /// <summary>
    /// The private World Event daily line, once events are unlocked and while a limit is set. Counts the events used today and
    /// any still in flight (a claim that has not reached Active yet), the same total the purchase refuses on. Null otherwise.
    /// </summary>
    internal static string EventDailyLine(AvatarState a, AvatarTuning t, int level)
    {
        if (level < t.EventMinLevel || t.EventDailyLimit <= 0)
            return null;

        var day = AvatarDay.BuildClock(t, UtcNow());
        var (used, pending) = AvatarManager.DailyCount(a.AvatarId, AvatarDailyKind.Event, day);
        var count = used + pending;

        var line = string.Format(CultureInfo.InvariantCulture, "Private World Events today: {0} of {1} used.", count, t.EventDailyLimit);

        return count >= t.EventDailyLimit
            ? line + " The next can be called in " + AvatarDay.UntilReset(day) + "."
            : line;
    }

    /// <summary>Today's Allegiance Blessing: available, or used with the countdown to the next Avatar day (a purchase in flight counts as used).</summary>
    internal static string BlessingDailyLine(AvatarState a, AvatarTuning t)
    {
        var day = AvatarDay.BuildClock(t, UtcNow());
        var (used, pending) = AvatarManager.DailyCount(a.AvatarId, AvatarDailyKind.Blessing, day);

        return used + pending > 0
            ? "Allegiance Blessing: called today. It can be called again in " + AvatarDay.UntilReset(day) + "."
            : "Allegiance Blessing: available today.";
    }

    /// <summary>The nearest unlock above <paramref name="level"/> among the gate tunables, else the placeholder line.</summary>
    internal static string NextUnlock(int level, long eventMinLevel, long gauntletMinLevel)
    {
        string name = null;
        long at = long.MaxValue;

        if (level < eventMinLevel)
        {
            name = EventUnlockLine;
            at = eventMinLevel;
        }

        if (level < gauntletMinLevel && gauntletMinLevel < at)
        {
            name = GauntletUnlockLine;
            at = gauntletMinLevel;
        }

        return name == null
            ? FutureUnlockLine
            : string.Format(CultureInfo.InvariantCulture, "{0} at level {1}.", name, at);
    }
}
