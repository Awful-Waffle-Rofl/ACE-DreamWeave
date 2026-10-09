using System;

using ACE.Server.Entity;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The group scaling factors of one Group Thread (Docs/Threads/GROUP-THREADS-DESIGN.md sections 5, 6.1 and
    /// 6.2). The ONE owner of the formulas; no other file re-derives them.
    ///
    ///   N  = locked roster size, owner included
    ///   E  = 1 + g(N - 1)                                  effort
    ///   C  = 1 + s(E - 1)                                  target count multiplier
    ///   H  = E / C_actual                                  trash and elite health multiplier
    ///   D  = min(d(N - 1), cap), cap 0 = uncapped          damage rating bonus
    ///   B  = min(1 + b(N - 1), bonusCap), never below 1.0  reward bonus
    ///   T  = N * Fellowship.GetMemberSharePercent(N)       fellowship share total
    ///
    /// Immutable. A run snapshots one instance at lock time (ThreadDungeonManager.TryStart) and every consumer
    /// reads the factors from that snapshot; nothing re-reads a group tunable after the lock.
    ///
    /// N = 1 is <see cref="Solo"/>: every factor is exactly 1.0 or 0. Consumers guard group-only arithmetic on
    /// <see cref="IsGroup"/>, never on a factor happening to equal 1.0.
    ///
    /// Every static helper sanitises its own inputs, so a garbage tunable degrades to the compiled default
    /// instead of reaching a spawn count or an XP grant as NaN or a negative number.
    /// </summary>
    public sealed class GroupScaling
    {
        public const double DefaultEffortPerMember = 1.0;
        public const double DefaultCountShare = 0.5;
        public const int DefaultGroupMaxMonsters = 180;
        public const int DefaultDamageRatingPerMember = 15;
        public const int DefaultDamageRatingCap = 300;
        public const double DefaultRewardBonusPerMember = 0.05;
        public const double DefaultRewardBonusCap = 1.5;
        public const int DefaultLootHoldMinutes = 30;

        /// <summary>Matches Fellowship.AbsoluteMaxFellows: no fellowship can field a larger roster.</summary>
        public const int MaxRosterSize = 100;

        /// <summary>
        /// The solo snapshot. RosterSize 1, every multiplier exactly 1.0, the damage bonus 0.
        /// GroupMaxMonsters is 0: a solo run has no group count cap, and no solo path reads it. LootHoldMinutes is
        /// 0: a solo run is never held for loot.
        /// </summary>
        public static readonly GroupScaling Solo = new GroupScaling(1, 1.0, 1.0, 0, 0, 1.0, 1.0, 0);

        private GroupScaling(int rosterSize, double effort, double countTarget, int groupMaxMonsters, int damageRatingBonus, double rewardBonus, double shareTotal,
            int lootHoldMinutes)
        {
            RosterSize = rosterSize;
            Effort = effort;
            CountTarget = countTarget;
            GroupMaxMonsters = groupMaxMonsters;
            DamageRatingBonus = damageRatingBonus;
            RewardBonus = rewardBonus;
            ShareTotal = shareTotal;
            LootHoldMinutes = lootHoldMinutes;
        }

        /// <summary>N, owner included. 1 for <see cref="Solo"/>.</summary>
        public int RosterSize { get; }

        /// <summary>E = 1 + g(N - 1).</summary>
        public double Effort { get; }

        /// <summary>C = 1 + s(E - 1), the TARGET count multiplier before the group cap.</summary>
        public double CountTarget { get; }

        /// <summary>The group count cap (dynamic_dungeons_group_max_monsters_per_run). 0 for <see cref="Solo"/>.</summary>
        public int GroupMaxMonsters { get; }

        /// <summary>Damage rating added to trash AND boss after the boss floor. 0 for <see cref="Solo"/>.</summary>
        public int DamageRatingBonus { get; }

        /// <summary>B, in [1.0, bonusCap].</summary>
        public double RewardBonus { get; }

        /// <summary>T(N) = N * Fellowship.GetMemberSharePercent(N, plateau).</summary>
        public double ShareTotal { get; }

        /// <summary>
        /// The group loot hold (dynamic_dungeons_group_loot_hold_minutes, final review F1): a Cleared group run that
        /// still owes loot is held from the cleared-and-empty reap, and its copy's empty window is this many minutes
        /// in place of the landblock default (ThreadDungeonRun.ResolveUnloadInterval), so it stays loaded only while
        /// some roster member has been inside within that long. 0 = no hold. 0 for <see cref="Solo"/>. A positive
        /// value below the empty-grace floor (ThreadDungeonManager.EmptyGraceMinutesMin, 5) is raised to that floor
        /// at the single clamp site (ThreadDungeonRun_EmptyGrace.ResolveUnloadInterval), so 1-4 here behave as 5.
        /// </summary>
        public int LootHoldMinutes { get; }

        /// <summary><see cref="LootHoldMinutes"/> as a span.</summary>
        public TimeSpan LootHold => TimeSpan.FromMinutes(LootHoldMinutes);

        public bool IsGroup => RosterSize > 1;

        /// <summary>
        /// Builds the snapshot for a roster of <paramref name="rosterSize"/>. The size clamps to
        /// [1, <see cref="MaxRosterSize"/>], and 1 returns <see cref="Solo"/> whatever the other arguments say.
        /// <paramref name="lootHoldMinutes"/> defaults to <see cref="DefaultLootHoldMinutes"/>; a negative value reads
        /// as that default and a value past int.MaxValue saturates.
        /// </summary>
        public static GroupScaling Compute(int rosterSize, double effortPerMember, double countShare, long groupMaxMonsters,
            long damageRatingPerMember, long damageRatingCap, double rewardBonusPerMember, double rewardBonusCap, double sharePlateau,
            long lootHoldMinutes = DefaultLootHoldMinutes)
        {
            var n = ClampRoster(rosterSize);

            if (n == 1)
                return Solo;

            var effort = EffortFor(n, effortPerMember);
            var countTarget = CountTargetFor(effort, countShare);
            var groupMax = SanitiseGroupMax(groupMaxMonsters);
            var damage = DamageRatingBonusFor(n, damageRatingPerMember, damageRatingCap);
            var bonus = RewardBonusFor(n, rewardBonusPerMember, rewardBonusCap);
            var share = ShareTotalFor(n, sharePlateau);

            var hold = SanitiseLootHold(lootHoldMinutes);

            return new GroupScaling(n, effort, countTarget, groupMax, damage, bonus, share, hold);
        }

        /// <summary>E = 1 + g(N - 1). g NaN, infinite or negative reads as <see cref="DefaultEffortPerMember"/>.</summary>
        public static double EffortFor(int rosterSize, double effortPerMember)
        {
            var n = ClampRoster(rosterSize);
            if (n == 1)
                return 1.0;

            var g = SanitiseRate(effortPerMember, DefaultEffortPerMember);
            return 1.0 + g * (n - 1);
        }

        /// <summary>
        /// C = 1 + s(E - 1). s NaN, infinite or negative reads as <see cref="DefaultCountShare"/>; an effort that is
        /// not a finite number of at least 1.0 reads as 1.0, so C is never below 1.0.
        /// </summary>
        public static double CountTargetFor(double effort, double countShare)
        {
            if (double.IsNaN(effort) || double.IsInfinity(effort) || effort < 1.0)
                effort = 1.0;

            var s = SanitiseRate(countShare, DefaultCountShare);
            return 1.0 + s * (effort - 1.0);
        }

        /// <summary>
        /// D = min(d(N - 1), cap), cap 0 meaning uncapped. A negative d or cap reads as its default. Saturates at
        /// int.MaxValue rather than overflowing. 0 for a roster of one.
        /// </summary>
        public static int DamageRatingBonusFor(int rosterSize, long perMember, long cap)
        {
            var n = ClampRoster(rosterSize);
            if (n == 1)
                return 0;

            if (perMember < 0) perMember = DefaultDamageRatingPerMember;
            if (cap < 0) cap = DefaultDamageRatingCap;

            var others = n - 1;
            var raw = perMember > long.MaxValue / others ? long.MaxValue : perMember * others;

            if (cap > 0 && raw > cap)
                raw = cap;

            return raw > int.MaxValue ? int.MaxValue : (int)raw;
        }

        /// <summary>
        /// B = min(1 + b(N - 1), cap), never below 1.0. b NaN, infinite or negative reads as
        /// <see cref="DefaultRewardBonusPerMember"/>; cap NaN reads as <see cref="DefaultRewardBonusCap"/>, and a cap
        /// below 1.0 reads as 1.0. 1.0 for a roster of one.
        /// </summary>
        public static double RewardBonusFor(int rosterSize, double perMember, double cap)
        {
            var n = ClampRoster(rosterSize);
            if (n == 1)
                return 1.0;

            var b = SanitiseRate(perMember, DefaultRewardBonusPerMember);

            if (double.IsNaN(cap)) cap = DefaultRewardBonusCap;
            if (cap < 1.0) cap = 1.0;

            return Math.Max(1.0, Math.Min(1.0 + b * (n - 1), cap));
        }

        /// <summary>
        /// T(N) = N * Fellowship.GetMemberSharePercent(N, plateau): the total XP a fellowship of N splits among its
        /// members, so dividing a kill's XP by T and letting Fellowship.SplitXp multiply it back leaves each
        /// member with the per-kill value. A NaN or infinite plateau is passed as 0, which
        /// GetMemberSharePercent reads as its own retail plateau. 1.0 for a roster of one.
        /// </summary>
        public static double ShareTotalFor(int rosterSize, double sharePlateau)
        {
            var n = ClampRoster(rosterSize);
            if (n == 1)
                return 1.0;

            if (double.IsNaN(sharePlateau) || double.IsInfinity(sharePlateau))
                sharePlateau = 0.0;

            return n * Fellowship.GetMemberSharePercent(n, sharePlateau);
        }

        /// <summary>
        /// The group slot count: min(max(round(points * countMult * C), round(minMonsters * C)), groupMax), then
        /// never below <paramref name="soloSlots"/> (ruling R27: an admin cap below the solo count must not lower
        /// the count and so inflate H). 0 when there are no curated points, exactly like the solo count block in
        /// DungeonPopulationBuilder, whose Math.Round (banker's rounding) this matches.
        ///
        /// countMult NaN or negative reads as 1.0 (the builder's own rule); C that is not a finite number of at
        /// least 1.0 reads as 1.0; groupMax &lt;= 0 reads as <see cref="DefaultGroupMaxMonsters"/>.
        /// </summary>
        public static int GroupSlotsFor(int pointCount, double countMult, int minMonsters, double countTarget, int groupMaxMonsters, int soloSlots)
        {
            if (pointCount <= 0)
                return 0;

            if (double.IsNaN(countMult) || countMult < 0) countMult = 1.0;
            if (double.IsNaN(countTarget) || double.IsInfinity(countTarget) || countTarget < 1.0) countTarget = 1.0;
            if (groupMaxMonsters <= 0) groupMaxMonsters = DefaultGroupMaxMonsters;

            var desired = RoundToInt(pointCount * countMult * countTarget);
            var floor = RoundToInt(minMonsters * countTarget);

            var slots = Math.Min(Math.Max(desired, floor), groupMaxMonsters);
            slots = Math.Max(slots, soloSlots);
            return Math.Max(0, slots);
        }

        /// <summary>C_actual = groupSlots / soloSlots; 1.0 when soloSlots &lt;= 0.</summary>
        public static double CountActualFor(int soloSlots, int groupSlots)
        {
            if (soloSlots <= 0)
                return 1.0;

            return (double)groupSlots / soloSlots;
        }

        /// <summary>H = E / C_actual; E itself when C_actual is NaN or &lt;= 0.</summary>
        public static double HealthMultFor(double effort, double countActual)
        {
            if (double.IsNaN(countActual) || countActual <= 0)
                return effort;

            return effort / countActual;
        }

        /// <summary>Per-kill XP and luminance factor for trash and elites: H * B / T. Exactly 1.0 when !IsGroup.</summary>
        public double TrashRewardFactor(double healthMult)
        {
            if (!IsGroup)
                return 1.0;

            return SanitiseHealth(healthMult) * RewardBonus / SafeShareTotal;
        }

        /// <summary>Per-kill XP and luminance factor for the boss: E * B / T. Exactly 1.0 when !IsGroup.</summary>
        public double BossRewardFactor()
        {
            if (!IsGroup)
                return 1.0;

            return Effort * RewardBonus / SafeShareTotal;
        }

        /// <summary>Loot roll factor for trash and elites: H * B. Exactly 1.0 when !IsGroup.</summary>
        public double TrashLootFactor(double healthMult)
        {
            if (!IsGroup)
                return 1.0;

            return SanitiseHealth(healthMult) * RewardBonus;
        }

        /// <summary>Loot roll factor for the boss: E * B. Exactly 1.0 when !IsGroup.</summary>
        public double BossLootFactor()
        {
            if (!IsGroup)
                return 1.0;

            return Effort * RewardBonus;
        }

        public override string ToString()
            => $"group N={RosterSize} E={Effort:0.###} C={CountTarget:0.###} max={GroupMaxMonsters} dr={DamageRatingBonus} B={RewardBonus:0.###} T={ShareTotal:0.###}";

        private double SafeShareTotal => double.IsNaN(ShareTotal) || ShareTotal <= 0 ? 1.0 : ShareTotal;

        private static int ClampRoster(int rosterSize) => Math.Clamp(rosterSize, 1, MaxRosterSize);

        private static double SanitiseRate(double value, double fallback)
            => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? fallback : value;

        private static double SanitiseHealth(double healthMult)
            => double.IsNaN(healthMult) || double.IsInfinity(healthMult) || healthMult <= 0 ? 1.0 : healthMult;

        private static int SanitiseGroupMax(long groupMaxMonsters)
        {
            if (groupMaxMonsters <= 0)
                return DefaultGroupMaxMonsters;

            return groupMaxMonsters > int.MaxValue ? int.MaxValue : (int)groupMaxMonsters;
        }

        /// <summary>Clamps dynamic_dungeons_group_loot_hold_minutes for <see cref="Compute"/>: negative reads as
        /// <see cref="DefaultLootHoldMinutes"/>, and a value past int.MaxValue saturates.</summary>
        private static int SanitiseLootHold(long lootHoldMinutes)
        {
            if (lootHoldMinutes < 0)
                return DefaultLootHoldMinutes;

            return lootHoldMinutes > int.MaxValue ? int.MaxValue : (int)lootHoldMinutes;
        }

        private static int RoundToInt(double value)
        {
            if (double.IsNaN(value))
                return 0;

            var rounded = Math.Round(value);

            if (rounded >= int.MaxValue) return int.MaxValue;
            if (rounded <= int.MinValue) return int.MinValue;
            return (int)rounded;
        }
    }
}
