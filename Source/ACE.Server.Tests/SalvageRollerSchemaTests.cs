using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Salvage Roller schema shape (Docs/SalvageRoller/DESIGN.md 5.2, 5.7, 5.9): the three tables created by
    /// Database/Updates/Shard/2026-11-02-00-Add-Salvage-Roller.sql, their EF mapping, the stored enum
    /// values, the orphan-purge exemption and the character-purge hook.
    ///
    /// Modelled on AccountCapacityUpgradeSchemaTests: the SQL is checked as text and the EF model is built
    /// offline, because ACE.Server.Tests has no database. That the SQL applies twice against a real MySQL,
    /// and that the purge hook commits against one, stay manual steps.
    /// </summary>
    [TestClass]
    public class SalvageRollerSchemaTests
    {
        private const string MigrationPath = "Database/Updates/Shard/2026-11-02-00-Add-Salvage-Roller.sql";

        private static readonly Dictionary<string, string> BatchColumns = new Dictionary<string, string>
        {
            { "Id",          "id" },
            { "CharacterId", "character_Id" },
            { "AccountId",   "account_Id" },
            { "ItemGuid",    "item_Guid" },
            { "ItemWcid",    "item_Wcid" },
            { "Action",      "action" },
            { "State",       "state" },
            { "ChosenSeq",   "chosen_Seq" },
            { "RollCount",   "roll_Count" },
            { "CreatedAt",   "created_At" },
            { "Payload",     "payload" },
        };

        private static readonly Dictionary<string, string> ItemColumns = new Dictionary<string, string>
        {
            { "Guid",    "guid" },
            { "BatchId", "batch_Id" },
            { "Role",    "role" },
        };

        private static readonly Dictionary<string, string> AuditColumns = new Dictionary<string, string>
        {
            { "Id",            "id" },
            { "BatchId",       "batch_Id" },
            { "CharacterId",   "character_Id" },
            { "AccountId",     "account_Id" },
            { "CharacterName", "character_Name" },
            { "Event",         "event" },
            { "ItemGuid",      "item_Guid" },
            { "CreatedAt",     "created_At" },
            { "Detail",        "detail" },
        };

        private static string Statements()
        {
            var sql = PooledLootSourceText.Read(MigrationPath);
            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(sql, @"(?m)--.*$", string.Empty);
        }

        private static ShardDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            return new ShardDbContext(options);
        }

        // ---- the migration ----

        [TestMethod]
        public void Migration_CreatesAllThreeTables_Idempotently()
        {
            var statements = Statements();

            StringAssert.Contains(statements, "CREATE TABLE IF NOT EXISTS `character_salvage_batch` (");
            StringAssert.Contains(statements, "CREATE TABLE IF NOT EXISTS `character_salvage_batch_item` (");
            StringAssert.Contains(statements, "CREATE TABLE IF NOT EXISTS `salvage_roller_audit` (");

            Assert.IsFalse(statements.Contains("DROP"), "a shard migration must never DROP");
            Assert.IsFalse(Regex.IsMatch(statements, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
            // DELIMITER is a mysql client command, not SQL: the applier sends the whole file to the server as ONE
            // MySqlCommand (Program_DbUpdates.PatchDatabase), which would reject it
            Assert.IsFalse(statements.Contains("DELIMITER"), "no DELIMITER: the applier is not the mysql client");
            Assert.IsFalse(statements.Contains('@'), "no session variables in this migration");
        }

        [TestMethod]
        public void Migration_DeclaresNoForeignKey()
        {
            // Matches the account_vault tables. The character purge deletes these rows explicitly in the same
            // SaveChanges as the character, so nothing relies on a cascade.
            Assert.IsFalse(Statements().Contains("FOREIGN KEY"));
        }

        [TestMethod]
        public void Migration_KeysEnforceOneBatchPerCharacterAndOneRowPerGuid()
        {
            var sql = Statements();

            StringAssert.Contains(sql, "UNIQUE KEY `character_salvage_batch_character_uidx` (`character_Id`)");
            StringAssert.Contains(sql, "PRIMARY KEY (`guid`)");
            StringAssert.Contains(sql, "KEY `character_salvage_batch_item_batch_idx` (`batch_Id`)");
            StringAssert.Contains(sql, "KEY `salvage_roller_audit_character_idx` (`character_Id`)");
            StringAssert.Contains(sql, "KEY `salvage_roller_audit_item_idx` (`item_Guid`)");

            Assert.IsTrue(Regex.IsMatch(sql, @"`payload`\s+mediumtext\s+NULL"), "payload is NULL until Pending");
            Assert.IsTrue(Regex.IsMatch(sql, @"`detail`\s+mediumtext\s+NOT NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`chosen_Seq`\s+int\s+NULL"));
            Assert.IsTrue(Regex.IsMatch(sql, @"`batch_Id`\s+bigint unsigned\s+NULL"), "an audit row may predate its batch row");
            Assert.IsTrue(Regex.IsMatch(sql, @"`state`\s+tinyint unsigned\s+NOT NULL DEFAULT 0"), "a new row is Escrowing");
        }

        // ---- the entities and the EF model ----

        [TestMethod]
        public void Entities_PropertiesMatchTheColumnMaps()
        {
            var sql = Statements();

            AssertColumns(typeof(CharacterSalvageBatch), BatchColumns, sql, ignore: "Items");
            AssertColumns(typeof(CharacterSalvageBatchItem), ItemColumns, sql);
            AssertColumns(typeof(SalvageRollerAudit), AuditColumns, sql);

            foreach (var set in new[] { "CharacterSalvageBatch", "CharacterSalvageBatchItem", "SalvageRollerAudit" })
                Assert.IsNotNull(typeof(ShardDbContext).GetProperty(set, BindingFlags.Public | BindingFlags.Instance), $"ShardDbContext.{set}");
        }

        [TestMethod]
        public void EfModel_MapsEveryEntityToItsTableAndColumns()
        {
            using var context = CreateContext();

            AssertMapped(context, typeof(CharacterSalvageBatch), "character_salvage_batch", BatchColumns);
            AssertMapped(context, typeof(CharacterSalvageBatchItem), "character_salvage_batch_item", ItemColumns);
            AssertMapped(context, typeof(SalvageRollerAudit), "salvage_roller_audit", AuditColumns);
        }

        [TestMethod]
        public void EfModel_BatchItemsHangOffTheBatchByBatchId()
        {
            // The navigation is what lets AddSalvageBatch insert the batch and its items in ONE SaveChanges.
            using var context = CreateContext();

            var batch = context.Model.FindEntityType(typeof(CharacterSalvageBatch));
            var navigation = batch.FindNavigation(nameof(CharacterSalvageBatch.Items));

            Assert.IsNotNull(navigation, "CharacterSalvageBatch.Items must be a mapped navigation");
            Assert.AreEqual(typeof(CharacterSalvageBatchItem), navigation.TargetEntityType.ClrType);
            Assert.AreEqual(nameof(CharacterSalvageBatchItem.BatchId), navigation.ForeignKey.Properties.Single().Name);

            var guid = context.Model.FindEntityType(typeof(CharacterSalvageBatchItem)).FindProperty(nameof(CharacterSalvageBatchItem.Guid));
            Assert.AreEqual(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, guid.ValueGenerated, "the item key is the object's own guid, never generated");
        }

        // ---- stored enum values ----

        [TestMethod]
        public void StoredEnums_KeepTheirNumericValues()
        {
            // Renumbering any of these silently rewrites the meaning of every row already written. Pinned by
            // NAME -> value, read through Enum.Parse, so each row is a real lookup rather than a constant fold.
            var pinned = new (Type Enum, string Name, int Value)[]
            {
                (typeof(SalvageBatchState), "Escrowing", 0),
                (typeof(SalvageBatchState), "Pending", 1),
                (typeof(SalvageBatchState), "Taking", 2),

                (typeof(SalvageBatchItemRole), "Target", 0),
                (typeof(SalvageBatchItemRole), "Hammer", 1),

                (typeof(SalvageBatchAction), "TourmalineReroll", 1),
                (typeof(SalvageBatchAction), "AmethystSwap", 2),
                (typeof(SalvageBatchAction), "ObsidianConvert", 3),
                (typeof(SalvageBatchAction), "ObsidianReroll", 4),
                (typeof(SalvageBatchAction), "SerpentineReroll", 5),
                (typeof(SalvageBatchAction), "GreenJadeSwap", 6),

                (typeof(SalvageRollerAuditEvent), "Sold", 1),
                (typeof(SalvageRollerAuditEvent), "Escrowed", 2),
                (typeof(SalvageRollerAuditEvent), "Aborted", 3),
                (typeof(SalvageRollerAuditEvent), "Rolled", 4),
                (typeof(SalvageRollerAuditEvent), "HammersConsumed", 5),
                (typeof(SalvageRollerAuditEvent), "Taken", 6),
                (typeof(SalvageRollerAuditEvent), "ReconcileAbort", 7),
                (typeof(SalvageRollerAuditEvent), "ReconcileForward", 8),
                (typeof(SalvageRollerAuditEvent), "DeliveryFailed", 9),
                (typeof(SalvageRollerAuditEvent), "CharacterPurged", 10),
                (typeof(SalvageRollerAuditEvent), "Error", 11),
            };

            foreach (var (enumType, name, value) in pinned)
                Assert.AreEqual(value, Convert.ToInt32(Enum.Parse(enumType, name)), $"{enumType.Name}.{name}");

            // And nothing was added to an enum without a pin here.
            foreach (var enumType in pinned.Select(p => p.Enum).Distinct())
            {
                var unpinned = Enum.GetNames(enumType).Where(n => n != "None" && !pinned.Any(p => p.Enum == enumType && p.Name == n)).ToList();
                Assert.AreEqual(0, unpinned.Count, $"{enumType.Name} has unpinned member(s): {string.Join(", ", unpinned)}");
            }
        }

        [TestMethod]
        public void MigrationComments_DescribeTheSameStateAndActionValues()
        {
            var raw = PooledLootSourceText.Read(MigrationPath);

            StringAssert.Contains(raw, "'0 Escrowing, 1 Pending, 2 Taking'");
            StringAssert.Contains(raw, "'0 Target, 1 Hammer'");
            StringAssert.Contains(raw, "'1 TourmalineReroll, 2 AmethystSwap, 3 ObsidianConvert, 4 ObsidianReroll, 5 SerpentineReroll'");
        }

        // ---- the orphan purge and the character purge ----

        [TestMethod]
        public void OrphanPurge_KeepTestExemptsEverySalvageBatchItem()
        {
            var file = PooledLootSourceText.Read("Source/ACE.Database/ShardDatabaseOfflineTools.cs");

            var blockStart = file.IndexOf("// Purge biotas that don't have a parent Container, Wielder or Location", StringComparison.Ordinal);
            var blockEnd = file.IndexOf("// Purge allegiances biotas", StringComparison.Ordinal);

            Assert.IsTrue(blockStart >= 0 && blockEnd > blockStart, "the no-parent purge block moved; re-anchor this test");

            var block = file.Substring(blockStart, blockEnd - blockStart);

            StringAssert.Contains(block, "context.CharacterSalvageBatchItem.AsNoTracking().Select(i => i.Guid).ToHashSet()");

            var exemption = block.IndexOf("if (salvageBatchItems.Contains(kvp.Key))", StringComparison.Ordinal);
            var keepTest = block.IndexOf("results.Add(kvp.Key);", StringComparison.Ordinal);

            Assert.IsTrue(exemption >= 0, "the keep-test must skip every batch item guid");
            Assert.IsTrue(exemption < keepTest, "the exemption must run before the biota is added to the purge list");

            // FAILS CLOSED: a read that throws leaves salvageBatchItems null, and a null set walks NO biotas -
            // never an empty set that would exempt nothing
            var read = block.IndexOf("salvageBatchItems = context.CharacterSalvageBatchItem", StringComparison.Ordinal);
            var catchAt = block.IndexOf("catch (Exception ex)", read, StringComparison.Ordinal);
            var nulled = block.IndexOf("salvageBatchItems = null;", catchAt, StringComparison.Ordinal);
            var walk = block.IndexOf("foreach (var kvp in biotas)", StringComparison.Ordinal);
            var bail = block.IndexOf("if (salvageBatchItems == null)\r\n                        break;", StringComparison.Ordinal);
            if (bail < 0)
                bail = block.IndexOf("if (salvageBatchItems == null)\n                        break;", StringComparison.Ordinal);
            var firstAdd = block.IndexOf("results.Add(kvp.Key);", StringComparison.Ordinal);

            Assert.IsTrue(read >= 0 && catchAt > read && nulled > catchAt, "the batch item read is wrapped, and a failure nulls the set");
            Assert.IsTrue(walk > nulled && bail > walk && bail < firstAdd, "a failed read must leave the walk before anything is added to the purge list");
            StringAssert.Contains(block.Substring(catchAt, nulled - catchAt), "log.Error(", "a skipped pass is logged at ERROR");
        }

        [TestMethod]
        public void PurgeCharacter_AbandonClearsTheChangeTrackerFirst()
        {
            var file = PooledLootSourceText.Read("Source/ACE.Database/ShardDatabaseOfflineTools.cs");
            var body = PooledLootSourceText.MethodBody(file, "public static void PurgeCharacter(ShardDbContext context, uint characterId,");

            var stage = body.IndexOf("StageSalvageBatchPurge(", StringComparison.Ordinal);
            var catchAt = body.IndexOf("catch (Exception ex)", stage, StringComparison.Ordinal);
            var firstStatement = body.IndexOf(';', catchAt);
            var catchHead = body.Substring(catchAt, firstStatement - catchAt + 1);

            StringAssert.Contains(catchHead, "context.ChangeTracker.Clear();", "the abandon path's FIRST statement drops every staged deletion");
        }

        [TestMethod]
        public void EscrowContainerKind_IsTheSameValueOnBothSides()
        {
            Assert.AreEqual(AccountVaultStore.EscrowContainerKind, ShardDatabaseOfflineTools.SalvageRollerEscrowContainerKind);
        }

        [TestMethod]
        public void PurgeCharacter_StagesTheBatchBeforeItsOneSaveChanges_AndAbandonsOnFailure()
        {
            var file = PooledLootSourceText.Read("Source/ACE.Database/ShardDatabaseOfflineTools.cs");
            var body = PooledLootSourceText.MethodBody(file, "public static void PurgeCharacter(ShardDbContext context, uint characterId,");

            var stage = body.IndexOf("StageSalvageBatchPurge(context, characterId, character, alreadyRemoved, reason)", StringComparison.Ordinal);
            var save = body.IndexOf("context.SaveChanges()", StringComparison.Ordinal);

            Assert.IsTrue(stage >= 0, "PurgeCharacter must stage the Salvage Roller batch");
            Assert.IsTrue(stage < save, "the batch must be staged in the SAME context before the single SaveChanges");
            Assert.AreEqual(1, Regex.Matches(body, @"\.SaveChanges\(\)").Count, "exactly one SaveChanges: the batch purge is atomic with the character");

            // A batch that cannot be read must abandon the purge: the catch returns before the SaveChanges.
            var catchAt = body.IndexOf("catch (Exception ex)", stage, StringComparison.Ordinal);
            var returnAt = body.IndexOf("return;", catchAt, StringComparison.Ordinal);

            Assert.IsTrue(catchAt > stage && returnAt > catchAt && returnAt < save, "a failed batch read must return before SaveChanges");

            // The guids already staged by the inventory and wielded passes are handed over, so nothing is removed twice.
            StringAssert.Contains(body, "new HashSet<uint>(inventoryBiotas.Select(b => b.Id))");
            StringAssert.Contains(body, "alreadyRemoved.UnionWith(wieldedGuids)");
        }

        [TestMethod]
        public void PurgeHook_WritesItsAuditRowAndDeletesEverythingInTheCallersContext()
        {
            var file = PooledLootSourceText.Read("Source/ACE.Database/ShardDatabaseOfflineTools_SalvageRoller.cs");
            var body = PooledLootSourceText.MethodBody(file, "internal static int StageSalvageBatchPurge(");

            StringAssert.Contains(body, "context.SalvageRollerAudit.Add(");
            StringAssert.Contains(body, "Event = (byte)SalvageRollerAuditEvent.CharacterPurged");
            StringAssert.Contains(body, "context.Biota.Remove(biota)");
            StringAssert.Contains(body, "context.CharacterSalvageBatchItem.Remove(item)");
            StringAssert.Contains(body, "context.CharacterSalvageBatch.Remove(batch)");
            StringAssert.Contains(body, "alreadyRemoved.Contains(item.Guid)");

            Assert.IsFalse(body.Contains("SaveChanges"), "the hook stages only; the caller's single SaveChanges commits");
            Assert.IsFalse(body.Contains("new ShardDbContext"), "the hook must use the caller's context");
            Assert.IsFalse(body.Contains(".GetBiota("), "the virtual GetBiota could hand the caller's context to the cache");
        }

        [TestMethod]
        public void LoadUnparented_ReadsAFreshUncachedBiota_AndRefusesOneWithAParentOrALocation()
        {
            // step 7 review F8 and F1(b): reconcile must see what the shard holds now, through a fresh context and the
            // non-virtual read, and must never rehydrate an object something else (a container, a landblock) holds
            var dao = PooledLootSourceText.Read("Source/ACE.Database/ShardDatabase_SalvageRoller.cs");
            var iDao = dao.IndexOf("public Biota GetSalvageRollerBiotaUncached(", StringComparison.Ordinal);
            Assert.IsTrue(iDao >= 0, "GetSalvageRollerBiotaUncached was not found");

            var daoBody = dao.Substring(iDao, dao.IndexOf("public bool TryGetLatestSalvageRollerAuditDetail(", iDao, StringComparison.Ordinal) - iDao);
            StringAssert.Contains(daoBody, "new ShardDbContext()");
            StringAssert.Contains(daoBody, "GetBiotaCore(context, id)");
            Assert.IsFalse(daoBody.Contains(".GetBiota("), "the virtual GetBiota could serve a cached copy");

            var host = PooledLootSourceText.Read("Source/ACE.Server/SalvageRoller/SalvageRollerHost.cs");
            var iHost = host.IndexOf("public ShardBiotaState LookUpShard(", StringComparison.Ordinal);
            Assert.IsTrue(iHost >= 0, "PlayerSalvageRollerHost.LookUpShard was not found");

            var hostBody = host.Substring(iHost, host.IndexOf("public void RemoveShardBiota(", iHost, StringComparison.Ordinal) - iHost);
            StringAssert.Contains(hostBody, "Db.GetSalvageRollerBiotaUncached(guid)");
            Assert.IsFalse(hostBody.Contains(".GetBiota("), "never the cached read");
            StringAssert.Contains(hostBody, "SalvageRollerManager.IsLoose(item, out holder)");
            StringAssert.Contains(hostBody, "return ShardBiotaState.Held;");
            StringAssert.Contains(hostBody, "return ShardBiotaState.Absent;");

            // the shard removal for a Hammer still recorded in escrow is the queued, idempotent one
            StringAssert.Contains(host, "public void RemoveShardBiota(uint guid, Action<bool> landed) => DatabaseManager.Shard.RemoveBiota(guid, landed);");
        }

        [TestMethod]
        public void FindInPack_SearchesTheInventoryAndTheEquippedItems()
        {
            // final review item 2: a result equipped between delivery and reconcile is still the character's
            var host = PooledLootSourceText.Read("Source/ACE.Server/SalvageRoller/SalvageRollerHost.cs");

            StringAssert.Contains(host, "public WorldObject FindInPack(uint guid) => player.FindObject(guid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);");
        }

        [TestMethod]
        public void Startup_InstallsTheSnapshotSerializerBeforeTheCharacterPurge()
        {
            var program = PooledLootSourceText.Read("Source/ACE.Server/Program.cs");

            var install = program.IndexOf("SalvageRollerAuditSnapshot.InstallPurgeSerializer()", StringComparison.Ordinal);
            var purge = program.IndexOf("ShardDatabaseOfflineTools.PurgeCharactersInParallel(", StringComparison.Ordinal);

            Assert.IsTrue(install >= 0, "the serializer must be installed at startup");
            Assert.IsTrue(install < purge, "and before the character purge runs");
        }

        // ---- the audit snapshot ----

        private static ACE.Entity.Models.Biota SampleBiota()
        {
            return new ACE.Entity.Models.Biota
            {
                Id = 0x80001234,
                WeenieClassId = 7001,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemWorkmanship, 8 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Sword" } },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint> { { PropertyInstanceId.AllowedWielder, 0x50002222 } },
            };
        }

        [TestMethod]
        public void AuditSnapshot_WithNoSerializerInstalled_FallsBackToTheWholeBiota()
        {
            var saved = ShardDatabaseOfflineTools.SalvageRollerItemSnapshotSerializer;

            try
            {
                ShardDatabaseOfflineTools.SalvageRollerItemSnapshotSerializer = null;

                var (format, snapshot) = ShardDatabaseOfflineTools.SnapshotForAudit(SampleBiota());

                Assert.AreEqual("ACE.Entity.Models.Biota", format);
                Assert.IsNotNull(snapshot);

                var json = snapshot.ToJsonString();

                StringAssert.Contains(json, "Test Sword");
                StringAssert.Contains(json, "7001");
            }
            finally
            {
                ShardDatabaseOfflineTools.SalvageRollerItemSnapshotSerializer = saved;
            }
        }

        [TestMethod]
        public void AuditSnapshot_UsesTheInstalledFullBiotaSerializer_AndFallsBackWhenItThrows()
        {
            var saved = ShardDatabaseOfflineTools.SalvageRollerItemSnapshotSerializer;

            try
            {
                ACE.Server.SalvageRoller.SalvageRollerAuditSnapshot.InstallPurgeSerializer();

                var (format, snapshot) = ShardDatabaseOfflineTools.SnapshotForAudit(SampleBiota());

                Assert.AreEqual("SalvageRollerFullBiota", format);
                Assert.AreEqual(ACE.Server.SalvageRoller.SalvageRollerAuditSnapshot.Version, (int)snapshot["v"]);
                Assert.AreEqual(0x80001234u, (uint)snapshot["src"]);
                StringAssert.Contains(snapshot.ToJsonString(), "Test Sword");

                // step 7 review F10: the purge's record is the whole biota, so the instance ids DuoGemSnapshot drops survive
                var back = ACE.Server.SalvageRoller.SalvageRollerAuditSnapshot.Deserialize(snapshot);
                Assert.AreEqual(0x50002222u, back.PropertiesIID[PropertyInstanceId.AllowedWielder], "AllowedWielder survives the CharacterPurged snapshot");

                ShardDatabaseOfflineTools.SalvageRollerItemSnapshotSerializer = _ => throw new InvalidOperationException("boom");

                var (fallbackFormat, fallback) = ShardDatabaseOfflineTools.SnapshotForAudit(SampleBiota());

                Assert.AreEqual("ACE.Entity.Models.Biota", fallbackFormat, "a throwing serializer must not cost the audit row its snapshot");
                StringAssert.Contains(fallback.ToJsonString(), "Test Sword");
            }
            finally
            {
                ShardDatabaseOfflineTools.SalvageRollerItemSnapshotSerializer = saved;
            }
        }

        // ---- helpers ----

        private static void AssertColumns(Type entity, Dictionary<string, string> columns, string sql, string ignore = null)
        {
            var clrProperties = entity.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .Where(n => n != ignore)
                .ToList();

            CollectionAssert.AreEquivalent(columns.Keys.ToList(), clrProperties, $"{entity.Name}'s CLR properties and the expected column map have diverged");

            foreach (var column in columns.Values)
                StringAssert.Contains(sql, $"`{column}`", $"{entity.Name}: column {column} is missing from the migration");
        }

        private static void AssertMapped(ShardDbContext context, Type entity, string table, Dictionary<string, string> columns)
        {
            var type = context.Model.FindEntityType(entity);

            Assert.IsNotNull(type, $"{entity.Name} is not in the model - is its Configure method registered in OnModelCreatingPartial?");
            Assert.AreEqual(table, type.GetTableName());

            foreach (var (property, column) in columns)
                Assert.AreEqual(column, type.FindProperty(property)?.GetColumnName(), $"{entity.Name}.{property}");
        }
    }
}
