using System;
using System.ComponentModel;
using System.Globalization;

namespace ACE.Entity.Enum.Properties
{
    // No properties are sent to the client unless they featured an attribute.
    // SendOnLogin gets sent to players in the PlayerDescription event
    // AssessmentProperty gets sent in successful appraisal
    public enum PropertyInt : ushort
    {
        Undef                                    = 0,
        ItemType                                 = 1,
        [AssessmentProperty]
        CreatureType                             = 2,
        PaletteTemplate                          = 3,
        ClothingPriority                         = 4,
        [AssessmentProperty][SendOnLogin]
        EncumbranceVal                           = 5, // ENCUMB_VAL_INT,
        ItemsCapacity                            = 6,
        [SendOnLogin]
        ContainersCapacity                       = 7,
        Mass                                     = 8,
        ValidLocations                           = 9, // LOCATIONS_INT,
        CurrentWieldedLocation                   = 10,
        MaxStackSize                             = 11,
        StackSize                                = 12,
        StackUnitEncumbrance                     = 13,
        StackUnitMass                            = 14,
        StackUnitValue                           = 15,
        ItemUseable                              = 16,
        [AssessmentProperty]
        RareId                                   = 17,
        UiEffects                                = 18,
        [AssessmentProperty]
        Value                                    = 19,
        [SendOnLogin][Ephemeral]
        CoinValue                                = 20,
        TotalExperience                          = 21,
        AvailableCharacter                       = 22,
        TotalSkillCredits                        = 23,
        [SendOnLogin]
        AvailableSkillCredits                    = 24,
        [SendOnLogin][AssessmentProperty]
        Level                                    = 25,
        [AssessmentProperty]
        AccountRequirements                      = 26,
        ArmorType                                = 27,
        [AssessmentProperty]
        ArmorLevel                               = 28,
        AllegianceCpPool                         = 29,
        [SendOnLogin][AssessmentProperty]
        AllegianceRank                           = 30,
        ChannelsAllowed                          = 31,
        ChannelsActive                           = 32,
        [AssessmentProperty]
        Bonded                                   = 33,
        MonarchsRank                             = 34,
        [AssessmentProperty]
        AllegianceFollowers                      = 35,
        [AssessmentProperty]
        ResistMagic                              = 36,
        ResistItemAppraisal                      = 37,
        [AssessmentProperty]
        ResistLockpick                           = 38,
        DeprecatedResistRepair                   = 39,
        [SendOnLogin]
        CombatMode                               = 40,
        CurrentAttackHeight                      = 41,
        CombatCollisions                         = 42,
        [SendOnLogin][AssessmentProperty]
        NumDeaths                                = 43,
        Damage                                   = 44,
        [AssessmentProperty]
        DamageType                               = 45,
        DefaultCombatStyle                       = 46,
        [SendOnLogin][AssessmentProperty]
        AttackType                               = 47,
        WeaponSkill                              = 48,
        WeaponTime                               = 49,
        AmmoType                                 = 50,
        CombatUse                                = 51,
        ParentLocation                           = 52,
        /// <summary>
        /// TODO: Migrate inventory order away from this and instead use the new InventoryOrder property
        /// TODO: PlacementPosition is used (very sparingly) in cache.bin, so it has (or had) a meaning at one point before we hijacked it
        /// TODO: and used it for our own inventory order
        /// </summary>
        PlacementPosition                        = 53,
        WeaponEncumbrance                        = 54,
        WeaponMass                               = 55,
        ShieldValue                              = 56,
        ShieldEncumbrance                        = 57,
        MissileInventoryLocation                 = 58,
        FullDamageType                           = 59,
        WeaponRange                              = 60,
        AttackersSkill                           = 61,
        DefendersSkill                           = 62,
        AttackersSkillValue                      = 63,
        AttackersClass                           = 64,
        Placement                                = 65,
        CheckpointStatus                         = 66,
        Tolerance                                = 67,
        TargetingTactic                          = 68,
        CombatTactic                             = 69,
        HomesickTargetingTactic                  = 70,
        NumFollowFailures                        = 71,
        FriendType                               = 72,
        FoeType                                  = 73,
        MerchandiseItemTypes                     = 74,
        MerchandiseMinValue                      = 75,
        MerchandiseMaxValue                      = 76,
        NumItemsSold                             = 77,
        NumItemsBought                           = 78,
        MoneyIncome                              = 79,
        MoneyOutflow                             = 80,
        [Ephemeral]
        MaxGeneratedObjects                      = 81,
        [Ephemeral]
        InitGeneratedObjects                     = 82,
        ActivationResponse                       = 83,
        OriginalValue                            = 84,
        NumMoveFailures                          = 85,
        [AssessmentProperty]
        MinLevel                                 = 86,
        [AssessmentProperty]
        MaxLevel                                 = 87,
        LockpickMod                              = 88,
        [AssessmentProperty]
        BoosterEnum                              = 89,
        [AssessmentProperty]
        BoostValue                               = 90,
        [AssessmentProperty]
        MaxStructure                             = 91,
        [AssessmentProperty]
        Structure                                = 92,
        PhysicsState                             = 93,
        TargetType                               = 94,
        RadarBlipColor                           = 95,
        EncumbranceCapacity                      = 96,
        LoginTimestamp                           = 97,
        [SendOnLogin][AssessmentProperty]
        CreationTimestamp                        = 98,
        PkLevelModifier                          = 99,
        GeneratorType                            = 100,
        AiAllowedCombatStyle                     = 101,
        LogoffTimestamp                          = 102,
        GeneratorDestructionType                 = 103,
        ActivationCreateClass                    = 104,
        [AssessmentProperty]
        ItemWorkmanship                          = 105,
        [AssessmentProperty]
        ItemSpellcraft                           = 106,
        [AssessmentProperty]
        ItemCurMana                              = 107,
        [AssessmentProperty]
        ItemMaxMana                              = 108,
        [AssessmentProperty]
        ItemDifficulty                           = 109,
        [AssessmentProperty]
        ItemAllegianceRankLimit                  = 110,
        [AssessmentProperty]
        PortalBitmask                            = 111,
        AdvocateLevel                            = 112,
        [SendOnLogin][AssessmentProperty]
        Gender                                   = 113,
        [AssessmentProperty]
        Attuned                                  = 114,
        [AssessmentProperty]
        ItemSkillLevelLimit                      = 115,
        GateLogic                                = 116,
        [AssessmentProperty]
        ItemManaCost                             = 117,
        Logoff                                   = 118,
        Active                                   = 119,
        AttackHeight                             = 120,
        NumAttackFailures                        = 121,
        AiCpThreshold                            = 122,
        AiAdvancementStrategy                    = 123,
        Version                                  = 124,
        [SendOnLogin][AssessmentProperty]
        Age                                      = 125,
        VendorHappyMean                          = 126,
        VendorHappyVariance                      = 127,
        CloakStatus                              = 128,
        [SendOnLogin]
        VitaeCpPool                              = 129,
        NumServicesSold                          = 130,
        [AssessmentProperty]
        MaterialType                             = 131,
        [SendOnLogin]
        NumAllegianceBreaks                      = 132,
        [Ephemeral]
        ShowableOnRadar                          = 133,
        [SendOnLogin][AssessmentProperty]
        PlayerKillerStatus                       = 134,
        VendorHappyMaxItems                      = 135,
        ScorePageNum                             = 136,
        ScoreConfigNum                           = 137,
        ScoreNumScores                           = 138,
        [SendOnLogin]
        DeathLevel                               = 139,
        AiOptions                                = 140,
        OpenToEveryone                           = 141,
        GeneratorTimeType                        = 142,
        GeneratorStartTime                       = 143,
        GeneratorEndTime                         = 144,
        GeneratorEndDestructionType              = 145,
        XpOverride                               = 146,
        NumCrashAndTurns                         = 147,
        ComponentWarningThreshold                = 148,
        HouseStatus                              = 149,
        HookPlacement                            = 150,
        HookType                                 = 151,
        HookItemType                             = 152,
        AiPpThreshold                            = 153,
        GeneratorVersion                         = 154,
        HouseType                                = 155,
        PickupEmoteOffset                        = 156,
        WeenieIteration                          = 157,
        [AssessmentProperty]
        WieldRequirements                        = 158,
        [AssessmentProperty]
        WieldSkillType                           = 159,
        [AssessmentProperty]
        WieldDifficulty                          = 160,
        HouseMaxHooksUsable                      = 161,
        [Ephemeral]
        HouseCurrentHooksUsable                  = 162,
        AllegianceMinLevel                       = 163,
        AllegianceMaxLevel                       = 164,
        HouseRelinkHookCount                     = 165,
        [AssessmentProperty]
        SlayerCreatureType                       = 166,
        ConfirmationInProgress                   = 167,
        ConfirmationTypeInProgress               = 168,
        TsysMutationData                         = 169,
        [AssessmentProperty]
        NumItemsInMaterial                       = 170,
        [AssessmentProperty]
        NumTimesTinkered                         = 171,
        [AssessmentProperty]
        AppraisalLongDescDecoration              = 172,
        [AssessmentProperty]
        AppraisalLockpickSuccessPercent          = 173,
        [AssessmentProperty][Ephemeral]
        AppraisalPages                           = 174,
        [AssessmentProperty][Ephemeral]
        AppraisalMaxPages                        = 175,
        [AssessmentProperty]
        AppraisalItemSkill                       = 176,
        [AssessmentProperty]
        GemCount                                 = 177,
        [AssessmentProperty]
        GemType                                  = 178,
        [AssessmentProperty]
        ImbuedEffect                             = 179,
        AttackersRawSkillValue                   = 180,
        [SendOnLogin][AssessmentProperty]
        ChessRank                                = 181,
        ChessTotalGames                          = 182,
        ChessGamesWon                            = 183,
        ChessGamesLost                           = 184,
        TypeOfAlteration                         = 185,
        SkillToBeAltered                         = 186,
        SkillAlterationCount                     = 187,
        [SendOnLogin][AssessmentProperty]
        HeritageGroup                            = 188,
        TransferFromAttribute                    = 189,
        TransferToAttribute                      = 190,
        AttributeTransferCount                   = 191,
        [SendOnLogin][AssessmentProperty]
        FakeFishingSkill                         = 192,
        [AssessmentProperty]
        NumKeys                                  = 193,
        DeathTimestamp                           = 194,
        PkTimestamp                              = 195,
        VictimTimestamp                          = 196,
        HookGroup                                = 197,
        AllegianceSwearTimestamp                 = 198,
        [SendOnLogin]
        HousePurchaseTimestamp                   = 199,
        RedirectableEquippedArmorCount           = 200,
        MeleeDefenseImbuedEffectTypeCache        = 201,
        MissileDefenseImbuedEffectTypeCache      = 202,
        MagicDefenseImbuedEffectTypeCache        = 203,
        [AssessmentProperty]
        ElementalDamageBonus                     = 204,
        ImbueAttempts                            = 205,
        ImbueSuccesses                           = 206,
        CreatureKills                            = 207,
        PlayerKillsPk                            = 208,
        PlayerKillsPkl                           = 209,
        RaresTierOne                             = 210,
        RaresTierTwo                             = 211,
        RaresTierThree                           = 212,
        RaresTierFour                            = 213,
        RaresTierFive                            = 214,
        AugmentationStat                         = 215,
        AugmentationFamilyStat                   = 216,
        AugmentationInnateFamily                 = 217,
        [SendOnLogin]
        AugmentationInnateStrength               = 218,
        [SendOnLogin]
        AugmentationInnateEndurance              = 219,
        [SendOnLogin]
        AugmentationInnateCoordination           = 220,
        [SendOnLogin]
        AugmentationInnateQuickness              = 221,
        [SendOnLogin]
        AugmentationInnateFocus                  = 222,
        [SendOnLogin]
        AugmentationInnateSelf                   = 223,
        [SendOnLogin]
        AugmentationSpecializeSalvaging          = 224,
        [SendOnLogin]
        AugmentationSpecializeItemTinkering      = 225,
        [SendOnLogin]
        AugmentationSpecializeArmorTinkering     = 226,
        [SendOnLogin]
        AugmentationSpecializeMagicItemTinkering = 227,
        [SendOnLogin]
        AugmentationSpecializeWeaponTinkering    = 228,
        [SendOnLogin]
        AugmentationExtraPackSlot                = 229,
        [SendOnLogin]
        AugmentationIncreasedCarryingCapacity    = 230,
        [SendOnLogin]
        AugmentationLessDeathItemLoss            = 231,
        [SendOnLogin]
        AugmentationSpellsRemainPastDeath        = 232,
        [SendOnLogin]
        AugmentationCriticalDefense              = 233,
        [SendOnLogin]
        AugmentationBonusXp                      = 234,
        [SendOnLogin]
        AugmentationBonusSalvage                 = 235,
        [SendOnLogin]
        AugmentationBonusImbueChance             = 236,
        [SendOnLogin]
        AugmentationFasterRegen                  = 237,
        [SendOnLogin]
        AugmentationIncreasedSpellDuration       = 238,
        AugmentationResistanceFamily             = 239,
        [SendOnLogin]
        AugmentationResistanceSlash              = 240,
        [SendOnLogin]
        AugmentationResistancePierce             = 241,
        [SendOnLogin]
        AugmentationResistanceBlunt              = 242,
        [SendOnLogin]
        AugmentationResistanceAcid               = 243,
        [SendOnLogin]
        AugmentationResistanceFire               = 244,
        [SendOnLogin]
        AugmentationResistanceFrost              = 245,
        [SendOnLogin]
        AugmentationResistanceLightning          = 246,
        RaresTierOneLogin                        = 247,
        RaresTierTwoLogin                        = 248,
        RaresTierThreeLogin                      = 249,
        RaresTierFourLogin                       = 250,
        RaresTierFiveLogin                       = 251,
        RaresLoginTimestamp                      = 252,
        RaresTierSix                             = 253,
        RaresTierSeven                           = 254,
        RaresTierSixLogin                        = 255,
        RaresTierSevenLogin                      = 256,
        [AssessmentProperty]
        ItemAttributeLimit                       = 257,
        [AssessmentProperty]
        ItemAttributeLevelLimit                  = 258,
        [AssessmentProperty]
        ItemAttribute2ndLimit                    = 259,
        [AssessmentProperty]
        ItemAttribute2ndLevelLimit               = 260,
        [AssessmentProperty]
        CharacterTitleId                         = 261,
        [AssessmentProperty]
        NumCharacterTitles                       = 262,
        [AssessmentProperty]
        ResistanceModifierType                   = 263,
        FreeTinkersBitfield                      = 264,
        [AssessmentProperty]
        EquipmentSetId                           = 265,
        PetClass                                 = 266,
        [AssessmentProperty]
        Lifespan                                 = 267,
        [AssessmentProperty][Ephemeral]
        RemainingLifespan                        = 268,
        UseCreateQuantity                        = 269,
        [AssessmentProperty]
        WieldRequirements2                       = 270,
        [AssessmentProperty]
        WieldSkillType2                          = 271,
        [AssessmentProperty]
        WieldDifficulty2                         = 272,
        [AssessmentProperty]
        WieldRequirements3                       = 273,
        [AssessmentProperty]
        WieldSkillType3                          = 274,
        [AssessmentProperty]
        WieldDifficulty3                         = 275,
        [AssessmentProperty]
        WieldRequirements4                       = 276,
        [AssessmentProperty]
        WieldSkillType4                          = 277,
        [AssessmentProperty]
        WieldDifficulty4                         = 278,
        [AssessmentProperty]
        Unique                                   = 279,
        [AssessmentProperty]
        SharedCooldown                           = 280,
        [SendOnLogin][AssessmentProperty]
        Faction1Bits                             = 281,
        Faction2Bits                             = 282,
        Faction3Bits                             = 283,
        Hatred1Bits                              = 284,
        Hatred2Bits                              = 285,
        Hatred3Bits                              = 286,
        [SendOnLogin][AssessmentProperty]
        SocietyRankCelhan                        = 287,
        [SendOnLogin][AssessmentProperty]
        SocietyRankEldweb                        = 288,
        [SendOnLogin][AssessmentProperty]
        SocietyRankRadblo                        = 289,
        HearLocalSignals                         = 290,
        HearLocalSignalsRadius                   = 291,
        [AssessmentProperty]
        Cleaving                                 = 292,
        AugmentationSpecializeGearcraft          = 293,
        [SendOnLogin]
        AugmentationInfusedCreatureMagic         = 294,
        [SendOnLogin]
        AugmentationInfusedItemMagic             = 295,
        [SendOnLogin]
        AugmentationInfusedLifeMagic             = 296,
        [SendOnLogin]
        AugmentationInfusedWarMagic              = 297,
        [SendOnLogin]
        AugmentationCriticalExpertise            = 298,
        [SendOnLogin]
        AugmentationCriticalPower                = 299,
        [SendOnLogin]
        AugmentationSkilledMelee                 = 300,
        [SendOnLogin]
        AugmentationSkilledMissile               = 301,
        [SendOnLogin]
        AugmentationSkilledMagic                 = 302,
        [AssessmentProperty]
        ImbuedEffect2                            = 303,
        [AssessmentProperty]
        ImbuedEffect3                            = 304,
        [AssessmentProperty]
        ImbuedEffect4                            = 305,
        [AssessmentProperty]
        ImbuedEffect5                            = 306,
        [SendOnLogin][AssessmentProperty]
        DamageRating                             = 307,
        [SendOnLogin][AssessmentProperty]
        DamageResistRating                       = 308,
        [SendOnLogin]
        AugmentationDamageBonus                  = 309,
        [SendOnLogin]
        AugmentationDamageReduction              = 310,
        /// <summary>
        /// Used for crafting / tinkering augmentation stacking
        ///
        /// https://acpedia.org/wiki/Weapon_Augmentations#Weapon_Augmentation_Stacking
        /// https://acpedia.org/wiki/Armor_Augmentations
        /// https://asheron.fandom.com/wiki/Category:Upgrade_Items#Weapon_Augmentation_Stacking
        /// 
        /// 1  == Class B weapon augmentations
        /// 2  == Class A weapon augmentations
        /// 4  == Class C weapon augmentations / Gauntlet Amplifications
        /// 8  == Corrupted Amber weapon augmentations
        /// 16 == Purchased Armor Augmentations
        ///
        /// </summary>
        ImbueStackingBits                        = 311,
        [SendOnLogin]
        HealOverTime                             = 312,
        [SendOnLogin][AssessmentProperty]
        CritRating                               = 313,
        [SendOnLogin][AssessmentProperty]
        CritDamageRating                         = 314,
        [SendOnLogin][AssessmentProperty]
        CritResistRating                         = 315,
        [SendOnLogin][AssessmentProperty]
        CritDamageResistRating                   = 316,
        [SendOnLogin]
        HealingResistRating                      = 317,
        [SendOnLogin]
        DamageOverTime                           = 318,
        [AssessmentProperty]
        ItemMaxLevel                             = 319,
        [AssessmentProperty]
        ItemXpStyle                              = 320,
        EquipmentSetExtra                        = 321,
        [SendOnLogin]
        AetheriaBitfield                         = 322,
        [SendOnLogin][AssessmentProperty]
        HealingBoostRating                       = 323,
        [AssessmentProperty]
        HeritageSpecificArmor                    = 324,
        AlternateRacialSkills                    = 325,
        [SendOnLogin]
        AugmentationJackOfAllTrades              = 326,
        AugmentationResistanceNether             = 327,
        [SendOnLogin]
        AugmentationInfusedVoidMagic             = 328,
        [SendOnLogin]
        WeaknessRating                           = 329,
        [SendOnLogin]
        NetherOverTime                           = 330,
        [SendOnLogin]
        NetherResistRating                       = 331,
        LuminanceAward                           = 332,
        [SendOnLogin]
        LumAugDamageRating                       = 333,
        [SendOnLogin]
        LumAugDamageReductionRating              = 334,
        [SendOnLogin]
        LumAugCritDamageRating                   = 335,
        [SendOnLogin]
        LumAugCritReductionRating                = 336,
        [SendOnLogin]
        LumAugSurgeEffectRating                  = 337,
        [SendOnLogin]
        LumAugSurgeChanceRating                  = 338,
        [SendOnLogin]
        LumAugItemManaUsage                      = 339,
        [SendOnLogin]
        LumAugItemManaGain                       = 340,
        [SendOnLogin]
        LumAugVitality                           = 341,
        [SendOnLogin]
        LumAugHealingRating                      = 342,
        [SendOnLogin]
        LumAugSkilledCraft                       = 343,
        [SendOnLogin]
        LumAugSkilledSpec                        = 344,
        LumAugNoDestroyCraft                     = 345,
        RestrictInteraction                      = 346,
        [SendOnLogin]
        OlthoiLootTimestamp                      = 347,
        OlthoiLootStep                           = 348,
        UseCreatesContractId                     = 349,
        [SendOnLogin][AssessmentProperty]
        DotResistRating                          = 350,
        [SendOnLogin][AssessmentProperty]
        LifeResistRating                         = 351,
        [AssessmentProperty]
        CloakWeaveProc                           = 352,
        [AssessmentProperty]
        WeaponType                               = 353,
        [SendOnLogin]
        MeleeMastery                             = 354,
        [SendOnLogin]
        RangedMastery                            = 355,
        SneakAttackRating                        = 356,
        RecklessnessRating                       = 357,
        DeceptionRating                          = 358,
        CombatPetRange                           = 359,
        [SendOnLogin]
        WeaponAuraDamage                         = 360,
        [SendOnLogin]
        WeaponAuraSpeed                          = 361,
        [SendOnLogin]
        SummoningMastery                         = 362,
        HeartbeatLifespan                        = 363,
        UseLevelRequirement                      = 364,
        [SendOnLogin]
        LumAugAllSkills                          = 365,
        [AssessmentProperty]
        UseRequiresSkill                         = 366,
        [AssessmentProperty]
        UseRequiresSkillLevel                    = 367,
        [AssessmentProperty]
        UseRequiresSkillSpec                     = 368,
        [AssessmentProperty]
        UseRequiresLevel                         = 369,
        [SendOnLogin][AssessmentProperty]
        GearDamage                               = 370,
        [SendOnLogin][AssessmentProperty]
        GearDamageResist                         = 371,
        [SendOnLogin][AssessmentProperty]
        GearCrit                                 = 372,
        [SendOnLogin][AssessmentProperty]
        GearCritResist                           = 373,
        [SendOnLogin][AssessmentProperty]
        GearCritDamage                           = 374,
        [SendOnLogin][AssessmentProperty]
        GearCritDamageResist                     = 375,
        [SendOnLogin][AssessmentProperty]
        GearHealingBoost                         = 376,
        [SendOnLogin][AssessmentProperty]
        GearNetherResist                         = 377,
        [SendOnLogin][AssessmentProperty]
        GearLifeResist                           = 378,
        [SendOnLogin][AssessmentProperty]
        GearMaxHealth                            = 379,
        Unknown380                               = 380,
        [SendOnLogin][AssessmentProperty]
        PKDamageRating                           = 381,
        [SendOnLogin][AssessmentProperty]
        PKDamageResistRating                     = 382,
        [SendOnLogin][AssessmentProperty]
        GearPKDamageRating                       = 383,
        [SendOnLogin][AssessmentProperty]
        GearPKDamageResistRating                 = 384,
        Unknown385                               = 385,
        /// <summary>
        /// Overpower chance % for endgame creatures.
        /// </summary>
        [SendOnLogin][AssessmentProperty]
        Overpower                                = 386,
        [SendOnLogin][AssessmentProperty]
        OverpowerResist                          = 387,
        // Client does not display accurately
        [SendOnLogin][AssessmentProperty]
        GearOverpower                            = 388,
        // Client does not display accurately
        [SendOnLogin][AssessmentProperty]
        GearOverpowerResist                      = 389,
        // Number of times a character has enlightened. RETIRED 2026-08-08 (XP-LANE-SPEC sec 4): the count
        // is frozen and no longer shown. SendOnLogin/AssessmentProperty were REMOVED deliberately and must
        // not be restored casually - the client adds Enlightenment * 2 to its own max-health formula when
        // it receives this, so re-sending it without also restoring the matching correction in
        // Player_Vitals inflates every affected health bar by 2 per enlightenment.
        //
        // The property itself STAYS on the biota: it is the input to the retirement credit
        // (Player.GrantEnlightenmentRetirementCredit, whose idempotence depends on re-reading it) and to
        // the grandfathered +1/enlightenment stat floor in CreatureSkill/CreatureAttribute. Never zero it.
        Enlightenment                            = 390,

        /* Custom Properties */
        PCAPRecordedAutonomousMovement           = 8007,
        PCAPRecordedMaxVelocityEstimated         = 8030,
        PCAPRecordedPlacement                    = 8041,
        PCAPRecordedAppraisalPages               = 8042,
        PCAPRecordedAppraisalMaxPages            = 8043,

        // TotalLogins                           = 9001,
        // DeletionTimestamp                     = 9002,
        // CharacterOptions1                     = 9003,
        // CharacterOptions2                     = 9004,
        // LootTier                              = 9005,
        // GeneratorProbability                  = 9006,
        // WeenieType                            = 9007
        CurrentLoyaltyAtLastLogoff               = 9008,
        CurrentLeadershipAtLastLogoff            = 9009,
        AllegianceOfficerRank                    = 9010,
        HouseRentTimestamp                       = 9011,
        Hairstyle                                = 9012,
        [Ephemeral]
        VisualClothingPriority                   = 9013,
        SquelchGlobal                            = 9014,
        InventoryOrder                           = 9015,
        // PROTOTYPE: number of additional targets for a multi-shot missile weapon - see WorldObject_Weapon.MultiShotCount
        MultiShotCount                           = 9016,
        // Class ability system (see ACE.Server.Entity.ClassAbilityRegistry) - point balances on the player biota
        AvailableClassAbilityPoints                = 9017,
        TotalClassAbilityPointsEarned              = 9018,
        ClassAbilityPointsPurchasedWithLum         = 9019,
        // on a consumable item (Gem): class ability points granted when used
        ClassAbilityPointValue                     = 9020,
        // on a consumable class-ability token (Gem): the class ability it teaches (value = ClassAbilityId)
        // (9021/9022 are taken by PortalInstancing/PortalRealm below - stay in the free 9023+ range)
        ClassAbilityTokenId                        = 9023,
        // on a consumable class-ability token (Gem): the exact rank/tier it teaches (1..MaxRank);
        // using it learns that rank (must be current rank + 1) and spends the normal point cost
        ClassAbilityTokenTier                      = 9024,
        // on the player biota: how many level-milestone class ability points have been paid out so far
        // (DESIGN.md sec 2a). Idempotent catch-up compares this to the entitlement derived from Level,
        // so the grant is retroactive-safe for existing characters and immune to missed level-ups.
        // LIFETIME total - must NEVER be reset (incl. by enlightenment, which sets Level back to 1): a
        // re-leveling character then computes owed = entitled - this <= 0 and is never paid a milestone
        // point twice. See Player.GrantMilestoneClassAbilityPoints.
        MilestoneClassAbilityPointsGranted         = 9026,
        // (9025 and 9027-9031 are taken by PortalExitInstance and the challenge-portal properties below - next free id is 9033)
        // on the player biota: how many enlightenment-milestone class ability points have been paid out so
        // far (DESIGN.md sec 2c). Idempotent catch-up compares this to the entitlement derived from the
        // Enlightenment count, so the grant is retroactive-safe for characters enlightened before this lane
        // existed. LIFETIME total - must NEVER be reset (incl. by enlightenment, which sets Level back to 1
        // but must leave this counter and the earned points intact): a re-leveling character then computes
        // owed = entitled - this and is never paid a milestone point twice. See
        // Player.GrantEnlightenmentClassAbilityPoints.
        EnlightenmentClassAbilityPointsGranted     = 9032,

        // ACRealms port (id matches ACRealms): the realm a character calls home;
        // portals and recalls resolve their destination instance from this
        HomeRealm                                = 42000,

        // WaffleACE (custom band): when 1 on a portal, each use routes the player into
        // a fresh ephemeral instance of the destination dungeon
        PortalInstancing                         = 9021,

        // WaffleACE (custom band): routes portal users into this realm's default
        // instance (per-realm content overrides make it a different experience);
        // composes with PortalInstancing (= private ephemeral copy of the realm)
        PortalRealm                              = 9022,

        // WaffleACE (custom band): when 1 on a portal, using it inside an ephemeral
        // instance sends the player to their stored EphemeralRealmExitTo instead of a
        // static Destination - i.e. an exit portal whose target is per-player. Used by
        // the DreamWeave Loomstone, whose exit is the character's own training hall.
        PortalExitInstance                       = 9025,

        // WaffleACE (custom band): on a portal weenie, the length in seconds of the DPS-challenge
        // run it arms; > 0 marks the portal as a DPS-challenge portal (drops the player into a
        // strictly single-player ephemeral arena and starts the timed damage trial on arrival).
        DpsChallengeDuration                     = 9027,

        // WaffleACE (custom band): on a portal weenie, the seconds between escalation tiers of the survival
        // challenge it arms; > 0 marks the portal as a survival-challenge portal (drops the player into a
        // strictly single-player ephemeral arena where enemies get stronger each tier until the player dies).
        SurvivalChallengeInterval                = 9028,

        // WaffleACE (custom band): on a portal weenie, the total number of waves in the gauntlet it arms; > 0
        // marks the portal as a wave-challenge portal (drops the player into a strictly single-player ephemeral
        // dungeon and runs a wave-by-wave survival gauntlet - see Player_WaveChallenge.cs).
        WaveChallengeWaves                       = 9029,

        // WaffleACE (custom band): on a wave-roster weenie, which wave (1-based) that roster defines. Content-set
        // and purely a safety net - the engine addresses rosters by wcid arithmetic and only sanity-checks this
        // value against the wave it is spawning, logging a mismatch without changing behavior.
        WaveChallengeWaveIndex                   = 9030,

        // WaffleACE (custom band): on a wave-challenge portal weenie, the wcid of the wave 1 roster weenie. The
        // roster weenie for wave N is WaveChallengeRosterBaseWcid + N - 1, so a gauntlet's rosters must occupy a
        // contiguous wcid block. A roster weenie carries no behavior of its own: the engine reads its generator
        // table (weenie_properties_generator) as the wave's spawn list.
        WaveChallengeRosterBaseWcid              = 9031,

        // WaffleACE (custom band): on an equipment-mod-eligible item, how many equipment mods that item
        // can carry. Written once when the item is first modded (the value is the rating count the item
        // was born with; an unrated item caps at 1) and never grown afterwards. Absent = never modded.
        // The mod values themselves live in the reserved PropertyFloat band 8100-8199 as potency scalars -
        // see ACE.Server.EquipmentMods.EquipmentModRegistry.
        // (9032 is taken by EnlightenmentClassAbilityPointsGranted, allocated in the PropertyInt 9028 collision fix - next free id is 9034)
        GearModCapacity                          = 9033,

        // WaffleACE (custom band): on a weapon managed by the weapon-mod system, how many of its ten
        // tinker slots are currently filled by layer 1 tinkers (the rest being preserved imbues and
        // Tier A special modifiers). PRESENCE marks the weapon as managed by that system, which is what
        // suppresses the retail TinkerLog integrity gate on our own items - see
        // ACE.Server.WeaponMods.WeaponModManager. Absent = never rerolled or swapped.
        // (next free id is 9035)
        WeaponModTinkerCount                     = 9034,

        // WaffleACE (custom band): a multi-use salvage tool's fixed CAPACITY - how many applications it holds
        // when new. Authored on the weenie and never changed in play. The LIVE count is PropertyInt.Structure,
        // decremented once per application, with MaxStructure equal to this value; that is what lets the client
        // draw its own uses-remaining bar. See ACE.Server.Entity.SalvageTool.
        //
        // PRESENCE with a value above 0 is what MARKS an item as a multi-use salvage tool rather than an
        // ordinary salvage bag, and that distinction is load-bearing: without it a tool at 7 of 10 would be
        // indistinguishable from a PARTIAL bag at 70 of 100, and a partial bag is deliberately refused by both
        // mod systems. Absent on every ordinary bag, which is destroyed outright by its single use.
        //
        // Deliberately carries no [AssessmentProperty]: the client does not know this id and the value must
        // never be sent to it, so the appraisal line is the only player-facing surface for the capacity.
        // (next free id is 9036)
        SalvageToolCharges                       = 9035,

        // WaffleACE (custom band): ALPHA-TEST-ONLY marker. PRESENCE on a source item routes
        // EquipmentModManager.HandleApply to the Maximize action instead of the normal
        // material-driven paths - every equipment mod already on the target has its potency set to
        // exactly 1.0 (the maximum roll), with no change to which mods are present. Kept out of
        // production purely by wcid 1001909's absence from Content/prod-manifest.txt - there is no
        // code-side gate. See ACE.Server.EquipmentMods.EquipmentModManager.
        // (next free id is 9037)
        EquipmentModMaximizer                    = 9036,

        // WaffleACE (custom band): how many class ability points this character has bought with
        // EXPERIENCE. Drives the geometric price curve for the next such point, so it must never be
        // reset - it is the curve's position, not a balance. DELIBERATELY separate from 9019
        // ClassAbilityPointsPurchasedWithLum: the two lanes escalate independently, so a character
        // buys the cheap first point on each. See Docs/ClassAbilities/XP-LANE-SPEC.md sec 3.3 and
        // Player.TryBuyClassAbilityPointsWithXp.
        // (next free id is 9038)
        ClassAbilityPointsPurchasedWithXp        = 9037,

        // WaffleACE (custom band): the CreatureType whose death fills this item by one charge. PRESENCE
        // with a value above 0 is what MARKS an item as a kill-fill vessel; nothing else does. The live
        // count is PropertyInt.Structure and the capacity is MaxStructure, both of which the client already
        // knows, so it draws the uses bar for free. See ACE.Server.Entity.KillFillVessel.
        //
        // Deliberately carries no [AssessmentProperty]: the client does not know this id, and the rule is
        // explained to the player in the vessel's own description text instead.
        // (next free id is 9039)
        KillFillCreatureType                     = 9038,

        // WaffleACE (custom band): OPTIONAL location filter on a 9038 vessel - the landblock (the high 16
        // bits of a cell id, e.g. 0x564E) a kill must happen in. Absent or 0 means the vessel fills wherever
        // its creature type appears.
        //
        // Optional because the two intended uses genuinely differ. A vessel handed out by a quest giver to
        // send the player through a specific portal MUST carry one, or that portal is decorative and the
        // player farms whatever is convenient instead - see
        // Content/preview/quest_assay_row/PORTAL-PHASE-OPTIONS.md section 6. A vessel bought off a vendor
        // has no portal to protect and can legitimately omit it.
        // (next free id is 9040)
        KillFillLandblock                        = 9039,

        // WaffleACE (custom band): OPTIONAL realm filter on a 9038 vessel - the realm a kill must happen
        // in. Pairs with 9039, and both are needed to scope a vessel to one dungeon copy: a landblock id
        // is shared by every realm's copy of that block, so a landblock filter alone still admits the
        // retail original. Bay 2 is the worked example - retail portal 22870 portalcrystalminelow has a
        // destination byte-identical to bay 2's own portal 1002521, so without this filter the vessel
        // fills on retail's copper, granite and sandstone golems (all CreatureType 13) in realm 0.
        //
        // ABSENCE, NOT ZERO, IS WHAT MEANS "ANY REALM" HERE, and that is deliberately unlike 9039. Realm
        // 0 is a real, reachable realm - the base retail world - so 0 has to keep meaning "realm 0 only",
        // which is what a vendor vessel intended for retail content would author. A landblock 0 is not a
        // real place, so 9039 can afford to spend 0 as its "anywhere" sentinel and this one cannot.
        // (next free id is 9041)
        KillFillRealm                            = 9040,

        // World Events (WaffleACE, Docs/WorldEvents/TECH-DESIGN.md 2.13). WorldEventId is the runtime stamp -
        // the event's RunId - on every object a running event spawned (creatures, Rifts, Weave Caches). Its
        // PRESENCE excludes the object from shard persistence (WorldObject_Database.IsDynamicThatShouldPersistToShard)
        // and marks it for the orphan filter in Landblock.SpawnDynamicShardObjects; it is never authored in
        // content. WorldEventsCompleted is an optional lifetime counter on a player. WorldEventRole is the
        // catalog role of a WorldEventCreature-flagged weenie: 0 trash / 1 elite / 2 champion / 3 named
        // boss. Role 3 joins the catalog but is never drawn by wave or champion selection - a named boss
        // arrives only when the boss axis asks for it by id (BOSS-STANDARD.md section 1).
        WorldEventId                             = 9041,
        WorldEventsCompleted                     = 9042,
        WorldEventRole                           = 9043,

        // RefireStations (WaffleACE, DreamWeave, 2026-08-18). On a piece of equipment, ArcaneLoreOriginal
        // records the ItemDifficulty (109, Arcane Lore requirement) it was born with, the FIRST time the
        // Arcane Alignment Table (wcid 1002751) aligns it - the ceiling every later alignment clamps against.
        // DefenseWieldOriginal records the WieldDifficulty of the first Melee/Missile/Magic Defense
        // wield-requirement slot found, the first time the Defense Requirement Reforge (wcid 1002752) reforges
        // it - same role, different property. Both are absent until the item's first reforge.
        ArcaneLoreOriginal                       = 9044,
        DefenseWieldOriginal                     = 9045,

        // Equipment mods (WaffleACE, DreamWeave, 2026-08-26). The BORN-WITH value of each of the ten gear
        // ratings, in the same order as EquipmentModManager.GearRatingProperties. Stamped once, at the
        // moment the item is created - from its weenie template in WorldObjectFactory.CreateNewWorldObject,
        // and again from the rolled value in LootGenerationFactory.TryMutateGearRating - and never written
        // again after that. Nothing a player can do adds to one.
        //
        // This exists because the retail Luminous/Empowered Amber gems ADD gear ratings to an already-made
        // item through ordinary recipes (RecipeManager.ModifyInt writing PropertyInt 370-383 directly), and
        // Obsidian pays out one equipment mod per rating point. Without a record of what the item was born
        // with, a crafted point is indistinguishable from a rolled one and buys a mod it never earned. So
        // Obsidian sums THESE, not the live ratings - see EquipmentModManager.SumGearRatings.
        //
        // ABSENT MEANS ZERO, NOT UNKNOWN. An item carrying a live rating with no stamp beside it converts
        // into nothing. That is the safe direction - a crafted point can never be mistaken for a natural
        // one - and the one-time backfill in Database/Updates/Shard covers every item that predates this.
        GearDamageOriginal                       = 9046,
        GearDamageResistOriginal                 = 9047,
        GearCritOriginal                         = 9048,
        GearCritResistOriginal                   = 9049,
        GearCritDamageOriginal                   = 9050,
        GearCritDamageResistOriginal             = 9051,
        GearHealingBoostOriginal                 = 9052,
        GearMaxHealthOriginal                    = 9053,
        GearPKDamageRatingOriginal               = 9054,
        GearPKDamageResistRatingOriginal         = 9055,

        // Proving Grounds: Speed (WaffleACE, DreamWeave, 2026-08-26): the speed_season id that
        // BestSpeedRunCenti (PropertyInt64.BestSpeedRunCenti) was set under. A convenience cache on the
        // player biota only - the character_speed_run shard table is the authority. When this does not
        // match the active season, the cached best is treated as unset.
        SpeedChallengeSeasonId                   = 9056,

        // Objective Lock (ACE.Server.Entity.ObjectiveLock): the summed weight of live tokens the gate must
        // reach or exceed to open. Carrying this property is what MAKES an object the gate rather than a
        // contributor - see ObjectiveLock.Required.
        ObjectiveLockRequired                    = 9057,
        // Objective Lock: how much this contributor's token is worth toward the gate's ObjectiveLockRequired,
        // passed as Contribute's weight. Defaults to 1 when unset.
        ObjectiveLockWeight                      = 9058,

        // Mule Form Token (WaffleACE, DreamWeave, 2026-09-01): on a token carrying PropertyBool
        // MuleFormToken, the WeenieClassId of the creature the token is attuned to. Absent means the
        // token is unattuned; once set it is never rewritten (there is no re-attunement).
        MuleFormWcid                             = 9059,

        /// <summary>
        /// Threads (WaffleACE): on a creature, the run id of the ThreadDungeonRun that spawned it.
        /// Stamped before EnterWorld by ThreadDungeonSpawner and excluded from persistence in
        /// WorldObject_Database, exactly like WorldEventId. Absent means the creature is not run-owned.
        /// </summary>
        ThreadDungeonRunId                      = 9060,

        /// <summary>
        /// Player Facets: which facet slot the character is currently standing on. Absent or 1
        /// means slot 1, the base build. Slot rows live in the character_facet shard table; this
        /// property only records which one is live.
        /// </summary>
        ActiveFacetSlot                        = 9061,

        /// <summary>
        /// Custom Dreamweave Augmentations (WaffleACE, DreamWeave, 2026-09-05): on a player, how many
        /// account-wide +100 mule vault entry augmentations have been bought on this character. Summed
        /// across every character on the account by CustomAugBroker.AccountAugCount - this property
        /// is per-character storage, but the benefit and the price index are account-wide.
        /// </summary>
        AugmentationMuleSpace                    = 9062,

        /// <summary>
        /// Custom Dreamweave Augmentations (WaffleACE, DreamWeave, 2026-09-05): on a player, how many
        /// +10% pick-up speed augmentations have been bought on this character. Per-character, uncapped;
        /// read by PickupSpeed.Compute alongside the shipped Quickhand pick-up boons.
        /// </summary>
        AugmentationPickupSpeed                  = 9063,

        /// <summary>
        /// Custom Dreamweave Augmentations (WaffleACE, DreamWeave, 2026-09-05): on a player, how many
        /// +10% spell duration augmentations have been bought on this character. Per-character, uncapped;
        /// read alongside the retail AugmentationIncreasedSpellDuration count at the same three
        /// EnchantmentManager/AddEnchantmentResult sites.
        /// </summary>
        AugmentationSpellDurationCustom           = 9064,

        /// <summary>
        /// ML Treasure Hunt (WaffleACE, Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md): dig steps completed on
        /// this treasure map instance, incremented once per dig animation while the player stands on the
        /// rolled site, and read to decide whether the next Use continues digging or pays out. Absent
        /// means digging has not started.
        /// </summary>
        TreasureMapDigProgress                   = 9065,

        /// <summary>
        /// ML Treasure Hunt: on a treasure map, the creature wcid to spawn at the dug site instead of
        /// paying currency. Greater than 0 makes this the rare Relaria variant; absent or 0 is an
        /// ordinary map.
        /// </summary>
        TreasureMapBossWcid                      = 9066,

        /// <summary>
        /// Class Ability overhaul deployment migration (WaffleACE): PRESENCE means the one-shot forced
        /// full respec has ALREADY RUN for this character and must never run again. Its ABSENCE is the
        /// only thing that makes the sweep run, so the value itself carries no meaning beyond "stamped".
        /// Written by Player.ApplyClassAbilityOverhaulRespec once that sweep has cleared the character's
        /// learned ability rows, set AvailableClassAbilityPoints to TotalClassAbilityPointsEarned,
        /// emptied every stored character_facet ability set and consumed every unused training token in
        /// the pack. Never cleared - clearing it would re-run a destructive migration.
        /// </summary>
        ClassAbilityOverhaulRespecDone           = 9067,

        /// <summary>
        /// ML digsite encounters (WaffleACE): on anything a dug-up encounter placed - every creature it
        /// spawns and its reward chest - the id of the encounter that placed it. PRESENCE is what matters:
        /// it is the persistence exclusion read by WorldObject.IsDynamicThatShouldPersistToShard, the
        /// persisted half of the two-key death check in Creature.Die, the corpse-suppression key, and the
        /// marker MlDigsiteOrphanFilter sweeps on. Absent means the object is not encounter-owned.
        ///
        /// Unlike TreasureMapBossWcid (9066) this has exactly ONE carrier class - objects the encounter
        /// itself placed - so no `is Creature` narrowing is needed anywhere it is read. The chest carries
        /// it; the chest's CONTENTS deliberately do not, so an item a player pulls out of the chest
        /// persists normally by the ordinary pickup path.
        /// </summary>
        MlDigsiteEncounterId                     = 9068,

        /// <summary>
        /// Bluespire ladder (WaffleACE): on a PORTAL weenie, which rung of the ladder this portal leads
        /// into, 1-6. PRESENCE is what makes a portal a ladder portal at all - absent means an ordinary
        /// portal and Portal.CheckUseRequirements falls straight through, which is why the added cost on
        /// every other portal in the game is one null check.
        ///
        /// The rung is the ONLY thing the weenie carries. The level gate, the prerequisite rung and the
        /// rung-1 entry quest are all PropertyManager tunables (bluespire_ladder_*), read live on every
        /// use, so raising a gate re-locks players who unlocked at the old one. Deliberately NOT
        /// MinLevel (86) / QuestRestriction (37): those are weenie properties, so retuning either would
        /// be a content apply rather than a live tune, and QuestRestriction holds a single string where
        /// rung 1 needs both a cleared-rung check and an entry-quest check.
        /// </summary>
        BluespireLadderRung                      = 9069,

        /// <summary>
        /// Death spawn (WaffleACE): on a CREATURE weenie, the wcid to spawn when this creature dies.
        /// The generic primitive behind the rung-5 shell swap - a first form dies, adds step out - with
        /// no per-boss code. Paired with DeathSpawnCount (9071) and DeathSpawnRadius (PropertyFloat
        /// 9013); absent, zero or negative means no death spawn and the death path does nothing extra.
        /// Handled once in Creature.OnDeath beside the KillQuest block, in the dying creature's own
        /// landblock instance.
        /// </summary>
        DeathSpawnWcid                           = 9070,

        /// <summary>
        /// Death spawn (WaffleACE): how many copies of DeathSpawnWcid (9070) to place. Absent or below 1
        /// reads as 1, so a weenie that names a wcid and forgets the count still spawns one thing rather
        /// than nothing. Clamped at DeathSpawnPlan.MaxCount so a typo cannot flood a room.
        /// </summary>
        DeathSpawnCount                          = 9071,

        /// <summary>
        /// ML Treasure Hunt test tooling (WaffleACE): on a treasure map created by the admin
        /// /testtreasuremap command, the digsite encounter type FinishDig's MlDigsiteManager.TryStart call
        /// must use instead of rolling one (MlDigsiteRules.PickType). Stored as (int)MlDigsiteType + 1, so
        /// 0/absent means "roll normally" rather than colliding with MlDigsiteType.WavesAndMiniBoss (0).
        /// See MlDigsiteRules.EncodeForcedType / DecodeForcedType.
        /// </summary>
        TreasureMapForcedDigsiteType              = 9072,

        /// <summary>
        /// ML Treasure Hunt test tooling (WaffleACE): on a treasure map created by the admin
        /// /testtreasuremap command with an explicit Boss Rush set id, the MlDigsiteMechanicSet.SetId to
        /// pin for that one encounter, overriding ml_digsite_bossrush_set_force without touching the
        /// tunable. Absent or 0 means the encounter rolls its set (or uses the live tunable) as normal.
        /// </summary>
        TreasureMapForcedMechanicSet              = 9073,

        /// <summary>
        /// Wave encounters (WaffleACE, ACE.Server.WaveEncounters): stamped on every creature a running
        /// wave encounter spawned, valued with that encounter's id. Three readers, all keyed on PRESENCE:
        /// Creature.Die's two-key death hook (with the in-memory P_WaveEncounter back-reference),
        /// WorldObject.IsDynamicThatShouldPersistToShard (never saved), and WaveEncounterOrphanFilter (a
        /// stray biota found at landblock load is deleted). Never authored in content.
        /// </summary>
        WaveEncounterId                           = 9074,

        /// <summary>
        /// PvP arena (WaffleACE, Docs/Pvp/DESIGN.md): crash-recovery marker only, never a rating. Stamped by
        /// Player.EnterPvpMatch with the player's pre-match PlayerKillerStatus before the flip to PK Lite, removed
        /// by ExitPvpMatch after restoring it. Present at login means a match ended without a clean exit: the
        /// login path restores the status from it and removes it (Player.RestorePvpMatchStatusAtLogin).
        /// </summary>
        PvpMatchReturnPkStatus                    = 9075,

        /// <summary>
        /// ML Treasure Hunt zone isolation (WaffleACE): on a treasure map, the Marae Lassel level zone
        /// (South/North/Plateau, (int)MlTreasureZone) it was dropped or first read in. Stamped once, at the
        /// same moment the site is placed (MlTreasureDrop.StampSite / TreasureMapHandler.EnsureSiteStamped),
        /// and read back to keep every later placement - the lazy first-use placement, /rrtm, and the
        /// digsite encounter the completed dig opens - confined to that same zone. Absent or 0 (Unknown) on
        /// a map dropped before this property existed; every zone-aware lookup treats Unknown as unfiltered.
        /// </summary>
        TreasureMapZone                           = 9076,

        /// <summary>
        /// PvP arena Blood payout (WaffleACE, Docs/Pvp/DESIGN.md "Rewards"): how many arena matches have PAID Blood
        /// to this character on the arena day named by <see cref="PvpArenaBloodPaidDay"/>. Read and written only on
        /// the player's own action chain (Player.GrantArenaBlood) or, when offline, on the world thread through the
        /// OfflinePlayer. A stored day earlier than today means the count is stale and reads as 0.
        /// </summary>
        PvpArenaBloodPaidCount                    = 9077,

        /// <summary>
        /// PvP arena Blood payout: the arena day index (SurveyDay.DayIndex at pvp_arena_blood_reset_timezone /
        /// pvp_arena_blood_reset_hour) that <see cref="PvpArenaBloodPaidCount"/> belongs to.
        /// </summary>
        PvpArenaBloodPaidDay                      = 9078,

        /// <summary>
        /// PvP arena Blood payout: Blood already EARNED (and counted against the daily cap) that could not be
        /// delivered - a full pack, too much burden, or the player offline at payout. Delivered at the next login
        /// (Player.DeliverOwedArenaBlood) without counting against the cap again. Absent or 0 means nothing owed.
        /// </summary>
        PvpArenaBloodOwed                         = 9079,

        /// <summary>
        /// Thread-Guide (WaffleACE): the highest guide rung level this character has WON (0 or absent = none).
        /// Written only by the clear seam (ThreadGuideStation.RecordGuideClear, wired as ThreadDungeonManager.GuideRecorder) and the /dd guide admin
        /// subcommand; ThreadGuideLadder.NextRung reads it to decide the next rung.
        /// </summary>
        ThreadGuideLevel                          = 9080,

        /// <summary>
        /// Thread-Guide (WaffleACE): the serial of this character's current guide fragment grant. Bumped only on a
        /// successful give (NPC grant or fail regrant), so every older guide item fails ThreadGuideRules.IsCurrent.
        /// </summary>
        ThreadGuideGrantSerial                    = 9081,

        /// <summary>
        /// Thread-Guide (WaffleACE): lifetime count of ALL Thread clears by this character, guide or not, under the
        /// same recipients and nothing-spawned gate as the daily survey (ThreadDungeonManager.RecordClearCount).
        /// </summary>
        ThreadClearsLifetime                      = 9082,

        /// <summary>
        /// PvP battleground Mark of the Hopeslayer payout (WaffleACE, Docs/Pvp/BATTLEGROUNDS.md "Rewards"): how many
        /// battleground matches have PAID Marks to this character on the day named by <see cref="PvpBgMarksPaidDay"/>.
        /// The battleground daily cap's own ledger, independent of <see cref="PvpArenaBloodPaidCount"/>. Read and
        /// written only on the player's own action chain (Player.GrantArenaBlood, ledger kind Battleground) or, when
        /// offline, on the world thread through the OfflinePlayer. A stored day earlier than today reads as 0.
        /// </summary>
        PvpBgMarksPaidCount                       = 9083,

        /// <summary>
        /// PvP battleground Mark payout: the day index (the same reset zone and hour as the arena day) that
        /// <see cref="PvpBgMarksPaidCount"/> belongs to. Owed Marks stay in the shared <see cref="PvpArenaBloodOwed"/>.
        /// </summary>
        PvpBgMarksPaidDay                         = 9084,
        // (next free id is 9085)
    }

    public static class PropertyIntExtensions
    {
        public static string GetValueEnumName(this PropertyInt property, int value)
        {
            switch (property)
            {
                case PropertyInt.ActivationResponse:
                    return System.Enum.GetName(typeof(ActivationResponse), value);
                case PropertyInt.AetheriaBitfield:
                    return System.Enum.GetName(typeof(AetheriaBitfield), value);
                case PropertyInt.AttackHeight:
                    return System.Enum.GetName(typeof(AttackHeight), value);
                case PropertyInt.AttackType:
                    return System.Enum.GetName(typeof(AttackType), value);
                case PropertyInt.Attuned:
                    return System.Enum.GetName(typeof(AttunedStatus), value);
                case PropertyInt.AmmoType:
                    return System.Enum.GetName(typeof(AmmoType), value);
                case PropertyInt.Bonded:
                    return System.Enum.GetName(typeof(BondedStatus), value);
                case PropertyInt.ChannelsActive:
                case PropertyInt.ChannelsAllowed:
                    return System.Enum.GetName(typeof(Channel), value);
                case PropertyInt.CombatMode:
                    return System.Enum.GetName(typeof(CombatMode), value);
                case PropertyInt.DefaultCombatStyle:
                case PropertyInt.AiAllowedCombatStyle:
                    return System.Enum.GetName(typeof(CombatStyle), value);
                case PropertyInt.CombatUse:
                    return System.Enum.GetName(typeof(CombatUse), value);
                case PropertyInt.ClothingPriority:
                    return System.Enum.GetName(typeof(CoverageMask), value);
                case PropertyInt.CreatureType:
                case PropertyInt.SlayerCreatureType:
                case PropertyInt.FoeType:
                case PropertyInt.FriendType:
                    return System.Enum.GetName(typeof(CreatureType), value);
                case PropertyInt.DamageType:
                case PropertyInt.ResistanceModifierType:
                    return System.Enum.GetName(typeof(DamageType), value);
                case PropertyInt.CurrentWieldedLocation:
                case PropertyInt.ValidLocations:
                    return System.Enum.GetName(typeof(EquipMask), value);
                case PropertyInt.EquipmentSetId:
                    return System.Enum.GetName(typeof(EquipmentSet), value);
                case PropertyInt.Gender:
                    return System.Enum.GetName(typeof(Gender), value);
                case PropertyInt.GeneratorDestructionType:
                case PropertyInt.GeneratorEndDestructionType:
                    return System.Enum.GetName(typeof(GeneratorDestruct), value);
                case PropertyInt.GeneratorTimeType:
                    return System.Enum.GetName(typeof(GeneratorTimeType), value);
                case PropertyInt.GeneratorType:
                    return System.Enum.GetName(typeof(GeneratorType), value);
                case PropertyInt.HeritageGroup:
                case PropertyInt.HeritageSpecificArmor:
                    return System.Enum.GetName(typeof(HeritageGroup), value);
                case PropertyInt.HookType:
                    return System.Enum.GetName(typeof(HookType), value);
                case PropertyInt.HouseType:
                    return System.Enum.GetName(typeof(HouseType), value);
                case PropertyInt.ImbuedEffect:
                case PropertyInt.ImbuedEffect2:
                case PropertyInt.ImbuedEffect3:
                case PropertyInt.ImbuedEffect4:
                case PropertyInt.ImbuedEffect5:
                    return System.Enum.GetName(typeof(ImbuedEffectType), value);
                case PropertyInt.HookItemType:
                case PropertyInt.ItemType:
                case PropertyInt.MerchandiseItemTypes:
                case PropertyInt.TargetType:
                    return System.Enum.GetName(typeof(ItemType), value);
                case PropertyInt.ItemXpStyle:
                    return System.Enum.GetName(typeof(ItemXpStyle), value);
                case PropertyInt.MaterialType:
                    return System.Enum.GetName(typeof(MaterialType), value);
                case PropertyInt.PaletteTemplate:
                    return System.Enum.GetName(typeof(PaletteTemplate), value);
                case PropertyInt.PhysicsState:
                    return System.Enum.GetName(typeof(PhysicsState), value);
                case PropertyInt.HookPlacement:
                case PropertyInt.Placement:
                case PropertyInt.PCAPRecordedPlacement:
                    return System.Enum.GetName(typeof(Placement), value);
                case PropertyInt.PortalBitmask:
                    return System.Enum.GetName(typeof(PortalBitmask), value);
                case PropertyInt.PlayerKillerStatus:
                    return System.Enum.GetName(typeof(PlayerKillerStatus), value);
                case PropertyInt.BoosterEnum:
                    return System.Enum.GetName(typeof(PropertyAttribute2nd), value);
                case PropertyInt.ShowableOnRadar:
                    return System.Enum.GetName(typeof(RadarBehavior), value);
                case PropertyInt.RadarBlipColor:
                    return System.Enum.GetName(typeof(RadarColor), value);
                case PropertyInt.WeaponSkill:
                case PropertyInt.WieldSkillType:
                case PropertyInt.WieldSkillType2:
                case PropertyInt.WieldSkillType3:
                case PropertyInt.WieldSkillType4:
                case PropertyInt.AppraisalItemSkill:
                    return System.Enum.GetName(typeof(Skill), value);
                case PropertyInt.AccountRequirements:
                    return System.Enum.GetName(typeof(SubscriptionStatus), value);
                case PropertyInt.SummoningMastery:
                    return System.Enum.GetName(typeof(SummoningMastery), value);
                case PropertyInt.UiEffects:
                    return System.Enum.GetName(typeof(UiEffects), value);
                case PropertyInt.ItemUseable:
                    return System.Enum.GetName(typeof(Usable), value);
                case PropertyInt.WeaponType:
                    return System.Enum.GetName(typeof(WeaponType), value);
                case PropertyInt.WieldRequirements:
                case PropertyInt.WieldRequirements2:
                case PropertyInt.WieldRequirements3:
                case PropertyInt.WieldRequirements4:
                    return System.Enum.GetName(typeof(WieldRequirement), value);

                case PropertyInt.GeneratorStartTime:
                case PropertyInt.GeneratorEndTime:
                    return DateTimeOffset.FromUnixTimeSeconds(value).DateTime.ToString(CultureInfo.InvariantCulture);

                case PropertyInt.ArmorType:
                    return System.Enum.GetName(typeof(ArmorType), value);
                case PropertyInt.ParentLocation:
                    return System.Enum.GetName(typeof(ParentLocation), value);
                case PropertyInt.PlacementPosition:
                    return System.Enum.GetName(typeof(Placement), value);
                case PropertyInt.HouseStatus:
                    return System.Enum.GetName(typeof(HouseStatus), value);

                case PropertyInt.UseCreatesContractId:
                    return System.Enum.GetName(typeof(ContractId), value);

                case PropertyInt.Faction1Bits:
                case PropertyInt.Faction2Bits:
                case PropertyInt.Faction3Bits:
                case PropertyInt.Hatred1Bits:
                case PropertyInt.Hatred2Bits:
                case PropertyInt.Hatred3Bits:
                    return System.Enum.GetName(typeof(FactionBits), value);

                case PropertyInt.UseRequiresSkill:
                case PropertyInt.UseRequiresSkillSpec:
                case PropertyInt.SkillToBeAltered:
                    return System.Enum.GetName(typeof(Skill), value);

                case PropertyInt.HookGroup:
                    return System.Enum.GetName(typeof(HookGroupType), value);

                //case PropertyInt.TypeOfAlteration:
                //    return System.Enum.GetName(typeof(SkillAlterationType), value);
            }

            return null;
        }
    }
}
