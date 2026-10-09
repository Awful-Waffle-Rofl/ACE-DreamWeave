using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The object-owning edge of the Threads pooled-loot model: banking a kill from a dying creature, and
    /// destroying anything the run refuses. The run itself only holds references under its lock; whoever is
    /// refused a hand-off destroys the object here, so no path strands an out-of-world item (invariant 5).
    /// </summary>
    public static class ThreadLootPool
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Test seam for the kill-time rare roll. Null (the default) is production: Corpse.RollRareFor, with the
        /// killer's found-rare bookkeeping applied by BankKill only after the ledger append succeeds. A seam
        /// roller replaces the roll and books nothing.
        /// </summary>
        internal static Func<Creature, DamageHistoryInfo, WorldObject> HeldRareRoller;

        /// <summary>
        /// Called from Creature.Die() for a pooled run creature, BEFORE the run kill hook, on the landblock
        /// thread that killed it. Applies CreateCorpse's exact rare eligibility, including the CombatPet ->
        /// owner substitution (DamageHistoryInfo.ResolvePetOwnerAsKiller), and banks the creature's own
        /// profile and affinities. Returns false when the run refused the entry (it has ended); the held rare
        /// is destroyed in that case and the killer's rare bookkeeping is not applied.
        /// </summary>
        public static bool BankKill(Creature creature, DamageHistoryInfo killer)
        {
            var run = creature?.P_DungeonRun;

            if (run == null)
                return false;

            // Same substitution CreateCorpse's lootKiller makes: a kill landed by a player's CombatPet is
            // eligible exactly as if the owner had made the kill (DamageHistoryInfo.ResolvePetOwnerAsKiller).
            // Resolved once, at the top, so canRare, ResolveRareFor, FallbackRecipient and FromKill all see
            // the owner.
            killer = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);

            var canRare = Creature.ResolveCanGenerateRare(creature.CanGenerateRare, creature.Level, killer != null,
                killer != null && killer.IsPlayer, killer != null && killer.IsOlthoiPlayer, () => killer.TryGetAttacker()?.Level);

            var isBoss = creature.DungeonRole == DungeonRole.Boss;

            // Group Threads (rulings R17, R18). A SOLO run takes neither branch: one roll, the killer's own rare roll,
            // and no random draw or presence lookup beyond what it made before Group Threads.
            var rolls = run.IsGroup ? RollCount(isBoss ? run.BossLootFactor : run.TrashLootFactor, DrawUniform01) : 1;

            DamageHistoryInfo rareFor = null;
            uint rareRecipientGuid = 0;

            if (run.IsGroup)
            {
                rareFor = ResolveRareFor(canRare, killer, () => (RareCandidates ?? DefaultRareCandidates)(run), DrawIndexInclusive, out rareRecipientGuid);

                // Ruling Q-T7-1: nobody picked, so the rare (if any) is rolled for the killer and books the killer's
                // rare timer. A killer on the roster is then the recipient, so the rare reaches the pile of the
                // player whose timer it spent rather than being dealt to someone else. FromKill drops the guid
                // again when no rare is actually rolled.
                rareRecipientGuid = FallbackRecipient(rareFor, rareRecipientGuid, killer, run.IsRosterMember);
            }

            // Filled only by the production roll, and applied only once the run has accepted the entry: the corpse
            // books only after the rare lands on it (Corpse.TryGenerateRare), so a rare destroyed by a refused
            // append must not reset the rare timer or advance a tier counter. RollRareFor reports the player it rolled
            // FOR, so in a group run the bookkeeping lands on the recipient, never on a killer who did not get the rare.
            var seam = HeldRareRoller;
            Player rareKiller = null;
            var rareTimestamp = 0;
            var rareRealTime = false;
            var rareTier = 0;

            var entry = ThreadLootLedgerEntry.FromKill(isBoss, killer, canRare,
                creature.DeathTreasure, creature.P_DungeonSalvageAffinities,
                k => seam != null
                    ? seam(creature, k)
                    : Corpse.RollRareFor(k, creature.Name, creature.Guid, out rareKiller, out rareTimestamp, out rareRealTime, out rareTier),
                rolls, rareFor, rareRecipientGuid);

            if (AppendOrDiscard(run, entry))
            {
                if (seam == null && entry.HeldRare != null)
                    Corpse.ApplyRareFoundBookkeeping(rareKiller, rareRealTime, rareTimestamp, rareTier);

                return true;
            }

            log.Warn($"[DYNDUNGEON] {run} refused a pooled kill from {creature.Name} (0x{creature.Guid.Full:X8}); the run has ended");
            return false;
        }

        /// <summary>A run cannot ask a loot factory for an unbounded number of rolls on a landblock thread (the ScaledLootCount rule).</summary>
        internal const int MaxRollsPerKill = 100;

        /// <summary>
        /// Ruling R17: a loot roll factor realised as whole rolls. floor(f), plus one more with probability frac(f).
        /// A factor that is NaN or at most 1.0 is exactly one roll and never calls <paramref name="uniform01"/>; nor
        /// does a factor with no fractional part (frac &lt;= 1e-9). Capped at <see cref="MaxRollsPerKill"/>, and an
        /// infinite factor reads as the cap without a draw.
        /// </summary>
        /// <param name="uniform01">A uniform draw in [0, 1). Called at most once.</param>
        internal static int RollCount(double factor, Func<double> uniform01)
        {
            if (double.IsNaN(factor) || factor <= 1.0)
                return 1;

            if (double.IsInfinity(factor) || factor >= MaxRollsPerKill)
                return MaxRollsPerKill;

            var whole = Math.Floor(factor);
            var frac = factor - whole;
            var rolls = (int)whole;

            if (frac > 1e-9 && uniform01() < frac)
                rolls++;

            return Math.Min(rolls, MaxRollsPerKill);
        }

        /// <summary>
        /// Ruling R18, the group rare recipient. Eligibility stays with the killer: an ineligible kill, a missing
        /// killer or an Olthoi killer returns null WITHOUT reading <paramref name="candidates"/> or drawing. Otherwise
        /// one candidate is picked uniformly (<paramref name="nextInclusive"/>(0, count - 1); a single candidate is
        /// taken without a draw) and the rare is rolled for them. Olthoi players are never candidates, the corpse
        /// rule's own exclusion. No candidate at all returns null, which is today's roll for the killer.
        /// </summary>
        /// <param name="candidates">The online roster members inside the run, in roster order.</param>
        /// <param name="recipientGuid">The picked member's guid; 0 when null is returned.</param>
        internal static DamageHistoryInfo ResolveRareFor(bool canGenerateRare, DamageHistoryInfo killer,
            Func<IReadOnlyList<WorldObject>> candidates, Func<int, int, int> nextInclusive, out uint recipientGuid)
        {
            recipientGuid = 0;

            if (!canGenerateRare || killer == null || killer.IsOlthoiPlayer || candidates == null)
                return null;

            var pool = (candidates() ?? Array.Empty<WorldObject>())
                .Where(c => c != null && !(c is Player p && p.IsOlthoiPlayer))
                .ToList();

            if (pool.Count == 0)
                return null;

            var index = pool.Count == 1 ? 0 : Math.Clamp(nextInclusive(0, pool.Count - 1), 0, pool.Count - 1);
            var pick = pool[index];

            recipientGuid = pick.Guid.Full;
            return new DamageHistoryInfo(pick);
        }

        /// <summary>
        /// Ruling Q-T7-1. When <see cref="ResolveRareFor"/> picked a recipient (<paramref name="rareFor"/> non-null),
        /// that pick stands. Otherwise the rare is the killer's own roll: the killer's guid when the killer is on the
        /// roster, else 0 (the solo rule).
        /// </summary>
        internal static uint FallbackRecipient(DamageHistoryInfo rareFor, uint pickedGuid, DamageHistoryInfo killer, Func<uint, bool> isRosterMember)
        {
            if (rareFor != null)
                return pickedGuid;

            if (killer == null || isRosterMember == null)
                return 0;

            var guid = killer.Guid.Full;
            return isRosterMember(guid) ? guid : 0;
        }

        /// <summary>Test seam for the group roll-count draw. Null (the default) is production: ThreadSafeRandom.Next(0f, 1f).</summary>
        internal static Func<double> UniformSource;

        /// <summary>Test seam for the group rare-recipient index draw. Null (the default) is production: ThreadSafeRandom.Next(min, max), inclusive.</summary>
        internal static Func<int, int, int> IndexSource;

        /// <summary>Test seam for the group rare candidates. Null (the default) is production: ThreadRunPresence.OnlineMembersInside.</summary>
        internal static Func<ThreadDungeonRun, IReadOnlyList<WorldObject>> RareCandidates;

        private static double DrawUniform01() => UniformSource != null ? UniformSource() : ThreadSafeRandom.Next(0f, 1f);

        private static int DrawIndexInclusive(int min, int max) => IndexSource != null ? IndexSource(min, max) : ThreadSafeRandom.Next(min, max);

        private static IReadOnlyList<WorldObject> DefaultRareCandidates(ThreadDungeonRun run) => ThreadRunPresence.OnlineMembersInside(run);

        internal static bool AppendOrDiscard(ThreadDungeonRun run, ThreadLootLedgerEntry entry)
        {
            if (run != null && run.TryAppendLootEntry(entry))
                return true;

            entry?.HeldRare?.Destroy();
            return false;
        }

        internal static bool OverflowOrDestroy(ThreadDungeonRun run, WorldObject item, string rareFinderName)
        {
            if (item == null)
                return false;

            if (run != null && run.TryAddOverflow(item, rareFinderName))
                return true;

            item.Destroy();
            return false;
        }

        /// <summary>
        /// Invariant 5: destroys overflow, unclaimed held rares, (group) built items still in the deal buffers, and anything the loot trickle built ahead of the clear that was never delivered. Call after run.MarkEnded. Also destroys every roster
        /// member's undelivered held pile (DrainHeldPiles records those rounds as forfeited first) - roster-generic
        /// since round 4 (owner ruling 2026-09-22), so a solo run's owner pile drains here exactly as a group
        /// member's does; before round 4 a solo run never held a pile, so this line was a no-op for it.
        /// </summary>
        public static LootPoolDrain DisposeForRunEnd(ThreadDungeonRun run)
        {
            var drain = run.DrainLootPool();

            foreach (var item in drain.Overflow)
                item.Destroy();

            foreach (var rare in drain.HeldRares)
                rare.Destroy();

            foreach (var item in drain.Buffered)
                item.Destroy();

            foreach (var item in drain.Prebuilt)
                item.Destroy();

            var held = run.DrainHeldPiles();
            drain.HeldPileItems = held.Count;

            foreach (var item in held)
                item.Destroy();

            if (held.Count > 0)
                log.Info($"[DYNDUNGEON] {run} ended with {held.Count} undelivered held pile item(s)");

            return drain;
        }

        public static bool AnyCacheHoldsItems(ThreadDungeonRun run)
            => run != null && run.PlacedCachesSnapshot().Any(c => c.Inventory.Count > 0);

        /// <summary>Group Threads (Task 10): <see cref="AnyCacheHoldsItems"/> limited to the caches that belong to <paramref name="memberGuid"/>.</summary>
        public static bool AnyCacheHoldsItemsFor(ThreadDungeonRun run, uint memberGuid)
            => run != null && run.PlacedCachesSnapshot().Any(c => c.Inventory.Count > 0 && IsCacheFor(c, memberGuid));

        /// <summary>A placed cache belongs to the player its owner stamp names (Chest.P_DungeonCacheOwnerGuid). A container with no stamp belongs to nobody.</summary>
        internal static bool IsCacheFor(Container cache, uint memberGuid)
            => cache is Chest chest && chest.P_DungeonCacheOwnerGuid == memberGuid;
    }
}
