using System;
using System.Collections.Generic;

using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Pvp
{
    /// <summary>How a participant's match is scored for the Blood payout. A forfeit is carried separately (it pays 0).</summary>
    public enum PvpBloodResult
    {
        Win,
        Loss,
        Draw
    }

    /// <summary>
    /// The daily-cap decision for one paid match: whether it pays, and the count and day to store when it does.
    /// When <see cref="Pay"/> is false nothing is written back: a match while capped never increments.
    /// </summary>
    public readonly record struct PvpBloodCapDecision(bool Pay, int NewCount, int NewDay);

    /// <summary>
    /// A character's stored Blood state: the paid-match count, the arena day it belongs to, and Blood owed (earned and
    /// already counted, not yet delivered). PropertyInt 9077 / 9078 / 9079.
    /// </summary>
    public readonly record struct PvpBloodLedger(int PaidCount, int PaidDay, int Owed);

    /// <summary>
    /// Puts up to <paramref name="amount"/> Blood in the pack, advancing <paramref name="delivered"/> as each stack lands,
    /// so that if it throws part way the caller still knows how much arrived.
    /// </summary>
    public delegate void PvpBloodDeliverer(int amount, ref int delivered);

    /// <summary>
    /// The result of <see cref="PvpArenaRewards.Earn"/> or <see cref="PvpArenaRewards.DeliverOwed"/>: whether the daily
    /// cap refused the match, the ledger to store, how much reached the pack, and the delivery exception if one was caught.
    /// </summary>
    public sealed record PvpBloodSettlement(bool Capped, PvpBloodLedger Ledger, int Delivered, Exception DeliveryError);

    /// <summary>
    /// How an amount of Blood lands in a pack: <see cref="TopUps"/> adds to existing non-full stacks (by their index in
    /// the list the plan was built from), then <see cref="NewStacks"/> are created, each at most one max stack.
    /// </summary>
    public sealed record PvpBloodStackPlan(IReadOnlyList<(int Index, int Add)> TopUps, IReadOnlyList<int> NewStacks);

    /// <summary>
    /// Which daily-cap ledger a grant counts against. Arena grants use PropertyInt 9077 / 9078, battleground grants
    /// their own 9083 / 9084, so neither mode's cap consumes the other's. The owed balance (9079) is shared: it is the
    /// same item, delivered the same way.
    /// </summary>
    public enum PvpBloodLedgerKind
    {
        Arena,
        Battleground
    }

    /// <summary>
    /// What the coordinator hands the gateway with each grant: the match (for logs), the cap dials as they stood
    /// at Resolve, so the player-side cap check reads the same settings the match resolved under, and the
    /// <see cref="Ledger"/> whose daily cap the grant counts against (defaults to the arena's).
    /// </summary>
    public sealed record PvpBloodGrant(Guid MatchId, int DailyCap, string ResetTimezone, int ResetHour, PvpBloodLedgerKind Ledger = PvpBloodLedgerKind.Arena);

    /// <summary>
    /// The PvP arena Blood payout, pure (Docs/Pvp/DESIGN.md "Rewards"; owner ruling 2026-10-01). Blood is a COSMETIC
    /// currency - Bonded and Attuned, spent only at the Bloodwarden, who stocks cosmetics only. No XP, power or
    /// tradeable reward of any kind comes from the arena.
    ///
    /// <para/>
    /// Per participant, exactly once per match: win <see cref="PvpArenaDials.BloodWin"/>, loss
    /// <see cref="PvpArenaDials.BloodLoss"/>, draw <see cref="PvpArenaDials.BloodDraw"/>, forfeiter 0. Nothing at all for
    /// a match that never went Live, a canceled match (Resolve never runs for one), or same-IP opponents. Unrated
    /// matches still pay. At most <see cref="PvpArenaDials.BloodDailyCap"/> PAID matches per character per arena day.
    ///
    /// <para/>
    /// Reads no PropertyManager tunable and touches no live clock or Player: every input is an argument, so all of it
    /// is unit testable. The coordinator decides the amount (<see cref="BloodFor"/>); the player's own action chain
    /// decides the cap (<see cref="DecideCap"/>) at grant time against the live character, and lays the stacks out
    /// (<see cref="PlanStacks"/>).
    /// </summary>
    public static class PvpArenaRewards
    {
        /// <summary>The Blood item (stackable, Bonded + Attuned). Authored as content; the one place its wcid lives in C#.</summary>
        public const uint BloodWcid = 1006650;

        /// <summary>
        /// The result a seat is paid as. A draw is a draw for everyone. FFA (more than two teams): first place, shared
        /// first included, is a win and every other placement a loss. Two-team: the coordinator's own won flag (the same
        /// one written to the match record).
        /// </summary>
        public static PvpBloodResult ResultFor(bool isDraw, bool twoTeam, bool won, int placement)
        {
            if (isDraw)
                return PvpBloodResult.Draw;

            if (!twoTeam)
                return placement == 1 ? PvpBloodResult.Win : PvpBloodResult.Loss;

            return won ? PvpBloodResult.Win : PvpBloodResult.Loss;
        }

        /// <summary>
        /// Blood owed to one participant of a resolved match, before the daily cap. 0 when the payout is off, the match
        /// never went Live, the opponents shared an IP, or the participant forfeited. A non-positive dial pays 0.
        /// </summary>
        public static int BloodFor(PvpBloodResult result, bool forfeited, bool wentLive, bool sameIp, PvpArenaDials dials)
        {
            if (dials == null || !dials.BloodEnabled || !wentLive || sameIp || forfeited)
                return 0;

            int amount;

            switch (result)
            {
                case PvpBloodResult.Win: amount = dials.BloodWin; break;
                case PvpBloodResult.Loss: amount = dials.BloodLoss; break;
                default: amount = dials.BloodDraw; break;
            }

            return Math.Max(0, amount);
        }

        /// <summary>
        /// The effective battleground Marks scale: a NaN or infinite value reads as 1.0 (unchanged), a negative value as 0
        /// (pays nothing), anything else as itself.
        /// </summary>
        public static double SanitizeMarksScale(double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale))
                return 1.0;

            return Math.Max(0.0, scale);
        }

        /// <summary>
        /// Marks owed to one participant of a resolved BATTLEGROUND match, before the battleground daily cap: the per-result
        /// base (pvp_bg_marks_win / _loss / _draw) times <see cref="SanitizeMarksScale"/> of pvp_bg_marks_scale, rounded
        /// to the nearest whole Mark (halves away from zero) and never below 0. 0 when the shared master switch
        /// (pvp_arena_blood_enabled, passed as <paramref name="enabled"/>) is off, the match never went Live, opposing teams shared an IP, or the participant
        /// forfeited. A non-positive base pays 0 whatever the scale. Saturates at int.MaxValue.
        /// </summary>
        public static int BgMarksFor(PvpBloodResult result, bool forfeited, bool wentLive, bool sameIp, bool enabled, BattlegroundDials bg)
        {
            if (bg == null || !enabled || !wentLive || sameIp || forfeited)
                return 0;

            int baseAmount;

            switch (result)
            {
                case PvpBloodResult.Win: baseAmount = bg.MarksWin; break;
                case PvpBloodResult.Loss: baseAmount = bg.MarksLoss; break;
                default: baseAmount = bg.MarksDraw; break;
            }

            if (baseAmount <= 0)
                return 0;

            var scaled = Math.Round(baseAmount * SanitizeMarksScale(bg.MarksScale), MidpointRounding.AwayFromZero);

            if (scaled >= int.MaxValue)
                return int.MaxValue;

            return (int)Math.Max(0.0, scaled);
        }

        /// <summary>
        /// The daily cap for one match that would pay. A stored day EARLIER than <paramref name="today"/> means the count
        /// belongs to a past day and reads as 0. A stored day at or after today counts as today - the Threads survey
        /// reset's rule (SurveyDayClock.IsToday): a stamp from the future (the clock stepped back) must read as already
        /// counted, never as a fresh day, or every backward clock step would reopen the cap. A cap of 0 or less is no cap.
        /// </summary>
        public static PvpBloodCapDecision DecideCap(int storedCount, int storedDay, int today, int dailyCap)
        {
            var current = storedDay >= today ? Math.Max(0, storedCount) : 0;
            var day = Math.Max(storedDay, today);

            if (dailyCap > 0 && current >= dailyCap)
                return new PvpBloodCapDecision(false, current, day);

            return new PvpBloodCapDecision(true, current == int.MaxValue ? current : current + 1, day);
        }

        /// <summary>
        /// One match's Blood against a character's ledger: the daily cap (refused = nothing delivered, ledger unchanged),
        /// then delivery, then <see cref="SettleEarn"/>. A delivery that throws is caught here, never propagated: whatever
        /// it had not delivered is banked as owed, and the cap is consumed exactly once either way. With
        /// <paramref name="countInPack"/> the delivered amount is MEASURED (Blood in the pack after minus before) as well as
        /// counted, and the larger wins: a stack that reached the pack before a throw is never banked as owed too.
        /// </summary>
        public static PvpBloodSettlement Earn(PvpBloodLedger ledger, int amount, int today, int dailyCap, PvpBloodDeliverer deliver, Func<int> countInPack = null)
        {
            var decision = DecideCap(ledger.PaidCount, ledger.PaidDay, today, dailyCap);

            if (!decision.Pay)
                return new PvpBloodSettlement(true, ledger, 0, null);

            var (delivered, error) = RunDelivery(amount, deliver, countInPack);

            return new PvpBloodSettlement(false, SettleEarn(ledger, decision, amount, delivered), delivered, error);
        }

        /// <summary>
        /// Delivers the owed Blood in <paramref name="ledger"/>, then <see cref="SettleOwedDelivery"/>. Takes no day and no
        /// cap on purpose: delivery never consults or consumes the cap. A delivery that throws is caught; the
        /// undelivered rest stays owed. <paramref name="countInPack"/> measures delivery as in <see cref="Earn"/>.
        /// </summary>
        public static PvpBloodSettlement DeliverOwed(PvpBloodLedger ledger, PvpBloodDeliverer deliver, Func<int> countInPack = null)
        {
            if (ledger.Owed <= 0)
                return new PvpBloodSettlement(false, ledger, 0, null);

            var (delivered, error) = RunDelivery(ledger.Owed, deliver, countInPack);

            return new PvpBloodSettlement(false, SettleOwedDelivery(ledger, delivered), delivered, error);
        }

        /// <summary>
        /// Runs the delivery, catching any throw. Delivered = max(what the deliverer counted, what the pack measurement
        /// shows arrived), clamped to [0, amount]. A failed measurement falls back to the count alone.
        /// </summary>
        private static (int Delivered, Exception Error) RunDelivery(int amount, PvpBloodDeliverer deliver, Func<int> countInPack)
        {
            var delivered = 0;
            Exception error = null;
            int? before = TryCount(countInPack);

            try
            {
                deliver?.Invoke(amount, ref delivered);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            var after = before.HasValue ? TryCount(countInPack) : null;

            if (before.HasValue && after.HasValue)
                delivered = Math.Max(delivered, after.Value - before.Value);

            return (Math.Clamp(delivered, 0, Math.Max(0, amount)), error);
        }

        private static int? TryCount(Func<int> countInPack)
        {
            if (countInPack == null)
                return null;

            try
            {
                return countInPack();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The ledger after a PAID match (<paramref name="decision"/>.Pay true) of which <paramref name="delivered"/> of
        /// <paramref name="amount"/> reached the pack: the count and day from the cap decision, and the undelivered rest
        /// added to owed. This is the only place the cap is consumed. A capped decision returns the ledger unchanged.
        /// </summary>
        public static PvpBloodLedger SettleEarn(PvpBloodLedger ledger, PvpBloodCapDecision decision, int amount, int delivered)
        {
            if (!decision.Pay)
                return ledger;

            var undelivered = Math.Max(0, amount - Math.Clamp(delivered, 0, Math.Max(0, amount)));

            return new PvpBloodLedger(decision.NewCount, decision.NewDay, AddOwed(ledger.Owed, undelivered));
        }

        /// <summary>
        /// The ledger after delivering <paramref name="delivered"/> of the owed Blood at login. Never reads or writes the
        /// cap: owed Blood was counted when it was earned. Delivering more than is owed cannot drive owed negative.
        /// </summary>
        public static PvpBloodLedger SettleOwedDelivery(PvpBloodLedger ledger, int delivered)
        {
            var owed = Math.Max(0, ledger.Owed);

            return ledger with { Owed = owed - Math.Clamp(delivered, 0, owed) };
        }

        /// <summary>Owed plus <paramref name="add"/>, saturating at int.MaxValue and never below 0.</summary>
        public static int AddOwed(int current, int add) => (int)Math.Min(int.MaxValue, (long)Math.Max(0, current) + Math.Max(0, add));

        /// <summary>
        /// The arena day <paramref name="unixSeconds"/> falls on: the Threads survey reset's own day arithmetic
        /// (SurveyDay.DayIndex), measured at the Blood reset zone and hour. Stored as a PropertyInt; a day number is
        /// well inside int range.
        /// </summary>
        public static int DayIndex(uint unixSeconds, TimeZoneInfo zone, int resetHour)
        {
            return (int)SurveyDay.DayIndex(unixSeconds, zone, Math.Clamp(resetHour, 0, 23));
        }

        /// <summary>
        /// Lays <paramref name="amount"/> Blood out over the existing stacks (sizes in <paramref name="existingStackSizes"/>):
        /// tops each non-full stack up in order, then creates new stacks of at most <paramref name="maxStack"/> for the
        /// rest. 995 in one stack + 10, max 1000: that stack to 1000 and one new stack of 5.
        /// </summary>
        public static PvpBloodStackPlan PlanStacks(IReadOnlyList<int> existingStackSizes, int maxStack, int amount)
        {
            var topUps = new List<(int Index, int Add)>();
            var newStacks = new List<int>();

            if (amount <= 0 || maxStack <= 0)
                return new PvpBloodStackPlan(topUps, newStacks);

            var remaining = amount;

            if (existingStackSizes != null)
            {
                for (var i = 0; i < existingStackSizes.Count && remaining > 0; i++)
                {
                    var room = maxStack - existingStackSizes[i];

                    if (room <= 0)
                        continue;

                    var add = Math.Min(room, remaining);
                    topUps.Add((i, add));
                    remaining -= add;
                }
            }

            while (remaining > 0)
            {
                var size = Math.Min(maxStack, remaining);
                newStacks.Add(size);
                remaining -= size;
            }

            return new PvpBloodStackPlan(topUps, newStacks);
        }

        /// <summary>The Blood reset zone, through the Threads survey reset's resolver and cache (unknown id: fixed UTC-5, warned once).</summary>
        internal static TimeZoneInfo ResolveZone(string zoneId) => SurveyArchivistStation.ResolveZone(zoneId);
    }
}
