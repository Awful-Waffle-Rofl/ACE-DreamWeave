using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

// aliased rather than a plain `using ACE.Server.Entity;` - that namespace carries its own Spell/SpellType
// shapes and this file already depends on the ACE.Entity.Enum ones
using Eligibility = ACE.Server.ClassAbilities.DrainSurplusEligibility;
using Distribution = ACE.Server.ClassAbilities.DrainSurplusDistribution;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the five Blood Mage mechanics built 2026-08-03 - Sanguine Reserve, Exsanguinate,
    /// Weakened Blood, Crimson Harvest and Blood Price - at the only layer that can be tested here: the
    /// pure statics each one's arithmetic was extracted into, plus the tunable defaults they read.
    ///
    /// The stateful halves (Player_ClassAbilityBuffs' charge pool and Blood Price payment,
    /// Creature_ClassAbilityDebuffs' Weakened Blood mark, and the re-entrant Crimson Harvest fan-out in
    /// WorldObject_Magic) all need a live Player/landblock and are not covered here, the same limit
    /// Frenzy and Nether Rush have. That is precisely why the arithmetic is in these statics: what CAN be
    /// tested is, and what is left inline is only wiring.
    /// </summary>
    [TestClass]
    public class BloodMageMechanicsTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;
        private static long L(string key) => PropertyManager.GetLong(key).Item;

        // ---- Sanguine Reserve: the charge ramp -----------------------------------------------------

        [TestMethod]
        public void SanguineReserve_DamageMultiplier_IsOnePlusSevenPercentPerCharge()
        {
            // BLOOD-MAGE-DESIGN sec 3: +7% per charge, caps 3/4/5 by rank -> +21/28/35%
            Assert.AreEqual(1.21, BloodChargeMath.DamageMultiplier(3, 0.07), 1e-6);
            Assert.AreEqual(1.28, BloodChargeMath.DamageMultiplier(4, 0.07), 1e-6);
            Assert.AreEqual(1.35, BloodChargeMath.DamageMultiplier(5, 0.07), 1e-6);
        }

        [TestMethod]
        public void SanguineReserve_DamageMultiplier_NoChargesOrBadTunableIsANoOp()
        {
            Assert.AreEqual(1.0, BloodChargeMath.DamageMultiplier(0, 0.07), 1e-9);
            Assert.AreEqual(1.0, BloodChargeMath.DamageMultiplier(-2, 0.07), 1e-9);

            // a negative per-charge tunable must never become a damage PENALTY
            Assert.AreEqual(1.0, BloodChargeMath.DamageMultiplier(5, -0.07), 1e-9);
        }

        [TestMethod]
        public void SanguineReserve_TunableDefaults_MatchTheDesignTable()
        {
            Assert.AreEqual(0.07, D("class_ability_bloodmage_charge_per_stack"), 1e-9);
            Assert.AreEqual(15.0, D("class_ability_bloodmage_charge_expire_seconds"), 1e-9);

            Assert.AreEqual(3, L("class_ability_bloodmage_charge_stack_cap_r1"));
            Assert.AreEqual(4, L("class_ability_bloodmage_charge_stack_cap_r2"));
            Assert.AreEqual(5, L("class_ability_bloodmage_charge_stack_cap_r3"));

            // the caps the ability actually resolves, end to end
            Assert.AreEqual(5, BloodChargeMath.StackCap(3,
                L("class_ability_bloodmage_charge_stack_cap_r1"),
                L("class_ability_bloodmage_charge_stack_cap_r2"),
                L("class_ability_bloodmage_charge_stack_cap_r3")));
        }

        /// <summary>
        /// The number the new per-charge feedback lines quote. The player is now told the pool size and the
        /// bonus it is currently worth on every gained charge (user, live test 2026-08-03: "the amount of
        /// blood charges stacked up is not clear to me"), so the ramp has to read as a clean whole-number
        /// percentage at every rung, not just at the peak.
        /// </summary>
        [TestMethod]
        public void BloodCharge_BonusPercent_ReadsCleanlyAtEveryRungOfTheRamp()
        {
            var perStack = D("class_ability_bloodmage_charge_per_stack");

            // 1..5 charges at +7% each, the numbers the accumulation messages print
            Assert.AreEqual(7, BloodChargeMath.BonusPercent(BloodChargeMath.DamageMultiplier(1, perStack)));
            Assert.AreEqual(14, BloodChargeMath.BonusPercent(BloodChargeMath.DamageMultiplier(2, perStack)));
            Assert.AreEqual(21, BloodChargeMath.BonusPercent(BloodChargeMath.DamageMultiplier(3, perStack)));
            Assert.AreEqual(28, BloodChargeMath.BonusPercent(BloodChargeMath.DamageMultiplier(4, perStack)));
            Assert.AreEqual(35, BloodChargeMath.BonusPercent(BloodChargeMath.DamageMultiplier(5, perStack)));

            // the same helper formats the Exsanguinate burst line, so the two can never disagree
            Assert.AreEqual(105, BloodChargeMath.BonusPercent(
                ExsanguinateMath.BurstMultiplier(5, perStack, D("class_ability_exsanguinate_multiplier"))));
        }

        [TestMethod]
        public void BloodCharge_BonusPercent_NeverReportsANegativeBonus()
        {
            // an empty pool, and a mis-set tunable that produced a sub-1.0 multiplier, both read as 0
            Assert.AreEqual(0, BloodChargeMath.BonusPercent(1.0));
            Assert.AreEqual(0, BloodChargeMath.BonusPercent(0.5));
            Assert.AreEqual(0, BloodChargeMath.BonusPercent(BloodChargeMath.DamageMultiplier(0, 0.07)));
        }

        // ---- Exsanguinate: "the pool is consumed once per CAST", as a call-graph invariant ---------

        private static string ReadServerSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, "repo root (a directory containing Source/ACE.Server/WorldObjects) not found above the test output dir");

            var path = Path.Combine(dir.FullName, "Source", relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.IsTrue(File.Exists(path), $"expected source file not found: {path}");

            return File.ReadAllText(path);
        }

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;

            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;

            return count;
        }

        /// <summary>
        /// EXSANGUINATE CONSUMES THE POOL AT MOST ONCE PER CAST. Since 2026-08-03 the whole Curse of Raven
        /// Fury ring carries the burst (user: "whole ring on raven fury + exsanguinate"), which is only
        /// coherent if the eight projectiles SHARE one consumption instead of each attempting their own.
        ///
        /// That property is structural, not arithmetic, so this is a source-level guard rather than a unit
        /// test - the stateful half needs a live Player and a landblock. It pins the call graph that makes
        /// the property true:
        ///
        ///   ClearBloodChargeStacks() has ONE call site, inside ApplyBloodChargeDamage's burst branch;
        ///   ApplyBloodChargeDamage has TWO callers, and only ONE of them can pass exsanguinateEligible;
        ///   the one that can is ApplyLifeProjectileBloodCharge, called once per cast;
        ///   SpellProjectile - the per-projectile damage site - cannot reach the pool at all.
        ///
        /// The last assertion is the one that matters most. Moving the pool read back into
        /// SpellProjectile.CalculateDamage is the obvious-looking simplification, and it silently reverts
        /// the ring to "whichever projectile lands first takes the burst, the other seven get nothing".
        /// </summary>
        [TestMethod]
        public void Exsanguinate_PoolConsumption_IsReachableFromExactlyOneCastTimeCallSite()
        {
            var buffs = ReadServerSource("ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");
            var projectile = ReadServerSource("ACE.Server/WorldObjects/SpellProjectile.cs");
            var magic = ReadServerSource("ACE.Server/WorldObjects/WorldObject_Magic.cs");

            // the pool is emptied in exactly one place: the declaration, plus the single call in the burst
            Assert.AreEqual(2, CountOf(buffs, "ClearBloodChargeStacks("),
                "ClearBloodChargeStacks should have exactly one declaration and one call site");

            // one declaration plus exactly two calls, one per life-damage site
            Assert.AreEqual(3, CountOf(buffs, "ApplyBloodChargeDamage("),
                "ApplyBloodChargeDamage should have exactly one declaration and two callers");

            // and only one of those two can spend the pool
            Assert.AreEqual(1, CountOf(buffs, "ApplyBloodChargeDamage(exsanguinateEligible: true"),
                "exactly one caller may pass exsanguinateEligible: true - the cast-time resolver");
            Assert.AreEqual(1, CountOf(buffs, "ApplyBloodChargeDamage(exsanguinateEligible: false"),
                "the Harm site must hardcode exsanguinateEligible: false");

            // THE LOAD-BEARING ONE: the per-projectile damage site must not be able to read the pool
            Assert.AreEqual(0, CountOf(projectile, "ApplyBloodChargeDamage"),
                "SpellProjectile must not read the Blood Charge pool - the burst is resolved once per cast, " +
                "not once per projectile, or a Raven Fury ring gives the burst to whichever projectile lands first");

            // the cast-time resolver runs once per projectile cast, from one site
            Assert.AreEqual(1, CountOf(magic, "ApplyLifeProjectileBloodCharge("),
                "ApplyLifeProjectileBloodCharge should be called from exactly one cast site");
            Assert.AreEqual(1, CountOf(buffs, "public void ApplyLifeProjectileBloodCharge("),
                "ApplyLifeProjectileBloodCharge should have exactly one declaration");
        }

        /// <summary>
        /// THE OTHER HALF OF THE EXCLUSION, which is wiring rather than arithmetic and so is guarded at the
        /// source level: neither player-side life-damage site may grant a charge without first asking the
        /// cast's outcome. Deleting either gate would restore the exact bug the user reported - a Hecatomb
        /// that empties the pool and immediately puts a charge back into it, so the pool reads 1/5 the
        /// instant after it burst.
        ///
        /// The Drain grant in WorldObject_Magic is deliberately NOT in scope: Drain is not a life projectile,
        /// cannot be eligible, and its outcome is Accrue by construction.
        /// </summary>
        [TestMethod]
        public void Exsanguinate_ChargeGrant_IsGatedOnTheCastOutcomeAtBothPlayerSites()
        {
            var buffs = ReadServerSource("ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");

            const string call = "TryGrantBloodCharge();";

            var sites = 0;

            for (var i = buffs.IndexOf(call, StringComparison.Ordinal); i >= 0; i = buffs.IndexOf(call, i + call.Length, StringComparison.Ordinal))
            {
                sites++;

                var start = Math.Max(0, i - 160);
                var window = buffs.Substring(start, i - start);

                Assert.IsTrue(window.Contains("ExsanguinateMath.GrantsCharge("),
                    $"the TryGrantBloodCharge call at offset {i} is not gated on the cast's BloodChargeCastOutcome - " +
                    "an exsanguinating cast must never accrue a charge");
            }

            Assert.AreEqual(2, sites,
                "TryGrantBloodCharge should be called from exactly two player-side sites: the Harm damage site and the life-projectile damage site");
        }

        // ---- Exsanguinate: the firing rule, the burst, the resistance ignore -----------------------

        [TestMethod]
        public void Exsanguinate_Burst_AppliesThePoolAtThreeTimesPerChargeValue()
        {
            // design's own worked example: a full 5-charge pool becomes +105% instead of +35%
            Assert.AreEqual(2.05, ExsanguinateMath.BurstMultiplier(5, 0.07, 3.0), 1e-6);
            Assert.AreEqual(1.35, BloodChargeMath.DamageMultiplier(5, 0.07), 1e-6);

            // the containment lever named in the design: 3.0 -> 2.5, per-charge value untouched
            Assert.AreEqual(1.875, ExsanguinateMath.BurstMultiplier(5, 0.07, 2.5), 1e-6);
        }

        [TestMethod]
        public void Exsanguinate_Burst_DegradesToThePlainRampRatherThanReducingDamage()
        {
            // a multiplier below 1.0 must never make spending the pool WORSE than holding it
            Assert.AreEqual(BloodChargeMath.DamageMultiplier(4, 0.07), ExsanguinateMath.BurstMultiplier(4, 0.07, 0.5), 1e-6);
            Assert.AreEqual(BloodChargeMath.DamageMultiplier(4, 0.07), ExsanguinateMath.BurstMultiplier(4, 0.07, 1.0), 1e-6);

            // an empty pool bursts for nothing
            Assert.AreEqual(1.0, ExsanguinateMath.BurstMultiplier(0, 0.07, 3.0), 1e-9);
        }

        [TestMethod]
        public void Exsanguinate_ResistanceIgnore_MovesResistanceAQuarterOfTheWayToUnresisted()
        {
            // target resists health drain to 0.60; ignoring 25% lifts it to 0.60 + 0.25*0.40 = 0.70,
            // so damage is multiplied by 0.70 / 0.60
            Assert.AreEqual(0.70 / 0.60, ExsanguinateMath.ResistanceIgnoreMultiplier(0.60, 0.25), 1e-6);

            // full ignore is exactly 1/r - the resistance is erased for the strike
            Assert.AreEqual(1.0 / 0.60, ExsanguinateMath.ResistanceIgnoreMultiplier(0.60, 1.0), 1e-6);

            // a fraction above 1.0 is clamped, never extrapolated into a vulnerability
            Assert.AreEqual(1.0 / 0.60, ExsanguinateMath.ResistanceIgnoreMultiplier(0.60, 4.0), 1e-6);
        }

        [TestMethod]
        public void Exsanguinate_ResistanceIgnore_IsANoOpWhenTheTargetIsNotResistant()
        {
            // THE LOAD-BEARING CASE: a target under Weakened Blood carries a combined HealthDrain modifier
            // of 2.0+. If that value were ever passed here, "moving it toward 1.0" would CUT damage. The
            // guard is what makes the caller's resistance-only split safe.
            Assert.AreEqual(1.0, ExsanguinateMath.ResistanceIgnoreMultiplier(2.50, 0.25), 1e-9);
            Assert.AreEqual(1.0, ExsanguinateMath.ResistanceIgnoreMultiplier(1.00, 0.25), 1e-9);

            // degenerate inputs
            Assert.AreEqual(1.0, ExsanguinateMath.ResistanceIgnoreMultiplier(0.0, 0.25), 1e-9);
            Assert.AreEqual(1.0, ExsanguinateMath.ResistanceIgnoreMultiplier(-1.0, 0.25), 1e-9);
            Assert.AreEqual(1.0, ExsanguinateMath.ResistanceIgnoreMultiplier(0.60, 0.0), 1e-9);
        }

        /// <summary>
        /// THE FIRING RULE, exhaustively. User ruling, live test 2026-08-03: "exsang when full charges and on
        /// cast of heca/raven - otherwise accrue a blood charge."
        ///
        /// Every combination of the four inputs is asserted here rather than a sample of them, because the
        /// complaint this replaced was that the outcome was unpredictable: a rule that is only spot-checked
        /// leaves exactly the gap the player would find first.
        /// </summary>
        [TestMethod]
        public void Exsanguinate_Resolve_BurstsOnlyOnAnEligibleCastAtAFullPool()
        {
            // the one firing case: eligible spell, owns the ability, pool at the rank's cap
            Assert.AreEqual(BloodChargeCastOutcome.Burst, ExsanguinateMath.Resolve(true, true, 3, 3));
            Assert.AreEqual(BloodChargeCastOutcome.Burst, ExsanguinateMath.Resolve(true, true, 4, 4));
            Assert.AreEqual(BloodChargeCastOutcome.Burst, ExsanguinateMath.Resolve(true, true, 5, 5));

            // a partial pool accrues, at every rung below the cap. THIS is the behaviour change: the
            // predecessor burst here too, for whatever the pool happened to hold.
            for (var stacks = 0; stacks < 5; stacks++)
                Assert.AreEqual(BloodChargeCastOutcome.Accrue, ExsanguinateMath.Resolve(true, true, stacks, 5),
                    $"a pool of {stacks}/5 must accrue, not burst");

            // an ineligible cast (Harm, Drain) never bursts, however full the pool
            Assert.AreEqual(BloodChargeCastOutcome.Accrue, ExsanguinateMath.Resolve(false, true, 5, 5));

            // no Exsanguinate: a full pool is just the Sanguine Reserve ramp
            Assert.AreEqual(BloodChargeCastOutcome.Accrue, ExsanguinateMath.Resolve(true, false, 5, 5));

            // no Sanguine Reserve at all: cap 0 means there is no pool to spend, so T3 is inert without T1
            Assert.AreEqual(BloodChargeCastOutcome.Accrue, ExsanguinateMath.Resolve(true, true, 0, 0));
            Assert.AreEqual(BloodChargeCastOutcome.Accrue, ExsanguinateMath.Resolve(true, true, 3, 0));

            // an over-cap pool (a rank lowered while charges were held) is full, not rejected
            Assert.AreEqual(BloodChargeCastOutcome.Burst, ExsanguinateMath.Resolve(true, true, 7, 5));
        }

        /// <summary>
        /// THE MUTUAL EXCLUSION, at the layer that can prove it: one cast either bursts or accrues, never
        /// both and never neither. "Cannot accrue a blood charge in the same spell attack as exsang" (user,
        /// 2026-08-03).
        ///
        /// This is provable as arithmetic only because both halves are derived from ONE value. Before the
        /// change the burst was a bool and the grant was unconditional, so the two could not be compared at
        /// all - the pool emptied and refilled to 1 on the same cast.
        /// </summary>
        [TestMethod]
        public void Exsanguinate_BurstAndAccrue_AreExclusiveAndExhaustiveForEveryInput()
        {
            foreach (var eligible in new[] { false, true })
            {
                foreach (var owns in new[] { false, true })
                {
                    for (var cap = 0; cap <= 5; cap++)
                    {
                        for (var stacks = 0; stacks <= 7; stacks++)
                        {
                            var outcome = ExsanguinateMath.Resolve(eligible, owns, stacks, cap);

                            var bursts = ExsanguinateMath.Bursts(outcome);
                            var grants = ExsanguinateMath.GrantsCharge(outcome);

                            Assert.AreNotEqual(bursts, grants,
                                $"eligible={eligible} owns={owns} stacks={stacks} cap={cap}: a cast must do exactly one of burst / accrue");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// THE COOLDOWN IS GONE, AND ITS TUNABLE WITH IT. It was the reason an identical Hecatomb resolved
        /// differently from one cast to the next - a 10-second gate the player had no readout for. A full
        /// pool is the gate now, and it is one the player counted into themselves.
        ///
        /// This pins the removal rather than trusting it: a re-added default would put the invisible
        /// condition back the moment something read it.
        /// </summary>
        [TestMethod]
        public void Exsanguinate_HasNoCooldownTunable()
        {
            Assert.IsFalse(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey("class_ability_exsanguinate_cooldown_seconds"),
                "class_ability_exsanguinate_cooldown_seconds was removed with the code that read it - the firing gate is a FULL pool, not a clock");
        }

        [TestMethod]
        public void Exsanguinate_TunableDefaults_MatchTheDesignTable()
        {
            Assert.AreEqual(3.0, D("class_ability_exsanguinate_multiplier"), 1e-9);
            Assert.AreEqual(0.25, D("class_ability_exsanguinate_resist_ignore"), 1e-9);
        }

        // ---- Weakened Blood: the target-side vulnerability -----------------------------------------

        [TestMethod]
        public void WeakenedBlood_ResistanceMod_IsTheTopThreeRungsOfTheVulnerabilityLadder()
        {
            var r1 = D("class_ability_weakenedblood_resist_r1");
            var r2 = D("class_ability_weakenedblood_resist_r2");
            var r3 = D("class_ability_weakenedblood_resist_r3");

            Assert.AreEqual(2.00, r1, 1e-9);
            Assert.AreEqual(2.50, r2, 1e-9);
            Assert.AreEqual(3.10, r3, 1e-9);   // the retail Incantation value exactly
            Assert.AreEqual(20.0, D("class_ability_weakenedblood_duration_seconds"), 1e-9);

            Assert.AreEqual(2.00, WeakenedBloodMath.ResistanceMod(1, r1, r2, r3), 1e-9);
            Assert.AreEqual(2.50, WeakenedBloodMath.ResistanceMod(2, r1, r2, r3), 1e-9);
            Assert.AreEqual(3.10, WeakenedBloodMath.ResistanceMod(3, r1, r2, r3), 1e-9);

            // unlearned is no vulnerability; an out-of-range persisted rank clamps to max, never to zero
            Assert.AreEqual(1.00, WeakenedBloodMath.ResistanceMod(0, r1, r2, r3), 1e-9);
            Assert.AreEqual(3.10, WeakenedBloodMath.ResistanceMod(9, r1, r2, r3), 1e-9);
        }

        [TestMethod]
        public void WeakenedBlood_ResistanceMod_NeverReturnsAResistance()
        {
            // a value below 1.0 on this axis would be a RESISTANCE, which the mark must never produce
            Assert.AreEqual(1.0, WeakenedBloodMath.ResistanceMod(1, 0.5, 2.5, 3.1), 1e-9);
        }

        [TestMethod]
        public void WeakenedBlood_Refresh_TakesTheMaxOfBothTermsIndependently()
        {
            // a rank-1 blood mage refreshing a live rank-3 mark must not downgrade it to 2.00,
            // but must still be able to extend it
            var (mod, expire) = WeakenedBloodMath.Refresh(now: 100.0, existingMod: 3.10, existingExpire: 105.0,
                                                          incomingMod: 2.00, incomingExpire: 120.0);
            Assert.AreEqual(3.10, mod, 1e-9);
            Assert.AreEqual(120.0, expire, 1e-9);

            // and a rank-3 application must not SHORTEN a longer-lived mark
            (mod, expire) = WeakenedBloodMath.Refresh(now: 100.0, existingMod: 2.00, existingExpire: 130.0,
                                                      incomingMod: 3.10, incomingExpire: 120.0);
            Assert.AreEqual(3.10, mod, 1e-9);
            Assert.AreEqual(130.0, expire, 1e-9);
        }

        [TestMethod]
        public void WeakenedBlood_Refresh_IgnoresAnExpiredMarkEntirely()
        {
            // a lapsed rank-3 mark must not resurrect itself through a later rank-1 hit
            var (mod, expire) = WeakenedBloodMath.Refresh(now: 200.0, existingMod: 3.10, existingExpire: 105.0,
                                                          incomingMod: 2.00, incomingExpire: 220.0);
            Assert.AreEqual(2.00, mod, 1e-9);
            Assert.AreEqual(220.0, expire, 1e-9);
        }

        [TestMethod]
        public void WeakenedBlood_IsActive_ExpiresAtTheBoundary()
        {
            Assert.IsTrue(WeakenedBloodMath.IsActive(119.9, 120.0));
            Assert.IsFalse(WeakenedBloodMath.IsActive(120.0, 120.0));
            Assert.IsFalse(WeakenedBloodMath.IsActive(0.0, 0.0));
        }

        /// <summary>
        /// THE DRAIN VISUAL OUTRANKS THE FESTER VISUAL, AND THE CHAT LINE IS THE ACTUAL TELL.
        ///
        /// Measured against the client's own portal.dat rather than assumed: Fester Other V/VI and the
        /// Incantation all carry TargetEffect 0x26 (RegenDownREd), while Harm Other and Drain Health Other
        /// carry 0x20 (HealthDownRed). Both are enqueued on the same creature in the same tick - Fester's
        /// from inside HandleCastSpell_Boost/_Transfer, the spell's own from DoSpellEffects immediately
        /// after the handler returns - so whichever goes LAST is the one the player sees.
        ///
        /// This was tried both ways against a live client, and the user picked drain-wins:
        ///
        ///   delayed one tick  -> Fester is visible, drain is displaced. Rejected: "it seems the fester is
        ///                        now applying on drain instead of the drain effect. Drain effect is better
        ///                        ... revert to show the drain as a priority over fester effect."
        ///   immediate (this)  -> drain is visible, Fester is swamped. The mark is confirmed by the caster's
        ///                        chat line instead, which was the underlying need all along.
        ///
        /// So this guards the ABSENCE of the delay, which is the fragile half - a future reader who sees a
        /// regen-down script that never renders will be tempted to defer it again, silently trading away the
        /// drain burst. The chat line assertion is what keeps the mark confirmable without it.
        /// </summary>
        [TestMethod]
        public void WeakenedBlood_FesterVisual_IsImmediate_SoTheDrainScriptLandsOnTop()
        {
            var buffs = ReadServerSource("ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");

            var start = buffs.IndexOf("private void ApplyWeakenedBloodFester(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "ApplyWeakenedBloodFester not found");

            var end = buffs.IndexOf("\n        }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "could not delimit ApplyWeakenedBloodFester");

            var body = buffs.Substring(start, end - start);

            Assert.IsFalse(body.Contains("AddDelayForOneTick()"),
                "the Fester TargetEffect broadcast must stay IMMEDIATE - deferring it makes Fester the visible script and displaces the drain burst, which the user explicitly rejected on 2026-08-03");

            Assert.IsTrue(body.Contains("GameMessageScript(target.Guid, spell.TargetEffect"),
                "the broadcast must still be the spell's own TargetEffect, read from portal.dat rather than hardcoded");

            Assert.IsTrue(body.Contains("SendClassAbilityBuffMessage("),
                "the caster chat confirmation is the ONLY reliable tell once the Fester script is swamped by the drain - without it the mark is unconfirmable");
        }

        /// <summary>
        /// EXSANGUINATE MUST REACH CURSE OF RAVEN FURY, WHICH IS UNTARGETED.
        ///
        /// Probed from portal.dat 2026-08-03: Raven Fury (3818) carries NonComponentTargetType None, while
        /// Martyr's Hecatomb VII (2766) and the Incantation (4428) carry Creature. All three are
        /// MetaSpellType LifeProjectile with DamageType Health, so the ONLY thing separating them at the
        /// cast-time gate is whether a target exists.
        ///
        /// The bug this guards: ApplyLifeProjectileBloodCharge gated unconditionally on
        /// `target is not Creature || target is Player`. For a 360-degree ring there is no aimed target, so
        /// `target` is null or the caster and that gate returned every single time - Exsanguinate could
        /// never fire on Raven Fury, while Hecatomb worked, which is exactly what the live test reported
        /// ("exsang didnt seem to work with raven's fury"). It failed silently: no log, no message, just a
        /// pool that never spent.
        ///
        /// Source wiring rather than arithmetic, so it is pinned here. PvE gating is NOT what this
        /// weakens - SpellProjectile.CalculateDamage re-checks `targetPlayer == null` per projectile.
        /// </summary>
        [TestMethod]
        public void Exsanguinate_CastTimeGate_DoesNotRequireATargetForUntargetedRingSpells()
        {
            var buffs = ReadServerSource("ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");

            var start = buffs.IndexOf("public void ApplyLifeProjectileBloodCharge(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "ApplyLifeProjectileBloodCharge not found");

            var end = buffs.IndexOf("\n        }", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "could not delimit ApplyLifeProjectileBloodCharge");

            var body = buffs.Substring(start, end - start);

            Assert.IsTrue(body.Contains("NonComponentTargetType != ItemType.None"),
                "the cast-time target check must be conditioned on the spell actually BEING targeted - an unconditional target check silently disables Exsanguinate for Curse of Raven Fury, which is untargeted");

            // The eligibility itself must still be passed as a literal true from this site: it is what
            // makes "only a life projectile can spend the pool" a property of the call graph.
            Assert.IsTrue(body.Contains("exsanguinateEligible: true"),
                "the life-projectile site is the only caller allowed to pass exsanguinateEligible true");

            Assert.IsTrue(body.Contains("SpellType.LifeProjectile") && body.Contains("DamageType.Health"),
                "the type and damage-type gates must remain - a mana or stamina life projectile still spends nothing");
        }

        /// <summary>
        /// THE DRAIN EXCLUSION, pinned so it cannot silently come back.
        ///
        /// User ruling 2026-08-02: "Lets exclude drains from the blood rend/vuln ... Blood rend/vuln should
        /// apply to harm, heca, raven." Weakened Blood is that vulnerability. An earlier revision of the
        /// design doc listed Drain among the beneficiaries and a build followed it; this test is what makes
        /// the next such revision fail loudly instead of shipping.
        ///
        /// This is a live gate, not a decorative predicate: HandleCastSpell_Transfer branches on
        /// DamageBenefits to choose between GetHealthDrainResistanceOnly (no axis) and
        /// GetResistanceMod(HealthDrain) (axis). Flipping the Transfer arm to true changes runtime damage.
        /// </summary>
        [TestMethod]
        public void WeakenedBlood_DamageBenefits_ExcludesDrainAndCoversHarmHecatombAndRavenFury()
        {
            // DRAIN. Excluded by ruling - the whole point of this test.
            Assert.IsFalse(WeakenedBloodMath.DamageBenefits(SpellType.Transfer));

            // Harm (a Health Boost) and the two life projectiles (Martyr's Hecatomb, Curse of Raven Fury)
            Assert.IsTrue(WeakenedBloodMath.DamageBenefits(SpellType.Boost));
            Assert.IsTrue(WeakenedBloodMath.DamageBenefits(SpellType.FellowBoost));
            Assert.IsTrue(WeakenedBloodMath.DamageBenefits(SpellType.LifeProjectile));

            // nothing else reads the mark: war and void bolts are on the elemental axis, not this one,
            // and enchantments/dispels deal no damage for a vulnerability to act on
            Assert.IsFalse(WeakenedBloodMath.DamageBenefits(SpellType.Projectile));
            Assert.IsFalse(WeakenedBloodMath.DamageBenefits(SpellType.Enchantment));
            Assert.IsFalse(WeakenedBloodMath.DamageBenefits(SpellType.EnchantmentProjectile));
            Assert.IsFalse(WeakenedBloodMath.DamageBenefits(SpellType.Dispel));
            Assert.IsFalse(WeakenedBloodMath.DamageBenefits(SpellType.Undef));
        }

        /// <summary>
        /// Applying the mark and benefiting from it are separate questions, and only the second is
        /// excluded for Drain. Nothing in the math layer gates application - Player.TryApplyWeakenedBlood
        /// is called from the Drain path unconditionally - so this pins the arithmetic that a Drain-applied
        /// mark is a full-strength one, i.e. the exclusion did not accidentally weaken what Drain sets up
        /// for the Harm and Hecatomb that follow.
        /// </summary>
        [TestMethod]
        public void WeakenedBlood_AMarkAppliedByDrain_IsFullStrengthForTheSpellsThatDoBenefit()
        {
            var r3 = D("class_ability_weakenedblood_resist_r3");

            // whatever landed it, rank 3 is rank 3
            Assert.AreEqual(3.10, WeakenedBloodMath.ResistanceMod(3, D("class_ability_weakenedblood_resist_r1"),
                D("class_ability_weakenedblood_resist_r2"), r3), 1e-9);

            // and a Harm or Hecatomb arriving under it resolves against the full value
            Assert.IsTrue(WeakenedBloodMath.DamageBenefits(SpellType.Boost));
            Assert.AreEqual(3.10f, WeakenedBloodMath.VulnerabilityMod(1.0, r3), 1e-6f);
        }

        [TestMethod]
        public void WeakenedBlood_VulnerabilityMod_TakesTheMaxNeverTheProduct()
        {
            // a maxed Blood Rending caster (2.50) plus a rank-3 mark (3.10) is 3.10, NOT 7.75
            Assert.AreEqual(3.10f, WeakenedBloodMath.VulnerabilityMod(2.50, 3.10), 1e-6f);

            // and the weapon term wins when it is the larger one
            Assert.AreEqual(2.50f, WeakenedBloodMath.VulnerabilityMod(2.50, 2.00), 1e-6f);

            // nothing applied, and a rend below 1.0 (a resistance, not a vulnerability), both floor at 1.0
            Assert.AreEqual(1.0f, WeakenedBloodMath.VulnerabilityMod(1.0, 1.0), 1e-9f);
            Assert.AreEqual(1.0f, WeakenedBloodMath.VulnerabilityMod(0.4, 1.0), 1e-9f);
            Assert.AreEqual(2.00f, WeakenedBloodMath.VulnerabilityMod(0.4, 2.00), 1e-6f);
        }

        /// <summary>
        /// The mark now also lands the retail FESTER enchantment, so the player can see it (user, live test
        /// 2026-08-03). This pins BOTH halves of that choice: which rung of the Fester ladder each rank
        /// uses, and the numeric spell ids those enum members actually carry.
        ///
        /// The id assertions are not redundant with the enum names. SpellId is a 6000-member implicitly
        /// numbered enum, so inserting one member shifts every id after it - and the rung above the two this
        /// uses is not even named Fester (FesterOther7 = 2178 is "Decrepitude's Grasp"), which is exactly
        /// the kind of neighbour an off-by-one lands on unnoticed. Verified against ace_world.spell.
        /// </summary>
        [TestMethod]
        public void WeakenedBlood_FesterSpell_TracksTheSameLadderRungsAsTheResistanceMod()
        {
            // rank -> the same rung the 2.00 / 2.50 / 3.10 vulnerability values come from:
            // Vulnerability Other V, VI, and the Incantation
            Assert.AreEqual(SpellId.FesterOther5, WeakenedBloodMath.FesterSpell(1));
            Assert.AreEqual(SpellId.FesterOther6, WeakenedBloodMath.FesterSpell(2));
            Assert.AreEqual(SpellId.FesterOther8, WeakenedBloodMath.FesterSpell(3));

            // unlearned applies nothing; an out-of-range persisted rank clamps to rank 3, the same way
            // ResistanceMod treats one
            Assert.AreEqual(SpellId.Undef, WeakenedBloodMath.FesterSpell(0));
            Assert.AreEqual(SpellId.Undef, WeakenedBloodMath.FesterSpell(-2));
            Assert.AreEqual(SpellId.FesterOther8, WeakenedBloodMath.FesterSpell(9));

            // the ids themselves, against ace_world.spell:
            //   175  Fester Other V
            //   176  Fester Other VI
            //   4489 Incantation of Fester Other
            Assert.AreEqual(175u, (uint)WeakenedBloodMath.FesterSpell(1));
            Assert.AreEqual(176u, (uint)WeakenedBloodMath.FesterSpell(2));
            Assert.AreEqual(4489u, (uint)WeakenedBloodMath.FesterSpell(3));

            // and the rung deliberately skipped between them, so a future "make the tiers contiguous"
            // edit has to argue with a name rather than a number
            Assert.AreEqual(2178u, (uint)SpellId.FesterOther7);
        }

        /// <summary>
        /// Applying Fester does not move the vulnerability. The mark and the enchantment are separate on
        /// purpose - the registry's top-layer selection is by PowerLevel, never by StatModValue - so the
        /// resistance arithmetic must be exactly what it was before the enchantment existed.
        /// </summary>
        [TestMethod]
        public void WeakenedBlood_FesterApplication_DoesNotChangeTheVulnerabilityValues()
        {
            var r1 = D("class_ability_weakenedblood_resist_r1");
            var r2 = D("class_ability_weakenedblood_resist_r2");
            var r3 = D("class_ability_weakenedblood_resist_r3");

            Assert.AreEqual(2.00, WeakenedBloodMath.ResistanceMod(1, r1, r2, r3), 1e-9);
            Assert.AreEqual(2.50, WeakenedBloodMath.ResistanceMod(2, r1, r2, r3), 1e-9);
            Assert.AreEqual(3.10, WeakenedBloodMath.ResistanceMod(3, r1, r2, r3), 1e-9);

            // still MAX, still never a product
            Assert.AreEqual(3.10f, WeakenedBloodMath.VulnerabilityMod(2.50, 3.10), 1e-6f);
        }

        // ---- Crimson Harvest: nearest-first target selection ---------------------------------------

        [TestMethod]
        public void CrimsonHarvest_TunableDefaults_AreFourTargetsWithinEightMetres()
        {
            Assert.AreEqual(8.0, D("class_ability_crimsonharvest_radius"), 1e-9);
            Assert.AreEqual(4, L("class_ability_crimsonharvest_max_targets"));
        }

        [TestMethod]
        public void CrimsonHarvest_SelectsNearestFirstAndCapsAtFour()
        {
            // six candidates, all in range; the two furthest must be dropped
            var distances = new double[] { 7.0, 1.0, 5.0, 2.0, 6.5, 3.0 };

            CollectionAssert.AreEqual(new[] { 1, 3, 5, 2 },
                CrimsonHarvestMath.SelectSecondaryTargets(distances, 8.0, 4));
        }

        [TestMethod]
        public void CrimsonHarvest_ExcludesAnythingOutsideTheRadius()
        {
            var distances = new double[] { 9.0, 8.0, 12.0, 0.5 };

            // 8.0 is the radius itself and is INSIDE; 9.0 and 12.0 are out
            CollectionAssert.AreEqual(new[] { 3, 1 },
                CrimsonHarvestMath.SelectSecondaryTargets(distances, 8.0, 4));

            // nobody in range, and degenerate settings, all yield no extra targets rather than throwing
            Assert.AreEqual(0, CrimsonHarvestMath.SelectSecondaryTargets(new double[] { 20.0 }, 8.0, 4).Length);
            Assert.AreEqual(0, CrimsonHarvestMath.SelectSecondaryTargets(distances, 0.0, 4).Length);
            Assert.AreEqual(0, CrimsonHarvestMath.SelectSecondaryTargets(distances, 8.0, 0).Length);
            Assert.AreEqual(0, CrimsonHarvestMath.SelectSecondaryTargets(null, 8.0, 4).Length);
        }

        [TestMethod]
        public void CrimsonHarvest_TiesResolveInCandidateOrder()
        {
            // a stack of identically-placed creatures must pick deterministically, not arbitrarily
            var distances = new double[] { 4.0, 4.0, 4.0, 4.0, 4.0 };

            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 },
                CrimsonHarvestMath.SelectSecondaryTargets(distances, 8.0, 4));
        }

        // ---- Blood Price: health for damage, on any school ------------------------------------------

        [TestMethod]
        public void BloodPrice_RankLadders_MatchTheDesignTable()
        {
            var c1 = D("class_ability_bloodprice_health_cost_r1");
            var c2 = D("class_ability_bloodprice_health_cost_r2");
            var c3 = D("class_ability_bloodprice_health_cost_r3");
            var b1 = D("class_ability_bloodprice_damage_r1");
            var b2 = D("class_ability_bloodprice_damage_r2");
            var b3 = D("class_ability_bloodprice_damage_r3");

            Assert.AreEqual(0.03, c1, 1e-9);
            Assert.AreEqual(0.04, c2, 1e-9);
            Assert.AreEqual(0.05, c3, 1e-9);
            Assert.AreEqual(0.08, b1, 1e-9);
            Assert.AreEqual(0.16, b2, 1e-9);
            Assert.AreEqual(0.24, b3, 1e-9);
            Assert.AreEqual(0.20, D("class_ability_bloodprice_min_health_fraction"), 1e-9);

            Assert.AreEqual(0.04, BloodPriceMath.HealthCostFraction(2, c1, c2, c3), 1e-9);
            Assert.AreEqual(0.24, BloodPriceMath.DamageBonus(3, b1, b2, b3), 1e-9);

            // unlearned costs and gains nothing; an out-of-range rank clamps to max
            Assert.AreEqual(0.0, BloodPriceMath.HealthCostFraction(0, c1, c2, c3), 1e-9);
            Assert.AreEqual(0.0, BloodPriceMath.DamageBonus(0, b1, b2, b3), 1e-9);
            Assert.AreEqual(0.05, BloodPriceMath.HealthCostFraction(7, c1, c2, c3), 1e-9);

            // negative tunables must not heal the caster or invert the bonus
            Assert.AreEqual(0.0, BloodPriceMath.HealthCostFraction(1, -0.5, c2, c3), 1e-9);
            Assert.AreEqual(0.0, BloodPriceMath.DamageBonus(1, -0.5, b2, b3), 1e-9);
        }

        [TestMethod]
        public void BloodPrice_Resolve_SpendsPercentOfCurrentHealthNotMaximum()
        {
            // rank 3 at 400/1000 health: 5% of CURRENT (400) = 20, not 5% of max (50)
            var charge = BloodPriceMath.Resolve(400, 1000, 0.05, 0.24, 0.20);

            Assert.AreEqual(20u, charge.HealthCost);
            Assert.AreEqual(1.24f, charge.DamageMultiplier, 1e-6f);
        }

        [TestMethod]
        public void BloodPrice_Resolve_BelowTheFloorIsFreeAndUnbuffed_BothOrNeither()
        {
            // 199/1000 is below the 20% floor: no cost AND no bonus
            var below = BloodPriceMath.Resolve(199, 1000, 0.05, 0.24, 0.20);
            Assert.AreEqual(0u, below.HealthCost);
            Assert.AreEqual(1.0f, below.DamageMultiplier, 1e-9f);

            // exactly at the floor still qualifies
            var atFloor = BloodPriceMath.Resolve(200, 1000, 0.05, 0.24, 0.20);
            Assert.AreEqual(10u, atFloor.HealthCost);
            Assert.AreEqual(1.24f, atFloor.DamageMultiplier, 1e-6f);
        }

        [TestMethod]
        public void BloodPrice_Resolve_CanNeverKillItsOwnCaster()
        {
            // a retuned cost fraction of 100% still leaves the caster alive at 1 health
            var charge = BloodPriceMath.Resolve(500, 1000, 1.0, 0.24, 0.20);
            Assert.AreEqual(499u, charge.HealthCost);

            // and the degenerate zero cases resolve to a no-op rather than an underflow
            Assert.AreEqual(0u, BloodPriceMath.Resolve(0, 1000, 0.05, 0.24, 0.20).HealthCost);
            Assert.AreEqual(1.0f, BloodPriceMath.Resolve(0, 1000, 0.05, 0.24, 0.20).DamageMultiplier, 1e-9f);
            Assert.AreEqual(0u, BloodPriceMath.Resolve(400, 0, 0.05, 0.24, 0.20).HealthCost);
        }

        [TestMethod]
        public void BloodPrice_SpellQualifies_CoversWarVoidAndTheTwoLifeNukes()
        {
            // war and void bolts, and the life projectiles (Hecatomb, Curse of Raven Fury)
            Assert.IsTrue(BloodPriceMath.SpellQualifies(true, SpellType.Projectile, affectsHealth: false));
            Assert.IsTrue(BloodPriceMath.SpellQualifies(true, SpellType.LifeProjectile, affectsHealth: true));

            // Harm - a harmful Health boost
            Assert.IsTrue(BloodPriceMath.SpellQualifies(true, SpellType.Boost, affectsHealth: true));

            // a harmful Mana or Stamina boost is not health damage
            Assert.IsFalse(BloodPriceMath.SpellQualifies(true, SpellType.Boost, affectsHealth: false));
        }

        [TestMethod]
        public void BloodPrice_SpellQualifies_ExcludesDrainAndEveryNonDamagingSpell()
        {
            // DRAIN. Excluded mechanically, not by school: its damage is bounded by spell.TransferCap, so a
            // multiplier on its roll is unreachable and the health cost would be pure loss.
            Assert.IsFalse(BloodPriceMath.SpellQualifies(true, SpellType.Transfer, affectsHealth: true));

            // debuffs and dispels are harmful but deal no damage for the bonus to act on
            Assert.IsFalse(BloodPriceMath.SpellQualifies(true, SpellType.Enchantment, affectsHealth: true));
            Assert.IsFalse(BloodPriceMath.SpellQualifies(true, SpellType.EnchantmentProjectile, affectsHealth: true));
            Assert.IsFalse(BloodPriceMath.SpellQualifies(true, SpellType.Dispel, affectsHealth: true));

            // and nothing beneficial ever costs health, whatever its shape
            Assert.IsFalse(BloodPriceMath.SpellQualifies(false, SpellType.Projectile, affectsHealth: true));
            Assert.IsFalse(BloodPriceMath.SpellQualifies(false, SpellType.Boost, affectsHealth: true));
        }

        // ---- Transfusion: the Drain surplus cascade also reaches the caster's own summons ------------
        //
        // User, 2026-08-03: "Transfusion should also work on player summons in range."
        //
        // Neither a Player nor a Pet can be constructed under the test host (Player's static initializer
        // blows up), so the recipient list itself is untestable here. What IS testable is the pure gate,
        // the pure distribution math, and - for the wiring that joins them - the source of
        // WorldObject_Magic, in the same source-guard style the Exsanguinate call-graph tests use above.

        /// <summary>
        /// THE DESIGN CONSEQUENCE OF THE CHANGE: a fellowship is no longer required.
        ///
        /// Before this, CasterQualifies ANDed in hasFellowship, so a solo Blood Mage was on the retail path
        /// no matter what stood next to them - and on the retail path a caster at full health drains for
        /// zero. A lone summoner with a hurt pet is exactly the case the user asked for, so the last
        /// condition became hasFellowship OR hasSummon.
        /// </summary>
        [TestMethod]
        public void Transfusion_SoloCasterWithASummon_NowQualifiesWhereTheyPreviouslyDidNot()
        {
            Assert.IsTrue(Eligibility.CasterQualifies(
                    casterIsPlayer: true, casterIsTransferDestination: true, targetIsEligibleVictim: true,
                    transfusionRank: 1, hasFellowship: false, hasSummon: true),
                "a solo Blood Mage with an active summon must get the cascade - this is the whole point of the change");
        }

        [TestMethod]
        public void Transfusion_CasterWithNeitherFellowshipNorSummon_StillTakesTheRetailPath()
        {
            Assert.IsFalse(Eligibility.CasterQualifies(
                    casterIsPlayer: true, casterIsTransferDestination: true, targetIsEligibleVictim: true,
                    transfusionRank: 1, hasFellowship: false, hasSummon: false),
                "with nobody to cascade to at all, Drain must behave exactly as retail - no widened capacity, no split, no payout");

            // and the summon must not become a back door around any of the conditions that were already there
            Assert.IsFalse(Eligibility.CasterQualifies(true, true, true, 0, false, true), "unlearned Transfusion");
            Assert.IsFalse(Eligibility.CasterQualifies(false, true, true, 1, false, true), "monster caster");
            Assert.IsFalse(Eligibility.CasterQualifies(true, true, false, 1, false, true), "PvP victim");
            Assert.IsFalse(Eligibility.CasterQualifies(true, false, true, 1, false, true), "caster is not the transfer destination");
        }

        /// <summary>
        /// A SUMMON'S MISSING HEALTH MUST FEED THE HEADROOM THAT UNCAPS THE DRAIN, not just the payout.
        ///
        /// This is the half that is easy to get half-right. Retail bounds the transfer by the caster's OWN
        /// missing health, so a caster at full health has missingDest == 0, the overflow scalar is 0 and the
        /// drain deals nothing. Adding summons to the recipient LIST alone would leave a solo caster at full
        /// health with a hurt pet still draining for zero - there would be a recipient and no surplus to give
        /// them. The summon's missing health has to reach fellowMissingTotal, exactly as a fellow's does.
        ///
        /// The arithmetic below replicates the cap site; the source assertions pin the chain that carries a
        /// summon's figure into it, since that chain needs live world state and cannot be executed here.
        /// </summary>
        [TestMethod]
        public void Transfusion_SummonMissingHealth_WidensTheTransferBudgetForAFullHealthCaster()
        {
            // the retail zero-drain case: the caster is at FULL health
            const uint missingDest = 0;

            // one summon in range, missing 300. GetDrainSurplusFellows accumulates this into
            // fellowMissingTotal through the same path a fellow takes.
            var recipientMissing = new uint[] { 300 };

            ulong fellowMissingTotal = 0;

            foreach (var m in recipientMissing)
                fellowMissingTotal += m;

            // HandleCastSpell_Transfer's cap site, replicated
            var maxDestVitalChange = missingDest;

            if (fellowMissingTotal > 0)
                maxDestVitalChange = (uint)Math.Min(uint.MaxValue, (ulong)missingDest + fellowMissingTotal);

            Assert.AreEqual(300u, maxDestVitalChange,
                "with the summon counted the drain has 300 points of headroom; without it maxDestVitalChange stays 0 and the drain is scaled away entirely");

            // and the surplus the caster cannot absorb reaches the summon in full at rank 3
            var delivered = Distribution.ApplyShare(maxDestVitalChange - missingDest,
                Distribution.ShareFraction(3,
                    D("class_ability_transfusion_share_r1"),
                    D("class_ability_transfusion_share_r2"),
                    D("class_ability_transfusion_share_r3"),
                    0.0));

            CollectionAssert.AreEqual(new uint[] { 300 }, Distribution.Distribute(delivered, recipientMissing));

            // ---- the wiring that makes the figure above a summon's, and not only a fellow's ----
            var magic = ReadServerSource("ACE.Server/WorldObjects/WorldObject_Magic.cs");

            Assert.IsTrue(magic.Contains("TryAddRecipient(caster.CurrentActivePet);"),
                "the caster's primary summon must be offered to the same per-recipient filter fellows go through");
            Assert.IsTrue(magic.Contains("TryAddRecipient(caster.SecondaryActivePet);"),
                "the Summon 2x second slot must be offered too");

            // the accumulator inside TryAddRecipient, and the hand-off to the out parameter the cap reads
            Assert.IsTrue(magic.Contains("missingTotal += missing;"),
                "every accepted recipient, summon included, must add its missing health to the running total");
            Assert.IsTrue(magic.Contains("fellowMissingTotal = missingTotal;"),
                "that running total is what the caller's transfer budget is widened by");

            Assert.IsTrue(magic.Contains("maxDestVitalChange = (uint)Math.Min(uint.MaxValue, (ulong)missingDest + fellowMissingTotal);"),
                "the cap site must still add the recipients' missing health to the caster's own, or none of the above reaches the drain");
        }

        /// <summary>
        /// The recipient list has to be a Creature list, and the payout has to survive a recipient with no
        /// session. SendChatMessage is a Player method - a Pet has neither the method nor a session behind
        /// it - so a summon's heal notice goes to its OWNER, who is always the caster because only the
        /// caster's own summons are ever collected.
        ///
        /// The visual is deliberately NOT redirected: EnqueueBroadcast is a WorldObject concern, so it still
        /// plays on the summon itself and everyone nearby sees which body the drain fed.
        /// </summary>
        [TestMethod]
        public void Transfusion_SummonPayout_BroadcastsOnTheSummonButMessagesTheOwner()
        {
            var magic = ReadServerSource("ACE.Server/WorldObjects/WorldObject_Magic.cs");

            Assert.IsTrue(magic.Contains("private List<Creature> GetDrainSurplusFellows("),
                "the recipient list must be typed as Creature - a Pet is a Creature and is not a Player, so a List<Player> cannot carry a summon at all");

            Assert.AreEqual(0, CountOf(magic, "private List<Player> GetDrainSurplusFellows("),
                "the old Player-only signature must be gone, not merely overloaded");

            // the visual still fires on the recipient itself, summon or fellow
            Assert.IsTrue(magic.Contains("fellow.EnqueueBroadcast(new GameMessageScript(fellow.Guid, DrainSurplusHealEffect, spell.Formula.Scale));"),
                "every recipient still broadcasts the heal script for itself");

            // the chat split: players get the existing wording, summons route to their owner
            Assert.IsTrue(magic.Contains("if (fellow is Player fellowPlayer)"),
                "the chat notice must be type-tested - calling SendChatMessage on a sessionless summon is the crash this guards");
            Assert.IsTrue(magic.Contains("summon.P_PetOwner.SendChatMessage("),
                "a summon's heal notice must be delivered to its owner instead");
            Assert.IsTrue(magic.Contains("$\"Your {summon.Name} gains {fellowGain} points of health"),
                "the owner's line must name the summon, or it is indistinguishable from their own heal line");

            // and the pre-existing fellow wording is untouched
            Assert.IsTrue(magic.Contains("$\"You gain {fellowGain} points of health due to {Name} casting {spell.Name} on {targetCreature.Name}\""),
                "the fellow wording must not change - only the summon branch is new");
        }

        /// <summary>
        /// NO CHANGE TO DRAIN DAMAGE. Widening the recipient list must not move srcVitalChange or the
        /// caster's own share by a single point at any rank: the share fraction is still applied only to
        /// the part of the transfer the caster could not absorb, and the caster is still paid missingDest.
        /// </summary>
        [TestMethod]
        public void Transfusion_AddingSummons_DoesNotTouchTheDrainOrTheCastersOwnShare()
        {
            var magic = ReadServerSource("ACE.Server/WorldObjects/WorldObject_Magic.cs");

            Assert.IsTrue(magic.Contains("DrainSurplusDistribution.ApplyShare(destVitalChange - missingDest, drainShareFraction)"),
                "the delivery fraction must still be applied to the SURPLUS alone - applying it any earlier would scale the drain itself");

            Assert.IsTrue(magic.Contains("destVitalChange = missingDest;"),
                "the caster must still keep exactly their own missing health, whatever the recipients are");

            // the payout loop must never write back to the drain figure
            var loopStart = magic.IndexOf("if (fellowShares != null)", StringComparison.Ordinal);
            Assert.IsTrue(loopStart >= 0, "the recipient payout loop was not found");

            // the block closes at 12 spaces of indent; the inner for-loop closes at 16, so this cannot
            // over-run into it or stop short of it
            var loopEnd = magic.IndexOf("\n            }", loopStart, StringComparison.Ordinal);
            Assert.IsTrue(loopEnd > loopStart, "could not delimit the recipient payout loop");

            var loop = magic.Substring(loopStart, loopEnd - loopStart);

            Assert.AreEqual(0, CountOf(loop, "srcVitalChange"),
                "the recipient payout must not touch srcVitalChange - the damage the victim took is settled before this point");
        }

        // ---- life_drain_resist_floor: GetEffectiveResistHealthDrain -------------------------------

        /// <summary>
        /// Retail sets 116 creature weenies' ResistHealthDrain to exactly 0 (bloodless constructs, wisps,
        /// crystals, plated tuskers), which made a Blood Mage's Life-school drains land for 0 damage on
        /// them. GetEffectiveResistHealthDrain must floor that at the life_drain_resist_floor tunable
        /// (default 0.75) rather than passing 0 through.
        /// </summary>
        [TestMethod]
        public void GetEffectiveResistHealthDrain_ZeroResistIsFlooredAtTheTunable()
        {
            var creature = TestCreatures.CreateQuestBearer();
            creature.ResistHealthDrain = 0.0;

            Assert.AreEqual(D("life_drain_resist_floor"), creature.GetEffectiveResistHealthDrain(), 1e-9);
        }

        /// <summary>
        /// The same floor must catch the retail cluster of creatures sitting below 0.75 but above 0
        /// (~350 creatures at 0.5, 0.25, etc.), not just the exact-zero case.
        /// </summary>
        [TestMethod]
        public void GetEffectiveResistHealthDrain_BelowFloorResistIsRaisedToTheFloor()
        {
            var creature = TestCreatures.CreateQuestBearer();
            creature.ResistHealthDrain = 0.5;

            Assert.AreEqual(D("life_drain_resist_floor"), creature.GetEffectiveResistHealthDrain(), 1e-9);
        }

        /// <summary>
        /// A creature already at or above the floor must pass through untouched, and a creature with no
        /// ResistHealthDrain property set at all must keep the existing "unset" default of 1.0 rather than
        /// being pulled down (or up) by the floor.
        /// </summary>
        [TestMethod]
        public void GetEffectiveResistHealthDrain_AtOrAboveFloorIsUnchanged()
        {
            var aboveFloor = TestCreatures.CreateQuestBearer();
            aboveFloor.ResistHealthDrain = 0.9;
            Assert.AreEqual(0.9, aboveFloor.GetEffectiveResistHealthDrain(), 1e-9);

            var unset = TestCreatures.CreateQuestBearer();
            Assert.IsNull(unset.ResistHealthDrain);
            Assert.AreEqual(1.0, unset.GetEffectiveResistHealthDrain(), 1e-9);
        }
    }
}
