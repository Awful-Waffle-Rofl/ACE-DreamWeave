using System;
using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;

namespace ACE.Server.Avatars;

/// <summary>
/// Last-seen time per ACCOUNT (the max over its characters). A login on any character of an account
/// makes the whole account active. Times are unix seconds. Not thread safe: callers serialize access.
/// </summary>
public sealed class AccountActivityIndex
{
    private const double SecondsPerDay = 86400.0;

    private readonly Dictionary<uint, double> lastSeen = new Dictionary<uint, double>();

    /// <summary>Patches one account in place. Only ever moves the last-seen time forward.</summary>
    public void Touch(uint accountId, double nowUnix)
    {
        if (!lastSeen.TryGetValue(accountId, out var existing) || nowUnix > existing)
            lastSeen[accountId] = nowUnix;
    }

    /// <summary>Builds an index keeping the max last-seen per account.</summary>
    public static AccountActivityIndex Build(IEnumerable<(uint AccountId, double LastSeenUnix)> characters)
    {
        var index = new AccountActivityIndex();
        foreach (var (accountId, seen) in characters)
            index.Touch(accountId, seen);
        return index;
    }

    public double? LastSeen(uint accountId) => lastSeen.TryGetValue(accountId, out var seen) ? seen : null;

    /// <summary>True when the account was seen within the window. An unknown account is not active.</summary>
    public bool IsActive(uint accountId, double nowUnix, int windowDays)
    {
        return lastSeen.TryGetValue(accountId, out var seen) && nowUnix - seen <= windowDays * SecondsPerDay;
    }

    public int CountActive(double nowUnix, int windowDays)
    {
        var count = 0;
        foreach (var seen in lastSeen.Values)
            if (nowUnix - seen <= windowDays * SecondsPerDay)
                count++;
        return count;
    }

    /// <summary>
    /// Pure last-seen rule for one character: online is now, offline is max(login, logoff). Null when an
    /// offline character has no usable timestamp.
    /// </summary>
    internal static double? SeenAt(bool isOnline, double nowUnix, double? login, double? logoff)
    {
        if (isOnline)
            return nowUnix;

        var seen = Math.Max(login ?? 0.0, logoff ?? 0.0);
        return seen > 0.0 ? seen : null;
    }

    /// <summary>
    /// Reads every character from PlayerManager (memory only: the account is loaded once at construction).
    /// An online character counts as seen now; an offline one as max(login, logoff) timestamp.
    /// </summary>
    public static AccountActivityIndex FromPlayerManager(double nowUnix)
    {
        var index = new AccountActivityIndex();
        foreach (var player in PlayerManager.GetAllPlayers())
        {
            var account = player.Account;
            if (account == null)
                continue;

            var seen = SeenAt(player is ACE.Server.WorldObjects.Player, nowUnix,
                player.GetProperty(PropertyFloat.LoginTimestamp), player.GetProperty(PropertyFloat.LogoffTimestamp));
            if (seen.HasValue)
                index.Touch(account.AccountId, seen.Value);
        }
        return index;
    }
}
