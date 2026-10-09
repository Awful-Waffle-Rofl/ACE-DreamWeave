using System;

using log4net;

using ACE.Common.Extensions;
using ACE.Database.Models.Auth;

namespace ACE.Server.Managers.Market
{
    /// <summary>Result of <see cref="AdminAuthorizer.Check"/>. Unavailable means the check itself could not run (no reader configured, or the reader threw) - distinct from NotAdmin, which is a definite refusal.</summary>
    public enum AdminCheck
    {
        Admin,
        NotAdmin,
        Unavailable,
    }

    /// <summary>Who is acting, once <see cref="AdminCheck.Admin"/> has been established.</summary>
    public readonly record struct AdminPrincipal(uint AccountId, string AccountName)
    {
        /// <summary>The account's access level as read by the same Check that authorized it (PLAN-P4.md section 3.4). An init property, not a positional member, so existing constructions keep compiling.</summary>
        public ACE.Entity.Enum.AccessLevel Level { get; init; }
    }

    /// <summary>
    /// Every /v1/admin route's authorization gate (DESIGN section 8). Re-reads the account row on
    /// EVERY call - no caching - so a demoted or banned account is refused on its very next request,
    /// not merely at its next login. Fails closed: a null account, a reader that throws, any access
    /// level other than exactly Admin (5), or an active ban all answer NotAdmin/Unavailable, never
    /// Admin.
    /// </summary>
    public sealed class AdminAuthorizer
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Func<uint, Account> reader;
        private readonly Func<DateTime> utcNow;

        public AdminAuthorizer(Func<uint, Account> reader, Func<DateTime> utcNow = null)
        {
            this.reader = reader;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Account id 0 is refused WITHOUT a read (it can never resolve to a row). A missing reader,
        /// or one that throws, is Unavailable rather than NotAdmin - the caller cannot tell "definitely
        /// not an admin" from "could not check", and the two must not be confused: Unavailable maps to
        /// 500 server_error (refused, never granted), never to a silent Admin grant.
        /// </summary>
        public AdminCheck Check(uint accountId, out AdminPrincipal principal)
        {
            principal = default;

            if (accountId == 0)
                return AdminCheck.NotAdmin;

            if (reader == null)
                return AdminCheck.Unavailable;

            Account account;

            try
            {
                account = reader(accountId);
            }
            catch (Exception ex)
            {
                log.Warn($"[MARKET][ADMIN] AdminAuthorizer.Check: the account reader threw for account {accountId}: {ex.GetFullMessage()}");
                return AdminCheck.Unavailable;
            }

            if (account == null)
                return AdminCheck.NotAdmin;

            if (account.AccessLevel != (uint)ACE.Entity.Enum.AccessLevel.Admin)
                return AdminCheck.NotAdmin;

            if (MarketAccountAuth.IsBanned(account, utcNow()))
                return AdminCheck.NotAdmin;

            principal = new AdminPrincipal(accountId, account.AccountName) { Level = (ACE.Entity.Enum.AccessLevel)account.AccessLevel };
            return AdminCheck.Admin;
        }
    }
}
