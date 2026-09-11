using System.IO;
using System.Text;

using ACE.Database.Models.Shard;
using ACE.Database.SQLFormatters.Shard;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers CharacterSQLWriter.CreateSQLINSERTStatement's handling of Character.GameplayOptions
    /// (a byte[]/blob column). Before the fix, string-interpolating a non-null byte[] directly called
    /// its default ToString(), emitting the literal token "System.Byte[]" instead of a SQL literal -
    /// invalid SQL that would fail to execute against a real database. This writer had zero callers
    /// before the prodcharseed extractor (Task 6) became the first, which is how this was found.
    /// </summary>
    [TestClass]
    public class CharacterSQLWriterGameplayOptionsTests
    {
        private static string CreateInsertStatement(Character character)
        {
            var writer = new CharacterSQLWriter();

            using var stream = new MemoryStream();
            using (var streamWriter = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.CreateSQLINSERTStatement(character, streamWriter);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// Every column set explicitly, so the expected VALUES line below can be written out by hand
        /// instead of being derived from the writer's own formatting. That is the whole point of these
        /// two tests: an expectation computed from the code under test passes whether or not
        /// GetSQLBinary exists.
        /// </summary>
        private static Character FullyPopulatedCharacter(byte[] gameplayOptions)
        {
            return new Character
            {
                Id = 0x5A000000,               // 1509949440
                AccountId = 7,
                Name = "Rostersource 0",
                IsPlussed = false,
                IsDeleted = true,
                DeleteTime = 1699999999,
                LastLoginTimestamp = 1700000000,
                TotalLogins = 42,
                CharacterOptions1 = 1,
                CharacterOptions2 = 2,
                GameplayOptions = gameplayOptions,
                SpellbookFilters = 3,
                HairTexture = 4,
                DefaultHairTexture = 5
            };
        }

        [TestMethod]
        public void CreateSQLINSERTStatement_NonEmptyGameplayOptions_EmitsHexLiteral()
        {
            // Hand-written, not computed. If GetSQLBinary regresses to a bare byte[] interpolation this
            // line becomes "..., System.Byte[], ..." and the assert fails on the exact substitution the
            // fix exists to prevent.
            const string expected =
                "VALUES (1509949440, 7, 'Rostersource 0', False, True, 1699999999, 1700000000, 42, 1, 2, 0xDEADBEEF, 3, 4, 5);";

            var output = CreateInsertStatement(FullyPopulatedCharacter(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));

            StringAssert.Contains(output, expected);
        }

        [TestMethod]
        public void CreateSQLINSERTStatement_NullOrEmptyGameplayOptions_EmitsNull()
        {
            const string expected =
                "VALUES (1509949440, 7, 'Rostersource 0', False, True, 1699999999, 1700000000, 42, 1, 2, NULL, 3, 4, 5);";

            var nullOutput = CreateInsertStatement(FullyPopulatedCharacter(null));
            var emptyOutput = CreateInsertStatement(FullyPopulatedCharacter(new byte[0]));

            StringAssert.Contains(nullOutput, expected);
            StringAssert.Contains(emptyOutput, expected);
        }
    }
}
