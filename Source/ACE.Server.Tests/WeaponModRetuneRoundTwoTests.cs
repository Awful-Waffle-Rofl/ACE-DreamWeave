using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.Structure;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The SECOND round of 2026-07-30 live-play changes, kept apart from WeaponModRetuneTests.cs so each round
    /// can be reviewed on its own evidence.
    ///
    ///   CHANGE 3 - three magnitudes retuned onto the scale the shipped game actually uses, on the rating
    ///              scarcity VERIFIED against ace_world across every weenie in the game: of the eight Gear*
    ///              ratings this system ever fed, only GearDamage 370 (one weenie, value 1) and GearMaxHealth
    ///              379 (13 weenies, max 5) appear at all, and the other six appear on ZERO.
    ///   CHANGE 4 - the appraisal "Property Details:" block moved from PropertyString.LongDesc to
    ///              PropertyString.Use, because a live @appraiseprobe run showed Use renders HIGHEST of the
    ///              three writable free-text slots while LongDesc renders at the bottom, under the spell list.
    ///
    /// CHANGE 3's SUBJECTS MOVED, 2026-07-30 third pass. This pass originally retuned Crit Ward 30 -> 2,
    /// Mending 30 -> 2 and Vigor 100 -> 5. All three were then REMOVED outright when the Tier A pool was cut to
    /// damage-oriented modifiers, so these tests could not keep naming them. They were REPOINTED, not deleted,
    /// onto the three rows the round-three retune moved on the SAME evidence - Devastation 50 -> 5,
    /// Bloodthirst 25 -> 5 and Weak Point 17 -> 2. Because a MaxRoll of 5 and a MaxRoll of 2 produce exactly
    /// the distributions Vigor and Crit Ward produced, every expected outcome set below is unchanged. That the
    /// five removed rows are gone and unreachable is asserted in WeaponModPoolCutTests.
    ///
    /// CHANGE 3's MAGNITUDES MOVED AGAIN, 2026-07-30 fourth pass: Devastation and Bloodthirst 5 -> 6, Weak
    /// Point 2 -> 3, on the same rating-scarcity evidence as the round-three retune. The Retuned table and the
    /// outcome sets below were updated in place rather than repointed, because the three rows themselves did
    /// not change - only how far each one can roll.
    /// </summary>
    [TestClass]
    public class WeaponModRetuneRoundTwoTests
    {
        private static readonly WeaponClass[] Classes = { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster };

        /// <summary>The three rating rows carrying a retuned magnitude, each with the maximum it was moved to.</summary>
        private static readonly (WeaponModId Id, double MaxRoll, PropertyInt Native)[] Retuned =
        {
            (WeaponModId.Devastation, 6.0, PropertyInt.GearCritDamage),
            (WeaponModId.Bloodthirst, 6.0, PropertyInt.GearDamage),
            (WeaponModId.WeakPoint,   3.0, PropertyInt.GearCrit),
        };

        /// <summary>
        /// Every row this pass deliberately did NOT touch, at the magnitude it must still carry. This is a
        /// scope guard, not a magnitude opinion: a later pass that moves one of these has to move this table
        /// with it deliberately.
        /// </summary>
        private static readonly (WeaponModId Id, double MaxRoll)[] Untouched =
        {
            (WeaponModId.Cleave,       1.0),
            (WeaponModId.ShieldBypass, 0.50),
            (WeaponModId.SwiftFlight,  6.0),
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ============ CHANGE 3 - the three rating rows onto the shipped scale ============

        /// <summary>
        /// The constants themselves. Pinned separately from the resulting distribution below, because the
        /// constant alone does not say what a player actually receives and the distribution alone would still
        /// pass if a MaxRoll were reached by some other route.
        /// </summary>
        [TestMethod]
        public void Change3_RetunedMaxRollsArePinned()
        {
            foreach (var (id, maxRoll, native) in Retuned)
            {
                var definition = WeaponModRegistry.Get(id);

                Assert.AreEqual(maxRoll, definition.MaxRoll, 1e-12,
                    $"{id}: MaxRoll must be {maxRoll} after the 2026-07-30 retunes onto the scale ace_world actually uses");

                Assert.IsNotNull(definition.NativeInt, $"{id}: must still feed an integer native");
                Assert.AreEqual(native, definition.NativeInt.Value, $"{id}: the retune must not have moved which property it feeds");
                Assert.IsFalse(definition.Binary, $"{id}: rolls a magnitude; a binary modifier would grant MaxRoll unconditionally");
                Assert.AreEqual(WeaponModDefinition.DefaultMinPotency, definition.MinPotency, 1e-12, $"{id}: potency floor unchanged");
            }
        }

        /// <summary>
        /// The three rating rows are NOT all on one number, and that split is asserted on purpose.
        ///
        /// This test used to say "Vigor sits at the exact retail maximum and its siblings do not", pinning
        /// Vigor's 5 against Crit Ward's and Mending's 2. All three of those rows are gone, but the shape of the
        /// claim survives them: the round-three retune put Devastation and Bloodthirst at 5 and Weak Point at 2
        /// (now 6 and 3, fourth pass), and each number is a separate repo-owner directive rather than one rule
        /// applied three times. A later pass that "harmonizes" the three onto a single MaxRoll fails here and
        /// has to be deliberate about which directive it is discarding.
        /// </summary>
        [TestMethod]
        public void Change3_TheRatingMagnitudesAreNotHarmonizedOntoOneNumber()
        {
            Assert.AreEqual(6.0, WeaponModRegistry.Get(WeaponModId.Devastation).MaxRoll, 1e-12);
            Assert.AreEqual(6.0, WeaponModRegistry.Get(WeaponModId.Bloodthirst).MaxRoll, 1e-12);

            Assert.AreEqual(3.0, WeaponModRegistry.Get(WeaponModId.WeakPoint).MaxRoll, 1e-12,
                "Weak Point is 3, not 6: a point of critical CHANCE rating is not priced the same as a point of critical DAMAGE or damage rating, and the two directives are separate");

            Assert.AreNotEqual(WeaponModRegistry.Get(WeaponModId.WeakPoint).MaxRoll,
                WeaponModRegistry.Get(WeaponModId.Devastation).MaxRoll,
                "the three rating rows must not be collapsed onto one magnitude without a deliberate decision");
        }

        /// <summary>The scope guard: nothing else in the table moved.</summary>
        [TestMethod]
        public void Change3_EveryOtherMagnitudeIsUnchanged()
        {
            foreach (var (id, maxRoll) in Untouched)
            {
                Assert.AreEqual(maxRoll, WeaponModRegistry.Get(id).MaxRoll, 1e-12,
                    $"{id}: the retunes moved the three RATING rows only; the non-rating {id} must still be {maxRoll}");
            }

            Assert.AreEqual(Retuned.Length + Untouched.Length, WeaponModRegistry.TierAMods.Count,
                "a TIER A modifier was added or removed without this table being updated, so the scope guard above is no longer covering the whole tier. Tier B magnitudes are pinned in WeaponModTierBTests, which is where a Tier B addition belongs");
        }

        /// <summary>
        /// The full sweep the retune has to survive: every workmanship 1..10 against the whole potency band.
        /// Two properties, both of which the retune could have broken - the applied magnitude must never exceed
        /// the new MaxRoll (a smaller maximum must not be reachable from above through the rounding step), and
        /// it must never round away to nothing (a smaller maximum pushes the raw value toward zero, which is
        /// exactly what the floor-at-1 rule in WeaponModValue.Resolve exists to catch).
        /// </summary>
        [TestMethod]
        public void Change3_AppliedMagnitudeStaysInsideMaxRollAndNeverRoundsToZero()
        {
            foreach (var (id, maxRoll, _) in Retuned)
            {
                var definition = WeaponModRegistry.Get(id);

                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    for (var step = 0; step <= 500; step++)
                    {
                        var potency = definition.MinPotency + (1.0 - definition.MinPotency) * step / 500.0;
                        var applied = WeaponModValue.Resolve(definition, potency, workmanship, 1.0);

                        Assert.IsTrue(applied <= maxRoll,
                            $"{id}: workmanship {workmanship}, potency {potency} resolved to {applied}, above its MaxRoll of {maxRoll}");

                        Assert.IsTrue(applied >= 1.0,
                            $"{id}: workmanship {workmanship}, potency {potency} resolved to {applied} - a rolled special must never be a no-op");

                        Assert.AreEqual(applied, Math.Round(applied), 1e-12,
                            $"{id}: workmanship {workmanship}, potency {potency} resolved to {applied}, which is not a whole number on an integer native");
                    }
                }
            }
        }

        /// <summary>
        /// The three reported workmanship points, as exact outcome sets rather than bounds. Computed from
        /// applied = MaxRoll x potency x (workmanship / 10), rounded away from zero, floored at 1 above zero,
        /// over the potency band [0.25, 1]. RECOMPUTED 2026-07-30, fourth pass, after Weak Point moved 2 -> 3
        /// and Devastation/Bloodthirst moved 5 -> 6:
        ///
        ///   Weak Point (MaxRoll 3)
        ///     workmanship 1  - raw 0.075 .. 0.30, always rounds to 0 and is floored. {1}
        ///     workmanship 5  - raw 0.375 .. 1.50; 2 only at raw exactly 1.5, i.e. potency exactly 1.0. {1, 2}
        ///     workmanship 10 - raw 0.75 .. 3.00; 2 from raw 1.5 (potency 0.5) up, 3 from raw 2.5
        ///                      (potency 0.8333...) up. {1, 2, 3}
        ///
        ///   Devastation / Bloodthirst (MaxRoll 6)
        ///     workmanship 1  - raw 0.15 .. 0.60; 0.5 and above rounds away from zero to 1, everything below
        ///                      floors to 1. {1}
        ///     workmanship 5  - raw 0.75 .. 3.00. {1, 2, 3}
        ///     workmanship 10 - raw 1.50 .. 6.00; the MINIMUM raw is exactly 1.5 (at potency exactly 0.25,
        ///                      the MinPotency floor), which rounds AWAY FROM ZERO to 2, not 1 - so 1 is NOT
        ///                      reachable at workmanship 10 even though it is the normal outcome at every lower
        ///                      workmanship. {2, 3, 4, 5, 6}
        /// </summary>
        [TestMethod]
        public void Change3_ReportedWorkmanshipPointsGiveExactlyTheseMagnitudes()
        {
            AssertOutcomes(WeaponModId.WeakPoint, 1, new[] { 1.0 });
            AssertOutcomes(WeaponModId.WeakPoint, 5, new[] { 1.0, 2.0 });
            AssertOutcomes(WeaponModId.WeakPoint, 10, new[] { 1.0, 2.0, 3.0 });

            AssertOutcomes(WeaponModId.Devastation, 1, new[] { 1.0 });
            AssertOutcomes(WeaponModId.Devastation, 5, new[] { 1.0, 2.0, 3.0 });
            AssertOutcomes(WeaponModId.Devastation, 10, new[] { 2.0, 3.0, 4.0, 5.0, 6.0 });

            AssertOutcomes(WeaponModId.Bloodthirst, 1, new[] { 1.0 });
            AssertOutcomes(WeaponModId.Bloodthirst, 5, new[] { 1.0, 2.0, 3.0 });
            AssertOutcomes(WeaponModId.Bloodthirst, 10, new[] { 2.0, 3.0, 4.0, 5.0, 6.0 });
        }

        /// <summary>
        /// THE BOUNDARY EFFECT, called out explicitly rather than left implicit inside the set-equality check
        /// above: at MaxRoll 6, workmanship 10, the MINIMUM potency (the MinPotency floor of 0.25) produces a
        /// raw value of exactly 1.5, which Math.Round(..., AwayFromZero) sends to 2, not 1. A broader "stays
        /// within 1..6" range assertion would not catch a regression that let 1 back in - or one that pushed the
        /// floor to 3 - so this pins the exact minimum outcome by itself, for both retuned integer-native rows
        /// that share MaxRoll 6.
        /// </summary>
        [TestMethod]
        public void Change3_MaxRollSixNeverResolvesToOneAtWorkmanshipTen()
        {
            foreach (var id in new[] { WeaponModId.Devastation, WeaponModId.Bloodthirst })
            {
                var definition = WeaponModRegistry.Get(id);

                var atMinPotency = WeaponModValue.Resolve(definition, definition.MinPotency, 10.0, 1.0);

                Assert.AreEqual(2.0, atMinPotency, 1e-12,
                    $"{id}: workmanship 10 at the MinPotency floor ({definition.MinPotency}) must resolve to exactly 2 (raw 1.5 rounds away from zero), not 1");

                for (var roll = 0; roll < 2000; roll++)
                {
                    var rolled = WeaponModValue.Roll(definition, 10.0);

                    Assert.AreNotEqual(1.0, rolled, $"{id}: a live roll at workmanship 10 produced 1, which is unreachable at MaxRoll {definition.MaxRoll}");
                }
            }
        }

        private static void AssertOutcomes(WeaponModId id, int workmanship, double[] expected)
        {
            var definition = WeaponModRegistry.Get(id);
            var seen = new HashSet<double>();

            for (var step = 0; step <= 10000; step++)
            {
                var potency = definition.MinPotency + (1.0 - definition.MinPotency) * step / 10000.0;

                seen.Add(WeaponModValue.Resolve(definition, potency, workmanship, 1.0));
            }

            CollectionAssert.AreEquivalent(expected, seen.ToArray(),
                $"{id} at workmanship {workmanship} produced {{{string.Join(", ", seen.OrderBy(v => v))}}}, expected {{{string.Join(", ", expected)}}}");
        }

        /// <summary>
        /// End to end on real weapons through the real flows: whatever the reroll and swap do, the native
        /// property each retuned modifier feeds is never left above the new maximum, and never at 0 while its
        /// record exists. This is the assertion that would have caught the live +21 on Resolute, applied to the
        /// three rows that carried the same defect.
        /// </summary>
        [TestMethod]
        public void Change3_NoFlowEverLeavesARetunedNativeAboveItsMaxRoll()
        {
            var held = Retuned.ToDictionary(r => r.Id, r => 0);

            foreach (var weaponClass in Classes)
            {
                var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
                var probe = new WeaponModProbe(weapon);

                for (var use = 0; use < 200; use++)
                {
                    var context = $"{weaponClass} use {use}";

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{context}: reroll produced no result");

                    probe.AssertInvariants(weapon, weaponClass, context);
                    AssertRetunedBounded(weapon, context, held);

                    WeaponModTestKit.Swap(weapon, weaponClass, 10.0, context);

                    probe.AssertInvariants(weapon, weaponClass, context);
                    AssertRetunedBounded(weapon, context, held);
                }
            }

            foreach (var (id, _, _) in Retuned)
            {
                Assert.IsTrue(held[id] > 0,
                    $"{id} never rolled at all across 600 rerolls and swaps, so nothing was actually bounded");
            }
        }

        private static void AssertRetunedBounded(WorldObject weapon, string context, Dictionary<WeaponModId, int> held)
        {
            foreach (var (id, maxRoll, native) in Retuned)
            {
                var definition = WeaponModRegistry.Get(id);
                var record = weapon.GetProperty(definition.Record);

                if (record == null)
                    continue;

                held[id]++;

                Assert.IsTrue(record.Value >= 1.0 && record.Value <= maxRoll,
                    $"{context}: {id} recorded a magnitude of {record.Value}; the retune bounds it to 1..{maxRoll}");

                var applied = weapon.GetProperty(native) ?? 0;

                Assert.IsTrue(applied >= 1 && applied <= maxRoll,
                    $"{context}: {native} reads {applied}, outside 1..{maxRoll}. These ratings are near-absent from ace_world, so anything larger has no peer in the item set.");
            }
        }

        // ================= CHANGE 4 - the Property Details block moved to Use =================

        private const string Header = "Property Details:";

        /// <summary>
        /// The block lands in Use and LongDesc is not written at all.
        ///
        /// WHY Use: VERIFIED 2026-07-30 by a live @appraiseprobe run (the repo owner drove the client). The probe
        /// stamps a distinct two-line marker into ShortDesc, LongDesc and Use, and one examine showed Use drawn
        /// HIGHEST of the three with both of its lines rendered - so it is the slot nearest the client's own
        /// "Properties:" lines, and it preserves embedded newlines. LongDesc draws at the bottom of the panel,
        /// below the spell list, which is where this block used to land.
        /// </summary>
        [TestMethod]
        public void Change4_ThePropertyDetailsBlockIsWrittenToUseAndNotLongDesc()
        {
            var properties = new Dictionary<PropertyString, string>();

            AppraiseInfo.WritePropertyDetails(properties, new[] { "- Bloodthirst: +5 damage rating" });

            Assert.IsTrue(properties.TryGetValue(PropertyString.Use, out var use), "the block must be written to PropertyString.Use");
            Assert.IsTrue(use.Contains(Header), $"Use does not carry the block: \"{use}\"");
            Assert.IsTrue(use.Contains("- Bloodthirst: +5 damage rating"), $"Use dropped the detail line: \"{use}\"");

            Assert.IsFalse(properties.ContainsKey(PropertyString.LongDesc),
                "LongDesc must no longer be written at all - the client draws it below the spell list");

            // BOTH padding newlines REMOVED 2026-08-02 after seeing them rendered live: the client already
            // draws its own gap above the Use slot, so a leading "\n" doubled it, and a trailing "\n" drew a
            // stray empty line under the last bullet. The block is now exactly its own text.
            Assert.AreEqual(Header + "\n- Bloodthirst: +5 damage rating", use,
                "the block must carry no padding newlines of its own - the client supplies the gap above it");
        }

        /// <summary>
        /// A pre-existing Use string is NOT clobbered: it is kept and the block appended after it, separated by a
        /// blank line, mirroring how the LongDesc path handled an item that already had a description.
        /// </summary>
        [TestMethod]
        public void Change4_APreExistingUseStringSurvivesWithTheBlockAppended()
        {
            var properties = new Dictionary<PropertyString, string>
            {
                { PropertyString.Use, "Use this on a weapon to reroll its tinkers." },
            };

            AppraiseInfo.WritePropertyDetails(properties, new[] { "- Weak Point: +2 critical chance rating" });

            Assert.AreEqual("Use this on a weapon to reroll its tinkers.\n\n" + Header + "\n- Weak Point: +2 critical chance rating",
                properties[PropertyString.Use],
                "the item's own Use text must survive, with the block appended below it after a blank line and no trailing newline");
        }

        /// <summary>
        /// The ManaStone interaction, as far as a unit test can reach it. AppraiseInfo overwrites Use for
        /// `wo is ManaStone`, and the two writers are kept apart by ORDER - WritePropertyDetails is called after
        /// every type-specific branch, so a mana stone that somehow carried a mod line gets its own use message
        /// first and the block appended below, rather than the block being silently eaten.
        ///
        /// This asserts the append half, driven with the exact string the ManaStone branch writes. The ORDERING
        /// itself is not reachable from a unit test: it lives in AppraiseInfo.BuildProfile, whose only entry
        /// points need a live Player examiner (see Change4_TheBlockReachesUseThroughABuiltProfile for how far
        /// that gets).
        /// </summary>
        [TestMethod]
        public void Change4_AManaStoneUseMessageIsNotClobberedByTheBlock()
        {
            const string manaStoneUse = "Use on a magic item to give the stone's stored Mana to that item.";

            var properties = new Dictionary<PropertyString, string> { { PropertyString.Use, manaStoneUse } };

            AppraiseInfo.WritePropertyDetails(properties, new[] { "- Multi Shot: Fires 1 additional arrow in a 20 arc." });

            Assert.IsTrue(properties[PropertyString.Use].StartsWith(manaStoneUse, StringComparison.Ordinal),
                "the mana stone's own use message must come first and must survive intact");

            Assert.IsTrue(properties[PropertyString.Use].Contains(Header),
                "the block must still be present after it, not dropped");
        }

        /// <summary>A pre-existing LongDesc is left exactly as it was.</summary>
        [TestMethod]
        public void Change4_APreExistingLongDescIsLeftUntouched()
        {
            var properties = new Dictionary<PropertyString, string>
            {
                { PropertyString.LongDesc, "A plain sword, notched from use." },
            };

            AppraiseInfo.WritePropertyDetails(properties, new[] { "- Devastation: +5 critical damage rating" });

            Assert.AreEqual("A plain sword, notched from use.", properties[PropertyString.LongDesc],
                "the item's flavor text must be returned to the client unmodified now that the block no longer rides on it");

            Assert.IsTrue(properties[PropertyString.Use].Contains(Header), "the block still has to land somewhere");
        }

        /// <summary>An item with nothing to show is not given an empty block, and gets no Use row invented for it.</summary>
        [TestMethod]
        public void Change4_NoDetailLinesWritesNothingAtAll()
        {
            var properties = new Dictionary<PropertyString, string>
            {
                { PropertyString.LongDesc, "A plain sword." },
            };

            AppraiseInfo.WritePropertyDetails(properties, new string[0]);

            Assert.IsFalse(properties.ContainsKey(PropertyString.Use), "an item with no mod lines must not gain a Use string");
            Assert.AreEqual("A plain sword.", properties[PropertyString.LongDesc]);
            Assert.AreEqual(1, properties.Count);
        }

        /// <summary>
        /// The block reaching Use through a REAL AppraiseInfo build, not just through the helper - which is what
        /// proves the call site was moved too, and that nothing between it and the end of BuildProfile clears the
        /// slot again.
        ///
        /// Built with a null examiner. That is safe for this shape of item and only for this shape: every use of
        /// `examiner` in BuildProfile sits behind a property test no bare test weapon satisfies (ScribeAccount,
        /// HouseOwnerAccount, a locked Door or Chest). If a future edit makes the examiner unconditional, this
        /// test fails loudly rather than silently covering less, which is the intended failure mode.
        /// </summary>
        [TestMethod]
        public void Change4_TheBlockReachesUseThroughABuiltProfile()
        {
            var priorGate = PropertyManager.GetBool("weapon_mods_enabled").Item;

            PropertyManager.ModifyBool("weapon_mods_enabled", true);

            try
            {
                var bloodthirst = WeaponModRegistry.Get(WeaponModId.Bloodthirst);
                var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

                weapon.SetProperty(PropertyString.LongDesc, "A plain sword, notched from use.");

                WeaponModTinkerSet.ApplySpecial(weapon, bloodthirst, WeaponModValue.Resolve(bloodthirst, 1.0, 10.0, 1.0));

                var info = new AppraiseInfo(weapon, null);

                Assert.IsTrue(info.PropertiesString.TryGetValue(PropertyString.Use, out var use),
                    "a weapon carrying a rolled special must come back with the block in Use");

                Assert.IsTrue(use.Contains(Header), $"Use does not carry the block: \"{use}\"");
                Assert.IsTrue(use.Contains(bloodthirst.DisplayName), $"Use does not name the modifier: \"{use}\"");

                Assert.AreEqual("A plain sword, notched from use.", info.PropertiesString[PropertyString.LongDesc],
                    "LongDesc must reach the client exactly as the item stored it");
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", priorGate);
            }
        }
    }
}
