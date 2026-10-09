using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;

using ACE.Entity.Adapter;
using ACE.Entity.Enum;
using ACE.Entity.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Character creation used to mutate a create list that is shared with the cached "human" weenie.
    ///
    /// PropertiesCreateList is one of the collections a WorldObject shares BY REFERENCE with the CACHED
    /// weenie: WorldObject.cs:141 builds the biota with referenceWeenieCollectionsForCommonProperties: true,
    /// and ACE.Entity/Adapter/WeenieConverter.cs:77-83 then does a bare
    /// `result.PropertiesCreateList = weenie.PropertiesCreateList`. CharacterHandler.cs resolves the human
    /// weenie via DatabaseManager.World.GetCachedWeenie("human") - one cached, process-lifetime instance
    /// shared by every character creation. Player's non-Olthoi branch used to call
    /// `Biota.PropertiesCreateList?.Clear()`, which emptied that shared instance in place: the FIRST
    /// non-Olthoi character created after server start left every LATER character (and anything else that
    /// resolves the cached "human" weenie) with an empty create list for the life of the process. The fix
    /// swaps in a fresh, independent empty Collection instead of clearing the shared one.
    ///
    /// What this harness CANNOT do, and why the coverage below is shaped the way it is: Player's constructor
    /// also calls DatabaseManager.Authentication.GetAccountById, which opens a live AuthDbContext, and
    /// GuidManager.NewPlayerGuid (CharacterHandler.cs:119) needs a live shard database too - neither is
    /// reachable from this harness (see TestEnvironment.cs; no test in this project constructs a live
    /// Player, per StageTestCommandsTests.cs's own note). So coverage here is split in two:
    ///   1. A behavioral test that drives the REAL WeenieConverter (not a stand-in) to reconfirm the
    ///      reference-sharing premise the bug rests on, shaped like a human weenie (5 create-list rows,
    ///      matching wcid 1 in ace_world).
    ///   2. A source-level regression guard, scoped to Player's non-Olthoi constructor by brace matching so
    ///      an unrelated Clear() elsewhere in this very large file cannot satisfy it, that fails if the
    ///      constructor ever goes back to clearing Biota.PropertiesCreateList in place instead of replacing
    ///      it.
    /// Neither test constructs a live Player; the constructor itself is not exercised end-to-end here.
    /// </summary>
    [TestClass]
    public class PlayerHumanCreateListShareTests
    {
        private const uint HumanWcid = 1;

        /// <summary>A weenie shaped like the cached "human" weenie: 5 create-list rows (ace_world wcid 1).</summary>
        private static Weenie HumanWeenie()
        {
            var weenie = new Weenie
            {
                WeenieClassId = HumanWcid,
                WeenieType = WeenieType.Creature,
                PropertiesCreateList = new Collection<PropertiesCreateList>()
            };

            for (var i = 0; i < 5; i++)
            {
                weenie.PropertiesCreateList.Add(new PropertiesCreateList
                {
                    WeenieClassId = 300u + (uint)i,
                    DestinationType = DestinationType.Wield,
                    StackSize = 1,
                    Palette = 0,
                    Shade = 0,
                    TryToBond = false
                });
            }

            return weenie;
        }

        /// <summary>The exact conversion WorldObject's protected ctor performs (WorldObject.cs:141).</summary>
        private static Biota NewInstanceOf(Weenie weenie, uint guid) =>
            WeenieConverter.ConvertToBiota(weenie, guid, false, true);

        [TestMethod]
        public void A_new_characters_biota_shares_its_create_list_with_the_cached_human_weenie()
        {
            // The premise the historical bug (and this test file) rests on. If this ever stops holding,
            // clearing Biota.PropertiesCreateList in place would be harmless, and the regression guard
            // below would be measuring nothing.
            var weenie = HumanWeenie();
            var biota = NewInstanceOf(weenie, 0x50000001);

            Assert.AreSame(weenie.PropertiesCreateList, biota.PropertiesCreateList,
                "WeenieConverter no longer reference-shares PropertiesCreateList; the historical Player " +
                "character-creation hazard has changed.");
        }

        [TestMethod]
        public void A_sibling_character_built_from_the_same_cached_weenie_is_also_exposed()
        {
            // The production symptom, in the shape it would actually appear: two characters created from
            // the same cached "human" weenie. Before the fix, clearing the first character's create list
            // would have emptied the second's too, since both point at the same underlying collection.
            var weenie = HumanWeenie();
            var firstCharacter = NewInstanceOf(weenie, 0x50000001);
            var secondCharacter = NewInstanceOf(weenie, 0x50000002);

            Assert.AreSame(firstCharacter.PropertiesCreateList, secondCharacter.PropertiesCreateList,
                "two characters built from the same cached weenie no longer share a create list instance");
        }

        [TestMethod]
        public void Constructor_NonOlthoiBranch_ReplacesTheSharedListInsteadOfClearingItInPlace()
        {
            var path = FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", "Player.cs"));
            var body = ExtractMethodBody(File.ReadAllText(path), "public Player(Weenie weenie, ObjectGuid guid, uint accountId)");

            Assert.IsFalse(Regex.IsMatch(body, @"PropertiesCreateList\s*\?{0,1}\s*\.\s*Clear\s*\(\s*\)"),
                "Player's non-Olthoi branch clears Biota.PropertiesCreateList in place again - that collection " +
                "is reference-shared with the cached 'human' weenie (WeenieConverter.cs:77-83), so Clear() " +
                "would empty the cached weenie's create list for every later character created from it, for " +
                "the life of the process.");

            StringAssert.Contains(body, "new Collection<PropertiesCreateList>()",
                "Player's non-Olthoi branch no longer swaps in a fresh, independent create list - this test " +
                "needs updating if the fix now takes a different shape, but a bare removal of the swap is " +
                "the historical bug returning.");
        }

        #region source helpers

        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works
        /// regardless of the bin\Debug vs bin\x64\Debug output layout. Same idiom as
        /// LandblockInitObservabilityTests.FindInSourceTree, kept local rather than shared.
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        /// <summary>
        /// Returns the source text of one method body, by brace matching from the method's opening brace.
        /// Scoping the assertion to a single constructor is the point: a file-wide grep would be satisfied
        /// by an unrelated Clear() call anywhere else in this very large file. Same idiom as
        /// LandblockInitObservabilityTests.ExtractMethodBody, kept local rather than shared.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find '{signature}' in the source under test");

            var open = source.IndexOf('{', start);
            Assert.IsTrue(open >= 0, $"Could not find the opening brace of '{signature}'");

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"Unbalanced braces while extracting '{signature}'");
            return null;
        }

        #endregion
    }
}
