using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Archer pass: multishot overflow, Pinning Shot's one-roll-per-volley, Long Draw's Missile Weapons
    /// affinity, the Double Volley readout null-player fix, and the retuned defaults. Everything here is pure
    /// (no live Player or world), plus source scans pinning the launch-path wiring that cannot run headless.
    /// </summary>
    [TestClass]
    public class ClassAbilityArcherPassTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- multishot overflow -------------------------------------------------------------------

        [TestMethod]
        public void Overflow_OneTargetThreeExtras_ThreeOverflowArrowsAtAThirdEach()
        {
            var plan = MultiShotOverflow.Plan(new[] { 0.6f, 0.6f, 0.6f }, acquiredTargets: 0);

            Assert.AreEqual(3, plan.Count);
            Assert.IsTrue(plan.All(s => s.IsOverflow), "no secondary target: every extra arrow overflows onto the primary");
            Assert.IsTrue(plan.All(s => Math.Abs(s.DamageMultiplier - 0.2f) < 1e-6f), "each overflow arrow is one third of its own 0.6");
        }

        [TestMethod]
        public void Overflow_TwoTargetsThreeExtras_OneRealExtraTwoOverflow()
        {
            // "2 targets" = the primary plus ONE acquired secondary
            var plan = MultiShotOverflow.Plan(new[] { 0.6f, 0.6f, 0.6f }, acquiredTargets: 1);

            Assert.AreEqual(3, plan.Count);
            Assert.IsFalse(plan[0].IsOverflow);
            Assert.AreEqual(0, plan[0].ExtraTargetIndex);
            Assert.AreEqual(0.6f, plan[0].DamageMultiplier, 1e-6f, "a real extra keeps its full multiplier");
            Assert.IsTrue(plan[1].IsOverflow && plan[2].IsOverflow);
            Assert.AreEqual(0.2f, plan[1].DamageMultiplier, 1e-6f);
            Assert.AreEqual(0.2f, plan[2].DamageMultiplier, 1e-6f);
        }

        [TestMethod]
        public void Overflow_EnoughTargets_NothingOverflows()
        {
            var plan = MultiShotOverflow.Plan(new[] { 0.6f, 0.6f }, acquiredTargets: 2);

            Assert.IsTrue(plan.All(s => !s.IsOverflow));
            CollectionAssert.AreEqual(new[] { 0, 1 }, plan.Select(s => s.ExtraTargetIndex).ToArray());
        }

        [TestMethod]
        public void Overflow_EachArrowUsesItsOwnMultiplier_WeaponAndAbilityAlike()
        {
            // a weapon arrow (1.0) and an ability arrow (0.6) have different multipliers; the overflow
            // fraction applies to the arrow's OWN value, not a shared one
            var plan = MultiShotOverflow.Plan(new[] { 1.0f, 0.6f }, acquiredTargets: 0);

            Assert.AreEqual(1.0f / 3.0f, plan[0].DamageMultiplier, 1e-6f);
            Assert.AreEqual(0.2f, plan[1].DamageMultiplier, 1e-6f);
        }

        [TestMethod]
        public void Overflow_DegenerateInputs_PlanNothingOrClamp()
        {
            Assert.AreEqual(0, MultiShotOverflow.Plan(null, 3).Count);
            Assert.AreEqual(0, MultiShotOverflow.Plan(new float[0], 3).Count);
            Assert.IsTrue(MultiShotOverflow.Plan(new[] { 0.6f }, -5).Single().IsOverflow, "a negative count is treated as zero targets");
            Assert.AreEqual(1.0f / 3.0f, MultiShotOverflow.OverflowFraction, 1e-9f);
        }

        [TestMethod]
        public void Overflow_PrimaryOutOfSight_DropsOverflowKeepsRealExtras()
        {
            // overflow arrows never collide, so a primary behind a wall must not take them
            var plan = MultiShotOverflow.Plan(new[] { 0.6f, 0.6f, 0.6f }, acquiredTargets: 1, primaryInSight: false);

            Assert.AreEqual(1, plan.Count, "only the arrow with an acquired (sight-checked) secondary target remains");
            Assert.AreEqual(0, plan[0].ExtraTargetIndex);
            Assert.AreEqual(0.6f, plan[0].DamageMultiplier, 1e-6f);

            Assert.AreEqual(0, MultiShotOverflow.Plan(new[] { 0.6f, 0.6f }, acquiredTargets: 0, primaryInSight: false).Count);
        }

        // ---- Pinning Shot: one roll per volley ------------------------------------------------------

        [TestMethod]
        public void PinRoll_VolleyWin_PinsWithoutDrawingAPerHitRoll()
        {
            var drew = false;

            Assert.IsTrue(PinningShotAbility.ResolveRoll(true, () => { drew = true; return false; }));
            Assert.IsFalse(drew, "a volley that won already decided: the per-hit roll must not run");
        }

        [TestMethod]
        public void PinRoll_VolleyLoss_PinsNothingEvenIfAPerHitRollWouldWin()
        {
            var drew = false;

            Assert.IsFalse(PinningShotAbility.ResolveRoll(false, () => { drew = true; return true; }));
            Assert.IsFalse(drew, "a volley that lost is final for every arrow of the volley");
        }

        [TestMethod]
        public void PinRoll_NoVolleyRoll_FallsBackToThePerHitRoll()
        {
            Assert.IsTrue(PinningShotAbility.ResolveRoll(null, () => true));
            Assert.IsFalse(PinningShotAbility.ResolveRoll(null, () => false));
        }

        [TestMethod]
        public void PinRoll_AllOrNone_FourArrowsShareOneResult()
        {
            // the volley's single result is read by each arrow that LANDS; a miss never reaches the check
            foreach (var volley in new bool?[] { true, false })
            {
                var results = Enumerable.Range(0, 4).Select(_ => PinningShotAbility.ResolveRoll(volley, () => !volley.Value)).ToArray();

                Assert.IsTrue(results.All(r => r == volley.Value), $"volley={volley}: every arrow must read the same decision");
            }
        }

        [TestMethod]
        public void PinningShot_ImmunityDefault_IsEightSeconds()
        {
            Assert.AreEqual(8.0, PropertyManager.GetDouble("class_ability_pinningshot_immunity_seconds").Item, 1e-9);
        }

        [TestMethod]
        public void PinningShot_Description_StatesTheVolleyRuleAndTheNewImmunity()
        {
            var d = new PinningShotAbility().Definition.Description;

            StringAssert.Contains(d, "rolls once");
            StringAssert.Contains(d, "8 seconds");
            Assert.IsFalse(d.Contains("15 seconds"));
        }

        // ---- Hunter's Mark ------------------------------------------------------------------------------

        [TestMethod]
        public void HuntersMark_PerRankDoubled_TwoFourSixEightTen()
        {
            var perRank = PropertyManager.GetDouble("class_ability_huntersmark_percent_per_rank").Item;

            Assert.AreEqual(0.02, perRank, 1e-9);
            Assert.AreEqual(0.10, HuntersMarkAbility.MarkPercent(5, perRank, 1.0), 1e-9);
            StringAssert.Contains(new HuntersMarkAbility().Definition.Description, "2/4/6/8/10%");
        }

        // ---- Double Volley --------------------------------------------------------------------------------

        [TestMethod]
        public void DoubleVolley_GetReadout_WithNullPlayer_DoesNotThrow_AndReportsRankOnly()
        {
            var ability = new DoubleVolleyAbility();

            var readout = ability.GetReadout(null, 2);

            Assert.IsTrue(readout.HasValue);
            Assert.AreEqual(20.0, readout.Skill, 1e-6);
            Assert.AreEqual(0.0, readout.Affinity, 1e-6, "neutral multiplier 1.0 adds nothing");
            Assert.AreEqual(0.0, readout.Gear, 1e-6);
            Assert.AreEqual(20.0, readout.Effective, 1e-6);
            StringAssert.Contains(ability.Definition.Description, "10/20/30%");
        }

        [TestMethod]
        public void DoubleVolley_GetReadout_RankZero_WithNullPlayer_IsZero()
        {
            var readout = new DoubleVolleyAbility().GetReadout(null, 0);

            Assert.AreEqual(0.0, readout.Effective, 1e-6);
        }

        // ---- Long Draw -------------------------------------------------------------------------------------

        [TestMethod]
        public void LongDraw_DeclaresMissileWeaponsAffinity()
        {
            Assert.AreEqual(Skill.MissileWeapons, new LongDrawAbility().Definition.AffinitySkill);
            StringAssert.Contains(new LongDrawAbility().Definition.Description, "Missile Weapons");
        }

        [TestMethod]
        public void LongDraw_GetReadout_WithNullPlayer_ReportsRankPeak_NeutralAffinity()
        {
            var readout = new LongDrawAbility().GetReadout(null, 3);

            Assert.AreEqual(30.0, readout.Skill, 1e-6);
            Assert.AreEqual(0.0, readout.Affinity, 1e-6);
            Assert.AreEqual(0.0, readout.Gear, 1e-6);
            Assert.AreEqual(readout.Skill + readout.Affinity + readout.Gear, readout.Effective, 1e-6);
        }

        [TestMethod]
        public void LongDraw_Peak_AffinityMultipliesTheRankTerm_NotTheGearTerm()
        {
            // Rank 3, 10%/rank, a 1.5x Missile Weapons multiplier, a 5% gear mod.
            Assert.AreEqual(0.50, LongDrawAbility.Peak(3, 0.10, 1.5, 0.05), 1e-9);
            Assert.AreEqual(0.30, LongDrawAbility.Peak(3, 0.10, 1.0, 0.0), 1e-9, "neutral multiplier is rank only");
            Assert.AreEqual(0.30, LongDrawAbility.Peak(3, 0.10, 0.0, 0.0), 1e-9, "a sub-1.0 multiplier is floored to neutral");
            Assert.AreEqual(0.05, LongDrawAbility.Peak(0, 0.10, 2.0, 0.05), 1e-9, "rank 0 contributes no rank term and no affinity, only gear");

            var peak = LongDrawAbility.Peak(3, 0.10, 1.5, 0.05);
            Assert.AreEqual(0.50, LongDrawAbility.DistanceBonus(60, 1, peak, 15, 50), 1e-9, "at/above max range the bonus is the full peak");
            Assert.AreEqual(0.25, LongDrawAbility.DistanceBonus(32.5, 1, peak, 15, 50), 1e-9, "midway is half the peak");
        }

        // ---- repricing -----------------------------------------------------------------------------------------

        [TestMethod]
        public void CritRating_IsOneOneOne_AndWaveFiveRecordsTheDrop()
        {
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, ClassAbilityRegistry.GetHandler(ClassAbilityId.CritRating).Definition.CostPerRank);

            var wave = ClassAbilityRepricing.Waves.Single(w => w.Number == 5);
            Assert.AreEqual("Archer pass", wave.Label);

            var row = wave.Rows.Single();
            Assert.AreEqual(ClassAbilityId.CritRating, row.Ability);
            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, row.OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, row.NewCosts.ToArray());
            Assert.AreEqual(RepriceAction.Keep, row.Action);
        }

        // ---- launch-path wiring (source scans: LaunchMissile needs a live world) --------------------------------

        [TestMethod]
        public void LaunchMissile_RollsPinOnce_StampsEveryProjectile_AndRoutesOverflow()
        {
            // Custom Summons Task 3 lifted the extra-arrow volley (secondary targets, overflow, the cosmetic
            // arrow) from Player_Missile into Creature_Missile so a combat pet fires the same arrows. The player
            // launch path is the two files together: Player_Missile rolls the pin and hands it to
            // Creature.FireMultiShotVolley, which carries it onto every arrow.
            var player = File.ReadAllText(FindSource("ACE.Server", "WorldObjects", "Player_Missile.cs"));
            var creature = File.ReadAllText(FindSource("ACE.Server", "WorldObjects", "Creature_Missile.cs"));
            var text = player + "\n" + creature;

            Assert.AreEqual(1, CountOf(text, "RollClassAbilityVolleyPin()"), "the volley must roll Pinning Shot exactly once");
            StringAssert.Contains(player, "LaunchProjectile(launcher, ammo, target, origin, orientation, velocity, volleyPinRoll: volleyPinRoll)");
            StringAssert.Contains(player, "FireMultiShotVolley(launcher, ammo, weapon, creature, projectileSpeed, shotMultipliers, PrimaryInSight, volleyPinRoll)",
                "the player volley hands its one pin roll and its lazy sight test to the shared volley");
            StringAssert.Contains(creature, "MultiShotOverflow.Plan(shotMultipliers, multiShotTargets.Count, multiShotTargets.Count >= shotMultipliers.Count || primaryInSight())");
            // cleave-style arrows at the primary never collide, so both overflow and the Double Volley re-fire
            // at the primary are gated on line of sight (overflow arrows were hitting through walls); the
            // sight test is lazy so an ordinary shot never runs it
            StringAssert.Contains(player, "bool PrimaryInSight() => primaryInSightCache ??= IsDirectVisible(creature);");
            Assert.IsTrue(Regex.IsMatch(player, @"if \(PrimaryInSight\(\)\)\s*FireMultiShotArrow\(launcher, ammo, creature, projectileSpeed, 1\.0f"), "the Double Volley re-fire at the primary must be sight-gated");

            // primary-side overflow, secondary extra, the Double Volley primary re-fire and the re-fire loop
            // all hand the one result on
            Assert.AreEqual(6, CountOf(text, "volleyPinRoll: volleyPinRoll"), "the launch, overflow arrow, secondary extra, re-fire primary, re-fire loop and the cosmetic LaunchProjectile (stamped before AddObject) must all carry the roll");
            StringAssert.Contains(text, "out var flightTime, volleyPinRoll)");
            Assert.IsFalse(text.Contains("cosmeticProjectile.ClassAbilityVolleyPinRoll"), "a post-AddObject stamp can be missed by a spawn-overlap collision");
        }

        [TestMethod]
        public void PinningShot_WeaponPath_ReadsTheStampFromTheDamageSource_SpellPathKeepsPerHit()
        {
            var text = File.ReadAllText(FindSource("ACE.Server", "ClassAbilities", "Abilities", "PinningShotAbility.cs"));

            StringAssert.Contains(text, "damageEvent.DamageSource?.ClassAbilityVolleyPinRoll");
            StringAssert.Contains(text, "TryPin(caster, rank, primaryTarget, null)");
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            var i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }

        private static string FindSource(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(new[] { dir.FullName, "Source" }.Concat(relative).ToArray());
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {string.Join("/", relative)} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
