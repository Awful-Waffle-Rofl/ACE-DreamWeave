using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

using log4net;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity.RewardClaims;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Realms;
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

    /// <summary>
    /// WHY a character counted as CONFINED, in the order IsConfinedCore checks for it - the first applicable
    /// reason wins. None means the character is not confined at all. Exists so /iplimitwho and similar
    /// diagnostics can say WHY rather than just CONFINED/FREE; IsConfined(Player) is the plain bool most call
    /// sites want and is just "GetConfinementReason(player) != None".
    /// </summary>
    public enum IpLimitConfinementReason
    {
        None,
        Teleporting,
        MuleLandblock,
        Housing,
        ActivityTail,
        PvpArena,
        SpeedRun,
        ProvingGrounds,
    }

    /// <summary>
    /// Which activity zones count toward confinement, and how long the post-activity tail lasts. Mirrors the
    /// PvpTunables.PvpArenaDials seam (Source/ACE.Server/Pvp/PvpTunables.cs) exactly - see
    /// IpLimitZoneTunables below for the same fallback-to-Defaults reasoning.
    /// </summary>
    public sealed record IpLimitZoneDials(bool ConfineArena, bool ConfineProving, bool ConfineSpeed, bool ConfineHousing, int TailSeconds);

    /// <summary>
    /// Test seam for the ip_limit_confine_* / ip_limit_activity_tail_seconds tunables, following
    /// ACE.Server.Pvp.PvpTunables exactly: PropertyManager.Get* throws in ACE.Server.Tests for any uncached
    /// key, so every read here is wrapped and falls back to <see cref="Defaults"/> - the SAME defaults the
    /// tunables ship with, so a test-environment read and a fresh, unconfigured shard read agree.
    /// </summary>
    public static class IpLimitZoneTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(IpLimitZoneTunables));

        public static Func<IpLimitZoneDials> DialSource = ReadFromProperties;

        public static readonly IpLimitZoneDials Defaults = new IpLimitZoneDials(
            ConfineArena: true,
            ConfineProving: true,
            ConfineSpeed: true,
            ConfineHousing: true,
            TailSeconds: 15);

        private static IpLimitZoneDials ReadFromProperties()
        {
            try
            {
                var d = Defaults;

                return new IpLimitZoneDials(
                    ConfineArena: PropertyManager.GetBool("ip_limit_confine_pvp_arena", d.ConfineArena).Item,
                    ConfineProving: PropertyManager.GetBool("ip_limit_confine_proving_grounds", d.ConfineProving).Item,
                    ConfineSpeed: PropertyManager.GetBool("ip_limit_confine_speed_runs", d.ConfineSpeed).Item,
                    ConfineHousing: PropertyManager.GetBool("ip_limit_confine_housing", d.ConfineHousing).Item,
                    TailSeconds: (int)PropertyManager.GetLong("ip_limit_activity_tail_seconds", d.TailSeconds).Item);
            }
            catch (Exception ex)
            {
                log.Error("[IPLIMIT] could not read the ip_limit_confine_*/ip_limit_activity_tail_seconds tunables; falling back to built-in defaults", ex);
                return Defaults;
            }
        }
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
    /// ip_limit_max_free characters in-world OUTSIDE the confined set, plus up to ip_limit_max_confined
    /// additional characters as long as each of those currently counts as CONFINED: inside a configured mule
    /// landblock (mule_landblocks - the Marketplace by default), any character standing in a housing
    /// landblock (see IsInHousing, gated by ip_limit_confine_housing), inside a live PvP arena match or Proving
    /// Grounds run (gated by the ip_limit_confine_* dials), or still inside the short post-activity tail after
    /// one of those (ip_limit_activity_tail_seconds - see IsConfinedCore, GetConfinementReason). Total in-world
    /// cap for an IP is ip_limit_max_free + ip_limit_max_confined. Exempt accounts (household IPs) bypass
    /// everything.
    ///
    /// SelectViolators is the whole eviction rule, and IsConfinedCore is the whole confinement rule; both are
    /// pure - no PropertyManager reads, no world state - so they are unit-testable on their own (see
    /// IpLimitRuleTests, IpLimitConfinementTests). Evaluate delegates to SelectViolators so the login gate and
    /// the periodic sweep share exactly one implementation of the eviction rule instead of two copies that
    /// could drift.
    ///
    /// This class also owns the not-pure runtime helpers (Enabled, GetMuleLandblocks, IsExempt, IsConfined,
    /// GetConfinementReason, GetResidents) and the CSV admin helpers for mule_landblocks /
    /// ip_limit_exempt_accounts. These are kept separate from the pure rules above deliberately, so the rules
    /// themselves never need a live world to test.
    /// </summary>
    public static class IpLimitManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string MuleLandblocksConfigKey = "mule_landblocks";
        public const string ExemptAccountsConfigKey = "ip_limit_exempt_accounts";
        public const string ClustersConfigKey = "ip_household_clusters";

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

        /// <summary>
        /// The whole "is this character CONFINED" rule, pure: no PropertyManager reads, no world state, just
        /// the facts a caller already gathered plus the resolved <see cref="IpLimitZoneDials"/>. Checked in
        /// order, first match wins (see <see cref="IpLimitConfinementReason"/>):
        ///
        /// 1. Teleporting - mid-portal-transit, position is in flight, never treated as "outside".
        /// 2. Inside a configured mule landblock (mule_landblocks, the Marketplace by default).
        /// 2b. ANY character in a housing landblock (cottage, villa, mansion, apartment; the House+SlumLord set, see
        ///    IsHousingLandblock), while ip_limit_confine_housing is on. An ephemeral realm instance (Thread dungeon,
        ///    private instance) sharing the landblock id is not at a house (IsHousingPlacement).
        /// 3. Inside the post-activity tail (see <paramref name="inActivityTail"/>) - checked before the
        ///    activity terms themselves so a character that JUST left the zone (activity term now false, tail
        ///    still running) is still reported as confined rather than falling through to None.
        /// 4. Standing in a live PvP arena match's bound instance, while ip_limit_confine_pvp_arena is on.
        /// 5. Standing in a live Proving Grounds SPEED run's bound instance, while ip_limit_confine_speed_runs
        ///    is on. Speed is its own dial (not folded into ConfineProving) so it can be switched off
        ///    independently of the other three Proving Grounds disciplines.
        /// 6. Standing in a live Proving Grounds DPS/Survival/Wave run's bound instance, while
        ///    ip_limit_confine_proving_grounds is on.
        ///
        /// A dial that is off makes its own term inert but never disables the others - this is what makes the
        /// "every dial off" case (all FOUR: arena, proving, speed and housing) reduce exactly to the pre-existing
        /// Marketplace-only rule (teleporting + mule landblock only), which IpLimitConfinementTests pins as a control.
        /// </summary>
        internal static IpLimitConfinementReason IsConfinedCore(
            bool teleporting,
            bool inMuleLandblock,
            bool inHousing,
            bool inArenaMatchSpace,
            bool inSpeedRun,
            bool inProvingGroundsRun,
            bool inActivityTail,
            IpLimitZoneDials dials)
        {
            if (teleporting)
                return IpLimitConfinementReason.Teleporting;

            if (inMuleLandblock)
                return IpLimitConfinementReason.MuleLandblock;

            if (inHousing && dials.ConfineHousing)
                return IpLimitConfinementReason.Housing;

            if (inActivityTail)
                return IpLimitConfinementReason.ActivityTail;

            if (inArenaMatchSpace && dials.ConfineArena)
                return IpLimitConfinementReason.PvpArena;

            if (inSpeedRun && dials.ConfineSpeed)
                return IpLimitConfinementReason.SpeedRun;

            if (inProvingGroundsRun && dials.ConfineProving)
                return IpLimitConfinementReason.ProvingGrounds;

            return IpLimitConfinementReason.None;
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
        /// This is the reaction site for a character that WALKS into violation (out of a confined zone): it
        /// warns, waits ip_limit_grace_seconds, then logs off. There is exactly one other site, CheckOnArrival,
        /// called from Player.OnTeleportComplete: owner ruling 2026-10-18 reversed the earlier "sweep only"
        /// design because a character could recall to a lifestone and farm there until the sweep caught it, so
        /// a TELEPORT into violation is enforced immediately, with no grace. The heartbeat has no hook.
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

            // Group the eligible players by household. Same exclusions as GetResidents: no session, on the way
            // out, or a true bypass (IsExempt). The household lists are read once for the whole sweep.
            var lists = ReadHouseholdLists();

            var eligible = online.Where(p => p.Session != null && !p.IsLoggingOut && !IsExempt(p.Session));

            var groups = GroupByHousehold(eligible, p => HouseholdKey(p.Session, lists));

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

                    // Stamp the activity-tail marker BEFORE reading IsConfined, so this sweep's own observation
                    // of a live activity term is what the tail (and this sweep's own confinement check) sees.
                    RefreshActivityStamp(player);

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

                    SendGraceWarning(player, graceSeconds);

                    continue;
                }

                if (now < player.IpLimitGraceExpiry.Value)
                    continue;

                // Warned, the grace ran out, and still a violator on this sweep.
                player.IpLimitGraceExpiry = null;

                log.Info($"[IP limit] logging off {player.Name} (level {player.Level ?? 0}, account {player.Session?.Account}, household {HouseholdKey(player.Session, lists)}, address {player.Session?.EndPointC2S?.Address}) - still over the address limit {graceSeconds}s after being warned.");

                // forceImmediate so a PK logout timer cannot stall the eviction. Session.LogOffPlayer, not
                // Player.ForceLogoff - the latter is documented as system use only.
                // Deserter lockout: a server eviction, not the player's desertion.
                player.PvpServerLogoffPending = true;
                player.Session?.LogOffPlayer(true);
            }
        }

        /// <summary>
        /// The one grace warning text, shared by the sweep and the arrival check so the two cannot drift.
        /// </summary>
        private static void SendGraceWarning(Player player, long graceSeconds)
        {
            player.Session?.Network.EnqueueSend(new GameMessageSystemChat($"Too many characters from your network address are in the world. Return to the Marketplace, go to a house, join an arena match or Proving Grounds run, or log another character off, within {graceSeconds} seconds or this character will be logged off.", ChatMessageType.Broadcast));
        }

        public enum ArrivalOutcome
        {
            Allow,
            Grace,
            Boot,
        }

        /// <summary>
        /// Pure. What to do with an arriving character. Not violating: Allow. Violating with a FRESH activity
        /// stamp (the sweep saw it in an arena match or Proving Grounds run within dials.TailSeconds of now, and
        /// that reason's own dial is still on): Grace, because it is landing outside only because the run ended
        /// and deserves the normal warning. Violating with a stale or absent stamp (a lifestone recall): Boot.
        /// Freshness is measured at <paramref name="departedAt"/> (when the teleport STARTED), not at arrival: the
        /// stamp cannot refresh once the teleport begins, so transit and queue deferral must not age it out.
        /// </summary>
        internal static ArrivalOutcome DecideArrival(bool violates, double? activitySeenAt, IpLimitConfinementReason? activityReason, double departedAt, IpLimitZoneDials dials)
        {
            if (!violates)
                return ArrivalOutcome.Allow;

            if (dials.TailSeconds > 0 && activitySeenAt != null && activityReason != null && IsActivityDialOn(activityReason.Value, dials))
            {
                var elapsed = departedAt - activitySeenAt.Value;
                if (elapsed >= 0 && elapsed <= dials.TailSeconds)
                    return ArrivalOutcome.Grace;
            }

            return ArrivalOutcome.Boot;
        }

        /// <summary>
        /// Pure. Whether a character that has just ARRIVED (teleport or login) at a spot would violate the
        /// limit. Residents already on a grace warning (<paramref name="knownViolatorGuids"/>) and residents
        /// that SelectViolators picks among themselves are dropped first: they are already violators, and the
        /// arriving character must not be booted for a conflict it did not cause. The mover is then evaluated
        /// as the incoming candidate with level int.MinValue, so it ALWAYS loses a contested slot: the
        /// character whose teleport created the conflict is removed, never the one that was legitimately out.
        /// </summary>
        internal static bool ArrivalViolates(bool moverConfined, IReadOnlyList<IpLimitCandidate> residents, IReadOnlyCollection<uint> knownViolatorGuids, int maxFree, int maxConfined)
        {
            residents ??= Array.Empty<IpLimitCandidate>();

            var selfViolators = new HashSet<uint>(SelectViolators(residents, maxFree, maxConfined));

            var remaining = residents
                .Where(r => !selfViolators.Contains(r.Guid) && (knownViolatorGuids == null || !knownViolatorGuids.Contains(r.Guid)))
                .ToList();

            var mover = new IpLimitCandidate(0, int.MinValue, moverConfined, double.MaxValue);

            return Evaluate(mover, remaining, maxFree, maxConfined).Action == IpLimitAction.Refuse;
        }

        /// <summary>
        /// Immediate enforcement on teleport arrival (and login completion), called from
        /// Player.OnTeleportComplete after the challenge-exit reconciliations. Gated by ip_limit_enabled and
        /// ip_limit_enforce_on_arrival. Never throws: a failure here must not break teleport completion.
        /// </summary>
        public static void CheckOnArrival(Player player)
        {
            try
            {
                if (player == null || !Enabled)
                    return;

                if (!PropertyManager.GetBool("ip_limit_enforce_on_arrival").Item)
                    return;

                var session = player.Session;
                if (session == null || player.IsLoggingOut || IsExempt(session))
                    return;

                var maxFree = ClampCap(PropertyManager.GetLong("ip_limit_max_free").Item);
                var maxConfined = ClampCap(PropertyManager.GetLong("ip_limit_max_confined").Item);

                var householdKey = HouseholdKey(session);
                var residents = GetResidents(householdKey, session.AccountId);

                // With no other residents only maxFree 0 can still make the arrival a violation.
                if (residents.Count == 0 && maxFree > 0)
                    return;

                var residentGuids = new HashSet<uint>(residents.Select(r => r.Guid));
                var known = PlayerManager.GetAllOnline()
                    .Where(p => residentGuids.Contains(p.Guid.Full) && p.IpLimitGraceExpiry != null)
                    .Select(p => p.Guid.Full)
                    .ToList();

                var violates = ArrivalViolates(IsConfined(player), residents, known, maxFree, maxConfined);

                var outcome = DecideArrival(violates, player.IpLimitActivitySeenAt, player.IpLimitActivityReason, player.LastTeleportStartTimestamp ?? Time.GetUnixTime(), IpLimitZoneTunables.DialSource());

                if (outcome == ArrivalOutcome.Allow)
                    return;

                if (outcome == ArrivalOutcome.Grace)
                {
                    // Landed outside because an arena match or Proving Grounds run just ended: normal grace path.
                    if (player.IpLimitGraceExpiry == null)
                    {
                        var graceSeconds = Math.Max(0, PropertyManager.GetLong("ip_limit_grace_seconds").Item);
                        player.IpLimitGraceExpiry = Time.GetFutureUnixTime(graceSeconds);
                        SendGraceWarning(player, graceSeconds);
                    }

                    return;
                }

                log.Info($"[IP limit] logging off {player.Name} (level {player.Level ?? 0}, account {session.Account}, household {householdKey}, address {session.EndPointC2S?.Address}, landblock {player.Location?.LandblockShort:X4}) - arrived at a spot that is over the address limit.");

                session.Network.EnqueueSend(new GameMessageSystemChat("You have been logged off: another character from your network address is already in the world outside the Marketplace, a house, or an arena or Proving Grounds run, so this character cannot be out there too. Log your other character off or stay inside one of those places.", ChatMessageType.Broadcast));

                player.IpLimitGraceExpiry = null;
                player.PvpServerLogoffPending = true;
                session.LogOffPlayer(true);
            }
            catch (Exception ex)
            {
                log.Error($"[IP limit] arrival check failed for {player?.Name}", ex);
            }
        }

        /// <summary>
        /// Parses the mule_landblocks list via the shared <see cref="LandblockRealmList"/> parser: entries are
        /// <c>LLLL</c> (any realm) or <c>LLLL@R</c> (realm R only), a malformed entry is SKIPPED with a logged
        /// warning rather than thrown - a bad config string must never break login. Bare entries keep the
        /// pre-realm behaviour of this list exactly, which matched ephemeral instances too
        /// (<see cref="MuleLandblocksBareMatchesEphemeral"/>).
        /// </summary>
        public static LandblockRealmList GetMuleLandblocks()
        {
            return LandblockRealmList.Parse(PropertyManager.GetString(MuleLandblocksConfigKey).Item, MuleLandblocksConfigKey, MuleLandblocksBareMatchesEphemeral);
        }

        /// <summary>
        /// mule_landblocks matched on LandblockShort alone before it became realm-aware, so a bare entry also
        /// matched an ephemeral instance of that landblock. Kept, so existing bare rows behave exactly as before.
        /// </summary>
        internal const bool MuleLandblocksBareMatchesEphemeral = true;

        /// <summary>
        /// Pure: whether a character at <paramref name="landblock"/> in instance <paramref name="instance"/>
        /// stands in a mule landblock. Shared by the live confinement check (current Location) and the login
        /// gate (the PERSISTED location, whose instance must be honoured too - a character logged out in the
        /// realm-0 copy of a realm-scoped mule landblock is not in the Marketplace).
        /// </summary>
        internal static bool IsInMuleLandblock(LandblockRealmList muleLandblocks, ushort landblock, uint instance)
        {
            return muleLandblocks != null && muleLandblocks.ContainsInstance(landblock, instance);
        }

        /// <summary>
        /// Landblock ids (ObjCellId >> 16) holding at least one House object or SlumLord in the world DB content, all
        /// four HouseTypes. House objects are included because mansion and villa houses often sit on landblocks away
        /// from their slumlord, and this set must match Landblock.Houses (the /mule summon housing rule). Built on
        /// first use from DatabaseManager.World.GetHousingObjCellIds (not HouseList.FindAllHouses, which writes to the
        /// console, and not GetHousesAll, which is slumlord-only), so it works for an OFFLINE character's persisted
        /// location without the landblock being loaded. Instance-agnostic: world content is the same in every
        /// instance (ephemeral copies are excluded separately, see IsHousingPlacement). A successful build is cached
        /// for the process lifetime and the set is never mutated after it is published, so concurrent readers are
        /// safe. A FAILED build is never cached as a set, but is negatively cached for
        /// <see cref="HousingSetCache.RetryAfterSeconds"/> so a down world DB costs one query and one log line per
        /// window instead of one per player per sweep.
        /// </summary>
        private static readonly HousingSetCache housingLandblocks = new HousingSetCache(LiveHouseSource, Time.GetUnixTime);

        private static IEnumerable<uint> LiveHouseSource()
            => ACE.Database.DatabaseManager.World.GetHousingObjCellIds();

        /// <summary>
        /// Holds the housing landblock set. Success: cached forever. Failure (zero rows or any exception from the
        /// source): returns "not housing" WITHOUT touching the source or logging until RetryAfterSeconds have
        /// passed on the injected clock, then tries again. The source and clock are injected so tests need no
        /// world database.
        /// </summary>
        internal sealed class HousingSetCache
        {
            internal const double RetryAfterSeconds = 60;

            private readonly Func<IEnumerable<uint>> source;
            private readonly Func<double> clock;
            private readonly object buildLock = new object();

            private volatile HashSet<ushort> set;
            private double retryAt;

            internal HousingSetCache(Func<IEnumerable<uint>> source, Func<double> clock)
            {
                this.source = source;
                this.clock = clock;
            }

            internal bool Contains(ushort landblock)
            {
                var cached = set;
                if (cached != null)
                    return cached.Contains(landblock);

                lock (buildLock)
                {
                    cached = set;
                    if (cached != null)
                        return cached.Contains(landblock);

                    var now = clock();
                    if (now < retryAt)
                        return false;

                    try
                    {
                        cached = BuildHousingLandblocks(source);
                        set = cached;
                        return cached.Contains(landblock);
                    }
                    catch (Exception ex)
                    {
                        retryAt = now + RetryAfterSeconds;
                        log.Error($"[IPLIMIT] could not build the housing landblock set from the world database; treating every landblock as non-housing for {RetryAfterSeconds:0} seconds", ex);
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Builds the landblock set from a source of house ObjCellIds. THROWS InvalidOperationException on zero rows: a world
        /// DB that is not populated yet (or a failed query that returned nothing) must never publish an empty set,
        /// because an empty set would be cached for the process lifetime and silently disable the whole rule.
        /// </summary>
        internal static HashSet<ushort> BuildHousingLandblocks(Func<IEnumerable<uint>> houseSource)
        {
            var set = new HashSet<ushort>();

            foreach (var objCellId in houseSource())
                set.Add((ushort)(objCellId >> 16));

            if (set.Count == 0)
                throw new InvalidOperationException("the world database returned no houses; refusing to cache an empty housing landblock set");

            return set;
        }

        /// <summary>
        /// Whether the landblock holds a house in the world DB content. A lookup failure reads as "no" (logged once
        /// per retry window, see <see cref="HousingSetCache"/>), the conservative direction: the character then
        /// counts against the free cap instead of slipping past it.
        /// </summary>
        internal static bool IsHousingLandblock(ushort landblock)
            => housingLandblocks.Contains(landblock);

        /// <summary>
        /// Pure: whether a spot counts as AT A HOUSE. An ephemeral realm instance (a Thread dungeon or other
        /// private instance that shares the landblock id with a housing landblock) is not at a house, so the
        /// ephemeral check runs first and the set is never consulted for it.
        /// </summary>
        internal static bool IsHousingPlacement(bool isEphemeralRealm, ushort landblock, Func<ushort, bool> isHousingLandblock)
        {
            if (isEphemeralRealm)
                return false;

            return isHousingLandblock(landblock);
        }
        /// <summary>
        /// Pure: the whole "character in housing" rule for callers that have only a landblock (the login gate).
        /// Applies to ANY character, mule or not. Both checks must hold: the landblock holds a house, and the
        /// ip_limit_confine_housing dial is on. The set check runs first and the dial is an injected delegate so it
        /// is only read once the set check passes, and so tests need no PropertyManager or world DB.
        /// </summary>
        internal static bool IsInHousingCore(
            bool isEphemeralRealm,
            ushort landblock,
            Func<ushort, bool> isHousingLandblock,
            Func<bool> housingDialOn)
        {
            if (!IsHousingPlacement(isEphemeralRealm, landblock, isHousingLandblock))
                return false;

            return housingDialOn();
        }

        /// <summary>
        /// Live wiring of <see cref="IsInHousingCore"/>: the world-DB housing set and the ip_limit_confine_housing
        /// dial. Used by the login gate (the PERSISTED location of an offline character); the live check goes
        /// through <see cref="GetConfinementReason"/>, whose <see cref="IsConfinedCore"/> applies the same dial.
        /// </summary>
        public static bool IsInHousing(ushort landblock, uint instance)
        {
            // Same ephemeral test Position.IsEphemeralRealm uses, on the raw persisted instance value.
            ACE.Entity.Position.ParseInstanceID(instance, out var isEphemeralRealm, out _, out _);

            return IsInHousingCore(
                isEphemeralRealm,
                landblock,
                IsHousingLandblock,
                () => IpLimitZoneTunables.DialSource().ConfineHousing);
        }

        /// <summary>
        /// True if the session is a TRUE BYPASS of the IP active-player limit for any of: access level at or
        /// above ip_limit_exempt_access_level (unless that key is -1); a loopback remote address; or the remote
        /// address already appearing in the existing Config.js AllowUnlimitedSessionsFromIPAddresses allowlist.
        ///
        /// An account listed in ip_limit_exempt_accounts is deliberately NOT a bypass any more: it is a household
        /// of exactly one (see <see cref="HouseholdKeyCore"/>), so it is limited like any other household but
        /// never shares a household with the other accounts behind its address.
        /// </summary>
        public static bool IsExempt(Session session)
        {
            if (session == null)
                return true;

            var exemptAccessLevel = PropertyManager.GetLong("ip_limit_exempt_access_level").Item;
            if (exemptAccessLevel >= 0 && (long)session.AccessLevel >= exemptAccessLevel)
                return true;

            // Loopback and the access level are not households (the puzzle policy honours neither), and the address
            // allowlist stays a bypass here. The exempt-ACCOUNT list is no longer part of this check: it is a
            // household of one, resolved by HouseholdKeyCore.
            var address = session.EndPointC2S?.Address;
            if (address != null && IPAddress.IsLoopback(address))
                return true;

            return IsAddressAllowlistedCore(address, ReadHouseholdLists());
        }

        /// <summary>
        /// The household inputs: account names in ip_limit_exempt_accounts (each a household of exactly one),
        /// addresses in Config.js AllowUnlimitedSessionsFromIPAddresses (a bypass), and the admin-defined account
        /// clusters in ip_household_clusters (each a synthetic household). Reads PropertyManager, so call it on
        /// the world thread (or wherever IsExempt itself is already called).
        /// </summary>
        public sealed class HouseholdLists
        {
            public static readonly HouseholdLists Empty = new HouseholdLists(Array.Empty<string>(), Array.Empty<string>());

            public HouseholdLists(IReadOnlyCollection<string> exemptAccounts, IReadOnlyCollection<string> allowedAddresses, AccountClusters clusters = null)
            {
                ExemptAccounts = exemptAccounts ?? Array.Empty<string>();
                AllowedAddresses = allowedAddresses ?? Array.Empty<string>();
                Clusters = clusters ?? AccountClusters.Empty;
            }

            public IReadOnlyCollection<string> ExemptAccounts { get; }
            public IReadOnlyCollection<string> AllowedAddresses { get; }
            public AccountClusters Clusters { get; }
        }

        // Memoized on the raw inputs: every active-player check, claim and puzzle pull reads the household lists,
        // and the parse (plus its warning log) must run once per CHANGE, not once per read. One immutable snapshot
        // object is swapped atomically, so concurrent readers never see a torn pair.
        internal sealed class ListsSnapshot
        {
            public string ExemptRaw;
            public string ClustersRaw;
            public IReadOnlyCollection<string> AllowedRef;
            public HouseholdLists Lists;
        }

        private static ListsSnapshot listsSnapshot;

        // The canonical clusters value the last /iplimit cluster command wrote. A reparse that lands on exactly this
        // value is a command change, not a hand edit, so it does not log the hand-edit warning.
        private static volatile string lastWrittenClustersFormat;

        /// <summary>Test seam: what TryWriteClusters last recorded.</summary>
        internal static string LastWrittenClustersFormat => lastWrittenClustersFormat;

        // Test seams. RawSource is the three raw inputs (PropertyManager throws in unit tests); AccountIdResolver
        // is the advisory conflict check's name lookup, which is a DB read and so must never run on a reading thread.
        internal static Func<(string ExemptRaw, string ClustersRaw, IReadOnlyCollection<string> Allowed)> RawSource = ReadRawInputs;

        internal static Func<string, uint?> AccountIdResolver = name => ACE.Database.DatabaseManager.Authentication.GetAccountByName(name)?.AccountId;

        /// <summary>The most recent advisory conflict check (null before the first reparse). Exposed so a test can await it.</summary>
        internal static System.Threading.Tasks.Task LastConflictCheck;

        private static (string ExemptRaw, string ClustersRaw, IReadOnlyCollection<string> Allowed) ReadRawInputs()
            => (PropertyManager.GetString(ExemptAccountsConfigKey).Item ?? "",
                PropertyManager.GetString(ClustersConfigKey).Item ?? "",
                ConfigManager.Config?.Server?.Network?.AllowUnlimitedSessionsFromIPAddresses ?? Array.Empty<string>());

        /// <summary>
        /// Reads the household lists now. IN-MEMORY PARSING ONLY: this runs on the world tick and on landblock
        /// threads, so it never touches the database. The same raw inputs return the SAME instance; a changed input
        /// reparses, and the advisory exempt-vs-cluster conflict check (which does look accounts up) is then handed
        /// to a pool thread. Falls back to <see cref="HouseholdLists.Empty"/> if the inputs cannot be read.
        /// </summary>
        public static HouseholdLists ReadHouseholdLists()
        {
            try
            {
                var (exemptRaw, clustersRaw, allowed) = RawSource();

                var lists = ReadHouseholdListsCore(exemptRaw, clustersRaw, allowed, ref listsSnapshot, message => log.Warn(message), out var reparsed, lastWrittenClustersFormat);

                if (reparsed)
                {
                    var names = lists.ExemptAccounts;
                    var clusters = lists.Clusters;
                    var resolver = AccountIdResolver;

                    LastConflictCheck = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            foreach (var warning in ExemptClusterConflictWarnings(names, clusters, resolver))
                                log.Warn($"[IP household] {warning}");
                        }
                        catch (Exception)
                        {
                            // Advisory (a log line only); never let it cost anything.
                        }
                    });
                }

                return lists;
            }
            catch (Exception)
            {
                return HouseholdLists.Empty;
            }
        }

        /// <summary>
        /// Pure memoizing core of <see cref="ReadHouseholdLists"/>. Returns the snapshot's instance when all three
        /// inputs are unchanged (<paramref name="reparsed"/> false); otherwise parses, replaces the snapshot, and
        /// reports parse warnings - plus one line when the cluster membership differs from the previous snapshot -
        /// through <paramref name="warn"/>.
        /// </summary>
        internal static HouseholdLists ReadHouseholdListsCore(string exemptRaw, string clustersRaw, IReadOnlyCollection<string> allowed, ref ListsSnapshot snapshot, Action<string> warn, out bool reparsed, string lastWrittenFormat = null)
        {
            exemptRaw ??= "";
            clustersRaw ??= "";
            allowed ??= Array.Empty<string>();

            var snap = snapshot;
            if (snap != null && snap.ExemptRaw == exemptRaw && snap.ClustersRaw == clustersRaw && ReferenceEquals(snap.AllowedRef, allowed))
            {
                reparsed = false;
                return snap.Lists;
            }

            var clusters = AccountClusters.Parse(clustersRaw, out var warnings);
            foreach (var warning in warnings)
                warn?.Invoke($"[IP household] {ClustersConfigKey}: {warning}");

            if (snap != null && snap.Lists.Clusters.Format() != clusters.Format() && clusters.Format() != lastWrittenFormat)
                warn?.Invoke($"[IP household] {ClustersConfigKey} membership changed (was '{snap.Lists.Clusters.Format()}', now '{clusters.Format()}'). /iplimit cluster carries reward-claim history; a hand edit does not.");

            var lists = new HouseholdLists(ParseCsv(exemptRaw), allowed, clusters);
            snapshot = new ListsSnapshot { ExemptRaw = exemptRaw, ClustersRaw = clustersRaw, AllowedRef = allowed, Lists = lists };
            reparsed = true;
            return lists;
        }

        /// <summary>
        /// Pure: is this account name (case-insensitive) or this address (its exact ToString) on a household
        /// allowlist? NOT a bypass test (see <see cref="IsExempt"/>): it exists for the Thread puzzle key, which
        /// has always keyed both kinds per account. Null inputs simply do not match.
        /// </summary>
        public static bool IsHouseholdExemptCore(string accountName, IPAddress address, HouseholdLists lists)
        {
            if (lists == null)
                return false;

            return IsAccountExemptCore(accountName, lists) || IsAddressAllowlistedCore(address, lists);
        }

        /// <summary>Pure: is this account name (case-insensitive) on ip_limit_exempt_accounts? A household of one.</summary>
        public static bool IsAccountExemptCore(string accountName, HouseholdLists lists)
            => lists != null && !string.IsNullOrEmpty(accountName) && lists.ExemptAccounts.Contains(accountName, StringComparer.OrdinalIgnoreCase);

        /// <summary>Pure: is this address (its exact ToString) in AllowUnlimitedSessionsFromIPAddresses? A true bypass.</summary>
        public static bool IsAddressAllowlistedCore(IPAddress address, HouseholdLists lists)
            => lists != null && address != null && lists.AllowedAddresses.Contains(address.ToString());

        // ==================================================================================
        // Household key
        // ==================================================================================

        /// <summary>Prefix of a cluster household key.</summary>
        public const string ClusterKeyPrefix = "cluster:";

        /// <summary>Prefix of an exempt-account household-of-one key. Shared with ThreadPuzzleIpKey, which already used it.</summary>
        public const string AccountKeyPrefix = "acct:";

        /// <summary>The household key of a single account ("acct:&lt;id&gt;"). The one place this string is formatted.</summary>
        public static string AccountKey(uint accountId)
            => AccountKeyPrefix + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The household key of a cluster ("cluster:&lt;name&gt;"). The one place this string is formatted.</summary>
        public static string ClusterKey(string clusterName)
            => ClusterKeyPrefix + clusterName;

        /// <summary>
        /// Pure: the household an account belongs to, in order: its cluster ("cluster:&lt;name&gt;"); else, if the
        /// account is on ip_limit_exempt_accounts, a household of one ("acct:&lt;id&gt;"); else the connection,
        /// normalised exactly as reward claims always have (RewardClaimRules.NormalizeIpKey), so keys for ordinary
        /// accounts are byte-identical to the pre-cluster ones (note that an IPv4-mapped IPv6 address now shares a
        /// household with the plain IPv4 form of the same address in the active-player limit, where the old
        /// IPAddress.Equals grouping kept them apart; that is intended). Null when there is nothing to key on (no cluster,
        /// not exempt, no address). The caller's own bypass (access level, loopback, address allowlist) runs BEFORE
        /// this and is not repeated here. Keys are at most 45 characters and never parse as an IP address.
        ///
        /// <paramref name="accountName"/> is needed because ip_limit_exempt_accounts lists account NAMES.
        /// </summary>
        public static string HouseholdKeyCore(uint accountId, string accountName, IPAddress address, HouseholdLists lists)
        {
            lists ??= HouseholdLists.Empty;

            var cluster = accountId == 0 ? null : lists.Clusters.ClusterOf(accountId);
            if (cluster != null)
                return ClusterKey(cluster);

            if (accountId != 0 && IsAccountExemptCore(accountName, lists))
                return AccountKey(accountId);

            return RewardClaimRules.NormalizeIpKey(address, false);
        }

        /// <summary>The household key of a session. Null for a null session. See <see cref="HouseholdKeyCore"/>.</summary>
        public static string HouseholdKey(Session session)
            => session == null ? null : HouseholdKeyCore(session.AccountId, session.Account, session.EndPointC2S?.Address, ReadHouseholdLists());

        /// <summary>The household key of a session against lists the caller already read.</summary>
        public static string HouseholdKey(Session session, HouseholdLists lists)
            => session == null ? null : HouseholdKeyCore(session.AccountId, session.Account, session.EndPointC2S?.Address, lists);

        /// <summary>
        /// Every cluster membership change, and every add to or removal from ip_limit_exempt_accounts, routes
        /// through here with the account's address-independent key before and after (see
        /// <see cref="AddressIndependentKey"/>; null = keyed by address). Carries the account's reward_claim rows
        /// to the new household (<see cref="RewardClaimCarryForward"/>), atomically for the account. Blocks on the
        /// shard database: call it off the world thread.
        /// </summary>
        public static RewardClaimCarryForwardOutcome OnClusterMembershipChanged(uint accountId, string oldKey, string newKey)
            => RewardClaimCarryForward.Apply(RewardClaimService.Store, accountId, oldKey, newKey);

        /// <summary>
        /// True while the player counts as CONFINED under the IP active-player limit: inside a configured mule
        /// landblock, mid-teleport, standing in a landblock that holds a house (non-ephemeral instance, subject to
        /// ip_limit_confine_housing), inside a live PvP arena match or Proving Grounds run (subject to the
        /// ip_limit_confine_* dials), or still inside the post-activity tail after one of those. See
        /// <see cref="GetConfinementReason"/> for WHY, and <see cref="IsConfinedCore"/> for the pure rule this
        /// gathers facts for.
        /// </summary>
        public static bool IsConfined(Player player)
        {
            return GetConfinementReason(player) != IpLimitConfinementReason.None;
        }

        /// <summary>
        /// Whether the dial that governs <paramref name="reason"/> (one of PvpArena, SpeedRun, ProvingGrounds -
        /// the only reasons RefreshActivityStamp ever stamps) is currently on. Any other reason is not a stamped
        /// activity reason and returns false. Shared by the stamp side (RefreshActivityStamp) and the read side
        /// (IsWithinActivityTail) so both agree on which dial governs which reason.
        /// </summary>
        internal static bool IsActivityDialOn(IpLimitConfinementReason reason, IpLimitZoneDials dials)
        {
            switch (reason)
            {
                case IpLimitConfinementReason.PvpArena:
                    return dials.ConfineArena;
                case IpLimitConfinementReason.SpeedRun:
                    return dials.ConfineSpeed;
                case IpLimitConfinementReason.ProvingGrounds:
                    return dials.ConfineProving;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure. Whether a character stamped by RefreshActivityStamp still counts as inside the post-activity
        /// tail RIGHT NOW. All four conditions must hold:
        ///   1. The tail is enabled at all (dials.TailSeconds > 0).
        ///   2. There IS a stamp (<paramref name="seenAt"/> and <paramref name="stampedReason"/> both non-null).
        ///   3. The reason's OWN dial is still on at read time - a dial switched off after the stamp stops its
        ///      tail immediately rather than letting an already-stamped character ride out the remaining window
        ///      under a switch that is no longer enforcing anything.
        ///   4. The character is still inside an ephemeral realm instance, and it is the EXACT SAME instance
        ///      (<paramref name="currentInstance"/> == <paramref name="stampedInstance"/>) it was stamped in -
        ///      moving into some OTHER ephemeral instance (a Thread dungeon, a private instance) within the
        ///      window does not carry the tail along.
        /// Then, and only then, elapsed time against <paramref name="dials"/>.TailSeconds decides it.
        /// </summary>
        internal static bool IsWithinActivityTail(
            double? seenAt,
            uint? stampedInstance,
            IpLimitConfinementReason? stampedReason,
            bool isEphemeralRealm,
            uint currentInstance,
            double now,
            IpLimitZoneDials dials)
        {
            if (dials.TailSeconds <= 0)
                return false;

            if (seenAt == null || stampedReason == null)
                return false;

            if (!IsActivityDialOn(stampedReason.Value, dials))
                return false;

            if (!isEphemeralRealm)
                return false;

            if (stampedInstance != currentInstance)
                return false;

            var elapsed = now - seenAt.Value;
            return elapsed >= 0 && elapsed <= dials.TailSeconds;
        }

        /// <summary>
        /// Gathers the live facts (landblock, teleport state, arena/Proving-Grounds instance membership, the
        /// post-activity tail stamp) and resolves them against <see cref="IsConfinedCore"/>. Read-only - it
        /// does NOT stamp <see cref="Player.IpLimitActivitySeenAt"/>; only <see cref="Tick"/> does that, so a
        /// read-only caller (the login gate's GetResidents, /iplimitwho) can never itself extend a character's
        /// tail window just by looking.
        ///
        /// The tail requires THREE things to still hold, not just elapsed time: the stamping reason's own dial
        /// must still be on (a dial switched off after the stamp stops its tail immediately), the character
        /// must still be inside the EXACT instance it was stamped in (Location.Instance == IpLimitActivityInstance
        /// - moving into some other ephemeral instance, such as a Thread dungeon or a private instance, within
        /// the window does not carry the tail along), and that instance must still be ephemeral at all.
        /// </summary>
        public static IpLimitConfinementReason GetConfinementReason(Player player)
        {
            if (player == null)
                return IpLimitConfinementReason.None;

            var dials = IpLimitZoneTunables.DialSource();

            var teleporting = player.Teleporting;

            var inMuleLandblock = false;
            var inHousing = false;
            var location = player.Location;
            if (location != null)
            {
                inMuleLandblock = IsInMuleLandblock(GetMuleLandblocks(), (ushort)location.LandblockShort, location.Instance);
                inHousing = IsHousingPlacement(location.IsEphemeralRealm, (ushort)location.LandblockShort, IsHousingLandblock);
            }

            var inArenaMatchSpace = player.IsInPvpArenaMatchSpace;
            var inSpeedRun = player.IsInSpeedChallengeInstance;
            var inProvingGroundsRun = player.IsInDpsChallengeInstance || player.IsInSurvivalChallengeInstance || player.IsInWaveChallengeInstance;

            var inActivityTail = IsWithinActivityTail(
                player.IpLimitActivitySeenAt,
                player.IpLimitActivityInstance,
                player.IpLimitActivityReason,
                player.Location?.IsEphemeralRealm ?? false,
                player.Location?.Instance ?? 0,
                Time.GetUnixTime(),
                dials);

            return IsConfinedCore(teleporting, inMuleLandblock, inHousing, inArenaMatchSpace, inSpeedRun, inProvingGroundsRun, inActivityTail, dials);
        }

        /// <summary>
        /// Stamps <see cref="Player.IpLimitActivitySeenAt"/> (plus <see cref="Player.IpLimitActivityInstance"/>
        /// and <see cref="Player.IpLimitActivityReason"/>) while the player is currently inside a live PvP
        /// arena match or Proving Grounds run whose OWN dial has switched it on - a zone whose dial is off never
        /// stamps, so turning every ip_limit_confine_* dial off (all four, housing included, though housing never stamps)
        /// reduces this whole feature to the Marketplace-only rule (the "every dial off" control in IpLimitConfinementTests), rather than leaving a stale tail able to
        /// confine a character through a zone that is not supposed to confine at all. Checked in the same
        /// priority order as IsConfinedCore (arena, then speed, then proving) so the recorded reason is the one
        /// that would actually be confining the character right now.
        ///
        /// Called once per player per <see cref="Tick"/> pass, BEFORE that player's <see cref="IsConfined"/> is
        /// evaluated for this same sweep, so the activity-tail window always extends from the last sweep that
        /// actually saw the activity term true, not from a stale earlier one.
        /// </summary>
        private static void RefreshActivityStamp(Player player)
        {
            if (player == null)
                return;

            var dials = IpLimitZoneTunables.DialSource();

            IpLimitConfinementReason? active = null;

            if (player.IsInPvpArenaMatchSpace && dials.ConfineArena)
                active = IpLimitConfinementReason.PvpArena;
            else if (player.IsInSpeedChallengeInstance && dials.ConfineSpeed)
                active = IpLimitConfinementReason.SpeedRun;
            else if ((player.IsInDpsChallengeInstance || player.IsInSurvivalChallengeInstance || player.IsInWaveChallengeInstance) && dials.ConfineProving)
                active = IpLimitConfinementReason.ProvingGrounds;

            if (active == null)
                return;

            player.IpLimitActivitySeenAt = Time.GetUnixTime();
            player.IpLimitActivityInstance = player.Location?.Instance;
            player.IpLimitActivityReason = active;
        }

        /// <summary>
        /// All online players in household `householdKey` (see <see cref="HouseholdKey(Session)"/>), excluding: a
        /// null Session; a player currently logging out; a true-bypass session (IsExempt); and any player whose
        /// Session.AccountId == excludeAccountId. That last exclusion is load-bearing - it stops a player's own
        /// reconnect (whose stale prior session is briefly still counted as online) from booting their own other
        /// character.
        /// </summary>
        public static List<IpLimitCandidate> GetResidents(string householdKey, uint excludeAccountId)
        {
            var result = new List<IpLimitCandidate>();

            if (householdKey == null)
                return result;

            var lists = ReadHouseholdLists();

            foreach (var player in PlayerManager.GetAllOnline())
            {
                var session = player.Session;
                if (session == null)
                    continue;

                if (player.IsLoggingOut)
                    continue;

                if (session.AccountId == excludeAccountId)
                    continue;

                if (IsExempt(session))
                    continue;

                if (!string.Equals(HouseholdKey(session, lists), householdKey, StringComparison.Ordinal))
                    continue;

                result.Add(new IpLimitCandidate(player.Guid.Full, player.Level ?? 0, IsConfined(player), player.LoginTimestamp ?? 0));
            }

            return result;
        }

        /// <summary>
        /// Pure: groups items by household key, preserving input order inside each group. An item whose key is
        /// null (nothing to key on) is dropped. This is the grouping the sweep applies before SelectViolators,
        /// extracted so a test can drive it with <see cref="HouseholdKeyCore"/> against raw-address grouping.
        /// </summary>
        public static Dictionary<string, List<T>> GroupByHousehold<T>(IEnumerable<T> items, Func<T, string> keyOf)
        {
            var groups = new Dictionary<string, List<T>>(StringComparer.Ordinal);

            foreach (var item in items)
            {
                var key = keyOf(item);
                if (key == null)
                    continue;

                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<T>();
                    groups[key] = group;
                }

                group.Add(item);
            }

            return groups;
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
        /// Normalizes a mule_landblocks entry to its canonical spelling via
        /// <see cref="LandblockRealmList.NormalizeEntry"/>: a bare landblock (0x016C or 016C, any case) becomes
        /// 4 upper-case hex digits, and a realm-scoped one (01f5@1, 0x01F5 @ 1) becomes <c>01F5@1</c>.
        /// Returns null if the token is not a valid entry.
        /// </summary>
        public static string NormalizeLandblockToken(string token)
        {
            return LandblockRealmList.NormalizeEntry(token);
        }

        private const string LandblockTokenHelp = "expected LLLL or LLLL@R, e.g. 016C, 0x016C or 01F5@1";

        /// <summary>
        /// Current mule_landblocks entries in canonical spelling (016C, 01F5@1), de-duplicated, sorted.
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
        /// Adds a landblock entry (016C, 0x016C, or realm-scoped 01F5@1) to mule_landblocks. False with a message if malformed, already
        /// present, or the config write fails.
        /// </summary>
        public static bool TryAddMuleLandblock(string token, out string message)
        {
            var normalized = NormalizeLandblockToken(token);
            if (normalized == null)
            {
                message = $"'{token}' is not a valid landblock entry ({LandblockTokenHelp}).";
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
                message = $"'{token}' is not a valid landblock entry ({LandblockTokenHelp}).";
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

        // ==================================================================================
        // Cluster admin helpers
        // ==================================================================================

        /// <summary>The current clusters (memoized with the rest of the household lists).</summary>
        public static AccountClusters ReadClusters() => ReadHouseholdLists().Clusters;

        /// <summary>
        /// Writes <paramref name="clusters"/> back to ip_household_clusters in canonical form. False with a
        /// message if the config write fails.
        /// </summary>
        public static bool TryWriteClusters(AccountClusters clusters, out string message)
        {
            var format = (clusters ?? AccountClusters.Empty).Format();

            // Recorded BEFORE the write so a read that races it already knows this change came from the command
            // (ReadHouseholdListsCore skips its hand-edit warning for exactly this value).
            var previous = lastWrittenClustersFormat;
            lastWrittenClustersFormat = format;

            if (!PropertyManager.ModifyString(ClustersConfigKey, format))
            {
                lastWrittenClustersFormat = previous;
                message = $"Failed to write '{ClustersConfigKey}' - is it declared in DefaultStringProperties?";
                return false;
            }

            message = null;
            return true;
        }

        /// <summary>
        /// Pure: the refusal text for putting an account into cluster <paramref name="targetCluster"/>, or null if
        /// allowed. An account has at most one household: another cluster, or its own exempt-list entry (a cluster
        /// of one), both refuse.
        /// </summary>
        public static string ClusterMembershipRefusal(uint accountId, string accountName, string targetCluster, HouseholdLists lists)
        {
            lists ??= HouseholdLists.Empty;

            var existing = lists.Clusters.ClusterOf(accountId);
            if (existing != null && !string.Equals(existing, targetCluster, StringComparison.Ordinal))
                return $"'{accountName}' is already in cluster '{existing}'. An account can be in only one cluster.";

            if (IsAccountExemptCore(accountName, lists))
                return $"'{accountName}' is already in its own household (an exempt-list account is a cluster of one). Remove it from the exempt list first.";

            return null;
        }

        /// <summary>
        /// Pure: the refusal text for putting an account on the exempt list, or null if allowed. An account already
        /// in a cluster has its household; remove it from the cluster first.
        /// </summary>
        public static string ExemptMembershipRefusal(uint accountId, string accountName, HouseholdLists lists)
        {
            var existing = (lists ?? HouseholdLists.Empty).Clusters.ClusterOf(accountId);
            return existing == null
                ? null
                : $"'{accountName}' is already in cluster '{existing}'. An account can have only one household; remove it from the cluster first.";
        }

        /// <summary>
        /// Pure: warnings for a hand-edited config that puts an account in both a cluster and the exempt list.
        /// The cluster entry is kept and the account's acct: singleton ignored (HouseholdKeyCore checks the cluster
        /// first), the same deterministic resolution as an account listed in two clusters. The resolver maps an
        /// account name to its id (null if unknown).
        /// </summary>
        public static List<string> ExemptClusterConflictWarnings(IEnumerable<string> exemptNames, AccountClusters clusters, Func<string, uint?> resolveId)
        {
            var warnings = new List<string>();
            if (clusters == null || clusters.Count == 0)
                return warnings;

            foreach (var name in exemptNames ?? Array.Empty<string>())
            {
                var id = resolveId(name);
                var cluster = id.HasValue ? clusters.ClusterOf(id.Value) : null;
                if (cluster != null)
                    warnings.Add($"account '{name}' (#{id}) is on {ExemptAccountsConfigKey} and in cluster '{cluster}'; it stays in '{cluster}' and its acct: household is ignored");
            }

            return warnings;
        }

        /// <summary>
        /// The key an account is household-keyed under independent of any address: "cluster:&lt;name&gt;", "acct:&lt;id&gt;",
        /// or null when it is an ordinary account (keyed by whatever address it connects from). Used to describe
        /// a membership change to <see cref="OnClusterMembershipChanged"/>.
        /// </summary>
        public static string AddressIndependentKey(uint accountId, string accountName, HouseholdLists lists)
            => HouseholdKeyCore(accountId, accountName, null, lists);
    }
}
