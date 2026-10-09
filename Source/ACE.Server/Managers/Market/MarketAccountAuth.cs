using System;

using ACE.Database.Models.Auth;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The account checks the market web API shares, so the login path and the admin gate cannot drift
    /// apart on what "banned" means.
    /// </summary>
    public static class MarketAccountAuth
    {
        /// <summary>
        /// True while a ban is in force: a ban expiry strictly after <paramref name="utcNow"/>. No expiry,
        /// or an expiry at or before now, is not banned. These are AdminAuthorizer.Check's original
        /// semantics, and the same rule the game login applies (AuthenticationHandler: now &lt; BanExpireTime).
        /// </summary>
        public static bool IsBanned(Account account, DateTime utcNow)
        {
            return account?.BanExpireTime != null && account.BanExpireTime.Value > utcNow;
        }

        /// <summary>
        /// The market login decision: the account id when the account exists, the password matches and the
        /// account is not currently banned, otherwise 0. Unknown account, wrong password and banned all
        /// answer the same 0, so the endpoint cannot be used to enumerate accounts or ban status. The ban
        /// is checked only AFTER the password, so a banned account costs the same bcrypt verify as any other
        /// and response time does not reveal it either. The password check is a delegate so this is
        /// testable without a config or a database (Account.PasswordMatches reads both).
        /// </summary>
        public static uint ResolveLogin(Account account, string password, DateTime utcNow, Func<Account, string, bool> passwordMatches)
        {
            if (account == null || passwordMatches == null || !passwordMatches(account, password))
                return 0u;

            if (IsBanned(account, utcNow))
                return 0u;

            return account.AccountId;
        }
    }
}
