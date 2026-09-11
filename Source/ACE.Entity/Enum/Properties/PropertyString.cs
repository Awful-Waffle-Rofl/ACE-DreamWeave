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
    }
}
