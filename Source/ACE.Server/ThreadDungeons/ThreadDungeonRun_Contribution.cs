using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>One roster member's contribution figures, detached from the run, in roster order.</summary>
    public readonly struct ContributionRow
    {
        public ContributionRow(uint guid, string name, long dealt, long taken)
        {
            Guid = guid;
            Name = name;
            Dealt = dealt;
            Taken = taken;
        }

        public uint Guid { get; }
        public string Name { get; }
        public long Dealt { get; }
        public long Taken { get; }
    }

    /// <summary>
    /// The contribution half of a GROUP run: per-member damage DEALT to this run's creatures and damage TAKEN inside
    /// its copy, summed while the run is Starting or Active and reported once, when it reaches Cleared
    /// (ThreadDungeonManager.AnnounceCleared). In-memory only; the figures die with the run.
    ///
    /// What is counted is the health actually removed. The direct sinks (melee, missile, spell projectiles, life magic,
    /// hotspots, procs, thorns and reflect) record through DamageHistory.Add AFTER their vital write, with the figure
    /// UpdateVitalDelta returned - floored at the victim's remaining health, and after any ward or barrier - and
    /// ThreadContributionHook feeds that figure in. A DoT tick is the exception: EnchantmentManager.ApplyDamageTick
    /// records its DamageHistory credits BEFORE the write, and for a monster the ward runs only inside the write, so
    /// those credits are ignored here and the tick is credited from Creature.TakeDamage's own write instead
    /// (ThreadContributionHook.BeginDotTick / OnDotTickApplied), still before the death check. Either way an overkill
    /// counts only what the victim had left, and a hit a ward ate counts only what got through.
    ///
    /// Threading. The counters live on the per-member state and are written with Interlocked from the landblock
    /// tick threads, OUTSIDE stateLock, for the reason the roster note gives for Xp and Lum: they sit on the hottest
    /// path in the server and no decision reads them together with other state. The close latch is a separate
    /// int read with Volatile, set inside the critical sections that leave Active (CheckClearedLocked, MarkEnded),
    /// so the damage path never takes stateLock and cannot contend with, or deadlock against, RecordKill.
    /// A killing blow is still counted: every sink records the hit before Creature.Die runs RecordKill, and it is
    /// RecordKill that closes the ledger.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        /// <summary>The longest name the summary prints in full; longer names are cut and end in "...".</summary>
        internal const int ContributionMaxNameChars = 32;

        /// <summary>AC cuts a chat line at 255 characters; every summary line stays at or under this.</summary>
        internal const int ContributionMaxLineChars = 250;

        internal const string ContributionHeader = "Thread cleared - contributions:";

        /// <summary>0 while the ledger is open, 1 once the run has left Starting/Active. Volatile, never under stateLock on read.</summary>
        private int contributionClosed;

        /// <summary>Called inside stateLock by every transition out of Starting/Active.</summary>
        private void CloseContributionLocked() => Volatile.Write(ref contributionClosed, 1);

        /// <summary>
        /// True while this run's ledger would accept a record: a group run not yet Cleared or Ended. Lock-free, for the DoT
        /// staging gate in EnchantmentManager.ApplyDamageTick, so a solo run or a leftover monster in a finished run stages
        /// (and allocates) nothing.
        /// </summary>
        public bool IsContributionOpen => IsGroup && Volatile.Read(ref contributionClosed) == 0;

        /// <summary>
        /// Adds <paramref name="amount"/> health a run creature lost to <paramref name="playerGuid"/> (a player, or the
        /// owner of the pet that dealt it - the caller resolves that). A no-op for a solo run, a non-member, a zero
        /// amount, or once the run has Cleared or Ended.
        /// </summary>
        public void RecordDamageDealt(uint playerGuid, uint amount)
        {
            if (amount == 0 || !IsGroup || Volatile.Read(ref contributionClosed) != 0 || !members.TryGetValue(playerGuid, out var ms))
                return;

            Interlocked.Add(ref ms.Dealt, amount);
        }

        /// <summary>Adds <paramref name="amount"/> health a roster member lost inside this run's copy. Same gates as <see cref="RecordDamageDealt"/>.</summary>
        public void RecordDamageTaken(uint playerGuid, uint amount)
        {
            if (amount == 0 || !IsGroup || Volatile.Read(ref contributionClosed) != 0 || !members.TryGetValue(playerGuid, out var ms))
                return;

            Interlocked.Add(ref ms.Taken, amount);
        }

        /// <summary>Every roster member's figures, in roster order, zeros included.</summary>
        public IReadOnlyList<ContributionRow> SnapshotContributions()
        {
            var rows = new List<ContributionRow>(roster.Count);

            foreach (var member in roster)
            {
                var ms = members[member.Guid];
                rows.Add(new ContributionRow(member.Guid, member.Name, Interlocked.Read(ref ms.Dealt), Interlocked.Read(ref ms.Taken)));
            }

            return rows;
        }

        /// <summary>
        /// The clear-time summary: <see cref="ContributionHeader"/>, then one line per row, sorted by dealt descending
        /// (ties keep the order given, which is roster order). Names are padded to one column and cut at
        /// <see cref="ContributionMaxNameChars"/>; figures use InvariantCulture thousands separators. Every line is
        /// at most <see cref="ContributionMaxLineChars"/> characters. Pure. Empty for a null or empty row set.
        /// </summary>
        public static IReadOnlyList<string> FormatContributionSummary(IReadOnlyList<ContributionRow> rows)
        {
            var lines = new List<string>();

            if (rows == null || rows.Count == 0)
                return lines;

            var sorted = rows.OrderByDescending(r => r.Dealt).ToList();

            var names = sorted.Select(r => ContributionName(r.Name)).ToList();
            var dealt = sorted.Select(r => Math.Max(0, r.Dealt).ToString("N0", CultureInfo.InvariantCulture)).ToList();
            var taken = sorted.Select(r => Math.Max(0, r.Taken).ToString("N0", CultureInfo.InvariantCulture)).ToList();

            var nameWidth = names.Max(n => n.Length);
            var dealtWidth = dealt.Max(d => d.Length);

            lines.Add(ContributionHeader);

            for (var i = 0; i < sorted.Count; i++)
            {
                var line = $"  {names[i].PadRight(nameWidth)}  dealt {dealt[i].PadLeft(dealtWidth)}   taken {taken[i]}";

                // Unreachable with the name cut above (the longest possible line is well under 150), kept so the
                // chat limit is a property of this method rather than of today's arithmetic.
                if (line.Length > ContributionMaxLineChars)
                    line = line.Substring(0, ContributionMaxLineChars);

                lines.Add(line);
            }

            return lines;
        }

        /// <summary>The printed form of a member's name: never null, cut to <see cref="ContributionMaxNameChars"/> with a "..." tail.</summary>
        internal static string ContributionName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim();

            return name.Length <= ContributionMaxNameChars
                ? name
                : name.Substring(0, ContributionMaxNameChars - 3) + "...";
        }
    }
}
