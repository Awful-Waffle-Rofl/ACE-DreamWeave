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

        /// <summary>Parses a special wave type name (case-insensitive). False for anything unknown or "normal".</summary>
        public static bool TryParseWaveType(string name, out HoldWaveType type)
        {
            if (!string.IsNullOrWhiteSpace(name) && name.Trim().All(char.IsLetter) && Enum.TryParse(name.Trim(), true, out type) && type != HoldWaveType.Normal
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
        /// holds. Shared by the caravan damage cap and the player-heal cap.
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
        /// "Wave 3 of 7", or "Wave 8, holding the line" once the counted waves are spent and the minimum time is
        /// keeping the waves coming. "Wave 0 of 7" before the first.
        /// </summary>
        public static string WaveProgress(int started, int target)
        {
            if (target > 0 && started > target)
                return $"Wave {started}, holding the line";

            return $"Wave {Math.Max(0, started)} of {Math.Max(0, target)}";
        }

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
        /// other field is copied unchanged. Lets one goals.json list put a wagon behind each merchant
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
                        Snap = entry.Snap
                    });
                }
            }

            return placed;
        }

        /// <summary>
        /// Fills a Hold outcome line's count tokens (D6 - pure): {alive}, {total} and {lost} as they stood when
        /// the run finished, so the success and fail broadcasts can say "2 of 3 caravans survived".
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
