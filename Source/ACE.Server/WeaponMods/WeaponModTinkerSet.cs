using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The slot arithmetic, the tinker-log format, and the apply/reverse of a whole layer 1 set. Everything here
    /// is deliberately free of Player, Session and database, so the reversal math can be tested directly.
    ///
    /// THE SLOT MODEL (CHANGED 2026-08-06 - SPECIALS ARE NO LONGER IN IT):
    ///
    ///     10 = reservedSlots + tinkerCount            specials are bounded separately by MaxSpecials
    ///
    /// It used to read "10 = reservedSlots + specialCount + tinkerCount", so every special a weapon rolled cost
    /// it a tinker. That made an inert special strictly WORSE than no special, which is the defect this change
    /// removes. Tinker capacity is now <see cref="AvailableSlots"/> - ten minus the reserved slots - whatever
    /// the weapon happens to be carrying in specials.
    ///
    /// RESERVED SLOTS ARE NOT JUST IMBUES (HARD). A slot consumed by a tinker this table does not own - Oak,
    /// imbue salvage, or a bag with no MaterialType - is one this system can neither reverse nor account for,
    /// so it MUST be reserved. An earlier revision reserved only the imbue popcount while
    /// <see cref="ReadComposition"/> filtered the log down to owned materials, which handed those slots straight
    /// back to the refill: ten Oak (wcid 20989 has a live cook_book row, so it is hand-tinkerable today) gave
    /// NumTimesTinkered 10, a ten-entry log that the gate accepted, nothing reversible, and then ten fresh
    /// tinkers plus up to three specials on top - twenty tinkers of effect on one weapon, plus a permanent
    /// WeaponTime floored to 0 that nominal subtraction can never undo. See <see cref="ComputeReservedSlots"/>.
    ///
    /// PropertyInt.NumTimesTinkered 171 is SET to 10 on every application, never incremented, and MUST NEVER
    /// EXCEED 10: TinkeringDifficulty is an unguarded 10-element list indexed directly by that counter
    /// (RecipeManager.cs:244, list at :323-336), so any ungated recipe path reaching it at 11 throws. The
    /// counter always reads 10 because the budget is always full, whatever the mix inside it.
    ///
    /// SPECIALS NEVER INCREMENTED THAT COUNTER, BEFORE OR AFTER THE DECOUPLING, and this is the fact the whole
    /// migration question turns on. <see cref="WriteComposition"/> SETS the counter to 10 unconditionally, and
    /// specials are not written into either tinker log. What specials used to do was SHORTEN the log, because
    /// the tinker count they were subtracted from was the number of log entries written - so a pre-decoupling
    /// weapon holding N specials carries NumTimesTinkered 10 against a log of 10 - N entries. After the
    /// decoupling the log is written at full length again and the two agree by construction.
    ///
    /// THAT ASYMMETRY IS WHY THE INTEGRITY GATE STAYS SUPPRESSED ON A MANAGED WEAPON. See
    /// <see cref="PassesIntegrityGate"/>: tightening it would refuse every weapon rolled before this change,
    /// which is exactly the migration hazard. No data migration is needed and none should be written.
    ///
    /// Slayer is NOT a slot: SlayerCreatureType 166 and SlayerDamageBonus 138 are untouched and cost nothing.
    /// </summary>
    public static class WeaponModTinkerSet
    {
        // ---------------- slot arithmetic (pure) ----------------

        /// <summary>
        /// How many slots a weapon's imbues consume: the popcount of PropertyInt.ImbuedEffect 179, CLAMPED to
        /// [0, 10]. ImbuedEffectType is a [Flags] enum that includes the high bits 0x20000000, 0x40000000 and
        /// 0x80000000, so an unclamped popcount can exceed 10 on odd data and drive tinkerCount negative.
        /// </summary>
        public static int ReservedImbueSlots(int imbuedEffect)
        {
            var bits = System.Numerics.BitOperations.PopCount((uint)imbuedEffect);

            return ClampReserved(bits);
        }

        public static int ClampReserved(int reserved) => Math.Clamp(reserved, 0, WeaponModRegistry.TotalSlots);

        /// <summary>
        /// The full reserved count: imbue slots PLUS every log entry this table does not own and therefore
        /// cannot reverse.
        ///
        ///     unknown  = parsedLogCount - knownCount
        ///     reserved = clamp(imbuePopcount + max(0, unknown - imbuePopcount), 0, 10)
        ///
        /// Imbue salvage stamps a log entry of its own and is equally "unknown" to this table, so the inner
        /// subtraction is what stops one imbue being charged twice - once for its ImbuedEffect bit and again for
        /// its log entry. VERIFIED against the writer: RecipeManager.HandleTinkerLog appends exactly one entry
        /// per successful tinker, imbues included, and PrismaticDriftStone.AppendTinkerLog follows the same
        /// one-entry-per-material shape, so the counts are directly comparable.
        ///
        /// Resulting behaviour: ten Oak reserves 10, so <see cref="AvailableSlots"/> is 0 and the use is refused
        /// outright - correct, because nothing on that weapon can be reversed. Five Oak plus five Iron reserves
        /// 5, refills five, and the total effect stays at ten. One imbue material plus nine Iron reserves 1,
        /// exactly as it did before this rule existed.
        /// </summary>
        public static int ComputeReservedSlots(int imbuePopcount, int parsedLogCount, int knownCount)
        {
            var imbue = ClampReserved(imbuePopcount);
            var unknown = Math.Max(0, parsedLogCount - knownCount);

            return ClampReserved(imbue + Math.Max(0, unknown - imbue));
        }

        /// <summary>
        /// Slots left for layer 1 tinkers once the reserved slots have taken theirs. Never negative.
        ///
        /// IT NO LONGER TAKES A SPECIAL COUNT (2026-08-06). The parameter was dropped rather than kept and
        /// ignored, deliberately: an overload that silently discarded it would leave every existing call site
        /// compiling and quietly changing meaning, and the call sites are exactly what needed reviewing.
        /// Identical to <see cref="AvailableSlots"/> by construction now - both names are kept because they
        /// answer different questions ("how many tinkers do I roll" against "is there anything here to work
        /// with at all") and the second is what <see cref="WeaponModManager.ResolveRefusal"/> gates on.
        /// </summary>
        public static int ComputeTinkerCount(int reservedSlots) => AvailableSlots(reservedSlots);

        /// <summary>
        /// Slots a weapon has available for TINKERS, once reserved slots are taken. Since the 2026-08-06
        /// decoupling this is the whole of the non-reserved budget: specials do not draw on it.
        /// </summary>
        public static int AvailableSlots(int reservedSlots) =>
            Math.Max(0, WeaponModRegistry.TotalSlots - ClampReserved(reservedSlots));

        // ---------------- log format (pure) ----------------

        /// <summary>
        /// Parses a tinker log: a comma-separated list of MaterialType ids, the format
        /// RecipeManager.HandleTinkerLog writes. Returns FALSE when an entry is not a number at all, which is
        /// what the integrity gate treats as an unreadable log.
        ///
        /// Note an entry may be a well-formed number that is not a defined MaterialType - HandleTinkerLog falls
        /// back to the source's WeenieClassId when it has no MaterialType. Those parse fine and simply are not
        /// materials this system knows how to reverse.
        /// </summary>
        public static bool TryParseLog(string csv, out List<MaterialType> entries)
        {
            entries = new List<MaterialType>();

            if (string.IsNullOrWhiteSpace(csv))
                return true;

            foreach (var raw in csv.Split(','))
            {
                var token = raw.Trim();

                if (token.Length == 0)
                    return false;

                if (!uint.TryParse(token, out var value))
                    return false;

                entries.Add((MaterialType)value);
            }

            return true;
        }

        /// <summary>Renders a tinker composition back into the retail log format. Empty list = null (remove the row).</summary>
        public static string SerializeLog(IEnumerable<MaterialType> entries)
        {
            if (entries == null)
                return null;

            var list = entries.ToList();

            if (list.Count == 0)
                return null;

            return string.Join(",", list.Select(m => ((uint)m).ToString()));
        }

        /// <summary>Only the entries this system knows how to apply and reverse. See WeaponTinkerTable.IsKnown.</summary>
        public static List<MaterialType> KnownOnly(IEnumerable<MaterialType> entries) =>
            entries == null ? new List<MaterialType>() : entries.Where(WeaponTinkerTable.IsKnown).ToList();

        /// <summary>Groups a composition into (material, count) pairs, so a multiply is done once via Math.Pow.</summary>
        public static Dictionary<MaterialType, int> Counts(IEnumerable<MaterialType> entries)
        {
            var counts = new Dictionary<MaterialType, int>();

            if (entries == null)
                return counts;

            foreach (var material in entries)
            {
                counts.TryGetValue(material, out var current);
                counts[material] = current + 1;
            }

            return counts;
        }

        // ---------------- item state ----------------

        /// <summary>
        /// TRUE when the weapon carries PropertyInt.WeaponModTinkerCount 9034, which marks it as managed by this
        /// system. On a managed weapon the retail integrity gate is suppressed and reversal reads
        /// WeaponModTinkerLog instead of TinkerLog, because our own weapons carry specials in part of the budget
        /// and would otherwise trip the gate on every use.
        /// </summary>
        public static bool IsManaged(WorldObject weapon) => weapon?.GetProperty(PropertyInt.WeaponModTinkerCount) != null;

        /// <summary>Which log holds the composition this system should reverse.</summary>
        public static PropertyString ReversalLog(WorldObject weapon) =>
            IsManaged(weapon) ? PropertyString.WeaponModTinkerLog : PropertyString.TinkerLog;

        /// <summary>Every entry in the weapon's reversal log, owned or not. Empty when the log is absent or unreadable.</summary>
        public static List<MaterialType> ReadFullComposition(WorldObject weapon)
        {
            if (weapon == null)
                return new List<MaterialType>();

            TryParseLog(weapon.GetProperty(ReversalLog(weapon)), out var entries);

            return entries;
        }

        /// <summary>
        /// The layer 1 composition currently on the weapon, filtered to materials this table owns. Entries it
        /// does not own - imbue salvage, Oak, a bag with no MaterialType - are left strictly alone rather than
        /// guessed at, so their effects simply persist across a reroll. Their SLOTS are charged for by
        /// <see cref="ComputeReservedSlots"/>, and the entries themselves are carried forward verbatim by
        /// <see cref="WriteComposition"/>, so a reroll neither reverses them nor forgets them.
        /// </summary>
        public static List<MaterialType> ReadComposition(WorldObject weapon) => KnownOnly(ReadFullComposition(weapon));

        /// <summary>The log entries this table does not own, in log order. These are preserved verbatim across a rewrite.</summary>
        public static List<MaterialType> ReadUnaccountedEntries(WorldObject weapon) =>
            ReadFullComposition(weapon).Where(m => !WeaponTinkerTable.IsKnown(m)).ToList();

        public static int ReadReservedImbueSlots(WorldObject weapon) =>
            ReservedImbueSlots(weapon?.GetProperty(PropertyInt.ImbuedEffect) ?? 0);

        /// <summary>
        /// The weapon's full reserved-slot count: its imbues plus every log entry this table cannot reverse.
        /// This is what both flows budget against - see <see cref="ComputeReservedSlots"/>.
        /// </summary>
        public static int ReadReservedSlots(WorldObject weapon)
        {
            var entries = ReadFullComposition(weapon);

            return ComputeReservedSlots(ReadReservedImbueSlots(weapon), entries.Count, KnownOnly(entries).Count);
        }

        // ---------------- apply / reverse a whole set ----------------

        public static void ApplyTinkers(WorldObject weapon, IEnumerable<MaterialType> materials)
        {
            if (weapon == null)
                return;

            foreach (var kvp in Counts(materials))
            {
                if (WeaponTinkerTable.TryGet(kvp.Key, out var definition))
                    definition.Apply(weapon, kvp.Value);
            }
        }

        public static void ReverseTinkers(WorldObject weapon, IEnumerable<MaterialType> materials)
        {
            if (weapon == null)
                return;

            foreach (var kvp in Counts(materials))
            {
                if (WeaponTinkerTable.TryGet(kvp.Key, out var definition))
                    definition.Reverse(weapon, kvp.Value);
            }
        }

        // ---------------- specials on an item ----------------

        /// <summary>
        /// THE RAW STORED VALUE, whose MEANING DEPENDS ON THE TIER - a Tier A record holds an applied
        /// magnitude, a Tier B record holds a roll fraction. Use <see cref="ReadMagnitude"/> unless you are
        /// doing Tier A reversal arithmetic, which is the one thing that needs the number as stored.
        /// </summary>
        public static double? ReadRecord(WorldObject weapon, WeaponModDefinition definition) =>
            weapon == null || definition == null ? null : weapon.GetProperty(definition.Record);

        /// <summary>
        /// A held special's EFFECTIVE MAGNITUDE, in the native property's own units, whichever tier it is.
        /// This is the number the engine acts on and the number the appraisal panel prints, and it is the one
        /// every caller outside the reversal arithmetic wants.
        ///
        /// For Tier A it is the record verbatim. For Tier B the record is a roll fraction and the magnitude is
        /// recomputed against the LIVE catalog on every read - which is the whole point of the split: retuning
        /// a MaxRoll or the scale tunable moves weapons already in the world.
        /// </summary>
        public static double ReadMagnitude(WorldObject weapon, WeaponModDefinition definition, double scale)
        {
            var record = ReadRecord(weapon, definition);

            if (record == null || double.IsNaN(record.Value))
                return 0.0;

            return definition.Tier == WeaponModTier.B
                ? WeaponModValue.MagnitudeFromFraction(definition, record.Value, scale)
                : record.Value;
        }

        /// <summary>ReadMagnitude against the live weapon_mod_magnitude_scale tunable.</summary>
        public static double ReadMagnitude(WorldObject weapon, WeaponModDefinition definition) =>
            ReadMagnitude(weapon, definition, WeaponModValue.MagnitudeScale());

        /// <summary>
        /// Every special currently recorded on the weapon, with its EFFECTIVE MAGNITUDE - not the raw record.
        /// Tier B entries are resolved against the live catalog on the way out, so a caller never has to know
        /// which tier it is holding. <see cref="ReadRecord"/> is the raw accessor for the one caller that does.
        /// </summary>
        public static List<(WeaponModDefinition Definition, double Magnitude)> ReadSpecials(WorldObject weapon)
        {
            var specials = new List<(WeaponModDefinition, double)>();

            if (weapon == null)
                return specials;

            var scale = WeaponModValue.MagnitudeScale();

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                if (weapon.GetProperty(definition.Record) != null)
                    specials.Add((definition, ReadMagnitude(weapon, definition, scale)));
            }

            return specials;
        }

        public static int SpecialCount(WorldObject weapon) => ReadSpecials(weapon).Count;

        /// <summary>
        /// THE SINGLE PLACE THE TIER STORAGE SPLIT IS WRITTEN. A Tier A row adds its magnitude to a native
        /// property and records exactly what it added, because reversal has to subtract that same number back
        /// off a value the loot generator may also have contributed to. A Tier B row writes no native at all,
        /// so it records the ROLL FRACTION instead and lets every read re-derive the magnitude from the live
        /// catalog.
        ///
        /// Returns the applied magnitude, so a caller can report what it just did without recomputing it.
        /// </summary>
        public static double ApplySpecialAtFraction(WorldObject weapon, WeaponModDefinition definition, double fraction, double scale)
        {
            if (weapon == null || definition == null)
                return 0.0;

            var magnitude = WeaponModValue.MagnitudeFromFraction(definition, fraction, scale);

            if (definition.Tier == WeaponModTier.B)
            {
                // SetProperty, never player.UpdateProperty: this row is bookkeeping and must never reach the
                // client. No native is written - a Tier B row has none.
                weapon.SetProperty(definition.Record, WeaponModValue.Clamp01(fraction));

                return magnitude;
            }

            definition.WriteNative(weapon, definition.ApplyValue(definition.ReadNative(weapon), magnitude));

            weapon.SetProperty(definition.Record, magnitude);

            return magnitude;
        }

        /// <summary>ApplySpecialAtFraction against the live weapon_mod_magnitude_scale tunable.</summary>
        public static double ApplySpecialAtFraction(WorldObject weapon, WeaponModDefinition definition, double fraction) =>
            ApplySpecialAtFraction(weapon, definition, fraction, WeaponModValue.MagnitudeScale());

        /// <summary>
        /// Applies a special so the weapon ends up carrying <paramref name="magnitude"/> RIGHT NOW, converting
        /// to whatever its tier actually stores. The contract is unchanged from before the storage split, which
        /// is why the roll path and every existing caller still read naturally.
        ///
        /// PREFER <see cref="ApplySpecialAtFraction"/> WHEREVER THE ROLL IS IN HAND. This overload has to invert
        /// through <see cref="WeaponModValue.FractionFor"/> for a Tier B row, and that inversion is lossy at the
        /// edges: a magnitude above the row's current ceiling clamps to a full-strength roll rather than
        /// round-tripping. Exact for any magnitude the catalog can actually produce, which is why it remains
        /// the natural way to seed a weapon.
        /// </summary>
        public static void ApplySpecial(WorldObject weapon, WeaponModDefinition definition, double magnitude)
        {
            if (weapon == null || definition == null)
                return;

            if (definition.Tier != WeaponModTier.B)
            {
                definition.WriteNative(weapon, definition.ApplyValue(definition.ReadNative(weapon), magnitude));

                weapon.SetProperty(definition.Record, magnitude);

                return;
            }

            var scale = WeaponModValue.MagnitudeScale();

            weapon.SetProperty(definition.Record, WeaponModValue.FractionFor(definition, magnitude, scale));
        }

        /// <summary>
        /// Takes a special's recorded magnitude back off its native property and CLEARS the record with
        /// RemoveProperty, never SetProperty(0). Zeroing would leave a dead biota_properties_float row per
        /// modifier ever rolled, so a heavily rerolled weapon would accumulate up to 20 junk rows that all read
        /// as absent. This is the ClearMods pattern from EquipmentModManager.
        /// </summary>
        /// <remarks>
        /// READS THE RAW RECORD, NOT <see cref="ReadMagnitude"/>, and that is required rather than incidental.
        /// Tier A reversal must subtract back exactly the number that was ADDED, which is what the record
        /// holds; re-deriving it from the live catalog would subtract a retuned value from a native the old
        /// value went into, and the difference would stick permanently on an item nobody can audit.
        ///
        /// The Tier B case reaches the same two lines and is a no-op by construction: a Tier B row has no
        /// native, so <see cref="WeaponModDefinition.WriteNative"/> returns without writing and the roll
        /// fraction never enters any arithmetic. Only the RemoveProperty below does anything for it.
        /// </remarks>
        public static void ReverseSpecial(WorldObject weapon, WeaponModDefinition definition)
        {
            if (weapon == null || definition == null)
                return;

            var record = ReadRecord(weapon, definition);

            if (record == null)
                return;

            definition.WriteNative(weapon, definition.ReverseValue(definition.ReadNative(weapon), record.Value));

            weapon.RemoveProperty(definition.Record);
        }

        /// <summary>Reverses and clears every special on the weapon.</summary>
        public static void ClearSpecials(WorldObject weapon)
        {
            if (weapon == null)
                return;

            foreach (var definition in WeaponModRegistry.AllMods)
                ReverseSpecial(weapon, definition);
        }

        // ---------------- bookkeeping ----------------

        /// <summary>
        /// Writes the post-application state: the tinker counter, the tinker cap counter, and BOTH logs.
        ///
        /// THE LOGS ARE REPLACED, NEVER APPENDED (HARD). RecipeManager.HandleTinkerLog does "TinkerLog += ..."
        /// which is right for retail, where a log grows to ten once and the item locks, and exactly wrong here:
        /// reroll is repeatable, so appending would bloat the row without bound, break the integrity gate on the
        /// second use (a 20-entry log against a NumTimesTinkered pinned at 10 refuses every subsequent reroll),
        /// and make reversal subtract tinkers that were already removed. Never call HandleTinkerLog and never
        /// use "+=" here.
        ///
        /// THE UNACCOUNTED ENTRIES ARE CARRIED FORWARD (HARD). <paramref name="preserved"/> is written ahead of
        /// the new set, and it must be the log entries this table does not own (see
        /// <see cref="ReadUnaccountedEntries"/>), captured BEFORE anything is applied. Dropping them would undo
        /// <see cref="ComputeReservedSlots"/> one use later: the slots would be reserved on this reroll and then
        /// vanish from the log, so the NEXT reroll would see an all-known log, reserve nothing, and refill the
        /// full ten on top of effects that were never reversed.
        /// </summary>
        public static void WriteComposition(WorldObject weapon, IEnumerable<MaterialType> preserved, IEnumerable<MaterialType> tinkers)
        {
            if (weapon == null)
                return;

            var list = tinkers?.ToList() ?? new List<MaterialType>();
            var carried = preserved?.ToList() ?? new List<MaterialType>();

            var log = SerializeLog(carried.Concat(list));

            weapon.SetProperty(PropertyInt.NumTimesTinkered, WeaponModRegistry.TotalSlots);

            // this system's OWN count: the entries it applied and can reverse. The carried entries are somebody
            // else's, and counting them here would make the marker lie about what a reversal can undo.
            weapon.SetProperty(PropertyInt.WeaponModTinkerCount, list.Count);

            if (log == null)
            {
                weapon.RemoveProperty(PropertyString.TinkerLog);
                weapon.RemoveProperty(PropertyString.WeaponModTinkerLog);
            }
            else
            {
                weapon.SetProperty(PropertyString.TinkerLog, log);
                weapon.SetProperty(PropertyString.WeaponModTinkerLog, log);
            }
        }

        /// <summary>
        /// The retail integrity gate: the parsed TinkerLog entry count must equal NumTimesTinkered. Older shard
        /// data and retail-era items can carry a tinker count with a null or short log, and silently reversing a
        /// set you cannot fully see corrupts the item. SUPPRESSED on a weapon already managed by this system,
        /// which reads its own log instead.
        ///
        /// THIS GATE IS ABOUT COMPLETENESS, NOT OWNERSHIP. Passing it means the log accounts for every slot the
        /// counter claims; it says NOTHING about whether this table can reverse those entries. Ten Oak passes it
        /// cleanly. The budget half of that problem is <see cref="ComputeReservedSlots"/>, which must be applied
        /// alongside the gate, never instead of it.
        ///
        /// THE SUPPRESSION ON A MANAGED WEAPON IS DELIBERATELY UNCHANGED BY THE 2026-08-06 DECOUPLING, AND
        /// TIGHTENING IT WOULD BE A MIGRATION BUG. It is tempting to un-suppress it now, because a weapon
        /// written by the NEW code does satisfy it: the log is written at full tinker length, so log entries
        /// plus imbue bits account for all ten slots. But a weapon rolled by the OLD code carries a log short by
        /// exactly its special count, and un-suppressing would refuse it with TinkerLogMismatch - permanently,
        /// because the only way to rewrite that log is a reroll, which is what the refusal blocks. Weapons in
        /// that state exist on the dev shard today. So the gate stays suppressed and the accounting stays where
        /// it always was, in <see cref="ComputeReservedSlots"/>.
        ///
        /// NOR DOES AN OLD WEAPON GAIN FREE CAPACITY FROM THE SUPPRESSION. Capacity comes from
        /// <see cref="ReadReservedSlots"/>, which counts UNKNOWN log entries against KNOWN ones. Specials appear
        /// in neither, so a short log changes nothing about the reserved figure, and the one path that grants
        /// tinkers - <see cref="WeaponModManager.ApplyReroll"/> - reverses everything the weapon currently holds
        /// before it refills. The old set is undone, then a new full-length set is applied.
        /// </summary>
        public static bool PassesIntegrityGate(WorldObject weapon)
        {
            if (weapon == null)
                return false;

            if (IsManaged(weapon))
                return true;

            if (!TryParseLog(weapon.GetProperty(PropertyString.TinkerLog), out var entries))
                return false;

            return entries.Count == (weapon.GetProperty(PropertyInt.NumTimesTinkered) ?? 0);
        }
    }
}
