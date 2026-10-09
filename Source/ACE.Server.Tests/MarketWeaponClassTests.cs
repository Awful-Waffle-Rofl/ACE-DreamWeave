using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// MarketWeaponClass.Classify is pure and takes primitives, so these tests never need a
    /// WorldObject or a Weenie - they exercise Classify (and Label) directly, plus one round-trip
    /// through MarketSnapshot's serialization to prove the JSON keys the web app depends on.
    /// </summary>
    [TestClass]
    public class MarketWeaponClassTests
    {
        // ---- rule 5: modern melee skills ----

        [TestMethod]
        public void Classify_LightWeapons_ReturnsLight()
            => Assert.AreEqual("light", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.LightWeapons, null, null));

        [TestMethod]
        public void Classify_HeavyWeapons_ReturnsHeavy()
            => Assert.AreEqual("heavy", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.HeavyWeapons, null, null));

        [TestMethod]
        public void Classify_FinesseWeapons_ReturnsFinesse()
            => Assert.AreEqual("finesse", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.FinesseWeapons, null, null));

        [TestMethod]
        public void Classify_TwoHandedCombat_ReturnsTwoHanded()
            => Assert.AreEqual("two_handed", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.TwoHandedCombat, null, null));

        // ---- rule 5: retired melee skills, each its own bucket ----

        [TestMethod]
        public void Classify_RetiredSword_ReturnsSword()
            => Assert.AreEqual("sword", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.Sword, null, null));

        [TestMethod]
        public void Classify_RetiredAxe_ReturnsAxe()
            => Assert.AreEqual("axe", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.Axe, null, null));

        [TestMethod]
        public void Classify_RetiredMace_ReturnsMace()
            => Assert.AreEqual("mace", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.Mace, null, null));

        [TestMethod]
        public void Classify_RetiredSpear_ReturnsSpear()
            => Assert.AreEqual("spear", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.Spear, null, null));

        [TestMethod]
        public void Classify_RetiredDagger_ReturnsDagger()
            => Assert.AreEqual("dagger", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.Dagger, null, null));

        [TestMethod]
        public void Classify_RetiredStaff_ReturnsStaff()
            => Assert.AreEqual("staff", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.Staff, null, null));

        [TestMethod]
        public void Classify_RetiredUnarmedCombat_ReturnsUnarmed()
            => Assert.AreEqual("unarmed", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.UnarmedCombat, null, null));

        // ---- rule 3: MissileLauncher classified by AmmoType, including crystal/chorizite variants ----

        [TestMethod]
        public void Classify_MissileLauncherWithArrow_ReturnsBow()
            => Assert.AreEqual("bow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.Arrow, null));

        [TestMethod]
        public void Classify_MissileLauncherWithArrowCrystal_ReturnsBow()
            => Assert.AreEqual("bow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.ArrowCrystal, null));

        [TestMethod]
        public void Classify_MissileLauncherWithArrowChorizite_ReturnsBow()
            => Assert.AreEqual("bow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.ArrowChorizite, null));

        [TestMethod]
        public void Classify_MissileLauncherWithBolt_ReturnsCrossbow()
            => Assert.AreEqual("crossbow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.Bolt, null));

        [TestMethod]
        public void Classify_MissileLauncherWithBoltCrystal_ReturnsCrossbow()
            => Assert.AreEqual("crossbow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.BoltCrystal, null));

        [TestMethod]
        public void Classify_MissileLauncherWithBoltChorizite_ReturnsCrossbow()
            => Assert.AreEqual("crossbow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.BoltChorizite, null));

        [TestMethod]
        public void Classify_MissileLauncherWithAtlatl_ReturnsAtlatl()
            => Assert.AreEqual("atlatl", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.Atlatl, null));

        [TestMethod]
        public void Classify_MissileLauncherWithAtlatlCrystal_ReturnsAtlatl()
            => Assert.AreEqual("atlatl", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.AtlatlCrystal, null));

        [TestMethod]
        public void Classify_MissileLauncherWithAtlatlChorizite_ReturnsAtlatl()
            => Assert.AreEqual("atlatl", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, (int)AmmoType.AtlatlChorizite, null));

        [TestMethod]
        public void Classify_MissileLauncherWithCombinedArrowFlags_ReturnsBow()
        {
            // A combined flag value distinguishes the bitwise (ammo & mask) != 0 test this classifier
            // actually uses from a plain == comparison, which would fail on a combined value.
            var combined = (int)(AmmoType.Arrow | AmmoType.ArrowCrystal);
            Assert.AreEqual("bow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, null, combined, null));
        }

        // ---- rule 2 vs rule 3 ordering: the actual trap this classifier exists to avoid ----

        [TestMethod]
        public void Classify_AmmunitionCarryingArrowAmmoType_ReturnsAmmunitionNotBow()
        {
            // An arrow (WeenieType.Ammunition) itself carries PropertyInt.AmmoType = Arrow. If the
            // Ammunition rule did not run before the MissileLauncher/AmmoType rule, this would
            // wrongly classify as "bow" instead of "ammunition".
            Assert.AreEqual("ammunition", MarketWeaponClass.Classify(WeenieType.Ammunition, null, (int)AmmoType.Arrow, null));
        }

        // ---- rule 4: thrown weapons (the Missile weenie type itself) ----

        [TestMethod]
        public void Classify_MissileWeenieType_ReturnsThrown()
            => Assert.AreEqual("thrown", MarketWeaponClass.Classify(WeenieType.Missile, null, null, null));

        [TestMethod]
        public void Classify_MissileWeenieTypeWithRetiredThrownWeaponSkill_ReturnsThrown()
            => Assert.AreEqual("thrown", MarketWeaponClass.Classify(WeenieType.Missile, (int)Skill.ThrownWeapon, null, null));

        [TestMethod]
        public void Classify_MissileWeenieTypeWithMissileWeaponsSkill_ReturnsThrown()
            => Assert.AreEqual("thrown", MarketWeaponClass.Classify(WeenieType.Missile, (int)Skill.MissileWeapons, null, null));

        // ---- rule 5: Skill.MissileWeapons on a non-launcher (the throwable-crockery case) ----

        [TestMethod]
        public void Classify_MissileWeaponsSkillOnGenericWeenie_ReturnsThrown_ThrowableCrockeryCase()
        {
            // chalice, tankard, cup, etc: WeenieType.Generic, ItemType.MissileWeapon, WeaponSkill =
            // MissileWeapons, no AmmoType. Matches the web app's legacy panel-line fallback so a
            // re-listed item does not silently change bucket.
            Assert.AreEqual("thrown", MarketWeaponClass.Classify(WeenieType.Generic, (int)Skill.MissileWeapons, null, null));
        }

        [TestMethod]
        public void Classify_MissileWeaponsSkillOnMeleeWeaponWeenie_ReturnsThrown()
            => Assert.AreEqual("thrown", MarketWeaponClass.Classify(WeenieType.MeleeWeapon, (int)Skill.MissileWeapons, null, null));

        [TestMethod]
        public void Classify_MissileWeaponsSkillOnLauncherWithNoAmmoType_DoesNotReturnThrown()
        {
            // The guard: a launcher can only reach the MissileWeapons case by falling through rule 3
            // with an absent/unrecognized AmmoType. It must NOT answer "thrown" here - it must keep
            // falling through to rule 6, which can still resolve a retired WeaponType.
            Assert.IsNull(MarketWeaponClass.Classify(WeenieType.MissileLauncher, (int)Skill.MissileWeapons, null, null));
            Assert.AreEqual("bow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, (int)Skill.MissileWeapons, null, (int)WeaponType.Bow));
        }

        // ---- rule 1: casters ----

        [TestMethod]
        public void Classify_Caster_ReturnsCaster()
            => Assert.AreEqual("caster", MarketWeaponClass.Classify(WeenieType.Caster, null, null, null));

        // ---- non-weapons ----

        [TestMethod]
        public void Classify_NonWeaponWithNoWeaponProperties_ReturnsNull()
            => Assert.IsNull(MarketWeaponClass.Classify(WeenieType.Clothing, null, null, null));

        // ---- rule 3 falls through to rule 5 when AmmoType is absent ----

        [TestMethod]
        public void Classify_MissileLauncherWithNullAmmoTypeButRetiredBowSkill_ReturnsBow()
            => Assert.AreEqual("bow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, (int)Skill.Bow, null, null));

        [TestMethod]
        public void Classify_CrossbowSkillAlone_ReturnsCrossbow()
            => Assert.AreEqual("crossbow", MarketWeaponClass.Classify(WeenieType.MissileLauncher, (int)Skill.Crossbow, null, null));

        [TestMethod]
        public void Classify_ThrownWeaponSkillAlone_ReturnsThrown()
        {
            // WeenieType.Missile short-circuits at rule 4 before the skill switch runs, so a
            // non-Missile, non-launcher WeenieType is the only way to actually exercise
            // "case Skill.ThrownWeapon" in rule 5.
            Assert.AreEqual("thrown", MarketWeaponClass.Classify(WeenieType.Generic, (int)Skill.ThrownWeapon, null, null));
        }

        // ---- rule 6: WeaponType, only when rule 5 found nothing ----

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeMagic_ReturnsCaster()
            => Assert.AreEqual("caster", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Magic));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeUnarmed_ReturnsUnarmed()
            => Assert.AreEqual("unarmed", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Unarmed));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeSword_ReturnsSword()
            => Assert.AreEqual("sword", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Sword));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeAxe_ReturnsAxe()
            => Assert.AreEqual("axe", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Axe));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeMace_ReturnsMace()
            => Assert.AreEqual("mace", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Mace));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeSpear_ReturnsSpear()
            => Assert.AreEqual("spear", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Spear));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeDagger_ReturnsDagger()
            => Assert.AreEqual("dagger", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Dagger));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeStaff_ReturnsStaff()
            => Assert.AreEqual("staff", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Staff));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeBow_ReturnsBow()
            => Assert.AreEqual("bow", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Bow));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeCrossbow_ReturnsCrossbow()
            => Assert.AreEqual("crossbow", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Crossbow));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeThrown_ReturnsThrown()
            => Assert.AreEqual("thrown", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Thrown));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeTwoHanded_ReturnsTwoHanded()
            => Assert.AreEqual("two_handed", MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.TwoHanded));

        [TestMethod]
        public void Classify_NoWeaponSkillButWeaponTypeUndef_ReturnsNull()
            => Assert.IsNull(MarketWeaponClass.Classify(WeenieType.Generic, null, null, (int)WeaponType.Undef));

        // ---- Label ----

        [TestMethod]
        public void Label_ReturnsTheExactLabelForEveryToken()
        {
            Assert.AreEqual("Light Weapons", MarketWeaponClass.Label("light"));
            Assert.AreEqual("Heavy Weapons", MarketWeaponClass.Label("heavy"));
            Assert.AreEqual("Finesse Weapons", MarketWeaponClass.Label("finesse"));
            Assert.AreEqual("Two Handed", MarketWeaponClass.Label("two_handed"));
            Assert.AreEqual("Unarmed", MarketWeaponClass.Label("unarmed"));
            Assert.AreEqual("Sword", MarketWeaponClass.Label("sword"));
            Assert.AreEqual("Axe", MarketWeaponClass.Label("axe"));
            Assert.AreEqual("Mace", MarketWeaponClass.Label("mace"));
            Assert.AreEqual("Spear", MarketWeaponClass.Label("spear"));
            Assert.AreEqual("Dagger", MarketWeaponClass.Label("dagger"));
            Assert.AreEqual("Staff", MarketWeaponClass.Label("staff"));
            Assert.AreEqual("Bow", MarketWeaponClass.Label("bow"));
            Assert.AreEqual("Crossbow", MarketWeaponClass.Label("crossbow"));
            Assert.AreEqual("Atlatl", MarketWeaponClass.Label("atlatl"));
            Assert.AreEqual("Thrown", MarketWeaponClass.Label("thrown"));
            Assert.AreEqual("Ammunition", MarketWeaponClass.Label("ammunition"));
            Assert.AreEqual("Caster", MarketWeaponClass.Label("caster"));
        }

        [TestMethod]
        public void Label_UnknownToken_ReturnsNull()
            => Assert.IsNull(MarketWeaponClass.Label("not_a_real_token"));

        // ---- the wire contract ----

        [TestMethod]
        public void Serialize_UsesTheLiteralWeaponClassFieldNames()
        {
            var snapshot = new ListingSnapshot
            {
                WeaponClass = "bow",
                WeaponClassName = "Bow",
            };

            var json = JsonSerializer.Serialize(snapshot, MarketSnapshot.JsonOptions);

            Assert.IsTrue(json.Contains("\"weapon_class\":\"bow\""), json);
            Assert.IsTrue(json.Contains("\"weapon_class_name\":\"Bow\""), json);

            var after = MarketSnapshot.Deserialize(json);
            Assert.AreEqual("bow", after.WeaponClass);
            Assert.AreEqual("Bow", after.WeaponClassName);
        }
    }
}
