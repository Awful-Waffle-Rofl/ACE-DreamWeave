using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.Frays
{
    public enum FraysRole { Swarm, Standard, Elite, Snarl }

    public enum FraysFightKind { Melee, Caster, Archer }

    /// <summary>One retail creature a Threads family can field in the Frays, with how it fights and how often it comes up.</summary>
    public sealed class FraysFamilyMember
    {
        public uint Wcid { get; init; }
        public string Name { get; init; }
        public int Level { get; init; }
        public FraysFightKind Kind { get; init; }
        public double Weight { get; init; }
    }

    /// <summary>
    /// One creature family. Either a Threads family (<see cref="Table"/>: the World Events species table Threads draws from,
    /// fielding its retail creatures weighted towards the ones that chase you), or one of the five original DreamWeave
    /// families with four fixed creatures each (used when the species tables are not loaded, and in unit tests).
    /// </summary>
    public sealed class FraysFamily
    {
        public string Key { get; init; }
        public string Name { get; init; }
        public CreatureType Type { get; init; }
        public uint Swarm { get; init; }
        public uint Standard { get; init; }
        public uint Elite { get; init; }
        public uint Snarl { get; init; }
        public string Resists { get; init; }
        public string WeakTo { get; init; }
        public string Deals { get; init; }

        /// <summary>The Threads species table this family comes from; null for an original DreamWeave family.</summary>
        public SpeciesTableDef Table { get; init; }

        /// <summary>Its creatures for swarm and standard slots (Threads families).</summary>
        public IReadOnlyList<FraysFamilyMember> Trash { get; init; } = Array.Empty<FraysFamilyMember>();

        /// <summary>Its creatures for elite and Snarl slots (Threads families; the trash pool when it has none).</summary>
        public IReadOnlyList<FraysFamilyMember> Elites { get; init; } = Array.Empty<FraysFamilyMember>();

        /// <summary>How often this family is picked for a week: its melee share, casters and archers counting for less.</summary>
        public double Weight { get; init; } = 1;

        public int Melee { get; init; }
        public int Casters { get; init; }
        public int Archers { get; init; }

        /// <summary>
        /// True when <paramref name="wcid"/> fights by casting: its member kind in this family, else what its weenie says.
        /// </summary>
        public bool IsCaster(uint wcid)
        {
            var member = Trash.FirstOrDefault(m => m.Wcid == wcid) ?? Elites.FirstOrDefault(m => m.Wcid == wcid);

            if (member != null)
                return member.Kind == FraysFightKind.Caster;

            return wcid != 0 && FraysFamilies.KindOf(wcid) == FraysFightKind.Caster;
        }

        /// <summary>
        /// The creature for a role. With <paramref name="castersAllowed"/> false (below caster_min_heat) caster members are
        /// left out, unless the pool has nothing else.
        /// </summary>
        /// <summary>Every pack creature casts: below caster_min_heat they come up with empty spell books and fight in melee.</summary>
        public bool CasterOnlyPacks => Table != null && Trash.Count > 0 && Trash.All(m => m.Kind == FraysFightKind.Caster);

        public uint WcidFor(FraysRole role, bool castersAllowed)
        {
            if (castersAllowed || Table == null)
                return WcidFor(role);

            var pool = (role == FraysRole.Elite || role == FraysRole.Snarl) && Elites.Count > 0 ? Elites : Trash;
            var noCasters = pool.Where(m => m.Kind != FraysFightKind.Caster).ToList();

            // an elite or Snarl pool of casters only: a pack creature of the family that does not cast steps up
            if (noCasters.Count == 0 && !ReferenceEquals(pool, Trash))
                noCasters = Trash.Where(m => m.Kind != FraysFightKind.Caster).ToList();

            if (noCasters.Count == 0)
                return WcidFor(role);

            if (role == FraysRole.Snarl)
                return noCasters.OrderByDescending(m => m.Level).ThenBy(m => m.Wcid).First().Wcid;

            return FraysFamilies.PickWeighted(noCasters, ThreadSafeRandom.Next(0.0f, 1.0f));
        }

        public uint WcidFor(FraysRole role)
        {
            if (Table == null)
            {
                return role switch
                {
                    FraysRole.Swarm => Swarm,
                    FraysRole.Elite => Elite,
                    FraysRole.Snarl => Snarl,
                    _ => Standard,
                };
            }

            var pool = (role == FraysRole.Elite || role == FraysRole.Snarl) && Elites.Count > 0 ? Elites : Trash;

            // A Snarl is the family's best showing: its highest-level elite.
            if (role == FraysRole.Snarl)
                return pool.OrderByDescending(m => m.Level).ThenBy(m => m.Wcid).First().Wcid;

            return FraysFamilies.PickWeighted(pool, ThreadSafeRandom.Next(0.0f, 1.0f));
        }

        /// <summary>
        /// The Great Snarl's creature (of the Snarl's family, but not the same creature):
        /// the family's highest-level member other than the Snarl's, so the two never look alike. The Snarl's own when the
        /// family has nothing else.
        /// </summary>
        public uint GreatSnarlWcid()
        {
            var snarl = WcidFor(FraysRole.Snarl);

            if (Table == null)
                return Elite != 0 && Elite != snarl ? Elite : snarl;

            var other = Trash.Concat(Elites).Where(m => m.Wcid != snarl).OrderByDescending(m => m.Level).ThenBy(m => m.Wcid).FirstOrDefault();
            return other?.Wcid ?? snarl;
        }

        /// <summary>True when <paramref name="wcid"/> is one of this family's creatures.</summary>
        public bool Contains(uint wcid) => Table == null
            ? wcid == Swarm || wcid == Standard || wcid == Elite || wcid == Snarl
            : Trash.Any(m => m.Wcid == wcid) || Elites.Any(m => m.Wcid == wcid);

        public string Line => Table == null
            ? $"{Name}: resist {Resists}; weak to {WeakTo}; deal {Deals}. Bring a {Name.TrimEnd('s')} slayer."
            : $"{Name}: {Melee} melee, {Casters} caster and {Archers} archer kinds (melee come up most). Bring a {Type} slayer.";
    }

    /// <summary>
    /// The weekly creature families. Each week every Fray runs the same one family.
    ///
    /// The families are Threads' own: every World Events species table (Content/events/axes/species) that can field a
    /// creature. Members are retail creatures, lifted to the camper's level and Heat on Threads' curves like any Frays
    /// creature. They are weighted by how they fight (WorldEventRangedMembers): a melee creature that runs at you counts
    /// fully, a caster counts family_caster_weight and an archer family_archer_weight, so packs are mostly chasers and a
    /// family made mostly of casters or archers comes up rarely. Creatures that stand still, summon (generators) or
    /// cannot be attacked are left out, so an unattended camp can always kill what it meets.
    ///
    /// A week's family is drawn by weight (seeded by the week, so everyone sees the same ones). With the species
    /// tables not loaded (or family_source "custom") the five original DreamWeave families run in turn instead.
    /// </summary>
    public static class FraysFamilies
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(FraysFamilies));

        /// <summary>The five original DreamWeave families, four fixed creatures each.</summary>
        public static readonly IReadOnlyList<FraysFamily> Custom = new List<FraysFamily>
        {
            new FraysFamily { Key = "olthoi", Name = "Olthoi", Type = CreatureType.Olthoi, Swarm = 2000059, Standard = 2000060, Elite = 2000061, Snarl = 2000062,
                Resists = "acid, pierce", WeakTo = "bludgeon, slash", Deals = "slash, acid" },
            new FraysFamily { Key = "golem", Name = "Golems", Type = CreatureType.Golem, Swarm = 2000063, Standard = 2000064, Elite = 2000065, Snarl = 2000066,
                Resists = "their own element, bludgeon", WeakTo = "the opposite element", Deals = "bludgeon and an element" },
            new FraysFamily { Key = "undead", Name = "Undead", Type = CreatureType.Undead, Swarm = 2000067, Standard = 2000068, Elite = 2000069, Snarl = 2000070,
                Resists = "cold, pierce", WeakTo = "fire, bludgeon", Deals = "slash, cold" },
            new FraysFamily { Key = "tusker", Name = "Tuskers", Type = CreatureType.Tusker, Swarm = 2000071, Standard = 2000072, Elite = 2000073, Snarl = 2000074,
                Resists = "slash, bludgeon", WeakTo = "pierce, fire", Deals = "bludgeon, pierce" },
            new FraysFamily { Key = "lugian", Name = "Lugians", Type = CreatureType.Lugian, Swarm = 2000088, Standard = 2000089, Elite = 2000090, Snarl = 2000091,
                Resists = "bludgeon, cold", WeakTo = "slash, fire", Deals = "bludgeon" },
        };

        private static readonly object buildLock = new object();
        private static object builtFrom;
        private static double builtCasterWeight = double.NaN, builtArcherWeight = double.NaN;
        private static IReadOnlyList<FraysFamily> threads = Array.Empty<FraysFamily>();

        /// <summary>Test seam: the species tables (null = the live World Events store).</summary>
        internal static Func<IReadOnlyDictionary<string, SpeciesTableDef>> TablesSource = LiveTables;

        /// <summary>Test seam: what a member is (null = left out), from its weenie.</summary>
        internal static Func<uint, FraysFightKind?> KindOf = LiveKindOf;

        private static IReadOnlyDictionary<string, SpeciesTableDef> LiveTables()
        {
            try
            {
                return WorldEventManager.Store?.SpeciesTables;
            }
            catch
            {
                return null;
            }
        }

        private sealed record ExcludeCache(IReadOnlyList<FraysFamily> From, string Text, IReadOnlyList<FraysFamily> Result);
        private static ExcludeCache excludeCache;

        /// <summary>
        /// The families without those family_exclude names (key or creature type, any case), cached until the list or the
        /// setting changes. Never empty: excluding every family leaves the list as it was.
        /// </summary>
        public static IReadOnlyList<FraysFamily> Excluding(IReadOnlyList<FraysFamily> families, string exclude)
        {
            if (families == null || string.IsNullOrWhiteSpace(exclude))
                return families;

            var cache = excludeCache;

            if (cache != null && ReferenceEquals(cache.From, families) && cache.Text == exclude)
                return cache.Result;

            var names = new HashSet<string>(exclude.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
            var kept = families.Where(f => !names.Contains(f.Key ?? "") && !(f.Table != null && names.Contains(f.Type.ToString()))).ToList();
            var result = kept.Count > 0 ? kept : families;

            excludeCache = new ExcludeCache(families, exclude, result);
            return result;
        }

        /// <summary>Every family the week draw runs over, excluded ones too: Threads' families when loaded, else the five original ones.</summary>
        private static IReadOnlyList<FraysFamily> Pool
        {
            get
            {
                if (!FraysSettings.Text(FraysSettings.FamilySource).Equals("custom", StringComparison.OrdinalIgnoreCase))
                {
                    var t = ThreadsFamilies();

                    if (t.Count > 1)
                        return t;
                }

                return Custom;
            }
        }

        /// <summary>Every family in the rotation, without those family_exclude names.</summary>
        public static IReadOnlyList<FraysFamily> All => Excluding(Pool, FraysSettings.Text(FraysSettings.FamilyExclude));

        /// <summary>
        /// Builds the Threads families on a background thread at startup: built on first use instead, the first fire lit after a restart
        /// would load 748 creature weenies inside its landblock's tick and freeze the plateau for about 8 s. Never throws.
        /// </summary>
        public static System.Threading.Tasks.Task WarmUp()
        {
            if (!FraysSettings.Toggle(FraysSettings.FamiliesEnabled))
                return null;

            return System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    ThreadsFamilies();
                }
                catch (Exception ex)
                {
                    log.Warn("[FRAYS] family warm-up failed; the first fire lit will build them instead", ex);
                }
            });
        }

        /// <summary>The Threads families, built once per loaded set of species tables (and again when a weight setting changes).</summary>
        public static IReadOnlyList<FraysFamily> ThreadsFamilies()
        {
            var tables = TablesSource?.Invoke();

            if (tables == null || tables.Count == 0)
                return Array.Empty<FraysFamily>();

            var casterWeight = FraysSettings.Number(FraysSettings.FamilyCasterWeight);
            var archerWeight = FraysSettings.Number(FraysSettings.FamilyArcherWeight);

            lock (buildLock)
            {
                if (ReferenceEquals(builtFrom, tables) && builtCasterWeight == casterWeight && builtArcherWeight == archerWeight)
                    return threads;

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var list = new List<FraysFamily>();

                foreach (var table in tables.Values.Where(t => t != null).OrderBy(t => t.Id, StringComparer.Ordinal))
                {
                    var family = Build(table, casterWeight, archerWeight);

                    if (family != null)
                        list.Add(family);
                }

                threads = list;
                builtFrom = tables;
                builtCasterWeight = casterWeight;
                builtArcherWeight = archerWeight;

                if (TablesSource == LiveTables)
                {
                    log.Info($"[FRAYS] {list.Count} Threads families ready for the Frays ({list.Sum(f => f.Trash.Count + f.Elites.Count)} creatures) in {watch.ElapsedMilliseconds} ms");
                    var exclude = new HashSet<string>(FraysSettings.Text(FraysSettings.FamilyExclude).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
                    var excludedNow = list.Where(x => exclude.Contains(x.Key ?? "") || exclude.Contains(x.Type.ToString())).Select(x => x.Key).ToList();
                    log.Info($"[FRAYS] families left out (family_exclude): {(excludedNow.Count > 0 ? string.Join(", ", excludedNow) : "none")}; families whose packs all cast, fighting in melee with empty spell books below caster_min_heat: {string.Join(", ", list.Where(x => x.CasterOnlyPacks && !excludedNow.Contains(x.Key)).Select(x => x.Key))}");
                }

                return threads;
            }
        }

        /// <summary>One Threads family, or null when it can field nothing.</summary>
        internal static FraysFamily Build(SpeciesTableDef table, double casterWeight, double archerWeight)
        {
            var trash = new List<FraysFamilyMember>();
            var elites = new List<FraysFamilyMember>();
            int melee = 0, casters = 0, archers = 0;

            foreach (var m in table.Members.Where(m => m != null && m.Wcid != 0 && m.Role >= 0 && m.Role <= 2).GroupBy(m => m.Wcid).Select(g => g.First()))
            {
                var kind = KindOf(m.Wcid);

                if (kind == null)
                    continue;

                var weight = kind == FraysFightKind.Melee ? 1.0 : kind == FraysFightKind.Caster ? casterWeight : archerWeight;

                if (kind == FraysFightKind.Melee) melee++;
                else if (kind == FraysFightKind.Caster) casters++;
                else archers++;

                if (!(weight > 0))
                    continue;

                var member = new FraysFamilyMember { Wcid = m.Wcid, Name = m.Name, Level = m.Level, Kind = kind.Value, Weight = weight };

                if (m.Role == 0)
                    trash.Add(member);
                else
                    elites.Add(member);
            }

            // The family's upper half by level: every creature is lifted to the camper anyway, and the upper half keeps
            // the look (and authored attacks) of a creature made for that kind of fight.
            if (trash.Count > 2)
            {
                var top = trash.Max(m => m.Level);
                trash = trash.Where(m => m.Level >= top / 2).ToList();
            }

            if (trash.Count == 0)
            {
                trash = elites.ToList();

                if (trash.Count == 0)
                    return null;
            }

            var total = melee + casters + archers;
            var familyWeight = total == 0 ? 0 : (melee + casters * casterWeight + archers * archerWeight) / total;

            if (!(familyWeight > 0))
                return null;

            Enum.TryParse<CreatureType>(table.CreatureType ?? "", true, out var type);

            return new FraysFamily
            {
                Key = table.Id,
                Name = DisplayName(table),
                Type = type,
                Table = table,
                Trash = trash,
                Elites = elites,
                Weight = familyWeight,
                Melee = melee,
                Casters = casters,
                Archers = archers,
            };
        }

        private static string DisplayName(SpeciesTableDef table)
        {
            var name = (table.DisplayName ?? table.Id ?? "").Trim();

            if (name.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(4);

            return name.Length == 0 ? table.Id : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// What a creature is, from its weenie: melee, caster or archer (WorldEventRangedMembers' rules), or null when an
        /// unattended camp could not deal with it: not a creature, cannot be attacked, never moves, or summons (generators).
        /// </summary>
        private static FraysFightKind? LiveKindOf(uint wcid)
        {
            try
            {
                var w = DatabaseManager.World.GetCachedWeenie(wcid);

                if (w == null || w.WeenieType != WeenieType.Creature)
                    return null;

                if (w.GetProperty(PropertyBool.AiImmobile) == true || w.GetProperty(PropertyBool.Attackable) == false)
                    return null;

                if (w.PropertiesGenerator != null && w.PropertiesGenerator.Count > 0)
                    return null;

                if (!WorldEventRangedMembers.IsRanged(new FamilyMember { Wcid = wcid }))
                    return FraysFightKind.Melee;

                return WorldEventRangedMembers.IsCasterWcid(wcid) ? FraysFightKind.Caster : FraysFightKind.Archer;
            }
            catch (Exception ex)
            {
                log.Warn($"[FRAYS] could not read weenie {wcid} for the Frays families; left out", ex);
                return null;
            }
        }

        /// <summary>A member by weight, with <paramref name="roll"/> in [0, 1).</summary>
        public static uint PickWeighted(IReadOnlyList<FraysFamilyMember> pool, double roll)
        {
            var total = pool.Sum(m => m.Weight);
            var at = roll * total;

            foreach (var m in pool)
            {
                at -= m.Weight;

                if (at < 0)
                    return m.Wcid;
            }

            return pool[^1].Wcid;
        }

        /// <summary>The Frays in order (index 0, 1): their landblocks. A landblock not listed runs its own fixed mix.</summary>
        public static IReadOnlyList<ushort> Frays => FraysSettings.Text(FraysSettings.FrayOrder)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => ushort.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var lb) ? lb : (ushort)0)
            .Where(lb => lb != 0).ToList();

        /// <summary>
        /// A week's family for Fray <paramref name="frayIndex"/>. The original families run in turn; Threads families are
        /// drawn by weight without repeats, from a random seeded by the week.
        /// </summary>
        public static FraysFamily ForWeek(int week, int frayIndex)
        {
            var kept = All;

            if (kept[0].Table == null)
                return kept[(((week + frayIndex) % kept.Count) + kept.Count) % kept.Count];

            // The draw runs over every family, excluded ones too, and draws again when it lands on one: so leaving a family
            // out changes only its own weeks, never another week's family.
            var all = Pool;
            var n = all.Count;
            var keep = new HashSet<FraysFamily>(kept);

            var rng = new Random(unchecked(week * 7919 + 104729));
            var left = all.ToList();
            FraysFamily pick = null;

            for (var i = 0; i <= frayIndex % n && left.Count > 0; i++)
            {
                var total = left.Sum(f => f.Weight);
                var at = rng.NextDouble() * total;
                pick = left[^1];

                foreach (var f in left)
                {
                    at -= f.Weight;

                    if (at < 0)
                    {
                        pick = f;
                        break;
                    }
                }

                left.Remove(pick);

                if (!keep.Contains(pick))
                    i--;   // an excluded family: draw again
            }

            return pick != null && keep.Contains(pick) ? pick : kept[0];
        }

        /// <summary>This week's family at a Fray's landblock, or null when families are off or the landblock is no Fray.</summary>
        public static FraysFamily For(ushort landblock)
        {
            if (!FraysSettings.Toggle(FraysSettings.FamiliesEnabled))
                return null;

            var pinned = FraysSettings.Text(FraysSettings.FamilyPin);

            if (pinned.Length > 0 && All.FirstOrDefault(f => f.Key.Equals(pinned, StringComparison.OrdinalIgnoreCase)) is FraysFamily pin)
                return pin;

            // One family a week for every Fray (not one per Fray).
            return Frays.Contains(landblock) ? ForWeek(FraysWeek.Current, 0) : null;
        }

        /// <summary>
        /// The Vissidal island's Frays (the Western Shore, the Eastern Heights and the Sky Fray plateau) share one family each
        /// week.
        /// </summary>
        private static readonly ushort[] Vissidal = { FraysDefs.VissidalCoast, FraysDefs.VissidalHeights, FraysDefs.VissidalSky };

        /// <summary>The landblock whose place in the rotation a Fray takes (a Vissidal Fray: the coast's).</summary>
        public static ushort GroupOf(ushort landblock) => Vissidal.Contains(landblock) ? FraysDefs.VissidalCoast : landblock;

        /// <summary>The family a creature belongs to, or null.</summary>
        public static FraysFamily Of(uint wcid) => All.FirstOrDefault(f => f.Contains(wcid));

        /// <summary>The share of a standard creature's health (and so its Heat, Ember and loot worth) for a role.</summary>
        public static double HealthShare(FraysRole role) => role switch
        {
            FraysRole.Swarm => FraysSettings.Number(FraysSettings.SwarmHealthShare),
            FraysRole.Elite => FraysRules.EliteHealth,
            _ => 1,
        };

        public static IEnumerable<string> WeekLines()
        {
            var f = Frays.Count > 0 ? For(Frays[0]) : null;

            if (f != null)
                yield return $"Every Fray this week: {f.Line}";
        }
    }
}
