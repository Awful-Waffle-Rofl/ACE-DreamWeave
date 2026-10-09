using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using ACE.Database;
using ACE.Database.Models.Shard;

namespace ACE.Server.Entity.RewardClaims
{
    /// <summary>What the gate must do before any database call.</summary>
    public enum RewardClaimPrecheck
    {
        /// <summary>A listed key, from its own NPC, for a known account, with the gate on: run the claim.</summary>
        NeedsClaim,

        /// <summary>reward_claim_once_enabled is off. Branch TestSuccess, no DB call.</summary>
        UngatedDisabled,

        /// <summary>The key is not on the allowlist. Branch TestSuccess, no DB call, logged as an error.</summary>
        UngatedUnlistedKey,

        /// <summary>A listed key run from an NPC that does not own it. Branch TestSuccess, no DB call, logged as an error.</summary>
        UngatedWrongNpc,

        /// <summary>Account id 0. Cannot be gated; no branch.</summary>
        NoAccount
    }

    /// <summary>The adjudicated result of one claim attempt.</summary>
    public enum RewardClaimOutcome
    {
        Granted,
        DeniedAccount,
        DeniedIp,
        StoreFailed
    }

    /// <summary>
    /// The claim gate as pure functions (modelled on WorldEventClaimRules): no Player, Session,
    /// PropertyManager or database in here, so every rule is unit-testable.
    /// </summary>
    public static class RewardClaimRules
    {
        public static RewardClaimPrecheck Precheck(bool enabled, string key, uint emoterWcid, uint accountId)
        {
            if (!enabled)
                return RewardClaimPrecheck.UngatedDisabled;

            if (!RewardClaimAllowlist.TryGet(key, out var entry))
                return RewardClaimPrecheck.UngatedUnlistedKey;

            if (entry.NpcWcid != emoterWcid)
                return RewardClaimPrecheck.UngatedWrongNpc;

            if (accountId == 0)
                return RewardClaimPrecheck.NoAccount;

            return RewardClaimPrecheck.NeedsClaim;
        }

        /// <summary>Stored-key suffix marker for ClaimPeriod.SpeedSeason: Key + "#S" + season id.</summary>
        public const string SpeedSeasonSuffix = "#S";

        /// <summary>Stored-key suffix marker for ClaimPeriod.UtcDay: Key + "#D" + yyyyMMdd.</summary>
        public const string UtcDaySuffix = "#D";

        /// <summary>The width of reward_claim.claim_Key (varchar(64)). A longer stored key is refused, never truncated.</summary>
        public const int MaxStoredKeyLength = 64;

        private const string UtcDayFormat = "yyyyMMdd";

        /// <summary>
        /// The ONE place a claim period becomes a stored claim_Key. Forever = the bare key; SpeedSeason =
        /// key + "#S" + <paramref name="activeSeasonId"/>; UtcDay = key + "#D" + the UTC date of
        /// <paramref name="utcNow"/>. Returns NULL - the caller must fail closed - when the period cannot be
        /// resolved (SpeedSeason with no active season), for an unknown period, or when the result would not
        /// fit claim_Key.
        /// </summary>
        public static string StoredClaimKey(RewardClaimEntry entry, int? activeSeasonId, DateTime utcNow)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key))
                return null;

            string stored;

            switch (entry.Period)
            {
                case ClaimPeriod.Forever:
                    stored = entry.Key;
                    break;

                case ClaimPeriod.SpeedSeason:
                    if (!activeSeasonId.HasValue)
                        return null;
                    stored = entry.Key + SpeedSeasonSuffix + activeSeasonId.Value.ToString(CultureInfo.InvariantCulture);
                    break;

                case ClaimPeriod.UtcDay:
                    var utc = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : utcNow;
                    stored = entry.Key + UtcDaySuffix + utc.ToString(UtcDayFormat, CultureInfo.InvariantCulture);
                    break;

                default:
                    return null;
            }

            return stored.Length <= MaxStoredKeyLength ? stored : null;
        }

        /// <summary>
        /// The inverse of StoredClaimKey, for the admin command: maps a stored claim_Key back to its allowlist
        /// entry. A Forever key must be bare; a periodic key must carry its own period's suffix with a valid
        /// value (an integer season id, or a real yyyyMMdd date). Anything else is FALSE.
        /// </summary>
        public static bool TryParseStoredKey(string stored, out RewardClaimEntry entry)
        {
            entry = null;

            if (string.IsNullOrEmpty(stored) || stored.Length > MaxStoredKeyLength)
                return false;

            var hash = stored.IndexOf('#');

            if (hash < 0)
            {
                if (RewardClaimAllowlist.TryGet(stored, out var bare) && bare.Period == ClaimPeriod.Forever)
                {
                    entry = bare;
                    return true;
                }

                return false;
            }

            if (!RewardClaimAllowlist.TryGet(stored.Substring(0, hash), out var candidate))
                return false;

            var suffix = stored.Substring(hash);

            switch (candidate.Period)
            {
                case ClaimPeriod.SpeedSeason:
                    if (!suffix.StartsWith(SpeedSeasonSuffix, StringComparison.Ordinal))
                        return false;
                    var seasonText = suffix.Substring(SpeedSeasonSuffix.Length);
                    if (seasonText.Length == 0 || !int.TryParse(seasonText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seasonId)
                        || !string.Equals(seasonId.ToString(CultureInfo.InvariantCulture), seasonText, StringComparison.Ordinal))
                        return false;
                    break;

                case ClaimPeriod.UtcDay:
                    if (!suffix.StartsWith(UtcDaySuffix, StringComparison.Ordinal))
                        return false;
                    if (!DateTime.TryParseExact(suffix.Substring(UtcDaySuffix.Length), UtcDayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        return false;
                    break;

                default:
                    return false;
            }

            entry = candidate;
            return true;
        }

        /// <summary>
        /// The ip_Key stored and matched for an address. NULL when the session is exempt (the IP rule is
        /// skipped and the address is not consumed) or when there is no address. Otherwise an IPv4-mapped
        /// IPv6 address collapses to its IPv4 form, an IPv6 scope id is dropped, and the result is the
        /// canonical ToString (lowercase for IPv6), so one client can never hold two keys.
        /// </summary>
        public static string NormalizeIpKey(IPAddress address, bool exempt)
        {
            if (exempt || address == null)
                return null;

            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
                address = new IPAddress(address.GetAddressBytes());

            return address.ToString();
        }

        /// <summary>
        /// Adjudicates an insert. A Duplicate is read against the conflicting rows: a row carrying this
        /// attempt's own token means an earlier try of this same attempt committed and lost its ack, so it
        /// is a grant. Otherwise an account match outranks an IP match. A Duplicate with no explainable
        /// conflict (a row deleted in between, or a failed read) is StoreFailed, never a denial.
        /// </summary>
        public static RewardClaimOutcome Interpret(RewardClaimInsertResult insert, IReadOnlyList<RewardClaim> conflicts, string token, uint accountId, string ipKey)
        {
            switch (insert)
            {
                case RewardClaimInsertResult.Inserted:
                    return RewardClaimOutcome.Granted;

                case RewardClaimInsertResult.Duplicate:
                    break;

                default:
                    return RewardClaimOutcome.StoreFailed;
            }

            if (conflicts == null)
                return RewardClaimOutcome.StoreFailed;

            if (!string.IsNullOrEmpty(token))
            {
                foreach (var row in conflicts)
                {
                    if (row != null && string.Equals(row.ClaimToken, token, StringComparison.Ordinal))
                        return RewardClaimOutcome.Granted;
                }
            }

            foreach (var row in conflicts)
            {
                if (row != null && row.AccountId == accountId)
                    return RewardClaimOutcome.DeniedAccount;
            }

            if (ipKey != null)
            {
                foreach (var row in conflicts)
                {
                    if (row != null && string.Equals(row.IpKey, ipKey, StringComparison.Ordinal))
                        return RewardClaimOutcome.DeniedIp;
                }
            }

            return RewardClaimOutcome.StoreFailed;
        }
    }
}
