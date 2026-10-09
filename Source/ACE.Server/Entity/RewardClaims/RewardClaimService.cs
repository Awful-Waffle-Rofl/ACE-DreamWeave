using System;
using System.Net;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Server.Managers;

namespace ACE.Server.Entity.RewardClaims
{
    /// <summary>Which emote set the ClaimRewardOnce emote should run next.</summary>
    public enum RewardClaimBranch
    {
        /// <summary>Run nothing. The chain stops here; <see cref="RewardClaimDecision.Message"/> says why.</summary>
        None,
        TestSuccess,
        TestFailure
    }

    /// <summary>Everything the emote needs to act on a claim: the branch, an optional transient message, and the logged result token.</summary>
    public sealed class RewardClaimDecision
    {
        public RewardClaimBranch Branch { get; }
        public string Message { get; }
        public string LogResult { get; }

        public RewardClaimDecision(RewardClaimBranch branch, string message, string logResult)
        {
            Branch = branch;
            Message = message;
            LogResult = logResult;
        }
    }

    /// <summary>The live inputs of one claim, resolved by the emote. The two callbacks are invoked only when the claim actually runs.</summary>
    public sealed class RewardClaimRequest
    {
        public string ClaimKey { get; set; }
        public uint EmoterWcid { get; set; }
        public uint AccountId { get; set; }
        public uint CharacterId { get; set; }
        public string CharacterName { get; set; }
        public IPAddress Address { get; set; }

        /// <summary>IpLimitManager.IsExempt(session) in production. Null reads as not exempt.</summary>
        public Func<bool> IsExempt { get; set; }

        /// <summary>Whether the player can hold one of the reward wcid. Null reads as room available.</summary>
        public Func<uint, bool> HasRoomForReward { get; set; }
    }

    /// <summary>
    /// The once-per-account / once-per-IP claim gate behind EmoteType.ClaimRewardOnce. Synchronous by
    /// design: the emote branches in the same call stack as the claim, so nothing can interleave between
    /// the database adjudicating the claim and the reward branch starting.
    /// </summary>
    public static class RewardClaimService
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Kill switch. Defaults TRUE. Off = every claim is ungated with no database call.</summary>
        public const string PropertyKey = "reward_claim_once_enabled";

        public const string NoRoomMessage = "You need a free pack slot before the hammer can be handed over.";
        public const string StoreFailedMessage = "The assay ledger could not be reached. Try the turn-in again shortly.";

        /// <summary>A SpeedSeason claim made in a gap between seasons. Nothing is paid or recorded.</summary>
        public const string NoActiveSeasonMessage = "The season ladder is between seasons. Come back when the next one opens.";

        public const string ResultGranted = "granted";
        public const string ResultDeniedAccount = "denied:account";
        public const string ResultDeniedIp = "denied:ip";
        public const string ResultUngatedDisabled = "ungated:disabled";
        public const string ResultUngatedUnlisted = "ungated:unlisted";
        public const string ResultUngatedWrongNpc = "ungated:wrong-npc";
        public const string ResultNoRoom = "noroom";
        public const string ResultFailed = "failed";

        private static IRewardClaimStore store = new ShardRewardClaimStore();

        /// <summary>Test seam for the kill-switch read; a test that reassigns it must restore it.</summary>
        internal static Func<bool> EnabledSource = ReadEnabledFromProperties;

        /// <summary>Test seam for the per-attempt token.</summary>
        internal static Func<string> TokenSource = NewToken;

        /// <summary>Test seam for ClaimPeriod.SpeedSeason: the active speed_season id, or null in a gap between seasons.</summary>
        internal static Func<int?> ActiveSpeedSeasonSource = ReadActiveSpeedSeason;

        /// <summary>Test seam for ClaimPeriod.UtcDay: the current UTC time.</summary>
        internal static Func<DateTime> UtcNowSource = ReadUtcNow;

        public static bool Enabled => (EnabledSource ?? ReadEnabledFromProperties)();

        /// <summary>Swaps the store (null restores the shard store) and restores the other seams to production.</summary>
        internal static void ResetForTesting(IRewardClaimStore testStore)
        {
            store = testStore ?? new ShardRewardClaimStore();
            EnabledSource = ReadEnabledFromProperties;
            TokenSource = NewToken;
            ActiveSpeedSeasonSource = ReadActiveSpeedSeason;
            UtcNowSource = ReadUtcNow;
        }

        /// <summary>
        /// The same active-season read the Pace-Master's InqInt64Stat uses (EmoteManager, via
        /// Player.GetSeasonAwareSpeedBest), so the claim is filed under the season whose tiers the herald is
        /// paying. Never throws: an unreadable registry reads as no season, which fails the claim closed.
        /// </summary>
        private static int? ReadActiveSpeedSeason()
        {
            try
            {
                return SpeedSeasonManager.GetActiveSeason()?.Id;
            }
            catch (Exception ex)
            {
                log.Error("[REWARDCLAIM] could not read the active speed season; season-scoped claims will fail closed", ex);
                return null;
            }
        }

        private static DateTime ReadUtcNow() => DateTime.UtcNow;

        /// <summary>
        /// The claim_Key this entry stores right now: the bare key for Forever, otherwise the key plus the
        /// current period's suffix. NULL when the period cannot be resolved (no active speed season).
        /// </summary>
        public static string CurrentStoredKey(RewardClaimEntry entry) => CurrentStoredKey(entry, out _);

        /// <summary>As CurrentStoredKey; <paramref name="noActiveSeason"/> is TRUE when a SpeedSeason key is unresolvable because no season is active.</summary>
        internal static string CurrentStoredKey(RewardClaimEntry entry, out bool noActiveSeason)
        {
            noActiveSeason = false;

            if (entry == null)
                return null;

            int? seasonId = null;

            if (entry.Period == ClaimPeriod.SpeedSeason)
            {
                seasonId = (ActiveSpeedSeasonSource ?? ReadActiveSpeedSeason)();
                noActiveSeason = !seasonId.HasValue;
            }

            var utcNow = entry.Period == ClaimPeriod.UtcDay ? (UtcNowSource ?? ReadUtcNow)() : default;

            return RewardClaimRules.StoredClaimKey(entry, seasonId, utcNow);
        }

        /// <summary>
        /// The production flag read. PropertyManager.GetBool dereferences DatabaseManager.ShardConfig on an
        /// uncached read, which throws in a unit-test host (the same hazard EmoteGivePreflight wraps). An
        /// unreadable flag reads as its default, TRUE: the gate stays armed, and a gate that cannot reach
        /// its store already refuses safely with StoreFailed rather than paying out.
        /// </summary>
        internal static bool ReadEnabledFromProperties()
        {
            try
            {
                return PropertyManager.GetBool(PropertyKey).Item;
            }
            catch (Exception ex)
            {
                log.Error($"[REWARDCLAIM] could not read the {PropertyKey} property; treating the gate as on", ex);
                return true;
            }
        }

        private static string NewToken() => Guid.NewGuid().ToString("N");

        /// <summary>The whole decision for one ClaimRewardOnce emote, including its one log line.</summary>
        public static RewardClaimDecision Evaluate(RewardClaimRequest request)
        {
            if (request == null)
                return new RewardClaimDecision(RewardClaimBranch.None, StoreFailedMessage, ResultFailed);

            var address = request.Address?.ToString();

            switch (RewardClaimRules.Precheck(Enabled, request.ClaimKey, request.EmoterWcid, request.AccountId))
            {
                case RewardClaimPrecheck.UngatedDisabled:
                    LogDecision(ResultUngatedDisabled, request, address, null);
                    return new RewardClaimDecision(RewardClaimBranch.TestSuccess, null, ResultUngatedDisabled);

                case RewardClaimPrecheck.UngatedUnlistedKey:
                    LogDecision(ResultUngatedUnlisted, request, address, null, "the key is not on RewardClaimAllowlist; content bug, passing through ungated");
                    return new RewardClaimDecision(RewardClaimBranch.TestSuccess, null, ResultUngatedUnlisted);

                case RewardClaimPrecheck.UngatedWrongNpc:
                    LogDecision(ResultUngatedWrongNpc, request, address, null, "the emoting NPC does not own this key; content bug, passing through ungated");
                    return new RewardClaimDecision(RewardClaimBranch.TestSuccess, null, ResultUngatedWrongNpc);

                case RewardClaimPrecheck.NoAccount:
                    LogDecision(ResultFailed, request, address, null, "no account id");
                    RewardClaimAllowlist.TryGet(request.ClaimKey, out var noAccountEntry);
                    return new RewardClaimDecision(RewardClaimBranch.None, noAccountEntry?.StoreFailedMessage ?? StoreFailedMessage, ResultFailed);
            }

            RewardClaimAllowlist.TryGet(request.ClaimKey, out var entry);

            var noRoomMessage = entry.NoRoomMessage ?? NoRoomMessage;
            var storeFailedMessage = entry.StoreFailedMessage ?? StoreFailedMessage;

            // The claim period is resolved FIRST, before any callback or store call: a periodic key whose
            // period cannot be resolved (no active speed season) fails closed with no branch, exactly like a
            // store failure, so it can neither pay nor burn a claim under the wrong period.
            string storedKey;
            bool noActiveSeason;

            try
            {
                storedKey = CurrentStoredKey(entry, out noActiveSeason);
            }
            catch (Exception ex)
            {
                LogDecision(ResultFailed, request, address, null, $"resolving the {entry.Period} claim period threw: {ex.Message}");
                return new RewardClaimDecision(RewardClaimBranch.None, storeFailedMessage, ResultFailed);
            }

            if (storedKey == null && noActiveSeason)
            {
                LogDecision(ResultFailed, request, address, null, "no active speed season; the season-scoped claim fails closed");
                return new RewardClaimDecision(RewardClaimBranch.None, NoActiveSeasonMessage, ResultFailed);
            }

            if (storedKey == null)
            {
                LogDecision(ResultFailed, request, address, null, $"the {entry.Period} claim period could not be resolved (the stored key would exceed {RewardClaimRules.MaxStoredKeyLength} chars, or the period is unknown); failing closed");
                return new RewardClaimDecision(RewardClaimBranch.None, storeFailedMessage, ResultFailed);
            }

            bool hasRoom;
            bool exempt;

            try
            {
                hasRoom = request.HasRoomForReward == null || request.HasRoomForReward(entry.RewardWcid);
            }
            catch (Exception ex)
            {
                LogDecision(ResultFailed, request, address, null, $"the pack-room check threw: {ex.Message}", storedKey);
                return new RewardClaimDecision(RewardClaimBranch.None, storeFailedMessage, ResultFailed);
            }

            if (!hasRoom)
            {
                LogDecision(ResultNoRoom, request, address, null, null, storedKey);
                return new RewardClaimDecision(RewardClaimBranch.None, noRoomMessage, ResultNoRoom);
            }

            try
            {
                exempt = request.IsExempt != null && request.IsExempt();
            }
            catch (Exception ex)
            {
                LogDecision(ResultFailed, request, address, null, $"the IP exemption check threw: {ex.Message}", storedKey);
                return new RewardClaimDecision(RewardClaimBranch.None, storeFailedMessage, ResultFailed);
            }

            var ipKey = RewardClaimRules.NormalizeIpKey(request.Address, exempt);

            var outcome = TryClaim(storedKey, request.AccountId, request.CharacterId, entry.NpcWcid, ipKey, address);

            switch (outcome)
            {
                case RewardClaimOutcome.Granted:
                    LogDecision(ResultGranted, request, address, ipKey ?? "exempt", null, storedKey);
                    return new RewardClaimDecision(RewardClaimBranch.TestSuccess, null, ResultGranted);

                case RewardClaimOutcome.DeniedAccount:
                    LogDecision(ResultDeniedAccount, request, address, ipKey ?? "exempt", null, storedKey);
                    return new RewardClaimDecision(RewardClaimBranch.TestFailure, null, ResultDeniedAccount);

                case RewardClaimOutcome.DeniedIp:
                    LogDecision(ResultDeniedIp, request, address, ipKey, null, storedKey);
                    return new RewardClaimDecision(RewardClaimBranch.TestFailure, null, ResultDeniedIp);

                default:
                    LogDecision(ResultFailed, request, address, ipKey ?? "exempt", "the claim store failed", storedKey);
                    return new RewardClaimDecision(RewardClaimBranch.None, storeFailedMessage, ResultFailed);
            }
        }

        /// <summary>
        /// One claim attempt against the store: insert, and on a duplicate read the conflicting rows back so
        /// <see cref="RewardClaimRules.Interpret"/> can tell this attempt's own row from somebody else's.
        /// Any store exception is StoreFailed.
        /// </summary>
        public static RewardClaimOutcome TryClaim(string claimKey, uint accountId, uint characterId, uint npcWcid, string ipKey, string ipAddress)
        {
            var currentStore = store;
            var token = (TokenSource ?? NewToken)();

            try
            {
                var insert = currentStore.TryInsert(new RewardClaim
                {
                    ClaimKey = claimKey,
                    AccountId = accountId,
                    IpKey = ipKey,
                    IpAddress = ipAddress,
                    CharacterId = characterId,
                    NpcWcid = npcWcid,
                    ClaimToken = token,
                });

                if (insert != RewardClaimInsertResult.Duplicate)
                    return RewardClaimRules.Interpret(insert, null, token, accountId, ipKey);

                if (!currentStore.TryGetClaims(claimKey, accountId, ipKey, out var conflicts))
                    return RewardClaimOutcome.StoreFailed;

                return RewardClaimRules.Interpret(insert, conflicts, token, accountId, ipKey);
            }
            catch (Exception ex)
            {
                log.Error($"[REWARDCLAIM] TryClaim threw for key {claimKey}, account {accountId}", ex);
                return RewardClaimOutcome.StoreFailed;
            }
        }

        private static void LogDecision(string result, RewardClaimRequest request, string address, string ipKey, string reason = null, string storedKey = null)
        {
            var storedPart = storedKey != null && !string.Equals(storedKey, request.ClaimKey, StringComparison.Ordinal) ? $" storedKey={storedKey}" : "";
            var line = $"[REWARDCLAIM] result={result} key={request.ClaimKey ?? "-"}{storedPart} account={request.AccountId} character={request.CharacterName ?? "-"} (0x{request.CharacterId:X8}) ip={address ?? "-"} ipKey={ipKey ?? "-"} npc={request.EmoterWcid}{(reason != null ? $" reason={reason}" : "")}";

            switch (result)
            {
                case ResultUngatedUnlisted:
                case ResultUngatedWrongNpc:
                case ResultFailed:
                    log.Error(line);
                    break;

                default:
                    log.Info(line);
                    break;
            }
        }

        /// <summary>The store the admin command uses; the same one the gate writes through.</summary>
        internal static IRewardClaimStore Store => store;
    }
}
