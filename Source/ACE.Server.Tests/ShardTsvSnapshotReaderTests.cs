using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Adapter;
using ACE.Database.Models.Shard;
using ACE.Database.Snapshot;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins ShardTsvSnapshotReader: the mysql batch-format cell rules, the column-name mapping (against headers
    /// spelled exactly as the mysql client prints them for the real shard schema), the navigation wiring, the
    /// possession split, and a generic round trip over every scalar column of every biota and character table.
    /// All fixtures are synthetic; no prod data is involved.
    /// </summary>
    [TestClass]
    public class ShardTsvSnapshotReaderTests
    {
        private const uint CharacterId = 0x50000001;
        private const uint MainPackSword = 0x80000010;
        private const uint SidePack = 0x80000011;
        private const uint ItemInSidePack = 0x80000012;
        private const uint PackInSidePack = 0x80000013;
        private const uint ItemInNestedPack = 0x80000014;
        private const uint WieldedArmor = 0x80000015;
        private const uint Unrelated = 0x80000016;

        private string dir;

        [TestInitialize]
        public void Setup()
        {
            dir = Path.Combine(Path.GetTempPath(), "ace_snapshot_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private void WriteTable(string table, params string[] lines)
        {
            File.WriteAllText(Path.Combine(dir, table + ".tsv"), lines.Length == 0 ? "" : string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        [TestMethod]
        public void ParseCell_FollowsTheMysqlBatchFormat()
        {
            Assert.IsNull(ShardTsvSnapshotReader.ParseCell("NULL", typeof(string)));
            Assert.IsNull(ShardTsvSnapshotReader.ParseCell("NULL", typeof(uint?)));
            Assert.ThrowsExactly<InvalidDataException>(() => ShardTsvSnapshotReader.ParseCell("NULL", typeof(uint)));

            Assert.AreEqual("a\tb\nc\\d", ShardTsvSnapshotReader.ParseCell("a\\tb\\nc\\\\d", typeof(string)));
            Assert.AreEqual("x\0y", ShardTsvSnapshotReader.ParseCell("x\\0y", typeof(string)));

            Assert.AreEqual(true, ShardTsvSnapshotReader.ParseCell("1", typeof(bool)));
            Assert.AreEqual(false, ShardTsvSnapshotReader.ParseCell("0", typeof(bool)));

            CollectionAssert.AreEqual(new byte[] { 0x02, 0x00, 0xFF }, (byte[])ShardTsvSnapshotReader.ParseCell("0200FF", typeof(byte[])));

            Assert.AreEqual(1e-05f, (float)ShardTsvSnapshotReader.ParseCell("1e-05", typeof(float)));
            Assert.AreEqual(1792747093.005634, (double)ShardTsvSnapshotReader.ParseCell("1792747093.005634", typeof(double)), 1e-6);
            Assert.AreEqual(-17205.0, (double)ShardTsvSnapshotReader.ParseCell("-17205", typeof(double)));

            Assert.AreEqual((ushort)65535, ShardTsvSnapshotReader.ParseCell("65535", typeof(ushort)));
            Assert.AreEqual(2147593538u, ShardTsvSnapshotReader.ParseCell("2147593538", typeof(uint)));
            Assert.AreEqual(-666, ShardTsvSnapshotReader.ParseCell("-666", typeof(int)));

            // an integral column must never silently round a fraction or wrap an overflow
            Assert.ThrowsExactly<FormatException>(() => ShardTsvSnapshotReader.ParseCell("1.5", typeof(int)));
            Assert.ThrowsExactly<OverflowException>(() => ShardTsvSnapshotReader.ParseCell("70000", typeof(ushort)));
        }

        /// <summary>
        /// Headers spelled exactly as the mysql client printed them for the prod shard schema, so a drift between
        /// the scaffolded column names and the model would fail here.
        /// </summary>
        private void WriteHandFixture()
        {
            WriteTable("character",
                "id\taccount_Id\tname\tis_Plussed\tis_Deleted\tdelete_Time\tlast_Login_Timestamp\ttotal_Logins\tcharacter_Options_1\tcharacter_Options_2\tgameplay_Options\tspellbook_Filters\thair_Texture\tdefault_Hair_Texture",
                $"{CharacterId}\t18\tFixture\\tName\t0\t0\t0\t1792747093.005634\t257\t2029888834\t45451201\t0200FF\t16383\t0\t0");

            WriteTable("biota",
                "id\tweenie_Class_Id\tweenie_Type\tpopulated_Collection_Flags",
                $"{CharacterId}\t1\t{(int)WeenieType.Creature}\t4294967295",
                $"{MainPackSword}\t3000\t{(int)WeenieType.MeleeWeapon}\t4294967295",
                $"{SidePack}\t136\t{(int)WeenieType.Container}\t4294967295",
                $"{ItemInSidePack}\t3001\t{(int)WeenieType.Generic}\t4294967295",
                $"{PackInSidePack}\t137\t{(int)WeenieType.Container}\t4294967295",
                $"{ItemInNestedPack}\t3002\t{(int)WeenieType.Generic}\t4294967295",
                $"{WieldedArmor}\t3003\t{(int)WeenieType.Clothing}\t4294967295",
                $"{Unrelated}\t3004\t{(int)WeenieType.Generic}\t4294967295");

            WriteTable("biota_properties_i_i_d",
                "object_Id\ttype\tvalue",
                $"{MainPackSword}\t{(int)PropertyInstanceId.Container}\t{CharacterId}",
                $"{SidePack}\t{(int)PropertyInstanceId.Container}\t{CharacterId}",
                $"{ItemInSidePack}\t{(int)PropertyInstanceId.Container}\t{SidePack}",
                $"{PackInSidePack}\t{(int)PropertyInstanceId.Container}\t{SidePack}",
                $"{ItemInNestedPack}\t{(int)PropertyInstanceId.Container}\t{PackInSidePack}",
                $"{WieldedArmor}\t{(int)PropertyInstanceId.Wielder}\t{CharacterId}",
                $"{Unrelated}\t{(int)PropertyInstanceId.Container}\t12345");

            WriteTable("biota_properties_int",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyInt.Level}\t400",
                $"{WieldedArmor}\t{(int)PropertyInt.ArmorLevel}\t650");

            WriteTable("biota_properties_bool",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyBool.Stuck}\t1");

            WriteTable("biota_properties_string",
                "object_Id\ttype\tvalue",
                $"{CharacterId}\t{(int)PropertyString.Name}\tBill\\\\dozer\\nline2");

            WriteTable("biota_properties_skill",
                "object_Id\ttype\tlevel_From_P_P\ts_a_c\tp_p\tinit_Level\tresistance_At_Last_Check\tlast_Used_Time",
                $"{CharacterId}\t{(int)Skill.HeavyWeapons}\t190\t{(int)SkillAdvancementClass.Specialized}\t4100000000\t10\t0\t1792747093.5");

            WriteTable("biota_properties_emote",
                "id\tobject_Id\tcategory\tprobability\tweenie_Class_Id\tstyle\tsubstyle\tquest\tvendor_Type\tmin_Health\tmax_Health",
                $"77\t{CharacterId}\t1\t1\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL");

            WriteTable("biota_properties_emote_action",
                "id\temote_Id\torder\ttype\tdelay\textent\tmotion\tmessage\ttest_String\tmin\tmax\tmin_64\tmax_64\tmin_Dbl\tmax_Dbl\tstat\tdisplay\tamount\tamount_64\thero_X_P_64\tpercent\tspell_Id\twealth_Rating\ttreasure_Class\ttreasure_Type\tp_Script\tsound\tdestination_Type\tweenie_Class_Id\tstack_Size\tpalette\tshade\ttry_To_Bond\tobj_Cell_Id\torigin_X\torigin_Y\torigin_Z\tangles_W\tangles_X\tangles_Y\tangles_Z",
                "501\t77\t0\t1\t0\t1\tNULL\thello\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\t1\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\t0\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL");

            WriteTable("character_properties_quest_registry",
                "character_Id\tquest_Name\tlast_Time_Completed\tnum_Times_Completed",
                $"{CharacterId}\tsomequest\t1700000000\t3");

            // an empty export (mysql prints nothing at all for an empty result)
            WriteTable("biota_properties_book");
        }

        [TestMethod]
        public void Load_MapsColumnsWiresNavigationsAndSplitsPossessions()
        {
            WriteHandFixture();

            var snapshot = ShardTsvSnapshotReader.Load(dir);

            Assert.AreEqual(CharacterId, snapshot.Character.Id);
            Assert.AreEqual(18u, snapshot.Character.AccountId);
            Assert.AreEqual("Fixture\tName", snapshot.Character.Name);
            Assert.AreEqual(257, snapshot.Character.TotalLogins);
            CollectionAssert.AreEqual(new byte[] { 0x02, 0x00, 0xFF }, snapshot.Character.GameplayOptions);
            Assert.AreEqual(1, snapshot.Character.CharacterPropertiesQuestRegistry.Count);
            Assert.AreEqual("somequest", snapshot.Character.CharacterPropertiesQuestRegistry.Single().QuestName);

            var biota = snapshot.CharacterBiota;
            Assert.AreEqual(CharacterId, biota.Id);

            var skill = biota.BiotaPropertiesSkill.Single();
            Assert.AreEqual((ushort)Skill.HeavyWeapons, skill.Type);
            Assert.AreEqual((ushort)190, skill.LevelFromPP);
            Assert.AreEqual((uint)SkillAdvancementClass.Specialized, skill.SAC);
            Assert.AreEqual(4100000000u, skill.PP);
            Assert.AreEqual(10u, skill.InitLevel);

            Assert.AreEqual("Bill\\dozer\nline2", biota.BiotaPropertiesString.Single().Value);
            Assert.IsTrue(biota.BiotaPropertiesBool.Single().Value);

            var emote = biota.BiotaPropertiesEmote.Single();
            Assert.IsNull(emote.WeenieClassId);
            Assert.AreEqual("hello", emote.BiotaPropertiesEmoteAction.Single().Message);
            Assert.IsTrue(emote.BiotaPropertiesEmoteAction.Single().Display);

            // the possession split is GetPossessionsCore's: top level, then exactly one nested Container level
            CollectionAssert.AreEqual(new[] { MainPackSword, SidePack, ItemInSidePack, PackInSidePack }, snapshot.Inventory.Select(b => b.Id).ToArray());
            CollectionAssert.AreEqual(new[] { WieldedArmor }, snapshot.WieldedItems.Select(b => b.Id).ToArray());
            Assert.AreEqual(8, snapshot.Biotas.Count);

            Assert.AreEqual(0, snapshot.RowCounts["biota_properties_book"]);
            Assert.AreEqual(8, snapshot.RowCounts["biota"]);

            // and the EF shape converts to the runtime shape the way the live load path converts it
            var entity = BiotaConverter.ConvertToEntityBiota(biota);
            Assert.AreEqual(400, entity.PropertiesInt[PropertyInt.Level]);
            Assert.AreEqual(SkillAdvancementClass.Specialized, entity.PropertiesSkill[Skill.HeavyWeapons].SAC);
            Assert.AreEqual(190u, entity.PropertiesSkill[Skill.HeavyWeapons].LevelFromPP);
            Assert.AreEqual(1, entity.PropertiesEmote.Count);
            Assert.AreEqual(1, entity.PropertiesEmote.Single().PropertiesEmoteAction.Count);

            var armor = BiotaConverter.ConvertToEntityBiota(snapshot.WieldedItems.Single());
            Assert.AreEqual(650, armor.PropertiesInt[PropertyInt.ArmorLevel]);
            Assert.AreEqual(CharacterId, armor.PropertiesIID[PropertyInstanceId.Wielder]);
        }

        [TestMethod]
        public void Load_IsStrictAboutUnknownTablesColumnsAndFieldCounts()
        {
            WriteHandFixture();
            WriteTable("biota_properties_int", "object_Id\ttype\tvalu", $"{CharacterId}\t25\t400");
            Assert.ThrowsExactly<InvalidDataException>(() => ShardTsvSnapshotReader.Load(dir));

            WriteTable("biota_properties_int", "object_Id\ttype\tvalue", $"{CharacterId}\t25");
            Assert.ThrowsExactly<InvalidDataException>(() => ShardTsvSnapshotReader.Load(dir));

            WriteTable("biota_properties_int", "object_Id\ttype\tvalue");
            WriteTable("not_a_shard_table", "a", "1");
            Assert.ThrowsExactly<InvalidDataException>(() => ShardTsvSnapshotReader.Load(dir));
        }

        [TestMethod]
        public void Load_RequiresACharacterChoiceWhenSeveralRowsArePresent()
        {
            WriteHandFixture();

            WriteTable("character",
                "id\taccount_Id\tname\tis_Plussed\tis_Deleted\tdelete_Time\tlast_Login_Timestamp\ttotal_Logins\tcharacter_Options_1\tcharacter_Options_2\tgameplay_Options\tspellbook_Filters\thair_Texture\tdefault_Hair_Texture",
                $"{CharacterId}\t18\tA\t0\t0\t0\t0\t0\t0\t0\tNULL\t0\t0\t0",
                $"{CharacterId + 1}\t18\tB\t0\t0\t0\t0\t0\t0\t0\tNULL\t0\t0\t0");

            Assert.ThrowsExactly<InvalidDataException>(() => ShardTsvSnapshotReader.Load(dir));

            Assert.AreEqual("A", ShardTsvSnapshotReader.Load(dir, CharacterId).Character.Name);
        }

        /// <summary>
        /// A generic round trip: one row per biota and character table, every scalar column set to a distinct
        /// non-default value of its CLR type, written in the batch format and read back. Exercises every column
        /// name and every parser the real schema needs, so a scaffold change that adds a column type the reader
        /// cannot parse fails here rather than on an export.
        /// </summary>
        [TestMethod]
        public void RoundTrip_EveryScalarColumnOfEveryBiotaAndCharacterTable()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=localhost;database=ace_shard", new MySqlServerVersion(new Version(8, 0, 0)))
                .Options;

            IModel model;

            using (var context = new ShardDbContext(options))
                model = context.Model;

            var written = new Dictionary<string, object>();
            var seed = 1;

            foreach (var entityType in model.GetEntityTypes())
            {
                var table = entityType.GetTableName();

                if (table == null || !(table.StartsWith("biota", StringComparison.Ordinal) || table == "character" || table.StartsWith("character_properties_", StringComparison.Ordinal)))
                    continue;

                var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
                var row = Activator.CreateInstance(entityType.ClrType);
                var header = new List<string>();
                var cells = new List<string>();

                foreach (var property in entityType.GetProperties())
                {
                    var value = SampleValue(property.ClrType, seed++, property, entityType);

                    property.PropertyInfo.SetValue(row, value);

                    header.Add(property.GetColumnName(store));
                    cells.Add(BatchCell(value));
                }

                WriteTable(table, string.Join("\t", header), string.Join("\t", cells));
                written[table] = row;
            }

            Assert.IsTrue(written.Count >= 35, $"expected the 26 biota and 9 character tables, found {written.Count}");

            var snapshot = ShardTsvSnapshotReader.Load(dir, CharacterId);

            Assert.AreEqual(written.Count, snapshot.RowCounts.Count);

            foreach (var entityType in model.GetEntityTypes())
            {
                var table = entityType.GetTableName();

                if (table == null || !written.TryGetValue(table, out var expected))
                    continue;

                var actual = FindLoadedRow(snapshot, entityType.ClrType);

                Assert.IsNotNull(actual, $"{table}: row not reachable from the loaded snapshot");

                foreach (var property in entityType.GetProperties())
                {
                    var e = property.PropertyInfo.GetValue(expected);
                    var a = property.PropertyInfo.GetValue(actual);

                    if (e is byte[] eb)
                        CollectionAssert.AreEqual(eb, (byte[])a, $"{table}.{property.Name}");
                    else
                        Assert.AreEqual(e, a, $"{table}.{property.Name}");
                }
            }
        }

        /// <summary>
        /// Every key column points at the one character or biota id, or at the one emote's id, so the round-trip
        /// rows wire together. Everything else gets a distinct value.
        /// </summary>
        private static object SampleValue(Type clrType, int seed, IProperty property, IEntityType entityType)
        {
            var name = property.Name;

            if (name == "ObjectId" || name == "CharacterId" || name == "AllegianceId" || (name == "Id" && (entityType.ClrType == typeof(Biota) || entityType.ClrType == typeof(Character))))
                return Convert.ChangeType(CharacterId, Nullable.GetUnderlyingType(clrType) ?? clrType, CultureInfo.InvariantCulture);

            if (entityType.ClrType == typeof(BiotaPropertiesEmote) && name == "Id")
                return 4242u;

            if (entityType.ClrType == typeof(BiotaPropertiesEmoteAction) && name == "EmoteId")
                return 4242u;

            var t = Nullable.GetUnderlyingType(clrType) ?? clrType;

            if (t == typeof(string)) return $"s{seed}\twith\\escapes\nand\0nul";
            if (t == typeof(byte[])) return new byte[] { (byte)seed, 0x00, 0xAB };
            if (t == typeof(bool)) return seed % 2 == 0;
            if (t == typeof(float)) return seed + 0.25f;
            if (t == typeof(double)) return seed + 0.125;
            if (t == typeof(DateTime)) return new DateTime(2026, 1, 1, 0, 0, seed % 60);
            if (t == typeof(byte)) return (byte)(seed % 250 + 1);
            if (t == typeof(sbyte)) return (sbyte)(seed % 120 + 1);

            return Convert.ChangeType(seed + 1, t, CultureInfo.InvariantCulture);
        }

        private static string BatchCell(object value)
        {
            switch (value)
            {
                case null: return ShardTsvSnapshotReader.NullToken;
                case bool b: return b ? "1" : "0";
                case byte[] bytes: return Convert.ToHexString(bytes);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case DateTime dt: return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case string s: return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\0", "\\0");
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static object FindLoadedRow(ShardSnapshot snapshot, Type clrType)
        {
            if (clrType == typeof(Character))
                return snapshot.Character;

            if (clrType == typeof(Biota))
                return snapshot.CharacterBiota;

            if (clrType == typeof(BiotaPropertiesEmoteAction))
                return snapshot.CharacterBiota.BiotaPropertiesEmote.SingleOrDefault()?.BiotaPropertiesEmoteAction.SingleOrDefault();

            foreach (var owner in new object[] { snapshot.CharacterBiota, snapshot.Character })
            {
                foreach (var prop in owner.GetType().GetProperties())
                {
                    var value = prop.GetValue(owner);

                    if (value != null && value.GetType() == clrType)
                        return value;

                    if (value is IEnumerable enumerable && !(value is string))
                    {
                        foreach (var item in enumerable)
                        {
                            if (item != null && item.GetType() == clrType)
                                return item;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Smoke test against a REAL export, never committed: set ACE_SIM_SNAPSHOT_DIR to a snapshot directory
        /// and every biota in it must load and convert. Inconclusive when the variable is unset, which is every
        /// normal run and CI.
        /// </summary>
        [TestMethod]
        public void RealSnapshot_LoadsAndConvertsEveryBiota_WhenDirectoryIsProvided()
        {
            var snapshotDir = Environment.GetEnvironmentVariable("ACE_SIM_SNAPSHOT_DIR");

            if (string.IsNullOrWhiteSpace(snapshotDir))
                Assert.Inconclusive("Set ACE_SIM_SNAPSHOT_DIR to a snapshot directory to run this smoke test.");

            var snapshot = ShardTsvSnapshotReader.Load(snapshotDir);

            foreach (var biota in snapshot.Biotas.Values)
                Assert.IsNotNull(BiotaConverter.ConvertToEntityBiota(biota));

            var entity = BiotaConverter.ConvertToEntityBiota(snapshot.CharacterBiota);

            Console.WriteLine($"character 0x{snapshot.Character.Id:X8} {snapshot.Character.Name}: level {(entity.PropertiesInt != null && entity.PropertiesInt.TryGetValue(PropertyInt.Level, out var lvl) ? lvl : 0)}, " +
                              $"{snapshot.Biotas.Count} biotas, inventory {snapshot.Inventory.Count}, wielded {snapshot.WieldedItems.Count}, " +
                              $"skills {entity.PropertiesSkill?.Count}, enchantments {entity.PropertiesEnchantmentRegistry?.Count}");

            foreach (var kvp in snapshot.RowCounts.OrderBy(k => k.Key))
                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
        }
    }
}
