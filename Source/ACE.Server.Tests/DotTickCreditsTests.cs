using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// DotTickCredits: the per-damager attribution arithmetic for one damage-over-time tick, extracted from
    /// EnchantmentManager.ApplyDamageTick when that method was restructured on 2026-09-08.
    ///
    /// WHY THE RESTRUCTURE HAPPENED. ApplyDamageTick used to clamp its accumulated tick total to the
    /// victim's current Health inside the accumulation loop, before Sanguine Ward or Mana Barrier ran. An
    /// absorber handed a figure already capped at current Health always leaves Health strictly positive, so
    /// no DoT of any size could kill a warded or barriered player - the same overkill immunity the barrier
    /// fix had just removed from the other four damage sites, one level upstream. The loop now accumulates
    /// the raw uncapped total, the absorbers run against the whole tick, the vital write is left uncapped
    /// (UpdateVitalDelta floors at zero by itself), and the health cap survives only here, for attribution.
    ///
    /// THIS IS THE PART OF THE RESTRUCTURE THAT COULD SILENTLY GO WRONG, in two separate ways, and the
    /// second one did. Damage history feeds kill credit, so the per-damager figures must sum to what was
    /// actually removed and never exceed it - and the ORDER they are written in decides who is recorded as
    /// landing the killing blow, because DamageHistory.LastDamager reads the last log entry. The first
    /// version of the restructure grouped into a Dictionary and replayed it in first-insertion order, which
    /// flips the killing blow on any tick where one damager contributes both before and after another. See
    /// GroupByLastContribution_OrdersByLastTouch_NotFirstInsertion.
    ///
    /// WHAT IS NOT COVERED HERE, AND WHY. ApplyDamageTick itself cannot be driven from this harness: it
    /// resolves each damager through WorldObject.CurrentLandblock.GetObject, so with no landblock every
    /// enchantment is skipped and the method does nothing, and the player-victim branch additionally needs a
    /// live Player, which ACE.Server.Tests cannot construct (see SanguineWardTests and ManaBarrierTests for
    /// the same limit). So the absorb-then-clamp ORDER is pinned as arithmetic in
    /// ManaBarrierTests.AbsorbDamage_ClampBeforeAbsorbIsWhatSpares_TheDoTOverkillCase, the attribution is
    /// pinned here, and the wiring between them is verified by reading the method.
    /// </summary>
    [TestClass]
    public class DotTickCreditsTests
    {
        private static uint Sum(IEnumerable<uint> values) => (uint)values.Sum(v => (long)v);

        private static KeyValuePair<string, float> C(string damager, float amount) =>
            new KeyValuePair<string, float>(damager, amount);

        // ---- grouping order ----------------------------------------------------------------------

        /// <summary>
        /// THE KILLING-BLOW REGRESSION, found in review of the 2026-09-08 restructure. ApplyDamageTick
        /// replays DamageHistory.Add over the grouped entries, and DamageHistory.LastDamager resolves to the
        /// attacker on the last negative-amount log entry, so this order decides who landed the killing
        /// blow on a finishing tick.
        ///
        /// The original code called Add once per enchantment as it walked the list, which is
        /// last-contribution order. The first restructure grouped into a Dictionary and replayed
        /// damagers.ToList(), which is FIRST-insertion order - a Dictionary does not move a key when its
        /// value is updated. The two differ whenever one damager contributes both before and after another,
        /// which is the ordinary stacked-DoT shape: [A, B, A] is one caster holding two spell categories on
        /// the target plus a second caster holding one.
        /// </summary>
        [TestMethod]
        public void GroupByLastContribution_OrdersByLastTouch_NotFirstInsertion()
        {
            // three top-layer DoTs in enchantment-list order: A, then B, then A again
            var grouped = DotTickCredits.GroupByLastContribution(new List<KeyValuePair<string, float>>
            {
                C("A", 100.0f),
                C("B", 40.0f),
                C("A", 60.0f),
            });

            CollectionAssert.AreEqual(new[] { "B", "A" }, grouped.Select(e => e.Key).ToList(),
                "last-contribution order: A contributed most recently, so A is replayed last and keeps the kill");

            // and the amounts travel with their keys
            Assert.AreEqual(40.0f, grouped[0].Value, 1e-3f);
            Assert.AreEqual(160.0f, grouped[1].Value, 1e-3f, "A's two contributions must be summed");
        }

        [TestMethod]
        public void GroupByLastContribution_KeepsListOrderWhenNoDamagerRepeats()
        {
            var grouped = DotTickCredits.GroupByLastContribution(new List<KeyValuePair<string, float>>
            {
                C("A", 1.0f),
                C("B", 2.0f),
                C("C", 3.0f),
            });

            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, grouped.Select(e => e.Key).ToList());
        }

        [TestMethod]
        public void GroupByLastContribution_MovesADamagerToTheEndEveryTimeItContributes()
        {
            // A, B, A, B: B contributed last, so B is replayed last
            var grouped = DotTickCredits.GroupByLastContribution(new List<KeyValuePair<string, float>>
            {
                C("A", 1.0f),
                C("B", 2.0f),
                C("A", 4.0f),
                C("B", 8.0f),
            });

            CollectionAssert.AreEqual(new[] { "A", "B" }, grouped.Select(e => e.Key).ToList());
            Assert.AreEqual(5.0f, grouped[0].Value, 1e-3f);
            Assert.AreEqual(10.0f, grouped[1].Value, 1e-3f);

            // a single damager holding several DoTs is one entry, and stays one entry
            var solo = DotTickCredits.GroupByLastContribution(new List<KeyValuePair<string, float>>
            {
                C("A", 1.0f),
                C("A", 2.0f),
                C("A", 4.0f),
            });

            CollectionAssert.AreEqual(new[] { "A" }, solo.Select(e => e.Key).ToList());
            Assert.AreEqual(7.0f, solo[0].Value, 1e-3f);
        }

        [TestMethod]
        public void GroupByLastContribution_HandlesNoContributions()
        {
            Assert.AreEqual(0, DotTickCredits.GroupByLastContribution(new List<KeyValuePair<string, float>>()).Count);
            Assert.AreEqual(0, DotTickCredits.GroupByLastContribution<string>(null).Count);
        }

        /// <summary>
        /// The grouping feeds Total and Split, so the two invariants those carry must survive the reorder.
        /// They do, because the amounts travel with their keys; the only thing the order changes is which
        /// entry absorbs the rounding remainder, which was always unspecified.
        /// </summary>
        [TestMethod]
        public void GroupByLastContribution_PreservesTheTotalAndTheSplitInvariants()
        {
            var grouped = DotTickCredits.GroupByLastContribution(new List<KeyValuePair<string, float>>
            {
                C("A", 1200.0f),
                C("B", 2300.0f),
                C("A", 1500.0f),
            });

            var rawAmounts = grouped.Select(e => e.Value).ToList();

            Assert.AreEqual(5000.0f, DotTickCredits.Total(rawAmounts), 1e-3f);

            var credits = DotTickCredits.Split(rawAmounts, 300);

            Assert.AreEqual(300u, Sum(credits));

            foreach (var credit in credits)
                Assert.IsTrue(credit <= 300u);

            // B dealt 2300 of 5000 and sorts first; A dealt 2700 and sorts last
            CollectionAssert.AreEqual(new uint[] { 138, 162 }, credits);
        }

        // ---- the total ---------------------------------------------------------------------------

        [TestMethod]
        public void Total_SumsEveryContribution_AndIsUncapped()
        {
            var raw = new List<float> { 1200.0f, 2300.0f, 1500.0f };

            // 5000, deliberately far above any plausible current health - the whole point is that the
            // absorbers see the tick as thrown, not a health-capped share of it
            Assert.AreEqual(5000.0f, DotTickCredits.Total(raw), 1e-3f);

            Assert.AreEqual(0.0f, DotTickCredits.Total(new List<float>()), 1e-9f);
            Assert.AreEqual(0.0f, DotTickCredits.Total(null), 1e-9f);
        }

        // ---- the split ---------------------------------------------------------------------------

        /// <summary>
        /// The coordinator's reported case: 300 Health, a stacked tick nominally worth 5000 across three
        /// damagers, a barrier absorbing 30%. 3500 is written, Health floors at zero, and the damagers are
        /// credited with the 300 that was actually removed - not the 3500 the write asked for, and not the
        /// 5000 that was rolled.
        /// </summary>
        [TestMethod]
        public void Split_CreditsOnlyTheHealthActuallyRemoved_OnAnOverkillTick()
        {
            var raw = new List<float> { 1200.0f, 2300.0f, 1500.0f };

            var credits = DotTickCredits.Split(raw, 300);

            Assert.AreEqual(300u, Sum(credits), "credits must sum to the health actually removed");

            foreach (var credit in credits)
                Assert.IsTrue(credit <= 300u, "no damager may be credited more than was removed");

            // proportional to what each dealt: 1200/2300/1500 of 5000 -> 72/138/90
            CollectionAssert.AreEqual(new uint[] { 72, 138, 90 }, credits);
        }

        [TestMethod]
        public void Split_SumsExactly_AcrossAwkwardRoundings()
        {
            // three equal damagers and a total that does not divide by three: independent per-damager
            // rounding would give 33+33+33 = 99 or 34+34+34 = 102, never 100
            var thirds = DotTickCredits.Split(new List<float> { 10.0f, 10.0f, 10.0f }, 100);
            Assert.AreEqual(100u, Sum(thirds));

            // seven damagers, a prime total, and uneven contributions
            var raw = new List<float> { 3.5f, 11.25f, 0.75f, 40.0f, 7.125f, 19.5f, 2.875f };

            foreach (var applied in new uint[] { 1, 2, 7, 13, 97, 101, 999, 65537 })
            {
                var credits = DotTickCredits.Split(raw, applied);

                Assert.AreEqual(raw.Count, credits.Length);
                Assert.AreEqual(applied, Sum(credits), $"credits must sum exactly for applied={applied}");

                foreach (var credit in credits)
                    Assert.IsTrue(credit <= applied, $"no credit may exceed applied={applied}");
            }
        }

        [TestMethod]
        public void Split_IsProportionalToWhatEachDamagerDealt()
        {
            // one damager dealing three quarters of the tick gets three quarters of the credit
            var credits = DotTickCredits.Split(new List<float> { 300.0f, 100.0f }, 400);

            CollectionAssert.AreEqual(new uint[] { 300, 100 }, credits);

            // and a damager contributing nothing is credited nothing
            var withZero = DotTickCredits.Split(new List<float> { 50.0f, 0.0f, 50.0f }, 80);

            Assert.AreEqual(0u, withZero[1]);
            Assert.AreEqual(80u, Sum(withZero));
        }

        [TestMethod]
        public void Split_CreditsNothingWhenNothingLanded()
        {
            var raw = new List<float> { 10.0f, 20.0f };

            // the tick was fully absorbed, or the victim was invincible
            var none = DotTickCredits.Split(raw, 0);
            Assert.AreEqual(2, none.Length);
            Assert.AreEqual(0u, Sum(none));

            // nothing was dealt in the first place
            var noDamage = DotTickCredits.Split(new List<float> { 0.0f, 0.0f }, 50);
            Assert.AreEqual(0u, Sum(noDamage));

            // no damagers at all
            Assert.AreEqual(0, DotTickCredits.Split(new List<float>(), 50).Length);
            Assert.AreEqual(0, DotTickCredits.Split(null, 50).Length);
        }

        [TestMethod]
        public void Split_SurvivesANegativeContribution_WithoutUnderflowing()
        {
            // not reachable from a real DoT term, but the split subtracts unsigned cumulative totals, so a
            // negative contribution must clamp rather than wrap to four billion
            var credits = DotTickCredits.Split(new List<float> { 100.0f, -30.0f, 50.0f }, 60);

            Assert.AreEqual(60u, Sum(credits));

            foreach (var credit in credits)
                Assert.IsTrue(credit <= 60u);
        }

        [TestMethod]
        public void Split_HandsTheWholeTotalToASingleDamager()
        {
            var credits = DotTickCredits.Split(new List<float> { 4321.0f }, 300);

            CollectionAssert.AreEqual(new uint[] { 300 }, credits);
        }
    }
}
