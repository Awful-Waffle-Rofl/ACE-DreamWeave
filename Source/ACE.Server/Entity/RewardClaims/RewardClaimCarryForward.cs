using System;
using System.Net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Server.Managers;

namespace ACE.Server.Entity.RewardClaims
{
    /// <summary>What a carry-forward for one account did. <see cref="Counts"/> is null unless <see cref="Ok"/>.</summary>
    public sealed class RewardClaimCarryForwardOutcome
    {
        /// <summary>The household key did not change, so nothing was read or written.</summary>
        public bool Skipped { get; set; }

        /// <summary>The re-key ran and committed (or was skipped).</summary>
        public bool Ok { get; set; }

        public string NewKey { get; set; }

        public RewardClaimRekeyResult Counts { get; set; }
    }

    /// <summary>
    /// Carries an account's reward_claim rows with it when its household changes (cluster create/add/remove/
    /// delete, ip_limit_exempt_accounts add/remove), so one account's claim never blocks a household it has
    /// left, and an account's own claims follow it into the household it joins.
    ///
    /// The rules, per row of the account whose ip_Key is NOT NULL (a NULL ip_Key was claimed under a bypass
    /// and is never touched - the store filters those out):
    /// - the new key is a household (cluster: or acct:): the row moves to that key;
    /// - the new key is not (the account went back to keying by address): the row reverts to the key of the
    ///   row's OWN stored ip_Address, because the account may be offline and have no live address. No stored
    ///   address, or one that does not parse, means NULL;
    /// - the target (claim_Key, ip_Key) is already held by another account: NULL. The row stays, so the PRIMARY
    ///   KEY still stops this account claiming twice, and the slot it held is freed.
    /// </summary>
    public static class RewardClaimCarryForward
    {
        /// <summary>Pure: is this a household key (cluster: or acct:) rather than an address key?</summary>
        public static bool IsHouseholdKey(string key)
            => key != null
               && (key.StartsWith(IpLimitManager.ClusterKeyPrefix, StringComparison.Ordinal)
                   || key.StartsWith(IpLimitManager.AccountKeyPrefix, StringComparison.Ordinal));

        /// <summary>Pure: does a change from <paramref name="oldKey"/> to <paramref name="newKey"/> move any rows at all?</summary>
        public static bool ShouldRekey(string oldKey, string newKey)
            => !string.Equals(oldKey, newKey, StringComparison.Ordinal);

        /// <summary>
        /// Pure: the ip_Key one row should carry once its account's household is <paramref name="newHouseholdKey"/>
        /// (a cluster: or acct: key, or anything else - typically null - for "keyed by address again").
        /// <paramref name="heldByAnotherAccount"/> TRUE means the first answer is already held by another account
        /// for this claim key, and the answer is then NULL.
        /// </summary>
        public static string TargetIpKey(string newHouseholdKey, string rowIpAddress, bool heldByAnotherAccount)
        {
            if (heldByAnotherAccount)
                return null;

            if (IsHouseholdKey(newHouseholdKey))
                return newHouseholdKey;

            return AddressKey(rowIpAddress);
        }

        /// <summary>
        /// Pure: the key of a stored raw address, or null when there is none or it does not parse. Routed through
        /// HouseholdKeyCore with no account and no lists, which is exactly RewardClaimRules.NormalizeIpKey(address,
        /// false): the household resolver stays the one source of every claim key.
        /// </summary>
        internal static string AddressKey(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress.Trim(), out var address))
                return null;

            return IpLimitManager.HouseholdKeyCore(0, null, address, IpLimitManager.HouseholdLists.Empty);
        }

        /// <summary>
        /// Re-keys one account's rows in the store, atomically. Blocks on the database: never call it on the world
        /// thread (IpLimitCommands runs it on a pool thread).
        /// </summary>
        public static RewardClaimCarryForwardOutcome Apply(IRewardClaimStore store, uint accountId, string oldKey, string newKey)
        {
            if (!ShouldRekey(oldKey, newKey))
                return new RewardClaimCarryForwardOutcome { Skipped = true, Ok = true, NewKey = newKey };

            var ok = store.TryRekeyAccount(accountId, (row, held) => TargetIpKey(newKey, row.IpAddress, held), out var counts);

            return new RewardClaimCarryForwardOutcome { Ok = ok, NewKey = newKey, Counts = ok ? counts : null };
        }

        /// <summary>The admin-facing line for one account's outcome.</summary>
        public static string Describe(string accountLabel, RewardClaimCarryForwardOutcome outcome)
        {
            if (outcome == null || !outcome.Ok)
                return $"Reward claims for '{accountLabel}' were NOT carried forward: the re-key failed and nothing was changed (see the server log).";

            if (outcome.Skipped)
                return $"Reward claims for '{accountLabel}': household unchanged, nothing to carry forward.";

            var c = outcome.Counts ?? new RewardClaimRekeyResult();
            var target = IsHouseholdKey(outcome.NewKey) ? outcome.NewKey : "each row's own recorded address";

            return $"Reward claims for '{accountLabel}' carried forward to {target}: {c.Rekeyed} row(s) re-keyed, "
                   + $"{c.NulledByCollision} row(s) nulled because another account already held that claim, "
                   + $"{c.NulledNoKey} row(s) nulled with no usable recorded address, {c.Unchanged} unchanged.";
        }
    }
}
