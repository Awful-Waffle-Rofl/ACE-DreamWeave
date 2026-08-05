using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The THIRD round of 2026-07-30 live-play changes: the Tier A pool cut, the two renames, and the three
    /// magnitudes that came down with them. Kept in its own file, like the two rounds before it, so each round
    /// of repo-owner direction can be reviewed on its own evidence.
    ///
    ///   CHANGE 5 - RENAMES. "Crushing Blow" and "Biting Strike" are the RETAIL names for
    ///              PropertyFloat.CriticalMultiplier 136 and CriticalFrequency 147 (both carry those names as
    ///              doc comments on WorldObject_Weapon), so players would read the modifiers as the retail
    ///              mechanics they deliberately avoid. Renamed to Devastation and Weak Point. The reserved
    ///              PropertyFloat ids 8130 and 8131 did NOT move - only the names did.
    ///   CHANGE 6 - REMOVALS. Warding, Crit Ward, Resolute, Vigor and Mending are gone. The pool is
    ///              damage-oriented only. Their PropertyFloat ids 8136-8140 are RETIRED and must never be
    ///              reused, because weapons on dev shards carry records at them right now.
    ///   CHANGE 7 - MAGNITUDES. Devastation 50 -> 5, Weak Point 17 -> 2, Bloodthirst 25 -> 5. The resulting
    ///              distributions are pinned in WeaponModRetuneRoundTwoTests, which was repointed onto these
    ///              three rows when its own subjects were removed.
    ///
    /// The resulting pool is six entries at melee 5, missile 5, caster 3.
    /// </summary>
    [TestClass]
    public class WeaponModPoolCutTests
    {
        private static readonly WeaponClass[] Classes = { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster };

        /// <summary>
        /// The five PropertyFloat ids the removed modifiers held, with the name each carried. Read as raw ints
        /// on purpose: the enum members are gone, so there is nothing left to name them by.
        /// </summary>
        private static readonly (int Id, string WasCalled)[] RetiredRecordIds =
        {
            (8136, "WeaponModWarding"),
            (8137, "WeaponModCritWard"),
            (8138, "WeaponModResolute"),
            (8139, "WeaponModVigor"),
            (8140, "WeaponModMending"),
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ================= CHANGE 5 - the six surviving names =================

        /// <summary>
        /// THE SIX NAMES, PINNED. A rename is a player-facing change - it reaches the appraisal panel and the
        /// craft chat lines - so it must be deliberate rather than a side effect of an editor refactor. This is
        /// the test a future rename has to walk past on purpose.
        ///
        /// Devastation and Weak Point are the two that moved, and the reason they moved is asserted alongside
        /// them: the names they replaced belong to the retail crit properties this system routes AROUND.
        /// </summary>
        [TestMethod]
        public void Change5_TheSixSurvivingModifiersCarryExactlyTheseNames()
        {
            var expected = new Dictionary<WeaponModId, string>
            {
                { WeaponModId.Devastation,  "Devastation" },
                { WeaponModId.WeakPoint,    "Weak Point" },
                { WeaponModId.Bloodthirst,  "Bloodthirst" },
                { WeaponModId.Cleave,       "Cleave" },
                { WeaponModId.ShieldBypass, "Shield Bypass" },
                { WeaponModId.SwiftFlight,  "Swift Flight" },
            };

            Assert.AreEqual(expected.Count, WeaponModRegistry.TierAMods.Count,
                "the TIER A half of the registry no longer holds exactly the six modifiers this test names. Tier B rows are counted separately on purpose - this file is the record of the Tier A pool cut and its scope is Tier A");

            foreach (var kvp in expected)
            {
                Assert.IsTrue(WeaponModRegistry.TryGet(kvp.Key, out var definition), $"{kvp.Key}: missing registry row");

                Assert.AreEqual(kvp.Value, definition.DisplayName,
                    $"{kvp.Key}'s DisplayName is '{definition.DisplayName}'. Renaming a modifier changes what every player reads on the appraisal panel and in the craft lines, so it has to be a decision rather than a refactor artifact.");
            }

            // the two retail names must not come back: they belong to CriticalMultiplier 136 and
            // CriticalFrequency 147, which this system deliberately never writes
            foreach (var mod in WeaponModRegistry.AllMods)
            {
                Assert.AreNotEqual("Crushing Blow", mod.DisplayName,
                    $"{mod.Id}: 'Crushing Blow' is the retail name for PropertyFloat.CriticalMultiplier 136 and collides with retail imbue vocabulary");

                Assert.AreNotEqual("Biting Strike", mod.DisplayName,
                    $"{mod.Id}: 'Biting Strike' is the retail name for PropertyFloat.CriticalFrequency 147 and collides with retail imbue vocabulary");
            }
        }

        /// <summary>The renames moved names only: the two reserved record ids are exactly where they were.</summary>
        [TestMethod]
        public void Change5_TheRenamesDidNotMoveTheReservedPropertyIds()
        {
            Assert.AreEqual(8130, (int)PropertyFloat.WeaponModDevastation, "8130 was WeaponModCrushingBlow and must keep its id");
            Assert.AreEqual(8131, (int)PropertyFloat.WeaponModWeakPoint, "8131 was WeaponModBitingStrike and must keep its id");

            Assert.AreEqual(PropertyFloat.WeaponModDevastation, WeaponModRegistry.Get(WeaponModId.Devastation).Record);
            Assert.AreEqual(PropertyFloat.WeaponModWeakPoint, WeaponModRegistry.Get(WeaponModId.WeakPoint).Record);

            // the ids still resolve back to their definitions by record, which is the lookup reversal uses
            Assert.IsTrue(WeaponModRegistry.TryGet((PropertyFloat)8130, out var devastation));
            Assert.AreEqual(WeaponModId.Devastation, devastation.Id);

            Assert.IsTrue(WeaponModRegistry.TryGet((PropertyFloat)8131, out var weakPoint));
            Assert.AreEqual(WeaponModId.WeakPoint, weakPoint.Id);
        }

        // ================= CHANGE 6 - the five removals =================

        /// <summary>
        /// No removed modifier is reachable from ANY pool, from the id lookup, or from the record lookup.
        ///
        /// The record lookup is the one that matters most: it is how reversal finds a definition for a
        /// PropertyFloat sitting on a weapon, so a removed id resolving to anything at all would mean the
        /// removal had not actually happened.
        ///
        /// The companion rule - that the five ids stay UNALLOCATED so no future property inherits a dev-shard
        /// weapon's stale record - is guarded beside the same rule for PropertyFloat 9003, in
        /// WaveChallengePropertyTests.PropertyFloat_8136_To_8140_StayUnallocated.
        /// </summary>
        [TestMethod]
        public void Change6_NoRemovedModifierIsReachableFromAnyPoolOrLookup()
        {
            foreach (var (id, wasCalled) in RetiredRecordIds)
            {
                Assert.IsFalse(WeaponModRegistry.TryGet((PropertyFloat)id, out _),
                    $"PropertyFloat {id} ({wasCalled}) still resolves to a registry row; it was removed on 2026-07-30 and the id is retired");

                Assert.IsFalse(WeaponModRegistry.AllMods.Any(m => (int)m.Record == id),
                    $"a registry row still holds the retired record id {id} ({wasCalled})");

                foreach (var weaponClass in Classes)
                {
                    Assert.IsFalse(WeaponModRegistry.Pool(weaponClass).Any(m => (int)m.Record == id),
                        $"the {weaponClass} pool can still roll the retired record id {id} ({wasCalled})");
                }
            }

            // and nothing is reachable by an id outside the six, either - the removed WeaponModId values were
            // 7 through 11
            for (var raw = 7; raw <= 11; raw++)
            {
                Assert.IsFalse(WeaponModRegistry.TryGet((WeaponModId)raw, out _),
                    $"WeaponModId {raw} still resolves to a registry row; ids 7-11 were Warding, Crit Ward, Resolute, Vigor and Mending and are retired");
            }
        }

        /// <summary>
        /// The live record band shrank with the pool and the retired ids sit outside it, so
        /// WeaponModRegistry.PropertyBandEnd cannot be read as "this system still owns 8140".
        /// </summary>
        [TestMethod]
        public void Change6_TheLiveRecordBandStopsAtTheLastSurvivingModifier()
        {
            Assert.AreEqual(8130, WeaponModRegistry.PropertyBandStart);
            Assert.AreEqual(8135, WeaponModRegistry.PropertyBandEnd, "the live band came down from 8140 with the pool cut");

            Assert.AreEqual(8136, WeaponModRegistry.RetiredPropertyBandStart);
            Assert.AreEqual(8140, WeaponModRegistry.RetiredPropertyBandEnd);

            Assert.AreEqual(WeaponModRegistry.PropertyBandEnd + 1, WeaponModRegistry.RetiredPropertyBandStart,
                "the live band and the retired band must be adjacent with no gap, or an id belongs to neither");

            Assert.AreEqual(WeaponModRegistry.RetiredPropertyBandEnd + 1, WeaponModRegistry.TierBPropertyBandStart,
                "the Tier B band must start immediately after the retired band, or an id belongs to neither");

            foreach (var mod in WeaponModRegistry.TierAMods)
            {
                var record = (int)mod.Record;

                Assert.IsTrue(record >= WeaponModRegistry.PropertyBandStart && record <= WeaponModRegistry.PropertyBandEnd,
                    $"{mod.Id}: record {record} is outside the live Tier A band {WeaponModRegistry.PropertyBandStart}-{WeaponModRegistry.PropertyBandEnd}");
            }

            // and no Tier B row may reach back into the Tier A band or the retired one - the retired ids are
            // still carried by weapons on dev shards, so a reused id would resolve a stale record to a live row
            foreach (var mod in WeaponModRegistry.TierBMods)
            {
                var record = (int)mod.Record;

                Assert.IsTrue(record >= WeaponModRegistry.TierBPropertyBandStart && record <= WeaponModRegistry.TierBPropertyBandEnd,
                    $"{mod.Id}: record {record} is outside the live Tier B band {WeaponModRegistry.TierBPropertyBandStart}-{WeaponModRegistry.TierBPropertyBandEnd}");
            }
        }

        /// <summary>
        /// KNOWN CONSEQUENCE, PINNED SO IT CANNOT BE FORGOTTEN. ClearSpecials and the whole reversal path walk
        /// WeaponModRegistry.AllMods, so a record at a REMOVED id is no longer iterated: a weapon already
        /// carrying one keeps its native delta forever, with no way for this system to reverse it.
        ///
        /// That is ACCEPTABLE here only because the feature has never been enabled outside a disposable dev
        /// shard - "@weaponmodkit clean" plus fresh weapons resolves it. Shipping a modifier removal after the
        /// feature went live would need a data migration: sweep ace_shard for every biota carrying a record at
        /// the removed id and subtract the recorded magnitude back off its native property.
        ///
        /// This test asserts the consequence rather than a fix, deliberately. If a future pass makes stale
        /// records reversible it will fail, which is the correct moment to re-read this comment.
        /// </summary>
        [TestMethod]
        public void Change6_AStaleRecordAtARemovedIdIsNotReversedAndThatIsTheAcceptedCost()
        {
            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

            // the shape a dev-shard weapon is in right now: a stale magnitude record at Warding's retired id,
            // with the matching delta sitting on the native property
            const int wardingRecord = 8136;

            weapon.SetProperty((PropertyFloat)wardingRecord, 7.0);
            weapon.SetProperty(PropertyInt.GearDamageResist, 7);

            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), "the reroll itself must still succeed");

            Assert.AreEqual(7.0, weapon.GetProperty((PropertyFloat)wardingRecord).Value, 1e-12,
                "a record at a removed id is not iterated by ClearSpecials, so the reroll leaves it exactly where it was");

            Assert.AreEqual(7, weapon.GetProperty(PropertyInt.GearDamageResist),
                "and the native delta it granted stays live in combat - this is the accepted cost of removing a modifier, and it is why a post-launch removal would need a data migration");
        }

        // ================= CHANGE 6 - the resulting pool =================

        /// <summary>
        /// The exact membership of each class pool, not just its depth. Depth alone would pass if two modifiers
        /// swapped classes.
        /// </summary>
        [TestMethod]
        public void Change6_EachClassPoolHoldsExactlyTheseModifiers()
        {
            var expected = new Dictionary<WeaponClass, WeaponModId[]>
            {
                {
                    WeaponClass.Melee,
                    new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst, WeaponModId.Cleave, WeaponModId.ShieldBypass }
                },
                {
                    WeaponClass.Missile,
                    new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst, WeaponModId.ShieldBypass, WeaponModId.SwiftFlight }
                },
                {
                    WeaponClass.Caster,
                    new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst }
                },
            };

            // the pure gate-off overload, because this file's subject is the TIER A pool: its membership must
            // not move whether or not the Tier B gate is on, and reading the live accessor would make that
            // depend on a tunable
            foreach (var kvp in expected)
            {
                CollectionAssert.AreEquivalent(kvp.Value, WeaponModRegistry.Pool(kvp.Key, false).Select(m => m.Id).ToArray(),
                    $"the {kvp.Key} special pool membership changed");

                foreach (var id in kvp.Value)
                {
                    Assert.IsTrue(WeaponModRegistry.Pool(kvp.Key, true).Any(m => m.Id == id),
                        $"{id} left the {kvp.Key} pool when the Tier B gate is on - the gate ADDS rows, it must never remove one");
                }
            }

            Assert.AreEqual(5, WeaponModRegistry.Pool(WeaponClass.Melee, false).Count, "melee special pool");
            Assert.AreEqual(5, WeaponModRegistry.Pool(WeaponClass.Missile, false).Count, "missile special pool");
            Assert.AreEqual(3, WeaponModRegistry.Pool(WeaponClass.Caster, false).Count, "caster special pool");

            // the TINKER pools were not part of this change and must not have moved with it
            Assert.AreEqual(4, WeaponTinkerTable.Pool(WeaponClass.Melee).Count, "melee tinker pool");
            Assert.AreEqual(3, WeaponTinkerTable.Pool(WeaponClass.Missile).Count, "missile tinker pool");
            Assert.AreEqual(4, WeaponTinkerTable.Pool(WeaponClass.Caster).Count, "caster tinker pool");
        }

        /// <summary>
        /// THE CASTER POOL NOW EQUALS THE PER-WEAPON SPECIAL CAP EXACTLY, and this test exists to say that is
        /// INTENDED rather than to guard against it.
        ///
        /// A caster that rolls three specials necessarily holds all three, so casters keep a magnitude lottery
        /// and have no identity lottery left. That is a real consequence of cutting the pool to six
        /// damage-oriented rows, it was raised with the repo owner separately, and NOTHING in the code works
        /// around it: there is no guard, and MaxSpecials is unchanged at 3. The distinct-draw path still has to
        /// behave - it must return all three without repeating and without running short - which is the part
        /// that is actually verified here.
        ///
        /// WHAT THE SINGLE GATE DID TO THIS CLAIM (2026-07-30). Under the two-gate arrangement
        /// "weapon_mods_enabled true, weapon_mod_tier_b_enabled false" was a REACHABLE shard state, so a real
        /// caster could roll against a 3-deep pool. With one gate that combination no longer exists: a caster
        /// that can roll anything at all is rolling against 9. The arithmetic below is still worth pinning -
        /// the distinct-draw path must not run short on a pool exactly as deep as the cap - but it now
        /// describes the Tier A half in isolation rather than a state a player can reach.
        /// </summary>
        [TestMethod]
        public void Change6_ACasterAtTheSpecialCapNecessarilyHoldsTheWholePool()
        {
            // the claim is about the TIER A caster pool specifically - with the gate on the caster pool is 9
            // deep and the identity lottery comes back - so the gate state is a stated precondition here rather
            // than an accident of the shipped default
            Assert.IsFalse(WeaponModRegistry.Enabled(),
                "precondition: this claim holds while weapon_mods_enabled is FALSE, which is its shipped default");

            var pool = WeaponModRegistry.Pool(WeaponClass.Caster);

            Assert.AreEqual(WeaponModRegistry.MaxSpecials, pool.Count,
                "the caster pool is expected to equal the 3-special cap after the pool cut; if this changed, the 'no identity lottery for casters' note in the brief needs revisiting");

            Assert.AreEqual(3, WeaponModRegistry.MaxSpecials, "the per-weapon cap is unchanged - the pool cut did not move it");

            for (var draw = 0; draw < 500; draw++)
            {
                var rolled = WeaponModRoller.RollDistinctSpecials(WeaponClass.Caster, WeaponModRegistry.MaxSpecials);

                Assert.AreEqual(WeaponModRegistry.MaxSpecials, rolled.Count,
                    "a caster draw at the cap must return three specials - the pool is exactly three deep, so it must not run short");

                CollectionAssert.AreEquivalent(pool.Select(m => m.Id).ToArray(), rolled.Select(m => m.Id).ToArray(),
                    "a caster at the cap holds the whole pool, every time");
            }
        }

        // ================= CHANGE 7 - the three retuned magnitudes =================

        /// <summary>
        /// The full sweep the retune has to survive, at the ROLL path rather than at Resolve: no rolled
        /// magnitude ever exceeds its MaxRoll, and none of them ever rounds to zero, across every workmanship
        /// crossed with the whole potency band.
        ///
        /// WeaponModRetuneRoundTwoTests sweeps Resolve directly over the same three rows; this one goes through
        /// WeaponModValue.Roll, so it also covers the MinPotency draw and the live magnitude-scale tunable that
        /// Resolve takes as an argument.
        /// </summary>
        [TestMethod]
        public void Change7_NoRetunedMagnitudeEverExceedsItsMaxRollOrRoundsToZero()
        {
            var retuned = new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst };

            foreach (var id in retuned)
            {
                var definition = WeaponModRegistry.Get(id);

                Assert.IsTrue(definition.IsInteger, $"{id}: precondition - these three feed integer natives");

                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    for (var roll = 0; roll < 400; roll++)
                    {
                        var magnitude = WeaponModValue.Roll(definition, workmanship);

                        Assert.IsTrue(magnitude <= definition.MaxRoll,
                            $"{id}: workmanship {workmanship} rolled {magnitude}, above its MaxRoll of {definition.MaxRoll}");

                        Assert.IsTrue(magnitude >= 1.0,
                            $"{id}: workmanship {workmanship} rolled {magnitude} - a rolled special must never be a no-op");

                        Assert.AreEqual(magnitude, Math.Round(magnitude), 1e-12,
                            $"{id}: workmanship {workmanship} rolled {magnitude}, which is not a whole number on an integer native");

                        Assert.IsTrue(WeaponModValue.IsLiveMagnitude(definition, magnitude),
                            $"{id}: workmanship {workmanship} rolled {magnitude}, which the live-magnitude test would drop");
                    }
                }
            }
        }

        /// <summary>
        /// The retuned MaxRolls, pinned as constants against the numbers they replaced, so a revert reads as a
        /// deliberate act rather than as a merge artifact.
        /// </summary>
        [TestMethod]
        public void Change7_TheThreeRetunedMaxRollsArePinnedAgainstWhatTheyReplaced()
        {
            Assert.AreEqual(6.0, WeaponModRegistry.Get(WeaponModId.Devastation).MaxRoll, 1e-12, "Devastation was 50, then 5; now 6");
            Assert.AreEqual(3.0, WeaponModRegistry.Get(WeaponModId.WeakPoint).MaxRoll, 1e-12, "Weak Point was 17, then 2; now 3");
            Assert.AreEqual(6.0, WeaponModRegistry.Get(WeaponModId.Bloodthirst).MaxRoll, 1e-12, "Bloodthirst was 25, then 5; now 6");

            // the three rows the retune deliberately did NOT touch
            Assert.AreEqual(1.0, WeaponModRegistry.Get(WeaponModId.Cleave).MaxRoll, 1e-12, "Cleave is binary and stays +1");
            Assert.AreEqual(0.50, WeaponModRegistry.Get(WeaponModId.ShieldBypass).MaxRoll, 1e-12);
            Assert.AreEqual(6.0, WeaponModRegistry.Get(WeaponModId.SwiftFlight).MaxRoll, 1e-12);
        }

        /// <summary>
        /// End to end: 600 rerolls and swaps across all three classes never leave one of the retuned natives
        /// above its new maximum, and the test fails if a modifier never rolled at all rather than passing
        /// vacuously.
        /// </summary>
        [TestMethod]
        public void Change7_NoFlowEverLeavesARetunedNativeAboveItsNewMaximum()
        {
            var bounds = new (WeaponModId Id, int MaxRoll, PropertyInt Native)[]
            {
                (WeaponModId.Devastation, 6, PropertyInt.GearCritDamage),
                (WeaponModId.WeakPoint,   3, PropertyInt.GearCrit),
                (WeaponModId.Bloodthirst, 6, PropertyInt.GearDamage),
            };

            var held = bounds.ToDictionary(b => b.Id, b => 0);

            foreach (var weaponClass in Classes)
            {
                var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
                var probe = new WeaponModProbe(weapon);

                for (var use = 0; use < 100; use++)
                {
                    var context = $"{weaponClass} use {use}";

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{context}: reroll produced no result");

                    probe.AssertInvariants(weapon, weaponClass, context);
                    AssertBounded(weapon, bounds, held, context);

                    WeaponModTestKit.Swap(weapon, weaponClass, 10.0, context);

                    probe.AssertInvariants(weapon, weaponClass, context);
                    AssertBounded(weapon, bounds, held, context);
                }
            }

            foreach (var (id, _, _) in bounds)
            {
                Assert.IsTrue(held[id] > 0,
                    $"{id} never rolled at all across 600 rerolls and swaps, so nothing was actually bounded");
            }
        }

        private static void AssertBounded(ACE.Server.WorldObjects.WorldObject weapon,
            (WeaponModId Id, int MaxRoll, PropertyInt Native)[] bounds, Dictionary<WeaponModId, int> held, string context)
        {
            foreach (var (id, maxRoll, native) in bounds)
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
                    $"{context}: {native} reads {applied}, outside 1..{maxRoll}");
            }
        }
    }
}
