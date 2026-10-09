using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Player_PvpTemplate.cs on a seeded Player (no session, no database, no world): the gate predicate's
    /// wiring and classification, the apply's never-overwrite precheck, the restore's no-record no-op, and the
    /// login sweep - record plus issued items gives a clean player, issued items with no record are destroyed.
    ///
    /// The Player is built with RuntimeHelpers.GetUninitializedObject and only the instance fields the code under
    /// test reads (Biota, its lock, the guid, Inventory, EquippedObjects, EnchantmentManager), because a real
    /// constructor - and any static Player field - reaches the World database. The two shard side effects go
    /// through PvpTemplateSettings.SaveBiota / DestroyItem, swapped here for recorders and put back in cleanup.
    /// </summary>
    [TestClass]
    public class PvpTemplatePlayerTests
    {
        private Func<bool> savedEnabled;
        private Func<bool> savedKeepOwnBuffs;
        private Action<WorldObject> savedSave;
        private Action<WorldObject> savedDestroy;
        private Func<Player, ObjectGuid, WorldObject> savedSilentDequip;
        private Action<Player, WorldObject> savedMoveToPack;

        private List<WorldObject> saved;
        private List<WorldObject> destroyed;

        [TestInitialize]
        public void Setup()
        {
            savedEnabled = PvpTemplateSettings.EnabledSource;
            savedKeepOwnBuffs = PvpTemplateSettings.KeepOwnBuffsSource;
            savedSave = PvpTemplateSettings.SaveBiota;
            savedDestroy = PvpTemplateSettings.DestroyItem;
            savedSilentDequip = PvpTemplateSettings.SilentDequip;
            savedMoveToPack = PvpTemplateSettings.MoveWornItemToPack;

            // A seeded Player cannot run Creature.TryDequipObject; this does the part the sweep relies on.
            PvpTemplateSettings.SilentDequip = (p, guid) => p.EquippedObjects.Remove(guid, out var wo) ? wo : null;

            saved = new List<WorldObject>();
            destroyed = new List<WorldObject>();

            PvpTemplateSettings.SaveBiota = wo => saved.Add(wo);
            PvpTemplateSettings.DestroyItem = wo => destroyed.Add(wo);
        }

        [TestCleanup]
        public void Cleanup()
        {
            PvpTemplateSettings.EnabledSource = savedEnabled;
            PvpTemplateSettings.KeepOwnBuffsSource = savedKeepOwnBuffs;
            PvpTemplateSettings.SaveBiota = savedSave;
            PvpTemplateSettings.DestroyItem = savedDestroy;
            PvpTemplateSettings.SilentDequip = savedSilentDequip;
            PvpTemplateSettings.MoveWornItemToPack = savedMoveToPack;
        }

        // ================= fixtures =================

        internal static void SetField(Type declaring, object target, string name, object value)
        {
            var field = declaring.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{declaring.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }

        internal static void SeedWorldObject(WorldObject wo, Biota biota, uint guid)
        {
            biota.Id = guid;
            SetField(typeof(WorldObject), wo, "<Biota>k__BackingField", biota);
            SetField(typeof(WorldObject), wo, "BiotaDatabaseLock", new ReaderWriterLockSlim());
            SetField(typeof(WorldObject), wo, "<Guid>k__BackingField", new ObjectGuid(guid));
        }

        internal static Player SeededPlayer(Biota biota = null)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SeedWorldObject(player, biota ?? PvpTemplateCoreTests.OwnBiota(), PvpTemplateCoreTests.PlayerGuid);
            SetField(typeof(Container), player, "<Inventory>k__BackingField", new Dictionary<ObjectGuid, WorldObject>());
            SetField(typeof(Creature), player, "<EquippedObjects>k__BackingField", new Dictionary<ObjectGuid, WorldObject>());
            player.EnchantmentManager = new EnchantmentManagerWithCaching(player);

            return player;
        }

        private static uint nextItemGuid = 0x80200000;

        internal static GenericObject Item(bool issued)
        {
            var item = (GenericObject)RuntimeHelpers.GetUninitializedObject(typeof(GenericObject));

            var biota = new Biota
            {
                WeenieType = ACE.Entity.Enum.WeenieType.Generic,
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, issued ? "Issued Sword" : "Own Sword" } },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
            };

            if (issued)
                biota.PropertiesBool[PropertyBool.PvpTemplateIssued] = true;

            SeedWorldObject(item, biota, Interlocked.Increment(ref nextItemGuid));

            return item;
        }

        internal static void Give(Player player, WorldObject item, int slot)
        {
            item.PlacementPosition = slot;
            player.Inventory[item.Guid] = item;
        }

        /// <summary>The record JSON a real apply writes, for <paramref name="definition"/> on the player's current biota.</summary>
        private static string ApplyData(Player player, PvpTemplateDefinition definition)
        {
            var record = PvpTemplateOverlay.Capture(player.Biota, player.BiotaDatabaseLock, definition, Guid.NewGuid(), DateTime.UtcNow, true, new List<ACE.Server.Entity.Facets.FacetEquipEntry>());

            Assert.IsTrue(PvpTemplateJson.TrySerializeRecord(record, out var json, out _));

            PvpTemplateOverlay.ApplyBuild(player.Biota, player.BiotaDatabaseLock, definition);
            PvpTemplateOverlay.RemoveEnchantments(player.Biota, player.BiotaDatabaseLock, PvpTemplateOverlay.NonItemEnchantments(player.Biota, player.BiotaDatabaseLock));
            PvpTemplateOverlay.AddEnchantmentsPreservingLayer(player.Biota, player.BiotaDatabaseLock, PvpTemplateOverlay.BuildTemplateBuffs(definition, player.Guid.Full));

            return json;
        }

        internal static string RecordWithSpells(params int[] spells)
        {
            var record = new PvpTemplateRestoreRecord { TemplateKey = "t", TemplateSpells = spells.ToList() };
            Assert.IsTrue(PvpTemplateJson.TrySerializeRecord(record, out var json, out _));
            return json;
        }

        // ================= classification and the predicate =================

        [TestMethod]
        public void Classify_IssuedPersonalAndForeign()
        {
            var player = SeededPlayer();
            var own = Item(issued: false);
            var issued = Item(issued: true);
            var foreign = Item(issued: false);

            Give(player, own, 0);
            Give(player, issued, 1);

            Assert.AreEqual(PvpTemplateItemKind.Personal, player.ClassifyForPvpTemplate(own));
            Assert.AreEqual(PvpTemplateItemKind.Issued, player.ClassifyForPvpTemplate(issued));
            Assert.AreEqual(PvpTemplateItemKind.None, player.ClassifyForPvpTemplate(foreign), "an object the player does not possess is not personal");
            Assert.AreEqual(PvpTemplateItemKind.None, player.ClassifyForPvpTemplate(null));
            Assert.AreEqual(PvpTemplateItemKind.None, player.ClassifyForPvpTemplate(player), "the player is not their own item");
        }

        /// <summary>The predicate keys on the record's presence, the item's classification, and the system scope.</summary>
        [TestMethod]
        public void Blocked_FollowsTheRecordAndTheScope()
        {
            var player = SeededPlayer();
            var own = Item(issued: false);
            var issued = Item(issued: true);
            Give(player, own, 0);
            Give(player, issued, 1);

            Assert.IsFalse(player.IsPvpTemplated);
            Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.Equip, own));
            Assert.AreEqual(PvpTemplateText.IssuedItemOutsideMatch, player.PvpTemplateBlocked(PvpTemplateAction.Equip, issued));
            Assert.AreEqual(PvpTemplateText.IssuedItemLocked, player.PvpTemplateBlocked(PvpTemplateAction.Drop, issued));
            Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.Experience));

            player.SetProperty(PropertyString.PvpTemplateRestore, RecordWithSpells(3));

            Assert.IsTrue(player.IsPvpTemplated);
            Assert.AreEqual(PvpTemplateText.PersonalItemLocked, player.PvpTemplateBlocked(PvpTemplateAction.Equip, own));
            Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.Equip, issued));
            Assert.AreEqual(PvpTemplateText.ProgressionLocked, player.PvpTemplateBlocked(PvpTemplateAction.Experience));
            Assert.AreEqual(PvpTemplateText.MixedStack, player.PvpTemplateBlocked(PvpTemplateAction.MergeOrSplit, own, issued));

            using (player.BeginPvpTemplateSystemOperation())
            {
                using (player.BeginPvpTemplateSystemOperation())
                    Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.Equip, own), "nested scope");

                Assert.IsTrue(player.PvpTemplateSystemBypass, "the outer scope survives the inner one's dispose");
                Assert.IsNull(player.PvpTemplateBlocked(PvpTemplateAction.Drop, issued));
            }

            Assert.IsFalse(player.PvpTemplateSystemBypass);
            Assert.AreEqual(PvpTemplateText.PersonalItemLocked, player.PvpTemplateBlocked(PvpTemplateAction.Equip, own));
        }

        [TestMethod]
        public void CastBlocked_AllowsOnlyTemplateSpells_AndAnUnreadableRecordRefusesAll()
        {
            var player = SeededPlayer();

            Assert.IsNull(player.PvpTemplateCastBlocked(9), "untemplated");

            player.SetProperty(PropertyString.PvpTemplateRestore, RecordWithSpells(3, 4));

            Assert.IsNull(player.PvpTemplateCastBlocked(3));
            Assert.AreEqual(PvpTemplateText.NonTemplateSpell, player.PvpTemplateCastBlocked(9));

            player.SetProperty(PropertyString.PvpTemplateRestore, "{garbage");

            Assert.IsTrue(player.IsPvpTemplated, "an unreadable record still counts as templated");
            Assert.AreEqual(PvpTemplateText.NonTemplateSpell, player.PvpTemplateCastBlocked(3), "inert, never open");
        }

        // ================= apply precheck: a record is never overwritten =================

        [TestMethod]
        public void Apply_NeverOverwritesAnExistingRecord()
        {
            PvpTemplateSettings.EnabledSource = () => true;

            var player = SeededPlayer();
            var existing = RecordWithSpells(42);
            player.SetProperty(PropertyString.PvpTemplateRestore, existing);
            var before = PvpTemplateCoreTests.Fingerprint(player.Biota);

            var result = player.ApplyPvpTemplateNow(PvpTemplateCoreTests.Template(), Guid.NewGuid());

            Assert.IsFalse(result.Success);
            Assert.AreEqual(PvpTemplateText.ApplyAlreadyTemplated, result.Refusal);
            Assert.AreEqual(existing, player.GetProperty(PropertyString.PvpTemplateRestore), "the existing record is untouched");
            Assert.AreEqual(before, PvpTemplateCoreTests.Fingerprint(player.Biota), "nothing was written");
            Assert.AreEqual(0, saved.Count);
        }

        [TestMethod]
        public void Apply_RefusesWhileDisabled_WritingNothing()
        {
            PvpTemplateSettings.EnabledSource = () => false;

            var player = SeededPlayer();
            var before = PvpTemplateCoreTests.Fingerprint(player.Biota);

            var result = player.ApplyPvpTemplateNow(PvpTemplateCoreTests.Template(), Guid.NewGuid());

            Assert.IsFalse(result.Success);
            Assert.AreEqual(PvpTemplateText.ApplyDisabled, result.Refusal);
            Assert.IsFalse(player.IsPvpTemplated);
            Assert.AreEqual(before, PvpTemplateCoreTests.Fingerprint(player.Biota));
        }

        [TestMethod]
        public void Restore_WithNoRecord_IsANoOp()
        {
            var player = SeededPlayer();
            var before = PvpTemplateCoreTests.Fingerprint(player.Biota);

            Assert.IsFalse(player.RestorePvpTemplateNow("test", silent: true, announce: false));
            Assert.AreEqual(before, PvpTemplateCoreTests.Fingerprint(player.Biota));
            Assert.AreEqual(0, saved.Count);
        }

        // ================= the login sweep =================

        /// <summary>
        /// A crash mid-match: the record and the issued kit are both on the character. Login destroys every issued
        /// item, restores the build exactly, clears the record, keeps the personal item, and saves.
        /// </summary>
        [TestMethod]
        public void Login_RecordAndIssuedItems_GivesACleanPlayer()
        {
            var player = SeededPlayer();
            var original = PvpTemplateCoreTests.Fingerprint(player.Biota);

            player.SetProperty(PropertyString.PvpTemplateRestore, ApplyData(player, PvpTemplateCoreTests.Template()));

            var own = Item(issued: false);
            var kit1 = Item(issued: true);
            var kit2 = Item(issued: true);
            Give(player, own, 0);
            Give(player, kit1, 1);
            Give(player, kit2, 2);

            Assert.AreNotEqual(original, PvpTemplateCoreTests.Fingerprint(player.Biota), "sanity: the player is templated");

            var outcome = player.RestorePvpTemplateAtLoginCore();

            Assert.AreEqual(Player.PvpTemplateLoginOutcomeKind.Restored, outcome.Kind);
            Assert.AreEqual(2, outcome.Swept);
            Assert.IsFalse(player.IsPvpTemplated, "the record is gone");
            Assert.AreEqual(original, PvpTemplateCoreTests.Fingerprint(player.Biota), "the build is exactly the player's own again");

            CollectionAssert.AreEquivalent(new WorldObject[] { kit1, kit2 }, destroyed);
            CollectionAssert.AreEquivalent(new WorldObject[] { own }, player.Inventory.Values.ToList());
            Assert.IsTrue(saved.Contains(player), "the restored player is saved");
            Assert.IsFalse(player.PvpTemplateSystemBypass, "the system scope is closed");
        }

        /// <summary>Issued items with no record (crash after the restore's record clear, before the destroy committed) are destroyed.</summary>
        [TestMethod]
        public void Login_IssuedItemsWithNoRecord_AreDestroyed()
        {
            var player = SeededPlayer();
            var before = PvpTemplateCoreTests.Fingerprint(player.Biota);

            var own = Item(issued: false);
            var stray = Item(issued: true);
            Give(player, own, 0);
            Give(player, stray, 1);

            var outcome = player.RestorePvpTemplateAtLoginCore();

            Assert.AreEqual(Player.PvpTemplateLoginOutcomeKind.Swept, outcome.Kind);
            Assert.AreEqual(1, outcome.Swept);
            CollectionAssert.AreEqual(new WorldObject[] { stray }, destroyed);
            CollectionAssert.AreEquivalent(new WorldObject[] { own }, player.Inventory.Values.ToList());
            Assert.AreEqual(before, PvpTemplateCoreTests.Fingerprint(player.Biota), "no record: the build is not touched");
            Assert.IsTrue(saved.Contains(player));
        }

        [TestMethod]
        public void Login_NothingToDo_IsClean_AndDoesNotSave()
        {
            var player = SeededPlayer();
            Give(player, Item(issued: false), 0);

            var outcome = player.RestorePvpTemplateAtLoginCore();

            Assert.AreEqual(Player.PvpTemplateLoginOutcomeKind.Clean, outcome.Kind);
            Assert.AreEqual(0, destroyed.Count);
            Assert.AreEqual(0, saved.Count);
        }

        /// <summary>An unreadable record is KEPT (crash invariant 4): the player logs in inert. The sweep still runs.</summary>
        [TestMethod]
        public void Login_UnreadableRecord_IsKept_AndThePlayerIsInert()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, "{\"v\":99,\"rec\":{}}");
            var stray = Item(issued: true);
            Give(player, stray, 0);

            var outcome = player.RestorePvpTemplateAtLoginCore();

            Assert.AreEqual(Player.PvpTemplateLoginOutcomeKind.Inert, outcome.Kind);
            Assert.IsTrue(player.IsPvpTemplated, "the record is kept");
            CollectionAssert.AreEqual(new WorldObject[] { stray }, destroyed, "issued items go regardless of the record");
        }

        /// <summary>Running the login restore twice is the same as once: the second finds nothing.</summary>
        [TestMethod]
        public void Login_Twice_SecondIsClean()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, ApplyData(player, PvpTemplateCoreTests.Template()));
            Give(player, Item(issued: true), 0);

            Assert.AreEqual(Player.PvpTemplateLoginOutcomeKind.Restored, player.RestorePvpTemplateAtLoginCore().Kind);
            var once = PvpTemplateCoreTests.Fingerprint(player.Biota, includeUntrainedZeroSkills: true);

            Assert.AreEqual(Player.PvpTemplateLoginOutcomeKind.Clean, player.RestorePvpTemplateAtLoginCore().Kind);
            Assert.AreEqual(once, PvpTemplateCoreTests.Fingerprint(player.Biota, includeUntrainedZeroSkills: true));
        }

        // ================= review fixes =================

        private static void Wear(Player player, WorldObject item, EquipMask slot)
        {
            item.SetProperty(PropertyInt.CurrentWieldedLocation, (int)slot);
            player.EquippedObjects[item.Guid] = item;
        }

        /// <summary>
        /// M1: an issued item that cannot be destroyed keeps the record (crash invariant 4). Before the fix the
        /// failure was logged and the record cleared anyway, leaving template gear on an open player.
        /// </summary>
        [TestMethod]
        public void Restore_WhenAnIssuedItemCannotBeDestroyed_KeepsTheRecord()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, ApplyData(player, PvpTemplateCoreTests.Template()));
            Give(player, Item(issued: true), 0);

            PvpTemplateSettings.DestroyItem = wo => throw new InvalidOperationException("shard down");

            Assert.IsFalse(player.RestorePvpTemplateNow("test", silent: true, announce: false));
            Assert.IsTrue(player.IsPvpTemplated, "the record is kept");
            Assert.IsFalse(saved.Contains(player), "a failed restore does not save a half-restored player");
        }

        [TestMethod]
        public void Login_WhenAnIssuedItemCannotBeDestroyed_IsInert()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, ApplyData(player, PvpTemplateCoreTests.Template()));
            Give(player, Item(issued: true), 0);

            PvpTemplateSettings.DestroyItem = wo => throw new InvalidOperationException("shard down");

            Assert.AreEqual(Player.PvpTemplateLoginOutcomeKind.Inert, player.RestorePvpTemplateAtLoginCore().Kind);
            Assert.IsTrue(player.IsPvpTemplated);
        }

        /// <summary>
        /// M2: a restore that fails is not retried every beat. Discriminates the backoff: without it the third beat
        /// (one second after the failure) would attempt again.
        /// </summary>
        [TestMethod]
        public void Heartbeat_AfterAFailedRestore_BacksOff()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, "{\"v\":99,\"rec\":{}}"); // unreadable: every restore fails
            var t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
            var delay = PvpTemplateSettings.BackstopDelay();

            Assert.IsFalse(player.PvpTemplateHeartbeatCore(t0, inMatch: false, teleporting: false), "first beat stamps");
            Assert.IsTrue(player.PvpTemplateHeartbeatCore(t0 + delay, false, false), "attempts once the delay has passed");
            Assert.IsTrue(player.IsPvpTemplated, "and fails");

            Assert.IsFalse(player.PvpTemplateHeartbeatCore(t0 + delay + TimeSpan.FromSeconds(1), false, false), "no retry inside the backoff");
            Assert.IsFalse(player.PvpTemplateHeartbeatCore(t0 + delay + TimeSpan.FromSeconds(59), false, false));
            Assert.IsTrue(player.PvpTemplateHeartbeatCore(t0 + delay + PvpTemplateSettings.HeartbeatRetryBackoff, false, false), "retries after the backoff");
        }

        [TestMethod]
        public void Heartbeat_NeverRunsInAMatchOrWhileTeleporting()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, "{\"v\":99,\"rec\":{}}");
            var t0 = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(player.PvpTemplateHeartbeatCore(t0, false, false));
            Assert.IsFalse(player.PvpTemplateHeartbeatCore(t0.AddMinutes(5), inMatch: false, teleporting: true));
            Assert.IsFalse(player.PvpTemplateHeartbeatCore(t0.AddMinutes(5), inMatch: true, teleporting: false));
        }

        /// <summary>
        /// M3: a personal item worn around the gates is taken off by the backstop, and the count is what actually
        /// moved. The move here is the seam (the real one is HandleActionPutItemInContainer, a live check).
        /// </summary>
        [TestMethod]
        public void Backstop_RemovesAPersonalItemWornAroundTheGates()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, RecordWithSpells(3));
            var own = Item(issued: false);
            var kit = Item(issued: true);
            Wear(player, own, EquipMask.MeleeWeapon);
            Wear(player, kit, EquipMask.ChestArmor);

            var moves = new List<WorldObject>();
            PvpTemplateSettings.MoveWornItemToPack = (p, item) =>
            {
                Assert.IsTrue(p.PvpTemplateSystemBypass, "the move runs through the system bypass");
                moves.Add(item);
                p.EquippedObjects.Remove(item.Guid);
                Give(p, item, 0);
            };

            var result = player.PvpTemplateRunEquippedBackstop();

            Assert.AreEqual(1, result.Moved);
            Assert.AreEqual(0, result.Survivors);
            CollectionAssert.AreEqual(new WorldObject[] { own }, moves, "only the personal item is moved; the kit stays on");
            Assert.IsTrue(player.EquippedObjects.ContainsKey(kit.Guid));
            Assert.IsFalse(player.PvpTemplateSystemBypass);
        }

        /// <summary>M3: a move that does not happen (full pack) is reported as a survivor, never as moved.</summary>
        [TestMethod]
        public void Backstop_FullPack_ReportsASurvivor_NotAMove()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, RecordWithSpells(3));
            Wear(player, Item(issued: false), EquipMask.MeleeWeapon);

            PvpTemplateSettings.MoveWornItemToPack = (p, item) => { };

            var result = player.PvpTemplateRunEquippedBackstop();

            Assert.AreEqual(0, result.Moved);
            Assert.AreEqual(1, result.Survivors);
            Assert.AreEqual(0, player.PvpTemplateCheckEquippedBackstop(), "the int form reports only what moved");
        }

        [TestMethod]
        public void Backstop_DoesNothingForAnUntemplatedPlayer()
        {
            var player = SeededPlayer();
            Wear(player, Item(issued: false), EquipMask.MeleeWeapon);
            PvpTemplateSettings.MoveWornItemToPack = (p, item) => Assert.Fail("an untemplated player's gear is never touched");

            var result = player.PvpTemplateRunEquippedBackstop();

            Assert.AreEqual(0, result.Moved + result.Survivors);
        }

        /// <summary>
        /// M4: the silent sweep of a WORN issued item. It comes off, is destroyed, and takes its own item-granted
        /// rows with it; the player's own item rows and vitae stay. (The dequip itself is the seam.)
        /// </summary>
        [TestMethod]
        public void Login_WornIssuedItem_IsDequippedAndDestroyed_WithItsItemSpells()
        {
            var player = SeededPlayer();
            var worn = Item(issued: true);
            Wear(player, worn, EquipMask.MeleeWeapon);

            player.Biota.PropertiesEnchantmentRegistry.Add(new ACE.Entity.Models.PropertiesEnchantmentRegistry { SpellId = 4321, LayerId = 1, Duration = -1, CasterObjectId = worn.Guid.Full });

            var outcome = player.RestorePvpTemplateAtLoginCore();

            Assert.AreEqual(Player.PvpTemplateLoginOutcomeKind.Swept, outcome.Kind);
            CollectionAssert.AreEqual(new WorldObject[] { worn }, destroyed);
            Assert.AreEqual(0, player.EquippedObjects.Count);

            var spells = player.Biota.PropertiesEnchantmentRegistry.Select(e => e.SpellId).ToList();
            CollectionAssert.DoesNotContain(spells, 4321, "the destroyed item's own spell row is removed");
            CollectionAssert.Contains(spells, 200, "the player's own item row stays");
            CollectionAssert.Contains(spells, PvpTemplateOverlay.VitaeSpellId, "vitae stays");
        }

        /// <summary>L6: an own item that is already worn is satisfied as is - no lookup, no vault, no report.</summary>
        [TestMethod]
        public void OwnEquip_AlreadyWorn_IsSatisfied()
        {
            var player = SeededPlayer();
            var own = Item(issued: false);
            Wear(player, own, EquipMask.MeleeWeapon);

            var report = player.RestorePvpTemplateOwnEquip(new List<ACE.Server.Entity.Facets.FacetEquipEntry>
            {
                new ACE.Server.Entity.Facets.FacetEquipEntry { Guid = own.Guid.Full, Wcid = 1, Slot = (int)EquipMask.MeleeWeapon },
            });

            Assert.AreEqual(0, report.Count, string.Join("; ", report));
            Assert.IsTrue(player.EquippedObjects.ContainsKey(own.Guid));
        }

        /// <summary>L1: a player holding an issued item is not itself "issued"; a real pack holding one is.</summary>
        [TestMethod]
        public void IsIssued_LooksInsidePacks_NotInsideCreatures()
        {
            var player = SeededPlayer();
            Give(player, Item(issued: true), 0);

            var pack = (Container)RuntimeHelpers.GetUninitializedObject(typeof(Container));
            SeedWorldObject(pack, new Biota { WeenieType = ACE.Entity.Enum.WeenieType.Container }, 0x80300001);
            SetField(typeof(Container), pack, "<Inventory>k__BackingField", new Dictionary<ObjectGuid, WorldObject>());
            var potion = Item(issued: true);
            pack.Inventory[potion.Guid] = potion;

            Assert.IsFalse(PvpTemplate.IsIssued(player));
            Assert.IsTrue(PvpTemplate.IsIssued(pack));
        }

        /// <summary>
        /// Issued gear can be unequipped while templated (owner ruling 2026-10-04), so at exit an issued item may sit
        /// in a side pack rather than a slot. The restore collects from every possession (GetAllPossessions walks the
        /// side packs), so it is destroyed, the personal pack and its personal contents stay, and the record clears.
        /// </summary>
        [TestMethod]
        public void Restore_DestroysAnUnequippedIssuedItemInASidePack()
        {
            var player = SeededPlayer();
            player.SetProperty(PropertyString.PvpTemplateRestore, ApplyData(player, PvpTemplateCoreTests.Template()));

            var pack = (Container)RuntimeHelpers.GetUninitializedObject(typeof(Container));
            SeedWorldObject(pack, new Biota { WeenieType = ACE.Entity.Enum.WeenieType.Container }, 0x80300002);
            SetField(typeof(Container), pack, "<Inventory>k__BackingField", new Dictionary<ObjectGuid, WorldObject>());
            Give(player, pack, 0);

            var shield = Item(issued: true);
            var own = Item(issued: false);
            shield.PlacementPosition = 0;
            own.PlacementPosition = 1;
            pack.Inventory[shield.Guid] = shield;
            pack.Inventory[own.Guid] = own;

            var worn = Item(issued: true);
            Wear(player, worn, EquipMask.MeleeWeapon);

            Assert.IsTrue(player.RestorePvpTemplateNow("test", silent: true, announce: false));

            CollectionAssert.AreEquivalent(new WorldObject[] { shield, worn }, destroyed, "the side-pack issued item and the worn one are both destroyed");
            Assert.IsFalse(pack.Inventory.ContainsKey(shield.Guid), "the issued item is out of the side pack");
            Assert.IsTrue(pack.Inventory.ContainsKey(own.Guid), "the personal item stays in the side pack");
            Assert.IsTrue(player.Inventory.ContainsKey(pack.Guid), "the personal pack stays");
            Assert.IsFalse(player.IsPvpTemplated, "the record is cleared");
            Assert.AreEqual(0, player.GetAllPossessions().Count(PvpTemplate.IsMarkedIssued));
        }
    }
}
