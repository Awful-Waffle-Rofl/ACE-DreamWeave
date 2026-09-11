using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers
{
    /// <summary>
    /// A candidate character considered by the IP active-player limit rule. Level 0 for the not-yet-in-world
    /// incoming character is fine - it simply makes that character the most eligible to be evicted on a tie.
    /// </summary>
    public readonly struct IpLimitCandidate
    {
        /// <summary>
        /// 0 for the not-yet-in-world incoming character being evaluated at login.
        /// </summary>
        public uint Guid { get; }

        public int Level { get; }

        /// <summary>
        /// True if this character is currently inside a configured mule landblock.
        /// </summary>
        public bool Confined { get; }

        /// <summary>
        /// Unix seconds. Higher = logged in more recently.
        /// </summary>
        public double LoginTimestamp { get; }

        public IpLimitCandidate(uint guid, int level, bool confined, double loginTimestamp)
        {
            Guid = guid;
            Level = level;
            Confined = confined;
            LoginTimestamp = loginTimestamp;
        }
    }

    public enum IpLimitAction
    {
        Admit,
        Refuse,
        AdmitAfterEvicting,
    }

    public class IpLimitDecision
    {
        public IpLimitAction Action { get; }

        /// <summary>
        /// Guids to remove. Empty unless Action == AdmitAfterEvicting.
        /// </summary>
        public IReadOnlyList<uint> Evict { get; }

        public IpLimitDecision(IpLimitAction action, IReadOnlyList<uint> evict)
        {
            Action = action;
            Evict = evict;
        }
    }

    /// <summary>
    /// WaffleACE: IP-based active-player limit. A non-exempt client IP may have at most
    /// ip_limit_max_free characters in-world OUTSIDE the configured mule landblocks (mule_landblocks - the
    /// Marketplace by default), plus up to ip_limit_max_confined additional characters as long as each of
    /// those is currently inside a mule landblock. Total in-world cap for an IP is
    /// ip_limit_max_free + ip_limit_max_confined. Exempt accounts (household IPs) bypass everything.
    ///
    /// SelectViolators is the whole rule, and it is pure - no PropertyManager reads, no world state - so it
    /// is unit-testable on its own (see IpLimitRuleTests). Evaluate delegates to it so the login gate and the
    /// periodic sweep share exactly one implementation of the rule instead of two copies that could drift.
    ///
    /// This class also owns the not-pure runtime helpers (Enabled, GetMuleLandblocks, IsExempt, IsConfined,
    /// GetResidents) and the CSV admin helpers for mule_landblocks / ip_limit_exempt_accounts. These are kept
    /// separate from the pure rule above deliberately, so the rule itself never needs a live world to test.
    /// </summary>
    public static class IpLimitManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string MuleLandblocksConfigKey = "mule_landblocks";
        public const string ExemptAccountsConfigKey = "ip_limit_exempt_accounts";

        // ==================================================================================
        // Pure rule
        // ==================================================================================

        /// <summary>
        /// The whole rule. Two independent caps, applied in order:
        ///
        /// 1. Outside cap: among candidates with Confined == false, if the count exceeds maxFree, the excess
        ///    (chosen by the comparator below) are violators.
        /// 2. Total cap: among all candidates NOT already selected in step 1, if the total count exceeds
        ///    maxFree + maxConfined, the excess (chosen by the comparator below) are violators.
        ///
        /// For each cap, the excess is the front of the group sorted by Level ascending (lowest level evicted
        /// first), then LoginTimestamp descending (among equal levels, the most recently logged-in character
        /// is evicted first, so the character that has been on longest survives), then Guid ascending as a
        /// final deterministic tiebreak.
        /// </summary>
        public static IReadOnlyList<uint> SelectViolators(IReadOnlyList<IpLimitCandidate> candidates, int maxFree, int maxConfined)
        {
            if (candidates == null || candidates.Count == 0)
                return Array.Empty<uint>();

            var violatorIndices = SelectViolatorIndices(candidates, maxFree, maxConfined);

            return violatorIndices.OrderBy(i => i).Select(i => candidates[i].Guid).ToList();
        }

        // Index-based core of the rule. Indices into `candidates`, not the candidates themselves, because a
        // resident and the incoming candidate can be structurally identical (incoming's Guid is always 0), so
        // identity has to be positional, not value-based - this is what lets Evaluate below ask "was the LAST
        // element (the incoming candidate) selected" unambiguously.
        private static HashSet<int> SelectViolatorIndices(IReadOnlyList<IpLimitCandidate> candidates, int maxFree, int maxConfined)
        {
            var comparer = Comparer<int>.Create((ia, ib) =>
            {
                var a = candidates[ia];
                var b = candidates[ib];

                var byLevel = a.Level.CompareTo(b.Level);
                if (byLevel != 0)
                    return byLevel;

                var byLogin = b.LoginTimestamp.CompareTo(a.LoginTimestamp);
                if (byLogin != 0)
                    return byLogin;

                var byGuid = a.Guid.CompareTo(b.Guid);
                if (byGuid != 0)
                    return byGuid;

                return ia.CompareTo(ib);
            });

            var violatorIndices = new HashSet<int>();

            // 1. Outside cap
            var outside = Enumerable.Range(0, candidates.Count).Where(i => !candidates[i].Confined).OrderBy(i => i, comparer).ToList();
            if (outside.Count > maxFree)
            {
                var excessCount = outside.Count - maxFree;
                foreach (var i in outside.Take(excessCount))
                    violatorIndices.Add(i);
            }

            // 2. Total cap, over everyone not already selected above.
            var totalCap = maxFree + maxConfined;
            var remaining = Enumerable.Range(0, candidates.Count).Where(i => !violatorIndices.Contains(i)).OrderBy(i => i, comparer).ToList();

            if (remaining.Count > totalCap)
            {
                var excessCount = remaining.Count - totalCap;
                foreach (var i in remaining.Take(excessCount))
                    violatorIndices.Add(i);
            }

            return violatorIndices;
        }

        /// <summary>
        /// Evaluates whether an incoming (not-yet-in-world) candidate may join, given the current residents.
        /// Delegates entirely to the same index-based selection SelectViolators uses, run over
        /// residents + incoming - this is the one function shared by the login gate and the periodic sweep.
        /// Whether the incoming candidate itself was selected is determined by its POSITION (the last index)
        /// in that combined list, not by its guid, since incoming's guid is always 0.
        /// </summary>
        public static IpLimitDecision Evaluate(IpLimitCandidate incoming, IReadOnlyList<IpLimitCandidate> residents, int maxFree, int maxConfined)
        {
            var all = new List<IpLimitCandidate>(residents ?? Array.Empty<IpLimitCandidate>())
            {
                incoming
            };

            var incomingIndex = all.Count - 1;

            var violatorIndices = SelectViolatorIndices(all, maxFree, maxConfined);

            if (violatorIndices.Contains(incomingIndex))
                return new IpLimitDecision(IpLimitAction.Refuse, Array.Empty<uint>());

            if (violatorIndices.Count == 0)
                return new IpLimitDecision(IpLimitAction.Admit, Array.Empty<uint>());

            var evictGuids = violatorIndices.OrderBy(i => i).Select(i => all[i].Guid).ToList();
            return new IpLimitDecision(IpLimitAction.AdmitAfterEvicting, evictGuids);
        }

        /// <summary>
        /// Pure. Given one (account id -> LoginTimestamp) pair per online character in an IP group, returns
        /// the INDICES to keep: exactly one per account, the entry with the HIGHEST LoginTimestamp.
        ///
        /// One account cannot legitimately have two characters in-world at once, so a second entry for the
        /// same account is a stale session that PlayerManager has not reaped yet. Counting it would make a
        /// reconnecting player evict themselves - this is the periodic sweep's equivalent of the login gate's
        /// excludeAccountId argument to GetResidents.
        ///
        /// Indices are returned in ascending order. On an exact LoginTimestamp tie the LOWEST index wins, so
        /// the result is deterministic for any input rather than dependent on dictionary ordering.
        /// </summary>
        public static List<int> SelectAccountRepresentatives(IReadOnlyList<KeyValuePair<uint, double>> accountLogins)
        {
            var result = new List<int>();

            if (accountLogins == null || accountLogins.Count == 0)
                return result;

            // account id -> index of the best entry seen so far for that account
            var best = new Dictionary<uint, int>();

            for (var i = 0; i < accountLogins.Count; i++)
            {
                var accountId = accountLogins[i].Key;

                if (!best.TryGetValue(accountId, out var incumbent))
                {
                    best[accountId] = i;
                    continue;
                }

                if (accountLogins[i].Value > accountLogins[incumbent].Value)
                    best[accountId] = i;
            }

            result.AddRange(best.Values);
            result.Sort();

            return result;
        }

        /// <summary>
        /// Pure. Clamps a raw ip_limit_max_* config value into [0, int.MaxValue] before it is narrowed to the
        /// int the rule takes. Both caps are stored as longs by PropertyManager, and a bare (int) cast of an
        /// out-of-range long is UNCHECKED in C# - it silently wraps. An admin typo such as
        /// "/modifylong ip_limit_max_free 5000000000" (one digit too many, or a pasted millisecond timestamp)
        /// would therefore land on an arbitrary wrapped value instead of the intended "effectively no limit",
        /// and a wrap into the negatives would make the outside cap negative, which selects EVERY character
        /// on EVERY non-exempt address as a violator - a server-wide lockout caused by a config typo. Saturate
        /// instead, so a too-large value means "no practical limit" and the failure mode of a fat-fingered
        /// config is a limit that does nothing rather than one that boots everybody.
        ///
        /// The floor is 0, not 1, for BOTH caps. Zero is a legitimate configuration, not a mistake: maxFree 0
        /// means no character from an address may stand outside a mule landblock at all (everyone must be in
        /// the Marketplace), and maxConfined 0 means the mule allowance is switched off and only the free cap
        /// applies. Both are severe but coherent, so they are honoured rather than silently raised to 1. Only
        /// genuinely unrepresentable values (negative, or beyond int range) are clamped.
        /// </summary>
        internal static int ClampCap(long value)
        {
            return (int)Math.Max(0, Math.Min(int.MaxValue, value));
        }

        // ==================================================================================
        // Runtime helpers - read live config/session/world state. Not pure, not unit-tested directly here.
        // ==================================================================================

        public static bool Enabled => PropertyManager.GetBool("ip_limit_enabled").Item;

        /// <summary>
        /// Unix seconds of the next due sweep. Only ever touched from the world tick thread.
        /// </summary>
        private static double nextSweepTime;

        /// <summary>
        /// Whether the last Tick saw the feature enabled, so the transition to disabled can be noticed exactly
        /// once. Only ever touched from the world tick thread.
        /// </summary>
        private static bool sweepWasEnabled;

        /// <summary>
        /// The ongoing re-check sweep, called once per world tick from WorldManager.UpdateWorld and
        /// self-rate-limited to ip_limit_sweep_seconds.
        ///
        /// This exists because the login gate alone is trivially bypassable - admit a character while it is
        /// standing in the Marketplace, then walk it out - and because the gate's landblock read is only a
        /// PREDICTION: DoPlayerEnterWorld can relocate a character after admission (no-log landblock handling,
        /// first-login routing, dead-instance relocation, spawn-failure fallback). The sweep is what makes the
        /// gate honest.
        ///
        /// Deliberately the ONE reaction site: there is no hook in OnTeleportComplete and none in the player
        /// heartbeat. A worst case of one sweep interval before a violator is noticed is acceptable for a
        /// house rule, and is much cheaper to reason about than a decision scattered over several call sites.
        /// </summary>
        public static void Tick()
        {
            if (!Enabled)
            {
                // Steady state while disabled is a single bool read and a return. The one-shot clearing pass
                // below runs only on the transition from enabled to disabled: without it a grace stamp could
                // outlive a disable/re-enable cycle and get its character logged off on the first sweep after
                // re-enabling, without a fresh warning.
                if (sweepWasEnabled)
                {
                    sweepWasEnabled = false;

                    foreach (var player in PlayerManager.GetAllOnline())
                        player.IpLimitGraceExpiry = null;
                }

                return;
            }

            sweepWasEnabled = true;

            var now = Time.GetUnixTime();

            if (nextSweepTime > now)
                return;

            // Clamped to at least 1s so a mis-set config value cannot turn this into a per-tick sweep.
            var sweepSeconds = Math.Max(1, PropertyManager.GetLong("ip_limit_sweep_seconds").Item);
            nextSweepTime = Time.GetFutureUnixTime(sweepSeconds);

            // Clamped, not bare-cast: see ClampCap for why an unchecked long -> int narrowing of these two is
            // a server-wide lockout waiting on one admin typo.
            var maxFree = ClampCap(PropertyManager.GetLong("ip_limit_max_free").Item);
            var maxConfined = ClampCap(PropertyManager.GetLong("ip_limit_max_confined").Item);
            var graceSeconds = Math.Max(0, PropertyManager.GetLong("ip_limit_grace_seconds").Item);

            var online = PlayerManager.GetAllOnline();

            // Group the eligible players by client address. Same exclusions as GetResidents: no session, on
            // the way out, or exempt.
            var groups = new Dictionary<IPAddress, List<Player>>();

            foreach (var player in online)
            {
                var session = player.Session;
                if (session == null)
                    continue;

                if (player.IsLoggingOut)
                    continue;

                var address = session.EndPointC2S?.Address;
                if (address == null)
                    continue;

                if (IsExempt(session))
                    continue;

                if (!groups.TryGetValue(address, out var group))
                {
                    group = new List<Player>();
                    groups[address] = group;
                }

                group.Add(player);
            }

            var violators = new HashSet<uint>();

            foreach (var group in groups.Values)
            {
                // One account cannot legitimately have two characters in-world, so collapse duplicates to the
                // most recently logged-in entry before applying the rule - see SelectAccountRepresentatives.
                var accountLogins = group.Select(p => new KeyValuePair<uint, double>(p.Session.AccountId, p.LoginTimestamp ?? 0)).ToList();

                var candidates = new List<IpLimitCandidate>();

                foreach (var i in SelectAccountRepresentatives(accountLogins))
                {
                    var player = group[i];
                    candidates.Add(new IpLimitCandidate(player.Guid.Full, player.Level ?? 0, IsConfined(player), player.LoginTimestamp ?? 0));
                }

                foreach (var guid in SelectViolators(candidates, maxFree, maxConfined))
                    violators.Add(guid);
            }

            // Grace handling, over every online player - not just the grouped ones - so that a player who has
            // become exempt, or whose session went away, still gets their stamp cleared.
            foreach (var player in online)
            {
                if (!violators.Contains(player.Guid.Full))
                {
                    // Released silently. A player who walks back into a mule landblock, or who is promoted
                    // because a higher level character from the same address logged off, gets no message.
                    player.IpLimitGraceExpiry = null;
                    continue;
                }

                if (player.IpLimitGraceExpiry == null)
                {
                    player.IpLimitGraceExpiry = Time.GetFutureUnixTime(graceSeconds);

                    player.Session?.Network.EnqueueSend(new GameMessageSystemChat($"Too many characters from your network address are in the world. Return to the Marketplace, or log one of your other characters off, within {graceSeconds} seconds or this character will be logged off.", ChatMessageType.Broadcast));

                    continue;
                }

                if (now < player.IpLimitGraceExpiry.Value)
                    continue;

                // Warned, the grace ran out, and still a violator on this sweep.
                player.IpLimitGraceExpiry = null;

                PlayerManager.BroadcastToAuditChannel(null, $"IP limit: logging off {player.Name} (level {player.Level ?? 0}, account {player.Session?.Account}, address {player.Session?.EndPointC2S?.Address}) - still over the address limit {graceSeconds}s after being warned.");

                // forceImmediate so a PK logout timer cannot stall the eviction. Session.LogOffPlayer, not
                // Player.ForceLogoff - the latter is documented as system use only.
                player.Session?.LogOffPlayer(true);
            }
        }

        /// <summary>
        /// Parses the mule_landblocks CSV. Accepts entries as either 0x016C or 016C, case-insensitive,
        /// tolerates whitespace and empty entries, and SKIPS a malformed entry with a logged warning rather
        /// than throwing - a bad config string must never break login.
        /// </summary>
        public static HashSet<ushort> GetMuleLandblocks()
        {
            var raw = PropertyManager.GetString(MuleLandblocksConfigKey).Item;

            var result = new HashSet<ushort>();

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            foreach (var token in raw.Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    trimmed = trimmed.Substring(2);

                if (ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var landblock))
                {
                    result.Add(landblock);
                }
                else
                {
                    log.Warn($"IpLimitManager.GetMuleLandblocks: skipping malformed entry '{token}' in '{MuleLandblocksConfigKey}'.");
                }
            }

            return result;
        }

        /// <summary>
        /// True if the session is exempt from the IP active-player limit for any of: access level at or above
        /// ip_limit_exempt_access_level (unless that key is -1); account name listed in
        /// ip_limit_exempt_accounts; a loopback remote address; or the remote address already appearing in the
        /// existing Config.js AllowUnlimitedSessionsFromIPAddresses allowlist.
        /// </summary>
        public static bool IsExempt(Session session)
        {
            if (session == null)
                return true;

            var exemptAccessLevel = PropertyManager.GetLong("ip_limit_exempt_access_level").Item;
            if (exemptAccessLevel >= 0 && (long)session.AccessLevel >= exemptAccessLevel)
                return true;

            var accountName = session.Account;
            if (!string.IsNullOrEmpty(accountName))
            {
                var exemptAccounts = ParseCsv(PropertyManager.GetString(ExemptAccountsConfigKey).Item);
                if (exemptAccounts.Contains(accountName, StringComparer.OrdinalIgnoreCase))
                    return true;
            }

            var address = session.EndPointC2S?.Address;
            if (address != null)
            {
                if (IPAddress.IsLoopback(address))
                    return true;

                var addressString = address.ToString();
                var allowlist = ConfigManager.Config?.Server?.Network?.AllowUnlimitedSessionsFromIPAddresses;
                if (allowlist != null && allowlist.Contains(addressString))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True while the player is inside a configured mule landblock, OR mid-teleport (Teleporting) - a
        /// teleporting player's position is in flight, and treating them as "outside" during a portal transit
        /// would boot them for simply using a portal.
        /// </summary>
        public static bool IsConfined(Player player)
        {
            if (player == null)
                return false;

            if (player.Teleporting)
                return true;

            var landblock = player.Location?.LandblockShort;
            if (landblock == null)
                return false;

            var muleLandblocks = GetMuleLandblocks();
            return muleLandblocks.Contains((ushort)landblock.Value);
        }

        /// <summary>
        /// All online players sharing `address`, excluding: a null Session; a player currently logging out;
        /// an exempt session; and any player whose Session.AccountId == excludeAccountId. That last exclusion
        /// is load-bearing - it stops a player's own reconnect (whose stale prior session is briefly still
        /// counted as online) from booting their own other character.
        /// </summary>
        public static List<IpLimitCandidate> GetResidents(IPAddress address, uint excludeAccountId)
        {
            var result = new List<IpLimitCandidate>();

            if (address == null)
                return result;

            foreach (var player in PlayerManager.GetAllOnline())
            {
                var session = player.Session;
                if (session == null)
                    continue;

                if (player.IsLoggingOut)
                    continue;

                if (session.AccountId == excludeAccountId)
                    continue;

                var remoteAddress = session.EndPointC2S?.Address;
                if (remoteAddress == null || !remoteAddress.Equals(address))
                    continue;

                if (IsExempt(session))
                    continue;

                result.Add(new IpLimitCandidate(player.Guid.Full, player.Level ?? 0, IsConfined(player), player.LoginTimestamp ?? 0));
            }

            return result;
        }

        // ==================================================================================
        // CSV admin helpers - shared by both mule_landblocks and ip_limit_exempt_accounts so the
        // admin-command layer does not need to write its own parser.
        // ==================================================================================

        private static List<string> ParseCsv(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var token in raw.Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (seen.Add(trimmed))
                    result.Add(trimmed);
            }

            return result;
        }

        private static string FormatCsv(IEnumerable<string> values)
        {
            return string.Join(",", values);
        }

        /// <summary>
        /// Normalizes a landblock token (0x016C or 016C, any case) to 4 upper-case hex digits with no 0x
        /// prefix. Returns null if the token is not a valid landblock id.
        /// </summary>
        public static string NormalizeLandblockToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            var trimmed = token.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(2);

            if (!ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var landblock))
                return null;

            return landblock.ToString("X4");
        }

        /// <summary>
        /// Current mule_landblocks entries, normalized to 4 upper-case hex digits, de-duplicated, sorted.
        /// </summary>
        public static List<string> ListMuleLandblocks()
        {
            var raw = ParseCsv(PropertyManager.GetString(MuleLandblocksConfigKey).Item);

            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in raw)
            {
                var n = NormalizeLandblockToken(token);
                if (n != null)
                    normalized.Add(n);
            }

            var result = normalized.ToList();
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Adds a landblock id (0x016C or 016C) to mule_landblocks. False with a message if malformed, already
        /// present, or the config write fails.
        /// </summary>
        public static bool TryAddMuleLandblock(string token, out string message)
        {
            var normalized = NormalizeLandblockToken(token);
            if (normalized == null)
            {
                message = $"'{token}' is not a valid landblock id (expected 4 hex digits, e.g. 016C or 0x016C).";
                return false;
            }

            var current = ListMuleLandblocks();
            if (current.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                message = $"'{normalized}' is already in {MuleLandblocksConfigKey}.";
                return false;
            }

            current.Add(normalized);
            current.Sort(StringComparer.OrdinalIgnoreCase);

            if (!PropertyManager.ModifyString(MuleLandblocksConfigKey, FormatCsv(current)))
            {
                message = $"Failed to write '{MuleLandblocksConfigKey}' - is it declared in DefaultStringProperties?";
                return false;
            }

            message = $"Added '{normalized}' to {MuleLandblocksConfigKey}.";
            return true;
        }

        /// <summary>
        /// Removes a landblock id from mule_landblocks, matched after normalization.
        /// </summary>
        public static bool TryRemoveMuleLandblock(string token, out string message)
        {
            var normalized = NormalizeLandblockToken(token);
            if (normalized == null)
            {
                message = $"'{token}' is not a valid landblock id (expected 4 hex digits, e.g. 016C or 0x016C).";
                return false;
            }

            var current = ListMuleLandblocks();
            var removed = current.RemoveAll(n => string.Equals(n, normalized, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                message = $"'{normalized}' is not in {MuleLandblocksConfigKey}.";
                return false;
            }

            if (!PropertyManager.ModifyString(MuleLandblocksConfigKey, FormatCsv(current)))
            {
                message = $"Failed to write '{MuleLandblocksConfigKey}' - is it declared in DefaultStringProperties?";
                return false;
            }

            message = $"Removed '{normalized}' from {MuleLandblocksConfigKey}.";
            return true;
        }

        /// <summary>
        /// Current ip_limit_exempt_accounts entries, de-duplicated case-insensitively, sorted.
        /// </summary>
        public static List<string> ListExemptAccounts()
        {
            var current = new HashSet<string>(ParseCsv(PropertyManager.GetString(ExemptAccountsConfigKey).Item), StringComparer.OrdinalIgnoreCase);
            var result = current.ToList();
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Adds an account name (verbatim) to ip_limit_exempt_accounts. False with a message if already
        /// present or the config write fails.
        /// </summary>
        public static bool TryAddExemptAccount(string accountName, out string message)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                message = "Account name cannot be empty.";
                return false;
            }

            var current = ParseCsv(PropertyManager.GetString(ExemptAccountsConfigKey).Item);
            if (current.Any(n => string.Equals(n, accountName, StringComparison.OrdinalIgnoreCase)))
            {
                message = $"'{accountName}' is already in {ExemptAccountsConfigKey}.";
                return false;
            }

            current.Add(accountName);
            current.Sort(StringComparer.OrdinalIgnoreCase);

            if (!PropertyManager.ModifyString(ExemptAccountsConfigKey, FormatCsv(current)))
            {
                message = $"Failed to write '{ExemptAccountsConfigKey}' - is it declared in DefaultStringProperties?";
                return false;
            }

            message = $"Added '{accountName}' to {ExemptAccountsConfigKey}.";
            return true;
        }

        /// <summary>
        /// Removes an account name from ip_limit_exempt_accounts, matched case-insensitively.
        /// </summary>
        public static bool TryRemoveExemptAccount(string accountName, out string message)
        {
            if (string.IsNullOrWhiteSpace(accountName))
            {
                message = "Account name cannot be empty.";
                return false;
            }

            var current = ParseCsv(PropertyManager.GetString(ExemptAccountsConfigKey).Item);
            var removed = current.RemoveAll(n => string.Equals(n, accountName, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                message = $"'{accountName}' is not in {ExemptAccountsConfigKey}.";
                return false;
            }

            if (!PropertyManager.ModifyString(ExemptAccountsConfigKey, FormatCsv(current)))
            {
                message = $"Failed to write '{ExemptAccountsConfigKey}' - is it declared in DefaultStringProperties?";
                return false;
            }

            message = $"Removed '{accountName}' from {ExemptAccountsConfigKey}.";
            return true;
        }
    }
}
