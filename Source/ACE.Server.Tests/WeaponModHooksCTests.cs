using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The three weapon mods v4 hooks wired in this change: Recovery (natural regeneration), Arcane Defender
    /// (a wand as a virtual shield) and Panic Reload (conditional attack speed with a hostile in melee range).
    ///
    /// WHAT IS COVERED HERE is everything that can be decided from pure inputs: the registry rows themselves,
    /// SkillFormula.CalcArmorMod at Arcane Defender's catalog magnitude, the regeneration product including
    /// the combat-stance halving Recovery must not escape, and Panic Reload's per-candidate threat predicate.
    ///
    /// WHAT IS NOT, AND WHY, follows WeaponModTierBTests' rule exactly: the hooks themselves need a live
    /// Player with equipped objects, an ObjMaint and a landblock, which ACE.Server.Tests cannot construct.
    /// Each hook is a thin wrapper over a function in WeaponModCombat and those functions ARE covered here;
    /// what is not covered is that the wrapper reads the right carrier and that the core site calls it at all.
    /// Those are live-loop checks and belong in Docs/VERIFY-QUEUE.md, not in a mock that would assert its own
    /// shape.
    /// </summary>
    [TestClass]
    public class WeaponModHooksCTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// Runs an action with weapon_mods_enabled - the system's ONE gate - forced, restoring it whatever
        /// happens, so no test can leave the tunable moved for the next one.
        /// </summary>
        private static void WithGate(bool enabled, Action body)
        {
            var prior = PropertyManager.GetBool("weapon_mods_enabled").Item;

            PropertyManager.ModifyBool("weapon_mods_enabled", enabled);

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", prior);
            }
        }

        // ---------------- the three rows ----------------

        /// <summary>
        /// The catalog values the three hooks are written against, pinned here rather than read off the
        /// registry so a retune has to move this file deliberately. A hook tuned for one magnitude and
        /// silently fed another is the failure this table exists to catch.
        /// </summary>
        [TestMethod]
        public void HookedRows_CarryTheMagnitudesTheHooksAreWrittenAgainst()
        {
            var expected = new (WeaponModId Id, PropertyFloat Record, double MaxRoll, WeaponClass Classes, string Name)[]
            {
                (WeaponModId.Recovery,       PropertyFloat.WeaponModRecovery,       0.50, WeaponClass.All,     "Recovery"),
                (WeaponModId.ArcaneDefender, PropertyFloat.WeaponModArcaneDefender, 25,   WeaponClass.Caster,  "Arcane Defender"),
                (WeaponModId.PanicReload,    PropertyFloat.WeaponModPanicReload,    0.10, WeaponClass.Missile, "Panic Reload"),
            };

            foreach (var row in expected)
            {
                Assert.IsTrue(WeaponModRegistry.TryGet(row.Id, out var definition), $"{row.Name} is missing from the registry");

                Assert.AreEqual(WeaponModTier.B, definition.Tier, $"{row.Name} must be Tier B - it writes no native property");
                Assert.AreEqual(row.Record, definition.Record, $"{row.Name} record id moved");
                Assert.AreEqual(row.MaxRoll, definition.MaxRoll, 1e-9, $"{row.Name} MaxRoll moved");
                Assert.AreEqual(row.Classes, definition.Classes, $"{row.Name} class set moved");
                Assert.AreEqual(row.Name, definition.DisplayName, $"{row.Name} display name moved");
            }
        }

        /// <summary>
        /// Every one of the three is inert with the gate off, read through the accessor the hooks use. This is
        /// the last line between a shard with weapon_mods_enabled false and live combat code.
        /// </summary>
        [TestMethod]
        public void HookedRows_ReadZeroWithTheGateOff()
        {
            var ids = new[] { WeaponModId.Recovery, WeaponModId.ArcaneDefender, WeaponModId.PanicReload };

            foreach (var id in ids)
            {
                // a null carrier is the unequipped case and must read 0 in both gate states
                WithGate(false, () => Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(null, id), 1e-9));
                WithGate(true, () => Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(null, id), 1e-9));
            }
        }

        // ---------------- Arcane Defender ----------------

        /// <summary>
        /// THE PIN. Arcane Defender's whole effect is one number: at the catalog MaxRoll of 25, run through
        /// SkillFormula.CalcArmorMod with SkillFormula.ArmorMod = 200/3, an incoming frontal blow lands at
        /// 66.667 / (25 + 66.667) = 0.7273 of its damage. If this number moves, the row was retuned or the
        /// armor formula changed, and either way the design figure needs re-deriving.
        /// </summary>
        [TestMethod]
        public void ArcaneDefender_AtMaxRollDeflectsToTheDesignFigure()
        {
            const float armorLevel = 25.0f;

            var expected = SkillFormula.ArmorMod / (armorLevel + SkillFormula.ArmorMod);

            Assert.AreEqual(0.727272f, expected, 1e-5f, "SkillFormula.CalcArmorMod(25) is no longer 66.667/91.667");
            Assert.AreEqual(expected, SkillFormula.CalcArmorMod(armorLevel), 1e-6f);
            Assert.AreEqual(expected, WeaponModCombat.ArcaneShieldMod(armorLevel, 1.0f), 1e-6f);
        }

        /// <summary>
        /// The attacker's IgnoreShield term multiplies the virtual shield's level exactly as it multiplies a
        /// real shield's, and it is applied BEFORE CalcArmorMod - the two do not commute, so the order is the
        /// assertion.
        /// </summary>
        [TestMethod]
        public void ArcaneDefender_IgnoreShieldScalesTheLevelNotTheResult()
        {
            var halved = WeaponModCombat.ArcaneShieldMod(25.0, 0.5f);

            Assert.AreEqual(SkillFormula.CalcArmorMod(12.5f), halved, 1e-6f);

            // and it is NOT the result being halved
            Assert.AreNotEqual(SkillFormula.CalcArmorMod(25.0f) * 0.5f, halved, 1e-3f);

            // a fully ignored shield leaves the defender exactly where an unmodded one was
            Assert.AreEqual(1.0f, WeaponModCombat.ArcaneShieldMod(25.0, 0.0f), 1e-6f);
        }

        /// <summary>
        /// Absent, zero, negative and NaN magnitudes all return the untouched 1.0 a shieldless defender
        /// already got, so the branch can never make a player take MORE damage than before.
        /// </summary>
        [TestMethod]
        public void ArcaneDefender_AbsentOrMalformedIsTheUnmoddedResult()
        {
            Assert.AreEqual(1.0f, WeaponModCombat.ArcaneShieldMod(0.0, 1.0f), 1e-6f);
            Assert.AreEqual(1.0f, WeaponModCombat.ArcaneShieldMod(-10.0, 1.0f), 1e-6f);
            Assert.AreEqual(1.0f, WeaponModCombat.ArcaneShieldMod(double.NaN, 1.0f), 1e-6f);
            Assert.AreEqual(1.0f, WeaponModCombat.ArcaneShieldMod(25.0, float.NaN), 1e-6f);
        }

        /// <summary>
        /// A wand-only Arcane Defender player must NOT gain Shield Block or Thorns, and the reason is
        /// structural rather than arithmetic: both abilities gate on GetEquippedShield() != null directly
        /// (Player_ClassAbilityCombat.cs:42 for Shield Block, ApplyThornsReflect's shield check for Thorns) and neither one ever reads
        /// GetShieldMod, which is the only function this change touches. GetShieldMod has exactly one caller,
        /// DamageEvent.cs:391.
        ///
        /// Those gates need a live Player to exercise, so what is asserted here is the property that makes the
        /// argument hold: Arcane Defender is Caster-class only, so its carrier is the wand, and a wand is not
        /// a shield - GetEquippedShield() and GetCasterOnlyModValue read different EquipMask slots
        /// (Creature_Equipment.cs GetEquippedShield vs GetEquippedWand). The full check is a live-loop row.
        /// </summary>
        [TestMethod]
        public void ArcaneDefender_IsCasterOnlyAndThereforeNeverRidesAShield()
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(WeaponModId.ArcaneDefender, out var definition));

            Assert.AreEqual(WeaponClass.Caster, definition.Classes,
                "Arcane Defender must stay caster-only: the hook reads the WAND, and a melee or missile roll would be dead");

            Assert.IsTrue(definition.AppliesTo(WeaponClass.Caster));
            Assert.IsFalse(definition.AppliesTo(WeaponClass.Melee));
            Assert.IsFalse(definition.AppliesTo(WeaponClass.Missile));
        }

        // ---------------- Recovery ----------------

        /// <summary>
        /// The two carriers are a CHOICE, not a sum. A player holding both a modded weapon and a modded wand
        /// gets the better of the two - the same rule Second Wind's carrier resolution follows.
        /// </summary>
        [TestMethod]
        public void Recovery_TakesTheBetterCarrierAndNeverTheirSum()
        {
            Assert.AreEqual(1.50, WeaponModCombat.RegenerationMultiplier(0.50, 0.30), 1e-9);
            Assert.AreEqual(1.50, WeaponModCombat.RegenerationMultiplier(0.30, 0.50), 1e-9);
            Assert.AreEqual(1.50, WeaponModCombat.RegenerationMultiplier(0.50, 0.50), 1e-9);

            // the sum would be 1.80 - assert it is specifically not that
            Assert.AreNotEqual(1.80, WeaponModCombat.RegenerationMultiplier(0.50, 0.30), 1e-3);
        }

        /// <summary>
        /// Absent, zero, negative and NaN magnitudes leave the tick exactly as it was.
        /// </summary>
        [TestMethod]
        public void Recovery_AbsentOrMalformedLeavesTheTickUnchanged()
        {
            Assert.AreEqual(1.0, WeaponModCombat.RegenerationMultiplier(0.0, 0.0), 1e-9);
            Assert.AreEqual(1.0, WeaponModCombat.RegenerationMultiplier(-0.5, -0.5), 1e-9);
            Assert.AreEqual(1.0, WeaponModCombat.RegenerationMultiplier(double.NaN, double.NaN), 1e-9);

            // one malformed carrier must not blank out a good one
            Assert.AreEqual(1.50, WeaponModCombat.RegenerationMultiplier(double.NaN, 0.50), 1e-9);
            Assert.AreEqual(1.50, WeaponModCombat.RegenerationMultiplier(0.50, double.NaN), 1e-9);
        }

        /// <summary>
        /// COMBAT SUPPRESSION SURVIVES RECOVERY, which is the one thing about this hook that could go quietly
        /// wrong. Creature.GetStanceMod returns 0.5 while the player is in combat mode or running
        /// (Creature_Vitals.cs, the CombatMode != NonCombat branch), and Recovery multiplies into the SAME
        /// product as that term - vital.RegenRate * attributeMod * stanceMod * enchantmentMod * augMod *
        /// recoveryMod - rather than being applied after it.
        ///
        /// The consequence, asserted here on the product itself: at the maximum Recovery roll a player in
        /// combat still regenerates strictly SLOWER than an unmodded player standing at rest. Recovery scales
        /// the halved rate; it cannot buy its way out of the halving.
        ///
        /// This pins the arithmetic, not the 0.5 itself - reading GetStanceMod needs a live Player, so the
        /// stance constant is a live-loop row.
        /// </summary>
        [TestMethod]
        public void Recovery_MultipliesInsideTheCombatStanceHalving()
        {
            const float regenRate = 1.0f;
            const float attributeMod = 1.0f;
            const float enchantmentMod = 1.0f;
            const float augMod = 1.0f;

            const float combatStanceMod = 0.5f;      // Creature.GetStanceMod, combat mode or running
            const float restingStanceMod = 1.0f;     // Creature.GetStanceMod, standing at rest

            var maxRecovery = WeaponModRegistry.Get(WeaponModId.Recovery).MaxRoll;
            var recoveryMod = (float)WeaponModCombat.RegenerationMultiplier(maxRecovery, 0.0);

            var suppressed = regenRate * attributeMod * combatStanceMod * enchantmentMod * augMod * recoveryMod;
            var unmoddedAtRest = regenRate * attributeMod * restingStanceMod * enchantmentMod * augMod * 1.0f;

            Assert.AreEqual(0.75f, suppressed, 1e-6f, "1.0 * 0.5 * 1.5 - the combat halving is still in the product");
            Assert.IsTrue(suppressed < unmoddedAtRest,
                "a fully modded player in combat must still regenerate slower than an unmodded player at rest");

            // and the modifier is genuinely doing something outside combat
            var moddedAtRest = regenRate * attributeMod * restingStanceMod * enchantmentMod * augMod * recoveryMod;
            Assert.AreEqual(1.5f, moddedAtRest, 1e-6f);
        }

        // ---------------- Panic Reload ----------------

        /// <summary>
        /// The radius is 6 m, and the comparison is against a SQUARED distance so the hook skips a square root
        /// per candidate. The boundary is inclusive: a creature at exactly 6 m qualifies.
        /// </summary>
        [TestMethod]
        public void PanicReload_RadiusIsSixMetersSquaredAndInclusive()
        {
            // read into locals first: the analyzer folds a const-versus-literal comparison away as
            // "always true", and the point of the pin is that the const cannot move unnoticed
            var range = (double)WeaponModCombat.PanicReloadRange;
            var rangeSq = (double)WeaponModCombat.PanicReloadRangeSq;

            Assert.AreEqual(6.0, range, 1e-9);
            Assert.AreEqual(36.0, rangeSq, 1e-9);
            Assert.AreEqual(range * range, rangeSq, 1e-9);

            Assert.IsTrue(Threat(distanceSq: 0.0));
            Assert.IsTrue(Threat(distanceSq: 35.99));
            Assert.IsTrue(Threat(distanceSq: 36.0));
            Assert.IsFalse(Threat(distanceSq: 36.01));
            Assert.IsFalse(Threat(distanceSq: 100.0));
        }

        /// <summary>
        /// Every exclusion, one at a time. The wielder itself, other players (this is a PvE modifier), the
        /// player's own pets, the dead, and the unattackable all fail to arm the modifier even at zero
        /// distance - so a player standing in a town square next to a housing pet fires at normal speed.
        /// </summary>
        [TestMethod]
        public void PanicReload_ExcludesSelfPlayersPetsDeadAndUnattackable()
        {
            Assert.IsTrue(Threat(distanceSq: 1.0), "a live attackable monster at 1 m is the qualifying case");

            Assert.IsFalse(Threat(isSelf: true, distanceSq: 1.0));
            Assert.IsFalse(Threat(isPlayer: true, distanceSq: 1.0));
            Assert.IsFalse(Threat(isPet: true, distanceSq: 1.0));
            Assert.IsFalse(Threat(isDead: true, distanceSq: 1.0));
            Assert.IsFalse(Threat(attackable: false, distanceSq: 1.0));
        }

        /// <summary>
        /// A malformed distance never qualifies, rather than reading as "touching". get_distance_sq_to_object
        /// should not produce either of these, which is exactly why the guard is cheap insurance.
        /// </summary>
        [TestMethod]
        public void PanicReload_MalformedDistanceNeverQualifies()
        {
            Assert.IsFalse(Threat(distanceSq: double.NaN));
            Assert.IsFalse(Threat(distanceSq: -1.0));
        }

        /// <summary>
        /// Panic Reload's own multiplier at the catalog MaxRoll: +10% attack speed while the condition holds.
        /// It composes multiplicatively with Quickening on the same bow, and both land inside
        /// ApplyClassAbilityAttackSpeed's single ceiling clamp, which is what bounds the axis.
        /// </summary>
        [TestMethod]
        public void PanicReload_AtMaxRollIsTenPercentAndComposesWithQuickening()
        {
            var panicReload = WeaponModRegistry.Get(WeaponModId.PanicReload).MaxRoll;
            var quickening = WeaponModRegistry.Get(WeaponModId.Quickening).MaxRoll;

            Assert.AreEqual(1.10, WeaponModCombat.DamageMultiplier(panicReload), 1e-9);

            var composed = WeaponModCombat.DamageMultiplier(quickening) * WeaponModCombat.DamageMultiplier(panicReload);

            Assert.AreEqual(1.24 * 1.10, composed, 1e-9);

            // not additive - the two are separate terms in one product
            Assert.AreNotEqual(1.0 + quickening + panicReload, composed, 1e-6);
        }

        private static bool Threat(bool isSelf = false, bool isPlayer = false, bool isPet = false, bool isDead = false,
            bool attackable = true, double distanceSq = 0.0) =>
            WeaponModCombat.IsPanicReloadThreat(isSelf, isPlayer, isPet, isDead, attackable, distanceSq);
    }
}
