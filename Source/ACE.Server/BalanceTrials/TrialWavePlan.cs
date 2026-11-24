using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The trial monster plan. The plan comes from the live builder, solo, with the live readers; the waves are a selection
    /// over it and the room is a re-pointing of it. Nothing here picks a wcid.
    /// </summary>
    public static class TrialWavePlan
    {
        /// <summary>Builds the plan through the live builder with solo group scaling. Callers pass LivePlanSources.Live; a test fixture supplies its own whole set of sources.</summary>
        internal static DungeonSpawnPlan BuildPlan(DungeonGemSpec spec, ThreadDungeonSpawner.LivePlanSources sources, out ThreadDungeonSpawner.LivePlanBuildStats stats)
        {
            if (!sources.Store.Dungeons.TryGetValue(spec.DungeonId, out var dungeon))
                throw new TrialArchetypeException($"dungeon '{spec.DungeonId}' is not in the dungeon store");

            return ThreadDungeonSpawner.BuildLivePlan(spec, dungeon, GroupScaling.Solo, sources, () => $"trial {spec}", out stats);
        }

        /// <summary>
        /// The first Count entries of each wave's role, in plan order. An entry an earlier wave took is not offered again, so two waves
        /// of one role split the pool. A short role, or a Boss wave when the plan found no boss candidate, adds one shortfall line.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<DungeonSpawnPlanEntry>> Select(DungeonSpawnPlan plan, IReadOnlyList<TrialWave> waves, out IReadOnlyList<string> shortfalls)
        {
            var lines = new List<string>();
            var taken = new HashSet<DungeonSpawnPlanEntry>();
            var result = new List<IReadOnlyList<DungeonSpawnPlanEntry>>();

            foreach (var wave in waves)
            {
                var picked = plan.Entries.Where(e => e.Role == wave.Role && !taken.Contains(e)).Take(wave.Count).ToList();

                foreach (var e in picked)
                    taken.Add(e);

                var bossGap = wave.Role == DungeonRole.Boss && plan.BossNoCandidate;

                if (picked.Count < wave.Count || bossGap)
                    lines.Add($"{wave.Role}: wanted {wave.Count}, got {picked.Count}" + (bossGap ? " (the plan found no boss candidate)" : ""));

                result.Add(picked);
            }

            shortfalls = lines;
            return result;
        }

        /// <summary>
        /// Moves a wave into a room. Each new entry keeps Wcid, Role, UpliftLevel, StampLevel and Stretched; the Boss takes the room's
        /// boss point and every other entry takes the next room point in order.
        /// </summary>
        public static IReadOnlyList<DungeonSpawnPlanEntry> Repoint(IReadOnlyList<DungeonSpawnPlanEntry> wave, TrialRoom room)
        {
            var moved = new List<DungeonSpawnPlanEntry>(wave.Count);
            var next = 0;

            foreach (var e in wave)
            {
                DungeonSpawnPointDef point;

                if (e.Role == DungeonRole.Boss)
                    point = room.BossPoint;
                else
                {
                    if (next >= room.Points.Count)
                        throw new TrialArchetypeException($"room '{room.Id}' has {room.Points.Count} points but the wave needs more");

                    point = room.Points[next++];
                }

                moved.Add(new DungeonSpawnPlanEntry(e.Wcid, e.Role, point, e.UpliftLevel, e.StampLevel, e.Stretched));
            }

            return moved;
        }
    }
}
