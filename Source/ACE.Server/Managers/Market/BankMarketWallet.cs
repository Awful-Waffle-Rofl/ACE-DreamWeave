using System;

using log4net;

using ACE.Database;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The reference IMarketWallet: the bank (DESIGN 4.1). MMD is banked pyreals at Player.MmdValue per
    /// note, floored, as the vendor shop hook already treats it. An unknown character reads as balance 0
    /// and never throws.
    ///
    /// THE POOL IS ACCOUNT-WIDE, THE KEY IS STILL A CHARACTER. Banked pyreals live in the account_bank
    /// row keyed by account id, not in the per-character PropertyInt64.BankedPyreals (9004) this wallet
    /// used to read and write. IMarketWallet is keyed by character guid because a sale must complete
    /// whether or not the seller is logged in, so every method here resolves the character to its
    /// account first and then operates on that account's pool. NOTHING IN THIS FILE MAY TOUCH 9004:
    /// Player_Bank.ModifyBankBalance throws for it precisely so a forgotten path is loud instead of
    /// silently writing a property nothing reads.
    ///
    /// ONLINE AND OFFLINE ARE BOTH FIRST-CLASS, but they differ only in which face of the same ledger
    /// they call. An online character goes through Player.TryModifyBankedPyreals so the bank's own
    /// LEDGER STATE UNKNOWN marker names the character; an offline one goes straight to
    /// AccountBankManager.TryAdjust, and this file writes the equivalent marker itself. Either way the
    /// database applies and adjudicates the delta - there is no read-modify-write here to lose.
    /// </summary>
    public class BankMarketWallet : IMarketWallet
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The production resolver, kept nameable so a test can restore it instead of restating it.</summary>
        internal static readonly Func<uint, IPlayer> DefaultResolveCharacter = guid => PlayerManager.FindByGuid(guid);

        /// <summary>
        /// How a character guid becomes the IPlayer whose account owns the pool. Settable so a test
        /// needs no live PlayerManager, mirroring MarketNotifier.Send; production never reassigns it.
        /// </summary>
        internal static Func<uint, IPlayer> ResolveCharacter { get; set; } = DefaultResolveCharacter;

        public long GetBalanceMmd(uint characterGuid)
        {
            var holder = ResolveCharacter(characterGuid);

            if (holder == null)
                return 0;

            // The online read goes through the Player getter so a missing Account is reported once per
            // character in the bank's own voice. Both spellings resolve to the same account pool.
            if (holder is Player online)
                return online.BankedPyreals / Player.MmdValue;

            var account = holder.Account;

            if (account == null)
            {
                log.Warn($"[MARKET] character 0x{characterGuid:X8} has no Account, so it has no banked pyreal pool. Reporting a balance of 0.");
                return 0;
            }

            return AccountBankManager.GetBalance(account.AccountId) / Player.MmdValue;
        }

        public bool TryDebit(uint characterGuid, long amountMmd, out MarketError error)
        {
            error = MarketError.None;

            if (amountMmd < 0)
            {
                error = MarketError.ServerError;
                return false;
            }

            if (amountMmd == 0)
                return true;

            var holder = ResolveCharacter(characterGuid);

            if (holder == null)
            {
                log.Warn($"[MARKET] debit refused: no character 0x{characterGuid:X8} is known to this server.");
                error = MarketError.BadCredentials;
                return false;
            }

            var account = holder.Account;

            if (account == null)
            {
                log.Error($"[MARKET] debit of {amountMmd} MMD refused: character 0x{characterGuid:X8} has no Account, so there is no pool to draw on.");
                error = MarketError.ServerError;
                return false;
            }

            // TryGetBalance, not GetBalance: an unavailable balance reads as 0, and refusing a solvent
            // buyer with "insufficient funds" because a SELECT failed is a lie the player cannot act on.
            // ServerError is the truthful answer and the next read retries.
            if (!AccountBankManager.TryGetBalance(account.AccountId, out var bankedPyreals))
            {
                log.Error($"[MARKET] debit of {amountMmd} MMD from character 0x{characterGuid:X8} refused: the banked pyreal balance for account {account.AccountId} could not be read.");
                error = MarketError.ServerError;
                return false;
            }

            // Bounds-checks BEFORE multiplying, so an amount whose pyreal value would wrap long cannot
            // reach the ledger as a negative delta. It doubles as the affordability pre-check; the
            // guarded UPDATE below is still the adjudicator, which is why a Refused from it is mapped
            // to InsufficientFunds rather than treated as impossible.
            if (!Player.TryDebitBankedMmd(bankedPyreals, amountMmd, out var pyrealsToDebit))
            {
                error = MarketError.InsufficientFunds;
                return false;
            }

            var result = Adjust(holder, account.AccountId, -pyrealsToDebit);

            switch (result)
            {
                case AccountBankAdjustResult.Applied:
                case AccountBankAdjustResult.AppliedCountUnknown:
                    // AppliedCountUnknown is a SUCCESS here and the distinction matters: the pyreals
                    // provably left the pool and only the read-back of the new balance was lost.
                    // Reporting it as a failure would fail a purchase the buyer has already paid for.
                    RefreshOnlineCoin(account.AccountId);
                    return true;

                case AccountBankAdjustResult.Refused:
                    error = MarketError.InsufficientFunds;
                    return false;

                default:
                    // Failed: nobody knows whether the pyreals moved. Never reported as success, and
                    // NOT reported as a plain ServerError either - LedgerUnknown is what makes
                    // MarketManager.Buy resolve the row to DebitLedgerUnknown rather than Failed, so
                    // the one case where a buyer may have paid for nothing is findable in
                    // market_transaction instead of sitting among the ordinary refusals.
                    log.Error($"[MARKET] LEDGER STATE UNKNOWN: debit of {amountMmd} MMD ({pyrealsToDebit} pyreals) from character 0x{characterGuid:X8}, account {account.AccountId}. The adjustment neither committed nor provably failed - reconcile account_bank against this line and market_transaction.");
                    error = MarketError.LedgerUnknown;
                    return false;
            }
        }

        public bool TryCredit(uint characterGuid, long amountMmd)
        {
            if (amountMmd <= 0)
                return true;

            // Bounds-checked before anything is looked up, and before multiplying: a count whose pyreal
            // value would wrap long lands NEGATIVE and would silently debit the payee instead of paying
            // them.
            if (!Player.TryCreditBankedMmd(amountMmd, out var pyrealsToCredit))
            {
                log.Error($"[MARKET] CREDIT REFUSED: {amountMmd} MMD to character 0x{characterGuid:X8} exceeds the representable pyreal range. Reconcile against market_transaction.");
                return false;
            }

            var holder = ResolveCharacter(characterGuid);

            if (holder == null)
            {
                // The loudest line the market can produce short of a lost item: somebody was owed money
                // and there is nobody to pay. Recoverable only from market_transaction.
                log.Error($"[MARKET] CREDIT LOST: {amountMmd} MMD could not be paid to character 0x{characterGuid:X8}, which this server does not know. Reconcile against market_transaction.");
                return false;
            }

            var account = holder.Account;

            if (account == null)
            {
                log.Error($"[MARKET] CREDIT LOST: {amountMmd} MMD could not be paid to character 0x{characterGuid:X8}, which has no Account and therefore no banked pyreal pool. Reconcile against market_transaction.");
                return false;
            }

            var result = Adjust(holder, account.AccountId, pyrealsToCredit);

            switch (result)
            {
                case AccountBankAdjustResult.Applied:
                case AccountBankAdjustResult.AppliedCountUnknown:
                    RefreshOnlineCoin(account.AccountId);
                    return true;

                case AccountBankAdjustResult.Refused:
                    log.Error($"[MARKET] CREDIT LOST: {amountMmd} MMD ({pyrealsToCredit} pyreals) to character 0x{characterGuid:X8} was refused by account {account.AccountId}'s pool. The balance provably did not move. Reconcile against market_transaction.");
                    return false;

                default:
                    // Failed. NOT the same as lost, and the caller's own CREDIT LOST line that follows
                    // this one must be read with this qualifier: the credit may have committed, so an
                    // operator paying it by hand off that line alone could pay it twice.
                    log.Error($"[MARKET] LEDGER STATE UNKNOWN: credit of {amountMmd} MMD ({pyrealsToCredit} pyreals) to character 0x{characterGuid:X8}, account {account.AccountId}. Whether it committed is UNKNOWN - reconcile account_bank against this line BEFORE paying it by hand.");
                    return false;
            }
        }

        /// <summary>
        /// Applies a signed pyreal delta to the character's account pool, normalised to the four
        /// outcomes whichever face it went through.
        ///
        /// The online branch exists for its LOGGING, not for its arithmetic: both branches end in the
        /// same AccountBankManager.TryAdjust against the same row, but Player.TryAdjustBankedPyreals
        /// writes the bank's LEDGER STATE UNKNOWN line with the character's name and guid on the way
        /// past. A null Account is impossible here - both callers resolve one before calling - which is
        /// what keeps TryModifyBankedPyreals' own null-Account Refused out of the mapping below.
        /// </summary>
        private static AccountBankAdjustResult Adjust(IPlayer holder, uint accountId, long deltaPyreals)
        {
            if (holder is Player online)
            {
                var applied = online.TryModifyBankedPyreals(deltaPyreals, out _, out var stateUnknown);

                if (applied)
                    return stateUnknown ? AccountBankAdjustResult.AppliedCountUnknown : AccountBankAdjustResult.Applied;

                return stateUnknown ? AccountBankAdjustResult.Failed : AccountBankAdjustResult.Refused;
            }

            return AccountBankManager.TryAdjust(accountId, deltaPyreals, out _);
        }

        /// <summary>
        /// Pushes the new spendable-coin figure to EVERY online character on the account, not just the
        /// one the market named. The pool is shared, so a sibling standing at a vendor has just had
        /// their spendable coin change too, and the client refuses to send a purchase it believes is
        /// unaffordable. This is also what replaces the old online path's UpdateCoinValue call.
        ///
        /// Wrapped, because the money has already moved by the time this runs and a throw here would
        /// answer 500 on the Kestrel thread for a purchase that actually succeeded.
        /// </summary>
        private static void RefreshOnlineCoin(uint accountId)
        {
            try
            {
                foreach (var accountPlayer in PlayerManager.GetAccountPlayersSnapshot(accountId))
                {
                    if (accountPlayer is Player online)
                        online.SendSpendableCoinValue();
                }
            }
            catch (Exception ex)
            {
                log.Warn($"[MARKET] could not refresh the spendable coin figure for account {accountId} after a wallet move: {ex.Message}. The balance itself is unaffected.");
            }
        }
    }
}
