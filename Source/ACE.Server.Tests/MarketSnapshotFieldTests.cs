using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The four snapshot data defects the web app cannot render around: a raw accumulated
    /// workmanship where the 1 to 10 value belongs, a missing Surge (proc spell), a missing primary
    /// spell, and the damage fields a DMG column needs - including a caster's element, which reached
    /// the web through no channel at all before.
    ///
    /// Nothing here may depend on the dats being present: spell NAMES resolve through
    /// MarketSnapshot.SpellName, which falls back to "Spell &lt;id&gt;" with no dats loaded, and other
    /// test classes in this assembly initialize DatManager when the dats happen to be installed.
    /// </summary>
    [TestClass]
    public class MarketSnapshotFieldTests
    {
        private static int nextGuid = 0x7F500000;

        private IClothingIconSource savedClothingIcons;

        [TestInitialize]
        public void Setup()
        {
            savedClothingIcons = MarketSnapshot.ClothingIcons;
            MarketSnapshot.ClothingIcons = null;
        }

        [TestCleanup]
        public void Teardown() => MarketSnapshot.ClothingIcons = savedClothingIcons;

        // ---- fixtures ----

        /// <summary>
        /// A salvage bag, the shape that produced the report: ItemWorkmanship is the SUM over every
        /// unit in the bag, so the stored number is 117 where the player must be shown 1.17.
        /// </summary>
        private static Weenie SalvageBag(int itemWorkmanship, int? numItemsInMaterial)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                { PropertyInt.MaterialType, (int)MaterialType.Steel },
                { PropertyInt.ItemWorkmanship, itemWorkmanship },
                { PropertyInt.EncumbranceVal, 500 },
                { PropertyInt.Value, 1000 },
            };

            if (numItemsInMaterial.HasValue)
                ints[PropertyInt.NumItemsInMaterial] = numItemsInMaterial.Value;

            return new Weenie
            {
                WeenieClassId = 20983,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Salvage (100)" },
                },
                PropertiesInt = ints,
            };
        }

        /// <summary>A cloak with a Surge: ProcSpell set, no ProcSpellRate, and a spellbook beside it.</summary>
        private static Weenie SurgingCloak()
        {
            return new Weenie
            {
                WeenieClassId = 1001800,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Cloak of Testing" },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.EncumbranceVal, 50 },
                    { PropertyInt.Value, 4000 },
                    { PropertyInt.UiEffects, (int)UiEffects.Magical },
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.ProcSpell, 5204 },
                },
                PropertiesSpellBook = new Dictionary<int, float>
                {
                    { 4325, 2f },
                },
            };
        }

        /// <summary>
        /// A caster. Its element is the case with no other channel: MarketAppraisal.AddWeapon returns
        /// early for WeenieType.Caster and "Damage Type" is emitted only inside AddWeapon.
        /// </summary>
        private static Weenie ElementalWand()
        {
            return new Weenie
            {
                WeenieClassId = 1001850,
                WeenieType = WeenieType.Caster,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Wand of Testing" },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                    { PropertyInt.DamageType, (int)DamageType.Fire },
                    { PropertyInt.EncumbranceVal, 50 },
                    { PropertyInt.Value, 8000 },
                    { PropertyInt.ElementalDamageBonus, 5 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.ElementalDamageMod, 1.08 },
                    { PropertyFloat.DamageMod, 2.63 },
                },
                PropertiesDID = new Dictionary<PropertyDataId, uint>
                {
                    { PropertyDataId.Spell, 2137 },
                },
                // The primary spell sits SECOND in the spellbook on purpose: projecting the book as-is
                // would put 4325 first, so the assertion on ordering cannot pass by coincidence.
                PropertiesSpellBook = new Dictionary<int, float>
                {
                    { 4325, 2f },
                    { 2137, 2f },
                },
            };
        }

        /// <summary>A real melee weapon carrying a modern WeaponSkill, for the WeaponClass mirror test.</summary>
        private static Weenie HeavyWeaponsSword()
        {
            return new Weenie
            {
                WeenieClassId = 1001900,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Sword of Testing" },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.WeaponSkill, (int)Skill.HeavyWeapons },
                    { PropertyInt.EncumbranceVal, 100 },
                    { PropertyInt.Value, 500 },
                },
            };
        }

        /// <summary>A real missile launcher carrying an AmmoType, for the WeaponClass mirror test.</summary>
        private static Weenie CrossbowLauncher()
        {
            return new Weenie
            {
                WeenieClassId = 1001901,
                WeenieType = WeenieType.MissileLauncher,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Crossbow of Testing" },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MissileWeapon },
                    { PropertyInt.AmmoType, (int)AmmoType.Bolt },
                    { PropertyInt.EncumbranceVal, 100 },
                    { PropertyInt.Value, 500 },
                },
            };
        }

        /// <summary>A chest piece carrying both ValidLocations and an EquipmentSetId, for the slot/set mirror test.</summary>
        private static Weenie ChestArmorInADefendersSet()
        {
            return new Weenie
            {
                WeenieClassId = 1001902,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Breastplate of Testing" },
                },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ValidLocations, (int)(EquipMask.ChestArmor | EquipMask.AbdomenArmor) },
                    { PropertyInt.EquipmentSetId, (int)EquipmentSet.Defenders },
                    { PropertyInt.EncumbranceVal, 100 },
                    { PropertyInt.Value, 500 },
                },
            };
        }

        private static WorldObject AsGeneric(Weenie weenie)
            => new GenericObject(weenie, new ObjectGuid((uint)nextGuid++));

        private static WorldObject AsClothing(Weenie weenie)
            => new Clothing(weenie, new ObjectGuid((uint)nextGuid++));

        private static WorldObject AsCaster(Weenie weenie)
            => new Caster(weenie, new ObjectGuid((uint)nextGuid++));

        private static WorldObject AsMeleeWeapon(Weenie weenie)
            => new MeleeWeapon(weenie, new ObjectGuid((uint)nextGuid++));

        private static WorldObject AsMissileLauncher(Weenie weenie)
            => new MissileLauncher(weenie, new ObjectGuid((uint)nextGuid++));

        private static void AssertHas(IReadOnlyList<string> panel, string expected)
            => Assert.IsTrue(panel.Contains(expected),
                $"expected the panel line \"{expected}\"; the panel was:\n  {string.Join("\n  ", panel)}");

        // ---- defect 1: workmanship ----

        [TestMethod]
        public void SalvageBag_WorkmanshipIsThePerUnitValue_NotTheAccumulatedTotal()
        {
            // The player's report: a bag showed 117.
            var weenie = SalvageBag(117, 100);

            Assert.AreEqual(1.17, MarketSnapshot.FromWeenie(weenie).Workmanship.Value, 0.0001);
            Assert.AreEqual(1.17, MarketSnapshot.FromItem(AsGeneric(weenie)).Workmanship.Value, 0.0001);
        }

        [TestMethod]
        public void SalvageBag_PanelLineCarriesThePerUnitValueToo()
        {
            AssertHas(MarketSnapshot.FromWeenie(SalvageBag(117, 100)).PanelLines, "Workmanship: 1.17");
        }

        [TestMethod]
        public void SalvageBag_NumItemsInMaterialTravelsWithTheSnapshot()
        {
            Assert.AreEqual(100, MarketSnapshot.FromWeenie(SalvageBag(117, 100)).NumItemsInMaterial);
            Assert.IsNull(MarketSnapshot.FromWeenie(SalvageBag(7, null)).NumItemsInMaterial);
        }

        [TestMethod]
        public void SalvageBag_StructureFieldsAreActuallyProjected()
        {
            // A fixture with non-null Structure/MaxStructure/SalvageToolCharges, so this fails if
            // either projection leaves the three fields null instead of reading them off the item.
            var weenie = SalvageBag(117, 100);
            weenie.PropertiesInt[PropertyInt.Structure] = 100;
            weenie.PropertiesInt[PropertyInt.MaxStructure] = 100;
            weenie.PropertiesInt[PropertyInt.SalvageToolCharges] = 5;

            var fromWeenie = MarketSnapshot.FromWeenie(weenie);
            Assert.AreEqual(100, fromWeenie.Structure);
            Assert.AreEqual(100, fromWeenie.MaxStructure);
            Assert.AreEqual(5, fromWeenie.SalvageToolCharges);

            var fromItem = MarketSnapshot.FromItem(AsGeneric(weenie));
            Assert.AreEqual(100, fromItem.Structure);
            Assert.AreEqual(100, fromItem.MaxStructure);
            Assert.AreEqual(5, fromItem.SalvageToolCharges);
        }

        [TestMethod]
        public void Workmanship_WithNoNumItemsInMaterial_IsTheStoredValueUnchanged()
        {
            // Nothing to divide by, and 7 is already inside [1, 10], so no recovery branch runs.
            Assert.AreEqual(7.0, MarketSnapshot.FromWeenie(SalvageBag(7, null)).Workmanship.Value, 0.0001);
            Assert.AreEqual(7.0, MarketSnapshot.FromItem(AsGeneric(SalvageBag(7, null))).Workmanship.Value, 0.0001);
        }

        [TestMethod]
        public void Workmanship_AbsentEntirely_ProjectsNullAndEmitsNoPanelLine()
        {
            var weenie = SalvageBag(5, null);
            weenie.PropertiesInt.Remove(PropertyInt.ItemWorkmanship);

            var snapshot = MarketSnapshot.FromWeenie(weenie);

            Assert.IsNull(snapshot.Workmanship);
            Assert.IsFalse(snapshot.PanelLines.Any(l => l.StartsWith("Workmanship")),
                "a workmanship line appeared for an item that has none:\n  " + string.Join("\n  ", snapshot.PanelLines));
        }

        [TestMethod]
        public void Workmanship_OutOfRange_IsRecoveredAndClamped_WithoutWritingToTheItem()
        {
            // 117 with nothing to divide by is outside [1, 10], which is the branch of
            // WorldObject.Workmanship that REWRITES ItemWorkmanship in place. This projection runs for
            // every row of a vault view, so it must recompute locally and leave the item alone.
            var item = AsGeneric(SalvageBag(117, null));

            var snapshot = MarketSnapshot.FromItem(item);

            Assert.AreEqual(1.0, snapshot.Workmanship.Value, 0.0001, "the recovered value must be clamped into [1, 10]");
            Assert.AreEqual(117, item.GetProperty(PropertyInt.ItemWorkmanship),
                "projecting a snapshot rewrote the item's stored ItemWorkmanship");
        }

        [TestMethod]
        public void Workmanship_ZeroOverZero_ResolvesRatherThanPoisoningTheJson()
        {
            // 0 / 0 is NaN, and a NaN double makes JsonSerializer throw, which would lose the whole
            // snapshot rather than one field.
            var weenie = SalvageBag(0, 0);
            weenie.PropertiesInt[PropertyInt.Structure] = 0;

            var snapshot = MarketSnapshot.FromWeenie(weenie);

            Assert.IsTrue(snapshot.Workmanship.HasValue && !double.IsNaN(snapshot.Workmanship.Value));
            Assert.IsNotNull(MarketSnapshot.Serialize(snapshot));
        }

        // ---- defect 2: the Surge and the primary spell ----

        [TestMethod]
        public void ProcSpell_ProjectsIntoTheSpellListInTheGamesPosition()
        {
            var fromWeenie = MarketSnapshot.FromWeenie(SurgingCloak());
            var fromItem = MarketSnapshot.FromItem(AsClothing(SurgingCloak()));

            foreach (var snapshot in new[] { fromWeenie, fromItem })
            {
                CollectionAssert.AreEqual(new List<int> { 5204, 4325 }, snapshot.SpellIds,
                    "the proc spell must lead the spellbook, as AppraiseInfo.BuildSpells orders it");

                Assert.AreEqual(5204, snapshot.ProcSpellId);
                Assert.AreEqual(snapshot.SpellNames[0], snapshot.ProcSpellName);
            }
        }

        [TestMethod]
        public void ProcSpell_EmitsASurgePanelLine()
        {
            var snapshot = MarketSnapshot.FromWeenie(SurgingCloak());

            var surge = snapshot.PanelLines.SingleOrDefault(l => l.StartsWith("Surge: "));

            Assert.IsNotNull(surge, "no Surge line:\n  " + string.Join("\n  ", snapshot.PanelLines));
            Assert.AreEqual($"Surge: {snapshot.ProcSpellName}", surge,
                "aetheria never sets ProcSpellRate, so no rate suffix may be rendered when it is absent");
        }

        [TestMethod]
        public void ProcSpellRate_WhenSet_BecomesTheSuffix()
        {
            var weenie = SurgingCloak();
            weenie.PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.ProcSpellRate, 0.05 } };

            var snapshot = MarketSnapshot.FromWeenie(weenie);

            AssertHas(snapshot.PanelLines, $"Surge: {snapshot.ProcSpellName} +5%");
        }

        [TestMethod]
        public void NoProcSpell_EmitsNeitherTheFieldNorTheLine()
        {
            var weenie = SurgingCloak();
            weenie.PropertiesDID.Remove(PropertyDataId.ProcSpell);

            var snapshot = MarketSnapshot.FromWeenie(weenie);

            Assert.IsNull(snapshot.ProcSpellId);
            Assert.IsNull(snapshot.ProcSpellName);
            Assert.IsFalse(snapshot.PanelLines.Any(l => l.StartsWith("Surge")),
                "a Surge line appeared for an item with no proc spell:\n  " + string.Join("\n  ", snapshot.PanelLines));
            CollectionAssert.AreEqual(new List<int> { 4325 }, snapshot.SpellIds);
        }

        [TestMethod]
        public void PrimarySpell_LeadsTheListAndIsNotDuplicatedByTheSpellbook()
        {
            // The wand's SpellDID 2137 is ALSO in its spellbook, exactly as a real caster carries it.
            var fromWeenie = MarketSnapshot.FromWeenie(ElementalWand());
            var fromItem = MarketSnapshot.FromItem(AsCaster(ElementalWand()));

            foreach (var snapshot in new[] { fromWeenie, fromItem })
            {
                CollectionAssert.AreEqual(new List<int> { 2137, 4325 }, snapshot.SpellIds,
                    "the primary spell must appear exactly once, first");

                Assert.AreEqual(2, snapshot.SpellNames.Count);
            }
        }

        // ---- defects 3 and 4: the fields the web renders a DMG column from ----

        [TestMethod]
        public void Caster_ProjectsANonNullDamageType_TheOnlyChannelItHas()
        {
            var fromWeenie = MarketSnapshot.FromWeenie(ElementalWand());
            var fromItem = MarketSnapshot.FromItem(AsCaster(ElementalWand()));

            foreach (var snapshot in new[] { fromWeenie, fromItem })
            {
                Assert.AreEqual((int)DamageType.Fire, snapshot.DamageType);
                Assert.AreEqual(1.08, snapshot.ElementalDamageMod.Value, 0.0001);
                Assert.AreEqual(2.63, snapshot.DamageMod.Value, 0.0001);
                Assert.AreEqual(5, snapshot.ElementalDamageBonus);
            }

            // The control: the panel lines really do withhold a caster's damage type, which is why the
            // numeric field above is not redundant.
            Assert.IsFalse(fromWeenie.PanelLines.Any(l => l.StartsWith("Damage Type")),
                "a caster's panel now carries a Damage Type line; this test's premise no longer holds:\n  "
                + string.Join("\n  ", fromWeenie.PanelLines));
        }

        [TestMethod]
        public void UiEffects_TravelsWithTheSnapshot()
        {
            Assert.AreEqual((int)UiEffects.Magical, MarketSnapshot.FromWeenie(SurgingCloak()).UiEffects);
            Assert.AreEqual((int)UiEffects.Magical, MarketSnapshot.FromItem(AsClothing(SurgingCloak())).UiEffects);
            Assert.IsNull(MarketSnapshot.FromWeenie(SalvageBag(7, null)).UiEffects);
        }

        // ---- the two projections stay mirrored ----

        [TestMethod]
        public void FromItem_AndFromWeenie_MirrorEveryFieldOfTheSnapshot()
        {
            // MarketSnapshot.FromWeenie's own doc comment requires this, and a new field added to one
            // overload and not the other is exactly the failure it warns about.
            AssertMirrored(SalvageBag(117, 100), AsGeneric);
            AssertMirrored(SurgingCloak(), AsClothing);
            AssertMirrored(ElementalWand(), AsCaster);
            AssertMirrored(HeavyWeaponsSword(), AsMeleeWeapon);
            AssertMirrored(CrossbowLauncher(), AsMissileLauncher);
            AssertMirrored(ChestArmorInADefendersSet(), AsClothing);
        }

        [TestMethod]
        public void WeaponClass_TravelsWithBothProjections()
        {
            // The mirror test above proves FromItem and FromWeenie agree with each other; this
            // proves the shared value is the RIGHT one, not just a consistent one - a swapped
            // PropertyInt at MarketSnapshot.cs's Classify call sites would still pass the mirror
            // test (null == null, or a matching wrong value on both sides).
            var sword = HeavyWeaponsSword();
            Assert.AreEqual("heavy", MarketSnapshot.FromWeenie(sword).WeaponClass);
            Assert.AreEqual("heavy", MarketSnapshot.FromItem(AsMeleeWeapon(sword)).WeaponClass);
            Assert.AreEqual("Heavy Weapons", MarketSnapshot.FromWeenie(sword).WeaponClassName);

            var crossbow = CrossbowLauncher();
            Assert.AreEqual("crossbow", MarketSnapshot.FromWeenie(crossbow).WeaponClass);
            Assert.AreEqual("crossbow", MarketSnapshot.FromItem(AsMissileLauncher(crossbow)).WeaponClass);
            Assert.AreEqual("Crossbow", MarketSnapshot.FromWeenie(crossbow).WeaponClassName);
        }

        [TestMethod]
        public void SlotsAndEquipmentSet_TravelWithBothProjections()
        {
            // Same shape as WeaponClass_TravelsWithBothProjections above: this proves the shared
            // value is the RIGHT one for the same underlying property data, not just a consistent
            // one - a swapped PropertyInt at MarketSnapshot.cs's Classify call sites would still
            // pass a mirror test alone (null == null, or a matching wrong value on both sides).
            var chestPiece = ChestArmorInADefendersSet();
            var expectedSlots = new List<string> { "chest", "abdomen" };

            CollectionAssert.AreEqual(expectedSlots, MarketSnapshot.FromWeenie(chestPiece).Slots);
            CollectionAssert.AreEqual(expectedSlots, MarketSnapshot.FromItem(AsClothing(chestPiece)).Slots);

            Assert.AreEqual("Defenders", MarketSnapshot.FromWeenie(chestPiece).EquipmentSet);
            Assert.AreEqual("Defenders", MarketSnapshot.FromItem(AsClothing(chestPiece)).EquipmentSet);
            Assert.AreEqual("Defenders", MarketSnapshot.FromWeenie(chestPiece).EquipmentSetName);
            Assert.AreEqual("Defenders", MarketSnapshot.FromItem(AsClothing(chestPiece)).EquipmentSetName);
        }

        private static void AssertMirrored(Weenie weenie, Func<Weenie, WorldObject> toItem)
        {
            var fromWeenie = MarketSnapshot.FromWeenie(weenie);
            var fromItem = MarketSnapshot.FromItem(toItem(weenie));

            foreach (var property in typeof(ListingSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var a = property.GetValue(fromWeenie);
                var b = property.GetValue(fromItem);

                if (a is IEnumerable listA && !(a is string))
                {
                    CollectionAssert.AreEqual(listA.Cast<object>().ToList(), ((IEnumerable)b).Cast<object>().ToList(),
                        $"{weenie.GetName()}.{property.Name} differs between FromWeenie and FromItem");
                    continue;
                }

                Assert.AreEqual(a, b, $"{weenie.GetName()}.{property.Name} differs between FromWeenie and FromItem");
            }
        }

        // ---- the wire contract ----

        [TestMethod]
        public void Serialize_UsesTheExactFieldNamesTheWebAppReads()
        {
            // A null field is omitted entirely (JsonIgnoreCondition.WhenWritingNull), so each name is
            // asserted against a fixture that actually carries it.
            var wand = MarketSnapshot.Serialize(MarketSnapshot.FromWeenie(ElementalWand()));

            foreach (var name in new[]
            {
                "\"damage_type\":16", "\"damage_mod\":2.63",
                "\"elemental_damage_bonus\":5", "\"elemental_damage_mod\":1.08",
            })
            {
                Assert.IsTrue(wand.Contains(name), $"the wand JSON is missing {name}:\n{wand}");
            }

            var bag = MarketSnapshot.Serialize(MarketSnapshot.FromWeenie(SalvageBag(117, 100)));
            Assert.IsTrue(bag.Contains("\"workmanship\":1.17"), $"the bag JSON did not carry workmanship 1.17:\n{bag}");
            Assert.IsTrue(bag.Contains("\"num_items_in_material\":100"), bag);

            var cloak = MarketSnapshot.Serialize(MarketSnapshot.FromWeenie(SurgingCloak()));
            Assert.IsTrue(cloak.Contains("\"proc_spell_id\":5204"), cloak);
            Assert.IsTrue(cloak.Contains("\"proc_spell_name\":"), cloak);
            Assert.IsTrue(cloak.Contains("\"ui_effects\":1"), cloak);
        }

        [TestMethod]
        public void Deserialize_AnOldRowStillReads_WithWorkmanshipStoredAsAnInteger()
        {
            // A snapshot is captured at listing time and refreshed by the snapshot backfill, which
            // touches ACTIVE listings only whether it runs automatically after a world start or
            // from /marketbackfill run. Every CLOSED row written
            // before this change is therefore still in market_listing.snapshot_Json with workmanship
            // as a bare integer and none of the new fields present, permanently - so this parse has
            // to keep working no matter how many backfills are run.
            const string legacy =
                "{\"name\":\"Salvage (100)\",\"wcid\":20983,\"item_type\":128,\"item_type_name\":\"TinkeringMaterial\","
                + "\"material_type\":60,\"material_name\":\"Steel\",\"workmanship\":117,\"value\":1000,"
                + "\"icon_id\":100672256,\"spell_ids\":[],\"spell_names\":[],\"panel_lines\":[\"Value: 1000\"]}";

            var snapshot = MarketSnapshot.Deserialize(legacy);

            Assert.AreEqual("Salvage (100)", snapshot.Name, "the legacy row failed to parse and became an empty snapshot");
            Assert.AreEqual(117.0, snapshot.Workmanship.Value, 0.0001,
                "an integer workmanship must still read into the widened double? field");

            Assert.IsNull(snapshot.NumItemsInMaterial);
            Assert.IsNull(snapshot.UiEffects);
            Assert.IsNull(snapshot.DamageType);
            Assert.IsNull(snapshot.DamageMod);
            Assert.IsNull(snapshot.ElementalDamageBonus);
            Assert.IsNull(snapshot.ElementalDamageMod);
            Assert.IsNull(snapshot.ProcSpellId);
            Assert.IsNull(snapshot.ProcSpellName);
        }

        [TestMethod]
        public void Deserialize_ARoundTripOfTheNewFieldsSurvives()
        {
            var before = MarketSnapshot.FromWeenie(SurgingCloak());

            var after = MarketSnapshot.Deserialize(MarketSnapshot.Serialize(before));

            Assert.AreEqual(before.ProcSpellId, after.ProcSpellId);
            Assert.AreEqual(before.ProcSpellName, after.ProcSpellName);
            Assert.AreEqual(before.UiEffects, after.UiEffects);
            CollectionAssert.AreEqual(before.SpellIds, after.SpellIds);
        }

        [TestMethod]
        public void JsonOptions_SnakeCasesTheNewNamesExactlyAsTheContractPublishesThem()
        {
            // A guard on the naming POLICY rather than on one payload: the web side is written
            // against these strings, and a rename here breaks it silently.
            var json = JsonSerializer.Serialize(new ListingSnapshot
            {
                Workmanship = 1.17,
                NumItemsInMaterial = 100,
                UiEffects = 1,
                DamageType = 16,
                DamageMod = 2.63,
                ElementalDamageBonus = 5,
                ElementalDamageMod = 1.08,
                ProcSpellId = 5204,
                ProcSpellName = "Surge of Testing",
                Structure = 1,
                MaxStructure = 1,
                SalvageToolCharges = 100,
            }, MarketSnapshot.JsonOptions);

            foreach (var name in new[]
            {
                "\"workmanship\":1.17", "\"num_items_in_material\":100", "\"ui_effects\":1",
                "\"damage_type\":16", "\"damage_mod\":2.63", "\"elemental_damage_bonus\":5",
                "\"elemental_damage_mod\":1.08", "\"proc_spell_id\":5204",
                "\"proc_spell_name\":\"Surge of Testing\"",
                "\"structure\":1", "\"max_structure\":1", "\"salvage_tool_charges\":100",
            })
            {
                Assert.IsTrue(json.Contains(name), $"the contract name {name} is not what the policy produced:\n{json}");
            }
        }
    }
}
