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

        // On a Drift Network prop weenie: flags it as the EXPERIENCE-for-CAP exchange stone, which on use
        // prompts to buy one class ability point at the xp curve price (Docs/ClassAbilities/XP-LANE-SPEC.md
        // sec 3.7, ClassAbilities.ClassAbilityTrainer.HandleXpExchange). The sibling of 9014
        // ClassAbilityLumExchanger; the two stones are the same statue model in different colours, and each
        // lane keeps its own purchase counter so their prices escalate independently.
        ClassAbilityXpExchanger            = 9025,

        // World Events (WaffleACE, Docs/WorldEvents/TECH-DESIGN.md 2.13). WorldEventObjective marks a Rift or
        // boss objective spawn (the Rift weenie carries it; family members never do). WorldEventCache marks the
        // Weave Cache reward giver, dispatched by GenericObject.ActOnUse -> WorldEvents.WorldEventCacheHandler
        // (same pattern as 9025). WorldEventCreature marks a creature weenie as a member of the world-event
        // family catalog, which is built by scanning the world weenie cache; it needs PropertyString
        // WorldEventFamily (9010) and PropertyInt WorldEventRole (9043) alongside. 9029 is reserved spare.
        WorldEventObjective                = 9026,
        WorldEventCache                    = 9027,
        WorldEventCreature                 = 9028,

        // Refire Forge (WaffleACE, DreamWeave): on the Marketplace Workmanship Reforge NPC weenie (wcid
        // 1002750), flags it as the give-target the workmanship refire intercept looks for
        // (Player_Inventory.GiveObjectToNPC -> RefireStations.WorkmanshipReforgeStation.HandleGive,
        // ACE.Server/RefireStations/). 9029 is World Events' own reserved spare, not this one.
        WorkmanshipRefireForge              = 9030,

        // RefireStations (WaffleACE, DreamWeave, 2026-08-18): on the Marketplace Arcane Alignment Table NPC
        // weenie (wcid 1002751), flags it as the give-target for the ItemDifficulty (Arcane Lore requirement)
        // shift station - RefireStations.ArcaneAlignmentStation.HandleGive.
        ArcaneAlignmentTable                = 9031,

        // RefireStations: on the Marketplace Defense Requirement Reforge NPC weenie (wcid 1002752), flags it
        // as the give-target for the Melee/Missile/Magic Defense wield-requirement reforge station -
        // RefireStations.DefenseReforgeStation.HandleGive.
        DefenseRequirementReforge           = 9032,

        // Marketplace spell tutor (WaffleACE): on the NPC weenie (wcid 1002800), flags it as the combined
        // magic-school spell tutor. Tells to it are intercepted in GameActionTalkDirect and handled by
        // ACE.Server.Entity.SpellTutor, which reads the retail professor NPCs (53381-53385) for spell lists,
        // prices and skill gates and takes payment in pyreals from pack then bank.
        SpellTutor                         = 9033,

        // RefireStations (2026-08-18): on one of the three station attendant NPC weenies (wcids 1002753-
        // 1002755), flags it as an attendant that refuses ANY given item and hands it straight back -
        // Player_Inventory.GiveObjectToNPC checks this before the three station intercepts above, since a
        // Give/Refuse emote row only ever matches a specific item wcid and cannot express a blanket refusal
        // (WorldObject.HasGiveOrRefuseEmoteForItem / EmoteManager.GetEmoteSet). 9033 collided with SpellTutor
        // from #650, so this was renumbered from 9033 to 9034.
        RefireStationAttendant              = 9034,

        // RefireStations (WaffleACE, DreamWeave, 2026-08-18): on the Marketplace Weaver's Loom NPC weenie
        // (wcid 1002757), flags it as the give-target for the undergarment coverage upgrade station -
        // RefireStations.CoverageLoomStation.HandleGive.
        CoverageLoom                        = 9035,

        // RefireStations (WaffleACE, DreamWeave, 2026-08-19): on the Marketplace Brewer's Cauldron NPC weenie
        // (wcid 1002759), flags it as the give-target for the jewelry blessing-brew station -
        // RefireStations.BrewersCauldronStation.HandleGive.
        BrewersCauldron                     = 9036,

        // Marketplace Tinkerer's Inspiration pedestal (WaffleACE, DreamWeave, 2026-08-19): on the Generic
        // pedestal weenie (wcid 1002761), flags it as the use-dispatch target for the free tinkering buff -
        // GenericObject.ActOnUse -> Entity.TinkerersInspiration.TryHandleUse.
        TinkerersInspiration                = 9037,

        // World Events (WaffleACE, DreamWeave, 2026-08-19): on a CREATURE weenie, makes every create_list
        // Wield item it spawns take the creature's own ObjScale (PropertyFloat.DefaultScale), multiplied by
        // whatever scale the item weenie already carries, so a scaled-up boss holds a proportionate weapon,
        // shield or bow instead of a normal-sized one. Read in Creature_Equipment.GenerateWieldList.
        ScaleWieldedToBody                  = 9038,
        // WaffleACE fork: far-visible scenery. The server normally withholds CreateObject for a not-yet-known
        // object more than ObjectMaint.InitialClamp_Dist (112.5 m, 2D) from the player, and a spawning static
        // only notifies players inside that radius. An object with this flag skips both clamps, so it is
        // announced as soon as its landblock is in the player's 3x3 (the outdoor visibility window).
        // Cached onto PhysicsObj.IgnoreInitialClamp at InitPhysicsObj. For inert decor only - never on
        // creatures, players or anything that moves or broadcasts often.
        IgnoreInitialClamp                  = 9039,

        // Marketplace Salvage Forge (WaffleACE, DreamWeave, 2026-08-23): on the Salvage Forge NPC (wcid
        // 1002652), flags it as the give-target for the sixth RefireStation - a player hands the forge one
        // full salvage bag and it forges the material's multi-charge Hammer, replacing the Hollow Hammer
        // (wcid 1002650) as the entry point while the Hollow Hammer itself stays usable on bags already in a
        // player's pack. RefireStations.SalvageForgeStation.HandleGive.
        SalvageForgeStation                 = 9040,

        // Proving Grounds: Speed (WaffleACE, DreamWeave, 2026-08-26): on the season portal weenie, marks it
        // as the entry point for the speed challenge. Portal.ActOnUse resolves the active season from
        // SpeedSeasonManager and overrides portalDest with that season's entry position; a portal with no
        // active season refuses use. See Docs/ProvingGroundsSpeed/DESIGN.md section 3.2.
        SpeedChallengeEntry                 = 9041,

        // Proving Grounds: Speed (WaffleACE, DreamWeave, 2026-08-26): on a player while a speed run is
        // armed. Persisted so a mid-run logout is caught at next login in WorldManager.DoPlayerEnterWorld
        // and the player is re-homed to their lifestone, matching the other three Proving Grounds arenas.
        SpeedChallengeActive                = 9042,

        // Proving Grounds: Speed (WaffleACE, DreamWeave, 2026-08-26): on a world object whose use path can
        // finish a run (altar, lever, pedestal, exit portal). Gated by the active season's objective wcid,
        // so a flagged prop left over from another season cannot end a run early.
        SpeedChallengeGoal                  = 9043,

        // Proving Grounds: Speed (WaffleACE, DreamWeave, 2026-08-26): on a creature whose death can finish a
        // run. Gated by the active season's objective wcid; reports to the run controller in the same shape
        // OnWaveCreatureDied uses.
        SpeedChallengeBoss                  = 9044,

        // NOTE: 9045 (SpeedChallengeScaledCreature) is deliberately reserved in property-registry.tsv for
        // the deferred level-scaling phase (DESIGN.md section 12) and has NO enum member in v1. Do not add
        // it here until that phase lands - see the registry row's notes.

        // Objective Lock (ACE.Server.Entity.ObjectiveLock): on a contributor, marks it as the "wrong answer"
        // punishment path - contributing it calls ObjectiveLock.Reset instead of Contribute, clearing every
        // token on the lock. This is a no-op once the gate has latched open: Reset itself refuses to run
        // while Latched is TRUE, so a gate that has already opened can never be re-closed by a later reset.
        ObjectiveLockResets                 = 9046,

        // Proving Grounds: Speed (WaffleACE, DreamWeave, 2026-08-26): on a world object, activating it starts
        // the run clock. Gated by the active season's start_wcid, so a flagged object left over from another
        // season cannot start a run.
        SpeedChallengeStart                 = 9047,

        // Proving Grounds: Speed (WaffleACE, DreamWeave, 2026-08-27): on a plain (non-HousePortal) Portal,
        // opts it into PlayerInstanceSelectMode.Same instead of the default HomeRealmDefault, so a chute
        // portal at the far end of a dungeon wing can return the player to the hub inside the SAME
        // ephemeral instance rather than the shared-world copy. PropertyBool because it is a plain on/off
        // flag - unlike the sibling PortalExitInstance, which is a PropertyInt compared against 1 because
        // it carries a second meaningful value. Portal.ActOnUse only honors this when the portal's
        // Destination landblock matches the landblock the player is currently in; see the guard comment
        // there for why a mismatch is refused rather than routed blindly.
        PortalSameInstance                  = 9048,
        // Mule Vendor: opt-in on a WeenieType.Vendor weenie. WorldObjectFactory constructs a
        // PersonalVendor rather than a Vendor when this is set. Read at construction only.
        PersonalVendor                       = 9049,

        // Mule Vendor: opt-in on the summoning contract item. Gem.ActOnUse dispatches to
        // MuleSummonHandler.TryHandleUse.
        MuleVendorContract                   = 9050,

        // Offline Experience Bonus (WaffleACE, DreamWeave, 2026-08-30): RETIRED 2026-08-31 - do not read or
        // write. Was a player-set pause on the offline experience bonus, toggled by /offlinebonus on|off;
        // superseded by the automatic activity-gated clock in Player_OfflineBonus.cs. The enum member is kept
        // (and the id stays claimed) because existing shard biotas may still carry bool 9051 = true. Do not
        // reuse the id. See property-registry.tsv.
        OfflineExperienceBonusPaused        = 9051,

        // World Events boss confinement (WaffleACE, DreamWeave, 2026-08-30): on a creature, omit the
        // MovementParamFlags.Sticky flag every melee monster otherwise gets in Creature.GetMovementParameters.
        // Sticky keeps the physics mover latched onto the target it started toward, which is what turns one
        // fleeing player into a cross-landblock chase. Absent or false leaves movement exactly as it is.
        DisableSticky                       = 9052,

        // Mule Form Token (WaffleACE, DreamWeave, 2026-09-01): marks an item as a mule form token.
        // Gem.UseGem and Gem.HandleActionUseOnTarget dispatch to MuleFormToken on this flag rather than
        // on a wcid, so a second token weenie with different art or a different kill count is pure
        // content. Absent or false leaves the item an ordinary gem.
        MuleFormToken                       = 9053,

        // WaffleACE fork (DreamWeave, 2026-09-03): per-character chat toggle, set by the player command
        // "/summondamage on|off" and read in Pet.NotifyOwnerOfDamage. When set, the pet's owner - and only
        // the owner - gets a chat line for every hit their own summoned pet lands. Absent or false is off,
        // so the default is silence and existing characters need no migration. See NotifyOwnerOfDamage for
        // the four hooked damage routes and the two gaps (vital drains, damage-over-time ticks).
        SummonDamageMessages                = 9054,

        // WaffleACE fork (Threads phase 2, 2026-09-04): marks the Fragment Press station.
        // Read by Gem.HandleActionUseOnTarget -> RawFragment.UseObjectOnTarget (use a raw fragment on
        // it to press) and by Player_Inventory.GiveObjectToNPC (give an unbound Thread Gem to
        // re-open it into a raw fragment). Behind dynamic_dungeons_press_enabled.
        DungeonGemPress                      = 9055,

        // Player Facets (WaffleACE, DreamWeave, 2026-09-05): marks that the one-time slot-2 unlock notice
        // has already been shown to this character. Set (and persisted) BEFORE the notice is sent, in
        // Player_Facets.SendFacetUnlockNoticeIfDue, so a throw partway through delivery cannot cause the
        // notice to fire again on a later login. Absent or false means the notice has not been shown yet.
        FacetUnlockNoticeShown            = 9056,

        // Custom Dreamweave Augmentations (WaffleACE, DreamWeave, 2026-09-05): marks the give-target NPC
        // as a Custom Dreamweave Augmentation broker. Read by Player_Inventory.GiveObjectToNPC to
        // intercept the give path before any emote logic, alongside PropertyInt.AugmentationStat
        // naming which AugmentationType the NPC brokers.
        CustomAugBroker                      = 9057,

        // Threads (WaffleACE, 2026-09-07): marks the Survey-Archivist NPC (wcid 1003613) whose
        // use pays the daily-survey reward. Read by Creature.ActOnUse ->
        // ThreadDungeons.SurveyArchivistStation.TryHandleUse. The NPC's 19-set Use emote cascade was
        // DELETED and reimplemented in C# because WorldObject.OnActivate runs EmoteManager.OnUse BEFORE
        // ActOnUse, so there is no seam that lets the emotes run for flavour while C# pays the award.
        DungeonSurveyArchivist               = 9058,

        // Per-character preference for the outgoing damage-over-time combat message: the line the
        // caster sees when their DoT spell ticks on a target. Absent means follow the server-wide
        // PropertyManager tunable show_dot_messages (default false); set true/false by the player
        // via /dotdamage on|off.
        ShowDotDamage                         = 9059,

        // ML Treasure Hunt (WaffleACE, Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md): presence marks a Gem
        // weenie as a treasure map, dispatched from Gem.UseGem to TreasureMapHandler. A weenie carrying
        // this MUST have MaxStackSize 1 (the dig site lives in PropertyFloat 9010/9011 per instance) and
        // MUST carry no Use emote set - there is no CheckUseRequirements override in this fork, so a Use
        // emote would fire before ActOnUse and pre-empt the handler.
        TreasureMap                          = 9060,

        // ML Treasure Hunt: set once /rrtm has moved this map instance's dig site. A second reroll on
        // the same map is refused, so the originally rolled site stays measurable.
        TreasureMapRerolled                  = 9061,
        // WaffleACE fork (DreamWeave, 2026-09-10): per-character preference, set by the player command
        // "/summonattackontarget on|off". ABSENT means ON. When on, the owner's combat pets switch to the
        // monster the owner most recently attacked - Player.OnAttackMonster records it (runtime-only, never
        // persisted) and CombatPet.HandleFindTarget reads it on the pet's own tick. 9060 and 9061 are skipped
        // on purpose: both are reserved on origin/feat/ml-treasure-hunt (TreasureMap, TreasureMapRerolled).
        SummonAttackOnTarget                  = 9062,

        // Bluespire ladder rung 6 (WaffleACE): on a CREATURE weenie, opt this creature into crowd
        // scaling. At its own spawn moment its maximum health is multiplied and a DamageRating addend is
        // applied, both sized by how many alive, non-staff players are in the SAME landblock instance -
        // a dungeon is the unit, not a radius. Absent or false is the whole cost on every other creature
        // spawn in the game: one property read. Dials are the bluespire_ladder_d6_crowd_* tunables.
        BluespireCrowdScaled                  = 9063,

        // ML Relaria repeat-kill reward (WaffleACE): per-character preference, set by the player command
        // "/relaria aura on|off". ABSENT means ON, matching this fork's other per-character preference
        // toggles (9059 ShowDotDamage, 9062 SummonAttackOnTarget). Only meaningful once the character has
        // rolled the aura at all (MlRelariaTrophy.RepeatAuraQuestName stamped) - read by
        // Player.RelariaAuraEnabled, which gates whether the pulse in Player_RelariaAura re-arms.
        MlRelariaAuraEnabled                  = 9064,

        // Kill-task registry-row bootstrap (WaffleACE): on a creature, this kill may CREATE the killer's
        // (and eligible fellows') kill-task quest row instead of only crediting one they already hold, the
        // same bypass the Bluespire ladder's six rung bosses use (BluespireLadderRewards.ShouldBootstrapClearRow)
        // but per-creature rather than by a fixed name table. Motivating case: the Menhir Drummer
        // (1003262) can be killed by a player who has not yet accepted Kanokeh's charge (and so does not
        // hold the row HasQuest would require) - Kanokeh's own SetQuestCompletions arm, which runs at
        // charge ACCEPTANCE, is guarded against wiping a pre-kill by #1291 (must land with or before this
        // one).
        //
        // SLOT-1 ONLY (fixed 2026-09-25): this flag applies only to the primary KillQuest slot
        // (PropertyString 45). KillQuest2/KillQuest3 stay HasQuest-gated regardless of this property, even
        // on a creature that carries it true - it is read once per creature but must not be OR'd blindly
        // into every slot, or an unrelated quest tagged on KillQuest2/KillQuest3 inherits the bypass too
        // (real incident: 1003262's KillQuest2 briefly carried 'AunTumerokBountyCount', which let the same
        // kill skip Hea Piritaha's HasQuest gate for anyone). See
        // Creature.OnDeath_HandleKillTask/ShouldBypassHasQuestGate.
        KillQuestCreatesRow                   = 9065,

        // ML Treasure Hunt test tooling (WaffleACE): marks a treasure map instance as created by the admin
        // /testtreasuremap command. FinishDig reports MlDigsiteManager.TryStart's refusal reason to the
        // digger in chat only for a map carrying this (a non-test map stays silent), and
        // MlRelariaSpawner.TrySpawn skips the IsBossSite catalogue check for this map's dig, since a test
        // map's site is stamped at the admin's own position rather than rolled from the catalogue.
        TreasureMapAdminTest                  = 9066,

        // Wave encounters (WaffleACE, ACE.Server.WaveEncounters): marks an object whose activation starts
        // an object-anchored wave encounter (WorldObject.OnActivate -> WaveEncounterManager.TryStart). THE
        // gate for that hook: the anchor also carries the Proving Grounds ids 9029/9031 (waves, roster
        // base) and float 9004 (breather), but the PG portal carries 9031 too, so the hook must never key
        // on those alone.
        WaveEncounterAnchor                   = 9067,

        // ML Bluespire quest weapon tinker lock (WaffleACE, 2026-09-27): on a target, refuses
        // every use-on-target tinker source except one with a cook_book row keyed on (source
        // wcid, target wcid), or a WeenieType.ManaStone source (mana recharging is not
        // tinkering). Refire/give-target stations refuse a locked item outright. See
        // ACE.Server.Entity.TinkerLock. Read from the item's OWN biota only - no weenie fallback,
        // no migration, no shard write. Absent or false leaves an item unaffected. Stamped on the
        // 8 Bluespire quest weapons (1005450-1005457) only - the 3 Siraluun crests (1005480-
        // 1005482) do NOT carry this property (owner ruling, 2026-09-27) and tinker normally.
        TinkerLocked                          = 9068,

        // Placement opt-in: a Stuck object carrying this spawns at EXACTLY its authored position. The server's
        // spawn placement (PhysicsObj.enter_world -> SetPositionInternal -> Transition.FindPlacementPosition)
        // otherwise pushes an object out of baked geometry it overlaps - walls, and the dat's baked static
        // props - and steps it down, and Ethereal does not prevent either. With this set (and Stuck) the spawn
        // is forced into the authored cell instead (PhysicsObj.ForceIntoCellExact). Honoured only on a Stuck
        // non-creature, or on a Creature that is Stuck AND AiImmobile AND NpcLooksLikeObject (a station prop);
        // anything else is refused with a one-time warning per wcid. For decor meant to overlap baked geometry; pair it with Ethereal if players should walk
        // through the object.
        SpawnAtExactPosition                  = 9069,

        // Per-character fast tick preference set by /fasttick on|off|default. Absent follows the server property
        // fast_tick_all_players; an explicit true/false overrides it in either direction. PK/PKLite are always fast tick.
        FastTickOptIn                         = 9070,

        // PvP Template Facets (Docs/Pvp/TEMPLATES.md): on an item, marks it as part of an issued template kit.
        // Issued items are economy-locked always and destroyed at match exit and by the login sweep, which keys on
        // this mark ALONE. Tested through PvpTemplate.IsIssued, never read inline.
        PvpTemplateIssued                     = 9071,

        // Thread-Guide (WaffleACE): weenie marker on the Thread-Guide NPC. Creature.ActOnUse dispatches a
        // creature carrying it to ThreadDungeons.ThreadGuideStation, beside the Survey-Archivist dispatch. The
        // NPC must carry NO Use emote set, because emotes run before ActOnUse and would double-fire.
        ThreadGuideNpc                        = 9072,

        // Thread-Guide (WaffleACE): per-character latch for the one-time level-50 eligibility notice
        // (Player.SendThreadGuideNoticeIfDue). Set and saved BEFORE the notice is sent. Absent means not shown.
        ThreadGuideNoticeShown                = 9073,
    }
}
