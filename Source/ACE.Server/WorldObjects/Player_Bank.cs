using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    // WaffleACE banking system, ported/trimmed from Conquest-ACE.
    // Fungible currency (pyreals, luminance, legendary keys, promissory notes) can be deposited
    // into a persistent per-character bank, transferred between characters, and (for pyreals /
    // promissory notes) spent directly at vendors via the always-on shop hook (see Vendor.cs /
    // Player_Commerce.cs). Balances are stored as PropertyInt64 on the character biota, so no
    // schema change is required.
    partial class Player
    {
        // Guards cross-player balance mutation (transfers). Per-player deposit/withdraw are already
        // serialized by the single-threaded per-actor command processing, so they don't need it.
        private readonly object bankBalanceLock = new object();

        // Serializes credits to *offline* transfer targets. Two online senders crediting the same
        // offline character concurrently would otherwise lose an update (read-modify-write race).
        private static readonly object offlineBankTransferLock = new object();

        // Currency weenies
        private const uint PromissoryNoteWcid = 43901;   // "Promissory Note" (Absalom Sarraf currency)

        // "Class Ability Point" (Content/sql/weenies/1001010_classabilitypoint_currency.sql) - a display-only
        // vendor alternate-currency weenie whose "bank balance" IS the player's AvailableClassAbilityPoints.
        // The Drift Network class-ability trainers price their tokens in this currency; the always-on shop hook
        // below draws it straight from the CAP property, so no Class Ability Point item is ever granted or held.
        private const uint ClassAbilityPointCurrencyWcid = 1001010;

        // Legendary key weenies present in our world db, mapped to the number of uses each represents.
        private static readonly Dictionary<uint, int> LegendaryKeyUses = new Dictionary<uint, int>
        {
            { 48746, 1 },   // aged legendary key (single use)
            { 48748, 2 },   // legendary key (2 use)
            { 51954, 10 },  // durable legendary key (10 use)
            { 52010, 5 },   // rynthid legendary key (5 use)
        };
        private const uint LegendaryKeyWithdrawWcid = 48746; // withdrawn keys are single-use

        // Trade note (promissory-note ItemType) denominations, for withdrawing banked pyreals as notes.
        private static readonly (uint wcid, long value)[] TradeNoteDenoms =
        {
            (20630, 250000), // MMD
            (2627,  100000),
            (2626,   50000),
            (2625,   10000),
            (2624,    5000),
            (2623,    1000),
            (2622,     500),
            (2621,     100),
        };

        // Safety cap on how many discrete items a single withdraw can materialize (anti-spam / anti-DoS).
        private const int MaxWithdrawItems = 10000;

        // Minimum spacing between mutating bank commands, to blunt scripted spam. In-memory (per session).
        private const double BankCommandCooldownSeconds = 1.0;
        public DateTime LastBankCommandTime { get; set; } = DateTime.MinValue;

        #region Balance properties

        public long BankedPyreals
        {
            get => GetProperty(PropertyInt64.BankedPyreals) ?? 0;
            set => SetProperty(PropertyInt64.BankedPyreals, value);
        }

        public long BankedLuminance
        {
            get => GetProperty(PropertyInt64.BankedLuminance) ?? 0;
            set => SetProperty(PropertyInt64.BankedLuminance, value);
        }

        public long BankedLegendaryKeys
        {
            get => GetProperty(PropertyInt64.BankedLegendaryKeys) ?? 0;
            set => SetProperty(PropertyInt64.BankedLegendaryKeys, value);
        }

        public long BankedPromissoryNotes
        {
            get => GetProperty(PropertyInt64.BankedPromissoryNotes) ?? 0;
            set => SetProperty(PropertyInt64.BankedPromissoryNotes, value);
        }

        /// <summary>
        /// When false, vendor sale proceeds pay out as coin stacks into the pack (retail behavior) instead of
        /// being credited straight to the bank. Defaults to TRUE, so existing characters are unchanged.
        /// Exists because inventory-reading tools poll the actual pyreal stacks in the pack to decide what they
        /// can afford; with proceeds always banked, that reading never changes and a sell-to-restock loop
        /// cannot converge.
        /// </summary>
        public bool BankAutoDeposit
        {
            get => GetProperty(PropertyBool.BankAutoDeposit) ?? true;
            set => SetProperty(PropertyBool.BankAutoDeposit, value);
        }

        /// <summary>
        /// Applies a signed delta to a banked balance atomically under <see cref="bankBalanceLock"/> and
        /// returns the new balance. This is the ONLY way balances should be mutated: a player's own
        /// deposit/withdraw/spend run on that player's thread, but a transfer credits a balance from the
        /// *sender's* thread, so the read-modify-write must be serialized or a concurrent transfer-in
        /// could be lost (which, interleaved with a withdraw, would dupe currency).
        /// </summary>
        private long ModifyBankBalance(PropertyInt64 prop, long delta)
        {
            lock (bankBalanceLock)
            {
                var updated = (GetProperty(prop) ?? 0) + delta;
                SetProperty(prop, updated);
                return updated;
            }
        }

        #endregion

        /// <summary>
        /// Parses an amount string with an optional magnitude suffix (k/m/b/t/q) and optional decimal,
        /// e.g. "1000", "10k", "1.5m". Rejects blanks, negatives, non-numerics, and anything that would
        /// overflow a long (guarding against crafted inputs like "999999q").
        /// </summary>
        public static bool TryParseAmount(string input, out long value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            input = input.Trim().ToLowerInvariant();

            long multiplier = 1;
            switch (input[input.Length - 1])
            {
                case 'q': multiplier = 1_000_000_000_000_000L; input = input[..^1]; break;
                case 't': multiplier = 1_000_000_000_000L; input = input[..^1]; break;
                case 'b': multiplier = 1_000_000_000L; input = input[..^1]; break;
                case 'm': multiplier = 1_000_000L; input = input[..^1]; break;
                case 'k': multiplier = 1_000L; input = input[..^1]; break;
            }

            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                return false;

            if (double.IsNaN(numeric) || double.IsInfinity(numeric) || numeric < 0)
                return false;

            var result = numeric * multiplier;
            if (result > long.MaxValue) // reject overflow rather than wrapping to a bogus (possibly negative) long
                return false;

            value = (long)Math.Round(result);
            return value >= 0;
        }

        private void BankMsg(string message)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] {message}", ChatMessageType.System));
        }

        #region Deposit

        /// <summary>
        /// Deposits pyreals from inventory into the bank. Credits only what is actually removed.
        /// amount &lt;= 0 means "all".
        /// </summary>
        public long DepositPyreals(long amount = -1, bool suppressChat = false)
        {
            long deposited = 0;
            var removed = new List<WorldObject>();

            foreach (var coin in GetInventoryItemsOfWCID(coinStackWcid))
            {
                if (amount == 0)
                    break;

                var stackSize = coin.StackSize ?? 1;

                if (amount < 0 || amount >= stackSize)
                {
                    // take the whole stack
                    if (TryRemoveFromInventory(coin.Guid, out var whole))
                    {
                        removed.Add(whole);
                        deposited += stackSize;
                        if (amount > 0) amount -= stackSize;
                    }
                }
                else
                {
                    // take a partial stack: remove the stack, re-add the remainder (space is guaranteed since we just freed it)
                    var take = (int)amount;
                    if (TryRemoveFromInventory(coin.Guid, out var whole))
                    {
                        removed.Add(whole);
                        deposited += take;

                        var remainder = WorldObjectFactory.CreateNewWorldObject(coinStackWcid);
                        remainder.SetStackSize(stackSize - take);
                        if (!TryCreateInInventoryWithNetworking(remainder))
                        {
                            // Should not happen (we just freed a slot). Refund into the bank credit so nothing is lost.
                            log.Error($"[BANK] {Name} DepositPyreals could not re-add remainder of {stackSize - take}; crediting it to avoid loss.");
                            remainder.Destroy();
                            deposited += stackSize - take;
                        }
                        amount = 0;
                    }
                }
            }

            foreach (var wo in removed)
            {
                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(wo));
                wo.Destroy();
            }

            if (deposited > 0)
            {
                var balance = ModifyBankBalance(PropertyInt64.BankedPyreals, deposited);
                UpdateCoinValue();
                RushNextPlayerSave(5);
                if (!suppressChat) BankMsg($"Deposited {deposited:N0} pyreals. Balance: {balance:N0}");
            }
            else if (!suppressChat)
                BankMsg("No pyreals found to deposit.");

            return deposited;
        }

        /// <summary>
        /// Deposits the pyreal value of all trade notes (ItemType.PromissoryNote) in inventory.
        /// item.Value is the full stack value, so it is credited exactly once.
        /// </summary>
        public long DepositTradeNotes(bool suppressChat = false)
        {
            long deposited = 0;
            var removed = new List<WorldObject>();

            foreach (var note in GetAllTradeNotes())
            {
                var value = note.Value ?? 0;
                if (value <= 0)
                    continue;

                if (TryRemoveFromInventory(note.Guid, out var whole))
                {
                    removed.Add(whole);
                    deposited += value;
                }
            }

            foreach (var wo in removed)
            {
                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(wo));
                wo.Destroy();
            }

            if (deposited > 0)
            {
                var balance = ModifyBankBalance(PropertyInt64.BankedPyreals, deposited);
                UpdateCoinValue();
                RushNextPlayerSave(5);
                if (!suppressChat) BankMsg($"Deposited {deposited:N0} pyreals of trade notes. Balance: {balance:N0}");
            }
            else if (!suppressChat)
                BankMsg("No trade notes found to deposit.");

            return deposited;
        }

        private List<WorldObject> GetAllTradeNotes()
        {
            var notes = new List<WorldObject>();
            foreach (var item in GetAllPossessions())
            {
                if (item.ItemType == ItemType.PromissoryNote)
                    notes.Add(item);
            }
            return notes;
        }

        /// <summary>
        /// Deposits the pyreal face value of every bankable pea (silver / gold / pyreal) in inventory.
        /// Credits only what is actually removed. Peas fold into the pyreal balance - there is no separate
        /// pea balance and no way to withdraw peas back out.
        /// </summary>
        public long DepositPeas(bool suppressChat = false)
        {
            long deposited = 0;
            long peas = 0;
            var removed = new List<WorldObject>();

            foreach (var wcid in BankablePeas.FaceValues.Keys)
            {
                foreach (var pea in GetInventoryItemsOfWCID(wcid))
                {
                    var stackSize = Math.Max(1, pea.StackSize ?? 1);

                    if (!BankablePeas.TryGetFaceValue(pea.WeenieClassId, stackSize, out var stackValue))
                        continue;

                    if (TryRemoveFromInventory(pea.Guid, out var whole))
                    {
                        removed.Add(whole);
                        deposited += stackValue;
                        peas += stackSize;
                    }
                }
            }

            foreach (var wo in removed)
            {
                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(wo));
                wo.Destroy();
            }

            if (deposited > 0)
            {
                var balance = ModifyBankBalance(PropertyInt64.BankedPyreals, deposited);
                UpdateCoinValue();
                RushNextPlayerSave(5);
                if (!suppressChat) BankMsg($"Deposited {peas:N0} pea(s) worth {deposited:N0} pyreals. Balance: {balance:N0}");
            }
            else if (!suppressChat)
                BankMsg("No peas found to deposit.");

            return deposited;
        }

        /// <summary>
        /// Moves available (earned) luminance into the bank. Requires the player to be luminance-flagged.
        /// amount &lt;= 0 means "all available".
        /// </summary>
        public long DepositLuminance(long amount = -1, bool suppressChat = false)
        {
            if (!MaximumLuminance.HasValue || MaximumLuminance <= 0)
            {
                if (!suppressChat) BankMsg("You have not been luminance flagged yet.");
                return 0;
            }

            var available = AvailableLuminance ?? 0;
            var toDeposit = amount < 0 ? available : Math.Min(amount, available);

            if (toDeposit <= 0)
            {
                if (!suppressChat) BankMsg("No luminance available to deposit.");
                return 0;
            }

            AvailableLuminance = available - toDeposit;
            var balance = ModifyBankBalance(PropertyInt64.BankedLuminance, toDeposit);
            RushNextPlayerSave(5);

            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableLuminance, AvailableLuminance ?? 0));
            if (!suppressChat) BankMsg($"Deposited {toDeposit:N0} luminance. Balance: {balance:N0}");
            return toDeposit;
        }

        /// <summary>
        /// Everything this player could spend at a Luminance-priced NPC: capped available Luminance plus the
        /// uncapped bank. This is the read-side counterpart of <see cref="TrySpendLuminanceIncludingBank"/>,
        /// and the two MUST agree on which pools count.
        ///
        /// Retail content puts an affordability precheck in front of every Luminance purchase, and that check
        /// is a plain InqInt64Stat on PropertyInt64.AvailableLuminance - a raw property read that knows
        /// nothing about the bank. When the spend side learned about the bank and the check side did not, a
        /// player holding banked Luminance was told "You do not have enough Luminance." for a purchase they
        /// could afford, and never reached the spend at all. See EmoteManager's InqInt64Stat handler.
        /// </summary>
        public long GetSpendableLuminance() => (AvailableLuminance ?? 0) + BankedLuminance;

        /// <summary>
        /// Spends <paramref name="amount"/> Luminance at an NPC, drawing from available (earned) Luminance
        /// first and then the uncapped bank. Returns FALSE without spending anything if the combined total is
        /// insufficient. Luminance is spendable this way but deliberately NOT transferable between players
        /// (see the disabled TransferLuminance) - the bank is a personal wallet, not a trade channel. This is
        /// the single luminance-spend entry point for Luminance-priced NPC content such as class ability tokens.
        /// </summary>
        public bool TrySpendLuminanceIncludingBank(long amount)
        {
            if (amount <= 0)
                return true;

            var available = AvailableLuminance ?? 0;
            if (available + BankedLuminance < amount)
                return false;

            var fromAvailable = Math.Min(available, amount);
            if (fromAvailable > 0)
                SpendLuminance(fromAvailable);   // sends the available-luminance network update

            var fromBank = amount - fromAvailable;
            if (fromBank > 0)
            {
                ModifyBankBalance(PropertyInt64.BankedLuminance, -fromBank);
                RushNextPlayerSave(5);
            }

            return true;
        }

        /// <summary>
        /// Directly credits banked Luminance (admin / testing), bypassing the deposit flow's luminance-flag
        /// and available-balance requirements, and returns the new banked balance. Banked Luminance is uncapped
        /// (unlike available Luminance, which MaximumLuminance limits), so this is how a test character gets
        /// enough Luminance to spend on Luminance-priced content.
        /// </summary>
        public long AddBankedLuminance(long amount)
        {
            var balance = ModifyBankBalance(PropertyInt64.BankedLuminance, amount);
            RushNextPlayerSave(5);
            return balance;
        }

        /// <summary>
        /// Deposits legendary keys, crediting the number of *uses* each key represents.
        /// </summary>
        public long DepositLegendaryKeys(bool suppressChat = false)
        {
            long deposited = 0;
            var removed = new List<WorldObject>();

            foreach (var kvp in LegendaryKeyUses)
            {
                foreach (var key in GetInventoryItemsOfWCID(kvp.Key))
                {
                    var uses = key.Structure ?? (uint)kvp.Value;
                    if (TryRemoveFromInventory(key.Guid, out var whole))
                    {
                        removed.Add(whole);
                        deposited += uses;
                    }
                }
            }

            foreach (var wo in removed)
            {
                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(wo));
                wo.Destroy();
            }

            if (deposited > 0)
            {
                var balance = ModifyBankBalance(PropertyInt64.BankedLegendaryKeys, deposited);
                RushNextPlayerSave(5);
                if (!suppressChat) BankMsg($"Deposited {deposited:N0} legendary keys. Balance: {balance:N0}");
            }
            else if (!suppressChat)
                BankMsg("No legendary keys found to deposit.");

            return deposited;
        }

        /// <summary>
        /// Deposits promissory notes (count-based token). Credits one per note removed.
        /// </summary>
        public long DepositPromissoryNotes(bool suppressChat = false)
        {
            long deposited = 0;
            var removed = new List<WorldObject>();

            foreach (var note in GetInventoryItemsOfWCID(PromissoryNoteWcid))
            {
                var count = note.StackSize ?? 1; // 43901 is a Stackable (MaxStackSize 1000), so credit the whole stack
                if (TryRemoveFromInventory(note.Guid, out var whole))
                {
                    removed.Add(whole);
                    deposited += count;
                }
            }

            foreach (var wo in removed)
            {
                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(wo));
                wo.Destroy();
            }

            if (deposited > 0)
            {
                var balance = ModifyBankBalance(PropertyInt64.BankedPromissoryNotes, deposited);
                RushNextPlayerSave(5);
                if (!suppressChat) BankMsg($"Deposited {deposited:N0} promissory notes. Balance: {balance:N0}");
            }
            else if (!suppressChat)
                BankMsg("No promissory notes found to deposit.");

            return deposited;
        }

        /// <summary>
        /// Deposits everything the bank supports.
        /// </summary>
        public void DepositAll()
        {
            DepositPyreals(-1, suppressChat: true);
            DepositTradeNotes(suppressChat: true);
            DepositPeas(suppressChat: true);
            DepositLuminance(-1, suppressChat: true);
            DepositLegendaryKeys(suppressChat: true);
            DepositPromissoryNotes(suppressChat: true);
            BankMsg($"Deposited all. Pyreals: {BankedPyreals:N0} | Luminance: {BankedLuminance:N0} | Keys: {BankedLegendaryKeys:N0} | Promissory Notes: {BankedPromissoryNotes:N0}");
        }

        #endregion

        #region Withdraw

        /// <summary>
        /// Size of the next stack to hand out: the smaller of what is still owed and the weenie's own
        /// MaxStackSize, never below 1. Kept as a pure static so the clamp and the int64 -> int32 narrowing
        /// are unit-testable without a live Player (the same pattern as TryDebitClassAbilityPoints).
        ///
        /// <paramref name="maxStackSize"/> is the object's own value and is deliberately NOT defaulted to any
        /// hardcoded cap: trade-note MaxStackSize is world-database data, so a constant here would diverge from
        /// the weenie the next time that value is retuned. Null or a nonsense 0 falls back to 1 (one unit per
        /// object), which is the safe direction - it hands out more objects, never more units.
        ///
        /// The Math.Min runs in long and only then narrows, so a caller owing more than int.MaxValue units
        /// still gets a correctly clamped stack rather than a wrapped negative one.
        /// </summary>
        internal static int NextStackSize(long wanted, ushort? maxStackSize)
        {
            var maxStack = maxStackSize ?? 1;
            if (maxStack < 1)
                maxStack = 1;

            if (wanted < 1)
                return 1;

            return (int)Math.Min(wanted, (long)maxStack);
        }

        /// <summary>
        /// Creates ONE stack of <paramref name="wcid"/> in the player's pack, sized at the smaller of
        /// <paramref name="wanted"/> and the weenie's own MaxStackSize, and returns how many units actually
        /// reached the inventory. Returns 0 - having destroyed the stack, so nothing is orphaned - if the
        /// weenie could not be created or the pack had no room. Callers loop over stacks and debit the bank
        /// by the returned totals, so a partial withdrawal never debits more than the player received.
        ///
        /// The per-stack cap is read off the created object rather than hardcoded: trade-note MaxStackSize is
        /// world-database data (see Content/sql/patches/trade_notes_stack_1000.sql), so a hardcoded number
        /// here would silently diverge from the weenie the next time that value is retuned.
        ///
        /// The placed size is read back from the object instead of assumed, because SetStackSize is a no-op
        /// on anything that is not a Stackable (WorldObject_Properties.cs) - a weenie retyped away from
        /// Stackable would otherwise credit a full stack while handing over a single item.
        /// </summary>
        private long TryCreateStackInInventory(uint wcid, long wanted)
        {
            if (wanted <= 0)
                return 0;

            var stack = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (stack == null)
                return 0;

            stack.SetStackSize(NextStackSize(wanted, stack.MaxStackSize));

            var placed = stack.StackSize ?? 1;

            if (!TryCreateInInventoryWithNetworking(stack))
            {
                stack.Destroy();
                return 0;
            }

            return placed;
        }

        /// <summary>
        /// Withdraws banked pyreals into inventory: 250k MMD trade notes for the bulk, coin stacks for the
        /// remainder. Debits only what was actually created (partial withdrawal on insufficient pack space).
        /// </summary>
        public void WithdrawPyreals(long amount)
        {
            if (amount <= 0) { BankMsg("Amount must be greater than zero."); return; }
            if (amount > BankedPyreals) { BankMsg($"Insufficient banked pyreals. You have {BankedPyreals:N0}."); return; }

            long created = 0;
            int items = 0;

            // Bulk as 250k notes to keep item/object count sane. MaxWithdrawItems counts objects (stacks)
            // here, which is what the constant's "discrete items a single withdraw can materialize" means.
            long remaining = amount;
            var mmd = TradeNoteDenoms[0];
            while (remaining >= mmd.value && items < MaxWithdrawItems)
            {
                var notes = TryCreateStackInInventory(mmd.wcid, remaining / mmd.value);
                if (notes <= 0) break;
                created += notes * mmd.value;
                remaining -= notes * mmd.value;
                items++;
            }

            // Remainder as coin stacks.
            while (remaining > 0 && items < MaxWithdrawItems)
            {
                var coins = TryCreateStackInInventory(coinStackWcid, remaining);
                if (coins <= 0) break;
                created += coins;
                remaining -= coins;
                items++;
            }

            if (created <= 0) { BankMsg("Could not withdraw - check your pack space."); return; }

            var balance = ModifyBankBalance(PropertyInt64.BankedPyreals, -created);
            UpdateCoinValue();
            RushNextPlayerSave(5);

            if (created == amount)
                BankMsg($"Withdrew {created:N0} pyreals. Balance: {balance:N0}");
            else
                BankMsg($"Withdrew {created:N0} pyreals (partial - out of pack space). Balance: {balance:N0}");
        }

        /// <summary>
        /// Withdraws banked pyreals as trade notes of a specific denomination value, emitted as STACKS rather
        /// than as single notes: one full stack per pack slot until the requested count is met, then a
        /// remainder stack. Debits only the notes that actually reached the pack.
        /// </summary>
        public void WithdrawTradeNotes(long denomValue, long count)
        {
            if (count <= 0) { BankMsg("Count must be greater than zero."); return; }

            uint wcid = 0;
            foreach (var d in TradeNoteDenoms)
                if (d.value == denomValue) { wcid = d.wcid; break; }

            if (wcid == 0) { BankMsg("Unknown trade note denomination."); return; }

            var totalCost = denomValue * count;
            if (totalCost > BankedPyreals) { BankMsg($"Insufficient banked pyreals. Need {totalCost:N0}, have {BankedPyreals:N0}."); return; }
            if (count > MaxWithdrawItems) { BankMsg($"Cannot withdraw more than {MaxWithdrawItems:N0} notes at once."); return; }

            long createdNotes = 0;
            long remaining = count;

            while (remaining > 0)
            {
                var notes = TryCreateStackInInventory(wcid, remaining);
                if (notes <= 0) break;   // weenie missing, or the pack is full - keep whatever already landed
                createdNotes += notes;
                remaining -= notes;
            }

            if (createdNotes <= 0) { BankMsg("Could not withdraw - check your pack space."); return; }

            var balance = ModifyBankBalance(PropertyInt64.BankedPyreals, -(createdNotes * denomValue));
            UpdateCoinValue();
            RushNextPlayerSave(5);

            if (createdNotes == count)
                BankMsg($"Withdrew {createdNotes:N0} trade note(s) worth {createdNotes * denomValue:N0} pyreals. Balance: {balance:N0}");
            else
                BankMsg($"Withdrew {createdNotes:N0} of {count:N0} trade note(s) worth {createdNotes * denomValue:N0} pyreals (partial - out of pack space). Balance: {balance:N0}");
        }

        /// <summary>
        /// Withdraws banked legendary keys as single-use keys. Debits only what was created.
        ///
        /// Deliberately NOT stacked, unlike the trade-note and promissory-note paths: the legendary key
        /// weenies are WeenieType.Key (48746/48748/51954/52010, verified against ace_world), so they are not
        /// Stackable and carry no MaxStackSize row at all - SetStackSize would simply no-op on them.
        /// </summary>
        public void WithdrawLegendaryKeys(long amount)
        {
            if (amount <= 0) { BankMsg("Amount must be greater than zero."); return; }
            if (amount > BankedLegendaryKeys) { BankMsg($"Insufficient banked legendary keys. You have {BankedLegendaryKeys:N0}."); return; }
            if (amount > MaxWithdrawItems) { BankMsg($"Cannot withdraw more than {MaxWithdrawItems:N0} keys at once."); return; }

            long created = 0;
            for (long i = 0; i < amount; i++)
            {
                var key = WorldObjectFactory.CreateNewWorldObject(LegendaryKeyWithdrawWcid);
                if (key == null) break;
                if (!TryCreateInInventoryWithNetworking(key)) { key.Destroy(); break; }
                created++;
            }

            if (created <= 0) { BankMsg("Could not withdraw - check your pack space."); return; }

            var balance = ModifyBankBalance(PropertyInt64.BankedLegendaryKeys, -created);
            RushNextPlayerSave(5);
            BankMsg($"Withdrew {created:N0} legendary key(s). Balance: {balance:N0}");
        }

        /// <summary>
        /// Withdraws banked promissory notes, emitted as STACKS (wcid 43901 is a Stackable with its own
        /// MaxStackSize). Debits only what was created.
        /// </summary>
        public void WithdrawPromissoryNotes(long amount)
        {
            if (amount <= 0) { BankMsg("Amount must be greater than zero."); return; }
            if (amount > BankedPromissoryNotes) { BankMsg($"Insufficient banked promissory notes. You have {BankedPromissoryNotes:N0}."); return; }
            if (amount > MaxWithdrawItems) { BankMsg($"Cannot withdraw more than {MaxWithdrawItems:N0} promissory notes at once."); return; }

            long created = 0;
            long remaining = amount;

            while (remaining > 0)
            {
                var notes = TryCreateStackInInventory(PromissoryNoteWcid, remaining);
                if (notes <= 0) break;
                created += notes;
                remaining -= notes;
            }

            if (created <= 0) { BankMsg("Could not withdraw - check your pack space."); return; }

            var balance = ModifyBankBalance(PropertyInt64.BankedPromissoryNotes, -created);
            RushNextPlayerSave(5);

            if (created == amount)
                BankMsg($"Withdrew {created:N0} promissory note(s). Balance: {balance:N0}");
            else
                BankMsg($"Withdrew {created:N0} of {amount:N0} promissory note(s) (partial - out of pack space). Balance: {balance:N0}");
        }

        #endregion

        #region Transfer

        private enum BankCurrency { Pyreals, Luminance, LegendaryKeys, PromissoryNotes }

        private long GetBankBalance(BankCurrency c) => c switch
        {
            BankCurrency.Pyreals => BankedPyreals,
            BankCurrency.Luminance => BankedLuminance,
            BankCurrency.LegendaryKeys => BankedLegendaryKeys,
            BankCurrency.PromissoryNotes => BankedPromissoryNotes,
            _ => 0
        };

        private static PropertyInt64 BankProp(BankCurrency c) => c switch
        {
            BankCurrency.Pyreals => PropertyInt64.BankedPyreals,
            BankCurrency.Luminance => PropertyInt64.BankedLuminance,
            BankCurrency.LegendaryKeys => PropertyInt64.BankedLegendaryKeys,
            BankCurrency.PromissoryNotes => PropertyInt64.BankedPromissoryNotes,
            _ => PropertyInt64.BankedPyreals
        };

        public bool TransferPyreals(long amount, string target) => Transfer(BankCurrency.Pyreals, "pyreals", amount, target);
        // Luminance transfer is currently disabled. To re-enable, restore the Transfer(...) call below
        // (the underlying Transfer(BankCurrency.Luminance, ...) support is intact) and re-enable the "l"
        // case in PlayerCommands.HandleBankTransfer.
        public bool TransferLuminance(long amount, string target)
        {
            BankMsg("Luminance transfer is currently disabled.");
            return false;
        }
        public bool TransferLegendaryKeys(long amount, string target) => Transfer(BankCurrency.LegendaryKeys, "legendary keys", amount, target);
        public bool TransferPromissoryNotes(long amount, string target) => Transfer(BankCurrency.PromissoryNotes, "promissory notes", amount, target);

        private bool Transfer(BankCurrency currency, string label, long amount, string targetName)
        {
            if (amount <= 0) { BankMsg("Transfer amount must be greater than zero."); return false; }
            if (string.IsNullOrWhiteSpace(targetName)) { BankMsg("Specify a target character."); return false; }

            if (GetBankBalance(currency) < amount)
            {
                BankMsg($"Insufficient banked {label} to transfer. You have {GetBankBalance(currency):N0}.");
                return false;
            }

            var target = PlayerManager.FindByName(targetName);
            if (target == null) { BankMsg($"Character '{targetName}' not found."); return false; }

            if (target.Guid == Guid) { BankMsg($"You cannot transfer {label} to yourself."); return false; }

            var prop = BankProp(currency);

            if (target is Player online)
            {
                // Deadlock-safe ordered double-lock on the two players' balance locks.
                var sourceFirst = Guid.Full <= online.Guid.Full;
                var first = sourceFirst ? bankBalanceLock : online.bankBalanceLock;
                var second = sourceFirst ? online.bankBalanceLock : bankBalanceLock;

                lock (first)
                {
                    lock (second)
                    {
                        if (GetBankBalance(currency) < amount) { BankMsg($"Insufficient banked {label} to transfer."); return false; }
                        SetProperty(prop, GetBankBalance(currency) - amount);
                        online.SetProperty(prop, (online.GetProperty(prop) ?? 0) + amount);
                    }
                }

                online.Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] Received {amount:N0} {label} from {Name}.", ChatMessageType.System));
                online.RushNextPlayerSave(5);

                // A pyreal credit changes what the recipient can spend at a vendor, so refresh their coin display.
                online.SendSpendableCoinValue();
            }
            else
            {
                // Offline target: serialize offline credits globally, and only debit self under our lock.
                lock (offlineBankTransferLock)
                {
                    lock (bankBalanceLock)
                    {
                        if (GetBankBalance(currency) < amount) { BankMsg($"Insufficient banked {label} to transfer."); return false; }
                        SetProperty(prop, GetBankBalance(currency) - amount);
                    }

                    target.SetProperty(prop, (target.GetProperty(prop) ?? 0) + amount);
                    target.SaveBiotaToDatabase();
                }
            }

            RushNextPlayerSave(5);

            // The debit reduces our own spendable coin, so refresh our display too.
            SendSpendableCoinValue();

            BankMsg($"Transferred {amount:N0} {label} to {target.Name}.");

            // Analytics (Tier-2): record the currency transfer for flow/mule detection (recipient may be offline).
            AnalyticsManager.RecordBankTransfer(this, target.Guid.Full, target.Name, label, amount);

            return true;
        }

        #endregion

        #region Shop hook bridge (alternate currencies)

        /// <summary>
        /// Banked amount of a vendor alternate currency, for the always-on shop hook. Promissory notes are
        /// backed by their own banked balance; Class Ability Points are backed by the CAP property (there is no
        /// bankable item behind them at all). Every other alternate currency returns 0 (inventory-only).
        /// </summary>
        public long GetBankedAlternateCurrency(uint wcid)
        {
            if (wcid == PromissoryNoteWcid)
                return BankedPromissoryNotes;
            if (wcid == ClassAbilityPointCurrencyWcid)
                return AvailableClassAbilityPoints;
            return 0;
        }

        /// <summary>
        /// Debits a banked alternate currency for a vendor purchase. Returns the display name, or null
        /// if the currency is not bankable or the balance is insufficient (caller must not hand over goods).
        /// </summary>
        public string DebitBankedAlternateCurrency(uint wcid, long amount)
        {
            if (wcid == PromissoryNoteWcid && BankedPromissoryNotes >= amount)
            {
                ModifyBankBalance(PropertyInt64.BankedPromissoryNotes, -amount);
                return "Promissory Notes";
            }
            if (wcid == ClassAbilityPointCurrencyWcid && TryDebitClassAbilityPoints(AvailableClassAbilityPoints, amount, out var newPoints))
            {
                AvailableClassAbilityPoints = newPoints;
                SaveBiotaToDatabase();
                return "Class Ability Points";
            }
            return null;
        }

        /// <summary>
        /// Pure core of the Class Ability Point vendor-currency debit, kept static so the affordability guard
        /// and the int64->int32 narrowing are unit-testable without a live Player (the same pattern as
        /// ClassAbilityTokenCatalog.Evaluate). Returns FALSE without changing <paramref name="newPoints"/> when
        /// the debit is negative or exceeds the balance; otherwise sets the resulting balance and returns TRUE.
        /// CAP is an int32 and token prices are single digits, so a genuine purchase never nears the boundary;
        /// the amount &lt;= currentPoints guard also bounds <paramref name="amount"/> to an int, making the cast safe.
        /// </summary>
        internal static bool TryDebitClassAbilityPoints(int currentPoints, long amount, out int newPoints)
        {
            newPoints = currentPoints;
            if (amount < 0 || amount > currentPoints)
                return false;

            newPoints = currentPoints - (int)amount;
            return true;
        }

        public static string GetAlternateCurrencyName(uint wcid)
        {
            if (wcid == PromissoryNoteWcid)
                return "Promissory Notes";
            if (wcid == ClassAbilityPointCurrencyWcid)
                return "Class Ability Points";
            return null;
        }

        #endregion
    }

    /// <summary>
    /// The loot-drop "pea" spell components the bank accepts, and the pyreal face value each one banks for.
    ///
    /// Peas are tier-scaled monster drops (see Factories/Tables/Wcids/SpellComponentWcids.Roll) with an
    /// unusually high value-to-burden ratio - 50,000 pyreals at 10 burden for a Pyreal Pea - so players carry
    /// them as portable wealth rather than as components, and vendors already buy them at up to face value
    /// (BuyPrice 0.6-1.0 on the archmages that stock them). Banking them cannot eat a caster's components:
    /// peas occupy spell-component ids 113-186, and no spell in the client spell table references one - every
    /// base formula draws only from ids 1-74, 110-112 and 187-198, and the per-account scramble in
    /// SpellTable.RandomizeVersion1/2/3 rewrites only taper slots, clamped to [63,74] by its `% 0xC + 63`.
    /// So Spell.GetComponentWCID never yields a pea wcid. Verified against client_portal.dat on 2026-07-31 by
    /// dumping the spell table, not inferred from the naming.
    ///
    /// Face value comes from this table rather than from the item's own Value property, so a pea whose Value
    /// was somehow altered can never credit more than face.
    ///
    /// This lives outside Player deliberately. Player is beforefieldinit with a static field initializer that
    /// hits the world database (Player_Location.cs), so reading a static table through Player from a unit test
    /// drags in that type initializer and throws TypeInitializationException with no server running.
    /// </summary>
    internal static class BankablePeas
    {
        internal static readonly Dictionary<uint, long> FaceValues = new Dictionary<uint, long>
        {
            { 8331, 12500 },   // Silver Pea  (peascarabsilver)
            { 8327, 25000 },   // Gold Pea    (peascarabgold)
            { 8330, 50000 },   // Pyreal Pea  (peascarabpyreal)
        };

        /// <summary>
        /// Face value of a stack of bankable peas, or FALSE if the wcid is not one. A missing or corrupt
        /// stack size counts as a single pea rather than crediting zero (or, if negative, a debit).
        /// </summary>
        internal static bool TryGetFaceValue(uint wcid, int stackSize, out long value)
        {
            value = 0;

            if (!FaceValues.TryGetValue(wcid, out var unitValue))
                return false;

            value = unitValue * Math.Max(1, stackSize);
            return true;
        }
    }
}
