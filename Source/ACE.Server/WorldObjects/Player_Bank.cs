using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Database;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;
using ACE.Server.Entity.Facets;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Managers.Analytics;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    /// <summary>Legendary key weenies /bank deposit accepts. Split out of Player so tests can reference it directly.</summary>
    internal static class BankLegendaryKeyTable
    {
        // Legendary key weenies present in our world db, mapped to the number of uses each represents (their
        // MaxStructure). Every non-bonded key that opens the legendary chests is listed: the lock codes
        // keychestleg (48746, 51954) and legarmormagicweaponchest (the rest). The four BONDED variants
        // (72474, 72628, 72635, 72669) are deliberately absent - banking one would launder it into a
        // tradeable key on withdrawal.
        internal static readonly Dictionary<uint, int> Uses = new Dictionary<uint, int>
        {
            { 48746, 1 },   // aged legendary key (single use)
            { 48747, 1 },   // legendary key
            { 48748, 2 },   // legendary key (2 use)
            { 48749, 3 },   // legendary key (3 use)
            { 48750, 4 },   // legendary key (4 use)
            { 48914, 1 },   // legendary key
            { 51558, 1 },   // legendary key
            { 51586, 3 },   // legendary key (3 use)
            { 51648, 3 },   // legendary key (3 use)
            { 51954, 10 },  // durable legendary key (10 use)
            { 51963, 25 },  // legendary key (25 use)
            { 52010, 5 },   // rynthid legendary key (5 use)
            { 72048, 1 },   // legendary key
            { 72338, 3 },   // legendary key (3 use)
            { 72600, 1 },   // legendary key
            { 72807, 2 },   // legendary key (2 use)
            { 87168, 4 },   // legendary key (4 use)
        };

        // Withdrawal key weenies. Uses is the source of truth for how many uses each represents.
        internal const uint AgedWcid = 48746;
        internal const uint DurableWcid = 51954;
        internal static readonly int DurableUses = Uses[DurableWcid];
    }

    // WaffleACE banking system, ported/trimmed from Conquest-ACE.
    // Fungible currency (pyreals, luminance, legendary keys, promissory notes) can be deposited
    // into a persistent per-character bank, transferred between characters, and (for pyreals /
    // promissory notes / MMDs) spent directly at vendors via the always-on shop hook (see Vendor.cs /
    // Player_Commerce.cs). MMDs (Trade Note (250,000)) are not banked as their own balance; a vendor
    // priced in MMDs draws the shortfall (after pack MMDs are spent) straight from banked pyreals at
    // 250,000 pyreals per note, floored - see the "Shop hook bridge" region below. Balances are stored
    // as PropertyInt64 on the character biota, so no schema change is required.
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

        // "Trade Note (250,000)" (tradenote250000, Factories/Enum/WeenieClassName.cs) - the MMD. Not itself
        // bankable as its own balance; instead it is backed by BankedPyreals at MmdValue pyreals per note
        // (floor division), the same banked pyreals a player can already withdraw as trade notes (see
        // TradeNoteDenoms above).
        //
        // INTERNAL, not private: ACE.Server.RefireStations (the Workmanship Reforge and its siblings, the
        // Arcane Alignment Table and Defense Requirement Reforge) reuses these two constants directly rather
        // than duplicating the MMD wcid/value pair.
        internal const uint MmdWcid = 20630;
        internal const long MmdValue = 250000;

        // "Class Ability Point" (Content/sql/weenies/1001010_classabilitypoint_currency.sql) - a display-only
        // vendor alternate-currency weenie whose "bank balance" IS the player's AvailableClassAbilityPoints.
        // The Drift Network class-ability trainers price their tokens in this currency; the always-on shop hook
        // below draws it straight from the CAP property, so no Class Ability Point item is ever granted or held.
        private const uint ClassAbilityPointCurrencyWcid = 1001010;


        /// <summary>
        /// Splits a legendary-key withdrawal, measured in uses, into Durable Legendary Keys (10 uses each) plus
        /// Aged Legendary Keys (1 use each) for the remainder: 15 uses -> 1 durable + 5 aged. Pure so it can
        /// be unit tested; the caller applies the item-count cap to <c>Durable + Aged</c>.
        /// </summary>
        public static (long Durable, long Aged) PlanLegendaryKeyWithdrawal(long uses)
        {
            if (uses <= 0) return (0, 0);
            return (uses / BankLegendaryKeyTable.DurableUses, uses % BankLegendaryKeyTable.DurableUses);
        }

        /// <summary>True when paying out <paramref name="uses"/> creates no more than MaxWithdrawItems keys (the cap counts keys, not uses).</summary>
        public static bool LegendaryKeyWithdrawalWithinItemCap(long uses)
        {
            var (durable, aged) = PlanLegendaryKeyWithdrawal(uses);
            return durable + aged <= MaxWithdrawItems;
        }

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

        /// <summary>
        /// This ACCOUNT's banked pyreals, shared by every character on it. Backed by the account_bank
        /// row rather than by this character's biota (see AccountBankManager); PropertyInt64.
        /// BankedPyreals (9004) is now only the legacy value the login fold drains.
        ///
        /// DELIBERATELY GETTER-ONLY. A setter would be a read-modify-write in C# over a row two
        /// characters on one account can briefly both hold open, and a lost update on this balance is
        /// duplicated or destroyed currency. Every mutation goes through
        /// <see cref="TryAdjustBankedPyreals"/>, which lets the database apply and adjudicate the delta
        /// in one guarded statement.
        ///
        /// A null <see cref="Account"/> reads as 0 and is logged once per character. That is a display
        /// answer, not an authorisation: no path mutates the balance without first taking the account
        /// id through <see cref="TryGetBankAccountId"/>, which refuses outright.
        /// </summary>
        public long BankedPyreals
        {
            get
            {
                var account = Account;

                if (account == null)
                {
                    LogMissingBankAccount("read the banked pyreal balance");
                    return 0;
                }

                return AccountBankManager.GetBalance(account.AccountId);
            }
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
        /// Applies a signed delta to one of the THREE PER-CHARACTER banked balances - luminance,
        /// legendary keys, promissory notes - atomically under <see cref="bankBalanceLock"/>, and
        /// returns the new balance. Those three are still plain PropertyInt64 values on this
        /// character's biota, so the read-modify-write here must be serialized: a player's own
        /// deposit/withdraw/spend run on that player's thread, but a transfer credits a balance from
        /// the *sender's* thread, and a lost update interleaved with a withdraw would dupe currency.
        ///
        /// PYREALS ARE NOT ONE OF THEM AND THIS METHOD REFUSES THEM. They live in the account-wide
        /// account_bank row, where the database applies and adjudicates each delta; routing one through
        /// here would silently write a per-character 9004 property that nothing reads, so the player's
        /// money would appear to vanish. The refusal is a throw rather than a logged no-op precisely
        /// because a no-op is what that bug looks like: every pyreal path was converted to
        /// <see cref="TryAdjustBankedPyreals"/>, so this can only be reached by a new one that forgot,
        /// and it should be impossible to miss.
        /// </summary>
        private long ModifyBankBalance(PropertyInt64 prop, long delta)
        {
            if (prop == PropertyInt64.BankedPyreals)
                throw new InvalidOperationException("Banked pyreals are account-wide and live in account_bank. Use TryAdjustBankedPyreals, which lets the database adjudicate the delta - ModifyBankBalance would write a per-character 9004 property that nothing reads.");

            lock (bankBalanceLock)
            {
                var updated = SaturatingAdd(GetProperty(prop) ?? 0, delta);
                SetProperty(prop, updated);
                return updated;
            }
        }

        /// <summary>Set once a null <see cref="Account"/> has been reported, so the log carries one line per character rather than one per inventory move.</summary>
        private bool loggedMissingBankAccount;

        private void LogMissingBankAccount(string operation)
        {
            if (loggedMissingBankAccount)
                return;

            loggedMissingBankAccount = true;

            log.Error($"[BANK] {Name} (0x{Guid.Full:X8}) has no Account, so it cannot {operation}. Banked pyreals are keyed by account id; every pyreal path for this character will refuse until it relogs.");
        }

        /// <summary>
        /// The account id every pyreal mutation is keyed by, or FALSE having already told the player
        /// why not. <see cref="Account"/> is populated from the auth database in the Player constructor
        /// (Player.cs) and can come back null, so this guard runs BEFORE anything is taken from the
        /// player - never after, or a deposit would destroy coin it could not credit.
        /// </summary>
        private bool TryGetBankAccountId(out uint accountId)
        {
            var account = Account;

            if (account == null)
            {
                accountId = 0;

                LogMissingBankAccount("move banked pyreals");
                BankMsg(BankUnavailableMessage);

                return false;
            }

            accountId = account.AccountId;
            return true;
        }

        /// <summary>
        /// Applies a signed delta to this account's banked pyreals and returns which of the four
        /// outcomes happened. THE ONLY WAY BANKED PYREALS MOVE.
        ///
        /// <paramref name="operation"/> names the caller ("deposit coin", "withdraw notes", ...) and is
        /// used for one thing: the LEDGER STATE UNKNOWN line written here on
        /// <see cref="AccountBankAdjustResult.Failed"/>, so that every site logs it in the same shape
        /// without every site having to remember to. Callers still decide which WAY to fail - a deposit
        /// and a withdraw resolve an unknown in opposite directions - but none of them has to write the
        /// marker itself.
        ///
        /// <paramref name="newBalance"/> is meaningful ONLY for
        /// <see cref="AccountBankAdjustResult.Applied"/>.
        /// </summary>
        private AccountBankAdjustResult TryAdjustBankedPyreals(long delta, string operation, out long newBalance)
        {
            newBalance = 0;

            var account = Account;

            if (account == null)
            {
                // Reachable only from a path that skipped TryGetBankAccountId. Refused is the truthful
                // answer - nothing was attempted, so nothing moved - and the log says which path.
                LogMissingBankAccount($"apply a banked pyreal delta of {delta} ({operation})");
                return AccountBankAdjustResult.Refused;
            }

            var result = AccountBankManager.TryAdjust(account.AccountId, delta, out newBalance);

            if (result == AccountBankAdjustResult.Failed)
                log.Error($"[BANK] LEDGER STATE UNKNOWN: account {account.AccountId}, delta {delta}, operation {operation}, character {Name} (0x{Guid.Full:X8}). The adjustment neither committed nor provably failed - reconcile account_bank against this line.");

            return result;
        }

        /// <summary>
        /// The public face of <see cref="TryAdjustBankedPyreals"/>, for callers outside this file that
        /// need all four outcomes without naming the enum.
        ///
        /// The two booleans encode the four outcomes without collapsing any pair, which is the whole
        /// point - reading Failed as Refused is the bug the enum exists to prevent:
        ///
        ///   Applied              returns TRUE,  stateUnknown FALSE. newBalance is the new balance.
        ///   AppliedCountUnknown  returns TRUE,  stateUnknown TRUE.  The pyreals moved; newBalance is 0
        ///                                                           and is not a balance.
        ///   Refused              returns FALSE, stateUnknown FALSE. The balance PROVABLY did not move.
        ///   Failed               returns FALSE, stateUnknown TRUE.  Nobody knows whether it moved. The
        ///                                                           FALSE here is "do not proceed as
        ///                                                           though it succeeded", NOT "nothing
        ///                                                           happened" - branch on stateUnknown
        ///                                                           before you unwind anything.
        /// </summary>
        public bool TryModifyBankedPyreals(long delta, out long newBalance, out bool stateUnknown)
        {
            var result = TryAdjustBankedPyreals(delta, "external caller", out newBalance);

            stateUnknown = result == AccountBankAdjustResult.AppliedCountUnknown || result == AccountBankAdjustResult.Failed;

            return result == AccountBankAdjustResult.Applied || result == AccountBankAdjustResult.AppliedCountUnknown;
        }

        /// <summary>
        /// The balance to show after a credit or debit whose resulting balance the database could not
        /// report (<see cref="AccountBankAdjustResult.AppliedCountUnknown"/>). The manager has dropped
        /// its cached entry by then, so this re-reads rather than repeating a number known to be stale.
        /// </summary>
        private long BalanceAfterUnknownCount() => BankedPyreals;

        /// <summary>
        /// a + b, pinned to the long range instead of wrapping. A banked balance is a plain signed 64-bit
        /// property with no ceiling of its own, so a large enough credit - repeated /mylum grants on a test
        /// shard, most obviously - could roll a balance past long.MaxValue and land it NEGATIVE, turning a
        /// full wallet into a debt no spend could clear. Saturating is the right shape for that: the balance
        /// stops climbing rather than inverting, and every spend path still compares against a sane number.
        /// </summary>
        private static long SaturatingAdd(long a, long b)
        {
            if (b > 0 && a > long.MaxValue - b)
                return long.MaxValue;

            if (b < 0 && a < long.MinValue - b)
                return long.MinValue;

            return a + b;
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

        /// <summary>
        /// Sent whenever a pyreal path finds <see cref="Account"/> null. It says "relog" rather than
        /// naming the cause because the cause is not the player's: the account row failed to load in
        /// the Player constructor, and a fresh login is the only thing that re-runs that.
        /// </summary>
        private const string BankUnavailableMessage = "Your bank is unavailable right now. Log out and back in, then try again.";

        /// <summary>
        /// Sent when the guarded UPDATE PROVABLY refused a debit. Since every debit path checks
        /// affordability first, reaching this means the cached balance and the ledger disagree - so the
        /// message points the player at /bank, which re-reads.
        /// </summary>
        private const string BankDebitRefusedMessage = "The bank refused the debit - your balance may have changed. Check /bank and try again.";

        /// <summary>
        /// Sent when a purchase-side debit came back with the state unknown. The goods are NOT handed
        /// over: the pyreals may already be gone, and handing over goods as well would turn an unknown
        /// into a certain loss for the shard rather than for the player.
        /// </summary>
        private const string BankStateUnknownPurchaseMessage = "The bank could not confirm your balance, so the purchase was cancelled. Try again in a moment.";

        /// <summary>
        /// Sent when a deposit credit did not land. The items it would have paid for are handed back,
        /// so "nothing was taken" is literally true in the refused case and the honest thing to say in
        /// the unknown one - see <see cref="TryCommitPyrealDeposit"/> for why the unknown case still
        /// returns them.
        /// </summary>
        private const string BankDepositNotCreditedMessage = "The bank could not accept that deposit, so nothing was taken. Try again in a moment.";

        /// <summary>
        /// Sent when the SOURCE side of a transfer came back with the state unknown. Nothing has been
        /// credited at that point, so the transfer is abandoned rather than completed on a guess.
        /// </summary>
        private const string BankTransferDebitUnknownMessage = "The bank could not confirm the debit, so the transfer was cancelled. Check /bank before retrying.";

        private void BankMsg(string message)
        {
            Session.Network.EnqueueSend(new GameMessageSystemChat($"[BANK] {message}", ChatMessageType.System));
        }

        #region Login and logout

        /// <summary>
        /// Warms this account's pooled pyreal balance at login.
        ///
        /// MUST RUN BEFORE SendSelf(). GameEventPlayerDescription snapshots GetSpendableCoinValue,
        /// which reads BankedPyreals, and that snapshot is what the client shows and what it uses to
        /// decide whether a purchase is worth sending at all. A cold cache there would put the whole
        /// session's first coin figure at 0 until something else moved it.
        ///
        /// Called from PlayerEnterWorld immediately after InitQuestStamps, which is the same position
        /// and the same reasoning: account-wide state read once at login.
        /// </summary>
        public void InitAccountBank()
        {
            if (!TryGetBankAccountIdQuietly(out var accountId))
                return;

            TryFoldLegacyBankedPyreals(accountId);

            if (!AccountBankManager.TryGetBalance(accountId, out _))
            {
                // Not fatal. The entry is left unloaded, so the next read retries, and every mutation
                // is adjudicated by the row rather than by the cache - the visible cost is a coin
                // figure of 0 until a read succeeds.
                log.Error($"[BANK] {Name} (0x{Guid.Full:X8}) logged in without a readable banked pyreal balance for account {accountId}. It will be shown as 0 until a later read succeeds.");
            }
        }

        /// <summary>
        /// Folds this character's legacy per-character banked pyreals (PropertyInt64.BankedPyreals,
        /// 9004) into the account pool, exactly once, ever.
        ///
        /// WHY THIS EXISTS ALONGSIDE THE BULK SQL. Database/Updates/Shard/2026-09-01-00-Add-Account-Bank.sql
        /// does the same job in one pass, but the boot patcher records a FAILED script as applied
        /// (Program_DbUpdates.cs), so the bulk fold can silently never run and never be retried. This is
        /// the safety net for that, and it is also what makes the deploy order irrelevant: whichever
        /// runs first, account_bank_fold's character_Guid primary key arbitrates and the loser folds
        /// nothing.
        ///
        /// THE STEP ORDER IS THE WHOLE CORRECTNESS ARGUMENT. Claim, then credit, then remove. Removing
        /// the property before the credit lands would destroy the balance; crediting before the claim
        /// would let two racing folds both credit. The claim row also records the amount, so a crash
        /// between the claim and the credit leaves a repairable record of what was owed rather than a
        /// silent loss - which is the one residual risk here, and is why the amount column exists.
        ///
        /// A FALSE CLAIM IS NOT TAKEN AS PROOF. TryClaimFold returns false for a duplicate key and for
        /// a database failure alike, and acting on the first response (drop the stale property) when
        /// the truth was the second destroys a balance nothing has credited. So a false is confirmed
        /// against HasFold, and an unanswerable HasFold leaves the property alone for the next login.
        /// </summary>
        private void TryFoldLegacyBankedPyreals(uint accountId)
        {
            var legacy = GetProperty(PropertyInt64.BankedPyreals) ?? 0;

            // A zero row carries no money and the bulk migration drops it as noise. A NEGATIVE one is
            // left strictly alone: it cannot be credited, no path produces one, and it is evidence.
            if (legacy <= 0)
                return;

            if (!AccountBankManager.TryClaimFold(Guid.Full, accountId, legacy))
            {
                var alreadyFolded = AccountBankManager.HasFold(Guid.Full);

                if (alreadyFolded != true)
                {
                    log.Error($"[BANK] {Name} (0x{Guid.Full:X8}) still holds {legacy:N0} legacy banked pyreals and the fold could not be claimed or confirmed. The property is being LEFT IN PLACE so a later login can retry - it has not been credited to account {accountId}.");
                    return;
                }

                // Somebody else folded this character - the bulk SQL, or an earlier session that
                // credited the pool and then failed to persist the removal. The pool already holds
                // the value, so the property is stale and must go, or every login re-reads it.
                log.Info($"[BANK] {Name} (0x{Guid.Full:X8}) was already folded into account {accountId}; dropping the stale legacy balance of {legacy:N0}.");

                RemoveProperty(PropertyInt64.BankedPyreals);
                SaveBiotaToDatabase();
                return;
            }

            var credit = AccountBankManager.TryAdjust(accountId, legacy, out _);

            if (credit == AccountBankAdjustResult.Refused || credit == AccountBankAdjustResult.Failed)
            {
                // The claim row is written and names the amount, so this is recoverable by hand. The
                // property stays put, which is the safe direction: it is not double-credited, because
                // the claim row now blocks every other fold path from touching this character.
                log.Error($"[BANK] {Name} (0x{Guid.Full:X8}): the fold of {legacy:N0} legacy banked pyreals into account {accountId} came back {credit} AFTER the claim row was written. The pool was not credited and no other path will retry - repair from account_bank_fold, where the amount is recorded.");
                return;
            }

            RemoveProperty(PropertyInt64.BankedPyreals);
            SaveBiotaToDatabase();

            log.Info($"[BANK] folded {legacy:N0} legacy banked pyreals from {Name} (0x{Guid.Full:X8}) into account {accountId}.");
        }

        /// <summary>
        /// Drops this account's cached balance at logout, but ONLY when no other character on the
        /// account is still online.
        ///
        /// The check is not paranoia about a supported case: one session per account is enforced at
        /// login, but the in-world guard in CharacterHandler is per-CHARACTER, so a logging-out player
        /// and a newly entering same-account player can briefly coexist. Releasing unconditionally in
        /// that window would drop the entry the survivor is reading through.
        ///
        /// Call AFTER PlayerManager.SwitchPlayerFromOnlineToOffline, which is what makes this character
        /// stop counting as online in the snapshot below.
        /// </summary>
        public void ReleaseAccountBank()
        {
            if (!TryGetBankAccountIdQuietly(out var accountId))
                return;

            foreach (var accountPlayer in PlayerManager.GetAccountPlayersSnapshot(accountId))
            {
                // playerAccounts holds every character on the account, online and offline alike - the
                // logout switch replaces this one's entry with an OfflinePlayer rather than removing
                // it - so the type is what distinguishes them, not membership.
                if (accountPlayer is Player)
                    return;
            }

            AccountBankManager.Release(accountId);
        }

        /// <summary>
        /// <see cref="TryGetBankAccountId"/> without the player-facing message, for the login and
        /// logout hooks. Those two run outside any player action, and a chat line there would arrive
        /// with no context - at logout there may be no session left to send it to at all.
        /// </summary>
        private bool TryGetBankAccountIdQuietly(out uint accountId)
        {
            var account = Account;

            if (account == null)
            {
                accountId = 0;
                LogMissingBankAccount("resolve its banked pyreal pool");
                return false;
            }

            accountId = account.AccountId;
            return true;
        }

        #endregion

        #region Deposit

        /// <summary>
        /// Credits <paramref name="deposited"/> pyreals to the account pool and only then destroys the
        /// items that paid for it. Returns FALSE, having handed those items back, if the credit did not
        /// land.
        ///
        /// CREDIT FIRST, DESTROY SECOND. The old order could not get this wrong, because a SetProperty
        /// on the character's own biota cannot fail; a guarded UPDATE against a shared row can, and
        /// destroying the coin before knowing the credit landed would turn a database hiccup into a
        /// player's money simply ceasing to exist. The items are only detached at this point, never
        /// destroyed, which is what makes handing them back possible.
        ///
        /// ON Failed THE ITEMS ARE STILL HANDED BACK, and that is a considered choice rather than an
        /// oversight: the credit may have landed, so returning them can duplicate the value. It follows
        /// the vault's deposit-side rule (AccountVaultStore.TryDepositBatch's LEDGER STATE UNKNOWN arm) for the same reason - a
        /// deposit leaves the goods in the player's hands, so keeping them when the credit DID land is
        /// a dupe visible beside a LEDGER STATE UNKNOWN line an operator can reconcile, while
        /// destroying them when it did NOT is an unrecoverable loss. A visible dupe beats a silent
        /// loss. The withdraw path resolves the same unknown the opposite way, and for the mirrored
        /// reason.
        /// </summary>
        private bool TryCommitPyrealDeposit(long deposited, List<WorldObject> removed, string operation, out long balance)
        {
            balance = 0;

            var credit = TryAdjustBankedPyreals(deposited, operation, out var newBalance);

            if (credit == AccountBankAdjustResult.Applied)
            {
                balance = newBalance;
            }
            else if (credit == AccountBankAdjustResult.AppliedCountUnknown)
            {
                // The pyreals ARE in the pool; only the resulting balance is unknown. The manager has
                // already dropped its cached entry, so this re-read is a fresh one rather than the
                // stale number it just discarded.
                balance = BalanceAfterUnknownCount();
            }
            else
            {
                if (credit == AccountBankAdjustResult.Refused)
                    log.Error($"[BANK] {Name} (0x{Guid.Full:X8}): the pool REFUSED a credit of {deposited:N0} pyreals ({operation}). Nothing was destroyed and the items are being returned.");

                foreach (var wo in removed)
                {
                    // The slots these came out of were freed moments ago, so the re-add is expected to
                    // succeed. If it somehow does not, the object is detached and parented by nothing,
                    // and leaving it that way leaks it into no container at all - so it is destroyed
                    // and the exact amount is logged, because that line is then the only record of what
                    // the player is owed.
                    if (TryCreateInInventoryWithNetworking(wo))
                        continue;

                    log.Error($"[BANK] {Name} (0x{Guid.Full:X8}): could not return {wo.Name} (0x{wo.Guid.Full:X8}, value {wo.Value ?? 0:N0}) after an uncredited {operation}. It is being destroyed; the player is owed that value.");
                    wo.Destroy();
                }

                BankMsg(BankDepositNotCreditedMessage);
                return false;
            }

            foreach (var wo in removed)
            {
                Session.Network.EnqueueSend(new GameMessageInventoryRemoveObject(wo));
                wo.Destroy();
            }

            return true;
        }

        /// <summary>
        /// Deposits pyreals from inventory into the bank. Credits only what is actually removed.
        /// amount &lt;= 0 means "all".
        /// </summary>
        public long DepositPyreals(long amount = -1, bool suppressChat = false)
        {
            // Before anything is taken out of the pack, not after: a deposit that removed the coin and
            // then found it had nowhere to credit it would destroy it.
            if (!TryGetBankAccountId(out _))
                return 0;

            long deposited = 0;
            var removed = new List<WorldObject>();

            // The leftover of a partial stack, built during the sweep but not handed back until the
            // credit has landed. See the partial branch below.
            WorldObject pendingRemainder = null;

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
                    // Take a partial stack: remove the whole stack and build the remainder, but do NOT
                    // put the remainder back yet. The credit can now fail, and if it does the whole
                    // original stack goes back to the player - so a remainder already sitting in the
                    // pack beside it would be a straight duplicate of the part that was not deposited.
                    // It is added below, after the credit is known to have landed.
                    var take = (int)amount;
                    if (TryRemoveFromInventory(coin.Guid, out var whole))
                    {
                        removed.Add(whole);
                        deposited += take;

                        pendingRemainder = WorldObjectFactory.CreateNewWorldObject(coinStackWcid);
                        pendingRemainder.SetStackSize(stackSize - take);

                        amount = 0;
                    }
                }
            }

            if (deposited <= 0)
            {
                pendingRemainder?.Destroy();

                if (!suppressChat)
                    BankMsg("No pyreals found to deposit.");

                return 0;
            }

            if (!TryCommitPyrealDeposit(deposited, removed, "deposit coin", out var balance))
            {
                // The whole original stack has gone back to the pack, so the split it was going to be
                // divided into is void. Handing the remainder over as well would duplicate it.
                pendingRemainder?.Destroy();
                return 0;
            }

            if (pendingRemainder != null)
            {
                var remainderValue = pendingRemainder.StackSize ?? 0;

                if (!TryCreateInInventoryWithNetworking(pendingRemainder))
                {
                    // Should not happen: the slot was freed moments ago. Credit it rather than lose it,
                    // exactly as this branch always did - the only change is that the credit is now a
                    // second guarded delta rather than part of the first, because the first has already
                    // committed by the time we get here.
                    log.Error($"[BANK] {Name} DepositPyreals could not re-add remainder of {remainderValue}; crediting it to avoid loss.");
                    pendingRemainder.Destroy();

                    var remainderCredit = TryAdjustBankedPyreals(remainderValue, "deposit coin remainder", out var remainderBalance);

                    if (remainderCredit == AccountBankAdjustResult.Applied)
                    {
                        deposited += remainderValue;
                        balance = remainderBalance;
                    }
                    else if (remainderCredit == AccountBankAdjustResult.AppliedCountUnknown)
                    {
                        deposited += remainderValue;
                        balance = BalanceAfterUnknownCount();
                    }
                    else
                    {
                        log.Error($"[BANK] {Name} DepositPyreals could not credit the {remainderValue:N0} pyreal remainder either. That value is LOST and the player is owed it.");
                    }
                }
            }

            UpdateCoinValue();
            RushNextPlayerSave(5);

            if (!suppressChat)
                BankMsg($"Deposited {deposited:N0} pyreals. Balance: {balance:N0}");

            return deposited;
        }

        /// <summary>
        /// Deposits the pyreal value of all trade notes (ItemType.PromissoryNote) in inventory.
        /// item.Value is the full stack value, so it is credited exactly once.
        /// </summary>
        public long DepositTradeNotes(bool suppressChat = false)
        {
            if (!TryGetBankAccountId(out _))
                return 0;

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

            if (deposited <= 0)
            {
                if (!suppressChat)
                    BankMsg("No trade notes found to deposit.");

                return 0;
            }

            if (!TryCommitPyrealDeposit(deposited, removed, "deposit notes", out var balance))
                return 0;

            UpdateCoinValue();
            RushNextPlayerSave(5);

            if (!suppressChat)
                BankMsg($"Deposited {deposited:N0} pyreals of trade notes. Balance: {balance:N0}");

            return deposited;
        }

        /// <summary>
        /// Credits ONE already-detached trade note's face value to this player's banked pyreals and
        /// destroys it, returning the amount credited (0 means nothing was credited and the caller
        /// still owns the note).
        ///
        /// Why this lives in Player_Bank rather than at its only call site
        /// (PersonalVendor.DepositItems): <see cref="ModifyBankBalance"/> and <see cref="BankMsg"/>
        /// are private to this file, and a second copy of the credit sequence in the vendor would be
        /// a second place the saturating add, the coin refresh and the rush save all have to stay
        /// correct. The two routes a trade note can reach the bank by - /bank deposit notes
        /// (<see cref="DepositTradeNotes"/>) and a mule deposit - now share this one credit.
        ///
        /// note.Value is the FULL stack value, exactly as DepositTradeNotes reads it, so it is
        /// credited once and never multiplied by StackSize.
        ///
        /// It does NOT remove the note from inventory, and must not: the sell path
        /// (Player_Commerce.HandleActionSellItem) has already detached it and flushed that state to
        /// the shard before ProcessItemsForPurchase runs, so the only step left is the destroy. That
        /// is also why a note that is somehow STILL parented is refused here rather than destroyed -
        /// destroying an object that another container still references is the one outcome worse than
        /// handing the note back.
        ///
        /// A note worth 0 or less is refused for the same reason: the caller can return it, but
        /// nothing can undo a destroy that credited nothing.
        /// </summary>
        public long BankTradeNote(WorldObject note)
        {
            if (note == null || note.ItemType != ItemType.PromissoryNote)
                return 0;

            var value = note.Value ?? 0;

            if (value <= 0)
                return 0;

            if (note.ContainerId != null || note.WielderId != null)
                return 0;

            // Account is checked here rather than through TryGetBankAccountId because that helper
            // reports through BankMsg, which dereferences Session unconditionally - and this method is
            // deliberately callable from a context that has none (see the remarks above). Returning 0
            // is the contract for "not credited", and the caller still owns the note.
            if (Account == null)
            {
                LogMissingBankAccount("credit a trade note");
                return 0;
            }

            var credit = TryAdjustBankedPyreals(value, "bank trade note", out var newBalance);

            // Not credited, so the note is NOT destroyed and the caller keeps it. On Failed the credit
            // may have landed and the player may keep both - the deposit-side rule in
            // TryCommitPyrealDeposit, for the same reason: a visible dupe beside a LEDGER STATE UNKNOWN
            // line beats destroying a note nothing paid for.
            if (credit == AccountBankAdjustResult.Refused || credit == AccountBankAdjustResult.Failed)
            {
                log.Error($"[BANK] {Name} (0x{Guid.Full:X8}): could not credit a trade note worth {value:N0} pyreals ({credit}). The note was NOT destroyed.");
                return 0;
            }

            var balance = credit == AccountBankAdjustResult.Applied ? newBalance : BalanceAfterUnknownCount();

            // Session is non-null for every real sell transaction (the handler runs off a session
            // message), but the null-conditional keeps this callable from a context that has no
            // network layer rather than making the credit itself depend on one.
            Session?.Network?.EnqueueSend(new GameMessageInventoryRemoveObject(note));

            note.Destroy();

            UpdateCoinValue();
            RushNextPlayerSave(5);

            if (Session != null)
                BankMsg($"Deposited {value:N0} pyreals of trade notes. Balance: {balance:N0}");

            return value;
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
        /// Deposits the pyreal face value of every bankable pea (lead / iron / copper / silver / gold /
        /// pyreal) in inventory.
        /// Credits only what is actually removed. Peas fold into the pyreal balance - there is no separate
        /// pea balance and no way to withdraw peas back out.
        /// </summary>
        public long DepositPeas(bool suppressChat = false)
        {
            if (!TryGetBankAccountId(out _))
                return 0;

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

            if (deposited <= 0)
            {
                if (!suppressChat)
                    BankMsg("No peas found to deposit.");

                return 0;
            }

            if (!TryCommitPyrealDeposit(deposited, removed, "deposit peas", out var balance))
                return 0;

            UpdateCoinValue();
            RushNextPlayerSave(5);

            if (!suppressChat)
                BankMsg($"Deposited {peas:N0} pea(s) worth {deposited:N0} pyreals. Balance: {balance:N0}");

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
        /// <remarks>
        /// Saturating, because both terms are unbounded longs: available Luminance is capped by
        /// MaximumLuminance, but the bank is not, and a bank near long.MaxValue would make a plain sum wrap
        /// NEGATIVE - which reads as "you cannot afford this" on the richest possible character.
        /// </remarks>
        public long GetSpendableLuminance() => SaturatingAdd(AvailableLuminance ?? 0, BankedLuminance);

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
            if (GetSpendableLuminance() < amount)
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
        /// Spends <paramref name="amount"/> pyreals at an NPC, drawing from pack coin first and then the
        /// uncapped bank. Returns FALSE without spending anything if the combined total is insufficient (or
        /// if <paramref name="amount"/> exceeds what a coin-stack spend can express). This is the single
        /// pyreal-spend entry point for NPC content that takes coin from pack-then-bank (e.g. the marketplace
        /// spell tutor) - it routes through the same always-on bank shop hook as vendor purchases
        /// (<see cref="SpendCurrency"/>), which already spends inventory coin first and falls back to
        /// BankedPyreals for the remainder.
        /// </summary>
        public bool TrySpendPyrealsIncludingBank(long amount)
        {
            if (amount <= 0)
                return true;

            if (amount > uint.MaxValue)
                return false;

            if ((CoinValue ?? 0) + BankedPyreals < amount)
                return false;

            return SpendCurrency(coinStackWcid, (uint)amount, true) != null;
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

            foreach (var kvp in BankLegendaryKeyTable.Uses)
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

            // Peas are destroyed by this sweep and are also the Splitting Tool's input (see BankablePeas), so
            // call it out separately even though the value is already folded into the pyreal balance.
            var peaValue = DepositPeas(suppressChat: true);

            DepositLuminance(-1, suppressChat: true);
            DepositLegendaryKeys(suppressChat: true);
            DepositPromissoryNotes(suppressChat: true);

            var peas = peaValue > 0 ? $" (including {peaValue:N0} from peas)" : "";
            BankMsg($"Deposited all. Pyreals: {BankedPyreals:N0}{peas} | Luminance: {BankedLuminance:N0} | Keys: {BankedLegendaryKeys:N0} | Promissory Notes: {BankedPromissoryNotes:N0}");
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
        /// Puts <paramref name="shortfall"/> pyreals back after a withdraw that could not build
        /// everything it debited for.
        ///
        /// A COMPENSATING POSITIVE DELTA, never a smaller debit. The debit has already committed; the
        /// only way back is to add the difference. Re-debiting "the right amount" instead would apply
        /// two debits, and adjusting the original is not something a committed row offers.
        /// </summary>
        private void RefundWithdrawShortfall(long shortfall, string operation)
        {
            if (shortfall <= 0)
                return;

            var refund = TryAdjustBankedPyreals(shortfall, $"{operation} shortfall refund", out _);

            if (refund == AccountBankAdjustResult.Applied || refund == AccountBankAdjustResult.AppliedCountUnknown)
                return;

            // The debit landed and the refund did not, so the player is genuinely short by this much.
            // Nothing here can fix it; the line is what makes it repairable by hand.
            log.Error($"[BANK] {Name} (0x{Guid.Full:X8}): a {operation} debited {shortfall:N0} pyreals more than it delivered and the refund came back {refund}. The player is owed {shortfall:N0} pyreals.");
        }

        /// <summary>
        /// Withdraws banked pyreals into inventory: 250k MMD trade notes for the bulk, coin stacks for the
        /// remainder. Debits the full amount up front and refunds whatever the pack had no room for.
        ///
        /// DEBIT BEFORE DELIVERY, which is the reverse of how this read before banked pyreals moved to a
        /// row that can refuse. Creating the items first and debiting afterwards was safe only while the
        /// debit could not fail; against a guarded UPDATE it is a dupe, because a refused or failed
        /// debit leaves the player holding items nothing paid for. So the whole amount is taken first,
        /// and a shortfall is corrected by a compensating positive delta - see
        /// <see cref="RefundWithdrawShortfall"/>. Same ordering, and the same reasoning, as
        /// AccountVaultStore.WithdrawFromLedger.
        /// </summary>
        public void WithdrawPyreals(long amount)
        {
            if (amount <= 0) { BankMsg("Amount must be greater than zero."); return; }
            if (!TryGetBankAccountId(out _)) return;
            if (amount > BankedPyreals) { BankMsg($"Insufficient banked pyreals. You have {BankedPyreals:N0}."); return; }

            var debit = TryAdjustBankedPyreals(-amount, "withdraw pyreals", out _);

            // Refused is the only outcome that PROVES the balance did not move, so it is the only one
            // that may unwind by simply refusing - nothing has been created yet.
            if (debit == AccountBankAdjustResult.Refused)
            {
                BankMsg(BankDebitRefusedMessage);
                return;
            }

            // Failed: TryAdjustBankedPyreals has already written the LEDGER STATE UNKNOWN line. This
            // PROCEEDS rather than refusing, mirroring the vault's withdraw side - the pyreals may
            // already be gone and the player has nothing to show for it, so handing over the items and
            // leaving a marker is the recoverable direction. Refusing here would be a silent loss.

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

            if (created < amount)
                RefundWithdrawShortfall(amount - created, "withdraw pyreals");

            if (created <= 0)
            {
                // Nothing reached the pack, so the whole debit has just been refunded and no biota
                // changed: an inventory walk and a rush save would both be pure work. CoinValue is
                // ephemeral and never dirties the biota, so it has nothing to persist either.
                //
                // The spendable figure is still pushed, and only that. It costs a cached read, and it
                // covers the one case that is not a no-op - a refund that did NOT land leaves the pool
                // genuinely lower, and without this the client would keep showing the old total until
                // some unrelated event moved it. SendSpendableCoinValue suppresses itself when the
                // number has not changed, which is every ordinary trip through here.
                SendSpendableCoinValue();

                BankMsg("Could not withdraw - check your pack space.");
                return;
            }

            // Below the guard on purpose. Reaching here means TryCreateStackInInventory placed real
            // objects in the pack, so inventory biotas were created and the rush save has something to
            // persist - which is exactly the condition invariant 6 asks for.
            UpdateCoinValue();
            RushNextPlayerSave(5);

            // Read after both deltas so the figure shown is the one the pool actually holds.
            var balance = BankedPyreals;

            if (created == amount)
                BankMsg($"Withdrew {created:N0} pyreals. Balance: {balance:N0}");
            else
                BankMsg($"Withdrew {created:N0} pyreals (partial - out of pack space). Balance: {balance:N0}");
        }

        /// <summary>
        /// Withdraws banked pyreals as trade notes of a specific denomination value, emitted as STACKS rather
        /// than as single notes: one full stack per pack slot until the requested count is met, then a
        /// remainder stack. Debits the full cost up front and refunds the notes the pack had no room for.
        ///
        /// Same inversion, and the same reasoning, as <see cref="WithdrawPyreals"/>: the debit runs
        /// before anything is created, and a shortfall is corrected by a compensating positive delta
        /// rather than by debiting a different amount.
        /// </summary>
        public void WithdrawTradeNotes(long denomValue, long count)
        {
            if (count <= 0) { BankMsg("Count must be greater than zero."); return; }

            uint wcid = 0;
            foreach (var d in TradeNoteDenoms)
                if (d.value == denomValue) { wcid = d.wcid; break; }

            if (wcid == 0) { BankMsg("Unknown trade note denomination."); return; }

            if (!TryGetBankAccountId(out _)) return;

            var totalCost = denomValue * count;
            if (totalCost > BankedPyreals) { BankMsg($"Insufficient banked pyreals. Need {totalCost:N0}, have {BankedPyreals:N0}."); return; }
            if (count > MaxWithdrawItems) { BankMsg($"Cannot withdraw more than {MaxWithdrawItems:N0} notes at once."); return; }

            var debit = TryAdjustBankedPyreals(-totalCost, "withdraw notes", out _);

            if (debit == AccountBankAdjustResult.Refused)
            {
                BankMsg(BankDebitRefusedMessage);
                return;
            }

            // Failed proceeds, and the LEDGER STATE UNKNOWN line is already written. See WithdrawPyreals.

            long createdNotes = 0;
            long remaining = count;

            while (remaining > 0)
            {
                var notes = TryCreateStackInInventory(wcid, remaining);
                if (notes <= 0) break;   // weenie missing, or the pack is full - keep whatever already landed
                createdNotes += notes;
                remaining -= notes;
            }

            if (createdNotes < count)
                RefundWithdrawShortfall((count - createdNotes) * denomValue, "withdraw notes");

            if (createdNotes <= 0)
            {
                // Same reasoning as WithdrawPyreals: a fully refunded debit changed no biota, so the
                // coin walk and the rush save have nothing to do, and only the spendable figure is
                // pushed - for the case where the refund itself did not land.
                SendSpendableCoinValue();

                BankMsg("Could not withdraw - check your pack space.");
                return;
            }

            // Below the guard: notes actually reached the pack, so inventory biotas exist to persist.
            UpdateCoinValue();
            RushNextPlayerSave(5);

            var balance = BankedPyreals;

            if (createdNotes == count)
                BankMsg($"Withdrew {createdNotes:N0} trade note(s) worth {createdNotes * denomValue:N0} pyreals. Balance: {balance:N0}");
            else
                BankMsg($"Withdrew {createdNotes:N0} of {count:N0} trade note(s) worth {createdNotes * denomValue:N0} pyreals (partial - out of pack space). Balance: {balance:N0}");
        }

        /// <summary>Creates one key of the given wcid in the player's pack; false when it could not be made or placed.</summary>
        private bool TryCreateLegendaryKey(uint wcid)
        {
            var key = WorldObjectFactory.CreateNewWorldObject(wcid);
            if (key == null) return false;
            if (!TryCreateInInventoryWithNetworking(key)) { key.Destroy(); return false; }
            return true;
        }

        /// <summary>
        /// The create loop behind <see cref="WithdrawLegendaryKeys"/>, with the creator injected so the partial-failure
        /// accounting is testable. Durables first; aged keys are attempted only if every durable was made. Returns the
        /// uses actually created (the debit) and the per-type counts.
        /// </summary>
        internal static (long Uses, long Durable, long Aged) CreateLegendaryKeys(long amount, Func<uint, bool> tryCreate)
        {
            var (durable, aged) = PlanLegendaryKeyWithdrawal(amount);
            long createdDurable = 0, createdAged = 0;
            for (long i = 0; i < durable; i++)
            {
                if (!tryCreate(BankLegendaryKeyTable.DurableWcid)) break;
                createdDurable++;
            }

            // After a pack-full stop the player keeps the rest of the balance rather than getting loose single-use keys.
            if (createdDurable == durable)
            {
                for (long i = 0; i < aged; i++)
                {
                    if (!tryCreate(BankLegendaryKeyTable.AgedWcid)) break;
                    createdAged++;
                }
            }

            return (createdDurable * BankLegendaryKeyTable.DurableUses + createdAged, createdDurable, createdAged);
        }

        /// <summary>
        /// Withdraws banked legendary keys. <paramref name="amount"/> is in USES: it is paid out as
        /// amount / 10 Durable Legendary Keys (51954, 10 uses each) plus amount % 10 Aged Legendary Keys
        /// (48746, 1 use each), so 15 -> 1 durable + 5 aged. MaxWithdrawItems caps the number of KEYS
        /// created, not the number of uses. Durables are made first; debits only the uses actually created
        /// (a durable counts 10), so a pack-full partial withdrawal stays correct.
        ///
        /// Deliberately NOT stacked, unlike the trade-note and promissory-note paths: the legendary key
        /// weenies are WeenieType.Key (verified against ace_world for the withdrawn 48746 and 51954), so they are not
        /// Stackable and carry no MaxStackSize row at all - SetStackSize would simply no-op on them.
        /// </summary>
        public void WithdrawLegendaryKeys(long amount)
        {
            if (amount <= 0) { BankMsg("Amount must be greater than zero."); return; }
            if (amount > BankedLegendaryKeys) { BankMsg($"Insufficient banked legendary keys. You have {BankedLegendaryKeys:N0}."); return; }

            if (!LegendaryKeyWithdrawalWithinItemCap(amount)) { BankMsg($"Cannot withdraw more than {MaxWithdrawItems:N0} keys at once."); return; }

            var (createdUses, createdDurable, createdAged) = CreateLegendaryKeys(amount, TryCreateLegendaryKey);

            if (createdUses <= 0) { BankMsg("Could not withdraw - check your pack space."); return; }

            var balance = ModifyBankBalance(PropertyInt64.BankedLegendaryKeys, -createdUses);
            RushNextPlayerSave(5);
            BankMsg($"Withdrew {createdUses:N0} use(s) of legendary keys as {createdDurable:N0} durable (10 uses) + {createdAged:N0} aged (1 use). Balance: {balance:N0}");
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

        public bool TransferPyreals(long amount, string target) => TransferBankedPyreals(amount, target);
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

        /// <summary>
        /// Moves pyreals from this ACCOUNT's pool to another account's.
        ///
        /// Kept apart from <see cref="Transfer"/> rather than folded into it, because almost nothing
        /// the shared method does still applies. There is no guid-ordered double lock: the two pools
        /// are separate rows, each adjudicating its own delta, so there is no pair of balances to hold
        /// still. There is no offline branch either - an account's pool is durable whether or not any
        /// of its characters is logged in, so an offline recipient needs no biota save and no global
        /// lock to serialize one.
        ///
        /// The order is debit source, then credit target. If the credit is REFUSED the source is
        /// refunded with a compensating positive delta; if it comes back UNKNOWN it is NOT refunded,
        /// because a refund over a credit that did land creates pyreals out of nothing. That is the
        /// vault's deposit-side rule applied to the receiving end.
        /// </summary>
        private bool TransferBankedPyreals(long amount, string targetName)
        {
            const string label = "pyreals";

            if (amount <= 0) { BankMsg("Transfer amount must be greater than zero."); return false; }
            if (string.IsNullOrWhiteSpace(targetName)) { BankMsg("Specify a target character."); return false; }

            if (!TryGetBankAccountId(out var sourceAccountId))
                return false;

            if (BankedPyreals < amount)
            {
                BankMsg($"Insufficient banked {label} to transfer. You have {BankedPyreals:N0}.");
                return false;
            }

            var target = PlayerManager.FindByName(targetName);
            if (target == null) { BankMsg($"Character '{targetName}' not found."); return false; }

            if (target.Guid == Guid) { BankMsg($"You cannot transfer {label} to yourself."); return false; }

            var targetAccount = target.Account;

            if (targetAccount == null)
            {
                log.Error($"[BANK] {Name} (0x{Guid.Full:X8}) tried to transfer {amount:N0} pyreals to {target.Name} (0x{target.Guid.Full:X8}), which has no Account. There is no pool to credit, so the transfer is refused before anything moves.");
                BankMsg(BankUnavailableMessage);
                return false;
            }

            // BEFORE ANY BALANCE IS TOUCHED. Both characters draw on the same row, so this would debit
            // and credit the same pool - a no-op with two log lines if it worked, and a real loss if
            // the credit half failed. The guid self-check above no longer covers it: two DIFFERENT
            // characters on one account are now the same wallet.
            if (targetAccount.AccountId == sourceAccountId)
            {
                BankMsg($"Your characters already share one pyreal bank - there is nothing to transfer to {target.Name}.");
                return false;
            }

            var debit = TryAdjustBankedPyreals(-amount, $"transfer to {target.Name}", out _);

            if (debit == AccountBankAdjustResult.Refused)
            {
                BankMsg(BankDebitRefusedMessage);
                return false;
            }

            if (debit == AccountBankAdjustResult.Failed)
            {
                // The LEDGER STATE UNKNOWN line is already written. Aborting here is the safe direction:
                // nothing has been credited, so at worst the source is short by an amount the marker
                // names, whereas crediting the target on top of an unknown debit could create pyreals.
                BankMsg(BankTransferDebitUnknownMessage);
                return false;
            }

            var credit = AccountBankManager.TryAdjust(targetAccount.AccountId, amount, out _);

            if (credit == AccountBankAdjustResult.Refused)
            {
                var refund = TryAdjustBankedPyreals(amount, $"transfer refund after a refused credit to {target.Name}", out _);

                if (refund == AccountBankAdjustResult.Applied || refund == AccountBankAdjustResult.AppliedCountUnknown)
                {
                    BankMsg($"Transfer failed - the bank could not credit {target.Name}. Your {amount:N0} pyreals were returned.");
                }
                else
                {
                    log.Error($"[BANK] {Name} (0x{Guid.Full:X8}): a transfer of {amount:N0} pyreals to {target.Name} was refused by the target pool AND the refund to account {sourceAccountId} came back {refund}. The source is short {amount:N0} pyreals and is owed them.");
                    BankMsg($"Transfer failed - the bank could not credit {target.Name}. Your {amount:N0} pyreals were returned.");
                }

                SendSpendableCoinValue();
                return false;
            }

            if (credit == AccountBankAdjustResult.Failed)
            {
                // NOT REFUNDED, deliberately. The credit may have landed; refunding on top of it would
                // mint the amount. The source is debited either way, so the marker is what an operator
                // reconciles from - and the player is told not to retry, because a retry over a credit
                // that did land doubles the loss.
                log.Error($"[BANK] LEDGER STATE UNKNOWN: account {targetAccount.AccountId}, delta {amount}, operation transfer credit from {Name} (0x{Guid.Full:X8}, account {sourceAccountId}). The source WAS debited and is NOT being refunded, because a refund over a credit that landed would create pyreals. Reconcile both pools against this line.");

                BankMsg($"The bank could not confirm the transfer of {amount:N0} pyreals to {target.Name}. Do not retry - an admin needs to check the ledger.");

                SendSpendableCoinValue();
                return false;
            }

            // EVERY online character on the target ACCOUNT, not just the named one. The pool is shared,
            // so a sibling standing at a vendor has just had their spendable coin change too, and the
            // client refuses to send a purchase it believes is unaffordable.
            foreach (var accountPlayer in PlayerManager.GetAccountPlayersSnapshot(targetAccount.AccountId))
            {
                if (accountPlayer is Player online)
                {
                    online.Session?.Network?.EnqueueSend(new GameMessageSystemChat($"[BANK] Received {amount:N0} {label} from {Name}.", ChatMessageType.System));
                    online.SendSpendableCoinValue();
                }
            }

            // No RushNextPlayerSave and no target.SaveBiotaToDatabase: neither side's biota changed.
            // The credit is durable the moment the row commits, which is exactly what moving the
            // balance off a per-character property bought.
            SendSpendableCoinValue();

            BankMsg($"Transferred {amount:N0} {label} to {target.Name}.");

            AnalyticsManager.RecordBankTransfer(this, target.Guid.Full, target.Name, label, amount);

            return true;
        }

        /// <summary>
        /// The three PER-CHARACTER banked currencies. Pyreals are not one of them and are routed to
        /// <see cref="TransferBankedPyreals"/> before they can reach here; the guard below is what makes
        /// that a compile-time-shaped mistake rather than a silent write to a dead property.
        /// </summary>
        private bool Transfer(BankCurrency currency, string label, long amount, string targetName)
        {
            if (currency == BankCurrency.Pyreals)
                throw new InvalidOperationException("Banked pyreals are account-wide and are transferred by TransferBankedPyreals. This method still holds two per-character balances still under a guid-ordered double lock, which is not what a shared pool needs.");

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
        /// bankable item behind them at all); MMDs (Trade Note (250,000), wcid <see cref="MmdWcid"/>) are backed
        /// by BankedPyreals at <see cref="MmdValue"/> pyreals per note, floored. Every other alternate currency
        /// returns 0 (inventory-only).
        /// </summary>
        public long GetBankedAlternateCurrency(uint wcid)
        {
            if (wcid == PromissoryNoteWcid)
                return BankedPromissoryNotes;
            // PK facet: class ability points cannot be spent in any form there, so as a vendor currency the
            // balance reads as 0 (every CAP-priced purchase then fails its affordability check) - and
            // DebitBankedAlternateCurrency below refuses on its own as well, so neither half relies on the other.
            if (wcid == ClassAbilityPointCurrencyWcid)
                return FacetPk.CanSpendClassAbilityPoints(IsPkFacetRuleActive) && !IsInPvpMatch ? AvailableClassAbilityPoints : 0;
            if (wcid == MmdWcid)
                return BankedPyreals / MmdValue;
            return 0;
        }

        /// <summary>
        /// Debits a banked alternate currency for a vendor purchase. Returns the display name, or null
        /// if the currency is not bankable or the balance is insufficient (caller must not hand over goods).
        /// </summary>
        public string DebitBankedAlternateCurrency(uint wcid, long amount, out bool stateUnknown)
        {
            stateUnknown = false;

            if (wcid == PromissoryNoteWcid && BankedPromissoryNotes >= amount)
            {
                ModifyBankBalance(PropertyInt64.BankedPromissoryNotes, -amount);
                return "Promissory Notes";
            }
            // PK facet: the class ability point freeze covers points as a vendor currency too. A null return is
            // "not bankable or insufficient", which every caller already treats as "do not hand over goods".
            if (wcid == ClassAbilityPointCurrencyWcid && (!FacetPk.CanSpendClassAbilityPoints(IsPkFacetRuleActive) || IsInPvpMatch))
                return null;
            if (wcid == ClassAbilityPointCurrencyWcid && TryDebitClassAbilityPoints(AvailableClassAbilityPoints, amount, out var newPoints))
            {
                // Expressed as a delta rather than the absolute newPoints, because the CAP funnel
                // (Player_ClassAbilities.AdjustClassAbilityPoints) is delta-shaped: it is the one place
                // either counter changes, and it records the change in `character_cap_ledger`.
                // TryDebitClassAbilityPoints has already validated that newPoints is a non-negative
                // in-range result, so this subtraction is exactly the -amount it approved.
                AdjustClassAbilityPoints(newPoints - AvailableClassAbilityPoints, 0, CapLedgerReason.VendorDebit,
                    detail: $"vendor alternate-currency debit of {amount}");

                SaveBiotaToDatabase();
                return "Class Ability Points";
            }
            if (wcid == MmdWcid && TryDebitBankedMmd(BankedPyreals, amount, out var pyrealsToDebit))
            {
                // MMDs are backed by the account pool, so this debit is the one branch here that can
                // come back with the state unknown. The out parameter exists for it alone: the two
                // per-character branches above mutate a biota property, which cannot fail this way.
                var debit = TryAdjustBankedPyreals(-pyrealsToDebit, "MMD vendor debit", out _);

                if (debit == AccountBankAdjustResult.Failed)
                {
                    stateUnknown = true;
                    return null;
                }

                if (debit == AccountBankAdjustResult.Refused)
                    return null;

                UpdateCoinValue();
                return "Trade Notes (250,000)";
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

        /// <summary>
        /// Pure core of the MMD vendor-currency debit, kept static so the affordability guard is
        /// unit-testable without a live Player (the same pattern as <see cref="TryDebitClassAbilityPoints"/>).
        /// Returns FALSE without changing <paramref name="pyrealsToDebit"/> when <paramref name="amount"/> is
        /// negative or exceeds what <paramref name="bankedPyreals"/> can cover; otherwise sets the exact pyreal
        /// cost (<paramref name="amount"/> * <see cref="MmdValue"/>) and returns TRUE. Comparing against the
        /// floored note count before multiplying, rather than multiplying first and checking the product,
        /// avoids ever multiplying an out-of-range <paramref name="amount"/>.
        /// </summary>
        internal static bool TryDebitBankedMmd(long bankedPyreals, long amount, out long pyrealsToDebit)
        {
            pyrealsToDebit = 0;
            if (amount < 0 || amount > bankedPyreals / MmdValue)
                return false;

            pyrealsToDebit = amount * MmdValue;
            return true;
        }

        public static string GetAlternateCurrencyName(uint wcid)
        {
            if (wcid == PromissoryNoteWcid)
                return "Promissory Notes";
            if (wcid == ClassAbilityPointCurrencyWcid)
                return "Class Ability Points";
            if (wcid == MmdWcid)
                return "Trade Notes (250,000)";
            return null;
        }

        // ---- Market wallet (Docs/Market/DESIGN.md 4.1) ----
        // Only the PURE arithmetic lives here now. The market's own credit and debit routines were
        // removed when banked pyreals moved to the account-wide account_bank row: they mutated through
        // ModifyBankBalance(PropertyInt64.BankedPyreals) and SetProperty(PropertyInt64.BankedPyreals),
        // both of which now write - or refuse to write - a per-character property nothing reads.
        // BankMarketWallet resolves the character to its account and goes through
        // TryModifyBankedPyreals / AccountBankManager.TryAdjust instead, so the database adjudicates
        // every delta and neither an online nor an offline payee needs a lock or a biota save here.

        /// <summary>
        /// Pure core of the MMD credit and the mirror of <see cref="TryDebitBankedMmd"/>: bounds-checks
        /// BEFORE multiplying. Returns FALSE for a non-positive count, or one whose pyreal value would wrap
        /// long and land NEGATIVE, which would silently debit the payee instead of paying them.
        /// </summary>
        internal static bool TryCreditBankedMmd(long notes, out long pyrealsToCredit)
        {
            pyrealsToCredit = 0;
            if (notes <= 0 || notes > long.MaxValue / MmdValue)
                return false;

            pyrealsToCredit = notes * MmdValue;
            return true;
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
    /// A pea is not INERT to crafting, though: the retail Splitting Tool (wcid 8283) destroys one pea to yield
    /// 20 of the matching real scarab component (cook_book recipes 1720-1725, one per pea, all with
    /// success_Destroy_Target_Chance 1). Banking therefore forecloses that conversion - but it never costs the
    /// player value, because face value is 2.5x the split output in every tier: Lead 500 vs 20x10, Iron 2,500
    /// vs 20x50, Copper 5,000 vs 20x100, Silver 12,500 vs 20x250, Gold 25,000 vs 20x500, Pyreal 50,000 vs
    /// 20x1,000. Every scarab is separately vendor-stocked, so a player who wanted components can just buy
    /// them. That ratio is why the sweep is not gated behind an explicit /b d pea.
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
            { 8329,   500 },   // Lead Pea    (peascarablead)
            { 8328,  2500 },   // Iron Pea    (peascarabiron)
            { 8326,  5000 },   // Copper Pea  (peascarabcopper)
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
