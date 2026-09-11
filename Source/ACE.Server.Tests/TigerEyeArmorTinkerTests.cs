using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tiger eye salvage as an ARMOR tinker: source recognition, the full-bag price, the target eligibility
    /// ladder, the rolled steel tinkers, the TinkerLog, and the permanent tinker lock.
    ///
    /// Items are built from in-memory weenies (never persisted, so nothing here touches the database), with
    /// static-range guids, following the PrismaticDriftStoneTests / EquipmentModApplicationTests fixture
    /// pattern. Assertions are invariants - "the delta equals N times the per-tinker step" - never a
    /// hand-derived numeric sequence.
    ///
    /// What is NOT covered here, and would need a live Player with a session, inventory and motion table:
    ///   - TigerEyeArmorTinker.UseObjectOnTarget end to end: the busy/combat guards, the ClapHands chain and
    ///     its TOCTOU re-verification, NextUseTime.
    ///   - VerifyUseRequirements' player-facing half: the LocationsICanMove lookups and the refusal messages.
    ///     Every rule those branches consult IS covered below through its own predicate, which is why the
    ///     predicates exist as separate public methods.
    ///   - The consume itself. What is testable, and is asserted below, is that a partial bag never reaches
    ///     it: the bag is consumed inside Apply, which only runs after VerifyUseRequirements returns None,
    ///     and IsFullBag is what that gate calls.
    ///   - There is no confirmation round trip to cover: this system raises no server-side confirmation at
    ///     all. The gate is the client's own generic tinkering-material prompt (see the class remarks on
    ///     TigerEyeArmorTinker).
    /// </summary>
    [TestClass]
    public class TigerEyeArmorTinkerTests
    {
        private static uint nextGuid = 0x7C000000;   // static guid range, clear of GuidManager
        private static uint nextWcid = 992000;

        /// <summary>How many rolls to take when a test needs to see the whole random range.</summary>
        private const int RollSamples = 2000;

        /// <summary>
        /// A bare item from an in-memory weenie: no database, no dat files (Clothing's SetEphemeralValues is
        /// empty). Enough for every property read/write this flow performs.
        ///
        /// ItemWorkmanship 10 is the default because WorldObject.Workmanship is ItemWorkmanship divided by
        /// NumItemsInMaterial (which defaults to 1), so 10 reads back as workmanship 10 - and because a null
        /// workmanship is itself one of the refusals under test, it has to be opt-in rather than accidental.
        /// </summary>
        private static WorldObject MakeItem(ItemType itemType, EquipMask validLocations, int? armorLevel = 100, int? workmanship = 10, Dictionary<PropertyInt, int> ints = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                    { PropertyInt.ValidLocations, (int)validLocations },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Item" } },
            };

            if (armorLevel != null)
                weenie.PropertiesInt[PropertyInt.ArmorLevel] = armorLevel.Value;

            if (workmanship != null)
                weenie.PropertiesInt[PropertyInt.ItemWorkmanship] = workmanship.Value;

            if (ints != null)
            {
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;
            }

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A plain piece of body armor: the ordinary, always-eligible target.</summary>
        private static WorldObject MakeArmor(int armorLevel = 100) => MakeItem(ItemType.Armor, EquipMask.ChestArmor, armorLevel);

        // ---------------------------------------------------------------------------------------
        // constants
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void Constants_MatchTheRetailSteelTinker()
        {
            // a retail steel armor tinker is a FLAT +20 armor level - RecipeManager.TryMutateNative,
            // case 0x38000011 (Steel), RecipeManager.cs:504: `target.ArmorLevel += 20;`
            Assert.AreEqual(20, TigerEyeArmorTinker.SteelArmorLevelPerTinker);

            // and the tinker history has to say steel, since that is what is being emulated
            Assert.AreEqual(MaterialType.Steel, TigerEyeArmorTinker.TinkerMaterial);

            // MaterialType 42 (0x2A) is TigerEye, pinned by raw id as well as by name: it is the salvage bag
            // weenie 21081 "materialtigereye" and the fork's pre-filled clone 1001751
            Assert.AreEqual(MaterialType.TigerEye, TigerEyeArmorTinker.SourceMaterial);
            Assert.AreEqual((MaterialType)0x2A, TigerEyeArmorTinker.SourceMaterial);

            Assert.AreEqual(1, TigerEyeArmorTinker.MinTinkers);
            Assert.AreEqual(5, TigerEyeArmorTinker.MaxTinkers);

            // the permanent lock matches retail's ten-tinker budget, so ordinary tinkering refuses the item
            Assert.AreEqual(10, TigerEyeArmorTinker.LockedTinkerCount);

            Assert.AreEqual(100, TigerEyeArmorTinker.DefaultMaxStructure);
        }

        // ---------------------------------------------------------------------------------------
        // source recognition
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void IsTigerEyeSalvage_NeedsBothTheItemTypeAndTheMaterial()
        {
            Assert.IsTrue(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringMaterial, MaterialType.TigerEye));

            // a raw tiger eye GEM carries the same MaterialType and must not be taken for a bag
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.Gem, MaterialType.TigerEye));
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.Jewelry, MaterialType.TigerEye));

            // ItemType.TinkeringTool never reaches the server at all - the client hijacks its use for its own
            // salvage panel (GameActionCreateTinkeringTool, 0x027D) - so it must stay refused here
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringTool, MaterialType.TigerEye));

            // a bag of anything else keeps its ordinary behavior
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringMaterial, MaterialType.Obsidian));
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringMaterial, MaterialType.Steel));
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringMaterial, null));

            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage((WorldObject)null));
        }

        /// <summary>
        /// THE TWO SYSTEMS MUST NOT BOTH CLAIM THE SAME BAG. Tiger eye used to be the equipment-mod system's
        /// low tier; this feature took it, and the RecipeManager intercept for it is placed AHEAD of the
        /// equipment-mod one. If EquipmentModManager ever answered to tiger eye again the ordering would be
        /// the only thing keeping them apart, which is exactly the coupling this split removed.
        /// </summary>
        [TestMethod]
        public void TigerEyeHasLeftTheEquipmentModSystemEntirely()
        {
            Assert.IsFalse(EquipmentModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.TigerEye),
                "the equipment-mod system must be Obsidian-only");

            Assert.AreEqual(MaterialType.Obsidian, EquipmentModManager.HighTierMaterial);

            // and the reverse: this feature must not claim the equipment-mod bag
            Assert.IsFalse(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringMaterial, EquipmentModManager.HighTierMaterial));
        }

        // ---------------------------------------------------------------------------------------
        // the full-bag price
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void IsFullBag_TreatsAMissingMaxStructureAsOneHundred()
        {
            // the retail salvage bag weenies carry no MaxStructure at all - Player_Crafting.TryAddSalvage
            // defaults it to 100 - so a real full bag is Structure 100 with MaxStructure null
            Assert.IsTrue(TigerEyeArmorTinker.IsFullBag(TigerEyeArmorTinker.DefaultMaxStructure, null));
            Assert.IsTrue(TigerEyeArmorTinker.IsFullBag(100, 100));

            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(99, null));
            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(1, null));
            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(0, null));
            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(null, null));

            // a partial bag is refused, never partly consumed - a whole bag is the price of one application
            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(50, 100));
            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(99, 100));

            // an over-full bag still counts
            Assert.IsTrue(TigerEyeArmorTinker.IsFullBag(150, 100));

            // a zero capacity is nonsense data and must not read as "full"
            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(0, 0));

            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag((WorldObject)null));
        }

        /// <summary>
        /// The half of "a partial bag is refused and not consumed" that is testable without a live Player:
        /// the consume happens inside Apply, which only runs once VerifyUseRequirements has returned None, and
        /// IsFullBag is the predicate that gate calls. A false here is what stops the bag being spent.
        /// </summary>
        [TestMethod]
        public void PartialBag_FailsTheGateThatGuardsTheConsume()
        {
            var partial = MakeItem(ItemType.TinkeringMaterial, EquipMask.None, armorLevel: null, workmanship: null,
                ints: new Dictionary<PropertyInt, int> { { PropertyInt.Structure, 50 }, { PropertyInt.MaxStructure, 100 } });

            Assert.IsTrue(TigerEyeArmorTinker.IsTigerEyeSalvage(ItemType.TinkeringMaterial, MaterialType.TigerEye),
                "the material half passes, so fullness is genuinely the deciding gate here");

            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(partial),
                "a half-full bag cannot pay for a whole application");
        }

        // ---------------------------------------------------------------------------------------
        // the Tiger Eye Hammer - a multi-charge fuel source accepted alongside a full bag (2026-08-08)
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The two-armed fuel gate is tested through TigerEyeArmorTinker.IsUsableSource, which is the SAME
        /// method VerifyUseRequirements calls - not a copy of its rule. VerifyUseRequirements itself cannot be
        /// driven here: it needs a live Player (FindObject, SendCraftMessage/SendUseDoneEvent all touch
        /// player.Session), which cannot be constructed in this test project without a live world database
        /// (see SalvageToolTests' own remarks on why SalvageTool.TryConsume is untestable the same way).
        ///
        /// ASSERTING `IsFullBag(x) || HasUsableCharge(x)` HERE INSTEAD WOULD BE WORTHLESS, and that is why the
        /// predicate was extracted. Spelling the disjunction out in the test makes the test pass whether or not
        /// the server combines the two arms the same way - it would agree with itself while the gate regressed
        /// silently. Calling the shared method means reverting either arm in production fails these tests.
        /// </summary>
        [TestMethod]
        public void FuelGate_AcceptsAChargedTool_WhereAPartialBagIsRefused()
        {
            var tenOfTen = MakeItem(ItemType.TinkeringMaterial, EquipMask.None, armorLevel: null, workmanship: null,
                ints: new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.Structure, 10 }, { PropertyInt.MaxStructure, 10 }, { PropertyInt.SalvageToolCharges, 10 },
                });

            // a fresh 10-of-10 tool happens to also read as "full" by IsFullBag's own value rule (Structure
            // >= MaxStructure), since its Structure and MaxStructure are authored equal - but it must be
            // accepted through SalvageTool.HasUsableCharge regardless, which is what the partial-charge case
            // right below actually isolates
            Assert.IsTrue(SalvageTool.HasUsableCharge(tenOfTen), "a fresh 10-charge tool has usable charges");
            Assert.IsTrue(TigerEyeArmorTinker.IsUsableSource(tenOfTen),
                "a fresh 10-charge tool must be accepted");

            var partialBag = MakeItem(ItemType.TinkeringMaterial, EquipMask.None, armorLevel: null, workmanship: null,
                ints: new Dictionary<PropertyInt, int> { { PropertyInt.Structure, 50 }, { PropertyInt.MaxStructure, 100 } });

            Assert.IsFalse(TigerEyeArmorTinker.IsUsableSource(partialBag),
                "a half-full bag must still be refused - the tool allowance must not leak into ordinary bags");
        }

        /// <summary>
        /// THE CORE OF THIS CHANGE. A 7-of-10 tool must never be mistaken for a partial bag: a tool's
        /// Structure is a COUNT of whole applications, so 7 of 10 pays for one perfectly well, while a bag's
        /// Structure is a FRACTION of one unit of salvage, so a bag anywhere short of its MaxStructure cannot.
        /// Both are exercised side by side so the contrast is the assertion, not two isolated facts.
        /// </summary>
        [TestMethod]
        public void FuelGate_PartlySpentToolIsAccepted_ButPartlySpentBagIsRefused()
        {
            var sevenOfTen = MakeItem(ItemType.TinkeringMaterial, EquipMask.None, armorLevel: null, workmanship: null,
                ints: new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.Structure, 7 }, { PropertyInt.MaxStructure, 10 }, { PropertyInt.SalvageToolCharges, 10 },
                });

            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(sevenOfTen), "7 of 10 is not full by the bag rule");
            Assert.IsTrue(SalvageTool.HasUsableCharge(sevenOfTen), "but 7 of 10 charges is perfectly usable");
            Assert.IsTrue(TigerEyeArmorTinker.IsUsableSource(sevenOfTen),
                "the combined gate must accept a 7-of-10 tool");

            var sevenOfTenBag = MakeItem(ItemType.TinkeringMaterial, EquipMask.None, armorLevel: null, workmanship: null,
                ints: new Dictionary<PropertyInt, int> { { PropertyInt.Structure, 7 }, { PropertyInt.MaxStructure, 10 } });

            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(sevenOfTenBag), "7 of 10 salvage is not a whole unit");
            Assert.IsFalse(SalvageTool.HasUsableCharge(sevenOfTenBag), "no SalvageToolCharges property means this is judged as a bag, not a tool");
            Assert.IsFalse(TigerEyeArmorTinker.IsUsableSource(sevenOfTenBag),
                "a 7-of-10 BAG must never be mistaken for an acceptable 7-of-10 TOOL");
        }

        /// <summary>
        /// A spent tool (0 charges) fails both halves of the gate. In play it should not exist - SalvageTool
        /// destroys a tool on its last charge - but the gate must refuse it if it ever is seen, and the
        /// player-facing message for this case is "has no uses remaining", never "is not full" (see
        /// VerifyUseRequirements).
        /// </summary>
        [TestMethod]
        public void FuelGate_RefusesASpentTool()
        {
            var spent = MakeItem(ItemType.TinkeringMaterial, EquipMask.None, armorLevel: null, workmanship: null,
                ints: new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.Structure, 0 }, { PropertyInt.MaxStructure, 10 }, { PropertyInt.SalvageToolCharges, 10 },
                });

            Assert.IsTrue(SalvageTool.IsSalvageTool(spent), "the capacity property is still present on a spent tool");
            Assert.IsFalse(SalvageTool.HasUsableCharge(spent), "but nothing is left to spend");
            Assert.IsFalse(TigerEyeArmorTinker.IsFullBag(spent), "and 0 is nowhere near full by the bag rule either");
            Assert.IsFalse(TigerEyeArmorTinker.IsUsableSource(spent),
                "a spent tool must fail the combined gate");
        }

        // ---------------------------------------------------------------------------------------
        // target eligibility: item class
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void IsEligibleItemClass_AcceptsArmorAtAnySlot()
        {
            foreach (var slot in new[] { EquipMask.ChestArmor, EquipMask.AbdomenArmor, EquipMask.UpperArmArmor, EquipMask.LowerLegArmor, EquipMask.HeadWear })
            {
                Assert.IsTrue(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Armor, slot),
                    $"ItemType.Armor is accepted outright, regardless of slot ({slot})");
            }
        }

        /// <summary>
        /// Clothing is accepted only when it covers ONLY an extremity, which is retail's rule at
        /// RecipeManager_New.cs:277. The three accepted masks are single bits, verified against real
        /// ace_world weenies: capcloth (118) ValidLocations 1 = HeadWear, glovescloth (121) 32 = HandWear,
        /// shoes (132) 256 = FootWear.
        /// </summary>
        [TestMethod]
        public void IsEligibleItemClass_AcceptsClothingAtEachOfTheThreeExtremitySlots()
        {
            Assert.IsTrue(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Clothing, EquipMask.HeadWear));
            Assert.IsTrue(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Clothing, EquipMask.HandWear));
            Assert.IsTrue(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Clothing, EquipMask.FootWear));

            // and through the WorldObject overload, so the two agree
            Assert.IsTrue(TigerEyeArmorTinker.IsEligibleItemClass(MakeItem(ItemType.Clothing, EquipMask.HeadWear)));
            Assert.IsTrue(TigerEyeArmorTinker.IsEligibleItemClass(MakeItem(ItemType.Clothing, EquipMask.HandWear)));
            Assert.IsTrue(TigerEyeArmorTinker.IsEligibleItemClass(MakeItem(ItemType.Clothing, EquipMask.FootWear)));
        }

        /// <summary>
        /// The comparison is EQUALITY, not a flag test, and this is what that buys: clothing covering an
        /// extremity PLUS a non-extremity is refused. Both masks are real ace_world values - boots (2606) is
        /// ValidLocations 384 = FootWear | LowerLegWear, robeaerfalle (8133) is 32512, neither of which is a
        /// single extremity bit.
        /// </summary>
        [TestMethod]
        public void IsEligibleItemClass_RefusesBootsAndRobes()
        {
            var bootsMask = EquipMask.FootWear | EquipMask.LowerLegWear;   // 384, wcid 2606 "boots"

            Assert.AreEqual(384, (int)bootsMask, "the fixture mask must be the real one, or the test proves nothing");

            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Clothing, bootsMask),
                "boots cover an extremity AND a non-extremity, so a flag test would wrongly let them through");

            var robeMask = EquipMask.FootWear | EquipMask.ChestArmor | EquipMask.AbdomenArmor
                | EquipMask.UpperArmArmor | EquipMask.LowerArmArmor | EquipMask.UpperLegArmor | EquipMask.LowerLegArmor;   // 32512, wcid 8133

            Assert.AreEqual(32512, (int)robeMask, "the fixture mask must be the real one, or the test proves nothing");

            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Clothing, robeMask),
                "a robe covers the feet among much else and must not qualify as extremity clothing");

            // a shirt (wcid 130, ValidLocations 30) touches no extremity at all
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Clothing,
                EquipMask.ChestWear | EquipMask.AbdomenWear | EquipMask.UpperArmWear | EquipMask.LowerArmWear));

            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Clothing, EquipMask.None));
        }

        [TestMethod]
        public void IsEligibleItemClass_RefusesJewelryAndEverythingElse()
        {
            // jewelry is neither Armor nor Clothing, whatever slot it occupies
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Jewelry, EquipMask.FingerWearLeft));
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Jewelry, EquipMask.NeckWear));
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Jewelry, EquipMask.WristWearLeft));
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(MakeItem(ItemType.Jewelry, EquipMask.FingerWearLeft)));

            // and neither are the weapon classes, gems or a trinket
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.MeleeWeapon, EquipMask.MeleeWeapon));
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.MissileWeapon, EquipMask.MissileWeapon));
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Caster, EquipMask.Held));
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Gem, EquipMask.None));
            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass(ItemType.Misc, EquipMask.TrinketOne));

            Assert.IsFalse(TigerEyeArmorTinker.IsEligibleItemClass((WorldObject)null));
        }

        /// <summary>
        /// A SHIELD IS ACCEPTED, and that is retail's rule rather than an oversight - but it is the one
        /// outcome here worth flagging, so it is pinned deliberately instead of left implicit.
        ///
        /// The gate this feature mirrors (RecipeManager_New.cs:273) is `target.ItemType == ItemType.Armor`,
        /// and EVERY shield weenie in ace_world is ItemType.Armor: 133 of 133 rows with CombatUse
        /// (PropertyInt 51) = 4 (Shield) carry ItemType 2, queried 2026-08-08. So a shield passes the item
        /// class gate, the armor-level gate and the workmanship gate, exactly as it does for a real steel
        /// salvage application.
        ///
        /// Note this is NOT the equipment-mod system's rule, which excludes shields explicitly
        /// (EquipmentModManager.IsEligibleSlot) because lootgen never rolls gear ratings on them. Ratings and
        /// armor level are different things, and the two systems are gated differently on purpose.
        /// </summary>
        [TestMethod]
        public void IsEligibleItemClass_AcceptsAShield_BecauseEveryShieldWeenieIsItemTypeArmor()
        {
            var shield = MakeItem(ItemType.Armor, EquipMask.Shield, armorLevel: 200,
                ints: new Dictionary<PropertyInt, int> { { PropertyInt.CombatUse, (int)CombatUse.Shield } });

            Assert.IsTrue(shield.IsShield, "the fixture must actually be a shield, or the assertion below is vacuous");

            Assert.IsTrue(TigerEyeArmorTinker.IsEligibleItemClass(shield),
                "retail's steel gate is ItemType.Armor, and shields are ItemType.Armor");
        }

        // ---------------------------------------------------------------------------------------
        // target eligibility: workmanship, armor level, enchantability, cleanliness
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void HasTinkerableArmor_NeedsBothAWorkmanshipAndAnArmorLevel()
        {
            Assert.IsTrue(TigerEyeArmorTinker.HasTinkerableArmor(MakeArmor()));

            Assert.IsFalse(TigerEyeArmorTinker.HasTinkerableArmor(MakeItem(ItemType.Armor, EquipMask.ChestArmor, workmanship: null)),
                "no workmanship means it is not a loot-generated item, which is what a tinker needs");

            Assert.IsFalse(TigerEyeArmorTinker.HasTinkerableArmor(MakeItem(ItemType.Armor, EquipMask.ChestArmor, armorLevel: null)),
                "no armor level means a steel tinker has nothing to raise");

            Assert.IsFalse(TigerEyeArmorTinker.HasTinkerableArmor(MakeItem(ItemType.Armor, EquipMask.ChestArmor, armorLevel: 0)),
                "an armor level of 0 is the same as none - HasArmorLevel is > 0");

            Assert.IsFalse(TigerEyeArmorTinker.HasTinkerableArmor(null));
        }

        [TestMethod]
        public void IsEnchantable_RefusesAnItemThatResistsMagicOutright()
        {
            // WorldObject.IsEnchantable is (ResistMagic ?? 0) < 9999, the client's own formula
            var normal = MakeArmor();
            Assert.IsTrue(normal.IsEnchantable);

            var warded = MakeItem(ItemType.Armor, EquipMask.ChestArmor,
                ints: new Dictionary<PropertyInt, int> { { PropertyInt.ResistMagic, 9999 } });

            Assert.IsFalse(warded.IsEnchantable,
                "retail's steel gate refuses an unenchantable item (RecipeManager_New.cs:283)");
        }

        [TestMethod]
        public void IsCleanArmor_AcceptsAnUntouchedItem()
        {
            Assert.IsTrue(TigerEyeArmorTinker.IsCleanArmor(MakeArmor()),
                "a fresh item has no tinkers, no imbue and no tinker log");

            Assert.IsFalse(TigerEyeArmorTinker.IsCleanArmor(null));
        }

        [TestMethod]
        public void IsCleanArmor_RejectsAnAlreadyTinkeredItem()
        {
            var armor = MakeArmor();
            armor.NumTimesTinkered = 1;

            Assert.IsFalse(TigerEyeArmorTinker.IsCleanArmor(armor),
                "NumTimesTinkered > 0 means the item has spent tinker slots already");
        }

        [TestMethod]
        public void IsCleanArmor_RejectsAnImbuedItem()
        {
            var armor = MakeArmor();
            armor.ImbuedEffect = ImbuedEffectType.MeleeDefense;

            Assert.IsFalse(TigerEyeArmorTinker.IsCleanArmor(armor),
                "an existing imbue means the item has been worked on already");
        }

        [TestMethod]
        public void IsCleanArmor_RejectsATinkerLoggedItem()
        {
            var armor = MakeArmor();
            armor.TinkerLog = ((uint)MaterialType.Steel).ToString();

            Assert.IsFalse(TigerEyeArmorTinker.IsCleanArmor(armor),
                "a tinker log records past work even when the counters were never written");
        }

        [TestMethod]
        public void IsCleanArmor_RejectsAnItemThisFeatureAlreadyProcessed()
        {
            // the guard must be self-consistent: whatever ApplyToArmor leaves behind is not clean
            var armor = MakeArmor();
            TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: false);

            Assert.IsFalse(TigerEyeArmorTinker.IsCleanArmor(armor),
                "the bag must not be applicable twice to the same item");
        }

        // ---------------------------------------------------------------------------------------
        // application
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void ApplyToArmor_RollsBetweenMinAndMaxTinkers()
        {
            var seen = new HashSet<int>();

            for (var i = 0; i < RollSamples; i++)
            {
                var result = TigerEyeArmorTinker.ApplyToArmor(MakeArmor(), mintMod: false);

                Assert.IsTrue(result.NumTinkers >= TigerEyeArmorTinker.MinTinkers && result.NumTinkers <= TigerEyeArmorTinker.MaxTinkers,
                    $"rolled {result.NumTinkers} tinkers, outside the inclusive range [{TigerEyeArmorTinker.MinTinkers}, {TigerEyeArmorTinker.MaxTinkers}]");

                seen.Add(result.NumTinkers);
            }

            var expectedCount = TigerEyeArmorTinker.MaxTinkers - TigerEyeArmorTinker.MinTinkers + 1;

            // ThreadSafeRandom.Next's max is INCLUSIVE in this project, so BOTH endpoints must be reachable -
            // an exclusive max would silently make MaxTinkers unreachable and this is what catches that
            Assert.AreEqual(expectedCount, seen.Count,
                $"over {RollSamples} rolls every value in [{TigerEyeArmorTinker.MinTinkers}, {TigerEyeArmorTinker.MaxTinkers}] "
                + $"({expectedCount} distinct values) should appear; saw {seen.Count}: {string.Join(",", seen.OrderBy(n => n))}");

            Assert.IsTrue(seen.Contains(TigerEyeArmorTinker.MinTinkers), "the minimum must be reachable");
            Assert.IsTrue(seen.Contains(TigerEyeArmorTinker.MaxTinkers), "the maximum must be reachable");
        }

        [TestMethod]
        public void ApplyToArmor_RaisesArmorLevelByTwentyPerTinker()
        {
            const int baseArmorLevel = 100;

            var seen = new HashSet<int>();

            for (var i = 0; i < RollSamples; i++)
            {
                var armor = MakeArmor(baseArmorLevel);
                var result = TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: false);

                var expected = baseArmorLevel + result.NumTinkers * TigerEyeArmorTinker.SteelArmorLevelPerTinker;

                Assert.AreEqual(expected, armor.ArmorLevel,
                    $"base {baseArmorLevel} + {result.NumTinkers} tinkers x {TigerEyeArmorTinker.SteelArmorLevelPerTinker} = {expected}");

                Assert.AreEqual(result.NumTinkers * TigerEyeArmorTinker.SteelArmorLevelPerTinker, result.ArmorLevelGained,
                    "the reported gain and the applied gain must be the same number");

                seen.Add(result.NumTinkers);
            }

            Assert.IsTrue(seen.Count > 1,
                $"the arithmetic assertion is only meaningful if the sample varied the tinker count; saw only {string.Join(",", seen)}");
        }

        [TestMethod]
        public void ApplyToArmor_LocksTinkeringRegardlessOfTinkerCount()
        {
            var seen = new HashSet<int>();

            for (var i = 0; i < RollSamples; i++)
            {
                var armor = MakeArmor();
                var result = TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: false);

                seen.Add(result.NumTinkers);

                Assert.AreEqual(TigerEyeArmorTinker.LockedTinkerCount, armor.NumTimesTinkered,
                    $"{result.NumTinkers} tinkers landed, but NumTimesTinkered must always be the lock value "
                    + $"{TigerEyeArmorTinker.LockedTinkerCount}, not the tinker count");
            }

            Assert.IsTrue(seen.Count > 1,
                "the lock assertion is only meaningful if the sample actually varied the tinker count; "
                + $"saw only {string.Join(",", seen)}");
        }

        [TestMethod]
        public void ApplyToArmor_TinkerLogRecordsExactlyOneSteelEntryPerTinker()
        {
            var steel = ((uint)MaterialType.Steel).ToString();

            for (var i = 0; i < RollSamples; i++)
            {
                var armor = MakeArmor();
                var result = TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: false);

                var entries = armor.TinkerLog.Split(',');

                Assert.AreEqual(result.NumTinkers, entries.Length,
                    $"{result.NumTinkers} tinkers = {result.NumTinkers} tinker log entries");

                foreach (var entry in entries)
                    Assert.AreEqual(steel, entry, "every entry must be the steel material id");

                CollectionAssert.AreEqual(
                    result.Materials.Select(m => ((uint)m).ToString()).ToList(),
                    entries.ToList(),
                    "the reported material list and the written tinker log must be the same sequence");
            }
        }

        [TestMethod]
        public void ApplyToArmor_AppendsToAnExistingTinkerLogRatherThanReplacingIt()
        {
            // the clean-item guard means this cannot happen through normal use, but the log writer is shared
            // shaped with RecipeManager.HandleTinkerLog and must not clobber prior entries
            var armor = MakeArmor();
            var priorEntry = ((uint)MaterialType.Iron).ToString();
            armor.TinkerLog = priorEntry;

            var result = TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: false);

            var entries = armor.TinkerLog.Split(',');

            Assert.AreEqual(priorEntry, entries[0], "the pre-existing tinker log entry must survive");

            Assert.AreEqual(1 + result.NumTinkers, entries.Length,
                $"1 prior + {result.NumTinkers} steel = {1 + result.NumTinkers} entries");
        }

        [TestMethod]
        public void ApplyToArmor_ReportsEveryMaterialItWrote()
        {
            for (var i = 0; i < 200; i++)
            {
                var result = TigerEyeArmorTinker.ApplyToArmor(MakeArmor(), mintMod: false);

                Assert.AreEqual(result.NumTinkers, result.Materials.Count);
                Assert.IsTrue(result.Materials.All(m => m == MaterialType.Steel));
            }
        }

        /// <summary>
        /// The application touches ONLY the three things it claims to. In particular it must not write an
        /// ImbuedEffect - a steel armor tinker is an armor-level increment, not an imbue, and stamping one
        /// would put a rending icon underlay on a piece of armor.
        /// </summary>
        [TestMethod]
        public void ApplyToArmor_DoesNotImbueOrTouchWeaponProperties()
        {
            var armor = MakeArmor();

            TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: false);

            Assert.AreEqual(ImbuedEffectType.Undef, armor.ImbuedEffect, "a steel armor tinker is not an imbue");
            Assert.IsNull(armor.IconUnderlayId, "no imbue means no icon underlay");
            Assert.IsNull(armor.Damage, "an armor tinker must not write a weapon damage property");
            Assert.IsNull(armor.DamageMod);
            Assert.IsNull(armor.ElementalDamageMod);
        }

        // ---------------------------------------------------------------------------------------
        // MatchesAppliedSignature - the terminal seal Obsidian reads (repo-owner ruling, 2026-08-08)
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// An item actually driven through ApplyToArmor matches its own signature, across the whole
        /// MinTinkers..MaxTinkers roll range - driven through the real apply path rather than hand-stamped
        /// properties, so this breaks if ApplyToArmor's shape ever changes.
        /// </summary>
        [TestMethod]
        public void MatchesAppliedSignature_AcceptsAnItemActuallyAppliedTo()
        {
            var seen = new HashSet<int>();

            for (var i = 0; i < RollSamples; i++)
            {
                var armor = MakeArmor();
                var result = TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: false);

                seen.Add(result.NumTinkers);

                Assert.IsTrue(TigerEyeArmorTinker.MatchesAppliedSignature(armor),
                    $"an item just processed by ApplyToArmor ({result.NumTinkers} tinkers) must match its own signature");
            }

            var expectedCount = TigerEyeArmorTinker.MaxTinkers - TigerEyeArmorTinker.MinTinkers + 1;

            Assert.AreEqual(expectedCount, seen.Count,
                $"the sample must cover every tinker count in [{TigerEyeArmorTinker.MinTinkers}, {TigerEyeArmorTinker.MaxTinkers}] "
                + "or this test has not actually exercised the whole signature range");
        }

        [TestMethod]
        public void MatchesAppliedSignature_RejectsACleanUntouchedItem()
        {
            Assert.IsFalse(TigerEyeArmorTinker.MatchesAppliedSignature(MakeArmor()),
                "a fresh item has NumTimesTinkered 0 and no tinker log at all");
        }

        [TestMethod]
        public void MatchesAppliedSignature_RejectsTheRightLockWithANonSteelEntry()
        {
            var armor = MakeArmor();
            armor.NumTimesTinkered = TigerEyeArmorTinker.LockedTinkerCount;
            armor.TinkerLog = ((uint)MaterialType.Iron).ToString();

            Assert.IsFalse(TigerEyeArmorTinker.MatchesAppliedSignature(armor),
                "the lock value alone is not enough - every log entry must be steel");
        }

        [TestMethod]
        public void MatchesAppliedSignature_RejectsASteelLogWithTheWrongNumTimesTinkered()
        {
            var armor = MakeArmor();
            armor.NumTimesTinkered = 3;   // NOT the lock value
            armor.TinkerLog = ((uint)MaterialType.Steel).ToString();

            Assert.IsFalse(TigerEyeArmorTinker.MatchesAppliedSignature(armor),
                "a steel log is not enough on its own - NumTimesTinkered must equal LockedTinkerCount");
        }

        [TestMethod]
        public void MatchesAppliedSignature_RejectsAMalformedLogRatherThanThrowing()
        {
            var armor = MakeArmor();
            armor.NumTimesTinkered = TigerEyeArmorTinker.LockedTinkerCount;
            armor.TinkerLog = "not,a,number";

            Assert.IsFalse(TigerEyeArmorTinker.MatchesAppliedSignature(armor),
                "a non-numeric log entry must fail the parse cleanly rather than throw");
        }

        /// <summary>
        /// Confirms the unforgeability argument in MatchesAppliedSignature's doc comment with the real
        /// constant values: 10 hand-applied steel tinkers is the only way to reach NumTimesTinkered ==
        /// LockedTinkerCount (10) by hand, and 10 log entries is already outside [MinTinkers, MaxTinkers]
        /// = [1, 5], so the hand-tinkered item this represents must be rejected.
        /// </summary>
        [TestMethod]
        public void MatchesAppliedSignature_RejectsTenHandAppliedSteelTinkers()
        {
            var armor = MakeArmor();
            armor.NumTimesTinkered = TigerEyeArmorTinker.LockedTinkerCount;
            armor.TinkerLog = string.Join(",", Enumerable.Repeat(((uint)MaterialType.Steel).ToString(), TigerEyeArmorTinker.LockedTinkerCount));

            Assert.IsFalse(TigerEyeArmorTinker.MatchesAppliedSignature(armor),
                "10 hand-applied steel tinkers reach the lock value honestly, but the entry count (10) is "
                + $"above MaxTinkers ({TigerEyeArmorTinker.MaxTinkers}), so this must still be rejected");
        }

        // ---------------------------------------------------------------------------------------
        // the additive equipment-mod grant (repo-owner ruling, 2026-08-08)
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// mintMod: true on an ordinary, eligible piece of armor mints exactly one mod and stamps a
        /// GearModCapacity of 1, WITHOUT changing anything about the steel tinkers, the armor gain, the
        /// TinkerLog or the permanent lock - the mod is purely additive.
        /// </summary>
        [TestMethod]
        public void ApplyToArmor_MintModTrue_MintsExactlyOneModAndSetsCapacityOne_AndStillAppliesSteelTinkers()
        {
            const int baseArmorLevel = 100;

            var armor = MakeArmor(baseArmorLevel);
            var result = TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: true);

            // the mod grant did not touch any of the steel-tinker outcomes
            Assert.IsTrue(result.NumTinkers >= TigerEyeArmorTinker.MinTinkers && result.NumTinkers <= TigerEyeArmorTinker.MaxTinkers);
            Assert.AreEqual(baseArmorLevel + result.ArmorLevelGained, armor.ArmorLevel);
            Assert.AreEqual(result.NumTinkers, result.Materials.Count);
            Assert.AreEqual(TigerEyeArmorTinker.LockedTinkerCount, armor.NumTimesTinkered);
            Assert.IsTrue(TigerEyeArmorTinker.MatchesAppliedSignature(armor), "the seal signature must still match");

            // exactly one mod was minted and reported
            Assert.AreEqual(1, EquipmentModManager.GetModCount(armor), "exactly one mod property must be written");
            Assert.AreEqual(1, EquipmentModManager.GetModCapacity(armor), "GearModCapacity must be stamped to 1");
            Assert.AreEqual(1, result.ModsApplied.Count, "one report line for the minted mod");
        }

        /// <summary>
        /// mintMod: false is the control - tinkers and the lock still happen, but nothing about the
        /// equipment-mod system is ever touched.
        /// </summary>
        [TestMethod]
        public void ApplyToArmor_MintModFalse_NeverMintsAModOrCapacity()
        {
            var armor = MakeArmor();
            var result = TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: false);

            Assert.AreEqual(TigerEyeArmorTinker.LockedTinkerCount, armor.NumTimesTinkered);
            Assert.IsTrue(result.NumTinkers >= TigerEyeArmorTinker.MinTinkers && result.NumTinkers <= TigerEyeArmorTinker.MaxTinkers);

            Assert.AreEqual(0, EquipmentModManager.GetModCount(armor), "mintMod false must never write a mod");
            Assert.AreEqual(0, EquipmentModManager.GetModCapacity(armor), "mintMod false must never stamp GearModCapacity");
            Assert.AreEqual(0, result.ModsApplied.Count);
        }

        /// <summary>
        /// THE MOST IMPORTANT NEW TEST (per the spec). A shield passes IsEligibleItemClass (every shield
        /// weenie is ItemType.Armor) and gets its steel tinkers exactly like any other armor - but it must
        /// NEVER become a mod carrier, matching Obsidian's own IsEligibleSlot(isShield: true) => false rule.
        /// This is the parity guard EquipmentModManager.IsEligibleTarget exists to enforce, and it is the one
        /// case where a target eligible for tinkers is NOT eligible for the mod grant.
        /// </summary>
        [TestMethod]
        public void ApplyToArmor_MintModTrueOnShield_AppliesTinkersButNoMod()
        {
            var shield = MakeItem(ItemType.Armor, EquipMask.Shield, armorLevel: 200,
                ints: new Dictionary<PropertyInt, int> { { PropertyInt.CombatUse, (int)CombatUse.Shield } });

            Assert.IsTrue(shield.IsShield, "the fixture must actually be a shield, or the assertion below is vacuous");
            Assert.IsFalse(EquipmentModManager.IsEligibleTarget(shield), "sanity: a shield is not an eligible mod target");

            var result = TigerEyeArmorTinker.ApplyToArmor(shield, mintMod: true);

            // steel tinkers still land in full
            Assert.IsTrue(result.NumTinkers >= TigerEyeArmorTinker.MinTinkers && result.NumTinkers <= TigerEyeArmorTinker.MaxTinkers);
            Assert.AreEqual(200 + result.ArmorLevelGained, shield.ArmorLevel);
            Assert.AreEqual(TigerEyeArmorTinker.LockedTinkerCount, shield.NumTimesTinkered);
            Assert.IsTrue(TigerEyeArmorTinker.MatchesAppliedSignature(shield));

            // but no mod, despite mintMod: true
            Assert.AreEqual(0, EquipmentModManager.GetModCount(shield), "a shield must never become a mod carrier");
            Assert.AreEqual(0, EquipmentModManager.GetModCapacity(shield), "a shield must never get a stamped capacity");
            Assert.AreEqual(0, result.ModsApplied.Count, "no report line, since nothing was minted");
        }

        /// <summary>
        /// The minted potency is a REAL roll (EquipmentModRoller.RollPotency), not the old removed
        /// equipment_mod_lowtier_potency constant. Sampled over many independent applications, the stored
        /// fraction must vary and must stay within [the mod's own MinPotency floor, 1.0] scaled by the
        /// item's workmanship/10 - MakeArmor's default workmanship of 10 makes that scale factor 1.0, so the
        /// stored fraction and the raw potency coincide here.
        /// </summary>
        [TestMethod]
        public void ApplyToArmor_MintedPotency_IsARealRollWithinTheMinPotencyToOneBand()
        {
            var seenFractions = new HashSet<double>();

            for (var i = 0; i < RollSamples; i++)
            {
                var armor = MakeArmor();
                TigerEyeArmorTinker.ApplyToArmor(armor, mintMod: true);

                var mods = EquipmentModDisplay.GetMods(armor);
                Assert.AreEqual(1, mods.Count);

                var (definition, potency) = mods[0];
                var floor = EquipmentModRoller.MinPotency(definition);

                Assert.IsTrue(potency >= floor - 1e-9 && potency <= 1.0 + 1e-9,
                    $"potency {potency} outside [{floor}, 1.0] for {definition.Id}");

                seenFractions.Add(Math.Round(potency, 3));
            }

            Assert.IsTrue(seenFractions.Count > 10,
                $"a real uniform roll over {RollSamples} samples must produce more than a handful of distinct "
                + $"values; saw {seenFractions.Count}, which would mean a fixed/deterministic roll survived");
        }
    }
}
