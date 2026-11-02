using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The quality-tier aura and a weapon's OWN baked VisualEffectScript (PropertyDataId 9000) share one slot.
    /// WeaponModManager.ApplyTierScript: the tier aura wins while the weapon holds a tier, and a fall back to
    /// None restores the weenie TEMPLATE's base script instead of removing the row (the Olthoi Voidclaw 1007600's
    /// black smoke 0x33001154 was being erased by a Tourmaline reroll that crossed and then dropped below the
    /// Exceptional threshold). A weapon with no base row still ends with none.
    ///
    /// The template is read through WeaponModManager.TemplateWeenieReader, stubbed here and restored in finally.
    /// </summary>
    [TestClass]
    public class WeaponModTierBaseScriptTests
    {
        private const uint BaseScript = 0x33001154;

        private static uint nextGuid = 0x7E9A0000;
        private static uint nextWcid = 993900;

        private static (WorldObject weapon, Weenie template) MakeWeapon(uint? baseScript)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Weapon" } },
                PropertiesDID = new Dictionary<PropertyDataId, uint>(),
            };

            if (baseScript.HasValue)
                weenie.PropertiesDID[PropertyDataId.VisualEffectScript] = baseScript.Value;

            return (new Clothing(weenie, new ObjectGuid(nextGuid++)), weenie);
        }

        private static void WithTemplate(Weenie template, System.Action body)
        {
            var original = WeaponModManager.TemplateWeenieReader;
            try
            {
                WeaponModManager.TemplateWeenieReader = wcid => wcid == template.WeenieClassId ? template : null;
                body();
            }
            finally
            {
                WeaponModManager.TemplateWeenieReader = original;
            }
        }

        [TestMethod]
        public void BaseScript_NoneToExceptionalToNone_TierWinsThenTheBaseScriptReturns()
        {
            var (weapon, template) = MakeWeapon(BaseScript);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional);

            Assert.IsTrue(aura.HasValue, "the Exceptional tier carries an aura");
            Assert.AreNotEqual(BaseScript, aura.Value, "the aura must differ from the base script or the test proves nothing");
            Assert.AreEqual(BaseScript, weapon.VisualEffectScript, "the weapon starts with its baked script");

            WithTemplate(template, () =>
            {
                WeaponModManager.ApplyTierScript(weapon, WeaponQualityTier.None, WeaponQualityTier.Exceptional);
                Assert.AreEqual(aura, weapon.VisualEffectScript, "while at tier, the tier aura wins over the base script");

                WeaponModManager.ApplyTierScript(weapon, WeaponQualityTier.Exceptional, WeaponQualityTier.None);
                Assert.AreEqual(BaseScript, weapon.VisualEffectScript, "back at None, the template's base script is restored");
            });
        }

        [TestMethod]
        public void NoBaseScript_NoneToExceptionalToNone_EndsWithNoRow()
        {
            var (weapon, template) = MakeWeapon(null);
            var aura = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional);

            Assert.IsNull(weapon.VisualEffectScript);

            WithTemplate(template, () =>
            {
                WeaponModManager.ApplyTierScript(weapon, WeaponQualityTier.None, WeaponQualityTier.Exceptional);
                Assert.AreEqual(aura, weapon.VisualEffectScript);

                WeaponModManager.ApplyTierScript(weapon, WeaponQualityTier.Exceptional, WeaponQualityTier.None);
                Assert.IsNull(weapon.VisualEffectScript, "no base row: the drop to None removes the row, as before");
            });
        }

        [TestMethod]
        public void BaseScript_MissingTemplate_DropToNoneRemovesTheRow()
        {
            var (weapon, _) = MakeWeapon(BaseScript);
            var original = WeaponModManager.TemplateWeenieReader;
            try
            {
                WeaponModManager.TemplateWeenieReader = wcid => throw new System.InvalidOperationException("no world db");

                WeaponModManager.ApplyTierScript(weapon, WeaponQualityTier.None, WeaponQualityTier.Exceptional);
                WeaponModManager.ApplyTierScript(weapon, WeaponQualityTier.Exceptional, WeaponQualityTier.None);
                Assert.IsNull(weapon.VisualEffectScript, "an unreadable template reads as no base script");
            }
            finally
            {
                WeaponModManager.TemplateWeenieReader = original;
            }
        }
    }
}
