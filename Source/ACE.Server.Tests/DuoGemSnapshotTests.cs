using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Custom Summons Task 7: the Duo Gem slot snapshot. A summon item captured into a slot and restored by the hammer
    /// must come back with every mutable table intact (plan invariant 1: trade, vault, mule or market keep both
    /// snapshots intact), with a NEW guid, and with none of the PvP template issued stamps.
    /// </summary>
    [TestClass]
    public class DuoGemSnapshotTests
    {
        [ClassInitialize]
        public static void Init(TestContext _) => TestGameTables.EnsureInitialized();

        /// <summary>A summon item carrying a value in every table the snapshot claims to carry.</summary>
        private static PetDevice RichItem()
        {
            var item = DuoGemTestKit.Summon("Rich Essence", 5001, structure: 7, element: UiEffects.Frost, damage: 18, crit: 14);

            item.SetProperty(PropertyInt.Attuned, (int)AttunedStatus.Attuned);
            item.SetProperty(PropertyInt.Bonded, (int)BondedStatus.Bonded);
            item.SetProperty(PropertyInt.PlacementPosition, 3);
            item.SetProperty(PropertyInt.CurrentWieldedLocation, 1);
            item.SetProperty(PropertyInt64.ItemTotalXp, 123456789L);
            item.SetProperty(PropertyBool.Inscribable, true);
            item.SetProperty(PropertyFloat.CooldownDuration, 44.5);
            item.SetProperty(PropertyString.Inscription, "for Gones");
            item.SetProperty(PropertyString.ScribeName, "Wild");
            item.SetProperty(PropertyDataId.SummonBoundSpell1, 111u);
            item.SetProperty(PropertyDataId.SummonBoundSpell2, 222u);
            item.SetProperty(PropertyInstanceId.Scribe, 0x50000001u);
            item.SetProperty(PropertyInstanceId.Container, 0x50000002u);

            item.Biota.PropertiesSpellBook = new Dictionary<int, float> { { 3000, 2.0f } };
            item.Biota.PropertiesAnimPart = new List<PropertiesAnimPart> { new PropertiesAnimPart { Index = 1, AnimationId = 0x01000123 } };
            item.Biota.PropertiesPalette = new List<PropertiesPalette> { new PropertiesPalette { SubPaletteId = 0x04000456, Offset = 2, Length = 9 } };
            item.Biota.PropertiesTextureMap = new List<PropertiesTextureMap> { new PropertiesTextureMap { PartIndex = 2, OldTexture = 0x05000001, NewTexture = 0x05000002 } };
            item.Biota.PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry>
            {
                new PropertiesEnchantmentRegistry { SpellId = 4000, LayerId = 1, StartTime = -12.5, Duration = 300, CasterObjectId = item.Guid.Full, StatModValue = 1.5f },
                new PropertiesEnchantmentRegistry { SpellId = 4001, LayerId = 1, StartTime = -1, Duration = 60, CasterObjectId = 0x50000009 },
            };

            return item;
        }

        private static DuoGemSnapshot RoundTrip(WorldObject item)
        {
            Assert.IsTrue(DuoGemSnapshot.Capture(item).TrySerialize(out var json));
            Assert.IsTrue(DuoGemSnapshot.TryRead(json, out var read, out var error), error);
            return read;
        }

        [TestMethod]
        public void Snapshot_RoundTrips_AllMutableTables()
        {
            var item = RichItem();
            const uint newGuid = 0x7F5E0001;

            var snap = RoundTrip(item);
            var biota = snap.BuildRestoredBiota(newGuid);

            Assert.AreEqual(item.Guid.Full, snap.SourceGuid, "the absorbed guid is recorded for the same-item check");
            Assert.AreEqual(newGuid, biota.Id, "a restored item has a NEW guid");
            Assert.AreEqual(item.WeenieClassId, biota.WeenieClassId);
            Assert.AreEqual(WeenieType.PetDevice, biota.WeenieType);

            // every int except placement, Attuned / Bonded / Value included (no PvP economy drop)
            foreach (var kvp in item.Biota.PropertiesInt)
            {
                if (kvp.Key == PropertyInt.PlacementPosition || kvp.Key == PropertyInt.CurrentWieldedLocation)
                    Assert.IsFalse(biota.PropertiesInt.ContainsKey(kvp.Key), $"{kvp.Key} is placement and must not be restored");
                else
                    Assert.AreEqual(kvp.Value, biota.PropertiesInt[kvp.Key], kvp.Key.ToString());
            }

            Assert.AreEqual(7, biota.PropertiesInt[PropertyInt.Structure], "charges as absorbed");
            Assert.AreEqual((int)AttunedStatus.Attuned, biota.PropertiesInt[PropertyInt.Attuned]);
            Assert.AreEqual((int)BondedStatus.Bonded, biota.PropertiesInt[PropertyInt.Bonded]);
            Assert.AreEqual(1234, biota.PropertiesInt[PropertyInt.Value]);

            CollectionAssert.AreEquivalent(item.Biota.PropertiesInt64.ToList(), biota.PropertiesInt64.ToList());
            CollectionAssert.AreEquivalent(item.Biota.PropertiesBool.ToList(), biota.PropertiesBool.ToList());
            CollectionAssert.AreEquivalent(item.Biota.PropertiesFloat.ToList(), biota.PropertiesFloat.ToList());
            CollectionAssert.AreEquivalent(item.Biota.PropertiesString.ToList(), biota.PropertiesString.ToList());
            CollectionAssert.AreEquivalent(item.Biota.PropertiesDID.ToList(), biota.PropertiesDID.ToList());
            CollectionAssert.AreEquivalent(item.Biota.PropertiesSpellBook.ToList(), biota.PropertiesSpellBook.ToList());

            Assert.AreEqual(0x01000123u, biota.PropertiesAnimPart.Single().AnimationId);
            Assert.AreEqual((byte)1, biota.PropertiesAnimPart.Single().Index);
            Assert.AreEqual(0x04000456u, biota.PropertiesPalette.Single().SubPaletteId);
            Assert.AreEqual((ushort)9, biota.PropertiesPalette.Single().Length);
            Assert.AreEqual(0x05000002u, biota.PropertiesTextureMap.Single().NewTexture);

            // enchantments exactly as they were, the self-cast one re-owned by the new guid
            var e = biota.PropertiesEnchantmentRegistry.OrderBy(x => x.SpellId).ToList();
            Assert.AreEqual(2, e.Count);
            Assert.AreEqual(-12.5, e[0].StartTime, "no PvP full-duration reset");
            Assert.AreEqual(1.5f, e[0].StatModValue);
            Assert.AreEqual(newGuid, e[0].CasterObjectId, "an enchantment the item cast on itself follows the item");
            Assert.AreEqual(0x50000009u, e[1].CasterObjectId, "anyone else's stays theirs");

            // only the inscriber survives among the instance ids; no container, no positions
            Assert.AreEqual(0x50000001u, biota.PropertiesIID[PropertyInstanceId.Scribe]);
            Assert.IsFalse(biota.PropertiesIID.ContainsKey(PropertyInstanceId.Container));
            Assert.IsNull(biota.PropertiesPosition);

            // and no PvP template issued stamp
            Assert.IsFalse(biota.PropertiesBool.ContainsKey(PropertyBool.PvpTemplateIssued));
        }

        [TestMethod]
        public void Snapshot_RestoresAsAPetDevice_WithAbsorbedCharges_NotRefilled()
        {
            var item = DuoGemTestKit.Summon("Spent Essence", 5002, structure: 3);

            var restored = WorldObjectFactory.CreateWorldObject(RoundTrip(item).BuildRestoredBiota(0x7F5E0002));

            Assert.IsInstanceOfType(restored, typeof(PetDevice));
            Assert.AreEqual(3, (int?)restored.Structure, "the biota constructor keeps the absorbed charges");
            Assert.AreEqual(50, (int?)restored.MaxStructure);
        }

        [TestMethod]
        public void Snapshot_ToSource_MatchesFromDevice()
        {
            var item = RichItem();

            Assert.AreEqual(PetSummonSource.FromDevice(item), RoundTrip(item).ToSource());
        }

        [TestMethod]
        public void Snapshot_RestoreOverWeenieBase_NeverMutatesTheBase()
        {
            var item = DuoGemTestKit.Summon("Base Essence", 5003);
            var snap = RoundTrip(item);

            var sharedInts = new Dictionary<PropertyInt, int> { { PropertyInt.Value, 1 } };
            var weenieBase = new Biota { PropertiesInt = sharedInts };

            var biota = snap.BuildRestoredBiota(0x7F5E0003, weenieBase);

            Assert.AreNotSame(sharedInts, biota.PropertiesInt);
            Assert.AreEqual(1, sharedInts.Count);
            Assert.AreEqual(1, sharedInts[PropertyInt.Value]);
        }

        [TestMethod]
        public void Read_RefusesUnknownVersion_Malformed_AndEmpty()
        {
            Assert.IsTrue(DuoGemSnapshot.Capture(DuoGemTestKit.Summon("V", 5004)).TrySerialize(out var json));

            var future = json.Replace("\"v\":1", "\"v\":2");
            Assert.AreNotEqual(json, future, "fixture: the version field was rewritten");

            Assert.IsFalse(DuoGemSnapshot.TryRead(future, out _, out var e1));
            StringAssert.Contains(e1, "schema version 2");

            Assert.IsFalse(DuoGemSnapshot.TryRead("{not json", out _, out _));
            Assert.IsFalse(DuoGemSnapshot.TryRead("{\"v\":1}", out _, out _), "no item body");
            Assert.IsFalse(DuoGemSnapshot.TryRead("", out _, out _));
            Assert.IsFalse(DuoGemSnapshot.TryRead(null, out _, out _));
        }

        [TestMethod]
        public void Serialize_RefusesAboveTheTextColumnCap()
        {
            var item = DuoGemTestKit.Summon("Huge", 5005);
            item.SetProperty(PropertyString.Inscription, new string('x', DuoGemSnapshot.MaxBytes + 1));

            Assert.IsFalse(DuoGemSnapshot.Capture(item).TrySerialize(out var json));
            Assert.IsNull(json);
        }
    }
}
