using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// goals.json type "KillCount" (TECH-DESIGN 2.5, PLAN 1.7). Completes once <see cref="Target"/> event
    /// creatures that are NOT one of the theme's objective (rift) spawns have died.
    ///
    /// Target sizing (PLAN 1.7: "n derived from participants at Staged") would naturally be
    /// <c>goal.Count.Resolve(audience.Count)</c> at construction, but WorldEventManager.TryStart builds the
    /// objective (via the factory) BEFORE WorldEvent.Stage runs - see WorldEventManager.cs's TryStart body
    /// and WorldEvent.Stage, which samples Audience only after the event is already Staged. Audience.Count
    /// is therefore always 0 at construction time. Freezing the target at construction would make every
    /// KillCount run target the base count regardless of turnout, so the factory instead hands this class a
    /// <see cref="Func{TResult}"/> and the target is resolved and frozen on the FIRST evaluation (the first
    /// Tick or OnCreatureDied after Stage has actually sampled the audience), never re-resolved after that.
    /// </summary>
    public sealed class KillCountObjective : IWorldEventObjective
    {
        private readonly GoalDef goal;
        private readonly Func<int> participantCount;
        private readonly Func<IReadOnlyCollection<uint>> sourceGuids;
        private readonly WorldEventParticipation ledger;

        private int killed;
        private int? target;

        private string lastKillerName;
        private string topDamagerName;

        public KillCountObjective(GoalDef goal, Func<int> participantCount,
            Func<IReadOnlyCollection<uint>> sourceGuids, WorldEventParticipation ledger)
        {
            this.goal = goal;
            this.participantCount = participantCount ?? (() => 0);
            this.sourceGuids = sourceGuids ?? (() => Array.Empty<uint>());
            this.ledger = ledger;
        }

        /// <summary>
        /// Resolved from goal.Count against participantCount() on first read. No longer frozen for the rest
        /// of the run (TECH-DESIGN 2.15): <see cref="OnAudienceResampled"/> may RATCHET it upwards as the
        /// crowd grows. It can never fall - see that method's remarks for why.
        /// </summary>
        private int Target
        {
            get
            {
                if (target == null)
                    target = goal?.Count?.Resolve(participantCount()) ?? 0;

                return target.Value;
            }
        }

        /// <summary>
        /// TECH-DESIGN 2.15: re-size against a fresh audience sample, upwards only.
        ///
        /// This is also what first sizes an objective whose target was never read - which is the normal
        /// case, since the very first wave re-samples the audience before anything has died. The old
        /// freeze-on-first-read rule stays as the fallback for a run that somehow evaluates before any
        /// sample.
        /// </summary>
        public void OnAudienceResampled(int participantCount)
        {
            var resolved = goal?.Count?.Resolve(Math.Max(0, participantCount)) ?? 0;

            if (target == null || resolved > target.Value)
                target = resolved;
        }

        /// <summary>
        /// The current target, or null while it has never been sized. Reads the backing field rather than
        /// the <see cref="Target"/> property on purpose: the status line asking a question must not be what
        /// decides how big the run is (the same trap <see cref="RemainingKills"/> documents).
        /// </summary>
        public int? TargetKills => target;

        public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            var guid = creature?.Guid.Full ?? 0;
            var wcid = creature?.WeenieClassId ?? 0;
            var wasEventCreature = creature?.GetProperty(PropertyInt.WorldEventId) != null;

            OnDeathCore(guid, wcid, wasEventCreature, WorldEventMvpResolver.ResolveName(lastDamager),
                WorldEventMvpResolver.ResolveName(topDamager));
        }

        /// <summary>
        /// Testable core (TECH-DESIGN D6): no engine object crosses this boundary. Ignores anything that
        /// isn't a stamped event creature, and ignores objective (rift) spawns - those are the theme's own
        /// pressure, not kill-count progress.
        /// </summary>
        public void OnDeathCore(uint guid, uint wcid, bool wasEventCreature, string lastDamagerName, string topDamagerName)
        {
            // Touches Target so the freeze also happens on the first death, not only the first Tick.
            _ = Target;

            if (!wasEventCreature)
                return;

            if (sourceGuids().Contains(guid))
                return;

            killed++;
            lastKillerName = lastDamagerName;
            this.topDamagerName = topDamagerName;
        }

        public void Tick(double now)
        {
            // Touches Target so a run with no deaths yet still freezes the count as soon as the objective is
            // first evaluated, per the class remarks.
            _ = Target;
        }

        public bool IsComplete => killed >= Target;

        /// <summary>
        /// WP-20: kills still needed, floored at 0, or null while the lazy target is still UNFROZEN.
        ///
        /// Reads the backing field rather than the <see cref="Target"/> property on purpose - touching that
        /// property is what freezes the target against the current participant count, and the wave cadence
        /// asking a question must never be what decides how big the run is. Before the freeze the honest
        /// answer is "no opinion yet", which is also the safe one: nothing has died, so the full target is
        /// still outstanding and the cadence should behave exactly as before.
        /// </summary>
        public int? RemainingKills => target == null ? (int?)null : Math.Max(0, target.Value - killed);

        public string ProgressText
        {
            get
            {
                var template = goal?.ProgressTemplate ?? "{killed} of {target} slain.";

                return template.Replace("{killed}", killed.ToString()).Replace("{target}", Target.ToString());
            }
        }

        public WorldEventMvp Mvp()
        {
            switch (goal?.RuleKind)
            {
                case MvpRule.MostKills:
                    return WorldEventMvpResolver.MostKills(ledger);

                case MvpRule.KillingBlow:
                    return WorldEventMvpResolver.KillingBlow(lastKillerName);

                case MvpRule.KillingBlowAndTopDamage:
                    return WorldEventMvpResolver.KillingBlowAndTopDamage(lastKillerName, topDamagerName);

                default:
                    return WorldEventMvp.None;
            }
        }
    }
}
