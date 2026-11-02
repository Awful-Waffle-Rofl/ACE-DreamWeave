using System;
using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// The per-caster burn table behind Elemental Proficiency's Acid effect (Archmage T3). Kept off Player so the
    /// whole stateful loop is testable in a host where Player's static initializer cannot run: Player supplies the
    /// scheduler (an ActionChain on its own queue), the liveness check and the damage call, and this class owns
    /// every decision.
    ///
    /// REFRESH, LIKE ACID PROC (owner ruling 2026-11-02): one burn per target with a single tick counter. While a
    /// target's loop is running, a new acid hit overwrites the per-tick amount and resets the remaining ticks to
    /// the full budget. It schedules NOTHING, so the running loop keeps its own cadence and the next tick lands
    /// exactly when it would have. With no running loop, a new burn starts, and its first tick comes one interval
    /// after the hit. Unlike Acid Proc there is no immediate tick, because the hit itself just landed.
    ///
    /// An entry exists only while its loop runs: the loop removes it after its last tick, or when the target is
    /// gone, so "has an entry" and "loop running" are the same fact. A chain left over from a cleared table (see
    /// <see cref="Clear"/>) finds a different instance, or none, under its guid and stops without touching it.
    ///
    /// Not thread-safe: every call, and every action handed to the scheduler, must run on the caster's own queue.
    /// </summary>
    public sealed class ElementalAcidBurnTracker
    {
        /// <summary>What a landing acid hit does to the target's burn.</summary>
        public enum Plan
        {
            /// <summary>No loop is running: create the entry and schedule its first tick one interval out.</summary>
            StartNew,

            /// <summary>A loop is running: overwrite the amount, reset the ticks, schedule nothing.</summary>
            Refresh,
        }

        /// <summary>The new-versus-refresh decision, from whether the target's loop is running. Pure.</summary>
        public static Plan PlanHit(bool loopRunning) => loopRunning ? Plan.Refresh : Plan.StartNew;

        private sealed class Burn
        {
            public int RemainingTicks;
            public uint TickAmount;
        }

        private readonly Dictionary<uint, Burn> burns = new Dictionary<uint, Burn>();

        /// <summary>Number of targets with a running burn.</summary>
        public int Count => burns.Count;

        /// <summary>True while <paramref name="guid"/> has a running burn.</summary>
        public bool IsBurning(uint guid) => burns.ContainsKey(guid);

        /// <summary>The running burn's per-tick amount and remaining ticks, for tests and readouts. False with no burn.</summary>
        public bool TryGetState(uint guid, out uint tickAmount, out int remainingTicks)
        {
            if (burns.TryGetValue(guid, out var burn))
            {
                tickAmount = burn.TickAmount;
                remainingTicks = burn.RemainingTicks;
                return true;
            }

            tickAmount = 0;
            remainingTicks = 0;
            return false;
        }

        /// <summary>Drops every burn. Any loop still scheduled finds its entry gone and stops.</summary>
        public void Clear() => burns.Clear();

        /// <summary>
        /// Applies one landed acid hit. Returns the plan taken: <see cref="Plan.StartNew"/> only when a new burn
        /// started, which is when the caller tells the caster. <paramref name="intervalSeconds"/>,
        /// <paramref name="targetAlive"/>, <paramref name="dealTick"/> and <paramref name="schedule"/> are used only
        /// by a new burn; a refresh keeps the running loop's. A zero amount or a tick budget below 1 does nothing
        /// and reports a refresh, so no chat line fires.
        /// </summary>
        public Plan Apply(uint guid, uint tickAmount, int ticks, double intervalSeconds,
            Func<bool> targetAlive, Action<uint> dealTick, Action<double, Action> schedule)
        {
            if (tickAmount == 0 || ticks < 1)
                return Plan.Refresh;

            burns.TryGetValue(guid, out var existing);

            var plan = PlanHit(existing != null);

            if (plan == Plan.Refresh)
            {
                existing.TickAmount = tickAmount;
                existing.RemainingTicks = ticks;
                return plan;
            }

            var burn = new Burn { RemainingTicks = ticks, TickAmount = tickAmount };
            burns[guid] = burn;

            ScheduleTick(guid, burn, intervalSeconds, targetAlive, dealTick, schedule);

            return plan;
        }

        private void ScheduleTick(uint guid, Burn startedFor, double intervalSeconds,
            Func<bool> targetAlive, Action<uint> dealTick, Action<double, Action> schedule)
        {
            schedule(intervalSeconds, () =>
            {
                // a cleared table leaves this chain holding a stale instance: stop without touching the current one
                if (!burns.TryGetValue(guid, out var burn) || !ReferenceEquals(burn, startedFor))
                    return;

                if (!targetAlive())
                {
                    burns.Remove(guid);
                    return;
                }

                // read at tick time, so a refresh's new amount is what this tick deals. If the damage call throws,
                // this loop is over: drop its entry (only if it is still this instance) so the next hit starts a new
                // burn instead of refreshing an entry no loop will ever tick, then let the exception propagate.
                try
                {
                    dealTick(startedFor.TickAmount);
                }
                catch
                {
                    if (burns.TryGetValue(guid, out var current) && ReferenceEquals(current, startedFor))
                        burns.Remove(guid);
                    throw;
                }

                // the damage call can reach code that clears the table (a death); never schedule or remove for a
                // burn that is no longer the current one
                if (!burns.TryGetValue(guid, out burn) || !ReferenceEquals(burn, startedFor))
                    return;

                startedFor.RemainingTicks--;

                if (startedFor.RemainingTicks > 0)
                    ScheduleTick(guid, startedFor, intervalSeconds, targetAlive, dealTick, schedule);
                else
                    burns.Remove(guid);
            });
        }
    }
}
