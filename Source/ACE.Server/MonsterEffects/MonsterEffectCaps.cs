using ACE.Server.Managers;

namespace ACE.Server.MonsterEffects
{
    /// <summary>
    /// The live ceilings the monster-effect layer answers to, one accessor per tunable.
    ///
    /// Every one reads PropertyManager on each call rather than caching, so a ceiling can be moved with
    /// /modifyserverdouble on a running world and take effect on the next attack - the whole reason these
    /// are tunables and not constants. The reads are cheap (a dictionary hit) and none of them sits inside a
    /// loop; a dispatch site that needs a cap several times in one calculation should read it once into a
    /// local, not memoise it here.
    ///
    /// These are CAPS, not magnitudes. A per-effect magnitude is authored on the weenie (PropertyString
    /// 9015), because it varies per monster; a cap bounds what any weenie may ask for and so belongs to the
    /// server. The split is what lets a content author tune one monster without a code change and lets the
    /// repo owner bound the whole layer without touching content.
    ///
    /// Nothing here is enforced automatically. A cap applies where its number is consumed, and each phase
    /// names the cap it clamps against in Docs/MonsterEffects/CATALOG.md.
    /// </summary>
    public static class MonsterEffectCaps
    {
        /// <summary>
        /// Master switch. Read at the DISPATCH sites, never at construction: a monster built while the
        /// system was off must still pick the effects up when it is switched back on, without a relog or a
        /// landblock reset.
        /// </summary>
        public static bool Enabled => PropertyManager.GetBool("monster_effects_enabled").Item;

        /// <summary>Maximum total chance a monster may avoid an incoming attack through this layer.</summary>
        public static double AvoidanceCap => PropertyManager.GetDouble("monster_effect_avoidance_cap").Item;

        /// <summary>Maximum chance for any one on-hit proc, after every ramp and bonus.</summary>
        public static double ProcChanceCap => PropertyManager.GetDouble("monster_effect_proc_chance_cap").Item;

        /// <summary>Maximum fraction of damage dealt that a monster may convert into its own vitals.</summary>
        public static double LeechCap => PropertyManager.GetDouble("monster_effect_leech_cap").Item;

        /// <summary>Maximum fraction of incoming damage a monster may send back at its attacker.</summary>
        public static double ReflectCap => PropertyManager.GetDouble("monster_effect_reflect_cap").Item;

        /// <summary>Maximum attack- or cast-speed multiplier from this layer.</summary>
        public static double SpeedCap => PropertyManager.GetDouble("monster_effect_speed_cap").Item;

        /// <summary>Maximum stacks any ramp may hold, whatever the record's own max says.</summary>
        public static int RampStackCap => (int)PropertyManager.GetLong("monster_effect_ramp_stack_cap").Item;

        /// <summary>Maximum extra damage one flat rider may add to a single hit.</summary>
        public static double DamageRiderCap => PropertyManager.GetDouble("monster_effect_damage_rider_cap").Item;

        /// <summary>
        /// Maximum magnitude a debuff may apply on its POINT-valued shapes - what=imperil (armor points) and
        /// what=attackskills (skill points). Not the vuln shape: see <see cref="DebuffVulnCap"/> for why the
        /// two cannot share one number.
        /// </summary>
        public static double DebuffCap => PropertyManager.GetDouble("monster_effect_debuff_cap").Item;

        /// <summary>
        /// Maximum magnitude a debuff may apply on what=vuln, whose mag= is a FRACTION added to a resist
        /// multiplier (mag=0.5 means 1.5x damage taken) rather than a point count. A single cap across all
        /// three shapes cannot work: any ceiling loose enough for armor points passes a mag=50 typo straight
        /// through as a 51x multiplier.
        /// </summary>
        public static double DebuffVulnCap => PropertyManager.GetDouble("monster_effect_debuff_vuln_cap").Item;

        /// <summary>Maximum effects one monster may carry; the rest of the authored list is dropped.</summary>
        public static int MaxPerMonster => (int)PropertyManager.GetLong("monster_effect_max_per_monster").Item;

        /// <summary>Maximum extra casts one completed cast may chain into.</summary>
        public static int RecastCap => (int)PropertyManager.GetLong("monster_effect_recast_cap").Item;

        /// <summary>Maximum fraction of incoming damage a manabarrier effect may divert to Mana.</summary>
        public static double ManaBarrierCap => PropertyManager.GetDouble("monster_effect_manabarrier_cap").Item;

        /// <summary>Maximum ward grant, as a multiple of the carrying monster's own maximum health.</summary>
        public static double WardCap => PropertyManager.GetDouble("monster_effect_ward_cap").Item;
    }
}
