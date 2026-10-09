using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The kill-fill vessel - an item that gains a charge when the player HOLDING it lands the killing blow
    /// on a creature it accepts. Covers recognition, the creature-type and landblock filters, the optional
    /// landblock, the capacity clamp, and the side-pack search.
    ///
    /// THE SHAPE UNDER TEST: everything the mechanic keys on is authored on the item - PropertyInt 9038
    /// KillFillCreatureType (whose PRESENCE marks the item), PropertyInt 9039 KillFillLandblock (optional),
    /// MaxStructure as capacity and Structure as the live count. No creature type, landblock or wcid is a
    /// literal in KillFillVessel, and these tests deliberately use values that have nothing to do with bay 2
    /// so a regression that hard-codes bay 2 back in would fail here.
    ///
    /// WHAT IS NOT COVERED, and cannot be from a unit test:
    ///
    ///   Player.ApplyKillFillVesselCreatureDeath itself. Every line of it needs a live Player - the write
    ///   goes through Player.UpdateProperty, which enqueues on player.Session, and the full branch stamps
    ///   through player.QuestManager. Constructing a Player in a test needs a live world database (a static
    ///   field on Player triggers it). So the property write, the GameMessagePublicUpdatePropertyInt send,
    ///   the AssayRowVesselFull stamp and the full-vessel chat line are all UNVERIFIED by this suite and
    ///   need the live test queued in Docs/VERIFY-QUEUE.md.
    ///
    ///   The dispatch site in Creature.OnDeath, and therefore the killing-blow-only guarantee itself. That
    ///   guarantee comes from the call site passing DamageHistory.LastDamager, which is control flow, not a
    ///   value this suite can construct.
    ///
    ///   The Creature overload of Accepts. Creature's ephemeral setup reaches for the world database and the
    ///   dat files, so the primitive overload it delegates to is what is exercised here.
    /// </summary>
    [TestClass]
    public class KillFillVesselTests
    {
        private static uint nextGuid = 0x7D200000;   // static guid range, clear of GuidManager
        private static uint nextWcid = 993000;

        // deliberately NOT bay 2's values: bay 2 is Golem (13) inside 0x564E in realm 1, so a regression
        // that reintroduced those as literals would not satisfy these tests
        private const int Shreth = (int)CreatureType.Shreth;
        private const ushort SomeLandblock = 0x1234;
        private const ushort OtherLandblock = 0x5678;
        private const ushort SomeRealm = 4;
        private const ushort OtherRealm = 7;
        private const ushort BaseRealm = 0;   // the retail world, a real and reachable realm

        /// <summary>
        /// A vessel built from an in-memory weenie. WeenieType.Generic's SetEphemeralValues touches neither
        /// the database nor the dat files. <paramref name="creatureType"/> null omits the marker property
        /// entirely (so the item is not a vessel); <paramref name="landblock"/> and <paramref name="realm"/>
        /// null omit the two optional location filters.
        /// </summary>
        private static WorldObject MakeVessel(int? creatureType = Shreth, int? landblock = SomeLandblock, int? realm = SomeRealm, int capacity = 20, int charges = 1, string name = "Test Vessel")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.Structure, charges },
                    { PropertyInt.MaxStructure, capacity },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            if (creatureType != null)
                weenie.PropertiesInt[PropertyInt.KillFillCreatureType] = creatureType.Value;

            if (landblock != null)
                weenie.PropertiesInt[PropertyInt.KillFillLandblock] = landblock.Value;

            if (realm != null)
                weenie.PropertiesInt[PropertyInt.KillFillRealm] = realm.Value;

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>An ordinary carried item: no marker property, no counter.</summary>
        private static WorldObject MakeOrdinaryItem(string name = "Bread")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Misc } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static Container MakeContainer(string name = "Pack")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Container },
                    { PropertyInt.ItemsCapacity, 24 },
                    { PropertyInt.ContainersCapacity, 7 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        private static void Put(Container container, WorldObject item) => container.Inventory[item.Guid] = item;

        // ---------------- recognition ----------------

        [TestMethod]
        public void IsKillFillVessel_NeedsTheMarkerPropertyAndACapacity()
        {
            Assert.IsFalse(KillFillVessel.IsKillFillVessel(null));
            Assert.IsFalse(KillFillVessel.IsKillFillVessel(MakeOrdinaryItem()));

            // the marker is the creature type: without it, a counter alone is not a vessel
            Assert.IsFalse(KillFillVessel.IsKillFillVessel(MakeVessel(creatureType: null)));

            // and a creature type without a capacity is not one either
            Assert.IsFalse(KillFillVessel.IsKillFillVessel(MakeVessel(capacity: 0)));

            Assert.IsTrue(KillFillVessel.IsKillFillVessel(MakeVessel()));
        }

        [TestMethod]
        public void IsKillFillVessel_DoesNotRequireEitherLocationFilter()
        {
            // both are optional by design - a vendor-sold vessel may legitimately omit them
            Assert.IsTrue(KillFillVessel.IsKillFillVessel(MakeVessel(landblock: null)));
            Assert.IsTrue(KillFillVessel.IsKillFillVessel(MakeVessel(realm: null)));
            Assert.IsTrue(KillFillVessel.IsKillFillVessel(MakeVessel(landblock: null, realm: null)));
        }

        [TestMethod]
        public void GetCharges_And_GetCapacity_ReadStructureAndMaxStructure()
        {
            Assert.AreEqual(0, KillFillVessel.GetCharges(null));
            Assert.AreEqual(0, KillFillVessel.GetCapacity(null));

            var vessel = MakeVessel(capacity: 20, charges: 3);

            Assert.AreEqual(3, KillFillVessel.GetCharges(vessel));
            Assert.AreEqual(20, KillFillVessel.GetCapacity(vessel));
        }

        [TestMethod]
        public void HasRoom_IsFalseAtCapacityAndForNonVessels()
        {
            Assert.IsFalse(KillFillVessel.HasRoom(null));
            Assert.IsFalse(KillFillVessel.HasRoom(MakeOrdinaryItem()));

            Assert.IsTrue(KillFillVessel.HasRoom(MakeVessel(capacity: 20, charges: 0)));
            Assert.IsTrue(KillFillVessel.HasRoom(MakeVessel(capacity: 20, charges: 19)));
            Assert.IsFalse(KillFillVessel.HasRoom(MakeVessel(capacity: 20, charges: 20)));

            // a vessel somehow over capacity stays closed rather than going backwards
            Assert.IsFalse(KillFillVessel.HasRoom(MakeVessel(capacity: 20, charges: 25)));
        }

        // ---------------- the filters ----------------

        [TestMethod]
        public void Accepts_MatchesOnCreatureTypeReadFromTheVessel()
        {
            var vessel = MakeVessel(creatureType: Shreth, landblock: SomeLandblock);

            Assert.IsTrue(KillFillVessel.Accepts(vessel, SomeLandblock, SomeRealm, Shreth));

            // right place, wrong family
            Assert.IsFalse(KillFillVessel.Accepts(vessel, SomeLandblock, SomeRealm, (int)CreatureType.Golem));

            // a creature with no CreatureType at all never qualifies
            Assert.IsFalse(KillFillVessel.Accepts(vessel, SomeLandblock, SomeRealm, null));
        }

        [TestMethod]
        public void Accepts_RejectsTheRightCreatureInTheWrongLandblock()
        {
            // this is the whole point of the location filter: without it the portal to the vessel's dungeon
            // is decorative and the player farms the same creature type wherever it is most convenient
            var vessel = MakeVessel(creatureType: Shreth, landblock: SomeLandblock);

            Assert.IsTrue(KillFillVessel.Accepts(vessel, SomeLandblock, SomeRealm, Shreth));
            Assert.IsFalse(KillFillVessel.Accepts(vessel, OtherLandblock, SomeRealm, Shreth));
        }

        [TestMethod]
        public void Accepts_WithNoLandblockAuthored_FillsAnywhere()
        {
            var anywhere = MakeVessel(creatureType: Shreth, landblock: null);

            Assert.IsTrue(KillFillVessel.Accepts(anywhere, SomeLandblock, SomeRealm, Shreth));
            Assert.IsTrue(KillFillVessel.Accepts(anywhere, OtherLandblock, SomeRealm, Shreth));

            // still filtered on family - "anywhere" relaxes the location only
            Assert.IsFalse(KillFillVessel.Accepts(anywhere, SomeLandblock, SomeRealm, (int)CreatureType.Golem));
        }

        [TestMethod]
        public void Accepts_LandblockZero_MeansAnywhereAndNotLandblockZero()
        {
            var explicitZero = MakeVessel(creatureType: Shreth, landblock: 0);

            Assert.IsTrue(KillFillVessel.Accepts(explicitZero, SomeLandblock, SomeRealm, Shreth));
            Assert.IsTrue(KillFillVessel.Accepts(explicitZero, 0, SomeRealm, Shreth));
        }

        [TestMethod]
        public void Accepts_RejectsTheRightCreatureInTheRightLandblockInTheWrongRealm()
        {
            // THE REASON THIS FILTER EXISTS. A landblock id is shared by every realm's copy of that block,
            // so a landblock filter alone still admits the retail original of a dungeon we copied. Bay 2's
            // live case: retail portal 22870 lands at cell 0x564E0233, byte-identical to bay 2's own portal
            // 1002521, and retail 0x564E holds copper/granite/sandstone golems that are all CreatureType 13.
            var vessel = MakeVessel(creatureType: Shreth, landblock: SomeLandblock, realm: SomeRealm);

            Assert.IsTrue(KillFillVessel.Accepts(vessel, SomeLandblock, SomeRealm, Shreth));

            // same block, same creature family, different realm - the retail original
            Assert.IsFalse(KillFillVessel.Accepts(vessel, SomeLandblock, OtherRealm, Shreth));
            Assert.IsFalse(KillFillVessel.Accepts(vessel, SomeLandblock, BaseRealm, Shreth));
        }

        [TestMethod]
        public void Accepts_WithNoRealmAuthored_FillsInAnyRealm()
        {
            var anyRealm = MakeVessel(creatureType: Shreth, landblock: SomeLandblock, realm: null);

            Assert.IsTrue(KillFillVessel.Accepts(anyRealm, SomeLandblock, SomeRealm, Shreth));
            Assert.IsTrue(KillFillVessel.Accepts(anyRealm, SomeLandblock, OtherRealm, Shreth));
            Assert.IsTrue(KillFillVessel.Accepts(anyRealm, SomeLandblock, BaseRealm, Shreth));

            // the landblock filter still applies - dropping the realm relaxes only the realm
            Assert.IsFalse(KillFillVessel.Accepts(anyRealm, OtherLandblock, SomeRealm, Shreth));
        }

        [TestMethod]
        public void Accepts_RealmZero_MeansRealmZeroOnly_NotAnyRealm()
        {
            // the asymmetry with the landblock sentinel, pinned: realm 0 is the base retail world, a real
            // and reachable place, so a vessel authored with realm 0 is a retail-only vessel. If this ever
            // starts behaving like "anywhere", a vendor vessel aimed at retail content silently becomes
            // fillable everywhere.
            var retailOnly = MakeVessel(creatureType: Shreth, landblock: SomeLandblock, realm: 0);

            Assert.IsTrue(KillFillVessel.Accepts(retailOnly, SomeLandblock, BaseRealm, Shreth));
            Assert.IsFalse(KillFillVessel.Accepts(retailOnly, SomeLandblock, SomeRealm, Shreth));
            Assert.IsFalse(KillFillVessel.Accepts(retailOnly, SomeLandblock, OtherRealm, Shreth));
        }

        [TestMethod]
        public void FindFillable_SkipsAVesselScopedToAnotherRealm()
        {
            var pack = MakeContainer();

            var wrongRealm = MakeVessel(realm: OtherRealm, name: "Wrong Realm");
            var good = MakeVessel(name: "Good");

            Put(pack, wrongRealm);
            Put(pack, good);

            Assert.AreSame(good, KillFillVessel.FindFillable(pack, SomeLandblock, SomeRealm, Shreth));
        }

        [TestMethod]
        public void Accepts_IsFalseForAnythingThatIsNotAVessel()
        {
            Assert.IsFalse(KillFillVessel.Accepts(null, SomeLandblock, SomeRealm, Shreth));
            Assert.IsFalse(KillFillVessel.Accepts(MakeOrdinaryItem(), SomeLandblock, SomeRealm, Shreth));
        }

        [TestMethod]
        public void Accepts_CreatureOverload_IsFalseForAVictimWithNoLocation()
        {
            // a victim already removed from the world has no landblock to test against
            Assert.IsFalse(KillFillVessel.Accepts(MakeVessel(), (Creature)null));
        }

        // ---------------- the clamp ----------------

        [TestMethod]
        public void NextCharges_AddsOneAndNeverExceedsCapacity()
        {
            Assert.AreEqual(1, KillFillVessel.NextCharges(0, 20));
            Assert.AreEqual(2, KillFillVessel.NextCharges(1, 20));
            Assert.AreEqual(20, KillFillVessel.NextCharges(19, 20));

            // a kill against a full vessel is a no-op, not an overflow
            Assert.AreEqual(20, KillFillVessel.NextCharges(20, 20));
            Assert.AreEqual(20, KillFillVessel.NextCharges(25, 20));
        }

        // ---------------- finding the vessel ----------------

        [TestMethod]
        public void FindFillable_FindsAVesselInTheMainPackAndIgnoresOrdinaryItems()
        {
            var pack = MakeContainer();
            Put(pack, MakeOrdinaryItem());
            var vessel = MakeVessel();
            Put(pack, vessel);
            Put(pack, MakeOrdinaryItem("Cheese"));

            Assert.AreSame(vessel, KillFillVessel.FindFillable(pack, SomeLandblock, SomeRealm, Shreth));
        }

        [TestMethod]
        public void FindFillable_SearchesSidePacks()
        {
            // Container.Inventory holds only what is directly in that container, so a vessel stowed in a
            // side pouch would otherwise silently never fill
            var pack = MakeContainer();
            var side = MakeContainer("Side Pouch");
            var vessel = MakeVessel();

            Put(side, vessel);
            Put(pack, MakeOrdinaryItem());
            Put(pack, side);

            Assert.AreSame(vessel, KillFillVessel.FindFillable(pack, SomeLandblock, SomeRealm, Shreth));
        }

        [TestMethod]
        public void FindFillable_SkipsAFullVesselAndAMismatchedOne()
        {
            var pack = MakeContainer();

            var full = MakeVessel(capacity: 20, charges: 20, name: "Full");
            var wrongFamily = MakeVessel(creatureType: (int)CreatureType.Golem, name: "Wrong Family");
            var wrongPlace = MakeVessel(landblock: OtherLandblock, name: "Wrong Place");
            var good = MakeVessel(name: "Good");

            Put(pack, full);
            Put(pack, wrongFamily);
            Put(pack, wrongPlace);
            Put(pack, good);

            Assert.AreSame(good, KillFillVessel.FindFillable(pack, SomeLandblock, SomeRealm, Shreth));
        }

        [TestMethod]
        public void FindFillable_ReturnsNullWhenNothingQualifies()
        {
            Assert.IsNull(KillFillVessel.FindFillable(null, SomeLandblock, SomeRealm, Shreth));

            var pack = MakeContainer();
            Put(pack, MakeOrdinaryItem());
            Put(pack, MakeVessel(capacity: 20, charges: 20));

            Assert.IsNull(KillFillVessel.FindFillable(pack, SomeLandblock, SomeRealm, Shreth));
        }
    }
}
