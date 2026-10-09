using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ACE.Server.Entity.RewardClaims
{
    /// <summary>
    /// How long one claim of a key lasts before the same account or IP may claim it again. Resolved to a
    /// suffix on the stored claim_Key in exactly one place, RewardClaimRules.StoredClaimKey.
    /// </summary>
    public enum ClaimPeriod
    {
        /// <summary>Once ever. Stored key = Key, no suffix.</summary>
        Forever,

        /// <summary>Once per speed season. Stored key = Key + "#S" + the active speed_season id.</summary>
        SpeedSeason,

        /// <summary>Once per UTC calendar day. Stored key = Key + "#D" + yyyyMMdd (UTC).</summary>
        UtcDay
    }

    /// <summary>One allowlisted claim: the only NPC allowed to run it, and the reward it guards.</summary>
    public sealed class RewardClaimEntry
    {
        public string Key { get; }

        /// <summary>The wcid of the NPC whose emote chain may run this claim. Any other emoter is ungated.</summary>
        public uint NpcWcid { get; }

        /// <summary>The reward the claim guards. Used only for the pre-claim pack-room check.</summary>
        public uint RewardWcid { get; }

        /// <summary>
        /// The claim period. Anything but Forever stores Key plus a period suffix in reward_claim
        /// (RewardClaimRules.StoredClaimKey), so the gate is once per account and once per IP PER PERIOD.
        /// Content and this allowlist always use the unsuffixed Key.
        /// </summary>
        public ClaimPeriod Period { get; }

        /// <summary>Transient text when the player cannot hold the reward. Null = RewardClaimService.NoRoomMessage.</summary>
        public string NoRoomMessage { get; }

        /// <summary>Transient text when the claim cannot be recorded. Null = RewardClaimService.StoreFailedMessage.</summary>
        public string StoreFailedMessage { get; }

        public RewardClaimEntry(string key, uint npcWcid, uint rewardWcid, ClaimPeriod period = ClaimPeriod.Forever, string noRoomMessage = null, string storeFailedMessage = null)
        {
            Key = key;
            NpcWcid = npcWcid;
            RewardWcid = rewardWcid;
            Period = period;
            NoRoomMessage = noRoomMessage;
            StoreFailedMessage = storeFailedMessage;
        }
    }

    /// <summary>
    /// The ONLY rewards the ClaimRewardOnce emote may gate, hardcoded on purpose: content can reference
    /// the emote anywhere, but a key that is not listed here, or a listed key run from any NPC other than
    /// its own, is logged and passes straight to TestSuccess without touching the database. So the gate
    /// can never change the behaviour of any other content.
    ///
    /// Keys are compared ordinally (case-sensitive), matching the ascii_bin claim_Key column.
    /// Assay Row wcids verified against each NPC's Give emote row in Content/sql/weenies/10025xx on 2026-09-15.
    /// Proving Grounds wcids verified against each herald's TestSuccess "@claim" Give row on 2026-10-02:
    /// tiers 1-3 Give wcid 20630 (stacks 10/25/100).
    ///
    /// There is deliberately NO tier 4 entry for Attack, Defense or Wave. Tier 4 pays a Proving Grounds
    /// Commendation (wcid 1000304), which carries the class ability point property, and the owner's ruling
    /// is that the point is earned per character while the MMD tiers stay per account and per connection.
    /// Tier 4 is bounded instead by its own per-character quest stamp: the herald's chain runs
    /// InqQuest on the tier stamp first, and EmoteManager sets success = hasQuest and not canSolve, so with
    /// max_Solves 1 on the quest row a second visit routes to QuestSuccess (turn and tell only) and never
    /// reaches the Give. Adding a tier 4 key back here would re-impose the account/IP cap the ruling removed.
    /// </summary>
    public static class RewardClaimAllowlist
    {
        public const string TigerEyeHammer = "AssayRowTigerEyeHammer";
        public const string TourmalineHammer = "AssayRowTourmalineHammer";
        public const string SerpentineHammer = "AssayRowSerpentineHammer";
        public const string AmethystHammer = "AssayRowAmethystHammer";
        public const string ObsidianHammer = "AssayRowObsidianHammer";

        // Proving Grounds herald tiers. Each key is the tier's per-character stamp + "@claim".
        // Tiers 1-3 only: tier 4 is per character and is not claim-gated (see the class remarks).
        public const string ProvingGroundsAttackTier1 = "ProvingGroundsAttackTier1@claim";
        public const string ProvingGroundsAttackTier2 = "ProvingGroundsAttackTier2@claim";
        public const string ProvingGroundsAttackTier3 = "ProvingGroundsAttackTier3@claim";
        public const string ProvingGroundsDefenseTier1 = "ProvingGroundsDefenseTier1@claim";
        public const string ProvingGroundsDefenseTier2 = "ProvingGroundsDefenseTier2@claim";
        public const string ProvingGroundsDefenseTier3 = "ProvingGroundsDefenseTier3@claim";
        public const string ProvingGroundsWaveTier1 = "ProvingGroundsWaveTier1@claim";
        public const string ProvingGroundsWaveTier2 = "ProvingGroundsWaveTier2@claim";
        public const string ProvingGroundsWaveTier3 = "ProvingGroundsWaveTier3@claim";
        public const string ProvingGroundsSpeedTier1 = "ProvingGroundsSpeedTier1@claim";
        public const string ProvingGroundsSpeedTier2 = "ProvingGroundsSpeedTier2@claim";
        public const string ProvingGroundsSpeedTier3 = "ProvingGroundsSpeedTier3@claim";

        public const uint MasterAtArmsOrdwinCray = 1000303;
        public const uint WardMasterOsricCray = 1000306;
        public const uint FieldMarshalOswynCray = 1001551;
        public const uint PaceMasterOddaCray = 1003002;

        public const uint TradeNoteMmd = 20630;
        public const uint ProvingGroundsCommendation = 1000304;

        public const string ProvingGroundsNoRoomMessage = "You need a free pack slot before the reward can be handed over.";
        public const string ProvingGroundsStoreFailedMessage = "The Proving Grounds ledger could not be reached. Try again shortly.";

        public static readonly IReadOnlyDictionary<string, RewardClaimEntry> Entries = Build();

        private static RewardClaimEntry ProvingGrounds(string key, uint npcWcid, uint rewardWcid, ClaimPeriod period = ClaimPeriod.Forever)
            => new RewardClaimEntry(key, npcWcid, rewardWcid, period, ProvingGroundsNoRoomMessage, ProvingGroundsStoreFailedMessage);

        private static IReadOnlyDictionary<string, RewardClaimEntry> Build()
        {
            var entries = new[]
            {
                new RewardClaimEntry(TigerEyeHammer, 1002511, 1001918),   // Platewright Odell Marsh -> Tiger Eye Hammer
                new RewardClaimEntry(TourmalineHammer, 1002512, 1001910), // Edgewright Kenji Ashida -> Tourmaline Hammer
                new RewardClaimEntry(SerpentineHammer, 1002513, 1001916), // Ulgrath the Grain-Reader -> Serpentine Hammer
                new RewardClaimEntry(AmethystHammer, 1002514, 1001911),   // Slotwright Yusra Tannim -> Amethyst Hammer
                new RewardClaimEntry(ObsidianHammer, 1002516, 1001912),   // Glasswright Rin Kuroda -> Obsidian Hammer

                // Master-at-Arms Ordwin Cray (Attack). Never resets: fixed keys.
                ProvingGrounds(ProvingGroundsAttackTier1, MasterAtArmsOrdwinCray, TradeNoteMmd),
                ProvingGrounds(ProvingGroundsAttackTier2, MasterAtArmsOrdwinCray, TradeNoteMmd),
                ProvingGrounds(ProvingGroundsAttackTier3, MasterAtArmsOrdwinCray, TradeNoteMmd),

                // Ward-Master Osric Cray (Defense). Never resets: fixed keys.
                ProvingGrounds(ProvingGroundsDefenseTier1, WardMasterOsricCray, TradeNoteMmd),
                ProvingGrounds(ProvingGroundsDefenseTier2, WardMasterOsricCray, TradeNoteMmd),
                ProvingGrounds(ProvingGroundsDefenseTier3, WardMasterOsricCray, TradeNoteMmd),

                // Field-Marshal Oswyn Cray (Wave). Never resets: fixed keys.
                ProvingGrounds(ProvingGroundsWaveTier1, FieldMarshalOswynCray, TradeNoteMmd),
                ProvingGrounds(ProvingGroundsWaveTier2, FieldMarshalOswynCray, TradeNoteMmd),
                ProvingGrounds(ProvingGroundsWaveTier3, FieldMarshalOswynCray, TradeNoteMmd),

                // Pace-Master Odda Cray (Speed). Player.SpeedRewardClaimQuests erases the per-character
                // stamps at every season rollover, so these keys are ClaimPeriod.SpeedSeason: once per IP per season.
                ProvingGrounds(ProvingGroundsSpeedTier1, PaceMasterOddaCray, TradeNoteMmd, ClaimPeriod.SpeedSeason),
                ProvingGrounds(ProvingGroundsSpeedTier2, PaceMasterOddaCray, TradeNoteMmd, ClaimPeriod.SpeedSeason),
                ProvingGrounds(ProvingGroundsSpeedTier3, PaceMasterOddaCray, TradeNoteMmd, ClaimPeriod.SpeedSeason),
            };

            var map = new Dictionary<string, RewardClaimEntry>(StringComparer.Ordinal);

            foreach (var entry in entries)
                map.Add(entry.Key, entry);

            return new ReadOnlyDictionary<string, RewardClaimEntry>(map);
        }

        public static bool TryGet(string key, out RewardClaimEntry entry)
        {
            entry = null;

            if (string.IsNullOrEmpty(key))
                return false;

            return Entries.TryGetValue(key, out entry);
        }
    }
}
