using System;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.Knots
{
    /// <summary>
    /// The Fiber level a Thread run stamps (Docs/Knots/KNOTS-DESIGN.md section 3.2): the floor of the mean of the
    /// run's PLANNED spawn set at the levels ThreadDungeonSpawner actually places each creature at, boss counted once.
    /// Pure: the authored level arrives through <c>levelOf</c> (the live weenie's PropertyInt.Level in production).
    ///
    /// Mirrors the spawner's level writes, in its order (ThreadDungeonSpawner.TryPlaceOnce):
    ///   - boss: plan.BossLevel when it is above 0, else the authored level; then BandUplift.LevelFloor raises it to
    ///     the entry's UpliftLevel when that is higher (ApplyUplift runs after the boss stamp). A boss takes no
    ///     reach-up stamp (the spawner's stamp write is gated on !isBoss).
    ///   - everyone else: the authored level, raised to StampLevel when higher, then to UpliftLevel when higher.
    /// Press level ops need no term here: they change the gem's spec level (RawFragmentRules' WithLevel) before the
    /// run exists, so the plan is already built from the pressed level.
    /// </summary>
    public static class KnotLevelMath
    {
        /// <summary>The planned average level, or null for a null or empty plan (no Fiber can roll then).</summary>
        public static int? PlannedAverage(DungeonSpawnPlan plan, Func<uint, int> levelOf)
        {
            if (plan == null || plan.Entries.Count == 0 || levelOf == null)
                return null;

            long sum = 0;
            var count = 0;
            var bossCounted = false;

            foreach (var entry in plan.Entries)
            {
                if (entry == null)
                    continue;

                var level = PlacedLevel(entry, plan.BossLevel, levelOf(entry.Wcid), ref bossCounted);

                if (level == null)
                    continue;

                sum += level.Value;
                count++;
            }

            if (count == 0)
                return null;

            // Floor, not truncation, so a (pathological) negative authored level still rounds down.
            return (int)Math.Floor((double)sum / count);
        }

        /// <summary>
        /// One entry's placed level, or null for a second boss entry (the boss is counted once).
        /// </summary>
        internal static int? PlacedLevel(DungeonSpawnPlanEntry entry, int planBossLevel, int authoredLevel, ref bool bossCounted)
        {
            if (entry.Role == DungeonRole.Boss)
            {
                if (bossCounted)
                    return null;

                bossCounted = true;

                var boss = planBossLevel > 0 ? planBossLevel : authoredLevel;

                return entry.UpliftLevel > boss ? entry.UpliftLevel : boss;
            }

            return Math.Max(authoredLevel, Math.Max(entry.StampLevel, entry.UpliftLevel));
        }
    }
}
