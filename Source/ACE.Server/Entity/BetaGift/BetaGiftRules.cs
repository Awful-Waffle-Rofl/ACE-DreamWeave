using System;

using ACE.Entity.Enum;

namespace ACE.Server.Entity.BetaGift
{
    /// <summary>
    /// Pure eligibility rule for the Dreamweave beta-tester gift trinket: every character created on an
    /// account whose ace_auth.account.create_Time is before the configured cutoff gets one copy of the
    /// gift weenie at creation. No PropertyManager/DB access here on purpose - CharacterHandler reads the
    /// tunables and the account's create time and passes plain values in, which is what makes this class
    /// testable with no live server.
    /// </summary>
    public static class BetaGiftRules
    {
        /// <summary>
        /// Default for the beta_gift_account_cutoff_unix tunable: 2026-10-02T00:00:00Z, the instant the
        /// repo owner named as the beta shut-off (8pm EDT 10/1/2026). Computed with DateTimeOffset rather
        /// than hand-entered as a raw number, so a test can pin the exact same formula rather than a
        /// second, independently-typed copy of the same date drifting out of sync with this one.
        /// </summary>
        public static readonly long DefaultAccountCutoffUnix =
            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        /// <summary>
        /// True when a newly-created character on this account should receive the gift.
        /// </summary>
        /// <summary>
        /// True when a reason string came from the "no account" case rather than ordinary ineligibility -
        /// the one case CharacterHandler warns on, since a null Account is unexpected (GetAccountById
        /// returning null between login and character creation), while an unmet cutoff/disabled
        /// wcid/Olthoi heritage are all routine and never logged per-character.
        /// </summary>
        public const string NoAccountReason = "no account: player.Account was null";

        /// <param name="accountCreateTime">
        /// ace_auth.account.create_Time as read from the database, or null when the Account itself is
        /// null (DatabaseManager.Authentication.GetAccountById can return null - see AuthenticationDatabase.cs).
        /// The column is always written with DateTime.UtcNow (AuthenticationDatabase.CreateAccount), but
        /// Pomelo/MySql hands the value back with DateTimeKind.Unspecified, not Utc - so a non-null value
        /// is treated as ALREADY being a UTC wall-clock reading regardless of its Kind (DateTime.SpecifyKind,
        /// never DateTime.ToUniversalTime, which would silently reinterpret an Unspecified value as the
        /// host's local time and shift it).
        /// </param>
        /// <param name="cutoffUnixSeconds">beta_gift_account_cutoff_unix - Unix seconds, UTC.</param>
        /// <param name="giftWcid">
        /// beta_gift_wcid - the gift weenie's class id. &lt;= 0 disables the gift entirely; a value above
        /// uint.MaxValue is also rejected here rather than left for CharacterHandler's (uint) cast to
        /// wrap it into an unrelated, small wcid.
        /// </param>
        /// <param name="heritage">The character's heritage. Olthoi/OlthoiAcid characters are always skipped.</param>
        /// <param name="reason">A short, non-player-facing reason for the result - for logging/tests, never shown to the player.</param>
        public static bool IsEligible(DateTime? accountCreateTime, long cutoffUnixSeconds, long giftWcid, HeritageGroup heritage, out string reason)
        {
            if (accountCreateTime == null)
            {
                reason = NoAccountReason;
                return false;
            }

            if (giftWcid <= 0 || giftWcid > uint.MaxValue)
            {
                reason = "disabled: beta_gift_wcid out of range (must be 1..uint.MaxValue - CharacterHandler casts it to uint)";
                return false;
            }

            if (heritage == HeritageGroup.Olthoi || heritage == HeritageGroup.OlthoiAcid)
            {
                reason = "skipped: Olthoi heritage";
                return false;
            }

            var accountCreateUtc = DateTime.SpecifyKind(accountCreateTime.Value, DateTimeKind.Utc);
            var cutoffUtc = DateTimeOffset.FromUnixTimeSeconds(cutoffUnixSeconds).UtcDateTime;

            if (accountCreateUtc >= cutoffUtc)
            {
                reason = "not eligible: account created at or after the beta cutoff";
                return false;
            }

            reason = "eligible";
            return true;
        }
    }
}
