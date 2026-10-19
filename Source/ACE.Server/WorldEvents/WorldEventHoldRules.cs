using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

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
        /// One wave's type (D6 - pure). In order:
        ///   * the final counted wave is always the finale, when the goal defines one;
        ///   * an admin-forced type (--wave-type) applies to every other wave;
        ///   * waves before <paramref name="fromWave"/> are ordinary;
        ///   * otherwise a roll against <paramref name="chance"/>, then a weighted pick among the goal's
        ///     special types, never the finale and never the same type as <paramref name="previous"/>.
        /// </summary>
        public static HoldWaveType PickWaveType(int waveNumber, int targetWaves, HoldWaveType previous,
            double chance, int fromWave, IReadOnlyList<SpecialWaveDef> types, Random rng, HoldWaveType? forced)
        {
            var hasFinale = types != null && types.Any(t => t != null && t.TypeKind == HoldWaveType.Finale);

            if (hasFinale && targetWaves > 0 && waveNumber == targetWaves)
                return HoldWaveType.Finale;

            if (forced != null && forced.Value != HoldWaveType.Finale && forced.Value != HoldWaveType.Normal)
                return forced.Value;

            if (types == null || waveNumber < Math.Max(1, fromWave) || !double.IsFinite(chance) || chance <= 0)
                return HoldWaveType.Normal;

            rng ??= new Random();

            if (rng.NextDouble() >= chance)
                return HoldWaveType.Normal;

            var eligible = types.Where(t => t != null && t.TypeKind != HoldWaveType.Finale && t.TypeKind != HoldWaveType.Normal
                && t.TypeKind != previous && t.Weight > 0 && double.IsFinite(t.Weight)).ToList();

            if (eligible.Count == 0)
                return HoldWaveType.Normal;

            var roll = rng.NextDouble() * eligible.Sum(t => t.Weight);

            foreach (var t in eligible)
            {
                roll -= t.Weight;

                if (roll < 0)
                    return t.TypeKind;
            }

            return eligible[eligible.Count - 1].TypeKind;
        }

        /// <summary>
        /// <see cref="PickWaveType(int, int, HoldWaveType, double, int, IReadOnlyList{SpecialWaveDef}, Random, HoldWaveType?)"/>
        /// with a guaranteed minimum (D6 - pure): when <see cref="MustBeSpecial"/> says this middle wave has to
        /// be special to reach <paramref name="minSpecial"/>, the chance roll is skipped and a type is always
        /// drawn. The finale and a forced type win as before.
        /// </summary>
        public static HoldWaveType PickWaveType(int waveNumber, int targetWaves, HoldWaveType previous,
            double chance, int fromWave, IReadOnlyList<SpecialWaveDef> types, Random rng, HoldWaveType? forced,
            int specialsSoFar, int minSpecial)
        {
            var hasFinale = types != null && types.Any(t => t != null && t.TypeKind == HoldWaveType.Finale);

            if (!MustBeSpecial(waveNumber, targetWaves, fromWave, hasFinale, specialsSoFar, minSpecial))
                return PickWaveType(waveNumber, targetWaves, previous, chance, fromWave, types, rng, forced);

            // Always special: a certain roll. A forced type still applies through the same path.
            var type = PickWaveType(waveNumber, targetWaves, previous, 1.0, fromWave, types, rng, forced);

            // Every eligible type was the previous one (a goal with only one special type): repeat it rather
            // than miss the guarantee.
            if (type == HoldWaveType.Normal && types != null)
            {
                var only = types.FirstOrDefault(t => t != null && t.TypeKind != HoldWaveType.Finale && t.TypeKind != HoldWaveType.Normal && t.Weight > 0);

                if (only != null)
                    type = only.TypeKind;
            }

            return type;
        }

        /// <summary>
        /// Whether wave <paramref name="waveNumber"/> of <paramref name="targetWaves"/> must be special so the
        /// run gets at least <paramref name="minSpecial"/> special waves (D6 - pure). Only the middle waves count
        /// (from <paramref name="fromWave"/> to the last before the finale). The guaranteed ones are spread
        /// evenly over that span, and a wave is also forced when the waves left would otherwise be too few.
        /// </summary>
        public static bool MustBeSpecial(int waveNumber, int targetWaves, int fromWave, bool hasFinale, int specialsSoFar, int minSpecial)
        {
            if (minSpecial <= 0 || targetWaves <= 0)
                return false;

            var first = Math.Max(1, fromWave);
            var last = hasFinale ? targetWaves - 1 : targetWaves;

            if (waveNumber < first || waveNumber > last || last < first)
                return false;

            var span = last - first + 1;
            var want = Math.Min(minSpecial, span);
            var need = want - Math.Max(0, specialsSoFar);

            if (need <= 0)
                return false;

            // Catch-up: as many waves left (this one included) as specials still needed.
            if (last - waveNumber + 1 <= need)
                return true;

            return GuaranteedSpecialSlots(first, last, want).Contains(waveNumber);
        }

        /// <summary>
        /// The waves the guaranteed specials fall on (D6 - pure): <paramref name="count"/> slots spread evenly
        /// over waves <paramref name="first"/> to <paramref name="last"/>, for example waves 3 and 5 for two
        /// specials over waves 2 to 6.
        /// </summary>
        public static HashSet<int> GuaranteedSpecialSlots(int first, int last, int count)
        {
            var slots = new HashSet<int>();
            var span = last - first + 1;

            if (span <= 0 || count <= 0)
                return slots;

            count = Math.Min(count, span);

            for (var i = 0; i < count; i++)
                slots.Add(first + (int)Math.Floor((i + 0.5) * span / count));

            return slots;
        }

        /// <summary>
        /// The Hold wave cadence (D6 - pure), used when protect.waveIntervalSeconds is set: the next wave is due
        /// once <paramref name="interval"/> seconds have passed since the last, or earlier once no more than
        /// <paramref name="aliveShare"/> of the last wave's size is still alive. Never sooner than
        /// <paramref name="minGap"/> after the last wave.
        /// </summary>
        public static bool HoldNextWaveDue(double now, double lastWaveAt, double interval, double minGap,
            int liveCount, int lastWaveSize, double aliveShare)
        {
            var since = now - lastWaveAt;

            if (since < Math.Max(0, minGap))
                return false;

            if (interval > 0 && since >= interval)
                return true;

            if (lastWaveSize <= 0)
                return liveCount <= 0;

            return liveCount <= Math.Floor(Math.Max(0, aliveShare) * lastWaveSize);
        }

        /// <summary>
        /// The cadence with protect.nextWaveRequiresBoth (D6 - pure): the next wave waits for BOTH the interval and
        /// most of the field being dead (whichever comes later), so a group that clears fast still gets a breather
        /// and a group that falls behind is not buried. <paramref name="maxInterval"/> (0 = none) sends it anyway, so a
        /// run where nobody kills never stalls. Never sooner than <paramref name="minGap"/>. Without
        /// <paramref name="requireBoth"/> it is the either-or rule above.
        /// </summary>
        public static bool HoldNextWaveDue(double now, double lastWaveAt, double interval, double minGap,
            int liveCount, int lastWaveSize, double aliveShare, bool requireBoth, double maxInterval)
        {
            if (!requireBoth)
                return HoldNextWaveDue(now, lastWaveAt, interval, minGap, liveCount, lastWaveSize, aliveShare);

            var since = now - lastWaveAt;

            if (since < Math.Max(0, minGap))
                return false;

            if (maxInterval > 0 && since >= maxInterval)
                return true;

            var intervalDone = !(interval > 0) || since >= interval;
            var mostDead = lastWaveSize <= 0 ? liveCount <= 0 : liveCount <= Math.Floor(Math.Max(0, aliveShare) * lastWaveSize);

            return intervalDone && mostDead;
        }

        /// <summary>
        /// Whether a Hold ranged-wave monster keeps its distance (D6 - pure): only with a hold distance set and a
        /// ranged attack to use (a missile weapon or spells). A melee-only monster closes in as usual.
        /// </summary>
        public static bool HoldsRange(double holdDistance, bool hasMissileWeapon, bool hasSpells)
        {
            return holdDistance > 0 && (hasMissileWeapon || hasSpells);
        }

        /// <summary>The slack (m) past the hold distance before a ranged monster walks in again, so it does not twitch on the line.</summary>
        public const double RangedHoldSlack = 1.5;

        /// <summary>
        /// Whether a ranged monster is still too far and should walk in (D6 - pure): farther than the hold
        /// distance plus <see cref="RangedHoldSlack"/>.
        /// </summary>
        public static bool RangedHoldShouldApproach(double targetDist, double holdDistance)
        {
            return targetDist > holdDistance + RangedHoldSlack;
        }

        /// <summary>
        /// How many of a <paramref name="waveSize"/> wave take each role (D6 - pure), from protect.waveMix's shares in
        /// role order (Seeker, Breach, Runner, Ranged, Hunter). Largest-remainder rounding, so the counts always
        /// sum to the wave. When the wave has room for every role with a share above 0, each gets at least one
        /// (taken from the biggest role). Shares are normalised; all zero puts the whole wave on Seeker.
        /// </summary>
        public static int[] WaveMixCounts(int waveSize, IReadOnlyList<double> shares)
        {
            var roles = Enum.GetValues(typeof(HoldRole)).Length;
            var counts = new int[roles];

            if (waveSize <= 0)
                return counts;

            var clean = new double[roles];

            for (var i = 0; i < roles && shares != null && i < shares.Count; i++)
                clean[i] = double.IsFinite(shares[i]) && shares[i] > 0 ? shares[i] : 0;

            var total = clean.Sum();

            if (total <= 0)
            {
                counts[(int)HoldRole.Seeker] = waveSize;
                return counts;
            }

            var exact = clean.Select(s => s / total * waveSize).ToArray();

            for (var i = 0; i < roles; i++)
                counts[i] = (int)Math.Floor(exact[i]);

            var left = waveSize - counts.Sum();

            foreach (var i in Enumerable.Range(0, roles).OrderByDescending(i => exact[i] - counts[i]).ThenBy(i => i))
            {
                if (left <= 0)
                    break;

                counts[i]++;
                left--;
            }

            var wanted = Enumerable.Range(0, roles).Where(i => clean[i] > 0).ToList();

            if (waveSize >= wanted.Count)
            {
                foreach (var i in wanted)
                {
                    if (counts[i] > 0)
                        continue;

                    var donor = Enumerable.Range(0, roles).OrderByDescending(j => counts[j]).First();

                    if (counts[donor] <= 1)
                        break;

                    counts[donor]--;
                    counts[i]++;
                }
            }

            return counts;
        }

        /// <summary>
        /// <see cref="WaveMixCounts(int, IReadOnlyList{double})"/> with per-role limits (D6 - pure), in role order:
        /// a role above its maximum (-1 = none) hands the extra to Seeker; a role below its minimum takes monsters
        /// from the biggest other roles, never taking one below its own minimum or below 1 when its share is above
        /// 0. A wave too small for every minimum fills them as far as it can. Always sums to the wave.
        /// </summary>
        public static int[] WaveMixCounts(int waveSize, IReadOnlyList<double> shares, IReadOnlyList<int> minimums, IReadOnlyList<int> maximums)
        {
            var counts = WaveMixCounts(waveSize, shares);
            var roles = counts.Length;

            int Min(int r) => minimums != null && r < minimums.Count ? Math.Max(0, minimums[r]) : 0;
            int Max(int r) => maximums != null && r < maximums.Count ? maximums[r] : -1;
            bool Wanted(int r) => shares != null && r < shares.Count && double.IsFinite(shares[r]) && shares[r] > 0;

            // Maximums: the extra seeks.
            for (var r = 0; r < roles; r++)
            {
                var max = Max(r);

                if (max >= 0 && counts[r] > max && r != (int)HoldRole.Seeker)
                {
                    counts[(int)HoldRole.Seeker] += counts[r] - max;
                    counts[r] = max;
                }
            }

            // No minimum ever takes more than half the wave (3 ranged out of a solo wave of 5 would be most of it).
            int Floor(int r)
            {
                var min = Max(r) >= 0 ? Math.Min(Min(r), Max(r)) : Min(r);
                return Math.Min(min, waveSize / 2);
            }

            // Minimums, in priority order (corrupted, then ranged, then the rest), taken from the biggest roles that
            // can spare one. First pass: never take a wanted role below 1. Second pass, for a wave too small for
            // every role: roles drop out, the least important first (hunters, runners, breach, seekers).
            var order = Enumerable.Range(0, roles).OrderByDescending(r => r).ToList();

            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var r in order)
                {
                    var min = Floor(r);

                    while (counts[r] < min)
                    {
                        var keepOne = pass == 0;

                        var donor = Enumerable.Range(0, roles)
                            .Where(d => d != r && counts[d] > Math.Max(Floor(d), keepOne && Wanted(d) ? 1 : 0))
                            .OrderByDescending(d => counts[d])
                            .ThenBy(d => Array.IndexOf(DropOrder, (HoldRole)d) < 0 ? int.MaxValue : Array.IndexOf(DropOrder, (HoldRole)d))
                            .DefaultIfEmpty(-1)
                            .First();

                        if (donor < 0)
                            break;

                        counts[donor]--;
                        counts[r]++;
                    }
                }
            }

            return counts;
        }

        /// <summary>Which roles give way first when a small wave cannot hold every role (D6): least important first.</summary>
        public static readonly HoldRole[] DropOrder = { HoldRole.Hunter, HoldRole.Runner, HoldRole.Breach, HoldRole.Seeker, HoldRole.Ranged, HoldRole.Corrupted };

        // ---- ward, corrupted aura, portals (D6 - pure) ----------------------------------------------------

        /// <summary>Parses a ward's damage kinds (melee, missile, magic; case-insensitive). False for anything else or an empty list.</summary>
        public static bool TryParseCombatKinds(IEnumerable<string> names, out WorldEventCombatKinds kinds)
        {
            kinds = WorldEventCombatKinds.None;

            if (names == null)
                return false;

            foreach (var name in names)
            {
                switch (name?.Trim().ToLowerInvariant())
                {
                    case "melee": kinds |= WorldEventCombatKinds.Melee; break;
                    case "missile": kinds |= WorldEventCombatKinds.Missile; break;
                    case "magic": kinds |= WorldEventCombatKinds.Magic; break;
                    default: return false;
                }
            }

            return kinds != WorldEventCombatKinds.None;
        }

        /// <summary>What a hit does to a ward (D6 - pure).</summary>
        public enum WardOutcome
        {
            /// <summary>No ward, or a ward that ignores this kind: full damage.</summary>
            None,

            /// <summary>The ward cut the hit by its reduction.</summary>
            Reduced,

            /// <summary>The hit shattered the ward and landed in full.</summary>
            Shattered
        }

        /// <summary>
        /// A hit of <paramref name="kind"/> on a warded monster (D6 - pure). While the ward is up a breaking kind
        /// shatters it (that hit lands in full), and a reduced kind is cut by <paramref name="reduction"/>. While it
        /// is down every hit lands in full. Returns the damage to apply.
        /// </summary>
        public static float WardHit(float damage, WorldEventCombatKinds kind, bool wardUp, double reduction,
            WorldEventCombatKinds reduces, WorldEventCombatKinds breakOn, out WardOutcome outcome)
        {
            outcome = WardOutcome.None;

            if (!wardUp || !(damage > 0))
                return damage;

            if ((breakOn & kind) != 0)
            {
                outcome = WardOutcome.Shattered;
                return damage;
            }

            if ((reduces & kind) != 0 && double.IsFinite(reduction) && reduction > 0)
            {
                outcome = WardOutcome.Reduced;
                return (float)(damage * (1.0 - Math.Clamp(reduction, 0.0, 1.0)));
            }

            return damage;
        }

        /// <summary>
        /// How much harder a wave monster hits a protected target because of corrupted monsters (D6 - pure): the
        /// corrupted monster itself <paramref name="selfMult"/>; one within <paramref name="radius"/> of a living
        /// corrupted monster <paramref name="empowerMult"/>; anyone else 1. <paramref name="nearestCorruptedDist"/>
        /// is the distance to the nearest OTHER living corrupted monster of the run (null when none).
        /// </summary>
        // ---- the corrupted assault (D6 - pure) -------------------------------------------------------------------

        /// <summary>
        /// Whether wave <paramref name="waveNumber"/> is a corrupted assault: assaults on, never the finale, from
        /// <paramref name="fromWave"/> on and every <paramref name="everyWaves"/> waves after it.
        /// </summary>
        public static bool IsAssaultWave(int waveNumber, int everyWaves, int fromWave, bool finale)
        {
            if (everyWaves <= 0 || finale || waveNumber < Math.Max(1, fromWave))
                return false;

            return (waveNumber - Math.Max(1, fromWave)) % everyWaves == 0;
        }

        /// <summary>
        /// The waveMix shares, minimums and maximums in role order for the wave's own monsters: in assault mode no
        /// corrupted among them (the assault's come on top, <see cref="AssignAssaultWave"/>). Unchanged when assaults are off.
        /// </summary>
        public static (double[] Shares, int[] Minimums, int[] Maximums) AssaultMix(IReadOnlyList<double> shares, IReadOnlyList<int> minimums,
            IReadOnlyList<int> maximums, bool assaultMode)
        {
            var s = shares?.ToArray() ?? new double[0];
            var mins = minimums?.ToArray() ?? new int[0];
            var maxs = maximums?.ToArray() ?? new int[0];

            var c = (int)HoldRole.Corrupted;

            if (!assaultMode)
                return (s, mins, maxs);

            if (c < s.Length) s[c] = 0;
            if (c < mins.Length) mins[c] = 0;
            if (c < maxs.Length) maxs[c] = 0;

            return (s, mins, maxs);
        }

        /// <summary>
        /// The roles of an assault wave of <paramref name="size"/> (D6 - pure): <paramref name="assault"/> slots, never a
        /// kept one (an elite, a champion), are Corrupted; the rest are an ordinary mixed wave of their own size
        /// (<see cref="AssignWaveMix(int, IReadOnlyList{double}, IReadOnlyList{int}, IReadOnlyList{int}, ISet{int}, Random)"/>
        /// with the corrupted role removed), so the assault never eats the breach squad or the ranged.
        /// </summary>
        public static HoldRole[] AssignAssaultWave(int size, int assault, IReadOnlyList<double> shares, IReadOnlyList<int> minimums,
            IReadOnlyList<int> maximums, ISet<int> keepIndices, Random rng)
        {
            var roles = new HoldRole[Math.Max(0, size)];

            if (size <= 0)
                return roles;

            rng ??= new Random();

            var candidates = Enumerable.Range(0, size).Where(i => keepIndices == null || !keepIndices.Contains(i)).ToList();

            for (var i = candidates.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            var assaultSlots = new HashSet<int>(candidates.Take(Math.Clamp(assault, 0, candidates.Count)));
            var rest = Enumerable.Range(0, size).Where(i => !assaultSlots.Contains(i)).ToList();

            var innerKeep = new HashSet<int>();

            for (var j = 0; j < rest.Count; j++)
                if (keepIndices != null && keepIndices.Contains(rest[j]))
                    innerKeep.Add(j);

            var mix = AssaultMix(shares, minimums, maximums, true);
            var inner = AssignWaveMix(rest.Count, mix.Shares, mix.Minimums, mix.Maximums, innerKeep, rng);

            for (var j = 0; j < rest.Count; j++)
                roles[rest[j]] = inner[j];

            foreach (var i in assaultSlots)
                roles[i] = HoldRole.Corrupted;

            return roles;
        }

        /// <summary>
        /// Which target each of <paramref name="count"/> assault monsters marches on: the standing targets in a shuffled
        /// order, a different one each while enough stand, then round again. Empty when none stands.
        /// </summary>
        public static List<int> AssaultTargets(int standing, int count, Random rng)
        {
            var result = new List<int>();

            if (standing <= 0 || count <= 0)
                return result;

            rng ??= new Random();

            var order = Enumerable.Range(0, standing).ToList();

            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            for (var k = 0; k < count; k++)
                result.Add(order[k % order.Count]);

            return result;
        }

        /// <summary>The call-out when an assault monster is sent (D6 - pure): {monster} and {target} filled; the default when the pool is empty.</summary>
        public static string AssaultLine(string template, string monster, string target)
        {
            var line = string.IsNullOrWhiteSpace(template) ? "A {monster} marches on {target}!" : template;

            return line.Replace("{monster}", string.IsNullOrWhiteSpace(monster) ? "corrupted foe" : monster.Trim())
                       .Replace("{target}", string.IsNullOrWhiteSpace(target) ? "the envoys" : target.Trim());
        }

        /// <summary>
        /// The finale boss's size multiplier (D6 - pure): <paramref name="configured"/> (protect.finaleBossScale) within
        /// 1 to 2.5, or, when it is 0 or less, <paramref name="worldEventBossScale"/>: the same multiplier every other World
        /// Events boss gets (world_events_boss_scale_multiplier), so the Warlord is the size of the event's other bosses.
        /// </summary>
        public static float FinaleBossScale(double configured, float worldEventBossScale)
        {
            if (!double.IsFinite(configured))
                return 1.0f;

            if (configured <= 0)
                return worldEventBossScale > 0 && float.IsFinite(worldEventBossScale) ? worldEventBossScale : 1.0f;

            return (float)Math.Clamp(configured, 1.0, 2.5);
        }

        /// <summary>
        /// Whether a run monster's hit on a target goes through protect.minDeathSeconds (D6 - pure): every hit does,
        /// except a corrupted monster's in assault mode, whose job is to break its one target unless players stop it.
        /// </summary>
        public static bool UsesDeathCap(bool attackerCorrupted, bool assaultMode) => !(attackerCorrupted && assaultMode);

        public static double EmpowerFactor(bool attackerIsCorrupted, double? nearestCorruptedDist, double radius, double empowerMult, double selfMult)
        {
            if (attackerIsCorrupted)
                return double.IsFinite(selfMult) && selfMult > 1 ? selfMult : 1.0;

            if (nearestCorruptedDist == null || !(radius > 0) || !(nearestCorruptedDist.Value <= radius))
                return 1.0;

            return double.IsFinite(empowerMult) && empowerMult > 1 ? empowerMult : 1.0;
        }

        /// <summary>
        /// A title in front of a name (D6 - pure): "Corrupted Shallows Gorger". The name is kept as it is (an elite
        /// stays "Corrupted Elite ..."), and a name that already starts with the title is left alone.
        /// </summary>
        public static string Titled(string prefix, string name)
        {
            var bare = (name ?? "").Trim();

            if (string.IsNullOrWhiteSpace(prefix))
                return bare;

            var p = prefix.Trim();

            if (bare.StartsWith(p + " ", StringComparison.Ordinal))
                return bare;

            return string.IsNullOrEmpty(bare) ? p : $"{p} {bare}";
        }

        /// <summary>
        /// Where the portals stand (D6 - pure): <paramref name="count"/> points evenly on a ring of
        /// <paramref name="radius"/>, the first at compass bearing <paramref name="rotationDegrees"/>, each facing
        /// outward like a target (so a portal's parts can be laid out with <see cref="PlaceTargetDecor"/>).
        /// </summary>
        public static List<RingOffset> PortalOffsets(int count, double radius, double rotationDegrees)
        {
            var offsets = new List<RingOffset>();

            if (count <= 0 || !double.IsFinite(radius) || radius <= 0)
                return offsets;

            var rotation = double.IsFinite(rotationDegrees) ? rotationDegrees : 0;

            for (var i = 0; i < count; i++)
            {
                var degrees = ((rotation + 360.0 * i / count) % 360.0 + 360.0) % 360.0;
                var radians = degrees * Math.PI / 180.0;

                offsets.Add(new RingOffset((float)(radius * Math.Sin(radians)), (float)(radius * Math.Cos(radians)), OutwardYaw(degrees)));
            }

            return offsets;
        }

        /// <summary>The compass bearings, in radians (0 = north, clockwise), of <see cref="PortalOffsets"/> (D6 - pure).</summary>
        public static List<double> PortalBearings(int count, double rotationDegrees)
        {
            var bearings = new List<double>();
            var rotation = double.IsFinite(rotationDegrees) ? rotationDegrees : 0;

            for (var i = 0; i < count; i++)
                bearings.Add((((rotation + 360.0 * i / count) % 360.0 + 360.0) % 360.0) * Math.PI / 180.0);

            return bearings;
        }

        /// <summary>
        /// One portal's parts at <paramref name="scale"/> (D6 - pure): dx, dy, dz and scale multiplied, everything else
        /// kept, so a full-size authored portal (the acid portal theme's rings) shrinks about its own base. Always
        /// snapped: dz is metres above the ground under the portal.
        /// </summary>
        public static List<DecorDef> ScalePortalParts(IReadOnlyList<DecorDef> parts, double scale)
        {
            var s = (float)(double.IsFinite(scale) && scale > 0 ? scale : 1.0);

            return (parts ?? new List<DecorDef>()).Where(p => p != null).Select(p => new DecorDef
            {
                Wcid = p.Wcid,
                Dx = p.Dx * s,
                Dy = p.Dy * s,
                Dz = p.Dz * s,
                Scale = (p.Scale > 0 ? p.Scale : 1f) * s,
                Speed = p.Speed,
                Inverted = p.Inverted,
                Yaw = p.Yaw,
                Pitch = p.Pitch,

                // Always on the ground where the portal stands, which can be well above or below the anchor.
                Snap = true
            }).ToList();
        }

        /// <summary>
        /// How much of a heal a target's per-wave budget lets through (D6 - pure): <paramref name="requested"/> health,
        /// against a budget of <paramref name="budgetShare"/> of <paramref name="maxHealth"/> of which
        /// <paramref name="spentThisWave"/> is used. A budget of 0 or less means no budget. Never negative.
        /// </summary>
        public static int HealWithinBudget(int requested, uint maxHealth, double budgetShare, int spentThisWave)
        {
            if (requested <= 0)
                return 0;

            if (!(budgetShare > 0) || !double.IsFinite(budgetShare))
                return requested;

            var budget = (int)Math.Floor(Math.Min(1.0, budgetShare) * maxHealth);

            return Math.Max(0, Math.Min(requested, budget - Math.Max(0, spentThisWave)));
        }

        /// <summary>What one heal on a protected target came to (D6).</summary>
        public enum ProtectedHealOutcome
        {
            /// <summary>Some health was restored.</summary>
            Healed,

            /// <summary>Nothing: the target was already at full health.</summary>
            Full,

            /// <summary>Nothing: the target's healing budget for this wave is spent.</summary>
            BudgetSpent,

            /// <summary>Nothing: not a living protected target, or nothing asked for.</summary>
            None
        }

        /// <summary>Whether a kill counts as a rescue (D6 - pure): a player's kill, or a pet's (it has an owner). Never a monster's.</summary>
        public static bool IsRescueKill(bool killerIsPlayer, bool killerHasPetOwner) => killerIsPlayer || killerHasPetOwner;

        /// <summary>
        /// Whether a target is clear enough to regenerate (D6 - pure): no living wave monster within
        /// <paramref name="radius"/> metres. <paramref name="nearestMonster"/> is null when none is alive.
        /// </summary>
        public static bool IsClear(double? nearestMonster, double radius)
        {
            return nearestMonster == null || !(radius > 0) || nearestMonster.Value > radius;
        }

        /// <summary>
        /// Whether a ready shrine plays its ready effect now (D6 - pure): ready (<paramref name="now"/> past
        /// <paramref name="readyAt"/>, 0 = never used), and either it has not pulsed since it became ready or the last
        /// pulse is <paramref name="period"/> seconds old. A period of 0 means no repeat: once per time it becomes ready.
        /// </summary>
        public static bool ShrineReadyPulseDue(double now, double readyAt, double pulsedAt, double period)
        {
            if (now < readyAt)
                return false;

            // No repeat: once when it becomes ready (and once when it is first placed).
            if (!(period > 0))
                return pulsedAt <= 0 || pulsedAt < readyAt;

            return pulsedAt < readyAt || pulsedAt <= 0 || now - pulsedAt >= period;
        }

        /// <summary>What a player is told using a shrine that is resting (D6 - pure). ASCII.</summary>
        public static string ShrineCooldownLine(double secondsLeft)
        {
            var s = (int)Math.Ceiling(Math.Max(1, secondsLeft));

            return $"The shrine is still gathering its strength. It will be ready in {s} second{(s == 1 ? "" : "s")}.";
        }

        /// <summary>What a player is told when a shrine heal restores nothing (D6 - pure). ASCII.</summary>
        public static string ShrineWastedLine(string targetName, ProtectedHealOutcome outcome)
        {
            var name = string.IsNullOrWhiteSpace(targetName) ? "The defender" : targetName;

            return outcome == ProtectedHealOutcome.BudgetSpent
                ? $"{name} can draw no more strength this wave. The shrine holds its power for now."
                : $"{name} needs no restoring. The shrine holds its power for now.";
        }

        /// <summary>One target's shrine as a decor entry in the target's own frame (D6 - pure): on the ground, flagged as a shrine.</summary>
        public static DecorDef ShrineDecor(ShrineDef shrine)
        {
            return new DecorDef
            {
                Wcid = shrine?.Wcid ?? 0,
                Dx = (float)(shrine?.Dx ?? 0),
                Dy = (float)(shrine?.Dy ?? 0),
                Scale = (float)Math.Clamp(shrine?.Scale ?? 1, 0.1, 3),
                Speed = 1,
                Snap = true,
                HoldShrine = true
            };
        }

        /// <summary>Every <paramref name="from"/> role becomes <paramref name="to"/> (D6 - pure). Returns how many moved.</summary>
        public static int ReassignRole(HoldRole[] roles, HoldRole from, HoldRole to)
        {
            if (roles == null)
                return 0;

            var moved = 0;

            for (var i = 0; i < roles.Length; i++)
            {
                if (roles[i] == from)
                {
                    roles[i] = to;
                    moved++;
                }
            }

            return moved;
        }

        /// <summary>The attacker count an avoided target is given, so it is only ever picked when nothing else is in reach.</summary>
        public const int AvoidedTargetPenalty = 10000;

        /// <summary>
        /// Attacker counts with <paramref name="avoidGuid"/>'s target made a last resort (D6 - pure): its count is
        /// raised by <see cref="AvoidedTargetPenalty"/> for the least-attacked pick. Unchanged when the guid is 0
        /// or not among the targets. The lists are parallel.
        /// </summary>
        public static List<int> AvoidTarget(IReadOnlyList<int> attackers, IReadOnlyList<uint> guids, uint avoidGuid)
        {
            var result = attackers?.ToList() ?? new List<int>();

            if (avoidGuid == 0 || guids == null)
                return result;

            for (var i = 0; i < result.Count && i < guids.Count; i++)
            {
                if (guids[i] == avoidGuid)
                    result[i] += AvoidedTargetPenalty;
            }

            return result;
        }

        /// <summary>
        /// One role per monster of a mixed wave (D6 - pure): <see cref="WaveMixCounts"/>, laid over the wave in a
        /// random order. Ranged never lands on a <paramref name="keepIndices"/> entry (an elite or an overflow
        /// champion, which keep their own monster), so those take another role, swapped with a plain slot.
        /// </summary>
        public static HoldRole[] AssignWaveMix(int waveSize, IReadOnlyList<double> shares, ISet<int> keepIndices, Random rng)
        {
            return AssignWaveMix(waveSize, shares, null, null, keepIndices, rng);
        }

        /// <summary><see cref="AssignWaveMix(int, IReadOnlyList{double}, ISet{int}, Random)"/> with per-role minimums and maximums (see the limited WaveMixCounts).</summary>
        public static HoldRole[] AssignWaveMix(int waveSize, IReadOnlyList<double> shares, IReadOnlyList<int> minimums, IReadOnlyList<int> maximums,
            ISet<int> keepIndices, Random rng)
        {
            var roles = new HoldRole[Math.Max(0, waveSize)];

            if (waveSize <= 0)
                return roles;

            rng ??= new Random();

            var counts = WaveMixCounts(waveSize, shares, minimums, maximums);
            var bag = new List<HoldRole>();

            for (var r = 0; r < counts.Length; r++)
                for (var k = 0; k < counts[r]; k++)
                    bag.Add((HoldRole)r);

            // Shuffle, so roles spread over the monster types the pick drew.
            for (var i = bag.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (bag[i], bag[j]) = (bag[j], bag[i]);
            }

            for (var i = 0; i < waveSize; i++)
                roles[i] = bag[i];

            if (keepIndices != null)
            {
                foreach (var kept in keepIndices)
                {
                    if (kept < 0 || kept >= waveSize || roles[kept] != HoldRole.Ranged)
                        continue;

                    var swap = Enumerable.Range(0, waveSize).FirstOrDefault(i => !keepIndices.Contains(i) && roles[i] != HoldRole.Ranged, -1);

                    if (swap >= 0)
                        (roles[kept], roles[swap]) = (roles[swap], roles[kept]);
                    else
                        roles[kept] = HoldRole.Seeker;
                }
            }

            return roles;
        }

        /// <summary>
        /// A mixed wave's trash with its Ranged slots redrawn from the family's ranged and caster members (D6 -
        /// pure). Other slots, and every slot when the pool is empty, keep their monster.
        /// </summary>
        public static List<uint> ReplaceRangedSlots(IReadOnlyList<uint> trash, IReadOnlyList<HoldRole> roles, IReadOnlyList<uint> rangedPool, Random rng)
        {
            var result = trash?.ToList() ?? new List<uint>();

            if (rangedPool == null || rangedPool.Count == 0 || roles == null)
                return result;

            rng ??= new Random();

            for (var i = 0; i < result.Count && i < roles.Count; i++)
            {
                if (roles[i] == HoldRole.Ranged)
                    result[i] = rangedPool[rng.Next(rangedPool.Count)];
            }

            return result;
        }

        /// <summary>
        /// The final wave without copies of its boss (D6 - pure): every <paramref name="bossWcid"/> in
        /// <paramref name="trash"/> is swapped for another monster of the same wave, so the boss is the only one of
        /// its kind. Unchanged when the wave has nothing else to swap in.
        /// </summary>
        public static List<uint> WithoutBossLookalikes(IReadOnlyList<uint> trash, uint bossWcid, Random rng)
        {
            var result = trash?.ToList() ?? new List<uint>();
            var others = result.Where(w => w != bossWcid).ToList();

            if (bossWcid == 0 || others.Count == 0)
                return result;

            rng ??= new Random();

            for (var i = 0; i < result.Count; i++)
            {
                if (result[i] == bossWcid)
                    result[i] = others[rng.Next(others.Count)];
            }

            return result;
        }

        /// <summary>
        /// The finale boss's display name (D6 - pure): <paramref name="prefix"/> in front of its own name, with any
        /// "Champion " or "Elite " a promotion put there taken off first ("Warlord Apostate Shock Trooper").
        /// </summary>
        public static string FinaleBossName(string prefix, string name)
        {
            var bare = (name ?? "").Trim();

            foreach (var promoted in new[] { "Champion ", "Elite " })
            {
                if (bare.StartsWith(promoted, StringComparison.Ordinal))
                    bare = bare.Substring(promoted.Length);
            }

            if (string.IsNullOrWhiteSpace(prefix))
                return string.IsNullOrEmpty(bare) ? "the champion" : bare;

            var p = prefix.Trim();

            if (bare.StartsWith(p + " ", StringComparison.Ordinal))
                return bare;

            return string.IsNullOrEmpty(bare) ? p : $"{p} {bare}";
        }

        /// <summary>protect.finaleBossPrefix: blank, or letters and spaces up to 20 characters (D6 - pure).</summary>
        public static bool IsValidBossPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return true;

            return prefix.Length <= 20 && prefix.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == ' ');
        }

        /// <summary>The label a phase broadcast starts with (D6 - pure): "Final wave" for the finale, else "Wave 4 of 7".</summary>
        public static string PhaseLabel(int waveNumber, int targetWaves, bool isFinale)
        {
            return isFinale ? "Final wave" : WaveProgress(waveNumber, targetWaves);
        }

        /// <summary>A target's health as a whole percent (D6 - pure), 0 to 100, rounded up so a living target never reads 0.</summary>
        public static int HealthPercent(uint current, uint max)
        {
            if (max == 0 || current == 0)
                return 0;

            return (int)Math.Clamp(Math.Ceiling(current * 100.0 / max), 1, 100);
        }

        /// <summary>
        /// Every standing target's health, for the {targetHealth} progress token (D6 - pure):
        /// "Sir Draithon 80%, Sir Donovan 64%". Empty when none stand.
        /// </summary>
        public static string TargetHealthSummary(IEnumerable<(string Name, uint Current, uint Max)> targets)
        {
            if (targets == null)
                return "";

            return string.Join(", ", targets.Where(t => t.Current > 0)
                .Select(t => $"{(string.IsNullOrWhiteSpace(t.Name) ? "?" : t.Name)} {HealthPercent(t.Current, t.Max)}%"));
        }

        /// <summary>The line a player gets when assessing a protected target (D6 - pure): "Sir Donovan is at 64% health."</summary>
        public static string ProtectedHealthLine(string name, uint current, uint max)
        {
            return $"{(string.IsNullOrWhiteSpace(name) ? "It" : name)} is at {HealthPercent(current, max)}% health.";
        }

        /// <summary>
        /// protect.bossEffect (D6 - pure): a PlayScript name (case-insensitive) or "none" (and null or blank,
        /// which read as none). False for anything else.
        /// </summary>
        public static bool TryParseBossEffect(string name, out ACE.Entity.Enum.PlayScript script)
        {
            script = ACE.Entity.Enum.PlayScript.Invalid;

            if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                return true;

            var trimmed = name.Trim();

            if (!trimmed.All(char.IsLetterOrDigit) || !Enum.TryParse(trimmed, true, out script))
                return false;

            return Enum.IsDefined(typeof(ACE.Entity.Enum.PlayScript), script) && script != ACE.Entity.Enum.PlayScript.Invalid;
        }

        /// <summary>
        /// A runner's damage to a protected target (D6 - pure): the time-based slice for an attacker that swings
        /// <paramref name="attackSpeedMult"/> times as fast, so one runner alone destroys a target in
        /// undefendedSeconds / attackSpeedMult. A multiplier of 1 or less is the plain slice.
        /// </summary>
        public static float ProtectedHitDamage(uint maxHealth, double intervalSeconds, double undefendedSeconds, double attackSpeedMult)
        {
            var mult = double.IsFinite(attackSpeedMult) && attackSpeedMult > 1 ? attackSpeedMult : 1.0;

            return ProtectedHitDamage(maxHealth, intervalSeconds, undefendedSeconds / mult);
        }

        /// <summary>Parses a special wave type name (case-insensitive). False for anything unknown or "normal".</summary>
        public static bool TryParseWaveType(string name, out HoldWaveType type)
        {
            if (!string.IsNullOrWhiteSpace(name) && name.Trim().All(char.IsLetter) && Enum.TryParse(name.Trim(), true, out type) && type != HoldWaveType.Normal && type != HoldWaveType.Mixed
                && Enum.IsDefined(typeof(HoldWaveType), type))
                return true;

            type = HoldWaveType.Normal;
            return false;
        }

        private static readonly string[] CompassNames = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

        /// <summary>
        /// Which side of the anchor a point is on (D6 - pure), as one of eight compass words, from its offset
        /// (<paramref name="dx"/> metres east, <paramref name="dy"/> north). "center" when it sits on the anchor.
        /// </summary>
        public static string CompassSide(double dx, double dy)
        {
            if (!double.IsFinite(dx) || !double.IsFinite(dy) || (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5))
                return "center";

            var degrees = Math.Atan2(dx, dy) * 180.0 / Math.PI;

            if (degrees < 0)
                degrees += 360.0;

            var index = (int)Math.Round(degrees / 45.0) % 8;

            return CompassNames[index];
        }

        /// <summary>
        /// The share of a heal that lands on a target last hit <paramref name="secondsSinceHit"/> ago (D6 - pure):
        /// 1 - <paramref name="reduction"/> inside <paramref name="windowSeconds"/>, else 1. A target never hit
        /// (null) heals in full.
        /// </summary>
        public static double HealAfterHitFactor(double? secondsSinceHit, double reduction, double windowSeconds)
        {
            if (secondsSinceHit == null || !double.IsFinite(secondsSinceHit.Value) || !(reduction > 0) || !(windowSeconds > 0))
                return 1.0;

            if (secondsSinceHit.Value < 0 || secondsSinceHit.Value >= windowSeconds)
                return 1.0;

            return Math.Max(0.0, 1.0 - Math.Min(1.0, reduction));
        }

        /// <summary>The index of the highest health fraction (current / max), or -1 for an empty list. Ties keep the first.</summary>
        public static int PickHealthiest(IReadOnlyList<(uint Current, uint Max)> health)
        {
            var best = -1;
            var bestFraction = -1.0;

            if (health == null)
                return best;

            for (var i = 0; i < health.Count; i++)
            {
                var (current, max) = health[i];

                if (max == 0)
                    continue;

                var fraction = (double)current / max;

                if (fraction > bestFraction)
                {
                    best = i;
                    bestFraction = fraction;
                }
            }

            return best;
        }

        /// <summary>
        /// A ranged wave's monsters (D6 - pure): each wcid in <paramref name="wave"/> is replaced, in turn, by one
        /// of the <paramref name="rangedPool"/> wcids (cycling); with an empty pool the wave is returned unchanged,
        /// which the caller logs as "no ranged members, ordinary wave".
        /// </summary>
        public static List<uint> ReplaceWithRanged(IReadOnlyList<uint> wave, IReadOnlyList<uint> rangedPool, Random rng)
        {
            var result = new List<uint>(wave ?? Array.Empty<uint>());

            if (rangedPool == null || rangedPool.Count == 0)
                return result;

            rng ??= new Random();

            var offset = rng.Next(rangedPool.Count);

            for (var i = 0; i < result.Count; i++)
                result[i] = rangedPool[(i + offset) % rangedPool.Count];

            return result;
        }

        /// <summary>
        /// A hunter wave's hunters (D6 - pure): <paramref name="hunters"/> indices of a <paramref name="waveSize"/>
        /// batch, non-seekers first (in order) and then seekers, never <paramref name="excludedIndex"/> (the
        /// finale champion). A seeker picked here stops being a seeker.
        /// </summary>
        public static HashSet<int> PickHunterIndices(int waveSize, int hunters, ISet<int> seekers, int excludedIndex)
        {
            var result = new HashSet<int>();

            if (waveSize <= 0 || hunters <= 0)
                return result;

            var order = Enumerable.Range(0, waveSize).Where(i => i != excludedIndex)
                .OrderBy(i => seekers != null && seekers.Contains(i) ? 1 : 0).ThenBy(i => i);

            foreach (var i in order)
            {
                if (result.Count >= hunters)
                    break;

                result.Add(i);
            }

            return result;
        }

        /// <summary>
        /// Whether an awake seeker has strayed (D6 - pure): it is a seeker, its current target is not a
        /// protected target, and at least one target still stands for it to go back to.
        /// </summary>
        public static bool SeekerStrayed(bool isSeeker, bool targetIsProtected, bool targetsStanding)
        {
            return isSeeker && !targetIsProtected && targetsStanding;
        }

        /// <summary>
        /// The per-wave seeker summary for the log (D6 - pure): how many of the wave's creatures are seekers,
        /// hunters and others, with the targets the seekers were sent at (guid to count).
        /// </summary>
        public static string WaveRoleSummary(int waveNumber, IReadOnlyList<(bool Seeker, bool Hunter, uint ForcedGuid)> roles)
        {
            roles ??= Array.Empty<(bool, bool, uint)>();

            var seekers = roles.Count(r => r.Seeker);
            var hunters = roles.Count(r => r.Hunter);
            var others = roles.Count - seekers - hunters;
            var forced = roles.Where(r => r.Seeker && r.ForcedGuid != 0).GroupBy(r => r.ForcedGuid)
                .Select(g => $"0x{g.Key:X8}x{g.Count()}").ToList();

            return $"hold wave {waveNumber} roles: seekers={seekers} hunters={hunters} others={others}" +
                   (forced.Count > 0 ? $" breach=[{string.Join(",", forced)}]" : "");
        }

        /// <summary>How many of a <paramref name="waveSize"/> wave a Runners wave keeps: the share, at least one.</summary>
        public static int RunnersKeep(int waveSize, double countMult)
        {
            if (waveSize <= 0)
                return 0;

            if (!double.IsFinite(countMult) || countMult <= 0 || countMult >= 1)
                return waveSize;

            return Math.Max(1, (int)Math.Round(waveSize * countMult));
        }

        /// <summary>Fills a special wave line's {side} and {champion} tokens; the rest are left for FillLine.</summary>
        public static string FillWaveTokens(string template, string side, string champion)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            return template
                .Replace("{side}", string.IsNullOrWhiteSpace(side) ? "nearest" : side)
                .Replace("{champion}", string.IsNullOrWhiteSpace(champion) ? "Their champion" : champion);
        }

        /// <summary><see cref="FillWaveTokens(string, string, string)"/> plus {target}: the breach squad's target by name ("one of us" when unknown).</summary>
        public static string FillWaveTokens(string template, string side, string champion, string target)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            return FillWaveTokens(template.Replace("{target}", string.IsNullOrWhiteSpace(target) ? "one of us" : target), side, champion);
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
        /// A token bucket (D6 - pure): refills at <paramref name="ratePerSecond"/> up to
        /// <paramref name="capacity"/>, starting full (lastAt 0 = never used), and lets through at most what it
        /// holds. Shared by the target damage cap and the player-heal cap.
        /// </summary>
        public static double TakeFromBucket(double requested, double ratePerSecond, double capacity,
            ref double tokens, ref double lastAt, double now)
        {
            if (!double.IsFinite(requested))
                return 0;

            if (requested <= 0)
                return requested;

            if (!(ratePerSecond > 0) || !(capacity > 0) || !double.IsFinite(now))
                return 0;

            if (!(lastAt > 0) || !double.IsFinite(lastAt) || !double.IsFinite(tokens))
                tokens = capacity;
            else
                tokens = Math.Min(capacity, tokens + ratePerSecond * Math.Max(0, now - lastAt));

            lastAt = now;

            var allowed = Math.Min(requested, tokens);

            tokens -= allowed;

            return allowed;
        }

        /// <summary>What counts as one typical player heal (a Heal VI cast or a decent healing kit), in raw health points.</summary>
        public const double TypicalPlayerHeal = 100.0;

        /// <summary>The share of a protected target's max health one typical player heal restores.</summary>
        public const double HealShareOfTypicalHeal = 0.04;

        /// <summary>
        /// A player's heal on a protected target, converted (D6 - pure): damage to targets is time-based (a
        /// share of max health), so a raw heal is converted the same way - one typical player heal restores
        /// <see cref="HealShareOfTypicalHeal"/> of max health, bigger heals proportionally more - then capped
        /// by the target's own bucket at maxHealPerTenSeconds of max health per ten seconds. Returns the health
        /// to restore (0 when healing is off or the cap is spent).
        /// </summary>
        public static int ConvertPlayerHeal(double rawHeal, uint maxHealth, double maxHealPerTenSeconds,
            ref double tokens, ref double lastAt, double now)
        {
            if (!(rawHeal > 0) || maxHealth == 0 || !double.IsFinite(maxHealPerTenSeconds) || !(maxHealPerTenSeconds > 0))
                return 0;

            var wanted = maxHealth * HealShareOfTypicalHeal * (rawHeal / TypicalPlayerHeal);

            var windowAllowance = maxHealth * maxHealPerTenSeconds;

            var allowed = TakeFromBucket(wanted, windowAllowance / 10.0, windowAllowance, ref tokens, ref lastAt, now);

            return (int)Math.Floor(allowed);
        }

        /// <summary>The health one between-waves heal restores: max health times the fraction, at least 1 when the fraction is above 0.</summary>
        public static int BetweenWavesHeal(uint maxHealth, double fraction)
        {
            if (maxHealth == 0 || !double.IsFinite(fraction) || !(fraction > 0))
                return 0;

            return Math.Max(1, (int)Math.Round(maxHealth * Math.Min(1.0, fraction)));
        }

        /// <summary>How much bigger (in creatures) the final counted wave is, for a wave of <paramref name="baseSize"/>: a quarter more, at least one.</summary>
        public static int FinalWaveBonus(int baseSize)
        {
            return Math.Max(1, (int)Math.Ceiling(Math.Max(0, baseSize) * 0.25));
        }

        /// <summary>
        /// "Wave 3 of 7"; "Wave 0 of 7" before the first. Never a number above the total.
        /// </summary>
        public static string WaveProgress(int started, int target)
        {
            // Never "Wave 6 of 5": a wave count past the total (which the cap should make impossible) reads as
            // the last wave.
            target = Math.Max(0, target);
            started = Math.Max(0, started);

            if (target > 0 && started > target)
                started = target;

            return $"Wave {started} of {target}";
        }

        /// <summary>
        /// The target damage cap (D6 - pure): a token bucket that refills at maxHealth / minDeathSeconds per
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

            return (float)TakeFromBucket(requested, rate, rate * DefaultCapBurstSeconds, ref tokens, ref lastAt, now);
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
                // Placement bearing: 0 degrees is due north, increasing clockwise (toward east), the usual
                // compass sense.
                var degrees = 360.0 * i / count;
                var radians = degrees * Math.PI / 180.0;

                var dx = (float)(radius * Math.Sin(radians));
                var dy = (float)(radius * Math.Cos(radians));

                offsets.Add(new RingOffset(dx, dy, OutwardYaw(degrees)));
            }

            return offsets;
        }

        /// <summary>
        /// The npc yaw that faces AWAY from the centre for a target at compass <paramref name="bearingDegrees"/>
        /// (D6 - pure). NpcRotation's yaw 0 faces north and positive yaw turns COUNTER-clockwise, while a
        /// compass bearing turns clockwise, so the outward heading is the negated bearing, normalised to
        /// [0, 360).
        /// </summary>
        public static float OutwardYaw(double bearingDegrees)
        {
            var yaw = -bearingDegrees % 360.0;

            if (yaw < 0)
                yaw += 360.0;

            return (float)yaw;
        }

        /// <summary>
        /// A Hold scene's per-target props (D6 - pure): each entry of <paramref name="local"/> is in a target's
        /// own frame - Dx metres to its RIGHT, Dy metres FORWARD (outward, the way it faces), Yaw relative to
        /// its facing - and is placed once for every target in <paramref name="targets"/>, giving
        /// anchor-relative copies (Dx east, Dy north, Yaw absolute) for the ordinary decor placement. Every
        /// other field is copied unchanged. Lets one goals.json list put a prop beside each target
        /// whatever the count or radius.
        /// </summary>
        public static List<DecorDef> PlaceTargetDecor(IReadOnlyList<RingOffset> targets, IReadOnlyList<DecorDef> local)
        {
            var placed = new List<DecorDef>();

            if (targets == null || local == null)
                return placed;

            foreach (var target in targets)
            {
                // The target faces outward: forward is its bearing from the anchor, right is 90 degrees clockwise
                // of that. Recovered from the yaw so a target sitting on the anchor (no offset) still has one.
                var bearing = -target.YawDegrees * Math.PI / 180.0;

                var forwardX = Math.Sin(bearing);
                var forwardY = Math.Cos(bearing);
                var rightX = Math.Cos(bearing);
                var rightY = -Math.Sin(bearing);

                foreach (var entry in local)
                {
                    if (entry == null)
                        continue;

                    placed.Add(new DecorDef
                    {
                        Wcid = entry.Wcid,
                        Dx = (float)(target.Dx + entry.Dx * rightX + entry.Dy * forwardX),
                        Dy = (float)(target.Dy + entry.Dx * rightY + entry.Dy * forwardY),
                        Dz = entry.Dz,
                        Scale = entry.Scale,
                        Speed = entry.Speed,
                        Inverted = entry.Inverted,
                        Yaw = entry.Yaw + target.YawDegrees,
                        Pitch = entry.Pitch,
                        Snap = entry.Snap,
                        HoldShrine = entry.HoldShrine
                    });
                }
            }

            return placed;
        }

        /// <summary>
        /// Fills a Hold outcome line's count tokens (D6 - pure): {alive}, {total} and {lost} as they stood when
        /// the run finished, so the success and fail broadcasts can say "2 of 3 targets survived".
        /// </summary>
        public static string FillCounts(string template, int alive, int total)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            return template
                .Replace("{alive}", alive.ToString())
                .Replace("{total}", total.ToString())
                .Replace("{lost}", Math.Max(0, total - alive).ToString());
        }
    }
}
