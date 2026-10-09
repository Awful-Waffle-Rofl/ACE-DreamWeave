using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The two live-play retunes of 2026-07-30, each pinned against the exact behaviour it replaces.
    ///
    ///   CHANGE 1 - the Amethyst swap reported "Lost: 1 Granite tinker / Gained: 1 Granite tinker", spending a
    ///              whole salvage bag on a guaranteed no-op. A tinker replacement now excludes the material just
    ///              removed. Specials are DELIBERATELY exempt, because their magnitude is rerolled.
    ///   CHANGE 2 - Resolute rolled +21 critical damage resistance rating, on a scale nothing else in the game
    ///              touches (VERIFIED: GearCritDamageResist 375 appears on ZERO ace_world weenies). This section
    ///              is the regression guard for THAT CLASS OF DEFECT - a rolled magnitude escaping its intended
    ///              band - not a pin on any one MaxRoll value. The MaxRoll it guards has moved twice since
    ///              (2 then 3); the guard's job is to keep proving the escape can never happen again, at
    ///              whatever MaxRoll the row currently carries.
    ///
    /// Kept in its own file rather than appended to WeaponModFixTests.cs so the retunes can be reviewed apart
    /// from the correctness fixes that file records.
    ///
    /// CHANGE 2's SUBJECT MOVED, 2026-07-30 third pass. Resolute was REMOVED outright when the Tier A pool was
    /// cut to damage-oriented modifiers, so these four tests could not keep naming it. They were REPOINTED, not
    /// deleted, onto Weak Point (GearCrit 372), which at the time took the identical MaxRoll of 2 and therefore
    /// had the identical "normal 1, max 2" distribution. That Resolute itself is gone, and unreachable from
    /// every pool and lookup, is asserted in WeaponModPoolCutTests.
    ///
    /// CHANGE 2's SHAPE MOVED AGAIN, 2026-07-30 fourth pass. Weak Point's MaxRoll was retuned from 2 to 3
    /// alongside Devastation and Bloodthirst (5 to 6). The four tests below were rewritten in place - renamed
    /// and re-derived for the new "1, 2, or 3" shape - rather than deleted, because they are still the only
    /// place in the suite that reproduces the exact +21-style defect end to end (constant pin, potency-sweep
    /// derivation, full workmanship sweep, and the real reroll/swap flow). Do not read this section as
    /// redundant with WeaponModRetuneRoundTwoTests.cs's Change3 coverage and delete it: Change3 exercises the
    /// same rows from the "three retuned rows together" angle; this file is the one-row, defect-shaped angle
    /// the coverage originated from.
    /// </summary>
    [TestClass]
    public class WeaponModRetuneTests
    {
        private static readonly WeaponClass[] Classes = { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// The material name out of a layer 1 swap line - "Lost: 1 Granite tinker" / "Gained: 1 Granite tinker" -
        /// or NULL when the line describes a special instead. Reading the player-facing line on purpose: the
        /// reported defect WAS those two lines naming the same material.
        /// </summary>
        private static string TinkerMaterialName(string line)
        {
            const string suffix = " tinker";

            if (line == null || !line.EndsWith(suffix, StringComparison.Ordinal))
                return null;

            var marker = line.IndexOf(": 1 ", StringComparison.Ordinal);

            if (marker < 0)
                return null;

            var start = marker + 4;

            return line.Substring(start, line.Length - suffix.Length - start);
        }

        // ================= CHANGE 1 - a tinker is never replaced by itself =================

        /// <summary>
        /// SUPERSEDED 2026-08-06 (Amethyst rework). THE ORIGINAL DEFECT: the swap drew its replacement tinker
        /// from the whole class pool, so a melee weapon had a 1-in-4 chance (missile 1-in-3) of being handed
        /// back the material it had just lost - a full salvage bag spent on a guaranteed no-op. That whole
        /// defect CLASS is gone now, not merely fixed: Amethyst never touches tinkers at all any more, so there
        /// is no tinker-replacement draw left to exclude the removed material from. This test now asserts that
        /// structurally - across repeated swaps on a seeded weapon, the tinker log and tinker count never move
        /// at all, which makes the original defect unreachable by construction rather than merely rare.
        /// </summary>
        [TestMethod]
        public void Change1_SwapNeverTouchesTinkersSoTheOriginalDefectClassIsUnreachable()
        {
            foreach (var weaponClass in Classes)
            {
                for (var trial = 0; trial < 40; trial++)
                {
                    var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);
                    WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Pool(weaponClass, false)[0], 0.1);

                    var beforeLog = weapon.GetProperty(PropertyString.WeaponModTinkerLog);
                    var beforeCount = weapon.GetProperty(PropertyInt.WeaponModTinkerCount);

                    var probe = new WeaponModProbe(weapon);

                    for (var use = 0; use < 12 && WeaponModTinkerSet.SpecialCount(weapon) > 0; use++)
                    {
                        var context = $"{weaponClass} trial {trial} use {use}";
                        var lines = WeaponModTestKit.Swap(weapon, weaponClass, 10.0, context);

                        if (lines == null)
                            break;

                        probe.AssertInvariants(weapon, weaponClass, context);

                        Assert.AreEqual(beforeLog, weapon.GetProperty(PropertyString.WeaponModTinkerLog), $"{context}: the tinker log moved");
                        Assert.AreEqual(beforeCount, weapon.GetProperty(PropertyInt.WeaponModTinkerCount), $"{context}: the tinker count moved");
                    }
                }
            }
        }

        /// <summary>
        /// The exclusion at the roller, directly: every material in every class pool, excluded, many draws each.
        /// The draw must never return the excluded material and must never come back null, because a null there
        /// makes ApplySwap bail out AFTER the bag has been consumed.
        /// </summary>
        [TestMethod]
        public void Change1_RollTinkerNeverReturnsTheExcludedMaterial()
        {
            foreach (var weaponClass in Classes)
            {
                foreach (var excluded in WeaponTinkerTable.Pool(weaponClass))
                {
                    for (var draw = 0; draw < 200; draw++)
                    {
                        var material = WeaponModRoller.RollTinker(weaponClass, excluded.Material);

                        Assert.IsNotNull(material,
                            $"{weaponClass}: excluding {excluded.Material} produced no draw at all - ApplySwap would then refuse with the bag already gone");

                        Assert.AreNotEqual(excluded.Material, material.Value,
                            $"{weaponClass}: the draw returned the excluded {excluded.Material}");
                    }
                }

                // a material the class cannot draw anyway excludes nothing and must not narrow the pool
                var drawn = new HashSet<MaterialType>();

                for (var draw = 0; draw < 400; draw++)
                    drawn.Add(WeaponModRoller.RollTinker(weaponClass, MaterialType.Silver).Value);

                Assert.AreEqual(WeaponTinkerTable.Pool(weaponClass).Count, drawn.Count,
                    $"{weaponClass}: excluding a material outside the pool must leave every entry drawable");
            }

            // an empty pool is the ONLY null, exclusion or not
            Assert.IsNull(WeaponModRoller.RollTinker(WeaponClass.None, MaterialType.Iron));
            Assert.IsNull(WeaponModRoller.RollTinker(WeaponClass.None, null));
        }

        /// <summary>
        /// ASSERTED, NOT ASSUMED: excluding one material always leaves something to draw. Pools are melee 4,
        /// missile 3, caster 4, so the minimum after an exclusion is 2. The degenerate single-entry pool that
        /// WeaponModRoller.RollTinker's fallback exists for is NOT constructible against the shipped table - this
        /// invariant is what makes it unreachable, so it is the thing worth pinning. If a future pool is cut to
        /// one entry this test fails first, and the fallback (draw the excluded material rather than return null
        /// or spin) is what keeps the bag from being eaten for a refusal.
        /// </summary>
        [TestMethod]
        public void Change1_EveryTinkerPoolHasRoomForTheExclusion()
        {
            var expected = new Dictionary<WeaponClass, int>
            {
                { WeaponClass.Melee, 4 },
                { WeaponClass.Missile, 3 },
                { WeaponClass.Caster, 4 },
            };

            foreach (var kvp in expected)
            {
                var pool = WeaponTinkerTable.Pool(kvp.Key);

                Assert.AreEqual(kvp.Value, pool.Count, $"{kvp.Key}: tinker pool size");

                Assert.IsTrue(pool.Count - 1 >= 2,
                    $"{kvp.Key}: excluding one material leaves {pool.Count - 1} candidates. Below 2 the swap's replacement draw stops being meaningfully random and the degenerate fallback becomes reachable.");
            }
        }

        /// <summary>
        /// THE EXEMPTION, PINNED. Losing a special and gaining the same special back is a REAL and intended
        /// outcome, because the magnitude is rerolled - the player can come out ahead, which is not true of a
        /// tinker. This drives a one-special weapon through the real swap path until that outcome happens, so a
        /// future "fix" that extends the tinker exclusion to specials fails here rather than silently removing a
        /// designed upside.
        ///
        /// Odds per trial are about 1 in 50 (1-in-10 to remove the special, then 1-in-5 to redraw it out of the
        /// five-entry melee pool), so 5000 trials makes a false failure vanishingly unlikely rather than merely
        /// improbable. It was 1 in 100 before the 2026-07-30 pool cut halved the melee pool from ten to five;
        /// the trial count is deliberately left at 5000 rather than reduced with the odds.
        /// </summary>
        [TestMethod]
        public void Change1_LosingAndRegainingTheSameSpecialStaysPossible()
        {
            var prior = WeaponModTestKit.SwapTunable("weapon_mod_swap_special_chance", 1.0);

            try
            {
                var target = WeaponModRegistry.Get(WeaponModId.Bloodthirst);
                var seen = false;

                for (var trial = 0; trial < 5000 && !seen; trial++)
                {
                    var weapon = MakeOneSpecialWeapon(WeaponClass.Melee, target);

                    var lines = WeaponModTestKit.Swap(weapon, WeaponClass.Melee, 10.0, $"trial {trial}");

                    Assert.IsNotNull(lines, $"trial {trial}: a full weapon below the special cap must accept a swap");

                    if (lines[0] != $"Lost: {target.DisplayName}")
                        continue;   // the removal landed on a tinker, so this trial says nothing either way

                    if (weapon.GetProperty(target.Record) != null)
                        seen = true;
                }

                Assert.IsTrue(seen,
                    $"{target.DisplayName} was never lost and regained in 5000 swaps. That outcome is DELIBERATE - a special's magnitude is rerolled, so the same special back is a fresh roll rather than a no-op - and the tinker exclusion must not have been extended to specials.");
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_swap_special_chance", prior);
            }
        }

        /// <summary>Nine layer 1 tinkers plus exactly one named special: a full, managed, swappable budget.</summary>
        private static WorldObject MakeOneSpecialWeapon(WeaponClass weaponClass, WeaponModDefinition definition)
        {
            var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
            var tinkers = WeaponModTestKit.HandTinkeredComposition(weaponClass).Take(WeaponModRegistry.TotalSlots - 1).ToList();

            WeaponModTinkerSet.ApplyTinkers(weapon, tinkers);
            WeaponModTinkerSet.ApplySpecial(weapon, definition, WeaponModValue.Roll(definition, 10.0));
            WeaponModTinkerSet.WriteComposition(weapon, new List<MaterialType>(), tinkers);

            return weapon;
        }

        // ================= CHANGE 2 - a MaxRoll of 3 rolls 1, 2, or 3, and never escapes that band =================

        /// <summary>
        /// THE DEFECT this section originates from: Resolute's MaxRoll was 30, and a live roll produced +21
        /// critical damage resistance rating. VERIFIED against ace_world across every weenie in the game:
        /// GearCritDamageResist 375 appears on ZERO items, as do GearDamageResist 371, GearCrit 372,
        /// GearCritResist 373, GearCritDamage 374 and GearHealingBoost 376. Only GearDamage 370 (one item,
        /// value 1) and GearMaxHealth 379 (13 items, max 5) appear at all, so a +21 grant sat on a scale
        /// nothing else in the game touches.
        ///
        /// This pins the constant AND the shape the formula turns it into, because the constant alone does not
        /// say what a player actually receives.
        ///
        /// REPOINTED onto Weak Point (GearCrit 372) when Resolute was removed with the pool cut, then RETUNED
        /// AGAIN alongside it: Weak Point's MaxRoll moved 17 -> 2 -> 3 (2026-07-30, third then fourth pass).
        /// The MaxRoll value itself is incidental to this test - what it guards is that whatever the current
        /// MaxRoll is, the applied magnitude never escapes 1..MaxRoll.
        /// </summary>
        [TestMethod]
        public void Change2_WeakPointMaxRollIsThree()
        {
            var weakPoint = WeaponModRegistry.Get(WeaponModId.WeakPoint);

            Assert.AreEqual(3.0, weakPoint.MaxRoll, 1e-12,
                "Weak Point's MaxRoll was retuned from 17 to 2 (third pass) and then from 2 to 3 (fourth pass) on 2026-07-30, on the same rating-scarcity evidence that took Resolute from 30 to 2 before Resolute was removed");

            Assert.IsNotNull(weakPoint.NativeInt);
            Assert.AreEqual(PropertyInt.GearCrit, weakPoint.NativeInt.Value,
                "the retune must not have moved which property Weak Point feeds");

            Assert.IsFalse(weakPoint.Binary, "Weak Point rolls a magnitude; a binary modifier would grant MaxRoll unconditionally");
            Assert.AreEqual(WeaponModDefinition.DefaultMinPotency, weakPoint.MinPotency, 1e-12);
        }

        /// <summary>
        /// The full potency sweep at workmanship 1, 5 and 10. The numbers this asserts, computed from
        /// applied = MaxRoll x potency x (workmanship / 10), rounded away from zero, floored at 1 above zero,
        /// at MaxRoll 3. RECOMPUTED 2026-08-06 over the NEW potency band [0.60, 1] (DefaultMinPotency moved
        /// from 0.25 with the v3 magnitude pass):
        ///
        ///   workmanship 1  - raw 0.18 .. 0.30, always rounds to 0 and is floored to 1. ALWAYS 1.
        ///   workmanship 5  - raw 0.90 .. 1.50; 2 only at raw exactly 1.5, i.e. potency exactly 1.0. {1, 2}
        ///   workmanship 10 - raw 1.80 .. 3.00; the MINIMUM raw is 1.8, already past the 1.5 rounding
        ///                    threshold, so 1 is NOT reachable at workmanship 10 any more. {2, 3}
        ///
        /// So the band is exactly 1..3, and the top value is reachable well below the top of the potency band
        /// once workmanship is high enough - unlike the old MaxRoll-2 shape, 2 is no longer a top-workmanship-only
        /// outcome at workmanship 5.
        /// </summary>
        [TestMethod]
        public void Change2_WeakPointPotencySweepStaysWithinOneToThree()
        {
            var weakPoint = WeaponModRegistry.Get(WeaponModId.WeakPoint);

            foreach (var workmanship in new[] { 1.0, 5.0, 10.0 })
            {
                var seen = new HashSet<double>();

                for (var step = 0; step <= 1000; step++)
                {
                    var potency = weakPoint.MinPotency + (1.0 - weakPoint.MinPotency) * step / 1000.0;
                    var applied = WeaponModValue.Resolve(weakPoint, potency, workmanship, 1.0);

                    Assert.IsTrue(applied >= 1.0 && applied <= 3.0,
                        $"workmanship {workmanship}, potency {potency}: Weak Point resolved to {applied}, outside the intended 1..3");

                    Assert.AreEqual(applied, Math.Round(applied), 1e-12, "an integer native must resolve to a whole number");

                    seen.Add(applied);
                }

                double[] expected;

                if (workmanship == 1.0)
                    expected = new[] { 1.0 };
                else if (workmanship == 5.0)
                    expected = new[] { 1.0, 2.0 };
                else
                    expected = new[] { 2.0, 3.0 };

                CollectionAssert.AreEquivalent(expected, seen.ToArray(),
                    $"workmanship {workmanship}: Weak Point produced {{{string.Join(", ", seen.OrderBy(v => v))}}}, expected {{{string.Join(", ", expected)}}}");
            }
        }

        /// <summary>The whole workmanship range, not just the three reported points: never above 3, never below 1.</summary>
        [TestMethod]
        public void Change2_WeakPointStaysWithinOneAndThreeAcrossEveryWorkmanship()
        {
            var weakPoint = WeaponModRegistry.Get(WeaponModId.WeakPoint);

            for (var workmanship = 1; workmanship <= 10; workmanship++)
            {
                for (var step = 0; step <= 200; step++)
                {
                    var potency = weakPoint.MinPotency + (1.0 - weakPoint.MinPotency) * step / 200.0;
                    var applied = WeaponModValue.Resolve(weakPoint, potency, workmanship, 1.0);

                    Assert.IsTrue(applied >= 1.0 && applied <= 3.0,
                        $"workmanship {workmanship}, potency {potency}: {applied}");
                }
            }

            // and through the live roll path, which is where MinPotency and the scale tunable actually come from
            var values = new HashSet<double>();

            for (var roll = 0; roll < 5000; roll++)
                values.Add(WeaponModValue.Roll(weakPoint, 10.0));

            CollectionAssert.AreEquivalent(new[] { 2.0, 3.0 }, values.ToArray(),
                $"Weak Point's live rolls at workmanship 10 produced {string.Join(", ", values.OrderBy(v => v))}; the only intended outcomes are 2 and 3 after the 2026-08-06 MinPotency retune (1 is no longer reachable)");
        }

        /// <summary>
        /// End to end on a real weapon: whatever the reroll and swap flows do, the native GearCrit they leave
        /// behind is never above 3. This is the assertion that would have caught the live +21 on Resolute,
        /// applied to the surviving row that inherited Resolute's MaxRoll.
        /// </summary>
        [TestMethod]
        public void Change2_NoFlowEverLeavesGearCritAboveThree()
        {
            var weakPoint = WeaponModRegistry.Get(WeaponModId.WeakPoint);
            var held = 0;

            foreach (var weaponClass in Classes)
            {
                var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
                var probe = new WeaponModProbe(weapon);

                for (var use = 0; use < 200; use++)
                {
                    var context = $"{weaponClass} use {use}";

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{context}: reroll produced no result");

                    probe.AssertInvariants(weapon, weaponClass, context);

                    AssertWeakPointBounded(weapon, weakPoint, context, ref held);

                    WeaponModTestKit.Swap(weapon, weaponClass, 10.0, context);

                    probe.AssertInvariants(weapon, weaponClass, context);

                    AssertWeakPointBounded(weapon, weakPoint, context, ref held);
                }
            }

            Assert.IsTrue(held > 0, "Weak Point never rolled at all across 600 rerolls and swaps, so nothing was actually bounded");
        }

        private static void AssertWeakPointBounded(WorldObject weapon, WeaponModDefinition weakPoint, string context, ref int held)
        {
            var record = weapon.GetProperty(weakPoint.Record);

            if (record == null)
                return;

            held++;

            Assert.IsTrue(record.Value >= 1.0 && record.Value <= 3.0,
                $"{context}: Weak Point recorded a magnitude of {record.Value}; the retune bounds it to 1..3");

            var native = weapon.GetProperty(PropertyInt.GearCrit) ?? 0;

            Assert.IsTrue(native >= 1 && native <= 3,
                $"{context}: GearCrit reads {native}. Live play reported a +21 on the sibling rating, and ZERO ace_world weenies carry GearCrit at all.");
        }
    }
}
