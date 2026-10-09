using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;

namespace ACE.Server.Managers
{
    /// <summary>
    /// WaffleACE: keeps staff characters and mules off the /top leaderboards and out of the Proving Grounds new-record
    /// broadcasts, so a player never sees a dev/admin alt sitting at rank 1 and doubting the boards are fair.
    ///
    /// Two independent signals decide exemption. Account.AccessLevel is read once and cached at boot/login time
    /// (OfflinePlayer resolves it in its constructor, Player resolves it at login), so it is a SNAPSHOT: a
    /// mid-session @set-accountaccess change is not reflected for an offline character until the next restart.
    /// The persisted per-character staff PropertyBools (IsAdmin/IsArch/IsEnvoy/IsSentinel), set at login from the
    /// session access level, are checked as a second signal precisely because they get refreshed on the
    /// character's own next login even when the account snapshot above is stale.
    ///
    /// Advocate is deliberately NOT in the inherent-exemption tier, and IsAdvocate is deliberately excluded from
    /// StaffCharacterBools: a normal player can reach Advocate access on their own (AdvocateQuest && IsAdvocate
    /// promotes the session), with no admin action involved. An inherent Advocate exemption would therefore be
    /// player-triggerable, which defeats the entire point of the exemption - the accounts list below is where a
    /// staff Advocate alt goes if it needs hiding.
    /// </summary>
    public static class LeaderboardExemptionManager
    {
        /// <summary>
        /// Shard config string key: comma-separated ACCOUNT names (not character names) additionally exempt from
        /// the leaderboards, on top of the inherent access-level and staff-bool exemptions below.
        /// </summary>
        public const string ConfigKey = "top_exempt_accounts";

        /// <summary>
        /// Any account at this AccessLevel or above is exempt inherently, with no listing needed.
        /// </summary>
        public const AccessLevel InherentExemptAccessLevel = AccessLevel.Sentinel;

        /// <summary>
        /// Persisted per-character bools that mark a staff character, checked as a second signal alongside the
        /// (possibly stale) Account snapshot. IsAdvocate is deliberately excluded - see the class doc comment.
        /// </summary>
        public static readonly IReadOnlyList<PropertyBool> StaffCharacterBools = new List<PropertyBool>
        {
            PropertyBool.IsAdmin,
            PropertyBool.IsArch,
            PropertyBool.IsEnvoy,
            PropertyBool.IsSentinel,
        };

        private sealed class ExemptNamesCacheEntry
        {
            public readonly string Raw;
            public readonly IReadOnlySet<string> Names;

            public ExemptNamesCacheEntry(string raw, IReadOnlySet<string> names)
            {
                Raw = raw;
                Names = names;
            }
        }

        private static ExemptNamesCacheEntry cachedExemptNames;

        /// <summary>
        /// Splits on comma, semicolon, newline, carriage return, tab and space; trims, drops empty tokens,
        /// de-duplicates case-insensitively (first spelling seen wins), and returns sorted. Pure - no config read.
        /// </summary>
        public static IReadOnlyList<string> ParseAccountList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            var separators = new[] { ',', ';', '\n', '\r', '\t', ' ' };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var token in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (seen.Add(trimmed))
                    result.Add(trimmed);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Joins into the comma-separated form ParseAccountList reads back.
        /// </summary>
        public static string FormatAccountList(IEnumerable<string> names)
        {
            return string.Join(", ", names);
        }

        /// <summary>
        /// Reads and parses the configured exempt-account list, caching the parsed set keyed on the exact raw
        /// config string so a board render over thousands of players does not re-parse per player. The cache is a
        /// single immutable holder swapped via Volatile.Read/Write - a benign duplicate rebuild under a race is
        /// fine, a torn read is not. Never returns a set a caller could mutate into the cache.
        /// </summary>
        public static IReadOnlySet<string> GetExemptAccountNames()
        {
            var raw = PropertyManager.GetString(ConfigKey).Item;

            var cached = Volatile.Read(ref cachedExemptNames);
            if (cached != null && string.Equals(cached.Raw, raw, StringComparison.Ordinal))
                return cached.Names;

            var names = new HashSet<string>(ParseAccountList(raw), StringComparer.OrdinalIgnoreCase);
            var entry = new ExemptNamesCacheEntry(raw, names);
            Volatile.Write(ref cachedExemptNames, entry);
            return names;
        }

        /// <summary>
        /// The whole exemption decision as a pure function, so it is unit-testable without faking IPlayer.
        /// Tolerates null accountName, null exemptNames, and a null accountAccessLevel.
        /// </summary>
        public static bool IsExemptCore(uint? accountAccessLevel, string accountName, bool hasStaffCharacterBool, IReadOnlySet<string> exemptNames)
        {
            return IsExemptCore(accountAccessLevel, accountName, hasStaffCharacterBool, false, exemptNames);
        }

        /// <summary>
        /// Full decision including the mule signal. A mule (PropertyBool.IsMule) is a storage-and-trade-only
        /// character that conversion sets to mule_level (default 180) with matching TotalExperience, so without
        /// this check it would sit near the top of /top level on its converted level alone.
        /// </summary>
        public static bool IsExemptCore(uint? accountAccessLevel, string accountName, bool hasStaffCharacterBool, bool isMule, IReadOnlySet<string> exemptNames)
        {
            if (isMule)
                return true;

            if (accountAccessLevel.HasValue && accountAccessLevel.Value >= (uint)InherentExemptAccessLevel)
                return true;

            if (hasStaffCharacterBool)
                return true;

            if (!string.IsNullOrEmpty(accountName) && exemptNames != null && exemptNames.Contains(accountName))
                return true;

            return false;
        }

        /// <summary>
        /// Gathers the three inputs from an IPlayer and evaluates IsExemptCore. Null player returns false.
        /// </summary>
        public static bool IsExempt(IPlayer player, IReadOnlySet<string> exemptNames)
        {
            if (player == null)
                return false;

            var hasStaffCharacterBool = StaffCharacterBools.Any(b => player.GetProperty(b) == true);
            var isMule = player.GetProperty(PropertyBool.IsMule) == true;

            return IsExemptCore(player.Account?.AccessLevel, player.Account?.AccountName, hasStaffCharacterBool, isMule, exemptNames);
        }

        /// <summary>
        /// Convenience overload that reads the current configured exempt-account list first.
        /// </summary>
        public static bool IsExempt(IPlayer player)
        {
            return IsExempt(player, GetExemptAccountNames());
        }

        /// <summary>
        /// Adds an account name (verbatim - the caller/command handler is responsible for canonicalising it
        /// against the auth DB first) to the exempt list. False with a message when already present, or when the
        /// config write itself fails (which would mean the "top_exempt_accounts" default property declaration is
        /// missing).
        /// </summary>
        public static bool TryAddAccount(string accountName, out string message)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                message = "Account name cannot be empty.";
                return false;
            }

            var current = ParseAccountList(PropertyManager.GetString(ConfigKey).Item).ToList();

            if (current.Any(n => string.Equals(n, accountName, StringComparison.OrdinalIgnoreCase)))
            {
                message = $"'{accountName}' is already on the leaderboard exemption list.";
                return false;
            }

            current.Add(accountName);
            current.Sort(StringComparer.OrdinalIgnoreCase);

            if (!PropertyManager.ModifyString(ConfigKey, FormatAccountList(current)))
            {
                message = $"Failed to write the '{ConfigKey}' config property - is it declared in DefaultStringProperties?";
                return false;
            }

            message = $"Added '{accountName}' to the leaderboard exemption list.";
            return true;
        }

        /// <summary>
        /// Removes an account name from the exempt list, matched case-insensitively. Does NOT require the account
        /// to still exist in the auth database - a deleted account must still be removable from this list.
        /// </summary>
        public static bool TryRemoveAccount(string accountName, out string message)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                message = "Account name cannot be empty.";
                return false;
            }

            var current = ParseAccountList(PropertyManager.GetString(ConfigKey).Item).ToList();

            var removed = current.RemoveAll(n => string.Equals(n, accountName, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                message = $"'{accountName}' is not on the leaderboard exemption list.";
                return false;
            }

            if (!PropertyManager.ModifyString(ConfigKey, FormatAccountList(current)))
            {
                message = $"Failed to write the '{ConfigKey}' config property - is it declared in DefaultStringProperties?";
                return false;
            }

            message = $"Removed '{accountName}' from the leaderboard exemption list.";
            return true;
        }
    }
}
