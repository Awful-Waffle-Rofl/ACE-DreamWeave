using System;
using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for HEMOMANCY (Blood Mage T1): healing arms a short damage buff.
    ///
    /// Two arming routes, both measured on the APPLIED health delta (what the vital write actually added,
    /// never the requested amount, so overheal arms nothing):
    ///  - a single NON-LEECH heal of at least thresholdFraction of maximum health (5% at the default);
    ///  - LEECH heals (Drain return, Transfusion share, Bloodlust lifesteal) summing to that same amount
    ///    inside a rolling window (2 s at the default). Leech heals come in many small pieces, so they
    ///    accumulate rather than each having to clear the bar alone. The window empties when it arms.
    ///
    /// Arming while already active REFRESHES the expiry; it never stacks. The bonus is not stored at all -
    /// it is recomputed from the live rank and Healing affinity on every read - so a second trigger cannot
    /// double it by construction.
    ///
    /// Kept as statics plus one small state object so the arming rules are unit-testable without a live
    /// Player; Player_Hemomancy.cs owns the per-player instance and the chat line.
    /// </summary>
    public static class HemomancyMath
    {
        /// <summary>
        /// Tolerance on the threshold comparison so an exactly-threshold heal (5% of a max health whose
        /// product is not representable in binary) is never refused by a rounding hair.
        /// </summary>
        public const double ThresholdEpsilon = 1e-9;

        /// <summary>The arming amount in health points: maxHealth * thresholdFraction (0 when either is not positive).</summary>
        public static double ThresholdAmount(double maxHealth, double thresholdFraction)
        {
            if (maxHealth <= 0.0 || thresholdFraction <= 0.0)
                return 0.0;

            return maxHealth * thresholdFraction;
        }

        /// <summary>
        /// TRUE when a heal of <paramref name="applied"/> points meets the threshold. Compared as a FRACTION
        /// of max health with a tiny tolerance: exactly 5% arms, 4.99% does not. A non-positive max health or
        /// threshold never arms (a mis-set 0 threshold must not make every heal arm).
        /// </summary>
        public static bool MeetsThreshold(double applied, double maxHealth, double thresholdFraction)
        {
            if (applied <= 0.0 || maxHealth <= 0.0 || thresholdFraction <= 0.0)
                return false;

            return applied / maxHealth >= thresholdFraction - ThresholdEpsilon;
        }

        /// <summary>
        /// The damage multiplier (1.0 = none): 1 + rank * percentPerRank * affinityMultiplier while the buff
        /// is active, else exactly 1.0. Healing MULTIPLIES the rank bonus (the 2026-09-12 affinity shape);
        /// a multiplier below 1 is floored at 1, matching Adrenaline.DamageBonus. No gear term exists.
        /// </summary>
        public static float DamageMultiplier(int rank, double percentPerRank, double affinityMultiplier, bool active)
        {
            if (!active || rank <= 0 || percentPerRank <= 0.0)
                return 1.0f;

            var bonus = rank * percentPerRank * (affinityMultiplier < 1.0 ? 1.0 : affinityMultiplier);

            return (float)(1.0 + bonus);
        }
    }

    /// <summary>
    /// The rolling leech accumulator: entries older than the window are evicted before every add, and the
    /// whole window clears on arming. Pure - the caller supplies "now".
    /// </summary>
    public sealed class HemomancyLeechWindow
    {
        private readonly Queue<(double Time, double Amount)> entries = new Queue<(double Time, double Amount)>();

        /// <summary>Sum of the amounts currently inside the window.</summary>
        public double Sum { get; private set; }

        /// <summary>Number of entries currently inside the window.</summary>
        public int Count => entries.Count;

        /// <summary>
        /// Drops every entry whose age exceeds <paramref name="windowSeconds"/> (an entry exactly at the
        /// window edge is kept). The sum is recomputed from what is left, so float drift cannot accumulate
        /// across many add/evict cycles.
        /// </summary>
        public void Evict(double now, double windowSeconds)
        {
            var evicted = false;

            while (entries.Count > 0 && now - entries.Peek().Time > windowSeconds)
            {
                entries.Dequeue();
                evicted = true;
            }

            if (evicted)
            {
                var sum = 0.0;
                foreach (var entry in entries)
                    sum += entry.Amount;
                Sum = sum;
            }
        }

        /// <summary>Evicts stale entries, then records this one.</summary>
        public void Add(double now, double amount, double windowSeconds)
        {
            Evict(now, windowSeconds);

            if (amount <= 0.0)
                return;

            entries.Enqueue((now, amount));
            Sum += amount;
        }

        public void Clear()
        {
            entries.Clear();
            Sum = 0.0;
        }
    }

    /// <summary>What one heal did to the buff.</summary>
    public enum HemomancyArmResult
    {
        /// <summary>The heal did not reach the threshold (nor complete a leech window).</summary>
        None,

        /// <summary>The buff was inactive and is now active - the only result that announces.</summary>
        Armed,

        /// <summary>The buff was already active; its expiry moved out, nothing stacked.</summary>
        Refreshed,
    }

    /// <summary>
    /// Per-player Hemomancy state: the buff's expiry and the leech window. Transient (never persisted) and
    /// touched only on the owning player's landblock thread, the same contract as the rest of the class
    /// ability buff family.
    /// </summary>
    public sealed class HemomancyState
    {
        public readonly HemomancyLeechWindow LeechWindow = new HemomancyLeechWindow();

        /// <summary>Unix time the buff ends at; 0 = never armed.</summary>
        public double BuffEndTime { get; private set; }

        public bool IsActive(double now) => BuffEndTime > 0.0 && now <= BuffEndTime;

        /// <summary>
        /// Feeds one APPLIED heal into the arming rules. A non-leech heal arms on its own size; a leech heal
        /// accumulates in the window and arms on the window sum. Any arming clears the leech window and sets
        /// the expiry to now + duration (a refresh, never an extension past that).
        /// </summary>
        public HemomancyArmResult OnHealthRestored(double now, double applied, bool isLeech, double maxHealth,
            double thresholdFraction, double leechWindowSeconds, double durationSeconds)
        {
            if (applied <= 0.0 || durationSeconds <= 0.0)
                return HemomancyArmResult.None;

            bool arms;

            if (isLeech)
            {
                LeechWindow.Add(now, applied, Math.Max(0.0, leechWindowSeconds));
                arms = HemomancyMath.MeetsThreshold(LeechWindow.Sum, maxHealth, thresholdFraction);
            }
            else
            {
                arms = HemomancyMath.MeetsThreshold(applied, maxHealth, thresholdFraction);
            }

            if (!arms)
                return HemomancyArmResult.None;

            var wasActive = IsActive(now);

            LeechWindow.Clear();
            BuffEndTime = now + durationSeconds;

            return wasActive ? HemomancyArmResult.Refreshed : HemomancyArmResult.Armed;
        }

        /// <summary>
        /// TRUE exactly once when an armed buff has lapsed (it was armed and <paramref name="now"/> is past its
        /// expiry), and clears the expiry so the next call is FALSE and the next arming reads as a fresh
        /// activation. The one-shot behind the "Hemomancy fades." line.
        /// </summary>
        public bool TryConsumeExpiry(double now)
        {
            if (BuffEndTime <= 0.0 || IsActive(now))
                return false;

            BuffEndTime = 0.0;
            return true;
        }

        /// <summary>Drops the buff and the window (rank lost, or an explicit reset).</summary>
        public void Reset()
        {
            LeechWindow.Clear();
            BuffEndTime = 0.0;
        }
    }
}
