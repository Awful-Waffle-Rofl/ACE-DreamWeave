using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

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

                    // C6: Hold is deferred to P2. The enum value exists so a later WP can add the
                    // implementation without a rename, but the factory must refuse it in the meantime.
                    error = "goal type Hold is deferred to P2 (C6)";
                    return null;

                default:

                    error = "unknown goal type";
                    return null;
            }
        }

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
