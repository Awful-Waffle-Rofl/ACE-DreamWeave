using System;
using System.Collections.Generic;

using ACE.Database.Models.Shard;

namespace ACE.Server.Avatars;

/// <summary>
/// The daily cap per ACCOUNT, across every Avatar. One instance per manager: an account with characters in
/// two allegiances fills one cap, not two. Holds only the current UTC day per account; a new day resets it.
///
/// SPLIT-INDEPENDENT. Each account keeps its raw (pre-curve) and credited totals for the day, and every call
/// credits the curve of the NEW raw total, computed from zero, minus what was already credited. So the day's
/// credited total depends only on the day's raw total, never on how it was split into batches or Avatars:
/// flooring the past-soft part of each small batch separately would credit nothing at realistic rates.
/// Not thread safe: AvatarManager calls it under its lock.
/// </summary>
public sealed class AvatarAccountCaps
{
    private readonly Dictionary<uint, (DateTime Day, long Raw, long Credited)> today = new Dictionary<uint, (DateTime, long, long)>();

    /// <summary>
    /// Credits <paramref name="contribution"/> raw points for this account on that UTC day, across all Avatars,
    /// and returns the newly credited amount: curve(rawSoFar + contribution) - creditedSoFar, never negative.
    /// </summary>
    public long Credit(uint accountId, DateTime dayUtc, long contribution, AvatarTuning t)
    {
        var day = dayUtc.Date;
        var (raw, creditedSoFar) = Totals(accountId, day);

        if (contribution <= 0)
            return 0;

        var rawAfter = SaturatingAdd(raw, contribution);
        var target = AvatarCurves.CreditAccountDay(0, rawAfter, t);
        var credited = Math.Max(0L, target - creditedSoFar);

        today[accountId] = (day, rawAfter, SaturatingAdd(creditedSoFar, credited));
        return credited;
    }

    /// <summary>The credited total for this account on that UTC day, across all Avatars.</summary>
    public long CreditedToday(uint accountId, DateTime dayUtc) => Totals(accountId, dayUtc.Date).Credited;

    /// <summary>
    /// Rebuilds the caps from stored rows, summing raw and credited per account over every Avatar. Only the
    /// newest day seen per account is kept, so passing a wider window than today is harmless.
    /// </summary>
    public void Restore(IEnumerable<AllegianceAvatarContribution> todaysRows)
    {
        foreach (var row in todaysRows)
        {
            var day = row.Day.Date;
            var raw = ToLong(row.RawXp);
            var credited = ToLong(row.Credited);

            if (!today.TryGetValue(row.AccountId, out var entry) || entry.Day < day)
                today[row.AccountId] = (day, raw, credited);
            else if (entry.Day == day)
                today[row.AccountId] = (day, SaturatingAdd(entry.Raw, raw), SaturatingAdd(entry.Credited, credited));
        }
    }

    private (long Raw, long Credited) Totals(uint accountId, DateTime day)
    {
        return today.TryGetValue(accountId, out var entry) && entry.Day == day ? (entry.Raw, entry.Credited) : (0, 0);
    }

    private static long ToLong(ulong v) => (long)Math.Min(v, (ulong)long.MaxValue);

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}
