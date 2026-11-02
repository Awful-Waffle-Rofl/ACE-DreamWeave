using System;
using System.Collections.Generic;
using System.Globalization;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// The shared "ball_wcids" arg of the balls and rain mechanics: a comma-separated list of ball weenies, one picked
    /// at random per ball (random colours). Grammar: 1..<see cref="MaxEntries"/> positive decimal integers separated by
    /// commas, spaces around an entry allowed, no empty entry, no duplicates. Given, it replaces "ball_wcid"; absent,
    /// the mechanic uses "ball_wcid" alone exactly as before (no extra random draw anywhere, so every pattern's
    /// golden plan is unchanged).
    ///
    /// The pick draws from its OWN stream, a <see cref="HazardBallColourPicker"/> seeded from the run seed
    /// (<see cref="Picker"/>), never from HazardContext.Rng: the pattern's stream is untouched by how many colours there
    /// are, so a list of one colour plays exactly the plan "ball_wcid" would. The picker is not a System.Random, so no
    /// mechanic file constructs one (invariant 9, HazardReconciliationTests' source scan).
    /// </summary>
    public static class HazardBallWcids
    {
        public const string Key = "ball_wcids";

        public const int MaxEntries = 16;

        /// <summary>Mixed into the run seed so the colour stream is not the pattern stream (HazardContext.Rng is seeded with the run seed itself).</summary>
        public const int PickerSalt = 0x5BA11C01;

        /// <summary>Null when the arg is absent or valid, else one line naming the problem.</summary>
        public static string Validate(HazardArgs args)
        {
            TryParse(args, out _, out var error);
            return error;
        }

        /// <summary>
        /// The list, or null (with a null <paramref name="error"/>) when the arg is absent. False with
        /// <paramref name="error"/> when it is given but does not follow the grammar.
        /// </summary>
        public static bool TryParse(HazardArgs args, out IReadOnlyList<uint> wcids, out string error)
        {
            wcids = null;
            error = null;

            var raw = args?.GetString(Key);

            if (raw == null)
                return true;

            var list = new List<uint>();
            var parts = raw.Split(',');

            if (parts.Length > MaxEntries)
            {
                error = $"{Key}='{raw}' lists {parts.Length} wcids; at most {MaxEntries}";
                return false;
            }

            foreach (var part in parts)
            {
                var p = part.Trim();

                if (p.Length == 0)
                {
                    error = $"{Key}='{raw}' has an empty entry (comma-separated wcids, e.g. {Key}=1006951,1006957)";
                    return false;
                }

                if (!uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var wcid) || wcid == 0 || wcid > int.MaxValue)
                {
                    error = $"{Key}='{raw}': '{p}' is not a wcid (a positive decimal integer)";
                    return false;
                }

                if (list.Contains(wcid))
                {
                    error = $"{Key}='{raw}' lists {wcid} twice";
                    return false;
                }

                list.Add(wcid);
            }

            wcids = list;
            return true;
        }

        /// <summary>The ball wcids a run may spawn: the parsed list when given and valid, else <paramref name="single"/> alone.</summary>
        public static IReadOnlyList<uint> Candidates(HazardArgs args, uint single)
            => TryParse(args, out var list, out _) && list != null && list.Count > 0 ? list : new[] { single };

        /// <summary>The colour picker of one run: its own stream, from the run seed.</summary>
        public static HazardBallColourPicker Picker(int runSeed) => new HazardBallColourPicker(unchecked(runSeed ^ PickerSalt));

        /// <summary>One ball's wcid. A single candidate draws nothing.</summary>
        public static uint Pick(IReadOnlyList<uint> candidates, HazardBallColourPicker picker)
        {
            if (candidates == null || candidates.Count == 0)
                return HazardDefaults.RollingDeathRedWcid;

            return candidates.Count == 1 ? candidates[0] : candidates[picker.Next(candidates.Count)];
        }

        /// <summary>RequiredWcids for a ball-spawning run: every candidate, then <paramref name="extra"/>.</summary>
        public static IEnumerable<uint> Required(HazardArgs args, uint single, params uint[] extra)
        {
            foreach (var w in Candidates(args, single))
                yield return w;

            foreach (var w in extra)
                yield return w;
        }
    }

    /// <summary>
    /// A small deterministic stream (SplitMix64) for the ball colour picks: same seed, same picks, on every platform and
    /// .NET version. <see cref="Draws"/> counts the draws taken, so a test can prove a single candidate takes none.
    /// </summary>
    public sealed class HazardBallColourPicker
    {
        private ulong state;

        public HazardBallColourPicker(int seed) => state = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL);

        /// <summary>Draws taken so far.</summary>
        public int Draws { get; private set; }

        /// <summary>A value in [0, <paramref name="n"/>); n must be positive.</summary>
        public int Next(int n)
        {
            if (n <= 0)
                throw new ArgumentOutOfRangeException(nameof(n));

            Draws++;

            var z = unchecked(state += 0x9E3779B97F4A7C15UL);
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            z ^= z >> 31;

            return (int)(z % (ulong)n);
        }
    }
}
