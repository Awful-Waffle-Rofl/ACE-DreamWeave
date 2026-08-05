using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tests for the weapon-mod system: the registry's structural invariants, action and material resolution,
    /// the slot arithmetic, the cumulative odds table, and - the reason this system is split into pure classes
    /// at all - the apply/reverse round trip for both layer 1 tinkers and Tier A specials.
    ///
    /// What is NOT covered here, and needs the live loop instead, because it all requires a Player with a
    /// session, inventory and motion table: WeaponModManager.UseObjectOnTarget end to end (the busy and
    /// peace-mode guards, the ClapHands chain and the SECOND VerifyUseRequirements that guards it, NextUseTime),
    /// the player-facing half of VerifyUseRequirements (the inventory-only checks and the refusal messages),
    /// HandleApply's networking, and the RecipeManager intercept firing for a real bag and falling through when
    /// weapon_mods_enabled is false.
    ///
    /// The confirmation round trip used to be on that list. It is gone entirely: this system raised its own
    /// dialog until 2026-07-30 and no longer does, because the client's generic tinkering prompt already gates
    /// every use before the server is contacted. What survives here is a pair of reflection tests at the bottom
    /// of this file asserting the SHAPE of that removal.
    ///
    /// The roller's RNG is not injectable (the sibling system has none either), so the odds table is exercised
    /// through its pure resolution function over a deterministic sweep rather than by sampling, and coverage
    /// tests run many iterations.
    /// </summary>
    [TestClass]
    public class WeaponModTests
    {
        private static uint nextGuid = 0x7E000000;   // static guid range, clear of GuidManager
        private static uint nextWcid = 992000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// A bare item from an in-memory weenie: no database, no dat files. Every property read and write the
        /// weapon-mod flow performs goes through the generic GetProperty/SetProperty surface, so the concrete
        /// WorldObject subclass is irrelevant - what matters is that ItemType and CombatUse are settable.
        /// </summary>
        private static WorldObject MakeWeapon(ItemType itemType = ItemType.MeleeWeapon, CombatUse? combatUse = CombatUse.Melee,
            Dictionary<PropertyInt, int> ints = null, Dictionary<PropertyFloat, double> floats = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Weapon" } },
            };

            if (combatUse != null)
                weenie.PropertiesInt[PropertyInt.CombatUse] = (int)combatUse.Value;

            if (ints != null)
            {
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;
            }

            if (floats != null)
            {
                weenie.PropertiesFloat = new Dictionary<PropertyFloat, double>();

                foreach (var kvp in floats)
                    weenie.PropertiesFloat[kvp.Key] = kvp.Value;
            }

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        // ---------------- registry integrity ----------------

        [TestMethod]
        public void Registry_HasOneRowPerCatalogEntry()
        {
            var enumValues = Enum.GetValues(typeof(WeaponModId)).Cast<WeaponModId>().ToList();

            Assert.AreEqual(13, enumValues.Count,
                "the catalog is 6 Tier A plus 7 Tier B. Tier A came to 6 in the 2026-07-30 pool cut - Warding, Crit Ward, Resolute, Vigor and Mending were removed as defensive/sustain rows, and Armor Cleaving was cut before that. Tier B is 7, not 9: Sunder and Rampage are phase 2 and deliberately have no enum member");
            Assert.AreEqual(13, WeaponModRegistry.AllMods.Count, "every WeaponModId needs exactly one registry row");

            Assert.AreEqual(6, WeaponModRegistry.TierAMods.Count, "the Tier A half");
            Assert.AreEqual(7, WeaponModRegistry.TierBMods.Count, "the Tier B half");
            Assert.AreEqual(WeaponModRegistry.AllMods.Count,
                WeaponModRegistry.TierAMods.Count + WeaponModRegistry.TierBMods.Count,
                "every row belongs to exactly one tier");

            foreach (var id in enumValues)
            {
                Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id}: missing registry row");
                Assert.AreEqual(id, definition.Id);
            }
        }

        [TestMethod]
        public void Registry_EveryRowIsInternallyConsistent()
        {
            foreach (var mod in WeaponModRegistry.AllMods)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(mod.DisplayName), $"{mod.Id}: DisplayName is empty");
                Assert.IsFalse(string.IsNullOrWhiteSpace(mod.DisplayFormat), $"{mod.Id}: DisplayFormat is empty");
                Assert.IsTrue(mod.MaxRoll > 0, $"{mod.Id}: MaxRoll must be > 0");
                Assert.AreNotEqual(WeaponClass.None, mod.Classes, $"{mod.Id}: belongs to no weapon class, so it can never be rolled");

                if (mod.Tier == WeaponModTier.A)
                {
                    // exactly one native property, or the apply/reverse arithmetic has nothing to write to
                    Assert.IsTrue((mod.NativeInt != null) ^ (mod.NativeFloat != null),
                        $"{mod.Id}: a Tier A row must set exactly one of NativeInt / NativeFloat");

                    var record = (int)mod.Record;
                    Assert.IsTrue(record >= WeaponModRegistry.PropertyBandStart && record <= WeaponModRegistry.PropertyBandEnd,
                        $"{mod.Id}: record PropertyFloat {mod.Record} ({record}) is outside the reserved Tier A range " +
                        $"{WeaponModRegistry.PropertyBandStart}-{WeaponModRegistry.PropertyBandEnd}");
                }
                else
                {
                    // NO native at all. This is what keeps the magnitude fractional (IsInteger is derived from
                    // NativeInt), what makes ApplySpecial write only the record, and what makes reversal a bare
                    // RemoveProperty. A Tier B row that grew a native would break all three silently.
                    Assert.IsNull(mod.NativeInt, $"{mod.Id}: a Tier B row must set no native property");
                    Assert.IsNull(mod.NativeFloat, $"{mod.Id}: a Tier B row must set no native property");
                    Assert.IsFalse(mod.WritesNative, $"{mod.Id}: a Tier B row must not write a native property");
                    Assert.IsFalse(mod.IsInteger,
                        $"{mod.Id}: a Tier B magnitude is a FRACTION and must never take the integer branch in WeaponModValue.Resolve, which rounds and then floors at 1");

                    var record = (int)mod.Record;
                    Assert.IsTrue(record >= WeaponModRegistry.TierBPropertyBandStart && record <= WeaponModRegistry.TierBPropertyBandEnd,
                        $"{mod.Id}: record PropertyFloat {mod.Record} ({record}) is outside the reserved Tier B range " +
                        $"{WeaponModRegistry.TierBPropertyBandStart}-{WeaponModRegistry.TierBPropertyBandEnd}");
                }

                Assert.IsFalse(string.IsNullOrWhiteSpace(mod.Format(mod.MaxRoll)), $"{mod.Id}: Format produced nothing");
            }

            var ids = WeaponModRegistry.AllMods.Select(m => m.Id).ToList();
            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "duplicate WeaponModId in the registry");

            var records = WeaponModRegistry.AllMods.Select(m => m.Record).ToList();
            Assert.AreEqual(records.Count, records.Distinct().Count(), "duplicate record PropertyFloat in the registry");
        }

        /// <summary>
        /// HARD invariant. Devastation and Weak Point must route through the gear ratings, never through
        /// PropertyFloat.CriticalMultiplier 136 / CriticalFrequency 147: those two are consumed with Math.Max
        /// against the skill-scaled imbue bonus rather than summed, and this system preserves imbues by design,
        /// so a rolled value would be silently swallowed on exactly the weapons players care about.
        /// </summary>
        [TestMethod]
        public void Registry_CritRoutesThroughRatingsNeverTheRetailProperties()
        {
            Assert.AreEqual(PropertyInt.GearCritDamage, WeaponModRegistry.Get(WeaponModId.Devastation).NativeInt);
            Assert.AreEqual(PropertyInt.GearCrit, WeaponModRegistry.Get(WeaponModId.WeakPoint).NativeInt);

            // pinned by raw id too, so a renamed enum member cannot quietly move these
            Assert.AreEqual(374, (int)WeaponModRegistry.Get(WeaponModId.Devastation).NativeInt.Value);
            Assert.AreEqual(372, (int)WeaponModRegistry.Get(WeaponModId.WeakPoint).NativeInt.Value);

            foreach (var mod in WeaponModRegistry.AllMods)
            {
                Assert.AreNotEqual(PropertyFloat.CriticalMultiplier, mod.NativeFloat, $"{mod.Id} must not use the retail CriticalMultiplier");
                Assert.AreNotEqual(PropertyFloat.CriticalFrequency, mod.NativeFloat, $"{mod.Id} must not use the retail CriticalFrequency");

                // Armor Cleaving is cut: IgnoreArmor 155 is read only as a null/non-null flag, so there is no
                // magnitude axis to roll
                Assert.AreNotEqual(PropertyFloat.IgnoreArmor, mod.NativeFloat, $"{mod.Id} must not use IgnoreArmor - Armor Cleaving is cut by design");
            }
        }

        /// <summary>
        /// Pool depth is the arithmetic the design's power assessment rests on, so it is asserted rather than
        /// left to inspection, in BOTH states of the system's ONE gate: specials melee 5 / missile 5 / caster 3
        /// with weapon_mods_enabled off, and melee 11 / missile 11 / caster 9 with it on. Tinkers are melee 4,
        /// missile 3, caster 4 either way - Tier B does not touch layer 1.
        ///
        /// UPDATED 2026-07-30 from melee 10 / missile 10 / caster 8, when Warding, Crit Ward, Resolute, Vigor
        /// and Mending were removed to make the pool damage-oriented only. The tinker pools did NOT move.
        ///
        /// UPDATED AGAIN 2026-07-30: Tier B's separate tunable (weapon_mod_tier_b_enabled) was removed and the
        /// whole system consolidated onto weapon_mods_enabled. The DEPTHS did not move with it - only which
        /// bool selects between them.
        ///
        /// The gate-on numbers are COMPUTED from the class columns rather than restated: Tier B adds the three
        /// leeches, Ambush and Second Wind to every class, Quickening to melee and missile, and Overload to
        /// casters - so 6 to melee, 6 to missile, 6 to caster.
        /// </summary>
        [TestMethod]
        public void Registry_PoolDepthMatchesTheDesign()
        {
            // the live accessor, at the shipped default (gate off)
            Assert.IsFalse(WeaponModRegistry.Enabled(), "precondition: weapon_mods_enabled ships FALSE");

            Assert.AreEqual(5, WeaponModRegistry.Pool(WeaponClass.Melee).Count, "melee special pool");
            Assert.AreEqual(5, WeaponModRegistry.Pool(WeaponClass.Missile).Count, "missile special pool");
            Assert.AreEqual(3, WeaponModRegistry.Pool(WeaponClass.Caster).Count, "caster special pool");

            // the same numbers through the pure overload, so the gate and the depths are pinned independently
            Assert.AreEqual(5, WeaponModRegistry.Pool(WeaponClass.Melee, false).Count, "melee special pool, gate off");
            Assert.AreEqual(5, WeaponModRegistry.Pool(WeaponClass.Missile, false).Count, "missile special pool, gate off");
            Assert.AreEqual(3, WeaponModRegistry.Pool(WeaponClass.Caster, false).Count, "caster special pool, gate off");

            Assert.AreEqual(11, WeaponModRegistry.Pool(WeaponClass.Melee, true).Count, "melee special pool, gate on: 5 Tier A + 6 Tier B");
            Assert.AreEqual(11, WeaponModRegistry.Pool(WeaponClass.Missile, true).Count, "missile special pool, gate on: 5 Tier A + 6 Tier B");
            Assert.AreEqual(9, WeaponModRegistry.Pool(WeaponClass.Caster, true).Count, "caster special pool, gate on: 3 Tier A + 6 Tier B");

            Assert.AreEqual(4, WeaponTinkerTable.Pool(WeaponClass.Melee).Count, "melee tinker pool");
            Assert.AreEqual(3, WeaponTinkerTable.Pool(WeaponClass.Missile).Count, "missile tinker pool");
            Assert.AreEqual(4, WeaponTinkerTable.Pool(WeaponClass.Caster).Count, "caster tinker pool");

            Assert.AreEqual(0, WeaponModRegistry.Pool(WeaponClass.None).Count);
            Assert.AreEqual(0, WeaponModRegistry.Pool(WeaponClass.None, true).Count);
            Assert.AreEqual(0, WeaponTinkerTable.Pool(WeaponClass.None).Count);
        }

        /// <summary>
        /// Oak is not reversible: its script is "WeaponTime -= 50" with a floor at 0, and 75% of weapon weenies
        /// carrying WeaponTime are already below 50, so nominal subtraction inflates the property instead of
        /// restoring it. It must never appear in a pool.
        /// </summary>
        [TestMethod]
        public void TinkerTable_OakIsInNoPool()
        {
            Assert.IsFalse(WeaponTinkerTable.IsKnown(MaterialType.Oak), "Oak must not be a known layer 1 material - it cannot be reversed");

            foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
            {
                Assert.IsFalse(WeaponTinkerTable.Pool(weaponClass).Any(m => m.Material == MaterialType.Oak),
                    $"Oak is in the {weaponClass} tinker pool");
            }

            // Velvet is out of the missile pool on purpose: GetWeaponOffenseModifier returns the default
            // unconditionally on a ranged weapon, so WeaponOffense is a dead roll there
            Assert.IsFalse(WeaponTinkerTable.Pool(WeaponClass.Missile).Any(m => m.Material == MaterialType.Velvet),
                "Velvet must stay out of the missile pool - WeaponOffense is dead on ranged weapons");
        }

        [TestMethod]
        public void TinkerTable_MaterialIdsMatchTheVerifiedTable()
        {
            // pinned by raw id as well as by name, against Source/ACE.Entity/Enum/MaterialType.cs
            Assert.AreEqual(61, (int)MaterialType.Iron);
            Assert.AreEqual(74, (int)MaterialType.Mahogany);
            Assert.AreEqual(23, (int)MaterialType.GreenGarnet);
            Assert.AreEqual(33, (int)MaterialType.Opal);
            Assert.AreEqual(67, (int)MaterialType.Granite);
            Assert.AreEqual(57, (int)MaterialType.Brass);
            Assert.AreEqual(7, (int)MaterialType.Velvet);

            Assert.AreEqual(7, WeaponTinkerTable.AllMaterials.Count, "the layer 1 table is 7 materials after the Oak drop");

            foreach (var material in WeaponTinkerTable.AllMaterials)
            {
                Assert.IsTrue((material.IntProperty != null) ^ (material.FloatProperty != null),
                    $"{material.Material}: exactly one of IntProperty / FloatProperty must be set");
                Assert.AreNotEqual(WeaponClass.None, material.Classes, $"{material.Material}: belongs to no weapon class");
            }
        }

        // ---------------- action and material resolution ----------------

        [TestMethod]
        public void Action_TourmalineRerollsAmethystSwapsAnythingElseDoesNeither()
        {
            Assert.AreEqual(WeaponModManager.WeaponModAction.Reroll,
                WeaponModManager.ResolveAction(ItemType.TinkeringMaterial, MaterialType.Tourmaline));

            Assert.AreEqual(WeaponModManager.WeaponModAction.Swap,
                WeaponModManager.ResolveAction(ItemType.TinkeringMaterial, MaterialType.Amethyst));

            // pinned by raw id: MaterialType 43 (0x2B) is Tourmaline (bag wcid 21082), 12 (0x0C) is Amethyst
            // (bag wcid 21036)
            Assert.AreEqual(43, (int)MaterialType.Tourmaline);
            Assert.AreEqual(12, (int)MaterialType.Amethyst);
            Assert.AreEqual(WeaponModManager.WeaponModAction.Reroll,
                WeaponModManager.ResolveAction(ItemType.TinkeringMaterial, (MaterialType)0x2B));
            Assert.AreEqual(WeaponModManager.WeaponModAction.Swap,
                WeaponModManager.ResolveAction(ItemType.TinkeringMaterial, (MaterialType)0x0C));

            // every other salvage bag keeps its ordinary tinkering behavior
            foreach (var other in new[] { MaterialType.Iron, MaterialType.Silver, MaterialType.TigerEye, MaterialType.Obsidian, MaterialType.Granite })
            {
                Assert.AreEqual(WeaponModManager.WeaponModAction.None,
                    WeaponModManager.ResolveAction(ItemType.TinkeringMaterial, other), $"{other} must route to neither flow");
            }

            Assert.AreEqual(WeaponModManager.WeaponModAction.None,
                WeaponModManager.ResolveAction(ItemType.TinkeringMaterial, null));
        }

        [TestMethod]
        public void Material_RequiresBothItemTypeAndMaterialType()
        {
            Assert.IsTrue(WeaponModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Tourmaline));
            Assert.IsTrue(WeaponModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Amethyst));

            // a RAW gem carries the same MaterialType but is ItemType.Gem - requiring TinkeringMaterial is what
            // stops it being taken for a salvage bag
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.Gem, MaterialType.Tourmaline));
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.Gem, MaterialType.Amethyst));
            Assert.AreEqual(WeaponModManager.WeaponModAction.None, WeaponModManager.ResolveAction(ItemType.Gem, MaterialType.Tourmaline));
            Assert.AreEqual(WeaponModManager.WeaponModAction.None, WeaponModManager.ResolveAction(ItemType.Gem, MaterialType.Amethyst));

            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.TinkeringMaterial, MaterialType.Silver));
            Assert.IsFalse(WeaponModManager.IsModMaterial(ItemType.MeleeWeapon, MaterialType.Tourmaline));
        }

        [TestMethod]
        public void Classify_RoutesWeaponsAndRejectsTheImpostors()
        {
            Assert.AreEqual(WeaponClass.Melee, WeaponClassifier.Classify(ItemType.MeleeWeapon, CombatUse.Melee));
            Assert.AreEqual(WeaponClass.Melee, WeaponClassifier.Classify(ItemType.MeleeWeapon, CombatUse.TwoHanded));
            Assert.AreEqual(WeaponClass.Missile, WeaponClassifier.Classify(ItemType.MissileWeapon, CombatUse.Missile));
            Assert.AreEqual(WeaponClass.Caster, WeaponClassifier.Classify(ItemType.Caster, CombatUse.Melee));

            // shields carry ItemType.MeleeWeapon, ammunition carries ItemType.MissileWeapon
            Assert.AreEqual(WeaponClass.None, WeaponClassifier.Classify(ItemType.MeleeWeapon, CombatUse.Shield));
            Assert.AreEqual(WeaponClass.None, WeaponClassifier.Classify(ItemType.MissileWeapon, CombatUse.Ammo));

            Assert.AreEqual(WeaponClass.None, WeaponClassifier.Classify(ItemType.Armor, CombatUse.None));
            Assert.AreEqual(WeaponClass.None, WeaponClassifier.Classify(ItemType.Jewelry, null));

            Assert.AreEqual(WeaponClass.Missile, WeaponClassifier.Classify(MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile)));
            Assert.AreEqual(WeaponClass.None, WeaponClassifier.Classify(MakeWeapon(ItemType.MeleeWeapon, CombatUse.Shield)));
        }

        [TestMethod]
        public void Refusal_RulesHoldOverPlainValues()
        {
            const int full = WeaponModRegistry.TotalSlots;

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, true, 0, 0, 0));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAModMaterial,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.None, WeaponClass.Melee, true, true, full, 0, 0));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAWeapon,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.None, true, true, full, 0, 0));

            // a weapon with no workmanship would mint specials worth nothing; refused, never defaulted to zero
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoWorkmanship,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, false, true, full, 0, 0));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, true, full, 0, 10));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.TinkerLogMismatch,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, false, full, 0, 0));

            // swap-only rules
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.SwapNeedsFullBudget,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, true, true, 9, 0, 0));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.SwapAtSpecialCap,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, true, true, full, 3, 0));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, true, true, full, 2, 0));

            // ... and neither of them applies to the reroll, which is what makes it the path back from a bad trio
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, true, 3, 3, 0));
        }

        // ---------------- slot arithmetic ----------------

        [TestMethod]
        public void Slots_ReservedPlusSpecialsPlusTinkersIsAlwaysTen()
        {
            for (var reserved = 0; reserved <= WeaponModRegistry.TotalSlots; reserved++)
            {
                for (var rolled = 0; rolled <= 5; rolled++)
                {
                    var specials = WeaponModRoller.ClampSpecialCount(rolled, reserved);
                    var tinkers = WeaponModTinkerSet.ComputeTinkerCount(reserved, specials);

                    Assert.IsTrue(specials >= 0, $"reserved {reserved}, rolled {rolled}: negative special count");
                    Assert.IsTrue(tinkers >= 0, $"reserved {reserved}, rolled {rolled}: negative tinker count");

                    Assert.IsTrue(specials <= Math.Min(WeaponModRegistry.MaxSpecials, WeaponModRegistry.TotalSlots - reserved),
                        $"reserved {reserved}, rolled {rolled}: {specials} specials exceeds min(3, 10 - reserved)");

                    Assert.AreEqual(WeaponModRegistry.TotalSlots, reserved + specials + tinkers,
                        $"reserved {reserved}, rolled {rolled}: the budget does not add to 10");
                }
            }
        }

        /// <summary>
        /// ImbuedEffectType is a [Flags] enum that includes the high bits 0x20000000, 0x40000000 and 0x80000000,
        /// so an unclamped popcount can exceed 10 on odd data and drive tinkerCount negative.
        /// </summary>
        [TestMethod]
        public void Slots_ImbuePopcountIsClampedToTen()
        {
            Assert.AreEqual(0, WeaponModTinkerSet.ReservedImbueSlots(0));
            Assert.AreEqual(1, WeaponModTinkerSet.ReservedImbueSlots(0x1));
            Assert.AreEqual(3, WeaponModTinkerSet.ReservedImbueSlots(0x1 | 0x4 | 0x40));

            // every bit set: 32 bits, clamped to 10
            Assert.AreEqual(10, WeaponModTinkerSet.ReservedImbueSlots(unchecked((int)0xFFFFFFFF)));
            Assert.AreEqual(10, WeaponModTinkerSet.ReservedImbueSlots(unchecked((int)0x80000000) | 0x40000000 | 0x20000000 | 0xFF));

            // the case an unclamped popcount gets wrong: 32 set bits would drive the tinker count to -22
            Assert.AreEqual(0, WeaponModTinkerSet.ComputeTinkerCount(WeaponModTinkerSet.ReservedImbueSlots(unchecked((int)0xFFFFFFFF)), 0));
            Assert.AreEqual(0, WeaponModTinkerSet.AvailableSlots(WeaponModTinkerSet.ReservedImbueSlots(unchecked((int)0xFFFFFFFF))));

            // and the raw arithmetic is defended even when handed an out-of-range count directly
            Assert.AreEqual(0, WeaponModTinkerSet.ComputeTinkerCount(50, 0));
            Assert.AreEqual(10, WeaponModTinkerSet.ComputeTinkerCount(-5, 0));
        }

        /// <summary>
        /// Three specials is a PERMANENT per-weapon bound, enforced as a literal clamp rather than left implied
        /// by the odds table - a tuning pass that raises weapon_mod_special_chance_3 to 1.0 must not be able to
        /// produce a fourth.
        /// </summary>
        [TestMethod]
        public void Slots_SpecialCountNeverExceedsThreeEvenWithTheOddsForcedToCertainty()
        {
            var prior1 = PropertyManager.GetDouble("weapon_mod_special_chance_1").Item;
            var prior2 = PropertyManager.GetDouble("weapon_mod_special_chance_2").Item;
            var prior3 = PropertyManager.GetDouble("weapon_mod_special_chance_3").Item;

            try
            {
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", 1.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", 1.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", 1.0);

                for (var i = 0; i < 500; i++)
                {
                    var rolled = WeaponModRoller.RollSpecialCount();

                    Assert.AreEqual(3, rolled, "with all three odds at 1.0 the raw roll is always three");

                    for (var reserved = 0; reserved <= WeaponModRegistry.TotalSlots; reserved++)
                    {
                        var clamped = WeaponModRoller.ClampSpecialCount(rolled, reserved);

                        Assert.IsTrue(clamped <= WeaponModRegistry.MaxSpecials, $"reserved {reserved}: {clamped} specials, above the permanent bound of 3");
                        Assert.IsTrue(clamped <= WeaponModRegistry.TotalSlots - reserved, $"reserved {reserved}: {clamped} specials overruns the budget");
                        Assert.IsTrue(clamped >= 0);
                    }
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", prior2);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", prior3);
            }
        }

        // ---------------- cumulative odds ----------------

        /// <summary>
        /// The three tunables are CUMULATIVE - P(at least one), P(at least two), P(at least three) - and the
        /// defaults are specified to give none 65%, one 25%, two 8%, three 2%. Asserted over a deterministic
        /// sweep of the pure resolution function rather than by sampling, so it cannot flake.
        /// </summary>
        [TestMethod]
        public void Odds_CumulativeTunablesProduceTheIntendedSplit()
        {
            const int samples = 1000000;

            var counts = new int[4];

            for (var i = 0; i < samples; i++)
            {
                var roll = (i + 0.5) / samples;

                counts[WeaponModRoller.ResolveSpecialCount(roll, 0.35, 0.10, 0.02)]++;
            }

            Assert.AreEqual(0.65, counts[0] / (double)samples, 1e-4, "P(no special)");
            Assert.AreEqual(0.25, counts[1] / (double)samples, 1e-4, "P(exactly one)");
            Assert.AreEqual(0.08, counts[2] / (double)samples, 1e-4, "P(exactly two)");
            Assert.AreEqual(0.02, counts[3] / (double)samples, 1e-4, "P(exactly three)");
        }

        [TestMethod]
        public void Odds_NonMonotonicTunablesAreClampedNotThrown()
        {
            // ascending instead of descending: every band collapses onto the smallest, and nothing throws
            var (one, two, three) = WeaponModRoller.SanitizeChances(0.10, 0.50, 0.90);

            Assert.AreEqual(0.10, one, 1e-12);
            Assert.AreEqual(0.10, two, 1e-12);
            Assert.AreEqual(0.10, three, 1e-12);

            // out of range in both directions
            var (lo, mid, hi) = WeaponModRoller.SanitizeChances(5.0, -1.0, double.NaN);

            Assert.AreEqual(1.0, lo, 1e-12);
            Assert.AreEqual(0.0, mid, 1e-12);
            Assert.AreEqual(0.0, hi, 1e-12);

            // and the resolved count is still a legal, non-negative band under a broken set
            for (var i = 0; i <= 100; i++)
            {
                var count = WeaponModRoller.ResolveSpecialCount(i / 100.0, 0.10, 0.50, 0.90);

                Assert.IsTrue(count >= 0 && count <= 3, $"roll {i / 100.0} produced a count of {count}");
            }
        }

        // ---------------- distinctness ----------------

        [TestMethod]
        public void Specials_ARolledSetNeverRepeatsAnEntry()
        {
            foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
            {
                for (var i = 0; i < 2000; i++)
                {
                    var rolled = WeaponModRoller.RollDistinctSpecials(weaponClass, WeaponModRegistry.MaxSpecials);

                    Assert.AreEqual(WeaponModRegistry.MaxSpecials, rolled.Count, $"{weaponClass}: pool ran short");
                    Assert.AreEqual(rolled.Count, rolled.Select(d => d.Id).Distinct().Count(), $"{weaponClass}: duplicate special in one set");

                    foreach (var definition in rolled)
                        Assert.IsTrue(definition.AppliesTo(weaponClass), $"{definition.Id} is not a {weaponClass} modifier");
                }
            }

            // an exhausted pool yields nothing rather than repeating
            var all = WeaponModRegistry.Pool(WeaponClass.Caster).Select(d => d.Id).ToList();
            Assert.IsNull(WeaponModRoller.RollSpecial(WeaponClass.Caster, all));
        }

        [TestMethod]
        public void Tinkers_AreDrawnUniformlyWithReplacementFromTheClassPool()
        {
            foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
            {
                var seen = new HashSet<MaterialType>();

                for (var i = 0; i < 2000; i++)
                {
                    var rolled = WeaponModRoller.RollTinkers(weaponClass, WeaponModRegistry.TotalSlots);

                    Assert.AreEqual(WeaponModRegistry.TotalSlots, rolled.Count);

                    foreach (var material in rolled)
                    {
                        Assert.IsTrue(WeaponTinkerTable.TryGet(material, out var definition));
                        Assert.IsTrue(definition.AppliesTo(weaponClass), $"{material} is not a {weaponClass} tinker");

                        seen.Add(material);
                    }
                }

                Assert.AreEqual(WeaponTinkerTable.Pool(weaponClass).Count, seen.Count, $"{weaponClass}: the draw did not cover the whole pool");
            }
        }

        // ---------------- layer 1 reversal ----------------

        /// <summary>
        /// Apply then reverse must return every layer 1 property to its exact starting value, and an ABSENT
        /// property must come back absent - the row is removed, not left sitting at the engine default.
        /// </summary>
        [TestMethod]
        public void Reversal_LayerOneRoundTripsFromEveryStartingState()
        {
            foreach (var material in WeaponTinkerTable.AllMaterials)
            {
                for (var n = 1; n <= WeaponModRegistry.TotalSlots; n++)
                {
                    // the ABSENT case: apply writes a value, reverse removes the row entirely
                    var applied = material.ApplyValue(null, n);
                    var reversed = material.ReverseValue(applied, n);

                    Assert.IsNull(reversed, $"{material.Material} x{n}: reversing from absent must REMOVE the row, not write {reversed}");

                    // a present starting value comes back exactly
                    foreach (var start in new[] { material.EngineDefault + 0.5, material.EngineDefault + 3.0, material.EngineDefault + 17.0 })
                    {
                        var up = material.ApplyValue(start, n);
                        var down = material.ReverseValue(up, n);

                        Assert.IsNotNull(down, $"{material.Material} x{n} from {start}: a value above the default must not be removed");
                        Assert.AreEqual(start, down.Value, 1e-9, $"{material.Material} x{n}: round trip from {start} landed on {down.Value}");
                    }
                }
            }
        }

        /// <summary>
        /// The multiply case (Granite) has to round trip without float drift accumulating: the power is taken
        /// once through Math.Pow rather than n times in a loop.
        /// </summary>
        [TestMethod]
        public void Reversal_TheMultiplyCaseRoundTripsForOneThroughTen()
        {
            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.Granite, out var granite));
            Assert.AreEqual(WeaponTinkerOp.Multiply, granite.Op);

            for (var n = 1; n <= WeaponModRegistry.TotalSlots; n++)
            {
                foreach (var start in new[] { 0.1, 0.25, 0.5, 0.9, 7.5 })
                {
                    var applied = granite.ApplyValue(start, n);

                    Assert.AreEqual(start * Math.Pow(0.8, n), applied, 1e-12, $"Granite x{n}: apply from {start}");

                    var reversed = granite.ReverseValue(applied, n);

                    Assert.IsNotNull(reversed, $"Granite x{n} from {start}: must not be removed");
                    Assert.AreEqual(start, reversed.Value, 1e-9, $"Granite x{n}: round trip from {start}");
                }
            }
        }

        /// <summary>
        /// CORRECTED. This test previously asserted that ANY material with a multiplier-floor default removes a
        /// below-default result, on the premise that a below-default value could only be the retail "set 0.01"
        /// artifact. That premise is false: 11 weapon weenies ship DamageMod below 1.0 and 26 ship WeaponDefense
        /// below 1.0. The rule is now per-material and keyed on whether the material's LIVE DAT mutation script
        /// actually carries the set branch - see WeaponTinkerMaterial.RecoverSetBranch. Only Green Garnet
        /// qualifies among the seven in the table.
        /// </summary>
        [TestMethod]
        public void Reversal_AValueBelowTheEngineDefaultIsRemovedOnlyForSetBranchMaterials()
        {
            // Green Garnet's script IS "ElementalDamageMod (>= 0.01 ? add : set) 0.01", so 0.01 on a property
            // whose absent value reads 1.0 really is the artifact, and absent is the correct restore
            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.GreenGarnet, out var greenGarnet));
            Assert.IsTrue(greenGarnet.RecoverSetBranch, "Green Garnet's script carries the set branch");
            Assert.IsNull(greenGarnet.ReverseValue(0.01, 1), "a set-branch artifact must be removed, not driven negative");

            // every other material in the table is a plain "+=" / "*=", so a below-default value is real data and
            // must survive the reversal rather than being deleted
            foreach (var material in WeaponTinkerTable.AllMaterials.Where(m => !m.RecoverSetBranch && m.EngineDefault > 0.0))
            {
                var reversed = material.ReverseValue(0.5, 1);

                Assert.IsNotNull(reversed, $"{material.Material}: a legitimately below-default value must NOT be removed");
                Assert.AreEqual(0.5 - material.Delta, reversed.Value, 1e-9, $"{material.Material}: plain nominal subtraction");
            }

            // Damage and DamageVariance have an engine default of 0, and a result that lands exactly on the
            // default still restores absent
            Assert.IsTrue(WeaponTinkerTable.TryGet(MaterialType.Iron, out var iron));
            Assert.IsFalse(iron.RecoverSetBranch, "Iron's script is a plain 'Damage += 1'");
            Assert.IsNull(iron.ReverseValue(2.0, 5), "Iron reversing past 0 must not write a negative Damage");
            Assert.IsNull(iron.ReverseValue(5.0, 5), "Iron reversing exactly to 0 restores absent");
            Assert.AreEqual(3.0, iron.ReverseValue(8.0, 5).Value, 1e-9);
        }

        /// <summary>The same round trip, driven through a real (database-free) item rather than over plain doubles.</summary>
        [TestMethod]
        public void Reversal_LayerOneRoundTripsOnARealItem()
        {
            var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.Damage, 12 } },
                new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } });

            var set = new List<MaterialType>
            {
                MaterialType.Iron, MaterialType.Iron, MaterialType.Iron,
                MaterialType.Granite, MaterialType.Granite,
                MaterialType.Brass, MaterialType.Velvet,
            };

            WeaponModTinkerSet.ApplyTinkers(weapon, set);

            Assert.AreEqual(15, weapon.GetProperty(PropertyInt.Damage));
            Assert.AreEqual(0.4 * 0.64, weapon.GetProperty(PropertyFloat.DamageVariance).Value, 1e-12);
            Assert.AreEqual(1.01, weapon.GetProperty(PropertyFloat.WeaponDefense).Value, 1e-12);
            Assert.AreEqual(1.01, weapon.GetProperty(PropertyFloat.WeaponOffense).Value, 1e-12);

            WeaponModTinkerSet.ReverseTinkers(weapon, set);

            Assert.AreEqual(12, weapon.GetProperty(PropertyInt.Damage));
            Assert.AreEqual(0.4, weapon.GetProperty(PropertyFloat.DamageVariance).Value, 1e-9);

            // both started absent, so both must come back absent rather than sitting at 1.0
            Assert.IsNull(weapon.GetProperty(PropertyFloat.WeaponDefense), "WeaponDefense must be REMOVED, not left at the engine default");
            Assert.IsNull(weapon.GetProperty(PropertyFloat.WeaponOffense), "WeaponOffense must be REMOVED, not left at the engine default");
        }

        // ---------------- special reversal ----------------

        [TestMethod]
        public void Reversal_SpecialsRestoreTheNativePropertyExactlyAndClearTheRecord()
        {
            foreach (var definition in WeaponModRegistry.AllMods)
            {
                var weapon = MakeWeapon();

                var before = definition.ReadNative(weapon);
                var magnitude = WeaponModValue.Resolve(definition, 1.0, 10.0, 1.0);

                WeaponModTinkerSet.ApplySpecial(weapon, definition, magnitude);

                Assert.IsNotNull(weapon.GetProperty(definition.Record), $"{definition.Id}: the record was not written");
                Assert.AreEqual(magnitude, weapon.GetProperty(definition.Record).Value, 1e-12,
                    $"{definition.Id}: the record must hold the APPLIED MAGNITUDE, not a potency");

                WeaponModTinkerSet.ReverseSpecial(weapon, definition);

                if (definition.RemoveOnDefault)
                {
                    Assert.AreEqual(before, definition.ReadNative(weapon), $"{definition.Id}: the native property was not restored");
                }
                else
                {
                    // CORRECTED for the Swift Flight fix. A definition with RemoveOnDefault == false never
                    // deletes its native row, because MaximumVelocity's read sites disagree about what absent
                    // means (1.0 at WeaponProfile.cs:57, 20.0 at Creature_Missile.cs:517) and five weenies carry
                    // it at exactly 20.0 - deleting theirs made the appraisal panel report velocity 1.0. On a
                    // weapon that started ABSENT the reversal therefore leaves the row at the engine default,
                    // which reads identically in combat. That is the accepted cost, and it is defensive only:
                    // every missile launcher in practice ships with MaximumVelocity already set.
                    Assert.IsNull(before, $"{definition.Id}: precondition - this arm covers the absent case");
                    Assert.AreEqual(definition.NativeDefault, definition.ReadNative(weapon).Value, 1e-9,
                        $"{definition.Id}: the native property must be restored to the default, not removed");
                }

                // cleared with RemoveProperty, never SetProperty(0) - a zeroed row is a dead biota row that
                // accumulates one per modifier ever rolled
                Assert.IsNull(weapon.GetProperty(definition.Record), $"{definition.Id}: the record must be REMOVED, not zeroed");
            }
        }

        /// <summary>
        /// The whole reason the record stores an applied magnitude instead of a potency: retuning
        /// weapon_mod_magnitude_scale between application and reversal must not make the subtraction wrong.
        /// </summary>
        [TestMethod]
        public void Reversal_SurvivesAMagnitudeScaleChangeBetweenApplicationAndReversal()
        {
            var prior = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            try
            {
                var definition = WeaponModRegistry.Get(WeaponModId.Devastation);

                // a weapon that already carries a loot-generated value on the same native property
                var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                    new Dictionary<PropertyInt, int> { { PropertyInt.GearCritDamage, 7 } });

                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);

                var magnitude = WeaponModValue.Roll(definition, 10.0f);

                WeaponModTinkerSet.ApplySpecial(weapon, definition, magnitude);

                Assert.AreEqual(7 + (int)magnitude, weapon.GetProperty(PropertyInt.GearCritDamage));

                // an operator dials the whole layer up between the two halves
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 2.5);

                WeaponModTinkerSet.ReverseSpecial(weapon, definition);

                Assert.AreEqual(7, weapon.GetProperty(PropertyInt.GearCritDamage), "the loot-generated value must survive the scale change intact");
                Assert.IsNull(weapon.GetProperty(definition.Record));
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", prior);
            }
        }

        /// <summary>
        /// Cleaving stores TOTAL targets including the primary, so a non-cleaving weapon behaves as 1 rather
        /// than 0 and +1 has to land on 2 to be worth one extra target. Reversing back to 1 must remove the row,
        /// which restores IsCleaving == false.
        /// </summary>
        [TestMethod]
        public void Cleave_AppliesAgainstTheTotalTargetCountAndRestoresNonCleaving()
        {
            var definition = WeaponModRegistry.Get(WeaponModId.Cleave);

            Assert.IsTrue(definition.Binary, "Cleave has no magnitude axis - it is always exactly +1");
            Assert.AreEqual(1.0, WeaponModValue.Resolve(definition, 0.25, 1.0, 0.1), 1e-12, "a binary modifier ignores potency, workmanship and scale");

            var weapon = MakeWeapon();

            WeaponModTinkerSet.ApplySpecial(weapon, definition, WeaponModValue.Resolve(definition, 1.0, 10.0, 1.0));

            Assert.AreEqual(2, weapon.GetProperty(PropertyInt.Cleaving), "a non-cleaving weapon must land on 2 (one extra target), not 1");

            WeaponModTinkerSet.ReverseSpecial(weapon, definition);

            Assert.IsNull(weapon.GetProperty(PropertyInt.Cleaving), "the weapon must go back to not cleaving at all");

            // an already-cleaving weapon keeps its own value
            var cleaver = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, new Dictionary<PropertyInt, int> { { PropertyInt.Cleaving, 3 } });

            WeaponModTinkerSet.ApplySpecial(cleaver, definition, 1.0);
            Assert.AreEqual(4, cleaver.GetProperty(PropertyInt.Cleaving));

            WeaponModTinkerSet.ReverseSpecial(cleaver, definition);
            Assert.AreEqual(3, cleaver.GetProperty(PropertyInt.Cleaving));
        }

        // ---------------- magnitude ----------------

        [TestMethod]
        public void Magnitude_ScalesWithWorkmanshipAndNeverRoundsAnIntegerNativeToZero()
        {
            // The float-valued native carries no rounding, so the workmanship term is exactly linear there.
            // Kept as the linearity demonstration on purpose: it used to be Devastation, whose MaxRoll of 50
            // made the rounding step invisible. At the retuned MaxRoll of 5 the rounding binds hard, so a test
            // that showed linearity through an integer native would now be showing the rounding instead.
            var bypass = WeaponModRegistry.Get(WeaponModId.ShieldBypass);

            Assert.AreEqual(0.50, WeaponModValue.Resolve(bypass, 1.0, 10.0, 1.0), 1e-12, "perfect roll on a workmanship 10 weapon");
            Assert.AreEqual(0.05, WeaponModValue.Resolve(bypass, 1.0, 1.0, 1.0), 1e-12, "perfect roll on a workmanship 1 weapon");
            Assert.AreEqual(0.25, WeaponModValue.Resolve(bypass, 1.0, 10.0, 0.5), 1e-12, "the scale tunable halves it");

            // ... and the integer native, at its retuned MaxRoll of 6, as the EXACT composition of the linear
            // value, the away-from-zero rounding and the floor-at-1 rule - not as a tolerance around linearity.
            var devastation = WeaponModRegistry.Get(WeaponModId.Devastation);

            Assert.AreEqual(6.0, devastation.MaxRoll, 1e-12, "Devastation was retuned from 50 to 5, then from 5 to 6, on 2026-07-30");
            Assert.AreEqual(6.0, WeaponModValue.Resolve(devastation, 1.0, 10.0, 1.0), 1e-12, "perfect roll on a workmanship 10 weapon");
            Assert.AreEqual(1.0, WeaponModValue.Resolve(devastation, 1.0, 1.0, 1.0), 1e-12, "workmanship 1: a linear 0.6, rounded away from zero");
            Assert.AreEqual(3.0, WeaponModValue.Resolve(devastation, 1.0, 10.0, 0.5), 1e-12, "the scale tunable halves it to a linear 3.0, which rounds away from zero to 3");

            // the floor: any nonzero unrounded value becomes at least 1, so a rolled special is never a no-op
            foreach (var definition in WeaponModRegistry.AllMods.Where(d => d.IsInteger && !d.Binary))
            {
                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    var value = WeaponModValue.Resolve(definition, WeaponModDefinition.DefaultMinPotency, workmanship, 1.0);

                    Assert.IsTrue(value >= 1.0, $"{definition.Id} at workmanship {workmanship} and the potency floor resolved to {value}");
                    Assert.AreEqual(Math.Round(value), value, 1e-12, $"{definition.Id}: an integer native must get a whole number");
                }
            }

            // a rolled potency always sits in [MinPotency, 1]
            foreach (var definition in WeaponModRegistry.AllMods)
            {
                var floor = WeaponModRoller.MinPotency(definition);

                for (var i = 0; i < 2000; i++)
                {
                    var potency = WeaponModRoller.RollPotency(definition);

                    Assert.IsTrue(potency >= floor - 1e-12 && potency <= 1.0, $"{definition.Id}: rolled potency {potency} outside [{floor}, 1]");
                }
            }
        }

        // ---------------- logs ----------------

        [TestMethod]
        public void Log_ParsesAndSerializesTheRetailFormat()
        {
            Assert.IsTrue(WeaponModTinkerSet.TryParseLog("61,61,57,7", out var parsed));
            CollectionAssert.AreEqual(
                new[] { MaterialType.Iron, MaterialType.Iron, MaterialType.Brass, MaterialType.Velvet },
                parsed);

            Assert.AreEqual("61,61,57,7", WeaponModTinkerSet.SerializeLog(parsed));

            Assert.IsTrue(WeaponModTinkerSet.TryParseLog(null, out var empty));
            Assert.AreEqual(0, empty.Count);
            Assert.IsNull(WeaponModTinkerSet.SerializeLog(empty), "an empty composition removes the row rather than writing an empty string");

            // HandleTinkerLog falls back to the source's WeenieClassId when it has no MaterialType, so a
            // well-formed number that is not a defined material still parses - it is simply not one this system
            // knows how to reverse
            Assert.IsTrue(WeaponModTinkerSet.TryParseLog("61,21082", out var withWcid));
            Assert.AreEqual(2, withWcid.Count);
            Assert.AreEqual(1, WeaponModTinkerSet.KnownOnly(withWcid).Count);

            // genuine garbage fails, which is what the integrity gate refuses on
            Assert.IsFalse(WeaponModTinkerSet.TryParseLog("61,,57", out _));
            Assert.IsFalse(WeaponModTinkerSet.TryParseLog("iron,brass", out _));
        }

        [TestMethod]
        public void Log_IntegrityGateRefusesAMismatchAndIsSuppressedOnAManagedWeapon()
        {
            // a clean untinkered weapon: no log, no count
            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(MakeWeapon()));

            var consistent = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 3 } });
            consistent.SetProperty(PropertyString.TinkerLog, "61,61,57");
            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(consistent));

            // the retail-era shape the gate exists for: a tinker count with a short or missing log
            var mismatched = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 } });
            mismatched.SetProperty(PropertyString.TinkerLog, "61,61");
            Assert.IsFalse(WeaponModTinkerSet.PassesIntegrityGate(mismatched));

            var noLog = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, new Dictionary<PropertyInt, int> { { PropertyInt.NumTimesTinkered, 10 } });
            Assert.IsFalse(WeaponModTinkerSet.PassesIntegrityGate(noLog));

            // ... and it is suppressed once the weapon is ours, because our own weapons carry specials in part
            // of the budget and would otherwise trip it on every use
            mismatched.SetProperty(PropertyInt.WeaponModTinkerCount, 7);
            Assert.IsTrue(WeaponModTinkerSet.IsManaged(mismatched));
            Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(mismatched));
            Assert.AreEqual(PropertyString.WeaponModTinkerLog, WeaponModTinkerSet.ReversalLog(mismatched));
        }

        /// <summary>
        /// LOGS ARE REPLACED, NEVER APPENDED. The obvious implementation - RecipeManager.HandleTinkerLog, which
        /// does "TinkerLog += ..." - would leave 20 entries after a second reroll, which bloats the row without
        /// bound, refuses every subsequent reroll through the integrity gate, and makes reversal subtract
        /// tinkers that were already removed.
        /// </summary>
        [TestMethod]
        public void Log_ASecondRerollLeavesBothLogsAtTenEntriesNotTwenty()
        {
            var prior1 = PropertyManager.GetDouble("weapon_mod_special_chance_1").Item;
            var prior2 = PropertyManager.GetDouble("weapon_mod_special_chance_2").Item;
            var prior3 = PropertyManager.GetDouble("weapon_mod_special_chance_3").Item;

            try
            {
                // force zero specials so the whole budget is layer 1 and the count is deterministic
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", 0.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", 0.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", 0.0);

                var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                    new Dictionary<PropertyInt, int> { { PropertyInt.Damage, 12 } },
                    new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } });

                for (var pass = 1; pass <= 3; pass++)
                {
                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"pass {pass} produced no result");

                    Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.TinkerLog), out var retail));
                    Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.WeaponModTinkerLog), out var own));

                    Assert.AreEqual(10, retail.Count, $"pass {pass}: TinkerLog was APPENDED to rather than replaced");
                    Assert.AreEqual(10, own.Count, $"pass {pass}: WeaponModTinkerLog was APPENDED to rather than replaced");

                    Assert.AreEqual(10, weapon.GetProperty(PropertyInt.NumTimesTinkered), "NumTimesTinkered is SET to 10, never incremented");
                    Assert.AreEqual(10, weapon.GetProperty(PropertyInt.WeaponModTinkerCount));
                    Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon));
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", prior2);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", prior3);
            }
        }

        /// <summary>
        /// A full reroll on a weapon carrying imbues: the imbue bits keep their slots, NumTimesTinkered lands on
        /// exactly 10, and specials plus tinkers fill the rest. Run many times so the RNG's whole range of
        /// special counts is exercised.
        /// </summary>
        [TestMethod]
        public void Reroll_AlwaysFillsExactlyTenSlotsAndNeverExceedsTheTinkerCap()
        {
            // ImbuedEffectType.CriticalStrike | ImbuedEffectType.ArmorRending: two bits, two reserved slots
            const int twoImbues = 0x1 | 0x40;

            var reserved = WeaponModTinkerSet.ReservedImbueSlots(twoImbues);
            Assert.AreEqual(2, reserved);

            for (var i = 0; i < 300; i++)
            {
                var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                    new Dictionary<PropertyInt, int> { { PropertyInt.Damage, 12 }, { PropertyInt.ImbuedEffect, twoImbues } },
                    new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } });

                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0));

                var specials = WeaponModTinkerSet.SpecialCount(weapon);
                var tinkers = weapon.GetProperty(PropertyInt.WeaponModTinkerCount) ?? -1;

                Assert.IsTrue(specials <= WeaponModRegistry.MaxSpecials, $"{specials} specials, above the permanent bound of 3");
                Assert.AreEqual(WeaponModRegistry.TotalSlots, reserved + specials + tinkers, "the budget does not add to 10");

                // NumTimesTinkered must never exceed 10: TinkeringDifficulty is an unguarded 10-element list
                // indexed directly by it, so 11 is a crash on any ungated recipe path
                Assert.AreEqual(10, weapon.GetProperty(PropertyInt.NumTimesTinkered));

                // imbues and slayer are untouched
                Assert.AreEqual(twoImbues, weapon.GetProperty(PropertyInt.ImbuedEffect));
            }
        }

        /// <summary>
        /// The swap trades exactly one slot for exactly one modifier: the budget stays at ten, NumTimesTinkered
        /// stays at ten, and the special count never passes three.
        /// </summary>
        [TestMethod]
        public void Swap_TradesOneSlotAndKeepsTheBudgetAtTen()
        {
            for (var i = 0; i < 500; i++)
            {
                var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                    new Dictionary<PropertyInt, int> { { PropertyInt.Damage, 12 }, { PropertyInt.NumTimesTinkered, 10 } },
                    new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } });

                // a hand-tinkered ten-Iron weapon, the headline case
                var iron = Enumerable.Repeat(MaterialType.Iron, 10).ToList();
                WeaponModTinkerSet.ApplyTinkers(weapon, iron);
                weapon.SetProperty(PropertyString.TinkerLog, WeaponModTinkerSet.SerializeLog(iron));

                for (var use = 0; use < 6; use++)
                {
                    if (WeaponModTinkerSet.SpecialCount(weapon) >= WeaponModRegistry.MaxSpecials)
                        break;

                    Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), $"use {use} produced no result");

                    var specials = WeaponModTinkerSet.SpecialCount(weapon);
                    var tinkers = weapon.GetProperty(PropertyInt.WeaponModTinkerCount) ?? -1;

                    Assert.IsTrue(specials <= WeaponModRegistry.MaxSpecials, $"use {use}: {specials} specials");
                    Assert.AreEqual(WeaponModRegistry.TotalSlots, specials + tinkers, $"use {use}: the budget does not add to 10");
                    Assert.AreEqual(10, weapon.GetProperty(PropertyInt.NumTimesTinkered), $"use {use}: NumTimesTinkered moved off 10");

                    Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.WeaponModTinkerLog), out var own));
                    Assert.AreEqual(tinkers, own.Count, $"use {use}: the log and the tinker count disagree");
                }
            }
        }

        // ---------------- the confirmation removal (2026-07-30) ----------------

        /// <summary>
        /// This system raises NO server-side confirmation of its own. The client fires its generic tinkering
        /// prompt before the server is ever contacted, so a server dialog was a second panel for one gesture;
        /// it was removed on a repo-owner directive from live play, and the explanation it carried moved onto
        /// the two salvage bags' LongDesc (Content/sql/patches/weapon_mods_salvage_bag_description.sql).
        ///
        /// Asserted by REFLECTION rather than by driving the flow, because UseObjectOnTarget needs a Player with
        /// a session, inventory and motion table and is unreachable from a unit test either way. What reflection
        /// CAN prove is the shape: no confirmed flag to re-enter through, and no Confirmation subclass to
        /// re-enter from. A future pass that restores either one fails here.
        /// </summary>
        [TestMethod]
        public void Confirmation_UseObjectOnTargetCarriesNoConfirmedFlag()
        {
            var overloads = typeof(WeaponModManager)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == nameof(WeaponModManager.UseObjectOnTarget))
                .ToList();

            Assert.AreEqual(1, overloads.Count, "UseObjectOnTarget should have exactly one shape");

            var parameters = overloads[0].GetParameters();

            CollectionAssert.AreEqual(
                new[] { typeof(Player), typeof(WorldObject), typeof(WorldObject) },
                parameters.Select(p => p.ParameterType).ToArray(),
                "UseObjectOnTarget takes the player, the salvage bag and the weapon and nothing else - a fourth "
                + "parameter means a confirmation re-entry flag came back");

            Assert.IsFalse(parameters.Any(p => p.ParameterType == typeof(bool)),
                "no bool parameter: the confirmed flag was removed with the dialog");
        }

        /// <summary>Companion to the above: nothing in this system is a <see cref="Confirmation"/> any more.</summary>
        [TestMethod]
        public void Confirmation_NoConfirmationSubclassRemainsInTheWeaponModNamespace()
        {
            var confirmations = typeof(WeaponModManager).Assembly
                .GetTypes()
                .Where(t => typeof(ACE.Server.Entity.Confirmation).IsAssignableFrom(t))
                .Where(t => (t.Namespace ?? string.Empty).StartsWith("ACE.Server.WeaponMods", StringComparison.Ordinal)
                    || (t.DeclaringType != null && t.DeclaringType == typeof(WeaponModManager)))
                .Select(t => t.FullName)
                .ToList();

            Assert.AreEqual(0, confirmations.Count,
                "the weapon-mod system declares no Confirmation subclass; found: " + string.Join(", ", confirmations));
        }
    }
}
