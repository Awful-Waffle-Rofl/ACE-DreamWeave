using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Database.SQLFormatters.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Regression coverage for the bug where BiotaSQLWriter emitted the column `biota_Class_Id`
    /// in four INSERT column lists, when the real shard schema names that column `weenie_Class_Id`
    /// in all four tables. Nothing caught this because the writers had no callers until recently -
    /// it surfaced live as a MySQL "ERROR 1054 (42S22): Unknown column 'biota_Class_Id' in 'field
    /// list'" during an apply.
    ///
    /// This test drives BiotaSQLWriter and CharacterSQLWriter over synthetic objects populated
    /// broadly enough to hit every INSERT INTO call site in both writers, captures the emitted SQL
    /// text, and asserts every (table, column) pair it names actually exists in the shard schema
    /// (Database/Base/ShardBase.sql). Comparison is case-sensitive: the schema spells identifiers
    /// snake_Case with capitalized word boundaries (e.g. weenie_Class_Id, object_Id), and MySQL on
    /// a case-sensitive filesystem/collation would reject a mismatched case just like a wrong name.
    /// </summary>
    [TestClass]
    public class ShardSQLWriterSchemaTests
    {
        private const string SchemaRelativePath = "Database/Base/ShardBase.sql";

        private const int ExpectedTableCount = 40;

        // Every table name that at least one of the two writers under test is expected to emit an
        // INSERT INTO for. Used only to assert coverage (that our fixtures actually reached every
        // INSERT site) - not used as the pass/fail oracle for column names, which is the schema.
        private static readonly HashSet<string> ExpectedTables = new HashSet<string>(StringComparer.Ordinal)
        {
            // BiotaSQLWriter (25 INSERT INTO sites)
            "biota",
            "biota_properties_int",
            "biota_properties_int64",
            "biota_properties_bool",
            "biota_properties_float",
            "biota_properties_string",
            "biota_properties_d_i_d",
            "biota_properties_position",
            "biota_properties_i_i_d",
            "biota_properties_attribute",
            "biota_properties_attribute_2nd",
            "biota_properties_skill",
            "biota_properties_body_part",
            "biota_properties_spell_book",
            "biota_properties_event_filter",
            "biota_properties_emote",
            "biota_properties_emote_action",
            "biota_properties_create_list",
            "biota_properties_book",
            "biota_properties_book_page_data",
            "biota_properties_generator",
            "biota_properties_palette",
            "biota_properties_texture_map",
            "biota_properties_anim_part",
            "biota_properties_enchantment_registry",

            // CharacterSQLWriter (8 INSERT INTO sites)
            "character",
            "character_properties_contract_registry",
            "character_properties_fill_comp_book",
            "character_properties_friend_list",
            "character_properties_quest_registry",
            "character_properties_shortcut_bar",
            "character_properties_spell_bar",
            "character_properties_title_book",
        };

        [TestMethod]
        public void EmittedColumns_AllExistInShardSchema()
        {
            var schemaPath = FindInSourceTree(SchemaRelativePath);

            Assert.IsNotNull(schemaPath, $"Could not find {SchemaRelativePath} by walking up from {AppContext.BaseDirectory}.");

            var schema = ParseSchema(schemaPath);

            Assert.AreEqual(ExpectedTableCount, schema.Count,
                $"Expected {ExpectedTableCount} CREATE TABLE blocks in {SchemaRelativePath}, found {schema.Count}. " +
                "The parser rules (CREATE TABLE / column line / ENGINE close) may need to be revisited, or the schema file itself changed.");

            var emittedSql = CaptureEmittedSql();

            var statements = ParseInsertStatements(emittedSql);

            // Coverage check: make sure our fixtures actually reached every INSERT INTO call site
            // in both writers, so a silently-skipped site can't hide a schema mismatch from us.
            var emittedTables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var statement in statements)
                emittedTables.Add(statement.Table);

            var missingTables = new List<string>();
            foreach (var expected in ExpectedTables)
            {
                if (!emittedTables.Contains(expected))
                    missingTables.Add(expected);
            }

            Assert.AreEqual(0, missingTables.Count,
                $"The synthetic fixtures did not reach the INSERT INTO site(s) for: {string.Join(", ", missingTables)}. " +
                "Every INSERT INTO call site in BiotaSQLWriter/CharacterSQLWriter must be exercised by this test.");

            // The real assertion: every (table, column) pair emitted must exist in the shard schema.
            foreach (var statement in statements)
            {
                var writerMethod = DescribeWriter(statement.Table);

                Assert.IsTrue(schema.TryGetValue(statement.Table, out var columns),
                    $"Table `{statement.Table}` (emitted by {writerMethod}) does not exist in {SchemaRelativePath} at all.");

                foreach (var column in statement.Columns)
                {
                    Assert.IsTrue(columns.Contains(column),
                        $"Column `{column}` on table `{statement.Table}` (emitted by {writerMethod}) does not exist in the shard schema " +
                        $"({SchemaRelativePath}). This is exactly the class of bug that shipped `biota_Class_Id` instead of `weenie_Class_Id`.");
                }
            }
        }

        // ---- Schema oracle: parse CREATE TABLE blocks out of ShardBase.sql ----

        private static readonly Regex TableOpenRegex = new Regex(@"^CREATE TABLE `([a-z_0-9]+)`", RegexOptions.Compiled);
        private static readonly Regex TableCloseRegex = new Regex(@"^\)\s*ENGINE", RegexOptions.Compiled);
        private static readonly Regex ColumnRegex = new Regex(@"^\s*`([A-Za-z_0-9]+)`\s", RegexOptions.Compiled);

        private static Dictionary<string, HashSet<string>> ParseSchema(string schemaPath)
        {
            var tables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            string currentTable = null;

            foreach (var rawLine in File.ReadAllLines(schemaPath))
            {
                if (currentTable == null)
                {
                    var openMatch = TableOpenRegex.Match(rawLine);
                    if (openMatch.Success)
                    {
                        currentTable = openMatch.Groups[1].Value;
                        tables[currentTable] = new HashSet<string>(StringComparer.Ordinal);
                    }

                    continue;
                }

                if (TableCloseRegex.IsMatch(rawLine))
                {
                    currentTable = null;
                    continue;
                }

                var columnMatch = ColumnRegex.Match(rawLine);
                if (columnMatch.Success)
                    tables[currentTable].Add(columnMatch.Groups[1].Value);
            }

            Assert.IsNull(currentTable, $"Reached end of {SchemaRelativePath} while still inside CREATE TABLE `{currentTable}` - the ENGINE-close pattern did not match; the parser rules may need to be revisited.");

            return tables;
        }

        // ---- Driving the writers over synthetic fixtures ----

        private static string CaptureEmittedSql()
        {
            using var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
            {
                var biotaWriter = new BiotaSQLWriter();
                biotaWriter.CreateSQLINSERTStatement(BuildSyntheticBiota(), writer);

                var characterWriter = new CharacterSQLWriter();
                characterWriter.CreateSQLINSERTStatement(BuildSyntheticCharacter(), writer);

                writer.Flush();
            }

            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static Biota BuildSyntheticBiota()
        {
            const uint id = 100u;

            var biota = new Biota
            {
                Id = id,
                WeenieClassId = 42,
                WeenieType = 1,
                PopulatedCollectionFlags = 0,
            };

            biota.BiotaPropertiesInt.Add(new BiotaPropertiesInt { ObjectId = id, Type = 1, Value = 1 });
            biota.BiotaPropertiesInt64.Add(new BiotaPropertiesInt64 { ObjectId = id, Type = 1, Value = 1L });
            biota.BiotaPropertiesBool.Add(new BiotaPropertiesBool { ObjectId = id, Type = 1, Value = true });
            biota.BiotaPropertiesFloat.Add(new BiotaPropertiesFloat { ObjectId = id, Type = 1, Value = 1.0 });
            biota.BiotaPropertiesString.Add(new BiotaPropertiesString { ObjectId = id, Type = 1, Value = "test" });
            biota.BiotaPropertiesDID.Add(new BiotaPropertiesDID { ObjectId = id, Type = 1, Value = 1 });

            biota.BiotaPropertiesPosition.Add(new BiotaPropertiesPosition
            {
                ObjectId = id,
                PositionType = 1,
                ObjCellId = 0x00010001,
                OriginX = 1.0f,
                OriginY = 1.0f,
                OriginZ = 1.0f,
                AnglesW = 1.0f,
                AnglesX = 0.0f,
                AnglesY = 0.0f,
                AnglesZ = 0.0f,
            });

            biota.BiotaPropertiesIID.Add(new BiotaPropertiesIID { ObjectId = id, Type = 1, Value = 1 });

            biota.BiotaPropertiesAttribute.Add(new BiotaPropertiesAttribute { ObjectId = id, Type = 1, InitLevel = 10, LevelFromCP = 0, CPSpent = 0 });
            biota.BiotaPropertiesAttribute2nd.Add(new BiotaPropertiesAttribute2nd { ObjectId = id, Type = 1, InitLevel = 10, LevelFromCP = 0, CPSpent = 0, CurrentLevel = 10 });

            biota.BiotaPropertiesSkill.Add(new BiotaPropertiesSkill
            {
                ObjectId = id,
                Type = 1,
                LevelFromPP = 0,
                SAC = 0,
                PP = 0,
                InitLevel = 0,
                ResistanceAtLastCheck = 0,
                LastUsedTime = 0.0,
            });

            biota.BiotaPropertiesBodyPart.Add(new BiotaPropertiesBodyPart
            {
                Id = 1,
                ObjectId = id,
                Key = 1,
                DType = 0,
                DVal = 0,
                DVar = 0f,
                BaseArmor = 0,
                ArmorVsSlash = 0,
                ArmorVsPierce = 0,
                ArmorVsBludgeon = 0,
                ArmorVsCold = 0,
                ArmorVsFire = 0,
                ArmorVsAcid = 0,
                ArmorVsElectric = 0,
                ArmorVsNether = 0,
                BH = 0,
                HLF = 0f,
                MLF = 0f,
                LLF = 0f,
                HRF = 0f,
                MRF = 0f,
                LRF = 0f,
                HLB = 0f,
                MLB = 0f,
                LLB = 0f,
                HRB = 0f,
                MRB = 0f,
                LRB = 0f,
            });

            biota.BiotaPropertiesSpellBook.Add(new BiotaPropertiesSpellBook { ObjectId = id, Spell = 1, Probability = 1.0f });
            biota.BiotaPropertiesEventFilter.Add(new BiotaPropertiesEventFilter { ObjectId = id, Event = 1 });

            var emote = new BiotaPropertiesEmote
            {
                Id = 1,
                ObjectId = id,
                Category = 1,
                Probability = 1.0f,
                WeenieClassId = 42,
                Style = 1,
                Substyle = 1,
                Quest = "TestQuest",
                VendorType = 1,
                MinHealth = 0.0f,
                MaxHealth = 1.0f,
            };
            emote.BiotaPropertiesEmoteAction.Add(new BiotaPropertiesEmoteAction
            {
                Id = 1,
                EmoteId = 1,
                Order = 0,
                Type = 1,
                Delay = 0f,
                Extent = 0f,
                Motion = 1,
                Message = "hi",
                TestString = "test",
                Min = 1,
                Max = 2,
                Min64 = 1L,
                Max64 = 2L,
                MinDbl = 1.0,
                MaxDbl = 2.0,
                Stat = 1,
                Display = true,
                Amount = 1,
                Amount64 = 1L,
                HeroXP64 = 1L,
                Percent = 0.5,
                SpellId = 1,
                WealthRating = 1,
                TreasureClass = 1,
                TreasureType = 1,
                PScript = 1,
                Sound = 1,
                DestinationType = 1,
                WeenieClassId = 42,
                StackSize = 1,
                Palette = 1,
                Shade = 0.5f,
                TryToBond = false,
                ObjCellId = 0x00010001,
                OriginX = 1.0f,
                OriginY = 1.0f,
                OriginZ = 1.0f,
                AnglesW = 1.0f,
                AnglesX = 0.0f,
                AnglesY = 0.0f,
                AnglesZ = 0.0f,
            });
            biota.BiotaPropertiesEmote.Add(emote);

            biota.BiotaPropertiesCreateList.Add(new BiotaPropertiesCreateList
            {
                Id = 1,
                ObjectId = id,
                DestinationType = 1,
                WeenieClassId = 42,
                StackSize = 1,
                Palette = 0,
                Shade = 0f,
                TryToBond = false,
            });

            biota.BiotaPropertiesBook = new BiotaPropertiesBook { ObjectId = id, MaxNumPages = 10, MaxNumCharsPerPage = 100 };

            biota.BiotaPropertiesBookPageData.Add(new BiotaPropertiesBookPageData
            {
                Id = 1,
                ObjectId = id,
                PageId = 0,
                AuthorId = 1,
                AuthorName = "Author",
                AuthorAccount = "account",
                IgnoreAuthor = false,
                PageText = "Once upon a time.",
            });

            biota.BiotaPropertiesGenerator.Add(new BiotaPropertiesGenerator
            {
                Id = 1,
                ObjectId = id,
                Probability = 1.0f,
                WeenieClassId = 42,
                Delay = 0f,
                InitCreate = 1,
                MaxCreate = 1,
                WhenCreate = 1,
                WhereCreate = 1,
                StackSize = 1,
                PaletteId = 0,
                Shade = 0f,
                ObjCellId = 0x00010001,
                OriginX = 1.0f,
                OriginY = 1.0f,
                OriginZ = 1.0f,
                AnglesW = 1.0f,
                AnglesX = 0.0f,
                AnglesY = 0.0f,
                AnglesZ = 0.0f,
            });

            biota.BiotaPropertiesPalette.Add(new BiotaPropertiesPalette { Id = 1, ObjectId = id, SubPaletteId = 1, Offset = 0, Length = 1, Order = 0 });
            biota.BiotaPropertiesTextureMap.Add(new BiotaPropertiesTextureMap { Id = 1, ObjectId = id, Index = 0, OldId = 1, NewId = 2, Order = 0 });
            biota.BiotaPropertiesAnimPart.Add(new BiotaPropertiesAnimPart { Id = 1, ObjectId = id, Index = 0, AnimationId = 1, Order = 0 });

            biota.BiotaPropertiesEnchantmentRegistry.Add(new BiotaPropertiesEnchantmentRegistry
            {
                ObjectId = id,
                EnchantmentCategory = 1,
                SpellId = 1,
                LayerId = 1,
                HasSpellSetId = false,
                SpellCategory = 1,
                PowerLevel = 1,
                StartTime = 0.0,
                Duration = 60.0,
                CasterObjectId = id,
                DegradeModifier = 0f,
                DegradeLimit = 0f,
                LastTimeDegraded = 0.0,
                StatModType = 1,
                StatModKey = 1,
                StatModValue = 1.0f,
                SpellSetId = 0,
            });

            return biota;
        }

        private static Character BuildSyntheticCharacter()
        {
            const uint id = 200u;

            var character = new Character
            {
                Id = id,
                AccountId = 1,
                Name = "TestCharacter",
                IsPlussed = false,
                IsDeleted = false,
                DeleteTime = 0,
                LastLoginTimestamp = 0.0,
                TotalLogins = 1,
                CharacterOptions1 = 0,
                CharacterOptions2 = 0,
                GameplayOptions = new byte[] { 1, 2, 3 },
                SpellbookFilters = 0,
                HairTexture = 0,
                DefaultHairTexture = 0,
            };

            character.CharacterPropertiesContractRegistry.Add(new CharacterPropertiesContractRegistry { CharacterId = id, ContractId = 1, DeleteContract = false, SetAsDisplayContract = false });
            character.CharacterPropertiesFillCompBook.Add(new CharacterPropertiesFillCompBook { CharacterId = id, SpellComponentId = 1, QuantityToRebuy = 1 });
            character.CharacterPropertiesFriendList.Add(new CharacterPropertiesFriendList { CharacterId = id, FriendId = 1 });
            character.CharacterPropertiesQuestRegistry.Add(new CharacterPropertiesQuestRegistry { CharacterId = id, QuestName = "TestQuest", LastTimeCompleted = 0, NumTimesCompleted = 1 });
            character.CharacterPropertiesShortcutBar.Add(new CharacterPropertiesShortcutBar { CharacterId = id, ShortcutBarIndex = 0, ShortcutObjectId = 1 });
            character.CharacterPropertiesSpellBar.Add(new CharacterPropertiesSpellBar { CharacterId = id, SpellBarNumber = 0, SpellBarIndex = 0, SpellId = 1 });
            character.CharacterPropertiesTitleBook.Add(new CharacterPropertiesTitleBook { CharacterId = id, TitleId = 1 });

            return character;
        }

        // ---- Parsing "INSERT INTO `table` (`c1`, `c2`, ...)" out of the emitted SQL ----

        private sealed class InsertStatement
        {
            public string Table;
            public List<string> Columns;
        }

        private static readonly Regex InsertRegex = new Regex(
            @"INSERT INTO `([a-z_0-9]+)`\s*\(([^)]*)\)",
            RegexOptions.Compiled);

        private static List<InsertStatement> ParseInsertStatements(string sql)
        {
            var result = new List<InsertStatement>();

            foreach (Match match in InsertRegex.Matches(sql))
            {
                var table = match.Groups[1].Value;
                var columnList = match.Groups[2].Value;

                var columns = new List<string>();
                foreach (var rawColumn in columnList.Split(','))
                {
                    var trimmed = rawColumn.Trim().Trim('`');
                    if (trimmed.Length > 0)
                        columns.Add(trimmed);
                }

                result.Add(new InsertStatement { Table = table, Columns = columns });
            }

            return result;
        }

        private static string DescribeWriter(string table)
        {
            if (table == "character" || table.StartsWith("character_properties_", StringComparison.Ordinal))
                return $"CharacterSQLWriter.CreateSQLINSERTStatement for `{table}`";

            return $"BiotaSQLWriter.CreateSQLINSERTStatement for `{table}`";
        }

        // ---- Same walk-up idiom as TestEnvironment.FindInSourceTree / PropertyRegistryTests.FindInSourceTree ----

        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
