using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The tinker-locked-shield exception (owner ruling 2026-11-25; today only the Shield of the Rose, 1007451): a target
    /// that is TinkerLocked AND a shield admits Tourmaline and Amethyst (only while weapon_mods_enabled is on) and Black Opal
    /// and Fire Opal (regardless). Workmanship is data (the weenie row and a shard
    /// migration), not code, so nothing here writes it. A locked weapon is unaffected, and every other source stays refused.
    /// </summary>
    [TestClass]
    public class TinkerLockedShieldTests
    {
        private static uint nextWcid = 9_410_000;
        private static uint nextGuid = 0x7E3A0000;

        private bool savedWeaponMods;

        [ClassInitialize]
        public static void Setup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            savedWeaponMods = PropertyManager.GetBool("weapon_mods_enabled").Item;
            PropertyManager.ModifyBool("weapon_mods_enabled", true);
        }

        [TestCleanup]
        public void Restore()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", savedWeaponMods);
        }

        // ---- the pure core -----------------------------------------------------------------------------------------

        /// <summary>(sourceIsWeaponModMaterial) for each of the four shield materials: Tourmaline and Amethyst are weapon-mod salvage, the opals are not.</summary>
        private static readonly (string name, bool isWeaponModMaterial)[] Four =
        {
            ("Tourmaline", true), ("Amethyst", true), ("Black Opal", false), ("Fire Opal", false),
        };

        private static bool Refused(bool locked, bool shield, bool widens, bool modMaterial, bool enabled,
            bool drift = false, bool manaStone = false, bool cookbook = false, bool paragon = false)
        {
            // the production shape: the exception's answer is computed once and handed to the core
            var admits = TinkerLock.LockedShieldAdmits(locked, shield, widens, modMaterial, enabled);

            return TinkerLock.IsRefused(targetLocked: locked, lockedShieldAdmits: admits, targetIsDriftAtlan: drift, sourceIsManaStone: manaStone,
                hasCookbookRow: cookbook, sourceIsWeaponModMaterial: modMaterial, weaponModsEnabled: enabled, sourceIsParagonModSource: paragon);
        }

        [TestMethod]
        public void LockedShield_EachOfTheFour_WeaponModsOn_IsAdmitted()
        {
            foreach (var (name, modMaterial) in Four)
            {
                Assert.IsTrue(TinkerLock.LockedShieldAdmits(true, true, true, modMaterial, true), name);
                Assert.IsFalse(Refused(locked: true, shield: true, widens: true, modMaterial: modMaterial, enabled: true), name);
            }
        }

        [TestMethod]
        public void LockedShield_WeaponModsOff_RefusesTourmalineAndAmethyst_AdmitsTheOpals()
        {
            foreach (var (name, modMaterial) in Four)
            {
                Assert.AreEqual(!modMaterial, TinkerLock.LockedShieldAdmits(true, true, true, modMaterial, false), name);
                Assert.AreEqual(modMaterial, Refused(locked: true, shield: true, widens: true, modMaterial: modMaterial, enabled: false), name);
            }
        }

        [TestMethod]
        public void LockedWeapon_IsNotAffected_ByTheShieldException()
        {
            foreach (var (name, modMaterial) in Four)
            {
                Assert.IsFalse(TinkerLock.LockedShieldAdmits(true, false, true, modMaterial, true), name);
                Assert.IsTrue(Refused(locked: true, shield: false, widens: true, modMaterial: modMaterial, enabled: true), name + " on a locked weapon");
            }
        }

        [TestMethod]
        public void LockedShield_OtherSalvage_StaysRefused()
        {
            Assert.IsFalse(TinkerLock.LockedShieldAdmits(true, true, false, false, true));
            Assert.IsTrue(Refused(locked: true, shield: true, widens: false, modMaterial: false, enabled: true), "e.g. Steel");
            Assert.IsTrue(Refused(locked: true, shield: true, widens: false, modMaterial: false, enabled: false));

            // the lock's own exemptions still stand
            Assert.IsFalse(Refused(locked: true, shield: true, widens: false, modMaterial: false, enabled: true, manaStone: true), "mana stone");
            Assert.IsFalse(Refused(locked: true, shield: true, widens: false, modMaterial: false, enabled: true, cookbook: true), "exact cook_book pair");
        }

        [TestMethod]
        public void NotLocked_IsUnchanged()
        {
            foreach (var shield in new[] { true, false })
                foreach (var widens in new[] { true, false })
                    foreach (var enabled in new[] { true, false })
                    {
                        Assert.IsFalse(TinkerLock.LockedShieldAdmits(false, shield, widens, false, enabled), "the exception needs the lock");
                        Assert.AreEqual(TinkerLock.IsRefused(false, false, false, false, false, enabled),
                            Refused(locked: false, shield: shield, widens: widens, modMaterial: false, enabled: enabled));
                        Assert.IsFalse(Refused(locked: false, shield: shield, widens: widens, modMaterial: false, enabled: enabled));
                    }
        }

        [TestMethod]
        public void TheDriftRule_StillAppliesOnItsOwnTerms()
        {
            // The Rose carries neither DriftAtlanWeapon nor ModSalvageAllowMask; if a locked shield ever did, the Drift rule
            // still refuses a material it does not list (an opal) even though the shield exception admitted it.
            Assert.IsTrue(Refused(locked: true, shield: true, widens: true, modMaterial: false, enabled: true, drift: true), "opal on a Drift shield");
            Assert.IsFalse(Refused(locked: true, shield: true, widens: true, modMaterial: true, enabled: true, drift: true), "Tourmaline on a Drift shield");
        }

        [TestMethod]
        public void RefusalText_PureSelection()
        {
            Assert.AreEqual("The glass and thread of this piece will take only Tourmaline, Amethyst, Black Opal and Fire Opal.",
                TinkerLock.RefusalMessage(1u, false, "Shield of the Rose", targetIsKnotItem: true, targetIsLockedShield: true));
            Assert.AreEqual("The glass and thread of this piece will not take salvage.",
                TinkerLock.RefusalMessage(1u, false, "Knot Gauntlets", targetIsKnotItem: true), "every other Knot item keeps the old text");
            Assert.AreEqual(string.Format(TinkerLock.SiraluunRefusalFormat, "Locked Shield"),
                TinkerLock.RefusalMessage(1u, false, "Locked Shield", targetIsKnotItem: false, targetIsLockedShield: true), "a non-Knot locked shield keeps Siraluun");
        }

        [TestMethod]
        public void RefusalText_Production_KnotLockedShieldOnly()
        {
            var steel = Bag(MaterialType.Steel);

            Assert.AreEqual("The glass and thread of this piece will take only Tourmaline, Amethyst, Black Opal and Fire Opal.",
                TinkerLock.RefusalMessage(steel, Knot(Rose())), "a Knot tinker-locked shield");
            Assert.AreEqual(TinkerLock.KnotRefusalText, TinkerLock.RefusalMessage(steel, Knot(LockedSword())), "a Knot locked weapon");
            Assert.AreEqual(TinkerLock.KnotRefusalText,
                TinkerLock.RefusalMessage(steel, Knot(Make(ItemType.Armor, combatUse: CombatUse.Shield))), "a Knot shield that is not locked");
            Assert.AreEqual(TinkerLock.KnotRefusalText, TinkerLock.RefusalMessage(steel, Knot(Make(ItemType.Armor, locked: true))), "a Knot locked non-shield armor piece");
        }

        private static WorldObject Knot(WorldObject item)
        {
            item.SetProperty(PropertyFloat.KnotTraitShieldTrade, 0.5);
            Assert.IsTrue(TinkerLock.IsKnotItem(item), "harness: a Knot item");
            return item;
        }

        // ---- the production predicate over real objects ------------------------------------------------------------

        private static WorldObject Make(ItemType itemType, MaterialType? material = null, CombatUse? combatUse = null, bool locked = false,
            uint? wcid = null)
        {
            var ints = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)itemType } };

            if (material.HasValue)
                ints[PropertyInt.MaterialType] = (int)material.Value;
            if (combatUse.HasValue)
                ints[PropertyInt.CombatUse] = (int)combatUse.Value;

            var weenie = new Weenie
            {
                WeenieClassId = wcid ?? nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = ints,
                PropertiesBool = locked ? new Dictionary<PropertyBool, bool> { { PropertyBool.TinkerLocked, true } } : new Dictionary<PropertyBool, bool>(),
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Rose() => Make(ItemType.Armor, combatUse: CombatUse.Shield, locked: true);
        private static WorldObject LockedSword() => Make(ItemType.MeleeWeapon, combatUse: CombatUse.Melee, locked: true);
        private static WorldObject Bag(MaterialType m) => Make(ItemType.TinkeringMaterial, m);

        private static IEnumerable<(string name, WorldObject source)> FourSources()
        {
            yield return ("Tourmaline bag", Bag(MaterialType.Tourmaline));
            yield return ("Amethyst bag", Bag(MaterialType.Amethyst));
            yield return ("Black Opal bag", Bag(MaterialType.BlackOpal));
            yield return ("Fire Opal bag", Bag(MaterialType.FireOpal));
            yield return ("foolproof Black Opal", Make(ItemType.Misc, MaterialType.BlackOpal, wcid: 36621));
            yield return ("foolproof Fire Opal", Make(ItemType.Misc, MaterialType.FireOpal, wcid: 36623));
        }

        [TestMethod]
        public void ProductionIsRefused_UsesTheSameLockedShieldAdmits_SourceScan()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Entity/TinkerLock.cs");
            var body = PooledLootSourceText.MethodBody(source, "public static bool IsRefused(WorldObject source, WorldObject target)");

            StringAssert.Contains(body, "var lockedShieldAdmits = LockedShieldAdmits(source, target);");
            StringAssert.Contains(body, "if (IsRefused(targetLocked, lockedShieldAdmits, ");
        }

        [TestMethod]
        public void IsLockedShield_KeysOnTinkerLockedAndShield()
        {
            Assert.IsTrue(TinkerLock.IsLockedShield(Rose()));
            Assert.IsFalse(TinkerLock.IsLockedShield(LockedSword()));
            Assert.IsFalse(TinkerLock.IsLockedShield(Make(ItemType.Armor, combatUse: CombatUse.Shield)));
            Assert.IsFalse(TinkerLock.IsLockedShield(null));
        }

        [TestMethod]
        public void Production_LockedShield_AdmitsTheFourAndTheFoolproofBags()
        {
            foreach (var (name, source) in FourSources())
                Assert.IsTrue(TinkerLock.LockedShieldAdmits(source, Rose()), name);
        }

        [TestMethod]
        public void Production_WeaponModsOff_OnlyTheOpalsAreAdmitted()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", false);

            foreach (var (name, source) in FourSources())
            {
                var isOpal = source.MaterialType == MaterialType.BlackOpal || source.MaterialType == MaterialType.FireOpal;
                Assert.AreEqual(isOpal, TinkerLock.LockedShieldAdmits(source, Rose()), name);
            }
        }

        [TestMethod]
        public void Production_LockedWeaponOrOtherSalvageOrUnlockedShield_IsNotAdmitted()
        {
            foreach (var (name, source) in FourSources())
                Assert.IsFalse(TinkerLock.LockedShieldAdmits(source, LockedSword()), name + " on a locked sword");

            Assert.IsFalse(TinkerLock.LockedShieldAdmits(Bag(MaterialType.Steel), Rose()), "Steel");
            Assert.IsFalse(TinkerLock.LockedShieldAdmits(Make(ItemType.Misc, MaterialType.BlackOpal), Rose()), "a non-foolproof Misc opal");
            Assert.IsFalse(TinkerLock.LockedShieldAdmits(Bag(MaterialType.Tourmaline), Make(ItemType.Armor, combatUse: CombatUse.Shield)),
                "an unlocked shield needs no exception");
        }
    }
}
