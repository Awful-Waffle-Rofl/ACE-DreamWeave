using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Factories.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Phase 1 framework tests for the equipment-mod system: registry integrity, the potency clamp, and the
    /// potency -> magnitude resolution (including the global scale tunable).
    ///
    /// The equipped read side (Creature.GetEquippedModPotencySum / GetEquippedModValue) is NOT covered here:
    /// EquippedObjects is populated through Creature.TryEquipObject, which needs a live wielder with a
    /// landblock, motion table and network session, so a fixture would test the harness rather than the mod
    /// math. What that method adds over the pure pieces is a per-item Clamp01 inside a LINQ Sum, and both
    /// halves are proven here (ClampedPotencySum_MatchesTheReadSideContract mirrors the exact expression).
    /// The equipped path is verified in-game per the plan's Phase 4 live loop.
    /// </summary>
    [TestClass]
    public class EquipmentModTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// Class abilities an equipment-mod row may name while no handler is registered for the id, because
        /// the id was deliberately reserved either AHEAD of its handler or AFTER the handler was retired but
        /// is still held inert pending a future repurpose.
        ///
        /// Emptied 2026-08-17 (Phase 1, Berserker/Rogue balance pass) when BreakArmorAbility registered,
        /// REFILLED 2026-09-12 (class ability overhaul) with Heavy Draw and Shield Check, and EMPTIED AGAIN
        /// 2026-10-02 when those two rows were retired outright (removed from EquipmentModRegistry, not
        /// repurposed) rather than held pending Pinning Shot/Kinetic Charge - see RetiredIds below and the
        /// comments on EquipmentModId.HeavyDraw/ShieldCheck. The same set exists in EquipmentModHookTests.
        /// </summary>
        private static readonly HashSet<ClassAbilityId> PendingHandlers = new();

        /// <summary>
        /// EquipmentModIds that are reserved but deliberately carry NO registry row - the mod was retired
        /// pre-launch with no gear to migrate rather than repurposed. See the retirement comments on
        /// EquipmentModId.HeavyDraw and EquipmentModId.ShieldCheck.
        /// </summary>
        private static readonly HashSet<EquipmentModId> RetiredIds = new()
        {
            EquipmentModId.HeavyDraw,
            EquipmentModId.ShieldCheck,
        };

        [TestMethod]
        public void Registry_HasOneRowPerCatalogEntry()
        {
            var enumValues = Enum.GetValues(typeof(EquipmentModId)).Cast<EquipmentModId>().ToList();

            Assert.AreEqual(49, enumValues.Count, "the enum carries 49 members (47 registered mods plus 2 retired, reserved-forever ids: HeavyDraw, ShieldCheck)");
            Assert.AreEqual(47, EquipmentModRegistry.AllMods.Count, "every non-retired EquipmentModId needs exactly one registry row");

            foreach (var id in enumValues)
            {
                if (RetiredIds.Contains(id))
                {
                    Assert.IsFalse(EquipmentModRegistry.TryGet(id, out _), $"{id}: retired id must not have a registry row");
                    continue;
                }

                Assert.IsTrue(EquipmentModRegistry.TryGet(id, out var definition), $"{id}: missing registry row");
                Assert.AreEqual(id, definition.Id);
                Assert.AreSame(definition, EquipmentModRegistry.Get(id));
            }
        }

        [TestMethod]
        public void Registry_IdsAndPropertiesAreUnique()
        {
            var ids = EquipmentModRegistry.AllMods.Select(m => m.Id).ToList();
            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "duplicate EquipmentModId in the registry");

            var properties = EquipmentModRegistry.AllMods.Select(m => m.Property).ToList();
            Assert.AreEqual(properties.Count, properties.Distinct().Count(), "duplicate PropertyFloat in the registry");
        }

        [TestMethod]
        public void Registry_EveryPropertyIsInsideTheReservedBand()
        {
            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                var id = (int)mod.Property;

                Assert.IsTrue(id >= EquipmentModRegistry.PropertyBandStart && id <= EquipmentModRegistry.PropertyBandEnd,
                    $"{mod.Id}: PropertyFloat {mod.Property} ({id}) is outside the reserved equipment-mod band " +
                    $"{EquipmentModRegistry.PropertyBandStart}-{EquipmentModRegistry.PropertyBandEnd}");
            }
        }

        [TestMethod]
        public void Registry_EveryRowIsInternallyConsistent()
        {
            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                Assert.IsTrue(mod.MaxMagnitude > 0, $"{mod.Id}: MaxMagnitude must be > 0");
                Assert.IsFalse(string.IsNullOrWhiteSpace(mod.DisplayName), $"{mod.Id}: DisplayName is empty");
                Assert.IsFalse(string.IsNullOrWhiteSpace(mod.DisplayFormat), $"{mod.Id}: DisplayFormat is empty");
                Assert.IsTrue(mod.DisplayScale > 0, $"{mod.Id}: DisplayScale must be > 0");

                // the linked ability must be a real, registered class ability - a mod that names a retired or
                // typo'd id would silently never find its compose site in the later hook phase
                Assert.IsTrue(ClassAbilityRegistry.Abilities.ContainsKey(mod.LinkedAbility)
                        || PendingHandlers.Contains(mod.LinkedAbility),
                    $"{mod.Id}: LinkedAbility {mod.LinkedAbility} is not a registered class ability");

                // format string must actually render (a bad placeholder would throw mid-appraisal)
                var rendered = mod.Format(mod.MaxMagnitude);
                Assert.IsFalse(string.IsNullOrWhiteSpace(rendered), $"{mod.Id}: Format produced nothing");
            }
        }

        [TestMethod]
        public void Registry_LookupByPropertyRoundTrips()
        {
            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                Assert.IsTrue(EquipmentModRegistry.TryGet(mod.Property, out var byProperty));
                Assert.AreEqual(mod.Id, byProperty.Id);
                Assert.AreSame(mod, EquipmentModRegistry.Get(mod.Property));
            }

            // a property outside the band must not resolve to a mod
            Assert.IsFalse(EquipmentModRegistry.TryGet(PropertyFloat.DamageMod, out _));
        }

        [TestMethod]
        public void Registry_RollAlwaysReturnsACatalogEntry()
        {
            var seen = new HashSet<EquipmentModId>();

            for (var i = 0; i < 10000; i++)
            {
                var rolled = EquipmentModRegistry.RollModType();

                Assert.IsTrue(EquipmentModRegistry.TryGet(rolled, out _), $"rolled {rolled}, which has no registry row");
                seen.Add(rolled);
            }

            // uniform over the whole catalog: 10k draws hitting every entry is effectively certain
            Assert.AreEqual(EquipmentModRegistry.AllMods.Count, seen.Count, "uniform roll table did not cover the whole catalog");
        }

        /// <summary>
        /// Regression for the Phase 4 finding EQ4-2: the uniform table summed to 1.00000029, past
        /// ChanceTable's 1e-7 tolerance, so ChanceTable.VerifyTable() logged an ERROR line on EVERY mod roll.
        /// The cause was building the final slot's remainder in FLOAT while the verifier sums in DECIMAL.
        ///
        /// This asserts BOTH halves: the exact criterion VerifyTable applies, and then a real verification
        /// pass through the real ChanceTable code. The second half runs on a FRESH table seeded with the
        /// registry's weights, because ChanceTable verifies itself only on a table's first roll - asserting
        /// against the shared registry table would pass vacuously as soon as any other test rolled first.
        /// </summary>
        [TestMethod]
        public void Registry_ChanceTableVerifiesSilently()
        {
            // VerifyTable's own criterion, reproduced exactly: sum in decimal, tolerance 1e-7.
            var total = 0.0M;

            foreach (var entry in EquipmentModRegistry.ModChanceTable)
                total += (decimal)entry.chance;

            Assert.IsTrue(Math.Abs(1.0M - total) <= 0.0000001M, $"chance table sums to {total}, expected 1.0 within 1e-7");

            // Now drive the real ChanceTable.VerifyTable() and prove it stays silent.
            var appender = new MemoryAppender();
            var hierarchy = (Hierarchy)LogManager.GetRepository(typeof(ChanceTable<EquipmentModId>).Assembly);
            var root = hierarchy.Root;
            var priorLevel = root.Level;
            var priorConfigured = hierarchy.Configured;

            try
            {
                root.AddAppender(appender);
                root.Level = Level.All;
                hierarchy.Configured = true;

                var fresh = new ChanceTable<EquipmentModId>();
                fresh.AddRange(EquipmentModRegistry.ModChanceTable);
                fresh.Roll();

                var errors = appender.GetEvents().Where(e => e.Level >= Level.Error).ToList();

                Assert.AreEqual(0, errors.Count, $"the mod roll table logged {errors.Count} error(s): {string.Join(" | ", errors.Select(e => e.RenderedMessage))}");

                // Control: a deliberately broken table MUST trip the same capture, otherwise the assertion
                // above proves only that the log harness is deaf.
                var broken = new ChanceTable<EquipmentModId> { (EquipmentModId.Deadeye, 0.5f) };
                broken.Roll();

                Assert.IsTrue(appender.GetEvents().Any(e => e.Level >= Level.Error), "log capture saw nothing even for a table that sums to 0.5 - the silence above is not evidence");
            }
            finally
            {
                root.RemoveAppender(appender);
                root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;
            }
        }

        /// <summary>
        /// The EQ4-2 fix must not buy an exact sum by skewing the distribution: mod type is uniform over the
        /// whole catalog in v1 (plan decision 3). Asserted analytically on the weights rather than by
        /// sampling, so it cannot flake.
        /// </summary>
        [TestMethod]
        public void Registry_ChanceTableStaysUniform()
        {
            var table = EquipmentModRegistry.ModChanceTable;

            Assert.AreEqual(EquipmentModRegistry.AllMods.Count, table.Count, "roll table and catalog are different sizes");

            CollectionAssert.AreEqual(
                EquipmentModRegistry.AllMods.Select(m => m.Id).ToList(),
                table.Select(e => e.result).ToList(),
                "roll table entries do not match the catalog one-for-one, in order");

            var expected = 1.0 / EquipmentModRegistry.AllMods.Count;

            foreach (var entry in table)
                Assert.AreEqual(expected, entry.chance, 1e-6, $"{entry.result} carries weight {entry.chance}, which is not uniform");
        }

        [TestMethod]
        public void Registry_RollRespectsExclusions()
        {
            // no duplicate mod types on one item: the roll must never return an already-present type
            var exclude = EquipmentModRegistry.AllMods.Take(EquipmentModRegistry.AllMods.Count - 1).Select(m => m.Id).ToList();
            var onlyRemaining = EquipmentModRegistry.AllMods.Last().Id;

            for (var i = 0; i < 100; i++)
                Assert.AreEqual(onlyRemaining, EquipmentModRegistry.RollModType(exclude));

            // fully excluded catalog = no legal mod to add
            var all = EquipmentModRegistry.AllMods.Select(m => m.Id).ToList();
            Assert.IsNull(EquipmentModRegistry.RollModType(all));
        }

        [TestMethod]
        public void Clamp01_ForcesEveryPotencyIntoRange()
        {
            Assert.AreEqual(0.0, EquipmentModRoller.Clamp01(0.0), 1e-12);
            Assert.AreEqual(1.0, EquipmentModRoller.Clamp01(1.0), 1e-12);
            Assert.AreEqual(0.37, EquipmentModRoller.Clamp01(0.37), 1e-12);

            // negative and overflow potency - the shard-contamination cases (a rollback can resurrect rows
            // written by an older build, so the clamp has to hold at read as well as at write)
            Assert.AreEqual(0.0, EquipmentModRoller.Clamp01(-0.0001), 1e-12);
            Assert.AreEqual(0.0, EquipmentModRoller.Clamp01(-50.0), 1e-12);
            Assert.AreEqual(1.0, EquipmentModRoller.Clamp01(1.0001), 1e-12);
            Assert.AreEqual(1.0, EquipmentModRoller.Clamp01(1000.0), 1e-12);

            Assert.AreEqual(0.0, EquipmentModRoller.Clamp01(double.NegativeInfinity), 1e-12);
            Assert.AreEqual(1.0, EquipmentModRoller.Clamp01(double.PositiveInfinity), 1e-12);
            Assert.AreEqual(0.0, EquipmentModRoller.Clamp01(double.NaN), 1e-12, "NaN must sanitize to 0, not propagate");
        }

        [TestMethod]
        public void RollPotency_StaysInRange()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);
            var floor = EquipmentModRoller.MinPotency(deadeye);

            for (var i = 0; i < 10000; i++)
            {
                var potency = EquipmentModRoller.RollPotency(deadeye);

                Assert.IsTrue(potency >= floor - 1e-12 && potency <= 1.0, $"rolled potency {potency} out of [{floor}, 1]");
            }
        }

        [TestMethod]
        public void PotencyScale_DefaultsToOne()
        {
            Assert.AreEqual(1.0, PropertyManager.GetDouble("equipment_mod_potency_scale").Item, 1e-12);
            Assert.AreEqual(1.0, EquipmentModValue.PotencyScale(), 1e-12);
        }

        [TestMethod]
        public void ModsAreEnabledByDefault()
        {
            Assert.IsTrue(PropertyManager.GetBool("equipment_mods_enabled").Item, "the equipment mod layer ships enabled as standard content");
        }

        [TestMethod]
        public void Resolve_IsPotencyTimesMaxTimesScale()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);
            Assert.AreEqual(0.028, deadeye.MaxMagnitude, 1e-12, "Deadeye's max roll is +2.8% missile damage");

            Assert.AreEqual(0.0, EquipmentModValue.Resolve(deadeye, 0.0), 1e-12);
            Assert.AreEqual(0.028, EquipmentModValue.Resolve(deadeye, 1.0), 1e-12);
            Assert.AreEqual(0.014, EquipmentModValue.Resolve(deadeye, 0.5), 1e-12);

            // an arbitrary interior potency resolves linearly - 20% of max
            Assert.AreEqual(0.0056, EquipmentModValue.Resolve(deadeye, 0.2), 1e-12);

            // clamped at read: contaminated rows can never exceed the registry maximum
            Assert.AreEqual(0.028, EquipmentModValue.Resolve(deadeye, 99.0), 1e-12);
            Assert.AreEqual(0.0, EquipmentModValue.Resolve(deadeye, -99.0), 1e-12);

            // a flat mod resolves in absolute units, not a fraction
            var venom = EquipmentModRegistry.Get(EquipmentModId.Venom);
            Assert.AreEqual(4.1, EquipmentModValue.Resolve(venom, 1.0), 1e-12, "Venom's max roll is +4.1 flat poison");
            Assert.AreEqual(0.82, EquipmentModValue.Resolve(venom, 0.2), 1e-12);

            Assert.AreEqual(0.0, EquipmentModValue.Resolve(null, 1.0), 1e-12);
        }

        [TestMethod]
        public void Resolve_HonorsThePotencyScaleTunable()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            try
            {
                PropertyManager.ModifyDouble("equipment_mod_potency_scale", 0.5);
                Assert.AreEqual(0.014, EquipmentModValue.Resolve(deadeye, 1.0), 1e-12, "half scale halves every magnitude");
                Assert.AreEqual(0.014, EquipmentModValue.MagnitudePerPotency(deadeye), 1e-12);

                PropertyManager.ModifyDouble("equipment_mod_potency_scale", 2.0);
                Assert.AreEqual(0.056, EquipmentModValue.Resolve(deadeye, 1.0), 1e-12);

                PropertyManager.ModifyDouble("equipment_mod_potency_scale", 0.0);
                Assert.AreEqual(0.0, EquipmentModValue.Resolve(deadeye, 1.0), 1e-12, "scale 0 mutes the layer without touching items");
            }
            finally
            {
                PropertyManager.ModifyDouble("equipment_mod_potency_scale", 1.0);
            }
        }

        [TestMethod]
        public void MagnitudePerPotency_LetsAStackExceedASingleMaxRoll()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            Assert.AreEqual(0.028, EquipmentModValue.MagnitudePerPotency(deadeye), 1e-12);
            Assert.AreEqual(0.0, EquipmentModValue.MagnitudePerPotency(null), 1e-12);

            // three max-rolled items of the same type: Deadeye declares no StackCap, so the applied value is
            // 3x a single max roll. Resolve() must NOT be used on a sum - it would clamp the stack to 1x.
            const double potencySum = 3.0;

            Assert.AreEqual(0.084, potencySum * EquipmentModValue.MagnitudePerPotency(deadeye), 1e-12);
            Assert.AreEqual(0.028, EquipmentModValue.Resolve(deadeye, potencySum), 1e-12, "Resolve clamps its argument by design");
        }

        [TestMethod]
        public void ClampedPotencySum_MatchesTheReadSideContract()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);

            // the exact expression Creature.GetEquippedModPotencySum applies over EquippedObjects.Values:
            // per-item Clamp01, then an uncapped sum
            var storedOnItems = new[] { 1.0, 0.5, 5.0, -3.0, double.NaN, 0.25 };

            var potencySum = storedOnItems.Sum(EquipmentModRoller.Clamp01);

            // 1.0 + 0.5 + clamp(5.0) + clamp(-3.0) + clamp(NaN) + 0.25 = 1.0 + 0.5 + 1.0 + 0 + 0 + 0.25
            Assert.AreEqual(2.75, potencySum, 1e-12, "each item clamps to [0, 1] individually; the sum is uncapped");
            Assert.AreEqual(0.077, potencySum * EquipmentModValue.MagnitudePerPotency(deadeye), 1e-12);
        }

        // ---------------- StackCap: the cross-item cap, orthogonal to the per-instance [0, 1] clamp ----------------

        /// <summary>
        /// Mirrors Creature.GetEquippedModPotencySum's exact cap expression, since that method itself needs a
        /// live wielder with EquippedObjects populated (see the class remarks) and cannot be exercised here.
        /// </summary>
        private static double ApplyStackCap(EquipmentModDefinition definition, double sum) =>
            definition.StackCap > 0.0 ? Math.Min(sum, definition.StackCap) : sum;

        [TestMethod]
        public void StackCap_UnsetLeavesTheSumUncapped()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);
            Assert.AreEqual(0.0, deadeye.StackCap, 1e-12, "Deadeye must not declare a cap");

            // many equipped items, well past any plausible cap - the no-cross-item-cap ruling (2026-07-24)
            // still holds for every row that leaves StackCap unset
            const double manyItemsSum = 16.0;

            Assert.AreEqual(manyItemsSum, ApplyStackCap(deadeye, manyItemsSum), 1e-12);
        }

        [TestMethod]
        public void StackCap_ClampsTheSumAtTheDeclaredCeiling()
        {
            var acidProc = EquipmentModRegistry.Get(EquipmentModId.AcidProc);
            Assert.AreEqual(3.0, acidProc.StackCap, 1e-12, "Acid Proc caps at three perfect rolls");

            var capped = ApplyStackCap(acidProc, 10.0);

            Assert.IsTrue(capped <= acidProc.StackCap, "a capped sum must never exceed its declared ceiling");
            Assert.AreEqual(acidProc.StackCap, capped, 1e-12);
        }

        [TestMethod]
        public void StackCap_DoesNotAffectASingleItemBelowTheCap()
        {
            var acidProc = EquipmentModRegistry.Get(EquipmentModId.AcidProc);

            Assert.AreEqual(1.0, ApplyStackCap(acidProc, 1.0), 1e-12, "one max-rolled item sits under the cap and must pass through unchanged");
        }

        [TestMethod]
        public void AcidProc_SixMaxRolledItemsResolveToTheCappedValue()
        {
            var acidProc = EquipmentModRegistry.Get(EquipmentModId.AcidProc);

            // six equipped items, each stored at potency 1.0 - the scenario DESIGN.md's stacking table calls
            // out as the point an uncapped Acid Proc stack would reach ~100%
            const double rawSum = 6.0;

            var cappedSum = ApplyStackCap(acidProc, rawSum);

            Assert.AreEqual(acidProc.StackCap, cappedSum, 1e-12, "six perfect rolls must resolve to the cap, not 6x MaxMagnitude");
            Assert.IsTrue(cappedSum < rawSum, "the cap must actually be binding at six items");

            var appliedValue = cappedSum * EquipmentModValue.MagnitudePerPotency(acidProc);

            // 3.0 x 0.103 = 0.309, i.e. +30.9pp added to the 40% base proc chance -> 70.9%, short of a guarantee
            Assert.AreEqual(0.309, appliedValue, 1e-9);
        }

        [TestMethod]
        public void Format_RendersDisplayUnitsNotHookUnits()
        {
            var deadeye = EquipmentModRegistry.Get(EquipmentModId.Deadeye);
            Assert.AreEqual("+2% missile damage", deadeye.Format(0.02));
            Assert.AreEqual("+0.4% missile damage", deadeye.Format(0.004));

            var venom = EquipmentModRegistry.Get(EquipmentModId.Venom);
            Assert.AreEqual("+2 poison damage per hit", venom.Format(2.0));

            var lingeringFury = EquipmentModRegistry.Get(EquipmentModId.LingeringFury);
            Assert.AreEqual("+3s Frenzy stack window", lingeringFury.Format(3.0));
        }

        [TestMethod]
        public void StandaloneFlagsMatchTheRatifiedTable()
        {
            // the hybrid standalone rule (plan decision 2): pure magnitude mods work at ability rank 0,
            // machinery amplifiers do not. Machinery mods must never claim a core-hook kind.
            var expectedMachinery = new[]
            {
                EquipmentModId.Splitshot,
                EquipmentModId.DoubleVolley,
                EquipmentModId.AcidProc,
                EquipmentModId.Caustic,
                EquipmentModId.Riposte,
                EquipmentModId.FrenziedPace,
                EquipmentModId.LingeringFury,
                EquipmentModId.EchoCast,
                EquipmentModId.ElementalRend,
                EquipmentModId.Resonance,
                EquipmentModId.NetherBloom,
                // 2026-08-17 Berserker/Rogue balance pass: id 18 was standalone Blood Fury (a rank-0 melee
                // ramp) and became machinery Break Armor (a proc-chance amplifier) when Blood Fury retired.
                EquipmentModId.BreakArmor,
                // 2026-08-04 class-catalog reconciliation: all 19 new rows are machinery (see the section
                // comment in EquipmentModRegistry - every linked ability returns 0 or identity at rank 0).
                EquipmentModId.BloodCharge,
                EquipmentModId.Transfusion,
                EquipmentModId.Hemorrhage,
                EquipmentModId.Deepen,
                EquipmentModId.Bloodletting,
                EquipmentModId.Sanguinate,
                EquipmentModId.BloodPrice,
                EquipmentModId.Clotting,
                EquipmentModId.Spellblade,
                EquipmentModId.Harmonics,
                EquipmentModId.Runeblade,
                EquipmentModId.Sundermark,
                EquipmentModId.Surge,
                EquipmentModId.Spellstorm,
                EquipmentModId.Cascade,
                EquipmentModId.DispellingEdge,
                EquipmentModId.Provoke,
                EquipmentModId.Bellow,
                EquipmentModId.ShieldWall,
            };

            var actualMachinery = EquipmentModRegistry.AllMods.Where(m => !m.Standalone).Select(m => m.Id).ToList();

            CollectionAssert.AreEquivalent(expectedMachinery, actualMachinery, "machinery/standalone split diverged from the ratified mod table");

            foreach (var mod in EquipmentModRegistry.AllMods.Where(m => !m.Standalone))
            {
                Assert.AreEqual(EquipmentModHookKind.AbilityMachinery, mod.HookKind,
                    $"{mod.Id}: a machinery mod must read inside its ability handler, never at a core combat site");
            }

            foreach (var mod in EquipmentModRegistry.AllMods.Where(m => m.Standalone))
            {
                Assert.AreNotEqual(EquipmentModHookKind.AbilityMachinery, mod.HookKind,
                    $"{mod.Id}: a standalone mod needs a rank-0-reachable hook kind");
            }
        }

        [TestMethod]
        public void EveryMagnitudeStaysInsideTheRatifiedBudget()
        {
            // power-assessor inverse assessment ceiling (2026-08-04 normalization pass): no fractional mod
            // exceeds the current largest ratified value, and the two absolute-unit mods are the documented
            // exceptions (flat poison, seconds). Nether Bloom is a third documented exception: its
            // "fractional" value is a bloom-jump PROBABILITY, not a damage percent, so it is not comparable
            // to the damage-stream budget this loop enforces.
            foreach (var mod in EquipmentModRegistry.AllMods.Where(m => m.DisplayScale == 100.0 && m.Id != EquipmentModId.NetherBloom))
            {
                Assert.IsTrue(mod.MaxMagnitude <= 0.256 + 1e-12,
                    $"{mod.Id}: max magnitude {mod.MaxMagnitude} exceeds the largest ratified fractional value (Caustic, 0.256)");
            }

            Assert.AreEqual(0.25, EquipmentModRegistry.Get(EquipmentModId.NetherBloom).MaxMagnitude, 1e-12,
                "Nether Bloom's gear term is a bloom-jump probability (task spec), not a damage percent - " +
                "documented exception to the fractional-damage budget");

            // the two magnitudes the earlier inverse assessment explicitly cut - regressions here are silent overbudget
            Assert.AreEqual(0.01, EquipmentModRegistry.Get(EquipmentModId.Thorns).MaxMagnitude, 1e-12,
                "Thorns was cut from 3% to 1% of shield AL (reflect is raw and per-attacker)");
            Assert.AreEqual(0.025, EquipmentModRegistry.Get(EquipmentModId.Splitshot).MaxMagnitude, 1e-12,
                "Splitshot's current ratified magnitude (2026-08-04 normalization)");
        }

        // ---------------- the useless-mod floor (reported case: "Frenzied Pace: +0%") ----------------

        /// <summary>
        /// Regression for the reported "Frenzied Pace: +0%" roll. Every mod must have a real roll floor: 46
        /// of the 49 rows declare none and take EquipmentModRoller.DefaultMinPotency, and the three that do
        /// declare one (Venom, Caustic, Thorns) declare a HIGHER one for quantization reasons. There is no
        /// exemption mechanism by design, so this is an exhaustive check with no allow-list.
        /// </summary>
        [TestMethod]
        public void EveryModHasANonzeroRollFloor()
        {
            // pinned through the sanitizer rather than off the constant, so this proves what is APPLIED
            var catalogDefault = EquipmentModRoller.MinPotency(new EquipmentModDefinition());

            Assert.AreEqual(0.10, catalogDefault, 1e-12,
                "the catalog default floor is 0.10 - dropping it to 0 is exactly the defect this test exists for");
            Assert.AreEqual(EquipmentModRoller.DefaultMinPotency, catalogDefault, 1e-12,
                "the published constant and the floor the sanitizer applies must be the same number");

            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                var floor = EquipmentModRoller.MinPotency(mod);

                Assert.IsTrue(floor >= EquipmentModRoller.DefaultMinPotency - 1e-12 && floor < 1.0,
                    $"{mod.Id}: effective roll floor {floor} is below the catalog default {EquipmentModRoller.DefaultMinPotency}");

                // and the roll itself honors it - the floor is worthless if RollPotency ignores it
                for (var i = 0; i < 200; i++)
                {
                    var potency = EquipmentModRoller.RollPotency(mod);

                    Assert.IsTrue(potency >= floor - 1e-12 && potency <= 1.0,
                        $"{mod.Id}: rolled potency {potency} escaped [{floor}, 1]");
                }
            }
        }

        /// <summary>
        /// The bug was a FORMATTING bug as much as a roll bug, so this asserts on the rendered string, not on
        /// the resolved double: at its own minimum roll, no mod may render the same text a potency of zero
        /// renders. That comparison is the exact "is this line indistinguishable from a dead mod" question,
        /// and it needs no per-mod expected string.
        /// </summary>
        [TestMethod]
        public void NoModRendersAZeroMagnitudeAtItsMinimumRoll()
        {
            // COMPARES THE MAGNITUDE TEXT, NOT THE WHOLE Describe LINE, and that distinction became
            // load-bearing on 2026-08-07 when the intensity bracket was added. A full-line comparison would
            // now pass on the BRACKET differing ("[10%]" vs no bracket) even if both magnitudes still
            // rendered "+0%" - which is the exact failure this test exists to catch. Rendering the magnitude
            // through the same Format/Resolve pair Describe uses keeps the subject unchanged.
            string Magnitude(EquipmentModDefinition mod, double potency) =>
                mod.Format(EquipmentModValue.Resolve(mod, potency));

            foreach (var mod in EquipmentModRegistry.AllMods)
            {
                var floor = EquipmentModRoller.MinPotency(mod);

                var atFloor = Magnitude(mod, floor);
                var atZero = Magnitude(mod, 0.0);

                Assert.AreNotEqual(atZero, atFloor,
                    $"{mod.Id}: the weakest legal roll renders identically to a potency of 0 ({atFloor}) - " +
                    $"raise MinPotency or widen DisplayFormat (MaxMagnitude {mod.MaxMagnitude}, DisplayScale {mod.DisplayScale})");

                // the floor is now the ONLY value that needs checking. There used to be a second stamped
                // value - the fixed low-tier (TigerEye) potency - and it was asserted separately here; with
                // that path removed every potency the flow can produce comes from RollPotency, which never
                // returns below the floor, so the assertion above covers the whole reachable band.
            }
        }

        /// <summary>
        /// The reported case, pinned exactly. Frenzied Pace is the one per-stack mod in the catalog. Its
        /// MaxMagnitude was raised from 0.0015 to 0.003 (2026-08-04 normalization), doubling its display
        /// range in proportion, which is what let the format drop from four decimals to the catalog's usual
        /// two: the floor now prints 0.03% and a perfect roll prints 0.3%, both legible at two decimals.
        /// </summary>
        [TestMethod]
        public void FrenziedPace_MinimumRollRendersMeaningfulDigits()
        {
            var frenziedPace = EquipmentModRegistry.Get(EquipmentModId.FrenziedPace);

            Assert.AreEqual(0.003, frenziedPace.MaxMagnitude, 1e-12);
            Assert.AreEqual(100.0, frenziedPace.DisplayScale, 1e-12);
            Assert.AreEqual(EquipmentModRoller.DefaultMinPotency, EquipmentModRoller.MinPotency(frenziedPace), 1e-12);

            Assert.AreEqual("Frenzied Pace [10%]: +0.03% attack speed per Frenzy stack",
                EquipmentModDisplay.Describe(frenziedPace, EquipmentModRoller.MinPotency(frenziedPace)),
                "the weakest legal Frenzied Pace - this line is what read '+0%' before the original fix");

            Assert.AreEqual("Frenzied Pace [100%]: +0.3% attack speed per Frenzy stack",
                EquipmentModDisplay.Describe(frenziedPace, 1.0), "a perfect roll must not gain spurious trailing zeros");

            // control: the OLD three-decimal format is what turned a low roll into a literal 0. Reproduced
            // generically (independent of any live registry value) to prove the failure mode is reachable at
            // all - without it, "not zero" below could just mean the format was never capable of printing zero.
            Assert.AreEqual("+0%", string.Format(CultureInfo.InvariantCulture, "+{0:0.###}%", 0.0004),
                "control: a resolved value of 0.0004% still rounds to a bare zero at three decimals");

            // a legacy sub-floor row (stamped before the floor existed, potency below the current 0.10
            // minimum) must still render a number under the shipped two-decimal format
            Assert.AreEqual("Frenzied Pace [3%]: +0.01% attack speed per Frenzy stack", EquipmentModDisplay.Describe(frenziedPace, 0.034),
                "a legacy sub-floor row must still render a number");
        }

        // ---------------- 2026-08-04 class-catalog reconciliation (BloodMage, Spellsword, Vanguard) ----------------

        private static readonly EquipmentModId[] reconciliationRowIds =
        {
            EquipmentModId.BloodCharge,
            EquipmentModId.Transfusion,
            EquipmentModId.Hemorrhage,
            EquipmentModId.Deepen,
            EquipmentModId.Bloodletting,
            EquipmentModId.Sanguinate,
            EquipmentModId.BloodPrice,
            EquipmentModId.Clotting,
            EquipmentModId.Spellblade,
            EquipmentModId.Harmonics,
            EquipmentModId.Runeblade,
            EquipmentModId.Sundermark,
            EquipmentModId.Surge,
            EquipmentModId.Spellstorm,
            EquipmentModId.Cascade,
            EquipmentModId.DispellingEdge,
            EquipmentModId.Provoke,
            EquipmentModId.Bellow,
            EquipmentModId.ShieldWall,
        };

        /// <summary>
        /// Asserted as an invariant on the live catalog, not a hand-derived count copied from the design doc:
        /// the reconciliation is exactly 19 rows, and the catalog was 30 (shipped) + 19 = 49 rows before the
        /// 2026-10-02 retirement of HeavyDraw and ShieldCheck dropped the REGISTERED row count to 47 (the
        /// enum still carries 49 members - two are reserved and unregistered, see RetiredIds above).
        /// </summary>
        [TestMethod]
        public void Reconciliation_CatalogIs47RegisteredRowsOf49EnumMembers()
        {
            Assert.AreEqual(19, reconciliationRowIds.Length, "the reconciliation itself is 19 mods");
            Assert.AreEqual(47, EquipmentModRegistry.AllMods.Count, "30 shipped + 19 reconciled - 2 retired (HeavyDraw, ShieldCheck) = 47");
            Assert.AreEqual(49, Enum.GetValues(typeof(EquipmentModId)).Length, "EquipmentModId must carry one member per registry row plus the 2 retired, reserved ids");

            // 8168 (ShieldWall) is the last row, shipped once its prerequisite landed: BlockChance now
            // clamps its Armor Tinkering rider with class_ability_affinity_chance_cap, so the pooled
            // avoidance cap no longer absorbs the mod.
            Assert.IsTrue(Enum.IsDefined(typeof(EquipmentModId), "ShieldWall"), "ShieldWall is allocated now that its prerequisite has landed");
            Assert.IsTrue(EquipmentModRegistry.TryGet(PropertyFloat.GearModShieldWall, out var shieldWall), "8168 must resolve to a registry row");
            Assert.AreEqual(8168, (int)PropertyFloat.GearModShieldWall, "the Shield Wall row is PropertyFloat 8168");
            Assert.AreEqual(EquipmentModId.ShieldWall, shieldWall.Id);
            Assert.AreEqual(49, (int)EquipmentModId.ShieldWall, "appended as id 49, never renumbered");
        }

        /// <summary>Every one of the 19 reconciliation rows is machinery - none has a rank-0 standalone formula.</summary>
        [TestMethod]
        public void Reconciliation_EveryNewRowIsMachinery()
        {
            foreach (var id in reconciliationRowIds)
            {
                var mod = EquipmentModRegistry.Get(id);
                Assert.IsFalse(mod.Standalone, $"{id}: the reconciliation rows are all machinery amplifiers, not standalone");
                Assert.AreEqual(EquipmentModHookKind.AbilityMachinery, mod.HookKind, $"{id}: machinery mods read inside their ability handler");
            }
        }

        /// <summary>
        /// StackCap now covers 14 rows total: the 5 pre-existing proc-chance caps (DoubleVolley, AcidProc,
        /// EchoCast, ElementalRend, NetherBloom), the 8 rows from the 2026-08-04 reconciliation (Spellblade,
        /// Runeblade, Sundermark, Surge, Spellstorm, Cascade, DispellingEdge, ShieldWall), and Break Armor,
        /// added 2026-08-17 when mod 18 was repurposed from Blood Fury into a proc-chance mod. Every declared
        /// cap is 3.0 (three perfect rolls); no other row declares one.
        /// </summary>
        [TestMethod]
        public void Reconciliation_StackCapCoversExactlyFourteenRowsAtThreePointZero()
        {
            var capped = EquipmentModRegistry.AllMods.Where(m => m.StackCap > 0.0).ToList();

            Assert.AreEqual(14, capped.Count, "5 pre-existing + 8 reconciliation + Break Armor = 14 proc-chance caps");

            foreach (var mod in capped)
                Assert.AreEqual(3.0, mod.StackCap, 1e-12, $"{mod.Id}: every declared StackCap is three perfect rolls");

            var expectedCapped = new[]
            {
                EquipmentModId.DoubleVolley,
                EquipmentModId.AcidProc,
                EquipmentModId.EchoCast,
                EquipmentModId.ElementalRend,
                EquipmentModId.NetherBloom,
                EquipmentModId.Spellblade,
                EquipmentModId.Runeblade,
                EquipmentModId.Sundermark,
                EquipmentModId.Surge,
                EquipmentModId.Spellstorm,
                EquipmentModId.Cascade,
                EquipmentModId.DispellingEdge,
                EquipmentModId.ShieldWall,
                EquipmentModId.BreakArmor,
            };

            CollectionAssert.AreEquivalent(expectedCapped, capped.Select(m => m.Id).ToList(), "the capped-row set diverged from the ratified list");
        }

        /// <summary>
        /// The naming-collision guard (DESIGN.md section 2.2/2.3): EquipmentModId.Harmonics is the Spellsword
        /// per-stack magic-damage mod and links to ClassAbilityId.Resonance (the Spellsword ability), while
        /// the PRE-EXISTING EquipmentModId.Resonance is the unrelated Archmage Spell AOE mod and links to
        /// ClassAbilityId.SpellAoe. Two different EquipmentModId members intentionally point at two
        /// different-but-similarly-named things; this pins that neither was mixed up.
        /// </summary>
        [TestMethod]
        public void Reconciliation_HarmonicsAndResonanceDoNotCollide()
        {
            var harmonics = EquipmentModRegistry.Get(EquipmentModId.Harmonics);
            var resonance = EquipmentModRegistry.Get(EquipmentModId.Resonance);

            Assert.AreEqual(ClassAbilityId.Resonance, harmonics.LinkedAbility, "Harmonics links to the Spellsword Resonance ability");
            Assert.AreEqual(ClassAbilityId.SpellAoe, resonance.LinkedAbility, "the pre-existing Resonance mod links to Archmage Spell AOE, unchanged");
            Assert.AreNotEqual(harmonics.LinkedAbility, resonance.LinkedAbility, "the two mods must not resolve to the same class ability");
            Assert.AreNotEqual(harmonics.Property, resonance.Property, "the two mods must carry distinct PropertyFloat storage");
        }
    }
}
