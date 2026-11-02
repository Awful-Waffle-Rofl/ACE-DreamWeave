using System;
using System.Collections.Generic;
using System.Reflection;

using ACE.Common;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The Weave Cache use path (TECH-DESIGN 2.7, 2.8 option C - repo owner decision C1, 2026-08-15).
    ///
    /// There is no WorldEventCache WorldObject subclass: WorldObjectFactory dispatches on WeenieType alone
    /// (drift finding D3), so the cache is a plain Generic weenie carrying PropertyBool.WorldEventCache, and
    /// GenericObject.ActOnUse hands it here - the same marker-property dispatch the fork already ships for
    /// the CAP exchange stone (PropertyBool.ClassAbilityXpExchanger -> ClassAbilityTrainer.TryHandleUse).
    ///
    /// Because the dispatch happens inside ActOnUse rather than in CheckUseRequirements, this method is the
    /// only veto point. Nothing is consumed on a refusal, so a player may retry for as long as the claim
    /// window is open.
    ///
    /// THREADING. This runs on the landblock thread that owns the cache, while WorldEvent state is written
    /// on the world thread. Reads of State and ClaimWindowEndsAt are benign (a stale read at worst refuses a
    /// claim a fraction of a second early or late).
    ///
    /// "The landblock thread" is not one thread, and the distinction matters for the participation read
    /// below. The use callback reaches here through Player.CreateMoveToChain, and which queue it lands in
    /// depends on Player.FastTick (PK/PKLite players always; otherwise the player's /fasttick choice, or fast_tick_all_players when they have not chosen):
    /// without FastTick it goes on the PLAYER's own action queue (Player_Tick.cs:1004 -> :66), drained from
    /// Landblock.TickSingleThreadedWork, which LandblockManager runs as a plain foreach on the WORLD thread
    /// (LandblockManager.cs:557-577); with FastTick the callback fires from UpdateObjectPhysics
    /// (Player_Tick.cs:340, Player_Move.cs:210) inside Landblock.TickPhysics, which LandblockManager runs
    /// under Parallel.ForEach over landblock GROUPS (LandblockManager.cs:371). So this method must be
    /// treated as running on a landblock-group worker, NOT on the single world thread.
    ///
    /// That is why the participation read below (evt.Participation.TryGetRecord) does not inherit the
    /// "WorldEventManager.Tick never overlaps landblock work" argument that covers every other WorldEvent
    /// reader. Since 2026-10-04 the ledger has a second, hit-time writer and takes its own lock, so the read
    /// is memory-safe on any thread. Its ANSWER is also stable for the whole claim window: the claim gate
    /// above admits only Rewarding, the hit-time writer credits only while Active, and entering Rewarding
    /// cuts every held creature's P_WorldEvent back-reference synchronously, so no death can credit either.
    /// The full argument is in WorldEventParticipation's class comment.
    ///
    /// The claim itself is check-then-act, so lock (evt.ClaimedCharacters) - one lock object for all three
    /// claim sets - SPANS THE WHOLE OF IT: the rule evaluation, the account sibling test, the crate creation,
    /// the TryCreateForGive and the marking all sit inside ONE critical section. That is deliberate and must
    /// stay that way. Evaluating under the lock and then marking under a second acquisition would let two
    /// simultaneous uses by the same character both pass the check and both be paid. Today that is hard to
    /// reach because a run's caches all land on one landblock and therefore one action queue, but that is an
    /// emergent property of the current placement, not a guarantee - a wider ring, or WP-03's geometry, would
    /// silently break it. TryCreateForGive is synchronous and never re-enters this lock.
    ///
    /// Everything that does NOT touch event state stays outside the lock: the PlayerManager account
    /// snapshot, the PropertyManager reads, the player's lifetime counter, the sound and every log line.
    ///
    /// The claim set types themselves are not changed here: WorldEvent.cs belongs to WP-03 this wave.
    /// </summary>
    public static class WorldEventCacheHandler
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Handles a player using a Weave Cache. Returns false only when there is nothing to handle at all
        /// (a null cache or activator), which lets GenericObject.ActOnUse run its normal tail; every other
        /// path - including every refusal - returns true, because a WorldEventCache-flagged object has no
        /// meaningful ordinary use behaviour to fall through to.
        ///
        /// Never throws: a throw here would surface out of the player's use action on the landblock thread.
        /// </summary>
        public static bool TryHandleUse(WorldObject cache, Player player)
        {
            if (cache == null || player == null)
                return false;

            try
            {
                return Handle(cache, player);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] claim threw for {player.Name} (0x{player.Guid.Full:X8}) on cache 0x{cache.Guid.Full:X8}", ex);

                TrySendTransientError(player, "The cache holds nothing you can take.");

                return true;
            }
        }

        /// <summary>
        /// The claim policy the pipeline runs under for <paramref name="evt"/>: its objective's
        /// <see cref="Objectives.IWorldEventObjective.ClaimPolicy"/>, or <see cref="WorldEventClaimPolicy.Default"/>
        /// when there is no run, no objective, the objective answers null, or the read throws.
        /// </summary>
        internal static WorldEventClaimPolicy ClaimPolicyFor(WorldEvent evt)
        {
            try
            {
                return evt?.Objective?.ClaimPolicy ?? WorldEventClaimPolicy.Default;
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] run={evt?.RunId} objective ClaimPolicy threw; claiming under the default policy", ex);
                return WorldEventClaimPolicy.Default;
            }
        }

        private static bool Handle(WorldObject cache, Player player)
        {
            var runId = cache.GetProperty(PropertyInt.WorldEventId);
            var evt = runId == null ? null : WorldEventManager.FindRun((uint)runId.Value);

            // A cache left over from a finished run must never pay. The run id stamp is applied at spawn
            // time by WorldEventRewardDelivery, so an unstamped cache (hand-spawned by an admin, say) also
            // lands here.
            if (evt == null)
            {
                TrySendTransientError(player, "This cache is spent.");
                return true;
            }

            var reward = evt.Composition?.Reward;

            if (reward == null)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} claim refused: the composition carries no reward axis");
                TrySendTransientError(player, "The cache holds nothing you can take.");
                return true;
            }

            var characterGuid = player.Guid.Full;
            var accountId = player.Session?.AccountId ?? 0;
            var ip = player.Session?.EndPointC2S?.Address?.ToString();
            var householdKey = IpLimitManager.HouseholdKey(player.Session);

            // 1. The window. Rewarding is the only state a claim is legal in, and ClaimWindowEndsAt is set
            //    the moment the run enters it. A 0 end time means "not yet armed", which the state check has
            //    already covered.
            var now = Time.GetUnixTime();

            if (evt.State != WorldEventState.Rewarding || (evt.ClaimWindowEndsAt > 0 && now >= evt.ClaimWindowEndsAt))
                return Deny(evt, cache, player, ClaimDenial.NotRewarding, characterGuid, accountId, ip);

            // 2. Gates. A reward axis can turn any rule off for its own content; the two server properties
            //    are the operator's global override on top of that (C4 - both default ON).
            var gateByCharacter = reward.GateByCharacter;
            var gateByAccount = reward.GateByAccount && PropertyManager.GetBool("world_events_account_gate_enabled").Item;
            var gateByIp = reward.GateByIp && PropertyManager.GetBool("world_events_ip_gate_enabled").Item;

            var exemptIps = WorldEventClaimRules.ParseExemptIps(PropertyManager.GetString("world_events_ip_exempt").Item);

            // 3. The claim pipeline's pure stages (WorldEventClaimRules: ResolveEligibility -> ResolvePayout ->
            //    ResolveExtras), all resolved BEFORE the lock from plain values. Under
            //    WorldEventClaimPolicy.Default with no bonus crates every decision below is the pre-pipeline one.
            //    The policy is the run's objective's (IWorldEventObjective.ClaimPolicy), which is that default for
            //    every objective that does not supply its own, so the thresholds are off for those.
            var policy = ClaimPolicyFor(evt);
            var paysSuccess = WorldEvent.PaysSuccessCrate(evt.Outcome);

            // Participation payout (repo owner decision, 2026-09-07; widened 2026-10-04): a claimant who
            // neither dealt damage to nor took damage from a run creature during the fight - so has no
            // ledger record at all - gets the reward axis's coal booby prize instead of the outcome crate,
            // never a different set of claimants (2.7's claim gate above is unchanged). Neither read touches
            // event state, so both stay OUTSIDE the lock, next to the other PropertyManager reads and the
            // account sibling snapshot. A null ledger (should not happen - WorldEvent always constructs one -
            // but guarded anyway) reads as "credit unknown", which resolves to hasCredit true so the fallback
            // is fail-open toward the ordinary crate, never toward coal.
            //
            // Presence payout (repo owner rulings 2026-10-19): while world_events_presence_payout_enabled is on,
            // presence inside the arena REPLACES the credit rule - a known presence of 0 pays coal, anything else
            // pays the outcome crate, stamped below with its multiplier. Switched off, ResolvePresencePayout says
            // UsesCreditRule and the credit rule runs exactly as it did before. The ledger read is lock-protected
            // inside WorldEventPresence and the window is closed by the time the run reaches Rewarding, so the
            // answer is stable for the whole claim window.
            //
            // The threshold inputs (present seconds, damage share) are read only while their threshold is on.
            var presencePayoutEnabled = PropertyManager.GetBool("world_events_presence_payout_enabled", true).Item;
            var presenceFraction = 0.0;
            var presenceKnown = presencePayoutEnabled && evt.Presence != null && evt.Presence.TryGetFraction(characterGuid, out presenceFraction);

            var creditEnabled = false;
            var hasCredit = true;

            if (!presencePayoutEnabled)
            {
                creditEnabled = PropertyManager.GetBool("world_events_participation_payout_enabled").Item;
                hasCredit = WorldEventParticipation.HasCreditOrUnknown(evt.Participation, characterGuid);
            }

            var presentSeconds = 0.0;
            var presentSecondsKnown = policy.Thresholds.PresenceOn && evt.Presence != null && evt.Presence.TryGetPresentSeconds(characterGuid, out presentSeconds);

            var damageShare = 0.0;
            var damageShareKnown = policy.Thresholds.ContributionOn && evt.Participation != null
                && WorldEventClaimRules.TryComputeDamageShare(evt.Participation.Records, characterGuid, out damageShare);

            var eligibility = WorldEventClaimRules.ResolveEligibility(new ClaimInputs
            {
                PresenceEnabled = presencePayoutEnabled,
                PresenceKnown = presenceKnown,
                PresenceFraction = presenceFraction,
                PresentSecondsKnown = presentSecondsKnown,
                PresentSeconds = presentSeconds,
                CreditEnabled = creditEnabled,
                HasCredit = hasCredit,
                DamageShareKnown = damageShareKnown,
                DamageShare = damageShare,
                PaysSuccess = paysSuccess,
                CoalWcid = reward.CoalWcid,
                Policy = policy
            });

            var payout = WorldEventClaimRules.ResolvePayout(eligibility, reward, evt.Outcome, policy);

            // A policy that pays nothing for this outcome refuses the claim through the fixed denial line.
            // Unreachable under the default policy.
            if (payout.Denial != ClaimDenial.None)
                return Deny(evt, cache, player, payout.Denial, characterGuid, accountId, ip);

            // The crate this outcome pays, checked BEFORE the lock so a misconfigured reward axis is refused
            // without ever entering the critical section. A Failed* outcome still opens a claim window and pays
            // the consolation crate; Aborted* outcomes never reach Rewarding at all.
            var crateWcid = payout.CrateWcid;

            if (crateWcid == 0)
            {
                log.Error($"[WORLDEVENT] run={evt.RunId} claim refused: reward axis {reward.Id} has no crate wcid for outcome {evt.Outcome}");
                TrySendTransientError(player, "The cache holds nothing you can take.");
                return true;
            }

            var payCoal = payout.PayCoal;

            // Bonus crates (owner ruling 2026-11-01). A run outside a chain is its own final stage. Empty for every
            // reward profile that ships today.
            // TODO(chain slice H8): pass the run's real final-stage flag once chains exist.
            var extras = WorldEventClaimRules.ResolveExtras(payout, reward, isFinalChainStage: true);

            // The account sibling snapshot reads PlayerManager, not event state, so it is taken OUTSIDE the
            // claim lock: holding the claim lock across PlayerManager's own read lock would nest two
            // unrelated locks for no benefit. Only the membership test against ClaimedCharacters is inside.
            var siblingGuids = gateByAccount && accountId != 0
                ? SnapshotAccountCharacters(accountId, characterGuid)
                : null;

            var denial = ClaimDenial.None;
            var createFailed = false;
            var giveFailed = false;
            var roomShort = false;
            var freeSlots = 0;
            var extrasGiven = 0;
            var attemptedWcid = crateWcid;

            // What the presence rule stamped on the created crate (WorldEventClaimRules.ResolveStamp), decided
            // inside the lock once the coal-to-crate fallback is known, and read again by the claim line.
            (bool Stamp, double Multiplier) stamp = (false, 0);

            // ONE critical section: evaluate, sibling-check, room precheck, create, give the primary, give the
            // extras, mark. See the class comment for why the check and the mark must not be separate acquisitions.
            lock (evt.ClaimedCharacters)
            {
                // Rules 1 and 2 (character, then the account SET). The IP gate is suppressed on this call so
                // the sibling rule can slot in ahead of it and the reported reason still follows the fixed
                // character -> account -> IP order of 5.6.
                denial = WorldEventClaimRules.Evaluate(characterGuid, accountId, ip, householdKey,
                    evt.ClaimedCharacters, evt.ClaimedAccounts, evt.ClaimedIps, exemptIps,
                    gateByCharacter, gateByAccount, false);

                // Rule 2b, belt and braces for the account rule (2.7): the claimed-accounts set only knows
                // about sessions this run has already seen, so a character who claimed while its account id
                // was unknown (accountId 0) would not be covered by it. Every OTHER character currently
                // known to belong to this account is tested against the character set instead. It runs
                // BEFORE the IP rule so an account hit is never reported as an IP hit.
                if (denial == ClaimDenial.None && siblingGuids != null && AnySiblingClaimed(evt, siblingGuids))
                    denial = ClaimDenial.AlreadyClaimedAccount;

                // Rule 3 (IP), last.
                if (denial == ClaimDenial.None)
                    denial = WorldEventClaimRules.Evaluate(characterGuid, accountId, ip, householdKey,
                        evt.ClaimedCharacters, evt.ClaimedAccounts, evt.ClaimedIps, exemptIps,
                        false, false, gateByIp);

                // Room for the primary AND every extra, before anything is created (owner ruling 2026-11-01): no
                // room means nothing given and nothing marked, so the claim stays retryable. Only checked when
                // there ARE extras - a claim with none fails on a full pack exactly as it always has, through
                // TryCreateForGive's own line and claim-give-failed below.
                if (denial == ClaimDenial.None && extras.Count > 0)
                {
                    freeSlots = player.GetFreeInventorySlots();
                    roomShort = freeSlots < WorldEventClaimRules.RequiredFreeSlots(extras);
                }

                if (denial == ClaimDenial.None && !roomShort)
                {
                    // The payout wcid: coal when the rule above says so, otherwise the ordinary outcome
                    // crate. A content mistake in coalWcid must never deny a claim, so a failed coal create
                    // falls back to the ordinary crate right here rather than surfacing createFailed - that
                    // is the ONLY special case; giveFailed below still applies to whichever object this
                    // resolves to.
                    var payoutWcid = payout.PayoutWcid;
                    attemptedWcid = payoutWcid;
                    var crate = WorldObjectFactory.CreateNewWorldObject(payoutWcid);

                    if (crate == null && payCoal)
                    {
                        log.Error($"[WORLDEVENT] run={evt.RunId} coal wcid {payoutWcid} failed to create, falling back to crate wcid {crateWcid}");

                        payCoal = false;
                        payoutWcid = crateWcid;
                        attemptedWcid = payoutWcid;
                        crate = WorldObjectFactory.CreateNewWorldObject(payoutWcid);
                    }

                    // Presence payout: the multiplier and the reward profile go on the crate BEFORE the give, so the
                    // object a player receives already carries what its roll needs. Only a success crate is
                    // stamped (the consolation crate is not scaled); a crate that stood in for a failed coal
                    // create carries m 1.0 (unknown), never the coal's 0 - and never the presence multiplier either,
                    // whichever rule decided the coal (payout.PayCoal is the decision, payCoal what was paid).
                    stamp = WorldEventClaimRules.ResolveStamp(payout.Presence, payout.PayCoal, payCoal, paysSuccess);

                    if (crate != null && stamp.Stamp)
                    {
                        crate.SetProperty(PropertyFloat.WorldEventRewardMultiplier, stamp.Multiplier);
                        crate.SetProperty(PropertyString.WorldEventRewardProfile, reward.Id);
                    }

                    if (crate == null)
                        createFailed = true;
                    else if (!player.TryCreateForGive(cache, crate))
                    {
                        // Deliberately NOT marked claimed: the claim stays retryable for as long as the
                        // window is open, which is what makes "make room and click it again" work.
                        // TryCreateForGive has already sent the "tries to give you" line, so no extra
                        // message belongs here.
                        crate.Destroy();
                        giveFailed = true;
                    }
                    else
                    {
                        // Marked strictly AFTER a successful give (2.7 idempotency rule), inside the same
                        // critical section the check ran in - and BEFORE the extras, so nothing an extra does
                        // (a throw included) can leave a paid primary unmarked and let a retry pay it twice.
                        evt.ClaimedCharacters.Add(characterGuid);

                        if (accountId != 0)
                            evt.ClaimedAccounts.Add(accountId);

                        var ipKey = WorldEventClaimRules.NormalizeIp(householdKey);

                        if (ipKey.Length > 0)
                            evt.ClaimedIps.Add(ipKey);

                        // The extras, still inside the lock, after the primary landed and the claim is marked. The
                        // room precheck above makes a failure here rare (a unique item, a template gate); one that
                        // fails or throws is logged and dropped, and the claim stays marked.
                        extrasGiven = GiveExtras(evt, cache, player, extras);
                    }
                }
            }

            if (denial != ClaimDenial.None)
            {
                // Pooled loot: a character who has already claimed and clicks again retries a share whose chest never
                // reached the world. Outside the claim lock, like the first attempt below. No other denial reaches
                // a share: an account or IP sibling's claim is gate-denied and gets nothing.
                if (denial == ClaimDenial.AlreadyClaimedCharacter)
                    WorldEventShareDelivery.TryDeliver(evt, cache, player);

                return Deny(evt, cache, player, denial, characterGuid, accountId, ip);
            }

            if (roomShort)
            {
                // Like a full pack: neither granted nor denied, still retryable, so not the 5.2 claim line.
                var needed = WorldEventClaimRules.RequiredFreeSlots(extras);

                log.Info($"[WORLDEVENT] run={evt.RunId} claim-room-short char={player.Name} guid=0x{characterGuid:X8} need={needed} free={freeSlots} - not marked claimed, still retryable");
                TrySendTransientError(player, $"You need {needed} free pack slots to take this reward. Make room and use the {cache.Name} again.");

                return true;
            }

            if (createFailed)
            {
                // attemptedWcid, not crateWcid. The two are equal on every path that can reach here today
                // (the last create attempt is always the crate - either payCoal was false from the start,
                // or the coal attempt already fell back to it), but that is an implicit invariant no test
                // can hold, and a second coal-only fallback would silently desync the logged wcid from the
                // one that actually failed.
                log.Error($"[WORLDEVENT] run={evt.RunId} claim refused: failed to create crate wcid {attemptedWcid}");
                TrySendTransientError(player, "The cache holds nothing you can take.");
                return true;
            }

            if (giveFailed)
            {
                // Deliberately NOT the 5.2 claim line: that line's result field is granted or
                // denied:<ClaimDenial name>, and a full pack is neither - no rule fired and the claim is
                // still available. A distinct line keeps the fixed format parseable.
                log.Info($"[WORLDEVENT] run={evt.RunId} claim-give-failed char={player.Name} guid=0x{characterGuid:X8} crate={attemptedWcid} - not marked claimed, still retryable");

                return true;
            }

            // A coal payout completed nothing - the claimant fought none of the run, so the lifetime
            // counter (WorldEventsCompleted) is skipped for them. WorldObject has no IncProperty for
            // PropertyInt (only PropertyFloat), so this is the read-modify-write idiom the rest of the
            // server uses.
            if (!payCoal)
            {
                var completed = player.GetProperty(PropertyInt.WorldEventsCompleted) ?? 0;
                player.SetProperty(PropertyInt.WorldEventsCompleted, completed + 1);
            }

            if (cache.UseSound > 0)
                player.Session?.Network?.EnqueueSend(new GameMessageSound(player.Guid, cache.UseSound));

            // A threshold coal (a policy threshold, never the default) gets its own line; every other claim gets
            // exactly the line it got before the pipeline.
            var thresholdMessage = payCoal ? WorldEventClaimRules.ThresholdMessage(payout.CoalReason) : null;

            if (thresholdMessage != null)
                TrySendTransientError(player, thresholdMessage);
            else if (payout.Presence.UsesCreditRule)
            {
                if (payCoal)
                    TrySendTransientError(player, "You did not fight here. The cache offers you what you earned.");
            }
            else
            {
                // Presence payout: the crate's percentage and multiplier, or the Non Participation line for coal.
                // No line is sent when a coal-DECIDED claim (presence or threshold) fell back to the crate: that crate
                // carries m 1.0, so neither the coal line nor the "carries a Nx reward" line is true of it.
                var presenceMessage = payout.PayCoal && !payCoal ? null : WorldEventClaimRules.PresenceMessage(payout.Presence);

                if (presenceMessage != null)
                    TrySendTransientError(player, presenceMessage);
            }

            // payout= is a SEPARATE field appended after result=granted - never fold it into that token, or
            // "result=granted:coal" would break the monitoring queries that parse result= as exactly
            // granted or denied:<ClaimDenial name> (see ClaimDenial's doc comment). The presence suffix
            // (presence= mult=) follows payout= under the presence rule and is empty under the credit rule, so
            // the line is unchanged byte for byte with the switch off. mult= is the multiplier actually stamped,
            // or "none" when nothing was (coal, or an unscaled consolation crate). The pipeline suffix (tier=,
            // extras=) comes last and is empty for every default claim, so the line is unchanged byte for byte
            // under WorldEventClaimPolicy.Default with no bonus crates.
            var presenceLogSuffix = WorldEventClaimRules.PresenceLogSuffix(payout.Presence, stamp);
            var pipelineLogSuffix = WorldEventClaimRules.PipelineLogSuffix(payout, payCoal, extrasGiven, extras.Count);

            log.Info($"[WORLDEVENT] run={evt.RunId} claim char={player.Name} guid=0x{characterGuid:X8} account={accountId} ip={FormatIp(ip)} result=granted payout={(payCoal ? "coal" : "crate")}{presenceLogSuffix}{pipelineLogSuffix}");

            // Pooled loot (world_events_pooled_loot_enabled): the claimant's share, built into their own chest. AFTER the
            // crate is given and OUTSIDE the claim lock - the fill is a multi-tick chain and must never hold the lock
            // other claimants wait on. TryDeliver never throws and is a no-op with no pool, no seat or an empty share,
            // so the crate claim above is unaffected whatever happens here. It is the pipeline's fourth stage and the
            // ONLY delivery outside the lock.
            //
            // KNOWN LIMITATION under the presence rule (world_events_presence_payout_enabled): the seats are still
            // the participation ledger's credit records (WorldEvent.DealLootPool), while the crate-or-coal answer
            // above comes from presence. So a claimant with combat credit but zero presence receives coal AND a
            // share, and one who was present but never fought or was hit receives a scaled crate and NO share. Left
            // unchanged deliberately (the owner has pooled loot switched off); revisit before enabling both.
            WorldEventShareDelivery.TryDeliver(evt, cache, player);

            return true;
        }

        /// <summary>
        /// Gives each bonus crate after the primary. Must be called under lock (evt.ClaimedCharacters); like the
        /// primary give, TryCreateForGive is synchronous and never re-enters it. Returns how many landed. A crate
        /// that fails to create or to give, or throws, is logged and dropped (destroyed when it was created), and
        /// the loop goes on to the next one. The claim is already marked by the time this runs.
        /// </summary>
        private static int GiveExtras(WorldEvent evt, WorldObject cache, Player player, ClaimExtras extras)
        {
            var given = 0;

            foreach (var wcid in extras.Wcids)
            {
                WorldObject extra = null;

                try
                {
                    extra = WorldObjectFactory.CreateNewWorldObject(wcid);

                    if (extra == null)
                    {
                        log.Error($"[WORLDEVENT] run={evt.RunId} bonus crate wcid {wcid} failed to create for {player.Name}");
                        continue;
                    }

                    if (!player.TryCreateForGive(cache, extra))
                    {
                        var refused = extra;
                        extra = null;
                        refused.Destroy();
                        log.Warn($"[WORLDEVENT] run={evt.RunId} bonus crate wcid {wcid} could not be given to {player.Name} (0x{player.Guid.Full:X8})");
                        continue;
                    }

                    given++;
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] run={evt.RunId} bonus crate wcid {wcid} threw for {player.Name} (0x{player.Guid.Full:X8})", ex);

                    if (extra != null)
                        TryDestroy(extra);
                }
            }

            return given;
        }

        /// <summary>Destroys a dropped bonus crate without letting a second throw escape the claim lock.</summary>
        private static void TryDestroy(WorldObject wo)
        {
            try
            {
                wo.Destroy();
            }
            catch (Exception ex)
            {
                log.Debug($"[WORLDEVENT] could not destroy dropped bonus crate 0x{wo.Guid.Full:X8}", ex);
            }
        }

        /// <summary>
        /// Guids of every character on the account EXCEPT the claiming one. Reads PlayerManager only, so it
        /// must be called OUTSIDE lock (evt.ClaimedCharacters).
        /// </summary>
        private static List<uint> SnapshotAccountCharacters(uint accountId, uint characterGuid)
        {
            var guids = new List<uint>();

            var siblings = PlayerManager.GetAccountPlayersSnapshot(accountId);

            if (siblings == null)
                return guids;

            foreach (var sibling in siblings)
            {
                if (sibling == null)
                    continue;

                var guid = sibling.Guid.Full;

                if (guid != characterGuid)
                    guids.Add(guid);
            }

            return guids;
        }

        /// <summary>
        /// True when one of the account's other characters is already in the run's claimed-character set.
        /// Must be called under lock (evt.ClaimedCharacters).
        /// </summary>
        private static bool AnySiblingClaimed(WorldEvent evt, List<uint> siblingGuids)
        {
            foreach (var guid in siblingGuids)
            {
                if (evt.ClaimedCharacters.Contains(guid))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Sends the refusal, writes the 5.2 claim line with result=denied:&lt;rule&gt;, and consumes nothing.
        /// </summary>
        private static bool Deny(WorldEvent evt, WorldObject cache, Player player, ClaimDenial denial,
            uint characterGuid, uint accountId, string ip)
        {
            TrySendTransientError(player, WorldEventClaimRules.DenialMessage(denial, cache?.Name));

            log.Info($"[WORLDEVENT] run={evt.RunId} claim char={player.Name} guid=0x{characterGuid:X8} account={accountId} ip={FormatIp(ip)} result=denied:{denial}");

            return true;
        }

        /// <summary>Keeps the log line's ip= field non-empty so a fixed-format parser never sees a gap.</summary>
        private static string FormatIp(string ip)
        {
            return string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();
        }

        /// <summary>
        /// SendTransientError dereferences Session unconditionally, so a player whose session has already
        /// gone away would throw out of the use action. The claim itself is unaffected either way.
        /// </summary>
        private static void TrySendTransientError(Player player, string message)
        {
            if (player?.Session?.Network == null || string.IsNullOrEmpty(message))
                return;

            try
            {
                player.SendTransientError(message);
            }
            catch (Exception ex)
            {
                log.Debug($"[WORLDEVENT] could not send claim message to {player.Name}", ex);
            }
        }
    }
}
