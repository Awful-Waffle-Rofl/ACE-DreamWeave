using System.Security.Cryptography;

namespace ACE.Server.Managers.CharacterSheets
{
    /// <summary>
    /// The public character sheet slug: 10 characters of base62 drawn from RandomNumberGenerator. Never
    /// derived from the character guid, which is dense and enumerable. Case-sensitive (the column is
    /// ascii_bin), so "abc" and "ABC" are different slugs.
    /// </summary>
    public static class SheetSlug
    {
        public const int Length = 10;

        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        public static string New()
        {
            var chars = new char[Length];

            for (var i = 0; i < Length; i++)
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

            return new string(chars);
        }

        /// <summary>Exactly <see cref="Length"/> characters, every one ASCII [0-9A-Za-z].</summary>
        public static bool IsWellFormed(string s)
        {
            if (s == null || s.Length != Length)
                return false;

            foreach (var c in s)
            {
                if (!char.IsAsciiLetterOrDigit(c))
                    return false;
            }

            return true;
        }
    }
}
