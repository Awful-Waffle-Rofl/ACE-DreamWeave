using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Imbue effect curves against the retail formulas documented inline in WorldObject_Weapon.cs
    /// (Anon's formula docs / acpedia):
    ///   Critical Strike:  melee (BS-100)/600, missile+magic (BS-60)/600, floored at the default
    ///                     crit rate (10% physical, 5% war magic), capped at 50%; halved for magic in PvP
    ///   Crippling Blow:   melee (BS-40)/60, missile+magic BS/60, floor 1x, cap 6x
    ///   Rending:          melee BS/160, missile+magic BS/144, clamped to [1.0, 2.5]
    ///   Armor Rending:    melee (BS-160)/400, missile (BS-144)/360, max 60% armor ignored, never for casters
    /// Base skill caps at 400 (melee) / 360 (missile, magic); ranks past the cap do nothing.
    ///
    /// A CreatureSkill whose advancement class is Inactive derives Base purely from InitLevel,
    /// so exact base-skill values can be driven with no live Creature, database, or dat files.
    /// </summary>
    [TestClass]
    public class WeaponImbueTests
    {
        private const float Epsilon = 0.0001f;

        private static CreatureSkill MakeSkill(Skill skill, uint baseSkill)
            => new CreatureSkill(null, skill, new PropertiesSkill { InitLevel = baseSkill, SAC = SkillAdvancementClass.Inactive });

        // ---- Critical Strike ----

        [TestMethod]
        public void CriticalStrike_Melee_MatchesRetailCurve()
        {
            // below the effective threshold the default 10% physical crit rate applies
            Assert.AreEqual(0.10f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.HeavyWeapons, 100)), Epsilon);
            Assert.AreEqual(0.10f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.HeavyWeapons, 160)), Epsilon);
            Assert.AreEqual(0.25f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.HeavyWeapons, 250)), Epsilon);
            Assert.AreEqual(0.50f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.HeavyWeapons, 400)), Epsilon);
        }

        [TestMethod]
        public void CriticalStrike_MissileAndMagic_MatchesRetailCurve()
        {
            Assert.AreEqual(0.10f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.MissileWeapons, 60)), Epsilon);
            Assert.AreEqual(0.25f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.MissileWeapons, 210)), Epsilon);
            Assert.AreEqual(0.50f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.MissileWeapons, 360)), Epsilon);

            // war magic floors at its own 5% default, not the physical 10%
            Assert.AreEqual(0.05f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.WarMagic, 60)), Epsilon);
            Assert.AreEqual(0.50f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.WarMagic, 360)), Epsilon);
        }

        [TestMethod]
        public void CriticalStrike_PvP_HalvesMagicOnly()
        {
            Assert.AreEqual(0.25f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.WarMagic, 360), isPvP: true), Epsilon);
            Assert.AreEqual(0.50f, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.HeavyWeapons, 400), isPvP: true), Epsilon);
        }

        [TestMethod]
        public void CriticalStrike_RanksPastTheBaseSkillCap_DoNothing()
        {
            Assert.AreEqual(WorldObject.MaxCriticalStrikeMod, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.HeavyWeapons, 800)), Epsilon);
            Assert.AreEqual(WorldObject.MaxCriticalStrikeMod, WorldObject.GetCriticalStrikeMod(MakeSkill(Skill.MissileWeapons, 800)), Epsilon);
        }

        // ---- Crippling Blow ----

        [TestMethod]
        public void CripplingBlow_Melee_MatchesRetailCurve()
        {
            Assert.AreEqual(1.0f, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.HeavyWeapons, 40)), Epsilon);
            Assert.AreEqual(1.0f, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.HeavyWeapons, 100)), Epsilon);
            Assert.AreEqual(2.0f, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.HeavyWeapons, 160)), Epsilon);
            Assert.AreEqual(6.0f, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.HeavyWeapons, 400)), Epsilon);
        }

        [TestMethod]
        public void CripplingBlow_MissileAndMagic_MatchesRetailCurve()
        {
            Assert.AreEqual(2.0f, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.MissileWeapons, 120)), Epsilon);
            Assert.AreEqual(6.0f, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.MissileWeapons, 360)), Epsilon);
            Assert.AreEqual(6.0f, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.WarMagic, 360)), Epsilon);
        }

        [TestMethod]
        public void CripplingBlow_NeverExceedsTheSixTimesCap()
        {
            // the 6x cap emerges from the base skill cap, not an explicit clamp - so probe past it
            Assert.AreEqual(WorldObject.MaxCripplingBlowMod, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.HeavyWeapons, 1000)), Epsilon);
            Assert.AreEqual(WorldObject.MaxCripplingBlowMod, WorldObject.GetCripplingBlowMod(MakeSkill(Skill.WarMagic, 1000)), Epsilon);
        }

        // ---- Rending (elemental) ----

        [TestMethod]
        public void Rending_MatchesRetailCurveAndCaps()
        {
            Assert.AreEqual(1.0f, WorldObject.GetRendingMod(MakeSkill(Skill.HeavyWeapons, 160)), Epsilon);
            Assert.AreEqual(1.5f, WorldObject.GetRendingMod(MakeSkill(Skill.HeavyWeapons, 240)), Epsilon);
            Assert.AreEqual(2.5f, WorldObject.GetRendingMod(MakeSkill(Skill.HeavyWeapons, 400)), Epsilon);

            Assert.AreEqual(1.0f, WorldObject.GetRendingMod(MakeSkill(Skill.MissileWeapons, 144)), Epsilon);
            Assert.AreEqual(1.5f, WorldObject.GetRendingMod(MakeSkill(Skill.MissileWeapons, 216)), Epsilon);
            Assert.AreEqual(2.5f, WorldObject.GetRendingMod(MakeSkill(Skill.MissileWeapons, 360)), Epsilon);

            // rending can never exceed the level 6 vuln equivalent, or drop below neutral
            Assert.AreEqual(WorldObject.MaxRendingMod, WorldObject.GetRendingMod(MakeSkill(Skill.HeavyWeapons, 1000)), Epsilon);
            Assert.AreEqual(1.0f, WorldObject.GetRendingMod(MakeSkill(Skill.HeavyWeapons, 0)), Epsilon);
        }

        // ---- Armor Rending ----

        [TestMethod]
        public void ArmorRending_MatchesRetailCurveAndCaps()
        {
            // returned value is the armor multiplier: 1.0 = no effect, 0.4 = 60% of armor ignored
            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingMod(MakeSkill(Skill.HeavyWeapons, 160)), Epsilon);
            Assert.AreEqual(0.5f, WorldObject.GetArmorRendingMod(MakeSkill(Skill.HeavyWeapons, 360)), Epsilon);
            Assert.AreEqual(0.4f, WorldObject.GetArmorRendingMod(MakeSkill(Skill.HeavyWeapons, 400)), Epsilon);
            Assert.AreEqual(1.0f - WorldObject.MaxArmorRendingMod, WorldObject.GetArmorRendingMod(MakeSkill(Skill.HeavyWeapons, 800)), Epsilon);

            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingMod(MakeSkill(Skill.MissileWeapons, 144)), Epsilon);
            Assert.AreEqual(0.4f, WorldObject.GetArmorRendingMod(MakeSkill(Skill.MissileWeapons, 360)), Epsilon);
        }

        [TestMethod]
        public void ArmorRending_DoesNothingForCasters()
        {
            Assert.AreEqual(1.0f, WorldObject.GetArmorRendingMod(MakeSkill(Skill.WarMagic, 360)), Epsilon);
        }

        // ---- shared curve invariants ----

        [TestMethod]
        public void AllImbueCurves_AreMonotonicAndBounded()
        {
            // property-style sweep: more base skill can never make any imbue effect weaker,
            // and every effect stays inside its documented floor/cap at every skill level
            foreach (var skill in new[] { Skill.HeavyWeapons, Skill.MissileWeapons, Skill.WarMagic })
            {
                var prevCS = 0.0f;
                var prevCB = 0.0f;
                var prevRend = 0.0f;
                var prevAR = 2.0f;

                for (uint baseSkill = 0; baseSkill <= 800; baseSkill += 5)
                {
                    var s = MakeSkill(skill, baseSkill);

                    var cs = WorldObject.GetCriticalStrikeMod(s);
                    Assert.IsTrue(cs >= prevCS - Epsilon, $"{skill} CS fell at BS {baseSkill}");
                    Assert.IsTrue(cs >= 0.05f - Epsilon && cs <= WorldObject.MaxCriticalStrikeMod + Epsilon, $"{skill} CS {cs} out of bounds at BS {baseSkill}");

                    var cb = WorldObject.GetCripplingBlowMod(s);
                    Assert.IsTrue(cb >= prevCB - Epsilon, $"{skill} CB fell at BS {baseSkill}");
                    Assert.IsTrue(cb >= 1.0f - Epsilon && cb <= WorldObject.MaxCripplingBlowMod + Epsilon, $"{skill} CB {cb} out of bounds at BS {baseSkill}");

                    var rend = WorldObject.GetRendingMod(s);
                    Assert.IsTrue(rend >= prevRend - Epsilon, $"{skill} Rend fell at BS {baseSkill}");
                    Assert.IsTrue(rend >= 1.0f - Epsilon && rend <= WorldObject.MaxRendingMod + Epsilon, $"{skill} Rend {rend} out of bounds at BS {baseSkill}");

                    // armor rending is a multiplier that shrinks as skill grows
                    var ar = WorldObject.GetArmorRendingMod(s);
                    Assert.IsTrue(ar <= prevAR + Epsilon, $"{skill} AR rose at BS {baseSkill}");
                    Assert.IsTrue(ar >= 1.0f - WorldObject.MaxArmorRendingMod - Epsilon && ar <= 1.0f + Epsilon, $"{skill} AR {ar} out of bounds at BS {baseSkill}");

                    prevCS = cs;
                    prevCB = cb;
                    prevRend = rend;
                    prevAR = ar;
                }
            }
        }

        [TestMethod]
        public void BaseSkillImbued_CapsAt400MeleeAnd360Missile()
        {
            Assert.AreEqual(400, WorldObject.GetBaseSkillImbued(MakeSkill(Skill.HeavyWeapons, 500)));
            Assert.AreEqual(360, WorldObject.GetBaseSkillImbued(MakeSkill(Skill.MissileWeapons, 500)));
            Assert.AreEqual(360, WorldObject.GetBaseSkillImbued(MakeSkill(Skill.WarMagic, 500)));
            Assert.AreEqual(250, WorldObject.GetBaseSkillImbued(MakeSkill(Skill.HeavyWeapons, 250)));
        }

        // ---- skill / damage type classification ----

        [TestMethod]
        public void ImbuedSkillType_ClassifiesEverySkillFamily()
        {
            foreach (var melee in new[] { Skill.LightWeapons, Skill.HeavyWeapons, Skill.FinesseWeapons, Skill.DualWield, Skill.TwoHandedCombat,
                                          Skill.Axe, Skill.Dagger, Skill.Mace, Skill.Spear, Skill.Staff, Skill.Sword, Skill.UnarmedCombat })
                Assert.AreEqual(WorldObject.ImbuedSkillType.Melee, WorldObject.GetImbuedSkillType(MakeSkill(melee, 100)), melee.ToString());

            foreach (var missile in new[] { Skill.MissileWeapons, Skill.Bow, Skill.Crossbow, Skill.Sling, Skill.ThrownWeapon })
                Assert.AreEqual(WorldObject.ImbuedSkillType.Missile, WorldObject.GetImbuedSkillType(MakeSkill(missile, 100)), missile.ToString());

            // LifeMagic is deliberately Magic here (Martyr's Hecatomb crits)
            foreach (var magic in new[] { Skill.WarMagic, Skill.VoidMagic, Skill.LifeMagic })
                Assert.AreEqual(WorldObject.ImbuedSkillType.Magic, WorldObject.GetImbuedSkillType(MakeSkill(magic, 100)), magic.ToString());

            Assert.AreEqual(WorldObject.ImbuedSkillType.Undef, WorldObject.GetImbuedSkillType(MakeSkill(Skill.MeleeDefense, 100)));
            Assert.AreEqual(WorldObject.ImbuedSkillType.Undef, WorldObject.GetImbuedSkillType(null));
        }

        [TestMethod]
        public void RendDamageType_MapsEveryElementAndRejectsTheRest()
        {
            Assert.AreEqual(ImbuedEffectType.SlashRending, WorldObject.GetRendDamageType(DamageType.Slash));
            Assert.AreEqual(ImbuedEffectType.PierceRending, WorldObject.GetRendDamageType(DamageType.Pierce));
            Assert.AreEqual(ImbuedEffectType.BludgeonRending, WorldObject.GetRendDamageType(DamageType.Bludgeon));
            Assert.AreEqual(ImbuedEffectType.FireRending, WorldObject.GetRendDamageType(DamageType.Fire));
            Assert.AreEqual(ImbuedEffectType.ColdRending, WorldObject.GetRendDamageType(DamageType.Cold));
            Assert.AreEqual(ImbuedEffectType.AcidRending, WorldObject.GetRendDamageType(DamageType.Acid));
            Assert.AreEqual(ImbuedEffectType.ElectricRending, WorldObject.GetRendDamageType(DamageType.Electric));
            Assert.AreEqual(ImbuedEffectType.NetherRending, WorldObject.GetRendDamageType(DamageType.Nether));

            Assert.AreEqual(ImbuedEffectType.Undef, WorldObject.GetRendDamageType(DamageType.Undef));

            // Health USED to be Undef here. It now maps to Blood Rending - the life-magic counterpart of the
            // elemental rends - so a Sanguine caster can cleave Health resistance the way a Fire wand cleaves
            // Fire. Stamina and Mana are deliberately left unmapped; only the life school gained a rend.
            // Full coverage of the new behaviour lives in LifeVulnerabilityTests.
            Assert.AreEqual(ImbuedEffectType.HealthRending, WorldObject.GetRendDamageType(DamageType.Health));
            Assert.AreEqual(ImbuedEffectType.Undef, WorldObject.GetRendDamageType(DamageType.Stamina));
            Assert.AreEqual(ImbuedEffectType.Undef, WorldObject.GetRendDamageType(DamageType.Mana));
        }

        // ---- interval helpers ----

        [TestMethod]
        public void GetInterval_ClampsToZeroOneAndInterpolatesLinearly()
        {
            Assert.AreEqual(0.0f, WorldObject.GetInterval(50, 100, 200), Epsilon);
            Assert.AreEqual(0.0f, WorldObject.GetInterval(100, 100, 200), Epsilon);
            Assert.AreEqual(0.5f, WorldObject.GetInterval(150, 100, 200), Epsilon);
            Assert.AreEqual(1.0f, WorldObject.GetInterval(200, 100, 200), Epsilon);
            Assert.AreEqual(1.0f, WorldObject.GetInterval(500, 100, 200), Epsilon);
        }

        [TestMethod]
        public void SetInterval_ProjectsZeroOneBackOntoTheRange()
        {
            Assert.AreEqual(100.0f, WorldObject.SetInterval(0.0f, 100, 200), Epsilon);
            Assert.AreEqual(150.0f, WorldObject.SetInterval(0.5f, 100, 200), Epsilon);
            Assert.AreEqual(200.0f, WorldObject.SetInterval(1.0f, 100, 200), Epsilon);
        }
    }
}
