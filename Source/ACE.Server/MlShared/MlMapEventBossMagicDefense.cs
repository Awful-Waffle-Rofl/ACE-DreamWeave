using System;

namespace ACE.Server.MlShared
{
    /// <summary>
    /// RoZ round 19 owner ruling M1: Marae Lassel map-event bosses (the digsite Boss/MiniBoss/Checkpoint/
    /// Priority roles, and the Aun Relaria treasure-map boss) resist magic too heavily. Rather than hand-edit
    /// every boss weenie's authored MagicDefense - which the weenie generator would silently revert on its
    /// next regeneration pass, and several of these boss SQL files already carry hand edits it would revert
    /// (see Content/sql/weenies/1002853 Rauhea the Longwing.sql) - the scale-down is applied here, once, at
    /// spawn, to both hand-spawners (MlDigsiteSpawner.TrySpawn, MlRelariaSpawner.PrepareBoss).
    ///
    /// <see cref="ScaleInitLevel"/> is the ONLY math in this class and takes no PropertyManager reads, so it
    /// stays unit-testable without the test-harness PropertyManager-read restriction (PropertyManager reads
    /// throw under MSTest - see MlDigsiteTunables's own remarks). Callers read the live tunable
    /// (ml_mapevent_boss_magic_defense_scale) themselves and pass the resolved double in.
    /// </summary>
    public static class MlMapEventBossMagicDefense
    {
        /// <summary>
        /// The InitLevel to stamp so the creature's current MagicDefense becomes
        /// round(authoredCurrent * scale), achieved by LOWERING InitLevel alone (never raising it - a scale
        /// above 1 is clamped away before this is called) and never below 0, which is the skill's own floor
        /// (CreatureSkill.InitLevel is a uint). <paramref name="scale"/> is expected already clamped to
        /// [0, 1] by the caller (the tunable clamps on read); this method clamps again defensively so it is
        /// never the reason InitLevel goes negative or the skill goes up.
        /// </summary>
        /// <param name="authoredInitLevel">the creature's InitLevel exactly as spawned/authored</param>
        /// <param name="authoredCurrentSkill">the creature's Current MagicDefense before this scale is
        /// applied - the value the target is measured against</param>
        /// <param name="scale">multiplier on authoredCurrentSkill; 1.0 is a no-op</param>
        public static uint ScaleInitLevel(uint authoredInitLevel, uint authoredCurrentSkill, double scale)
        {
            if (double.IsNaN(scale))
                scale = 1.0;

            scale = Math.Clamp(scale, 0.0, 1.0);

            var target = (uint)Math.Round(authoredCurrentSkill * scale, MidpointRounding.AwayFromZero);

            var delta = authoredCurrentSkill > target ? authoredCurrentSkill - target : 0;

            return delta >= authoredInitLevel ? 0 : authoredInitLevel - delta;
        }
    }
}
