using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.BalanceTrials
{
    /// <summary>One rotation entry: the family a rep is forced to, and optionally the dungeon it runs in (null keeps the gem's).</summary>
    public sealed record TrialRotationEntry(string Family, string Dungeon = null);

    /// <summary>
    /// The enemy rotation. A scenario with a rotation fights a different family each rep, in the list's order, identical for every
    /// archetype. The index is the rep character's seed-walk index (TrialRepSetup.SeedRep), the same one the gem seed uses, so the
    /// walk survives a relog and a rebuild and restarts for each archetype. Pure: no world access.
    /// </summary>
    public static class TrialRotation
    {
        /// <summary>The 0-based entry for walk index <paramref name="repIndex"/> (1-based): entry (repIndex - 1) % count.</summary>
        public static int EntryIndex(IReadOnlyList<TrialRotationEntry> rotation, int repIndex)
        {
            if (rotation == null || rotation.Count == 0)
                throw new ArgumentException("rotation is empty", nameof(rotation));
            if (repIndex < 1)
                throw new ArgumentOutOfRangeException(nameof(repIndex), "rep index is 1-based");

            return (int)((repIndex - 1L) % rotation.Count);
        }

        /// <summary>The gem rep <paramref name="repIndex"/> runs: the same gem with the entry's Family (and Dungeon when the entry sets one). No rotation returns the gem unchanged.</summary>
        public static TrialGem GemFor(TrialGem gem, IReadOnlyList<TrialRotationEntry> rotation, int repIndex)
        {
            if (rotation == null || rotation.Count == 0)
                return gem;

            var entry = rotation[EntryIndex(rotation, repIndex)];

            return gem with { Family = entry.Family, Dungeon = entry.Dungeon ?? gem.Dungeon };
        }

        /// <summary>The status fragment for the rep that runs next: "next=&lt;k&gt;:&lt;family&gt;" with k the 1-based entry position, or null without a rotation.</summary>
        public static string NextLabel(IReadOnlyList<TrialRotationEntry> rotation, int nextRepIndex)
        {
            if (rotation == null || rotation.Count == 0)
                return null;

            var k = EntryIndex(rotation, nextRepIndex);

            return $"next={k + 1}:{rotation[k].Family}";
        }

        /// <summary>The longest refusal text a player sees (chat lines are 255 characters; this leaves room for the "trial refused: " prefix).</summary>
        public const int MaxRefusalChars = 220;

        /// <summary>The most indices a Step-seed start check builds plans for (each is a full plan build). Fixed seeds are bounded by the entry count.</summary>
        public const int MaxStepChecks = 30;

        /// <summary>How many reps of a rotation scenario the start check leaves unchecked (Step seed, run longer than <see cref="MaxStepChecks"/>); 0 otherwise.</summary>
        public static int UncheckedReps(TrialScenario scenario)
            => scenario?.Rotation != null && scenario.Gem.SeedMode == TrialSeedMode.Step ? Math.Max(0, scenario.Reps - MaxStepChecks) : 0;

        /// <summary>The walk index the status line names as next: while a rep runs the walk has not stepped yet, so the next rep is one past it.</summary>
        public static int NextRepIndex(int walkIndex, TrialPhase phase) => phase == TrialPhase.Running ? walkIndex + 1 : walkIndex;

        /// <summary>Text trimmed to <paramref name="max"/> characters, ending in "..." when it was cut.</summary>
        public static string Cap(string text, int max = MaxRefusalChars)
            => text == null || text.Length <= max ? text : text.Substring(0, max - 3) + "...";

        /// <summary>
        /// One entry's shortfall lines from the plan builder's outputs: the wave shortfalls, a line when the plan did not draw the
        /// forced family (a forced family the tables cannot satisfy yields a plan with no family and often a passing wave select), and,
        /// when anything is short, only the FIRST plan note that starts with "family:" or "band:".
        /// </summary>
        public static IReadOnlyList<string> ShortfallLines(IReadOnlyList<string> waveShortfalls, string forcedFamily, string planFamily, IEnumerable<string> planNotes)
        {
            var lines = new List<string>(waveShortfalls ?? Array.Empty<string>());

            if (!string.Equals(planFamily, forcedFamily, StringComparison.Ordinal))
                lines.Add($"forced family not drawn (plan family: {(string.IsNullOrEmpty(planFamily) ? "none" : planFamily)})");

            if (lines.Count > 0)
            {
                var note = (planNotes ?? Array.Empty<string>()).FirstOrDefault(n => n != null && (n.StartsWith("family:", StringComparison.Ordinal) || n.StartsWith("band:", StringComparison.Ordinal)));

                if (note != null)
                    lines.Add(note);
            }

            return lines;
        }

        /// <summary>
        /// The start-time check over the walk indices the session will use: startIndex .. startIndex + Reps - 1 (a Step seed is bounded
        /// to the first <see cref="MaxStepChecks"/>; see <see cref="UncheckedReps"/>).
        /// <paramref name="planShortfalls"/> builds the plan for one index's gem and returns its shortfall lines, empty when good; a
        /// throw counts as a shortfall. With a Fixed seed the seed does not vary, so each distinct entry is checked once, at the first
        /// index that reaches it; a Step seed checks every index. The first failure refuses, naming the entry position and family.
        /// Returns the capped refusal (<see cref="MaxRefusalChars"/>), with the full text in <paramref name="full"/>; null when there
        /// is no rotation or every index builds.
        /// </summary>
        public static string Precheck(TrialScenario scenario, Func<TrialGem, int, IReadOnlyList<string>> planShortfalls, out string full, int startIndex = 1)
        {
            full = null;
            var rotation = scenario?.Rotation;

            if (rotation == null || rotation.Count == 0)
                return null;

            var fixedSeed = scenario.Gem.SeedMode == TrialSeedMode.Fixed;
            var checkedEntries = new HashSet<int>();

            var limit = fixedSeed ? scenario.Reps : Math.Min(scenario.Reps, MaxStepChecks);

            for (long n = 0; n < limit; n++)
            {
                var index = (int)Math.Min(int.MaxValue, startIndex + n);
                var entry = EntryIndex(rotation, index);

                if (fixedSeed && !checkedEntries.Add(entry))
                    continue;

                IReadOnlyList<string> lines;

                try
                {
                    lines = planShortfalls(GemFor(scenario.Gem, rotation, index), index);
                }
                catch (Exception ex)
                {
                    lines = new[] { ex.Message };
                }

                if (lines != null && lines.Count > 0)
                {
                    full = $"rotation entry {entry + 1} (family '{rotation[entry].Family}') cannot be built at level {scenario.Gem.Level} (rep index {index}): {string.Join("; ", lines)}";
                    return Cap(full);
                }
            }

            return null;
        }
    }
}
