using System;
using System.Collections.Generic;

using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;

// World Events Hold H4a: the pure combat rules the engine hooks run (D6 - no clock, no world, no settings: every tunable
// arrives as an argument, read by the caller from one WorldEventHoldTuning snapshot). Ported from the contributor's
// WorldEventHoldRules / WorldEventFinaleBoss with the HoldSettings reads lifted into parameters. Each Hold slice adds
// WorldEventHoldRules helpers only in its own partial file; this one is H4's.
namespace ACE.Server.WorldEvents
{
    public static partial class WorldEventHoldRules
    {
        // ---- identity -------------------------------------------------------------------------------------------

        /// <summary>
        /// The run ids the physics layer caches for one creature (pure): a knight's own run as knightRun, a wave monster's
        /// or the finale boss's run as monsterRun; (0, 0) for a creature with no tag (<paramref name="kind"/> null) or a
        /// run id of 0.
        /// </summary>
        public static (uint KnightRun, uint MonsterRun) PhysicsRunIds(HoldUnitKind? kind, uint runId)
        {
            if (kind == null || runId == 0)
                return (0, 0);

            return kind == HoldUnitKind.Knight ? (runId, 0u) : (0u, runId);
        }

        /// <summary>
        /// Whether a physics object may keep a knight as an attack target (pure, cached values only): the target is a
        /// knight of a run (non-zero <paramref name="knightRun"/>) and the attacker is a monster of that same run.
        /// </summary>
        public static bool PhysicsIsKnightTargetFor(uint knightRun, bool attackerIsMonster, uint attackerRun)
            => knightRun != 0 && attackerIsMonster && attackerRun == knightRun;

        /// <summary>
        /// Whether an attacker may damage a knight (pure): a knight is damaged only by a wave monster or the finale boss
        /// of its own, still active run, never by a combat pet, a player, another knight or another run.
        /// </summary>
        public static bool MayDamageKnight(uint knightRun, bool attackerIsPetOrPlayer, HoldUnitKind? attackerKind, uint attackerRun, bool runActive)
            => knightRun != 0 && !attackerIsPetOrPlayer && attackerKind != null && attackerKind != HoldUnitKind.Knight
               && attackerRun == knightRun && runActive;

        // ---- knight hit pacing ----------------------------------------------------------------------------------

        /// <summary>
        /// The seconds of attacking one hit on a knight stands for (pure): the time since that attacker's previous hit,
        /// <paramref name="firstHitSeconds"/> for its first (or an unusable clock), clamped to [0, <paramref name="maxSeconds"/>].
        /// </summary>
        public static double HitInterval(double? lastHitAt, double now, double firstHitSeconds, double maxSeconds)
        {
            var max = double.IsFinite(maxSeconds) && maxSeconds > 0 ? maxSeconds : 0;

            if (lastHitAt == null || !double.IsFinite(lastHitAt.Value) || !double.IsFinite(now))
                return double.IsFinite(firstHitSeconds) && firstHitSeconds > 0 ? firstHitSeconds : 0;

            return Math.Clamp(now - lastHitAt.Value, 0, max);
        }

        /// <summary>
        /// The damage one run monster's hit deals to a knight (pure): maxHealth * interval / undefendedSeconds, so one
        /// attacker hitting continuously empties the knight in undefendedSeconds whatever its weapon. An attacker that
        /// swings <paramref name="attackSpeedMult"/> times as fast (above 1) empties it that much sooner. At least 1 for a
        /// positive interval; 0 for a zero interval or any input it cannot use.
        /// </summary>
        public static float ProtectedHitDamage(uint maxHealth, double intervalSeconds, double undefendedSeconds, double attackSpeedMult = 1.0)
        {
            var mult = double.IsFinite(attackSpeedMult) && attackSpeedMult > 1 ? attackSpeedMult : 1.0;
            var seconds = undefendedSeconds / mult;

            if (maxHealth == 0 || !double.IsFinite(intervalSeconds) || !(intervalSeconds > 0) || !double.IsFinite(seconds) || !(seconds > 0))
                return 0f;

            return (float)Math.Max(1.0, Math.Round(maxHealth * intervalSeconds / seconds));
        }

        /// <summary>
        /// A token bucket (pure): refills at <paramref name="ratePerSecond"/> up to <paramref name="capacity"/>, starting
        /// full (lastAt 0 = never used), and lets through at most what it holds. Shared by the knight damage cap and the
        /// player-heal cap.
        /// </summary>
        public static double TakeFromBucket(double requested, double ratePerSecond, double capacity, ref double tokens, ref double lastAt, double now)
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

        /// <summary>
        /// The knight damage cap (pure): a token bucket refilling at maxHealth / minDeathSeconds per second and holding
        /// <paramref name="burstSeconds"/> of that, so however many attackers pile on a knight loses its whole health over
        /// about minDeathSeconds at the fastest. minDeathSeconds 0 or less (or any unusable input) is no cap.
        /// </summary>
        public static float CapProtectedHit(float requested, uint maxHealth, double minDeathSeconds, double burstSeconds, ref double tokens, ref double lastAt, double now)
        {
            if (!(requested > 0) || maxHealth == 0 || !double.IsFinite(minDeathSeconds) || !(minDeathSeconds > 0) || !double.IsFinite(now))
                return requested;

            var rate = maxHealth / minDeathSeconds;
            var burst = double.IsFinite(burstSeconds) && burstSeconds > 0 ? burstSeconds : 0;

            return (float)TakeFromBucket(requested, rate, rate * burst, ref tokens, ref lastAt, now);
        }

        /// <summary>
        /// How much harder a wave monster hits a knight because of the run's corrupted monsters (pure): a corrupted one
        /// itself <paramref name="selfMult"/>; one within <paramref name="radius"/> of another living corrupted monster
        /// <paramref name="empowerMult"/>; anyone else 1. Multipliers at or below 1 count as 1.
        /// </summary>
        public static double EmpowerFactor(bool attackerIsCorrupted, double? nearestCorruptedDist, double radius, double empowerMult, double selfMult)
        {
            if (attackerIsCorrupted)
                return double.IsFinite(selfMult) && selfMult > 1 ? selfMult : 1.0;

            if (nearestCorruptedDist == null || !(radius > 0) || !(nearestCorruptedDist.Value <= radius))
                return 1.0;

            return double.IsFinite(empowerMult) && empowerMult > 1 ? empowerMult : 1.0;
        }

        /// <summary>A settings multiplier as a hit uses it (pure): <paramref name="mult"/> when finite and above 0, else 1.</summary>
        public static double SafeMult(double mult) => double.IsFinite(mult) && mult > 0 ? mult : 1.0;

        // ---- the ranged ward ------------------------------------------------------------------------------------

        /// <summary>What a hit does to a ward.</summary>
        public enum WardOutcome
        {
            /// <summary>No ward up, or a ward that ignores this kind: full damage.</summary>
            None,

            /// <summary>The ward cut the hit by its reduction.</summary>
            Reduced,

            /// <summary>The hit shattered the ward and landed in full.</summary>
            Shattered
        }

        /// <summary>
        /// A hit of <paramref name="kind"/> on a warded monster (pure). While the ward is up a breaking kind shatters it
        /// (that hit lands in full) and a reduced kind is cut by <paramref name="reduction"/>. While it is down every hit
        /// lands in full.
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

        /// <summary>How long a shattered ward stays down (pure): <paramref name="shatterSeconds"/> clamped to 1..120.</summary>
        public static double WardDownSeconds(double shatterSeconds)
            => double.IsFinite(shatterSeconds) ? Math.Clamp(shatterSeconds, 1, 120) : 10;

        // ---- target selection -----------------------------------------------------------------------------------

        /// <summary>The attacker count an avoided knight is given, so it is only ever picked when nothing else is in reach.</summary>
        public const int AvoidedTargetPenalty = 10000;

        /// <summary>
        /// Attacker counts with <paramref name="avoidGuid"/>'s knight made a last resort (pure): its count is raised by
        /// <see cref="AvoidedTargetPenalty"/>. Unchanged when the guid is 0 or not among the targets. Parallel lists.
        /// </summary>
        public static List<int> AvoidTarget(IReadOnlyList<int> attackers, IReadOnlyList<uint> guids, uint avoidGuid)
        {
            var result = attackers == null ? new List<int>() : new List<int>(attackers);

            if (avoidGuid == 0 || guids == null)
                return result;

            for (var i = 0; i < result.Count && i < guids.Count; i++)
                if (guids[i] == avoidGuid)
                    result[i] += AvoidedTargetPenalty;

            return result;
        }

        /// <summary>
        /// A seeker's pick (pure): among the targets within <paramref name="maxDistSq"/>, the one with the fewest attackers
        /// already on it, the nearest among ties. -1 when none is in range. Parallel lists.
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

        /// <summary>The index of the highest health fraction (current / max), or -1 for an empty list. Ties keep the first; a max of 0 is skipped.</summary>
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
        /// The index of the smallest squared distance no more than <paramref name="maxDistSq"/>, or -1 when none
        /// qualifies (empty, all too far, or not finite). Ties keep the first.
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
        /// Whether an awake seeker has strayed (pure): it is a seeker, its current target is not a knight, and at least
        /// one knight still stands for it to go back to.
        /// </summary>
        public static bool SeekerStrayed(bool isSeeker, bool targetIsKnight, bool knightsStanding)
            => isSeeker && !targetIsKnight && knightsStanding;

        // ---- ranged keep-distance (read by H4b's tick) ----------------------------------------------------------

        /// <summary>
        /// Whether a Hold ranged-wave monster keeps its distance (pure): only with a hold distance set and a ranged attack
        /// to use (a missile weapon or spells). A melee-only monster closes in as usual.
        /// </summary>
        public static bool HoldsRange(double holdDistance, bool hasMissileWeapon, bool hasSpells)
            => holdDistance > 0 && (hasMissileWeapon || hasSpells);

        /// <summary>
        /// Whether a ranged monster is still too far and should walk in (pure): farther than the hold distance plus
        /// <paramref name="slack"/> (a negative or unusable slack counts as 0).
        /// </summary>
        public static bool RangedHoldShouldApproach(double targetDist, double holdDistance, double slack)
            => targetDist > holdDistance + (double.IsFinite(slack) && slack > 0 ? slack : 0);
    }
}
