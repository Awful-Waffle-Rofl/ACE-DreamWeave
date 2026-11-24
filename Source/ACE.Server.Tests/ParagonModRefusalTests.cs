using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WeaponMods;

using R = ACE.Server.WeaponMods.ParagonModManager.ParagonModRefusal;

namespace ACE.Server.Tests
{
    /// <summary>The pure rule table for the gem and the reset item: every refusal, in order, with no Player.</summary>
    [TestClass]
    public class ParagonModRefusalTests
    {
        private const int Melee140 = 140;

        /// <summary>A passing gem use, with each argument overridable.</summary>
        private static R Gem(bool enabled = true, bool sourceInPack = true, bool targetInPack = true, WeaponClass weaponClass = WeaponClass.Melee,
            int? setId = Melee140, int? maxLevel = 50, int? level = -1, int? filled = null, int? mod = (int)WeaponModId.Devastation, bool readsBack = true)
        {
            // level -1 means "fully leveled": the same as the max
            var effectiveLevel = level == -1 ? maxLevel : level;

            return ParagonModManager.ResolveGemRefusal(enabled, sourceInPack, targetInPack, weaponClass, setId, maxLevel,
                effectiveLevel, filled ?? (maxLevel.HasValue ? maxLevel.Value - 50 : 0), mod, ACE.Entity.Enum.ItemXpStyle.Fixed, readsBack);
        }

        [TestMethod]
        public void BaselineCase_Passes()
        {
            Assert.AreEqual(R.None, Gem());
            Assert.AreEqual(R.None, Gem(maxLevel: 54, filled: 4));
        }

        [TestMethod]
        public void GateOff_Refuses()
        {
            Assert.AreEqual(R.Disabled, Gem(enabled: false));
        }

        [TestMethod]
        public void NotInPack_Refuses_ForEitherItem()
        {
            Assert.AreEqual(R.SourceNotInPack, Gem(sourceInPack: false));
            Assert.AreEqual(R.TargetNotInPack, Gem(targetInPack: false), "an equipped weapon is not in the pack");
        }

        [TestMethod]
        public void NotAWeapon_Refuses()
        {
            Assert.AreEqual(R.NotAWeapon, Gem(weaponClass: WeaponClass.None));
        }

        [TestMethod]
        public void WrongSet_Refuses_AndAllThreeParagonSetsPass()
        {
            Assert.AreEqual(R.NotParagonSet, Gem(setId: null));
            Assert.AreEqual(R.NotParagonSet, Gem(setId: 137));
            Assert.AreEqual(R.NotParagonSet, Gem(setId: 141));

            foreach (var set in new[] { 138, 139, 140 })
                Assert.AreEqual(R.None, Gem(setId: set), $"set {set}");
        }

        [TestMethod]
        public void UnsupportedXpStyle_Refuses_OnlyFixedAndFixedPlusBasePass()
        {
            const int Weapon = 50;

            foreach (var style in new ACE.Entity.Enum.ItemXpStyle?[] { ACE.Entity.Enum.ItemXpStyle.Fixed, ACE.Entity.Enum.ItemXpStyle.FixedPlusBase })
            {
                Assert.AreEqual(R.None, ParagonModManager.ResolveGemRefusal(true, true, true, WeaponClass.Melee, Melee140, Weapon, Weapon, 0,
                    (int)WeaponModId.Devastation, style, true), style.ToString());
            }

            foreach (var style in new ACE.Entity.Enum.ItemXpStyle?[] { ACE.Entity.Enum.ItemXpStyle.ScalesWithLevel, null })
            {
                Assert.AreEqual(R.UnsupportedXpStyle, ParagonModManager.ResolveGemRefusal(true, true, true, WeaponClass.Melee, Melee140, Weapon, Weapon, 0,
                    (int)WeaponModId.Devastation, style, true), style.ToString());
            }
        }

        [TestMethod]
        public void LevelRange_Refuses_Outside50To54()
        {
            Assert.AreEqual(R.NotParagonLevel, Gem(maxLevel: null));
            Assert.AreEqual(R.NotParagonLevel, Gem(maxLevel: 49));
            Assert.AreEqual(R.NotParagonLevel, Gem(maxLevel: 56));
        }

        [TestMethod]
        public void Max55_Refuses_AsAtMaxGems()
        {
            Assert.AreEqual(R.AtMaxGems, Gem(maxLevel: 55, filled: 5));
        }

        [TestMethod]
        public void NotFullyLeveled_Refuses()
        {
            Assert.AreEqual(R.NotFullyLeveled, Gem(maxLevel: 52, level: 51, filled: 2));
            Assert.AreEqual(R.NotFullyLeveled, Gem(maxLevel: 50, level: 0));
            Assert.AreEqual(R.NotFullyLeveled, Gem(maxLevel: 50, level: null), "a weapon with no item level is not fully leveled");
        }

        [TestMethod]
        public void SlotDesync_Refuses()
        {
            Assert.AreEqual(R.SlotDesync, Gem(maxLevel: 52, filled: 1), "level says 2 gems, slots hold 1");
            Assert.AreEqual(R.SlotDesync, Gem(maxLevel: 50, filled: 1), "slots hold a gem the level does not account for");
            Assert.AreEqual(R.SlotDesync, Gem(maxLevel: 52, filled: -1), "malformed slots (a gap)");
        }

        [TestMethod]
        public void XpWouldOverflow_Refuses_AfterSlotDesync_AndBeforeTheMod()
        {
            Assert.AreEqual(R.XpWouldOverflow, Gem(readsBack: false));
            Assert.AreEqual(R.SlotDesync, Gem(maxLevel: 52, filled: 1, readsBack: false), "earlier rules win");
            Assert.AreEqual(R.XpWouldOverflow, Gem(mod: 9999, readsBack: false), "reported before the mod is looked at");
        }

        [TestMethod]
        public void UnknownMod_Refuses()
        {
            Assert.AreEqual(R.UnknownMod, Gem(mod: null));
            Assert.AreEqual(R.UnknownMod, Gem(mod: 9999));
        }

        [TestMethod]
        public void WrongClass_Refuses_TensionIsMissileOnly_LeverageIsMeleeOnly()
        {
            Assert.AreEqual(R.WrongClass, Gem(weaponClass: WeaponClass.Melee, mod: (int)WeaponModId.Tension));
            Assert.AreEqual(R.None, Gem(weaponClass: WeaponClass.Missile, mod: (int)WeaponModId.Tension));

            Assert.AreEqual(R.WrongClass, Gem(weaponClass: WeaponClass.Missile, mod: (int)WeaponModId.Leverage));
            Assert.AreEqual(R.WrongClass, Gem(weaponClass: WeaponClass.Caster, mod: (int)WeaponModId.Leverage));
            Assert.AreEqual(R.None, Gem(weaponClass: WeaponClass.Melee, mod: (int)WeaponModId.Leverage));
        }

        [TestMethod]
        public void Order_GateBeforePackBeforeWeaponState()
        {
            Assert.AreEqual(R.Disabled, Gem(enabled: false, sourceInPack: false, setId: null));
            Assert.AreEqual(R.SourceNotInPack, Gem(sourceInPack: false, targetInPack: false));
            Assert.AreEqual(R.NotParagonSet, Gem(setId: 1, maxLevel: 55, filled: 5));
        }

        // ---------------- reset ----------------

        private static R Reset(bool enabled = true, bool sourceInPack = true, bool targetInPack = true, WeaponClass weaponClass = WeaponClass.Melee,
            int? setId = Melee140, int filled = 2, int refund = 0, uint? currency = null, bool room = true) =>
            ParagonModManager.ResolveResetRefusal(enabled, sourceInPack, targetInPack, weaponClass, setId, filled, refund, currency, room);

        [TestMethod]
        public void Reset_Refusals()
        {
            Assert.AreEqual(R.None, Reset());
            Assert.AreEqual(R.Disabled, Reset(enabled: false));
            Assert.AreEqual(R.SourceNotInPack, Reset(sourceInPack: false));
            Assert.AreEqual(R.TargetNotInPack, Reset(targetInPack: false));
            Assert.AreEqual(R.NotAWeapon, Reset(weaponClass: WeaponClass.None));
            Assert.AreEqual(R.NotParagonSet, Reset(setId: 5));
            Assert.AreEqual(R.NothingToReset, Reset(filled: 0));
        }

        [TestMethod]
        public void ParagonSets_AreExactly138_139_140()
        {
            CollectionAssert.AreEqual(new[] { 138, 139, 140 }, ParagonModManager.ParagonSetIds);
        }
    }
}
