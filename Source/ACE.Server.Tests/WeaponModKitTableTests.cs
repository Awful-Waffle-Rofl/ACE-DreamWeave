using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Structural tests for the @weaponmodkit definition table - the PURE half of the weapon-mod test harness.
    ///
    /// The spawn path itself is deliberately NOT covered: it needs a live Player with a session and inventory,
    /// which is the same reason WeaponModManager.UseObjectOnTarget is left to the live loop. What IS covered is
    /// everything a typo in the table would break silently: a zero wcid, two rows fighting over one name, a
    /// property id that does not resolve, and - the one that would quietly produce a useless kit item - a
    /// forced TinkerLog whose entry count disagrees with its forced NumTimesTinkered, which
    /// WeaponModTinkerSet.PassesIntegrityGate refuses outright.
    ///
    /// The wcids themselves were verified against ace_world on 2026-07-30; ACE.Server.Tests has no database,
    /// so this asserts only that each row carries one.
    /// </summary>
    [TestClass]
    public class WeaponModKitTableTests
    {
        [TestMethod]
        public void Kit_IsNotEmpty()
        {
            Assert.IsTrue(WeaponModKitTable.Entries.Count > 0, "the kit definition table is empty");
        }

        [TestMethod]
        public void Kit_EveryRowHasAValidWcidAndCount()
        {
            foreach (var entry in WeaponModKitTable.Entries)
            {
                Assert.AreNotEqual(0u, entry.Wcid, $"row '{entry.Case}' has no wcid");
                Assert.IsTrue(entry.Count >= 1, $"row '{entry.Case}' has a count of {entry.Count}");
                Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Case), "a row has no case name");
                Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Expected), $"row '{entry.Case}' has no expected outcome");
            }
        }

        [TestMethod]
        public void Kit_NamesAreUniqueAndPrefixed()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in WeaponModKitTable.Entries)
            {
                StringAssert.StartsWith(entry.ItemName, WeaponModKitTable.NamePrefix,
                    $"row '{entry.Case}' does not carry the kit name prefix, so @weaponmodkit clean would leave it behind");

                Assert.IsTrue(seen.Add(entry.ItemName), $"duplicate kit item name: {entry.ItemName}");
            }
        }

        [TestMethod]
        public void Kit_EveryForcedPropertyIdResolves()
        {
            foreach (var entry in WeaponModKitTable.Entries)
            {
                foreach (var (property, _) in entry.Ints)
                    Assert.IsTrue(Enum.IsDefined(typeof(PropertyInt), property), $"row '{entry.Case}': PropertyInt {(int)property} is not defined");

                foreach (var (property, _) in entry.Floats)
                    Assert.IsTrue(Enum.IsDefined(typeof(PropertyFloat), property), $"row '{entry.Case}': PropertyFloat {(int)property} is not defined");

                foreach (var (property, _) in entry.Strings)
                    Assert.IsTrue(Enum.IsDefined(typeof(PropertyString), property), $"row '{entry.Case}': PropertyString {(int)property} is not defined");
            }
        }

        [TestMethod]
        public void Kit_NoRowForcesTheSamePropertyTwice()
        {
            foreach (var entry in WeaponModKitTable.Entries)
            {
                Assert.AreEqual(entry.Ints.Count, entry.Ints.Select(i => i.Property).Distinct().Count(), $"row '{entry.Case}' forces a PropertyInt twice");
                Assert.AreEqual(entry.Floats.Count, entry.Floats.Select(f => f.Property).Distinct().Count(), $"row '{entry.Case}' forces a PropertyFloat twice");
                Assert.AreEqual(entry.Strings.Count, entry.Strings.Select(s => s.Property).Distinct().Count(), $"row '{entry.Case}' forces a PropertyString twice");
            }
        }

        /// <summary>
        /// Every forced log has to parse as a MaterialType id list in RecipeManager.HandleTinkerLog's format, or
        /// WeaponModTinkerSet.PassesIntegrityGate reads it as an unreadable log and refuses the item.
        /// </summary>
        [TestMethod]
        public void Kit_ForcedTinkerLogsParse()
        {
            foreach (var entry in WeaponModKitTable.Entries)
            {
                var log = entry.ForcedTinkerLog;

                if (log == null)
                    continue;

                Assert.IsTrue(WeaponModTinkerSet.TryParseLog(log, out _), $"row '{entry.Case}': forced TinkerLog '{log}' does not parse");
            }
        }

        /// <summary>
        /// THE ONE THAT MATTERS. A row that forces a tinker log and a tinker count out of step spawns an item
        /// that refuses every use - a silently useless kit entry rather than a visible failure.
        ///
        /// The relation is log entries PLUS forced special records = NumTimesTinkered, which is the slot model
        /// (10 = reserved + specials + tinkers) rather than the integrity gate alone. On a row forcing no
        /// specials the two are identical, so this is exactly the original assertion for every pre-Tier-B row;
        /// on the two Tier B rows, which force three special records against a seven-entry log, the gate itself
        /// is suppressed (they also force WeaponModTinkerCount, which marks the item managed) and the slot
        /// model is what has to hold instead.
        /// </summary>
        [TestMethod]
        public void Kit_ForcedLogLengthPlusSpecialsEqualsForcedTinkerCount()
        {
            var checked_ = 0;
            var withSpecials = 0;

            foreach (var entry in WeaponModKitTable.Entries)
            {
                var log = entry.ForcedTinkerLog;
                var count = entry.ForcedTinkerCount;

                if (log == null && count == null)
                    continue;

                Assert.IsNotNull(log, $"row '{entry.Case}' forces NumTimesTinkered but no TinkerLog");
                Assert.IsNotNull(count, $"row '{entry.Case}' forces a TinkerLog but no NumTimesTinkered");

                Assert.IsTrue(WeaponModTinkerSet.TryParseLog(log, out var entries), $"row '{entry.Case}': forced TinkerLog does not parse");

                var specials = entry.ForcedSpecialCount;

                Assert.AreEqual(count.Value, entries.Count + specials,
                    $"row '{entry.Case}': NumTimesTinkered {count.Value} against a {entries.Count}-entry log plus {specials} forced special record(s) - the ten-slot budget does not add up, so this item spawns in a state no flow can produce");

                Assert.IsTrue(specials <= WeaponModRegistry.MaxSpecials,
                    $"row '{entry.Case}' forces {specials} special records, above the permanent per-weapon bound of {WeaponModRegistry.MaxSpecials}");

                if (specials > 0)
                {
                    withSpecials++;

                    // a weapon carrying specials must also be marked MANAGED, or the retail integrity gate
                    // compares its short log against NumTimesTinkered and refuses every use
                    Assert.IsTrue(entry.Ints.Any(i => i.Property == PropertyInt.WeaponModTinkerCount && i.Value != null),
                        $"row '{entry.Case}' forces special records but no WeaponModTinkerCount, so the integrity gate would refuse it on its short log");
                }

                checked_++;
            }

            Assert.IsTrue(checked_ > 0, "no row forces a tinker log, so this test asserted nothing");
            Assert.IsTrue(withSpecials > 0, "no row forces a special record, so the specials half of this test asserted nothing");
        }

        /// <summary>
        /// The imbued row is the reason the kit exists at all, so its arithmetic is pinned explicitly: ten log
        /// entries against NumTimesTinkered 10, two of them materials this system does not own, an ImbuedEffect
        /// whose popcount is 2, and therefore exactly 2 reserved slots with 8 available.
        /// </summary>
        [TestMethod]
        public void Kit_ImbuedRowReservesExactlyTwoSlots()
        {
            var entry = WeaponModKitTable.Entries.Single(e => e.Case == "imbued melee");

            var imbued = entry.Ints.Single(i => i.Property == PropertyInt.ImbuedEffect).Value;

            Assert.IsNotNull(imbued);
            Assert.AreEqual((int)(ImbuedEffectType.CriticalStrike | ImbuedEffectType.ArmorRending), imbued.Value);

            Assert.IsTrue(WeaponModTinkerSet.TryParseLog(entry.ForcedTinkerLog, out var entries));
            Assert.AreEqual(10, entries.Count);
            Assert.AreEqual(10, entry.ForcedTinkerCount);

            var known = WeaponModTinkerSet.KnownOnly(entries);

            Assert.AreEqual(8, known.Count, "eight Iron should be the only entries this system owns");

            var imbuePopcount = WeaponModTinkerSet.ReservedImbueSlots(imbued.Value);

            Assert.AreEqual(2, imbuePopcount);

            var reserved = WeaponModTinkerSet.ComputeReservedSlots(imbuePopcount, entries.Count, known.Count);

            Assert.AreEqual(2, reserved, "the two imbues must be charged once, not twice");
            Assert.AreEqual(8, WeaponModTinkerSet.AvailableSlots(reserved));
        }

        /// <summary>The ten-Oak row is the refusal case, so it has to reserve all ten and leave nothing available.</summary>
        [TestMethod]
        public void Kit_TenOakRowReservesEverySlot()
        {
            var entry = WeaponModKitTable.Entries.Single(e => e.Case == "ten-Oak melee");

            Assert.IsTrue(WeaponModTinkerSet.TryParseLog(entry.ForcedTinkerLog, out var entries));
            Assert.AreEqual(10, entries.Count);
            Assert.IsTrue(entries.All(m => m == MaterialType.Oak));
            Assert.AreEqual(0, WeaponModTinkerSet.KnownOnly(entries).Count, "Oak must not be a material this system owns");

            var reserved = WeaponModTinkerSet.ComputeReservedSlots(0, entries.Count, 0);

            Assert.AreEqual(10, reserved);
            Assert.AreEqual(0, WeaponModTinkerSet.AvailableSlots(reserved));
        }

        /// <summary>Both salvage bags must spawn FULL, or every use is refused for an incomplete unit of salvage.</summary>
        [TestMethod]
        public void Kit_BothBagsSpawnFull()
        {
            var bags = WeaponModKitTable.Bags;

            Assert.AreEqual(2, bags.Count, "the kit should carry exactly two salvage bag rows");

            foreach (var bag in bags)
            {
                var structure = bag.Ints.Single(i => i.Property == PropertyInt.Structure).Value;

                Assert.IsNotNull(structure, $"row '{bag.Case}' does not force a Structure");
                Assert.IsTrue(WeaponModManager.IsFullBag(structure, WeaponModKitTable.FullBagStructure),
                    $"row '{bag.Case}' spawns a bag at {structure} against a MaxStructure of {WeaponModKitTable.FullBagStructure}");

                Assert.IsTrue(bag.Count > 1, $"row '{bag.Case}' is consumed on every use and should spawn more than one");
            }
        }

        /// <summary>The no-workmanship row is a REMOVAL, because 53315 ships ItemWorkmanship 8 in ace_world.</summary>
        [TestMethod]
        public void Kit_NoWorkmanshipRowRemovesTheProperty()
        {
            var entry = WeaponModKitTable.Entries.Single(e => e.Case == "no workmanship");

            var forced = entry.Ints.Single(i => i.Property == PropertyInt.ItemWorkmanship);

            Assert.IsNull(forced.Value, "the no-workmanship case must REMOVE ItemWorkmanship, not set it");
        }

        /// <summary>BuildLog has to emit exactly the format WeaponModTinkerSet.TryParseLog reads back.</summary>
        [TestMethod]
        public void BuildLog_RoundTripsThroughTheLogParser()
        {
            var log = WeaponModKitTable.BuildLog((MaterialType.BlackOpal, 1), (MaterialType.Sunstone, 1), (MaterialType.Iron, 3));

            Assert.AreEqual("16,41,61,61,61", log);

            Assert.IsTrue(WeaponModTinkerSet.TryParseLog(log, out var entries));

            CollectionAssert.AreEqual(
                new[] { MaterialType.BlackOpal, MaterialType.Sunstone, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron },
                entries);
        }
    }
}
