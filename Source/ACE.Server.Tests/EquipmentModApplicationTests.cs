using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Phase 2 tests for the equipment-mod application flow: material and bag recognition, target eligibility,
    /// the three-path decision table, capacity math, distinct-type selection, and the property writes on a real
    /// (database-free) item.
    ///
    /// What is NOT covered here, and needs the Phase 4 live loop instead - everything below the decision layer
    /// requires a live Player with a session, inventory and motion table:
    ///   - EquipmentModManager.UseObjectOnTarget end to end: the busy/combat guards, the ClapHands chain and
    ///     its re-verification, NextUseTime.
    ///   - VerifyUseRequirements' player-facing half: the inventory-only checks (source and target both found
    ///     via SearchLocations.MyInventory, so an equipped target is refused) and the refusal messages.
    ///   - There is no confirmation round trip to cover: this system raises no server-side confirmation at all.
    ///     The gate is the client's own generic tinkering-material prompt, fired client-side before the server
    ///     is contacted (see EquipmentModManager's class remarks).
    ///   - HandleApply's networking: the salvage bag consume, the UpdateObject resend, MoveItemToFirstContainerSlot.
    ///   - The RecipeManager.UseObjectOnTarget intercept firing for a real bag, and falling through to normal
    ///     tinkering when equipment_mods_enabled is false.
    /// The pure decision and write logic those paths call is exercised below.
    /// </summary>
    [TestClass]
    public class EquipmentModApplicationTests
    {
        private static uint nextGuid = 0x7D000000;   // static guid range, clear of GuidManager
        private static uint nextWcid = 991000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// A bare Clothing item from an in-memory weenie: no database, no dat files (Clothing's
        /// SetEphemeralValues is empty). Enough for every property read/write the mod flow performs.
        /// </summary>
        private static WorldObject MakeItem(EquipMask validLocations = EquipMask.ChestArmor, Dictionary<PropertyInt, int> ints = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)validLocations },
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Item" } },
            };

            if (ints != null)
            {
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;
            }

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        // ---------------- material recognition ----------------

        [TestMethod]
        public void ModMaterial_MatchesOnlyTheTwoDesignatedSalvageBags()
        {
            Assert.IsTrue(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.TigerEye));
            Assert.IsTrue(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Obsidian));

            // pinned by raw id as well as by name: MaterialType 42 (0x2A) is TigerEye and 69 (0x45) is
            // Obsidian, verified against the ace_world salvage bag weenies 21081 "materialtigereye" and
            // 21063 "materialobsidian" (both ItemType.TinkeringMaterial, MaterialType 42 / 69)
            Assert.IsTrue(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, (MaterialType)0x2A));
            Assert.IsTrue(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, (MaterialType)0x45));

            // other salvage bags keep their ordinary tinkering behavior
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Serpentine));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Silver));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, null));

            // a raw gem of the same material is NOT a bag: same MaterialType, different ItemType
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.Gem, MaterialType.TigerEye));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.Jewelry, MaterialType.Obsidian));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.Armor, MaterialType.TigerEye));

            Assert.IsFalse(EquipmentModManager.IsModMaterial(null));
        }

        [TestMethod]
        public void FullBag_TreatsAMissingMaxStructureAsOneHundred()
        {
            // the salvage bag weenies carry no MaxStructure at all - Player_Crafting.TryAddSalvage defaults it
            // to 100 - so a real full bag is Structure 100 with MaxStructure null
            Assert.IsTrue(EquipmentModManager.IsFullBag(EquipmentModManager.DefaultMaxStructure, null), "the fallback must be the same 100 the salvaging code uses");
            Assert.IsFalse(EquipmentModManager.IsFullBag(EquipmentModManager.DefaultMaxStructure - 1, null));

            Assert.IsTrue(EquipmentModManager.IsFullBag(100, null));
            Assert.IsFalse(EquipmentModManager.IsFullBag(99, null));
            Assert.IsFalse(EquipmentModManager.IsFullBag(1, null));
            Assert.IsFalse(EquipmentModManager.IsFullBag(0, null));
            Assert.IsFalse(EquipmentModManager.IsFullBag(null, null));

            // an explicit MaxStructure is honored when present
            Assert.IsTrue(EquipmentModManager.IsFullBag(50, 50));
            Assert.IsFalse(EquipmentModManager.IsFullBag(49, 50));
            Assert.IsTrue(EquipmentModManager.IsFullBag(100, 100));

            Assert.IsFalse(EquipmentModManager.IsFullBag(null));
        }

        // ---------------- target eligibility ----------------

        [TestMethod]
        public void EligibleSlots_CoverWhereGearRatingsRoll()
        {
            // armor and clothing
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.ChestArmor, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.ChestArmor | EquipMask.AbdomenArmor, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.HandWear, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.FootWear, false));

            // crowns are head wear, and roll ratings today
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.HeadWear, false));

            // cloaks and classic jewelry
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.Cloak, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.NeckWear, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.WristWearLeft, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.FingerWearRight, false));

            // the trinket slot: the six loot trinkets are JewelryWcids entries, so a jewelry roll marks them
            // TreasureItemType.Jewelry and TryMutateGearRating's roll.IsJewelry branch gives them
            // GearHealingBoost / GearMaxHealth at t8 - they carry ratings, so they must be convertible
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.TrinketOne, false));
        }

        /// <summary>
        /// The trinket slot in its own right, pinned by raw value as well as by name. The six loot trinkets
        /// (wcids 41483-41488) all declare ValidLocations (PropertyInt 9) = 0x04000000 in ace_world, which is
        /// exactly EquipMask.TrinketOne - so an item arriving with that literal mask must be eligible.
        /// </summary>
        [TestMethod]
        public void TrinketSlot_IsEligible_BecauseTrinketsRollGearRatings()
        {
            // 0x04000000 is the literal ValidLocations those six weenies carry, asserted as the raw value
            // rather than through the enum name so a rename cannot quietly decouple the two
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot((EquipMask)0x04000000, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(EquipMask.TrinketOne, false));

            Assert.IsTrue(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.TrinketOne)));
            Assert.IsTrue(EquipmentModManager.IsEligibleTarget(MakeItem((EquipMask)0x04000000)));
        }

        /// <summary>
        /// The other half of the boundary: sigils stay excluded. Aetheria never passes through a TreasureRoll
        /// (LootGenerationFactory.TryRollAetheria -> CreateAetheria -> MutateAetheria is its own mundane
        /// add-on path), so it never reaches TryMutateGearRating and carries no Gear* property to convert.
        /// </summary>
        [TestMethod]
        public void SigilSlots_StayIneligible_BecauseAetheriaCarriesNoRatings()
        {
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.SigilOne, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.SigilTwo, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.SigilThree, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.Sigil, false));

            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.SigilOne)));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.Sigil)));
        }

        [TestMethod]
        public void IneligibleSlots_ExcludeWeaponsShieldsAndSigils()
        {
            // a shield is refused by the shield flag even though its slot mask says nothing
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.Shield, true));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.Shield, false));

            // an armored item that is flagged as a shield is still refused
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.ChestArmor, true));

            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.MeleeWeapon, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.MissileWeapon, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.TwoHanded, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.MissileAmmo, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.Held, false));

            // the sigil slots (Aetheria) are NOT gear-rating carriers in lootgen, and are excluded on
            // purpose - EquipMask.Jewelry would have swept them in. See
            // SigilSlots_StayIneligible_BecauseAetheriaCarriesNoRatings for why. The trinket slot, which
            // EquipMask.Jewelry also covers, IS eligible - trinkets do roll ratings.
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.SigilOne, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.SigilTwo, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.SigilThree, false));

            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(EquipMask.None, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(null));
        }

        [TestMethod]
        public void IsEligibleTarget_ReadsTheItemsOwnSlots()
        {
            Assert.IsTrue(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.ChestArmor)));
            Assert.IsTrue(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.Cloak)));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.MeleeWeapon)));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.SigilOne)));
        }

        // ---------------- rating sums ----------------

        [TestMethod]
        public void GearRatingProperties_AreTheTenLiveRatings()
        {
            Assert.AreEqual(10, EquipmentModManager.GearRatingProperties.Length);
            Assert.AreEqual(10, EquipmentModManager.GearRatingProperties.Distinct().Count(), "duplicate rating property");

            // the exact set the equipped-items rating cache in Creature_Equipment.cs maintains
            var expected = new[]
            {
                PropertyInt.GearDamage,
                PropertyInt.GearDamageResist,
                PropertyInt.GearCrit,
                PropertyInt.GearCritResist,
                PropertyInt.GearCritDamage,
                PropertyInt.GearCritDamageResist,
                PropertyInt.GearHealingBoost,
                PropertyInt.GearMaxHealth,
                PropertyInt.GearPKDamageRating,
                PropertyInt.GearPKDamageResistRating,
            };

            CollectionAssert.AreEquivalent(expected, EquipmentModManager.GearRatingProperties);
        }

        [TestMethod]
        public void SumGearRatings_AddsEveryRatingProperty()
        {
            Assert.AreEqual(0, EquipmentModManager.SumGearRatings(MakeItem()));
            Assert.AreEqual(0, EquipmentModManager.SumGearRatings(null));

            // the common case: one rating of 1 on a T8 armor piece
            var single = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearCritDamage, 1 } });
            Assert.AreEqual(1, EquipmentModManager.SumGearRatings(single));

            // a rating of 3 converts into 3 mods
            var three = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 3 } });
            Assert.AreEqual(3, EquipmentModManager.SumGearRatings(three));

            // spread across several rating properties: the POINTS are what count, not the property count
            var spread = MakeItem(ints: new Dictionary<PropertyInt, int>
            {
                { PropertyInt.GearDamage, 1 },
                { PropertyInt.GearCritDamageResist, 2 },
                { PropertyInt.GearMaxHealth, 1 },
            });
            Assert.AreEqual(4, EquipmentModManager.SumGearRatings(spread));

            // every rating at once
            var all = MakeItem(ints: EquipmentModManager.GearRatingProperties.ToDictionary(p => p, _ => 1));
            Assert.AreEqual(10, EquipmentModManager.SumGearRatings(all));
        }

        // ---------------- the decision table ----------------

        [TestMethod]
        public void LowTier_AppliesOnlyToAnUnratedUnmoddedItem()
        {
            var refusal = EquipmentModManager.ResolveAction(MaterialType.TigerEye, ratingPoints: 0, modCount: 0, capacity: 0, out var action, out var count);

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, refusal);
            Assert.AreEqual(EquipmentModManager.ModAction.LowTierApply, action);
            Assert.AreEqual(1, count, "an unrated item's capacity is 1");
        }

        [TestMethod]
        public void LowTier_IsRefusedOnRatedOrAlreadyModifiedItems()
        {
            // rated item: that is the high-tier conversion's job
            var rated = EquipmentModManager.ResolveAction(MaterialType.TigerEye, ratingPoints: 1, modCount: 0, capacity: 0, out var action, out var count);
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.LowTierOnRatedItem, rated);
            Assert.AreEqual(EquipmentModManager.ModAction.None, action);
            Assert.AreEqual(0, count);

            // already carries a mod
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.LowTierOnModifiedItem,
                EquipmentModManager.ResolveAction(MaterialType.TigerEye, 0, 1, 1, out _, out _));

            // capacity stamped but mods since cleared - still not a low-tier target
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.LowTierOnModifiedItem,
                EquipmentModManager.ResolveAction(MaterialType.TigerEye, 0, 0, 1, out _, out _));

            // rated AND modified reports the rating refusal first (the actionable advice)
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.LowTierOnRatedItem,
                EquipmentModManager.ResolveAction(MaterialType.TigerEye, 2, 1, 1, out _, out _));
        }

        [TestMethod]
        public void HighTier_ConvertsRatedItemsAndCountEqualsRatingPoints()
        {
            foreach (var points in new[] { 1, 2, 3, 10 })
            {
                var refusal = EquipmentModManager.ResolveAction(MaterialType.Obsidian, points, modCount: 0, capacity: 0, out var action, out var count);

                Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, refusal);
                Assert.AreEqual(EquipmentModManager.ModAction.Convert, action);
                Assert.AreEqual(points, count, "one mod per rating point");
            }

            // ratings take priority over a reroll: a rated item is always converted, never rerolled
            var mixed = EquipmentModManager.ResolveAction(MaterialType.Obsidian, 2, modCount: 3, capacity: 3, out var mixedAction, out var mixedCount);
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, mixed);
            Assert.AreEqual(EquipmentModManager.ModAction.Convert, mixedAction);
            Assert.AreEqual(2, mixedCount);
        }

        [TestMethod]
        public void HighTier_RerollsConvertedItemsAndPreservesTheModCount()
        {
            var refusal = EquipmentModManager.ResolveAction(MaterialType.Obsidian, ratingPoints: 0, modCount: 3, capacity: 3, out var action, out var count);

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, refusal);
            Assert.AreEqual(EquipmentModManager.ModAction.Reroll, action);
            Assert.AreEqual(3, count, "a reroll keeps the count fixed at the item's capacity");

            // a low-tier item (capacity 1) rerolls to 1
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None,
                EquipmentModManager.ResolveAction(MaterialType.Obsidian, 0, 1, 1, out var lowAction, out var lowCount));
            Assert.AreEqual(EquipmentModManager.ModAction.Reroll, lowAction);
            Assert.AreEqual(1, lowCount);

            // the reroll count follows CAPACITY, not the current mod count, so a partially stripped item is
            // refilled to its born bound
            EquipmentModManager.ResolveAction(MaterialType.Obsidian, 0, modCount: 1, capacity: 3, out _, out var refillCount);
            Assert.AreEqual(3, refillCount);
        }

        [TestMethod]
        public void HighTier_IsRefusedWhenThereIsNothingToConvertOrReroll()
        {
            // a plain unrated, unmodded item
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.NothingToConvertOrReroll,
                EquipmentModManager.ResolveAction(MaterialType.Obsidian, 0, 0, 0, out var action, out var count));
            Assert.AreEqual(EquipmentModManager.ModAction.None, action);
            Assert.AreEqual(0, count);

            // capacity stamped but every mod stripped: nothing to reroll
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.NothingToConvertOrReroll,
                EquipmentModManager.ResolveAction(MaterialType.Obsidian, 0, 0, 2, out _, out _));
        }

        [TestMethod]
        public void ConvertedCapacity_IsTheBornRatingBound()
        {
            Assert.AreEqual(1, EquipmentModManager.ComputeConvertedCapacity(0, 1));
            Assert.AreEqual(3, EquipmentModManager.ComputeConvertedCapacity(0, 3));

            // defensive: a rated item that somehow already had a mod grows rather than orphaning it
            Assert.AreEqual(3, EquipmentModManager.ComputeConvertedCapacity(1, 2));
        }

        // ---------------- distinct type selection ----------------

        [TestMethod]
        public void RollDistinctModTypes_NeverRepeatsAType()
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                var rolled = EquipmentModRoller.RollDistinctModTypes(3);

                Assert.AreEqual(3, rolled.Count);
                Assert.AreEqual(3, rolled.Distinct().Count(), "no duplicate mod types on one item");

                foreach (var id in rolled)
                    Assert.IsTrue(EquipmentModRegistry.TryGet(id, out _));
            }
        }

        [TestMethod]
        public void RollDistinctModTypes_ExcludesTypesTheItemAlreadyCarries()
        {
            var existing = new List<EquipmentModId> { EquipmentModId.Deadeye, EquipmentModId.Venom, EquipmentModId.Thorns };

            for (var attempt = 0; attempt < 500; attempt++)
            {
                var rolled = EquipmentModRoller.RollDistinctModTypes(2, existing);

                Assert.AreEqual(2, rolled.Count);
                Assert.AreEqual(2, rolled.Distinct().Count());

                foreach (var id in rolled)
                    Assert.IsFalse(existing.Contains(id), $"rolled {id}, which the item already carries");
            }
        }

        [TestMethod]
        public void RollDistinctModTypes_HandlesDegenerateCounts()
        {
            Assert.AreEqual(0, EquipmentModRoller.RollDistinctModTypes(0).Count);
            Assert.AreEqual(0, EquipmentModRoller.RollDistinctModTypes(-1).Count);

            // asking for the whole catalog returns the whole catalog, each type once
            var all = EquipmentModRoller.RollDistinctModTypes(EquipmentModRegistry.AllMods.Count);
            Assert.AreEqual(EquipmentModRegistry.AllMods.Count, all.Count);
            Assert.AreEqual(EquipmentModRegistry.AllMods.Count, all.Distinct().Count());

            // asking for more than exists returns what exists instead of looping forever
            var tooMany = EquipmentModRoller.RollDistinctModTypes(EquipmentModRegistry.AllMods.Count + 5);
            Assert.AreEqual(EquipmentModRegistry.AllMods.Count, tooMany.Count);

            // fully excluded catalog
            Assert.AreEqual(0, EquipmentModRoller.RollDistinctModTypes(1, EquipmentModRegistry.AllMods.Select(m => m.Id).ToList()).Count);
        }

        // ---------------- item state reads and writes ----------------

        [TestMethod]
        public void ModCountAndCapacity_ReadTheItemsProperties()
        {
            var item = MakeItem();

            Assert.AreEqual(0, EquipmentModManager.GetModCount(item));
            Assert.AreEqual(0, EquipmentModManager.GetModCapacity(item));
            Assert.AreEqual(0, EquipmentModManager.GetModCapacity(null));

            item.SetProperty(PropertyFloat.GearModDeadeye, 0.5);
            item.SetProperty(PropertyFloat.GearModVenom, 0.25);
            item.SetProperty(PropertyInt.GearModCapacity, 2);

            Assert.AreEqual(2, EquipmentModManager.GetModCount(item));
            Assert.AreEqual(2, EquipmentModManager.GetModCapacity(item));

            // a potency of 0 is a legitimate (worthless) roll and must still count as an occupied slot
            var zeroRoll = MakeItem();
            zeroRoll.SetProperty(PropertyFloat.GearModThorns, 0.0);
            Assert.AreEqual(1, EquipmentModManager.GetModCount(zeroRoll));
        }

        [TestMethod]
        public void ClearMods_RemovesEveryPotencyButKeepsCapacity()
        {
            var item = MakeItem();

            foreach (var mod in EquipmentModRegistry.AllMods.Take(3))
                item.SetProperty(mod.Property, 0.5);

            item.SetProperty(PropertyInt.GearModCapacity, 3);
            item.SetProperty(PropertyFloat.WeaponDefense, 1.1);   // an unrelated float must survive

            EquipmentModManager.ClearMods(item);

            Assert.AreEqual(0, EquipmentModManager.GetModCount(item));
            Assert.AreEqual(3, EquipmentModManager.GetModCapacity(item), "capacity is the born-rating bound and survives a reroll");
            Assert.AreEqual(1.1, item.GetProperty(PropertyFloat.WeaponDefense).Value, 1e-12);

            foreach (var mod in EquipmentModRegistry.AllMods)
                Assert.IsNull(item.GetProperty(mod.Property), $"{mod.Id} potency was not removed");

            EquipmentModManager.ClearMods(null);   // must not throw
        }

        [TestMethod]
        public void LowTierPotencyWrite_MatchesTheTunable()
        {
            // what the low-tier path stamps on a mod sitting at the catalog default floor: the tunable value
            // (0.2, which is above that floor), not a roll
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);
            var expected = PropertyManager.GetDouble("equipment_mod_lowtier_potency").Item;

            Assert.AreEqual(0.2, expected, 1e-12);
            Assert.AreEqual(expected, EquipmentModRoller.LowTierPotency(deadeye), 1e-12);

            var item = MakeItem();
            item.SetProperty(PropertyFloat.GearModDeadeye, EquipmentModRoller.LowTierPotency(deadeye));

            var mods = EquipmentModDisplay.GetMods(item);
            Assert.AreEqual(1, mods.Count);
            Assert.AreEqual(0.2, mods[0].Potency, 1e-12);

            // and it resolves to 20% of the mod's maximum
            Assert.AreEqual(0.0056, EquipmentModValue.Resolve(mods[0].Definition, mods[0].Potency), 1e-12);
        }

        [TestMethod]
        public void AppraisalLines_RenderModsAndCapacityFromTheItem()
        {
            var item = MakeItem();

            Assert.AreEqual(0, EquipmentModDisplay.GetAppraisalLines(item).Count, "an unmodded item contributes nothing");
            Assert.AreEqual(0, EquipmentModDisplay.GetAppraisalLines(null).Count);

            item.SetProperty(PropertyFloat.GearModDeadeye, 1.0);
            item.SetProperty(PropertyFloat.GearModVenom, 0.2);
            item.SetProperty(PropertyInt.GearModCapacity, 2);

            var lines = EquipmentModDisplay.GetAppraisalLines(item);

            CollectionAssert.AreEqual(new[]
            {
                "- Deadeye: +2.8% missile damage",
                "- Venom: +0.82 poison damage per hit",
                "- Mod Capacity: 2",
            }, lines);
        }

        [TestMethod]
        public void Describe_IsTheSharedRenderingForAppraisalAndSuccessMessages()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            Assert.AreEqual("Deadeye: +2.8% missile damage", EquipmentModDisplay.Describe(deadeye, 1.0));
            Assert.AreEqual("Deadeye: +0.56% missile damage", EquipmentModDisplay.Describe(deadeye, 0.2));
            Assert.AreEqual("Deadeye: +0% missile damage", EquipmentModDisplay.Describe(deadeye, 0.0));

            // clamped: a contaminated potency renders at the maximum, never above it
            Assert.AreEqual("Deadeye: +2.8% missile damage", EquipmentModDisplay.Describe(deadeye, 7.0));

            Assert.AreEqual(string.Empty, EquipmentModDisplay.Describe(null, 1.0));
        }
    }
}
