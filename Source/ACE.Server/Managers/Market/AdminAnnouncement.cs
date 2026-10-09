namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Pure normalization and validation for POST /v1/admin/announce (PLAN-P3.md section 1.2 and
    /// 2). No static state, so it needs no PropertyManager read and is unit-testable with no live
    /// server. The market repo's Api/AdminAnnouncementText.cs mirrors this rule byte for byte
    /// (invariant 10) - do not change one without the other.
    /// </summary>
    public static class AdminAnnouncement
    {
        public const int MaxChars = 500;

        // No MaxBodyBytes here: MarketApiHost.MaxAdminBodyBytes is the single source of the 8 KiB
        // figure (code review, 2026-09-15) - MapAdminPost's ReadBodyText already bounds rawBody to it
        // and passes null above it, so this class need not know the number at all.

        /// <summary>Every char.IsControl character (CR, LF and TAB included) becomes a space, then both ends are trimmed. Null stays null.</summary>
        public static string Normalize(string raw)
        {
            if (raw == null)
                return null;

            var chars = raw.ToCharArray();

            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i]))
                    chars[i] = ' ';
            }

            return new string(chars).Trim();
        }

        /// <summary>False when the normalized result is null, empty, or longer than <see cref="MaxChars"/>. Never truncates.</summary>
        public static bool TryNormalize(string raw, out string text)
        {
            text = Normalize(raw);

            if (string.IsNullOrEmpty(text) || text.Length > MaxChars)
            {
                text = null;
                return false;
            }

            return true;
        }

        public static string AuditMessage(string accountName, string text) =>
            $"{accountName} issued a world broadcast from the web admin panel: {text}";
    }
}
