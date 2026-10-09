using System;
using System.Collections.Generic;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Why a Weave Cache claim was refused (TECH-DESIGN 5.6 - these names are fixed; the denial log line
    /// writes result=denied:&lt;enum name&gt;, so renaming a member breaks the monitoring queries).
    /// </summary>
    public enum ClaimDenial
    {
        None,
        NotRewarding,
        AlreadyClaimedCharacter,
        AlreadyClaimedAccount,
        AlreadyClaimedIp
    }

    /// <summary>
    /// The claim gate, as pure arithmetic over sets (TECH-DESIGN 2.7, 5.6). Nothing here touches a Player,
    /// a Session, a landblock or the PropertyManager - the caller
    /// (<see cref="WorldEventCacheHandler"/>) resolves all of that and passes plain values in, which is what
    /// makes every rule unit-testable under D6.
    ///
    /// Per decision C13 claim eligibility is PRESENCE, not damage credit: there is deliberately no
    /// participation or fellowship input here. The only questions this class answers are "has this
    /// character / this account / this connection already been paid".
    /// </summary>
    public static class WorldEventClaimRules
    {
        /// <summary>Name used when the caller has no cache object to name (never expected in production).</summary>
        private const string DefaultCacheName = "Weave Cache";

        /// <summary>
        /// Evaluates the three claim rules in the fixed order character, account, IP, and returns the FIRST
        /// one that refuses. <see cref="ClaimDenial.NotRewarding"/> is never returned from here: whether the
        /// run is still in its claim window is state the handler checks before it calls this, and it only
        /// calls this when the window is open.
        ///
        /// A gate whose flag is false is skipped entirely, as is the account rule for
        /// <paramref name="accountId"/> 0 (an unauthenticated or detached player) and the IP rule for a
        /// null/empty or exempt <paramref name="ip"/>.
        /// </summary>
        /// <param name="characterGuid">The claiming character's full guid.</param>
        /// <param name="accountId">The claiming session's account id; 0 when unknown.</param>
        /// <param name="ip">The claiming session's remote address as a string; null or empty when unknown.</param>
        /// <param name="claimedCharacters">Character guids already paid by this run. May be null.</param>
        /// <param name="claimedAccounts">Account ids already paid by this run. May be null.</param>
        /// <param name="claimedIps">Addresses already paid by this run. May be null.</param>
        /// <param name="exemptIps">Addresses the IP rule never fires for (shared households, C4). May be null.</param>
        public static ClaimDenial Evaluate(uint characterGuid, uint accountId, string ip,
            ISet<uint> claimedCharacters, ISet<uint> claimedAccounts, ISet<string> claimedIps, ISet<string> exemptIps,
            bool gateByCharacter, bool gateByAccount, bool gateByIp)
        {
            if (gateByCharacter && claimedCharacters != null && claimedCharacters.Contains(characterGuid))
                return ClaimDenial.AlreadyClaimedCharacter;

            if (gateByAccount && accountId != 0 && claimedAccounts != null && claimedAccounts.Contains(accountId))
                return ClaimDenial.AlreadyClaimedAccount;

            if (gateByIp)
            {
                var key = NormalizeIp(ip);

                if (!string.IsNullOrEmpty(key) && !ContainsIp(exemptIps, key) && ContainsIp(claimedIps, key))
                    return ClaimDenial.AlreadyClaimedIp;
            }

            return ClaimDenial.None;
        }

        /// <summary>
        /// Parses the world_events_ip_exempt server property: addresses separated by commas, semicolons or
        /// whitespace, each trimmed, empty entries dropped. Never returns null, and the returned set is
        /// case-insensitive so an IPv6 address written in either case matches.
        /// </summary>
        public static ISet<string> ParseExemptIps(string csv)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(csv))
                return result;

            var parts = csv.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var trimmed = part.Trim();

                if (trimmed.Length > 0)
                    result.Add(trimmed);
            }

            return result;
        }

        /// <summary>
        /// The player-facing refusal text for a denial. Pure, so the exact wording is unit-testable and can
        /// never drift between the handler and the tests. Returns an empty string for
        /// <see cref="ClaimDenial.None"/> - a caller that reaches this with None has a bug, but it should not
        /// send the player a message about it.
        /// </summary>
        public static string DenialMessage(ClaimDenial d, string cacheName)
        {
            var name = string.IsNullOrWhiteSpace(cacheName) ? DefaultCacheName : cacheName.Trim();

            switch (d)
            {
                case ClaimDenial.NotRewarding:
                    return $"The {name} is sealed - the event's claim window is closed.";

                case ClaimDenial.AlreadyClaimedCharacter:
                    return "You have already claimed your reward from this event.";

                case ClaimDenial.AlreadyClaimedAccount:
                    return "Another character on your account has already claimed this event's reward.";

                case ClaimDenial.AlreadyClaimedIp:
                    return "A reward for this event has already been claimed from your connection.";

                default:
                    return "";
            }
        }

        /// <summary>
        /// The coal-payout decision, as pure arithmetic (repo owner decision, 2026-09-07): pay the booby
        /// prize instead of the ordinary outcome crate iff the feature is switched on, the reward axis
        /// actually configured a coal wcid, and the claimant has no damage or kill credit on this run.
        ///
        /// Deliberately FAIL-OPEN in every other direction - the feature disabled, an unconfigured
        /// (coalWcid == 0) axis, or (at the caller) a missing participation ledger all fall through to the
        /// ordinary crate rather than denying or downgrading a player's claim. This does NOT change who may
        /// claim (see <see cref="Evaluate"/>) - only which item a granted claim hands over.
        /// </summary>
        public static bool PaysCoal(bool featureEnabled, uint coalWcid, bool hasCredit)
        {
            return featureEnabled && coalWcid != 0 && !hasCredit;
        }

        /// <summary>
        /// Trims an address for comparison. Null and whitespace-only both collapse to an empty string, which
        /// is the caller's signal to skip the IP rule entirely.
        /// </summary>
        public static string NormalizeIp(string ip)
        {
            return string.IsNullOrWhiteSpace(ip) ? "" : ip.Trim();
        }

        /// <summary>
        /// Case-insensitive, trim-insensitive membership test that does NOT depend on the caller having built
        /// the set with an OrdinalIgnoreCase comparer. The live sets do (WorldEvent.ClaimedIps and
        /// ParseExemptIps), so the fast path hits; the linear fallback is what keeps a plain
        /// HashSet&lt;string&gt; from a unit test behaving differently from production.
        /// </summary>
        private static bool ContainsIp(ISet<string> set, string normalized)
        {
            if (set == null || set.Count == 0 || string.IsNullOrEmpty(normalized))
                return false;

            if (set.Contains(normalized))
                return true;

            foreach (var entry in set)
            {
                if (entry == null)
                    continue;

                if (string.Equals(entry.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
