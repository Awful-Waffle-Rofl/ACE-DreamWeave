using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Round trip of the Salvage Roller DAO (ShardDatabase_SalvageRoller.cs) and its character-purge hook
    /// (ShardDatabaseOfflineTools_SalvageRoller.cs) against a real shard database.
    ///
    /// THIS WRITES ROWS: batches, item rows, audit rows, characters, biotas and an account_vault row, all deleted
    /// again afterwards. Like PvpArenaRoundTripTests it runs ONLY when <see cref="PvpArenaRoundTripTests.OptInVariable"/>
    /// is 1, which is a promise that Config.js's Shard block points at a DISPOSABLE database with
    /// Database/Updates/Shard/2026-11-02-00-Add-Salvage-Roller.sql applied. Without it every test reports
    /// Inconclusive, because TestEnvironment otherwise falls back to a developer's real Config.js.
    ///
    /// Ids are drawn from a random high range per test so parallel or leftover rows cannot collide.
    /// </summary>
    [TestClass]
    public class SalvageRollerRoundTripTests
    {
        private const uint AccountId = 0x7FFFFF01;

        private static ShardDatabase RequireDisposableShard()
        {
            if (Environment.GetEnvironmentVariable(PvpArenaRoundTripTests.OptInVariable) != "1")
                Assert.Inconclusive($"Skipped: set {PvpArenaRoundTripTests.OptInVariable}=1 only when Config.js points at a disposable shard database with the Salvage Roller migration applied. This test writes rows.");

            TestEnvironment.InitializeConfig();

            var shardDb = new ShardDatabase();

            bool reachable;

            try
            {
                reachable = shardDb.Exists(false);
            }
            catch
            {
                reachable = false;
            }

            if (!reachable)
                Assert.Inconclusive("Skipped: the shard database is not reachable with the configured Config.js.");

            return shardDb;
        }

        /// <summary>A block of eight fresh ids in a random high range: id, id+1, ... id+7.</summary>
        private static uint FreshIds() => 0xE0000000u + (uint)new Random().Next(0, 0x01FFFFFE) * 8u;

        private static CharacterSalvageBatch Batch(uint characterId, uint target, params uint[] hammers)
        {
            var batch = new CharacterSalvageBatch
            {
                CharacterId = characterId,
                AccountId = AccountId,
                ItemGuid = target,
                ItemWcid = 12345,
                Action = (byte)SalvageBatchAction.TourmalineReroll,
                State = (byte)SalvageBatchState.Escrowing,
                RollCount = 10 * hammers.Length,
                CreatedAt = DateTime.UtcNow,
            };

            batch.Items.Add(new CharacterSalvageBatchItem { Guid = target, Role = (byte)SalvageBatchItemRole.Target });

            foreach (var hammer in hammers)
                batch.Items.Add(new CharacterSalvageBatchItem { Guid = hammer, Role = (byte)SalvageBatchItemRole.Hammer });

            return batch;
        }

        /// <summary>
        /// Deletes every row a test seeded. account_vault is touched only when <paramref name="vaultContainers"/> names
        /// a row the test seeded there, so a cleanup can never be the step that fails.
        /// </summary>
        private static void Cleanup(IEnumerable<uint> characterIds, IEnumerable<uint> biotaIds, IEnumerable<uint> vaultContainers = null)
        {
            var characters = characterIds.ToList();
            var biotas = biotaIds.ToList();
            var vaults = vaultContainers?.ToList() ?? new List<uint>();

            using var context = new ShardDbContext();

            var batchIds = context.CharacterSalvageBatch.Where(b => characters.Contains(b.CharacterId)).Select(b => b.Id).ToList();

            context.CharacterSalvageBatchItem.Where(i => batchIds.Contains(i.BatchId) || biotas.Contains(i.Guid)).ExecuteDelete();
            context.CharacterSalvageBatch.Where(b => characters.Contains(b.CharacterId)).ExecuteDelete();
            context.SalvageRollerAudit.Where(a => characters.Contains(a.CharacterId)).ExecuteDelete();
            if (vaults.Count > 0)
                context.AccountVault.Where(v => vaults.Contains(v.ContainerGuid)).ExecuteDelete();
            context.BiotaPropertiesIID.Where(p => biotas.Contains(p.ObjectId)).ExecuteDelete();
            context.BiotaPropertiesString.Where(p => biotas.Contains(p.ObjectId)).ExecuteDelete();
            context.Biota.Where(b => biotas.Contains(b.Id)).ExecuteDelete();
            context.Character.Where(c => characters.Contains(c.Id)).ExecuteDelete();
        }

        // ---------------- the DAO ----------------

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Batch_WithTwoItemRows_RoundTrips()
        {
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            try
            {
                Assert.IsTrue(shardDb.TryGetSalvageBatchForCharacter(id, out var none));
                Assert.IsNull(none, "a character with no batch reads as null, not as a failure");

                Assert.IsTrue(shardDb.AddSalvageBatch(Batch(id, id + 1, id + 2)));

                Assert.IsTrue(shardDb.TryGetSalvageBatchForCharacter(id, out var read));
                Assert.IsNotNull(read);
                Assert.AreEqual(AccountId, read.AccountId);
                Assert.AreEqual(id + 1, read.ItemGuid);
                Assert.AreEqual((byte)SalvageBatchState.Escrowing, read.State);
                Assert.AreEqual(10, read.RollCount);
                Assert.IsNull(read.Payload);
                Assert.IsNull(read.ChosenSeq);

                var items = read.Items.OrderBy(i => i.Guid).ToList();
                Assert.AreEqual(2, items.Count, "both item rows landed with the batch's generated id");
                Assert.IsTrue(items.All(i => i.BatchId == read.Id));
                Assert.AreEqual((byte)SalvageBatchItemRole.Target, items[0].Role);
                Assert.AreEqual((byte)SalvageBatchItemRole.Hammer, items[1].Role);
            }
            finally
            {
                Cleanup(new[] { id }, new[] { id + 1, id + 2 });
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void SecondBatch_ForTheSameCharacter_IsRefusedByTheUniqueKey()
        {
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            try
            {
                Assert.IsTrue(shardDb.AddSalvageBatch(Batch(id, id + 1, id + 2)));
                Assert.IsFalse(shardDb.AddSalvageBatch(Batch(id, id + 3, id + 4)), "one batch per character is a database fact");

                using var context = new ShardDbContext();
                Assert.AreEqual(1, context.CharacterSalvageBatch.Count(b => b.CharacterId == id));
                Assert.AreEqual(0, context.CharacterSalvageBatchItem.Count(i => i.Guid == id + 3 || i.Guid == id + 4), "the refused batch's item rows were not inserted either");
            }
            finally
            {
                Cleanup(new[] { id }, new[] { id + 1, id + 2, id + 3, id + 4 });
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void StateMoves_AreGuarded_OneRowThenNone()
        {
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            try
            {
                Assert.IsTrue(shardDb.AddSalvageBatch(Batch(id, id + 1, id + 2)));
                Assert.IsTrue(shardDb.TryGetSalvageBatchForCharacter(id, out var batch));

                Assert.IsTrue(shardDb.TryCommitSalvageBatchPending(batch.Id, "{\"v\":1}"), "Escrowing -> Pending moves exactly one row");
                Assert.IsFalse(shardDb.TryCommitSalvageBatchPending(batch.Id, "{\"v\":1}"), "a repeat finds no Escrowing row and moves none");

                Assert.IsTrue(shardDb.TrySetSalvageBatchTaking(batch.Id, 7));
                Assert.IsFalse(shardDb.TrySetSalvageBatchTaking(batch.Id, 8), "a second pick is refused");

                Assert.IsTrue(shardDb.TryGetSalvageBatchForCharacter(id, out var read));
                Assert.AreEqual((byte)SalvageBatchState.Taking, read.State);
                Assert.AreEqual("{\"v\":1}", read.Payload);
                Assert.AreEqual(7, read.ChosenSeq, "the refused second pick did not overwrite the first");
            }
            finally
            {
                Cleanup(new[] { id }, new[] { id + 1, id + 2 });
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void Deletes_AreIdempotent()
        {
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            try
            {
                Assert.IsTrue(shardDb.AddSalvageBatch(Batch(id, id + 1, id + 2)));
                Assert.IsTrue(shardDb.TryGetSalvageBatchForCharacter(id, out var batch));

                Assert.IsTrue(shardDb.DeleteSalvageBatchItem(id + 2));
                Assert.IsTrue(shardDb.DeleteSalvageBatchItem(id + 2), "deleting a gone item row is still success");

                Assert.IsTrue(shardDb.DeleteSalvageBatch(batch.Id));
                Assert.IsTrue(shardDb.DeleteSalvageBatch(batch.Id), "deleting a gone batch is still success");

                Assert.IsTrue(shardDb.TryGetSalvageBatchForCharacter(id, out var gone));
                Assert.IsNull(gone);

                using var context = new ShardDbContext();
                Assert.AreEqual(0, context.CharacterSalvageBatchItem.Count(i => i.BatchId == batch.Id), "the batch delete took its remaining item row with it");
            }
            finally
            {
                Cleanup(new[] { id }, new[] { id + 1, id + 2 });
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void UncachedBiotaRead_ReturnsEveryCollection_AndNullForNoRow()
        {
            // step 7 review F8: reconcile's fresh read. It must carry the collections (an instance id here), and a row that
            // is not there must read as null, not throw.
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            try
            {
                using (var context = new ShardDbContext())
                {
                    context.Biota.Add(new Biota
                    {
                        Id = id, WeenieClassId = 12345, WeenieType = (int)WeenieType.MeleeWeapon,
                        PopulatedCollectionFlags = (uint)(ShardDatabase.PopulatedCollectionFlags.BiotaPropertiesIID | ShardDatabase.PopulatedCollectionFlags.BiotaPropertiesString),
                    });
                    context.BiotaPropertiesString.Add(new BiotaPropertiesString { ObjectId = id, Type = (ushort)PropertyString.Name, Value = "Uncached Sword" });
                    context.BiotaPropertiesIID.Add(new BiotaPropertiesIID { ObjectId = id, Type = (ushort)PropertyInstanceId.AllowedWielder, Value = 0x50003333 });
                    context.SaveChanges();
                }

                var biota = shardDb.GetSalvageRollerBiotaUncached(id);

                Assert.IsNotNull(biota);
                Assert.AreEqual("Uncached Sword", biota.BiotaPropertiesString.Single().Value);
                Assert.AreEqual(0x50003333u, biota.BiotaPropertiesIID.Single(p => p.Type == (ushort)PropertyInstanceId.AllowedWielder).Value);

                Assert.IsNull(shardDb.GetSalvageRollerBiotaUncached(id + 1), "no row reads as null");
            }
            finally
            {
                Cleanup(Array.Empty<uint>(), new[] { id });
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void LatestAuditDetail_IsTheNewestRowOfThatEventForThatBatch()
        {
            // step 7 review F7: a delivery finished later reads its Taken row back for the pre-take state
            var shardDb = RequireDisposableShard();
            var id = FreshIds();
            var batchId = (ulong)id;

            SalvageRollerAudit Row(SalvageRollerAuditEvent ev, ulong batch, string detail) => new SalvageRollerAudit
            {
                BatchId = batch, CharacterId = id, AccountId = AccountId, CharacterName = "Audit Reader", Event = (byte)ev, ItemGuid = id + 1, CreatedAt = DateTime.UtcNow, Detail = detail,
            };

            try
            {
                Assert.IsTrue(shardDb.TryGetLatestSalvageRollerAuditDetail(batchId, SalvageRollerAuditEvent.Taken, out var none));
                Assert.IsNull(none, "no row reads as null");

                Assert.IsTrue(shardDb.AddSalvageRollerAudit(Row(SalvageRollerAuditEvent.Taken, batchId, "{\"n\":1}")));
                Assert.IsTrue(shardDb.AddSalvageRollerAudit(Row(SalvageRollerAuditEvent.Taken, batchId, "{\"n\":2}")));
                Assert.IsTrue(shardDb.AddSalvageRollerAudit(Row(SalvageRollerAuditEvent.Rolled, batchId, "{\"n\":3}")));
                Assert.IsTrue(shardDb.AddSalvageRollerAudit(Row(SalvageRollerAuditEvent.Taken, batchId + 1, "{\"n\":4}")));

                Assert.IsTrue(shardDb.TryGetLatestSalvageRollerAuditDetail(batchId, SalvageRollerAuditEvent.Taken, out var detail));
                Assert.AreEqual("{\"n\":2}", detail, "the newest Taken row of THIS batch");
            }
            finally
            {
                Cleanup(new[] { id }, Array.Empty<uint>());
            }
        }

        // ---------------- the character purge ----------------

        private static void SeedCharacter(ShardDbContext context, uint id, string name)
        {
            context.Character.Add(new Character { Id = id, AccountId = AccountId, Name = name, IsDeleted = true, DeleteTime = 1 });
            SeedBiota(context, id, WeenieType.Creature, name, null);
        }

        private static void SeedBiota(ShardDbContext context, uint id, WeenieType type, string name, uint? container)
        {
            context.Biota.Add(new Biota { Id = id, WeenieClassId = 12345, WeenieType = (int)type });
            context.BiotaPropertiesString.Add(new BiotaPropertiesString { ObjectId = id, Type = (ushort)PropertyString.Name, Value = name });

            if (container != null)
                context.BiotaPropertiesIID.Add(new BiotaPropertiesIID { ObjectId = id, Type = (ushort)PropertyInstanceId.Container, Value = container.Value });
        }

        private static SalvageRollerAudit SinglePurgeAudit(uint characterId)
        {
            using var context = new ShardDbContext();

            var rows = context.SalvageRollerAudit.AsNoTracking().Where(a => a.CharacterId == characterId).ToList();

            Assert.AreEqual(1, rows.Count, "exactly one audit row for the purge");
            Assert.AreEqual((byte)SalvageRollerAuditEvent.CharacterPurged, rows[0].Event);

            return rows[0];
        }

        private static JsonObject ItemEntry(SalvageRollerAudit audit, uint guid) =>
            JsonNode.Parse(audit.Detail)["items"].AsArray().Select(n => n.AsObject()).Single(o => (uint)o["guid"] == guid);

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void PurgeCharacter_RemovesTheEscrowedTargetHammerAndBatch_AndWritesOneAuditRowWithASnapshot()
        {
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            var character = id;
            var escrow = id + 1;
            var target = id + 2;
            var hammer = id + 3;

            try
            {
                using (var context = new ShardDbContext())
                {
                    SeedCharacter(context, character, "SalvageRollerPurgeTest");
                    SeedBiota(context, escrow, WeenieType.Container, "Account Escrow", null);
                    context.AccountVault.Add(new AccountVault { AccountId = AccountId, ContainerGuid = escrow, Kind = ShardDatabaseOfflineTools.SalvageRollerEscrowContainerKind, CreatedAt = DateTime.UtcNow });
                    SeedBiota(context, target, WeenieType.Generic, "Escrowed Sword", escrow);
                    SeedBiota(context, hammer, WeenieType.Generic, "Parentless Hammer", null);
                    context.SaveChanges();
                }

                Assert.IsTrue(shardDb.AddSalvageBatch(Batch(character, target, hammer)));

                ShardDatabaseOfflineTools.PurgeCharacter(character, out var charactersPurged, out _, out var possessionsPurged, "test purge");

                Assert.AreEqual(1, charactersPurged);
                Assert.AreEqual(2, possessionsPurged, "the escrowed target and the parentless Hammer");

                using (var context = new ShardDbContext())
                {
                    Assert.IsFalse(context.Biota.Any(b => b.Id == target || b.Id == hammer), "both escrowed objects are gone");
                    Assert.IsTrue(context.Biota.Any(b => b.Id == escrow), "the escrow container itself stays");
                    Assert.IsFalse(context.CharacterSalvageBatch.Any(b => b.CharacterId == character));
                    Assert.IsFalse(context.CharacterSalvageBatchItem.Any(i => i.Guid == target || i.Guid == hammer));
                    Assert.IsFalse(context.Character.Any(c => c.Id == character));
                }

                var audit = SinglePurgeAudit(character);
                Assert.AreEqual(target, audit.ItemGuid);

                var entry = ItemEntry(audit, target);
                StringAssert.StartsWith((string)entry["outcome"], "deleted from escrow");
                Assert.IsNotNull(entry["snapshot"], "the audit row carries the item's snapshot");
                Assert.AreEqual("Escrowed Sword", (string)entry["name"]);
                StringAssert.StartsWith((string)ItemEntry(audit, hammer)["outcome"], "deleted from escrow");
            }
            finally
            {
                Cleanup(new[] { character }, new[] { character, escrow, target, hammer }, new[] { escrow });
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void PurgeCharacter_TargetAlreadyInThePack_IsDeletedOnceWithThePossessions()
        {
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            var character = id;
            var target = id + 1;

            try
            {
                using (var context = new ShardDbContext())
                {
                    SeedCharacter(context, character, "SalvageRollerPackTest");
                    SeedBiota(context, target, WeenieType.Generic, "Delivered Sword", character);
                    context.SaveChanges();
                }

                Assert.IsTrue(shardDb.AddSalvageBatch(Batch(character, target)));

                ShardDatabaseOfflineTools.PurgeCharacter(character, out var charactersPurged, out _, out var possessionsPurged, "test purge");

                Assert.AreEqual(1, charactersPurged);
                Assert.AreEqual(1, possessionsPurged, "the target is counted once, by the inventory pass, not again by the batch");

                using (var context = new ShardDbContext())
                {
                    Assert.IsFalse(context.Biota.Any(b => b.Id == target));
                    Assert.IsFalse(context.CharacterSalvageBatch.Any(b => b.CharacterId == character));
                }

                StringAssert.StartsWith((string)ItemEntry(SinglePurgeAudit(character), target)["outcome"], "deleted with the character's own possessions");
            }
            finally
            {
                Cleanup(new[] { character }, new[] { character, target });
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void PurgeCharacter_AnItemThatLeftEscrowForAnotherPack_Survives()
        {
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            var character = id;
            var other = id + 1;
            var traded = id + 2;

            try
            {
                using (var context = new ShardDbContext())
                {
                    SeedCharacter(context, character, "SalvageRollerLeaverTest");
                    SeedCharacter(context, other, "SalvageRollerOtherTest");
                    SeedBiota(context, traded, WeenieType.Generic, "Traded Sword", other);
                    context.SaveChanges();
                }

                // a batch row still naming an object that now sits in ANOTHER character's pack
                Assert.IsTrue(shardDb.AddSalvageBatch(Batch(character, traded)));

                ShardDatabaseOfflineTools.PurgeCharacter(character, out var charactersPurged, out _, out var possessionsPurged, "test purge");

                Assert.AreEqual(1, charactersPurged);
                Assert.AreEqual(0, possessionsPurged);

                using (var context = new ShardDbContext())
                {
                    Assert.IsTrue(context.Biota.Any(b => b.Id == traded), "an object in another character's pack is not this batch's to delete");
                    Assert.IsFalse(context.CharacterSalvageBatch.Any(b => b.CharacterId == character), "the batch row itself still goes");
                }

                Assert.AreEqual($"had left escrow (container 0x{other:X8}); not deleted", (string)ItemEntry(SinglePurgeAudit(character), traded)["outcome"]);
            }
            finally
            {
                Cleanup(new[] { character, other }, new[] { character, other, traded });
            }
        }

        // ---------------- step 5 review F5: an escrow look-alike is not escrow ----------------

        /// <summary>
        /// Seeds a character, a vault container of <paramref name="vaultKind"/> owned by <paramref name="vaultAccount"/>
        /// (when <paramref name="vaultKind"/> is not null), and a target that sits in that container, or is wielded by
        /// a second character when <paramref name="wielded"/> is true. Purges the character and asserts the target
        /// survives with exactly <paramref name="expectedOutcome"/> (formatted with the container and the wielder).
        /// </summary>
        private static void AssertPurgeLeavesTargetAlone(uint? vaultKind, uint vaultAccount, bool wielded, string expectedOutcome)
        {
            var shardDb = RequireDisposableShard();
            var id = FreshIds();

            var character = id;
            var holder = id + 1;     // the vault container, or the wielding character
            var target = id + 2;

            try
            {
                using (var context = new ShardDbContext())
                {
                    SeedCharacter(context, character, "SalvageRollerLookAlikeTest");

                    if (wielded)
                    {
                        SeedCharacter(context, holder, "SalvageRollerWielderTest");
                        SeedBiota(context, target, WeenieType.Generic, "Wielded Sword", null);
                        context.BiotaPropertiesIID.Add(new BiotaPropertiesIID { ObjectId = target, Type = (ushort)PropertyInstanceId.Wielder, Value = holder });
                    }
                    else
                    {
                        SeedBiota(context, holder, WeenieType.Container, "Look-alike Vault", null);
                        context.AccountVault.Add(new AccountVault { AccountId = vaultAccount, ContainerGuid = holder, Kind = (byte)vaultKind.Value, CreatedAt = DateTime.UtcNow });
                        SeedBiota(context, target, WeenieType.Generic, "Vaulted Sword", holder);
                    }

                    context.SaveChanges();
                }

                Assert.IsTrue(shardDb.AddSalvageBatch(Batch(character, target)));

                ShardDatabaseOfflineTools.PurgeCharacter(character, out var charactersPurged, out _, out var possessionsPurged, "test purge");

                Assert.AreEqual(1, charactersPurged);
                Assert.AreEqual(0, possessionsPurged, "the target is not this character's possession and not in its account's escrow");

                using (var context = new ShardDbContext())
                {
                    Assert.IsTrue(context.Biota.Any(b => b.Id == target), "the target survives");
                    Assert.IsFalse(context.CharacterSalvageBatch.Any(b => b.CharacterId == character), "the batch row itself still goes");
                }

                Assert.AreEqual(string.Format(expectedOutcome, holder), (string)ItemEntry(SinglePurgeAudit(character), target)["outcome"]);
            }
            finally
            {
                if (wielded)
                    Cleanup(new[] { character, holder }, new[] { character, holder, target });
                else
                    Cleanup(new[] { character }, new[] { character, holder, target }, new[] { holder });
            }
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void PurgeCharacter_AnItemInTheSameAccountsKindZeroVault_Survives()
        {
            AssertPurgeLeavesTargetAlone(0, AccountId, false, "had left escrow (container 0x{0:X8}); not deleted");
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void PurgeCharacter_AnItemInAnotherAccountsEscrowContainer_Survives()
        {
            AssertPurgeLeavesTargetAlone((uint)ShardDatabaseOfflineTools.SalvageRollerEscrowContainerKind, AccountId + 1, false,
                "had left escrow (container 0x{0:X8}); not deleted");
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void PurgeCharacter_AWieldedItem_Survives()
        {
            AssertPurgeLeavesTargetAlone(null, AccountId, true, "had left escrow (wielded by 0x{0:X8}); not deleted");
        }
    }
}
