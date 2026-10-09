using System;
using System.Collections.Generic;

using ACE.Database.Models.Shard;
using ACE.Server.Entity.Facets;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The pure, Player-free half of the one-shot Class Ability overhaul respec - the deployment
    /// migration Player.ApplyClassAbilityOverhaulRespec runs as a login sweep.
    ///
    /// Split out for the reason CapLedger is: ACE.Server.Tests can never construct a live Player, so
    /// every decision that CAN be a pure function lives here and is pinned by
    /// ClassAbilityOverhaulRespecTests, leaving only the genuinely Player-bound wiring untested until
    /// a live pass. See Docs/VERIFY-QUEUE.md for what that live pass still owes.
    /// </summary>
    public static class ClassAbilityOverhaulRespec
    {
        /// <summary>
        /// True for exactly the quest registry rows the overhaul sweep may erase: a
        /// <see cref="ClassAbilityRegistry.QuestKeyPrefix"/> row the LIVE registry can still resolve.
        ///
        /// A row the registry CANNOT resolve is deliberately left alone rather than erased, and that
        /// asymmetry is load-bearing. Those rows belong to Player.SweepRetiredClassAbilities, which
        /// prices them from RetiredClassAbilities and refunds them at their HISTORICAL cost; it runs
        /// earlier in the same login. Erasing one here would destroy rank data this sweep cannot
        /// price. A row that is neither resolvable nor retired is left in place for the CAP audit to
        /// count as an orphan, which is exactly what that counter exists for.
        ///
        /// The prefix test is not repeated here because
        /// <see cref="ClassAbilityRegistry.TryGetByQuestKey"/> already applies it (and returns false
        /// for a null name), so a second copy could only drift away from it.
        /// </summary>
        public static bool IsSweepableQuestRow(string questName)
        {
            return ClassAbilityRegistry.TryGetByQuestKey(questName, out _);
        }

        /// <summary>
        /// The abilities_Json every stored facet row is rewritten to by the sweep.
        ///
        /// Produced by the SAME serializer the facet system writes with, never a hardcoded "{}", so a
        /// future serializer change cannot leave the sweep writing a blob
        /// FacetSnapshot.DeserializeAbilities would read as anything other than "no abilities".
        /// </summary>
        public static string EmptyAbilitiesJson => FacetSnapshot.SerializeAbilities(new Dictionary<string, int>());

        /// <summary>
        /// Empties the stored ability set on every facet row that still carries one, and waits for every
        /// write to CONFIRM. Returns FALSE when any write did not confirm success, in which case the
        /// caller must abandon the sweep WITHOUT stamping its one-shot guard.
        ///
        /// THIS IS PART OF THE SWEEP, NOT A SEPARATE SAFETY STEP. A stored facet build is a SECOND copy
        /// of the character's ranks, and clearing only the live build is strictly worse than doing
        /// nothing: ApplyFacetAbilities rewrites the quest rows and deliberately does not touch the point
        /// counters, so the first switch would re-apply a stored build's ranks for free while
        /// FacetPools.AvailableClassAbilityPointsAfterSwap subtracted an incoming spend for ranks the
        /// player no longer paid for. The player would lose points AND gain a build they did not buy.
        ///
        /// Emptying every stored build is also what makes "Available == Earned" a stable invariant rather
        /// than a value the first switch corrupts: with every stored set empty, a swap computes an
        /// outgoing spend of 0 and an incoming spend of 0 and leaves the pool alone. There is no
        /// per-facet pool to repair alongside it - character_facet carries no CAP column of its own
        /// (CharacterFacetPartial.cs), and the switch path's only CAP write is a single
        /// AdjustClassAbilityPoints call against the live character property.
        ///
        /// WHY THE WRITE IS CONFIRMED RATHER THAN FIRE-AND-FORGET: the caller stamps a permanent
        /// per-character guard immediately afterwards. A write lost to a transiently backed-up shard
        /// queue would therefore leave a stored slot holding the old fully-ranked set with the guard
        /// permanently preventing a retry - reproducing the exact double-dip described above. The wait is
        /// affordable for the same reason the read-side block is: this runs once per character, ever.
        ///
        /// Only rows that actually need a write are sent, so <paramref name="cleared"/> is an honest
        /// record of what changed rather than of how many slots exist. skills_Json, equip_Json and
        /// attrs_Json are left exactly as read - only the class ability set is being reset.
        /// </summary>
        public static bool TryClearStoredAbilities(
            IReadOnlyList<CharacterFacet> rows,
            int timeoutMs,
            Action<CharacterFacet, Action<bool>> saver,
            out int cleared)
        {
            cleared = 0;

            if (rows == null || rows.Count == 0)
                return true;

            var emptyAbilities = EmptyAbilitiesJson;
            var dirty = new List<CharacterFacet>();

            foreach (var row in rows)
            {
                if (row == null || row.AbilitiesJson == emptyAbilities)
                    continue;

                row.AbilitiesJson = emptyAbilities;
                row.UpdatedAt = DateTime.UtcNow;

                dirty.Add(row);
            }

            if (dirty.Count == 0)
                return true;

            if (!FacetRowFetch.TrySaveAll(dirty, timeoutMs, saver))
                return false;

            cleared = dirty.Count;
            return true;
        }

        /// <summary>
        /// The login notice, as its paragraphs. ONE source for both deliveries: the modal popup joins
        /// these with a blank line, and the chat mirror sends them one line at a time, so the two can
        /// never drift apart. A player whose entire build has just vanished has to be told why in the
        /// same session, and the chat copy is what survives dismissing the modal.
        ///
        /// The final paragraph is appended ONLY when the sweep actually consumed training tokens, so a
        /// player who held none is never told about an item they never had.
        /// </summary>
        /// <param name="pointsAvailable">
        /// The character's class ability points after the sweep credited them, which by construction is
        /// its lifetime TotalClassAbilityPointsEarned.
        /// </param>
        /// <param name="consumedTokenNames">Item names of the unused training tokens the sweep consumed; may be null or empty.</param>
        public static IReadOnlyList<string> BuildNoticeParagraphs(int pointsAvailable, IReadOnlyList<string> consumedTokenNames)
        {
            var paragraphs = new List<string>
            {
                "Your class abilities have been rebuilt.",

                $"Every class ability you had learned has been unlearned, and all of your class ability points have been returned - you have {pointsAvailable:N0} to spend. Nothing was lost: costs have come down across the board, many abilities are stronger, and several are new, so your old build is very likely not the one you want any more.",

                "Affinity skills now multiply an ability's own bonus instead of adding a flat amount, so investing deeply in one ability now beats splashing a single rank into several.",

                "Visit a class ability trainer to spend your points. Tier 2 and Tier 3 abilities unlock again as you reinvest.",
            };

            if (consumedTokenNames != null && consumedTokenNames.Count > 0)
            {
                var wording = consumedTokenNames.Count == 1
                    ? "One unused class ability training token was"
                    : $"{consumedTokenNames.Count:N0} unused class ability training tokens were";

                paragraphs.Add($"{wording} consumed as part of this reset, because the points you paid for {(consumedTokenNames.Count == 1 ? "it" : "them")} are already included in the total above: {string.Join(", ", consumedTokenNames)}.");
            }

            return paragraphs;
        }
    }
}
