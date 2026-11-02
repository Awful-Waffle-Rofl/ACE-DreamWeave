using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Spellsword class-ability war procs read the caster's Item Enchantment for their resist roll, imbue
    /// scaling (attackSkill) and skill damage bonus; every other projectile keeps reading Spell.School.
    /// A full impact needs a live landblock, so these test the selection helpers directly (dat-free: the
    /// proc branch never touches Spell) and pin the non-proc path and the TryResistSpell default by source scan.
    /// Reads no PropertyManager keys.
    /// </summary>
    [TestClass]
    public class SpellswordProcSkillTests
    {
        private static Creature NewCreature() =>
            new Creature(new Weenie { WeenieClassId = 994200, WeenieType = WeenieType.Creature }, new ObjectGuid(0x7F420001));

        private static uint nextWcid = 994100;

        private static SpellProjectile NewProjectile()
        {
            var weenie = new Weenie { WeenieClassId = nextWcid++, WeenieType = WeenieType.ProjectileSpell };
            return new SpellProjectile(weenie, new ObjectGuid(0x7F410000 + nextWcid));
        }

        [TestMethod]
        public void ProcProjectile_SelectsItemEnchantment_ForResistAttackSkillAndBonus()
        {
            var proc = NewProjectile();
            proc.IsClassAbilityProc = true;

            Assert.AreEqual(Skill.ItemEnchantment, proc.CasterSkillOverride);

            TestGameTables.EnsureInitialized();   // a bare Creature needs the synthetic dat tables

            var caster = NewCreature();
            var picked = proc.GetCasterMagicCreatureSkill(caster);

            Assert.AreSame(caster.GetCreatureSkill(Skill.ItemEnchantment), picked, "the proc reads the caster's Item Enchantment");
            Assert.AreNotSame(caster.GetCreatureSkill(Skill.WarMagic), picked);
            Assert.AreNotSame(caster.GetCreatureSkill(Skill.VoidMagic), picked);
        }

        [TestMethod]
        public void NonProcProjectile_HasNoOverride_AndPicksTheSpellSchoolSkill()
        {
            Assert.IsNull(NewProjectile().CasterSkillOverride, "an ordinary projectile has no skill override");

            TestGameTables.EnsureInitialized();

            var caster = NewCreature();

            // The pure selection GetCasterMagicCreatureSkill delegates to, with the override a non-proc yields (null).
            Assert.AreSame(caster.GetCreatureSkill(Skill.WarMagic), SpellProjectile.PickCasterSkill(caster, null, MagicSchool.WarMagic));
            Assert.AreSame(caster.GetCreatureSkill(Skill.VoidMagic), SpellProjectile.PickCasterSkill(caster, null, MagicSchool.VoidMagic));

            // And a proc's override wins over whatever the school is.
            var proc = NewProjectile();
            proc.IsClassAbilityProc = true;
            Assert.AreSame(caster.GetCreatureSkill(Skill.ItemEnchantment), SpellProjectile.PickCasterSkill(caster, proc.CasterSkillOverride, MagicSchool.WarMagic));
            Assert.AreSame(caster.GetCreatureSkill(Skill.ItemEnchantment), SpellProjectile.PickCasterSkill(caster, proc.CasterSkillOverride, MagicSchool.VoidMagic));
        }

        [TestMethod]
        public void ProcAttackSkill_ClassifiesAsMagic_SoWeaponImbuesScale()
        {
            // The real chain: the skill a proc hands to GetWeaponCritDamageMod / GetWeaponMagicCritFrequency /
            // GetWeaponResistanceModifier must classify as Magic, or CS, CB and Rending silently do nothing.
            TestGameTables.EnsureInitialized();

            var proc = NewProjectile();
            proc.IsClassAbilityProc = true;

            Assert.AreEqual(WorldObject.ImbuedSkillType.Magic, WorldObject.GetImbuedSkillType(proc.GetCasterMagicCreatureSkill(NewCreature())));
        }

        [TestMethod]
        public void EchoOfAProc_InheritsItemEnchantment_WithoutBecomingAProc()
        {
            var parent = NewProjectile();
            parent.IsClassAbilityProc = true;

            var echo = NewProjectile();
            ACE.Server.ClassAbilities.Abilities.EchoCastAbility.StampEchoCopy(echo, parent);

            Assert.AreEqual(Skill.ItemEnchantment, echo.CasterSkillOverride, "an echo of a proc reads the proc's skill");
            Assert.IsFalse(echo.IsClassAbilityProc, "the echo must not become a proc - IsClassAbilityProc is Cascade's gate");

            // An echo of an ordinary cast keeps the spell's school.
            var plainEcho = NewProjectile();
            ACE.Server.ClassAbilities.Abilities.EchoCastAbility.StampEchoCopy(plainEcho, NewProjectile());

            Assert.IsNull(plainEcho.CasterSkillOverride);
            Assert.IsFalse(plainEcho.InheritsProcCasterSkill);
        }

        [TestMethod]
        public void SpellProjectile_EverySiteReadsTheOneOverrideMember()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs");

            StringAssert.Contains(src, "public Skill? CasterSkillOverride => IsClassAbilityProc || InheritsProcCasterSkill ? Skill.ItemEnchantment : (Skill?)null;");
            StringAssert.Contains(src, "PickCasterSkill(caster, CasterSkillOverride, CasterSkillOverride.HasValue ? default : Spell.School);");
            StringAssert.Contains(src, "attackSkill = GetCasterMagicCreatureSkill(sourceCreature);");
            StringAssert.Contains(src, "var magicSkill = GetCasterMagicCreatureSkill(sourcePlayer).Current;");
            StringAssert.Contains(src, "source.TryResistSpell(target, Spell, resistSource, true, CasterSkillOverride);");

            // Not routed through the helper: proficiency credit keeps the spell's own school.
            StringAssert.Contains(src, "Proficiency.OnSuccessUse(player, player.GetCreatureSkill(Spell.School), Spell.PowerMod);");
        }

        [TestMethod]
        public void TryResistSpell_WithNoOverride_ReadsSpellSchool_AndItemAndWielderBranchesUnchanged()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs");

            StringAssert.Contains(src, "bool projectileHit = false, Skill? casterSkillOverride = null)");
            StringAssert.Contains(src, "casterSkillOverride.HasValue ? casterCreature.GetCreatureSkill(casterSkillOverride.Value) : casterCreature.GetCreatureSkill(spell.School)");
            StringAssert.Contains(src, "magicSkill = (uint)caster.ItemSpellcraft;");
            StringAssert.Contains(src, "magicSkill = wielder.GetCreatureSkill(spell.School).Current;");
        }
    }
}