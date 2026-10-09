using System;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Pvp;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The player side of the PvP arena Blood payout (Docs/Pvp/DESIGN.md "Rewards"). The coordinator decides each
    /// participant's amount and PvpBloodGrantDispatcher makes sure exactly one of these settles it:
    ///   - <see cref="GrantArenaBlood"/> runs on this player's own action chain. It applies the daily cap, delivers,
    ///     and banks whatever did not reach the pack as owed.
    ///   - <see cref="GrantArenaBloodOffline"/> is the same cap decision against the stored character; the Blood is
    ///     banked as owed.
    ///   - <see cref="DeliverOwedArenaBlood"/> delivers owed Blood after login, never touching the cap.
    /// Every ledger change goes through the pure PvpArenaRewards.SettleEarn / SettleOwedDelivery, so the arithmetic
    /// is unit tested. Blood is cosmetic currency only: Bonded, Attuned, no XP and no power.
    /// </summary>
    partial class Player
    {
        /// <summary>Seconds after entering the world before owed Blood is delivered, so the client has the pack.</summary>
        private const float OwedArenaBloodLoginDelaySeconds = 5.0f;

        /// <summary>
        /// One match's Blood, on this player's action chain. Capped: nothing is paid, nothing is incremented, one line
        /// says so. Otherwise the Blood is delivered and the ledger settled: the paid count goes up by one and whatever
        /// did not reach the pack - including everything left after a delivery that threw part way - is owed.
        /// </summary>
        public void GrantArenaBlood(int amount, PvpBloodGrant grant)
        {
            if (amount <= 0 || grant == null)
                return;

            var today = ArenaBloodToday(grant);
            var kind = grant.Ledger;
            var ledger = ReadArenaBloodLedger(kind);
            var result = PvpArenaRewards.Earn(ledger, amount, today, grant.DailyCap, DeliverArenaBlood, CountArenaBloodInPack);

            if (result.Capped)
            {
                log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): match {grant.MatchId} paid no Blood - daily cap {grant.DailyCap} already reached (day {today}, {kind} ledger)");
                Session?.Network.EnqueueSend(new GameMessageSystemChat(kind == PvpBloodLedgerKind.Battleground ? PvpArenaText.BgMarksDailyLimit : PvpArenaText.BloodDailyLimit, ChatMessageType.Broadcast));
                return;
            }

            var delivered = result.Delivered;
            var settled = result.Ledger;

            if (result.DeliveryError != null)
                log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): delivering Blood for match {grant.MatchId} threw after {delivered} of {amount}; the rest is banked as owed", result.DeliveryError);

            WriteArenaBloodLedger(kind, ledger, settled);

            var owed = amount - delivered;
            var bg = kind == PvpBloodLedgerKind.Battleground;

            if (delivered > 0)
                Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.Fill(bg ? PvpArenaText.BgMarksReceived : PvpArenaText.BloodReceived, ("amount", delivered), ("marks", PvpArenaText.MarkNoun(delivered))), ChatMessageType.Broadcast));

            if (owed > 0)
                Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.Fill(bg ? PvpArenaText.BgMarksOwed : PvpArenaText.BloodOwed, ("amount", owed), ("marks", PvpArenaText.MarkNoun(owed))), ChatMessageType.Broadcast));

            log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): match {grant.MatchId} paid {amount} Blood ({delivered} delivered, {owed} owed); paid match {settled.PaidCount} of day {settled.PaidDay} ({kind} ledger)");

            RushNextPlayerSave(5);
        }

        /// <summary>
        /// One match's Blood for a character with an offline record, on that stored character (OfflinePlayer, whose
        /// property access takes its own biota lock). The same cap decision as <see cref="GrantArenaBlood"/>; a paid
        /// amount is banked entirely as owed. False, with nothing written, when there is no offline record (still
        /// online, or mid-logout): the dispatcher retries later.
        /// </summary>
        public static bool GrantArenaBloodOffline(uint characterId, int amount, PvpBloodGrant grant)
        {
            if (amount <= 0 || grant == null)
                return true;

            var offline = PlayerManager.GetOfflinePlayer(characterId);

            if (offline == null)
                return false;

            var today = ArenaBloodToday(grant);
            var (countProp, dayProp) = LedgerProperties(grant.Ledger);
            var ledger = new PvpBloodLedger(
                offline.GetProperty(countProp) ?? 0,
                offline.GetProperty(dayProp) ?? 0,
                offline.GetProperty(PropertyInt.PvpArenaBloodOwed) ?? 0);

            // No delivery offline: a null deliverer delivers nothing, so the whole amount is banked as owed.
            var result = PvpArenaRewards.Earn(ledger, amount, today, grant.DailyCap, null);

            if (result.Capped)
            {
                log.Info($"[PVP] 0x{characterId:X8} (offline): match {grant.MatchId} paid no Blood - daily cap {grant.DailyCap} already reached (day {today}, {grant.Ledger} ledger)");
                return true;
            }

            var settled = result.Ledger;

            offline.SetProperty(countProp, settled.PaidCount);
            offline.SetProperty(dayProp, settled.PaidDay);
            offline.SetProperty(PropertyInt.PvpArenaBloodOwed, settled.Owed);
            offline.SaveBiotaToDatabase();

            log.Info($"[PVP] 0x{characterId:X8} (offline): match {grant.MatchId} paid {amount} Blood, banked as owed ({settled.Owed} owed in all); paid match {settled.PaidCount} of day {settled.PaidDay} ({grant.Ledger} ledger)");

            return true;
        }

        /// <summary>
        /// Queues delivery of any owed Blood a few seconds after entering the world (WorldManager's login path). A no-op
        /// when nothing is owed.
        /// </summary>
        public void ScheduleOwedArenaBloodDelivery()
        {
            if ((GetProperty(PropertyInt.PvpArenaBloodOwed) ?? 0) <= 0)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(OwedArenaBloodLoginDelaySeconds);
            chain.AddAction(this, () =>
            {
                try
                {
                    DeliverOwedArenaBlood();
                }
                catch (Exception ex)
                {
                    log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): delivering owed arena Blood threw", ex);
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// Delivers owed Blood, on this player's action chain. Never touches the daily cap: owed Blood was counted when
        /// it was earned. Whatever was not delivered - a full pack, or a delivery that threw part way - stays owed.
        /// </summary>
        public void DeliverOwedArenaBlood()
        {
            // Owed is one balance shared by both modes, and delivery never touches a cap, so the ledger kind is moot here.
            var ledger = ReadArenaBloodLedger(PvpBloodLedgerKind.Arena);

            if (ledger.Owed <= 0)
                return;

            var result = PvpArenaRewards.DeliverOwed(ledger, DeliverArenaBlood, CountArenaBloodInPack);
            var delivered = result.Delivered;
            var settled = result.Ledger;

            if (result.DeliveryError != null)
                log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): delivering owed Blood threw after {delivered} of {ledger.Owed}; the rest stays owed", result.DeliveryError);

            WriteArenaBloodLedger(PvpBloodLedgerKind.Arena, ledger, settled);

            if (delivered > 0)
                Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.Fill(PvpArenaText.BloodOwedDelivered, ("amount", delivered), ("marks", PvpArenaText.MarkNoun(delivered))), ChatMessageType.Broadcast));

            if (settled.Owed > 0)
                Session?.Network.EnqueueSend(new GameMessageSystemChat(PvpArenaText.Fill(PvpArenaText.BloodOwedRemaining, ("amount", settled.Owed), ("marks", PvpArenaText.MarkNoun(settled.Owed))), ChatMessageType.Broadcast));

            log.Info($"[PVP] {Name} (0x{Guid.Full:X8}): owed Blood at login {ledger.Owed}: {delivered} delivered, {settled.Owed} still owed");

            RushNextPlayerSave(5);
        }

        /// <summary>
        /// Puts up to <paramref name="amount"/> Blood in the pack: tops up existing non-full Blood stacks first, then
        /// creates new stacks (the layout is the pure PvpArenaRewards.PlanStacks). Stops at the first thing that does not
        /// fit (burden, or no pack slot for a new stack). <paramref name="delivered"/> is advanced as each stack lands,
        /// so a caller that catches a throw still knows exactly how much reached the pack. The encumbrance update goes
        /// out on every exit once anything was delivered.
        /// </summary>
        private void DeliverArenaBlood(int amount, ref int delivered)
        {
            if (amount <= 0)
                return;

            var weenie = DatabaseManager.World.GetCachedWeenie(PvpArenaRewards.BloodWcid);

            if (weenie == null)
            {
                log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): the Blood weenie {PvpArenaRewards.BloodWcid} is not in the world database; {amount} Blood kept as owed");
                return;
            }

            var maxStack = weenie.IsStackable() ? Math.Max(1, weenie.GetMaxStackSize()) : 1;
            var unitBurden = weenie.IsStackable() ? weenie.GetStackUnitEncumbrance() : (weenie.GetProperty(PropertyInt.EncumbranceVal) ?? 0);

            var stacks = GetInventoryItemsOfWCID(PvpArenaRewards.BloodWcid);
            var plan = PvpArenaRewards.PlanStacks(stacks.Select(s => s.StackSize ?? 1).ToList(), maxStack, amount);

            try
            {
                foreach (var (index, add) in plan.TopUps)
                {
                    var stack = FindObject(stacks[index].Guid, SearchLocations.MyInventory, out var container, out var rootContainer, out _);

                    if (stack == null || !HasEnoughBurdenToAddToInventory(unitBurden * add) || !AdjustStack(stack, add, container, rootContainer))
                        return;

                    delivered += add;
                    Session?.Network.EnqueueSend(new GameMessageSetStackSize(stack));
                }

                foreach (var size in plan.NewStacks)
                {
                    var item = WorldObjectFactory.CreateNewWorldObject(PvpArenaRewards.BloodWcid);

                    if (item == null)
                    {
                        log.Error($"[PVP] {Name} (0x{Guid.Full:X8}): could not create Blood {PvpArenaRewards.BloodWcid}");
                        return;
                    }

                    item.SetStackSize(size);

                    if (!TryCreateInInventoryWithNetworking(item))
                    {
                        item.Destroy();
                        return;
                    }

                    delivered += size;
                }
            }
            finally
            {
                if (delivered > 0)
                    Session?.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceVal, EncumbranceVal ?? 0));
            }
        }

        /// <summary>
        /// Blood in the pack right now. Passed to Earn / DeliverOwed so delivery is MEASURED, not only counted: a stack
        /// TryCreateInInventoryWithNetworking had already added before throwing (send, serialisation, save) is seen
        /// as delivered and never banked as owed as well.
        /// </summary>
        private int CountArenaBloodInPack() => GetNumInventoryItemsOfWCID(PvpArenaRewards.BloodWcid);

        /// <summary>
        /// The paid-count and paid-day properties of a daily-cap ledger: the arena's 9077 / 9078, the battleground's own
        /// 9083 / 9084. The owed balance (9079) is shared and not part of this choice.
        /// </summary>
        internal static (PropertyInt Count, PropertyInt Day) LedgerProperties(PvpBloodLedgerKind kind) => kind == PvpBloodLedgerKind.Battleground
            ? (PropertyInt.PvpBgMarksPaidCount, PropertyInt.PvpBgMarksPaidDay)
            : (PropertyInt.PvpArenaBloodPaidCount, PropertyInt.PvpArenaBloodPaidDay);

        private PvpBloodLedger ReadArenaBloodLedger(PvpBloodLedgerKind kind)
        {
            var (countProp, dayProp) = LedgerProperties(kind);

            return new PvpBloodLedger(
                GetProperty(countProp) ?? 0,
                GetProperty(dayProp) ?? 0,
                GetProperty(PropertyInt.PvpArenaBloodOwed) ?? 0);
        }

        /// <summary>Writes only what changed between <paramref name="before"/> and <paramref name="after"/>; owed 0 removes the property.</summary>
        private void WriteArenaBloodLedger(PvpBloodLedgerKind kind, PvpBloodLedger before, PvpBloodLedger after)
        {
            var (countProp, dayProp) = LedgerProperties(kind);

            if (after.PaidCount != before.PaidCount)
                SetProperty(countProp, after.PaidCount);

            if (after.PaidDay != before.PaidDay)
                SetProperty(dayProp, after.PaidDay);

            if (after.Owed != before.Owed)
            {
                if (after.Owed > 0)
                    SetProperty(PropertyInt.PvpArenaBloodOwed, after.Owed);
                else
                    RemoveProperty(PropertyInt.PvpArenaBloodOwed);
            }
        }

        /// <summary>The arena day now, at the reset zone and hour the match resolved under.</summary>
        private static int ArenaBloodToday(PvpBloodGrant grant)
        {
            var zone = PvpArenaRewards.ResolveZone(grant.ResetTimezone);
            return PvpArenaRewards.DayIndex((uint)Time.GetUnixTime(), zone, grant.ResetHour);
        }
    }
}
