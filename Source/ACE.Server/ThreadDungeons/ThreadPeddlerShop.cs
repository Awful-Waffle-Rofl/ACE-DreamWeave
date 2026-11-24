using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>What a delivery seam does after <see cref="ThreadPeddlerShop.Gate"/>.</summary>
    public enum PeddlerHold
    {
        /// <summary>No shop holds this member: deliver as usual.</summary>
        Proceed,

        /// <summary>This member's shop is open. Touch nothing and re-arm no retry: the release requests placement.</summary>
        Held,

        /// <summary>The clear-time pile is not complete yet. Touch nothing; the delivery chain's own retry brings it back.</summary>
        WaitPile,

        /// <summary>The member would be presented but is not inside: wait for their arrival, exactly as an undelivered pile does.</summary>
        WaitArrival,
    }

    /// <summary>What <see cref="ThreadPeddlerShop.Present"/> did for one member.</summary>
    internal enum PeddlerPresentResult
    {
        /// <summary>The member's shop is open and they were told: hold their delivery.</summary>
        Held,

        /// <summary>No shop for them (nothing affordable, or the peddler cannot appear): they are Done, deliver as usual.</summary>
        Skipped,

        /// <summary>The member could not be found: decide nothing, wait for their arrival.</summary>
        NotNow,
    }

    /// <summary>
    /// The clear-only Wandering Peddler's flow (owner rulings 2026-11-19; Docs/Threads/ENCOUNTERS.md "Wandering Peddler"),
    /// shared by the two delivery seams so solo and group hold, present and release exactly alike.
    ///
    ///   Gate     - both seams, AFTER the clear pile is complete and BEFORE the pile is first delivered.
    ///   Present  - the first time a seated member reaches the gate inside the run: creates the run's one NPC beside them
    ///              when it does not exist yet (only for a member who can afford something), opens their shop, arms them on
    ///              the NPC and tells them. A member who can afford nothing is skipped (Done) and told so.
    ///   Release  - every done source: <see cref="ThreadDungeonRun.TryMarkShopDone"/>, and only its winner requests the
    ///              placement that forms the member's caches. A Busy placement re-flags the member: the run's
    ///              placement-wanted flag for solo, the member's delivery-wanted flag for group.
    ///
    /// Threading. Gate and Present run on the delivery chain (the run landblock's queue), where the Thread Caches are
    /// created too. Release runs on whatever thread the done source is on (the player's queue for a Yes, the world thread
    /// for /spawncache, the peddler's landblock for the poll); it touches only the run's locked state and the placement
    /// latch, which are both safe from any thread.
    /// </summary>
    public static class ThreadPeddlerShop
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Test seam: the placement a release requests. Production: an Auto delivery (ThreadCachePlacer.RequestPlacement).</summary>
        internal static Func<ThreadDungeonRun, uint, CacheRequestOutcome> RequestPlacement = (run, memberGuid) => ThreadCachePlacer.RequestPlacement(run, CacheRequestMode.Auto);

        /// <summary>Test seam: the clock Present and the liveness sweep read, unix seconds.</summary>
        internal static Func<double> Clock = () => ACE.Common.Time.GetUnixTime();

        /// <summary>
        /// The hold gate. <paramref name="inside"/> is read only when the member would be presented;
        /// <paramref name="present"/> is run only for a member inside. Null <paramref name="present"/> skips the member.
        /// </summary>
        internal static PeddlerHold Gate(ThreadDungeonRun run, uint memberGuid, Func<bool> inside, Func<PeddlerPresentResult> present)
        {
            if (run == null)
                return PeddlerHold.Proceed;

            switch (run.PeddlerGateFor(memberGuid))
            {
                case PeddlerGate.Held:
                {
                    // Liveness backstop: a shop is held only while its NPC stands. One destroyed without departing (its
                    // landblock torn down) can never release anyone, so the shop closes here and this member delivers now;
                    // any other open shop is released with its own placement request.
                    var npc = run.PeddlerNpc;

                    if (npc != null && !npc.IsDestroyed)
                        return PeddlerHold.Held;

                    // This member delivers in this same pass; every other open shop gets its own placement request.
                    CloseAndReleaseAll(run, "its NPC is gone", except: memberGuid);
                    return PeddlerHold.Proceed;
                }

                case PeddlerGate.WaitPile:
                    return PeddlerHold.WaitPile;

                case PeddlerGate.Present:
                    if (inside == null || !inside())
                        return PeddlerHold.WaitArrival;

                    if (present == null)
                    {
                        run.MarkPeddlerShopSkipped(memberGuid);
                        return PeddlerHold.Proceed;
                    }

                    switch (present())
                    {
                        case PeddlerPresentResult.Held:
                            return PeddlerHold.Held;

                        case PeddlerPresentResult.NotNow:
                            return PeddlerHold.WaitArrival;

                        default:
                            return PeddlerHold.Proceed;
                    }

                default:
                    return PeddlerHold.Proceed;
            }
        }

        /// <summary>
        /// Presents the peddler to one member (see the class summary). <paramref name="createAt"/> builds and enters the NPC
        /// beside the member, or returns null; <paramref name="tell"/> sends one chat line. Never throws: a throw skips the
        /// member, so a broken peddler can never hold a pile.
        /// </summary>
        internal static PeddlerPresentResult Present(ThreadDungeonRun run, Player member, Func<Player, PeddlerPlan, ThreadPeddlerVendor> createAt, Action<Player, string> tell)
        {
            if (run == null || member == null)
                return PeddlerPresentResult.NotNow;

            var guid = member.Guid.Full;

            try
            {
                var plan = run.PeddlerPlan;

                if (plan == null)
                {
                    run.MarkPeddlerShopSkipped(guid);
                    return PeddlerPresentResult.Skipped;
                }

                // Ruling 5e's cap: a shop opened this late would only run into the TTL. Skipped, and nobody is told.
                if (Clock() >= ThreadPeddlerVendor.ExpiryCapUnix(run))
                {
                    run.MarkPeddlerShopSkipped(guid);
                    run.RecordEncounterOutcome($"peddler: {member.Name} reached the clear too close to the thread's expiry; no shop");
                    return PeddlerPresentResult.Skipped;
                }

                // Ruling 3: a member who can afford nothing is done at once, and is never the one the NPC appears for.
                // Before the open nothing is charged, so the balance IS the basis the open will snapshot: the cheapest offer
                // at this member's own (percent) price, which is the flat price without percent pricing.
                var balance = run.PeddlerBalance(guid);
                var cheapest = plan.MinPriceFor(balance);

                if (balance < cheapest)
                {
                    if (run.MarkPeddlerShopSkipped(guid))
                    {
                        tell?.Invoke(member, ThreadPeddlerVendor.NothingAffordableMessage);
                        run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "peddler: {0} can afford nothing (balance {1}, cheapest {2}); no shop", member.Name, balance, cheapest));
                    }

                    return PeddlerPresentResult.Skipped;
                }

                var npc = run.PeddlerNpc;

                // Destroyed without departing (its landblock torn down): it is gone, and so is the shop.
                if (npc != null && npc.IsDestroyed)
                {
                    CloseAndReleaseAll(run, "its NPC is gone");
                    npc = null;
                }

                if (npc == null)
                {
                    // Gone or never coming: one NPC per run, ever.
                    if (run.PeddlerClosed)
                    {
                        run.MarkPeddlerShopSkipped(guid);
                        return PeddlerPresentResult.Skipped;
                    }

                    npc = createAt?.Invoke(member, plan);

                    if (npc == null)
                    {
                        run.ClosePeddler();
                        run.MarkPeddlerShopSkipped(guid);
                        run.RecordEncounterOutcome($"peddler: could not appear beside {member.Name}; no shop for anyone");
                        log.Warn($"[DYNDUNGEON] {run} peddler could not appear beside {member.Name}; the shop is closed");
                        return PeddlerPresentResult.Skipped;
                    }

                    if (!run.TrySetPeddlerNpc(npc))
                    {
                        // Unreachable while Present runs only on the delivery chain; kept so a second NPC can never stand.
                        log.Error($"[DYNDUNGEON] {run} a second peddler NPC was built; destroyed");
                        npc.MarkRunEnded();
                        try { if (!npc.IsDestroyed) npc.Destroy(); } catch { }

                        npc = run.PeddlerNpc;

                        if (npc == null)
                        {
                            run.MarkPeddlerShopSkipped(guid);
                            return PeddlerPresentResult.Skipped;
                        }
                    }
                    else
                        run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "peddler: appeared beside {0} with {1} offer(s)", member.Name, plan.Offers.Count));
                }

                // The poll starts only AFTER the open-and-arm attempt (StartWatch is idempotent), so its first tick can never
                // find a freshly created NPC with nobody armed yet; and when the open fails, it still starts, finds zero shops
                // and departs (ThreadPeddlerVendor.DepartureReason).
                try
                {
                    if (!run.TryOpenPeddlerShop(guid))
                    {
                        run.MarkPeddlerShopSkipped(guid);
                        return PeddlerPresentResult.Skipped;
                    }

                    // The NPC left between the open and the arm: done at once, and the seam delivers in this same pass.
                    if (!npc.Arm(guid))
                    {
                        run.TryMarkShopDone(guid);
                        return PeddlerPresentResult.Skipped;
                    }
                }
                finally
                {
                    npc.StartWatch();
                }

                tell?.Invoke(member, ThreadPeddlerVendor.PresentedMessage);
                tell?.Invoke(member, ThreadPeddlerVendor.ExclusionMessage);

                // One line for stage tuning (the encounter log and the server log): the basis and this member's price for every offer.
                var basis = run.PeddlerBasis(guid) ?? balance;
                var prices = string.Join(", ", plan.Offers.Select(o => $"{o.EntryId} {plan.PriceFor(o, basis).ToString(CultureInfo.InvariantCulture)}"));
                var openLine = string.Format(CultureInfo.InvariantCulture, "peddler: shop open for {0} (balance {1}, basis {2}, {3} pricing): {4}",
                    member.Name, balance, basis, plan.HasPercentPricing ? "percent" : "flat", prices);
                run.RecordEncounterOutcome(openLine);
                log.Info($"[DYNDUNGEON] {run} {openLine}");
                return PeddlerPresentResult.Held;
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} presenting the peddler to {member.Name} threw; no shop for them", ex);

                if (!run.TryMarkShopDone(guid))
                    run.MarkPeddlerShopSkipped(guid);

                return PeddlerPresentResult.Skipped;
            }
        }

        /// <summary>The production Present for one seam: the NPC forms at the member's cache spot (ThreadCachePlacer.PlacePeddlerAtMember).</summary>
        internal static PeddlerPresentResult PresentLive(ThreadDungeonRun run, ACE.Server.Entity.Landblock landblock, Player member)
            => Present(run, member,
                (m, plan) => ThreadCachePlacer.PlacePeddlerAtMember(run, landblock, m, position => PeddlerEncounterType.CreateNpcAt(run, plan, position, out _)),
                (m, text) => m.Session?.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast)));

        /// <summary>
        /// One member is done (any source). True only for the caller that won <see cref="ThreadDungeonRun.TryMarkShopDone"/>,
        /// which then requests placement (<see cref="RequestAfterRelease"/>). Every later caller changes nothing.
        /// </summary>
        public static bool Release(ThreadDungeonRun run, uint memberGuid, string why)
        {
            if (run == null || !run.TryMarkShopDone(memberGuid))
                return false;

            run.PeddlerNpc?.WithdrawFinishedQuestion(memberGuid);

            var name = run.GetMember(memberGuid)?.Name ?? $"0x{memberGuid:X8}";
            run.RecordEncounterOutcome($"peddler: {name} done ({why})");
            log.Info($"[DYNDUNGEON] {run} peddler: {name} done ({why})");

            RequestAfterRelease(run, memberGuid);
            return true;
        }

        /// <summary>
        /// Closes the run's peddler and releases every shop still open: each member's pending Finished question is
        /// withdrawn and their placement requested, except <paramref name="except"/> (a seam that delivers that member in
        /// the same pass). Returns the members released. Any thread.
        /// </summary>
        internal static List<uint> CloseAndReleaseAll(ThreadDungeonRun run, string why, uint except = 0)
        {
            if (run == null)
                return new List<uint>();

            var npc = run.PeddlerNpc;
            var released = run.ClosePeddler();

            foreach (var guid in released)
            {
                npc?.WithdrawFinishedQuestion(guid);

                if (guid != except)
                    RequestAfterRelease(run, guid);
            }

            if (released.Count > 0)
            {
                run.RecordEncounterOutcome($"peddler: {why}; {released.Count} open shop(s) released");
                log.Warn($"[DYNDUNGEON] {run} peddler: {why}; {released.Count} open shop(s) released");
            }

            return released;
        }

        /// <summary>
        /// The Tick's liveness sweep (ThreadDungeonManager.Tick, Cleared runs): when some shop is open and the NPC is gone,
        /// destroyed, or its poll has stopped (<see cref="ThreadPeddlerVendor.IsAlive"/>), every open shop is released.
        /// True when it released anything.
        /// </summary>
        internal static bool SweepLiveness(ThreadDungeonRun run)
        {
            if (run == null || run.State != ThreadDungeonRunState.Cleared || !run.AnyPeddlerShopOpen)
                return false;

            var npc = run.PeddlerNpc;

            // The NPC's own clock: its poll heartbeat is stamped with it.
            if (npc != null && npc.IsAlive(npc.Clock()))
                return false;

            return CloseAndReleaseAll(run, npc == null ? "no NPC" : "its NPC is gone or its poll stopped").Count > 0;
        }

        /// <summary>
        /// The placement a release owes: an Auto delivery, which forms the member's caches. Anything
        /// but Queued re-flags the member for the existing retry (solo: the run's placement-wanted flag; group: the member's
        /// delivery-wanted flag), so a release is never dropped. Nothing for a run that is no longer Cleared.
        /// </summary>
        internal static void RequestAfterRelease(ThreadDungeonRun run, uint memberGuid)
        {
            if (run == null || run.State != ThreadDungeonRunState.Cleared)
                return;

            CacheRequestOutcome outcome;

            try
            {
                outcome = RequestPlacement(run, memberGuid);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} requesting placement after a peddler release threw", ex);
                outcome = CacheRequestOutcome.Busy;
            }

            if (outcome == CacheRequestOutcome.Queued)
                return;

            if (run.IsGroup)
                run.MarkMemberDeliveryWanted(memberGuid);
            else
                run.MarkPlacementWanted();
        }
    }
}
