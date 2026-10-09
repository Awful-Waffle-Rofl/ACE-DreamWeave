using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The mods projection behind the web market's mods section and its Weapon mod / Armor mod
    /// filters (Docs/Market/EQUIPMENT-MODS-WEB-DESIGN.md). Loads the default properties because the
    /// intensity and effect figures read weapon_mod_magnitude_scale and equipment_mod_potency_scale.
    /// </summary>
    [TestClass]
    public class MarketItemModsTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static uint nextGuid = 0x7F580000;

        private IClothingIconSource savedClothingIcons;

        [TestInitialize]
        public void Setup()
        {
            // Nothing here may depend on the dats being installed.
            savedClothingIcons = MarketSnapshot.ClothingIcons;
            MarketSnapshot.ClothingIcons = null;
        }

        [TestCleanup]
        public void Teardown() => MarketSnapshot.ClothingIcons = savedClothingIcons;

        private static Weenie SwordWeenie() => new Weenie
        {
            WeenieClassId = 1001910,
            WeenieType = WeenieType.MeleeWeapon,
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Sword of Modding" } },
            PropertiesInt = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                { PropertyInt.WeaponSkill, (int)Skill.HeavyWeapons },
                { PropertyInt.ItemWorkmanship, 10 },
                { PropertyInt.EncumbranceVal, 100 },
                { PropertyInt.Value, 500 },
            },
        };

        private static WorldObject Sword() => new MeleeWeapon(SwordWeenie(), new ObjectGuid(nextGuid++));

        private static WorldObject Breastplate() => new Clothing(new Weenie
        {
            WeenieClassId = 1001911,
            WeenieType = WeenieType.Clothing,
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Breastplate of Modding" } },
            PropertiesInt = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)ItemType.Armor },
                { PropertyInt.ValidLocations, (int)EquipMask.ChestArmor },
                { PropertyInt.ItemWorkmanship, 10 },
                { PropertyInt.EncumbranceVal, 100 },
                { PropertyInt.Value, 500 },
            },
        }, new ObjectGuid(nextGuid++));

        private static void ApplyPerfect(WorldObject weapon, params WeaponModId[] ids)
        {
            foreach (var id in ids)
            {
                var definition = WeaponModRegistry.Get(id);
                WeaponModTinkerSet.ApplySpecial(weapon, definition, WeaponModValue.MaxMagnitude(definition));
            }
        }

        /// <summary>One projected row rebuilt into the exact appraisal bullet line it came from.</summary>
        private static string AsAppraisalLine(ItemMod mod)
            => $"- {mod.Name}{(mod.IntensityPct.HasValue ? $" [{mod.IntensityPct}%]" : string.Empty)}: {mod.Effect}";

        // ---- the token rule ----

        [TestMethod]
        public void Token_AOneWordMember_IsUnchanged()
        {
            Assert.AreEqual("Heft", MarketItemMods.Token(nameof(WeaponModId.Heft)));
            Assert.AreEqual("Heft", MarketItemMods.Token(WeaponModId.Heft));
        }

        [TestMethod]
        public void Token_ATwoWordMember_SplitsAtTheSecondCapital()
        {
            Assert.AreEqual("Weak Point", MarketItemMods.Token(WeaponModId.WeakPoint));
            Assert.AreEqual("Arcane Defender", MarketItemMods.Token(WeaponModId.ArcaneDefender));
            Assert.AreEqual("Eagle Eye", MarketItemMods.Token(EquipmentModId.EagleEye));
        }

        [TestMethod]
        public void Token_AThreeWordName_SplitsIntoThreeWords()
        {
            // No current WeaponModId or EquipmentModId member has three words (checked 2026-09-14),
            // so the rule is pinned on a literal member-shaped name.
            Assert.AreEqual("Nether Bloom Surge", MarketItemMods.Token("NetherBloomSurge"));
        }

        [TestMethod]
        public void Token_ARunOfCapitals_StaysOneWordUntilALowercaseLetterFollows()
        {
            // No current member has an acronym run (checked 2026-09-14). Pinned so a future one
            // splits predictably instead of letter by letter.
            Assert.AreEqual("AOE Blast", MarketItemMods.Token("AOEBlast"));
            Assert.AreEqual("Spell AOE", MarketItemMods.Token("SpellAOE"));
        }

        [TestMethod]
        public void Token_NullOrEmpty_IsEmpty()
        {
            Assert.AreEqual(string.Empty, MarketItemMods.Token((string)null));
            Assert.AreEqual(string.Empty, MarketItemMods.Token(string.Empty));
        }

        /// <summary>
        /// Saved Browse URLs carry these strings. An enum member rename changes a token and silently
        /// breaks every such URL, so every current row is pinned here, in registry order.
        /// </summary>
        [TestMethod]
        public void Token_OfEveryWeaponRow_IsPinned()
        {
            var expected = new[]
            {
                "Devastation", "Weak Point", "Bloodthirst", "Shield Bypass", "Ambush", "Quickening",
                "Second Wind", "Heft", "Tension", "Leverage", "Attunement", "Focus", "Execution",
                "Efficiency", "Recovery", "Mana Well", "Cleanse", "Longevity", "Siphon", "Quick Refresh",
                "Arcane Defender", "Panic Reload",
            };

            CollectionAssert.AreEqual(expected, WeaponModRegistry.AllMods.Select(d => MarketItemMods.Token(d.Id)).ToArray(),
                "a weapon mod token moved; saved Browse URLs for that mod would stop matching");
        }

        [TestMethod]
        public void Token_OfEveryEquipmentRow_IsPinned()
        {
            var expected = new[]
            {
                // Heavy Draw (id 4) and Shield Check (id 13) were retired 2026-10-02 and dropped from
                // EquipmentModRegistry.AllMods - see EquipmentModId.cs. Their tokens never shipped on a
                // live market listing (pre-launch), so there is no saved Browse URL to protect by keeping
                // them pinned here.
                "Deadeye", "Eagle Eye", "Long Draw", "Splitshot", "Double Volley",
                "Venom", "Acid Proc", "Caustic", "Riposte", "Attack Speed",
                "Thorns", "Bulwark",
                "Frenzied Pace", "Lingering Fury", "Savage Blows", "Break Armor", "Executioner", "Bloodlust",
                "Overchannel", "Echo Cast", "Elemental Rend", "Resonance",
                "Void Damage", "Withering", "Empowered Summons", "Soul Tether", "Nether Bloom",
                "Mana Barrier",
                "Blood Charge", "Transfusion", "Hemorrhage", "Deepen", "Bloodletting", "Sanguinate", "Blood Price", "Clotting",
                "Spellblade", "Harmonics", "Runeblade", "Sundermark", "Surge", "Spellstorm", "Cascade", "Dispelling Edge",
                "Provoke", "Bellow", "Shield Wall",
            };

            CollectionAssert.AreEqual(expected, EquipmentModRegistry.AllMods.Select(d => MarketItemMods.Token(d.Id)).ToArray(),
                "an equipment mod token moved; saved Browse URLs for that mod would stop matching");
        }

        [TestMethod]
        public void Tokens_AreUniqueWithinEachKind()
        {
            var weapon = WeaponModRegistry.AllMods.Select(d => MarketItemMods.Token(d.Id)).ToList();
            var armor = EquipmentModRegistry.AllMods.Select(d => MarketItemMods.Token(d.Id)).ToList();

            Assert.AreEqual(weapon.Count, weapon.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "two weapon rows share a token");
            Assert.AreEqual(armor.Count, armor.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "two equipment rows share a token");
        }

        // ---- the wire contract ----

        [TestMethod]
        public void JsonOptions_SnakeCasesTheModFieldsExactlyAsTheContractPublishesThem()
        {
            // Effect strings here avoid '+', which the default encoder writes as \u002B; the round
            // trip test below covers a real '+' effect.
            var json = JsonSerializer.Serialize(new ListingSnapshot
            {
                Mods = new List<ItemMod>
                {
                    new ItemMod { Kind = "weapon", Token = "Arcane Defender", Name = "Arcane Defender", IntensityPct = 67, Effect = "deflects frontal blows as a 25 armor level shield" },
                    new ItemMod { Kind = "armor", Token = "Deadeye", Name = "Deadeye", IntensityPct = null, Effect = "missile damage" },
                },
                ModCapacity = 3,
                WeaponQualityTier = "Elite",
            }, MarketSnapshot.JsonOptions);

            foreach (var name in new[]
            {
                "\"mods\":[{\"kind\":\"weapon\",\"token\":\"Arcane Defender\",\"name\":\"Arcane Defender\",\"intensity_pct\":67,\"effect\":\"deflects frontal blows as a 25 armor level shield\"}",
                "{\"kind\":\"armor\",\"token\":\"Deadeye\",\"name\":\"Deadeye\",\"effect\":\"missile damage\"}",
                "\"mod_capacity\":3",
                "\"weapon_quality_tier\":\"Elite\"",
            })
            {
                Assert.IsTrue(json.Contains(name), $"the contract name {name} is not what the policy produced:\n{json}");
            }
        }

        [TestMethod]
        public void Serialize_OmitsAllThreeFieldsWhenNull()
        {
            var json = MarketSnapshot.Serialize(new ListingSnapshot { Name = "Plain Sword" });

            Assert.IsFalse(json.Contains("\"mods\""), json);
            Assert.IsFalse(json.Contains("\"mod_capacity\""), json);
            Assert.IsFalse(json.Contains("\"weapon_quality_tier\""), json);
        }

        [TestMethod]
        public void Deserialize_RoundTripsTheModFields()
        {
            var before = new ListingSnapshot
            {
                Name = "Sword of Modding",
                Mods = new List<ItemMod> { new ItemMod { Kind = "weapon", Token = "Devastation", Name = "Devastation", IntensityPct = 67, Effect = "+4 critical damage rating" } },
                ModCapacity = 2,
                WeaponQualityTier = "God",
            };

            var after = MarketSnapshot.Deserialize(MarketSnapshot.Serialize(before));

            Assert.AreEqual(1, after.Mods.Count);
            Assert.AreEqual("+4 critical damage rating", after.Mods[0].Effect);
            Assert.AreEqual(67, after.Mods[0].IntensityPct);
            Assert.AreEqual(2, after.ModCapacity);
            Assert.AreEqual("God", after.WeaponQualityTier);
        }

        // ---- the effect text is exactly what Describe prints ----

        [TestMethod]
        public void WeaponEffect_IsTheTextDescribePrintsAfterItsColon_ForEveryRow()
        {
            foreach (var definition in WeaponModRegistry.AllMods)
            {
                var magnitude = WeaponModValue.MaxMagnitude(definition) * 0.8;
                var line = WeaponModDisplay.Describe(definition, magnitude);

                Assert.AreEqual(line.Substring(line.IndexOf(": ", StringComparison.Ordinal) + 2),
                    WeaponModDisplay.Effect(definition, magnitude), $"{definition.Id}: Effect and Describe disagree");
            }

            Assert.AreEqual(string.Empty, WeaponModDisplay.Effect(null, 1.0));
        }

        [TestMethod]
        public void EquipmentEffect_IsTheTextDescribePrintsAfterItsColon_ForEveryRow()
        {
            foreach (var definition in EquipmentModRegistry.AllMods)
            {
                var line = EquipmentModDisplay.Describe(definition, 0.5);

                Assert.AreEqual(line.Substring(line.IndexOf(": ", StringComparison.Ordinal) + 2),
                    EquipmentModDisplay.Effect(definition, 0.5), $"{definition.Id}: Effect and Describe disagree");
            }

            Assert.AreEqual(string.Empty, EquipmentModDisplay.Effect(null, 1.0));
        }

        // ---- the projection ----

        [TestMethod]
        public void Weapon_TierAAndTierBRows_ProjectTheAppraisalLinesParts_InRegistryOrder()
        {
            var sword = Sword();
            var devastation = WeaponModRegistry.Get(WeaponModId.Devastation);   // Tier A, integer native
            var quickening = WeaponModRegistry.Get(WeaponModId.Quickening);     // Tier B, fraction record

            WeaponModTinkerSet.ApplySpecial(sword, quickening, WeaponModValue.MaxMagnitude(quickening) * 0.75);
            WeaponModTinkerSet.ApplySpecial(sword, devastation, 4.0);

            var snapshot = MarketSnapshot.FromItem(sword);

            Assert.IsNotNull(snapshot.Mods, "a modded weapon projected no mods");
            CollectionAssert.AreEqual(new[] { "Devastation", "Quickening" }, snapshot.Mods.Select(m => m.Token).ToArray());
            Assert.IsTrue(snapshot.Mods.All(m => m.Kind == MarketItemMods.KindWeapon));

            // The whole point: the web row and the appraisal line are one text.
            CollectionAssert.AreEqual(WeaponModDisplay.GetAppraisalLines(sword), snapshot.Mods.Select(AsAppraisalLine).ToList(),
                "a projected weapon row disagrees with the appraisal panel");

            // Literal pins at the default weapon_mod_magnitude_scale of 1.0 (PropertyManager default).
            Assert.AreEqual(67, snapshot.Mods[0].IntensityPct);
            Assert.AreEqual("+4 critical damage rating", snapshot.Mods[0].Effect);
            Assert.AreEqual(75, snapshot.Mods[1].IntensityPct);
            Assert.AreEqual("+18% attack speed", snapshot.Mods[1].Effect);

            Assert.IsNull(snapshot.ModCapacity);
            Assert.IsNull(snapshot.WeaponQualityTier, "two specials sum to 142, below every tier");
        }

        [TestMethod]
        public void Armor_EquipmentRows_ProjectTheAppraisalLinesParts_AndTheCapacity()
        {
            var plate = Breastplate();
            plate.SetProperty(EquipmentModRegistry.Get(EquipmentModId.EagleEye).Property, 0.5);
            plate.SetProperty(EquipmentModRegistry.Get(EquipmentModId.Deadeye).Property, 0.83);
            plate.SetProperty(PropertyInt.GearModCapacity, 3);

            var snapshot = MarketSnapshot.FromItem(plate);

            CollectionAssert.AreEqual(new[] { "Deadeye", "Eagle Eye" }, snapshot.Mods.Select(m => m.Token).ToArray());
            Assert.IsTrue(snapshot.Mods.All(m => m.Kind == MarketItemMods.KindArmor));
            Assert.AreEqual(83, snapshot.Mods[0].IntensityPct);
            Assert.AreEqual(3, snapshot.ModCapacity);
            Assert.IsNull(snapshot.WeaponQualityTier);

            var panel = EquipmentModDisplay.GetAppraisalLines(plate);

            CollectionAssert.AreEqual(panel.Where(l => !l.StartsWith("- Mod Capacity:", StringComparison.Ordinal)).ToList(),
                snapshot.Mods.Select(AsAppraisalLine).ToList(), "a projected armor row disagrees with the appraisal panel");
            Assert.IsTrue(panel.Contains("- Mod Capacity: 3"), "control: the panel prints the same capacity");
        }

        [TestMethod]
        public void ACapacityOnlyItem_ProjectsCapacityAndNoMods()
        {
            var plate = Breastplate();
            plate.SetProperty(PropertyInt.GearModCapacity, 2);

            var snapshot = MarketSnapshot.FromItem(plate);

            Assert.IsNull(snapshot.Mods, "an item with capacity and no mods must project null, never an empty list");
            Assert.AreEqual(2, snapshot.ModCapacity);
        }

        [TestMethod]
        public void QualityTier_IsProjectedOnceSummedIntensityCrossesAThreshold()
        {
            var below = Sword();
            ApplyPerfect(below, WeaponModId.Ambush, WeaponModId.Quickening);
            Assert.IsNull(MarketSnapshot.FromItem(below).WeaponQualityTier, "two perfect specials sum to 200, below Exceptional");

            var exceptional = Sword();
            ApplyPerfect(exceptional, WeaponModId.Ambush, WeaponModId.Quickening, WeaponModId.SecondWind);
            Assert.AreEqual("Exceptional", MarketSnapshot.FromItem(exceptional).WeaponQualityTier,
                "three perfect specials sum to 300, the inclusive Exceptional threshold");

            var god = Sword();
            ApplyPerfect(god, WeaponModId.Ambush, WeaponModId.Quickening, WeaponModId.SecondWind, WeaponModId.Focus);
            Assert.AreEqual("God", MarketSnapshot.FromItem(god).WeaponQualityTier, "four perfect specials sum to 400");
        }

        [TestMethod]
        public void QualityTier_EliteRange_IsProjectedWhenSummedIntensityLandsInside350To374()
        {
            var elite = Sword();
            ApplyPerfect(elite, WeaponModId.Ambush, WeaponModId.SecondWind, WeaponModId.Focus);

            var quickening = WeaponModRegistry.Get(WeaponModId.Quickening);
            WeaponModTinkerSet.ApplySpecialAtFraction(elite, quickening, 0.6);

            var expectedTotal = WeaponModTinkerSet.ReadSpecials(elite)
                .Sum(s => WeaponModDisplay.IntensityPercent(s.Definition, s.Magnitude));
            Assert.IsTrue(expectedTotal >= 350 && expectedTotal <= 374,
                $"test setup must land inside the Elite band (350-374); computed {expectedTotal}");

            Assert.AreEqual("Elite", MarketSnapshot.FromItem(elite).WeaponQualityTier);
        }

        [TestMethod]
        public void WithTheWeaponSwitchOff_WeaponRowsAndTheTierAreAbsent()
        {
            var sword = Sword();
            ApplyPerfect(sword, WeaponModId.Ambush, WeaponModId.Quickening, WeaponModId.SecondWind, WeaponModId.Focus);

            var saved = MarketItemMods.WeaponModsEnabledSource;

            try
            {
                MarketItemMods.WeaponModsEnabledSource = () => false;

                var snapshot = MarketSnapshot.FromItem(sword);

                Assert.IsNull(snapshot.Mods);
                Assert.IsNull(snapshot.WeaponQualityTier);
            }
            finally
            {
                MarketItemMods.WeaponModsEnabledSource = saved;
            }

            Assert.AreEqual("God", MarketSnapshot.FromItem(sword).WeaponQualityTier, "control: with the switch restored the tier is back");
        }

        [TestMethod]
        public void WithTheEquipmentSwitchOff_ArmorRowsAndCapacityAreAbsent_WhileWeaponRowsRemain()
        {
            var sword = Sword();
            ApplyPerfect(sword, WeaponModId.Heft);
            sword.SetProperty(EquipmentModRegistry.Get(EquipmentModId.Deadeye).Property, 0.5);
            sword.SetProperty(PropertyInt.GearModCapacity, 1);

            var saved = MarketItemMods.EquipmentModsEnabledSource;

            try
            {
                MarketItemMods.EquipmentModsEnabledSource = () => false;

                var snapshot = MarketSnapshot.FromItem(sword);

                CollectionAssert.AreEqual(new[] { MarketItemMods.KindWeapon }, snapshot.Mods.Select(m => m.Kind).ToArray());
                Assert.IsNull(snapshot.ModCapacity);
            }
            finally
            {
                MarketItemMods.EquipmentModsEnabledSource = saved;
            }
        }

        [TestMethod]
        public void AnItemCarryingBothKinds_ListsWeaponRowsFirst()
        {
            var sword = Sword();
            sword.SetProperty(EquipmentModRegistry.Get(EquipmentModId.Deadeye).Property, 0.5);
            ApplyPerfect(sword, WeaponModId.Heft);

            var snapshot = MarketSnapshot.FromItem(sword);

            CollectionAssert.AreEqual(new[] { MarketItemMods.KindWeapon, MarketItemMods.KindArmor },
                snapshot.Mods.Select(m => m.Kind).ToArray());
            CollectionAssert.AreEqual(new[] { "Heft", "Deadeye" }, snapshot.Mods.Select(m => m.Token).ToArray());
        }

        /// <summary>
        /// The tier must come from the projected weapon rows, never a fresh WeaponQualityTiers.Evaluate
        /// re-walk (which could disagree if a scale retune landed between the two reads mid backfill), and
        /// it must never count an armor row's intensity toward a weapon's tier.
        /// </summary>
        [TestMethod]
        public void Tier_OfAnItemCarryingBothKinds_MatchesEvaluate_AndExcludesArmorIntensity()
        {
            var sword = Sword();
            sword.SetProperty(EquipmentModRegistry.Get(EquipmentModId.Deadeye).Property, 1.0);
            ApplyPerfect(sword, WeaponModId.Ambush, WeaponModId.Quickening, WeaponModId.SecondWind);

            var snapshot = MarketSnapshot.FromItem(sword);

            Assert.AreEqual(WeaponQualityTiers.NameFor(WeaponQualityTiers.Evaluate(sword)), snapshot.WeaponQualityTier,
                "the projected tier must match WeaponQualityTiers.Evaluate for the same item");
            Assert.AreEqual("Exceptional", snapshot.WeaponQualityTier,
                "control: three perfect weapon specials, a full-intensity armor row, still Exceptional not God");
        }

        [TestMethod]
        public void AnUnreportableIntensity_IsOmitted_ExactlyAsAppraisalDropsTheBracket()
        {
            var plate = Breastplate();
            plate.SetProperty(EquipmentModRegistry.Get(EquipmentModId.Deadeye).Property, 0.7);

            var prior = PropertyManager.GetDouble("equipment_mod_potency_scale").Item;

            try
            {
                PropertyManager.ModifyDouble("equipment_mod_potency_scale", 0.0);

                var snapshot = MarketSnapshot.FromItem(plate);

                Assert.IsNull(snapshot.Mods[0].IntensityPct, "a muted scale leaves no honest figure");
                CollectionAssert.AreEqual(EquipmentModDisplay.GetAppraisalLines(plate), snapshot.Mods.Select(AsAppraisalLine).ToList());
            }
            finally
            {
                PropertyManager.ModifyDouble("equipment_mod_potency_scale", prior);
            }
        }

        [TestMethod]
        public void AModLessItem_ProjectsAllThreeFieldsNull()
        {
            var snapshot = MarketSnapshot.FromItem(Sword());

            Assert.IsNull(snapshot.Mods);
            Assert.IsNull(snapshot.ModCapacity);
            Assert.IsNull(snapshot.WeaponQualityTier);
            Assert.IsFalse(MarketSnapshot.Serialize(snapshot).Contains("\"mods\""));
        }

        /// <summary>
        /// A mod-less item must read no switch (and therefore no PropertyManager value): the presence check
        /// runs first. Counting, not throwing, because Apply swallows a throw and would pass either way.
        /// </summary>
        [TestMethod]
        public void AModLessItem_ReadsNeitherSwitch()
        {
            var savedWeapon = MarketItemMods.WeaponModsEnabledSource;
            var savedEquipment = MarketItemMods.EquipmentModsEnabledSource;
            var reads = 0;

            try
            {
                MarketItemMods.WeaponModsEnabledSource = () => { reads++; return true; };
                MarketItemMods.EquipmentModsEnabledSource = () => { reads++; return true; };

                MarketSnapshot.FromItem(Sword());

                Assert.AreEqual(0, reads, "a mod-less item read a switch; presence must be checked first");

                var modded = Sword();
                ApplyPerfect(modded, WeaponModId.Heft);
                MarketSnapshot.FromItem(modded);

                Assert.IsTrue(reads > 0, "control: a modded item must read its switch");
            }
            finally
            {
                MarketItemMods.WeaponModsEnabledSource = savedWeapon;
                MarketItemMods.EquipmentModsEnabledSource = savedEquipment;
            }
        }

        [TestMethod]
        public void FromWeenie_LeavesTheModFieldsNull_EvenForAWeenieCarryingAModRecord()
        {
            var weenie = SwordWeenie();
            weenie.PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.WeaponModDevastation, 4.0 } };
            weenie.PropertiesInt[PropertyInt.GearModCapacity] = 3;

            var snapshot = MarketSnapshot.FromWeenie(weenie);

            Assert.IsNull(snapshot.Mods);
            Assert.IsNull(snapshot.ModCapacity);
            Assert.IsNull(snapshot.WeaponQualityTier);
        }
    }
}
