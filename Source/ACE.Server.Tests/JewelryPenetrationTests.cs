using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Entity.Enum;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.JewelryAspectTestKit;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Jewelry Aspect Penetration (Task C): the getter gates every site relies on, the pure resistance math,
    /// and source-scan pins for the four hit sites that cannot be driven without client dats (physical, life and
    /// war/void projectiles, Harm), including the negative pins (Drain and player DoT ticks never penetrate).
    /// </summary>
    [TestClass]
    public class JewelryPenetrationTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedTunables() => Seed();

        [TestCleanup]
        public void RestoreTunables() => Restore();

        // ---------------- pure math ----------------

        [TestMethod]
        public void Penetrate_MovesAResistedModTowardOne_NeverAWeakness()
        {
            Assert.AreEqual(0.6f, JewelryAspectMath.Penetrate(0.5f, 0.2, 0.0), 1e-6, "k = 0: 0.5 + 0.5 * 0.2");
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(0.5f, 1.0, 0.0), 1e-6, "full penetration removes the resistance");
            Assert.AreEqual(0.5f, JewelryAspectMath.Penetrate(0.5f, 0.0, 1.0), 1e-6);
            Assert.AreEqual(1.5f, JewelryAspectMath.Penetrate(1.5f, 0.4, 0.0), "k = 0: a weakness is left alone");
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(1.0f, 0.4, 0.0), "k = 0: neutral stays neutral");
        }

        [TestMethod]
        public void Penetrate_Amplify_NeutralTargetTakesBonus_AtTheSeededTunable()
        {
            var player = BarePlayer(out var equipped);
            var creature = BareCreature();

            Wear(equipped, Piece(10, 0, 0, EquipMask.NeckWear));
            Wear(equipped, Piece(10, 0, 0, EquipMask.WristWearLeft));

            var p = player.GetPenetrationAgainst(creature);
            var k = player.GetPenetrationAmplify();

            Assert.AreEqual(0.16, p, 1e-9, "20 Attack x 0.008 (control)");
            Assert.AreEqual(1.0, k, 1e-9, "seeded amplify");
            Assert.AreEqual(1.16f, JewelryAspectMath.Penetrate(1.0f, p, k), 1e-6, "a neutral target takes +p");
            Assert.AreEqual(1.5f * 1.16f, JewelryAspectMath.Penetrate(1.5f, p, k), 1e-6, "a weak target scales up");

            PropertyManager.ModifyDouble("jewelry_aspects_penetration_amplify", 0.0);
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(1.0f, p, player.GetPenetrationAmplify()), 1e-6, "k = 0 is today's behaviour");
        }

        // ---------------- getter gates the sites depend on ----------------

        [TestMethod]
        public void Getter_PlayerAttackerOnlyAgainstANonPlayer_AndKillSwitch()
        {
            var player = BarePlayer(out var equipped);
            var rival = BarePlayer(out _);
            var creature = BareCreature();

            Wear(equipped, Piece(10, 0, 0, EquipMask.NeckWear));

            Assert.AreEqual(0.08, player.GetPenetrationAgainst(creature), 1e-9, "control: a creature target gets the layer");
            Assert.AreEqual(0.0, player.GetPenetrationAgainst(rival), "PvP pair");

            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);
            Assert.AreEqual(0.0, player.GetPenetrationAgainst(creature), "kill switch");
        }

        [TestMethod]
        public void Getter_CountsOnlyAttackSlots()
        {
            var player = BarePlayer(out var equipped);
            var creature = BareCreature();

            Wear(equipped, Piece(0, 10, 10, EquipMask.NeckWear));
            Assert.AreEqual(0.0, player.GetPenetrationAgainst(creature), "Defense and Utility slots give no Penetration");

            Wear(equipped, Piece(5, 5, 0, EquipMask.WristWearLeft));
            Assert.AreEqual(0.04, player.GetPenetrationAgainst(creature), 1e-9, "5 Attack x 0.008");
        }

        // ---------------- source scans (sites that need dats to drive) ----------------

        [TestMethod]
        public void SourceScan_PhysicalHit_ResistanceOnly_PlayerAttackerCreatureDefenderOnly()
        {
            var text = Flat(PooledLootSourceText.Read("Source/ACE.Server/Entity/DamageEvent.cs"));

            Assert.AreEqual(1, Count(text, "GetPenetrationAgainst("), "exactly one physical site");
            StringAssert.Contains(text, "if (attacker is Player penetrationAttacker) { var penetrationP = penetrationAttacker.GetPenetrationAgainst(defender); ResistanceMod = JewelryAspectMath.Penetrate(ResistanceMod, penetrationP, penetrationP > 0 ? penetrationAttacker.GetPenetrationAmplify() : 0.0); }");

            // Penetration never touches armor: both armor calls are the master text, with no penetration term
            StringAssert.Contains(text, "ArmorMod = CreaturePart.GetArmorMod(DamageType, Armor, Attacker, Weapon, ignoreArmorMod);");
            Assert.AreEqual(0, Count(text, "jewelryPenetration"), "no penetration variable feeds the armor path");
            Assert.AreEqual(0, Count(text, "1.0 - jewelry"), "no armor-ignore scaling");

            // the armor site is the creature-part branch, the player-defender armor call is untouched
            StringAssert.Contains(text, "ArmorMod = attacker.GetArmorMod(playerDefender, DamageType, Armor, Weapon, ignoreArmorMod);");

            // the resistance penetration sits inside the non-player-defender else-branch, after the innate lookup
            var innate = text.IndexOf("inversionInnate));", System.StringComparison.Ordinal);
            var pen = text.IndexOf("JewelryAspectMath.Penetrate(ResistanceMod", System.StringComparison.Ordinal);
            var playerResist = text.IndexOf("ResistanceMod = playerDefender.GetResistanceMod(", System.StringComparison.Ordinal);
            Assert.IsTrue(playerResist >= 0 && innate > playerResist && pen > innate, "penetration follows the creature-defender lookup, not the player one");
        }

        [TestMethod]
        public void SourceScan_Projectiles_LifeAndWarVoid_PlayerCasterNonPlayerTarget()
        {
            var text = Flat(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs"));

            Assert.AreEqual(2, Count(text, "GetPenetrationAgainst("), "life branch and war/void branch");
            StringAssert.Contains(text, "if (sourceCreature is Player lifePenetrationCaster && target is not Player) { var lifePenetrationP = lifePenetrationCaster.GetPenetrationAgainst(target); resistanceMod = JewelryAspectMath.Penetrate(resistanceMod, lifePenetrationP, lifePenetrationP > 0 ? lifePenetrationCaster.GetPenetrationAmplify() : 0.0); }");
            StringAssert.Contains(text, "if (sourceCreature is Player warPenetrationCaster && target is not Player) { var warPenetrationP = warPenetrationCaster.GetPenetrationAgainst(target); resistanceMod = JewelryAspectMath.Penetrate(resistanceMod, warPenetrationP, warPenetrationP > 0 ? warPenetrationCaster.GetPenetrationAmplify() : 0.0); }");

            // war/void: penetration precedes the PvP-only void modifier, so that modifier is never penetrated
            var pen = text.IndexOf("warPenetrationCaster.GetPenetrationAgainst", System.StringComparison.Ordinal);
            var voidMod = text.IndexOf("void_pvp_modifier", System.StringComparison.Ordinal);
            Assert.IsTrue(pen >= 0 && voidMod > pen);
        }

        [TestMethod]
        public void SourceScan_Harm_Penetrates_ButDrainDoesNot()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Magic.cs");

            Assert.AreEqual(1, Count(text, "GetPenetrationAgainst("), "Harm is the only WorldObject_Magic site (ruling 3: not Drain)");

            var boost = Flat(PooledLootSourceText.MethodBody(text, "private void HandleCastSpell_Boost("));
            StringAssert.Contains(boost, "var harmResistanceMod = targetCreature.GetResistanceMod(resistanceType, this, null, lifeWeaponResistMod);");
            StringAssert.Contains(boost, "if (isHarmfulLifeBoost && this is Player harmPenetrationCaster && targetCreature is not Player) { var harmPenetrationP = harmPenetrationCaster.GetPenetrationAgainst(targetCreature); harmResistanceMod = JewelryAspectMath.Penetrate((float)harmResistanceMod, harmPenetrationP, harmPenetrationP > 0 ? harmPenetrationCaster.GetPenetrationAmplify() : 0.0); }");
            Assert.AreEqual(0, Count(boost, "(float)targetCreature.GetResistanceMod"), "no unconditional narrowing of the resistance mod to float");
            StringAssert.Contains(boost, "* harmResistanceMod");

            // the transfer (Drain) path is a different method and carries none
            var transfer = PooledLootSourceText.MethodBody(text, "private void HandleCastSpell_Transfer(");
            Assert.AreEqual(0, Count(transfer, "Penetrat"), "Drain never penetrates");
        }

        [TestMethod]
        public void SourceScan_PlayerDotTicks_NeverPenetrate()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs");
            Assert.AreEqual(0, Count(text, "Penetrat"), "ruling 3: player DoT ticks are out of scope");
        }
    }
}
