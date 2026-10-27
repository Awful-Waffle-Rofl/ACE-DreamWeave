using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>Rewards for Hold runs and the stages of their chain. Other World Events are never touched by these.</summary>
    public static partial class HoldSettings
    {
        public const string FailureRewardConsolation = "consolation", FailureRewardPartial = "partial", FailureRewardNone = "none";

        private static readonly string[] FailureRewards = { FailureRewardConsolation, FailureRewardPartial, FailureRewardNone };

        static partial void RegisterRewards(List<HoldSetting> r)
        {
            r.Add(TextSetting(GroupRewards, HoldKeys.ChainRewardStage1, "standard", HoldApplies.NextEvent, "chain.stageRewards", false,
                "Stage 1's reward profile (a rewards.json id).", g => g.Chain?.RewardForStage(1)));
            r.Add(TextSetting(GroupRewards, HoldKeys.ChainRewardStage2, "chain_final", HoldApplies.StageEnd, "chain.stageRewards", false,
                "Stage 2's reward profile.", g => g.Chain?.RewardForStage(2)));
            r.Add(TextSetting(GroupRewards, HoldKeys.ChainRewardFinal, "chain_final", HoldApplies.StageEnd, "chain.stageRewards", false,
                "The last stage's reward profile (wins over stage 2's when stage 2 is the last).",
                g => g.Chain?.RewardForStage(Math.Max(2, g.Chain?.MaxLength ?? 2))));

            r.Add(Int(GroupRewards, HoldKeys.ParticipantsPerCache, 8, 1, 100, HoldApplies.StageEnd, null, false,
                "Caches per player: one cache for every this many players present (1 = a cache each). Not set: the profile's participantsPerCache.",
                otherFile: "rewards.json"));
            r.Add(Num(GroupRewards, HoldKeys.MinContribution, 0, 0, 0.5, HoldApplies.Live, null, true,
                "The share of the stage's damage a player must deal for the crate; below it they get the coal prize (0 = off)."));
            r.Add(Tog(GroupRewards, HoldKeys.KeepStageRewardsOnFail, true, HoldApplies.StageEnd, null, true,
                "On (as today): an earlier stage's cache stays claimable if a later stage fails. Off: it closes when the chain fails."));
            r.Add(TextSetting(GroupRewards, HoldKeys.ChainFullBonus, "", HoldApplies.StageEnd, null, true,
                "The full-chain bonus: crate wcids added to a win on the chain's last stage, e.g. \"1002600,1002601\". " +
                "Not set: the profile's bonusCrateWcids (chain_final: a Hammer Crate and a Salvage Crate). \"none\" = no bonus."));
            r.Add(Pick(GroupRewards, HoldKeys.FailureReward, FailureRewardConsolation, FailureRewards, HoldApplies.Live, null, true,
                "What a failed stage's cache pays: consolation (the consolation crate, as today), partial (the success crate, no bonus) or none."));
        }

        /// <summary>A stage's reward profile set live: final for the last stage, else stage1 / stage2. Null when not set.</summary>
        public static string StageRewardOverride(int stage, int maxLength)
        {
            string key = null;

            if (stage >= 2 && stage >= maxLength && IsSet(HoldKeys.ChainRewardFinal))
                key = HoldKeys.ChainRewardFinal;
            else if (stage <= 1 && IsSet(HoldKeys.ChainRewardStage1))
                key = HoldKeys.ChainRewardStage1;
            else if (stage == 2 && IsSet(HoldKeys.ChainRewardStage2))
                key = HoldKeys.ChainRewardStage2;

            if (key == null)
                return null;

            var id = Text(key, "").Trim().ToLowerInvariant();

            return id.Length == 0 ? null : id;
        }

        /// <summary>The cache count rule's participants-per-cache for <paramref name="evt"/>: the setting for a Hold run or chain stage, else the profile's.</summary>
        public static int ParticipantsPerCacheFor(WorldEvent evt, int profile) =>
            evt != null && evt.IsHoldOrChain && IsSet(HoldKeys.ParticipantsPerCache) ? Integer(HoldKeys.ParticipantsPerCache, profile) : profile;

        /// <summary>What a failed stage's cache pays for <paramref name="evt"/>: the setting for a Hold run or chain stage, else consolation.</summary>
        public static string FailureRewardFor(WorldEvent evt) =>
            evt != null && evt.IsHoldOrChain ? Choice(HoldKeys.FailureReward, FailureRewardConsolation) : FailureRewardConsolation;

        /// <summary>
        /// The minimum contribution rule (world_events_hold_min_contribution) for a Hold run or chain stage: true when it is off,
        /// for any other run, or when the player dealt at least that share of the damage on the run.
        /// </summary>
        public static bool MeetsMinContribution(WorldEvent evt, uint characterGuid)
        {
            if (evt == null || !evt.IsHoldOrChain)
                return true;

            var min = Number(HoldKeys.MinContribution, 0);

            if (!(min > 0) || evt.Participation == null)
                return true;

            var records = evt.Participation.Records;
            var total = records.Sum(r => (double)Math.Max(0, r.Damage));
            var mine = evt.Participation.TryGetRecord(characterGuid, out var rec) ? rec.Damage : 0;

            return MeetsMinContribution(mine, total, min);
        }

        /// <summary>The rule (D6 - pure): with no damage dealt by anyone, everyone meets it.</summary>
        public static bool MeetsMinContribution(double mine, double total, double minShare) =>
            !(minShare > 0) || !(total > 0) || mine / total >= minShare - 1e-9;

        /// <summary>The full-chain bonus set live for the last stage of a chain (an empty list for "none"); null when not set or not the last stage.</summary>
        public static List<uint> FullChainBonusFor(WorldEvent evt)
        {
            if (evt == null || !evt.IsFinalChainStage || !IsSet(HoldKeys.ChainFullBonus))
                return null;

            return ParseWcidList(Text(HoldKeys.ChainFullBonus, ""), out _);
        }

        /// <summary>"1002600, 1002601" (D6 - pure); "none" is empty. <paramref name="bad"/> lists entries that are not wcids.</summary>
        public static List<uint> ParseWcidList(string raw, out List<string> bad)
        {
            bad = new List<string>();
            var result = new List<uint>();

            if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                return result;

            foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (uint.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wcid) && wcid != 0)
                    result.Add(wcid);
                else
                    bad.Add(part);
            }

            return result;
        }

        private static string CheckWcidList(string key, string raw)
        {
            ParseWcidList(raw, out var bad);
            return bad.Count == 0 ? null : $"{key}: {string.Join(", ", bad.Select(b => $"'{b}'"))} is not a wcid; skipped";
        }

        private static string CheckRewardId(string key, string raw)
        {
            var store = WorldEventManager.Store;
            var id = raw?.Trim().ToLowerInvariant();

            if (store == null || store.IsEmpty || string.IsNullOrEmpty(id))
                return null;

            return store.Rewards.ContainsKey(id) ? null : $"{key}='{raw}' is not in rewards.json; that stage pays the default";
        }
    }
}
