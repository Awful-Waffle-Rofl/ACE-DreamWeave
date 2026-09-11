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
    /// The resulting pool was six entries at melee 5, missile 5, caster 3. The 2026-08-06 v3 expansion (Heft,
    /// Tension, Leverage, Attunement, Focus, Execution, see WeaponModRegistry.TierBExpansionBandStart) added
    /// six rows to the catalog, but all six are Tier B, so the Tier A pool this file's scope is about was
    /// unchanged by it. The live gate-on depths (which DO include the v3 rows) are asserted in
    /// WeaponModTests.Registry_PoolDepthMatchesTheDesign instead.
    ///
    ///   CHANGE 8 - A SEVENTH REMOVAL, 2026-08-07. Cleave is gone, on the same terms and for the same reason
    ///              as Change 6's five: its WeaponModId 4 and its PropertyFloat 8133 are both RETIRED and must
    ///              never be reused, because weapons on dev shards carry a stale 8133 record AND the orphaned
    ///              PropertyInt.Cleaving native it was the bookkeeping for. Tier A is now five entries at
    ///              melee 4, missile 5, caster 3 - only the melee column moved, because Cleave was the one
    ///              Tier A row restricted to melee.
    ///   CHANGE 9 - AN EIGHTH REMOVAL, 2026-08-17 (catalog v4). Swift Flight is gone, on the same terms as
    ///              Changes 6 and 8: its WeaponModId 6 and its PropertyFloat 8135 are both RETIRED and must
    ///              never be reused. Tier A is now four entries at melee 4, missile 4, caster 3 - only the
    ///              missile column moved, because Swift Flight was the one Tier A row restricted to missile.
    ///
    /// Changes 8 and 9 are folded into this file rather than given their own because each is the same change
    /// made again. The tables below carry a per-row retirement date so the rounds stay separable in the
    /// evidence even though the rule is one rule.
    /// </summary>
    [TestClass]
    public class WeaponModPoolCutTests
    {
        private static readonly WeaponClass[] Classes = { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster };

        /// <summary>
        /// Every PropertyFloat id a removed modifier held, with the name it carried and the date it went. Read
        /// as raw ints on purpose: the enum members are gone, so there is nothing left to name them by.
        ///
        /// 8133 IS OUT OF NUMERIC ORDER WITH THE REST AND THAT IS THE POINT. Change 6's five sit in a
        /// contiguous retired band ABOVE the live one; Cleave's 8133 is a HOLE INSIDE the live Tier A band
        /// 8130-8135, because it was removed from the middle of a shipped range rather than off the end of
        /// one. Nothing writes it, so the invariant harness's band scan still balances - but it is the reason
        /// the live band's bounds cannot be read as "every id in here is allocated".
        /// </summary>
        private static readonly (int Id, string WasCalled, string RetiredOn)[] RetiredRecordIds =
        {
            (8136, "WeaponModWarding",     "2026-07-30"),
            (8137, "WeaponModCritWard",    "2026-07-30"),
            (8138, "WeaponModResolute",    "2026-07-30"),
            (8139, "WeaponModVigor",       "2026-07-30"),
            (8140, "WeaponModMending",     "2026-07-30"),
            (8133, "WeaponModCleave",      "2026-08-07"),
            (8135, "WeaponModSwiftFlight", "2026-08-17"),
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ================= CHANGE 5 - the six surviving names =================

        /// <summary>
        /// THE SURVIVING NAMES, PINNED. A rename is a player-facing change - it reaches the appraisal panel and
        /// the craft chat lines - so it must be deliberate rather than a side effect of an editor refactor.
        /// This is the test a future rename has to walk past on purpose.
        ///
        /// Devastation and Weak Point are the two that moved, and the reason they moved is asserted alongside
        /// them: the names they replaced belong to the retail crit properties this system routes AROUND.
        ///
        /// It was six names until 2026-08-07, when Cleave was removed (Change 8) and the table came down to
        /// five with it, then to four on 2026-08-17 when Swift Flight was removed (Change 9).
        /// </summary>
        [TestMethod]
        public void Change5_TheSurvivingModifiersCarryExactlyTheseNames()
        {
            var expected = new Dictionary<WeaponModId, string>
            {
                { WeaponModId.Devastation,  "Devastation" },
                { WeaponModId.WeakPoint,    "Weak Point" },
                { WeaponModId.Bloodthirst,  "Bloodthirst" },
                { WeaponModId.ShieldBypass, "Shield Bypass" },
            };

            // 2026-08-06: the v3 expansion (Heft, Tension, Leverage, Attunement, Focus, Execution) added six
            // more rows to the catalog, but ALL SIX ARE TIER B (see WeaponModRegistry.TierBExpansionBandStart),
            // so Tier A was untouched by it. The only things that have moved Tier A's count since the pool cut
            // are the 2026-08-07 Cleave removal and the 2026-08-17 Swift Flight removal, which took it from six
            // to the four named here.
            Assert.AreEqual(expected.Count, WeaponModRegistry.TierAMods.Count,
                "the TIER A half of the registry no longer holds exactly the modifiers this test names. Tier B rows are counted separately on purpose - this file is the record of the Tier A pool cut and its scope is Tier A");

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
        /// WaveChallengePropertyTests.PropertyFloat_8133_8135_8136To8140_8141To8143_8146_StayUnallocated.
        /// </summary>
        [TestMethod]
        public void Change6_NoRemovedModifierIsReachableFromAnyPoolOrLookup()
        {
            foreach (var (id, wasCalled, retiredOn) in RetiredRecordIds)
            {
                Assert.IsFalse(WeaponModRegistry.TryGet((PropertyFloat)id, out _),
                    $"PropertyFloat {id} ({wasCalled}) still resolves to a registry row; it was removed on {retiredOn} and the id is retired");

                Assert.IsFalse(WeaponModRegistry.AllMods.Any(m => (int)m.Record == id),
                    $"a registry row still holds the retired record id {id} ({wasCalled}, retired {retiredOn})");

                foreach (var weaponClass in Classes)
                {
                    Assert.IsFalse(WeaponModRegistry.Pool(weaponClass).Any(m => (int)m.Record == id),
                        $"the {weaponClass} pool can still roll the retired record id {id} ({wasCalled}, retired {retiredOn})");
                }
            }

            // and nothing is reachable by a retired WeaponModId either - 7 through 11 went on 2026-07-30, and
            // 4 (Cleave) on 2026-08-07, and 6 (Swift Flight) on 2026-08-17. 4 and 6 are checked separately
            // rather than folded into the range because they sit INSIDE the original Tier A run 1-6, so a loop
            // over a contiguous range cannot reach either.
            for (var raw = 7; raw <= 11; raw++)
            {
                Assert.IsFalse(WeaponModRegistry.TryGet((WeaponModId)raw, out _),
                    $"WeaponModId {raw} still resolves to a registry row; ids 7-11 were Warding, Crit Ward, Resolute, Vigor and Mending and are retired");
            }

            Assert.IsFalse(WeaponModRegistry.TryGet((WeaponModId)4, out _),
                "WeaponModId 4 still resolves to a registry row; it was Cleave, removed 2026-08-07, and the id is retired");

            Assert.IsFalse(WeaponModRegistry.TryGet((WeaponModId)6, out _),
                "WeaponModId 6 still resolves to a registry row; it was Swift Flight, removed 2026-08-17, and the id is retired");
        }

        /// <summary>
        /// The live record band shrank with the pool and the retired ids sit outside it, so
        /// WeaponModRegistry.PropertyBandEnd cannot be read as "this system still owns 8140".
        /// </summary>
        [TestMethod]
        public void Change6_TheLiveRecordBandStopsAtTheLastSurvivingModifier()
        {
            Assert.AreEqual(8130, WeaponModRegistry.PropertyBandStart);
            Assert.AreEqual(8134, WeaponModRegistry.PropertyBandEnd, "the live band came down from 8140 with the pool cut, then from 8135 to 8134 when Swift Flight retired");

            Assert.AreEqual(8135, WeaponModRegistry.RetiredPropertyBandStart);
            Assert.AreEqual(8140, WeaponModRegistry.RetiredPropertyBandEnd);

            Assert.AreEqual(WeaponModRegistry.PropertyBandEnd + 1, WeaponModRegistry.RetiredPropertyBandStart,
                "the live band and the retired band must be adjacent with no gap, or an id belongs to neither");

            Assert.AreEqual(WeaponModRegistry.RetiredPropertyBandEnd + 1, WeaponModRegistry.TierBPropertyBandStart,
                "the Tier B band must start immediately after the retired band, or an id belongs to neither");

            // WeaponModRegistry.TierAMods is exactly the surviving Tier A rows, no filter needed: the
            // 2026-08-06 v3 expansion is entirely Tier B, and the 2026-08-07 Cleave removal took a row OUT
            // rather than adding one outside the band.
            foreach (var mod in WeaponModRegistry.TierAMods)
            {
                var record = (int)mod.Record;

                Assert.IsTrue(record >= WeaponModRegistry.PropertyBandStart && record <= WeaponModRegistry.PropertyBandEnd,
                    $"{mod.Id}: record {record} is outside the live Tier A band {WeaponModRegistry.PropertyBandStart}-{WeaponModRegistry.PropertyBandEnd}");
            }

            // and no Tier B row may reach back into the Tier A band or the retired one - the retired ids are
            // still carried by weapons on dev shards, so a reused id would resolve a stale record to a live row.
            // A Tier B record is valid in the original v2 band, the 2026-08-06 v3 expansion band, OR the
            // 2026-08-17 v4 expansion band - see WeaponModRegistry.TierBExpansionBandStart and
            // WeaponModRegistry.TierBV4BandStart for why all three are disjoint.
            foreach (var mod in WeaponModRegistry.TierBMods)
            {
                var record = (int)mod.Record;

                var inOriginalBand = record >= WeaponModRegistry.TierBPropertyBandStart && record <= WeaponModRegistry.TierBPropertyBandEnd;
                var inExpansionBand = record >= WeaponModRegistry.TierBExpansionBandStart && record <= WeaponModRegistry.TierBExpansionBandEnd;
                var inV4Band = record >= WeaponModRegistry.TierBV4BandStart && record <= WeaponModRegistry.TierBV4BandEnd;

                Assert.IsTrue(inOriginalBand || inExpansionBand || inV4Band,
                    $"{mod.Id}: record {record} is outside all three reserved Tier B ranges {WeaponModRegistry.TierBPropertyBandStart}-{WeaponModRegistry.TierBPropertyBandEnd}, {WeaponModRegistry.TierBExpansionBandStart}-{WeaponModRegistry.TierBExpansionBandEnd} and {WeaponModRegistry.TierBV4BandStart}-{WeaponModRegistry.TierBV4BandEnd}");
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
        /// The exact membership of the POOL-CUT SURVIVORS within each class pool, not just their presence.
        /// This file's scope is the pool cut, so this asserts the survivors are all still there and their
        /// classes did not move. It was six survivors until Cleave went on 2026-08-07 (Change 8); the melee
        /// list came down with it and no other class was touched, because Cleave was melee only.
        /// The gate-off Tier A pool is UNCHANGED by the 2026-08-06 v3 expansion (all six
        /// of its rows are Tier B), so this subset check is now also an exact one, but it stays written as a
        /// subset check to keep this file's scope narrow to the pool cut rather than restating the current
        /// exact depths, which are asserted in WeaponModTests.Registry_PoolDepthMatchesTheDesign instead.
        /// </summary>
        [TestMethod]
        public void Change6_EachClassPoolStillHoldsExactlyTheseSurvivors()
        {
            var expected = new Dictionary<WeaponClass, WeaponModId[]>
            {
                {
                    WeaponClass.Melee,
                    // Cleave was the fourth melee entry until it was removed on 2026-08-07 (Change 8)
                    new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst, WeaponModId.ShieldBypass }
                },
                {
                    WeaponClass.Missile,
                    // Swift Flight was the fifth missile entry until it was removed on 2026-08-17 (Change 9)
                    new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst, WeaponModId.ShieldBypass }
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
                var pool = WeaponModRegistry.Pool(kvp.Key, false).Select(m => m.Id).ToArray();

                foreach (var id in kvp.Value)
                    Assert.IsTrue(pool.Contains(id), $"{id} is missing from the {kvp.Key} pool - a pool-cut survivor must stay present");

                foreach (var id in kvp.Value)
                {
                    Assert.IsTrue(WeaponModRegistry.Pool(kvp.Key, true).Any(m => m.Id == id),
                        $"{id} left the {kvp.Key} pool when the Tier B gate is on - the gate ADDS rows, it must never remove one");
                }
            }

            // the TINKER pools were not part of this change and must not have moved with it
            Assert.AreEqual(4, WeaponTinkerTable.Pool(WeaponClass.Melee).Count, "melee tinker pool");
            Assert.AreEqual(3, WeaponTinkerTable.Pool(WeaponClass.Missile).Count, "missile tinker pool");
            Assert.AreEqual(4, WeaponTinkerTable.Pool(WeaponClass.Caster).Count, "caster tinker pool");
        }

        /// <summary>
        /// THE TIER A CASTER POOL IS NO DEEPER THAN THE PER-WEAPON SPECIAL CAP, and this test exists to say that
        /// is INTENDED rather than to guard against it.
        ///
        /// A caster that rolls its maximum necessarily holds the whole pool, so casters keep a magnitude lottery
        /// and have no identity lottery left. That is a real consequence of cutting the pool to six
        /// damage-oriented rows, it was raised with the repo owner separately, and NOTHING in the code works
        /// around it: there is no guard. The distinct-draw path still has to behave - it must return all three
        /// without repeating and without running short - which is the part that is actually verified here.
        ///
        /// UPDATED 2026-08-06 (cap raise): the cap moved from 3 to 4 while the Tier A caster pool stayed at 3,
        /// making the relation "shallower than" rather than "equal to". A caster draw of 4 legitimately
        /// returns 3.
        ///
        /// THE SAME-DAY v3 EXPANSION DID NOT REOPEN THIS. Attunement was originally drafted as a Tier A caster
        /// row (which would have brought the Tier A caster pool to 4, equal to the cap again) but was reworked
        /// to Tier B the same day - see WeaponModRegistry.TierBExpansionBandStart - so the Tier A caster pool
        /// stays at 3 and this test's claim is unchanged.
        ///
        /// WHAT THE SINGLE GATE DID TO THIS CLAIM (2026-07-30). Under the two-gate arrangement
        /// "weapon_mods_enabled true, weapon_mod_tier_b_enabled false" was a REACHABLE shard state, so a real
        /// caster could roll against a shallow Tier A-only pool. With one gate that combination no longer
        /// exists: a caster that can roll anything at all is rolling against the gate-on depth (12). The
        /// arithmetic below is still worth pinning - the distinct-draw path must not run short on a pool
        /// exactly as deep as the cap - but it now describes the Tier A half in isolation rather than a state
        /// a player can reach.
        /// </summary>
        [TestMethod]
        public void Change6_ACasterAtTheSpecialCapNecessarilyHoldsTheWholePool()
        {
            // the claim is about the TIER A caster pool specifically - with the gate on the caster pool is 12
            // deep and the identity lottery comes back - so the gate state is a stated precondition here.
            // weapon_mods_enabled now ships TRUE as standard content, so the gate is forced off explicitly
            // rather than relied on as the shipped default.
            var priorGate = PropertyManager.GetBool("weapon_mods_enabled").Item;
            PropertyManager.ModifyBool("weapon_mods_enabled", false);
            try
            {
                Assert.IsFalse(WeaponModRegistry.Enabled(),
                    "precondition: this claim holds while weapon_mods_enabled is forced FALSE");

                var pool = WeaponModRegistry.Pool(WeaponClass.Caster);

                Assert.AreEqual(3, pool.Count,
                    "the Tier A caster pool is expected to be three deep after the pool cut; if this changed, the 'no identity lottery for casters' note in the brief needs revisiting");

                // THE CAP MOVED PAST THE POOL ON 2026-08-06 (3 -> 4), so the pool is SHALLOWER than the cap rather
                // than equal to it. The claim this test exists for is unaffected and if anything stronger: a
                // caster on the Tier A half alone cannot even reach the cap, so it necessarily holds the whole
                // pool whenever it holds its maximum.
                Assert.AreEqual(4, WeaponModRegistry.MaxSpecials, "the per-weapon cap");
                Assert.IsTrue(pool.Count < WeaponModRegistry.MaxSpecials, "the Tier A caster pool is shallower than the cap");

                for (var draw = 0; draw < 500; draw++)
                {
                    var rolled = WeaponModRoller.RollDistinctSpecials(WeaponClass.Caster, WeaponModRegistry.MaxSpecials);

                    Assert.AreEqual(pool.Count, rolled.Count,
                        "a caster draw at the cap must return the whole pool - it must not run short of it, and it must not invent an extra");

                    CollectionAssert.AreEquivalent(pool.Select(m => m.Id).ToArray(), rolled.Select(m => m.Id).ToArray(),
                        "a caster at its maximum holds the whole pool, every time");
                }
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", priorGate);
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

            // the rows the retune deliberately did NOT touch. Cleave was a third until 2026-08-07 (Change 8)
            // and Swift Flight was a fourth until 2026-08-17 (Change 9); both removals are not retunes and do
            // not change what the retune did or did not reach.
            Assert.AreEqual(0.50, WeaponModRegistry.Get(WeaponModId.ShieldBypass).MaxRoll, 1e-12);
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
