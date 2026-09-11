using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
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
        /// <param name="ints">
        /// Properties written BEFORE the born-with stamp, so a gear rating passed here reads as NATURAL -
        /// the same thing WorldObjectFactory.CreateNewWorldObject does to a weenie-authored rating.
        /// </param>
        /// <param name="crafted">
        /// Gear ratings written AFTER the stamp, so they read as CRAFTED: what a Luminous or Empowered Amber
        /// gem leaves behind when its recipe adds a rating to a finished item. Nothing stamps these, which
        /// is the whole reason Obsidian will not convert them.
        /// </param>
        private static WorldObject MakeItem(EquipMask validLocations = EquipMask.ChestArmor, Dictionary<PropertyInt, int> ints = null, Dictionary<PropertyInt, int> crafted = null, ItemType itemType = ItemType.Armor)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ValidLocations, (int)validLocations },
                    { PropertyInt.ItemType, (int)itemType },

                    // ADDED 2026-08-07 with the workmanship term. A stamped mod is now potency x
                    // workmanship/10, so an item with no workmanship takes a mod worth zero (and is refused
                    // outright by the real flow). 10 is both the neutral value - it leaves every pre-existing
                    // expectation in this file unchanged - and the realistic one: the 963 modded items on
                    // stage average workmanship 9.99. Override via the ints parameter to test other values.
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Item" } },
            };

            if (ints != null)
            {
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;
            }

            var wo = new Clothing(weenie, new ObjectGuid(nextGuid++));

            // mirrors production: every item is stamped once at creation, from whatever its template (or,
            // for loot, its roll) gave it - see WorldObjectFactory.CreateNewWorldObject
            EquipmentModManager.StampOriginalGearRatings(wo);

            if (crafted != null)
            {
                foreach (var kvp in crafted)
                    wo.SetProperty(kvp.Key, kvp.Value);
            }

            return wo;
        }

        // ---------------- material recognition ----------------

        [TestMethod]
        public void ModMaterial_MatchesOnlyTheOneDesignatedSalvageBag()
        {
            Assert.IsTrue(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Obsidian));

            // pinned by raw id as well as by name: MaterialType 69 (0x45) is Obsidian, verified against the
            // ace_world salvage bag weenie 21063 "materialobsidian" (ItemType.TinkeringMaterial, MaterialType 69)
            Assert.IsTrue(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, (MaterialType)0x45));

            // TIGER EYE HAS LEFT THIS SYSTEM. It is now the armor tinker
            // (ACE.Server.Entity.TigerEyeArmorTinker), claimed by its own intercept ahead of this one, so the
            // mod system must not answer to it at all - if it did, the two would race for the same bag.
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.TigerEye));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, (MaterialType)0x2A));

            // other salvage bags keep their ordinary tinkering behavior
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Serpentine));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Silver));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, null));

            // a raw gem of the same material is NOT a bag: same MaterialType, different ItemType
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.Gem, MaterialType.Obsidian));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.Jewelry, MaterialType.Obsidian));
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.Armor, MaterialType.Obsidian));

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

        /// <summary>
        /// Repo-owner ruling 2026-08-30 (second pass, same day): eligibility is ARMOR ONLY, not general
        /// clothing. Obsidian shares TigerEyeArmorTinker.IsEligibleItemClass's rule wholesale: ItemType.Armor
        /// outright, or ItemType.Clothing whose ValidLocations is EXACTLY one extremity slot (head, hands or
        /// feet). Shirts, pants, robes, cloaks, jewelry and the trinket slot are all excluded even though
        /// several of them still roll gear ratings in lootgen (LootGenerationFactory_Clothing is unchanged) -
        /// ratings on those classes remain as ratings rather than becoming mods.
        /// </summary>
        [TestMethod]
        public void EligibleSlot_IsArmorOrExtremityClothingOnly()
        {
            // an ordinary armor chest piece
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(ItemType.Armor, EquipMask.ChestArmor, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(ItemType.Armor, EquipMask.ChestArmor | EquipMask.AbdomenArmor, false));

            // helm, gauntlets and boots are ItemType.Clothing carrying EXACTLY one extremity bit - eligible
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(ItemType.Clothing, EquipMask.HeadWear, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(ItemType.Clothing, EquipMask.HandWear, false));
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(ItemType.Clothing, EquipMask.FootWear, false));

            // a plate helm is ItemType.Armor, so it passes on the armor branch alone regardless of slot mask
            Assert.IsTrue(EquipmentModManager.IsEligibleSlot(ItemType.Armor, EquipMask.HeadWear, false));

            // a shirt: ItemType.Clothing, ChestWear only - not an extremity, so refused
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Clothing, EquipMask.ChestWear, false));

            // a hooded robe: ItemType.Clothing covering an extremity (head) PLUS non-extremity slots - the
            // equality test is the whole point, since a flag test would have let this slip in through the
            // HeadWear bit
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Clothing,
                EquipMask.HeadWear | EquipMask.ChestWear | EquipMask.AbdomenWear | EquipMask.UpperArmWear | EquipMask.LowerArmWear, false));
        }

        /// <summary>
        /// The trinket slot in its own right, pinned by raw value as well as by name. The six loot trinkets
        /// (wcids 41483-41488) all declare ValidLocations (PropertyInt 9) = 0x04000000 in ace_world, which is
        /// exactly EquipMask.TrinketOne, and ItemType.Jewelry. They roll gear ratings via
        /// TryMutateGearRating's roll.IsJewelry branch, but the armor-only ruling excludes them from Obsidian
        /// mod eligibility regardless - ratings on trinkets remain as ratings.
        /// </summary>
        [TestMethod]
        public void TrinketSlot_IsIneligible_DespiteRollingGearRatings()
        {
            // 0x04000000 is the literal ValidLocations those six weenies carry, asserted as the raw value
            // rather than through the enum name so a rename cannot quietly decouple the two
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, (EquipMask)0x04000000, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.TrinketOne, false));

            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.TrinketOne, itemType: ItemType.Jewelry)));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem((EquipMask)0x04000000, itemType: ItemType.Jewelry)));
        }

        /// <summary>
        /// The other half of the boundary: sigils stay excluded. Aetheria never passes through a TreasureRoll
        /// (LootGenerationFactory.TryRollAetheria -> CreateAetheria -> MutateAetheria is its own mundane
        /// add-on path), so it never reaches TryMutateGearRating and carries no Gear* property to convert.
        /// </summary>
        [TestMethod]
        public void SigilSlots_StayIneligible_BecauseAetheriaCarriesNoRatings()
        {
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.SigilOne, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.SigilTwo, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.SigilThree, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.Sigil, false));

            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.SigilOne, itemType: ItemType.Jewelry)));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.Sigil, itemType: ItemType.Jewelry)));
        }

        [TestMethod]
        public void IneligibleSlots_ExcludeWeaponsShieldsClothingAndJewelry()
        {
            // a shield is refused by the isShield FLAG, not by its equip mask - a genuine shield weenie is
            // ItemType.Armor (so it would otherwise pass) and derives IsShield from CombatUse == Shield
            // rather than from ValidLocations, which is why this predicate takes isShield as its own bool
            // parameter instead of trying to read it out of EquipMask
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Armor, EquipMask.Shield, true));

            // an armored item that is flagged as a shield is still refused
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Armor, EquipMask.ChestArmor, true));

            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.MeleeWeapon, EquipMask.MeleeWeapon, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.MissileWeapon, EquipMask.MissileWeapon, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.MeleeWeapon, EquipMask.TwoHanded, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.MissileWeapon, EquipMask.MissileAmmo, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Misc, EquipMask.Held, false));

            // sigils (Aetheria): see SigilSlots_StayIneligible_BecauseAetheriaCarriesNoRatings
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.SigilOne, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.SigilTwo, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.SigilThree, false));

            // cloak: ItemType.Clothing but not an extremity-only mask
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Clothing, EquipMask.Cloak, false));

            // classic jewelry and the trinket slot: ItemType.Jewelry, so the ItemType.Clothing branch never
            // even applies - see TrinketSlot_IsIneligible_DespiteRollingGearRatings
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.NeckWear, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.WristWearLeft, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.FingerWearRight, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.Jewelry, EquipMask.TrinketOne, false));

            Assert.IsFalse(EquipmentModManager.IsEligibleSlot(ItemType.None, EquipMask.None, false));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(null));
        }

        [TestMethod]
        public void IsEligibleTarget_ReadsTheItemsOwnItemTypeAndSlots()
        {
            Assert.IsTrue(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.ChestArmor, itemType: ItemType.Armor)));
            Assert.IsTrue(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.HeadWear, itemType: ItemType.Clothing)));
            Assert.IsTrue(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.HandWear, itemType: ItemType.Clothing)));
            Assert.IsTrue(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.FootWear, itemType: ItemType.Clothing)));

            // a shirt: Clothing but not extremity-only
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.ChestWear, itemType: ItemType.Clothing)));

            // a hooded robe: Clothing covering head plus torso and arms
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(
                EquipMask.HeadWear | EquipMask.ChestWear | EquipMask.AbdomenWear | EquipMask.UpperArmWear | EquipMask.LowerArmWear,
                itemType: ItemType.Clothing)));

            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.Cloak, itemType: ItemType.Clothing)));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.MeleeWeapon, itemType: ItemType.MeleeWeapon)));
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(MakeItem(EquipMask.SigilOne, itemType: ItemType.Jewelry)));
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

        // ---------------- born-with stamps: what separates a rolled rating from a crafted one ----------------

        [TestMethod]
        public void OriginalGearRatingProperties_ShadowTheLiveRatingsIndexForIndex()
        {
            // the two arrays are read together by ordinal in StampOriginalGearRatings and
            // SpendNaturalRatings, so a length or order drift silently pairs the wrong two properties
            Assert.AreEqual(EquipmentModManager.GearRatingProperties.Length, EquipmentModManager.OriginalGearRatingProperties.Length);

            var expected = new[]
            {
                PropertyInt.GearDamageOriginal,
                PropertyInt.GearDamageResistOriginal,
                PropertyInt.GearCritOriginal,
                PropertyInt.GearCritResistOriginal,
                PropertyInt.GearCritDamageOriginal,
                PropertyInt.GearCritDamageResistOriginal,
                PropertyInt.GearHealingBoostOriginal,
                PropertyInt.GearMaxHealthOriginal,
                PropertyInt.GearPKDamageRatingOriginal,
                PropertyInt.GearPKDamageResistRatingOriginal,
            };

            CollectionAssert.AreEqual(expected, EquipmentModManager.OriginalGearRatingProperties);

            // pinned by raw id too: these are fork ids 9046-9055, reserved in Source/property-registry.tsv
            for (var i = 0; i < expected.Length; i++)
                Assert.AreEqual(9046 + i, (int)expected[i]);
        }

        [TestMethod]
        public void StampOriginalGearRatings_RecordsLiveRatingsAndWritesNoZeroRows()
        {
            var item = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearCritDamage, 3 } });

            Assert.AreEqual(3, item.GetProperty(PropertyInt.GearCritDamageOriginal));

            // an unrated property gets no row at all, rather than a 0 - same rule the mod systems follow
            Assert.IsNull(item.GetProperty(PropertyInt.GearDamageOriginal));

            // OVERWRITES rather than accumulates, which is what makes the loot path's second call safe:
            // CreateNewWorldObject stamps the (rating-free) template, then TryMutateGearRating stamps the roll
            item.SetProperty(PropertyInt.GearCritDamage, 5);
            EquipmentModManager.StampOriginalGearRatings(item);
            Assert.AreEqual(5, item.GetProperty(PropertyInt.GearCritDamageOriginal));

            // and a rating that goes away takes its stamp with it
            item.RemoveProperty(PropertyInt.GearCritDamage);
            EquipmentModManager.StampOriginalGearRatings(item);
            Assert.IsNull(item.GetProperty(PropertyInt.GearCritDamageOriginal));

            EquipmentModManager.StampOriginalGearRatings(null);   // must not throw
        }

        [TestMethod]
        public void SumGearRatings_CountsOnlyWhatTheItemWasBornWith()
        {
            // A LUMINOUS AMBER GEM ON AN OTHERWISE UNRATED PIECE BUYS NOTHING. Recipe 8913 adds
            // GearHealingBoost +2 to a helm through RecipeManager.ModifyInt, which writes the live property
            // and nothing else - so there is no stamp beside it and no mod to be had.
            var gemmedOnly = MakeItem(crafted: new Dictionary<PropertyInt, int> { { PropertyInt.GearHealingBoost, 2 } });
            Assert.AreEqual(2, gemmedOnly.GetProperty(PropertyInt.GearHealingBoost));
            Assert.AreEqual(0, EquipmentModManager.SumGearRatings(gemmedOnly));

            // NATURAL PLUS CRAFTED PAYS OUT FOR THE NATURAL HALF ONLY. A T8 piece born with
            // GearCritDamage 2 that then took an Empowered Amber GearMaxHealth +1 is worth two mods, not three.
            var both = MakeItem(
                ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearCritDamage, 2 } },
                crafted: new Dictionary<PropertyInt, int> { { PropertyInt.GearMaxHealth, 1 } });
            Assert.AreEqual(2, EquipmentModManager.SumGearRatings(both));

            // a gem that stacked onto the SAME property the item was born with is still only worth the born half
            var stacked = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 1 } });
            stacked.SetProperty(PropertyInt.GearDamage, 2);
            Assert.AreEqual(1, EquipmentModManager.SumGearRatings(stacked));
        }

        [TestMethod]
        public void SpendNaturalRatings_TakesTheBornHalfAndLeavesTheCraftedHalf()
        {
            var item = MakeItem(
                ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearCritDamage, 2 } },
                crafted: new Dictionary<PropertyInt, int> { { PropertyInt.GearMaxHealth, 1 } });

            var writes = EquipmentModManager.SpendNaturalRatings(item);

            // the natural rating is spent in full, and is reported as a removal for the caller to push
            Assert.IsTrue(writes.ContainsKey(PropertyInt.GearCritDamage));
            Assert.IsNull(writes[PropertyInt.GearCritDamage]);

            // the crafted one is not touched at all - not spent, and not even reported
            Assert.IsFalse(writes.ContainsKey(PropertyInt.GearMaxHealth));
            Assert.AreEqual(1, item.GetProperty(PropertyInt.GearMaxHealth));

            // every stamp is gone, so the conversion cannot be paid for twice
            foreach (var stamp in EquipmentModManager.OriginalGearRatingProperties)
                Assert.IsNull(item.GetProperty(stamp));

            Assert.AreEqual(0, EquipmentModManager.SumGearRatings(item));
        }

        [TestMethod]
        public void SpendNaturalRatings_LeavesTheRemainderWhenAGemStackedOnTheSameProperty()
        {
            // born with GearDamage 1, then a gem added another point to the same property
            var item = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 1 } });
            item.SetProperty(PropertyInt.GearDamage, 3);

            var writes = EquipmentModManager.SpendNaturalRatings(item);

            Assert.AreEqual(2, writes[PropertyInt.GearDamage]);
            Assert.IsNull(item.GetProperty(PropertyInt.GearDamageOriginal));
        }

        [TestMethod]
        public void SpendNaturalRatings_ClampsAStampAboveTheLiveRating()
        {
            // an admin edit or a content change could leave a stamp higher than the rating; the rating must
            // land on nothing rather than going negative
            var item = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 4 } });
            item.SetProperty(PropertyInt.GearCrit, 1);

            var writes = EquipmentModManager.SpendNaturalRatings(item);

            Assert.IsNull(writes[PropertyInt.GearCrit]);
            Assert.IsNull(item.GetProperty(PropertyInt.GearCritOriginal));

            Assert.AreEqual(0, EquipmentModManager.SpendNaturalRatings(null).Count);
        }

        [TestMethod]
        public void ConvertedItemCannotBePaidOutAgainAfterAGemRearmsTheRating()
        {
            // THE LOOP THE STAMPS EXIST TO CLOSE. Each amber recipe's "already imbued" gate reads the very
            // Gear* property a conversion clears (recipe 8913 gates on GearHealingBoost, for one), so after a
            // conversion the gem can be applied a second time. What must not follow is a second payout.
            var item = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearHealingBoost, 2 } });

            Assert.AreEqual(2, EquipmentModManager.SumGearRatings(item));
            EquipmentModManager.SpendNaturalRatings(item);
            item.SetProperty(PropertyInt.GearModCapacity, 2);

            // the gem lands again, because its own gate sees a cleared rating property
            item.SetProperty(PropertyInt.GearHealingBoost, 2);

            // ...and buys nothing: with no stamp there are no rating points, so the item resolves to Reroll
            Assert.AreEqual(0, EquipmentModManager.SumGearRatings(item));

            var refusal = EquipmentModManager.ResolveAction(
                EquipmentModManager.SumGearRatings(item), modCount: 2, capacity: 2, out var action, out var count);

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, refusal);
            Assert.AreEqual(EquipmentModManager.ModAction.Reroll, action);
            Assert.AreEqual(2, count);
        }

        // ---------------- the decision table ----------------

        /// <summary>
        /// The low-tier (TigerEye) arm of the decision table was REMOVED when tiger eye became an armor
        /// tinker. An unrated, unmodded item is now simply refused - the two tests that used to assert
        /// LowTierApply / LowTierOnRatedItem / LowTierOnModifiedItem are gone with the enum members, and this
        /// is the replacement that pins the new answer.
        /// </summary>
        [TestMethod]
        public void UnratedUnmoddedItem_IsRefusedOutright_TheLowTierPathIsGone()
        {
            var refusal = EquipmentModManager.ResolveAction(ratingPoints: 0, modCount: 0, capacity: 0, out var action, out var count);

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.NothingToConvertOrReroll, refusal,
                "an unrated, unmodded item has nothing to convert and nothing to reroll, so there is no way in");
            Assert.AreEqual(EquipmentModManager.ModAction.None, action);
            Assert.AreEqual(0, count);
        }

        [TestMethod]
        public void Convert_HandlesRatedItemsAndCountEqualsRatingPoints()
        {
            foreach (var points in new[] { 1, 2, 3, 10 })
            {
                var refusal = EquipmentModManager.ResolveAction(points, modCount: 0, capacity: 0, out var action, out var count);

                Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, refusal);
                Assert.AreEqual(EquipmentModManager.ModAction.Convert, action);
                Assert.AreEqual(points, count, "one mod per rating point");
            }

            // ratings take priority over a reroll: a rated item is always converted, never rerolled
            var mixed = EquipmentModManager.ResolveAction(2, modCount: 3, capacity: 3, out var mixedAction, out var mixedCount);
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, mixed);
            Assert.AreEqual(EquipmentModManager.ModAction.Convert, mixedAction);
            Assert.AreEqual(2, mixedCount);
        }

        [TestMethod]
        public void Reroll_HandlesConvertedItemsAndPreservesTheModCount()
        {
            var refusal = EquipmentModManager.ResolveAction(ratingPoints: 0, modCount: 3, capacity: 3, out var action, out var count);

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, refusal);
            Assert.AreEqual(EquipmentModManager.ModAction.Reroll, action);
            Assert.AreEqual(3, count, "a reroll keeps the count fixed at the item's capacity");

            // a single-mod item (capacity 1) rerolls to 1
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None,
                EquipmentModManager.ResolveAction(0, 1, 1, out var oneAction, out var oneCount));
            Assert.AreEqual(EquipmentModManager.ModAction.Reroll, oneAction);
            Assert.AreEqual(1, oneCount);

            // the reroll count follows CAPACITY, not the current mod count, so a partially stripped item is
            // refilled to its born bound
            EquipmentModManager.ResolveAction(0, modCount: 1, capacity: 3, out _, out var refillCount);
            Assert.AreEqual(3, refillCount);
        }

        [TestMethod]
        public void ResolveAction_IsRefusedWhenThereIsNothingToConvertOrReroll()
        {
            // a plain unrated, unmodded item
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.NothingToConvertOrReroll,
                EquipmentModManager.ResolveAction(0, 0, 0, out var action, out var count));
            Assert.AreEqual(EquipmentModManager.ModAction.None, action);
            Assert.AreEqual(0, count);

            // capacity stamped but every mod stripped: nothing to reroll
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.NothingToConvertOrReroll,
                EquipmentModManager.ResolveAction(0, 0, 2, out _, out _));
        }

        [TestMethod]
        public void ConvertedCapacity_IsTheBornRatingBound()
        {
            Assert.AreEqual(1, EquipmentModManager.ComputeConvertedCapacity(0, 1));
            Assert.AreEqual(3, EquipmentModManager.ComputeConvertedCapacity(0, 3));

            // defensive: a rated item that somehow already had a mod grows rather than orphaning it
            Assert.AreEqual(3, EquipmentModManager.ComputeConvertedCapacity(1, 2));
        }

        // ---------------- the tiger-eye seal (repo-owner ruling, 2026-08-08) ----------------

        /// <summary>
        /// A bare Armor item usable by TigerEyeArmorTinker.ApplyToArmor - the same shape MakeItem produces
        /// (Clothing weenie, ItemType.Armor, ItemWorkmanship 10), plus an ArmorLevel so ApplyToArmor has
        /// something to raise.
        /// </summary>
        private static WorldObject MakeTinkerableItem(EquipMask validLocations = EquipMask.ChestArmor)
        {
            return MakeItem(validLocations, new Dictionary<PropertyInt, int> { { PropertyInt.ArmorLevel, 100 } });
        }

        [TestMethod]
        public void ResolveTigerEyeRefusal_SealsAnItemActuallyTigerEyed()
        {
            var item = MakeTinkerableItem();

            TigerEyeArmorTinker.ApplyToArmor(item, mintMod: false);

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.SealedByTigerEye,
                EquipmentModManager.ResolveTigerEyeRefusal(item),
                "an item that was actually run through ApplyToArmor must be refused by Obsidian");
        }

        /// <summary>
        /// The seal needed no changes to keep working once Tiger Eye started minting its own mod alongside
        /// the steel tinkers (repo-owner ruling, 2026-08-08): ResolveTigerEyeRefusal / MatchesAppliedSignature
        /// key ONLY on NumTimesTinkered plus a pure-Steel TinkerLog, and never read GearModCapacity or any
        /// GearMod* property, so a Tiger-Eye'd item that ALSO carries a minted mod must still seal exactly
        /// the same as one that does not.
        /// </summary>
        [TestMethod]
        public void ResolveTigerEyeRefusal_StillSealsAnItemThatAlsoCarriesAMintedMod()
        {
            var item = MakeTinkerableItem();

            var result = TigerEyeArmorTinker.ApplyToArmor(item, mintMod: true);

            Assert.AreEqual(1, result.ModsApplied.Count, "sanity: the fixture must actually have minted a mod, or this test is vacuous");
            Assert.AreEqual(1, EquipmentModManager.GetModCount(item));
            Assert.AreEqual(1, EquipmentModManager.GetModCapacity(item));

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.SealedByTigerEye,
                EquipmentModManager.ResolveTigerEyeRefusal(item),
                "a minted mod riding alongside the seal must not weaken it - Obsidian must still refuse");
        }

        /// <summary>
        /// The controls proving normal Obsidian use is unaffected: an untouched item, and a gear-rated item
        /// that was never tiger-eye'd, both read None.
        /// </summary>
        [TestMethod]
        public void ResolveTigerEyeRefusal_IsNoneForUntouchedAndGearRatedItems()
        {
            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None,
                EquipmentModManager.ResolveTigerEyeRefusal(MakeItem()),
                "a plain untouched item is unaffected");

            var rated = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 2 } });

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None,
                EquipmentModManager.ResolveTigerEyeRefusal(rated),
                "a gear-rated item that was never tiger-eye'd must still convert normally");

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, EquipmentModManager.ResolveTigerEyeRefusal(null));
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

        /// <summary>
        /// Replaces LowTierPotencyWrite_MatchesTheTunable, which pinned the removed
        /// equipment_mod_lowtier_potency stamp. What survives it is the round trip that test was really
        /// covering: a potency written onto an item reads back unchanged and resolves through the registry.
        /// </summary>
        [TestMethod]
        public void PotencyWrite_RoundTripsThroughTheItemAndResolves()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            var item = MakeItem();
            item.SetProperty(PropertyFloat.GearModDeadeye, 0.2);

            var mods = EquipmentModDisplay.GetMods(item);
            Assert.AreEqual(1, mods.Count);
            Assert.AreEqual(deadeye.Id, mods[0].Definition.Id);
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

            // the intensity bracket reaches the panel too (2026-08-07). Deadeye is a perfect roll and Venom a
            // 20% one, so the two brackets differ - a fixed pair would not prove the figure tracks the item.
            // The capacity line carries none: it is a count, not a roll.
            CollectionAssert.AreEqual(new[]
            {
                "- Deadeye [100%]: +2.8% missile damage",
                "- Venom [20%]: +0.82 poison damage per hit",
                "- Mod Capacity: 2",
            }, lines);
        }

        [TestMethod]
        public void Describe_IsTheSharedRenderingForAppraisalAndSuccessMessages()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            Assert.AreEqual("Deadeye [100%]: +2.8% missile damage", EquipmentModDisplay.Describe(deadeye, 1.0));
            Assert.AreEqual("Deadeye [20%]: +0.56% missile damage", EquipmentModDisplay.Describe(deadeye, 0.2));

            // a potency of zero has no intensity to report, so the bracket is DROPPED rather than printed as
            // "[0%]" - the line still names the mod and still shows its magnitude
            Assert.AreEqual("Deadeye: +0% missile damage", EquipmentModDisplay.Describe(deadeye, 0.0));

            // clamped at BOTH ends: a contaminated potency renders at the maximum and reads 100%, never above
            Assert.AreEqual("Deadeye [100%]: +2.8% missile damage", EquipmentModDisplay.Describe(deadeye, 7.0));

            Assert.AreEqual(string.Empty, EquipmentModDisplay.Describe(null, 1.0));
        }

        // ---------------- ALPHA-TEST-ONLY: the equipment-mod maximizer ----------------

        /// <summary>
        /// Phase: the maximizer tool (wcid 1001909 "Awfully OP Mod Hammer", marked by
        /// PropertyInt.EquipmentModMaximizer) never reaches a live Player - EquipmentModManager.MaximizeMods
        /// is the pure, WorldObject-only extraction of its whole rule, so these tests exercise it and
        /// ResolveMaximizeAction directly rather than routing through HandleApply/VerifyUseRequirements
        /// (which need a real Player - see the class remarks).
        /// </summary>
        [TestMethod]
        public void MaximizeMods_SetsEveryExistingModToExactlyOne_AndPreservesTheSet()
        {
            var item = MakeItem();

            item.SetProperty(PropertyFloat.GearModDeadeye, 0.13);
            item.SetProperty(PropertyFloat.GearModVenom, 0.61);
            item.SetProperty(PropertyFloat.GearModThorns, 0.99);

            var applied = EquipmentModManager.MaximizeMods(item);

            Assert.AreEqual(3, applied.Count, "one report line per mod already present");

            // exactly the same three mods, still - no addition, no removal
            var mods = EquipmentModDisplay.GetMods(item);
            Assert.AreEqual(3, mods.Count);

            var byId = mods.ToDictionary(m => m.Definition.Id, m => m.Potency);

            Assert.IsTrue(byId.ContainsKey(EquipmentModId.Deadeye));
            Assert.IsTrue(byId.ContainsKey(EquipmentModId.Venom));
            Assert.IsTrue(byId.ContainsKey(EquipmentModId.Thorns));

            Assert.AreEqual(1.0, byId[EquipmentModId.Deadeye], 1e-12);
            Assert.AreEqual(1.0, byId[EquipmentModId.Venom], 1e-12);
            Assert.AreEqual(1.0, byId[EquipmentModId.Thorns], 1e-12);

            // a mod type that was never on the item stays absent
            Assert.IsFalse(byId.ContainsKey(EquipmentModId.Splitshot), "MaximizeMods must never ADD a mod");
            Assert.IsNull(item.GetProperty(PropertyFloat.GearModSplitshot));
            Assert.AreEqual(3, EquipmentModManager.GetModCount(item), "sanity: mod count unchanged");

            // "MAXIMUM" IS THE BEST THIS ITEM CAN HOLD, NOT A FLAT 1.0 (2026-08-07). The assertions above are
            // on a workmanship 10 item, where the two coincide; on a lesser item the maximizer must stamp the
            // workmanship-scaled ceiling instead. Stamping 1.0 there would put the item above anything a real
            // roll on it could produce, and the appraisal bracket would have to clamp to conceal it.
            var lesser = MakeItem(ints: new Dictionary<PropertyInt, int> { { PropertyInt.ItemWorkmanship, 7 } });

            lesser.SetProperty(PropertyFloat.GearModDeadeye, 0.13);

            EquipmentModManager.MaximizeMods(lesser);

            Assert.AreEqual(0.7, lesser.GetProperty(PropertyFloat.GearModDeadeye).Value, 1e-12,
                "a workmanship 7 item must maximize to 0.7, not to 1.0");

            Assert.AreEqual(70, EquipmentModDisplay.IntensityPercent(
                EquipmentModRegistry.Get(EquipmentModId.Deadeye), lesser.GetProperty(PropertyFloat.GearModDeadeye).Value),
                "and it reads 70% - a maximized lesser item is honestly reported as below a perfect one");
        }

        [TestMethod]
        public void MaximizeMods_OnAnUnmoddedItem_ReturnsNothingAndTouchesNothing()
        {
            var item = MakeItem();

            var applied = EquipmentModManager.MaximizeMods(item);

            Assert.AreEqual(0, applied.Count);
            Assert.AreEqual(0, EquipmentModManager.GetModCount(item));

            foreach (var mod in EquipmentModRegistry.AllMods)
                Assert.IsNull(item.GetProperty(mod.Property), $"{mod.Id} must stay absent on an unmodded item");
        }

        [TestMethod]
        public void MaximizeMods_NullTarget_ReturnsEmptyAndDoesNotThrow()
        {
            var applied = EquipmentModManager.MaximizeMods(null);

            Assert.AreEqual(0, applied.Count);
        }

        [TestMethod]
        public void ResolveMaximizeAction_RefusesAnUnmoddedItem_ButActsOnAModdedOne()
        {
            var refusal = EquipmentModManager.ResolveMaximizeAction(0, out var action, out var count);

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.NothingToMaximize, refusal);
            Assert.AreEqual(EquipmentModManager.ModAction.None, action);
            Assert.AreEqual(0, count);

            var modded = EquipmentModManager.ResolveMaximizeAction(3, out var moddedAction, out var moddedCount);

            Assert.AreEqual(EquipmentModManager.ModActionRefusal.None, modded);
            Assert.AreEqual(EquipmentModManager.ModAction.Maximize, moddedAction);
            Assert.AreEqual(3, moddedCount, "count is the item's current mod count - the maximizer never changes the set");
        }

        [TestMethod]
        public void IsMaximizerSource_ReadsOnlyTheMarkerProperty()
        {
            var plainBag = MakeItem();
            plainBag.SetProperty(PropertyInt.ItemType, (int)ACE.Entity.Enum.ItemType.TinkeringMaterial);
            plainBag.SetProperty(PropertyInt.MaterialType, (int)ACE.Entity.Enum.MaterialType.Obsidian);

            Assert.IsFalse(EquipmentModManager.IsMaximizerSource(plainBag), "an ordinary salvage bag/tool carries no marker");

            var hammer = MakeItem();
            hammer.SetProperty(PropertyInt.EquipmentModMaximizer, 1);

            Assert.IsTrue(EquipmentModManager.IsMaximizerSource(hammer));

            Assert.IsFalse(EquipmentModManager.IsMaximizerSource(null));
        }
    }
}
