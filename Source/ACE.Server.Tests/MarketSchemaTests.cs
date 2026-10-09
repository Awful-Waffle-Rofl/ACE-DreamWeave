using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

// NOT a plain `using ACE.Server.Managers.Market` - that namespace declares MarketListing and
// MarketTransaction too, and every typeof below would become ambiguous.
using MarketRejectOperation = ACE.Server.Managers.Market.MarketRejectOperation;
using MarketBuyOrderStatus = ACE.Server.Managers.Market.MarketBuyOrderStatus;
using MarketBuyOrderKind = ACE.Server.Managers.Market.MarketBuyOrderKind;

namespace ACE.Server.Tests
{
    /// <summary>Market schema shape - catches an entity/migration mismatch at build time.</summary>
    [TestClass]
    public class MarketSchemaTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Migration(string fileName)
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Updates", "Shard", fileName));

        /// <summary>
        /// The migration with its comments stripped - the SQL the applier actually executes.
        ///
        /// The forbidden-token assertions below have to run against this, not the raw file. A header
        /// that EXPLAINS why the script uses no DELIMITER, or why it is a separate file from the
        /// CREATE TABLE script, contains those words as prose; a raw Contains check then fails the
        /// migration for documenting itself, which teaches the next author to write a thinner header.
        /// </summary>
        private static string Statements(string fileName)
        {
            var sql = Migration(fileName);

            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"(?m)--.*$", string.Empty);

            return sql;
        }

        private const string ListingFile = "2026-08-30-00-Add-Market-Listing.sql";
        private const string TransactionFile = "2026-08-30-01-Add-Market-Transaction.sql";
        private const string RejectedAttemptFile = "2026-09-03-00-Add-Market-Rejected-Attempt.sql";
        private const string InvestigationIndexFile = "2026-09-03-01-Market-Transaction-Investigation-Indexes.sql";
        private const string BuyOrderFile = "2026-09-05-00-Add-Market-Buy-Order.sql";
        private const string TransactionBuyOrderFile = "2026-09-05-01-Market-Transaction-Buy-Order-Id.sql";
        private const string BuyOrderKindFile = "2026-09-07-00-Market-Buy-Order-Kind.sql";
        private const string ClassKeyFile = "2026-09-27-00-Market-Listing-And-Barrel-Class-Key.sql";

        /// <summary>Every market migration's executable SQL, so a new one cannot skip these rules.</summary>
        private static string[] AllMarketStatements() => new[]
        {
            Statements(ListingFile),
            Statements(TransactionFile),
            Statements(RejectedAttemptFile),
            Statements(InvestigationIndexFile),
            Statements(BuyOrderFile),
            Statements(TransactionBuyOrderFile),
            Statements(BuyOrderKindFile),
            Statements(ClassKeyFile),
        };

        [TestMethod]
        public void Migrations_CreateEveryTable_Idempotently()
        {
            StringAssert.Contains(Migration(ListingFile), "CREATE TABLE IF NOT EXISTS `market_listing`");
            StringAssert.Contains(Migration(TransactionFile), "CREATE TABLE IF NOT EXISTS `market_transaction`");
            StringAssert.Contains(Migration(RejectedAttemptFile), "CREATE TABLE IF NOT EXISTS `market_rejected_attempt`");
            StringAssert.Contains(Migration(BuyOrderFile), "CREATE TABLE IF NOT EXISTS `market_buy_order`");

            foreach (var sql in AllMarketStatements())
            {
                Assert.IsFalse(sql.Contains("DROP TABLE"), "a shard migration must never DROP");
                Assert.IsFalse(Regex.IsMatch(sql, @"CREATE TABLE\s+`"), "every CREATE TABLE must be IF NOT EXISTS");
                Assert.IsFalse(sql.Contains("DELIMITER"), "no DELIMITER: the applier splits on semicolons");
                Assert.IsFalse(Regex.IsMatch(sql, @"(?<!@)@[A-Za-z_]"), "no @-variables in a shard migration");
                Assert.IsFalse(sql.Contains("FOREIGN KEY"), "market rows must survive character deletion");
            }
        }

        [TestMethod]
        public void RejectedAttempt_CarriesTheInvestigationIndexes()
        {
            var sql = Migration(RejectedAttemptFile);

            // The four access paths /marketadmin rejects offers. Without these the table is a scan.
            StringAssert.Contains(sql, "KEY `market_rejected_attempt_account_time_idx` (`account_Id`, `timestamp`)");
            StringAssert.Contains(sql, "KEY `market_rejected_attempt_time_idx` (`timestamp`)");
            StringAssert.Contains(sql, "KEY `market_rejected_attempt_listing_idx` (`listing_Id`)");
            StringAssert.Contains(sql, "KEY `market_rejected_attempt_reason_time_idx` (`reason_Code`, `timestamp`)");

            // reason_Code stores the wire-code STRING, never the MarketError ordinal.
            StringAssert.Contains(sql, "`reason_Code`      varchar(32)   NOT NULL");
        }

        /// <summary>
        /// The header of the rejected-attempt migration has to say what the table deliberately does
        /// NOT record, because an investigator reading an empty result set otherwise cannot tell
        /// "nothing happened" from "this class of event is not recorded here".
        /// </summary>
        [TestMethod]
        public void RejectedAttempt_HeaderStatesWhatIsNotRecorded()
        {
            var sql = Migration(RejectedAttemptFile);

            StringAssert.Contains(sql, "MarketError.Disabled");
            StringAssert.Contains(sql, "MarketApiHost");
            StringAssert.Contains(sql, "market_transaction");
        }

        /// <summary>
        /// MySQL 8.0 has no CREATE INDEX IF NOT EXISTS, and the boot patcher marks a script applied
        /// even when it threw - so an unguarded ADD INDEX is a permanent failure on any shard that
        /// already has the index, with no retry. Every one must sit behind its own guard.
        /// </summary>
        [TestMethod]
        public void InvestigationIndexes_EveryAddIndexIsGuarded()
        {
            var sql = Migration(InvestigationIndexFile);

            var added = Regex.Matches(sql, @"ADD INDEX `(?<name>[A-Za-z0-9_]+)`")
                             .Cast<Match>()
                             .Select(m => m.Groups["name"].Value)
                             .ToList();

            Assert.AreEqual(3, added.Count, "expected exactly the three composite indexes this file adds");

            foreach (var name in added)
            {
                StringAssert.Contains(sql, $"`INDEX_NAME` = '{name}'",
                    $"ADD INDEX {name} is not guarded by an information_schema check");
            }

            StringAssert.Contains(sql, "information_schema.`STATISTICS`");

            // A separate file rather than an edit to the CREATE TABLE script, which any shard that
            // already applied it would silently skip.
            Assert.IsFalse(Statements(InvestigationIndexFile).Contains("CREATE TABLE"),
                "the index migration must not create the table");
        }

        [TestMethod]
        public void Listing_PartialUniqueEmulation_IsPresent()
        {
            var sql = Migration(ListingFile);

            // NULL-is-distinct in a UNIQUE index emulates "one Active listing per item" - DESIGN 5.1.
            StringAssert.Contains(sql, "UNIQUE KEY `market_listing_active_item_uidx` (`active_Item_Guid`)");
            StringAssert.Contains(sql, "UNIQUE KEY `market_listing_active_ledger_uidx` (`seller_Account_Id`, `active_Ledger_Wcid`)");
        }

        [TestMethod]
        public void BuyOrder_PartialUniqueEmulation_IsPresent()
        {
            StringAssert.Contains(Migration(BuyOrderFile), "UNIQUE KEY `market_buy_order_active_uidx` (`buyer_Account_Id`, `active_Material`)");
            StringAssert.Contains(Migration(BuyOrderFile), "KEY `market_buy_order_expires_idx` (`expires_At`)");
        }

        /// <summary>
        /// The kind migration REPLACES the unique key rather than adding a second one, in TWO separately
        /// guarded ALTERs - a DROP guarded on the old key's shape and an ADD guarded on the new key's
        /// absence - never one combined drop-and-re-add of the same index name, whose acceptance across
        /// server versions this repo cannot test.
        ///
        /// Every DDL must be guarded through information_schema. Since #830 a throwing script is not
        /// recorded in applied_updates.txt and IS retried next boot, but it also STOPS the loop
        /// (Program_DbUpdates.cs:762-766), so one unguarded statement blocks every later Shard update
        /// script on every boot until a human intervenes.
        ///
        /// The key must still span active_Material, which is what keeps it off closed rows: a key of
        /// (buyer_Account_Id, order_Kind) alone would refuse a buyer's SECOND ever bag order.
        /// </summary>
        [TestMethod]
        public void BuyOrderKind_ReplacesTheUniqueKey_Guarded()
        {
            var sql = Statements(BuyOrderKindFile);

            StringAssert.Contains(sql, "ADD COLUMN `order_Kind` tinyint unsigned NOT NULL DEFAULT 0");
            StringAssert.Contains(sql, "`COLUMN_NAME` = 'order_Kind'");
            StringAssert.Contains(sql, "information_schema.`COLUMNS`");
            StringAssert.Contains(sql, "information_schema.`STATISTICS`");

            StringAssert.Contains(sql, "DROP INDEX `market_buy_order_active_uidx`");
            StringAssert.Contains(sql, "ADD UNIQUE KEY `market_buy_order_active_uidx` (`buyer_Account_Id`, `active_Material`, `order_Kind`)");

            // The DROP and the ADD must be SEPARATE statements. A trailing comma after the DROP is
            // the signature of the combined "drop and re-add the same index name in one ALTER" form,
            // whose acceptance across server versions this repo has no way to test.
            Assert.IsFalse(Regex.IsMatch(sql, @"DROP INDEX `market_buy_order_active_uidx`\s*,"),
                "the drop and the add must be two separately guarded ALTERs, not one combined statement");

            // Three guards, one per DDL: the column, the old key's shape, the new key's absence.
            Assert.AreEqual(3, Regex.Matches(sql, @"\bALTER TABLE\b").Count, "expected exactly the three guarded ALTERs this file performs");
            Assert.AreEqual(3, Regex.Matches(sql, @"\bIF (NOT )?EXISTS\s*\(").Count, "every ALTER must sit behind its own information_schema guard");

            Assert.IsFalse(sql.Contains("CREATE TABLE"), "the kind migration must not create the table");
            Assert.IsFalse(sql.Contains("active_Material` int NOT NULL"), "active_Material must stay nullable: NULL is what frees the key");
        }

        [TestMethod]
        public void TransactionBuyOrderId_IsGuardedThroughInformationSchema()
        {
            var sql = Statements(TransactionBuyOrderFile);
            StringAssert.Contains(sql, "information_schema.`COLUMNS`");
            StringAssert.Contains(sql, "information_schema.`STATISTICS`");
            StringAssert.Contains(sql, "ADD COLUMN `buy_order_Id` int unsigned NULL");
            StringAssert.Contains(sql, "ADD INDEX `market_transaction_buy_order_idx` (`buy_order_Id`)");
            Assert.IsFalse(sql.Contains("ADD COLUMN `buy_order_Id` int unsigned NOT NULL"), "a fill marker must be nullable");
        }

        [TestMethod]
        public void Entities_ExposeEveryColumnTheManagerNeeds()
        {
            AssertHasProperties(typeof(MarketListing),
                "Id", "SellerAccountId", "SellerCharacterGuid", "SellerCharacterName", "ItemGuid",
                "ActiveItemGuid", "ActiveLedgerWcid", "Wcid", "Count", "PriceMmd", "Status",
                "CreatedAt", "ClosedAt", "SnapshotJson", "ClassKey", "ActiveClassKey");

            AssertHasProperties(typeof(MarketTransaction),
                "Id", "ListingId", "BuyerAccountId", "BuyerCharacterGuid", "BuyerCharacterName",
                "SellerAccountId", "SellerCharacterGuid", "SellerCharacterName", "Wcid", "ItemName",
                "Count", "PriceMmdTotal", "Timestamp", "Channel", "Status", "BuyOrderId");

            AssertHasProperties(typeof(MarketRejectedAttempt),
                "Id", "Operation", "ReasonCode", "AccountId", "CharacterGuid", "CharacterName",
                "ListingId", "ItemGuid", "Wcid", "Count", "PriceMmd", "Channel", "Timestamp", "Detail");

            AssertHasProperties(typeof(MarketBuyOrder),
                "Id", "BuyerAccountId", "BuyerCharacterGuid", "BuyerCharacterName", "MaterialType", "OrderKind", "Wcid",
                "PriceMmd", "CountTotal", "CountRemaining", "EscrowMmd", "Status", "ActiveMaterial",
                "CreatedAt", "ExpiresAt", "ClosedAt");
            Assert.AreEqual(typeof(int?), typeof(MarketBuyOrder).GetProperty("ActiveMaterial").PropertyType, "ActiveMaterial must stay nullable: NULL is what frees the UNIQUE key");
            Assert.AreEqual(typeof(uint?), typeof(MarketTransaction).GetProperty("BuyOrderId").PropertyType);
        }

        /// <summary>
        /// The class-line migration: five nullable columns across two tables and one unique key, every
        /// DDL behind its own information_schema guard. The key must span active_Class_Key (NULL off
        /// Active) so a seller's closed class listings never collide with a new one.
        /// </summary>
        [TestMethod]
        public void ClassKey_ColumnsAndUniqueKey_AreGuardedAndNullable()
        {
            var sql = Statements(ClassKeyFile);

            StringAssert.Contains(sql, "ADD COLUMN `class_Key` char(32) NULL");
            StringAssert.Contains(sql, "ADD COLUMN `active_Class_Key` char(32) NULL");
            StringAssert.Contains(sql, "ADD UNIQUE KEY `market_listing_active_class_uidx` (`seller_Account_Id`, `active_Class_Key`)");
            StringAssert.Contains(sql, "ADD COLUMN `canonical_Form` text NULL");
            StringAssert.Contains(sql, "ADD COLUMN `value` bigint NULL");

            foreach (var column in new[] { "class_Key", "active_Class_Key", "canonical_Form", "value" })
                StringAssert.Contains(sql, $"`COLUMN_NAME` = '{column}'", $"ADD COLUMN {column} is not guarded");

            StringAssert.Contains(sql, "`INDEX_NAME` = 'market_listing_active_class_uidx'");

            // class_Key is added to BOTH tables, so its guard must name each table.
            StringAssert.Contains(sql, "`TABLE_NAME` = 'market_listing'");
            StringAssert.Contains(sql, "`TABLE_NAME` = 'account_vault_barrel'");

            Assert.AreEqual(6, Regex.Matches(sql, @"\bALTER TABLE\b").Count, "expected exactly the six guarded ALTERs this file performs");
            Assert.AreEqual(6, Regex.Matches(sql, @"\bIF NOT EXISTS\s*\(").Count, "every ALTER must sit behind its own information_schema guard");
            Assert.AreEqual(6, Regex.Matches(sql, @"\bCALL\b").Count);

            Assert.IsFalse(Regex.IsMatch(sql, @"(class_Key|active_Class_Key|canonical_Form|`value`)`?\s+\w+(\(\d+\))?\s+NOT NULL"),
                "every new column must be nullable: NULL is what every pre-existing row and every non-class row carries");
            Assert.IsFalse(sql.Contains("CREATE TABLE"), "the class-key migration must not create a table");
        }

        [TestMethod]
        public void ClassKey_EfModel_MapsTheNewColumnsAndTheUniqueIndex()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            using var ctx = new ShardDbContext(options);

            var listing = ctx.Model.FindEntityType(typeof(MarketListing));
            Assert.AreEqual("class_Key", listing.FindProperty("ClassKey").GetColumnName());
            Assert.AreEqual("active_Class_Key", listing.FindProperty("ActiveClassKey").GetColumnName());

            var classIndex = listing.GetIndexes().Single(i => i.GetDatabaseName() == "market_listing_active_class_uidx");
            Assert.IsTrue(classIndex.IsUnique);
            CollectionAssert.AreEqual(new[] { "SellerAccountId", "ActiveClassKey" }, classIndex.Properties.Select(p => p.Name).ToArray());

            var barrel = ctx.Model.FindEntityType(typeof(AccountVaultBarrel));
            Assert.AreEqual("class_Key", barrel.FindProperty("ClassKey").GetColumnName());
            Assert.AreEqual("canonical_Form", barrel.FindProperty("CanonicalForm").GetColumnName());
            Assert.AreEqual("value", barrel.FindProperty("Value").GetColumnName());
            Assert.AreEqual(typeof(long?), typeof(AccountVaultBarrel).GetProperty("Value").PropertyType, "NULL means not a class barreling, never 0");
        }

        /// <summary>
        /// The three columns whose C# type must stay nullable, because "no listing", "not a stored
        /// item" and "no detail" are all real states a non-nullable column would flatten to 0 or "".
        /// </summary>
        [TestMethod]
        public void RejectedAttempt_NullableColumnsStayNullable()
        {
            foreach (var name in new[] { "ListingId", "ItemGuid" })
            {
                var type = typeof(MarketRejectedAttempt).GetProperty(name).PropertyType;
                Assert.AreEqual(typeof(uint?), type, $"MarketRejectedAttempt.{name} must stay nullable");
            }

            Assert.AreEqual(typeof(string), typeof(MarketRejectedAttempt).GetProperty("Detail").PropertyType);
        }

        /// <summary>The values ARE the operation column, so a renumbering would silently rewrite history.</summary>
        [TestMethod]
        public void RejectOperation_ValuesAreTheColumn()
        {
            Assert.AreEqual(0, (int)MarketRejectOperation.List);
            Assert.AreEqual(1, (int)MarketRejectOperation.Buy);
            Assert.AreEqual(2, (int)MarketRejectOperation.Delist);
            Assert.AreEqual(3, (int)MarketRejectOperation.PlaceOrder);
            Assert.AreEqual(4, (int)MarketRejectOperation.FillOrder);
            Assert.AreEqual(5, (int)MarketRejectOperation.CancelOrder);
        }

        /// <summary>The values ARE the market_buy_order.status column, so a renumbering would silently rewrite history.</summary>
        [TestMethod]
        public void BuyOrderStatus_ValuesAreTheColumn()
        {
            Assert.AreEqual(0, (int)MarketBuyOrderStatus.Pending);
            Assert.AreEqual(1, (int)MarketBuyOrderStatus.Active);
            Assert.AreEqual(2, (int)MarketBuyOrderStatus.Filled);
            Assert.AreEqual(3, (int)MarketBuyOrderStatus.Cancelled);
            Assert.AreEqual(4, (int)MarketBuyOrderStatus.Expired);
            Assert.AreEqual(5, (int)MarketBuyOrderStatus.Failed);
            Assert.AreEqual(6, (int)MarketBuyOrderStatus.DebitLedgerUnknown);
        }

        /// <summary>
        /// The values ARE the market_buy_order.order_Kind column. SalvageBag must stay 0: every row
        /// written before the column existed reads 0, and renumbering would reinterpret every one of
        /// them as a hammer order.
        /// </summary>
        [TestMethod]
        public void BuyOrderKind_ValuesAreTheColumn()
        {
            Assert.AreEqual(0, (int)MarketBuyOrderKind.SalvageBag);
            Assert.AreEqual(1, (int)MarketBuyOrderKind.SalvageHammer);
            Assert.AreEqual(MarketBuyOrderKind.SalvageBag, default(MarketBuyOrderKind), "the CLR default must be the pre-existing meaning");
        }

        private static void AssertHasProperties(Type type, params string[] names)
        {
            foreach (var name in names)
                Assert.IsNotNull(type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance),
                    $"{type.Name} is missing {name}");
        }
    }
}
