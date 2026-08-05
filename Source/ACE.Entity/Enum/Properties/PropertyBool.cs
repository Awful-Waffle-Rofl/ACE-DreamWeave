using System.ComponentModel;

namespace ACE.Entity.Enum.Properties
{
    // No properties are sent to the client unless they featured an attribute.
    // SendOnLogin gets sent to players in the PlayerDescription event
    // AssessmentProperty gets sent in successful appraisal
    public enum PropertyBool : ushort
    {
        Undef                            = 0,
        [Ephemeral]
        Stuck                            = 1,
        [AssessmentProperty][Ephemeral]
        Open                             = 2,
        [AssessmentProperty]
        Locked                           = 3,
        RotProof                         = 4,
        AllegianceUpdateRequest          = 5,
        AiUsesMana                       = 6,
        AiUseHumanMagicAnimations        = 7,
        AllowGive                        = 8,
        CurrentlyAttacking               = 9,
        AttackerAi                       = 10,
        IgnoreCollisions                 = 11,
        ReportCollisions                 = 12,
        Ethereal                         = 13,
        GravityStatus                    = 14,
        LightsStatus                     = 15,
        ScriptedCollision                = 16,
        Inelastic                        = 17,
        [Ephemeral]
        Visibility                       = 18,
        Attackable                       = 19,
        SafeSpellComponents              = 20,
        [SendOnLogin]
        AdvocateState                    = 21,
        Inscribable                      = 22,
        DestroyOnSell                    = 23,
        UiHidden                         = 24,
        IgnoreHouseBarriers              = 25,
        HiddenAdmin                      = 26,
        PkWounder                        = 27,
        PkKiller                         = 28,
        NoCorpse                         = 29,
        UnderLifestoneProtection         = 30,
        ItemManaUpdatePending            = 31,
        [Ephemeral]
        GeneratorStatus                  = 32,
        [Ephemeral]
        ResetMessagePending              = 33,
        DefaultOpen                      = 34,
        DefaultLocked                    = 35,
        DefaultOn                        = 36,
        OpenForBusiness                  = 37,
        IsFrozen                         = 38,
        DealMagicalItems                 = 39,
        LogoffImDead                     = 40,
        ReportCollisionsAsEnvironment    = 41,
        AllowEdgeSlide                   = 42,
        AdvocateQuest                    = 43,
        [SendOnLogin][Ephemeral]
        IsAdmin                          = 44,
        [SendOnLogin][Ephemeral]
        IsArch                           = 45,
        [SendOnLogin][Ephemeral]
        IsSentinel                       = 46,
        [SendOnLogin]
        IsAdvocate                       = 47,
        CurrentlyPoweringUp              = 48,
        [Ephemeral]
        GeneratorEnteredWorld            = 49,
        NeverFailCasting                 = 50,
        VendorService                    = 51,
        AiImmobile                       = 52,
        DamagedByCollisions              = 53,
        IsDynamic                        = 54,
        IsHot                            = 55,
        IsAffecting                      = 56,
        AffectsAis                       = 57,
        SpellQueueActive                 = 58,
        [Ephemeral]
        GeneratorDisabled                = 59,
        IsAcceptingTells                 = 60,
        LoggingChannel                   = 61,
        OpensAnyLock                     = 62,
        [AssessmentProperty]
        UnlimitedUse                     = 63,
        GeneratedTreasureItem            = 64,
        IgnoreMagicResist                = 65,
        IgnoreMagicArmor                 = 66,
        AiAllowTrade                     = 67,
        [SendOnLogin]
        SpellComponentsRequired          = 68,
        [AssessmentProperty]
        IsSellable                       = 69,
        IgnoreShieldsBySkill             = 70,
        NoDraw                           = 71,
        ActivationUntargeted             = 72,
        HouseHasGottenPriorityBootPos    = 73,
        [Ephemeral]
        GeneratorAutomaticDestruction    = 74,
        HouseHooksVisible                = 75,
        HouseRequiresMonarch             = 76,
        HouseHooksEnabled                = 77,
        HouseNotifiedHudOfHookCount      = 78,
        AiAcceptEverything               = 79,
        IgnorePortalRestrictions         = 80,
        RequiresBackpackSlot             = 81,
        DontTurnOrMoveWhenGiving         = 82,
        NpcLooksLikeObject               = 83,
        IgnoreCloIcons                   = 84,
        [AssessmentProperty]
        AppraisalHasAllowedWielder       = 85,
        ChestRegenOnClose                = 86,
        LogoffInMinigame                 = 87,
        PortalShowDestination            = 88,
        PortalIgnoresPkAttackTimer       = 89,
        NpcInteractsSilently             = 90,
        [AssessmentProperty]
        Retained                         = 91,
        IgnoreAuthor                     = 92,
        Limbo                            = 93,
        [AssessmentProperty]
        AppraisalHasAllowedActivator     = 94,
        ExistedBeforeAllegianceXpChanges = 95,
        IsDeaf                           = 96,
        [SendOnLogin][Ephemeral]
        IsPsr                            = 97,
        Invincible                       = 98,
        [AssessmentProperty]
        Ivoryable                        = 99,
        [AssessmentProperty]
        Dyable                           = 100,
        CanGenerateRare                  = 101,
        CorpseGeneratedRare              = 102,
        NonProjectileMagicImmune         = 103,
        [SendOnLogin]
        ActdReceivedItems                = 104,
        Unknown105                       = 105,
        [Ephemeral]
        FirstEnterWorldDone              = 106,
        RecallsDisabled                  = 107,
        [AssessmentProperty]
        RareUsesTimer                    = 108,
        ActdPreorderReceivedItems        = 109,
        [Ephemeral]
        Afk                              = 110,
        IsGagged                         = 111,
        ProcSpellSelfTargeted            = 112,
        IsAllegianceGagged               = 113,
        EquipmentSetTriggerPiece         = 114,
        Uninscribe                       = 115,
        WieldOnUse                       = 116,
        ChestClearedWhenClosed           = 117,
        NeverAttack                      = 118,
        SuppressGenerateEffect           = 119,
        TreasureCorpse                   = 120,
        EquipmentSetAddLevel             = 121,
        BarberActive                     = 122,
        TopLayerPriority                 = 123,
        [SendOnLogin]
        NoHeldItemShown                  = 124,
        [SendOnLogin]
        LoginAtLifestone                 = 125,
        OlthoiPk                         = 126,
        [SendOnLogin]
        Account15Days                    = 127,
        HadNoVitae                       = 128,
        NoOlthoiTalk                     = 129,
        [AssessmentProperty]
        AutowieldLeft                    = 130,

        /* Custom Properties */
        LinkedPortalOneSummon            = 9001,
        LinkedPortalTwoSummon            = 9002,
        HouseEvicted                     = 9003,
        UntrainedSkills                  = 9004,
        [Ephemeral]
        IsEnvoy                          = 9005,
        UnspecializedSkills              = 9006,
        FreeSkillResetRenewed            = 9007,
        FreeAttributeResetRenewed        = 9008,
        SkillTemplesTimerReset           = 9009,
        FreeMasteryResetRenewed          = 9010,
        // PROTOTYPE: multi-shot missile weapon flag - see WorldObject_Weapon.IsMultiShot
        MultiShot                        = 9011,
        // RETIRED 2026-08-03. Once marked a class ability token instance whose point cost was already paid, with
        // absent/false meaning a legacy token that charged points on USE. Nothing reads it now: EVERY class ability
        // token is prepaid unconditionally (Gem.UseClassAbilityToken). It was retired rather than repaired because
        // only one of the two acquisition paths ever stamped it, so vendor-bought tokens were charged twice.
        // The id stays allocated - do NOT reuse it; shard biotas written before this date still carry it.
        ClassAbilityTokenPrepaid           = 9012,
        // On a Drift Network NPC weenie: flags it as the class ability exchanger, which refunds a player's UNUSED
        // prepaid vouchers on use (see ClassAbilities.ClassAbilityTrainer). Trainer NPCs use PropertyString
        // ClassAbilityTrainerAbilities instead.
        ClassAbilityExchanger              = 9013,
        // On a Drift Network object weenie (the Arcane Pedestal prop): flags it as the Luminance-for-CAP
        // exchange pedestal, which on use prompts to buy one class ability point at the piecewise curve price
        // (see ClassAbilities.ClassAbilityTrainer.HandleLumExchange). Dispatched from GenericObject.ActOnUse.
        ClassAbilityLumExchanger           = 9014,

        // DPS challenge (WaffleACE): on the arena dummy creature weenie, flags it as the DPS-challenge
        // target whose DamageHistory the run reads to score the player.
        DpsChallengeTarget                 = 9015,
        // DPS challenge (WaffleACE): set on a player while a challenge run is armed/in progress; persisted,
        // so a mid-run logout is caught at next login and the player is re-homed to their lifestone.
        DpsChallengeActive                 = 9016,

        // Survival challenge (WaffleACE): on an arena creature weenie, flags it for the run controller's per-tier
        // raw-power raises (attributes, combat skills, DamageRating and Level scaled up each escalation tier).
        SurvivalChallengeCreature          = 9017,
        // Survival challenge (WaffleACE): set on a player while a survival run is armed/in progress; persisted,
        // so a mid-run logout is caught at next login and the player is re-homed to their lifestone.
        SurvivalChallengeActive            = 9018,

        // Wave challenge (WaffleACE): on a wave-roster creature weenie, flags it as a wave-gauntlet enemy. Its
        // death reports back to the run controller, which advances the gauntlet once the whole wave is dead
        // (see Creature_Death.Die -> Player_WaveChallenge.OnWaveCreatureDied). Content-set: every creature a
        // wave roster spawns MUST carry it, or its death is invisible to the run and the wave never clears.
        WaveChallengeCreature              = 9020,
        // Wave challenge (WaffleACE): set on a player while a wave run is armed/in progress; persisted, so a
        // mid-run logout is caught at next login and the player is re-homed to their lifestone.
        WaveChallengeActive                = 9021,
        // Survival challenge (WaffleACE): on a portal weenie, refuses entry to a player carrying any rare gem
        // and strips rare-gem buffs on arrival (keeps the survival arena free of rare-gem power).
        PortalBlocksRareGems               = 9019,

        // Banking (WaffleACE): on a player, controls where vendor sale proceeds land. Absent or TRUE (the
        // default, so existing characters are unchanged) credits the bank directly; FALSE pays out real coin
        // stacks into the pack, which is what inventory-reading tools poll (see Player_Commerce.HandleActionSellItem).
        BankAutoDeposit                    = 9022,

        // Mule (WaffleACE): on a player, marks an irreversible storage-and-trade-only character. Blocks every
        // progression and combat path (see Player_Mule.MuleBlocked), overrides carrying capacity outright in
        // Player.GetEncumbranceCapacity (attributes are left completely untouched), and exempts the character
        // from vitae. There is no un-mule path anywhere in the fork.
        IsMule                             = 9023,

        // On a Drift Network NPC weenie: flags it as the FULL class-ability respec NPC, which on use charges
        // a flat Luminance fee (class_ability_full_respec_lum_cost) and unlearns every learned class ability
        // at once, refunding all spent points in one pass (see ClassAbilities.ClassAbilityTrainer.HandleRespec).
        // Tier unlocks are NOT touched - they are non-refundable and survive a respec (DESIGN.md sec 4).
        ClassAbilityRespecer               = 9024,
    }
}
