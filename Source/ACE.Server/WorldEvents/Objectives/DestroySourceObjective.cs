using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// goals.json type "DestroySource" (TECH-DESIGN 2.5, PLAN 1.8). Completes only once EVERY guid the
    /// theme's objective spawner has ever placed (WorldEventSpawner.SourceGuids - "rifts") has been reported
    /// dead. A theme with no objective wcid (ObjectiveWcid == 0) never places a source, so this objective
    /// never completes on its own - the run's timers (max duration / abandon) are what end it, per R16.
    /// </summary>
    public sealed class DestroySourceObjective : IWorldEventObjective
    {
        private readonly GoalDef goal;
        private readonly Func<IReadOnlyCollection<uint>> sourceGuids;

        /// <summary>
        /// What ONE objective spawn is called in player-facing text, and its plural (2026-08-19) - "rift" /
        /// "rifts" by default, "pillar" / "pillars" on the element portal themes. They come from the SOURCE
        /// (SourceThemeDef.ObjectiveNoun/ObjectiveNounPlural), not the goal, which is what lets a single
        /// destroy_source goal serve both without the hardcoded "rift" this class used to carry.
        /// </summary>
        private readonly string noun;
        private readonly string nounPlural;

        private readonly HashSet<uint> dead = new HashSet<uint>();

        private string lastKillerName;
        private string topDamagerName;

        public DestroySourceObjective(GoalDef goal, Func<IReadOnlyCollection<uint>> sourceGuids)
            : this(goal, sourceGuids, null, null)
        {
        }

        /// <summary>
        /// <paramref name="noun"/> / <paramref name="nounPlural"/> null or blank fall back to
        /// "rift" / "rifts", so the two-argument constructor and any caller with no source in hand keep the
        /// pre-2026-08-19 wording exactly.
        /// </summary>
        public DestroySourceObjective(GoalDef goal, Func<IReadOnlyCollection<uint>> sourceGuids,
            string noun, string nounPlural)
        {
            this.goal = goal;
            this.sourceGuids = sourceGuids ?? (() => Array.Empty<uint>());

            this.noun = !string.IsNullOrWhiteSpace(noun) ? noun : SourceThemeDef.DefaultObjectiveNoun;
            this.nounPlural = !string.IsNullOrWhiteSpace(nounPlural) ? nounPlural : SourceThemeDef.DefaultObjectiveNounPlural;
        }

        public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
        {
            OnDeathCore(creature?.Guid.Full ?? 0, WorldEventMvpResolver.ResolveName(lastDamager),
                WorldEventMvpResolver.ResolveName(topDamager));
        }

        /// <summary>Testable core (D6). A guid that is not one of the theme's registered sources is ignored.</summary>
        public void OnDeathCore(uint guid, string lastDamagerName, string topDamagerName)
        {
            var sources = sourceGuids() ?? Array.Empty<uint>();

            if (!sources.Contains(guid))
                return;

            dead.Add(guid);
            lastKillerName = lastDamagerName;
            this.topDamagerName = topDamagerName;
        }

        public void Tick(double now)
        {
        }

        public bool IsComplete
        {
            get
            {
                var sources = sourceGuids() ?? Array.Empty<uint>();

                if (sources.Count == 0)
                    return false;

                foreach (var guid in sources)
                {
                    if (!dead.Contains(guid))
                        return false;
                }

                return true;
            }
        }

        public string ProgressText
        {
            get
            {
                var sources = sourceGuids() ?? Array.Empty<uint>();
                var total = sources.Count;

                if (total == 0)
                    return $"no {nounPlural} were placed";

                var remaining = sources.Count(g => !dead.Contains(g));

                var template = goal?.ProgressTemplate ?? "{remaining} of {total} {nounPlural} remain.";

                return template
                    .Replace("{remaining}", remaining.ToString())
                    .Replace("{total}", total.ToString())
                    .Replace("{nounPlural}", nounPlural)
                    .Replace("{noun}", noun);
            }
        }

        /// <summary>
        /// Killing blow on the last objective spawn to die, plus the top damager on that same spawn when it
        /// is a different person (PLAN 1.8). Not driven by goal.mvpRule - see WorldEventMvpResolver's remarks.
        /// </summary>
        /// <summary>
        /// WP-20: null - "no opinion". This objective completes when every rift is down, which trash deaths
        /// do not advance at all, so it must never suppress a wave. Returning 0 here would stop the theme
        /// spawning pressure for the rest of the run.
        /// </summary>
        public int? RemainingKills => null;

        public WorldEventMvp Mvp()
        {
            return WorldEventMvpResolver.KillerPlusTopDamage(lastKillerName, topDamagerName,
                $"destroyed the last {noun}", $"destroyed the last {noun}, top damage: {{0}}");
        }
    }
}
