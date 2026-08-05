using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Prismatic Drift Stone starter tinkering gem: the dynamic rend mapping, the class
    /// appropriate damage boosts, the clean-weapon guard, and the permanent tinker lock.
    ///
    /// Weapons are built from in-memory weenies (never persisted, so nothing here touches the
    /// database), with static-range guids, following the ContainerStackTests fixture pattern.
    /// Assertions are invariants - "the delta equals N times the per-boost step" - never a
    /// hand-derived numeric sequence; the arithmetic lives in the assertion messages.
    /// </summary>
    [TestClass]
    public class PrismaticDriftStoneTests
    {
        private static uint nextGuid = 0x7E000000;

        /// <summary>How many rolls to take when a test needs to see the whole random range.</summary>
        private const int RollSamples = 2000;

        private const double FloatTolerance = 1e-9;

        private static MeleeWeapon CreateMeleeWeapon(DamageType damageType, int baseDamage = 10)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 314,   // arbitrary dagger-like wcid; nothing reads it back from the db
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.Damage, baseDamage },
                },
            };

            if (damageType != DamageType.Undef)
                weenie.PropertiesInt.Add(PropertyInt.DamageType, (int)damageType);

            return new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        private static MissileLauncher CreateMissileLauncher(DamageType damageType, double baseDamageMod = 1.5)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 306,   // arbitrary bow-like wcid
                WeenieType = WeenieType.MissileLauncher,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MissileWeapon },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.DamageMod, baseDamageMod },
                },
            };

            if (damageType != DamageType.Undef)
                weenie.PropertiesInt.Add(PropertyInt.DamageType, (int)damageType);

            return new MissileLauncher(weenie, new ObjectGuid(nextGuid++));
        }

        private static Caster CreateCaster(DamageType damageType, double baseElementalDamageMod = 1.0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 2472,   // arbitrary wand-like wcid
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.ElementalDamageMod, baseElementalDamageMod },
                },
            };

            if (damageType != DamageType.Undef)
                weenie.PropertiesInt.Add(PropertyInt.DamageType, (int)damageType);

            return new Caster(weenie, new ObjectGuid(nextGuid++));
        }

        // ---------------------------------------------------------------------------------------
        // rend mapping: single damage types
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void GetRend_SingleDamageType_MapsToMatchingRend()
        {
            var expected = new Dictionary<DamageType, ImbuedEffectType>
            {
                { DamageType.Slash,    ImbuedEffectType.SlashRending },
                { DamageType.Pierce,   ImbuedEffectType.PierceRending },
                { DamageType.Bludgeon, ImbuedEffectType.BludgeonRending },
                { DamageType.Fire,     ImbuedEffectType.FireRending },
                { DamageType.Cold,     ImbuedEffectType.ColdRending },
                { DamageType.Acid,     ImbuedEffectType.AcidRending },
                { DamageType.Electric, ImbuedEffectType.ElectricRending },
            };

            foreach (var kvp in expected)
            {
                Assert.AreEqual(kvp.Value, PrismaticDriftStone.GetRend(kvp.Key, ItemType.MeleeWeapon),
                    $"{kvp.Key} weapon should earn {kvp.Value}");
            }
        }

        [TestMethod]
        public void GetRend_Nether_FallsBackToCriticalStrike()
        {
            // nether has no usable rend, so a nether wand gets CriticalStrike instead
            Assert.AreEqual(ImbuedEffectType.CriticalStrike, PrismaticDriftStone.GetRend(DamageType.Nether, ItemType.Caster),
                "a nether caster has no rend available and must fall back to CriticalStrike");
        }

        [TestMethod]
        public void GetRend_EveryRendIsInTheRetailIconUnderlayTable()
        {
            // every outcome this mechanic can produce must have a retail icon underlay, or the
            // imbue would apply with no visible marker on the item
            var allDamageTypes = new[]
            {
                DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon,
                DamageType.Fire, DamageType.Cold, DamageType.Acid, DamageType.Electric,
                DamageType.Nether, DamageType.Undef,
            };

            var itemTypes = new[] { ItemType.MeleeWeapon, ItemType.MissileWeapon, ItemType.Caster };

            foreach (var damageType in allDamageTypes)
            {
                foreach (var itemType in itemTypes)
                {
                    var rend = PrismaticDriftStone.GetRend(damageType, itemType);

                    Assert.IsTrue(RecipeManager.IconUnderlay.ContainsKey(rend),
                        $"{damageType} + {itemType} produced {rend}, which has no entry in RecipeManager.IconUnderlay");

                    Assert.AreNotEqual(MaterialType.Unknown, PrismaticDriftStone.GetRendMaterial(rend),
                        $"{damageType} + {itemType} produced {rend}, which has no TinkerLog material");
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // rend mapping: multi-type priority
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void GetRend_ElementalBeatsPhysical()
        {
            // real multi-type weenies from ace_world: greatswordofflameandlight is Slash|Fire (17),
            // scepterlugian is Bludgeon|Fire (20), shardofharraagsdagger is Slash|Pierce|Electric (66)
            Assert.AreEqual(ImbuedEffectType.FireRending,
                PrismaticDriftStone.GetRend(DamageType.Slash | DamageType.Fire, ItemType.MeleeWeapon),
                "Slash|Fire must resolve to the elemental bit, not Slash");

            Assert.AreEqual(ImbuedEffectType.FireRending,
                PrismaticDriftStone.GetRend(DamageType.Bludgeon | DamageType.Fire, ItemType.MeleeWeapon),
                "Bludgeon|Fire must resolve to the elemental bit, not Bludgeon");

            Assert.AreEqual(ImbuedEffectType.ElectricRending,
                PrismaticDriftStone.GetRend(DamageType.Slash | DamageType.Pierce | DamageType.Electric, ItemType.MeleeWeapon),
                "Slash|Pierce|Electric must resolve to the elemental bit");
        }

        [TestMethod]
        public void GetRend_FullPriorityOrderIsDeterministic()
        {
            // Fire > Cold > Acid > Electric > Nether > Slash > Pierce > Bludgeon.
            // Walk the order: at each step, the union of that type with every LOWER priority type
            // must still resolve to the higher one.
            var order = new[]
            {
                Tuple.Create(DamageType.Fire,     ImbuedEffectType.FireRending),
                Tuple.Create(DamageType.Cold,     ImbuedEffectType.ColdRending),
                Tuple.Create(DamageType.Acid,     ImbuedEffectType.AcidRending),
                Tuple.Create(DamageType.Electric, ImbuedEffectType.ElectricRending),
                Tuple.Create(DamageType.Nether,   ImbuedEffectType.CriticalStrike),
                Tuple.Create(DamageType.Slash,    ImbuedEffectType.SlashRending),
                Tuple.Create(DamageType.Pierce,   ImbuedEffectType.PierceRending),
                Tuple.Create(DamageType.Bludgeon, ImbuedEffectType.BludgeonRending),
            };

            for (var i = 0; i < order.Length; i++)
            {
                var combined = order[i].Item1;

                for (var j = i + 1; j < order.Length; j++)
                    combined |= order[j].Item1;

                Assert.AreEqual(order[i].Item2, PrismaticDriftStone.GetRend(combined, ItemType.MeleeWeapon),
                    $"{combined} contains priority-{i} {order[i].Item1} plus every lower priority bit, so it must resolve to {order[i].Item2}");
            }
        }

        [TestMethod]
        public void GetRend_PriorityIsIndependentOfBitOrder()
        {
            // the mapping must not depend on which order the caller happens to think of the bits in
            Assert.AreEqual(PrismaticDriftStone.GetRend(DamageType.Slash | DamageType.Fire, ItemType.MeleeWeapon),
                            PrismaticDriftStone.GetRend(DamageType.Fire | DamageType.Slash, ItemType.MeleeWeapon),
                "DamageType is a flags enum; the union is order independent and so must the rend be");
        }

        // ---------------------------------------------------------------------------------------
        // rend mapping: no rendable damage bit
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void GetRend_ElementlessMissileLauncher_DefaultsToPierceRending()
        {
            // 544 of 747 MissileLauncher weenies in ace_world carry no DamageType at all (bowlong,
            // bowshort, crossbowheavy ...), and 31 more carry DamageType 0
            Assert.AreEqual(ImbuedEffectType.PierceRending,
                PrismaticDriftStone.GetRend(DamageType.Undef, ItemType.MissileWeapon),
                "an elementless bow defaults to PierceRending");
        }

        [TestMethod]
        public void GetRend_ElementlessNonMissileWeapon_DefaultsToCriticalStrike()
        {
            // 271 of 425 Caster weenies carry no DamageType at all (wand, orb, staff ...)
            Assert.AreEqual(ImbuedEffectType.CriticalStrike,
                PrismaticDriftStone.GetRend(DamageType.Undef, ItemType.Caster),
                "a plain wand has no element to rend, so it falls back to CriticalStrike");

            Assert.AreEqual(ImbuedEffectType.CriticalStrike,
                PrismaticDriftStone.GetRend(DamageType.Undef, ItemType.MeleeWeapon),
                "a melee weapon with no damage type also falls back to CriticalStrike");
        }

        [TestMethod]
        public void GetRend_WorldObjectOverload_ReadsTheWeaponsOwnProperties()
        {
            var weapon = CreateMeleeWeapon(DamageType.Acid);

            Assert.AreEqual(PrismaticDriftStone.GetRend(DamageType.Acid, ItemType.MeleeWeapon),
                            PrismaticDriftStone.GetRend(weapon),
                "the WorldObject overload must agree with the pure mapping for the same inputs");
        }

        // ---------------------------------------------------------------------------------------
        // boost class selection
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void GetBoostClass_SelectsPerWeaponClass()
        {
            Assert.AreEqual(PrismaticDriftStone.BoostClass.MeleeDamage,
                PrismaticDriftStone.GetBoostClass(CreateMeleeWeapon(DamageType.Slash)),
                "a melee weapon gets the Iron equivalent (Damage)");

            Assert.AreEqual(PrismaticDriftStone.BoostClass.MissileDamageMod,
                PrismaticDriftStone.GetBoostClass(CreateMissileLauncher(DamageType.Undef)),
                "a missile launcher gets the Mahogany equivalent (DamageMod)");

            Assert.AreEqual(PrismaticDriftStone.BoostClass.CasterElementalDamageMod,
                PrismaticDriftStone.GetBoostClass(CreateCaster(DamageType.Fire)),
                "a caster gets the Green Garnet equivalent (ElementalDamageMod)");
        }

        [TestMethod]
        public void GetBoostClass_IneligibleItemType_IsNone()
        {
            Assert.AreEqual(PrismaticDriftStone.BoostClass.None,
                PrismaticDriftStone.GetBoostClass(ItemType.Armor),
                "armor is not a weapon class and has no boost");
        }

        [TestMethod]
        public void GetBoostMaterial_MatchesTheRetailTinkeringMaterials()
        {
            Assert.AreEqual(MaterialType.Iron,
                PrismaticDriftStone.GetBoostMaterial(PrismaticDriftStone.BoostClass.MeleeDamage));

            Assert.AreEqual(MaterialType.Mahogany,
                PrismaticDriftStone.GetBoostMaterial(PrismaticDriftStone.BoostClass.MissileDamageMod));

            Assert.AreEqual(MaterialType.GreenGarnet,
                PrismaticDriftStone.GetBoostMaterial(PrismaticDriftStone.BoostClass.CasterElementalDamageMod));
        }

        // ---------------------------------------------------------------------------------------
        // eligibility + the clean-weapon guard
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void IsEligibleWeapon_AcceptsTheThreeWeaponClasses()
        {
            Assert.IsTrue(PrismaticDriftStone.IsEligibleWeapon(CreateMeleeWeapon(DamageType.Slash)));
            Assert.IsTrue(PrismaticDriftStone.IsEligibleWeapon(CreateMissileLauncher(DamageType.Undef)));
            Assert.IsTrue(PrismaticDriftStone.IsEligibleWeapon(CreateCaster(DamageType.Fire)));
        }

        [TestMethod]
        public void IsEligibleWeapon_RejectsEverythingElse()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 136,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                },
            };

            var armor = new Clothing(weenie, new ObjectGuid(nextGuid++));

            Assert.IsFalse(PrismaticDriftStone.IsEligibleWeapon(armor),
                "armor is not a melee weapon, missile launcher or caster");
        }

        [TestMethod]
        public void IsCleanWeapon_AcceptsAnUntouchedWeapon()
        {
            Assert.IsTrue(PrismaticDriftStone.IsCleanWeapon(CreateMeleeWeapon(DamageType.Slash)),
                "a fresh weapon has no tinkers, no imbue and no tinker log");
        }

        [TestMethod]
        public void IsCleanWeapon_RejectsAnAlreadyTinkeredWeapon()
        {
            var weapon = CreateMeleeWeapon(DamageType.Slash);
            weapon.NumTimesTinkered = 1;

            Assert.IsFalse(PrismaticDriftStone.IsCleanWeapon(weapon),
                "NumTimesTinkered > 0 means the weapon has spent tinker slots already");
        }

        [TestMethod]
        public void IsCleanWeapon_RejectsAnAlreadyImbuedWeapon()
        {
            var weapon = CreateMeleeWeapon(DamageType.Slash);
            weapon.ImbuedEffect = ImbuedEffectType.CriticalStrike;

            Assert.IsFalse(PrismaticDriftStone.IsCleanWeapon(weapon),
                "an existing imbue would be overwritten, so the stone must refuse");
        }

        [TestMethod]
        public void IsCleanWeapon_RejectsATinkerLoggedWeapon()
        {
            var weapon = CreateMeleeWeapon(DamageType.Slash);
            weapon.TinkerLog = ((uint)MaterialType.Iron).ToString();

            Assert.IsFalse(PrismaticDriftStone.IsCleanWeapon(weapon),
                "a tinker log records past work even when the counters were never written");
        }

        [TestMethod]
        public void IsCleanWeapon_RejectsAWeaponThisStoneAlreadyProcessed()
        {
            // the guard must be self-consistent: whatever ApplyToWeapon leaves behind is not clean
            var weapon = CreateMeleeWeapon(DamageType.Slash);
            PrismaticDriftStone.ApplyToWeapon(weapon);

            Assert.IsFalse(PrismaticDriftStone.IsCleanWeapon(weapon),
                "the stone must not be applicable twice to the same weapon");
        }

        // ---------------------------------------------------------------------------------------
        // application
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void ApplyToWeapon_RollsBetweenMinAndMaxBoosts()
        {
            var seen = new HashSet<int>();

            for (var i = 0; i < RollSamples; i++)
            {
                var result = PrismaticDriftStone.ApplyToWeapon(CreateMeleeWeapon(DamageType.Slash));

                Assert.IsTrue(result.NumBoosts >= PrismaticDriftStone.MinBoosts && result.NumBoosts <= PrismaticDriftStone.MaxBoosts,
                    $"rolled {result.NumBoosts} boosts, outside the inclusive range [{PrismaticDriftStone.MinBoosts}, {PrismaticDriftStone.MaxBoosts}]");

                seen.Add(result.NumBoosts);
            }

            var expectedCount = PrismaticDriftStone.MaxBoosts - PrismaticDriftStone.MinBoosts + 1;

            Assert.AreEqual(expectedCount, seen.Count,
                $"over {RollSamples} rolls every value in [{PrismaticDriftStone.MinBoosts}, {PrismaticDriftStone.MaxBoosts}] "
                + $"({expectedCount} distinct values) should appear; saw {seen.Count}: {string.Join(",", seen.OrderBy(n => n))}");
        }

        [TestMethod]
        public void ApplyToWeapon_LocksTinkeringRegardlessOfBoostCount()
        {
            var seenBoostCounts = new HashSet<int>();

            for (var i = 0; i < RollSamples; i++)
            {
                var weapon = CreateMeleeWeapon(DamageType.Slash);
                var result = PrismaticDriftStone.ApplyToWeapon(weapon);

                seenBoostCounts.Add(result.NumBoosts);

                Assert.AreEqual(PrismaticDriftStone.LockedTinkerCount, weapon.NumTimesTinkered,
                    $"{result.NumBoosts} boosts landed, but NumTimesTinkered must always be the lock value "
                    + $"{PrismaticDriftStone.LockedTinkerCount}, not the boost count");
            }

            Assert.IsTrue(seenBoostCounts.Count > 1,
                "the lock assertion is only meaningful if the sample actually varied the boost count; "
                + $"saw only {string.Join(",", seenBoostCounts)}");
        }

        [TestMethod]
        public void ApplyToWeapon_Melee_AddsOneDamagePerBoost()
        {
            const int baseDamage = 10;

            for (var i = 0; i < RollSamples; i++)
            {
                var weapon = CreateMeleeWeapon(DamageType.Slash, baseDamage);
                var result = PrismaticDriftStone.ApplyToWeapon(weapon);

                var expected = baseDamage + result.NumBoosts * PrismaticDriftStone.MeleeDamagePerBoost;

                Assert.AreEqual(expected, weapon.Damage,
                    $"base {baseDamage} + {result.NumBoosts} boosts x {PrismaticDriftStone.MeleeDamagePerBoost} = {expected}");
            }
        }

        [TestMethod]
        public void ApplyToWeapon_MissileLauncher_AddsDamageModPerBoost()
        {
            const double baseDamageMod = 1.5;

            for (var i = 0; i < RollSamples; i++)
            {
                var weapon = CreateMissileLauncher(DamageType.Undef, baseDamageMod);
                var result = PrismaticDriftStone.ApplyToWeapon(weapon);

                var expected = baseDamageMod + result.NumBoosts * PrismaticDriftStone.MissileDamageModPerBoost;

                Assert.AreEqual(expected, weapon.DamageMod ?? 0.0, FloatTolerance,
                    $"base {baseDamageMod} + {result.NumBoosts} boosts x {PrismaticDriftStone.MissileDamageModPerBoost} = {expected}");
            }
        }

        [TestMethod]
        public void ApplyToWeapon_Caster_AddsElementalDamageModPerBoost()
        {
            const double baseElementalDamageMod = 1.0;

            for (var i = 0; i < RollSamples; i++)
            {
                var weapon = CreateCaster(DamageType.Fire, baseElementalDamageMod);
                var result = PrismaticDriftStone.ApplyToWeapon(weapon);

                var expected = baseElementalDamageMod + result.NumBoosts * PrismaticDriftStone.CasterElementalDamageModPerBoost;

                Assert.AreEqual(expected, weapon.ElementalDamageMod ?? 0.0, FloatTolerance,
                    $"base {baseElementalDamageMod} + {result.NumBoosts} boosts x {PrismaticDriftStone.CasterElementalDamageModPerBoost} = {expected}");
            }
        }

        [TestMethod]
        public void ApplyToWeapon_OnlyTouchesItsOwnWeaponClassProperty()
        {
            // a melee application must not move the missile / caster knobs, and vice versa
            var melee = CreateMeleeWeapon(DamageType.Slash);
            PrismaticDriftStone.ApplyToWeapon(melee);

            Assert.IsNull(melee.DamageMod, "a melee application must not write DamageMod");
            Assert.IsNull(melee.ElementalDamageMod, "a melee application must not write ElementalDamageMod");

            var launcher = CreateMissileLauncher(DamageType.Undef);
            PrismaticDriftStone.ApplyToWeapon(launcher);

            Assert.IsNull(launcher.Damage, "a missile application must not write Damage");
            Assert.IsNull(launcher.ElementalDamageMod, "a missile application must not write ElementalDamageMod");

            var caster = CreateCaster(DamageType.Fire);
            PrismaticDriftStone.ApplyToWeapon(caster);

            Assert.IsNull(caster.Damage, "a caster application must not write Damage");
            Assert.IsNull(caster.DamageMod, "a caster application must not write DamageMod");
        }

        [TestMethod]
        public void ApplyToWeapon_SetsTheImbueAndItsRetailSideEffects()
        {
            var weapon = CreateMeleeWeapon(DamageType.Fire);
            var result = PrismaticDriftStone.ApplyToWeapon(weapon);

            Assert.AreEqual(ImbuedEffectType.FireRending, result.Rend,
                "a fire weapon earns FireRending");

            Assert.AreEqual(result.Rend, weapon.ImbuedEffect,
                "the reported rend and the property written on the weapon must agree");

            Assert.AreEqual(RecipeManager.IconUnderlay[result.Rend], weapon.IconUnderlayId,
                "the imbue must carry the same icon underlay retail uses for that rend");
        }

        [TestMethod]
        public void ApplyToWeapon_TinkerLogRecordsEveryBoostPlusTheRendGem()
        {
            for (var i = 0; i < RollSamples; i++)
            {
                var weapon = CreateMeleeWeapon(DamageType.Cold);
                var result = PrismaticDriftStone.ApplyToWeapon(weapon);

                var entries = weapon.TinkerLog.Split(',');

                Assert.AreEqual(result.NumBoosts + 1, entries.Length,
                    $"{result.NumBoosts} boost materials + 1 rend gem = {result.NumBoosts + 1} tinker log entries");

                var boostMaterial = ((uint)PrismaticDriftStone.GetBoostMaterial(result.BoostClass)).ToString();
                var rendMaterial = ((uint)PrismaticDriftStone.GetRendMaterial(result.Rend)).ToString();

                for (var e = 0; e < result.NumBoosts; e++)
                {
                    Assert.AreEqual(boostMaterial, entries[e],
                        $"tinker log entry {e} of {entries.Length} should be the boost material {boostMaterial}");
                }

                Assert.AreEqual(rendMaterial, entries[entries.Length - 1],
                    $"the last tinker log entry should be the rend gem material {rendMaterial}");

                CollectionAssert.AreEqual(
                    result.Materials.Select(m => ((uint)m).ToString()).ToList(),
                    entries.ToList(),
                    "the reported material list and the written tinker log must be the same sequence");
            }
        }

        [TestMethod]
        public void ApplyToWeapon_AppendsToAnExistingTinkerLogRatherThanReplacingIt()
        {
            // the clean-weapon guard means this cannot happen through normal use, but the log writer
            // is shared shaped with RecipeManager.HandleTinkerLog and must not clobber prior entries
            var weapon = CreateMeleeWeapon(DamageType.Slash);
            var priorEntry = ((uint)MaterialType.Steel).ToString();
            weapon.TinkerLog = priorEntry;

            var result = PrismaticDriftStone.ApplyToWeapon(weapon);

            var entries = weapon.TinkerLog.Split(',');

            Assert.AreEqual(priorEntry, entries[0],
                "the pre-existing tinker log entry must survive");

            Assert.AreEqual(1 + result.NumBoosts + 1, entries.Length,
                $"1 prior + {result.NumBoosts} boosts + 1 rend gem = {1 + result.NumBoosts + 1} entries");
        }

        [TestMethod]
        public void ApplyToWeapon_ResultBoostClassMatchesTheWeaponClass()
        {
            var melee = CreateMeleeWeapon(DamageType.Slash);
            Assert.AreEqual(PrismaticDriftStone.BoostClass.MeleeDamage, PrismaticDriftStone.ApplyToWeapon(melee).BoostClass);

            var launcher = CreateMissileLauncher(DamageType.Cold);
            Assert.AreEqual(PrismaticDriftStone.BoostClass.MissileDamageMod, PrismaticDriftStone.ApplyToWeapon(launcher).BoostClass);

            var caster = CreateCaster(DamageType.Nether);
            Assert.AreEqual(PrismaticDriftStone.BoostClass.CasterElementalDamageMod, PrismaticDriftStone.ApplyToWeapon(caster).BoostClass);
        }

        [TestMethod]
        public void ApplyToWeapon_NetherWand_GetsCriticalStrikeAndCasterBoosts()
        {
            var wand = CreateCaster(DamageType.Nether);
            var result = PrismaticDriftStone.ApplyToWeapon(wand);

            Assert.AreEqual(ImbuedEffectType.CriticalStrike, wand.ImbuedEffect,
                "a nether wand has no rend available");

            Assert.AreEqual(MaterialType.BlackOpal, PrismaticDriftStone.GetRendMaterial(result.Rend),
                "CriticalStrike is retail's Black Opal imbue");

            Assert.AreEqual(PrismaticDriftStone.BoostClass.CasterElementalDamageMod, result.BoostClass,
                "it is still a caster, so it still gets caster boosts");
        }

        [TestMethod]
        public void ApplyToWeapon_ElementlessBow_GetsPierceRendingAndMissileBoosts()
        {
            var bow = CreateMissileLauncher(DamageType.Undef);
            var result = PrismaticDriftStone.ApplyToWeapon(bow);

            Assert.AreEqual(ImbuedEffectType.PierceRending, bow.ImbuedEffect,
                "an elementless bow defaults to PierceRending");

            Assert.AreEqual(PrismaticDriftStone.BoostClass.MissileDamageMod, result.BoostClass,
                "an elementless bow still gets missile boosts");
        }

        // ---------------------------------------------------------------------------------------
        // wcid dispatch
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void IsPrismaticDriftStone_OnlyMatchesItsOwnWcid()
        {
            Assert.IsTrue(PrismaticDriftStone.IsPrismaticDriftStone(PrismaticDriftStone.PrismaticDriftStoneWcid));
            Assert.IsFalse(PrismaticDriftStone.IsPrismaticDriftStone(PrismaticDriftStone.PrismaticDriftStoneWcid + 1));
            Assert.IsFalse(PrismaticDriftStone.IsPrismaticDriftStone((WorldObject)null));
        }

        [TestMethod]
        public void ConfirmationText_FitsUnderTheClientDialogClip()
        {
            // the client confirmation panel silently clips somewhere around 600 characters.
            // check the LONGEST reachable variant: the unaligned-caster warning, with a long name.
            const int clipLimit = 600;

            var longName = "A Weapon With A Fairly Long Name For Testing Purposes";

            var plain = CreateMeleeWeapon(DamageType.Slash);
            plain.Name = longName;

            var warned = CreateCaster(DamageType.Undef);
            warned.Name = longName;

            foreach (var weapon in new WorldObject[] { plain, warned })
            {
                var text = PrismaticDriftStone.GetConfirmationText(weapon);

                Assert.IsTrue(text.Length < clipLimit,
                    $"confirmation text is {text.Length} characters, at or over the ~{clipLimit} clip");

                Assert.IsTrue(text.All(c => c < 128),
                    "confirmation text must be ASCII only (the client is CP1252)");

                Assert.IsFalse(text.Any(c => c == '\u2013' || c == '\u2014'),
                    "confirmation text must not contain an em or en dash");
            }
        }

        // ---------------------------------------------------------------------------------------
        // attunement-foreclosure warning (unaligned casters)
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public void IsUnalignedCaster_OnlyTrueForACasterWithNoDamageTypeBit()
        {
            Assert.IsTrue(PrismaticDriftStone.IsUnalignedCaster(CreateCaster(DamageType.Undef)),
                "a plain wand or orb carries no DamageType bit at all");

            Assert.IsFalse(PrismaticDriftStone.IsUnalignedCaster(CreateCaster(DamageType.Fire)),
                "a fire caster is already aligned");

            Assert.IsFalse(PrismaticDriftStone.IsUnalignedCaster(CreateCaster(DamageType.Nether)),
                "a nether wand is aligned, even though its rend falls back to CriticalStrike");

            Assert.IsFalse(PrismaticDriftStone.IsUnalignedCaster(CreateMissileLauncher(DamageType.Undef)),
                "an elementless bow is not a caster and cannot be attuned");

            Assert.IsFalse(PrismaticDriftStone.IsUnalignedCaster(CreateMeleeWeapon(DamageType.Undef)),
                "an elementless melee weapon is not a caster");

            Assert.IsFalse(PrismaticDriftStone.IsUnalignedCaster(null),
                "a null target is not an unaligned caster");
        }

        [TestMethod]
        public void ConfirmationText_WarnsAboutForeclosedAttunement_OnlyForUnalignedCasters()
        {
            // the warning exists so the CriticalStrike-on-an-unattuned-caster path is an INFORMED
            // choice: an Attuned Drift Prism could have given it an element first, and this stone's
            // permanent lock closes that door
            const string warningMarker = "can never be attuned to an element afterward";

            var unaligned = CreateCaster(DamageType.Undef);

            Assert.IsTrue(PrismaticDriftStone.GetConfirmationText(unaligned).Contains(warningMarker),
                "an unaligned caster must be warned that the lock forecloses attunement");

            var shouldNotWarn = new WorldObject[]
            {
                CreateCaster(DamageType.Fire),
                CreateCaster(DamageType.Nether),
                CreateMissileLauncher(DamageType.Undef),
                CreateMeleeWeapon(DamageType.Slash),
                CreateMeleeWeapon(DamageType.Undef),
            };

            foreach (var weapon in shouldNotWarn)
            {
                Assert.IsFalse(PrismaticDriftStone.GetConfirmationText(weapon).Contains(warningMarker),
                    $"{weapon.GetType().Name} with damage type {weapon.W_DamageType} cannot be attuned by a prism, so it must not carry the warning");
            }
        }

        [TestMethod]
        public void ConfirmationText_WarningIsAdditiveNotAReplacement()
        {
            // the warned variant must still say everything the plain one says
            var plain = PrismaticDriftStone.GetConfirmationText(CreateCaster(DamageType.Fire));
            var warned = PrismaticDriftStone.GetConfirmationText(CreateCaster(DamageType.Undef));

            Assert.IsTrue(warned.Length > plain.Length,
                $"the warned variant ({warned.Length} chars) should be longer than the plain one ({plain.Length} chars)");

            Assert.IsTrue(warned.Contains("can never be tinkered again"),
                "the warning must not displace the permanent-lock sentence");

            Assert.IsTrue(warned.Contains("The stone is consumed."),
                "the warning must not displace the consumption sentence");
        }
    }
}
