using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.Shard;

namespace ACE.Server.Avatars;

/// <summary>
/// One Avatar's contribution rows, one per (account, UTC day), in contribution points. Holds absolute running
/// totals: TakeDirty hands out snapshots of those totals, so a resubmitted row writes the same values again
/// rather than adding them twice. Not thread safe: AvatarManager calls it under its lock.
/// </summary>
public sealed class AvatarLedger
{
    private sealed class Entry
    {
        public ulong Raw;
        public ulong Credited;
        public uint LastCharacter;
        public bool Dirty;
    }

    private readonly uint avatarId;
    private readonly Dictionary<(uint AccountId, DateTime Day), Entry> rows = new Dictionary<(uint, DateTime), Entry>();

    public AvatarLedger(uint avatarId)
    {
        this.avatarId = avatarId;
    }

    /// <summary>Adds to the (account, day) row and marks it dirty. raw is before the daily curve, credited after it.</summary>
    public void Record(uint accountId, uint characterGuid, long raw, long credited, DateTime dayUtc)
    {
        var entry = GetOrAdd(accountId, dayUtc.Date);
        entry.Raw = SaturatingAdd(entry.Raw, raw);
        entry.Credited = SaturatingAdd(entry.Credited, credited);
        if (characterGuid != 0)
            entry.LastCharacter = characterGuid;
        entry.Dirty = true;
    }

    /// <summary>Snapshots every dirty row with its absolute totals and clears the dirty flags.</summary>
    public IReadOnlyList<AllegianceAvatarContribution> TakeDirty()
    {
        var taken = new List<AllegianceAvatarContribution>();
        foreach (var (key, entry) in rows)
        {
            if (!entry.Dirty)
                continue;

            entry.Dirty = false;
            taken.Add(new AllegianceAvatarContribution
            {
                AvatarId = avatarId,
                AccountId = key.AccountId,
                Day = DateTime.SpecifyKind(key.Day, DateTimeKind.Utc),
                RawXp = entry.Raw,
                Credited = entry.Credited,
                LastCharacterId = entry.LastCharacter,
            });
        }
        return taken;
    }

    /// <summary>
    /// Re-marks rows from a failed flush. The next TakeDirty writes the CURRENT totals for those keys, which
    /// include everything the failed batch carried.
    /// </summary>
    public void MarkDirty(IEnumerable<AllegianceAvatarContribution> failedRows)
    {
        foreach (var row in failedRows)
        {
            if (row.AvatarId == avatarId && rows.TryGetValue((row.AccountId, row.Day.Date), out var entry))
                entry.Dirty = true;
        }
    }

    /// <summary>Loads stored rows for this Avatar (rows of other Avatars are ignored). Restored rows are not dirty.</summary>
    public void Restore(IEnumerable<AllegianceAvatarContribution> rowsSinceDay)
    {
        foreach (var row in rowsSinceDay)
        {
            if (row.AvatarId != avatarId)
                continue;

            var entry = GetOrAdd(row.AccountId, row.Day.Date);
            entry.Raw = row.RawXp;
            entry.Credited = row.Credited;
            entry.LastCharacter = row.LastCharacterId;
        }
    }

    /// <summary>Credited totals per account over the days on or after <paramref name="sinceDayUtc"/>, highest first, ties to the lower account id.</summary>
    public IReadOnlyList<(uint AccountId, long Credited)> Top(DateTime sinceDayUtc, int count)
    {
        var since = sinceDayUtc.Date;
        var totals = new Dictionary<uint, ulong>();
        foreach (var (key, entry) in rows)
        {
            if (key.Day < since)
                continue;
            totals.TryGetValue(key.AccountId, out var sum);
            totals[key.AccountId] = SaturatingAdd(sum, entry.Credited);
        }

        return totals
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(Math.Max(0, count))
            .Select(kv => (kv.Key, (long)Math.Min(kv.Value, (ulong)long.MaxValue)))
            .ToList();
    }

    /// <summary>Drops clean rows older than <paramref name="beforeDayUtc"/>; they are stored and outside every window still read.</summary>
    public void Prune(DateTime beforeDayUtc)
    {
        var before = beforeDayUtc.Date;
        var stale = rows.Where(kv => kv.Key.Day < before && !kv.Value.Dirty).Select(kv => kv.Key).ToList();
        foreach (var key in stale)
            rows.Remove(key);
    }

    private Entry GetOrAdd(uint accountId, DateTime day)
    {
        if (!rows.TryGetValue((accountId, day), out var entry))
        {
            entry = new Entry();
            rows[(accountId, day)] = entry;
        }
        return entry;
    }

    private static ulong SaturatingAdd(ulong a, long b)
    {
        if (b <= 0)
            return a;
        return SaturatingAdd(a, (ulong)b);
    }

    private static ulong SaturatingAdd(ulong a, ulong b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b;
}
