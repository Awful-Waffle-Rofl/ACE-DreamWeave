using System.ComponentModel;

namespace ACE.Entity.Enum.Properties
{
    // No properties are sent to the client unless they featured an attribute.
    // SendOnLogin gets sent to players in the PlayerDescription event
    // AssessmentProperty gets sent in successful appraisal
    public enum PropertyFloat : ushort
    {
        Undef                          = 0,
        HeartbeatInterval              = 1,
        [Ephemeral]
        HeartbeatTimestamp             = 2,
        HealthRate                     = 3,
        StaminaRate                    = 4,
        [AssessmentProperty]
        ManaRate                       = 5,
        HealthUponResurrection         = 6,
        StaminaUponResurrection        = 7,
        ManaUponResurrection           = 8,
        StartTime                      = 9,
        StopTime                       = 10,
        ResetInterval                  = 11,
        Shade                          = 12,
        ArmorModVsSlash                = 13,
        ArmorModVsPierce               = 14,
        ArmorModVsBludgeon             = 15,
        ArmorModVsCold                 = 16,
        ArmorModVsFire                 = 17,
        ArmorModVsAcid                 = 18,
        ArmorModVsElectric             = 19,
        CombatSpeed                    = 20,
        WeaponLength                   = 21,
        DamageVariance                 = 22,
        CurrentPowerMod                = 23,
        AccuracyMod                    = 24,
        StrengthMod                    = 25,
        MaximumVelocity                = 26,
        RotationSpeed                  = 27,
        MotionTimestamp                = 28,
        [AssessmentProperty]
        WeaponDefense                  = 29,
        WimpyLevel                     = 30,
        VisualAwarenessRange           = 31,
        AuralAwarenessRange            = 32,
        PerceptionLevel                = 33,
        PowerupTime                    = 34,
        MaxChargeDistance              = 35,
        ChargeSpeed                    = 36,
        BuyPrice                       = 37,
        SellPrice                      = 38,
        DefaultScale                   = 39,
        LockpickMod                    = 40,
        RegenerationInterval           = 41,
        RegenerationTimestamp          = 42,
        GeneratorRadius                = 43,
        TimeToRot                      = 44,
        DeathTimestamp                 = 45,
        PkTimestamp                    = 46,
        VictimTimestamp                = 47,
        LoginTimestamp                 = 48,
        CreationTimestamp              = 49,
        MinimumTimeSincePk             = 50,
        DeprecatedHousekeepingPriority = 51,
        AbuseLoggingTimestamp          = 52,
        [Ephemeral]
        LastPortalTeleportTimestamp    = 53,
        UseRadius                      = 54,
        HomeRadius                     = 55,
        ReleasedTimestamp              = 56,
        MinHomeRadius                  = 57,
        Facing                         = 58,
        [Ephemeral]
        ResetTimestamp                 = 59,
        LogoffTimestamp                = 60,
        EconRecoveryInterval           = 61,
        WeaponOffense                  = 62,
        DamageMod                      = 63,
        ResistSlash                    = 64,
        ResistPierce                   = 65,
        ResistBludgeon                 = 66,
        ResistFire                     = 67,
        ResistCold                     = 68,
        ResistAcid                     = 69,
        ResistElectric                 = 70,
        ResistHealthBoost              = 71,
        ResistStaminaDrain             = 72,
        ResistStaminaBoost             = 73,
        ResistManaDrain                = 74,
        ResistManaBoost                = 75,
        Translucency                   = 76,
        PhysicsScriptIntensity         = 77,
        Friction                       = 78,
        Elasticity                     = 79,
        AiUseMagicDelay                = 80,
        ItemMinSpellcraftMod           = 81,
        ItemMaxSpellcraftMod           = 82,
        ItemRankProbability            = 83,
        Shade2                         = 84,
        Shade3                         = 85,
        Shade4                         = 86,
        [AssessmentProperty]
        ItemEfficiency                 = 87,
        ItemManaUpdateTimestamp        = 88,
        SpellGestureSpeedMod           = 89,
        SpellStanceSpeedMod            = 90,
        AllegianceAppraisalTimestamp   = 91,
        PowerLevel                     = 92,
        AccuracyLevel                  = 93,
        AttackAngle                    = 94,
        AttackTimestamp                = 95,
        CheckpointTimestamp            = 96,
        SoldTimestamp                  = 97,
        UseTimestamp                   = 98,
        [Ephemeral]
        UseLockTimestamp               = 99,
        [AssessmentProperty]
        HealkitMod                     = 100,
        FrozenTimestamp                = 101,
        HealthRateMod                  = 102,
        AllegianceSwearTimestamp       = 103,
        ObviousRadarRange              = 104,
        HotspotCycleTime               = 105,
        HotspotCycleTimeVariance       = 106,
        SpamTimestamp                  = 107,
        SpamRate                       = 108,
        BondWieldedTreasure            = 109,
        BulkMod                        = 110,
        SizeMod                        = 111,
        GagTimestamp                   = 112,
        GeneratorUpdateTimestamp       = 113,
        DeathSpamTimestamp             = 114,
        DeathSpamRate                  = 115,
        WildAttackProbability          = 116,
        FocusedProbability             = 117,
        CrashAndTurnProbability        = 118,
        CrashAndTurnRadius             = 119,
        CrashAndTurnBias               = 120,
        GeneratorInitialDelay          = 121,
        AiAcquireHealth                = 122,
        AiAcquireStamina               = 123,
        AiAcquireMana                  = 124,
        /// <summary>
        /// this had a default of "1" - leaving comment to investigate potential options for defaulting these things (125)
        /// </summary>
        [SendOnLogin]
        ResistHealthDrain              = 125,
        LifestoneProtectionTimestamp   = 126,
        AiCounteractEnchantment        = 127,
        AiDispelEnchantment            = 128,
        TradeTimestamp                 = 129,
        AiTargetedDetectionRadius      = 130,
        EmotePriority                  = 131,
        [Ephemeral]
        LastTeleportStartTimestamp     = 132,
        EventSpamTimestamp             = 133,
        EventSpamRate                  = 134,
        InventoryOffset                = 135,
        [AssessmentProperty]
        CriticalMultiplier             = 136,
        [AssessmentProperty]
        ManaStoneDestroyChance         = 137,
        SlayerDamageBonus              = 138,
        AllegianceInfoSpamTimestamp    = 139,
        AllegianceInfoSpamRate         = 140,
        NextSpellcastTimestamp         = 141,
        [Ephemeral]
        AppraisalRequestedTimestamp    = 142,
        AppraisalHeartbeatDueTimestamp = 143,
        [AssessmentProperty]
        ManaConversionMod              = 144,
        LastPkAttackTimestamp          = 145,
        FellowshipUpdateTimestamp      = 146,
        [AssessmentProperty]
        CriticalFrequency              = 147,
        LimboStartTimestamp            = 148,
        [AssessmentProperty]
        WeaponMissileDefense           = 149,
        [AssessmentProperty]
        WeaponMagicDefense             = 150,
        IgnoreShield                   = 151,
        [AssessmentProperty]
        ElementalDamageMod             = 152,
        StartMissileAttackTimestamp    = 153,
        LastRareUsedTimestamp          = 154,
        [AssessmentProperty]
        IgnoreArmor                    = 155,
        ProcSpellRate                  = 156,
        [AssessmentProperty]
        ResistanceModifier             = 157,
        AllegianceGagTimestamp         = 158,
        [AssessmentProperty]
        AbsorbMagicDamage              = 159,
        CachedMaxAbsorbMagicDamage     = 160,
        GagDuration                    = 161,
        AllegianceGagDuration          = 162,
        [SendOnLogin]
        GlobalXpMod                    = 163,
        HealingModifier                = 164,
        ArmorModVsNether               = 165,
        ResistNether                   = 166,
        [AssessmentProperty]
        CooldownDuration               = 167,
        [SendOnLogin]
        WeaponAuraOffense              = 168,
        [SendOnLogin]
        WeaponAuraDefense              = 169,
        [SendOnLogin]
        WeaponAuraElemental            = 170,
        [SendOnLogin]
        WeaponAuraManaConv             = 171,

        /* Custom Properties */
        PCAPRecordedWorkmanship        = 8004,
        PCAPRecordedVelocityX          = 8010,
        PCAPRecordedVelocityY          = 8011,
        PCAPRecordedVelocityZ          = 8012,
        PCAPRecordedAccelerationX      = 8013,
        PCAPRecordedAccelerationY      = 8014,
        PCAPRecordedAccelerationZ      = 8015,
        PCAPRecordeOmegaX              = 8016,
        PCAPRecordeOmegaY              = 8017,
        PCAPRecordeOmegaZ              = 8018,
        // PROTOTYPE: multi-shot missile weapon tuning - see WorldObject_Weapon.MultiShotSpreadAngle / MultiShotDamageMultiplier
        MultiShotSpreadAngle           = 8019,
        MultiShotDamageMultiplier      = 8020,

        /* Equipment mods (WaffleACE) - the band 8100-8199 is RESERVED WHOLESALE for equipment mods.
         * One id per mod type; append only, never renumber (live item rows carry these ids).
         *
         * Each value stored on an item is a POTENCY SCALAR in [0, 1], NOT the mod's final magnitude.
         * The magnitude is resolved at read time as potency x EquipmentModRegistry max x the
         * equipment_mod_potency_scale tunable, so rebalancing a mod rescales every existing item in
         * the world instantly - no shard migration, and the clamp is trivially [0, 1].
         *
         * Deliberately NO [AssessmentProperty] and NO [SendOnLogin]: a raw potency scalar is
         * meaningless to the client and must never be sent. Display goes through the
         * "Property Details:" block in AppraiseInfo, which renders the resolved magnitude instead.
         * See ACE.Server.EquipmentMods.EquipmentModRegistry for the one-row-per-mod table. */
        GearModDeadeye                 = 8100,
        GearModEagleEye                = 8101,
        GearModLongDraw                = 8102,
        GearModHeavyDraw               = 8103,
        GearModSplitshot               = 8104,
        GearModDoubleVolley            = 8105,
        GearModVenom                   = 8106,
        GearModAcidProc                = 8107,
        GearModCaustic                 = 8108,
        GearModRiposte                 = 8109,
        GearModAttackSpeed             = 8110,
        GearModThorns                  = 8111,
        GearModShieldCheck             = 8112,
        GearModBulwark                 = 8113,
        GearModFrenziedPace            = 8114,
        GearModLingeringFury           = 8115,
        GearModSavageBlows             = 8116,
        GearModBloodFury               = 8117,
        GearModExecutioner             = 8118,
        GearModBloodlust               = 8119,
        GearModOverchannel             = 8120,
        GearModEchoCast                = 8121,
        GearModElementalRend           = 8122,
        GearModResonance               = 8123,
        GearModVoidDamage              = 8124,
        GearModWithering               = 8125,
        GearModEmpoweredSummons        = 8126,
        GearModManaBarrier             = 8127,
        GearModNetherBloom             = 8128,
        GearModSoulTether              = 8129,

        /* Equipment mods, class-catalog reconciliation (WaffleACE, 2026-08-04) - closes the gap for
         * BloodMage and Spellsword (zero mods each) plus the Vanguard Provoke/Bellow/Shield Wall
         * correction. All 19 are machinery: every linked ability returns 0 or the identity value at rank 0,
         * so none of them has a rank-0 formula for a standalone term to stand alone in
         * (Docs/EquipmentMods/DESIGN.md section 2.3). 8168 (GearModShieldWall) shipped last, once its
         * prerequisite landed: ShieldBlockAbility.BlockChance now clamps its Armor Tinkering rider with
         * class_ability_affinity_chance_cap, so the pooled avoidance cap no longer absorbs the mod. */
        GearModBloodCharge             = 8150,
        GearModTransfusion             = 8151,
        GearModHemorrhage              = 8152,
        GearModDeepen                  = 8153,
        GearModBloodletting            = 8154,
        GearModSanguinate              = 8155,
        GearModBloodPrice              = 8156,
        GearModClotting                = 8157,
        GearModSpellblade              = 8158,
        GearModHarmonics               = 8159,
        GearModRuneblade               = 8160,
        GearModSundermark              = 8161,
        GearModSurge                   = 8162,
        GearModSpellstorm              = 8163,
        GearModCascade                 = 8164,
        GearModDispellingEdge          = 8165,
        GearModProvoke                 = 8166,
        GearModBellow                  = 8167,
        GearModShieldWall              = 8168,

        /* Weapon mods (WaffleACE) - Tier A special modifiers, split out of the 8100-8199 band.
         *
         * DELIBERATELY UNLIKE the GearMod* ids above: each of these stores the APPLIED MAGNITUDE that
         * this system added to a native property, NOT a potency scalar. Tier A writes to native
         * properties (the Gear* ratings, Cleaving, IgnoreShield, MaximumVelocity) that may already
         * carry a loot-generated value, so reversal has to subtract exactly what was added. A potency
         * scalar would go wrong the moment weapon_mod_magnitude_scale moved between application and
         * reversal, and the native property would drift permanently.
         *
         * Deliberately NO [AssessmentProperty] and NO [SendOnLogin]: these are bookkeeping rows for the
         * reversal arithmetic and must never reach the client. Display goes through the
         * "Property Details:" block in AppraiseInfo.
         * See ACE.Server.WeaponMods.WeaponModRegistry for the one-row-per-modifier table.
         *
         * 8136-8140 ARE RETIRED AND MUST NEVER BE REUSED. They held WeaponModWarding, WeaponModCritWard,
         * WeaponModResolute, WeaponModVigor and WeaponModMending until 2026-07-30, when the Tier A pool was
         * cut to damage-oriented modifiers only and those five defensive/sustain rows were removed. The
         * members are gone rather than renamed so nothing can write them again, but weapons on dev shards
         * still carry records at those ids. Registered as RETIRED_DO_NOT_REUSE in Source/property-registry.tsv
         * and guarded by WaveChallengePropertyTests.PropertyFloat_8136_To_8140_StayUnallocated. */
        WeaponModDevastation           = 8130,
        WeaponModWeakPoint             = 8131,
        WeaponModBloodthirst           = 8132,
        WeaponModCleave                = 8133,
        WeaponModShieldBypass          = 8134,
        WeaponModSwiftFlight           = 8135,

        /* Weapon mods (WaffleACE) - Tier B special modifiers, v2.
         *
         * STORAGE IS SIMPLER HERE THAN IN TIER A, AND THE DIFFERENCE MATTERS. Tier A stores an applied
         * magnitude because it WRITES a native property that has to be reversed exactly. Tier B writes no
         * native property at all - every effect is read live at combat time off the equipped weapon - so
         * these ids hold the applied magnitude as a FRACTION (0.04 = 4%, 0.15 = 15%), reversal is a bare
         * RemoveProperty, and none of Tier A's subtract-what-you-added machinery applies.
         *
         * Same client rules as Tier A: no [AssessmentProperty], no [SendOnLogin]. Display goes through the
         * "Property Details:" block in AppraiseInfo.
         *
         * 8148 and 8149 are RESERVED for Sunder and Rampage, which are phase 2 and deliberately have no
         * enum member yet: Sunder needs a new enchantment/spell row and Rampage needs per-target stack
         * state, neither of which exists. They are registered as reserved in Source/property-registry.tsv
         * so nothing else can take them in the meantime. */
        WeaponModLifeLeech             = 8141,
        WeaponModManaLeech             = 8142,
        WeaponModStaminaLeech          = 8143,
        WeaponModAmbush                = 8144,
        WeaponModQuickening            = 8145,
        WeaponModOverload              = 8146,
        WeaponModSecondWind            = 8147,

        /* Offline experience bonus (WaffleACE) */
        // Banked "offline bonus" time, in seconds, for this character. Accrues 1:1 with time spent
        // offline (capped by offline_bonus_max_seconds), drains 1:1 with time spent online. While
        // this is > 0, the character's own combat XP/Luminance is boosted by offline_bonus_multiplier.
        OfflineExperienceBonusRemaining = 9000,

        /* Survival challenge (WaffleACE) */
        // On a survival-challenge portal weenie: the per-tier multiplier applied in-place to arena creatures' raw
        // power - their six attributes and combat skills - each escalation tier. Because attribute/skill values feed
        // the standard combat formulas (the melee attribute-damage mod, evade checks, and spell-resist checks), this
        // ramps the creatures' accuracy, resist penetration and melee attribute damage through the native flows.
        // Default 1.3 when absent. (Guaranteed damage growth for both melee and spells rides SurvivalChallenge
        // RatingPerTier instead, since monster war-spell base damage is flat and does not scale with caster stats.)
        SurvivalChallengeRampRate       = 9001,
        // On a survival-challenge portal weenie: the flat DamageRating (PropertyInt 307) increment added in-place to
        // every arena creature each tier. DamageRating feeds the standard (100 + rating)/100 rating mod in BOTH the
        // melee DamageEvent path and the spell-projectile damage path, so this is the uniform per-tier damage lever
        // for zefir melee and wisp bolts alike. Additive: at +N/tier the damage factor is (100 + N*tier)/100 (linear
        // in tier, not exponential). Default 15 when absent.
        SurvivalChallengeRatingPerTier  = 9002,

        // 9003 is deliberately left unused - it was allocated and then retired, and stale rows may still exist
        // in shipped content. Do not reuse it.

        /* Wave challenge (WaffleACE) */
        // On a wave-challenge portal weenie: the breathing room, in seconds, between one wave being fully cleared
        // and the next wave spawning. Default 10 when absent.
        WaveChallengeInterWaveDelay     = 9004,
        // On a wave-challenge portal weenie: how many seconds of ZERO damage dealt to the live wave end the run as
        // stalled (scoring the last fully-cleared wave). Damage-based rather than an absolute per-wave time limit,
        // because a late boss wave legitimately takes tens of minutes to grind down. 0 disables the watchdog
        // entirely. Default 150 when absent.
        WaveChallengeStallTimeout       = 9005,
        // On a wave-challenge portal weenie: how many seconds a single wave may stay alive before the run ends,
        // scoring the last fully-cleared wave. Unlike WaveChallengeStallTimeout this is an ABSOLUTE limit - it
        // expires even while the player is landing damage - and the two watchdogs run side by side: the stall
        // timeout catches an idle player, this catches a wave that is being fought but cannot be finished. The
        // player is warned at 2 minutes, 1 minute and 30 seconds remaining. 0 or less disables it entirely.
        // Default 300 when absent.
        WaveChallengeWaveTimeLimit      = 9006,
    }
}
