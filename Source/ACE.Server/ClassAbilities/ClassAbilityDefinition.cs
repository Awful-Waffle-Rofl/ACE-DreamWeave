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
        HeavyDraw           = 25,   // RETIRED 2026-09-12 (class ability overhaul) - id reserved, never re-use;
                                    // unregistered + inert. Archer T2 was a wall of silent damage riders and
                                    // the stamina surcharge played badly; Pinning Shot takes the slot.
        DoubleVolley        = 26,
        LongDraw            = 27,

        // Rogue combat
        Parry               = 28,
        Riposte             = 29,
        AttackSpeed         = 30,
        AcidProc            = 31,

        // Vanguard combat
        ShieldBlock         = 32,
        ShieldCheck         = 33,   // RETIRED 2026-09-12 (class ability overhaul) - id reserved, never re-use;
                                    // unregistered + inert. It only did anything for a character who had also
                                    // bought Rogue's Parry; Kinetic Charge takes the Vanguard T2 slot.

        // Berserker combat
        SavageBlows         = 34,
        BloodFury           = 35,   // RETIRED 2026-08-17 (Berserker/Rogue balance pass) - id reserved, never
                                    // re-use; unregistered + inert. The Berserker T2 slot it held is taken by
                                    // Break Armor (71); its equipment mod (id 18 / PropertyFloat 8117) was
                                    // repurposed in place as Break Armor's proc-chance machinery mod.
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

        // Berserker / Rogue balance pass 2026-08-17. Ids RESERVED here in Phase 0 so the parallel Phase 1
        // worktrees agree on them; the handlers, token catalog slots and CAP totals land in Phases 1-2.
        // Append only, never renumber.
        BreakArmor          = 71,   // Berserker T2 (replaces Blood Fury)
        Surefooted          = 72,   // RETIRED 2026-09-12 (class ability overhaul) - id reserved, never re-use;
                                    // unregistered + inert. Its stacking avoidance overlapped Parry inside
                                    // the same pooled cap. Killer Instinct (77) replaces it in the class, but
                                    // at Rogue T3, NOT the T2 this comment claimed until 2026-09-12: T3 is
                                    // the signed-off placement and the only one that reproduces Rogue's 44
                                    // CAP total (T3 would otherwise be 9 and T2 18, against 15/15).
        PocketSand          = 73,   // Rogue T3

        // Class ability overhaul 2026-09-12, NEW-ABILITY WAVE. Fifteen entries: one 5-rank Tier 1 "splash"
        // ability per class (the eight ending in the T1 column below), plus seven others filling the slots
        // the same overhaul's three retirements vacated and the tier gaps the reprice opened. Ids are
        // RESERVED here in Phase 0 so the parallel Phase 1 worktrees agree on them. Each handler shipped
        // REGISTRATION ONLY (Implemented = false, no hook, no tunable) and has since been implemented by
        // its mechanic slice, so none of these is registration-only any more.
        // Append only, never renumber or reuse - ownership persists by NAME, but these numeric ids are the
        // token-content and in-memory identity and must stay disjoint from the EnhancedStatAbility synthetic
        // range (0x1000+).
        HuntersMark         = 74,   // Archer T1
        PinningShot         = 75,   // Archer T2 (the slot Heavy Draw vacated)
        Opportunist         = 76,   // Rogue T1
        KillerInstinct      = 77,   // Rogue T3
        RallyingPresence    = 78,   // Vanguard T1
        KineticCharge       = 79,   // Vanguard T1 as of 2026-09-29 (owner ruling); entered at T2 on
                                    // 2026-09-12 in the slot Shield Check vacated
        ReflectMagic        = 80,   // Vanguard T3. Token/display asymmetry is DELIBERATE: the canonical token
                                    // is "reflect_magic" but it DISPLAYS as "Reflect", per the design table.
        Adrenaline          = 81,   // Berserker T1
        Vengeance           = 82,   // Berserker T2
        QuickenedCasting    = 83,   // Archmage T1
        UmbralSiphon        = 84,   // Void/Summon T1
        SoulJump            = 85,   // Void/Summon T3
        Hemomancy           = 86,   // Blood Mage T1
        Spellweave          = 87,   // Spellsword T1
        RunicWard           = 88,   // Spellsword T2
        CloakedInPower      = 89,   // Vanguard T2 (added 2026-09-29): a floor under the wearer's cloak
                                    // spell-proc chance
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
        /// The legacy trained Skill whose effective value rides this ability's magnitude (its "affinity"),
        /// or NULL when the ability carries no affinity rider at all. Declared here purely as DATA so
        /// tooling - the /abilities readout, the planner catalog under tools/ca-planner - can read an
        /// ability's affinity without parsing C#. It is NOT the mechanic: the rider is still computed by the
        /// handler's own <c>Player.GetClassAbilityScaling(Skill.X, ...)</c> (legacy additive) or
        /// <c>Player.GetClassAbilityAffinityMultiplier(Skill.X, ...)</c> (multiplicative) call, and this
        /// field must name the SAME skill that call passes. Which of the two primitives an ability uses is
        /// invisible here on purpose - the declaration says a rider EXISTS, not how it is computed.
        ///
        /// The two are kept in step by ClassAbilityAffinityDeclarationTests, which scans every handler under
        /// ClassAbilities/Abilities for either call and fails when a declaration disagrees with it. Two
        /// kinds of exception are carried explicitly in that test rather than inferred: handlers whose file
        /// contains a scaling call belonging to a DIFFERENT ability (Parry / Shield Block read each other's
        /// half of their pooled avoidance roll, and Poison Weapon computes Acid Proc's rider), and abilities
        /// whose scaling call lives outside their handler file (Acid Proc, Frenzy).
        ///
        /// The generated Enhanced-stat, Training-bundle and Rating families have no affinity - they read a
        /// skill to BOOST it, which is not a rider on a separate mechanic - so they leave this null.
        ///
        /// An ability with MORE than one rider declares its primary here and the rest in
        /// <see cref="AdditionalAffinitySkills"/>. Read both wherever "which skills feed this ability" is the
        /// question; this field alone is only "the headline one".
        /// </summary>
        public ACE.Entity.Enum.Skill? AffinitySkill;

        /// <summary>
        /// Further legacy skills that ride this ability's magnitude, beyond the primary
        /// <see cref="AffinitySkill"/>. Null or empty for almost everything - most abilities have one rider
        /// or none.
        ///
        /// Empowered Summons is the reason this exists and currently its only user: Leadership scales its pet
        /// stats and Loyalty scales its pet duration (shipped 2026-09-12), and a single field silently told
        /// the planner that Loyalty feeds nothing. Same contract as AffinitySkill - DATA mirroring a real
        /// <c>Player.GetClassAbilityScaling(Skill.X, ...)</c> call somewhere, kept honest by
        /// ClassAbilityAffinityDeclarationTests, which requires every entry here to name the file that
        /// carries its call.
        /// </summary>
        public ACE.Entity.Enum.Skill[] AdditionalAffinitySkills;

        /// <summary>
        /// Total points invested to be at the given rank
        /// </summary>
        public int CumulativeCost(int rank) => CostPerRank.Take(Math.Clamp(rank, 0, MaxRank)).Sum();
    }
}
