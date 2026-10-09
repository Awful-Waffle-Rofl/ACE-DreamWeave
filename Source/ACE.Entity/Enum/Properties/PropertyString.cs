using System.ComponentModel;

namespace ACE.Entity.Enum.Properties
{
    // No properties are sent to the client unless they featured an attribute.
    // SendOnLogin gets sent to players in the PlayerDescription event
    // AssessmentProperty gets sent in successful appraisal
    public enum PropertyString : ushort
    {
        Undef                          = 0,
        [SendOnLogin]
        Name                           = 1,
        /// <summary>
        /// default "Adventurer"
        /// </summary
        Title                          = 2,
        Sex                            = 3,
        HeritageGroup                  = 4,
        [SendOnLogin][AssessmentProperty]
        Template                       = 5,
        AttackersName                  = 6,
        [AssessmentProperty]
        Inscription                    = 7,
        [AssessmentProperty]
        ScribeName                     = 8,
        VendorsName                    = 9,
        [AssessmentProperty]
        Fellowship                     = 10,
        MonarchsName                   = 11,
        LockCode                       = 12,
        KeyCode                        = 13,
        [AssessmentProperty]
        Use                            = 14,
        [AssessmentProperty]
        ShortDesc                      = 15,
        [AssessmentProperty]
        LongDesc                       = 16,
        ActivationTalk                 = 17,
        UseMessage                     = 18,
        ItemHeritageGroupRestriction   = 19,
        PluralName                     = 20,
        [AssessmentProperty]
        MonarchsTitle                  = 21,
        ActivationFailure              = 22,
        [AssessmentProperty]
        ScribeAccount                  = 23,
        TownName                       = 24,
        [AssessmentProperty]
        CraftsmanName                  = 25,
        UsePkServerError               = 26,
        ScoreCachedText                = 27,
        ScoreDefaultEntryFormat        = 28,
        ScoreFirstEntryFormat          = 29,
        ScoreLastEntryFormat           = 30,
        ScoreOnlyEntryFormat           = 31,
        ScoreNoEntry                   = 32,
        Quest                          = 33,
        GeneratorEvent                 = 34,
        [AssessmentProperty]
        PatronsTitle                   = 35,
        HouseOwnerName                 = 36,
        QuestRestriction               = 37,
        [AssessmentProperty]
        AppraisalPortalDestination     = 38,
        [AssessmentProperty]
        TinkerName                     = 39,
        [AssessmentProperty]
        ImbuerName                     = 40,
        HouseOwnerAccount              = 41,
        DisplayName                    = 42,
        [AssessmentProperty]
        DateOfBirth                    = 43,
        ThirdPartyApi                  = 44,
        KillQuest                      = 45,
        [Ephemeral]
        Afk                            = 46,
        [AssessmentProperty]
        AllegianceName                 = 47,
        AugmentationAddQuest           = 48,
        KillQuest2                     = 49,
        KillQuest3                     = 50,
        UseSendsSignal                 = 51,
        [AssessmentProperty]
        GearPlatingName                = 52,

        /* Custom Properties */
        PCAPRecordedCurrentMotionState = 8006,
        PCAPRecordedServerName         = 8031,
        PCAPRecordedCharacterName      = 8032,

        AllegianceMotd                 = 9001,
        AllegianceMotdSetBy            = 9002,
        AllegianceSpeakerTitle         = 9003,
        AllegianceSeneschalTitle       = 9004,
        AllegianceCastellanTitle       = 9005,
        GodState                       = 9006,
        TinkerLog                      = 9007,
        // On a Drift Network trainer NPC weenie: a comma/semicolon-separated list of ClassAbilityId Names the NPC
        // trains (e.g. "multishot,thorns"). Its presence flags the weenie as a class ability trainer; on use it
        // sells the next learnable rank of each listed skill for class ability points (see
        // ACE.Server.ClassAbilities.ClassAbilityTrainer). The exchanger NPC uses PropertyBool ClassAbilityExchanger.
        ClassAbilityTrainerAbilities        = 9008,
        // The weapon-mod system's own record of a weapon's CURRENT layer 1 tinker composition: a
        // comma-separated list of MaterialType ids, same format as TinkerLog 9007. REPLACED wholesale on
        // every reroll and every swap, never appended - it describes the weapon's current state, never its
        // history. See ACE.Server.WeaponMods.WeaponModManager.
        WeaponModTinkerLog                  = 9009,
        // World Events (WaffleACE, Docs/WorldEvents/TECH-DESIGN.md 2.13): the family id of a creature weenie
        // flagged PropertyBool.WorldEventCreature, read by the catalog scan and grouped on. Must equal an id in
        // Content/events/axes/families.json (lowercase [a-z0-9_]+).
        WorldEventFamily                    = 9010,
        // Objective Lock (ACE.Server.Entity.ObjectiveLock): the shared string name binding a gate to its
        // contributors, e.g. "lca_pentagon". Carried by the gate AND every contributor object; a contribution
        // is only routed to a gate whose ObjectiveLockKey matches the contributor's.
        ObjectiveLockKey                    = 9011,
        // Objective Lock: this contributor's token identity, passed as Contribute's tokenKey. When unset,
        // defaults to the contributor's own guid - this is what makes "six creatures in a room" six distinct
        // tokens with no per-creature authoring needed, since each creature's guid is already unique.
        ObjectiveLockToken                  = 9012,
        // Pick-up speed quest boon gems (Docs/Plans/pickup-boon-plan.md): on a Gem weenie (WeenieType 38),
        // the boon key this gem claims when used, e.g. "Firecut" - must match PickupBoon_<Key>'s suffix
        // exactly and satisfy Player.IsValidPickupBoonKey. See Gem.UsePickupBoonGem / Player_PickupBoons.cs.
        PickupBoonKey                       = 9013,

        /// <summary>
        /// Threads (WaffleACE): the Thread Gem's generator inputs as one string, see
        /// ACE.Server.ThreadDungeons.DungeonGemSpec. Presence on a Gem weenie routes Gem.UseGem to the
        /// dungeon-gem handler.
        /// </summary>
        DungeonGemSpec                     = 9014,

        /// <summary>
        /// Monster combat effects (WaffleACE, Docs/MonsterEffects/DESIGN.md): every combat effect a monster
        /// weenie carries, as one authored string. Records are separated by ';'; within a record the first
        /// whitespace-separated token is the effect kind and every remaining token is key=value, e.g.
        /// "flatdamage type=fire amount=30 chance=0.35; leech vital=health pct=0.12".
        ///
        /// Parsed by ACE.Server.MonsterEffects.MonsterEffectParser once per (wcid, string) and cached, so a
        /// landblock full of one wcid parses once. Absence leaves Creature.MonsterEffects null, which is the
        /// single null check every combat hot path makes.
        /// </summary>
        MonsterCombatEffects               = 9015,

        /// <summary>
        /// Threads (WaffleACE): the character's rolling ring of the GEM LEVELS of their last
        /// SurveyLevelRing.Depth filed daily surveys, newest first,
        /// as "v1|lv=275,185,...". Written by ThreadDungeonManager.RecordSurvey at file time and read by
        /// SurveyArchivistStation, which scales the daily-survey reward to the average level of the surveys
        /// the tier being paid actually covers. Absent or unparseable reads as an EMPTY ring, which pays the
        /// unscaled (ratio 1.0) reward - the grandfather clause for characters who filed before this shipped.
        /// </summary>
        DungeonSurveyLevels                = 9016,

        /// <summary>
        /// One-time-per-character class ability point grants (WaffleACE,
        /// Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md section 6 "Trophy"): on a consumable that already
        /// carries PropertyInt.ClassAbilityPointValue, the quest registry row name that records the claim.
        ///
        /// PRESENCE is what makes that grant one-time; absence leaves the item repeatable, which is what
        /// every CAP consumable shipped before this (the Proving Grounds and Meridian Commendations) needs.
        /// The value must match [A-Za-z0-9_]+ - QuestManager parses '@' and '%' as completion/format
        /// markers, so neither may appear in an authored name.
        ///
        /// Read by Gem.UseGem via Player.TryBeginOneTimeClassAbilityGrant; the points themselves are still
        /// paid by Player.GrantClassAbilityPoints, which is the only sanctioned way to move
        /// AvailableClassAbilityPoints and TotalClassAbilityPointsEarned together.
        /// </summary>
        ClassAbilityGrantQuest             = 9017,

        /// <summary>
        /// One-time-per-character UseCreateItem payout gate (code review fix, RoZ round 11,
        /// Docs/Marae-Lassel/quests): on a WeenieType.Gem (38) weenie that ALSO carries
        /// PropertyDataId.UseCreateItem, the quest registry row name that gates how many times its
        /// payout may be claimed, independent of how many copies of the gem itself the character
        /// holds or where they came from.
        ///
        /// This is a SEPARATE concern from PropertyString.Quest (33) on the same item, which (via
        /// Player_Inventory.VerifyQuest) limits how many copies of the item a character may RECEIVE.
        /// A generator can therefore stock several copies to stop one character stripping a
        /// container empty (the Quest 33 gate), while this property still limits the payout itself
        /// to once per character regardless of how many copies slip through - closing the pooling
        /// exploit where several legitimately-claimed copies are handed to one character and each
        /// used in turn.
        ///
        /// Read by Gem.UseGem via Player.TryBeginOneTimeItemGrant/CompleteOneTimeItemGrant
        /// (Source/ACE.Server/Entity/OneTimeItemGrant.cs), which use QuestManager.CanSolve/Stamp -
        /// the same primitives Player_Inventory.VerifyQuest uses - so the quest row's own
        /// min_Delta/max_Solves genuinely govern the payout cadence. Stamped only AFTER
        /// HandleUseCreateItem successfully creates and places the payout items, so a refused
        /// creation (full pack, no inventory slots) never burns the character's claim. The value
        /// must match [A-Za-z0-9_]+, the same shape as ClassAbilityGrantQuest above, for the same
        /// reason (QuestManager parses '@' and '%' as completion/format markers).
        /// </summary>
        UseCreateGrantQuest                = 9018,
        /// <summary>
        /// Comma-separated decimal body-part indices a worn item's ClothingTable must NOT apply,
        /// e.g. "16" or "12,15,16". Whitespace around tokens is tolerated; invalid or out-of-range
        /// (outside 0-255) tokens are ignored. An excluded part is left out of the item's coverage,
        /// so it falls back to the wearer's own body (and to any other equipped item that also
        /// covers it) instead of being painted over by this item's ClothingTable. A non-empty set
        /// also clips the item's sub-palette ranges (both its ClothingTable ranges and its own
        /// weenie palette rows) so none of them touch the wearer's base-appearance palette region
        /// [0x0, 0x28) - skin, hair, eyes - a range straddling that boundary keeps only the part at
        /// or above 0x28. Those colors belong to the character, not to a reused ClothingTable. Read
        /// in Creature.CalculateObjDesc.
        /// </summary>
        ClothingPartExclusions             = 9019,
        /// <summary>
        /// Per-part MODEL swaps a worn item applies to its wearer: comma-separated "part:0xGFXOBJ"
        /// pairs, e.g. "0:0x01003F99,1:0x01003F9A". Whitespace is tolerated; malformed pairs, parts
        /// above 255 and ids outside the GfxObj range 0x01000000-0x01FFFFFF are ignored; a repeated
        /// part keeps its last pair; at wear time an id missing from client_portal.dat is dropped
        /// (warned once). If no pair survives, the item is worn exactly as it would be without the
        /// property. Lets an item dress the wearer in meshes no ClothingTable carries
        /// (a creature's own baked setup, such as Asheron's 0x020016CB). Read only in
        /// Creature.CalculateObjDesc for an equipped item: each pair becomes an AnimPartChange and a
        /// covered part, after the item's ClothingTable effects (whose entry for an overridden part is
        /// skipped) and before the naked-part fill. Parts in the item's ClothingPartExclusions are
        /// skipped. Deliberately a separate property rather than weenie_properties_anim_part rows,
        /// because WorldObject.CalculateObjDesc applies those rows to the item's OWN ground/pack model.
        /// </summary>
        WornModelOverrides                 = 9020,

        /// <summary>
        /// Wave encounters (WaffleACE, ACE.Server.WaveEncounters): on a wave-encounter anchor, the kill
        /// quest name the runner assigns at RUNTIME to the last creature left alive in the final wave, so
        /// the player who kills it is credited through the ordinary KillQuest (PropertyString 45) path. No
        /// creature in the final wave carries the quest statically. Absent means no clear credit.
        /// </summary>
        WaveEncounterClearQuest            = 9021,

        /// <summary>
        /// PvP Template Facets (Docs/Pvp/TEMPLATES.md): on a player, the JSON restore record written when a
        /// template match is applied. Its PRESENCE is the definition of templated; it is never sent to the client
        /// (not SendOnLogin). Read and written only by Player_PvpTemplate.cs.
        /// </summary>
        PvpTemplateRestore                 = 9022,

        /// <summary>
        /// PvP Template Facets (Docs/Pvp/TEMPLATES.md "Commands"): on a player, the arena template key chosen with
        /// /arena template or /arena join, remembered across relogs and used by every later join that names no key.
        /// Validated against the enabled templates at join, accept and dispatch, so a stale key only refuses the join.
        /// </summary>
        PvpTemplatePreference              = 9023,
    }
}
