using System;
using System.Linq;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Server-side "class abilities" - per-character combat perks learned by spending class ability points
    /// via the /abilities command. Distinct from the retail trained-Skill system (ACE.Entity.Enum.Skill).
    ///
    /// Each skill is one self-contained handler class under ClassAbilities/Skills implementing
    /// IClassAbility plus whichever hook interfaces (ClassAbilityHooks.cs) its mechanic needs.
    /// Ownership/rank persists as CharacterPropertiesQuestRegistry rows keyed ClassAbility_&lt;name&gt;
    /// (NumTimesCompleted = rank); point balances persist as custom PropertyInts on the player biota.
    /// See Player_ClassAbilities.cs for the runtime layer and ClassAbilityCommands.cs for the command surface.
    /// </summary>
    public enum ClassAbilityId
    {
        Multishot     = 1,
        Thorns        = 2,
        Taunt         = 3,
        PoisonWeapon  = 4,
        SpellAoe      = 5,
        BattleHardened = 6,
        // 7 (Fleetfoot) retired: player run speed is client-computed from the Run skill, so a
        // server-side run-rate multiplier can't affect the caster; the generated Enhanced Run
        // class ability (which boosts the client-visible Run skill) covers run speed instead.
        Frenzy        = 8,
        NetherRush    = 9,

        // --- redesign additions (SKILL-TABLES-PREVIEW). Numeric values are in-memory + token-content
        // identifiers only; ownership persists by name, so these must stay unique and disjoint from the
        // EnhancedStatAbility synthetic ranges (0x1000+), never reused. Append only. ---

        // Multi-skill passive "bundles"
        ArcherTraining      = 10,
        RogueTraining       = 11,
        VanguardTraining    = 12,
        BerserkerTraining   = 13,
        ArchmageTraining    = 14,
        VoidTraining        = 15,
        AdvancedWeaponry    = 16,   // RETIRED 2026-08-03 (skill redistribution) - id reserved, never re-use;
                                    // unregistered + inert. Heavy Weapons and Light Weapons split out of
                                    // Advanced Weaponry (Heavy Weapons is now homed under Vanguard via
                                    // EnhancedStatAbility; Light Weapons is unhomed).
        QuestionableTactics = 17,   // RETIRED 2026-08-03 (skill redistribution) - id reserved, never re-use;
                                    // unregistered + inert. Sneak Attack and Deception moved into Rogue
                                    // Training; Dirty Fighting is unhomed.

        // Rating skills (feed ACE's existing rating pools)
        CritRating          = 18,
        CritDamageRating    = 19,
        DamageResistRating  = 20,
        CritResistRating    = 21,
        DamageRating        = 22,

        // Archer combat
        Deadeye             = 23,
        EagleEye            = 24,
        HeavyDraw           = 25,
        DoubleVolley        = 26,
        LongDraw            = 27,

        // Rogue combat
        Parry               = 28,
        Riposte             = 29,
        AttackSpeed         = 30,
        AcidProc            = 31,

        // Vanguard combat
        ShieldBlock         = 32,
        ShieldCheck         = 33,

        // Berserker combat
        SavageBlows         = 34,
        BloodFury           = 35,
        Executioner         = 36,
        Whirlwind           = 37,
        Bloodlust           = 38,

        // Archmage combat
        FlatCastSpeed       = 39,
        Overchannel         = 40,
        EchoCast            = 41,
        ElementalRend       = 42,

        // Void / Summon combat
        StreakToArc         = 43,   // RETIRED 2026-07-18 - id reserved, never re-use; unregistered + inert
        EmpoweredSummons    = 44,
        Summon2x            = 45,
        VoidDamage          = 46,
        Withering           = 47,

        // Tier-2 additions 2026-07-30 (append only - never reuse or renumber)
        ManaBarrier         = 48,   // Archmage T2
        NetherBloom         = 49,   // Void/Summon T2 game-changer (the slot StreakToArc vacated)
        SoulTether          = 50,   // Void/Summon T2

        // Blood Mage (7th class), scaffolded 2026-08-02, registered 2026-08-03. Append only, never renumber.
        // Eight of these have bespoke handler classes under ClassAbilities/Abilities; BloodMageTraining is
        // built by BundleStatAbility.GenerateAll and HealBoostRating by RatingAbility.GenerateAll.
        SanguineReserve     = 51,
        // RESERVED AND UNUSED. Enhanced Life Magic is a member of the GENERATED Enhanced-stat family, so its
        // real registry id is the synthetic EnhancedStatAbility.ClassIdForSkill(Skill.LifeMagic)
        // (= SkillIdBase + Skill.LifeMagic), not this value. The scaffold minted 52 before that was settled;
        // it is kept so no future id reuses it and so the persisted-name contract is never disturbed, but
        // nothing registers, resolves, or persists under it. Do not build an ability on this id.
        EnhancedLifeMagic   = 52,
        Transfusion         = 53,
        BloodMageTraining   = 54,
        WeakenedBlood       = 55,
        Malediction         = 56,
        CrimsonHarvest      = 57,
        HealBoostRating     = 58,
        Exsanguinate        = 59,
        BloodPrice          = 60,
        SanguineWard        = 61,

        // Spellsword (8th class), 2026-08-03. Append only, never renumber. Nine bespoke entries; the
        // tenth and eleventh class members are generated - SpellswordTraining is built by
        // BundleStatAbility.GenerateAll, and Enhanced Light Weapons / Enhanced Melee Defense are members
        // of the generated Enhanced-stat family whose real ids are the synthetic
        // EnhancedStatAbility.ClassIdForSkill(Skill.LightWeapons / Skill.MeleeDefense).
        Spellblade          = 62,   // T1 game-changer: Streak proc
        Resonance           = 63,   // T1
        SpellswordTraining  = 64,   // T1 bundle (generated by BundleStatAbility.GenerateAll)
        Runeblade           = 65,   // T2 game-changer: Blast proc
        Sundermark          = 66,   // T2: Vulnerability proc, any weapon
        Spellsurge          = 67,   // T2
        Spellstorm          = 68,   // T3 game-changer: Ring proc
        Cascade             = 69,   // T3
        DispellingEdge      = 70,   // T3
    }

    /// <summary>
    /// The seven skill "classes" a class ability can belong to (SKILL-TABLES-PREVIEW). Purely a grouping +
    /// tier-unlock axis - a character is not locked to one class; membership only decides which Training
    /// bundle a skill sits under and which pool its "points spent in class" tier unlock reads.
    /// <see cref="None"/> is for the generated Enhanced-stat family, which is untiered and open.
    /// </summary>
    public enum ClassAbilityClass
    {
        None = 0,
        Archer,
        Rogue,
        Vanguard,
        Berserker,
        Archmage,
        VoidSummon,
        BloodMage,
        Spellsword,
    }

    public class ClassAbilityDefinition
    {
        public ClassAbilityId Id;

        /// <summary>
        /// Which class this skill belongs to (None = the open, untiered Enhanced-stat family).
        /// Drives the "points spent in class" half of the Tier 2/3 unlock.
        /// </summary>
        public ClassAbilityClass AbilityClass = ClassAbilityClass.None;

        /// <summary>
        /// Tier within the class: 1 = open, 2/3 = gated behind cumulative-CAP and points-spent-in-class
        /// thresholds. 0 = untiered (Enhanced-stat family), always open. See Player tier-unlock check.
        /// </summary>
        public int Tier;

        /// <summary>
        /// Canonical command token - lowercase, single word, used in /abilities subcommands
        /// and as the quest registry key suffix
        /// </summary>
        public string Name;

        public string DisplayName;

        /// <summary>
        /// Grouping label for the /abilities listing (e.g. "Combat", "Enhanced Skill",
        /// "Enhanced Attribute", "Enhanced Vital"). Purely presentational.
        /// </summary>
        public string Category = "Combat";

        /// <summary>
        /// Shown by /abilities info - should include per-rank effect text
        /// </summary>
        public string Description;

        /// <summary>
        /// 1 = flat one-time unlock
        /// </summary>
        public int MaxRank;

        /// <summary>
        /// Point cost of each rank; length must equal MaxRank
        /// </summary>
        public int[] CostPerRank;

        /// <summary>
        /// FALSE = definition exists but the mechanic isn't wired up yet - listed as
        /// "Coming soon" and not learnable
        /// </summary>
        public bool Implemented;

        /// <summary>
        /// Total points invested to be at the given rank
        /// </summary>
        public int CumulativeCost(int rank) => CostPerRank.Take(Math.Clamp(rank, 0, MaxRank)).Sum();
    }
}
