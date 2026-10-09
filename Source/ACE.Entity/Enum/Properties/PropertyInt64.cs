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

        /* Quest stamps (WaffleACE): count of distinct quest names stamped anywhere on the account, derived from
           the union of the per-character QuestStampSeen_ ledger rows - so two characters completing the same
           quest are worth one, not two. Computed at login and incremented live only for a quest no character on
           the account has been stamped for before; read by NPC InqInt64Stat emotes (stat 9019) */
        [Ephemeral]
        AccountQuestStampCount = 9019,

        /* Survival challenge (WaffleACE): a player's best seconds-survived in the instanced survival arena run */
        BestSurvivalScore     = 9020,

        /* Wave challenge (WaffleACE): LEGACY. Superseded by BestWaveScoreCenti (9022). Read-only fallback for
           characters scored before fractional wave scoring shipped - never written any more. */
        BestWaveScore         = 9021,

        /* Wave challenge (WaffleACE): a player's best wave-gauntlet score in HUNDREDTHS of a wave, i.e.
           (fully-cleared waves * 100) + the whole-percent of the live wave's total health destroyed when the run
           ended (capped at 99 so a partial wave can never tie a full clear). Persisted incrementally on every
           wave clear and again at run end. GetBestWaveScoreCenti falls back to BestWaveScore * 100 for characters
           who have never written this property, so older ladder entries are not lost. */
        BestWaveScoreCenti    = 9022,

        /* Proving Grounds: Speed (WaffleACE, DreamWeave, 2026-08-26): personal best for the season named by
           PropertyInt.SpeedChallengeSeasonId, in CENTISECONDS (hundredths of a second), matching the
           BestWaveScoreCenti convention. Cache for personal-best messaging only - derived from the
           character_speed_run shard table, never the authority. LOWER IS BETTER, unlike every other
           Proving Grounds board. */
        BestSpeedRunCenti     = 9023,

        /* Wave challenge (WaffleACE): this player's best FULL-CLEAR time of the wave gauntlet, in
           CENTISECONDS (hundredths of a second), matching the BestWaveScoreCenti/BestSpeedRunCenti
           convention. Written ONLY on a full clear (every wave cleared), never on a partial run, and only
           when it improves on the stored value. LOWER IS BETTER. Tiebreak for the wave board: players tied
           on BestWaveScoreCenti rank by this ascending, and a tied player with no recorded time (every full
           clear before this shipped - there is no historical data to backfill) ranks below every tied player
           who has one. */
        BestWaveClearTimeCenti = 9024,
    }
}
