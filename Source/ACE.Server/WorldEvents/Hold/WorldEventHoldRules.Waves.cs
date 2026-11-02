using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;

// World Events Hold H5: the pure rules of the spawner and placement side - wave types and the wave mix, role assignment,
// the corrupted assault, the finale boss's name and size, the ring/portal/decor layout. No clock, no world, no settings
// (D6). Ported from DreamWeave #17's WorldEventHoldRules.cs. Each Hold slice adds WorldEventHoldRules helpers only in its
// own partial file; nothing here redefines a helper of another slice.
namespace ACE.Server.WorldEvents
{
    public static partial class WorldEventHoldRules
    {
        // ---- seekers ----------------------------------------------------------------------------------------------

        /// <summary>
        /// How many of a <paramref name="waveSize"/> wave go straight for the knights at <paramref name="share"/> (0 to 1).
        /// <paramref name="carry"/> is the run's running fractional remainder, so a 30% share stays 30% across many small
        /// waves instead of rounding to 0 each time. A share of 0 or less gives 0 and resets the carry; 1 or more gives all.
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

        /// <summary><paramref name="count"/> distinct indices drawn uniformly from 0..<paramref name="size"/>-1 (a partial Fisher-Yates).</summary>
        public static HashSet<int> PickIndices(int size, int count, Random rng)
        {
            var picked = new HashSet<int>();

            if (size <= 0 || count <= 0)
                return picked;

            count = Math.Min(count, size);
            rng ??= new Random();

            var pool = Enumerable.Range(0, size).ToArray();

            for (var i = 0; i < count; i++)
            {
                var j = rng.Next(i, size);

                (pool[i], pool[j]) = (pool[j], pool[i]);

                picked.Add(pool[i]);
            }

            return picked;
        }

        // ---- wave types -------------------------------------------------------------------------------------------

        /// <summary>
        /// One wave's type, in order: the final counted wave is the finale when the goal defines one; an admin-forced type
        /// applies to every other wave; waves before <paramref name="fromWave"/> are ordinary; otherwise a roll against
        /// <paramref name="chance"/>, then a weighted pick among the special types, never the finale and never the same type
        /// as <paramref name="previous"/>.
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
                && t.TypeKind != HoldWaveType.Mixed && t.TypeKind != previous && t.Weight > 0 && double.IsFinite(t.Weight)).ToList();

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
        /// with a guaranteed minimum: when <see cref="MustBeSpecial"/> says this middle wave has to be special to reach
        /// <paramref name="minSpecial"/>, the chance roll is skipped and a type is always drawn.
        /// </summary>
        public static HoldWaveType PickWaveType(int waveNumber, int targetWaves, HoldWaveType previous,
            double chance, int fromWave, IReadOnlyList<SpecialWaveDef> types, Random rng, HoldWaveType? forced,
            int specialsSoFar, int minSpecial)
        {
            var hasFinale = types != null && types.Any(t => t != null && t.TypeKind == HoldWaveType.Finale);

            if (!MustBeSpecial(waveNumber, targetWaves, fromWave, hasFinale, specialsSoFar, minSpecial))
                return PickWaveType(waveNumber, targetWaves, previous, chance, fromWave, types, rng, forced);

            var type = PickWaveType(waveNumber, targetWaves, previous, 1.0, fromWave, types, rng, forced);

            // Every eligible type was the previous one (a goal with only one special type): repeat it rather than miss the guarantee.
            if (type == HoldWaveType.Normal && types != null)
            {
                var only = types.FirstOrDefault(t => t != null && t.TypeKind != HoldWaveType.Finale && t.TypeKind != HoldWaveType.Normal
                    && t.TypeKind != HoldWaveType.Mixed && t.Weight > 0);

                if (only != null)
                    type = only.TypeKind;
            }

            return type;
        }

        /// <summary>
        /// Whether wave <paramref name="waveNumber"/> of <paramref name="targetWaves"/> must be special so the run gets at least
        /// <paramref name="minSpecial"/> special waves. Only the middle waves count (from <paramref name="fromWave"/> to the last
        /// before the finale); the guaranteed ones are spread evenly, and a wave is also forced when too few waves are left.
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

            if (last - waveNumber + 1 <= need)
                return true;

            return GuaranteedSpecialSlots(first, last, want).Contains(waveNumber);
        }

        /// <summary>The waves the guaranteed specials fall on: <paramref name="count"/> slots spread evenly over waves <paramref name="first"/> to <paramref name="last"/>.</summary>
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

        // ---- the wave mix -----------------------------------------------------------------------------------------

        /// <summary>Which roles give way first when a small wave cannot hold every role: least important first.</summary>
        public static readonly HoldRole[] DropOrder = { HoldRole.Hunter, HoldRole.Runner, HoldRole.Breach, HoldRole.Seeker, HoldRole.Ranged, HoldRole.Corrupted };

        /// <summary>The number of <see cref="HoldRole"/> values (the length of every per-role array).</summary>
        public static int RoleCount => Enum.GetValues(typeof(HoldRole)).Length;

        /// <summary>
        /// How many of a <paramref name="waveSize"/> wave take each role, from shares in role order. Largest-remainder rounding,
        /// so the counts always sum to the wave; when the wave has room for every role with a share above 0, each gets at least
        /// one (taken from the biggest role). Shares are normalised; all zero puts the whole wave on Seeker.
        /// </summary>
        public static int[] WaveMixCounts(int waveSize, IReadOnlyList<double> shares)
        {
            var roles = RoleCount;
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
        /// <see cref="WaveMixCounts(int, IReadOnlyList{double})"/> with per-role limits in role order: a role above its maximum
        /// (-1 = none) hands the extra to Seeker; a role below its minimum takes monsters from the biggest other roles, never
        /// below their own minimum or below 1 when their share is above 0. No minimum takes more than half the wave.
        /// </summary>
        public static int[] WaveMixCounts(int waveSize, IReadOnlyList<double> shares, IReadOnlyList<int> minimums, IReadOnlyList<int> maximums)
        {
            var counts = WaveMixCounts(waveSize, shares);
            var roles = counts.Length;

            int Min(int r) => minimums != null && r < minimums.Count ? Math.Max(0, minimums[r]) : 0;
            int Max(int r) => maximums != null && r < maximums.Count ? maximums[r] : -1;
            bool Wanted(int r) => shares != null && r < shares.Count && double.IsFinite(shares[r]) && shares[r] > 0;

            for (var r = 0; r < roles; r++)
            {
                var max = Max(r);

                if (max >= 0 && counts[r] > max && r != (int)HoldRole.Seeker)
                {
                    counts[(int)HoldRole.Seeker] += counts[r] - max;
                    counts[r] = max;
                }
            }

            int Floor(int r)
            {
                var min = Max(r) >= 0 ? Math.Min(Min(r), Max(r)) : Min(r);
                return Math.Min(min, waveSize / 2);
            }

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

        /// <summary>
        /// One role per monster of a mixed wave: <see cref="WaveMixCounts(int, IReadOnlyList{double}, IReadOnlyList{int}, IReadOnlyList{int})"/>,
        /// laid over the wave in a random order. Ranged never lands on a <paramref name="keepIndices"/> entry (an elite or an
        /// overflow champion keeps its own monster); such a slot swaps with a plain one, or seeks.
        /// </summary>
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

        /// <summary>Every <paramref name="from"/> role becomes <paramref name="to"/>. Returns how many moved.</summary>
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

        /// <summary>
        /// A mixed wave's trash with its Ranged slots redrawn from the ranged pool. With a caster share (0 to 1) and a pool of
        /// both kinds, each slot draws a caster with that chance, else an archer; otherwise a plain draw. Other slots, and every
        /// slot when the pool is empty, keep their monster.
        /// </summary>
        public static List<uint> ReplaceRangedSlots(IReadOnlyList<uint> trash, IReadOnlyList<HoldRole> roles, IReadOnlyList<uint> rangedPool, Random rng,
            Func<uint, bool> isCaster = null, double? casterShare = null)
        {
            var result = trash?.ToList() ?? new List<uint>();

            if (rangedPool == null || rangedPool.Count == 0 || roles == null)
                return result;

            rng ??= new Random();

            var casters = isCaster != null ? rangedPool.Where(isCaster).ToList() : new List<uint>();
            var archers = isCaster != null ? rangedPool.Where(w => !isCaster(w)).ToList() : new List<uint>();
            var split = casterShare != null && casters.Count > 0 && archers.Count > 0;
            var share = Math.Clamp(casterShare ?? 0, 0, 1);

            for (var i = 0; i < result.Count && i < roles.Count; i++)
            {
                if (roles[i] != HoldRole.Ranged)
                    continue;

                var pool = split ? (rng.NextDouble() < share ? casters : archers) : rangedPool;
                result[i] = pool[rng.Next(pool.Count)];
            }

            return result;
        }

        /// <summary>A one-type ranged wave: each wcid replaced, in turn, by one from <paramref name="rangedPool"/> (cycling from a random start). Unchanged for an empty pool.</summary>
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
        /// A hunter wave's hunters: <paramref name="hunters"/> indices of a <paramref name="waveSize"/> batch, non-seekers first
        /// (in order) and then seekers, never <paramref name="excludedIndex"/> (the finale boss).
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

        // ---- the corrupted assault ----------------------------------------------------------------------------------

        /// <summary>Whether wave <paramref name="waveNumber"/> is a corrupted assault: assaults on, never the finale, from <paramref name="fromWave"/> every <paramref name="everyWaves"/>.</summary>
        public static bool IsAssaultWave(int waveNumber, int everyWaves, int fromWave, bool finale)
        {
            if (everyWaves <= 0 || finale || waveNumber < Math.Max(1, fromWave))
                return false;

            return (waveNumber - Math.Max(1, fromWave)) % everyWaves == 0;
        }

        /// <summary>The mix arrays for a wave's own monsters: in assault mode no corrupted among them (the assault's come on top). Copies; unchanged when assaults are off.</summary>
        public static (double[] Shares, int[] Minimums, int[] Maximums) AssaultMix(IReadOnlyList<double> shares, IReadOnlyList<int> minimums,
            IReadOnlyList<int> maximums, bool assaultMode)
        {
            var s = shares?.ToArray() ?? new double[0];
            var mins = minimums?.ToArray() ?? new int[0];
            var maxs = maximums?.ToArray() ?? new int[0];

            if (!assaultMode)
                return (s, mins, maxs);

            var c = (int)HoldRole.Corrupted;

            if (c < s.Length) s[c] = 0;
            if (c < mins.Length) mins[c] = 0;
            if (c < maxs.Length) maxs[c] = 0;

            return (s, mins, maxs);
        }

        /// <summary>
        /// The roles of an assault wave of <paramref name="size"/>: <paramref name="assault"/> slots, never a kept one, are
        /// Corrupted; the rest are an ordinary mixed wave of their own size with the corrupted role removed.
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

        /// <summary>Which standing knight (by index) each of <paramref name="count"/> assault monsters marches on: a shuffled order, a different one each while enough stand.</summary>
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

        // ---- names, levels and the finale boss -----------------------------------------------------------------------

        /// <summary>A title in front of a name ("Corrupted Shallows Gorger"); a name that already starts with it is left alone.</summary>
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

        /// <summary>The finale boss's display name: <paramref name="prefix"/> in front of its own name, any "Champion " or "Elite " a promotion added taken off first.</summary>
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

        /// <summary>
        /// The finale boss's size multiplier: <paramref name="configured"/> within 1 to 2.5, or, when it is 0 or less,
        /// <paramref name="worldEventBossScale"/> (the multiplier every other World Events boss gets).
        /// </summary>
        public static float FinaleBossScale(double configured, float worldEventBossScale)
        {
            if (!double.IsFinite(configured))
                return 1.0f;

            if (configured <= 0)
                return worldEventBossScale > 0 && float.IsFinite(worldEventBossScale) ? worldEventBossScale : 1.0f;

            return (float)Math.Clamp(configured, 1.0, 2.5);
        }

        /// <summary>The final wave without copies of its boss: every <paramref name="bossWcid"/> swapped for another monster of the wave.</summary>
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
        /// A boss's adds: <paramref name="count"/> ordinary (role 0) members drawn at random, from those in
        /// <paramref name="band"/> when any are, else all of them; never the boss's own monster.
        /// </summary>
        public static List<uint> PickAdds(IEnumerable<FamilyMember> members, LevelBand? band, uint bossWcid, int count, Random rng)
        {
            var result = new List<uint>();

            if (members == null || count <= 0)
                return result;

            var ordinary = members.Where(m => m != null && m.Wcid != 0 && m.Role == 0 && m.Wcid != bossWcid).ToList();

            if (band != null && ordinary.Any(m => band.Value.Contains(m.Level)))
                ordinary = ordinary.Where(m => band.Value.Contains(m.Level)).ToList();

            var pool = ordinary.Select(m => m.Wcid).Distinct().ToList();

            if (pool.Count == 0)
                return result;

            rng ??= new Random();

            for (var i = 0; i < count; i++)
                result.Add(pool[rng.Next(pool.Count)]);

            return result;
        }

        /// <summary>A level band moved by <paramref name="offset"/> levels, kept within 1 to 275.</summary>
        public static LevelBand ShiftBand(LevelBand band, int offset)
        {
            var low = Math.Clamp(band.Low + offset, 1, 275);
            var high = Math.Clamp(band.High + offset, low, 275);

            return new LevelBand(low, high);
        }

        /// <summary>
        /// Role level offsets: every slot with an offset, not in <paramref name="keep"/>, redrawn from the pool for its offset
        /// (a slot whose pool is empty keeps its monster). Returns the new trash and the slots that moved.
        /// </summary>
        public static List<uint> RedrawForLevelOffsets(IReadOnlyList<uint> trash, ISet<int> keep, Func<int, int> offsetForSlot,
            Func<int, IReadOnlyList<uint>> poolForSlot, Random rng, out Dictionary<int, int> moved)
        {
            moved = new Dictionary<int, int>();
            var result = trash?.ToList() ?? new List<uint>();
            rng ??= new Random();

            for (var i = 0; i < result.Count; i++)
            {
                if (keep != null && keep.Contains(i))
                    continue;

                var offset = offsetForSlot?.Invoke(i) ?? 0;

                if (offset == 0)
                    continue;

                var pool = poolForSlot?.Invoke(i);

                if (pool == null || pool.Count == 0)
                    continue;

                result[i] = pool[rng.Next(pool.Count)];
                moved[i] = offset;
            }

            return result;
        }

        /// <summary>A slot level moved by a role level offset; "no slot level" (<paramref name="none"/>) stays as it is.</summary>
        public static int MovedSlotLevel(int slotLevel, int offset, int none = 0) =>
            offset == 0 || slotLevel == none ? slotLevel : Math.Clamp(slotLevel + offset, 1, 275);

        /// <summary>The tuning role of a wave monster from its wave role: breach, seekers and hunters are melee; a ranged slot is a caster when <paramref name="isCaster"/>.</summary>
        public static HoldDamageRole DamageRoleFor(HoldRole role, bool isCaster)
        {
            switch (role)
            {
                case HoldRole.Runner: return HoldDamageRole.Runner;
                case HoldRole.Corrupted: return HoldDamageRole.Corrupted;
                case HoldRole.Ranged: return isCaster ? HoldDamageRole.Caster : HoldDamageRole.Ranged;
                default: return HoldDamageRole.Melee;
            }
        }

        // ---- layout ----------------------------------------------------------------------------------------------

        /// <summary>One knight's (or portal's) offset from the anchor, in metres east/north, and its facing.</summary>
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
        /// Where <paramref name="count"/> knights stand: one at the anchor itself; two or more evenly on a ring of
        /// <paramref name="radius"/> metres, the first due north, each facing outward. A radius of 0 or less puts them all at the anchor.
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
                var degrees = 360.0 * i / count;
                var radians = degrees * Math.PI / 180.0;

                offsets.Add(new RingOffset((float)(radius * Math.Sin(radians)), (float)(radius * Math.Cos(radians)), OutwardYaw(degrees)));
            }

            return offsets;
        }

        /// <summary>
        /// The npc yaw that faces AWAY from the centre for compass bearing <paramref name="bearingDegrees"/>: NpcRotation's yaw 0
        /// faces north and positive yaw turns counter-clockwise, so the outward heading is the negated bearing in [0, 360).
        /// </summary>
        public static float OutwardYaw(double bearingDegrees)
        {
            var yaw = -bearingDegrees % 360.0;

            if (yaw < 0)
                yaw += 360.0;

            return (float)yaw;
        }

        /// <summary>The portals: <paramref name="count"/> points evenly on a ring of <paramref name="radius"/>, the first at <paramref name="rotationDegrees"/>, each facing outward.</summary>
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

        /// <summary>The compass bearings, in radians (0 = north, clockwise), of <see cref="PortalOffsets"/>.</summary>
        public static List<double> PortalBearings(int count, double rotationDegrees)
        {
            var bearings = new List<double>();
            var rotation = double.IsFinite(rotationDegrees) ? rotationDegrees : 0;

            for (var i = 0; i < count; i++)
                bearings.Add((((rotation + 360.0 * i / count) % 360.0 + 360.0) % 360.0) * Math.PI / 180.0);

            return bearings;
        }

        /// <summary>One portal's parts at <paramref name="scale"/>: dx, dy, dz and scale multiplied, always snapped to the ground under the portal.</summary>
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
                Snap = true
            }).ToList();
        }

        /// <summary>
        /// A scene's per-knight props: each entry of <paramref name="local"/> is in a knight's own frame (Dx to its RIGHT, Dy
        /// FORWARD, Yaw relative to its facing) and is placed once per knight, giving anchor-relative copies.
        /// </summary>
        public static List<DecorDef> PlaceTargetDecor(IReadOnlyList<RingOffset> targets, IReadOnlyList<DecorDef> local)
        {
            var placed = new List<DecorDef>();

            if (targets == null || local == null)
                return placed;

            foreach (var target in targets)
            {
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

        /// <summary>One knight's shrine as a decor entry in the knight's own frame: on the ground, flagged as a shrine.</summary>
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

        /// <summary>Whether a wave spawn must be refused because the terrain snap put it indoors while the anchor is outdoors (an indoor anchor is never refused for that).</summary>
        public static bool RefusesIndoorSpawn(bool isHoldRun, bool isWave, bool candidateIndoors, bool anchorIndoors) =>
            isHoldRun && isWave && candidateIndoors && !anchorIndoors;

        private static readonly string[] CompassNames = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

        /// <summary>Which side of the anchor an offset is on (<paramref name="dx"/> east, <paramref name="dy"/> north), as one of eight compass words; "center" on the anchor.</summary>
        public static string CompassSide(double dx, double dy)
        {
            if (!double.IsFinite(dx) || !double.IsFinite(dy) || (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5))
                return "center";

            var degrees = Math.Atan2(dx, dy) * 180.0 / Math.PI;

            if (degrees < 0)
                degrees += 360.0;

            return CompassNames[(int)Math.Round(degrees / 45.0) % 8];
        }

        /// <summary>The per-wave role summary for the log: how many seekers, hunters and others, with the knights the forced seekers were sent at.</summary>
        public static string WaveRoleSummary(int waveNumber, IReadOnlyList<WorldEventHoldTag> tags)
        {
            var list = (tags ?? Array.Empty<WorldEventHoldTag>()).Where(t => t != null).ToList();

            var seekers = list.Count(t => t.Seeker);
            var hunters = list.Count(t => t.Hunter);
            var forced = list.Where(t => t.Seeker && t.ForcedTargetGuid != 0).GroupBy(t => t.ForcedTargetGuid)
                .Select(g => $"0x{g.Key:X8}x{g.Count()}").ToList();

            return $"hold wave {waveNumber} roles: seekers={seekers} hunters={hunters} others={list.Count - seekers - hunters}" +
                   (forced.Count > 0 ? $" forced=[{string.Join(",", forced)}]" : "");
        }
    }
}
