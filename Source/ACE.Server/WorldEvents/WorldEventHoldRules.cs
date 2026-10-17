using System;
using System.Collections.Generic;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The pure rules behind the Hold goal ("protect the targets"): how many of each wave become seekers,
    /// which ones, and where the targets stand (D6 - no clock, no world, so every rule is unit testable).
    /// </summary>
    public static class WorldEventHoldRules
    {
        /// <summary>
        /// How many of a <paramref name="waveSize"/> wave go straight for the protected targets, at
        /// <paramref name="share"/> (0 to 1). <paramref name="carry"/> is the run's running fractional
        /// remainder: the exact share is waveSize * share + carry, the count is its floor, and what is left
        /// carries to the next wave. That keeps a 30% share at 30% over a run of 3-monster waves instead of
        /// rounding to 0 every time.
        ///
        /// A share of 0 or less (or not finite) gives 0 and resets the carry; a share of 1 or more gives the
        /// whole wave.
        /// </summary>
        public static int SeekerCount(int waveSize, double share, ref double carry)
        {
            if (waveSize <= 0)
                return 0;

            if (!double.IsFinite(share) || share <= 0)
            {
                carry = 0;
                return 0;
            }

            if (share >= 1)
            {
                carry = 0;
                return waveSize;
            }

            if (!double.IsFinite(carry) || carry < 0 || carry >= 1)
                carry = 0;

            var exact = waveSize * share + carry;
            var count = (int)Math.Floor(exact);

            carry = exact - count;

            return Math.Min(count, waveSize);
        }

        /// <summary>
        /// <paramref name="count"/> distinct indices drawn uniformly from 0..<paramref name="size"/>-1 (a
        /// partial Fisher-Yates shuffle). Clamped to the size; an empty set for a count of 0 or less.
        /// </summary>
        public static HashSet<int> PickIndices(int size, int count, Random rng)
        {
            var picked = new HashSet<int>();

            if (size <= 0 || count <= 0)
                return picked;

            count = Math.Min(count, size);
            rng ??= new Random();

            var pool = new int[size];

            for (var i = 0; i < size; i++)
                pool[i] = i;

            for (var i = 0; i < count; i++)
            {
                var j = rng.Next(i, size);

                (pool[i], pool[j]) = (pool[j], pool[i]);

                picked.Add(pool[i]);
            }

            return picked;
        }

        /// <summary>
        /// Fills a protect line's tokens (see ProtectLinesDef). {Family} is <paramref name="family"/> with its
        /// first letter capitalized; a missing family falls back to "the attackers", so a line still reads
        /// when a run has no family display name. Unknown tokens are left as written, and so are {anchor}
        /// and {pct} when <paramref name="anchor"/> / <paramref name="pct"/> is null.
        /// </summary>
        public static string FillLine(string template, string family, string noun, string nounPlural, string anchor,
            int alive, int total, int? pct = null)
        {
            if (string.IsNullOrEmpty(template))
                return "";

            var familyText = string.IsNullOrWhiteSpace(family) ? DefaultFamilyText : family.Trim();

            var result = template
                .Replace("{Family}", CapitalizeFirst(familyText))
                .Replace("{family}", familyText)
                .Replace("{nounPlural}", nounPlural ?? "")
                .Replace("{noun}", noun ?? "")
                .Replace("{alive}", alive.ToString())
                .Replace("{total}", total.ToString());

            if (anchor != null)
                result = result.Replace("{anchor}", anchor);

            if (pct != null)
                result = result.Replace("{pct}", pct.Value.ToString());

            return result;
        }

        /// <summary>What {family} reads as when a run has no family display name.</summary>
        public const string DefaultFamilyText = "the attackers";

        /// <summary>"the Emberwrought" to "The Emberwrought". Only the first character changes.</summary>
        public static string CapitalizeFirst(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";

            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>One line from <paramref name="pool"/> at random, skipping blanks; null when there is none.</summary>
        public static string PickLine(IReadOnlyList<string> pool, Random rng)
        {
            if (pool == null || pool.Count == 0)
                return null;

            var usable = new List<string>();

            foreach (var line in pool)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    usable.Add(line);
            }

            if (usable.Count == 0)
                return null;

            rng ??= new Random();

            return usable[rng.Next(usable.Count)];
        }

        /// <summary>
        /// The DreamWeave player-text rule: replaces every em dash (U+2014) and en dash (U+2013) with a plain
        /// hyphen. Returns true when anything changed, so the loader can report it.
        /// </summary>
        public static bool RepairDashes(string text, out string repaired)
        {
            repaired = text;

            if (string.IsNullOrEmpty(text))
                return false;

            repaired = text.Replace((char)0x2014, '-').Replace((char)0x2013, '-');

            return repaired != text;
        }

        /// <summary>An attacker's first hit on a protected target counts as this many seconds of attacking.</summary>
        public const double DefaultHitIntervalSeconds = 2.0;

        /// <summary>
        /// The longest gap one hit may stand for. A monster that wandered off, or missed for a while, does not
        /// get to bank the whole gap into one huge hit.
        /// </summary>
        public const double MaxHitIntervalSeconds = 4.0;

        /// <summary>
        /// The seconds of attacking one hit on a protected target stands for (D6 - pure): the time since
        /// that attacker's previous hit, <see cref="DefaultHitIntervalSeconds"/> for its first, clamped to
        /// [0, <see cref="MaxHitIntervalSeconds"/>].
        /// </summary>
        public static double HitInterval(double? lastHitAt, double now)
        {
            if (lastHitAt == null || !double.IsFinite(lastHitAt.Value) || !double.IsFinite(now))
                return DefaultHitIntervalSeconds;

            return Math.Clamp(now - lastHitAt.Value, 0, MaxHitIntervalSeconds);
        }

        /// <summary>
        /// The damage one event monster's hit deals to a protected target (D6 - pure):
        /// maxHealth * interval / undefendedSeconds, so one attacker hitting continuously empties the target
        /// in undefendedSeconds whatever its weapon, and N attackers in roughly undefendedSeconds / N. At
        /// least 1 for any positive interval. Returns 0 for a zero interval (two hits in the same instant
        /// from the same attacker share the slice) and for any input it cannot use.
        /// </summary>
        public static float ProtectedHitDamage(uint maxHealth, double intervalSeconds, double undefendedSeconds)
        {
            if (maxHealth == 0 || !double.IsFinite(intervalSeconds) || !(intervalSeconds > 0)
                || !double.IsFinite(undefendedSeconds) || !(undefendedSeconds > 0))
                return 0f;

            var damage = maxHealth * intervalSeconds / undefendedSeconds;

            return (float)Math.Max(1.0, Math.Round(damage));
        }

        /// <summary>
        /// A seeker's pick (D6 - pure): among the targets within <paramref name="maxDistSq"/>, the one with
        /// the fewest attackers already on it, the nearest among ties. -1 when none is in range. The two lists
        /// are parallel (one entry per target).
        /// </summary>
        public static int PickLeastAttacked(IReadOnlyList<int> attackers, IReadOnlyList<double> distSq, double maxDistSq)
        {
            var best = -1;

            if (attackers == null || distSq == null)
                return best;

            var n = Math.Min(attackers.Count, distSq.Count);

            for (var i = 0; i < n; i++)
            {
                var d = distSq[i];

                if (!double.IsFinite(d) || d < 0 || d > maxDistSq)
                    continue;

                if (best < 0 || attackers[i] < attackers[best] || (attackers[i] == attackers[best] && d < distSq[best]))
                    best = i;
            }

            return best;
        }

        /// <summary>A hit-rate cap's burst: seconds of the capped rate a target can absorb at once.</summary>
        public const double DefaultCapBurstSeconds = 2.0;

        /// <summary>
        /// The caravan damage cap (D6 - pure): a token bucket that refills at maxHealth / minDeathSeconds per
        /// second, holding at most <see cref="DefaultCapBurstSeconds"/> of that. A hit takes what it asked
        /// for, up to what is in the bucket. So however many attackers pile on, the target loses at most its
        /// whole health over about minDeathSeconds; a single attacker that hits more slowly than that is
        /// never capped. <paramref name="tokens"/>/<paramref name="lastAt"/> are the target's own bucket
        /// state (lastAt 0 = never hit: the bucket starts full). minDeathSeconds 0 or less means no cap.
        /// </summary>
        public static float CapProtectedHit(float requested, uint maxHealth, double minDeathSeconds,
            ref double tokens, ref double lastAt, double now)
        {
            if (!(requested > 0) || maxHealth == 0 || !double.IsFinite(minDeathSeconds) || !(minDeathSeconds > 0) || !double.IsFinite(now))
                return requested;

            var rate = maxHealth / minDeathSeconds;
            var capacity = rate * DefaultCapBurstSeconds;

            if (!(lastAt > 0) || !double.IsFinite(lastAt) || !double.IsFinite(tokens))
                tokens = capacity;
            else
                tokens = Math.Min(capacity, tokens + rate * Math.Max(0, now - lastAt));

            lastAt = now;

            var allowed = Math.Min(requested, tokens);

            tokens -= allowed;

            return (float)allowed;
        }

        /// <summary>
        /// The index of the smallest squared distance that is no more than <paramref name="maxDistSq"/>, or
        /// -1 when none qualifies (empty list, everything too far, or not finite). Ties keep the first.
        /// </summary>
        public static int NearestWithin(IReadOnlyList<double> distSq, double maxDistSq)
        {
            var best = -1;

            if (distSq == null)
                return best;

            for (var i = 0; i < distSq.Count; i++)
            {
                var d = distSq[i];

                if (!double.IsFinite(d) || d < 0 || d > maxDistSq)
                    continue;

                if (best < 0 || d < distSq[best])
                    best = i;
            }

            return best;
        }

        /// <summary>
        /// Hold runs only: whether a wave spawn candidate must be refused because the terrain snap put it in
        /// an indoor cell (inside a building) while the run's anchor is outdoors (D6 - pure). An indoor
        /// anchor is a deliberate indoor event, so it is never refused for that.
        /// </summary>
        public static bool RefusesIndoorSpawn(bool isHoldRun, bool isWave, bool candidateIndoors, bool anchorIndoors)
        {
            return isHoldRun && isWave && candidateIndoors && !anchorIndoors;
        }

        /// <summary>One protected target's offset from the anchor, in metres east/north, and its facing.</summary>
        public readonly struct RingOffset
        {
            public readonly float Dx;
            public readonly float Dy;
            public readonly float YawDegrees;

            public RingOffset(float dx, float dy, float yawDegrees)
            {
                Dx = dx;
                Dy = dy;
                YawDegrees = yawDegrees;
            }
        }

        /// <summary>
        /// Where <paramref name="count"/> targets stand: one target goes at the anchor itself; two or more are
        /// spread evenly on a ring of <paramref name="radius"/> metres, the first due north, each facing
        /// outward. A radius of 0 (or less, or not finite) puts them all at the anchor.
        /// </summary>
        public static List<RingOffset> RingOffsets(int count, double radius)
        {
            var offsets = new List<RingOffset>();

            if (count <= 0)
                return offsets;

            if (count == 1 || !double.IsFinite(radius) || radius <= 0)
            {
                for (var i = 0; i < count; i++)
                    offsets.Add(new RingOffset(0f, 0f, 0f));

                return offsets;
            }

            for (var i = 0; i < count; i++)
            {
                // 0 degrees is due north, increasing clockwise (toward east), the usual compass sense.
                var degrees = 360.0 * i / count;
                var radians = degrees * Math.PI / 180.0;

                var dx = (float)(radius * Math.Sin(radians));
                var dy = (float)(radius * Math.Cos(radians));

                offsets.Add(new RingOffset(dx, dy, (float)degrees));
            }

            return offsets;
        }
    }
}
