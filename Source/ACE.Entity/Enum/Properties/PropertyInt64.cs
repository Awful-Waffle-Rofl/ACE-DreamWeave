using System.ComponentModel;

namespace ACE.Entity.Enum.Properties
{
    // No properties are sent to the client unless they featured an attribute.
    // SendOnLogin gets sent to players in the PlayerDescription event
    // AssessmentProperty gets sent in successful appraisal
    public enum PropertyInt64 : ushort
    {
        Undef                 = 0,
        [SendOnLogin]
        TotalExperience       = 1,
        [SendOnLogin]
        AvailableExperience   = 2,
        [AssessmentProperty]
        AugmentationCost      = 3,
        [AssessmentProperty]
        ItemTotalXp           = 4,
        [AssessmentProperty]
        ItemBaseXp            = 5,
        [SendOnLogin]
        AvailableLuminance    = 6,
        [SendOnLogin]
        MaximumLuminance      = 7,
        InteractionReqs       = 8,

        /* Custom Properties */
        AllegianceXPCached    = 9000,
        AllegianceXPGenerated = 9001,
        AllegianceXPReceived  = 9002,
        VerifyXp              = 9003,

        /* Banking system (WaffleACE) - ids mirror Conquest-ACE for data interop */
        BankedPyreals         = 9004,
        BankedLuminance       = 9005,
        BankedLegendaryKeys   = 9015,
        BankedPromissoryNotes = 9016,

        /* DPS challenge (WaffleACE): a player's best recorded total damage in the instanced arena run */
        BestDpsScore          = 9017,

        /* Quest stamps (WaffleACE): monotonic count of distinct eligible quests this character has ever been
           stamped for; backfilled at first login after the feature shipped; never decremented (Erase/Decrement
           do not reduce it) and never raised twice by the same quest - the QuestStampSeen_ ledger rows in the
           quest registry, not the quest rows themselves, are what record which quests have already been paid */
        QuestStampCount       = 9018,

        /* Quest stamps (WaffleACE): sum of QuestStampCount across the account, computed at login and incremented
           live; read by NPC InqInt64Stat emotes (stat 9019) */
        [Ephemeral]
        AccountQuestStampCount = 9019,

        /* Survival challenge (WaffleACE): a player's best seconds-survived in the instanced survival arena run */
        BestSurvivalScore     = 9020,

        /* Wave challenge (WaffleACE): a player's highest fully-cleared wave in the instanced wave gauntlet.
           Persisted incrementally, on every wave clear, so a crash or a forfeit still keeps the waves that
           were genuinely cleared */
        BestWaveScore         = 9021,
    }
}
