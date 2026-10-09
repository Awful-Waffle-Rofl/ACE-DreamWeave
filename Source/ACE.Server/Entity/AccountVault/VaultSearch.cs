using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

using ACE.Entity.Models;
using ACE.Server.Managers.Market;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The one definition of "does this vault entry match a /mule search pattern", shared by the live
    /// vendor view's filter (PersonalVendor.RebuildView, plain /mule search) and the chat report of
    /// /mule search all. Moved here verbatim from RebuildView's filter loop so the two cannot drift: a
    /// pattern that shows an item in a panel must report the same item in chat, and vice versa.
    /// </summary>
    internal static class VaultSearch
    {
        /// <summary>
        /// A ledger row is matched against its WEENIE (it has no live WorldObject yet); a stored/group
        /// row against the real WorldObject already sitting in the vault. A null weenie (could not be
        /// resolved) or a null WorldObject counts as "no match" rather than throwing.
        ///
        /// A counted CLASS row has no WorldObject either, so it is matched against its weenie as well -
        /// but against its OWN name when it carries one. Name is part of a class's identity key, so a
        /// hand-renamed bag forms its own class, and searching that class against the template's name
        /// would fail to find exactly the item a player renamed in order to find it.
        /// </summary>
        internal static bool Matches(Regex filter, VaultEntry entry, Func<uint, Weenie> weenieLookup)
        {
            if (entry.Kind != VaultEntryKind.StoredItem)
            {
                var weenie = weenieLookup(entry.Wcid);

                if (weenie == null)
                    return false;

                var text = entry.DisplayName != null
                    ? ItemTextSearch.SearchText(entry.DisplayName, null)
                    : ItemTextSearch.SearchText(weenie);

                if (!ItemTextSearch.IsMatch(filter, text))
                    return false;
            }
            else if (entry.WorldObject == null || !ItemTextSearch.IsMatch(filter, ItemTextSearch.SearchText(entry.WorldObject)))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Every entry matching <paramref name="regex"/>, keeping the first <paramref name="keep"/> as
        /// displayable hits and counting the rest. Stops as soon as <paramref name="budget"/> is spent,
        /// with <see cref="VaultScanResult.BudgetHit"/> set - the total is then a lower bound.
        ///
        /// The budget is checked BEFORE each entry, so one scan spends at most one match's regex
        /// timeout (ItemTextSearch's 100 ms) past it.
        /// </summary>
        internal static VaultScanResult Scan(IEnumerable<VaultEntry> entries, Regex regex, int keep, VaultSearchBudget budget, Func<uint, Weenie> weenieLookup)
        {
            var result = new VaultScanResult();

            if (entries == null)
                return result;

            foreach (var entry in entries)
            {
                if (budget != null && budget.Exhausted)
                {
                    result.BudgetHit = true;
                    break;
                }

                if (entry == null || !Matches(regex, entry, weenieLookup))
                    continue;

                result.Total++;

                if (result.Hits.Count < keep)
                    result.Hits.Add(new VaultSearchHit(DisplayName(entry, weenieLookup), entry.Count));
            }

            return result;
        }

        private static string DisplayName(VaultEntry entry, Func<uint, Weenie> weenieLookup)
        {
            if (entry.Kind != VaultEntryKind.StoredItem)
                return entry.DisplayName ?? weenieLookup(entry.Wcid)?.GetName() ?? $"wcid {entry.Wcid}";

            return entry.WorldObject?.Name ?? $"wcid {entry.Wcid}";
        }
    }

    /// <summary>One displayable match: the item's name and the units (or group members) it stands for.</summary>
    internal sealed class VaultSearchHit
    {
        public string Name { get; }

        public long Count { get; }

        public VaultSearchHit(string name, long count)
        {
            Name = name;
            Count = count;
        }
    }

    /// <summary>What <see cref="VaultSearch.Scan"/> found in one store.</summary>
    internal sealed class VaultScanResult
    {
        /// <summary>The first matches, at most the scan's keep count.</summary>
        public List<VaultSearchHit> Hits { get; } = new List<VaultSearchHit>();

        /// <summary>Every match counted, including those past the keep count.</summary>
        public int Total { get; set; }

        /// <summary>True when the scan stopped early because the command's regex budget ran out.</summary>
        public bool BudgetHit { get; set; }
    }

    /// <summary>
    /// A wall-clock allowance shared by every scan in one command. The clock is injected so a test can
    /// drive exhaustion deterministically; production uses a started <see cref="Stopwatch"/>.
    /// </summary>
    internal sealed class VaultSearchBudget
    {
        private readonly TimeSpan limit;
        private readonly Func<TimeSpan> elapsed;

        public VaultSearchBudget(TimeSpan limit, Func<TimeSpan> elapsed)
        {
            this.limit = limit;
            this.elapsed = elapsed;
        }

        public static VaultSearchBudget StartNew(TimeSpan limit)
        {
            var stopwatch = Stopwatch.StartNew();

            return new VaultSearchBudget(limit, () => stopwatch.Elapsed);
        }

        public bool Exhausted => elapsed() >= limit;
    }
}
