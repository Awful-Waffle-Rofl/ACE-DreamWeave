using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;

namespace ACE.Server.WorldEvents.Objectives
{
    /// <summary>
    /// WP-06's factory body. WorldEventManager.TryStart calls this AFTER composing and constructing the
    /// (not-yet-Staged) WorldEvent, and refuses the start when it returns null - so a run whose objective
    /// cannot be built never reaches Staged/Announced.
    /// </summary>
    public static class WorldEventObjectiveFactory
    {
        public static IWorldEventObjective Create(WorldEvent evt, out string error)
        {
            error = null;

            var goal = evt?.Composition?.Goal;

            if (goal == null)
            {
                error = "unknown goal type";
                return null;
            }

            switch (goal.TypeKind)
            {
                case GoalType.KillCount:

                    // evt.Audience is not sampled until WorldEvent.Stage runs (TryStart builds the objective
                    // BEFORE calling Stage), so participantCount is handed as a Func and resolved lazily by
                    // KillCountObjective itself - see that class's remarks.
                    //
                    // The exclusion func is the UNION of SourceGuids and NpcGuids (WP-16), belt and braces
                    // alongside the withheld P_WorldEvent back-reference that is the PRIMARY guard against an
                    // npc death ever reaching OnDeathCore at all (Creature_Death.cs:130).
                    return new KillCountObjective(goal, () => evt.Audience.Count,
                        () => ExclusionGuids(evt.Spawner.SourceGuids, evt.Spawner.NpcGuids), evt.Participation);

                case GoalType.DestroySource:

                    // The nouns come from the SOURCE, not the goal (2026-08-19): destroy_source is one goal
                    // serving both the rift theme ("rift"/"rifts") and the element portals
                    // ("pillar"/"pillars"), so the progress line and the MVP sentence read correctly for
                    // whichever theme composed the run.
                    return new DestroySourceObjective(goal, () => evt.Spawner.SourceGuids,
                        evt.Composition?.ObjectiveNoun, evt.Composition?.ObjectiveNounPlural);

                case GoalType.KillBoss:

                    // WorldEvent spawns a boss for BOTH non-None kinds: the family champion picked from the
                    // roster, and the Named boss's own wcid (see WorldEvent.TickChampion, gated on
                    // Composition.Boss.Kind != BossKind.None). Only Boss.Kind == None would stage a run
                    // whose BossGuid can never leave 0, so the objective could never complete - refused
                    // here instead of left to time out.
                    if (evt.Composition?.Boss == null || evt.Composition.Boss.Kind == BossKind.None)
                    {
                        error = "goal kill_boss needs a --boss that is not 'none' (try --boss auto)";
                        return null;
                    }

                    // A named boss has a name worth putting in the progress line; a family champion is
                    // picked from a roster at spawn time and has none, so it keeps the generic wording.
                    var bossName = evt.Composition.Boss.Kind == BossKind.Named
                        ? evt.Composition.Boss.DisplayName
                        : null;

                    return new KillBossObjective(goal, () => evt.Spawner.BossGuid, bossName);

                case GoalType.Hold:

                    // World Events Hold: the owner's master switch world_events_hold_enabled ships OFF (the composer
                    // refuses a pinned Hold goal while it is off, too). Off - or unreadable - refuses the
                    // start and names the property, so an admin sees exactly what to turn on.
                    if (!HoldEnabled(goal))
                    {
                        error = $"goal type Hold is off ({HoldEnabledProperty} is false)";
                        return null;
                    }

                    if (goal.Protect == null)
                    {
                        error = $"goal '{goal.Id}' is a Hold goal with no protect block";
                        return null;
                    }

                    var hold = new HoldObjective(goal, new WorldEventHoldRun(evt.RunId), () => evt.ActiveAt, evt.Participation,
                        () => evt.Audience.Count, () => evt.Composition?.FamilyDisplayName);

                    // H5: the start-spot rule's portal plan (WorldEventHoldStartSpotRule), null when it made none.
                    hold.SetAreaPlan(evt.Composition?.AreaPlan);

                    // H6: the production run - the knights, the wave cadence, the run ticks and the run-shaping hooks.
                    hold.AttachEvent(evt);

                    return hold;

                default:

                    error = "unknown goal type";
                    return null;
            }
        }

        /// <summary>The master switch a Hold goal needs (world_events_hold_enabled).</summary>
        public const string HoldEnabledProperty = HoldKeys.PropertyPrefix + HoldKeys.Enabled;

        /// <summary>world_events_hold_enabled through the Hold settings resolver; false when it cannot be read.</summary>
        private static bool HoldEnabled(GoalDef goal)
        {
            try
            {
                return WorldEventHoldSettingsResolver.Flag(goal, HoldKeys.Enabled);
            }
            catch (Exception ex)
            {
                log.Warn($"[WORLDEVENT] could not read {HoldEnabledProperty} for goal '{goal?.Id}'; treating Hold as off", ex);
                return false;
            }
        }

        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventObjectiveFactory));

        /// <summary>
        /// The start-spot rule for <paramref name="goal"/>: where a run of it may really start. WorldEventManager.TryStart
        /// applies it after composing and before building the candidate run. A Hold goal gets WorldEventHoldStartSpotRule
        /// (clear area and town reach, H5); every other goal the no-op rule (the composed spot stands).
        ///
        /// THREADING: TryStart applies the rule UNDER the manager's lock, on the caller's thread (a command or the scheduler),
        /// not on the world thread. A rule that needs landblock or physics queries (clear-area checks, ground probes) must
        /// therefore resolve its spot BEFORE TryStart takes the lock and only check or apply the result here; doing the
        /// queries inside the rule would hold the manager lock across them. The Hold rule reads no landblock or physics state
        /// at all - only the client dat and the town list - so it resolves in place (see its remarks).
        /// </summary>
        public static IWorldEventStartSpotRule StartSpotRuleFor(GoalDef goal) =>
            StartSpotRuleOverride?.Invoke(goal)
            ?? (goal?.TypeKind == GoalType.Hold ? ACE.Server.WorldEvents.Hold.WorldEventHoldStartSpotRule.Instance : (IWorldEventStartSpotRule)WorldEventNoStartSpotRule.Instance);

        /// <summary>
        /// Test seam: when set, <see cref="StartSpotRuleFor"/> returns this function's rule (a null result falls back to the
        /// no-op). Production never sets it. Process-wide, so a test that sets it must restore it in a finally.
        /// </summary>
        internal static Func<GoalDef, IWorldEventStartSpotRule> StartSpotRuleOverride;

        /// <summary>
        /// The union of a theme's objective (rift/source) guids and its spawn-time npc guids (WP-16), read
        /// fresh on every call so it always reflects whatever the spawner has adopted so far.
        /// </summary>
        private static IReadOnlyCollection<uint> ExclusionGuids(IReadOnlyCollection<uint> sourceGuids,
            IReadOnlyCollection<uint> npcGuids)
        {
            if (npcGuids == null || npcGuids.Count == 0)
                return sourceGuids ?? Array.Empty<uint>();

            if (sourceGuids == null || sourceGuids.Count == 0)
                return npcGuids;

            return sourceGuids.Concat(npcGuids).ToList();
        }
    }
}
