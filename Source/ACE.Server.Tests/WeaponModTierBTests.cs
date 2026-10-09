using System;
using System.Collections.Generic;
using System.IO;
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
    /// Tier B - the v2 half of the weapon-mod catalog. Seven modifiers, none of which writes a native property:
    /// each one's record holds a FRACTION and a hand-wired hook in Player_WeaponMods.cs reads it live.
    ///
    /// WHAT IS COVERED HERE: the registry rows and their magnitudes, the fractional-versus-integer split that
    /// keeps a 4% leech from being rounded to +1 of nothing, the weapon_mods_enabled gate in both directions,
    /// the pool depths in both gate states, every pure helper behind the seven effects, and the v1 invariants
    /// re-run with Tier B live in the pool.
    ///
    /// THERE IS ONE GATE, NOT TWO. Tier B had its own tunable (weapon_mod_tier_b_enabled) until 2026-07-30,
    /// when it was removed by repo-owner directive; weapon_mods_enabled is now the single master switch for
    /// crafting, the roll pools and every combat hook alike. The gate tests below are what stands between a
    /// shard with the system off and live Tier B combat code, so they assert BOTH halves of "off" - not
    /// reachable from any pool, and inert at every read - rather than implying one from the other.
    ///
    /// WHAT IS NOT, AND WHY. The combat hooks themselves - ApplyWeaponModOutgoingDamage, ApplyWeaponModSpellHit,
    /// GetWeaponModSpellDamageMod, GetWeaponModAttackSpeedMod, ApplyWeaponModCreatureDeath - all need a live
    /// Player with a session, equipped objects and vitals, which
    /// ACE.Server.Tests cannot construct. Every one of them is a thin wrapper over a function in
    /// WeaponModCombat, and those functions ARE covered here; what is not covered is that the wrapper reads the
    /// right item and that the core site calls it at all. Those are live-loop checks and are queued in
    /// Docs/VERIFY-QUEUE.md rather than faked with a mock that would only assert its own shape.
    ///
    /// The one exception is the weapon-only rule (trap 1), which is too expensive to get wrong to leave to a
    /// live run - see WeaponOnly_* below for the two tests that guard it without a Player.
    /// </summary>
    [TestClass]
    public class WeaponModTierBTests
    {
        private static readonly WeaponClass[] Classes = { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster };

        /// <summary>
        /// The three SURVIVING v2 rows, with the magnitude and class set each must carry. Pinned as a table
        /// rather than read off the registry, so a retune has to move this file deliberately. Magnitudes are
        /// from Docs/WeaponMods/DESIGN.md "Tier B - v2, new combat hooks".
        ///
        /// LifeLeech, ManaLeech, StaminaLeech and Overload were retired 2026-08-17 in the catalog v4 pass and no
        /// longer appear here - see PropertyFloat.cs and WeaponModId.cs for the retirement record.
        /// </summary>
        private static readonly (WeaponModId Id, PropertyFloat Record, double MaxRoll, WeaponClass Classes, string Name)[] Expected =
        {
            (WeaponModId.Ambush,       PropertyFloat.WeaponModAmbush,       0.30, WeaponClass.All,                            "Ambush"),
            (WeaponModId.Quickening,   PropertyFloat.WeaponModQuickening,   0.24, WeaponClass.Melee | WeaponClass.Missile,    "Quickening"),
            (WeaponModId.SecondWind,   PropertyFloat.WeaponModSecondWind,   0.12, WeaponClass.All,                            "Second Wind"),
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// Runs an action with weapon_mods_enabled - the system's ONE gate - forced, restoring it whatever
        /// happens. Every test in this file that names a gate state goes through here, so no test can leave the
        /// tunable moved for the next one.
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

        // ================= the rows =================

        [TestMethod]
        public void TierB_HoldsExactlyTheseSurvivingV2RowsPlusTheV3AndV4Expansions()
        {
            // 2026-08-06: the v3 expansion (Heft, Tension, Leverage, Attunement, Focus, Execution) added six
            // more Tier B rows, covered in detail in WeaponModCatalogV3Tests.cs. Folded into this scope guard
            // as a fixed count rather than dropped, so it still catches an accidental Tier B add/remove.
            const int rowsAddedByTheV3Expansion = 6;

            // 2026-08-17: the v4 expansion (Efficiency, Recovery, Mana Well, Cleanse, Longevity, Siphon, Quick
            // Refresh, Arcane Defender, Panic Reload) added nine more, currently inert - covered in detail in
            // WeaponModCatalogV4Tests.cs. Same reasoning as the v3 count above.
            const int rowsAddedByTheV4Expansion = 9;

            Assert.AreEqual(Expected.Length + rowsAddedByTheV3Expansion + rowsAddedByTheV4Expansion, WeaponModRegistry.TierBMods.Count,
                "the Tier B half of the registry no longer holds exactly the three surviving v2 rows this table names plus the six v3-expansion rows plus the nine v4-expansion rows");

            foreach (var (id, record, maxRoll, classes, name) in Expected)
            {
                Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id}: missing registry row");

                Assert.AreEqual(WeaponModTier.B, definition.Tier, $"{id}: must be Tier B");
                Assert.AreEqual(record, definition.Record, $"{id}: reserved record id moved");
                Assert.AreEqual(maxRoll, definition.MaxRoll, 1e-12, $"{id}: MaxRoll must be {maxRoll}");
                Assert.AreEqual(classes, definition.Classes, $"{id}: class set moved");
                Assert.AreEqual(name, definition.DisplayName, $"{id}: renaming a modifier changes what every player reads on the appraisal panel, so it has to be a decision rather than a refactor artifact");

                Assert.AreEqual(WeaponModDefinition.DefaultMinPotency, definition.MinPotency, 1e-12, $"{id}: potency floor matches DefaultMinPotency, same as Tier A");
                Assert.IsFalse(definition.Binary, $"{id}: rolls a magnitude; a binary modifier would grant MaxRoll unconditionally");
            }
        }

        /// <summary>
        /// SUNDER AND RAMPAGE ARE PHASE 2 AND MUST NOT BE HERE. Both were specified alongside these seven and
        /// both were deliberately deferred: Sunder needs a new enchantment/spell row to carry its debuff and
        /// Rampage needs genuinely new per-target stack state. Their PropertyFloats 8148 and 8149 are RESERVED
        /// so nothing else takes them, but an enum member or a registry row here would put an inert entry into
        /// every roll pool - a slot the player pays for that does nothing at all.
        /// </summary>
        [TestMethod]
        public void TierB_SunderAndRampageAreReservedNotImplemented()
        {
            var names = Enum.GetNames(typeof(WeaponModId));

            CollectionAssert.DoesNotContain(names, "Sunder", "Sunder is phase 2 - it needs a new enchantment/spell row that does not exist yet");
            CollectionAssert.DoesNotContain(names, "Rampage", "Rampage is phase 2 - it needs per-target stack state that does not exist yet");

            foreach (var id in new[] { WeaponModRegistry.TierBReservedBandStart, WeaponModRegistry.TierBReservedBandEnd })
            {
                Assert.IsFalse(WeaponModRegistry.TryGet((PropertyFloat)id, out _),
                    $"PropertyFloat {id} resolves to a registry row, but 8148-8149 are reserved for the phase-2 Sunder and Rampage");

                // PropertyFloat's underlying type is ushort, so the value has to be cast to it - IsDefined
                // throws on a boxed int against a ushort-backed enum rather than returning false
                Assert.IsFalse(Enum.IsDefined(typeof(PropertyFloat), (ushort)id),
                    $"PropertyFloat {id} has an enum member, but it is reserved for a phase-2 entry that does not exist");
            }

            Assert.AreEqual(8148, WeaponModRegistry.TierBReservedBandStart);
            Assert.AreEqual(8149, WeaponModRegistry.TierBReservedBandEnd);
        }

        /// <summary>
        /// THE STORAGE DIVERGENCE, ASSERTED. A Tier B row writes NO native property, and three separate
        /// behaviours depend on that single fact:
        ///
        ///   - IsInteger is derived from NativeInt, so a null native keeps the magnitude FRACTIONAL. The
        ///     integer branch in WeaponModValue.Resolve rounds and then floors at 1, which would turn a 4%
        ///     leech into "+1" of a unit that does not exist.
        ///   - ApplySpecial writes only the record, because WriteNative no-ops.
        ///   - Reversal is a bare RemoveProperty with nothing to subtract and nothing to restore.
        /// </summary>
        [TestMethod]
        public void TierB_WritesNoNativeAndStaysFractional()
        {
            foreach (var definition in WeaponModRegistry.TierBMods)
            {
                Assert.IsNull(definition.NativeInt, $"{definition.Id}: a Tier B row must set no NativeInt");
                Assert.IsNull(definition.NativeFloat, $"{definition.Id}: a Tier B row must set no NativeFloat");
                Assert.IsFalse(definition.WritesNative, $"{definition.Id}: WritesNative must be false");
                Assert.IsFalse(definition.IsInteger, $"{definition.Id}: a Tier B magnitude must never take the integer branch");

                // the arithmetic entry points tolerate a row with no native rather than dereferencing one
                var weapon = WeaponModTestKit.MakeWeapon();

                Assert.IsNull(definition.ReadNative(weapon), $"{definition.Id}: ReadNative must be null on a row with no native");

                definition.WriteNative(weapon, 5.0);

                Assert.IsNull(definition.ReadNative(weapon), $"{definition.Id}: WriteNative must be a no-op on a row with no native");
            }
        }

        /// <summary>
        /// The full apply / reverse cycle for a Tier B row on a real item: the record holds the ROLL FRACTION
        /// exactly, that fraction resolves back to the magnitude it came from, nothing else on the item moves,
        /// and reversal REMOVES the row rather than zeroing it.
        ///
        /// REWRITTEN FOR THE 2026-08-07 STORAGE SPLIT. This test previously asserted the record held the
        /// magnitude, which is now true only of Tier A. The workmanship sweep is what makes the distinction
        /// visible: the fraction tracks workmanship while MaxRoll stays out of it entirely.
        /// </summary>
        [TestMethod]
        public void TierB_ApplyWritesOnlyTheRecordAndReverseRemovesIt()
        {
            foreach (var definition in WeaponModRegistry.TierBMods)
            {
                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee, workmanship: workmanship);

                    var before = SnapshotFloats(weapon);
                    var fraction = WeaponModValue.RollFraction(1.0, workmanship);
                    var magnitude = WeaponModValue.MagnitudeFromFraction(definition, fraction, 1.0);

                    Assert.AreEqual(workmanship / 10.0, fraction, 1e-12,
                        $"{definition.Id}: a perfect roll at workmanship {workmanship} is exactly that workmanship as a fraction, with no MaxRoll term in it");

                    Assert.AreEqual(WeaponModValue.Resolve(definition, 1.0, workmanship, 1.0), magnitude, 1e-12,
                        $"{definition.Id}: fraction storage must be substitutable for Resolve - if these diverge, a rolled weapon and a seeded one stop agreeing");

                    Assert.AreEqual(definition.MaxRoll * workmanship / 10.0, magnitude, 1e-12,
                        $"{definition.Id}: a perfect roll at workmanship {workmanship} is exactly the linear fraction, with no rounding");

                    WeaponModTinkerSet.ApplySpecialAtFraction(weapon, definition, fraction, 1.0);

                    Assert.AreEqual(fraction, weapon.GetProperty(definition.Record).Value, 1e-12,
                        $"{definition.Id}: the record must hold the ROLL FRACTION, not the magnitude - a magnitude here would stop MaxRoll retunes reaching existing weapons");

                    Assert.AreEqual(magnitude, WeaponModTinkerSet.ReadMagnitude(weapon, definition, 1.0), 1e-12,
                        $"{definition.Id}: the stored fraction must resolve back to the magnitude it was applied at");

                    // THE POINT OF THE SPLIT, asserted rather than described: the catalog moves under an
                    // existing weapon. Halving the scale halves what this already-applied modifier is worth,
                    // with no reroll and no migration.
                    Assert.AreEqual(magnitude / 2.0, WeaponModTinkerSet.ReadMagnitude(weapon, definition, 0.5), 1e-12,
                        $"{definition.Id}: a retune must reach a weapon already carrying the modifier");

                    var after = SnapshotFloats(weapon);
                    after.Remove(definition.Record);

                    CollectionAssert.AreEquivalent(before.ToList(), after.ToList(),
                        $"{definition.Id}: applying a Tier B modifier changed a property other than its own record");

                    WeaponModTinkerSet.ReverseSpecial(weapon, definition);

                    Assert.IsNull(weapon.GetProperty(definition.Record),
                        $"{definition.Id}: the record must be REMOVED, never zeroed - a zeroed row is the dead row the design forbids");

                    CollectionAssert.AreEquivalent(before.ToList(), SnapshotFloats(weapon).ToList(),
                        $"{definition.Id}: the item did not return to its starting state after a reversal");
                }
            }
        }

        /// <summary>ClearSpecials walks the whole registry, so it must clear a Tier B record as readily as a Tier A one.</summary>
        [TestMethod]
        public void TierB_ClearSpecialsClearsTierBRecordsToo()
        {
            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

            WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(WeaponModId.Devastation), 4.0);
            WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(WeaponModId.Ambush), 0.30);
            WeaponModTinkerSet.ApplySpecial(weapon, WeaponModRegistry.Get(WeaponModId.SecondWind), 0.12);

            Assert.AreEqual(3, WeaponModTinkerSet.SpecialCount(weapon), "precondition: three records, one Tier A and two Tier B");

            WeaponModTinkerSet.ClearSpecials(weapon);

            Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon), "ClearSpecials left a record behind");

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                Assert.IsNull(weapon.GetProperty(definition.Record),
                    $"{definition.Id}: a record survived ClearSpecials, so it would occupy a slot forever");
            }
        }

        // ================= magnitude =================

        /// <summary>
        /// The sweep every entry has to survive: across the whole potency band crossed with every workmanship,
        /// a rolled magnitude never exceeds its MaxRoll and never resolves to zero. Run through
        /// WeaponModValue.Roll, so it covers the MinPotency draw and the live magnitude-scale tunable too.
        ///
        /// The zero half is the one that matters for a fractional entry: a 4% maximum at the 0.25 potency floor
        /// on a workmanship 1 weapon is 0.001, which the INTEGER branch would round to 0 and then floor to 1.
        /// It must survive as 0.001.
        /// </summary>
        [TestMethod]
        public void TierB_RolledMagnitudeNeverExceedsMaxRollAndNeverResolvesToZero()
        {
            foreach (var definition in WeaponModRegistry.TierBMods)
            {
                var floor = definition.MaxRoll * WeaponModDefinition.DefaultMinPotency / 10.0;

                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    for (var roll = 0; roll < 400; roll++)
                    {
                        var magnitude = WeaponModValue.Roll(definition, workmanship);

                        Assert.IsTrue(magnitude <= definition.MaxRoll + 1e-12,
                            $"{definition.Id}: workmanship {workmanship} rolled {magnitude}, above its MaxRoll of {definition.MaxRoll}");

                        Assert.IsTrue(magnitude > 0.0,
                            $"{definition.Id}: workmanship {workmanship} rolled {magnitude} - a rolled special must never be a no-op");

                        Assert.IsTrue(magnitude >= floor - 1e-12,
                            $"{definition.Id}: workmanship {workmanship} rolled {magnitude}, below the MinPotency floor's own minimum of {floor}");

                        Assert.IsTrue(WeaponModValue.IsLiveMagnitude(definition, magnitude),
                            $"{definition.Id}: workmanship {workmanship} rolled {magnitude}, which the live-magnitude test would drop");

                        Assert.AreNotEqual(Math.Round(magnitude), magnitude,
                            $"{definition.Id}: workmanship {workmanship} rolled {magnitude}, a whole number - a fractional entry that quantizes has taken the integer branch");
                    }
                }
            }
        }

        /// <summary>
        /// The exact worst case, stated as a number rather than as a bound: the smallest magnitude any Tier B
        /// entry can roll is its MaxRoll x 0.25 x 0.1, and the largest is its MaxRoll. Both endpoints of the
        /// potency band, both endpoints of workmanship.
        /// </summary>
        [TestMethod]
        public void TierB_TheMagnitudeBandIsExactlyMaxRollTimesPotencyTimesWorkmanship()
        {
            // TOLERANCE WIDENED FROM 1e-15 TO 1e-12 on 2026-08-06: the v3 expansion added Heft, whose MaxRoll
            // of 22 is over an order of magnitude larger than any prior Tier B row, and accumulates more
            // floating-point error per multiply than 1e-15 tolerates. Still far tighter than the design cares
            // about - this remains an exactness check, not a fuzzy one.
            const double tolerance = 1e-12;

            foreach (var definition in WeaponModRegistry.TierBMods)
            {
                Assert.AreEqual(definition.MaxRoll * 0.25 * 0.1, WeaponModValue.Resolve(definition, 0.25, 1.0, 1.0), tolerance,
                    $"{definition.Id}: the floor of the band");

                Assert.AreEqual(definition.MaxRoll, WeaponModValue.Resolve(definition, 1.0, 10.0, 1.0), tolerance,
                    $"{definition.Id}: the ceiling of the band");

                // linearity in workmanship, exact because nothing rounds
                var unit = WeaponModValue.Resolve(definition, 1.0, 1.0, 1.0);

                for (var workmanship = 1; workmanship <= 10; workmanship++)
                {
                    Assert.AreEqual(unit * workmanship, WeaponModValue.Resolve(definition, 1.0, workmanship, 1.0), tolerance,
                        $"{definition.Id}: workmanship {workmanship} must be exactly {workmanship} times workmanship 1");
                }
            }
        }

        // ================= the gate =================

        /// <summary>
        /// GATE OFF: no Tier B entry is reachable from ANY roll pool, on any class, over a draw deep enough
        /// that a leak would have to be spectacularly unlucky to hide.
        ///
        /// THIS IS THE PIN THAT MATTERS NOW. weapon_mods_enabled is the only thing standing between a shard
        /// running with the system off and live Tier B combat code, so its two halves - unreachable from every
        /// pool here, inert at every read in the test below - are asserted explicitly rather than either one
        /// being taken as evidence for the other. Both depths are checked at the LIVE accessor as well as
        /// through the pure overload, so a wiring mistake that left Pool() reading some other bool fails here.
        /// </summary>
        [TestMethod]
        public void Gate_OffMakesEveryTierBEntryUnreachableFromEveryPool()
        {
            WithGate(false, () =>
            {
                var tierB = new HashSet<WeaponModId>(WeaponModRegistry.TierBMods.Select(m => m.Id));

                Assert.IsFalse(WeaponModRegistry.Enabled(), "precondition: the gate really is off at the live accessor");

                // the depths the design prices against, at the LIVE accessor - Tier A only (unchanged by the
                // 2026-08-06 v3 expansion or the 2026-08-17 v4 expansion, since every row each expansion adds
                // is Tier B; melee came down by one on 2026-08-07 with the Cleave removal, and missile came
                // down by one on 2026-08-17 with the Swift Flight removal, both Tier A removals)
                Assert.AreEqual(4, WeaponModRegistry.Pool(WeaponClass.Melee).Count, "melee pool with weapon_mods_enabled FALSE: Tier A only");
                Assert.AreEqual(4, WeaponModRegistry.Pool(WeaponClass.Missile).Count, "missile pool with weapon_mods_enabled FALSE: Tier A only");
                Assert.AreEqual(3, WeaponModRegistry.Pool(WeaponClass.Caster).Count, "caster pool with weapon_mods_enabled FALSE: Tier A only");

                foreach (var weaponClass in Classes)
                {
                    foreach (var definition in WeaponModRegistry.Pool(weaponClass))
                    {
                        Assert.AreEqual(WeaponModTier.A, definition.Tier,
                            $"{definition.Id} is in the {weaponClass} pool with weapon_mods_enabled FALSE");
                    }

                    for (var draw = 0; draw < 2000; draw++)
                    {
                        foreach (var rolled in WeaponModRoller.RollDistinctSpecials(weaponClass, WeaponModRegistry.MaxSpecials))
                        {
                            Assert.IsFalse(tierB.Contains(rolled.Id),
                                $"{rolled.Id} was rolled onto a {weaponClass} weapon with weapon_mods_enabled FALSE");
                        }
                    }
                }

                // and a full reroll cannot produce one either
                foreach (var weaponClass in Classes)
                {
                    var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);

                    for (var use = 0; use < 200; use++)
                    {
                        Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} reroll {use} produced no result");

                        foreach (var (definition, _) in WeaponModTinkerSet.ReadSpecials(weapon))
                        {
                            Assert.AreEqual(WeaponModTier.A, definition.Tier,
                                $"{weaponClass} reroll {use} landed {definition.Id} with weapon_mods_enabled FALSE");
                        }
                    }
                }
            });
        }

        /// <summary>
        /// GATE OFF: every combat effect reads zero, even on a weapon that already carries a Tier B record from
        /// when the gate was on. That is the "and every effect is inert" half of the gate, and it is the reason
        /// the gate lives inside WeaponModCombat.ReadWeaponOnly rather than at each hook.
        ///
        /// THE APPRAISAL HALF IS ASSERTED THROUGH A REAL BUILT PROFILE, not through
        /// WeaponModDisplay.GetAppraisalLines. Under the single gate that method no longer filters by tier - its
        /// only caller, AppraiseInfo.BuildProfile, is itself behind weapon_mods_enabled, so it can only run in
        /// the state where every Tier B hook is live. The claim worth pinning is therefore the one a player can
        /// observe: with the gate off, the weapon-mod block does not reach the panel at all. Asserting it at
        /// AppraiseInfo keeps the guarantee at the level where it is actually reachable.
        /// </summary>
        [TestMethod]
        public void Gate_OffMakesAHeldTierBRecordInert()
        {
            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

            foreach (var definition in WeaponModRegistry.TierBMods)
                WeaponModTinkerSet.ApplySpecial(weapon, definition, definition.MaxRoll);

            WithGate(false, () =>
            {
                foreach (var definition in WeaponModRegistry.TierBMods)
                {
                    Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, definition.Id), 1e-15,
                        $"{definition.Id} reads a live value with weapon_mods_enabled FALSE, so its hook would fire");
                }

                var use = BuiltProfileUse(weapon);

                foreach (var definition in WeaponModRegistry.TierBMods)
                {
                    Assert.IsFalse(use.Contains(definition.DisplayName, StringComparison.Ordinal),
                        $"the appraisal panel named {definition.Id} with weapon_mods_enabled FALSE, where it is inert - a panel that lies on an operator's say-so is worse than one that shows less");
                }
            });

            WithGate(true, () =>
            {
                foreach (var definition in WeaponModRegistry.TierBMods)
                {
                    Assert.AreEqual(definition.MaxRoll, WeaponModCombat.ReadWeaponOnly(weapon, definition.Id), 1e-15,
                        $"{definition.Id} reads zero with weapon_mods_enabled TRUE, so its hook would never fire");
                }

                // the same records, now visible - both at the display helper and on the built panel, so the
                // gate-off assertion above is proved non-vacuous at the same surface it was made on
                var lines = WeaponModDisplay.GetAppraisalLines(weapon);
                var use = BuiltProfileUse(weapon);

                foreach (var definition in WeaponModRegistry.TierBMods)
                {
                    Assert.IsTrue(lines.Any(l => l.Contains(definition.DisplayName)),
                        $"{definition.Id} is held and live but does not appear on the appraisal panel");

                    Assert.IsTrue(use.Contains(definition.DisplayName, StringComparison.Ordinal),
                        $"{definition.Id} is held and live but does not reach a BUILT appraisal profile");
                }
            });
        }

        /// <summary>
        /// GATE ON: every Tier B entry is reachable from the pools of exactly the classes its row names, and
        /// the resulting depths are the ones the design's pricing rests on.
        /// </summary>
        [TestMethod]
        public void Gate_OnAddsEveryTierBEntryToTheRightClassPools()
        {
            WithGate(true, () =>
            {
                Assert.AreEqual(16, WeaponModRegistry.Pool(WeaponClass.Melee).Count, "melee pool with the gate on: 4 Tier A + 3 Tier B v2 + 4 Tier B v3 + 5 Tier B v4");
                Assert.AreEqual(17, WeaponModRegistry.Pool(WeaponClass.Missile).Count, "missile pool with the gate on: 4 Tier A + 3 Tier B v2 + 4 Tier B v3 + 6 Tier B v4");
                Assert.AreEqual(16, WeaponModRegistry.Pool(WeaponClass.Caster).Count, "caster pool with the gate on: 3 Tier A + 2 Tier B v2 + 3 Tier B v3 + 8 Tier B v4");

                foreach (var (id, _, _, classes, _) in Expected)
                {
                    foreach (var weaponClass in Classes)
                    {
                        var inPool = WeaponModRegistry.Pool(weaponClass).Any(m => m.Id == id);
                        var shouldBe = (classes & weaponClass) != 0;

                        Assert.AreEqual(shouldBe, inPool,
                            $"{id} is {(inPool ? "" : "not ")}in the {weaponClass} pool but its row says {classes}");
                    }
                }

                // Quickening is the one that would be silently dead in the wrong pool, so it is pinned by name
                // as well as by the table above
                Assert.IsFalse(WeaponModRegistry.Pool(WeaponClass.Caster).Any(m => m.Id == WeaponModId.Quickening),
                    "Quickening is melee and missile only - its hook reads GetEquippedWeapon(), which never returns a wand");

                // and a deep draw actually reaches every one of them
                foreach (var weaponClass in Classes)
                {
                    var seen = new HashSet<WeaponModId>();

                    for (var draw = 0; draw < 4000; draw++)
                    {
                        foreach (var rolled in WeaponModRoller.RollDistinctSpecials(weaponClass, WeaponModRegistry.MaxSpecials))
                            seen.Add(rolled.Id);
                    }

                    foreach (var definition in WeaponModRegistry.Pool(weaponClass))
                    {
                        Assert.IsTrue(seen.Contains(definition.Id),
                            $"{definition.Id} is in the {weaponClass} pool but never came out of 4000 draws");
                    }
                }
            });
        }

        // ================= the v1 invariants, with Tier B live =================

        /// <summary>
        /// The whole v1 invariant harness, re-run with the Tier B gate ON: the budget sums to ten,
        /// NumTimesTinkered is pinned at ten, neither log grows, no record is left dead in either band, the
        /// permanent three-special bound holds, no weapon holds the same special twice, and CanApply agrees
        /// with ApplySwap on every use.
        ///
        /// This is the test that would have caught a Tier B row breaking the slot model - the pool is nearly
        /// twice as deep with the gate on, so a bug that needs two Tier B entries on one weapon is common here
        /// and unreachable in the gate-off suites.
        /// </summary>
        [TestMethod]
        public void Gate_OnHoldsEveryV1InvariantAcrossLongRerollAndSwapRuns()
        {
            WithGate(true, () =>
            {
                foreach (var weaponClass in Classes)
                {
                    var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
                    var probe = new WeaponModProbe(weapon);

                    for (var use = 0; use < 120; use++)
                    {
                        var context = $"{weaponClass} tier-B use {use}";

                        Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{context}: reroll produced no result");

                        probe.AssertInvariants(weapon, weaponClass, context);

                        WeaponModTestKit.Swap(weapon, weaponClass, 10.0, context);

                        probe.AssertInvariants(weapon, weaponClass, context);
                    }
                }
            });
        }

        /// <summary>
        /// An imbued weapon with the gate on: reserved slots still come off the budget first, and a Tier B
        /// record never takes a slot an imbue owns.
        /// </summary>
        [TestMethod]
        public void Gate_OnRespectsReservedImbueSlots()
        {
            WithGate(true, () =>
            {
                foreach (var weaponClass in Classes)
                {
                    // two imbue bits, so two of the ten slots are spoken for
                    var weapon = WeaponModTestKit.MakeUntinkered(weaponClass,
                        (int)(ACE.Entity.Enum.ImbuedEffectType.CriticalStrike | ACE.Entity.Enum.ImbuedEffectType.ArmorRending));

                    var probe = new WeaponModProbe(weapon);

                    for (var use = 0; use < 60; use++)
                    {
                        var context = $"{weaponClass} imbued tier-B use {use}";

                        Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{context}: reroll produced no result");

                        probe.AssertInvariants(weapon, weaponClass, context);

                        Assert.AreEqual(2, WeaponModTinkerSet.ReadReservedSlots(weapon), $"{context}: the two imbues must keep their slots");
                    }
                }
            });
        }

        // ================= trap 1: the weapon-only accessor =================

        /// <summary>
        /// THE MOST VALUABLE TEST IN THIS FILE. A weapon mod lives on ONE item. The armor system's sibling
        /// accessors - Creature.GetEquippedModValue and Creature.GetEquippedModPotencySum - SUM a mod's stored
        /// value across EVERY equipped item, which is right there (armor mods land on up to a dozen pieces and
        /// stack additively) and would be an overcount by however many items a player is wearing here.
        ///
        /// Reusing one of them would have let a player stack Ambush off a helmet, and it would have looked
        /// completely ordinary in review, because the call reads identically. So this asserts the difference
        /// numerically: the same records on five items, the summing shape against the weapon-only one.
        /// </summary>
        [TestMethod]
        public void WeaponOnly_ReadsTheWeaponAloneAndNeverSumsOtherEquippedItems()
        {
            WithGate(true, () =>
            {
                var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

                // four other pieces a player could be wearing at the same time, each carrying the SAME record.
                // A weapon mod can never legitimately reach armor, but a shard rollback or a hand-edited biota
                // can put a row anywhere, and the accessor must be indifferent to that rather than adding it in.
                var otherEquipped = new List<WorldObject>();

                // Seeded as ROLL FRACTIONS, because that is what a Tier B record holds since the 2026-08-07
                // storage split. A full-strength roll is 1.0 and reads back as the row's MaxRoll.
                for (var i = 0; i < 4; i++)
                {
                    var piece = WeaponModTestKit.MakeWeapon();

                    piece.SetProperty(PropertyFloat.WeaponModAmbush, 1.0);
                    otherEquipped.Add(piece);
                }

                weapon.SetProperty(PropertyFloat.WeaponModAmbush, 1.0);

                var ambush = WeaponModRegistry.Get(WeaponModId.Ambush);
                var weaponOnly = WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Ambush);

                Assert.AreEqual(ambush.MaxRoll, weaponOnly, 1e-15,
                    "the weapon-only accessor must resolve the weapon's own record and nothing else");

                // the shape the armor system uses, computed here so the two are compared rather than described
                var summedAcrossEquipped = otherEquipped.Concat(new[] { weapon })
                    .Sum(i => i.GetProperty(PropertyFloat.WeaponModAmbush) ?? 0.0);

                Assert.AreEqual(5.0, summedAcrossEquipped, 1e-15, "precondition: five items at a full-strength roll sum to 5.0");

                Assert.AreNotEqual(summedAcrossEquipped, weaponOnly,
                    "the weapon-only accessor returned the SUM across equipped items - that is Creature.GetEquippedModValue's contract, and using it here multiplies every weapon mod by the number of items a player is wearing");

                // and it is indifferent to the other items entirely: strip the weapon's own record and it reads
                // zero however many other pieces carry one
                weapon.RemoveProperty(PropertyFloat.WeaponModAmbush);

                Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Ambush), 1e-15,
                    "a weapon with no record of its own must read zero, whatever else is equipped");

                Assert.AreEqual(4.0, otherEquipped.Sum(i => i.GetProperty(PropertyFloat.WeaponModAmbush) ?? 0.0), 1e-15,
                    "precondition: the other four items still carry their records, so the zero above is not vacuous");
            });
        }

        /// <summary>
        /// The structural half of the same guard, because the numeric test above can only prove the accessor
        /// this file calls is weapon-only - it cannot prove a HOOK does not call the summing one directly.
        ///
        /// Player_WeaponMods.cs is the whole of the Tier B combat layer, so scanning it for the three summing
        /// entry points covers every hook at once. A future hook that reaches for GetEquippedModValue fails
        /// here rather than shipping a mod worth twelve times its rolled value.
        /// </summary>
        [TestMethod]
        public void WeaponOnly_TheCombatLayerNeverCallsTheSummingAccessors()
        {
            var path = FindInSourceTree("Source/ACE.Server/WorldObjects/Player_WeaponMods.cs");

            Assert.IsNotNull(path, $"Could not find Player_WeaponMods.cs by walking up from {AppContext.BaseDirectory}.");

            var source = File.ReadAllText(path);

            // the accessor names appear in this file's own doc comment explaining why they must not be used,
            // so the scan is for a CALL - a "." immediately before the name - not for the bare name
            foreach (var forbidden in new[] { "GetEquippedModValue(", "GetEquippedModPotencySum(", "EquippedObjects" })
            {
                Assert.IsFalse(source.Contains("." + forbidden, StringComparison.Ordinal),
                    $"Player_WeaponMods.cs calls {forbidden} - that sums a mod's value across EVERY equipped item, which multiplies a weapon mod by however many pieces the player is wearing. A weapon mod lives on ONE item; read it with WeaponModCombat.ReadWeaponOnly.");
            }

            // and the file really is the combat layer, so the scan is not passing over an empty file
            Assert.IsTrue(source.Contains("ApplyWeaponModOutgoingDamage", StringComparison.Ordinal), "Player_WeaponMods.cs no longer holds the outgoing-damage hook, so this scan may be looking at the wrong file");
            Assert.IsTrue(source.Contains("GetEquippedWand()", StringComparison.Ordinal), "Player_WeaponMods.cs no longer reads the wand, so Overload and the spell-side effects are dead on every caster");
        }

        // ================= the pure helpers behind each effect =================

        /// <summary>
        /// Ambush's condition. A target at full health is one that has not been hit yet, which is what makes
        /// "the first strike" expressible with no per-target state at all.
        /// </summary>
        [TestMethod]
        public void Ambush_FiresOnlyAgainstAFullHealthTarget()
        {
            var target = TestCreatures.CreateDefender(maxHealth: 100);

            // read the maximum rather than assuming it: it comes out of the retail attribute formula, so a
            // hardcoded 100 would make this test about TestCreatures rather than about the predicate
            var max = target.Health.MaxValue;

            Assert.IsTrue(max > 2, "precondition: the test creature has a usable health pool");

            target.Health.Current = max;
            Assert.IsTrue(WeaponModCombat.IsFullHealth(target), "an untouched target is in range");

            target.Health.Current = max - 1;
            Assert.IsFalse(WeaponModCombat.IsFullHealth(target), "one point of damage takes a target out of range");

            target.Health.Current = 1;
            Assert.IsFalse(WeaponModCombat.IsFullHealth(target), "a nearly dead target is not an opener");

            target.Health.Current = 0;
            Assert.IsFalse(WeaponModCombat.IsFullHealth(target), "a dead target is not an opener");

            target.Health.Current = max;
            Assert.IsTrue(WeaponModCombat.IsFullHealth(target), "a fully healed target is an opener again");

            target.Health.Current = max + 5;
            Assert.IsTrue(WeaponModCombat.IsFullHealth(target),
                "a target reading above its maximum - a buffed vital caught mid-recalculation - still counts as full rather than falling out of range");

            Assert.IsFalse(WeaponModCombat.IsFullHealth(null), "a null target is never in range");
        }

        /// <summary>The multiplier Ambush turns into. Absent, zero and negative all read as no bonus.</summary>
        [TestMethod]
        public void Ambush_MultiplierIsOnePlusTheFraction()
        {
            Assert.AreEqual(1.15, WeaponModCombat.DamageMultiplier(0.15), 1e-15);
            Assert.AreEqual(1.0, WeaponModCombat.DamageMultiplier(0.0), 1e-15);
            Assert.AreEqual(1.0, WeaponModCombat.DamageMultiplier(-0.5), 1e-15, "a negative magnitude must never SLOW or WEAKEN anything");
            Assert.AreEqual(1.0, WeaponModCombat.DamageMultiplier(double.NaN), 1e-15);

            // the maximum roll on a workmanship 10 weapon is exactly the design's current MaxRoll (retuned to
            // +30% on 2026-08-06, from +15%)
            Assert.AreEqual(1.30, WeaponModCombat.DamageMultiplier(WeaponModRegistry.Get(WeaponModId.Ambush).MaxRoll), 1e-15);
        }

        /// <summary>
        /// RollsFree's general behavior. The magnitude is a PROBABILITY, not a percent bonus, so the whole
        /// helper is a comparison - and the boundary cases are what a wrong comparison would get wrong.
        ///
        /// FORMERLY "Overload_RollsFreeExactlyAtItsStatedRate", renamed 2026-08-17 when Overload (its only
        /// caller) was retired in the catalog v4 pass. RollsFree itself survives - Cleanse and Siphon are its
        /// intended Phase 2 reusers - so the test is re-pointed at a plain 0.20 chance rather than deleted.
        /// </summary>
        [TestMethod]
        public void RollsFree_FiresExactlyAtItsStatedRate()
        {
            Assert.IsTrue(WeaponModCombat.RollsFree(0.20, 0.0), "a roll of 0 is inside a 20% chance");
            Assert.IsTrue(WeaponModCombat.RollsFree(0.20, 0.199999), "a roll just under the chance fires");
            Assert.IsFalse(WeaponModCombat.RollsFree(0.20, 0.20), "the comparison is strict, so the draw is uniform over [0, chance)");
            Assert.IsFalse(WeaponModCombat.RollsFree(0.20, 0.9), "a roll above the chance does not fire");

            Assert.IsFalse(WeaponModCombat.RollsFree(0.0, 0.0), "an unequipped modifier never fires, not even on a roll of 0");
            Assert.IsFalse(WeaponModCombat.RollsFree(-1.0, 0.0));
            Assert.IsFalse(WeaponModCombat.RollsFree(double.NaN, 0.5));
            Assert.IsFalse(WeaponModCombat.RollsFree(0.5, double.NaN));

            // the observed rate over the whole unit interval is the stated rate
            const double chance = 0.20;
            var fired = 0;

            for (var step = 0; step < 10000; step++)
            {
                if (WeaponModCombat.RollsFree(chance, step / 10000.0))
                    fired++;
            }

            Assert.AreEqual(2000, fired, "a 0.20 chance must fire on exactly a fifth of a uniform sweep");
        }

        /// <summary>
        /// Second Wind's payout: a fraction of MAXIMUM, rounded half up, never negative.
        ///
        /// UNLIKE THE LEECHES THERE IS NO CARRY, and the boundary below records what that costs. A kill is a
        /// discrete, infrequent event rather than a per-hit trickle, so there is no stream of sub-point
        /// payments to accumulate - but it does mean a payout under half a point rounds away. That is only
        /// reachable at a low roll on a small pool, which is the workmanship curve working as designed rather
        /// than a dead modifier: the same weapon's other magnitudes are equally small.
        /// </summary>
        [TestMethod]
        public void SecondWind_RestoresAFractionOfTheMaximum()
        {
            Assert.AreEqual(12, WeaponModCombat.RestoreFromMax(100, 0.12));
            Assert.AreEqual(60, WeaponModCombat.RestoreFromMax(500, 0.12));
            Assert.AreEqual(1, WeaponModCombat.RestoreFromMax(5, 0.12), "0.6 rounds up to 1");
            Assert.AreEqual(0, WeaponModCombat.RestoreFromMax(4, 0.12), "0.48 rounds DOWN to nothing - there is no carry here, unlike the leeches");
            Assert.AreEqual(0, WeaponModCombat.RestoreFromMax(0, 0.12), "a vital with no maximum restores nothing rather than throwing");
            Assert.AreEqual(0, WeaponModCombat.RestoreFromMax(500, 0.0), "an unequipped modifier restores nothing");
            Assert.AreEqual(0, WeaponModCombat.RestoreFromMax(500, -0.5));
            Assert.AreEqual(0, WeaponModCombat.RestoreFromMax(500, double.NaN));

            // the WORST legal roll - potency at the 0.25 floor on a workmanship 1 weapon - still buys
            // something on a realistic endgame vital pool, which is the case that decides whether the
            // no-carry choice above is survivable
            var floor = WeaponModRegistry.Get(WeaponModId.SecondWind).MaxRoll * 0.25 * 0.1;

            Assert.AreEqual(0.003, floor, 1e-15, "precondition: the lowest Second Wind magnitude is 0.3% of maximum");

            Assert.IsTrue(WeaponModCombat.RestoreFromMax(400, floor) > 0,
                $"the lowest possible Second Wind roll ({floor}) restores nothing on a 400 health pool, so the modifier would be a dead slot at endgame rather than merely a weak one");

            // and the worst roll a workmanship 10 weapon can produce is comfortably clear of the rounding edge
            var endgameFloor = WeaponModRegistry.Get(WeaponModId.SecondWind).MaxRoll * 0.25;

            Assert.AreEqual(3, WeaponModCombat.RestoreFromMax(100, endgameFloor), "the worst roll on a workmanship 10 weapon is 3% of maximum");
        }

        // The three leech-specific tests that lived here (Leech_AmountIsDamageTimesTheFraction,
        // Leech_AccrualPaysOutTheExactTotalWithinHalfAPoint, Leech_AccrualAgreesWithTheBloodlustPrecedent) were
        // removed 2026-08-17 in the catalog v4 pass, alongside WeaponModCombat.AccrueVital and .LeechAmount
        // themselves: LifeLeech/ManaLeech/StaminaLeech were the only callers, and all three were retired. See
        // PropertyFloat.cs and WeaponModId.cs for the retirement record.

        // ================= helpers =================

        /// <summary>
        /// The "Property Details:" block as a REAL built appraisal profile would send it, or "" when the item
        /// gets no Use string at all. This is the surface the weapon-mod block actually reaches a player
        /// through, and it is behind weapon_mods_enabled at AppraiseInfo.BuildProfile - which is why the gate
        /// claims about the panel are asserted here rather than against WeaponModDisplay directly.
        ///
        /// Built with a null examiner, which is safe for a bare test weapon and only for that: every use of the
        /// examiner in BuildProfile sits behind a property test (ScribeAccount, HouseOwnerAccount, a locked Door
        /// or Chest) that no weapon here satisfies. Same idiom, and same caveat, as
        /// WeaponModRetuneRoundTwoTests.Change4_TheBlockReachesUseThroughABuiltProfile.
        /// </summary>
        private static string BuiltProfileUse(WorldObject weapon)
        {
            var info = new AppraiseInfo(weapon, null);

            return info.PropertiesString.TryGetValue(PropertyString.Use, out var use) ? use : string.Empty;
        }

        private static Dictionary<PropertyFloat, double> SnapshotFloats(WorldObject weapon)
        {
            var snapshot = new Dictionary<PropertyFloat, double>();

            foreach (PropertyFloat property in Enum.GetValues(typeof(PropertyFloat)))
            {
                var value = weapon.GetProperty(property);

                if (value != null)
                    snapshot[property] = value.Value;
            }

            return snapshot;
        }

        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works regardless of
        /// the bin\Debug vs bin\x64\Debug output layout. Same idiom as PropertyRegistryTests.
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
