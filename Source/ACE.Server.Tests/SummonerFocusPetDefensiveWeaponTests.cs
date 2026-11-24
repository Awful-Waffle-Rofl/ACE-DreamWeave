using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.SummonerTransferFixtures;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Defensive Weapon on a Summoner focus is Shared (owner ruling Q3): the wielder keeps it and the owner's combat pets
    /// take it too, through both Creature.GetShieldMod branches (the value DamageEvent stores as its shield mod) and the
    /// simulator's MonsterDefenderProfile shield row. Frontal physical only, stance-bound, and refused for a Player
    /// attacker, an Echo and a plain wand.
    /// </summary>
    [TestClass]
    public class SummonerFocusPetDefensiveWeaponTests
    {
        private static uint nextGuid = 0x7E7A0000;
        private static uint nextWcid = 997900;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedKeys()
        {
            Seed();
            PropertyManager.ModifyBool("shield_applies_in_noncombat", false);
        }

        [TestCleanup]
        public void RestoreKeys()
        {
            Restore();
            PropertyManager.ModifyBool("shield_applies_in_noncombat", (bool)DefaultPropertyManager.DefaultBooleanProperties["shield_applies_in_noncombat"].Item);
        }

        private static Caster DwFocus() => Wand(true, ImbuedEffectType.Undef, WeaponModId.DefensiveWeapon);

        /// <summary>The owner's pet at (55, 45) facing +Y in a melee stance, so TestCreatures' attacker at (50, 50) is frontal.</summary>
        private static CombatPet Defender(Player owner, CombatMode mode = CombatMode.Melee, bool echo = false, WorldObject shield = null)
        {
            var pet = Pet(owner, echo: echo, shieldSkill: shield != null ? 60u : 0u);
            pet.Location = new Position(0x00010064, 55.0f, 45.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            if (shield != null)
                pet.EquippedObjects[shield.Guid] = shield;

            typeof(Creature).GetProperty(nameof(Creature.CombatMode)).GetSetMethod(true).Invoke(pet, new object[] { mode });
            return pet;
        }

        private static WorldObject Shield()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                    { PropertyInt.ArmorLevel, 100 },
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.Shield },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.ArmorModVsSlash, 1.0 } },
            };

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>The level CalcArmorMod was handed, recovered from its output.</summary>
        private static double LevelFrom(float shieldMod) =>
            shieldMod >= 1.0f ? 0.0 : SkillFormula.ArmorMod / shieldMod - SkillFormula.ArmorMod;

        [TestMethod]
        public void No_shield_pet_takes_a_frontal_virtual_shield_at_the_focus_level()
        {
            var attacker = TestCreatures.CreateAttacker();
            var ignore = attacker.GetIgnoreShieldMod(null);
            var owner = Owner(DwFocus());

            Assert.AreEqual(1.0f, Defender(Owner(Wand(true))).GetShieldMod(attacker, DamageType.Slash, null), "positive control: no DW, no shield");
            Assert.AreEqual(WeaponModCombat.ArcaneShieldMod(200.0, ignore), Defender(owner).GetShieldMod(attacker, DamageType.Slash, null), 1e-6f, "ArcaneShieldMod(200)");
            Assert.IsTrue(Defender(owner).GetShieldMod(attacker, DamageType.Slash, null) < 0.6f, "a real cut");

            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 0.5);
            Assert.AreEqual(WeaponModCombat.ArcaneShieldMod(100.0, ignore), Defender(owner).GetShieldMod(attacker, DamageType.Slash, null), 1e-6f, "half transfer: level 100");
        }

        [TestMethod]
        public void Shielded_pet_takes_the_focus_level_after_the_shield_cap()
        {
            var attacker = TestCreatures.CreateAttacker();
            var ignore = attacker.GetIgnoreShieldMod(null);

            var shieldOnly = Defender(Owner(Wand(true)), shield: Shield()).GetShieldMod(attacker, DamageType.Slash, null);
            var both = Defender(Owner(DwFocus()), shield: Shield()).GetShieldMod(attacker, DamageType.Slash, null);

            Assert.IsTrue(shieldOnly < 1.0f, "fixture: the shield alone counts");
            Assert.AreEqual(LevelFrom(shieldOnly) + 200.0 * ignore, LevelFrom(both), 1e-2, "AfterShieldCap: the 200 rides on top of the capped shield level");
        }

        [TestMethod]
        public void Defensive_Weapon_negatives()
        {
            var attacker = TestCreatures.CreateAttacker();
            var owner = Owner(DwFocus());

            Assert.IsTrue(Defender(owner).GetShieldMod(attacker, DamageType.Slash, null) < 1.0f, "positive control");

            var behind = TestCreatures.CreateAttacker();
            behind.Location = new Position(0x00010064, 55.0f, 35.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            Assert.AreEqual(1.0f, Defender(owner).GetShieldMod(behind, DamageType.Slash, null), "rear arc");

            Assert.AreEqual(1.0f, Defender(owner, CombatMode.NonCombat).GetShieldMod(attacker, DamageType.Slash, null), "NonCombat: stance-bound");
            Assert.AreEqual(1.0f, Defender(owner, echo: true).GetShieldMod(attacker, DamageType.Slash, null), "Echo");
            Assert.AreEqual(1.0f, Defender(Owner(Wand(false, ImbuedEffectType.Undef, WeaponModId.DefensiveWeapon))).GetShieldMod(attacker, DamageType.Slash, null), "plain wand");
            Assert.AreEqual(0.0, SummonerFocus.PetDefensiveWeaponLevel(Defender(owner), PlayerTarget()), "Player attacker: no DW");
            Assert.AreEqual(0.0, SummonerFocus.PetDefensiveWeaponLevel(attacker, attacker), "a monster defender never reads it");
            Assert.AreEqual(200.0, SummonerFocus.PetDefensiveWeaponLevel(Defender(owner), null), 1e-9, "a literal null attacker is target-less and reads the focus");
            Assert.AreEqual(0.0, SummonerFocus.PetDefensiveWeaponLevel(Defender(owner), Shield()), "a non-Creature attacker reads none");

            var pvpOwner = Owner(DwFocus());
            pvpOwner.SetPvpBindingForTests(SummonerFocusTests.LiveBinding());
            Assert.AreEqual(1.0f, Defender(pvpOwner).GetShieldMod(attacker, DamageType.Slash, null), "owner in a PvP match");

            PropertyManager.ModifyDouble(SummonerFocus.PetModTransferPercentKey, 0.0);
            Assert.AreEqual(1.0f, Defender(owner).GetShieldMod(attacker, DamageType.Slash, null), "transfer 0");
        }

        /// <summary>With shield_applies_in_noncombat on, a peace-mode shield still counts, and Defensive Weapon still adds nothing (stance-bound, like the player's).</summary>
        [TestMethod]
        public void NonCombat_with_a_peace_mode_shield_adds_no_Defensive_Weapon()
        {
            var attacker = TestCreatures.CreateAttacker();
            PropertyManager.ModifyBool("shield_applies_in_noncombat", true);

            var shieldOnly = Defender(Owner(Wand(true)), CombatMode.NonCombat, shield: Shield()).GetShieldMod(attacker, DamageType.Slash, null);
            var both = Defender(Owner(DwFocus()), CombatMode.NonCombat, shield: Shield()).GetShieldMod(attacker, DamageType.Slash, null);

            Assert.IsTrue(shieldOnly < 1.0f, "harness: the peace-mode shield counts");
            Assert.AreEqual(shieldOnly, both, 1e-6f, "NonCombat: no Defensive Weapon on top");
            Assert.IsTrue(Defender(Owner(DwFocus()), shield: Shield()).GetShieldMod(attacker, DamageType.Slash, null) < shieldOnly, "control: in a combat stance it adds");
        }

        [TestMethod]
        public void The_owner_keeps_it_Shared()
        {
            var owner = Owner(DwFocus());
            Defender(owner);

            Assert.AreEqual(200.0, owner.GetDefensiveWeaponModValue(), 1e-9, "the wielder still reads 200");
        }

        [TestMethod]
        public void Simulator_shield_row_matches_the_live_shield_mod()
        {
            var attacker = TestCreatures.CreateAttacker();
            var ignore = attacker.GetIgnoreShieldMod(null);

            // the duel's ownerless pet, through the simulator seam (an owned pet's profile reads its owner's position
            // for Rallying Presence, which this fixture owner has none of)
            var focus = DwFocus();

            foreach (var shield in new WorldObject[] { null, Shield() })
            {
                SummonerFocus.SimulatedPetFocusSource = null;
                var bare = MonsterDefenderProfile.Build(Defender(null, shield: shield)).Shield[new ShieldKey(DamageType.Slash, false)];

                SummonerFocus.SimulatedPetFocusSource = _ => focus;
                var pet = Defender(null, shield: shield);
                var live = pet.GetShieldMod(attacker, DamageType.Slash, null);
                var row = MonsterDefenderProfile.Build(pet).Shield[new ShieldKey(DamageType.Slash, false)];

                Assert.IsTrue(live < 1.0f, "positive control");
                Assert.AreEqual(live, SkillFormula.CalcArmorMod(row.CappedEffectiveLevel * ignore), 1e-5f, $"profile row = live ({(shield == null ? "no shield" : "shield")})");
                Assert.AreEqual(200.0, row.CappedEffectiveLevel - bare.CappedEffectiveLevel, 1e-3, "the row carries the 200");
            }
        }
    }
}
