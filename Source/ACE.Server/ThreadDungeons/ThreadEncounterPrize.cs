using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>One row of the encounters.json "prize.entries" list as authored. Validated by <see cref="ThreadEncounterPrizeTable.Parse"/>.</summary>
    public class ThreadEncounterPrizeEntryDef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>An item entry's wcids. More than one: the roll picks uniformly among those whose weenie exists. Absent on a salvage entry.</summary>
        [JsonPropertyName("wcids")]
        public List<uint> Wcids { get; set; } = new List<uint>();

        /// <summary>The stack size given (one stack). Above the item's MaxStackSize it is delivered as several stacks; a non-stackable item is always 1. Must be 1 (or absent) on a salvage entry.</summary>
        [JsonPropertyName("qty")]
        public int Qty { get; set; } = 1;

        /// <summary>Relative weight among the entries available at roll time. 0 = never rolled.</summary>
        [JsonPropertyName("weight")]
        public double Weight { get; set; } = 1.0;

        /// <summary>A salvage entry's block: several full salvage bags instead of one item. Null on an item entry.</summary>
        [JsonPropertyName("salvage")]
        public ThreadEncounterPrizeSalvageDef Salvage { get; set; }

        /// <summary>A Fiber entry's block (owner ruling 2026-11-11): Knots Fibers at the run's Knot level instead of an item. Null otherwise.</summary>
        [JsonPropertyName("fiber")]
        public ThreadEncounterPrizeFiberDef Fiber { get; set; }

        /// <summary>A Pattern entry's block (owner ruling 2026-11-11): one Knots Pattern for a pinnacle the Knots drop could roll. Null otherwise. Takes no keys.</summary>
        [JsonPropertyName("pattern")]
        public JsonElement? Pattern { get; set; }
    }

    /// <summary>A Fiber entry's "fiber" block as authored: how many Fibers (each a separate object; a Fiber does not stack).</summary>
    public class ThreadEncounterPrizeFiberDef
    {
        [JsonPropertyName("qty")]
        public int Qty { get; set; } = 1;
    }

    /// <summary>A salvage entry's "salvage" block as authored: the material pool, the bag count range, and each bag's structure and workmanship.</summary>
    public class ThreadEncounterPrizeSalvageDef
    {
        /// <summary>The salvage-bag wcids. Each bag picks one uniformly among those whose weenie exists.</summary>
        [JsonPropertyName("wcids")]
        public List<uint> Wcids { get; set; } = new List<uint>();

        [JsonPropertyName("bags")]
        public ThreadEncounterPrizeRangeDef Bags { get; set; }

        [JsonPropertyName("structure")]
        public int Structure { get; set; } = ThreadEncounterPrizeSalvage.DefaultStructure;

        [JsonPropertyName("workmanship")]
        public int Workmanship { get; set; } = ThreadEncounterPrizeSalvage.DefaultWorkmanship;
    }

    /// <summary>An inclusive { min, max } range.</summary>
    public class ThreadEncounterPrizeRangeDef
    {
        [JsonPropertyName("min")]
        public int Min { get; set; }

        [JsonPropertyName("max")]
        public int Max { get; set; }
    }

    /// <summary>A validated salvage block. Immutable.</summary>
    public sealed class ThreadEncounterPrizeSalvage
    {
        public const int DefaultStructure = 100;
        public const int DefaultWorkmanship = 10;
        public const int MaxBags = 10;
        public const int MaxStructure = 100;
        public const int MaxWorkmanship = 10;

        public ThreadEncounterPrizeSalvage(int bagsMin, int bagsMax, int structure, int workmanship)
        {
            BagsMin = bagsMin;
            BagsMax = bagsMax;
            Structure = structure;
            Workmanship = workmanship;
        }

        public int BagsMin { get; }
        public int BagsMax { get; }
        public int Structure { get; }
        public int Workmanship { get; }
    }

    /// <summary>A validated prize entry. Immutable. A salvage entry has <see cref="Salvage"/> set and its material pool in <see cref="Wcids"/>.</summary>
    public sealed class ThreadEncounterPrizeEntry
    {
        public ThreadEncounterPrizeEntry(string id, IReadOnlyList<uint> wcids, int qty, double weight, ThreadEncounterPrizeSalvage salvage = null, bool fiber = false,
            bool pattern = false)
        {
            Id = id;
            Wcids = wcids ?? Array.Empty<uint>();
            Qty = qty;
            Weight = weight;
            Salvage = salvage;
            IsFiber = fiber && salvage == null;
            IsPattern = pattern && salvage == null && !IsFiber;
        }

        /// <summary>
        /// A Pattern entry: <see cref="Wcids"/> is the Pattern weenie alone, and the one object is built by
        /// KnotDrops.PatternFactory for a pinnacle drawn uniformly from the pool the Knots drop rolls from
        /// (KnotPinnacles.Available). Unavailable when that pool is empty.
        /// </summary>
        public bool IsPattern { get; }

        public string Id { get; }
        public IReadOnlyList<uint> Wcids { get; }

        /// <summary>The stack size for an item entry; the number of Fibers for a Fiber entry; 1 for a salvage entry.</summary>
        public int Qty { get; }
        public double Weight { get; }
        public ThreadEncounterPrizeSalvage Salvage { get; }
        public bool IsSalvage => Salvage != null;

        /// <summary>
        /// A Fiber entry: <see cref="Wcids"/> is the Fiber weenie alone (so a missing weenie renormalises like any other),
        /// and each object is built by KnotDrops.FiberFactory at the run's Knot level. Unavailable on a run with no level.
        /// </summary>
        public bool IsFiber { get; }
    }

    /// <summary>One roll's result: the entry and the wcid of every object it gives (one for an item entry, one per bag for a salvage entry).</summary>
    public sealed class ThreadEncounterPrizePick
    {
        public ThreadEncounterPrizePick(ThreadEncounterPrizeEntry entry, IReadOnlyList<uint> wcids, int fiberLevel = 0, Knots.PinnacleDef pattern = null)
        {
            Entry = entry;
            Wcids = wcids ?? Array.Empty<uint>();
            FiberLevel = fiberLevel;
            Pattern = pattern;
        }

        public ThreadEncounterPrizeEntry Entry { get; }

        /// <summary>A Pattern pick's pinnacle; null otherwise.</summary>
        public Knots.PinnacleDef Pattern { get; }

        /// <summary>A Fiber pick's Knot level (the run's, as the Knots drop roll reads it); 0 otherwise.</summary>
        public int FiberLevel { get; }

        /// <summary>One wcid per object to deliver.</summary>
        public IReadOnlyList<uint> Wcids { get; }

        /// <summary>The first (for an item entry, the only) wcid.</summary>
        public uint Wcid => Wcids.Count > 0 ? Wcids[0] : 0u;

        public int Qty => Entry.Qty;
    }

    /// <summary>
    /// The shared encounter prize table (encounters.json "prize", Docs/Threads/ENCOUNTERS.md "Prize"). Immutable, built
    /// by <see cref="Parse"/> inside ThreadEncounterStore.Parse, so /dd reload re-reads it with the rest of the file.
    /// Fail-open like the rest of encounters.json: an absent section is an empty table (no prize, no diagnostic), a
    /// section that is not an object is one diagnostic and empty, and a bad entry costs only itself.
    /// </summary>
    public sealed class ThreadEncounterPrizeTable
    {
        public const int MaxQty = 10000;

        /// <summary>The most Fibers one fiber entry gives (each is its own object; a Fiber does not stack).</summary>
        public const int MaxFiberQty = 10;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static readonly ThreadEncounterPrizeTable Empty = new ThreadEncounterPrizeTable(Array.Empty<ThreadEncounterPrizeEntry>());

        public ThreadEncounterPrizeTable(IReadOnlyList<ThreadEncounterPrizeEntry> entries)
        {
            Entries = entries ?? Array.Empty<ThreadEncounterPrizeEntry>();
        }

        /// <summary>Every valid entry in file order, weight-0 ones included.</summary>
        public IReadOnlyList<ThreadEncounterPrizeEntry> Entries { get; }

        /// <summary>
        /// encounters.json "prize" -> the table. Undefined or null is "no section": empty, no diagnostic. Diagnostics are
        /// prefixed with <paramref name="fileName"/>. Pure.
        /// </summary>
        public static ThreadEncounterPrizeTable Parse(JsonElement prize, string fileName, List<string> diagnostics)
        {
            diagnostics ??= new List<string>();

            if (prize.ValueKind == JsonValueKind.Undefined || prize.ValueKind == JsonValueKind.Null)
                return Empty;

            if (prize.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add($"{fileName}: prize must be an object, not {prize.ValueKind}; no encounter prize");
                return Empty;
            }

            if (!prize.TryGetProperty("entries", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add($"{fileName}: prize.entries must be an array; no encounter prize");
                return Empty;
            }

            var entries = new List<ThreadEncounterPrizeEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var i = 0;

            foreach (var row in rows.EnumerateArray())
            {
                var label = $"prize.entries[{i++}]";
                ThreadEncounterPrizeEntryDef def;

                try
                {
                    def = row.ValueKind == JsonValueKind.Object ? row.Deserialize<ThreadEncounterPrizeEntryDef>(JsonOptions) : null;
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
                {
                    diagnostics.Add($"{fileName}: {label} is malformed - {ex.Message}; skipped");
                    continue;
                }

                if (def == null)
                {
                    diagnostics.Add($"{fileName}: {label} is not an object; skipped");
                    continue;
                }

                if (def.Id != null)
                    label = $"prize entry '{def.Id}'";

                var problem = Validate(def);

                if (problem != null)
                {
                    diagnostics.Add($"{fileName}: {label}: {problem}; skipped");
                    continue;
                }

                if (!ids.Add(def.Id))
                {
                    diagnostics.Add($"{fileName}: duplicate prize entry id '{def.Id}'; the first stands");
                    continue;
                }

                if (def.Salvage != null)
                {
                    var s = def.Salvage;
                    entries.Add(new ThreadEncounterPrizeEntry(def.Id, s.Wcids.Distinct().ToList(), 1, def.Weight,
                        new ThreadEncounterPrizeSalvage(s.Bags.Min, s.Bags.Max, s.Structure, s.Workmanship)));
                }
                else if (def.Fiber != null)
                    entries.Add(new ThreadEncounterPrizeEntry(def.Id, new[] { Knots.KnotItems.FiberWcid }, def.Fiber.Qty, def.Weight, fiber: true));
                else if (def.Pattern != null)
                    entries.Add(new ThreadEncounterPrizeEntry(def.Id, new[] { Knots.KnotItems.PatternWcid }, 1, def.Weight, pattern: true));
                else
                    entries.Add(new ThreadEncounterPrizeEntry(def.Id, def.Wcids.Distinct().ToList(), def.Qty, def.Weight));
            }

            return entries.Count == 0 ? Empty : new ThreadEncounterPrizeTable(entries);
        }

        /// <summary>Why an entry is unusable, or null when it is valid. Pure.</summary>
        internal static string Validate(ThreadEncounterPrizeEntryDef def)
        {
            if (string.IsNullOrEmpty(def.Id) || !def.Id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                return $"id '{def.Id}' must match [a-z0-9_]+";

            if (double.IsNaN(def.Weight) || double.IsInfinity(def.Weight) || def.Weight < 0)
                return $"weight {def.Weight.ToString(CultureInfo.InvariantCulture)} must be a finite number >= 0";

            if ((def.Salvage != null ? 1 : 0) + (def.Fiber != null ? 1 : 0) + (def.Pattern != null ? 1 : 0) > 1)
                return "an entry is at most one of salvage, fiber or pattern";

            if (def.Pattern != null)
            {
                if (def.Pattern.Value.ValueKind != JsonValueKind.Object || def.Pattern.Value.EnumerateObject().Any())
                    return "pattern must be an empty object {}";

                if (def.Wcids != null && def.Wcids.Count > 0)
                    return "a pattern entry names no wcids (it is always the Pattern, wcid " + Knots.KnotItems.PatternWcid.ToString(CultureInfo.InvariantCulture) + ")";

                if (def.Qty != 1)
                    return "a pattern entry gives one Pattern; qty must be 1 or absent";

                return null;
            }

            if (def.Salvage != null)
                return ValidateSalvage(def);

            if (def.Fiber != null)
            {
                if (def.Wcids != null && def.Wcids.Count > 0)
                    return "a fiber entry names no wcids (it is always the Fiber, wcid " + Knots.KnotItems.FiberWcid.ToString(CultureInfo.InvariantCulture) + ")";

                if (def.Qty != 1)
                    return "qty goes inside fiber, not beside it";

                if (def.Fiber.Qty < 1 || def.Fiber.Qty > MaxFiberQty)
                    return $"fiber.qty {def.Fiber.Qty} must be 1 to {MaxFiberQty}";

                return null;
            }

            if (def.Wcids == null || def.Wcids.Count == 0)
                return "wcids must list at least one wcid";

            if (def.Wcids.Any(w => w == 0))
                return "wcids must be positive";

            if (def.Qty < 1 || def.Qty > MaxQty)
                return $"qty {def.Qty} must be 1 to {MaxQty}";

            return null;
        }

        private static string ValidateSalvage(ThreadEncounterPrizeEntryDef def)
        {
            var s = def.Salvage;

            if (def.Wcids != null && def.Wcids.Count > 0)
                return "a salvage entry lists its wcids inside salvage, not beside it";

            if (def.Qty != 1)
                return "qty does not apply to a salvage entry (its bag count is salvage.bags)";

            if (s.Wcids == null || s.Wcids.Count == 0)
                return "salvage.wcids must list at least one wcid";

            if (s.Wcids.Any(w => w == 0))
                return "salvage.wcids must be positive";

            if (s.Bags == null || s.Bags.Min < 1 || s.Bags.Max < s.Bags.Min || s.Bags.Max > ThreadEncounterPrizeSalvage.MaxBags)
                return $"salvage.bags must be {{ min, max }} with 1 <= min <= max <= {ThreadEncounterPrizeSalvage.MaxBags}";

            if (s.Structure < 1 || s.Structure > ThreadEncounterPrizeSalvage.MaxStructure)
                return $"salvage.structure {s.Structure} must be 1 to {ThreadEncounterPrizeSalvage.MaxStructure}";

            if (s.Workmanship < 1 || s.Workmanship > ThreadEncounterPrizeSalvage.MaxWorkmanship)
                return $"salvage.workmanship {s.Workmanship} must be 1 to {ThreadEncounterPrizeSalvage.MaxWorkmanship}";

            return null;
        }
    }

    /// <summary>
    /// The shared encounter prize (owner rulings 2026-11-09, Docs/Threads/ENCOUNTERS.md "Prize"): one table for ANY
    /// encounter victory. Defense grants it to every member inside when the pillar holds; Jump to each finisher, once
    /// each; a future encounter (the Craftwork Golem) calls <see cref="Grant"/> the same way. The Shrine has no victory
    /// and grants none.
    ///
    /// The roll is <see cref="Roll"/>, pure over a seeded Random: an entry none of whose wcids has a weenie is skipped
    /// and the remaining weights renormalise; a multi-wcid item entry picks uniformly among the wcids that exist, and a
    /// salvage entry (owner ruling 2026-11-10) draws a bag count and then one material per bag uniformly among the bag
    /// wcids that exist. The world database caches a missing weenie as missing, so an entry authored ahead of its item
    /// starts dropping after the next server restart, or a live /import-sql weenie (or /clearcache), once the weenie exists.
    ///
    /// Threading. <see cref="Grant"/> may be called from any landblock thread (the hooks fire on the pillar's or the
    /// Jump NPC's landblock). It only rolls, then queues the delivery onto the PLAYER's own action queue, which
    /// Player_Tick drains on the player's landblock thread; every inventory and world mutation happens there. A player
    /// who logged off before that runs is never ticked again, so nothing is created for them; one who left the run in
    /// between is skipped by the re-check at delivery.
    /// </summary>
    public static class ThreadEncounterPrize
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string FullPackLine = "Your pack is full, so your prize was placed at your feet.";

        /// <summary>A salvage bag weenie's name prefix ("Salvaged Amethyst"); the rest is the material the chat line shows.</summary>
        public const string SalvagedPrefix = "Salvaged ";

        /// <summary>Does a weenie exist for this wcid? The world database's weenie cache, which also caches a miss.</summary>
        internal static Func<uint, bool> WeenieExists = wcid => DatabaseManager.World.GetCachedWeenie(wcid) != null;

        /// <summary>The prize table: the live Thread store's.</summary>
        private static readonly Func<ThreadEncounterPrizeTable> TableSource = () => ThreadDungeonManager.Store?.Encounters?.Prize ?? ThreadEncounterPrizeTable.Empty;

        /// <summary>The pinnacles a prize Pattern may be for: the same pool the Knots drop rolls from (KnotDrops.RollFor).</summary>
        private static readonly Func<IReadOnlyList<Knots.PinnacleDef>> PatternPool = () => Knots.KnotPinnacles.Available(ThreadDungeonManager.Store);

        /// <summary>
        /// The Knots inputs to the roll for <paramref name="run"/>. Code review 2026-11-11: the prize's Fiber and Pattern
        /// follow the Knot drop's own gate (KnotDrops.KnotsAvailableFor: not a Thread Guide run, a stamped Knot level, pooled
        /// loot, something spawned, dynamic_dungeons_fiber_enabled on). Closed: no level and an empty pool, so both entries
        /// leave the roll and the rest renormalise. Open: the run's Knot level (the stamp KnotDrops.RollFor reads) and the
        /// Knots drop's pinnacle pool (enabled pinnacles whose encounter is registered).
        /// </summary>
        internal static (int? FiberLevel, IReadOnlyList<Knots.PinnacleDef> PatternPool) KnotsInputs(ThreadDungeonRun run)
            => Knots.KnotDrops.KnotsAvailableFor(run)
                ? (run.KnotLevel, PatternPool())
                : (null, Array.Empty<Knots.PinnacleDef>());

        /// <summary>The object factory: WorldObjectFactory.</summary>
        internal static Func<uint, WorldObject> CreateObject = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);

        /// <summary>The wcids already warned about as missing (one warning per wcid per process).</summary>
        private static readonly ConcurrentDictionary<uint, byte> warnedMissing = new ConcurrentDictionary<uint, byte>();

        /// <summary>True exactly once per wcid per process: the one-time missing-weenie warning latch.</summary>
        internal static bool FirstMissingWarning(uint wcid) => warnedMissing.TryAdd(wcid, 0);

        /// <summary>
        /// One prize roll. An entry with weight 0, or none of whose wcids exists (<paramref name="exists"/>), is left
        /// out and the remaining weights renormalise; each missing wcid seen is added to <paramref name="missing"/>. One
        /// rng.NextDouble picks the entry by weight. Then, for an item entry with more than one available wcid, one
        /// rng.Next picks among them uniformly. For a salvage entry, one rng.Next(min, max + 1) draws the bag count,
        /// and each bag then picks its material with one rng.Next over the available pool (none when the pool has one
        /// wcid). A Fiber entry is available only with a <paramref name="fiberLevel"/> (the run's Knot level, null when the
        /// run never stamped one) and its Fiber weenie; it gives qty Fibers at that level. Null when nothing is available.
        /// A Pattern entry is available only with a non-empty <paramref name="patternPool"/> (the pinnacles the Knots drop
        /// can roll, KnotPinnacles.Available) and its Pattern weenie; one more rng.Next then picks the pinnacle uniformly.
        /// Pure over <paramref name="rng"/>.
        /// </summary>
        public static ThreadEncounterPrizePick Roll(IReadOnlyList<ThreadEncounterPrizeEntry> entries, Func<uint, bool> exists, Random rng, ICollection<uint> missing = null,
            int? fiberLevel = null, IReadOnlyList<Knots.PinnacleDef> patternPool = null)
        {
            if (entries == null || exists == null || rng == null)
                return null;

            var available = new List<(ThreadEncounterPrizeEntry Entry, List<uint> Wcids)>();
            var total = 0.0;

            foreach (var entry in entries)
            {
                if (entry == null || !(entry.Weight > 0))
                    continue;

                // No Knot level on the run: the Fiber entry leaves the roll like a missing weenie, and the rest renormalise.
                if (entry.IsFiber && !(fiberLevel > 0))
                    continue;

                // No pinnacle the Knots drop could roll: the Pattern entry leaves the roll the same way.
                if (entry.IsPattern && (patternPool == null || patternPool.Count == 0))
                    continue;

                var wcids = new List<uint>();

                foreach (var wcid in entry.Wcids)
                {
                    if (exists(wcid))
                        wcids.Add(wcid);
                    else
                        missing?.Add(wcid);
                }

                if (wcids.Count == 0)
                    continue;

                available.Add((entry, wcids));
                total += entry.Weight;
            }

            if (available.Count == 0 || !(total > 0))
                return null;

            var r = rng.NextDouble() * total;
            var chosen = available[available.Count - 1];
            var cumulative = 0.0;

            foreach (var a in available)
            {
                cumulative += a.Entry.Weight;

                if (r < cumulative)
                {
                    chosen = a;
                    break;
                }
            }

            if (chosen.Entry.IsPattern)
                return new ThreadEncounterPrizePick(chosen.Entry, new[] { chosen.Wcids[0] }, 0,
                    patternPool.Count == 1 ? patternPool[0] : patternPool[rng.Next(patternPool.Count)]);

            if (chosen.Entry.IsFiber)
                return new ThreadEncounterPrizePick(chosen.Entry, Enumerable.Repeat(chosen.Wcids[0], Math.Max(1, chosen.Entry.Qty)).ToArray(), fiberLevel.Value);

            if (!chosen.Entry.IsSalvage)
            {
                var pick = chosen.Wcids.Count == 1 ? chosen.Wcids[0] : chosen.Wcids[rng.Next(chosen.Wcids.Count)];
                return new ThreadEncounterPrizePick(chosen.Entry, new[] { pick });
            }

            var salvage = chosen.Entry.Salvage;
            var count = rng.Next(salvage.BagsMin, salvage.BagsMax + 1);
            var bags = new uint[count];

            for (var i = 0; i < count; i++)
                bags[i] = chosen.Wcids.Count == 1 ? chosen.Wcids[0] : chosen.Wcids[rng.Next(chosen.Wcids.Count)];

            return new ThreadEncounterPrizePick(chosen.Entry, bags);
        }


        /// <summary>The winner's line: "Encounter prize: {qty} {name}." (qty omitted at 1), plus the full-pack line when it went to the ground. Pure.</summary>
        public static string DeliveryText(int qty, string name, bool atFeet)
        {
            var line = qty > 1
                ? $"Encounter prize: {qty.ToString(CultureInfo.InvariantCulture)} {name}."
                : $"Encounter prize: {name}.";

            return atFeet ? $"{line} {FullPackLine}" : line;
        }

        /// <summary>
        /// The gamble suffix: with <paramref name="bonusWaves"/> above 0 (only the Gambler's Cache passes any), the line
        /// gets " (deepened once)" (1) or " (deepened {k} times)" (2 or more) after its prize sentence, BEFORE the full-pack
        /// sentence when there is one; at 0 it is returned unchanged, so every other encounter's line is as before. Pure.
        /// </summary>
        internal static string WithDeepened(string line, int bonusWaves)
        {
            if (bonusWaves <= 0 || line == null)
                return line;

            var suffix = bonusWaves == 1 ? " (deepened once)" : $" (deepened {bonusWaves.ToString(CultureInfo.InvariantCulture)} times)";
            var pack = " " + FullPackLine;

            return line.EndsWith(pack, StringComparison.Ordinal)
                ? line.Substring(0, line.Length - pack.Length) + suffix + pack
                : line + suffix;
        }

        /// <summary>
        /// An item, Fiber or Pattern winner's line. The count is the stack size for an item entry, and the number of
        /// Fibers actually delivered for a Fiber entry (each Fiber is its own object). Pure.
        /// </summary>
        internal static string ItemDeliveryText(ThreadEncounterPrizeEntry entry, int stackQty, int delivered, string name, bool atFeet)
            => DeliveryText(entry != null && entry.IsFiber ? delivered : stackQty, name, atFeet);

        /// <summary>
        /// The salvage winner's line: "Encounter prize: 3 bags of salvage (Amethyst x2, Tourmaline)." The materials are
        /// grouped, ordered alphabetically (ordinal), and counted with "xN" when more than one. "1 bag" for one. The
        /// full-pack line is added once when any bag went to the ground. Pure.
        /// </summary>
        public static string SalvageDeliveryText(IReadOnlyCollection<string> materials, bool anyAtFeet)
        {
            var list = materials ?? Array.Empty<string>();
            var groups = list.GroupBy(m => m, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count().ToString(CultureInfo.InvariantCulture)}" : g.Key);

            var count = list.Count;
            var line = $"Encounter prize: {count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "bag" : "bags")} of salvage ({string.Join(", ", groups)}).";

            return anyAtFeet ? $"{line} {FullPackLine}" : line;
        }

        /// <summary>The material a salvage bag weenie's name names: "Salvaged Green Jade" -> "Green Jade"; a name without the prefix is returned as is. Pure.</summary>
        public static string SalvageMaterialLabel(string weenieName)
        {
            if (string.IsNullOrEmpty(weenieName))
                return "Unknown";

            return weenieName.StartsWith(SalvagedPrefix, StringComparison.Ordinal) && weenieName.Length > SalvagedPrefix.Length
                ? weenieName.Substring(SalvagedPrefix.Length)
                : weenieName;
        }

        /// <summary>
        /// A full salvage bag's quality fields, built the way AdminCommands.HandleCISalvage builds one: NumItemsInMaterial
        /// is the workmanship (one "item" per workmanship point), ItemWorkmanship is workmanship x NumItemsInMaterial, so
        /// the displayed Workmanship (ItemWorkmanship / NumItemsInMaterial) is exactly the workmanship, a whole number as
        /// Player.RoundBagWorkmanship leaves a bag the player path fills. Name is "Salvage (structure)". Pure.
        /// </summary>
        public static (string Name, ushort Structure, int ItemWorkmanship, int NumItemsInMaterial) SalvageBagFields(int structure, int workmanship)
        {
            var nim = Math.Max(1, workmanship);
            return ($"Salvage ({structure.ToString(CultureInfo.InvariantCulture)})", (ushort)structure, workmanship * nim, nim);
        }

        /// <summary>
        /// The gamble quantity rule. A single valuable (a Pattern, a Fiber, or an item entry whose Qty is 1) never grows
        /// and stays <paramref name="baseQty"/>. Anything else grows by half its base, integer floor, per wave:
        /// baseQty + waves * (baseQty / 2), so 5 grows 5, 7, 9, 11, 13; 2 grows 2, 3, 4, 5, 6; 1 never grows. Waves are clamped to [0, MaxBonusWaves] and the arithmetic cannot overflow. Pure.
        /// </summary>
        internal static int GambleQuantity(int baseQty, int waves, bool singleValuable)
        {
            if (singleValuable)
                return baseQty;

            var grown = (long)baseQty + (long)Math.Clamp(waves, 0, MaxBonusWaves) * (baseQty / 2);
            return (int)Math.Min(grown, int.MaxValue);
        }

        /// <summary>The Gambler's most bonus waves; <see cref="GambleQuantity"/> clamps waves to [0, this].</summary>
        internal const int MaxBonusWaves = 4;

        /// <summary>The most stacks one item entry is delivered as, however large the quantity and small the max stack.</summary>
        internal const int MaxStacksPerEntry = 20;

        /// <summary>
        /// The stack sizes a quantity is delivered as: one stack when it fits, otherwise as many as it takes (each at most
        /// <paramref name="maxStack"/>, the remainder last). A non-stackable item, or one with no max stack, is a single 1
        /// (one stack of 1). At most <see cref="MaxStacksPerEntry"/> stacks: the rest of the quantity is dropped (the caller logs it). Pure.
        /// </summary>
        internal static IReadOnlyList<int> StackSizes(int qty, bool stackable, int? maxStack)
        {
            if (!stackable || maxStack == null || maxStack < 1)
                return new[] { 1 };

            var max = maxStack.Value;
            var left = Math.Max(1, qty);
            var sizes = new List<int>();

            while (left > 0 && sizes.Count < MaxStacksPerEntry)
            {
                var take = Math.Min(left, max);
                sizes.Add(take);
                left -= take;
            }

            return sizes;
        }

        /// <summary>
        /// THE entry point: one prize for <paramref name="player"/> for winning an encounter of kind
        /// <paramref name="encounterKind"/> in <paramref name="run"/>. Rolls here (any thread) and queues the delivery
        /// onto the player's own landblock thread. False, queueing nothing, when the run's
        /// dynamic_dungeons_encounter_prize_enabled stamp is off, the table has nothing available, or either argument
        /// is null. Never throws.
        /// </summary>
        public static bool Grant(ThreadDungeonRun run, Player player, string encounterKind)
            => Grant(run, player, encounterKind, 0);

        /// <summary>
        /// <see cref="Grant(ThreadDungeonRun, Player, string)"/> with <paramref name="bonusWaves"/> extra waves of
        /// gamble growth (<see cref="GambleQuantity"/>) applied to the quantities the pick gives. 0 is the plain prize.
        /// </summary>
        public static bool Grant(ThreadDungeonRun run, Player player, string encounterKind, int bonusWaves)
        {
            if (run == null || player == null)
                return false;

            try
            {
                if (!run.EncounterPrizeEnabled)
                    return false;

                var missing = new List<uint>();
                var (fiberLevel, patternPool) = KnotsInputs(run);
                var pick = Roll(TableSource()?.Entries, WeenieExists, Random.Shared, missing, fiberLevel, patternPool);

                foreach (var wcid in missing)
                {
                    if (FirstMissingWarning(wcid))
                        log.Warn($"[DYNDUNGEON] encounter prize: wcid {wcid} has no weenie; skipped until it exists (weights renormalised)");
                }

                if (pick == null)
                {
                    log.Warn($"[DYNDUNGEON] {run} {encounterKind} prize for {player.Name}: the prize table has nothing available; no prize");
                    return false;
                }

                new ActionChain(player, () => Deliver(run, player, pick, encounterKind, bonusWaves)).EnqueueChain();
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} {encounterKind} prize for {player.Name} threw; no prize", ex);
                return false;
            }
        }

        /// <summary>
        /// Builds every object of the pick, unplaced. A Fiber or Pattern goes through the Knots seam; a salvage entry gives
        /// one bag per pick wcid plus the gamble's extra bags (each extra bag draws its own material from the entry's wcid
        /// pool), each filled with its quality fields and labelled by material; an item entry gives its quantity
        /// (<see cref="GambleQuantity"/>) as stacks of at most the item's MaxStackSize. Label is the name the chat line
        /// uses. With <paramref name="bonusWaves"/> 0 this is exactly what the delivery has always built, except that a
        /// quantity above MaxStackSize is now several stacks, not one clamped stack. A wcid that fails to create is
        /// logged and left out. Never throws on a creation failure.
        /// </summary>
        internal static List<(WorldObject Item, string Label)> BuildObjects(ThreadEncounterPrizePick pick, int bonusWaves)
            => BuildObjects(pick, bonusWaves, Random.Shared);

        internal static List<(WorldObject Item, string Label)> BuildObjects(ThreadEncounterPrizePick pick, int bonusWaves, Random rng)
        {
            var built = new List<(WorldObject Item, string Label)>();

            if (pick?.Entry == null)
                return built;

            var salvage = pick.Entry.Salvage;
            var fiber = pick.Entry.IsFiber;
            var wcids = pick.Wcids.ToList();

            if (salvage != null)
            {
                var extra = GambleQuantity(wcids.Count, bonusWaves, false) - wcids.Count;

                if (extra > 0)
                {
                    var pool = pick.Entry.Wcids.Where(w => WeenieExists(w)).ToList();

                    if (pool.Count == 0)
                        pool = pick.Entry.Wcids.ToList();

                    for (var i = 0; i < extra && pool.Count > 0; i++)
                        wcids.Add(pool.Count == 1 ? pool[0] : pool[rng.Next(pool.Count)]);
                }
            }

            foreach (var wcid in wcids)
            {
                // A Fiber is built by the Knots seam, which stamps KnotLevel and the "Fiber (Level N)" name.
                // A Pattern by the Knots seam too, which stamps PinnacleKey and the "Pattern: <name>" name.
                var item = fiber ? Knots.KnotDrops.FiberFactory(pick.FiberLevel)
                    : pick.Entry.IsPattern ? Knots.KnotDrops.PatternFactory(pick.Pattern)
                    : CreateObject(wcid);

                if (item == null)
                {
                    log.Warn($"[DYNDUNGEON] encounter prize {pick.Entry.Id}: wcid {wcid} did not create; skipped");
                    continue;
                }

                var label = item.Name;
                var extraStacks = new List<(WorldObject Item, string Label)>();

                if (salvage != null)
                {
                    label = SalvageMaterialLabel(item.Name);

                    var fields = SalvageBagFields(salvage.Structure, salvage.Workmanship);
                    item.Name = fields.Name;
                    item.Structure = fields.Structure;
                    item.ItemWorkmanship = fields.ItemWorkmanship;
                    item.NumItemsInMaterial = fields.NumItemsInMaterial;
                }
                else if (!fiber && !pick.Entry.IsPattern)
                {
                    var qty = GambleQuantity(pick.Qty, bonusWaves, pick.Qty == 1);
                    var sizes = StackSizes(qty, item is Stackable, item.MaxStackSize);

                    if (item is Stackable && item.MaxStackSize > 0 && sizes.Sum() < qty)
                        log.Warn($"[DYNDUNGEON] encounter prize {pick.Entry.Id}: wcid {wcid} asked for {qty} but the {MaxStacksPerEntry}-stack cap delivers {sizes.Sum()}; remainder dropped");

                    if (sizes[0] > 1)
                        item.SetStackSize(sizes[0]);

                    for (var s = 1; s < sizes.Count; s++)
                    {
                        var more = CreateObject(wcid);

                        if (more == null)
                        {
                            log.Warn($"[DYNDUNGEON] encounter prize {pick.Entry.Id}: wcid {wcid} did not create another stack; skipped");
                            continue;
                        }

                        more.SetStackSize(sizes[s]);
                        extraStacks.Add((more, more.Name));
                    }
                }

                built.Add((item, label));
                built.AddRange(extraStacks);
            }

            return built;
        }

        /// <summary>
        /// The player's landblock thread (their action queue): builds every object of the pick, packs each or drops it
        /// at their feet, then plays the level-up effect once and sends one line. Never throws.
        /// </summary>
        private static void Deliver(ThreadDungeonRun run, Player player, ThreadEncounterPrizePick pick, string encounterKind, int bonusWaves)
        {
            try
            {
                if (player.IsDestroyed || player.Session == null || !ReferenceEquals(PlayerManager.GetOnlinePlayer(player.Guid.Full), player))
                {
                    log.Info($"[DYNDUNGEON] {run} {encounterKind} prize for {player.Name}: no longer online; skipped");
                    return;
                }

                if (!ThreadRunPresence.IsMemberInside(run, player))
                {
                    log.Info($"[DYNDUNGEON] {run} {encounterKind} prize for {player.Name}: no longer inside the run; skipped");
                    return;
                }

                var salvage = pick.Entry.Salvage;
                var materials = new List<string>();
                var anyAtFeet = false;
                var qty = 0;
                string name = null;

                var fiber = pick.Entry.IsFiber;
                var plain = salvage == null && !fiber && !pick.Entry.IsPattern;

                var objects = BuildObjects(pick, bonusWaves);
                var wanted = plain ? 1 : pick.Wcids.Count;

                if (objects.Count < wanted)
                    log.Warn($"[DYNDUNGEON] {run} {encounterKind} prize for {player.Name}: {pick.Entry.Id} built {objects.Count} of {wanted} object(s); the rest did not create");

                foreach (var (item, label) in objects)
                {
                    var wcid = item.WeenieClassId;

                    // Read before placing: a stackable that merges into a pack stack is consumed by the merge.
                    var stack = item.StackSize ?? 1;

                    if (!TryPlace(run, player, item, wcid, encounterKind, out var atFeet))
                        continue;

                    anyAtFeet |= atFeet;
                    materials.Add(label);

                    if (plain)
                        qty += stack;

                    if (salvage == null)
                        name = label;
                }

                if (materials.Count == 0)
                {
                    log.Warn($"[DYNDUNGEON] {run} {encounterKind} prize for {player.Name}: nothing of {pick.Entry.Id} could be delivered; no prize");
                    return;
                }

                if (!plain)
                    qty = materials.Count;

                var text = WithDeepened(salvage != null ? SalvageDeliveryText(materials, anyAtFeet) : ItemDeliveryText(pick.Entry, qty, materials.Count, name, anyAtFeet), bonusWaves);

                // One effect and one line per grant, however many objects it gave.
                player.PlayParticleEffect(PlayScript.AetheriaLevelUp, player.Guid);
                player.Session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));

                var what = salvage != null ? $"{materials.Count} salvage bag(s) ({string.Join(", ", materials)})" : $"{qty} x {name}";
                run.RecordEncounterOutcome($"{encounterKind} prize: {player.Name} got {what} ({pick.Entry.Id}){(anyAtFeet ? " partly at their feet" : "")}");
                log.Info($"[DYNDUNGEON] {run} {encounterKind} prize: {player.Name} got {what} ({pick.Entry.Id}){(anyAtFeet ? " partly at their feet (pack full)" : "")}");
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} {encounterKind} prize delivery for {player.Name} threw", ex);
            }
        }

        /// <summary>
        /// One object into the winner's pack, else at their feet. False when it could not be placed anywhere; it is then
        /// destroyed and logged, never orphaned. Never throws.
        /// </summary>
        private static bool TryPlace(ThreadDungeonRun run, Player player, WorldObject item, uint wcid, string encounterKind, out bool atFeet)
        {
            atFeet = false;
            var name = item.Name;

            try
            {
                // No burden check (ruling 2026-11-09): a winner over burden with a free slot still gets the prize in the
                // pack, so the ground drop below means no free slot at all.
                if (player.TryCreateInInventoryWithNetworking(item, out _, burdenCheck: false))
                    return true;

                // The corpse-overflow path (Creature.DropLootInWorld): onto this landblock at the winner's feet. It
                // destroys (and logs) an item the landblock refuses, so nothing is left in no container.
                var location = player.Location;

                if (location == null || !Creature.DropLootInWorld(new[] { item }, location, player.Guid))
                {
                    if (location == null && !item.IsDestroyed)
                        item.Destroy();

                    log.Warn($"[DYNDUNGEON] {run} {encounterKind} prize for {player.Name}: pack full and the ground refused {name} (wcid {wcid}); destroyed");
                    return false;
                }

                atFeet = true;
                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} {encounterKind} prize for {player.Name}: placing {name} (wcid {wcid}) threw", ex);

                // Not packed (no container) and not on a landblock: DropLootInWorld sets Location before AddObject, so a
                // throw there leaves a located item that is in no world. Destroyed rather than orphaned.
                try
                {
                    if (!item.IsDestroyed && item.ContainerId == null && item.CurrentLandblock == null)
                        item.Destroy();
                }
                catch { }

                return false;
            }
        }
    }
}
