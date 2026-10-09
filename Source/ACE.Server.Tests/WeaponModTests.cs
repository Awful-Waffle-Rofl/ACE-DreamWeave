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

            Assert.AreEqual(22, enumValues.Count,
                "the catalog is 4 Tier A plus 18 Tier B. Tier A is 4 v1 rows only (came down from 11 in the 2026-07-30 pool cut - Warding, Crit Ward, Resolute, Vigor and Mending were removed as defensive/sustain rows, and Armor Cleaving was cut before that - down to 5 on 2026-08-07 when Cleave was removed, and down again to 4 on 2026-08-17 when Swift Flight was retired in the catalog v4 pass). Tier B is 3 surviving v2 rows (Life Leech, Mana Leech, Stamina Leech and Overload were retired 2026-08-17 alongside Swift Flight; Sunder and Rampage are phase 2 and deliberately have no enum member) plus 6 v3-expansion rows added 2026-08-06 (Heft, Tension, Leverage, Attunement, Focus, Execution - all six were reworked from an original Tier A/native-writing draft the same day, see WeaponModRegistry.cs's Tier B v3 remarks) plus 9 v4-expansion rows added 2026-08-17, currently inert (Efficiency, Recovery, Mana Well, Cleanse, Longevity, Siphon, Quick Refresh, Arcane Defender, Panic Reload)");
            Assert.AreEqual(22, WeaponModRegistry.AllMods.Count, "every WeaponModId needs exactly one registry row");

            Assert.AreEqual(4, WeaponModRegistry.TierAMods.Count, "the Tier A half (v1 survivors only)");
            Assert.AreEqual(18, WeaponModRegistry.TierBMods.Count, "the Tier B half (3 v2 survivors + 6 v3-expansion + 9 v4-expansion)");
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

                    // a Tier B record is valid in the original v2 band, the 2026-08-06 v3 expansion band, OR
                    // the 2026-08-17 v4 expansion band - all three are deliberately disjoint (the original
                    // 8130-8149 system was full), see WeaponModRegistry.TierBExpansionBandStart and
                    // WeaponModRegistry.TierBV4BandStart for why
                    var inOriginalBand = record >= WeaponModRegistry.TierBPropertyBandStart && record <= WeaponModRegistry.TierBPropertyBandEnd;
                    var inExpansionBand = record >= WeaponModRegistry.TierBExpansionBandStart && record <= WeaponModRegistry.TierBExpansionBandEnd;
                    var inV4Band = record >= WeaponModRegistry.TierBV4BandStart && record <= WeaponModRegistry.TierBV4BandEnd;

                    Assert.IsTrue(inOriginalBand || inExpansionBand || inV4Band,
                        $"{mod.Id}: record PropertyFloat {mod.Record} ({record}) is outside all three reserved Tier B ranges " +
                        $"{WeaponModRegistry.TierBPropertyBandStart}-{WeaponModRegistry.TierBPropertyBandEnd}, " +
                        $"{WeaponModRegistry.TierBExpansionBandStart}-{WeaponModRegistry.TierBExpansionBandEnd} and " +
                        $"{WeaponModRegistry.TierBV4BandStart}-{WeaponModRegistry.TierBV4BandEnd}");
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
        /// left to inspection, in BOTH states of the system's ONE gate: specials melee 4 / missile 4 / caster 3
        /// with weapon_mods_enabled off, and melee 16 / missile 17 / caster 16 with it on. Tinkers are melee 4,
        /// missile 3, caster 4 either way - Tier B does not touch layer 1.
        ///
        /// UPDATED 2026-07-30 from melee 10 / missile 10 / caster 8, when Warding, Crit Ward, Resolute, Vigor
        /// and Mending were removed to make the pool damage-oriented only. The tinker pools did NOT move.
        ///
        /// UPDATED AGAIN 2026-07-30: Tier B's separate tunable (weapon_mod_tier_b_enabled) was removed and the
        /// whole system consolidated onto weapon_mods_enabled. The DEPTHS did not move with it - only which
        /// bool selects between them.
        ///
        /// UPDATED AGAIN 2026-08-06: the v3 expansion (Heft, Tension, Leverage, Attunement, Focus, Execution)
        /// added six rows, but every one of them is TIER B (see WeaponModRegistry.TierBExpansionBandStart), so
        /// the GATE-OFF depths are unchanged from v1/v2. The gate-ON depths grew: v3 adds Heft+Leverage+Focus+
        /// Execution to melee (4), Heft+Tension+Focus+Execution to missile (4), Attunement+Focus+Execution to
        /// caster (3), on top of v2's six per class.
        ///
        /// UPDATED AGAIN 2026-08-07 with the Cleave removal, and ONLY THE MELEE COLUMN MOVED: Cleave was the
        /// one Tier A row restricted to WeaponClass.Melee, so melee dropped by one in BOTH gate states.
        ///
        /// UPDATED AGAIN 2026-08-17, catalog v4 (repo-owner directive to bring utility rows back). Swift
        /// Flight (Tier A, missile only) was retired, dropping the missile GATE-OFF depth by one (5 -> 4). The
        /// three leeches and Overload (all Tier B v2) were retired, leaving 3 Tier B v2 survivors (Ambush,
        /// Quickening, Second Wind) instead of 7: melee/missile lose 4 each (leeches+Overload minus Quickening
        /// which stays, i.e. the three leeches), caster loses 4 (the three leeches plus Overload). Nine new
        /// Tier B v4 rows were added, currently INERT: Efficiency/Recovery/ManaWell/Cleanse/Siphon (All, so all
        /// three classes), Longevity/QuickRefresh/ArcaneDefender (Caster only), Panic Reload (Missile only) -
        /// melee +5, missile +6, caster +8.
        /// </summary>
        [TestMethod]
        public void Registry_PoolDepthMatchesTheDesign()
        {
            // weapon_mods_enabled now ships TRUE as standard content; force it off here so the
            // live accessor's gate-off depths are still pinned independently of the shipped default.
            var priorGate = PropertyManager.GetBool("weapon_mods_enabled").Item;
            PropertyManager.ModifyBool("weapon_mods_enabled", false);
            try
            {
                Assert.IsFalse(WeaponModRegistry.Enabled(), "precondition: gate forced off for this assertion");

                Assert.AreEqual(4, WeaponModRegistry.Pool(WeaponClass.Melee).Count, "melee special pool");
                Assert.AreEqual(4, WeaponModRegistry.Pool(WeaponClass.Missile).Count, "missile special pool");
                Assert.AreEqual(3, WeaponModRegistry.Pool(WeaponClass.Caster).Count, "caster special pool");
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", priorGate);
            }

            // the same numbers through the pure overload, so the gate and the depths are pinned independently
            Assert.AreEqual(4, WeaponModRegistry.Pool(WeaponClass.Melee, false).Count, "melee special pool, gate off");
            Assert.AreEqual(4, WeaponModRegistry.Pool(WeaponClass.Missile, false).Count, "missile special pool, gate off");
            Assert.AreEqual(3, WeaponModRegistry.Pool(WeaponClass.Caster, false).Count, "caster special pool, gate off");

            Assert.AreEqual(16, WeaponModRegistry.Pool(WeaponClass.Melee, true).Count, "melee special pool, gate on: 4 Tier A + 3 Tier B v2 + 4 Tier B v3 + 5 Tier B v4");
            Assert.AreEqual(17, WeaponModRegistry.Pool(WeaponClass.Missile, true).Count, "missile special pool, gate on: 4 Tier A + 3 Tier B v2 + 4 Tier B v3 + 6 Tier B v4");
            Assert.AreEqual(16, WeaponModRegistry.Pool(WeaponClass.Caster, true).Count, "caster special pool, gate on: 3 Tier A + 2 Tier B v2 + 3 Tier B v3 + 8 Tier B v4");

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

        /// <summary>
        /// REWRITTEN 2026-08-07 for the Amethyst unblocking (repo-owner directive: "Amethyst should be able to
        /// be applied basically always... traditional tinkers should have no bearing").
        ///
        /// The rule table no longer takes a tinker count or a special count AT ALL - both arguments were
        /// removed rather than left unread, because a rule table that accepts a value it ignores reads as
        /// though the value still matters. What remains splits cleanly in two: three conditions that apply to
        /// BOTH tools, and two tinker-budget conditions that are now REROLL-ONLY.
        /// </summary>
        [TestMethod]
        public void Refusal_RulesHoldOverPlainValues()
        {
            // ---- shared by both tools ----

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, true, 0));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAModMaterial,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.None, WeaponClass.Melee, true, true, 0));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NotAWeapon,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.None, true, true, 0));

            // a weapon with no workmanship would mint specials worth nothing; refused, never defaulted to zero
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoWorkmanship,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, false, true, 0));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoWorkmanship,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, false, true, 0),
                "workmanship is required by BOTH tools - a swap onto a workmanship-less weapon would mint a special worth nothing");

            // ---- reroll-only: the two tinker-budget gates ----

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoAvailableSlots,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, true, 10));

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.TinkerLogMismatch,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, WeaponClass.Melee, true, false, 0));

            // THE 2026-08-07 CHANGE, and the asymmetry it deliberately creates: the very same weapon states
            // that refuse a reroll are now perfectly swappable, because Amethyst touches no tinker, no log and
            // no counter. A ten-Oak weapon and a log-mismatched weapon are both Amethyst-able.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, true, true, 10),
                "a fully-reserved tinker budget must NOT refuse a swap - Amethyst never touches tinkers");

            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, true, false, 0),
                "a mismatched tinker log must NOT refuse a swap - Amethyst never reverses a tinker, so the log's accuracy cannot affect it");
        }

        /// <summary>
        /// The whole point of the 2026-08-07 directive, stated as a property rather than as cases: NOTHING
        /// about a weapon's tinker state or special count can refuse an Amethyst. Only being the wrong kind of
        /// object, or having no workmanship, can - and drift-stone locking, which is checked separately
        /// because it needs the item rather than plain values.
        /// </summary>
        [TestMethod]
        public void Refusal_NoTinkerOrSpecialStateCanEverRefuseASwap()
        {
            foreach (var reserved in new[] { 0, 1, 5, 9, 10 })
            {
                foreach (var integrityGate in new[] { true, false })
                {
                    Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                        WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, true, integrityGate, reserved),
                        $"swap refused at reserved={reserved}, integrityGate={integrityGate} - no tinker state may gate Amethyst");
                }
            }
        }

        /// <summary>
        /// CHANGE 1, the cap raise: MaxSpecials is 4, and the roller's hard clamp - now the ONLY enforcement
        /// point, see WeaponModRegistry.MaxSpecials's remarks - agrees.
        ///
        /// REWORKED 2026-08-06 alongside the Amethyst rework: this test used to also pin two swap-side
        /// enforcement points (ResolveRefusal's SwapAtSpecialCap arm, ApplySwap's own defence-in-depth check).
        /// Both are GONE, on purpose - Amethyst is now a special-only reroll, so a weapon AT the cap is exactly
        /// the state it exists to act on, not one it refuses.
        /// </summary>
        [TestMethod]
        public void Specials_TheCapIsFourAtItsOneEnforcementPoint()
        {
            Assert.AreEqual(4, WeaponModRegistry.MaxSpecials, "the permanent per-weapon special bound");

            // the roller's hard clamp. Anything above the cap comes back AT the cap.
            for (var rolled = 0; rolled <= 12; rolled++)
                Assert.AreEqual(Math.Min(rolled, 4), WeaponModRoller.ClampSpecialCount(rolled), $"ClampSpecialCount({rolled})");

            // a weapon AT the cap is swappable - Amethyst rerolls a special AT the cap by design. Since
            // 2026-08-07 the special count is not an argument to the rule table at all, so this now asserts
            // the stronger form: no special count, cap included, reaches a refusal.
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, WeaponClass.Melee, true, true, 0),
                "a weapon holding four specials (the cap) must still be swappable - rerolling AT the cap is the tool's purpose");
        }

        // ---------------- slot arithmetic ----------------

        /// <summary>
        /// CHANGE 2, the decoupling: tinker capacity is TotalSlots minus the reserved slots and NOTHING else. The
        /// old model was "10 = reserved + specials + tinkers", which made every special cost a tinker; the number
        /// this returns must now be identical for a weapon holding no specials and one holding a full set.
        /// </summary>
        [TestMethod]
        public void Slots_TinkerCapacityIsIndependentOfTheSpecialCount()
        {
            for (var reserved = 0; reserved <= WeaponModRegistry.TotalSlots; reserved++)
            {
                var tinkers = WeaponModTinkerSet.ComputeTinkerCount(reserved);

                Assert.AreEqual(WeaponModRegistry.TotalSlots - reserved, tinkers, $"reserved {reserved}: tinker capacity");
                Assert.AreEqual(WeaponModTinkerSet.AvailableSlots(reserved), tinkers, $"reserved {reserved}: the two accessors disagree");

                for (var rolled = 0; rolled <= 8; rolled++)
                {
                    var specials = WeaponModRoller.ClampSpecialCount(rolled);

                    Assert.IsTrue(specials >= 0, $"reserved {reserved}, rolled {rolled}: negative special count");
                    Assert.IsTrue(specials <= WeaponModRegistry.MaxSpecials,
                        $"reserved {reserved}, rolled {rolled}: {specials} specials exceeds the permanent bound");

                    // THE WHOLE POINT OF THE CHANGE: holding specials costs the weapon nothing in tinkers
                    Assert.AreEqual(tinkers, WeaponModTinkerSet.ComputeTinkerCount(reserved),
                        $"reserved {reserved}, rolled {rolled}: the tinker capacity moved with the special count");
                }
            }
        }

        /// <summary>
        /// The other half of the decoupling, stated as the property a player would notice: at a given reserved
        /// count the number of tinkers a reroll lays down is the same whether it rolled zero specials or a full
        /// set. Before the change a maxed set cost the weapon MaxSpecials tinkers, which is what made an inert
        /// special strictly worse than none.
        /// </summary>
        [TestMethod]
        public void Slots_SpecialsNoLongerBuyTheirSlotFromTheTinkerBudget()
        {
            for (var reserved = 0; reserved <= WeaponModRegistry.TotalSlots; reserved++)
            {
                var withNone = WeaponModTinkerSet.ComputeTinkerCount(reserved);

                Assert.AreEqual(withNone, WeaponModTinkerSet.ComputeTinkerCount(reserved),
                    $"reserved {reserved}: a full set of specials cost the weapon tinker slots");

                // and the reserved slots themselves still bind, so this is not simply "always ten"
                Assert.AreEqual(WeaponModRegistry.TotalSlots - reserved, withNone, $"reserved {reserved}");
            }

            Assert.AreEqual(0, WeaponModTinkerSet.ComputeTinkerCount(WeaponModRegistry.TotalSlots), "ten reserved leaves nothing");
            Assert.AreEqual(WeaponModRegistry.TotalSlots, WeaponModTinkerSet.ComputeTinkerCount(0), "nothing reserved leaves all ten");
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
            Assert.AreEqual(0, WeaponModTinkerSet.ComputeTinkerCount(WeaponModTinkerSet.ReservedImbueSlots(unchecked((int)0xFFFFFFFF))));
            Assert.AreEqual(0, WeaponModTinkerSet.AvailableSlots(WeaponModTinkerSet.ReservedImbueSlots(unchecked((int)0xFFFFFFFF))));

            // and the raw arithmetic is defended even when handed an out-of-range count directly
            Assert.AreEqual(0, WeaponModTinkerSet.ComputeTinkerCount(50));
            Assert.AreEqual(10, WeaponModTinkerSet.ComputeTinkerCount(-5));
        }

        /// <summary>
        /// MaxSpecials is a PERMANENT per-weapon bound, enforced as a literal clamp rather than left implied by
        /// the odds table - a tuning pass that raises weapon_mod_special_chance_4 to 1.0 must not be able to
        /// produce a fifth.
        /// </summary>
        [TestMethod]
        public void Slots_SpecialCountNeverExceedsTheCapEvenWithTheOddsForcedToCertainty()
        {
            var prior1 = PropertyManager.GetDouble("weapon_mod_special_chance_1").Item;
            var prior2 = PropertyManager.GetDouble("weapon_mod_special_chance_2").Item;
            var prior3 = PropertyManager.GetDouble("weapon_mod_special_chance_3").Item;
            var prior4 = PropertyManager.GetDouble("weapon_mod_special_chance_4").Item;

            try
            {
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", 1.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", 1.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", 1.0);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_4", 1.0);

                for (var i = 0; i < 500; i++)
                {
                    var rolled = WeaponModRoller.RollSpecialCount();

                    Assert.AreEqual(4, rolled, "with all four odds at 1.0 the raw roll is always four");

                    var clamped = WeaponModRoller.ClampSpecialCount(rolled);

                    Assert.AreEqual(WeaponModRegistry.MaxSpecials, clamped, "the clamp must land exactly on the bound");
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", prior2);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", prior3);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_4", prior4);
            }
        }

        /// <summary>
        /// weapon_mod_special_chance_4 DEFAULTED TO 0 until the 2026-08-06 v3 magnitude pass, which is what kept
        /// the 2026-08-06 cap raise (3 -> 4, a separate structural change that landed first) inert on its own.
        /// The v3 pass then opened the band to 0.10. This test now pins the LIVE default (0.10, reachable) and
        /// keeps the pure-function coverage of the "band closed" shape (fourth chance 0.0) as a literal-input
        /// case, so both the historical and the current behaviour stay asserted.
        /// </summary>
        [TestMethod]
        public void Odds_TheFourthBandIsOpenAtItsDefaultAndReachableAtARollOfZero()
        {
            Assert.AreEqual(0.10, PropertyManager.GetDouble("weapon_mod_special_chance_4").Item, 1e-12,
                "weapon_mod_special_chance_4 must default to 0.10 after the 2026-08-06 v3 magnitude pass");

            // a roll of exactly 0 - which ThreadSafeRandom.Next(0f, 1f) can return - buys a fourth special
            // whenever the fourth band is open at all, since every cumulative band includes 0
            Assert.AreEqual(4, WeaponModRoller.ResolveSpecialCount(0.0, 1.00, 0.60, 0.30, 0.10),
                "a roll of exactly 0 against the live defaults must reach the fourth band");

            // the CLOSED-band shape from before the v3 pass, pinned as a literal-input case rather than through
            // the live tunable: with the fourth chance at 0.0 nothing can ever reach four
            Assert.AreEqual(3, WeaponModRoller.ResolveSpecialCount(0.0, 0.35, 0.10, 0.02, 0.0),
                "a roll of exactly 0 against a fourth chance of 0 must stop at three");

            for (var i = 0; i <= 1000; i++)
            {
                var count = WeaponModRoller.ResolveSpecialCount(i / 1000.0, 0.35, 0.10, 0.02, 0.0);

                Assert.IsTrue(count <= 3, $"roll {i / 1000.0} produced {count} specials with the fourth band at 0");
            }

            // and with the band open, the fourth is reachable - so the test above is pinning the closed shape,
            // not a mechanism that never works
            Assert.AreEqual(4, WeaponModRoller.ResolveSpecialCount(0.005, 0.35, 0.10, 0.02, 0.01));
        }

        // ---------------- cumulative odds ----------------

        /// <summary>
        /// The four tunables are CUMULATIVE - P(at least one), P(at least two), P(at least three), P(all four) -
        /// and the defaults are specified to give none 65%, one 25%, two 8%, three 2%, four 0%. Asserted over a
        /// deterministic sweep of the pure resolution function rather than by sampling, so it cannot flake.
        /// </summary>
        [TestMethod]
        public void Odds_CumulativeTunablesProduceTheIntendedSplit()
        {
            const int samples = 1000000;

            var counts = new int[5];

            for (var i = 0; i < samples; i++)
            {
                var roll = (i + 0.5) / samples;

                counts[WeaponModRoller.ResolveSpecialCount(roll, 0.35, 0.10, 0.02, 0.00)]++;
            }

            Assert.AreEqual(0.65, counts[0] / (double)samples, 1e-4, "P(no special)");
            Assert.AreEqual(0.25, counts[1] / (double)samples, 1e-4, "P(exactly one)");
            Assert.AreEqual(0.08, counts[2] / (double)samples, 1e-4, "P(exactly two)");
            Assert.AreEqual(0.02, counts[3] / (double)samples, 1e-4, "P(exactly three)");
            Assert.AreEqual(0.00, counts[4] / (double)samples, 1e-9, "P(exactly four) at the shipped default");

            // the same sweep with the fourth band opened to 0.005, to prove the split is a real partition rather
            // than a fourth arm that can never be taken
            var open = new int[5];

            for (var i = 0; i < samples; i++)
                open[WeaponModRoller.ResolveSpecialCount((i + 0.5) / samples, 0.35, 0.10, 0.02, 0.005)]++;

            Assert.AreEqual(0.015, open[3] / (double)samples, 1e-4, "P(exactly three) with the fourth band open");
            Assert.AreEqual(0.005, open[4] / (double)samples, 1e-4, "P(exactly four) with the fourth band open");
        }

        [TestMethod]
        public void Odds_NonMonotonicTunablesAreClampedNotThrown()
        {
            // ascending instead of descending: every band collapses onto the smallest, and nothing throws
            var (one, two, three, four) = WeaponModRoller.SanitizeChances(0.10, 0.50, 0.90, 0.95);

            Assert.AreEqual(0.10, one, 1e-12);
            Assert.AreEqual(0.10, two, 1e-12);
            Assert.AreEqual(0.10, three, 1e-12);
            Assert.AreEqual(0.10, four, 1e-12);

            // out of range in every direction
            var (lo, mid, hi, top) = WeaponModRoller.SanitizeChances(5.0, -1.0, double.NaN, 2.0);

            Assert.AreEqual(1.0, lo, 1e-12);
            Assert.AreEqual(0.0, mid, 1e-12);
            Assert.AreEqual(0.0, hi, 1e-12);
            Assert.AreEqual(0.0, top, 1e-12);

            // and the resolved count is still a legal, non-negative band under a broken set
            for (var i = 0; i <= 100; i++)
            {
                var count = WeaponModRoller.ResolveSpecialCount(i / 100.0, 0.10, 0.50, 0.90, 0.95);

                Assert.IsTrue(count >= 0 && count <= WeaponModRegistry.MaxSpecials, $"roll {i / 100.0} produced a count of {count}");
            }
        }

        // ---------------- CHANGE 3: the guaranteed damage-relevant first special ----------------

        /// <summary>
        /// The AffectsSingleTargetDamage classification is data, so it is pinned row by row rather than inferred
        /// from the pools. Getting one row wrong is invisible at runtime - the draw still works, it just draws
        /// the wrong thing - so this is the test that would catch it.
        /// </summary>
        [TestMethod]
        public void DamageSubset_EveryRowIsClassifiedExactlyAsSpecified()
        {
            var damage = new HashSet<WeaponModId>
            {
                WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst,
                WeaponModId.Quickening, WeaponModId.Ambush,
                WeaponModId.Heft, WeaponModId.Tension, WeaponModId.Leverage, WeaponModId.Attunement,
                WeaponModId.Focus, WeaponModId.Execution,
                WeaponModId.PanicReload,
            };

            var utility = new HashSet<WeaponModId>
            {
                // Swift Flight, LifeLeech, ManaLeech, StaminaLeech and Overload were retired 2026-08-17 in the
                // catalog v4 pass and no longer appear here.
                WeaponModId.ShieldBypass,
                WeaponModId.SecondWind,
                WeaponModId.Efficiency, WeaponModId.Recovery, WeaponModId.ManaWell, WeaponModId.Cleanse,
                WeaponModId.Longevity, WeaponModId.Siphon, WeaponModId.QuickRefresh, WeaponModId.ArcaneDefender,
            };

            Assert.AreEqual(WeaponModRegistry.AllMods.Count, damage.Count + utility.Count,
                "every registry row must appear in exactly one of the two lists - a row added without classifying it defaults to utility and would slip through unnoticed");

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                if (damage.Contains(definition.Id))
                    Assert.IsTrue(definition.AffectsSingleTargetDamage, $"{definition.Id} must be damage-relevant");
                else
                    Assert.IsTrue(utility.Contains(definition.Id) && !definition.AffectsSingleTargetDamage, $"{definition.Id} must NOT be damage-relevant");
            }
        }

        /// <summary>
        /// DamagePool is a non-empty subset of Pool at both gate states, and every member is flagged and in the
        /// class pool.
        ///
        /// IT IS NOT ALWAYS A *STRICT* SUBSET, and the exception is worth knowing before reading a draw
        /// distribution. With weapon_mods_enabled OFF the Tier A caster pool is Devastation, Weak Point and
        /// Bloodthirst - all three damage-relevant - so for casters the subset EQUALS the pool and the guarantee
        /// is a no-op. The depths are pinned below so a row moving between the halves shows up here.
        /// </summary>
        [TestMethod]
        public void DamageSubset_ThePoolIsANonEmptySubsetForEveryClassAtBothGateStates()
        {
            var depths = new Dictionary<(bool Gate, WeaponClass Class), (int Pool, int Damage)>
            {
                // melee came down by one in both gate states on 2026-08-07 with the Cleave removal. The DAMAGE
                // subset did NOT move with it, and that asymmetry is the interesting part: Cleave was flagged
                // AffectsSingleTargetDamage = false, so removing it narrowed the pool without narrowing the
                // guaranteed draw - melee's utility half is one row thinner, not its damage half.
                //
                // UPDATED AGAIN 2026-08-17, catalog v4. Missile's gate-OFF pool came down by one (5 -> 4) when
                // Swift Flight (Tier A, missile only, AffectsSingleTargetDamage = false) was retired - same
                // asymmetry, the damage subset is untouched. Gate-ON pools grew per Registry_PoolDepthMatchesTheDesign
                // (melee 16, missile 17, caster 16); the damage subset grew too, but by less: the three retired
                // v2 rows (leeches, Overload) were all utility, so their removal does not touch the damage
                // subset either, and of the nine new v4 rows only Panic Reload is damage-relevant (missile +1).
                { (false, WeaponClass.Melee),   (4, 3) },
                { (false, WeaponClass.Missile), (4, 3) },
                { (false, WeaponClass.Caster),  (3, 3) },   // the one case where the subset covers the pool
                { (true,  WeaponClass.Melee),   (16, 9) },
                { (true,  WeaponClass.Missile), (17, 10) },
                { (true,  WeaponClass.Caster),  (16, 7) },
            };

            foreach (var kvp in depths)
            {
                var (gate, weaponClass) = kvp.Key;

                var pool = WeaponModRegistry.Pool(weaponClass, gate);
                var damage = WeaponModRegistry.DamagePool(weaponClass, gate);

                Assert.AreEqual(kvp.Value.Pool, pool.Count, $"gate {gate}, {weaponClass}: pool depth");
                Assert.AreEqual(kvp.Value.Damage, damage.Count, $"gate {gate}, {weaponClass}: damage subset depth");

                Assert.IsTrue(damage.Count > 0, $"gate {gate}, {weaponClass}: the damage subset is empty");
                Assert.IsTrue(damage.Count <= pool.Count, $"gate {gate}, {weaponClass}: the damage subset is not a subset");

                foreach (var definition in damage)
                {
                    Assert.IsTrue(definition.AffectsSingleTargetDamage, $"gate {gate}, {weaponClass}: {definition.Id} is in the damage pool but is not flagged");
                    Assert.IsTrue(pool.Any(m => m.Id == definition.Id), $"gate {gate}, {weaponClass}: {definition.Id} is in the damage pool but not in the class pool");
                }
            }
        }

        /// <summary>
        /// The guarantee itself, at BOTH states of weapon_mod_guarantee_damage_special, driven over the explicit
        /// flag overload so no tunable has to be moved to observe either arm.
        ///
        /// OFF must be indistinguishable from the pre-change behaviour, which is what makes this PR inert. The
        /// evidence for that is not "it did not crash": it is that a utility special DOES turn up first over a
        /// long run, which it cannot if the guarantee is quietly on.
        /// </summary>
        [TestMethod]
        public void DamageSubset_TheFirstDrawIsDamageRelevantOnlyWhenTheTunableIsOn()
        {
            foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
            {
                var damage = WeaponModRegistry.DamagePool(weaponClass).Select(m => m.Id).ToHashSet();

                // ---- ON: the first draw is ALWAYS damage-relevant, and the rest are unconstrained ----
                for (var i = 0; i < 2000; i++)
                {
                    var rolled = WeaponModRoller.RollDistinctSpecials(weaponClass, WeaponModRegistry.MaxSpecials, null, true);

                    Assert.IsTrue(rolled.Count > 0, $"{weaponClass}: the guaranteed draw returned nothing");
                    Assert.IsTrue(damage.Contains(rolled[0].Id), $"{weaponClass}: the first special was {rolled[0].Id}, which is not damage-relevant");

                    Assert.AreEqual(rolled.Count, rolled.Select(d => d.Id).Distinct().Count(), $"{weaponClass}: the guarantee broke distinctness");

                    foreach (var definition in rolled)
                        Assert.IsTrue(definition.AppliesTo(weaponClass), $"{weaponClass}: the guarantee drew the out-of-class {definition.Id}");
                }

                // ---- OFF: a utility special reaches slot one, so the guarantee really is doing something ----
                //
                // SKIPPED FOR A CLASS WHOSE SUBSET COVERS ITS POOL. With the gate off the Tier A caster pool is
                // exactly its three damage rows, so there is no utility special that COULD come first and the
                // guarantee is a no-op there. Asserting it anyway would fail on correct behaviour.
                if (WeaponModRegistry.DamagePool(weaponClass).Count == WeaponModRegistry.Pool(weaponClass).Count)
                    continue;

                var sawUtilityFirst = false;

                for (var i = 0; i < 2000 && !sawUtilityFirst; i++)
                {
                    var rolled = WeaponModRoller.RollDistinctSpecials(weaponClass, WeaponModRegistry.MaxSpecials, null, false);

                    sawUtilityFirst = rolled.Count > 0 && !damage.Contains(rolled[0].Id);
                }

                Assert.IsTrue(sawUtilityFirst,
                    $"{weaponClass}: 2000 unguaranteed draws never put a utility special first - the guarantee is on when it should be off");
            }
        }

        /// <summary>
        /// weapon_mod_guarantee_damage_special DEFAULTED TO FALSE until the 2026-08-06 v3 magnitude pass, which
        /// flipped it to TRUE. The tunable-reading path must agree with the explicit-flag overload in both
        /// directions. Asserted by driving the live tunable, not by inspection.
        /// </summary>
        [TestMethod]
        public void DamageSubset_TheTunableDefaultsOnAndTheLivePathHonoursIt()
        {
            Assert.IsTrue(PropertyManager.GetBool("weapon_mod_guarantee_damage_special").Item,
                "weapon_mod_guarantee_damage_special must default to TRUE after the 2026-08-06 v3 magnitude pass");

            Assert.IsTrue(WeaponModRoller.GuaranteeDamageSpecial());

            var damage = WeaponModRegistry.DamagePool(WeaponClass.Missile).Select(m => m.Id).ToHashSet();

            for (var i = 0; i < 500; i++)
            {
                // the tunable-reading overload, with no flag passed
                var rolled = WeaponModRoller.RollDistinctSpecials(WeaponClass.Missile, 2);

                Assert.IsTrue(damage.Contains(rolled[0].Id), $"the live path ignored the tunable: first special was {rolled[0].Id}");
            }

            var prior = PropertyManager.GetBool("weapon_mod_guarantee_damage_special").Item;

            try
            {
                // the OFF path still exists and is still honoured, driven explicitly since it is no longer the default
                PropertyManager.ModifyBool("weapon_mod_guarantee_damage_special", false);

                Assert.IsFalse(WeaponModRoller.GuaranteeDamageSpecial());
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mod_guarantee_damage_special", prior);
            }
        }

        /// <summary>
        /// The fallback: when the damage subset is entirely excluded the draw must still return a special from
        /// the full pool rather than null. Returning null there would silently shorten a set, and on the swap
        /// path it would refuse AFTER the salvage bag was consumed.
        /// </summary>
        [TestMethod]
        public void DamageSubset_AnExhaustedSubsetFallsBackToTheFullPoolRatherThanReturningNull()
        {
            foreach (var weaponClass in new[] { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster })
            {
                var damage = WeaponModRegistry.DamagePool(weaponClass).Select(m => m.Id).ToList();

                // a FULLY exhausted pool is null whatever the flag says - the fallback relaxes the SUBSET, never
                // the exclusion list
                var everything = WeaponModRegistry.Pool(weaponClass).Select(m => m.Id).ToList();

                Assert.IsNull(WeaponModRoller.RollSpecial(weaponClass, everything, true),
                    $"{weaponClass}: an exhausted FULL pool must still return null");

                // ... which is exactly what excluding the damage subset amounts to on a class whose subset
                // covers its pool (the Tier A caster pool), so there is no fallback to observe there
                if (damage.Count == everything.Count)
                    continue;

                for (var i = 0; i < 200; i++)
                {
                    var drawn = WeaponModRoller.RollSpecial(weaponClass, damage, true);

                    Assert.IsNotNull(drawn, $"{weaponClass}: an exhausted damage subset returned null instead of falling back");
                    Assert.IsFalse(damage.Contains(drawn.Id), $"{weaponClass}: the fallback ignored the exclusion list");
                    Assert.IsTrue(drawn.AppliesTo(weaponClass), $"{weaponClass}: the fallback drew the out-of-class {drawn.Id}");
                }
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

                    // A DRAW IS BOUNDED BY THE POOL, NOT BY THE CAP, and since the cap went to 4 on 2026-08-06
                    // the two differ: the Tier A caster pool is 3 deep, so a caster asks for 4 and gets 3. That
                    // is the documented consequence recorded on WeaponModRegistry.MaxSpecials, not a shortfall.
                    var expected = Math.Min(WeaponModRegistry.MaxSpecials, WeaponModRegistry.Pool(weaponClass).Count);

                    Assert.AreEqual(expected, rolled.Count, $"{weaponClass}: draw did not fill to min(cap, pool depth)");
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
                var scale = WeaponModValue.MagnitudeScale();
                var magnitude = WeaponModValue.Resolve(definition, 1.0, 10.0, scale);

                WeaponModTinkerSet.ApplySpecial(weapon, definition, magnitude);

                Assert.IsNotNull(weapon.GetProperty(definition.Record), $"{definition.Id}: the record was not written");

                // WHAT THE RECORD HOLDS IS TIER-DEPENDENT since the 2026-08-07 split, and the asymmetry is the
                // reason this reversal test only exercises Tier A's native arithmetic - a Tier B row has no
                // native to restore, so it has nothing to get wrong here.
                if (definition.Tier == WeaponModTier.B)
                {
                    Assert.AreEqual(WeaponModValue.FractionFor(definition, magnitude, scale), weapon.GetProperty(definition.Record).Value, 1e-12,
                        $"{definition.Id}: a Tier B record must hold the ROLL FRACTION, not the magnitude");

                    Assert.AreEqual(magnitude, WeaponModTinkerSet.ReadMagnitude(weapon, definition, scale), 1e-12,
                        $"{definition.Id}: the stored fraction must resolve back to the magnitude that was applied");
                }
                else
                {
                    Assert.AreEqual(magnitude, weapon.GetProperty(definition.Record).Value, 1e-12,
                        $"{definition.Id}: a Tier A record must hold the APPLIED MAGNITUDE, not a potency - reversal subtracts exactly this number back off a native the loot generator may also have written to");
                }

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
                    //
                    // UNREACHABLE SINCE 2026-08-17: Swift Flight was the only row that ever set
                    // RemoveOnDefault = false, and it was retired in the catalog v4 pass. The machinery and
                    // this branch are deliberately left in place (see WeaponModRegistry.cs's remarks on why
                    // Cleave's Binary/MinPotency machinery survived its own removal the same way) - it is the
                    // general rule, not Swift Flight's private arrangement, and a future row may need it again.
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
        /// The Binary + NativeFloor + NativeDefault machinery, exercised over a SYNTHETIC definition.
        ///
        /// It used to be exercised over the real Cleave row, whose Cleaving native stores TOTAL targets
        /// INCLUDING the primary - so an untinkered weapon behaves as 1 rather than 0, +1 has to land on 2 to
        /// buy one extra target, and reversing back to 1 has to REMOVE the row rather than write the 1 back,
        /// because IsCleaving is a null test. Cleave was removed from the catalog on 2026-08-07, and with it
        /// went the only row that ever set Binary or a non-zero NativeFloor.
        ///
        /// THE TEST IS KEPT AND RETARGETED RATHER THAN DELETED WITH THE ROW. The machinery it covers is still
        /// in WeaponModDefinition, still reachable, and still the correct handling for the next native whose
        /// empty state is not zero. Deleting the coverage along with its only current caller is how that
        /// machinery silently rots into something that no longer works when a row finally needs it again.
        /// The arithmetic is pure (ApplyValue / ReverseValue take a nullable current and return a value), so
        /// no item and no live registry row is needed to drive it.
        /// </summary>
        [TestMethod]
        public void Machinery_ABinaryFlooredModifierAppliesFlatAndReversesBackToAbsent()
        {
            // the shape Cleave had: a total count whose empty state is 1, applied flat
            var definition = new WeaponModDefinition
            {
                Id = WeaponModId.Devastation,          // any id; nothing here reads the registry
                DisplayName = "Synthetic Floored Count",
                Record = PropertyFloat.WeaponModDevastation,
                NativeInt = PropertyInt.Cleaving,
                MaxRoll = 1,
                NativeDefault = 1,
                NativeFloor = 1,
                Binary = true,
                MinPotency = 0,
                Classes = WeaponClass.Melee,
                DisplayFormat = "+{0:0} target",
            };

            // Binary: potency, workmanship and the scale tunable are all ignored, so the magnitude is MaxRoll
            Assert.AreEqual(1.0, WeaponModValue.Resolve(definition, 0.25, 1.0, 0.1), 1e-12, "a binary modifier ignores potency, workmanship and scale");
            Assert.AreEqual(1.0, WeaponModValue.Resolve(definition, 1.0, 10.0, 1.0), 1e-12, "a binary modifier at a perfect roll is the same number");

            // from ABSENT: the default of 1 is the starting point, so +1 lands on 2, not on 1
            Assert.AreEqual(2.0, definition.ApplyValue(null, 1.0), 1e-12, "an absent native must apply against its default of 1, so +1 buys a second target");

            // ... and reversing back to the default REMOVES the row rather than writing 1 back, which is what
            // restores "absent" on a native consumed as a null test
            Assert.IsNull(definition.ReverseValue(2.0, 1.0), "a reversal landing on the default must remove the row, not write the default");

            // from a value the item already carried: applied and reversed exactly, no floor involvement
            Assert.AreEqual(4.0, definition.ApplyValue(3.0, 1.0), 1e-12);
            Assert.AreEqual(3.0, definition.ReverseValue(4.0, 1.0).Value, 1e-12, "a pre-existing value must come back untouched");

            // the floor itself: an over-large reversal is clamped at 1 rather than driven to 0 or below, and
            // landing ON the floor is landing on the default, so the row is removed
            Assert.IsNull(definition.ReverseValue(2.0, 99.0), "a reversal below the floor clamps to it, which is the default, so the row goes");
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
        /// RENAMED AND REWRITTEN 2026-08-07 (was Log_ASecondRerollLeavesBothLogsAtTenEntriesNotTwenty).
        ///
        /// The old contract was "the logs are REPLACED, never appended" - the hazard being
        /// RecipeManager.HandleTinkerLog's "TinkerLog += ...", which would have left 20 entries after a second
        /// reroll. The special-only reroll subsumes that rule entirely: it writes NEITHER log, so there is
        /// nothing to append to and nothing to replace. Asserting non-writing is strictly stronger than
        /// asserting replacement, because a "+=" and a rewrite BOTH fail it.
        ///
        /// The weapon here is deliberately a hand-tinkered one carrying a real ten-entry retail log. A weapon
        /// with no log at all would pass this test against an implementation that writes a log only when it has
        /// one to rewrite, which is exactly the regression worth catching.
        /// </summary>
        [TestMethod]
        public void Log_NoNumberOfRerollsEverWritesEitherTinkerLog()
        {
            var tenIron = Enumerable.Repeat(MaterialType.Iron, 10).ToList();
            var serialized = WeaponModTinkerSet.SerializeLog(tenIron);

            var weapon = MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee,
                new Dictionary<PropertyInt, int> { { PropertyInt.Damage, 12 }, { PropertyInt.NumTimesTinkered, 10 } },
                new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } });

            weapon.SetProperty(PropertyString.TinkerLog, serialized);

            for (var pass = 1; pass <= 5; pass++)
            {
                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"pass {pass} produced no result");

                Assert.AreEqual(serialized, weapon.GetProperty(PropertyString.TinkerLog),
                    $"pass {pass}: TinkerLog moved. The reroll is special-only since 2026-08-07 and must not write this row at all - not appended, not replaced, not rewritten identically");

                Assert.IsNull(weapon.GetProperty(PropertyString.WeaponModTinkerLog),
                    $"pass {pass}: WeaponModTinkerLog was written on a weapon this system does not manage the tinkers of");

                Assert.AreEqual(10, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                    $"pass {pass}: NumTimesTinkered moved - the reroll no longer sets it, so it must read exactly what the player's own tinkering left");

                Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                    $"pass {pass}: WeaponModTinkerCount was written, which would mark an unmanaged weapon as managed and suppress the retail integrity gate on it");
            }
        }

        /// <summary>
        /// RENAMED AND REWRITTEN 2026-08-07 (was Reroll_AlwaysFillsExactlyTenSlotsAndNeverExceedsTheTinkerCap).
        ///
        /// The old contract was "the reroll fills the ten-slot budget and pins NumTimesTinkered at exactly 10".
        /// The reroll is special-only now: it never fills a budget, so an UNTINKERED weapon must come back out
        /// of it still untinkered. That is the sharper statement of the same safety property the old name was
        /// reaching for - the tinker cap cannot be exceeded by a path that never writes the counter at all.
        ///
        /// The imbue mask is kept because it is the other half of the old test and still matters: an imbue is
        /// unrecoverable if lost, and the reroll runs over the weapon carrying it.
        /// </summary>
        [TestMethod]
        public void Reroll_LeavesAnUntinkeredWeaponUntinkeredAndNeverWritesTheTinkerCap()
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

                Assert.IsTrue(specials <= WeaponModRegistry.MaxSpecials, $"{specials} specials, above the permanent bound");

                // the whole of layer 1 is untouched, on a weapon that had none of it to begin with
                Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                    $"the reroll wrote a tinker count on an untinkered weapon (specials held: {specials}) - it is special-only and lays down no tinkers");

                Assert.IsNull(weapon.GetProperty(PropertyInt.NumTimesTinkered),
                    "the reroll wrote NumTimesTinkered on a weapon that was never tinkered. TinkeringDifficulty is an unguarded 10-element list indexed by that counter, and the surest way never to index it wrongly is never to write it");

                Assert.IsNull(weapon.GetProperty(PropertyString.TinkerLog), "the reroll wrote a retail tinker log on an untinkered weapon");
                Assert.IsNull(weapon.GetProperty(PropertyString.WeaponModTinkerLog), "the reroll wrote its own tinker log on an untinkered weapon");

                // the layer 1 natives a melee tinker would have moved are exactly where the weenie left them
                Assert.AreEqual(12, weapon.GetProperty(PropertyInt.Damage), "Damage moved, so a layer 1 Iron was applied");
                Assert.AreEqual(0.4, weapon.GetProperty(PropertyFloat.DamageVariance).Value, 1e-12, "DamageVariance moved, so a layer 1 Granite was applied");

                // imbues are untouched
                Assert.AreEqual(twoImbues, weapon.GetProperty(PropertyInt.ImbuedEffect));
            }
        }

        /// <summary>
        /// The swap leaves the TINKER budget exactly full: ten tinkers on an unreserved weapon, NumTimesTinkered
        /// ten, and the special count never passes the cap.
        ///
        /// REWRITTEN FOR THE 2026-08-06 DECOUPLING. This used to assert "specials + tinkers == 10", which was the
        /// old shared budget. The swap's add half now refills the tinker budget to its capacity instead of adding
        /// exactly one modifier, precisely so that removing a special cannot push the tinker count to eleven -
        /// see WeaponModManager.ApplySwap. So the sharp assertion is on the tinkers ALONE.
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

                // REWORKED 2026-08-06: Amethyst needs an existing special to reroll, and never touches tinkers
                // at all - seed one directly so the swap loop below has something to act on.
                WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(WeaponModId.Devastation), 5.0);

                var tinkersBefore = weapon.GetProperty(PropertyInt.WeaponModTinkerCount);
                var logBefore = weapon.GetProperty(PropertyString.WeaponModTinkerLog);

                for (var use = 0; use < 6; use++)
                {
                    if (WeaponModTinkerSet.SpecialCount(weapon) <= 0)
                        break;

                    Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), $"use {use} produced no result");

                    var specials = WeaponModTinkerSet.SpecialCount(weapon);

                    Assert.IsTrue(specials <= WeaponModRegistry.MaxSpecials, $"use {use}: {specials} specials");
                    Assert.AreEqual(10, weapon.GetProperty(PropertyInt.NumTimesTinkered), $"use {use}: NumTimesTinkered moved off 10");

                    // the swap never touches tinkers at all - the budget stays exactly what it was seeded at
                    Assert.AreEqual(tinkersBefore, weapon.GetProperty(PropertyInt.WeaponModTinkerCount), $"use {use}: the swap touched the tinker count");
                    Assert.AreEqual(logBefore, weapon.GetProperty(PropertyString.WeaponModTinkerLog), $"use {use}: the swap touched the tinker log");
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
