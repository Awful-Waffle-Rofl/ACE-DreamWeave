namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The currency seam (DESIGN 4.1 and 10; reference impl BankMarketWallet/Player_Bank). Everything is
    /// in WHOLE MMD (converted+floored by the impl), keyed by CHARACTER guid since a sale must complete whether or not the seller is logged in.
    /// </summary>
    public interface IMarketWallet
    {
        /// <summary>Whole MMD this character can spend right now. 0 for an unknown character.</summary>
        long GetBalanceMmd(uint characterGuid);

        /// <summary>Removes whole MMD. False leaves the balance untouched; this is the only routine failure here.</summary>
        bool TryDebit(uint characterGuid, long amountMmd, out MarketError error);

        /// <summary>
        /// Adds whole MMD, online or offline. False means the credit did NOT land and must be logged with
        /// the character guid and amount: it is a seller who was not paid or a buyer who was not refunded.
        /// </summary>
        bool TryCredit(uint characterGuid, long amountMmd);
    }
}
