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
        /// Runs ApplySwap and holds it to its OWN contract rather than assuming it always succeeds.
        ///
        /// REWORKED TWICE. The 2026-08-06 rework left exactly one legitimate null path (a weapon holding zero
        /// specials, with nothing to reroll). The 2026-08-07 directive removed even that: Amethyst ADDS a
        /// special when none are held, so ApplySwap has NO legitimate null path at all on a valid weapon.
        ///
        /// What this now holds it to, in both directions:
        ///
        ///   - it never refuses, and CanApply never refuses, and the two agree;
        ///   - on a ZERO-special weapon it ADDS - the count lands on exactly one and nothing is reported lost;
        ///   - on any other count it TRADES - the count is unchanged and something is reported lost.
        ///
        /// The count assertions are the important half. "Did not refuse" would still pass if the swap silently
        /// did nothing, and the muted-scale paths in this suite exercise exactly that shape.
        /// </summary>
        public static List<string> Swap(WorldObject weapon, WeaponClass weaponClass, double workmanship, string context)
        {
            var before = WeaponModTinkerSet.SpecialCount(weapon);

            // read BEFORE the mutation: the bag is consumed on CanApply's word alone, so the two must agree
            // about the state the swap was entered in
            var canApply = WeaponModManager.CanApply(weapon, weaponClass, WeaponModManager.WeaponModAction.Swap);

            var lines = WeaponModManager.ApplySwap(weapon, weaponClass, workmanship);

            Assert.AreEqual(canApply, lines != null,
                $"{context}: CanApply said {canApply} but ApplySwap {(lines == null ? "refused" : "ran")} - the bag is consumed before the apply on CanApply's word alone");

            Assert.IsNotNull(lines,
                $"{context}: ApplySwap refused a weapon holding {before} specials - since 2026-08-07 it has no legitimate refusal on a valid weapon");

            var after = WeaponModTinkerSet.SpecialCount(weapon);
            var reportedALoss = lines.Any(l => l.StartsWith("Lost:", StringComparison.Ordinal));

            if (before == 0)
            {
                // the ADD path. At a muted magnitude scale the drawn special resolves to zero and is dropped,
                // so landing back on zero is legitimate - what is never legitimate is reporting a loss when
                // the weapon held nothing to lose.
                Assert.IsTrue(after <= 1,
                    $"{context}: a swap on a zero-special weapon left {after} specials - it may add at most one");

                Assert.IsFalse(reportedALoss,
                    $"{context}: the swap reported a loss on a weapon that held no specials");

                return lines;
            }

            // the TRADE path: one out, one in. A muted scale drops the replacement, so the count may fall by
            // one, but it must never RISE - Amethyst is not a way to accumulate a set.
            Assert.IsTrue(after <= before,
                $"{context}: a swap grew the special count from {before} to {after} - it trades one for one and must never accumulate");

            Assert.IsTrue(reportedALoss,
                $"{context}: the swap removed nothing from a weapon holding {before} specials");

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
            // The budget below is 10 = reserved + tinkers where "tinkers" counts only what this system applied,
            // so the reserved term has to cover the unreversible log entries too - otherwise the sum comes up
            // short by exactly the number of foreign entries on any weapon carrying them (a five-Oak weapon sums
            // to 5), and the harness reports a false failure rather than checking the invariant.
            // ReadReservedSlots is also what production budgets against (WeaponModManager.cs ApplyReroll).
            var reserved = WeaponModTinkerSet.ReadReservedSlots(weapon);
            var specials = WeaponModTinkerSet.ReadSpecials(weapon);

            // an unmanaged (hand-tinkered) weapon has no WeaponModTinkerCount yet, so fall back to its log
            var recordedTinkers = weapon.GetProperty(PropertyInt.WeaponModTinkerCount);
            var tinkerCount = recordedTinkers ?? WeaponModTinkerSet.ReadComposition(weapon).Count;

            // the log entries this system does not own: they are carried forward verbatim into both logs and are
            // counted by "reserved", never by WeaponModTinkerCount
            var unaccounted = WeaponModTinkerSet.ReadUnaccountedEntries(weapon).Count;

            // ---- the slot model: reserved + tinkers never EXCEEDS 10 ----
            //
            // CHANGED 2026-08-06 WITH THE SPECIAL/SLOT DECOUPLING. This used to read
            // "reserved + specials.Count + tinkerCount", because a special consumed a tinker slot. It no longer
            // does, and the sharp part of the invariant is that the special count does not appear here AT ALL:
            // a weapon holding a full set must show the same tinker count as one holding none.
            //
            // RELAXED FROM == TO <= 2026-08-07 WITH THE SPECIAL-ONLY REROLL. The equality only ever held
            // because ApplyReroll FORCED it - it refilled every unreserved slot on every use. Now that the
            // reroll does not manage layer 1 at all, a partially hand-tinkered weapon sits legitimately below
            // ten and stays there, exactly as it would under ordinary retail tinkering. Anything ABOVE ten is
            // still a hard defect; anything below is now normal player state.
            //
            // The equality's real job - catching a silent revert of the decoupling - is now done directly by
            // Reroll_LeavesTheTinkerCompositionByteIdentical, which is a stronger check than a sum that a
            // budget refill could satisfy by coincidence.
            Assert.IsTrue(reserved + tinkerCount <= WeaponModRegistry.TotalSlots,
                $"{context}: SLOT MODEL - reserved {reserved} + tinkers {tinkerCount} exceeds {WeaponModRegistry.TotalSlots} (specials held: {specials.Count}, which must NOT be a term)");

            // ---- NumTimesTinkered never exceeds 10 ----
            var numTimesTinkered = weapon.GetProperty(PropertyInt.NumTimesTinkered) ?? -1;

            Assert.IsTrue(numTimesTinkered <= WeaponModRegistry.TotalSlots,
                $"{context}: TINKER CAP - NumTimesTinkered is {numTimesTinkered}; TinkeringDifficulty is an unguarded 10-element list indexed by it, so anything ABOVE ten is a latent crash or a reopened retail cap. Below ten is legitimate player state since the reroll stopped forcing it (2026-08-07)");

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

            // ---- the permanent special bound, and no weapon holds the same special twice ----
            Assert.IsTrue(specials.Count <= WeaponModRegistry.MaxSpecials,
                $"{context}: SPECIAL BOUND - {specials.Count} specials held, above the permanent per-weapon bound of {WeaponModRegistry.MaxSpecials}");

            Assert.AreEqual(specials.Count, specials.Select(s => s.Definition.Id).Distinct().Count(),
                $"{context}: SPECIAL DISTINCTNESS - the same special is held twice");

            // ---- no dead rows: the four LIVE record bands together hold EXACTLY one row per held special ----
            //
            // All four, not just the original Tier A band: a Tier B record is written and cleared by the same
            // ApplySpecial / ReverseSpecial pair, so scanning only 8130-8134 would let a zeroed v2, v3 or v4
            // Tier B row through unseen. The RETIRED band 8135-8140 is deliberately NOT scanned - a stale
            // record there is the accepted cost recorded in WeaponModPoolCutTests, not a dead row this system
            // wrote. Omitting the v4 band (8027-8035) here would leave a stuck record at those ids invisible
            // to this harness.
            //
            // The phase-2 reserved band 8148-8149 IS scanned, and must always be empty: nothing writes Sunder
            // or Rampage yet, so any row there means something is writing an id it does not own.
            var bandRows = 0;

            for (var id = WeaponModRegistry.PropertyBandStart; id <= WeaponModRegistry.PropertyBandEnd; id++)
            {
                if (weapon.GetProperty((PropertyFloat)id) != null)
                    bandRows++;
            }

            for (var id = WeaponModRegistry.TierBExpansionBandStart; id <= WeaponModRegistry.TierBExpansionBandEnd; id++)
            {
                if (weapon.GetProperty((PropertyFloat)id) != null)
                    bandRows++;
            }

            for (var id = WeaponModRegistry.TierBV4BandStart; id <= WeaponModRegistry.TierBV4BandEnd; id++)
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
                $"{context}: NO DEAD ROWS - the reserved PropertyFloat bands {WeaponModRegistry.PropertyBandStart}-{WeaponModRegistry.PropertyBandEnd}, {WeaponModRegistry.TierBExpansionBandStart}-{WeaponModRegistry.TierBExpansionBandEnd}, {WeaponModRegistry.TierBV4BandStart}-{WeaponModRegistry.TierBV4BandEnd} and {WeaponModRegistry.TierBPropertyBandStart}-{WeaponModRegistry.TierBPropertyBandEnd} hold {bandRows} rows between them against {specials.Count} held specials; a record must be cleared with RemoveProperty, never zeroed");

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

        /// <summary>
        /// THE SHARP INVARIANT OF THE 2026-08-07 SPECIAL-ONLY REROLL, and the direct replacement for the
        /// "reserved + tinkers == TotalSlots" equality that WeaponModProbe.AssertInvariants had to relax to a
        /// "&lt;=" on the same day.
        ///
        /// WHY IT IS STRONGER THAN THE EQUALITY IT REPLACES. A sum can be satisfied by coincidence: an
        /// implementation that reversed the whole composition and refilled the budget would land back on ten
        /// and pass, which is precisely what the old equality could not distinguish. A BYTE COMPARISON of the
        /// composition cannot be satisfied that way - a refill would have to redraw the same seven materials in
        /// the same order, from a four-material pool, sixty times running.
        ///
        /// THE COMPOSITION IS DELIBERATELY NOT FULL. Seven of ten, so a budget refill has three visibly empty
        /// slots to reach for; a ten-tinker weapon would leave a refill nothing to do and this test nothing to
        /// see. It is also mixed rather than uniform, so a redraw that happened to hit the right COUNT would
        /// still have to hit the right split.
        ///
        /// AND THE SPECIALS MUST STILL MOVE. Without the second half this test would pass trivially against an
        /// ApplyReroll that did nothing at all, which is a considerably worse bug than the one it is guarding.
        /// </summary>
        [TestMethod]
        public void Reroll_LeavesTheTinkerCompositionByteIdentical()
        {
            const int cycles = 60;

            // 4 Iron + 3 Velvet: seven of ten, two materials from the melee pool of four, laid down by hand
            var composition = Enumerable.Repeat(MaterialType.Iron, 4)
                .Concat(Enumerable.Repeat(MaterialType.Velvet, 3))
                .ToList();

            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);
            var probe = new WeaponModProbe(weapon);

            WeaponModTinkerSet.ApplyTinkers(weapon, composition);

            var log = WeaponModTinkerSet.SerializeLog(composition);

            weapon.SetProperty(PropertyString.TinkerLog, log);
            weapon.SetProperty(PropertyInt.NumTimesTinkered, composition.Count);

            // every layer 1 native any melee material can reach, snapshotted AFTER the hand tinkering. Read as
            // a set rather than as computed constants so the test does not have to restate the material table's
            // arithmetic - what matters is that these numbers do not move, not what they are.
            var natives = new Dictionary<PropertyFloat, double?>();

            foreach (var material in WeaponTinkerTable.Pool(WeaponClass.Melee).Where(m => m.FloatProperty != null))
                natives[material.FloatProperty.Value] = weapon.GetProperty(material.FloatProperty.Value);

            var damage = weapon.GetProperty(PropertyInt.Damage);

            Assert.AreEqual(7, WeaponModTinkerSet.ReadComposition(weapon).Count, "precondition: seven hand-laid tinkers");

            // NOTE the reserved figure is ZERO here, not three: "reserved" counts slots this system CANNOT
            // work with (imbues and unreversible log entries), and all seven of these are ordinary reversible
            // materials. So the pre-2026-08-07 reroll would have seen ten available slots on this weapon and
            // laid down ten - the three unspent slots below are the visible part of that, and the four Iron
            // and three Velvet being redrawn is the rest.
            Assert.AreEqual(0, WeaponModTinkerSet.ReadReservedSlots(weapon), "precondition: nothing here is unreversible, so nothing is reserved");
            Assert.AreEqual(3, WeaponModRegistry.TotalSlots - WeaponModTinkerSet.ReadComposition(weapon).Count,
                "precondition: three of the ten slots are unspent, so a budget refill would have somewhere visible to put them");

            var signatures = new HashSet<string>();

            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"cycle {cycle}: the reroll produced no result");

                probe.AssertInvariants(weapon, WeaponClass.Melee, $"byte-identical composition cycle {cycle}");

                // ---- the composition, byte for byte ----
                Assert.AreEqual(log, weapon.GetProperty(PropertyString.TinkerLog),
                    $"cycle {cycle}: TinkerLog moved. The reroll must not reverse, redraw, reorder or re-serialize the player's composition");

                Assert.IsNull(weapon.GetProperty(PropertyString.WeaponModTinkerLog),
                    $"cycle {cycle}: WeaponModTinkerLog was written. This weapon's tinkers are the player's, not this system's, and writing this row claims them");

                Assert.AreEqual(7, weapon.GetProperty(PropertyInt.NumTimesTinkered),
                    $"cycle {cycle}: NumTimesTinkered moved off the seven the player actually spent");

                Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                    $"cycle {cycle}: WeaponModTinkerCount was written, which marks the weapon as managed and would suppress the retail integrity gate on it forever");

                // ---- and the EFFECTS of that composition, which is where a reverse-then-refill would show up
                //      even if it happened to rewrite an identical log ----
                Assert.AreEqual(damage, weapon.GetProperty(PropertyInt.Damage),
                    $"cycle {cycle}: Damage moved, so the four Iron were reversed, re-applied, or both");

                foreach (var kvp in natives)
                {
                    Assert.AreEqual(kvp.Value, weapon.GetProperty(kvp.Key),
                        $"cycle {cycle}: {kvp.Key} moved, so a layer 1 material was reversed or applied");
                }

                signatures.Add(string.Join("|", WeaponModTinkerSet.ReadSpecials(weapon)
                    .OrderBy(s => s.Definition.Id)
                    .Select(s => $"{s.Definition.Id}:{s.Magnitude:0.####}")));
            }

            Assert.IsTrue(signatures.Count > 1,
                $"{cycles} rerolls produced {signatures.Count} distinct special set(s). The reroll must actually reroll the specials - a no-op would satisfy every byte-identical assertion above");
        }

        /// <summary>
        /// REWORKED TWICE, and the swing is worth reading. The 2026-08-06 rework made Amethyst a special-only
        /// reroll that could NOT grant a first special, so a zero-special weapon REFUSED. The 2026-08-07
        /// directive reversed that: Amethyst adds a first special when none are held, so a zero-special weapon
        /// is accepted and gains one.
        ///
        /// So this test now: confirms the zero-special weapon is ACCEPTED and that the swap adds exactly one
        /// special without reporting a loss, then confirms it stays accepted once specials exist, including AT
        /// the cap - rerolling a special AT the cap is the tool's other purpose.
        /// </summary>
        [TestMethod]
        public void Sequence_SwapAddsAtZeroSpecialsAndAcceptsOnceAndAtTheCap()
        {
            var bag = WeaponModTestKit.MakeBag(MaterialType.Amethyst);

            foreach (var weaponClass in AllClasses)
            {
                var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);
                var probe = new WeaponModProbe(weapon);

                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(bag, weapon),
                    $"{weaponClass}: a zero-special weapon must ACCEPT a swap - Amethyst adds a first special");

                Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon), $"{weaponClass}: precondition - the weapon starts with no specials");

                var added = WeaponModManager.ApplySwap(weapon, weaponClass, 10.0);

                Assert.IsNotNull(added, $"{weaponClass}: the swap must not refuse a zero-special weapon");
                Assert.AreEqual(1, WeaponModTinkerSet.SpecialCount(weapon),
                    $"{weaponClass}: a swap on a zero-special weapon must leave exactly one special");
                Assert.IsFalse(added.Any(l => l.StartsWith("Lost:", StringComparison.Ordinal)),
                    $"{weaponClass}: nothing was held, so nothing may be reported as lost");

                // seed specials the only remaining way: a Tourmaline reroll, forced to produce at least the
                // reachable count so this test does not depend on the live odds table
                var reachable = ReachableSpecials(weaponClass);

                var prior1 = PropertyManager.GetDouble("weapon_mod_special_chance_1").Item;

                try
                {
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_1", 1.0);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_2", 1.0);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_3", 1.0);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_4", 1.0);

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass}: seeding reroll produced no result");
                }
                finally
                {
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_2", 0.60);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_3", 0.30);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_4", 0.10);
                }

                Assert.AreEqual(reachable, WeaponModTinkerSet.SpecialCount(weapon), $"{weaponClass}: the forced reroll did not fill to the reachable pool depth");

                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(bag, weapon),
                    $"{weaponClass}: a weapon holding at least one special must accept a swap, including at the cap - SwapAtSpecialCap is gone");

                Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, weaponClass, 10.0), $"{weaponClass}: the swap produced no result");

                probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} swap");

                // ... and the reroll stays available too, which is what makes it the path back from a bad set
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None,
                    WeaponModTestKit.Refusal(WeaponModTestKit.MakeBag(MaterialType.Tourmaline), weapon),
                    $"{weaponClass}: the reroll must remain available at the special cap");
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

            var prior1 = PropertyManager.GetDouble("weapon_mod_special_chance_1").Item;
            var prior2 = PropertyManager.GetDouble("weapon_mod_special_chance_2").Item;
            var prior3 = PropertyManager.GetDouble("weapon_mod_special_chance_3").Item;
            var prior4 = PropertyManager.GetDouble("weapon_mod_special_chance_4").Item;

            try
            {
            foreach (var weaponClass in AllClasses)
            {
                for (var round = 0; round < 15; round++)
                {
                    var weapon = WeaponModTestKit.MakeHandTinkered(weaponClass);
                    var probe = new WeaponModProbe(weapon);

                    var reachable = ReachableSpecials(weaponClass);

                    // REWORKED 2026-08-06: Amethyst can no longer grind a weapon up to a full set from zero -
                    // it needs a special to reroll, not add - so a full set is seeded with a forced-odds reroll
                    // instead, which is the only remaining way to reach one.
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_1", 1.0);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_2", 1.0);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_3", 1.0);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_4", 1.0);

                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, weaponClass, 10.0), $"{weaponClass} round {round}: seeding reroll produced no result");
                    probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} round {round} after the seeding reroll");

                    PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_2", prior2);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_3", prior3);
                    PropertyManager.ModifyDouble("weapon_mod_special_chance_4", prior4);

                    Assert.AreEqual(reachable, WeaponModTinkerSet.SpecialCount(weapon), $"{weaponClass} round {round}: the seeding reroll did not fill to the reachable pool depth");

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

                    // and the weapon accepts a swap again as long as it holds at least one special - REWORKED
                    // 2026-08-06: being AT the cap no longer refuses a swap, only holding zero specials does
                    if (after.Count > 0)
                    {
                        Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModTestKit.Refusal(amethyst, weapon),
                            $"{weaponClass} round {round}: the weapon is not workable after the reroll");

                        Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, weaponClass, 10.0), $"{weaponClass} round {round}: the post-reroll swap produced no result");
                        probe.AssertInvariants(weapon, weaponClass, $"{weaponClass} round {round} post-reroll swap");
                    }
                }
            }
            }
            finally
            {
                PropertyManager.ModifyDouble("weapon_mod_special_chance_1", prior1);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_2", prior2);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_3", prior3);
                PropertyManager.ModifyDouble("weapon_mod_special_chance_4", prior4);
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

            // REWORKED 2026-08-06: Amethyst needs an existing special to reroll, and can no longer grind one up
            // from zero. Seed one with a reroll first - at the shipped defaults weapon_mod_special_chance_1 is
            // 1.00, so a reroll now ALWAYS produces at least one special, which is what keeps this loop swap-
            // driven rather than stalling on a zero-special weapon.
            Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), "seeding reroll");
            Assert.IsTrue(WeaponModTinkerSet.SpecialCount(weapon) > 0, "the seeding reroll produced no special at the shipped defaults");

            var swaps = 0;

            for (var i = 1; i <= 200; i++)
            {
                if (WeaponModTinkerSet.SpecialCount(weapon) <= 0)
                {
                    // the rare case: a swap replacement resolved to zero magnitude and the last special was
                    // lost. A reroll re-seeds - at the shipped defaults it always produces at least one.
                    Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), $"re-seeding reroll at {i}");
                    Assert.IsTrue(WeaponModTinkerSet.SpecialCount(weapon) > 0, $"re-seeding reroll at {i} produced no special");
                }
                else
                {
                    // REWORKED 2026-08-06: being AT the cap no longer refuses a swap - rerolling a special AT
                    // the cap is the tool's purpose - so every non-zero state is a real swap now, not just a
                    // below-cap subset of them.
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

        /// <summary>
        /// RENAMED AND REWRITTEN 2026-08-07 (was Slayer_SurvivesFiftyRerollsUnchangedAndCostsNoSlot).
        ///
        /// "Costs no slot" used to be demonstrated INDIRECTLY: an unreserved weapon laid down all ten tinkers
        /// on every reroll, so slayer quietly taking a slot would have shown up as a nine. The special-only
        /// reroll lays down no tinkers, so that reading is gone - and the direct reading that replaces it is
        /// better evidence anyway. Slayer costs no slot if and only if it is not counted as RESERVED and does
        /// not appear as a special RECORD, and both are now asserted against the accessors themselves rather
        /// than inferred from a total.
        ///
        /// SlayerCreatureType 166 and SlayerDamageBonus 138 are also the two properties an over-eager "reverse
        /// everything the weapon carries" implementation is most likely to sweep up, which is why fifty rerolls
        /// rather than one.
        /// </summary>
        [TestMethod]
        public void Slayer_SurvivesFiftyRerollsByteIdenticalAndIsNeverCountedOrRecorded()
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

                    // byte-identical
                    Assert.AreEqual((int)CreatureType.Olthoi, weapon.GetProperty(PropertyInt.SlayerCreatureType), $"{weaponClass} reroll {i}: SlayerCreatureType moved");
                    Assert.AreEqual(1.75, weapon.GetProperty(PropertyFloat.SlayerDamageBonus).Value, 1e-12, $"{weaponClass} reroll {i}: SlayerDamageBonus moved");

                    // and costs nothing, read directly off the two things a cost would show up in
                    Assert.AreEqual(0, WeaponModTinkerSet.ReadReservedSlots(weapon),
                        $"{weaponClass} reroll {i}: slayer is being counted as a reserved slot - it is not part of the ten-slot budget at all");

                    Assert.IsFalse(WeaponModTinkerSet.ReadSpecials(weapon).Any(s => s.Definition.NativeInt == PropertyInt.SlayerCreatureType
                            || s.Definition.NativeFloat == PropertyFloat.SlayerDamageBonus),
                        $"{weaponClass} reroll {i}: a special claims one of the slayer properties as its native, so a reversal would rewrite the weapon's slayer");

                    // the layer 1 budget is untouched on a weapon that never had one
                    Assert.IsNull(weapon.GetProperty(PropertyInt.WeaponModTinkerCount),
                        $"{weaponClass} reroll {i}: the reroll wrote a tinker count on an untinkered weapon");
                }
            }
        }

        // ================= H. statistical =================

        /// <summary>
        /// The reroll's special-count distribution measured through the REAL application path rather than the
        /// pure odds function. RECOMPUTED 2026-08-06 for the v3 magnitude pass's special-chance retune
        /// (1.00 / 0.60 / 0.30 / 0.10, from 0.35 / 0.10 / 0.02 / 0.00): none 0 / one 40 / two 30 / three 20 /
        /// four 10, derived as P(exactly n) = P(at least n) - P(at least n+1) over the cumulative bands. 4000
        /// samples with a wide tolerance - this is a regression tripwire against the cumulative bands being
        /// read as conditional, not a precision measurement. The melee Tier A pool is 7 deep (v3 expansion),
        /// well above the special cap of 4, so a draw of four is never truncated by pool depth here.
        /// </summary>
        [TestMethod]
        public void Statistics_RerollSpecialCountMatchesTheDesignedDistribution()
        {
            const int samples = 4000;

            var counts = new int[5];

            for (var i = 0; i < samples; i++)
            {
                var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0));

                counts[WeaponModTinkerSet.SpecialCount(weapon)]++;
            }

            Assert.AreEqual(0.00, counts[0] / (double)samples, 0.01, "P(no special) over the real reroll path - unreachable now that chance_1 defaults to 1.00");
            Assert.AreEqual(0.40, counts[1] / (double)samples, 0.03, "P(exactly one)");
            Assert.AreEqual(0.30, counts[2] / (double)samples, 0.03, "P(exactly two)");
            Assert.AreEqual(0.20, counts[3] / (double)samples, 0.03, "P(exactly three)");
            Assert.AreEqual(0.10, counts[4] / (double)samples, 0.02, "P(exactly four)");
        }

        /// <summary>
        /// SUPERSEDED TWICE. weapon_mod_swap_special_chance used to be the odds ApplySwap added a special on a
        /// weapon holding none; the 2026-08-06 rework retired that by making the swap refuse a zero-special
        /// weapon outright. The 2026-08-07 directive retired the refusal in turn: Amethyst adds a first
        /// special DETERMINISTICALLY, at no chance at all.
        ///
        /// That determinism is the thing worth pinning - the tunable is gone from this path in both
        /// directions, so 500 samples must produce 500 additions, not a distribution.
        /// </summary>
        [TestMethod]
        public void Statistics_SwapAlwaysAddsAFirstSpecialAndAlwaysAttemptsAReplacementOnceOneExists()
        {
            const int samples = 500;

            for (var i = 0; i < samples; i++)
            {
                var zero = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);

                Assert.IsNotNull(WeaponModManager.ApplySwap(zero, WeaponClass.Melee, 10.0), $"sample {i}: swap refused a zero-special weapon");
                Assert.AreEqual(1, WeaponModTinkerSet.SpecialCount(zero),
                    $"sample {i}: a swap on a zero-special weapon must ALWAYS add exactly one - there is no chance roll on this path");

                var seeded = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);
                WeaponModTinkerSet.ApplySpecial(seeded, WeaponModRegistry.Get(WeaponModId.ShieldBypass), 0.3);

                Assert.IsNotNull(WeaponModManager.ApplySwap(seeded, WeaponClass.Melee, 10.0), $"sample {i}: swap refused on a weapon holding one special");
            }
        }

        /// <summary>
        /// STILL TRUE AFTER 2026-08-07, FOR A COMPLETELY DIFFERENT REASON - which is why the test is kept and
        /// retargeted rather than deleted. Design section 8's original swap priced a mean of about 11.9 uses
        /// to GROW a fresh ten-tinker weapon up to a full set of specials.
        ///
        /// It used to be impossible because the swap REFUSED a zero-special weapon. It is now impossible
        /// because of the shape of what replaced that refusal: the swap adds only when the weapon holds
        /// nothing, and from one special onwards it removes one and adds one, so the count is pinned at 1
        /// forever. Amethyst is a way to reach A special and then reroll it - never a way to accumulate a set.
        ///
        /// The only path to more than one special remains the Tourmaline reroll's odds table, covered by
        /// Statistics_RerollSpecialCountMatchesTheDesignedDistribution above. If a future change lets the swap
        /// grow a set, the design's grind pricing has to be revisited, and this test is the tripwire.
        /// </summary>
        [TestMethod]
        public void Statistics_SwapCanNeverGrindFromZeroToAFullSet()
        {
            var weapon = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);

            for (var use = 0; use < 20; use++)
            {
                Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), $"use {use}: swap must not refuse");

                Assert.AreEqual(1, WeaponModTinkerSet.SpecialCount(weapon),
                    $"use {use}: the swap must hold the count at exactly one - the first use adds, and every use after it trades one for one");
            }

            Assert.IsTrue(WeaponModTinkerSet.SpecialCount(weapon) < WeaponModRegistry.MaxSpecials,
                "twenty swaps must never accumulate a full set - only the Tourmaline reroll can produce more than one special");
        }

        // ---------------- helpers ----------------

        /// <summary>
        /// The most specials a weapon of this class can actually END UP HOLDING right now: the cap, bounded by
        /// the depth of its pool at the live gate state.
        ///
        /// THE TWO STOPPED BEING THE SAME NUMBER ON 2026-08-06, when MaxSpecials went from 3 to 4 while the Tier
        /// A caster pool stayed at 3. A loop written as "swap until SpecialCount reaches MaxSpecials" then never
        /// terminates for a caster with weapon_mods_enabled off. Use this rather than the constant wherever a
        /// test drives a weapon UP TO its maximum, regardless of the live gate state.
        /// </summary>
        private static int ReachableSpecials(WeaponClass weaponClass) =>
            Math.Min(WeaponModRegistry.MaxSpecials, WeaponModRegistry.Pool(weaponClass).Count);

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
