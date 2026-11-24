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

    internal static void ResetSeams()
    {
        GauntletMinLevel = () => ACE.Server.Managers.PropertyManager.GetLong("allegiance_gauntlet_min_avatar_level").Item;
        KeeperName = guid => AvatarLifecycle.NameOf?.Invoke(guid);
        Top = AvatarManager.TopContributors;
    }

    public static IReadOnlyList<string> Lines(AvatarState a, AvatarTuning t, Func<uint, string> accountDisplayName)
    {
        var lines = new List<string>();
        var level = a.Level(t);

        lines.Add(string.Format(CultureInfo.InvariantCulture, "Allegiance Avatar - level {0}.", level));
        // Available Favor: what a pending event purchase holds is not spendable, so it is shown apart.
        lines.Add(a.ReservedFavor > 0
            ? string.Format(CultureInfo.InvariantCulture, "Favor: {0} / {1} ({2} held for an event).", a.AvailableFavor, t.FavorCap, a.ReservedFavor)
            : string.Format(CultureInfo.InvariantCulture, "Favor: {0} / {1}.", a.AvailableFavor, t.FavorCap));

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

        lines.Add("Next unlock: " + NextUnlock(level, t.EventMinLevel, GauntletMinLevel()));

        return lines;
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
