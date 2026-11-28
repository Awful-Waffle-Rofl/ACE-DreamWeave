using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Database;
using ACE.Entity.Enum.Properties;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// One resolved archetype. <see cref="Sha"/> stands where the archetype file's sha stood: for a seeded archetype it is the
    /// file's sha (TrialArchetypeLoader.LoadDirectory); for a curated one it is the master stamp's field 1 (TrialCurated), which
    /// rebuild compares the master stamp against and promote checks lineage on. <see cref="Broken"/> is a curated slot with no
    /// stamp whose broken record (TrialCopyGate) keeps a curated lineage: start and rebuild already refuse a broken master.
    /// </summary>
    public sealed record TrialResolvedArchetype(TrialArchetype Archetype, string Sha, bool Curated, bool Broken);

    /// <summary>
    /// An inconsistency between the seeded files and the curated masters. <see cref="Ids"/> are the archetype ids it makes
    /// ambiguous: resolving any of them refuses with <see cref="Message"/>, which names the slot-addressed recovery
    /// (@trial unpromote slot:&lt;n&gt;). Every other id resolves normally.
    /// </summary>
    public sealed record TrialArchetypeCollision(IReadOnlyList<string> Ids, IReadOnlyList<int> Slots, string Message);

    /// <summary>What @trial list shows: every archetype, then every collision.</summary>
    public sealed record TrialArchetypeListing(IReadOnlyList<TrialResolvedArchetype> Archetypes, IReadOnlyList<TrialArchetypeCollision> Collisions);

    /// <summary>
    /// Finds an archetype by id: the seeded files first (TrialArchetypeLoader.LoadDirectory, unchanged), then a scan of the
    /// master stamps for a curated one (TrialCurated). The scan is fresh on every call: nothing is cached, so a master adopted,
    /// promoted or unpromoted a moment ago is seen by the next command.
    ///
    /// Collisions (<see cref="Collisions"/>): a seeded file's slot holding a curated stamp or lineage, one curated id in two
    /// slots, and a curated id equal to a seeded file's id (flagged, never silently shadowed by the file). Resolving an id a
    /// collision names throws TrialArchetypeException (the callers' "trial data" refusal); any other id resolves normally, and
    /// <see cref="All"/> reports the collisions without throwing. A failed stamp scan throws for every id.
    /// </summary>
    public static class TrialArchetypeResolver
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The unpromote argument that addresses a master slot directly: <c>slot:&lt;n&gt;</c>.</summary>
        public const string SlotPrefix = "slot:";

        /// <summary>
        /// Test seam: the master stamps by slot (0..MasterCount-1), only slots that carry one. Live: one narrow shard read of
        /// biota_properties_string type 9034 over the master guids (ShardDatabase.GetBiotaStringPropertyInRange), synchronous
        /// on the calling thread, like the master stamp read rebuild and promote make. Masters are parked, so PlayerManager never
        /// has them.
        /// </summary>
        internal static Func<IReadOnlyDictionary<int, string>> StampScan = LiveStampScan;

        private static IReadOnlyDictionary<int, string> LiveStampScan()
        {
            var rows = DatabaseManager.Shard.BaseDatabase.GetBiotaStringPropertyInRange((ushort)PropertyString.TrialArchetypeStamp, TrialRoster.SlotBase, TrialRoster.SlotBase + (uint)TrialRoster.MasterCount - 1);
            return rows.ToDictionary(r => (int)(r.Key - TrialRoster.SlotBase), r => r.Value);
        }

        /// <summary>Resolves <paramref name="id"/> live: the files in <paramref name="archetypesDir"/>, the stamp scan and the shared copy gate. Null when no archetype has the id.</summary>
        public static TrialResolvedArchetype Resolve(string archetypesDir, string id)
            => Resolve(TrialArchetypeLoader.LoadDirectory(archetypesDir), Scan(), TrialCopyGate.Shared, id);

        /// <summary>The master slots the seeded archetype files name. No stamp scan. Throws TrialArchetypeException on bad files.</summary>
        public static IReadOnlyCollection<int> SeededSlots(string archetypesDir)
            => TrialArchetypeLoader.LoadDirectory(archetypesDir).Select(a => a.Archetype.Slot).ToList();

        /// <summary>Every archetype and every collision, live.</summary>
        public static TrialArchetypeListing All(string archetypesDir)
            => All(TrialArchetypeLoader.LoadDirectory(archetypesDir), Scan(), TrialCopyGate.Shared);

        internal static TrialResolvedArchetype Resolve(IReadOnlyList<(TrialArchetype Archetype, string Sha256)> seeded, IReadOnlyDictionary<int, string> stamps, TrialCopyGate gate, string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            var curated = Curated(stamps, gate);

            var collision = Collisions(seeded, curated).FirstOrDefault(c => c.Ids.Contains(id, StringComparer.Ordinal));
            if (collision != null)
                throw new TrialArchetypeException(collision.Message);

            var file = seeded.FirstOrDefault(s => string.Equals(s.Archetype.Id, id, StringComparison.Ordinal));
            if (file.Archetype != null)
                return new TrialResolvedArchetype(file.Archetype, file.Sha256, false, false);

            return curated.FirstOrDefault(c => string.Equals(c.Archetype.Id, id, StringComparison.Ordinal));
        }

        internal static TrialArchetypeListing All(IReadOnlyList<(TrialArchetype Archetype, string Sha256)> seeded, IReadOnlyDictionary<int, string> stamps, TrialCopyGate gate)
        {
            var curated = Curated(stamps, gate);
            var archetypes = seeded.Select(s => new TrialResolvedArchetype(s.Archetype, s.Sha256, false, false)).Concat(curated).ToList();
            return new TrialArchetypeListing(archetypes, Collisions(seeded, curated));
        }

        /// <summary>The collisions between the seeded files and the curated masters (stamps and broken lineages).</summary>
        internal static IReadOnlyList<TrialArchetypeCollision> Collisions(IReadOnlyList<(TrialArchetype Archetype, string Sha256)> seeded, IReadOnlyDictionary<int, string> stamps, TrialCopyGate gate)
            => Collisions(seeded, Curated(stamps, gate));

        private static List<TrialArchetypeCollision> Collisions(IReadOnlyList<(TrialArchetype Archetype, string Sha256)> seeded, List<TrialResolvedArchetype> curated)
        {
            ArgumentNullException.ThrowIfNull(seeded);

            var result = new List<TrialArchetypeCollision>();

            // A seeded file's slot whose master is curated: the file and the master are two archetypes on one slot.
            foreach (var c in curated)
            {
                var file = seeded.FirstOrDefault(s => s.Archetype.Slot == c.Archetype.Slot);
                if (file.Archetype == null)
                    continue;

                var slot = c.Archetype.Slot;
                result.Add(new TrialArchetypeCollision(new[] { file.Archetype.Id, c.Archetype.Id }.Distinct(StringComparer.Ordinal).ToList(), new[] { slot },
                    $"slot {slot} is the seeded archetype file '{file.Archetype.Id}', but its master is the curated archetype '{c.Archetype.Id}'; purge that master with @trial unpromote {SlotPrefix}{slot} (a reseed then rebuilds the file's master)"));
            }

            // One curated id in two or more slots.
            foreach (var group in curated.GroupBy(c => c.Archetype.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                var slots = group.Select(c => c.Archetype.Slot).OrderBy(s => s).ToList();
                result.Add(new TrialArchetypeCollision(new[] { group.Key }, slots,
                    $"curated archetype '{group.Key}' is in slots {string.Join(", ", slots)}; purge all but one with @trial unpromote {SlotPrefix}<n> (one of {string.Join(", ", slots.Select(s => SlotPrefix + s))})"));
            }

            // A curated id that is also a seeded file's id, in another slot: flagged, never shadowed by the file.
            foreach (var c in curated)
            {
                var file = seeded.FirstOrDefault(s => string.Equals(s.Archetype.Id, c.Archetype.Id, StringComparison.Ordinal));
                if (file.Archetype == null || file.Archetype.Slot == c.Archetype.Slot)
                    continue;

                result.Add(new TrialArchetypeCollision(new[] { c.Archetype.Id }, new[] { c.Archetype.Slot, file.Archetype.Slot },
                    $"'{c.Archetype.Id}' is both the seeded archetype file in slot {file.Archetype.Slot} and the curated master in slot {c.Archetype.Slot}; purge the curated master with @trial unpromote {SlotPrefix}{c.Archetype.Slot}"));
            }

            return result;
        }

        /// <summary>The curated archetypes by slot: a curated stamp, or, on a slot with no stamp, a curated broken lineage.</summary>
        private static List<TrialResolvedArchetype> Curated(IReadOnlyDictionary<int, string> stamps, TrialCopyGate gate)
        {
            ArgumentNullException.ThrowIfNull(gate);

            stamps ??= new Dictionary<int, string>();

            var result = new List<TrialResolvedArchetype>();

            for (var slot = 0; slot < TrialRoster.MasterCount; slot++)
            {
                stamps.TryGetValue(slot, out var stamp);

                string field1;
                var broken = false;

                if (!string.IsNullOrEmpty(stamp))
                {
                    // Only a full curated stamp counts; a seeded or seeded-then-promoted stamp is the file's business.
                    if (!TrialCurated.TryParse(stamp, out _, out _, out _, out _))
                        continue;

                    TrialRebuild.TryParseStamp(stamp, out field1, out _, out _);
                }
                else
                {
                    // No stamp: a curated adopt or promote whose purge ran and whose copy has not landed (or failed). The broken
                    // record keeps the lineage, read before the purge.
                    if (!gate.TryGetBrokenLineage(TrialRoster.MasterSlot(slot), out field1, out _) || !TrialCurated.TryParseIdentity(field1, out _, out _, out _))
                        continue;

                    broken = true;
                }

                TrialCurated.TryParseIdentity(field1, out var id, out var cls, out var level);

                result.Add(new TrialResolvedArchetype(new TrialArchetype(id, slot, cls, level, null, null, TrialCurated.BuffProfile), field1, true, broken));
            }

            return result;
        }

        private static IReadOnlyDictionary<int, string> Scan()
        {
            try
            {
                return StampScan();
            }
            catch (Exception ex)
            {
                log.Error("[TRIAL] the master stamp scan threw", ex);
                throw new TrialArchetypeException($"the master stamp scan failed ({ex.GetType().Name}); check the server log");
            }
        }

        /// <summary>Parses the unpromote argument <c>slot:&lt;n&gt;</c> (n a master slot, digits only). False for an archetype id.</summary>
        public static bool TryParseSlotArgument(string argument, out int slot)
        {
            slot = -1;

            if (argument == null || !argument.StartsWith(SlotPrefix, StringComparison.Ordinal))
                return false;

            var digits = argument.Substring(SlotPrefix.Length);
            if (digits.Length == 0 || digits.Length > 2 || !digits.All(char.IsAsciiDigit)
                || !int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                || n.ToString(System.Globalization.CultureInfo.InvariantCulture) != digits || n >= TrialRoster.MasterCount)
                return false;

            slot = n;
            return true;
        }

        /// <summary>The list line for one archetype: "id (slot N)", or "id (slot N, curated Class Llevel)" with ", broken" when it resolved broken.</summary>
        public static string Describe(TrialResolvedArchetype r)
            => r.Curated
                ? $"{r.Archetype.Id} (slot {r.Archetype.Slot}, curated {r.Archetype.Class} L{r.Archetype.TargetLevel}{(r.Broken ? ", broken" : "")})"
                : $"{r.Archetype.Id} (slot {r.Archetype.Slot})";
    }
}
