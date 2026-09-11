using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.CombatSimulator;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the arithmetic ProfileBuilder transcribes from the engine's defender-side functions.
    ///
    /// WHY THESE TEST THREE HELPERS RATHER THAN ProfileBuilder.Walk. No Player can be constructed
    /// in this assembly at all: both Player constructors call
    /// DatabaseManager.Authentication.GetAccountById (Player.cs:149 and :177), which opens an
    /// AuthDbContext whose OnConfiguring dereferences ConfigManager.Config.MySql.Authentication
    /// (Source\ACE.Database\Models\Auth\AuthDbContext.cs:27) - null in a headless run, so the
    /// constructor throws NullReferenceException before the walk starts. Walk takes a Player, so
    /// it and the spec's R2 purity assertion are both out of reach here, and a mock standing in for
    /// a Player would assert nothing about the transcription these exist to protect. The
    /// composition helpers are internal for exactly this reason (InternalsVisibleTo,
    /// ACE.Server.csproj:15).
    ///
    /// WHAT THIS DOES NOT COVER, said plainly so nobody reads a green run as more than it is:
    /// these assert that the values are COMPOSED correctly once read, not that each row is filled
    /// from the right engine function. The magic-defense bug - Walk reading GetEffectiveDefenseSkill
    /// for CombatType.Magic, which silently returns MELEE defense - lived in the second category
    /// and would still pass every test in this file. Closing that class needs a Player and
    /// therefore a database-backed test project.
    /// </summary>
    [TestClass]
    public class ProfileBuilderCompositionTests
    {
        private const float Epsilon = 0.0001f;

        /// <summary>
        /// Monster_Melee.cs:496-534, per armor piece:
        ///
        ///   effectiveAL = baseArmor + armorMod       = 200 + 50  = 250
        ///   effectiveRL = clamp(resistance + bane)   = 1.0 + 0.5 = 1.5
        ///   result      = 250 * 1.5                              = 375
        /// </summary>
        [TestMethod]
        public void PieceArmorLevel_MultipliesTheSummedLevelByTheSummedResistance()
        {
            var result = ProfileBuilder.ComposePieceArmorLevel(
                baseArmor: 200, armorModEnchantment: 50.0f, resistance: 1.0, armorBane: 0.5f, ignoreMagicArmor: false);

            Assert.AreEqual(375.0f, result, Epsilon);
        }

        /// <summary>
        /// ignoreMagicArmor drops BOTH enchantment terms, not just the armor-level one - the engine
        /// zeroes the bane at Monster_Melee.cs:517-518 as well as the armor mod at :508-509.
        ///
        ///   effectiveAL = 200 + 0    = 200
        ///   effectiveRL = 1.0 + 0    = 1.0
        ///   result                   = 200
        ///
        /// The same inputs with the flag off give 375, so this discriminates: a version that zeroed
        /// only the armor mod would return 200 * 1.5 = 300.
        /// </summary>
        [TestMethod]
        public void PieceArmorLevel_IgnoreMagicArmorDropsBothTheArmorModAndTheBane()
        {
            var result = ProfileBuilder.ComposePieceArmorLevel(
                baseArmor: 200, armorModEnchantment: 50.0f, resistance: 1.0, armorBane: 0.5f, ignoreMagicArmor: true);

            Assert.AreEqual(200.0f, result, Epsilon);
        }

        /// <summary>
        /// The resistance clamp at Monster_Melee.cs:524 bounds the summed resistance to [-2, 2],
        /// and it is applied to the RESISTANCE, never to the armor level.
        ///
        ///   high: clamp(3.0)  =  2.0, 100 *  2.0 =  200
        ///   low:  clamp(-5.0) = -2.0, 100 * -2.0 = -200
        /// </summary>
        [TestMethod]
        public void PieceArmorLevel_ClampsResistanceToPlusOrMinusTwo()
        {
            Assert.AreEqual(200.0f, ProfileBuilder.ComposePieceArmorLevel(
                baseArmor: 100, armorModEnchantment: 0.0f, resistance: 3.0, armorBane: 0.0f, ignoreMagicArmor: false), Epsilon);

            Assert.AreEqual(-200.0f, ProfileBuilder.ComposePieceArmorLevel(
                baseArmor: 100, armorModEnchantment: 0.0f, resistance: -5.0, armorBane: 0.0f, ignoreMagicArmor: false), Epsilon);
        }

        /// <summary>
        /// Creature_Properties.cs:129-132: the stored protection mod is whichever of the life
        /// protection and the natural resistance is MORE powerful, and more powerful means LOWER.
        /// The vulnerability mod passes through raw - its weapon floor is an attacker term applied
        /// by MitigationMath at call time.
        /// </summary>
        [TestMethod]
        public void ResistancePair_TakesTheLowerOfProtectionAndNaturalResistance()
        {
            var natural = ProfileBuilder.ComposeResistancePair(protMod: 0.8f, vulnMod: 1.3f, naturalResistMod: 0.5f, resistAug: 0);

            Assert.AreEqual(0.5f, natural.P0, Epsilon, "natural resistance is the stronger of the two here");
            Assert.AreEqual(1.3f, natural.V0, Epsilon, "the vulnerability mod is stored raw");

            var protection = ProfileBuilder.ComposeResistancePair(protMod: 0.4f, vulnMod: 1.0f, naturalResistMod: 0.9f, resistAug: 0);

            Assert.AreEqual(0.4f, protection.P0, Epsilon, "life protection is the stronger of the two here");
        }

        /// <summary>
        /// Creature_Properties.cs:135-143, the player resistance augmentation:
        ///
        ///   augFactor = min(1.0, 3 * 0.1) = 0.3
        ///   P0        = 0.5 * (1 - 0.3)   = 0.35
        ///
        /// and at 20 ranks the factor saturates at 1.0, taking P0 to zero rather than negative.
        /// </summary>
        [TestMethod]
        public void ResistancePair_AppliesTheResistanceAugmentationAndSaturatesIt()
        {
            var some = ProfileBuilder.ComposeResistancePair(protMod: 0.5f, vulnMod: 1.0f, naturalResistMod: 1.0f, resistAug: 3);

            Assert.AreEqual(0.35f, some.P0, Epsilon);

            var saturated = ProfileBuilder.ComposeResistancePair(protMod: 0.5f, vulnMod: 1.0f, naturalResistMod: 1.0f, resistAug: 20);

            Assert.AreEqual(0.0f, saturated.P0, Epsilon, "the augmentation factor caps at 1.0, so P0 floors at zero");
        }

        /// <summary>
        /// Creature_Combat.cs:726-769:
        ///
        ///   effectiveSL = baseSL + modSL          = 300 + 100 = 400
        ///   effectiveRL = clamp(1.0 + 0.0)                    = 1.0
        ///   level       = 400, cap = 1000 (spec)              = 400
        /// </summary>
        [TestMethod]
        public void ShieldLevel_SumsTheEnchantmentAndScalesByResistance()
        {
            var result = ProfileBuilder.ComposeShieldLevel(
                baseSL: 300.0f, modSL: 100.0f, baseRL: 1.0, modRL: 0.0f,
                ignoreMagicArmor: false, shieldSkillCurrent: 1000, shieldSpecialized: true);

            Assert.AreEqual(400.0f, result, Epsilon);
        }

        /// <summary>
        /// THE CAP IS APPLIED AFTER THE ENCHANTMENTS, which is the ordering most likely to drift,
        /// so this is the assertion that matters most in this file. Specialized, so the cap is the
        /// full shield skill:
        ///
        ///   level = (300 + 100) * 1.0 = 400, cap = 350 -> 350
        ///
        /// A version that capped BEFORE the enchantment would give min(300, 350) + 100 = 400, so
        /// this case discriminates between the two orderings rather than merely exercising them.
        /// </summary>
        [TestMethod]
        public void ShieldLevel_AppliesTheSkillCapAfterTheEnchantmentsNotBefore()
        {
            var result = ProfileBuilder.ComposeShieldLevel(
                baseSL: 300.0f, modSL: 100.0f, baseRL: 1.0, modRL: 0.0f,
                ignoreMagicArmor: false, shieldSkillCurrent: 350, shieldSpecialized: true);

            Assert.AreEqual(350.0f, result, Epsilon);
        }

        /// <summary>
        /// Creature_Combat.cs:766-767: a trained or untrained shield skill caps at HALF the skill,
        /// a specialized one at the whole skill. At skill 500 the trained cap is 250, which bites
        /// the 400 level above.
        /// </summary>
        [TestMethod]
        public void ShieldLevel_HalvesTheCapUnlessTheSkillIsSpecialized()
        {
            var trained = ProfileBuilder.ComposeShieldLevel(
                baseSL: 300.0f, modSL: 100.0f, baseRL: 1.0, modRL: 0.0f,
                ignoreMagicArmor: false, shieldSkillCurrent: 500, shieldSpecialized: false);

            Assert.AreEqual(250.0f, trained, Epsilon);

            var specialized = ProfileBuilder.ComposeShieldLevel(
                baseSL: 300.0f, modSL: 100.0f, baseRL: 1.0, modRL: 0.0f,
                ignoreMagicArmor: false, shieldSkillCurrent: 500, shieldSpecialized: true);

            Assert.AreEqual(400.0f, specialized, Epsilon, "at cap 500 the 400 level is under the cap and passes through");
        }

        /// <summary>
        /// ignoreMagicArmor zeroes BOTH the shield's armor-level enchantment (Creature_Combat.cs:734-735)
        /// and its per-damage-type bane or lure (:746-747), which is the whole reason the shield table
        /// carries that boolean as a key axis.
        ///
        ///   effectiveSL = 300 + 0        = 300
        ///   effectiveRL = clamp(1.0 + 0) = 1.0
        ///   result                       = 300
        ///
        /// The same inputs with the flag off give (300 + 100) * 1.5 = 600, so a version that dropped
        /// only one of the two would land on 450 or 400 rather than 300.
        /// </summary>
        [TestMethod]
        public void ShieldLevel_IgnoreMagicArmorDropsBothShieldEnchantments()
        {
            var ignored = ProfileBuilder.ComposeShieldLevel(
                baseSL: 300.0f, modSL: 100.0f, baseRL: 1.0, modRL: 0.5f,
                ignoreMagicArmor: true, shieldSkillCurrent: 1000, shieldSpecialized: true);

            Assert.AreEqual(300.0f, ignored, Epsilon);

            var honoured = ProfileBuilder.ComposeShieldLevel(
                baseSL: 300.0f, modSL: 100.0f, baseRL: 1.0, modRL: 0.5f,
                ignoreMagicArmor: false, shieldSkillCurrent: 1000, shieldSpecialized: true);

            Assert.AreEqual(600.0f, honoured, Epsilon);
        }

        /// <summary>
        /// The shield resistance takes the same [-2, 2] clamp the armor pieces do
        /// (Creature_Combat.cs:751-752), applied before the cap.
        ///
        ///   effectiveRL = clamp(4.0) = 2.0, level = 300 * 2.0 = 600, cap 1000 -> 600
        /// </summary>
        [TestMethod]
        public void ShieldLevel_ClampsResistanceBeforeApplyingTheCap()
        {
            var result = ProfileBuilder.ComposeShieldLevel(
                baseSL: 300.0f, modSL: 0.0f, baseRL: 4.0, modRL: 0.0f,
                ignoreMagicArmor: false, shieldSkillCurrent: 1000, shieldSpecialized: true);

            Assert.AreEqual(600.0f, result, Epsilon);
        }
    }
}
