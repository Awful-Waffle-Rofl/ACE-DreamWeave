using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// In-memory item construction shared by the two weapon-mod scenario suites. Deliberately duplicated from
    /// WeaponModTests.MakeWeapon rather than shared with it: that file is the unit-level suite and is reviewed
    /// separately, and a helper shared across both would couple their guid ranges.
    /// </summary>
    internal static class WeaponModTestKit
    {
        private static uint nextGuid = 0x7D000000;   // static range, clear of GuidManager AND of WeaponModTests
        private static uint nextWcid = 993000;

        /// <summary>
        /// A bare item from an in-memory weenie: no database, no dat files. Every read and write the weapon-mod
        /// flow performs goes through the generic GetProperty/SetProperty surface, so the concrete WorldObject
        /// subclass is irrelevant - what matters is that ItemType, CombatUse and workmanship are settable.
        /// </summary>
        public static WorldObject MakeWeapon(ItemType itemType = ItemType.MeleeWeapon, CombatUse? combatUse = CombatUse.Melee,
            Dictionary<PropertyInt, int> ints = null, Dictionary<PropertyFloat, double> floats = null,
            int? workmanship = 10, Dictionary<PropertyString, string> strings = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Weapon" } },
            };

            if (workmanship != null)
                weenie.PropertiesInt[PropertyInt.ItemWorkmanship] = workmanship.Value;

            if (combatUse != null)
                weenie.PropertiesInt[PropertyInt.CombatUse] = (int)combatUse.Value;

            if (ints != null)
            {
                foreach (var kvp in ints)
                    weenie.PropertiesInt[kvp.Key] = kvp.Value;
            }

            if (floats != null)
            {
                weenie.PropertiesFloat = new Dictionary<PropertyFloat, double>();

                foreach (var kvp in floats)
                    weenie.PropertiesFloat[kvp.Key] = kvp.Value;
            }

            if (strings != null)
            {
                foreach (var kvp in strings)
                    weenie.PropertiesString[kvp.Key] = kvp.Value;
            }

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A salvage bag: the ItemType / MaterialType / Structure triple the material check reads.</summary>
        public static WorldObject MakeBag(MaterialType material, int structure = 100, int? maxStructure = 100,
            ItemType itemType = ItemType.TinkeringMaterial)
        {
            var bag = MakeWeapon(itemType, null, workmanship: null);

            bag.SetProperty(PropertyInt.MaterialType, (int)material);
            bag.SetProperty(PropertyInt.Structure, structure);

            if (maxStructure != null)
                bag.SetProperty(PropertyInt.MaxStructure, maxStructure.Value);

            return bag;
        }

        /// <summary>The layer 1 composition a hand-tinkered weapon of each class starts from. Deliberately MIXED.</summary>
        public static List<MaterialType> HandTinkeredComposition(WeaponClass weaponClass)
        {
            switch (weaponClass)
            {
                case WeaponClass.Melee:
                    return new List<MaterialType>
                    {
                        MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron,
                        MaterialType.Granite, MaterialType.Granite,
                        MaterialType.Brass, MaterialType.Brass,
                        MaterialType.Velvet, MaterialType.Velvet,
                    };

                case WeaponClass.Missile:
                    return new List<MaterialType>
                    {
                        MaterialType.Mahogany, MaterialType.Mahogany, MaterialType.Mahogany, MaterialType.Mahogany,
                        MaterialType.Granite, MaterialType.Granite, MaterialType.Granite,
                        MaterialType.Brass, MaterialType.Brass, MaterialType.Brass,
                    };

                default:
                    return new List<MaterialType>
                    {
                        MaterialType.GreenGarnet, MaterialType.GreenGarnet, MaterialType.GreenGarnet,
                        MaterialType.Opal, MaterialType.Opal, MaterialType.Opal,
                        MaterialType.Brass, MaterialType.Brass,
                        MaterialType.Velvet, MaterialType.Velvet,
                    };
            }
        }

        public static (ItemType Type, CombatUse Use) Shape(WeaponClass weaponClass)
        {
            switch (weaponClass)
            {
                case WeaponClass.Missile: return (ItemType.MissileWeapon, CombatUse.Missile);
                case WeaponClass.Caster: return (ItemType.Caster, CombatUse.Melee);
                default: return (ItemType.MeleeWeapon, CombatUse.Melee);
            }
        }

        /// <summary>A pristine, untinkered weapon of the given class carrying plausible loot-generated natives.</summary>
        public static WorldObject MakeUntinkered(WeaponClass weaponClass, int? imbuedEffect = null, int workmanship = 10)
        {
            var shape = Shape(weaponClass);

            var ints = new Dictionary<PropertyInt, int>();
            var floats = new Dictionary<PropertyFloat, double>();

            if (imbuedEffect != null)
                ints[PropertyInt.ImbuedEffect] = imbuedEffect.Value;

            switch (weaponClass)
            {
                case WeaponClass.Melee:
                    ints[PropertyInt.Damage] = 15;
                    floats[PropertyFloat.DamageVariance] = 0.45;
                    floats[PropertyFloat.WeaponDefense] = 1.03;
                    floats[PropertyFloat.WeaponOffense] = 1.07;
                    break;

                case WeaponClass.Missile:
                    floats[PropertyFloat.DamageMod] = 1.2;
                    floats[PropertyFloat.DamageVariance] = 0.45;
                    floats[PropertyFloat.WeaponDefense] = 1.03;
                    floats[PropertyFloat.MaximumVelocity] = 24.0;
                    break;

                default:
                    floats[PropertyFloat.ElementalDamageMod] = 1.2;
                    floats[PropertyFloat.ManaConversionMod] = 0.05;
                    floats[PropertyFloat.WeaponDefense] = 1.03;
                    floats[PropertyFloat.WeaponOffense] = 1.07;
                    break;
            }

            return MakeWeapon(shape.Type, shape.Use, ints, floats, workmanship);
        }

        /// <summary>
        /// A weapon in the state a SWAP legally accepts: hand-tinkered to a full ten, retail log consistent with
        /// NumTimesTinkered, and NOT yet managed by this system.
        /// </summary>
        public static WorldObject MakeHandTinkered(WeaponClass weaponClass, int? imbuedEffect = null, int workmanship = 10)
        {
            var weapon = MakeUntinkered(weaponClass, imbuedEffect, workmanship);
            var composition = HandTinkeredComposition(weaponClass);

            var reserved = WeaponModTinkerSet.ReservedImbueSlots(imbuedEffect ?? 0);

            if (reserved > 0)
                composition = composition.Take(WeaponModRegistry.TotalSlots - reserved).ToList();

            WeaponModTinkerSet.ApplyTinkers(weapon, composition);

            weapon.SetProperty(PropertyString.TinkerLog, WeaponModTinkerSet.SerializeLog(composition));
            weapon.SetProperty(PropertyInt.NumTimesTinkered, composition.Count);

            return weapon;
        }

        /// <summary>
        /// The refusal the production path would reach for this source/target pair. It CALLS the production
        /// wiring - WeaponModManager.ResolveRefusal(action, target), the same overload VerifyUseRequirements
        /// calls - rather than re-assembling the rule table's seven arguments here.
        ///
        /// TEST DEFECT, fixed 2026-07-30: this helper used to build those arguments itself and passed
        /// ReadReservedImbueSlots (imbue popcount only) where production passes ReadReservedSlots (imbues PLUS
        /// the log entries this system cannot reverse). Every refusal test therefore agreed with the helper and
        /// not with the server, and a ten-Oak weapon read as fully available here while production refused it.
        /// A helper that re-implements production can silently drift from it; one that calls production cannot.
        /// </summary>
        public static WeaponModManager.WeaponModRefusal Refusal(WorldObject source, WorldObject target) =>
            WeaponModManager.ResolveRefusal(WeaponModManager.ResolveAction(source), target);

        /// <summary>
        /// Runs ApplySwap and holds it to its OWN contract rather than assuming it always succeeds. ApplySwap has
        /// exactly two legitimate null paths, and this asserts both directions of that:
        ///
        ///   - the weapon already holds the full trio, where its defence-in-depth guard on the permanent
        ///     three-special bound refuses outright rather than replacing one of the three;
        ///   - the weapon has nothing this system owns to remove.
        ///
        /// A null in any other state is a failure, and so is a NON-null in either of those states - the second
        /// direction is the one that catches the guard being deleted as redundant. Returns the lines, or null when
        /// a guard legitimately fired.
        /// </summary>
        public static List<string> Swap(WorldObject weapon, WeaponClass weaponClass, double workmanship, string context)
        {
            var specials = WeaponModTinkerSet.SpecialCount(weapon);
            var atCap = specials >= WeaponModRegistry.MaxSpecials;
            var removable = WeaponModTinkerSet.ReadComposition(weapon).Count + specials;

            // read BEFORE the mutation: the bag is consumed on CanApply's word alone, so the two must agree
            // about the state the swap was entered in
            var canApply = WeaponModManager.CanApply(weapon, weaponClass, WeaponModManager.WeaponModAction.Swap);

            var lines = WeaponModManager.ApplySwap(weapon, weaponClass, workmanship);

            Assert.AreEqual(canApply, lines != null,
                $"{context}: CanApply said {canApply} but ApplySwap {(lines == null ? "refused" : "ran")} - the bag is consumed before the apply on CanApply's word alone");

            if (lines == null)
            {
                Assert.IsTrue(atCap || removable == 0,
                    $"{context}: ApplySwap produced no result on a weapon holding {specials} specials with {removable} removable slots - its only legitimate refusals are the three-special guard and an empty weapon");

                return null;
            }

            Assert.IsFalse(atCap,
                $"{context}: ApplySwap ran on a weapon already holding {WeaponModRegistry.MaxSpecials} specials - the defence-in-depth guard on the permanent three-special bound did not fire, so a player can reroll one special of a chosen trio");

            return lines;
        }

        /// <summary>Sets a tunable and returns its prior value, for restoration in a finally.</summary>
        public static double SwapTunable(string key, double value)
        {
            var prior = PropertyManager.GetDouble(key).Item;

            PropertyManager.ModifyDouble(key, value);

            return prior;
        }
    }

    /// <summary>
    /// The invariant harness. Constructed against a weapon in its STARTING state so it can tell a pre-existing
    /// loot value apart from one this system left behind, then called after EVERY mutation in every sequence.
    ///
    /// Every assert names the invariant it protects, because a scenario failure 30 operations into a fuzz run is
    /// only actionable if the message says what broke rather than which number differed.
    /// </summary>
    internal sealed class WeaponModProbe
    {
        private readonly int? imbuedEffect;
        private readonly int? slayerCreatureType;
        private readonly double? slayerDamageBonus;

        private readonly HashSet<PropertyInt> seededNativeInts = new HashSet<PropertyInt>();
        private readonly HashSet<PropertyFloat> seededNativeFloats = new HashSet<PropertyFloat>();

        public WeaponModProbe(WorldObject weapon)
        {
            imbuedEffect = weapon.GetProperty(PropertyInt.ImbuedEffect);
            slayerCreatureType = weapon.GetProperty(PropertyInt.SlayerCreatureType);
            slayerDamageBonus = weapon.GetProperty(PropertyFloat.SlayerDamageBonus);

            foreach (var mod in WeaponModRegistry.AllMods)
            {
                if (mod.NativeInt != null && weapon.GetProperty(mod.NativeInt.Value) != null)
                    seededNativeInts.Add(mod.NativeInt.Value);

                if (mod.NativeFloat != null && weapon.GetProperty(mod.NativeFloat.Value) != null)
                    seededNativeFloats.Add(mod.NativeFloat.Value);
            }
        }

        public void AssertInvariants(WorldObject weapon, WeaponClass weaponClass, string context)
        {
            // TEST DEFECT, fixed 2026-07-30: this read ReadReservedImbueSlots, which is the imbue popcount ALONE.
            // The budget below is 10 = reserved + specials + tinkers where "tinkers" counts only what this system
            // applied, so the reserved term has to cover the unreversible log entries too - otherwise the sum
            // comes up short by exactly the number of foreign entries on any weapon carrying them (a five-Oak
            // weapon sums to 5), and the harness reports a false failure rather than checking the invariant.
            // ReadReservedSlots is also what production budgets against (WeaponModManager.cs ApplyReroll).
            var reserved = WeaponModTinkerSet.ReadReservedSlots(weapon);
            var specials = WeaponModTinkerSet.ReadSpecials(weapon);

            // an unmanaged (hand-tinkered) weapon has no WeaponModTinkerCount yet, so fall back to its log
            var recordedTinkers = weapon.GetProperty(PropertyInt.WeaponModTinkerCount);
            var tinkerCount = recordedTinkers ?? WeaponModTinkerSet.ReadComposition(weapon).Count;

            // the log entries this system does not own: they are carried forward verbatim into both logs and are
            // counted by "reserved", never by WeaponModTinkerCount
            var unaccounted = WeaponModTinkerSet.ReadUnaccountedEntries(weapon).Count;

            // ---- the slot model: 10 = reserved + specials + tinkers ----
            Assert.AreEqual(WeaponModRegistry.TotalSlots, reserved + specials.Count + tinkerCount,
                $"{context}: SLOT MODEL - reserved {reserved} + specials {specials.Count} + tinkers {tinkerCount} must be exactly {WeaponModRegistry.TotalSlots}");

            // ---- NumTimesTinkered is pinned at 10, never 11 ----
            var numTimesTinkered = weapon.GetProperty(PropertyInt.NumTimesTinkered) ?? -1;

            Assert.AreEqual(WeaponModRegistry.TotalSlots, numTimesTinkered,
                $"{context}: TINKER CAP - NumTimesTinkered is {numTimesTinkered}; TinkeringDifficulty is an unguarded 10-element list indexed by it, so anything but exactly 10 is a latent crash or a reopened retail cap");

            // ---- both logs parse, neither grows past ten ----
            Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.TinkerLog), out var retailLog),
                $"{context}: LOG FORMAT - TinkerLog does not parse: '{weapon.GetProperty(PropertyString.TinkerLog)}'");

            Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.WeaponModTinkerLog), out var ownLog),
                $"{context}: LOG FORMAT - WeaponModTinkerLog does not parse: '{weapon.GetProperty(PropertyString.WeaponModTinkerLog)}'");

            Assert.IsTrue(retailLog.Count <= WeaponModRegistry.TotalSlots,
                $"{context}: LOG REPLACED NOT APPENDED - TinkerLog holds {retailLog.Count} entries, above the ten-slot budget");

            Assert.IsTrue(ownLog.Count <= WeaponModRegistry.TotalSlots,
                $"{context}: LOG REPLACED NOT APPENDED - WeaponModTinkerLog holds {ownLog.Count} entries, above the ten-slot budget");

            if (recordedTinkers != null)
            {
                // TEST DEFECT, fixed alongside the accessor above: WeaponModTinkerCount counts ONLY the entries
                // this system applied and can reverse, while the log also carries the unaccounted entries
                // forward verbatim, so the two are equal only on a weapon that has none. The relation that
                // actually holds - and that pins the carry-forward - is log == unaccounted + our own count.
                Assert.AreEqual(unaccounted + recordedTinkers.Value, ownLog.Count,
                    $"{context}: LOG AGREES WITH COUNT - WeaponModTinkerLog holds {ownLog.Count} entries against {unaccounted} carried-forward entries plus a WeaponModTinkerCount of {recordedTinkers.Value}");
            }

            // ---- the permanent three-special bound, and no weapon holds the same special twice ----
            Assert.IsTrue(specials.Count <= WeaponModRegistry.MaxSpecials,
                $"{context}: SPECIAL BOUND - {specials.Count} specials held, above the permanent per-weapon bound of {WeaponModRegistry.MaxSpecials}");

            Assert.AreEqual(specials.Count, specials.Select(s => s.Definition.Id).Distinct().Count(),
                $"{context}: SPECIAL DISTINCTNESS - the same special is held twice");

            // ---- no dead rows: the two LIVE record bands together hold EXACTLY one row per held special ----
            //
            // Both bands, not just Tier A's: a Tier B record is written and cleared by the same ApplySpecial /
            // ReverseSpecial pair, so scanning only 8130-8135 would let a zeroed Tier B row through unseen. The
            // RETIRED band 8136-8140 is deliberately NOT scanned - a stale record there is the accepted cost
            // recorded in WeaponModPoolCutTests, not a dead row this system wrote.
            //
            // The phase-2 reserved band 8148-8149 IS scanned, and must always be empty: nothing writes Sunder
            // or Rampage yet, so any row there means something is writing an id it does not own.
            var bandRows = 0;

            for (var id = WeaponModRegistry.PropertyBandStart; id <= WeaponModRegistry.PropertyBandEnd; id++)
            {
                if (weapon.GetProperty((PropertyFloat)id) != null)
                    bandRows++;
            }

            for (var id = WeaponModRegistry.TierBPropertyBandStart; id <= WeaponModRegistry.TierBPropertyBandEnd; id++)
            {
                if (weapon.GetProperty((PropertyFloat)id) != null)
                    bandRows++;
            }

            Assert.AreEqual(specials.Count, bandRows,
                $"{context}: NO DEAD ROWS - the reserved PropertyFloat bands {WeaponModRegistry.PropertyBandStart}-{WeaponModRegistry.PropertyBandEnd} and {WeaponModRegistry.TierBPropertyBandStart}-{WeaponModRegistry.TierBPropertyBandEnd} hold {bandRows} rows between them against {specials.Count} held specials; a record must be cleared with RemoveProperty, never zeroed");

            for (var id = WeaponModRegistry.TierBReservedBandStart; id <= WeaponModRegistry.TierBReservedBandEnd; id++)
            {
                Assert.IsNull(weapon.GetProperty((PropertyFloat)id),
                    $"{context}: RESERVED BAND - PropertyFloat {id} holds a row, but 8148-8149 are reserved for the phase-2 Sunder and Rampage and nothing writes them yet");
            }

            foreach (var (definition, magnitude) in specials)
            {
                // NO OPT-OUT. This used to be suspendable via an AllowZeroMagnitudeRecords flag, set only by the
                // magnitude-scale-0 case, because at that scale every non-binary special resolved to exactly 0,
                // still took a slot and still wrote a 0.0 record into the reserved band. That was the defect, not
                // an exception to the rule: a zero-magnitude special is now never applied at all and its slot
                // converts to a tinker, so the rule holds at every scale and the harness enforces it everywhere.
                Assert.IsTrue(magnitude > 0.0,
                    $"{context}: LIVE MAGNITUDE - {definition.Id} occupies a slot with a recorded magnitude of {magnitude}, which is a slot spent on nothing");

                Assert.IsTrue(definition.AppliesTo(weaponClass),
                    $"{context}: CLASS POOL - {definition.Id} is held on a {weaponClass} weapon but is not in that class's pool");
            }

            // ---- imbues and slayer are untouched, byte for byte ----
            Assert.AreEqual(imbuedEffect, weapon.GetProperty(PropertyInt.ImbuedEffect),
                $"{context}: IMBUES PRESERVED - ImbuedEffect moved");

            Assert.AreEqual(slayerCreatureType, weapon.GetProperty(PropertyInt.SlayerCreatureType),
                $"{context}: SLAYER PRESERVED - SlayerCreatureType moved");

            Assert.AreEqual(slayerDamageBonus, weapon.GetProperty(PropertyFloat.SlayerDamageBonus),
                $"{context}: SLAYER PRESERVED - SlayerDamageBonus moved");

            // ---- an unheld special leaves no native residue ----
            var held = new HashSet<WeaponModId>(specials.Select(s => s.Definition.Id));

            foreach (var mod in WeaponModRegistry.AllMods)
            {
                if (held.Contains(mod.Id))
                    continue;

                if (mod.NativeInt != null && !seededNativeInts.Contains(mod.NativeInt.Value))
                {
                    var value = weapon.GetProperty(mod.NativeInt.Value);

                    Assert.IsTrue(value == null || Math.Abs(value.Value - mod.NativeDefault) <= WeaponModRegistry.Epsilon,
                        $"{context}: NO NATIVE RESIDUE - {mod.Id} is not held but {mod.NativeInt.Value} reads {value} on a weapon that never had it");
                }

                if (mod.NativeFloat != null && !seededNativeFloats.Contains(mod.NativeFloat.Value))
                {
                    var value = weapon.GetProperty(mod.NativeFloat.Value);

                    Assert.IsTrue(value == null || Math.Abs(value.Value - mod.NativeDefault) <= WeaponModRegistry.Epsilon,
                        $"{context}: NO NATIVE RESIDUE - {mod.Id} is not held but {mod.NativeFloat.Value} reads {value} on a weapon that never had it");
                }
            }
        }
    }

    /// <summary>
    /// Sequence, drift and statistical coverage for the weapon-mod system: long runs of ApplyReroll / ApplySwap
    /// with the full invariant harness asserted after EVERY mutation.
    ///
    /// The unit-level rules live in WeaponModTests; the per-operation interactions (imbues, retail tinkering,
    /// quantities, class pools, the refusal matrix, tunable extremes) live in WeaponModInteractionTests. What is
    /// here is specifically the accumulation class of bug: a value that ratchets, a log that grows, a row that is
    /// zeroed instead of removed, a slot that is handed out twice.
    /// </summary>
    [TestClass]
    public class WeaponModSequenceTests
    {
        private static readonly WeaponClass[] AllClasses = { WeaponClass.Melee, WeaponClass.Missile, WeaponClass.Caster };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ================= A. sequences and orders =================

        [TestMethod]
        public void Sequence_FiftyConsecutiveRerollsHoldEveryInvariant()
        {
            foreach (var weaponClass in AllClasses)
            {
                var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);
                var probe = new WeaponModProbe(weapon);

                for (var i = 1; i <= 50; i++)
                {
                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0),
                        $"{weaponClass} reroll {i}: produced no result");

                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} reroll {i}");
                }
            }
        }

        [TestMethod]
        public void Sequence_SwapsFillToThreeSpecialsAndThenTheGateRefuses()
        {
            var bag = WeaponModTestKit.MakeBag(MaterialType.Amethyst);

            foreach (var weaponClass in AllClasses)
            {
                var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);
                var probe = new WeaponModProbe(weapon);

                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(bag, weapon),
                    $"{weaponClass}: a hand-tinkered ten-slot weapon must accept a swap");

                var uses = 0;

                while (WeaponModTinkerSet.SpecialCount(weapon) < WeaponModRegistry.MaxSpecials)
                {
                    Assert.IsTrue(++uses < 500, $"{weaponClass}: {uses} swaps without reaching three specials - the swap has stopped granting them");

                    Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, weaponClass, 10.0), $"{weaponClass} swap {uses}: produced no result");

                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} swap {uses}");
                }

                // three specials is a PERMANENT bound: the swap is refused outright rather than replacing one
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.SwapAtSpecialCap, WeaponModTestKit.Refusal(bag, weapon),
                    $"{weaponClass}: a weapon at three specials must refuse further swaps");

                // ... while the reroll stays available, which is what makes it the path back from a bad trio
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                    WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Tourmaline), weapon),
                    $"{weaponClass}: the reroll must remain available at three specials");
            }
        }

        [TestMethod]
        public void Sequence_ThirtyAlternationsOfRerollAndSwap()
        {
            foreach (var weaponClass in AllClasses)
            {
                var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);
                var probe = new WeaponModProbe(weapon);

                for (var i = 1; i <= 30; i++)
                {
                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} alternation {i}: reroll produced no result");
                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} alternation {i} after reroll");

                    // the swap legitimately refuses when the reroll just handed the weapon a full trio
                    WeaponModTestKit.Swap(weapon, weaponClass, 10.0, $"{weaponClass} alternation {i}");
                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} alternation {i} after swap");
                }
            }
        }

        /// <summary>
        /// Every sequence over {reroll, swap} of length 1 to 4 - 30 of them - each fully asserted after every
        /// step. Exhaustive rather than sampled, because the interesting failures are order-dependent: a swap
        /// that follows a reroll reads a managed weapon's own log, a swap that follows a swap reads one this
        /// system wrote twice.
        /// </summary>
        [TestMethod]
        public void Sequence_EveryOrderOfLengthOneToFourHoldsEveryInvariant()
        {
            var executed = 0;

            for (var length = 1; length <= 4; length++)
            {
                for (var mask = 0; mask < (1 << length); mask++)
                {
                    var label = string.Empty;

                    var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);
                    var probe = new WeaponModProbe(weapon);

                    probe.AssertInvariants(weapon, WeaponClass.Melee, "hand-tinkered seed state");

                    for (var step = 0; step < length; step++)
                    {
                        var isSwap = ((mask >> step) & 1) == 1;

                        label += isSwap ? "S" : "R";

                        if (isSwap)
                        {
                            // Swap() asserts the swap's own contract: it refuses only at the three-special bound
                            WeaponModTestKit.Swap(weapon, WeaponClass.Melee, 10.0, $"sequence {label} step {step + 1}");
                        }
                        else
                        {
                            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0),
                                $"sequence {label}: step {step + 1} produced no result");
                        }

                        probe.AssertInvariants(weapon, WeaponClass.Melee, $"sequence {label} after step {step + 1}");
                    }

                    executed++;
                }
            }

            Assert.AreEqual(30, executed, "every sequence of length 1 to 4 over a two-letter alphabet is 2 + 4 + 8 + 16");
        }

        /// <summary>
        /// The unstructured attack: random operations in random orders on many weapons, invariants after every
        /// single step. This is the shape that catches state a tidy sequence never reaches.
        /// </summary>
        [TestMethod]
        public void Sequence_FuzzTwoHundredWeaponsFortyOperationsEach()
        {
            var random = new Random(20260730);

            for (var w = 0; w < 200; w++)
            {
                var weaponClass = AllClasses[random.Next(AllClasses.Length)];

                // mix pristine, hand-tinkered and imbued starting states
                var imbue = random.Next(4) == 0 ? (int?)(1 << random.Next(15)) : null;

                var weapon = random.Next(2) == 0
                    ? WeaponModTestKit.MakeUntinkered(weaponClass, imbue, 1 + random.Next(10))
                    : WeaponModTestKit.MakeHandTinkered(weaponClass, imbue, 1 + random.Next(10));

                var probe = new WeaponModProbe(weapon);
                var workmanship = weapon.Workmanship ?? 10.0f;

                for (var op = 1; op <= 40; op++)
                {
                    var isSwap = random.Next(2) == 1;

                    // a swap on a weapon with nothing removable, or one already at the three-special bound,
                    // legitimately produces nothing - Swap() asserts exactly that. A reroll never does.
                    var lines = isSwap
                        ? WeaponModTestKit.Swap(weapon, weaponClass, workmanship, $"weapon {w} ({weaponClass}) op {op}")
                        : WeaponModManager.ApplyReroll(weapon, weaponClass, workmanship);

                    if (lines == null)
                    {
                        Assert.IsTrue(isSwap, $"weapon {w} ({weaponClass}) op {op}: a reroll produced no result");

                        continue;
                    }

                    probe.AssertInvariants(weapon, weaponClass, $"weapon {w} ({weaponClass}, imbue {imbue}) op {op} {(isSwap ? "swap" : "reroll")}");
                }
            }
        }

        /// <summary>
        /// The player's actual attack pattern: swap up to a full trio, then reroll to break out of a bad one, and
        /// confirm the weapon is workable afterwards rather than stuck. The reroll must genuinely WIPE the trio -
        /// if it left even one record behind, the weapon would be permanently refused for swaps.
        /// </summary>
        [TestMethod]
        public void Sequence_RerollAfterAFullTrioWipesItAndLeavesTheWeaponWorkable()
        {
            var amethyst = WeaponModTestKit.MakeBag(MaterialType.Amethyst);

            foreach (var weaponClass in AllClasses)
            {
                for (var round = 0; round < 15; round++)
                {
                    var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);
                    var probe = new WeaponModProbe(weapon);

                    var uses = 0;

                    while (WeaponModTinkerSet.SpecialCount(weapon) < WeaponModRegistry.MaxSpecials)
                    {
                        Assert.IsTrue(++uses < 500, $"{weaponClass}: could not reach three specials");
                        WeaponModManager.ApplySwap(weapon, weaponClass, 10.0);
                        probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} round {round} swap {uses}");
                    }

                    var trio = WeaponModTinkerSet.ReadSpecials(weapon).Select(s => s.Definition.Id).ToList();

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} round {round}: reroll produced no result");
                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} round {round} after the reroll");

                    // the trio's RECORDS are gone whatever the new roll produced - every one of the three was
                    // either replaced by a fresh magnitude or removed outright
                    var after = WeaponModTinkerSet.ReadSpecials(weapon).Select(s => s.Definition.Id).ToList();

                    Assert.IsTrue(after.Count <= WeaponModRegistry.MaxSpecials, $"{weaponClass} round {round}: the reroll left more than three specials");

                    foreach (var id in trio.Where(id => !after.Contains(id)))
                    {
                        var definition = WeaponModRegistry.Get(id);

                        Assert.IsNull(weapon.GetProperty(definition.Record),
                            $"{weaponClass} round {round}: {id} was dropped by the reroll but its record survived");
                    }

                    // and the weapon accepts a swap again, so a bad trio is escapable rather than terminal
                    if (after.Count < WeaponModRegistry.MaxSpecials)
                    {
                        Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(amethyst, weapon),
                            $"{weaponClass} round {round}: the weapon is not workable after the reroll");

                        Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, weaponClass, 10.0), $"{weaponClass} round {round}: the post-reroll swap produced no result");
                        probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} round {round} post-reroll swap");
                    }
                }
            }
        }

        // ================= B. drift and accumulation =================

        /// <summary>
        /// The corruption class the whole design exists to prevent. A hundred reroll cycles on a weapon carrying
        /// known loot-generated natives: every value must stay inside the band its own starting value plus at
        /// most ten applications can reach. A ratchet in EITHER direction is a failure - drifting up is a free
        /// permanent buff, drifting down destroys the player's item.
        /// </summary>
        [TestMethod]
        public void Drift_SeededNativesNeverLeaveTheirAchievableBandOverAHundredRerolls()
        {
            const int cycles = 100;

            // (property, start, low, high) - the band a start value plus 0 to 10 applications can occupy
            var melee = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);
            var meleeProbe = new WeaponModProbe(melee);

            var caster = WeaponModTestKit.MakeUntinkered(WeaponClass.Caster);
            var casterProbe = new WeaponModProbe(caster);

            var missile = WeaponModTestKit.MakeUntinkered(WeaponClass.Missile);
            var missileProbe = new WeaponModProbe(missile);

            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                Assert.IsNotNull(WeaponModManager.ApplyReroll(melee, WeaponClass.Melee, 10.0), $"melee cycle {cycle}");
                meleeProbe.AssertInvariants(melee, WeaponClass.Melee, $"melee drift cycle {cycle}");

                AssertBand(melee, PropertyInt.Damage, 15, 25, $"melee cycle {cycle}");
                AssertBand(melee, PropertyFloat.WeaponDefense, 1.03, 1.13, $"melee cycle {cycle}");
                AssertBand(melee, PropertyFloat.WeaponOffense, 1.07, 1.17, $"melee cycle {cycle}");
                AssertBand(melee, PropertyFloat.DamageVariance, 0.45 * Math.Pow(0.8, 10), 0.45, $"melee cycle {cycle}");

                Assert.IsNotNull(WeaponModManager.ApplyReroll(caster, WeaponClass.Caster, 10.0), $"caster cycle {cycle}");
                casterProbe.AssertInvariants(caster, WeaponClass.Caster, $"caster drift cycle {cycle}");

                AssertBand(caster, PropertyFloat.ElementalDamageMod, 1.2, 1.3, $"caster cycle {cycle}");
                AssertBand(caster, PropertyFloat.ManaConversionMod, 0.05, 0.15, $"caster cycle {cycle}");
                AssertBand(caster, PropertyFloat.WeaponDefense, 1.03, 1.13, $"caster cycle {cycle}");
                AssertBand(caster, PropertyFloat.WeaponOffense, 1.07, 1.17, $"caster cycle {cycle}");

                Assert.IsNotNull(WeaponModManager.ApplyReroll(missile, WeaponClass.Missile, 10.0), $"missile cycle {cycle}");
                missileProbe.AssertInvariants(missile, WeaponClass.Missile, $"missile drift cycle {cycle}");

                AssertBand(missile, PropertyFloat.DamageMod, 1.2, 1.6, $"missile cycle {cycle}");
                AssertBand(missile, PropertyFloat.WeaponDefense, 1.03, 1.13, $"missile cycle {cycle}");
                AssertBand(missile, PropertyFloat.DamageVariance, 0.45 * Math.Pow(0.8, 10), 0.45, $"missile cycle {cycle}");

                // the logs describe the CURRENT state only, so their length is bounded whatever the history
                foreach (var weapon in new[] { melee, caster, missile })
                {
                    foreach (var log in new[] { PropertyString.TinkerLog, PropertyString.WeaponModTinkerLog })
                    {
                        var text = weapon.GetProperty(log) ?? string.Empty;

                        Assert.IsTrue(text.Length <= 40,
                            $"cycle {cycle}: {log} has grown to {text.Length} characters ('{text}') - ten two-digit ids and nine commas is 39, so anything longer means the log is being APPENDED to");
                    }
                }
            }
        }

        /// <summary>
        /// The same run against a weapon that starts with NONE of the layer 1 properties. Every one of them must
        /// come back ABSENT whenever the fresh composition holds none of its material - a row left sitting at the
        /// engine default reads identically to the engine but marks a clean weapon as tinkered forever.
        /// </summary>
        [TestMethod]
        public void Drift_AbsentNativesReturnToAbsentWheneverTheCompositionDropsTheirMaterial()
        {
            foreach (var weaponClass in AllClasses)
            {
                var shape = WeaponModTestKit.Shape(weaponClass);
                var weapon = WeaponModTestKit.MakeWeapon(shape.Type, shape.Use);
                var probe = new WeaponModProbe(weapon);

                for (var cycle = 1; cycle <= 100; cycle++)
                {
                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} cycle {cycle}");
                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} absent-native cycle {cycle}");

                    Assert.IsTrue(WeaponModTinkerSet.TryParseLog(weapon.GetProperty(PropertyString.WeaponModTinkerLog), out var entries));

                    var counts = WeaponModTinkerSet.Counts(entries);

                    foreach (var material in WeaponTinkerTable.Pool(weaponClass))
                    {
                        counts.TryGetValue(material.Material, out var n);

                        var value = material.ReadValue(weapon);

                        var expected = material.ApplyValue(null, n);

                        // TEST DEFECT, fixed 2026-07-30: the "applied, therefore a row exists" branch below was
                        // unconditional, which is wrong for a MULTIPLY material on an absent property. Granite is
                        // a bare "DamageVariance *= 0.8" and this weapon starts with no DamageVariance, so it
                        // computes 0.0 * 0.8^n = 0.0, which IS the engine default - and
                        // WeaponTinkerMaterial.Apply deliberately writes no row there, because a row holding the
                        // engine default is the junk row the design forbids (pinned in
                        // WeaponModInteractionTests.Tinkers_GraniteOnAnAbsentDamageVarianceWritesNoJunkRow, and
                        // "Granite is a dead roll on a zero-variance weapon" is faithful to retail on purpose).
                        // The rule is therefore about the RESULT, not about whether the material was drawn: a
                        // result on the engine default means absent, anything else means a row of exactly that
                        // value. Note this is stricter than the old n == 0 branch, which merely tolerated a row
                        // sitting on the default; nothing may write one at all.
                        // n == 0 falls into this branch on its own: ApplyValue(null, 0) is the engine default.
                        if (Math.Abs(expected - material.EngineDefault) <= WeaponModRegistry.Epsilon)
                        {
                            Assert.IsNull(value,
                                $"{weaponClass} cycle {cycle}: {n}x {material.Material} from absent lands on the engine default of {material.EngineDefault}, so there must be NO row - found {value}");
                        }
                        else
                        {
                            Assert.IsNotNull(value, $"{weaponClass} cycle {cycle}: {n}x {material.Material} applied but its property is absent");
                            Assert.AreEqual(expected, value.Value, 1e-9,
                                $"{weaponClass} cycle {cycle}: {n}x {material.Material} from absent must read {expected}, not {value.Value} - a residue from the previous cycle has accumulated");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// CONFIRMED DEFECT (asserts the CORRECT behaviour and therefore fails against the current code).
        ///
        /// WeaponTinkerMaterial.ReverseValue:125-126 removes any result that lands below EngineDefault, on the
        /// premise that a below-default value can only be wreckage from the retail "set 0.01" branch. That
        /// premise is false: ace_world carries 11 weapon weenies with DamageMod below 1.0 (0.1, 0.2, 0.3, 0.5 x6,
        /// 0.6 x2 - the phantom launchers and the newbie launchers) and 26 with WeaponDefense below 1.0.
        ///
        /// Exploit: a missile weapon with DamageMod 0.5. One reroll draws Mahogany and lifts it; the NEXT reroll
        /// reverses to 0.50, sees 0.50 &lt; 1.0, and deletes the row. The engine then reads "?? 1.0", so the
        /// weapon's damage multiplier goes 0.5 to 1.0 permanently - a 100% damage gain for two salvage bags.
        /// </summary>
        [TestMethod]
        public void Drift_ALegitimatelyBelowDefaultDamageModIsNeverBuffedByRerolling()
        {
            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MissileWeapon, CombatUse.Missile, null,
                new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageMod, 0.5 } });

            var probe = new WeaponModProbe(weapon);

            for (var cycle = 1; cycle <= 25; cycle++)
            {
                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Missile, 10.0), $"cycle {cycle}");
                probe.AssertInvariants(weapon, WeaponClass.Missile, $"below-default DamageMod cycle {cycle}");

                var value = weapon.GetProperty(PropertyFloat.DamageMod);

                Assert.IsNotNull(value,
                    $"cycle {cycle}: DamageMod was REMOVED from a weapon whose loot-generated value was 0.5. The engine reads an absent DamageMod as 1.0 (BaseDamageMod.cs:52, WeaponProfile.cs:104), so this doubles the weapon's damage multiplier for the price of a salvage bag. The below-default removal must apply only to Green Garnet and Opal, whose DAT scripts really do have a 'set' branch.");

                Assert.IsTrue(value.Value >= 0.5 - 1e-9 && value.Value <= 0.5 + 10 * 0.04 + 1e-9,
                    $"cycle {cycle}: DamageMod is {value.Value}, outside the achievable band [0.5, 0.9]");
            }
        }

        /// <summary>
        /// CONFIRMED DEFECT, the same root cause on the other property (asserts the CORRECT behaviour and
        /// therefore fails). 23 ace_world weenies carry WeaponDefense 0.8, two carry 0.92 and one carries 0.96 -
        /// none of them wreckage from the retail bug, all of them buffed to 1.0 by a second reroll today.
        /// </summary>
        [TestMethod]
        public void Drift_ALegitimatelyBelowDefaultWeaponDefenseIsNeverBuffedByRerolling()
        {
            var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, null,
                new Dictionary<PropertyFloat, double> { { PropertyFloat.WeaponDefense, 0.8 } });

            var probe = new WeaponModProbe(weapon);

            for (var cycle = 1; cycle <= 25; cycle++)
            {
                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"cycle {cycle}");
                probe.AssertInvariants(weapon, WeaponClass.Melee, $"below-default WeaponDefense cycle {cycle}");

                var value = weapon.GetProperty(PropertyFloat.WeaponDefense);

                Assert.IsNotNull(value,
                    $"cycle {cycle}: WeaponDefense was REMOVED from a weapon whose loot-generated value was 0.8 (lugianaxebig 7327 and 22 siblings). Absent reads as 1.0, so the reroll hands the weapon a permanent +25% defense modifier.");

                Assert.IsTrue(value.Value >= 0.8 - 1e-9 && value.Value <= 0.8 + 10 * 0.01 + 1e-9,
                    $"cycle {cycle}: WeaponDefense is {value.Value}, outside the achievable band [0.8, 0.9]");
            }
        }

        /// <summary>
        /// The same accumulation attack down the OTHER path. A swap reverses and applies ONE material at a time
        /// rather than a whole composition, so its arithmetic is a different code path from the reroll's, and two
        /// hundred of them is where a one-per-operation rounding error would show up.
        /// </summary>
        [TestMethod]
        public void Drift_SwapDrivenSequencesNeverRatchetASeededNative()
        {
            var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);
            var probe = new WeaponModProbe(weapon);

            // the hand-tinkered seed is four Iron, two Granite, two Brass and two Velvet on top of the loot values
            var baseDamage = 15;
            var baseVariance = 0.45;
            var baseDefense = 1.03;
            var baseOffense = 1.07;

            var swaps = 0;

            for (var i = 1; i <= 200; i++)
            {
                if (WeaponModTinkerSet.SpecialCount(weapon) >= WeaponModRegistry.MaxSpecials)
                {
                    // the swap's three-special guard refuses here by design, and a reroll is the only way back
                    // out of a full trio - take it so the loop keeps driving real swaps rather than stalling
                    Assert.IsNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0),
                        $"swap {i}: the three-special guard did not fire on a weapon holding a full trio");

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"escape reroll at {i}");
                }
                else
                {
                    Assert.IsNotNull(WeaponModTestKit.Swap(weapon, WeaponClass.Melee, 10.0, $"swap {i}"), $"swap {i}: produced no result");

                    swaps++;
                }

                probe.AssertInvariants(weapon, WeaponClass.Melee, $"swap-drift {i}");

                AssertBand(weapon, PropertyInt.Damage, baseDamage, baseDamage + 10, $"swap {i}");
                AssertBand(weapon, PropertyFloat.WeaponDefense, baseDefense, baseDefense + 0.10, $"swap {i}");
                AssertBand(weapon, PropertyFloat.WeaponOffense, baseOffense, baseOffense + 0.10, $"swap {i}");
                AssertBand(weapon, PropertyFloat.DamageVariance, baseVariance * Math.Pow(0.8, 10), baseVariance, $"swap {i}");
            }

            // the escape rerolls must stay a small minority, or this has quietly stopped being a swap-driven test
            Assert.IsTrue(swaps >= 100, $"only {swaps} of 200 operations were actual swaps - the swap path is no longer what this test drives");
        }

        /// <summary>
        /// The reason the record holds an APPLIED MAGNITUDE rather than a potency, attacked through the real
        /// reroll path: an operator moving weapon_mod_magnitude_scale between one reroll and the next must never
        /// leave a residue on a native property that already carried a loot-generated value.
        /// </summary>
        [TestMethod]
        public void Drift_SeededSpecialNativesSurviveMagnitudeScaleChangesAcrossRerolls()
        {
            var prior = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            try
            {
                // UPDATED 2026-07-30 with the pool cut. GearMaxHealth 379 was Vigor's native and Vigor was
                // removed, so the third seeded rating moved to GearCrit 372 (Weak Point) - a Tier A native that
                // still exists. The premise is unchanged: every seeded value is a loot-generated number sitting
                // on a property this system also writes to.
                var seeded = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.GearCritDamage, 7 },
                    { PropertyInt.GearDamage, 3 },
                    { PropertyInt.GearCrit, 4 },
                    { PropertyInt.Damage, 15 },
                };

                var weapon = WeaponModTestKit.MakeWeapon(ItemType.MeleeWeapon, CombatUse.Melee, seeded);
                var probe = new WeaponModProbe(weapon);

                var scales = new[] { 1.0, 7.0, 0.3, 3.0, 1.0, 100.0, 0.5 };

                for (var cycle = 0; cycle < 60; cycle++)
                {
                    PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", scales[cycle % scales.Length]);

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"cycle {cycle}");

                    probe.AssertInvariants(weapon, WeaponClass.Melee, $"scale-change cycle {cycle} (scale {scales[cycle % scales.Length]})");

                    foreach (var kvp in seeded)
                    {
                        if (kvp.Key == PropertyInt.Damage)
                            continue;   // layer 1 territory, banded by its own test

                        var definition = WeaponModRegistry.AllMods.FirstOrDefault(m => m.NativeInt == kvp.Key);

                        Assert.IsNotNull(definition, $"{kvp.Key} is no longer a Tier A native - the test's premise has moved");

                        var record = weapon.GetProperty(definition.Record);
                        var expected = kvp.Value + (int)(record ?? 0.0);

                        Assert.AreEqual(expected, weapon.GetProperty(kvp.Key),
                            $"cycle {cycle} at scale {scales[cycle % scales.Length]}: {kvp.Key} must read its loot value {kvp.Value} plus exactly the recorded magnitude {record}, or the property has drifted permanently");
                    }
                }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", prior);
            }
        }

        // ================= G. slayer =================

        [TestMethod]
        public void Slayer_SurvivesFiftyRerollsUnchangedAndCostsNoSlot()
        {
            foreach (var weaponClass in AllClasses)
            {
                var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);

                weapon.SetProperty(PropertyInt.SlayerCreatureType, (int)CreatureType.Olthoi);
                weapon.SetProperty(PropertyFloat.SlayerDamageBonus, 1.75);

                var probe = new WeaponModProbe(weapon);

                for (var i = 1; i <= 50; i++)
                {
                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} reroll {i}");
                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} slayer reroll {i}");

                    // byte-identical, and the full ten slots still went to tinkers and specials
                    Assert.AreEqual((int)CreatureType.Olthoi, weapon.GetProperty(PropertyInt.SlayerCreatureType), $"{weaponClass} reroll {i}: SlayerCreatureType moved");
                    Assert.AreEqual(1.75, weapon.GetProperty(PropertyFloat.SlayerDamageBonus).Value, 1e-12, $"{weaponClass} reroll {i}: SlayerDamageBonus moved");

                    var tinkers = weapon.GetProperty(PropertyInt.WeaponModTinkerCount) ?? -1;

                    Assert.AreEqual(WeaponModRegistry.TotalSlots, tinkers + WeaponModTinkerSet.SpecialCount(weapon),
                        $"{weaponClass} reroll {i}: slayer consumed a slot - it is not part of the ten-slot budget");
                }
            }
        }

        // ================= H. statistical =================

        /// <summary>
        /// The reroll's special-count distribution measured through the REAL application path rather than the
        /// pure odds function: none 65 / one 25 / two 8 / three 2. 4000 samples with a wide tolerance - this is a
        /// regression tripwire against the cumulative bands being read as conditional, not a precision
        /// measurement.
        /// </summary>
        [TestMethod]
        public void Statistics_RerollSpecialCountMatchesTheDesignedDistribution()
        {
            const int samples = 4000;

            var counts = new int[4];

            for (var i = 0; i < samples; i++)
            {
                var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0));

                counts[WeaponModTinkerSet.SpecialCount(weapon)]++;
            }

            Assert.AreEqual(0.65, counts[0] / (double)samples, 0.03, "P(no special) over the real reroll path");
            Assert.AreEqual(0.25, counts[1] / (double)samples, 0.03, "P(exactly one)");
            Assert.AreEqual(0.08, counts[2] / (double)samples, 0.02, "P(exactly two)");
            Assert.AreEqual(0.02, counts[3] / (double)samples, 0.012, "P(exactly three)");
        }

        [TestMethod]
        public void Statistics_SwapAddsASpecialAtTheTunedChance()
        {
            const int samples = 4000;

            var gained = 0;

            for (var i = 0; i < samples; i++)
            {
                var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);

                Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0));

                gained += WeaponModTinkerSet.SpecialCount(weapon);
            }

            var expected = PropertyManager.GetDouble("weapon_mod_swap_special_chance").Item;

            Assert.AreEqual(expected, gained / (double)samples, 0.035,
                $"one swap on a weapon holding no specials adds one with probability weapon_mod_swap_special_chance ({expected})");
        }

        /// <summary>
        /// Design section 8 prices the swap at a mean of about 11.9 uses to reach a full trio from a fresh ten
        /// tinker weapon, and about 12.5 when one imbue is preserved. The tolerance is deliberately wide: this
        /// exists to catch the swap silently becoming a ratchet (a removal that never touches a special, say),
        /// not to pin a number.
        /// </summary>
        [TestMethod]
        public void Statistics_MeanSwapsToAFullTrioMatchesTheDesignedGrind()
        {
            Assert.AreEqual(11.9, MeanSwapsToThreeSpecials(null, 400), 2.0,
                "mean swaps to three specials on a ten-slot weapon (design section 8)");

            // one imbue removes one slot from the removal pool, so a special is likelier to be the casualty
            Assert.AreEqual(12.5, MeanSwapsToThreeSpecials((int)ImbuedEffectType.CriticalStrike, 400), 2.0,
                "mean swaps to three specials with one imbue preserved");
        }

        private static double MeanSwapsToThreeSpecials(int? imbue, int trials)
        {
            var total = 0L;

            for (var trial = 0; trial < trials; trial++)
            {
                var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee, imbue);

                var uses = 0;

                while (WeaponModTinkerSet.SpecialCount(weapon) < WeaponModRegistry.MaxSpecials)
                {
                    Assert.IsTrue(++uses < 1000, $"trial {trial}: {uses} swaps without a trio - the swap has stopped granting specials");

                    Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), $"trial {trial} use {uses}: produced no result");
                }

                total += uses;
            }

            return total / (double)trials;
        }

        // ---------------- helpers ----------------

        private static void AssertBand(WorldObject weapon, PropertyInt property, int low, int high, string context)
        {
            var value = weapon.GetProperty(property);

            Assert.IsNotNull(value, $"{context}: {property} was removed from a weapon that carried a loot-generated value");
            Assert.IsTrue(value.Value >= low && value.Value <= high,
                $"{context}: {property} is {value.Value}, outside the achievable band [{low}, {high}] - a ratchet in either direction is silent item corruption");
        }

        private static void AssertBand(WorldObject weapon, PropertyFloat property, double low, double high, string context)
        {
            var value = weapon.GetProperty(property);

            Assert.IsNotNull(value, $"{context}: {property} was removed from a weapon that carried a loot-generated value");
            Assert.IsTrue(value.Value >= low - 1e-9 && value.Value <= high + 1e-9,
                $"{context}: {property} is {value.Value}, outside the achievable band [{low}, {high}] - a ratchet in either direction is silent item corruption");
        }
    }
}
