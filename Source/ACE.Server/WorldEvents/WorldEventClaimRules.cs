using System;
using System.Collections.Generic;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Why a Weave Cache claim was refused (TECH-DESIGN 5.6 - these names are fixed; the denial log line
    /// writes result=denied:&lt;enum name&gt;, so renaming a member breaks the monitoring queries).
    /// New members are only ever APPENDED, so every existing token keeps its name.
    /// </summary>
    public enum ClaimDenial
    {
        None,
        NotRewarding,
        AlreadyClaimedCharacter,
        AlreadyClaimedAccount,
        AlreadyClaimedIp,

        /// <summary>
        /// The run's claim policy pays nothing for this outcome (<see cref="FailureRewardMode.None"/> on a
        /// failed run). Never returned under <see cref="WorldEventClaimPolicy.Default"/>.
        /// </summary>
        NoRewardForOutcome
    }

    /// <summary>
    /// The result of <see cref="WorldEventClaimRules.ResolvePresencePayout"/>.
    /// </summary>
    public readonly struct PresencePayout
    {
        /// <summary>The presence switch is off: the caller runs the damage/kill-credit rule exactly as before.</summary>
        public bool UsesCreditRule { get; }

        /// <summary>Pay the reward axis's coal wcid instead of the outcome crate.</summary>
        public bool PayCoal { get; }

        /// <summary>Stamp <see cref="Multiplier"/> and the reward profile on the created crate (success crate only).</summary>
        public bool StampMultiplier { get; }

        /// <summary>The crate's multiplier m: 2p when known, 1.0 when unknown, 0 for coal.</summary>
        public double Multiplier { get; }

        /// <summary>The presence fraction was known.</summary>
        public bool Known { get; }

        /// <summary>The presence fraction p (0..1); 0 when unknown.</summary>
        public double Fraction { get; }

        public PresencePayout(bool usesCreditRule, bool payCoal, bool stampMultiplier, double multiplier, bool known, double fraction)
        {
            UsesCreditRule = usesCreditRule;
            PayCoal = payCoal;
            StampMultiplier = stampMultiplier;
            Multiplier = multiplier;
            Known = known;
            Fraction = fraction;
        }
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
            => Evaluate(characterGuid, accountId, ip, ip, claimedCharacters, claimedAccounts, claimedIps, exemptIps, gateByCharacter, gateByAccount, gateByIp);

        /// <summary>
        /// The household-aware form. The IP rule is held against <paramref name="householdKey"/> (what
        /// IpLimitManager.HouseholdKey returns: an address for an ordinary account, "cluster:name" or "acct:id"
        /// otherwise) and <paramref name="claimedIps"/> holds those keys. <paramref name="exemptIps"/>
        /// (world_events_ip_exempt) is still matched against the RAW <paramref name="ip"/>, so an exempt address
        /// stays exempt whatever household its claimant is in.
        /// </summary>
        public static ClaimDenial Evaluate(uint characterGuid, uint accountId, string ip, string householdKey,
            ISet<uint> claimedCharacters, ISet<uint> claimedAccounts, ISet<string> claimedIps, ISet<string> exemptIps,
            bool gateByCharacter, bool gateByAccount, bool gateByIp)
        {
            if (gateByCharacter && claimedCharacters != null && claimedCharacters.Contains(characterGuid))
                return ClaimDenial.AlreadyClaimedCharacter;

            if (gateByAccount && accountId != 0 && claimedAccounts != null && claimedAccounts.Contains(accountId))
                return ClaimDenial.AlreadyClaimedAccount;

            if (gateByIp)
            {
                var key = NormalizeIp(householdKey);
                var rawIp = NormalizeIp(ip);

                if (!string.IsNullOrEmpty(key) && !ContainsIp(exemptIps, rawIp) && ContainsIp(claimedIps, key))
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

                case ClaimDenial.NoRewardForOutcome:
                    return $"The {name} holds nothing for an event that ended this way.";

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
        /// The presence-payout decision, as pure arithmetic (repo owner rulings 2026-10-19). Replaces
        /// <see cref="PaysCoal"/>'s credit rule ONLY while world_events_presence_payout_enabled is on; with the
        /// switch off the result says <see cref="PresencePayout.UsesCreditRule"/> and the caller runs today's
        /// credit rule unchanged.
        ///
        /// Switch on:
        ///   * presence known and exactly 0, and the axis has a coal wcid: coal;
        ///   * presence known and above 0: the outcome crate, multiplier m = 2p clamped to [0, 2];
        ///   * presence unknown (no arena, no ledger, an empty window): the outcome crate at m 1.0 - never coal,
        ///     never above 1;
        ///   * a known 0 with no coal wcid (coalWcid 0): the outcome crate at m 1.0 (an axis without coal has
        ///     no booby prize, so absence is not punished by a zero-roll crate).
        /// Only a SUCCESS crate is scaled (<see cref="PresencePayout.StampMultiplier"/>): the consolation crate of
        /// a failed run is unchanged, one per present claimant, not scaled.
        /// </summary>
        public static PresencePayout ResolvePresencePayout(bool presenceEnabled, uint coalWcid, bool fractionKnown, double fraction, bool paysSuccess)
        {
            if (!presenceEnabled)
                return new PresencePayout(true, false, false, 1.0, fractionKnown, fraction);

            if (!fractionKnown || double.IsNaN(fraction) || double.IsInfinity(fraction))
                return new PresencePayout(false, false, paysSuccess, 1.0, false, 0);

            var p = Math.Max(0.0, Math.Min(1.0, fraction));

            if (p <= 0)
            {
                if (coalWcid != 0)
                    return new PresencePayout(false, true, false, 0.0, true, 0);

                return new PresencePayout(false, false, paysSuccess, 1.0, true, 0);
            }

            return new PresencePayout(false, false, paysSuccess, paysSuccess ? 2.0 * p : 1.0, true, p);
        }

        /// <summary>
        /// The player-facing line for a presence payout, or null when none is sent: a stamped crate gets its
        /// percentage and multiplier, a presence coal gets the Non Participation line. The credit rule's own coal
        /// text is the handler's, unchanged.
        /// </summary>
        public static string PresenceMessage(PresencePayout payout)
        {
            if (payout.UsesCreditRule)
                return null;

            if (payout.PayCoal)
                return "You were never inside the event ring, so you receive a Non Participation Award.";

            // A known 0 that is not coal (an axis with no coalWcid) gets its crate without a "present for 0%" line.
            if (payout.StampMultiplier && payout.Known && payout.Fraction > 0)
                return $"You were present for {PresencePercent(payout.Fraction)}% of the event. Your Event Crate carries a {payout.Multiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}x reward.";

            return null;
        }

        /// <summary>
        /// What the claim handler stamps on the created object, as pure arithmetic. <paramref name="payCoal"/> is
        /// what the handler ACTUALLY paid (false when a failed coal create fell back to the crate);
        /// <paramref name="paysSuccess"/> is whether the outcome pays the success crate.
        ///   * switch off (credit rule): nothing;
        ///   * coal paid: nothing;
        ///   * coal fell back to the crate: m 1.0 on a success crate (unknown, never the coal's 0), nothing on a
        ///     consolation crate;
        ///   * otherwise: the payout's own multiplier when it stamps (a success crate), else nothing.
        /// </summary>
        public static (bool Stamp, double Multiplier) ResolveStamp(PresencePayout payout, bool payCoal, bool paysSuccess)
            => ResolveStamp(payout, payout.PayCoal, payCoal, paysSuccess);

        /// <summary>
        /// The pipeline form of <see cref="ResolveStamp(PresencePayout, bool, bool)"/>: <paramref name="coalDecided"/>
        /// is whether the claim was DECIDED coal for any reason (the presence rule or a policy threshold, i.e.
        /// <see cref="ClaimPayout.PayCoal"/>), and <paramref name="coalPaid"/> is whether coal was actually paid.
        /// Any coal-decided claim whose coal create fell back to the crate is stamped m 1.0 on a success crate,
        /// never the presence multiplier: a claimant under a threshold must not receive a scaled crate because a
        /// coal wcid failed to create. With coalDecided equal to the presence payout's own PayCoal this is exactly
        /// the three-argument form.
        /// </summary>
        public static (bool Stamp, double Multiplier) ResolveStamp(PresencePayout payout, bool coalDecided, bool coalPaid, bool paysSuccess)
        {
            if (payout.UsesCreditRule || coalPaid)
                return (false, 0);

            if (coalDecided)
                return paysSuccess ? (true, 1.0) : (false, 0);

            return payout.StampMultiplier ? (true, payout.Multiplier) : (false, 0);
        }

        /// <summary>
        /// The presence suffix for the claim log line, appended after payout=: " presence=&lt;pct|unknown&gt;
        /// mult=&lt;m|none&gt;", where mult is the multiplier actually stamped on the crate and "none" when nothing
        /// was stamped (coal, or an unscaled consolation crate). Empty under the credit rule, so the line is
        /// unchanged with the switch off.
        /// </summary>
        public static string PresenceLogSuffix(PresencePayout payout, (bool Stamp, double Multiplier) stamp)
        {
            if (payout.UsesCreditRule)
                return "";

            var pct = payout.Known ? PresencePercent(payout.Fraction).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
            var mult = stamp.Stamp ? stamp.Multiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "none";

            return $" presence={pct} mult={mult}";
        }

        // ---- The claim pipeline (slice R3, 2026-11-01) ------------------------------------------------------
        //
        // Four stages: ResolveEligibility -> ResolvePayout -> ResolveExtras are pure and live here; delivery (the
        // gives, the marking and WorldEventShareDelivery.TryDeliver) lives in WorldEventCacheHandler. Under
        // WorldEventClaimPolicy.Default with no bonus crates every decision is the pre-pipeline one.

        /// <summary>
        /// Stage 1, eligibility: crate or coal. Runs <see cref="ResolvePresencePayout"/> and, while the presence
        /// switch is off, <see cref="PaysCoal"/> exactly as the handler did before the pipeline, then applies the
        /// policy's thresholds: a claimant below either NONZERO threshold is paid coal.
        ///
        /// The thresholds are fail-open in the same directions as the rules they sit beside: a threshold is
        /// skipped when the axis has no coal wcid (there is no coal to pay), and when its input is unknown (no
        /// presence ledger entry, or a run whose total damage is 0), so an unknown never downgrades a claim.
        /// </summary>
        public static ClaimEligibility ResolveEligibility(in ClaimInputs inputs)
        {
            var presence = ResolvePresencePayout(inputs.PresenceEnabled, inputs.CoalWcid, inputs.PresenceKnown, inputs.PresenceFraction, inputs.PaysSuccess);

            if (presence.UsesCreditRule)
            {
                if (PaysCoal(inputs.CreditEnabled, inputs.CoalWcid, inputs.HasCredit))
                    return new ClaimEligibility(presence, ClaimCoalReason.NoCredit);
            }
            else if (presence.PayCoal)
                return new ClaimEligibility(presence, ClaimCoalReason.NoPresence);

            if (inputs.CoalWcid == 0)
                return new ClaimEligibility(presence, ClaimCoalReason.None);

            var thresholds = (inputs.Policy ?? WorldEventClaimPolicy.Default).Thresholds;

            if (thresholds.PresenceOn && inputs.PresentSecondsKnown && !double.IsNaN(inputs.PresentSeconds) && inputs.PresentSeconds < thresholds.MinPresenceSeconds)
                return new ClaimEligibility(presence, ClaimCoalReason.BelowPresenceThreshold);

            if (thresholds.ContributionOn && inputs.DamageShareKnown && !double.IsNaN(inputs.DamageShare) && inputs.DamageShare < thresholds.MinContributionShare)
                return new ClaimEligibility(presence, ClaimCoalReason.BelowContributionThreshold);

            return new ClaimEligibility(presence, ClaimCoalReason.None);
        }

        /// <summary>
        /// Stage 2, payout: which wcid the primary give hands over. The outcome crate is the success crate when
        /// <see cref="WorldEvent.PaysSuccessCrate"/> says so and the consolation crate otherwise, exactly as
        /// before; coal replaces it when stage 1 says coal. A failed outcome under
        /// <see cref="FailureRewardMode.None"/> is refused with <see cref="ClaimDenial.NoRewardForOutcome"/>.
        ///
        /// The presence multiplier is not decided here: it depends on whether a coal create fell back to the
        /// crate, which only the handler knows, so the handler still calls <see cref="ResolveStamp"/> with
        /// <see cref="ClaimPayout.Presence"/>.
        ///
        /// A 0 <see cref="ClaimPayout.CrateWcid"/> is a misconfigured axis; the handler refuses it before the lock.
        /// </summary>
        public static ClaimPayout ResolvePayout(ClaimEligibility eligibility, Defs.RewardDef reward, WorldEventOutcome outcome, WorldEventClaimPolicy policy)
        {
            policy ??= WorldEventClaimPolicy.Default;

            var paysSuccess = WorldEvent.PaysSuccessCrate(outcome);

            if (!paysSuccess && policy.OnFailure == FailureRewardMode.None)
                return new ClaimPayout(ClaimDenial.NoRewardForOutcome, 0, 0, false, false, eligibility.Presence, ClaimCoalReason.None, null);

            var crateWcid = reward == null ? 0u : (paysSuccess ? reward.SuccessCrateWcid : reward.ConsolationCrateWcid);
            var payCoal = eligibility.PayCoal;
            var payoutWcid = payCoal ? reward?.CoalWcid ?? 0u : crateWcid;

            string tier = null;

            if (eligibility.CoalReason == ClaimCoalReason.BelowPresenceThreshold)
                tier = "below-presence";
            else if (eligibility.CoalReason == ClaimCoalReason.BelowContributionThreshold)
                tier = "below-contribution";
            else if (!paysSuccess && policy.OnFailure == FailureRewardMode.Partial)
                tier = "partial";

            return new ClaimPayout(ClaimDenial.None, crateWcid, payoutWcid, payCoal, paysSuccess, eligibility.Presence, eligibility.CoalReason, tier);
        }

        /// <summary>
        /// Stage 3, extras: the bonus crates given after the primary, inside the claim lock. Empty for a denied
        /// payout, for any coal claim (decided on what stage 1 chose, so a coal create that fell back to the
        /// crate still gets none), for an axis with no <see cref="Defs.RewardDef.BonusCrateWcids"/>, and for a
        /// <see cref="Defs.RewardDef.BonusFinalStageOnly"/> axis on any stage but a chain's final one. Zero
        /// entries are skipped.
        /// </summary>
        public static ClaimExtras ResolveExtras(ClaimPayout payout, Defs.RewardDef reward, bool isFinalChainStage)
        {
            if (payout.Denial != ClaimDenial.None || payout.PayCoal || reward?.BonusCrateWcids == null || reward.BonusCrateWcids.Count == 0)
                return ClaimExtras.Empty;

            if (reward.BonusFinalStageOnly && !isFinalChainStage)
                return ClaimExtras.Empty;

            var wcids = new List<uint>(reward.BonusCrateWcids.Count);

            foreach (var wcid in reward.BonusCrateWcids)
            {
                if (wcid != 0)
                    wcids.Add(wcid);
            }

            return wcids.Count == 0 ? ClaimExtras.Empty : new ClaimExtras(wcids);
        }

        /// <summary>
        /// The free pack slots a claim needs before anything is given: 1 for the primary plus one per extra.
        /// The handler only prechecks when there ARE extras, so a claim with none fails on a full pack exactly as
        /// before (TryCreateForGive's own line, then claim-give-failed).
        /// </summary>
        public static int RequiredFreeSlots(ClaimExtras extras) => 1 + (extras?.Count ?? 0);

        /// <summary>
        /// The pipeline's suffix for the granted claim line, appended after the presence suffix: " tier=&lt;t&gt;"
        /// only when the payout carries a tier, " extras=&lt;given&gt;/&lt;total&gt;" only when there were extras.
        /// Empty for every claim that has neither, so the line is byte-identical to the pre-pipeline one.
        /// The line reports the FINAL payout: a threshold tier (below-presence, below-contribution) names a coal, so it
        /// is written only when <paramref name="coalPaid"/> - a threshold coal whose create fell back to the crate
        /// logs payout=crate with no threshold tier (the fallback has its own error line).
        /// </summary>
        public static string PipelineLogSuffix(ClaimPayout payout, bool coalPaid, int extrasGiven, int extrasTotal)
        {
            var suffix = "";

            var thresholdTier = payout.CoalReason == ClaimCoalReason.BelowPresenceThreshold || payout.CoalReason == ClaimCoalReason.BelowContributionThreshold;

            if (!string.IsNullOrEmpty(payout.Tier) && (coalPaid || !thresholdTier))
                suffix += $" tier={payout.Tier}";

            if (extrasTotal > 0)
                suffix += $" extras={extrasGiven}/{extrasTotal}";

            return suffix;
        }

        /// <summary>The player-facing line for a threshold coal, or null when the coal (if any) is not a threshold coal.</summary>
        public static string ThresholdMessage(ClaimCoalReason reason)
        {
            switch (reason)
            {
                case ClaimCoalReason.BelowPresenceThreshold:
                    return "You were not inside the event long enough, so you receive a Non Participation Award.";

                case ClaimCoalReason.BelowContributionThreshold:
                    return "You did too little of the fighting here, so you receive a Non Participation Award.";

                default:
                    return null;
            }
        }

        /// <summary>
        /// The claimant's share of the run's total damage, for <see cref="ClaimInputs.DamageShare"/>. False (share 0)
        /// when there is no record set or the total is not positive: an undefined share is unknown, never 0.
        ///
        /// Deliberately NOT fail-open the other way: once the run's total is positive, a claimant with NO ledger
        /// record at all, and one whose record is contact-only (a healer or tank who dealt no damage), has a KNOWN
        /// share of 0, so a contribution threshold pays them coal. That is the threshold doing its job, not an
        /// unknown input - only "nobody dealt any damage" is unknown.
        /// </summary>
        public static bool TryComputeDamageShare(IEnumerable<ParticipantRecord> records, uint characterGuid, out double share)
        {
            share = 0;

            if (records == null)
                return false;

            double total = 0;
            double mine = 0;

            foreach (var rec in records)
            {
                if (rec == null || rec.Damage <= 0)
                    continue;

                total += rec.Damage;

                if (rec.CharacterGuid == characterGuid)
                    mine += rec.Damage;
            }

            if (total <= 0)
                return false;

            share = mine / total;
            return true;
        }

        /// <summary>A presence fraction as a whole percentage, rounded half away from zero.</summary>
        public static int PresencePercent(double fraction)
        {
            if (double.IsNaN(fraction))
                return 0;

            return (int)Math.Round(Math.Max(0.0, Math.Min(1.0, fraction)) * 100.0, MidpointRounding.AwayFromZero);
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
