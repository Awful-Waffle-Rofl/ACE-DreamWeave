using System;
using System.IO;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// The upgraded cloak's magnitudes: Cloak.ProcStatModValue (BuildEntry), Cloak.RefreshedProcStatModValue (the refresh
    /// branch of EnchantmentManager.Add) and Cloak.DamageReductionBase. Every retail case must return the input unchanged.
    /// The two EnchantmentManager sites are pinned by a source scan because BuildEntry needs a live Spell row.
    /// </summary>
    [TestClass]
    public class CloakProcMagnitudeTests
    {
        private const uint CloakedInSkill = (uint)SpellId.CloakAllSkill;

        private static ACE.Server.WorldObjects.WorldObject StampedCloak(uint procSpell, double? value)
        {
            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.ProcSpell = procSpell;

            if (value != null)
                cloak.SetProperty(PropertyFloat.CloakProcStatModValue, value.Value);

            return cloak;
        }

        [TestMethod]
        public void ProcStatModValue_NullWeapon_IsTheSpellValue()
            => Assert.AreEqual(20f, Cloak.ProcStatModValue(null, CloakedInSkill, 20f));

        [TestMethod]
        public void ProcStatModValue_RetailCloak_IsTheSpellValue()
            => Assert.AreEqual(20f, Cloak.ProcStatModValue(StampedCloak(CloakedInSkill, null), CloakedInSkill, 20f));

        [TestMethod]
        public void ProcStatModValue_StampedCloak_OwnProcSpell_IsTheStamp()
            => Assert.AreEqual(40f, Cloak.ProcStatModValue(StampedCloak(CloakedInSkill, 40), CloakedInSkill, 20f));

        [TestMethod]
        public void ProcStatModValue_NonCloakCarryingTheProperty_IsTheSpellValue()
        {
            var amulet = UpgradedCloakTestItems.Item(cloak: false);
            amulet.ProcSpell = CloakedInSkill;
            amulet.SetProperty(PropertyFloat.CloakProcStatModValue, 40.0);

            Assert.IsFalse(Cloak.IsCloak(amulet), "control: the fixture is not a cloak");
            Assert.AreEqual(20f, Cloak.ProcStatModValue(amulet, CloakedInSkill, 20f));
        }

        [TestMethod]
        public void ProcStatModValue_OtherSpellThanProcSpell_IsTheSpellValue()
        {
            var cloak = StampedCloak(CloakedInSkill, 40);

            // a set spell, or anything else the cloak casts, keeps its own value
            Assert.AreEqual(10f, Cloak.ProcStatModValue(cloak, 5697, 10f));
        }

        [TestMethod]
        public void Refresh_RetailCloakRefreshingAPlus40Entry_DropsBackToTheSpellValue()
            => Assert.AreEqual(20f, Cloak.RefreshedProcStatModValue(StampedCloak(CloakedInSkill, null), CloakedInSkill, 40f, 20f));

        [TestMethod]
        public void Refresh_UpgradedCloakRefreshingAPlus20Entry_RaisesItToTheStamp()
            => Assert.AreEqual(40f, Cloak.RefreshedProcStatModValue(StampedCloak(CloakedInSkill, 40), CloakedInSkill, 20f, 20f));

        [TestMethod]
        public void Refresh_RetailCloakOwnNonSkillProc_KeepsExisting()
        {
            // a Shroud proc refreshed by its own retail cloak: the existing (possibly BuildEntry-transformed) value stays
            Assert.AreEqual(33f, Cloak.RefreshedProcStatModValue(StampedCloak((uint)SpellId.CloakMagicDLower, null), (uint)SpellId.CloakMagicDLower, 33f, 10f));
        }

        [TestMethod]
        public void Refresh_StampedCloakOwnNonSkillProc_KeepsExisting()
        {
            // even a stamped value on the cloak never reaches a spell other than Cloaked in Skill on refresh
            Assert.AreEqual(33f, Cloak.RefreshedProcStatModValue(StampedCloak((uint)SpellId.CloakMeleeDLower, 40), (uint)SpellId.CloakMeleeDLower, 33f, 10f));
        }

        [TestMethod]
        public void Refresh_NotACloakProc_KeepsTheExistingValue()
        {
            // the pre-existing refresh behaviour for every non-cloak caster: the entry keeps its original StatModValue
            Assert.AreEqual(33f, Cloak.RefreshedProcStatModValue(null, CloakedInSkill, 33f, 20f));
            Assert.AreEqual(33f, Cloak.RefreshedProcStatModValue(StampedCloak(CloakedInSkill, 40), 5697, 33f, 10f));

            var amulet = UpgradedCloakTestItems.Item(cloak: false);
            amulet.ProcSpell = CloakedInSkill;
            amulet.SetProperty(PropertyFloat.CloakProcStatModValue, 40.0);
            Assert.AreEqual(33f, Cloak.RefreshedProcStatModValue(amulet, CloakedInSkill, 33f, 20f));
        }

        [TestMethod]
        public void DamageReductionBase_NullOrRetail_Is200_StampedIsTheStamp()
        {
            Assert.AreEqual(200, Cloak.DamageReductionAmount);
            Assert.AreEqual(200, Cloak.DamageReductionBase(null));
            Assert.AreEqual(200, Cloak.DamageReductionBase(UpgradedCloakTestItems.Item(cloak: true)));

            var stamped = UpgradedCloakTestItems.Item(cloak: true);
            stamped.SetProperty(PropertyInt.CloakDamageReductionAmount, 400);
            Assert.AreEqual(400, Cloak.DamageReductionBase(stamped));
        }

        [TestMethod]
        public void PropertyIds_ArePinned()
        {
            Assert.AreEqual(9096, (int)PropertyInt.CloakDamageReductionAmount);
            Assert.AreEqual(9097, (int)PropertyInt.SetBonusMaxLevel);
            Assert.AreEqual(9019, (int)PropertyFloat.CloakProcStatModValue);
        }

        [TestMethod]
        public void EnchantmentManager_AppliesTheMagnitudeAtBuildAndRefresh()
        {
            var text = Source("Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs");

            StringAssert.Matches(text, new Regex(@"entry\.StatModValue = spell\.StatModVal;\s*(//[^\n]*\n\s*)*entry\.StatModValue = Cloak\.ProcStatModValue\(weapon, spell\.Id, entry\.StatModValue\);"), "BuildEntry must apply the cloak magnitude right after the spell value");
            StringAssert.Contains(text, "refreshSpell.StatModValue = Cloak.RefreshedProcStatModValue(weapon, spell.Id, refreshSpell.StatModValue, spell.StatModVal);");
        }

        [TestMethod]
        public void GetDamageReductionAmount_ReadsTheCloakBase()
            => StringAssert.Contains(Source("Source/ACE.Server/Entity/Cloak.cs"), "ApplyCloakDamageReduction(source, defender, DamageReductionBase(WornCloak(defender)))");

        internal static string Source(string repoRelativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, repoRelativePath);

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n");
            }

            Assert.Fail($"Could not find {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
